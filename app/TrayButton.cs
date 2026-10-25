// The progress window's Minimize to tray button: the tray icon's three arrows side by side, left of the title bar's
// minimize. Windows
// lets no app add a caption button, so this is a small owned layered window over the title bar, drawn with per-pixel
// alpha so the bar's own color shows through. It follows its owner and hides while the owner is hidden or minimized.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Arrow
{
    public class TrayButton : Form
    {
        readonly Form owner;
        readonly ToolTip tip = new ToolTip();
        readonly Action onTheme;
        bool wanted, hover, pressed;
        public event Action Pressed;

        public TrayButton(Form owner)
        {
            this.owner = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Owner = owner;
            tip.SetToolTip(this, "Minimize to tray");
            EventHandler place = (s, e) => Place();
            owner.Move += place;
            owner.Resize += place;
            owner.VisibleChanged += place;
            owner.Activated += (s, e) => Draw();
            owner.Deactivate += (s, e) => Draw();
            onTheme = Draw;
            Theme.Changed += onTheme;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                var p = base.CreateParams;
                p.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return p;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // Shown while this is true and its owner is shown and not minimized (the download can go to the tray)
        public bool Wanted { set { wanted = value; Place(); } }

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        // One caption button's width left of the minimize, minimize, maximize, close being DWMWA_CAPTION_BUTTON_BOUNDS
        void Place()
        {
            if (IsDisposed || owner.IsDisposed) return;
            RECT w, b;
            var show = wanted && owner.Visible && owner.WindowState == FormWindowState.Normal && owner.IsHandleCreated
                && GetWindowRect(owner.Handle, out w) && DwmGetWindowAttribute(owner.Handle, 5, out b, Marshal.SizeOf(typeof(RECT))) == 0 && b.R > b.L;
            if (!show) { if (Visible) Hide(); return; }
            GetWindowRect(owner.Handle, out w);
            DwmGetWindowAttribute(owner.Handle, 5, out b, Marshal.SizeOf(typeof(RECT)));
            var bw = (b.R - b.L) / 3;
            Bounds = new Rectangle(w.L + b.L - bw, w.T + b.T, bw, b.B - b.T);
            if (!Visible) Show();
            Draw();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Draw(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Draw(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Draw(); } base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var click = pressed && ClientRectangle.Contains(e.Location);
            pressed = false;
            Draw();
            base.OnMouseUp(e);
            var h = Pressed;
            if (click && h != null) h();
        }

        // The caption glyphs' look: 1 px lines, white on a dark title bar, black on a light one, dimmer while the owner
        // is inactive; hover and press tint the button as Windows tints its own
        void Draw()
        {
            if (!Visible || Width <= 0 || Height <= 0) return;
            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 0 would let the clicks through to the title bar
                    Paint(g, new Rectangle(0, 0, Width, Height), Theme.Dark, Form.ActiveForm == owner, hover, pressed);
                }
                Present(bmp);
            }
        }

        // The button in r: also for pictures of it (ARROW_SHOTS, reviews): the tray icon's three arrows side by side, apart
        // and stepping down like stairs,
        // all in its grey and still (the download may not have started yet)
        public static void Paint(Graphics g, Rectangle r, bool dark, bool active, bool hover, bool pressed)
        {
            var ink = dark ? Color.White : Color.Black;
            if (hover || pressed) using (var tint = new SolidBrush(Color.FromArgb(pressed ? 36 : 22, ink))) g.FillRectangle(tint, r);
            var grey = dark ? Color.FromArgb(0xD8, 0xD8, 0xDC) : Color.FromArgb(0x3B, 0x3C, 0x3D);
            if (!active) grey = Color.FromArgb(150, grey);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float u = 1.1f * Theme.Scale, cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, step = 8.2f * u;
            using (var pen = new Pen(grey, 1.6f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                for (var i = -1; i <= 1; i++)
                {
                    float x = cx + i * step, y = cy - 1.15f * u + i * 2.6f * u; // each a step lower: stairs going down
                    g.DrawLines(pen, new[] { new PointF(x - 2.3f * u, y), new PointF(x, y + 2.3f * u), new PointF(x + 2.3f * u, y) });
                }
        }

        // Three chevrons pointing down, stacked and centered on c, in units of u pixels: each 4.6 wide, its stroke 1.6;
        // step: from one to the next (3.6: touching, as in the tray). The frame's one is lit, the others dim (-1: none)
        public static void Chevrons(Graphics g, PointF c, float u, float step, Color color, int frame)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = c.Y - (2 * step + 2.3f) * u / 2;
            for (var i = 0; i < 3; i++)
                using (var pen = new Pen(i == frame ? color : Color.FromArgb(110 * color.A / 255, color), 1.6f * u))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    var y = top + i * step * u;
                    g.DrawLines(pen, new[] { new PointF(c.X - 2.3f * u, y), new PointF(c.X, y + 2.3f * u), new PointF(c.X + 2.3f * u, y) });
                }
        }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref POINT at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        void Present(Bitmap bmp)
        {
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var hbmp = bmp.GetHbitmap(Color.FromArgb(0)); // premultiplied, with its alpha
            var old = SelectObject(mem, hbmp);
            try
            {
                var at = new POINT { X = Left, Y = Top };
                var size = new SIZE { W = bmp.Width, H = bmp.Height };
                var from = new POINT();
                var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
                UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2); // ULW_ALPHA
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
            Theme.Changed -= onTheme;
            if (disposing) tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
