# Exclr8CEF

A custom .NET/Avalonia binding to Chromium. Provides an HTML rendering control with a Chromium update cadence you control.

## Why this exists

Existing options for embedding HTML in .NET cross-platform:

| Option | Engine | Issue |
|---|---|---|
| Avalonia 12 NativeWebView | WebView2 / WKWebView / WPE WebKit | Three engines → rendering diverges across platforms |
| CefGlue.Avalonia | Chromium (CEF) | Stuck on Chromium 120 (Dec 2023). Pure-managed P/Invoke + regex header parser → unsustainable |
| DotNetBrowser | Chromium | Commercial, proprietary |
| CefSharp | Chromium | Tracks upstream within weeks, but Windows-only (C++/CLI) |

For applications where rendering quality *is* the product, none of these fit. Exclr8CEF is built fresh to give:

- Same Chromium engine and pixels on every platform (Win/macOS/Linux)
- Update cadence we control — target tracking upstream within weeks
- Permissive license, fully open
- A binding layer designed for sustainability — generated, not regex-parsed

## Architecture

Two NuGet packages, mirroring how `Microsoft.Web.WebView2.Core` and `Microsoft.Web.WebView2.Wpf` are split:

```
Console / WPF / MAUI / ASP.NET / service / etc.        Avalonia desktop app
        │                                                       │
        ▼                                                       ▼
┌─────────────────────────────┐                  ┌─────────────────────────────┐
│ Exclr8Cef                   │ ◄─── refs ────── │ Exclr8Cef.WebView           │
│  ├─ Cef (static facade)     │                  │  └─ WebView (Avalonia ctrl) │
│  ├─ CefVersions (record)    │                  └─────────────────────────────┘
│  └─ Exclr8Cef.Native        │
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

**`Exclr8Cef`** — framework-agnostic. Use from any .NET host. Public surface: `Cef.Initialize`, `Cef.CreateBrowser`, `Cef.RunMessageLoop`, `Cef.Shutdown`, `Cef.GetVersions`. Raw P/Invokes are `internal`.

**`Exclr8Cef.WebView`** — Avalonia integration. Adds the `WebView` control. Depends on `Exclr8Cef` and `Avalonia`.

Subprocess hosting (renderer/GPU/utility) is in C++, matching CEF's reference implementation. No .NET runtime in helper processes.

### Why this layering

The two reference architectures in production today:

- **CefSharp** — native shim against CEF's *C++* API, thin .NET on top. Tracks Chromium within weeks. The shim catches CEF API breaks at compile time. Windows-only because it uses C++/CLI.
- **CefGlue** — pure-managed P/Invoke against CEF's *C* API. Cross-platform but lagging 2+ years. Regex-based header parser is brittle; runtime errors scatter across hundreds of call sites.

Exclr8CEF takes CefSharp's shim model and ports it to plain C++ with a C ABI for cross-platform reach. ClangSharp replaces CefGlue's regex parser with proper libclang AST analysis — turning every CEF version bump into "regenerate, fix what broke, ship."

## Status

**Stages 4c–4e complete.** A real Avalonia desktop app with a `WebView` control that hosts an embedded Chromium browser via off-screen rendering. Public C# API:

- `Cef.InitializeForOsr` + `Cef.Shutdown`
- `Cef.CreateOffscreenBrowser` / `ResizeOffscreenBrowser`
- `Cef.LoadUrl` / `GoBack` / `GoForward` / `Reload` / `StopLoad` / `CloseBrowser` / `WasHidden`
- `Cef.SendMouseMove/Click/Wheel`, `Cef.SendKeyEvent`, `Cef.SetBrowserFocus`
- `Cef.ExecuteJavaScript`, `Cef.ShowDevTools` / `CloseDevTools`
- `Cef.PrintToPdfAsync`
- `Cef.AddressChanged` / `TitleChanged` / `LoadingStateChanged` / `BrowserClosed` static events
- `Cef.EvaluateJavaScriptAsync(browserId, code)` — returns JSON-serialized result via render-process IPC
- `Cef.GetCookiesAsync(url) / SetCookie / DeleteCookies`
- `Cef.ImeSetComposition / ImeCommitText / ImeFinishComposing / ImeCancel` (Avalonia IME integration follow-on)
- `WebView` Avalonia control: `Url`, `Title`, `IsLoading`, `CanGoBack`, `CanGoForward` properties; `GoBack/Forward/Reload/StopLoad/ShowDevTools/EvaluateJavaScriptAsync/PrintToPdfAsync` methods.
- Avalonia `Key` → Windows VK code mapping in `Exclr8Cef.WebView.KeyMap`.

Demo (`Exclr8Cef.WebView.Demo.app`) has a full browser-style toolbar (◀ ▶ ⟳ ✕ DevTools Run JS Save PDF), a URL address bar with Enter-to-navigate, and starts on a self-contained Chromium-rendered test page. "Run JS" demonstrates the round-trip `EvaluateJavaScriptAsync`: the demo executes a small expression in the page and prints the JSON result to the status bar.

## Layout

```
exclr8cef/
├── README.md                        # this file
├── LICENSE                          # MIT
├── .gitignore
├── .config/
│   └── dotnet-tools.json            # local tool manifest (ClangSharpPInvokeGenerator)
├── scripts/
│   ├── download-cef.sh              # pulls pinned CEF binaries from cef-builds.spotifycdn.com
│   └── regenerate-bindings.sh       # runs ClangSharp on exclr8cef.h
├── native/                          # C++ shim and subprocess helper
│   ├── CMakeLists.txt
│   ├── shim/                        # The C ABI surface
│   │   ├── exclr8cef.h              # public C ABI (parsed by ClangSharp)
│   │   ├── exclr8cef.cc             # cross-platform impl (versions, browser queue)
│   │   ├── exclr8cef_mac.mm         # macOS: NSApplication, library loader, init/run/shutdown
│   │   ├── exclr8cef_app.h/.cc      # CefApp + browser creation (Views framework)
│   │   ├── exclr8cef_client.h/.cc   # CefClient (lifecycle, errors)
│   │   └── version_probe.c          # native smoke test
│   ├── helper/                      # Subprocess helper exe (macOS Helper.app)
│   └── demo/                        # Visual demo: opens chrome://version
├── src/                             # shipping .NET projects
│   ├── Exclr8Cef/                   # framework-agnostic NuGet package
│   │   ├── Exclr8Cef.csproj
│   │   ├── Cef.cs                   # public static facade
│   │   ├── CefVersions.cs           # public record
│   │   ├── generate-bindings.rsp    # ClangSharp config
│   │   └── Generated/               # ClangSharp output (committed; types are internal)
│   │       ├── Excef.cs             # internal static class with [DllImport]s
│   │       ├── excef_versions.cs    # internal struct with [InlineArray(64)] buffers
│   │       └── *.cs                 # helper attributes
│   ├── Exclr8Cef.WebView/           # Avalonia integration package
│   └── runtime/                     # per-RID native runtime package template
├── samples/                         # example apps, not packed
│   ├── Exclr8Cef.ConsoleDemo/       # .NET-driven CEF window
│   └── Exclr8Cef.WebView.Demo/      # Avalonia + embedded WebView
├── tests/
│   └── Exclr8Cef.SmokeTest/         # managed equivalent of version_probe
├── third_party/cef/<platform>/      # extracted CEF (gitignored, ~150 MB)
```

## Building (macOS arm64)

```bash
# 1. Download CEF (~150 MB)
./scripts/download-cef.sh

# 2. Configure and build
cmake -S native -B native/build -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --parallel

# 3. Stage 1: smoke test (prints linked CEF + Chromium versions)
./native/build/shim/exclr8cef_version_probe

# 4. Stage 2: open a real browser window
open native/build/demo/Release/exclr8cef_demo.app

# 5. Stage 3: regenerate C# bindings, run the managed smoke test
./scripts/regenerate-bindings.sh
dotnet run --project tests/Exclr8Cef.SmokeTest

# 6. Stage 4a: build and launch the .NET-driven demo (.app bundle)
./scripts/build-console-demo.sh
open samples/Exclr8Cef.ConsoleDemo/bin/Release/Exclr8Cef.ConsoleDemo.app

# 7. Stage 4c: build and launch the Avalonia + embedded WebView demo
./scripts/build-avalonia-demo.sh
open samples/Exclr8Cef.WebView.Demo/bin/Release/Exclr8Cef.WebView.Demo.app
```

Expected probe output (identical from native and .NET):
```
Exclr8CEF shim version : 0.2.0-stage2
CEF version            : 147.0.10
Chromium version       : 147.0.7727.118
```

The Stage 2 demo opens a window rendering `chrome://version` — confirming end-to-end: download → build → link → helper subprocesses → message loop → browser → Chromium pixels. The Stage 3 smoke test confirms the same path through ClangSharp-generated C# bindings.

## Roadmap

| Stage | Goal | Output |
|---|---|---|
| **1** ✓ | Scaffolding + version probe | `exclr8cef_version_probe` prints CEF + Chromium versions |
| **2** ✓ | Full CEF init + subprocess helper | Demo opens chrome://version in a Chromium-rendered window |
| **3** ✓ | ClangSharp binding generator + idiomatic API | Two-package split: `Exclr8Cef` (framework-agnostic) and `Exclr8Cef.WebView` (Avalonia, Stage 4b). Managed smoke test prints same versions |
| **4a** ✓ | Console demo: .NET-driven CEF | `Exclr8Cef.ConsoleDemo.app` bundles a self-contained .NET app + CEF framework + 5 Helper.app bundles. Opens chrome://version. |
| **4b** | Native NSView embedding (abandoned in favor of 4c) | C ABI (`excef_create_browser_view`) builds, but same-process native window embedding hits an obj-c class collision (`ExtensionDropdownHandler` in CEF vs `libAvaloniaNative.dylib`) and Cocoa run-loop ownership conflicts. Kept available in the ABI for non-Avalonia hosts. |
| **4c** ✓ | OSR Avalonia WebView | `Exclr8Cef.WebView.WebView` control: CEF paints into BGRA buffer → `WriteableBitmap` → Avalonia draw. `Cef.InitializeForOsr` + `Cef.CreateOffscreenBrowser`. |
| **4d** ✓ | Input forwarding + multi-browser paint dispatch | `Cef.SendMouse*` / `SendKeyEvent` / `SetBrowserFocus`. WebView forwards Pointer/Wheel/Key/TextInput events. Paint handlers keyed in a ConcurrentDictionary so multiple WebView instances coexist. |
| **4e** ✓ | Navigation, JS, DevTools, browser events | `Cef.LoadUrl/GoBack/GoForward/Reload/StopLoad/CloseBrowser/WasHidden`, `Cef.ExecuteJavaScript`, `Cef.ShowDevTools/CloseDevTools`. Static events `Cef.AddressChanged/TitleChanged/LoadingStateChanged`. WebView surfaces `Title`, `IsLoading`, `CanGoBack`, `CanGoForward` as Avalonia properties. Demo has a full toolbar (◀ ▶ ⟳ ✕ DevTools Save PDF) plus address bar with Enter-to-navigate. |
| **4f** ✓ | JS eval w/ result, cookies, IME, key map, browser-closed event | `Exclr8CefApp` now also implements `CefRenderProcessHandler`; helper subprocess passes the app to `CefExecuteProcess` so renderer-side IPC works. `Cef.EvaluateJavaScriptAsync` returns a JSON-serialized result via "Eval" / "EvalResult" `CefProcessMessage` round-trip. Cookie API via `CefCookieManager`: `Cef.GetCookiesAsync(url)`, `Cef.SetCookie(url, name, value, ...)`, `Cef.DeleteCookies(url, name)`. `Cef.BrowserClosed` static event from `OnBeforeClose`. IME ABI: `Cef.ImeSetComposition / ImeCommitText / ImeFinishComposing / ImeCancel`. Avalonia `Key` → Windows VK code translation in `KeyMap.cs`. Demo adds a "Run JS" button. |
| **4g** ✓ | Avalonia IME wired into WebView | `WebViewTextInputMethodClient` extends `Avalonia.Input.TextInput.TextInputMethodClient`. `WebView` subscribes to `TextInputMethodClientRequested` and provides the client. `SetPreeditText` from the platform IME is forwarded to `Cef.ImeSetComposition`; lost focus calls `Cef.ImeCancel`. Composition events (CJK input, dead keys for diacritics) now flow into the embedded Chromium browser. |
| **5** ✓ | Auto-update pipeline + Windows/Linux readiness | `cef.json` source-of-truth. Cross-platform shim refactor (Windows/Linux now have `excef_initialize_offscreen` / `excef_initialize_external_pump` / `excef_do_message_loop_work`). `Cef.ExecuteProcess` wrapper for Windows/Linux subprocess re-invocation. NuGet packaging: `Exclr8Cef`, `Exclr8Cef.WebView`, and `runtime.<rid>.Exclr8Cef` per-platform packages with `runtime.json` for auto RID resolution. GitHub Actions: `ci.yml` (matrix build), `upstream-check.yml` (cron PR-bot), `release.yml` (tag-driven NuGet publish). |
| **5** | Distribution | Playwright-style binary fetch, NuGet packages per RID, CI matrix Win/macOS/Linux × x64/arm64 |
| **6** | Polish | Code signing, App Store / notarization, request interception, JS interop, custom schemes |

## C ABI naming

All exported symbols use the `excef_` prefix (e.g. `excef_initialize`, `excef_create_browser`). This is the surface ClangSharp parses to generate C# P/Invoke; keeping it short and consistent matters for the generator output.

## CEF version pinning + auto-update

The pinned CEF version lives in **`cef.json`** at the repo root — single source of truth read by every build script and CI workflow.

### Manual bump

```bash
./scripts/check-cef-upstream.sh           # exit 1 if upstream is newer
./scripts/bump-cef.sh "147.0.11+abc..."   # rewrites cef.json
rm -rf third_party/cef/                   # force re-download
./scripts/download-cef.sh
cmake --build native/build --parallel
./scripts/regenerate-bindings.sh          # if API changed
dotnet build tests/Exclr8Cef.SmokeTest
```

### Automatic bump (on GitHub)

Three workflows in `.github/workflows/`:

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | push to main, PRs | 4-RID matrix (osx-arm64, osx-x64, win-x64, linux-x64): downloads CEF, builds native shim, regenerates bindings, builds managed, runs smoke test, uploads demo binaries as artifacts |
| `upstream-check.yml` | daily at 09:00 UTC + manual | Hits `cef-builds.spotifycdn.com/index.json`. If newer stable than `cef.json`, opens (or updates) a PR titled `chore(cef): bump to <version>`. The PR triggers `ci.yml`. |
| `release.yml` | `v*` tag | Builds native + managed across all RIDs. Packs `runtime.<rid>.Exclr8Cef.nupkg` per platform (~135 MB compressed for macOS, smaller elsewhere) plus `Exclr8Cef.nupkg` and `Exclr8Cef.WebView.nupkg`. Pushes to `nuget.org` using the `NUGET_API_KEY` secret. |

### Distribution shape

NuGet packages produced per release (5 + 2 + 2 = 9 .nupkg files):

```
Exclr8Cef.<version>.nupkg                    ← managed bindings + runtime.json
Exclr8Cef.WebView.<version>.nupkg            ← Avalonia control
runtime.osx-arm64.Exclr8Cef.<version>.nupkg  ← native shim + CEF framework (per RID)
runtime.osx-x64.Exclr8Cef.<version>.nupkg
runtime.win-x64.Exclr8Cef.<version>.nupkg
runtime.linux-x64.Exclr8Cef.<version>.nupkg
runtime.linux-arm64.Exclr8Cef.<version>.nupkg
```

Consumers add `Exclr8Cef.WebView`. NuGet's `runtime.json` resolution picks the matching `runtime.<rid>.Exclr8Cef` automatically based on the consumer's `RuntimeIdentifier`.

**macOS caveat:** the native files land in `runtimes/osx-arm64/native/` after restore but `dotnet publish` won't auto-build a `.app` bundle. Use `scripts/build-avalonia-demo.sh` as a template for the `.app` packaging step. Windows and Linux work out of the box with `dotnet publish`.

### Setup needed before workflows run

After `git push` to GitHub:

1. Configure `NUGET_API_KEY` repo secret (Settings → Secrets and variables → Actions).
2. Optional: create a `nuget-publish` GitHub environment that gates releases on a manual approval.
3. The `upstream-check.yml` workflow needs `contents: write` + `pull-requests: write` (already declared in the workflow); ensure the default `GITHUB_TOKEN` permissions or branch protection allow auto-PRs.

## License

MIT — see [LICENSE](LICENSE). CEF itself is BSD; libcef binaries from Spotify CDN are redistributable under CEF's license.
