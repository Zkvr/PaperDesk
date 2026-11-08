using System.Windows.Controls.Primitives;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Controls;

public static class StickyTools
{
    public static Popup Create(DeskController desk,Guid id,FrameworkElement target,Action<Placement> apply,bool paletteOnly,Action<Size> resize)
    {
        var panel = new StackPanel { Margin = new Thickness(13), Width = 208 };
        var popup = CreatePopup(desk, target, panel, PlacementMode.Bottom);
        void Save(Placement value){desk.Store.SavePlacement(value);apply(value);}
        var heading=new DockPanel{Margin=new Thickness(3,0,0,9)};
        if(!paletteOnly)
        {
            var favorite=new Button{Content=desk.Store.GetPlacement(id).Favorite?"★":"☆",FontSize=20,Padding=new Thickness(6,0,6,1),Margin=new Thickness(0),Foreground=(Brush)Application.Current.FindResource("Accent"),ToolTip=L.T("Избранное")};DockPanel.SetDock(favorite,Dock.Right);heading.Children.Add(favorite);
            favorite.Click+=(_,_)=>{var p=desk.Store.GetPlacement(id);Save(p with{Favorite=!p.Favorite});favorite.Content=!p.Favorite?"★":"☆";};
        }
        heading.Children.Add(new TextBlock{Text=L.T("Цвет бумаги"),FontSize=11,Foreground=(Brush)Application.Current.FindResource("MutedInk"),VerticalAlignment=VerticalAlignment.Center});panel.Children.Add(heading);
        var colors=new PaperColorPicker{SelectedColor=desk.Store.GetPlacement(id).Color};colors.ColorSelected+=color=>{Save(desk.Store.GetPlacement(id) with{Color=color});if(paletteOnly)popup.IsOpen=false;};panel.Children.Add(colors);
        if(paletteOnly)return popup;
        panel.Children.Add(new TextBlock{Text=L.T("Размер листика"),FontSize=11,Foreground=(Brush)Application.Current.FindResource("MutedInk"),Margin=new Thickness(3,13,0,5)});
        var sizes=new NoteSizePicker();sizes.SizeSelected+=resize;panel.Children.Add(sizes);
        panel.Children.Add(new TextBlock{Text=L.T("Размер текста"),FontSize=11,Foreground=(Brush)Application.Current.FindResource("MutedInk"),Margin=new Thickness(3,13,0,5)});
        var textPanel=new StackPanel{Margin=new Thickness(2,9,2,7)};
        var font=new ComboBox{ItemsSource=new[]{"Segoe UI","Georgia","Consolas"},SelectedItem=desk.Store.GetPlacement(id).Font,FontSize=12,Margin=new Thickness(0,0,0,9)};font.SelectionChanged+=(_,_)=>{if(font.SelectedItem is string name)Save(desk.Store.GetPlacement(id) with{Font=name});};textPanel.Children.Add(font);
        var sizeRow=new Grid();sizeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});sizeRow.ColumnDefinitions.Add(new ColumnDefinition());sizeRow.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var size=new TextBlock{Text=desk.Store.GetPlacement(id).FontSize.ToString("0"),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center,Foreground=(Brush)Application.Current.FindResource("Ink")};Grid.SetColumn(size,1);sizeRow.Children.Add(size);
        foreach(var (symbol,delta,col) in new[]{("−",-2,0),("+",2,2)}){var button=new Button{Content=symbol,Width=36,Padding=new Thickness(6,3,6,3),Margin=new Thickness(0),ToolTip=delta<0?L.T("Уменьшить текст"):L.T("Увеличить текст")};Grid.SetColumn(button,col);sizeRow.Children.Add(button);button.Click+=(_,_)=>{var p=desk.Store.GetPlacement(id);var next=p with{FontSize=Math.Clamp(p.FontSize+delta,12,32)};Save(next);size.Text=next.FontSize.ToString("0");};}
        panel.Children.Add(sizeRow);panel.Children.Add(new Expander{Header=L.T("Шрифт"),Content=textPanel,Margin=new Thickness(3,10,0,9),Foreground=(Brush)Application.Current.FindResource("Ink"),FontSize=12});
        Button Action(string glyph,string label,Action action,bool danger=false)
        {
            var row=new Grid{Width=180};row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(25)});row.ColumnDefinitions.Add(new ColumnDefinition());row.Children.Add(new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=13,VerticalAlignment=VerticalAlignment.Center,Foreground=danger?new SolidColorBrush(Color.FromRgb(225,167,167)):(Brush)Application.Current.FindResource("MutedInk")});var caption=new TextBlock{Text=label,FontSize=12,VerticalAlignment=VerticalAlignment.Center,Foreground=danger?new SolidColorBrush(Color.FromRgb(225,167,167)):(Brush)Application.Current.FindResource("Ink")};Grid.SetColumn(caption,1);row.Children.Add(caption);var b=new Button{Content=row,Padding=new Thickness(10,9,10,9),Margin=new Thickness(0,1,0,1)};b.Click+=(_,_)=>{popup.IsOpen=false;action();};System.Windows.Automation.AutomationProperties.SetName(b,label);panel.Children.Add(b);return b;
        }
        Action("\uE80A",L.T("Открыть менеджер"),desk.ShowManager);
        if (desk.SurfaceStack(id) is Guid group && (group != Guid.Empty || desk.StackNotes(group).Count > 1))
            Action("\uE8BB", L.T("Закрыть стопку"), () => desk.CloseStack(group));
        Action("\uE7B8",L.T("В архив"),()=>desk.ChangeState(id,"archive"));
        Action("\uE74D",L.T("В корзину"),()=>desk.ChangeState(id,"trash"),true);
        return popup;
    }

    public static Popup CreateStackPicker(DeskController desk, Guid active, FrameworkElement target)
    {
        var panel = new StackPanel { Margin = new Thickness(10), Width = 240 };
        var popup = CreatePopup(desk, target, panel, PlacementMode.Top);
        if (desk.SurfaceStack(active) is not Guid group) return popup;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        if (group != Guid.Empty)
        {
            var rename = new Button
            {
                Content = "\uE70F", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13,
                Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0),
                ToolTip = L.T("Переименовать стопку")
            };
            System.Windows.Automation.AutomationProperties.SetName(rename, L.T("Переименовать стопку"));
            DockPanel.SetDock(rename, Dock.Right);
            heading.Children.Add(rename);
            rename.Click += (_, _) => { popup.IsOpen = false; StackMenu.Rename(desk, group, target); };
        }
        heading.Children.Add(new TextBlock
        {
            Text = group == Guid.Empty ? L.T("Листики") : desk.StackName(group),
            TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Foreground = (Brush)Application.Current.FindResource("MutedInk"),
            Margin = new Thickness(8, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = group == Guid.Empty ? null : desk.StackName(group)
        });
        panel.Children.Add(heading);
        var rows = new StackPanel();
        panel.Children.Add(new ScrollViewer
        {
            Content = rows, MaxHeight = 285, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = true
        });

        Button? current = null;
        var notes = desk.StackNotes(group);
        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            var title = string.IsNullOrWhiteSpace(note.Title) ? L.T("Без названия") : note.Title;
            var selected = note.Id == active;
            var row = new DockPanel();
            var number = new TextBlock
            {
                Text = (index + 1).ToString(), Width = 25, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("MutedInk")
            };
            row.Children.Add(number);
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 10, Height = 10, Fill = PaperPalette.Brush(desk.Store.GetPlacement(note.Id).Color),
                Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button
            {
                Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch, ToolTip = title,
                Padding = new Thickness(8, 7, 8, 7), Margin = new Thickness(0, 1, 0, 1)
            };
            if (selected)
            {
                current = button;
                button.Background = new SolidColorBrush(Color.FromArgb(40, 232, 200, 140));
            }
            System.Windows.Automation.AutomationProperties.SetName(button, $"{index + 1}. {title}" + (selected ? L.T(", текущий листик") : ""));
            button.Click += (_, _) => { popup.IsOpen = false; desk.SelectStackNote(note.Id); };
            rows.Children.Add(button);
        }
        var actions = new UniformGrid { Rows = 1, Columns = 2, Margin = new Thickness(0, 7, 0, 0) };
        var minimize = new Button { Content = L.T("Свернуть"), Padding = new Thickness(8, 8, 8, 8), ToolTip = L.T("Восстановить можно через трей или горячую клавишу") };
        minimize.Click += (_, _) => { popup.IsOpen = false; desk.MinimizeStack(group); };
        var close = new Button
        {
            Content = new TextBlock { Text = L.T("Закрыть стопку"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Padding = new Thickness(8, 8, 8, 8), ToolTip = L.T("Листики останутся в менеджере")
        };
        close.Click += (_, _) => { popup.IsOpen = false; desk.CloseStack(group); };
        actions.Children.Add(minimize);
        actions.Children.Add(close);
        panel.Children.Add(actions);
        popup.Opened += (_, _) => { current?.BringIntoView(); current?.Focus(); };
        panel.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            popup.IsOpen = false;
            target.Focus();
            e.Handled = true;
        };
        return popup;
    }

    private static Popup CreatePopup(DeskController desk, FrameworkElement target, FrameworkElement content, PlacementMode placement)
    {
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(250, 37, 42, 50)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Child = content, Margin = new Thickness(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 15, ShadowDepth = 4, Opacity = .3 }
        };
        return new Popup
        {
            PlacementTarget = target, Placement = placement, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = desk.Settings.Animations && SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None,
            VerticalOffset = placement == PlacementMode.Top ? -3 : 7, Child = frame
        };
    }
}
