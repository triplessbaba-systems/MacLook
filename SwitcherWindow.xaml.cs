using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacLook;

/// <summary>macOS-style app switcher for Alt+Tab: one big icon per app, most recently used first.</summary>
public partial class SwitcherWindow : Window
{
    sealed record SwitchApp(string Name, ImageSource? Icon, List<AppWindow> Windows);

    const double Outer = 40, Pad = 12, LabelHeight = 24, BottomPad = 8;
    // A quick Alt+Tab just flips to the previous app without flashing the panel.
    readonly DispatcherTimer showDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly List<Border> highlights = new();
    List<SwitchApp> apps = new();
    int selected;
    bool active;
    double cell;

    public SwitcherWindow()
    {
        InitializeComponent();
        showDelay.Tick += (_, _) =>
        {
            showDelay.Stop();
            ShowPanel();
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle);
    }

    public void Begin(bool reverse)
    {
        // Windows come back in z-order, so grouping keeps apps in most-recently-used order.
        apps = WindowTracker.GetWindows()
            .GroupBy(w => w.Key)
            .Select(g => g.ToList())
            .Select(list => new SwitchApp(WindowTracker.GetAppName(list[0].ExePath), IconLoader.Get(WindowTracker.GetIconPath(list[0])), list))
            .ToList();
        active = apps.Count > 0;
        if (!active) return;

        // Usually the first app is the one in front, so start on the second.
        var fg = Native.GetForegroundWindow();
        bool firstIsActive = apps[0].Windows.Any(w => w.Handle == fg);
        selected = reverse ? apps.Count - 1 : firstIsActive ? Math.Min(1, apps.Count - 1) : 0;
        showDelay.Start();
    }

    public void Move(int delta)
    {
        if (!active) return;
        selected = (selected + delta + apps.Count) % apps.Count;
        if (IsVisible) UpdateSelection();
    }

    public void Commit()
    {
        if (!active) return;
        var app = apps[selected];
        Finish();
        var fg = Native.GetForegroundWindow();
        if (!app.Windows.Any(w => w.Handle == fg)) WindowTracker.Activate(app.Windows[0].Handle);
    }

    public void Cancel()
    {
        if (active) Finish();
    }

    /// <summary>Q while switching: quit the selected app (like Cmd+Tab, Q).</summary>
    public void QuitSelected()
    {
        if (!active) return;
        foreach (var w in apps[selected].Windows) Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        apps.RemoveAt(selected);
        if (apps.Count == 0) { Finish(); return; }
        selected %= apps.Count;
        if (IsVisible) ShowPanel();
    }

    void Finish()
    {
        active = false;
        showDelay.Stop();
        Hide();
    }

    void ShowPanel()
    {
        if (!active) return;
        var screen = Screens.AtCursor().Bounds;
        double icon = Math.Clamp((screen.Width - 200) / apps.Count - 28, 36, 96);
        cell = icon + 28;

        Icons.Children.Clear();
        highlights.Clear();
        for (int i = 0; i < apps.Count; i++)
        {
            var highlight = new Border { CornerRadius = new CornerRadius(16), Width = cell - 4, Height = cell - 4, Visibility = Visibility.Hidden };
            highlight.SetResourceReference(Border.BackgroundProperty, "SwitcherSelect");
            var image = new Image { Source = apps[i].Icon, Width = icon, Height = icon };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var slot = new Grid { Width = cell, Height = cell, Background = Brushes.Transparent };
            slot.Children.Add(highlight);
            slot.Children.Add(image);
            // The mouse works too: hover selects, click switches.
            int index = i;
            slot.MouseEnter += (_, _) => { selected = index; UpdateSelection(); };
            slot.MouseLeftButtonUp += (_, _) => { selected = index; Commit(); };
            Icons.Children.Add(slot);
            highlights.Add(highlight);
        }

        double panelW = apps.Count * cell + 2 * Pad, panelH = Pad + cell + LabelHeight + BottomPad;
        bool wasVisible = IsVisible;
        Width = panelW + 2 * Outer;
        Height = panelH + 2 * Outer;
        Left = screen.Left + Math.Round((screen.Width - Width) / 2);
        Top = screen.Top + Math.Round((screen.Height - Height) / 2);

        // Real frosted glass: blur a snapshot of what's behind the panel (nothing moves while Alt is held).
        if (!wasVisible)
        {
            double scale = Screens.Scale;
            var rect = new Int32Rect((int)((Left + Outer) * scale), (int)((Top + Outer) * scale), (int)(panelW * scale), (int)(panelH * scale));
            Panel.Background = new ImageBrush(BitmapUtil.BlurredScreen(rect)) { Stretch = Stretch.Fill };
        }

        UpdateSelection();
        Show();
    }

    void UpdateSelection()
    {
        for (int i = 0; i < highlights.Count; i++)
            highlights[i].Visibility = i == selected ? Visibility.Visible : Visibility.Hidden;
        AppName.Text = apps[selected].Name;
        AppName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = selected * cell + cell / 2 - AppName.DesiredSize.Width / 2;
        Canvas.SetLeft(AppName, Math.Clamp(x, 0, Math.Max(0, apps.Count * cell - AppName.DesiredSize.Width)));
        Canvas.SetTop(AppName, 2);
    }
}
