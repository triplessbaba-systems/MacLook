using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace MacLook;

/// <summary>Asks GitHub for the newest MacLook release (at most once a day, if enabled).</summary>
internal static class Updater
{
    const string Repo = "triplessbaba-systems/MacLook";

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    /// <summary>Set when a newer release exists.</summary>
    public static (string Version, string Url)? Available { get; private set; }

    /// <param name="force">Check now even if it was done recently or automatic checks are off.</param>
    /// <returns>true if a newer version is available.</returns>
    public static async Task<bool> CheckAsync(bool force = false)
    {
        var config = App.Config;
        if (!force && (!config.CheckForUpdates || DateTime.Now - config.LastUpdateCheck < TimeSpan.FromHours(20)))
            return Available != null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MacLook/" + CurrentText);
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? "";
            var url = doc.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{Repo}/releases";
            config.LastUpdateCheck = DateTime.Now;
            config.Save();
            Available = Version.TryParse(tag, out var latest) && latest > Current ? (tag, url) : null;
        }
        catch (Exception ex) { App.Log("update check: " + ex.Message); }
        return Available != null;
    }
}
