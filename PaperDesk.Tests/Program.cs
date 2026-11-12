using PaperDesk.Core;
using PaperDesk.Storage;
using System.Net;
using System.Text;

if (args is ["--interrupted-write", var interruptedPath])
    return InterruptedWriteCheck.WriteUntilInterrupted(interruptedPath);

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
    ("Killing a writer preserves the last committed text and immutable outgoing operation",
        () => WithPath(InterruptedWriteCheck.Run)),
    ("An invalid SQLite file is preserved and can be moved after a failed open", () => WithPath(path => {
        var bytes = Encoding.UTF8.GetBytes("synthetic damaged SQLite file");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var damaged = new NoteStore(path);
            throw new Exception("A damaged SQLite file was opened");
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
        Equal(true, File.ReadAllBytes(path).SequenceEqual(bytes));
    })),
    ("A failed SQLite schema upgrade rolls back and can be retried", () => WithPath(path => {
        var note = new Note { Body = "keep the existing data" };
        using (var store = new NoteStore(path)) store.Save(note);

        using var database = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = """
            DROP TABLE desktop_stacks;
            DROP TABLE note_images;
            CREATE VIEW note_images AS SELECT 1 AS unexpected;
            PRAGMA user_version=0;
            """;
        command.ExecuteNonQuery();

        Throws<Microsoft.Data.Sqlite.SqliteException>(() => { using var failed = new NoteStore(path); });
        command.CommandText = "PRAGMA user_version";
        Equal(0L, (long)command.ExecuteScalar()!);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='desktop_stacks'";
        Equal(0L, (long)command.ExecuteScalar()!);

        command.CommandText = """
            DROP VIEW note_images;
            CREATE TABLE note_images(note_id TEXT NOT NULL, id TEXT NOT NULL, png BLOB NOT NULL, PRIMARY KEY(note_id,id));
            """;
        command.ExecuteNonQuery();
        using var reopened = new NoteStore(path);
        Equal(note.Body, reopened.Find(note.Id)!.Note.Body);
        Equal(note.Id, reopened.Pending().Single().Note.Id);
        Equal(0, reopened.ImageLayouts(note.Id).Count);
        command.CommandText = "PRAGMA user_version";
        Equal(1L, (long)command.ExecuteScalar()!);
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
        s.SavePlacement(new Placement { NoteId = n.Id, X = 512, Texture = "linen", Corners = "round", Shadow = "none", Opacity = .92 });
        using var other = new NoteStore(Path.Combine(Path.GetDirectoryName(Path.GetTempFileName())!, Guid.NewGuid() + ".db"));
        Equal(1, other.Import(s.Export())); Equal(n.Body, other.All().Single().Note.Body);
        Equal(true, other.All().Single().Note.DeletedAt != null);
        var restored = other.GetPlacement(n.Id);
        Equal(512d, restored.X);
        Equal("linen", restored.Texture);
        Equal("round", restored.Corners);
        Equal("none", restored.Shadow);
        Equal(.92, restored.Opacity);
        Equal(true, Json.Read<Backup>(other.Export()).RecoveryVersions.Any(x => x.Body == "server"));
    })),
    ("Older placements retain the paper defaults and invalid appearance is normalized", () => WithStore(s => {
        var old = Json.Read<Placement>("{\"color\":\"#DCEBFF\"}");
        Equal("fibres", old.Texture); Equal("folded", old.Corners); Equal("soft", old.Shadow); Equal(1d, old.Opacity);
        var note = new Note { Body = "Paper" }; s.Save(note);
        s.SavePlacement(old with { NoteId = note.Id, Texture = "other", Corners = "other", Shadow = "other", Opacity = .2 });
        var saved = s.GetPlacement(note.Id);
        Equal("fibres", saved.Texture); Equal("folded", saved.Corners); Equal("soft", saved.Shadow);
        Equal(Placement.MinimumOpacity, saved.Opacity); Equal("#DCEBFF", saved.Color);
    })),
    ("Exported files preserve the backup and Markdown formats", () => WithPath(path => {
        using var s = new NoteStore(path);
        var note = new Note { Title = "Заголовок", Body = "Текст 🌿" };
        s.Save(note);
        var folder = Path.GetDirectoryName(path)!;
        var backup = Path.Combine(folder, "backup.json");
        var markdown = Path.Combine(folder, "notes.md");
        File.WriteAllText(backup, "old backup");
        s.ExportFile(backup);
        s.ExportFile(markdown, markdown: true);
        Equal("# Заголовок\n\nТекст 🌿", File.ReadAllText(markdown));
        using var restored = new NoteStore(Path.Combine(folder, "restored.db"));
        Equal(1, restored.ImportFiles([backup]));
        Equal(note.Body, restored.Find(note.Id)!.Note.Body);
    })),
    ("Failed export replacement leaves the previous backup and removes the temporary file", () => WithPath(path => {
        using var s = new NoteStore(path);
        s.Save(new Note { Body = "New contents" });
        var folder = Path.GetDirectoryName(path)!;
        var backup = Path.Combine(folder, "backup.json");
        File.WriteAllText(backup, "previous backup");
        var replacementRejected = false;
        using (File.Open(backup, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            try { s.ExportFile(backup); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { replacementRejected = true; }
        }
        Equal(true, replacementRejected);
        Equal("previous backup", File.ReadAllText(backup));
        Equal(0, Directory.GetFiles(folder, ".paperdesk-export-*.tmp").Length);
    })),
    ("Import collision keeps the existing note and moves history, images and placement to the copy", () => WithStore(s => {
        var note = new Note { Body = "original" };
        var stack = new DesktopStack(Guid.NewGuid(), "Imported stack", 10, 20, 310, 330, note.Id);
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+V04cAAAAASUVORK5CYII=");
        var layout = new NoteImageLayout(.2, .3, .4, ImageTextMode.Inline, 1);
        var backup = new Backup(1, DateTimeOffset.UtcNow, [note],
            [new Placement { NoteId = note.Id, StackId = stack.Id }], [note with { Body = "previous" }],
            [stack], [new NoteImage(note.Id, Guid.NewGuid(), png, layout)]);
        s.Save(note with { Body = "edited" });
        s.SaveStack(stack with { Name = "Existing stack" });

        Equal(1, s.Import(Json.Write(backup)));

        Equal("edited", s.Find(note.Id)!.Note.Body);
        var copy = s.All().Single(row => row.Note.Id != note.Id).Note;
        Equal("original", copy.Body);
        var copiedStack = s.AllStacks().Single(group => group.Id != stack.Id);
        Equal("Existing stack", s.GetStack(stack.Id)!.Name);
        Equal(copy.Id, copiedStack.ActiveNoteId);
        Equal<Guid?>(copiedStack.Id, s.GetPlacement(copy.Id).StackId);
        var image = s.ImageLayouts(copy.Id).Single();
        Equal(layout, image.Layout);
        Equal(true, png.SequenceEqual(s.ReadImage(copy.Id, image.Id)!));
        Equal(0, s.ImageIds(note.Id).Count);
        var previous = Json.Read<Backup>(s.Export()).RecoveryVersions.Single();
        Equal(copy.Id, previous.Id);
        Equal("previous", previous.Body);
    })),
    ("Duplicating a note copies paper styling and attachments into an independent draft", () => WithStore(s => {
        var source = new Note { Title = "Plan", Body = "- [x] Keep the original\n\n**Text**" };
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+V04cAAAAASUVORK5CYII=");
        var layout = new NoteImageLayout(.2, .3, .4, ImageTextMode.Wrap, 1);
        s.Save(source);
        var outgoing = s.Pending().Single();
        s.SavePlacement(new Placement { NoteId = source.Id, Visible = true, Favorite = true,
            Topmost = true, Color = "#DDF0D5", FontSize = 22, Texture = "smooth", Corners = "square", Shadow = "deep", Opacity = .88 });
        Equal(true, s.AddImages(source.Id, [png], layout));
        var originalPicture = s.ImageIds(source.Id).Single();
        var changes = 0;
        s.Changed += () => changes++;

        var id = s.Duplicate(source.Id) ?? throw new Exception("The original must be duplicable.");

        Equal(1, changes);
        Equal(true, id != source.Id);
        var copy = s.Find(id)!.Note;
        Equal(source.Body, copy.Body);
        Equal(L.F("{0} — копия", source.Title), copy.Title);
        var placement = s.GetPlacement(id);
        Equal("#DDF0D5", placement.Color);
        Equal(22d, placement.FontSize);
        Equal("smooth", placement.Texture); Equal("square", placement.Corners); Equal("deep", placement.Shadow); Equal(.88, placement.Opacity);
        Equal<Guid?>(null, placement.StackId);
        Equal(false, placement.Visible || placement.Favorite || placement.Topmost);
        var picture = s.ImageLayouts(id).Single();
        Equal(layout, picture.Layout);
        Equal(true, picture.Id != originalPicture && png.SequenceEqual(s.ReadImage(id, picture.Id)!));
        s.RemoveImage(id, picture.Id);
        Equal(originalPicture, s.ImageIds(source.Id).Single());
        Equal(outgoing, s.Pending().Single(op => op.Note.Id == source.Id));
        Equal(id, s.Pending().Single(op => op.Note.Id != source.Id).Note.Id);
        Equal<Guid?>(null, s.Duplicate(Guid.NewGuid()));
    })),
    ("Legacy orphan history does not attach to an existing note or keep a foreign owner", () => WithStore(s => {
        var note = new Note { Body = "existing" };
        s.Save(note);
        var orphan = note with { OwnerId = Guid.NewGuid(), Body = "legacy history" };
        var backup = new Backup(1, DateTimeOffset.UtcNow, [], [],
            [orphan, orphan with { Body = "another snapshot" }]);

        Equal(0, s.Import(Json.Write(backup)));

        var history = Json.Read<Backup>(s.Export()).RecoveryVersions;
        Equal(2, history.Count);
        Equal(1, history.Select(version => version.Id).Distinct().Count());
        Equal(false, history.Any(version => version.Id == note.Id || version.OwnerId != note.OwnerId));
        Equal("existing", s.All().Single().Note.Body);
    })),
    ("Ambiguous backup IDs leave notes and queued writes intact", () => WithStore(s => {
        var existing = new Note { Body = "existing" };
        var duplicate = new Note { Body = "first" };
        s.Save(existing);
        var operations = s.Pending().ToArray();
        var backup = new Backup(1, DateTimeOffset.UtcNow,
            [duplicate, duplicate with { Body = "second" }], [], []);

        Throws<InvalidDataException>(() => s.Import(Json.Write(backup)));

        Equal(existing.Id, s.All().Single().Note.Id);
        Equal(true, operations.SequenceEqual(s.Pending()));
    })),
    ("A later failed file rolls back the entire import selection", () => WithPath(path => {
        using var s = new NoteStore(path);
        var existing = new Note { Body = "existing" };
        s.Save(existing);
        var operation = s.Pending().Single();
        var changes = 0;
        s.Changed += () => changes++;
        var folder = Path.GetDirectoryName(path)!;
        var markdown = Path.Combine(folder, "first.md");
        var json = Path.Combine(folder, "second.json");
        var damaged = Path.Combine(folder, "damaged.json");
        var note = new Note { Body = "backup body" };
        var stack = new DesktopStack(Guid.NewGuid(), "Backup stack", 10, 20, 310, 330, note.Id);
        File.WriteAllText(markdown, "# Imported note");
        File.WriteAllText(json, Json.Write(new Backup(1, DateTimeOffset.UtcNow, [note],
            [new Placement { NoteId = note.Id, StackId = stack.Id }], [note with { Body = "old history" }], [stack])));
        File.WriteAllText(damaged, "{broken");

        Exception? error = null;
        try { s.ImportFiles([markdown, json, damaged]); }
        catch (Exception ex) { error = ex; }

        Equal(1, s.All().Count);
        Equal(existing.Id, s.All().Single().Note.Id);
        Equal(operation, s.Pending().Single());
        Equal(0, s.AllStacks().Count);
        Equal(0, Json.Read<Backup>(s.Export()).RecoveryVersions.Count);
        Equal(0, changes);
        Equal(true, error is InvalidDataException && error.Message.StartsWith("damaged.json\n", StringComparison.Ordinal));
    })),
    ("Mixed files commit together and notify once", () => WithPath(path => {
        var folder = Path.GetDirectoryName(path)!;
        var text = Path.Combine(folder, "Заметка.txt");
        var json = Path.Combine(folder, "second.JSON");
        var note = new Note { Body = "backup body" };
        File.WriteAllText(text, "Привет 🌿", Encoding.Unicode);
        File.WriteAllText(json, Json.Write(new Backup(1, DateTimeOffset.UtcNow, [note], [], [])));

        using (var s = new NoteStore(path))
        {
            var changes = 0;
            s.Changed += () => changes++;
            Equal(2, s.ImportFiles([text, json]));
            Equal(1, changes);
        }

        using var reopened = new NoteStore(path);
        Equal("Привет 🌿", reopened.All().Single(row => row.Note.Title == "Заметка").Note.Body);
        Equal(note.Body, reopened.Find(note.Id)!.Note.Body);
        Equal(2, reopened.Pending().Count);
    })),
    ("JSON file import accepts UTF-8, UTF-16 and UTF-32 backups", () => WithPath(path => {
        using var s = new NoteStore(path);
        var source = Path.Combine(Path.GetDirectoryName(path)!, "legacy.json");
        var body = "Старая копия 🌿";
        var backup = Json.Write(new Backup(1, DateTimeOffset.UtcNow, [new Note { Body = body }], [], []));
        Encoding[] encodings = [new UTF8Encoding(false), Encoding.UTF8, Encoding.Unicode,
            Encoding.BigEndianUnicode, Encoding.UTF32, new UTF32Encoding(true, true)];
        foreach (var encoding in encodings)
        {
            File.WriteAllText(source, backup, encoding);
            Equal(1, s.ImportFiles([source]));
        }
        Equal(encodings.Length, s.All().Count);
        Equal(true, s.All().All(row => row.Note.Body == body));
    })),
    ("Oversized text import rolls back earlier files", () => WithPath(path => {
        using var s = new NoteStore(path);
        var folder = Path.GetDirectoryName(path)!;
        var first = Path.Combine(folder, "first.md");
        var oversized = Path.Combine(folder, "oversized.txt");
        File.WriteAllText(first, "Small file");
        using (var file = File.Create(oversized)) file.SetLength(16L * 1024 * 1024 + 1);

        Throws<InvalidDataException>(() => s.ImportFiles([first, oversized]));
        Equal(0, s.All().Count);
        Equal(0, s.Pending().Count);
    })),
    ("A large version 1 backup rolls back until explicitly confirmed and can then be restored", () => WithPath(path => {
        using var s = new NoteStore(path);
        var folder = Path.GetDirectoryName(path)!;
        var markdown = Path.Combine(folder, "first.md");
        var backupPath = Path.Combine(folder, "large.json");
        var note = new Note { Body = "Legacy backup" };
        File.WriteAllText(markdown, "First file");
        File.WriteAllText(backupPath, Json.Write(new Backup(1, DateTimeOffset.UtcNow, [note], [], [])));
        using (var large = File.Open(backupPath, FileMode.Append))
        {
            var padding = new byte[1024 * 1024];
            Array.Fill(padding, (byte)' ');
            for (var i = 0; i < LargeBackupException.LimitMiB; i++) large.Write(padding);
        }

        Throws<LargeBackupException>(() => s.ImportFiles([markdown, backupPath]));
        Equal(0, s.All().Count);
        Equal(0, s.Pending().Count);
        Equal(2, s.ImportFiles([markdown, backupPath], allowLargeBackup: true));
        Equal(note.Body, s.Find(note.Id)!.Note.Body);
        Equal(2, s.Pending().Count);
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
        Throws<System.Text.Json.JsonException>(() => s.Import("{broken"));
        var backup = new Backup(1, DateTimeOffset.UtcNow, [new Note()], [], []);
        Throws<InvalidDataException>(() => s.Import(Json.Write(backup with { Placements = [null!] })));
        Throws<InvalidDataException>(() => s.Import(Json.Write(backup with { Stacks = [null!] })));
        Equal(0, s.All().Count);
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
