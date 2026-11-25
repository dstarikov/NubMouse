// The running application: tray icon and menu, settings, and the scroll engine's lifetime.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NubMouse
{
    sealed class TrayApp : ApplicationContext, ISettingsHost
    {
        static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(1);

        readonly Settings settings;
        readonly ScrollEngine engine;
        readonly ScrollIndicator indicator;
        readonly NotifyIcon tray;
        readonly Instance.Receiver receiver;
        readonly Control invoker;                 // marshals background events onto the UI thread
        readonly System.Windows.Forms.Timer updateTimer;
        readonly Icon iconOn = Ui.MakeIcon(true), iconOff = Ui.MakeIcon(false), iconWarn = Ui.MakeIcon(true, true);
        readonly ElanMiddleButton elan;
        Action balloonClick;
        bool updateBusy;

        // Is the TrackPoint driver still doing its own middle-button scrolling? Known from ELAN's
        // saved setting, or seen by the scroll engine (wheel events while the button is held).
        bool? elanScrolls;
        bool driverSeenScrolling;
        bool driverNotified;                    // this run's notification for the current problem is out
        bool DriverScrolls { get { return elanScrolls == true || driverSeenScrolling; } }

        // ELAN: a pending switch of the driver's Middle Button Action, made once the TrackPoint is
        // idle (see SyncElan).
        readonly System.Windows.Forms.Timer elanTimer;
        bool elanTarget;                        // switch to: true = Scrolling, false = Middle Click
        int elanWaited;                         // ms waited so far for the TrackPoint to be idle
        bool elanOpenOnFail;                    // the user asked for it: open the settings if it fails
        /// The driver is scrolling while NubMouse is on, and no pending switch will fix it.
        bool DriverProblem { get { return settings.Enabled && DriverScrolls && !(elanTimer.Enabled && !elanTarget); } }

        ToolStripSeparator driverSwitchSep;
        ToolStripMenuItem driverItem, switchDriverItem, scrollPausedItem, enabledItem, naturalItem, axisLockItem, indicatorItem,
            devicesMenu, startupItem, adminItem, hideItem, autoUpdateItem, autoInstallItem;
        bool adminStartup;                      // cached: does the elevated task have a logon trigger?
        SettingsWindow settingsWindow;          // the open settings window, if any

        public TrayApp(Settings settings)
        {
            this.settings = settings;
            if (!settings.DevicesInitialized && string.IsNullOrEmpty(settings.DeviceModes) && string.IsNullOrEmpty(settings.DeviceFilter))
            {
                string rec = RawInputDevices.RecommendedDeviceModes();
                if (!string.IsNullOrEmpty(rec)) settings.DeviceModes = rec;
                settings.DevicesInitialized = true;
                settings.Save();
            }
            invoker = new Control();
            invoker.CreateControl();
            PointerHider.RecoverAfterCrash();
            indicator = new ScrollIndicator();

            engine = new ScrollEngine(settings);
            engine.DriverScrollDetected += () => invoker.BeginInvoke((Action)(() => { driverSeenScrolling = true; UpdateDriverStatus(); }));
            engine.DriverScrollStopped += () => invoker.BeginInvoke((Action)(() => { driverSeenScrolling = false; UpdateDriverStatus(); }));
            engine.ScrollVisualChanged += (mode, x, y) => invoker.BeginInvoke((Action)(() => indicator.UpdateState(engine.VisualMode, x, y, settings.IndicatorOffset, settings.IndicatorStyle, settings.IndicatorSize)));

            elanScrolls = ElanMiddleButton.Scrolls;
            elan = new ElanMiddleButton();
            elan.Changed += () => invoker.BeginInvoke((Action)OnElanChanged);
            elanTimer = new System.Windows.Forms.Timer { Interval = 100 };
            elanTimer.Tick += delegate { OnElanTimer(); };
            DebugLog.Write("ELAN middle button setting: {0}", elanScrolls == null ? "not found" : elanScrolls.Value ? "Scrolling" : "Middle Click");

            tray = new NotifyIcon { ContextMenuStrip = BuildMenu() };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) SetEnabled(!settings.Enabled); };
            tray.BalloonTipClicked += delegate { var a = balloonClick; balloonClick = null; if (a != null) a(); };
            tray.BalloonTipClosed += delegate { balloonClick = null; };

            receiver = new Instance.Receiver(OnCommand);
            if (settings.RunAsAdmin) adminStartup = AdminMode.StartsAtLogon();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            if (settings.Enabled) SetEnabled(true);
            else SyncElan();
            Refresh();
            tray.Visible = !settings.HideTrayIcon;

            if (!Installer.IsPackaged)
            {
                updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(1).TotalMilliseconds };
                updateTimer.Tick += delegate { AutoCheckForUpdates(); };
                updateTimer.Start();
                invoker.BeginInvoke((Action)AutoCheckForUpdates);
            }
            invoker.BeginInvoke((Action)UpdateDriverStatus);   // once the icon is showing
        }

        // ---- Menu -------------------------------------------------------------------------

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip { ShowItemToolTips = true };
            m.Items.Add(new ToolStripMenuItem(Installer.AppName + " " + Installer.CurrentVersionText) { Enabled = false });
            driverItem = new ToolStripMenuItem("TrackPoint driver is still scrolling: fix it…", SystemIcons.Warning.ToBitmap(),
                                               delegate { FixDriver(); });
            m.Items.Add(driverItem);
            m.Items.Add(new ToolStripSeparator());

            enabledItem = Item(m.Items, "&Enabled (click the icon to toggle)", delegate { SetEnabled(!settings.Enabled); });
            enabledItem.Font = new System.Drawing.Font(enabledItem.Font, System.Drawing.FontStyle.Bold);
            Item(m.Items, "&Settings and test pad…", delegate { ShowSettings(); }).ToolTipText =
                "Every scrolling, middle-click, indicator and native-drag setting, with a pad to try them on.";
            m.Items.Add(new ToolStripSeparator());

            // Quick toggles; everything else is in the settings window.
            naturalItem = Item(m.Items, "&Natural (reversed) direction", delegate { Change(s => s.Natural = !s.Natural); });
            axisLockItem = Item(m.Items, "&Lock to one direction (vertical or horizontal)", delegate { Change(s => s.AxisLock = !s.AxisLock); });
            axisLockItem.ToolTipText = "Like the TrackPoint driver: a push scrolls straight up/down or left/right, not diagonally.";
            indicatorItem = Item(m.Items, "Show scroll-direction &indicator", delegate { Change(s => s.ScrollIndicator = !s.ScrollIndicator); });
            indicatorItem.ToolTipText = "While scrolling, the pointer turns into a small indicator showing the scroll direction.";
            m.Items.Add(new ToolStripSeparator());

            devicesMenu = Submenu(m, "&Devices");
            devicesMenu.DropDownOpening += delegate { PopulateDevicesMenu(); };
            devicesMenu.DropDownItems.Add(new ToolStripMenuItem("Loading…") { Enabled = false });

            var driver = Submenu(m, "&TrackPoint driver");
            switchDriverItem = Item(driver.DropDownItems, "Set it to &Middle Click while NubMouse is on",
                                    delegate { Change(s => s.SwitchDriver = !s.SwitchDriver); SyncElan(); });
            switchDriverItem.ToolTipText = "Switches the driver's Middle Button Action to Middle Click when NubMouse starts or is turned on, " +
                                           "so you don't have to change it in the TrackPoint settings.";
            scrollPausedItem = Item(driver.DropDownItems, "Back to &Scrolling when paused or closed",
                                    delegate { Change(s => s.ScrollWhenPaused = !s.ScrollWhenPaused); SyncElan(); });
            scrollPausedItem.ToolTipText = "While NubMouse is paused or closed, the driver's own middle-button scrolling works. " +
                                           "Turn this off to keep a plain middle click instead.";
            driverSwitchSep = new ToolStripSeparator();
            driver.DropDownItems.Add(driverSwitchSep);
            Item(driver.DropDownItems, "Open TrackPoint driver &settings…", delegate { OpenDriverSettings(); });
            m.Items.Add(new ToolStripSeparator());

            var startup = Submenu(m, "St&artup");
            if (Installer.IsPackaged)
            {
                startupItem = Item(startup.DropDownItems, "Start with &Windows (Windows Settings…)", delegate { Open("ms-settings:startupapps"); });
                adminItem = new ToolStripMenuItem { Visible = false };
            }
            else
            {
                startupItem = Item(startup.DropDownItems, "Start with &Windows", delegate { ToggleStartup(); });
                adminItem = Item(startup.DropDownItems, "Run as a&dministrator (scroll in admin windows)", delegate { ToggleAdmin(); });
            }
            hideItem = Item(startup.DropDownItems, "&Hide tray icon", delegate { HideIcon(); });

            if (!Installer.IsPackaged)
            {
                var updates = Submenu(m, "&Updates");
                autoUpdateItem = Item(updates.DropDownItems, "Check for updates &automatically", delegate { Change(s => s.CheckForUpdates = !s.CheckForUpdates); });
                autoInstallItem = Item(updates.DropDownItems, "&Install updates automatically", delegate { Change(s => s.AutoInstallUpdates = !s.AutoInstallUpdates); });
                Item(updates.DropDownItems, "Check for updates &now", delegate { CheckForUpdatesNow(); });
            }
            else
            {
                autoUpdateItem = new ToolStripMenuItem { Visible = false };
                autoInstallItem = new ToolStripMenuItem { Visible = false };
            }

            Item(m.Items, "&Help", delegate { Open(Installer.RepoUrl + "#readme"); });
            if (DebugLog.On) Item(m.Items, "Open debug &log", delegate { Open(DebugLog.FilePath); });
            m.Items.Add(new ToolStripSeparator());
            Item(m.Items, "E&xit", delegate { ExitThread(); });

            m.Opening += delegate { Refresh(); };
            return m;
        }

        static ToolStripMenuItem Item(ToolStripItemCollection items, string text, EventHandler onClick)
        {
            var i = new ToolStripMenuItem(text, null, onClick);
            items.Add(i);
            return i;
        }

        static ToolStripMenuItem Submenu(ContextMenuStrip m, string text)
        {
            var i = new ToolStripMenuItem(text);
            i.DropDown.ShowItemToolTips = true;
            m.Items.Add(i);
            return i;
        }

        void PopulateDevicesMenu()
        {
            devicesMenu.DropDownItems.Clear();
            var allowed = settings.AllowedDeviceKeys();
            var allItem = Item(devicesMenu.DropDownItems, "&All pointing devices (default)", delegate { Change(s => s.DeviceFilter = ""); });
            allItem.Checked = allowed.Count == 0;
            devicesMenu.DropDownItems.Add(new ToolStripSeparator());

            var devices = RawInputDevices.ListMouseDevices();
            if (devices.Count == 0)
            {
                devicesMenu.DropDownItems.Add(new ToolStripMenuItem("(No pointing devices detected)") { Enabled = false });
                return;
            }
            foreach (var dev in devices)
            {
                string key = dev.Key;
                var item = Item(devicesMenu.DropDownItems, dev.Name.Replace("&", "&&"), delegate
                {
                    Change(s =>
                    {
                        var set = s.AllowedDeviceKeys();
                        if (set.Count == 0) set.Add(key);
                        else if (!set.Remove(key)) set.Add(key);
                        var list = new System.Collections.Generic.List<string>(set);
                        list.Sort(StringComparer.OrdinalIgnoreCase);
                        s.DeviceFilter = string.Join(",", list.ToArray());
                    });
                });
                item.Checked = allowed.Contains(key);
                item.ToolTipText = "Tick one or more devices to restrict NubMouse to only those devices (leaving external mice untouched).";
            }
        }

        void Refresh()
        {
            enabledItem.Checked = settings.Enabled;
            naturalItem.Checked = settings.Natural;
            axisLockItem.Checked = settings.AxisLock;
            indicatorItem.Checked = settings.ScrollIndicator;
            startupItem.Checked = StartsWithWindows;
            adminItem.Checked = settings.RunAsAdmin;
            hideItem.Checked = settings.HideTrayIcon;
            autoUpdateItem.Checked = settings.CheckForUpdates;
            autoInstallItem.Checked = settings.AutoInstallUpdates;
            autoInstallItem.ToolTipText = AutoInstallBlocked;
            autoInstallItem.Enabled = autoInstallItem.ToolTipText == null;

            bool warn = DriverProblem;
            bool canFix = elanScrolls == true && ElanMiddleButton.CanSet;
            driverItem.Visible = warn;
            driverItem.Text = canFix ? "TrackPoint driver is still scrolling: set it to Middle Click" : "TrackPoint driver is still scrolling: fix it…";
            driverItem.ToolTipText = canFix ? "Switches the driver's Middle Button Action to Middle Click so NubMouse can scroll."
                : "In the TrackPoint settings, set Middle Button Action to Middle Click so NubMouse can scroll.";
            // The switch needs ELAN's driver; other drivers just get the settings link.
            switchDriverItem.Visible = scrollPausedItem.Visible = driverSwitchSep.Visible = elanScrolls != null;
            switchDriverItem.Checked = settings.SwitchDriver;
            scrollPausedItem.Checked = settings.ScrollWhenPaused;
            scrollPausedItem.Enabled = settings.SwitchDriver;

            tray.Icon = warn ? iconWarn : settings.Enabled ? iconOn : iconOff;
            string text = warn ? Installer.AppName + " - driver is still scrolling (right-click to fix)"
                : Installer.AppName + (settings.Enabled ? " - on" : " - paused (normal middle button)") +
                  (Installer.IsElevated ? " (admin)" : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;   // NotifyIcon's limit
            if (settingsWindow != null) settingsWindow.LoadValues();
        }

        void Change(Action<Settings> edit)
        {
            edit(settings);
            settings.Save();
            engine.Configure(settings);
            Refresh();
        }

        // ---- Actions ----------------------------------------------------------------------

        void ShowSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow(settings, this);
                settingsWindow.FormClosed += delegate { settingsWindow = null; };
                settingsWindow.Show();
            }
            if (settingsWindow.WindowState == FormWindowState.Minimized) settingsWindow.WindowState = FormWindowState.Normal;
            settingsWindow.Activate();
        }

        // ---- What the settings window can do (see ISettingsHost) ---------------------------

        void ISettingsHost.Change(Action<Settings> edit) { Change(edit); }
        void ISettingsHost.SetEnabled(bool on) { SetEnabled(on); Refresh(); }
        bool ISettingsHost.MiddleHeld { get { return engine.MiddleHeld; } }
        string ISettingsHost.LastActiveDeviceKey { get { return engine.LastActiveDeviceKey; } }
        bool ISettingsHost.StartsWithWindows { get { return StartsWithWindows; } }
        void ISettingsHost.ToggleStartup() { ToggleStartup(); }
        void ISettingsHost.ToggleAdmin() { ToggleAdmin(); }
        void ISettingsHost.SetTrayIconShown(bool show) { if (show) ShowIcon(); else HideIcon(); Refresh(); }
        void ISettingsHost.CheckForUpdatesNow() { CheckForUpdatesNow(); }
        void ISettingsHost.OpenDriverSettings() { OpenDriverSettings(); }
        void ISettingsHost.FixDriver() { FixDriver(); }
        void ISettingsHost.SyncDriver() { SyncElan(); }
        bool? ISettingsHost.ElanScrolls { get { return elanScrolls; } }
        bool ISettingsHost.DriverProblem { get { return DriverProblem; } }
        string ISettingsHost.AutoInstallBlocked { get { return AutoInstallBlocked; } }
        void ISettingsHost.Open(string target) { Open(target); }

        /// Why automatic installs can't be turned on right now, or null if they can.
        string AutoInstallBlocked
        {
            get
            {
                var scope = Installer.ScopeOfCurrentExe();
                if (scope == InstallScope.AllUsers) return "All-users installs need administrator rights to update, so they always ask first.";
                if (scope == InstallScope.None) return "Only installed copies can update themselves.";
                if (!settings.CheckForUpdates) return "Turn on automatic update checks first.";
                return null;
            }
        }

        void SetEnabled(bool on)
        {
            if (on)
            {
                int err = engine.Start();
                if (err != 0)
                {
                    Ui.Error("Could not install the mouse hook (Windows error " + err + ").");
                    on = false;
                }
            }
            else engine.Stop();

            if (settings.Enabled != on) { settings.Enabled = on; settings.Save(); }
            SyncElan();
        }

        // ---- ELAN driver switching ----------------------------------------------------------

        /// ELAN only: while NubMouse is on, the driver's Middle Button Action is set to
        /// Middle Click (SwitchDriver). When it's paused (or exits, see Dispose), a Scrolling
        /// setting it was switched from is put back (ScrollWhenPaused). The switch waits until
        /// the TrackPoint is idle: switching during a press can leave the driver scrolling until
        /// the next one.
        void SyncElan()
        {
            elanTimer.Stop();
            if (elanScrolls != null && settings.SwitchDriver)
            {
                if (settings.Enabled && elanScrolls == true) StartElanSwitch(false, false);
                else if (!settings.Enabled && settings.ScrollWhenPaused && settings.RestoreDriverScroll && elanScrolls == false)
                    StartElanSwitch(true, false);
            }
            UpdateDriverStatus();
        }

        void StartElanSwitch(bool scrolls, bool userAsked)
        {
            elanTarget = scrolls;
            elanWaited = 0;
            elanOpenOnFail = userAsked;
            elanTimer.Stop();
            elanTimer.Start();
        }

        void OnElanTimer()
        {
            elanWaited += elanTimer.Interval;
            // Middle button up and no wheel events from the driver for 300 ms; after 5 s of
            // constant scrolling, a shorter gap does.
            if (!ElanMiddleButton.TrackPointIdle(engine, elanWaited < 5000 ? 300 : 100)) return;
            elanTimer.Stop();
            bool scrolls = elanTarget;
            if (ElanMiddleButton.TrySet(scrolls))
            {
                elanScrolls = scrolls;   // the registry watcher reports the same, which OnElanChanged ignores
                if (!scrolls) driverSeenScrolling = false;
                if (settings.RestoreDriverScroll != !scrolls) Change(s => s.RestoreDriverScroll = !scrolls);
            }
            else if (elanOpenOnFail) OpenDriverSettings();
            UpdateDriverStatus();
        }

        /// The warning's "set it" (menu line, notification button): switch to Middle Click if the
        /// driver allows it, otherwise open its settings.
        void FixDriver()
        {
            if (elanScrolls == true && ElanMiddleButton.CanSet) StartElanSwitch(false, true);
            else OpenDriverSettings();
            UpdateDriverStatus();
        }

        bool StartsWithWindows { get { return settings.RunAsAdmin ? adminStartup : Installer.StartsWithWindows; } }

        void ToggleStartup()
        {
            try
            {
                if (settings.RunAsAdmin)
                {
                    // Start with Windows is the task's logon trigger in administrator mode.
                    if (AdminMode.CreateTask(Installer.CurrentExe, !adminStartup)) adminStartup = !adminStartup;
                }
                else Installer.SetStartWithWindows(!Installer.StartsWithWindows, Installer.CurrentExe);
            }
            catch (Exception ex) { Ui.Error("Could not change the startup setting:\n\n" + ex.Message); }
            Refresh();
        }

        void ToggleAdmin()
        {
            if (!settings.RunAsAdmin)
            {
                if (!AdminMode.IsAllowed(Installer.CurrentExe))
                {
                    // The elevated task only starts the copy in Program Files (see AdminMode).
                    if (!Ui.Confirm("Run as administrator needs NubMouse installed for all users.\n\n" +
                                    "It then runs from Program Files, where only administrators can change it, so no " +
                                    "other program can replace it with something that would start with administrator rights.\n\n" +
                                    "Reinstall NubMouse for all users now? Windows asks for permission, and NubMouse " +
                                    "restarts. Then turn on Run as administrator again.")) return;
                    Installer.LaunchUnelevated(Installer.CurrentExe, "--install --all-users" + (StartsWithWindows ? "" : " --no-startup"));
                    return;
                }
                if (!Ui.Confirm("Run NubMouse as administrator?\n\n" +
                                "This lets it scroll in windows that run as administrator (admin terminals, Task Manager, " +
                                "some installers). Windows asks for permission once to set it up; after that it starts " +
                                "elevated without asking, including with Windows if that is on.")) return;
                bool startup = Installer.StartsWithWindows;
                if (!AdminMode.CreateTask(Installer.CurrentExe, startup)) return;   // UAC declined or failed
                adminStartup = startup;
                Installer.SetStartWithWindows(false, null);                         // the task replaces the Run entry
                Change(s => s.RunAsAdmin = true);
                if (!Installer.IsElevated)
                {
                    Program.AfterExit = () => { if (!AdminMode.RunTask()) Installer.LaunchUnelevated(Installer.CurrentExe, null); };
                    ExitThread();
                }
            }
            else
            {
                bool startup = adminStartup;
                if (!AdminMode.DeleteTask()) return;
                Change(s => s.RunAsAdmin = false);
                if (startup) Installer.SetStartWithWindows(true, Installer.CurrentExe);
                if (Installer.IsElevated)
                {
                    Program.AfterExit = () => Installer.LaunchUnelevated(Installer.CurrentExe, null);
                    ExitThread();
                }
            }
        }

        void HideIcon()
        {
            if (!Ui.Confirm("Hide the tray icon?\n\nNubMouse keeps running. To show the icon again, " +
                            "start NubMouse again (from the Start menu or its exe).")) return;
            Change(s => s.HideTrayIcon = true);
            tray.Visible = false;
        }

        void ShowIcon()
        {
            if (settings.HideTrayIcon) Change(s => s.HideTrayIcon = false);
            tray.Visible = true;
            Balloon("NubMouse is running", settings.Enabled ? "Right-click this icon for options." : "Paused. Click this icon to turn it back on.", null);
        }

        void OnCommand(Command c)
        {
            switch (c)
            {
                case Command.Show: ShowIcon(); break;
                case Command.Toggle: SetEnabled(!settings.Enabled); break;
                case Command.Enable: SetEnabled(true); break;
                case Command.Disable: SetEnabled(false); break;
                case Command.Exit: ExitThread(); break;
                case Command.FixDriver: FixDriver(); break;
                case Command.DriverSettings: OpenDriverSettings(); break;
                case Command.Settings: ShowSettings(); break;
            }
        }

        void OnElanChanged()
        {
            bool? now = ElanMiddleButton.Scrolls;
            if (now == elanScrolls) return;
            DebugLog.Write("ELAN middle button setting changed to {0}", now == null ? "not found" : now.Value ? "Scrolling" : "Middle Click");
            elanScrolls = now;
            if (now == false) driverSeenScrolling = false;   // just switched to Middle Click
            // Changed in the TrackPoint settings (our own switches are already known): that's
            // the user's choice for now, so a pending switch is dropped. Switched to Middle Click
            // while on, it was Scrolling before, which comes back on pause like our own switch.
            elanTimer.Stop();
            bool restore = now == false && settings.Enabled;
            if (settings.RestoreDriverScroll != restore) Change(s => s.RestoreDriverScroll = restore);
            UpdateDriverStatus();
        }

        /// Shows the driver-still-scrolling state: badge, tooltip and menu line (see Refresh),
        /// plus a notification that waits in the notification centre until the user dismisses
        /// it, the problem is fixed, or NubMouse pauses or exits. With ELAN's driver the
        /// switch usually fixes it first, so the notification is the fallback: the switch failed,
        /// is turned off, or the user set Scrolling again while NubMouse is on. A dismissed
        /// notification isn't shown again until the problem has cleared and come back
        /// (DriverWarningShown).
        void UpdateDriverStatus()
        {
            if (!DriverProblem)
            {
                Notifications.RemoveDriverWarning();
                driverNotified = false;
                if (!DriverScrolls && settings.DriverWarningShown) Change(s => s.DriverWarningShown = false);   // fixed: nudge again if it comes back
            }
            else if (!driverNotified && !settings.DriverWarningShown)
            {
                driverNotified = true;
                bool canFix = elanScrolls == true && ElanMiddleButton.CanSet;
                bool toast = Notifications.ShowDriverWarning(canFix,
                    () => invoker.BeginInvoke((Action)(() => Change(s => s.DriverWarningShown = true))));
                if (!toast && tray.Visible)
                {
                    // No toasts (very old Windows 10, or blocked): the old, brief tray balloon.
                    Change(s => s.DriverWarningShown = true);
                    Balloon("Your TrackPoint driver is still scrolling",
                            canFix ? "Click here to set its Middle Button Action to Middle Click so NubMouse can take over."
                                   : "Set its Middle Button Action to Middle Click so NubMouse can take over. Click here to open the TrackPoint settings.",
                            FixDriver);
                }
            }
            Refresh();
        }

        public static void OpenDriverSettings()
        {
            // Current drivers ship a Store app for their settings (e.g. ELAN's "TrackPoint" app,
            // the one Settings → Bluetooth & devices → Mouse → TrackPoint settings opens).
            // Older drivers add a TrackPoint tab to the classic Mouse Properties dialog instead.
            string aumid = FindTrackPointApp();
            if (aumid != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + aumid) { UseShellExecute = false });
                    return;
                }
                catch (Exception) { }
            }
            try { Process.Start(new ProcessStartInfo("control.exe", "main.cpl") { UseShellExecute = true }); }
            catch (Exception) { Open("ms-settings:mousetouchpad"); }
        }

        // Finds an installed Store app with "TrackPoint" in its package name and returns its app
        // user model ID ("PackageFamilyName!AppId"), or null.
        static string FindTrackPointApp()
        {
            const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(Packages))
                {
                    if (root == null) return null;
                    foreach (string fullName in root.GetSubKeyNames())
                    {
                        // Full name: Name_Version_Architecture_ResourceId_PublisherId. Resource
                        // packages (non-empty ResourceId) hold only language/scale assets.
                        string[] parts = fullName.Split('_');
                        if (parts.Length != 5 || parts[3].Length != 0) continue;
                        if (parts[0].IndexOf("TrackPoint", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (var pkg = root.OpenSubKey(fullName))
                        {
                            if (pkg == null) continue;
                            string[] apps = pkg.GetSubKeyNames();
                            if (apps.Length > 0) return parts[0] + "_" + parts[4] + "!" + apps[0];
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        /// Opens a link or file with its default app. When elevated, it goes through Explorer so
        /// the app starts unelevated: file and link associations live in HKCU, which any
        /// program running as the user can change, so opening them elevated would let such a
        /// program start itself with administrator rights.
        static void Open(string target)
        {
            try
            {
                if (Installer.IsElevated)
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }

        void Balloon(string title, string text, Action onClick)
        {
            if (!tray.Visible) return;
            balloonClick = onClick;
            tray.ShowBalloonTip(10000, title, text, ToolTipIcon.Info);
        }

        // Windows can drop low-level hooks across lock/sleep, and a button-up may never arrive.
        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect ||
                e.Reason == SessionSwitchReason.RemoteConnect)
                invoker.BeginInvoke((Action)RestartEngine);
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) invoker.BeginInvoke((Action)RestartEngine);
        }

        void RestartEngine()
        {
            DebugLog.Write("unlock / resume: re-installing the mouse hook");
            if (settings.Enabled && engine.Restart() != 0) SetEnabled(false);
        }

        // ---- Updates ----------------------------------------------------------------------

        void AutoCheckForUpdates()
        {
            if (!settings.CheckForUpdates || updateBusy) return;
            if (DateTime.UtcNow - new DateTime(settings.LastUpdateCheck, DateTimeKind.Utc) < UpdateInterval) return;
            Change(s => s.LastUpdateCheck = DateTime.UtcNow.Ticks);
            RunUpdateCheck(false);
        }

        void CheckForUpdatesNow()
        {
            if (!updateBusy) RunUpdateCheck(true);
        }

        void RunUpdateCheck(bool interactive)
        {
            updateBusy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo info = null; Exception error = null;
                try { info = Updater.CheckLatest(); } catch (Exception ex) { error = ex; }
                invoker.BeginInvoke((Action)(() =>
                {
                    updateBusy = false;
                    if (error != null) { if (interactive) Ui.Error("Could not check for updates:\n\n" + error.Message); }
                    else if (info == null) { if (interactive) Ui.Info("You have the latest version (" + Installer.CurrentVersionText + ")."); }
                    else if (interactive) OfferUpdate(info);
                    else if (CanInstallSilently()) InstallUpdate(info, Installer.ScopeOfCurrentExe(), false);
                    else Balloon("NubMouse " + info.Version + " is available", "Click here to update.", () => OfferUpdate(info));
                }));
            });
        }

        /// Automatic installs only happen when asked for, and only for per-user installs:
        /// an all-users install would need a UAC prompt, which shouldn't appear out of nowhere.
        bool CanInstallSilently()
        {
            var scope = Installer.ScopeOfCurrentExe();
            return settings.AutoInstallUpdates && (scope == InstallScope.User || (scope == InstallScope.AllUsers && Installer.IsElevated));
        }

        void OfferUpdate(UpdateInfo info)
        {
            var scope = Installer.ScopeOfCurrentExe();
            if (scope == InstallScope.None)
            {
                // Portable copy: don't install anything behind the user's back.
                if (Ui.Confirm("NubMouse " + info.Version + " is available. Open the download page?")) Open(info.PageUrl);
                return;
            }
            if (!Ui.Confirm("Update NubMouse " + Installer.CurrentVersion + " to " + info.Version + "?\n\n" +
                            "It will be downloaded from GitHub, checked against the release checksums, and restarted.")) return;
            InstallUpdate(info, scope, true);
        }

        void InstallUpdate(UpdateInfo info, InstallScope scope, bool interactive)
        {
            if (updateBusy) return;
            updateBusy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string exe = null; Exception error = null;
                try { exe = Updater.Download(info); } catch (Exception ex) { error = ex; }
                invoker.BeginInvoke((Action)(() =>
                {
                    updateBusy = false;
                    if (error == null)
                    {
                        try { Updater.Apply(exe, scope); return; }   // the new version asks us to exit
                        catch (Exception ex) { error = ex; }
                    }
                    if (interactive) Ui.Error("The update failed:\n\n" + error.Message);
                    else Balloon("Automatic update to " + info.Version + " failed", error.Message + " Click here to try again.", () => OfferUpdate(info));
                }));
            });
        }

        // ---- Shutdown ---------------------------------------------------------------------

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (settingsWindow != null) settingsWindow.Close();
                if (updateTimer != null) updateTimer.Dispose();
                elanTimer.Dispose();
                // Exiting counts as pausing: put a Scrolling setting we switched from back (see
                // SyncElan). Exit is usually chosen from the menu, so the TrackPoint is idle.
                if (settings.SwitchDriver && settings.ScrollWhenPaused && settings.RestoreDriverScroll && elanScrolls == false)
                {
                    for (int i = 0; i < 20 && !ElanMiddleButton.TrackPointIdle(engine, 200); i++) Thread.Sleep(50);
                    if (ElanMiddleButton.TrySet(true)) { settings.RestoreDriverScroll = false; settings.Save(); }
                }
                elan.Dispose();
                Notifications.RemoveDriverWarning();   // don't leave a stale warning behind
                engine.Dispose();
                indicator.Dispose();
                DebugLog.Write("exiting");
                DebugLog.Stop();
                tray.Visible = false;
                tray.Dispose();
                receiver.Dispose();
                invoker.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
