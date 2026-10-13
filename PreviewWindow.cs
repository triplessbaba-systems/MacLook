using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Live thumbnails of an app's windows above its bar icon, like hovering a Windows taskbar button.
/// The pictures are drawn by Windows (DWM thumbnails) straight into this window.
/// </summary>
internal sealed class PreviewWindow : Window
{
    const double ThumbW = 200, ThumbH = 120, CellW = ThumbW + 16, Pad = 6;
    const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    readonly StackPanel cells = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(Pad) };
    readonly List<IntPtr> thumbnails = new();
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    IntPtr hwnd;

    public DockEntry? Entry { get; private set; }

    public PreviewWindow()
    {
        Title = "MacLook Önizleme";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = false; // DWM thumbnails need a normal (non-layered) window
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "PreviewBg");
        SetResourceReference(FontFamilyProperty, "UiFont");
        SetResourceReference(ForegroundProperty, "PanelText");
        FontSize = 12;
        Content = cells;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd);
            int round = 2; // DWMWCP_ROUND: Windows 11 rounded corners
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
        MouseEnter += (_, _) => hideTimer.Stop();
        MouseLeave += (_, _) => HideSoon();
        hideTimer.Tick += (_, _) => HideNow();
    }

    /// <param name="anchor">The bar icon, in screen DIPs.</param>
    public void ShowFor(DockEntry entry, Rect anchor)
    {
        hideTimer.Stop();
        ClearThumbnails();
        Entry = entry;
        var windows = entry.Windows.Take(5).ToList();
        cells.Children.Clear();
        foreach (var w in windows) cells.Children.Add(MakeCell(entry, w));

        Width = windows.Count * CellW + 2 * Pad;
        Height = 24 + ThumbH + 16 + 2 * Pad;
        Left = Math.Clamp(anchor.X + anchor.Width / 2 - Width / 2, 4, SystemParameters.PrimaryScreenWidth - Width - 4);
        Top = anchor.Y - Height - 8;
        Show();
        UpdateLayout();
        AttachThumbnails(windows);
    }

    public void HideSoon()
    {
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void HideNow()
    {
        hideTimer.Stop();
        ClearThumbnails();
        Entry = null;
        Hide();
    }

    FrameworkElement MakeCell(DockEntry entry, AppWindow window)
    {
        var title = new TextBlock { Text = window.Title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var close = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Cursor = Cursors.Hand, Visibility = Visibility.Hidden, Margin = new Thickness(6, 0, 0, 0) };
        close.SetResourceReference(Border.BackgroundProperty, "TileOff");
        var x = new TextBlock { Text = "", FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        x.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        close.Child = x;
        var header = new DockPanel { Height = 22, Margin = new Thickness(2, 0, 2, 4) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        // DWM draws the live picture over this area; minimized windows have none, so show the app icon there.
        var picture = new Border { Width = ThumbW, Height = ThumbH, Tag = window.Handle };
        if (Native.IsIconic(window.Handle))
            picture.Child = new Image { Source = entry.Icon.Source, Width = 48, Height = 48, Opacity = 0.8 };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(picture);
        var cell = new Border { Width = CellW - 4, Margin = new Thickness(2, 0, 2, 0), Padding = new Thickness(6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = stack, Cursor = Cursors.Hand };
        cell.MouseEnter += (_, _) => { cell.SetResourceReference(Border.BackgroundProperty, "PanelHover"); close.Visibility = Visibility.Visible; };
        cell.MouseLeave += (_, _) => { cell.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            HideNow();
            WindowTracker.Activate(window.Handle);
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Native.PostMessage(window.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            HideNow();
        };
        return cell;
    }

    void AttachThumbnails(List<AppWindow> windows)
    {
        double scale = BitmapUtil.DpiScale(this);
        foreach (var cell in cells.Children.OfType<Border>())
        {
            var picture = (Border)((StackPanel)cell.Child).Children[1];
            var source = (IntPtr)picture.Tag;
            if (Native.IsIconic(source)) continue;
            if (Native.DwmRegisterThumbnail(hwnd, source, out var thumb) != 0) continue;
            thumbnails.Add(thumb);
            Native.DwmQueryThumbnailSourceSize(thumb, out var size);

            // Fit the window's picture inside the box, keeping its shape.
            var topLeft = picture.TranslatePoint(new Point(), this);
            double boxW = ThumbW * scale, boxH = ThumbH * scale;
            double fit = Math.Min(boxW / Math.Max(1, size.cx), boxH / Math.Max(1, size.cy));
            double w = size.cx * fit, h = size.cy * fit;
            double left = topLeft.X * scale + (boxW - w) / 2, top = topLeft.Y * scale + (boxH - h) / 2;
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT { Left = (int)left, Top = (int)top, Right = (int)(left + w), Bottom = (int)(top + h) },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbnails()
    {
        foreach (var thumb in thumbnails) Native.DwmUnregisterThumbnail(thumb);
        thumbnails.Clear();
    }
}
