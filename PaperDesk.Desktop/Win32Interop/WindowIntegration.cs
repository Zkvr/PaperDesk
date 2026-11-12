using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Input;

namespace PaperDesk.Desktop.Win32Interop;

public sealed class HotkeyHost : IDisposable
{
    private readonly HwndSource source;
    private readonly Action action;
    private int registeredId;
    private (uint Modifiers, uint Key) registered;
    public string? Shortcut { get; private set; }
    public HotkeyHost(Action action)
    {
        this.action = action; source = new HwndSource(new HwndSourceParameters("PaperDesk messages") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) }); source.AddHook(Hook);
    }
    public void Register(string shortcut)
    {
        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ArgumentException(L.T("Пример горячей клавиши: Ctrl+Alt+N"));
        uint modifiers = 0x4000;
        foreach (var part in parts[..^1]) modifiers |= part.ToLowerInvariant() switch { "ctrl" => 2u, "alt" => 1u, "shift" => 4u, "win" => 8u, _ => throw new ArgumentException(L.T("Используйте Ctrl, Alt, Shift или Win")) };
        if (parts.Length < 2 || !Enum.TryParse<Key>(parts[^1], true, out var key) || key == Key.None) throw new ArgumentException(L.T("Пример горячей клавиши: Ctrl+Alt+N"));
        var combination = (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (registeredId == 0 || registered != combination)
        {
            var nextId = registeredId == 1 ? 2 : 1;
            if (!Native.RegisterHotKey(source.Handle, nextId, combination.Item1, combination.Item2))
                throw new InvalidOperationException(L.F("Комбинация {0} занята. Выберите другую.", shortcut));
            Clear();
            registeredId = nextId;
            registered = combination;
        }
        Shortcut = string.Join("+", parts);
    }
    public void Clear()
    {
        if (registeredId != 0) Native.UnregisterHotKey(source.Handle, registeredId);
        registeredId = 0;
        Shortcut = null;
    }
    private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x0312 && registeredId != 0 && wParam == registeredId) { handled = true; action(); }
        return 0;
    }
    public void Dispose() { Clear(); source.Dispose(); }
}

public static class DesktopLayer
{
    private const uint ParentWindow = 1;
    private static readonly WorkerCache Workers = new(FindOrRequestWorker, Native.IsWindow);

    internal static bool IsDesktopAt(Native.CursorPoint point)
    {
        var hit = Native.WindowFromPoint(point);
        var shell = Native.GetShellWindow();
        if (hit == 0 || shell == 0 ||
            Native.GetWindowThreadProcessId(hit, out var process) == 0 ||
            Native.GetWindowThreadProcessId(shell, out var shellProcess) == 0 || process != shellProcess) return false;

        const uint rootWindow = 2;
        var root = Native.GetAncestor(hit, rootWindow);
        if (root == shell) return true;
        var className = new System.Text.StringBuilder(32);
        Native.GetClassName(root, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW";
    }

    public static bool ForegroundIsPaperDesk()
    {
        var handle = Native.GetForegroundWindow();
        return handle != 0 && Native.GetWindowThreadProcessId(handle, out var process) != 0 && process == Environment.ProcessId;
    }

    public static bool Attach(Window window, bool desktop)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var parent = Native.GetAncestor(handle, ParentWindow);
        if (!desktop)
        {
            if (parent != 0 && parent != Native.GetDesktopWindow()) Native.SetParent(handle, 0);
            return true;
        }

        var worker = Workers.Get(Native.GetShellWindow(), Environment.TickCount64);
        if (worker == 0) return false;
        if (parent == worker) return true;
        Native.SetParent(handle, worker);
        return Native.GetAncestor(handle, ParentWindow) == worker;
    }

    private static nint FindOrRequestWorker(nint shell)
    {
        var worker = FindWorker(shell);
        if (worker != 0) return worker;
        var progman = Native.FindWindow("Progman", null);
        if (progman == 0) return 0;
        const uint abortIfHung = 2;
        Native.SendMessageTimeout(progman, 0x052C, 0, 0, abortIfHung, 500, out _);
        return FindWorker(shell);
    }

    private static nint FindWorker(nint shell)
    {
        Native.GetWindowThreadProcessId(shell, out var shellProcess);
        nint worker = 0;
        Native.EnumWindows((top, _) =>
        {
            if (Native.FindWindowEx(top, 0, "SHELLDLL_DefView", null) == 0) return true;
            var candidate = Native.FindWindowEx(0, top, "WorkerW", null);
            if (Native.GetWindowThreadProcessId(candidate, out var process) == 0 || process != shellProcess) return true;
            worker = candidate;
            return false;
        }, 0);
        return worker;
    }

    internal sealed class WorkerCache(Func<nint, nint> discover, Func<nint, bool> isWindow)
    {
        private nint previousShell, worker;
        private long retryAt;

        internal nint Get(nint shell, long now)
        {
            if (shell != previousShell)
            {
                previousShell = shell;
                worker = 0;
                retryAt = 0;
            }
            if (shell == 0) return 0;
            if (worker != 0 && isWindow(worker)) return worker;
            if (now < retryAt) return 0;
            retryAt = now + 30_000;
            worker = discover(shell);
            if (worker != 0) retryAt = 0;
            return worker;
        }
    }
    public static bool FullScreenOtherApp()
    {
        var h = Native.GetForegroundWindow(); if (h == 0) return false;
        Native.GetWindowThreadProcessId(h, out var process); if (process == Environment.ProcessId) return false;
        var className = new System.Text.StringBuilder(256); Native.GetClassName(h, className, 256);
        if (className.ToString() is "Progman" or "WorkerW") return false;
        if (!Native.GetWindowRect(h, out var r)) return false;
        var screen = System.Windows.Forms.Screen.FromHandle(h).Bounds;
        return r.Left <= screen.Left && r.Top <= screen.Top && r.Right >= screen.Right && r.Bottom >= screen.Bottom;
    }
}

internal static class Native
{
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string cls, string? title);
    [DllImport("user32.dll")] internal static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint handle);
    internal delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] internal static extern nint SendMessageTimeout(nint h, uint msg, nint w, nint l, uint flags, uint timeout, out nint result);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint h, uint flags);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint h, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint h, System.Text.StringBuilder text, int max);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint h, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    internal static void MoveWithoutActivate(nint handle, int x, int y)
    {
        const uint parentWindow = 1;
        const uint noSize = 0x0001, noZOrder = 0x0004, noActivate = 0x0010;
        var point = new CursorPoint { X = x, Y = y };
        var parent = GetAncestor(handle, parentWindow);
        if (parent != 0) ScreenToClient(parent, ref point);
        SetWindowPos(handle, 0, point.X, point.Y, 0, 0, noSize | noZOrder | noActivate);
    }
    [DllImport("user32.dll")] private static extern bool ScreenToClient(nint h, ref CursorPoint point);
    [StructLayout(LayoutKind.Sequential)] internal struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(nint handle, int index, int value);
}
