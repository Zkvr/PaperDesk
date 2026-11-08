using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;
using PaperDesk.Core;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Views;

static class WebEditorChecks
{
    public static int Run(string folder)
    {
        System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var frame = new DispatcherFrame();
        var check = Dispatcher.CurrentDispatcher.InvokeAsync(() => Check(folder)).Task.Unwrap();
        _ = check.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        check.GetAwaiter().GetResult();
        return 0;
    }

    private static async Task Check(string folder)
    {
        var profile = Path.Combine(folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), Json.Write(new Settings { Animations = true }));
        using var desk = new DeskController(profile);
        var note = new Note { Title = "WebView smoke", Body = "Before\n\nAfter\n\n- [ ] Task\n\n:::spoiler Details\nSecret\n:::\n\n| Head | Name |\n| --- | --- |\n| One | Two |" };
        desk.Store.Save(note);
        var bitmap = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgr32, null, new byte[64 * 64 * 4], 64 * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        Require(desk.Store.AddImages(note.Id, [buffer.ToArray()], new NoteImageLayout(.18, 3, .36, ImageTextMode.Overlay, 2)), "seed image");
        var manager = new ManagerWindow(desk) { Width = 1180, Height = 740, ShowActivated = false };
        var sticky = new StickyWindow(desk, note.Id, false) { Width = 260, Height = 275, ShowActivated = false };
        var managerBody = (NoteBodyEditor)manager.FindName("EditBody");
        var stickyBody = (NoteBodyEditor)sticky.FindName("BodyBox");
        managerBody.EnableWebEditor(desk, () => { });
        stickyBody.EnableWebEditor(desk, () => { });
        desk.Changed += RefreshSticky;
        manager.Show();
        sticky.Show();
        try
        {
            var cards = (ListBox)manager.FindName("Cards");
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
            var wide = await Browser(managerBody);
            var narrow = await Browser(stickyBody);
            const string painted = "document.querySelector('img[data-picture]')?.naturalWidth === 64";
            await Until(async () => await Eval(wide, painted) == "true" && await Eval(narrow, painted) == "true", "both views must load the local image");
            Require(await Eval(wide, "innerHeight") != await Eval(narrow, "innerHeight"), "viewport heights must differ");
            const string anchored = "(() => { const pic = document.querySelector('img[data-picture]'); const before = document.querySelector('.tiptap > p'); const delta = pic.getBoundingClientRect().top - before.getBoundingClientRect().bottom; return pic.dataset.mode === 'overlay' && Math.abs(delta) <= 1 })()";
            Require(await Eval(wide, anchored) == "true" && await Eval(narrow, anchored) == "true", "same logical overlay position on both papers");
            Require(await Eval(wide, "!!document.querySelector('table th') && !!document.querySelector('input[type=checkbox]') && !!document.querySelector('summary')") == "true",
                "existing table, checklist and spoiler are rendered");

            await Eval(wide, "document.querySelector('img').dispatchEvent(new MouseEvent('contextmenu', {bubbles:true})); [...document.querySelectorAll('#menu button')].find(b => /В тексте|In text/.test(b.textContent)).click()");
            await Until(() => Task.FromResult(desk.Store.ImageLayouts(note.Id).Single().Layout?.Mode == ImageTextMode.Inline), "image mode must save");
            await Until(async () => await Eval(narrow, "document.querySelector('img')?.dataset.mode") == "\"inline\"", "other window must follow the new mode");

            manager.Activate();
            wide.Focus();
            await Eval(wide, "(() => { const root = document.querySelector('.tiptap'); root.focus(); getSelection().collapse(root.firstElementChild.firstChild, 6); })()");
            await Key(wide, "Enter", 13);
            await Until(() => Task.FromResult(desk.Store.ImageLayouts(note.Id).Single().Layout!.Anchor > 2), "Enter must move image to the next paragraph");
            await Key(wide, "Backspace", 8);
            await Until(() => Task.FromResult(desk.Store.ImageLayouts(note.Id).Single().Layout!.Anchor == 2), "Backspace must remove the empty paragraph");
            await Key(wide, "Enter", 13);
            await wide.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.insertText", "{\"text\":\"A new paragraph\"}");
            await Until(() => Task.FromResult(desk.Store.Find(note.Id)!.Note.Body.Contains("Before\n\nA new paragraph\n\nAfter")), "text before the image must persist");
            await Until(async () => await Eval(narrow, "document.querySelector('img').parentElement.parentElement.previousElementSibling?.textContent") == "\"A new paragraph\"",
                "the second view must keep the image attached after the new paragraph");
            await Eval(narrow, "window.scrollTo(0, document.documentElement.scrollHeight)");
            Require(await Eval(narrow, "document.querySelector('img').getBoundingClientRect().top < 0") == "true", "the picture must scroll with the note");
            await Eval(wide, "document.querySelector('input[type=checkbox]').click()");
            await Until(() => Task.FromResult(desk.Store.Find(note.Id)!.Note.Body.Contains("- [x] Task")), "checkbox must persist");
            var saved = desk.Store.Find(note.Id)!.Note.Body;
            Require(saved.Contains(":::spoiler Details\nSecret\n:::") && Regex.IsMatch(saved, @"\| One +\| Two +\|"),
                "editing must preserve table and spoiler text");
            Console.WriteLine("PASS WebView2 manager and sticky share the image anchor; Enter, Backspace, typing, scrolling, mode, and checkbox persist as expected");

            typeof(ManagerWindow).GetMethod("TrashClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(manager, [manager, new RoutedEventArgs()]);
            var crumple = (PaperCrumple)manager.FindName("EditorCrumple");
            await Until(() => Task.FromResult(!crumple.IsPlaying && !managerBody.IsEnabled), "trash must finish and close the manager editor");
            Require(desk.Store.Find(note.Id)?.Note.DeletedAt != null, "trash must retain the note in SQLite");
            desk.Undo();
            await Until(() => Task.FromResult(cards.Items.OfType<NoteCard>().Any()), "undo must put the card back");
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
            await Until(async () => await Eval(await Browser(managerBody), painted) == "true", "reopening the manager editor must show the restored note");
            Console.WriteLine("PASS the manager can trash a note with WebView2 and reopen it after undo");

            for (var attempt = 0; attempt < 3; attempt++)
            {
                cards.UnselectAll();
                manager.UpdateLayout();
                await Until(() => Task.FromResult(wide.Visibility == Visibility.Hidden), "deselecting must hide the WebView2 preview");
                cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
                await Until(() => Task.FromResult(wide.Visibility == Visibility.Visible), "reselecting must show the same preview");
            }
            Console.WriteLine("PASS manager preview survives repeated closing and reopening");

            var topNote = new Note { Title = "Stack top", Body = "Discard this sheet" };
            desk.Store.Save(topNote);
            var group = desk.CreateStack("Trash regression");
            desk.Store.SavePlacement(desk.Store.GetPlacement(note.Id) with { StackId = group });
            desk.Store.SavePlacement(desk.Store.GetPlacement(topNote.Id) with { StackId = group });
            desk.ShowSticky(note.Id);
            desk.ShowSticky(topNote.Id);
            var stack = Application.Current.Windows.OfType<StickyWindow>().Single(window => window.NoteId == topNote.Id);
            var stackBody = (NoteBodyEditor)stack.FindName("BodyBox");
            stackBody.EnableWebEditor(desk, () => { });
            var stackBrowser = await Browser(stackBody);
            await Until(async () => await Eval(stackBrowser, "document.querySelector('.tiptap').textContent") == "\"Discard this sheet\"", "stack top must load into WebView2");
            desk.ChangeState(topNote.Id, "trash");
            await Until(() => Task.FromResult(!stack.IsCrumpling && stack.NoteId == note.Id), "trashing the top sheet must reveal the next note");
            await Until(async () => await Eval(stackBrowser, painted) == "true", "the next note must retain its image");
            await desk.PurgeTrash([topNote.Id]);
            Require(desk.Store.Find(topNote.Id) == null && desk.Store.Find(note.Id) != null, "purge must remove only the trashed note");
            Console.WriteLine("PASS trashing the desktop stack top reveals the next WebView2 note and permanent deletion removes only the trashed note");
        }
        finally
        {
            desk.Changed -= RefreshSticky;
            sticky.Close();
            manager.Close();
        }

        void RefreshSticky() => sticky.RefreshContent();
    }

    private static async Task<WebView2CompositionControl> Browser(NoteBodyEditor body)
    {
        await Until(() => Task.FromResult(body.IsWebActive), "the real WebView2 editor must become ready");
        return ((Grid)body.Content).Children.OfType<WebView2CompositionControl>().Single();
    }

    private static Task<string> Eval(WebView2CompositionControl host, string script) => host.CoreWebView2.ExecuteScriptAsync(script);

    private static async Task Key(WebView2CompositionControl host, string key, int code)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await host.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = code }));
    }

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(18);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(60);
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
