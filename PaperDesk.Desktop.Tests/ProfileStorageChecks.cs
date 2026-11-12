using System.IO;
using Microsoft.Data.Sqlite;
using PaperDesk.Core;
using PaperDesk.Desktop.Services;
using PaperDesk.Storage;

internal static class ProfileStorageChecks
{
    internal static int Run(string folder)
    {
        var testRoot = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(folder), Guid.NewGuid().ToString("N"))).FullName;
        var app = Directory.CreateDirectory(Path.Combine(testRoot, "installed app")).FullName;
        var local = Path.Combine(testRoot, "LocalAppData");
        var legacy = Path.Combine(app, "data");
        var installed = Path.Combine(local, "PaperDesk", "data");
        Require(ProfileStorage.Resolve([], app, local) == (legacy, null), "Portable must use adjacent data");
        File.WriteAllText(Path.Combine(app, "PaperDesk.installed.txt"), "Installed");
        Require(ProfileStorage.Resolve([], app, local) == (installed, legacy), "Installed app must select the user profile");
        var custom = Path.Combine(testRoot, "custom profile");
        Require(ProfileStorage.Resolve(["--startup", "--data", custom], app, local) == (custom, null),
            "An explicit profile must take precedence even at startup");
        RequireThrows<ArgumentException>(() => ProfileStorage.Resolve(["--data"], app, local));
        Console.WriteLine("PASS portable, installed and explicit profile selection");

        var oldDb = Path.Combine(legacy, "profiles", "local", "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(oldDb)!);
        var settings = Json.Write(new Settings { AutoStart = true, Animations = false });
        File.WriteAllText(Path.Combine(legacy, "settings.json"), settings);
        var vaultBytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(Path.Combine(legacy, "session-synthetic.dpapi"), vaultBytes);
        var oldCache = Directory.CreateDirectory(Path.Combine(legacy, "web-editor"));
        File.WriteAllText(Path.Combine(oldCache.FullName, "cache.txt"), "generated");
        var note = new Note { Body = "committed in WAL" };
        var fixtureDb = Path.Combine(testRoot, "fixture.db");
        using (var fixture = new NoteStore(fixtureDb))
        {
            fixture.Save(note);
            File.Copy(fixtureDb, oldDb);
            File.Copy(fixtureDb + "-wal", oldDb + "-wal");
        }
        var originalDb = File.ReadAllBytes(oldDb);
        var originalWal = File.ReadAllBytes(oldDb + "-wal");
        var secondDb = Path.Combine(legacy, "profiles", "other", "notes.db");
        var secondNote = new Note { Body = "another profile" };
        using (var other = new NoteStore(secondDb)) other.Save(secondNote);

        ProfileStorage.Migrate(installed, legacy);

        var migratedDb = Path.Combine(installed, "profiles", "local", "notes.db");
        using (var migrated = new NoteStore(migratedDb))
        {
            Require(migrated.Find(note.Id)?.Note.Body == note.Body, "Migration must include committed WAL writes");
            Require(migrated.Pending().Single().Note.Id == note.Id, "Migration must preserve queued writes");
            migrated.Save(note with { Body = "edited after migration" });
        }
        using (var other = new NoteStore(Path.Combine(installed, "profiles", "other", "notes.db")))
            Require(other.Find(secondNote.Id) != null, "Every note profile must be migrated");
        Require(File.ReadAllText(Path.Combine(installed, "settings.json")) == settings, "Settings must be copied");
        Require(File.ReadAllBytes(Path.Combine(installed, "session-synthetic.dpapi")).SequenceEqual(vaultBytes),
            "Protected session files must be copied as opaque bytes");
        Require(!Directory.Exists(Path.Combine(installed, "web-editor")), "WebView2 cache can be regenerated");
        Require(File.ReadAllBytes(oldDb).SequenceEqual(originalDb) && File.ReadAllBytes(oldDb + "-wal").SequenceEqual(originalWal),
            "The old database and WAL must remain byte-for-byte intact");
        ProfileStorage.Migrate(installed, legacy);
        using (var reopened = new NoteStore(migratedDb))
            Require(reopened.Find(note.Id)?.Note.Body == "edited after migration", "Restart must retain edits in the migrated profile");
        Console.WriteLine("PASS staged upgrade preserves WAL, settings, profiles and subsequent edits");

        var retryLegacy = Path.Combine(testRoot, "retry", "data");
        var retryDb = Path.Combine(retryLegacy, "profiles", "local", "notes.db");
        Directory.CreateDirectory(Path.GetDirectoryName(retryDb)!);
        var invalid = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(retryDb, invalid);
        var retryRoot = Path.Combine(testRoot, "retry-target", "data");
        RequireThrows<SqliteException>(() => ProfileStorage.Migrate(retryRoot, retryLegacy));
        Require(!Directory.Exists(retryRoot) && File.ReadAllBytes(retryDb).SequenceEqual(invalid),
            "A corrupt profile must leave the source intact and the destination absent");
        File.Delete(retryDb);
        using (var source = new NoteStore(retryDb)) source.Save(note);
        using (var running = new Mutex(true, "Local\\PaperDesk-" + ProfileStorage.Key(retryLegacy), out var acquired))
        {
            Require(acquired, "The fixture must acquire the old application's mutex");
            try { RequireThrows<IOException>(() => ProfileStorage.Migrate(retryRoot, retryLegacy)); }
            finally { running.ReleaseMutex(); }
        }
        Require(!Directory.Exists(retryRoot), "Migration must wait for the old profile to close");
        ProfileStorage.Migrate(retryRoot, retryLegacy);
        using (var reopened = new NoteStore(Path.Combine(retryRoot, "profiles", "local", "notes.db")))
            Require(reopened.Find(note.Id) != null, "Migration must be retryable");
        RequireThrows<IOException>(() => ProfileStorage.Migrate(retryRoot, legacy));
        Console.WriteLine("PASS failed, busy and conflicting profiles are preserved and migration can be retried");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }
}
