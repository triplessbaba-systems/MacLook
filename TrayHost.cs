using ManagedShell;
using ManagedShell.Interop;
using ManagedShell.WindowsTray;
using Microsoft.Win32;
using System.IO;

namespace MacLook;

/// <summary>
/// Real system tray icons (Discord, Steam, ...) via ManagedShell, the library RetroBar uses.
/// It receives the icons apps register with the shell and forwards clicks back to them.
/// </summary>
internal static class TrayHost
{
    static ShellManager? shell;
    static readonly Dictionary<string, bool> promoted = new(StringComparer.OrdinalIgnoreCase);
    static DateTime promotedRead;

    // Windows stores tray icon paths with known-folder GUIDs in front.
    static readonly Dictionary<string, string> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
    };

    public static NotificationArea? Area => shell?.NotificationArea;

    public static void Start()
    {
        try
        {
            var config = ShellManager.DefaultShellConfig;
            config.EnableTasksService = false;
            config.AutoStartTasksService = false;
            config.EnableTrayService = true;
            config.AutoStartTrayService = true;
            shell = new ShellManager(config);
        }
        catch (Exception ex)
        {
            App.Log("tray failed: " + ex.Message);
            shell = null;
        }
    }

    public static void Stop()
    {
        try { shell?.Dispose(); }
        catch { }
        shell = null;
    }

    /// <summary>Tells apps where the tray is, so their popups open next to it.</summary>
    public static void SetBarRect(RECT px, uint edge)
    {
        Area?.SetTrayHostSizeData(new TrayHostSizeData
        {
            edge = edge == AppBar.Top ? NativeMethods.ABEdge.ABE_TOP : NativeMethods.ABEdge.ABE_BOTTOM,
            rc = new NativeMethods.Rect { Left = px.Left, Top = px.Top, Right = px.Right, Bottom = px.Bottom },
        });
    }

    /// <summary>Windows' own "show on taskbar" setting for an icon (Settings → Taskbar → Other system tray icons).</summary>
    public static bool IsPromoted(NotifyIcon icon)
    {
        if (DateTime.Now - promotedRead > TimeSpan.FromSeconds(5)) ReadPromoted();
        return icon.Path is string path && promoted.TryGetValue(path, out var shown) && shown;
    }

    static void ReadPromoted()
    {
        promotedRead = DateTime.Now;
        promoted.Clear();
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root == null) return;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("ExecutablePath") is not string path) continue;
            foreach (var (guid, folder) in KnownFolders)
                if (path.StartsWith(guid, StringComparison.OrdinalIgnoreCase))
                    path = Path.Combine(folder, path[(guid.Length + 1)..]);
            bool shown = key.GetValue("IsPromoted") is int v && v == 1;
            promoted[path] = promoted.GetValueOrDefault(path) || shown;
        }
    }
}
