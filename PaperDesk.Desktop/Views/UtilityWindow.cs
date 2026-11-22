using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Shell;

namespace PaperDesk.Desktop.Views;

internal class UtilityWindow : Window
{
    protected UtilityWindow(string title, Window? owner, double width, double height)
    {
        Title = title;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Owner = owner;
        Width = width;
        Height = height;
        MinWidth = 440;
        MinHeight = 315;
        WindowStyle = WindowStyle.None;
        ShowInTaskbar = owner == null;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "DeskFrame");
        SetResourceReference(ForegroundProperty, "Ink");
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0)
        });

        var layout = new DockPanel();
        var titleBar = new DockPanel { Background = Brushes.Transparent, LastChildFill = true, Margin = new Thickness(14, 7, 7, 4) };
        var close = new Button { Content = "\uE8BB", Style = (Style)FindResource("WindowButton") };
        AutomationProperties.SetName(close, L.T("Закрыть"));
        close.ToolTip = L.T("Закрыть");
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        var heading = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(10, 0, 0, 0) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        titleBar.Children.Add(heading);
        DockPanel.SetDock(titleBar, Dock.Top);
        layout.Children.Add(titleBar);
        Body = new DockPanel();
        layout.Children.Add(Body);
        var frame = new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(48, 255, 255, 255)), Child = layout };
        Content = frame;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    protected DockPanel Body { get; }

    protected void AddSections(params (string Label, Panel Page)[] pages)
    {
        var rail = new StackPanel { Width = 147, Margin = new Thickness(0, 1, 0, 0) };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pages[0].Page };
        var surface = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 16, 12, 16), Child = scroll };
        surface.SetResourceReference(Border.BackgroundProperty, "DeskPanel");
        var pane = new DockPanel { Margin = new Thickness(18, 6, 18, 16) };
        DockPanel.SetDock(rail, Dock.Left);
        pane.Children.Add(rail);
        pane.Children.Add(surface);
        Body.Children.Add(pane);
        foreach (var (label, page) in pages)
        {
            var tab = new RadioButton { Content = label, Style = (Style)FindResource("NavItem"), IsChecked = page == pages[0].Page };
            tab.Checked += (_, _) =>
            {
                scroll.Content = page;
                scroll.ScrollToTop();
            };
            rail.Children.Add(tab);
        }
    }

    protected static StackPanel Section(string title)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        return panel;
    }

    protected static TextBlock Secondary(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
        return block;
    }
}
