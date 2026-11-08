using System.Windows.Controls.Primitives;

namespace PaperDesk.Desktop.Controls;

public sealed class PaperColorPicker : UniformGrid
{
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor),typeof(string),typeof(PaperColorPicker),new PropertyMetadata("#F4E5AD",Changed));
    public string SelectedColor { get=>(string)GetValue(SelectedColorProperty);set=>SetValue(SelectedColorProperty,value); }
    public event Action<string>? ColorSelected;
    public PaperColorPicker()
    {
        Rows=1;Columns=6;
        foreach(var (name,color) in PaperPalette.Colors)
        {
            var button=new Button{Tag=color,ToolTip=name,Width=32,Height=32,Margin=new Thickness(1),Padding=new Thickness(3),BorderThickness=new Thickness(2),Background=Brushes.Transparent};
            button.Content=new System.Windows.Shapes.Ellipse{Width=22,Height=22,Fill=PaperPalette.Brush(color)};
            var template=new ControlTemplate(typeof(Button));var ring=new FrameworkElementFactory(typeof(Border));ring.SetValue(Border.CornerRadiusProperty,new CornerRadius(16));ring.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding("BorderBrush"){RelativeSource=new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)});ring.SetValue(Border.BorderThicknessProperty,new Thickness(2));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(VerticalAlignmentProperty,VerticalAlignment.Center);ring.AppendChild(content);template.VisualTree=ring;button.Template=template;
            System.Windows.Automation.AutomationProperties.SetName(button,name+L.T(" бумага"));
            button.Click+=(_,_)=>{SelectedColor=color;ColorSelected?.Invoke(color);};Children.Add(button);
        }
        Refresh();
    }
    private static void Changed(DependencyObject obj,DependencyPropertyChangedEventArgs args)=>((PaperColorPicker)obj).Refresh();
    private void Refresh(){foreach(Button button in Children){var selected=PaperPalette.Normalize(SelectedColor)==(string)button.Tag;button.BorderBrush=selected?new SolidColorBrush(Color.FromRgb(232,200,140)):Brushes.Transparent;System.Windows.Automation.AutomationProperties.SetItemStatus(button,selected?L.T("Выбран"):"");}}
}
