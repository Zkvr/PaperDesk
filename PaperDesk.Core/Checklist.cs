using System.Text.RegularExpressions;
namespace PaperDesk.Core;
public record ChecklistItem(int LineIndex, string Label, bool IsChecked);
public static class Checklist
{
    private static readonly Regex Marker = new(@"^(?<prefix>[ \t]*(?:[-*+][ \t]+)?)(?<marker>\[[ xX]\]|[☐☑☒])(?<gap>[ \t]*)(?<label>.*)$", RegexOptions.Compiled);
    public static IReadOnlyList<ChecklistItem> Read(string text) => text.Split('\n').Select((line,index) => (Match:Marker.Match(line.TrimEnd('\r')),Index:index)).Where(x=>x.Match.Success).Select(x=>new ChecklistItem(x.Index,x.Match.Groups["label"].Value,x.Match.Groups["marker"].Value is "[x]" or "[X]" or "☑" or "☒")).ToList();
    public static string SetChecked(string text, int lineIndex, bool value)
    {
        var lines = text.Split('\n'); if (lineIndex < 0 || lineIndex >= lines.Length) return text;
        var match = Marker.Match(lines[lineIndex]); if (!match.Success) return text;
        var marker = match.Groups["marker"];
        var replacement = marker.Value.StartsWith('[') ? (value ? "[x]" : "[ ]") : (value ? "☑" : "☐");
        lines[lineIndex] = lines[lineIndex][..marker.Index] + replacement + lines[lineIndex][(marker.Index+marker.Length)..];
        return string.Join('\n',lines);
    }
}
public static class NoteStack
{
    public static IReadOnlyList<Note> Order(IEnumerable<Note> notes) => notes.Where(n=>n.ArchivedAt==null&&n.DeletedAt==null).OrderBy(n=>n.CreatedAt).ThenBy(n=>n.Id).ToList();
    public static Guid Next(IEnumerable<Note> notes, Guid active, int direction)
    {
        var ordered = Order(notes); if (ordered.Count==0) return Guid.Empty;
        var index = ordered.ToList().FindIndex(x=>x.Id==active); if (index<0) return ordered[0].Id;
        return ordered[((index+direction)%ordered.Count+ordered.Count)%ordered.Count].Id;
    }
}
