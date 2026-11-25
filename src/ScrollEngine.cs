// The core: a low-level mouse hook that turns "middle button held + TrackPoint movement" into
// wheel scrolling, and a lone middle press into an ordinary middle click.
//
// Threads:
//   hook thread   - owns the WH_MOUSE_LL hook, the autoscroll watcher and their message loop. It
//                   only updates state and queues work, because Windows silently removes
//                   low-level hooks that are slow.
//   output thread - performs every SendInput call, in order.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using N = NubMouse.Native;

namespace NubMouse
{
    enum ScrollMode { None, Vertical, Horizontal, Both }

    sealed class ScrollEngine : IDisposable
    {
        /// Raised (on a background thread) when real wheel events arrive while the middle button
        /// is held, i.e. the TrackPoint driver is still doing its own middle-button scrolling.
        /// Raised again only after <see cref="DriverScrollStopped"/>.
        public event Action DriverScrollDetected;

        /// Raised (on a background thread) when the active scrolling direction changes (for the
        /// optional visual scroll indicator).
        public event Action<ScrollMode, int, int> ScrollVisualChanged;

        /// Is the middle button held (a press NubMouse swallowed, so Windows doesn't see it)?
        public bool MiddleHeld { get { return held; } }
        /// Filter key of the last physical pointing device that sent Raw Input (for identifying devices in Settings).
        public string LastActiveDeviceKey { get { return lastActiveDeviceKey; } }
        volatile string lastActiveDeviceKey;
        /// Environment.TickCount of the last real (non-injected) wheel event seen while running:
        /// with ELAN's driver in Scrolling mode, that's the driver scrolling.
        public int LastWheelTick { get { return lastWheelTick; } }
        volatile int lastWheelTick = Environment.TickCount - 60000;

        /// Raised (on a background thread) after <see cref="DriverScrollDetected"/>, when a press
        /// is scrolled by NubMouse with no wheel events from the driver: its middle-button
        /// scrolling has been turned off.
        public event Action DriverScrollStopped;

        // How long after a middle click a switch to an app's own cursor counts as autoscroll, and
        // how far the pointer may have moved meanwhile.
        const uint WatchMs = 500;
        const int WatchSlop = 4;
        const int WM_ARM_WATCH = N.WM_APP + 1;   // hook thread: wParam = press id, lParam = cursor before the click

        enum Kind { Wheel, Click, CancelClick, RawMiddleDown, RawMiddleUp, RawMove }
        struct Work { public Kind Kind; public int A, B; }

        volatile Settings cfg, texDefaultCfg;
        volatile HashSet<string> allowedDevices;
        volatile Dictionary<string, DeviceMode> deviceModes;
        volatile Dictionary<string, int[][]> profileDragShortcuts = new Dictionary<string, int[][]>(StringComparer.OrdinalIgnoreCase);
        volatile Dictionary<string, int[][]> profileWheelDragShortcuts = new Dictionary<string, int[][]>(StringComparer.OrdinalIgnoreCase);
        volatile bool anyKeyMoveToDrag;
        readonly N.HookProc proc;                // fields keep the delegates alive while hooked
        readonly N.WinEventProc winEventProc;
        readonly BlockingCollection<Work> output = new BlockingCollection<Work>();
        readonly Thread outputThread;

        Thread hookThread;
        uint hookThreadId;
        IntPtr hook;
        int driverScrollReported;

        // Press state. Written only on the hook thread; pressId is also read by the output thread.
        bool held, scrolling, driverScrolled, passthroughHeld, rawConfirmed, usingRawDeltas, wheelDragging, keyDragging;
        string activePressDeviceKey;
        int[] wheelDragShortcut, keyDragShortcut;
        UIntPtr wheelDragTimer, keyDragTimer;
        N.POINT anchor;
        uint downTime;
        int accX, accY, travel;
        volatile int pressId;
        ScrollMode visualMode;
        readonly Dictionary<IntPtr, string> deviceCache = new Dictionary<IntPtr, string>();

        // Axis lock (hook thread only): the axis being scrolled, and recent movement per axis.
        // recentX/Y decay by AxisDecay per move, so they weigh roughly the last 5 moves (~50 ms).
        // How much more the other axis must move to switch to it comes from Settings.Stickiness.
        const float AxisDecay = 0.8f, AxisSwitchMin = 8f;
        enum Axis { None, Vertical, Horizontal }
        Axis axis;
        float recentX, recentY;

        // Per-press statistics for the debug log (hook thread only).
        int dbgMoves, dbgStill, dbgWheelY, dbgWheelX;
        long dbgMaxHookTicks;

        // Autoscroll watcher state, hook thread only.
        bool watching;
        int watchId;
        IntPtr watchBefore, winEventHook;
        UIntPtr watchTimer;
        N.POINT watchPos;

        public ScrollEngine(Settings settings)
        {
            ApplyConfig(settings);
            proc = HookCallback;
            winEventProc = OnWinEvent;
            outputThread = new Thread(OutputLoop) { IsBackground = true, Name = "NubMouse output" };
            outputThread.Start();
        }

        public bool Running { get { return hookThread != null; } }

        public void Configure(Settings settings)
        {
            ApplyConfig(settings);
            DebugLog.Write("settings changed, " + Describe(cfg));
        }

        void ApplyConfig(Settings settings)
        {
            var next = settings.Clone();
            var texDef = next.Clone();
            texDef.MiddleButtonScroll = false;
            texDef.KeyMoveToDrag = true;
            if (texDef.DragShortcuts().Count == 0) texDef.DragKeys = "Ctrl+Alt";
            var modes = next.ParsedDeviceModes();
            var dsc = next.DragShortcuts().ToArray();
            var wdsc = next.WheelDragShortcuts().ToArray();
            var texDsc = texDef.DragShortcuts().ToArray();
            var pDsc = new Dictionary<string, int[][]>(StringComparer.OrdinalIgnoreCase);
            var pWdsc = new Dictionary<string, int[][]>(StringComparer.OrdinalIgnoreCase);
            bool anyKey = true;   // TEX / DragOnly devices default to KeyMoveToDrag + Ctrl+Alt even if global DragKeys is off
            if (next.DeviceProfiles != null)
            {
                foreach (var kv in next.DeviceProfiles)
                {
                    if (kv.Value == null) continue;
                    var sc = kv.Value.DragShortcuts().ToArray();
                    pDsc[kv.Key] = sc;
                    pWdsc[kv.Key] = kv.Value.WheelDragShortcuts().ToArray();
                }
            }
            cfg = next;
            texDefaultCfg = texDef;
            allowedDevices = next.AllowedDeviceKeys();
            deviceModes = modes;
            dragShortcuts = dsc;
            wheelDragShortcuts = wdsc;
            texDefaultDragShortcuts = texDsc;
            profileDragShortcuts = pDsc;
            profileWheelDragShortcuts = pWdsc;
            anyKeyMoveToDrag = anyKey;
        }

        Settings ActiveCfg(string devKey)
        {
            Settings p;
            if (!string.IsNullOrEmpty(devKey) && cfg.DeviceProfiles != null && cfg.DeviceProfiles.TryGetValue(devKey, out p) && p != null)
                return p;
            if (!string.IsNullOrEmpty(devKey) && (RawInputDevices.IsFirmwareScrollDevice(devKey) || ModeFor(devKey) == DeviceMode.DragOnly))
                return texDefaultCfg;
            return cfg;
        }

        int[][] DragShortcutsFor(string devKey)
        {
            int[][] sc;
            if (!string.IsNullOrEmpty(devKey) && profileDragShortcuts.TryGetValue(devKey, out sc)) return sc;
            if (!string.IsNullOrEmpty(devKey) && (RawInputDevices.IsFirmwareScrollDevice(devKey) || ModeFor(devKey) == DeviceMode.DragOnly))
                return texDefaultDragShortcuts;
            return dragShortcuts;
        }

        int[][] WheelDragShortcutsFor(string devKey)
        {
            int[][] sc;
            if (!string.IsNullOrEmpty(devKey) && profileWheelDragShortcuts.TryGetValue(devKey, out sc)) return sc;
            return wheelDragShortcuts;
        }

        static bool HasDragOnly(Dictionary<string, DeviceMode> map)
        {
            foreach (var v in map.Values) if (v == DeviceMode.DragOnly) return true;
            return false;
        }

        DeviceMode ModeFor(string devKey)
        {
            var modes = deviceModes;
            DeviceMode m;
            if (!string.IsNullOrEmpty(devKey) && modes.TryGetValue(devKey, out m)) return m;
            var allowed = allowedDevices;
            if (allowed.Count > 0) return !string.IsNullOrEmpty(devKey) && allowed.Contains(devKey) ? DeviceMode.Full : DeviceMode.Ignore;
            return DeviceMode.Full;
        }

        int[][] dragShortcuts = new int[0][];
        int[][] wheelDragShortcuts = new int[0][];
        int[][] texDefaultDragShortcuts = new int[0][];

        /// The first shortcut whose keys are all held down right now, or null.
        public static int[] HeldShortcut(int[][] shortcuts)
        {
            foreach (var keys in shortcuts)
                if (IsShortcutHeld(keys)) return keys;
            return null;
        }

        static bool IsShortcutHeld(int[] keys)
        {
            if (keys == null || keys.Length == 0) return false;
            foreach (int vk in keys)
                if ((N.GetAsyncKeyState(vk) & 0x8000) == 0 && !(vk == 0x5B && (N.GetAsyncKeyState(0x5C) & 0x8000) != 0)) return false;
            return true;
        }

        static string Describe(Settings c)
        {
            return string.Format("speed={0} hspeed={1} deadzone={2} clicktimeout={3} notched={4} natural={5} axislock={6} stickiness={7} indicator={8} dragmod={9} devices={10} cancelautoscroll={11}",
                                 c.Speed, c.HorizontalSpeed, c.Deadzone, c.ClickTimeout, c.Notched, c.Natural, c.AxisLock,
                                 Settings.StickinessText(c.Stickiness), c.ScrollIndicator, string.IsNullOrEmpty(c.DragKeys) ? "off" : c.DragKeys,
                                 string.IsNullOrEmpty(c.DeviceFilter) ? "all" : c.DeviceFilter, c.CancelAutoscroll);
        }

        sealed class RawInputWindow : NativeWindow
        {
            readonly Action<IntPtr> onInput;
            public RawInputWindow(Action<IntPtr> onInput)
            {
                this.onInput = onInput;
                CreateHandle(new CreateParams { Parent = N.HWND_MESSAGE });
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == N.WM_INPUT) { onInput(m.LParam); return; }
                base.WndProc(ref m);
            }
        }

        /// Installs the hook. Returns 0 on success or the Win32 error code.
        public int Start()
        {
            if (hookThread != null) return 0;
            int error = 0;
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                hookThreadId = N.GetCurrentThreadId();
                held = passthroughHeld = rawConfirmed = usingRawDeltas = false;
                visualMode = ScrollMode.None;
                deviceCache.Clear();

                RawInputWindow rawWin = null;
                bool rawRegistered = false;
                try
                {
                    rawWin = new RawInputWindow(OnRawInput);
                    var rid = new[] { new N.RAWINPUTDEVICE { usUsagePage = N.HID_USAGE_PAGE_GENERIC, usUsage = N.HID_USAGE_GENERIC_MOUSE, dwFlags = N.RIDEV_INPUTSINK, hwndTarget = rawWin.Handle } };
                    rawRegistered = N.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(N.RAWINPUTDEVICE)));
                }
                catch (Exception) { rawRegistered = false; }

                hook = N.SetWindowsHookEx(N.WH_MOUSE_LL, proc, N.GetModuleHandle(null), 0);
                if (hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
                ready.Set();
                if (hook == IntPtr.Zero)
                {
                    if (rawRegistered && rawWin != null) UnregisterRawInput();
                    if (rawWin != null) rawWin.DestroyHandle();
                    DebugLog.Write("could not install mouse hook, error {0}", error);
                    return;
                }
                DebugLog.Write("mouse hook installed (rawinput={0}), " + Describe(cfg), rawRegistered);

                N.MSG msg;
                while (N.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd != IntPtr.Zero) N.DispatchMessage(ref msg);
                    else if (msg.message == WM_ARM_WATCH) ArmWatch(msg.wParam.ToInt32(), msg.lParam);
                    else if (msg.message == N.WM_TIMER && (ulong)msg.wParam.ToInt64() == watchTimer.ToUInt64())
                    {
                        DebugLog.Write("press #{0}: autoscroll watch ended, no app cursor seen", watchId);
                        Disarm();
                    }
                    else if (msg.message == N.WM_TIMER && wheelDragTimer != UIntPtr.Zero && (ulong)msg.wParam.ToInt64() == wheelDragTimer.ToUInt64())
                    {
                        if (wheelDragging && !IsShortcutHeld(wheelDragShortcut)) StopWheelDrag();
                    }
                    else if (msg.message == N.WM_TIMER && keyDragTimer != UIntPtr.Zero && (ulong)msg.wParam.ToInt64() == keyDragTimer.ToUInt64())
                    {
                        if (keyDragging && !IsShortcutHeld(keyDragShortcut)) StopKeyDrag();
                    }
                }

                Disarm();
                StopWheelDrag();
                StopKeyDrag();
                NotifyVisual(ScrollMode.None);
                N.UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
                if (rawRegistered) UnregisterRawInput();
                if (rawWin != null) rawWin.DestroyHandle();
                DebugLog.Write("mouse hook removed");
            });
            t.IsBackground = true;
            t.Name = "NubMouse hook";
            t.Priority = ThreadPriority.Highest;
            t.Start();
            ready.WaitOne();
            ready.Close();

            if (error != 0) { t.Join(); return error; }
            hookThread = t;
            return 0;
        }

        /// Removes the hook. The middle button then reaches apps exactly as the driver sends it.
        public void Stop()
        {
            var t = hookThread;
            if (t == null) return;
            hookThread = null;
            N.PostThreadMessage(hookThreadId, N.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            t.Join(2000);
        }

        /// Re-installs the hook and clears any half-finished press. Used after unlock / resume,
        /// where Windows may have dropped the hook or swallowed a button-up.
        public int Restart()
        {
            Stop();
            return Start();
        }

        public void Dispose()
        {
            Stop();
            output.CompleteAdding();
            outputThread.Join(1000);
        }

        // ---- Hook ------------------------------------------------------------------------

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                long t0 = DebugLog.On ? Stopwatch.GetTimestamp() : 0;
                int msg = wParam.ToInt32();
                // Fast path: pointer movement without the middle button held is by far the most
                // common event and never interesting (unless KeyMoveToDrag or a DragOnly device is active).
                bool relevant = msg == N.WM_MOUSEMOVE ? (held || anyKeyMoveToDrag)
                    : msg == N.WM_MBUTTONDOWN || msg == N.WM_MBUTTONUP || msg == N.WM_MOUSEWHEEL || msg == N.WM_MOUSEHWHEEL;
                // Ignore injected input: that includes our own SendInput output, which comes back
                // through this hook, and input from remote-control / macro tools.
                bool swallow = relevant && (Marshal.ReadInt32(lParam, N.MSLL_FLAGS) & N.LLMHF_INJECTED) == 0 && Handle(msg, lParam);
                if (t0 != 0 && relevant) TimeHook(t0, msg);
                if (swallow) return (IntPtr)1;
            }
            return N.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void TimeHook(long t0, int msg)
        {
            long ticks = Stopwatch.GetTimestamp() - t0;
            if (ticks > dbgMaxHookTicks) dbgMaxHookTicks = ticks;
            double us = ticks * 1e6 / Stopwatch.Frequency;
            if (us > 1000) DebugLog.Write("SLOW hook callback: {0:0} us for message 0x{1:X}", us, msg);
        }

        static void UnregisterRawInput()
        {
            var rid = new[] { new N.RAWINPUTDEVICE { usUsagePage = N.HID_USAGE_PAGE_GENERIC, usUsage = N.HID_USAGE_GENERIC_MOUSE, dwFlags = N.RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
            N.RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(N.RAWINPUTDEVICE)));
        }

        static readonly uint RawMouseSize = (uint)Marshal.SizeOf(typeof(N.RAWINPUT_MOUSE));
        static readonly uint RawHeaderSize = (uint)Marshal.SizeOf(typeof(N.RAWINPUTHEADER));

        void OnRawInput(IntPtr lParam)
        {
            var raw = default(N.RAWINPUT_MOUSE);
            uint size = RawMouseSize;
            if (N.GetRawInputData(lParam, N.RID_INPUT, ref raw, ref size, RawHeaderSize) == uint.MaxValue) return;
            if (raw.header.dwType != N.RIM_TYPEMOUSE || raw.header.hDevice == IntPtr.Zero) return;

            string devKey;
            if (!deviceCache.TryGetValue(raw.header.hDevice, out devKey))
            {
                devKey = RawInputDevices.GetDeviceKey(raw.header.hDevice);
                deviceCache[raw.header.hDevice] = devKey;
            }
            if (!string.IsNullOrEmpty(devKey) &&
                (raw.mouse.lLastX != 0 || raw.mouse.lLastY != 0 || raw.mouse.usButtonFlags != 0))
                lastActiveDeviceKey = devKey;

            if ((raw.mouse.usButtonFlags & N.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0 && held && !rawConfirmed)
            {
                rawConfirmed = true;
                activePressDeviceKey = devKey;
                if (ModeFor(devKey) != DeviceMode.Full || !ActiveCfg(devKey).MiddleButtonScroll)
                {
                    // Middle button came from an ignored or drag-only device (e.g. external mouse or
                    // TEX Shura): replay the press so the user gets native middle-click / middle-drag.
                    held = false;
                    passthroughHeld = true;
                    NotifyVisual(ScrollMode.None);
                    output.Add(new Work { Kind = Kind.RawMiddleDown });
                    DebugLog.Write("press #{0}: middle down from {1} ({2}): passed through", pressId, devKey ?? "(unknown)", ModeFor(devKey));
                    return;
                }
            }

            if (held && !driverScrolled && (raw.mouse.usFlags & N.MOUSE_MOVE_ABSOLUTE) == 0 &&
                (raw.mouse.lLastX != 0 || raw.mouse.lLastY != 0))
            {
                if (ModeFor(devKey) != DeviceMode.Full || !ActiveCfg(devKey).MiddleButtonScroll) return;
                activePressDeviceKey = devKey;
                usingRawDeltas = true;
                OnMoveWhileHeld(raw.mouse.lLastX, raw.mouse.lLastY);
            }
        }

        /// The indicator state right now. Notifications can arrive out of order (they go through
        /// the thread pool), so the receiver should show this rather than the mode it was sent.
        public ScrollMode VisualMode { get { return ActiveCfg(activePressDeviceKey ?? lastActiveDeviceKey).ScrollIndicator ? visualMode : ScrollMode.None; } }

        void NotifyVisual(ScrollMode mode)
        {
            if (visualMode == mode) return;
            visualMode = mode;
            if (mode != ScrollMode.None && !ActiveCfg(activePressDeviceKey ?? lastActiveDeviceKey).ScrollIndicator) return;
            var h = ScrollVisualChanged;
            if (h != null)
            {
                int ax = anchor.x, ay = anchor.y;
                ThreadPool.QueueUserWorkItem(_ => h(mode, ax, ay));
            }
        }

        /// Returns true to swallow the event.
        bool Handle(int msg, IntPtr m)
        {
            switch (msg)
            {
                case N.WM_MBUTTONDOWN:
                    var downKey = lastActiveDeviceKey;
                    // When middle-button scrolling is turned off (e.g. TEX Shura using only 3D/CAD
                    // drag shortcuts), leave physical middle clicks completely untouched.
                    if (!ActiveCfg(downKey).MiddleButtonScroll) return false;
                    // Holding all the keys of any native-drag shortcut passes a middle-button drag straight through.
                    int[] shortcut = HeldShortcut(DragShortcutsFor(downKey));
                    if (shortcut != null)
                    {
                        passthroughHeld = true;
                        DebugLog.Write("middle down with {0} held: native drag passed on", Settings.ShortcutText(shortcut));
                        return false;
                    }
                    // Windows can't take back a delivered press, so hold it until we know
                    // whether this is a click or a scroll.
                    held = true;
                    activePressDeviceKey = downKey;
                    scrolling = driverScrolled = passthroughHeld = rawConfirmed = usingRawDeltas = false;
                    anchor.x = Marshal.ReadInt32(m, N.MSLL_PT_X);
                    anchor.y = Marshal.ReadInt32(m, N.MSLL_PT_Y);
                    downTime = unchecked((uint)Marshal.ReadInt32(m, N.MSLL_TIME));
                    accX = accY = travel = 0;
                    axis = Axis.None;
                    recentX = recentY = 0;
                    pressId++;                // also stops any autoscroll watch from the last click
                    dbgMoves = dbgStill = dbgWheelY = dbgWheelX = 0;
                    dbgMaxHookTicks = 0;
                    DebugLog.Write("press #{0}: middle down at ({1},{2})", pressId, anchor.x, anchor.y);
                    return true;

                case N.WM_MBUTTONUP:
                    if (passthroughHeld)
                    {
                        passthroughHeld = false;
                        return false;
                    }
                    if (!held)                 // pressed before the hook was installed
                    {
                        DebugLog.Write("middle up without a middle down seen: passed on");
                        return false;
                    }
                    held = false;
                    NotifyVisual(ScrollMode.None);
                    uint heldMs = unchecked((uint)Marshal.ReadInt32(m, N.MSLL_TIME) - downTime);
                    string result;
                    if (scrolling)
                    {
                        result = "scroll";
                        if (!driverScrolled && Interlocked.Exchange(ref driverScrollReported, 0) == 1)
                        {
                            DebugLog.Write("press #{0}: scrolled with no wheel events from the driver (its scrolling is off now)", pressId);
                            ThreadPool.QueueUserWorkItem(_ => { var h = DriverScrollStopped; if (h != null) h(); });
                        }
                    }
                    else if (driverScrolled) result = "no click (driver scrolled)";
                    else
                    {
                        // A press held for a long time without scrolling is a cancelled scroll,
                        // not a click.
                        int limit = ActiveCfg(activePressDeviceKey ?? lastActiveDeviceKey).ClickTimeout;
                        if (limit == 0 || heldMs <= (uint)limit) { QueueClick(); result = "click"; }
                        else result = "no click (held longer than clicktimeout)";
                    }
                    if (DebugLog.On)
                        DebugLog.Write("press #{0}: middle up after {1} ms, travel {2}, {3} moves ({4} with no offset), " +
                                       "wheel sent v={5} h={6}, slowest hook callback {7:0} us -> {8}",
                                       pressId, heldMs, travel, dbgMoves, dbgStill, dbgWheelY, dbgWheelX,
                                       dbgMaxHookTicks * 1e6 / Stopwatch.Frequency, result);
                    return true;

                case N.WM_MOUSEMOVE:
                    if (!held)
                    {
                        // Optional: hold a drag shortcut + move pointer (no middle button) -> real middle-drag.
                        var devKey = lastActiveDeviceKey;
                        var mode = ModeFor(devKey);
                        var ac = ActiveCfg(devKey);
                        if ((ac.KeyMoveToDrag || mode == DeviceMode.DragOnly) && mode != DeviceMode.Ignore && !passthroughHeld && !keyDragging)
                        {
                            int[] sc = HeldShortcut(DragShortcutsFor(devKey));
                            if (sc != null)
                            {
                                keyDragging = true;
                                keyDragShortcut = sc;
                                output.Add(new Work { Kind = Kind.RawMiddleDown });
                                if (keyDragTimer == UIntPtr.Zero)
                                    keyDragTimer = N.SetTimer(IntPtr.Zero, UIntPtr.Zero, 30, IntPtr.Zero);
                                DebugLog.Write("key-move-to-drag started with {0} on {1}", Settings.ShortcutText(sc), devKey ?? "(unknown)");
                            }
                        }
                        return false;          // pass raw movement straight through for smooth 360° drag
                    }
                    if (!usingRawDeltas)
                        OnMoveWhileHeld(Marshal.ReadInt32(m, N.MSLL_PT_X) - anchor.x, Marshal.ReadInt32(m, N.MSLL_PT_Y) - anchor.y);
                    return true;               // swallowing every move keeps the pointer at anchor

                case N.WM_MOUSEWHEEL:
                case N.WM_MOUSEHWHEEL:
                    lastWheelTick = Environment.TickCount;
                    // Optional: convert hardware wheel events (from firmware-scroll keyboards like
                    // TEX Shura's M_FN) into a synthetic middle-button drag while a drag shortcut is held.
                    var wKey = lastActiveDeviceKey;
                    if (!held && ActiveCfg(wKey).WheelToDrag && ModeFor(wKey) != DeviceMode.Ignore)
                    {
                        {
                            var wsc = WheelDragShortcutsFor(wKey);
                            var wheelSc = wsc.Length > 0 ? wsc : DragShortcutsFor(wKey);
                            int[] sc = wheelDragging ? (IsShortcutHeld(wheelDragShortcut) ? wheelDragShortcut : null) : HeldShortcut(wheelSc);
                            if (sc != null)
                            {
                                int wheelDelta = (short)((Marshal.ReadInt32(m, N.MSLL_MOUSEDATA) >> 16) & 0xFFFF);
                                if (wheelDelta != 0)
                                {
                                    int mag = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta) * 14.0 / N.WHEEL_DELTA));
                                    int sgn = wheelDelta > 0 ? 1 : -1;
                                    int dx = msg == N.WM_MOUSEHWHEEL ? sgn * mag : 0;
                                    int dy = msg == N.WM_MOUSEWHEEL ? -sgn * mag : 0;
                                    if (!wheelDragging)
                                    {
                                        wheelDragging = true;
                                        wheelDragShortcut = sc;
                                        output.Add(new Work { Kind = Kind.RawMiddleDown });
                                        if (wheelDragTimer == UIntPtr.Zero)
                                            wheelDragTimer = N.SetTimer(IntPtr.Zero, UIntPtr.Zero, 40, IntPtr.Zero);
                                        DebugLog.Write("wheel-to-drag started with {0}", Settings.ShortcutText(sc));
                                    }
                                    output.Add(new Work { Kind = Kind.RawMove, A = dx, B = dy });
                                    return true;
                                }
                            }
                            else if (wheelDragging) StopWheelDrag();
                        }
                    }
                    // Real wheel input while the button is down: the driver is scrolling by
                    // itself. Let it through, but don't add a middle click on release.
                    if (held && !driverScrolled)
                    {
                        driverScrolled = true;
                        DebugLog.Write("press #{0}: real wheel event while held (the driver is scrolling)", pressId);
                        if (Interlocked.Exchange(ref driverScrollReported, 1) == 0)
                            ThreadPool.QueueUserWorkItem(_ => { var h = DriverScrollDetected; if (h != null) h(); });
                    }
                    return false;
            }
            return false;
        }

        void OnMoveWhileHeld(int dx, int dy)
        {
            dbgMoves++;
            // A move with no offset most likely means the pointer is pinned against a screen edge.
            if (dx == 0 && dy == 0) { dbgStill++; DebugLog.Write("  move with no offset (pointer at a screen edge?)"); return; }
            var c = ActiveCfg(activePressDeviceKey ?? lastActiveDeviceKey);

            travel += Math.Abs(dx) + Math.Abs(dy);
            recentX = recentX * AxisDecay + Math.Abs(dx);
            recentY = recentY * AxisDecay + Math.Abs(dy);
            if (!scrolling)
            {
                if (travel <= c.Deadzone) { DebugLog.Write("  move ({0},{1}), travel {2}: within deadzone", dx, dy, travel); return; }
                scrolling = true;
                DebugLog.Write("  move ({0},{1}), travel {2}: now scrolling", dx, dy, travel);
            }

            if (c.AxisLock)
            {
                // Lock to the main direction of the push, like the TrackPoint driver does, but
                // switch when the other direction clearly takes over (e.g. down a sheet, then
                // across to a column) instead of making the user press again.
                if (axis == Axis.None)
                {
                    axis = recentY >= recentX ? Axis.Vertical : Axis.Horizontal;
                    DebugLog.Write("  locked to {0} scrolling", axis == Axis.Vertical ? "vertical" : "horizontal");
                }
                else
                {
                    float on = axis == Axis.Vertical ? recentY : recentX, off = axis == Axis.Vertical ? recentX : recentY;
                    if (off >= AxisSwitchMin && off > on * Settings.StickinessRatio(c.Stickiness))
                    {
                        axis = axis == Axis.Vertical ? Axis.Horizontal : Axis.Vertical;
                        accX = accY = 0;
                        DebugLog.Write("  switched to {0} scrolling", axis == Axis.Vertical ? "vertical" : "horizontal");
                    }
                }
                if (axis == Axis.Vertical) dx = 0; else dy = 0;
                NotifyVisual(axis == Axis.Vertical ? ScrollMode.Vertical : ScrollMode.Horizontal);
            }
            else
            {
                NotifyVisual(ScrollMode.Both);
            }

            // Default: like a mouse wheel. Push up -> positive wheel (scroll up),
            // push right -> positive h-wheel (scroll right). "natural" flips both.
            int sign = c.Natural ? 1 : -1;
            accY += sign * dy * c.Speed;
            accX -= sign * dx * c.EffectiveHorizontalSpeed;

            int unit = c.Notched ? N.WHEEL_DELTA : 1;
            int wy = accY / unit * unit, wx = accX / unit * unit;
            accY -= wy; accX -= wx;
            dbgWheelY += wy; dbgWheelX += wx;
            DebugLog.Write("  move ({0},{1}) -> wheel v={2} h={3}", dx, dy, wy, wx);
            if (wy != 0 || wx != 0) output.Add(new Work { Kind = Kind.Wheel, A = wy, B = wx });
        }

        void StopWheelDrag()
        {
            if (wheelDragTimer != UIntPtr.Zero) { N.KillTimer(IntPtr.Zero, wheelDragTimer); wheelDragTimer = UIntPtr.Zero; }
            if (!wheelDragging) return;
            wheelDragging = false;
            wheelDragShortcut = null;
            output.Add(new Work { Kind = Kind.RawMiddleUp });
            DebugLog.Write("wheel-to-drag ended");
        }

        void StopKeyDrag()
        {
            if (keyDragTimer != UIntPtr.Zero) { N.KillTimer(IntPtr.Zero, keyDragTimer); keyDragTimer = UIntPtr.Zero; }
            if (!keyDragging) return;
            keyDragging = false;
            keyDragShortcut = null;
            output.Add(new Work { Kind = Kind.RawMiddleUp });
            DebugLog.Write("key-move-to-drag ended");
        }

        void QueueClick()
        {
            output.Add(new Work { Kind = Kind.Click, A = pressId, B = ActiveCfg(activePressDeviceKey ?? lastActiveDeviceKey).CancelAutoscroll ? 1 : 0 });
        }

        // ---- Output ----------------------------------------------------------------------

        readonly N.INPUT[] one = new N.INPUT[1], two = new N.INPUT[2];   // output thread only

        void OutputLoop()
        {
            Work w;
            bool havePending = false;
            Work pending = default(Work);
            while (true)
            {
                if (havePending) { w = pending; havePending = false; }
                else
                {
                    try { w = output.Take(); }
                    catch (InvalidOperationException) { return; }   // CompleteAdding: shutting down
                }

                if (w.Kind == Kind.Wheel)
                {
                    // If scrolling produced events faster than they were sent, send them as one.
                    Work next;
                    int merged = 1;
                    while (output.TryTake(out next))
                    {
                        if (next.Kind != Kind.Wheel) { pending = next; havePending = true; break; }
                        w.A += next.A; w.B += next.B;
                        merged++;
                    }
                    if (merged > 1) DebugLog.Write("sending {0} queued wheel steps as one", merged);
                }

                try { Do(w); } catch (Exception) { /* never let one bad send stop the queue */ }
            }
        }

        void Do(Work w)
        {
            switch (w.Kind)
            {
                case Kind.Wheel:
                    if (w.A != 0 && w.B != 0) { Set(two, 0, N.MOUSEEVENTF_WHEEL, w.A); Set(two, 1, N.MOUSEEVENTF_HWHEEL, w.B); Send(two); }
                    else if (w.A != 0) { Set(one, 0, N.MOUSEEVENTF_WHEEL, w.A); Send(one); }
                    else if (w.B != 0) { Set(one, 0, N.MOUSEEVENTF_HWHEEL, w.B); Send(one); }
                    break;

                case Kind.Click:
                    // Arm the watcher before clicking, with the cursor as it was before, so even
                    // an app that switches cursor instantly is seen.
                    if (w.B != 0) N.PostThreadMessage(hookThreadId, WM_ARM_WATCH, (IntPtr)w.A, CurrentCursor());
                    SendClick();
                    DebugLog.Write("press #{0}: sent middle click", w.A);
                    break;

                case Kind.CancelClick:
                    // Only if no new press has started since: then it's still safe to undo.
                    if (pressId == w.A) { SendClick(); DebugLog.Write("press #{0}: sent second click to cancel autoscroll", w.A); }
                    else DebugLog.Write("press #{0}: autoscroll cancel skipped, a new press started", w.A);
                    break;

                case Kind.RawMiddleDown:
                    Set(one, 0, N.MOUSEEVENTF_MIDDLEDOWN, 0);
                    Send(one);
                    break;

                case Kind.RawMiddleUp:
                    Set(one, 0, N.MOUSEEVENTF_MIDDLEUP, 0);
                    Send(one);
                    break;

                case Kind.RawMove:
                    one[0].type = N.INPUT_MOUSE;
                    one[0].mi = new N.MOUSEINPUT { dx = w.A, dy = w.B, dwFlags = N.MOUSEEVENTF_MOVE };
                    Send(one);
                    break;
            }
        }

        void SendClick()
        {
            Set(two, 0, N.MOUSEEVENTF_MIDDLEDOWN, 0);
            Set(two, 1, N.MOUSEEVENTF_MIDDLEUP, 0);
            Send(two);
        }

        static void Set(N.INPUT[] a, int i, uint flags, int data)
        {
            a[i].type = N.INPUT_MOUSE;
            a[i].mi = new N.MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data) };
        }

        static readonly int InputSize = Marshal.SizeOf(typeof(N.INPUT));

        static void Send(N.INPUT[] inputs) { N.SendInput((uint)inputs.Length, inputs, InputSize); }

        // ---- Autoscroll cancel -----------------------------------------------------------
        // Chromium/Electron apps, Office and others enter "sticky" autoscroll on a lone middle
        // click and show their own pan cursor. For a short time after our click, a WinEvent hook
        // reports every cursor shape change as it happens; if the cursor turns into a non-system
        // cursor while the pointer stays put, a second click leaves autoscroll again.

        static readonly int[] SystemCursorIds = { 32512, 32513, 32514, 32515, 32516, 32640, 32641, 32642,
            32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };
        static HashSet<IntPtr> systemCursors;

        static bool IsSystemCursor(IntPtr c)
        {
            var set = systemCursors;
            if (set == null)
            {
                set = new HashSet<IntPtr> { IntPtr.Zero };
                foreach (int id in SystemCursorIds) set.Add(N.LoadCursor(IntPtr.Zero, (IntPtr)id));
                systemCursors = set;
            }
            return set.Contains(c);
        }

        static IntPtr CurrentCursor()
        {
            var ci = new N.CURSORINFO { cbSize = Marshal.SizeOf(typeof(N.CURSORINFO)) };
            return N.GetCursorInfo(ref ci) ? ci.hCursor : IntPtr.Zero;
        }

        void ArmWatch(int id, IntPtr before)
        {
            if (pressId != id) return;         // a new press already started
            watching = true;
            watchId = id;
            watchBefore = before;
            N.GetCursorPos(out watchPos);
            if (winEventHook == IntPtr.Zero)
                winEventHook = N.SetWinEventHook(N.EVENT_OBJECT_NAMECHANGE, N.EVENT_OBJECT_NAMECHANGE, IntPtr.Zero,
                    winEventProc, 0, 0, N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);
            if (watchTimer != UIntPtr.Zero) N.KillTimer(IntPtr.Zero, watchTimer);
            watchTimer = N.SetTimer(IntPtr.Zero, UIntPtr.Zero, WatchMs, IntPtr.Zero);
            DebugLog.Write("press #{0}: autoscroll watch started, cursor before the click 0x{1:X}", id, before.ToInt64());
            CheckCursor();                     // the app may have switched before the hook was in place
        }

        void OnWinEvent(IntPtr h, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject == N.OBJID_CURSOR && watching) CheckCursor();
        }

        void CheckCursor()
        {
            if (pressId != watchId) { DebugLog.Write("autoscroll watch stopped: a new press started"); Disarm(); return; }
            IntPtr now = CurrentCursor();
            if (now == watchBefore) return;
            if (IsSystemCursor(now))
            {
                DebugLog.Write("press #{0}: cursor -> 0x{1:X}, a standard cursor: ignored", watchId, now.ToInt64());
                return;
            }

            N.POINT p;
            // The pointer moved away: the new cursor belongs to whatever it is over now.
            bool moved = N.GetCursorPos(out p) && (Math.Abs(p.x - watchPos.x) > WatchSlop || Math.Abs(p.y - watchPos.y) > WatchSlop);
            DebugLog.Write("press #{0}: cursor -> 0x{1:X}, an app cursor, pointer {2}", watchId, now.ToInt64(),
                           moved ? "moved away: ignored" : "still: cancelling autoscroll");
            if (!moved) output.Add(new Work { Kind = Kind.CancelClick, A = watchId });
            Disarm();
        }

        void Disarm()
        {
            watching = false;
            if (watchTimer != UIntPtr.Zero) { N.KillTimer(IntPtr.Zero, watchTimer); watchTimer = UIntPtr.Zero; }
            if (winEventHook != IntPtr.Zero) { N.UnhookWinEvent(winEventHook); winEventHook = IntPtr.Zero; }
        }
    }
}
