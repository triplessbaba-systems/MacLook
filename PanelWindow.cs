using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Base for pop-up panels (Start menu, Control Center): frosted glass made from a blurred
/// snapshot of what's behind, slides in, and closes when you click anywhere else.
/// </summary>
public class PanelWindow : Window
{
    protected const double ShadowMargin = 30;
    Border backdrop = null!;
    TranslateTransform slide = null!;
    bool closing;
    int openMenus; // context menus opened from the panel shouldn't close it
    DateTime shownAt;

    public bool IsOpen => IsVisible && !closing;

    protected PanelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SourceInitialized += (_, _) => Native.MakeToolWindow(new WindowInteropHelper(this).Handle, noActivate: false);
        Deactivated += (_, _) =>
        {
            if (openMenus > 0 || !IsOpen) return;
            // Right after opening, focus can bounce back briefly (e.g. as the Windows key is released): take it back.
            if (DateTime.Now - shownAt < TimeSpan.FromMilliseconds(600))
            {
                Dispatcher.BeginInvoke(() => { WindowTracker.Activate(new WindowInteropHelper(this).Handle); Activate(); });
                return;
            }
            Dismiss();
        };
    }

    /// <summary>Call after InitializeComponent with the panel's backdrop border and slide transform.</summary>
    protected void InitPanel(Border backdropBorder, TranslateTransform slideTransform)
    {
        backdrop = backdropBorder;
        slide = slideTransform;
    }

    /// <param name="panel">Visible panel rectangle in screen DIPs (without the shadow margin).</param>
    protected void ShowPanel(Rect panel, bool fromBottom)
    {
        closing = false;
        Left = panel.X - ShadowMargin;
        Top = panel.Y - ShadowMargin;
        Width = panel.Width + 2 * ShadowMargin;
        Height = panel.Height + 2 * ShadowMargin;

        double scale = BitmapUtil.DpiScale(App.Dock);
        var px = new Int32Rect((int)(panel.X * scale), (int)(panel.Y * scale), (int)(panel.Width * scale), (int)(panel.Height * scale));
        backdrop.Background = new ImageBrush(BitmapUtil.BlurredScreen(px, 14)) { Stretch = Stretch.Fill };

        shownAt = DateTime.Now;
        Show();
        WindowTracker.Activate(new WindowInteropHelper(this).Handle);
        Activate();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromBottom ? 14 : -14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    public void Dismiss()
    {
        if (!IsOpen) return;
        closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (!closing) return;
            Hide();
            closing = false;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Opens a context menu from inside the panel without the panel closing underneath it.</summary>
    protected void OpenMenu(ContextMenu menu)
    {
        openMenus++;
        menu.Closed += (_, _) =>
        {
            openMenus--;
            // If the click that closed the menu went outside the panel, close the panel too.
            Dispatcher.BeginInvoke(() => { if (!IsActive) Dismiss(); }, DispatcherPriority.Background);
        };
        menu.IsOpen = true;
    }
}

/// <summary>macOS Control Center style slider: a thick rounded track that fills with white.</summary>
internal sealed class MacSlider : Grid
{
    const double Knob = 28;
    readonly Border fill;
    readonly TextBlock icon;
    double value;

    /// <summary>Raised while the user drags (0..1).</summary>
    public event Action<double>? ValueChanged;
    /// <summary>Raised when the user lets go.</summary>
    public event Action<double>? Committed;

    public MacSlider(string glyph)
    {
        Height = Knob;
        Background = Brushes.Transparent;
        Cursor = System.Windows.Input.Cursors.Hand;
        var track = new Border { CornerRadius = new CornerRadius(Knob / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "SliderTrack");
        fill = new Border { CornerRadius = new CornerRadius(Knob / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = Knob };
        fill.SetResourceReference(Border.BackgroundProperty, "SliderFill");
        fill.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Direction = 0, Opacity = 0.15 };
        icon = new TextBlock
        {
            Text = glyph,
            FontSize = 13,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "SliderIcon");
        Children.Add(track);
        Children.Add(fill);
        Children.Add(icon);

        MouseLeftButtonDown += (_, e) => { CaptureMouse(); Drag(e.GetPosition(this).X); };
        MouseMove += (_, e) => { if (IsMouseCaptured) Drag(e.GetPosition(this).X); };
        MouseLeftButtonUp += (_, _) =>
        {
            if (!IsMouseCaptured) return;
            ReleaseMouseCapture();
            Committed?.Invoke(value);
        };
        SizeChanged += (_, _) => Render();
    }

    public double Value
    {
        get => value;
        set { this.value = Math.Clamp(value, 0, 1); Render(); }
    }

    public string Glyph { set => icon.Text = value; }

    void Drag(double x)
    {
        Value = (x - Knob / 2) / Math.Max(1, ActualWidth - Knob);
        ValueChanged?.Invoke(value);
    }

    void Render()
    {
        if (ActualWidth > 0) fill.Width = Knob + (ActualWidth - Knob) * value;
    }
}
