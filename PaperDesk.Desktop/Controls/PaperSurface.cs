namespace PaperDesk.Desktop.Controls;

/// <summary>Draws the sheet and the edges that curl behind the hand.</summary>
public sealed class PaperSurface : Decorator
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(PaperSurface), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(244, 229, 173)), FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    private static readonly DependencyProperty PoseProperty = DependencyProperty.Register(
        nameof(Pose), typeof(PaperPose), typeof(PaperSurface), new FrameworkPropertyMetadata(PaperPose.Flat, FrameworkPropertyMetadataOptions.AffectsRender));
    internal PaperPose Pose { get => (PaperPose)GetValue(PoseProperty); set => SetValue(PoseProperty, value); }
    public static readonly DependencyProperty TextureProperty = DependencyProperty.Register(
        nameof(Texture), typeof(string), typeof(PaperSurface), new FrameworkPropertyMetadata("fibres", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Texture { get => (string)GetValue(TextureProperty); set => SetValue(TextureProperty, value); }
    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners), typeof(string), typeof(PaperSurface), new FrameworkPropertyMetadata("folded", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Corners { get => (string)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public static readonly DependencyProperty PaperOpacityProperty = DependencyProperty.Register(
        nameof(PaperOpacity), typeof(double), typeof(PaperSurface), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double PaperOpacity { get => (double)GetValue(PaperOpacityProperty); set => SetValue(PaperOpacityProperty, value); }

    internal static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(Pen), typeof(PaperSurface), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    internal Pen? Outline { get => (Pen?)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    internal bool ClipContentToPaper { get; set; }

    private static readonly DrawingBrush Fibres = CreateFibres();
    private static readonly DrawingBrush Linen = CreateLinen();
    private static readonly Brush FaceLight = new LinearGradientBrush(Color.FromArgb(34, 255, 255, 255), Colors.Transparent, 70);
    private static readonly Pen RimLight = new(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), 1);

    static PaperSurface()
    {
        FaceLight.Freeze();
        RimLight.Freeze();
    }

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
        var motion = Pose;
        var flex = motion.Flex;
        var left = Math.Clamp(flex.X, 0, 1);
        var right = Math.Clamp(-flex.X, 0, 1);
        var top = Math.Clamp(flex.Y, 0, 1);
        var bottom = Math.Clamp(-flex.Y, 0, 1);
        var peel = Math.Clamp(motion.Peel, 0, 1);
        var anchor = motion.Grip;
        var bow = Math.Clamp(span * .12, 8, 32);
        var peeking = peel * bow;
        var bottomBow = bottom * bow * .5;
        var topBow = top * bow * .65;
        var anchorX = Math.Clamp(anchor.X * w, w * .08, w * .92);
        var nearGlue = 1 - anchor.Y;
        var radius = Corners == "square" ? 0 : 10;
        var folded = Corners == "folded";
        var topLeft = new Corner(0, 0, 1, 1, span, left * .48 + top * .72, radius);
        var topRight = new Corner(w, 0, -1, 1, span, right * .48 + top * .72, radius);
        var bottomLeft = new Corner(0, h, 1, -1, span, left * .98 + bottom * .96 + peel * (.34 + .58 * anchor.X), radius);
        var bottomRight = new Corner(w, h, -1, -1, span,
            right * .98 + bottom * .96 + peel * (.34 + .58 * (1 - anchor.X)), folded ? 24 : radius, folded);

        var paper = new StreamGeometry();
        using (var g = paper.Open())
        {
            g.BeginFigure(topLeft.Horizontal, true, true);
            g.BezierTo(new Point(w * .33, topBow * Math.Abs(.33 - anchor.X)), new Point(w * .66, topBow * Math.Abs(.66 - anchor.X)),
                topRight.Horizontal, true, false);
            g.QuadraticBezierTo(topRight.OuterControl, topRight.Vertical, true, false);
            g.BezierTo(new Point(w - right * bow * (.25 + nearGlue * .27), h * .35),
                new Point(w - right * bow * (.20 + nearGlue * .43), h * .72), bottomRight.Vertical, true, false);
            g.QuadraticBezierTo(bottomRight.OuterControl, bottomRight.Horizontal, true, false);
            DrawBottom(g, bottomRight.Horizontal, bottomLeft.Horizontal);
            g.QuadraticBezierTo(bottomLeft.OuterControl, bottomLeft.Vertical, true, false);
            g.BezierTo(new Point(left * bow * (.20 + nearGlue * .43), h * .72),
                new Point(left * bow * (.25 + nearGlue * .27), h * .35), topLeft.Vertical, true, false);
            g.QuadraticBezierTo(topLeft.OuterControl, topLeft.Horizontal, true, false);
        }
        paper.Freeze();
        if (ClipContentToPaper && Child is { } content) content.Clip = paper;
        var translucent = PaperOpacity < 1;
        if (translucent) dc.PushOpacity(PaperOpacity);
        dc.DrawGeometry(Fill, null, paper);
        dc.PushClip(paper);
        var face = new Rect(0, 0, w, h);
        dc.DrawRectangle(FaceLight, null, face);
        if (Texture != "smooth") dc.DrawRectangle(Texture == "linen" ? Linen : Fibres, null, face);
        if (peelingEdge()) DrawLiftedEdge();
        DrawFold(dc, topLeft);
        DrawFold(dc, topRight);
        DrawFold(dc, bottomLeft);
        DrawFold(dc, bottomRight);
        dc.Pop();
        dc.DrawGeometry(null, RimLight, paper);
        if (translucent) dc.Pop();
        if (Outline is { } outline)
        {
            dc.PushClip(paper);
            dc.DrawGeometry(null, outline, paper);
            dc.Pop();
        }

        bool peelingEdge() => peeking > .25;

        void DrawBottom(StreamGeometryContext geometry, Point rightEdge, Point leftEdge)
        {
            if (!peelingEdge())
            {
                geometry.BezierTo(new Point(w * .62, h - .8 - bottomBow), new Point(w * .38, h + .35 - bottomBow), leftEdge, true, false);
                return;
            }
            var gripEdge = new Point(anchorX, h - Math.Min(1.5, bottomBow));
            geometry.BezierTo(new Point(rightEdge.X, h - peeking * (1 - anchor.X)),
                new Point(anchorX + (rightEdge.X - anchorX) * .33, h - peeking * .36), gripEdge, true, false);
            geometry.BezierTo(new Point(anchorX - (anchorX - leftEdge.X) * .33, h - peeking * .36),
                new Point(leftEdge.X, h - peeking * anchor.X), leftEdge, true, false);
        }

        void DrawLiftedEdge()
        {
            var edge = new StreamGeometry();
            using (var geometry = edge.Open())
            {
                geometry.BeginFigure(bottomRight.Horizontal, true, true);
                DrawBottom(geometry, bottomRight.Horizontal, bottomLeft.Horizontal);
                geometry.QuadraticBezierTo(new Point(anchorX, h - peeking * 1.9), bottomRight.Horizontal, true, false);
            }
            var light = new LinearGradientBrush(Color.FromArgb((byte)(80 * peel), 82, 66, 44),
                Color.FromArgb((byte)(148 * peel), 255, 249, 222), new Point(0, 0), new Point(0, 1));
            dc.DrawGeometry(light, null, edge);
        }
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
