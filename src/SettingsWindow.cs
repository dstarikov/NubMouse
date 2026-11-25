// The settings window: every scrolling and middle-click setting in one place, next to a test pad
// that shows what NubMouse sends (wheel events, middle clicks, passed-through middle drags)
// without switching to another app to try a change.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace NubMouse
{
    /// What the settings window needs from the running app (TrayApp): the same actions as the tray menu.
    interface ISettingsHost
    {
        void Change(Action<Settings> edit);          // edit, save, reconfigure the engine, refresh both UIs
        void SetEnabled(bool on);
        bool MiddleHeld { get; }
        string LastActiveDeviceKey { get; }
        bool StartsWithWindows { get; }
        void ToggleStartup();
        void ToggleAdmin();                         // may restart the app (elevated or not)
        void SetTrayIconShown(bool show);
        void CheckForUpdatesNow();
        void OpenDriverSettings();
        void FixDriver();
        void SyncDriver();                          // apply SwitchDriver / ScrollWhenPaused now
        bool? ElanScrolls { get; }                  // ELAN driver: is Middle Button Action Scrolling? null = not ELAN
        bool DriverProblem { get; }
        string AutoInstallBlocked { get; }          // why automatic installs are unavailable, or null
        void Open(string target);
    }

    sealed class SettingsWindow : Form, IMessageFilter
    {
        readonly Settings settings;
        readonly ISettingsHost host;
        readonly Action<Action<Settings>> change;   // host.Change
        readonly Action<bool> setEnabled;
        readonly float k;                           // DPI scale; sizes below are at 100 %
        readonly TestPad pad;
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        readonly List<Action> loaders = new List<Action>();
        bool loading;
        string activeProfileKey;                    // null = All devices (default), else device key for per-device profile

        Settings Profile() { return activeProfileKey == null ? settings : settings.EffectiveProfileFor(activeProfileKey); }

        void ChangeProfile(Action<Settings> edit)
        {
            string key = activeProfileKey;
            if (key == null) change(edit);
            else change(s => edit(s.GetOrCreateProfile(key)));
        }

        // Click time limit slider: 2..20 = 200..2000 ms, the last stop = no limit (as in the tray menu).
        const int TimeoutMin = 2, TimeoutMax = 20, TimeoutNone = 21;
        static readonly string[] StickyLabels = { "Loose", "Normal", "Firm", "Fixed" };
        static readonly string[] DragKeyLabels = { "Alt", "Ctrl", "Shift" };
        static readonly string[] DragComboLabels = { "Ctrl+Alt", "Alt+Shift", "Ctrl+Shift" };

        // Look: a light grey window with white section cards, like Windows 11 Settings.
        static readonly Color WindowBack = Color.FromArgb(243, 243, 243), CardBack = Color.White, CardBorder = Color.FromArgb(224, 224, 226);
        static readonly Color Subtle = Color.FromArgb(96, 96, 102);

        Font titleFont, sectionFont;
        int indent;                                 // left indent for the next rows (settings that depend on the one above)
        Label stateLabel, padStatus;
        ListBox log;

        public SettingsWindow(Settings settings, ISettingsHost host)
        {
            this.settings = settings;
            this.host = host;
            change = host.Change;
            setEnabled = host.SetEnabled;
            using (var g = CreateGraphics()) k = g.DpiX / 96f;

            Text = Installer.AppName + " settings";
            Icon = Ui.MakeIcon(true);
            StartPosition = FormStartPosition.CenterScreen;
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(S(1080), S(760));
            MinimumSize = new Size(S(900), S(560));
            BackColor = WindowBack;
            titleFont = new Font(Font.FontFamily, Font.Size * 1.45f, FontStyle.Bold);
            sectionFont = new Font(Font.FontFamily, Font.Size * 1.15f, FontStyle.Bold);

            // ---- Header: name, state, on/off -------------------------------------------------
            var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, BackColor = CardBack,
                                                Padding = new Padding(S(20), S(14), S(20), S(14)) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(new Label { Text = Installer.AppName, Font = titleFont, AutoSize = true, Margin = Padding.Empty }, 0, 0);
            stateLabel = new Label { AutoSize = true, ForeColor = Subtle, Margin = new Padding(S(2), S(2), 0, 0) };
            header.Controls.Add(stateLabel, 0, 1);
            var enabled = new CheckBox { Text = "Enabled", AutoSize = true, Font = sectionFont, Anchor = AnchorStyles.Right };
            enabled.CheckedChanged += delegate { if (!loading) setEnabled(enabled.Checked); };
            tips.SetToolTip(enabled, "Off = the middle button and TrackPoint work as if NubMouse weren't running.");
            header.Controls.Add(enabled, 1, 0);
            header.SetRowSpan(enabled, 2);
            loaders.Add(() =>
            {
                enabled.Checked = settings.Enabled;
                stateLabel.Text = settings.Enabled ? "On: hold the middle button and push the TrackPoint to scroll."
                                                   : "Paused: the middle button and TrackPoint work normally.";
            });
            var headerLine = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = CardBorder };

            // ---- Footer: buttons ----------------------------------------------------------------
            var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = CardBack,
                                                Padding = new Padding(S(20), S(10), S(20), S(10)) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var reset = new Button { Text = "Reset to defaults", AutoSize = true, Padding = new Padding(S(6), S(2), S(6), S(2)) };
            reset.Click += delegate { ResetDefaults(); };
            tips.SetToolTip(reset, "Puts the scrolling, middle-click and native-drag settings back to their defaults.");
            var close = new Button { Text = "Close", AutoSize = true, Padding = new Padding(S(14), S(2), S(14), S(2)), Anchor = AnchorStyles.Right };
            close.Click += delegate { Close(); };
            footer.Controls.Add(reset, 0, 0);
            footer.Controls.Add(close, 1, 0);
            CancelButton = close;
            var footerLine = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = CardBorder };

            // ---- Body: settings cards on the left, test pad on the right ------------------------
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(S(14), S(14), S(14), S(4)) };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(510)));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Two pages of cards under a tab strip: tuning (with the test pad beside it) and general.
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var cards = Page();
            var general = Page();
            reset.Visible = false;
            cards.VisibleChanged += delegate { reset.Visible = cards.Visible; };   // it resets the tuning settings only
            left.Controls.Add(Tabs(new[] { "General", "Scroll && click" }, new Control[] { general, cards }), 0, 0);
            var pages = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            pages.Controls.Add(cards);
            pages.Controls.Add(general);
            left.Controls.Add(pages, 0, 1);
            body.Controls.Add(left, 0, 0);

            BuildProfileCard(cards);

            var list = Card(cards, "Scrolling", "How the TrackPoint scrolls while you hold the middle button.");
            Check(list, "Take over middle button for scrolling",
                  "Turn this off if your keyboard already handles middle-button scrolling in firmware (e.g. TEX Shura) and you only want NubMouse's 3D/CAD drag shortcuts below.",
                  () => Profile().MiddleButtonScroll, v => ChangeProfile(s => s.MiddleButtonScroll = v));
            var speedRow = Slider(list, "Speed", 1, 16, 100, () => Profile().Speed, v => ChangeProfile(s => s.Speed = v),
                   v => v == 4 ? "default" : null,
                   "Wheel units per pixel of TrackPoint movement (one wheel notch is 120 units). The slider goes to 16; type up to 100.");
            var hSpeedRow = Slider(list, "Horizontal", 0, 16, 100, () => Profile().HorizontalSpeed, v => ChangeProfile(s => s.HorizontalSpeed = v),
                   v => v == 0 ? "same as vertical" : null,
                   "Separate horizontal speed. 0 = same as vertical.");
            var naturalBox = Check(list, "Natural (reversed) direction", null, () => Profile().Natural, v => ChangeProfile(s => s.Natural = v));
            var notchedBox = Check(list, "Notched scrolling (whole wheel steps)", "Send only whole wheel notches, for apps that mishandle smooth scrolling.",
                  () => Profile().Notched, v => ChangeProfile(s => s.Notched = v));
            var axisLockBox = Check(list, "Lock to one direction (vertical or horizontal)", "Like the TrackPoint driver: a push scrolls straight, not diagonally.",
                  () => Profile().AxisLock, v => ChangeProfile(s => s.AxisLock = v));
            indent = S(24);
            var stickyRow = StickinessSlider(list);
            indent = 0;
            var indicatorBox = Check(list, "Show scroll-direction indicator", "While scrolling, the pointer turns into a small indicator showing the scroll direction.",
                  () => Profile().ScrollIndicator, v => ChangeProfile(s => s.ScrollIndicator = v));
            indent = S(24);
            var lookRow = IndicatorLook(list);
            var sizeRow = Slider(list, "Size", Settings.IndicatorSizeMin, Settings.IndicatorSizeMax, Settings.IndicatorSizeMax,
                   () => Profile().IndicatorSize, v => ChangeProfile(s => s.IndicatorSize = v), v => v == 34 ? "px, default" : "px",
                   "Indicator diameter in pixels at 100 % display scaling. Tray presets: Tiny 20, Small 26, Medium 34, Large 46.");
            var gapRow = Slider(list, "Gap", 0, 40, 100, () => Profile().IndicatorOffset, v => ChangeProfile(s => s.IndicatorOffset = v),
                   v => v == 0 ? "on the pointer" : "px from the pointer",
                   "Advanced: put the indicator beside the pointer instead of on it (below and to the right), this many pixels away. " +
                   "0 = on the pointer, which hides the pointer while scrolling.");
            var advanced = new LinkLabel { Text = "Advanced: place it beside the pointer", AutoSize = true, Margin = new Padding(indent + S(2), S(2), 0, S(4)) };
            list.Controls.Add(advanced);
            indent = 0;
            bool showGap = Profile().IndicatorOffset > 0;
            advanced.LinkClicked += delegate { showGap = !showGap; LoadValues(); };

            var clickCard = Card(cards, "Middle click", "When a press counts as a click, and what happens after it.");
            Slider(clickCard, "Deadzone", 0, 20, 100, () => Profile().Deadzone, v => ChangeProfile(s => s.Deadzone = v),
                   v => (v == 3 ? "px, default" : "px"),
                   "How far you push the TrackPoint while holding the button before it counts as a scroll instead of a click.");
            TimeoutSlider(clickCard);
            Check(clickCard, "Cancel apps' autoscroll after a middle click", "Many apps start click-to-autoscroll on a middle click; this ends it right away.",
                  () => Profile().CancelAutoscroll, v => ChangeProfile(s => s.CancelAutoscroll = v));

            loaders.Add(() =>
            {
                var p = Profile();
                bool ms = p.MiddleButtonScroll;
                speedRow.Enabled = hSpeedRow.Enabled = naturalBox.Enabled = notchedBox.Enabled = axisLockBox.Enabled = indicatorBox.Enabled = clickCard.Enabled = ms;
                stickyRow.Enabled = ms && p.AxisLock;
                lookRow.Enabled = sizeRow.Enabled = gapRow.Enabled = advanced.Enabled = ms && p.ScrollIndicator;
                gapRow.Visible = showGap || p.IndicatorOffset > 0;
                advanced.Text = gapRow.Visible ? "Hide advanced placement" : "Advanced: place it beside the pointer";
            });

            list = Card(cards, "Native drag (for 3D modeling apps)",
                        "Blender, CAD and other 3D apps rotate and pan the view by dragging with the middle button. " +
                        "Hold a shortcut as you press the middle button to give them that drag instead of scrolling. Any shortcut works.");
            var dragStatus = new Label
            {
                AutoSize = true, UseMnemonic = false, Font = new Font(Font, FontStyle.Bold),
                MaximumSize = new Size(S(CardWidth - 36), 0), Margin = new Padding(S(2), 0, 0, S(6))
            };
            list.Controls.Add(dragStatus);
            loaders.Add(() =>
            {
                var p = Profile();
                var sc = p.DragShortcuts();
                if (sc.Count == 0)
                {
                    dragStatus.Text = "Disabled \u2014 select a quick key, combo, or add a shortcut below to enable.";
                    dragStatus.ForeColor = Subtle;
                }
                else
                {
                    dragStatus.Text = "Enabled (" + Settings.ShortcutsText(sc) + (p.KeyMoveToDrag ? " + move nub)" : " + middle button)");
                    dragStatus.ForeColor = Color.FromArgb(0, 103, 192);
                }
            });
            DragKeys(list);
            list.Controls.Add(new Panel { Height = 1, Width = S(CardWidth - 36), BackColor = CardBorder, Margin = new Padding(0, S(10), 0, S(10)) });
            list.Controls.Add(new Label
            {
                Text = "Firmware-scroll keyboards (e.g. TEX Shura)",
                Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, S(2))
            });
            Note(list, "For keyboards whose middle button (M_FN) scrolls in firmware and can't be rebound:");
            Check(list, "Drag without middle button",
                  "Works around firmware axis locking for smooth 360\u00b0 orbiting by using your chosen shortcut key(s) + nub movement instead of the middle button. Automatically enables Ctrl+Alt if no shortcut is ticked.",
                  () => Profile().KeyMoveToDrag, v => ChangeProfile(s =>
                  {
                      s.KeyMoveToDrag = v;
                      if (v && s.DragShortcuts().Count == 0) s.DragKeys = "Ctrl+Alt";
                  }));
            indent = S(22);
            Note(list, "Hold shortcut + move nub for smooth 360\u00b0 drag (no middle button needed).");
            indent = 0;

            var advWheel = new LinkLabel { Text = "Advanced: convert firmware scroll to drag (M_FN)", AutoSize = true, Margin = new Padding(S(2), S(6), 0, S(2)) };
            list.Controls.Add(advWheel);
            var wheelBox = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(S(14), S(2), 0, 0), Visible = false };
            Check(wheelBox, "Convert firmware scroll + hotkey to drag",
                  "Works like Quick keys + middle button using M_FN, but inherits the firmware's axis-locked scroll steps.",
                  () => Profile().WheelToDrag, v => ChangeProfile(s => s.WheelToDrag = v));
            indent = S(22);
            Note(wheelBox, "Uses M_FN + shortcut; inherits firmware axis locking.");
            indent = 0;
            var sameKeys = new CheckBox { Text = "Use same shortcuts as above", AutoSize = true, Margin = new Padding(S(22), S(2), 0, S(2)) };
            sameKeys.CheckedChanged += delegate
            {
                if (loading) return;
                ChangeProfile(s => s.WheelDragKeys = sameKeys.Checked ? "" : (s.WheelDragShortcuts().Count > 0 ? s.WheelDragKeys : "Alt"));
            };
            wheelBox.Controls.Add(sameKeys);
            var wKeysRow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(S(CardWidth - 56), 0), Margin = new Padding(S(38), S(2), 0, S(2)) };
            foreach (var lbl in new[] { "Alt", "Ctrl", "Shift", "Ctrl+Alt", "Alt+Shift", "Ctrl+Shift" })
            {
                var sc = Settings.ParseShortcuts(lbl)[0];
                var cb = new CheckBox { Text = lbl, AutoSize = true, Margin = new Padding(0, S(2), S(10), S(2)) };
                cb.CheckedChanged += delegate
                {
                    if (loading) return;
                    bool on = cb.Checked;
                    ChangeProfile(s =>
                    {
                        var l = s.WheelDragShortcuts();
                        l.RemoveAll(x => SameKeys(x, sc));
                        if (on) l.Add(sc);
                        s.WheelDragKeys = Settings.ShortcutsText(l);
                    });
                };
                loaders.Add(() =>
                {
                    var p = Profile();
                    var wlist = p.WheelDragShortcuts();
                    cb.Checked = wlist.Exists(x => SameKeys(x, sc));
                    cb.Enabled = p.WheelToDrag && wlist.Count > 0;
                });
                wKeysRow.Controls.Add(cb);
            }
            loaders.Add(() =>
            {
                var p = Profile();
                sameKeys.Checked = p.WheelDragShortcuts().Count == 0;
                sameKeys.Enabled = p.WheelToDrag;
            });
            wheelBox.Controls.Add(wKeysRow);
            list.Controls.Add(wheelBox);
            advWheel.LinkClicked += delegate
            {
                wheelBox.Visible = !wheelBox.Visible;
                advWheel.Text = wheelBox.Visible ? "Hide advanced firmware-scroll drag" : "Advanced: convert firmware scroll to drag (M_FN)";
            };
            BuildGeneralPage(general);

            // Test pad card: what you're doing now, the pad, and recent events.
            var padCard = new CardTable { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, BackColor = CardBack,
                                          Padding = new Padding(S(16), S(12), S(16), S(14)), Margin = new Padding(S(6), 0, 0, S(10)) };
            padCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            padCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            padCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            padCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            padCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            padCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            padCard.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            padCard.RowStyles.Add(new RowStyle(SizeType.Absolute, S(132)));
            padCard.Controls.Add(new Label { Text = "Test pad", Font = sectionFont, AutoSize = true, Margin = new Padding(0, 0, 0, S(2)) }, 0, 0);
            padCard.Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Subtle, MaximumSize = new Size(S(520), 0), Margin = new Padding(0, 0, 0, S(6)),
                Text = "Try changes here without switching apps: hold the middle button and push the TrackPoint to scroll, " +
                       "tap it to click, or hold a native-drag shortcut, press the middle button and move."
            }, 0, 1);
            var padDevice = new Label
            {
                AutoSize = false, AutoEllipsis = true, UseMnemonic = false, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Height = S(20), ForeColor = Subtle, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, S(2)),
                Text = "Input device: move pointer to detect"
            };
            padCard.Controls.Add(padDevice, 0, 2);
            padStatus = new Label { AutoSize = false, AutoEllipsis = true, Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = S(24), Font = new Font(Font, FontStyle.Bold),
                                    TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, 0, 0, S(4)) };
            padCard.Controls.Add(padStatus, 0, 3);
            pad = new TestPad(settings, () => host.MiddleHeld, () => host.LastActiveDeviceKey, k) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            padCard.Controls.Add(pad, 0, 4);

            var logHead = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, S(10), 0, S(4)) };
            logHead.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            logHead.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            logHead.Controls.Add(new Label { Text = "Recent events", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
            var clear = new LinkLabel { Text = "Clear", AutoSize = true, Margin = Padding.Empty };
            logHead.Controls.Add(clear, 1, 0);
            padCard.Controls.Add(logHead, 0, 5);
            log = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle,
                                BackColor = TestPad.Back, ForeColor = TestPad.Ink, Margin = Padding.Empty };
            padCard.Controls.Add(log, 0, 6);
            clear.LinkClicked += delegate { log.Items.Clear(); };
            body.Controls.Add(padCard, 1, 0);

            var padDevNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action refreshPadDevNames = () =>
            {
                padDevNames.Clear();
                foreach (var d in RawInputDevices.ListMouseDevices()) padDevNames[d.Key] = d.Name;
            };
            refreshPadDevNames();
            string lastPadDevKey = null;
            Action<bool> updatePadDevice = force =>
            {
                string key = host.LastActiveDeviceKey;
                if (!force && string.Equals(key, lastPadDevKey, StringComparison.OrdinalIgnoreCase)) return;
                lastPadDevKey = key;
                if (string.IsNullOrEmpty(key))
                {
                    padDevice.Text = "Input device: move pointer to detect";
                    padDevice.ForeColor = Subtle;
                    return;
                }
                string name;
                if (!padDevNames.TryGetValue(key, out name))
                {
                    refreshPadDevNames();
                    if (!padDevNames.TryGetValue(key, out name)) name = key;
                }
                var modes = settings.ParsedDeviceModes();
                var allowed = settings.AllowedDeviceKeys();
                DeviceMode mode;
                if (!modes.TryGetValue(key, out mode))
                    mode = allowed.Count > 0 && !allowed.Contains(key) ? DeviceMode.Ignore : DeviceMode.Full;
                string modeText = mode == DeviceMode.DragOnly ? "3D drag only" : mode == DeviceMode.Ignore ? "Ignored (native)" : "Full NubMouse";
                bool custom = settings.DeviceProfiles != null && settings.DeviceProfiles.ContainsKey(key);
                padDevice.Text = "Input device: " + name + "  \u00b7  " + modeText + (custom ? "  \u00b7  custom profile" : "");
                padDevice.ForeColor = mode == DeviceMode.Ignore ? Subtle : Color.FromArgb(0, 103, 192);
            };
            var padDevTimer = new Timer { Interval = 150 };
            padDevTimer.Tick += delegate { if (Visible) updatePadDevice(false); };
            padDevTimer.Start();
            FormClosed += delegate { padDevTimer.Stop(); padDevTimer.Dispose(); };
            loaders.Add(() => updatePadDevice(true));

            pad.StatusChanged += (text, color) => { padStatus.Text = text; padStatus.ForeColor = color; };
            pad.Logged += line =>
            {
                log.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "   " + line);
                while (log.Items.Count > 200) log.Items.RemoveAt(log.Items.Count - 1);
            };
            loaders.Add(() =>
            {
                if (!settings.Enabled) { padStatus.Text = "NubMouse is paused, so the pad shows the plain middle button."; padStatus.ForeColor = TestPad.Amber; }
                else if (padStatus.ForeColor == TestPad.Amber && padStatus.Text.StartsWith("NubMouse is paused")) padStatus.Text = "";
                if (padStatus.Text.Length == 0) { padStatus.Text = "Ready: try scrolling or clicking on the grid."; padStatus.ForeColor = Subtle; }
            });

            // Dock order: the fill goes in first so the top/bottom bars take their space.
            Controls.Add(body);
            Controls.Add(headerLine);
            Controls.Add(header);
            Controls.Add(footerLine);
            Controls.Add(footer);

            LoadValues();
            Application.AddMessageFilter(this);
            Shown += delegate { close.Focus(); };
        }

        int S(int px) { return (int)Math.Round(px * k); }

        /// Shows the current settings (also called by the tray when they change there).
        public void LoadValues()
        {
            loading = true;
            try { foreach (var l in loaders) l(); }
            finally { loading = false; }
            pad.Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnFormClosed(e);
        }

        /// Wheel messages go to the focused window on some setups: route any over the pad to it,
        /// and keep scrolling over sliders / numeric boxes / closed dropdowns scrolling the page
        /// instead of accidentally changing their values.
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != Native.WM_MOUSEWHEEL && m.Msg != Native.WM_MOUSEHWHEEL) return false;
            if (!IsHandleCreated || !Visible) return false;
            var target = Control.FromChildHandle(m.HWnd);
            if (target == null || target.FindForm() != this) return false;

            long lp = m.LParam.ToInt64();
            var pt = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            if (pad.IsHandleCreated && pad.Visible && pad.RectangleToScreen(pad.ClientRectangle).Contains(pt))
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                pad.OnWheel(delta, m.Msg == Native.WM_MOUSEHWHEEL);
                return true;
            }

            // Redirect wheel events aimed at a TrackBar, NumericUpDown, or closed ComboBox inside
            // this window to its enclosing scrollable page so scrolling never nudges a control.
            bool block = false;
            ScrollableControl page = null;
            for (var c = target; c != null && c != this; c = c.Parent)
            {
                if (c is TrackBar || c is NumericUpDown) block = true;
                else if (c is ComboBox && !((ComboBox)c).DroppedDown) block = true;
                if (page == null)
                {
                    var sc = c as ScrollableControl;
                    if (sc != null && sc.AutoScroll) page = sc;
                }
            }
            if (!block) return false;
            if (page != null && page.IsHandleCreated) Native.SendMessage(page.Handle, m.Msg, m.WParam, m.LParam);
            return true;
        }

        void ResetDefaults()
        {
            var d = new Settings();
            bool tex = activeProfileKey != null && settings.IsFirmwareScrollOrDragOnly(activeProfileKey);
            ChangeProfile(s =>
            {
                s.MiddleButtonScroll = !tex;
                s.Speed = d.Speed; s.HorizontalSpeed = d.HorizontalSpeed; s.Natural = d.Natural; s.Notched = d.Notched;
                s.ScrollIndicator = d.ScrollIndicator; s.IndicatorOffset = d.IndicatorOffset; s.IndicatorStyle = d.IndicatorStyle; s.IndicatorSize = d.IndicatorSize; s.AxisLock = d.AxisLock; s.Stickiness = d.Stickiness;
                s.Deadzone = d.Deadzone; s.ClickTimeout = d.ClickTimeout; s.CancelAutoscroll = d.CancelAutoscroll;
                s.DragKeys = tex ? "Ctrl+Alt" : d.DragKeys; s.KeyMoveToDrag = tex; s.WheelToDrag = d.WheelToDrag; s.WheelDragKeys = d.WheelDragKeys;
            });
            LoadValues();
        }

        void BuildProfileCard(FlowLayoutPanel cards)
        {
            var list = Card(cards, "Configuring", "Apply the settings below to all pointing devices, or pick one device to give it its own custom profile.");
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(0, 0, 0, S(3)), Margin = new Padding(0, S(2), 0, S(4)) };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(320), Margin = new Padding(0, S(2), S(8), S(2)) };
            var useDefault = new Button { Text = "Use default", AutoSize = true, Padding = new Padding(S(6), S(1), S(6), S(1)), Margin = new Padding(0, S(1), 0, S(1)) };
            tips.SetToolTip(useDefault, "Remove this device's custom profile so it inherits the 'All devices (default)' settings again.");
            row.Controls.Add(combo);
            row.Controls.Add(useDefault);
            list.Controls.Add(row);

            var status = new Label
            {
                AutoSize = true, UseMnemonic = false, ForeColor = Subtle,
                MaximumSize = new Size(S(CardWidth - 36), 0), Margin = new Padding(S(2), S(2), 0, S(2))
            };
            list.Controls.Add(status);

            var keys = new List<string>();       // index 0 = null (All devices), 1..N = device keys
            var names = new List<string>();

            Action populate = () =>
            {
                keys.Clear();
                names.Clear();
                combo.Items.Clear();

                keys.Add(null);
                names.Add("All devices (default)");
                combo.Items.Add("All devices (default)");

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dev in RawInputDevices.ListMouseDevices())
                {
                    seen.Add(dev.Key);
                    keys.Add(dev.Key);
                    names.Add(dev.Name);
                    bool custom = settings.DeviceProfiles != null && settings.DeviceProfiles.ContainsKey(dev.Key);
                    combo.Items.Add(custom ? dev.Name + " (custom)" : dev.Name);
                }
                if (settings.DeviceProfiles != null)
                {
                    foreach (var kv in settings.DeviceProfiles)
                    {
                        if (seen.Contains(kv.Key)) continue;
                        keys.Add(kv.Key);
                        names.Add(kv.Key);
                        combo.Items.Add(kv.Key + " (custom, disconnected)");
                    }
                }

                int sel = 0;
                if (activeProfileKey != null)
                {
                    for (int i = 1; i < keys.Count; i++)
                        if (string.Equals(keys[i], activeProfileKey, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
                    if (sel == 0) activeProfileKey = null;
                }
                combo.SelectedIndex = sel;
            };

            combo.SelectedIndexChanged += delegate
            {
                if (loading || combo.SelectedIndex < 0 || combo.SelectedIndex >= keys.Count) return;
                activeProfileKey = keys[combo.SelectedIndex];
                LoadValues();
            };

            useDefault.Click += delegate
            {
                if (activeProfileKey == null) return;
                string kRem = activeProfileKey;
                change(s => { if (s.DeviceProfiles != null) s.DeviceProfiles.Remove(kRem); });
                LoadValues();
            };

            var boldFont = new Font(Font, FontStyle.Bold);
            FormClosed += delegate { boldFont.Dispose(); };

            loaders.Add(() =>
            {
                populate();
                bool hasCustom = activeProfileKey != null && settings.DeviceProfiles != null && settings.DeviceProfiles.ContainsKey(activeProfileKey);
                useDefault.Enabled = hasCustom;
                useDefault.Visible = activeProfileKey != null;
                if (activeProfileKey == null)
                {
                    status.Font = Font;
                    status.ForeColor = Subtle;
                    status.Text = "Editing default settings used by every device without its own custom profile.";
                }
                else
                {
                    int idx = combo.SelectedIndex;
                    string label = idx >= 0 && idx < names.Count ? names[idx] : activeProfileKey;
                    if (hasCustom)
                    {
                        status.Font = boldFont;
                        status.ForeColor = Color.FromArgb(0, 103, 192);
                        status.Text = "Custom profile active for " + label + ". Changes below apply only to this device.";
                    }
                    else
                    {
                        status.Font = Font;
                        status.ForeColor = Color.FromArgb(0, 103, 192);
                        status.Text = "Inheriting default settings for " + label + ". Change any setting below to give it a custom profile.";
                    }
                }
            });
        }

        // ---- Pages and tabs ----------------------------------------------------------------------

        FlowLayoutPanel Page()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                                         AutoScroll = true, Margin = Padding.Empty, Padding = new Padding(0, 0, S(8), 0) };
        }

        /// A row of pill-shaped tabs; each shows its page and hides the others.
        Control Tabs(string[] names, Control[] pages)
        {
            var strip = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, S(10)), Padding = Padding.Empty };
            var buttons = new RadioButton[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var b = new RadioButton
                {
                    Text = names[i], Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleCenter,
                    Padding = new Padding(S(10), S(3), S(10), S(3)), Margin = new Padding(0, 0, S(6), 0), Cursor = Cursors.Hand,
                    Font = sectionFont, BackColor = WindowBack
                };
                b.FlatAppearance.BorderSize = 1;
                b.FlatAppearance.CheckedBackColor = CardBack;
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(234, 234, 236);
                b.CheckedChanged += delegate
                {
                    b.FlatAppearance.BorderColor = b.Checked ? CardBorder : WindowBack;
                    b.ForeColor = b.Checked ? SystemColors.ControlText : Subtle;
                    pages[index].Visible = b.Checked;
                };
                buttons[i] = b;
                strip.Controls.Add(b);
            }
            for (int i = 0; i < pages.Length; i++) pages[i].Visible = i == 0;
            buttons[0].Checked = true;
            for (int i = 1; i < buttons.Length; i++) { buttons[i].FlatAppearance.BorderColor = WindowBack; buttons[i].ForeColor = Subtle; }
            return strip;
        }

        Button ActionButton(FlowLayoutPanel list, string text, EventHandler click, string tip)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(indent + S(2), S(6), 0, S(4)), Padding = new Padding(S(6), S(1), S(6), S(1)) };
            b.Click += click;
            if (tip != null) tips.SetToolTip(b, tip);
            list.Controls.Add(b);
            return b;
        }

        Label Note(FlowLayoutPanel list, string text)
        {
            var l = new Label { Text = text, AutoSize = true, ForeColor = Subtle, MaximumSize = new Size(S(CardWidth - 36) - indent, 0),
                                Margin = new Padding(indent + S(2), S(2), 0, S(4)) };
            list.Controls.Add(l);
            return l;
        }

        // ---- General page: devices, driver, startup, updates, troubleshooting -------------------

        void BuildGeneralPage(FlowLayoutPanel page)
        {
            // Devices
            var list = Card(page, "Devices", "Which pointing devices NubMouse listens to. Leave this on all devices unless an " +
                                             "external mouse should keep its own middle button.");
            DevicesSection(list);

            // TrackPoint driver
            list = Card(page, "TrackPoint driver", "NubMouse needs the driver to send a plain middle click instead of doing its " +
                                                   "own middle-button scrolling.");
            var driverState = Note(list, "");
            var warning = new Label { AutoSize = true, ForeColor = TestPad.Amber, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(S(2), S(4), 0, 0),
                                      MaximumSize = new Size(S(CardWidth - 36), 0), Text = "The driver is still scrolling while NubMouse is on." };
            list.Controls.Add(warning);
            var fix = ActionButton(list, "Fix it", delegate { host.FixDriver(); }, "Switches the driver's Middle Button Action to Middle Click, or opens its settings.");
            var switchBox = Check(list, "Set it to Middle Click while NubMouse is on",
                "Switches the driver's Middle Button Action to Middle Click when NubMouse starts or is turned on.",
                () => settings.SwitchDriver, v => { change(s => s.SwitchDriver = v); host.SyncDriver(); });
            indent = S(24);
            var pausedBox = Check(list, "Back to Scrolling when paused or closed",
                "While NubMouse is paused or closed, the driver's own middle-button scrolling works. Untick to keep a plain middle click.",
                () => settings.ScrollWhenPaused, v => { change(s => s.ScrollWhenPaused = v); host.SyncDriver(); });
            indent = 0;
            ActionButton(list, "Open TrackPoint driver settings…", delegate { host.OpenDriverSettings(); },
                   "The driver's own settings, where Middle Button Action is.");
            loaders.Add(() =>
            {
                bool? elan = host.ElanScrolls;
                driverState.Text = elan == null
                    ? "This isn't an ELAN driver, so NubMouse can't change it for you: open its settings and set Middle Button Action to Middle Click."
                    : "ELAN driver. Middle Button Action is set to " + (elan.Value ? "Scrolling." : "Middle Click.");
                warning.Visible = fix.Visible = host.DriverProblem;
                switchBox.Visible = pausedBox.Visible = elan != null;
                pausedBox.Enabled = settings.SwitchDriver;
            });

            // Startup (a Store/MSIX package starts through Windows' own startup settings, and can't
            // use the elevated task)
            list = Card(page, "Startup", "How NubMouse starts and where it shows.");
            if (Installer.IsPackaged)
                ActionButton(list, "Start with Windows: open Windows startup settings…", delegate { host.Open("ms-settings:startupapps"); },
                             "Store installs are started with Windows from Settings > Apps > Startup.");
            else
            {
                Check(list, "Start with Windows", null, () => host.StartsWithWindows,
                      v => { if (v != host.StartsWithWindows) host.ToggleStartup(); LoadValues(); });
                Check(list, "Run as administrator (scroll in admin windows)",
                      "Lets it scroll in windows that run as administrator (admin terminals, Task Manager, some installers). " +
                      "Windows asks for permission once. NubMouse restarts to switch.",
                      () => settings.RunAsAdmin, v => { if (v != settings.RunAsAdmin) host.ToggleAdmin(); if (!IsDisposed) LoadValues(); });
            }
            Check(list, "Show the tray icon",
                  "Without the icon NubMouse keeps running; start it again from the Start menu to show the icon or this window.",
                  () => !settings.HideTrayIcon, v => { host.SetTrayIconShown(v); LoadValues(); });

            // Updates
            if (Installer.IsPackaged)
                Card(page, "Updates", "This copy was installed from the Microsoft Store, which keeps it up to date.");
            else
            {
                list = Card(page, "Updates", "Update checks only contact GitHub, and only when turned on.");
                Check(list, "Check for updates automatically", "Once a day; shows a notification when a new version is out.",
                      () => settings.CheckForUpdates, v => change(s => s.CheckForUpdates = v));
                indent = S(24);
                var autoInstall = Check(list, "Install updates automatically", "Installs a found update straight away, without asking.",
                      () => settings.AutoInstallUpdates, v => change(s => s.AutoInstallUpdates = v));
                var blocked = Note(list, "");
                indent = 0;
                ActionButton(list, "Check for updates now", delegate { host.CheckForUpdatesNow(); }, null);
                loaders.Add(() =>
                {
                    string why = host.AutoInstallBlocked;
                    autoInstall.Enabled = why == null;
                    blocked.Text = why ?? "";
                    blocked.Visible = why != null;
                });
            }

            // Troubleshooting & about
            list = Card(page, "Troubleshooting and about", Installer.AppName + " " + Installer.CurrentVersionText + ". Settings are saved " +
                        (Settings.UsesPortableIni ? "in " + Settings.PortableIniPath + "." : "for your Windows account."));
            var logNote = Note(list, "");
            var startLog = ActionButton(list, "Start a debug log", delegate
            {
                DebugLog.Start();
                DebugLog.Write("debug log started from the settings window, " + Installer.AppName + " " + Installer.CurrentVersionText);
                LoadValues();
            }, "Records every middle press, scroll step and autoscroll decision until NubMouse exits. Attach it to a bug report.");
            var openLog = ActionButton(list, "Open debug log", delegate { host.Open(DebugLog.FilePath); }, null);
            loaders.Add(() =>
            {
                logNote.Text = DebugLog.On
                    ? "A debug log is being written to " + DebugLog.FilePath + " until NubMouse exits."
                    : "A debug log records what NubMouse sees and does, to attach to a bug report.";
                startLog.Visible = !DebugLog.On;
                openLog.Visible = DebugLog.On;
            });
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, S(8), 0, 0) };
            var help = new LinkLabel { Text = "Help and documentation", AutoSize = true, Margin = new Padding(S(2), 0, S(16), 0) };
            help.LinkClicked += delegate { host.Open(Installer.RepoUrl + "#readme"); };
            var issue = new LinkLabel { Text = "Report a problem", AutoSize = true, Margin = Padding.Empty };
            issue.LinkClicked += delegate { host.Open(Installer.RepoUrl + "/issues/new"); };
            links.Controls.Add(help);
            links.Controls.Add(issue);
            list.Controls.Add(links);
        }

        /// Per-device modes: Full NubMouse (default), 3D drag shortcuts only (for TEX Shura /
        /// firmware-scroll keyboards), or Ignore (native mouse).
        void DevicesSection(FlowLayoutPanel list)
        {
            var rows = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, S(2), 0, 0) };
            var activeLabel = new Label
            {
                Text = "Move or click any pointing device to identify it here.",
                AutoSize = true, UseMnemonic = false, ForeColor = Subtle, Margin = new Padding(S(2), S(6), 0, S(2)),
                MaximumSize = new Size(S(CardWidth - 40), 0)
            };
            var refresh = new LinkLabel { Text = "Refresh the list", AutoSize = true, Margin = new Padding(S(2), S(2), 0, S(2)) };
            var wizardBtn = new Button { Text = "Device setup &wizard\u2026", AutoSize = true, Padding = new Padding(S(8), S(2), S(8), S(2)), Margin = new Padding(0, S(6), 0, S(4)) };
            wizardBtn.Click += delegate
            {
                using (var wiz = new DeviceWizard(host))
                {
                    if (wiz.ShowDialog(this) == DialogResult.OK) LoadValues();
                }
            };
            tips.SetToolTip(wizardBtn, "Auto-detect TrackPoints, TEX keyboards, and regular mice, wiggle each device to identify it, and apply recommended settings.");
            list.Controls.Add(rows);
            list.Controls.Add(activeLabel);
            list.Controls.Add(refresh);
            list.Controls.Add(wizardBtn);

            var modeLabels = new[] { "Full NubMouse", "3D drag only", "Ignore (native)" };
            var devLabels = new Dictionary<string, Label>(StringComparer.OrdinalIgnoreCase);
            var devNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var devCombos = new Dictionary<string, ComboBox>(StringComparer.OrdinalIgnoreCase);
            var boldFont = new Font(Font, FontStyle.Bold);
            string shownActive = null;

            Action<string, DeviceMode> setMode = (key, mode) =>
            {
                change(s =>
                {
                    var map = s.ParsedDeviceModes();
                    if (mode == DeviceMode.Full) map.Remove(key);
                    else map[key] = mode;
                    s.DeviceModes = Settings.FormatDeviceModes(map);
                    s.DeviceFilter = "";   // superseded by per-device modes
                });
            };

            Action fill = () =>
            {
                rows.Controls.Clear();
                devLabels.Clear();
                devNames.Clear();
                devCombos.Clear();
                shownActive = null;
                var devices = RawInputDevices.ListMouseDevices();
                if (devices.Count == 0)
                {
                    rows.Controls.Add(new Label { Text = "(No pointing devices detected)", AutoSize = true, ForeColor = Subtle });
                    return;
                }
                foreach (var dev in devices)
                {
                    string key = dev.Key;
                    var row = new TableLayoutPanel
                    {
                        ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)),
                        Width = S(CardWidth - 36)
                    };
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(CardWidth - 180)));
                    row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(140)));
                    var lbl = new Label
                    {
                        Text = dev.Name, AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left,
                        MaximumSize = new Size(S(CardWidth - 186), 0), Margin = new Padding(0, S(4), S(6), S(2))
                    };
                    var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(136), Anchor = AnchorStyles.Right, Margin = Padding.Empty };
                    cb.Items.AddRange(modeLabels);
                    cb.SelectedIndexChanged += delegate
                    {
                        if (loading || cb.SelectedIndex < 0) return;
                        setMode(key, (DeviceMode)cb.SelectedIndex);
                    };
                    tips.SetToolTip(lbl, dev.Key);
                    tips.SetToolTip(cb, "Full NubMouse = scrolling + middle-click + drag shortcuts.\n3D drag only = leaves middle button untouched, enables smooth shortcut + nub drag (e.g. TEX Shura).\nIgnore (native) = untouched external mouse.");
                    row.Controls.Add(lbl, 0, 0);
                    row.Controls.Add(cb, 1, 0);
                    rows.Controls.Add(row);
                    devLabels[key] = lbl;
                    devNames[key] = dev.Name;
                    devCombos[key] = cb;
                }
            };
            fill();

            var activeTimer = new Timer { Interval = 150 };
            activeTimer.Tick += delegate
            {
                if (!Visible) return;
                string key = host.LastActiveDeviceKey;
                if (string.Equals(key, shownActive, StringComparison.OrdinalIgnoreCase)) return;
                shownActive = key;
                string matchedName = null;
                foreach (var kv in devLabels)
                {
                    bool hit = !string.IsNullOrEmpty(key) && string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase);
                    kv.Value.Font = hit ? boldFont : Font;
                    kv.Value.Text = hit ? devNames[kv.Key] + "  \u2190 active" : devNames[kv.Key];
                    if (hit) matchedName = devNames[kv.Key];
                }
                if (matchedName != null)
                {
                    activeLabel.Text = "Active device: " + matchedName;
                    activeLabel.ForeColor = Color.FromArgb(0, 103, 192);
                }
                else if (!string.IsNullOrEmpty(key))
                {
                    activeLabel.Text = "Active device: " + key + " (click Refresh if newly plugged in)";
                    activeLabel.ForeColor = Color.FromArgb(0, 103, 192);
                }
            };
            activeTimer.Start();
            FormClosed += delegate { activeTimer.Stop(); activeTimer.Dispose(); boldFont.Dispose(); };
            refresh.LinkClicked += delegate { loading = true; try { fill(); } finally { loading = false; } LoadValues(); };
            loaders.Add(() =>
            {
                var modes = settings.ParsedDeviceModes();
                var allowed = settings.AllowedDeviceKeys();
                foreach (var kv in devCombos)
                {
                    DeviceMode m;
                    if (!modes.TryGetValue(kv.Key, out m))
                        m = allowed.Count > 0 && !allowed.Contains(kv.Key) ? DeviceMode.Ignore : DeviceMode.Full;
                    kv.Value.SelectedIndex = (int)m;
                }
            });
            Note(list, "Full NubMouse = all features · 3D drag only = TEX Shura / firmware-scroll · Ignore = native external mouse.");
        }

        // ---- Cards and rows -------------------------------------------------------------------

        const int CardWidth = 480;                  // at 100 %; the left column is a little wider for the scroll bar

        /// A white rounded section with a title and a short description; returns the panel rows go into.
        FlowLayoutPanel Card(FlowLayoutPanel cards, string title, string description)
        {
            var card = new CardFlow
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = CardBack, Padding = new Padding(S(16), S(12), S(16), S(12)), Margin = new Padding(0, 0, 0, S(12)),
                MinimumSize = new Size(S(CardWidth), 0), MaximumSize = new Size(S(CardWidth), 0)
            };
            card.Controls.Add(new Label { Text = title, Font = sectionFont, AutoSize = true, Margin = new Padding(0, 0, 0, S(2)) });
            card.Controls.Add(new Label
            {
                Text = description, AutoSize = true, ForeColor = Subtle, MaximumSize = new Size(S(CardWidth - 36), 0),
                Margin = new Padding(0, 0, 0, S(8))
            });
            cards.Controls.Add(card);
            return card;
        }

        /// Rounded white card drawn over the window's grey; children inherit the white.
        static void PaintCard(Control c, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(c.Parent != null ? c.Parent.BackColor : WindowBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = 8 * g.DpiX / 96f;
            var rect = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
            using (var path = new GraphicsPath())
            {
                path.AddArc(rect.X, rect.Y, 2 * r, 2 * r, 180, 90);
                path.AddArc(rect.Right - 2 * r, rect.Y, 2 * r, 2 * r, 270, 90);
                path.AddArc(rect.Right - 2 * r, rect.Bottom - 2 * r, 2 * r, 2 * r, 0, 90);
                path.AddArc(rect.X, rect.Bottom - 2 * r, 2 * r, 2 * r, 90, 90);
                path.CloseFigure();
                using (var fill = new SolidBrush(CardBack)) g.FillPath(fill, path);
                using (var pen = new Pen(CardBorder)) g.DrawPath(pen, path);
            }
        }

        sealed class CardFlow : FlowLayoutPanel
        {
            public CardFlow() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaintBackground(PaintEventArgs e) { PaintCard(this, e); }
        }

        sealed class CardTable : TableLayoutPanel
        {
            public CardTable() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
            protected override void OnPaintBackground(PaintEventArgs e) { PaintCard(this, e); }
        }

        CheckBox Check(FlowLayoutPanel list, string text, string tip, Func<bool> get, Action<bool> set)
        {
            var box = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(indent + S(2), S(5), 0, S(5)) };
            box.CheckedChanged += delegate { if (!loading) set(box.Checked); };
            if (tip != null) tips.SetToolTip(box, tip);
            loaders.Add(() => box.Checked = get());
            list.Controls.Add(box);
            return box;
        }

        TableLayoutPanel Row(FlowLayoutPanel list, string text, string tip)
        {
            var row = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(112)));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(indent, 0, 0, 0) };
            tips.SetToolTip(label, tip);
            row.Controls.Add(label, 0, 0);
            list.Controls.Add(row);
            return row;
        }

        /// Slider for the common range plus a number box that also takes values past its end.
        Control Slider(FlowLayoutPanel list, string text, int min, int max, int typedMax, Func<int> get, Action<int> set,
                       Func<int, string> note, string tip)
        {
            var row = Row(list, text, tip);
            var bar = new NoWheelTrackBar { Minimum = min, Maximum = max, TickFrequency = 1, LargeChange = 1, TickStyle = TickStyle.None,
                                            AutoSize = false, Width = S(150), Height = S(28), Anchor = AnchorStyles.Left };
            var num = new NoWheelNumericUpDown { Minimum = min, Maximum = typedMax, Width = S(60), Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right };
            var info = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            cell.Controls.Add(num);
            cell.Controls.Add(info);
            row.Controls.Add(bar, 1, 0);
            row.Controls.Add(cell, 2, 0);
            tips.SetToolTip(bar, tip);
            tips.SetToolTip(num, tip);
            Action<int> show = v =>
            {
                bar.Value = Math.Max(min, Math.Min(max, v));
                if (num.Value != v) num.Value = v;
                info.Text = note(v) ?? "";
            };
            // ValueChanged, not Scroll: the mouse wheel and keys move the slider without raising Scroll.
            bar.ValueChanged += delegate { if (loading) return; num.Value = bar.Value; };
            num.ValueChanged += delegate
            {
                int v = (int)num.Value;
                bool was = loading;
                loading = true;
                try { show(v); }
                finally { loading = was; }
                if (!was) set(v);
            };
            loaders.Add(() => show(get()));
            return row;
        }

        void TimeoutSlider(FlowLayoutPanel list)
        {
            const string tip = "A press held longer than this without scrolling sends no middle click, so you can change your mind " +
                               "about a scroll by just letting go. 0 = no limit.";
            var row = Row(list, "Time limit", tip);
            var bar = new NoWheelTrackBar { Minimum = TimeoutMin, Maximum = TimeoutNone, TickFrequency = 1, LargeChange = 1, TickStyle = TickStyle.None,
                                            AutoSize = false, Width = S(150), Height = S(28), Anchor = AnchorStyles.Left };
            var num = new NoWheelNumericUpDown { Minimum = 0, Maximum = 10000, Increment = 50, Width = S(60), Anchor = AnchorStyles.Left, TextAlign = HorizontalAlignment.Right };
            var info = new Label { AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = SystemColors.GrayText };
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            cell.Controls.Add(num);
            cell.Controls.Add(info);
            row.Controls.Add(bar, 1, 0);
            row.Controls.Add(cell, 2, 0);
            tips.SetToolTip(bar, tip);
            tips.SetToolTip(num, tip);
            Action<int> show = ms =>
            {
                bar.Value = ms == 0 ? TimeoutNone : Math.Max(TimeoutMin, Math.Min(TimeoutMax, (ms + 50) / 100));
                if (num.Value != ms) num.Value = ms;
                info.Text = ms == 0 ? "no limit" : ms == 600 ? "ms, default" : "ms";
            };
            bar.ValueChanged += delegate { if (loading) return; num.Value = bar.Value == TimeoutNone ? 0 : bar.Value * 100; };
            num.ValueChanged += delegate
            {
                int ms = (int)num.Value;
                bool was = loading;
                loading = true;
                try { show(ms); }
                finally { loading = was; }
                if (!was) ChangeProfile(s => s.ClickTimeout = ms);
            };
            loaders.Add(() => show(Profile().ClickTimeout));
        }

        /// One tick box per key: holding any ticked key with the middle button passes a native drag through.
        /// Native-drag shortcuts: tick boxes for the common single keys, plus a list of any other
        /// keys or key combinations (recorded by pressing them). Any one of them will do.
        void DragKeys(FlowLayoutPanel list)
        {
            const string tip = "Hold all the keys of any one shortcut while pressing the middle button to send a real middle-button drag, " +
                               "for rotating and panning in Blender, CAD and other 3D apps. None = off.";
            var row = Row(list, "Quick keys", tip);
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            var quickShortcuts = new List<int[]>();
            for (int i = 0; i < DragKeyLabels.Length; i++)
            {
                var single = Settings.ParseShortcuts(DragKeyLabels[i])[0];
                quickShortcuts.Add(single);
                var box = new CheckBox { Text = DragKeyLabels[i], AutoSize = true, Margin = new Padding(0, S(3), S(10), S(3)) };
                box.CheckedChanged += delegate
                {
                    if (loading) return;
                    bool on = box.Checked;
                    EditShortcuts(l => { l.RemoveAll(x => SameKeys(x, single)); if (on) l.Add(single); });
                };
                tips.SetToolTip(box, tip);
                loaders.Add(() => box.Checked = Profile().DragShortcuts().Exists(x => SameKeys(x, single)));
                cell.Controls.Add(box);
            }
            var quickSuffix = new Label { Text = "+ middle button", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, S(5), 0, 0) };
            cell.Controls.Add(quickSuffix);
            row.Controls.Add(cell, 1, 0);
            row.SetColumnSpan(cell, 2);

            var comboRow = Row(list, "Combos", "Two-key combos — great for no-middle-button drag (TEX Shura / 3D modeling) so single keys stay free.");
            var comboCell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            for (int i = 0; i < DragComboLabels.Length; i++)
            {
                var combo = Settings.ParseShortcuts(DragComboLabels[i])[0];
                quickShortcuts.Add(combo);
                var box = new CheckBox { Text = DragComboLabels[i], AutoSize = true, Margin = new Padding(0, S(3), S(10), S(3)) };
                box.CheckedChanged += delegate
                {
                    if (loading) return;
                    bool on = box.Checked;
                    EditShortcuts(l => { l.RemoveAll(x => SameKeys(x, combo)); if (on) l.Add(combo); });
                };
                tips.SetToolTip(box, "One-click combo shortcut — keeps single Alt/Ctrl/Shift keys free.");
                loaders.Add(() => box.Checked = Profile().DragShortcuts().Exists(x => SameKeys(x, combo)));
                comboCell.Controls.Add(box);
            }
            var comboSuffix = new Label { Text = "+ middle button", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, S(5), 0, 0) };
            comboCell.Controls.Add(comboSuffix);
            loaders.Add(() =>
            {
                bool needsMid = !Profile().KeyMoveToDrag;
                quickSuffix.Visible = comboSuffix.Visible = needsMid;
            });
            comboRow.Controls.Add(comboCell, 1, 0);
            comboRow.SetColumnSpan(comboCell, 2);

            // Every other shortcut, with Add (record) and Remove.
            var more = Row(list, "More shortcuts", tip);
            var box2 = new ListBox { Width = S(200), Height = S(70), IntegralHeight = false, Anchor = AnchorStyles.Left };
            var add = new Button { Text = "Add…", AutoSize = true, Margin = new Padding(S(6), 0, 0, S(4)) };
            var remove = new Button { Text = "Remove", AutoSize = true, Enabled = false, Margin = new Padding(S(6), 0, 0, 0) };
            var hint = new Label { AutoSize = true, MaximumSize = new Size(S(CardWidth - 36), 0), ForeColor = SystemColors.GrayText, Margin = new Padding(S(2), S(2), 0, S(4)) };
            var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
            buttons.Controls.Add(add);
            buttons.Controls.Add(remove);
            var cell2 = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            cell2.Controls.Add(box2);
            cell2.Controls.Add(buttons);
            more.Controls.Add(cell2, 1, 0);
            more.SetColumnSpan(cell2, 2);
            tips.SetToolTip(box2, tip);
            tips.SetToolTip(add, "Click, then hold the keys of the new shortcut together and let go. Esc cancels.");
            list.Controls.Add(hint);
            const string idleHint = "Add a key or combination, e.g. Space or Ctrl+Shift. Holding a normal key also types it in the app.";
            hint.Text = idleHint;

            var shown = new List<int[]>();
            loaders.Add(() =>
            {
                shown.Clear();
                box2.Items.Clear();
                foreach (var s in Profile().DragShortcuts())
                {
                    if (quickShortcuts.Exists(q => SameKeys(q, s))) continue;   // a tick box above
                    shown.Add(s);
                    box2.Items.Add(Settings.ShortcutText(s));
                }
                remove.Enabled = box2.SelectedIndex >= 0;
            });
            box2.SelectedIndexChanged += delegate { remove.Enabled = box2.SelectedIndex >= 0; };
            remove.Click += delegate
            {
                int i = box2.SelectedIndex;
                if (i < 0 || i >= shown.Count) return;
                var gone = shown[i];
                EditShortcuts(l => l.RemoveAll(x => SameKeys(x, gone)));
            };

            // Recording: watch the keyboard state (so Alt, Win and the side mouse buttons work too)
            // and take every key held at once; the shortcut is done when they're all let go.
            var rec = new Timer { Interval = 15 };
            var seen = new List<int>();
            int started = 0;
            Action stop = () => { rec.Stop(); add.Enabled = true; add.Text = "Add…"; hint.Text = idleHint; hint.ForeColor = SystemColors.GrayText; };
            add.Click += delegate
            {
                seen.Clear();
                started = Environment.TickCount;
                add.Enabled = false;            // also keeps Space/Enter from clicking it again
                add.Text = "Press keys…";
                hint.Text = "Hold the keys together, then let go. Esc cancels.";
                hint.ForeColor = SystemColors.HighlightText == SystemColors.Highlight ? SystemColors.ControlText : SystemColors.Highlight;
                rec.Start();
            };
            rec.Tick += delegate
            {
                var down = HeldKeys();
                foreach (int vk in down) if (!seen.Contains(vk)) seen.Add(vk);
                if (seen.Count > 0 && down.Count > 0)
                    hint.Text = Settings.ShortcutText(Settings.SortKeys(seen)) + "  — let go to add it";
                if (seen.Contains(0x1B) && seen.Count == 1 && down.Count == 0) { stop(); return; }   // Esc alone
                if (seen.Count > 0 && down.Count == 0)
                {
                    var keys = Settings.SortKeys(seen);
                    stop();
                    EditShortcuts(l => { if (!l.Exists(x => SameKeys(x, keys))) l.Add(keys); });
                    return;
                }
                if (Environment.TickCount - started > 15000) stop();
            };
            FormClosed += delegate { rec.Dispose(); };
        }

        /// Keys held right now, as shortcut key codes: one code per modifier (not left/right) and
        /// no left/right/middle mouse buttons.
        static List<int> HeldKeys()
        {
            var down = new List<int>();
            for (int vk = 1; vk < 255; vk++)
            {
                if (vk == 0x01 || vk == 0x02 || vk == 0x04) continue;          // mouse buttons the user clicks with
                if (vk >= 0xA0 && vk <= 0xA5) continue;                        // left/right Shift/Ctrl/Alt: the plain codes cover them
                if ((Native.GetAsyncKeyState(vk) & 0x8000) == 0) continue;
                int code = vk == 0x5C ? 0x5B : vk;                            // either Windows key
                if (!down.Contains(code)) down.Add(code);
            }
            return down;
        }

        static bool SameKeys(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;   // both sorted by SortKeys
            return true;
        }

        void EditShortcuts(Action<List<int[]>> edit)
        {
            var l = Profile().DragShortcuts();
            edit(l);
            var text = Settings.ShortcutsText(l);
            ChangeProfile(s => s.DragKeys = text);
            LoadValues();
        }

        // ---- Stickiness: a ratio slider with the named presets marked under it ----------------

        // The slider's stops, as ratios in tenths. Fine steps at the low end (where small changes
        // are easy to feel) and coarser ones above Firm keep the preset names well apart.
        static readonly int[] StickySteps = { 12, 13, 14, 15, 16, 17, 18, 19, 20, 22, 24, 26, 28, 30, 33, 36, 40, Settings.StickinessFixed };
        const int TBM_GETTHUMBRECT = 0x0400 + 25, TBM_GETCHANNELRECT = 0x0400 + 26;

        /// The stop nearest to a ratio (an exact match when it's one of the steps).
        static int StickyStop(int tenths)
        {
            if (tenths == Settings.StickinessFixed) return StickySteps.Length - 1;
            int best = 0;
            for (int i = 1; i < StickySteps.Length - 1; i++)
                if (Math.Abs(StickySteps[i] - tenths) < Math.Abs(StickySteps[best] - tenths)) best = i;
            return best;
        }

        static int StickyValue(int stop) { return StickySteps[Math.Max(0, Math.Min(StickySteps.Length - 1, stop))]; }

        Control StickinessSlider(FlowLayoutPanel list)
        {
            const string tip = "How hard it is to switch to the other direction during one press: the other direction has to be " +
                               "this many times stronger. Normal (2.0×) is the default. Fixed never switches; let go and press again, like the driver. Click a name to jump to it.";
            var row = Row(list, "Stickiness", tip);
            var bar = new NoWheelTrackBar
            {
                Minimum = 0, Maximum = StickySteps.Length - 1, SmallChange = 1, LargeChange = 3,
                TickStyle = TickStyle.None, AutoSize = false, Width = S(250), Height = S(28), Margin = new Padding(0, S(2), 0, 0)
            };
            var value = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(4), S(4), 0, 0) };
            var marks = new PresetMarks(bar, k) { Width = bar.Width, Height = S(26), MinimumSize = new Size(bar.Width, S(26)), Margin = Padding.Empty };
            var cell = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            cell.Controls.Add(bar, 0, 0);
            cell.Controls.Add(value, 1, 0);
            cell.Controls.Add(marks, 0, 1);
            row.Controls.Add(cell, 1, 0);
            row.SetColumnSpan(cell, 2);
            tips.SetToolTip(bar, tip);
            tips.SetToolTip(marks, tip);

            Action show = () =>
            {
                int v = StickyValue(bar.Value);
                value.Text = v == Settings.StickinessFixed ? "Fixed" : (v / 10.0).ToString("0.0") + "×";
                marks.Current = bar.Value;
            };
            bar.ValueChanged += delegate
            {
                show();
                if (loading) return;
                int v = StickyValue(bar.Value);
                ChangeProfile(s => s.Stickiness = v);
            };
            bar.Resize += delegate { marks.Invalidate(); };
            marks.Picked += stop => bar.Value = stop;
            loaders.Add(() => { bar.Value = StickyStop(Profile().Stickiness); show(); });
            return row;
        }

        /// Tick marks and preset names under the stickiness slider, each under its stop; the
        /// current one is highlighted, and clicking a name moves the slider there.
        sealed class PresetMarks : Control
        {
            readonly TrackBar bar;
            readonly float k;
            public event Action<int> Picked;
            int current;
            public int Current { get { return current; } set { current = value; Invalidate(); } }

            public PresetMarks(TrackBar bar, float k)
            {
                this.bar = bar;
                this.k = k;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
            }

            int Stop(int i) { return StickyStop(Settings.StickinessPresets[i]); }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            struct RECT { public int L, T, R, B; }
            [System.Runtime.InteropServices.DllImport("user32.dll")]
            static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref RECT lParam);

            /// The x of a slider stop: where the thumb's centre sits at that stop. The thumb travels
            /// along the channel, inset by half its width at each end.
            float X(int stop)
            {
                if (!bar.IsHandleCreated) return 0;
                RECT ch = new RECT(), th = new RECT();
                SendMessage(bar.Handle, TBM_GETCHANNELRECT, IntPtr.Zero, ref ch);
                SendMessage(bar.Handle, TBM_GETTHUMBRECT, IntPtr.Zero, ref th);
                float half = (th.R - th.L) / 2f;
                float left = ch.L + half, travel = (ch.R - ch.L) - 2 * half;
                return left + (stop - bar.Minimum) * travel / (bar.Maximum - bar.Minimum);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (var normal = new SolidBrush(Enabled ? Subtle : SystemColors.GrayText))
                using (var active = new SolidBrush(Enabled ? Color.FromArgb(0, 95, 184) : SystemColors.GrayText))
                using (var bold = new Font(Font, FontStyle.Bold))
                {
                    float tick = 5 * k, gap = 2 * k;
                    for (int i = 0; i < StickyLabels.Length; i++)
                    {
                        bool on = Stop(i) == current;
                        string text = StickyLabels[i];
                        var font = on ? bold : Font;
                        var size = g.MeasureString(text, font);
                        float cx = X(Stop(i));
                        using (var pen = new Pen(on ? active.Color : Color.FromArgb(160, 160, 166), Math.Max(1f, k)))
                            g.DrawLine(pen, cx, 0, cx, tick);
                        float x = Math.Max(0, Math.Min(Width - size.Width, cx - size.Width / 2f));
                        g.DrawString(text, font, on ? active : normal, x, tick + gap);
                    }
                }
            }

            protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

            protected override void OnMouseClick(MouseEventArgs e)
            {
                base.OnMouseClick(e);
                int best = 0;
                for (int i = 1; i < StickyLabels.Length; i++)
                    if (Math.Abs(X(Stop(i)) - e.X) < Math.Abs(X(Stop(best)) - e.X)) best = i;
                var h = Picked;
                if (h != null) h(Stop(best));
            }
        }

        /// Style and size pickers with a live preview of the indicator.
        Control IndicatorLook(FlowLayoutPanel list)
        {
            const string tip = "How the indicator looks. The preview shows it over a light and a dark background.";
            var row = Row(list, "Look", tip);
            var style = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(110), Anchor = AnchorStyles.Left };
            for (int i = 0; i < IndicatorArt.StyleLabels.Length; i++) style.Items.Add(IndicatorArt.StyleLabels[i] + (i == 0 ? " (default)" : ""));
            var preview = new IndicatorPreview(k) { Width = S(150), Height = S(76), Margin = new Padding(S(10), 0, 0, 0) };
            style.SelectedIndexChanged += delegate { if (!loading && style.SelectedIndex >= 0) { int v = style.SelectedIndex; ChangeProfile(s => s.IndicatorStyle = v); } };
            tips.SetToolTip(style, tip);
            tips.SetToolTip(preview, tip);
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
            cell.Controls.Add(style);
            cell.Controls.Add(preview);
            row.Controls.Add(cell, 1, 0);
            row.SetColumnSpan(cell, 2);
            loaders.Add(() =>
            {
                var p = Profile();
                style.SelectedIndex = Math.Max(0, Math.Min(style.Items.Count - 1, p.IndicatorStyle));
                preview.Set(p.IndicatorStyle, p.IndicatorSize, p.ScrollIndicator);
            });
            return row;
        }

        /// The indicator drawn half over white and half over a dark background, as it would look on screen.
        sealed class IndicatorPreview : Control
        {
            readonly float k;
            int style, size;
            bool on = true;

            public IndicatorPreview(float k)
            {
                this.k = k;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            }

            public void Set(int style, int size, bool on) { this.style = style; this.size = size; this.on = on; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int w = Width, h = Height;
                using (var light = new SolidBrush(Color.FromArgb(246, 246, 248))) g.FillRectangle(light, 0, 0, w / 2, h);
                using (var dark = new SolidBrush(Color.FromArgb(42, 43, 48))) g.FillRectangle(dark, w / 2, 0, w - w / 2, h);
                using (var border = new Pen(CardBorder)) g.DrawRectangle(border, 0, 0, w - 1, h - 1);
                float d = Math.Min(IndicatorArt.Diameter(size, k), h - 8);
                var r = new RectangleF((w - d) / 2f, (h - d) / 2f, d, d);
                var state = g.Save();
                if (!on) using (var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255))) { IndicatorArt.Draw(g, r, ScrollMode.Both, style); g.FillRectangle(dim, 0, 0, w, h); }
                else IndicatorArt.Draw(g, r, ScrollMode.Both, style);
                g.Restore(state);
            }
        }

        Control Choice(FlowLayoutPanel list, string text, string[] labels, Func<int> get, Action<int> set, string tip)
        {
            var row = Row(list, text, tip);
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200), Anchor = AnchorStyles.Left };
            combo.Items.AddRange(labels);
            combo.SelectedIndexChanged += delegate { if (!loading && combo.SelectedIndex >= 0) set(combo.SelectedIndex); };
            tips.SetToolTip(combo, tip);
            row.Controls.Add(combo, 1, 0);
            row.SetColumnSpan(combo, 2);
            loaders.Add(() => combo.SelectedIndex = Math.Max(0, Math.Min(labels.Length - 1, get())));
            return row;
        }
    }

    /// A scrollable grid that shows the wheel input it gets, middle clicks (and presses that sent
    /// none), and native middle drags passed through with the drag key.
    sealed class TestPad : Control
    {
        readonly Settings settings;
        readonly Func<bool> middleHeld;
        readonly Func<string> activeDeviceKey;
        readonly float k;
        readonly Timer timer = new Timer { Interval = 15 };

        internal static readonly Color Back = Color.FromArgb(250, 250, 251), Alt = Color.FromArgb(242, 243, 246), Grid = Color.FromArgb(222, 224, 230);
        internal static readonly Color Ink = Color.FromArgb(32, 33, 36), Muted = Color.FromArgb(110, 112, 120);
        internal static readonly Color Red = Color.FromArgb(220, 30, 40), Blue = Color.FromArgb(31, 111, 235), Amber = Color.FromArgb(200, 130, 0);

        // Content offset in pixels; wheel units turn into pixels like a typical app (3 lines per notch).
        double offX, offY;                 // what is drawn; glides toward the target like an app's smooth scrolling
        double targetX, targetY;           // where the wheel input says the content should be
        float PixelsPerUnit { get { int lines = SystemInformation.MouseWheelScrollLines; if (lines <= 0) lines = 3; return lines * 20f * k / 120f; } }

        // The current or last scroll gesture.
        struct Gesture { public int Start, Last, Events, UnitsX, UnitsY, MinStep; public bool Smooth; }
        Gesture g;
        bool gestureOpen;

        // Middle button as this window sees it, and as the engine sees it (it hides presses it uses).
        bool down;
        int downTick;
        Point downPt;
        readonly List<Point> path = new List<Point>();
        string dragKey;
        bool engineHeld;
        int engineDownTick, engineUpTick;
        bool checkPending;
        int lastClickTick, lastWheelSinceHold;
        Point flashPt;
        int flashTick = -100000;

        /// What's happening now (shown above the pad), and lines for the event list below it.
        public event Action<string, Color> StatusChanged;
        public event Action<string> Logged;

        public TestPad(Settings settings, Func<bool> middleHeld, Func<string> activeDeviceKey, float k)
        {
            this.settings = settings;
            this.middleHeld = middleHeld;
            this.activeDeviceKey = activeDeviceKey;
            this.k = k;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Font = SystemFonts.MessageBoxFont;
            Cursor = Cursors.Cross;
            timer.Tick += delegate { Tick(); };
            timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        int S(float px) { return (int)Math.Round(px * k); }

        void AddLog(string line) { var h = Logged; if (h != null) h(line); }

        // The status line changes with every wheel event while scrolling; updating the label that
        // often makes the card repaint constantly, so Tick passes it on at most this often.
        const int StatusInterval = 100;
        string statusText, sentText;
        Color statusColor;
        int sentTick;

        void SetStatus(string text, Color color) { statusText = text; statusColor = color; }

        void FlushStatus(int now)
        {
            if (statusText == null || statusText == sentText || now - sentTick < StatusInterval) return;
            sentText = statusText;
            sentTick = now;
            var h = StatusChanged;
            if (h != null) h(statusText, statusColor);
        }

        /// Smooth scrolling like a browser: the drawn grid closes most of the gap to where the
        /// wheel input put it within ~100 ms, so uneven event timing doesn't make it jump.
        int lastAnimTick;

        void Animate(int now)
        {
            double dt = Math.Max(1, Math.Min(100, now - lastAnimTick));
            lastAnimTick = now;
            double dx = targetX - offX, dy = targetY - offY;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            {
                if (dx != 0 || dy != 0) { offX = targetX; offY = targetY; Invalidate(); }
                return;
            }
            double f = 1 - Math.Exp(-dt / 45.0);
            offX += dx * f;
            offY += dy * f;
            Invalidate();
        }

        // ---- Input ----------------------------------------------------------------------------

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEWHEEL || m.Msg == Native.WM_MOUSEHWHEEL)
            {
                OnWheel((short)((m.WParam.ToInt64() >> 16) & 0xFFFF), m.Msg == Native.WM_MOUSEHWHEEL);
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public void OnWheel(int delta, bool horizontal)
        {
            int now = Environment.TickCount;
            if (!gestureOpen || now - g.Last > 300)
            {
                if (gestureOpen) LogGesture();
                g = new Gesture { Start = now, MinStep = int.MaxValue };   // the real indicator (if on) shows direction
                gestureOpen = true;
            }
            g.Last = now;
            g.Events++;
            int step = Math.Abs(delta);
            if (step > 0 && step < g.MinStep) g.MinStep = step;
            if (step % 120 != 0) g.Smooth = true;
            // Wheel up / tilt left move the content down / right, as in any app.
            if (horizontal) { g.UnitsX += delta; targetX -= delta * PixelsPerUnit; }
            else { g.UnitsY += delta; targetY += delta * PixelsPerUnit; }
            lastWheelSinceHold = now;
            SetStatus(Describe(g, true), Ink);   // shown at most every StatusInterval ms; Tick moves the grid
        }

        static string Dir(int x, int y)
        {
            if (x == 0 && y == 0) return "";
            if (Math.Abs(y) >= Math.Abs(x)) return y > 0 ? "up" : "down";
            return x > 0 ? "right" : "left";
        }

        string Describe(Gesture e, bool live)
        {
            string axes = e.UnitsX != 0 && e.UnitsY != 0 ? "both axes" : e.UnitsX != 0 ? "horizontal" : "vertical";
            string what = (live ? "Scrolling " : "Scrolled ") + Dir(e.UnitsX, e.UnitsY) + " (" + axes + ")";
            string units = e.UnitsY != 0 && e.UnitsX != 0
                ? string.Format("{0} vertical / {1} horizontal units", Math.Abs(e.UnitsY), Math.Abs(e.UnitsX))
                : string.Format("{0} units = {1:0.#} notches", Math.Abs(e.UnitsY + e.UnitsX), Math.Abs(e.UnitsY + e.UnitsX) / 120.0);
            string kind = e.Smooth ? "smooth" : "whole notches";
            return string.Format("{0}: {1} events, {2}, {3}", what, e.Events, units, kind);
        }

        void LogGesture()
        {
            gestureOpen = false;
            int ms = Math.Max(1, g.Last - g.Start);
            AddLog(Describe(g, false) + string.Format(", {0} ms", ms));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Middle) return;
            down = true;
            downTick = Environment.TickCount;
            downPt = e.Location;
            path.Clear();
            path.Add(e.Location);
            var active = settings.EffectiveProfileFor(activeDeviceKey());
            var held = ScrollEngine.HeldShortcut(active.DragShortcuts().ToArray());
            dragKey = held != null ? Settings.ShortcutText(held) : null;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!down) return;
            path.Add(e.Location);
            if (path.Count > 4000) path.RemoveAt(0);
            int dx = e.X - downPt.X, dy = e.Y - downPt.Y;
            SetStatus(string.Format("Native middle drag{0} reaching this window: {1}, {2} px",
                                    dragKey != null ? " (" + dragKey + ")" : "", dx, dy), Blue);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Middle || !down) return;
            down = false;
            int held = Environment.TickCount - downTick;
            lastClickTick = Environment.TickCount;
            double dist = Math.Sqrt(Math.Pow(e.X - downPt.X, 2) + Math.Pow(e.Y - downPt.Y, 2));
            if (dist > 4 * k)
            {
                string msg = string.Format("Native middle drag passed through{0}: moved {1}, {2} px",
                                           dragKey != null ? " with " + dragKey : "", e.X - downPt.X, e.Y - downPt.Y);
                SetStatus(msg, Blue);
                AddLog(msg);
            }
            else
            {
                path.Clear();
                flashPt = e.Location;
                flashTick = Environment.TickCount;
                // A click sent by NubMouse arrives as one quick down/up after the button was
                // released, so the hold time is only meaningful when the press passed straight through.
                string msg = dragKey != null
                    ? string.Format("Middle click passed through with {0} (held {1} ms)", dragKey, held)
                    : "Middle click";
                SetStatus(msg, Red);
                AddLog(msg);
            }
            Invalidate();
        }

        // ---- Timer: engine state, presses that sent nothing, animation -----------------------

        void Tick()
        {
            int now = Environment.TickCount;
            bool held = middleHeld();
            bool over = ClientRectangle.Contains(PointToClient(Cursor.Position));
            if (held && !engineHeld)
            {
                engineHeld = true;
                engineDownTick = now;
                lastWheelSinceHold = 0;
                checkPending = false;
                if (over) { SetStatus("Middle button held: push the TrackPoint to scroll, or let go to click", Muted); Invalidate(); }
            }
            else if (!held && engineHeld)
            {
                engineHeld = false;
                engineUpTick = now;
                checkPending = over;
            }
            // The click (if any) arrives just after release; if nothing came, say why.
            if (checkPending && now - engineUpTick > 200)
            {
                checkPending = false;
                bool clicked = lastClickTick - engineDownTick >= 0;
                bool scrolled = lastWheelSinceHold != 0;
                if (!clicked && !scrolled)
                {
                    int heldMs = engineUpTick - engineDownTick;
                    var active = settings.EffectiveProfileFor(activeDeviceKey());
                    string why = active.ClickTimeout > 0 && heldMs >= active.ClickTimeout
                        ? string.Format("held {0} ms, past the {1} ms time limit", heldMs, active.ClickTimeout)
                        : string.Format("pushed past the {0} px deadzone, so it counted as a scroll", active.Deadzone);
                    string msg = "No middle click sent: " + why;
                    SetStatus(msg, Amber);
                    AddLog(msg);
                    Invalidate();
                }
            }
            if (gestureOpen && now - g.Last > 300)
            {
                LogGesture();
                SetStatus(Describe(g, false), Ink);
                Invalidate();
            }
            if (now - flashTick < 700) Invalidate();
            Animate(now);
            FlushStatus(now);
        }

        // ---- Drawing --------------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            var gr = e.Graphics;
            gr.Clear(Back);
            int cell = S(48);
            int w = ClientSize.Width, h = ClientSize.Height;

            // Grid that moves with the scroll offset, with coordinates so movement is easy to read.
            int ox = (int)Math.Floor(offX), oy = (int)Math.Floor(offY);
            int firstCol = (int)Math.Floor(-ox / (double)cell), firstRow = (int)Math.Floor(-oy / (double)cell);
            using (var alt = new SolidBrush(Alt))
            using (var line = new Pen(Grid))
            using (var label = new SolidBrush(Muted))
            using (var small = new Font(Font.FontFamily, Font.Size * 0.8f))
            {
                for (int r = firstRow; r * cell + oy < h; r++)
                    for (int c = firstCol; c * cell + ox < w; c++)
                    {
                        int x = c * cell + ox, y = r * cell + oy;
                        if (((r + c) & 1) != 0) gr.FillRectangle(alt, x, y, cell, cell);
                        if (((r % 4) + 4) % 4 == 0 && ((c % 4) + 4) % 4 == 0)
                            gr.DrawString(c + "," + r, small, label, x + S(3), y + S(3));
                    }
                for (int c = firstCol; c * cell + ox < w; c++) gr.DrawLine(line, c * cell + ox, 0, c * cell + ox, h);
                for (int r = firstRow; r * cell + oy < h; r++) gr.DrawLine(line, 0, r * cell + oy, w, r * cell + oy);
            }
            gr.SmoothingMode = SmoothingMode.AntiAlias;

            // Native drag path.
            if (path.Count > 1)
            {
                using (var pen = new Pen(Blue, S(3)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                    gr.DrawLines(pen, path.ToArray());
                using (var dot = new SolidBrush(Blue))
                    gr.FillEllipse(dot, path[0].X - S(5), path[0].Y - S(5), S(10), S(10));
            }

            // Click ripple.
            int age = Environment.TickCount - flashTick;
            if (age >= 0 && age < 700)
            {
                float t = age / 700f, r = S(10) + t * S(40);
                using (var pen = new Pen(Color.FromArgb((int)(255 * (1 - t)), Red), S(3)))
                    gr.DrawEllipse(pen, flashPt.X - r, flashPt.Y - r, 2 * r, 2 * r);
            }
            gr.SmoothingMode = SmoothingMode.None;

            using (var border = new Pen(Grid))
                gr.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }
    }

    /// TrackBar that ignores mouse-wheel events completely and forwards them to its scrollable page.
    sealed class NoWheelTrackBar : TrackBar
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEWHEEL || m.Msg == Native.WM_MOUSEHWHEEL)
            {
                ForwardWheelToScrollParent(this, ref m);
                return;
            }
            base.WndProc(ref m);
        }

        internal static void ForwardWheelToScrollParent(Control c, ref Message m)
        {
            for (var p = c.Parent; p != null; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll && sc.IsHandleCreated)
                {
                    Native.SendMessage(sc.Handle, m.Msg, m.WParam, m.LParam);
                    return;
                }
            }
        }
    }

    /// NumericUpDown that ignores mouse-wheel events and forwards them to its scrollable page.
    sealed class NoWheelNumericUpDown : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var hm = e as HandledMouseEventArgs;
            if (hm != null) hm.Handled = true;
            var msg = Message.Create(Handle, Native.WM_MOUSEWHEEL, (IntPtr)((e.Delta << 16) & 0xFFFF0000), IntPtr.Zero);
            NoWheelTrackBar.ForwardWheelToScrollParent(this, ref msg);
        }
    }
}
