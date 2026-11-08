using System.Diagnostics;

namespace PaperDesk.Desktop.Controls;

/// <summary>Lets the loose part of the sheet trail the hand and spring back after release.</summary>
internal sealed class PaperDragResponse(Action<Vector, double> render)
{
    private Vector target, pull, momentum;
    private long lastSample;
    private TimeSpan? lastFrame;
    private bool grabbed, rendering;
    private double grip;

    public void Grab()
    {
        grabbed = true;
        target = default;
        if (rendering) return;
        rendering = true;
        lastSample = Stopwatch.GetTimestamp();
        lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Follow(Vector velocity)
    {
        if (!grabbed) return;
        target = velocity / 570;
        if (target.LengthSquared > 1) target.Normalize();
        lastSample = Stopwatch.GetTimestamp();
    }

    public void Release() => grabbed = false;

    public void Stop()
    {
        if (rendering) CompositionTarget.Rendering -= OnFrame;
        rendering = grabbed = false;
        target = pull = momentum = default;
        grip = 0;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || lastFrame == frame.RenderingTime) return;
        var elapsed = lastFrame.HasValue ? frame.RenderingTime - lastFrame.Value : TimeSpan.FromMilliseconds(16);
        lastFrame = frame.RenderingTime;
        var dt = Math.Clamp(elapsed.TotalSeconds, .001, .035);
        var now = Stopwatch.GetTimestamp();
        var idle = Math.Max(0, Stopwatch.GetElapsedTime(lastSample, now).TotalSeconds - .045);
        var desired = grabbed ? target * Math.Exp(-idle * 13) : default;
        // A light spring responds to a direction change without snapping to the new pose.
        momentum += ((desired - pull) * 240 - momentum * 17) * dt;
        pull += momentum * dt;
        if (pull.LengthSquared > 1) pull.Normalize();
        grip += ((grabbed ? 1 : 0) - grip) * (1 - Math.Exp(-dt * 24));
        if (!grabbed && grip < .015 && pull.LengthSquared < .00005 && momentum.LengthSquared < .005)
        {
            Stop();
            render(default, 0);
            return;
        }
        render(pull, grip);
    }
}
