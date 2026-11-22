using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Views;

static partial class UiChecks
{
    private static void CheckStickyFace(StickyWindow sticky, string folder)
    {
        var root = (FrameworkElement)sticky.Content;
        var sheet = (PaperSurface)sticky.FindName("Paper");
        var bent = (BentPaperView)sticky.FindName("BentFace");
        var close = (Button)sticky.FindName("HideButton");
        var closeOrigin = close.TranslatePoint(new Point(), root);
        var bounds = new Int32Rect((int)closeOrigin.X, (int)closeOrigin.Y, (int)close.ActualWidth, (int)close.ActualHeight);
        var faceOrigin = sheet.TranslatePoint(new Point(), root);
        var corner = new Int32Rect((int)faceOrigin.X, (int)faceOrigin.Y, 1, 1);
        // The production renderer removes the flat shadow before capturing the face.
        var shadow = sheet.Effect;
        sheet.Effect = null;
        try
        {
            var resting = Capture("sticky-flat");
            bent.Show(sheet, PaperPose.Flat with { Grip = new Point(.72, .96), Peel = .68 }, false);
            sticky.UpdateLayout();
            var lifted = Capture("sticky-lift");
            Require(PixelAlpha(resting, corner) < 60 && PixelAlpha(lifted, corner) < 60,
                "The upper corner of a rounded sticky must stay transparent when lifted");
            var before = Glyph(resting);
            var after = Glyph(lifted);
            Require(before.Count > 5 && before.Intersect(after).Count() > before.Count * .65,
                "The close icon must remain visible at the same pixels on the lifted sheet");
        }
        finally { bent.Clear(); sheet.Effect = shadow; }
        CheckLandingWindow(sticky, folder);
        Console.WriteLine("PASS the lifted sticky retains its rounded corner and toolbar position");

        RenderTargetBitmap Capture(string name)
        {
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
                (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name + ".png"));
            png.Save(output);
            return bitmap;
        }

        HashSet<int> Glyph(RenderTargetBitmap bitmap)
        {
            var pixels = new byte[bounds.Width * bounds.Height * 4];
            bitmap.CopyPixels(bounds, pixels, bounds.Width * 4, 0);
            return Enumerable.Range(0, pixels.Length / 4).Where(i => pixels[i * 4 + 3] > 180 && pixels[i * 4 + 2] < 145).ToHashSet();
        }
    }

    private static void CheckLandingWindow(StickyWindow owner, string folder)
    {
        var to = owner.PaperScreenBounds();
        var from = to;
        from.Offset(-68, 42);
        PaperSurface Face(string text, Brush color) => new() { Fill = color, Child = new TextBlock
            { Margin = new Thickness(20), Text = text, FontSize = 22, Foreground = Brushes.Black } };
        var overlay = StackLanding.Create(owner, from, to, PaperPose.Flat with { Grip = new Point(.8, .9), Peel = .8 },
            Face("Стопка", Brushes.LightBlue), Face("Список дел", Brushes.LemonChiffon));
        var stage = (Canvas)overlay.Content;
        var bent = stage.Children.OfType<BentPaperView>().Single();
        var frame = new System.Windows.Threading.DispatcherFrame();
        var limit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        limit.Tick += (_, _) => frame.Continue = false;
        overlay.Closed += (_, _) => frame.Continue = false;
        var startY = bent.RenderTransform.Value.OffsetY;
        var endY = Canvas.GetTop(stage.Children[0]);
        var captures = 0;
        void Observe(object? sender, EventArgs args)
        {
            var y = bent.RenderTransform.Value.OffsetY;
            if (!overlay.IsVisible || captures >= 3 || y > startY + (endY - startY) * captures * .45) return;
            stage.UpdateLayout();
            SaveSized(stage, Path.Combine(folder, $"stack-landing-{++captures}.png"));
        }
        try
        {
            overlay.Show();
            CompositionTarget.Rendering += Observe;
            limit.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Require(!overlay.IsVisible && captures == 3, "Stack landing must cross the release and contact positions, then remove its overlay");
        }
        finally
        {
            limit.Stop();
            CompositionTarget.Rendering -= Observe;
            if (overlay.IsVisible) overlay.Close();
        }
    }

    private static void CheckIncomingInk(Window overlay)
    {
        var stage = (Canvas)overlay.Content;
        overlay.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(stage.ActualWidth), (int)Math.Ceiling(stage.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(stage);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var pink = Enumerable.Range(0, pixels.Length / 4).Count(i =>
            pixels[4 * i + 3] > 220 && pixels[4 * i] > 135 && pixels[4 * i + 2] - pixels[4 * i + 1] > 25);
        Require(pink > 1000, "The incoming paper must stay visible over the stack while landing");
    }

    private static void CheckPaperBend(string folder, bool captureMotion = false)
    {
        Directory.CreateDirectory(folder);
        var size = new Size(320, 290);
        var grip = new Point(.82, .96);
        var pose = PaperPose.Flat with { Peel = .95, Grip = grip };
        var priorHeight = double.PositiveInfinity;
        foreach (var t in new[] { 0d, .2, .4, .6, .8, 1 })
        {
            var projected = new PaperBend(size, StackLanding.PoseAt(pose, t));
            var corner = projected.At(size.Width, size.Height);
            Require(projected.At(0, 0).Z == 0 && corner.Z <= priorHeight, "Landing must unroll towards contact without bouncing");
            priorHeight = corner.Z;
        }
        Require(priorHeight < .001, "The last landing frame must be flat");
        var shape = new PaperBend(size, pose);
        var left = shape.At(0, size.Height);
        var right = shape.At(size.Width, size.Height);
        Require(shape.At(0, 0).Z == 0 && shape.At(size.Width, 10).Z == 0 && right.Z > left.Z + 12,
            "Glue stays down while the free corner nearest the fingers rises first");
        var mirror = new PaperBend(size, pose with { Grip = new Point(1 - grip.X, grip.Y) });
        Require(Math.Abs(mirror.At(0, size.Height).Z - right.Z) < .01, "Left and right grips are mirror images");
        var finger = new PaperBend(size, pose with { FreeGrip = 1 }).At(grip.X * size.Width, grip.Y * size.Height);
        Require(Math.Abs(finger.X - grip.X * size.Width) + Math.Abs(finger.Y - grip.Y * size.Height) < .01,
            "A detached sheet must follow the fingertip in screen space");
        double length = 0;
        var previous = shape.At(160, 0);
        for (var y = 1; y <= size.Height; y++)
        {
            var next = shape.At(160, y);
            length += (next - previous).Length;
            previous = next;
        }
        Require(Math.Abs(length - size.Height) < .01, "Curling must preserve the material length of a sheet");
        var contact = new PaperContact(260, free: false);
        Require(contact.Move(new Vector(0, 60), out _) == default && !contact.Free &&
            contact.Move(new Vector(0, -59), out _) == default && !contact.Free, "Short tugs cannot translate the adhesive");
        Require(contact.Move(new Vector(0, -60), out _).Length < 1 && contact.Free,
            "Peeling back must release continuously, without jumping an entire grip length");
        var far = new Vector(0, -140);
        Require(contact.Move(far, out _) == far && contact.Move(new Vector(0, -20), out _) == new Vector(0, -20),
            "After the fingers take up slack, reversing must track directly without sticking again");

        var paper = new PaperSurface { Fill = Brushes.LemonChiffon, Corners = "rounded", ClipContentToPaper = true };
        var canvas = new Canvas();
        AddInk("Перед выходом", 23, 30, 27);
        AddInk("Забрать ключи", 24, 117, 19);
        AddInk("Позвонить в семь", 24, 172, 19);
        var marker = new Border { Width = 12, Height = 9, Background = Brushes.DarkBlue };
        Canvas.SetLeft(marker, 247); Canvas.SetTop(marker, 195); canvas.Children.Add(marker);
        paper.Child = canvas;
        var host = new Grid { Width = size.Width, Height = size.Height };
        var flat = new Grid();
        flat.Children.Add(paper);
        var bent = new BentPaperView();
        host.Children.Add(flat); host.Children.Add(bent);
        Layout(host);
        SaveRender(host, Path.Combine(folder, "sticker-rest.png"));
        foreach (var (name, amount) in new[] { ("lift", .48), ("peel", pose.Peel) })
        {
            var frame = pose with { Peel = amount };
            bent.Show(paper, frame, shadow: true);
            flat.Opacity = 0;
            Layout(host);
            var bitmap = Render(host);
            SaveRender(host, Path.Combine(folder, $"sticker-{name}.png"));
            var mark = new PaperBend(size, frame).At(253, 199);
            var pixels = new byte[4 * 7 * 7];
            bitmap.CopyPixels(new Int32Rect((int)mark.X - 3, (int)mark.Y - 3, 7, 7), pixels, 28, 0);
            Require(Enumerable.Range(0, 49).Any(i => pixels[i*4] > pixels[i*4+2] + 30 && pixels[i*4+3] > 200),
                "The ink must move to its projected position on the actual rendered face");
        }
        bent.Clear(); flat.Opacity = 1; Layout(host);
        Require(bent.Child == null && bent.Visibility == Visibility.Collapsed, "A resting sheet releases its 3D scene");
        Console.WriteLine("PASS pinned adhesive, mirrored peel, constant paper length, captured grip and rendered moving ink");
        if (captureMotion)
        {
            canvas.Children.Remove(marker);
            paper.Fill = new SolidColorBrush(Color.FromRgb(244, 229, 173));
            var frames = Directory.CreateDirectory(Path.Combine(folder, "motion")).FullName;
            var stage = new Grid { Width = 420, Height = 382, Background = new SolidColorBrush(Color.FromRgb(61, 68, 73)) };
            var nextSheet = new PaperSurface { Width = size.Width, Height = size.Height, Corners = "rounded",
                Fill = new SolidColorBrush(Color.FromRgb(214, 227, 203)),
                RenderTransform = new TranslateTransform(3, 4) };
            stage.Children.Add(nextSheet); stage.Children.Add(host);
            var displacement = new TranslateTransform(); host.RenderTransform = displacement;
            var motion = new PaperMotion(); motion.Hold(grip, true);
            var deskContact = new PaperContact(size.Height, false);
            Vector previousTravel = default;
            for (var i = 0; i < 64; i++)
            {
                var t = Math.Clamp((i - 5) / 33d, 0, 1);
                var travel = new Vector(-16, -85) * (t * t * (3 - 2 * t));
                if (i == 42) motion.LetGo();
                if (i < 42)
                {
                    var window = deskContact.Move(travel, out var fraction);
                    displacement.X = window.X; displacement.Y = window.Y;
                    motion.Pull((travel - previousTravel) * 30, fraction, travel.X / Math.Max(12, travel.Length));
                }
                motion.Advance(1d/30, 0);
                previousTravel = travel;
                if (motion.Pose.Peel > .002 || motion.Pose.Flex.Length > .002)
                {
                    bent.Show(paper, motion.Pose, true); flat.Opacity = 0;
                }
                else { bent.Clear(); flat.Opacity = 1; }
                Layout(stage);
                SaveRender(stage, Path.Combine(frames, $"frame-{i:D2}.png"));
            }
            bent.Clear();
        }

        void AddInk(string value, double x, double y, double fontSize)
        {
            var text = new TextBlock { Text = value, FontSize = fontSize, Foreground = Brushes.SaddleBrown,
                FontFamily = new FontFamily("Segoe Print") };
            Canvas.SetLeft(text, x); Canvas.SetTop(text, y); canvas.Children.Add(text);
        }
    }
}
