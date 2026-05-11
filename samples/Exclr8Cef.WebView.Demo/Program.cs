using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Exclr8Cef;

namespace Exclr8Cef.WebView.Demo;

// Stage 4c (OSR) demo: Avalonia hosts an embedded Chromium browser via
// off-screen rendering. CEF doesn't create native windows; it paints into
// a buffer and we blit it to a WriteableBitmap.

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // On Windows/Linux the same binary serves as the CEF subprocess
        // (renderer / GPU / utility / etc.); CEF re-invokes it with
        // --type=*. Detect that role first and exit early. macOS uses a
        // separate Helper.app bundle so this is a no-op there.
        int subproc = Cef.ExecuteProcess(args);
        if (subproc >= 0) return subproc;

        Console.WriteLine($"Exclr8Cef {Cef.GetVersions().Shim} — OSR Avalonia demo");

        // Resolve helper subprocess path. macOS uses the bundled
        // Helper.app; Windows/Linux re-uses the same exe.
        string? helperPath = null;
        if (OperatingSystem.IsMacOS())
        {
            helperPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "Frameworks",
                "exclr8cef_demo Helper.app", "Contents", "MacOS",
                "exclr8cef_demo Helper"));
            Console.WriteLine($"Helper subprocess: {helperPath} (exists={File.Exists(helperPath)})");
        }

        // Avalonia first so libAvaloniaNative claims duplicated obj-c
        // class names before CEF loads (macOS-specific concern; harmless
        // elsewhere).
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
        BuildAvaloniaApp().SetupWithLifetime(lifetime);

        // Configure CEF init settings before the InitializeForOsr call.
        // Settings applied after init have no effect on this process.
        //
        // UA strategy: set the full UserAgent (instead of UserAgentProduct,
        // which REPLACES the "Chrome/…" token in CEF's default UA and
        // makes sites think we're not Chromium at all). The full string
        // keeps Chrome's identifier and appends our app token — the
        // pattern Slack / Discord / etc. use for Electron-based apps.
        var v = Cef.GetVersions();
        var platform = OperatingSystem.IsMacOS()
            ? "(Macintosh; Intel Mac OS X 10_15_7)"
            : OperatingSystem.IsWindows()
                ? "(Windows NT 10.0; Win64; x64)"
                : "(X11; Linux x86_64)";
        Cef.SetInitSettings(new Cef.CefSettings
        {
            CachePath = Path.Combine(AppContext.BaseDirectory, "cef-cache"),
            UserAgent = $"Mozilla/5.0 {platform} AppleWebKit/537.36 (KHTML, like Gecko) "
                      + $"Chrome/{v.Chromium} Safari/537.36 Exclr8CefDemo/{v.Shim}",
            PersistSessionCookies = true,
            LogSeverity = Cef.CefLogSeverity.Warning,
        });

        // Register `app://` custom scheme so the demo's static assets can be
        // served from disk via a real host/path URL. The CEF forum
        // consensus is to use STANDARD alone for top-level navigation —
        // SECURE / CORS_ENABLED add security-policy checks that can abort
        // navigation to the custom scheme with net::ERR_ABORTED. Add them
        // later if you actually need secure-context or cross-origin
        // semantics for your scheme.
        Cef.RegisterCustomScheme("app", standard: true, secure: false, corsEnabled: false);
        Cef.SchemeRequest += OnSchemeRequest;

        // CEF in OSR mode (windowless rendering + external pump).
        Cef.InitializeForOsr(args, helperPath, SchedulePumpWork);

        try
        {
            return lifetime.Start(args);
        }
        finally
        {
            Cef.Shutdown();
        }
    }

    // Stage 4c uses a steady ~60Hz timer to drive CEF rather than
    // honoring the SchedulePumpWork delay precisely. Simpler, more robust
    // for static + animated content. Production code can switch to a
    // deduplicated dispatcher timer keyed off SchedulePumpWork.
    private static void SchedulePumpWork(long delayMs)
    {
        // No-op: the AfterSetup timer drives CEF unconditionally.
    }

    /// <summary>
    /// Resolve <c>app://</c> URLs from the bundle's MacOS dir. Acts like a
    /// minimal static-file server — production hosts would map URLs to
    /// embedded resources (<c>EmbeddedResource</c> in the csproj) or a
    /// custom asset bundle instead of disk.
    /// </summary>
    private static void OnSchemeRequest(object? sender, Cef.SchemeRequestEventArgs e)
    {
        // URL shape: app://demo/<path>. Anything else → 404.
        if (!e.Url.StartsWith("app://demo/", StringComparison.Ordinal))
        {
            e.NotFound();
            return;
        }
        var rel = e.Url["app://demo/".Length..];
        var path = Path.Combine(AppContext.BaseDirectory, rel);
        if (!File.Exists(path)) { e.NotFound(); return; }

        var body = File.ReadAllBytes(path);
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css"            => "text/css; charset=utf-8",
            ".js"             => "application/javascript; charset=utf-8",
            ".json"           => "application/json; charset=utf-8",
            ".png"            => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg"            => "image/svg+xml",
            ".woff2"          => "font/woff2",
            ".woff"           => "font/woff",
            _                 => "application/octet-stream",
        };
        e.Continue(body, mime);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .AfterSetup(_ =>
            {
                var timer = new DispatcherTimer(
                    TimeSpan.FromMilliseconds(16),
                    DispatcherPriority.Background,
                    (_, _) => Cef.DoMessageLoopWork());
                timer.Start();
            });

}
