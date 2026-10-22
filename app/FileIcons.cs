// The icon Windows shows for a file type (File Explorer, IDM), from the extension alone: SHGFI_USEFILEATTRIBUTES never
// touches the disk. The sidebar's Types get Windows' own icon for the kind instead (DrawType), or the user's picture for
// Programs and Other. Cached per extension or kind and size; callers draw their glyph when Draw returns false.
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
        // Windows' icon for ext ("" = the generic file), or for "*" + a category's kind, at px pixels at 96 DPI; null when
        // Windows gives none
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

        // A category's kind, the same on every PC: a typical file's icon comes from whatever app opens that type (on one
        // PC a .pdf showed the Claude app's logo for Documents)
        public static bool DrawType(Graphics g, string category, Rectangle r, int px)
        {
            if (category != "Programs" && category != "Other") return Draw(g, "*" + category, r, px);
            var b = Get("*" + category, px);
            if (b == null) return false;
            // black on transparent: keep the alpha, paint it in the text color, here since the theme can switch
            var c = Theme.Text;
            var m = new ColorMatrix(new float[][] {
                new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { c.R / 255f, c.G / 255f, c.B / 255f, 0, 1 } });
            using (var a = new ImageAttributes())
            {
                a.SetColorMatrix(m);
                g.DrawImage(b, new Rectangle(r.X + (r.Width - b.Width) / 2, r.Y + (r.Height - b.Height) / 2, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, a);
            }
            return true;
        }

        // Centered in r; false when there is no icon
        public static bool Draw(Graphics g, string ext, Rectangle r, int px)
        {
            var b = Get(ext, px);
            if (b == null) return false;
            g.DrawImage(b, new Rectangle(r.X + (r.Width - b.Width) / 2, r.Y + (r.Height - b.Height) / 2, b.Width, b.Height));
            return true;
        }

        static Bitmap Load(string key, int size)
        {
            if (key == "*Programs" || key == "*Other") return Picture(key.Substring(1).ToLowerInvariant(), size);
            var h = IntPtr.Zero;
            try
            {
                // the small (16 px) image up to 16 px, else the large one (32 px at 96 DPI), scaled down
                var flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                h = key.StartsWith("*") ? KindIcon(key.Substring(1), flags) : ExtIcon(key, flags);
                if (h == IntPtr.Zero) return null;
                using (var icon = Icon.FromHandle(h))
                using (var src = icon.ToBitmap()) return Scale(src, size);
            }
            catch (Exception) { return null; }
            finally { if (h != IntPtr.Zero) DestroyIcon(h); }
        }

        // assets/types/<name>.png next to the exe; read into memory so the file isn't locked. null when missing or unreadable
        static Bitmap Picture(string name, int size)
        {
            try
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(Path.Combine(Program.Folder, "assets", "types", name + ".png"))))
                using (var src = new Bitmap(ms)) return Scale(src, size);
            }
            catch (Exception) { return null; }
        }

        static Bitmap Scale(Image src, int size)
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

        static IntPtr ExtIcon(string ext, uint flags)
        {
            var info = new SHFILEINFO();
            return SHGetFileInfo("x" + ext, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_USEFILEATTRIBUTES) == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
        }

        // Windows' Videos, Music and Documents folders, and a .zip file's icon (on most PCs the archiver's)
        static IntPtr KindIcon(string category, uint flags)
        {
            switch (category)
            {
                case "Video": return FolderIcon("18989B1D-99B5-455B-841C-AB7C74E4DDFC", flags);
                case "Music": return FolderIcon("4BD8D571-6D19-48D3-BE97-422220080E43", flags);
                case "Documents": return FolderIcon("FDD39AD0-238F-46AF-ADB4-6C85480369C7", flags);
                case "Compressed": return ExtIcon(".zip", flags);
                default: return IntPtr.Zero;
            }
        }

        static IntPtr FolderIcon(string knownFolder, uint flags)
        {
            IntPtr pidl;
            if (SHGetKnownFolderIDList(new Guid(knownFolder), 0, IntPtr.Zero, out pidl) != 0) return IntPtr.Zero;
            try
            {
                var info = new SHFILEINFO();
                return SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_PIDL) == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_PIDL = 0x8, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;

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
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderIDList([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr pidl);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);
    }
}
