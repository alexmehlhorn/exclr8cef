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
