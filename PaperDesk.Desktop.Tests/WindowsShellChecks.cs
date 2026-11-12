using System.IO;
using System.Windows;
using System.Windows.Interop;
using PaperDesk.Core;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Views;
using PaperDesk.Desktop.Win32Interop;

static class WindowsShellChecks
{
    public static int CheckGeometry()
    {
        var primary = new Rect(0, 0, 1920, 1040);
        var secondary = new Rect(-2560, -120, 2560, 1400);
        var titleAt200Percent = new Rect(-200, -100, 240, 80);
        var titleOnSecondary = new Rect(-400, -100, 240, 80);
        Require(StickyWindow.RecoveryPosition(titleOnSecondary, [primary, secondary], primary) == null,
            "A fully visible title on a negative-origin high-DPI monitor must be kept.");
        Require(StickyWindow.RecoveryPosition(titleAt200Percent, [primary, secondary], primary) == new Point(40, 40),
            "A title in the gap between monitor working areas must be moved to the primary monitor.");
        Require(StickyWindow.RecoveryPosition(titleOnSecondary, [primary], primary) == new Point(40, 40),
            "A note from a disconnected monitor must return to the primary monitor.");
        Require(StickyWindow.RecoveryPosition(new Rect(400, 30, 120, 40), [primary], primary) == null,
            "A visible note on a 100% monitor must stay in place.");
        var sheet = new Rect(-650, 40, 180, 150);
        Require(PaperDrag.SnapOffset(sheet, [new Rect(-458, 55, 180, 150), new Rect(-457, 55, 180, 150)], 8, 12) == new Vector(4, 0),
            "Snap must use the nearest eligible edge at a negative screen origin.");
        Require(PaperDrag.SnapOffset(sheet, [new Rect(-458, 195, 180, 150), new Rect(-400, 55, 180, 150)], 8, 12) == null,
            "Diagonal or distant sheets must not pull on the cursor.");
        Require(PaperDrag.SnapOffset(sheet, [new Rect(-643, 211, 180, 150)], 16, 24) == new Vector(0, 5),
            "Snap must use a gap scaled to 200% DPI on a neighbouring vertical edge.");
        Console.WriteLine("PASS physical monitor geometry at 100% and 200%, negative origins and a disconnected display");
        CheckWorkerCache();
        return 0;
    }

    private static void CheckWorkerCache()
    {
        var searches = 0;
        nint worker = 0;
        var cache = new DesktopLayer.WorkerCache(_ => { searches++; return worker; }, handle => handle == worker);
        Require(cache.Get(0, 0) == 0 && searches == 0, "A missing Explorer must not start worker discovery.");
        for (var now = 0; now < 30_000; now += 2_000)
            for (var sheet = 0; sheet < 4; sheet++)
                Require(cache.Get(1, now) == 0 && searches == 1, "Idle sheets must share a failed worker lookup.");
        worker = 10;
        Require(cache.Get(1, 30_000) == 10 && cache.Get(1, 32_000) == 10 && searches == 2,
            "A successful lookup must be reused across idle ticks.");
        worker = 11;
        Require(cache.Get(1, 34_000) == 11 && searches == 3, "A destroyed worker must be replaced promptly.");
        worker = 0;
        Require(cache.Get(1, 36_000) == 0 && searches == 4, "A worker can disappear while Explorer is restarting.");
        worker = 12;
        Require(cache.Get(2, 36_001) == 12 && searches == 5, "A new Explorer must bypass the previous shell's retry delay.");
        Console.WriteLine("PASS visible notes share a desktop worker and failed Explorer lookups; restarting Explorer clears the cache");
    }

    public static int CheckDesktop(string folder)
    {
        using var first = new HotkeyHost(() => { });
        using var second = new HotkeyHost(() => { });
        var occupied = RegisterFreeShortcut(first);
        var previous = RegisterFreeShortcut(second);
        try
        {
            second.Register(occupied);
            throw new Exception("Windows accepted an already registered shortcut.");
        }
        catch (InvalidOperationException)
        {
            Require(second.Shortcut == previous, "The old binding must remain active after a conflict.");
        }
        first.Clear();
        second.Register(occupied);
        Require(second.Shortcut == occupied, "The binding must be available after the other owner unregisters it.");
        Console.WriteLine("PASS real Windows hotkey conflict keeps the previous binding and can be retried");

        using var desk = new DeskController(Path.Combine(folder, Guid.NewGuid().ToString("N")));
        var note = new Note { Title = "Synthetic display recovery" };
        desk.Store.Save(note);
        var window = new StickyWindow(desk, note.Id, false) { ShowActivated = false };
        window.Show();
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            Native.MoveWithoutActivate(hwnd, -30000, -30000);
            window.EnsureOnScreen();
            Require(Native.GetWindowRect(hwnd, out var bounds) && bounds.Left > -30000 && bounds.Top > -30000,
                "The real off-screen sticky must move into the working area.");
            var recovered = window.PointToScreen(new Point());
            window.EnsureOnScreen();
            Require(window.PointToScreen(new Point()) == recovered, "Recovering an already visible sticky must not move it.");
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            window.Hide();
            Native.MoveWithoutActivate(hwnd, -30000, -30000);
            window.Show();
            Require(window.PointToScreen(new Point()) == recovered, "A previously hidden sticky must be recovered when it is shown.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS real WPF window returns to the screen and stays at the recovered position");
        return 0;
    }

    public static int CheckTrayExit(string folder)
    {
        var seed = !Directory.Exists(folder);
        using var desk = new DeskController(folder);
        if (seed)
        {
            var stack = new DesktopStack(Guid.NewGuid(), "Synthetic stack", 100, 120, 300, 310, Guid.Empty);
            desk.Store.SaveStack(stack);
            Save("Loose", null);
            Save("Top", stack.Id);
            Save("Underneath", stack.Id);
            Save("Hide explicitly", null);
        }

        desk.Start();
        var app = Application.Current;
        var windows = app.Windows.OfType<StickyWindow>().Where(window => window.IsVisible).ToArray();
        Require(windows.Length == (seed ? 3 : 2), "Start in the tray must restore the loose sheet and stack.");
        if (seed)
        {
            var hidden = desk.Store.All().Single(row => row.Note.Title == "Hide explicitly").Note.Id;
            windows.Single(window => window.NoteId == hidden).Close();
        }
        app.Dispatcher.BeginInvoke((Action)desk.Exit);
        app.Run();
        var placements = desk.Store.All().ToDictionary(row => row.Note.Title, row => desk.Store.GetPlacement(row.Note.Id).Visible);
        Require(placements.Count == 4 && placements["Loose"] && placements["Top"] && placements["Underneath"] &&
            !placements["Hide explicitly"], "Exiting from the tray must preserve desktop placement and respect explicitly hidden notes.");
        Console.WriteLine("PASS tray exit preserves the loose sheet, stack and explicitly hidden note");
        return 0;

        void Save(string title, Guid? stackId)
        {
            var note = new Note { Title = title, Body = "Synthetic note" };
            desk.Store.Save(note);
            desk.Store.SavePlacement(new Placement { NoteId = note.Id, StackId = stackId, Visible = true });
        }
    }

    private static string RegisterFreeShortcut(HotkeyHost host)
    {
        for (var key = 13; key <= 24; key++)
        {
            var shortcut = $"Ctrl+Alt+Shift+F{key}";
            try { host.Register(shortcut); return shortcut; }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("No synthetic test shortcut was available.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
