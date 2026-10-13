using System.IO;
using System.Text.Json;

namespace MacLook;

internal sealed class PinnedApp
{
    public string Name { get; set; } = "";
    /// <summary>What gets launched: an exe or a .lnk shortcut.</summary>
    public string Path { get; set; } = "";
    /// <summary>Exe file name used to match running windows, e.g. "chrome.exe".</summary>
    public string MatchExe { get; set; } = "";
}

internal sealed class Config
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacLook");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public List<PinnedApp> Pinned { get; set; } = new();
    /// <summary>Apps pinned in the Start menu (Apps folder IDs); null until first filled with defaults.</summary>
    public List<string>? StartPins { get; set; }
    /// <summary>Windows key opens MacLook's Start menu instead of Windows'.</summary>
    public bool WinKeyOpensStart { get; set; } = true;
    /// <summary>Alt+Tab shows the macOS-style app switcher.</summary>
    public bool MacAppSwitcher { get; set; } = true;

    public static Config Load()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch { }
        var config = CreateDefault();
        config.Save();
        return config;
    }

    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));

    // Open on first run doesn't mean the user wants them on the bar.
    static readonly HashSet<string> SystemTools = ["taskmgr.exe", "systemsettings.exe", "applicationframehost.exe", "mmc.exe", "control.exe"];

    /// <summary>First run: File Explorer, apps pinned to the Windows taskbar and apps that are open right now.</summary>
    static Config CreateDefault()
    {
        var config = new Config();
        config.Pinned.Add(new PinnedApp
        {
            Name = "Dosya Gezgini",
            Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            MatchExe = "explorer.exe",
        });
        bool Has(string key) => config.Pinned.Any(p => p.MatchExe == key);

        var pinnedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (Directory.Exists(pinnedDir))
            foreach (var lnk in Directory.GetFiles(pinnedDir, "*.lnk").OrderBy(f => f))
            {
                var key = Shortcuts.GetTargetExe(lnk);
                if (key.Length > 0 && !Has(key))
                    config.Pinned.Add(new PinnedApp { Name = Path.GetFileNameWithoutExtension(lnk), Path = lnk, MatchExe = key });
            }

        foreach (var w in WindowTracker.GetWindows())
            if (!Has(w.Key) && !SystemTools.Contains(w.Key))
                config.Pinned.Add(CreatePin(w));
        return config;
    }

    /// <summary>
    /// Pin for a running app. Prefers its Start menu shortcut (exe paths change on updates);
    /// Store apps are launched through shell:AppsFolder since their exe can't be started directly.
    /// </summary>
    public static PinnedApp CreatePin(AppWindow window)
    {
        var lnk = Shortcuts.FindInStartMenu(window.Key);
        return new PinnedApp
        {
            Name = lnk != null ? Path.GetFileNameWithoutExtension(lnk) : WindowTracker.GetAppName(window.ExePath),
            Path = lnk ?? WindowTracker.GetIconPath(window),
            MatchExe = window.Key,
        };
    }
}
