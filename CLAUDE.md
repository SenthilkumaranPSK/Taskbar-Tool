# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TaskbarMediaWidget is a Windows 11 WPF app with a single purpose: show a compact "now playing" media widget genuinely embedded in the real Windows taskbar (not a floating overlay), positioned immediately to the right of the Start button. It reflects whatever app is currently playing media via the OS-level System Media Transport Controls (SMTC) — Spotify, browser tabs, YouTube Music, etc.

This is a from-scratch reimplementation, inspired by (but not copied from — see licensing note below) [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature, which was studied as reference and is summarized in `docs/reference-fluentflyout-taskbar-widget.md`. Deliberately out of scope: flyouts, volume mixer, lock-key indicators, audio visualizer, MSIX/Store packaging, licensing gates, localization, settings UI. If a feature isn't "show now-playing info docked next to Start," it doesn't belong here.

## Build & run

- **Build**: `dotnet build Taskbar-Tool.slnx`
- **Run**: `dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj` (or launch from Visual Studio with `TaskbarMediaWidget` as startup project)
- **No test project** — this is fundamentally not unit-testable; verification is manual (see below).
- Single instance enforced via a named mutex (`Core/SingleInstanceGuard.cs`) — running a second copy just exits immediately.
- Target framework: `net10.0-windows10.0.22000.0` (matches installed SDK 10.0.103; the `10.0.22000.0` suffix is required for the SMTC WinRT APIs).

## Architecture

### The reparenting technique (the whole point of this app)

`Taskbar/TaskbarWidgetWindow.xaml.cs` is a WPF window that gets turned into a genuine `WS_CHILD` of the taskbar's own HWND (`Shell_TrayWnd`) via `SetParent` — not an always-on-top overlay. Once reparented, WPF's own `Window.Left`/`Top` no longer apply; position and size are driven entirely by raw `SetWindowPos` calls (`Interop/NativeMethods.cs`) in the taskbar's client coordinate space. A ~1.3s poll timer continuously re-resolves the taskbar handle and re-applies position (self-healing, not purely event-driven), and explicit hooks handle `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` for scale/resolution changes.

**Explorer restarts require full window recreation, not revival**: `DestroyWindow` on a parent also destroys true `WS_CHILD` children, so when `explorer.exe` dies, the reparented widget HWND is destroyed as a side effect and cannot be reused. `TaskbarWidgetWindow` detects this via `WM_TASKBARCREATED`, waits for the taskbar to come back (`HandleExplorerRestartAsync`), then raises `ExplorerRestarted` — `App.xaml.cs.RecreateWidgetWindow()` closes the dead instance and constructs a fresh one. Don't try to "fix" this by re-`SetParent`-ing the same instance; it won't work.

Full background and the Win32 API surface this is built on: `docs/reference-fluentflyout-taskbar-widget.md`.

### Positioning next to the Start button — unverified, needs on-device tuning

`Taskbar/StartButtonLocator.cs` is the one piece of this app with no working prior art (FluentFlyout never looks up the Start button itself). It tries UI Automation first (`AutomationId="StartButton"` — **an educated guess, not confirmed**), sanity-checks the result against plausible bounds, and falls back to a fixed DPI-scaled pixel offset **only when the taskbar is Left-aligned** (checked via the `TaskbarAl` registry value) — a fixed offset would be visibly wrong when Center-aligned (the Windows 11 default), so that case intentionally hides the widget rather than guessing.

**Before relying on this in daily use**: check `%LocalAppData%\TaskbarMediaWidget\log.txt` for `"StartButton automation id did not resolve"` or `"failed sanity check"` warnings, and confirm/replace `StartButtonAutomationId` and `FallbackStartButtonWidthLogicalPx` (both marked `TODO(verify/measure on-device)` in `StartButtonLocator.cs`) against the real taskbar.

### Media session data

`Media/MediaSessionService.cs` wraps `Dubya.WindowsMediaController.MediaManager` (SMTC wrapper NuGet package — don't hand-roll raw WinRT SMTC interop). Session selection is deliberately simple: prefer the OS-focused session, else the first session that's actually `Playing`, else the first available session, else `null` (nothing playing → widget collapses). No allow/block-list filtering, no "pause other sessions" — that's FluentFlyout-specific scope this app doesn't need.

`Media/ThumbnailConverter.cs` converts an SMTC thumbnail (`IRandomAccessStreamReference`) to a WPF `BitmapImage` by reading it via `DataReader` into a `MemoryStream` — deliberately avoids `WindowsRuntimeStreamExtensions.AsStreamForRead`, which doesn't resolve cleanly against `IRandomAccessStreamWithContentType` in this project's WinRT interop setup.

### Wiring

No MVVM framework — plain C# events, wired directly in `App.xaml.cs`: `MediaSessionService.NowPlayingChanged` → `TaskbarWidgetWindow.UpdateNowPlaying`; the widget's `PreviousRequested`/`PlayPauseRequested`/`NextRequested` → the corresponding `MediaSessionService` command methods. `App.xaml.cs` also owns the single-instance guard, unhandled-exception logging, and the tray icon (`Tray/TrayIconService.cs` — a `System.Windows.Forms.NotifyIcon` with a single "Exit" item; it's the only way to close the app, since there's no visible main window).

### Gotchas worth knowing before touching this code

- `SetWindowPos` in `Interop/NativeMethods.cs` must stay a classic `[DllImport]`, not a `[LibraryImport]` source-generated binding — the source-generated version has been observed to break topmost/visibility behavior for a window reparented into another process.
- `UseWindowsForms=true` (needed only for the tray icon) plus `UseWPF=true` means `Application`, `UserControl`, and `Size` are ambiguous between `System.Windows.*` and `System.Windows.Forms`/`System.Drawing` — fully qualify these where the compiler flags CS0104 rather than adding blanket `using` aliases.
- DPI awareness is declared via the `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` project property (not `app.manifest` directly) — WinForms' own DPI configuration conflicts with a hand-written manifest DPI block when both toolkits are in the same project (`WFO0003`). This is required, not optional: the positioning math reads DPI straight off the taskbar HWND via `GetDpiForWindow`.

## Verification

No automated tests are meaningful here — this is live interaction with `explorer.exe`'s real window tree. After building, check manually:

1. Play media somewhere, confirm the widget appears touching the taskbar next to Start.
2. Confirm true embedding (not overlay): drag a maximized window over the taskbar — the widget should get clipped like a native taskbar element, and never appear in Alt-Tab.
3. Toggle **Settings → Personalization → Taskbar → alignment** between Left and Center — confirm behavior matches the design in `StartButtonLocator.cs` (positioned correctly on Left; gracefully hidden rather than misplaced on Center if automation fails).
4. Change display scaling live (100/125/150%), confirm no drift.
5. `taskkill /f /im explorer.exe` (self-restarts) — confirm the widget reappears correctly within ~60s, no crash.
6. Pause/stop all media — confirm the widget collapses cleanly instead of showing an empty box.
7. Launch a second instance — confirm it exits immediately.

## Licensing note

FluentFlyout (the reference material) is GPL-3.0-or-later. This project reimplements the *technique* freely, not its code — no files were copied. Keep it that way if pulling in more reference material later.
