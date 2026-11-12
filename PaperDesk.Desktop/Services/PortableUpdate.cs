using System.Diagnostics;
using System.IO.Compression;

namespace PaperDesk.Desktop.Services;

internal static class PortableUpdate
{
    internal const string ApplyArgument = "--apply-portable-update";
    private const string Marker = "PaperDesk.portable.txt";
    private const string PackageFolder = "package";
    private const string JobName = "update.json";
    private const string Finished = "finished";
    private static string WorkRoot => Path.Combine(Path.GetTempPath(), "PaperDesk-updates");
    private static readonly HashSet<string> ProfileEntries = new(StringComparer.OrdinalIgnoreCase)
        { "data", "profiles", "logs", "web-editor", "settings.json", "PaperDesk.installed.txt" };

    private sealed record Job(string Target, string Profile, string From, string To, int ParentId, long ParentStart);

    internal static bool IsPortable(string folder) => File.Exists(Path.Combine(folder, Marker));

    internal static string CreateWorkspace()
    {
        Directory.CreateDirectory(WorkRoot);
        CheckEntry(WorkRoot, directory: true);
        return Directory.CreateDirectory(Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"))).FullName;
    }

    internal static void StageAndStart(string workspace, string archive, string target, string profile, string version, CancellationToken token)
    {
        var package = Path.Combine(workspace, PackageFolder);
        Extract(archive, package, token);
        ReleaseUpdates.RequireVersion(Path.Combine(package, "PaperDesk.exe"), version);
        _ = ReadManifest(package, version);
        using var parent = Process.GetCurrentProcess();
        var job = new Job(Path.TrimEndingDirectorySeparator(target), Path.TrimEndingDirectorySeparator(profile), ReleaseUpdates.CurrentVersion, version, parent.Id,
            parent.StartTime.ToUniversalTime().Ticks);
        var jobPath = Path.Combine(workspace, JobName);
        File.WriteAllText(jobPath, Json.Write(job));
        token.ThrowIfCancellationRequested();
        Launch(package, ApplyArgument, jobPath);
    }

    internal static void Apply(string jobPath)
    {
        var package = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var workspace = Path.GetDirectoryName(package)!;
        if (!IsWorkspace(workspace) || Path.GetFileName(package) != PackageFolder ||
            !Path.GetFullPath(jobPath).Equals(Path.Combine(workspace, JobName), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid portable update workspace.");
        CheckEntry(workspace, directory: true);
        CheckEntry(package, directory: true);
        var job = Json.Read<Job>(File.ReadAllText(jobPath));
        if (job.To != ReleaseUpdates.CurrentVersion || !Path.IsPathFullyQualified(job.Target) || !Path.IsPathFullyQualified(job.Profile) ||
            !job.Target.Equals(Path.GetFullPath(job.Target), StringComparison.OrdinalIgnoreCase) ||
            IsWithin(job.Target, WorkRoot) || !IsPortable(job.Target) || ProfileStorage.IsInstalled(job.Target))
            throw new InvalidDataException("The update target is not a PaperDesk portable copy.");

        using var gate = new Mutex(false, "Local\\PaperDesk-update-" + ProfileStorage.Key(job.Target));
        if (!gate.WaitOne(0)) throw new IOException("PaperDesk is already updating this copy.");
        try
        {
            WaitForParent(job);
            ReleaseUpdates.RequireVersion(Path.Combine(job.Target, "PaperDesk.exe"), job.From);
            using var helper = Process.GetCurrentProcess();
            try
            {
                ApplyFiles(package, job.Target, job.Profile, job.From, job.To, Path.Combine(workspace, "backup"),
                    () => Launch(job.Target, "--data", job.Profile));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                Launch(job.Target, "--data", job.Profile);
                MarkFinished(workspace, helper);
                throw;
            }
            MarkFinished(workspace, helper);
        }
        finally { gate.ReleaseMutex(); }
    }

    internal static void Extract(string archivePath, string package, CancellationToken token)
    {
        Directory.CreateDirectory(package);
        using var archive = ZipFile.OpenRead(archivePath);
        const long maxBytes = 1024L * 1024 * 1024;
        if (archive.Entries.Count > 2000) throw new InvalidDataException("The update contains too many files.");
        long total = 0;
        var buffer = new byte[64 * 1024];
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var destination = FilePath(package, entry.FullName);
            if (entry.Length < 0 || entry.Length > maxBytes - total || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Invalid portable archive entry.");
            total += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = File.Open(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var left = entry.Length;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                left -= read;
                if (left < 0) throw new InvalidDataException("The archive entry is larger than declared.");
                output.Write(buffer, 0, read);
            }
            if (left != 0) throw new InvalidDataException("The archive entry is incomplete.");
        }
    }

    internal static void ApplyFiles(string package, string target, string profile, string from, string to, string backup, Action restart)
    {
        var installedFiles = ReadManifest(target, from);
        var newFiles = ReadManifest(package, to);
        var applied = new List<(string Destination, string? Backup)>();
        foreach (var name in newFiles)
        {
            var destination = FilePath(target, name);
            if (!profile.Equals(target, StringComparison.OrdinalIgnoreCase) && IsWithin(destination, profile))
                throw new InvalidDataException("The update overlaps the selected data folder.");
            CheckPath(target, destination);
            var source = FilePath(package, name);
            CheckPath(package, source);
            if (!File.Exists(source)) throw new InvalidDataException("A portable package file is missing.");
            if (File.Exists(destination) && !installedFiles.Contains(name))
                throw new IOException("The update would overwrite a file outside the previous package.");
        }

        try
        {
            foreach (var name in newFiles.OrderBy(name => name == Marker ? 2 : name == "PaperDesk.exe" ? 1 : 0))
            {
                var destination = FilePath(target, name);
                string? original = null;
                if (File.Exists(destination))
                {
                    original = FilePath(backup, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Copy(destination, original);
                }
                Replace(FilePath(package, name), destination, overwrite: original != null);
                applied.Add((destination, original));
            }
            restart();
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var (destination, original) in Enumerable.Reverse(applied))
            {
                try
                {
                    if (original == null) File.Delete(destination);
                    else Replace(original, destination, overwrite: true);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { failures.Add(rollback); }
            }
            if (failures.Count > 1) throw new AggregateException("The portable update backup is retained.", failures);
            throw;
        }
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

    private static void WaitForParent(Job job)
    {
        Process parent;
        try { parent = Process.GetProcessById(job.ParentId); }
        catch (ArgumentException) { return; }
        using (parent)
        {
            if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != job.ParentStart) return;
            if (!parent.WaitForExit(20_000)) throw new IOException("Close the other PaperDesk process and retry the update.");
        }
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

    private static void Launch(string folder, params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(folder, "PaperDesk.exe")) { WorkingDirectory = folder, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Unable to start PaperDesk.");
    }

    private static HashSet<string> ReadManifest(string folder, string version)
    {
        CheckEntry(folder, directory: true);
        var manifest = Path.Combine(folder, Marker);
        CheckEntry(manifest, directory: false);
        if (new FileInfo(manifest).Length > 256 * 1024) throw new InvalidDataException("Invalid portable package manifest.");
        var lines = File.ReadAllLines(manifest);
        if (lines.Length < 4 || lines[0] != version) throw new InvalidDataException("The portable package manifest version does not match.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
        foreach (var name in lines.Skip(1))
        {
            _ = FilePath(folder, name);
            if (!names.Add(name)) throw new InvalidDataException("Duplicate file in the portable package manifest.");
        }
        if (!names.Contains("PaperDesk.exe") || !names.Contains("PaperDesk.dll") || !names.Contains("PaperDesk.deps.json"))
            throw new InvalidDataException("The portable package is incomplete.");
        return names;
    }

    private static string FilePath(string folder, string name)
    {
        var parts = name.Split('/');
        if (ProfileEntries.Contains(parts[0]) || parts.Any(part => part.Length == 0 || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part)))
            throw new InvalidDataException("Invalid portable package path.");
        return Path.Combine(folder, string.Join(Path.DirectorySeparatorChar, parts));
    }

    private static bool IsDeviceName(string part)
    {
        var stem = part.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "0123456789¹²³".Contains(stem[3]);
    }

    private static void CheckPath(string root, string path)
    {
        CheckEntry(root, directory: true);
        for (var folder = Path.GetDirectoryName(path)!; !folder.Equals(root, StringComparison.OrdinalIgnoreCase); folder = Path.GetDirectoryName(folder)!)
            CheckEntry(folder, directory: true);
        CheckEntry(path, directory: false);
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

    private static bool IsWithin(string path, string folder) =>
        path.Equals(Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool IsWorkspace(string path) => Guid.TryParseExact(Path.GetFileName(path), "N", out _) &&
        Path.GetDirectoryName(path)?.Equals(Path.TrimEndingDirectorySeparator(WorkRoot), StringComparison.OrdinalIgnoreCase) == true;

    private static void Replace(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, destination, overwrite);
        }
        finally { File.Delete(temporary); }
    }
}
