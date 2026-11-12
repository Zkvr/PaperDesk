using System.Text.Json;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Data.Sqlite;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Controls;

namespace PaperDesk.Desktop.Views;

public partial class ManagerWindow : Window
{
    private readonly DeskController desk;
    private readonly ManagerViewModel vm;
    private readonly DesktopCardDrag cardDrag;
    private Guid? selected;
    private bool loading;
    private bool selecting, purging;
    private string shownTitle = "", shownBody = "";
    private Popup? appearance;
    private readonly System.Windows.Media.Effects.DropShadowEffect editorShadow = new() { Color = Colors.Black };
    public ManagerWindow(DeskController controller)
    {
        desk = controller; vm = new(desk); InitializeComponent(); DataContext = vm;
        cardDrag = new DesktopCardDrag(this, Cards, desk, () => selecting);
        EditBody.ConnectTitle(EditTitle);
        EditBody.ConnectFormatMenu(FormatButton, EditorPaper);
        EditorPictures.ConnectDrop(EditorPaper, EditBody);
        if (Application.Current is App) EditBody.EnableWebEditor(desk, CreateNote, FocusSearch);
        ConfigureUpdates();
        desk.Changed += Refresh;
        Closed += (_, _) => { if (appearance != null) appearance.IsOpen = false; EditorCrumple.Cancel(); desk.Changed -= Refresh; };
        UpdateGrouping();
        Refresh();
        Loaded += (_, _) => WindowMaterial.Reveal(Workspace, desk.Settings.Animations);
        IsVisibleChanged += (_, _) => { if (!IsVisible) StopMotion(); };
        StateChanged += (_, _) => RootFrame.CornerRadius = new CornerRadius(WindowState == WindowState.Maximized ? 0 : 14);
        SizeChanged += (_, _) => UpdateEditorWidth();
        PreviewKeyDown += ManagerKeyDown;
    }
    private void Refresh()
    {
        var previous = SelectedIds().ToHashSet();
        loading = true;
        vm.Refresh();
        if (vm.Filter == "conflict" && !vm.HasConflicts)
        {
            AllNotesSection.IsChecked = true;
            SetSection("all");
            previous.Clear();
            vm.Refresh();
        }
        foreach (var card in vm.Notes)
            if (previous.Contains(card.Id) && !Cards.SelectedItems.Contains(card)) Cards.SelectedItems.Add(card);
        UndoButton.IsEnabled = desk.CanUndo;
        DesktopStacksButton.Visibility = vm.HasDesktopStacks ? Visibility.Visible : Visibility.Collapsed;
        ConflictsSection.Visibility = vm.HasConflicts ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = vm.Notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WelcomePanel.Visibility = desk.Settings.WelcomePending && vm.AllCount + vm.ArchiveCount + vm.TrashCount == 0
            ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = !string.IsNullOrWhiteSpace(vm.Search) ? L.T("Ничего не найдено") : vm.Filter == "all" ? L.T("Пока нет заметок") : L.T("Здесь пока пусто");
        EmptyHint.Text = !string.IsNullOrWhiteSpace(vm.Search) ? L.T("Измените запрос или выберите другой раздел.")
            : vm.Filter == "all" ? L.T("Нажмите «Новый листик», чтобы начать.") : L.T("Заметки появятся здесь, когда вы добавите их в этот раздел.");
        loading = false;
        UpdateSelection();
    }
    private void LoadEditor()
    {
        if (EditorCrumple?.IsPlaying == true) return;
        var row = selected.HasValue ? desk.Store.Find(selected.Value) : null; loading = true;
        UpdateEditorWidth(row != null);
        if (EditTitle.Text != (row?.Note.Title ?? "")) EditTitle.Text = row?.Note.Title ?? "";
        if (EditBody.Text != (row?.Note.Body ?? "")) { var pos = EditBody.CaretIndex; EditBody.Text = row?.Note.Body ?? ""; EditBody.CaretIndex = Math.Min(pos, EditBody.Text.Length); }
        shownTitle = EditTitle.Text;
        shownBody = EditBody.Text;
        FormatButton.IsEnabled = EditTitle.IsEnabled = EditBody.IsEnabled = row != null;
        var canAttach = row?.Note is { ArchivedAt: null, DeletedAt: null };
        AddImagesButton.Visibility = canAttach ? Visibility.Visible : Visibility.Collapsed;
        EditorPictures.Bind(desk.Store, row?.Note.Id, canAttach);
        if (row != null)
        {
            var placement = desk.Store.GetPlacement(row.Note.Id);
            EditorPaper.ApplyAppearance(placement);
            EditorPaper.ApplyShadow(editorShadow, placement.Shadow);
            EditorColorDot.Fill = EditorPaper.Fill;
            AppearanceButton.Visibility = row.Note.DeletedAt == null ? Visibility.Visible : Visibility.Collapsed;
            EditBody.FontFamily = new FontFamily(placement.Font);
            EditBody.FontSize = Math.Clamp(placement.FontSize, 12, 32);
            EditBody.QueueWebSync();
            var active = row.Note.ArchivedAt == null && row.Note.DeletedAt == null;
            var onDesktop = active && desk.IsOnDesktop(placement);
            PreviewState.Text = row.Note.DeletedAt != null ? L.T("В корзине") : row.Note.ArchivedAt != null ? L.T("В архиве") :
                onDesktop ? L.T("На столе") : L.T("Не на столе");
            if (onDesktop && desk.Settings.StackedNotes && placement.StackId is Guid group)
                PreviewState.Text = desk.StackName(group);
            PreviewState.ToolTip = PreviewState.Text;
            FavoriteButton.Content = placement.Favorite ? "★" : "☆";
            FavoriteButton.ToolTip = placement.Favorite ? L.T("Убрать из избранного") : L.T("В избранное");
            ShowButton.Content = onDesktop ? L.T("Перейти к листику") : L.T("Показать на столе");
            ShowButton.Visibility = FavoriteButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            MoreButton.Visibility = Visibility.Visible;
            RestoreButton.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        }
        var rejected = row != null && desk.Store.RejectedNotes().Contains(row.Note.Id);
        EditorHint.Visibility = row?.Conflict != null || rejected ? Visibility.Visible : Visibility.Collapsed;
        EditorHint.Text = row?.Conflict != null ? L.T("Две версии сохранены. Выберите вариант ниже.")
            : L.T("Правка осталась на этом устройстве. После редактирования отправим её снова.");
        ConflictButton.Visibility = row?.Conflict != null ? Visibility.Visible : Visibility.Collapsed; loading = false;
    }
    private void CardSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) UpdateSelection();
    }
    private Guid[] SelectedIds() => Cards.SelectedItems.OfType<NoteCard>().Select(card => card.Id).ToArray();

    private void UpdateSelection()
    {
        var ids = SelectedIds();
        Guid? next = !selecting && ids.Length == 1 ? ids[0] : null;
        var changed = next != selected;
        selected = next;
        LoadEditor();
        if (changed) EditBody.ResetNavigation();
        var all = ids.Select(id => desk.Store.Find(id)?.Note).OfType<Note>().ToArray();
        BulkBar.Visibility = selecting || ids.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        SelectionCount.Text = L.F("Выбрано: {0}", ids.Length);
        BulkStack.Visibility = desk.Settings.StackedNotes && all.Any(n => n.ArchivedAt == null && n.DeletedAt == null)
            ? Visibility.Visible : Visibility.Collapsed;
        BulkUnstack.Visibility = BulkStack.Visibility == Visibility.Visible &&
            all.Any(note => note is { ArchivedAt: null, DeletedAt: null } && desk.Store.GetPlacement(note.Id).StackId.HasValue)
            ? Visibility.Visible : Visibility.Collapsed;
        BulkArchive.Visibility = all.Any(n => n.ArchivedAt == null && n.DeletedAt == null) ? Visibility.Visible : Visibility.Collapsed;
        BulkTrash.Visibility = all.Any(n => n.DeletedAt == null) ? Visibility.Visible : Visibility.Collapsed;
        BulkRestore.Visibility = all.Any(n => n.ArchivedAt != null || n.DeletedAt != null) ? Visibility.Visible : Visibility.Collapsed;
        BulkPurge.Visibility = all.Length > 0 && all.All(n => n.DeletedAt != null) ? Visibility.Visible : Visibility.Collapsed;
        BulkPurge.IsEnabled = !purging;
    }
    private void SelectModeClick(object sender, RoutedEventArgs e)
    {
        SetSelecting(!selecting);
        UpdateSelection();
    }

    private void SetSelecting(bool enabled)
    {
        selecting = enabled;
        Cards.SelectionMode = selecting ? SelectionMode.Multiple : SelectionMode.Extended;
        if (!selecting) Cards.UnselectAll();
        SelectButton.Content = selecting ? L.T("Готово") : L.T("Выбрать");
    }
    private void SelectAllClick(object sender, RoutedEventArgs e) => Cards.SelectAll();
    private void AssignStackClick(object sender, RoutedEventArgs e) => StackMenu.Assign(desk, (FrameworkElement)sender, SelectedIds());
    private void ExtractStackClick(object sender, RoutedEventArgs e) => desk.ExtractFromStack(SelectedIds());
    private void DesktopStacksClick(object sender, RoutedEventArgs e) => StackMenu.Show(desk, (FrameworkElement)sender);
    private void BulkArchiveClick(object sender, RoutedEventArgs e) => ApplyBulk("archive");
    private void BulkTrashClick(object sender, RoutedEventArgs e) => ApplyBulk("trash");
    private void BulkRestoreClick(object sender, RoutedEventArgs e) => ApplyBulk("restore");
    private void ApplyBulk(string action)
    {
        desk.ChangeStates(SelectedIds(), action);
        Cards.UnselectAll();
        Refresh();
    }
    private async void PurgeClick(object sender, RoutedEventArgs e)
    {
        var ids = SelectedIds();
        if (purging || ids.Length == 0) return;
        var store = desk.Store;
        if (MessageBox.Show(this, L.F("Удалить выбранные заметки навсегда ({0})? Восстановить их будет нельзя.", ids.Length),
            L.T("Окончательное удаление"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes ||
            !ReferenceEquals(store, desk.Store)) return;
        purging = true;
        BulkPurge.IsEnabled = false;
        try
        {
            await desk.PurgeTrash(ids);
            Cards.UnselectAll();
            Refresh();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PaperDesk"); }
        finally { purging = false; BulkPurge.IsEnabled = true; }
    }
    private void FormatClick(object sender, RoutedEventArgs e) => EditBody.OpenFormatMenu();
    private void UpdateEditorWidth() => UpdateEditorWidth(selected.HasValue);
    private void UpdateEditorWidth(bool open) =>
        EditorColumn.Width = new GridLength(open ? Math.Clamp((ActualWidth - 188) * .44, 335, 570) : 0);

    private void ManagerKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.F1) { ShowHelp(); e.Handled = true; return; }
            if (!Cards.IsKeyboardFocusWithin) return;
            if (e.Key == Key.Enter && Cards.SelectedItem is NoteCard && !selecting && Cards.SelectedItems.Count == 1)
            {
                EditTitle.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && vm.Filter != "trash" && Cards.SelectedItems.Count != 0)
            {
                ApplyBulk("trash");
                e.Handled = true;
            }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.N) { CreateNote(); e.Handled = true; return; }
        if (e.Key == Key.F) { FocusSearch(); e.Handled = true; return; }
        if (e.Key != Key.V || AddImagesButton.Visibility != Visibility.Visible ||
            !(EditorPaper.IsKeyboardFocusWithin || Cards.IsKeyboardFocusWithin)) return;
        e.Handled = EditorPictures.Paste();
    }

    private void AddImagesClick(object sender, RoutedEventArgs e) => EditorPictures.Browse();

    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (loading || selected is not Guid id) return;
        desk.Store.SaveText(id, sender == EditTitle && EditTitle.Text != shownTitle ? EditTitle.Text : null,
            sender == EditBody && EditBody.Text != shownBody ? EditBody.Text : null, shownTitle, shownBody);
        shownTitle = EditTitle.Text;
        shownBody = EditBody.Text;
    }
    private void FilterClick(object sender, RoutedEventArgs e)
    {
        var filter = (string)((FrameworkElement)sender).Tag;
        if (filter == vm.Filter) return;
        SetSection(filter);
        Refresh();
    }

    private void SetSection(string filter)
    {
        cardDrag.Cancel();
        SetSelecting(false);
        vm.Filter = filter;
        FilterTitle.Text = filter switch
        {
            "visible" => L.T("На столе"), "hidden" => L.T("Не на столе"), "favorite" => L.T("Избранное"),
            "archive" => L.T("Архив"), "trash" => L.T("Корзина"), "conflict" => L.T("Конфликты"),
            _ => L.T("Все заметки")
        };
        UpdateGrouping();
    }
    private void CollapsePreviewClick(object sender, RoutedEventArgs e)
    {
        selected = null;
        Cards.SelectedItem = null;
        EditorCrumple.Cancel();
        FinishCrumple();
    }
    private void GridViewClick(object sender, RoutedEventArgs e) => SetView(false);
    private void ListViewClick(object sender, RoutedEventArgs e) => SetView(true);
    private void SetView(bool list)
    {
        Cards.Tag = list ? "list" : "grid";
        Cards.ItemsPanel = (ItemsPanelTemplate)Resources[list ? "ListPanel" : "GridPanel"];
        GridViewButton.SetResourceReference(ForegroundProperty, list ? "MutedInk" : "Accent");
        ListViewButton.SetResourceReference(ForegroundProperty, list ? "Accent" : "MutedInk");
    }

    private void UpdateGrouping()
    {
        var groups = CollectionViewSource.GetDefaultView(vm.Notes).GroupDescriptions;
        if (vm.Filter == "all")
        {
            if (groups.Count != 0) return;
            Cards.GroupStyle.Add((GroupStyle)Resources["DeskGroupStyle"]);
            groups.Add(new PropertyGroupDescription(nameof(NoteCard.Section)));
        }
        else if (groups.Count != 0)
        {
            // An empty grouped view can retain its group panel when the next filter fills it.
            Cards.GroupStyle.Clear();
            groups.Clear();
        }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e) { if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this); }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (vm != null) { vm.Search = SearchBox.Text; Refresh(); } }
    internal void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key == Key.Escape && SearchBox.Text.Length != 0) { SearchBox.Clear(); e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Down) || Cards.Items.Count == 0 ||
            Cards.Items[0] is not NoteCard result) return;
        SetSelecting(false);
        Cards.SelectedItem = result;
        Cards.ScrollIntoView(result);
        if (e.Key == Key.Enter) EditTitle.Focus();
        else
        {
            Cards.UpdateLayout();
            if (Cards.ItemContainerGenerator.ContainerFromItem(result) is ListBoxItem container) container.Focus();
            else Cards.Focus();
        }
        e.Handled = true;
    }
    private void SortChanged(object sender, SelectionChangedEventArgs e) { if (vm != null && Cards != null) { vm.Sort = SortBox.SelectedIndex; Refresh(); } }
    private void NewClick(object sender, RoutedEventArgs e) => CreateNote();
    private void CreateNote()
    {
        var id = desk.CreateNote();
        desk.DismissWelcome();
        OpenNote(id);
    }
    private void OpenNote(Guid id)
    {
        AllNotesSection.IsChecked = true;
        SetSection("all");
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        else Refresh();
        if (vm.Notes.FirstOrDefault(card => card.Id == id) is not { } note) return;
        Cards.SelectedItem = note;
        Cards.ScrollIntoView(note);
        EditTitle.Focus();
    }
    private void ExampleClick(object sender, RoutedEventArgs e)
    {
        if (desk.CreateWelcomeExample() is Guid id) OpenNote(id);
    }
    private void DismissWelcomeClick(object sender, RoutedEventArgs e)
    {
        desk.DismissWelcome();
        Refresh();
    }
    private void HelpClick(object sender, RoutedEventArgs e) => ShowHelp();
    private void ShowHelp() => MessageBox.Show(this,
        L.T("Верхняя ручка перемещает заметку или стопку. Перетащите её на другую заметку, чтобы собрать стопку. Потяните нижнюю ручку, чтобы отделить один листик.") +
        "\n\n" + L.T("Ctrl+N — новый листик в менеджере\nCtrl+F — поиск, Enter — открыть результат\nDelete — отправить выбранное в корзину\nCtrl+Page Up / Page Down — сменить листик в открытой стопке\nCtrl+Shift+F — форматирование при редактировании") +
        "\n\n" + L.F("{0} — новый листик на столе\n{1} — показать листики поверх окон", desk.Settings.Hotkey, desk.Settings.SummonHotkey),
        L.T("Горячие клавиши и жесты"), MessageBoxButton.OK);
    private void SettingsClick(object sender, RoutedEventArgs e) => desk.ShowSettings();
    private void ShowClick(object sender, RoutedEventArgs e) { if (selected.HasValue) desk.ShowSticky(selected.Value); }
    private void HideClick(object sender, RoutedEventArgs e) { if (selected.HasValue) desk.HideSticky(selected.Value); }
    private void ArchiveClick(object sender, RoutedEventArgs e) { if (selected.HasValue) desk.ChangeState(selected.Value, "archive"); }
    private void TrashClick(object sender, RoutedEventArgs e)
    {
        if (selected is not Guid id || EditorCrumple.IsPlaying || desk.Store.Find(id)?.Note is not { DeletedAt: null }) return;
        EditBody.EndEdit();
        desk.ChangeState(id, "trash", animateSticky: false);
        if (desk.Settings.Animations && SystemParameters.ClientAreaAnimation && EditorCrumple.TryPlay(EditorPaper, FinishCrumple))
        {
            EditorPaper.IsEnabled = false;
            EditorPaper.Opacity = 0;
            EditorActions.IsEnabled = false;
        }
        if (vm.Filter != "trash") Cards.UnselectAll();
        Refresh();
    }

    private void FinishCrumple()
    {
        EditorPaper.Opacity = 1;
        EditorPaper.IsEnabled = true;
        EditorActions.IsEnabled = true;
        LoadEditor();
    }

    internal void StopMotion()
    {
        Workspace.BeginAnimation(OpacityProperty, null);
        EditorCrumple.FinishNow();
    }
    private void RestoreClick(object sender, RoutedEventArgs e) { if (selected.HasValue) desk.ChangeState(selected.Value, "restore"); }
    private void FileMenuClick(object sender, RoutedEventArgs e)
    {
        var actions = new List<(string, RoutedEventHandler)>
        {
            (L.T("Импортировать заметки…"), ImportClick), (L.T("Экспортировать копию…"), ExportClick)
        };
        OpenMenu((Button)sender, actions);
    }
    private void NoteMenuClick(object sender, RoutedEventArgs e)
    {
        var store = desk.Store;
        if (selected is not Guid id || store.Find(id)?.Note is not { } note) return;
        var button = (Button)sender;
        var actions = new List<(string, RoutedEventHandler)>();
        if (note.DeletedAt != null)
        {
            actions.Add((L.T("Удалить навсегда"), PurgeClick));
            OpenMenu(button, actions);
            return;
        }
        actions.Add((L.T("Дублировать заметку"), (_, _) =>
        {
            if (!ReferenceEquals(store, desk.Store)) return;
            desk.FlushWindows();
            if (store.Duplicate(id) is Guid copy) OpenNote(copy);
        }));
        actions.Add((L.T("Размер текста…"), (_, _) =>
        {
            if (ReferenceEquals(store, desk.Store)) OpenTextSize(button, id);
        }));
        if (note.ArchivedAt == null)
        {
            var placement = desk.Store.GetPlacement(id);
            if (desk.Settings.StackedNotes)
            {
                actions.Add((L.T("В стопку…"), (_, _) =>
                {
                    if (ReferenceEquals(store, desk.Store)) StackMenu.Assign(desk, button, [id]);
                }));
                if (placement.StackId.HasValue)
                    actions.Add((L.T("Извлечь из стопки"), (_, _) =>
                    {
                        if (ReferenceEquals(store, desk.Store)) desk.ExtractFromStack([id]);
                    }));
            }
            if (placement.Visible) actions.Add((L.T("Скрыть со стола"), HideClick));
            actions.Add((L.T("В архив"), ArchiveClick));
        }
        actions.Add((L.T("В корзину"), TrashClick));
        OpenMenu(button, actions);
    }

    private void AppearanceClick(object sender, RoutedEventArgs e)
    {
        if (selected is not Guid id) return;
        if (appearance != null) appearance.IsOpen = false;
        var popup = StickyTools.CreateAppearance(desk, id, (Button)sender, EditorPaper, PlacementMode.Left, _ => LoadEditor());
        appearance = popup;
        popup.Closed += (_, _) => { if (appearance == popup) appearance = null; };
        popup.IsOpen = true;
    }
    private void OpenTextSize(Button button, Guid id)
    {
        var store = desk.Store;
        var current = store.GetPlacement(id).FontSize;
        var sizes = new[] { 12, 14, 17, 20, 24, 28, 32 };
        OpenMenu(button, sizes.Select(size =>
            ((current == size ? "✓  " : "") + size.ToString(), (RoutedEventHandler)((_, _) =>
            {
                if (!ReferenceEquals(store, desk.Store) || store.Find(id) == null) return;
                store.SavePlacement(store.GetPlacement(id) with { FontSize = size });
                LoadEditor();
            }))));
    }
    private static void OpenMenu(Button button, IEnumerable<(string Label, RoutedEventHandler Handler)> actions)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (label, handler) in actions)
        {
            var item = new MenuItem { Header = label };
            item.Click += handler;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    private void UndoClick(object sender, RoutedEventArgs e) => desk.Undo();
    private void FavoriteClick(object sender, RoutedEventArgs e) { if (selected.HasValue) { var p = desk.Store.GetPlacement(selected.Value); desk.Store.SavePlacement(p with { Favorite = !p.Favorite }); } }
    private void ConflictClick(object sender, RoutedEventArgs e)
    {
        if (selected is Guid id && desk.Store.Find(id) is { Conflict: not null })
            new ConflictWindow(desk, id) { Owner = this }.ShowDialog();
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var store = desk.Store;
        desk.FlushWindows();
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "PaperDesk-" + DateTime.Now.ToString("yyyy-MM-dd"), Filter = L.T("Резервная копия JSON|*.json|Markdown|*.md") };
        if (dialog.ShowDialog(this) != true || !CheckFileProfile(store)) return;
        try { store.ExportFile(dialog.FileName, markdown: dialog.FilterIndex != 1); }
        catch (Exception ex) when (IsFileOperationError(ex))
        {
            ErrorLog.Write(desk.DataRoot, ErrorLog.Area.Export, ex);
            MessageBox.Show(this, ex.Message, L.T("Экспорт"));
        }
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        var store = desk.Store;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Заметки|*.txt;*.md;*.json"), Multiselect = true };
        if (dialog.ShowDialog(this) != true || !CheckFileProfile(store)) return;
        try
        {
            try { store.ImportFiles(dialog.FileNames); }
            catch (LargeBackupException ex)
            {
                if (MessageBox.Show(this, ex.Message + "\n\n" +
                    L.T("Для больших копий потребуется больше памяти. Импортировать выбранные файлы?"),
                    L.T("Импорт"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes ||
                    !CheckFileProfile(store)) return;
                store.ImportFiles(dialog.FileNames, allowLargeBackup: true);
            }
        }
        catch (Exception ex) when (IsFileOperationError(ex))
        {
            ErrorLog.Write(desk.DataRoot, ErrorLog.Area.Import, ex);
            MessageBox.Show(this, ex.Message, L.T("Импорт"));
            return;
        }
        Refresh();
    }

    private static bool IsFileOperationError(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidDataException or SqliteException or JsonException;

    private bool CheckFileProfile(NoteStore store)
    {
        if (ReferenceEquals(desk.Store, store)) return true;
        MessageBox.Show(this, L.T("Профиль сменился. Повторите действие в нужном аккаунте."), "PaperDesk");
        return false;
    }
}
