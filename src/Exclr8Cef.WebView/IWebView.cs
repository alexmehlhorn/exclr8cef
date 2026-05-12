namespace Exclr8Cef.WebView;

/// <summary>
/// Common runtime contract for both shipped Exclr8CEF Avalonia controls:
/// <see cref="WebView"/> (OSR — paints into Avalonia's compositor) and
/// <see cref="NativeWebView"/> (embedded — hosts a native NSView/HWND).
///
/// <para>What this interface gives you</para>
///
/// Code-level polymorphism. A ViewModel can hold an <see cref="IWebView"/>
/// reference and switch the underlying concrete control without rewriting
/// the calling code:
///
/// <code>
/// public class TabViewModel
/// {
///     public IWebView Browser { get; set; } = default!;
///     public void Navigate(string url) => Browser.LoadUrl(url);
/// }
/// </code>
///
/// <para>What this interface CAN'T give you</para>
///
/// XAML polymorphism. Avalonia's <c>StyledProperty&lt;T&gt;</c> /
/// <c>DirectProperty&lt;T&gt;</c> are static fields on the concrete control
/// type, so XAML attribute bindings still target <see cref="WebView"/> or
/// <see cref="NativeWebView"/> by their concrete tag name. The interface is
/// for procedural code, not declarative XAML.
///
/// <para>Why no abstract base class</para>
///
/// The two controls derive from different Avalonia framework types
/// (<c>Avalonia.Controls.Control</c> for the OSR-rendered path,
/// <c>Avalonia.Controls.NativeControlHost</c> for the foreign-widget path).
/// C# single inheritance + the framework-imposed hierarchy means a shared
/// abstract base isn't expressible. An interface is the lightest mechanism
/// that captures the contract without forcing either control to swap its
/// rendering model.
///
/// <para>What's intentionally NOT on this interface</para>
///
/// Mode-specific helpers (<c>InvalidateBitmap</c> on the OSR side; future
/// GPU-specific hooks on the embedded side) live on the concrete classes
/// only. Anything beyond the symmetric Avalonia-friendly surface should be
/// reached via <see cref="Browser"/> directly.
/// </summary>
public interface IWebView
{
    // ---- State (XAML-bindable on the concrete types) -----------------

    /// <summary>Two-way: setting navigates; getter follows page navigations.</summary>
    string? Url { get; set; }

    /// <summary>Current document title.</summary>
    string Title { get; }

    /// <summary>True while a navigation is in flight.</summary>
    bool IsLoading { get; }

    bool CanGoBack { get; }
    bool CanGoForward { get; }

    // ---- Identity ----------------------------------------------------

    /// <summary>
    /// The underlying tech-neutral browser. Null until the first arrange
    /// creates it, and after <see cref="Close"/>. Use for anything beyond
    /// the common surface — <c>browser.ConsoleMessage</c>, <c>FileDialog</c>,
    /// <c>AudioPacket</c>, etc.
    /// </summary>
    CefBrowser? Browser { get; }

    /// <summary>Browser id (0 if not yet created or closed). Convenience.</summary>
    int BrowserId { get; }

    /// <summary>
    /// Optional isolated request context (separate cookies / cache / storage
    /// from other browsers). MUST be set before the control is laid out the
    /// first time — after the underlying <see cref="CefBrowser"/> is created,
    /// changes are ignored.
    /// </summary>
    CefRequestContext? RequestContext { get; set; }

    // ---- Lifecycle events -------------------------------------------

    /// <summary>Fires once after the underlying browser is created and attached.</summary>
    event EventHandler? BrowserReady;

    /// <summary>Fires after the underlying browser has been fully closed.</summary>
    event EventHandler? BrowserClosed;

    // ---- Navigation -------------------------------------------------

    /// <summary>Load the given URL. Equivalent to setting <see cref="Url"/>.</summary>
    void LoadUrl(string url);
    void GoBack();
    void GoForward();
    void Reload(bool ignoreCache = false);
    void StopLoad();
    /// <summary>Close the underlying browser. Idempotent.</summary>
    void Close();

    // ---- DevTools ---------------------------------------------------

    void ShowDevTools();
    void CloseDevTools();

    // ---- JavaScript -------------------------------------------------

    void ExecuteJavaScript(string code);
    Task<string> EvaluateJavaScriptAsync(string code);

    // ---- Zoom -------------------------------------------------------

    /// <summary>0.0 = 100%. Each ±1 step ≈ 1.2× (Chromium convention).</summary>
    double ZoomLevel { get; set; }
    void ZoomIn();
    void ZoomOut();
    void ResetZoom();

    // ---- Clipboard / editing ---------------------------------------

    void Copy();
    void Paste();
    void Cut();
    void SelectAll();
    void Undo();
    void Redo();

    // ---- PDF --------------------------------------------------------

    /// <summary>Render the current page as a PDF at <paramref name="path"/>.</summary>
    Task<bool> PrintToPdfAsync(string path);
}
