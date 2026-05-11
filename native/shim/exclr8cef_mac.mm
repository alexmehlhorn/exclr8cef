// macOS-specific shim implementation:
// - CefScopedLibraryLoader to load the CEF framework dylib at runtime
// - NSApplication subclass conforming to CefAppProtocol (CEF requirement)
// - excef_execute_process / excef_initialize / excef_run_message_loop / excef_shutdown

#import <Cocoa/Cocoa.h>

#include <memory>

#include "include/cef_app.h"
#include "include/cef_application_mac.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/wrapper/cef_library_loader.h"

#include "exclr8cef.h"
#include "exclr8cef_app.h"
#include "exclr8cef_client.h"

namespace {
std::unique_ptr<CefScopedLibraryLoader> g_library_loader;
}

@interface Exclr8CefApplication : NSApplication <CefAppProtocol> {
@private
    BOOL handlingSendEvent_;
}
@end

@implementation Exclr8CefApplication

- (BOOL)isHandlingSendEvent {
    return handlingSendEvent_;
}

- (void)setHandlingSendEvent:(BOOL)handlingSendEvent {
    handlingSendEvent_ = handlingSendEvent;
}

- (void)sendEvent:(NSEvent*)event {
    CefScopedSendingEvent sendingEventScoper;
    [super sendEvent:event];
}

- (void)terminate:(id)sender {
    CefQuitMessageLoop();
}

@end

extern "C" int excef_execute_process(int argc, char** argv) {
    @autoreleasepool {
        CefScopedLibraryLoader loader;
        if (!loader.LoadInHelper()) return 1;

        CefMainArgs main_args(argc, argv);
        // Pass our app to the helper subprocess so the renderer can
        // handle "Eval" IPC messages via CefRenderProcessHandler.
        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        return CefExecuteProcess(main_args, app.get(), nullptr);
    }
}

extern "C" int excef_initialize(int argc, char** argv,
                                const char* subprocess_path) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        settings.no_sandbox = true;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path)
                .FromASCII(subprocess_path);
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" void excef_run_message_loop(void) {
    @autoreleasepool {
        CefRunMessageLoop();
    }
}

extern "C" void excef_shutdown(void) {
    @autoreleasepool {
        CefShutdown();
        exclr8cef::ResetApp();
        exclr8cef::SetSchedulePumpCallback(nullptr);
        g_library_loader.reset();
    }
}

// ---- External message pump variant ---------------------------------------

// Common implementation for both external-pump variants. Set
// `windowless_rendering_enabled = enable_osr`.
static int initialize_with_pump_impl(int argc, char** argv,
                                     const char* subprocess_path,
                                     excef_schedule_pump_work_t cb,
                                     bool enable_osr) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        exclr8cef::SetSchedulePumpCallback(
            reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        settings.no_sandbox = true;
        settings.external_message_pump = true;
        settings.windowless_rendering_enabled = enable_osr ? 1 : 0;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path).FromASCII(subprocess_path);
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" int excef_initialize_offscreen(int argc, char** argv,
                                          const char* subprocess_path,
                                          excef_schedule_pump_work_t cb) {
    return initialize_with_pump_impl(argc, argv, subprocess_path, cb,
                                     /*enable_osr=*/true);
}

extern "C" int excef_initialize_external_pump(int argc, char** argv,
                                              const char* subprocess_path,
                                              excef_schedule_pump_work_t cb) {
    @autoreleasepool {
        g_library_loader = std::make_unique<CefScopedLibraryLoader>();
        if (!g_library_loader->LoadInMain()) return 1;

        [Exclr8CefApplication sharedApplication];

        // Register the schedule callback with the app handler so CEF's
        // OnScheduleMessagePumpWork forwards into managed code.
        exclr8cef::SetSchedulePumpCallback(
            reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

        CefMainArgs main_args(argc, argv);
        CefSettings settings;
        settings.no_sandbox = true;
        settings.external_message_pump = true;
        if (subprocess_path && *subprocess_path) {
            CefString(&settings.browser_subprocess_path).FromASCII(subprocess_path);
        }
        exclr8cef::ApplyHostInitSettings(settings);

        CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
        if (!CefInitialize(main_args, settings, app.get(), nullptr)) return 2;
        return 0;
    }
}

extern "C" void excef_do_message_loop_work(void) {
    @autoreleasepool {
        CefDoMessageLoopWork();
    }
}

// ---- Embedded browser ----------------------------------------------------

extern "C" void* excef_create_browser_view(int width, int height,
                                           const char* url) {
    if (!url) return nullptr;

    @autoreleasepool {
        // Create a host NSView. Its frame is the requested size; the host UI
        // framework (Avalonia) will reposition/size as needed via Cocoa.
        NSView* host = [[NSView alloc]
            initWithFrame:NSMakeRect(0, 0, width, height)];
        [host setAutoresizingMask:NSViewWidthSizable | NSViewHeightSizable];

        // CefWindowInfo::SetAsChild parents the new browser view inside our
        // host. Alloy runtime style is required for native-window embedding;
        // Chrome runtime style only supports its own Views-managed windows.
        CefWindowInfo window_info;
        window_info.SetAsChild((__bridge void*)host,
                               CefRect(0, 0, width, height));
        window_info.runtime_style = CEF_RUNTIME_STYLE_ALLOY;

        CefRefPtr<exclr8cef::Exclr8CefClient> client(new exclr8cef::Exclr8CefClient());
        CefBrowserSettings browser_settings;

        bool ok = CefBrowserHost::CreateBrowser(
            window_info, client.get(), url, browser_settings,
            /*extra_info=*/nullptr, /*request_context=*/nullptr);

        if (!ok) {
            return nullptr;
        }

        // Caller takes ownership; ARC bridges and releases when consumer drops it.
        // CFBridgingRetain returns +1 retain count for the caller to balance.
        return (void*)CFBridgingRetain(host);
    }
}
