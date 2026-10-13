using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacLook;

/// <summary>Hides the Windows taskbar while MacLook runs and puts it back afterwards.</summary>
internal static class Taskbar
{
    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    // Original setting is kept on disk so it can be restored even after a crash (MacLook.exe --restore-taskbar).
    static readonly string StateFile = Path.Combine(Config.Dir, "taskbar-state.txt");
    static DispatcherTimer? guard;
    static bool active;

    public static void Hide()
    {
        Directory.CreateDirectory(Config.Dir);
        if (!File.Exists(StateFile)) File.WriteAllText(StateFile, GetState().ToString());
        SetState(ABS_AUTOHIDE);
        active = true;
        SetVisible(false);

        // Explorer occasionally shows the taskbar again on its own.
        guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        guard.Tick += (_, _) => { if (active) SetVisible(false); };
        guard.Start();
    }

    public static void Restore()
    {
        active = false;
        try { guard?.Stop(); } catch { }
        SetVisible(true);
        try
        {
            if (!File.Exists(StateFile)) return;
            if (int.TryParse(File.ReadAllText(StateFile), out var state)) SetState(state);
            File.Delete(StateFile);
        }
        catch { }
    }

    static int GetState()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (int)Native.SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    static void SetState(int state)
    {
        var data = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = TrayWindows().FirstOrDefault(),
            lParam = state,
        };
        Native.SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    /// <summary>Explorer's taskbars. Our tray host also owns a "Shell_TrayWnd" window, so filter by process.</summary>
    static IEnumerable<IntPtr> TrayWindows()
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            var h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid != (uint)Environment.ProcessId) yield return h;
            }
        }
    }

    static void SetVisible(bool visible)
    {
        foreach (var h in TrayWindows())
            if (Native.IsWindowVisible(h) != visible)
                Native.ShowWindow(h, visible ? Native.SW_SHOW : Native.SW_HIDE);
    }
}
