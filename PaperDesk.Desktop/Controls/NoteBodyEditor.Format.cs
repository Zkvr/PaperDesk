using System.Windows.Controls.Primitives;

namespace PaperDesk.Desktop.Controls;

public sealed partial class NoteBodyEditor
{
    private static readonly (NoteFormat.Kind Kind, string Label, string Glyph)[] Formats =
    [
        (NoteFormat.Kind.Bold, "Жирный", "B"),
        (NoteFormat.Kind.Italic, "Курсив", "I"),
        (NoteFormat.Kind.Strike, "Зачёркнутый", "S"),
        (NoteFormat.Kind.Code, "Код", "</>"),
        (NoteFormat.Kind.Heading, "Подзаголовок", "H"),
        (NoteFormat.Kind.Bullet, "Маркированный список", "\uE8FD"),
        (NoteFormat.Kind.Number, "Нумерованный список", "1≡"),
        (NoteFormat.Kind.Task, "Чек-лист", "☑"),
        (NoteFormat.Kind.Quote, "Цитата", "❞"),
        (NoteFormat.Kind.Spoiler, "Спойлер", "▸"),
        (NoteFormat.Kind.Table, "Таблица", "▦")
    ];
    private Button? formatButton;
    private FrameworkElement? formatPaper;

    internal void ConnectFormatMenu(Button button, FrameworkElement paper) =>
        (formatButton, formatPaper) = (button, paper);

    internal void OpenFormatMenu()
    {
        if (IsWebActive) { SendWebCommand("format"); return; }
        if (CreateFormatMenu() is not { } menu) return;
        var selection = CurrentSelection();
        var table = blockPiece != null ? blockPiece.Table : lastTable;
        if (table is { } cell && cell.Header < Text.Length &&
            TableCells(Text[cell.Header..NoteFormat.LineEnd(Text, cell.Header)]) is { } columns)
        {
            var commands = FormatItem("Правка таблицы", "▦");
            AddTableCommands(commands, action => Dispatcher.BeginInvoke(() => EditTable(cell, action)), cell.Row > cell.Header, columns.Length);
            menu.Items.Add(commands);
            menu.Items.Add(new Separator());
        }
        foreach (var (kind, label, glyph) in Formats)
        {
            if (kind is NoteFormat.Kind.Heading or NoteFormat.Kind.Spoiler) menu.Items.Add(new Separator());
            var item = FormatItem(label, glyph);
            item.IsEnabled = table == null || NoteFormat.InlineStyle(kind) != PaperInline.Style.Plain;
            item.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (formatMenu != menu || !IsEnabled || !IsVisible) return;
                formatMenu = null;
                ApplyFormat(kind, selection);
            });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OpenWebFormatMenu(WebDocument document, bool inTable)
    {
        if (CreateFormatMenu() is not { } menu) return;
        if (inTable)
        {
            Add("Добавить строку ниже", "+↓", nameof(TableAction.AddRow));
            Add("Добавить столбец справа", "+→", nameof(TableAction.AddColumn));
            Add("Удалить строку", "−↓", nameof(TableAction.RemoveRow));
            Add("Удалить столбец", "−→", nameof(TableAction.RemoveColumn));
            Add("Удалить таблицу", "×", nameof(TableAction.RemoveTable));
            menu.Items.Add(new Separator());
        }
        foreach (var (kind, label, glyph) in Formats)
        {
            if (kind is NoteFormat.Kind.Heading or NoteFormat.Kind.Spoiler) menu.Items.Add(new Separator());
            Add(label, glyph, kind.ToString(), !inTable || NoteFormat.InlineStyle(kind) != PaperInline.Style.Plain);
        }
        menu.IsOpen = true;

        void Add(string label, string glyph, string action, bool enabled = true)
        {
            var item = FormatItem(label, glyph);
            item.IsEnabled = enabled;
            item.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (formatMenu != menu || !IsVisible || !IsEnabled ||
                    webDocuments.LastOrDefault() != document ||
                    pictures?.WebNote is not { } active || active.Store != document.Store || active.Id != document.Id) return;
                webEditor?.Focus();
                SendWebCommand("formatAction", action);
            });
            menu.Items.Add(item);
        }
    }

    private ContextMenu? CreateFormatMenu()
    {
        if (formatButton is not { IsVisible: true, IsEnabled: true } anchor ||
            formatPaper is not { IsVisible: true } paper) return null;
        if (formatMenu is { IsOpen: true } previous) previous.IsOpen = false;
        var bounds = paper.TransformToVisual(anchor).TransformBounds(new Rect(paper.RenderSize));
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = (size, target, _) =>
            [
                new(new Point(Math.Max(target.Width, bounds.Right) + 8, 0), PopupPrimaryAxis.Vertical),
                new(new Point(Math.Min(0, bounds.Left) - size.Width - 8, 0), PopupPrimaryAxis.Vertical),
                new(new Point(target.Width - size.Width, Math.Min(0, bounds.Top) - size.Height - 8), PopupPrimaryAxis.Horizontal)
            ]
        };
        formatMenu = menu;
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (formatMenu?.IsOpen == true) return;
            if (IsWebActive) SetWebEditing(webEditor?.IsKeyboardFocusWithin == true);
            else if (!IsKeyboardFocusWithin) EndEdit();
        });
        return menu;
    }

    private static MenuItem FormatItem(string label, string glyph)
    {
        var icon = new TextBlock
        {
            Text = glyph, Width = 22, TextAlignment = TextAlignment.Center,
            FontSize = glyph.Length > 1 ? 11 : 15,
            FontFamily = new FontFamily(glyph == "\uE8FD" ? "Segoe MDL2 Assets" : "Segoe UI"),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (glyph is "B" or "H") icon.FontWeight = FontWeights.Bold;
        if (glyph == "I") icon.FontStyle = FontStyles.Italic;
        if (glyph == "S") icon.TextDecorations = TextDecorations.Strikethrough;
        return new MenuItem { Header = L.T(label), Icon = icon };
    }
}
