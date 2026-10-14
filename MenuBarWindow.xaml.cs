using Microsoft.Win32;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook;

public partial class MenuBarWindow : Window
{
    public const double BarHeight = 26;
    const byte CTRL = 0x11, SHIFT = 0x10, WIN = 0x5B;
    

    readonly DispatcherTimer fastTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly ImageBrush blurBrush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    readonly ScreenInfo screen;
    MenuItem appMenu = null!;
    IntPtr lastForeground, target;   // target: the app window the menus act on
    string targetKey = "";

    internal MenuBarWindow(ScreenInfo screen, bool primary)
    {
        InitializeComponent();
        this.screen = screen;
        Left = screen.Bounds.X;
        Top = screen.Bounds.Y;
        Width = screen.Bounds.Width;
        Height = BarHeight;

        BgBlur.Background = blurBrush;
        ApplyWallpaper();
        Wallpaper.Changed += ApplyWallpaper;
        BuildMenus();
        if (App.Config.IsDock) StatusHost.Content = new StatusArea(compact: true, showTray: primary);

        SourceInitialized += (_, _) =>
        {
            Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
            var bar = new AppBar(this, AppBar.Top, BarHeight, screen);
            bar.FullscreenChanged += fullscreen => Topmost = !fullscreen;
            bar.Register();
            if (primary && App.Config.IsDock)
            {
                var px = screen.BoundsPx;
                TrayHost.SetBarRect(new RECT { Left = px.Left, Top = px.Top, Right = px.Right, Bottom = px.Top + (int)(BarHeight * Screens.Scale) }, AppBar.Top);
            }
        };

        fastTimer.Tick += (_, _) => UpdateActiveApp();
        fastTimer.Start();
        Closed += (_, _) =>
        {
            fastTimer.Stop();
            Wallpaper.Changed -= ApplyWallpaper;
        };
    }

    void ApplyWallpaper()
    {
        blurBrush.ImageSource = Wallpaper.For(screen);
        blurBrush.Viewbox = new Rect(0, 0, Width, BarHeight);
        // Like macOS: white text over dark wallpapers, dark text over light ones.
        bool dark = Wallpaper.Luminance(screen, new Rect(0, 0, Width, BarHeight)) < 0.55;
        Resources["BarText"] = Theme.Brush(dark ? "#FFFFFFFF" : "#FF1D1D1F");
        Resources["BarHover"] = Theme.Brush(dark ? "#38FFFFFF" : "#1F000000");
        BgTint.Background = Theme.Brush(dark ? "#1F000000" : "#73FFFFFF");
    }

    // ---------- menus ----------

    void BuildMenus()
    {
        var logo = new Path { Data = Geometry.Parse(Glyphs.AppleLogo), Width = 13, Height = 16, Stretch = Stretch.Uniform, Margin = new Thickness(2, 0, 2, 1) };
        logo.SetResourceReference(Shape.FillProperty, "BarText");
        var system = TopMenu(logo);
        system.Items.Add(Ui.Item("Bu Bilgisayar Hakkında", () => Ui.Open("ms-settings:about")));
        system.Items.Add(new Separator());
        system.Items.Add(Ui.Item("Sistem Ayarları…", () => Ui.Open("ms-settings:")));
        system.Items.Add(Ui.Item("Microsoft Store…", () => Ui.Open("ms-windows-store:")));
        system.Items.Add(new Separator());
        system.Items.Add(Ui.Item("Görev Yöneticisi…", () => Ui.Open("taskmgr.exe"), "⌃⇧Esc"));
        system.Items.Add(new Separator());
        system.Items.Add(Ui.Item("Uyku", () => Native.SetSuspendState(false, false, false)));
        system.Items.Add(Ui.Item("Yeniden Başlat…", () => ConfirmShutdown("Bilgisayar yeniden başlatılsın mı?", "/r /t 0")));
        system.Items.Add(Ui.Item("Kapat…", () => ConfirmShutdown("Bilgisayar kapatılsın mı?", "/s /t 0")));
        system.Items.Add(new Separator());
        system.Items.Add(Ui.Item("Ekranı Kilitle", () => Native.LockWorkStation(), "⊞L"));
        system.Items.Add(new Separator());
        system.Items.Add(Ui.Item("MacLook Ayarları…", () => App.Settings.Open()));
        system.Items.Add(Ui.Item("MacLook'tan Çık", () => App.Quit("menü")));

        // A newer MacLook on GitHub shows up at the top of this menu.
        MenuItem? updateItem = null;
        system.SubmenuOpened += (_, _) =>
        {
            if (updateItem != null || Updater.Available is not { } update) return;
            updateItem = Ui.Item(L.T("Güncelleme Var:") + $" MacLook {update.Version}", () => Ui.Open(update.Url));
            updateItem.FontWeight = FontWeights.SemiBold;
            system.Items.Insert(0, updateItem);
            system.Items.Insert(1, new Separator());
        };

        appMenu = TopMenu("Masaüstü");
        appMenu.FontWeight = FontWeights.Bold;
        appMenu.Items.Add(Ui.Item("Gizle", () => MinimizeWhere(w => w.Key == targetKey)));
        appMenu.Items.Add(Ui.Item("Diğerlerini Gizle", () => MinimizeWhere(w => w.Key != targetKey)));
        appMenu.Items.Add(new Separator());
        appMenu.Items.Add(Ui.Item("Çık", () =>
        {
            foreach (var w in TargetAppWindows()) Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }, "⌥F4"));

        var file = TopMenu("Dosya");
        file.Items.Add(Keys("Yeni Pencere", "⌃N", CTRL, (byte)'N'));
        file.Items.Add(Keys("Yeni Sekme", "⌃T", CTRL, (byte)'T'));
        file.Items.Add(Keys("Aç…", "⌃O", CTRL, (byte)'O'));
        file.Items.Add(new Separator());
        file.Items.Add(Keys("Kaydet", "⌃S", CTRL, (byte)'S'));
        file.Items.Add(Keys("Farklı Kaydet…", "⇧⌃S", CTRL, SHIFT, (byte)'S'));
        file.Items.Add(new Separator());
        file.Items.Add(Keys("Yazdır…", "⌃P", CTRL, (byte)'P'));
        file.Items.Add(new Separator());
        file.Items.Add(Keys("Kapat", "⌃W", CTRL, (byte)'W'));

        var edit = TopMenu("Düzen");
        edit.Items.Add(Keys("Geri Al", "⌃Z", CTRL, (byte)'Z'));
        edit.Items.Add(Keys("Yinele", "⌃Y", CTRL, (byte)'Y'));
        edit.Items.Add(new Separator());
        edit.Items.Add(Keys("Kes", "⌃X", CTRL, (byte)'X'));
        edit.Items.Add(Keys("Kopyala", "⌃C", CTRL, (byte)'C'));
        edit.Items.Add(Keys("Yapıştır", "⌃V", CTRL, (byte)'V'));
        edit.Items.Add(Keys("Tümünü Seç", "⌃A", CTRL, (byte)'A'));
        edit.Items.Add(new Separator());
        edit.Items.Add(Keys("Bul…", "⌃F", CTRL, (byte)'F'));

        var view = TopMenu("Görünüm");
        view.Items.Add(Keys("Yakınlaştır", "⌃+", CTRL, 0xBB));
        view.Items.Add(Keys("Uzaklaştır", "⌃−", CTRL, 0xBD));
        view.Items.Add(Keys("Gerçek Boyut", "⌃0", CTRL, (byte)'0'));
        view.Items.Add(new Separator());
        view.Items.Add(Keys("Yenile", "F5", 0x74));
        view.Items.Add(Keys("Tam Ekran", "F11", 0x7A));

        var window = TopMenu("Pencere");
        window.Items.Add(Ui.Item("Küçült", () => WithTarget(h => Native.ShowWindow(h, Native.SW_MINIMIZE)), "⊞↓"));
        window.Items.Add(Ui.Item("Büyüt", () => WithTarget(h => Native.ShowWindow(h, Native.IsZoomed(h) ? Native.SW_RESTORE : Native.SW_MAXIMIZE)), "⊞↑"));
        window.Items.Add(new Separator());
        window.Items.Add(Keys("Ekranın Soluna Yasla", "⊞←", WIN, 0x25));
        window.Items.Add(Keys("Ekranın Sağına Yasla", "⊞→", WIN, 0x27));
        window.Items.Add(new Separator());
        window.Items.Add(Ui.Item("Tümünü Öne Getir", () =>
        {
            foreach (var w in TargetAppWindows().AsEnumerable().Reverse()) WindowTracker.Activate(w.Handle);
        }));
        window.Items.Add(new Separator());
        window.Items.Add(Ui.Item("Mission Control", () => App.MissionControl.Open()));
        window.Items.Add(Ui.Item("Launchpad", () => App.Launchpad.Open()));

        var help = TopMenu("Yardım");
        help.Items.Add(Keys("Windows Yardımı", "F1", 0x70));
        help.Items.Add(Ui.Item("MacLook Kısayolları", ShowShortcuts));
    }

    MenuItem TopMenu(object header)
    {
        var item = new MenuItem { Header = header is string text ? L.T(text) : header };
        AppMenu.Items.Add(item);
        return item;
    }

    /// <summary>Menu item that presses a keyboard shortcut in the app in front.</summary>
    MenuItem Keys(string text, string gesture, params byte[] keys) => Ui.Item(text, () => SendToTarget(keys), gesture);

    void SendToTarget(byte[] keys)
    {
        var h = target;
        // Let the menu close, hand focus back to the app, then press the shortcut there.
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (h != IntPtr.Zero) WindowTracker.Activate(h);
            Native.SendCombo(keys);
        };
        delay.Start();
    }

    void WithTarget(Action<IntPtr> action)
    {
        if (target != IntPtr.Zero) action(target);
    }

    List<AppWindow> TargetAppWindows() => WindowTracker.GetWindows().Where(w => w.Key == targetKey).ToList();

    static void MinimizeWhere(Func<AppWindow, bool> predicate)
    {
        foreach (var w in WindowTracker.GetWindows().Where(predicate))
            Native.ShowWindow(w.Handle, Native.SW_MINIMIZE);
    }

    internal static void ShowShortcuts() => MessageBox.Show(L.T(ShortcutHelp), L.T("MacLook Kısayolları"));

    internal const string ShortcutHelp =
        "Windows tuşu / sol alttaki logo  →  Başlat menüsü\n" +
        "Win+A / ağ-ses ikonu  →  Denetim Merkezi\n" +
        "Saate tıkla  →  Bildirim Merkezi ve takvim\n" +
        "Sağ alt köşe / Win+D  →  Masaüstünü göster\n" +
        "Alt+Tab  →  Uygulama değiştirici\n" +
        "     Alt basılıyken Tab / Shift+Tab / ← → ile seç,  Q: uygulamadan çık,  Esc: vazgeç\n\n" +
        "Alt bar:\n" +
        "     tıkla: aç / öne getir / küçült,  orta tık: yeni pencere,  sağ tık: menü\n" +
        "     sürükle: sırayı değiştir,  yukarı sürükleyip bırak: bardan kaldır\n" +
        "     ses ikonunun üstünde fare tekerleği: ses seviyesi\n" +
        "     ^: gizli tepsi simgeleri (barda hangileri dursun: Ayarlar → Görev çubuğu → Diğer sistem tepsisi simgeleri)\n\n" +
        "Menü çubuğu: Dosya, Düzen, Görünüm menüleri öndeki uygulamaya kısayolu gönderir\n" +
        "Arama ikonu / Alt+Boşluk  →  Spotlight\n" +
        "Sol üst köşe / Pencere menüsü (Win+Tab: Ayarlar'dan)  →  Mission Control\n" +
        "Launchpad: simgeyi sürükle → sırala,  başka bir simgenin üstüne bırak → klasör";

    static void ConfirmShutdown(string question, string args)
    {
        if (MessageBox.Show(L.T(question), "MacLook", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Ui.Open("shutdown", args);
    }

    // ---------- live status ----------

    void UpdateActiveApp()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == lastForeground || fg == IntPtr.Zero) return;
        lastForeground = fg;

        var cls = WindowTracker.GetClass(fg);
        if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
        if (cls is "Progman" or "WorkerW")
        {
            target = fg;
            targetKey = "";
            appMenu.Header = L.T("Masaüstü");
            return;
        }
        if (WindowTracker.GetExePath(fg) is not string exe) return; // one of our own windows
        target = fg;
        targetKey = WindowTracker.Normalize(exe);
        appMenu.Header = WindowTracker.GetAppName(exe);
    }

    void ControlCenter_Click(object sender, RoutedEventArgs e) => App.ControlCenter.Toggle(fromBar: false);
    void Search_Click(object sender, RoutedEventArgs e) => App.Spotlight.Toggle();
}
