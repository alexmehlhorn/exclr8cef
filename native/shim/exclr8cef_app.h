// CefApp implementation. Implements both browser-process and
// render-process roles so JS-eval IPC can flow through the same instance
// in either process type.

#ifndef EXCLR8CEF_APP_H_
#define EXCLR8CEF_APP_H_

#include <cstdint>
#include <string>

#include "include/cef_app.h"
#include "include/cef_render_process_handler.h"

namespace exclr8cef {

class Exclr8CefApp : public CefApp,
                     public CefBrowserProcessHandler,
                     public CefRenderProcessHandler {
public:
    Exclr8CefApp();

    // CefApp
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override {
        return this;
    }
    CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override {
        return this;
    }

    void OnBeforeCommandLineProcessing(
        const CefString& process_type,
        CefRefPtr<CefCommandLine> command_line) override;

    // CefBrowserProcessHandler
    void OnContextInitialized() override;
    void OnScheduleMessagePumpWork(int64_t delay_ms) override;

    // CefRenderProcessHandler — used in the renderer subprocess.
    bool OnProcessMessageReceived(
        CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame,
        CefProcessId source_process,
        CefRefPtr<CefProcessMessage> message) override;

private:
    IMPLEMENT_REFCOUNTING(Exclr8CefApp);
    DISALLOW_COPY_AND_ASSIGN(Exclr8CefApp);
};

CefRefPtr<Exclr8CefApp> EnsureApp();
void ResetApp();

void SetPendingBrowserUrl(const std::string& url);

typedef void (*ScheduleMessagePumpWorkCallback)(int64_t delay_ms);
void SetSchedulePumpCallback(ScheduleMessagePumpWorkCallback cb);

}  // namespace exclr8cef

#endif  // EXCLR8CEF_APP_H_
