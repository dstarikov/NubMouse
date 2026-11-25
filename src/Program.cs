// NubMouse - ThinkPad-style middle button for Windows.
//   Middle button pressed and released alone       -> normal middle click
//   Middle button held while moving the TrackPoint -> scroll (vertical + horizontal), pointer stays put
//
// Command line (see README):
//   NubMouse.exe [speed=N] [hspeed=N] [deadzone=N] [clicktimeout=MS] [notched] [natural] [diagonal] [stickiness=LEVEL] [indicator] [indicatorstyle=STYLE] [indicatorsize=SIZE] [indicatoroffset=N] [dragkeys=SHORTCUTS] [devices=all|IDs] [allowautoscroll] [hidden] [debug] [--portable]
//   NubMouse.exe --toggle | --enable | --disable | --show | --exit | --fix-driver | --driver-settings | --settings
//   NubMouse.exe --install [--all-users] [--no-startup] [--check-updates | --auto-update] [--quiet]
//   NubMouse.exe --uninstall [--all-users] [--quiet]
//   NubMouse.exe --admin-task create --user SID --exe PATH [--at-logon] | --admin-task delete --user SID   (internal, elevated)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace NubMouse
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Internal: create/delete the elevated-start task (runs elevated, see AdminMode).
            if (args.Length > 0 && args[0].Equals("--admin-task", StringComparison.OrdinalIgnoreCase))
                return AdminTaskCommand(args);

            var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var options = new List<string>();
            foreach (var a in args)
            {
                if (a.StartsWith("--") && !a.Contains("=") && IsFlag(a.Substring(2))) flags.Add(a.Substring(2));
                // The notification's buttons are nubmouse: links (see Notifications).
                else if (a.StartsWith(Notifications.Protocol + ":", StringComparison.OrdinalIgnoreCase))
                {
                    string verb = a.Substring(Notifications.Protocol.Length + 1).Trim('/');
                    if (verb.Equals("fix-driver", StringComparison.OrdinalIgnoreCase) || verb.Equals("driver-settings", StringComparison.OrdinalIgnoreCase))
                        flags.Add(verb);
                    else return 0;   // unknown link: do nothing rather than start a second copy
                }
                else options.Add(a);
            }
            bool quiet = flags.Contains("quiet");
            var scopeArg = flags.Contains("all-users") ? InstallScope.AllUsers : InstallScope.User;

            try
            {
                // Internal steps run in an elevated child by the installer.
                if (flags.Contains("install-files")) { Installer.InstallFiles(scopeArg); return 0; }
                if (flags.Contains("uninstall-files")) { Installer.UninstallFiles(scopeArg); return 0; }

                if (flags.Contains("uninstall"))
                    return Installer.Uninstall(flags.Contains("all-users") ? InstallScope.AllUsers : Installer.ScopeOfCurrentExe(), quiet);

                if (flags.Contains("install"))
                {
                    // Explicit installs turn on "Start with Windows" unless told not to; updates
                    // (--quiet) keep whatever the user had.
                    bool startup = !quiet && !flags.Contains("no-startup");
                    if (flags.Contains("auto-update")) SaveUpdateChoices(true, true);
                    else if (flags.Contains("check-updates")) SaveUpdateChoices(true, false);
                    return Installer.Install(scopeArg, startup, true, quiet) ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                if (!quiet) Ui.Error(ex.Message);
                return 1;
            }

            foreach (var c in new[] { Command.Toggle, Command.Enable, Command.Disable, Command.Exit })
                if (flags.Contains(c.ToString()))
                    return Instance.Send(c) ? 0 : 1;
            if (flags.Contains("show") && Instance.Send(Command.Show)) return 0;
            if (flags.Contains("settings") && Instance.Send(Command.Settings)) return 0;
            if (flags.Contains("fix-driver")) return Instance.Send(Command.FixDriver) ? 0 : 1;
            if (flags.Contains("driver-settings"))
            {
                if (!Instance.Send(Command.DriverSettings)) TrayApp.OpenDriverSettings();
                return 0;
            }

            Installer.CleanUpOldFiles();

            // Started from outside an installation (e.g. Downloads): offer to install or update.
            if (!Installer.IsPackaged && Installer.ScopeOfCurrentExe() == InstallScope.None && !flags.Contains("portable") && options.Count == 0)
            {
                var existing = Installer.FindInstalled();
                using (var dlg = new InstallDialog(existing, Installer.InstalledVersion(existing)))
                {
                    dlg.ShowDialog();
                    if (dlg.Choice == InstallChoice.Cancel) return 0;
                    // Saved for "Just run it" too: these are scrolling preferences, not install ones.
                    var chosen = Settings.Load();
                    chosen.AxisLock = dlg.AxisLock;
                    chosen.ScrollIndicator = dlg.ScrollIndicator;
                    chosen.Save();
                    if (dlg.Choice == InstallChoice.Install)
                    {
                        SaveUpdateChoices(dlg.CheckForUpdates, dlg.AutoInstallUpdates);
                        return Installer.Install(dlg.Scope, dlg.StartWithWindows, true, false) ? 0 : 1;
                    }
                }
            }

            if (Instance.IsRunning)
            {
                // Already running: bring its tray icon back (it may be hidden).
                Instance.Send(Command.Show);
                return 0;
            }

            // "Run as administrator" is on: hand over to the elevated scheduled task.
            if (!Installer.IsPackaged && !Installer.IsElevated && !flags.Contains("portable") && Settings.Load().RunAsAdmin && AdminMode.RunTask())
                return 0;

            using (var mutex = Instance.TryClaim())
            {
                if (mutex == null)
                {
                    // Already running: bring its tray icon back (it may be hidden).
                    Instance.Send(Command.Show);
                    return 0;
                }

                var settings = Settings.Load();
                var errors = new List<string>();
                settings.ApplyOptions(options, errors);
                if (errors.Count > 0)
                    Ui.Error("Ignoring unknown or invalid options:\n\n" + string.Join("\n", errors) +
                             "\n\nValid options: speed=N, hspeed=N, deadzone=N, clicktimeout=MS, notched, natural, diagonal, stickiness=loose|normal|firm|fixed, indicator, indicatorstyle=dark|light|minimal|trackpoint, indicatorsize=tiny|small|medium|large|16-64, indicatoroffset=N, dragkeys=off|alt,ctrl+shift,space (keys joined with +, shortcuts with commas), devices=all|VID:PID, allowautoscroll, hidden, debug.");

                if (settings.Debug)
                {
                    DebugLog.Start();
                    DebugLog.Write("started from {0}{1}", Installer.CurrentExe, Installer.IsElevated ? " (elevated)" : "");
                }

                using (var app = new TrayApp(settings))
                    Application.Run(app);
            }

            // Restarts (e.g. switching to or from administrator mode) happen once the mutex is free.
            if (AfterExit != null) AfterExit();
            return 0;
        }

        /// Set by the tray app to run something after it has fully shut down.
        public static Action AfterExit;

        static int AdminTaskCommand(string[] args)
        {
            try
            {
                string user = null, exe = null; bool atLogon = false;
                for (int i = 2; i < args.Length; i++)
                {
                    if (args[i] == "--user" && i + 1 < args.Length) user = args[++i];
                    else if (args[i] == "--exe" && i + 1 < args.Length) exe = args[++i];
                    else if (args[i] == "--at-logon") atLogon = true;
                }

                if (args.Length > 1 && args[1].Equals("delete", StringComparison.OrdinalIgnoreCase))
                    return (user != null ? AdminMode.DeleteTaskElevated(user) : AdminMode.DeleteTask()) ? 0 : 1;

                if (user == null || exe == null) return 2;
                return AdminMode.CreateTaskElevated(exe, atLogon, user) ? 0 : 1;
            }
            catch (Exception) { return 1; }
        }

        static void SaveUpdateChoices(bool check, bool autoInstall)
        {
            var s = Settings.Load();
            s.CheckForUpdates = check;
            s.AutoInstallUpdates = check && autoInstall;
            s.Save();
        }

        static readonly string[] Flags = { "install", "install-files", "uninstall", "uninstall-files", "all-users",
            "quiet", "no-startup", "check-updates", "auto-update", "portable", "toggle", "enable", "disable", "show", "exit",
            "fix-driver", "driver-settings", "settings" };

        static bool IsFlag(string s) { return Flags.Contains(s, StringComparer.OrdinalIgnoreCase); }
    }
}
