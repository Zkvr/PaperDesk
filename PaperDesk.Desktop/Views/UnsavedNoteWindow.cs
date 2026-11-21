namespace PaperDesk.Desktop.Views;

internal sealed record NoteDraft(string? Title, string? Body, string ShownTitle, string ShownBody,
    IReadOnlyDictionary<Guid, NoteImageLayout>? Layouts)
{
    internal NoteDraft Add(NoteDraft newer) => new(
        newer.Title ?? Title, newer.Body ?? Body,
        Title == null ? newer.ShownTitle : ShownTitle,
        Body == null ? newer.ShownBody : ShownBody,
        newer.Layouts ?? Layouts);
}

internal sealed class UnsavedNoteWindow : Window
{
    private readonly TextBox text = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private bool saved;
    internal NoteDraft Draft { get; private set; }

    internal UnsavedNoteWindow(NoteDraft draft, Action retry)
    {
        Draft = draft;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = L.T("Заметка не сохранена");
        Width = 520;
        Height = 340;
        MinWidth = 350;
        MinHeight = 240;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new DockPanel { Margin = new Thickness(18) };
        Content = panel;
        var status = new TextBlock
        {
            Text = L.T("PaperDesk не смог записать заметку. Повторите сохранение или скопируйте черновик."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(status, Dock.Top);
        panel.Children.Add(status);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        var copy = new Button { Content = L.T("Скопировать черновик") };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(text.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(this, L.T("Буфер обмена занят. Попробуйте ещё раз."), "PaperDesk");
            }
        };
        buttons.Children.Add(copy);
        var tryAgain = new Button { Content = L.T("Повторить сохранение") };
        tryAgain.Click += (_, _) => retry();
        buttons.Children.Add(tryAgain);
        panel.Children.Add(text);
        UpdateDraft(draft);
        Closing += (_, args) =>
        {
            if (!saved)
                args.Cancel = MessageBox.Show(this, L.T("Черновик ещё не сохранён. Закрыть его?"), "PaperDesk",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
        };
    }

    internal void UpdateDraft(NoteDraft draft)
    {
        Draft = draft;
        var updated = (draft.Title ?? draft.ShownTitle) + "\n\n" + (draft.Body ?? draft.ShownBody);
        if (text.Text != updated) text.Text = updated;
    }

    internal void Complete()
    {
        saved = true;
        Close();
    }
}
