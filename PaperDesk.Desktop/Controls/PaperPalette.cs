namespace PaperDesk.Desktop.Controls;

public static class PaperPalette
{
    public static readonly (string Name, string Color)[] Colors = [(L.T("Жёлтая"), "#F4E5AD"), (L.T("Розовая"), "#EACCD3"), (L.T("Мятная"), "#CCE0CE"), (L.T("Голубая"), "#CDDCE9"), (L.T("Лавандовая"), "#DAD0E7"), (L.T("Кремовая"), "#EADDC9")];
    public static string Normalize(string color) => color.ToUpperInvariant() switch { "#FFF1AA" => "#F4E5AD", "#D7F0DA" => "#CCE0CE", "#DCEBFF" => "#CDDCE9", "#EAE1FA" => "#DAD0E7", "#FADFE8" => "#EACCD3", _ => color };
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(Normalize(color)));
}
