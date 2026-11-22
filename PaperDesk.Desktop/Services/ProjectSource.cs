using System.ComponentModel;
using System.Diagnostics;

namespace PaperDesk.Desktop.Services;

internal static class ProjectSource
{
    internal const int MaxLength = 4096;
    private static readonly string[] extensions =
        [".txt", ".md", ".pdf", ".docx", ".xlsx", ".pptx", ".csv", ".png", ".jpg", ".jpeg", ".webp", ".psd"];

    internal static string DocumentFilter =>
        $"{L.T("Документы проекта")}|{string.Join(';', extensions.Select(extension => "*" + extension))}";

    internal static string? Normalize(string? value)
    {
        var source = value?.Trim();
        if (source is null || source.Length is 0 or > MaxLength || source.Any(char.IsControl)) return null;
        if (WebLink(source) is { } link) return link.AbsoluteUri.Length <= MaxLength ? link.AbsoluteUri : null;

        // Accept selected local documents. Shell shortcuts and executable formats are not project sources.
        if (source.Length < 4 || !char.IsAsciiLetter(source[0]) || source[1] != ':' ||
            source[2] is not ('\\' or '/') || source.AsSpan(2).Contains(':') ||
            source.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;
        try
        {
            var path = Path.GetFullPath(source);
            if (!extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) return null;
            var invalidCharacters = Path.GetInvalidFileNameChars();
            return path[3..].Split(Path.DirectorySeparatorChar)
                .All(part => part.Length != 0 && part.TrimEnd(' ', '.') == part && part.IndexOfAny(invalidCharacters) < 0) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static Uri? WebLink(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" &&
        uri.Host.Length != 0 && uri.UserInfo.Length == 0 ? uri : null;

    internal static string? Open(string source)
    {
        var target = Normalize(source);
        if (target == null) return L.T("Выберите ссылку HTTP(S) или локальный документ.");
        if (WebLink(target) == null && !File.Exists(target))
            return L.T("Файл проекта недоступен. Выберите его заново в меню «Проекты».");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return L.T("Не удалось открыть источник проекта.");
        }
    }
}
