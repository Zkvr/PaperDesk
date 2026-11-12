using System.IO;
using PaperDesk.Desktop.Services;

internal static class ErrorLogChecks
{
    internal static int Run(string folder)
    {
        var root = Directory.CreateDirectory(Path.Combine(folder, Guid.NewGuid().ToString("N"))).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
        var current = Path.Combine(profile, "logs", "errors.log");
        var error = new InvalidDataException("private note body", new IOException("private token"));
        ErrorLog.Write(profile, ErrorLog.Area.Import, error);
        var line = File.ReadAllText(current);
        Require(line.Contains("Import") && line.Contains("InvalidDataException") && line.Contains("IOException") &&
            line.Contains("0x") && !line.Contains("private"), "The journal must contain diagnostics without exception messages");
        Console.WriteLine("PASS local journal records error categories and codes without sensitive messages");

        using (var file = File.OpenWrite(current)) file.SetLength(128 * 1024);
        ErrorLog.Write(profile, ErrorLog.Area.Export, error);
        Require(new FileInfo(Path.Combine(profile, "logs", "errors.previous.log")).Length == 128 * 1024 &&
            new FileInfo(current).Length < 1024, "The journal must rotate at its size threshold");
        Console.WriteLine("PASS journal size is bounded with one previous file");

        var localAppData = Path.Combine(root, "LocalAppData");
        var missing = Path.Combine(localAppData, "PaperDesk", "data");
        ErrorLog.Write(missing, ErrorLog.Area.Startup, error, localAppData);
        Require(File.Exists(Path.Combine(localAppData, "PaperDesk", "logs", "errors.log")) &&
            !Directory.Exists(missing), "Logging a migration failure must not create the destination profile");
        var blocked = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        File.WriteAllText(Path.Combine(blocked, "logs"), "existing file");
        ErrorLog.Write(blocked, ErrorLog.Area.Dispatcher, error);
        Require(File.ReadAllText(Path.Combine(blocked, "logs")) == "existing file", "A logging failure must leave the original path intact");
        Console.WriteLine("PASS startup fallback preserves a missing profile and log I/O failures are nonfatal");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
