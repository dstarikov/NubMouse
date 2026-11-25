// Reads, watches and changes the ELAN TrackPoint driver's "Middle Button Action" setting.
//
// ELAN's TrackPoint settings app (the one Settings → Bluetooth & devices → Mouse → TrackPoint
// settings opens) saves that choice as HKCU\Software\Elantech\SmartPad\n3A2PST_Middle_Mode:
// 1 = Middle Click, 0 = Scrolling. The app sends it to the driver itself, so writing the value
// alone does nothing. But ELAN's per-user helper, EPDCtrl.exe, re-reads its saved settings and
// sends them to the driver when its named event Global\EPDCombo_UpdateRegistry is signalled (it
// also does on resume and when it starts). That event is open to the logged-on user, so writing
// the value and signalling it switches the driver at once, without elevation. Other drivers
// (Lenovo, Synaptics, older ELAN) have no such value, and the scroll engine's detection of the
// driver's wheel events is used instead.
using System;
using System.Threading;
using Microsoft.Win32;
using N = NubMouse.Native;

namespace NubMouse
{
    sealed class ElanMiddleButton : IDisposable
    {
        const string KeyPath = @"Software\Elantech\SmartPad";
        const string ValueName = "n3A2PST_Middle_Mode";

        /// Raised on a background thread when the key changes (check <see cref="Scrolls"/>).
        public event Action Changed;

        readonly RegistryKey key;
        readonly AutoResetEvent signal;
        readonly RegisteredWaitHandle wait;

        public ElanMiddleButton()
        {
            try { key = Registry.CurrentUser.OpenSubKey(KeyPath); } catch (Exception) { }
            if (key == null) return;   // not an ELAN driver: nothing to watch
            signal = new AutoResetEvent(false);
            if (!Arm()) { signal.Dispose(); signal = null; return; }
            wait = ThreadPool.RegisterWaitForSingleObject(signal, delegate
            {
                Arm();   // re-arm before reading, so a change made meanwhile isn't missed
                var h = Changed;
                if (h != null) h();
            }, null, Timeout.Infinite, false);
        }

        bool Arm()
        {
            try
            {
                // Thread-agnostic: the notification stays registered after the pool thread that
                // asked for it goes away.
                return N.RegNotifyChangeKeyValue(key.Handle, false,
                           N.REG_NOTIFY_CHANGE_LAST_SET | N.REG_NOTIFY_THREAD_AGNOSTIC,
                           signal.SafeWaitHandle, true) == 0;
            }
            catch (Exception) { return false; }
        }

        /// true = the driver scrolls with the middle button, false = it sends a middle click,
        /// null = unknown (not an ELAN driver, or the value is missing).
        public static bool? Scrolls
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                    {
                        object v = k == null ? null : k.GetValue(ValueName);
                        if (!(v is int)) return null;
                        return (int)v == 0;
                    }
                }
                catch (Exception) { return null; }
            }
        }

        const string UpdateEvent = @"Global\EPDCombo_UpdateRegistry";

        /// Can <see cref="TrySet"/> work here: an ELAN driver whose EPDCtrl helper is running?
        public static bool CanSet
        {
            get
            {
                if (Scrolls == null) return false;
                IntPtr ev = N.OpenEvent(N.EVENT_MODIFY_STATE, false, UpdateEvent);
                if (ev == IntPtr.Zero) return false;
                N.CloseHandle(ev);
                return true;
            }
        }

        /// Switches the driver's Middle Button Action (true = Scrolling, false = Middle Click):
        /// saves it where ELAN's app does and has EPDCtrl apply it. Returns false if that isn't
        /// possible (not ELAN, or EPDCtrl isn't running); nothing is changed then.
        /// Switch only while the TrackPoint is idle (see <see cref="TrackPointIdle"/>): switching
        /// during a middle press can leave the driver scrolling until the next press.
        public static bool TrySet(bool scrolls)
        {
            IntPtr ev = N.OpenEvent(N.EVENT_MODIFY_STATE, false, UpdateEvent);
            if (ev == IntPtr.Zero)
            {
                DebugLog.Write("ELAN: can't switch the middle button, EPDCtrl isn't running (error {0})", System.Runtime.InteropServices.Marshal.GetLastWin32Error());
                return false;
            }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (k == null || !(k.GetValue(ValueName) is int)) return false;
                    k.SetValue(ValueName, scrolls ? 0 : 1, RegistryValueKind.DWord);
                }
                bool ok = N.SetEvent(ev);
                DebugLog.Write("ELAN: middle button switched to {0}{1}", scrolls ? "Scrolling" : "Middle Click", ok ? "" : " (signal failed)");
                return ok;
            }
            catch (Exception ex)
            {
                DebugLog.Write("ELAN: can't switch the middle button: {0}", ex.Message);
                return false;
            }
            finally { N.CloseHandle(ev); }
        }

        /// Safe to switch: the middle button is up and the driver hasn't scrolled for
        /// <paramref name="ms"/> milliseconds. Pointer movement and typing don't matter.
        /// <paramref name="engine"/> knows about presses it swallowed (Windows doesn't see those)
        /// and the last real wheel event; in Scrolling mode the driver sends wheel events and no
        /// button events at all.
        public static bool TrackPointIdle(ScrollEngine engine, int ms)
        {
            if ((N.GetAsyncKeyState(N.VK_MBUTTON) & 0x8000) != 0 || engine.MiddleHeld) return false;
            return unchecked(Environment.TickCount - engine.LastWheelTick) >= ms;
        }

        public void Dispose()
        {
            if (wait != null) wait.Unregister(null);
            if (key != null) key.Close();   // also ends the pending notification
            if (signal != null) signal.Dispose();
        }
    }
}
