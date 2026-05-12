# Exclr8CEF

A custom .NET binding to Chromium via CEF, built for applications where rendering quality *is* the product. Ships an Avalonia `WebView` control plus a framework-agnostic core for any .NET host.

- **Same pixels, every platform.** macOS, Windows, Linux all render through the same Chromium build.
- **You control the cadence.** Tracks upstream Chromium within weeks via an auto-PR workflow.
- **Permissive licence**, fully open. MIT on the binding, BSD on CEF.

## Why this exists

| Option | Engine | Issue |
|---|---|---|
| Avalonia 12 NativeWebView | WebView2 / WKWebView / WPE WebKit | Three engines → rendering diverges across platforms |
| CefGlue.Avalonia | Chromium (CEF) | Stuck on Chromium 120 (Dec 2023). Pure-managed P/Invoke + regex header parser → unsustainable |
| DotNetBrowser | Chromium | Commercial, proprietary |
| CefSharp | Chromium | Tracks upstream within weeks, but Windows-only (C++/CLI) |

Exclr8CEF takes CefSharp's shim model and ports it to plain C++ with a C ABI for cross-platform reach. ClangSharp replaces brittle regex header parsing with proper libclang AST analysis — turning every Chromium bump into "regenerate, fix what broke, ship."

## Features

### Rendering

Two Avalonia controls ship in the same `Exclr8Cef.WebView` package — drop either (or both) into your XAML; pick per use case.

- **`WebView` (OSR)** — off-screen rendering. CEF paints into a BGRA buffer → `WriteableBitmap` → Avalonia compositor. Single render surface, Avalonia effects (Opacity, RenderTransform, overlays) work on the content. Good default.
- **`NativeWebView` (embedded)** — `NativeControlHost`-based. CEF paints directly into an NSView (macOS) or HWND (Windows). GPU path, no per-frame pixel copy — faster for video / canvas / WebGL. Trade-off: Avalonia render effects don't apply to the embedded content, and z-ordering with overlays can be awkward (native widget on top).
- **Headless** — no UI required. Drive via `CefBrowser` + DevTools/CDP from a console host.

Both controls expose the same Avalonia property surface (`Url`, `Title`, `IsLoading`, `CanGoBack`, `CanGoForward`) so swapping is mechanical. They can coexist in the same app — see [the init contract note](#process-init-contract).

### Navigation & input

- `LoadUrl`, `LoadRequest`, `LoadString`, `GoBack`, `GoForward`, `Reload`, `StopLoad`, `Close`
- `GetNavigationEntriesAsync` returns the full back/forward stack
- Mouse, wheel, keyboard, IME forwarding
- Clipboard (`Copy`/`Paste`/`Cut`/`SelectAll`/`Undo`/`Redo`)
- Zoom (`ZoomLevel`, `CanZoom`, `GetZoomLevel`)
- Focus management + key-event interception

### Browser events (per-browser, on `CefBrowser`)

Address, title, loading state, load start/end/error, loading progress, console, status, tooltip, favicon, fullscreen, cursor, scroll offset, auto-resize, render-process gone, frame lifecycle (created/attached/detached), main-frame changed, take/got focus, pre-key/key event, audio stream started/packet/stopped/error.

### Dialogs & interaction (intercept and respond, sync or async)

JS dialogs, file dialogs, context menus, auth requests, certificate errors, permission prompts, media-access requests, drag-and-drop (source + target), before-popup, find-result.

### JavaScript bridge

- `EvaluateJavaScriptAsync(code)` — returns the JSON-serialized result
- `ExecuteJavaScript(code)` — fire-and-forget
- Page-side `window.exclr8cef.invoke(method, args)` returns a `Promise` — the .NET host sees a `JsInvoke` event and replies via `e.Reply(json)` / `e.ReplyError(message)`. Both directions awaitable.

### DevTools / Chrome DevTools Protocol

- `ExecuteDevToolsMethodAsync("Page.captureScreenshot", paramsJson)` — universal CDP escape hatch
- `SendDevToolsMessageRaw` for fire-and-forget
- `DevToolsMessage` event for CDP events (Network, Page, DOM, …)
- `CapturePageAsync` is a thin wrapper

### Content interception

- **Custom schemes** — `Cef.RegisterCustomScheme("app", …)` + `SchemeRequest` event for routing scheme:// URLs through .NET
- **Per-URL claim** — `Cef.ShouldHandleResource` + `Cef.ResolveResourceHandlerRequest` serve any URL (http://, https://, file://, …) from the host with full status / mime / headers / body control
- **Streaming response filter** — `Cef.ShouldFilterResponse` + `Cef.ResponseFilter` rewrite response bodies in flight, chunk by chunk, with `ReadOnlySpan<byte>`/`Span<byte>` at the managed surface. Use cases: script injection, CSP stripping, content-type fixups
- **Resource-request header rewrite** — `ResourceRequest` event for per-request URL/header mutation

### Cookies & storage isolation

- Global cookies: `Cef.GetCookiesAsync` / `SetCookie` / `DeleteCookies`
- Per-context: `CefRequestContext.GetCookiesAsync` / `SetCookie` / `DeleteCookies`
- `CefRequestContext` for profile-style isolation (cookies, cache, storage, prefs all partitioned)
- Per-context preferences: `SetPreference`/`GetPreference`/`ClearHttpAuthCredentials`/`CloseAllConnections`

### Audio

- `EnableAudioCapture(true)` opts into tab-audio capture
- Interleaved float-PCM via `AudioPacket` event; `AudioStreamStarted` reports channel layout + sample rate
- Per-browser mute via `AudioMuted` property

### PDF + print

- `PrintToPdfAsync(path)` for default-styled PDF
- `Exclr8Cef.Print` package for full Chromium PDF settings: paper size, margins, scale, page ranges, header/footer HTML templates with `pageNumber` / `totalPages` / `title` / `date` / `url` substitutions

### Accessibility & spellcheck

- Raw Chromium accessibility tree streamed as JSON (`AccessibilityTreeChange` / `LocationChange`) — useful for automation and audits
- `ReplaceMisspelling`, `AddWordToDictionary` for context-menu spellcheck integration

### Vision / automation

For AI hosts that need pixel + DOM-probe access:

- `EnableFrameCapture` + `TryCaptureLastFrame` — synchronous BGRA snapshot
- `FrameStream` — `ChannelReader<PaintFrame>` (bounded, drop-oldest) for an agent loop
- `HitTestAtAsync(x, y)` — returns the DOM element under a point via the JS bridge
- `AcceleratedPaint` event — GPU shared-texture handle (IOSurface / D3D11 / dma-buf) per frame for hosts that want to consume paints without a CPU readback. The handle is platform-specific; the demo's `--accel-paint` mode shows the macOS IOSurface read path end-to-end.
- `Exclr8Cef.ConsoleDemo --url URL --screenshot OUT.png` — headless screenshot CLI

### Chrome runtime (windowed)

- `OnChromeCommand` intercepts IDC_* commands
- Menu, page-action, and toolbar-button visibility hooks
- Useful for kiosks, app-mode windows, locking down the UI

## Architecture

Two NuGet packages, mirroring how `Microsoft.Web.WebView2.Core` and `Microsoft.Web.WebView2.Wpf` are split:

```
Console / WPF / MAUI / ASP.NET / service / etc.        Avalonia desktop app
        │                                                       │
        ▼                                                       ▼
┌─────────────────────────────┐                  ┌─────────────────────────────┐
│ Exclr8Cef                   │ ◄─── refs ────── │ Exclr8Cef.WebView           │
│  ├─ Cef (static facade)     │                  │  └─ WebView (Avalonia ctrl) │
│  └─ Exclr8Cef.Native        │                  └─────────────────────────────┘
│     (internal P/Invoke,     │
│      ClangSharp-generated)  │
└─────────────────────────────┘
        │ DllImport
        ▼
libexclr8cef.{dylib,dll,so}     ← C++ shim, exposes a C ABI under `excef_*`
        │ direct C++ calls
        ▼
libcef.{dylib,dll,so}           ← Chromium Embedded Framework
```

**`Exclr8Cef`** — framework-agnostic. Use from any .NET host. Public surface: `Cef.*` static facade, `CefBrowser`, `CefRequestContext`, `CefVersions`. Raw P/Invokes are `internal`.

**`Exclr8Cef.WebView`** — Avalonia integration. Adds the `WebView` control. Builds against Avalonia 12.

**`Exclr8Cef.Print`** — optional package for advanced PDF print settings.

Subprocess hosting (renderer / GPU / utility) is in C++, matching CEF's reference implementation. No .NET runtime in helper processes.

## Quick start

### Use in an Avalonia app

```bash
dotnet add package Exclr8Cef.WebView
```

```xml
<!-- MainWindow.axaml -->
<Window xmlns:exclr8="clr-namespace:Exclr8Cef.WebView;assembly=Exclr8Cef.WebView">
    <!-- OSR (default; good for most cases) -->
    <exclr8:WebView Url="https://example.com" />

    <!-- or, for media-heavy content, the embedded GPU path: -->
    <!-- <exclr8:NativeWebView Url="https://example.com" /> -->
</Window>
```

```csharp
// Program.cs
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Exclr8Cef;
using Exclr8Cef.WebView;

public static int Main(string[] args)
{
    // Windows / Linux: the same binary serves as the CEF subprocess. macOS uses
    // a separate Helper.app bundle so this is a no-op there.
    int subproc = Cef.ExecuteProcess(args);
    if (subproc >= 0) return subproc;

    var lifetime = new ClassicDesktopStyleApplicationLifetime { Args = args };
    BuildAvaloniaApp().UseExclr8Cef().SetupWithLifetime(lifetime);
    AvaloniaSetup.InitializeForOsr(args);
    try { return lifetime.Start(args); }
    finally { Cef.Shutdown(); }
}

public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>().UsePlatformDetect();
```

That's the whole CEF setup. `AvaloniaSetup.InitializeForOsr` auto-resolves the macOS Helper.app under the running bundle's `Contents/Frameworks/`. `UseExclr8Cef()` chains a 16ms dispatcher timer to drain CEF on the UI thread. Both XAML controls (`cef:WebView`, `cef:NativeWebView`) Just Work under this init.

NuGet's `runtime.json` resolution picks the right native runtime package (`runtime.osx-arm64.Exclr8Cef`, `runtime.win-x64.Exclr8Cef`, …) automatically based on the consumer's `RuntimeIdentifier`.

### Process init contract

CEF is process-global: exactly one init call per process lifetime. The controls themselves don't init — the host picks the init mode, then any combination of `WebView` / `NativeWebView` instances works under that mode.

| Init call | Runtime | OSR (`WebView`) | Embedded (`NativeWebView`) | Windowed Chrome |
|---|---|---|---|---|
| `AvaloniaSetup.InitializeForOsr` (or raw `Cef.InitializeForOsr`) | Alloy global | ✓ | ✓ | ✗ |
| `Cef.Initialize` (default) | Chrome | ✗ | ✗ | ✓ |

OSR init is the right choice for any Avalonia app — it sets `windowless_rendering_enabled = true` (which *enables* OSR without forbidding windowed Alloy browsers) and uses an external message pump so Avalonia stays in charge of the platform run loop. You can then mix `WebView` and `NativeWebView` instances freely in the same window — see `samples/Exclr8Cef.WebView.Demo --mode=compare` for a working example.

### Build from source (macOS arm64)

```bash
# 1. Download CEF (~150 MB)
./scripts/download-cef.sh

# 2. Configure and build
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --parallel

# 3. Smoke test (prints linked CEF + Chromium versions)
./native/build/shim/exclr8cef_version_probe

# 4. Build managed bindings
./scripts/regenerate-bindings.sh
dotnet build src/Exclr8Cef/Exclr8Cef.csproj

# 5. Launch the demo
./scripts/build-avalonia-demo.sh
open samples/Exclr8Cef.WebView.Demo/bin/Release/Exclr8Cef.WebView.Demo.app
```

## Demos

- **`samples/Exclr8Cef.WebView.Demo`** — full Avalonia app, four modes:
  - **default** — OSR via the shipped `cef:WebView`. Full toolbar (◀ ▶ ⟳ ✕ Hit-test Capture-PNG DevTools Isolated Run-JS Save-PDF + zoom), URL bar, sectioned `app://`-served test page exercising every event surface, host-side event console color-coding every fired callback.
  - **`--mode=embedded`** — Avalonia hosts an embedded Alloy CEF browser via `NativeControlHost`. Uses the local `NativeCefView` wrapper to show the minimum manual-integration pattern (the shipped `cef:NativeWebView` does the same with full property bindings).
  - **`--mode=compare`** — *side-by-side*: OSR `cef:WebView` on the left, embedded `cef:NativeWebView` on the right, both shipped controls, one window, one CEF init. Each pane has its own URL bar + back/forward/reload so the two browsers can be navigated independently for visual comparison.
  - **`--mode=windowed`** — pure-CEF Chrome-runtime window (no Avalonia, no OSR). Different runtime, different permission UI; runs as a separate process pattern.
- **`samples/Exclr8Cef.ConsoleDemo`** — .NET-driven CEF host with no UI framework. Three modes:
  - **default** — opens `chrome://version` in a real Chromium window
  - **`--url URL --screenshot OUT.png`** — renders a page headless and writes the PNG via the CDP `Page.captureScreenshot` path. Useful for automation and visual-diff pipelines.
  - **`--url URL --accel-paint OUT.ppm`** *(macOS-only proof-of-wiring)* — renders headless, captures the first paint via CEF's GPU shared-texture handle (IOSurface on macOS), reads pixels straight out of GPU memory, and writes a Netpbm PPM. Demonstrates the `AcceleratedPaint` event end to end; non-macOS hosts wire the equivalent path (D3D11 shared handle on Windows, dma-buf fd on Linux) themselves.

## Repo layout

```
exclr8cef/
├── native/                          # C++ shim and subprocess helper
│   ├── shim/                        # The C ABI surface
│   │   ├── exclr8cef.h              # public C ABI (parsed by ClangSharp)
│   │   ├── exclr8cef.cc             # cross-platform impl
│   │   ├── exclr8cef_mac.mm         # macOS lifecycle + embedded mode
│   │   ├── exclr8cef_win.cc         # Windows embedded mode
│   │   ├── exclr8cef_app.cc         # CefApp + browser creation
│   │   ├── exclr8cef_osr.cc         # OSR handler (load / display / drag / …)
│   │   └── exclr8cef_print.cc       # optional PDF extension
│   ├── helper/                      # Subprocess helper exe (macOS Helper.app)
│   └── demo/                        # Native demo
├── src/                             # shipping .NET projects
│   ├── Exclr8Cef/                   # framework-agnostic package
│   ├── Exclr8Cef.WebView/           # Avalonia integration
│   ├── Exclr8Cef.Print/             # optional PDF extension
│   └── runtime/                     # per-RID native runtime package template
├── samples/                         # example apps
│   ├── Exclr8Cef.ConsoleDemo/
│   └── Exclr8Cef.WebView.Demo/
├── tests/
├── scripts/                         # CEF download, bindgen, demo packaging
└── third_party/cef/<platform>/      # extracted CEF (gitignored, ~150 MB)
```

## CEF version policy

The pinned Chromium / CEF version lives in `cef.json` at the repo root — single source of truth read by every build script and CI workflow.

- `upstream-check.yml` runs daily and opens a `chore(cef): bump to <version>` PR when a newer stable is available
- `ci.yml` builds the 4-RID matrix on every push/PR
- `release.yml` tags trigger a publish of `Exclr8Cef`, `Exclr8Cef.WebView`, `Exclr8Cef.Print`, and the per-RID `runtime.<rid>.Exclr8Cef` packages to nuget.org

## C ABI naming

All exported native symbols use the `excef_` prefix (e.g. `excef_initialize`, `excef_create_browser`). This is the surface ClangSharp parses; keeping it short and consistent matters for the generator output.

## License

MIT — see [LICENSE](LICENSE). CEF itself is BSD; libcef binaries from Spotify CDN are redistributable under CEF's license.
