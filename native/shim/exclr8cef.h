// Exclr8CEF — public C ABI for the Chromium binding.
// This header is what ClangSharp parses to generate C# P/Invoke bindings,
// so keep it strict C — no C++ types crossing the boundary.

#ifndef EXCLR8CEF_H_
#define EXCLR8CEF_H_

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  ifdef EXCLR8CEF_BUILDING
#    define EXCEF_API __declspec(dllexport)
#  else
#    define EXCEF_API __declspec(dllimport)
#  endif
#else
#  define EXCEF_API __attribute__((visibility("default")))
#endif

// ---- Versions -------------------------------------------------------------

#define EXCEF_VERSION_BUFFER_SIZE 64

typedef struct excef_versions {
    char shim_version[EXCEF_VERSION_BUFFER_SIZE];
    char cef_version[EXCEF_VERSION_BUFFER_SIZE];
    char chromium_version[EXCEF_VERSION_BUFFER_SIZE];
} excef_versions;

EXCEF_API void excef_get_versions(excef_versions* out);

// ---- Process / lifecycle --------------------------------------------------

// Returns:
//   >=  0  → caller is a CEF subprocess; exit with this code immediately
//   -1     → caller is the main process; continue with excef_initialize
EXCEF_API int excef_execute_process(int argc, char** argv);

// Initialize CEF in the main process. Sets up NSApplication on macOS.
//   subprocess_path: path to the helper executable. May be NULL on macOS,
//                    where CEF auto-discovers the Helper.app inside the
//                    bundle.
EXCEF_API int excef_initialize(int argc, char** argv, const char* subprocess_path);

// Open a top-level browser window pointed at `url`. Safe to call before or
// after excef_initialize; URLs requested before init complete are queued
// and opened automatically when CEF is ready.
EXCEF_API int excef_create_browser(const char* url);

// Run CEF's message loop. Blocks until all browsers close or
// excef_quit_message_loop is called.
EXCEF_API void excef_run_message_loop(void);

// Request that the message loop exit. Safe to call from any thread.
EXCEF_API void excef_quit_message_loop(void);

// Shut down CEF. Call after excef_run_message_loop returns.
EXCEF_API void excef_shutdown(void);

// ---- External message pump (for hosts that own their own UI loop) --------
//
// Use this initialization variant when the host (e.g. Avalonia, WPF)
// already runs the platform message loop and CEF needs to cooperate.
// Sets CefSettings.external_message_pump = true and registers a callback
// that CEF calls to ask the host to schedule pump work.

// Schedule a call to excef_do_message_loop_work after `delay_ms` ms.
// Called from CEF's internal threads — the host should marshal back to its
// UI thread and call excef_do_message_loop_work when the delay elapses.
typedef void (*excef_schedule_pump_work_t)(long long delay_ms);

EXCEF_API int excef_initialize_external_pump(int argc, char** argv,
                                             const char* subprocess_path,
                                             excef_schedule_pump_work_t schedule_callback);

// Drive CEF's pending work. Call from the host UI thread when the
// scheduled callback's delay has elapsed.
EXCEF_API void excef_do_message_loop_work(void);

// ---- Embedded browser ----------------------------------------------------
//
// Create an NSView (macOS) / HWND (Windows) / X11 Window (Linux) that
// hosts a CEF browser, sized to width x height, navigated to `url`.
// The caller (UI framework) takes ownership of the returned native handle:
//   - On macOS the NSView is returned with +1 retain; AppKit/ARC consumes it.
// The CEF browser is created asynchronously and added as a subview of the
// returned host view.
EXCEF_API void* excef_create_browser_view(int width, int height,
                                          const char* url);

// ---- Off-screen rendering (OSR) ------------------------------------------
//
// OSR mode: CEF doesn't create any native windows. Instead it renders to
// an internal pixel buffer and notifies the host via a paint callback,
// which the host can blit onto its own UI surface (Skia, GDI, etc.).
// This sidesteps macOS NSView/NSApplication conflicts when CEF is hosted
// inside an existing UI framework like Avalonia.

// Paint callback. `buffer` is BGRA8888 (32-bit, top-left origin) and is
// only valid during the call — copy what you need. Stride = width * 4.
typedef void (*excef_paint_callback_t)(
    int browser_id,
    const void* buffer,
    int width,
    int height);

// Initialize CEF for off-screen rendering with an external message pump.
// Combines external_message_pump=true and windowless_rendering_enabled=true.
EXCEF_API int excef_initialize_offscreen(
    int argc, char** argv,
    const char* subprocess_path,
    excef_schedule_pump_work_t schedule_callback);

// Create an off-screen browser whose view rect is `width x height` DIPs (CSS
// pixels), navigated to `url`. `device_scale_factor` controls HiDPI rendering:
// CEF allocates a paint buffer of (width × scale) × (height × scale) physical
// pixels and the page is laid out at the DIP size. Pass 1.0 for non-HiDPI hosts.
// `paint` fires whenever CEF has new pixels — the (width, height) reported to
// `paint` are in physical pixels.
// Returns a browser id (>= 1) on success, 0 on failure.
EXCEF_API int excef_create_offscreen_browser(
    int width, int height,
    float device_scale_factor,
    const char* url,
    excef_paint_callback_t paint);

// Tell CEF the browser has been resized to `width x height` DIPs and request
// a fresh paint. The buffer size will follow the current device_scale_factor.
EXCEF_API void excef_resize_offscreen_browser(int browser_id,
                                              int width, int height);

// Update the browser's device scale factor (e.g. when the host control is
// dragged across monitors with different DPI). CEF will re-layout and emit a
// fresh paint at the new physical-pixel size.
EXCEF_API void excef_set_device_scale_factor(int browser_id, float scale);

// Set the zoom level. 0.0 == 100% (default). Each 1.0 step is ~120% of the
// previous (CEF/Chromium convention: percentage = 100 * pow(1.2, level)).
EXCEF_API void excef_set_zoom_level(int browser_id, double level);
EXCEF_API double excef_get_zoom_level(int browser_id);

// Clipboard / editing primitives. Operate on the browser's focused frame.
// CEF in OSR mode does not auto-execute these from keyboard accelerators;
// the host must invoke them when Cmd/Ctrl + C / V / X / A / Z / Y is pressed.
EXCEF_API void excef_copy(int browser_id);
EXCEF_API void excef_paste(int browser_id);
EXCEF_API void excef_cut(int browser_id);
EXCEF_API void excef_select_all(int browser_id);
EXCEF_API void excef_undo(int browser_id);
EXCEF_API void excef_redo(int browser_id);

// ---- Drag and drop -------------------------------------------------------
//
// CEF in OSR mode does not interact with the platform's native drag-and-drop
// system. The host has to bridge: forward incoming OS drags to CEF as
// drag-target events, and forward CEF-initiated page drags out to the OS
// (or accept them internally) via the start-drag callback.
//
// DragOperationsMask values (subset of cef_drag_operations_mask_t):
#define EXCEF_DRAG_OP_NONE   0
#define EXCEF_DRAG_OP_COPY   1
#define EXCEF_DRAG_OP_LINK   2
#define EXCEF_DRAG_OP_GENERIC 4
#define EXCEF_DRAG_OP_PRIVATE 8
#define EXCEF_DRAG_OP_MOVE   16
#define EXCEF_DRAG_OP_DELETE 32
#define EXCEF_DRAG_OP_EVERY  0xFFFFFFFF

// Drag-target side: host calls these as the OS reports an external drag
// hovering / dropping over the WebView.
//
// `text`, `html`, `url`, and the `file_paths` array may be NULL or empty.
// `file_path_count` is the length of `file_paths`.
EXCEF_API void excef_drag_target_drag_enter(int browser_id,
                                            int x, int y,
                                            int modifiers,
                                            int allowed_ops,
                                            const char* text,
                                            const char* html,
                                            const char* url,
                                            const char** file_paths,
                                            int file_path_count);
EXCEF_API void excef_drag_target_drag_over(int browser_id,
                                            int x, int y,
                                            int modifiers,
                                            int allowed_ops);
EXCEF_API void excef_drag_target_drop(int browser_id,
                                      int x, int y,
                                      int modifiers);
EXCEF_API void excef_drag_target_drag_leave(int browser_id);

// Drag-source side: when the page initiates a drag, CEF calls this callback
// (set via excef_set_start_drag_callback) so the host can start an OS-level
// drag. Return 1 to indicate the host is handling it (host MUST eventually
// call excef_drag_source_ended_at + excef_drag_source_system_drag_ended);
// return 0 to fall back to internal-only DnD (the shim self-targets).
//
// The drag data is decomposed into individual fields. `link_url` / `text` /
// `html` / `file_names` are NULL/empty when not applicable. Do not retain
// the pointers past callback return — copy out anything you need.
typedef int (*excef_start_drag_cb_t)(int browser_id,
                                      int allowed_ops,
                                      int x, int y,
                                      const char* text,
                                      const char* html,
                                      const char* link_url,
                                      const char* link_title,
                                      const char** file_names,
                                      int file_name_count);
EXCEF_API void excef_set_start_drag_callback(excef_start_drag_cb_t cb);

// Host calls these once the OS-level drag finishes:
//   `op` is the operation that completed (EXCEF_DRAG_OP_*).
EXCEF_API void excef_drag_source_ended_at(int browser_id, int x, int y, int op);
EXCEF_API void excef_drag_source_system_drag_ended(int browser_id);

// ---- Input forwarding (OSR browsers) -------------------------------------
//
// Modifier flags — matches CEF's cef_event_flags_t subset.
#define EXCEF_MOD_NONE              0
#define EXCEF_MOD_CAPS_LOCK         (1 << 0)
#define EXCEF_MOD_SHIFT             (1 << 1)
#define EXCEF_MOD_CONTROL           (1 << 2)
#define EXCEF_MOD_ALT               (1 << 3)
#define EXCEF_MOD_LEFT_MOUSE        (1 << 4)
#define EXCEF_MOD_MIDDLE_MOUSE      (1 << 5)
#define EXCEF_MOD_RIGHT_MOUSE       (1 << 6)
#define EXCEF_MOD_COMMAND           (1 << 7)

// Mouse buttons — matches cef_mouse_button_type_t.
#define EXCEF_MBT_LEFT              0
#define EXCEF_MBT_MIDDLE            1
#define EXCEF_MBT_RIGHT             2

// Key event types — matches cef_key_event_type_t.
#define EXCEF_KEY_RAW_KEYDOWN       0
#define EXCEF_KEY_KEYDOWN           1
#define EXCEF_KEY_KEYUP             2
#define EXCEF_KEY_CHAR              3

EXCEF_API void excef_send_mouse_move(int browser_id, int x, int y,
                                     int modifiers, int mouse_leave);

EXCEF_API void excef_send_mouse_click(int browser_id, int x, int y,
                                      int button, int mouse_up,
                                      int click_count, int modifiers);

EXCEF_API void excef_send_mouse_wheel(int browser_id, int x, int y,
                                      int delta_x, int delta_y,
                                      int modifiers);

EXCEF_API void excef_send_key_event(int browser_id, int type,
                                    int windows_key_code, int native_key_code,
                                    int modifiers, int character,
                                    int unmodified_character,
                                    int is_system_key);

EXCEF_API void excef_set_browser_focus(int browser_id, int focus);

// ---- Navigation / lifecycle ----------------------------------------------

EXCEF_API void excef_load_url(int browser_id, const char* url);
EXCEF_API void excef_go_back(int browser_id);
EXCEF_API void excef_go_forward(int browser_id);
EXCEF_API void excef_reload(int browser_id, int ignore_cache);
EXCEF_API void excef_stop_load(int browser_id);
EXCEF_API void excef_close_browser(int browser_id, int force_close);
EXCEF_API void excef_was_hidden(int browser_id, int hidden);

// ---- JavaScript ----------------------------------------------------------

// Execute JS in the main frame. Fire-and-forget — no return value.
EXCEF_API void excef_execute_javascript(int browser_id,
                                        const char* code,
                                        const char* script_url);

// ---- DevTools ------------------------------------------------------------

EXCEF_API void excef_show_dev_tools(int browser_id);
EXCEF_API void excef_close_dev_tools(int browser_id);

// ---- Browser events ------------------------------------------------------
//
// Process-wide callbacks: register once; CEF dispatches to them for any
// browser. The C# host filters by browser_id.

typedef void (*excef_address_change_cb_t)(int browser_id, const char* url);
typedef void (*excef_title_change_cb_t)(int browser_id, const char* title);
typedef void (*excef_loading_state_cb_t)(int browser_id,
                                          int is_loading,
                                          int can_go_back,
                                          int can_go_forward);

EXCEF_API void excef_set_address_change_callback(excef_address_change_cb_t cb);
EXCEF_API void excef_set_title_change_callback(excef_title_change_cb_t cb);
EXCEF_API void excef_set_loading_state_callback(excef_loading_state_cb_t cb);

// ---- JS evaluation with result ------------------------------------------
//
// Async eval. The host generates a request id, calls excef_eval_javascript,
// and waits for the registered callback to fire with that request id and
// the JSON-serialized result (or an error message).

typedef void (*excef_eval_result_cb_t)(int browser_id,
                                        int request_id,
                                        int success,
                                        const char* result_json);

EXCEF_API void excef_set_eval_result_callback(excef_eval_result_cb_t cb);

// Returns 1 if scheduled, 0 if browser_id is unknown.
EXCEF_API int excef_eval_javascript(int browser_id, int request_id,
                                    const char* code);

// ---- Cookies -------------------------------------------------------------
//
// Async iteration. Callback fires once per cookie with done=0, then once
// more with done=1 (cookie fields ignored on the done call).

typedef void (*excef_cookie_visit_cb_t)(int request_id,
                                         int done,
                                         const char* name,
                                         const char* value,
                                         const char* domain,
                                         const char* path,
                                         int secure,
                                         int httponly);

EXCEF_API void excef_set_cookie_visit_callback(excef_cookie_visit_cb_t cb);

// Iterate cookies for a URL (or all if url is NULL/empty).
// Returns the request_id used (>=1) on success, 0 on failure.
EXCEF_API int excef_get_cookies(const char* url, int request_id);

// Set a cookie. Returns 1 if scheduled.
EXCEF_API int excef_set_cookie(const char* url,
                               const char* name,
                               const char* value,
                               const char* domain,
                               const char* path,
                               int secure,
                               int httponly);

// Delete cookies matching url + name (either may be empty).
EXCEF_API void excef_delete_cookies(const char* url, const char* name);

// ---- Browser-closed event ------------------------------------------------

typedef void (*excef_browser_closed_cb_t)(int browser_id);
EXCEF_API void excef_set_browser_closed_callback(excef_browser_closed_cb_t cb);

// ---- Cursor change event -------------------------------------------------
//
// `cursor_type` is a value from cef_cursor_type_t (CT_POINTER=0, CT_CROSS=1,
// CT_HAND=2, CT_IBEAM=3, CT_WAIT=4, CT_HELP=5, CT_EAST/NORTH/...RESIZE,
// CT_MOVE=29, CT_NOTALLOWED=38, CT_GRAB=41, CT_GRABBING=42, etc.). See
// CEF's include/internal/cef_types.h for the full list. Custom cursors
// (CSS `cursor: url(...)`) report CT_CUSTOM=45; the bitmap is not
// forwarded across the C ABI in this version.

typedef void (*excef_cursor_change_cb_t)(int browser_id, int cursor_type);
EXCEF_API void excef_set_cursor_change_callback(excef_cursor_change_cb_t cb);

// ---- Console-message event ----------------------------------------------
//
// CefDisplayHandler::OnConsoleMessage. Fires for every console.{log,info,
// warn,error,debug} call from the page (and for runtime warnings emitted
// by Chromium itself, e.g. CORS / deprecation). `level` is a value from
// cef_log_severity_t: 0=default, 1=verbose/debug, 2=info, 3=warning,
// 4=error, 5=fatal. `source` is the script URL (may be empty for inline
// scripts); `line` is 1-based.
typedef void (*excef_console_message_cb_t)(int browser_id,
                                             int level,
                                             const char* message,
                                             const char* source,
                                             int line);
EXCEF_API void excef_set_console_message_callback(excef_console_message_cb_t cb);

// ---- IME -----------------------------------------------------------------
//
// Forwards composition events to CEF. Avalonia IME integration uses these
// from a TextInputMethodClient implementation; minimum useful set:

EXCEF_API void excef_ime_set_composition(int browser_id,
                                          const char* text,
                                          int replacement_range_from,
                                          int replacement_range_length,
                                          int selection_range_from,
                                          int selection_range_length);

EXCEF_API void excef_ime_commit_text(int browser_id,
                                     const char* text,
                                     int replacement_range_from,
                                     int replacement_range_length,
                                     int relative_cursor_pos);

EXCEF_API void excef_ime_finish_composing(int browser_id, int keep_selection);
EXCEF_API void excef_ime_cancel(int browser_id);

// ---- PrintToPDF ----------------------------------------------------------
//
// Render the browser's current page as a PDF at `path`. Async — `callback`
// fires when complete with success=1 on success, 0 on failure.
//
// Advanced print settings (header/footer templates, margins, paper size)
// live in the optional extension header `exclr8cef_print.h`. Apps that
// don't need PDF customization can ignore that header and bind only to
// `excef_print_to_pdf` here.

typedef void (*excef_pdf_done_callback_t)(int browser_id, int success);

// Returns 1 if the request was scheduled, 0 if browser_id is unknown.
EXCEF_API int excef_print_to_pdf(int browser_id,
                                 const char* path,
                                 excef_pdf_done_callback_t callback);

#ifdef __cplusplus
}
#endif

#endif  // EXCLR8CEF_H_
