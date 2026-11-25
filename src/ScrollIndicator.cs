// Optional scroll-direction indicator shown on (or next to) the pinned pointer while scrolling.
// A tiny click-through, non-activating topmost overlay drawn with per-pixel alpha
// (UpdateLayeredWindow), so its edges and shadow are smooth over anything behind it.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using N = NubMouse.Native;

namespace NubMouse
{
    sealed class ScrollIndicator : Form
    {
        const int WS_EX_LAYERED = 0x00080000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int WS_EX_TOPMOST = 0x00000008;
        const int WS_EX_NOACTIVATE = 0x08000000;

        readonly float dpiScale;
        bool shown;
        // The last drawing, reused while the look doesn't change.
        Bitmap art;
        ScrollMode artMode;
        int artStyle = -1, artSize = -1;

        public ScrollIndicator()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            float dpi = 96f;
            try { using (var g = CreateGraphics()) dpi = g.DpiX; } catch (Exception) { }
            dpiScale = dpi / 96f;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        /// <paramref name="offset"/> is the gap from the pointer to the indicator in pixels at
        /// 100 % scaling; 0 centres the indicator on the pointer and hides the pointer meanwhile.
        public void UpdateState(ScrollMode mode, int cursorX, int cursorY, int offset, int style, int sizeIndex)
        {
            if (IsDisposed) return;
            if (mode == ScrollMode.None)
            {
                if (shown && IsHandleCreated) N.SetWindowPos(Handle, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE | N.SWP_HIDEWINDOW);
                shown = false;
                PointerHider.Restore();
                return;
            }
            if (offset <= 0) PointerHider.Hide();
            else PointerHider.Restore();

            if (art == null || artMode != mode || artStyle != style || artSize != sizeIndex)
            {
                if (art != null) art.Dispose();
                art = IndicatorArt.Render(mode, style, IndicatorArt.Diameter(sizeIndex, dpiScale));
                artMode = mode; artStyle = style; artSize = sizeIndex;
            }
            int canvas = art.Width;

            var area = Screen.FromPoint(new Point(cursorX, cursorY)).Bounds;
            int x, y;
            if (offset <= 0)
            {
                x = cursorX - canvas / 2;
                y = cursorY - canvas / 2;
            }
            else
            {
                // Below and to the right of the pointer, flipped near the right/bottom screen edge.
                int gap = (int)Math.Round(offset * dpiScale);
                x = cursorX + gap;
                y = cursorY + gap;
                if (x + canvas > area.Right) x = cursorX - gap - canvas;
                if (y + canvas > area.Bottom) y = cursorY - gap - canvas;
            }
            if (x < area.Left) x = area.Left;
            if (y < area.Top) y = area.Top;
            if (x + canvas > area.Right) x = area.Right - canvas;
            if (y + canvas > area.Bottom) y = area.Bottom - canvas;

            if (!IsHandleCreated) CreateHandle();
            Present(art, x, y);
            N.SetWindowPos(Handle, N.HWND_TOPMOST, 0, 0, 0, 0, N.SWP_NOMOVE | N.SWP_NOSIZE | N.SWP_NOACTIVATE | N.SWP_SHOWWINDOW);
            shown = true;
        }

        // ---- Layered window plumbing --------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }

        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT dst, ref SZ size, IntPtr hdcSrc, ref PT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        void Present(Bitmap bmp, int x, int y)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            try
            {
                var dst = new PT { X = x, Y = y };
                var size = new SZ { W = bmp.Width, H = bmp.Height };
                var src = new PT();
                var blend = new BLENDFUNCTION { Op = 0 /* AC_SRC_OVER */, Alpha = 255, Format = 1 /* AC_SRC_ALPHA */ };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbmp);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void Dispose(bool disposing)
        {
            PointerHider.Restore();
            if (disposing && art != null) art.Dispose();
            base.Dispose(disposing);
        }
    }

    /// Draws the indicator. Shared by the overlay and the settings window's preview.
    static class IndicatorArt
    {
        public static readonly string[] StyleLabels = { "Dark", "Light", "Minimal", "TrackPoint" };
        /// Labels for Settings.IndicatorSizePresets.
        public static readonly string[] SizeLabels = { "Tiny", "Small", "Medium", "Large" };

        static readonly Color TrackPointRed = Color.FromArgb(226, 35, 26);

        /// Screen pixels for a size given in px at 100 % scaling.
        public static int Diameter(int size, float dpiScale)
        {
            size = Math.Max(Settings.IndicatorSizeMin, Math.Min(Settings.IndicatorSizeMax, size));
            return (int)Math.Round(size * dpiScale);
        }

        /// A transparent bitmap with room for the shadow around a <paramref name="d"/>-pixel indicator.
        public static Bitmap Render(ScrollMode mode, int style, int d)
        {
            int pad = Math.Max(3, (int)Math.Round(d * 0.16));
            int canvas = d + 2 * pad;
            var bmp = new Bitmap(canvas, canvas, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, new RectangleF(pad, pad, d, d), mode, style);
            }
            return bmp;
        }

        public static void Draw(Graphics g, RectangleF r, ScrollMode mode, int style)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = r.Width, cx = r.X + s / 2f, cy = r.Y + s / 2f;
            bool vertical = mode == ScrollMode.Vertical || mode == ScrollMode.Both;
            bool horizontal = mode == ScrollMode.Horizontal || mode == ScrollMode.Both;

            switch (style)
            {
                case 1:   // Light: white disc, dark arrows
                    Shadow(g, cx, cy, s / 2f, 0.16f);
                    Disc(g, cx, cy, s / 2f, Color.FromArgb(250, 252, 252, 253), Color.FromArgb(250, 238, 238, 241), Color.FromArgb(70, 0, 0, 0));
                    Dot(g, cx, cy, s * 0.17f, TrackPointRed, Color.FromArgb(60, 0, 0, 0));
                    Chevrons(g, cx, cy, s, vertical, horizontal, Color.FromArgb(235, 45, 46, 52), null);
                    break;
                case 2:   // Minimal: just the arrows and the dot, outlined so they read on any background
                    Dot(g, cx, cy, s * 0.17f, TrackPointRed, Color.FromArgb(230, 255, 255, 255));
                    Chevrons(g, cx, cy, s * 1.25f, vertical, horizontal, Color.FromArgb(245, 255, 255, 255), Color.FromArgb(200, 20, 20, 24));
                    break;
                case 3:   // TrackPoint: a red nub with dark arrows around it
                    Shadow(g, cx, cy, s * 0.17f, 0.22f);
                    Nub(g, cx, cy, s * 0.17f);
                    Chevrons(g, cx, cy, s * 1.25f, vertical, horizontal, Color.FromArgb(245, 40, 40, 44), Color.FromArgb(220, 255, 255, 255));
                    break;
                default:  // Dark: charcoal disc, white arrows, red dot
                    Shadow(g, cx, cy, s / 2f, 0.22f);
                    Disc(g, cx, cy, s / 2f, Color.FromArgb(240, 58, 58, 64), Color.FromArgb(240, 30, 30, 34), Color.FromArgb(110, 255, 255, 255));
                    Dot(g, cx, cy, s * 0.17f, TrackPointRed, Color.FromArgb(90, 0, 0, 0));
                    Chevrons(g, cx, cy, s, vertical, horizontal, Color.FromArgb(245, 245, 245, 248), null);
                    break;
            }
        }

        /// A soft drop shadow: a few widening, fading circles a little below the shape.
        static void Shadow(Graphics g, float cx, float cy, float r, float strength)
        {
            float drop = Math.Max(1f, r * 0.08f);
            for (int i = 4; i >= 1; i--)
            {
                float rr = r + i * Math.Max(1f, r * 0.05f);
                using (var b = new SolidBrush(Color.FromArgb((int)(255 * strength / (i + 1)), 0, 0, 0)))
                    g.FillEllipse(b, cx - rr, cy - rr + drop, 2 * rr, 2 * rr);
            }
        }

        static void Disc(Graphics g, float cx, float cy, float r, Color top, Color bottom, Color rim)
        {
            var rect = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (var fill = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical))
                g.FillEllipse(fill, rect);
            float w = Math.Max(1f, r / 14f);
            using (var pen = new Pen(rim, w))
                g.DrawEllipse(pen, cx - r + w / 2, cy - r + w / 2, 2 * r - w, 2 * r - w);
        }

        static void Dot(Graphics g, float cx, float cy, float d, Color fill, Color ring)
        {
            float r = d / 2f, w = Math.Max(1f, d / 6f);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, cx - r, cy - r, d, d);
            using (var p = new Pen(ring, w)) g.DrawEllipse(p, cx - r - w / 2, cy - r - w / 2, d + w, d + w);
        }

        /// The TrackPoint nub: red with a slight highlight, like the real cap.
        static void Nub(Graphics g, float cx, float cy, float r)
        {
            var rect = new RectangleF(cx - r, cy - r, 2 * r, 2 * r);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var b = new PathGradientBrush(path))
                {
                    b.CenterPoint = new PointF(cx - r * 0.3f, cy - r * 0.35f);
                    b.CenterColor = Color.FromArgb(255, 244, 92, 80);
                    b.SurroundColors = new[] { Color.FromArgb(255, 176, 22, 18) };
                    g.FillEllipse(b, rect);
                }
            }
            using (var p = new Pen(Color.FromArgb(160, 90, 10, 8), Math.Max(1f, r / 7f)))
                g.DrawEllipse(p, rect);
        }

        static void Chevrons(Graphics g, float cx, float cy, float s, bool vertical, bool horizontal, Color ink, Color? outline)
        {
            float arm = s * 0.13f, dist = s * 0.31f, w = Math.Max(1.6f, s / 13f);
            var shapes = new System.Collections.Generic.List<PointF[]>();
            if (vertical)
            {
                shapes.Add(new[] { new PointF(cx - arm, cy - dist + arm * 0.75f), new PointF(cx, cy - dist), new PointF(cx + arm, cy - dist + arm * 0.75f) });
                shapes.Add(new[] { new PointF(cx - arm, cy + dist - arm * 0.75f), new PointF(cx, cy + dist), new PointF(cx + arm, cy + dist - arm * 0.75f) });
            }
            if (horizontal)
            {
                shapes.Add(new[] { new PointF(cx - dist + arm * 0.75f, cy - arm), new PointF(cx - dist, cy), new PointF(cx - dist + arm * 0.75f, cy + arm) });
                shapes.Add(new[] { new PointF(cx + dist - arm * 0.75f, cy - arm), new PointF(cx + dist, cy), new PointF(cx + dist - arm * 0.75f, cy + arm) });
            }
            if (outline.HasValue)
                using (var pen = new Pen(outline.Value, w * 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    foreach (var p in shapes) g.DrawLines(pen, p);
            using (var pen = new Pen(ink, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                foreach (var p in shapes) g.DrawLines(pen, p);
        }
    }
}


namespace NubMouse
{
    /// Hides the mouse pointer everywhere by swapping the system cursors for a blank one, and puts
    /// the user's cursors back (SPI_SETCURSORS reloads their scheme). A registry flag marks the
    /// swap, so if NubMouse is killed while the pointer is hidden, the next start restores it.
    static class PointerHider
    {
        const string KeyPath = @"Software\NubMouse", FlagName = "PointerHidden";
        const uint SPI_SETCURSORS = 0x0057;
        // Normal, I-beam, wait, cross, up arrow, the size arrows, no, hand, app starting, help, pen.
        static readonly uint[] Cursors = { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32631 };
        static bool hidden;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern System.IntPtr CreateCursor(System.IntPtr hInst, int xHot, int yHot, int w, int h, byte[] andPlane, byte[] xorPlane);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetSystemCursor(System.IntPtr cursor, uint id);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SystemParametersInfo(uint action, uint param, System.IntPtr pv, uint winIni);

        public static void Hide()
        {
            if (hidden) return;
            hidden = true;
            SetFlag(true);
            var and = new byte[32 * 4];   // 32x32: AND mask all 1s and XOR mask all 0s = transparent
            for (int i = 0; i < and.Length; i++) and[i] = 0xFF;
            var xor = new byte[32 * 4];
            foreach (uint id in Cursors)
                SetSystemCursor(CreateCursor(System.IntPtr.Zero, 0, 0, 32, 32, and, xor), id);   // takes ownership of the cursor
        }

        public static void Restore()
        {
            if (!hidden) return;
            hidden = false;
            SystemParametersInfo(SPI_SETCURSORS, 0, System.IntPtr.Zero, 0);
            SetFlag(false);
        }

        /// At startup: put the cursors back if a previous run left them hidden.
        public static void RecoverAfterCrash()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                    if (k == null || !(k.GetValue(FlagName) is int) || (int)k.GetValue(FlagName) == 0) return;
            }
            catch (System.Exception) { return; }
            SystemParametersInfo(SPI_SETCURSORS, 0, System.IntPtr.Zero, 0);
            SetFlag(false);
        }

        static void SetFlag(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (on) k.SetValue(FlagName, 1, Microsoft.Win32.RegistryValueKind.DWord);
                    else k.DeleteValue(FlagName, false);
                }
            }
            catch (System.Exception) { }
        }
    }
}
