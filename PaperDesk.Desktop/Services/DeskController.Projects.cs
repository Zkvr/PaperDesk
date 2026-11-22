namespace PaperDesk.Desktop.Services;

public sealed partial class DeskController
{
    private static readonly Guid landingExample = new("b4ef3c9b-c129-4ec0-8b37-d82afaa5fd72");
    private static readonly Guid reportExample = new("36f9ee85-58aa-4baa-de21-c56f490178bd");

    internal static bool IsProjectExample(Guid id) => id == landingExample || id == reportExample;

    internal string? ResumeProject(Guid id)
    {
        if (!Settings.StackedNotes || id == Guid.Empty || draggedSheet != null ||
            ProjectSource.Normalize(Store.GetStack(id)?.ProjectSource) is not { } source)
            return null;

        var others = Store.AllStacks()
            .Where(stack => stack.Id != id && stack.Id != Guid.Empty && ProjectSource.Normalize(stack.ProjectSource) != null)
            .Select(stack => stack.Id).ToHashSet();
        return RevealStack(id, others) ? source : null;
    }

    internal void SetProjectSource(Guid id, string? source)
    {
        if (!Settings.StackedNotes || id == Guid.Empty || Store.GetStack(id) is not { } stack) return;
        var target = ProjectSource.Normalize(source);
        if ((source != null && target == null) || string.Equals(stack.ProjectSource, target, StringComparison.Ordinal)) return;
        Store.SavePlacements([], stack with { ProjectSource = target });
    }

    internal Guid? CreateProject(string name, string source)
    {
        if (!Settings.StackedNotes || string.IsNullOrWhiteSpace(name) || ProjectSource.Normalize(source) is not { } target)
            return null;
        var id = Guid.NewGuid();
        SaveProjects([(id, name, target, [new Note { Title = L.T("Следующий шаг") }])]);
        return id;
    }

    internal bool HasProjectExamples => Store.GetStack(landingExample) != null && Store.GetStack(reportExample) != null;

    internal Guid? AddProjectExamples()
    {
        if (!Settings.StackedNotes || HasProjectExamples) return null;
        var folder = Path.Combine(Path.GetDirectoryName(BackupFolder)!, "project-examples");
        Directory.CreateDirectory(folder);
        var projects = new List<(Guid Id, string Name, string Source, Note[] Notes)>();
        Add(landingExample, L.T("Пример · Лендинг"), "landing-brief.txt",
            L.T("Бриф лендинга\n\nНа первом экране должны быть новая цена и кнопка заявки. Проверьте мобильную версию перед отправкой."),
            new Note { Title = L.T("Правки лендинга"), Body = L.T("- [ ] Обновить цену на первом экране\n- [ ] Проверить кнопку заявки") },
            new Note { Title = L.T("Перед отправкой"), Body = L.T("Открыть мобильный вид и проверить, что цена видна сразу.") });
        Add(reportExample, L.T("Пример · Отчёт"), "report-checklist.txt",
            L.T("Недельный отчёт\n\nСверьте итоговые цифры и приложите последнюю версию таблицы."),
            new Note { Title = L.T("Собрать отчёт"), Body = L.T("- [ ] Сверить итоговые цифры\n- [ ] Приложить таблицу") },
            new Note { Title = L.T("Перед отправкой"), Body = L.T("Проверить даты отчёта и подписи к таблице.") });

        SaveProjects(projects);
        return projects[0].Id;

        void Add(Guid id, string name, string file, string text, params Note[] notes)
        {
            if (Store.GetStack(id) != null) return;
            var path = Path.Combine(folder, file);
            if (!File.Exists(path)) File.WriteAllText(path, text);
            projects.Add((id, name, path, notes));
        }
    }

    private void SaveProjects(IEnumerable<(Guid Id, string Name, string Source, Note[] Notes)> projects)
    {
        var occupied = OccupiedBounds();
        var backup = new Backup(1, DateTimeOffset.UtcNow, [], [], [], []);
        foreach (var (id, name, source, notes) in projects)
        {
            var bounds = FreeSpace(330, 370, occupied);
            occupied.Add(bounds);
            backup.Notes.AddRange(notes);
            backup.Placements.AddRange(notes.Select(note => new Placement { NoteId = note.Id, StackId = id, Topmost = true }));
            backup.Stacks!.Add(new DesktopStack(id, name, bounds.X, bounds.Y, bounds.Width, bounds.Height, notes[0].Id)
                { ProjectSource = source, SheetOrder = notes.Select(note => note.Id).ToArray() });
        }
        Store.Import(Json.Write(backup));
    }
}
