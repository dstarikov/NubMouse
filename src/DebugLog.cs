// Optional diagnostic log, turned on with the "debug" option. Lines are queued and written by a
// background thread, so logging from the mouse hook costs only a string format and a queue add.
// When the log is off, every call returns immediately.
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace NubMouse
{
    static class DebugLog
    {
        const long MaxBytes = 20L * 1024 * 1024;   // start over rather than fill the disk

        static BlockingCollection<string> queue;
        static Thread writer;
        static readonly Stopwatch clock = Stopwatch.StartNew();

        public static bool On { get { return queue != null; } }
        public static string FilePath { get; private set; }
        public static string DefaultPath { get { return Path.Combine(Path.GetTempPath(), "NubMouse-debug.log"); } }

        public static void Start()
        {
            if (queue != null) return;
            // In administrator mode the log goes beside the exe in Program Files, not to %TEMP%:
            // any program running as the user can write to %TEMP% and could plant a link there
            // to make this elevated process write over another file.
            FilePath = Installer.IsElevated && AdminMode.IsAllowed(Installer.CurrentExe)
                ? Path.Combine(Path.GetDirectoryName(Installer.CurrentExe), "debug.log")
                : DefaultPath;
            queue = new BlockingCollection<string>();
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "NubMouse log" };
            writer.Start();
        }

        public static void Stop()
        {
            var q = queue;
            if (q == null) return;
            q.CompleteAdding();
            writer.Join(1000);
        }

        /// Milliseconds since start, with microsecond precision.
        public static double Now { get { return clock.Elapsed.TotalMilliseconds; } }

        public static void Write(string text)
        {
            var q = queue;
            if (q == null) return;
            string thread = Thread.CurrentThread.Name ?? "NubMouse ui";
            try { q.Add(Now.ToString("0000000.000") + "  [" + thread + "]  " + text); }
            catch (InvalidOperationException) { /* stopping */ }
        }

        public static void Write(string format, params object[] args)
        {
            if (queue != null) Write(string.Format(format, args));
        }

        static void WriteLoop()
        {
            StreamWriter w = null;
            try
            {
                w = new StreamWriter(FilePath, false) { AutoFlush = false };
                w.WriteLine("NubMouse {0} debug log, started {1:yyyy-MM-dd HH:mm:ss}. Times are ms since start.",
                            Installer.CurrentVersionText, DateTime.Now);
                foreach (var line in queue.GetConsumingEnumerable())
                {
                    w.WriteLine(line);
                    if (queue.Count == 0) w.Flush();   // flush when caught up, so the file is readable live
                    if (w.BaseStream.Length > MaxBytes)
                    {
                        w.Dispose();
                        w = new StreamWriter(FilePath, false);
                        w.WriteLine("(log restarted after reaching {0} MB)", MaxBytes / 1024 / 1024);
                    }
                }
            }
            catch (Exception) { /* logging must never take the app down */ }
            finally { if (w != null) w.Dispose(); }
        }
    }
}
