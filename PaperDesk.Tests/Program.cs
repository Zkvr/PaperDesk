using PaperDesk.Core;
using PaperDesk.Storage;
using System.Net;
using System.Text;

var tests = new (string Name, Action Run)[]
{
    ("Unicode checklist toggles only the clicked marker and preserves surrounding text", () => {
        var text="Покупки\n☐ Молоко\n☑ Хлеб\nобычная строка";
        Equal(2, Checklist.Read(text).Count); Equal("Молоко",Checklist.Read(text)[0].Label);
        Equal("Покупки\n☑ Молоко\n☑ Хлеб\nобычная строка", Checklist.SetChecked(text,1,true));
        Equal("Покупки\n☐ Молоко\n☐ Хлеб\nобычная строка", Checklist.SetChecked(text,2,false));
    }),
    ("Markdown checklist preserves indentation, bullets and CRLF", () => {
        var text="  - [ ] Задача 🌿\r\n* [X] Готово\r\n";
        Equal("  - [x] Задача 🌿\r\n* [X] Готово\r\n",Checklist.SetChecked(text,0,true));
        Equal("Готово",Checklist.Read(text)[1].Label); Equal(true,Checklist.Read(text)[1].IsChecked);
    }),
    ("A stale checklist click cannot rewrite a regular line", () => {
        Equal("Текст\nбез пункта",Checklist.SetChecked("Текст\nбез пункта",1,true));
        Equal("Текст",Checklist.SetChecked("Текст",5,true));
    }),
    ("Desktop stack cycles stably and skips archived or trashed notes", () => {
        var a=new Note{CreatedAt=DateTimeOffset.Parse("2026-01-01T00:00:00Z")};
        var b=new Note{CreatedAt=a.CreatedAt.AddMinutes(1)};
        var c=new Note{CreatedAt=a.CreatedAt.AddMinutes(2),ArchivedAt=DateTimeOffset.UtcNow};
        var d=new Note{CreatedAt=a.CreatedAt.AddMinutes(3),DeletedAt=DateTimeOffset.UtcNow};
        var notes=new[]{d,c,b,a}; Equal(2,NoteStack.Order(notes).Count);
        Equal(b.Id,NoteStack.Next(notes,a.Id,1)); Equal(a.Id,NoteStack.Next(notes,b.Id,1)); Equal(b.Id,NoteStack.Next(notes,a.Id,-1));
        Equal(b.Id,NoteStack.Next(new[]{a with{UpdatedAt=DateTimeOffset.UtcNow},b},a.Id,1));
        Equal(Guid.Empty,NoteStack.Next(Array.Empty<Note>(),a.Id,1));
    }),
    ("Desktop stack backup preserves loose notes, names and local bounds without overwriting existing stacks", () => WithStore(s => {
        Equal<Guid?>(Guid.Empty, Json.Read<Placement>("{}").StackId);
        var group = Guid.NewGuid();
        var a = new Note { Title = "In a stack" };
        var b = new Note { Title = "Loose sheet" };
        s.Save(a); s.Save(b);
        s.SavePlacements([new Placement { NoteId = a.Id, StackId = group, Visible = true },
            new Placement { NoteId = b.Id, StackId = null, Visible = true }]);
        var operations = s.Pending().ToArray();
        s.SaveStack(new DesktopStack(group, "Project", -230, 70, 300, 255, a.Id));
        Equal(true, operations.SequenceEqual(s.Pending()));
        var copy = s.Export();
        WithStore(other => {
            Equal(2, other.Import(copy));
            Equal<Guid?>(group, other.GetPlacement(a.Id).StackId);
            Equal<Guid?>(null, other.GetPlacement(b.Id).StackId);
            Equal(new DesktopStack(group, "Project", -230, 70, 300, 255, a.Id), other.GetStack(group));
            other.SaveStack(other.GetStack(group)! with { Name = "Existing project" });
            Equal(2, other.Import(copy));
            var imported = other.AllStacks().Single(x => x.Id != group);
            Equal("Existing project", other.GetStack(group)!.Name);
            Equal("Project", imported.Name);
            Equal<Guid?>(imported.Id, other.GetPlacement(imported.ActiveNoteId).StackId);
            Equal("In a stack", other.Find(imported.ActiveNoteId)!.Note.Title);
        });
    })),
    ("Offline text and operation survive restart", () => WithPath(path => {
        var n = new Note { Title = "Работа", Body = "Первый текст 🌿" };
        using (var s = new NoteStore(path)) s.Save(n);
        using var reopened = new NoteStore(path);
        Equal("Первый текст 🌿", reopened.Find(n.Id)!.Note.Body);
        Equal(1, reopened.Pending().Count);
    })),
    ("A delayed ACK preserves a newer local edit", () => WithStore(s => {
        var n = new Note { Body = "one" }; s.Save(n); var op = s.Pending().Single();
        s.Save(n with { Body = "two" }); s.Acknowledge(op, n with { Revision = 1 });
        Equal("two", s.Find(n.Id)!.Note.Body); Equal(true, s.Find(n.Id)!.Dirty);
        Equal(1L, s.Pending().Single().ExpectedRevision);
        Equal("two", s.Pending().Single().Note.Body);
    })),
    ("Retry uses the same immutable operation ID and payload", () => WithStore(s => {
        var n = new Note { Body = "one" }; s.Save(n); var op = s.Pending().Single();
        s.Save(n with { Body = "two" }); var retry = s.Pending().Single();
        Equal(op.OperationId, retry.OperationId); Equal("one", retry.Note.Body);
    })),
    ("Duplicate ACK cannot erase the next operation", () => WithStore(s => {
        var n = new Note { Body = "one" }; s.Save(n); var op = s.Pending().Single();
        s.Save(n with { Body = "two" }); var server = n with { Revision = 1 };
        s.Acknowledge(op, server); var nextId = s.Pending().Single().OperationId;
        s.Acknowledge(op, server); Equal(nextId, s.Pending().Single().OperationId);
        Equal("two", s.Find(n.Id)!.Note.Body);
    })),
    ("A mismatched cloud response cannot change another note or remove its queue", () => WithStore(s => {
        var a = new Note { Body = "a" }; var b = new Note { Body = "b" };
        s.Save(a); s.Save(b);
        var op = s.Pending().Single(x => x.Note.Id == a.Id);
        Throws<InvalidDataException>(() => s.Acknowledge(op, b with { Revision = 1 }));
        Throws<InvalidDataException>(() => s.SetConflict(op, b with { Revision = 1 }));
        Equal(2, s.Pending().Count);
        Equal("a", s.Find(a.Id)!.Note.Body); Equal("b", s.Find(b.Id)!.Note.Body);
    })),
    ("Remote deletion conflicts with dirty text and keeps both", () => WithStore(s => {
        var n = new Note { Body = "offline" }; s.Save(n); var op = s.Pending().Single();
        s.SetConflict(op, n with { Body = "remote", Revision = 2, DeletedAt = DateTimeOffset.UtcNow });
        Equal("offline", s.Find(n.Id)!.Note.Body); Equal("remote", s.Find(n.Id)!.Conflict!.Body);
        Equal(0, s.Pending().Count); s.Resolve(n.Id, "both");
        Equal(2, s.All().Count); Equal(true, s.All().Any(x => x.Note.Body == "offline" && x.Note.DeletedAt == null));
    })),
    ("Keeping local rebases on remote revision", () => WithStore(s => {
        var n = new Note { Body = "mine" }; s.Save(n);
        s.SetConflict(s.Pending().Single(), n with { Revision = 4, Body = "theirs" });
        s.Resolve(n.Id, "local"); Equal(4L, s.Pending().Single().ExpectedRevision);
        Equal("mine", s.Pending().Single().Note.Body);
    })),
    ("Remote pull never overwrites unsent text", () => WithStore(s => {
        var n = new Note { Body = "mine" }; s.Save(n);
        s.ApplyRemote(n with { Body = "theirs", Revision = 3 }); Equal("mine", s.Find(n.Id)!.Note.Body);
    })),
    ("Older remote revision cannot roll back a clean note", () => WithStore(s => {
        var n = new Note { Body = "new", Revision = 5 }; s.ApplyRemote(n);
        s.ApplyRemote(n with { Body = "old", Revision = 2 }); Equal("new", s.Find(n.Id)!.Note.Body);
    })),
    ("Remote content cannot change device placement", () => WithStore(s => {
        var n = new Note { Body = "text" }; s.SavePlacement(new Placement { NoteId = n.Id, X = -850, Color = "#D7F0DA", Visible = true });
        s.ApplyRemote(n with { Revision = 1 }); Equal(-850d, s.GetPlacement(n.Id).X); Equal(true, s.GetPlacement(n.Id).Visible);
    })),
    ("Stack visibility changes commit together and preserve note content", () => WithStore(s => {
        var notes = new[] { new Note { Body = "Первый" }, new Note { Body = "Второй" } };
        var placements = notes.Select(n => new Placement { NoteId = n.Id, Visible = true, Color = "#D7F0DA" }).ToArray();
        foreach (var note in notes) s.Save(note);
        s.SavePlacements(placements);
        IEnumerable<Placement> Interrupted()
        {
            yield return placements[0] with { Visible = false };
            throw new InvalidOperationException("Interrupted batch");
        }
        Throws<InvalidOperationException>(() => s.SavePlacements(Interrupted()));
        Equal(true, s.GetPlacement(notes[0].Id).Visible);
        s.SavePlacements(placements.Select(p => p with { Visible = false }));
        foreach (var note in notes)
        {
            Equal(false, s.GetPlacement(note.Id).Visible);
            Equal("#D7F0DA", s.GetPlacement(note.Id).Color);
            Equal(note.Body, s.Find(note.Id)!.Note.Body);
        }
    })),
    ("Backup roundtrip preserves Unicode, trash and recovery versions", () => WithStore(s => {
        var n = new Note { Title = "Поездка", Body = "# Список\nёжик 🦔\n<текст>", DeletedAt = DateTimeOffset.UtcNow }; s.Save(n);
        s.SetConflict(s.Pending().Single(), n with { Body = "server", Revision = 1 });
        s.SavePlacement(new Placement { NoteId = n.Id, X = 512 });
        using var other = new NoteStore(Path.Combine(Path.GetDirectoryName(Path.GetTempFileName())!, Guid.NewGuid() + ".db"));
        Equal(1, other.Import(s.Export())); Equal(n.Body, other.All().Single().Note.Body);
        Equal(true, other.All().Single().Note.DeletedAt != null);
        Equal(512d, other.GetPlacement(n.Id).X);
        Equal(true, Json.Read<Backup>(other.Export()).RecoveryVersions.Any(x => x.Body == "server"));
    })),
    ("Import collision keeps existing text as an independent copy", () => WithStore(s => {
        var n = new Note { Body = "original" }; s.Save(n); var backup = s.Export();
        s.Save(n with { Body = "edited" }); s.Import(backup);
        Equal(2, s.All().Count); Equal(true, s.All().Any(x => x.Note.Body == "edited"));
    })),
    ("Bulk archive is undoable and permanent deletion is limited to Trash", () => WithStore(s => {
        var a = new Note { Body = "do not keep this text" };
        var b = new Note { Body = "keep this note" };
        s.Save(a);
        s.Save(b);
        var previous = s.ChangeStates([a.Id, b.Id], "archive");
        Equal(2, previous.Count);
        Equal(true, s.All().All(row => row.Note.ArchivedAt != null));
        s.RestoreStates(previous);
        Equal(true, s.All().All(row => row.Note.ArchivedAt == null));
        s.ChangeStates([a.Id], "trash");
        Throws<InvalidOperationException>(() => s.PurgeTrash([a.Id, b.Id]));
        Equal(2, s.All().Count);
        s.PurgeTrash([a.Id]);
        s.ApplyRemote(a with { Revision = 99 });
        Equal(null, s.Find(a.Id));
        Equal(false, s.Export().Contains(a.Body));
        Equal(false, s.Pending().Any(op => op.Note.Id == a.Id));
    })),
    ("Foreign backup gets a new ID and safe placement", () => WithPath(path => {
        var foreign = new Note { OwnerId = Guid.NewGuid(), Body = "imported" };
        using var s = new NoteStore(path, Guid.NewGuid());
        var placement = new Placement { NoteId = foreign.Id, Visible = true, Color = null!, Font = null!, X = 1e100 };
        s.Import(Json.Write(new Backup(1, DateTimeOffset.UtcNow, [foreign], [placement], [])));
        var imported = s.All().Single().Note;
        Equal(false, foreign.Id == imported.Id);
        Equal(true, s.GetPlacement(imported.Id).Visible);
        Equal(new Placement().Color, s.GetPlacement(imported.Id).Color);
        Equal(new Placement().Font, s.GetPlacement(imported.Id).Font);
        Equal(new Placement().X, s.GetPlacement(imported.Id).X);
    })),
    ("Editing a field based on stale displayed text keeps both versions", () => WithStore(s => {
        var remote = new Note { Title = "Fresh title", Body = "Fresh body", Revision = 2 };
        s.ApplyRemote(remote);
        s.SaveText(remote.Id, "Local title", null, "Displayed old title", "Displayed old body");
        var row = s.Find(remote.Id)!;
        Equal("Local title", row.Note.Title);
        Equal("Fresh body", row.Note.Body);
        Equal("Fresh title", row.Conflict!.Title);
        Equal(0, s.Pending().Count);
    })),
    ("Profile rejects notes owned by a different account", () => WithPath(path => {
        using var s = new NoteStore(path, Guid.NewGuid());
        Throws<UnauthorizedAccessException>(() => s.ApplyRemote(new Note { OwnerId = Guid.NewGuid(), Revision = 1 }));
        Equal(0, s.All().Count);
    })),
    ("Network failure keeps the exact outgoing operation for retry", () => WithStore(s => {
        s.Save(new Note { Body = "survive" }); var op = s.Pending().Single();
        var cloud = new TestCloud { FailNext = true }; var engine = new SyncEngine(s, cloud);
        Throws<IOException>(() => engine.RunOnceAsync().GetAwaiter().GetResult());
        Equal(op.OperationId, s.Pending().Single().OperationId);
        engine.RunOnceAsync().GetAwaiter().GetResult(); Equal(0, s.Pending().Count); Equal(false, s.All().Single().Dirty);
    })),
    ("A rejected note cannot block other notes and a real edit replaces its rejected snapshot", () => WithStore(s => {
        var bad = new Note { Body = "unacceptable" }; var good = new Note { Body = "other edit" };
        s.Save(bad); s.Save(good);
        var oldOp = s.Pending().First();
        var cloud = new TestCloud { Reject = bad.Id };
        var remote = new Note { Body = "incoming", Revision = 1 }; cloud.Notes[remote.Id] = remote;
        var engine = new SyncEngine(s, cloud);
        Equal(1, engine.RunOnceAsync().GetAwaiter().GetResult());
        Equal("incoming", s.Find(remote.Id)!.Note.Body); Equal(true, cloud.Notes.ContainsKey(good.Id));
        Equal(0, s.Pending().Count); Equal(true, s.RejectedNotes().Contains(bad.Id));
        s.Save(bad with { Body = "shortened" });
        Equal(false, oldOp.OperationId == s.Pending().Single().OperationId);
        cloud.Reject = Guid.Empty;
        Equal(0, engine.RunOnceAsync().GetAwaiter().GetResult());
        Equal("shortened", cloud.Notes[bad.Id].Body);
    })),
    ("Server commit followed by lost response is idempotent on retry", () => WithStore(s => {
        s.Save(new Note { Body = "exactly once" }); var cloud = new TestCloud { LoseResponse = true }; var engine = new SyncEngine(s, cloud);
        Throws<IOException>(() => engine.RunOnceAsync().GetAwaiter().GetResult()); engine.RunOnceAsync().GetAwaiter().GetResult();
        Equal(1L, s.All().Single().Note.Revision); Equal(1, cloud.Notes.Count);
    })),
    ("Sync conflict preserves both versions and stops automatic sending", () => WithStore(s => {
        var n = new Note { Body = "offline" }; s.Save(n); var cloud = new TestCloud(); cloud.Notes[n.Id] = n with { Body = "remote", Revision = 1 };
        new SyncEngine(s, cloud).RunOnceAsync().GetAwaiter().GetResult();
        Equal("offline", s.Find(n.Id)!.Note.Body); Equal("remote", s.Find(n.Id)!.Conflict!.Body); Equal(0, s.Pending().Count);
    })),
    ("Damaged backup does not partially import", () => WithStore(s => {
        Throws<Exception>(() => s.Import("{broken")); Equal(0, s.All().Count);
    }))
};
var failures = 0;
foreach (var test in tests) { try { test.Run(); Console.WriteLine($"PASS {test.Name}"); } catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.Message}"); } }
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception($"Expected {typeof(T).Name}"); }
static void WithStore(Action<NoteStore> action) => WithPath(p => { using var s = new NoteStore(p); action(s); });
static void WithPath(Action<string> action) { var dir = Path.Combine(Path.GetTempPath(), "paperdesk-tests", Guid.NewGuid().ToString()); Directory.CreateDirectory(dir); try { action(Path.Combine(dir, "notes.db")); } finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(dir, true); } }

sealed class TestCloud : ICloudNotes
{
    public bool FailNext, LoseResponse;
    public Guid Reject;
    public Dictionary<Guid, Note> Notes = new();
    private readonly Dictionary<Guid, PushResult> operations = new();
    public Task<PushResult> PushAsync(Outgoing op, CancellationToken ct)
    {
        if (op.Note.Id == Reject) throw new CloudWriteRejectedException(new InvalidDataException("test rejection"));
        if (FailNext) { FailNext = false; throw new IOException("offline"); }
        if (operations.TryGetValue(op.OperationId, out var prior)) return Task.FromResult(prior);
        Notes.TryGetValue(op.Note.Id, out var old);
        var result = old != null && old.Revision != op.ExpectedRevision ? new PushResult("conflict", old) : new PushResult("ok", op.Note with { Revision = (old?.Revision ?? 0) + 1 });
        operations[op.OperationId] = result; if (result.Status == "ok") Notes[op.Note.Id] = result.Note!;
        if (LoseResponse) { LoseResponse = false; throw new IOException("response lost after commit"); }
        return Task.FromResult(result);
    }
    public Task<IReadOnlyList<Note>> FetchAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Note>>(Notes.Values.ToList());
}

sealed class WireHandler(Func<HttpRequestMessage, Task<string>> respond) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => new(HttpStatusCode.OK) { Content = new StringContent(await respond(request), Encoding.UTF8, "application/json") };
}
