using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

internal sealed class SettingsWindow : UtilityWindow
{
    private readonly DeskController desk;
    private readonly TextBlock message = Secondary("");

    internal SettingsWindow(DeskController controller) : base(L.T("Параметры"), null, 664, 508)
    {
        desk = controller;
        var settings = desk.Settings;
        var appearance = Section(L.T("Оформление"));
        appearance.Children.Add(ThemeChoices());
        Group(appearance, L.T("Листики на рабочем столе"));
        var animations = Option(appearance, L.T("Анимации листиков"), settings.Animations);
        var snap = Option(appearance, L.T("Привязывать листики к соседним краям"), settings.SnapNotes);

        var behavior = Section(L.T("Поведение"));
        var stacks = Option(behavior, L.T("Показывать листики стопками"), settings.StackedNotes);
        var desktop = Option(behavior, L.T("Слой рабочего стола (экспериментальный)"), settings.DesktopLayer);
        desktop.ToolTip = L.T("Если Explorer не поддерживает слой рабочего стола, используются обычные окна.");
        var editor = Option(behavior, L.T("Лёгкий редактор (после перезапуска)"), settings.NativeEditor);
        editor.ToolTip = L.T("Открывать заметки во встроенном редакторе Windows без WebView2.");
        Group(behavior, L.T("Запуск Windows"));
        var startup = Option(behavior, L.T("Запускать при входе в Windows"), settings.AutoStart);
        var tray = Option(behavior, L.T("При автозапуске открывать только трей"), settings.StartInTray);
        tray.Margin = new Thickness(20, 5, 0, 6);
        tray.IsEnabled = settings.AutoStart;
        startup.Checked += (_, _) => tray.IsEnabled = true;
        startup.Unchecked += (_, _) => tray.IsEnabled = false;
        var notes = Option(behavior, L.T("При запуске показывать открытые листики"), settings.ShowNotesAtStartup);

        var keys = Section(L.T("Горячие клавиши"));
        var shortcut = Field(keys, L.T("Новый листик"), settings.Hotkey);
        var summon = Field(keys, L.T("Вызвать заметки поверх окон"), settings.SummonHotkey);
        keys.Children.Add(Secondary(L.T("Введите сочетание, например Ctrl+Alt+N.")));

        var save = new Button { Content = L.T("Сохранить параметры"), Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("AccentButton") };
        var footer = new DockPanel { Margin = new Thickness(20, 0, 18, 14) };
        message.VerticalAlignment = VerticalAlignment.Center;
        message.Margin = new Thickness(0, 0, 9, 0);
        var folder = Secondary(L.T("Данные: ") + desk.DataRoot);
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.TextWrapping = TextWrapping.NoWrap;
        folder.ToolTip = desk.DataRoot;
        folder.FontSize = 11;
        if (desk.HotkeyWarning.Length > 0) message.Text = desk.HotkeyWarning;
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(save);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(message);
        info.Children.Add(folder);
        footer.Children.Add(info);
        DockPanel.SetDock(footer, Dock.Bottom);
        Body.Children.Add(footer);
        AddSections((L.T("Оформление"), appearance), (L.T("Поведение"), behavior), (L.T("Горячие клавиши"), keys));

        save.Click += (_, _) =>
        {
            try
            {
                desk.UpdateSettings(desk.Settings with
                {
                    Hotkey = shortcut.Text, SummonHotkey = summon.Text, DesktopLayer = desktop.IsChecked == true,
                    Animations = animations.IsChecked == true, NativeEditor = editor.IsChecked == true,
                    SnapNotes = snap.IsChecked == true, AutoStart = startup.IsChecked == true,
                    StartInTray = tray.IsChecked == true, ShowNotesAtStartup = notes.IsChecked == true, StackedNotes = stacks.IsChecked == true
                });
                message.Text = L.T("Параметры сохранены.");
            }
            catch (Exception ex) { message.Text = ex.Message; }
        };
    }

    private static void Group(Panel panel, string title) => panel.Children.Add(new TextBlock
    {
        Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 20, 0, 7)
    });

    private CheckBox Option(Panel panel, string title, bool selected)
    {
        var option = new CheckBox { IsChecked = selected, Content = title, Style = (Style)FindResource("SettingsToggle"), Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(option);
        return option;
    }

    private static TextBox Field(Panel panel, string title, string value)
    {
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 3, 0, 6) });
        var input = new TextBox { Text = value, Margin = new Thickness(0, 0, 0, 13) };
        AutomationProperties.SetName(input, title);
        panel.Children.Add(input);
        return input;
    }

    private FrameworkElement ThemeChoices()
    {
        var content = new StackPanel();
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
        return content;
    }

}
