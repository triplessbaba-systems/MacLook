using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacLook;

/// <summary>Loads large shell icons for exe, lnk or shell paths. Safe to call from background threads.</summary>
internal static class IconLoader
{
    const int SIIGBF_ICONONLY = 0x4;
    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string path, int size = 256)
    {
        var key = path + "|" + size;
        lock (Cache)
            if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            Native.SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            try { image = FromFactory(factory, size); }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch { }

        lock (Cache) Cache[key] = image;
        return image;
    }

    public static BitmapSource FromFactory(IShellItemImageFactory factory, int size)
    {
        var image = GetImage(factory, size);
        // Apps without a large icon come back as a tiny icon inside a square frame; the plain 48px one looks better.
        return size > 48 && LooksFramed(image) ? GetImage(factory, 48) : image;
    }

    static BitmapSource GetImage(IShellItemImageFactory factory, int size)
    {
        factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out var hbmp);
        try { return FromHBitmap(hbmp); }
        finally { Native.DeleteObject(hbmp); }
    }

    /// <summary>
    /// Windows draws small-only icons inside a faint square outline running along the image edge,
    /// with nothing between the outline and the icon in the middle.
    /// </summary>
    static bool LooksFramed(BitmapSource bmp)
    {
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        if (w < 64 || h < 64 || bmp.Format != PixelFormats.Pbgra32) return false;
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int Alpha(int x, int y) => px[(y * w + x) * 4 + 3];

        int faint = 0;
        for (int x = 0; x < w; x++) if (Alpha(x, 0) is > 8 and < 160) faint++;
        for (int y = 0; y < h; y++) if (Alpha(0, y) is > 8 and < 160) faint++;
        int inset = Math.Max(4, w / 16);
        bool hollow = Alpha(w / 2, inset) < 10 && Alpha(inset, h / 2) < 10;
        return hollow && faint > (w + h) * 0.85;
    }

    static BitmapSource FromHBitmap(IntPtr hbmp)
    {
        // CreateBitmapSourceFromHBitmap drops the alpha channel, so copy the DIB bits ourselves.
        if (Native.GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) == 0
            || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            return BitmapUtil.Saturate(fallback, 1.0);
        }

        int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
        var pixels = new byte[stride * h];
        if (ds.dsBmih.biHeight > 0) // bottom-up DIB
            for (int y = 0; y < h; y++)
                Marshal.Copy(ds.dsBm.bmBits + (h - 1 - y) * stride, pixels, y * stride, stride);
        else
            Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }
}
