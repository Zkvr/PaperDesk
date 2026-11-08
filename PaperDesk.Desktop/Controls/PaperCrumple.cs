using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace PaperDesk.Desktop.Controls;

/// <summary>Folds a snapshot of the sheet into a textured bundle, then drops it.</summary>
public sealed class PaperCrumple : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PaperCrumple), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsPlaying => image != null;

    private const int Side = 6;
    private static readonly Point[] Sheet = MakeSheet();
    private static readonly Face[] Faces = MakeFaces();
    private static readonly Brush Shadow = new RadialGradientBrush(Color.FromArgb(84, 23, 17, 10), Color.FromArgb(0, 23, 17, 10));
    private BitmapSource? image;

    public PaperCrumple()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
    }

    private const double Folded = .77;

    public bool TryPlay(FrameworkElement sheet, Action finished, bool desktopDrop = false)
    {
        if (IsPlaying || sheet.ActualWidth < 25 || sheet.ActualHeight < 25) return false;
        sheet.UpdateLayout();
        var scale = Math.Min(1.25, 1024 / Math.Max(sheet.ActualWidth, sheet.ActualHeight));
        var snapshot = new RenderTargetBitmap((int)Math.Ceiling(sheet.ActualWidth * scale),
            (int)Math.Ceiling(sheet.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        try { snapshot.Render(sheet); }
        catch (InvalidOperationException) { return false; }
        snapshot.Freeze();

        image = snapshot;
        Progress = 0;
        Visibility = Visibility.Visible;
        var animation = new DoubleAnimation(0, desktopDrop ? Folded : 1, TimeSpan.FromMilliseconds(desktopDrop ? 760 : 980));
        animation.Completed += (_, _) =>
        {
            if (image != snapshot) return;
            try { if (desktopDrop && IsVisible) DropBundle(); }
            finally { Cancel(); finished(); }
        };
        BeginAnimation(ProgressProperty, animation);
        return true;
    }

    public void Cancel()
    {
        image = null;
        Visibility = Visibility.Collapsed;
        BeginAnimation(ProgressProperty, null);
    }

    protected override void OnRender(DrawingContext dc) => DrawFrame(dc, Math.Clamp(Progress, 0, 1));

    private void DropBundle()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target || ActualWidth <= 0 || ActualHeight <= 0) return;
        var radius = Math.Clamp(Math.Min(ActualWidth, ActualHeight) * .18, 17, 45);
        var center = new Point(.52 * ActualWidth, .49 * ActualHeight);
        var region = new Rect(center.X - radius * 1.45, center.Y - radius * 1.45, radius * 2.9, radius * 2.9);
        region.Intersect(new Rect(RenderSize));
        if (region.IsEmpty) return;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            DrawFrame(dc, Folded);
            dc.Pop();
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        var bundle = new RenderTargetBitmap((int)Math.Ceiling(region.Width * dpi.DpiScaleX), (int)Math.Ceiling(region.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bundle.Render(visual);
        bundle.Freeze();
        PaperDropWindow.Launch(bundle, region.Size, PointToScreen(region.TopLeft), target.TransformFromDevice, Window.GetWindow(this)?.Topmost == true);
    }

    private void DrawFrame(DrawingContext dc, double progress)
    {
        if (image == null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var width = ActualWidth;
        var height = ActualHeight;
        var rect = new Rect(0, 0, width, height);
        if (progress < .015)
        {
            dc.DrawImage(image, rect);
            return;
        }

        var squeezeX = Smooth(progress / .61);
        var squeezeY = Smooth((progress - .055) / .62);
        var drop = Smooth((progress - .79) / .21);
        var wrinkle = Math.Sin(Math.PI * squeezeY);
        var span = Math.Min(width, height);
        var radius = Math.Clamp(span * .18, 17, 45) * (1 - .24 * drop);
        var center = new Point(width * (.52 + .16 * drop), height * (.49 + .64 * drop * drop));
        var from = new Point[Sheet.Length];
        var to = new Point[Sheet.Length];
        for (var index = 0; index < Sheet.Length; index++)
        {
            var point = Sheet[index];
            from[index] = new Point(point.X * width, point.Y * height);
            var axis = point - new Point(.5, .5);
            var angle = Math.Atan2(axis.Y, axis.X) + .18 * Math.Sin(index * 2.31) - 1.12 * drop;
            var distance = Math.Min(1, axis.Length * 1.85) * (1 + .22 * Math.Sin(index * 5.7));
            var knot = center + new Vector(Math.Cos(angle), Math.Sin(angle)) * (radius * distance);
            // Alternate ridges buckle before the corners meet at the center.
            to[index] = new Point(
                from[index].X + (knot.X - from[index].X) * squeezeX + Math.Sin(index * 2.6) * span * .13 * wrinkle,
                from[index].Y + (knot.Y - from[index].Y) * squeezeY + Math.Cos(index * 1.7) * span * .12 * wrinkle);
        }

        dc.PushOpacity(1 - Smooth((progress - .90) / .10));
        if (squeezeY > .35)
            dc.DrawEllipse(Shadow, null, center + new Vector(1, radius * .74), radius * 1.25, radius * .43);
        foreach (var face in Faces)
        {
            var a = to[face.A];
            var b = to[face.B];
            var c = to[face.C];
            var area = Vector.CrossProduct(b - a, c - a);
            if (Math.Abs(area) < .3) continue;

            var facet = Triangle(a, b, c);
            var map = TriangleMap(from[face.A], from[face.B], from[face.C], a, b, c);
            dc.PushClip(facet);
            dc.PushTransform(new MatrixTransform(map));
            dc.DrawImage(image, rect);
            dc.Pop();
            dc.Pop();

            var contrast = .23 * Smooth((progress - .015) / .14) + .77 * squeezeY;
            var shade = area < 0 ? -.90 : face.Shade;
            var alpha = (byte)Math.Clamp(Math.Abs(shade) * contrast * 152, 0, 152);
            var pigment = shade < 0 ? Color.FromArgb(alpha, 76, 53, 30) : Color.FromArgb(alpha, 255, 251, 230);
            var softPigment = Color.FromArgb((byte)(alpha * .27), pigment.R, pigment.G, pigment.B);
            var lighting = new LinearGradientBrush(pigment, softPigment, a + (b - a) * .5, c)
            {
                MappingMode = BrushMappingMode.Absolute
            };
            dc.DrawGeometry(lighting, null, facet);
        }
        dc.Pop();
    }

    private static double Smooth(double value)
    {
        var t = Math.Clamp(value, 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var triangle = new StreamGeometry();
        using var path = triangle.Open();
        path.BeginFigure(a, true, true);
        path.LineTo(b, true, false);
        path.LineTo(c, true, false);
        return triangle;
    }

    private static Matrix TriangleMap(Point s0, Point s1, Point s2, Point d0, Point d1, Point d2)
    {
        var x = s1 - s0;
        var y = s2 - s0;
        var a = d1 - d0;
        var b = d2 - d0;
        var det = Vector.CrossProduct(x, y);
        var m11 = (a.X * y.Y - b.X * x.Y) / det;
        var m21 = (b.X * x.X - a.X * y.X) / det;
        var m12 = (a.Y * y.Y - b.Y * x.Y) / det;
        var m22 = (b.Y * x.X - a.Y * y.X) / det;
        return new Matrix(m11, m12, m21, m22, d0.X - m11 * s0.X - m21 * s0.Y, d0.Y - m12 * s0.X - m22 * s0.Y);
    }

    private static Point[] MakeSheet()
    {
        var points = new Point[Side * Side];
        var random = new Random(117);
        for (var row = 0; row < Side; row++)
        for (var col = 0; col < Side; col++)
        {
            var x = (double)col / (Side - 1);
            var y = (double)row / (Side - 1);
            if (col > 0 && col < Side - 1) x += (random.NextDouble() - .5) * .13;
            if (row > 0 && row < Side - 1) y += (random.NextDouble() - .5) * .13;
            points[row * Side + col] = new Point(x, y);
        }
        return points;
    }

    private static Face[] MakeFaces()
    {
        var faces = new List<Face>();
        var random = new Random(72);
        for (var row = 0; row < Side - 1; row++)
        for (var col = 0; col < Side - 1; col++)
        {
            var a = row * Side + col;
            var b = a + 1;
            var c = a + Side;
            var d = c + 1;
            var dark = -.34 - random.NextDouble() * .56;
            var light = .23 + random.NextDouble() * .67;
            if ((row + col) % 2 == 0) { faces.Add(new(a, b, d, dark)); faces.Add(new(a, d, c, light)); }
            else { faces.Add(new(a, b, c, light)); faces.Add(new(b, d, c, dark)); }
        }
        return faces.ToArray();
    }

    private readonly record struct Face(int A, int B, int C, double Shade);
}
