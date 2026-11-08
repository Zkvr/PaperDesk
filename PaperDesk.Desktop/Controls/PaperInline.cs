using System.Text.RegularExpressions;
using System.Windows.Documents;

namespace PaperDesk.Desktop.Controls;

internal sealed class PaperInline
{
    [Flags]
    internal enum Style { Plain = 0, Bold = 1, Italic = 2, Strike = 4, Code = 8 }
    internal record struct Part(int Start, int Length, int SourceStart, Style Style);

    private static readonly Regex Markers = new(
        @"\x60\x60(?<code>[^\r\n]+?)\x60\x60|\x60(?<code>[^\x60\r\n]+)\x60|\*\*(?<bold>[^*\r\n]+)\*\*|(?<!\w)_(?<italic>[^_\r\n]+)_(?!\w)|\*(?!\*)(?<italic>[^*\r\n]+)\*|~~(?<strike>[^~\r\n]+)~~",
        RegexOptions.CultureInvariant);

    internal string Text { get; }
    internal bool HasMarkup { get; }
    internal int RawLength { get; }
    private readonly Part[] parts;

    internal PaperInline(string text)
    {
        RawLength = text.Length;
        var spans = new List<Part>();
        var visible = new System.Text.StringBuilder(text.Length);
        void Add(int start, int length, Style style)
        {
            if (length == 0) return;
            spans.Add(new Part(visible.Length, length, start, style));
            visible.Append(text, start, length);
        }

        void Read(int start, int length, Style inherited)
        {
            var end = start + length;
            var index = start;
            for (var match = Markers.Match(text, start, length); match.Success; match = match.NextMatch())
            {
                var style = match.Groups["bold"].Success ? Style.Bold : match.Groups["italic"].Success ? Style.Italic :
                    match.Groups["strike"].Success ? Style.Strike : Style.Code;
                if ((inherited & style) != 0) continue;
                Add(index, match.Index - index, inherited);
                var content = match.Groups[style.ToString().ToLowerInvariant()];
                if (style == Style.Code) Add(content.Index, content.Length, inherited | style);
                else Read(content.Index, content.Length, inherited | style);
                index = match.Index + match.Length;
            }
            Add(index, end - index, inherited);
        }
        Read(0, text.Length, Style.Plain);
        Text = visible.ToString();
        parts = spans.ToArray();
        HasMarkup = Text.Length != text.Length;
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

    internal int EndOffset(int offset) => offset == 0 ? SourceOffset(0) : SourceOffset(offset - 1) + 1;

    internal int VisibleOffset(int source)
    {
        foreach (var part in parts)
            if (source <= part.SourceStart + part.Length)
                return part.Start + Math.Clamp(source - part.SourceStart, 0, part.Length);
        return Text.Length;
    }

    internal string Replace(int start, int length, string insertion)
    {
        if (!HasMarkup) return Text[..start] + insertion + Text[(start + length)..];
        var styles = Styles();
        var end = start + length;
        var next = new Style[Text.Length - length + insertion.Length];
        styles.AsSpan(0, start).CopyTo(next);
        var style = length > 0 ? styles[start] : styles[Math.Clamp(start == 0 ? 0 : start - 1, 0, styles.Length - 1)];
        next.AsSpan(start, insertion.Length).Fill(style);
        styles.AsSpan(end).CopyTo(next.AsSpan(start + insertion.Length));
        return Serialize(Text[..start] + insertion + Text[end..], next);
    }

    internal bool HasStyle(int start, int end, Style style) =>
        start < end && parts.All(p => p.Start >= end || p.Start + p.Length <= start || (p.Style & style) == style);

    internal string SetStyle(int start, int end, Style style, bool enabled)
    {
        var styles = Styles();
        for (var i = start; i < end; i++) styles[i] = enabled ? styles[i] | style : styles[i] & ~style;
        return Serialize(Text, styles);
    }

    private Style[] Styles()
    {
        var styles = new Style[Text.Length];
        foreach (var part in parts) styles.AsSpan(part.Start, part.Length).Fill(part.Style);
        return styles;
    }

    private static string Serialize(string text, Style[] styles)
    {
        var result = new System.Text.StringBuilder(text.Length);
        for (var start = 0; start < styles.Length;)
        {
            if (text[start] is '\r' or '\n')
            {
                result.Append(text[start]);
                start++;
                continue;
            }
            var end = start + 1;
            while (end < styles.Length && text[end] is not ('\r' or '\n') && styles[end] == styles[start]) end++;
            result.Append(Decorate(text[start..end], styles[start]));
            start = end;
        }
        return result.ToString();
    }

    internal static string Decorate(string text, Style style)
    {
        if (text.Length == 0) return text;
        if (style.HasFlag(Style.Code))
        {
            var delimiter = text.Contains('\x60') ? "\x60\x60" : "\x60";
            text = delimiter + text + delimiter;
        }
        if (style.HasFlag(Style.Italic)) text = style.HasFlag(Style.Bold) ? "_" + text + "_" : "*" + text + "*";
        if (style.HasFlag(Style.Bold)) text = "**" + text + "**";
        if (style.HasFlag(Style.Strike)) text = "~~" + text + "~~";
        return text;
    }

    internal void Write(TextBlock caption, int start, int length, int selectedStart = 0, int selectedEnd = 0, Brush? highlight = null)
    {
        if (length == 0) return;
        var end = start + length;
        if (!HasMarkup && selectedStart == selectedEnd)
        {
            caption.Text = Text[start..end];
            return;
        }
        foreach (var run in Runs(start, length, selectedStart, selectedEnd, highlight)) caption.Inlines.Add(run);
    }

    internal IEnumerable<Run> Runs(int start, int length, int selectedStart = 0, int selectedEnd = 0, Brush? highlight = null)
    {
        var end = start + length;
        for (var index = FindPart(start); index < parts.Length && parts[index].Start < end; index++)
        {
            var part = parts[index];
            var from = Math.Max(start, part.Start);
            var to = Math.Min(end, part.Start + part.Length);
            var before = Math.Clamp(selectedStart, from, to);
            var after = Math.Clamp(selectedEnd, before, to);
            if (from < before) yield return MakeRun(from, before, false);
            if (before < after) yield return MakeRun(before, after, true);
            if (after < to) yield return MakeRun(after, to, false);

            Run MakeRun(int a, int b, bool selected)
            {
                var run = new Run(Text[a..b]);
                if (part.Style.HasFlag(Style.Bold)) run.FontWeight = FontWeights.Bold;
                if (part.Style.HasFlag(Style.Italic)) run.FontStyle = FontStyles.Italic;
                if (part.Style.HasFlag(Style.Strike)) run.TextDecorations = TextDecorations.Strikethrough;
                if (part.Style.HasFlag(Style.Code)) run.FontFamily = new FontFamily("Consolas");
                if (selected) run.Background = highlight;
                return run;
            }
        }
    }
}
