# Taskbar Tool

![version](https://img.shields.io/badge/version-1.1.0-brightgreen)
![platform](https://img.shields.io/badge/platform-Windows%2011-0078D6?logo=windows11&logoColor=white)
![framework](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![ui](https://img.shields.io/badge/UI-WPF-informational)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20ARM64-lightgrey)
![status](https://img.shields.io/badge/status-personal%20project-yellow)
![tests](https://img.shields.io/badge/tests-manual%20only-orange)

A compact "now playing" media widget that lives **inside the real Windows taskbar** — not a floating overlay, not a flyout. It reflects whatever is currently playing anywhere on the system via the OS-level System Media Transport Controls (SMTC): Spotify, a browser tab, VLC, YouTube Music, anything Windows itself already knows how to show media controls for.

## What it looks like

The widget docks flush against the taskbar's left edge and shows cover art, an animated mini equalizer, title, artist, live 2px track progress line, and transport controls:

```
[ 🎵 ılı Track Title          ⏮ ▶ ⏭ ]   [Start] [pinned apps...]           [tray icons] [clock]
  ═══════════════════ (live progress bar)
```

- **Interactive Flyout**: Left-clicking the widget opens a sleek Windows 11 Fluent 2 flyout above the taskbar with a scrubbable timeline slider, large album art, source app badge, volume control, and shuffle/repeat buttons.
- **Mouse Volume Control**: Hovering over the widget and scrolling the mouse wheel smoothly turns system volume up or down (triggering Windows 11's native volume OSD); middle-clicking toggles mute.
- **Dynamic Accent**: The pill picks up a subtle tint sampled from the album art's dominant color.
- **Standby Mode**: When nothing is playing, the widget displays a clean standby pill so you always know it's ready (or can be configured to hide completely via the tray menu).

## Why it's built the way it is

Most "taskbar widget" projects are actually floating always-on-top windows that *track* the taskbar's position. This one is different: the widget window is surgically reparented into the taskbar's own window (`Shell_TrayWnd`) as a genuine Win32 child window (`SetParent` + `WS_CHILD`). That means it:

- gets **clipped** like a real taskbar element when a maximized window is dragged over the taskbar
- **never appears in Alt-Tab**
- survives DPI changes, display changes, and even `explorer.exe` crashing/restarting (it's automatically rebuilt from scratch when Explorer comes back)

This is a from-scratch reimplementation of the *technique*, inspired by studying [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature (see [`docs/reference-fluentflyout-taskbar-widget.md`](docs/reference-fluentflyout-taskbar-widget.md)) — no code copied from it. FluentFlyout is GPL-3.0-or-later; see [Credits](#credits) below.

## Requirements

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (targets `net10.0-windows10.0.22000.0` — the Windows SDK contract needed for the SMTC WinRT APIs)

.NET is only needed to *build*. The published .exe is self-contained and runs on a machine with no .NET installed.

## Install

```powershell
dotnet publish src/TaskbarMediaWidget/TaskbarMediaWidget.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64 -o dist
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This installs to `%LOCALAPPDATA%\Programs\Taskbar Tool`, adds a Start Menu shortcut, and launches the app — all per-user, no elevation. `install.ps1 -Uninstall` reverses it.

Autostart is not configured by the installer on purpose: use the tray icon's **Run at startup** item, so there's one source of truth that stays in sync with Task Manager's Startup tab.

> The published .exe is ~231 MB because it bundles the .NET runtime **uncompressed** — compression would roughly halve the file but cost ~40 MB of extra RAM at runtime, since a compressed bundle can't be memory-mapped. See [`CLAUDE.md`](CLAUDE.md) for the measurements.

## Build & run (development)

```powershell
dotnet build Taskbar-Tool.slnx
dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj
```

Or open `Taskbar-Tool.slnx` in Visual Studio and run with `TaskbarMediaWidget` as the startup project.

The app has no visible main window — it's tray-only. Right-click the tray icon (it may be tucked behind the **`^`** hidden-icons chevron the first time) for two items: **Run at startup**, which toggles a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry (no admin prompt), and **Exit**, which is the only way to close the app. Launching a second copy while one is already running just exits immediately (single-instance guarded).

## How it works, briefly

| Piece | Responsibility |
|---|---|
| `Taskbar/TaskbarWidgetWindow.xaml.cs` | Reparents itself into `Shell_TrayWnd`; a self-healing poll (1.3s while visible, 5s while hidden) plus DPI/display-change hooks keeps it positioned and re-attaches after Explorer restarts |
| `Taskbar/TaskbarLocator.cs` | Resolves the taskbar's handle, DPI, and precise client rect — caching the rect and doing its UI Automation work off the UI thread |
| `Controls/NowPlayingWidgetControl.xaml.cs` | Interactive taskbar pill with live 2px progress bar, animated mini equalizer, and mouse-wheel volume control |
| `Flyouts/MediaFlyoutWindow.xaml.cs` | Fluent 2 media card with timeline seek scrubber, high-res cover art, and transport/volume controls |
| `Core/VolumeHelper.cs` | Smooth Windows volume adjustments & mute toggling triggering native Windows 11 OSD |
| `Core/AppSettings.cs` | Manages per-user preferences (e.g., toggleable "Hide widget when idle") |
| `Core/MemoryTrimmer.cs` | Returns the idle working set to the OS, throttled and only while the widget is hidden |
| `Media/MediaSessionService.cs` | Wraps [`WindowsMediaController`](https://github.com/DubyaDude/WindowsMediaController) (an SMTC wrapper) and manages playback, timeline, and session selection |
| `Media/ThumbnailConverter.cs` | Decodes an SMTC thumbnail stream into a WPF `BitmapImage` |
| `Media/AccentColorExtractor.cs` | Reduces that thumbnail to one vibrant color (weighted hue histogram) for the pill tint |
| `Core/StartupRegistration.cs` | Reads/writes the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` entry behind the tray's "Run at startup" toggle |
| `Tray/TrayIconService.cs` | Notification area icon with "Open Media Flyout", "Hide widget when idle", "Run at startup", and "Exit" |

Full architectural notes, gotchas, and the reasoning behind non-obvious decisions live in [`CLAUDE.md`](CLAUDE.md).

## Resource usage

Measured on the v1.1 build, running in the tray:

| | |
|---|---|
| CPU (steady state) | 0.00% |
| Working set | ~69–99 MB (lower end with the widget hidden) |
| Disk (installed .exe) | ~231 MB |

Expect a brief CPU spike in the first ~30 seconds after launch — that's single-file startup, JIT warm-up and the first render, not the steady state.

## Known limitations

- **Left-aligned taskbars**: the widget always docks to the taskbar's left edge. If your taskbar alignment is set to **Left** (Settings → Personalization → Taskbar), the widget will visually overlap the real Start button. It's tuned for the Windows 11 default (**Center**-aligned), where the left edge is empty space.
- Single monitor only — secondary-monitor taskbars aren't targeted.
- No automated tests — this manipulates `explorer.exe`'s real window tree, which isn't meaningfully unit-testable. Verification is manual (checklist in `CLAUDE.md`).

## Deliberately out of scope

Flyouts, a volume mixer, lock-key indicators, an audio visualizer, MSIX/Store packaging, licensing gates, localization, a settings UI. If a feature isn't "show now-playing info docked in the taskbar," it doesn't belong here.

## Credits

- [FluentFlyout](https://github.com/unchihugo/FluentFlyout) (GPL-3.0-or-later) — studied as reference for the taskbar-reparenting technique. No code was copied; see [`docs/reference-fluentflyout-taskbar-widget.md`](docs/reference-fluentflyout-taskbar-widget.md) for what was learned from it.
- [WindowsMediaController](https://github.com/DubyaDude/WindowsMediaController) (MIT) — the SMTC wrapper this project builds on.
