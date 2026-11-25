// "Run as administrator": lets scrolling reach elevated windows (admin terminals, Task Manager…),
// which Windows otherwise shields from a normal program's input.
//
// A program can't start elevated at logon from the Run key without a UAC prompt every time, so
// this uses a per-user scheduled task with "Run with highest privileges". Creating, changing or
// deleting the task needs one UAC prompt; starting it ("schtasks /run") doesn't. Every unelevated
// start of NubMouse (Start menu, after an update, …) hands over to the task, so it always
// ends up elevated. The task's logon trigger is "Start with Windows" while this mode is on.
//
// The task only ever starts the all-users copy in Program Files. A per-user copy lives in
// %LOCALAPPDATA%, which any program running as the user can write to, so pointing the task there
// would let such a program swap the exe and get administrator rights without a prompt.
using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace NubMouse
{
    static class AdminMode
    {
        /// The current user's task. The elevated side names it from the SID it was given instead,
        /// because a standard user elevating with an administrator's password runs that side as
        /// the administrator, whose name would differ.
        static string TaskName { get { return TaskNameFor(UserSid); } }

        static string TaskNameFor(string sid)
        {
            string name = sid;
            try { name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; } catch (Exception) { }
            int slash = name.LastIndexOf('\\');   // DOMAIN\user: a backslash would make a task folder
            return "NubMouse elevated - " + (slash >= 0 ? name.Substring(slash + 1) : name);
        }

        public static string UserSid { get { return WindowsIdentity.GetCurrent().User.Value; } }

        /// The only exe the task may start: the all-users install, which needs administrator
        /// rights to change.
        public static string AllowedExe { get { return Installer.InstalledExe(InstallScope.AllUsers); } }

        public static bool IsAllowed(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return false;
            try { return string.Equals(Path.GetFullPath(exe), AllowedExe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public static bool TaskExists()
        {
            return Schtasks("/Query /TN \"" + TaskName + "\"", null) == 0;
        }

        /// The task's XML, or null if there is no task.
        static string TaskXml()
        {
            string xml;
            return Schtasks("/Query /XML /TN \"" + TaskName + "\"", out xml) == 0 ? xml : null;
        }

        public static bool StartsAtLogon()
        {
            var xml = TaskXml();
            return xml != null && xml.Contains("<LogonTrigger>");
        }

        /// True if the task starts <paramref name="exe"/> (so it survives being reinstalled elsewhere).
        public static bool TaskRuns(string exe)
        {
            var xml = TaskXml();
            return xml != null && xml.IndexOf("<Command>" + SecurityElement.Escape(exe) + "</Command>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// Starts NubMouse through the task (elevated, no prompt).
        public static bool RunTask()
        {
            // A task left from an older version may point somewhere else; never start that.
            if (!File.Exists(AllowedExe) || !TaskRuns(AllowedExe)) return false;
            return Schtasks("/Run /TN \"" + TaskName + "\"", null) == 0;
        }

        // ---- Changes (need elevation) -------------------------------------------------------

        /// Creates or replaces the task. Runs here if elevated, otherwise in an elevated child.
        public static bool CreateTask(string exe, bool atLogon)
        {
            if (!IsAllowed(exe)) return false;
            if (!Installer.IsElevated)
                return Installer.RunElevated("--admin-task create --user " + UserSid + (atLogon ? " --at-logon" : "") +
                                             " --exe \"" + exe + "\"");
            return CreateTaskElevated(exe, atLogon, UserSid);
        }

        public static bool DeleteTask()
        {
            if (!TaskExists()) return true;
            if (!Installer.IsElevated) return Installer.RunElevated("--admin-task delete --user " + UserSid);
            return DeleteTaskElevated(UserSid);
        }

        /// Deletes every user's NubMouse task (when the all-users copy they start is removed).
        public static void DeleteAllTasksElevated()
        {
            string list;
            if (Schtasks("/Query /FO CSV /NH", out list) != 0) return;
            const string Prefix = "\\NubMouse elevated - ";
            foreach (var line in list.Split('\n'))
            {
                // Each line starts with the quoted task path: "\NubMouse elevated - name","…
                var l = line.Trim();
                if (!l.StartsWith("\"" + Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int end = l.IndexOf('"', 1);
                if (end <= 1) continue;
                string name = l.Substring(1, end - 1);
                Schtasks("/Delete /F /TN \"" + name + "\"", null);
            }
        }

        /// The elevated side of DeleteTask, for the user with <paramref name="sid"/>.
        public static bool DeleteTaskElevated(string sid)
        {
            string name = TaskNameFor(sid);
            if (Schtasks("/Query /TN \"" + name + "\"", null) != 0) return true;
            return Schtasks("/Delete /F /TN \"" + name + "\"", null) == 0;
        }

        /// The elevated side of CreateTask. <paramref name="sid"/> is the user who asked, which
        /// is passed along because the elevated process may run as a different account.
        public static bool CreateTaskElevated(string exe, bool atLogon, string sid)
        {
            // Checked again here: this side runs elevated, so it's the one that has to refuse.
            if (!IsAllowed(exe)) return false;
            // The XML goes beside the exe in Program Files, not in %TEMP%: a normal program
            // could edit it there between writing and registering, and point the task elsewhere.
            string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe)), "task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, BuildTaskXml(exe, atLogon, sid), Encoding.Unicode);
                return Schtasks("/Create /F /TN \"" + TaskNameFor(sid) + "\" /XML \"" + file + "\"", null) == 0;
            }
            finally { try { File.Delete(file); } catch (Exception) { } }
        }

        public static string BuildTaskXml(string exe, bool atLogon, string sid)
        {
            string e =SecurityElement.Escape(exe), u = SecurityElement.Escape(sid);
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n");
            xml.Append("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n");
            xml.Append("  <RegistrationInfo><Description>Starts NubMouse with administrator rights so it can scroll in elevated windows.</Description></RegistrationInfo>\n");
            if (atLogon)
                xml.Append("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + u + "</UserId></LogonTrigger></Triggers>\n");
            xml.Append("  <Principals><Principal id=\"Author\"><UserId>" + u + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n");
            xml.Append("  <Settings>\n");
            xml.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n");
            xml.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n");
            xml.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n");
            xml.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\n");
            xml.Append("    <Enabled>true</Enabled>\n");
            xml.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n");
            xml.Append("    <Priority>4</Priority>\n");   // tasks default to below-normal priority; the hook wants normal
            xml.Append("  </Settings>\n");
            xml.Append("  <Actions Context=\"Author\"><Exec><Command>" + e + "</Command><WorkingDirectory>" +
                       SecurityElement.Escape(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\n");
            xml.Append("</Task>\n");
            return xml.ToString();
        }

        // ---- schtasks.exe -------------------------------------------------------------------

        static int Schtasks(string args, string ignored)
        {
            string output;
            return Schtasks(args, out output);
        }

        static int Schtasks(string args, out string output)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
