// Single instance per logon session, plus a tiny remote control: a second launch (or
// "NubMouse.exe --toggle" from a shortcut or script) talks to the running copy through
// a hidden message-only window.
using System;
using System.Threading;
using System.Windows.Forms;
using N = NubMouse.Native;

namespace NubMouse
{
    enum Command { None = 0, Show = 1, Toggle = 2, Enable = 3, Disable = 4, Exit = 5, FixDriver = 6, DriverSettings = 7, Settings = 8, Last = Settings }

    static class Instance
    {
        const string MutexName = "Local\\NubMouse";
        const string WindowTitle = "NubMouse.Control";

        /// Tries to become the running instance. Returns null if another copy already runs.
        public static Mutex TryClaim()
        {
            bool fresh;
            var m = new Mutex(true, MutexName, out fresh);
            if (fresh) return m;
            m.Dispose();
            return null;
        }

        public static bool IsRunning { get { return FindRunning() != IntPtr.Zero; } }

        static IntPtr FindRunning()
        {
            return N.FindWindowEx(N.HWND_MESSAGE, IntPtr.Zero, null, WindowTitle);
        }

        public static bool Send(Command c)
        {
            IntPtr w = FindRunning();
            return w != IntPtr.Zero && N.PostMessage(w, N.WM_APP + (int)c, IntPtr.Zero, IntPtr.Zero);
        }

        /// Asks the running copy in this session to exit and waits for it (used before updating).
        public static void StopRunning()
        {
            if (!Send(Command.Exit)) return;
            try
            {
                using (var m = new Mutex(false, MutexName))
                {
                    bool got;
                    try { got = m.WaitOne(5000); }
                    catch (AbandonedMutexException) { got = true; }
                    if (got) m.ReleaseMutex();
                }
            }
            catch (Exception) { }
        }

        /// The receiving end, created by the running instance on its UI thread.
        public sealed class Receiver : NativeWindow, IDisposable
        {
            readonly Action<Command> onCommand;

            public Receiver(Action<Command> onCommand)
            {
                this.onCommand = onCommand;
                CreateHandle(new CreateParams { Caption = WindowTitle, Parent = N.HWND_MESSAGE });
                // When running elevated, Windows drops messages from normal-privilege processes
                // unless they are explicitly allowed.
                for (int c = 1; c <= (int)Command.Last; c++)
                    N.ChangeWindowMessageFilterEx(Handle, N.WM_APP + c, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
            }

            protected override void WndProc(ref Message m)
            {
                int c = m.Msg - N.WM_APP;
                if (c > 0 && c <= (int)Command.Last) { onCommand((Command)c); return; }
                base.WndProc(ref m);
            }

            public void Dispose() { DestroyHandle(); }
        }
    }
}
