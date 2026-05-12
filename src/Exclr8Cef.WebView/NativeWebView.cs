using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Exclr8Cef.WebView;

/// <summary>
/// Avalonia control hosting an embedded Chromium browser as a native child
/// widget (NSView on macOS, HWND on Windows). Sister control to <see cref="WebView"/>:
/// same Avalonia property surface and same underlying <see cref="CefBrowser"/>
/// event/command surface, but renders via the platform's compositor (GPU
/// path) instead of off-screen → <c>WriteableBitmap</c>.
///
/// Trade-offs vs. <see cref="WebView"/>:
/// <list type="bullet">
///   <item>Faster for media / canvas / WebGL — no per-frame pixel copy</item>
///   <item>Native widget owns its pointer / keyboard / IME / cursor — no
///         forwarding needed, no IME plumbing duplicated</item>
///   <item>Avalonia rendering effects (Opacity, RenderTransform, …) only
///         affect the frame, not the embedded content (the native widget
///         is on top of Avalonia's composited layer)</item>
///   <item>Pop-up / overlay z-ordering can be awkward — Avalonia overlays
///         draw below the native widget</item>
/// </list>
///
/// Process-init requirement: the host must call
/// <see cref="Cef.InitializeForOsr"/> (or any init that enables Alloy +
/// external message pump) before the first <see cref="NativeWebView"/> is
/// laid out. Mixing this control with <see cref="WebView"/> in one app is
/// supported under that same init choice.
///
/// Linux/X11 is not yet wired in the native shim — only macOS and Windows.
/// </summary>
public class NativeWebView : NativeControlHost
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<NativeWebView, string?>(nameof(Url), "about:blank");

    public static readonly DirectProperty<NativeWebView, string> TitleProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, string>(
            nameof(Title), o => o.Title);

    public static readonly DirectProperty<NativeWebView, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
            nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<NativeWebView, bool> CanGoBackProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
            nameof(CanGoBack), o => o.CanGoBack);

    public static readonly DirectProperty<NativeWebView, bool> CanGoForwardProperty =
        AvaloniaProperty.RegisterDirect<NativeWebView, bool>(
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
    /// The underlying tech-neutral browser. Null between control creation
    /// and the first arrange (when the native widget is attached). Hosts
    /// that need the full event/command surface bind to this directly —
    /// the control exposes only the Avalonia-friendly slice.
    /// </summary>
    public CefBrowser? Browser => _browser;

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    public int BrowserId => _browser?.Id ?? 0;

    /// <summary>
    /// Fires once when <see cref="Browser"/> is populated. Use this to
    /// subscribe to per-browser events (ConsoleMessage, FileDialog, etc.)
    /// that aren't mirrored as Avalonia properties on the control.
    /// </summary>
    public event EventHandler? BrowserReady;

    // Native handle returned by excef_create_embedded_host. NSView*/HWND
    // depending on platform; treated as an opaque IntPtr at this layer.
    private IntPtr _hostView;
    private CefBrowser? _browser;
    private bool _browserAttached;
    private bool _suppressUrlChange;
    private int _lastWidth;
    private int _lastHeight;

    // Avalonia's NativeControlHost expects a platform-specific handle-kind
    // string. The native shim returns the matching widget on each platform
    // — we just label it correctly. (Linux/X11 would be "XID" but the shim
    // doesn't implement it yet.)
    private static string PlatformHandleKind =>
        OperatingSystem.IsWindows() ? "HWND" :
        OperatingSystem.IsMacOS()   ? "NSView" :
        "XID";

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // Phase 1: create an empty native widget. Avalonia will parent it
        // into the visual tree before ArrangeOverride fires — at which
        // point the effective DPI / backing-scale is known and we can
        // attach the browser without it locking to a wrong DSF.
        var bounds = Bounds;
        int w = (int)bounds.Width  > 0 ? (int)bounds.Width  : 800;
        int h = (int)bounds.Height > 0 ? (int)bounds.Height : 600;
        _hostView = Cef.CreateEmbeddedHost(w, h);
        return new PlatformHandle(_hostView, PlatformHandleKind);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (_hostView == IntPtr.Zero) return size;
        int w = Math.Max(1, (int)finalSize.Width);
        int h = Math.Max(1, (int)finalSize.Height);

        if (!_browserAttached)
        {
            // Phase 2: the native widget is now parented and CEF can pick
            // up the correct backing-scale factor at browser-creation time.
            _browserAttached = true;
            _lastWidth = w;
            _lastHeight = h;
            var browser = Cef.AttachEmbeddedBrowser(_hostView, w, h, Url ?? "about:blank");
            if (browser is not null)
            {
                _browser = browser;
                SubscribeBrowserEvents(browser);
                BrowserReady?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (w != _lastWidth || h != _lastHeight)
        {
            _lastWidth = w;
            _lastHeight = h;
            Cef.ResizeBrowserView(_hostView, w, h);
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

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        // The CefBrowser is bound to the native widget's lifetime; once
        // Avalonia tears down the widget, CEF will fire OnBeforeClose for
        // the underlying browser. Unsubscribe so the dead browser ref
        // doesn't keep our property handlers alive.
        if (_browser is { } b)
        {
            UnsubscribeBrowserEvents(b);
            try { b.Close(force: false); } catch { /* already closing */ }
        }
        _browser = null;
        _browserAttached = false;
        _hostView = IntPtr.Zero;
        base.DestroyNativeControlCore(control);
    }

    // ---- Browser event routing → Avalonia property updates ------------

    private void SubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged      += OnBrowserAddressChanged;
        b.TitleChanged        += OnBrowserTitleChanged;
        b.LoadingStateChanged += OnBrowserLoadingStateChanged;
    }

    private void UnsubscribeBrowserEvents(CefBrowser b)
    {
        b.AddressChanged      -= OnBrowserAddressChanged;
        b.TitleChanged        -= OnBrowserTitleChanged;
        b.LoadingStateChanged -= OnBrowserLoadingStateChanged;
    }

    private void OnBrowserAddressChanged(object? sender, string url)
    {
        // Hop to UI thread; CEF fires these from its own UI thread, which
        // on our setup is the same as Avalonia's main thread when running
        // under InitializeForOsr — but we Post anyway to be defensive
        // against external-pump variants firing from elsewhere.
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
}
