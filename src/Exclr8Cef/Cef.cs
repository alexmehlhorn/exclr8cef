using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// Static facade for the Exclr8CEF process-wide lifecycle: init / shutdown,
/// message-loop pumping, top-level browser creation, the off-screen browser
/// factory, and the global cookie manager. Use from any .NET host —
/// console, WPF, MAUI, ASP.NET, services. UI-framework integrations
/// (<c>Exclr8Cef.WebView</c> for Avalonia, future WPF / MAUI variants)
/// build on the per-browser <see cref="CefBrowser"/> instance returned by
/// <see cref="CreateOffscreenBrowser"/>.
/// </summary>
public static class Cef
{
    private const int BufferSize = 64;

    /// <summary>
    /// Returns the linked shim, CEF, and Chromium versions. Safe to call
    /// before <see cref="Initialize"/>; reads compile-time constants from
    /// the shim.
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
    /// so this returns -1 immediately and is effectively a no-op.
    /// </summary>
    public static int ExecuteProcess(string[]? args = null)
    {
        if (OperatingSystem.IsMacOS()) return -1;
        args ??= Environment.GetCommandLineArgs();
        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            try { return Excef.excef_execute_process(argc, argv); }
            finally { FreeArgv(argv, argc); }
        }
    }

    /// <summary>
    /// Initialize CEF with its own internal message loop. Call once, then
    /// <see cref="RunMessageLoop"/>. Suited for console / non-UI hosts.
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
                if (rc != 0) throw new InvalidOperationException($"excef_initialize failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Initialize CEF with an external message pump. Use from UI hosts
    /// that already own the platform message loop (Avalonia, WPF). The
    /// host must call <see cref="DoMessageLoopWork"/> from its UI thread
    /// whenever <paramref name="schedulePumpWork"/> fires.
    /// </summary>
    public static void InitializeExternalPump(
        string[]? args,
        string? subprocessPath,
        Action<long> schedulePumpWork)
    {
        ArgumentNullException.ThrowIfNull(schedulePumpWork);
        args ??= Environment.GetCommandLineArgs();
        s_scheduleCallback = schedulePumpWork;

        unsafe
        {
            sbyte** argv = AllocArgv(args, out int argc);
            sbyte* subprocPtr = MarshalUtf8(subprocessPath);
            try
            {
                delegate* unmanaged[Cdecl]<long, void> trampoline = &SchedulePumpWorkTrampoline;
                int rc = Excef.excef_initialize_external_pump(argc, argv, subprocPtr, trampoline);
                if (rc != 0) throw new InvalidOperationException($"excef_initialize_external_pump failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    public static void DoMessageLoopWork() => Excef.excef_do_message_loop_work();

    /// <summary>
    /// Open a top-level Chromium browser window. Safe before or after
    /// <see cref="Initialize"/>; URLs requested pre-init are queued.
    /// Use <see cref="CreateOffscreenBrowser"/> for embedded OSR scenarios.
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
                if (rc != 0) throw new InvalidOperationException($"excef_create_browser failed with code {rc}");
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)ptr); }
        }
    }

    /// <summary>
    /// Create a native host view containing a CEF browser. Returns a handle
    /// to an <c>NSView</c> on macOS. Caller takes ownership.
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
            finally { Marshal.FreeCoTaskMem((IntPtr)ptr); }
        }
    }

    /// <summary>
    /// Initialize CEF in off-screen-rendering mode with an external message
    /// pump. Use from UI hosts that want to embed Chromium without giving
    /// CEF a native window — typical for Avalonia / WPF integration.
    /// Combine with <see cref="CreateOffscreenBrowser"/>.
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
                if (rc != 0) throw new InvalidOperationException($"excef_initialize_offscreen failed with code {rc}");
            }
            finally
            {
                FreeUtf8(subprocPtr);
                FreeArgv(argv, argc);
            }
        }
    }

    /// <summary>
    /// Create an off-screen browser whose view rect is <paramref name="width"/> ×
    /// <paramref name="height"/> DIPs. <paramref name="deviceScaleFactor"/> drives
    /// HiDPI: CEF allocates a paint buffer of (w × scale) × (h × scale) physical
    /// pixels while the page lays out at DIP size. Pass <c>1.0f</c> for non-HiDPI.
    /// Returns a <see cref="CefBrowser"/> with the full per-browser event/command
    /// surface, or <c>null</c> if creation failed.
    /// </summary>
    public static CefBrowser? CreateOffscreenBrowser(int width, int height, float deviceScaleFactor, string url)
    {
        ArgumentNullException.ThrowIfNull(url);

        unsafe
        {
            sbyte* urlPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try
            {
                delegate* unmanaged[Cdecl]<int, void*, int, int, void> trampoline = &PaintTrampoline;
                int id = Excef.excef_create_offscreen_browser(width, height, deviceScaleFactor, urlPtr, trampoline);
                if (id <= 0) return null;
                var browser = new CefBrowser(id);
                s_browsers[id] = browser;
                return browser;
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)urlPtr); }
        }
    }

    /// <summary>Iterates the currently-live OSR browsers.</summary>
    public static IEnumerable<CefBrowser> Browsers => s_browsers.Values;

    // ---- Cookies (process-wide cookie manager) -------------------------

    public sealed record CookieInfo(
        string Name,
        string Value,
        string Domain,
        string Path,
        bool Secure,
        bool HttpOnly);

    /// <summary>
    /// Get cookies. Pass empty/null <paramref name="url"/> to get all cookies.
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

    // ---- Process lifecycle ---------------------------------------------

    public static void RunMessageLoop() => Excef.excef_run_message_loop();
    public static void QuitMessageLoop() => Excef.excef_quit_message_loop();

    public static void Shutdown()
    {
        Excef.excef_shutdown();
        s_scheduleCallback = null;
        s_browsers.Clear();
        s_evalRequests.Clear();
        s_cookieRequests.Clear();
    }

    // ---- Shared enums (used by CefBrowser methods + native ABI) --------

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

    public enum CefMouseButton { Left = 0, Middle = 1, Right = 2 }

    public enum CefKeyEventType { RawKeyDown = 0, KeyDown = 1, KeyUp = 2, Char = 3 }

    /// <summary>
    /// CEF's <c>cef_log_severity_t</c>. Carried by <see cref="CefBrowser.ConsoleMessage"/>;
    /// derived from the JS console method (<c>console.log</c> → Info,
    /// <c>console.warn</c> → Warning, <c>console.error</c> → Error, …).
    /// </summary>
    public enum CefLogSeverity
    {
        Default = 0,
        Verbose = 1,  // a.k.a. Debug
        Info    = 2,
        Warning = 3,
        Error   = 4,
        Fatal   = 5,
        Disable = 99,
    }

    /// <summary>CEF's <c>cef_cursor_type_t</c>. Maps to OnCursorChange values.</summary>
    public enum CefCursorType
    {
        Pointer = 0, Cross = 1, Hand = 2, IBeam = 3, Wait = 4, Help = 5,
        EastResize = 6, NorthResize = 7, NorthEastResize = 8, NorthWestResize = 9,
        SouthResize = 10, SouthEastResize = 11, SouthWestResize = 12, WestResize = 13,
        NorthSouthResize = 14, EastWestResize = 15,
        NorthEastSouthWestResize = 16, NorthWestSouthEastResize = 17,
        ColumnResize = 18, RowResize = 19,
        MiddlePanning = 20, EastPanning = 21, NorthPanning = 22,
        NorthEastPanning = 23, NorthWestPanning = 24, SouthPanning = 25,
        SouthEastPanning = 26, SouthWestPanning = 27, WestPanning = 28,
        Move = 29, VerticalText = 30, Cell = 31, ContextMenu = 32,
        Alias = 33, Progress = 34, NoDrop = 35, Copy = 36, None = 37,
        NotAllowed = 38, ZoomIn = 39, ZoomOut = 40, Grab = 41, Grabbing = 42,
        MiddlePanningVertical = 43, MiddlePanningHorizontal = 44,
        Custom = 45,
        DndNone = 46, DndMove = 47, DndCopy = 48, DndLink = 49,
    }

    // ---- Native callback wiring ----------------------------------------

    private static bool s_eventsRegistered;
    private static readonly object s_eventsLock = new();

    /// <summary>
    /// Register the native event callbacks. Idempotent. Called automatically
    /// from <see cref="InitializeForOsr"/>; exposed for hosts that initialize
    /// CEF themselves but still want events.
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
                Excef.excef_set_console_message_callback(&ConsoleMessageTrampoline);
            }
            s_eventsRegistered = true;
        }
    }

    // ---- Trampolines: demux per-browser native callbacks to instances --

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void AddressChangeTrampoline(int browserId, sbyte* url)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        b.RaiseAddressChanged(Marshal.PtrToStringUTF8((IntPtr)url) ?? "");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void TitleChangeTrampoline(int browserId, sbyte* title)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        b.RaiseTitleChanged(Marshal.PtrToStringUTF8((IntPtr)title) ?? "");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void LoadingStateTrampoline(int browserId, int isLoading, int canGoBack, int canGoForward)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        b.RaiseLoadingStateChanged(isLoading != 0, canGoBack != 0, canGoForward != 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CursorChangeTrampoline(int browserId, int cursorType)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        b.RaiseCursorChanged((CefCursorType)cursorType);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ConsoleMessageTrampoline(int browserId, int level, sbyte* message, sbyte* source, int line)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        b.RaiseConsoleMessage(
            (CefLogSeverity)level,
            Marshal.PtrToStringUTF8((IntPtr)message) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)source) ?? "",
            line);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BrowserClosedTrampoline(int browserId)
    {
        if (s_browsers.TryRemove(browserId, out var b))
        {
            b.RaiseClosed();
        }
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

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void PdfDoneTrampoline(int browserId, int success)
    {
        if (!s_browsers.TryGetValue(browserId, out var b)) return;
        Action<int, int>? cb = null;
        lock (b.PdfQueue)
        {
            if (b.PdfQueue.Count > 0)
            {
                cb = b.PdfQueue[0];
                b.PdfQueue.RemoveAt(0);
            }
        }
        cb?.Invoke(browserId, success);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void PaintTrampoline(int browserId, void* buffer, int width, int height)
    {
        if (s_browsers.TryGetValue(browserId, out var b))
        {
            b.RaisePainted((IntPtr)buffer, width, height);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SchedulePumpWorkTrampoline(long delayMs)
        => s_scheduleCallback?.Invoke(delayMs);

    // ---- Internals ------------------------------------------------------

    private static Action<long>? s_scheduleCallback;

    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, CefBrowser> s_browsers = new();

    internal static int s_nextEvalRequestId;
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> s_evalRequests = new();

    private static int s_nextCookieRequestId;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (List<CookieInfo> List, TaskCompletionSource<List<CookieInfo>> Tcs)> s_cookieRequests = new();

    // ---- argv / utf-8 helpers ------------------------------------------

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
