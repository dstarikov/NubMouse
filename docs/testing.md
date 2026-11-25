# Testing on real hardware

`tests\smoke.ps1` covers starting, pausing, installing, updating and uninstalling, but not the
middle button itself: the mouse hook ignores synthetic (injected) input, so clicks and scrolling
can only be tested with a real TrackPoint. Use the `debug` option for that.

## Setup

1. Make sure the TrackPoint driver's own middle-button scrolling is off (tray menu →
   **Open TrackPoint driver settings…** → *Middle Button Action* = *Middle Click*).
2. Exit any running NubMouse (tray menu → **Exit**). Turn off *Run as administrator* first if
   it's on, since options aren't passed through its scheduled task.
3. Build and start the build with logging:

   ```powershell
   .\build.ps1
   ```

   ```powershell
   .\dist\NubMouse.exe --portable debug
   ```

4. Tray menu → **Open debug log** at any time. The file is `%TEMP%\NubMouse-debug.log`;
   it's rewritten on every start and flushed as it goes, so it can be left open in an editor
   that reloads.

Each line has the time in milliseconds since start and the thread that wrote it (`hook` or
`output`). Every middle press gets a number (`press #12`), so its lines can be followed.

## Checklist

| Test | What to do | What the log should show |
| --- | --- | --- |
| Click | Middle-click a link in a browser | `middle up after <600 ms, travel 0…3` → `click`, then `sent middle click`. The link opens in a new tab. |
| Scroll | Hold middle, push the TrackPoint up/down/left/right | `now scrolling`, `move (dx,dy) -> wheel v=… h=…` lines, then `-> scroll`. The pointer doesn't move. |
| Slow scroll | Hold middle and push very gently | Small `move` offsets still add up to wheel steps. |
| Axis lock | Hold middle and push slightly off-vertical; then, without letting go, push clearly sideways | `locked to vertical scrolling`, wheel lines with `h=0`; then `switched to horizontal scrolling` and lines with `v=0`. With **Lock to one direction** off, no lock lines and both `v` and `h` move. |
| Long press | Hold middle ~1 s without pushing, release | `-> no click (held longer than clicktimeout)`. Nothing happens in the app. |
| Autoscroll | Middle-click an empty part of a web page in Chrome or Edge | `autoscroll watch started`, `sent middle click`, `cursor -> 0x…, an app cursor, pointer still: cancelling autoscroll`, `sent second click`. Compare the times of the first and second click: that's how long the pan cursor could be visible. |
| No false cancel | Middle-click a link, then immediately move the pointer over other things | No `cancelling autoscroll` line. |
| Screen edges | Move the pointer to the very top of the screen (for example a maximized browser's tab strip), hold middle and push **up**; repeat at the other edges | Wheel lines keep coming. Lines saying `move with no offset (pointer at a screen edge?)` mean scrolling toward that edge doesn't work: the hook only sees the clamped pointer position (see [Raw Input](#known-open-questions)). |
| Driver still scrolling (non-ELAN) | Turn the driver's middle-button scrolling back on, hold middle and push | `real wheel event while held (the driver is scrolling)`, `-> no click (driver scrolled)`. The tray icon gets an amber badge, the menu shows the "still scrolling" line, and `driver notification shown` appears. |
| Notification | With the driver warning showing, close the popup; then clear it from the notification centre | `popup dismissed: keeping it in the notification centre` and `put in the notification centre`; it's still listed in the notification centre. Clearing it there logs `dismissed from the notification centre` and it doesn't come back. Clicking it opens the TrackPoint settings. |
| Driver setting (ELAN) | In the TrackPoint app, switch Middle Button Action between Scrolling and Middle Click | `ELAN middle button setting changed to …` right away; the badge and menu line appear / disappear with it. |
| Automatic switch (ELAN) | Set Scrolling in the TrackPoint app, exit NubMouse and start it again; pause, resume, exit | No warning at start; `ELAN: middle button switched to Middle Click` ~0.1 s after start and resume (even while moving the pointer), `… to Scrolling` on pause and exit. Middle click works while on; pushing with middle held scrolls through the driver while paused. With **Back to Scrolling when paused or closed** unticked, pause and exit leave it on Middle Click. |
| Switch while busy (ELAN) | Keep holding middle and pushing while toggling with `NubMouse.exe --toggle` from a shortcut key | The `switched to` line only comes after you let go of the middle button (and, while the driver scrolls, ~0.3 s after its last scroll); the driver never gets stuck scrolling with the button up. |
| Set it now (ELAN) | While on, set Scrolling in the TrackPoint app; click **Set it now** in the notification (also from the notification centre) | `driver notification shown with Set it now`, then `switched to Middle Click`; the notification goes away. **Open TrackPoint settings** opens the app. |
| Driver fixed (any driver) | After the test above, turn the driver's scrolling off again, hold middle and push | `scrolled with no wheel events from the driver (its scrolling is off now)`; the badge goes away. |
| Hook speed | Any of the above | `slowest hook callback` in each `middle up` line should be well under 1000 µs. Anything over 1 ms is also logged as `SLOW hook callback`. |
| Lock / sleep | Lock and unlock, or sleep and wake | `unlock / resume: re-installing the mouse hook`, `mouse hook removed`, `mouse hook installed`. Clicking and scrolling still work. |

## Known open questions

- **Screen edges.** The hook works out TrackPoint movement from the pointer position Windows
  reports, which is probably clamped to the screen. If the edge test above shows `no offset`
  lines, the fix is to read raw movement with the Raw Input API instead.
- **Office.** Office's autoscroll cursor may appear later than a browser's; if the autoscroll
  test in Word or Excel shows `autoscroll watch ended, no app cursor seen`, the 500 ms watch is
  too short for it.
