#include "exclr8cef_osr.h"

#include <atomic>
#include <map>
#include <mutex>
#include <string>

#include "include/base/cef_callback.h"
#include "include/cef_browser.h"
#include "include/cef_cookie.h"
#include "include/cef_frame.h"
#include "include/cef_process_message.h"
#include "include/cef_task.h"
#include "include/wrapper/cef_closure_task.h"

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
excef_cursor_change_cb_t g_cursor_change_cb = nullptr;
excef_console_message_cb_t g_console_message_cb = nullptr;
excef_load_start_cb_t g_load_start_cb = nullptr;
excef_load_end_cb_t g_load_end_cb = nullptr;
excef_load_error_cb_t g_load_error_cb = nullptr;
excef_loading_progress_cb_t g_loading_progress_cb = nullptr;
excef_status_message_cb_t g_status_message_cb = nullptr;
excef_tooltip_cb_t g_tooltip_cb = nullptr;
excef_favicon_cb_t g_favicon_cb = nullptr;
excef_fullscreen_cb_t g_fullscreen_cb = nullptr;
excef_browser_initialized_cb_t g_browser_initialized_cb = nullptr;
excef_scroll_offset_cb_t g_scroll_offset_cb = nullptr;
excef_auto_resize_cb_t g_auto_resize_cb = nullptr;
excef_js_dialog_cb_t g_js_dialog_cb = nullptr;
excef_file_dialog_cb_t g_file_dialog_cb = nullptr;
excef_context_menu_cb_t g_context_menu_cb = nullptr;

// Deferred-response registries (one per callback type — the CEF callback
// shape differs across handlers). The owning browser_id lets OnBeforeClose
// cancel any pending entries for the closing browser, rather than leaving
// the CEF callback object refcounted indefinitely.
struct PendingJsDialog {
    int browser_id;
    CefRefPtr<CefJSDialogCallback> callback;
};
struct PendingFileDialog {
    int browser_id;
    CefRefPtr<CefFileDialogCallback> callback;
};
struct PendingContextMenu {
    int browser_id;
    CefRefPtr<CefRunContextMenuCallback> callback;
};
std::atomic<uint64_t> g_next_token{1};
std::mutex g_js_dialog_mu;
std::map<uint64_t, PendingJsDialog> g_js_dialog_pending;
std::mutex g_file_dialog_mu;
std::map<uint64_t, PendingFileDialog> g_file_dialog_pending;
std::mutex g_context_menu_mu;
std::map<uint64_t, PendingContextMenu> g_context_menu_pending;

}  // namespace

Exclr8CefOsrHandler::Exclr8CefOsrHandler(int id, int width, int height,
                                          float device_scale_factor,
                                          excef_paint_callback_t paint_cb)
    : id_(id), width_(width), height_(height),
      device_scale_factor_(device_scale_factor > 0.0f ? device_scale_factor : 1.0f),
      paint_cb_(paint_cb) {}

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
    // View rect is in DIPs / CSS pixels; CEF multiplies by device_scale_factor
    // (returned from GetScreenInfo) to size the paint buffer.
    rect.x = 0;
    rect.y = 0;
    rect.width = width_;
    rect.height = height_;
}

bool Exclr8CefOsrHandler::GetScreenInfo(CefRefPtr<CefBrowser> /*browser*/,
                                        CefScreenInfo& info) {
    info.device_scale_factor = device_scale_factor_;
    // Treat the entire view as the available screen — host doesn't carry
    // monitor-extent info across the C ABI in this version.
    info.rect = CefRect(0, 0, width_, height_);
    info.available_rect = info.rect;
    return true;
}

void Exclr8CefOsrHandler::OnPaint(CefRefPtr<CefBrowser> /*browser*/,
                                  PaintElementType type,
                                  const RectList& /*dirtyRects*/,
                                  const void* buffer,
                                  int width, int height) {
    if (type != PET_VIEW) return;
    if (paint_cb_) paint_cb_(id_, buffer, width, height);
}

void Exclr8CefOsrHandler::OnScrollOffsetChanged(CefRefPtr<CefBrowser> /*browser*/,
                                                 double x, double y) {
    if (g_scroll_offset_cb) g_scroll_offset_cb(id_, x, y);
}

bool Exclr8CefOsrHandler::OnAutoResize(CefRefPtr<CefBrowser> /*browser*/,
                                        const CefSize& new_size) {
    if (g_auto_resize_cb) g_auto_resize_cb(id_, new_size.width, new_size.height);
    // Return true to mark the resize as handled (the host responds via its
    // event hook). Returning false would suggest Chromium take its own
    // action, which in OSR mode is a no-op anyway.
    return true;
}

bool Exclr8CefOsrHandler::OnJSDialog(CefRefPtr<CefBrowser> /*browser*/,
                                      const CefString& /*origin_url*/,
                                      JSDialogType dialog_type,
                                      const CefString& message_text,
                                      const CefString& default_prompt_text,
                                      CefRefPtr<CefJSDialogCallback> callback,
                                      bool& /*suppress_message*/) {
    if (!g_js_dialog_cb) return false;  // fall back to CEF default
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        g_js_dialog_pending[token] = PendingJsDialog{id_, callback};
    }
    std::string msg = message_text.ToString();
    std::string def = default_prompt_text.ToString();
    g_js_dialog_cb(id_, token, static_cast<int>(dialog_type),
                   msg.c_str(), def.c_str());
    return true;  // we'll respond asynchronously via excef_resolve_js_dialog
}

bool Exclr8CefOsrHandler::OnBeforeUnloadDialog(CefRefPtr<CefBrowser> /*browser*/,
                                                const CefString& message_text,
                                                bool /*is_reload*/,
                                                CefRefPtr<CefJSDialogCallback> callback) {
    if (!g_js_dialog_cb) return false;
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        g_js_dialog_pending[token] = PendingJsDialog{id_, callback};
    }
    std::string msg = message_text.ToString();
    g_js_dialog_cb(id_, token, /*dialog_type=*/3, msg.c_str(), "");
    return true;
}

bool Exclr8CefOsrHandler::RunContextMenu(CefRefPtr<CefBrowser> /*browser*/,
                                          CefRefPtr<CefFrame> /*frame*/,
                                          CefRefPtr<CefContextMenuParams> params,
                                          CefRefPtr<CefMenuModel> model,
                                          CefRefPtr<CefRunContextMenuCallback> callback) {
    if (!g_context_menu_cb) return false;  // fall back: suppress (no menu in OSR)
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_context_menu_mu);
        g_context_menu_pending[token] = PendingContextMenu{id_, callback};
    }

    // Serialize top-level command items only. Format: "<id>\t<label>" per
    // line; separators encode as "0\t". Submenus / checks / radios are
    // flattened to plain entries for v1.
    std::string items;
    size_t count = model->GetCount();
    for (size_t i = 0; i < count; ++i) {
        cef_menu_item_type_t t = model->GetTypeAt(i);
        if (t == MENUITEMTYPE_SEPARATOR) {
            if (!items.empty()) items += '\n';
            items += "0\t";
            continue;
        }
        // Treat COMMAND / CHECK / RADIO uniformly; skip submenus (their
        // command id is 0 and selecting them would be ambiguous).
        if (t == MENUITEMTYPE_SUBMENU) continue;
        int id = model->GetCommandIdAt(i);
        std::string label = model->GetLabelAt(i).ToString();
        if (!items.empty()) items += '\n';
        items += std::to_string(id);
        items += '\t';
        items += label;
    }
    g_context_menu_cb(id_, token, params->GetXCoord(), params->GetYCoord(), items.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnFileDialog(CefRefPtr<CefBrowser> /*browser*/,
                                        FileDialogMode mode,
                                        const CefString& title,
                                        const CefString& default_file_path,
                                        const std::vector<CefString>& accept_filters,
                                        const std::vector<CefString>& /*accept_extensions*/,
                                        const std::vector<CefString>& /*accept_descriptions*/,
                                        CefRefPtr<CefFileDialogCallback> callback) {
    if (!g_file_dialog_cb) return false;  // fall back to CEF default
    uint64_t token = g_next_token.fetch_add(1, std::memory_order_relaxed);
    {
        std::lock_guard<std::mutex> lock(g_file_dialog_mu);
        g_file_dialog_pending[token] = PendingFileDialog{id_, callback};
    }
    // Join filters on '\n' for ABI simplicity — host splits on the other side.
    std::string filters;
    for (size_t i = 0; i < accept_filters.size(); ++i) {
        if (i) filters += '\n';
        filters += accept_filters[i].ToString();
    }
    std::string title_s = title.ToString();
    std::string default_s = default_file_path.ToString();
    g_file_dialog_cb(id_, token, static_cast<int>(mode),
                     title_s.c_str(), default_s.c_str(),
                     filters.c_str());
    return true;
}

bool Exclr8CefOsrHandler::OnCursorChange(CefRefPtr<CefBrowser> /*browser*/,
                                         CefCursorHandle /*cursor*/,
                                         cef_cursor_type_t type,
                                         const CefCursorInfo& /*custom_cursor_info*/) {
    if (g_cursor_change_cb) {
        g_cursor_change_cb(id_, static_cast<int>(type));
    }
    // Return true: host (Avalonia) is responsible for setting the platform cursor.
    return true;
}

bool Exclr8CefOsrHandler::OnConsoleMessage(CefRefPtr<CefBrowser> /*browser*/,
                                            cef_log_severity_t level,
                                            const CefString& message,
                                            const CefString& source,
                                            int line) {
    if (g_console_message_cb) {
        std::string msg = message.ToString();
        std::string src = source.ToString();
        g_console_message_cb(id_, static_cast<int>(level),
                             msg.c_str(), src.c_str(), line);
    }
    // Return false to let Chromium also emit its default console output.
    return false;
}

void Exclr8CefOsrHandler::OnAfterCreated(CefRefPtr<CefBrowser> browser) {
    browser_ = browser;
    browser_->GetHost()->WasResized();
    if (g_browser_initialized_cb) {
        g_browser_initialized_cb(id_);
    }
}

void Exclr8CefOsrHandler::OnBeforeClose(CefRefPtr<CefBrowser> /*browser*/) {
    int closed_id = id_;

    // Cancel any pending deferred-response callbacks owned by this browser.
    // Without this, the CEF callback objects stay refcounted in our registry
    // until process exit; worse, if the host eventually resolves the token,
    // we'd try to invoke a callback on a dead browser.
    {
        std::lock_guard<std::mutex> lock(g_js_dialog_mu);
        for (auto it = g_js_dialog_pending.begin(); it != g_js_dialog_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Continue(false, CefString());
                it = g_js_dialog_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_file_dialog_mu);
        for (auto it = g_file_dialog_pending.begin(); it != g_file_dialog_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_file_dialog_pending.erase(it);
            } else {
                ++it;
            }
        }
    }
    {
        std::lock_guard<std::mutex> lock(g_context_menu_mu);
        for (auto it = g_context_menu_pending.begin(); it != g_context_menu_pending.end(); ) {
            if (it->second.browser_id == closed_id) {
                if (it->second.callback) it->second.callback->Cancel();
                it = g_context_menu_pending.erase(it);
            } else {
                ++it;
            }
        }
    }

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

void Exclr8CefOsrHandler::OnLoadStart(CefRefPtr<CefBrowser> /*browser*/,
                                       CefRefPtr<CefFrame> frame,
                                       TransitionType /*transition_type*/) {
    if (g_load_start_cb) {
        std::string url = frame->GetURL().ToString();
        g_load_start_cb(id_, frame->IsMain() ? 1 : 0, url.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadEnd(CefRefPtr<CefBrowser> /*browser*/,
                                     CefRefPtr<CefFrame> frame,
                                     int httpStatusCode) {
    if (g_load_end_cb) {
        std::string url = frame->GetURL().ToString();
        g_load_end_cb(id_, frame->IsMain() ? 1 : 0, url.c_str(), httpStatusCode);
    }
}

void Exclr8CefOsrHandler::OnLoadError(CefRefPtr<CefBrowser> /*browser*/,
                                       CefRefPtr<CefFrame> frame,
                                       ErrorCode errorCode,
                                       const CefString& errorText,
                                       const CefString& failedUrl) {
    if (g_load_error_cb) {
        std::string text = errorText.ToString();
        std::string failed = failedUrl.ToString();
        g_load_error_cb(id_, frame->IsMain() ? 1 : 0,
                        static_cast<int>(errorCode),
                        text.c_str(), failed.c_str());
    }
}

void Exclr8CefOsrHandler::OnLoadingProgressChange(CefRefPtr<CefBrowser> /*browser*/,
                                                   double progress) {
    if (g_loading_progress_cb) {
        g_loading_progress_cb(id_, progress);
    }
}

void Exclr8CefOsrHandler::OnStatusMessage(CefRefPtr<CefBrowser> /*browser*/,
                                           const CefString& value) {
    if (g_status_message_cb) {
        std::string s = value.ToString();
        g_status_message_cb(id_, s.c_str());
    }
}

bool Exclr8CefOsrHandler::OnTooltip(CefRefPtr<CefBrowser> /*browser*/,
                                     CefString& text) {
    if (g_tooltip_cb) {
        std::string s = text.ToString();
        g_tooltip_cb(id_, s.c_str());
    }
    // Return false so Chromium can also render its default tooltip; hosts
    // that want to fully take over should suppress at the C# layer.
    return false;
}

void Exclr8CefOsrHandler::OnFaviconURLChange(CefRefPtr<CefBrowser> /*browser*/,
                                              const std::vector<CefString>& icon_urls) {
    if (g_favicon_cb) {
        // We pass only the first URL; CEF orders them by browser-preference
        // (highest-resolution PNG ahead of .ico), so the first is the most
        // useful for typical tab-strip / window-icon use.
        std::string first = icon_urls.empty() ? std::string() : icon_urls.front().ToString();
        g_favicon_cb(id_, first.c_str());
    }
}

void Exclr8CefOsrHandler::OnFullscreenModeChange(CefRefPtr<CefBrowser> /*browser*/,
                                                  bool fullscreen) {
    if (g_fullscreen_cb) {
        g_fullscreen_cb(id_, fullscreen ? 1 : 0);
    }
}

void Exclr8CefOsrHandler::SetSize(int width, int height) {
    width_ = width;
    height_ = height;
    if (browser_) browser_->GetHost()->WasResized();
}

void Exclr8CefOsrHandler::SetDeviceScaleFactor(float scale) {
    if (scale <= 0.0f) return;
    if (scale == device_scale_factor_) return;
    device_scale_factor_ = scale;
    if (browser_) browser_->GetHost()->NotifyScreenInfoChanged();
}

bool Exclr8CefOsrHandler::StartDragging(CefRefPtr<CefBrowser> browser,
                                         CefRefPtr<CefDragData> drag_data,
                                         DragOperationsMask allowed_ops,
                                         int x, int y) {
    drag_data_ = drag_data;
    drag_allowed_ops_ = allowed_ops;
    drag_current_op_ = DRAG_OPERATION_NONE;
    in_drag_ = true;

    // Self-target: tell CEF the drag has entered THIS view as a drop target.
    // For internal-only DnD this is the same browser; full OS-level DnD
    // would route through the host's window system instead.
    CefMouseEvent ev;
    ev.x = x;
    ev.y = y;
    browser->GetHost()->DragTargetDragEnter(drag_data, ev, allowed_ops);
    return true;
}

void Exclr8CefOsrHandler::UpdateDragCursor(CefRefPtr<CefBrowser> /*browser*/,
                                            DragOperation operation) {
    drag_current_op_ = operation;
}

CefRefPtr<CefBrowser> GetOsrBrowser(int browser_id) {
    auto it = g_osr_browsers.find(browser_id);
    if (it == g_osr_browsers.end()) return nullptr;
    return it->second->browser();
}

}  // namespace exclr8cef

// ---- C ABI implementations ------------------------------------------------

extern "C" int excef_create_offscreen_browser(int width, int height,
                                              float device_scale_factor,
                                              const char* url,
                                              excef_paint_callback_t paint) {
    if (!url || width <= 0 || height <= 0) return 0;

    int id = exclr8cef::g_next_id++;
    auto handler = CefRefPtr<exclr8cef::Exclr8CefOsrHandler>(
        new exclr8cef::Exclr8CefOsrHandler(id, width, height,
                                            device_scale_factor, paint));
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

extern "C" void excef_set_device_scale_factor(int browser_id, float scale) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return;
    it->second->SetDeviceScaleFactor(scale);
}

extern "C" void excef_set_zoom_level(int browser_id, double level) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return;
    auto browser = it->second->browser();
    if (browser) browser->GetHost()->SetZoomLevel(level);
}

extern "C" double excef_get_zoom_level(int browser_id) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return 0.0;
    auto browser = it->second->browser();
    return browser ? browser->GetHost()->GetZoomLevel() : 0.0;
}

namespace {

CefRefPtr<CefFrame> get_focused_frame(int browser_id) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return nullptr;
    auto browser = it->second->browser();
    if (!browser) return nullptr;
    auto frame = browser->GetFocusedFrame();
    return frame ? frame : browser->GetMainFrame();
}

}  // namespace

extern "C" void excef_copy(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Copy();
}

extern "C" void excef_paste(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Paste();
}

extern "C" void excef_cut(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Cut();
}

extern "C" void excef_select_all(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->SelectAll();
}

extern "C" void excef_undo(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Undo();
}

extern "C" void excef_redo(int browser_id) {
    auto f = get_focused_frame(browser_id);
    if (f) f->Redo();
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
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return;
    auto handler = it->second;
    auto b = handler->browser();
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    if (handler->is_in_drag()) {
        // While a drag started by StartDragging is in flight, every mouse
        // move must also notify CEF as a drag-over so the renderer updates
        // the drop indicators and computes the eventual drop op.
        b->GetHost()->DragTargetDragOver(ev, handler->drag_allowed_ops());
    }
    b->GetHost()->SendMouseMoveEvent(ev, mouse_leave != 0);
}

extern "C" void excef_send_mouse_click(int browser_id, int x, int y,
                                       int button, int mouse_up,
                                       int click_count, int modifiers) {
    auto it = exclr8cef::g_osr_browsers.find(browser_id);
    if (it == exclr8cef::g_osr_browsers.end()) return;
    auto handler = it->second;
    auto b = handler->browser();
    if (!b) return;
    CefMouseEvent ev;
    ev.x = x; ev.y = y;
    ev.modifiers = static_cast<uint32_t>(modifiers);
    auto type = static_cast<cef_mouse_button_type_t>(button);

    // Left-button release while a drag is in flight: complete the drop.
    if (mouse_up != 0 && button == EXCEF_MBT_LEFT && handler->is_in_drag()) {
        auto host = b->GetHost();
        host->DragTargetDrop(ev);
        host->DragSourceEndedAt(x, y, handler->drag_current_op());
        host->DragSourceSystemDragEnded();
        handler->clear_drag();
    }

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
    // Pass native_key_code through verbatim. DO NOT fall back to
    // windows_key_code on macOS: Carbon and Windows VK numbering disagree
    // on common keys (VK_TAB=0x09 collides with Carbon V=0x09, VK_V=0x56
    // collides with Carbon F5=0x60, etc.), so the fallback turns Tab
    // presses into V keypresses in Chromium's DOM event handler.
    ev.native_key_code = native_key_code;
    ev.is_system_key = is_system_key != 0;
    ev.character = static_cast<char16_t>(character);
    ev.unmodified_character = static_cast<char16_t>(unmodified_character);
    // CEF removed IsFocusOnEditableField in newer versions; matching
    // cefclient's macOS sample which hardcodes false here. Tab / Enter
    // still navigate because the renderer's focus controller detects
    // them via the character / windows_key_code combo.
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
extern "C" void excef_set_cursor_change_callback(excef_cursor_change_cb_t cb) { exclr8cef::g_cursor_change_cb = cb; }
extern "C" void excef_set_console_message_callback(excef_console_message_cb_t cb) { exclr8cef::g_console_message_cb = cb; }
extern "C" void excef_set_load_start_callback(excef_load_start_cb_t cb) { exclr8cef::g_load_start_cb = cb; }
extern "C" void excef_set_load_end_callback(excef_load_end_cb_t cb) { exclr8cef::g_load_end_cb = cb; }
extern "C" void excef_set_load_error_callback(excef_load_error_cb_t cb) { exclr8cef::g_load_error_cb = cb; }
extern "C" void excef_set_loading_progress_callback(excef_loading_progress_cb_t cb) { exclr8cef::g_loading_progress_cb = cb; }
extern "C" void excef_set_status_message_callback(excef_status_message_cb_t cb) { exclr8cef::g_status_message_cb = cb; }
extern "C" void excef_set_tooltip_callback(excef_tooltip_cb_t cb) { exclr8cef::g_tooltip_cb = cb; }
extern "C" void excef_set_favicon_callback(excef_favicon_cb_t cb) { exclr8cef::g_favicon_cb = cb; }
extern "C" void excef_set_fullscreen_callback(excef_fullscreen_cb_t cb) { exclr8cef::g_fullscreen_cb = cb; }
extern "C" void excef_set_browser_initialized_callback(excef_browser_initialized_cb_t cb) { exclr8cef::g_browser_initialized_cb = cb; }
extern "C" void excef_set_scroll_offset_callback(excef_scroll_offset_cb_t cb) { exclr8cef::g_scroll_offset_cb = cb; }
extern "C" void excef_set_auto_resize_callback(excef_auto_resize_cb_t cb) { exclr8cef::g_auto_resize_cb = cb; }
extern "C" void excef_set_js_dialog_callback(excef_js_dialog_cb_t cb) { exclr8cef::g_js_dialog_cb = cb; }
extern "C" void excef_set_file_dialog_callback(excef_file_dialog_cb_t cb) { exclr8cef::g_file_dialog_cb = cb; }
extern "C" void excef_set_context_menu_callback(excef_context_menu_cb_t cb) { exclr8cef::g_context_menu_cb = cb; }

namespace {
// CefPostTask in this CEF version doesn't take naked lambdas via base::BindOnce
// without a function pointer, so we use explicit CefTask subclasses to hop
// to the CEF UI thread.
class JsDialogResolveTask : public CefTask {
public:
    JsDialogResolveTask(CefRefPtr<CefJSDialogCallback> cb, bool ok, CefString input)
        : cb_(std::move(cb)), ok_(ok), input_(std::move(input)) {}
    void Execute() override { if (cb_) cb_->Continue(ok_, input_); }
private:
    CefRefPtr<CefJSDialogCallback> cb_;
    bool ok_;
    CefString input_;
    IMPLEMENT_REFCOUNTING(JsDialogResolveTask);
};

class FileDialogResolveTask : public CefTask {
public:
    FileDialogResolveTask(CefRefPtr<CefFileDialogCallback> cb, std::vector<CefString> paths)
        : cb_(std::move(cb)), paths_(std::move(paths)) {}
    void Execute() override { if (cb_) cb_->Continue(paths_); }
private:
    CefRefPtr<CefFileDialogCallback> cb_;
    std::vector<CefString> paths_;
    IMPLEMENT_REFCOUNTING(FileDialogResolveTask);
};

class ContextMenuResolveTask : public CefTask {
public:
    ContextMenuResolveTask(CefRefPtr<CefRunContextMenuCallback> cb, int command_id)
        : cb_(std::move(cb)), command_id_(command_id) {}
    void Execute() override {
        if (!cb_) return;
        if (command_id_ < 0) cb_->Cancel();
        else cb_->Continue(command_id_, EVENTFLAG_NONE);
    }
private:
    CefRefPtr<CefRunContextMenuCallback> cb_;
    int command_id_;
    IMPLEMENT_REFCOUNTING(ContextMenuResolveTask);
};
}  // namespace

// Resolve a pending JS dialog. Look the token up, invoke Continue on the
// CEF UI thread (where the callback expects to be called from), drop our
// refcount. No-op on unknown tokens — safe for double-resolve / post-close.
// Resolve a pending file dialog. `paths` is newline-separated absolute
// paths (empty / NULL = cancel). Look up token, hop to UI thread, invoke
// CefFileDialogCallback::Continue. No-op on unknown token.
extern "C" void excef_resolve_file_dialog(uint64_t token, const char* paths) {
    CefRefPtr<CefFileDialogCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_file_dialog_mu);
        auto it = exclr8cef::g_file_dialog_pending.find(token);
        if (it == exclr8cef::g_file_dialog_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_file_dialog_pending.erase(it);
    }
    if (!callback) return;

    std::vector<CefString> selected;
    if (paths && *paths) {
        std::string s(paths);
        size_t start = 0;
        for (size_t i = 0; i <= s.size(); ++i) {
            if (i == s.size() || s[i] == '\n') {
                if (i > start) {
                    CefString p;
                    p.FromString(s.substr(start, i - start));
                    selected.push_back(p);
                }
                start = i + 1;
            }
        }
    }
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(selected);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new FileDialogResolveTask(callback, std::move(selected))));
    }
}

// Resolve a pending context menu. -1 = cancel; otherwise the chosen
// command id (must be one of the items we surfaced via the menu callback).
extern "C" void excef_resolve_context_menu(uint64_t token, int command_id) {
    CefRefPtr<CefRunContextMenuCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_context_menu_mu);
        auto it = exclr8cef::g_context_menu_pending.find(token);
        if (it == exclr8cef::g_context_menu_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_context_menu_pending.erase(it);
    }
    if (!callback) return;
    if (CefCurrentlyOn(TID_UI)) {
        if (command_id < 0) callback->Cancel();
        else callback->Continue(command_id, EVENTFLAG_NONE);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new ContextMenuResolveTask(callback, command_id)));
    }
}

extern "C" void excef_resolve_js_dialog(uint64_t token, int success, const char* user_input) {
    CefRefPtr<CefJSDialogCallback> callback;
    {
        std::lock_guard<std::mutex> lock(exclr8cef::g_js_dialog_mu);
        auto it = exclr8cef::g_js_dialog_pending.find(token);
        if (it == exclr8cef::g_js_dialog_pending.end()) return;
        callback = it->second.callback;
        exclr8cef::g_js_dialog_pending.erase(it);
    }
    if (!callback) return;
    CefString input;
    if (user_input && *user_input) input.FromString(user_input);
    bool ok = success != 0;
    if (CefCurrentlyOn(TID_UI)) {
        callback->Continue(ok, input);
    } else {
        CefPostTask(TID_UI, CefRefPtr<CefTask>(
            new JsDialogResolveTask(callback, ok, input)));
    }
}

extern "C" int excef_can_zoom(int browser_id, int command) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return 0;
    return b->GetHost()->CanZoom(static_cast<cef_zoom_command_t>(command)) ? 1 : 0;
}

extern "C" void excef_set_auto_resize_enabled(int browser_id, int enabled,
                                                int min_w, int min_h,
                                                int max_w, int max_h) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (!b) return;
    if (enabled) {
        b->GetHost()->SetAutoResizeEnabled(true,
            CefSize(min_w, min_h), CefSize(max_w, max_h));
    } else {
        b->GetHost()->SetAutoResizeEnabled(false, CefSize(), CefSize());
    }
}

extern "C" void excef_exit_fullscreen(int browser_id, int will_cause_resize) {
    auto b = exclr8cef::GetOsrBrowser(browser_id);
    if (b) b->GetHost()->ExitFullscreen(will_cause_resize != 0);
}

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
