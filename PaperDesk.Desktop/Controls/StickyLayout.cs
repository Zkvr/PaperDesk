namespace PaperDesk.Desktop.Controls;

/// <summary>Allocates space to the body first when a sheet is made small.</summary>
public static class StickyLayout
{
    public const double MinWidth = 220, MinHeight = 180;
    public static bool IsCompact(double width, double height) => width < 260 || height < 300;

    public static void Apply(Grid content, bool compact)
    {
        content.Margin = compact ? new Thickness(10,2,10,12) : new Thickness(18,6,18,18);
        content.RowDefinitions[0].Height = new GridLength(compact ? 26 : 32);
        foreach (FrameworkElement child in content.Children)
        {
            if (Grid.GetRow(child) == 0)
                foreach (var button in Buttons(child))
                {
                    button.MinWidth = compact ? 24 : 28;
                    button.MinHeight = compact ? 24 : 28;
                    button.Padding = compact ? new Thickness(4,1,4,1) : new Thickness(6,4,6,4);
                }
            if (Grid.GetRow(child) == 1 && child is Grid titleRow && titleRow.Children.OfType<TextBox>().FirstOrDefault() is { } title)
            {
                title.FontSize = compact ? 17 : 21;
                title.Padding = compact ? new Thickness(1,0,1,2) : new Thickness(1,5,1,10);
            }
            if (Grid.GetRow(child) == 3)
            {
                // Leave the folded corner to the resize thumb, outside the next-note button.
                child.Margin = new Thickness(0,compact ? 3 : 8,compact ? 18 : 12,0);
                foreach (var button in Buttons(child))
                {
                    button.MinHeight = compact ? 24 : 28;
                    button.Padding = compact ? new Thickness(2,0,2,0) : new Thickness(3,4,3,4);
                    button.FontSize = button.Content is string s && s.Length == 1 ? (compact ? 18 : 24) : (compact ? 11 : 13);
                }
            }
        }
    }

    private static IEnumerable<Button> Buttons(DependencyObject parent)
    {
        for (var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i);
            if (child is Button button) yield return button;
            else foreach (var nested in Buttons(child)) yield return nested;
        }
    }
}

public static class PaperStack
{
    // The first exposed sheet is the next note, including when cycling past the end.
    public static IReadOnlyList<string> ColorsBehind(IReadOnlyList<Note> ordered, Guid active, Func<Guid,string> color)
    {
        var index=ordered.ToList().FindIndex(n=>n.Id==active);
        if(index<0)return Array.Empty<string>();
        return Enumerable.Range(1,Math.Min(2,ordered.Count-1))
            .Select(offset=>PaperPalette.Normalize(color(ordered[(index+offset)%ordered.Count].Id))).ToList();
    }
}
