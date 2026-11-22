using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using PaperDesk.Core;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Views;
using PaperDesk.Storage;

static partial class UiChecks
{
    private static void CheckProjects(string folder)
    {
        var profile = Path.Combine(Path.GetFullPath(folder), "projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), Json.Write(new Settings { Animations = false }));
        var file = Path.Combine(profile, "Client report.txt");
        File.WriteAllText(file, "Test project document");
        const string url = "https://example.org/landing?revision=2";
        foreach (var invalid in new[] { "file:///C:/page.txt", "javascript:alert(1)", "my-app:launch",
                     "https://user:secret@example.org/", @"relative\page.txt", @"\\server\share\page.txt",
                     @"C:\page.txt.lnk", @"C:\page.pdf:payload", @"C:\launch.exe", "https://example.org/\nother" })
            Require(ProjectSource.Normalize(invalid) == null, "Unsupported links and runnable files must not open");
        Require(ProjectSource.Normalize(file) == file && ProjectSource.Normalize(url) == url,
            "Project sources must accept a local document and an HTTP(S) link");
        Require(ProjectSource.Open(Path.Combine(profile, "missing.txt")) is { Length: > 0 },
            "A missing document must be reported before starting the shell");

        Guid landing, report;
        var first = new Note { Title = "Лендинг", Body = "Черновик" };
        var next = new Note { Title = "Следующая правка", Body = "- [ ] Обновить цену" };
        var loose = new Note { Title = "Личная заметка" };
        using (var desk = new DeskController(profile))
        {
            foreach (var note in new[] { first, next, loose })
                desk.Store.Save(note);
            desk.Store.SavePlacement(new Placement { NoteId = loose.Id, StackId = null, Visible = true });
            desk.Start();
            landing = desk.CreateStack("Лендинг");
            desk.AssignStack([first.Id, next.Id], landing);
            desk.SelectStackNote(next.Id);
            desk.SetProjectSource(landing, url);
            report = desk.CreateProject("Отчёт", file) ?? throw new Exception("New project was not created");

            Require(desk.ResumeProject(report) == file && !desk.Store.GetPlacement(next.Id).Visible &&
                desk.Store.GetPlacement(loose.Id).Visible && VisibleSheet(report) is { } reportWindow &&
                desk.Store.GetPlacement(reportWindow.NoteId).Topmost,
                "Resuming a project must open its stack and hide only other project stacks");
            Require(desk.ResumeProject(landing) == url && VisibleSheet(landing)?.NoteId == next.Id &&
                VisibleSheet(report) == null && VisibleSheet(null)?.NoteId == loose.Id,
                "Returning to a project must restore its previously selected sheet and leave loose notes visible");
            Require(desk.Store.Find(next.Id)?.Note.Body == next.Body, "Switching must preserve the note text");

            var manager = new ManagerWindow(desk);
            manager.Show();
            try
            {
                var button = (Button)manager.FindName("ProjectsButton");
                Require(button.IsVisible, "The manager must expose its Projects menu");
                var menu = OpenMenu(button);
                var items = menu.Items.OfType<MenuItem>().ToArray();
                Require(items.Any(item => Label(item) == "Продолжить «Лендинг»") &&
                    items.Any(item => Label(item) == "Продолжить «Отчёт»"),
                    "Both saved projects must appear as direct resume commands");
                Require(items.All(item => Label(item)?.Contains("примера", StringComparison.Ordinal) != true),
                    "Example creation belongs in the guide rather than the Projects menu");
                menu.IsOpen = false;
                var board = (ListBox)manager.FindName("Cards");
                desk.SetProjectSource(landing, null);
                board.SelectedItem = board.Items.OfType<PaperDesk.Desktop.ViewModels.NoteCard>().Single(card => card.Id == next.Id);
                var attach = (Button)manager.FindName("ProjectSourceButton");
                Require(attach.IsVisible && Equals(attach.Content, "Прикрепить файл или ссылку…"),
                    "A selected sheet from a named stack must expose an explicit attach action");
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "Источник проекта");
                    dialog.UpdateLayout();
                    SaveSized(dialog, Path.Combine(folder, "project-source.png"));
                    Require(Descendants(dialog).OfType<Button>().Any(item => Equals(item.Content, "Прикрепить файл…")),
                        "Attaching a file needs an explicit picker button");
                    var input = Descendants(dialog).OfType<TextBox>().Single();
                    input.Text = url;
                    Descendants(dialog).OfType<Button>().Single(item => Equals(item.Content, "Сохранить"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }, DispatcherPriority.ContextIdle);
                attach.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Require(desk.Store.GetStack(landing)?.ProjectSource == url, "Attach must save the source on the selected note's stack");

                var guide = new HelpWindow(manager, desk);
                guide.Show();
                try
                {
                    guide.UpdateLayout();
                    SaveSized(guide, Path.Combine(folder, "help-projects.png"));
                    Descendants(guide).OfType<Button>().Single(item => Equals(item.Content, "Попробовать на примерах"))
                        .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    guide.UpdateLayout();
                    Require(Descendants(guide).OfType<Button>().Count(item => item.Content is string label &&
                        label.StartsWith("Пример", StringComparison.Ordinal)) == 2,
                        "Created examples must have their own resume actions in the guide");
                }
                finally { guide.Close(); }
                var added = desk.Store.AllStacks().Where(stack => stack.Name.StartsWith("Пример", StringComparison.Ordinal)).ToArray();
                var placements = desk.Store.AllPlacements().Values.ToArray();
                Require(added.Length == 2 && added.All(stack => File.Exists(stack.ProjectSource) &&
                    placements.Count(placement => placement.StackId == stack.Id) == 2) && desk.AddProjectExamples() == null,
                    "Adding the examples must create two local documents and stacks once");

                menu = OpenMenu(button);
                Require(menu.Items.OfType<MenuItem>().All(item => Label(item)?.Contains("Пример", StringComparison.Ordinal) != true),
                    "Existing example stacks must stay out of the Projects menu");
                var create = menu.Items.OfType<MenuItem>().Single(item => Label(item) == "Новый проект…");
                Dispatcher.CurrentDispatcher.BeginInvoke(() =>
                {
                    var dialog = Application.Current.Windows.OfType<Window>().Single(window => window.Title == "Новый проект");
                    dialog.UpdateLayout();
                    var inputs = Descendants(dialog).OfType<TextBox>().ToArray();
                    inputs.Single(input => AutomationProperties.GetName(input) == "Название проекта").Text = "Ревью";
                    inputs.Single(input => AutomationProperties.GetName(input) == "Файл или ссылка проекта").Text = url;
                    var save = Descendants(dialog).OfType<Button>().Single(item => Equals(item.Content, "Создать"));
                    Require(save.IsEnabled, "The dialog must allow a valid source to be saved");
                    save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }, DispatcherPriority.ContextIdle);
                menu.IsOpen = false;
                create.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(desk.Store.AllStacks().Single(stack => stack.Name == "Ревью").ProjectSource == url,
                    "Creating a project from the manager must save its source");
                desk.ResumeProject(landing);
            }
            finally { manager.Close(); }

            using var backup = new NoteStore(Path.Combine(profile, "restored.db"));
            backup.Import(desk.Store.Export());
            Require(backup.GetStack(report)?.ProjectSource == file && backup.GetStack(landing)?.ProjectSource == url,
                "A Windows backup must preserve both project sources");
        }

        using (var reopened = new DeskController(profile))
        {
            reopened.Start();
            Require(VisibleSheet(landing)?.NoteId == next.Id && VisibleSheet(report) == null &&
                reopened.ResumeProject(report) == file && VisibleSheet(report) != null,
                "The active stack and both resume commands must survive restart");
            reopened.SetProjectSource(report, null);
            Require(reopened.ResumeProject(report) == null && VisibleSheet(report) != null,
                "Removing a source must leave the stack and its notes usable");
        }
        Console.WriteLine("PASS projects resume the selected note, switch stacks, persist sources and add examples");

        static string? Label(MenuItem item) => (item.Header as TextBlock)?.Text ?? item.Header as string;
        static StickyWindow? VisibleSheet(Guid? stack) => Application.Current.Windows.OfType<StickyWindow>()
            .SingleOrDefault(window => window.IsVisible && window.StackId == stack);
        static ContextMenu OpenMenu(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            return PresentationSource.CurrentSources.Cast<PresentationSource>()
                .Select(source => source.RootVisual).OfType<Visual>().SelectMany(Descendants)
                .OfType<ContextMenu>().Single(menu => menu.IsOpen);
        }
    }
}
