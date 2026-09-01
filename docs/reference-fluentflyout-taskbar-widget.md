# Reference notes: how FluentFlyout docks a widget onto the real taskbar

These notes were captured by reading the source of [FluentFlyout](https://github.com/unchihugo/FluentFlyout) (GPL-3.0-or-later), which was cloned into this folder purely for research and has since been deleted. Nothing here is copied code — it's a summary of the *technique*, written fresh, kept as background for building our own `TaskbarMediaWidget` (see root `CLAUDE.md`).

## What FluentFlyout is

A Windows 11 WPF app (Fluent 2 styled) that shows flyouts for media controls, lock-key status, and a volume mixer, plus an optional "taskbar widget" that shows now-playing info directly embedded in the Windows taskbar. Our project only cares about that last piece.

## The taskbar widget: real embedding, not an overlay

FluentFlyout's taskbar widget is a **true child-window embed into the taskbar's own HWND** (`Shell_TrayWnd`), not a floating always-on-top window drawn over it. The sequence:

1. **Locate the taskbar window**: `FindWindow("Shell_TrayWnd", null)` for the primary monitor's taskbar (`FindWindow("Shell_SecondaryTrayWnd", ...)` + `EnumWindows`/`EnumThreadWindows` for secondary monitors).
2. **Create a normal WPF window**, get its HWND via `WindowInteropHelper`.
3. **Flip its window style** from `WS_POPUP` to `WS_CHILD` via `GetWindowLong`/`SetWindowLong(GWL_STYLE)`.
4. **`SetParent(hwnd, taskbarHandle)`** — this is the actual reparenting call. From this point the window is a genuine child of the taskbar, not a separate top-level window.
5. **Position purely via `SetWindowPos`**, using client-relative coordinates obtained via `ScreenToClient` — WPF's own `Window.Left`/`Top`/layout system does not apply once a window is reparented like this. `SetWindowPos` must be declared with `[DllImport]`, not the newer `[LibraryImport]` source-generator — FluentFlyout's code has an explicit comment that `LibraryImport` breaks topmost/visibility behavior for this specific call.
6. **A GDI region** (`CreateRectRgn`/`CombineRgn`/`SetWindowRgn`) is applied in FluentFlyout because its reparented window is sized to the *entire taskbar* (so it can also host an independent audio visualizer control at a different position on the same taskbar-wide canvas) — it carves out only the occupied sub-rects for paint/hit-testing. **This is not required by the reparenting technique itself** — a single-purpose widget can just size its child window to its own natural content rect and skip GDI regions entirely.
7. **No `SHAppBarMessage`/APPBAR API is used anywhere.** The widget doesn't reserve taskbar space (like a real appbar would) — it just rides inside the taskbar's existing client area, drawn over whatever was already there at that position.

## Staying correctly positioned (resilience)

- **A ~1200–1500ms poll timer** re-resolves the taskbar handle and re-applies position/size every tick, regardless of any explicit event — this is the primary resilience mechanism (self-healing, not purely event-driven).
- **`WM_DPICHANGED` / `WM_DPICHANGED_AFTERPARENT`** are handled explicitly to force a re-measure/re-position pass on scale changes. `app.manifest` must declare `dpiAwareness=PerMonitorV2` — without it, the WPF window's DPI-awareness context can mismatch Explorer's and all the `GetDpiForWindow(taskbarHandle)`-based physical-pixel math goes wrong on non-100%-scale monitors.
- **`WM_DISPLAYCHANGE`** and **`WM_SETTINGCHANGE`** (specifically `SPI_SETWORKAREA`) are handled for resolution/work-area changes, funneled through a debounce timer before recomputing geometry.
- **`WM_TASKBARCREATED`** (registered once via `RegisterWindowMessage("TaskbarCreated")`) detects `explorer.exe` restarts. Critically, FluentFlyout does **not** just re-`SetParent` the existing window when this fires — it closes the old window and constructs a brand new one. This is required, not a style choice: per Win32 semantics, `DestroyWindow` on a parent also destroys true `WS_CHILD` windows, so when Explorer dies, the reparented widget HWND is destroyed as a side effect and cannot be revived — only recreated. Before recreating, it polls `FindWindow("Shell_TrayWnd")` + `GetWindowRect` (up to ~60s) until the new taskbar has real geometry.

## Positioning along the taskbar

FluentFlyout offers three placement modes (near-start, center, near-end), computed per-monitor in raw (non-WPF-scaled) coordinates via its own monitor-enumeration helper (`EnumDisplayMonitors`/`GetMonitorInfo`/`GetDpiForMonitor`), converting to WPF's DPI-scaled coordinates only at the very end.

Placement anchors are found via **UI Automation**, not fixed pixel math where avoidable:

- Queries `Shell_TrayWnd`'s automation tree for elements by `AutomationId` — confirmed real IDs in use: `"WidgetsButton"` (native Windows 11 Widgets icon — always sits immediately right of the Start/Search/Task-View cluster, regardless of whether the taskbar is Left- or Center-aligned), `"SystemTrayIcon"`, `"TaskbarFrame"` (used to get an accurate taskbar client rect, since a raw `GetWindowRect` on `Shell_TrayWnd` can include invisible margins on some configurations).
- Lookups run on a background `Task.Run` with a short timeout (~1000ms find / ~500ms bounds-fetch), cached, and invalidated on `ElementNotAvailableException`/`COMException`/stale results.
- A fixed physical-pixel fallback constant is used only if the automation lookup fails.
- **Notably, FluentFlyout never looks up the Start button itself** — it has no "touching Start" placement mode, only "near start" (offset from the Widgets button). Finding the Start button specifically is new work for our project, not something we can port from here.

Vertical (left/right-docked) taskbars are supported via a 90° `RotateTransform` and swapped primary/cross-axis math, re-derived every timer tick by comparing taskbar width vs. height — not a separate event path.

## Media session data

Media info comes from the `Dubya.WindowsMediaController` NuGet package (v2.5.6 at time of reading), which wraps Windows' `GlobalSystemMediaTransportControlsSession` (SMTC) WinRT APIs: session enumeration, property-changed/playback-state-changed/session-closed events, and play/pause/next/previous commands. FluentFlyout picks "the" active session by filtering to an allow/block-list (out of scope for us) and preferring the OS-focused session, falling back to the first available one.

Thumbnail conversion is a short, unremarkable pattern: `IRandomAccessStreamReference.OpenReadAsync().AsStreamForRead()` → `BitmapImage` (`BeginInit`/`DecodePixelWidth`/`StreamSource`/`EndInit`/`Freeze`) — simple enough to write fresh rather than reuse.

## Takeaways for our project

The reusable technique, stripped to essentials: find `Shell_TrayWnd`, flip `WS_POPUP→WS_CHILD`, `SetParent` into it, drive size/position purely via `SetWindowPos`/`ScreenToClient` on a timer, and handle `WM_DPICHANGED`/`WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE`/`WM_TASKBARCREATED` (the last one requiring full window *recreation*, not revival). No APPBAR API needed. For anchoring next to the Start button specifically — the one piece with no prior art here — the same UI-Automation-with-fallback pattern applies, just against a (currently unverified) Start-button `AutomationId` instead of `WidgetsButton`.
