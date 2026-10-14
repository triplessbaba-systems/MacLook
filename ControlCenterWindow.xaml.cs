using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace MacLook;

/// <summary>macOS-style Control Center: connectivity, display and sound sliders, now playing, shortcuts.</summary>
public partial class ControlCenterWindow : PanelWindow
{
    const double PanelW = 340;

    readonly MacSlider sound = new(Glyphs.Volume);
    readonly MacSlider display = new(Glyphs.Brightness);
    readonly DispatcherTimer mediaTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    NowPlaying? nowPlaying;
    bool opening;
    IntPtr monitor; // the screen whose brightness the slider controls

    public ControlCenterWindow()
    {
        InitializeComponent();
        InitPanel(BackdropBorder, SlideTransform);
        SoundSliderHost.Content = sound;
        DisplaySliderHost.Content = display;

        sound.ValueChanged += v =>
        {
            AudioVolume.Level = v;
            sound.Glyph = v <= 0.001 ? Glyphs.Mute : Glyphs.Volume;
        };
        display.Committed += v =>
        {
            var monitor = this.monitor;
            Task.Run(() => Brightness.Set(monitor, (int)Math.Round(v * 100)));
        };
        OutputsButton.Click += (_, _) => ShowOutputs();

        BuildSmallTile(FocusTile, Glyphs.Moon, "Odak", "Bildirimler", wide: true, () => Open("ms-settings:notifications"));
        BuildSmallTile(NightTile, Glyphs.Brightness, "Gece Işığı", null, wide: false, () => Open("ms-settings:nightlight"));
        BuildSmallTile(CastTile, Glyphs.Cast, "Yansıt", null, wide: false, () => { Dismiss(); Native.SendWinKey('K'); });
        SmallTiles.Children.Add(SmallTile(Glyphs.Accessibility, "Erişilebilirlik", () => Open("ms-settings:easeofaccess")));
        SmallTiles.Children.Add(SmallTile(Glyphs.Energy, "Güç", () => Open("ms-settings:powersleep")));
        SmallTiles.Children.Add(SmallTile(Glyphs.Settings, "Ayarlar", () => Open("ms-settings:")));

        PrevButton.Click += async (_, _) => { if (nowPlaying != null) await nowPlaying.Session.TrySkipPreviousAsync(); await RefreshMediaSoon(); };
        PlayButton.Click += async (_, _) => { if (nowPlaying != null) await nowPlaying.Session.TryTogglePlayPauseAsync(); await RefreshMediaSoon(); };
        NextButton.Click += async (_, _) => { if (nowPlaying != null) await nowPlaying.Session.TrySkipNextAsync(); await RefreshMediaSoon(); };
        mediaTimer.Tick += async (_, _) => { if (IsOpen) await RefreshMedia(); else mediaTimer.Stop(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
    }

    /// <param name="fromBar">true: opens above the bottom bar; false: below the menu bar.</param>
    public void Toggle(bool fromBar)
    {
        if (IsOpen) Dismiss();
        else _ = OpenAsync(fromBar);
    }

    async Task OpenAsync(bool fromBar)
    {
        if (opening) return;
        opening = true;
        try
        {
            App.StartMenu.Dismiss();
            App.NotificationCenter.Dismiss();
            await PrepareAsync();

            Body.Measure(new Size(316, double.PositiveInfinity));
            double panelH = Body.DesiredSize.Height + 24;
            var screen = Screens.AtCursor().Bounds;
            double top = fromBar ? screen.Bottom - DockWindow.ReservedHeight - 8 - panelH : screen.Top + MenuBarWindow.BarHeight + 6;
            ShowPanel(new Rect(screen.Right - 8 - PanelW, top, PanelW, panelH), fromBottom: fromBar);
            mediaTimer.Start();
        }
        catch (Exception ex) { App.Log("cc failed: " + ex); }
        finally { opening = false; }
    }

    /// <summary>Reads current state into the modules (also used by --snapshot).</summary>
    internal async Task PrepareAsync()
    {
        monitor = Screens.AtCursor().Handle;
        var screenMonitor = monitor;
        var brightness = Task.Run(() => Brightness.Get(screenMonitor));
        await BuildConnectivity();
        await RefreshMedia();
        var volume = AudioVolume.Level;
        sound.Value = volume ?? 0;
        sound.Glyph = (volume ?? 0) <= 0.001 ? Glyphs.Mute : Glyphs.Volume;
        OutputName.Text = AudioVolume.Outputs().FirstOrDefault(o => o.IsDefault).Name ?? "";
        var level = await brightness;
        DisplayModule.Visibility = level == null ? Visibility.Collapsed : Visibility.Visible;
        display.Value = (level ?? 0) / 100.0;
    }

    void Open(string uri)
    {
        Dismiss();
        Ui.Open(uri);
    }

    /// <summary>Speakers, headphones…: pick where sound goes.</summary>
    void ShowOutputs()
    {
        var menu = new ContextMenu { PlacementTarget = OutputsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (id, name, isDefault) in AudioVolume.Outputs())
            menu.Items.Add(Ui.Item((isDefault ? "✓  " : "     ") + name, () =>
            {
                AudioVolume.SetDefaultOutput(id);
                OutputName.Text = name;
                sound.Value = AudioVolume.Level ?? 0;
            }));
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L.T("Çıkış aygıtı bulunamadı"), IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Ses Ayarları…", () => Open("ms-settings:sound")));
        OpenMenu(menu);
    }

    // ---------- connectivity ----------

    async Task BuildConnectivity()
    {
        Connectivity.Children.Clear();
        var wifi = await Radios.FindAsync(RadioKind.WiFi);
        var bluetooth = await Radios.FindAsync(RadioKind.Bluetooth);
        if (wifi != null) Connectivity.Children.Add(RadioRow(wifi, Glyphs.Wifi, "Wi-Fi", "ms-settings:network-wifi"));
        if (bluetooth != null) Connectivity.Children.Add(RadioRow(bluetooth, Glyphs.Bluetooth, "Bluetooth", "ms-settings:bluetooth"));

        bool online = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211);
        Connectivity.Children.Add(Row(Glyphs.Ethernet, "Ağ", online ? "Bağlı" : "Bağlı değil", online,
            () => Open("ms-settings:network"), () => Open("ms-settings:network")));
        // Windows' answer to AirDrop.
        Connectivity.Children.Add(Row(Glyphs.Share, "Yakın Paylaşım", "Ayarlar", false,
            () => Open("ms-settings:crossdevice"), () => Open("ms-settings:crossdevice")));
    }

    FrameworkElement RadioRow(Radio radio, string glyph, string title, string settings)
    {
        bool on = radio.State == RadioState.On;
        return Row(glyph, title, on ? "Açık" : "Kapalı", on, async () =>
        {
            await radio.SetStateAsync(radio.State == RadioState.On ? RadioState.Off : RadioState.On);
            await BuildConnectivity();
        }, () => Open(settings));
    }

    /// <summary>Round toggle (blue when on) followed by a title and status; the text opens settings.</summary>
    static FrameworkElement Row(string glyph, string title, string status, bool on, Action toggle, Action openSettings)
    {
        var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Cursor = Cursors.Hand };
        circle.SetResourceReference(Border.BackgroundProperty, on ? "Accent" : "TileOff");
        var icon = new TextBlock { Text = glyph, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        if (on) icon.Foreground = Brushes.White;
        circle.Child = icon;
        circle.MouseLeftButtonUp += (_, e) => { e.Handled = true; toggle(); };

        var texts = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        texts.Children.Add(new TextBlock { Text = L.T(title), FontSize = 12.5, FontWeight = FontWeights.SemiBold });
        var sub = new TextBlock { Text = L.T(status), FontSize = 11.5 };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        texts.Children.Add(sub);
        texts.MouseLeftButtonUp += (_, e) => { e.Handled = true; openSettings(); };

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(circle);
        row.Children.Add(texts);
        return row;
    }

    // ---------- tiles ----------

    static void BuildSmallTile(Border tile, string glyph, string title, string? subtitle, bool wide, Action click)
    {
        var icon = new TextBlock { Text = glyph, FontSize = wide ? 14 : 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        var label = new TextBlock
        {
            Text = L.T(title),
            FontSize = wide ? 12.5 : 11,
            FontWeight = wide ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = wide ? TextAlignment.Left : TextAlignment.Center,
        };

        if (wide)
        {
            var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = icon };
            circle.SetResourceReference(Border.BackgroundProperty, "TileOff");
            var texts = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(label);
            if (subtitle != null)
            {
                var sub = new TextBlock { Text = L.T(subtitle), FontSize = 11.5 };
                sub.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
                texts.Children.Add(sub);
            }
            var row = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(circle);
            row.Children.Add(texts);
            tile.Child = row;
        }
        else
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(icon);
            label.Margin = new Thickness(0, 5, 0, 0);
            stack.Children.Add(label);
            tile.Child = stack;
        }
        tile.Cursor = Cursors.Hand;
        tile.MouseEnter += (_, _) => tile.Opacity = 0.8;
        tile.MouseLeave += (_, _) => tile.Opacity = 1;
        tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
    }

    static Border SmallTile(string glyph, string title, Action click)
    {
        var tile = new Border { Style = (Style)Application.Current.FindResource("Tile"), Margin = new Thickness(5, 0, 5, 0), Height = 64, Padding = new Thickness(4) };
        BuildSmallTile(tile, glyph, title, null, wide: false, click);
        return tile;
    }

    // ---------- now playing ----------

    async Task RefreshMedia()
    {
        nowPlaying = await Media.GetAsync();
        if (nowPlaying == null || nowPlaying.Title.Length == 0)
        {
            MediaModule.Visibility = Visibility.Collapsed;
            return;
        }
        MediaModule.Visibility = Visibility.Visible;
        MediaTitle.Text = nowPlaying.Title;
        MediaArtist.Text = nowPlaying.Artist;
        PlayGlyph.Text = nowPlaying.Playing ? Glyphs.Pause : Glyphs.Play;
        if (nowPlaying.Cover != null) Cover.Background = new ImageBrush(nowPlaying.Cover) { Stretch = Stretch.UniformToFill };
        else Cover.SetResourceReference(Border.BackgroundProperty, "TileOff");
    }

    async Task RefreshMediaSoon()
    {
        await Task.Delay(300);
        await RefreshMedia();
    }
}
