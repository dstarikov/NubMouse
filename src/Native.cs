// Win32 declarations used by NubMouse. Kept in one place so the rest of the code reads
// as plain C#. (The project builds with the C# 5 compiler that ships with Windows.)
using System;
using System.Runtime.InteropServices;

namespace NubMouse
{
    static class Native
    {
        public const int WH_MOUSE_LL = 14;

        public const int WM_QUIT = 0x0012;
        public const int WM_INPUT = 0x00FF;
        public const int WM_TIMER = 0x0113;
        public const int WM_MOUSEMOVE = 0x0200;
        public const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
        public const int WM_MOUSEWHEEL = 0x020A, WM_MOUSEHWHEEL = 0x020E;
        public const int WM_APP = 0x8000;

        public const ushort HID_USAGE_PAGE_GENERIC = 0x01, HID_USAGE_GENERIC_MOUSE = 0x02;
        public const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
        public const uint RID_INPUT = 0x10000003;
        public const uint RIM_TYPEMOUSE = 0;
        public const uint RIDI_DEVICENAME = 0x20000007;
        public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
        public const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010, RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;

        public const uint LLMHF_INJECTED = 0x01;

        public const uint INPUT_MOUSE = 0;
        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_WHEEL = 0x0800, MOUSEEVENTF_HWHEEL = 0x1000;

        public const int WHEEL_DELTA = 120;

        public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        public const int OBJID_CURSOR = -9;
        public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

        // Field offsets in MSLLHOOKSTRUCT, read directly in the hook to avoid marshalling.
        public const int MSLL_PT_X = 0, MSLL_PT_Y = 4, MSLL_MOUSEDATA = 8, MSLL_FLAGS = 12, MSLL_TIME = 16;

        public static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }

        // INPUT is a union of MOUSEINPUT / KEYBDINPUT / HARDWAREINPUT; MOUSEINPUT is the largest,
        // so this layout has the right size on both 32- and 64-bit.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public MOUSEINPUT mi; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT pt; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWMOUSE
        {
            public ushort usFlags, pad;
            public ushort usButtonFlags, usButtonData;
            public uint ulRawButtons;
            public int lLastX, lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUT_MOUSE { public RAWINPUTHEADER header; public RAWMOUSE mouse; }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc fn, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr fn);
        [DllImport("user32.dll")]
        public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4, REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

        [DllImport("advapi32.dll")]
        public static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool watchSubtree,
            uint filter, Microsoft.Win32.SafeHandles.SafeWaitHandle ev, bool asynchronous);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(IntPtr hRawInput, uint command, ref RAWINPUT_MOUSE data, ref uint size, uint headerSize);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList([In, Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);   // [Out]: copy the filled entries back
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint command, System.Text.StringBuilder data, ref uint size);
        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_HIDEWINDOW = 0x0080;
        [DllImport("user32.dll")]
        public static extern bool PostThreadMessage(uint threadId, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, int msg, uint action, IntPtr changeInfo);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr id);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public const uint EVENT_MODIFY_STATE = 0x0002;
        public const int VK_MBUTTON = 0x04;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenEvent(uint access, bool inherit, string name);
        [DllImport("kernel32.dll")]
        public static extern bool SetEvent(IntPtr ev);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vk);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder packageFullName);
    }
}
