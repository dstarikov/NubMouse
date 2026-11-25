# How NubMouse works

The sources are in [`src/`](../src):

| File | What it does |
| --- | --- |
| [`ScrollEngine.cs`](../src/ScrollEngine.cs) | The mouse hook, click-or-scroll decision, wheel output, autoscroll cancelling |
| [`TrayApp.cs`](../src/TrayApp.cs) | Tray icon and menu, pause/resume, notifications, update checks |
| [`Settings.cs`](../src/Settings.cs) | Settings in the registry and command-line options |
| [`Instance.cs`](../src/Instance.cs) | Single instance and `--toggle`/`--exit`/… remote control |
| [`Installer.cs`](../src/Installer.cs) | Install, update in place, uninstall, Start with Windows |
| [`Updater.cs`](../src/Updater.cs) | GitHub release check, download and checksum verification |
| [`AdminMode.cs`](../src/AdminMode.cs) | "Run as administrator" through a scheduled task |
| [`Ui.cs`](../src/Ui.cs) | Install dialog, message boxes, tray icons |
| [`Notifications.cs`](../src/Notifications.cs) | The "driver is still scrolling" Windows notification |
| [`DriverStatus.cs`](../src/DriverStatus.cs) | Reads, watches and switches the ELAN driver's Middle Button Action setting |
| [`Native.cs`](../src/Native.cs) | Win32 declarations |

## Overview

```
TrackPoint driver ──► Windows input ──► low-level mouse hook (hook thread)
                                             │
             middle down ───────────────────►│ swallowed, remember position
             movement while held ───────────►│ swallowed (pointer stays put),
                                             │   converted to wheel / h-wheel ──► output thread ──► SendInput
             middle up, never scrolled ─────►│ swallowed, replayed as a click ──► output thread ──► SendInput
             driver's own wheel while held ─►│ passed on; no click on release; tray warning
             everything else ───────────────►│ passed on unchanged
```

## The hook and its threads

`SetWindowsHookEx(WH_MOUSE_LL, …)` installs a low-level mouse hook, which sees every mouse event
for the desktop before any window does and can either pass it on (`CallNextHookEx`) or swallow
it (return non-zero).

Windows gives low-level hooks a short time limit and silently removes hooks that are too slow.
So the hook runs on its **own high-priority thread** with nothing but a message loop, separate
from the tray UI (which can be busy showing a menu or dialog). The callback only updates state
and queues work; every `SendInput` call happens on a separate **output thread**, in order.

The callback is kept cheap. Pointer movement while the middle button is up, by far the most
common event, is passed on without reading the event data at all; for the rest only the needed
fields are read straight from memory. Work is queued as small structs rather than closures, so a
scroll creates no garbage, and if scroll steps pile up faster than they are sent, the output
thread merges them into one `SendInput`.

Events created with `SendInput` come back through the same hook with the `LLMHF_INJECTED` flag.
The hook ignores all injected input, so it never reacts to its own output (or to remote-control
and macro tools).

Pausing removes the hook entirely, so a paused NubMouse has no effect on input at all.
After unlocking the workstation or resuming from sleep the hook is re-installed, which also
clears any press whose button-up was lost.

## Click or scroll?

Windows has no way to take back a button press that has already been delivered, so the hook
**swallows the middle-button-down** and decides later:

1. **Middle down** — remember the pointer position (`anchor`), reset counters, swallow.
2. **Mouse move while held** — because every move is swallowed, the real pointer never leaves
   the anchor, so each event's offset from it is just the latest TrackPoint movement. Offsets add
   up in `travel`; once `travel` exceeds `deadzone`, the press becomes a scroll.
3. **Middle up** — if the press never became a scroll and was released within `clicktimeout`
   (600 ms by default), a full middle click (down + up) is sent. A longer press without scrolling
   is treated as a scroll you changed your mind about, and sends nothing.
   Either way the real button-up is swallowed. A button-up whose button-down the hook never saw
   (pressed before the hook was installed) is passed on.

## Turning movement into scrolling

While scrolling, each movement is multiplied by `speed` and added to vertical and horizontal
accumulators. By default it behaves like a mouse wheel: pushing up gives a positive wheel delta
(scroll up), pushing right a positive horizontal delta (scroll right). `natural` flips both.

**Axis lock** (on by default, off with `diagonal`) makes a press scroll along one axis at a time,
like the TrackPoint driver's own scrolling, so a slightly crooked push doesn't drift the page
sideways. Each axis keeps a decaying sum of recent movement (`recent = recent × 0.8 + |d|` per
move, so roughly the last 5 moves, ~50 ms). When scrolling starts, the axis with more recent
movement wins; the other axis's movement is then dropped. If the other axis's recent movement
gets above 8 px and more than *R* times the locked axis's, the lock switches over (and both
accumulators are cleared). In steady movement a push stays locked until it is more than
atan(*R*) off the locked axis. *R* is set by `stickiness`:

| `stickiness` | *R* | Switches when the push is more than… |
| --- | --- | --- |
| `loose` | 1.5 | ~56° off the locked axis |
| `normal` (default) | 2 | ~63° off |
| `firm` | 3 | ~72° off |
| `fixed` | ∞ | never: the axis stays for the whole press, like the driver |

At `normal`, a deliberate turn, for example from scrolling down a sheet to scrolling across it,
switches within a few moves, without letting go of the button.

Whole units are taken from the accumulators and sent as `MOUSEEVENTF_WHEEL` /
`MOUSEEVENTF_HWHEEL`; the remainder stays for next time, so slow movements still add up. The unit
is 1 by default (smooth scrolling in apps that support high-resolution wheel input) or 120, one
physical wheel notch, with `notched`.

## Detecting the driver's own scrolling

NubMouse knows the driver is still scrolling in two ways:

- **ELAN's saved setting.** ELAN's TrackPoint app saves *Middle Button Action* as
  `HKCU\Software\Elantech\SmartPad\n3A2PST_Middle_Mode` (1 = Middle Click, 0 = Scrolling).
  [`DriverStatus.cs`](../src/DriverStatus.cs) reads it at startup and watches the key with
  `RegNotifyChangeKeyValue`, so the state is known before the first press and updates the moment
  the user changes it. In Scrolling mode this driver sends no middle button events at all, only
  wheel events, so the setting is the only way to tell.
- **The driver's own wheel events**, for other drivers. If the hook sees **real wheel events while
  the middle button is held**, they are passed on (that's the driver scrolling) and the press is
  marked so no middle click is added on release. The state clears again when a later press is
  scrolled by NubMouse with no wheel events from the driver.

## Switching the ELAN driver's setting

ELAN's TrackPoint app sends *Middle Button Action* to the driver itself, so writing the registry
value alone does nothing. But ELAN's per-user helper `EPDCtrl.exe` (in System32, started by the
*ELAN PST Service*) re-reads its saved settings from the registry and sends them to the driver
when its named event `Global\EPDCombo_UpdateRegistry` is signalled; it also does so on resume
and when it starts. The event is open to the logged-on user, so
[`DriverStatus.cs`](../src/DriverStatus.cs) switches the driver by writing the value and calling
`SetEvent`, with no elevation. If the event can't be opened (EPDCtrl isn't running), nothing is
written and the warning below is the fallback. Lenovo and Synaptics drivers store the setting
differently and have no such hook, so they only get the warning.

Tested on the hardware by counting real (non-injected) wheel events from the driver with a
low-level hook while pressing and pushing: registry alone, and EPDCtrl's other events tried,
had no effect; `EPDCombo_UpdateRegistry`, `EPDMessage_Resume` and restarting EPDCtrl all
switched it, and the first is the lightest.

[`TrayApp.cs`](../src/TrayApp.cs) (`SyncElan`) switches to Middle Click when NubMouse
starts or is turned on, and back to Scrolling when it's paused or exits, if it was the one that
switched it from Scrolling (`RestoreDriverScroll`, saved so it survives a crash). Both
directions have their own setting (`SwitchDriver`, `ScrollWhenPaused`). A change the user makes
in the TrackPoint app while NubMouse runs is left alone: it shows the warning instead of
being undone.

**The switch waits until the TrackPoint is idle**: the middle button up (`GetAsyncKeyState`, plus
the scroll engine's own state for presses it swallowed) and no real wheel event for 300 ms
(recorded by the hook; in Scrolling mode the driver sends only wheel events, no button events).
Switching during a press can leave the driver stuck scrolling, even with the button released,
until the next press. Plain pointer movement and typing don't delay it, so it usually happens
within 100 ms (the timer's interval); after 5 s of constant scrolling a 100 ms gap is enough. On
exit it waits up to a second.

## The driver warning

While the driver is scrolling and NubMouse is on, and no pending switch is about to fix it,
the tray icon has an amber **!** badge and its tooltip says so, the menu shows **TrackPoint
driver is still scrolling**, and a notification is shown. With ELAN drivers that normally only
happens when the switch failed or is turned off, or the user set Scrolling again in the
TrackPoint app. The menu line and the notification's **Set it now** switch the setting (ELAN);
otherwise they open the driver's settings: the installed Store app with "TrackPoint" in its
package name (found in the per-user AppModel package repository in the registry and launched
via `shell:AppsFolder\<family>!<app>`), or *Mouse Properties* for older drivers that have no
such app.

### The notification

[`Notifications.cs`](../src/Notifications.cs) shows it as a Windows toast, not a tray balloon:
Windows shows a balloon briefly and then drops it, while a toast waits in the notification
centre and can be withdrawn by the app.

- **App identity.** Toasts need an app user model ID. Unpackaged apps can register one per user
  under `HKCU\Software\Classes\AppUserModelId\NubMouse` (display name, and an icon drawn
  by `Ui.MakeBitmap` and saved to `%LOCALAPPDATA%\NubMouse\notification.png`), with no Start
  menu shortcut. Uninstall removes both.
- **Buttons** are links to a per-user `nubmouse:` protocol
  (`HKCU\Software\Classes\nubmouse`, pointing at the current exe, re-registered with each
  notification): **Set it now** is `nubmouse:fix-driver`, **Open TrackPoint settings**
  (and a click on the body) `nubmouse:driver-settings`. Windows launches links itself,
  which also works from the notification centre after the popup has gone, where in-process click
  handling doesn't reach an unpackaged app. The launched exe passes the command to the running
  copy, like `--toggle` (or opens the settings itself if none is running). Uninstall removes the
  protocol.
- **Staying until cleared.** Windows doesn't reliably tell an unpackaged app when the user
  closes the popup, so while a warning is out a timer checks every second whether it's still
  listed (`ToastNotificationManager.History.GetHistory`). If it disappears while the popup is
  still up (the `TimedOut` event hasn't arrived, and less than 10 s have passed), the user
  closed the popup: it's put back with `SuppressPopup`, straight into the notification centre.
  If it disappears after that, the user cleared it from the notification centre, which counts
  as dismissed: `DriverWarningShown` is set and it isn't shown again until the problem has
  cleared and come back.
- **Withdrawn** (`History.Remove`) when the problem clears and when NubMouse pauses or exits, so a
  stale warning is never left behind. After a restart it's shown again if the problem is still
  there and it wasn't dismissed.
- If toasts aren't available, the old tray balloon is used instead.

The build references the Windows Runtime metadata in `%WINDIR%\System32\WinMetadata` and the
.NET Framework's `System.Runtime.WindowsRuntime` bridge, both part of Windows 10/11. The C# 5
compiler can't subscribe to Windows Runtime events without
`System.Runtime.InteropServices.WindowsRuntime.dll` also being referenced.

## Autoscroll cancelling

A lone middle click in many apps (Chromium-based browsers and Electron apps, Office, …) starts
"autoscroll", where the page scrolls toward wherever the mouse goes until you click again. These
apps show their own pan cursor in that mode.

Just before sending a middle click, the output thread notes the current cursor and asks the hook
thread to watch. The hook thread installs a WinEvent hook for `EVENT_OBJECT_NAMECHANGE`, which
Windows raises for `OBJID_CURSOR` whenever the cursor changes shape, and removes it again after
500 ms. On each change it checks the cursor (`GetCursorInfo`): if it is no longer the one from
before the click, isn't a standard Windows cursor, and the pointer has stayed within a few pixels
of where it was clicked, it assumes autoscroll started and queues one more click to end it. That
happens within a millisecond or two of the app changing its cursor, instead of on a polling tick.
A new middle press or a pointer that has moved away ends the watch. This is a heuristic: an app that shows its own cursor for something else right after a
middle click could get a spurious second click; turn the option off if you hit that.

## Single instance and remote control

A named mutex (`Local\NubMouse`) allows one copy per logon session. The running copy owns a
hidden message-only window; a second launch finds it with `FindWindowEx(HWND_MESSAGE, …)` and
posts it a command (`--toggle`, `--enable`, `--disable`, `--exit`; a plain second launch sends
"show", which brings back a hidden tray icon).

## Installing and updating

The exe is its own installer. Installing copies it to
`%LOCALAPPDATA%\Programs\NubMouse` (per user) or `%ProgramFiles%\NubMouse` (all
users), creates a Start menu shortcut and an *Apps & features* entry whose uninstall command is
`NubMouse.exe --uninstall`.

- **All users.** Only the file copy and machine-wide registration run elevated, in a child
  process (`--install-files --all-users`) started with a UAC prompt. Start with Windows, settings
  and starting the app stay in the user's own, unelevated process.
- **Updating in place.** Windows won't overwrite a running exe but will rename it, so an exe in
  use (for example by another user's session) is moved to `NubMouse.exe.old` and deleted
  on a later start. The files are copied *before* the running copy is asked to exit, so a failed
  copy or a declined UAC prompt leaves the old version running.
- **Update check.** Off by default. When turned on, once a day (and always on demand) the app reads
  `api.github.com/repos/…/releases/latest`. If the tag is newer than the exe's file version it
  offers the update, downloads `NubMouse.exe` and `SHA256SUMS.txt`, verifies the hash, and
  runs the new exe with `--install --quiet`, which replaces the installed copy and restarts it.
  Portable copies only get a link to the release page. With *Install updates automatically*
  on, a found update is installed without the confirmation, but only where that needs no UAC
  prompt.

## Running as administrator

User Interface Privilege Isolation stops a normal program from sending input to elevated windows.
The Run key can't start a program elevated without a UAC prompt at every logon, so
*Run as administrator* registers a scheduled task (`schtasks /Create /XML`) for the current user
with `RunLevel` *HighestAvailable*, normal priority (tasks default to below normal) and no time
limit. Registering, changing and deleting it needs elevation, so those steps run in a child
process started with a UAC prompt (`--admin-task create|delete`); starting it with
`schtasks /Run` doesn't.

The task only ever starts the all-users copy in Program Files. A per-user copy lives in
`%LOCALAPPDATA%`, which any program running as the user can write to, so a task pointing there
would let that program swap the exe and get administrator rights without a prompt. The elevated
`--admin-task create` step refuses any other path, and an unelevated start only hands over to
the task if it points at that copy. Turning the mode on from a per-user install offers to
reinstall for all users first, and reinstalling per user turns the mode off.

For the same reason, nothing the elevated side uses lives in `%TEMP%`. The task's XML is
written beside the exe in Program Files before it is registered, and an elevated copy downloads
updates to `Program Files\NubMouse\update`, so a normal program can't swap the file between the
checksum check and the elevated launch. The task is named after the user whose SID the elevated
step was given, so it is found again even when a standard user elevated with an administrator's
password.

The elevated copy also keeps away from anything else a normal program could tamper with. Links
and files it opens (Help, the debug log, a release page) go through Explorer and open
unelevated, because file and link associations live in `HKCU`. Its debug log is written beside
the exe instead of to `%TEMP%`, and it leaves `%TEMP%\NubMouse-update-*` cleanup to unelevated
starts. The settings it reads from `HKCU` are numbers, shortcuts and device lists, never paths
or commands. Removing the all-users copy deletes every user's task, since they all start it.

Every unelevated start checks the setting and hands over to the task, so the Start menu shortcut,
restarts after updates and logon all end up elevated. "Start with Windows" becomes the task's
logon trigger in this mode. The elevated copy allows the remote-control messages from
normal-privilege processes with `ChangeWindowMessageFilterEx`; Windows would otherwise drop them.

## Limitations

- **Elevated windows.** Scrolling there needs *Run as administrator* (see above).
- **Secure desktop.** Nothing runs on the UAC prompt or the lock screen.
- **All middle buttons.** A low-level hook can't tell which device a click came from, so an
  external mouse's middle button gets the same click-or-scroll treatment. Its wheel is unaffected.
