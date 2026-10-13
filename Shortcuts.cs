using System.IO;
using System.Text.RegularExpressions;

namespace MacLook;

/// <summary>Resolves .lnk shortcuts and finds Start menu shortcuts.</summary>
internal static class Shortcuts
{
    static Dictionary<string, string>? byExe, byName;

    static (string Target, string Args) Resolve(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(lnk);
            return ((string)shortcut.TargetPath, (string)shortcut.Arguments);
        }
        catch { return ("", ""); }
    }

    public static string GetTargetPath(string lnk) => Resolve(lnk).Target;

    /// <summary>Normalized exe name a shortcut launches, e.g. "discord.exe", or "".</summary>
    public static string GetTargetExe(string lnk)
    {
        var (target, args) = Resolve(lnk);
        var name = Path.GetFileName(target);
        // Squirrel apps (Discord, etc.) launch through Update.exe --processStart X.exe
        if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(args, @"--processStart\s+""?([^""\s]+)");
            if (m.Success) name = m.Groups[1].Value;
        }
        return name.Length == 0 ? "" : WindowTracker.Normalize(name);
    }

    /// <summary>Start menu shortcut that launches the given exe (normalized name), if any.</summary>
    public static string? FindInStartMenu(string exeKey)
    {
        BuildIndex();
        return byExe!.TryGetValue(exeKey, out var path) ? path : null;
    }

    /// <summary>Start menu shortcut whose title is exactly <paramref name="name"/>, if any.</summary>
    public static string? FindByName(string name)
    {
        BuildIndex();
        return byName!.TryGetValue(name, out var path) ? path : null;
    }

    static readonly object IndexLock = new();

    /// <summary>Builds the index ahead of time (it resolves every Start menu shortcut, which takes a moment).</summary>
    public static void Warm()
    {
        var thread = new Thread(BuildIndex) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    static void BuildIndex()
    {
        lock (IndexLock)
            if (byExe == null) FillIndex();
    }

    static void FillIndex()
    {
        var exes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var root in roots.Where(Directory.Exists))
            foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                var name = Path.GetFileNameWithoutExtension(lnk);
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("kaldır", StringComparison.OrdinalIgnoreCase)) continue;
                names.TryAdd(name, lnk);
                var exe = GetTargetExe(lnk);
                if (exe.Length > 0) exes.TryAdd(exe, lnk);
            }
        byName = names;
        byExe = exes; // set last: it's the "ready" flag
    }
}
