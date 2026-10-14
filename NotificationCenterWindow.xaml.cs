using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacLook;

/// <summary>macOS-style Notification Center: big clock, calendar widget and the Windows notifications.</summary>
public partial class NotificationCenterWindow : PanelWindow
{
    const double PanelW = 360;
    // The calendar starts on Monday in both languages.
    static string[] WeekDayNames => L.IsEnglish ? ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] : ["Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz"];

    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime shownMonth = DateTime.Today;
    bool opening;
    string shownIds = "";
    int ticks;
    List<NotificationItem> shownItems = new();
    readonly HashSet<string> expandedApps = new();

    public NotificationCenterWindow()
    {
        InitializeComponent();
        InitPanel(BackdropBorder, SlideTransform);
        foreach (var name in WeekDayNames)
        {
            var label = new TextBlock { Text = name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center };
            label.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
            WeekDays.Children.Add(label);
        }
        PrevMonth.Click += (_, _) => { shownMonth = shownMonth.AddMonths(-1); BuildCalendar(); };
        NextMonth.Click += (_, _) => { shownMonth = shownMonth.AddMonths(1); BuildCalendar(); };
        TodayButton.Click += (_, _) => { shownMonth = DateTime.Today; BuildCalendar(); };
        ClearAll.Click += async (_, _) => { Notifications.ClearAll(); await BuildNotifications([]); };
        clockTimer.Tick += async (_, _) =>
        {
            if (!IsOpen) { clockTimer.Stop(); return; }
            UpdateClock();
            if (++ticks % 3 == 0) await RefreshNotificationsIfChanged();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
    }

    /// <param name="fromTop">true: opens under the menu bar (clock up there, Dock style); false: above the bottom bar.</param>
    public void Toggle(bool fromTop = false)
    {
        if (IsOpen) Dismiss();
        else _ = OpenAsync(fromTop);
    }

    async Task OpenAsync(bool fromTop)
    {
        if (opening) return;
        opening = true;
        try
        {
            App.StartMenu.Dismiss();
            App.ControlCenter.Dismiss();
            double panelH = await PrepareAsync();
            var screen = Screens.AtCursor().Bounds;
            double top = fromTop ? screen.Top + MenuBarWindow.BarHeight + 6 : screen.Bottom - DockWindow.ReservedHeight - 8 - panelH;
            ShowPanel(new Rect(screen.Right - 8 - PanelW, top, PanelW, panelH), fromBottom: !fromTop);
            NotificationScroll.ScrollToTop();
            clockTimer.Start();
        }
        catch (Exception ex) { App.Log("notification center failed: " + ex); }
        finally { opening = false; }
    }

    /// <summary>Fills in clock, calendar and notifications; returns the panel height (also used by --snapshot).</summary>
    internal async Task<double> PrepareAsync()
    {
        UpdateClock();
        shownMonth = DateTime.Today;
        BuildCalendar();
        await BuildNotifications();

        // Measure everything except the list, then give the list an explicit height so it can scroll.
        double available = Screens.AtCursor().Bounds.Height - MenuBarWindow.BarHeight - DockWindow.ReservedHeight - 16;
        NotificationScroll.Height = 0;
        Body.Measure(new Size(336, double.PositiveInfinity));
        double fixedPart = Body.DesiredSize.Height + 24;
        NotificationList.Measure(new Size(336, double.PositiveInfinity));
        NotificationScroll.Height = Math.Max(0, Math.Min(NotificationList.DesiredSize.Height, available - fixedPart));
        return Math.Min(available, fixedPart + NotificationScroll.Height);
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        BigTime.Text = now.ToString("HH:mm", L.Culture);
        LongDate.Text = now.ToString(L.IsEnglish ? "dddd, MMMM d, yyyy" : "dddd, d MMMM yyyy", L.Culture);
    }

    // ---------- calendar ----------

    void BuildCalendar()
    {
        MonthTitle.Text = shownMonth.ToString("MMMM yyyy", L.Culture);
        Days.Children.Clear();
        var first = new DateTime(shownMonth.Year, shownMonth.Month, 1);
        int offset = ((int)first.DayOfWeek + 6) % 7; // weeks start on Monday
        var day = first.AddDays(-offset);
        for (int i = 0; i < 42; i++, day = day.AddDays(1))
        {
            bool today = day == DateTime.Today;
            var text = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12.5,
                FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = day.Month == shownMonth.Month ? 1 : 0.3,
            };
            var cell = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 1, 0, 1), Child = text };
            if (today)
            {
                cell.Background = Theme.Brush("#FFFF3B30"); // macOS marks today in red
                text.Foreground = Brushes.White;
            }
            Days.Children.Add(cell);
        }
    }

    // ---------- notifications ----------

    /// <summary>While open, pick up notifications that arrive or get dismissed elsewhere.</summary>
    async Task RefreshNotificationsIfChanged()
    {
        var items = await Notifications.GetAsync();
        if (items == null || Ids(items) == shownIds) return;
        await BuildNotifications(items);
    }

    static string Ids(List<NotificationItem> items) => string.Join(",", items.Select(i => i.Id));

    async Task BuildNotifications(List<NotificationItem>? items = null)
    {
        items ??= await Notifications.GetAsync();
        shownIds = items == null ? "" : Ids(items);
        if (items == null)
        {
            NotificationList.Children.Clear();
            NoNotifications.Text = L.T("Bildirimlere erişilemiyor");
            NoNotifications.Visibility = Visibility.Visible;
            ClearAll.Visibility = Visibility.Collapsed;
            return;
        }
        shownItems = items.Take(40).ToList();
        RenderNotifications();
    }

    /// <summary>One stack per app, like macOS: the newest card on top, the rest behind it until the stack is opened.</summary>
    void RenderNotifications()
    {
        NotificationList.Children.Clear();
        foreach (var group in shownItems.GroupBy(i => i.AppName)) // keeps the newest-first order of first appearance
        {
            var list = group.ToList();
            if (list.Count == 1)
            {
                NotificationList.Children.Add(MakeCard(list[0], null, () => Remove([list[0]])));
            }
            else if (expandedApps.Contains(group.Key))
            {
                NotificationList.Children.Add(StackHeader(group.Key, list));
                foreach (var item in list) NotificationList.Children.Add(MakeCard(item, null, () => Remove([item])));
            }
            else
            {
                NotificationList.Children.Add(MakeStack(group.Key, list));
            }
        }
        NoNotifications.Text = L.T("Yeni bildirim yok");
        NoNotifications.Visibility = shownItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearAll.Visibility = shownItems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    void Remove(List<NotificationItem> items)
    {
        foreach (var item in items) Notifications.Remove(item.Id);
        shownItems = shownItems.Except(items).ToList();
        shownIds = Ids(shownItems);
        RenderNotifications();
    }

    /// <summary>Collapsed stack: the newest card with the edges of the others showing below it.</summary>
    FrameworkElement MakeStack(string app, List<NotificationItem> list)
    {
        void Expand() { expandedApps.Add(app); RenderNotifications(); }
        var top = MakeCard(list[0], Expand, () => Remove(list), string.Format(L.T("+{0} bildirim daha"), list.Count - 1));
        top.Margin = new Thickness(0);
        var stack = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        for (int layer = Math.Min(2, list.Count - 1); layer >= 1; layer--)
        {
            var edge = new Border
            {
                Style = (Style)Application.Current.FindResource("Tile"),
                Margin = new Thickness(layer * 8, 0, layer * 8, -layer * 6),
                Opacity = 1 - layer * 0.25,
            };
            stack.Children.Add(edge);
        }
        stack.Children.Add(top);
        return stack;
    }

    FrameworkElement StackHeader(string app, List<NotificationItem> list)
    {
        var title = new TextBlock { Text = app, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var collapse = new Button { Style = (Style)FindResource("PillButton"), Padding = new Thickness(8, 2, 8, 2), Content = new TextBlock { Text = "Daralt", FontSize = 12 } };
        collapse.Click += (_, _) => { expandedApps.Remove(app); RenderNotifications(); };
        var clear = new Button { Style = (Style)FindResource("PillButton"), Padding = new Thickness(8, 2, 8, 2), Content = new TextBlock { Text = "Temizle", FontSize = 12 } };
        clear.Click += (_, _) => Remove(list);
        var row = new DockPanel { Margin = new Thickness(4, 2, 0, 6) };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(collapse, Dock.Right);
        row.Children.Add(clear);
        row.Children.Add(collapse);
        row.Children.Add(title);
        return row;
    }

    /// <param name="onClick">null: open the app that sent it.</param>
    FrameworkElement MakeCard(NotificationItem item, Action? onClick, Action onClose, string? footer = null)
    {
        var icon = new Image { Source = item.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var app = new TextBlock { Text = item.AppName, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        app.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        var time = new TextBlock { Text = UserInfo.Ago(item.Time), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        time.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");

        // Close button appears on hover, like macOS.
        var close = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Margin = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed, Cursor = Cursors.Hand };
        close.SetResourceReference(Border.BackgroundProperty, "TileOff");
        var x = new TextBlock { Text = "", FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        x.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        close.Child = x;

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(time);
        header.Children.Add(icon);
        header.Children.Add(app);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.Body.Length > 0)
            body.Children.Add(new TextBlock { Text = item.Body, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 52, TextTrimming = TextTrimming.CharacterEllipsis });
        if (footer != null)
        {
            var more = new TextBlock { Text = footer, FontSize = 11.5, Margin = new Thickness(0, 6, 0, 0) };
            more.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
            body.Children.Add(more);
        }

        var card = new Border { Style = (Style)Application.Current.FindResource("Tile"), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8), Child = body, Cursor = Cursors.Hand };
        card.MouseEnter += (_, _) => close.Visibility = Visibility.Visible;
        card.MouseLeave += (_, _) => close.Visibility = Visibility.Collapsed;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            onClose();
        };
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (onClick != null)
            {
                onClick();
                return;
            }
            Dismiss();
            if (item.AppId.Length > 0) Ui.Open(@"shell:AppsFolder\" + item.AppId);
        };
        return card;
    }
}
