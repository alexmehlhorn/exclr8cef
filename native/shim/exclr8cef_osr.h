// CefClient implementation for off-screen-rendering browsers.
// Handles paint, lifecycle, address/title/loading-state, IPC eval results.

#ifndef EXCLR8CEF_OSR_H_
#define EXCLR8CEF_OSR_H_

#include "include/cef_client.h"
#include "include/cef_display_handler.h"
#include "include/cef_life_span_handler.h"
#include "include/cef_load_handler.h"
#include "include/cef_render_handler.h"

#include "exclr8cef.h"

namespace exclr8cef {

class Exclr8CefOsrHandler : public CefClient,
                            public CefRenderHandler,
                            public CefLifeSpanHandler,
                            public CefDisplayHandler,
                            public CefLoadHandler {
public:
    Exclr8CefOsrHandler(int id, int width, int height,
                        excef_paint_callback_t paint_cb);

    // CefClient
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }

    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
                                  CefRefPtr<CefFrame> frame,
                                  CefProcessId source_process,
                                  CefRefPtr<CefProcessMessage> message) override;

    // CefRenderHandler
    void GetViewRect(CefRefPtr<CefBrowser> browser, CefRect& rect) override;
    void OnPaint(CefRefPtr<CefBrowser> browser,
                 PaintElementType type,
                 const RectList& dirtyRects,
                 const void* buffer,
                 int width, int height) override;

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

    int id() const { return id_; }
    int width() const { return width_; }
    int height() const { return height_; }
    CefRefPtr<CefBrowser> browser() const { return browser_; }

    void SetSize(int width, int height);

private:
    int id_;
    int width_;
    int height_;
    excef_paint_callback_t paint_cb_;
    CefRefPtr<CefBrowser> browser_;

    IMPLEMENT_REFCOUNTING(Exclr8CefOsrHandler);
};

}  // namespace exclr8cef

#endif  // EXCLR8CEF_OSR_H_
