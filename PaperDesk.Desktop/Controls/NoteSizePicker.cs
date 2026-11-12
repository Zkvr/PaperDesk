using System.Windows.Controls.Primitives;
using System.Windows.Shapes;

namespace PaperDesk.Desktop.Controls;

public sealed class NoteSizePicker : UniformGrid
{
    public event Action<Size>? SizeSelected;
    public NoteSizePicker()
    {
        Columns = 3;
        foreach (var (name, size, icon) in new[]
        {
            (L.T("Малый"), new Size(240, 220), new Size(12, 11)), (L.T("Средний"), new Size(330, 370), new Size(12, 14)),
            (L.T("Большой"), new Size(420, 470), new Size(15, 17)), (L.T("Широкий"), new Size(460, 260), new Size(18, 10)),
            (L.T("Квадратный"), new Size(330, 330), new Size(13, 13)), (L.T("Узкий"), new Size(240, 420), new Size(10, 17))
        })
        {
            var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var outline = new Rectangle { Width = icon.Width, Height = icon.Height, StrokeThickness = 1, RadiusX = 2, RadiusY = 2 };
            outline.SetResourceReference(Shape.StrokeProperty, "MutedInk");
            face.Children.Add(new Border { Height = 18, Child = outline });
            face.Children.Add(new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) });
            var button = new Button { Content = face, FontSize = 11, Padding = new Thickness(3, 5, 3, 5), Margin = new Thickness(2), ToolTip = $"{size.Width:0} × {size.Height:0}" };
            button.SetResourceReference(Control.BackgroundProperty, "DeskField");
            System.Windows.Automation.AutomationProperties.SetName(button,L.F("{0} листик, {1:0} на {2:0}", name, size.Width, size.Height));
            button.Click += (_, _) => SizeSelected?.Invoke(size);
            Children.Add(button);
        }
    }
}
