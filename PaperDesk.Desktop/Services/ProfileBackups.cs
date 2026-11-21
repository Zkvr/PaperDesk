using System.Globalization;

namespace PaperDesk.Desktop.Services;

internal static class ProfileBackups
{
    private const string Stamp = "yyyyMMdd-HHmmssfffffff";
    private const string Prefix = "PaperDesk-auto-";
    internal const string SearchPattern = Prefix + "*.json";
    private const int Keep = 7;

    internal static bool Capture(NoteStore store, string folder, DateTimeOffset now)
    {
        var directory = Directory.CreateDirectory(folder);
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The backup folder is linked.");
        var previous = directory.EnumerateFiles(SearchPattern)
            .Select(file => (File: file, Name: Path.GetFileNameWithoutExtension(file.Name)))
            .Where(entry => DateTimeOffset.TryParseExact(entry.Name[Prefix.Length..], Stamp,
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))
            .OrderByDescending(entry => entry.Name, StringComparer.Ordinal).ToArray();
        var stamp = now.UtcDateTime.ToString(Stamp, CultureInfo.InvariantCulture);
        if (previous.Length != 0 && string.CompareOrdinal(previous[0].Name,
                Prefix + now.UtcDateTime.AddHours(-1).ToString(Stamp, CultureInfo.InvariantCulture)) > 0)
            return false;
        if (!store.HasNotes()) return false;
        store.ExportFile(Path.Combine(folder, Prefix + stamp + ".json"));
        foreach (var entry in previous.Skip(Keep - 1)) entry.File.Delete();
        return true;
    }

    internal static string Restore(string file, string dataRoot, bool allowLargeBackup = false)
    {
        if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.T("Неподдерживаемый формат резервной копии"));
        var parent = Directory.CreateDirectory(Path.Combine(dataRoot, "restored"));
        if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The restored profiles folder is linked.");
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent.FullName, "." + id);
        var target = Path.Combine(parent.FullName, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + id);
        try
        {
            using (var store = new NoteStore(Path.Combine(staging, "profiles", "local", "notes.db")))
                store.ImportFiles([file], allowLargeBackup);
            Directory.Move(staging, target);
            return target;
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
