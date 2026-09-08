# TaskbarMediaWidget

![platform](https://img.shields.io/badge/platform-Windows%2011-0078D6?logo=windows11&logoColor=white)
![framework](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![ui](https://img.shields.io/badge/UI-WPF-informational)
![arch](https://img.shields.io/badge/arch-x64%20%7C%20ARM64-lightgrey)
![status](https://img.shields.io/badge/status-personal%20project-yellow)
![tests](https://img.shields.io/badge/tests-manual%20only-orange)

A compact "now playing" media widget that lives **inside the real Windows taskbar** — not a floating overlay, not a flyout. It reflects whatever is currently playing anywhere on the system via the OS-level System Media Transport Controls (SMTC): Spotify, a browser tab, VLC, YouTube Music, anything Windows itself already knows how to show media controls for.

## What it looks like

The widget docks flush against the taskbar's left edge and shows cover art, title, artist, and transport controls (previous / play-pause / next) that control the source app directly:

```
[ 🎵  Track Title            ⏮ ▶ ⏭ ]   [Start] [pinned apps...]           [tray icons] [clock]
  ↑ anchored to the taskbar's left edge, independent of Start's position
```

When nothing is playing, the widget disappears entirely — no empty box left behind.

## Why it's built the way it is

Most "taskbar widget" projects are actually floating always-on-top windows that *track* the taskbar's position. This one is different: the widget window is surgically reparented into the taskbar's own window (`Shell_TrayWnd`) as a genuine Win32 child window (`SetParent` + `WS_CHILD`). That means it:

- gets **clipped** like a real taskbar element when a maximized window is dragged over the taskbar
- **never appears in Alt-Tab**
- survives DPI changes, display changes, and even `explorer.exe` crashing/restarting (it's automatically rebuilt from scratch when Explorer comes back)

This is a from-scratch reimplementation of the *technique*, inspired by studying [FluentFlyout](https://github.com/unchihugo/FluentFlyout)'s taskbar-widget feature (see [`docs/reference-fluentflyout-taskbar-widget.md`](docs/reference-fluentflyout-taskbar-widget.md)) — no code copied from it. FluentFlyout is GPL-3.0-or-later; see [Credits](#credits) below.

## Requirements

- Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (targets `net10.0-windows10.0.22000.0` — the Windows SDK contract needed for the SMTC WinRT APIs)

## Build & run

```powershell
dotnet build Taskbar-Tool.slnx
dotnet run --project src/TaskbarMediaWidget/TaskbarMediaWidget.csproj
```

Or open `Taskbar-Tool.slnx` in Visual Studio and run with `TaskbarMediaWidget` as the startup project.

The app has no visible main window — it's tray-only. Right-click the tray icon (it may be tucked behind the **`^`** hidden-icons chevron the first time) and choose **Exit** to close it. Launching a second copy while one is already running just exits immediately (single-instance guarded).

## How it works, briefly

| Piece | Responsibility |
|---|---|
| `Taskbar/TaskbarWidgetWindow.xaml.cs` | Reparents itself into `Shell_TrayWnd`; a ~1.3s self-healing poll (plus DPI/display-change hooks) keeps it positioned and re-attaches after Explorer restarts |
| `Taskbar/TaskbarLocator.cs` | Resolves the taskbar's handle, DPI, and precise client rect |
| `Media/MediaSessionService.cs` | Wraps [`WindowsMediaController`](https://github.com/DubyaDude/WindowsMediaController) (an SMTC wrapper) and picks one active session: focused → else playing → else whatever's available |
| `Media/ThumbnailConverter.cs` | Decodes an SMTC thumbnail stream into a WPF `BitmapImage` |
| `Tray/TrayIconService.cs` | The only visible chrome — a tray icon with an Exit item |

Full architectural notes, gotchas, and the reasoning behind non-obvious decisions live in [`CLAUDE.md`](CLAUDE.md).

## Known limitations

- **Left-aligned taskbars**: the widget always docks to the taskbar's left edge. If your taskbar alignment is set to **Left** (Settings → Personalization → Taskbar), the widget will visually overlap the real Start button. It's tuned for the Windows 11 default (**Center**-aligned), where the left edge is empty space.
- Single monitor only — secondary-monitor taskbars aren't targeted.
- Tray icon is the stock system icon (no custom `.ico` yet).
- No automated tests — this manipulates `explorer.exe`'s real window tree, which isn't meaningfully unit-testable. Verification is manual (checklist in `CLAUDE.md`).

## Deliberately out of scope

Flyouts, a volume mixer, lock-key indicators, an audio visualizer, MSIX/Store packaging, licensing gates, localization, a settings UI. If a feature isn't "show now-playing info docked in the taskbar," it doesn't belong here.

## Credits

- [FluentFlyout](https://github.com/unchihugo/FluentFlyout) (GPL-3.0-or-later) — studied as reference for the taskbar-reparenting technique. No code was copied; see [`docs/reference-fluentflyout-taskbar-widget.md`](docs/reference-fluentflyout-taskbar-widget.md) for what was learned from it.
- [WindowsMediaController](https://github.com/DubyaDude/WindowsMediaController) (MIT) — the SMTC wrapper this project builds on.
