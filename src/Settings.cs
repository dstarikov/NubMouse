// User settings, stored under HKCU\Software\NubMouse — or in NubMouse.ini next to the
// exe if that file exists (portable mode).
// Command-line options override them for one run without changing what is stored.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace NubMouse
{
    enum DeviceMode { Full = 0, DragOnly = 1, Ignore = 2 }

    sealed class Settings
    {
        const string KeyPath = @"Software\NubMouse";

        public bool Enabled = true;          // false = "normal mode": hook removed, middle button untouched
        public bool MiddleButtonScroll = true; // false = leave middle button untouched (e.g. TEX Shura using only 3D/CAD drag shortcuts)
        public int Speed = 4;                // wheel units per pixel of TrackPoint movement (vertical, and horizontal when HorizontalSpeed == 0)
        public int HorizontalSpeed;          // 0 = same as Speed; 1-100 = independent horizontal speed
        public int Deadzone = 3;             // pixels of travel before a press counts as a scroll
        public int ClickTimeout = 600;       // ms; a longer press without scrolling sends no click (0 = no limit)
        public bool Notched;                 // send whole 120-unit notches only
        public bool Natural;                 // reverse both directions
        public bool AxisLock = true;         // scroll along one axis at a time, like the TrackPoint driver
        public int Stickiness = 20;          // how hard the axis lock is to switch: ratio in tenths (see StickinessRatio), 0 = fixed
        public bool ScrollIndicator;         // show a directional overlay near the cursor while scrolling (off by default)
        public int IndicatorOffset;          // advanced: px (at 100 % scaling) from the pointer to the indicator; 0 = on the pointer, which it hides
        public int IndicatorStyle;           // index into IndicatorStyleNames
        public int IndicatorSize = 34;       // indicator diameter in px at 100 % scaling, IndicatorSizeMin-IndicatorSizeMax

        public static readonly string[] IndicatorStyleNames = { "dark", "light", "minimal", "trackpoint" };
        public const int IndicatorSizeMin = 16, IndicatorSizeMax = 64;
        /// Named sizes (the tray menu and indicatorsize=NAME); any size in between works too.
        public static readonly string[] IndicatorSizeNames = { "tiny", "small", "medium", "large" };
        public static readonly int[] IndicatorSizePresets = { 20, 26, 34, 46 };

        /// "medium" or a number of px; -1 if neither.
        public static int ParseIndicatorSize(string text)
        {
            int i = Array.IndexOf(IndicatorSizeNames, (text ?? "").Trim().ToLowerInvariant()), n;
            if (i >= 0) return IndicatorSizePresets[i];
            if (int.TryParse(text, out n) && n >= IndicatorSizeMin && n <= IndicatorSizeMax) return n;
            return -1;
        }
        public string DragKeys = "";         // native-drag shortcuts: "Alt, Ctrl+Shift" = hold Alt, or Ctrl and Shift together; "" = off
        public bool KeyMoveToDrag;           // hold a drag shortcut + move pointer (no middle button) for smooth 360° middle-drag
        public bool WheelToDrag;             // convert hardware wheel + WheelDragKeys into synthetic middle-drag (TEX Shura M_FN)
        public string WheelDragKeys = "";    // empty = same shortcuts as DragKeys; otherwise separate shortcut(s) for WheelToDrag
        public string DeviceFilter = "";     // empty = all pointing devices; comma-separated device keys (e.g. "17EF:6009")
        public string DeviceModes = "";      // per-device overrides: "04D9:0532=dragonly;046D:C547=ignore"
        public bool DevicesInitialized;      // true once first-run device auto-detection or user device config has run
        public Dictionary<string, Settings> DeviceProfiles = new Dictionary<string, Settings>(StringComparer.OrdinalIgnoreCase);

        // Stickiness: how much more the other axis must move (recently) than the locked one to
        // switch to it, in tenths (20 = 2.0x). 0 = fixed: never switches, like the driver.
        public const int StickinessMin = 12, StickinessMax = 40, StickinessFixed = 0;
        /// Named levels, loosest first, and their values.
        public static readonly string[] StickinessNames = { "loose", "normal", "firm", "fixed" };
        public static readonly int[] StickinessPresets = { 15, 20, 30, StickinessFixed };

        public static float StickinessRatio(int tenths) { return tenths <= 0 ? float.PositiveInfinity : tenths / 10f; }

        /// "normal", or the ratio such as "2.4".
        public static string StickinessText(int tenths)
        {
            int i = Array.IndexOf(StickinessPresets, tenths);
            return i >= 0 ? StickinessNames[i] : (tenths / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// A level name or a ratio from 1.2 to 4.0; -1 if neither.
        public static int ParseStickiness(string text)
        {
            var t = (text ?? "").Trim().ToLowerInvariant();
            int i = Array.IndexOf(StickinessNames, t);
            if (i >= 0) return StickinessPresets[i];
            double r;
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r))
            {
                int tenths = (int)Math.Round(r * 10);
                if (tenths >= StickinessMin && tenths <= StickinessMax) return tenths;
            }
            return -1;
        }
        // ---- Native-drag shortcuts -------------------------------------------------------
        // Each shortcut is one or more keys that must all be held while pressing the middle button
        // (e.g. Alt, or Ctrl+Shift, or Space); any shortcut will do. Written "Alt, Ctrl+Shift".
        // Keys are virtual-key codes; modifiers use the either-side codes (VK_MENU, not VK_LMENU).

        const int VkShift = 0x10, VkCtrl = 0x11, VkAlt = 0x12, VkLWin = 0x5B, VkRWin = 0x5C;
        static readonly int[] ModifierOrder = { VkCtrl, VkAlt, VkShift, VkLWin };

        /// A readable name for a virtual-key code ("Alt", "Space", "Z", "5", "XButton1").
        public static string KeyName(int vk)
        {
            switch (vk)
            {
                case VkShift: return "Shift";
                case VkCtrl: return "Ctrl";
                case VkAlt: return "Alt";
                case VkLWin: case VkRWin: return "Win";
            }
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            return ((System.Windows.Forms.Keys)vk).ToString();
        }

        /// Parses one key name (case-insensitive). Returns 0 if it isn't one.
        public static int ParseKey(string text)
        {
            var t = (text ?? "").Trim();
            switch (t.ToLowerInvariant())
            {
                case "": return 0;
                case "shift": return VkShift;
                case "ctrl": case "control": return VkCtrl;
                case "alt": return VkAlt;
                case "win": case "windows": return VkLWin;
            }
            if (t.Length == 1 && t[0] >= '0' && t[0] <= '9') return t[0];
            int n;
            if (int.TryParse(t, out n)) return 0;   // a bare number would be read as a key code
            System.Windows.Forms.Keys key;
            if (Enum.TryParse(t, true, out key) && (int)key > 0 && (int)key < 256) return (int)key;
            return 0;
        }

        /// Puts a shortcut's keys in the usual order: Ctrl, Alt, Shift, Win, then the rest.
        public static int[] SortKeys(IEnumerable<int> keys)
        {
            var list = new List<int>();
            foreach (var k in keys) if (!list.Contains(k)) list.Add(k);
            list.Sort((a, b) =>
            {
                int ia = Array.IndexOf(ModifierOrder, a == VkRWin ? VkLWin : a), ib = Array.IndexOf(ModifierOrder, b == VkRWin ? VkLWin : b);
                if (ia < 0) ia = 100;
                if (ib < 0) ib = 100;
                return ia != ib ? ia.CompareTo(ib) : a.CompareTo(b);
            });
            return list.ToArray();
        }

        public static string ShortcutText(int[] keys)
        {
            var names = new string[keys.Length];
            for (int i = 0; i < keys.Length; i++) names[i] = KeyName(keys[i]);
            return string.Join("+", names);
        }

        /// Parses "Alt, Ctrl+Shift". "off"/"none"/"" = no shortcuts. Returns null if a key name is unknown.
        public static List<int[]> ParseShortcuts(string text)
        {
            var result = new List<int[]>();
            var t = (text ?? "").Trim();
            if (t.Length == 0 || t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase))
                return result;
            foreach (var shortcut in t.Split(',', ';'))
            {
                if (shortcut.Trim().Length == 0) continue;
                var keys = new List<int>();
                foreach (var name in shortcut.Split('+'))
                {
                    int vk = ParseKey(name);
                    if (vk == 0) return null;
                    keys.Add(vk);
                }
                result.Add(SortKeys(keys));
            }
            return result;
        }

        public static string ShortcutsText(List<int[]> shortcuts)
        {
            var parts = new List<string>();
            foreach (var s in shortcuts)
            {
                var text = ShortcutText(s);
                if (!parts.Contains(text)) parts.Add(text);
            }
            return string.Join(", ", parts.ToArray());
        }

        /// This setting's shortcuts (an unreadable value counts as none).
        public List<int[]> DragShortcuts() { return ParseShortcuts(DragKeys) ?? new List<int[]>(); }
        public List<int[]> WheelDragShortcuts() { return ParseShortcuts(WheelDragKeys) ?? new List<int[]>(); }

        /// The older settings kept one key as an index (1 = Alt, 2 = Ctrl, 3 = Shift).
        static readonly string[] OldDragKeyNames = { "", "Alt", "Ctrl", "Shift" };

        public bool CancelAutoscroll = true; // undo apps' click-to-autoscroll after a lone middle click
        public bool HideTrayIcon;            // run with no notification-area icon
        public bool DriverWarningShown;      // "driver is still scrolling" hint already shown once
        public bool SwitchDriver = true;     // ELAN: set the driver's middle button to Middle Click while on
        public bool ScrollWhenPaused = true; // ELAN: switch it back to Scrolling when paused or exiting
        public bool RestoreDriverScroll;     // ELAN: we switched it from Scrolling; put that back when paused/exiting
        public bool CheckForUpdates;         // look for a new GitHub release once a day
        public bool AutoInstallUpdates;      // install found updates without asking (per-user installs)
        public bool RunAsAdmin;              // start elevated through a scheduled task (see AdminMode)
        public long LastUpdateCheck;         // UTC ticks of the last automatic check
        public bool Debug;                   // write a diagnostic log (this run only, never saved)

        public int EffectiveHorizontalSpeed { get { return HorizontalSpeed > 0 ? HorizontalSpeed : Speed; } }

        /// Path to the side-by-side portable config file. When this file exists next to the exe,
        /// settings are loaded from and saved to it instead of the registry.
        public static string PortableIniPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Installer.CurrentExe) ?? "", Installer.AppName + ".ini"); }
        }

        public static bool UsesPortableIni
        {
            get { try { return File.Exists(PortableIniPath); } catch (Exception) { return false; } }
        }

        public Settings Clone()
        {
            var c = (Settings)MemberwiseClone();
            c.DeviceProfiles = new Dictionary<string, Settings>(StringComparer.OrdinalIgnoreCase);
            if (DeviceProfiles != null)
                foreach (var kv in DeviceProfiles)
                    if (kv.Value != null) c.DeviceProfiles[kv.Key] = (Settings)kv.Value.MemberwiseClone();
            return c;
        }

        public bool IsFirmwareScrollOrDragOnly(string devKey)
        {
            if (string.IsNullOrEmpty(devKey)) return false;
            if (RawInputDevices.IsFirmwareScrollDevice(devKey)) return true;
            DeviceMode m;
            return ParsedDeviceModes().TryGetValue(devKey, out m) && m == DeviceMode.DragOnly;
        }

        public Settings GetOrCreateProfile(string devKey)
        {
            Settings p;
            if (!DeviceProfiles.TryGetValue(devKey, out p) || p == null)
            {
                p = EffectiveProfileFor(devKey);
                if (p == this)
                {
                    p = (Settings)MemberwiseClone();
                    p.DeviceProfiles = new Dictionary<string, Settings>(StringComparer.OrdinalIgnoreCase);
                }
                DeviceProfiles[devKey] = p;
            }
            return p;
        }

        public Settings ProfileFor(string devKey)
        {
            Settings p;
            if (!string.IsNullOrEmpty(devKey) && DeviceProfiles != null && DeviceProfiles.TryGetValue(devKey, out p) && p != null)
                return p;
            return this;
        }

        /// Returns the device's custom profile if one exists, otherwise the default settings for
        /// that device (TEX / firmware-scroll devices default to MiddleButtonScroll = false and KeyMoveToDrag = true).
        public Settings EffectiveProfileFor(string devKey)
        {
            Settings p;
            if (!string.IsNullOrEmpty(devKey) && DeviceProfiles != null && DeviceProfiles.TryGetValue(devKey, out p) && p != null)
                return p;
            if (IsFirmwareScrollOrDragOnly(devKey))
            {
                var tex = (Settings)MemberwiseClone();
                tex.DeviceProfiles = new Dictionary<string, Settings>(StringComparer.OrdinalIgnoreCase);
                tex.MiddleButtonScroll = false;
                tex.KeyMoveToDrag = true;
                if (tex.DragShortcuts().Count == 0) tex.DragKeys = "Ctrl+Alt";
                return tex;
            }
            return this;
        }

        public static Settings Load()
        {
            var s = new Settings();
            if (UsesPortableIni)
            {
                LoadIni(s, PortableIniPath);
                return s;
            }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return s;
                    s.Enabled = ReadBool(k, "Enabled", s.Enabled);
                    s.MiddleButtonScroll = ReadBool(k, "MiddleButtonScroll", s.MiddleButtonScroll);
                    s.Speed = Clamp(ReadInt(k, "Speed", s.Speed), 1, 100);
                    s.HorizontalSpeed = Clamp(ReadInt(k, "HorizontalSpeed", s.HorizontalSpeed), 0, 100);
                    s.Deadzone = Clamp(ReadInt(k, "Deadzone", s.Deadzone), 0, 100);
                    s.ClickTimeout = Clamp(ReadInt(k, "ClickTimeout", s.ClickTimeout), 0, 10000);
                    s.Notched = ReadBool(k, "Notched", s.Notched);
                    s.Natural = ReadBool(k, "Natural", s.Natural);
                    s.AxisLock = ReadBool(k, "AxisLock", s.AxisLock);
                    // Older versions stored a level index (0-3 = loose, normal, firm, fixed).
                    int oldLevel = ReadInt(k, "Stickiness", -1);
                    int sticky = ReadInt(k, "StickinessRatio", oldLevel >= 0 && oldLevel < StickinessPresets.Length ? StickinessPresets[oldLevel] : s.Stickiness);
                    s.Stickiness = sticky <= 0 ? StickinessFixed : Clamp(sticky, StickinessMin, StickinessMax);
                    s.ScrollIndicator = ReadBool(k, "ScrollIndicator", s.ScrollIndicator);
                    s.IndicatorOffset = Clamp(ReadInt(k, "IndicatorOffset", s.IndicatorOffset), 0, 100);
                    s.IndicatorStyle = Clamp(ReadInt(k, "IndicatorStyle", s.IndicatorStyle), 0, IndicatorStyleNames.Length - 1);
                    // Earlier test builds stored a size index (0-2 = small, medium, large).
                    int oldSize = ReadInt(k, "IndicatorSize", -1);
                    int size = ReadInt(k, "IndicatorDiameter", oldSize >= 0 && oldSize <= 2 ? IndicatorSizePresets[oldSize + 1] : s.IndicatorSize);
                    s.IndicatorSize = Clamp(size, IndicatorSizeMin, IndicatorSizeMax);
                    var dragKeys = k.GetValue("DragKeys") as string;
                    if (dragKeys != null && ParseShortcuts(dragKeys) != null) s.DragKeys = dragKeys;
                    else if (dragKeys == null)
                    {
                        // Test builds kept a bit mask (1 = Alt, 2 = Ctrl, 4 = Shift); older versions one key as an index.
                        int mask = ReadInt(k, "DragModifiers", -1);
                        if (mask < 0) { int old = Clamp(ReadInt(k, "DragModifier", 0), 0, OldDragKeyNames.Length - 1); mask = old == 0 ? 0 : 1 << (old - 1); }
                        var names = new List<string>();
                        for (int i = 0; i < 3; i++) if ((mask & (1 << i)) != 0) names.Add(OldDragKeyNames[i + 1]);
                        s.DragKeys = string.Join(", ", names.ToArray());
                    }
                    s.KeyMoveToDrag = ReadBool(k, "KeyMoveToDrag", s.KeyMoveToDrag);
                    s.WheelToDrag = ReadBool(k, "WheelToDrag", s.WheelToDrag);
                    var wheelKeys = k.GetValue("WheelDragKeys") as string;
                    if (wheelKeys != null && ParseShortcuts(wheelKeys) != null) s.WheelDragKeys = wheelKeys;
                    s.DeviceFilter = (k.GetValue("DeviceFilter") as string) ?? "";
                    s.DeviceModes = (k.GetValue("DeviceModes") as string) ?? "";
                    s.DevicesInitialized = ReadBool(k, "DevicesInitialized", !string.IsNullOrEmpty(s.DeviceModes) || !string.IsNullOrEmpty(s.DeviceFilter));
                    s.CancelAutoscroll = ReadBool(k, "CancelAutoscroll", s.CancelAutoscroll);
                    s.HideTrayIcon = ReadBool(k, "HideTrayIcon", s.HideTrayIcon);
                    s.DriverWarningShown = ReadBool(k, "DriverWarningShown", s.DriverWarningShown);
                    s.SwitchDriver = ReadBool(k, "SwitchDriver", s.SwitchDriver);
                    s.ScrollWhenPaused = ReadBool(k, "ScrollWhenPaused", s.ScrollWhenPaused);
                    s.RestoreDriverScroll = ReadBool(k, "RestoreDriverScroll", s.RestoreDriverScroll);
                    s.CheckForUpdates = ReadBool(k, "CheckForUpdates", s.CheckForUpdates);
                    s.AutoInstallUpdates = ReadBool(k, "AutoInstallUpdates", s.AutoInstallUpdates);
                    s.RunAsAdmin = ReadBool(k, "RunAsAdmin", s.RunAsAdmin);
                    object t = k.GetValue("LastUpdateCheck");
                    if (t is long) s.LastUpdateCheck = (long)t;
                    LoadProfilesFromRegistry(s, k);
                }
            }
            catch (Exception) { /* unreadable settings: fall back to defaults */ }
            return s;
        }

        static void LoadProfilesFromRegistry(Settings s, RegistryKey root)
        {
            try
            {
                using (var devs = root.OpenSubKey("Profiles"))
                {
                    if (devs == null) return;
                    foreach (var sub in devs.GetSubKeyNames())
                    {
                        using (var dk = devs.OpenSubKey(sub))
                        {
                            if (dk == null) continue;
                            string key = (dk.GetValue("Key") as string) ?? sub.Replace('~', '\\');
                            var p = (Settings)s.MemberwiseClone();
                            p.DeviceProfiles = new Dictionary<string, Settings>(StringComparer.OrdinalIgnoreCase);
                            ReadTuningFromRegistry(p, dk);
                            s.DeviceProfiles[key] = p;
                        }
                    }
                }
            }
            catch (Exception) { }
        }

        static void ReadTuningFromRegistry(Settings s, RegistryKey k)
        {
            s.MiddleButtonScroll = ReadBool(k, "MiddleButtonScroll", s.MiddleButtonScroll);
            s.Speed = Clamp(ReadInt(k, "Speed", s.Speed), 1, 100);
            s.HorizontalSpeed = Clamp(ReadInt(k, "HorizontalSpeed", s.HorizontalSpeed), 0, 100);
            s.Deadzone = Clamp(ReadInt(k, "Deadzone", s.Deadzone), 0, 100);
            s.ClickTimeout = Clamp(ReadInt(k, "ClickTimeout", s.ClickTimeout), 0, 10000);
            s.Notched = ReadBool(k, "Notched", s.Notched);
            s.Natural = ReadBool(k, "Natural", s.Natural);
            s.AxisLock = ReadBool(k, "AxisLock", s.AxisLock);
            object ratio = k.GetValue("StickinessRatio");
            if (ratio is int) s.Stickiness = ClampStickiness((int)ratio);
            s.ScrollIndicator = ReadBool(k, "ScrollIndicator", s.ScrollIndicator);
            s.IndicatorOffset = Clamp(ReadInt(k, "IndicatorOffset", s.IndicatorOffset), 0, 100);
            s.IndicatorStyle = Clamp(ReadInt(k, "IndicatorStyle", s.IndicatorStyle), 0, IndicatorStyleNames.Length - 1);
            object diam = k.GetValue("IndicatorDiameter");
            if (diam is int) s.IndicatorSize = Clamp((int)diam, IndicatorSizeMin, IndicatorSizeMax);
            var dragKeys = k.GetValue("DragKeys") as string;
            if (dragKeys != null && ParseShortcuts(dragKeys) != null) s.DragKeys = dragKeys;
            s.KeyMoveToDrag = ReadBool(k, "KeyMoveToDrag", s.KeyMoveToDrag);
            s.WheelToDrag = ReadBool(k, "WheelToDrag", s.WheelToDrag);
            var wheelKeys = k.GetValue("WheelDragKeys") as string;
            if (wheelKeys != null && ParseShortcuts(wheelKeys) != null) s.WheelDragKeys = wheelKeys;
            s.CancelAutoscroll = ReadBool(k, "CancelAutoscroll", s.CancelAutoscroll);
        }

        static void WriteTuningToRegistry(Settings s, RegistryKey k)
        {
            k.SetValue("MiddleButtonScroll", s.MiddleButtonScroll ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("Speed", s.Speed, RegistryValueKind.DWord);
            k.SetValue("HorizontalSpeed", s.HorizontalSpeed, RegistryValueKind.DWord);
            k.SetValue("Deadzone", s.Deadzone, RegistryValueKind.DWord);
            k.SetValue("ClickTimeout", s.ClickTimeout, RegistryValueKind.DWord);
            k.SetValue("Notched", s.Notched ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("Natural", s.Natural ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("AxisLock", s.AxisLock ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("StickinessRatio", s.Stickiness, RegistryValueKind.DWord);
            k.SetValue("ScrollIndicator", s.ScrollIndicator ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("IndicatorOffset", s.IndicatorOffset, RegistryValueKind.DWord);
            k.SetValue("IndicatorStyle", s.IndicatorStyle, RegistryValueKind.DWord);
            k.SetValue("IndicatorDiameter", s.IndicatorSize, RegistryValueKind.DWord);
            k.SetValue("DragKeys", s.DragKeys ?? "", RegistryValueKind.String);
            k.SetValue("KeyMoveToDrag", s.KeyMoveToDrag ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("WheelToDrag", s.WheelToDrag ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("WheelDragKeys", s.WheelDragKeys ?? "", RegistryValueKind.String);
            k.SetValue("CancelAutoscroll", s.CancelAutoscroll ? 1 : 0, RegistryValueKind.DWord);
        }

        public void Save()
        {
            if (UsesPortableIni)
            {
                SaveIni(PortableIniPath);
                return;
            }
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    k.SetValue("Enabled", Enabled ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("MiddleButtonScroll", MiddleButtonScroll ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Speed", Speed, RegistryValueKind.DWord);
                    k.SetValue("HorizontalSpeed", HorizontalSpeed, RegistryValueKind.DWord);
                    k.SetValue("Deadzone", Deadzone, RegistryValueKind.DWord);
                    k.SetValue("ClickTimeout", ClickTimeout, RegistryValueKind.DWord);
                    k.SetValue("Notched", Notched ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("Natural", Natural ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AxisLock", AxisLock ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("StickinessRatio", Stickiness, RegistryValueKind.DWord);
                    k.DeleteValue("Stickiness", false);
                    k.SetValue("ScrollIndicator", ScrollIndicator ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("IndicatorOffset", IndicatorOffset, RegistryValueKind.DWord);
                    k.SetValue("IndicatorStyle", IndicatorStyle, RegistryValueKind.DWord);
                    k.SetValue("IndicatorDiameter", IndicatorSize, RegistryValueKind.DWord);
                    k.DeleteValue("IndicatorSize", false);
                    k.SetValue("DragKeys", DragKeys ?? "", RegistryValueKind.String);
                    k.SetValue("KeyMoveToDrag", KeyMoveToDrag ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("WheelToDrag", WheelToDrag ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("WheelDragKeys", WheelDragKeys ?? "", RegistryValueKind.String);
                    k.DeleteValue("DragModifier", false);   // the older single-key value
                    k.DeleteValue("DragModifiers", false);  // test builds
                    k.DeleteValue("DragKeyVk", false);
                    k.SetValue("DeviceFilter", DeviceFilter ?? "", RegistryValueKind.String);
                    k.SetValue("DeviceModes", DeviceModes ?? "", RegistryValueKind.String);
                    k.SetValue("DevicesInitialized", DevicesInitialized ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("CancelAutoscroll", CancelAutoscroll ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("HideTrayIcon", HideTrayIcon ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("DriverWarningShown", DriverWarningShown ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("SwitchDriver", SwitchDriver ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("ScrollWhenPaused", ScrollWhenPaused ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("RestoreDriverScroll", RestoreDriverScroll ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("CheckForUpdates", CheckForUpdates ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AutoInstallUpdates", AutoInstallUpdates ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("RunAsAdmin", RunAsAdmin ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("LastUpdateCheck", LastUpdateCheck, RegistryValueKind.QWord);
                    try { k.DeleteSubKeyTree("Profiles", false); } catch (Exception) { }
                    if (DeviceProfiles != null && DeviceProfiles.Count > 0)
                    {
                        using (var devs = k.CreateSubKey("Profiles"))
                        {
                            foreach (var kv in DeviceProfiles)
                            {
                                if (kv.Value == null) continue;
                                string safe = kv.Key.Replace('\\', '~');
                                using (var dk = devs.CreateSubKey(safe))
                                {
                                    dk.SetValue("Key", kv.Key, RegistryValueKind.String);
                                    WriteTuningToRegistry(kv.Value, dk);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception) { /* not fatal: settings just won't persist */ }
        }

        static void LoadIni(Settings s, string path)
        {
            try
            {
                Settings target = s;
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                    if (line[0] == '[' && line[line.Length - 1] == ']')
                    {
                        string sec = line.Substring(1, line.Length - 2).Trim();
                        if (sec.StartsWith("Profile:", StringComparison.OrdinalIgnoreCase))
                        {
                            string pkey = sec.Substring("Profile:".Length).Trim();
                            target = pkey.Length > 0 ? s.GetOrCreateProfile(pkey) : s;
                        }
                        else target = s;
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    int n; long l;
                    switch (key.ToLowerInvariant())
                    {
                        case "enabled": if (target == s) s.Enabled = ParseBool(val, s.Enabled); break;
                        case "middlebuttonscroll": target.MiddleButtonScroll = ParseBool(val, target.MiddleButtonScroll); break;
                        case "speed": if (int.TryParse(val, out n)) target.Speed = Clamp(n, 1, 100); break;
                        case "horizontalspeed": if (int.TryParse(val, out n)) target.HorizontalSpeed = Clamp(n, 0, 100); break;
                        case "deadzone": if (int.TryParse(val, out n)) target.Deadzone = Clamp(n, 0, 100); break;
                        case "clicktimeout": if (int.TryParse(val, out n)) target.ClickTimeout = Clamp(n, 0, 10000); break;
                        case "notched": target.Notched = ParseBool(val, target.Notched); break;
                        case "natural": target.Natural = ParseBool(val, target.Natural); break;
                        case "axislock": target.AxisLock = ParseBool(val, target.AxisLock); break;
                        case "stickiness":
                            n = ParseStickiness(val);
                            if (n >= 0) target.Stickiness = n;
                            break;
                        case "scrollindicator": target.ScrollIndicator = ParseBool(val, target.ScrollIndicator); break;
                        case "indicatoroffset": if (int.TryParse(val, out n)) target.IndicatorOffset = Clamp(n, 0, 100); break;
                        case "indicatorstyle":
                            n = Array.IndexOf(IndicatorStyleNames, val.ToLowerInvariant());
                            if (n >= 0) target.IndicatorStyle = n;
                            break;
                        case "indicatorsize":
                            n = ParseIndicatorSize(val);
                            if (n > 0) target.IndicatorSize = n;
                            break;
                        case "dragkeys":
                        case "dragmodifier":    // older name: one of off, alt, ctrl, shift
                            var shortcuts = ParseShortcuts(val);
                            if (shortcuts != null) target.DragKeys = ShortcutsText(shortcuts);
                            break;
                        case "keymovetodrag": target.KeyMoveToDrag = ParseBool(val, target.KeyMoveToDrag); break;
                        case "wheeltodrag": target.WheelToDrag = ParseBool(val, target.WheelToDrag); break;
                        case "wheeldragkeys":
                            var wsc = ParseShortcuts(val);
                            if (wsc != null) target.WheelDragKeys = ShortcutsText(wsc);
                            break;
                        case "devicefilter": if (target == s) s.DeviceFilter = val; break;
                        case "devicemodes": if (target == s) s.DeviceModes = val; break;
                        case "devicesinitialized": if (target == s) s.DevicesInitialized = ParseBool(val, s.DevicesInitialized); break;
                        case "cancelautoscroll": target.CancelAutoscroll = ParseBool(val, target.CancelAutoscroll); break;
                        case "hidetrayicon": if (target == s) s.HideTrayIcon = ParseBool(val, s.HideTrayIcon); break;
                        case "driverwarningshown": if (target == s) s.DriverWarningShown = ParseBool(val, s.DriverWarningShown); break;
                        case "switchdriver": if (target == s) s.SwitchDriver = ParseBool(val, s.SwitchDriver); break;
                        case "scrollwhenpaused": if (target == s) s.ScrollWhenPaused = ParseBool(val, s.ScrollWhenPaused); break;
                        case "restoredriverscroll": if (target == s) s.RestoreDriverScroll = ParseBool(val, s.RestoreDriverScroll); break;
                        case "checkforupdates": if (target == s) s.CheckForUpdates = ParseBool(val, s.CheckForUpdates); break;
                        case "autoinstallupdates": if (target == s) s.AutoInstallUpdates = ParseBool(val, s.AutoInstallUpdates); break;
                        case "runasadmin": if (target == s) s.RunAsAdmin = ParseBool(val, s.RunAsAdmin); break;
                        case "lastupdatecheck": if (target == s && long.TryParse(val, out l)) s.LastUpdateCheck = l; break;
                    }
                }
            }
            catch (Exception) { }
        }

        static void AppendTuningIni(StringBuilder sb, Settings t)
        {
            sb.AppendLine("MiddleButtonScroll=" + (t.MiddleButtonScroll ? 1 : 0));
            sb.AppendLine("Speed=" + t.Speed);
            sb.AppendLine("HorizontalSpeed=" + t.HorizontalSpeed);
            sb.AppendLine("Deadzone=" + t.Deadzone);
            sb.AppendLine("ClickTimeout=" + t.ClickTimeout);
            sb.AppendLine("Notched=" + (t.Notched ? 1 : 0));
            sb.AppendLine("Natural=" + (t.Natural ? 1 : 0));
            sb.AppendLine("AxisLock=" + (t.AxisLock ? 1 : 0));
            sb.AppendLine("Stickiness=" + StickinessText(t.Stickiness));
            sb.AppendLine("ScrollIndicator=" + (t.ScrollIndicator ? 1 : 0));
            sb.AppendLine("IndicatorOffset=" + t.IndicatorOffset);
            sb.AppendLine("IndicatorStyle=" + IndicatorStyleNames[t.IndicatorStyle]);
            sb.AppendLine("IndicatorSize=" + t.IndicatorSize);
            sb.AppendLine("DragKeys=" + (t.DragKeys ?? ""));
            sb.AppendLine("KeyMoveToDrag=" + (t.KeyMoveToDrag ? 1 : 0));
            sb.AppendLine("WheelToDrag=" + (t.WheelToDrag ? 1 : 0));
            sb.AppendLine("WheelDragKeys=" + (t.WheelDragKeys ?? ""));
            sb.AppendLine("CancelAutoscroll=" + (t.CancelAutoscroll ? 1 : 0));
        }

        void SaveIni(string path)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("; NubMouse portable settings");
                sb.AppendLine("Enabled=" + (Enabled ? 1 : 0));
                AppendTuningIni(sb, this);
                sb.AppendLine("DeviceFilter=" + (DeviceFilter ?? ""));
                sb.AppendLine("DeviceModes=" + (DeviceModes ?? ""));
                sb.AppendLine("DevicesInitialized=" + (DevicesInitialized ? 1 : 0));
                sb.AppendLine("HideTrayIcon=" + (HideTrayIcon ? 1 : 0));
                sb.AppendLine("DriverWarningShown=" + (DriverWarningShown ? 1 : 0));
                sb.AppendLine("SwitchDriver=" + (SwitchDriver ? 1 : 0));
                sb.AppendLine("ScrollWhenPaused=" + (ScrollWhenPaused ? 1 : 0));
                sb.AppendLine("RestoreDriverScroll=" + (RestoreDriverScroll ? 1 : 0));
                sb.AppendLine("CheckForUpdates=" + (CheckForUpdates ? 1 : 0));
                sb.AppendLine("AutoInstallUpdates=" + (AutoInstallUpdates ? 1 : 0));
                sb.AppendLine("RunAsAdmin=" + (RunAsAdmin ? 1 : 0));
                sb.AppendLine("LastUpdateCheck=" + LastUpdateCheck);
                if (DeviceProfiles != null)
                {
                    foreach (var kv in DeviceProfiles)
                    {
                        if (kv.Value == null) continue;
                        sb.AppendLine();
                        sb.AppendLine("[Profile:" + kv.Key + "]");
                        AppendTuningIni(sb, kv.Value);
                    }
                }
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        static bool ParseBool(string val, bool fallback)
        {
            var v = val.ToLowerInvariant();
            if (v == "1" || v == "true" || v == "yes" || v == "on") return true;
            if (v == "0" || v == "false" || v == "no" || v == "off") return false;
            return fallback;
        }

        public static void DeleteStored()
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false); } catch (Exception) { }
        }

        /// Applies option words such as "speed=6", "notched", "--natural". Unknown words are
        /// returned in <paramref name="errors"/> instead of throwing.
        public void ApplyOptions(IEnumerable<string> words, List<string> errors)
        {
            foreach (var raw in words)
            {
                var w = raw.Trim().TrimStart('-', '/').ToLowerInvariant();
                string name = w, value = null;
                int eq = w.IndexOf('=');
                if (eq >= 0) { name = w.Substring(0, eq); value = w.Substring(eq + 1); }

                int n;
                switch (name)
                {
                    case "speed":
                        if (int.TryParse(value, out n) && n >= 1 && n <= 100) Speed = n;
                        else errors.Add(raw + "  (speed must be 1-100)");
                        break;
                    case "hspeed":
                        if (int.TryParse(value, out n) && n >= 0 && n <= 100) HorizontalSpeed = n;
                        else errors.Add(raw + "  (hspeed must be 0-100, 0 = same as speed)");
                        break;
                    case "deadzone":
                        if (int.TryParse(value, out n) && n >= 0 && n <= 100) Deadzone = n;
                        else errors.Add(raw + "  (deadzone must be 0-100)");
                        break;
                    case "clicktimeout":
                        if (int.TryParse(value, out n) && n >= 0 && n <= 10000) ClickTimeout = n;
                        else errors.Add(raw + "  (clicktimeout must be 0-10000 ms)");
                        break;
                    case "notched": Notched = true; break;
                    case "smooth": Notched = false; break;
                    case "natural": Natural = true; break;
                    case "axislock": AxisLock = true; break;
                    case "diagonal": AxisLock = false; break;
                    case "stickiness":
                        n = ParseStickiness(value);
                        if (n >= 0) { Stickiness = n; AxisLock = true; }
                        else errors.Add(raw + "  (stickiness must be " + string.Join(", ", StickinessNames) + ", or a ratio from 1.2 to 4.0)");
                        break;
                    case "indicator": ScrollIndicator = true; break;
                    case "noindicator": ScrollIndicator = false; break;
                    case "indicatoroffset":
                        if (int.TryParse(value, out n) && n >= 0 && n <= 100) { IndicatorOffset = n; ScrollIndicator = true; }
                        else errors.Add(raw + "  (indicatoroffset must be 0-100 px; 0 = on the pointer)");
                        break;
                    case "indicatorstyle":
                        n = Array.IndexOf(IndicatorStyleNames, value ?? "");
                        if (n >= 0) { IndicatorStyle = n; ScrollIndicator = true; }
                        else errors.Add(raw + "  (indicatorstyle must be " + string.Join(", ", IndicatorStyleNames) + ")");
                        break;
                    case "indicatorsize":
                        n = ParseIndicatorSize(value);
                        if (n > 0) { IndicatorSize = n; ScrollIndicator = true; }
                        else errors.Add(raw + "  (indicatorsize must be " + string.Join(", ", IndicatorSizeNames) + ", or " + IndicatorSizeMin + "-" + IndicatorSizeMax + " px)");
                        break;
                    case "dragkeys":
                    case "dragmodifier":   // older name: one of off, alt, ctrl, shift
                        var shortcuts = value == null ? null : ParseShortcuts(value);
                        if (shortcuts != null) DragKeys = ShortcutsText(shortcuts);
                        else errors.Add(raw + "  (dragkeys must be off, or shortcuts such as alt,ctrl+shift,space: keys joined with +, shortcuts with commas)");
                        break;
                    case "middlescroll": MiddleButtonScroll = true; break;
                    case "nomiddlescroll": MiddleButtonScroll = false; break;
                    case "keydrag": KeyMoveToDrag = true; break;
                    case "nokeydrag": KeyMoveToDrag = false; break;
                    case "wheeltodrag": WheelToDrag = true; break;
                    case "nowheeltodrag": WheelToDrag = false; break;
                    case "devices":
                        if (value == null) errors.Add(raw + "  (devices=all or comma-separated VID:PID)");
                        else DeviceFilter = value == "all" ? "" : value.ToUpperInvariant();
                        break;
                    case "allowautoscroll": CancelAutoscroll = false; break;
                    case "hidden": HideTrayIcon = true; break;
                    case "debug": Debug = true; break;
                    default: errors.Add(raw); break;
                }
            }
        }

        /// Parses <see cref="DeviceModes"/> into a case-insensitive map of device key -> mode.
        public Dictionary<string, DeviceMode> ParsedDeviceModes()
        {
            var map = new Dictionary<string, DeviceMode>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(DeviceModes)) return map;
            foreach (var entry in DeviceModes.Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                string k = entry.Substring(0, eq).Trim();
                string v = entry.Substring(eq + 1).Trim().ToLowerInvariant();
                if (k.Length == 0) continue;
                if (v == "dragonly") map[k] = DeviceMode.DragOnly;
                else if (v == "ignore") map[k] = DeviceMode.Ignore;
                else if (v == "full") map[k] = DeviceMode.Full;
            }
            return map;
        }

        public static string FormatDeviceModes(Dictionary<string, DeviceMode> map)
        {
            var parts = new List<string>();
            foreach (var kv in map)
            {
                if (kv.Value == DeviceMode.Full) continue;
                parts.Add(kv.Key + "=" + (kv.Value == DeviceMode.DragOnly ? "dragonly" : "ignore"));
            }
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join(";", parts.ToArray());
        }

        /// Parses <see cref="DeviceFilter"/> into a case-insensitive set of device keys. Empty = all devices.
        public HashSet<string> AllowedDeviceKeys()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(DeviceFilter)) return set;
            foreach (var part in DeviceFilter.Split(','))
            {
                var p = part.Trim();
                if (p.Length > 0) set.Add(p);
            }
            return set;
        }

        static int ReadInt(RegistryKey k, string name, int fallback)
        {
            object v = k.GetValue(name);
            return v is int ? (int)v : fallback;
        }

        static bool ReadBool(RegistryKey k, string name, bool fallback)
        {
            object v = k.GetValue(name);
            return v is int ? (int)v != 0 : fallback;
        }

        static int ClampStickiness(int v) { return v <= 0 ? StickinessFixed : Clamp(v, StickinessMin, StickinessMax); }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
