using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace PaperDesk.Desktop.Controls;

internal sealed class PaperInline
{
    internal enum Style { Plain, Bold, Italic, Strike, Code }
    internal record struct Part(int Start, int Length, int SourceStart, Style Style);

    private static readonly Regex Markers = new(
        @"\*\*(?<bold>[^*]+)\*\*|(?<!\w)_(?<italic>[^_]+)_(?!\w)|~~(?<strike>[^~]+)~~|\x60(?<code>[^\x60]+)\x60",
        RegexOptions.CultureInvariant);

    internal string Text { get; }
    internal bool HasMarkup { get; }
    private readonly Part[] parts;

    internal PaperInline(string text)
    {
        var spans = new List<Part>();
        var visible = new System.Text.StringBuilder(text.Length);
        void Add(int start, int length, Style style)
        {
            if (length == 0) return;
            spans.Add(new Part(visible.Length, length, start, style));
            visible.Append(text, start, length);
        }

        var index = 0;
        foreach (Match match in Markers.Matches(text))
        {
            HasMarkup = true;
            Add(index, match.Index - index, Style.Plain);
            var style = match.Groups["bold"].Success ? Style.Bold : match.Groups["italic"].Success ? Style.Italic :
                match.Groups["strike"].Success ? Style.Strike : Style.Code;
            var content = match.Groups[style.ToString().ToLowerInvariant()];
            Add(content.Index, content.Length, style);
            index = match.Index + match.Length;
        }
        Add(index, text.Length - index, Style.Plain);
        Text = visible.ToString();
        parts = spans.ToArray();
    }

    private int FindPart(int offset)
    {
        var low = 0;
        var high = parts.Length - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (offset >= parts[mid].Start + parts[mid].Length) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    internal Part At(int offset) => parts[FindPart(offset)];

    internal int SourceOffset(int offset)
    {
        if (parts.Length == 0) return 0;
        var part = At(offset);
        return part.SourceStart + Math.Min(part.Length, offset - part.Start);
    }

    internal void Write(TextBlock caption, int start, int length)
    {
        if (length == 0) return;
        var end = start + length;
        if (!HasMarkup)
        {
            caption.Text = Text[start..end];
            return;
        }
        for (var index = FindPart(start); index < parts.Length && parts[index].Start < end; index++)
        {
            var part = parts[index];
            var from = Math.Max(start, part.Start);
            var to = Math.Min(end, part.Start + part.Length);
            var run = new Run(Text[from..to]);
            switch (part.Style)
            {
                case Style.Bold: run.FontWeight = FontWeights.Bold; break;
                case Style.Italic: run.FontStyle = FontStyles.Italic; break;
                case Style.Strike: run.TextDecorations = TextDecorations.Strikethrough; break;
                case Style.Code: run.FontFamily = new FontFamily("Consolas"); break;
            }
            caption.Inlines.Add(run);
        }
    }
}
