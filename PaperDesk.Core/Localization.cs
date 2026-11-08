using System.Globalization;
using System.Text.Json;

namespace PaperDesk.Core;

public static class L
{
    private static readonly Lazy<Dictionary<string, string>> English = new(() =>
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("PaperDesk.strings.en.json")
            ?? throw new InvalidOperationException("English resources are missing.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    });

    public static string T(string russian) => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
        ? russian : English.Value.GetValueOrDefault(russian, russian);

    public static string F(string russian, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, T(russian), values);
}
