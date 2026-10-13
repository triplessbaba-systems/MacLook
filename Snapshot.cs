using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacLook;

/// <summary>
/// MacLook.exe --snapshot &lt;folder&gt;: renders the pop-up panels to PNG files without showing
/// anything on screen or touching the taskbar, so the design can be checked while you work.
/// </summary>
internal static class Snapshot
{
    public static async Task RunAsync(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var loaded = new TaskCompletionSource();
            AppCatalog.Updated += () => loaded.TrySetResult();
            AppCatalog.Refresh();
            await Task.WhenAny(loaded.Task, Task.Delay(15000));

            var notifications = new NotificationCenterWindow();
            double panelH = await notifications.PrepareAsync();
            Save(notifications, 420, panelH + 60, Path.Combine(folder, "bildirim-merkezi.png"));

            var controls = new ControlCenterWindow();
            await controls.PrepareAsync();
            Save(controls, 400, null, Path.Combine(folder, "denetim-merkezi.png"));

            var start = new StartMenuWindow();
            start.Prepare();
            Save(start, 700, 800, Path.Combine(folder, "baslat.png"));

            Save(new SettingsWindow(), 520, null, Path.Combine(folder, "ayarlar.png"));

            var bar = new DockWindow();
            Save(bar, SystemParameters.PrimaryScreenWidth, bar.Height, Path.Combine(folder, "bar.png"), Wallpaper.Blurred);
        }
        catch (Exception ex) { App.Log("snapshot failed: " + ex); }
        Application.Current.Shutdown();
    }

    static void Save(Window window, double width, double? height, string path, BitmapSource? screenBackdrop = null)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height ?? double.PositiveInfinity));
        double h = height ?? root.DesiredSize.Height;
        root.Arrange(new Rect(0, 0, width, h));
        root.UpdateLayout();

        var image = new RenderTargetBitmap((int)width, (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            if (screenBackdrop != null) // the matching strip of the wallpaper, as on screen
                dc.DrawImage(new CroppedBitmap(screenBackdrop, new Int32Rect(0, screenBackdrop.PixelHeight - (int)(h * screenBackdrop.PixelHeight / SystemParameters.PrimaryScreenHeight),
                    screenBackdrop.PixelWidth, (int)(h * screenBackdrop.PixelHeight / SystemParameters.PrimaryScreenHeight))), new Rect(0, 0, width, h));
            else
                dc.DrawImage(Wallpaper.BlurredStrong, new Rect(0, 0, width, h)); // stand-in for what's behind the panel
        }
        image.Render(backdrop);
        image.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
