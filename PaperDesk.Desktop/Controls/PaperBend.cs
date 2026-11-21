using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;

namespace PaperDesk.Desktop.Controls;

// The surface rolls around an oblique line. Arc length is measured on the original
// sheet, so the ink, fibres and paper take exactly the same bend without stretching.
internal readonly struct PaperBend
{
    private readonly Vector normal;
    private readonly double contact, curvature;
    private readonly Vector offset;
    private readonly Point pivot;
    private readonly double swingSin, swingCos;

    internal PaperBend(Size size, PaperPose pose)
    {
        var height = size.Height;
        var curl = Math.Clamp(pose.Peel, 0, 1);
        pivot = new Point(pose.Grip.X * size.Width, pose.Grip.Y * height);
        var swing = Math.Clamp(pose.Flex.X, -1, 1) * pose.FreeGrip * Math.Min(.055, 7 / Math.Max(size.Width, height));
        (swingSin, swingCos) = Math.SinCos(swing);
        normal = new Vector((pose.Grip.X - .5) * .86 - .35 * pose.Flex.X - .62 * pose.Shear, 1);
        normal.Normalize();
        var topEnd = Math.Max(0, normal.X * size.Width);
        // On landing the contact line sweeps back down from the adhesive strip.
        contact = topEnd + height * (.12 + .45 * (1 - curl)) * normal.Y;
        var run = Math.Max(1, height * normal.Y + topEnd - contact);
        // Moving down tucks the trailing edge; lifting the hand lets that edge unroll.
        var carry = .60 * Math.Abs(pose.Flex.X) + .82 * pose.Flex.Y;
        curvature = Math.Clamp(2.15 * curl + carry * pose.FreeGrip, 0, 2.5) / run;
        var gripDistance = normal.X * pose.Grip.X * size.Width + normal.Y * pose.Grip.Y * height - contact;
        offset = gripDistance > 0 && curvature >= 1e-8
            ? normal * ((gripDistance - Math.Sin(gripDistance * curvature) / curvature) * pose.FreeGrip)
            : default;
    }

    internal Point3D At(double x, double y)
    {
        var distance = normal.X * x + normal.Y * y - contact;
        var z = 0d;
        if (distance > 0 && curvature >= 1e-8)
        {
            var (sin, cos) = Math.SinCos(distance * curvature);
            var retreat = distance - sin / curvature;
            x -= normal.X * retreat;
            y -= normal.Y * retreat;
            z = (1 - cos) / curvature;
        }
        x += offset.X - pivot.X;
        y += offset.Y - pivot.Y;
        return new Point3D(pivot.X + swingCos * x - swingSin * y, pivot.Y + swingSin * x + swingCos * y, z);
    }

    internal Vector3D SurfaceNormal(double x, double y)
    {
        var angle = Math.Max(0, normal.X * x + normal.Y * y - contact) * curvature;
        var (sin, cos) = Math.SinCos(angle);
        return new Vector3D(-(swingCos * normal.X - swingSin * normal.Y) * sin,
            (swingSin * normal.X + swingCos * normal.Y) * sin, cos);
    }
}

/// <summary>Projects the live paper face onto its bent surface only while lifted.</summary>
public sealed class BentPaperView : Decorator
{
    private const int Columns = 8, Rows = 20;
    private MeshGeometry3D? mesh;
    private GeometryModel3D? model;
    private OrthographicCamera? camera;
    private ImageBrush? ink;
    private PaperBend bend;
    private bool castsShadow;

    public BentPaperView() { Visibility = Visibility.Collapsed; IsHitTestVisible = false; }

    private bool ready;

    internal void Show(PaperSurface sheet, PaperPose pose, bool shadow, NoteBodyEditor? editor = null)
    {
        var size = sheet.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) return;
        if (mesh == null)
        {
            Create(sheet);
            ready = editor?.IsWebActive != true;
            if (!ready) _ = ApplyBrowserInk(sheet, editor!);
        }
        var width = size.Width;
        var height = size.Height;
        camera!.Width = width;
        camera.Position = new Point3D(width / 2, -height / 2, 4096);
        bend = new PaperBend(size, pose);
        var vertices = new Point3DCollection((Columns + 1) * (Rows + 1));
        var normals = new Vector3DCollection((Columns + 1) * (Rows + 1));
        for (var row = 0; row <= Rows; row++)
        for (var col = 0; col <= Columns; col++)
        {
            var x = width * col / Columns;
            var y = height * row / Rows;
            var point = bend.At(x, y);
            vertices.Add(new Point3D(point.X, -point.Y, point.Z));
            normals.Add(bend.SurfaceNormal(x, y));
        }
        vertices.Freeze();
        mesh!.Positions = vertices;
        normals.Freeze();
        mesh.Normals = normals;
        castsShadow = shadow;
        if (ready) Visibility = Visibility.Visible;
        InvalidateVisual();
    }

    private async Task ApplyBrowserInk(PaperSurface sheet, NoteBodyEditor editor)
    {
        // A composition-hosted browser isn't present in WPF's offscreen visual brush.
        var current = model!;
        var bounds = editor.TransformToAncestor(sheet).TransformBounds(new Rect(editor.RenderSize));
        var size = sheet.RenderSize;
        var browser = await editor.CapturePaperInk();
        if (browser == null || current != model || sheet.RenderSize != size) return;
        var face = new DrawingGroup();
        using (var dc = face.Open())
        {
            dc.DrawRectangle(ink, null, new Rect(size));
            if (sheet.Child?.Clip is { } clip) dc.PushClip(clip);
            dc.DrawImage(browser, bounds);
            if (sheet.Child?.Clip != null) dc.Pop();
        }
        current.Material = new DiffuseMaterial(new DrawingBrush(face) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size) });
        ready = true;
        Visibility = Visibility.Visible;
    }

    private void Create(PaperSurface sheet)
    {
        var uv = new PointCollection();
        var triangles = new Int32Collection();
        for (var row = 0; row <= Rows; row++)
        for (var col = 0; col <= Columns; col++)
        {
            uv.Add(new Point((double)col / Columns, (double)row / Rows));
            if (col == Columns || row == Rows) continue;
            var a = row * (Columns + 1) + col;
            var b = a + Columns + 1;
            triangles.Add(a); triangles.Add(b); triangles.Add(a + 1);
            triangles.Add(a + 1); triangles.Add(b); triangles.Add(b + 1);
        }
        triangles.Freeze(); uv.Freeze();
        mesh = new MeshGeometry3D { TextureCoordinates = uv, TriangleIndices = triangles };
        // Take one local-space image before hiding the live controls. A visual brush can
        // change with its source's hover, layout offset or opacity during the gesture.
        var size = sheet.RenderSize;
        var dpi = VisualTreeHelper.GetDpi(sheet);
        var scale = Math.Min(dpi.DpiScaleX, 2048 / Math.Max(size.Width, size.Height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(sheet);
        bitmap.Freeze();
        ink = new ImageBrush(bitmap) { Stretch = Stretch.Fill };
        ink.Freeze();
        model = new GeometryModel3D(mesh, new DiffuseMaterial(ink));
        var outline = sheet.Child?.Clip ?? new RectangleGeometry(new Rect(size));
        var underside = new DrawingBrush(new GeometryDrawing(sheet.Fill, null, outline))
        {
            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(size), Opacity = sheet.PaperOpacity
        };
        model.BackMaterial = new DiffuseMaterial(underside);
        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(170, 170, 170)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(132, 132, 132), new Vector3D(.25, .65, -1)));
        scene.Children.Add(model);
        camera = new OrthographicCamera { LookDirection = new Vector3D(0, 0, -1), UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 1, FarPlaneDistance = 8192 };
        var viewport = new Viewport3D { Camera = camera, ClipToBounds = false };
        viewport.Children.Add(new ModelVisual3D { Content = scene });
        Child = viewport;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!castsShadow || mesh == null) return;
        var shadow = new StreamGeometry();
        using (var g = shadow.Open())
        {
            for (var i = 0; i <= Columns; i++)
            {
                var edge = bend.At(ActualWidth * i / Columns, ActualHeight);
                var point = new Point(edge.X, edge.Y - 1);
                if (i == 0) g.BeginFigure(point, true, true);
                else g.LineTo(point, true, false);
            }
            for (var i = Columns; i >= 0; i--)
            {
                var edge = bend.At(ActualWidth * i / Columns, ActualHeight);
                g.LineTo(new Point(edge.X + edge.Z * .055, edge.Y + edge.Z * .20), true, false);
            }
        }
        var left = bend.At(0, ActualHeight);
        var right = bend.At(ActualWidth, ActualHeight);
        var across = new Vector(left.Y - right.Y, right.X - left.X);
        if (across.LengthSquared < 1) return;
        across.Normalize();
        var middle = bend.At(ActualWidth * .5, ActualHeight);
        var edgeMiddle = new Point(middle.X, middle.Y);
        var light = new LinearGradientBrush(Color.FromArgb(54, 41, 32, 27), Color.FromArgb(0, 41, 32, 27),
            edgeMiddle, edgeMiddle + across * Math.Max(1, middle.Z * .20)) { MappingMode = BrushMappingMode.Absolute };
        dc.DrawGeometry(light, null, shadow);
    }

    internal void Clear()
    {
        if (mesh == null) return;
        Child = null;
        mesh = null; model = null; ink = null; camera = null;
        ready = false;
        Visibility = Visibility.Collapsed;
    }
}
