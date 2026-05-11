using System.Runtime.InteropServices;
using Exclr8Cef.Native;

namespace Exclr8Cef;

/// <summary>
/// A single off-screen Chromium browser instance. Created via
/// <see cref="Cef.CreateOffscreenBrowser"/>. Exposes the full per-browser
/// event stack (navigation, title, loading, cursor, paint, …) and the
/// per-browser command surface (navigation, input forwarding, JavaScript,
/// clipboard, zoom, IME, PDF). UI-tech specific controls
/// (<c>Exclr8Cef.WebView</c> for Avalonia, hypothetical WPF/MAUI/headless
/// variants) wrap an instance of this class and forward only the bits
/// they need to wire to their host framework.
/// </summary>
public sealed class CefBrowser : IDisposable
{
    /// <summary>Browser id assigned by Exclr8Cef (≥ 1). Zero after close.</summary>
    public int Id { get; private set; }

    private string _url = "about:blank";
    private string _title = "";
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private double _zoomLevel;
    private bool _closed;

    /// <summary>The currently-loaded URL (kept in sync with CEF's AddressChange).</summary>
    public string Url => _url;

    /// <summary>The currently-loaded page title.</summary>
    public string Title => _title;

    public bool IsLoading => _isLoading;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;
    public bool IsClosed => _closed;

    private bool _initialized;
    /// <summary>
    /// True after CEF has constructed the underlying browser and it is
    /// ready for operations that need a CefBrowser ref (Copy/Paste/Cut,
    /// frame access, clipboard, dev-tools open, …). The numeric
    /// <see cref="Id"/> is valid immediately on return from
    /// <see cref="Cef.CreateOffscreenBrowser"/>, but those ops are
    /// no-ops until <see cref="Initialized"/> fires.
    /// </summary>
    public bool IsInitialized => _initialized;

    // ---- Events ---------------------------------------------------------
    //
    // All instance events. Sender is this CefBrowser. Fires on the CEF
    // pump thread — hosts that update UI should marshal to their UI
    // thread themselves (e.g. Dispatcher.UIThread.Post in Avalonia).

    /// <summary>Fires when the main-frame URL changes.</summary>
    public event EventHandler<string>? AddressChanged;

    /// <summary>Fires when the page title changes.</summary>
    public event EventHandler<string>? TitleChanged;

    /// <summary>Fires when loading state changes (isLoading / canGoBack / canGoForward).</summary>
    public event EventHandler<LoadingState>? LoadingStateChanged;

    /// <summary>Fires when the page requests a different cursor type (CSS <c>cursor:</c>).</summary>
    public event EventHandler<Cef.CefCursorType>? CursorChanged;

    /// <summary>
    /// Fires for every <c>console.{log,info,warn,error,debug}</c> call from
    /// the page and for Chromium-emitted runtime warnings (CORS, deprecation, …).
    /// </summary>
    public event EventHandler<ConsoleMessageEventArgs>? ConsoleMessage;

    /// <summary>Per-frame load started. Fires once per frame; check <c>IsMainFrame</c> for top-level.</summary>
    public event EventHandler<LoadStartEventArgs>? LoadStart;

    /// <summary>Per-frame load finished. <c>HttpStatusCode</c> is 0 for non-HTTP loads (data:/file:/about:).</summary>
    public event EventHandler<LoadEndEventArgs>? LoadEnd;

    /// <summary>Per-frame load error. ErrorCode is from <see cref="Cef.CefErrorCode"/>; ERR_ABORTED (-3) means navigation was intentionally cancelled.</summary>
    public event EventHandler<LoadErrorEventArgs>? LoadError;

    /// <summary>Main-frame loading progress as a value in [0.0, 1.0]. Fires repeatedly during a load.</summary>
    public event EventHandler<double>? LoadingProgress;

    /// <summary>Status-bar text, typically the URL of the link under the mouse cursor (empty when not hovering a link).</summary>
    public event EventHandler<string>? StatusMessage;

    /// <summary>Tooltip text from <c>title=</c> attributes / <c>aria-describedby</c>.</summary>
    public event EventHandler<string>? TooltipChanged;

    /// <summary>The highest-priority favicon URL declared by the page (empty if none).</summary>
    public event EventHandler<string>? FaviconChanged;

    /// <summary>Page entered or left HTML5 fullscreen.</summary>
    public event EventHandler<bool>? FullscreenModeChanged;

    /// <summary>
    /// Fires whenever the page's scroll position changes (in CSS pixels).
    /// May fire many times per second during smooth-scrolling — hosts that
    /// want a coarser stream should throttle locally.
    /// </summary>
    public event EventHandler<ScrollOffsetEventArgs>? ScrollOffsetChanged;

    /// <summary>
    /// Fires after the page's natural content size changes, but only when
    /// auto-resize is enabled (see <see cref="SetAutoResizeEnabled"/>).
    /// Width / height are in CSS pixels.
    /// </summary>
    public event EventHandler<AutoResizeEventArgs>? AutoResize;

    /// <summary>
    /// Fires once, when the underlying CefBrowser is constructed and ready
    /// for operations that need a CefBrowser ref. See <see cref="IsInitialized"/>.
    /// If subscription happens after the browser is already initialised,
    /// the handler is invoked synchronously on the subscribing thread —
    /// safe to use as a "do this once the browser is up" pattern.
    /// </summary>
    public event EventHandler? Initialized
    {
        add
        {
            _initializedHandlers += value;
            if (_initialized) value?.Invoke(this, EventArgs.Empty);
        }
        remove { _initializedHandlers -= value; }
    }
    private EventHandler? _initializedHandlers;

    /// <summary>Fires after the underlying CEF browser has been fully closed.</summary>
    public event EventHandler? Closed;

    /// <summary>
    /// Fires when CEF has new pixels for this browser. Buffer is BGRA8888
    /// (top-left origin, stride = width × 4) and only valid for the duration
    /// of the handler — copy what you need.
    /// </summary>
    public event EventHandler<PaintEventArgs>? Painted;

    internal CefBrowser(int id) { Id = id; }

    // ---- Navigation -----------------------------------------------------

    public void LoadUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(url);
            try { Excef.excef_load_url(Id, p); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void GoBack()    { if (!_closed) Excef.excef_go_back(Id); }
    public void GoForward() { if (!_closed) Excef.excef_go_forward(Id); }
    public void Reload(bool ignoreCache = false)
    {
        if (!_closed) Excef.excef_reload(Id, ignoreCache ? 1 : 0);
    }
    public void StopLoad() { if (!_closed) Excef.excef_stop_load(Id); }

    // ---- Sizing / paint pipeline ---------------------------------------

    /// <summary>Notify CEF that the off-screen view rect has changed (DIP size).</summary>
    public void Resize(int width, int height)
    {
        if (!_closed) Excef.excef_resize_offscreen_browser(Id, width, height);
    }

    /// <summary>
    /// Update the device scale factor (e.g. dragging across monitors with
    /// different DPI). CEF re-lays-out and emits a fresh paint at the new
    /// physical-pixel size.
    /// </summary>
    public void SetDeviceScaleFactor(float scale)
    {
        if (!_closed) Excef.excef_set_device_scale_factor(Id, scale);
    }

    public void WasHidden(bool hidden)
    {
        if (!_closed) Excef.excef_was_hidden(Id, hidden ? 1 : 0);
    }

    /// <summary>
    /// Exit page-driven HTML5 fullscreen. Equivalent to JS
    /// <c>document.exitFullscreen()</c>. Pass <paramref name="willCauseResize"/> = true
    /// (the default) so Chromium knows the host will resize the view in response —
    /// suppresses redundant layout flicker.
    /// </summary>
    public void ExitFullscreen(bool willCauseResize = true)
    {
        if (!_closed) Excef.excef_exit_fullscreen(Id, willCauseResize ? 1 : 0);
    }

    /// <summary>
    /// Enable / disable Chromium's auto-resize. When enabled, Chromium
    /// takes over the browser's view sizing — the page lays out at its
    /// natural content size (clamped to the min/max bounds), <see cref="AutoResize"/>
    /// fires with that size, and the host is expected to resize its
    /// control to match. Used for embed/iframe scenarios where the host
    /// wants the browser to be exactly as tall as the content.
    ///
    /// <para><b>Important:</b> if you enable auto-resize but don't resize
    /// the host control in response to the <see cref="AutoResize"/> event,
    /// the page won't render correctly and input may stop working — the
    /// host's view rect and Chromium's chosen rect diverge. For standard
    /// "fixed-size embed" scenarios leave auto-resize disabled.</para>
    ///
    /// Pass <c>enabled=false</c> to disable; min/max are ignored.
    /// </summary>
    public void SetAutoResizeEnabled(bool enabled,
                                      int minWidth = 1, int minHeight = 1,
                                      int maxWidth = 4096, int maxHeight = 4096)
    {
        if (!_closed) Excef.excef_set_auto_resize_enabled(Id, enabled ? 1 : 0,
            minWidth, minHeight, maxWidth, maxHeight);
    }

    // ---- Zoom -----------------------------------------------------------

    public double ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            _zoomLevel = value;
            if (!_closed) Excef.excef_set_zoom_level(Id, value);
        }
    }

    /// <summary>
    /// Whether the given zoom command is currently available. Useful for
    /// graying out zoom buttons at min/max zoom (zoom in / zoom out
    /// return false once the limit is reached).
    /// </summary>
    public bool CanZoom(Cef.CefZoomCommand command)
        => !_closed && Excef.excef_can_zoom(Id, (int)command) != 0;

    public bool CanZoomIn    => CanZoom(Cef.CefZoomCommand.In);
    public bool CanZoomOut   => CanZoom(Cef.CefZoomCommand.Out);
    public bool CanZoomReset => CanZoom(Cef.CefZoomCommand.Reset);

    // ---- Clipboard / editing -------------------------------------------

    public void Copy()      { if (!_closed) Excef.excef_copy(Id); }
    public void Paste()     { if (!_closed) Excef.excef_paste(Id); }
    public void Cut()       { if (!_closed) Excef.excef_cut(Id); }
    public void SelectAll() { if (!_closed) Excef.excef_select_all(Id); }
    public void Undo()      { if (!_closed) Excef.excef_undo(Id); }
    public void Redo()      { if (!_closed) Excef.excef_redo(Id); }

    // ---- Input forwarding ----------------------------------------------

    public void SendMouseMove(int x, int y, Cef.CefModifiers modifiers, bool mouseLeave)
    {
        if (!_closed) Excef.excef_send_mouse_move(Id, x, y, (int)modifiers, mouseLeave ? 1 : 0);
    }

    public void SendMouseClick(int x, int y, Cef.CefMouseButton button, bool mouseUp, int clickCount, Cef.CefModifiers modifiers)
    {
        if (!_closed) Excef.excef_send_mouse_click(Id, x, y, (int)button, mouseUp ? 1 : 0, clickCount, (int)modifiers);
    }

    public void SendMouseWheel(int x, int y, int deltaX, int deltaY, Cef.CefModifiers modifiers)
    {
        if (!_closed) Excef.excef_send_mouse_wheel(Id, x, y, deltaX, deltaY, (int)modifiers);
    }

    public void SendKeyEvent(
        Cef.CefKeyEventType type,
        int windowsKeyCode,
        int nativeKeyCode,
        Cef.CefModifiers modifiers,
        char character,
        char unmodifiedCharacter,
        bool isSystemKey)
    {
        if (_closed) return;
        Excef.excef_send_key_event(
            Id, (int)type,
            windowsKeyCode, nativeKeyCode,
            (int)modifiers,
            character, unmodifiedCharacter,
            isSystemKey ? 1 : 0);
    }

    public void SetFocus(bool focus)
    {
        if (!_closed) Excef.excef_set_browser_focus(Id, focus ? 1 : 0);
    }

    // ---- DevTools -------------------------------------------------------

    public void ShowDevTools()  { if (!_closed) Excef.excef_show_dev_tools(Id); }
    public void CloseDevTools() { if (!_closed) Excef.excef_close_dev_tools(Id); }

    // ---- JavaScript -----------------------------------------------------

    public void ExecuteJavaScript(string code, string? scriptUrl = null)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_closed) return;
        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            sbyte* urlPtr = scriptUrl is null ? null : (sbyte*)Marshal.StringToCoTaskMemUTF8(scriptUrl);
            try { Excef.excef_execute_javascript(Id, codePtr, urlPtr); }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
                if (urlPtr is not null) Marshal.FreeCoTaskMem((IntPtr)urlPtr);
            }
        }
    }

    /// <summary>
    /// Evaluate JS in the main frame and return the result as a JSON string.
    /// </summary>
    public Task<string> EvaluateJavaScriptAsync(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (_closed) return Task.FromException<string>(new InvalidOperationException("browser closed"));

        int reqId = Interlocked.Increment(ref Cef.s_nextEvalRequestId);
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Cef.s_evalRequests[reqId] = tcs;

        unsafe
        {
            sbyte* codePtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(code);
            try
            {
                int scheduled = Excef.excef_eval_javascript(Id, reqId, codePtr);
                if (scheduled == 0)
                {
                    Cef.s_evalRequests.TryRemove(reqId, out _);
                    tcs.TrySetException(new InvalidOperationException("eval not scheduled (browser unknown or closed)"));
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem((IntPtr)codePtr);
            }
        }
        return tcs.Task;
    }

    // ---- PDF ------------------------------------------------------------

    // The CEF callback only carries (browserId, success), so concurrent
    // prints on the same browser are demuxed via a FIFO queue.
    private readonly List<Action<int, int>> _pdfQueue = new();
    internal List<Action<int, int>> PdfQueue => _pdfQueue;

    public Task<bool> PrintToPdfAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_closed) return Task.FromResult(false);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<int, int> cb = (_, ok) => tcs.TrySetResult(ok != 0);

        unsafe
        {
            sbyte* pathPtr = (sbyte*)Marshal.StringToCoTaskMemUTF8(path);
            try
            {
                lock (_pdfQueue)
                {
                    _pdfQueue.Add(cb);
                    int scheduled = Excef.excef_print_to_pdf(Id, pathPtr, &Cef.PdfDoneTrampoline);
                    if (scheduled == 0)
                    {
                        _pdfQueue.RemoveAt(_pdfQueue.Count - 1);
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

    // ---- IME ------------------------------------------------------------

    public void ImeSetComposition(string text,
                                   int replacementRangeStart = 0,
                                   int replacementRangeLength = 0,
                                   int selectionStart = 0,
                                   int selectionLength = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_set_composition(Id, p,
                    replacementRangeStart, replacementRangeLength,
                    selectionStart, selectionLength);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void ImeCommitText(string text,
                               int replacementRangeStart = 0,
                               int replacementRangeLength = 0,
                               int relativeCursorPos = 0)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(text);
            try
            {
                Excef.excef_ime_commit_text(Id, p,
                    replacementRangeStart, replacementRangeLength,
                    relativeCursorPos);
            }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void ImeFinishComposing(bool keepSelection = false)
    {
        if (!_closed) Excef.excef_ime_finish_composing(Id, keepSelection ? 1 : 0);
    }

    public void ImeCancel() { if (!_closed) Excef.excef_ime_cancel(Id); }

    // ---- Close ----------------------------------------------------------

    /// <summary>Request the browser close. Idempotent.</summary>
    public void Close(bool force = false)
    {
        if (_closed) return;
        Excef.excef_close_browser(Id, force ? 1 : 0);
        // Actual transition to _closed happens in RaiseClosed (from the
        // OnBeforeClose native callback).
    }

    /// <summary>Equivalent to <c>Close(force: true)</c>.</summary>
    public void Dispose() => Close(force: true);

    // ---- Internal event raisers (called from Cef trampolines) ----------

    internal void RaiseAddressChanged(string url)
    {
        _url = url;
        AddressChanged?.Invoke(this, url);
    }

    internal void RaiseTitleChanged(string title)
    {
        _title = title;
        TitleChanged?.Invoke(this, title);
    }

    internal void RaiseLoadingStateChanged(bool isLoading, bool canGoBack, bool canGoForward)
    {
        _isLoading = isLoading;
        _canGoBack = canGoBack;
        _canGoForward = canGoForward;
        LoadingStateChanged?.Invoke(this, new LoadingState(isLoading, canGoBack, canGoForward));
    }

    internal void RaiseCursorChanged(Cef.CefCursorType type)
        => CursorChanged?.Invoke(this, type);

    internal void RaiseConsoleMessage(Cef.CefLogSeverity level, string message, string source, int line)
        => ConsoleMessage?.Invoke(this, new ConsoleMessageEventArgs(level, message, source, line));

    internal void RaiseLoadStart(bool isMainFrame, string url)
        => LoadStart?.Invoke(this, new LoadStartEventArgs(isMainFrame, url));

    internal void RaiseLoadEnd(bool isMainFrame, string url, int httpStatusCode)
        => LoadEnd?.Invoke(this, new LoadEndEventArgs(isMainFrame, url, httpStatusCode));

    internal void RaiseLoadError(bool isMainFrame, Cef.CefErrorCode errorCode, string errorText, string failedUrl)
        => LoadError?.Invoke(this, new LoadErrorEventArgs(isMainFrame, errorCode, errorText, failedUrl));

    internal void RaiseLoadingProgress(double progress)
        => LoadingProgress?.Invoke(this, progress);

    internal void RaiseInitialized()
    {
        _initialized = true;
        _initializedHandlers?.Invoke(this, EventArgs.Empty);
    }

    internal void RaiseStatusMessage(string value) => StatusMessage?.Invoke(this, value);
    internal void RaiseTooltipChanged(string text) => TooltipChanged?.Invoke(this, text);
    internal void RaiseFaviconChanged(string url) => FaviconChanged?.Invoke(this, url);
    internal void RaiseFullscreenChanged(bool fullscreen) => FullscreenModeChanged?.Invoke(this, fullscreen);

    internal void RaiseScrollOffset(double x, double y)
        => ScrollOffsetChanged?.Invoke(this, new ScrollOffsetEventArgs(x, y));

    internal void RaiseAutoResize(int w, int h)
        => AutoResize?.Invoke(this, new AutoResizeEventArgs(w, h));

    internal void RaisePainted(IntPtr buffer, int width, int height)
        => Painted?.Invoke(this, new PaintEventArgs(buffer, width, height));

    internal void RaiseClosed()
    {
        _closed = true;
        // Fail any pending PDF callbacks so callers' Tasks complete instead of hanging.
        lock (_pdfQueue)
        {
            foreach (var cb in _pdfQueue) cb(Id, 0);
            _pdfQueue.Clear();
        }
        Closed?.Invoke(this, EventArgs.Empty);
        Id = 0;
    }
}

/// <summary>Loading-state snapshot fired by <see cref="CefBrowser.LoadingStateChanged"/>.</summary>
public readonly record struct LoadingState(bool IsLoading, bool CanGoBack, bool CanGoForward);

/// <summary>Paint frame delivered to <see cref="CefBrowser.Painted"/>.</summary>
public sealed class PaintEventArgs : EventArgs
{
    public IntPtr Buffer { get; }
    public int Width { get; }
    public int Height { get; }
    public PaintEventArgs(IntPtr buffer, int width, int height)
    {
        Buffer = buffer;
        Width = width;
        Height = height;
    }
}

/// <summary>Args for <see cref="CefBrowser.ScrollOffsetChanged"/>.</summary>
public sealed class ScrollOffsetEventArgs : EventArgs
{
    public double X { get; }
    public double Y { get; }
    public ScrollOffsetEventArgs(double x, double y) { X = x; Y = y; }
}

/// <summary>Args for <see cref="CefBrowser.AutoResize"/>.</summary>
public sealed class AutoResizeEventArgs : EventArgs
{
    public int Width { get; }
    public int Height { get; }
    public AutoResizeEventArgs(int w, int h) { Width = w; Height = h; }
}

/// <summary>Args for <see cref="CefBrowser.LoadStart"/>.</summary>
public sealed class LoadStartEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    public string Url { get; }
    public LoadStartEventArgs(bool isMainFrame, string url) { IsMainFrame = isMainFrame; Url = url; }
}

/// <summary>Args for <see cref="CefBrowser.LoadEnd"/>.</summary>
public sealed class LoadEndEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    public string Url { get; }
    /// <summary>HTTP status code (200, 404, …). 0 for non-HTTP loads (data:, file:, about:).</summary>
    public int HttpStatusCode { get; }
    public LoadEndEventArgs(bool isMainFrame, string url, int status)
    {
        IsMainFrame = isMainFrame; Url = url; HttpStatusCode = status;
    }
}

/// <summary>Args for <see cref="CefBrowser.LoadError"/>.</summary>
public sealed class LoadErrorEventArgs : EventArgs
{
    public bool IsMainFrame { get; }
    /// <summary>Error code from <see cref="Cef.CefErrorCode"/>. ERR_ABORTED (-3) means intentional cancel.</summary>
    public Cef.CefErrorCode ErrorCode { get; }
    public string ErrorText { get; }
    public string FailedUrl { get; }
    public LoadErrorEventArgs(bool isMainFrame, Cef.CefErrorCode code, string text, string failedUrl)
    {
        IsMainFrame = isMainFrame;
        ErrorCode = code;
        ErrorText = text;
        FailedUrl = failedUrl;
    }
}

/// <summary>Console message delivered to <see cref="CefBrowser.ConsoleMessage"/>.</summary>
public sealed class ConsoleMessageEventArgs : EventArgs
{
    /// <summary>Severity level from <c>cef_log_severity_t</c>.</summary>
    public Cef.CefLogSeverity Level { get; }
    /// <summary>The formatted message text.</summary>
    public string Message { get; }
    /// <summary>Source script URL (empty for inline scripts).</summary>
    public string Source { get; }
    /// <summary>1-based line number.</summary>
    public int Line { get; }
    public ConsoleMessageEventArgs(Cef.CefLogSeverity level, string message, string source, int line)
    {
        Level = level;
        Message = message;
        Source = source;
        Line = line;
    }
}
