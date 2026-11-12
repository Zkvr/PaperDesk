using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace PaperDesk.Desktop.Controls;

public sealed class PaperColorPicker : UniformGrid
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(
        nameof(SelectedColor), typeof(string), typeof(PaperColorPicker), new PropertyMetadata("#F4E5AD", Changed));
    public string SelectedColor { get => (string)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public event Action<string>? ColorSelected;

    public PaperColorPicker()
    {
        Columns = 6;
        var style = (Style)FindResource("PaperSwatch");
        foreach (var (name, color) in PaperPalette.Colors)
        {
            var button = new Button
            {
                Style = style, Tag = color, ToolTip = name,
                Content = new Ellipse { Width = 22, Height = 22, Fill = PaperPalette.Brush(color) }
            };
            AutomationProperties.SetName(button, name + L.T(" бумага"));
            button.Click += (_, _) => { SelectedColor = color; ColorSelected?.Invoke(color); };
            Children.Add(button);
        }
        Refresh();
    }

    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args) => ((PaperColorPicker)obj).Refresh();

    private void Refresh()
    {
        var color = PaperPalette.Normalize(SelectedColor);
        foreach (Button button in Children)
        {
            var selected = string.Equals(color, (string)button.Tag, StringComparison.OrdinalIgnoreCase);
            if (selected) button.SetResourceReference(Control.BorderBrushProperty, "Accent");
            else button.ClearValue(Control.BorderBrushProperty);
            AutomationProperties.SetItemStatus(button, selected ? L.T("Выбран") : "");
        }
    }
}
