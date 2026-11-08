using System.Windows.Input;
using System.Windows.Interop;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.ViewModels;
using PaperDesk.Desktop.Views;
using PaperDesk.Desktop.Win32Interop;

namespace PaperDesk.Desktop.Controls;

internal sealed class DesktopCardDrag
{
    private readonly Window owner;
    private readonly ListBox cards;
    private readonly DeskController desk;
    private readonly Func<bool> selecting;
    private NoteCard? card;
    private NoteStore? sourceStore;
    private Point press;
    private Window? preview;
    private StickyWindow? dropTarget;
    private bool canDrop;

    internal DesktopCardDrag(Window owner, ListBox cards, DeskController desk, Func<bool> selecting)
    {
        this.owner = owner;
        this.cards = cards;
        this.desk = desk;
        this.selecting = selecting;
        cards.PreviewMouseLeftButtonDown += Begin;
        owner.PreviewMouseMove += Move;
        owner.PreviewMouseLeftButtonUp += Finish;
        owner.Deactivated += (_, _) => Cancel();
        owner.Closed += (_, _) => Cancel();
        cards.LostMouseCapture += (_, _) => { if (preview != null && Mouse.Captured != cards) Cancel(); };
        cards.QueryCursor += (_, e) =>
        {
            if (preview == null) return;
            e.Cursor = canDrop ? Cursors.SizeAll : Cursors.No;
            e.Handled = true;
        };
        owner.PreviewKeyDown += (_, e) =>
        {
            if (card == null || e.Key != Key.Escape) return;
            e.Handled = preview != null;
            Cancel();
        };
    }

    private void Begin(object sender, MouseButtonEventArgs e)
    {
        if (selecting() || Keyboard.Modifiers != ModifierKeys.None || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(cards, source) is not ListBoxItem { Content: NoteCard note } ||
            desk.Store.Find(note.Id)?.Note is not { ArchivedAt: null, DeletedAt: null }) return;
        card = note;
        sourceStore = desk.Store;
        press = e.GetPosition(owner);
    }

    private void Move(object sender, MouseEventArgs e)
    {
        if (card == null) return;
        if (e.LeftButton != MouseButtonState.Pressed || selecting() || !ReferenceEquals(sourceStore, desk.Store))
        {
            Cancel();
            return;
        }
        if (!Native.GetCursorPos(out var point)) return;
        if (preview == null)
        {
            var delta = e.GetPosition(owner) - press;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!cards.CaptureMouse()) { Cancel(); return; }
            preview = CreatePreview(card);
            preview.Show();
        }

        var source = PresentationSource.FromVisual(preview);
        var offset = source?.CompositionTarget?.TransformToDevice.Transform(new Vector(10, 12)) ?? new Vector(10, 12);
        Native.MoveWithoutActivate(new WindowInteropHelper(preview).Handle,
            point.X + (int)Math.Round(offset.X), point.Y + (int)Math.Round(offset.Y));
        Highlight(desk.FindCardDropTarget(card.Id, point));
        canDrop = dropTarget != null || DesktopLayer.IsDesktopAt(point);
        preview.Opacity = canDrop ? 1 : .72;
        Mouse.UpdateCursor();
        e.Handled = true;
    }

    private void Finish(object sender, MouseButtonEventArgs e)
    {
        if (preview == null) { Cancel(); return; }
        e.Handled = true;
        var note = card;
        var store = sourceStore;
        var transform = PresentationSource.FromVisual(preview)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Cancel();
        if (note == null || selecting() || !ReferenceEquals(store, desk.Store) || !Native.GetCursorPos(out var point)) return;
        if (desk.FindCardDropTarget(note.Id, point) is { } target) desk.StackCard(note.Id, target);
        else if (DesktopLayer.IsDesktopAt(point))
            desk.PlaceOnDesktop(note.Id, new Point(point.X, point.Y), transform);
    }

    private void Highlight(StickyWindow? target)
    {
        if (target == dropTarget) return;
        dropTarget?.HighlightDrop(false);
        dropTarget = target;
        dropTarget?.HighlightDrop(true);
    }

    internal void Cancel()
    {
        Highlight(null);
        var ghost = preview;
        preview = null;
        card = null;
        sourceStore = null;
        if (ghost == null) return;
        if (Mouse.Captured == cards) cards.ReleaseMouseCapture();
        ghost.Close();
        Mouse.UpdateCursor();
    }

    private static Window CreatePreview(NoteCard note)
    {
        var text = new StackPanel { Margin = new Thickness(12, 9, 13, 12), ClipToBounds = true };
        text.Children.Add(new TextBlock
        {
            Text = note.Title, FontFamily = new FontFamily("Segoe Print"), FontWeight = FontWeights.SemiBold,
            FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 5)
        });
        text.Children.Add(new TextBlock { Text = note.Preview, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Focusable = false,
            IsHitTestVisible = false, Topmost = true, Width = 177, Height = 119, Foreground = PaperPalette.Brush("#302D29"),
            Content = new PaperSurface { Fill = PaperPalette.Brush(note.Color), Child = text }
        };
        window.SourceInitialized += (_, _) =>
        {
            const int extendedStyle = -20, passThrough = 0x20, toolWindow = 0x80, noActivate = 0x08000000;
            var handle = new WindowInteropHelper(window).Handle;
            Native.SetWindowLong(handle, extendedStyle, Native.GetWindowLong(handle, extendedStyle) | passThrough | toolWindow | noActivate);
        };
        return window;
    }
}
