// Enumerates pointing devices via Raw Input and extracts stable VID:PID (or ACPI/PS2) keys
// plus human-readable labels so users can restrict NubMouse to their TrackPoint while
// leaving external mice untouched.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using N = NubMouse.Native;

namespace NubMouse
{
    sealed class PointingDevice
    {
        public string Key;          // stable identifier, e.g. "17EF:6009" or "ACPI\LEN0071"
        public string Name;         // friendly display name
        public bool LikelyTrackPoint;
        public DeviceMode SuggestedMode;
        public string SuggestedReason;
    }

    static class RawInputDevices
    {
        static readonly Regex VidPidRx = new Regex(@"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.Compiled);
        // Bluetooth LE (HID over GATT) encodes VID/PID as VID&0204D9_PID&2521 (2-hex source + 4-hex VID).
        static readonly Regex BleVidPidRx = new Regex(@"VID&[0-9A-Fa-f]{2}([0-9A-Fa-f]{4})_PID&([0-9A-Fa-f]{4})", RegexOptions.Compiled);

        // Well-known pointing-stick keyboards whose USB/HID descriptors report generic names
        // (e.g. Holtek's "USB-HID Keyboard" on TEX keyboards).
        static readonly Dictionary<string, string> KnownDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "04D9:0532", "TEX Shura (USB)" },
            { "04D9:2521", "TEX Shura (Bluetooth)" },
            { "04D9:0407", "TEX Shinobi" },
            { "17EF:60EE", "ThinkPad TrackPoint Keyboard II (USB)" },
            { "17EF:60E1", "ThinkPad TrackPoint Keyboard II (Bluetooth)" },
            { "17EF:6047", "ThinkPad Compact USB Keyboard with TrackPoint" },
            { "17EF:6048", "ThinkPad Compact Bluetooth Keyboard with TrackPoint" },
            { "17EF:6009", "ThinkPad USB Keyboard with TrackPoint" },
        };

        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool HidD_GetProductString(IntPtr hidDevice, StringBuilder buffer, uint bufferLengthBytes);

        // Firmware-scroll pointing-stick keyboards whose M_FN scrolls in hardware and works best in DragOnly mode.
        static readonly HashSet<string> FirmwareScrollKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "04D9:0532", // TEX Shura (USB)
            "04D9:2521", // TEX Shura (Bluetooth)
            "04D9:0407", // TEX Shinobi
        };

        /// True for known firmware-scroll keyboards (e.g. TEX Shura / Shinobi) where middle-button
        /// scrolling should be off by default so M_FN stays untouched.
        public static bool IsFirmwareScrollDevice(string devKey)
        {
            return !string.IsNullOrEmpty(devKey) && FirmwareScrollKeys.Contains(devKey);
        }

        // Common external mouse USB vendor IDs (suggested as Ignore when a TrackPoint/TEX is also present).
        static readonly HashSet<string> ExternalMouseVids = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "046D", // Logitech
            "1532", // Razer
            "1038", // SteelSeries
            "1B1C", // Corsair
            "045E", // Microsoft
            "0951", // HyperX
            "258A", // Glorious / Sino Wealth
            "373B", // Pulsar / Lamzu
            "047D", // Kensington
        };

        /// Returns the stable filter key for a Raw Input device handle, or null if unreadable.
        public static string GetDeviceKey(IntPtr hDevice)
        {
            string rawName = GetDeviceName(hDevice);
            return rawName == null ? null : KeyFromRawName(rawName);
        }

        /// Enumerates all currently connected pointing devices (deduplicated by Key).
        public static List<PointingDevice> ListMouseDevices()
        {
            var result = new List<PointingDevice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            uint count = 0;
            uint itemSize = (uint)Marshal.SizeOf(typeof(N.RAWINPUTDEVICELIST));
            if (N.GetRawInputDeviceList(null, ref count, itemSize) != 0 || count == 0) return result;

            var list = new N.RAWINPUTDEVICELIST[count];
            uint got = N.GetRawInputDeviceList(list, ref count, itemSize);
            if (got == uint.MaxValue) return result;

            for (int i = 0; i < got; i++)
            {
                if (list[i].dwType != N.RIM_TYPEMOUSE) continue;
                string rawName = GetDeviceName(list[i].hDevice);
                if (string.IsNullOrEmpty(rawName)) continue;
                string key = KeyFromRawName(rawName);
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;

                string known;
                bool isKnown = KnownDevices.TryGetValue(key, out known);
                string friendly = isKnown ? known : (TryGetHidProductName(rawName) ?? LookupFriendlyName(rawName));
                bool tp = isKnown || IsLikelyTrackPoint(rawName, friendly);
                string label = string.IsNullOrEmpty(friendly) ? key : friendly + " (" + key + ")";

                DeviceMode suggested = DeviceMode.Full;
                string reason = "Unknown device";
                if (FirmwareScrollKeys.Contains(key))
                {
                    suggested = DeviceMode.DragOnly;
                    reason = "Auto-detected: firmware-scroll keyboard";
                }
                else if (tp)
                {
                    suggested = DeviceMode.Full;
                    reason = "Auto-detected: TrackPoint / pointing stick";
                }
                else if (IsLikelyExternalMouse(key, rawName, friendly))
                {
                    suggested = DeviceMode.Ignore;
                    reason = "Auto-detected: external / virtual mouse";
                }

                result.Add(new PointingDevice
                {
                    Key = key, Name = label, LikelyTrackPoint = tp,
                    SuggestedMode = suggested, SuggestedReason = reason
                });
            }

            result.Sort((a, b) =>
            {
                if (a.LikelyTrackPoint != b.LikelyTrackPoint) return a.LikelyTrackPoint ? -1 : 1;
                return StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });
            return result;
        }

        /// Recommended per-device modes for the currently connected pointing devices:
        /// - Known TEX / firmware-scroll keyboards -> DragOnly
        /// - Known TrackPoints / pointing sticks -> Full
        /// - External, virtual, and (when a known TrackPoint/TEX is present) unknown devices -> Ignore
        public static string RecommendedDeviceModes()
        {
            var devices = ListMouseDevices();
            bool anyStick = false;
            foreach (var d in devices) if (d.LikelyTrackPoint) { anyStick = true; break; }
            var map = new Dictionary<string, DeviceMode>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in devices)
            {
                if (d.SuggestedMode == DeviceMode.DragOnly) map[d.Key] = DeviceMode.DragOnly;
                else if (d.LikelyTrackPoint) continue;   // Full
                else if (anyStick || d.SuggestedMode == DeviceMode.Ignore) map[d.Key] = DeviceMode.Ignore;
            }
            return Settings.FormatDeviceModes(map);
        }

        static bool IsLikelyExternalMouse(string key, string rawName, string friendly)
        {
            string combined = ((rawName ?? "") + " " + (friendly ?? "")).ToLowerInvariant();
            if (combined.Contains("fakerinput") || combined.Contains("virtual") || combined.Contains("logitech") ||
                combined.Contains("razer") || combined.Contains("usb receiver") || combined.Contains("gaming mouse"))
                return true;
            int colon = key.IndexOf(':');
            if (colon == 4 && ExternalMouseVids.Contains(key.Substring(0, 4))) return true;
            return false;
        }

        static string GetDeviceName(IntPtr hDevice)
        {
            if (hDevice == IntPtr.Zero) return null;
            uint chars = 0;
            if (N.GetRawInputDeviceInfo(hDevice, N.RIDI_DEVICENAME, null, ref chars) != 0 || chars == 0) return null;
            var sb = new StringBuilder((int)chars + 1);
            uint got = N.GetRawInputDeviceInfo(hDevice, N.RIDI_DEVICENAME, sb, ref chars);
            return got == uint.MaxValue ? null : sb.ToString();
        }

        /// Extracts a stable key like "17EF:6009" from a Raw Input device path such as
        /// "\\?\HID#VID_17EF&PID_6009&MI_01#...". Falls back to the hardware class+ID for PS/2 / ACPI.
        public static string KeyFromRawName(string rawName)
        {
            var m = VidPidRx.Match(rawName);
            if (m.Success) return (m.Groups[1].Value + ":" + m.Groups[2].Value).ToUpperInvariant();
            m = BleVidPidRx.Match(rawName);
            if (m.Success) return (m.Groups[1].Value + ":" + m.Groups[2].Value).ToUpperInvariant();

            // e.g. \\?\ACPI#LEN0071#4&...#{guid} -> ACPI\LEN0071
            string trimmed = rawName.StartsWith(@"\\?\") ? rawName.Substring(4) : rawName;
            var parts = trimmed.Split('#');
            if (parts.Length >= 2) return (parts[0] + @"\" + parts[1]).ToUpperInvariant();
            return trimmed.ToUpperInvariant();
        }

        static string TryGetHidProductName(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return null;
            IntPtr h = INVALID_HANDLE_VALUE;
            try
            {
                // Access = 0 opens only device metadata (works on mice Windows owns exclusively).
                h = CreateFile(rawName, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == INVALID_HANDLE_VALUE || h == IntPtr.Zero) return null;
                var sb = new StringBuilder(128);
                if (!HidD_GetProductString(h, sb, (uint)(sb.Capacity * 2))) return null;
                string s = sb.ToString().Trim();
                return s.Length > 0 ? s : null;
            }
            catch (Exception) { return null; }
            finally
            {
                if (h != INVALID_HANDLE_VALUE && h != IntPtr.Zero) N.CloseHandle(h);
            }
        }

        static string LookupFriendlyName(string rawName)
        {
            try
            {
                // \\?\HID#VID_17EF&PID_6009#7&...#{guid} -> SYSTEM\CurrentControlSet\Enum\HID\VID_17EF&PID_6009\7&...
                string trimmed = rawName.StartsWith(@"\\?\") ? rawName.Substring(4) : rawName;
                var parts = trimmed.Split('#');
                if (parts.Length < 3) return null;
                string regPath = @"SYSTEM\CurrentControlSet\Enum\" + parts[0] + @"\" + parts[1] + @"\" + parts[2];
                using (var k = Registry.LocalMachine.OpenSubKey(regPath))
                {
                    if (k == null) return null;
                    string desc = (k.GetValue("FriendlyName") as string) ?? (k.GetValue("DeviceDesc") as string);
                    if (string.IsNullOrEmpty(desc)) return null;
                    int semi = desc.LastIndexOf(';');
                    return (semi >= 0 ? desc.Substring(semi + 1) : desc).Trim();
                }
            }
            catch (Exception) { return null; }
        }

        static bool IsLikelyTrackPoint(string rawName, string friendly)
        {
            string combined = ((rawName ?? "") + " " + (friendly ?? "")).ToLowerInvariant();
            return combined.Contains("trackpoint") || combined.Contains("elan") || combined.Contains("synaptics") ||
                   combined.Contains("vid_17ef") || combined.Contains("len0") || combined.Contains("alps") ||
                   combined.Contains("shura") || combined.Contains("shinobi") || combined.Contains("yoda") ||
                   combined.Contains("trackstick") || combined.Contains("pointstick");
        }
    }
}
