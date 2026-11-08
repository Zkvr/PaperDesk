using System.Diagnostics;
using System.Windows.Input;
using PaperDesk.Desktop.Win32Interop;

namespace PaperDesk.Desktop.Controls;

/// <summary>Moves a sheet directly, without entering the Windows move/Snap loop.</summary>
public sealed class PaperDrag
{
    private readonly Window window;
    private readonly Action<bool> started;
    private readonly Action<Point, Vector> moved;
    private readonly Action<Point?, Rect> finished;
    private PaperDragTrack? track;
    private Point press;
    private Rect origin;
    private bool dragging, wholeStack;

    public PaperDrag(Window window, UIElement moveHandle, UIElement peelHandle, Action<bool> started, Action<Point, Vector> moved, Action<Point?, Rect> finished)
    {
        this.window = window;
        this.started = started;
        this.moved = moved;
        this.finished = finished;
        moveHandle.MouseLeftButtonDown += (_, e) => Begin(e, moveWholeStack: true);
        peelHandle.MouseLeftButtonDown += (_, e) => Begin(e, moveWholeStack: false);
        window.PreviewMouseMove += Move;
        window.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (track == null) return;
            End(drop: true);
            e.Handled = true;
        };
        window.LostMouseCapture += (_, _) => { if (Mouse.Captured != window) Cancel(); };
        window.Deactivated += (_, _) => Cancel();
        window.PreviewKeyDown += (_, e) =>
        {
            if (track == null || e.Key != Key.Escape) return;
            e.Handled = true;
            Cancel();
        };
    }
    private void Begin(MouseButtonEventArgs e, bool moveWholeStack)
    {
        if (track != null || e.LeftButton != MouseButtonState.Pressed || !Native.GetCursorPos(out var pointer)) return;
        origin = new Rect(window.Left, window.Top, window.Width, window.Height);
        press = e.GetPosition(window);
        wholeStack = moveWholeStack;
        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        // Capture the stable window, whose child sheet changes shape during the gesture.
        if (!window.CaptureMouse()) return;
        track = new PaperDragTrack(new Point(pointer.X, pointer.Y), origin, fromDevice, Stopwatch.GetTimestamp());
        e.Handled = true;
    }
    private void Move(object sender,MouseEventArgs e)
    {
        if (track == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Cancel();
            return;
        }
        var delta = e.GetPosition(window) - press;
        if (!dragging && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        FollowPointer();
        e.Handled = true;
    }

    private void FollowPointer()
    {
        // WPF can raise a move when its hit test changes while the mouse stays still.
        // An absolute cursor sample is independent of the moving window and its transforms.
        if (track == null || !Native.GetCursorPos(out var pointer)) return;
        var screen = new Point(pointer.X, pointer.Y);
        if (!track.TryMove(screen, Stopwatch.GetTimestamp(), out var bounds, out var velocity)) return;
        if (!dragging)
        {
            dragging = true;
            started(wholeStack);
            if (track == null) return;
        }
        if (window.Left != bounds.Left) window.Left = bounds.Left;
        if (window.Top != bounds.Top) window.Top = bounds.Top;
        moved(screen, velocity);
    }

    public void Cancel() => End(drop: false);

    private void End(bool drop)
    {
        if (track == null) return;
        if (dragging && drop) FollowPointer();
        if (track == null) return;
        track = null;
        var wasDragging = dragging;
        dragging = false;
        if (window.IsMouseCaptured) window.ReleaseMouseCapture();
        if (!wasDragging) return;
        if (drop && Native.GetCursorPos(out var pointer)) finished(new Point(pointer.X, pointer.Y), origin);
        else
        {
            window.Left = origin.Left;
            window.Top = origin.Top;
            finished(null, origin);
        }
    }
    public static Rect MovedBounds(Rect original,Vector physicalDelta,Matrix fromDevice)
    {
        var delta=fromDevice.Transform(physicalDelta);
        return new Rect(original.Left+delta.X,original.Top+delta.Y,original.Width,original.Height);
    }
}

internal sealed class PaperDragTrack
{
    private readonly Point screenAnchor;
    private readonly Rect windowAnchor;
    private readonly Matrix fromDevice;
    private Point previous;
    private long previousTime;

    public PaperDragTrack(Point screenAnchor, Rect windowAnchor, Matrix fromDevice, long startedAt)
    {
        this.screenAnchor = previous = screenAnchor;
        this.windowAnchor = windowAnchor;
        this.fromDevice = fromDevice;
        previousTime = startedAt;
    }

    public bool TryMove(Point screenPoint, long now, out Rect bounds, out Vector velocity)
    {
        bounds = default;
        velocity = default;
        if (screenPoint == previous) return false;

        var seconds = Math.Clamp(Stopwatch.GetElapsedTime(previousTime, now).TotalSeconds, .006, .1);
        velocity = fromDevice.Transform(screenPoint - previous) / seconds;
        bounds = PaperDrag.MovedBounds(windowAnchor, screenPoint - screenAnchor, fromDevice);
        previous = screenPoint;
        previousTime = now;
        return true;
    }
}
