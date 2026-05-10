#include "exclr8cef_osr.h"

#include <map>
#include <string>

#include "include/cef_browser.h"
#include "include/cef_cookie.h"
#include "include/cef_frame.h"
#include "include/cef_process_message.h"

namespace exclr8cef {

namespace {

int g_next_id = 1;
std::map<int, CefRefPtr<Exclr8CefOsrHandler>> g_osr_browsers;

excef_address_change_cb_t g_address_cb = nullptr;
excef_title_change_cb_t g_title_cb = nullptr;
excef_loading_state_cb_t g_loading_state_cb = nullptr;
excef_eval_result_cb_t g_eval_result_cb = nullptr;
excef_cookie_visit_cb_t g_cookie_visit_cb = nullptr;
excef_browser_closed_cb_t g_browser_closed_cb = nullptr;

}  // namespace

Exclr8CefOsrHandler::Exclr8CefOsrHandler(int id, int width, int height,
                                          excef_paint_callback_t paint_cb)
    : id_(id), width_(width), height_(height), paint_cb_(paint_cb) {}

bool Exclr8CefOsrHandler::OnProcessMessageReceived(
    CefRefPtr<CefBrowser> /*browser*/,
    CefRefPtr<CefFrame> /*frame*/,
    CefProcessId /*source_process*/,
    CefRefPtr<CefProcessMessage> message) {
    // Browser-process side: receive "EvalResult" responses from the renderer.
    if (message->GetName() != "EvalResult") return false;

    auto args = message->GetArgumentList();
    int request_id = args->GetInt(0);
    bool ok = args->GetBool(1);
    std::string payload = args->GetString(2).ToString();

    if (g_eval_result_cb) {
        g_eval_result_cb(id_, request_id, ok ? 1 : 0, payload.c_str());
    }
    return true;
}

void Exclr8CefOsrHandler::GetViewRect(CefRefPtr<CefBrowser> /*browser*/,
                                      CefRect& rect) {
    rect.x = 0;
    rect.y = 0;
    rect.width = width_;
    rect.height = height_;
}

void Exclr8CefOsrHandler::OnPaint(CefRefPtr<CefBrowser> /*browser*/,
                                  PaintElementType type,
                                  const RectList& /*dirtyRects*/,
                                  const void* buffer,
                                  int width, int height) {
    if (type != PET_VIEW) return;
    if (paint_cb_) paint_cb_(id_, buffer, width, height);
}

void Exclr8CefOsrHandler::OnAfterCreated(CefRefPtr<CefBrowser> browser) {
    browser_ = browser;
    browser_->GetHost()->WasResized();
}

void Exclr8CefOsrHandler::OnBeforeClose(CefRefPtr<CefBrowser> /*browser*/) {
    int closed_id = id_;
    browser_ = nullptr;
    g_osr_browsers.erase(id_);
    if (g_browser_closed_cb) {
        g_browser_closed_cb(closed_id);
    }
}

void Exclr8CefOsrHandler::OnAddressChange(CefRefPtr<CefBrowser> /*browser*/,
                                           CefRefPtr<CefFrame> frame,
                                           const CefString& url) {
    if (!frame->IsMain()) return;
    if (g_address_cb) {
        std::string s = url.ToString();
        g_address_cb(id_, s.c_str());
    }
}

void Exclr8CefOsrHandler::OnTitleChange(CefRefPtr<CefBrowser> /*browser*/,
                                         const CefString& title) {
    if (g_title_cb) {
        std::string s = title.ToString();
        g_title_cb(id_, s.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadingStateChange(CefRefPtr<CefBrowser> /*browser*/,
                                                bool isLoading,
                                                bool canGoBack,
                                                bool canGoForward) {
    if (g_loading_state_cb) {
        g_loading_state_cb(id_,
                           isLoading ? 1 : 0,
                           canGoBack ? 1 : 0,
                           canGoForward ? 1 : 0);
    }
}

void Exclr8CefOsrHandler::SetSize(int width, int height) {
    width_ = width;
    height_ = height;
    if (browser_) browser_->GetHost()->WasResized();
}

}  // namespace exclr8cef

// ---- C ABI implementations ------------------------------------------------

extern "C" int excef_create_offscreen_browser(int width, int height,
                                              const char* url,
                                              excef_paint_callback_t paint) {
    if (!url || width <= 0 || height <= 0) return 0;

    int id = exclr8cef::g_next_id++;
    auto handler = CefRefPtr<exclr8cef::Exclr8CefOsrHandler>(
        new exclr8cef::Exclr8CefOsrHandler(id, width, height, paint));
    exclr8cef::g_osr_browsers[id] = handler;

    CefWindowInfo window_info;
    window_info.SetAsWindowless((CefWindowHandle)0);
    window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

    CefBrowserSettings browser_settings;
    browser_settings.windowless_frame_rate = 30;

    if (!CefBrowserHost::CreateBrowser(window_info, handler.get(), url,
                                       browser_settings, nullptr, nullptr)) {
        exclr8cef::g_osr_browsers.erase(id);
        return 0;
    }
    return id;
}

extern "C" void excef_resize_offscreen_browser(int browser_id,
                                               int width, int height) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return;
    it->second->SetSize(width, height);
}

namespace {

CefRefPtr<CefBrowser> get_browser(int browser_id) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return nullptr;
    return it->second->browser();
}

}  // namespace

// ---- Input ----------------------------------------------------------------

extern "C" void excef_send_mouse_move(int browser_id, int x, int y,
                                      int modifiers, int mouse_leave) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->SendMouseMoveEvent(ev, mouse_leave != 0);
}

extern "C" void excef_send_mouse_click(int browser_id, int x, int y,
                                       int button, int mouse_up,
                                       int click_count, int modifiers) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    auto type = static_cast<cef_mouse_button_type_t>(button);
    b->GetHost()->SendMouseClickEvent(ev, type, mouse_up != 0, click_count);
}

extern "C" void excef_send_mouse_wheel(int browser_id, int x, int y,
                                       int delta_x, int delta_y,
                                       int modifiers) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    b->GetHost()->SendMouseWheelEvent(ev, delta_x, delta_y);
}

extern "C" void excef_send_key_event(int browser_id, int type,
                                     int windows_key_code, int native_key_code,
                                     int modifiers, int character,
                                     int unmodified_character,
                                     int is_system_key) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefKeyEvent ev;
    ev.type = static_cast<cef_key_event_type_t>(type);
    ev.modifiers = static_cast<uint32_t>(modifiers);
    ev.windows_key_code = windows_key_code;
    ev.native_key_code = native_key_code;
    ev.is_system_key = is_system_key != 0;
    ev.character = static_cast<char16_t>(character);
    ev.unmodified_character = static_cast<char16_t>(unmodified_character);
    ev.focus_on_editable_field = false;
    b->GetHost()->SendKeyEvent(ev);
}

extern "C" void excef_set_browser_focus(int browser_id, int focus) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->SetFocus(focus != 0);
}

// ---- Navigation -----------------------------------------------------------

extern "C" void excef_load_url(int browser_id, const char* url) {
    auto b = get_browser(browser_id);
    if (!b || !url) return;
    b->GetMainFrame()->LoadURL(url);
}
extern "C" void excef_go_back(int browser_id) { auto b = get_browser(browser_id); if (b) b->GoBack(); }
extern "C" void excef_go_forward(int browser_id) { auto b = get_browser(browser_id); if (b) b->GoForward(); }
extern "C" void excef_reload(int browser_id, int ignore_cache) {
    auto b = get_browser(browser_id);
    if (!b) return;
    if (ignore_cache) b->ReloadIgnoreCache();
    else b->Reload();
}
extern "C" void excef_stop_load(int browser_id) { auto b = get_browser(browser_id); if (b) b->StopLoad(); }
extern "C" void excef_close_browser(int browser_id, int force_close) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->CloseBrowser(force_close != 0);
}
extern "C" void excef_was_hidden(int browser_id, int hidden) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->WasHidden(hidden != 0);
}

// ---- JavaScript -----------------------------------------------------------

extern "C" void excef_execute_javascript(int browser_id, const char* code,
                                          const char* script_url) {
    auto b = get_browser(browser_id);
    if (!b || !code) return;
    b->GetMainFrame()->ExecuteJavaScript(code,
                                          script_url ? script_url : "", 0);
}

extern "C" void excef_set_eval_result_callback(excef_eval_result_cb_t cb) {
    exclr8cef::g_eval_result_cb = cb;
}

extern "C" int excef_eval_javascript(int browser_id, int request_id,
                                     const char* code) {
    auto b = get_browser(browser_id);
    if (!b || !code) return 0;
    auto msg = CefProcessMessage::Create("Eval");
    auto args = msg->GetArgumentList();
    args->SetInt(0, request_id);
    args->SetString(1, code);
    b->GetMainFrame()->SendProcessMessage(PID_RENDERER, msg);
    return 1;
}

// ---- DevTools -------------------------------------------------------------

extern "C" void excef_show_dev_tools(int browser_id) {
    auto b = get_browser(browser_id);
    if (!b) return;
    CefWindowInfo window_info;
    CefBrowserSettings settings;
    b->GetHost()->ShowDevTools(window_info, nullptr, settings, CefPoint());
}

extern "C" void excef_close_dev_tools(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->CloseDevTools();
}

// ---- Browser events -------------------------------------------------------

extern "C" void excef_set_address_change_callback(excef_address_change_cb_t cb) { exclr8cef::g_address_cb = cb; }
extern "C" void excef_set_title_change_callback(excef_title_change_cb_t cb) { exclr8cef::g_title_cb = cb; }
extern "C" void excef_set_loading_state_callback(excef_loading_state_cb_t cb) { exclr8cef::g_loading_state_cb = cb; }
extern "C" void excef_set_browser_closed_callback(excef_browser_closed_cb_t cb) { exclr8cef::g_browser_closed_cb = cb; }

// ---- Cookies --------------------------------------------------------------

namespace {

class CookieVisitorImpl : public CefCookieVisitor {
public:
    CookieVisitorImpl(int request_id) : request_id_(request_id) {}

    bool Visit(const CefCookie& cookie, int count, int total,
               bool& /*deleteCookie*/) override {
        if (exclr8cef::g_cookie_visit_cb) {
            std::string name = CefString(&cookie.name).ToString();
            std::string value = CefString(&cookie.value).ToString();
            std::string domain = CefString(&cookie.domain).ToString();
            std::string path = CefString(&cookie.path).ToString();
            exclr8cef::g_cookie_visit_cb(
                request_id_, /*done=*/0,
                name.c_str(), value.c_str(),
                domain.c_str(), path.c_str(),
                cookie.secure ? 1 : 0,
                cookie.httponly ? 1 : 0);
            if (count == total - 1) {
                exclr8cef::g_cookie_visit_cb(
                    request_id_, /*done=*/1,
                    "", "", "", "", 0, 0);
            }
        }
        return true;
    }

    ~CookieVisitorImpl() override {
        // Total may have been 0 — fire the done call so the host's TCS can complete.
        if (!fired_done_ && exclr8cef::g_cookie_visit_cb) {
            exclr8cef::g_cookie_visit_cb(
                request_id_, /*done=*/1,
                "", "", "", "", 0, 0);
        }
    }

private:
    int request_id_;
    bool fired_done_ = false;
    IMPLEMENT_REFCOUNTING(CookieVisitorImpl);
};

}  // namespace

extern "C" void excef_set_cookie_visit_callback(excef_cookie_visit_cb_t cb) {
    exclr8cef::g_cookie_visit_cb = cb;
}

extern "C" int excef_get_cookies(const char* url, int request_id) {
    auto mgr = CefCookieManager::GetGlobalManager(nullptr);
    if (!mgr) return 0;
    CefRefPtr<CookieVisitorImpl> visitor(new CookieVisitorImpl(request_id));
    if (url && *url) {
        mgr->VisitUrlCookies(url, /*include_http_only=*/true, visitor);
    } else {
        mgr->VisitAllCookies(visitor);
    }
    return request_id;
}

extern "C" int excef_set_cookie(const char* url,
                                const char* name,
                                const char* value,
                                const char* domain,
                                const char* path,
                                int secure,
                                int httponly) {
    if (!url || !name) return 0;
    auto mgr = CefCookieManager::GetGlobalManager(nullptr);
    if (!mgr) return 0;
    CefCookie c;
    CefString(&c.name).FromASCII(name);
    if (value) CefString(&c.value).FromASCII(value);
    if (domain) CefString(&c.domain).FromASCII(domain);
    if (path) CefString(&c.path).FromASCII(path);
    c.secure = secure != 0;
    c.httponly = httponly != 0;
    return mgr->SetCookie(url, c, nullptr) ? 1 : 0;
}

extern "C" void excef_delete_cookies(const char* url, const char* name) {
    auto mgr = CefCookieManager::GetGlobalManager(nullptr);
    if (!mgr) return;
    mgr->DeleteCookies(url ? url : "", name ? name : "", nullptr);
}

// ---- IME ------------------------------------------------------------------

extern "C" void excef_ime_set_composition(int browser_id,
                                           const char* text,
                                           int replacement_range_from,
                                           int replacement_range_length,
                                           int selection_range_from,
                                           int selection_range_length) {
    auto b = get_browser(browser_id);
    if (!b || !text) return;
    std::vector<CefCompositionUnderline> underlines;
    CefRange replacement(replacement_range_from,
                         replacement_range_from + replacement_range_length);
    CefRange selection(selection_range_from,
                       selection_range_from + selection_range_length);
    b->GetHost()->ImeSetComposition(text, underlines, replacement, selection);
}

extern "C" void excef_ime_commit_text(int browser_id,
                                      const char* text,
                                      int replacement_range_from,
                                      int replacement_range_length,
                                      int relative_cursor_pos) {
    auto b = get_browser(browser_id);
    if (!b || !text) return;
    CefRange replacement(replacement_range_from,
                         replacement_range_from + replacement_range_length);
    b->GetHost()->ImeCommitText(text, replacement, relative_cursor_pos);
}

extern "C" void excef_ime_finish_composing(int browser_id, int keep_selection) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->ImeFinishComposingText(keep_selection != 0);
}

extern "C" void excef_ime_cancel(int browser_id) {
    auto b = get_browser(browser_id);
    if (b) b->GetHost()->ImeCancelComposition();
}

// ---- PrintToPDF -----------------------------------------------------------

namespace {

class PdfCallback : public CefPdfPrintCallback {
public:
    PdfCallback(int browser_id, excef_pdf_done_callback_t cb)
        : browser_id_(browser_id), cb_(cb) {}

    void OnPdfPrintFinished(const CefString& /*path*/, bool ok) override {
        if (cb_) cb_(browser_id_, ok ? 1 : 0);
    }

private:
    int browser_id_;
    excef_pdf_done_callback_t cb_;
    IMPLEMENT_REFCOUNTING(PdfCallback);
};

}  // namespace

extern "C" int excef_print_to_pdf(int browser_id, const char* path,
                                  excef_pdf_done_callback_t callback) {
    if (!path) return 0;
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return 0;
    auto browser = it->second->browser();
    if (!browser) return 0;
    CefPdfPrintSettings settings;
    browser->GetHost()->PrintToPDF(path, settings,
                                   new PdfCallback(browser_id, callback));
    return 1;
}
