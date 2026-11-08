using System.Globalization;
using System.Text.RegularExpressions;

namespace PaperDesk.Desktop.Controls;

internal static class NoteFormat
{
    internal enum Kind { Bullet, Number, Task, Table, Spoiler, Heading, Quote, Bold, Italic, Strike, Code }
    internal record struct Edit(int Start, int Length, string Text, int SelectStart, int SelectLength = 0, int? OpenSpoilerAt = null);

    private static readonly Regex Prefix = new(
        @"^(?<indent>[ \t]*)(?<mark>(?:(?:[-+*][ \t]+)?(?:\[[ xX]\]|[☐☑☒])[ \t]*)|(?:[-+*]|[0-9]+\.|>)[ \t]+)",
        RegexOptions.CultureInvariant);

    internal static Edit Apply(string text, int start, int length, Kind kind)
    {
        var newline = Newline(text);
        var end = start + length;
        if (kind is Kind.Bold or Kind.Italic or Kind.Strike or Kind.Code)
            return ApplyInline(text, start, length, InlineStyle(kind));

        var firstLine = LineStart(text, start);
        var lastLine = LineEnd(text, end > start && text[end - 1] == '\n' ? end - 1 : end);
        if (kind == Kind.Table || (kind == Kind.Spoiler && length == 0))
        {
            var block = kind == Kind.Table
                ? new[] { "|  |  |", "| --- | --- |", "|  |  |" }
                : new[] { ":::spoiler", "", ":::" };
            var leading = lastLine == firstLine ? "" : newline;
            var template = leading + string.Join(newline, block);
            // The existing line break still separates the new block from the next paragraph.
            var trailing = lastLine == text.Length ? newline : "";
            var selectAt = kind == Kind.Table ? leading.Length + 3 : leading.Length + block[0].Length + newline.Length;
            return new Edit(lastLine, 0, template + trailing, selectAt, OpenSpoilerAt: kind == Kind.Spoiler ? lastLine + leading.Length : null);
        }

        var lines = text[firstLine..lastLine].Split('\n');
        if (kind == Kind.Spoiler)
        {
            var open = ":::spoiler" + newline;
            return new Edit(firstLine, lastLine - firstLine, open + text[firstLine..lastLine] + newline + ":::", open.Length, OpenSpoilerAt: firstLine);
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
            if (NoteSpoilers.Label(line) != null || NoteSpoilers.Closes(line) || NoteBodyEditor.TableCells(line) != null) continue;
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

    internal static PaperInline.Style InlineStyle(Kind kind) => kind switch
    {
        Kind.Bold => PaperInline.Style.Bold, Kind.Italic => PaperInline.Style.Italic,
        Kind.Strike => PaperInline.Style.Strike, Kind.Code => PaperInline.Style.Code, _ => PaperInline.Style.Plain
    };

    private static Edit ApplyInline(string text, int start, int length, PaperInline.Style style)
    {
        if (length == 0) return new Edit(start, 0, "", 0);
        var end = start + length;
        var first = LineStart(text, start);
        var last = LineEnd(text, end > first && text[end - 1] == '\n' ? end - 1 : end);
        var slices = SelectedSlices(text, start, end, first, last);
        if (slices.Count == 0) return new Edit(start, 0, "", 0);
        var enable = !slices.All(s => s.Inline.HasStyle(s.From, s.To, style));
        var result = new System.Text.StringBuilder();
        var previous = first;
        var selectStart = -1;
        var selectEnd = 0;
        foreach (var (offset, inline, from, to, _) in slices)
        {
            result.Append(text, previous, offset - previous);
            var updated = inline.SetStyle(from, to, style, enable);
            var mapped = new PaperInline(updated);
            if (selectStart < 0) selectStart = result.Length + mapped.SourceOffset(from);
            selectEnd = result.Length + mapped.EndOffset(to);
            result.Append(updated);
            previous = offset + inline.RawLength;
        }
        result.Append(text, previous, last - previous);
        return new Edit(first, last - first, result.ToString(), selectStart, selectEnd - selectStart);
    }

    internal static Edit ReplaceDocumentSelection(string text, int start, int length, string replacement)
    {
        var plain = new Edit(start, length, replacement, replacement.Length);
        if (length == 0 || length == text.Length) return plain;
        var end = start + length;
        var first = LineStart(text, start);
        var last = LineEnd(text, text[end - 1] == '\n' ? end - 1 : end);
        if (!text[first..last].Split('\n').Any(line =>
            NoteBodyEditor.TableCells(line) != null || NoteSpoilers.Label(line) != null || NoteSpoilers.Closes(line))) return plain;

        var slices = SelectedSlices(text, start, end, first, last);
        if (slices.Count == 0) return new Edit(start, 0, "", 0);
        var result = new System.Text.StringBuilder();
        var previous = first;
        var caret = -1;
        foreach (var (offset, inline, from, to, singleLine) in slices)
        {
            result.Append(text, previous, offset - previous);
            var insertion = caret >= 0 ? "" : singleLine
                ? replacement.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('|', '¦')
                : replacement;
            var updated = inline.Replace(from, to - from, insertion);
            if (caret < 0)
            {
                var parsed = new PaperInline(updated);
                caret = result.Length + parsed.EndOffset(Math.Min(parsed.Text.Length, from + insertion.Length));
            }
            result.Append(updated);
            previous = offset + inline.RawLength;
        }
        result.Append(text, previous, last - previous);
        return new Edit(first, last - first, result.ToString(), caret);
    }

    private static List<(int Offset, PaperInline Inline, int From, int To, bool SingleLine)> SelectedSlices(
        string text, int start, int end, int first, int last)
    {
        var slices = new List<(int, PaperInline, int, int, bool)>();
        for (var lineStart = first; lineStart <= last;)
        {
            var lineEnd = LineEnd(text, lineStart);
            var line = text[lineStart..lineEnd];
            if (!NoteSpoilers.Closes(line))
            {
                if (NoteBodyEditor.TableCells(line) is { } cells)
                {
                    if (!NoteBodyEditor.IsTableDivider(cells))
                    {
                        var cellStart = line.IndexOf('|') + 1;
                        for (var column = 0; column < cells.Length; column++)
                        {
                            var cellEnd = line.IndexOf('|', cellStart);
                            var from = cellStart;
                            while (from < cellEnd && char.IsWhiteSpace(line[from])) from++;
                            Add(lineStart + from, cells[column], singleLine: true);
                            cellStart = cellEnd + 1;
                        }
                    }
                }
                else
                {
                    var prefix = Prefix.Match(line).Length;
                    if (line.AsSpan(prefix).StartsWith("## ")) prefix += 3;
                    var spoilerTitle = NoteSpoilers.Label(line) != null;
                    if (spoilerTitle)
                    {
                        prefix = line.IndexOf(":::spoiler", StringComparison.Ordinal) + ":::spoiler".Length;
                        while (prefix < line.Length && char.IsWhiteSpace(line[prefix])) prefix++;
                    }
                    Add(lineStart + prefix, line[prefix..], spoilerTitle);
                }
            }
            if (lineEnd >= last) break;
            lineStart = AfterLine(text, lineEnd);
        }
        return slices;

        void Add(int offset, string raw, bool singleLine)
        {
            if (offset >= end || offset + raw.Length <= start) return;
            var inline = new PaperInline(raw);
            var from = inline.VisibleOffset(start - offset);
            var to = inline.VisibleOffset(end - offset);
            if (from < to) slices.Add((offset, inline, from, to, singleLine));
        }
    }

    internal static Edit? RemoveListMarker(string text, int caret)
    {
        var start = LineStart(text, caret);
        var match = Prefix.Match(text[start..LineEnd(text, caret)]);
        if (!match.Success || caret != start + match.Length) return null;
        var mark = match.Groups["mark"];
        return new Edit(start + mark.Index, mark.Length, "", 0);
    }

    internal static Edit? UnwrapSpoiler(string text, int caret)
    {
        var start = LineStart(text, caret);
        var openEnd = LineEnd(text, start);
        var opening = text[start..openEnd];
        if (NoteSpoilers.Label(opening) == null) return null;
        var bodyStart = AfterLine(text, openEnd);
        var closeStart = bodyStart;
        while (closeStart < text.Length)
        {
            var closeEnd = LineEnd(text, closeStart);
            if (NoteSpoilers.Closes(text[closeStart..closeEnd]))
            {
                var title = opening.TrimStart()[":::spoiler".Length..].TrimStart();
                var bodyEnd = closeStart > bodyStart ? closeStart - 1 : bodyStart;
                if (bodyEnd > bodyStart && text[bodyEnd - 1] == '\r') bodyEnd--;
                var body = text[bodyStart..bodyEnd];
                var replacement = string.IsNullOrWhiteSpace(body) ? title :
                    title.Length == 0 ? body : title + text[openEnd..bodyStart] + body;
                var end = AfterLine(text, closeEnd);
                if (replacement.Length > 0) replacement += text[closeEnd..end];
                return new Edit(start, end - start, replacement, 0);
            }
            closeStart = AfterLine(text, closeEnd);
        }
        return null;
    }

    internal static int NumberPrefix(string line)
    {
        var match = Prefix.Match(line);
        return match.Success && char.IsAsciiDigit(match.Groups["mark"].Value[0]) ? match.Length : 0;
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
        var newline = Newline(text);
        var prefix = newline + match.Groups["indent"].Value + mark + " ";
        return new Edit(caret, 0, prefix, prefix.Length);
    }

    internal static int LineStart(string text, int index) => index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    internal static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    internal static int AfterLine(string text, int end) => end >= text.Length ? end : end + (text[end] == '\r' ? 2 : 1);

    internal static int LineEnd(string text, int index)
    {
        var end = text.IndexOf('\n', index);
        return end < 0 ? text.Length : end > 0 && text[end - 1] == '\r' ? end - 1 : end;
    }
}
