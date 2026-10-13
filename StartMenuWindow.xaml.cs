using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MacLook;

/// <summary>
/// Start menu in macOS style, opened by the Windows key or the logo at the left of the bar:
/// search, pinned apps, recent files, all apps, account and power.
/// </summary>
public partial class StartMenuWindow : PanelWindow
{
    const double PanelW = 640, PanelH = 740;
    static readonly CultureInfo Turkish = new("tr-TR");
    static readonly CompareInfo Compare = Turkish.CompareInfo;
    // First run: the same apps the Windows Start menu had pinned, plus the usual ones.
    static readonly string[] DefaultPins =
    [
        "Microsoft Edge", "Google Chrome", "Discord", "Steam", "Spotify", "Dosya Gezgini",
        "Outlook", "Microsoft Store", "Ayarlar", "Fotoğraflar", "Solitaire & Casual Games", "Paint",
        "LinkedIn", "Hesap Makinesi", "Saat", "Not Defteri", "Ekran Alıntısı Aracı", "Xbox",
    ];

    List<AppInfo> appResults = new();
    List<RecentFile> recent = new(), fileResults = new();
    bool userLoaded;

    // Rows in the list page that the arrow keys move through.
    readonly List<(Border Row, Action Run)> nav = new();
    int navIndex = -1;
    bool collectNav;

    public StartMenuWindow()
    {
        InitializeComponent();
        InitPanel(BackdropBorder, SlideTransform);
        Search.TextChanged += (_, _) => OnSearch();
        PreviewKeyDown += OnKey;
        AllAppsButton.Click += (_, _) => ShowAllApps();
        BackButton.Click += (_, _) => ShowHome();
        LaunchpadButton.Click += (_, _) => { Dismiss(); App.Launchpad.Open(); };
        UserButton.Click += (_, _) => { Dismiss(); Ui.Open("ms-settings:accounts"); };
        PowerButton.Click += (_, _) => ShowPowerMenu();
        AppCatalog.Updated += () => { if (IsOpen) BuildPinned(); };
    }

    public void Toggle()
    {
        if (IsOpen) Dismiss();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        App.ControlCenter.Dismiss();
        App.NotificationCenter.Dismiss();
        Prepare();

        double screenH = SystemParameters.PrimaryScreenHeight;
        ShowPanel(new Rect(8, screenH - DockWindow.BarHeight - 8 - PanelH, PanelW, PanelH), fromBottom: true);
        Search.Focus();
        Keyboard.Focus(Search);
    }

    /// <summary>Fills in account, pinned apps and recent files (also used by --snapshot).</summary>
    internal void Prepare()
    {
        if (!userLoaded)
        {
            UserName.Text = UserInfo.DisplayName;
            if (UserInfo.Picture is { } picture) Avatar.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            userLoaded = true;
        }
        Search.Text = "";
        ShowHome();
        BuildPinned();
        BuildRecent();
    }

    void Launch(AppInfo app)
    {
        Dismiss();
        Ui.Open(app.LaunchPath);
    }

    void OpenFile(RecentFile file)
    {
        Dismiss();
        Ui.Open(file.Path);
    }

    // ---------- home ----------

    void ShowHome()
    {
        HomePage.Visibility = Visibility.Visible;
        ListPage.Visibility = Visibility.Collapsed;
    }

    void BuildPinned()
    {
        PinnedGrid.Children.Clear();
        foreach (var app in PinnedApps().Take(18)) PinnedGrid.Children.Add(MakeTile(app));
    }

    static List<AppInfo> PinnedApps()
    {
        var apps = AppCatalog.Apps;
        if (apps.Count == 0) return [];
        var config = App.Config;
        if (config.StartPins == null)
        {
            config.StartPins = DefaultPins.Select(name => FindByName(apps, name)).OfType<AppInfo>().Select(a => a.Id).Distinct().ToList();
            config.Save();
        }
        return config.StartPins.Select(id => apps.FirstOrDefault(a => a.Id == id)).OfType<AppInfo>().ToList();
    }

    static AppInfo? FindByName(IReadOnlyList<AppInfo> apps, string name) =>
        apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.CurrentCultureIgnoreCase))
        ?? apps.FirstOrDefault(a => a.Name.StartsWith(name, StringComparison.CurrentCultureIgnoreCase));

    void BuildRecent()
    {
        recent = UserInfo.RecentFiles(30);
        RecentGrid.Children.Clear();
        foreach (var file in recent.Take(6))
            RecentGrid.Children.Add(MakeRow(IconLoader.Get(file.Path, 64), file.Name, UserInfo.Ago(file.When), 32, () => OpenFile(file), null));
        NoRecent.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- all apps / search ----------

    void ShowAllApps()
    {
        ListTitle.Text = "Tüm Uygulamalar";
        BackButton.Visibility = Visibility.Visible;
        ListItems.Children.Clear();
        nav.Clear();
        navIndex = -1;
        collectNav = true;
        string? letter = null;
        foreach (var app in AppCatalog.Apps)
        {
            var first = FirstLetter(app.Name);
            if (first != letter)
            {
                letter = first;
                ListItems.Children.Add(SectionHeader(letter));
            }
            ListItems.Children.Add(MakeRow(app.Icon, app.Name, null, 26, () => Launch(app), () => AppMenu(app)));
        }
        collectNav = false;
        HomePage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        ListScroll.ScrollToTop();
    }

    void OnSearch()
    {
        var query = Search.Text.Trim();
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length == 0)
        {
            ShowHome();
            return;
        }

        appResults = AppCatalog.Apps
            .Where(a => Compare.IndexOf(a.Name, query, CompareOptions.IgnoreCase) >= 0)
            .OrderBy(a => Compare.IsPrefix(a.Name, query, CompareOptions.IgnoreCase) ? 0 : 1)
            .ThenBy(a => a.Name.Length)
            .Take(8)
            .ToList();
        fileResults = recent.Where(f => Compare.IndexOf(f.Name, query, CompareOptions.IgnoreCase) >= 0).Take(5).ToList();

        ListTitle.Text = appResults.Count + fileResults.Count > 0 ? "En iyi eşleşmeler" : "Sonuç bulunamadı";
        BackButton.Visibility = Visibility.Collapsed;
        ListItems.Children.Clear();
        nav.Clear();
        navIndex = -1;
        collectNav = true;
        if (appResults.Count > 0)
        {
            ListItems.Children.Add(SectionHeader("Uygulamalar"));
            foreach (var app in appResults)
                ListItems.Children.Add(MakeRow(app.Icon, app.Name, "Uygulama", 30, () => Launch(app), () => AppMenu(app)));
        }
        if (fileResults.Count > 0)
        {
            ListItems.Children.Add(SectionHeader("Belgeler"));
            foreach (var file in fileResults)
                ListItems.Children.Add(MakeRow(IconLoader.Get(file.Path, 64), file.Name, UserInfo.Ago(file.When), 30, () => OpenFile(file), null));
        }
        collectNav = false;
        Select(0); // Enter opens the top result; arrows move from there
        HomePage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        ListScroll.ScrollToTop();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = "";
                else if (ListPage.IsVisible) ShowHome();
                else Dismiss();
                e.Handled = true;
                break;
            case Key.Enter when ListPage.IsVisible && navIndex >= 0:
                nav[navIndex].Run();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when ListPage.IsVisible && nav.Count > 0:
                Select(Math.Clamp(navIndex + (e.Key == Key.Down ? 1 : -1), 0, nav.Count - 1));
                e.Handled = true;
                break;
        }
    }

    void Select(int index)
    {
        if (navIndex >= 0 && navIndex < nav.Count) nav[navIndex].Row.Background = Brushes.Transparent;
        navIndex = index < nav.Count ? index : -1;
        if (navIndex < 0) return;
        var row = nav[navIndex].Row;
        row.SetResourceReference(Border.BackgroundProperty, "PanelHover");
        row.BringIntoView();
    }

    bool IsSelected(Border row) => navIndex >= 0 && navIndex < nav.Count && nav[navIndex].Row == row;

    static string FirstLetter(string name)
    {
        var c = name.TrimStart().FirstOrDefault();
        return char.IsLetter(c) ? char.ToUpper(c, Turkish).ToString() : "#";
    }

    // ---------- menus ----------

    ContextMenu AppMenu(AppInfo app)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Ui.Item("Aç", () => Launch(app)));
        menu.Items.Add(new Separator());
        bool pinned = App.Config.StartPins?.Contains(app.Id) == true;
        menu.Items.Add(Ui.Item(pinned ? "Başlangıçtan Kaldır" : "Başlangıca Sabitle", () =>
        {
            var pins = App.Config.StartPins ??= [];
            if (!pins.Remove(app.Id)) pins.Add(app.Id);
            App.Config.Save();
            BuildPinned();
        }));
        if (BarKey(app) is string key)
        {
            bool onBar = App.Config.Pinned.Any(p => WindowTracker.Normalize(p.MatchExe) == key);
            menu.Items.Add(Ui.Item(onBar ? "Bardan Kaldır" : "Bara Sabitle", () => App.Dock.TogglePin(app.Name, app.LaunchPath, key)));
        }
        return menu;
    }

    /// <summary>Exe name the bar uses to spot the app's windows, when it can be worked out.</summary>
    static string? BarKey(AppInfo app)
    {
        if (app.Id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return WindowTracker.Normalize(Path.GetFileName(app.Id));
        if (Shortcuts.FindByName(app.Name) is string lnk && Shortcuts.GetTargetExe(lnk) is { Length: > 0 } exe) return exe;
        return null;
    }

    void ShowPowerMenu()
    {
        var menu = new ContextMenu { PlacementTarget = PowerButton, Placement = PlacementMode.Top };
        menu.Items.Add(Ui.Item("Kilitle", () => { Dismiss(); Native.LockWorkStation(); }));
        menu.Items.Add(Ui.Item("Oturumu Kapat…", () => Confirm("Oturum kapatılsın mı?", "/l")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Uyku", () => { Dismiss(); Native.SetSuspendState(false, false, false); }));
        menu.Items.Add(Ui.Item("Yeniden Başlat…", () => Confirm("Bilgisayar yeniden başlatılsın mı?", "/r /t 0")));
        menu.Items.Add(Ui.Item("Kapat…", () => Confirm("Bilgisayar kapatılsın mı?", "/s /t 0")));
        OpenMenu(menu);
    }

    void Confirm(string question, string args)
    {
        Dismiss();
        if (MessageBox.Show(question, "MacLook", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Ui.Open("shutdown", args);
    }

    // ---------- building blocks ----------

    FrameworkElement MakeTile(AppInfo app)
    {
        var image = new Image { Source = app.Icon, Width = 42, Height = 42 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var label = new TextBlock
        {
            Text = app.Name,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 88,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(image);
        stack.Children.Add(label);
        var tile = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(3), Background = Brushes.Transparent, Child = stack };
        Wire(tile, () => Launch(app), () => AppMenu(app));
        return tile;
    }

    FrameworkElement MakeRow(ImageSource? icon, string title, string? subtitle, double iconSize, Action click, Func<ContextMenu>? menu)
    {
        var image = new Image { Source = icon, Width = iconSize, Height = iconSize, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        if (subtitle != null)
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(0, 1, 0, 0) };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
            texts.Children.Add(sub);
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(texts, 1);
        grid.Children.Add(image);
        grid.Children.Add(texts);
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(3, 1, 3, 1), Background = Brushes.Transparent, Child = grid };
        Wire(row, click, menu);
        if (collectNav) nav.Add((row, click));
        return row;
    }

    static TextBlock SectionHeader(string text)
    {
        var header = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(13, 12, 0, 4) };
        header.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        return header;
    }

    void Wire(Border item, Action click, Func<ContextMenu>? menu)
    {
        item.Cursor = Cursors.Hand;
        item.MouseEnter += (_, _) => item.SetResourceReference(Border.BackgroundProperty, "PanelHover");
        item.MouseLeave += (_, _) => { if (!IsSelected(item)) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        if (menu != null)
            item.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                var m = menu();
                m.PlacementTarget = item;
                m.Placement = PlacementMode.MousePoint;
                OpenMenu(m);
            };
    }
}
