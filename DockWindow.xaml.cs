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
/// The bar at the bottom of one screen. Two styles:
/// a full-width bar (apps left, tray and clock right) or a floating macOS Dock with magnification.
/// </summary>
public partial class DockWindow : Window
{
    const double HeadRoom = 56, Gap = 6, SepWidth = 15, SidePad = 8;
    const double DockTopPad = 6, DotArea = 10, DockBottomPad = 2, DockBottomMargin = 6, RemoveDistance = 50;

    static double BarIcon(Config c) => Math.Clamp(c.BarIconSize, 22, 40);
    static double BarHeightFor(Config c) => BarIcon(c) + 20;
    static double DockBase(Config c) => Math.Clamp(c.DockIconSize, 32, 80);
    static double DockBgHeight(Config c) => DockTopPad + DockBase(c) + DotArea + DockBottomPad;

    /// <summary>Height taken by the bar or Dock at the bottom of a screen (DIPs); panels open above it.</summary>
    public static double ReservedHeight => App.Config.IsDock ? DockBgHeight(App.Config) + DockBottomMargin : BarHeightFor(App.Config);

    readonly Config config = App.Config;
    readonly ScreenInfo screen;
    readonly bool primary, dock, multiScreen;
    readonly double iconSize, cell, slot, maxScale;
    readonly Dictionary<string, DockEntry> entries = new();
    readonly List<string> runningOrder = new();
    readonly Rectangle runningSeparator;
    readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer stateTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly ImageBrush blurBrush = new() { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
    readonly PreviewWindow preview = new();
    readonly StatusArea? status;
    List<object> layout = new(); // DockEntry or separator Rectangle, left to right
    DockEntry? hovered;
    IntPtr hwnd;
    Rect blurRect;
    int openMenus;

    // Dock magnification
    double? mouseX;
    bool animating;

    // Drag to reorder: press on an icon, move, drop. Drag up and away to remove.
    DockEntry? pressed;
    Point pressPoint;
    bool dragging;

    // Auto-hide
    bool hidden;
    DateTime awaySince = DateTime.Now;

    internal DockWindow(ScreenInfo screen, bool primary)
    {
        InitializeComponent();
        this.screen = screen;
        this.primary = primary;
        dock = config.IsDock;
        multiScreen = Screens.All().Count > 1;

        if (dock)
        {
            iconSize = DockBase(config);
            cell = iconSize + Gap;
            slot = cell;
            maxScale = config.DockMagnification ? 1.6 : 1;
            Height = iconSize * maxScale + DotArea + DockTopPad + DockBottomPad + DockBottomMargin + HeadRoom;
        }
        else
        {
            iconSize = BarIcon(config);
            cell = iconSize + 14;
            slot = cell + 2; // entries have a 1px margin each side
            maxScale = 1;
            Height = BarHeightFor(config) + HeadRoom;
        }
        Left = screen.Bounds.X;
        Width = screen.Bounds.Width;
        Top = screen.Bounds.Bottom - Height;
        ApplyStyle();

        // The bar sits on a strip reserved for it, so the wallpaper is what's behind it.
        BgBlur.Background = blurBrush;
        ApplyWallpaper();
        Wallpaper.Changed += ApplyWallpaper;
        BgBlur.LayoutUpdated += (_, _) => SyncBlur();

        StartLogo.Data = Geometry.Parse(Glyphs.AppleLogo);
        runningSeparator = MakeSeparator();
        if (!dock)
        {
            status = new StatusArea(compact: false, showTray: primary);
            StatusHost.Content = status;
        }

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd);
            var bar = new AppBar(this, AppBar.Bottom, ReservedHeight, screen);
            bar.FullscreenChanged += fullscreen => Topmost = !fullscreen;
            bar.Register(reserve: !config.AutoHideBar);
            if (primary && !dock)
            {
                var px = screen.BoundsPx;
                TrayHost.SetBarRect(new RECT { Left = px.Left, Top = px.Bottom - (int)(ReservedHeight * Screens.Scale), Right = px.Right, Bottom = px.Bottom }, AppBar.Bottom);
            }
        };

        Items.MouseMove += OnDragMove;
        Items.MouseLeftButtonUp += OnDragRelease;
        Items.LostMouseCapture += (_, _) => { if (pressed != null) EndDrag(); };
        Bar.MouseRightButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            ShowBarMenu();
        };
        if (dock)
        {
            Root.MouseMove += (_, e) => { if (!dragging) { mouseX = e.GetPosition(Root).X; UpdateTargets(); } };
            Root.MouseLeave += (_, _) => { mouseX = null; UpdateTargets(); };
        }
        ShowDesktop.MouseEnter += (_, _) => ShowDesktop.SetResourceReference(Border.BackgroundProperty, "BarHover");
        ShowDesktop.MouseLeave += (_, _) => ShowDesktop.Background = Brushes.Transparent;
        previewTimer.Tick += (_, _) =>
        {
            previewTimer.Stop();
            if (hovered is { } entry && !dragging) ShowPreview(entry);
        };

        refreshTimer.Tick += (_, _) => Refresh();
        stateTimer.Tick += (_, _) => UpdateEntryStates();
        edgeTimer.Tick += (_, _) => CheckAutoHide();
        Closed += (_, _) =>
        {
            refreshTimer.Stop();
            stateTimer.Stop();
            edgeTimer.Stop();
            Wallpaper.Changed -= ApplyWallpaper;
            preview.Close();
        };

        Refresh();
        refreshTimer.Start();
        stateTimer.Start();
        if (config.AutoHideBar)
        {
            SetHidden(true, animate: false);
            edgeTimer.Start();
        }
    }

    /// <summary>Puts the bar above other topmost windows (e.g. over Launchpad).</summary>
    public void BringToFront() =>
        Native.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /* NOSIZE|NOMOVE|NOACTIVATE */);

    /// <summary>Lays the bar out as a full-width strip or as a floating Dock.</summary>
    void ApplyStyle()
    {
        if (dock)
        {
            double bg = DockBgHeight(config);
            Bar.HorizontalAlignment = HorizontalAlignment.Center;
            Bar.Margin = new Thickness(0, 0, 0, DockBottomMargin);
            foreach (var border in new[] { BgBlur, BgTint })
            {
                border.VerticalAlignment = VerticalAlignment.Bottom;
                border.Height = bg;
                border.CornerRadius = new CornerRadius(Math.Round(bg * 0.3));
            }
            BgTint.BorderThickness = new Thickness(1);
            BgBlur.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.25 };
            BarContent.VerticalAlignment = VerticalAlignment.Bottom;
            Items.VerticalAlignment = VerticalAlignment.Bottom;
            Items.Margin = new Thickness(SidePad, 0, SidePad, DockBottomPad);
            Items.Background = Theme.Brush("#01000000"); // keeps the mouse "inside" while icons are magnified
            StartButton.Visibility = ShowDesktop.Visibility = StatusHost.Visibility = Visibility.Collapsed;
        }
        else
        {
            Bar.Height = BarHeightFor(config);
            BgTint.BorderThickness = new Thickness(0, 1, 0, 0);
        }
    }

    void ApplyWallpaper()
    {
        blurBrush.ImageSource = Wallpaper.For(screen);
        blurRect = Rect.Empty;
        SyncBlur();
        // Like macOS: white text over dark wallpapers, dark text over light ones.
        double h = ReservedHeight;
        bool darkBg = Wallpaper.Luminance(screen, new Rect(0, screen.Bounds.Height - h, screen.Bounds.Width, h)) < 0.55;
        Resources["BarText"] = Theme.Brush(darkBg ? "#FFFFFFFF" : "#FF1D1D1F");
        Resources["BarHover"] = Theme.Brush(darkBg ? "#2EFFFFFF" : "#1F000000");
        BgTint.Background = Theme.Brush(darkBg ? (dock ? "#40000000" : "#33000000") : (dock ? "#59FFFFFF" : "#66FFFFFF"));
        BgTint.BorderBrush = Theme.Brush(darkBg ? "#33FFFFFF" : "#80FFFFFF");
    }

    /// <summary>Keeps the blurred wallpaper aligned with what's really behind the bar (the Dock changes width).</summary>
    void SyncBlur()
    {
        if (BgBlur.ActualWidth == 0) return;
        var p = BgBlur.TranslatePoint(new Point(), Root);
        var rect = new Rect(Left - screen.Bounds.X + p.X, Top - screen.Bounds.Y + p.Y, BgBlur.ActualWidth, BgBlur.ActualHeight);
        if (rect == blurRect) return;
        blurRect = rect;
        blurBrush.Viewbox = rect;
    }

    // ---------- apps ----------

    Rectangle MakeSeparator()
    {
        var r = new Rectangle { Width = 1, Opacity = 0.3, Margin = new Thickness((SepWidth - 1) / 2, 0, (SepWidth - 1) / 2, 0) };
        if (dock)
        {
            r.Height = iconSize * 0.7;
            r.VerticalAlignment = VerticalAlignment.Bottom;
            r.Margin = new Thickness((SepWidth - 1) / 2, 0, (SepWidth - 1) / 2, DotArea + iconSize * 0.15);
        }
        else
        {
            r.Height = iconSize * 0.85;
            r.VerticalAlignment = VerticalAlignment.Center;
        }
        r.SetResourceReference(Shape.FillProperty, "BarText");
        return r;
    }

    DockEntry CreateEntry(string key, string name, string? launchPath, bool pinned, ImageSource? icon)
    {
        var entry = new DockEntry(key, name, launchPath, pinned, icon);
        SizeEntry(entry, 1);
        entry.View.MouseEnter += (_, _) =>
        {
            hovered = entry;
            UpdateEntryStates();
            if (entry.AllWindows.Count > 0 && preview.IsVisible)
            {
                ShowPreview(entry); // already previewing: follow the mouse to the next app at once
                return;
            }
            ShowTip(entry.Icon, entry.Name);
            if (entry.AllWindows.Count > 0) previewTimer.Start();
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
            if (entry.AllWindows.Count == 0) Bounce(entry);
        };
        return entry;
    }

    /// <summary>Sizes an entry for the current style; <paramref name="scale"/> is the Dock magnification.</summary>
    void SizeEntry(DockEntry entry, double scale)
    {
        if (dock)
        {
            double size = iconSize * scale;
            entry.View.Width = size + Gap;
            entry.View.Height = size + DotArea;
            entry.View.Margin = new Thickness(0);
            entry.Icon.Width = entry.Icon.Height = size;
            entry.Icon.VerticalAlignment = VerticalAlignment.Top;
            entry.Icon.Margin = new Thickness(0);
            entry.Highlight.Visibility = Visibility.Collapsed; // the Dock has no hover highlight
        }
        else
        {
            entry.View.Width = entry.View.Height = cell;
            entry.View.Margin = new Thickness(1, 0, 1, 0);
            entry.Icon.Width = entry.Icon.Height = iconSize;
        }
    }

    DockEntry GetEntry(string id, Func<DockEntry> create)
    {
        if (!entries.TryGetValue(id, out var entry)) entries[id] = entry = create();
        return entry;
    }

    /// <summary>Pinned apps | running apps that aren't pinned (only those with a window on this screen).</summary>
    public void Refresh()
    {
        var all = WindowTracker.GetWindows();
        var here = multiScreen ? all.Where(w => Screens.MonitorOf(w.Handle) == screen.Handle).ToList() : all;
        var groups = here.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.ToList());
        var allGroups = multiScreen ? all.GroupBy(w => w.Key).ToDictionary(g => g.Key, g => g.ToList()) : groups;
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
        {
            entry.Windows = groups.TryGetValue(entry.Key, out var list) ? list : new List<AppWindow>();
            entry.AllWindows = allGroups.TryGetValue(entry.Key, out var allList) ? allList : new List<AppWindow>();
        }
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
        UpdateTargets();
    }

    /// <summary>Dot under running apps; on the bar, a wider line and soft highlight under the one in front.</summary>
    void UpdateEntryStates()
    {
        var fg = Native.GetForegroundWindow();
        foreach (var entry in layout.OfType<DockEntry>())
        {
            bool running = entry.Windows.Count > 0;
            bool front = running && entry.Windows.Any(w => w.Handle == fg);
            entry.Dot.Opacity = running ? 1 : 0;
            entry.Dot.Width = front && !dock ? 14 : 4;
            entry.Highlight.Opacity = entry == hovered ? 1 : front ? 0.6 : 0;
        }
    }

    // ---------- Dock magnification ----------

    /// <summary>
    /// Each icon grows by its distance to the mouse, measured on the un-magnified layout
    /// so the effect doesn't feed back into itself.
    /// </summary>
    void UpdateTargets()
    {
        if (!dock) return;
        double total = layout.Sum(o => o is DockEntry ? cell : SepWidth) + SidePad * 2;
        double x = (Width - total) / 2 + SidePad;
        double range = iconSize * 2.6;
        foreach (var item in layout)
        {
            double w = item is DockEntry ? cell : SepWidth;
            if (item is DockEntry entry)
            {
                entry.Target = 1;
                if (mouseX is double mx && maxScale > 1)
                {
                    double d = Math.Abs(mx - (x + w / 2));
                    if (d < range) entry.Target = 1 + (maxScale - 1) * (Math.Cos(Math.PI * d / range) + 1) / 2;
                }
            }
            x += w;
        }
        if (!animating)
        {
            animating = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        bool moving = false;
        foreach (var entry in layout.OfType<DockEntry>())
        {
            double diff = entry.Target - entry.Scale;
            if (Math.Abs(diff) > 0.002) { entry.Scale += diff * 0.3; moving = true; }
            else entry.Scale = entry.Target;
            SizeEntry(entry, entry.Scale);
        }
        if (hovered != null && Tip.Visibility == Visibility.Visible) PositionTip(hovered.Icon);
        if (!moving)
        {
            CompositionTarget.Rendering -= OnFrame;
            animating = false;
        }
    }

    // ---------- previews ----------

    void ShowPreview(DockEntry entry)
    {
        if (entry.AllWindows.Count == 0) return;
        HideTip();
        var p = entry.View.TranslatePoint(new Point(), Root);
        double barTop = Top + Root.ActualHeight - ReservedHeight;
        preview.ShowFor(entry, new Rect(Left + p.X, barTop, entry.View.ActualWidth, ReservedHeight), screen);
    }

    // ---------- drag to reorder ----------

    int PinnedCount => layout.OfType<DockEntry>().Count(e => e.Pinned);
    double ItemsLeft => Items.TranslatePoint(new Point(), Root).X;
    double BarTop => Root.ActualHeight - ReservedHeight;

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (pressed == null || !Items.IsMouseCaptured) return;
        var pos = e.GetPosition(Root);
        if (!dragging)
        {
            if ((pos - pressPoint).Length < 6) return;
            dragging = true;
            mouseX = null; // icons go back to normal size while dragging
            UpdateTargets();
            pressed.Icon.Opacity = 1;
            pressed.View.Opacity = 0.85;
            pressed.View.RenderTransform = new TranslateTransform();
            HideTip();
        }

        // Pinned icons trade places live as you move over them.
        if (pressed.Pinned)
        {
            int current = config.Pinned.FindIndex(p => WindowTracker.Normalize(p.MatchExe) == pressed.Key);
            int target = (int)Math.Clamp(Math.Floor((pos.X - ItemsLeft) / slot), 0, PinnedCount - 1);
            if (current >= 0 && target != current && target < config.Pinned.Count)
            {
                var pin = config.Pinned[current];
                config.Pinned.RemoveAt(current);
                config.Pinned.Insert(target, pin);
                pressPoint.X += (target - current) * slot; // the icon's slot moved with it
                Refresh();
            }
        }

        var shift = (TranslateTransform)pressed.View.RenderTransform;
        shift.X = pos.X - pressPoint.X;
        shift.Y = Math.Min(0, pos.Y - pressPoint.Y);
        if (pressed.Pinned && pos.Y < BarTop - RemoveDistance) ShowTip(pressed.Icon, L.T("Kaldır"));
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
        if (!entry.Pinned && entry.AllWindows.Count > 0 && pos.X < runningSeparator.TranslatePoint(new Point(), Root).X)
        {
            // A running app dropped among the pinned ones gets pinned there.
            int index = (int)Math.Clamp(Math.Floor((pos.X - ItemsLeft) / slot), 0, PinnedCount);
            config.Pinned.Insert(Math.Min(index, config.Pinned.Count), Config.CreatePin(entry.AllWindows[0]));
        }
        config.Save();
        App.RefreshDocks();
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

    // ---------- auto-hide ----------

    double VisibleHeight => dock ? DockBgHeight(config) + DockBottomMargin : BarHeightFor(config);

    void SetHidden(bool hide, bool animate = true)
    {
        hidden = hide;
        double to = hide ? VisibleHeight + 6 : 0;
        if (!animate)
        {
            HideShift.BeginAnimation(TranslateTransform.YProperty, null);
            HideShift.Y = to;
            return;
        }
        HideShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = hide ? EasingMode.EaseIn : EasingMode.EaseOut } });
    }

    /// <summary>Shows the bar when the mouse touches the bottom edge, hides it again once the mouse has left.</summary>
    void CheckAutoHide()
    {
        Native.GetCursorPos(out var p);
        var px = screen.BoundsPx;
        bool onScreen = p.X >= px.Left && p.X < px.Right && p.Y >= px.Top && p.Y <= px.Bottom;
        if (hidden)
        {
            if (onScreen && p.Y >= px.Bottom - 2) SetHidden(false);
            return;
        }
        bool near = onScreen && p.Y >= px.Bottom - (VisibleHeight + 20) * Screens.Scale;
        bool busy = dragging || openMenus > 0 || preview.IsVisible || (status?.IsBusy ?? false)
                    || App.StartMenu.IsOpen || App.ControlCenter.IsOpen || App.NotificationCenter.IsOpen || App.Launchpad.IsOpen;
        if (near || busy) { awaySince = DateTime.Now; return; }
        if (DateTime.Now - awaySince > TimeSpan.FromMilliseconds(700)) SetHidden(true);
    }

    // ---------- actions ----------

    void OnClick(DockEntry entry)
    {
        App.DismissPanels();

        // This screen's windows first; an app open only on another screen is still brought forward.
        var windows = entry.Windows.Count > 0 ? entry.Windows : entry.AllWindows;
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
        foreach (var w in entry.AllWindows.Take(12))
            menu.Items.Add(Ui.Item(w.Title.Length > 60 ? w.Title[..57] + "…" : w.Title, () => WindowTracker.Activate(w.Handle)));
        if (entry.AllWindows.Count > 0) menu.Items.Add(new Separator());

        if (entry.LaunchPath is string path)
            menu.Items.Add(Ui.Item(entry.AllWindows.Count > 0 ? "Yeni Pencere" : "Aç", () =>
            {
                if (entry.AllWindows.Count == 0) Bounce(entry);
                Ui.Open(path);
            }));
        menu.Items.Add(entry.Pinned
            ? Ui.Item(dock ? "Dock'tan Kaldır" : "Bardan Kaldır", () => Unpin(entry))
            : Ui.Item(dock ? "Dock'ta Tut" : "Barda Tut", () => Pin(entry)));

        if (entry.AllWindows.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Ui.Item("Çık", () =>
            {
                foreach (var w in entry.AllWindows) Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }));
        }
        OpenMenu(menu);
    }

    void ShowBarMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Ui.Item("Görev Yöneticisi", () => Ui.Open("taskmgr.exe"), "⌃⇧Esc"));
        menu.Items.Add(Ui.Item("Masaüstünü Göster", ToggleDesktop, "⊞D"));
        menu.Items.Add(Ui.Item("Mission Control", () => App.MissionControl.Open()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("Kişiselleştirme Ayarları…", () => Ui.Open("ms-settings:personalization")));
        menu.Items.Add(Ui.Item("Tepsi Simgesi Ayarları…", () => Ui.Open("ms-settings:taskbar")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Ui.Item("MacLook Ayarları…", () => App.Settings.Open()));
        menu.Items.Add(Ui.Item("MacLook Kısayolları", MenuBarWindow.ShowShortcuts));
        menu.Items.Add(Ui.Item("MacLook'tan Çık", () => App.Quit("bar menüsü")));
        OpenMenu(menu);
    }

    /// <summary>Keeps an auto-hiding bar up while one of its menus is open.</summary>
    void OpenMenu(ContextMenu menu)
    {
        openMenus++;
        menu.Closed += (_, _) => openMenus--;
        menu.IsOpen = true;
    }

    /// <summary>Pins or unpins an app chosen from the Start menu.</summary>
    public void TogglePin(string name, string launchPath, string key)
    {
        if (config.Pinned.RemoveAll(p => WindowTracker.Normalize(p.MatchExe) == key) == 0)
            config.Pinned.Add(new PinnedApp { Name = name, Path = launchPath, MatchExe = key });
        config.Save();
        App.RefreshDocks();
    }

    void Pin(DockEntry entry)
    {
        if (entry.AllWindows.Count == 0) return;
        config.Pinned.Add(Config.CreatePin(entry.AllWindows[0]));
        config.Save();
        App.RefreshDocks();
    }

    void Unpin(DockEntry entry)
    {
        config.Pinned.RemoveAll(p => WindowTracker.Normalize(p.MatchExe) == entry.Key);
        config.Save();
        App.RefreshDocks();
    }

    void Bounce(DockEntry entry)
    {
        double h = -iconSize * (dock ? 0.4 : 0.45);
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
        PositionTip(anchor);
    }

    void PositionTip(FrameworkElement anchor)
    {
        var p = anchor.TranslatePoint(new Point(anchor.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(Tip, Math.Round(Math.Clamp(p.X - Tip.ActualWidth / 2, 4, Math.Max(4, Width - Tip.ActualWidth - 4))));
        Canvas.SetTop(Tip, Math.Round(p.Y - Tip.ActualHeight - 8));
    }

    void HideTip() => Tip.Visibility = Visibility.Collapsed;

    static void ToggleDesktop() => Native.SendCombo(0x5B, (byte)'D');

    void ShowDesktop_Click(object sender, MouseButtonEventArgs e) => ToggleDesktop();
    void Start_Click(object sender, RoutedEventArgs e) => App.StartMenu.Toggle();
}

/// <summary>One app on the bar: pinned or just running.</summary>
internal sealed class DockEntry
{
    public string Key { get; }
    public string Name { get; }
    public string? LaunchPath { get; }
    public bool Pinned { get; }
    /// <summary>Windows on this bar's screen.</summary>
    public List<AppWindow> Windows { get; set; } = new();
    /// <summary>Windows on any screen.</summary>
    public List<AppWindow> AllWindows { get; set; } = new();

    public Grid View { get; }
    public Image Icon { get; }
    public Border Highlight { get; }
    public Border Dot { get; }
    public TranslateTransform Bounce { get; } = new();
    public double Scale { get; set; } = 1;
    public double Target { get; set; } = 1;

    public DockEntry(string key, string name, string? launchPath, bool pinned, ImageSource? icon)
    {
        Key = key;
        Name = L.T(name);
        LaunchPath = launchPath;
        Pinned = pinned;

        Highlight = new Border { CornerRadius = new CornerRadius(8), Opacity = 0 };
        Highlight.SetResourceReference(Border.BackgroundProperty, "BarHover");
        Icon = new Image { Source = icon, RenderTransform = Bounce, Margin = new Thickness(0, 0, 0, 2) };
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

        View = new Grid { Background = Brushes.Transparent };
        View.Children.Add(Highlight);
        View.Children.Add(Icon);
        View.Children.Add(Dot);
    }
}
