using Microsoft.Data.Sqlite;
using PaperDesk.Core;

namespace PaperDesk.Storage;

/// <summary>One isolated account database. Notes and immutable outgoing snapshots commit together.</summary>
public sealed class NoteStore : INoteStore, IDisposable
{
    private readonly SqliteConnection db;
    private readonly object gate = new();
    private readonly Guid owner;
    public event Action? Changed;
    static NoteStore() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    public NoteStore(string path, Guid ownerId = default)
    {
        owner = ownerId;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        using (var check = Command("PRAGMA quick_check"))
            if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException(L.T("База повреждена. Исходный файл сохранён; восстановите резервную копию в другом профиле."));
        Exec("""
            CREATE TABLE IF NOT EXISTS notes(id TEXT PRIMARY KEY, json TEXT NOT NULL, generation INTEGER NOT NULL, dirty INTEGER NOT NULL, conflict TEXT);
            CREATE TABLE IF NOT EXISTS outbox(note_id TEXT PRIMARY KEY, operation_id TEXT NOT NULL UNIQUE, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS placements(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS desktop_stacks(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS recovery(id INTEGER PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS purged(id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS note_images(note_id TEXT NOT NULL, id TEXT NOT NULL, png BLOB NOT NULL, layout TEXT, PRIMARY KEY(note_id,id));
            PRAGMA user_version=1;
            """);
        using var layoutColumn = Command("SELECT 1 FROM pragma_table_info('note_images') WHERE name='layout'");
        if (layoutColumn.ExecuteScalar() == null) Exec("ALTER TABLE note_images ADD COLUMN layout TEXT");
    }

    public IReadOnlyList<LocalNote> All()
    {
        lock (gate)
        {
            using var c = Command("SELECT json,generation,dirty,conflict FROM notes");
            using var r = c.ExecuteReader(); var list = new List<LocalNote>();
            while (r.Read()) list.Add(ReadLocal(r));
            return list.OrderByDescending(x => x.Note.UpdatedAt).ToList();
        }
    }
    public LocalNote? Find(Guid id)
    {
        lock (gate)
        {
            using var c = Command("SELECT json,generation,dirty,conflict FROM notes WHERE id=$id", ("$id", id.ToString()));
            using var r = c.ExecuteReader(); return r.Read() ? ReadLocal(r) : null;
        }
    }
    public void Save(Note note)
    {
        lock (gate) { using var tx = db.BeginTransaction(); SaveInternal(note); tx.Commit(); }
        Changed?.Invoke();
    }
    public void SaveText(Guid id, string? title, string? body, string shownTitle, string shownBody)
    {
        if (title == null && body == null) return;
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (Find(id) is not { } row) return;
            var current = row.Note;
            var updated = current with { Title = title ?? current.Title, Body = body ?? current.Body };
            if (updated == current) return;
            var divergence = (title != null && current.Title != shownTitle && current.Title != title)
                || (body != null && current.Body != shownBody && current.Body != body);
            if (divergence) Recover(current);
            if (divergence && !row.Dirty && current.Revision > 0)
                Put(new LocalNote(updated with { UpdatedAt = DateTimeOffset.UtcNow }, row.Generation + 1, true, current));
            else
                SaveInternal(updated);
            tx.Commit();
        }
        Changed?.Invoke();
    }
    private void SaveInternal(Note note)
    {
        if (IsPurged(note.Id)) return;
        var before = Find(note.Id);
        var n = note with { OwnerId = owner, Revision = before?.Note.Revision ?? 0, UpdatedAt = DateTimeOffset.UtcNow };
        var row = new LocalNote(n, (before?.Generation ?? 0) + 1, true, before?.Conflict);
        Put(row);
        if (row.Conflict == null && FindOutgoing(note.Id) is not { Rejected: false })
        {
            RemoveOutgoing(note.Id);
            Enqueue(row);
        }
    }
    private IReadOnlyList<Outgoing> Outbox()
    {
        lock (gate)
        {
            using var c = Command("SELECT json FROM outbox ORDER BY rowid");
            using var r = c.ExecuteReader(); var result = new List<Outgoing>();
            while (r.Read()) result.Add(Json.Read<Outgoing>(r.GetString(0)));
            return result;
        }
    }
    public IReadOnlyList<Outgoing> Pending() => Outbox().Where(op => !op.Rejected).ToList();
    public IReadOnlySet<Guid> RejectedNotes() => Outbox().Where(op => op.Rejected).Select(op => op.Note.Id).ToHashSet();

    public void MarkRejected(Outgoing operation)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (!HasOperation(operation) || Find(operation.Note.Id) is not { } row) return;
            if (row.Generation == operation.Generation)
                Exec("UPDATE outbox SET json=$json WHERE operation_id=$op", ("$json", Json.Write(operation with { Rejected = true })), ("$op", operation.OperationId.ToString()));
            else
            {
                RemoveOutgoing(operation.Note.Id);
                Enqueue(row);
            }
            tx.Commit();
        }
        Changed?.Invoke();
    }
    public void Acknowledge(Outgoing operation, Note server)
    {
        ValidateReply(operation, server);
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            if (!HasOperation(operation)) return;
            var current = Find(server.Id)!; RemoveOutgoing(server.Id);
            if (current.Generation == operation.Generation) Put(current with { Note = server, Dirty = false, Conflict = null });
            else { var next = current with { Note = current.Note with { Revision = server.Revision }, Conflict = null }; Put(next); Enqueue(next); }
            tx.Commit();
        }
        Changed?.Invoke();
    }
    public void SetConflict(Outgoing operation, Note server)
    {
        ValidateReply(operation, server);
        lock (gate)
        {
            using var tx = db.BeginTransaction(); if (!HasOperation(operation)) return;
            var current = Find(operation.Note.Id)!; Recover(server); Put(current with { Conflict = server }); RemoveOutgoing(server.Id); tx.Commit();
        }
        Changed?.Invoke();
    }
    public void ApplyRemote(Note server)
    {
        ValidateRemote(server);
        if (server.PurgedAt != null) { ApplyPurge([server.Id]); return; }
        lock (gate)
        {
            if (IsPurged(server.Id)) return;
            var current = Find(server.Id);
            if (current?.Dirty == true || (current != null && current.Note.Revision >= server.Revision)) return;
            Put(new LocalNote(server, current?.Generation ?? 0, false, null));
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<Note> ChangeStates(IEnumerable<Guid> ids, string action)
    {
        var changed = new List<Note>();
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in ids.Distinct())
            {
                if (Find(id)?.Note is not { } current) continue;
                var next = action switch
                {
                    "archive" when current.ArchivedAt == null && current.DeletedAt == null => current with { ArchivedAt = now },
                    "trash" when current.DeletedAt == null => current with { DeletedAt = now },
                    "restore" when current.ArchivedAt != null || current.DeletedAt != null => current with { ArchivedAt = null, DeletedAt = null },
                    _ => current
                };
                if (next == current) continue;
                changed.Add(current);
                SaveInternal(next);
                if (action == "restore" && GetPlacement(id) is { Visible: true } placement)
                    PutPlacement(placement with { Visible = false });
            }
            tx.Commit();
        }
        if (changed.Count > 0) Changed?.Invoke();
        return changed;
    }

    public void RestoreStates(IEnumerable<Note> previous)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            foreach (var note in previous)
                if (Find(note.Id)?.Note is { } current)
                    SaveInternal(current with { ArchivedAt = note.ArchivedAt, DeletedAt = note.DeletedAt });
            tx.Commit();
        }
        Changed?.Invoke();
    }

    public void PurgeTrash(IEnumerable<Guid> ids)
    {
        lock (gate)
        {
            var selected = ids.Distinct().ToArray();
            if (selected.Any(id => Find(id)?.Note.DeletedAt == null))
                throw new InvalidOperationException(L.T("Окончательно удалить можно только заметки из корзины."));
            ApplyPurge(selected);
        }
    }

    public void ApplyPurge(IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        lock (gate)
        {
            selected.RemoveWhere(IsPurged);
            if (selected.Count == 0) return;
            using var tx = db.BeginTransaction();
            foreach (var id in selected)
            {
                Exec("INSERT INTO purged(id) VALUES($id)", ("$id", id.ToString()));
                RemoveOutgoing(id);
                Exec("DELETE FROM notes WHERE id=$id; DELETE FROM placements WHERE id=$id; DELETE FROM note_images WHERE note_id=$id;", ("$id", id.ToString()));
            }
            var recoveryIds = new List<long>();
            using (var command = Command("SELECT id,json FROM recovery"))
            using (var rows = command.ExecuteReader())
                while (rows.Read())
                    if (selected.Contains(Json.Read<Note>(rows.GetString(1)).Id)) recoveryIds.Add(rows.GetInt64(0));
            foreach (var id in recoveryIds) Exec("DELETE FROM recovery WHERE id=$id", ("$id", id));
            tx.Commit();
        }
        Changed?.Invoke();
    }

    private bool IsPurged(Guid id)
    {
        using var command = Command("SELECT 1 FROM purged WHERE id=$id", ("$id", id.ToString()));
        return command.ExecuteScalar() != null;
    }
    public void Resolve(Guid id, string choice)
    {
        if (choice is not ("local" or "remote" or "both")) throw new ArgumentException(L.T("Неизвестный выбор версии"));
        lock (gate)
        {
            using var tx = db.BeginTransaction(); var current = Find(id)!;
            var remote = current.Conflict ?? throw new InvalidOperationException(L.T("Нет конфликта"));
            Recover(current.Note); Recover(remote); RemoveOutgoing(id);
            if (choice == "local") { var next = current with { Note = current.Note with { Revision = remote.Revision }, Conflict = null, Generation = current.Generation + 1 }; Put(next); Enqueue(next); }
            else { Put(current with { Note = remote, Dirty = false, Conflict = null }); if (choice == "both") SaveInternal(current.Note with { Id = Guid.NewGuid(), Revision = 0, Title = current.Note.Title + L.T(" (локальная копия)"), DeletedAt = null }); }
            tx.Commit();
        }
        Changed?.Invoke();
    }
    public Placement GetPlacement(Guid id)
    {
        lock (gate)
        {
            using var c = Command("SELECT json FROM placements WHERE id=$id", ("$id", id.ToString()));
            return c.ExecuteScalar() is string json ? SafePlacement(Json.Read<Placement>(json)) : new Placement { NoteId = id, StackId = null };
        }
    }
    public IReadOnlyDictionary<Guid, Placement> AllPlacements()
    {
        lock (gate)
        {
            using var c = Command("SELECT id,json FROM placements");
            using var r = c.ExecuteReader();
            var placements = new Dictionary<Guid, Placement>();
            while (r.Read()) placements.Add(Guid.Parse(r.GetString(0)), SafePlacement(Json.Read<Placement>(r.GetString(1))));
            return placements;
        }
    }
    public IReadOnlyList<DesktopStack> AllStacks()
    {
        lock (gate)
        {
            using var c = Command("SELECT json FROM desktop_stacks");
            using var r = c.ExecuteReader();
            var stacks = new List<DesktopStack>();
            while (r.Read()) stacks.Add(Json.Read<DesktopStack>(r.GetString(0)));
            return stacks;
        }
    }
    public DesktopStack? GetStack(Guid id)
    {
        lock (gate)
        {
            using var c = Command("SELECT json FROM desktop_stacks WHERE id=$id", ("$id", id.ToString()));
            return c.ExecuteScalar() is string json ? Json.Read<DesktopStack>(json) : null;
        }
    }
    // Layout stays on this device and has no effect on the outbox.
    public void SaveStack(DesktopStack stack)
    {
        lock (gate) PutStack(stack);
    }
    private void PutStack(DesktopStack stack)
    {
        var name = stack.Name?.Trim() ?? "";
        var bounds = SafePlacement(new Placement { X = stack.X, Y = stack.Y, Width = stack.Width, Height = stack.Height });
        var safe = stack with
        {
            Name = name[..Math.Min(name.Length, DesktopStack.MaxNameLength)],
            X = bounds.X, Y = bounds.Y,
            Width = double.IsFinite(bounds.Width) ? Math.Clamp(bounds.Width, 200, 1600) : 330,
            Height = double.IsFinite(bounds.Height) ? Math.Clamp(bounds.Height, 170, 1400) : 370
        };
        Exec("INSERT INTO desktop_stacks(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json",
            ("$id", safe.Id.ToString()), ("$json", Json.Write(safe)));
    }
    public void SavePlacement(Placement p)
    {
        lock (gate) PutPlacement(p);
        Changed?.Invoke();
    }
    public void SavePlacements(IEnumerable<Placement> placements, params DesktopStack[] stacks)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            foreach (var placement in placements) PutPlacement(placement);
            foreach (var stack in stacks) PutStack(stack);
            tx.Commit();
        }
        Changed?.Invoke();
    }
    private void PutPlacement(Placement p) => Exec(
        "INSERT INTO placements(id,json) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json",
        ("$id", p.NoteId.ToString()), ("$json", Json.Write(SafePlacement(p))));

    private static Placement SafePlacement(Placement p)
    {
        var defaults = new Placement();
        var color = p.Color;
        var validColor = color is { Length: 7 } && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
        return p with
        {
            Color = validColor ? color! : defaults.Color,
            Font = p.Font is "Segoe UI" or "Georgia" or "Consolas" ? p.Font : defaults.Font,
            X = double.IsFinite(p.X) && Math.Abs(p.X) < 1_000_000 ? p.X : defaults.X,
            Y = double.IsFinite(p.Y) && Math.Abs(p.Y) < 1_000_000 ? p.Y : defaults.Y,
            Width = double.IsFinite(p.Width) ? Math.Clamp(p.Width, 200, 1600) : defaults.Width,
            Height = double.IsFinite(p.Height) ? Math.Clamp(p.Height, 170, 1400) : defaults.Height
        };
    }
    public IReadOnlyList<Guid> ImageIds(Guid noteId) => ImageLayouts(noteId).Select(picture => picture.Id).ToArray();

    public IReadOnlyList<(Guid Id, NoteImageLayout? Layout)> ImageLayouts(Guid noteId)
    {
        lock (gate)
        {
            using var c = Command("SELECT id,layout FROM note_images WHERE note_id=$note ORDER BY rowid", ("$note", noteId.ToString()));
            using var rows = c.ExecuteReader();
            var pictures = new List<(Guid, NoteImageLayout?)>();
            while (rows.Read()) pictures.Add((Guid.Parse(rows.GetString(0)), ReadImageLayout(rows, 1)));
            return pictures;
        }
    }

    public IReadOnlySet<Guid> NotesWithImages()
    {
        lock (gate)
        {
            using var c = Command("SELECT DISTINCT note_id FROM note_images");
            using var rows = c.ExecuteReader();
            var ids = new HashSet<Guid>();
            while (rows.Read()) ids.Add(Guid.Parse(rows.GetString(0)));
            return ids;
        }
    }

    public byte[]? ReadImage(Guid noteId, Guid id)
    {
        lock (gate)
        {
            using var c = Command("SELECT png FROM note_images WHERE note_id=$note AND id=$id",
                ("$note", noteId.ToString()), ("$id", id.ToString()));
            return c.ExecuteScalar() as byte[];
        }
    }

    public bool AddImages(Guid noteId, IReadOnlyList<byte[]> pictures, NoteImageLayout? position = null)
    {
        if (pictures.Count == 0 || pictures.Any(png => !NoteImage.IsValid(png))) return false;
        lock (gate)
        {
            if (Find(noteId)?.Note is not { ArchivedAt: null, DeletedAt: null } ||
                pictures.Count > NoteImage.MaxPerNote - ImageIds(noteId).Count) return false;
            using var tx = db.BeginTransaction();
            for (var i = 0; i < pictures.Count; i++)
                PutImage(new NoteImage(noteId, Guid.NewGuid(), pictures[i], i == 0 ? position : null));
            tx.Commit();
        }
        Changed?.Invoke();
        return true;
    }

    public void SaveImageLayout(Guid noteId, Guid id, NoteImageLayout layout)
    {
        lock (gate)
        {
            if (Find(noteId)?.Note is not { ArchivedAt: null, DeletedAt: null }) return;
            Exec("UPDATE note_images SET layout=$layout WHERE note_id=$note AND id=$id",
                ("$layout", Json.Write(layout.Bounded())), ("$note", noteId.ToString()), ("$id", id.ToString()));
        }
        Changed?.Invoke();
    }

    public void RemoveImage(Guid noteId, Guid id)
    {
        lock (gate)
        {
            if (Find(noteId)?.Note is not { ArchivedAt: null, DeletedAt: null }) return;
            Exec("DELETE FROM note_images WHERE note_id=$note AND id=$id", ("$note", noteId.ToString()), ("$id", id.ToString()));
        }
        Changed?.Invoke();
    }

    private void PutImage(NoteImage picture) => Exec("INSERT INTO note_images(note_id,id,png,layout) VALUES($note,$id,$png,$layout)",
        ("$note", picture.NoteId.ToString()), ("$id", picture.Id.ToString()), ("$png", picture.Png),
        ("$layout", picture.Layout is { } layout ? Json.Write(layout.Bounded()) : null));

    private static NoteImageLayout? ReadImageLayout(SqliteDataReader row, int index) =>
        row.IsDBNull(index) ? null : Json.Read<NoteImageLayout>(row.GetString(index)).Bounded();

    public string Export()
    {
        lock (gate)
        {
            var notes = All().Select(x => x.Note).ToList();
            using var c = Command("SELECT json FROM recovery"); using var r = c.ExecuteReader(); var recovery = new List<Note>();
            while (r.Read()) recovery.Add(Json.Read<Note>(r.GetString(0)));
            using var pictures = Command("SELECT note_id,id,png,layout FROM note_images ORDER BY rowid");
            using var pictureRows = pictures.ExecuteReader();
            var images = new List<NoteImage>();
            while (pictureRows.Read())
                images.Add(new NoteImage(Guid.Parse(pictureRows.GetString(0)), Guid.Parse(pictureRows.GetString(1)),
                    (byte[])pictureRows[2], ReadImageLayout(pictureRows, 3)));
            return Json.Write(new Backup(1, DateTimeOffset.UtcNow, notes, notes.Select(n => GetPlacement(n.Id)).ToList(), recovery,
                AllStacks().ToList(), images.Count == 0 ? null : images));
        }
    }
    /// <summary>Import as local changes. Existing IDs are never overwritten.</summary>
    public int Import(string json)
    {
        var backup = Json.Read<Backup>(json);
        if (backup.Version != 1 || backup.Notes == null || backup.Notes.Any(n => n.Id == Guid.Empty || n.Title == null || n.Body == null)) throw new InvalidDataException(L.T("Неподдерживаемый формат резервной копии"));
        if (backup.Images is { } images && (images.Any(picture => picture is null || !NoteImage.IsValid(picture.Png)) ||
            images.GroupBy(picture => picture.NoteId).Any(group => group.Count() > NoteImage.MaxPerNote)))
            throw new InvalidDataException(L.T("В копии есть повреждённые или слишком большие изображения."));
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            var placements = (backup.Placements ?? []).ToLookup(p => p.NoteId);
            var existingStacks = AllStacks().Select(s => s.Id).ToHashSet();
            var importedStacks = (backup.Stacks ?? []).DistinctBy(s => s.Id).ToDictionary(s => s.Id,
                s => s.Id == Guid.Empty ? Guid.Empty : existingStacks.Contains(s.Id) ? Guid.NewGuid() : s.Id);
            var importedNotes = new Dictionary<Guid, Guid>();
            foreach (var note in backup.Notes)
            {
                if (note.PurgedAt != null) continue;
                var id = note.OwnerId == owner && Find(note.Id) == null && !IsPurged(note.Id) ? note.Id : Guid.NewGuid();
                SaveInternal(note with { Id = id, OwnerId = owner, Revision = 0 });
                importedNotes[note.Id] = id;
                var p = placements[note.Id].FirstOrDefault();
                if (p != null) PutPlacement(p with { NoteId = id,
                    StackId = p.StackId is Guid stackId ? importedStacks.GetValueOrDefault(stackId) : null });
            }
            foreach (var image in backup.Images ?? [])
                if (importedNotes.TryGetValue(image.NoteId, out var id))
                    PutImage(image with { NoteId = id, Id = Guid.NewGuid() });
            foreach (var stack in (backup.Stacks ?? []).DistinctBy(s => s.Id))
                if (stack.Id != Guid.Empty || !existingStacks.Contains(Guid.Empty))
                    PutStack(stack with { Id = importedStacks[stack.Id], ActiveNoteId = importedNotes.GetValueOrDefault(stack.ActiveNoteId) });
            foreach (var note in backup.RecoveryVersions ?? [])
                if (note.PurgedAt == null && !IsPurged(note.Id)) Recover(note);
            tx.Commit();
        }
        Changed?.Invoke(); return backup.Notes.Count;
    }
    private void ValidateRemote(Note note) { if (note.OwnerId != owner) throw new UnauthorizedAccessException(L.T("Заметка другого аккаунта отклонена")); }
    private void ValidateReply(Outgoing operation, Note note)
    {
        ValidateRemote(note);
        if (note.Id != operation.Note.Id) throw new InvalidDataException(L.T("Сервер вернул другую заметку для операции"));
    }
    private static LocalNote ReadLocal(SqliteDataReader r) => new(Json.Read<Note>(r.GetString(0)), r.GetInt64(1), r.GetBoolean(2), r.IsDBNull(3) ? null : Json.Read<Note>(r.GetString(3)));
    private void Put(LocalNote n) => Exec("INSERT INTO notes(id,json,generation,dirty,conflict) VALUES($id,$json,$g,$d,$c) ON CONFLICT(id) DO UPDATE SET json=$json,generation=$g,dirty=$d,conflict=$c", ("$id", n.Note.Id.ToString()), ("$json", Json.Write(n.Note)), ("$g", n.Generation), ("$d", n.Dirty ? 1 : 0), ("$c", n.Conflict == null ? null : Json.Write(n.Conflict)));
    private void Enqueue(LocalNote n) { var op = new Outgoing(Guid.NewGuid(), n.Note, n.Note.Revision, n.Generation); Exec("INSERT INTO outbox(note_id,operation_id,json) VALUES($id,$op,$json)", ("$id", n.Note.Id.ToString()), ("$op", op.OperationId.ToString()), ("$json", Json.Write(op))); }
    private Outgoing? FindOutgoing(Guid id)
    {
        using var c = Command("SELECT json FROM outbox WHERE note_id=$id", ("$id", id.ToString()));
        return c.ExecuteScalar() is string json ? Json.Read<Outgoing>(json) : null;
    }
    private bool HasOperation(Outgoing op) { using var c = Command("SELECT 1 FROM outbox WHERE operation_id=$op", ("$op", op.OperationId.ToString())); return c.ExecuteScalar() != null; }
    private void RemoveOutgoing(Guid id) => Exec("DELETE FROM outbox WHERE note_id=$id", ("$id", id.ToString()));
    private void Recover(Note n) => Exec("INSERT INTO recovery(json) VALUES($json)", ("$json", Json.Write(n)));
    private SqliteCommand Command(string sql, params (string, object?)[] values) { var c = db.CreateCommand(); c.CommandText = sql; foreach (var (key, value) in values) c.Parameters.AddWithValue(key, value ?? DBNull.Value); return c; }
    private void Exec(string sql, params (string, object?)[] values) { using var c = Command(sql, values); c.ExecuteNonQuery(); }
    public void Dispose() { lock (gate) db.Dispose(); }
}
