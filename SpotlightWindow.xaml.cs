using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacLook;

/// <summary>Spotlight: Alt+Space search bar for apps, files, settings, quick math and the web.</summary>
public partial class SpotlightWindow : PanelWindow
{
    const double PanelW = 680, RowH = 40;

    readonly DispatcherTimer fileDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly List<(Border Row, List<(TextBlock Text, string? Brush)> Texts)> rows = new();
    List<SpotlightResult> results = new(), localResults = new(), fileResults = new();
    CancellationTokenSource? fileSearch;
    int selected = -1;
    double maxPanelH;

    public SpotlightWindow()
    {
        InitializeComponent();
        InitPanel(BackdropBorder, SlideTransform);
        Search.TextChanged += (_, _) => OnQuery();
        PreviewKeyDown += OnKey;
        fileDelay.Tick += async (_, _) =>
        {
            fileDelay.Stop();
            await SearchFilesAsync();
        };
        Panel.SizeChanged += (_, _) => FitBackdrop();
    }

    public void Toggle()
    {
        if (IsOpen) Dismiss();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        App.DismissPanels();
        Search.Text = "";
        Clear();
        // The window is sized for the longest list; the blurred snapshot behind it is taken once, at that size.
        var screen = Screens.AtCursor().Bounds;
        double top = screen.Top + Math.Round(screen.Height * 0.2);
        maxPanelH = Math.Min(720, screen.Bottom - DockWindow.ReservedHeight - 16 - top);
        ShowPanel(new Rect(screen.Left + Math.Round((screen.Width - PanelW) / 2), top, PanelW, maxPanelH), fromBottom: false);
        FitBackdrop();
        Search.Focus();
        Keyboard.Focus(Search);
    }

    /// <summary>--snapshot: fills in the results for a query without showing the window.</summary>
    internal async Task PrepareAsync(string query)
    {
        Search.Text = query;
        fileDelay.Stop();
        await SearchFilesAsync();
    }

    /// <summary>Shows only the top part of the snapshot, as tall as the panel currently is.</summary>
    void FitBackdrop()
    {
        if (BackdropBorder.Background is not ImageBrush brush || maxPanelH <= 0) return;
        brush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
        brush.Viewbox = new Rect(0, 0, 1, Math.Clamp(Panel.ActualHeight / maxPanelH, 0.01, 1));
    }

    // ---------- searching ----------

    void OnQuery()
    {
        var query = Search.Text.Trim();
        Placeholder.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        fileSearch?.Cancel();
        fileDelay.Stop();
        if (query.Length == 0)
        {
            Clear();
            return;
        }
        localResults = SpotlightSearch.Local(query);
        fileResults = new();
        Render(query, keepSelection: false);
        if (query.Length >= 2) fileDelay.Start(); // the index is queried once typing pauses
    }

    async Task SearchFilesAsync()
    {
        var query = Search.Text.Trim();
        if (query.Length < 2) return;
        fileSearch = new CancellationTokenSource();
        var token = fileSearch.Token;
        var files = await Task.Run(() => SpotlightSearch.Files(query, token));
        if (token.IsCancellationRequested || Search.Text.Trim() != query) return;
        fileResults = files.Select(f => f.FilePath is string path ? f with { Icon = IconLoader.Get(path, 64) } : f).ToList();
        Render(query, keepSelection: true);
    }

    void Clear()
    {
        results = new();
        rows.Clear();
        Results.Children.Clear();
        Results.Visibility = Divider.Visibility = Visibility.Collapsed;
        selected = -1;
    }

    void Render(string query, bool keepSelection)
    {
        int previous = selected;
        results = [.. localResults.Take(9), .. fileResults.Take(5), SpotlightSearch.Web(query)];
        rows.Clear();
        Results.Children.Clear();
        string? section = null;
        for (int i = 0; i < results.Count; i++)
        {
            var heading = Section(results[i].Kind);
            if (heading != section)
            {
                section = heading;
                var header = new TextBlock { Text = L.T(heading), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, i == 0 ? 2 : 8, 0, 3) };
                header.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
                Results.Children.Add(header);
            }
            Results.Children.Add(MakeRow(results[i], i));
        }
        Results.Visibility = Divider.Visibility = Visibility.Visible;
        selected = -1;
        Select(keepSelection && previous >= 0 ? Math.Min(previous, rows.Count - 1) : 0);
    }

    static string Section(string kind) => kind switch
    {
        "Uygulama" => "Uygulamalar",
        "Ayarlar" => "Sistem Ayarları",
        "Belge" or "Klasör" => "Belgeler",
        _ => kind,
    };

    Border MakeRow(SpotlightResult result, int index)
    {
        FrameworkElement icon;
        if (result.Icon != null)
        {
            var image = new Image { Source = result.Icon, Width = 26, Height = 26 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            icon = image;
        }
        else
        {
            var glyph = new TextBlock { Text = result.Glyph, FontSize = 18, Width = 26, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon = glyph;
        }
        icon.Margin = new Thickness(0, 0, 12, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var title = new TextBlock { Text = result.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
        var subtitle = new TextBlock { Text = L.T(result.Subtitle), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");
        var kind = new TextBlock { Text = L.T(result.Kind), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "PanelSubText");

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        if (result.Subtitle.Length > 0 && result.Subtitle != result.Kind) texts.Children.Add(subtitle);
        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(kind, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(kind);
        line.Children.Add(texts);

        var row = new Border { Height = RowH, CornerRadius = new CornerRadius(7), Padding = new Thickness(10, 0, 12, 0), Background = Brushes.Transparent, Child = line, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => Select(index);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Run(index, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        };
        rows.Add((row, [(title, null), (subtitle, "PanelSubText"), (kind, "PanelSubText")]));
        return row;
    }

    /// <summary>The selected row is drawn like macOS: accent blue with white text.</summary>
    void Select(int index)
    {
        if (index == selected || index < 0 || index >= rows.Count) return;
        if (selected >= 0 && selected < rows.Count)
        {
            var (oldRow, oldTexts) = rows[selected];
            oldRow.Background = Brushes.Transparent;
            foreach (var (text, brush) in oldTexts)
                if (brush != null) text.SetResourceReference(TextBlock.ForegroundProperty, brush);
                else text.ClearValue(TextBlock.ForegroundProperty);
        }
        selected = index;
        var (row, texts) = rows[index];
        row.SetResourceReference(Border.BackgroundProperty, "Accent");
        foreach (var (text, _) in texts) text.Foreground = Brushes.White;
        row.BringIntoView();
    }

    void Run(int index, bool reveal)
    {
        if (index < 0 || index >= results.Count) return;
        var result = results[index];
        Dismiss();
        // Ctrl+Enter shows a file in its folder, like Cmd+Enter in macOS Spotlight.
        if (reveal && result.FilePath is string path) Ui.Open("explorer.exe", $"/select,\"{path}\"");
        else result.Open();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (Search.Text.Length > 0) Search.Text = "";
                else Dismiss();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when rows.Count > 0:
                Select(Math.Clamp(selected + (e.Key == Key.Down ? 1 : -1), 0, rows.Count - 1));
                e.Handled = true;
                break;
            case Key.Enter:
                Run(selected, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }
}
