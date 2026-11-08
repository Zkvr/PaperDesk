using System.Windows.Controls.Primitives;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Controls;

internal static class StackMenu
{
    public static void Assign(DeskController desk, FrameworkElement target, IReadOnlyList<Guid> ids)
    {
        if (ids.Count == 0) return;
        var store = desk.Store;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        var selected = ids.Select(id => store.GetPlacement(id).StackId).Distinct().ToArray();
        void Move(Guid? group)
        {
            if (ReferenceEquals(store, desk.Store)) desk.AssignStack(ids, group);
        }
        void Destination(string name, Guid? group) =>
            Add(menu, (selected.Length == 1 && selected[0] == group ? "✓  " : "") + name, () => Move(group));

        Destination(L.T("Общая стопка"), Guid.Empty);
        foreach (var (id, name) in NonemptyStacks(store).Where(stack => stack.Id != Guid.Empty))
            Destination(name, id);
        Destination(L.T("Отдельные листики"), null);
        menu.Items.Add(new Separator());
        Add(menu, L.T("Новая стопка…"), () =>
        {
            var name = AskName(Window.GetWindow(target));
            if (name != null && ReferenceEquals(store, desk.Store)) Move(desk.CreateStack(name));
        });
        menu.IsOpen = true;
    }

    public static void Show(DeskController desk, FrameworkElement target)
    {
        var store = desk.Store;
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        void ShowGroup(Guid id) { if (ReferenceEquals(store, desk.Store)) desk.ShowStack(id); }
        foreach (var (id, name) in NonemptyStacks(store))
            Add(menu, name, () => ShowGroup(id));
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    internal static IEnumerable<(Guid Id, string Name)> NonemptyStacks(NoteStore store)
    {
        var placements = store.AllPlacements();
        var groups = store.All().Where(row => row.Note is { ArchivedAt: null, DeletedAt: null })
            .Select(row => placements.GetValueOrDefault(row.Note.Id)?.StackId)
            .OfType<Guid>().ToHashSet();
        if (groups.Contains(Guid.Empty)) yield return (Guid.Empty, L.T("Общая стопка"));
        foreach (var stack in store.AllStacks().Where(stack => stack.Id != Guid.Empty && groups.Contains(stack.Id))
                     .OrderBy(stack => stack.Name, StringComparer.CurrentCulture))
            yield return (stack.Id, stack.Name);
    }

    public static void Rename(DeskController desk, Guid id, FrameworkElement target)
    {
        var store = desk.Store;
        if (store.GetStack(id) is not { } stack) return;
        var name = AskName(Window.GetWindow(target), stack.Name);
        if (name != null && ReferenceEquals(store, desk.Store)) desk.RenameStack(id, name);
    }

    private static void Add(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = new TextBlock { Text = label, MaxWidth = 330, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static string? AskName(Window? owner, string? current = null)
    {
        var input = new TextBox { Text = current ?? "", MaxLength = DesktopStack.MaxNameLength, Margin = new Thickness(0, 8, 0, 16) };
        System.Windows.Automation.AutomationProperties.SetName(input, L.T("Название стопки"));
        var window = new Window
        {
            Title = L.T(current == null ? "Новая стопка" : "Переименовать стопку"),
            Owner = owner, Width = 340, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T("Название стопки") });
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = L.T("Отмена"), IsCancel = true };
        var confirm = new Button { Content = L.T(current == null ? "Создать" : "Сохранить"),
            IsDefault = true, IsEnabled = !string.IsNullOrWhiteSpace(input.Text) };
        input.TextChanged += (_, _) => confirm.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        confirm.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        window.Content = panel;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
