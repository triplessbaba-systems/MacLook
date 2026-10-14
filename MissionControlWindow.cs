using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacLook;

/// <summary>
/// Mission Control: every window on the screen slides from where it is into a tidy grid of live
/// pictures (DWM thumbnails). Click one to switch to it; click empty space or press Esc to go back.
/// </summary>
internal sealed class MissionControlWindow : Window
{
    const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    const double AnimationMs = 260, LabelHeight = 30;

    sealed class Item
    {
        public required AppWindow Window;
        public IntPtr Thumbnail;
        public Rect From, To;      // physical pixels, relative to this window
        public Rect Slot;          // DIPs, for hit testing and labels
        public FrameworkElement? Label;
    }

    readonly Image backdrop = new() { Stretch = Stretch.Fill };
    readonly Canvas overlay = new() { Background = Brushes.Transparent };
    readonly Border highlight = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(3), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly List<Item> items = new();
    ScreenInfo screen = null!;
    IntPtr hwnd;
    DateTime animationStart;
    bool closing, animating;
    Item? hot;

    public bool IsOpen => IsVisible && !closing;

    public MissionControlWindow()
    {
        Title = "MacLook Mission Control";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false; // DWM thumbnails need a normal window
        Background = Brushes.Black;
        highlight.SetResourceReference(Border.BorderBrushProperty, "Accent");
        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(new Border { Background = Theme.Brush("#40000000") });
        overlay.Children.Add(highlight);
        root.Children.Add(overlay);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd, noActivate: false);
        };
        Deactivated += (_, _) => Close(null);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(null); };
        overlay.MouseMove += (_, e) => Hover(e.GetPosition(overlay));
        overlay.MouseLeftButtonUp += (_, e) => Close(ItemAt(e.GetPosition(overlay)));
    }

    public void Toggle()
    {
        if (IsOpen) Close(null);
        else Open();
    }

    public void Open()
    {
        if (IsVisible) return;
        App.DismissPanels();
        screen = Screens.AtCursor();
        var windows = WindowTracker.GetWindows()
            .Where(w => !Native.IsIconic(w.Handle) && Screens.MonitorOf(w.Handle) == screen.Handle)
            .ToList();

        Left = screen.Bounds.Left;
        Top = screen.Bounds.Top;
        Width = screen.Bounds.Width;
        Height = screen.Bounds.Height;
        backdrop.Source = Wallpaper.For(screen, strong: true);
        closing = false;
        Show();
        WindowTracker.Activate(hwnd);
        Activate();

        items.Clear();
        foreach (var w in windows)
        {
            if (Native.DwmRegisterThumbnail(hwnd, w.Handle, out var thumb) != 0) continue;
            items.Add(new Item { Window = w, Thumbnail = thumb, From = WindowRect(w.Handle) });
        }
        LayOut();
        animationStart = DateTime.Now;
        StartAnimation();
    }

    /// <param name="target">Window to switch to, or null to just go back.</param>
    void Close(Item? target)
    {
        if (!IsVisible || closing) return;
        closing = true;
        highlight.Visibility = Visibility.Collapsed;
        foreach (var item in items) if (item.Label != null) item.Label.Visibility = Visibility.Collapsed;
        if (target != null) WindowTracker.Activate(target.Window.Handle);
        // Play the layout backwards: pictures fly back to their windows.
        foreach (var item in items) (item.From, item.To) = (item.To, item.From);
        animationStart = DateTime.Now;
        StartAnimation();
    }

    // ---------- layout ----------

    /// <summary>Grid with as many columns as fit best, each window keeping its proportions.</summary>
    void LayOut()
    {
        foreach (var child in overlay.Children.OfType<FrameworkElement>().Where(c => c != highlight).ToList()) overlay.Children.Remove(child);
        if (items.Count == 0) return;
        double scale = Screens.Scale;
        double top = MenuBarWindow.BarHeight + 40, bottom = DockWindow.ReservedHeight + 40, side = 60;
        double areaW = Width - 2 * side, areaH = Height - top - bottom;
        int columns = (int)Math.Ceiling(Math.Sqrt(items.Count * areaW / areaH / 1.6));
        columns = Math.Clamp(columns, 1, items.Count);
        int rows = (int)Math.Ceiling(items.Count / (double)columns);
        double cellW = areaW / columns, cellH = areaH / rows;

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            int row = i / columns, col = i % columns;
            int inRow = Math.Min(columns, items.Count - row * columns);
            double rowOffset = (columns - inRow) * cellW / 2; // center a short last row
            Native.DwmQueryThumbnailSourceSize(item.Thumbnail, out var size);
            double boxW = cellW - 40, boxH = cellH - 40 - LabelHeight;
            double fit = Math.Min(1 / scale, Math.Min(boxW / Math.Max(1, size.cx), boxH / Math.Max(1, size.cy))); // never enlarge
            double w = size.cx * fit, h = size.cy * fit;
            double x = side + rowOffset + col * cellW + (cellW - w) / 2;
            double y = top + row * cellH + (cellH - LabelHeight - h) / 2;
            item.Slot = new Rect(x, y, w, h);
            item.To = new Rect(x * scale, y * scale, w * scale, h * scale);
            item.Label = MakeLabel(item);
            Canvas.SetLeft(item.Label, x);
            Canvas.SetTop(item.Label, y + h + 8);
            item.Label.Width = w;
            item.Label.Visibility = Visibility.Collapsed;
            overlay.Children.Add(item.Label);
        }
    }

    static FrameworkElement MakeLabel(Item item)
    {
        var icon = new Image { Source = IconLoader.Get(WindowTracker.GetIconPath(item.Window), 64), Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0) };
        var title = new TextBlock { Text = item.Window.Title, Foreground = Brushes.White, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(title);
        return new Border
        {
            Child = row,
            Padding = new Thickness(8, 3, 8, 4),
            CornerRadius = new CornerRadius(6),
            Background = Theme.Brush("#73000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    Rect WindowRect(IntPtr window)
    {
        if (Native.DwmGetWindowAttribute(window, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            return Rect.Empty;
        var px = screen.BoundsPx;
        return new Rect(r.Left - px.Left, r.Top - px.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    // ---------- animation ----------

    void StartAnimation()
    {
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += OnFrame;
        OnFrame(null, EventArgs.Empty);
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double t = Math.Clamp((DateTime.Now - animationStart).TotalMilliseconds / AnimationMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3); // ease out
        foreach (var item in items)
        {
            var from = item.From.IsEmpty ? item.To : item.From;
            var r = new Rect(
                from.X + (item.To.X - from.X) * eased, from.Y + (item.To.Y - from.Y) * eased,
                from.Width + (item.To.Width - from.Width) * eased, from.Height + (item.To.Height - from.Height) * eased);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT { Left = (int)r.X, Top = (int)r.Y, Right = (int)r.Right, Bottom = (int)r.Bottom },
                opacity = 255,
                fVisible = true,
            };
            Native.DwmUpdateThumbnailProperties(item.Thumbnail, ref props);
        }
        if (t < 1) return;

        CompositionTarget.Rendering -= OnFrame;
        animating = false;
        if (closing)
        {
            foreach (var item in items) Native.DwmUnregisterThumbnail(item.Thumbnail);
            items.Clear();
            Hide();
            closing = false;
            App.TrimMemorySoon();
        }
        else
            foreach (var item in items) if (item.Label != null) item.Label.Visibility = Visibility.Visible;
    }

    // ---------- mouse ----------

    Item? ItemAt(Point p) => items.FirstOrDefault(i => i.Slot.Contains(p));

    void Hover(Point p)
    {
        if (animating) return;
        var item = ItemAt(p);
        if (item == hot) return;
        hot = item;
        if (item == null)
        {
            highlight.Visibility = Visibility.Collapsed;
            return;
        }
        // The frame is drawn under the picture, a little larger, so it shows as an outline.
        highlight.Width = item.Slot.Width + 10;
        highlight.Height = item.Slot.Height + 10;
        Canvas.SetLeft(highlight, item.Slot.X - 5);
        Canvas.SetTop(highlight, item.Slot.Y - 5);
        highlight.Visibility = Visibility.Visible;
    }
}
