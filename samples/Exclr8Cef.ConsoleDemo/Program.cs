// Stage 4a console demo: drives the full Exclr8CEF lifecycle from .NET.
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

Cef.Initialize(args, helperPath);
Cef.CreateBrowser("chrome://version");
Cef.RunMessageLoop();
Cef.Shutdown();

Console.WriteLine("Exclr8Cef shut down cleanly.");
