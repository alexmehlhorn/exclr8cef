using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Static facade for the Exclr8CEF lifecycle. Use from any .NET host —
/// console, WPF, MAUI, ASP.NET, services. Avalonia hosts can use this
/// directly or compose the <c>WebView</c> control from
/// <c>Exclr8Cef.WebView</c>.
/// </summary>
public static class Cef
{
    private const int BufferSize = 64;

    /// <summary>
    /// Returns the linked shim, CEF, and Chromium versions. Safe to call
    /// before <see cref="Initialize"/>; this only reads compile-time constants
    /// from the shim.
    /// </summary>
    public static CefVersions GetVersions()
    {
        var raw = new excef_versions();
        unsafe
        {
            Excef.excef_get_versions(&raw);
            return new CefVersions(
                ReadCString((sbyte*)&raw.shim_version, BufferSize),
                ReadCString((sbyte*)&raw.cef_version, BufferSize),
                ReadCString((sbyte*)&raw.chromium_version, BufferSize));
        }
    }

    /// <summary>
    /// On Windows and Linux the same binary is reused as the CEF subprocess
    /// (renderer / GPU / utility / etc.); CEF re-invokes it with
    /// <c>--type=*</c>. The host main() must call this first; if it returns
    /// >= 0 the process is a CEF subprocess and should exit immediately
    /// with that code, otherwise (-1) it's the main process and continues.
    ///
    /// On macOS the subprocess lives in a separate <c>Helper.app</c> bundle
    /// so this returns -1 immediately and is effectively a no-op (the
    /// helper bundle calls into the shim directly).
    /// </summary>
    public static int ExecuteProcess(string[]? args = null)
    {
        if (OperatingSystem.IsMacOS()) return -1;
        args ??= Environment.GetCommandLineArgs();
        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            try
            {
                return Excef.excef_execute_process(argc, argv);
            }
            finally
            {
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Initialize CEF with its own internal message loop. Call once, then
    /// <see cref="RunMessageLoop"/>. Suited for console / non-UI hosts that
    /// don't already have a platform message loop.
    /// </summary>
    public static void Initialize(string[]? args = null, string? subprocessPath = null)
    {
        args ??= Environment.GetCommandLineArgs();

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                int rc = Excef.excef_initialize(argc, argv, subprocPtr);
                if (rc != 0)
                {
                    throw new InvalidOperationException(
                        $"excef_initialize failed with code {rc}");
                }
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Initialize CEF with an external message pump. Use from UI hosts that
    /// already own the platform message loop (Avalonia, WPF). The host must
    /// call <see cref="DoMessageLoopWork"/> from its UI thread whenever the
    /// <paramref name="schedulePumpWork"/> callback fires.
    /// </summary>
    public static void InitializeExternalPump(
        string[]? args,
        string? subprocessPath,
        Action<long> schedulePumpWork)
    {
        ArgumentNullException.ThrowIfNull(schedulePumpWork);
        args ??= Environment.GetCommandLineArgs();

        // Hold the callback alive — UnmanagedCallersOnly trampoline reads it
        // from a static field.
        s_scheduleCallback = schedulePumpWork;

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                delegate* unmanaged[Cdecl]<long, void> trampoline = &SchedulePumpWorkTrampoline;
                int rc = Excef.excef_initialize_external_pump(argc, argv, subprocPtr, trampoline);
                if (rc != 0)
                {
                    throw new InvalidOperationException(
                        $"excef_initialize_external_pump failed with code {rc}");
                }
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Drive CEF's pending work. Host should call this from its UI thread
    /// when the schedule callback's delay has elapsed.
    /// </summary>
    public static void DoMessageLoopWork() => Excef.excef_do_message_loop_work();

    /// <summary>
    /// Open a top-level Chromium browser window pointed at <paramref name="url"/>.
    /// Safe to call before or after <see cref="Initialize"/>: URLs requested
    /// before init complete are queued and opened automatically when CEF is
    /// ready.
    /// </summary>
    public static void CreateBrowser(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        unsafe
        {
            sbyte* ptr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                int rc = Excef.excef_create_browser(ptr);
                if (rc != 0)
                {
                    throw new InvalidOperationException(
                        $"excef_create_browser failed with code {rc}");
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)ptr);
            }
        }
    }

    /// <summary>
    /// Create a native host view containing a CEF browser. Returns a handle
    /// to an <c>NSView</c> on macOS (other platforms TBD). Caller takes
    /// ownership; the UI framework hosting the handle is responsible for
    /// release/positioning.
    /// </summary>
    public static IntPtr CreateBrowserView(int width, int height, string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        unsafe
        {
            sbyte* ptr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                void* viewPtr = Excef.excef_create_browser_view(width, height, ptr);
                return (IntPtr)viewPtr;
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)ptr);
            }
        }
    }

    /// <summary>
    /// Initialize CEF in off-screen-rendering mode with an external message
    /// pump. Use from UI hosts that want to embed Chromium without giving
    /// CEF a native window — typical for Avalonia integration. Combine with
    /// <see cref="CreateOffscreenBrowser"/> and a <c>WriteableBitmap</c>.
    /// </summary>
    public static void InitializeForOsr(
        string[]? args,
        string? subprocessPath,
        Action<long> schedulePumpWork)
    {
        ArgumentNullException.ThrowIfNull(schedulePumpWork);
        args ??= Environment.GetCommandLineArgs();
        s_scheduleCallback = schedulePumpWork;
        RegisterEventCallbacks();

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                delegate* unmanaged[Cdecl]<long, void> trampoline = &SchedulePumpWorkTrampoline;
                int rc = Excef.excef_initialize_offscreen(argc, argv, subprocPtr, trampoline);
                if (rc != 0)
                {
                    throw new InvalidOperationException(
                        $"excef_initialize_offscreen failed with code {rc}");
                }
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Paint callback for off-screen browsers. <paramref name="buffer"/> is
    /// BGRA8888 (32-bit, top-left origin, stride = width × 4) and is only
    /// valid for the duration of the call — copy what you need.
    /// </summary>
    public delegate void PaintHandler(int browserId, IntPtr buffer, int width, int height);

    /// <summary>
    /// Create an off-screen browser whose view rect is <paramref name="width"/> ×
    /// <paramref name="height"/> DIPs. <paramref name="deviceScaleFactor"/> controls
    /// HiDPI rendering: CEF allocates a paint buffer of (width × scale) ×
    /// (height × scale) physical pixels, while the page is laid out at the DIP
    /// size. Pass <c>1.0f</c> for non-HiDPI hosts.
    ///
    /// Returns a browser id (≥ 1) on success, 0 on failure. The
    /// <paramref name="onPaint"/> handler is invoked whenever CEF has new pixels;
    /// the (width, height) reported there are in physical pixels.
    /// </summary>
    public static int CreateOffscreenBrowser(int width, int height, float deviceScaleFactor, string url, PaintHandler onPaint)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(onPaint);

        unsafe
        {
            sbyte* urlPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                delegate* unmanaged[Cdecl]<int, void*, int, int, void> trampoline = &PaintTrampoline;
                int id = Excef.excef_create_offscreen_browser(width, height, deviceScaleFactor, urlPtr, trampoline);
                if (id > 0) s_paintHandlers[id] = onPaint;
                return id;
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
    }

    /// <summary>Notify CEF that an off-screen browser was resized (DIP size).</summary>
    public static void ResizeOffscreenBrowser(int browserId, int width, int height)
        => Excef.excef_resize_offscreen_browser(browserId, width, height);

    /// <summary>
    /// Update the device scale factor for an off-screen browser, e.g. when the
    /// host control is dragged across monitors with different DPI. CEF will
    /// re-layout and emit a fresh paint at the new physical-pixel size.
    /// </summary>
    public static void SetDeviceScaleFactor(int browserId, float scale)
        => Excef.excef_set_device_scale_factor(browserId, scale);

    /// <summary>
    /// Set the page zoom level. <c>0.0</c> = 100% (default). Each <c>+1.0</c>
    /// step is ~120% of the previous (CEF/Chromium convention:
    /// <c>percentage = 100 × pow(1.2, level)</c>).
    /// </summary>
    public static void SetZoomLevel(int browserId, double level)
        => Excef.excef_set_zoom_level(browserId, level);

    /// <summary>Get the current zoom level. <c>0.0</c> = 100%.</summary>
    public static double GetZoomLevel(int browserId)
        => Excef.excef_get_zoom_level(browserId);

    // ---- Clipboard / editing primitives -----------------------------------
    // Operate on the browser's focused frame. CEF in OSR mode does not
    // auto-execute these from keyboard accelerators — the host calls them
    // when Cmd/Ctrl + C / V / X / A / Z / Y is pressed.

    public static void Copy(int browserId)      => Excef.excef_copy(browserId);
    public static void Paste(int browserId)     => Excef.excef_paste(browserId);
    public static void Cut(int browserId)       => Excef.excef_cut(browserId);
    public static void SelectAll(int browserId) => Excef.excef_select_all(browserId);
    public static void Undo(int browserId)      => Excef.excef_undo(browserId);
    public static void Redo(int browserId)      => Excef.excef_redo(browserId);

    // ---- Input forwarding -------------------------------------------------

    [Flags]
    public enum CefModifiers : uint
    {
        None = 0,
        CapsLock = 1u << 0,
        Shift = 1u << 1,
        Control = 1u << 2,
        Alt = 1u << 3,
        LeftMouseButton = 1u << 4,
        MiddleMouseButton = 1u << 5,
        RightMouseButton = 1u << 6,
        Command = 1u << 7,
    }

    public enum CefMouseButton
    {
        Left = 0,
        Middle = 1,
        Right = 2,
    }

    public enum CefKeyEventType
    {
        RawKeyDown = 0,
        KeyDown = 1,
        KeyUp = 2,
        Char = 3,
    }

    public static void SendMouseMove(int browserId, int x, int y, CefModifiers modifiers, bool mouseLeave)
        => Excef.excef_send_mouse_move(browserId, x, y, (int)modifiers, mouseLeave ? 1 : 0);

    public static void SendMouseClick(int browserId, int x, int y, CefMouseButton button, bool mouseUp, int clickCount, CefModifiers modifiers)
        => Excef.excef_send_mouse_click(browserId, x, y, (int)button, mouseUp ? 1 : 0, clickCount, (int)modifiers);

    public static void SendMouseWheel(int browserId, int x, int y, int deltaX, int deltaY, CefModifiers modifiers)
        => Excef.excef_send_mouse_wheel(browserId, x, y, deltaX, deltaY, (int)modifiers);

    public static void SendKeyEvent(
        int browserId,
        CefKeyEventType type,
        int windowsKeyCode,
        int nativeKeyCode,
        CefModifiers modifiers,
        char character,
        char unmodifiedCharacter,
        bool isSystemKey)
        => Excef.excef_send_key_event(
            browserId, (int)type,
            windowsKeyCode, nativeKeyCode,
            (int)modifiers,
            character, unmodifiedCharacter,
            isSystemKey ? 1 : 0);

    public static void SetBrowserFocus(int browserId, bool focus)
        => Excef.excef_set_browser_focus(browserId, focus ? 1 : 0);

    // ---- Navigation -------------------------------------------------------

    public static void LoadUrl(int browserId, string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try { Excef.excef_load_url(browserId, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public static void GoBack(int browserId) => Excef.excef_go_back(browserId);
    public static void GoForward(int browserId) => Excef.excef_go_forward(browserId);
    public static void Reload(int browserId, bool ignoreCache = false)
        => Excef.excef_reload(browserId, ignoreCache ? 1 : 0);
    public static void StopLoad(int browserId) => Excef.excef_stop_load(browserId);

    public static void CloseBrowser(int browserId, bool forceClose = false)
        => Excef.excef_close_browser(browserId, forceClose ? 1 : 0);

    public static void WasHidden(int browserId, bool hidden)
        => Excef.excef_was_hidden(browserId, hidden ? 1 : 0);

    // ---- JavaScript -------------------------------------------------------

    public static void ExecuteJavaScript(int browserId, string code, string? scriptUrl = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            sbyte* urlPtr = scriptUrl is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(scriptUrl);
            try { Excef.excef_execute_javascript(browserId, codePtr, urlPtr); }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
    }

    // ---- DevTools ---------------------------------------------------------

    public static void ShowDevTools(int browserId) => Excef.excef_show_dev_tools(browserId);
    public static void CloseDevTools(int browserId) => Excef.excef_close_dev_tools(browserId);

    // ---- Browser events ---------------------------------------------------

    public sealed class BrowserStringEventArgs : EventArgs
    {
        public required int BrowserId { get; init; }
        public required string Value { get; init; }
    }

    public sealed class LoadingStateEventArgs : EventArgs
    {
        public required int BrowserId { get; init; }
        public required bool IsLoading { get; init; }
        public required bool CanGoBack { get; init; }
        public required bool CanGoForward { get; init; }
    }

    private static EventHandler<BrowserStringEventArgs>? s_addressChanged;
    private static EventHandler<BrowserStringEventArgs>? s_titleChanged;
    private static EventHandler<LoadingStateEventArgs>? s_loadingStateChanged;
    private static EventHandler<int>? s_browserClosed;
    private static EventHandler<CursorChangedEventArgs>? s_cursorChanged;

    /// <summary>
    /// CEF's <c>cef_cursor_type_t</c>. Maps directly to the values reported by
    /// CefDisplayHandler::OnCursorChange.
    /// </summary>
    public enum CefCursorType
    {
        Pointer = 0,
        Cross = 1,
        Hand = 2,
        IBeam = 3,
        Wait = 4,
        Help = 5,
        EastResize = 6,
        NorthResize = 7,
        NorthEastResize = 8,
        NorthWestResize = 9,
        SouthResize = 10,
        SouthEastResize = 11,
        SouthWestResize = 12,
        WestResize = 13,
        NorthSouthResize = 14,
        EastWestResize = 15,
        NorthEastSouthWestResize = 16,
        NorthWestSouthEastResize = 17,
        ColumnResize = 18,
        RowResize = 19,
        MiddlePanning = 20,
        EastPanning = 21,
        NorthPanning = 22,
        NorthEastPanning = 23,
        NorthWestPanning = 24,
        SouthPanning = 25,
        SouthEastPanning = 26,
        SouthWestPanning = 27,
        WestPanning = 28,
        Move = 29,
        VerticalText = 30,
        Cell = 31,
        ContextMenu = 32,
        Alias = 33,
        Progress = 34,
        NoDrop = 35,
        Copy = 36,
        None = 37,
        NotAllowed = 38,
        ZoomIn = 39,
        ZoomOut = 40,
        Grab = 41,
        Grabbing = 42,
        MiddlePanningVertical = 43,
        MiddlePanningHorizontal = 44,
        Custom = 45,
        DndNone = 46,
        DndMove = 47,
        DndCopy = 48,
        DndLink = 49,
    }

    public sealed class CursorChangedEventArgs : EventArgs
    {
        public required int BrowserId { get; init; }
        public required CefCursorType Type { get; init; }
    }

    /// <summary>Fires when a browser's main-frame URL changes.</summary>
    public static event EventHandler<BrowserStringEventArgs>? AddressChanged
    {
        add { RegisterEventCallbacks(); s_addressChanged += value; }
        remove { s_addressChanged -= value; }
    }

    /// <summary>Fires when a browser's page title changes.</summary>
    public static event EventHandler<BrowserStringEventArgs>? TitleChanged
    {
        add { RegisterEventCallbacks(); s_titleChanged += value; }
        remove { s_titleChanged -= value; }
    }

    /// <summary>Fires when loading state (isLoading / canGoBack / canGoForward) changes.</summary>
    public static event EventHandler<LoadingStateEventArgs>? LoadingStateChanged
    {
        add { RegisterEventCallbacks(); s_loadingStateChanged += value; }
        remove { s_loadingStateChanged -= value; }
    }

    /// <summary>Fires after a browser has fully closed (CefLifeSpanHandler::OnBeforeClose).</summary>
    public static event EventHandler<int>? BrowserClosed
    {
        add { RegisterEventCallbacks(); s_browserClosed += value; }
        remove { s_browserClosed -= value; }
    }

    /// <summary>Fires when the page requests a different cursor type (CSS <c>cursor:</c>).</summary>
    public static event EventHandler<CursorChangedEventArgs>? CursorChanged
    {
        add { RegisterEventCallbacks(); s_cursorChanged += value; }
        remove { s_cursorChanged -= value; }
    }

    // ---- JavaScript with result -------------------------------------------

    /// <summary>
    /// Evaluate JavaScript in the browser's main frame and return the result
    /// as a JSON string. Throws on JS error or unknown browser id.
    /// </summary>
    public static Task<string> EvaluateJavaScriptAsync(int browserId, string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        int reqId = Interlocked.Increment(ref s_nextEvalRequestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_evalRequests[reqId] = tcs;

        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            try
            {
                int scheduled = Excef.excef_eval_javascript(browserId, reqId, codePtr);
                if (scheduled == 0)
                {
                    s_evalRequests.TryRemove(reqId, out _);
                    tcs.TrySetException(new InvalidOperationException("eval not scheduled (unknown browser id)"));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
            }
        }
        return tcs.Task;
    }

    // ---- Cookies ----------------------------------------------------------

    public sealed record CookieInfo(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool Secure,
        bool HttpOnly);

    /// <summary>
    /// Get cookies. Pass an empty/null <paramref name="url"/> to get all cookies.
    /// </summary>
    public static Task<List<CookieInfo>> GetCookiesAsync(string? url = null)
    {
        int reqId = Interlocked.Increment(ref s_nextCookieRequestId);
        var tcs = new TaskCompletionSource<List<CookieInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_cookieRequests[reqId] = (new List<CookieInfo>(), tcs);

        unsafe
        {
            sbyte* urlPtr = url is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                int got = Excef.excef_get_cookies(urlPtr, reqId);
                if (got == 0)
                {
                    s_cookieRequests.TryRemove(reqId, out _);
                    tcs.TrySetException(new InvalidOperationException("CefCookieManager unavailable"));
                }
            }
            finally
            {
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
        return tcs.Task;
    }

    public static bool SetCookie(string url, string name, string value,
                                  string? domain = null, string? path = null,
                                  bool secure = false, bool httpOnly = false)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(name);
        unsafe
        {
            sbyte* urlPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* namePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            sbyte* valuePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(value ?? "");
            sbyte* domainPtr = domain is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(domain);
            sbyte* pathPtr = path is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                return Excef.excef_set_cookie(urlPtr, namePtr, valuePtr, domainPtr, pathPtr,
                                              secure ? 1 : 0, httpOnly ? 1 : 0) != 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)urlPtr);
                Marshal.FreeCoTaskMem((IntPtr)namePtr);
                Marshal.FreeCoTaskMem((IntPtr)valuePtr);
                if (domainPtr is not null) Marshal.FreeCoTaskMem((IntPtr)domainPtr);
                if (pathPtr is not null) Marshal.FreeCoTaskMem((IntPtr)pathPtr);
            }
        }
    }

    public static void DeleteCookies(string? url = null, string? name = null)
    {
        unsafe
        {
            sbyte* urlPtr = url is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            sbyte* namePtr = name is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(name);
            try { Excef.excef_delete_cookies(urlPtr, namePtr); }
            finally
            {
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
                if (namePtr is not null) Marshal.FreeCoTaskMem((IntPtr)namePtr);
            }
        }
    }

    // ---- IME --------------------------------------------------------------

    public static void ImeSetComposition(int browserId, string text,
                                          int replacementRangeStart = 0,
                                          int replacementRangeLength = 0,
                                          int selectionStart = 0,
                                          int selectionLength = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_set_composition(browserId, p,
                    replacementRangeStart, replacementRangeLength,
                    selectionStart, selectionLength);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public static void ImeCommitText(int browserId, string text,
                                      int replacementRangeStart = 0,
                                      int replacementRangeLength = 0,
                                      int relativeCursorPos = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_commit_text(browserId, p,
                    replacementRangeStart, replacementRangeLength,
                    relativeCursorPos);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public static void ImeFinishComposing(int browserId, bool keepSelection = false)
        => Excef.excef_ime_finish_composing(browserId, keepSelection ? 1 : 0);

    public static void ImeCancel(int browserId)
        => Excef.excef_ime_cancel(browserId);

    private static bool s_eventsRegistered;
    private static readonly object s_eventsLock = new();

    /// <summary>
    /// Register the native event callbacks. Idempotent. Called automatically
    /// on first subscription to <see cref="AddressChanged"/>, <see cref="TitleChanged"/>,
    /// <see cref="LoadingStateChanged"/>, or <see cref="BrowserClosed"/>, and
    /// also from <see cref="InitializeForOsr"/>. Exposed for hosts that want
    /// to opt in eagerly before any subscription happens.
    /// </summary>
    public static void RegisterEventCallbacks()
    {
        lock (s_eventsLock)
        {
            if (s_eventsRegistered) return;
            unsafe
            {
                Excef.excef_set_address_change_callback(&AddressChangeTrampoline);
                Excef.excef_set_title_change_callback(&TitleChangeTrampoline);
                Excef.excef_set_loading_state_callback(&LoadingStateTrampoline);
                Excef.excef_set_browser_closed_callback(&BrowserClosedTrampoline);
                Excef.excef_set_eval_result_callback(&EvalResultTrampoline);
                Excef.excef_set_cookie_visit_callback(&CookieVisitTrampoline);
                Excef.excef_set_cursor_change_callback(&CursorChangeTrampoline);
            }
            s_eventsRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AddressChangeTrampoline(int browserId, sbyte* url)
    {
        var s = Marshal.PtrToStringUTF8((IntPtr)url) ?? "";
        s_addressChanged?.Invoke(null, new BrowserStringEventArgs { BrowserId = browserId, Value = s });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TitleChangeTrampoline(int browserId, sbyte* title)
    {
        var s = Marshal.PtrToStringUTF8((IntPtr)title) ?? "";
        s_titleChanged?.Invoke(null, new BrowserStringEventArgs { BrowserId = browserId, Value = s });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LoadingStateTrampoline(int browserId, int isLoading, int canGoBack, int canGoForward)
    {
        s_loadingStateChanged?.Invoke(null, new LoadingStateEventArgs
        {
            BrowserId = browserId,
            IsLoading = isLoading != 0,
            CanGoBack = canGoBack != 0,
            CanGoForward = canGoForward != 0,
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CursorChangeTrampoline(int browserId, int cursorType)
    {
        s_cursorChanged?.Invoke(null, new CursorChangedEventArgs
        {
            BrowserId = browserId,
            Type = (CefCursorType)cursorType,
        });
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BrowserClosedTrampoline(int browserId)
    {
        s_paintHandlers.TryRemove(browserId, out _);
        if (s_pdfCallbacks.TryRemove(browserId, out var pdfQueue))
        {
            // Fail any pending PDF callbacks so callers' Tasks complete instead of hanging.
            Action<int, int>[] pending;
            lock (pdfQueue) { pending = pdfQueue.ToArray(); pdfQueue.Clear(); }
            foreach (var cb in pending) cb(browserId, 0);
        }
        s_browserClosed?.Invoke(null, browserId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void EvalResultTrampoline(int browserId, int requestId, int success, sbyte* payload)
    {
        if (!s_evalRequests.TryRemove(requestId, out var tcs)) return;
        var s = Marshal.PtrToStringUTF8((IntPtr)payload) ?? "";
        if (success != 0) tcs.TrySetResult(s);
        else tcs.TrySetException(new InvalidOperationException(string.IsNullOrEmpty(s) ? "JS eval failed" : s));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void CookieVisitTrampoline(int requestId, int done,
        sbyte* name, sbyte* value, sbyte* domain, sbyte* path,
        int secure, int httpOnly)
    {
        if (done != 0)
        {
            if (s_cookieRequests.TryRemove(requestId, out var entry))
            {
                entry.Tcs.TrySetResult(entry.List);
            }
            return;
        }
        if (s_cookieRequests.TryGetValue(requestId, out var e))
        {
            e.List.Add(new CookieInfo(
                Marshal.PtrToStringUTF8((IntPtr)name) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)value) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)domain) ?? "",
                Marshal.PtrToStringUTF8((IntPtr)path) ?? "",
                secure != 0,
                httpOnly != 0));
        }
    }

    /// <summary>
    /// Render the browser's current page as a PDF at <paramref name="path"/>.
    /// Returns a task that completes with <c>true</c> on success.
    /// </summary>
    public static Task<bool> PrintToPdfAsync(int browserId, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> cb = (_, ok) => tcs.TrySetResult(ok != 0);
        var queue = s_pdfCallbacks.GetOrAdd(browserId, _ => new List<Action<int, int>>());

        unsafe
        {
            sbyte* pathPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                delegate* unmanaged[Cdecl]<int, int, void> trampoline = &PdfDoneTrampoline;
                lock (queue)
                {
                    queue.Add(cb);
                    int scheduled = Excef.excef_print_to_pdf(browserId, pathPtr, trampoline);
                    if (scheduled == 0)
                    {
                        // Roll back the just-added callback so it doesn't poison FIFO order
                        // for subsequent successful prints.
                        queue.RemoveAt(queue.Count - 1);
                        tcs.TrySetResult(false);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)pathPtr);
            }
        }
        return tcs.Task;
    }

    /// <summary>
    /// Run CEF's internal message loop. Blocks until all browsers close or
    /// <see cref="QuitMessageLoop"/> is called. Use this if your host has no
    /// existing message loop.
    /// </summary>
    public static void RunMessageLoop() => Excef.excef_run_message_loop();

    /// <summary>Request the message loop to exit. Safe from any thread.</summary>
    public static void QuitMessageLoop() => Excef.excef_quit_message_loop();

    /// <summary>Shut CEF down. Call after the host's message loop ends.</summary>
    public static void Shutdown()
    {
        Excef.excef_shutdown();
        s_scheduleCallback = null;
        s_paintHandlers.Clear();
        s_pdfCallbacks.Clear();
    }

    // ---- Internals --------------------------------------------------------

    private static Action<long>? s_scheduleCallback;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, PaintHandler> s_paintHandlers = new();
    // PDF callbacks are stored as a per-browser list (locked) because the native
    // shim's done-callback only carries (browserId, success) — there's no request
    // id to disambiguate concurrent prints. CEF processes prints per-browser in
    // submission order, so FIFO dequeue matches CEF's callback order.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<Action<int, int>>> s_pdfCallbacks = new();
    private static int s_nextEvalRequestId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> s_evalRequests = new();
    private static int s_nextCookieRequestId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (List<CookieInfo> List, TaskCompletionSource<List<CookieInfo>> Tcs)> s_cookieRequests = new();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SchedulePumpWorkTrampoline(long delayMs)
    {
        s_scheduleCallback?.Invoke(delayMs);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PaintTrampoline(int browserId, void* buffer, int width, int height)
    {
        if (s_paintHandlers.TryGetValue(browserId, out var h))
        {
            h(browserId, (IntPtr)buffer, width, height);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void PdfDoneTrampoline(int browserId, int success)
    {
        if (!s_pdfCallbacks.TryGetValue(browserId, out var queue)) return;
        Action<int, int>? cb = null;
        lock (queue)
        {
            if (queue.Count > 0)
            {
                cb = queue[0];
                queue.RemoveAt(0);
            }
        }
        cb?.Invoke(browserId, success);
    }

    private static unsafe sbyte** AllocArgv(string[] args, out int argc)
    {
        argc = args.Length;
        nuint size = (nuint)(IntPtr.Size * argc);
        sbyte** argv = (sbyte**)NativeMemory.Alloc(size);
        for (int i = 0; i < argc; i++)
        {
            argv[i] = (sbyte*)Marshal.StringToCoTaskMemUTF8(args[i]);
        }
        return argv;
    }

    // CEF copies argv contents during init/exec (Chromium base::CommandLine
    // uses its own storage), so freeing after the native call returns is safe.
    private static unsafe void FreeArgv(sbyte** argv, int argc)
    {
        if (argv == null) return;
        for (int i = 0; i < argc; i++)
        {
            if (argv[i] is not null) Marshal.FreeCoTaskMem((IntPtr)argv[i]);
        }
        NativeMemory.Free(argv);
    }

    private static unsafe sbyte* MarshalUtf8(string? s)
        => s is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(s);

    private static unsafe void FreeUtf8(sbyte* p)
    {
        if (p is not null) Marshal.FreeCoTaskMem((IntPtr)p);
    }

    private static unsafe string ReadCString(sbyte* ptr, int maxLen)
    {
        int len = 0;
        while (len < maxLen && ptr[len] != 0) len++;
        return Encoding.UTF8.GetString((byte*)ptr, len);
    }
}
