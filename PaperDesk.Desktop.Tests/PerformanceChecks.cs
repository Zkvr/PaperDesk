using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PaperDesk.Core;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Views;
using PaperDesk.Storage;

static class PerformanceChecks
{
    public static int Seed(string folder, int count)
    {
        if (Directory.Exists(folder)) throw new IOException("Use a new directory for synthetic data.");
        using var store = new NoteStore(Path.Combine(folder, "profiles", "local", "notes.db"));
        var body = string.Concat(Enumerable.Repeat("Synthetic note text with a checklist and a line break.\n", 10));
        for (var i = 0; i < count; i++)
            store.Save(new Note { Title = $"Synthetic note {i + 1}", Body = "- [ ] Test\n" + body });
        var settings = new Settings { Animations = false, ShowNotesAtStartup = false };
        File.WriteAllText(Path.Combine(folder, "settings.json"), Json.Write(settings));
        return 0;
    }

    public static int Run(string folder)
    {
        var started = Stopwatch.StartNew();
        using var desk = new DeskController(folder);
        desk.Start();
        var startupMs = started.Elapsed.TotalMilliseconds;

        started.Restart();
        var manager = new ManagerWindow(desk) { Width = 1180, Height = 740, ShowActivated = false };
        manager.Show();
        manager.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var managerMs = started.Elapsed.TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var managerPrivateMiB = process.PrivateMemorySize64 / 1048576d;
        var managerWorkingMiB = process.WorkingSet64 / 1048576d;
        var vm = (ManagerViewModel)manager.DataContext;
        var note = desk.Store.Find(vm.Notes[0].Id)!.Note;
        var saveMs = new double[100];
        var body = note.Body;
        for (var i = 0; i < saveMs.Length; i++)
        {
            var next = i.ToString() + note.Body;
            started.Restart();
            desk.Store.SaveText(note.Id, null, next, note.Title, body);
            saveMs[i] = started.Elapsed.TotalMilliseconds;
            body = next;
        }
        started.Restart();
        vm.Refresh();
        manager.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var refreshMs = started.Elapsed.TotalMilliseconds;
        manager.Close();
        Array.Sort(saveMs);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Notes = vm.Notes.Count,
            StartupMs = Math.Round(startupMs, 2),
            ManagerMs = Math.Round(managerMs, 2),
            ManagerPrivateMiB = Math.Round(managerPrivateMiB, 2),
            ManagerWorkingMiB = Math.Round(managerWorkingMiB, 2),
            SaveP50Ms = Math.Round(saveMs[49], 3),
            SaveP95Ms = Math.Round(saveMs[94], 3),
            SaveMaxMs = Math.Round(saveMs[^1], 3),
            RefreshMs = Math.Round(refreshMs, 2)
        }));
        return 0;
    }

    public static int CheckScrolling(string folder)
    {
        using var desk = new DeskController(Path.Combine(folder, Guid.NewGuid().ToString("N")));
        for (var i = 0; i < 200; i++)
        {
            var note = new Note { Title = $"Synthetic card {i + 1}" };
            desk.Store.Save(note);
            if (i < 10) desk.Store.SavePlacement(new Placement { NoteId = note.Id, Visible = true, StackId = null });
        }
        var manager = new ManagerWindow(desk) { Width = 1180, Height = 740, ShowActivated = false };
        manager.Show();
        try
        {
            var cards = (ListBox)manager.FindName("Cards");
            CheckLastCard();
            ((Button)manager.FindName("ListViewButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            CheckLastCard();
            cards.SelectAll();
            if (cards.SelectedItems.Count != 200) throw new InvalidOperationException("Bulk selection must include unrealized cards.");

            void CheckLastCard()
            {
                manager.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var last = cards.Items.OfType<NoteCard>().Last();
                cards.ScrollIntoView(last);
                manager.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                manager.UpdateLayout();
                var containers = UiChecks.Descendants(cards).OfType<ListBoxItem>().ToArray();
                if (cards.Items.Count != 200 || containers.Length >= 100 ||
                    !containers.Any(container => Equals(container.DataContext, last)))
                    throw new InvalidOperationException("The grouped view must realize only nearby cards and scroll to the last note.");
            }
        }
        finally { manager.Close(); }
        Console.WriteLine("PASS grouped grid and list virtualize, reach the last card and select unrealized cards");
        return 0;
    }
}
