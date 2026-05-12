// Windows-specific shim implementation for embedded (windowed) browsers.
//
// Mirrors exclr8cef_mac.mm but with Win32 HWNDs instead of NSViews. The
// lifecycle entry points (excef_initialize / excef_execute_process /
// excef_run_message_loop / excef_shutdown / pump variants) for Windows
// already live in exclr8cef.cc under `#if !defined(__APPLE__)` — that
// path is shared with Linux. The pieces that ARE platform-specific here:
//
//   - excef_create_embedded_host: child-window factory, returns HWND
//   - excef_attach_embedded_browser: parents CEF to that HWND
//   - excef_create_browser_view: one-shot create-and-attach
//   - excef_resize_browser_view: SetWindowPos + WasResized
//
// All four functions are also declared in exclr8cef.h with EXCEF_API so
// the managed P/Invoke entries in Generated/Excef.cs resolve cleanly on
// a Windows DLL build. Without this file the Windows DLL would be
// missing those exports and managed embedded-mode calls would throw
// EntryPointNotFoundException.

#if defined(_WIN32)

#include <windows.h>

#include <map>
#include <mutex>
#include <string>

#include "include/cef_app.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"

#include "exclr8cef.h"
#include "exclr8cef_app.h"
#include "exclr8cef_osr.h"

namespace {

// Window class used for the host child window. Registered lazily on
// first call into excef_create_embedded_host so the DLL can be loaded
// without paying any GUI setup cost up front.
constexpr wchar_t kHostClassName[] = L"Exclr8CefHostWindow";

LRESULT CALLBACK HostWndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    // Default everything. CEF parents its browser HWND as a child of this
    // window and manages its own messages; we only need to be a valid
    // parent. WM_ERASEBKGND is short-circuited so the host doesn't paint
    // flashes between resizes and CEF's first paint.
    switch (msg) {
        case WM_ERASEBKGND:
            return 1;
        default:
            return DefWindowProcW(hwnd, msg, wp, lp);
    }
}

void EnsureHostClassRegistered() {
    static std::once_flag once;
    std::call_once(once, []() {
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_HREDRAW | CS_VREDRAW;
        wc.lpfnWndProc = HostWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)(COLOR_WINDOW + 1);
        wc.lpszClassName = kHostClassName;
        RegisterClassExW(&wc);
        // Ignore failure — a second register attempt under a duplicate
        // DLL load returns ERROR_CLASS_ALREADY_EXISTS, which is fine.
    });
}

// Map host HWND → browser id, so excef_resize_browser_view can find the
// backing browser to call WasResized() on. Same purpose as the Mac side's
// g_host_to_id, plus a pending-size cache for the resize-before-creation
// race (Avalonia's ArrangeOverride can fire before CEF finishes async
// browser creation; OnAfterCreated picks up the latest pending size).
std::mutex g_host_map_mu;
std::map<HWND, int> g_host_to_id;
struct PendingSize { int width; int height; };
std::map<HWND, PendingSize> g_pending_sizes;

// Subclass of the OSR handler used for embedded (windowed) browsers.
// Same handler surface (load / console / drag / permission / …) — only
// adds an OnAfterCreated hook that syncs the host HWND's child to the
// latest pending size and calls WasResized so Chromium lays out the
// page viewport correctly on first paint.
class EmbeddedOsrHandler : public exclr8cef::Exclr8CefOsrHandler {
public:
    EmbeddedOsrHandler(int id, int w, int h, HWND host_hwnd)
        : Exclr8CefOsrHandler(id, w, h, 1.0f, /*paint_cb=*/nullptr),
          host_(host_hwnd) {}

    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        Exclr8CefOsrHandler::OnAfterCreated(browser);
        if (!host_) return;
        // Pick the desired size — prefer a queued resize if Avalonia
        // already pushed one before browser creation completed.
        int w, h;
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            auto it = g_pending_sizes.find(host_);
            if (it != g_pending_sizes.end()) {
                w = it->second.width;
                h = it->second.height;
            } else {
                RECT r;
                GetClientRect(host_, &r);
                w = r.right - r.left;
                h = r.bottom - r.top;
            }
        }
        // Resize the CEF child HWND (it's the only child of the host).
        if (HWND child = GetWindow(host_, GW_CHILD)) {
            SetWindowPos(child, nullptr, 0, 0, w, h,
                         SWP_NOZORDER | SWP_NOACTIVATE);
        }
        browser->GetHost()->WasResized();
    }

    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override {
        if (host_) {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase(host_);
            g_pending_sizes.erase(host_);
        }
        Exclr8CefOsrHandler::OnBeforeClose(browser);
    }

private:
    HWND host_;
    IMPLEMENT_REFCOUNTING(EmbeddedOsrHandler);
};

HWND CreateHostWindow(int width, int height) {
    EnsureHostClassRegistered();
    // WS_CHILD + WS_CLIPCHILDREN: this HWND is meant to live inside the
    // host app's window. WS_CLIPCHILDREN prevents the host from painting
    // over CEF's child HWND during its own paint cycle. No parent is set
    // here — Avalonia's NativeControlHost re-parents the HWND once we
    // return it. CEF's child browser HWND will be created underneath.
    return CreateWindowExW(
        /*dwExStyle=*/0,
        kHostClassName,
        L"",
        WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
        /*x=*/0, /*y=*/0,
        width > 0 ? width : 1,
        height > 0 ? height : 1,
        /*hWndParent=*/nullptr,   // re-parented by host UI framework
        /*hMenu=*/nullptr,
        GetModuleHandleW(nullptr),
        /*lpParam=*/nullptr);
}

}  // namespace

// Phase 1: create an empty host HWND that the UI framework will parent.
extern "C" void* excef_create_embedded_host(int width, int height) {
    HWND h = CreateHostWindow(width, height);
    return reinterpret_cast<void*>(h);
}

// Phase 2: attach a CEF browser to a previously-created host HWND. Call
// this AFTER the UI framework has parented the HWND so Chromium reads
// the correct effective DPI at browser-creation time.
extern "C" int excef_attach_embedded_browser_in_context(void* host_view_ptr,
                                                          int width, int height,
                                                          const char* url,
                                                          int context_handle) {
    if (!host_view_ptr || !url) return 0;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);

    CefWindowInfo window_info;
    RECT host_rect;
    GetClientRect(host, &host_rect);
    int effective_w = width  > 0 ? width  : host_rect.right  - host_rect.left;
    int effective_h = height > 0 ? height : host_rect.bottom - host_rect.top;
    window_info.SetAsChild(host, CefRect(0, 0, effective_w, effective_h));
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    int id = exclr8cef::AllocateBrowserId();
    CefRefPtr<EmbeddedOsrHandler> handler(
        new EmbeddedOsrHandler(id, effective_w, effective_h, host));
    exclr8cef::RegisterOsrHandler(id, handler);
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id[host] = id;
    }

    CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
    CefRefPtr<CefRequestContext> pass = context_handle == 0 ? nullptr : ctx;

    CefBrowserSettings browser_settings;
    bool ok = CefBrowserHost::CreateBrowser(
        window_info, handler.get(), url, browser_settings,
        /*extra_info=*/nullptr, pass);

    if (!ok) {
        exclr8cef::UnregisterOsrHandler(id);
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id.erase(host);
        return 0;
    }
    return id;
}

extern "C" int excef_attach_embedded_browser(void* host_view_ptr,
                                              int width, int height,
                                              const char* url) {
    return excef_attach_embedded_browser_in_context(host_view_ptr, width, height, url, 0);
}

// Combined create + attach. Returns the host HWND as void* (with the
// browser id written to out_browser_id) so a single managed call can
// produce a renderable handle.
extern "C" void* excef_create_browser_view_in_context(int width, int height,
                                                       const char* url,
                                                       int* out_browser_id,
                                                       int context_handle) {
    if (out_browser_id) *out_browser_id = 0;
    if (!url) return nullptr;

    HWND host = CreateHostWindow(width, height);
    if (!host) return nullptr;

    CefWindowInfo window_info;
    window_info.SetAsChild(host, CefRect(0, 0, width, height));
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    int id = exclr8cef::AllocateBrowserId();
    CefRefPtr<EmbeddedOsrHandler> handler(
        new EmbeddedOsrHandler(id, width, height, host));
    exclr8cef::RegisterOsrHandler(id, handler);
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_host_to_id[host] = id;
    }

    CefRefPtr<CefRequestContext> ctx = exclr8cef::ResolveContext(context_handle);
    CefRefPtr<CefRequestContext> pass = context_handle == 0 ? nullptr : ctx;

    CefBrowserSettings browser_settings;
    bool ok = CefBrowserHost::CreateBrowser(
        window_info, handler.get(), url, browser_settings,
        /*extra_info=*/nullptr, pass);

    if (!ok) {
        exclr8cef::UnregisterOsrHandler(id);
        {
            std::lock_guard<std::mutex> lock(g_host_map_mu);
            g_host_to_id.erase(host);
        }
        DestroyWindow(host);
        return nullptr;
    }

    if (out_browser_id) *out_browser_id = id;
    return reinterpret_cast<void*>(host);
}

extern "C" void* excef_create_browser_view(int width, int height,
                                           const char* url,
                                           int* out_browser_id) {
    return excef_create_browser_view_in_context(width, height, url, out_browser_id, 0);
}

// Resize the host HWND, its child CEF window, and notify Chromium so
// the page viewport relays out. Without WasResized() the page keeps
// its original viewport size and overflows when the host grows.
// Hide/show the host HWND in place. Mirrors the mac impl so callers
// (e.g. VibeCoder's tab-switch path) can hide an embedded browser
// without tearing it down. See exclr8cef_mac.mm for rationale.
extern "C" void excef_set_embedded_host_hidden(void* host_view_ptr, int hidden) {
    if (!host_view_ptr) return;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);
    ShowWindow(host, hidden ? SW_HIDE : SW_SHOWNOACTIVATE);
    int browser_id = 0;
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        auto it = g_host_to_id.find(host);
        if (it != g_host_to_id.end()) browser_id = it->second;
    }
    if (browser_id != 0) {
        auto* handler = exclr8cef::LookupOsrHandler(browser_id);
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->WasHidden(hidden != 0);
        }
    }
}

extern "C" void excef_resize_browser_view(void* host_view_ptr,
                                          int width, int height) {
    if (!host_view_ptr || width <= 0 || height <= 0) return;
    HWND host = reinterpret_cast<HWND>(host_view_ptr);

    // Stash the latest desired size so OnAfterCreated can sync to it if
    // the browser isn't ready yet (Avalonia's ArrangeOverride often fires
    // before CEF finishes async browser creation).
    int browser_id = 0;
    {
        std::lock_guard<std::mutex> lock(g_host_map_mu);
        g_pending_sizes[host] = PendingSize{width, height};
        auto it = g_host_to_id.find(host);
        if (it != g_host_to_id.end()) browser_id = it->second;
    }

    // Resize the host HWND itself only if needed (Avalonia usually sets
    // this via TryUpdateNativeControlPosition; doing it here too is a
    // no-op when sizes match and keeps the path correct when the host
    // app drives the resize via a direct excef_resize_browser_view call).
    SetWindowPos(host, nullptr, 0, 0, width, height,
                  SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOMOVE);

    if (HWND child = GetWindow(host, GW_CHILD)) {
        SetWindowPos(child, nullptr, 0, 0, width, height,
                      SWP_NOZORDER | SWP_NOACTIVATE);
    }

    if (browser_id != 0) {
        auto* handler = exclr8cef::LookupOsrHandler(browser_id);
        if (handler && handler->browser()) {
            handler->browser()->GetHost()->WasResized();
        }
    }
}

#endif  // _WIN32
