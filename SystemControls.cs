using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Radios;
using Windows.Media.Control;

namespace MacLook;

/// <summary>Master volume of the default output device (Windows Core Audio).</summary>
internal static class AudioVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        int SetMasterVolumeLevelScalar(float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, [MarshalAs(UnmanagedType.LPStruct)] Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    static IAudioEndpointVolume? Endpoint()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* render */, 1 /* multimedia */, out var device) != 0) return null;
            device.Activate(typeof(IAudioEndpointVolume).GUID, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var endpoint);
            return (IAudioEndpointVolume)endpoint;
        }
        catch { return null; }
    }

    /// <summary>0..1, or null when there is no output device.</summary>
    public static double? Level
    {
        get
        {
            if (Endpoint() is not { } e) return null;
            e.GetMasterVolumeLevelScalar(out var level);
            e.GetMute(out var muted);
            return muted ? 0 : level;
        }
        set
        {
            if (Endpoint() is not { } e || value is not double v) return;
            e.SetMasterVolumeLevelScalar((float)Math.Clamp(v, 0, 1), Guid.Empty);
            e.SetMute(v <= 0.001, Guid.Empty);
        }
    }
}

/// <summary>Screen brightness through WMI; only laptop/built-in panels support it.</summary>
internal static class Brightness
{
    public static int? Get()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void Set(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in searcher.Get()) o.InvokeMethod("WmiSetBrightness", [1, Math.Clamp(percent, 0, 100)]);
        }
        catch { }
    }
}

internal sealed record NowPlaying(string Title, string Artist, ImageSource? Cover, bool Playing, GlobalSystemMediaTransportControlsSession Session);

/// <summary>Whatever is playing (Spotify, YouTube in Chrome, ...) via Windows' media session API.</summary>
internal static class Media
{
    static GlobalSystemMediaTransportControlsSessionManager? manager;

    public static async Task<NowPlaying?> GetAsync()
    {
        try
        {
            manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (manager.GetCurrentSession() is not { } session) return null;
            var props = await session.TryGetMediaPropertiesAsync();
            bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            ImageSource? cover = null;
            if (props.Thumbnail != null)
            {
                using var stream = (await props.Thumbnail.OpenReadAsync()).AsStreamForRead();
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = copy;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.EndInit();
                image.Freeze();
                cover = image;
            }
            return new NowPlaying(props.Title ?? "", props.Artist ?? "", cover, playing, session);
        }
        catch { return null; }
    }
}

/// <summary>Wi-Fi and Bluetooth on/off switches.</summary>
internal static class Radios
{
    public static async Task<Radio?> FindAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }
}

internal sealed record RecentFile(string Name, string Path, DateTime When);

internal static class UserInfo
{
    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);

    public static string DisplayName
    {
        get
        {
            var sb = new StringBuilder(256);
            int size = sb.Capacity;
            return GetUserNameEx(3 /* NameDisplay */, sb, ref size) && sb.Length > 0 ? sb.ToString() : Environment.UserName;
        }
    }

    public static ImageSource? Picture
    {
        get
        {
            try
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
                using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sid}");
                if (key?.GetValue("Image192") is not string path || !File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 96;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }
    }

    /// <summary>Recently opened files, newest first (from Windows' Recent folder).</summary>
    public static List<RecentFile> RecentFiles(int count)
    {
        var result = new List<RecentFile>();
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(dir)) return result;
        foreach (var lnk in new DirectoryInfo(dir).GetFiles("*.lnk").OrderByDescending(f => f.LastWriteTime))
        {
            if (result.Count >= count) break;
            var name = System.IO.Path.GetFileNameWithoutExtension(lnk.Name);
            if (name.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) || name.StartsWith('{')) continue;
            var target = Shortcuts.GetTargetPath(lnk.FullName);
            if (target.Length == 0 || !File.Exists(target)) continue; // files only, skip folders and dead links
            result.Add(new RecentFile(System.IO.Path.GetFileName(target), target, lnk.LastWriteTime));
        }
        return result;
    }

    /// <summary>"Az önce", "5 dakika önce", "Dün 18:48", "12 Eki".</summary>
    public static string Ago(DateTime when)
    {
        var tr = new CultureInfo("tr-TR");
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "Az önce";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} dakika önce";
        if (when.Date == DateTime.Today) return $"{(int)span.TotalHours} saat önce";
        if (when.Date == DateTime.Today.AddDays(-1)) return "Dün " + when.ToString("HH:mm", tr);
        return when.ToString("d MMM", tr);
    }
}
