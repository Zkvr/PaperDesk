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
    private Point press, grip;
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
        if (ItemsControl.ContainerFromElement(cards, source) is not ListBoxItem { Content: NoteCard note } item ||
            desk.Store.Find(note.Id)?.Note is not { ArchivedAt: null, DeletedAt: null }) return;
        card = note;
        sourceStore = desk.Store;
        press = e.GetPosition(owner);
        var local = e.GetPosition(item);
        grip = new Point(Math.Clamp(local.X / Math.Max(1, item.ActualWidth), 0, 1),
            Math.Clamp(local.Y / Math.Max(1, item.ActualHeight), 0, 1));
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
            preview = CreatePreview(desk, card.Id);
            var startTransform = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var start = StickyLayout.DesktopDropBounds(new Point(point.X, point.Y), startTransform, new Size(preview.Width, preview.Height), grip);
            preview.Left = start.Left;
            preview.Top = start.Top;
            preview.Show();
        }

        if (preview == null || card == null) return; // Showing a window may reenter a cancellation handler.
        var source = PresentationSource.FromVisual(preview);
        var composition = source?.CompositionTarget;
        if (composition == null) return;
        var bounds = StickyLayout.DesktopDropBounds(new Point(point.X, point.Y), composition.TransformFromDevice,
            new Size(preview.Width, preview.Height), grip);
        var pixel = composition.TransformToDevice.Transform(bounds.TopLeft);
        var handle = new WindowInteropHelper(preview).Handle;
        var x = (int)Math.Round(pixel.X);
        var y = (int)Math.Round(pixel.Y);
        if (!Native.GetWindowRect(handle, out var current) || current.Left != x || current.Top != y)
            Native.MoveWithoutActivate(handle, x, y);
        Highlight(desk.FindCardDropTarget(card.Id, point));
        var allowed = dropTarget != null || DesktopLayer.IsDesktopAt(point);
        if (allowed != canDrop) { canDrop = allowed; Mouse.UpdateCursor(); }
        e.Handled = true;
    }

    private void Finish(object sender, MouseButtonEventArgs e)
    {
        if (preview == null) { Cancel(); return; }
        e.Handled = true;
        var note = card;
        var store = sourceStore;
        var transform = PresentationSource.FromVisual(preview)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        try
        {
            if (note == null || selecting() || !ReferenceEquals(store, desk.Store) || !Native.GetCursorPos(out var point)) return;
            if (desk.FindCardDropTarget(note.Id, point) is { } target) desk.StackCard(note.Id, target);
            else if (DesktopLayer.IsDesktopAt(point))
                desk.PlaceOnDesktop(note.Id, new Point(point.X, point.Y), transform, grip);
        }
        finally { Cancel(); }
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
        canDrop = false;
        card = null;
        sourceStore = null;
        if (ghost == null) return;
        if (Mouse.Captured == cards) cards.ReleaseMouseCapture();
        ghost.Close();
        Mouse.UpdateCursor();
    }

    internal static StickyWindow CreatePreview(DeskController desk, Guid id)
    {
        var window = new StickyWindow(desk, id, animateOpening: false, cardPreview: true)
        {
            ShowActivated = false, Focusable = false, IsHitTestVisible = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true
        };
        window.SourceInitialized += (_, _) =>
        {
            const int style = -16, disabled = 0x08000000;
            const int extendedStyle = -20, passThrough = 0x20, toolWindow = 0x80, noActivate = 0x08000000;
            var handle = new WindowInteropHelper(window).Handle;
            Native.SetWindowLong(handle, style, Native.GetWindowLong(handle, style) | disabled);
            Native.SetWindowLong(handle, extendedStyle, Native.GetWindowLong(handle, extendedStyle) | passThrough | toolWindow | noActivate);
        };
        return window;
    }
}
