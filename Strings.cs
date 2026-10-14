using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Markup;

namespace MacLook;

/// <summary>
/// Interface language. Texts are written in Turkish in the code; in English mode they are looked up
/// in <see cref="English"/> (anything missing simply stays Turkish).
/// </summary>
internal static class L
{
    static readonly CultureInfo TurkishCulture = new("tr-TR"), EnglishCulture = new("en-US");

    /// <summary>"tr" or "en"; when not set, follows the Windows display language.</summary>
    public static bool IsEnglish =>
        (Override ?? App.ConfigOrNull?.Language ?? SavedLanguage.Value ?? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en")) == "en";

    /// <summary>Language forced for this run only (--snapshot &lt;folder&gt; en), never saved.</summary>
    public static string? Override;

    // Before the settings are loaded (e.g. --uninstall), read just the language from the file.
    static readonly Lazy<string?> SavedLanguage = new(() =>
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Config.FilePath));
            return json.RootElement.TryGetProperty(nameof(Config.Language), out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch { return null; }
    });

    /// <summary>Culture for dates, times and case-insensitive search.</summary>
    public static CultureInfo Culture => IsEnglish ? EnglishCulture : TurkishCulture;

    public static string T(string turkish) => IsEnglish && English.TryGetValue(turkish, out var english) ? english : turkish;

    /// <summary>"%40" in Turkish, "40%" in English.</summary>
    public static string Percent(double value) => IsEnglish ? $"{value}%" : $"%{value}";

    static readonly Dictionary<string, string> English = new()
    {
        // Menu bar
        ["Bu Bilgisayar Hakkında"] = "About This PC",
        ["Sistem Ayarları…"] = "System Settings…",
        ["Görev Yöneticisi…"] = "Task Manager…",
        ["Görev Yöneticisi"] = "Task Manager",
        ["Uyku"] = "Sleep",
        ["Yeniden Başlat…"] = "Restart…",
        ["Kapat…"] = "Shut Down…",
        ["Ekranı Kilitle"] = "Lock Screen",
        ["MacLook Ayarları…"] = "MacLook Settings…",
        ["MacLook Ayarları"] = "MacLook Settings",
        ["MacLook'tan Çık"] = "Quit MacLook",
        ["Güncelleme Var:"] = "Update Available:",
        ["Bilgisayar yeniden başlatılsın mı?"] = "Restart the computer?",
        ["Bilgisayar kapatılsın mı?"] = "Shut down the computer?",
        ["Masaüstü"] = "Desktop",
        ["Gizle"] = "Hide",
        ["Diğerlerini Gizle"] = "Hide Others",
        ["Çık"] = "Quit",
        ["Dosya"] = "File",
        ["Yeni Pencere"] = "New Window",
        ["Yeni Sekme"] = "New Tab",
        ["Aç…"] = "Open…",
        ["Kaydet"] = "Save",
        ["Farklı Kaydet…"] = "Save As…",
        ["Yazdır…"] = "Print…",
        ["Kapat"] = "Close",
        ["Düzen"] = "Edit",
        ["Geri Al"] = "Undo",
        ["Yinele"] = "Redo",
        ["Kes"] = "Cut",
        ["Kopyala"] = "Copy",
        ["Yapıştır"] = "Paste",
        ["Tümünü Seç"] = "Select All",
        ["Bul…"] = "Find…",
        ["Görünüm"] = "View",
        ["Yakınlaştır"] = "Zoom In",
        ["Uzaklaştır"] = "Zoom Out",
        ["Gerçek Boyut"] = "Actual Size",
        ["Yenile"] = "Reload",
        ["Tam Ekran"] = "Enter Full Screen",
        ["Pencere"] = "Window",
        ["Küçült"] = "Minimize",
        ["Büyüt"] = "Zoom",
        ["Ekranın Soluna Yasla"] = "Move Window to Left of Screen",
        ["Ekranın Sağına Yasla"] = "Move Window to Right of Screen",
        ["Tümünü Öne Getir"] = "Bring All to Front",
        ["Yardım"] = "Help",
        ["Windows Yardımı"] = "Windows Help",
        ["MacLook Kısayolları"] = "MacLook Shortcuts",
        ["Ara"] = "Search",
        ["Denetim Merkezi"] = "Control Center",
        [MenuBarWindow.ShortcutHelp] =
            "Windows key / logo at the bottom left  →  Start menu\n" +
            "Win+A / network-sound icon  →  Control Center\n" +
            "Click the clock  →  Notification Center and calendar\n" +
            "Bottom right corner / Win+D  →  Show desktop\n" +
            "Alt+Tab  →  App switcher\n" +
            "     While holding Alt: pick with Tab / Shift+Tab / ← →,  Q: quit the app,  Esc: cancel\n\n" +
            "Bottom bar:\n" +
            "     click: open / bring to front / minimize,  middle click: new window,  right click: menu\n" +
            "     drag: reorder,  drag up and drop: remove from the bar\n" +
            "     mouse wheel over the sound icon: volume\n" +
            "     ^: hidden tray icons (which ones stay on the bar: Settings → Taskbar → Other system tray icons)\n\n" +
            "Menu bar: the File, Edit and View menus send the shortcut to the app in front\n" +
            "Search icon / Alt+Space  →  Spotlight\n" +
            "Top left corner / Window menu (Win+Tab: turn on in Settings)  →  Mission Control\n" +
            "Launchpad: drag an icon → reorder,  drop it on another icon → folder",

        // Bar and Dock
        ["Başlat"] = "Start",
        ["Masaüstünü göster"] = "Show desktop",
        ["Masaüstünü Göster"] = "Show Desktop",
        ["Kaldır"] = "Remove",
        ["Aç"] = "Open",
        ["Dock'tan Kaldır"] = "Remove from Dock",
        ["Bardan Kaldır"] = "Remove from Bar",
        ["Dock'ta Tut"] = "Keep in Dock",
        ["Barda Tut"] = "Keep in Bar",
        ["Kişiselleştirme Ayarları…"] = "Personalization Settings…",
        ["Tepsi Simgesi Ayarları…"] = "Tray Icon Settings…",
        ["Gizli simgeleri göster"] = "Show hidden icons",
        ["Klavye dili (Win+Boşluk)"] = "Keyboard language (Win+Space)",
        ["Denetim Merkezi (tekerlek: ses)"] = "Control Center (scroll: volume)",
        ["Dosya Gezgini"] = "File Explorer",

        // Start menu
        ["MacLook Başlat"] = "MacLook Start",
        ["Uygulama ve belge ara"] = "Search apps and documents",
        ["Tüm Uygulamalar  ›"] = "All Apps  ›",
        ["Tüm Uygulamalar"] = "All Apps",
        ["Sabitlenenler"] = "Pinned",
        ["Önerilenler"] = "Recommended",
        ["Son açtığın dosyalar burada görünecek."] = "Files you open will show up here.",
        ["‹  Geri"] = "‹  Back",
        ["Hesap ayarları"] = "Account settings",
        ["En iyi eşleşmeler"] = "Best matches",
        ["Sonuç bulunamadı"] = "No results",
        ["Uygulama"] = "App",
        ["Uygulamalar"] = "Apps",
        ["Belgeler"] = "Documents",
        ["Başlangıçtan Kaldır"] = "Unpin from Start",
        ["Başlangıca Sabitle"] = "Pin to Start",
        ["Bara Sabitle"] = "Pin to Bar",
        ["Kilitle"] = "Lock",
        ["Oturumu Kapat…"] = "Sign Out…",
        ["Oturum kapatılsın mı?"] = "Sign out?",
        ["Az önce"] = "Just now",
        [" dakika önce"] = " min ago",
        [" saat önce"] = " hr ago",
        ["Dün"] = "Yesterday",

        // Control Center
        ["MacLook Denetim Merkezi"] = "MacLook Control Center",
        ["Odak"] = "Focus",
        ["Bildirimler"] = "Notifications",
        ["Gece Işığı"] = "Night Light",
        ["Yansıt"] = "Cast",
        ["Erişilebilirlik"] = "Accessibility",
        ["Güç"] = "Power",
        ["Ayarlar"] = "Settings",
        ["Ekran"] = "Display",
        ["Ses"] = "Sound",
        ["Çıkış aygıtı"] = "Output device",
        ["Çıkış aygıtı bulunamadı"] = "No output devices",
        ["Ses Ayarları…"] = "Sound Settings…",
        ["Ağ"] = "Network",
        ["Bağlı"] = "Connected",
        ["Bağlı değil"] = "Not connected",
        ["Yakın Paylaşım"] = "Nearby Sharing",
        ["Açık"] = "On",
        ["Kapalı"] = "Off",

        // Notification Center
        ["MacLook Bildirim Merkezi"] = "MacLook Notification Center",
        ["Bildirim Merkezi"] = "Notification Center",
        ["Önceki ay"] = "Previous month",
        ["Bugün"] = "Today",
        ["Sonraki ay"] = "Next month",
        ["Tümünü Temizle"] = "Clear All",
        ["Yeni bildirim yok"] = "No new notifications",
        ["Bildirimlere erişilemiyor"] = "Can't read notifications",
        ["+{0} bildirim daha"] = "+{0} more notifications",

        // Spotlight
        ["Spotlight Araması"] = "Spotlight Search",
        ["Hesap Makinesi"] = "Calculator",
        ["Windows Ayarları"] = "Windows Settings",
        ["Sistem Ayarları"] = "System Settings",
        ["\"{0}\" için web'de ara"] = "Search the web for \"{0}\"",
        ["Varsayılan tarayıcıda"] = "In your default browser",
        ["Klasör"] = "Folder",
        ["Belge"] = "Document",
        ["Bluetooth ve cihazlar"] = "Bluetooth & devices",
        ["Ağ ve İnternet"] = "Network & internet",
        ["Güç ve pil"] = "Power & battery",
        ["Depolama"] = "Storage",
        ["Varsayılan uygulamalar"] = "Default apps",
        ["Başlangıç uygulamaları"] = "Startup apps",
        ["Hesaplar"] = "Accounts",
        ["Tarih ve saat"] = "Date & time",
        ["Dil ve bölge"] = "Language & region",
        ["Klavye"] = "Keyboard",
        ["Fare"] = "Mouse",
        ["Yazıcılar ve tarayıcılar"] = "Printers & scanners",
        ["Gizlilik ve güvenlik"] = "Privacy & security",
        ["Kişiselleştirme"] = "Personalization",
        ["Arka plan (duvar kâğıdı)"] = "Background (wallpaper)",
        ["Renkler (açık / koyu mod)"] = "Colors (light / dark mode)",
        ["Temalar"] = "Themes",
        ["Görev çubuğu"] = "Taskbar",
        ["Gece ışığı"] = "Night light",
        ["Oyun"] = "Gaming",
        ["Bu bilgisayar hakkında"] = "About this PC",
        ["Kurtarma"] = "Recovery",
        ["Ekran kaydı ve yakalama"] = "Screen recording & capture",

        // Launchpad
        ["Adı değiştirmek için tıkla"] = "Click to rename",
        ["Yeni Klasör"] = "New Folder",
        ["Klasörden Çıkar"] = "Remove from Folder",
        ["Klasörü Dağıt"] = "Ungroup Folder",

        // Hot corners
        ["Başlat menüsü"] = "Start menu",

        // Settings
        ["Bitti"] = "Done",
        ["ALT BAR"] = "BAR & DOCK",
        ["KLAVYE"] = "KEYBOARD",
        ["SICAK KÖŞELER"] = "HOT CORNERS",
        ["GENEL"] = "GENERAL",
        ["WINDOWS AYARLARI"] = "WINDOWS SETTINGS",
        ["Stil"] = "Style",
        ["Dock: ortada yüzen, büyüyen simgeler; saat ve tepsi menü çubuğuna geçer."] = "Dock: floating in the middle with growing icons; the clock and tray move to the menu bar.",
        ["Tam genişlik"] = "Full width",
        ["Otomatik gizle"] = "Automatically hide",
        ["Fare ekranın altına gelince görünür."] = "Shows up when the mouse reaches the bottom of the screen.",
        ["Simge boyutu"] = "Icon size",
        ["Dock'ta büyütme"] = "Magnification",
        ["Fare üstüne gelince simgeler büyür (Dock stili)."] = "Icons grow under the mouse (Dock style).",
        ["Windows tuşu MacLook Başlat menüsünü açsın"] = "Windows key opens the MacLook Start menu",
        ["Kapalıyken Windows'un kendi Başlat menüsü açılır. Win+A da Denetim Merkezi'ni açar."] = "When off, Windows' own Start menu opens. Win+A opens Control Center either way.",
        ["Alt+Tab macOS uygulama değiştiricisini açsın"] = "Alt+Tab opens the macOS app switcher",
        ["Kapalıyken Windows'un kendi Alt+Tab ekranı açılır."] = "When off, Windows' own Alt+Tab opens.",
        ["Alt+Boşluk Spotlight'ı açsın"] = "Alt+Space opens Spotlight",
        ["Uygulama, dosya, ayar, hesap makinesi ve web araması."] = "Search apps, files and settings, do quick math, search the web.",
        ["Win+Tab Mission Control'ü açsın"] = "Win+Tab opens Mission Control",
        ["Kapalıyken Windows'un Görev Görünümü açılır."] = "When off, Windows' Task View opens.",
        ["Sol üst"] = "Top left",
        ["Sağ üst"] = "Top right",
        ["Sol alt"] = "Bottom left",
        ["Sağ alt"] = "Bottom right",
        ["Dil"] = "Language",
        ["MacLook yeniden başlar."] = "MacLook restarts.",
        ["Bilgisayar açılınca MacLook'u başlat"] = "Start MacLook when you sign in",
        ["Güncellemeleri otomatik denetle"] = "Check for updates automatically",
        ["Günde bir kez GitHub'daki son sürüme bakar."] = "Looks for the latest release on GitHub once a day.",
        ["Şimdi denetle"] = "Check now",
        ["Sürüm"] = "Version",
        ["Denetleniyor…"] = "Checking…",
        ["Yeni sürüm:"] = "New version:",
        ["güncel"] = "up to date",
        ["Kur"] = "Install",
        ["Bu bilgisayara kur"] = "Install on this computer",
        ["Başlat menüsüne eklenir, Ayarlar → Uygulamalar'dan kaldırılabilir. Yönetici izni gerekmez."] = "Adds MacLook to the Start menu; remove it any time from Settings → Apps. No admin rights needed.",
        ["Bardaki tepsi simgelerini seç"] = "Choose the tray icons on the bar",
        ["Bildirim ayarları"] = "Notification settings",
        ["Duvar kâğıdı ve renkler"] = "Wallpaper and colors",
        ["Açık / koyu mod"] = "Light / dark mode",
        ["MacLook kapatılınca Windows görev çubuğu geri gelir. Kısayolların listesi: elma menüsü → Yardım → MacLook Kısayolları."] =
            "The Windows taskbar comes back when MacLook is closed. All shortcuts: Apple menu → Help → MacLook Shortcuts.",

        // Installer
        ["Windows'a macOS görünümü"] = "A macOS look for Windows",
        ["Kurulum tamamlanamadı:"] = "Installation failed:",
        ["MacLook bu bilgisayardan kaldırılsın mı?"] = "Remove MacLook from this computer?",
        ["MacLook kaldırıldı. Ayarların %APPDATA%\\MacLook klasöründe duruyor; istersen silebilirsin."] =
            "MacLook was removed. Your settings are still in %APPDATA%\\MacLook; delete that folder if you like.",
    };
}

/// <summary>XAML: Text="{l:T 'Tüm Uygulamalar'}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
