using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Controls;

namespace PaperDesk.Desktop.ViewModels;

public sealed record NoteCard(Guid Id, string Title, string Preview, string Date, string Color, string State,
    bool Conflict, bool Favorite, bool OnDesktop, string StackName)
{
    public string Section => L.T(OnDesktop ? "На столе" : "Не на столе");
    public bool HasStack => StackName.Length != 0;
}
public sealed class ManagerViewModel(DeskController desk) : INotifyPropertyChanged
{
    public ObservableCollection<NoteCard> Notes { get; } = new();
    public string Account => desk.Account;
    public string Status => desk.Status;
    public string Count => Notes.Count.ToString();
    public int CardColumns { get; private set; } = 3;
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

    public void SetCardWidth(double width)
    {
        var columns = Math.Clamp((int)((width - 16) / 183), 1, 7);
        if (columns == CardColumns) return;
        CardColumns = columns;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardColumns)));
    }

    public void Refresh()
    {
        var placements = desk.Store.AllPlacements();
        var stacks = desk.Store.AllStacks().ToDictionary(stack => stack.Id, stack => stack.Name);
        stacks[Guid.Empty] = L.T("Общая стопка");
        var all = desk.Store.All().Select(row =>
        {
            var placement = placements.GetValueOrDefault(row.Note.Id) ?? new Placement { NoteId = row.Note.Id, StackId = null };
            var active = row.Note is { ArchivedAt: null, DeletedAt: null };
            return (Row: row, Placement: placement, Active: active, OnDesktop: active && desk.IsOnDesktop(placement));
        }).ToList();
        var rejected = desk.Store.RejectedNotes();
        var pictures = desk.Store.NotesWithImages();
        AllCount = all.Count(item => item.Active);
        VisibleCount = all.Count(item => item.OnDesktop);
        FavoriteCount = all.Count(item => item.Active && item.Placement.Favorite);
        ArchiveCount = all.Count(item => item.Row.Note is { DeletedAt: null, ArchivedAt: not null });
        TrashCount = all.Count(item => item.Row.Note.DeletedAt != null);
        HasConflicts = all.Any(item => item.Row.Conflict != null);
        HasDesktopStacks = desk.Settings.StackedNotes && all.Any(item => item.Active &&
            item.Placement.StackId is Guid group && stacks.ContainsKey(group));
        var rows = all.Where(item =>
        {
            var n = item.Row.Note;
            var match = Filter switch
            {
                "trash" => n.DeletedAt != null,
                "archive" => n.ArchivedAt != null && n.DeletedAt == null,
                "visible" => item.OnDesktop,
                "hidden" => item.Active && !item.OnDesktop,
                "favorite" => item.Active && item.Placement.Favorite,
                "conflict" => item.Row.Conflict != null,
                _ => item.Active
            };
            return match && (n.Title.Contains(Search, StringComparison.CurrentCultureIgnoreCase) || n.Body.Contains(Search, StringComparison.CurrentCultureIgnoreCase));
        });
        rows = Sort switch
        {
            1 => rows.OrderBy(item => item.Row.Note.Title),
            2 => rows.OrderByDescending(item => item.Row.Note.CreatedAt),
            _ => rows.OrderByDescending(item => item.Row.Note.UpdatedAt)
        };
        if (Filter == "all") rows = rows.OrderByDescending(item => item.OnDesktop);
        var cards = rows.Select(item =>
        {
            var (row, p, _, onDesktop) = item;
            var n = row.Note;
            var stackName = onDesktop && desk.Settings.StackedNotes && p.StackId is Guid group
                ? stacks.GetValueOrDefault(group) ?? "" : "";
            return new NoteCard(n.Id, string.IsNullOrWhiteSpace(n.Title) ? L.T("Без названия") : n.Title,
                string.IsNullOrWhiteSpace(n.Body) ? L.T(pictures.Contains(n.Id) ? "Изображения" : "Пустой листик") : FormatPreview(n.Body),
                n.UpdatedAt.ToLocalTime().ToString("dd MMM, HH:mm"), PaperPalette.Normalize(p.Color),
                row.Conflict != null ? L.T("⚠ Конфликт") : rejected.Contains(n.Id) ? L.T("⚠ Правка отклонена") : "",
                row.Conflict != null, p.Favorite, onDesktop, stackName);
        }).ToList();
        if (!Notes.SequenceEqual(cards))
        {
            Notes.Clear();
            foreach (var card in cards) Notes.Add(card);
        }
        foreach (var prop in new[] { nameof(Account), nameof(Status), nameof(Count), nameof(AllCount), nameof(VisibleCount), nameof(OffDeskCount), nameof(FavoriteCount), nameof(ArchiveCount), nameof(TrashCount) }) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    private static string FormatPreview(string text)
    {
        var preview = NoteSpoilers.Preview(text);
        var tasks = Checklist.Read(preview).ToDictionary(task => task.LineIndex);
        return string.Join('\n', preview.Split('\n').Take(8).Select((line, index) =>
        {
            if (tasks.TryGetValue(index, out var task))
                return (task.IsChecked ? "☑ " : "☐ ") + new PaperInline(task.Label).Text;
            if (NoteBodyEditor.TableCells(line) is { } cells)
                return NoteBodyEditor.IsTableDivider(cells) ? null : string.Join("  │  ", cells.Select(cell => new PaperInline(cell).Text));
            if (line.StartsWith("- ", StringComparison.Ordinal)) line = "• " + line[2..];
            else if (line.StartsWith("## ", StringComparison.Ordinal)) line = line[3..];
            else if (line.StartsWith("> ", StringComparison.Ordinal)) line = "│ " + line[2..];
            return new PaperInline(line).Text;
        }).Where(line => line != null));
    }
}
