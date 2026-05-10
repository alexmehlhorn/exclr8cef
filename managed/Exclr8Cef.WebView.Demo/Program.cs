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
