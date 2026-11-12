namespace PaperDesk.Desktop.Controls;

public static class PaperPalette
{
    public static readonly (string Name, string Color)[] Colors =
    [
        (L.T("Жёлтая"), "#F4E5AD"), (L.T("Розовая"), "#EACCD3"), (L.T("Мятная"), "#CCE0CE"),
        (L.T("Голубая"), "#CDDCE9"), (L.T("Лавандовая"), "#DAD0E7"), (L.T("Кремовая"), "#EADDC9"),
        (L.T("Персиковая"), "#F0C5A4"), (L.T("Коралловая"), "#EDC2BC"), (L.T("Шалфейная"), "#D9DEB4"),
        (L.T("Бирюзовая"), "#B7DBDA"), (L.T("Сиреневая"), "#CFC2E8"), (L.T("Слоновая кость"), "#F4F0E5")
    ];
    private static readonly Dictionary<string, SolidColorBrush> Swatches = Colors.ToDictionary(
        paper => paper.Color, paper => CreateBrush(paper.Color), StringComparer.OrdinalIgnoreCase);
    public static string Normalize(string color) => color.ToUpperInvariant() switch { "#FFF1AA" => "#F4E5AD", "#D7F0DA" => "#CCE0CE", "#DCEBFF" => "#CDDCE9", "#EAE1FA" => "#DAD0E7", "#FADFE8" => "#EACCD3", _ => color };
    public static SolidColorBrush Brush(string color)
    {
        var normalized = Normalize(color);
        return Swatches.TryGetValue(normalized, out var brush) ? brush : CreateBrush(normalized);
    }

    private static SolidColorBrush CreateBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
