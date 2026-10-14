using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// macOS hot corners: push the mouse into a screen corner to run an action.
/// Off while a fullscreen app (a game, a video) is in front.
/// </summary>
internal static class HotCorners
{
    public static readonly (string Id, string Name)[] Actions =
    [
        ("", "—"),
        ("missioncontrol", "Mission Control"),
        ("desktop", "Masaüstü"),
        ("launchpad", "Launchpad"),
        ("start", "Başlat menüsü"),
        ("notifications", "Bildirim Merkezi"),
        ("spotlight", "Spotlight"),
        ("lock", "Ekranı Kilitle"),
    ];

    const int Edge = 2, Rearm = 30;
    static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    static string? armedCorner; // the corner the mouse is in; it must leave before that corner fires again
    static DateTime enteredAt;

    public static void Start()
    {
        Timer.Tick += (_, _) => Check();
        Timer.Start();
    }

    public static string NameOf(string id) => L.T(Actions.FirstOrDefault(a => a.Id == id).Name ?? "—");

    static void Check()
    {
        var config = App.Config;
        Native.GetCursorPos(out var p);
        string? corner = null, action = null;
        foreach (var screen in Screens.All())
        {
            var b = screen.BoundsPx;
            bool left = p.X <= b.Left + Edge, right = p.X >= b.Right - 1 - Edge, top = p.Y <= b.Top + Edge, bottom = p.Y >= b.Bottom - 1 - Edge;
            if (p.X < b.Left || p.X >= b.Right || p.Y < b.Top || p.Y >= b.Bottom) continue;
            (corner, action) = (left, right, top, bottom) switch
            {
                (true, _, true, _) => ("tl", config.HotCornerTopLeft),
                (_, true, true, _) => ("tr", config.HotCornerTopRight),
                (true, _, _, true) => ("bl", config.HotCornerBottomLeft),
                (_, true, _, true) => ("br", config.HotCornerBottomRight),
                _ => (null, null),
            };
            // Leaving the corner by more than a few pixels re-arms it.
            bool away = p.X > b.Left + Rearm && p.X < b.Right - Rearm || p.Y > b.Top + Rearm && p.Y < b.Bottom - Rearm;
            if (away) armedCorner = null;
            break;
        }

        if (corner == null || string.IsNullOrEmpty(action))
        {
            if (armedCorner?.StartsWith("pending:") == true) armedCorner = null; // left before it fired
            return;
        }
        if (corner == armedCorner) return;
        if (armedCorner != "pending:" + corner)
        {
            armedCorner = "pending:" + corner; // must stay a moment, so flicking past a corner doesn't trigger
            enteredAt = DateTime.Now;
            return;
        }
        if (DateTime.Now - enteredAt < TimeSpan.FromMilliseconds(150)) return;
        armedCorner = corner;
        if (!FullscreenAppInFront()) Run(action);
    }

    static bool FullscreenAppInFront()
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == (uint)Environment.ProcessId || WindowTracker.GetClass(fg) is "Progman" or "WorkerW") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        var screen = Screens.Of(fg).BoundsPx;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }

    public static void Run(string action)
    {
        switch (action)
        {
            case "missioncontrol": App.MissionControl.Toggle(); break;
            case "desktop": Native.SendCombo(0x5B, (byte)'D'); break;
            case "launchpad": App.Launchpad.Toggle(); break;
            case "start": App.StartMenu.Toggle(); break;
            case "notifications": App.NotificationCenter.Toggle(fromTop: App.Config.IsDock); break;
            case "spotlight": App.Spotlight.Toggle(); break;
            case "lock": Native.LockWorkStation(); break;
        }
    }
}
