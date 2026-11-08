using System.Windows.Documents;

namespace PaperDesk.Desktop.Controls;

internal sealed class PaperLineInput : RichTextBox
{
    private readonly Paragraph line = new() { Margin = new Thickness(0) };

    internal PaperLineInput()
    {
        Document = new FlowDocument(line) { PagePadding = new Thickness(0), PageWidth = 100_000, ColumnWidth = 100_000 };
        AcceptsReturn = false;
        AcceptsTab = false;
        Padding = new Thickness(0);
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
    }

    internal string Text
    {
        get => new TextRange(line.ContentStart, line.ContentEnd).Text;
        set => new TextRange(line.ContentStart, line.ContentEnd).Text = value;
    }

    internal int CaretIndex { get => Offset(CaretPosition); set => Select(value, 0); }
    internal int SelectionStart => Offset(Selection.Start);
    internal int SelectionLength => Offset(Selection.End) - SelectionStart;
    internal string SelectedText { set => Selection.Text = value; }

    internal void Select(int start, int length) => Selection.Select(Position(start), Position(start + length));
    internal new void SelectAll() => Select(0, Text.Length);

    internal int CharacterAt(Point point) => GetPositionFromPoint(point, true) is { } hit ? Offset(hit) : 0;

    internal void SetRuns(PaperInline inline, int start, int length)
    {
        line.Inlines.Clear();
        foreach (var run in inline.Runs(start, length)) line.Inlines.Add(run);
    }

    private int Offset(TextPointer pointer) => pointer.CompareTo(line.ContentStart) < 0 ? 0 :
        new TextRange(line.ContentStart, pointer.CompareTo(line.ContentEnd) > 0 ? line.ContentEnd : pointer).Text.Length;

    private TextPointer Position(int offset)
    {
        var position = line.ContentStart;
        while (position.CompareTo(line.ContentEnd) < 0)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var length = position.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= length) return position.GetPositionAtOffset(Math.Max(0, offset), LogicalDirection.Forward)!;
                offset -= length;
            }
            position = position.GetNextContextPosition(LogicalDirection.Forward)!;
        }
        return line.ContentEnd;
    }
}
