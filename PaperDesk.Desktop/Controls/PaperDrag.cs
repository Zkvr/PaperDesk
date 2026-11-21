using System.Diagnostics;
using System.Windows.Input;
using PaperDesk.Desktop.Win32Interop;

namespace PaperDesk.Desktop.Controls;

/// <summary>Moves a sheet directly, without entering the Windows move/Snap loop.</summary>
internal sealed class PaperDrag
{
    private readonly Window window;
    private readonly UIElement paper;
    private readonly Action<bool, Point> started;
    private readonly Action<Point, Vector, double, double, Point?> moved;
    private readonly Action<Point?, Rect> finished;
    private PaperDragTrack? track;
    private Point press, grip;
    private Rect origin;
    private Point screenOrigin;
    private Matrix toDevice;
    private bool dragging, wholeStack;
    private PaperContact contact;
    internal bool InContact => dragging && !contact.Free;

    public PaperDrag(Window window, UIElement paper, UIElement moveHandle, UIElement peelHandle,
        Action<bool, Point> started, Action<Point, Vector, double, double, Point?> moved, Action<Point?, Rect> finished)
    {
        this.window = window;
        this.paper = paper;
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
        grip = e.GetPosition(paper);
        wholeStack = moveWholeStack;
        contact = new PaperContact(paper.RenderSize.Height, wholeStack);
        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        screenOrigin = window.PointToScreen(new Point());
        toDevice = fromDevice;
        toDevice.Invert();
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
            started(wholeStack, grip);
            if (track == null) return;
        }
        var travel = bounds.TopLeft - origin.TopLeft;
        var offset = contact.Move(travel, out var fraction);
        moved(screen, velocity, fraction, travel.X / Math.Max(12, travel.Length), screenOrigin + toDevice.Transform(offset));
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
        if (drop && contact.Free && Native.GetCursorPos(out var pointer)) finished(new Point(pointer.X, pointer.Y), origin);
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

    internal static Vector? SnapOffset(Rect sheet, IEnumerable<Rect> neighbours, double gap, double threshold)
    {
        double? x = null, y = null;
        foreach (var neighbour in neighbours)
        {
            if (neighbour.IsEmpty) continue;
            if (Overlap(sheet.Top, sheet.Bottom, neighbour.Top, neighbour.Bottom) >= threshold)
            {
                Consider(ref x, neighbour.Left - sheet.Right - gap);
                Consider(ref x, neighbour.Right - sheet.Left + gap);
            }
            if (Overlap(sheet.Left, sheet.Right, neighbour.Left, neighbour.Right) >= threshold)
            {
                Consider(ref y, neighbour.Top - sheet.Bottom - gap);
                Consider(ref y, neighbour.Bottom - sheet.Top + gap);
            }
        }
        return x.HasValue || y.HasValue ? new Vector(x ?? 0, y ?? 0) : null;

        void Consider(ref double? closest, double offset)
        {
            var distance = Math.Abs(offset);
            if (distance <= threshold && (!closest.HasValue || distance < Math.Abs(closest.Value))) closest = offset;
        }
        static double Overlap(double start, double end, double otherStart, double otherEnd) =>
            Math.Min(end, otherEnd) - Math.Max(start, otherStart);
    }
}

internal struct PaperContact(double height, bool free)
{
    private readonly double distance = Math.Clamp(height * .23, 38, 84);
    private Vector releaseOffset;
    private double slack = 1;
    internal bool Free { get; private set; } = free;

    internal Vector Move(Vector travel, out double fraction)
    {
        // A pull back over the glue releases sooner than shearing along the desk.
        fraction = new Vector(.76 * travel.X, travel.Y < 0 ? travel.Y : .58 * travel.Y).Length / distance;
        if (!Free)
        {
            if (fraction < 1) return default;
            Free = true;
            releaseOffset = travel / fraction;
        }
        slack = Math.Min(slack, Math.Max(0, 1 - (travel - releaseOffset).Length / distance));
        return travel - releaseOffset * slack;
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
