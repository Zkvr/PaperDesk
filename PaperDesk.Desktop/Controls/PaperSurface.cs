namespace PaperDesk.Desktop.Controls;

/// <summary>Draws the sheet and the edges that curl behind the hand.</summary>
public sealed class PaperSurface : Decorator
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(PaperSurface), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(244, 229, 173)), FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public static readonly DependencyProperty CurlLiftProperty = DependencyProperty.Register(
        nameof(CurlLift), typeof(double), typeof(PaperSurface), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double CurlLift { get => (double)GetValue(CurlLiftProperty); set => SetValue(CurlLiftProperty, value); }
    public static readonly DependencyProperty DragPullProperty = DependencyProperty.Register(
        nameof(DragPull), typeof(Vector), typeof(PaperSurface), new FrameworkPropertyMetadata(new Vector(), FrameworkPropertyMetadataOptions.AffectsRender));
    public Vector DragPull { get => (Vector)GetValue(DragPullProperty); set => SetValue(DragPullProperty, value); }
    public static readonly DependencyProperty TextureProperty = DependencyProperty.Register(
        nameof(Texture), typeof(string), typeof(PaperSurface), new FrameworkPropertyMetadata("fibres", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Texture { get => (string)GetValue(TextureProperty); set => SetValue(TextureProperty, value); }
    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners), typeof(string), typeof(PaperSurface), new FrameworkPropertyMetadata("folded", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Corners { get => (string)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public static readonly DependencyProperty PaperOpacityProperty = DependencyProperty.Register(
        nameof(PaperOpacity), typeof(double), typeof(PaperSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double PaperOpacity { get => (double)GetValue(PaperOpacityProperty); set => SetValue(PaperOpacityProperty, value); }

    private static readonly DrawingBrush Fibres = CreateFibres();
    private static readonly DrawingBrush Linen = CreateLinen();
    private static readonly Brush FaceLight = new LinearGradientBrush(Color.FromArgb(34, 255, 255, 255), Colors.Transparent, 70);
    private static readonly Pen RimLight = new(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);

    static PaperSurface()
    {
        FaceLight.Freeze();
        RimLight.Freeze();
    }

    internal static double BottomRightDragLift(Vector pull) => Math.Clamp(-Math.Min(0, pull.X) * .94 - Math.Min(0, pull.Y) * .94, 0, 1);

    internal void ApplyAppearance(Placement placement)
    {
        Fill = PaperPalette.Brush(placement.Color);
        Texture = placement.Texture;
        Corners = placement.Corners;
        PaperOpacity = placement.Opacity;
    }

    internal static (double Blur, double Depth, double Opacity) ShadowStyle(string shadow) => shadow switch
    {
        "none" => (0, 0, 0),
        "deep" => (27, 10, .43),
        _ => (20, 7, .28)
    };

    internal double ApplyShadow(System.Windows.Media.Effects.DropShadowEffect effect, string shadow)
    {
        var (blur, depth, opacity) = ShadowStyle(shadow);
        effect.BlurRadius = blur;
        effect.ShadowDepth = depth;
        effect.Opacity = opacity;
        Effect = opacity == 0 ? null : effect;
        return depth;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 25 || h < 25) return;

        var span = Math.Min(w, h);
        var pull = DragPull;
        var left = Math.Max(0, pull.X);
        var right = Math.Max(0, -pull.X);
        var top = Math.Max(0, pull.Y);
        var bottom = Math.Max(0, -pull.Y);
        var radius = Corners == "square" ? 0 : 10;
        var folded = Corners == "folded";
        var topLeft = new Corner(0, 0, 1, 1, span, left * .64 + top * .92, radius);
        var topRight = new Corner(w, 0, -1, 1, span, right * .64 + top * .92, radius);
        var bottomRight = new Corner(w, h, -1, -1, span, Math.Max(CurlLift, BottomRightDragLift(pull)), folded ? 24 : radius, folded);
        var bottomLeft = new Corner(0, h, 1, -1, span, left * .94 + bottom * .94, radius);

        var paper = new StreamGeometry();
        using (var g = paper.Open())
        {
            g.BeginFigure(topLeft.Horizontal, true, true);
            g.LineTo(topRight.Horizontal, true, false);
            g.QuadraticBezierTo(topRight.OuterControl, topRight.Vertical, true, false);
            g.LineTo(bottomRight.Vertical, true, false);
            g.QuadraticBezierTo(bottomRight.OuterControl, bottomRight.Horizontal, true, false);
            g.BezierTo(new Point(w * .62, h - .8), new Point(w * .38, h + .35), bottomLeft.Horizontal, true, false);
            g.QuadraticBezierTo(bottomLeft.OuterControl, bottomLeft.Vertical, true, false);
            g.LineTo(topLeft.Vertical, true, false);
            g.QuadraticBezierTo(topLeft.OuterControl, topLeft.Horizontal, true, false);
        }
        paper.Freeze();
        var translucent = PaperOpacity < 1;
        if (translucent) dc.PushOpacity(PaperOpacity);
        dc.DrawGeometry(Fill, null, paper);
        dc.PushClip(paper);
        var face = new Rect(0, 0, w, h);
        dc.DrawRectangle(FaceLight, null, face);
        if (Texture != "smooth") dc.DrawRectangle(Texture == "linen" ? Linen : Fibres, null, face);
        DrawFold(dc, topLeft);
        DrawFold(dc, topRight);
        DrawFold(dc, bottomLeft);
        DrawFold(dc, bottomRight);
        dc.Pop();
        dc.DrawGeometry(null, RimLight, paper);
        if (translucent) dc.Pop();
    }

    private static void DrawFold(DrawingContext dc, Corner corner)
    {
        if (!corner.RestingFold && corner.Lift < .015) return;
        var radius = corner.Radius;
        var ridge = corner.At(radius * .79, radius * .67);
        var fold = new StreamGeometry();
        using (var g = fold.Open())
        {
            g.BeginFigure(corner.Horizontal, true, true);
            g.QuadraticBezierTo(ridge, corner.Vertical, true, false);
            g.QuadraticBezierTo(corner.OuterControl, corner.Horizontal, true, false);
        }
        var crease = new StreamGeometry();
        using (var g = crease.Open())
        {
            g.BeginFigure(corner.Horizontal, false, false);
            g.QuadraticBezierTo(ridge, corner.Vertical, true, false);
        }
        dc.PushOpacity(corner.RestingFold ? 1 : Math.Min(1, corner.Lift * 2));
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(24 + 33 * corner.Lift), 90, 73, 45)), 2 + 2 * corner.Lift), crease);
        var underside = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = corner.At(radius * .27, radius * .27),
            EndPoint = corner.At(radius * .66, radius * .66),
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(224, 255, 255, 245), 0),
                new(Color.FromArgb(131, 255, 248, 219), .42),
                new(Color.FromArgb((byte)(76 + 45 * corner.Lift), 109, 91, 61), 1)
            }
        };
        dc.DrawGeometry(underside, null, fold);
        dc.Pop();
    }

    private readonly struct Corner(double x, double y, int dx, int dy, double span, double lift, double restRadius, bool restingFold = false)
    {
        public bool RestingFold => restingFold;
        public double Lift => Math.Clamp(lift, 0, 1);
        public double Radius
        {
            get
            {
                var rest = Math.Min(restRadius, span * .12);
                return rest + (Math.Min(70, span * .35) - rest) * Lift;
            }
        }
        public Point At(double horizontal, double vertical) => new(x + dx * horizontal, y + dy * vertical);
        public Point Horizontal => At(Radius, 0);
        public Point Vertical => At(0, Radius);
        public Point OuterControl => restingFold ? At(3, 3) : At(3 * Lift, 3 * Lift);
    }

    private static DrawingBrush CreateFibres()
    {
        var drawing = new DrawingGroup(); var random = new Random(23);
        using (var dc = drawing.Open())
            for (var i = 0; i < 100; i++)
            {
                var x = random.NextDouble() * 96; var y = random.NextDouble() * 96;
                var color = i % 3 == 0 ? Color.FromArgb(33, 255, 255, 255) : Color.FromArgb(15, 100, 81, 49);
                dc.DrawLine(new Pen(new SolidColorBrush(color), .45), new Point(x, y), new Point(x + 1 + random.NextDouble() * 4, y - .4));
            }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 96, 96), Stretch = Stretch.None };
        brush.Freeze(); return brush;
    }

    private static DrawingBrush CreateLinen()
    {
        var drawing = new DrawingGroup();
        var dark = new Pen(new SolidColorBrush(Color.FromArgb(12, 84, 63, 39)), .48);
        var light = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), .48);
        using (var dc = drawing.Open())
        {
            for (var offset = 1; offset < 12; offset += 3)
            {
                dc.DrawLine(dark, new Point(offset, 0), new Point(offset, 12));
                dc.DrawLine(light, new Point(offset + .75, 0), new Point(offset + .75, 12));
                dc.DrawLine(dark, new Point(0, offset), new Point(12, offset));
                dc.DrawLine(light, new Point(0, offset + .75), new Point(12, offset + .75));
            }
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            ViewboxUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 12, 12),
            Viewbox = new Rect(0, 0, 12, 12), Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }
}
