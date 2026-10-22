// The icon Windows shows for a file type (File Explorer, IDM), from the extension alone: SHGFI_USEFILEATTRIBUTES never
// touches the disk. Cached per extension and size; callers draw their glyph when Draw returns false. The sidebar's Types
// get the user's SVG icons instead (DrawType), filled in the text color.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Arrow
{
    public static class FileIcons
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        static readonly Dictionary<string, GraphicsPath> shapes = new Dictionary<string, GraphicsPath>();

        // Windows' icon for ext ("" = the generic file) at px pixels at 96 DPI; null when Windows gives none
        public static Bitmap Get(string ext, int px)
        {
            var size = Theme.Px(px);
            var key = ext + "|" + size;
            lock (cache)
            {
                Bitmap b;
                if (!cache.TryGetValue(key, out b)) cache[key] = b = Load(ext, size);
                return b;
            }
        }

        // assets/types/<category>.svg in a px square centered in r; false when it is missing or unreadable (Other has none)
        public static bool DrawType(Graphics g, string category, Rectangle r, int px)
        {
            try
            {
                GraphicsPath path;
                lock (shapes)
                    if (!shapes.TryGetValue(category, out path)) shapes[category] = path = Shape(category);
                if (path == null) return false;
                var size = Theme.Px(px);
                var state = g.Save();
                try
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TranslateTransform(r.X + (r.Width - size) / 2f, r.Y + (r.Height - size) / 2f);
                    g.ScaleTransform(size, size);
                    // the text color at paint time: the theme switches live
                    using (var brush = new SolidBrush(Theme.Text)) g.FillPath(brush, path);
                }
                finally { g.Restore(state); }
                return true;
            }
            catch (Exception) { return false; }
        }

        static GraphicsPath Shape(string category)
        {
            try
            {
                var svg = Core.ParseSvg(File.ReadAllText(Path.Combine(Program.Folder, "assets", "types", category.ToLowerInvariant() + ".svg")));
                if (svg == null) return null;
                // the files are even-odd
                var path = new GraphicsPath(FillMode.Alternate);
                var at = PointF.Empty;
                var start = at;
                foreach (var s in svg.Steps)
                {
                    var a = s.Args;
                    switch (s.Op)
                    {
                        case 'M': path.StartFigure(); start = at = new PointF((float)a[0], (float)a[1]); break;
                        case 'L': var to = new PointF((float)a[0], (float)a[1]); path.AddLine(at, to); at = to; break;
                        case 'C':
                            var end = new PointF((float)a[4], (float)a[5]);
                            path.AddBezier(at, new PointF((float)a[0], (float)a[1]), new PointF((float)a[2], (float)a[3]), end);
                            at = end;
                            break;
                        // after Z the pen is back at the figure's start
                        case 'Z': path.CloseFigure(); at = start; break;
                    }
                }
                // the viewBox becomes a 1 x 1 square, scaled to the icon size when drawn
                using (var m = new Matrix()) { m.Scale((float)(1 / svg.Width), (float)(1 / svg.Height)); path.Transform(m); }
                return path;
            }
            catch (Exception) { return null; }
        }

        // Centered in r; false when there is no icon
        public static bool Draw(Graphics g, string ext, Rectangle r, int px)
        {
            var b = Get(ext, px);
            if (b == null) return false;
            g.DrawImage(b, new Rectangle(r.X + (r.Width - b.Width) / 2, r.Y + (r.Height - b.Height) / 2, b.Width, b.Height));
            return true;
        }

        static Bitmap Load(string ext, int size)
        {
            var h = IntPtr.Zero;
            try
            {
                // the small (16 px) image up to 16 px, else the large one (32 px at 96 DPI), scaled down
                var flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                h = ExtIcon(ext, flags);
                if (h == IntPtr.Zero) return null;
                using (var icon = Icon.FromHandle(h))
                using (var src = icon.ToBitmap())
                {
                    var b = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.CompositingQuality = CompositingQuality.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, size, size));
                    }
                    return b;
                }
            }
            catch (Exception) { return null; }
            finally { if (h != IntPtr.Zero) DestroyIcon(h); }
        }

        static IntPtr ExtIcon(string ext, uint flags)
        {
            var info = new SHFILEINFO();
            return SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
        }

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);
    }
}
