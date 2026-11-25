# Changelog

## v0.1.0

First public release.

### Middle button

- **Tap** the middle button for a normal middle click (open links in background tabs, close
  tabs). **Hold it and push the TrackPoint** to scroll vertically and horizontally while the
  pointer stays put. Works the same in every app, because NubMouse sends ordinary wheel input.
- Smooth (high-resolution) wheel output by default, or whole notches.
- **Axis lock** (on by default): each push scrolls straight up/down or left/right, like the
  TrackPoint driver, and switches direction when you clearly push the other way. **Stickiness**
  (1.2×–4.0×, or Fixed) sets how readily it switches; turn axis lock off for diagonal scrolling.
- **Native middle-drag for 3D and CAD apps:** hold a shortcut (Alt, Ctrl, Shift, or any key or
  combination you record) while pressing the middle button to pass a real middle drag through,
  for orbiting and panning in Blender, CAD and design apps.
- **Per-device profiles:** choose Full NubMouse, 3D drag only, or Ignore for each pointing
  device, so an external mouse can keep its own middle button. Includes support for keyboards
  whose middle button scrolls in firmware, such as the TEX Shura, and a device setup wizard.
- Cancels apps' click-to-autoscroll mode after a lone middle click (optional, event-driven).
- A click time limit (`clicktimeout=`, default 600 ms), so a press held without scrolling
  doesn't send a stray click, and a deadzone, so a slight nudge still counts as a click.
- Optional scroll-direction indicator on the pointer, in four styles and sizes from 16 to 64 px.

### TrackPoint driver

- **ELAN drivers are set up automatically:** while NubMouse is on, the driver's Middle Button
  Action is switched to Middle Click, and back to Scrolling when NubMouse is paused or exits
  (both optional). Uninstalling restores Scrolling if NubMouse changed it.
- For other drivers, NubMouse detects when the driver is still doing its own middle-button
  scrolling, shows an amber badge and a Windows notification, and opens the driver's settings.
  `--fix-driver` and `--driver-settings` do the same from the command line.

### App

- **Settings window with a test pad:** every setting applies as you change it, next to a pad
  that shows exactly what NubMouse sends.
- Tray icon (red = on, grey = paused): click to pause or resume. Settings are saved per user.
- Self-installer: per user (no admin) or for all users, with a Start menu shortcut, an Apps &
  features entry and a clean uninstall (`--uninstall`). Running a newer exe updates the
  installed copy in place.
- Optional "Run as administrator" mode, so scrolling also works in elevated windows. It needs an
  all-users install, so the program it starts can't be changed without administrator rights.
- Optional daily update check and optional automatic updates, both off by default. Downloads
  are verified against the release's `SHA256SUMS.txt`.
- Remote control of the running copy: `--toggle`, `--enable`, `--disable`, `--show`,
  `--settings`, `--exit`.
- Optional `debug` log of middle presses, scroll steps and hook timing (never keys) in
  `%TEMP%\NubMouse-debug.log`.
- Re-installs its mouse hook after unlock and resume from sleep. The hook runs on its own
  high-priority thread with an allocation-free hot path.
