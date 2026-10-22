// The icon Windows shows for a file type (File Explorer, IDM), from the extension alone: SHGFI_USEFILEATTRIBUTES never
// touches the disk. The sidebar's Types get Windows' own icon for the kind instead (DrawType). Cached per extension or
// kind and size; callers draw their glyph when Draw returns false.
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
        public static bool DrawType(Graphics g, string category, Rectangle r, int px) { return Draw(g, "*" + category, r, px); }

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
            var h = IntPtr.Zero;
            try
            {
                // the small (16 px) image up to 16 px, else the large one (32 px at 96 DPI), scaled down
                var flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
                h = key.StartsWith("*") ? KindIcon(key.Substring(1), flags) : ExtIcon(key, flags);
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

        // Windows' Videos, Music and Documents folders, Programs and Features' software icon, the zip folder, a folder
        static IntPtr KindIcon(string category, uint flags)
        {
            switch (category)
            {
                case "Video": return FolderIcon("18989B1D-99B5-455B-841C-AB7C74E4DDFC", flags);
                case "Music": return FolderIcon("4BD8D571-6D19-48D3-BE97-422220080E43", flags);
                case "Documents": return FolderIcon("FDD39AD0-238F-46AF-ADB4-6C85480369C7", flags);
                case "Programs": return StockIcon(SIID_SOFTWARE, flags);
                case "Compressed": return StockIcon(SIID_ZIPFILE, flags);
                default: return StockIcon(SIID_FOLDER, flags);
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

        // SHGSI_ICON, SHGSI_SMALLICON and SHGSI_LARGEICON are the SHGFI_ values
        static IntPtr StockIcon(uint id, uint flags)
        {
            var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf(typeof(SHSTOCKICONINFO)) };
            return SHGetStockIconInfo(id, flags, ref info) == 0 ? info.hIcon : IntPtr.Zero;
        }

        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_SMALLICON = 0x1, SHGFI_PIDL = 0x8, SHGFI_USEFILEATTRIBUTES = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint SIID_FOLDER = 3, SIID_SOFTWARE = 82, SIID_ZIPFILE = 105;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderIDList([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr pidl);
        [DllImport("shell32.dll")]
        static extern int SHGetStockIconInfo(uint id, uint flags, ref SHSTOCKICONINFO info);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);
    }
}
