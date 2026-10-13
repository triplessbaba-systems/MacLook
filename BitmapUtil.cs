using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace MacLook;

/// <summary>Blur, saturation and screen capture helpers for the frosted-glass look.</summary>
internal static class BitmapUtil
{
    /// <summary>
    /// Draws <paramref name="source"/> into a width x height bitmap with a gaussian blur.
    /// It is drawn into a slightly larger canvas and cropped, so the edges don't fade out.
    /// </summary>
    public static BitmapSource Blur(BitmapSource? source, int width, int height, double radius, Stretch stretch, Brush background)
    {
        int m = (int)Math.Ceiling(radius) + 2;
        int fullW = width + 2 * m, fullH = height + 2 * m;
        var canvas = new Grid { Width = fullW, Height = fullH, Background = background };
        if (source != null)
            canvas.Children.Add(new Image
            {
                Source = source,
                Stretch = stretch,
                Effect = new BlurEffect { Radius = radius, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
            });
        canvas.Measure(new Size(fullW, fullH));
        canvas.Arrange(new Rect(0, 0, fullW, fullH));
        canvas.UpdateLayout();

        var rendered = new RenderTargetBitmap(fullW, fullH, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(canvas);
        return new CroppedBitmap(rendered, new Int32Rect(m, m, width, height));
    }

    /// <summary>Boosts color saturation (macOS "vibrancy") and copies into a frozen bitmap with the given DPI.</summary>
    public static BitmapSource Saturate(BitmapSource source, double factor, double dpiX = 96, double dpiY = 96)
    {
        var src = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            double gray = 0.299 * r + 0.587 * g + 0.114 * b;
            px[i] = (byte)Math.Clamp(gray + (b - gray) * factor, 0, a);
            px[i + 1] = (byte)Math.Clamp(gray + (g - gray) * factor, 0, a);
            px[i + 2] = (byte)Math.Clamp(gray + (r - gray) * factor, 0, a);
        }
        var result = BitmapSource.Create(w, h, dpiX, dpiY, PixelFormats.Pbgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Average perceived brightness (0..1) of a pixel region.</summary>
    public static double Luminance(BitmapSource bitmap, Int32Rect region)
    {
        region.X = Math.Clamp(region.X, 0, bitmap.PixelWidth - 1);
        region.Y = Math.Clamp(region.Y, 0, bitmap.PixelHeight - 1);
        region.Width = Math.Clamp(region.Width, 1, bitmap.PixelWidth - region.X);
        region.Height = Math.Clamp(region.Height, 1, bitmap.PixelHeight - region.Y);
        int stride = region.Width * 4;
        var px = new byte[stride * region.Height];
        bitmap.CopyPixels(region, px, stride, 0);
        double sum = 0;
        for (int i = 0; i < px.Length; i += 4)
            sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2];
        return sum / (px.Length / 4) / 255.0;
    }

    /// <summary>Copies a region of the screen (physical pixels).</summary>
    public static BitmapSource CaptureScreen(Int32Rect r)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = Native.CreateCompatibleDC(screen);
        var bmp = Native.CreateCompatibleBitmap(screen, r.Width, r.Height);
        var old = Native.SelectObject(mem, bmp);
        try
        {
            Native.BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 /* SRCCOPY */);
            Native.SelectObject(mem, old);
            return Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            Native.DeleteObject(bmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Blurred snapshot of what is on screen behind a rectangle (physical pixels), at half resolution.</summary>
    public static BitmapSource BlurredScreen(Int32Rect rect, double radius = 10)
    {
        int m = (int)Math.Ceiling(radius) + 2;
        int pad = m * 2; // becomes m after halving, which Blur() crops away again
        var capture = CaptureScreen(new Int32Rect(rect.X - pad, rect.Y - pad, rect.Width + 2 * pad, rect.Height + 2 * pad));
        var blurred = Blur(capture, Math.Max(1, rect.Width / 2), Math.Max(1, rect.Height / 2), radius, Stretch.Fill, Brushes.Black);
        return Saturate(blurred, 1.4);
    }

    /// <summary>Physical pixels per DIP on the primary screen.</summary>
    public static double DpiScale(Visual? visual)
    {
        var source = visual != null ? PresentationSource.FromVisual(visual) : null;
        return source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
    }
}
