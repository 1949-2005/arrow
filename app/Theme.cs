// Colors, fonts, icons and drawing helpers from docs/NATIVE_UI.md, light and dark, following Windows live.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Arrow
{
    public static class Glyph
    {
        public const char Download = '\uE896', Link = '\uE71B', Add = '\uE710', List = '\uE8FD', Clock = '\uE121', Options = '\uE9E9',
            More = '\uE712', Pause = '\uE769', Play = '\uE768', Stop = '\uE71A', Delete = '\uE74D', Folder = '\uE8B7', OpenFolder = '\uE838',
            OpenFile = '\uE8E5', Copy = '\uE8C8', Search = '\uE721', CheckCircle = '\uE930', Warning = '\uE7BA', Error = '\uE783',
            Clipboard = '\uE77F', Close = '\uE8BB', ChevronDown = '\uE70D', ChevronUp = '\uE70E', CheckMark = '\uE73E',
            Sort = '\uE8CB', View = '\uE8A9', SpeedLimit = '\uEC4A', Up = '\uE74A', Down = '\uE74B';
        public const char All = '\uE8B7', Active = '\uE896', Finished = '\uE930', Failed = '\uE783', Scheduled = '\uE121',
            Video = '\uE714', Music = '\uEC4F', Documents = '\uE8A5', Programs = '\uECAA', Compressed = '\uF012', Other = '\uE7C3';

        // Core.Category names
        public static char Of(string category)
        {
            switch (category)
            {
                case "Video": return Video;
                case "Music": return Music;
                case "Documents": return Documents;
                case "Programs": return Programs;
                case "Compressed": return Compressed;
                default: return Other;
            }
        }
    }

    public static class Theme
    {
        public static bool Dark { get; private set; }
        // Raised when Windows switches between light and dark, on the thread that called Init()
        public static event Action Changed;
        public static readonly float Scale;

        public static Font Body, Bold, Big, Small, SmallBold;
        static readonly string IconFamily;
        static readonly Dictionary<int, Font> iconFonts = new Dictionary<int, Font>();
        static readonly bool forced;

        static Theme()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            var names = new HashSet<string>(FontFamily.Families.Select(f => f.Name));
            Func<string, string, string> pick = (want, fallback) => names.Contains(want) ? want : fallback;
            // Segoe UI, not Segoe UI Variable: Variable has no Arabic, so GDI would draw Arabic in Tahoma
            // Text is semibold 14 px; emphasis is the regular family's Bold style (GDI has no "Segoe UI Bold" family)
            var regular = pick("Segoe UI", "Microsoft Sans Serif");
            var semibold = pick("Segoe UI Semibold", regular);
            Body = new Font(semibold, 10.5f);
            Small = new Font(semibold, 9f);
            Bold = new Font(regular, 10.5f, FontStyle.Bold);
            SmallBold = new Font(regular, 9f, FontStyle.Bold);
            Big = new Font(regular, 18f, FontStyle.Bold);
            IconFamily = pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
            var env = (Environment.GetEnvironmentVariable("ARROW_THEME") ?? "").ToLowerInvariant();
            forced = env == "light" || env == "dark";
            Dark = forced ? env == "dark" : SystemDark();
            if (!forced) SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                var d = SystemDark();
                if (d == Dark) return;
                Dark = d;
                var h = Changed;
                if (h != null) h();
            };
        }

        static bool SystemDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch (Exception) { return false; }
        }

        public static void Init() { } // runs the static constructor

        public static int Px(double v) { return (int)Math.Round(v * Scale); }

        static Color C(int light, int dark) { return Color.FromArgb(unchecked((int)0xFF000000) | (Dark ? dark : light)); }
        public static Color Top { get { return C(0xF6F6F8, 0x1E1E20); } }
        public static Color Sidebar { get { return C(0xEFEFF2, 0x252528); } }
        public static Color List { get { return C(0xFFFFFF, 0x1B1B1D); } }
        public static Color Inspector { get { return C(0xF7F7F9, 0x222225); } }
        public static Color Field { get { return C(0xFFFFFF, 0x252528); } }
        public static Color Line { get { return C(0xDCDCE0, 0x3A3A3E); } }
        public static Color Text { get { return C(0x1D1D1F, 0xF5F5F7); } }
        public static Color Muted { get { return C(0x6E6E73, 0xA1A1A6); } }
        public static Color Faint { get { return C(0x8E8E93, 0x8E8E93); } }
        public static Color Hover { get { return C(0xF0F0F3, 0x26262A); } }
        public static Color Soft { get { return C(0xE2E2E6, 0x38383C); } }
        public static Color Sel { get { return C(0x3B3C3D, 0x505154); } }
        public static Color OnSel { get { return C(0xFFFFFF, 0xFFFFFF); } }
        public static Color Accent { get { return C(0x3B3C3D, 0xECECEE); } }
        public static Color OnAccent { get { return C(0xFFFFFF, 0x1D1D1F); } }
        public static Color Bar { get { return C(0xE2E2E7, 0x3A3A3E); } }
        public static Color Done { get { return C(0x1E8E3E, 0x30D158); } }
        public static Color Err { get { return C(0xD70015, 0xFF453A); } }
        public static Color Warn { get { return C(0xB45309, 0xFFB340); } }

        // File-type color of a Core.Category name
        public static Color Category(string category)
        {
            switch (category)
            {
                case "Video": return C(0xAF52DE, 0xBF5AF2);
                case "Music": return C(0xE0245E, 0xFF375F);
                case "Documents": return C(0x0A6CDB, 0x4DA3FF);
                case "Programs": return C(0x5856D6, 0x7D7AFF);
                case "Compressed": return C(0xD97706, 0xFFB340);
                default: return C(0x8E8E93, 0x98989D);
            }
        }

        // A color at 14% (light) or 22% (dark) over a surface, as an opaque color: tiles
        public static Color Tint(Color c, Color surface) { return Mix(surface, c, Dark ? 0.22 : 0.14); }
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, RectangleF r, float radius, Color c)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = mode;
        }

        // A width-px border drawn inside r
        public static void Border(Graphics g, RectangleF r, float radius, Color c, float width)
        {
            var mode = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var h = width / 2;
            using (var p = Round(new RectangleF(r.X + h, r.Y + h, r.Width - width, r.Height - width), Math.Max(0, radius - h)))
            using (var pen = new Pen(c, width)) g.DrawPath(pen, p);
            g.SmoothingMode = mode;
        }

        // The keyboard focus ring: 2 px accent, 1 px inside the control
        public static void FocusRing(Graphics g, Rectangle r, float radius)
        {
            var i = Px(1);
            Border(g, new RectangleF(r.X + i, r.Y + i, r.Width - 2 * i, r.Height - 2 * i), Math.Max(0, radius - i), Accent, Px(2));
        }

        public static void HLine(Graphics g, int x1, int x2, int y) { using (var p = new Pen(Line)) g.DrawLine(p, x1, y, x2, y); }
        public static void VLine(Graphics g, int x, int y1, int y2) { using (var p = new Pen(Line)) g.DrawLine(p, x, y1, x, y2); }

        public const TextFormatFlags Line1 = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        public const TextFormatFlags Line1Right = (Line1 & ~TextFormatFlags.Left) | TextFormatFlags.Right;
        public const TextFormatFlags Wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

        // GDI has no color fonts: text with emoji is drawn and measured by ColorText (DirectWrite), the rest by TextRenderer
        public static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            s = s ?? "";
            if (!Core.HasEmoji(s) || !ColorText.Draw(g, s, f, r, c, flags)) TextRenderer.DrawText(g, s, f, r, c, flags);
        }
        public static void DrawText(Graphics g, string s, Font f, Rectangle r, Color c) { DrawText(g, s, f, r, c, Line1); }
        public static Size Measure(string s, Font f)
        {
            s = s ?? "";
            var size = Core.HasEmoji(s) ? ColorText.Measure(s, f, int.MaxValue, Line1) : null;
            return size ?? GdiMeasure(s, f, new Size(int.MaxValue, int.MaxValue), Line1);
        }
        public static int MeasureHeight(string s, Font f, int width)
        {
            if (string.IsNullOrEmpty(s)) s = " ";
            var size = Core.HasEmoji(s) ? ColorText.Measure(s, f, width, Wrap) : null;
            return (size ?? GdiMeasure(s, f, new Size(width, int.MaxValue), Wrap)).Height;
        }
        // A name shown whole in a row: the height of its block, line (a one-line name) or its wrapped lines with the same
        // space above and below as a line has
        public static int NameHeight(string s, Font f, int width, int line)
        {
            var h = MeasureHeight(s, f, Math.Max(1, width));
            return h < f.Height * 3 / 2 ? line : h + Math.Max(0, line - f.Height);
        }
        // Draws it: block from NameHeight, line = the first line's rectangle (the row's other cells share it)
        public static void DrawName(Graphics g, string s, Font f, Rectangle line, int block, Color c)
        {
            if (block <= line.Height) { DrawText(g, s, f, line, c); return; }
            var above = Math.Max(0, line.Height - f.Height) / 2;
            DrawText(g, s, f, new Rectangle(line.X, line.Y + above, line.Width, block - 2 * above), c, Wrap);
        }

        // With a device context: TextRenderer's overload without one ignores NoPadding and adds about 9 px, which pushed
        // centered button labels 4 px to the left
        static Size GdiMeasure(string s, Font f, Size proposed, TextFormatFlags flags)
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) return TextRenderer.MeasureText(g, s, f, proposed, flags);
        }

        // An icon from Segoe Fluent Icons (or MDL2) centered in r; px is the size at 96 DPI
        public static void DrawGlyph(Graphics g, char glyph, Rectangle r, Color c, int px)
        {
            Font f;
            lock (iconFonts)
                if (!iconFonts.TryGetValue(px, out f)) iconFonts[px] = f = new Font(IconFamily, Px(px), GraphicsUnit.Pixel);
            // grayscale antialiasing: ClearType fringes thin icon strokes with color
            var hint = g.TextRenderingHint;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(glyph.ToString(), f, b, r, sf);
            g.TextRenderingHint = hint;
        }

        // File-type tile: radius 12 (scaled with its size), category tint over the surface, glyph in the category color
        public static void DrawTile(Graphics g, Rectangle r, string category, Color surface)
        {
            var c = Category(category);
            Fill(g, r, Px(12) * r.Width / (float)Px(48), Tint(c, surface));
            DrawGlyph(g, Glyph.Of(category), r, c, r.Width >= Px(40) ? 24 : 16);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        // Dark or light title bar (Windows 10 2004+ uses 20, older builds 19)
        public static void TitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            var on = Dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);
            SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x37); // SWP_FRAMECHANGED | NOMOVE | NOSIZE | NOZORDER | NOACTIVATE: repaint the frame now
        }

        // Native scrollbars (and combo boxes / edits with cfd) in the current theme
        public static void Native(Control c, bool cfd)
        {
            if (c.IsHandleCreated) SetWindowTheme(c.Handle, Dark ? (cfd ? "DarkMode_CFD" : "DarkMode_Explorer") : "Explorer", null);
        }

        public static readonly ToolStripRenderer Menus = new MenuRenderer();
    }

    // Context menus in the theme: flat surface, line border, rounded hover, hairline separators
    class MenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Inspector)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(Point.Empty, e.ToolStrip.Size);
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Rectangle(Theme.Px(4), 0, e.Item.Width - Theme.Px(8), e.Item.Height);
            Theme.Fill(e.Graphics, r, Theme.Px(4), Theme.Hover);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            Theme.HLine(e.Graphics, Theme.Px(8), e.Item.Width - Theme.Px(8), y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Theme.DrawGlyph(e.Graphics, Glyph.CheckMark, e.ImageRectangle, Theme.Text, 12);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Muted;
            base.OnRenderArrow(e);
        }
    }
}
