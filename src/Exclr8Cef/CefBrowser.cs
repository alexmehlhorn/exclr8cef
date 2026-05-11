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
    /// Fires when the page invokes <c>alert()</c>, <c>confirm()</c>,
    /// <c>prompt()</c>, or <c>onbeforeunload</c>. Hosts MUST call either
    /// <see cref="JsDialogEventArgs.Continue"/> or
    /// <see cref="JsDialogEventArgs.Cancel"/> to dismiss the dialog and
    /// unblock the renderer; failing to do so will leave the page hung
    /// at the dialog call. If no subscriber exists, CEF's default
    /// behaviour (Chromium-rendered system-style dialog) runs instead.
    /// </summary>
    public event EventHandler<JsDialogEventArgs>? JsDialog;

    /// <summary>
    /// Fires when the page triggers a file chooser (<c>&lt;input type=file&gt;</c>,
    /// <c>showOpenFilePicker</c>, save-as on a download). Hosts MUST call
    /// either <see cref="FileDialogEventArgs.Continue"/> with one or more
    /// paths, or <see cref="FileDialogEventArgs.Cancel"/>. Without a
    /// subscriber, CEF's default file picker runs.
    /// </summary>
    public event EventHandler<FileDialogEventArgs>? FileDialog;

    /// <summary>
    /// Fires on right-click / long-press. In OSR mode CEF cannot render
    /// the menu itself, so the host MUST render its own using the
    /// supplied items and call <see cref="ContextMenuEventArgs.Continue"/>
    /// with the chosen id, or <see cref="ContextMenuEventArgs.Cancel"/>.
    /// Without a subscriber the menu is suppressed entirely.
    /// </summary>
    public event EventHandler<ContextMenuEventArgs>? ContextMenu;

    /// <summary>
    /// Fires once when a download begins, before any bytes are written.
    /// Host must call <see cref="DownloadStartingEventArgs.Continue"/>
    /// with a save path, or <see cref="DownloadStartingEventArgs.Cancel"/>.
    /// Without a subscriber the file is saved with a default name.
    /// </summary>
    public event EventHandler<DownloadStartingEventArgs>? DownloadStarting;

    /// <summary>
    /// Fires repeatedly while a download is in flight (typically a few
    /// times per second). The args carry the current snapshot plus
    /// Cancel / Pause / Resume actions. Actions must be called
    /// synchronously inside the event handler — the underlying CEF
    /// callback is per-invocation.
    /// </summary>
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgress;

    /// <summary>
    /// Fires when the server (or proxy) returns a 401/407 with an
    /// HTTP-Basic / Digest / NTLM challenge. Host MUST call
    /// <see cref="AuthRequestEventArgs.Continue"/> with credentials,
    /// or <see cref="AuthRequestEventArgs.Cancel"/>. Without a
    /// subscriber the request fails.
    /// </summary>
    public event EventHandler<AuthRequestEventArgs>? AuthRequest;

    /// <summary>
    /// Fires once or more per <see cref="Find"/> call with the current
    /// match count + ordinal. Observational only — no callback to dismiss.
    /// </summary>
    public event EventHandler<FindResultEventArgs>? FindResult;

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

    // ---- Find in page ---------------------------------------------------

    /// <summary>
    /// Begin (or continue) an in-page search. Calling with
    /// <paramref name="findNext"/> = true while a search is active jumps
    /// to the next match without restarting; <paramref name="findNext"/>
    /// = false starts a new search. Passing an empty <paramref name="searchText"/>
    /// stops the search. Results stream back on <see cref="FindResult"/>.
    /// </summary>
    public void Find(string searchText, bool forward = true, bool matchCase = false, bool findNext = false)
    {
        if (_closed) return;
        unsafe
        {
            sbyte* p = (sbyte*)Marshal.StringToCoTaskMemUTF8(searchText ?? "");
            try { Excef.excef_find(Id, p, forward ? 1 : 0, matchCase ? 1 : 0, findNext ? 1 : 0); }
            finally { Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void StopFinding(bool clearSelection = true)
    {
        if (!_closed) Excef.excef_stop_finding(Id, clearSelection ? 1 : 0);
    }

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

    internal bool HasJsDialogSubscriber => JsDialog is not null;
    internal bool HasFileDialogSubscriber => FileDialog is not null;
    internal bool HasContextMenuSubscriber => ContextMenu is not null;

    internal void RaiseJsDialog(ulong token, Cef.JsDialogType type, string message, string defaultPrompt)
        => JsDialog?.Invoke(this, new JsDialogEventArgs(token, type, message, defaultPrompt));

    internal void RaiseFileDialog(ulong token, Cef.FileDialogMode mode, string title, string defaultPath, string[] filters)
        => FileDialog?.Invoke(this, new FileDialogEventArgs(token, mode, title, defaultPath, filters));

    internal void RaiseContextMenu(ulong token, int x, int y, ContextMenuItem[] items)
        => ContextMenu?.Invoke(this, new ContextMenuEventArgs(token, x, y, items));

    internal bool HasDownloadStartingSubscriber => DownloadStarting is not null;

    internal void RaiseDownloadStarting(ulong token, int downloadId, string url, string suggestedName, string mimeType, long totalBytes)
        => DownloadStarting?.Invoke(this, new DownloadStartingEventArgs(token, downloadId, url, suggestedName, mimeType, totalBytes));

    internal void RaiseDownloadProgress(ulong token, int downloadId, int percent, long received, long total, long speed, Cef.DownloadState state, string fullPath)
        => DownloadProgress?.Invoke(this, new DownloadProgressEventArgs(token, downloadId, percent, received, total, speed, state, fullPath));

    internal bool HasAuthRequestSubscriber => AuthRequest is not null;

    internal void RaiseAuthRequest(ulong token, bool isProxy, string host, int port, string realm, string scheme)
        => AuthRequest?.Invoke(this, new AuthRequestEventArgs(token, isProxy, host, port, realm, scheme));

    internal void RaiseFindResult(int identifier, int count, int activeMatchOrdinal, bool finalUpdate)
        => FindResult?.Invoke(this, new FindResultEventArgs(identifier, count, activeMatchOrdinal, finalUpdate));

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

/// <summary>
/// Args for <see cref="CefBrowser.JsDialog"/>. Hosts MUST call exactly one
/// of <see cref="Continue"/> / <see cref="Cancel"/> to dismiss the dialog
/// — leaving it unresolved hangs the renderer.
/// </summary>
public sealed class JsDialogEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved; // 0 = pending, 1 = resolved (atomic flip)

    /// <summary>Which dialog primitive triggered the event.</summary>
    public Cef.JsDialogType Type { get; }
    /// <summary>The message text from the page (the alert/confirm/prompt argument).</summary>
    public string Message { get; }
    /// <summary>Default text for prompt() dialogs; empty otherwise.</summary>
    public string DefaultPromptText { get; }

    internal JsDialogEventArgs(ulong token, Cef.JsDialogType type, string message, string defaultPrompt)
    {
        _token = token;
        Type = type;
        Message = message;
        DefaultPromptText = defaultPrompt;
    }

    /// <summary>
    /// Accept the dialog. For prompts, <paramref name="userInput"/> is the
    /// text the user typed; ignored for alert / confirm / onbeforeunload.
    /// Idempotent — subsequent calls are no-ops.
    /// </summary>
    public void Continue(string? userInput = null)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = userInput is null ? null : (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(userInput);
            try { Native.Excef.excef_resolve_js_dialog(_token, 1, p); }
            finally { if (p is not null) System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    /// <summary>Cancel the dialog (user clicked Cancel / closed). Idempotent.</summary>
    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_js_dialog(_token, 0, null); }
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.AuthRequest"/>. Host MUST call
/// <see cref="Continue"/> with credentials or <see cref="Cancel"/>.
/// </summary>
public sealed class AuthRequestEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public bool IsProxy { get; }
    public string Host { get; }
    public int Port { get; }
    public string Realm { get; }
    /// <summary>Auth scheme — "basic", "digest", "ntlm", "negotiate".</summary>
    public string Scheme { get; }

    internal AuthRequestEventArgs(ulong token, bool isProxy, string host, int port, string realm, string scheme)
    {
        _token = token; IsProxy = isProxy; Host = host; Port = port; Realm = realm; Scheme = scheme;
    }

    public void Continue(string username, string password)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* u = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(username ?? "");
            sbyte* p = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(password ?? "");
            try { Native.Excef.excef_resolve_auth(_token, u, p); }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)u);
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p);
            }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_auth(_token, null, null); }
    }
}

/// <summary>Args for <see cref="CefBrowser.FindResult"/>.</summary>
public sealed class FindResultEventArgs : EventArgs
{
    /// <summary>CEF-internal search session id.</summary>
    public int Identifier { get; }
    /// <summary>Total match count.</summary>
    public int Count { get; }
    /// <summary>1-based ordinal of the currently-active (highlighted) match. 0 if no matches.</summary>
    public int ActiveMatchOrdinal { get; }
    /// <summary>True if this is the last update for the current Find session.</summary>
    public bool FinalUpdate { get; }
    internal FindResultEventArgs(int identifier, int count, int activeMatchOrdinal, bool finalUpdate)
    {
        Identifier = identifier; Count = count; ActiveMatchOrdinal = activeMatchOrdinal; FinalUpdate = finalUpdate;
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.DownloadStarting"/>. Host MUST call
/// <see cref="Continue"/> with a save path or <see cref="Cancel"/>.
/// </summary>
public sealed class DownloadStartingEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public int DownloadId { get; }
    public string Url { get; }
    public string SuggestedName { get; }
    public string MimeType { get; }
    /// <summary>Total content length, -1 if unknown.</summary>
    public long TotalBytes { get; }

    internal DownloadStartingEventArgs(ulong token, int downloadId, string url, string suggestedName, string mimeType, long totalBytes)
    {
        _token = token; DownloadId = downloadId; Url = url; SuggestedName = suggestedName;
        MimeType = mimeType; TotalBytes = totalBytes;
    }

    /// <summary>Start the download to <paramref name="path"/>. If <paramref name="showDialog"/> is true, the OS save-as dialog will appear as well.</summary>
    public void Continue(string path, bool showDialog = false)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe
        {
            sbyte* p = (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(path);
            try { Native.Excef.excef_resolve_download_starting(_token, p, showDialog ? 1 : 0); }
            finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_download_starting(_token, null, 0); }
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.DownloadProgress"/>. Action methods
/// (<see cref="Cancel"/> / <see cref="Pause"/> / <see cref="Resume"/>)
/// MUST be called synchronously inside the event handler.
/// </summary>
public sealed class DownloadProgressEventArgs : EventArgs
{
    private readonly ulong _token;

    public int DownloadId { get; }
    /// <summary>0–100, or -1 if total size is unknown.</summary>
    public int PercentComplete { get; }
    public long ReceivedBytes { get; }
    public long TotalBytes { get; }
    public long CurrentSpeedBytesPerSec { get; }
    public Cef.DownloadState State { get; }
    public string FullPath { get; }

    internal DownloadProgressEventArgs(ulong token, int downloadId, int percent, long received, long total, long speed, Cef.DownloadState state, string fullPath)
    {
        _token = token; DownloadId = downloadId; PercentComplete = percent;
        ReceivedBytes = received; TotalBytes = total; CurrentSpeedBytesPerSec = speed;
        State = state; FullPath = fullPath;
    }

    public void Cancel() => Native.Excef.excef_download_action(_token, 0);
    public void Pause()  => Native.Excef.excef_download_action(_token, 1);
    public void Resume() => Native.Excef.excef_download_action(_token, 2);
}

/// <summary>One entry in <see cref="ContextMenuEventArgs.Items"/>.</summary>
/// <param name="CommandId">CEF's command id; 0 + empty label = separator.</param>
/// <param name="Label">The display text (system locale).</param>
public readonly record struct ContextMenuItem(int CommandId, string Label)
{
    public bool IsSeparator => CommandId == 0 && string.IsNullOrEmpty(Label);
}

/// <summary>
/// Args for <see cref="CefBrowser.ContextMenu"/>. Hosts MUST call exactly
/// one of <see cref="Continue"/> / <see cref="Cancel"/>.
/// </summary>
public sealed class ContextMenuEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    /// <summary>Click position in CSS pixels relative to the page.</summary>
    public int X { get; }
    /// <summary>Click position in CSS pixels relative to the page.</summary>
    public int Y { get; }
    /// <summary>The menu items CEF would have shown. Render these in the host UI.</summary>
    public IReadOnlyList<ContextMenuItem> Items { get; }

    internal ContextMenuEventArgs(ulong token, int x, int y, ContextMenuItem[] items)
    {
        _token = token; X = x; Y = y; Items = items;
    }

    /// <summary>Resolve with the chosen command id (must match one of <see cref="Items"/>).</summary>
    public void Continue(int commandId)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Native.Excef.excef_resolve_context_menu(_token, commandId);
    }

    /// <summary>Dismiss without executing any command.</summary>
    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        Native.Excef.excef_resolve_context_menu(_token, -1);
    }
}

/// <summary>
/// Args for <see cref="CefBrowser.FileDialog"/>. Hosts MUST call exactly
/// one of <see cref="Continue"/> / <see cref="Cancel"/>.
/// </summary>
public sealed class FileDialogEventArgs : EventArgs
{
    private readonly ulong _token;
    private int _resolved;

    public Cef.FileDialogMode Mode { get; }
    /// <summary>Title for the picker (empty = host default).</summary>
    public string Title { get; }
    /// <summary>Default file/folder path (empty if no hint).</summary>
    public string DefaultPath { get; }
    /// <summary>
    /// Accept-filter list from the page (MIME types like "image/*", or
    /// globs like "*.txt"). Empty if no filter.
    /// </summary>
    public IReadOnlyList<string> AcceptFilters { get; }

    internal FileDialogEventArgs(ulong token, Cef.FileDialogMode mode, string title, string defaultPath, string[] filters)
    {
        _token = token;
        Mode = mode;
        Title = title;
        DefaultPath = defaultPath;
        AcceptFilters = filters;
    }

    /// <summary>
    /// Resolve with the user's selection. Pass one path for Open/Save/OpenFolder,
    /// one or more for OpenMultiple. Pass an empty array (or call <see cref="Cancel"/>)
    /// for "user cancelled".
    /// </summary>
    public void Continue(params string[] paths)
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        var joined = paths.Length == 0 ? null : string.Join('\n', paths);
        unsafe
        {
            sbyte* p = joined is null ? null : (sbyte*)System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(joined);
            try { Native.Excef.excef_resolve_file_dialog(_token, p); }
            finally { if (p is not null) System.Runtime.InteropServices.Marshal.FreeCoTaskMem((IntPtr)p); }
        }
    }

    public void Cancel()
    {
        if (System.Threading.Interlocked.Exchange(ref _resolved, 1) != 0) return;
        unsafe { Native.Excef.excef_resolve_file_dialog(_token, null); }
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
