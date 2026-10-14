using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MacLook;

/// <summary>MacLook's own settings, macOS System Settings style.</summary>
public partial class SettingsWindow : PanelWindow
{
    const double PanelW = 460;

    public SettingsWindow()
    {
        InitializeComponent();
        InitPanel(BackdropBorder, SlideTransform);
        Logo.Data = Geometry.Parse(Glyphs.AppleLogo);
        DoneButton.Click += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };

        var config = App.Config;

        // Bar settings rebuild the bars at once so the change is visible straight away.
        AddRow(BarRows, "Stil", "Dock: ortada yüzen, büyüyen simgeler; saat ve tepsi menü çubuğuna geçer.",
            new MacSegmented([L.T("Tam genişlik"), "Dock"], config.IsDock ? 1 : 0, i => { config.BarStyle = i == 1 ? "dock" : "bar"; Apply(); }));
        AddToggle(BarRows, "Otomatik gizle", "Fare ekranın altına gelince görünür.",
            () => config.AutoHideBar, v => { config.AutoHideBar = v; Apply(); });
        var size = new MacSlider(Glyphs.Apps) { Width = 150 };
        size.Value = config.IsDock ? (config.DockIconSize - 32) / 48 : (config.BarIconSize - 22) / 18;
        size.Committed += v =>
        {
            if (config.IsDock) config.DockIconSize = Math.Round(32 + v * 48);
            else config.BarIconSize = Math.Round(22 + v * 18);
            Apply();
        };
        AddRow(BarRows, "Simge boyutu", null, size);
        AddToggle(BarRows, "Dock'ta büyütme", "Fare üstüne gelince simgeler büyür (Dock stili).",
            () => config.DockMagnification, v => { config.DockMagnification = v; Apply(); }, last: true);

        AddToggle(KeyboardRows, "Windows tuşu MacLook Başlat menüsünü açsın", "Kapalıyken Windows'un kendi Başlat menüsü açılır. Win+A da Denetim Merkezi'ni açar.",
            () => config.WinKeyOpensStart, v => { config.WinKeyOpensStart = v; config.Save(); });
        AddToggle(KeyboardRows, "Alt+Tab macOS uygulama değiştiricisini açsın", "Kapalıyken Windows'un kendi Alt+Tab ekranı açılır.",
            () => config.MacAppSwitcher, v => { config.MacAppSwitcher = v; config.Save(); });
        AddToggle(KeyboardRows, "Alt+Boşluk Spotlight'ı açsın", "Uygulama, dosya, ayar, hesap makinesi ve web araması.",
            () => config.SpotlightHotkey, v => { config.SpotlightHotkey = v; config.Save(); });
        AddToggle(KeyboardRows, "Win+Tab Mission Control'ü açsın", "Kapalıyken Windows'un Görev Görünümü açılır.",
            () => config.WinTabMissionControl, v => { config.WinTabMissionControl = v; config.Save(); }, last: true);

        AddCorner("Sol üst", () => config.HotCornerTopLeft, v => config.HotCornerTopLeft = v);
        AddCorner("Sağ üst", () => config.HotCornerTopRight, v => config.HotCornerTopRight = v);
        AddCorner("Sol alt", () => config.HotCornerBottomLeft, v => config.HotCornerBottomLeft = v);
        AddCorner("Sağ alt", () => config.HotCornerBottomRight, v => config.HotCornerBottomRight = v, last: true);

        // Every text is read once when a window is built, so a new language needs a fresh start.
        AddRow(GeneralRows, "Dil", "MacLook yeniden başlar.",
            new MacSegmented(["Türkçe", "English"], L.IsEnglish ? 1 : 0, i =>
            {
                config.Language = i == 1 ? "en" : "tr";
                config.Save();
                App.Restart(openSettings: true);
            }));
        AddToggle(GeneralRows, "Bilgisayar açılınca MacLook'u başlat", null,
            () => Autostart.Enabled, v => Autostart.Enabled = v);
        AddToggle(GeneralRows, "Güncellemeleri otomatik denetle", "Günde bir kez GitHub'daki son sürüme bakar.",
            () => config.CheckForUpdates, v => { config.CheckForUpdates = v; config.Save(); });

        var check = new Button { Style = (Style)FindResource("PillButton"), Padding = new Thickness(10, 4, 10, 4), Content = new TextBlock { Text = L.T("Şimdi denetle") } };
        var versionText = new TextBlock { Text = L.T("Sürüm") + " " + Updater.CurrentText };
        check.Click += async (_, _) =>
        {
            versionText.Text = L.T("Denetleniyor…");
            if (await Updater.CheckAsync(force: true) && Updater.Available is { } update)
            {
                versionText.Text = L.T("Yeni sürüm:") + " " + update.Version;
                Ui.Open(update.Url);
            }
            else versionText.Text = L.T("Sürüm") + " " + Updater.CurrentText + " · " + L.T("güncel");
        };
        AddRow(GeneralRows, "MacLook", null, check, last: !Installer.CanInstall || Installer.RunningInstalled).Children.Add(Subtle(versionText));

        if (Installer.CanInstall && !Installer.RunningInstalled)
        {
            var install = new Button { Style = (Style)FindResource("PillButton"), Padding = new Thickness(10, 4, 10, 4), Content = new TextBlock { Text = L.T("Kur"), FontWeight = FontWeights.SemiBold } };
            install.Click += (_, _) => Installer.Install();
            AddRow(GeneralRows, "Bu bilgisayara kur", "Başlat menüsüne eklenir, Ayarlar → Uygulamalar'dan kaldırılabilir. Yönetici izni gerekmez.", install, last: true);
        }

        AddLink("Bardaki tepsi simgelerini seç", "ms-settings:taskbar");
        AddLink("Bildirim ayarları", "ms-settings:notifications");
        AddLink("Duvar kâğıdı ve renkler", "ms-settings:personalization");
        AddLink("Açık / koyu mod", "ms-settings:colors");

        Footer.Text = L.T("MacLook kapatılınca Windows görev çubuğu geri gelir. Kısayolların listesi: elma menüsü → Yardım → MacLook Kısayolları.");
    }

    public void Open()
    {
        if (IsOpen) return;
        App.StartMenu.Dismiss();
        App.ControlCenter.Dismiss();
        App.NotificationCenter.Dismiss();
        var screen = Screens.AtCursor().Bounds;
        Body.Measure(new Size(PanelW - 28, double.PositiveInfinity));
        double panelH = Math.Min(Body.DesiredSize.Height + 34, Math.Round(screen.Height * 0.85));
        Scroll.ScrollToTop();
        ShowPanel(new Rect(screen.Left + Math.Round((screen.Width - PanelW) / 2), screen.Top + Math.Round((screen.Height - panelH) / 2.4), PanelW, panelH), fromBottom: true);
    }

    /// <summary>Corner row: a button showing the chosen action; clicking it lists the choices.</summary>
    void AddCorner(string corner, Func<string> get, Action<string> set, bool last = false)
    {
        var label = new TextBlock { Text = HotCorners.NameOf(get()) };
        var arrow = new TextBlock { Text = "", FontSize = 10, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(label);
        content.Children.Add(arrow);
        var button = new Button { Style = (Style)FindResource("PillButton"), Content = content, Padding = new Thickness(10, 4, 10, 4) };
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var (id, name) in HotCorners.Actions)
                menu.Items.Add(Ui.Item((id == get() ? "✓  " : "     ") + L.T(name), () =>
                {
                    set(id);
                    label.Text = L.T(name);
                    App.Config.Save();
                }));
            OpenMenu(menu);
        };
        AddRow(CornerRows, corner, null, button, last);
    }

    static TextBlock Subtle(TextBlock text)
    {
        text.FontSize = 11.5;
        text.Margin = new Thickness(0, 2, 0, 0);
        text.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        return text;
    }

    static void Apply()
    {
        App.Config.Save();
        App.RebuildBars();
    }

    static void AddToggle(Panel host, string title, string? description, Func<bool> get, Action<bool> set, bool last = false)
    {
        var toggle = new MacSwitch(get());
        toggle.Toggled += set;
        AddRow(host, title, description, toggle, last);
    }

    /// <summary>A settings row: title (and description) on the left, a control on the right. Returns the text column.</summary>
    static StackPanel AddRow(Panel host, string title, string? description, FrameworkElement control, bool last = false)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        texts.Children.Add(new TextBlock { Text = L.T(title), TextWrapping = TextWrapping.Wrap });
        if (description != null)
        {
            var sub = new TextBlock { Text = L.T(description), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
            texts.Children.Add(sub);
        }
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 9, 0, 9) };
        row.Children.Add(control);
        row.Children.Add(texts);
        host.Children.Add(row);
        if (!last)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "PanelBorder");
            host.Children.Add(line);
        }
        return texts;
    }

    void AddLink(string title, string uri)
    {
        var arrow = new TextBlock { Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        var row = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        row.Children.Add(arrow);
        row.Children.Add(new TextBlock { Text = L.T(title) });
        var button = new Button { Style = (Style)FindResource("PillButton"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7) };
        button.Click += (_, _) => { Dismiss(); Ui.Open(uri); };
        LinkRows.Children.Add(button);
    }
}

/// <summary>macOS-style on/off switch: green pill with a sliding white knob.</summary>
internal sealed class MacSwitch : Border
{
    readonly TranslateTransform knobShift = new();
    bool on;

    public event Action<bool>? Toggled;

    public MacSwitch(bool initial)
    {
        Width = 40;
        Height = 24;
        CornerRadius = new CornerRadius(12);
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        var knob = new Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = knobShift,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.25 },
        };
        Child = knob;
        on = initial;
        Render(animate: false);
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            Render(animate: true);
            Toggled?.Invoke(on);
        };
    }

    void Render(bool animate)
    {
        if (on) Background = Theme.Brush("#FF34C759");
        else SetResourceReference(BackgroundProperty, "TileOff");
        double x = on ? 16 : 0;
        if (animate) knobShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase() });
        else knobShift.X = x;
    }
}

/// <summary>macOS-style segmented control: a row of options in a rounded track, the chosen one raised.</summary>
internal sealed class MacSegmented : Border
{
    readonly List<Border> segments = new();

    public MacSegmented(string[] options, int selected, Action<int> changed)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2);
        SetResourceReference(BackgroundProperty, "TileOff");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var segment = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 3, 12, 4),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock { Text = options[i], FontSize = 12.5 },
            };
            segment.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (Select(index)) changed(index);
            };
            segments.Add(segment);
            row.Children.Add(segment);
        }
        Child = row;
        Select(selected);
    }

    int current = -1;

    bool Select(int index)
    {
        if (index == current) return false;
        current = index;
        for (int i = 0; i < segments.Count; i++)
        {
            if (i == index)
            {
                segments[i].SetResourceReference(BackgroundProperty, "SegmentOn");
                segments[i].Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.2 };
            }
            else
            {
                segments[i].Background = Brushes.Transparent;
                segments[i].Effect = null;
            }
        }
        return true;
    }
}

/// <summary>"Start with Windows" via the user's Run key.</summary>
internal static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("MacLook") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("MacLook", $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue("MacLook", false);
        }
    }

    /// <summary>Keeps "start with Windows" on, but pointing at another copy (after installing).</summary>
    public static void Point(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue("MacLook", $"\"{exe}\"");
    }
}
