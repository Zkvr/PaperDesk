using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PaperDesk.Core;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Views;

static class UsabilityChecks
{
    public static int Run(string root)
    {
        var profile = Path.Combine(root, Guid.NewGuid().ToString("N"));
        using (var desk = new DeskController(profile))
        {
            var manager = new ManagerWindow(desk) { ShowActivated = false };
            manager.Show();
            try
            {
                var welcome = (FrameworkElement)manager.FindName("WelcomePanel");
                Require(welcome.IsVisible && desk.Store.All().Count == 0, "A fresh profile must show guidance before creating any notes.");
                Click((Button)manager.FindName("WelcomeExampleButton"));
                var example = desk.Store.All().Single().Note;
                Require(!welcome.IsVisible && !desk.Settings.WelcomePending && Checklist.Read(example.Body).Count == 3,
                    "The optional example must create one editable checklist and dismiss the welcome panel.");
                desk.ChangeState(example.Id, "trash", animateSticky: false);
                Require(desk.Store.Find(example.Id)?.Note.DeletedAt != null, "The sample must use the normal Trash flow.");
            }
            finally { manager.Close(); }
        }
        using (var reopened = new DeskController(profile))
            Require(!reopened.Settings.WelcomePending, "Reopening the profile must keep the welcome panel dismissed.");
        var legacy = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(legacy, "profiles"));
        using (var desk = new DeskController(legacy))
            Require(!desk.Settings.WelcomePending, "Existing profiles must skip first-run guidance.");
        Console.WriteLine("PASS first-run sample is opt-in, removable, and never reappears on the same profile");

        using (var desk = new DeskController(Path.Combine(root, Guid.NewGuid().ToString("N"))))
        {
            var ordinary = new Note { Title = "Alpha", Body = "first result text" };
            var favorite = new Note { Title = "Zulu", Body = "second result text" };
            desk.Store.Save(ordinary);
            desk.Store.Save(favorite);
            desk.Store.SavePlacement(new Placement { NoteId = favorite.Id, Favorite = true, StackId = null, FontSize = 24 });
            var manager = new ManagerWindow(desk) { ShowActivated = false };
            manager.Show();
            try
            {
                var cards = (ListBox)manager.FindName("Cards");
                var sort = (ComboBox)manager.FindName("SortBox");
                sort.SelectedIndex = 1;
                Require(((NoteCard)cards.Items[0]).Id == favorite.Id, "Favorites must stay on top when sorting by title.");
                var search = (TextBox)manager.FindName("SearchBox");
                search.Text = "second result text";
                search.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), 0, Key.Enter)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Require(cards.SelectedItem is NoteCard result && result.Id == favorite.Id,
                    "Enter in search must open its first matching note.");
                var body = (NoteBodyEditor)manager.FindName("EditBody");
                Require(body.FontSize == 24 && body.Text == favorite.Body, "The manager must use the note's saved typography.");

                var group = desk.CreateStack("Synthetic stack");
                desk.AssignStack([favorite.Id], group);
                desk.ExtractFromStack([ordinary.Id, favorite.Id]);
                Require(desk.Store.GetPlacement(favorite.Id) is { Visible: true, StackId: null } &&
                    !desk.Store.GetPlacement(ordinary.Id).Visible,
                    "Bulk extraction must remove stacked notes and leave already separate notes in place.");
            }
            finally { manager.Close(); }
        }
        Console.WriteLine("PASS keyboard search, pinned favorites, saved typography and bulk stack extraction");
        return 0;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
