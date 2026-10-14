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

/// <summary>
/// Full-screen grid of every installed app. Icons can be dragged to a new place,
/// or onto another icon to make a folder, like Launchpad on macOS.
/// </summary>
public partial class LaunchpadWindow : Window
{
    const int Columns = 7, Rows = 5, PerPage = Columns * Rows, GridTop = 120;
    static readonly CompareInfo Compare = new CultureInfo("tr-TR").CompareInfo;

    abstract record Item(string Key);
    sealed record AppItem(AppInfo App) : Item(App.Id);
    sealed record FolderItem(LaunchpadFolder Folder, List<AppInfo> Apps) : Item("folder:" + Folder.Id);

    readonly List<FrameworkElement> allPages = new();
    readonly List<UniformGrid> pageGrids = new();
    List<Item> items = new();
    double cellW, cellH, iconSize;
    List<AppInfo> results = new();
    int page, pageCount;
    bool closing, catalogDirty = true;
    int openMenus;
    DateTime lastWheel, lastCatalogRefresh = DateTime.Now;

    // Mouse: press on empty space and move to swipe pages; press on an icon and move to drag it.
    Point? pressPoint;
    FrameworkElement? pressedTile;
    bool swiping;
    double swipeOrigin;

    // Dragging an icon
    Item? dragItem;
    FolderItem? dragFromFolder;
    FrameworkElement? ghost;
    FrameworkElement? mergeTile;
    readonly Rectangle caret = new() { Width = 3, Fill = Brushes.White, RadiusX = 1.5, RadiusY = 1.5, Opacity = 0.9 };
    int dropIndex = -1;
    Item? mergeWith;
    DateTime edgeSince;
    int edgeDirection;

    FolderItem? openFolder;

    public bool IsOpen => IsVisible && !closing;

    public LaunchpadWindow()
    {
        InitializeComponent();
        FitTo(Screens.Primary);

        AppCatalog.Updated += () =>
        {
            catalogDirty = true;
            if (IsOpen && Search.Text.Length == 0 && dragItem == null) ShowAllPages();
        };
        Search.TextChanged += (_, _) => OnSearchChanged();
        PreviewKeyDown += OnKey;
        Root.PreviewMouseLeftButtonDown += OnPress;
        Root.PreviewMouseMove += OnMove;
        Root.PreviewMouseLeftButtonUp += OnRelease;
        Root.PreviewMouseRightButtonUp += OnRightClick;
        Root.MouseWheel += OnWheel;
        FolderName.KeyDown += (_, e) => { if (e.Key == Key.Enter) { RenameFolder(); Keyboard.Focus(Search); } };
        FolderName.LostKeyboardFocus += (_, _) => RenameFolder();
        Deactivated += (_, _) => { if (openMenus == 0) Dismiss(); };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: false);
    }

    /// <summary>Covers one screen; pages are rebuilt when the size differs from last time.</summary>
    void FitTo(ScreenInfo screen)
    {
        var b = screen.Bounds;
        if (b.Width != Width || b.Height != Height) catalogDirty = true;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        cellW = Math.Min(Width * 0.82 / Columns, 170);
        cellH = Math.Min((Height - GridTop - 180) / Rows, 160);
        iconSize = Math.Round(Math.Min(cellW, cellH) * 0.56);
    }

    public void Toggle()
    {
        if (IsOpen) Dismiss();
        else Open();
    }

    /// <summary>--snapshot: lays out the first page without showing the window.</summary>
    internal void PrepareSnapshot(ScreenInfo screen)
    {
        FitTo(screen);
        catalogDirty = true;
        Backdrop.Source = Wallpaper.For(screen, strong: true);
        ShowAllPages();
        SetPage(0, animate: false);
    }

    public void Open()
    {
        if (IsOpen) return;
        closing = false;
        var screen = Screens.AtCursor();
        FitTo(screen);
        Backdrop.Source = Wallpaper.For(screen, strong: true);
        Search.Text = "";
        CloseFolder();
        ShowAllPages();
        SetPage(0, animate: false);

        Show();
        WindowTracker.Activate(new WindowInteropHelper(this).Handle);
        Activate();
        Search.Focus();
        foreach (var bar in App.Docks) bar.BringToFront();

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
        CancelDrag();
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!closing) return;
            Hide();
            closing = false;
            App.TrimMemorySoon();
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

    // ---------- order and folders ----------

    /// <summary>Saved order first (folders included), then any apps not placed yet, alphabetically.</summary>
    void BuildItems()
    {
        var config = App.Config;
        var byId = AppCatalog.Apps.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
        var inFolders = config.LaunchpadFolders.SelectMany(f => f.Apps).ToHashSet();
        var result = new List<Item>();
        var placed = new HashSet<string>();

        FolderItem? AsItem(LaunchpadFolder folder)
        {
            var apps = folder.Apps.Select(id => byId.GetValueOrDefault(id)).OfType<AppInfo>().ToList();
            return apps.Count > 0 ? new FolderItem(folder, apps) : null;
        }

        foreach (var key in config.LaunchpadOrder ?? [])
        {
            if (!placed.Add(key)) continue;
            if (key.StartsWith("folder:"))
            {
                if (config.LaunchpadFolders.FirstOrDefault(f => "folder:" + f.Id == key) is { } folder && AsItem(folder) is { } item) result.Add(item);
            }
            else if (byId.TryGetValue(key, out var app) && !inFolders.Contains(key)) result.Add(new AppItem(app));
        }
        foreach (var folder in config.LaunchpadFolders)
            if (placed.Add("folder:" + folder.Id) && AsItem(folder) is { } item) result.Add(item);
        foreach (var app in AppCatalog.Apps)
            if (!inFolders.Contains(app.Id) && placed.Add(app.Id)) result.Add(new AppItem(app));
        items = result;
    }

    void Save()
    {
        var config = App.Config;
        // A folder left with one app (or none) goes away; its last app takes its place.
        for (int i = items.Count - 1; i >= 0; i--)
            if (items[i] is FolderItem { Apps.Count: <= 1 } small)
            {
                config.LaunchpadFolders.Remove(small.Folder);
                items.RemoveAt(i);
                if (small.Apps.Count == 1) items.Insert(i, new AppItem(small.Apps[0]));
            }
        foreach (var folder in items.OfType<FolderItem>()) folder.Folder.Apps = folder.Apps.Select(a => a.Id).ToList();
        config.LaunchpadOrder = items.Select(i => i.Key).ToList();
        config.Save();
        catalogDirty = true;
    }

    // ---------- pages ----------

    void ShowAllPages()
    {
        if (catalogDirty)
        {
            BuildItems();
            allPages.Clear();
            pageGrids.Clear();
            for (int i = 0; i < Math.Max(1, items.Count); i += PerPage)
                allPages.Add(MakePage(items.Skip(i).Take(PerPage)));
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

    FrameworkElement MakePage(IEnumerable<Item> pageItems)
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
        foreach (var item in pageItems) grid.Children.Add(MakeTile(item));
        pageGrids.Add(grid);
        var pageHost = new Grid { Width = Width, Height = Height };
        pageHost.Children.Add(grid);
        return pageHost;
    }

    FrameworkElement MakeTile(Item item)
    {
        FrameworkElement picture;
        string name;
        if (item is FolderItem folder)
        {
            // Folder icon: a frosted square with up to nine tiny app icons.
            var minis = new UniformGrid { Columns = 3, Rows = 3, Margin = new Thickness(iconSize * 0.1) };
            foreach (var app in folder.Apps.Take(9))
            {
                var mini = new Image { Source = app.Icon, Margin = new Thickness(iconSize * 0.025) };
                RenderOptions.SetBitmapScalingMode(mini, BitmapScalingMode.HighQuality);
                minis.Children.Add(mini);
            }
            picture = new Border
            {
                Width = iconSize,
                Height = iconSize,
                CornerRadius = new CornerRadius(iconSize * 0.24),
                Background = Theme.Brush("#47FFFFFF"),
                BorderBrush = Theme.Brush("#33FFFFFF"),
                BorderThickness = new Thickness(1),
                Child = minis,
            };
            name = folder.Folder.Name;
        }
        else
        {
            var app = ((AppItem)item).App;
            var image = new Image { Source = app.Icon, Width = iconSize, Height = iconSize };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            picture = image;
            name = app.Name;
        }
        var label = new TextBlock
        {
            Text = name,
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
            Tag = item,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.4),
            RenderTransform = new ScaleTransform(),
        };
        tile.Children.Add(picture);
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

    // ---------- folders ----------

    void OpenFolder(FolderItem folder)
    {
        openFolder = folder;
        FolderName.Text = folder.Folder.Name;
        FolderGrid.Children.Clear();
        FolderGrid.Columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(folder.Apps.Count)), 3, 5);
        foreach (var app in folder.Apps)
        {
            var tile = MakeTile(new AppItem(app));
            tile.Width = cellW;
            tile.Height = cellH;
            FolderGrid.Children.Add(tile);
        }
        FolderOverlay.Visibility = Visibility.Visible;
        var grow = new DoubleAnimation(0.85, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var scale = new ScaleTransform();
        FolderPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        FolderPanel.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        FolderOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    void CloseFolder()
    {
        if (openFolder == null) return;
        RenameFolder();
        openFolder = null;
        FolderOverlay.Visibility = Visibility.Collapsed;
        Keyboard.Focus(Search);
    }

    void RenameFolder()
    {
        if (openFolder == null) return;
        var name = FolderName.Text.Trim();
        if (name.Length == 0 || name == openFolder.Folder.Name) return;
        openFolder.Folder.Name = name;
        Save();
        ShowAllPages();
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
        CloseFolder();
        results = AppCatalog.Apps
            .Where(a => Compare.IndexOf(a.Name, query, CompareOptions.IgnoreCase) >= 0)
            .OrderBy(a => Compare.IsPrefix(a.Name, query, CompareOptions.IgnoreCase) ? 0 : 1)
            .Take(PerPage)
            .ToList();
        Pages.Children.Clear();
        var resultPage = MakePage(results.Select(a => (Item)new AppItem(a)));
        pageGrids.RemoveAt(pageGrids.Count - 1); // not one of the real pages
        Pages.Children.Add(resultPage);
        pageCount = 1;
        Dots.Visibility = Visibility.Collapsed;
        SetPage(0, animate: false);
    }

    bool Searching => Search.Text.Trim().Length > 0;

    // ---------- keyboard and wheel ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource == FolderName) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (dragItem != null) CancelDrag();
                else if (openFolder != null) CloseFolder();
                else if (Search.Text.Length > 0) Search.Text = "";
                else Dismiss();
                e.Handled = true;
                break;
            case Key.Enter:
                if (Searching && results.Count > 0) Launch(results[0]);
                e.Handled = true;
                break;
            case Key.Left or Key.Right when !Searching && openFolder == null:
                SetPage(page + (e.Key == Key.Right ? 1 : -1));
                e.Handled = true;
                break;
        }
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Searching || openFolder != null || (DateTime.Now - lastWheel).TotalMilliseconds < 350) return;
        lastWheel = DateTime.Now;
        SetPage(page + (e.Delta < 0 ? 1 : -1));
    }

    // ---------- mouse ----------

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (IsWithin(e.OriginalSource, SearchBox) || IsWithin(e.OriginalSource, FolderName)) return;
        if (FindTagged(e.OriginalSource) is { Tag: int dot })
        {
            SetPage(dot);
            e.Handled = true;
            return;
        }
        pressPoint = e.GetPosition(this);
        pressedTile = FindTagged(e.OriginalSource) is { Tag: Item } tile ? tile : null;
        if (pressedTile != null) pressedTile.Opacity = 0.6;
        swiping = false;
        swipeOrigin = PagesShift.X;
        Root.CaptureMouse();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (pressPoint is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (dragItem != null)
        {
            DragTo(pos);
            return;
        }
        if (swiping)
        {
            PagesShift.X = swipeOrigin + pos.X - start.X;
            return;
        }
        if ((pos - start).Length < 10) return;
        if (pressedTile?.Tag is Item item && !Searching) StartDrag(item, pressedTile, pos);
        else if (pressedTile == null && pageCount > 1 && openFolder == null)
        {
            swiping = true;
            PagesShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }

    void OnRelease(object sender, MouseButtonEventArgs e)
    {
        if (pressPoint is not Point start) return;
        Root.ReleaseMouseCapture();
        var pos = e.GetPosition(this);
        var tile = pressedTile;
        pressPoint = null;
        pressedTile = null;
        if (tile != null) tile.Opacity = 1;

        if (dragItem != null)
        {
            DropDragged(pos);
            return;
        }
        if (swiping)
        {
            swiping = false;
            double dx = pos.X - start.X;
            SetPage(dx < -80 ? page + 1 : dx > 80 ? page - 1 : page);
            return;
        }
        switch (tile?.Tag)
        {
            case AppItem app: Launch(app.App); break;
            case FolderItem folder: OpenFolder(folder); break;
            default:
                // Click on empty space: close the open folder, otherwise Launchpad itself (like macOS).
                if (openFolder != null) { if (!IsWithin(e.OriginalSource, FolderPanel)) CloseFolder(); }
                else Dismiss();
                break;
        }
    }

    void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindTagged(e.OriginalSource)?.Tag is not Item item || Searching) return;
        e.Handled = true;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        if (item is AppItem app)
        {
            menu.Items.Add(Ui.Item("Aç", () => Launch(app.App)));
            if (openFolder != null)
            {
                var folder = openFolder;
                menu.Items.Add(Ui.Item("Klasörden Çıkar", () =>
                {
                    folder.Apps.Remove(app.App);
                    items.Insert(items.IndexOf(folder) + 1, app);
                    Save();
                    CloseFolder();
                    ShowAllPages();
                }));
            }
        }
        else if (item is FolderItem folder)
        {
            menu.Items.Add(Ui.Item("Aç", () => OpenFolder(folder)));
            menu.Items.Add(Ui.Item("Klasörü Dağıt", () =>
            {
                int at = items.IndexOf(folder);
                items.RemoveAt(at);
                items.InsertRange(at, folder.Apps.Select(a => (Item)new AppItem(a)));
                App.Config.LaunchpadFolders.Remove(folder.Folder);
                Save();
                ShowAllPages();
            }));
        }
        openMenus++;
        menu.Closed += (_, _) => openMenus--;
        menu.IsOpen = true;
    }

    // ---------- drag and drop ----------

    void StartDrag(Item item, FrameworkElement tile, Point pos)
    {
        dragItem = item;
        dragFromFolder = openFolder != null && item is AppItem a && openFolder.Apps.Contains(a.App) ? openFolder : null;
        tile.Opacity = 0.25; // the empty spot it came from
        // A copy of the icon follows the mouse.
        ghost = MakeTile(item);
        ghost.Width = cellW;
        ghost.Height = cellH;
        ghost.Opacity = 0.95;
        DragLayer.Children.Add(ghost);
        DragTo(pos);
    }

    void DragTo(Point pos)
    {
        if (ghost == null || dragItem == null) return;
        Canvas.SetLeft(ghost, pos.X - cellW / 2);
        Canvas.SetTop(ghost, pos.Y - cellH * 0.4);

        // An icon dragged out of an open folder leaves the folder.
        if (dragFromFolder != null && openFolder != null && !IsOver(FolderPanel, pos))
            FolderOverlay.Visibility = Visibility.Collapsed;
        if (FolderOverlay.Visibility == Visibility.Visible) { ClearTargets(); return; }

        // Holding at the left or right edge turns the page.
        int direction = pos.X < 70 ? -1 : pos.X > Width - 70 ? 1 : 0;
        if (direction != edgeDirection) { edgeDirection = direction; edgeSince = DateTime.Now; }
        else if (direction != 0 && DateTime.Now - edgeSince > TimeSpan.FromMilliseconds(650))
        {
            int next = page + direction;
            if (next >= 0 && next <= pageCount) // one page past the end makes room for a new page
            {
                if (next == pageCount) { allPages.Add(MakePage([])); Pages.Children.Add(allPages[^1]); pageCount++; }
                SetPage(next);
            }
            edgeSince = DateTime.Now;
        }
        FindTarget(pos);
    }

    /// <summary>Over the middle of another icon: make / fill a folder. Elsewhere: put it in that slot.</summary>
    void FindTarget(Point pos)
    {
        ClearTargets();
        if (page >= pageGrids.Count) { dropIndex = items.Count; return; }
        var grid = pageGrids[page];
        var origin = grid.TranslatePoint(new Point(), this);
        double x = pos.X - origin.X, y = pos.Y - origin.Y;
        int col = (int)Math.Clamp(Math.Floor(x / cellW), 0, Columns - 1);
        int row = (int)Math.Clamp(Math.Floor(y / cellH), 0, Rows - 1);
        int slot = row * Columns + col;
        dropIndex = Math.Min(page * PerPage + slot, items.Count);

        double centerX = (col + 0.5) * cellW, centerY = row * cellH + cellH * 0.42;
        bool overCenter = Math.Abs(x - centerX) < iconSize * 0.38 && Math.Abs(y - centerY) < iconSize * 0.38;
        if (overCenter && slot < grid.Children.Count && grid.Children[slot] is FrameworkElement tile
            && tile.Tag is Item target && target != dragItem && dragItem is AppItem)
        {
            mergeWith = target;
            mergeTile = tile;
            var grow = new DoubleAnimation(1.18, TimeSpan.FromMilliseconds(150));
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            ((ScaleTransform)tile.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            return;
        }

        // A thin line shows where the icon will land.
        caret.Height = iconSize;
        Canvas.SetLeft(caret, origin.X + col * cellW + 2);
        Canvas.SetTop(caret, origin.Y + row * cellH + (cellH - iconSize) * 0.35);
        DragLayer.Children.Add(caret);
    }

    void ClearTargets()
    {
        DragLayer.Children.Remove(caret);
        if (mergeTile?.RenderTransform is ScaleTransform scale)
        {
            var back = new DoubleAnimation(1, TimeSpan.FromMilliseconds(120));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, back);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, back);
        }
        mergeTile = null;
        mergeWith = null;
    }

    void DropDragged(Point pos)
    {
        var item = dragItem!;
        var fromFolder = dragFromFolder;
        bool stillInFolder = fromFolder != null && FolderOverlay.Visibility == Visibility.Visible;
        var target = mergeWith;
        int index = dropIndex;
        CancelDrag();

        if (stillInFolder)
        {
            // Rearranged inside the open folder.
            var app = ((AppItem)item).App;
            var cell = FolderGrid.Children.OfType<FrameworkElement>().FirstOrDefault(t => IsOver(t, pos));
            if (cell?.Tag is AppItem over && over.App != app)
            {
                fromFolder!.Apps.Remove(app);
                fromFolder.Apps.Insert(fromFolder.Apps.IndexOf(over.App), app);
                Save();
                OpenFolder(fromFolder);
                ShowAllPages();
            }
            return;
        }

        // Take the item out of where it was.
        if (fromFolder != null) { fromFolder.Apps.Remove(((AppItem)item).App); openFolder = null; FolderOverlay.Visibility = Visibility.Collapsed; }
        else
        {
            int old = items.IndexOf(item);
            if (old >= 0) { items.RemoveAt(old); if (old < index) index--; }
        }

        if (target is FolderItem folder && item is AppItem a)
            folder.Apps.Add(a.App);
        else if (target is AppItem other && item is AppItem dropped)
        {
            // Two apps make a folder, in the place of the one dropped onto.
            var config = App.Config;
            var newFolder = new LaunchpadFolder();
            config.LaunchpadFolders.Add(newFolder);
            int at = items.IndexOf(other);
            items[at] = new FolderItem(newFolder, [other.App, dropped.App]);
        }
        else
            items.Insert(Math.Clamp(index, 0, items.Count), item);

        Save();
        ShowAllPages();
    }

    void CancelDrag()
    {
        ClearTargets();
        if (ghost != null) DragLayer.Children.Remove(ghost);
        ghost = null;
        dragItem = null;
        dragFromFolder = null;
        edgeDirection = 0;
        foreach (var tile in pageGrids.SelectMany(g => g.Children.OfType<FrameworkElement>()).Concat(FolderGrid.Children.OfType<FrameworkElement>()))
            tile.Opacity = 1;
    }

    // ---------- helpers ----------

    bool IsOver(FrameworkElement element, Point windowPoint)
    {
        if (!element.IsVisible) return false;
        var p = TranslatePoint(windowPoint, element);
        return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
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
