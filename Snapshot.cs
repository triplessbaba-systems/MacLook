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

            var spotlight = new SpotlightWindow();
            await spotlight.PrepareAsync("not");
            Save(spotlight, 740, null, Path.Combine(folder, "spotlight.png"));
            await spotlight.PrepareAsync("12*7+3");
            Save(spotlight, 740, null, Path.Combine(folder, "spotlight-hesap.png"));

            // Both bar styles. The setting is only switched in memory here, never saved.
            var screen = Screens.Primary;

            // Launchpad with a sample folder made of the first few apps (in memory only).
            var sample = new LaunchpadFolder { Name = "Örnek Klasör", Apps = AppCatalog.Apps.Skip(2).Take(5).Select(a => a.Id).ToList() };
            App.Config.LaunchpadFolders.Add(sample);
            var launchpad = new LaunchpadWindow();
            launchpad.PrepareSnapshot(screen);
            Save(launchpad, screen.Bounds.Width, screen.Bounds.Height, Path.Combine(folder, "launchpad.png"));
            App.Config.LaunchpadFolders.Remove(sample);

            var style = App.Config.BarStyle;
            foreach (var (name, value) in new[] { ("bar", "bar"), ("dock", "dock") })
            {
                App.Config.BarStyle = value;
                var bar = new DockWindow(screen, primary: true);
                Save(bar, screen.Bounds.Width, bar.Height, Path.Combine(folder, $"{name}.png"), StripAt(screen, bottom: true, bar.Height));
                var menuBar = new MenuBarWindow(screen, primary: true);
                Save(menuBar, screen.Bounds.Width, MenuBarWindow.BarHeight, Path.Combine(folder, $"menubar-{name}.png"), StripAt(screen, bottom: false, MenuBarWindow.BarHeight));
            }
            App.Config.BarStyle = style;
        }
        catch (Exception ex) { App.Log("snapshot failed: " + ex); }
        Application.Current.Shutdown();
    }

    /// <summary>The part of the (blurred) wallpaper that lies behind a strip at the top or bottom of a screen.</summary>
    static BitmapSource StripAt(ScreenInfo screen, bool bottom, double height)
    {
        var wallpaper = Wallpaper.For(screen);
        int h = (int)Math.Ceiling(height * wallpaper.PixelHeight / screen.Bounds.Height);
        return new CroppedBitmap(wallpaper, new Int32Rect(0, bottom ? wallpaper.PixelHeight - h : 0, wallpaper.PixelWidth, h));
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
                dc.DrawImage(screenBackdrop, new Rect(0, 0, width, h));
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
