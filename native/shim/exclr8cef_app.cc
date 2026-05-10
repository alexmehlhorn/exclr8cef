#include "exclr8cef_app.h"

#include <string>
#include <vector>

#include "include/base/cef_callback.h"
#include "include/cef_browser.h"
#include "include/cef_command_line.h"
#include "include/cef_frame.h"
#include "include/cef_process_message.h"
#include "include/cef_v8.h"
#include "include/views/cef_browser_view.h"
#include "include/views/cef_browser_view_delegate.h"
#include "include/views/cef_window.h"
#include "include/views/cef_window_delegate.h"
#include "include/wrapper/cef_closure_task.h"
#include "include/wrapper/cef_helpers.h"

#include "exclr8cef_client.h"

namespace exclr8cef {

namespace {

CefRefPtr<Exclr8CefApp> g_app;
std::vector<std::string> g_pending_urls;
bool g_context_initialized = false;
ScheduleMessagePumpWorkCallback g_schedule_pump_cb = nullptr;

class Exclr8CefWindowDelegate : public CefWindowDelegate {
public:
    Exclr8CefWindowDelegate(CefRefPtr<CefBrowserView> browser_view,
                            cef_runtime_style_t runtime_style,
                            cef_show_state_t initial_show_state)
        : browser_view_(browser_view),
          runtime_style_(runtime_style),
          initial_show_state_(initial_show_state) {}

    Exclr8CefWindowDelegate(const Exclr8CefWindowDelegate&) = delete;
    Exclr8CefWindowDelegate& operator=(const Exclr8CefWindowDelegate&) = delete;

    void OnWindowCreated(CefRefPtr<CefWindow> window) override {
        window->AddChildView(browser_view_);
        if (initial_show_state_ != CEF_SHOW_STATE_HIDDEN) {
            window->Show();
        }
    }

    void OnWindowDestroyed(CefRefPtr<CefWindow> /*window*/) override {
        browser_view_ = nullptr;
    }

    bool CanClose(CefRefPtr<CefWindow> /*window*/) override {
        if (browser_view_) {
            CefRefPtr<CefBrowser> browser = browser_view_->GetBrowser();
            if (browser) return browser->GetHost()->TryCloseBrowser();
        }
        return true;
    }

    CefSize GetPreferredSize(CefRefPtr<CefView> /*view*/) override {
        return CefSize(1024, 768);
    }

    cef_show_state_t GetInitialShowState(CefRefPtr<CefWindow> /*window*/) override {
        return initial_show_state_;
    }

    cef_runtime_style_t GetWindowRuntimeStyle() override {
        return runtime_style_;
    }

private:
    CefRefPtr<CefBrowserView> browser_view_;
    const cef_runtime_style_t runtime_style_;
    const cef_show_state_t initial_show_state_;

    IMPLEMENT_REFCOUNTING(Exclr8CefWindowDelegate);
};

class Exclr8CefBrowserViewDelegate : public CefBrowserViewDelegate {
public:
    explicit Exclr8CefBrowserViewDelegate(cef_runtime_style_t runtime_style)
        : runtime_style_(runtime_style) {}

    Exclr8CefBrowserViewDelegate(const Exclr8CefBrowserViewDelegate&) = delete;
    Exclr8CefBrowserViewDelegate& operator=(const Exclr8CefBrowserViewDelegate&) = delete;

    bool OnPopupBrowserViewCreated(CefRefPtr<CefBrowserView> /*browser_view*/,
                                   CefRefPtr<CefBrowserView> popup_browser_view,
                                   bool /*is_devtools*/) override {
        CefWindow::CreateTopLevelWindow(new Exclr8CefWindowDelegate(
            popup_browser_view, runtime_style_, CEF_SHOW_STATE_NORMAL));
        return true;
    }

    cef_runtime_style_t GetBrowserRuntimeStyle() override {
        return runtime_style_;
    }

private:
    const cef_runtime_style_t runtime_style_;

    IMPLEMENT_REFCOUNTING(Exclr8CefBrowserViewDelegate);
};

void CreateBrowserOnUI(const std::string& url) {
    CEF_REQUIRE_UI_THREAD();

    cef_runtime_style_t runtime_style = CEF_RUNTIME_STYLE_DEFAULT;

    CefRefPtr<Exclr8CefClient> client(new Exclr8CefClient());
    CefBrowserSettings browser_settings;

    CefRefPtr<CefBrowserView> browser_view = CefBrowserView::CreateBrowserView(
        client, url, browser_settings,
        /*extra_info=*/nullptr,
        /*request_context=*/nullptr,
        new Exclr8CefBrowserViewDelegate(runtime_style));

    if (!browser_view) return;

    CefWindow::CreateTopLevelWindow(new Exclr8CefWindowDelegate(
        browser_view, runtime_style, CEF_SHOW_STATE_NORMAL));
}

}  // namespace

Exclr8CefApp::Exclr8CefApp() = default;

void Exclr8CefApp::OnBeforeCommandLineProcessing(
    const CefString& process_type,
    CefRefPtr<CefCommandLine> command_line) {
    if (!process_type.empty()) return;

    // Skip the macOS Keychain prompt that blocks startup.
    command_line->AppendSwitch("use-mock-keychain");
}

void Exclr8CefApp::OnContextInitialized() {
    CEF_REQUIRE_UI_THREAD();
    g_context_initialized = true;

    for (const auto& url : g_pending_urls) {
        CreateBrowserOnUI(url);
    }
    g_pending_urls.clear();
}

void Exclr8CefApp::OnScheduleMessagePumpWork(int64_t delay_ms) {
    if (g_schedule_pump_cb) {
        g_schedule_pump_cb(delay_ms);
    }
}

bool Exclr8CefApp::OnProcessMessageReceived(
    CefRefPtr<CefBrowser> browser,
    CefRefPtr<CefFrame> frame,
    CefProcessId source_process,
    CefRefPtr<CefProcessMessage> message) {
    // Renderer-process side: handle "Eval" requests sent from the browser
    // process. Run the JS in the frame's V8 context, JSON-serialize the
    // result, and send back via "EvalResult".
    if (message->GetName() != "Eval") return false;

    auto args = message->GetArgumentList();
    int request_id = args->GetInt(0);
    CefString code = args->GetString(1);

    auto context = frame->GetV8Context();
    if (!context || !context->Enter()) {
        auto resp = CefProcessMessage::Create("EvalResult");
        auto rargs = resp->GetArgumentList();
        rargs->SetInt(0, request_id);
        rargs->SetBool(1, false);
        rargs->SetString(2, "no v8 context");
        frame->SendProcessMessage(PID_BROWSER, resp);
        return true;
    }

    CefRefPtr<CefV8Value> retval;
    CefRefPtr<CefV8Exception> exception;
    bool ok = context->Eval(code, "exclr8cef:eval", 0, retval, exception);

    std::string serialized;
    std::string error;
    if (!ok) {
        error = exception ? exception->GetMessage().ToString()
                          : std::string("eval failed");
    } else if (retval) {
        // Serialize via JSON.stringify(retval). If undefined, use 'null'.
        auto global = context->GetGlobal();
        auto json_obj = global->GetValue("JSON");
        if (json_obj && json_obj->IsObject()) {
            auto stringify = json_obj->GetValue("stringify");
            if (stringify && stringify->IsFunction()) {
                CefV8ValueList sargs;
                sargs.push_back(retval);
                auto json_result = stringify->ExecuteFunction(json_obj, sargs);
                if (json_result && json_result->IsString()) {
                    serialized = json_result->GetStringValue().ToString();
                }
            }
        }
        if (serialized.empty() && retval->IsString()) {
            serialized = "\"" + retval->GetStringValue().ToString() + "\"";
        }
        if (serialized.empty()) serialized = "null";
    } else {
        serialized = "null";
    }

    context->Exit();

    auto resp = CefProcessMessage::Create("EvalResult");
    auto rargs = resp->GetArgumentList();
    rargs->SetInt(0, request_id);
    rargs->SetBool(1, ok);
    rargs->SetString(2, ok ? serialized : error);
    frame->SendProcessMessage(PID_BROWSER, resp);
    return true;
}

CefRefPtr<Exclr8CefApp> EnsureApp() {
    if (!g_app) g_app = new Exclr8CefApp();
    return g_app;
}

void ResetApp() {
    g_app = nullptr;
    g_context_initialized = false;
    g_pending_urls.clear();
}

void SetPendingBrowserUrl(const std::string& url) {
    if (!g_context_initialized) {
        g_pending_urls.push_back(url);
        return;
    }
    if (CefCurrentlyOn(TID_UI)) {
        CreateBrowserOnUI(url);
    } else {
        CefPostTask(TID_UI, base::BindOnce(&CreateBrowserOnUI, url));
    }
}

void SetSchedulePumpCallback(ScheduleMessagePumpWorkCallback cb) {
    g_schedule_pump_cb = cb;
}

}  // namespace exclr8cef
