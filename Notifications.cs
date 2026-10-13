using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MacLook;

internal sealed record NotificationItem(uint Id, string AppName, string AppId, ImageSource? Icon, string Title, string Body, DateTime Time);

/// <summary>Reads (and clears) the notifications Windows shows in its notification center.</summary>
internal static class Notifications
{
    static UserNotificationListener? listener;
    static readonly Dictionary<string, ImageSource?> Icons = new();

    public static async Task<List<NotificationItem>?> GetAsync()
    {
        try
        {
            listener ??= UserNotificationListener.Current;
            if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed) return null;
            var result = new List<NotificationItem>();
            foreach (var n in (await listener.GetNotificationsAsync(NotificationKinds.Toast)).OrderByDescending(n => n.CreationTime))
            {
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements()
                    .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
                if (texts.Count == 0) continue;
                var appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
                var appId = n.AppInfo?.AppUserModelId ?? "";
                result.Add(new NotificationItem(n.Id, appName, appId, await IconAsync(n), texts[0], string.Join("\n", texts.Skip(1)), n.CreationTime.LocalDateTime));
            }
            return result;
        }
        catch { return null; }
    }

    public static void Remove(uint id)
    {
        try { listener?.RemoveNotification(id); }
        catch { }
    }

    public static void ClearAll()
    {
        try { listener?.ClearNotifications(); }
        catch { }
    }

    static async Task<ImageSource?> IconAsync(UserNotification n)
    {
        var appId = n.AppInfo?.AppUserModelId ?? "";
        if (Icons.TryGetValue(appId, out var cached)) return cached;
        // The Apps folder icon is the crisp one Launchpad uses; the notification logo is a small, padded fallback.
        var icon = appId.Length > 0 ? IconLoader.Get(@"shell:AppsFolder\" + appId, 64) : null;
        if (icon != null) return Icons[appId] = icon;
        try
        {
            var logo = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logo != null)
            {
                using var stream = (await logo.OpenReadAsync()).AsStreamForRead();
                var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                copy.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = copy;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                icon = image;
            }
        }
        catch { }
        return Icons[appId] = icon;
    }
}
