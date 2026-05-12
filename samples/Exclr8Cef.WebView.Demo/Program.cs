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

        // --mode=windowed: pure CEF Chrome-runtime browser window (no Avalonia, no OSR).
        // --mode=embedded: Avalonia hosts an embedded Alloy CEF browser via native NSView.
        // Default: OSR Avalonia demo.
        if (args.Any(a => a.Equals("--mode=windowed", StringComparison.OrdinalIgnoreCase)))
            return RunWindowed(args);
        if (args.Any(a => a.Equals("--mode=embedded", StringComparison.OrdinalIgnoreCase)))
            return RunEmbedded(args);

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
        // served from disk via a real host/path URL.
        // secure=true is required to expose secure-context web APIs on
        // app:// pages (navigator.mediaDevices, Notification, geolocation).
        // Without secure=true Chromium hides navigator.mediaDevices entirely.
        // standard=true alone keeps top-level navigation working; secure on
        // top of that is safe for our usage (no cross-origin / CORS needed).
        Cef.RegisterCustomScheme("app", standard: true, secure: true, corsEnabled: false);
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

    // Alloy-runtime browser hosted inside an Avalonia NativeControlHost.
    // CEF renders into a real NSView/HWND that Avalonia positions as a
    // child of the host window. Hardware-accelerated by Chromium, no
    // paint-buffer copy. But: Alloy only, so no Chrome permission UI.
    private static int RunEmbedded(string[] args)
    {
        Console.WriteLine($"Exclr8Cef {Cef.GetVersions().Shim} — embedded (native + Avalonia host) demo");
        // Do NOT touch Dispatcher.UIThread before BuildAvaloniaApp.SetupWithLifetime —
        // it lazily initialises and accessing it early leaves it in a no-platform
        // state that breaks Dispatcher.MainLoop later.

        string? helperPath = null;
        if (OperatingSystem.IsMacOS())
        {
            helperPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "Frameworks",
                "exclr8cef_demo Helper.app", "Contents", "MacOS",
                "exclr8cef_demo Helper"));
        }

        // Avalonia first — otherwise CEF's ExtensionDropdownHandler obj-c
        // class collides with libAvaloniaNative and crashes.
        // Override the default MainWindow factory so App.axaml.cs doesn't
        // create an OSR MainWindow during framework init.
        // Honor an optional --url= override so spawned instances can open
        // a specific page (e.g. the "New: pure Chrome" button passes
        // --url=https://www.exclr8.co.za).
        var urlOverride = args.FirstOrDefault(a => a.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))?.Substring("--url=".Length);
        App.MainWindowFactory = () => new EmbeddedHostWindow(urlOverride);
        // Strip --mode= args before passing to Avalonia (Avalonia's arg
        // parser doesn't recognise it and may misbehave on macOS).
        var avalArgs = args.Where(a => !a.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase)).ToArray();
        var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = avalArgs };
        BuildAvaloniaApp().SetupWithLifetime(lifetime);

        var v = Cef.GetVersions();
        var platform = OperatingSystem.IsMacOS()
            ? "(Macintosh; Intel Mac OS X 10_15_7)"
            : OperatingSystem.IsWindows()
                ? "(Windows NT 10.0; Win64; x64)"
                : "(X11; Linux x86_64)";
        Cef.SetInitSettings(new Cef.CefSettings
        {
            CachePath = Path.Combine(AppContext.BaseDirectory, "cef-cache-embedded"),
            UserAgent = $"Mozilla/5.0 {platform} AppleWebKit/537.36 (KHTML, like Gecko) "
                      + $"Chrome/{v.Chromium} Safari/537.36 Exclr8CefDemo/{v.Shim}",
            PersistSessionCookies = true,
            LogSeverity = Cef.CefLogSeverity.Warning,
        });

        // Same app:// scheme setup as the OSR demo so test-page.html loads.
        Cef.RegisterCustomScheme("app", standard: true, secure: true, corsEnabled: false);
        Cef.SchemeRequest += OnSchemeRequest;

        // Use the OSR init path even though we'll create windowed browsers.
        // `windowless_rendering_enabled = true` forces Alloy runtime globally
        // and prevents CEF from doing the NSApp-claiming work that breaks
        // Avalonia's main loop. Individual browsers can still be windowed
        // (our excef_create_browser_view uses SetAsChild + ALLOY).
        // The pump timer is created by BuildAvaloniaApp().AfterSetup —
        // no extra timer here.
        Cef.InitializeForOsr(args, helperPath, SchedulePumpWork);

        try { return lifetime.Start(avalArgs); }
        finally { Cef.Shutdown(); }
    }

    // Pure-CEF Chrome-runtime windowed mode. No Avalonia, no OSR — CEF
    // owns a real top-level window with the full Chrome browser UI
    // (omnibox, permission bubbles, notification UI). Different runtime,
    // different behavior — useful comparison for permission flows.
    private static int RunWindowed(string[] args)
    {
        Console.WriteLine($"Exclr8Cef {Cef.GetVersions().Shim} — windowed (Chrome runtime) demo");

        string? helperPath = null;
        if (OperatingSystem.IsMacOS())
        {
            helperPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "Frameworks",
                "exclr8cef_demo Helper.app", "Contents", "MacOS",
                "exclr8cef_demo Helper"));
        }

        var v = Cef.GetVersions();
        var platform = OperatingSystem.IsMacOS()
            ? "(Macintosh; Intel Mac OS X 10_15_7)"
            : OperatingSystem.IsWindows()
                ? "(Windows NT 10.0; Win64; x64)"
                : "(X11; Linux x86_64)";
        // Separate cache dir so the OSR parent process can keep its lock
        // on cef-cache/ while we launch with our own profile.
        Cef.SetInitSettings(new Cef.CefSettings
        {
            CachePath = Path.Combine(AppContext.BaseDirectory, "cef-cache-windowed"),
            UserAgent = $"Mozilla/5.0 {platform} AppleWebKit/537.36 (KHTML, like Gecko) "
                      + $"Chrome/{v.Chromium} Safari/537.36 Exclr8CefDemo/{v.Shim}",
            PersistSessionCookies = true,
            LogSeverity = Cef.CefLogSeverity.Warning,
        });

        Cef.RegisterCustomScheme("app", standard: true, secure: true, corsEnabled: false);
        Cef.SchemeRequest += OnSchemeRequest;

        Cef.Initialize(args, helperPath);

        // Optional --url= override; defaults to the demo test page.
        var urlArg = args.FirstOrDefault(a => a.StartsWith("--url=", StringComparison.OrdinalIgnoreCase));
        var url = urlArg is null ? "app://demo/test-page.html" : urlArg.Substring("--url=".Length);
        Cef.CreateBrowser(url);
        Cef.RunMessageLoop();
        Cef.Shutdown();
        return 0;
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
