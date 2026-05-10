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
/// CEF; surfaces address/title/loading-state changes as Avalonia
/// properties.
///
/// Pointer events: mouse, touch, and pen are all routed through the
/// OnPointer* overrides. Touch and pen are forwarded as left-button
/// mouse events with no gesture support (no pinch-zoom, no two-finger
/// scroll). Suitable for desktop-first hosts.
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
    // Browser dimensions in DIPs. HiDPI sharpness needs CEF's device_scale_factor
    // wired through the native shim's CefScreenInfo callback (TODO: shim change);
    // until then we render at DIP resolution, which is blurry on Retina/HiDPI but
    // keeps page layout matching the visual presentation so clicks land correctly.
    private int _browserWidth;
    private int _browserHeight;
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

        // Provide our IME client when the framework asks for one.
        TextInputMethodClientRequested += (_, e) =>
        {
            _imeClient ??= new WebViewTextInputMethodClient(this);
            e.Client = _imeClient;
        };
    }

    // ---- Public navigation methods ----------------------------------------

    public void GoBack() { if (_browserId > 0) Cef.GoBack(_browserId); }
    public void GoForward() { if (_browserId > 0) Cef.GoForward(_browserId); }
    public void Reload(bool ignoreCache = false) { if (_browserId > 0) Cef.Reload(_browserId, ignoreCache); }
    public void StopLoad() { if (_browserId > 0) Cef.StopLoad(_browserId); }
    public void ShowDevTools() { if (_browserId > 0) Cef.ShowDevTools(_browserId); }
    public void CloseDevTools() { if (_browserId > 0) Cef.CloseDevTools(_browserId); }
    public void ExecuteJavaScript(string code) { if (_browserId > 0) Cef.ExecuteJavaScript(_browserId, code); }

    /// <summary>Evaluate JS and return the result as a JSON string.</summary>
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

        int w = Math.Max(1, (int)finalSize.Width);
        int h = Math.Max(1, (int)finalSize.Height);

        if (_browserId == 0)
        {
            _browserWidth = w;
            _browserHeight = h;
            _browserId = Cef.CreateOffscreenBrowser(w, h, Url ?? "about:blank", OnCefPaint);
        }
        else if (w != _browserWidth || h != _browserHeight)
        {
            _browserWidth = w;
            _browserHeight = h;
            Cef.ResizeOffscreenBrowser(_browserId, w, h);
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
        // the previous race where CEF could overwrite a shared staging buffer
        // before the prior dispatcher post completed.
        byte[] snapshot = ArrayPool<byte>.Shared.Rent(byteCount);
        Marshal.Copy(buffer, snapshot, 0, byteCount);

        int w = width, h = height;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_browserId == 0) return;
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

    // ---- Input forwarding -------------------------------------------------
    // Coordinates are passed straight through in DIPs because CEF's view rect
    // is also configured in DIPs (see ArrangeOverride). When HiDPI lands at
    // the shim level (device_scale_factor in CefScreenInfo) this needs to
    // multiply by the render scale to match.

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers),
            mouseLeave: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId,
            (int)p.Position.X, (int)p.Position.Y,
            InputMapping.MapModifiers(e.KeyModifiers),
            mouseLeave: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_browserId == 0) return;
        Focus();
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_browserId == 0) return;
        Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.RawKeyDown,
            windowsKeyCode: KeyMap.AvaloniaToWindowsVK(e.Key), nativeKeyCode: 0,
            InputMapping.MapModifiers(e.KeyModifiers),
            character: '\0', unmodifiedCharacter: '\0',
            isSystemKey: false);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_browserId == 0) return;
        Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.KeyUp,
            windowsKeyCode: KeyMap.AvaloniaToWindowsVK(e.Key), nativeKeyCode: 0,
            InputMapping.MapModifiers(e.KeyModifiers),
            character: '\0', unmodifiedCharacter: '\0',
            isSystemKey: false);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_browserId == 0 || string.IsNullOrEmpty(e.Text)) return;
        foreach (char c in e.Text)
        {
            // For Char events CEF reads the character from `character`/
            // `unmodified_character`; windows_key_code should be 0 (the
            // previous code passed the char value, which collided with VK
            // codes for arrow/function keys).
            Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.Char,
                windowsKeyCode: 0, nativeKeyCode: 0,
                Cef.CefModifiers.None,
                character: c, unmodifiedCharacter: c,
                isSystemKey: false);
        }
    }
}
