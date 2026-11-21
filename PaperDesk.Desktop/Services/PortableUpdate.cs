using System.Diagnostics;

namespace PaperDesk.Desktop.Services;

internal static class PortableUpdate
{
    internal const string ApplyArgument = "--apply-portable-update";
    private const string Marker = "PaperDesk.portable.txt";
    private const string ZipName = "PaperDesk-Portable-win-x64.zip";
    private const string Finished = "finished";
    private static string WorkRoot => Path.Combine(Path.GetTempPath(), "PaperDesk-updates");

    internal static bool IsPortable(string folder) => File.Exists(Path.Combine(folder, Marker));

    internal static string CreateWorkspace()
    {
        Directory.CreateDirectory(WorkRoot);
        CheckEntry(WorkRoot, directory: true);
        return Directory.CreateDirectory(Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"))).FullName;
    }

    // Older releases launch the new executable as a helper. Offer the verified ZIP
    // they already downloaded and leave their running copy's files in place.
    internal static async Task SaveLegacyZip(string jobPath)
    {
        var package = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var workspace = Path.GetDirectoryName(package)!;
        if (!IsWorkspace(workspace) || Path.GetFileName(package) != "package" ||
            !Path.GetFullPath(jobPath).Equals(Path.Combine(workspace, "update.json"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid portable update workspace.");
        CheckEntry(WorkRoot, directory: true);
        CheckEntry(workspace, directory: true);
        CheckEntry(package, directory: true);
        var zip = Path.Combine(workspace, ZipName);
        CheckEntry(zip, directory: false);
        var profile = Json.Read<LegacyJob>(File.ReadAllText(jobPath)).Profile;
        if (!Path.IsPathFullyQualified(profile)) throw new InvalidDataException("Invalid data folder.");
        try { await OfferZip(zip, profile, null, CancellationToken.None); }
        finally { using var process = Process.GetCurrentProcess(); MarkFinished(workspace, process); }
    }

    internal static async Task OfferZip(string source, string profile, Window? owner, CancellationToken token)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = ZipName, Filter = L.T("Архив ZIP|*.zip") };
        if ((owner == null ? dialog.ShowDialog() : dialog.ShowDialog(owner)) != true) return;
        await SaveZip(source, dialog.FileName, token);
        var message = L.F("Архив проверен и сохранён. Распакуйте его в новую папку и закройте старый PaperDesk. Чтобы открыть существующие заметки, запустите новый PaperDesk.exe с параметрами:\n\n--data \"{0}\"", profile);
        if (owner == null) MessageBox.Show(message, "PaperDesk");
        else MessageBox.Show(owner, message, "PaperDesk");
    }

    internal static async Task SaveZip(string source, string destination, CancellationToken token)
    {
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a .zip file.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await input.CopyToAsync(output, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static void MarkFinished(string workspace, Process owner)
    {
        try { File.WriteAllLines(Path.Combine(workspace, Finished), [owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString()]); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static void CleanFinished()
    {
        try
        {
            if (!Directory.Exists(WorkRoot)) return;
            CheckEntry(WorkRoot, directory: true);
            foreach (var workspace in Directory.EnumerateDirectories(WorkRoot))
            {
                var marker = Path.Combine(workspace, Finished);
                if (!File.Exists(marker)) continue;
                var stamp = File.ReadAllLines(marker);
                if (stamp is [var idText, var started] && int.TryParse(idText, out var id) && long.TryParse(started, out var ticks) &&
                    !ProcessIsRunning(id, ticks)) DeleteWorkspace(workspace);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception) { }
    }

    internal static void DeleteWorkspace(string workspace)
    {
        try
        {
            if (!IsWorkspace(workspace)) return;
            CheckEntry(WorkRoot, directory: true);
            CheckEntry(workspace, directory: true);
            Directory.Delete(workspace, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static bool ProcessIsRunning(int id, long started)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void CheckEntry(string path, bool directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("An update path is linked or has an unexpected type.");
    }


    private static bool IsWorkspace(string path) => Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        Path.GetDirectoryName(path)?.Equals(Path.TrimEndingDirectorySeparator(WorkRoot), StringComparison.OrdinalIgnoreCase) == true;

    private sealed record LegacyJob(string Profile);
}
