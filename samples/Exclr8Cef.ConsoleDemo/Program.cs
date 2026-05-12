// Stage 4a console demo: drives the full Exclr8CEF lifecycle from .NET.
//
// Default mode: opens chrome://version in a native Chromium window and
// runs the CEF message loop until the user closes it.
//
// Headless screenshot mode (--screenshot OUT.png --url URL):
//   Spins up an offscreen browser, waits for the page to load, captures
//   a PNG via the DevTools protocol (Page.captureScreenshot), and exits.
//   Useful for automated visual diffing / archiving / vision pipelines.
//
// Must be run inside the Exclr8Cef.ConsoleDemo.app bundle produced by
// scripts/build-console-demo.sh — CEF's library loader needs the Chromium
// Embedded Framework framework adjacent to the executable inside a .app.

using Exclr8Cef;

var versions = Cef.GetVersions();
Console.WriteLine($"Exclr8Cef {versions.Shim} — running CEF {versions.Cef} (Chromium {versions.Chromium})");

// Compute the helper subprocess path inside the .app bundle.
// The bundle layout (set up by build-console-demo.sh) is:
//   <App>.app/Contents/MacOS/Exclr8Cef.ConsoleDemo
//   <App>.app/Contents/Frameworks/exclr8cef_demo Helper.app/Contents/MacOS/exclr8cef_demo Helper
// AppContext.BaseDirectory points at Contents/MacOS.
var helperPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory,
    "..", "Frameworks",
    "exclr8cef_demo Helper.app", "Contents", "MacOS",
    "exclr8cef_demo Helper"));

Console.WriteLine($"Helper subprocess: {helperPath}");
Console.WriteLine($"Exists: {File.Exists(helperPath)}");

string? screenshotOut = GetArg(args, "--screenshot");
string? screenshotUrl = GetArg(args, "--url");
int screenshotW = int.TryParse(GetArg(args, "--width"), out var w) ? w : 1280;
int screenshotH = int.TryParse(GetArg(args, "--height"), out var h) ? h : 800;
float screenshotDsf = float.TryParse(GetArg(args, "--dsf"), out var d) ? d : 2.0f;

if (screenshotOut is not null)
{
    if (screenshotUrl is null)
    {
        Console.Error.WriteLine("--screenshot requires --url URL");
        return 2;
    }
    return RunScreenshotMode(args, helperPath, screenshotUrl, screenshotOut,
                              screenshotW, screenshotH, screenshotDsf);
}

Cef.Initialize(args, helperPath);
Cef.CreateBrowser("chrome://version");
Cef.RunMessageLoop();
Cef.Shutdown();

Console.WriteLine("Exclr8Cef shut down cleanly.");
return 0;

static string? GetArg(string[] argv, string name)
{
    for (int i = 0; i < argv.Length; ++i)
    {
        if (argv[i] == name && i + 1 < argv.Length) return argv[i + 1];
        if (argv[i].StartsWith(name + "=", StringComparison.Ordinal))
            return argv[i].Substring(name.Length + 1);
    }
    return null;
}

static int RunScreenshotMode(string[] argv, string helperPath, string url, string outPath,
                              int width, int height, float dsf)
{
    Console.WriteLine($"Headless screenshot: {url} → {outPath} ({width}×{height} @ {dsf}x)");

    // OSR mode + external pump. The schedule callback is a no-op — we pump
    // synchronously in the main loop below. CEF still fires it when async
    // work needs to be drained sooner, but Sleep(10) inside the loop gives
    // us a tight enough cadence for a one-shot screenshot.
    Cef.InitializeForOsr(argv, helperPath, _ => { });

    var browser = Cef.CreateOffscreenBrowser(width, height, dsf, url);
    if (browser is null)
    {
        Console.Error.WriteLine("CreateOffscreenBrowser failed");
        Cef.Shutdown();
        return 3;
    }

    bool done = false;
    bool failed = false;
    DateTime? captureAt = null;     // grace-period gate, set by LoadEnd
    Task<byte[]>? captureTask = null;
    browser.LoadEnd += (_, e) =>
    {
        if (e.HttpStatusCode >= 400)
            Console.WriteLine($"  warning: HTTP {e.HttpStatusCode} from initial load");
        // Tiny grace period for late-running JS / font swap. CDP gives us
        // no general "page-fully-settled" signal — 250ms covers the common
        // cases; consumers needing more should drive their own wait via JS.
        captureAt ??= DateTime.UtcNow.AddMilliseconds(250);
    };
    browser.LoadError += (_, e) =>
    {
        if (e.FailedUrl == url)
        {
            Console.Error.WriteLine($"Load failed: {e.ErrorText} ({e.ErrorCode})");
            failed = true; done = true;
        }
    };

    // Pump from the main thread. CEF requires that ExecuteDevToolsMethod
    // (and most browser-host APIs) be called from the same thread that
    // initialized CEF — so we kick off CapturePageAsync inline here, then
    // poll the resulting Task without awaiting (which would transfer the
    // continuation onto the threadpool and break that contract).
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (!done && DateTime.UtcNow < deadline)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);

        if (captureTask is null && captureAt is DateTime t && DateTime.UtcNow >= t)
        {
            captureTask = browser.CapturePageAsync();
        }

        if (captureTask is { IsCompleted: true } finished)
        {
            try
            {
                var png = finished.GetAwaiter().GetResult();
                File.WriteAllBytes(outPath, png);
                Console.WriteLine($"Wrote {png.Length:N0} bytes to {outPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Capture failed: {ex.Message}");
                failed = true;
            }
            done = true;
        }
    }
    if (!done)
    {
        Console.Error.WriteLine("Timed out after 30s waiting for load + capture");
        failed = true;
    }

    // Drain a few more pump ticks so the browser closes cleanly before Shutdown.
    browser.Close();
    for (int i = 0; i < 100; ++i)
    {
        Cef.DoMessageLoopWork();
        Thread.Sleep(10);
    }
    Cef.Shutdown();
    return failed ? 4 : 0;
}
