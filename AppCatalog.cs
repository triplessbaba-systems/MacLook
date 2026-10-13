using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace MacLook;

internal sealed record AppInfo(string Name, string Id, ImageSource? Icon)
{
    public string LaunchPath => @"shell:AppsFolder\" + Id;
}

/// <summary>
/// Every installed app, as listed in the shell's Apps folder (the same list the Start menu uses,
/// including Microsoft Store apps). Loaded on a background thread.
/// </summary>
internal static class AppCatalog
{
    static readonly Guid FolderIdAppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    const uint SIGDN_NORMALDISPLAY = 0, SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    static readonly string[] JunkWords =
        ["uninstall", "kaldır", "readme", "benioku", "website", "web site", "documentation", "manual", "release notes", "license", "lisans",
         "check for updates", "configure java", "about java"];
    static readonly string[] JunkExtensions = [".url", ".txt", ".htm", ".html", ".chm", ".pdf", ".rtf", ".ini", ".log"];

    public static IReadOnlyList<AppInfo> Apps { get; private set; } = [];
    public static event Action? Updated;
    static int loading;

    public static void Refresh()
    {
        if (Interlocked.Exchange(ref loading, 1) == 1) return;
        var dispatcher = Application.Current.Dispatcher;
        var thread = new Thread(() =>
        {
            List<AppInfo> apps;
            try { apps = Enumerate(); }
            catch { apps = []; }
            dispatcher.InvokeAsync(() =>
            {
                if (apps.Count > 0)
                {
                    Apps = apps;
                    Updated?.Invoke();
                }
                loading = 0;
            });
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); // shell COM objects want STA
        thread.Start();
    }

    static List<AppInfo> Enumerate()
    {
        var result = new List<AppInfo>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        Native.SHGetKnownFolderItem(FolderIdAppsFolder, 0, IntPtr.Zero, typeof(IShellItem).GUID, out var folder);
        folder.BindToHandler(IntPtr.Zero, BhidEnumItems, typeof(IEnumShellItems).GUID, out var enumPtr);
        var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
        Marshal.Release(enumPtr);

        while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item != null)
        {
            try
            {
                var name = DisplayName(item, SIGDN_NORMALDISPLAY);
                var id = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
                if (IsJunk(name, id) || !seen.Add(name)) continue;
                var icon = IconLoader.FromFactory((IShellItemImageFactory)item, 128);
                result.Add(new AppInfo(name, id, icon));
            }
            catch { }
            finally { Marshal.ReleaseComObject(item); }
        }
        Marshal.ReleaseComObject(items);
        Marshal.ReleaseComObject(folder);

        var turkish = StringComparer.Create(new CultureInfo("tr-TR"), true);
        return result.OrderBy(a => a.Name, turkish).ToList();
    }

    static string DisplayName(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out var ptr);
        try { return Marshal.PtrToStringUni(ptr) ?? ""; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    static bool IsJunk(string name, string id)
    {
        if (name.Length == 0 || id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        if (JunkExtensions.Any(ext => id.EndsWith(ext, StringComparison.OrdinalIgnoreCase))) return true;
        var lower = name.ToLower(new CultureInfo("tr-TR"));
        return JunkWords.Any(lower.Contains);
    }
}
