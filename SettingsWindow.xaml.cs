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
        AddToggle(KeyboardRows, "Windows tuşu MacLook Başlat menüsünü açsın", "Kapalıyken Windows'un kendi Başlat menüsü açılır. Win+A da Denetim Merkezi'ni açar.",
            () => config.WinKeyOpensStart, v => { config.WinKeyOpensStart = v; config.Save(); });
        AddToggle(KeyboardRows, "Alt+Tab macOS uygulama değiştiricisini açsın", "Kapalıyken Windows'un kendi Alt+Tab ekranı açılır.",
            () => config.MacAppSwitcher, v => { config.MacAppSwitcher = v; config.Save(); }, last: true);

        AddToggle(GeneralRows, "Bilgisayar açılınca MacLook'u başlat", null,
            () => Autostart.Enabled, v => Autostart.Enabled = v, last: true);

        AddLink("Bardaki tepsi simgelerini seç", "ms-settings:taskbar");
        AddLink("Bildirim ayarları", "ms-settings:notifications");
        AddLink("Duvar kâğıdı ve renkler", "ms-settings:personalization");
        AddLink("Açık / koyu mod", "ms-settings:colors");

        Footer.Text = "MacLook kapatılınca Windows görev çubuğu geri gelir. Kısayolların listesi: elma menüsü → Yardım → MacLook Kısayolları.";
    }

    public void Open()
    {
        if (IsOpen) return;
        App.StartMenu.Dismiss();
        App.ControlCenter.Dismiss();
        App.NotificationCenter.Dismiss();
        Body.Measure(new Size(PanelW - 40, double.PositiveInfinity));
        double panelH = Body.DesiredSize.Height + 34;
        double screenW = SystemParameters.PrimaryScreenWidth, screenH = SystemParameters.PrimaryScreenHeight;
        ShowPanel(new Rect(Math.Round((screenW - PanelW) / 2), Math.Round((screenH - panelH) / 2.4), PanelW, panelH), fromBottom: true);
    }

    static void AddToggle(Panel host, string title, string? description, Func<bool> get, Action<bool> set, bool last = false)
    {
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        texts.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description != null)
        {
            var sub = new TextBlock { Text = description, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
            texts.Children.Add(sub);
        }
        var toggle = new MacSwitch(get());
        toggle.Toggled += set;
        DockPanel.SetDock(toggle, Dock.Right);
        var row = new DockPanel { Margin = new Thickness(0, 9, 0, 9) };
        row.Children.Add(toggle);
        row.Children.Add(texts);
        host.Children.Add(row);
        if (!last)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "PanelBorder");
            host.Children.Add(line);
        }
    }

    void AddLink(string title, string uri)
    {
        var arrow = new TextBlock { Text = "", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        arrow.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        arrow.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        var row = new DockPanel();
        DockPanel.SetDock(arrow, Dock.Right);
        row.Children.Add(arrow);
        row.Children.Add(new TextBlock { Text = title });
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
}
