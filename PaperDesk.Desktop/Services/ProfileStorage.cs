using System.Security.Cryptography;
using System.Text;

namespace PaperDesk.Desktop.Services;

internal static class ProfileStorage
{
    private const string InstalledMarker = "PaperDesk.installed.txt";
    private const string MigrationMarker = ".migrated-from";

    internal static (string Root, string? LegacyRoot) Resolve(string[] args, string appDirectory, string localAppData)
    {
        var dataIndex = Array.IndexOf(args, "--data");
        if (dataIndex >= 0)
        {
            if (dataIndex + 1 == args.Length || string.IsNullOrWhiteSpace(args[dataIndex + 1]) ||
                args[dataIndex + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(L.T("Укажите папку после --data."));
            return (Path.GetFullPath(args[dataIndex + 1]), null);
        }

        var adjacent = Path.Combine(appDirectory, "data");
        if (!Exists(Path.Combine(appDirectory, InstalledMarker), directory: false))
            return (adjacent, null);
        if (!Path.IsPathFullyQualified(localAppData))
            throw new IOException(L.T("Не найдена папка LocalAppData."));
        var installed = Path.Combine(localAppData, "PaperDesk", "data");
        return (installed, SamePath(adjacent, installed) ? null : adjacent);
    }

    internal static string Key(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..20];

    internal static void Migrate(string root, string? legacyRoot)
    {
        if (legacyRoot == null) return;
        if (Exists(root, directory: true))
        {
            var marker = Path.Combine(root, MigrationMarker);
            if (Exists(marker, directory: false) && SamePath(File.ReadAllText(marker), legacyRoot)) return;
            if (!Exists(legacyRoot, directory: true)) return;
            throw new IOException(L.T("Найдены два профиля PaperDesk. Укажите нужную папку через --data.") +
                "\n\n" + root + "\n" + legacyRoot);
        }
        if (!Exists(legacyRoot, directory: true)) return;

        using var legacyMutex = new Mutex(true, "Local\\PaperDesk-" + Key(legacyRoot), out var acquired);
        if (!acquired) throw new IOException(L.T("Закройте PaperDesk со старым профилем и запустите приложение снова."));
        try
        {
            var parent = Path.GetDirectoryName(root)!;
            if (SamePath(parent, legacyRoot) || parent.StartsWith(legacyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException(L.T("Новый профиль нельзя разместить внутри старого."));
            if (!Exists(parent, directory: true)) Directory.CreateDirectory(parent);

            var staging = Path.Combine(parent, ".profile-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                CopyProfile(legacyRoot, staging);
                File.WriteAllText(Path.Combine(staging, MigrationMarker), legacyRoot);
                Directory.Move(staging, root);
            }
            catch
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { legacyMutex.ReleaseMutex(); }
    }

    private static void CopyProfile(string source, string destination)
    {
        var settings = Path.Combine(destination, "settings.json");
        if (CopyFile(Path.Combine(source, "settings.json"), settings))
            _ = Json.Read<Settings>(File.ReadAllText(settings));
        foreach (var vault in Directory.EnumerateFiles(source, "session-*.dpapi"))
            CopyFile(vault, Path.Combine(destination, Path.GetFileName(vault)));

        var profiles = Path.Combine(source, "profiles");
        if (!Exists(profiles, directory: true)) return;
        foreach (var profile in Directory.EnumerateDirectories(profiles))
        {
            if (!Exists(profile, directory: true)) throw new DirectoryNotFoundException(profile);
            var database = Path.Combine(profile, "notes.db");
            if (!Exists(database, directory: false)) continue;
            var copiedProfile = Directory.CreateDirectory(Path.Combine(destination, "profiles", Path.GetFileName(profile)));
            var copiedDb = Path.Combine(copiedProfile.FullName, "notes.db");

            // Hold the source database open for reading while copying its persistent journal files.
            using var input = File.Open(database, FileMode.Open, FileAccess.Read, FileShare.Read);
            WriteCopy(input, copiedDb);
            CopyFile(database + "-wal", copiedDb + "-wal");
            CopyFile(database + "-journal", copiedDb + "-journal");
            using var checkedStore = new NoteStore(copiedDb);
        }
    }

    private static bool CopyFile(string source, string destination)
    {
        if (!Exists(source, directory: false)) return false;
        using var input = File.Open(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        WriteCopy(input, destination);
        return true;
    }

    private static void WriteCopy(FileStream input, string destination)
    {
        using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static bool Exists(string path, bool directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new IOException(L.T("Неожиданный тип файла или папки: ") + path);
        return true;
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
