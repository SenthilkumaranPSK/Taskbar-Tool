# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

TaskbarMediaWidget is a Windows 11 WPF app with a single purpose: show a compact "now playing" media widget genuinely embedded in the real Windows taskbar (not a floating overlay), anchored to the taskbar's left edge. It reflects whatever app is currently playing media via the OS-level System Media Transport Controls (SMTC) — Spotify, browser tabs, YouTube Music, etc.

This is a from-scratch reimplementation, inspired by (but not copied from — see licensing note below) [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature, which was studied as reference and is summarized in `docs/reference-fluentflyout-taskbar-widget.md`. Deliberately out of scope **for the current codebase**: flyouts, volume mixer, lock-key indicators, audio visualizer, MSIX/Store packaging, licensing gates, localization, settings UI. If a feature isn't "show now-playing info docked in the taskbar," it doesn't belong here yet.

`ROADMAP.md` sketches a much larger future direction (flyouts, an audio visualizer/spectrum engine, a volume mixer, Discord/Last.fm integrations, a settings UI, extra taskbar modules) that would supersede the out-of-scope list above. Treat it as an aspirational design doc, not a description of what exists today — none of its modules (`Audio/`, `Lyrics/`, `Flyouts/`, `Settings/`, etc.) have been built. Don't assume roadmap features exist when reading code, and don't start implementing roadmap items unless the user explicitly asks for that expansion.

## Build & run

- **Build**: `dotnet build Taskbar-Tool.slnx` — note the `.slnx` extension: this is the new XML-based solution format the .NET 10 SDK generates by default (`dotnet new sln`), not a classic `.sln` file.
- **Run**: `dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj` (or launch from Visual Studio with `TaskbarMediaWidget` as startup project)
- **No test project** — this is fundamentally not unit-testable; verification is manual (see below).
- **Diagnostics**: there is no console, no visible main window, and no tests — `%LOCALAPPDATA%\TaskbarMediaWidget\log.txt` (`Core/AppLog.cs`) is the only channel for finding out what actually happened. It rotates at 1 MB to `log.txt.old`, and it collapses runs of the *exact same* line into one entry with a trailing `(repeated N more times)` — a failure that recurs on every poll tick shows up as one line, so don't read a short log as "nothing went wrong."
- Single instance enforced via a `Local\`-scoped named mutex (`Core/SingleInstanceGuard.cs`, per-session deliberately — see Gotchas) — running a second copy just exits immediately.
- Target framework: `net10.0-windows10.0.22000.0` (matches installed SDK 10.0.103; the `10.0.22000.0` suffix is required for the SMTC WinRT APIs).
- Project targets both `x64` and `ARM64` (`Platforms`/`RuntimeIdentifiers` in the `.csproj`) — a plain `dotnet build` uses the default platform, so pass `-p:Platform=ARM64` (or `-r win-arm64`) when you need to verify the ARM64 path specifically.

## Architecture

### The reparenting technique (the whole point of this app)

`Taskbar/TaskbarWidgetWindow.xaml.cs` is a WPF window that gets turned into a genuine `WS_CHILD` of the taskbar's own HWND (`Shell_TrayWnd`) via `SetParent` — not an always-on-top overlay. Once reparented, WPF's own `Window.Left`/`Top` no longer apply; position and size are driven entirely by raw `SetWindowPos` calls (`Interop/NativeMethods.cs`) in the taskbar's client coordinate space. A poll timer continuously re-resolves the taskbar handle and re-applies position (self-healing, not purely event-driven) — 1.3s while the widget is visible, backed off to 5s while it's hidden — and explicit hooks handle `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` for scale/resolution/theme changes. Each poll tick caches the last-applied rect and the widget's content-version counter, so it skips the WPF measure pass and the `SetWindowPos` call entirely on ticks where nothing actually changed.

**Explorer restarts require full window recreation, not revival, and the recovery listener cannot live on the reparented window itself**: `DestroyWindow` on a parent also destroys true `WS_CHILD` children, so when `explorer.exe` dies, the reparented widget HWND is destroyed as a side effect and cannot be reused — `TryReposition` notices this via `NativeMethods.IsWindow` on the next poll tick and just stops polling. Recovery is owned by a separate class, `Taskbar/ShellWatchdogWindow.cs`: a hidden, **never-reparented** top-level window whose only job is to receive the `TaskbarCreated` broadcast. This split exists because `WM_TASKBARCREATED` is posted via `HWND_BROADCAST`, and `HWND_BROADCAST` explicitly excludes child windows — the instant `TaskbarWidgetWindow` reparents, it becomes structurally ineligible to ever receive that message itself, so an earlier version that tried to listen for it on the widget window directly had a permanently-dead recovery path. `App.xaml.cs` owns the watchdog, waits for the taskbar to actually come back (`HandleExplorerRestartAsync`), then calls `RecreateWidgetWindow()` to close the dead instance and construct a fresh one. Don't try to "fix" Explorer-restart recovery by re-`SetParent`-ing the same instance, and don't move the `TaskbarCreated` listener back onto `TaskbarWidgetWindow` — both approaches are structurally broken for the reason above. Because the new window starts with no now-playing state of its own, `App.xaml.cs.CreateWidgetWindow()` immediately hydrates it from `MediaSessionService.CurrentNowPlaying` (the last snapshot `RefreshAsync` published) right after `Show()` — otherwise the recreated widget would stay collapsed until the next incidental SMTC callback instead of reflecting whatever was already playing. Every `TaskbarWidgetWindow` also force-hides itself immediately after attaching (in `OnSourceInitialized`, construction-time only), so a freshly (re)created window never has a moment where it's visible with no content.

Full background and the Win32 API surface this is built on: `docs/reference-fluentflyout-taskbar-widget.md`.

### Positioning: anchored to the taskbar's left edge, not the Start button

An earlier version tried to dock the widget immediately right of the Start button via UI Automation (`AutomationId="StartButton"`), with a registry-alignment-aware pixel fallback. That was dropped: on a Center-aligned taskbar (the Windows 11 default), Start sits in the middle of the screen, so "next to Start" put the widget in the middle of the screen too, mixed in with pinned/running app icons — not the fixed, predictable spot the widget is meant to occupy. `TaskbarWidgetWindow.CalculateAndSetPosition` now just places it at a small fixed margin (`LeftEdgeMarginLogicalPx`) from the taskbar's own left edge (from `TaskbarLocator.GetTaskbarRect`), independent of Start's position or the taskbar's alignment setting. This means on a Left-aligned taskbar the widget can visually collide with the Start button itself — acceptable for this app's current single target configuration (Center-aligned), not yet handled for Left-aligned setups.

### Taskbar geometry still goes through UI Automation

Dropping the Start-button lookup did **not** remove UI Automation from the app, and `GetWindowRect` is not the primary source of the taskbar's rect. `TaskbarLocator.GetTaskbarRect` prefers the bounding rectangle of the `TaskbarFrame` automation element and only falls back to `GetWindowRect(Shell_TrayWnd)`, because on some Windows builds the raw window rect includes invisible margins that would push the widget off the visible bar. Every automation call goes through `Taskbar/AutomationLookup.cs`, which runs `FindFirst` on a background task with a hard 1s timeout — UIA can block or hang indefinitely against a busy/unresponsive shell, and this runs off a poll timer on the UI thread, so never call UIA inline here. The resolved element is cached per taskbar HWND and re-validated each tick by touching `Current.BoundingRectangle` and catching `ElementNotAvailableException` (an element that outlived its window throws rather than returning stale data). `UIAutomationClient`/`UIAutomationTypes` arrive implicitly via `UseWPF`; adding an explicit `<Reference>` for them produces a duplicate-reference warning (see the comment at the bottom of the `.csproj`).

### Media session data

`Media/MediaSessionService.cs` wraps `Dubya.WindowsMediaController.MediaManager` (SMTC wrapper NuGet package — don't hand-roll raw WinRT SMTC interop). Session selection is deliberately simple: prefer the OS-focused session, else the first session that's actually `Playing`, else the first available session, else `null` (nothing playing → widget collapses). No allow/block-list filtering, no "pause other sessions" — that's FluentFlyout-specific scope this app doesn't need. `GetActiveSession()` guards on `IsStarted` and retries once on `InvalidOperationException` before giving up — `CurrentMediaSessions` is a plain, unsynchronized `Dictionary` that the library mutates from SMTC callback threads, so a session opening/closing mid-enumeration is an expected, not exceptional, race.

Every SMTC callback triggers `RefreshAsync()` on an arbitrary thread-pool thread, and building a `NowPlayingInfo` involves multiple awaited calls — overlapping refreshes can finish out of order. `RefreshAsync` stamps each call with `Interlocked.Increment(ref _refreshGeneration)` and drops the result if a newer refresh has already started by the time it finishes, so a slow refresh can never overwrite a newer one's output. A 45s heartbeat calls `MediaManager.ForceUpdate()` (plus on `SystemEvents.PowerModeChanged`/`SessionSwitch`) to work around a documented upstream bug where SMTC events can silently stop firing — don't remove this thinking it's redundant with the event subscriptions.

`Media/ThumbnailConverter.cs` converts an SMTC thumbnail (`IRandomAccessStreamReference`) to a WPF `BitmapImage` by reading it via `DataReader` into a `MemoryStream` — deliberately avoids `WindowsRuntimeStreamExtensions.AsStreamForRead`, which doesn't resolve cleanly against `IRandomAccessStreamWithContentType` in this project's WinRT interop setup. `MediaSessionService` caches the most recent decoded thumbnail keyed on `(Title, Artist, AlbumTitle)` — Chromium-based browsers fire property-changed events aggressively, and without the cache the same JPEG gets re-decoded on every one.

### Theme awareness

`Interop/ThemeDetector.cs` reads `HKCU\...\Themes\Personalize\SystemUsesLightTheme` to tell whether the taskbar is in light or dark mode. `NowPlayingWidgetControl`'s text/hover brushes are `DynamicResource`s (not hardcoded colors) so `ApplyTheme(bool)` can swap them at runtime; `TaskbarWidgetWindow` re-applies on construction and again on the `WM_SETTINGCHANGE` broadcast where `lParam` is the literal string `"ImmersiveColorSet"` (a different signal than the `SPI_SETWORKAREA` check on the same message, which is about position, not color).

### Album-art accent tint

`Media/AccentColorExtractor.cs` reduces the already-decoded 96px thumbnail to a single vibrant color with a weighted hue histogram (24 buckets, each pixel weighted by saturation × value) rather than a median-cut/octree quantizer — at ~9k pixels the cheap version is accurate enough for a background wash and cheap enough to run inline on the refresh path. Three behaviors here are deliberate and easy to "fix" into bugs:

- It returns `null` for art with no usable hue (greyscale covers, solid black/white), and the widget then renders exactly as it did before the tint existed. Don't substitute a fallback color — inventing one is worse than not tinting.
- The winning color's **hue is kept but saturation and value are clamped** into a legible band. Raw cover-art colors are routinely either neon or muddy, and both look wrong washed across a taskbar.
- There is **no upper bound on value** in the pixel filter. An earlier version rejected `value > 0.97` to skip near-white letterboxing and silently discarded every fully-bright saturated pixel with it — `#FF0000`, `#FF8C00` and `#FF00FF` all sit at value 1.0, so vivid art produced no tint at all. Near-white is excluded by the saturation floor instead, which is the correct test for it.

The accent is extracted in `MediaSessionService.GetArtworkAsync` and shares the thumbnail's `(Title, Artist, AlbumTitle)` cache entry, so Chromium's repeated property-changed events don't re-extract it; `NowPlayingWidgetControl.ApplyAccent` early-outs on an unchanged color for the same reason. It renders into `PillBorder`, a `Border` wrapping the whole control whose `BorderThickness` is a constant 1 — constant matters, because it offsets the control's natural width by a fixed 2px and so never interferes with the `ContentVersion` measure-skip described below.

### Widget rendering and the marquee

`Controls/NowPlayingWidgetControl` is fixed-size by design: `Height="32"` on the UserControl, and a hardcoded `Width="110"` on the `TextClip` canvas that holds title+artist. Text that doesn't fit is neither wrapped nor trimmed — it scrolls, via a `DoubleAnimation` on `Canvas.Left` with `AutoReverse` and `RepeatBehavior.Forever`. Transport buttons are Segoe Fluent Icons glyphs, with the play/pause one swapped in code (`PlayGlyph`/`PauseGlyph`) rather than by a trigger.

That forever-repeating storyboard keeps WPF's composition thread running at display refresh rate for as long as it's alive, which is real battery cost for a widget that's up 24/7 — so it's only allowed to run while media is *actually playing* and the window is *actually visible*. `SetNowPlaying` stops it whenever the session isn't playing, and `TaskbarWidgetWindow.SetWindowVisible(false)` calls `StopMarquee()` on the way to hiding. It restarts only on a genuine track change, never on a play/pause toggle (which also routes through `SetNowPlaying`) — otherwise every pause would yank mid-scroll text back to the start.

`ContentVersion` is the contract between this control and its host window: it increments only when the displayed title/artist actually change, i.e. only when the natural size could have changed. `CalculateAndSetPosition` compares it against `_lastMeasuredContentVersion` to decide whether to redo the WPF measure pass and the `Width`/`Height` assignment. If you add anything to the control that can change its natural width (a duration label, an extra button), bump `ContentVersion` when it changes or the window will keep sizing itself to stale content.

### Wiring

No MVVM framework — plain C# events, wired directly in `App.xaml.cs`: `MediaSessionService.NowPlayingChanged` → `TaskbarWidgetWindow.UpdateNowPlaying`; the widget's `PreviousRequested`/`PlayPauseRequested`/`NextRequested` → the corresponding `MediaSessionService` command methods. Transport clicks don't call the async command methods directly — they go through `App.RunCommandAsync`, because a bare `_ = SomethingAsync()` puts any fault (e.g. the SMTC session dictionary being mutated mid-enumeration) on an unobserved `Task`, where it vanishes silently and the button just appears to do nothing. `DispatcherUnhandledException` is deliberately marked `Handled = true`: a recoverable UI fault should be logged, not allowed to take down a widget the user can only restart manually.

`App.xaml.cs` also owns the single-instance guard, unhandled-exception logging, and the tray icon (`Tray/TrayIconService.cs` — a `System.Windows.Forms.NotifyIcon` carrying a "Run at startup" toggle and an "Exit" item; Exit is the only way to close the app, since there's no visible main window).

"Run at startup" is backed by `Core/StartupRegistration.cs`, writing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — HKCU rather than HKLM for the same reason the single-instance mutex is `Local\`-scoped: this is a per-user app, and HKCU needs no elevation, so the toggle just works instead of prompting for admin. Two details that look like polish but aren't: the registered value is **quoted**, because this repo lives under a path containing spaces and an unquoted `Run` value is parsed at the first space; and `IsEnabled()` returns true only when the entry points at the *current* executable, so a stale entry left by a copy that has since moved reads as "off" rather than showing a tick for a path that no longer launches this build. The tray menu re-reads the registry on `Opening` instead of caching at construction, since Task Manager's Startup tab can flip the same entry behind the app's back.

### Gotchas worth knowing before touching this code

- `SetWindowPos` in `Interop/NativeMethods.cs` must stay a classic `[DllImport]`, not a `[LibraryImport]` source-generated binding — the source-generated version has been observed to break topmost/visibility behavior for a window reparented into another process.
- `UseWindowsForms=true` (needed only for the tray icon) plus `UseWPF=true` means `Application`, `UserControl`, `Size`, `Color`, and `Timer` are ambiguous between `System.Windows.*` and `System.Windows.Forms`/`System.Drawing`/`System.Timers` — fully qualify these where the compiler flags CS0104 rather than adding blanket `using` aliases.
- The single-instance mutex is `Local\`-scoped, not `Global\`, on purpose: every logged-on user gets their own `explorer.exe` and their own taskbar, so every user should get their own widget instance. A `Global\` mutex would block a second user's copy under fast user switching or RDP.
- `Resources/app.ico` needs **both** `<ApplicationIcon>` and a `<Resource Include>` entry in the `.csproj`, and neither substitutes for the other: `ApplicationIcon` only stamps the icon into the .exe's Win32 resources, while `TrayIconService` loads the same file through a `pack://` URI at runtime to pick a size-appropriate variant for the notification area (`Icon.ExtractAssociatedIcon` would hand back a downscaled 32px one instead). `.ico` has no default build action, so dropping the `<Resource>` line silently falls the tray back to the stock system icon.
- DPI awareness is declared via the `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` project property (not `app.manifest` directly) — WinForms' own DPI configuration conflicts with a hand-written manifest DPI block when both toolkits are in the same project (`WFO0003`). This is required, not optional: the positioning math reads DPI straight off the taskbar HWND via `GetDpiForWindow`.
- Shared app-level helpers (logging, single-instance guard) live under the `Core/` namespace, not `App/` — a namespace named `TaskbarMediaWidget.App` collides with the `App` class itself (`TaskbarMediaWidget.App`, the `Application` subclass) and fails to compile. Don't reintroduce an `App/` folder for anything other than `App.xaml`/`App.xaml.cs`.

## Verification

No automated tests are meaningful here — this is live interaction with `explorer.exe`'s real window tree. After building, check manually:

1. Play media somewhere, confirm the widget appears touching the taskbar's left edge.
2. Confirm true embedding (not overlay): drag a maximized window over the taskbar — the widget should get clipped like a native taskbar element, and never appear in Alt-Tab.
3. Toggle **Settings → Personalization → Taskbar → alignment** between Left and Center — the widget stays pinned to the left edge either way (by design, see Positioning above); on Left it will sit on top of/overlap the Start button, which is a known limitation, not a crash.
4. Change display scaling live (100/125/150%), confirm no drift.
5. `taskkill /f /im explorer.exe` (self-restarts) — confirm the widget reappears correctly within ~60s, no crash.
6. Pause/stop all media — confirm the widget collapses cleanly instead of showing an empty box.
7. Launch a second instance — confirm it exits immediately.
8. Play tracks with vividly-colored, greyscale, and near-black cover art in turn — confirm the pill picks up a matching tint for the first and stays fully untinted (not a grey wash) for the others.
9. Tray icon → **Run at startup**: toggle it on, confirm the tick survives closing and reopening the menu and that the entry shows up in Task Manager → Startup apps; toggle it back off and confirm it disappears.

For anything that misbehaves during these, read `%LOCALAPPDATA%\TaskbarMediaWidget\log.txt` — attach/reattach, SetParent failures, automation timeouts, and Explorer-restart recovery all log there, and it's the only place a swallowed background-task failure becomes visible.

## Keeping the docs in sync

`README.md` independently restates the scope list, the component table, and the known-limitations list (left-aligned-taskbar overlap, primary monitor only, placeholder tray icon). If you change positioning behavior, scope, or limitations, update it too — it is the user-facing copy of the same facts, not a pointer to this file. `docs/reference-fluentflyout-taskbar-widget.md` is background on the technique and doesn't describe current code, so it only needs changing if the underlying Win32 approach does.

## Licensing note

FluentFlyout (the reference material) is GPL-3.0-or-later. This project reimplements the *technique* freely, not its code — no files were copied. Keep it that way if pulling in more reference material later.
