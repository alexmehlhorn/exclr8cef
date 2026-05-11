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
/// off-screen rendering path. Owns a <see cref="CefBrowser"/> instance
/// (exposed via the <see cref="Browser"/> property) — hosts that need the
/// full per-browser event surface (console messages, downloads, dialogs,
/// …) subscribe to events on <c>webView.Browser</c> directly rather than
/// duplicating each event on the control.
///
/// The control owns the Avalonia-side concerns: paint → WriteableBitmap
/// → Render(), pointer / keyboard / IME forwarding, cursor mapping. The
/// underlying browser lifecycle (creation, resize, close) is also driven
/// from here for ergonomics, but the <see cref="CefBrowser"/> instance is
/// itself tech-neutral.
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

    /// <summary>
    /// The underlying tech-neutral browser. Null until the first arrange
    /// with non-zero size creates it, and after <see cref="Close"/>.
    /// Hosts that need the full event/command surface should bind to this
    /// directly — the control exposes only Avalonia-friendly slices.
    /// </summary>
    public CefBrowser? Browser => _browser;

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    public int BrowserId => _browser?.Id ?? 0;

    private CefBrowser? _browser;
    // Browser dimensions in DIPs / CSS pixels. The native shim multiplies
    // these by _renderScale (passed via SetDeviceScaleFactor + the create
    // call) to get the physical-pixel paint buffer size.
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
        // Avalonia's KeyboardNavigationHandler ALSO processes Tab.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);

        TextInputMethodClientRequested += (_, e) =>
        {
            _imeClient ??= new WebViewTextInputMethodClient(this);
            e.Client = _imeClient;
        };

        // KeyDown forwarding runs in the Tunnel phase so we claim the event
        // (for Tab, Enter, etc.) before any class handler — chiefly
        // KeyboardNavigationHandler — also processes it.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    // Set when OnKeyDownTunnel forwards a RawKeyDown to the browser; cleared
    // by OnKeyUp. OnTextInput consults this to decide whether to synthesize a
    // RawKeyDown ahead of its Char dispatch — required on macOS, where
    // Avalonia routes printable keys through the text-input system only,
    // never firing KeyDownEvent. Without a matching RawKeyDown the renderer
    // can't run keydown-anchored default actions (button-active-on-Space, …).
    private bool _keyDownForwarded;

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_browser is null) return;

        ForwardKeyToBrowser(e, isKeyUp: false);
        _keyDownForwarded = true;

        // Only claim Handled for keys whose entire behavior is the
        // RawKeyDown's default action (nav, function keys, Cmd shortcuts).
        // For printable keys with modifiers (e.g. Shift+letter) we MUST
        // leave Handled=false so Avalonia continues to its text-input
        // pipeline (interpretKeyEvents on macOS) and OnTextInput fires.
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
        if (_browser is null) return;

        // Cmd / Ctrl shortcuts (zoom, clipboard) — handle here so they don't
        // go to CEF as ordinary key events.
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
                case Key.C:          _browser.Copy();      return;
                case Key.V:          _browser.Paste();     return;
                case Key.X:          _browser.Cut();       return;
                case Key.A:          _browser.SelectAll(); return;
                case Key.Z:
                    if (shiftAccel) _browser.Redo(); else _browser.Undo();
                    return;
                case Key.Y:          _browser.Redo();      return;
            }
        }

        int vk = KeyMap.AvaloniaToWindowsVK(e.Key);
        int nativeCode = OperatingSystem.IsMacOS() ? KeyMap.AvaloniaToMacKeyCode(e.Key) : 0;
        if (nativeCode < 0) nativeCode = 0;
        var modifiers = InputMapping.MapModifiers(e.KeyModifiers);

        bool shifted = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        char keyChar = e.Key switch
        {
            Key.Enter  => '\r',
            Key.Space  => ' ',
            Key.Back   => '\b',
            Key.Escape => (char)27,
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

        _browser.SendKeyEvent(
            isKeyUp ? Cef.CefKeyEventType.KeyUp : Cef.CefKeyEventType.RawKeyDown,
            windowsKeyCode: vk, nativeKeyCode: nativeCode,
            modifiers: modifiers,
            character: keyChar, unmodifiedCharacter: keyChar,
            isSystemKey: false);

        // Enter needs a follow-up Char event for the renderer to dispatch a
        // keypress and run HTMLInputElement::defaultEventHandler — that's
        // what triggers form submission / button click. RawKeyDown alone
        // fires keydown but does NOT run the input's default action.
        if (!isKeyUp && e.Key == Key.Enter)
        {
            _browser.SendKeyEvent(Cef.CefKeyEventType.Char,
                windowsKeyCode: 0x0D, nativeKeyCode: nativeCode,
                modifiers: modifiers,
                character: '\r', unmodifiedCharacter: '\r',
                isSystemKey: false);
        }
    }

    // ---- Avalonia-friendly delegating methods --------------------------
    //
    // Most hosts can use `webView.Browser.GoBack()` etc. directly; these
    // shortcuts exist so XAML data-binding scenarios don't need a null
    // check on Browser when the control isn't yet ready.

    public void GoBack()      => _browser?.GoBack();
    public void GoForward()   => _browser?.GoForward();
    public void Reload(bool ignoreCache = false) => _browser?.Reload(ignoreCache);
    public void StopLoad()    => _browser?.StopLoad();
    public void ShowDevTools()  => _browser?.ShowDevTools();
    public void CloseDevTools() => _browser?.CloseDevTools();
    public void ExecuteJavaScript(string code) => _browser?.ExecuteJavaScript(code);

    private const double ZoomStep = 0.5;
    public double ZoomLevel
    {
        get => _browser?.ZoomLevel ?? 0;
        set { if (_browser is not null) _browser.ZoomLevel = value; }
    }
    public void ZoomIn()    => ZoomLevel = ZoomLevel + ZoomStep;
    public void ZoomOut()   => ZoomLevel = ZoomLevel - ZoomStep;
    public void ResetZoom() => ZoomLevel = 0;

    public void Copy()      => _browser?.Copy();
    public void Paste()     => _browser?.Paste();
    public void Cut()       => _browser?.Cut();
    public void SelectAll() => _browser?.SelectAll();
    public void Undo()      => _browser?.Undo();
    public void Redo()      => _browser?.Redo();

    public Task<string> EvaluateJavaScriptAsync(string code)
        => _browser is null ? Task.FromResult("null") : _browser.EvaluateJavaScriptAsync(code);

    public Task<bool> PrintToPdfAsync(string path)
        => _browser is null ? Task.FromResult(false) : _browser.PrintToPdfAsync(path);

    /// <summary>
    /// Close the underlying CEF browser and release the bitmap. Idempotent.
    /// Called automatically when the hosted <see cref="Window"/> closes.
    /// </summary>
    public void Close()
    {
        if (_browser is not null)
        {
            UnsubscribeBrowserEvents(_browser);
            _browser.Close(force: true);
            _browser = null;
        }
        _bitmap?.Dispose();
        _bitmap = null;
    }

    // ---- Avalonia integration ------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _browser?.WasHidden(false);

        if (e.Root is Window win)
        {
            _hostedWindow = win;
            win.Closing += OnHostWindowClosing;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _browser?.WasHidden(true);

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

        if (_browser is null)
        {
            _browserWidth = w;
            _browserHeight = h;
            _renderScale = scale;
            var browser = Cef.CreateOffscreenBrowser(w, h, (float)scale, Url ?? "about:blank");
            if (browser is not null)
            {
                _browser = browser;
                SubscribeBrowserEvents(browser);
            }
        }
        else
        {
            if (scale != _renderScale)
            {
                _browser.SetDeviceScaleFactor((float)scale);
                _renderScale = scale;
            }
            if (w != _browserWidth || h != _browserHeight)
            {
                _browserWidth = w;
                _browserHeight = h;
                _browser.Resize(w, h);
            }
        }

        return size;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty && !_suppressUrlChange && _browser is not null)
        {
            var newUrl = change.GetNewValue<string?>();
            if (!string.IsNullOrEmpty(newUrl)) _browser.LoadUrl(newUrl);
        }
    }

    // ---- Browser event routing -----------------------------------------

    private void SubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged       += OnBrowserAddressChanged;
        b.TitleChanged         += OnBrowserTitleChanged;
        b.LoadingStateChanged  += OnBrowserLoadingStateChanged;
        b.CursorChanged        += OnBrowserCursorChanged;
        b.Painted              += OnBrowserPainted;
    }

    private void UnsubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged       -= OnBrowserAddressChanged;
        b.TitleChanged         -= OnBrowserTitleChanged;
        b.LoadingStateChanged  -= OnBrowserLoadingStateChanged;
        b.CursorChanged        -= OnBrowserCursorChanged;
        b.Painted              -= OnBrowserPainted;
    }

    private void OnBrowserAddressChanged(object? sender, string url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _suppressUrlChange = true;
            try { SetCurrentValue(UrlProperty, url); }
            finally { _suppressUrlChange = false; }
        });
    }

    private void OnBrowserTitleChanged(object? sender, string title)
        => Dispatcher.UIThread.Post(() => Title = title);

    private void OnBrowserLoadingStateChanged(object? sender, LoadingState s)
        => Dispatcher.UIThread.Post(() =>
        {
            IsLoading = s.IsLoading;
            CanGoBack = s.CanGoBack;
            CanGoForward = s.CanGoForward;
        });

    private void OnBrowserCursorChanged(object? sender, Cef.CefCursorType type)
    {
        // Construct the Avalonia Cursor on the UI thread — Cursor wraps a
        // platform handle that is created lazily on first use, and creating
        // it on a CEF worker thread can produce a handle that doesn't render.
        Dispatcher.UIThread.Post(() => Cursor = MapCursor(type));
    }

    // ---- Paint pipeline ------------------------------------------------

    private void OnBrowserPainted(object? sender, PaintEventArgs e)
    {
        int byteCount = e.Width * e.Height * 4;
        // Per-paint pooled buffer captured into the dispatcher closure. Avoids
        // a race where CEF could overwrite a shared staging buffer before the
        // prior dispatcher post completes.
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(e.Buffer, snapshot, 0, byteCount);

        int w = e.Width, h = e.Height;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_browser is null) return;
                if (_bitmap is null || _bitmapWidth != w || _bitmapHeight != h)
                {
                    _bitmap?.Dispose();
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
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
        }
        else
        {
            context.FillRectangle(Avalonia.Media.Brushes.Black, new Rect(Bounds.Size));
        }
    }

    // ---- Cursor cache --------------------------------------------------

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

    // ---- Input forwarding ----------------------------------------------
    // Coordinates are in DIPs / CSS pixels.

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseMove(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseMove(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers, p.Properties),
            mouseLeave: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_browser is null) return;
        Focus();
        // Re-assert browser focus on every click. OnGotFocus only fires
        // for the initial focus transition into the control; a click that
        // moves the page-internal focus between elements doesn't trigger
        // it, leaving CEF's caret-blink stalled.
        _browser.SetFocus(true);
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseClick(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapPointerUpdateKind(p.Properties.PointerUpdateKind),
            mouseUp: false, e.ClickCount,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        _browser.SendMouseClick(
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapInitiatingButton(e.InitialPressMouseButton),
            mouseUp: true, 1,
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_browser is null) return;
        var p = e.GetCurrentPoint(this);
        const int linePixels = 40;
        _browser.SendMouseWheel(
            (int)p.Position.X, (int)p.Position.Y,
            (int)(e.Delta.X * linePixels),
            (int)(e.Delta.Y * linePixels),
            InputMapping.MapModifiers(e.KeyModifiers));
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _browser?.SetFocus(true);
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        if (_browser is not null)
        {
            _browser.ImeCancel();
            _browser.SetFocus(false);
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_browser is null) return;

        // Tab moves focus on KeyDown. Sending the KeyUp afterwards makes
        // Chromium synthesize a KeyDown on the now-focused element, doubling
        // navigation. Suppress KeyUp for Tab specifically; other keys' KeyUp
        // is needed for the default action to complete (Enter→click etc.).
        if (e.Key == Key.Tab)
        {
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
        if (_browser is null || string.IsNullOrEmpty(e.Text)) return;

        foreach (char c in e.Text)
        {
            // Skip control chars (Tab '\t', Enter '\r', Escape, …) — Avalonia
            // on macOS fires OnTextInput for these as well as OnKeyDown, but
            // sending both RawKeyDown and Char makes Chromium run editor
            // commands twice. We rely on OnKeyDown for those.
            if (char.IsControl(c)) continue;

            // If Avalonia didn't fire KeyDown for this key (the macOS path
            // for printable chars), synthesize a RawKeyDown so the renderer
            // pairs a keydown with the upcoming keyup. Required for default
            // actions anchored on keydown (button-active-on-Space, …).
            if (!_keyDownForwarded)
            {
                int synthVk = (c >= 'a' && c <= 'z') ? c - 32 : c;
                int synthNative = OperatingSystem.IsMacOS() ? CharToMacKeyCode(c) : 0;
                _browser.SendKeyEvent(Cef.CefKeyEventType.RawKeyDown,
                    windowsKeyCode: synthVk, nativeKeyCode: synthNative,
                    Cef.CefModifiers.None,
                    character: c, unmodifiedCharacter: c,
                    isSystemKey: false);
                _keyDownForwarded = true;
            }

            _browser.SendKeyEvent(Cef.CefKeyEventType.Char,
                windowsKeyCode: c, nativeKeyCode: 0,
                Cef.CefModifiers.None,
                character: c, unmodifiedCharacter: c,
                isSystemKey: false);
        }
        e.Handled = true;
    }

    /// <summary>
    /// Map a printable character to its macOS Carbon HIToolbox keycode for
    /// CefKeyEvent.native_key_code. Used when synthesizing a RawKeyDown from
    /// OnTextInput; without a correct native_key_code, Chromium's
    /// NSEventKeyCodeToDomKey returns the wrong DOM <c>code</c>
    /// (e.g. <c>code=KeyA</c> for every letter, since native=0 == kVK_ANSI_A).
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
