namespace PaperDesk.Core;

public static class NoteSpoilers
{
    private const string Marker = ":::spoiler";

    public static string? Label(string line)
    {
        line = line.Trim();
        if (line == Marker) return L.T("Спойлер");
        if (!line.StartsWith(Marker + " ", StringComparison.Ordinal)) return null;
        return line[Marker.Length..].Trim();
    }

    public static bool Closes(string line) => line.Trim() == ":::";

    public static string Preview(string text)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal)) return text;
        var lines = new List<string>();
        var hidden = false;
        foreach (var line in text.Split('\n'))
        {
            if (hidden)
            {
                if (Closes(line)) hidden = false;
                continue;
            }
            if (Label(line) is { } label)
            {
                lines.Add("▸ " + label);
                hidden = true;
            }
            else lines.Add(line);
        }
        return string.Join('\n', lines);
    }
}
