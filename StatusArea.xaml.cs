using ManagedShell.WindowsTray;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Hidden icons (^), tray icons, battery, keyboard language, network / volume and the clock.
/// Sits at the right of the bottom bar, or in the menu bar (compact) when the Dock style is used.
/// </summary>
public partial class StatusArea : UserControl
{

    readonly bool compact, showTray;
    readonly Dictionary<NotifyIcon, FrameworkElement> trayViews = new();
    readonly DispatcherTimer fastTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer slowTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer tipTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    readonly ToolTip volumeTip = new();

    /// <param name="compact">Menu bar version: shorter buttons and a one-line clock.</param>
    /// <param name="showTray">Only one status area shows the tray icons (the primary screen's).</param>
    internal StatusArea(bool compact, bool showTray)
    {
        this.compact = compact;
        this.showTray = showTray;
        InitializeComponent();

        var style = (Style)FindResource(compact ? "BarButton" : "BarItem");
        foreach (var button in new[] { OverflowButton, BatteryButton, LanguageButton, ControlCenterButton, ClockButton })
            button.Style = style;
        ClockButton.Padding = new Thickness(compact ? 8 : 10, 0, compact ? 8 : 10, 0);
        Time.FontSize = compact ? 13 : 12.5;
        Time.FontWeight = compact ? FontWeights.Normal : FontWeights.SemiBold;
        Date.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (compact)
        {
            Chevron.Data = Geometry.Parse("M 0.5,0.8 L 5,5 L 9.5,0.8"); // the list opens downwards
            Overflow.Placement = PlacementMode.Bottom;
        }
        volumeTip.PlacementTarget = ControlCenterButton;
        volumeTip.Placement = compact ? PlacementMode.Bottom : PlacementMode.Top;

        Overflow.Opened += (_, _) => Dispatcher.InvokeAsync(UpdateTrayPlacements, DispatcherPriority.Loaded);
        fastTimer.Tick += (_, _) => { UpdateClock(); UpdateLanguage(); };
        slowTimer.Tick += (_, _) => { UpdateStatus(); RefreshTray(); };
        tipTimer.Tick += (_, _) => { tipTimer.Stop(); volumeTip.IsOpen = false; };
        UpdateClock();
        UpdateStatus();
        UpdateLanguage();
        RefreshTray();

        Loaded += (_, _) =>
        {
            if (showTray && TrayHost.Area is { } area) area.TrayIcons.CollectionChanged += OnTrayChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            RefreshTray();
            UpdateClock();
            UpdateLanguage();
            UpdateStatus();
            fastTimer.Start();
            slowTimer.Start();
        };
        // The window closes when screens or settings change: let go of everything.
        Unloaded += (_, _) =>
        {
            fastTimer.Stop();
            slowTimer.Stop();
            if (TrayHost.Area is { } area) area.TrayIcons.CollectionChanged -= OnTrayChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            foreach (var icon in trayViews.Keys) icon.PropertyChanged -= OnTrayIconChanged;
            trayViews.Clear();
        };
    }

    /// <summary>True while the hidden-icons list is open (an auto-hiding bar stays up meanwhile).</summary>
    internal bool IsBusy => Overflow.IsOpen;

    void OnTrayChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshTray();
    void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(UpdateStatus);

    // ---------- tray ----------

    /// <summary>Icons Windows is set to show sit on the bar; the rest go under the chevron.</summary>
    void RefreshTray()
    {
        var icons = showTray && TrayHost.Area is { } area
            ? area.TrayIcons.Where(i => !i.IsHidden && i.Icon != null).ToList()
            : new List<NotifyIcon>();
        foreach (var gone in trayViews.Keys.Except(icons).ToList())
        {
            gone.PropertyChanged -= OnTrayIconChanged;
            trayViews.Remove(gone);
        }

        Tray.Children.Clear();
        OverflowIcons.Children.Clear();
        foreach (var icon in icons)
        {
            if (!trayViews.TryGetValue(icon, out var view))
            {
                trayViews[icon] = view = MakeTrayIcon(icon);
                icon.PropertyChanged += OnTrayIconChanged;
            }
            (TrayHost.IsPromoted(icon) ? (Panel)Tray : OverflowIcons).Children.Add(view);
        }
        OverflowButton.Visibility = OverflowIcons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (OverflowIcons.Children.Count == 0) Overflow.IsOpen = false;
        Dispatcher.InvokeAsync(UpdateTrayPlacements, DispatcherPriority.Loaded);
    }

    void OnTrayIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotifyIcon icon) return;
        if (e.PropertyName == nameof(NotifyIcon.IsHidden)) { RefreshTray(); return; }
        if (!trayViews.TryGetValue(icon, out var view)) return;
        if (e.PropertyName == nameof(NotifyIcon.Icon) && view is Border { Child: Image image }) image.Source = icon.Icon;
        if (e.PropertyName == nameof(NotifyIcon.Title)) view.ToolTip = icon.Title;
    }

    FrameworkElement MakeTrayIcon(NotifyIcon icon)
    {
        double size = compact ? 16 : 18;
        var image = new Image { Source = icon.Icon, Width = size, Height = size };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var host = new Border
        {
            Width = compact ? 26 : 30,
            Height = compact ? 22 : 36,
            CornerRadius = new CornerRadius(compact ? 5 : 7),
            Background = Brushes.Transparent,
            Child = image,
            ToolTip = string.IsNullOrEmpty(icon.Title) ? null : icon.Title,
        };
        host.MouseEnter += (_, _) =>
        {
            host.SetResourceReference(Border.BackgroundProperty, OverflowIcons.Children.Contains(host) ? "PanelHover" : "BarHover");
            icon.IconMouseEnter(CursorParam());
        };
        host.MouseLeave += (_, _) =>
        {
            host.Background = Brushes.Transparent;
            icon.IconMouseLeave(CursorParam());
        };
        host.MouseMove += (_, _) => icon.IconMouseMove(CursorParam());
        host.MouseDown += (_, e) =>
        {
            e.Handled = true;
            icon.IconMouseDown(e.ChangedButton, CursorParam(), (int)Native.GetDoubleClickTime());
        };
        host.MouseUp += (_, e) =>
        {
            e.Handled = true;
            bool inOverflow = OverflowIcons.Children.Contains(host);
            icon.IconMouseUp(e.ChangedButton, CursorParam(), (int)Native.GetDoubleClickTime());
            if (inOverflow && e.ChangedButton == MouseButton.Left) Overflow.IsOpen = false;
        };
        return host;
    }

    /// <summary>Apps ask where their icon is (to place balloons and menus), so keep its screen rectangle current.</summary>
    void UpdateTrayPlacements()
    {
        foreach (var (icon, view) in trayViews)
        {
            if (PresentationSource.FromVisual(view) == null || !view.IsVisible) continue;
            var p = view.PointToScreen(new Point(0, 0));
            double scale = Screens.Scale;
            icon.Placement = new ManagedShell.Interop.NativeMethods.Rect
            {
                Left = (int)p.X,
                Top = (int)p.Y,
                Right = (int)(p.X + view.ActualWidth * scale),
                Bottom = (int)(p.Y + view.ActualHeight * scale),
            };
        }
    }

    static uint CursorParam()
    {
        Native.GetCursorPos(out var p);
        return (uint)((p.Y << 16) | (p.X & 0xFFFF));
    }

    // ---------- status ----------

    void UpdateClock()
    {
        var now = DateTime.Now;
        var time = compact ? now.ToString("ddd d MMM HH:mm", L.Culture) : now.ToString("HH:mm", L.Culture);
        if (Time.Text == time) return;
        Time.Text = time;
        Date.Text = now.ToString("d", L.Culture);
    }

    void UpdateStatus()
    {
        if (Native.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryFlag != 255)
        {
            BatteryText.Text = L.Percent(power.BatteryLifePercent);
            BatteryLevel.Width = Math.Max(2, 18 * Math.Min((int)power.BatteryLifePercent, 100) / 100.0);
            BatteryButton.Visibility = Visibility.Visible;
        }
        else BatteryButton.Visibility = Visibility.Collapsed;

        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .ToList();
        bool wifi = up.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        bool wired = !wifi && up.Count > 0;
        WifiIcon.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
        WifiIcon.Opacity = wifi ? 1 : 0.35;
        EthernetIcon.Visibility = wired ? Visibility.Visible : Visibility.Collapsed;
        UpdateVolumeIcon(AudioVolume.Level);
    }

    void UpdateLanguage()
    {
        // Only worth showing when there is more than one keyboard layout to switch between.
        if (Native.GetKeyboardLayoutList(0, null) < 2)
        {
            LanguageButton.Visibility = Visibility.Collapsed;
            return;
        }
        var thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        int langId = (int)(Native.GetKeyboardLayout(thread).ToInt64() & 0xFFFF);
        try { LanguageText.Text = new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch { LanguageText.Text = "?"; }
        LanguageButton.Visibility = Visibility.Visible;
    }

    void UpdateVolumeIcon(double? level)
    {
        bool muted = level is null or <= 0.001;
        VolumeMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeWaves.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
    }

    void Volume_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (AudioVolume.Level is not double level) return;
        level = Math.Clamp(level + (e.Delta > 0 ? 0.04 : -0.04), 0, 1);
        AudioVolume.Level = level;
        UpdateVolumeIcon(level);
        volumeTip.Content = L.T("Ses") + " " + L.Percent(Math.Round(level * 100));
        volumeTip.IsOpen = true;
        tipTimer.Stop();
        tipTimer.Start();
    }

    void Overflow_Click(object sender, RoutedEventArgs e) => Overflow.IsOpen = !Overflow.IsOpen;
    void Language_Click(object sender, RoutedEventArgs e) => Native.SendCombo(0x5B, 0x20);
    void ControlCenter_Click(object sender, RoutedEventArgs e) => App.ControlCenter.Toggle(fromBar: !compact);
    void Clock_Click(object sender, RoutedEventArgs e) => App.NotificationCenter.Toggle(fromTop: compact);
}
