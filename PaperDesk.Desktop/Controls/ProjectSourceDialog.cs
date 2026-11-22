using System.Windows.Automation;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Views;

namespace PaperDesk.Desktop.Controls;

internal sealed class ProjectSourceDialog : UtilityWindow
{
    private ProjectSourceDialog(Window? owner, string title) : base(title, owner, 510, 380)
    {
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
    }

    internal static (string Name, string? Source)? Show(Window? owner, DesktopStack? stack = null)
    {
        var window = new ProjectSourceDialog(owner, stack == null ? L.T("Новый проект") : L.T("Источник проекта"));
        var panel = Section(stack?.Name ?? L.T("Новый проект"));
        panel.Margin = new Thickness(24, 7, 24, 20);
        var source = new TextBox { Text = stack?.ProjectSource ?? "", MaxLength = ProjectSource.MaxLength };
        var name = new TextBox { Text = stack?.Name ?? "", MaxLength = DesktopStack.MaxNameLength, Margin = new Thickness(0, 5, 0, 16) };
        AutomationProperties.SetName(name, L.T("Название проекта"));
        AutomationProperties.SetName(source, L.T("Файл или ссылка проекта"));
        if (stack == null)
        {
            panel.Children.Add(new TextBlock { Text = L.T("Название проекта") });
            panel.Children.Add(name);
        }

        var browse = new Button { Content = L.T("Прикрепить файл…"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0) };
        browse.SetResourceReference(BackgroundProperty, "AccentTint");
        browse.SetResourceReference(BorderBrushProperty, "AccentHover");
        panel.Children.Add(browse);
        panel.Children.Add(new TextBlock { Text = L.T("Файл проекта или ссылка"), Margin = new Thickness(0, 10, 0, 5) });
        panel.Children.Add(source);
        var guidance = Secondary("");
        guidance.Margin = new Thickness(0, 6, 0, 13);
        panel.Children.Add(guidance);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (!string.IsNullOrWhiteSpace(stack?.ProjectSource))
        {
            var detach = new Button { Content = L.T("Отвязать источник") };
            detach.Click += (_, _) => { source.Clear(); source.Focus(); };
            actions.Children.Add(detach);
        }
        actions.Children.Add(new Button { Content = L.T("Отмена"), IsCancel = true });
        var save = new Button { Content = L.T(stack == null ? "Создать" : "Сохранить"), IsDefault = true, Style = (Style)window.FindResource("AccentButton") };
        actions.Children.Add(save);
        panel.Children.Add(actions);
        window.Body.Children.Add(panel);

        void Update()
        {
            var empty = string.IsNullOrWhiteSpace(source.Text);
            var normalized = ProjectSource.Normalize(source.Text);
            var valid = normalized != null;
            var changed = !string.Equals(normalized, stack?.ProjectSource, StringComparison.Ordinal);
            save.IsEnabled = !string.IsNullOrWhiteSpace(name.Text) && changed && (valid || (stack != null && empty));
            guidance.Text = empty && !string.IsNullOrWhiteSpace(stack?.ProjectSource)
                ? L.T("Сохраните, чтобы отвязать источник.")
                : empty || valid
                    ? L.T("Выберите документ или вставьте ссылку на страницу.")
                    : L.T("Укажите локальный документ или ссылку, которая начинается с https:// или http://.");
        }
        name.TextChanged += (_, _) => Update();
        source.TextChanged += (_, _) => Update();
        browse.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Filter = ProjectSource.DocumentFilter, CheckFileExists = true };
            if (picker.ShowDialog(window) == true) source.Text = picker.FileName;
        };
        save.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => (stack == null ? name : source).Focus();
        Update();
        return window.ShowDialog() == true ? (name.Text.Trim(), ProjectSource.Normalize(source.Text)) : null;
    }
}
