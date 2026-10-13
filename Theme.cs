using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace MacLook;

/// <summary>Light/dark colors that follow the Windows system theme.</summary>
internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static void Init(ResourceDictionary resources)
    {
        Apply(resources);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General)
                Application.Current.Dispatcher.Invoke(() => Apply(resources));
        };
    }

    public static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    static void Apply(ResourceDictionary r)
    {
        IsDark = ReadIsDark();
        bool d = IsDark;
        // Menu bar text is picked per wallpaper by the menu bar itself; these are fallbacks.
        r["BarText"] = Brush(d ? "#FFFFFFFF" : "#FF1D1D1F");
        r["BarHover"] = Brush(d ? "#33FFFFFF" : "#1F000000");

        // Tooltips and hover inside menu-style panels.
        r["PanelHover"] = Brush(d ? "#26FFFFFF" : "#1A000000");
        r["PreviewBg"] = Brush(d ? "#FF2A2A2D" : "#FFF4F4F6");
        r["TipBg"] = Brush(d ? "#F02C2C2E" : "#F2F2F2F4");
        r["TipBorder"] = Brush(d ? "#26FFFFFF" : "#1A000000");
        r["TipText"] = Brush(d ? "#FFFFFFFF" : "#FF1D1D1F");

        // Menus.
        r["MenuBg"] = Brush(d ? "#F52A2A2D" : "#F7F4F4F6");
        r["MenuBorder"] = Brush(d ? "#30FFFFFF" : "#1F000000");
        r["MenuText"] = Brush(d ? "#FFFFFFFF" : "#FF1D1D1F");
        r["MenuHover"] = Brush("#FF0A7CFF");

        // Start menu and Control Center: frosted panel over a blurred snapshot of the screen.
        r["PanelTint"] = Brush(d ? "#D91E1E20" : "#E6F4F4F6");
        r["PanelBorder"] = Brush(d ? "#26FFFFFF" : "#26000000");
        r["PanelText"] = Brush(d ? "#FFFFFFFF" : "#FF1D1D1F");
        r["PanelSubText"] = Brush(d ? "#99FFFFFF" : "#8C3C3C43");
        r["FieldBg"] = Brush(d ? "#1AFFFFFF" : "#12000000");
        r["FieldBorder"] = Brush(d ? "#14FFFFFF" : "#14000000");
        r["FooterBg"] = Brush(d ? "#0DFFFFFF" : "#0A000000");
        r["TileBg"] = Brush(d ? "#1FFFFFFF" : "#B3FFFFFF");
        r["TileOff"] = Brush(d ? "#33FFFFFF" : "#1F000000");
        r["SliderTrack"] = Brush(d ? "#33FFFFFF" : "#1F000000");
        r["SliderFill"] = Brush("#FFFFFFFF");
        r["SliderIcon"] = Brush("#991D1D1F");
        r["Accent"] = Brush("#FF0A84FF");
        r["ScrollThumb"] = Brush(d ? "#4DFFFFFF" : "#40000000");

        // App switcher: tint over a blurred snapshot of the screen.
        r["SwitcherTint"] = Brush(d ? "#A6262628" : "#A6F0F0F2");
        r["SwitcherBorder"] = Brush(d ? "#26FFFFFF" : "#59FFFFFF");
        r["SwitcherSelect"] = Brush(d ? "#40FFFFFF" : "#2E000000");
        r["SwitcherText"] = Brush(d ? "#FFFFFFFF" : "#FF1D1D1F");
    }

    static bool ReadIsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 0;
    }
}
