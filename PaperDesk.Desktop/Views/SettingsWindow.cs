using System.Windows.Controls.Primitives;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

public sealed class SettingsWindow : Window
{
    private readonly DeskController desk;
    private readonly StackPanel panel = new() { Margin = new Thickness(26) };
    private readonly TextBox shortcut, summonShortcut;
    private readonly CheckBox desktop = new() { Content = L.T("Слой рабочего стола (экспериментальный)"), Margin = new Thickness(0, 14, 0, 10) };
    private readonly CheckBox animations = new() { Content = L.T("Анимации листиков"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox snapNotes = new() { Content = L.T("Привязывать листики к соседним краям"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox stacked = new() { Content = L.T("Показывать листики стопками"), Margin = new Thickness(0, 12, 0, 10) };
    private readonly CheckBox startup = new() { Content = L.T("Запускать при входе в Windows"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly CheckBox trayStartup = new() { Content = L.T("При автозапуске открывать только трей"), Margin = new Thickness(22, 0, 0, 10) };
    private readonly CheckBox noteStartup = new() { Content = L.T("При запуске показывать открытые листики"), Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    public SettingsWindow(DeskController controller)
    {
        desk = controller; Style = (Style)Application.Current.FindResource(typeof(Window)); Title = L.T("PaperDesk — параметры"); Width = 570; Height = 790; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = L.T("Параметры"), FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(ThemeSection());
        panel.Children.Add(new TextBlock { Text = L.T("Заметки и изображения хранятся на этом компьютере."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(message);
        if (!string.IsNullOrEmpty(desk.HotkeyWarning)) message.Text = desk.HotkeyWarning;
        panel.Children.Add(new Separator());
        shortcut = Field(L.T("Новый листик"), desk.Settings.Hotkey);
        summonShortcut = Field(L.T("Вызвать заметки поверх окон"), desk.Settings.SummonHotkey);
        desktop.IsChecked = desk.Settings.DesktopLayer; animations.IsChecked = desk.Settings.Animations; startup.IsChecked = desk.Settings.AutoStart;
        snapNotes.IsChecked = desk.Settings.SnapNotes;
        trayStartup.IsChecked = desk.Settings.StartInTray;
        trayStartup.IsEnabled = startup.IsChecked == true;
        startup.Checked += (_, _) => trayStartup.IsEnabled = true;
        startup.Unchecked += (_, _) => trayStartup.IsEnabled = false;
        noteStartup.IsChecked = desk.Settings.ShowNotesAtStartup;
        panel.Children.Add(new TextBlock { Text = L.T("Жесты и анимация"), FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 8) });
        panel.Children.Add(animations);
        panel.Children.Add(snapNotes);
        panel.Children.Add(desktop); panel.Children.Add(startup);
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
                    Animations = animations.IsChecked == true, SnapNotes = snapNotes.IsChecked == true, AutoStart = startup.IsChecked == true,
                    StartInTray = trayStartup.IsChecked == true, ShowNotesAtStartup = noteStartup.IsChecked == true, StackedNotes = stacked.IsChecked == true
                });
                message.Text = L.T("Параметры сохранены.");
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
        panel.Children.Add(new TextBlock { Text = L.T("Данные: ") + desk.DataRoot, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
    }
    private Border ThemeSection()
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = L.T("Оформление"), FontSize = 18, FontWeight = FontWeights.SemiBold });
        var themes = new UniformGrid { Columns = 3, Margin = new Thickness(-4, 9, -4, 0) };
        content.Children.Add(themes);
        var selected = ThemePalette.Find(desk.Settings.Theme);
        foreach (var theme in ThemePalette.Choices)
        {
            var sample = new Grid { Height = 51, Margin = new Thickness(7, 7, 7, 0) };
            sample.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sample.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            var rail = new Border { Background = ThemePalette.Brush(theme.Panel), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0) };
            rail.Child = new Border { Background = ThemePalette.Brush(theme.Accent), CornerRadius = new CornerRadius(2), Height = 5, Margin = new Thickness(4, 8, 4, 0), VerticalAlignment = VerticalAlignment.Top };
            sample.Children.Add(rail);
            var sheet = new PaperSurface { Fill = PaperPalette.Brush("#F4E5AD") };
            sheet.Child = new TextBlock { Text = "Aa", FontSize = 18, FontFamily = new FontFamily("Segoe Print"), Foreground = (Brush)FindResource("PaperInk"), Margin = new Thickness(7, 5, 4, 4) };
            Grid.SetColumn(sheet, 1);
            sample.Children.Add(sheet);
            var preview = new Border { Background = ThemePalette.Brush(theme.Shell), CornerRadius = new CornerRadius(5), Padding = new Thickness(0, 0, 0, 7), Child = sample };
            var body = new StackPanel();
            body.Children.Add(preview);
            body.Children.Add(new TextBlock { Text = L.T(theme.Label), Margin = new Thickness(2, 7, 2, 1), FontWeight = FontWeights.SemiBold });
            var choice = new RadioButton { Content = body, GroupName = "DeskTheme", IsChecked = theme == selected, Style = (Style)FindResource("ThemeChoice") };
            System.Windows.Automation.AutomationProperties.SetName(choice, L.T(theme.Label));
            choice.Checked += (_, _) =>
            {
                try { desk.SetTheme(theme.Id); }
                catch (IOException ex) { message.Text = ex.Message; RestoreChoice(); }
                catch (UnauthorizedAccessException ex) { message.Text = ex.Message; RestoreChoice(); }
            };
            themes.Children.Add(choice);
        }
        void RestoreChoice()
        {
            var current = Array.IndexOf(ThemePalette.Choices, ThemePalette.Find(desk.Settings.Theme));
            ((RadioButton)themes.Children[current]).IsChecked = true;
        }
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14), Margin = new Thickness(0, 14, 0, 16), Child = content };
        card.SetResourceReference(Border.BackgroundProperty, "DeskPanel");
        return card;
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
