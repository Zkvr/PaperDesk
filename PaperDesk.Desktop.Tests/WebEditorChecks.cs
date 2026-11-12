using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    public static int Run(string folder, bool withoutRuntime = false)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        if (withoutRuntime)
            Environment.SetEnvironmentVariable("WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", Path.GetFullPath(Path.Combine(folder, "missing-runtime")));
        return RunOnDispatcher(() => Check(folder, withoutRuntime));
    }

    public static int RunLifetime(string folder)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());
        return RunOnDispatcher(() => CheckLifetime(folder));
    }

    private static int RunOnDispatcher(Func<Task> test)
    {
        var frame = new DispatcherFrame();
        var check = Dispatcher.CurrentDispatcher.InvokeAsync(test).Task.Unwrap();
        _ = check.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        check.GetAwaiter().GetResult();
        return 0;
    }

    private static async Task CheckLifetime(string folder)
    {
        using var desk = new DeskController(Path.Combine(folder, Guid.NewGuid().ToString("N")));
        var note = new Note { Title = "Synthetic lifetime", Body = "Open and close WebView2" };
        desk.Store.Save(note);
        using var testProcess = Process.GetCurrentProcess();
        var closed = new System.Collections.Generic.List<WeakReference>();
        double warmPrivate = 0;
        for (var i = 0; i < 12; i++)
        {
            try { closed.Add(await OpenAndClose(i % 2 == 0)); }
            catch (Exception error) { throw new InvalidOperationException($"WebView2 cycle {i + 1} failed.", error); }
            if (i == 3) warmPrivate = PrivateMiB();
        }
        var finalPrivate = PrivateMiB();
        Require(closed.Count(reference => reference.IsAlive) == 0, "Closed WebView2 windows must be collectible.");
        Console.WriteLine(FormattableString.Invariant(
            $"PASS 12 manager and sticky open/close cycles release every browser process and window (host private MiB after 4/12: {warmPrivate:F1}/{finalPrivate:F1})"));

        async Task<WeakReference> OpenAndClose(bool managerMode)
        {
            Window window = managerMode ? new ManagerWindow(desk) : new StickyWindow(desk, note.Id, false);
            window.ShowActivated = false;
            var body = (NoteBodyEditor)window.FindName(managerMode ? "EditBody" : "BodyBox");
            body.EnableWebEditor(desk, () => { });
            window.Show();
            Process browser;
            try
            {
                if (window is ManagerWindow)
                {
                    var cards = (ListBox)window.FindName("Cards");
                    cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
                }
                var host = await Browser(body);
                browser = Process.GetProcessById(checked((int)host.CoreWebView2.BrowserProcessId));
            }
            finally { window.Close(); }
            using (browser)
            {
                await Until(() => Task.FromResult(!body.IsWebActive && browser.HasExited),
                    "The last closed editor must release its browser process.");
            }
            return new WeakReference(window);
        }

        double PrivateMiB()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            testProcess.Refresh();
            return testProcess.PrivateMemorySize64 / 1048576d;
        }
    }

    private static async Task Check(string folder, bool withoutRuntime)
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
        managerBody.EnableWebEditor(desk, () => { }, manager.FocusSearch);
        stickyBody.EnableWebEditor(desk, () => { });
        desk.Changed += RefreshSticky;
        manager.Show();
        sticky.Show();
        try
        {
            var cards = (ListBox)manager.FindName("Cards");
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single();
            if (withoutRuntime)
            {
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                await Until(() => Task.FromResult(new[] { managerBody, stickyBody }.All(body =>
                    !body.IsWebActive && ((Grid)body.Content).Children.OfType<WebView2CompositionControl>().Count() == 0)),
                    "both editors must recover from missing WebView2");
                managerBody.BeginEdit();
                Require(managerBody.IsEditing && managerBody.Text.Contains("Task") && stickyBody.Text.Contains("Secret"),
                    "the native editor must load the existing note and accept editing");
                var line = UiChecks.Descendants(managerBody).OfType<PaperLineInput>().Single();
                var before = desk.Store.Find(note.Id)!.Note.Body;
                manager.Activate();
                line.Focus();
                line.Select(line.Text.Length, 0);
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, line, "!"));
                await Until(() => Task.FromResult(desk.Store.Find(note.Id)!.Note.Body != before),
                    "typing in the native editor must persist immediately");
                var after = desk.Store.Find(note.Id)!.Note.Body;
                Require(after.Contains('!'), "typing in the native editor must persist immediately");
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Require(line.CanUndo, $"Native input must add an undo unit (enabled: {line.IsUndoEnabled}, limit: {line.UndoLimit}).");
                line.Undo();
                Require(desk.Store.Find(note.Id)!.Note.Body == before,
                    "native undo must restore the text and preserve surrounding Markdown");
                line.Redo();
                Require(desk.Store.Find(note.Id)!.Note.Body == after,
                    "native redo must restore the typed edit");
                Console.WriteLine("PASS manager and sticky fall back to editable WPF when WebView2 is unavailable");
                return;
            }
            var wide = await Browser(managerBody);
            var narrow = await Browser(stickyBody);
            const string painted = "document.querySelector('img[data-picture]')?.naturalWidth === 64";
            await Until(async () => await Eval(wide, painted) == "true" && await Eval(narrow, painted) == "true", "both views must load the local image");
            Require(await Eval(wide, "innerHeight") != await Eval(narrow, "innerHeight"), "viewport heights must differ");
            const string anchored = "(() => { const pic = document.querySelector('img[data-picture]'); const before = document.querySelector('.tiptap > p'); const delta = pic.getBoundingClientRect().top - before.getBoundingClientRect().bottom; return pic.dataset.mode === 'overlay' && Math.abs(delta) <= 1 })()";
            Require(await Eval(wide, anchored) == "true" && await Eval(narrow, anchored) == "true", "same logical overlay position on both papers");
            Require(await Eval(wide, "!!document.querySelector('table th') && !!document.querySelector('input[type=checkbox]') && !!document.querySelector('summary')") == "true",
                "existing table, checklist and spoiler are rendered");
            const string checkboxBesideLabel = """
                (() => {
                    const box = document.querySelector('input[type=checkbox]');
                    const check = box.getBoundingClientRect();
                    const label = box.parentElement.nextElementSibling.getBoundingClientRect();
                    return check.right <= label.left && check.bottom > label.top && check.top < label.bottom;
                })()
                """;
            Require(await Eval(wide, checkboxBesideLabel) == "true" && await Eval(narrow, checkboxBesideLabel) == "true",
                "A WebView2 checklist must put its checkbox beside its label");
            const string checkboxWidth = "document.querySelector('input[type=checkbox]').getBoundingClientRect().width";
            var originalPlacement = desk.Store.GetPlacement(note.Id);
            var originalWidth = JsonSerializer.Deserialize<double>(await Eval(narrow, checkboxWidth));
            desk.Store.SavePlacement(originalPlacement with { FontSize = 29 });
            await Until(async () => JsonSerializer.Deserialize<double>(await Eval(narrow, checkboxWidth)) > originalWidth * 1.4,
                "WebView2 checkboxes must grow with the text size");
            desk.Store.SavePlacement(originalPlacement);
            await Until(async () => JsonSerializer.Deserialize<double>(await Eval(narrow, checkboxWidth)) == originalWidth,
                "restoring the text size must restore the checkbox size");
            Console.WriteLine("PASS WebView2 checklist boxes stay beside their labels and scale with text");

            await Eval(wide, "document.querySelector('img').dispatchEvent(new MouseEvent('contextmenu', {bubbles:true})); [...document.querySelectorAll('#menu button')].find(b => /В тексте|In text/.test(b.textContent)).click()");
            await Until(() => Task.FromResult(desk.Store.ImageLayouts(note.Id).Single().Layout?.Mode == ImageTextMode.Inline), "image mode must save");
            await Until(async () => await Eval(narrow, "document.querySelector('img')?.dataset.mode") == "\"inline\"", "other window must follow the new mode");

            manager.Activate();
            wide.Focus();
            var imageLayout = desk.Store.ImageLayouts(note.Id).Single().Layout;
            var viewport = JsonSerializer.Deserialize<int>(await Eval(narrow, "innerHeight"));
            sticky.Height = sticky.MinHeight;
            await Until(async () => JsonSerializer.Deserialize<int>(await Eval(narrow, "innerHeight")) < viewport,
                "the compact sticky must resize the WebView2 viewport");
            Require(await Eval(narrow, """
                (() => {
                    const bounds = document.querySelector('img[data-picture]').getBoundingClientRect();
                    return bounds.width <= document.querySelector('.tiptap').clientWidth &&
                        bounds.height <= innerHeight * .8 + 1;
                })()
                """) == "true" && desk.Store.ImageLayouts(note.Id).Single().Layout == imageLayout,
                "The compact sticky must fit the picture without changing its saved layout.");
            sticky.Height = 275;
            Console.WriteLine("PASS compact WebView2 paper constrains the image without changing its saved layout");
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

            await Eval(wide, """
                (() => {
                    const root = document.querySelector('.tiptap');
                    const text = root.querySelector('p').firstChild;
                    root.focus();
                    getSelection().setBaseAndExtent(text, 0, text, 6);
                })()
                """);
            await FormatCommand(manager, wide, "Жирный");
            await Until(() => Task.FromResult(desk.Store.Find(note.Id)!.Note.Body.Contains("**Before**")), "bold must format the selected text");
            await Key(wide, "z", 90, modifiers: 2, code: "KeyZ");
            await Until(() => Task.FromResult(!desk.Store.Find(note.Id)!.Note.Body.Contains("**Before**")), "undo must remove the last format edit");
            await Key(wide, "y", 89, modifiers: 2, code: "KeyY");
            await Until(() => Task.FromResult(desk.Store.Find(note.Id)!.Note.Body.Contains("**Before**")), "redo must restore the format edit");
            var search = (TextBox)manager.FindName("SearchBox");
            await Eval(wide, "document.dispatchEvent(new KeyboardEvent('keydown', {ctrlKey:true, code:'KeyF'}))");
            await Until(() => Task.FromResult(search.IsKeyboardFocusWithin), "Ctrl+F in WebView2 must focus manager search");
            Console.WriteLine("PASS WebView2 undo, redo and search shortcut persist and route correctly");
            wide.Focus();
            await Eval(wide, """
                (() => {
                    const cell = document.querySelector('tbody td p');
                    cell.scrollIntoView();
                    getSelection().collapse(cell, 0);
                })()
                """);
            await FormatCommand(manager, wide, "Добавить строку ниже", shortcut: true);
            await Until(async () => await Eval(wide, "document.querySelectorAll('table tr').length") == "3",
                "adding a row must keep the table and add an editable row");
            Require(Regex.IsMatch(desk.Store.Find(note.Id)!.Note.Body, @"\| One +\| Two +\|"), "table editing must preserve the old values");
            Console.WriteLine("PASS WebView2 preserves selection when applying bold and adds a table row without losing cells");

            sticky.Activate();
            narrow.Focus();
            await Eval(narrow, "document.querySelector('.tiptap').focus()");
            await Until(() => Task.FromResult(((Button)sticky.FindName("FormatButton")).IsVisible),
                "Editing a sticky note must show its format icon");
            await FormatCommand(sticky, narrow, caption: null);
            manager.Activate();
            wide.Focus();
            Console.WriteLine("PASS the sticky format menu opens beside the paper and shows command icons");

            var imageData = Convert.ToBase64String(buffer.ToArray());
            await Eval(wide, $$"""
                (() => {
                    const root = document.querySelector('.tiptap');
                    const dataTransfer = new DataTransfer();
                    dataTransfer.items.add(new File([Uint8Array.from(atob('{{imageData}}'), c => c.charCodeAt(0))],
                        'qa-drop.png', { type: 'image/png' }));
                    const box = root.getBoundingClientRect();
                    root.dispatchEvent(new DragEvent('drop', {
                        bubbles: true, cancelable: true, dataTransfer,
                        clientX: box.left + 8, clientY: Math.max(5, box.top + 12)
                    }));
                })()
                """);
            await Until(() => Task.FromResult(desk.Store.ImageIds(note.Id).Count == 2), "dropping a file into WebView2 must attach it");
            var added = desk.Store.ImageIds(note.Id).Last();
            await Until(async () => await Eval(wide, $"document.querySelector('img[data-picture=\"{added}\"]')?.naturalWidth === 64") == "true",
                "the dropped attachment must render");
            await Eval(wide, $"document.querySelector('img[data-picture=\"{added}\"]').dispatchEvent(new MouseEvent('contextmenu', {{bubbles:true}})); " +
                "[...document.querySelectorAll('#menu button')].find(button => button.textContent === 'Удалить изображение').click()");
            await Until(() => Task.FromResult(desk.Store.ImageIds(note.Id).Count == 1), "removing the added image must keep the original image");
            Console.WriteLine("PASS WebView2 file drop renders a saved image and can remove only that attachment");

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

            var sourceBody = desk.Store.Find(note.Id)!.Note.Body;
            manager.Activate();
            wide.Focus();
            await Eval(wide, """
                (() => {
                    const root = document.querySelector('.tiptap');
                    const text = root.querySelector('strong').firstChild;
                    root.focus();
                    getSelection().setBaseAndExtent(text, 0, text, text.length);
                })()
                """);
            string copied = "[]";
            await Until(async () =>
            {
                copied = await Eval(wide, """
                    (() => {
                        const clipboard = new DataTransfer();
                        document.querySelector('.tiptap').dispatchEvent(new ClipboardEvent('copy',
                            { clipboardData: clipboard, bubbles: true, cancelable: true }));
                        return [clipboard.getData('text/plain'), clipboard.getData('text/html')];
                    })()
                    """);
                return JsonSerializer.Deserialize<string[]>(copied) is [var plain, var html] &&
                    plain.Trim() == "Before" && html.Contains("<strong");
            }, "Copy must keep the selected text and formatting.");
            var destination = new Note { Title = "Copy target", Body = "Target" };
            desk.Store.Save(destination);
            await Until(() => Task.FromResult(cards.Items.OfType<NoteCard>().Any(card => card.Id == destination.Id)),
                "the destination note must appear in the manager");
            cards.SelectedItem = cards.Items.OfType<NoteCard>().Single(card => card.Id == destination.Id);
            await Until(async () => await Eval(wide, "document.querySelector('.tiptap').textContent") == "\"Target\"",
                "the destination must have a separate editor document");
            await Eval(wide, $$"""
                (() => {
                    const [text, html] = {{copied}};
                    const root = document.querySelector('.tiptap');
                    root.focus();
                    getSelection().collapse(root.querySelector('p').firstChild, 6);
                    const clipboard = new DataTransfer();
                    clipboard.setData('text/plain', text);
                    clipboard.setData('text/html', html);
                    root.dispatchEvent(new ClipboardEvent('paste', { clipboardData: clipboard, bubbles: true, cancelable: true }));
                })()
                """);
            await Until(() => Task.FromResult(desk.Store.Find(destination.Id)!.Note.Body.Contains("**Before**")),
                "pasting into another note must keep the selected formatting");
            Require(desk.Store.Find(note.Id)!.Note.Body == sourceBody, "Copying must leave the source unchanged.");
            Console.WriteLine("PASS copying a formatted selection between WebView2 notes leaves the source unchanged");

            async Task FormatCommand(Window window, WebView2CompositionControl browser, string? caption, bool shortcut = false)
            {
                var button = (Button)window.FindName("FormatButton");
                Require(button.Content is System.Windows.Shapes.Path, "The toolbar format control must be an icon");
                if (shortcut)
                    await Eval(browser, "document.dispatchEvent(new KeyboardEvent('keydown', {ctrlKey:true, shiftKey:true, code:'KeyF'}))");
                else
                {
                    if (button.Focusable) button.Focus();
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                ContextMenu? menu = null;
                await Until(() =>
                {
                    menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Select(source => source.RootVisual).OfType<Visual>().SelectMany(UiChecks.Descendants)
                        .OfType<ContextMenu>().SingleOrDefault(candidate => candidate.IsOpen && candidate.PlacementTarget == button);
                    return Task.FromResult(menu is { ActualWidth: > 0, ActualHeight: > 0 });
                }, "Format must open a native menu");
                var paper = (FrameworkElement)window.FindName(window is StickyWindow ? "Paper" : "EditorPaper");
                var overlap = Rect.Intersect(Bounds(menu!), Bounds(paper));
                Require(overlap.IsEmpty || overlap.Width <= 1 || overlap.Height <= 1, "The native menu must keep the paper visible");
                Require(await Eval(browser, "document.querySelector('#menu').hidden") == "true", "The browser must not draw format commands over the paper");
                var items = menu!.Items.OfType<MenuItem>().ToList();
                Require(items.All(item => item.Template.FindName("ItemIcon", item) is ContentPresenter { Content: TextBlock, IsVisible: true }),
                    "Every format command must display its icon");
                if (caption != null)
                    items.Single(item => Equals(item.Header, caption)).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;

                static Rect Bounds(FrameworkElement element) => new(element.PointToScreen(new Point()),
                    element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
            }
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

    private static async Task Key(WebView2CompositionControl host, string key, int virtualKey, int modifiers = 0, string? code = null)
    {
        foreach (var type in new[] { "keyDown", "keyUp" })
            await host.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new { type, key, code = code ?? key, windowsVirtualKeyCode = virtualKey, modifiers }));
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
