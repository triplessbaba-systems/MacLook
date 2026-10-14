using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace MacLook;

internal sealed record SpotlightResult(string Title, string Subtitle, string Kind, ImageSource? Icon, string? Glyph, Action Open, string? FilePath = null);

/// <summary>What Spotlight searches: apps, Windows settings pages, a calculator, indexed files and the web.</summary>
internal static class SpotlightSearch
{
    static readonly CultureInfo Turkish = new("tr-TR");
    static readonly CompareInfo Compare = Turkish.CompareInfo;
    static readonly Regex MathExpression = new(@"^[\d\s.,+\-*/x×÷()]+$");

    // Windows Settings pages, by the names people look for.
    static readonly (string Name, string Uri)[] SettingsPages =
    [
        ("Ekran", "ms-settings:display"), ("Ses", "ms-settings:sound"), ("Bluetooth ve cihazlar", "ms-settings:bluetooth"),
        ("Wi-Fi", "ms-settings:network-wifi"), ("Ağ ve İnternet", "ms-settings:network"), ("Bildirimler", "ms-settings:notifications"),
        ("Güç ve pil", "ms-settings:powersleep"), ("Depolama", "ms-settings:storagesense"), ("Uygulamalar", "ms-settings:appsfeatures"),
        ("Varsayılan uygulamalar", "ms-settings:defaultapps"), ("Başlangıç uygulamaları", "ms-settings:startupapps"),
        ("Hesaplar", "ms-settings:accounts"), ("Tarih ve saat", "ms-settings:dateandtime"), ("Dil ve bölge", "ms-settings:regionlanguage"),
        ("Klavye", "ms-settings:keyboard"), ("Fare", "ms-settings:mousetouchpad"), ("Yazıcılar ve tarayıcılar", "ms-settings:printers"),
        ("Windows Update", "ms-settings:windowsupdate"), ("Gizlilik ve güvenlik", "ms-settings:privacy"), ("Kişiselleştirme", "ms-settings:personalization"),
        ("Arka plan (duvar kâğıdı)", "ms-settings:personalization-background"), ("Renkler (açık / koyu mod)", "ms-settings:colors"),
        ("Temalar", "ms-settings:themes"), ("Görev çubuğu", "ms-settings:taskbar"), ("Gece ışığı", "ms-settings:nightlight"),
        ("Odak", "ms-settings:focus"), ("Erişilebilirlik", "ms-settings:easeofaccess"), ("Oyun", "ms-settings:gaming"),
        ("Bu bilgisayar hakkında", "ms-settings:about"), ("Kurtarma", "ms-settings:recovery"), ("Ekran kaydı ve yakalama", "ms-settings:gaming-gamedvr"),
    ];

    /// <summary>Instant results: calculator, apps and settings pages.</summary>
    public static List<SpotlightResult> Local(string query)
    {
        var results = new List<SpotlightResult>();
        if (Calculate(query) is string answer)
            results.Add(new SpotlightResult("= " + answer, query, "Hesap Makinesi", null, Glyphs.Calculator, () => Clipboard.SetText(answer)));

        foreach (var app in AppCatalog.Apps
                     .Where(a => Compare.IndexOf(a.Name, query, CompareOptions.IgnoreCase) >= 0)
                     .OrderBy(a => Compare.IsPrefix(a.Name, query, CompareOptions.IgnoreCase) ? 0 : 1)
                     .ThenBy(a => a.Name.Length)
                     .Take(6))
            results.Add(new SpotlightResult(app.Name, "Uygulama", "Uygulama", app.Icon, null, () => Ui.Open(app.LaunchPath)));

        foreach (var (name, uri) in SettingsPages.Select(p => (Name: L.T(p.Name), p.Uri)).Where(p => Compare.IndexOf(p.Name, query, CompareOptions.IgnoreCase) >= 0).Take(3))
            results.Add(new SpotlightResult(name, "Windows Ayarları", "Ayarlar", null, Glyphs.Settings, () => Ui.Open(uri)));
        return results;
    }

    /// <summary>The web search row that always ends the list.</summary>
    public static SpotlightResult Web(string query) =>
        new(string.Format(L.T("\"{0}\" için web'de ara"), query), "Varsayılan tarayıcıda", "Web", null, Glyphs.Globe,
            () => Ui.Open("https://www.google.com/search?q=" + Uri.EscapeDataString(query)));

    // Developer and system clutter nobody means when searching for "their" files.
    static readonly string[] SkippedFolders = [@"\appdata\", @"\node_modules\", @"\obj\", @"\bin\", @"\packages\", @"\$recycle.bin\"];

    /// <summary>Files and folders in the user's profile, from the Windows Search index.</summary>
    public static List<SpotlightResult> Files(string query, CancellationToken cancel)
    {
        var found = new List<(string Path, string Name, string DisplayFolder, DateTime Modified)>();
        try
        {
            var scope = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
            var like = query.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
            using var connection = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            connection.Open();
            // ItemUrl holds the real path; ItemPathDisplay is translated ("C:\Kullanıcılar\...") and only good for showing.
            using var command = new OleDbCommand(
                "SELECT TOP 40 System.ItemUrl, System.ItemNameDisplay, System.ItemFolderPathDisplay, System.DateModified FROM SystemIndex " +
                $"WHERE SCOPE='file:{scope}' AND System.ItemNameDisplay LIKE '%{like}%' ORDER BY System.DateModified DESC", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read() && !cancel.IsCancellationRequested)
            {
                if (reader[0] is not string url || reader[1] is not string name || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
                var path = url[5..].Replace('/', '\\');
                var lower = path.ToLowerInvariant();
                if (SkippedFolders.Any(lower.Contains) || path.Split('\\').Any(part => part.StartsWith('.'))) continue;
                found.Add((path, name, reader[2] as string ?? "", reader[3] is DateTime modified ? modified : DateTime.MinValue));
            }
        }
        catch (Exception ex) { App.Log("spotlight files: " + ex.Message); }

        // Names that start with what was typed come first, then the most recently changed.
        return found
            .OrderBy(f => Compare.IsPrefix(f.Name, query, CompareOptions.IgnoreCase) ? 0 : 1)
            .ThenByDescending(f => f.Modified)
            .Take(5)
            .Select(f =>
            {
                bool isFolder = Directory.Exists(f.Path);
                return new SpotlightResult(f.Name, f.DisplayFolder, isFolder ? "Klasör" : "Belge", null, isFolder ? Glyphs.Folder : Glyphs.Document,
                    () => Ui.Open(f.Path), f.Path);
            })
            .ToList();
    }

    /// <summary>"12*7" → "84"; null when the text isn't arithmetic.</summary>
    static string? Calculate(string query)
    {
        if (!MathExpression.IsMatch(query) || !query.Any(char.IsDigit) || !query.Any(c => "+-*/x×÷".Contains(c))) return null;
        try
        {
            var expression = query.Replace('x', '*').Replace('×', '*').Replace('÷', '/').Replace(',', '.');
            var value = Convert.ToDouble(new DataTable().Compute(expression, null), CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || double.IsInfinity(value)) return null;
            return value.ToString("#,0.##########", L.Culture);
        }
        catch { return null; }
    }
}
