using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MacLook;

/// <summary>Full-screen grid of every installed app, opened with the Windows key.</summary>
public partial class LaunchpadWindow : Window
{
    const int Columns = 7, Rows = 5, GridTop = 120;
    static readonly CompareInfo Compare = new CultureInfo("tr-TR").CompareInfo;

    readonly List<FrameworkElement> allPages = new();
    readonly double cellW, cellH, iconSize;
    List<AppInfo> results = new();
    int page, pageCount;
    bool closing, catalogDirty = true;
    DateTime lastWheel, lastCatalogRefresh = DateTime.Now;

    // mouse press / swipe tracking
    Point? pressPoint;
    FrameworkElement? pressedTile;
    bool dragging;
    double dragOrigin;

    public bool IsOpen => IsVisible && !closing;

    public LaunchpadWindow()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        cellW = Math.Min(Width * 0.82 / Columns, 170);
        cellH = Math.Min((Height - GridTop - 180) / Rows, 160);
        iconSize = Math.Round(Math.Min(cellW, cellH) * 0.56);

        AppCatalog.Updated += () =>
        {
            catalogDirty = true;
            if (IsOpen && Search.Text.Length == 0) ShowAllPages();
        };
        Search.TextChanged += (_, _) => OnSearchChanged();
        PreviewKeyDown += OnKey;
        Root.PreviewMouseLeftButtonDown += OnPress;
        Root.PreviewMouseMove += OnDrag;
        Root.PreviewMouseLeftButtonUp += OnRelease;
        Root.MouseWheel += OnWheel;
        Deactivated += (_, _) => Dismiss();
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: false);
    }

    public void Toggle()
    {
        if (IsOpen) Dismiss();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        closing = false;
        Backdrop.Source = Wallpaper.BlurredStrong;
        Search.Text = "";
        ShowAllPages();
        SetPage(0, animate: false);

        Show();
        WindowTracker.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        Search.Focus();
        App.Dock.BringToFront();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        var zoom = new DoubleAnimation(1.12, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease };
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);

        if (DateTime.Now - lastCatalogRefresh > TimeSpan.FromMinutes(5))
        {
            lastCatalogRefresh = DateTime.Now;
            AppCatalog.Refresh(); // pick up newly installed apps
        }
    }

    public void Dismiss()
    {
        if (!IsOpen) return;
        closing = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!closing) return;
            Hide();
            closing = false;
        };
        BeginAnimation(OpacityProperty, fade);
        var zoom = new DoubleAnimation(1.06, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    void Launch(AppInfo app)
    {
        Ui.Open(app.LaunchPath);
        Dismiss();
    }

    // ---------- pages ----------

    void ShowAllPages()
    {
        if (catalogDirty)
        {
            allPages.Clear();
            var apps = AppCatalog.Apps;
            const int perPage = Columns * Rows;
            for (int i = 0; i < Math.Max(1, apps.Count); i += perPage)
                allPages.Add(MakePage(apps.Skip(i).Take(perPage)));
            catalogDirty = false;
        }
        Pages.Children.Clear();
        foreach (var p in allPages) Pages.Children.Add(p);
        pageCount = allPages.Count;

        Dots.Children.Clear();
        for (int i = 0; i < pageCount; i++)
            Dots.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(5, 0, 5, 0), Fill = Brushes.White, Tag = i, Cursor = Cursors.Hand });
        Dots.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        SetPage(Math.Min(page, pageCount - 1), animate: false);
    }

    FrameworkElement MakePage(IEnumerable<AppInfo> apps)
    {
        var grid = new UniformGrid
        {
            Columns = Columns,
            Rows = Rows,
            Width = cellW * Columns,
            Height = cellH * Rows,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, GridTop, 0, 0),
        };
        foreach (var app in apps) grid.Children.Add(MakeTile(app));
        var pageHost = new Grid { Width = Width, Height = Height };
        pageHost.Children.Add(grid);
        return pageHost;
    }

    FrameworkElement MakeTile(AppInfo app)
    {
        var image = new Image { Source = app.Icon, Width = iconSize, Height = iconSize };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var label = new TextBlock
        {
            Text = app.Name,
            Foreground = Brushes.White,
            FontSize = 13,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = cellW - 20,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var tile = new StackPanel
        {
            Background = Brushes.Transparent,
            Tag = app,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tile.Children.Add(image);
        tile.Children.Add(label);
        return tile;
    }

    void SetPage(int index, bool animate = true)
    {
        page = Math.Clamp(index, 0, Math.Max(0, pageCount - 1));
        double from = PagesShift.X, to = -page * Width;
        PagesShift.BeginAnimation(TranslateTransform.XProperty, null);
        PagesShift.X = to;
        if (animate && from != to)
            PagesShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(380)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } });
        for (int i = 0; i < Dots.Children.Count; i++)
            ((UIElement)Dots.Children[i]).Opacity = i == page ? 1 : 0.35;
    }

    // ---------- search ----------

    void OnSearchChanged()
    {
        var query = Search.Text.Trim();
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (query.Length == 0)
        {
            ShowAllPages();
            return;
        }
        results = AppCatalog.Apps
            .Where(a => Compare.IndexOf(a.Name, query, CompareOptions.IgnoreCase) >= 0)
            .OrderBy(a => Compare.IsPrefix(a.Name, query, CompareOptions.IgnoreCase) ? 0 : 1)
            .Take(Columns * Rows)
            .ToList();
        Pages.Children.Clear();
        Pages.Children.Add(MakePage(results));
        pageCount = 1;
        Dots.Visibility = Visibility.Collapsed;
        SetPage(0, animate: false);
    }

    // ---------- input ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = "";
                else Dismiss();
                e.Handled = true;
                break;
            case Key.Enter:
                if (Search.Text.Length > 0 && results.Count > 0) Launch(results[0]);
                e.Handled = true;
                break;
            case Key.Left or Key.Right when Search.Text.Length == 0:
                SetPage(page + (e.Key == Key.Right ? 1 : -1));
                e.Handled = true;
                break;
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Search.Text.Length > 0 || (DateTime.Now - lastWheel).TotalMilliseconds < 350) return;
        lastWheel = DateTime.Now;
        SetPage(page + (e.Delta < 0 ? 1 : -1));
    }

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource, SearchBox)) return;
        if (FindTagged(e.OriginalSource) is { Tag: int dot })
        {
            SetPage(dot);
            e.Handled = true;
            return;
        }
        pressPoint = e.GetPosition(this);
        pressedTile = FindTagged(e.OriginalSource) is { Tag: AppInfo } tile ? tile : null;
        if (pressedTile != null) pressedTile.Opacity = 0.6;
        dragging = false;
        dragOrigin = PagesShift.X;
        Root.CaptureMouse();
    }

    void OnDrag(object sender, MouseEventArgs e)
    {
        if (pressPoint is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        double dx = e.GetPosition(this).X - start.X;
        if (!dragging && Math.Abs(dx) > 10 && pageCount > 1)
        {
            dragging = true;
            if (pressedTile != null) pressedTile.Opacity = 1;
            PagesShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
        if (dragging) PagesShift.X = dragOrigin + dx;
    }

    void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (pressPoint is not Point start) return;
        Root.ReleaseMouseCapture();
        double dx = e.GetPosition(this).X - start.X;
        var tile = pressedTile;
        pressPoint = null;
        pressedTile = null;
        if (tile != null) tile.Opacity = 1;

        if (dragging)
        {
            dragging = false;
            SetPage(dx < -80 ? page + 1 : dx > 80 ? page - 1 : page);
        }
        else if (tile?.Tag is AppInfo app) Launch(app);
        else Dismiss(); // click on empty space closes, like macOS
    }

    static FrameworkElement? FindTagged(object source)
    {
        for (var d = source as DependencyObject; d != null; d = ParentOf(d))
            if (d is FrameworkElement { Tag: not null } fe) return fe;
        return null;
    }

    static bool IsWithin(object source, DependencyObject container)
    {
        for (var d = source as DependencyObject; d != null; d = ParentOf(d))
            if (d == container) return true;
        return false;
    }

    static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
}
