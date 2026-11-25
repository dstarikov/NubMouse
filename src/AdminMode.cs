// "Run as administrator": lets scrolling reach elevated windows (admin terminals, Task Manager…),
// which Windows otherwise shields from a normal program's input.
//
// A program can't start elevated at logon from the Run key without a UAC prompt every time, so
// this uses a per-user scheduled task with "Run with highest privileges". Creating, changing or
// deleting the task needs one UAC prompt; starting it ("schtasks /run") doesn't. Every unelevated
// start of NubMouse (Start menu, after an update, …) hands over to the task, so it always
// ends up elevated. The task's logon trigger is "Start with Windows" while this mode is on.
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
        static string TaskName { get { return "NubMouse elevated - " + Environment.UserName; } }

        public static string UserSid { get { return WindowsIdentity.GetCurrent().User.Value; } }

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
            return Schtasks("/Run /TN \"" + TaskName + "\"", null) == 0;
        }

        // ---- Changes (need elevation) -------------------------------------------------------

        /// Creates or replaces the task. Runs here if elevated, otherwise in an elevated child.
        public static bool CreateTask(string exe, bool atLogon)
        {
            if (!Installer.IsElevated)
                return Installer.RunElevated("--admin-task create --user " + UserSid + (atLogon ? " --at-logon" : "") +
                                             " --exe \"" + exe + "\"");
            return CreateTaskElevated(exe, atLogon, UserSid);
        }

        public static bool DeleteTask()
        {
            if (!TaskExists()) return true;
            if (!Installer.IsElevated) return Installer.RunElevated("--admin-task delete");
            return Schtasks("/Delete /F /TN \"" + TaskName + "\"", null) == 0;
        }

        /// The elevated side of CreateTask. <paramref name="sid"/> is the user who asked, which
        /// is passed along because the elevated process may run as a different account.
        public static bool CreateTaskElevated(string exe, bool atLogon, string sid)
        {
            string file = Path.Combine(Path.GetTempPath(), "NubMouse-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, BuildTaskXml(exe, atLogon, sid), Encoding.Unicode);
                return Schtasks("/Create /F /TN \"" + TaskName + "\" /XML \"" + file + "\"", null) == 0;
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
