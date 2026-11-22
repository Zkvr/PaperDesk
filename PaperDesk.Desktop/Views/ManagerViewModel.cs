using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Controls;

namespace PaperDesk.Desktop.ViewModels;

public sealed record NoteCard(Guid Id, string Title, string Preview, string Date, string Color, string State,
    bool Conflict, bool Favorite, bool OnDesktop, string StackName, string Texture, string Corners)
{
    public string Section => L.T(OnDesktop ? "На столе" : "Не на столе");
    public bool HasStack => StackName.Length != 0;
}
public sealed class ManagerViewModel : INotifyPropertyChanged
{
    private readonly DeskController desk;
    private readonly CardCollection notes = new();
    public ReadOnlyObservableCollection<NoteCard> Notes { get; }

    public ManagerViewModel(DeskController desk)
    {
        this.desk = desk;
        Notes = new(notes);
    }
    public string StorageLocation => desk.StorageLocation;
    public string Status => desk.Status;
    public string Count => Notes.Count.ToString();
    public int AllCount { get; private set; }
    public int VisibleCount { get; private set; }
    public int OffDeskCount => AllCount - VisibleCount;
    public int FavoriteCount { get; private set; }
    public int ArchiveCount { get; private set; }
    public int TrashCount { get; private set; }
    public bool HasConflicts { get; private set; }
    public bool HasDesktopStacks { get; private set; }
    public string Filter { get; set; } = "all";
    public string Search { get; set; } = "";
    public int Sort { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private NoteStore? source;
    private Dictionary<Guid, CachedCard> cached = [];
    private HashSet<Guid> stackIds = [];

    public void Refresh(IReadOnlyList<Guid>? edits = null)
    {
        var store = desk.Store;
        if (edits == null || source != store)
        {
            var placements = store.AllPlacements();
            var stacks = store.AllStacks().ToDictionary(stack => stack.Id, stack => stack.Name);
            stacks[Guid.Empty] = L.T("Общая стопка");
            stackIds = stacks.Keys.ToHashSet();
            var rejected = store.RejectedNotes();
            var pictures = store.NotesWithImages();
            var next = new Dictionary<Guid, CachedCard>();
            foreach (var row in store.All())
            {
                var n = row.Note;
                var p = placements.GetValueOrDefault(n.Id) ?? new Placement { NoteId = n.Id, StackId = null };
                var onDesktop = IsActive(row) && desk.IsOnDesktop(p);
                var stackName = onDesktop && desk.Settings.StackedNotes && p.StackId is Guid group
                    ? stacks.GetValueOrDefault(group) ?? "" : "";
                var previous = source == store ? cached.GetValueOrDefault(n.Id) : null;
                var preview = Preview(row, previous, pictures.Contains(n.Id));
                next.Add(n.Id, new(row, CreateCard(row, p, onDesktop, stackName, preview, rejected.Contains(n.Id)), p));
            }
            cached = next;
            source = store;
        }
        else
        {
            foreach (var id in edits)
            {
                if (!cached.TryGetValue(id, out var previous) || store.Find(id) is not { } row)
                {
                    Refresh();
                    return;
                }
                var preview = Preview(row, previous, string.IsNullOrWhiteSpace(row.Note.Body) && store.ImageIds(id).Count > 0);
                cached[id] = new(row, CreateCard(row, previous.Placement, previous.Card.OnDesktop,
                    previous.Card.StackName, preview, store.IsRejected(id)), previous.Placement);
            }
        }

        var all = cached.Values;
        AllCount = all.Count(item => IsActive(item.Row));
        VisibleCount = all.Count(item => item.Card.OnDesktop);
        FavoriteCount = all.Count(item => IsActive(item.Row) && item.Card.Favorite);
        ArchiveCount = all.Count(item => item.Row.Note is { DeletedAt: null, ArchivedAt: not null });
        TrashCount = all.Count(item => item.Row.Note.DeletedAt != null);
        HasConflicts = all.Any(item => item.Card.Conflict);
        HasDesktopStacks = desk.Settings.StackedNotes && all.Any(item => IsActive(item.Row) &&
            item.Placement.StackId is Guid group && stackIds.Contains(group));
        var rows = all.Where(item =>
        {
            var n = item.Row.Note;
            var match = Filter switch
            {
                "trash" => n.DeletedAt != null,
                "archive" => n.ArchivedAt != null && n.DeletedAt == null,
                "visible" => item.Card.OnDesktop,
                "hidden" => IsActive(item.Row) && !item.Card.OnDesktop,
                "favorite" => IsActive(item.Row) && item.Card.Favorite,
                "conflict" => item.Card.Conflict,
                _ => IsActive(item.Row)
            };
            return match && (n.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || n.Body.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
        });
        rows = Sort switch
        {
            1 => rows.OrderBy(item => item.Row.Note.Title).ThenByDescending(item => item.Row.Note.UpdatedAt),
            2 => rows.OrderByDescending(item => item.Row.Note.CreatedAt).ThenByDescending(item => item.Row.Note.UpdatedAt),
            _ => rows.OrderByDescending(item => item.Row.Note.UpdatedAt)
        };
        if (Filter == "all")
            rows = rows.OrderByDescending(item => item.Card.OnDesktop).ThenByDescending(item => item.Card.Favorite);
        else if (Filter is "visible" or "hidden")
            rows = rows.OrderByDescending(item => item.Card.Favorite);
        notes.Update(rows.Select(item => item.Card).ToList());
        foreach (var prop in new[] { nameof(StorageLocation), nameof(Status), nameof(Count), nameof(AllCount), nameof(VisibleCount), nameof(OffDeskCount), nameof(FavoriteCount), nameof(ArchiveCount), nameof(TrashCount) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    private sealed record CachedCard(LocalNote Row, NoteCard Card, Placement Placement);

    private static bool IsActive(LocalNote row) => row.Note is { ArchivedAt: null, DeletedAt: null };

    private static string Preview(LocalNote row, CachedCard? previous, bool hasImages) =>
        string.IsNullOrWhiteSpace(row.Note.Body) ? L.T(hasImages ? "Изображения" : "Пустой листик") :
        previous?.Row.Note.Body == row.Note.Body ? previous.Card.Preview : FormatPreview(row.Note.Body);

    private static NoteCard CreateCard(LocalNote row, Placement p, bool onDesktop, string stackName, string preview, bool rejected)
    {
        var n = row.Note;
        return new NoteCard(n.Id, string.IsNullOrWhiteSpace(n.Title) ? L.T("Без названия") : n.Title, preview,
            n.UpdatedAt.ToLocalTime().ToString("dd MMM, HH:mm"), PaperPalette.Normalize(p.Color),
            row.Conflict != null ? L.T("⚠ Конфликт") : rejected ? L.T("⚠ Правка отклонена") : "",
            row.Conflict != null, p.Favorite, onDesktop, stackName, p.Texture, p.Corners);
    }

    private static string FormatPreview(string text)
    {
        var preview = NoteSpoilers.Preview(text);
        var tasks = Checklist.Read(preview).ToDictionary(task => task.LineIndex);
        var lines = preview.Split('\n');
        var summary = new List<string>(8);
        for (var index = 0; index < lines.Length && summary.Count < 8; index++)
        {
            var line = lines[index];
            if (tasks.TryGetValue(index, out var task))
            {
                summary.Add((task.IsChecked ? "☑ " : "☐ ") + new PaperInline(task.Label).Text);
                continue;
            }
            if (index + 1 < lines.Length && NoteBodyEditor.TableCells(line) is { } cells &&
                NoteBodyEditor.TableCells(lines[index + 1]) is { } divider && divider.Length == cells.Length &&
                NoteBodyEditor.IsTableDivider(divider))
            {
                var headings = string.Join(" · ", cells.Select(cell => new PaperInline(cell).Text)
                    .Where(label => !string.IsNullOrWhiteSpace(label)).Take(3));
                summary.Add(headings.Length == 0 ? L.T("▦  Таблица") : "▦  " + headings);
                index++;
                while (index + 1 < lines.Length && NoteBodyEditor.TableCells(lines[index + 1])?.Length == cells.Length) index++;
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = "• " + line[2..];
            else if (line.StartsWith("## ", StringComparison.Ordinal)) line = line[3..];
            else if (line.StartsWith("> ", StringComparison.Ordinal)) line = "│ " + line[2..];
            summary.Add(new PaperInline(line).Text);
        }
        return string.Join('\n', summary);
    }

    private sealed class CardCollection : ObservableCollection<NoteCard>
    {
        public void Update(IReadOnlyList<NoteCard> cards)
        {
            if (Count == cards.Count)
            {
                var first = 0;
                while (first < Count && this[first] == cards[first]) first++;
                if (first == Count) return;
                if (this[first].Id == cards[first].Id &&
                    this[first].OnDesktop == cards[first].OnDesktop &&
                    Enumerable.Range(first + 1, Count - first - 1).All(i => this[i] == cards[i]))
                {
                    this[first] = cards[first];
                    return;
                }
            }
            Items.Clear();
            foreach (var card in cards) Items.Add(card);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
