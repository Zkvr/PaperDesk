using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using PaperDesk.Desktop.Win32Interop;

namespace PaperDesk.Desktop.Controls;

// A narrow, input-transparent overlay lets the bundle fall beyond the note window.
internal sealed class PaperDropWindow : Window
{
    internal static void Launch(BitmapSource bundle, Size size, Point screenPoint, Matrix fromDevice, bool topmost)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y));
        var start = fromDevice.Transform(screenPoint);
        var bottomRight = fromDevice.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        var distance = bottomRight.Y - start.Y;
        if (distance <= 0) return;

        var gutter = 26 + Math.Max(size.Width, size.Height) * .23;
        var drift = start.X + size.Width + gutter < bottomRight.X ? 22 : -22;
        var window = new PaperDropWindow
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
            IsHitTestVisible = false, Topmost = topmost, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = start.X - gutter, Top = start.Y - gutter, Width = size.Width + gutter * 2,
            Height = distance + gutter
        };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            const int extendedStyle = -20;
            const int passThrough = 0x00000020, toolWindow = 0x00000080, noActivate = 0x08000000;
            Native.SetWindowLong(handle, extendedStyle, Native.GetWindowLong(handle, extendedStyle) | passThrough | toolWindow | noActivate);
        };

        var rotate = new RotateTransform();
        var slide = new TranslateTransform();
        var motion = new TransformGroup { Children = { rotate, slide } };
        var image = new Image { Source = bundle, Width = size.Width, Height = size.Height, RenderTransform = motion, RenderTransformOrigin = new Point(.5, .5) };
        Canvas.SetLeft(image, gutter);
        Canvas.SetTop(image, gutter);
        var runway = new Canvas { ClipToBounds = true };
        runway.Children.Add(image);
        window.Content = runway;

        var duration = TimeSpan.FromMilliseconds(Math.Clamp(285 + 10 * Math.Sqrt(distance), 365, 660));
        DoubleAnimation Fall(double end) => new(0, end, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var fall = Fall(distance + size.Height);
        fall.Completed += (_, _) => { if (window.IsVisible) window.Close(); };
        window.Show();
        rotate.BeginAnimation(RotateTransform.AngleProperty, Fall(drift > 0 ? 74 : -74));
        slide.BeginAnimation(TranslateTransform.XProperty, Fall(drift));
        slide.BeginAnimation(TranslateTransform.YProperty, fall);
    }
}
