using Exclr8Cef.Cdp;

namespace Exclr8Cef;

/// <summary>
/// CDP-domain accessors on <see cref="CefBrowser"/>. Each property is
/// a lazy singleton — constructed the first time you touch it, never
/// torn down (the underlying browser lifetime governs everything).
/// Hosts that don't use a domain pay zero cost for it.
/// </summary>
public sealed partial class CefBrowser
{
    private PageClient? _page;
    private PerformanceTimelineClient? _perfTimeline;
    private InputClient? _input;
    private DomClient? _dom;
    private AccessibilityClient? _accessibility;
    private DomSnapshotClient? _domSnapshot;
    private OverlayClient? _overlay;

    /// <summary>
    /// CDP <c>Page</c> domain — lifecycle events, screenshot with
    /// clip/beyondViewport, MHTML capture, etc.
    /// </summary>
    public PageClient Page => _page ??= new PageClient(this);

    /// <summary>
    /// CDP <c>PerformanceTimeline</c> domain — LCP, layout-shift,
    /// long-task, paint timing as live events.
    /// </summary>
    public PerformanceTimelineClient PerformanceTimeline
        => _perfTimeline ??= new PerformanceTimelineClient(this);

    /// <summary>
    /// CDP <c>Input</c> domain — high-level input synthesis
    /// (insertText, scroll/tap gestures with momentum, IME).
    /// More reliable than raw key dispatch on real-world sites.
    /// </summary>
    public InputClient Input => _input ??= new InputClient(this);

    /// <summary>
    /// CDP <c>DOM</c> domain — reverse hit-test, element box-model
    /// geometry, content quads, descendant lookup. Substantially more
    /// capable than the JS-injected probe on
    /// <see cref="HitTestAtAsync"/>.
    /// </summary>
    public DomClient Dom => _dom ??= new DomClient(this);

    /// <summary>
    /// CDP <c>Accessibility</c> domain — Chromium's role-labeled,
    /// hierarchical, semantic page model. The single highest-leverage
    /// AI-targeting signal: "button named 'Sign in'" / "textbox
    /// labeled 'Email'" instead of div-soup.
    /// </summary>
    public AccessibilityClient Accessibility
        => _accessibility ??= new AccessibilityClient(this);

    /// <summary>
    /// CDP <c>DOMSnapshot</c> domain — single-call flattened DOM +
    /// layout + computed styles + text across all frames. Replaces
    /// 10–100 round-trips for whole-page analysis.
    /// </summary>
    public DomSnapshotClient DomSnapshot => _domSnapshot ??= new DomSnapshotClient(this);

    /// <summary>
    /// CDP <c>Overlay</c> domain — programmatic element highlighter
    /// (devtools' "inspect" look) + click-to-pick inspect mode.
    /// Useful for human-in-the-loop transparency.
    /// </summary>
    public OverlayClient Overlay => _overlay ??= new OverlayClient(this);
}
