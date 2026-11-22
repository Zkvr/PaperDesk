namespace PaperDesk.Desktop.Controls;

// One stationary transparent window lets the incoming sheet cross both window boundaries.
internal static class StackLanding
{
    internal static PaperPose PoseAt(PaperPose start, double progress)
    {
        var remaining = 1 - Ease(progress);
        // A flat sheet bows slightly as it meets the glue. An already lifted one unrolls directly.
        var fold = start.Peel * remaining + Math.Max(0, .72 - start.Peel) *
            Math.Pow(Math.Sin(Math.PI * progress), 2) * (1 - progress);
        return start with { Peel = fold, Flex = start.Flex * remaining,
            FreeGrip = start.FreeGrip * remaining, Shear = start.Shear * remaining };
    }

    private static double Ease(double t) => t * t * (3 - 2 * t);

    internal static Window Create(Window target, Rect from, Rect to, PaperPose pose, PaperSurface underneath, PaperSurface incoming)
    {
        var matrix = PresentationSource.FromVisual(target)!.CompositionTarget!.TransformFromDevice;
        from = Rect.Transform(from, matrix);
        to = Rect.Transform(to, matrix);
        var area = Rect.Union(from, to);
        area.Inflate(Math.Max(from.Width, to.Width) * .12, Math.Max(from.Height, to.Height) * .15);
        var stage = new Canvas();
        var window = new Window { Style = null, Owner = target, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false,
            Focusable = false, IsHitTestVisible = false, Topmost = true, Content = stage,
            Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height };
        WindowMaterial.SetEnabled(window, false);
        DesktopCardDrag.MakePassThrough(window);
        underneath.Width = to.Width;
        underneath.Height = to.Height;
        Canvas.SetLeft(underneath, to.Left - area.Left);
        Canvas.SetTop(underneath, to.Top - area.Top);
        stage.Children.Add(underneath);

        incoming.Measure(to.Size);
        incoming.Arrange(new Rect(to.Size));
        incoming.UpdateLayout();
        var bent = new BentPaperView { Width = to.Width, Height = to.Height };
        stage.Children.Add(bent);
        var transform = new MatrixTransform();
        bent.RenderTransform = transform;
        if (pose.Peel < .002) pose = PaperPose.Flat with { Grip = new Point(from.X < to.X ? .25 : .75, .94) };
        Draw(0);
        TimeSpan? first = null;
        window.ContentRendered += Start;
        window.Closed += (_, _) => { CompositionTarget.Rendering -= Render; bent.Clear(); window.Content = null; };
        return window;

        void Start(object? sender, EventArgs args)
        {
            window.ContentRendered -= Start;
            CompositionTarget.Rendering += Render;
        }
        void Render(object? sender, EventArgs args)
        {
            var now = ((RenderingEventArgs)args).RenderingTime;
            first ??= now;
            var t = Math.Clamp((now - first.Value).TotalMilliseconds / 475, 0, 1);
            if (t >= 1) { window.Close(); return; }
            Draw(t);
        }
        void Draw(double t)
        {
            var travel = Ease(Math.Min(1, t / .85));
            var delta = (from.TopLeft - to.TopLeft) * (1 - travel);
            transform.Matrix = new Matrix(1 + (from.Width / to.Width - 1) * (1 - travel), 0, 0,
                1 + (from.Height / to.Height - 1) * (1 - travel), to.Left - area.Left + delta.X, to.Top - area.Top + delta.Y);
            bent.Show(incoming, PoseAt(pose, t), shadow: true);
        }
    }
}
