// Text with emoji, in color: DirectWrite through a Direct2D DC render target (GDI's TextRenderer draws emoji as boxes).
// Theme sends only strings with emoji here and falls back to TextRenderer when Draw or Measure returns false.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Glide
{
    public static class ColorText
    {
        // One TextRenderer font as DirectWrite sees it, with GDI's line height and ascent so lines sit where GDI puts them
        class Face
        {
            public IntPtr Format, Ellipsis;
            public int Height, Ascent;
        }

        static readonly object gate = new object();
        static readonly Dictionary<Font, Face> faces = new Dictionary<Font, Face>();
        static IDWriteFactory dwrite;
        static ID2D1Factory d2d;
        static ID2D1DCRenderTarget target;
        static bool off;

        static bool Ok(int hr) { return hr >= 0; }

        static bool Start()
        {
            if (off) return false;
            if (target != null) return true;
            try
            {
                if (dwrite == null)
                {
                    object o;
                    var iid = typeof(IDWriteFactory).GUID;
                    if (!Ok(DWriteCreateFactory(0, ref iid, out o))) { off = true; return false; } // DWRITE_FACTORY_TYPE_SHARED
                    dwrite = (IDWriteFactory)o;
                }
                if (d2d == null)
                {
                    var iid = typeof(ID2D1Factory).GUID;
                    if (!Ok(D2D1CreateFactory(0, ref iid, IntPtr.Zero, out d2d))) { off = true; return false; } // SINGLE_THREADED
                }
                // Software (no GPU readback per draw: twice as fast here), B8G8R8A8 with ALPHA_MODE_IGNORE: ClearType, like GDI.
                // 96 DPI: one DIP is one pixel.
                var props = new RenderTargetProperties { Type = 1, PixelFormat = 87, AlphaMode = 3, DpiX = 96, DpiY = 96 };
                return Ok(d2d.CreateDCRenderTarget(ref props, out target)) && target != null;
            }
            catch (Exception) { off = true; return false; }
        }

        static Face FaceOf(Font f)
        {
            Face face;
            if (faces.TryGetValue(f, out face)) return face;
            // DirectWrite knows "Segoe UI Semibold" as the Segoe UI family at weight 600
            // shortcut: only the " Semibold" GDI names are mapped, add others if Theme starts using them
            var family = f.Name;
            var weight = f.Bold ? 700 : 400;
            if (family.EndsWith(" Semibold")) { family = family.Substring(0, family.Length - 9); if (!f.Bold) weight = 600; }
            var px = (float)Math.Round(f.SizeInPoints * Theme.Scale * 96 / 72);
            face = new Face();
            // a font DirectWrite can't make stays null: TextRenderer draws it
            if (!Ok(dwrite.CreateTextFormat(family, IntPtr.Zero, weight, f.Italic ? 2 : 0, 5, px, "en-us", out face.Format)) ||
                !Ok(dwrite.CreateEllipsisTrimmingSign(face.Format, out face.Ellipsis))) return faces[f] = null;
            var tm = new int[16]; // TEXTMETRICW: tmHeight, tmAscent, ...
            var dc = CreateCompatibleDC(IntPtr.Zero);
            var hfont = f.ToHfont();
            var old = SelectObject(dc, hfont);
            GetTextMetricsW(dc, tm);
            SelectObject(dc, old);
            DeleteObject(hfont);
            DeleteDC(dc);
            face.Height = tm[0];
            face.Ascent = tm[1];
            faces[f] = face;
            return face;
        }

        // A layout with TextRenderer's flags: wrapping, alignment, ellipsis, direction; caller releases it
        static IDWriteTextLayout Layout(string s, Face face, float width, float height, TextFormatFlags flags)
        {
            IDWriteTextLayout l;
            // GDI-compatible advances: the same widths TextRenderer gets, so ellipses and alignment fall in the same places
            if (!Ok(dwrite.CreateGdiCompatibleTextLayout(s, s.Length, face.Format, width, height, 1, IntPtr.Zero, false, out l)) || l == null) return null;
            var wrap = (flags & TextFormatFlags.WordBreak) != 0 && (flags & TextFormatFlags.SingleLine) == 0;
            l.SetWordWrapping(wrap ? 0 : 1); // WRAP, NO_WRAP
            l.SetTextAlignment((flags & TextFormatFlags.HorizontalCenter) != 0 ? 2 : (flags & TextFormatFlags.Right) != 0 ? 1 : 0); // CENTER, TRAILING, LEADING
            if ((flags & TextFormatFlags.RightToLeft) != 0) l.SetReadingDirection(1);
            l.SetLineSpacing(1, face.Height, face.Ascent); // UNIFORM: GDI's line height and baseline
            if ((flags & (TextFormatFlags.EndEllipsis | TextFormatFlags.PathEllipsis)) != 0)
            {
                // PathEllipsis keeps the part after the last backslash, as GDI does
                var t = (flags & TextFormatFlags.PathEllipsis) != 0 ? new Trimming { Granularity = 1, Delimiter = '\\', DelimiterCount = 1 } : new Trimming { Granularity = 1 };
                l.SetTrimming(ref t, face.Ellipsis);
            }
            return l;
        }

        public static bool Draw(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags flags)
        {
            if (r.Width <= 0 || r.Height <= 0) return true;
            lock (gate)
            {
                IDWriteTextLayout l = null;
                var brush = IntPtr.Zero;
                var hdc = IntPtr.Zero;
                try
                {
                    if (!Start()) return false;
                    var face = FaceOf(f);
                    if (face == null) return false;
                    l = Layout(s, face, r.Width, r.Height, flags);
                    if (l == null) return false;
                    TextMetrics m;
                    if (!Ok(l.GetMetrics(out m))) return false;
                    var h = (int)Math.Ceiling(m.Height);
                    var y = (flags & TextFormatFlags.Bottom) != 0 ? r.Height - h : (flags & TextFormatFlags.VerticalCenter) != 0 ? (r.Height - h) / 2 : 0;
                    var color = new ColorF { R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = 1 };
                    hdc = g.GetHdc();
                    var rect = new RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
                    if (!Ok(target.BindDC(hdc, ref rect))) return false;
                    if (!Ok(target.CreateSolidColorBrush(ref color, IntPtr.Zero, out brush))) return false;
                    target.BeginDraw();
                    target.DrawTextLayout(new PointF2 { X = 0, Y = y }, l, brush, 4 | 2); // ENABLE_COLOR_FONT | CLIP
                    var hr = target.EndDraw(IntPtr.Zero, IntPtr.Zero);
                    if (hr == unchecked((int)0x8899000C)) { Marshal.ReleaseComObject(target); target = null; } // D2DERR_RECREATE_TARGET
                    return Ok(hr);
                }
                catch (Exception) { return false; }
                finally
                {
                    if (brush != IntPtr.Zero) Marshal.Release(brush);
                    if (l != null) Marshal.ReleaseComObject(l);
                    if (hdc != IntPtr.Zero) g.ReleaseHdc(hdc);
                }
            }
        }

        // The size Draw needs: width of the widest line, height of all lines (GDI's line height each); null on failure
        public static Size? Measure(string s, Font f, int width, TextFormatFlags flags)
        {
            lock (gate)
            {
                IDWriteTextLayout l = null;
                try
                {
                    if (!Start()) return null;
                    var face = FaceOf(f);
                    if (face == null) return null;
                    l = Layout(s, face, width, 1e6f, flags);
                    TextMetrics m;
                    if (l == null || !Ok(l.GetMetrics(out m))) return null;
                    return new Size((int)Math.Ceiling(m.WidthIncludingTrailingWhitespace), (int)Math.Ceiling(m.Height));
                }
                catch (Exception) { return null; }
                finally { if (l != null) Marshal.ReleaseComObject(l); }
            }
        }

        [DllImport("dwrite.dll")]
        static extern int DWriteCreateFactory(int type, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);
        [DllImport("d2d1.dll")]
        static extern int D2D1CreateFactory(int type, ref Guid iid, IntPtr options, out ID2D1Factory factory);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool GetTextMetricsW(IntPtr hdc, [Out] int[] tm);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct PointF2 { public float X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct ColorF { public float R, G, B, A; }
        [StructLayout(LayoutKind.Sequential)] struct Trimming { public int Granularity; public uint Delimiter, DelimiterCount; }
        [StructLayout(LayoutKind.Sequential)]
        struct TextMetrics { public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight; public uint MaxBidiReorderingDepth, LineCount; }
        [StructLayout(LayoutKind.Sequential)]
        struct RenderTargetProperties { public int Type, PixelFormat, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }

        // COM interfaces in exact vtable order (IUnknown's three methods are implied); _ methods are slots never called

        [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteFactory
        {
            void _GetSystemFontCollection(); void _CreateCustomFontCollection(); void _RegisterFontCollectionLoader(); void _UnregisterFontCollectionLoader();
            void _CreateFontFileReference(); void _CreateCustomFontFileReference(); void _CreateFontFace(); void _CreateRenderingParams();
            void _CreateMonitorRenderingParams(); void _CreateCustomRenderingParams(); void _RegisterFontFileLoader(); void _UnregisterFontFileLoader();
            [PreserveSig] int CreateTextFormat([MarshalAs(UnmanagedType.LPWStr)] string family, IntPtr collection, int weight, int style, int stretch, float size,
                [MarshalAs(UnmanagedType.LPWStr)] string locale, out IntPtr format);
            void _CreateTypography(); void _GetGdiInterop(); void _CreateTextLayout();
            [PreserveSig] int CreateGdiCompatibleTextLayout([MarshalAs(UnmanagedType.LPWStr)] string text, int length, IntPtr format, float width, float height,
                float pixelsPerDip, IntPtr transform, [MarshalAs(UnmanagedType.Bool)] bool useGdiNatural, out IDWriteTextLayout layout);
            [PreserveSig] int CreateEllipsisTrimmingSign(IntPtr format, out IntPtr sign);
        }

        [ComImport, Guid("53737037-6d14-410b-9bfe-0b182bb70961"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDWriteTextLayout
        {
            // IDWriteTextFormat
            [PreserveSig] int SetTextAlignment(int a);
            [PreserveSig] int SetParagraphAlignment(int a);
            [PreserveSig] int SetWordWrapping(int w);
            [PreserveSig] int SetReadingDirection(int d);
            void _SetFlowDirection(); void _SetIncrementalTabStop();
            [PreserveSig] int SetTrimming(ref Trimming t, IntPtr sign);
            [PreserveSig] int SetLineSpacing(int method, float spacing, float baseline);
            void _GetTextAlignment(); void _GetParagraphAlignment(); void _GetWordWrapping(); void _GetReadingDirection(); void _GetFlowDirection();
            void _GetIncrementalTabStop(); void _GetTrimming(); void _GetLineSpacing(); void _GetFontCollection(); void _GetFontFamilyNameLength();
            void _GetFontFamilyName(); void _GetFontWeight(); void _GetFontStyle(); void _GetFontStretch(); void _GetFontSize(); void _GetLocaleNameLength();
            void _GetLocaleName();
            // IDWriteTextLayout
            void _SetMaxWidth(); void _SetMaxHeight(); void _SetFontCollection(); void _SetFontFamilyName(); void _SetFontWeight(); void _SetFontStyle();
            void _SetFontStretch(); void _SetFontSize(); void _SetUnderline(); void _SetStrikethrough(); void _SetDrawingEffect(); void _SetInlineObject();
            void _SetTypography(); void _SetLocaleName(); void _GetMaxWidth(); void _GetMaxHeight(); void _GetFontCollection2(); void _GetFontFamilyNameLength2();
            void _GetFontFamilyName2(); void _GetFontWeight2(); void _GetFontStyle2(); void _GetFontStretch2(); void _GetFontSize2(); void _GetUnderline();
            void _GetStrikethrough(); void _GetDrawingEffect(); void _GetInlineObject(); void _GetTypography(); void _GetLocaleNameLength2(); void _GetLocaleName2();
            void _Draw(); void _GetLineMetrics();
            [PreserveSig] int GetMetrics(out TextMetrics m);
        }

        [ComImport, Guid("06152247-6f50-465a-9245-118bfd3b6007"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1Factory
        {
            void _ReloadSystemMetrics(); void _GetDesktopDpi(); void _CreateRectangleGeometry(); void _CreateRoundedRectangleGeometry(); void _CreateEllipseGeometry();
            void _CreateGeometryGroup(); void _CreateTransformedGeometry(); void _CreatePathGeometry(); void _CreateStrokeStyle(); void _CreateDrawingStateBlock();
            void _CreateWicBitmapRenderTarget(); void _CreateHwndRenderTarget(); void _CreateDxgiSurfaceRenderTarget();
            [PreserveSig] int CreateDCRenderTarget(ref RenderTargetProperties props, out ID2D1DCRenderTarget target);
        }

        [ComImport, Guid("1c51bc64-de61-46fd-9899-63a5d8f03950"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID2D1DCRenderTarget
        {
            void _GetFactory();
            // ID2D1RenderTarget
            void _CreateBitmap(); void _CreateBitmapFromWicBitmap(); void _CreateSharedBitmap(); void _CreateBitmapBrush();
            [PreserveSig] int CreateSolidColorBrush(ref ColorF color, IntPtr props, out IntPtr brush);
            void _CreateGradientStopCollection(); void _CreateLinearGradientBrush(); void _CreateRadialGradientBrush(); void _CreateCompatibleRenderTarget();
            void _CreateLayer(); void _CreateMesh(); void _DrawLine(); void _DrawRectangle(); void _FillRectangle(); void _DrawRoundedRectangle();
            void _FillRoundedRectangle(); void _DrawEllipse(); void _FillEllipse(); void _DrawGeometry(); void _FillGeometry(); void _FillMesh();
            void _FillOpacityMask(); void _DrawBitmap(); void _DrawText();
            [PreserveSig] void DrawTextLayout(PointF2 origin, IDWriteTextLayout layout, IntPtr brush, int options);
            void _DrawGlyphRun(); void _SetTransform(); void _GetTransform(); void _SetAntialiasMode(); void _GetAntialiasMode(); void _SetTextAntialiasMode();
            void _GetTextAntialiasMode(); void _SetTextRenderingParams(); void _GetTextRenderingParams(); void _SetTags(); void _GetTags(); void _PushLayer();
            void _PopLayer(); void _Flush(); void _SaveDrawingState(); void _RestoreDrawingState(); void _PushAxisAlignedClip(); void _PopAxisAlignedClip();
            void _Clear();
            [PreserveSig] void BeginDraw();
            [PreserveSig] int EndDraw(IntPtr tag1, IntPtr tag2);
            void _GetPixelFormat(); void _SetDpi(); void _GetDpi(); void _GetSize(); void _GetPixelSize(); void _GetMaximumBitmapSize(); void _IsSupported();
            // ID2D1DCRenderTarget
            [PreserveSig] int BindDC(IntPtr hdc, ref RECT rect);
        }
    }
}
