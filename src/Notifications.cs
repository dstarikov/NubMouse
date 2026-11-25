// Windows toast notifications for the "TrackPoint driver is still scrolling" warning.
//
// Unlike a tray balloon (NotifyIcon.ShowBalloonTip), which Windows shows briefly and then drops,
// a toast waits in the notification centre until the user clears it, and the app can withdraw
// it, so it's removed as soon as the problem is fixed or NubMouse exits.
//
// Toasts need an app user model ID. Unpackaged apps can register one per user under
// HKCU\Software\Classes\AppUserModelId\<id> (display name and icon), without a Start menu
// shortcut. Its buttons are links to a per-user "nubmouse:" protocol that starts
// NubMouse.exe, which passes the command to the running copy (see Program): Windows launches
// links itself, so they also work from the notification centre after the popup has gone, which
// in-process click handling doesn't for an unpackaged app.
using System;
using System.Drawing.Imaging;
using System.IO;
using System.Security;
using System.Threading;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace NubMouse
{
    static class Notifications
    {
        const string Aumid = "NubMouse";
        const string AumidKey = @"Software\Classes\AppUserModelId\" + Aumid;
        const string Group = "NubMouse", DriverTag = "driver";
        public const string Protocol = "nubmouse";
        const string ProtocolKey = @"Software\Classes\" + Protocol;

        // The current driver warning, if any. Windows doesn't reliably report a dismissal to an
        // unpackaged app, so a timer checks whether it's still listed (see Watch).
        static readonly object gate = new object();
        static ToastNotification driverToast;    // also keeps its events connected
        static bool driverInCentre;              // the popup has gone (timed out, or shown silently)
        static DateTime driverShownAt;           // popups last ~5-7 s; after 10 s assume it's in the centre
        static bool driverCanFix;
        static Action driverDismissed;
        static Timer watchTimer;

        static string IconPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NubMouse", "notification.png"); }
        }

        /// Shows the driver warning, with a "Set it now" button when <paramref name="canFix"/>
        /// (Command.FixDriver) and "Open TrackPoint settings" (Command.DriverSettings), which is
        /// also what clicking the body does. <paramref name="onDismissed"/> is called on a
        /// background thread when the user dismisses it. Returns false if toasts aren't
        /// available, so the caller can fall back to a tray balloon.
        public static bool ShowDriverWarning(bool canFix, Action onDismissed)
        {
            lock (gate)
            {
                driverCanFix = canFix;
                driverDismissed = onDismissed;
                if (!Show(false)) return false;
                if (watchTimer == null) watchTimer = new Timer(delegate { Watch(); }, null, 1000, 1000);
                return true;
            }
        }

        /// Runs every second while a warning is out. If it has disappeared without us removing
        /// it, the user dismissed it: dismissing the popup puts it back silently, so it stays in
        /// the notification centre; dismissing it from there is the real "dismissed".
        static void Watch()
        {
            Action dismissed = null;
            lock (gate)
            {
                if (driverToast == null || IsListed()) return;
                if (!driverInCentre && DateTime.UtcNow - driverShownAt < TimeSpan.FromSeconds(10))
                {
                    DebugLog.Write("driver notification popup dismissed: keeping it in the notification centre");
                    Show(true);
                    return;
                }
                DebugLog.Write("driver notification dismissed from the notification centre");
                driverToast = null;
                dismissed = driverDismissed;
            }
            if (dismissed != null) dismissed();
        }

        static bool IsListed()
        {
            try
            {
                var list = Installer.IsPackaged
                    ? ToastNotificationManager.History.GetHistory()
                    : ToastNotificationManager.History.GetHistory(Aumid);
                foreach (var t in list)
                    if (t.Tag == DriverTag && t.Group == Group) return true;
                return false;
            }
            catch (Exception) { return true; }   // can't tell: assume it's still there
        }

        /// <paramref name="silent"/>: straight into the notification centre, without a popup.
        static bool Show(bool silent)
        {
            bool canFix = driverCanFix;
            try
            {
                Register();
                const string fix = Protocol + ":fix-driver", open = Protocol + ":driver-settings";
                var xml = new XmlDocument();
                xml.LoadXml(
                    "<toast activationType='protocol' launch='" + open + "'>" +
                    "<visual><binding template='ToastGeneric'>" +
                    "<text>Your TrackPoint driver is still scrolling</text>" +
                    "<text>" + (canFix
                        ? "Its Middle Button Action needs to be Middle Click so NubMouse can take over. Set it now, or change it in the TrackPoint settings."
                        : "Set its Middle Button Action to Middle Click so NubMouse can take over.") + "</text>" +
                    "</binding></visual>" +
                    "<actions>" +
                    (canFix ? "<action content='Set it now' activationType='protocol' arguments='" + fix + "'/>" : "") +
                    "<action content='Open TrackPoint settings' activationType='protocol' arguments='" + open + "'/>" +
                    "<action content='Dismiss' arguments='dismiss' activationType='system'/>" +
                    "</actions>" +
                    "</toast>");

                var toast = new ToastNotification(xml) { Tag = DriverTag, Group = Group, SuppressPopup = silent };
                toast.Dismissed += (s, e) =>
                {
                    DebugLog.Write("driver notification event: {0}", e.Reason);
                    if (e.Reason == ToastDismissalReason.TimedOut) lock (gate) if (driverToast == s) driverInCentre = true;
                };
                if (Installer.IsPackaged)
                {
                    try { ToastNotificationManager.History.Remove(DriverTag, Group); } catch (Exception) { }
                    ToastNotificationManager.CreateToastNotifier().Show(toast);
                }
                else
                {
                    try { ToastNotificationManager.History.Remove(DriverTag, Group, Aumid); } catch (Exception) { }
                    ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
                }
                driverToast = toast;
                driverInCentre = silent;
                driverShownAt = DateTime.UtcNow;
                DebugLog.Write("driver notification {0}{1}", silent ? "put in the notification centre" : "shown", canFix ? " with Set it now" : "");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Write("toast notifications unavailable: {0}", ex.Message);
                return false;
            }
        }

        /// Withdraws the driver warning, from the screen and the notification centre.
        public static void RemoveDriverWarning()
        {
            lock (gate)
            {
                if (watchTimer != null) { watchTimer.Dispose(); watchTimer = null; }
                if (driverToast == null) return;
                driverToast = null;
                try
                {
                    if (Installer.IsPackaged) ToastNotificationManager.History.Remove(DriverTag, Group);
                    else ToastNotificationManager.History.Remove(DriverTag, Group, Aumid);
                }
                catch (Exception) { }
            }
        }

        /// Registers the app's notification name and icon, and the nubmouse: protocol the
        /// notification's buttons use, for this user. Re-done for every notification, so the
        /// protocol follows the exe if it moves (e.g. a portable copy run from somewhere else).
        static void Register()
        {
            // MSIX packages declare their AUMID and protocol in AppxManifest.xml natively.
            if (Installer.IsPackaged) return;
            using (var k = Registry.CurrentUser.CreateSubKey(ProtocolKey))
            {
                k.SetValue("", "URL:" + Installer.AppName);
                k.SetValue("URL Protocol", "");
                using (var c = k.CreateSubKey(@"shell\open\command"))
                    c.SetValue("", "\"" + Installer.CurrentExe + "\" \"%1\"");
            }
            string icon = IconPath;
            if (!File.Exists(icon))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(icon));
                using (var bmp = Ui.MakeBitmap(64, true, false)) bmp.Save(icon, ImageFormat.Png);
            }
            using (var k = Registry.CurrentUser.CreateSubKey(AumidKey))
            {
                k.SetValue("DisplayName", Installer.AppName);
                k.SetValue("IconUri", icon);
            }
        }

        /// Removes the registration and icon (on uninstall).
        public static void Unregister()
        {
            try { ToastNotificationManager.History.Clear(Aumid); } catch (Exception) { }
            try { Registry.CurrentUser.DeleteSubKeyTree(AumidKey, false); } catch (Exception) { }
            try { Registry.CurrentUser.DeleteSubKeyTree(ProtocolKey, false); } catch (Exception) { }
            try { File.Delete(IconPath); Directory.Delete(Path.GetDirectoryName(IconPath)); } catch (Exception) { }
        }
    }
}
