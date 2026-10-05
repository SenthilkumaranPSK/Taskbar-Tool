# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Taskbar Tool** (v1.1; the code namespace is still `TaskbarMediaWidget` — see Naming below) is a Windows 11 WPF app with a single purpose: show a compact "now playing" media widget genuinely embedded in the real Windows taskbar (not a floating overlay), anchored to the taskbar's left edge. It reflects whatever app is currently playing media via the OS-level System Media Transport Controls (SMTC) — Spotify, browser tabs, YouTube Music, etc.

This is a from-scratch reimplementation, inspired by (but not copied from — see licensing note below) [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature, which was studied as reference and is summarized in `docs/reference-fluentflyout-taskbar-widget.md`. Deliberately out of scope **for the current codebase**: flyouts, volume mixer, lock-key indicators, audio visualizer, MSIX/Store packaging, licensing gates, localization, settings UI. If a feature isn't "show now-playing info docked in the taskbar," it doesn't belong here yet.

`ROADMAP.md` sketches a much larger future direction (flyouts, an audio visualizer/spectrum engine, a volume mixer, Discord/Last.fm integrations, a settings UI, extra taskbar modules) that would supersede the out-of-scope list above. Treat it as an aspirational design doc, not a description of what exists today — none of its modules (`Audio/`, `Lyrics/`, `Flyouts/`, `Settings/`, etc.) have been built. Don't assume roadmap features exist when reading code, and don't start implementing roadmap items unless the user explicitly asks for that expansion.

## Build & run

- **Build**: `dotnet build Taskbar-Tool.slnx` — note the `.slnx` extension: this is the new XML-based solution format the .NET 10 SDK generates by default (`dotnet new sln`), not a classic `.sln` file.
- **Run**: `dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj` (or launch from Visual Studio with `TaskbarMediaWidget` as startup project)
- **Publish + install**: `dotnet publish src/TaskbarMediaWidget/TaskbarMediaWidget.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist`, then `powershell -ExecutionPolicy Bypass -File .\install.ps1` (`-Uninstall` to remove). The install script is per-user by design — `%LOCALAPPDATA%\Programs\Taskbar Tool` plus a Start Menu shortcut, no elevation — and deliberately does *not* touch autostart, because the tray's "Run at startup" item is the single source of truth for that.
- **Naming**: the product is **Taskbar Tool** (`<Product>`/`<AssemblyTitle>`), the assembly and .exe are `TaskbarTool`, and the code namespace is still `TaskbarMediaWidget`. The namespace is deliberately left alone — renaming it touches every file for no user-visible gain. Don't "fix" the mismatch.
- **No test project** — this is fundamentally not unit-testable; verification is manual (see below). Pure helpers are the exception: `Media/AccentColorExtractor.cs` has no WPF-window dependency, so it can be compiled into a throwaway console project and exercised against synthetic bitmaps, which is how its filter bug was caught.
- **Diagnostics**: there is no console, no visible main window, and no tests — `%LOCALAPPDATA%\Taskbar Tool\log.txt` (`Core/AppLog.cs`) is the only channel for finding out what actually happened. It rotates at 1 MB to `log.txt.old`, and it collapses runs of the *exact same* line into one entry with a trailing `(repeated N more times)` — a failure that recurs on every poll tick shows up as one line, so don't read a short log as "nothing went wrong."
- Single instance enforced via a `Local\`-scoped named mutex (`Core/SingleInstanceGuard.cs`, per-session deliberately — see Gotchas) — running a second copy just exits immediately.
- Target framework: `net10.0-windows10.0.22000.0` (matches installed SDK 10.0.103; the `10.0.22000.0` suffix is required for the SMTC WinRT APIs).
- Project targets both `x64` and `ARM64` (`Platforms`/`RuntimeIdentifiers` in the `.csproj`) — a plain `dotnet build` uses the default platform, so pass `-p:Platform=ARM64` (or `-r win-arm64`) when you need to verify the ARM64 path specifically.
- `dist/` is gitignored and must stay that way: the published .exe is ~231 MB. Note that another agent (Antigravity/Gemini) is also used on this repo and has auto-committed shared working-tree changes before, so an unignored build artifact here is a live hazard, not a hypothetical one.
- The build is expected to be **0 warnings**. It has been kept there deliberately; treat a new warning as something to fix rather than baseline noise.

## Architecture

### The reparenting technique (the whole point of this app)

`Taskbar/TaskbarWidgetWindow.xaml.cs` is a WPF window that gets turned into a genuine `WS_CHILD` of the taskbar's own HWND (`Shell_TrayWnd`) via `SetParent` — not an always-on-top overlay. Once reparented, WPF's own `Window.Left`/`Top` no longer apply; position and size are driven entirely by raw `SetWindowPos` calls (`Interop/NativeMethods.cs`) in the taskbar's client coordinate space. A poll timer continuously re-resolves the taskbar handle and re-applies position (self-healing, not purely event-driven) — 1.3s while the widget is visible, backed off to 5s while it's hidden — and explicit hooks handle `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` for scale/resolution/theme changes. Each poll tick caches the last-applied rect and the widget's content-version counter, so it skips the WPF measure pass and the `SetWindowPos` call entirely on ticks where nothing actually changed.

**Explorer restarts require full window recreation, not revival, and the recovery listener cannot live on the reparented window itself**: `DestroyWindow` on a parent also destroys true `WS_CHILD` children, so when `explorer.exe` dies, the reparented widget HWND is destroyed as a side effect and cannot be reused — `TryReposition` notices this via `NativeMethods.IsWindow` on the next poll tick and just stops polling. Recovery is owned by a separate class, `Taskbar/ShellWatchdogWindow.cs`: a hidden, **never-reparented** top-level window whose only job is to receive the `TaskbarCreated` broadcast. This split exists because `WM_TASKBARCREATED` is posted via `HWND_BROADCAST`, and `HWND_BROADCAST` explicitly excludes child windows — the instant `TaskbarWidgetWindow` reparents, it becomes structurally ineligible to ever receive that message itself, so an earlier version that tried to listen for it on the widget window directly had a permanently-dead recovery path. `App.xaml.cs` owns the watchdog, waits for the taskbar to actually come back (`HandleExplorerRestartAsync`), then calls `RecreateWidgetWindow()` to close the dead instance and construct a fresh one. Don't try to "fix" Explorer-restart recovery by re-`SetParent`-ing the same instance, and don't move the `TaskbarCreated` listener back onto `TaskbarWidgetWindow` — both approaches are structurally broken for the reason above. Because the new window starts with no now-playing state of its own, `App.xaml.cs.CreateWidgetWindow()` immediately hydrates it from `MediaSessionService.CurrentNowPlaying` (the last snapshot `RefreshAsync` published) right after `Show()` — otherwise the recreated widget would stay collapsed until the next incidental SMTC callback instead of reflecting whatever was already playing. Every `TaskbarWidgetWindow` also force-hides itself immediately after attaching (in `OnSourceInitialized`, construction-time only), so a freshly (re)created window never has a moment where it's visible with no content.

Full background and the Win32 API surface this is built on: `docs/reference-fluentflyout-taskbar-widget.md`.

### Positioning: anchored to the taskbar's left edge, not the Start button

An earlier version tried to dock the widget immediately right of the Start button via UI Automation (`AutomationId="StartButton"`), with a registry-alignment-aware pixel fallback. That was dropped: on a Center-aligned taskbar (the Windows 11 default), Start sits in the middle of the screen, so "next to Start" put the widget in the middle of the screen too, mixed in with pinned/running app icons — not the fixed, predictable spot the widget is meant to occupy. `TaskbarWidgetWindow.CalculateAndSetPosition` now just places it at a small fixed margin (`LeftEdgeMarginLogicalPx`) from the taskbar's own left edge (from `TaskbarLocator.GetTaskbarRect`), independent of Start's position or the taskbar's alignment setting. This means on a Left-aligned taskbar the widget can visually collide with the Start button itself — acceptable for this app's current single target configuration (Center-aligned), not yet handled for Left-aligned setups.

### Taskbar geometry: UI Automation, but never on the UI thread

Dropping the Start-button lookup did **not** remove UI Automation from the app, and `GetWindowRect` is not the preferred source of the taskbar's rect. `TaskbarLocator` wants the bounding rectangle of the `TaskbarFrame` automation element, because on some Windows builds the raw window rect includes invisible margins that would push the widget off the visible bar.

**The rect is cached, and the UIA work that produces it runs on a thread-pool thread.** This is the single most important performance property of this file, and it was originally written the other way around: `GetTaskbarRect` resolved the element and read `Current.BoundingRectangle` inline, on every poll tick, on the UI thread. Both are synchronous cross-process calls into `explorer.exe`, and the element lookup was bounded only by `Task.Wait(1000)` — which bounds the *lookup* while still parking the UI thread for up to a second. Against a busy shell that produced exactly the periodic hitching you would predict. Now:

- `GetTaskbarRect` never blocks. It returns the cached refined rect, or falls back to `GetWindowRect` (cheap, in-process) while a background refresh is still in flight.
- `BeginRefreshRect` guards with an `Interlocked` flag so poll ticks — which arrive far faster than a slow UIA round-trip completes — can't pile up thread-pool work against an unresponsive shell.
- The cache is dropped only by `InvalidateRectCache()`, called from the events that can actually change taskbar geometry: `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `SPI_SETWORKAREA`, and re-attaching to a new taskbar HWND.

So if you need the taskbar rect somewhere new, call `GetTaskbarRect` freely — it's a lock and a struct copy. What you must not do is reintroduce a UIA call on the poll path. Note also that `CalculateAndSetPosition` only actually consumes `taskbarRect.Height` (for vertical centering); the horizontal position is a fixed margin in the parent's *client* coordinates, which is why an auto-hiding taskbar needs no special handling — a `WS_CHILD` slides and clips with its parent for free.

`Taskbar/AutomationLookup.cs` still wraps `FindFirst` in a background task with a hard 1s timeout; that's now belt-and-braces, since its caller is already off the UI thread. `UIAutomationClient`/`UIAutomationTypes` arrive implicitly via `UseWPF`; adding an explicit `<Reference>` for them produces a duplicate-reference warning (see the comment at the bottom of the `.csproj`).

### Media session data

`Media/MediaSessionService.cs` wraps `Dubya.WindowsMediaController.MediaManager` (SMTC wrapper NuGet package — don't hand-roll raw WinRT SMTC interop). Session selection is deliberately simple: prefer the OS-focused session, else the first session that's actually `Playing`, else the first available session, else `null` (nothing playing → widget collapses). No allow/block-list filtering, no "pause other sessions" — that's FluentFlyout-specific scope this app doesn't need. `GetActiveSession()` guards on `IsStarted` and retries once on `InvalidOperationException` before giving up — `CurrentMediaSessions` is a plain, unsynchronized `Dictionary` that the library mutates from SMTC callback threads, so a session opening/closing mid-enumeration is an expected, not exceptional, race.

Two subscription/ordering details in `Start()` exist to fix specific silent failures, and both read as redundant if you don't know why:

- **The bare `_ = RefreshAsync()` immediately after `_mediaManager.Start()` is not belt-and-braces.** `Start()` synchronously fires `OnAnySessionOpened`/`OnAnyMediaPropertyChanged` for sessions that already exist — i.e. music that was already playing before this app launched — *before* it sets its own internal started flag. Those callbacks reach `GetActiveSession()`, which bails on `!IsStarted`, and `RefreshAsync`'s catch-all swallows the rest. Net effect without that extra refresh: launch the app while Spotify is already playing and the widget stays empty until something incidental happens. Don't delete it as a duplicate of the event subscriptions.
- **`OnFocusedSessionChanged` is subscribed because focus changes are otherwise invisible.** Session selection prefers `GetFocusedSession()`, but nothing else tells this service when the OS's pick changes, so switching between two *already-open* sessions (pausing Spotify, starting a YouTube tab) would never trigger a refresh on its own.

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

### Memory and idle cost

This thing is up 24/7, so idle cost is a feature. Measured on the v1.1 published build: **~69–99 MB working set and 0.00% CPU** in steady state, the range depending on whether the widget is currently displayed (cover art, tinted pill and the marquee all cost pages). The CPU figure is the important one — it was not always 0. Four decisions hold this line, and three look like micro-optimization until you measure:

- **No UIA on the poll path** (above). This was the CPU and hitching fix, not a memory one.
- **`EnableCompressionInSingleFile` is off**, deliberately. A compressed single-file bundle cannot be memory-mapped — the runtime decompresses every assembly it touches into private memory. Measured here: 116 MB working set / 204 MB private compressed, versus 76 MB / 87 MB uncompressed. It costs ~140 MB of disk (89 MB → 231 MB .exe) to save ~40 MB of RAM, which is the right trade for this app. Don't turn it back on to shrink the download.
- **Workstation, non-concurrent GC** (`ServerGarbageCollection=false`, `ConcurrentGarbageCollection=false`). Server GC would reserve per-core heaps for an app that barely allocates; background GC costs a dedicated thread and extra committed memory to avoid pauses a heap this small will never produce.
- **`Core/MemoryTrimmer.cs`** hands the idle working set back to the OS via `SetProcessWorkingSetSize(-1, -1)`. WPF's startup burst (XAML parse, first layout/render, JIT) leaves far more resident than steady state needs, and an idle tray process never comes under the memory pressure that would reclaim it.

`MemoryTrimmer` is throttled and **event-driven on purpose**. Trimming forces pages to fault back in on next use, so doing it aggressively trades a smaller number in Task Manager for exactly the stutter this is all meant to avoid. It runs 15s after startup, on the "nothing is playing" transition, and from the poll tick *only while the widget is hidden* — never while visible, because that's when the marquee is animating. If you add a new trim call site, keep that rule.

Set expectations honestly about what the trim buys: it resets the floor, it does not cap anything. A trim drops the working set hard (~16 MB was observed immediately after the startup trim) and it then climbs back as the process touches those pages again. It is also genuinely inert while media is playing — if you're debugging "why didn't it trim", check whether the widget is visible before suspecting the code. `EnumChildWindows` on `Shell_TrayWnd` filtered to this process will tell you (`FindWindowEx` will not reliably — use the enumerating call).

Note that a first measurement shortly after launch will show something like 9% CPU: that's single-file startup, JIT/R2R warm-up, first render and UIA COM init, and it settles within a minute. Measure steady state, not the first 30 seconds.

### Wiring

No MVVM framework — plain C# events, wired directly in `App.xaml.cs`: `MediaSessionService.NowPlayingChanged` → `TaskbarWidgetWindow.UpdateNowPlaying`; the widget's `PreviousRequested`/`PlayPauseRequested`/`NextRequested` → the corresponding `MediaSessionService` command methods. Transport clicks don't call the async command methods directly — they go through `App.RunCommandAsync`, because a bare `_ = SomethingAsync()` puts any fault (e.g. the SMTC session dictionary being mutated mid-enumeration) on an unobserved `Task`, where it vanishes silently and the button just appears to do nothing. `DispatcherUnhandledException` is deliberately marked `Handled = true`: a recoverable UI fault should be logged, not allowed to take down a widget the user can only restart manually.

`App.xaml.cs` also owns the single-instance guard, unhandled-exception logging, and the tray icon (`Tray/TrayIconService.cs` — a `System.Windows.Forms.NotifyIcon` carrying a "Run at startup" toggle and an "Exit" item; Exit is the only way to close the app, since there's no visible main window).

"Run at startup" is backed by `Core/StartupRegistration.cs`, writing `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — HKCU rather than HKLM for the same reason the single-instance mutex is `Local\`-scoped: this is a per-user app, and HKCU needs no elevation, so the toggle just works instead of prompting for admin. Two details that look like polish but aren't: the registered value is **quoted**, because this repo lives under a path containing spaces and an unquoted `Run` value is parsed at the first space; and `IsEnabled()` returns true only when the entry points at the *current* executable, so a stale entry left by a copy that has since moved reads as "off" rather than showing a tick for a path that no longer launches this build. The tray menu re-reads the registry on `Opening` instead of caching at construction, since Task Manager's Startup tab can flip the same entry behind the app's back. Toggling also deletes the pre-1.1 `TaskbarMediaWidget` value name (`LegacyValueName`), so upgrading from an older build can't leave two `Run` entries both launching the widget at logon.

### Gotchas worth knowing before touching this code

- `SetWindowPos` in `Interop/NativeMethods.cs` must stay a classic `[DllImport]`, not a `[LibraryImport]` source-generated binding — the source-generated version has been observed to break topmost/visibility behavior for a window reparented into another process.
- `UseWindowsForms=true` (needed only for the tray icon) plus `UseWPF=true` means `Application`, `UserControl`, `Size`, `Color`, and `Timer` are ambiguous between `System.Windows.*` and `System.Windows.Forms`/`System.Drawing`/`System.Timers` — fully qualify these where the compiler flags CS0104 rather than adding blanket `using` aliases.
- The single-instance mutex is `Local\`-scoped, not `Global\`, on purpose: every logged-on user gets their own `explorer.exe` and their own taskbar, so every user should get their own widget instance. A `Global\` mutex would block a second user's copy under fast user switching or RDP.
- `Resources/app.ico` needs **both** `<ApplicationIcon>` and a `<Resource Include>` entry in the `.csproj`, and neither substitutes for the other: `ApplicationIcon` only stamps the icon into the .exe's Win32 resources, while `TrayIconService` loads the same file through a `pack://` URI at runtime to pick a size-appropriate variant for the notification area (`Icon.ExtractAssociatedIcon` would hand back a downscaled 32px one instead). `.ico` has no default build action, so dropping the `<Resource>` line silently falls the tray back to the stock system icon.
- DPI awareness is declared via the `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>` project property (not `app.manifest` directly) — WinForms' own DPI configuration conflicts with a hand-written manifest DPI block when both toolkits are in the same project (`WFO0003`). This is required, not optional: the positioning math reads DPI straight off the taskbar HWND via `GetDpiForWindow`.
- `System.IO` is **not** among this project's implicit usings — `Path` and `File` both fail with CS0103 unqualified. Existing code fully qualifies them (`System.IO.Path.Combine`) rather than adding a `using`, because `Path` would then be ambiguous with `System.Windows.Shapes.Path` under `UseWPF`.
- `Assembly.Location` returns an empty string in a single-file app (IL3000), which is exactly how this ships — use `AppContext.BaseDirectory` to locate files next to the executable. `Core/StartupRegistration.cs` hit this when building the autostart command.
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
10. Confirm the tray shows the custom music-note icon, not the generic stock Windows icon — a fallback to the stock icon means the `<Resource Include>` for `app.ico` was lost (see Gotchas) and is logged as a warning.
11. Let it sit for a few minutes, then check Task Manager: CPU should read 0% and working set should sit in the 70–100 MB band without climbing indefinitely. CPU that never drops below ~1% means something reintroduced per-tick work — the usual suspect is a UI Automation call back on the poll path. Note the first ~30s after launch legitimately shows several percent (startup, JIT, first render); measure after that.

For anything that misbehaves during these, read `%LOCALAPPDATA%\Taskbar Tool\log.txt` — attach/reattach, SetParent failures, automation timeouts, and Explorer-restart recovery all log there, and it's the only place a swallowed background-task failure becomes visible.

## Keeping the docs in sync

`README.md` is the user-facing copy of the same facts, not a pointer to this file, and it independently restates several things that go stale together:

- the version badge (currently `1.1.0`) and the install/publish commands,
- the component table,
- the **Resource usage** table (CPU, working-set range, installed .exe size) — keep it consistent with the Memory and idle cost section above,
- the known-limitations list, currently left-aligned-taskbar overlap, single-monitor-only, and no automated tests,
- the deliberately-out-of-scope list, which duplicates the one at the top of this file.

If you change positioning behavior, scope, limitations, resource characteristics, or the publish shape, update README too. `docs/reference-fluentflyout-taskbar-widget.md` is background on the technique and doesn't describe current code, so it only needs changing if the underlying Win32 approach does. `ROADMAP.md` is aspirational and should not be edited to match implementation work unless a roadmap item actually ships.

## Licensing note

FluentFlyout (the reference material) is GPL-3.0-or-later. This project reimplements the *technique* freely, not its code — no files were copied. Keep it that way if pulling in more reference material later.
