using System.Text;
using System.Text.Json;

namespace Exclr8Cef.Cdp;

/// <summary>
/// Typed client for CDP's <c>Page</c> domain — page lifecycle events,
/// rich screenshot options (clip + beyondViewport), and other
/// page-scoped operations. Reached via <see cref="CefBrowser.Page"/>.
/// </summary>
public sealed class PageClient : CdpDomainClient
{
    internal PageClient(CefBrowser browser) : base(browser) { }

    /// <inheritdoc />
    protected override string DomainName => "Page";

    // ---- Lifecycle events -------------------------------------------

    /// <summary>
    /// Per-frame lifecycle markers from Blink: <c>init</c>,
    /// <c>firstPaint</c>, <c>firstContentfulPaint</c>,
    /// <c>firstMeaningfulPaint</c>, <c>DOMContentLoaded</c>, <c>load</c>,
    /// <c>networkIdle</c>, <c>networkAlmostIdle</c>. Use this in place
    /// of <see cref="CefBrowser.LoadEnd"/> for SPA-aware "page is
    /// settled" detection (<c>LoadEnd</c> barely fires on SPAs;
    /// lifecycle events fire continuously).
    /// </summary>
    /// <remarks>
    /// Requires <see cref="SetLifecycleEventsEnabledAsync"/> = true.
    /// CDP doesn't auto-enable; calling that once after the page is
    /// loaded is enough — the setting sticks across navigations.
    /// </remarks>
    public event EventHandler<LifecycleEventArgs>? LifecycleEvent
    {
        add { EnsureEventSubscription(); _lifecycle += value; }
        remove { _lifecycle -= value; }
    }
    private EventHandler<LifecycleEventArgs>? _lifecycle;

    /// <summary>
    /// Turn lifecycle-event reporting on/off. Off by default — call
    /// once after subscribing to <see cref="LifecycleEvent"/>.
    /// </summary>
    public Task SetLifecycleEventsEnabledAsync(bool enabled = true)
        => Browser.ExecuteDevToolsMethodAsync(
            "Page.setLifecycleEventsEnabled",
            "{\"enabled\":" + (enabled ? "true" : "false") + "}");

    /// <inheritdoc />
    protected override void DispatchEvent(string method, string json)
    {
        switch (method)
        {
            case "Page.lifecycleEvent":
                if (_lifecycle is null) return;
                var p = CdpJson.ParseEventParams(json);
                _lifecycle.Invoke(this, new LifecycleEventArgs(
                    p.TryGetProperty("frameId", out var fid) ? fid.GetString() ?? "" : "",
                    p.TryGetProperty("loaderId", out var lid) ? lid.GetString() ?? "" : "",
                    p.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    p.TryGetProperty("timestamp", out var ts) ? ts.GetDouble() : 0));
                break;
        }
    }

    // ---- Screenshot -------------------------------------------------

    /// <summary>
    /// CDP <c>Page.captureScreenshot</c> with the full option surface —
    /// element-clipped screenshots, full-page (beyond viewport),
    /// speed/quality trade-off. Pair with <see cref="DomClient.GetBoxModelAsync"/>
    /// to screenshot just one element.
    /// </summary>
    /// <param name="format">"png" (default) | "jpeg" | "webp".</param>
    /// <param name="quality">0–100, for jpeg/webp only (ignored for png).</param>
    /// <param name="clip">Optional sub-region in CSS pixels; null = whole viewport.</param>
    /// <param name="captureBeyondViewport">If true, captures the full document height (true full-page).</param>
    /// <param name="optimizeForSpeed">If true, trades fidelity for capture speed (good for streaming).</param>
    /// <returns>Raw image bytes ready to write to disk or hand to a vision API.</returns>
    /// <remarks>
    /// Continuations run on the .NET threadpool, NOT the CEF UI thread —
    /// marshal back before touching browser state.
    /// </remarks>
    public async Task<byte[]> CaptureScreenshotAsync(
        string format = "png",
        int? quality = null,
        ScreenshotClip? clip = null,
        bool captureBeyondViewport = false,
        bool optimizeForSpeed = false)
    {
        var sb = new StringBuilder("{\"format\":\"").Append(format).Append('"');
        if (quality is int q) sb.Append(",\"quality\":").Append(q);
        if (captureBeyondViewport) sb.Append(",\"captureBeyondViewport\":true");
        if (optimizeForSpeed) sb.Append(",\"optimizeForSpeed\":true");
        if (clip is ScreenshotClip c)
        {
            sb.Append(",\"clip\":{\"x\":").Append(c.X.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"y\":").Append(c.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"width\":").Append(c.Width.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"height\":").Append(c.Height.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(",\"scale\":").Append(c.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append('}');
        }
        sb.Append('}');

        var reply = await Browser.ExecuteDevToolsMethodAsync("Page.captureScreenshot", sb.ToString());
        var result = CdpJson.ParseResult(reply);
        return Convert.FromBase64String(result.GetProperty("data").GetString() ?? "");
    }
}

/// <summary>
/// A CSS-pixel rectangle for <see cref="PageClient.CaptureScreenshotAsync"/>.
/// <paramref name="Scale"/> is the device-pixel multiplier (1.0 = 1
/// physical pixel per CSS pixel; 2.0 = retina). Defaults to 1.
/// </summary>
public readonly record struct ScreenshotClip(double X, double Y, double Width, double Height, double Scale = 1.0);

/// <summary>Args for <see cref="PageClient.LifecycleEvent"/>.</summary>
public sealed class LifecycleEventArgs : EventArgs
{
    /// <summary>The frame id this event fired on. Main frame is the typical case.</summary>
    public string FrameId { get; }
    /// <summary>Loader id (a CDP id distinct from frame id).</summary>
    public string LoaderId { get; }
    /// <summary>One of: init, firstPaint, firstContentfulPaint, firstMeaningfulPaint, DOMContentLoaded, load, networkIdle, networkAlmostIdle.</summary>
    public string Name { get; }
    /// <summary>Monotonic timestamp in seconds since CDP attachment.</summary>
    public double Timestamp { get; }

    internal LifecycleEventArgs(string frameId, string loaderId, string name, double timestamp)
    {
        FrameId = frameId; LoaderId = loaderId; Name = name; Timestamp = timestamp;
    }
}
