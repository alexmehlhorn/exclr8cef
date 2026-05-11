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

// ---- Per-frame Load events ----------------------------------------------
//
// CefLoadHandler. Fire once per frame; is_main_frame=1 lets hosts filter
// to top-level navigations. http_status_code is the actual HTTP status
// on LoadEnd (0 for non-HTTP loads / data: / file:). error_code on
// LoadError is from cef_errors.h (a negative integer; 0 = ERR_NONE).
typedef void (*excef_load_start_cb_t)(int browser_id, int is_main_frame, const char* url);
typedef void (*excef_load_end_cb_t)(int browser_id, int is_main_frame, const char* url, int http_status_code);
typedef void (*excef_load_error_cb_t)(int browser_id, int is_main_frame, int error_code, const char* error_text, const char* failed_url);
typedef void (*excef_loading_progress_cb_t)(int browser_id, double progress);

EXCEF_API void excef_set_load_start_callback(excef_load_start_cb_t cb);
EXCEF_API void excef_set_load_end_callback(excef_load_end_cb_t cb);
EXCEF_API void excef_set_load_error_callback(excef_load_error_cb_t cb);
EXCEF_API void excef_set_loading_progress_callback(excef_loading_progress_cb_t cb);

// ---- Display events (status / tooltip / favicon / fullscreen) -----------
//
// All CefDisplayHandler. Strings are non-null but may be empty.
// `first_url` on favicon is the highest-priority icon URL from the
// page's <link rel="icon" …> tags (we pass the first; hosts that want
// the full list can call back into the page via JS). `fullscreen` is 1
// when the page enters HTML5 fullscreen, 0 when it leaves.
typedef void (*excef_status_message_cb_t)(int browser_id, const char* value);
typedef void (*excef_tooltip_cb_t)(int browser_id, const char* text);
typedef void (*excef_favicon_cb_t)(int browser_id, const char* first_url);
typedef void (*excef_fullscreen_cb_t)(int browser_id, int fullscreen);

EXCEF_API void excef_set_status_message_callback(excef_status_message_cb_t cb);
EXCEF_API void excef_set_tooltip_callback(excef_tooltip_cb_t cb);
EXCEF_API void excef_set_favicon_callback(excef_favicon_cb_t cb);
EXCEF_API void excef_set_fullscreen_callback(excef_fullscreen_cb_t cb);

// Exit page-driven fullscreen. `will_cause_resize=1` lets Chromium know
// the host will resize the view in response, suppressing redundant layout
// flicker.
EXCEF_API void excef_exit_fullscreen(int browser_id, int will_cause_resize);

// ---- Browser-initialised event ------------------------------------------
//
// CefLifeSpanHandler::OnAfterCreated. Fires once per browser, on the CEF
// UI thread, after the underlying CefBrowser is fully constructed and
// available to host operations (frame access, clipboard, navigation, …).
// The browser id returned by excef_create_offscreen_browser is valid
// immediately, but ops that need the CefBrowser ref are no-ops until
// this fires.
typedef void (*excef_browser_initialized_cb_t)(int browser_id);
EXCEF_API void excef_set_browser_initialized_callback(excef_browser_initialized_cb_t cb);

// ---- Render-handler observation: scroll & auto-resize -------------------
//
// CefRenderHandler::OnScrollOffsetChanged fires whenever the page's
// scroll position changes (smooth-scroll updates included — many times
// per second during a flick). x/y are in CSS pixels.
typedef void (*excef_scroll_offset_cb_t)(int browser_id, double x, double y);
EXCEF_API void excef_set_scroll_offset_callback(excef_scroll_offset_cb_t cb);

// CefRenderHandler::OnAutoResize fires when the page content's natural
// size changes; only delivered if auto-resize is enabled on the host
// side. Width/height are in CSS pixels.
typedef void (*excef_auto_resize_cb_t)(int browser_id, int width, int height);
EXCEF_API void excef_set_auto_resize_callback(excef_auto_resize_cb_t cb);

// Toggle CefBrowserHost auto-resize. When enabled, Chromium re-measures
// the page after every layout and emits an OnAutoResize callback if the
// natural content size differs from the current view rect. Pass
// `enabled=0` to disable; min/max are in DIPs / CSS pixels (ignored when
// disabling).
EXCEF_API void excef_set_auto_resize_enabled(int browser_id, int enabled,
                                              int min_w, int min_h,
                                              int max_w, int max_h);

// Zoom availability: query CefBrowserHost::CanZoom. `command` matches
// cef_zoom_command_t — 0=ZOOM_OUT, 1=ZOOM_RESET, 2=ZOOM_IN. Returns 0/1.
// Useful for graying out zoom UI controls at min/max zoom.
EXCEF_API int excef_can_zoom(int browser_id, int command);

// ---- Deferred-response handlers ----------------------------------------
//
// Pattern: a CEF handler callback in the renderer's main process hands us
// a callback object (CefJSDialogCallback, CefFileDialogCallback, etc.). We
// stash the callback in a per-handler-type map keyed by a uint64_t token,
// fire the C# event with the token + payload, and return true to CEF
// (meaning "we'll respond async"). C# decides on its UI thread and calls
// the matching `excef_resolve_*` to invoke Continue / Cancel on the
// stored callback (the resolve hops to the CEF UI thread internally).
//
// Resolve calls on unknown tokens are no-ops — safe if C# resolves twice
// or after browser close. Pending callbacks for a browser are cancelled
// automatically when that browser closes.

// ---- JS dialog handler -------------------------------------------------
//
// CefJSDialogHandler::OnJSDialog + OnBeforeUnloadDialog. dialog_type:
//   0 = alert       (user_input ignored on resolve)
//   1 = confirm     (success only)
//   2 = prompt      (user_input is the typed text on success)
//   3 = onbeforeunload (success=1 means "leave page"; user_input ignored)
typedef void (*excef_js_dialog_cb_t)(
    int browser_id,
    unsigned long long token,
    int dialog_type,
    const char* message_text,
    const char* default_prompt_text);

EXCEF_API void excef_set_js_dialog_callback(excef_js_dialog_cb_t cb);

// Continue the pending JS dialog. `success` = 1 if the user accepted
// (clicked OK / Leave); `user_input` is the prompt text on accept
// (NULL/empty otherwise).
EXCEF_API void excef_resolve_js_dialog(unsigned long long token,
                                        int success,
                                        const char* user_input);

// ---- File dialog handler -----------------------------------------------
//
// CefDialogHandler::OnFileDialog. Fires when the page triggers a file
// chooser (<input type=file>, showOpenFilePicker, downloads with
// Save-As, etc.). `mode`:
//   0 = open single file
//   1 = open multiple files
//   2 = open folder
//   3 = save file
// `accept_filters` is newline-separated (each line is a MIME type
// like "image/*" or a glob like "*.txt"). Empty if no filter.
typedef void (*excef_file_dialog_cb_t)(
    int browser_id,
    unsigned long long token,
    int mode,
    const char* title,
    const char* default_file_path,
    const char* accept_filters);

EXCEF_API void excef_set_file_dialog_callback(excef_file_dialog_cb_t cb);

// Resolve. `paths` is newline-separated absolute paths (UTF-8). Pass
// NULL or empty string to cancel (the page sees an empty selection).
EXCEF_API void excef_resolve_file_dialog(unsigned long long token, const char* paths);

// ---- Context-menu handler ----------------------------------------------
//
// CefContextMenuHandler::RunContextMenu. Fires on right-click / long-press.
// The page-side `params` includes the click coordinates and selection /
// link / image info; we expose x,y here and serialize the model's command
// items as "<id>\t<label>" per line (separators come through as id=0 with
// empty label; submenus are flattened — first level only in v1).
//
// In OSR mode CEF cannot render the menu itself, so the host MUST render
// its own and call excef_resolve_context_menu with the chosen command id
// (or -1 to cancel). Without a host subscriber the menu is suppressed.
typedef void (*excef_context_menu_cb_t)(
    int browser_id,
    unsigned long long token,
    int x, int y,
    const char* items_joined);

EXCEF_API void excef_set_context_menu_callback(excef_context_menu_cb_t cb);

// Resolve with the chosen command id, or -1 to dismiss without action.
EXCEF_API void excef_resolve_context_menu(unsigned long long token, int command_id);

// ---- Download handler --------------------------------------------------
//
// CefDownloadHandler has two distinct hooks:
//
// 1. `OnBeforeDownload` — fires once at the start. Host must pick a
//    save path (or cancel). Standard deferred-response pattern:
//    download-starting callback fires, host resolves with path.
//
// 2. `OnDownloadUpdated` — fires repeatedly while the download is in
//    flight. Each invocation carries a *fresh* token; the host can
//    optionally call excef_download_action(token, ...) synchronously
//    inside the event handler to Cancel / Pause / Resume. Tokens are
//    invalidated after the handler returns.
typedef void (*excef_download_starting_cb_t)(
    int browser_id,
    unsigned long long token,
    int download_id,
    const char* url,
    const char* suggested_name,
    const char* mime_type,
    long long total_bytes);

EXCEF_API void excef_set_download_starting_callback(excef_download_starting_cb_t cb);

// Resolve. `path` = NULL/"" cancels the download. `show_dialog` = 1
// makes the OS show a save-as dialog (in addition to using `path`).
EXCEF_API void excef_resolve_download_starting(unsigned long long token,
                                                 const char* path,
                                                 int show_dialog);

// state: 0 = in-progress, 1 = complete, 2 = canceled.
// percent_complete may be -1 if total_bytes is unknown.
typedef void (*excef_download_progress_cb_t)(
    int browser_id,
    unsigned long long token,
    int download_id,
    int percent_complete,
    long long received_bytes,
    long long total_bytes,
    long long current_speed,
    int state,
    const char* full_path);

EXCEF_API void excef_set_download_progress_callback(excef_download_progress_cb_t cb);

// action: 0 = cancel, 1 = pause, 2 = resume. No-op on unknown tokens
// (the token is invalidated as soon as the event handler returns).
EXCEF_API void excef_download_action(unsigned long long token, int action);

// ---- Auth credentials handler ------------------------------------------
//
// CefRequestHandler::GetAuthCredentials. Fires when the server (or proxy)
// returns 401/407 with an HTTP-Basic, Digest, or NTLM challenge. Host
// supplies username + password via excef_resolve_auth, or cancels by
// passing NULL for both.
typedef void (*excef_auth_request_cb_t)(
    int browser_id,
    unsigned long long token,
    int is_proxy,
    const char* host,
    int port,
    const char* realm,
    const char* scheme);

EXCEF_API void excef_set_auth_request_callback(excef_auth_request_cb_t cb);

// Resolve. NULL/empty username = cancel (the request fails with 401/407).
EXCEF_API void excef_resolve_auth(unsigned long long token,
                                    const char* username,
                                    const char* password);

// ---- Find handler -------------------------------------------------------
//
// CefFindHandler::OnFindResult. Fires once or more per Find() call to
// report match count, ordinal of the active match, and whether this is
// the final update for the search session. `identifier` is a CEF-internal
// id tying results to a search.
typedef void (*excef_find_result_cb_t)(
    int browser_id,
    int identifier,
    int count,
    int active_match_ordinal,
    int final_update);

EXCEF_API void excef_set_find_result_callback(excef_find_result_cb_t cb);

// Begin (or update) an in-page search. `find_next` = 1 to jump to next
// match while keeping the previous search alive; 0 starts a new search.
// `forward` = direction; `match_case` = case sensitivity.
EXCEF_API void excef_find(int browser_id,
                           const char* search_text,
                           int forward,
                           int match_case,
                           int find_next);

// Stop the in-page search. `clear_selection` = 1 to also remove the
// orange highlight on the last match.
EXCEF_API void excef_stop_finding(int browser_id, int clear_selection);

// ---- Init settings ------------------------------------------------------
//
// A useful subset of CefSettings the host can configure before calling
// excef_initialize_offscreen / excef_initialize_external_pump / etc.
// All fields are optional:
//   - char* strings: NULL/empty = use CEF default
//   - int booleans: 0 = use CEF default
//   - log_severity: 0 = LOGSEVERITY_DEFAULT (which is Info in release builds)
//
// We intentionally do not expose fields the shim controls internally:
// browser_subprocess_path, framework_dir_path, multi_threaded_message_loop,
// external_message_pump, windowless_rendering_enabled.
typedef struct excef_init_settings {
    const char* cache_path;             // disk cache + cookies. NULL = in-memory.
    const char* root_cache_path;        // parent for browser caches; defaults to system app-data.
    const char* user_agent;             // full UA override (replaces Chromium's)
    const char* user_agent_product;     // product-token suffix ("MyApp/1.0"); leaves the rest
    const char* locale;                 // "en-US"; controls UI locale + accept-language fallback
    const char* accept_language_list;   // "en-US,en;q=0.9"; overrides locale-derived default
    const char* log_file;               // NULL/empty = stderr
    const char* javascript_flags;       // V8 flags, e.g. "--max-old-space-size=512"
    int log_severity;                   // cef_log_severity_t
    int persist_session_cookies;        // 0/1; requires cache_path
    int remote_debugging_port;          // 0 = disabled, else 1024..65535
} excef_init_settings;

// Stash settings to apply on the next init call. Pass NULL to clear back
// to defaults. The struct is copied; pointers don't need to outlive the call.
EXCEF_API void excef_set_init_settings(const excef_init_settings* settings);

// ---- Render-process termination ----------------------------------------
//
// CefRequestHandler::OnRenderProcessTerminated. The renderer subprocess
// hosting this browser's page died. `status` is from
// cef_termination_status_t:
//   0 = abnormal termination (generic non-zero exit)
//   1 = process was killed (SIGTERM / TerminateProcess)
//   2 = process crashed (SIGSEGV / access violation)
//   3 = out of memory
//   4 = launch failed (subprocess couldn't start)
//   5 = integrity failure (CEF integrity check failed)
// The browser's OSR paint buffer freezes after termination; hosts
// typically respond by calling Reload() on the affected browser.
typedef void (*excef_render_process_gone_cb_t)(int browser_id,
                                                 int status,
                                                 int error_code,
                                                 const char* error_string);
EXCEF_API void excef_set_render_process_gone_callback(excef_render_process_gone_cb_t cb);

// ---- Custom scheme + resource handler ----------------------------------
//
// Register a URL scheme (e.g. "app") that routes through a host-side
// callback. The host returns full response bytes for each request — no
// streaming in v1, just a single buffer per response.
//
// Workflow:
//   1. Host calls excef_register_custom_scheme(...) BEFORE init for every
//      scheme it wants to handle. The scheme is registered with Chromium
//      and (if `register_factory` is non-zero) hooked to our internal
//      resource-handler factory.
//   2. Host calls excef_set_scheme_request_callback() to receive requests.
//   3. When a page navigates to `app://...` or fetches `app://...`, the
//      callback fires with a fresh token + the URL + the HTTP method.
//   4. Host calls excef_resolve_scheme_request(token, ...) with the
//      response (status code, mime type, headers, body bytes).
//
// All buffers are copied; callers do not need to keep them alive past the
// resolve call. To return a 404, pass status_code=404 with empty body.

// is_standard:        1 = treat URLs as standard (have host, path, etc.);
//                     0 = opaque (everything after "scheme:" is the path)
// is_local:           1 = local (file-like) — blocks XHR from non-local
// is_display_isolated:1 = can only be displayed in <iframe> from same origin
// is_secure:          1 = treated as "secure context" (enables crypto APIs)
// is_cors_enabled:    1 = allow CORS requests
// is_csp_bypassing:   1 = exempt from Content-Security-Policy
//
// Returns 0 on success, non-zero on failure.
EXCEF_API int excef_register_custom_scheme(const char* scheme_name,
                                            int is_standard,
                                            int is_local,
                                            int is_display_isolated,
                                            int is_secure,
                                            int is_cors_enabled,
                                            int is_csp_bypassing);

typedef void (*excef_scheme_request_cb_t)(
    int browser_id,
    unsigned long long token,
    const char* url,
    const char* method);

EXCEF_API void excef_set_scheme_request_callback(excef_scheme_request_cb_t cb);

// Resolve a pending scheme request with the response body + metadata.
// `mime_type` is optional (NULL/empty → CEF infers from the URL).
// `body` may be NULL when `body_length == 0`. Multiple resolves on the
// same token are no-ops (idempotent).
EXCEF_API void excef_resolve_scheme_request(
    unsigned long long token,
    int status_code,
    const char* status_text,
    const char* mime_type,
    const unsigned char* body,
    int body_length);

// ---- Resource-request handler (lite) -----------------------------------
//
// Fires once per outgoing network request — main-frame navigations,
// sub-resources (CSS / JS / images / favicons), XHR/fetch, web workers.
// Host can inspect the URL / method / current headers and either let
// the request proceed (optionally replacing the header set) or cancel.
//
// `resource_type` mirrors cef_resource_type_t (0=main_frame, 1=sub_frame,
// 2=stylesheet, 3=script, 4=image, 5=font, 6=sub_resource, 7=object,
// 8=media, 9=worker, 12=favicon, 13=xhr, 15=service_worker, ...).
//
// `current_headers` is the request's current header set serialized as
// `Name: Value\n` lines (no trailing newline).
typedef void (*excef_resource_request_cb_t)(
    int browser_id,
    unsigned long long token,
    const char* url,
    const char* method,
    int resource_type,
    const char* current_headers);

EXCEF_API void excef_set_resource_request_callback(excef_resource_request_cb_t cb);

// Resolve. `action` = 0 → continue, 1 → cancel. If `action=0` and
// `new_headers` is non-NULL, the request's entire header set is REPLACED
// with the supplied list (same `Name: Value\n` format). Pass NULL or
// empty to keep existing headers untouched.
EXCEF_API void excef_resolve_resource_request(
    unsigned long long token,
    int action,
    const char* new_headers);

// ---- OSR popup support --------------------------------------------------
//
// Page popups (<select> dropdowns, autocomplete, etc.) paint into a
// *separate* buffer and at a *different* position from the main view. CEF
// signals popup lifecycle through three callbacks:
//
//   excef_popup_show_cb_t   — toggle visibility (1 = show, 0 = hide)
//   excef_popup_size_cb_t   — popup rect in DIP / CSS pixels, relative to
//                             the browser's main view origin
//   excef_popup_paint_cb_t  — popup bitmap (BGRA8888, physical pixels;
//                             same shape as the regular paint callback)
//
// Hosts that don't wire these callbacks will see popups silently dropped
// (the previous behaviour). With them wired and rendered as an overlay
// at the popup rect, <select> dropdowns and HTML popups Just Work.
typedef void (*excef_popup_show_cb_t)(int browser_id, int show);
typedef void (*excef_popup_size_cb_t)(int browser_id, int x, int y, int width, int height);
typedef void (*excef_popup_paint_cb_t)(int browser_id, const void* buffer, int width, int height);

EXCEF_API void excef_set_popup_show_callback(excef_popup_show_cb_t cb);
EXCEF_API void excef_set_popup_size_callback(excef_popup_size_cb_t cb);
EXCEF_API void excef_set_popup_paint_callback(excef_popup_paint_cb_t cb);

// ---- JavaScript bridge (lite) -------------------------------------------
//
// Installs a global function `window.exclr8cef.invoke(method, argsJson)`
// in every V8 context created in the renderer process. Calls from JS
// fire a CefProcessMessage to the browser process; we surface that to
// the host via this callback. Fire-and-forget for v1 — return values
// to JS (Promises) are a v2 concern.
//
// `args_json` is whatever the JS caller passed as the second argument
// (typically a JSON.stringify(...) of an args object). Host parses it
// however it likes.
typedef void (*excef_js_invoke_cb_t)(int browser_id,
                                       const char* method,
                                       const char* args_json);
EXCEF_API void excef_set_js_invoke_callback(excef_js_invoke_cb_t cb);

// ---- Request context (per-browser isolation) ---------------------------
//
// CefRequestContext lets browsers share or partition cookies, cache,
// localStorage, and other per-origin state. A browser created in context
// A has a completely separate cookie jar / cache / storage from one in
// context B — the pattern behind "incognito tabs", "multi-profile",
// "container" extensions, etc.
//
// Usage:
//   1. Host calls excef_create_request_context(cache_path) to get a
//      context handle (>= 1). Pass NULL/empty cache_path for an
//      in-memory (incognito-style) context.
//   2. Host passes the handle to excef_create_offscreen_browser_in_context
//      (or 0 for the global context, equivalent to the existing
//      excef_create_offscreen_browser).
//   3. Host calls excef_release_request_context when the context is no
//      longer needed — outstanding browsers keep it alive; once the last
//      browser closes, CEF tears the context down.
//
// Returns a positive handle on success, 0 on failure. The handle is
// stable for the process lifetime; reuse it across many browsers if you
// want them to share state.
EXCEF_API int excef_create_request_context(const char* cache_path);

// Drop our refcount on the context. The CEF instance is still kept alive
// by any in-flight browsers using it.
EXCEF_API void excef_release_request_context(int context_handle);

// Variant of excef_create_offscreen_browser that creates the browser in
// a specific request context. Pass context_handle=0 to use the global
// default context (equivalent to excef_create_offscreen_browser).
EXCEF_API int excef_create_offscreen_browser_in_context(
    int width, int height, float device_scale_factor,
    const char* url,
    excef_paint_callback_t paint,
    int context_handle);


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
