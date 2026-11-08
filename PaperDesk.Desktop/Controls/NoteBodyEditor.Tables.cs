namespace PaperDesk.Desktop.Controls;

public sealed partial class NoteBodyEditor
{
    private enum TableAction { AddRow, AddColumn, RemoveRow, RemoveColumn, RemoveTable }
    private sealed record TableLocation(int Header, int Row, int Column);
    private TableLocation? lastTable;
    private int navigationRevision;

    private static void AddTableCommands(ItemsControl menu, Action<TableAction> run, bool canRemoveRow, int columns)
    {
        Add("Добавить строку ниже", TableAction.AddRow);
        Add("Добавить столбец справа", TableAction.AddColumn, columns < 12);
        menu.Items.Add(new Separator());
        Add("Удалить строку", TableAction.RemoveRow, canRemoveRow);
        Add("Удалить столбец", TableAction.RemoveColumn, columns > 2);
        Add("Удалить таблицу", TableAction.RemoveTable);

        void Add(string label, TableAction action, bool enabled = true)
        {
            var item = new MenuItem { Header = L.T(label), IsEnabled = enabled };
            item.Click += (_, _) => run(action);
            menu.Items.Add(item);
        }
    }

    private ContextMenu TableMenu(TableLocation cell, int columns)
    {
        var menu = new ContextMenu();
        var snapshot = renderedText;
        var revision = navigationRevision;
        AddTableCommands(menu, action => Dispatcher.BeginInvoke(() =>
        {
            if (revision != navigationRevision) return;
            EditTable(cell with { Header = RebaseOffset(snapshot, cell.Header), Row = RebaseOffset(snapshot, cell.Row) }, action);
        }),
            cell.Row > cell.Header, columns);
        return menu;
    }

    private void EditTable(TableLocation cell, TableAction action)
    {
        if (!IsEnabled || cell.Header >= Text.Length || cell.Row > Text.Length) return;
        var rows = new List<List<string>>();
        var at = cell.Header;
        var end = at;
        var chosen = -1;
        var columns = 0;
        while (at < Text.Length)
        {
            var rowEnd = NoteFormat.LineEnd(Text, at);
            if (TableCells(Text[at..rowEnd]) is not { } cells || (rows.Count > 0 && cells.Length != columns)) break;
            columns = cells.Length;
            if (at == cell.Row) chosen = rows.Count;
            rows.Add(cells.ToList());
            end = rowEnd;
            var next = Text.IndexOf('\n', rowEnd);
            if (next < 0) break;
            at = next + 1;
        }
        if (chosen < 0 || rows.Count < 2 || !IsTableDivider(rows[1]) || cell.Column >= columns) return;

        EndEdit();
        if (action == TableAction.RemoveTable)
        {
            var next = Text.IndexOf('\n', end);
            var endIncludingSeparator = next < 0 ? end : next + 1;
            ApplyDocumentEdit(new NoteFormat.Edit(cell.Header, endIncludingSeparator - cell.Header, "", 0));
            RestoreSelection();
            return;
        }

        var column = cell.Column;
        switch (action)
        {
            case TableAction.AddRow:
                chosen = Math.Max(2, chosen + 1);
                rows.Insert(chosen, Enumerable.Repeat("", columns).ToList());
                break;
            case TableAction.AddColumn when columns < 12:
                column++;
                for (var row = 0; row < rows.Count; row++) rows[row].Insert(column, row == 1 ? "---" : "");
                break;
            case TableAction.RemoveRow when chosen >= 2:
                rows.RemoveAt(chosen);
                chosen = chosen < rows.Count ? chosen : rows.Count > 2 ? rows.Count - 1 : 0;
                break;
            case TableAction.RemoveColumn when columns > 2:
                foreach (var row in rows) row.RemoveAt(column);
                column = Math.Min(column, columns - 2);
                break;
            default:
                return;
        }

        var newline = Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var serialized = rows.Select(row => "| " + string.Join(" | ", row) + " |").ToArray();
        var target = serialized.Take(chosen).Sum(row => row.Length + newline.Length);
        var replacement = string.Join(newline, serialized);
        ApplyDocumentEdit(new NoteFormat.Edit(cell.Header, end - cell.Header, replacement,
            TableCellOffset(replacement, target, column)));
        RestoreSelection();
    }
}
