using ManagedShell.WindowsTray;
using System.ComponentModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Full-width bar at the bottom of the screen: apps on the left (Dock-style),
/// tray icons, network/volume and the clock on the right.
/// </summary>
public partial class DockWindow : Window
{
    public const double BarHeight = 50;
    const double HeadRoom = 60, Cell = 44, IconSize = 30;
    static readonly CultureInfo Turkish = new("tr-TR");

    readonly Config config = App.Config;
    readonly Dictionary<string, DockEntry> entries = new();
    readonly List<string> runningOrder = new();
    readonly Rectangle runningSeparator;
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer fastTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer slowTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly ImageBrush blurBrush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    readonly Dictionary<NotifyIcon, FrameworkElement> trayViews = new();
    readonly DispatcherTimer tipTimer = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly PreviewWindow preview = new();
    List<object> layout = new(); // DockEntry or separator Rectangle, left to right
    DockEntry? hovered;
    IntPtr hwnd;

    // Drag to reorder (macOS Dock style): press on an icon, move, drop. Drag up and away to remove.
    const double Slot = Cell + 2, RemoveDistance = 50;
    DockEntry? pressed;
    Point pressPoint;
    bool dragging;

    public DockWindow()
    {
        InitializeComponent();
        Width = SystemParameters.PrimaryScreenWidth;
        Height = BarHeight + HeadRoom;
        Left = 0;
        Top = SystemParameters.PrimaryScreenHeight - Height;
        Bar.Height = BarHeight;

        // The bar sits on a strip reserved for it, so the wallpaper is what's behind it.
        BgBlur.Background = blurBrush;
        ApplyWallpaper();
        Wallpaper.Changed += ApplyWallpaper;

        StartLogo.Data = Geometry.Parse(Glyphs.AppleLogo);
        runningSeparator = MakeSeparator();

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd);
            var bar = new AppBar(this, AppBar.Bottom, BarHeight);
            bar.FullscreenChanged += fullscreen => Topmost = !fullscreen;
            bar.Register();
            int screenW = Native.GetSystemMetrics(0), screenH = Native.GetSystemMetrics(1);
            TrayHost.SetBarRect(new RECT { Left = 0, Top = screenH - (int)(BarHeight * BitmapUtil.DpiScale(this)), Right = screenW, Bottom = screenH });
        };

        if (TrayHost.Area is { } area) area.TrayIcons.CollectionChanged += (_, _) => RefreshTray();
        Overflow.Opened += (_, _) => Dispatcher.InvokeAsync(UpdateTrayPlacements, DispatcherPriority.Loaded);

        Items.MouseMove += OnDragMove;
        Items.MouseLeftButtonUp += OnDragRelease;
        Items.LostMouseCapture += (_, _) => { if (pressed != null) EndDrag(); };
        Bar.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            ShowBarMenu();
        };
        ShowDesktop.MouseEnter += (_, _) => ShowDesktop.SetResourceReference(Border.BackgroundProperty, "BarHover");
        ShowDesktop.MouseLeave += (_, _) => ShowDesktop.Background = Brushes.Transparent;
        tipTimer.Tick += (_, _) => { tipTimer.Stop(); HideTip(); };
        previewTimer.Tick += (_, _) =>
        {
            previewTimer.Stop();
            if (hovered is { } entry && !dragging) ShowPreview(entry);
        };

        refreshTimer.Tick += (_, _) => Refresh();
        fastTimer.Tick += (_, _) => { UpdateEntryStates(); UpdateClock(); UpdateLanguage(); };
        slowTimer.Tick += (_, _) => { UpdateStatus(); RefreshTray(); };
        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.InvokeAsync(UpdateStatus);

        Refresh();
        RefreshTray();
        UpdateClock();
        UpdateStatus();
        refreshTimer.Start();
        fastTimer.Start();
        slowTimer.Start();
    }

    /// <summary>Puts the bar above other topmost windows (e.g. over Launchpad).</summary>
    public void BringToFront() =>
        Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);

    void ApplyWallpaper()
    {
        var rect = new Rect(0, SystemParameters.PrimaryScreenHeight - BarHeight, Width, BarHeight);
        blurBrush.ImageSource = Wallpaper.Blurred;
        blurBrush.Viewbox = rect;
        // Like macOS: white text over dark wallpapers, dark text over light ones.
        bool dark = Wallpaper.Luminance(rect) < 0.55;
        Resources["BarText"] = Theme.Brush(dark ? "#FFFFFFFF" : "#FF1D1D1F");
        Resources["BarHover"] = Theme.Brush(dark ? "#2EFFFFFF" : "#1F000000");
        BgTint.Background = Theme.Brush(dark ? "#33000000" : "#66FFFFFF");
        BgTint.BorderBrush = Theme.Brush(dark ? "#33FFFFFF" : "#80FFFFFF");
    }

    // ---------- apps ----------

    Rectangle MakeSeparator()
    {
        var r = new Rectangle { Width = 1, Height = 26, Margin = new Thickness(7, 0, 7, 0), Opacity = 0.3, VerticalAlignment = VerticalAlignment.Center };
        r.SetResourceReference(Shape.FillProperty, "BarText");
        return r;
    }

    DockEntry CreateEntry(string key, string name, string? launchPath, bool pinned, ImageSource? icon)
    {
        var entry = new DockEntry(key, name, launchPath, pinned, icon, Cell, IconSize);
        entry.View.MouseEnter += (_, _) =>
        {
            hovered = entry;
            UpdateEntryStates();
            if (entry.Windows.Count > 0 && preview.IsVisible)
            {
                ShowPreview(entry); // already previewing: follow the mouse to the next app at once
                return;
            }
            ShowTip(entry.View, entry.Name);
            if (entry.Windows.Count > 0) previewTimer.Start();
        };
        entry.View.MouseLeave += (_, _) =>
        {
            if (hovered == entry) hovered = null;
            entry.Icon.Opacity = 1;
            UpdateEntryStates();
            HideTip();
            previewTimer.Stop();
            if (preview.IsVisible) preview.HideSoon(); // stays open if the mouse moves onto it
        };
        entry.View.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            previewTimer.Stop();
            preview.HideNow();
            entry.Icon.Opacity = 0.65; // pressed look
            pressed = entry;
            pressPoint = e.GetPosition(Root);
            dragging = false;
            Items.CaptureMouse(); // the panel, not the icon: icons get re-inserted while reordering
        };
        entry.View.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            HideTip();
            previewTimer.Stop();
            preview.HideNow();
            ShowMenu(entry);
        };
        // Middle click opens another window, like on Windows.
        entry.View.MouseUp += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle || entry.LaunchPath == null) return;
            e.Handled = true;
            Ui.Open(entry.LaunchPath);
            if (entry.Windows.Count == 0) Bounce(entry);
        };
        return entry;
    }

    void ShowPreview(DockEntry entry)
    {
        if (entry.Windows.Count == 0) return;
        HideTip();
        var p = entry.View.TranslatePoint(new Point(), Root);
        preview.ShowFor(entry, new Rect(Left + p.X, Top + Root.ActualHeight - BarHeight, entry.View.ActualWidth, BarHeight));
    }

    // ---------- drag to reorder ----------

    int PinnedCount => layout.OfType<DockEntry>().Count(e => e.Pinned);
    double ItemsLeft => Items.TranslatePoint(new Point(), Root).X;
    double BarTop => Root.ActualHeight - BarHeight;

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (pressed == null || !Items.IsMouseCaptured) return;
        var pos = e.GetPosition(Root);
        if (!dragging)
        {
            if ((pos - pressPoint).Length < 6) return;
            dragging = true;
            pressed.Icon.Opacity = 1;
            pressed.View.Opacity = 0.85;
            pressed.View.RenderTransform = new TranslateTransform();
            HideTip();
        }

        // Pinned icons trade places live as you move over them.
        if (pressed.Pinned)
        {
            int current = config.Pinned.FindIndex(p => WindowTracker.Normalize(p.MatchExe) == pressed.Key);
            int target = (int)Math.Clamp(Math.Floor((pos.X - ItemsLeft) / Slot), 0, PinnedCount - 1);
            if (current >= 0 && target != current && target < config.Pinned.Count)
            {
                var pin = config.Pinned[current];
                config.Pinned.RemoveAt(current);
                config.Pinned.Insert(target, pin);
                pressPoint.X += (target - current) * Slot; // the icon's slot moved with it
                Refresh();
            }
        }

        var shift = (TranslateTransform)pressed.View.RenderTransform;
        shift.X = pos.X - pressPoint.X;
        shift.Y = Math.Min(0, pos.Y - pressPoint.Y);
        if (pressed.Pinned && pos.Y < BarTop - RemoveDistance) ShowTip(pressed.View, "Kaldır");
        else HideTip();
    }

    void OnDragRelease(object sender, MouseButtonEventArgs e)
    {
        if (pressed == null) return;
        var entry = pressed;
        var pos = e.GetPosition(Root);
        bool wasDragging = dragging;
        EndDrag();

        if (!wasDragging)
        {
            var p = e.GetPosition(entry.View);
            if (p.X >= 0 && p.Y >= 0 && p.X <= entry.View.ActualWidth && p.Y <= entry.View.ActualHeight) OnClick(entry);
            return;
        }

        if (entry.Pinned && pos.Y < BarTop - RemoveDistance)
        {
            Unpin(entry); // dragged off the bar, like removing from the macOS Dock
            return;
        }
        if (!entry.Pinned && entry.Windows.Count > 0 && pos.X < runningSeparator.TranslatePoint(new Point(), Root).X)
        {
            // A running app dropped among the pinned ones gets pinned there.
            int index = (int)Math.Clamp(Math.Floor((pos.X - ItemsLeft) / Slot), 0, PinnedCount);
            config.Pinned.Insert(Math.Min(index, config.Pinned.Count), Config.CreatePin(entry.Windows[0]));
        }
        config.Save();
        Refresh();
    }

    void EndDrag()
    {
        if (pressed != null)
        {
            pressed.Icon.Opacity = 1;
            pressed.View.Opacity = 1;
            pressed.View.RenderTransform = null;
        }
        pressed = null;
        dragging = false;
        HideTip();
        if (Items.IsMouseCaptured) Items.ReleaseMouseCapture();
    }

    void ShowBarMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Ui.Item("Görev Yöneticisi", () => Ui.Open("taskmgr.exe"), "⌃⇧Esc"));
        menu.Items.Add(Ui.Item("Masaüstünü Göster", ToggleDesktop, "⊞D"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Kişiselleştirme Ayarları…", () => Ui.Open("ms-settings:personalization")));
        menu.Items.Add(Ui.Item("Tepsi Simgesi Ayarları…", () => Ui.Open("ms-settings:taskbar")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("MacLook Ayarları…", () => App.Settings.Open()));
        menu.Items.Add(Ui.Item("MacLook Kısayolları", MenuBarWindow.ShowShortcuts));
        menu.Items.Add(Ui.Item("MacLook'tan Çık", () => App.Quit("bar menüsü")));
        menu.IsOpen = true;
    }

    DockEntry GetEntry(string id, Func<DockEntry> create)
    {
        if (!entries.TryGetValue(id, out var entry)) entries[id] = entry = create();
        return entry;
    }

    /// <summary>Pinned apps | running apps that aren't pinned.</summary>
    void Refresh()
    {
        var groups = WindowTracker.GetWindows().GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.ToList());
        var newLayout = new List<object>();

        var pinnedKeys = new HashSet<string>();
        foreach (var p in config.Pinned)
        {
            var key = WindowTracker.Normalize(p.MatchExe);
            if (pinnedKeys.Add(key))
                newLayout.Add(GetEntry("p|" + key, () => CreateEntry(key, p.Name, p.Path, pinned: true, IconLoader.Get(p.Path))));
        }

        runningOrder.RemoveAll(k => !groups.ContainsKey(k) || pinnedKeys.Contains(k));
        foreach (var k in groups.Keys)
            if (!pinnedKeys.Contains(k) && !runningOrder.Contains(k)) runningOrder.Add(k);
        if (runningOrder.Count > 0)
        {
            newLayout.Add(runningSeparator);
            foreach (var k in runningOrder)
            {
                var first = groups[k][0];
                newLayout.Add(GetEntry("r|" + k, () =>
                    CreateEntry(k, WindowTracker.GetAppName(first.ExePath), first.ExePath, pinned: false, IconLoader.Get(WindowTracker.GetIconPath(first)))));
            }
        }
        foreach (var entry in newLayout.OfType<DockEntry>())
            entry.Windows = groups.TryGetValue(entry.Key, out var list) ? list : new List<AppWindow>();
        UpdateEntryStates();

        if (newLayout.SequenceEqual(layout)) return;
        foreach (var id in entries.Where(kv => !newLayout.Contains(kv.Value)).Select(kv => kv.Key).ToList())
            entries.Remove(id);
        if (hovered != null && !newLayout.Contains(hovered))
        {
            hovered = null;
            HideTip();
        }

        layout = newLayout;
        Items.Children.Clear();
        foreach (var item in layout)
            Items.Children.Add(item is DockEntry entry ? entry.View : (UIElement)item);
    }

    /// <summary>Dot under running apps, a wider bar and soft highlight under the one in front.</summary>
    void UpdateEntryStates()
    {
        var fg = Native.GetForegroundWindow();
        foreach (var entry in layout.OfType<DockEntry>())
        {
            bool running = entry.Windows.Count > 0;
            bool front = running && entry.Windows.Any(w => w.Handle == fg);
            entry.Dot.Opacity = running ? 1 : 0;
            entry.Dot.Width = front ? 14 : 4;
            entry.Highlight.Opacity = entry == hovered ? 1 : front ? 0.6 : 0;
        }
    }

    void OnClick(DockEntry entry)
    {
        App.Launchpad.Dismiss();
        App.StartMenu.Dismiss();
        App.ControlCenter.Dismiss();

        var windows = entry.Windows;
        if (windows.Count == 0)
        {
            if (entry.LaunchPath == null) return;
            Ui.Open(entry.LaunchPath);
            Bounce(entry);
            return;
        }

        var fg = Native.GetForegroundWindow();
        int index = windows.FindIndex(w => w.Handle == fg);
        if (index < 0) WindowTracker.Activate(windows[0].Handle);              // bring the app forward
        else if (windows.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE); // already in front: minimize
        else WindowTracker.Activate(windows[^1].Handle);                        // several windows: cycle
    }

    void ShowMenu(DockEntry entry)
    {
        var menu = new ContextMenu { PlacementTarget = entry.View, Placement = PlacementMode.Top, VerticalOffset = -2 };
        foreach (var w in entry.Windows.Take(12))
            menu.Items.Add(Ui.Item(w.Title.Length > 60 ? w.Title[..57] + "…" : w.Title, () => WindowTracker.Activate(w.Handle)));
        if (entry.Windows.Count > 0) menu.Items.Add(new Separator());

        if (entry.LaunchPath is string path)
            menu.Items.Add(Ui.Item(entry.Windows.Count > 0 ? "Yeni Pencere" : "Aç", () =>
            {
                if (entry.Windows.Count == 0) Bounce(entry);
                Ui.Open(path);
            }));
        menu.Items.Add(entry.Pinned
            ? Ui.Item("Bardan Kaldır", () => Unpin(entry))
            : Ui.Item("Barda Tut", () => Pin(entry)));

        if (entry.Windows.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Çık", () =>
            {
                foreach (var w in entry.Windows) Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }));
        }
        menu.IsOpen = true;
    }

    /// <summary>Pins or unpins an app chosen from the Start menu.</summary>
    public void TogglePin(string name, string launchPath, string key)
    {
        if (config.Pinned.RemoveAll(p => WindowTracker.Normalize(p.MatchExe) == key) == 0)
            config.Pinned.Add(new PinnedApp { Name = name, Path = launchPath, MatchExe = key });
        config.Save();
        Refresh();
    }

    void Pin(DockEntry entry)
    {
        if (entry.Windows.Count == 0) return;
        config.Pinned.Add(Config.CreatePin(entry.Windows[0]));
        config.Save();
        Refresh();
    }

    void Unpin(DockEntry entry)
    {
        config.Pinned.RemoveAll(p => WindowTracker.Normalize(p.MatchExe) == entry.Key);
        config.Save();
        Refresh();
    }

    void Bounce(DockEntry entry)
    {
        const double h = -14;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(h, KeyTime.FromPercent(0.25), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.5), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(h * 0.5, KeyTime.FromPercent(0.7), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.9), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        entry.Bounce.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void ShowTip(FrameworkElement anchor, string text)
    {
        TipText.Text = text;
        Tip.Visibility = Visibility.Visible;
        Tip.UpdateLayout();
        var p = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(Tip, Math.Round(Math.Max(4, p.X - Tip.ActualWidth / 2)));
        Canvas.SetTop(Tip, Math.Round(p.Y - Tip.ActualHeight - 8));
    }

    void HideTip() => Tip.Visibility = Visibility.Collapsed;

    // ---------- tray ----------

    /// <summary>Icons Windows is set to show sit on the bar; the rest go under ^.</summary>
    void RefreshTray()
    {
        var area = TrayHost.Area;
        var icons = area?.TrayIcons.Where(i => !i.IsHidden && i.Icon != null).ToList() ?? new List<NotifyIcon>();
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
        var image = new Image { Source = icon.Icon, Width = 18, Height = 18 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var host = new Border
        {
            Width = 30,
            Height = 36,
            CornerRadius = new CornerRadius(7),
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
            double scale = BitmapUtil.DpiScale(view);
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

    void Overflow_Click(object sender, RoutedEventArgs e) => Overflow.IsOpen = !Overflow.IsOpen;

    // ---------- status ----------

    void UpdateClock()
    {
        var now = DateTime.Now;
        var time = now.ToString("HH:mm", Turkish);
        if (Time.Text != time)
        {
            Time.Text = time;
            Date.Text = now.ToString("dd.MM.yyyy", Turkish);
        }
    }

    void UpdateStatus()
    {
        if (Native.GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryFlag != 255)
        {
            BatteryText.Text = $"%{power.BatteryLifePercent}";
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
        ShowTip(ControlCenterButton, $"Ses %{Math.Round(level * 100)}");
        tipTimer.Stop();
        tipTimer.Start();
    }

    static void ToggleDesktop() => Native.SendCombo(0x5B, (byte)'D');

    void ShowDesktop_Click(object sender, MouseButtonEventArgs e) => ToggleDesktop();
    void Language_Click(object sender, RoutedEventArgs e) => Native.SendCombo(0x5B, 0x20);
    void ControlCenter_Click(object sender, RoutedEventArgs e) => App.ControlCenter.Toggle(fromBar: true);
    void Start_Click(object sender, RoutedEventArgs e) => App.StartMenu.Toggle();
    void Clock_Click(object sender, RoutedEventArgs e) => App.NotificationCenter.Toggle();
}

/// <summary>One app on the bar: pinned or just running.</summary>
internal sealed class DockEntry
{
    public string Key { get; }
    public string Name { get; }
    public string? LaunchPath { get; }
    public bool Pinned { get; }
    public List<AppWindow> Windows { get; set; } = new();

    public Grid View { get; }
    public Image Icon { get; }
    public Border Highlight { get; }
    public Border Dot { get; }
    public TranslateTransform Bounce { get; } = new();

    public DockEntry(string key, string name, string? launchPath, bool pinned, ImageSource? icon, double cell, double iconSize)
    {
        Key = key;
        Name = name;
        LaunchPath = launchPath;
        Pinned = pinned;

        Highlight = new Border { CornerRadius = new CornerRadius(8), Opacity = 0 };
        Highlight.SetResourceReference(Border.BackgroundProperty, "BarHover");
        Icon = new Image { Source = icon, Width = iconSize, Height = iconSize, RenderTransform = Bounce, Margin = new Thickness(0, 0, 0, 2) };
        RenderOptions.SetBitmapScalingMode(Icon, BitmapScalingMode.HighQuality);
        Dot = new Border
        {
            Width = 4,
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 2),
            Opacity = 0,
        };
        Dot.SetResourceReference(Border.BackgroundProperty, "BarText");

        View = new Grid { Width = cell, Height = cell, Background = Brushes.Transparent, Margin = new Thickness(1, 0, 1, 0) };
        View.Children.Add(Highlight);
        View.Children.Add(Icon);
        View.Children.Add(Dot);
    }
}
