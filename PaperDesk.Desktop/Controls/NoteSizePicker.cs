using System.Windows.Controls.Primitives;

namespace PaperDesk.Desktop.Controls;

public sealed class NoteSizePicker : UniformGrid
{
    public event Action<Size>? SizeSelected;
    public NoteSizePicker()
    {
        Rows=1;Columns=3;
        foreach(var (name,size) in new[]{(L.T("Малый"),new Size(240,220)),(L.T("Средний"),new Size(330,370)),(L.T("Большой"),new Size(420,470))})
        {
            var button=new Button{Content=name,FontSize=11,Padding=new Thickness(3,8,3,8),Margin=new Thickness(1),ToolTip=$"{size.Width:0} × {size.Height:0}"};
            System.Windows.Automation.AutomationProperties.SetName(button,L.F("{0} листик, {1:0} на {2:0}", name, size.Width, size.Height));
            button.Click+=(_,_)=>SizeSelected?.Invoke(size);Children.Add(button);
        }
    }
}
