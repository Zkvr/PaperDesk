using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using PaperDesk.Core;
using PaperDesk.Storage;

static class InterruptedWriteCheck
{
    private const string Before = "Before the edit";
    private const string Committed = "Saved before process termination 🌿";

    public static void Run(string path)
    {
        var note = new Note { Body = Before };
        Outgoing original;
        using (var store = new NoteStore(path))
        {
            store.Save(note);
            original = store.Pending().Single();
        }

        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--interrupted-write");
        start.ArgumentList.Add(path);
        using var writer = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test writer.");
        try
        {
            if (writer.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult() != "uncommitted")
                throw new InvalidOperationException("The test writer did not reach its open transaction.");
        }
        finally
        {
            if (!writer.HasExited) writer.Kill();
            if (!writer.WaitForExit(5000)) throw new InvalidOperationException("The test writer did not exit.");
        }

        using var reopened = new NoteStore(path);
        var persisted = reopened.Find(note.Id);
        if (persisted?.Note.Body != Committed || persisted.Generation != original.Generation + 1 ||
            reopened.Pending().Single() != original)
            throw new InvalidOperationException("The last committed edit or its outgoing state was lost.");
    }

    public static int WriteUntilInterrupted(string path)
    {
        Note note;
        using (var store = new NoteStore(path))
        {
            note = store.All().Single().Note;
            store.SaveText(note.Id, null, Committed, note.Title, note.Body);
        }

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction();
        using var update = db.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE notes SET json=$json, generation=generation+1; DELETE FROM outbox";
        update.Parameters.AddWithValue("$json", Json.Write(note with { Body = "Uncommitted text" }));
        if (update.ExecuteNonQuery() != 2) throw new InvalidOperationException("Expected one affected note and outgoing operation.");
        Console.WriteLine("uncommitted");
        Console.Out.Flush();
        Thread.Sleep(TimeSpan.FromSeconds(30));
        return 1;
    }
}
