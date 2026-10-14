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
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate([MarshalAs(UnmanagedType.LPStruct)] Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        int OpenPropertyStore(int access, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT value);

    // Undocumented but stable since Windows 7: the interface the Sound control panel uses to change the default device.
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(IntPtr a, IntPtr b);
        int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        int ResetDeviceFormat(IntPtr a);
        int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        int SetProcessingPeriod(IntPtr a, IntPtr b);
        int GetShareMode(IntPtr a, IntPtr b);
        int SetShareMode(IntPtr a, IntPtr b);
        int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    }

    /// <summary>Active output devices (speakers, headphones…) and which one is the default.</summary>
    public static List<(string Id, string Name, bool IsDefault)> Outputs()
    {
        var result = new List<(string, string, bool)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            string defaultId = "";
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var current) == 0) current.GetId(out defaultId);
            enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices);
            devices.GetCount(out var count);
            var nameKey = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // PKEY_Device_FriendlyName
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out var device);
                device.GetId(out var id);
                device.OpenPropertyStore(0 /* read */, out var store);
                store.GetValue(ref nameKey, out var value);
                var name = value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointer) ?? id : id;
                PropVariantClear(ref value);
                result.Add((id, name, id == defaultId));
            }
        }
        catch { }
        return result;
    }

    /// <summary>Makes a device the default for every kind of sound (console, multimedia, communications).</summary>
    public static void SetDefaultOutput(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
        }
        catch { }
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

/// <summary>
/// Screen brightness: WMI for built-in laptop panels, DDC/CI for external monitors that support it.
/// Calls can take a moment (DDC/CI talks to the monitor), so use them off the UI thread.
/// </summary>
internal static class Brightness
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr monitor, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr monitor, uint value);
    [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    /// <summary>0..100 for the screen (monitor handle), or null if it can't be changed.</summary>
    public static int? Get(IntPtr monitor)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in searcher.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return WithPhysicalMonitor(monitor, m =>
            GetMonitorBrightness(m, out var min, out var current, out var max) && max > min
                ? (int)Math.Round((current - min) * 100.0 / (max - min))
                : (int?)null);
    }

    public static void Set(IntPtr monitor, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
            bool any = false;
            foreach (ManagementObject o in searcher.Get()) { o.InvokeMethod("WmiSetBrightness", [1, percent]); any = true; }
            if (any) return;
        }
        catch { }
        WithPhysicalMonitor<int?>(monitor, m =>
        {
            if (GetMonitorBrightness(m, out var min, out _, out var max) && max > min)
                SetMonitorBrightness(m, (uint)(min + (max - min) * percent / 100.0));
            return null;
        });
    }

    static T? WithPhysicalMonitor<T>(IntPtr monitor, Func<IntPtr, T?> action)
    {
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) return default;
            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return default;
            try { return action(monitors[0].hPhysicalMonitor); }
            finally { DestroyPhysicalMonitors(count, monitors); }
        }
        catch { return default; }
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
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return L.T("Az önce");
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + L.T(" dakika önce");
        if (when.Date == DateTime.Today) return (int)span.TotalHours + L.T(" saat önce");
        if (when.Date == DateTime.Today.AddDays(-1)) return L.T("Dün") + " " + when.ToString("HH:mm", L.Culture);
        return when.ToString(L.IsEnglish ? "MMM d" : "d MMM", L.Culture);
    }
}
