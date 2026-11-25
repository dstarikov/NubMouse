// Small pieces of UI: message boxes, the install/update dialog, and the tray icons (drawn at
// run time, so the project has no binary assets).
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NubMouse
{
    static class Ui
    {
        public static void Error(string text) { MessageBox.Show(text, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        public static void Info(string text) { MessageBox.Show(text, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        public static bool Confirm(string text)
        {
            return MessageBox.Show(text, Installer.AppName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK;
        }

        // ---- Tray icons -----------------------------------------------------------------

        /// A dark key cap with a TrackPoint dot: red when active, grey when paused.
        public static Icon MakeIcon(bool active) { return MakeIcon(active, false); }

        /// <paramref name="warning"/> adds an amber "!" badge in the bottom-right corner (the
        /// TrackPoint driver is still doing its own middle-button scrolling).
        public static Icon MakeIcon(bool active, bool warning)
        {
            using (var bmp = MakeBitmap(Math.Max(16, SystemInformation.SmallIconSize.Width), active, warning))
                // Icon.FromHandle doesn't own the handle; these icons live for the whole run.
                return Icon.FromHandle(bmp.GetHicon());
        }

        /// The same drawing as <see cref="MakeIcon(bool, bool)"/>, at any size (e.g. for notifications).
        public static Bitmap MakeBitmap(int size, bool active, bool warning)
        {
            var bmp = new Bitmap(size, size);
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    float pad = size / 16f, r = size / 4f;
                    var key = new RectangleF(pad, pad, size - 2 * pad - 1, size - 2 * pad - 1);
                    using (var path = RoundedRect(key, r))
                    using (var fill = new SolidBrush(Color.FromArgb(40, 40, 44)))
                    using (var edge = new Pen(Color.FromArgb(120, 120, 128), Math.Max(1f, size / 16f)))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }
                    float d = size * 0.42f, o = (size - d) / 2f;
                    using (var dot = new SolidBrush(active ? Color.FromArgb(230, 30, 40) : Color.FromArgb(140, 140, 140)))
                        g.FillEllipse(dot, o, o, d, d);
                    if (warning)
                    {
                        float b = size * 0.56f, bx = size - b, by = size - b;
                        using (var ring = new SolidBrush(Color.FromArgb(40, 40, 44)))
                            g.FillEllipse(ring, bx - 1, by - 1, b + 1, b + 1);
                        using (var amber = new SolidBrush(Color.FromArgb(255, 176, 0)))
                            g.FillEllipse(amber, bx, by, b - 1, b - 1);
                        float cx = bx + (b - 1) / 2f, w = Math.Max(1.5f, size / 10f);
                        using (var mark = new Pen(Color.FromArgb(30, 30, 30), w))
                            g.DrawLine(mark, cx, by + b * 0.2f, cx, by + b * 0.55f);
                        using (var mark = new SolidBrush(Color.FromArgb(30, 30, 30)))
                            g.FillEllipse(mark, cx - w / 2f, by + b * 0.68f, w, w);
                    }
                }
                return bmp;
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    enum InstallChoice { Cancel, Install, RunPortable }

    /// Shown when the exe is started from outside an installation (e.g. from Downloads).
    sealed class InstallDialog : Form
    {
        readonly CheckBox axisLock, scrollIndicator, startup, allUsers, checkUpdates, autoInstall;
        public InstallChoice Choice = InstallChoice.Cancel;
        public bool AxisLock { get { return axisLock.Checked; } }
        public bool ScrollIndicator { get { return scrollIndicator.Checked; } }
        public bool StartWithWindows { get { return startup.Checked; } }
        public InstallScope Scope { get { return allUsers.Checked ? InstallScope.AllUsers : InstallScope.User; } }
        public bool CheckForUpdates { get { return checkUpdates.Checked; } }
        public bool AutoInstallUpdates { get { return autoInstall.Enabled && autoInstall.Checked; } }

        public InstallDialog(InstallScope existing, Version existingVersion)
        {
            Text = Installer.AppName + " " + Installer.CurrentVersionText;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = SystemFonts.MessageBoxFont;
            Padding = new Padding(12);
            Icon = Ui.MakeIcon(true);

            string heading, button;
            if (existing == InstallScope.None)
            {
                heading = "Install NubMouse?";
                button = "&Install";
            }
            else
            {
                int cmp = Installer.CurrentVersion.CompareTo(existingVersion);
                heading = cmp > 0 ? "Update NubMouse " + existingVersion + " to " + Installer.CurrentVersion + "?"
                        : cmp == 0 ? "NubMouse " + existingVersion + " is already installed. Reinstall it?"
                        : "A newer NubMouse (" + existingVersion + ") is installed. Install this older " + Installer.CurrentVersion + " over it?";
                button = cmp > 0 ? "&Update" : "&Reinstall";
            }

            var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            layout.Controls.Add(new Label { Text = heading, AutoSize = true, Font = new Font(Font.FontFamily, Font.Size * 1.2f), Margin = new Padding(0, 0, 0, 8) });
            layout.Controls.Add(new Label
            {
                Text = "Middle click stays a middle click; hold it and push the TrackPoint to scroll.\n" +
                       "ELAN TrackPoints are configured automatically; on other drivers, turn off driver scrolling if needed.",
                AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 10)
            });

            var saved = Settings.Load();
            axisLock = new CheckBox { Text = "Scroll in &one direction at a time (like the TrackPoint driver)", AutoSize = true,
                                      Checked = saved.AxisLock, Margin = new Padding(3, 0, 0, 2) };
            scrollIndicator = new CheckBox { Text = "Turn the pointer into a scroll-direction &indicator while scrolling", AutoSize = true,
                                             Checked = saved.ScrollIndicator, Margin = new Padding(3, 0, 0, 8) };
            startup = new CheckBox { Text = "&Start with Windows", AutoSize = true, Checked = existing == InstallScope.None || Installer.StartsWithWindows };
            allUsers = new CheckBox { Text = "Install for &all users (needs administrator rights)", AutoSize = true, Checked = existing == InstallScope.AllUsers };
            checkUpdates = new CheckBox { Text = "Check for &updates once a day", AutoSize = true, Checked = saved.CheckForUpdates };
            autoInstall = new CheckBox { Text = "Install updates au&tomatically (per-user installs only)", AutoSize = true,
                                         Checked = saved.AutoInstallUpdates, Margin = new Padding(20, 0, 0, 0) };
            layout.Controls.Add(axisLock);
            layout.Controls.Add(scrollIndicator);
            layout.Controls.Add(startup);
            layout.Controls.Add(allUsers);
            layout.Controls.Add(checkUpdates);
            layout.Controls.Add(autoInstall);
            EventHandler sync = delegate { autoInstall.Enabled = checkUpdates.Checked && !allUsers.Checked; };
            checkUpdates.CheckedChanged += sync;
            allUsers.CheckedChanged += sync;
            sync(null, EventArgs.Empty);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var portable = new Button { Text = "Just &run it", AutoSize = true };
            var install = new Button { Text = button, AutoSize = true };
            install.Click += delegate { Choice = InstallChoice.Install; DialogResult = DialogResult.OK; };
            portable.Click += delegate { Choice = InstallChoice.RunPortable; DialogResult = DialogResult.OK; };
            allUsers.CheckedChanged += delegate { SetShield(install, allUsers.Checked && !Installer.IsElevated); };
            SetShield(install, allUsers.Checked && !Installer.IsElevated);
            buttons.Controls.AddRange(new Control[] { cancel, portable, install });
            layout.Controls.Add(buttons);

            Controls.Add(layout);
            AcceptButton = install;
            CancelButton = cancel;
        }

        static void SetShield(Button b, bool on)
        {
            const int BCM_SETSHIELD = 0x160C;
            b.FlatStyle = FlatStyle.System;
            Native.SendMessage(b.Handle, BCM_SETSHIELD, IntPtr.Zero, on ? (IntPtr)1 : IntPtr.Zero);
        }
    }
}
