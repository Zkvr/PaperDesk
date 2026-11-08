using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

public sealed class ConflictWindow : Window
{
    public ConflictWindow(DeskController desk, Guid id)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window)); Title = L.T("Сравнить версии"); Width = 850; Height = 600; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var store = desk.Store;
        var row = store.Find(id)!; var remote = row.Conflict!;
        void CloseIfProfileChanged() { if (!ReferenceEquals(desk.Store, store)) Close(); }
        desk.Changed += CloseIfProfileChanged;
        Closed += (_, _) => desk.Changed -= CloseIfProfileChanged;
        var panel = new DockPanel { Margin = new Thickness(24) }; Content = panel;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        foreach (var (title, choice) in new[] { (L.T("Оставить локальную"), "local"), (L.T("Принять облачную"), "remote"), (L.T("Сохранить обе"), "both") })
        { var b = new Button { Content = title }; b.Click += (_, _) => { if (ReferenceEquals(desk.Store, store) && store.Find(id)?.Conflict != null) store.Resolve(id, choice); Close(); }; buttons.Children.Add(b); }
        var heading = new TextBlock { Text = L.T("Обе редакции сохранены. Резервная копия содержит версии для восстановления."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) }; DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); panel.Children.Add(grid);
        var notes = new[] { (L.T("На этом устройстве"), row.Note), (L.T("В облаке") + (remote.DeletedAt != null ? L.T(" • в корзине") : ""), remote) };
        for (var i = 0; i < 2; i++) { var box = new DockPanel { Margin = new Thickness(6) }; Grid.SetColumn(box, i); grid.Children.Add(box); var label = new TextBlock { Text = notes[i].Item1, Margin = new Thickness(0, 0, 0, 10), FontWeight = FontWeights.SemiBold }; DockPanel.SetDock(label, Dock.Top); box.Children.Add(label); box.Children.Add(new TextBox { Text = notes[i].Item2.Title + "\n\n" + notes[i].Item2.Body, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); }
    }
}
