using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Exclr8Cef.WebView;

/// <summary>
/// Avalonia control hosting an embedded Chromium browser via Exclr8CEF's
/// off-screen rendering (OSR) path. Forwards pointer/keyboard input to
/// CEF, surfaces address/title/loading-state changes as Avalonia
/// properties, and reflects CSS-driven cursor changes through Avalonia's
/// <see cref="Control.Cursor"/>.
///
/// Pointer events: mouse, touch, and pen are all routed through the
/// OnPointer* overrides. Touch and pen are forwarded as left-button
/// mouse events with no gesture support (no pinch-zoom, no two-finger
/// scroll). Suitable for desktop-first hosts.
///
/// HiDPI: the browser is configured with a device scale factor matching
/// the host TopLevel's <see cref="TopLevel.RenderScaling"/>. CEF lays
/// the page out at DIP/CSS-pixel size and renders into a buffer at
/// physical-pixel size; the resulting bitmap is drawn 1:1 at the
/// control's bounds with no upscale.
///
/// Lifecycle: the underlying CEF browser is created lazily on the first
/// arrange with non-zero size and held for the control's lifetime. It is
/// closed automatically when the host <see cref="Window"/> closes; call
/// <see cref="Close"/> to release it earlier.
/// </summary>
public class WebView : Control
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<WebView, string?>(nameof(Url), "about:blank");

    public static readonly DirectProperty<WebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<WebView, string>(
            nameof(Title), o => o.Title);

    public static readonly DirectProperty<WebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
            nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<WebView, bool> CanGoBackProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
            nameof(CanGoBack), o => o.CanGoBack);

    public static readonly DirectProperty<WebView, bool> CanGoForwardProperty =
        AvaloniaProperty.RegisterDirect<WebView, bool>(
            nameof(CanGoForward), o => o.CanGoForward);

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    private string _title = "";
    public string Title
    {
        get => _title;
        private set => SetAndRaise(TitleProperty, ref _title, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    private bool _canGoBack;
    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetAndRaise(CanGoBackProperty, ref _canGoBack, value);
    }

    private bool _canGoForward;
    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetAndRaise(CanGoForwardProperty, ref _canGoForward, value);
    }

    /// <summary>Browser id assigned by Exclr8Cef (0 if not yet created or closed).</summary>
    public int BrowserId => _browserId;

    private int _browserId;
    // Browser dimensions in DIPs / CSS pixels. The native shim multiplies these
    // by `_renderScale` (passed via SetDeviceScaleFactor + the create call) to
    // get the physical-pixel paint buffer size.
    private int _browserWidth;
    private int _browserHeight;
    private double _renderScale = 1.0;
    private WriteableBitmap? _bitmap;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private bool _attached;
    private bool _suppressUrlChange;
    private WebViewTextInputMethodClient? _imeClient;
    private Window? _hostedWindow;

    public WebView()
    {
        ClipToBounds = true;
        Focusable = true;

        // Disable Avalonia's tab-navigation involvement on the WebView. Tab
        // key handling belongs to the embedded Chromium page; without this,
        // Avalonia's KeyboardNavigationHandler ALSO processes Tab (moves
        // Avalonia focus to the next sibling control), so each Tab press
        // produces two effective Tab actions: the page navigates one input,
        // and Avalonia moves focus out — but the focus then re-enters the
        // WebView, the page sees a fresh Tab, and skips ahead.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);

        TextInputMethodClientRequested += (_, e) =>
        {
            _imeClient ??= new WebViewTextInputMethodClient(this);
            e.Client = _imeClient;
        };

        // KeyDown forwarding runs in the Tunnel phase: we need to claim the
        // event (for Tab, Enter, etc.) before any class handler — chiefly
        // KeyboardNavigationHandler — also processes it, otherwise the
        // page's Tab handling and Avalonia's focus shift fire concurrently.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Set when OnKeyDownTunnel forwards a RawKeyDown to the browser; cleared
    // by OnKeyUp. OnTextInput consults this to decide whether to synthesize a
    // RawKeyDown ahead of its Char dispatch — required on macOS, where
    // Avalonia routes printable keys (letters, digits, Space) through the
    // text-input system only, never firing KeyDownEvent. Without a matching
    // RawKeyDown the renderer can't run keydown-anchored default actions
    // (e.g. HTMLButtonElement::defaultEventHandler activates a focused
    // button on Space *keydown* and only dispatches click on keyup if the
    // button was activated).
    private bool _keyDownForwarded;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_browserId == 0) return;

        ForwardKeyToBrowser(e, isKeyUp: false);
        _keyDownForwarded = true;

        // Only claim Handled for keys whose entire behavior is the
        // RawKeyDown's default action (nav, function keys, Cmd shortcuts).
        // For printable keys with modifiers (e.g. Shift+letter) we MUST
        // leave Handled=false so Avalonia continues to its text-input
        // pipeline (interpretKeyEvents on macOS) and OnTextInput fires
        // with the character. Without that, the renderer gets RawKeyDown
        // but no Char/keypress and the input element won't insert the
        // typed character.
        var accelMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        bool isCmdShortcut = (e.KeyModifiers & accelMod) != 0;
        if (isCmdShortcut || IsNavigationKey(e.Key))
        {
            e.Handled = true;
        }
    }

    private static bool IsNavigationKey(Key k) => k switch
    {
        Key.Tab or Key.Return or Key.Enter or Key.Escape => true,
        Key.Up or Key.Down or Key.Left or Key.Right => true,
        Key.Home or Key.End or Key.PageUp or Key.PageDown => true,
        Key.Delete or Key.Back or Key.Insert => true,
        >= Key.F1 and <= Key.F24 => true,
        _ => false,
    };

    private void ForwardKeyToBrowser(KeyEventArgs e, bool isKeyUp)
    {
        // Cmd / Ctrl shortcuts (zoom, clipboard) — handle here so they
        // don't go to CEF as ordinary key events.
        var accelMod = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (!isKeyUp && (e.KeyModifiers & accelMod) != 0)
        {
            bool shiftAccel = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            switch (e.Key)
            {
                case Key.OemPlus:
                case Key.Add:        ZoomIn();    return;
                case Key.OemMinus:
                case Key.Subtract:   ZoomOut();   return;
                case Key.D0:
                case Key.NumPad0:    ResetZoom(); return;
                case Key.C:          Copy();      return;
                case Key.V:          Paste();     return;
                case Key.X:          Cut();       return;
                case Key.A:          SelectAll(); return;
                case Key.Z:
                    if (shiftAccel) Redo(); else Undo();
                    return;
                case Key.Y:          Redo();      return;
            }
        }

        int vk = KeyMap.AvaloniaToWindowsVK(e.Key);
        int nativeCode = OperatingSystem.IsMacOS() ? KeyMap.AvaloniaToMacKeyCode(e.Key) : 0;
        if (nativeCode < 0) nativeCode = 0;
        var modifiers = InputMapping.MapModifiers(e.KeyModifiers);

        bool shifted = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        // Compute keyChar for BOTH keydown and keyup. Chromium derives the
        // DOM `event.key` from this; if keyup has character=0 while keydown
        // had a real char, the renderer treats them as different keys, which
        // breaks behaviors that match a press-release pair (e.g. Space
        // activating a focused button: HTMLButtonElement::defaultEventHandler
        // sets `active` on keydown with key=" " and only dispatches click on
        // keyup if the key=" " matches).
        char keyChar = e.Key switch
        {
            Key.Enter  => '\r',  // also Key.Return
            Key.Space  => ' ',
            Key.Back   => '\b',
            Key.Escape => (char)27,
            // Tab / arrows / function keys / Home/End/PgUp/PgDn / Delete:
            // leave at 0. Chromium has hardcoded VK→DOM-key mapping for
            // these and supplying '\t' / arrow chars confuses it.
            _ => '\0',
        };
        if (keyChar == '\0')
        {
            if (vk >= 0x41 && vk <= 0x5A)
                keyChar = (char)(shifted ? vk : vk + 0x20);
            else if (vk >= 0x30 && vk <= 0x39 && !shifted)
                keyChar = (char)vk;
            else if (e.KeySymbol is { Length: > 0 } s && !char.IsControl(s[0]))
                keyChar = s[0];
        }

        Cef.SendKeyEvent(_browserId,
            isKeyUp ? Cef.CefKeyEventType.KeyUp : Cef.CefKeyEventType.RawKeyDown,
            windowsKeyCode: vk, nativeKeyCode: nativeCode,
            modifiers: modifiers,
            character: keyChar, unmodifiedCharacter: keyChar,
            isSystemKey: false);

        // Enter needs a follow-up Char event for the renderer to dispatch a
        // `keypress` and run HTMLInputElement::defaultEventHandler — that's
        // what triggers form submission / button click. RawKeyDown alone
        // fires the DOM keydown but does NOT run the input's default action.
        // Tab does NOT need this (and including it caused literal '\t'
        // insertion previously).
        if (!isKeyUp && e.Key == Key.Enter)
        {
            Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.Char,
                windowsKeyCode: 0x0D, nativeKeyCode: nativeCode,
                modifiers: modifiers,
                character: '\r', unmodifiedCharacter: '\r',
                isSystemKey: false);
        }
    }

    // ---- Public navigation methods ----------------------------------------

    public void GoBack() { if (_browserId > 0) Cef.GoBack(_browserId); }
    public void GoForward() { if (_browserId > 0) Cef.GoForward(_browserId); }
    public void Reload(bool ignoreCache = false) { if (_browserId > 0) Cef.Reload(_browserId, ignoreCache); }
    public void StopLoad() { if (_browserId > 0) Cef.StopLoad(_browserId); }
    public void ShowDevTools() { if (_browserId > 0) Cef.ShowDevTools(_browserId); }
    public void CloseDevTools() { if (_browserId > 0) Cef.CloseDevTools(_browserId); }
    public void ExecuteJavaScript(string code) { if (_browserId > 0) Cef.ExecuteJavaScript(_browserId, code); }

    // ---- Zoom ------------------------------------------------------------
    // Zoom level is in CEF's convention: 0.0 == 100%, each +1.0 step is ~120%
    // of the previous. We use a 0.5 step (≈ 110%) which roughly matches
    // Chrome's discrete zoom levels.
    private const double ZoomStep = 0.5;
    private double _zoomLevel;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set { _zoomLevel = value; if (_browserId > 0) Cef.SetZoomLevel(_browserId, value); }
    }
    public void ZoomIn() => ZoomLevel = _zoomLevel + ZoomStep;
    public void ZoomOut() => ZoomLevel = _zoomLevel - ZoomStep;
    public void ResetZoom() => ZoomLevel = 0;

    // ---- Clipboard / editing -------------------------------------------
    public void Copy()      { if (_browserId > 0) Cef.Copy(_browserId); }
    public void Paste()     { if (_browserId > 0) Cef.Paste(_browserId); }
    public void Cut()       { if (_browserId > 0) Cef.Cut(_browserId); }
    public void SelectAll() { if (_browserId > 0) Cef.SelectAll(_browserId); }
    public void Undo()      { if (_browserId > 0) Cef.Undo(_browserId); }
    public void Redo()      { if (_browserId > 0) Cef.Redo(_browserId); }

    public Task<string> EvaluateJavaScriptAsync(string code)
        => _browserId <= 0 ? Task.FromResult("null") : Cef.EvaluateJavaScriptAsync(_browserId, code);

    public Task<bool> PrintToPdfAsync(string path)
        => _browserId <= 0 ? Task.FromResult(false) : Cef.PrintToPdfAsync(_browserId, path);

    /// <summary>
    /// Close the underlying CEF browser and release the bitmap. Idempotent.
    /// Called automatically when the hosted <see cref="Window"/> closes.
    /// </summary>
    public void Close()
    {
        if (_browserId > 0)
        {
            Cef.CloseBrowser(_browserId, forceClose: true);
            _browserId = 0;
        }
        _bitmap?.Dispose();
        _bitmap = null;
    }

    // ---- Avalonia integration ---------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;

        // Subscribe to global Cef events here (not in ctor) so a control that
        // gets created and then dropped without ever attaching doesn't keep
        // itself alive via the static event handler.
        Cef.AddressChanged += OnGlobalAddressChanged;
        Cef.TitleChanged += OnGlobalTitleChanged;
        Cef.LoadingStateChanged += OnGlobalLoadingStateChanged;
        Cef.CursorChanged += OnGlobalCursorChanged;

        if (_browserId > 0) Cef.WasHidden(_browserId, false);

        if (e.Root is Window win)
        {
            _hostedWindow = win;
            win.Closing += OnHostWindowClosing;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Cef.AddressChanged -= OnGlobalAddressChanged;
        Cef.TitleChanged -= OnGlobalTitleChanged;
        Cef.LoadingStateChanged -= OnGlobalLoadingStateChanged;
        Cef.CursorChanged -= OnGlobalCursorChanged;

        _attached = false;
        if (_browserId > 0) Cef.WasHidden(_browserId, true);

        if (_hostedWindow is not null)
        {
            _hostedWindow.Closing -= OnHostWindowClosing;
            _hostedWindow = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostWindowClosing(object? sender, WindowClosingEventArgs e) => Close();

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (!_attached) return size;

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (scale <= 0) scale = 1.0;
        int w = Math.Max(1, (int)finalSize.Width);
        int h = Math.Max(1, (int)finalSize.Height);

        if (_browserId == 0)
        {
            _browserWidth = w;
            _browserHeight = h;
            _renderScale = scale;
            _browserId = Cef.CreateOffscreenBrowser(w, h, (float)scale,
                                                     Url ?? "about:blank", OnCefPaint);
        }
        else
        {
            if (scale != _renderScale)
            {
                Cef.SetDeviceScaleFactor(_browserId, (float)scale);
                _renderScale = scale;
            }
            if (w != _browserWidth || h != _browserHeight)
            {
                _browserWidth = w;
                _browserHeight = h;
                Cef.ResizeOffscreenBrowser(_browserId, w, h);
            }
        }

        return size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && !_suppressUrlChange && _browserId > 0)
        {
            var newUrl = change.GetNewValue<string?>();
            if (!string.IsNullOrEmpty(newUrl)) Cef.LoadUrl(_browserId, newUrl);
        }
    }

    // ---- Paint pipeline ---------------------------------------------------

    private void OnCefPaint(int id, IntPtr buffer, int width, int height)
    {
        if (id != _browserId) return;

        int byteCount = width * height * 4;
        // Per-paint pooled buffer captured into the dispatcher closure. Avoids
        // a race where CEF could overwrite a shared staging buffer before the
        // prior dispatcher post completes.
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(buffer, snapshot, 0, byteCount);

        // Snapshot the render scale alongside the buffer — the bitmap's DPI
        // vector must match the scale that CEF actually rendered at, even if
        // ApplyPendingResize has already updated _renderScale for the next
        // size pass.
        int w = width, h = height;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_browserId == 0) return;
                if (_bitmap is null || _bitmapWidth != w || _bitmapHeight != h)
                {
                    _bitmap?.Dispose();
                    // Bitmap holds the raw paint buffer at its actual pixel
                    // dimensions. DPI=96 means bitmap.Size in DIPs equals its
                    // pixel size; Render() stretches to Bounds.Size, but with
                    // enough source pixels (= physical-pixel buffer) to map
                    // 1:1 to physical on HiDPI. Sharp without DPI metadata
                    // tricks.
                    _bitmap = new WriteableBitmap(
                        new PixelSize(w, h),
                        new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    _bitmapWidth = w;
                    _bitmapHeight = h;
                }

                using (var locked = _bitmap.Lock())
                {
                    Marshal.Copy(snapshot, 0, locked.Address, byteCount);
                }
                InvalidateVisual();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(snapshot);
            }
        }, DispatcherPriority.Render);
    }

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is not null)
        {
            // Stretch the bitmap to fill the control's bounds. During a fast
            // drag the bitmap may briefly be at the previous size (CEF's
            // paint latency is ~30-100ms after WasResized); stretching means
            // content always visually fills the window at the cost of brief
            // transient distortion. Once the new paint lands, the bitmap is
            // recreated at the right pixel size and the distortion clears.
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
        }
        else
        {
            context.FillRectangle(Avalonia.Media.Brushes.Black, new Rect(Bounds.Size));
        }
    }

    // ---- Browser event routing --------------------------------------------

    private void OnGlobalAddressChanged(object? sender, Cef.BrowserStringEventArgs e)
    {
        if (e.BrowserId != _browserId) return;
        Dispatcher.UIThread.Post(() =>
        {
            _suppressUrlChange = true;
            try { SetCurrentValue(UrlProperty, e.Value); }
            finally { _suppressUrlChange = false; }
        });
    }

    private void OnGlobalTitleChanged(object? sender, Cef.BrowserStringEventArgs e)
    {
        if (e.BrowserId != _browserId) return;
        Dispatcher.UIThread.Post(() => Title = e.Value);
    }

    private void OnGlobalLoadingStateChanged(object? sender, Cef.LoadingStateEventArgs e)
    {
        if (e.BrowserId != _browserId) return;
        Dispatcher.UIThread.Post(() =>
        {
            IsLoading = e.IsLoading;
            CanGoBack = e.CanGoBack;
            CanGoForward = e.CanGoForward;
        });
    }

    private void OnGlobalCursorChanged(object? sender, Cef.CursorChangedEventArgs e)
    {
        if (e.BrowserId != _browserId) return;
        // Construct the Avalonia Cursor on the UI thread — Cursor wraps a
        // platform handle that is created lazily on first use, and creating
        // it on a CEF worker thread can produce a handle that doesn't render
        // (manifests as an invisible cursor when hovering over page controls).
        var t = e.Type;
        Dispatcher.UIThread.Post(() => Cursor = MapCursor(t));
    }

    // Cache cursors so we don't allocate a new platform handle on every
    // pointer move when the page rapidly toggles between two cursor types
    // (very common on UI with many small controls). Cursors are cheap but
    // the cache prevents handle thrashing.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Cef.CefCursorType, Cursor> s_cursorCache = new();

    private static Cursor MapCursor(Cef.CefCursorType t) =>
        s_cursorCache.GetOrAdd(t, BuildCursor);

    private static Cursor BuildCursor(Cef.CefCursorType t) => t switch
    {
        Cef.CefCursorType.Pointer                  => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.Cross                    => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.Hand                     => new Cursor(StandardCursorType.Hand),
        Cef.CefCursorType.IBeam                    => new Cursor(StandardCursorType.Ibeam),
        Cef.CefCursorType.Wait                     => new Cursor(StandardCursorType.Wait),
        Cef.CefCursorType.Help                     => new Cursor(StandardCursorType.Help),
        Cef.CefCursorType.EastResize               => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthResize              => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.NorthEastResize          => new Cursor(StandardCursorType.TopRightCorner),
        Cef.CefCursorType.NorthWestResize          => new Cursor(StandardCursorType.TopLeftCorner),
        Cef.CefCursorType.SouthResize              => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.SouthEastResize          => new Cursor(StandardCursorType.BottomRightCorner),
        Cef.CefCursorType.SouthWestResize          => new Cursor(StandardCursorType.BottomLeftCorner),
        Cef.CefCursorType.WestResize               => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthSouthResize         => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.EastWestResize           => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthEastSouthWestResize => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.NorthWestSouthEastResize => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.ColumnResize             => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.RowResize                => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.MiddlePanning            => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.MiddlePanningHorizontal  => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.MiddlePanningVertical    => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.EastPanning              => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.WestPanning              => new Cursor(StandardCursorType.SizeWestEast),
        Cef.CefCursorType.NorthPanning             => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.SouthPanning             => new Cursor(StandardCursorType.SizeNorthSouth),
        Cef.CefCursorType.NorthEastPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.NorthWestPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.SouthEastPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.SouthWestPanning         => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.Move                     => new Cursor(StandardCursorType.SizeAll),
        Cef.CefCursorType.VerticalText             => new Cursor(StandardCursorType.Ibeam),
        Cef.CefCursorType.Cell                     => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.ContextMenu              => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.Alias                    => new Cursor(StandardCursorType.DragLink),
        Cef.CefCursorType.Progress                 => new Cursor(StandardCursorType.AppStarting),
        Cef.CefCursorType.NoDrop                   => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.Copy                     => new Cursor(StandardCursorType.DragCopy),
        Cef.CefCursorType.None                     => new Cursor(StandardCursorType.None),
        Cef.CefCursorType.NotAllowed               => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.ZoomIn                   => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.ZoomOut                  => new Cursor(StandardCursorType.Cross),
        Cef.CefCursorType.Grab                     => new Cursor(StandardCursorType.Hand),
        Cef.CefCursorType.Grabbing                 => new Cursor(StandardCursorType.DragMove),
        Cef.CefCursorType.Custom                   => new Cursor(StandardCursorType.Arrow),
        Cef.CefCursorType.DndNone                  => new Cursor(StandardCursorType.No),
        Cef.CefCursorType.DndMove                  => new Cursor(StandardCursorType.DragMove),
        Cef.CefCursorType.DndCopy                  => new Cursor(StandardCursorType.DragCopy),
        Cef.CefCursorType.DndLink                  => new Cursor(StandardCursorType.DragLink),
        _                                          => new Cursor(StandardCursorType.Arrow),
    };

    // ---- Input forwarding -------------------------------------------------
    // Coordinates are in DIPs / CSS pixels — CEF's view rect is in DIPs and
    // the device scale factor handles the physical-pixel mapping internally.

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_browserId == 0) return;
        Focus();
        // Re-assert browser focus on every click. OnGotFocus only fires for
        // the initial focus transition into the control; a click that moves
        // the page-internal focus from one input to another doesn't trigger
        // it, leaving CEF's caret-blink state stalled. SetFocus(true) is
        // idempotent so calling it repeatedly is fine.
        Cef.SetBrowserFocus(_browserId, true);
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseClick(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapPointerUpdateKind(p.Properties.PointerUpdateKind),
            mouseUp: false, e.ClickCount,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseClick(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapInitiatingButton(e.InitialPressMouseButton),
            mouseUp: true, 1,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        const int linePixels = 40;
        Cef.SendMouseWheel(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            (int)(e.Delta.X * linePixels),
            (int)(e.Delta.Y * linePixels),
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (_browserId > 0) Cef.SetBrowserFocus(_browserId, true);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_browserId > 0)
        {
            Cef.ImeCancel(_browserId);
            Cef.SetBrowserFocus(_browserId, false);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_browserId == 0) return;

        // Tab moves focus to a new element on KeyDown. Sending the KeyUp to
        // the renderer afterwards makes Chromium *synthesize a KeyDown* on
        // the now-focused element (so the keyup has a matching keydown),
        // and that synthesized keydown's default action fires Tab navigation
        // a second time. Net: one Tab press = two focus moves. Suppress
        // the KeyUp for Tab specifically. Other keys (Enter, arrows, etc.)
        // typically don't move focus, so their KeyUp is needed for the
        // default action to complete (e.g. Enter activating a button click).
        if (e.Key == Key.Tab)
        {
            // Tab's RawKeyDown was forwarded in OnKeyDownTunnel; reset the
            // flag here so the next printable key in OnTextInput correctly
            // synthesizes its own RawKeyDown (otherwise the first char after
            // a Tab is sent as Char-only and Chromium drops it).
            _keyDownForwarded = false;
            e.Handled = true;
            return;
        }

        ForwardKeyToBrowser(e, isKeyUp: true);
        _keyDownForwarded = false;
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_browserId == 0 || string.IsNullOrEmpty(e.Text)) return;
        foreach (char c in e.Text)
        {
            // Skip control characters (Tab '\t', Enter '\r'/'\n', Escape, etc.)
            // — Avalonia on macOS fires OnTextInput for these as well as
            // OnKeyDown, but Chromium will run their editor commands twice if
            // we send both a RawKeyDown and a Char. We rely on OnKeyDown
            // alone for those.
            if (char.IsControl(c)) continue;

            // If Avalonia didn't fire KeyDown for this key (the macOS path for
            // printable chars), synthesize a RawKeyDown so the renderer pairs
            // a keydown with the upcoming keyup. Required for default actions
            // anchored on keydown (button-active-on-Space, etc.).
            if (!_keyDownForwarded)
            {
                int synthVk = (c >= 'a' && c <= 'z') ? c - 32 : c;
                int synthNative = OperatingSystem.IsMacOS() ? CharToMacKeyCode(c) : 0;
                Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.RawKeyDown,
                    windowsKeyCode: synthVk, nativeKeyCode: synthNative,
                    Cef.CefModifiers.None,
                    character: c, unmodifiedCharacter: c,
                    isSystemKey: false);
                _keyDownForwarded = true;
            }

            Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.Char,
                windowsKeyCode: c, nativeKeyCode: 0,
                Cef.CefModifiers.None,
                character: c, unmodifiedCharacter: c,
                isSystemKey: false);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Map a printable character to its macOS Carbon HIToolbox keycode for
    /// use as <c>CefKeyEvent.native_key_code</c>. Used when synthesizing a
    /// RawKeyDown from OnTextInput, where Avalonia hasn't given us a Key.
    /// Without a correct native_key_code, Chromium's NSEventKeyCodeToDomKey
    /// returns the wrong DOM <c>code</c> (e.g. <c>code=KeyA</c> for every
    /// letter, since native_key_code=0 == kVK_ANSI_A).
    /// </summary>
    private static int CharToMacKeyCode(char c)
    {
        if (c >= 'a' && c <= 'z') c = (char)(c - 32);
        return c switch
        {
            ' ' => 0x31,
            'A' => 0x00, 'B' => 0x0B, 'C' => 0x08, 'D' => 0x02,
            'E' => 0x0E, 'F' => 0x03, 'G' => 0x05, 'H' => 0x04,
            'I' => 0x22, 'J' => 0x26, 'K' => 0x28, 'L' => 0x25,
            'M' => 0x2E, 'N' => 0x2D, 'O' => 0x1F, 'P' => 0x23,
            'Q' => 0x0C, 'R' => 0x0F, 'S' => 0x01, 'T' => 0x11,
            'U' => 0x20, 'V' => 0x09, 'W' => 0x0D, 'X' => 0x07,
            'Y' => 0x10, 'Z' => 0x06,
            '0' => 0x1D, '1' => 0x12, '2' => 0x13, '3' => 0x14,
            '4' => 0x15, '5' => 0x17, '6' => 0x16, '7' => 0x1A,
            '8' => 0x1C, '9' => 0x19,
            _ => 0,
        };
    }
}
