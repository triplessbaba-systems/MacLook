using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;

namespace MacLook;

/// <summary>One monitor. <see cref="Bounds"/> is in WPF units (DIPs); <see cref="BoundsPx"/> in physical pixels.</summary>
internal sealed record ScreenInfo(IntPtr Handle, string Name, bool Primary, Rect Bounds, RECT BoundsPx)
{
    /// <summary>A rectangle given relative to this screen's top-left corner, in screen coordinates.</summary>
    public Rect Offset(Rect relative) => new(Bounds.X + relative.X, Bounds.Y + relative.Y, relative.Width, relative.Height);
}

/// <summary>The monitors, in WPF coordinates. MacLook is system-DPI aware, so one scale applies to all of them.</summary>
internal static class Screens
{
    const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// <summary>Physical pixels per DIP.</summary>
    public static double Scale => GetDpiForSystem() / 96.0;

    /// <summary>Raised (on the UI thread) when monitors are added, removed or rearranged.</summary>
    public static event Action? Changed;

    public static void Init()
    {
        SystemEvents.DisplaySettingsChanged += (_, _) => Application.Current.Dispatcher.InvokeAsync(() => Changed?.Invoke());
    }

    public static List<ScreenInfo> All()
    {
        var result = new List<ScreenInfo>();
        double scale = Scale;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var px = info.rcMonitor;
                var dip = new Rect(px.Left / scale, px.Top / scale, (px.Right - px.Left) / scale, (px.Bottom - px.Top) / scale);
                result.Add(new ScreenInfo(monitor, info.szDevice, (info.dwFlags & 1) != 0, dip, px));
            }
            return true;
        }, IntPtr.Zero);
        // Primary first, then left to right.
        return result.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ToList();
    }

    public static ScreenInfo Primary => All().First();

    public static ScreenInfo AtCursor()
    {
        Native.GetCursorPos(out var p);
        return Find(MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST));
    }

    public static ScreenInfo Of(IntPtr hwnd) => Find(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    public static IntPtr MonitorOf(IntPtr hwnd) => MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

    static ScreenInfo Find(IntPtr monitor)
    {
        var all = All();
        return all.FirstOrDefault(s => s.Handle == monitor) ?? all.First();
    }
}
