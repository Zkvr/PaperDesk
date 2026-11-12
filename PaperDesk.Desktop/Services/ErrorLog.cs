namespace PaperDesk.Desktop.Services;

internal static class ErrorLog
{
    internal enum Area { Startup, Dispatcher, Import, Export }
    private const long MaxBytes = 128 * 1024;
    private static readonly object Gate = new();

    internal static void Write(string? profileRoot, Area area, Exception error, string? localAppData = null)
    {
        try
        {
            string folder;
            if (Directory.Exists(profileRoot)) folder = Path.Combine(profileRoot!, "logs");
            else if (!string.IsNullOrEmpty(localAppData) && Path.IsPathFullyQualified(localAppData))
                folder = Path.Combine(localAppData, "PaperDesk", "logs");
            else return;

            var rootCause = error.GetBaseException();
            var line = FormattableString.Invariant(
                $"{DateTimeOffset.UtcNow:O}\t{area}\t{error.GetType().Name}\t0x{error.HResult:X8}\t{rootCause.GetType().Name}\t0x{rootCause.HResult:X8}");
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                var log = new FileInfo(Path.Combine(folder, "errors.log"));
                if (log.Exists && log.Length >= MaxBytes)
                    File.Move(log.FullName, Path.Combine(folder, "errors.previous.log"), overwrite: true);
                File.AppendAllText(log.FullName, line + Environment.NewLine);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must not replace the original failure.
        }
    }
}
