// Captures the README images from the real UI code: compiled together with src\*.cs by
// tools\screenshots.ps1, it shows the install dialog and the tray menu in front of a plain
// backdrop (so nothing from the desktop ends up in the images) and saves them as PNGs, plus the
// tray icon drawn large as a logo.
//
// Usage (via the script): Screenshots.exe <output folder>
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NubMouse.Tools
{
    static class Screenshots
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int l, t, r, b; }

        [DllImport("dwmapi.dll")]
        static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        const uint PW_RENDERFULLCONTENT = 2;

        static string outDir;
        static readonly Color Backdrop = Color.FromArgb(243, 243, 243);

        [STAThread]
        static int Main(string[] args)
        {
            outDir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(outDir);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

            Logo(256, true, "logo.png");
            Logo(256, false, "logo-paused.png");

            var screen = Screen.PrimaryScreen.WorkingArea;
            var back = new Form
            {
                FormBorderStyle = FormBorderStyle.None, BackColor = Backdrop, Bounds = screen,
                ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual
            };
            back.Show();

            // The dialog a first run from Downloads shows.
            var dialog = new InstallDialog(InstallScope.None, null) { TopMost = true };
            dialog.Show();

            TrayApp app = null;
            ContextMenuStrip menu = null;
            SettingsWindow win = null;
            int step = 0;
            var timer = new Timer { Interval = 700 };   // let each window paint fully before capturing
            timer.Tick += delegate
            {
                step++;
                if (step == 1)
                {
                    var r = WindowFrame(dialog);
                    r.Inflate(16, 16);                   // a margin of backdrop around the window
                    Capture(r, dialog, 16, "install-dialog.png");
                    dialog.Close();

                    // The tray menu as a typical install shows it: enabled, starting with Windows.
                    // The tray icon stays hidden and settings are only held in memory.
                    app = new TrayApp(new Settings { Enabled = true, HideTrayIcon = true });
                    menu = TrayMenu(app);
                    menu.Opened += delegate
                    {
                        MenuItem(app, "startupItem").Checked = true;
                        MenuItem(app, "hideItem").Checked = false;
                    };
                    menu.Show(screen.Left + 200, screen.Top + 80);
                }
                else if (step == 2)
                {
                    Capture(menu.Bounds, menu, 0, "tray-menu.png");
                    menu.Close();

                    // Settings window + live test pad (Scroll & click tab).
                    var settings = new Settings { Enabled = true, ScrollIndicator = true };
                    win = new SettingsWindow(settings, (ISettingsHost)app) { TopMost = true };
                    win.Show();
                }
                else if (step == 3)
                {
                    PopulateTestPad(win);
                    win.Refresh();
                    var r = WindowFrame(win);
                    r.Inflate(16, 16);
                    Capture(r, win, 16, "settings-scroll.png");
                    var genTab = FindControlByText(win, "General");
                    if (genTab != null)
                        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(genTab, new object[] { EventArgs.Empty });
                }
                else if (step == 4)
                {
                    var r = WindowFrame(win);
                    r.Inflate(16, 16);
                    Capture(r, win, 16, "settings-general.png");
                    win.Close();
                    timer.Stop();
                    app.Dispose();
                    back.Close();
                    Application.ExitThread();
                }
            };
            timer.Start();
            Application.Run();
            return 0;
        }

        /// Seeds the TestPad with a realistic scroll offset, native-drag path, middle-click ripple,
        /// and recent-event log entries so the screenshot shows what the pad actually draws.
        static void PopulateTestPad(SettingsWindow win)
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var pad = typeof(SettingsWindow).GetField("pad", flags).GetValue(win);
            if (pad != null)
            {
                var pt = pad.GetType();
                pt.GetField("offX", flags).SetValue(pad, -74.0);
                pt.GetField("offY", flags).SetValue(pad, -118.0);
                pt.GetField("targetX", flags).SetValue(pad, -74.0);
                pt.GetField("targetY", flags).SetValue(pad, -118.0);

                var path = (System.Collections.Generic.List<Point>)pt.GetField("path", flags).GetValue(pad);
                path.Clear();
                path.Add(new Point(110, 210));
                path.Add(new Point(142, 196));
                path.Add(new Point(182, 174));
                path.Add(new Point(226, 152));
                path.Add(new Point(268, 138));
                path.Add(new Point(304, 146));

                pt.GetField("flashPt", flags).SetValue(pad, new Point(365, 195));
                pt.GetField("flashTick", flags).SetValue(pad, Environment.TickCount - 160);
                ((Control)pad).Invalidate();
            }

            var status = (Label)typeof(SettingsWindow).GetField("padStatus", flags).GetValue(win);
            if (status != null)
            {
                status.Text = "Native middle drag passed through with Alt: moved +194, -64 px";
                status.ForeColor = Color.FromArgb(0, 103, 192);
            }

            var log = (ListBox)typeof(SettingsWindow).GetField("log", flags).GetValue(win);
            if (log != null)
            {
                log.Items.Clear();
                log.Items.Add("20:34:18  Native middle drag passed through with Alt: moved +194, -64 px");
                log.Items.Add("20:34:15  Middle click");
                log.Items.Add("20:34:12  Scrolled right 2.4 notches (smooth), 310 ms");
                log.Items.Add("20:34:09  Scrolled down 6.5 notches (smooth), 440 ms");
            }
        }

        static Control FindControlByText(Control root, string text)
        {
            foreach (Control c in root.Controls)
            {
                if (c.Text == text) return c;
                var found = FindControlByText(c, text);
                if (found != null) return found;
            }
            return null;
        }

        static ContextMenuStrip TrayMenu(TrayApp app)
        {
            var tray = (NotifyIcon)typeof(TrayApp).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(app);
            return tray.ContextMenuStrip;
        }

        static ToolStripMenuItem MenuItem(TrayApp app, string field)
        {
            var f = typeof(TrayApp).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) throw new MissingFieldException("TrayApp." + field + " (update tools\\Screenshots.cs)");
            return (ToolStripMenuItem)f.GetValue(app);
        }

        static Rectangle WindowFrame(Form f)
        {
            RECT r;
            DwmGetWindowAttribute(f.Handle, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT)));
            return Rectangle.FromLTRB(r.l, r.t, r.r, r.b);
        }

        static void Capture(Rectangle r, Control ctrl, int pad, string name)
        {
            try
            {
                using (var bmp = new Bitmap(r.Width, r.Height))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
                }
                Console.WriteLine("  {0}  {1}x{2}", name, r.Width, r.Height);
                return;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Headless / non-interactive desktop: fall back to PrintWindow.
            }

            using (var win = new Bitmap(ctrl.Width, ctrl.Height))
            {
                using (var g = Graphics.FromImage(win))
                {
                    IntPtr hdc = g.GetHdc();
                    try { PrintWindow(ctrl.Handle, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                var crop = new Rectangle(0, 0, win.Width, win.Height);
                if (ctrl is Form)
                {
                    var f = WindowFrame((Form)ctrl);
                    var rel = new Rectangle(f.Left - ctrl.Left, f.Top - ctrl.Top, f.Width, f.Height);
                    if (rel.Width > 0 && rel.Height > 0 && rel.X >= 0 && rel.Y >= 0 && rel.Right <= win.Width && rel.Bottom <= win.Height)
                        crop = rel;
                }
                if (pad <= 0)
                {
                    using (var outBmp = win.Clone(crop, win.PixelFormat))
                    {
                        outBmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
                        Console.WriteLine("  {0}  {1}x{2} (PrintWindow)", name, outBmp.Width, outBmp.Height);
                    }
                }
                else
                {
                    using (var framed = new Bitmap(crop.Width + pad * 2, crop.Height + pad * 2))
                    {
                        using (var g = Graphics.FromImage(framed))
                        {
                            g.Clear(Backdrop);
                            g.DrawImage(win, new Rectangle(pad, pad, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
                            using (var pen = new Pen(Color.FromArgb(200, 200, 200)))
                                g.DrawRectangle(pen, pad, pad, crop.Width - 1, crop.Height - 1);
                        }
                        framed.Save(Path.Combine(outDir, name), ImageFormat.Png);
                        Console.WriteLine("  {0}  {1}x{2} (PrintWindow)", name, framed.Width, framed.Height);
                    }
                }
            }
        }

        /// The tray icon's design (see Ui.MakeIcon) drawn large, on a transparent background.
        static void Logo(int size, bool active, string name)
        {
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float pad = size / 16f, radius = size / 4f, d = radius * 2;
                    var key = new RectangleF(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
                    using (var path = new GraphicsPath())
                    using (var fill = new SolidBrush(Color.FromArgb(40, 40, 44)))
                    using (var edge = new Pen(Color.FromArgb(120, 120, 128), size / 16f))
                    using (var dot = new SolidBrush(active ? Color.FromArgb(230, 30, 40) : Color.FromArgb(140, 140, 140)))
                    {
                        path.AddArc(key.X, key.Y, d, d, 180, 90);
                        path.AddArc(key.Right - d, key.Y, d, d, 270, 90);
                        path.AddArc(key.Right - d, key.Bottom - d, d, d, 0, 90);
                        path.AddArc(key.X, key.Bottom - d, d, d, 90, 90);
                        path.CloseFigure();
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                        float dd = size * 0.42f, o = (size - dd) / 2f;
                        g.FillEllipse(dot, o, o, dd, dd);
                    }
                }
                bmp.Save(Path.Combine(outDir, name), ImageFormat.Png);
            }
            Console.WriteLine("  {0}  {1}x{1}", name, size);
        }
    }
}
