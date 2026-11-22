using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Views;

internal sealed class HelpWindow : UtilityWindow
{
    internal HelpWindow(Window owner, DeskController desk) : base(L.T("Справка PaperDesk"), owner, 690, 490)
    {
        var projects = Section(L.T("Проекты"));
        Add(projects, L.T("Создайте проект"), L.T("В менеджере откройте «Проекты → Новый проект…». Дайте стопке имя и нажмите «Прикрепить файл…» или вставьте ссылку."));
        Add(projects, L.T("Добавьте файл к существующей стопке"), L.T("Выберите её листик в менеджере. Под листиком нажмите «Прикрепить файл или ссылку…»."));
        Add(projects, L.T("Вернитесь к работе"), L.T("В меню «Проекты» нажмите «Продолжить». PaperDesk откроет документ и покажет последний листик проекта."));
        if (!desk.Settings.StackedNotes)
            projects.Children.Add(Secondary(L.T("Включите стопки в разделе «Параметры → Поведение».")));
        else AddExamples(projects, desk);

        var gestures = Section(L.T("Жесты"));
        Add(gestures, L.T("Перенесите листик на стол"), L.T("Перетащите карточку из менеджера на рабочий стол."));
        Add(gestures, L.T("Соберите стопку"), L.T("Возьмитесь за верхнюю ручку и положите листик на другой."));
        Add(gestures, L.T("Снимите верхний листик"), L.T("Потяните за нижнюю ручку, чтобы отделить один листик от стопки."));

        var keys = Section(L.T("Горячие клавиши"));
        Shortcut(keys, desk.Settings.Hotkey, L.T("Новый листик на рабочем столе"));
        Shortcut(keys, desk.Settings.SummonHotkey, L.T("Вызвать заметки поверх окон"));
        Shortcut(keys, "Ctrl+N", L.T("Новый листик в менеджере"));
        Shortcut(keys, "Ctrl+F / Enter", L.T("Поиск / открыть результат"));
        Shortcut(keys, "Delete", L.T("Отправить выбранное в корзину"));
        Shortcut(keys, "Ctrl+Page Up / Page Down", L.T("Сменить листик в стопке"));
        Shortcut(keys, "Ctrl+Shift+F", L.T("Формат текста"));
        AddSections((L.T("Проекты"), projects), (L.T("Жесты"), gestures), (L.T("Горячие клавиши"), keys));
    }

    private void AddExamples(Panel projects, DeskController desk)
    {
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var status = Secondary("");
        projects.Children.Add(actions);
        projects.Children.Add(status);

        void ShowExamples()
        {
            actions.Children.Clear();
            foreach (var (id, name) in StackMenu.NonemptyStacks(desk.Store).Where(stack => DeskController.IsProjectExample(stack.Id)))
            {
                var open = new Button { Content = name, Padding = new Thickness(10, 7, 10, 7) };
                open.Click += (_, _) =>
                {
                    if (desk.ResumeProject(id) is { } source)
                        status.Text = ProjectSource.Open(source) ?? "";
                };
                actions.Children.Add(open);
            }
        }

        if (desk.HasProjectExamples)
        {
            ShowExamples();
            return;
        }
        var tryExamples = new Button { Content = L.T("Попробовать на примерах"), Style = (Style)FindResource("AccentButton"), Padding = new Thickness(12, 7, 12, 7) };
        actions.Children.Add(tryExamples);
        tryExamples.Click += (_, _) =>
        {
            try
            {
                if (desk.AddProjectExamples() is Guid id) desk.ResumeProject(id);
                status.Text = "";
                ShowExamples();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                status.Text = L.T("Не удалось создать примеры проектов.");
            }
        };
    }

    private static void Add(Panel section, string title, string text)
    {
        section.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        var line = Secondary(text);
        line.Margin = new Thickness(0, 3, 0, 14);
        section.Children.Add(line);
    }

    private static void Shortcut(Panel section, string keys, string description)
    {
        var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 9) };
        var shortcut = new System.Windows.Documents.Run(keys) { FontWeight = FontWeights.SemiBold };
        shortcut.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Accent");
        line.Inlines.Add(shortcut);
        line.Inlines.Add("  " + description);
        section.Children.Add(line);
    }
}
