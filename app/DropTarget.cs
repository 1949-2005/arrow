// IDM's drop target: a small tile that stays on top of every window. Links and files dropped on it are added like a
// drop on the main window; it shows the logo and, while something downloads, the total speed. Drag it anywhere (its
// place is kept in ui.json), double-click it to open the main window, right-click for its menu.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Glide
{
    public class DropTarget : Form
    {
        readonly Func<IDataObject, string> dropText;
        readonly Action<string> dropped;
        string speed = "";
        bool hot;

        public event Action Opened, Hidden, Moved;

        public DropTarget(Func<IDataObject, string> dropText, Action<string> dropped)
        {
            this.dropText = dropText;
            this.dropped = dropped;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(Theme.Px(64), Theme.Px(64));
            AllowDrop = true;
            DoubleBuffered = true;
            Text = Program.Name + " drop target";
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - Theme.Px(24), wa.Bottom - Height - Theme.Px(24));
            using (var p = Round(new Rectangle(Point.Empty, Size), Theme.Px(14))) Region = new Region(p);
            var menu = new ContextMenuStrip { Renderer = Theme.Menus, ShowImageMargin = false, Font = Theme.Body };
            menu.Items.Add("Open " + Program.Name, null, (s, e) => { var h = Opened; if (h != null) h(); });
            menu.Items.Add("Hide drop target", null, (s, e) => { var h = Hidden; if (h != null) h(); });
            ContextMenuStrip = menu;
            Theme.Changed += Invalidate;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
                var p = base.CreateParams;
                p.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; // not in Alt+Tab, never takes the focus from the app in front
                return p;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        // The total speed while downloads run, "" when none does
        public string Speed { set { if (speed == value) return; speed = value ?? ""; Invalidate(); } }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            var d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Round(r, Theme.Px(14)))
            {
                using (var b = new SolidBrush(hot ? Theme.Soft : Theme.Top)) g.FillPath(b, p);
                using (var pen = new Pen(hot ? Theme.Text : Theme.Line, hot ? 2 : 1)) g.DrawPath(pen, p);
            }
            var logo = AppIcon.Load(Theme.Px(32));
            var top = speed == "" ? (Height - Theme.Px(32)) / 2 : Theme.Px(8);
            if (logo != null) g.DrawIcon(logo, new Rectangle((Width - Theme.Px(32)) / 2, top, Theme.Px(32), Theme.Px(32)));
            if (speed != "") Theme.DrawText(g, speed, Theme.Small, new Rectangle(0, top + Theme.Px(34), Width, Theme.Px(16)), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { using (var b = new SolidBrush(Theme.Top)) e.Graphics.FillRectangle(b, ClientRectangle); }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);

        // Dragged by any point: Windows moves it as by a title bar
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            // the second click of a double-click arrives here (the tile has no title bar to get WM_NCLBUTTONDBLCLK)
            if (e.Clicks > 1) { var h = Opened; if (h != null) h(); return; }
            const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_NCLBUTTONDBLCLK = 0xA3, WM_EXITSIZEMOVE = 0x232;
            if (m.Msg == WM_NCLBUTTONDBLCLK) { var h = Opened; if (h != null) h(); return; }
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE) { var h = Moved; if (h != null) h(); }
        }

        protected override void OnDragEnter(DragEventArgs e)
        {
            var ok = dropText(e.Data) != null;
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok != hot) { hot = ok; Invalidate(); }
            base.OnDragEnter(e);
        }

        protected override void OnDragLeave(EventArgs e) { hot = false; Invalidate(); base.OnDragLeave(e); }

        protected override void OnDragDrop(DragEventArgs e)
        {
            hot = false;
            Invalidate();
            var text = dropText(e.Data);
            if (text != null) dropped(text);
            base.OnDragDrop(e);
        }

        protected override void Dispose(bool disposing)
        {
            Theme.Changed -= Invalidate;
            base.Dispose(disposing);
        }
    }
}
