# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**Taskbar Tool** (v1.1+; the code namespace is still `TaskbarMediaWidget` — see Naming below) is a Windows 11 WPF app designed to embed a compact, feature-rich "now playing" media widget genuinely inside the real Windows taskbar (`Shell_TrayWnd` as a `WS_CHILD`), accompanied by a Fluent 2 interactive flyout, real-time audio visualizer, multi-session SMTC switcher, native Discord Rich Presence, hardware & network monitor, and a modern Settings UI.

It reflects whatever app is currently playing media via the OS-level System Media Transport Controls (SMTC) — Spotify, browser tabs, YouTube Music, VLC, Apple Music, etc.

This is a from-scratch reimplementation, inspired by (but not copied from — see licensing note below) [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature, which was studied as reference and is summarized in `docs/reference-fluentflyout-taskbar-widget.md`.

## Build & run

- **Build**: `dotnet build Taskbar-Tool.slnx` — note the `.slnx` extension: this is the XML-based solution format the .NET 10 SDK generates (`dotnet new sln`), not a classic `.sln` file.
- **Run**: `dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj` (or launch from Visual Studio with `TaskbarMediaWidget` as startup project).
- **Publish + install**: `dotnet publish src/TaskbarMediaWidget/TaskbarMediaWidget.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist`, then `powershell -ExecutionPolicy Bypass -File .\install.ps1` (`-Uninstall` to remove). The install script is per-user by design — `%LOCALAPPDATA%\Programs\Taskbar Tool` plus a Start Menu shortcut, no elevation — and deliberately does *not* touch autostart, because the tray's "Run at startup" item is the single source of truth for that.
- **Naming**: the product is **Taskbar Tool** (`<Product>`/`<AssemblyTitle>`), the assembly and .exe are `TaskbarTool`, and the code namespace is still `TaskbarMediaWidget`. The namespace is deliberately left alone — renaming it touches every file for no user-visible gain. Don't "fix" the mismatch.
- **No test project** — this is fundamentally not unit-testable; verification is manual (see below). Pure helpers are the exception: `Media/AccentColorExtractor.cs` has no WPF-window dependency and can be exercised against synthetic bitmaps.
- **Diagnostics**: `%LOCALAPPDATA%\Taskbar Tool\log.txt` (`Core/AppLog.cs`) is the primary diagnostic channel. It rotates at 1 MB to `log.txt.old`, and it collapses runs of the *exact same* line into one entry with a trailing `(repeated N more times)` — a failure that recurs on every poll tick shows up as one line, so don't read a short log as "nothing went wrong."
- **Single Instance & Cross-Process Wakeup**: Enforced via a `Local\`-scoped named mutex (`Core/SingleInstanceGuard.cs`, per-session deliberately — see Gotchas). When a second copy is launched (e.g. user clicks the Start Menu shortcut while the app is already running), it broadcasts `TaskbarTool_ShowFlyoutMessage` via `NativeMethods.RegisterWindowMessage` and `PostMessage(HWND_BROADCAST)` to signal the running instance to immediately pop up the Media Flyout / Settings, and then the second instance cleanly shuts down.
- **Startup Mode**: Boot launch via the registry passes `--startup` to run silently in the background tray. Manual launch (without `--startup`) automatically displays the Media Flyout after a brief 400ms delay to give the user immediate visual feedback that the application is active.
- **Target framework**: `net10.0-windows10.0.22000.0` (matches installed SDK; the `10.0.22000.0` suffix is required for the SMTC WinRT APIs).
- **Zero External Dependencies**: Zero external packages are used for Discord RPC, WASAPI audio loopback, or CoreAudio device management. Everything is built natively with named pipes and Windows COM interop to maintain extreme lightness and fast startup.
- **0 Warnings Baseline**: The build is expected to compile with **0 warnings and 0 errors**. Treat any new warning as something to fix immediately.

## Architecture

### The reparenting technique (Core Engine)

`Taskbar/TaskbarWidgetWindow.xaml.cs` is a WPF window that gets turned into a genuine `WS_CHILD` of the taskbar's own HWND (`Shell_TrayWnd`) via `SetParent` — not an always-on-top overlay. Once reparented, WPF's own `Window.Left`/`Top` no longer apply; position and size are driven entirely by raw `SetWindowPos` calls (`Interop/NativeMethods.cs`) in the taskbar's client coordinate space. A poll timer continuously re-resolves the taskbar handle and re-applies position (self-healing, not purely event-driven) — 1.3s while the widget is visible, backed off to 5s while it's hidden — and explicit hooks handle `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE` for scale/resolution/theme changes. Each poll tick caches the last-applied rect and the widget's content-version counter, so it skips the WPF measure pass and the `SetWindowPos` call entirely on ticks where nothing actually changed.

**Explorer restarts require full window recreation, not revival, and the recovery listener cannot live on the reparented window itself**: `DestroyWindow` on a parent also destroys true `WS_CHILD` children, so when `explorer.exe` dies, the reparented widget HWND is destroyed as a side effect and cannot be reused — `TryReposition` notices this via `NativeMethods.IsWindow` on the next poll tick and just stops polling. Recovery is owned by a separate class, `Taskbar/ShellWatchdogWindow.cs`: a hidden, **never-reparented** top-level window that listens for both `WM_TASKBARCREATED` and the cross-process `TaskbarTool_ShowFlyoutMessage` broadcast. This split exists because broadcasts explicitly exclude child windows — the instant `TaskbarWidgetWindow` reparents, it becomes structurally ineligible to ever receive broadcasts itself. `App.xaml.cs` owns the watchdog, waits for the taskbar to actually come back (`HandleExplorerRestartAsync`), then calls `RecreateWidgetWindow()` to close the dead instance and construct a fresh one. `App.xaml.cs.CreateWidgetWindow()` immediately hydrates it from `MediaSessionService.CurrentNowPlaying` right after `Show()`. Furthermore, whenever `AttachToTaskbar()` succeeds, it calls `UpdateNowPlaying(_currentNowPlaying)` so that the Standby pill becomes visible immediately without waiting for an active audio stream.

### Positioning & Alignment
- **Center-Aligned Taskbar (Windows 11 Default)**: The widget is positioned at a fixed margin from the left edge of `Shell_TrayWnd` (`GetTaskbarRect().Left + margin`), staying distinct from centered app icons.
- **Left-Aligned Taskbar Support**: `TaskbarWidgetWindow.GetLeftEdgeMarginLogicalPx()` checks `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarAl`. If set to `0` (Left alignment), it applies a base margin of `148.0` DIPs to clear the Start button and search pill, combined with `AppSettings.TaskbarOffsetPx` configurable via the Settings slider (-50 to +200 DIPs).

### Taskbar geometry: UI Automation off the UI thread

`TaskbarLocator.cs` queries the bounding rectangle of the `TaskbarFrame` automation element off-thread to avoid invisible taskbar margins on Windows 11 builds:
- `GetTaskbarRect` never blocks. It returns the cached refined rect, or falls back to `GetWindowRect` (cheap, in-process) while a background refresh is in flight.
- `BeginRefreshRect` guards with an `Interlocked` flag so poll ticks cannot pile up thread-pool work against an unresponsive shell.
- The cache is dropped only by `InvalidateRectCache()`, called on `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, `SPI_SETWORKAREA`, and re-attaching to a new taskbar HWND.

### Media Session Management & Multi-Session Switcher
`Media/MediaSessionService.cs` wraps `Dubya.WindowsMediaController.MediaManager`:
- **Multi-Session Enumeration**: `GetAvailableSessionsAsync()` enumerates all concurrent SMTC sessions (Spotify, Chrome, VLC, Apple Music, etc.).
- **Session Switching**: `SelectSession(id)` and `SelectNextSession()` cycle through active media sessions. The pill context menu and flyout header provide one-click session toggles with an active session indicator badge (e.g. `1/2`).
- **Focus Player Window**: `FocusActiveSessionApp()` resolves the active session's process ID / window handle and restores it to the foreground (`ShowWindow(SW_RESTORE)` + `SetForegroundWindow`).
- **Ignore List / Blacklist**: Filters out unwanted audio sessions (e.g., Discord voice, Slack, Telegram) via `AppSettings.IgnoredApps`.
- **Concurrency Protection**: Uses `Interlocked.Increment(ref _refreshGeneration)` to drop out-of-order SMTC property updates, plus a 45s heartbeat calling `ForceUpdate()`.

### Interactive Fluent 2 Media Flyout
`Flyouts/MediaFlyoutWindow.xaml`/`.cs`:
- Windows 11 Fluent 2 card floating above the taskbar with timeline scrubber slider, high-res cover art, track info, transport controls, and volume slider.
- **Audio Device Switcher Bar**: Live dropdown querying active playback endpoints and allowing seamless default device switching without opening Windows Settings.
- **Session Switcher Button**: Displays session count and cycles active players.
- **One-Click Copy Track Info**: Copies `Artist - Title` to the clipboard with animated checkmark confirmation feedback.
- **Quick Tools**: System actions (Lock, Sleep, Task Manager) + live hardware and network telemetry.
- **Activation**: Opened via widget pill left-click, tray left-click, Start Menu shortcut activation, or global shortcut **`Win + Shift + Z`**.

### Fluent 2 Settings Window
`Settings/SettingsWindow.xaml`/`.cs`:
- Modern tabbed settings UI:
  - **General**: Run at startup, Global hotkey (`Win+Shift+Z`), Hide when idle, Discord Rich Presence.
  - **Appearance**: Layout switcher (Standard, Compact, Minimal, Wide), Progress bar style, Taskbar margin offset slider, WASAPI visualizer toggle, Standby network stats badge.
  - **Audio & Whitelist**: Default playback device selector, Ignored applications blacklist.
- Backed by `Core/AppSettings.cs`, persisting configurations in `HKCU\Software\TaskbarMediaWidget`.

### Native WASAPI Loopback Audio Visualizer
`Audio/WasapiAudioVisualizerService.cs`:
- Captures active audio output via Windows CoreAudio loopback (`IAudioClient`, `IAudioCaptureClient`).
- Computes real-time 3-band spectral energy (Bass: <250Hz, Mid: 250Hz–2.5kHz, Treble: >2.5kHz) using fast bandpass aggregation and smooth exponential decay.
- Dynamically animates mini equalizer bars on the taskbar widget pill. Pauses capture thread when audio is silent to maintain 0.00% CPU overhead.

### Zero-Dependency Discord Rich Presence
`Integrations/DiscordRpcService.cs`:
- Directly connects to Discord desktop client via local named pipe (`\\.\pipe\discord-ipc-0` through `4`).
- Serializes standard Discord IPC handshake and `SET_ACTIVITY` JSON payloads.
- Syncs current track title, artist, album art, elapsed/remaining timestamps, and player identity.
- Auto-reconnects in the background when Discord starts or exits.

### Centralized COM Interop (`Interop/ComAudio.cs`)
- All CoreAudio and WASAPI COM definitions (`MMDeviceEnumeratorComObject`, `PolicyConfigComObject`, `IMMDeviceEnumerator`, `IMMDevice`, `IAudioClient`, `IAudioCaptureClient`, `IAudioEndpointVolume`, `IPolicyConfig`, `IPropertyStore`, etc.) are centralized in `Interop/ComAudio.cs`.
- **Crucial Rule**: Never duplicate COM interface definitions with identical GUIDs across separate namespaces. In .NET Core, duplicate definitions cause Runtime Callable Wrapper (RCW) type mismatch errors (`InvalidCastException: Specified cast is not valid`).

### Hardware & Network Telemetry
`Core/HardwareMonitorService.cs`:
- Samples CPU usage, RAM utilization, live download/upload transfer rates (via `System.Net.NetworkInformation`), and laptop battery percentage/charging status.
- Rendered in the Flyout Tools panel and optionally on the taskbar pill during standby mode.

### Widget Layout Modes
`Controls/NowPlayingWidgetControl.xaml`/`.cs`:
- **Standard**: Full album art, scrolling marquee title/artist, live 2px progress bar, and media controls.
- **Compact**: Small album art, play/pause toggle, and session badge.
- **Minimal**: Ultra-compact glyph-only pill for clean desktop setups.
- **Wide**: Expanded pill with audio visualizer spectrum and complete transport controls.
- Context menu: Copy Track, Focus Player, Next Audio Session, Open Flyout, Settings.
- Double-click: Immediately brings the active player window to the foreground.

### Memory & Idle Cost
Measured on published single-file build:
- **CPU (steady state)**: 0.00% (WASAPI loopback capture and marquee storyboard pause when idle).
- **Working set**: ~69–99 MB (drops to ~16 MB upon startup/idle trim via `Core/MemoryTrimmer.cs`).
- **Disk**: ~231 MB uncompressed single-file .exe (`EnableCompressionInSingleFile=false` enables memory-mapping to save private RAM).

## Gotchas worth knowing before touching this code

- **Audio COM Interfaces Must Stay Centralized in `Interop/ComAudio.cs`**: Duplicating COM classes or interfaces (e.g. `MMDeviceEnumeratorComObject`, `IMMDeviceEnumerator`) in different namespaces causes the CLR COM type cache to bind the RCW to the first loaded type, causing `InvalidCastException: Specified cast is not valid` on subsequent casts.
- **Avoid Inexact Stream Reads on Named Pipes (CA2022)**: When reading from `PipeStream` in `DiscordRpcService`, always use `ReadExactly(buffer, offset, count)` rather than `Read()`.
- **Single-Instance Wakeup Requires Broadcaster**: `SingleInstanceGuard` uses `RegisterWindowMessage("TaskbarTool_ShowFlyoutMessage")` and `PostMessage(HWND_BROADCAST)`. The listener MUST live on `ShellWatchdogWindow` (a top-level unparented window), because reparented `WS_CHILD` windows do not receive broadcast messages.
- **Startup Visibility Contract**: In `TaskbarWidgetWindow.xaml.cs`, `AttachToTaskbar()` must call `UpdateNowPlaying(_currentNowPlaying)` upon attaching. Otherwise, if the app starts while no media is playing, the standby pill remains hidden until the next track change.
- **SetWindowPos Classic DllImport**: `SetWindowPos` in `Interop/NativeMethods.cs` must stay a classic `[DllImport]`, not `[LibraryImport]` source-generated binding.
- **UseWindowsForms + UseWPF Type Disambiguation**: `Application`, `UserControl`, `Size`, `Color`, `Timer`, and `Clipboard` are ambiguous between WPF and WinForms. Always fully qualify them (e.g., `System.Windows.Clipboard.SetText`) to avoid CS0104.
- **Single-Instance Mutex is `Local\`-Scoped**: Scoped to the user's interactive session so multi-user switching or RDP is not blocked.
- **Startup Launch Parameter**: Windows autostart writes `--startup` via `Core/StartupRegistration.cs`. Manual launches omit this flag, causing `App.xaml.cs` to show the Media Flyout after launch for immediate user confirmation.
- **Dual Icon Declaration**: `Resources/app.ico` needs both `<ApplicationIcon>` and `<Resource Include>` in `TaskbarMediaWidget.csproj`.
- **System.IO Unqualified Warnings**: `System.IO` is not in implicit usings; fully qualify paths (`System.IO.Path.Combine`) to prevent collisions with `System.Windows.Shapes.Path`.

## Verification

After building, manually verify the following flows:

1. **Now Playing Taskbar Embedding**: Play a track on Spotify/Chrome/VLC; confirm the pill docks at the taskbar's left edge, displays album art, live progress bar, and mini audio visualizer bars.
2. **True Win32 Child Clipping**: Drag a maximized window over the taskbar — verify the widget gets clipped like a native taskbar element and never appears in Alt-Tab.
3. **Left-Aligned Taskbar Positioning**: In Windows Settings → Personalization → Taskbar, switch alignment to Left. Confirm the widget shifts to clear the Start button (~148 DIPs) and responds to the margin slider in Settings.
4. **Interactive Flyout**: Left-click the taskbar pill, press **`Win + Shift + Z`**, or click the tray icon — verify the Fluent 2 Media Flyout opens smoothly with seeker, volume slider, and audio output device switcher.
5. **Session Switcher**: Open two media players (e.g. Spotify and YouTube in a browser). Verify the session badge displays (e.g. `1/2`) and clicking "Next Session" switches active control seamlessly.
6. **Focus Player**: Double-click the taskbar pill or select "Focus Player Window" from the pill's right-click context menu; verify the playing app is restored to the foreground.
7. **Copy Track Info**: Click "Copy Track Info" in the pill context menu or flyout; confirm the text (`Artist - Title`) is copied and an animated checkmark is displayed.
8. **Settings Window**: Open Settings from the tray icon or flyout gear. Verify layout switching (Standard, Compact, Minimal, Wide), margin slider, Discord RPC toggle, and audio device selection.
9. **Single-Instance Wakeup**: With Taskbar Tool running, launch the Start Menu shortcut again. Confirm it signals the running instance and pops up the Media Flyout instead of exiting silently.
10. **Explorer Restart Recovery**: Run `taskkill /f /im explorer.exe`. Confirm that when Explorer restarts, `ShellWatchdogWindow` catches `TaskbarCreated` and re-attaches the widget cleanly without crashing.
11. **Resource Footprint**: Check Task Manager in steady state — verify 0.00% CPU and 70–100 MB working set.

## Keeping the docs in sync

`README.md` is the user-facing copy of the same facts, not a pointer to this file. If you change positioning behavior, scope, limitations, resource characteristics, or the publish shape, update `README.md` too. `ROADMAP.md` reflects the broader future vision for the utility suite.

## Licensing note

FluentFlyout (the reference material) is GPL-3.0-or-later. This project reimplements the *technique* freely, not its code — no files were copied. Keep it that way if pulling in more reference material later.
