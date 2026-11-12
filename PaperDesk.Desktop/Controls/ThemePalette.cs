namespace PaperDesk.Desktop.Controls;

internal sealed record DeskTheme(string Id, string Label, Color Shell, Color Panel, Color Accent);

internal static class ThemePalette
{
    internal static readonly DeskTheme[] Choices =
    [
        new("graphite", "Графит", Color.FromRgb(27, 30, 36), Color.FromRgb(47, 52, 61), Color.FromRgb(232, 200, 140)),
        new("ocean", "Океан", Color.FromRgb(19, 33, 45), Color.FromRgb(42, 76, 91), Color.FromRgb(152, 220, 217)),
        new("clay", "Глина", Color.FromRgb(38, 27, 30), Color.FromRgb(79, 49, 53), Color.FromRgb(243, 180, 154))
    ];

    private static ResourceDictionary? active;

    internal static DeskTheme Find(string? id) => Choices.FirstOrDefault(theme => theme.Id == id) ?? Choices[0];

    internal static Brush Brush(Color color, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    internal static void Apply(string? id)
    {
        if (Application.Current is not { } app) return;
        var theme = Find(id);
        var dictionaries = app.Resources.MergedDictionaries;
        if (active != null) dictionaries.Remove(active);
        active = theme.Id == Choices[0].Id ? null : new ResourceDictionary
        {
            ["DeskWindowBackground"] = Brush(theme.Shell),
            ["DeskFrame"] = Brush(theme.Shell),
            ["DeskPanel"] = Brush(theme.Panel, 100),
            ["DeskField"] = Brush(theme.Panel, 146),
            ["DeskPopup"] = Brush(theme.Shell),
            ["Accent"] = Brush(theme.Accent),
            ["AccentTint"] = Brush(theme.Accent, 28),
            ["AccentHover"] = Brush(theme.Accent, 85),
            ["AccentOutline"] = Brush(theme.Accent, 181)
        };
        if (active != null) dictionaries.Add(active);
    }
}
