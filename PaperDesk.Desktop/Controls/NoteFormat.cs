using System.Globalization;
using System.Text.RegularExpressions;

namespace PaperDesk.Desktop.Controls;

internal static class NoteFormat
{
    internal enum Kind { Bullet, Number, Task, Table, Spoiler, Heading, Quote, Bold, Italic, Strike, Code }
    internal record struct Edit(int Start, int Length, string Text, int SelectStart, int SelectLength = 0);

    private static readonly Regex Prefix = new(
        @"^(?<indent>[ \t]*)(?<mark>(?:(?:[-+*][ \t]+)?(?:\[[ xX]\]|[☐☑☒])[ \t]*)|(?:[-+*]|[0-9]+\.|>)[ \t]+)",
        RegexOptions.CultureInvariant);

    internal static Edit Apply(string text, int start, int length, Kind kind)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var end = start + length;
        if (kind is Kind.Bold or Kind.Italic or Kind.Strike or Kind.Code)
        {
            var marker = kind switch { Kind.Bold => "**", Kind.Italic => "_", Kind.Strike => "~~", _ => "\x60" };
            var selected = length == 0 ? L.T("текст") : text.Substring(start, length);
            if (length > 0 && start >= marker.Length && text.Length - end >= marker.Length &&
                text.AsSpan(start - marker.Length, marker.Length).SequenceEqual(marker) &&
                text.AsSpan(end, marker.Length).SequenceEqual(marker))
                return new Edit(start - marker.Length, length + 2 * marker.Length, selected, 0, selected.Length);
            if (selected.Contains('\n'))
            {
                var styled = Regex.Replace(selected, @"[^\r\n]+", line => marker + line.Value + marker);
                return new Edit(start, length, styled, 0, styled.Length);
            }
            return new Edit(start, length, marker + selected + marker, marker.Length, selected.Length);
        }

        var firstLine = LineStart(text, start);
        var lastLine = LineEnd(text, end > start && text[end - 1] == '\n' ? end - 1 : end);
        if (kind == Kind.Table || (kind == Kind.Spoiler && length == 0))
        {
            var block = kind == Kind.Table
                ? new[] { L.T("| Столбец 1 | Столбец 2 |"), "| --- | --- |", L.T("| Значение | Значение |") }
                : new[] { L.T(":::spoiler Спойлер"), L.T("Скрытый текст"), ":::" };
            var leading = lastLine == firstLine ? "" : newline;
            var template = leading + string.Join(newline, block);
            // The existing line break still separates the new block from the next paragraph.
            var trailing = lastLine == text.Length ? newline : "";
            var selectAt = kind == Kind.Table ? leading.Length + 2 : leading.Length + block[0].Length + newline.Length;
            return new Edit(lastLine, 0, template + trailing, selectAt, kind == Kind.Table ? L.T("Столбец 1").Length : L.T("Скрытый текст").Length);
        }

        var lines = text[firstLine..lastLine].Split('\n');
        if (kind == Kind.Spoiler)
        {
            var open = L.T(":::spoiler Спойлер") + newline;
            return new Edit(firstLine, lastLine - firstLine, open + text[firstLine..lastLine] + newline + ":::", ":::spoiler ".Length, L.T("Спойлер").Length);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var mark = kind switch
            {
                Kind.Number => (i + 1).ToString(CultureInfo.InvariantCulture) + ". ",
                Kind.Task => "- [ ] ",
                Kind.Heading => "## ",
                Kind.Quote => "> ",
                _ => "- "
            };
            var match = Prefix.Match(line);
            if (kind == Kind.Task && match.Success)
            {
                var existing = match.Groups["mark"].Value;
                if (existing.Contains('[') || existing.IndexOfAny(['☐', '☑', '☒']) >= 0)
                {
                    lines[i] = line;
                    continue;
                }
            }
            var indent = line.Length - line.TrimStart(' ', '\t').Length;
            var content = match.Success ? line[match.Length..] : line[indent..];
            if (content.StartsWith("## ", StringComparison.Ordinal)) content = content[3..];
            lines[i] = line[..indent] + mark + content;
        }
        var replacement = string.Join(newline, lines);
        return new Edit(firstLine, lastLine - firstLine, replacement, replacement.Length);
    }

    internal static Edit? ContinueList(string text, int caret)
    {
        if (caret != LineEnd(text, caret)) return null;
        var start = LineStart(text, caret);
        var line = text[start..caret];
        var match = Prefix.Match(line);
        if (!match.Success) return null;
        if (string.IsNullOrWhiteSpace(line[match.Length..])) return new Edit(start, caret - start, "", 0);

        var mark = match.Groups["mark"].Value.TrimEnd();
        if (mark.Contains('[') || mark.IndexOfAny(['☐', '☑', '☒']) >= 0) mark = "- [ ]";
        else if (mark.EndsWith('.') && int.TryParse(mark.AsSpan(0, mark.Length - 1), out var number) && number < int.MaxValue)
            mark = (number + 1).ToString(CultureInfo.InvariantCulture) + ".";
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = newline + match.Groups["indent"].Value + mark + " ";
        return new Edit(caret, 0, prefix, prefix.Length);
    }

    private static int LineStart(string text, int index) => index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    private static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end > 0 && text[end - 1] == '\r' ? end - 1 : end;
    }
}
