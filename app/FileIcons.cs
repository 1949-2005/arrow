// The icon Windows shows for a file type (File Explorer, IDM), from the extension alone: SHGFI_USEFILEATTRIBUTES never
// touches the disk. Cached per extension and size; callers draw their glyph when Draw returns false.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Arrow
{
    public static class FileIcons
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
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
            var info = new SHFILEINFO();
            var h = IntPtr.Zero;
            try
            {
                // the small (16 px) image up to 16 px, else the large one (32 px at 96 DPI), scaled down
                var flags = SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                if (SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
                h = info.hIcon;
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
