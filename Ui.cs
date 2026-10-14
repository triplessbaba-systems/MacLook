using System.Diagnostics;
using System.Windows.Controls;

namespace MacLook;

internal static class Ui
{
    /// <param name="header">Translated when the interface is in English (window titles and names just pass through).</param>
    /// <param name="gesture">Shortcut shown on the right, e.g. "⌃C".</param>
    public static MenuItem Item(string header, Action onClick, string? gesture = null)
    {
        var item = new MenuItem { Header = L.T(header), InputGestureText = gesture ?? "" };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static void Open(string target, string? args = null)
    {
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            (target, args) = ("explorer.exe", target);
        try { Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch { }
    }
}
