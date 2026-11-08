using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

public sealed class SettingsWindow : Window
{
    private readonly DeskController desk;
    private readonly StackPanel panel = new() { Margin = new Thickness(26) };
    private readonly TextBox shortcut, summonShortcut;
    private readonly CheckBox desktop = new() { Content = L.T("Слой рабочего стола (экспериментальный)"), Margin = new Thickness(0, 14, 0, 10) };
    private readonly CheckBox animations = new() { Content = L.T("Анимация появления"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox stacked = new() { Content = L.T("Показывать листики стопками"), Margin = new Thickness(0, 12, 0, 10) };
    private readonly CheckBox startup = new() { Content = L.T("Запускать при входе в Windows"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox trayStartup = new() { Content = L.T("При автозапуске открывать только трей"), Margin = new Thickness(22, 0, 0, 10) };
    private readonly CheckBox noteStartup = new() { Content = L.T("При запуске показывать открытые листики"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    public SettingsWindow(DeskController controller)
    {
        desk = controller; Style = (Style)Application.Current.FindResource(typeof(Window)); Title = L.T("PaperDesk — параметры"); Width = 570; Height = 790; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = L.T("Параметры"), FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = L.T("Заметки и изображения хранятся на этом компьютере."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(message);
        if (!string.IsNullOrEmpty(desk.HotkeyWarning)) message.Text = desk.HotkeyWarning;
        panel.Children.Add(new Separator());
        shortcut = Field(L.T("Новый листик"), desk.Settings.Hotkey);
        summonShortcut = Field(L.T("Вызвать заметки поверх окон"), desk.Settings.SummonHotkey);
        desktop.IsChecked = desk.Settings.DesktopLayer; animations.IsChecked = desk.Settings.Animations; startup.IsChecked = desk.Settings.AutoStart;
        trayStartup.IsChecked = desk.Settings.StartInTray;
        trayStartup.IsEnabled = startup.IsChecked == true;
        startup.Checked += (_, _) => trayStartup.IsEnabled = true;
        startup.Unchecked += (_, _) => trayStartup.IsEnabled = false;
        noteStartup.IsChecked = desk.Settings.ShowNotesAtStartup;
        panel.Children.Add(desktop); panel.Children.Add(animations); panel.Children.Add(startup);
        panel.Children.Add(trayStartup); panel.Children.Add(noteStartup);
        stacked.IsChecked=desk.Settings.StackedNotes; panel.Children.Add(stacked);
        panel.Children.Add(new TextBlock { Text = L.T("Если Explorer не поддерживает слой рабочего стола, используются обычные окна. Закрытые листики остаются в менеджере."), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var save = new Button { Content = L.T("Сохранить параметры"), Margin = new Thickness(0, 18, 0, 8) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            try
            {
                desk.UpdateSettings(desk.Settings with
                {
                    Hotkey = shortcut.Text, SummonHotkey = summonShortcut.Text, DesktopLayer = desktop.IsChecked == true,
                    Animations = animations.IsChecked == true, AutoStart = startup.IsChecked == true,
                    StartInTray = trayStartup.IsChecked == true, ShowNotesAtStartup = noteStartup.IsChecked == true, StackedNotes = stacked.IsChecked == true
                });
                message.Text = L.T("Параметры сохранены.");
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        panel.Children.Add(new TextBlock { Text = L.T("Данные: ") + desk.DataRoot, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
    }
    private TextBox Field(string title, string value, Panel? parent = null)
    {
        parent ??= panel;
        parent.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 4) });
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 5) };
        parent.Children.Add(input);
        return input;
    }
}
