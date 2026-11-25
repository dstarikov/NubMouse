# Changelog

## Unreleased

- **Settings window with a test pad** (tray menu ▸ **Settings and test pad…**, or
  `--settings`): every scrolling and middle-click setting in one place, with number boxes that
  go past the sliders' ends, beside a pad that shows what NubMouse sends — scroll events
  and units, middle clicks, presses that sent no click (time limit or deadzone), and native
  middle drags passed through with the drag key — so you can tune without leaving the window.
  A **General** tab has everything else: devices, the TrackPoint driver options and a **Fix it**
  button when the driver is still scrolling, startup, updates, and troubleshooting (version,
  where settings are saved, starting and opening a debug log without restarting).
- Fixed: the **Devices** list was always empty, so NubMouse couldn't be limited to the
  TrackPoint. Device names with an "&" in them now show it.
- **Slimmer tray menu:** the right-click menu keeps Enabled, **Settings and test pad…**, quick
  toggles for natural direction, direction lock and the scroll indicator, and the Devices,
  TrackPoint driver, Startup and Updates menus. The sliders and the rest of the scrolling,
  middle-click and native-drag options moved to the settings window.
- **Stickiness is a slider:** any ratio from 1.2× to 4.0× plus Fixed, with the Loose, Normal,
  Firm and Fixed presets marked under it (click one to jump to it). `stickiness=2.5` works on
  the command line too; the preset names still do. Your previous choice carries over.
- **A nicer scroll-direction indicator:** it now sits on the pointer, which is hidden while
  scrolling so the indicator stands in for it. Four looks (Dark, Light, Minimal, TrackPoint)
  and any size from 16 to 64 px, picked in the settings window with a live
  preview (`indicatorstyle=`, `indicatorsize=`). It's drawn with real transparency, so the
  purple fringe around its edge is gone and it has a soft shadow. Placing it beside the pointer
  is still possible as an advanced setting (`indicatoroffset=N`).
- Fixed: the scroll-direction indicator could stay on screen after scrolling stopped.
- **Native middle-drag (for 3D modeling apps)** (was "Native middle-drag passthrough"): the
  name now says what it's for, rotating and panning in Blender, CAD and other 3D apps. Instead
  of one modifier key it takes any number of shortcuts, each a key or a combination held
  together: Alt, Ctrl and Shift are tick boxes, and the settings window records any other key
  or combination (Space, Ctrl+Shift, a side mouse button…). Any shortcut works.
  `dragkeys=alt,ctrl+shift`; `dragmodifier=` still works. An existing setting carries over.
- **The TrackPoint driver is set up automatically (ELAN drivers):** while NubMouse is on,
  the driver's Middle Button Action is switched to Middle Click, and when it's paused or exits,
  back to Scrolling, so the driver's own scrolling works while NubMouse is off. No more
  trip to the TrackPoint settings. The switch waits until the TrackPoint is idle. New
  **TrackPoint driver ▸** submenu: **Set it to Middle Click while NubMouse is on** and
  **Back to Scrolling when paused or closed** (both on by default; untick the second to keep a
  plain middle click while paused), plus **Open TrackPoint driver settings…**. Uninstalling
  puts back a Scrolling setting that was switched.
- The "driver is still scrolling" notification is now only the fallback (other drivers, a
  failed switch, or Scrolling set again while NubMouse is on). It has **Set it now**
  (ELAN) and **Open TrackPoint settings** buttons, which also work from the notification
  centre, and it's withdrawn when NubMouse is paused. New `--fix-driver` and
  `--driver-settings` commands.
- **Axis lock** (on by default): each push scrolls straight up/down or left/right, like the
  TrackPoint driver's own scrolling, instead of drifting diagonally. Pushing clearly the other
  way switches direction without letting go. Toggle it with **Lock to one direction** in the
  tray menu, the new checkbox in the install dialog, or the `diagonal` / `axislock` options.
  **Stickiness** (settings window, or `stickiness=`) sets how readily it switches; `fixed`
  never switches, like the driver.
- Reorganized tray menu with **Startup ▸** and **Updates ▸** submenus. Speed can now be any
  value 1–16 (the old submenu offered 8 choices), and deadzone and click time limit no longer
  need command-line options: all three are sliders in the settings window.
- **Open TrackPoint driver settings…** (renamed; with the driver-scrolling reminder) now opens the
  driver's TrackPoint settings app, where the Middle Button Action setting is, instead of the
  classic Mouse Properties dialog. Mouse Properties is still used when no such app is installed.
- Clearer "driver is still scrolling" warning: an amber badge on the tray icon, a tooltip, and a
  **TrackPoint driver is still scrolling: fix it…** menu line, which all clear as soon as the
  driver setting is changed. With ELAN drivers the saved Middle Button Action setting is read
  and watched, so the warning shows at startup, before the first scroll.
- The warning notification is now a Windows notification instead of a brief tray balloon: it
  stays in the notification centre until you clear it there (closing only the popup keeps it),
  opens the TrackPoint settings when clicked (also from the notification centre), and is
  withdrawn as soon as the driver setting is fixed or NubMouse exits. Once cleared, it
  isn't shown again until the problem has gone away and come back.
- Autoscroll cancelling reacts to cursor changes as they happen (WinEvent hook) instead of
  polling every 10 ms, so an app's pan cursor is cancelled within a frame instead of showing for
  up to ~30 ms. It also ignores cursor changes once the pointer has moved away from the click.
- New `clicktimeout=MS` option (default 600): a middle press held longer than that without
  scrolling no longer sends a middle click.
- Lighter mouse hook: ordinary pointer movement is passed on without reading the event, and
  scrolling no longer allocates per event. Scroll steps that queue up are sent as one.
- New `debug` option: writes a diagnostic log of middle presses, scroll steps, hook timing and
  autoscroll decisions to `%TEMP%\NubMouse-debug.log`, with **Open debug log** in the tray
  menu.

## v0.1.0 — 2026-11-24

First release.

- Middle button pressed and released alone sends a normal middle click.
- Middle button held while moving the TrackPoint scrolls vertically and horizontally, with the
  pointer kept in place. Smooth (high-resolution) wheel output by default, or whole notches.
- Cancels apps' click-to-autoscroll mode after a lone middle click (optional).
- Detects when the TrackPoint driver is still doing its own middle-button scrolling, never adds a
  stray middle click after it, and points you to the driver settings.
- Tray icon (red = on, grey = paused): click to pause/resume, menu for speed, direction, notched
  scrolling, autoscroll cancelling, Start with Windows, hidden mode, and updates. Settings are
  saved per user.
- Self-installer: per user (no admin) or for all users, Start menu shortcut, Apps & features
  entry, uninstall. Running a newer exe updates the installed copy in place.
- Optional "Run as administrator" mode (scrolling in elevated windows), started through a
  per-user scheduled task so it needs no UAC prompt at logon.
- Optional daily update check and optional automatic install of updates (both off by default);
  updates are verified against the release's `SHA256SUMS.txt`.
- Remote control of the running copy: `--toggle`, `--enable`, `--disable`, `--show`, `--exit`.
- Re-installs its mouse hook after unlock and resume from sleep.
