// CefClient implementation for off-screen-rendering browsers.
// Handles paint, lifecycle, address/title/loading-state, IPC eval results.

#ifndef EXCLR8CEF_OSR_H_
#define EXCLR8CEF_OSR_H_

#include "include/cef_client.h"
#include "include/cef_context_menu_handler.h"
#include "include/cef_dialog_handler.h"
#include "include/cef_display_handler.h"
#include "include/cef_download_handler.h"
#include "include/cef_jsdialog_handler.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_load_handler.h"
#include "include/cef_render_handler.h"

#include "exclr8cef.h"

namespace exclr8cef {

class Exclr8CefOsrHandler : public CefClient,
                            public CefRenderHandler,
                            public CefLifeSpanHandler,
                            public CefDisplayHandler,
                            public CefLoadHandler,
                            public CefJSDialogHandler,
                            public CefDialogHandler,
                            public CefContextMenuHandler,
                            public CefDownloadHandler {
public:
    Exclr8CefOsrHandler(int id, int width, int height, float device_scale_factor,
                        excef_paint_callback_t paint_cb);

    // CefClient
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefJSDialogHandler> GetJSDialogHandler() override { return this; }
    CefRefPtr<CefDialogHandler> GetDialogHandler() override { return this; }
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
    CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }

    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
                                  CefRefPtr<CefFrame> frame,
                                  CefProcessId source_process,
                                  CefRefPtr<CefProcessMessage> message) override;

    // CefRenderHandler
    void GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect) override;
    bool GetScreenInfo(CefRefPtr<CefBrowser> browser, CefScreenInfo& info) override;
    void OnPaint(CefRefPtr<CefBrowser> browser,
                 PaintElementType type,
                 const RectList& dirtyRects,
                 const void* buffer,
                 int width, int height) override;
    void OnScrollOffsetChanged(CefRefPtr<CefBrowser> browser,
                                double x, double y) override;

    bool OnAutoResize(CefRefPtr<CefBrowser> browser,
                       const CefSize& new_size) override;

    // CefJSDialogHandler
    bool OnJSDialog(CefRefPtr<CefBrowser> browser,
                    const CefString& origin_url,
                    JSDialogType dialog_type,
                    const CefString& message_text,
                    const CefString& default_prompt_text,
                    CefRefPtr<CefJSDialogCallback> callback,
                    bool& suppress_message) override;

    bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser> browser,
                               const CefString& message_text,
                               bool is_reload,
                               CefRefPtr<CefJSDialogCallback> callback) override;

    // CefDialogHandler
    bool OnFileDialog(CefRefPtr<CefBrowser> browser,
                       FileDialogMode mode,
                       const CefString& title,
                       const CefString& default_file_path,
                       const std::vector<CefString>& accept_filters,
                       const std::vector<CefString>& accept_extensions,
                       const std::vector<CefString>& accept_descriptions,
                       CefRefPtr<CefFileDialogCallback> callback) override;

    // CefContextMenuHandler
    bool RunContextMenu(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame,
                         CefRefPtr<CefContextMenuParams> params,
                         CefRefPtr<CefMenuModel> model,
                         CefRefPtr<CefRunContextMenuCallback> callback) override;

    // CefDownloadHandler
    bool OnBeforeDownload(CefRefPtr<CefBrowser> browser,
                           CefRefPtr<CefDownloadItem> download_item,
                           const CefString& suggested_name,
                           CefRefPtr<CefBeforeDownloadCallback> callback) override;

    void OnDownloadUpdated(CefRefPtr<CefBrowser> browser,
                            CefRefPtr<CefDownloadItem> download_item,
                            CefRefPtr<CefDownloadItemCallback> callback) override;

    bool StartDragging(CefRefPtr<CefBrowser> browser,
                       CefRefPtr<CefDragData> drag_data,
                       DragOperationsMask allowed_ops,
                       int x, int y) override;
    void UpdateDragCursor(CefRefPtr<CefBrowser> browser,
                          DragOperation operation) override;

    // CefLifeSpanHandler
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override;
    void OnBeforeClose(CefRefPtr<CefBrowser> browser) override;

    // CefDisplayHandler
    void OnAddressChange(CefRefPtr<CefBrowser> browser,
                         CefRefPtr<CefFrame> frame,
                         const CefString& url) override;
    void OnTitleChange(CefRefPtr<CefBrowser> browser,
                       const CefString& title) override;

    // CefLoadHandler
    void OnLoadingStateChange(CefRefPtr<CefBrowser> browser,
                              bool isLoading,
                              bool canGoBack,
                              bool canGoForward) override;

    void OnLoadStart(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     TransitionType transition_type) override;

    void OnLoadEnd(CefRefPtr<CefBrowser> browser,
                   CefRefPtr<CefFrame> frame,
                   int httpStatusCode) override;

    void OnLoadError(CefRefPtr<CefBrowser> browser,
                     CefRefPtr<CefFrame> frame,
                     ErrorCode errorCode,
                     const CefString& errorText,
                     const CefString& failedUrl) override;

    void OnLoadingProgressChange(CefRefPtr<CefBrowser> browser,
                                  double progress) override;

    // CefDisplayHandler
    bool OnCursorChange(CefRefPtr<CefBrowser> browser,
                        CefCursorHandle cursor,
                        cef_cursor_type_t type,
                        const CefCursorInfo& custom_cursor_info) override;

    bool OnConsoleMessage(CefRefPtr<CefBrowser> browser,
                          cef_log_severity_t level,
                          const CefString& message,
                          const CefString& source,
                          int line) override;

    void OnStatusMessage(CefRefPtr<CefBrowser> browser,
                         const CefString& value) override;

    bool OnTooltip(CefRefPtr<CefBrowser> browser,
                   CefString& text) override;

    void OnFaviconURLChange(CefRefPtr<CefBrowser> browser,
                            const std::vector<CefString>& icon_urls) override;

    void OnFullscreenModeChange(CefRefPtr<CefBrowser> browser,
                                 bool fullscreen) override;

    int id() const { return id_; }
    int width() const { return width_; }
    int height() const { return height_; }
    float device_scale_factor() const { return device_scale_factor_; }
    CefRefPtr<CefBrowser> browser() const { return browser_; }

    void SetSize(int width, int height);
    void SetDeviceScaleFactor(float scale);

    // Drag-and-drop bookkeeping. The shim handles internal-page DnD entirely
    // by intercepting mouse events while in_drag_ is true and converting
    // them to DragTarget* calls on the browser host.
    bool is_in_drag() const { return in_drag_; }
    DragOperationsMask drag_allowed_ops() const { return drag_allowed_ops_; }
    DragOperation drag_current_op() const { return drag_current_op_; }
    void clear_drag() {
        in_drag_ = false;
        drag_data_ = nullptr;
        drag_allowed_ops_ = DRAG_OPERATION_NONE;
        drag_current_op_ = DRAG_OPERATION_NONE;
    }

private:
    int id_;
    int width_;   // DIP / CSS-pixel size of the view rect.
    int height_;
    float device_scale_factor_;
    excef_paint_callback_t paint_cb_;
    CefRefPtr<CefBrowser> browser_;

    bool in_drag_ = false;
    CefRefPtr<CefDragData> drag_data_;
    DragOperationsMask drag_allowed_ops_ = DRAG_OPERATION_NONE;
    DragOperation drag_current_op_ = DRAG_OPERATION_NONE;

    IMPLEMENT_REFCOUNTING(Exclr8CefOsrHandler);
};

// Look up the CefBrowser for an OSR browser id created via
// excef_create_offscreen_browser. Returns nullptr if unknown.
// Exposed so optional extensions (e.g. exclr8cef_print.cc) can act on a
// browser without reaching into the OSR handler map directly.
CefRefPtr<CefBrowser> GetOsrBrowser(int browser_id);

}  // namespace exclr8cef

#endif  // EXCLR8CEF_OSR_H_
