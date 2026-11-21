using System.Diagnostics;

namespace PaperDesk.Desktop.Controls;

internal readonly record struct PaperPose(Point Grip, Vector Flex, double Peel, Vector Back, Vector Rear, double FreeGrip = 0, double Shear = 0)
{
    internal static PaperPose Flat => new(new Point(.5, .07), default, 0, default, default);
}

/// <summary>Elastic modes of a paper sheet and the lag of the pages underneath it.</summary>
internal sealed class PaperMotion
{
    private Vector input, flex, flexSpeed, back, backSpeed, rear, rearSpeed;
    private double lift, liftSpeed, pullFraction, freeGrip, shearInput, shear;
    private Point grip;
    private bool held, peeling;
    internal bool Detached { get; private set; }
    internal PaperPose Pose => new(grip, flex, lift, back, rear, freeGrip, shear);

    internal void Hold(Point anchor, bool peel)
    {
        grip = anchor;
        input = flex = flexSpeed = back = backSpeed = rear = rearSpeed = default;
        lift = liftSpeed = pullFraction = freeGrip = shearInput = shear = 0;
        held = true;
        peeling = peel;
        Detached = false;
    }

    internal void Pull(Vector velocity, double fraction, double lateral = 0)
    {
        input = velocity / 840;
        if (input.LengthSquared > 1) input.Normalize();
        pullFraction = Math.Clamp(fraction, 0, 1);
        Detached |= peeling && pullFraction >= 1;
        shearInput = peeling && !Detached ? lateral * pullFraction : 0;
    }

    internal void LetGo() => held = false;

    internal void Land(PaperPose pose)
    {
        Hold(pose.Grip, peel: false);
        held = false;
        flex = pose.Flex;
        lift = pose.Peel;
        back = pose.Back;
        rear = pose.Rear;
        freeGrip = pose.FreeGrip;
        shear = pose.Shear;
    }

    internal bool Advance(double seconds, double sincePull)
    {
        var force = held ? input * Math.Exp(-18 * Math.Max(0, sincePull - .035)) : default;
        if (force.LengthSquared < .00005) force = default;
        var curl = !held ? 0 : !peeling ? .20 : Detached ? .36 + .18 * force.Length : Math.Sqrt(pullFraction);
        var free = held && (!peeling || Detached) ? 1d : 0d;
        var twist = held ? shearInput : 0;

        // Substeps keep the springs stable on low-refresh displays and after a missed frame.
        var elapsed = Math.Clamp(seconds, 0, .05);
        var steps = Math.Max(1, (int)Math.Ceiling(elapsed * 120));
        var dt = elapsed / steps;
        for (var i = 0; i < steps; i++)
        {
            Spring(ref flex, ref flexSpeed, force, 430, held ? 28 : 25, dt);
            Spring(ref back, ref backSpeed, flex * .57, 520, 30, dt);
            Spring(ref rear, ref rearSpeed, back * .56, 480, 30, dt);
            liftSpeed += ((curl - lift) * 780 - liftSpeed * 34) * dt;
            lift += liftSpeed * dt;
            freeGrip += (free - freeGrip) * (1 - Math.Exp(-20 * dt));
            shear += (twist - shear) * (1 - Math.Exp(-20 * dt));
        }
        if (force != default || flex.LengthSquared + back.LengthSquared + rear.LengthSquared > .00008 ||
            flexSpeed.LengthSquared + backSpeed.LengthSquared + rearSpeed.LengthSquared > .009 ||
            Math.Abs(lift - curl) > .004 || Math.Abs(liftSpeed) > .045 ||
            Math.Abs(free - freeGrip) > .004 || Math.Abs(twist - shear) > .004) return true;
        flex = back = rear = flexSpeed = backSpeed = rearSpeed = default;
        lift = curl;
        liftSpeed = 0;
        freeGrip = free;
        shear = twist;
        return false;
    }

    private static void Spring(ref Vector value, ref Vector velocity, Vector target, double stiffness, double damping, double dt)
    {
        velocity += ((target - value) * stiffness - velocity * damping) * dt;
        value += velocity * dt;
    }
}

internal sealed class PaperDragResponse(Action<PaperPose> render)
{
    private readonly PaperMotion motion = new();
    private TimeSpan? lastFrame;
    private long lastPull;
    private bool grabbed, rendering;

    internal void Grab(Point anchor, bool peel)
    {
        motion.Hold(anchor, peel);
        grabbed = true;
        lastPull = Stopwatch.GetTimestamp();
        StartRendering();
    }

    internal void Follow(Vector velocity, double fraction, double lateral = 0)
    {
        if (!grabbed) return;
        motion.Pull(velocity, fraction, lateral);
        lastPull = Stopwatch.GetTimestamp();
        StartRendering();
    }

    internal void Release()
    {
        if (!grabbed) return;
        grabbed = false;
        motion.LetGo();
        StartRendering();
    }

    internal void Land(PaperPose pose)
    {
        grabbed = false;
        motion.Land(pose);
        render(pose);
        StartRendering();
    }

    internal void Stop()
    {
        StopRendering();
        grabbed = false;
        motion.LetGo();
    }

    private void StartRendering()
    {
        if (rendering) return;
        rendering = true;
        lastFrame = null;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!rendering) return;
        rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs args)
    {
        if (args is not RenderingEventArgs frame || lastFrame == frame.RenderingTime) return;
        var dt = lastFrame is TimeSpan previous ? (frame.RenderingTime - previous).TotalSeconds : 1d / 60;
        lastFrame = frame.RenderingTime;
        if (!motion.Advance(dt, Stopwatch.GetElapsedTime(lastPull).TotalSeconds)) StopRendering();
        render(motion.Pose);
    }
}
