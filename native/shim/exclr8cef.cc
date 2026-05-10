#include "exclr8cef.h"

#include <cstdio>
#include <cstring>
#include <string>

#include "include/cef_app.h"
#include "include/cef_version.h"

#include "exclr8cef_app.h"

#if defined(_WIN32)
#  include <windows.h>
#endif

namespace {

constexpr const char kShimVersion[] = "0.5.0-stage5";

void copy_to(char* dst, size_t dst_size, const char* src) {
    if (!dst || dst_size == 0) return;
    std::strncpy(dst, src, dst_size - 1);
    dst[dst_size - 1] = '\0';
}

#if !defined(__APPLE__)
// CefMainArgs has different constructors per platform. On Windows it takes
// HINSTANCE; on Linux it takes argc/argv. Wrap so the rest of the code
// stays platform-agnostic.
static CefMainArgs make_main_args(int argc, char** argv) {
#  if defined(_WIN32)
    (void)argc; (void)argv;
    return CefMainArgs(GetModuleHandle(nullptr));
#  else
    return CefMainArgs(argc, argv);
#  endif
}
#endif

}  // namespace

extern "C" void excef_get_versions(excef_versions* out) {
    if (!out) return;

    copy_to(out->shim_version, EXCEF_VERSION_BUFFER_SIZE, kShimVersion);

    char cef_buf[EXCEF_VERSION_BUFFER_SIZE];
    std::snprintf(cef_buf, sizeof(cef_buf), "%d.%d.%d",
                  CEF_VERSION_MAJOR, CEF_VERSION_MINOR, CEF_VERSION_PATCH);
    copy_to(out->cef_version, EXCEF_VERSION_BUFFER_SIZE, cef_buf);

    char chrome_buf[EXCEF_VERSION_BUFFER_SIZE];
    std::snprintf(chrome_buf, sizeof(chrome_buf), "%d.%d.%d.%d",
                  CHROME_VERSION_MAJOR, CHROME_VERSION_MINOR,
                  CHROME_VERSION_BUILD, CHROME_VERSION_PATCH);
    copy_to(out->chromium_version, EXCEF_VERSION_BUFFER_SIZE, chrome_buf);
}

extern "C" int excef_create_browser(const char* url) {
    if (!url) return 1;
    exclr8cef::SetPendingBrowserUrl(url);
    return 0;
}

extern "C" void excef_quit_message_loop(void) {
    CefQuitMessageLoop();
}

// ---- Lifecycle (Windows/Linux) -------------------------------------------
//
// macOS-specific implementations live in exclr8cef_mac.mm because they
// need CefScopedLibraryLoader + an NSApplication subclass. On Windows
// and Linux libcef is linked normally and the host owns the run loop.

#if !defined(__APPLE__)

extern "C" int excef_execute_process(int argc, char** argv) {
    auto main_args = make_main_args(argc, argv);
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    return CefExecuteProcess(main_args, app, nullptr);
}

extern "C" int excef_initialize(int argc, char** argv,
                                const char* subprocess_path) {
    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    settings.no_sandbox = true;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromASCII(subprocess_path);
    }
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 1;
    return 0;
}

extern "C" int excef_initialize_external_pump(int argc, char** argv,
                                              const char* subprocess_path,
                                              excef_schedule_pump_work_t cb) {
    exclr8cef::SetSchedulePumpCallback(
        reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    settings.no_sandbox = true;
    settings.external_message_pump = true;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromASCII(subprocess_path);
    }
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 2;
    return 0;
}

extern "C" int excef_initialize_offscreen(int argc, char** argv,
                                          const char* subprocess_path,
                                          excef_schedule_pump_work_t cb) {
    exclr8cef::SetSchedulePumpCallback(
        reinterpret_cast<exclr8cef::ScheduleMessagePumpWorkCallback>(cb));

    auto main_args = make_main_args(argc, argv);
    CefSettings settings;
    settings.no_sandbox = true;
    settings.external_message_pump = true;
    settings.windowless_rendering_enabled = true;
    if (subprocess_path && *subprocess_path) {
        CefString(&settings.browser_subprocess_path).FromASCII(subprocess_path);
    }
    CefRefPtr<exclr8cef::Exclr8CefApp> app = exclr8cef::EnsureApp();
    if (!CefInitialize(main_args, settings, app, nullptr)) return 2;
    return 0;
}

extern "C" void excef_run_message_loop(void) {
    CefRunMessageLoop();
}

extern "C" void excef_do_message_loop_work(void) {
    CefDoMessageLoopWork();
}

extern "C" void excef_shutdown(void) {
    CefShutdown();
    exclr8cef::ResetApp();
    exclr8cef::SetSchedulePumpCallback(nullptr);
}

#endif  // !__APPLE__
