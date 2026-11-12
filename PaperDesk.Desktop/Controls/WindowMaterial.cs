using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Animation;

namespace PaperDesk.Desktop.Controls;

/// <summary>System material belongs to chrome, never to the paper or its text.</summary>
public static class WindowMaterial
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(WindowMaterial), new PropertyMetadata(false, Changed));
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not Window window) return;
        window.SourceInitialized -= Apply;
        if ((bool)args.NewValue) { window.SourceInitialized += Apply; if (new WindowInteropHelper(window).Handle != 0) Apply(window, EventArgs.Empty); }
    }
    private static void Apply(object? sender, EventArgs args)
    {
        var window = (Window)sender!; if (window.AllowsTransparency || !GetEnabled(window)) return;
        var handle = new WindowInteropHelper(window).Handle;
        var dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var round = 2; DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        var transparency = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 1);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast && !Equals(transparency, 0))
        {
            var material = 3; // DWMSBT_TRANSIENTWINDOW: system Desktop Acrylic.
            if (DwmSetWindowAttribute(handle, 38, ref material, sizeof(int)) == 0)
            {
                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(handle, ref margins);
                if (PresentationSource.FromVisual(window) is HwndSource source) source.CompositionTarget.BackgroundColor = Colors.Transparent;
                return;
            }
        }
        window.SetResourceReference(Control.BackgroundProperty, "DeskWindowBackground");
    }
    public static void Reveal(FrameworkElement surface, bool enabled)
    {
        if (!enabled || !SystemParameters.ClientAreaAnimation) return;
        surface.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.3, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop
        });
    }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint handle, ref Margins margins);
}
