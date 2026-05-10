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
/// </summary>
public class WebView : Control
{
    public static readonly StyledProperty<string> UrlProperty =
        AvaloniaProperty.Register<WebView, string>(nameof(Url), "about:blank");

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

    public string Url
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

    /// <summary>Browser id assigned by Exclr8Cef (0 if not yet created).</summary>
    public int BrowserId => _browserId;

    private int _browserId;
    private int _browserWidth;
    private int _browserHeight;
    private WriteableBitmap? _bitmap;
    private byte[]? _pixelStaging;
    private int _bitmapWidth;
    private int _bitmapHeight;
    private bool _attached;
    private bool _suppressUrlChange;
    private WebViewTextInputMethodClient? _imeClient;

    public WebView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cef.AddressChanged += OnGlobalAddressChanged;
        Cef.TitleChanged += OnGlobalTitleChanged;
        Cef.LoadingStateChanged += OnGlobalLoadingStateChanged;

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

    // ---- Avalonia integration ---------------------------------------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_browserId > 0)
        {
            Cef.WasHidden(_browserId, true);
        }
        base.OnDetachedFromVisualTree(e);
    }

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
            _browserId = Cef.CreateOffscreenBrowser(w, h, Url, OnCefPaint);
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
            Cef.LoadUrl(_browserId, change.GetNewValue<string>());
        }
    }

    // ---- Paint pipeline ---------------------------------------------------

    private unsafe void OnCefPaint(int id, IntPtr buffer, int width, int height)
    {
        if (id != _browserId) return;

        int byteCount = width * height * 4;
        if (_pixelStaging == null || _pixelStaging.Length < byteCount)
        {
            _pixelStaging = new byte[byteCount];
        }
        Marshal.Copy(buffer, _pixelStaging, 0, byteCount);

        int w = width, h = height;
        Dispatcher.UIThread.Post(() =>
        {
            if (_pixelStaging == null) return;
            if (_bitmap == null || _bitmapWidth != w || _bitmapHeight != h)
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
                Marshal.Copy(_pixelStaging, 0, locked.Address, w * h * 4);
            }
            InvalidateVisual();
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId, (int)p.Position.X, (int)p.Position.Y,
            MapModifiers(e.KeyModifiers), mouseLeave: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseMove(_browserId, (int)p.Position.X, (int)p.Position.Y,
            MapModifiers(e.KeyModifiers), mouseLeave: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_browserId == 0) return;
        Focus();
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseClick(_browserId, (int)p.Position.X, (int)p.Position.Y,
            MapPointerUpdateKind(p.Properties.PointerUpdateKind), mouseUp: false, e.ClickCount,
            MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        Cef.SendMouseClick(_browserId, (int)p.Position.X, (int)p.Position.Y,
            MapInitiatingButton(e.InitialPressMouseButton), mouseUp: true, 1,
            MapModifiers(e.KeyModifiers));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_browserId == 0) return;
        var p = e.GetCurrentPoint(this);
        const int linePixels = 40;
        Cef.SendMouseWheel(_browserId, (int)p.Position.X, (int)p.Position.Y,
            (int)(e.Delta.X * linePixels),
            (int)(e.Delta.Y * linePixels),
            MapModifiers(e.KeyModifiers));
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
            MapModifiers(e.KeyModifiers),
            character: '\0', unmodifiedCharacter: '\0',
            isSystemKey: false);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_browserId == 0) return;
        Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.KeyUp,
            windowsKeyCode: KeyMap.AvaloniaToWindowsVK(e.Key), nativeKeyCode: 0,
            MapModifiers(e.KeyModifiers),
            character: '\0', unmodifiedCharacter: '\0',
            isSystemKey: false);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_browserId == 0 || string.IsNullOrEmpty(e.Text)) return;
        foreach (char c in e.Text)
        {
            Cef.SendKeyEvent(_browserId, Cef.CefKeyEventType.Char,
                windowsKeyCode: c, nativeKeyCode: 0,
                Cef.CefModifiers.None,
                character: c, unmodifiedCharacter: c,
                isSystemKey: false);
        }
    }

    private static Cef.CefModifiers MapModifiers(KeyModifiers km)
    {
        var flags = Cef.CefModifiers.None;
        if ((km & KeyModifiers.Shift) != 0) flags |= Cef.CefModifiers.Shift;
        if ((km & KeyModifiers.Control) != 0) flags |= Cef.CefModifiers.Control;
        if ((km & KeyModifiers.Alt) != 0) flags |= Cef.CefModifiers.Alt;
        if ((km & KeyModifiers.Meta) != 0) flags |= Cef.CefModifiers.Command;
        return flags;
    }

    private static Cef.CefMouseButton MapPointerUpdateKind(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => Cef.CefMouseButton.Middle,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => Cef.CefMouseButton.Right,
        _ => Cef.CefMouseButton.Left,
    };

    private static Cef.CefMouseButton MapInitiatingButton(MouseButton btn) => btn switch
    {
        MouseButton.Right => Cef.CefMouseButton.Right,
        MouseButton.Middle => Cef.CefMouseButton.Middle,
        _ => Cef.CefMouseButton.Left,
    };
}
