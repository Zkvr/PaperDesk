using System.IO;
using System.Windows;
using System.Windows.Interop;
using PaperDesk.Core;
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
        Console.WriteLine("PASS physical monitor geometry at 100% and 200%, negative origins and a disconnected display");
        return 0;
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
