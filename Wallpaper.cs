using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Blurred copy of the desktop wallpaper. The menu bar and Dock sit on screen strips reserved
/// for them, so the wallpaper is what's behind them: painting it blurred gives the macOS
/// frosted-glass look with smooth rounded corners and no cost while the Dock animates.
/// </summary>
internal static class Wallpaper
{
    const int Downscale = 4; // blur works on a quarter-size image; it's blurry anyway

    /// <summary>Primary-screen blur (kept for callers that don't care about other monitors).</summary>
    public static BitmapSource Blurred => For(Screens.Primary);
    /// <summary>Heavier primary-screen blur.</summary>
    public static BitmapSource BlurredStrong => For(Screens.Primary, strong: true);
    public static event Action? Changed;

    static BitmapSource? source;
    static Stretch stretch;
    static Brush background = Brushes.Black;
    static readonly Dictionary<string, BitmapSource> Cache = new();

    static FileSystemWatcher? engineWatcher;
    static DispatcherTimer? reloadDelay;

    public static void Init()
    {
        Load();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Desktop) ScheduleReload();
        };
        // Wallpaper Engine rewrites its config.json when the wallpaper changes.
        if (WallpaperEngineConfig() is string config)
        {
            engineWatcher = new FileSystemWatcher(Path.GetDirectoryName(config)!, "config.json") { NotifyFilter = NotifyFilters.LastWrite };
            engineWatcher.Changed += (_, _) => ScheduleReload();
            engineWatcher.EnableRaisingEvents = true;
        }
    }

    static void ScheduleReload() => Application.Current.Dispatcher.InvokeAsync(() =>
    {
        reloadDelay ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        reloadDelay.Tick -= OnReload;
        reloadDelay.Tick += OnReload;
        reloadDelay.Stop();
        reloadDelay.Start();
    });

    static void OnReload(object? sender, EventArgs e)
    {
        reloadDelay!.Stop();
        Load();
        Changed?.Invoke();
    }

    /// <summary>
    /// Blurred wallpaper sized for one screen. Its size in DIPs equals the screen's, so a brush can use
    /// coordinates relative to the screen's top-left corner as its Viewbox.
    /// </summary>
    public static BitmapSource For(ScreenInfo screen, bool strong = false)
    {
        double screenW = screen.Bounds.Width, screenH = screen.Bounds.Height;
        var key = $"{screenW}x{screenH}|{strong}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        int w = (int)Math.Ceiling(screenW / Downscale), h = (int)Math.Ceiling(screenH / Downscale);
        double dpiX = 96.0 * w / screenW, dpiY = 96.0 * h / screenH;
        var blurred = BitmapUtil.Blur(source, w, h, strong ? 20 : 9, stretch, background);
        return Cache[key] = BitmapUtil.Saturate(blurred, strong ? 1.3 : 1.5, dpiX, dpiY);
    }

    /// <summary>Brightness (0..1) of the wallpaper behind a rectangle given relative to the screen.</summary>
    public static double Luminance(ScreenInfo screen, Rect relative)
    {
        var bitmap = For(screen);
        double sx = bitmap.PixelWidth / screen.Bounds.Width, sy = bitmap.PixelHeight / screen.Bounds.Height;
        return BitmapUtil.Luminance(bitmap, new Int32Rect(
            (int)(relative.X * sx), (int)(relative.Y * sy),
            (int)Math.Ceiling(relative.Width * sx), (int)Math.Ceiling(relative.Height * sy)));
    }

    static void Load()
    {
        Cache.Clear();
        source = LoadSource((int)Math.Ceiling(Screens.All().Max(s => s.Bounds.Width) / Downscale * 2));
        stretch = ReadStretch();
        background = new SolidColorBrush(ReadBackgroundColor());
    }

    static BitmapSource? LoadSource(int decodeWidth)
    {
        var candidates = new List<string>();
        // A live wallpaper (Wallpaper Engine) covers the Windows wallpaper, so prefer its preview image.
        if (WallpaperEnginePreview() is string preview) candidates.Add(preview);
        var sb = new StringBuilder(520);
        if (Native.SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0) && sb.Length > 0)
            candidates.Add(sb.ToString());
        // Slideshows and Spotlight leave the current image here.
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper"));

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = decodeWidth;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { }
        }
        return null;
    }

    /// <summary>config.json of a running Wallpaper Engine, if any.</summary>
    static string? WallpaperEngineConfig()
    {
        foreach (var name in new[] { "wallpaper64", "wallpaper32" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                if (WindowTracker.GetProcessPath((uint)process.Id) is not string exe) continue;
                var config = Path.Combine(Path.GetDirectoryName(exe)!, "config.json");
                if (File.Exists(config)) return config;
            }
        return null;
    }

    /// <summary>Preview image of the wallpaper Wallpaper Engine is currently showing.</summary>
    static string? WallpaperEnginePreview()
    {
        try
        {
            if (WallpaperEngineConfig() is not string config) return null;
            // The first "selectedwallpapers" block is the active one (later ones are the recent list).
            var match = Regex.Match(File.ReadAllText(config),
                @"""selectedwallpapers""\s*:\s*\{\s*""[^""]+""\s*:\s*\{\s*""file""\s*:\s*""([^""]+)""");
            if (!match.Success) return null;
            var project = Path.GetDirectoryName(match.Groups[1].Value.Replace('/', '\\'));
            if (project == null || !Directory.Exists(project)) return null;

            var projectJson = Path.Combine(project, "project.json");
            if (File.Exists(projectJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(projectJson));
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Name.Equals("preview", StringComparison.OrdinalIgnoreCase) && prop.Value.GetString() is string p
                        && File.Exists(Path.Combine(project, p)))
                        return Path.Combine(project, p);
            }
            return Directory.GetFiles(project, "preview.*").FirstOrDefault();
        }
        catch { return null; }
    }

    static Stretch ReadStretch()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return (key?.GetValue("WallpaperStyle") as string) switch
        {
            "6" => Stretch.Uniform,   // Fit
            "2" => Stretch.Fill,      // Stretch
            _ => Stretch.UniformToFill, // Fill, Span, Tile, Center
        };
    }

    static Color ReadBackgroundColor()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
        var parts = (key?.GetValue("Background") as string)?.Split(' ');
        if (parts?.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g) && byte.TryParse(parts[2], out var b))
            return Color.FromRgb(r, g, b);
        return Color.FromRgb(30, 40, 60);
    }
}
