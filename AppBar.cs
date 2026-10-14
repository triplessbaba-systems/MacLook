using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MacLook;

/// <summary>
/// Registers a window as a desktop toolbar so maximized windows leave room for it,
/// just like the menu bar and Dock on macOS.
/// </summary>
internal sealed class AppBar
{
    public const uint Top = 1, Bottom = 3;
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

    readonly Window window;
    readonly uint edge;
    readonly ScreenInfo screen;
    double heightDip;
    IntPtr hwnd;
    uint callbackMessage;
    bool registered;

    /// <summary>True when a fullscreen app (game, video) takes over the screen.</summary>
    public event Action<bool>? FullscreenChanged;

    public AppBar(Window window, uint edge, double heightDip, ScreenInfo screen)
    {
        this.window = window;
        this.edge = edge;
        this.heightDip = heightDip;
        this.screen = screen;
    }

    /// <param name="reserve">false: only listen for fullscreen apps, don't keep windows out of the strip (auto-hide).</param>
    public void Register(bool reserve = true)
    {
        hwnd = new WindowInteropHelper(window).Handle;
        callbackMessage = Native.RegisterWindowMessage("MacLook.AppBarMessage");
        var data = NewData();
        data.uCallbackMessage = callbackMessage;
        Native.SHAppBarMessage(ABM_NEW, ref data);
        registered = true;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);
        if (!reserve) heightDip = 0;
        UpdatePosition();
        window.Closed += (_, _) => Unregister();
    }

    public void Unregister()
    {
        if (!registered) return;
        registered = false;
        var remove = NewData();
        Native.SHAppBarMessage(ABM_REMOVE, ref remove);
    }

    void UpdatePosition()
    {
        var px = screen.BoundsPx;
        int size = (int)Math.Ceiling(heightDip * Screens.Scale);
        var data = NewData();
        data.rc = edge == Top
            ? new RECT { Left = px.Left, Top = px.Top, Right = px.Right, Bottom = px.Top + size }
            : new RECT { Left = px.Left, Top = px.Bottom - size, Right = px.Right, Bottom = px.Bottom };
        Native.SHAppBarMessage(ABM_QUERYPOS, ref data);
        if (edge == Top) data.rc.Bottom = data.rc.Top + size;
        else data.rc.Top = data.rc.Bottom - size;
        Native.SHAppBarMessage(ABM_SETPOS, ref data);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != (int)callbackMessage || !registered) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case ABN_POSCHANGED:
                UpdatePosition();
                break;
            case ABN_FULLSCREENAPP:
                bool fullscreen = lParam != IntPtr.Zero;
                // Ignore the desktop (sometimes reported as fullscreen) and our own full-screen windows.
                var fg = Native.GetForegroundWindow();
                var fgClass = WindowTracker.GetClass(fg);
                Native.GetWindowThreadProcessId(fg, out var fgPid);
                bool ignore = fgClass is "Progman" or "WorkerW" || fgPid == (uint)Environment.ProcessId;
                // Only react to fullscreen apps on this window's own monitor.
                if (fullscreen && Screens.MonitorOf(fg) != screen.Handle) ignore = true;
                if (!fullscreen || !ignore)
                    FullscreenChanged?.Invoke(fullscreen);
                break;
        }
        handled = true;
        return IntPtr.Zero;
    }

    APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uEdge = edge };
}
