using System.IO;
using System.Diagnostics;
using System.Text;

namespace MacLook;

internal sealed record AppWindow(IntPtr Handle, string Title, string ExePath, string Key, uint Pid);

/// <summary>Finds the windows that would normally appear on the taskbar.</summary>
internal static class WindowTracker
{
    static readonly uint OwnPid = (uint)Environment.ProcessId;
    static readonly HashSet<string> IgnoredClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    // Some apps draw their main window from a helper process.
    static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steamwebhelper.exe"] = "steam.exe",
    };
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string exe)
    {
        var name = Path.GetFileName(exe).ToLowerInvariant();
        return Aliases.TryGetValue(name, out var alias) ? alias : name;
    }

    /// <summary>Taskbar-style windows in z-order (topmost first).</summary>
    public static List<AppWindow> GetWindows()
    {
        var list = new List<AppWindow>();
        Native.EnumWindows((h, _) =>
        {
            if (IsAppWindow(h) && GetExePath(h, out var pid) is string exe)
                list.Add(new AppWindow(h, GetTitle(h), exe, Normalize(exe), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsAppWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool forceShow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!forceShow && ((ex & Native.WS_EX_TOOLWINDOW) != 0 || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero)) return false;
        if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        return !IgnoredClasses.Contains(GetClass(h));
    }

    public static string GetTitle(IntPtr h)
    {
        var sb = new StringBuilder(Native.GetWindowTextLength(h) + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        Native.GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Exe behind a window, or null for our own windows.</summary>
    public static string? GetExePath(IntPtr hWnd) => GetExePath(hWnd, out _);

    public static string? GetExePath(IntPtr hWnd, out uint pid)
    {
        Native.GetWindowThreadProcessId(hWnd, out pid);
        if (pid == OwnPid) return null;
        var path = GetProcessPath(pid);
        if (path != null && path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Store apps live in a child CoreWindow owned by the real app process.
            uint framePid = pid, innerPid = 0;
            Native.EnumChildWindows(hWnd, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out var childPid);
                if (childPid == framePid) return true;
                innerPid = childPid;
                return false;
            }, IntPtr.Zero);
            if (innerPid != 0 && GetProcessPath(innerPid) is string inner)
            {
                path = inner;
                pid = innerPid;
            }
        }
        return path;
    }

    static readonly Dictionary<string, string> IconPathCache = new();

    /// <summary>Best path for an app's icon: its Store app ID for packaged apps (exe icons are generic there), else the exe.</summary>
    public static string GetIconPath(AppWindow w)
    {
        if (IconPathCache.TryGetValue(w.Key, out var path)) return path;
        path = w.ExePath;
        if (w.ExePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) && GetAppUserModelId(w.Pid) is string appId)
            path = @"shell:AppsFolder\" + appId;
        return IconPathCache[w.Key] = path;
    }

    /// <summary>App ID of a Microsoft Store (packaged) app process, used to launch it via shell:AppsFolder.</summary>
    public static string? GetAppUserModelId(uint pid)
    {
        var h = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint length = 512;
            var sb = new StringBuilder((int)length);
            return Native.GetApplicationUserModelId(h, ref length, sb) == 0 ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    public static string? GetProcessPath(uint pid)
    {
        var h = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return Native.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    public static string GetAppName(string exePath)
    {
        if (NameCache.TryGetValue(exePath, out var cached)) return cached;
        string name;
        if (Normalize(exePath) == "explorer.exe") name = L.T("Dosya Gezgini");
        else
        {
            try { name = FileVersionInfo.GetVersionInfo(exePath).FileDescription?.Trim() ?? ""; }
            catch { name = ""; }
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(exePath);
        }
        return NameCache[exePath] = name;
    }

    public static void Activate(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        if (Native.SetForegroundWindow(h)) return;

        // Windows sometimes refuses; borrowing the foreground thread's input state gets around it.
        var fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var ownThread = Native.GetCurrentThreadId();
        Native.AttachThreadInput(ownThread, fgThread, true);
        Native.SetForegroundWindow(h);
        Native.BringWindowToTop(h);
        Native.AttachThreadInput(ownThread, fgThread, false);
    }
}
