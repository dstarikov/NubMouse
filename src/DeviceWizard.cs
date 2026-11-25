// Interactive device setup wizard: auto-detects known pointing sticks, firmware-scroll keyboards
// (like TEX Shura), and regular external mice, highlights whichever device you wiggle live, and
// applies recommended per-device modes in one click.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace NubMouse
{
    sealed class DeviceWizard : Form
    {
        static readonly Color WindowBack = Color.FromArgb(243, 243, 243);
        static readonly Color CardBack = Color.White;
        static readonly Color CardBorder = Color.FromArgb(224, 224, 226);
        static readonly Color ActiveBorder = Color.FromArgb(0, 103, 192);
        static readonly Color ActiveBack = Color.FromArgb(240, 247, 255);
        static readonly Color Subtle = Color.FromArgb(96, 96, 102);

        static readonly string[] TypeChoices =
        {
            "ThinkPad / TrackPoint (Full NubMouse)",
            "TEX / firmware-scroll keyboard (3D drag only)",
            "Regular mouse (Ignore \u2014 keep native)",
        };

        readonly ISettingsHost host;
        readonly float k;
        readonly FlowLayoutPanel cards;
        readonly Label liveLabel;
        readonly Timer poll;
        readonly Font boldFont;
        readonly List<DeviceRow> rows = new List<DeviceRow>();
        string shownActive;

        sealed class DeviceRow
        {
            public PointingDevice Device;
            public Panel Card;
            public Label Title;
            public ComboBox Choice;
            public bool Highlighted;
        }

        public DeviceWizard(ISettingsHost host)
        {
            this.host = host;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            Text = Installer.AppName + " \u2014 Device setup wizard";
            Icon = Ui.MakeIcon(true);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = SystemFonts.MessageBoxFont;
            boldFont = new Font(Font, FontStyle.Bold);
            BackColor = WindowBack;
            ClientSize = new Size(S(640), S(520));

            // Header
            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                BackColor = CardBack, Padding = new Padding(S(20), S(14), S(20), S(12))
            };
            header.Controls.Add(new Label { Text = "Identify & configure your pointing devices", Font = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, S(4)) });
            header.Controls.Add(new Label
            {
                Text = "Wiggle or click any TrackPoint, TEX keyboard, or mouse to see it light up below. Known devices are pre-configured automatically; pick a type for any unknown device.",
                AutoSize = true, ForeColor = Subtle, MaximumSize = new Size(S(595), 0), Margin = new Padding(0, 0, 0, S(6))
            });
            liveLabel = new Label
            {
                Text = "Waiting for movement\u2026 wiggle a mouse or TrackPoint.",
                AutoSize = true, ForeColor = ActiveBorder, Font = boldFont, Margin = Padding.Empty
            };
            header.Controls.Add(liveLabel);
            var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = CardBorder };

            // Footer
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = CardBack,
                Padding = new Padding(S(16), S(10), S(16), S(10))
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var leftBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            var refreshBtn = new Button { Text = "&Refresh", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)) };
            var suggestBtn = new Button { Text = "Use &recommended", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)), Margin = new Padding(S(6), 0, 0, 0) };
            leftBtns.Controls.Add(refreshBtn);
            leftBtns.Controls.Add(suggestBtn);

            var rightBtns = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
            var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(S(10), S(2), S(10), S(2)) };
            var applyBtn = new Button { Text = "&Apply settings", AutoSize = true, Padding = new Padding(S(12), S(2), S(12), S(2)), Margin = new Padding(S(6), 0, 0, 0) };
            rightBtns.Controls.Add(cancelBtn);
            rightBtns.Controls.Add(applyBtn);

            footer.Controls.Add(leftBtns, 0, 0);
            footer.Controls.Add(rightBtns, 1, 0);
            var footerLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = CardBorder };
            AcceptButton = applyBtn;
            CancelButton = cancelBtn;

            // Body
            cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                Padding = new Padding(S(16), S(12), S(16), S(12)), BackColor = WindowBack
            };

            Controls.Add(cards);
            Controls.Add(footerLine);
            Controls.Add(footer);
            Controls.Add(headerLine);
            Controls.Add(header);

            refreshBtn.Click += delegate { Populate(false); };
            suggestBtn.Click += delegate { Populate(true); };
            applyBtn.Click += delegate { Apply(); DialogResult = DialogResult.OK; Close(); };

            Populate(true);

            poll = new Timer { Interval = 120 };
            poll.Tick += delegate { UpdateLive(); };
            poll.Start();
        }

        int S(int px) { return (int)Math.Round(px * k); }

        void Populate(bool useSuggestions)
        {
            cards.Controls.Clear();
            rows.Clear();
            shownActive = null;

            var devices = RawInputDevices.ListMouseDevices();
            if (devices.Count == 0)
            {
                cards.Controls.Add(new Label { Text = "No pointing devices detected.", AutoSize = true, ForeColor = Subtle });
                return;
            }

            // If no known TrackPoint or TEX keyboard was detected, avoid suggesting Ignore for everything.
            bool anyPointingStick = false;
            foreach (var d in devices) if (d.LikelyTrackPoint) { anyPointingStick = true; break; }

            var saved = Settings.Load();
            var existingModes = saved.ParsedDeviceModes();
            int cardW = S(586);

            foreach (var dev in devices)
            {
                DeviceMode initial;
                if (useSuggestions)
                {
                    initial = (!anyPointingStick && dev.SuggestedMode == DeviceMode.Ignore) ? DeviceMode.Full : dev.SuggestedMode;
                }
                else if (!existingModes.TryGetValue(dev.Key, out initial))
                {
                    initial = dev.SuggestedMode;
                }

                var row = new DeviceRow { Device = dev };
                var card = new Panel
                {
                    Width = cardW, AutoSize = true, BackColor = CardBack,
                    Padding = new Padding(S(14), S(10), S(14), S(10)), Margin = new Padding(0, 0, 0, S(8))
                };
                card.Paint += (s, e) =>
                {
                    using (var pen = new Pen(row.Highlighted ? ActiveBorder : CardBorder, row.Highlighted ? 2f : 1f))
                        e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                };

                var tbl = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, AutoSize = true, Width = cardW - S(28), BackColor = Color.Transparent };
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                tbl.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                var title = new Label
                {
                    Text = dev.Name, Font = boldFont, AutoSize = true, UseMnemonic = false,
                    MaximumSize = new Size(S(290), 0), Margin = new Padding(0, 0, S(8), S(2))
                };
                var reason = new Label
                {
                    Text = dev.SuggestedReason, AutoSize = true, ForeColor = Subtle,
                    MaximumSize = new Size(S(290), 0), Margin = Padding.Empty
                };
                var combo = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList, Width = S(245),
                    Anchor = AnchorStyles.Right, Margin = new Padding(S(8), S(2), 0, 0)
                };
                combo.Items.AddRange(TypeChoices);
                combo.SelectedIndex = (int)initial;

                tbl.Controls.Add(title, 0, 0);
                tbl.Controls.Add(reason, 0, 1);
                tbl.Controls.Add(combo, 1, 0);
                tbl.SetRowSpan(combo, 2);
                card.Controls.Add(tbl);
                cards.Controls.Add(card);

                row.Card = card;
                row.Title = title;
                row.Choice = combo;
                rows.Add(row);
            }
            UpdateLive();
        }

        void UpdateLive()
        {
            string key = host.LastActiveDeviceKey;
            if (string.Equals(key, shownActive, StringComparison.OrdinalIgnoreCase)) return;
            shownActive = key;

            string matched = null;
            foreach (var r in rows)
            {
                bool hit = !string.IsNullOrEmpty(key) && string.Equals(r.Device.Key, key, StringComparison.OrdinalIgnoreCase);
                r.Highlighted = hit;
                r.Card.BackColor = hit ? ActiveBack : CardBack;
                r.Title.Text = hit ? r.Device.Name + "  \u2190 moving now" : r.Device.Name;
                r.Title.ForeColor = hit ? ActiveBorder : SystemColors.ControlText;
                r.Card.Invalidate();
                if (hit) matched = r.Device.Name;
            }
            if (matched != null) liveLabel.Text = "Moving now: " + matched;
            else if (!string.IsNullOrEmpty(key)) liveLabel.Text = "Moving now: " + key + " (click Refresh if newly plugged in)";
        }

        void Apply()
        {
            var map = new Dictionary<string, DeviceMode>(StringComparer.OrdinalIgnoreCase);
            bool anyDragOnly = false;
            foreach (var r in rows)
            {
                var mode = (DeviceMode)Math.Max(0, r.Choice.SelectedIndex);
                if (mode != DeviceMode.Full) map[r.Device.Key] = mode;
                if (mode == DeviceMode.DragOnly) anyDragOnly = true;
            }
            string formatted = Settings.FormatDeviceModes(map);
            host.Change(s =>
            {
                s.DeviceModes = formatted;
                s.DeviceFilter = "";
                if (anyDragOnly && s.DragShortcuts().Count == 0) s.DragKeys = "Ctrl+Alt";
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                poll.Stop();
                poll.Dispose();
                boldFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
