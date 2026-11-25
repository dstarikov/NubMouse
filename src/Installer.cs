// Self-installer: copies the exe to a fixed place, adds a Start menu shortcut and an
// "Apps & features" entry, and handles updates by installing over an existing copy.
//
//   Per user (default): %LOCALAPPDATA%\Programs\NubMouse   no admin rights needed
//   All users:          %ProgramFiles%\NubMouse            needs elevation for the files
//
// Only the machine-wide file work runs elevated (as a child process with --install-files).
// Everything that belongs to the user - "Start with Windows", settings, launching the app -
// stays in the user's own, unelevated process.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;

namespace NubMouse
{
    enum InstallScope { None, User, AllUsers }

    static class Installer
    {
        public const string AppName = "NubMouse";
        public const string ExeName = "NubMouse.exe";
        public const string RepoUrl = "https://github.com/dstarikov/NubMouse";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NubMouse";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // ---- Where things are -------------------------------------------------------------

        static readonly bool isPackaged = DetectPackaged();
        /// True when running inside an MSIX / Microsoft Store package.
        public static bool IsPackaged { get { return isPackaged; } }

        static bool DetectPackaged()
        {
            try
            {
                int len = 0;
                int rc = Native.GetCurrentPackageFullName(ref len, null);
                return rc == 0 || rc == 122;   // ERROR_SUCCESS or ERROR_INSUFFICIENT_BUFFER
            }
            catch (Exception) { return false; }
        }

        public static string CurrentExe { get { return Assembly.GetExecutingAssembly().Location; } }

        public static string InstallDir(InstallScope scope)
        {
            return scope == InstallScope.AllUsers
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
        }

        public static string InstalledExe(InstallScope scope) { return Path.Combine(InstallDir(scope), ExeName); }

        /// Where an elevated copy downloads updates: inside the all-users install, which only
        /// administrators can write to (see Updater.UpdateDir).
        public static string ElevatedUpdateDir { get { return Path.Combine(InstallDir(InstallScope.AllUsers), "update"); } }

        static string ShortcutPath(InstallScope scope)
        {
            var folder = scope == InstallScope.AllUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs;
            return Path.Combine(Environment.GetFolderPath(folder), AppName + ".lnk");
        }

        static RegistryKey Root(InstallScope scope)
        {
            return scope == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;
        }

        /// The scope of an existing installation: per-user wins if both exist.
        public static InstallScope FindInstalled()
        {
            if (File.Exists(InstalledExe(InstallScope.User))) return InstallScope.User;
            if (File.Exists(InstalledExe(InstallScope.AllUsers))) return InstallScope.AllUsers;
            return InstallScope.None;
        }

        /// The scope this exe is installed as, or None when it runs from anywhere else.
        public static InstallScope ScopeOfCurrentExe()
        {
            foreach (var s in new[] { InstallScope.User, InstallScope.AllUsers })
                if (SamePath(CurrentExe, InstalledExe(s))) return s;
            return InstallScope.None;
        }

        public static Version InstalledVersion(InstallScope scope)
        {
            try
            {
                var fv = FileVersionInfo.GetVersionInfo(InstalledExe(scope));
                return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch (Exception) { return new Version(0, 0, 0); }
        }

        public static Version CurrentVersion
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, v.Build); }
        }

        public static string CurrentVersionText
        {
            get
            {
                var a = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    Assembly.GetExecutingAssembly(), typeof(AssemblyInformationalVersionAttribute));
                return a != null ? a.InformationalVersion : CurrentVersion.ToString();
            }
        }

        public static bool IsElevated
        {
            get { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        // ---- Start with Windows (always per user) -----------------------------------------

        public static bool StartsWithWindows
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(AppName) != null;
            }
        }

        /// Points the Run entry at <paramref name="exe"/> (or removes it).
        public static void SetStartWithWindows(bool on, string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(AppName, "\"" + exe + "\"");
                else k.DeleteValue(AppName, false);
            }
        }

        // ---- Install / update ---------------------------------------------------------------

        /// Installs (or updates) this exe for the given scope. Runs in the user's process; the
        /// machine-wide part is delegated to an elevated child. Returns false if cancelled/failed.
        public static bool Install(InstallScope scope, bool startWithWindows, bool launch, bool quiet)
        {
            bool admin = Settings.Load().RunAsAdmin;
            bool hadStartup = admin ? AdminMode.StartsAtLogon() : StartsWithWindows;
            if (admin && scope != InstallScope.AllUsers)
            {
                // Administrator mode needs the all-users copy (see AdminMode), so a per-user
                // install turns it off and uses the normal startup entry instead.
                var s = Settings.Load();
                s.RunAsAdmin = false;
                s.Save();
                admin = false;
            }

            // Copy first: a running copy can be replaced (it is renamed aside), and if the copy
            // fails or UAC is declined, the old version simply keeps running.
            if (!RunFilesStep(scope, "--install-files", quiet)) return false;

            // Switching scope: remove the other copy so only one is ever registered.
            var other = scope == InstallScope.User ? InstallScope.AllUsers : InstallScope.User;
            if (File.Exists(InstalledExe(other)))
                RunFilesStep(other, "--uninstall-files", quiet);

            string exe = InstalledExe(scope);
            if (admin)
            {
                // Elevated mode starts through a scheduled task; point it at the new location.
                if (!AdminMode.TaskRuns(exe)) AdminMode.CreateTask(exe, startWithWindows || hadStartup);
            }
            else if (startWithWindows || hadStartup) SetStartWithWindows(true, exe);

            Instance.StopRunning();
            if (launch) LaunchUnelevated(exe, null);
            return true;
        }

        /// Runs a file step for the scope: directly when allowed, else in an elevated child.
        static bool RunFilesStep(InstallScope scope, string verb, bool quiet)
        {
            try
            {
                if (scope == InstallScope.User || IsElevated)
                {
                    if (verb == "--install-files") InstallFiles(scope); else UninstallFiles(scope);
                    return true;
                }
                return RunElevated(verb + " --all-users");
            }
            catch (Exception ex)
            {
                if (!quiet) Ui.Error("Could not " + (verb == "--install-files" ? "install" : "remove") + " " + AppName + ":\n\n" + ex.Message);
                return false;
            }
        }

        /// Runs this exe with <paramref name="args"/> elevated (UAC prompt) and waits for it.
        /// Returns false if it failed or the prompt was declined.
        public static bool RunElevated(string args)
        {
            try
            {
                var psi = new ProcessStartInfo(CurrentExe, args) { UseShellExecute = true, Verb = "runas" };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
            }
            catch (System.ComponentModel.Win32Exception) { return false; }   // UAC prompt declined
        }

        /// Copies the exe into place and registers it. May run elevated.
        public static void InstallFiles(InstallScope scope)
        {
            string dir = InstallDir(scope), exe = InstalledExe(scope);
            Directory.CreateDirectory(dir);

            if (!SamePath(CurrentExe, exe))
            {
                // A copy may still be running in another user's session. Windows lets a running
                // exe be renamed but not overwritten, so move it aside; it's cleaned up later.
                string old = exe + ".old";
                TryDelete(old);
                if (File.Exists(exe))
                {
                    try { File.Delete(exe); }
                    catch (Exception) { File.Move(exe, old); }
                }
                File.Copy(CurrentExe, exe);
            }

            CreateShortcut(ShortcutPath(scope), exe);

            using (var k = Root(scope).CreateSubKey(UninstallKey))
            {
                string flag = scope == InstallScope.AllUsers ? " --all-users" : "";
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", CurrentVersion.ToString());
                k.SetValue("Publisher", "dstarikov");
                k.SetValue("DisplayIcon", exe);
                k.SetValue("InstallLocation", dir);
                k.SetValue("URLInfoAbout", RepoUrl);
                k.SetValue("HelpLink", RepoUrl);
                k.SetValue("UninstallString", "\"" + exe + "\" --uninstall" + flag);
                k.SetValue("QuietUninstallString", "\"" + exe + "\" --uninstall --quiet" + flag);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        /// Deletes leftovers of an earlier update (an exe that was in use at the time).
        public static void CleanUpOldFiles()
        {
            TryDelete(CurrentExe + ".old");
            DeleteTempUpdateFolders();
            // Only an elevated copy can remove the elevated update folder in Program Files.
            if (IsElevated && Directory.Exists(ElevatedUpdateDir) && !SamePath(Path.GetDirectoryName(CurrentExe), ElevatedUpdateDir))
                try { Directory.Delete(ElevatedUpdateDir, true); } catch (Exception) { }
        }

        /// Deletes %TEMP%\NubMouse-update-* folders, except the one this exe runs from. Skipped
        /// when elevated: any program running as the user can write to %TEMP% and could plant a
        /// link there to make an elevated recursive delete remove something else.
        static void DeleteTempUpdateFolders()
        {
            if (IsElevated) return;
            try
            {
                foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "NubMouse-update-*"))
                    if (!SamePath(Path.GetDirectoryName(CurrentExe), d))
                        try { Directory.Delete(d, true); } catch (Exception) { }
            }
            catch (Exception) { }
        }

        // ---- Uninstall ----------------------------------------------------------------------

        public static int Uninstall(InstallScope scope, bool quiet)
        {
            if (scope == InstallScope.None) scope = FindInstalled();
            if (scope == InstallScope.None)
            {
                if (!quiet) Ui.Info(AppName + " is not installed.");
                return 0;
            }
            if (!quiet && !Ui.Confirm("Uninstall " + AppName + "?\n\nThe middle button goes back to however your TrackPoint driver handles it."))
                return 1;

            Instance.StopRunning();   // which puts back the driver's Scrolling if it was switched from it
            if (Settings.Load().RestoreDriverScroll) ElanMiddleButton.TrySet(true);   // in case it wasn't running
            SetStartWithWindows(false, null);
            AdminMode.DeleteTask();
            Settings.DeleteStored();
            Notifications.Unregister();
            if (!IsElevated) TryDelete(DebugLog.DefaultPath);   // an elevated copy's log is in the install folder
            DeleteTempUpdateFolders();

            // No "done" message here: this process usually runs from the folder being removed,
            // so it has to exit promptly for the delayed folder delete to succeed.
            return RunFilesStep(scope, "--uninstall-files", quiet) ? 0 : 1;
        }

        /// Removes the shortcut, the uninstall entry and the install folder. May run elevated.
        public static void UninstallFiles(InstallScope scope)
        {
            TryDelete(ShortcutPath(scope));
            try { Root(scope).DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            // Every user's administrator-mode task starts this copy, so none is useful without it.
            if (scope == InstallScope.AllUsers && IsElevated) AdminMode.DeleteAllTasksElevated();

            string dir = InstallDir(scope);
            if (!Directory.Exists(dir)) return;
            if (Path.GetDirectoryName(CurrentExe).Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                // Can't delete our own running exe: let cmd do it once we have exited.
                var psi = new ProcessStartInfo("cmd.exe",
                    "/d /c ping -n 3 127.0.0.1 >nul & rd /s /q \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
                Process.Start(psi);
            }
            else
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        // ---- Helpers ------------------------------------------------------------------------

        /// Starts a program as the normal (unelevated) user, even from an elevated process.
        public static void LaunchUnelevated(string exe, string args)
        {
            if (IsElevated && string.IsNullOrEmpty(args))
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(exe, args ?? "") { UseShellExecute = false });
        }

        static void CreateShortcut(string lnk, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var t = sc.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "ThinkPad-style middle click and TrackPoint scrolling" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
        }
    }
}
