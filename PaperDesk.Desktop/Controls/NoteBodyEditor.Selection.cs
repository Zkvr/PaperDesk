using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;

namespace PaperDesk.Desktop.Controls;

public sealed partial class NoteBodyEditor
{
    private bool documentSelection;
    private int? mouseAnchor, keyboardAnchor;
    private int selectionCaret;
    private static readonly Brush SelectedInk = PaperPalette.Brush("#B7C8DD");

    private (int Start, int Length) CurrentSelection()
    {
        if (documentSelection && lastSelection is { } selected) return selected;
        if (blockEditor is { } field && blockPiece is { } piece)
        {
            var start = piece.Raw(field.SelectionStart);
            return (start, piece.Raw(field.SelectionStart + field.SelectionLength) - start);
        }
        return IsEditing ? (editor.SelectionStart, editor.SelectionLength) : lastSelection ?? (Text.Length, 0);
    }

    private void RestoreSelection()
    {
        if (lastSelection is not { } selection) return;
        BeginEdit(selection.Start);
        if (blockEditor is { } field && blockPiece is { } piece)
        {
            var end = selection.Start + selection.Length;
            if (selection.Start >= piece.Raw(0) && end <= piece.Raw(piece.Length))
            {
                var from = piece.Visible(selection.Start);
                field.Select(from, piece.Visible(end) - from);
            }
            else if (selection.Length > 0) SelectDocument(selection.Start, end);
        }
        else if (EditingPlainText) editor.Select(selection.Start, selection.Length);
    }

    internal void SelectDocument(int anchor, int caret)
    {
        lastSelection = (Math.Min(anchor, caret), Math.Abs(anchor - caret));
        selectionCaret = caret;
        documentSelection = anchor != caret;
        if (blockEditor is { } field && blockPiece is { } piece)
        {
            var from = piece.Visible(lastSelection.Value.Start);
            field.Select(from, piece.Visible(lastSelection.Value.Start + lastSelection.Value.Length) - from);
        }
        PaintSelection();
    }

    private void ClearSelection(bool afterMouseDown = false)
    {
        keyboardAnchor = null;
        if (!documentSelection) return;
        documentSelection = false;
        if (afterMouseDown) Dispatcher.BeginInvoke(PaintSelection);
        else PaintSelection();
    }

    private void PaintSelection()
    {
        foreach (var caption in EditableCaptions()) PaintCaption(caption);
    }

    private void PaintCaption(TextBlock caption)
    {
        if (caption.Tag is not RowCaret piece || piece.Length == 0) return;
        var (start, length) = documentSelection ? CurrentSelection() : (0, 0);
        var from = piece.Visible(start);
        var to = piece.Visible(start + length);
        caption.Inlines.Clear();
        piece.Inline.Write(caption, piece.Start, piece.Length, piece.Start + from, piece.Start + to, SelectedInk);
    }

    private void ConnectSelection()
    {
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            mouseAnchor = null;
            ClearSelection(afterMouseDown: true);
            pendingStyle = PaperInline.Style.Plain;
            if (blockEditor is { IsMouseOver: true } field && blockPiece is { } piece)
            {
                var at = field.CharacterAt(e.GetPosition(field));
                BeginMouseSelection(piece.Raw(Math.Clamp(at, 0, piece.Length)));
            }
        };
        PreviewMouseMove += (_, e) =>
        {
            if (mouseAnchor is not int anchor || e.LeftButton != MouseButtonState.Pressed ||
                blockEditor is not { } field || blockPiece is not { } active) return;
            var local = e.GetPosition(field);
            if (!documentSelection && ReferenceEquals(Mouse.Captured, field) &&
                local.Y >= 0 && local.Y <= field.ActualHeight) return;

            var point = e.GetPosition(lines);
            if (e.GetPosition(viewer).Y < 0) viewer.LineUp();
            else if (e.GetPosition(viewer).Y >= viewer.ActualHeight) viewer.LineDown();
            var hit = CaptionAt(point);
            if (hit == null && local.Y < 0) return;
            if (hit == null && local.Y > field.ActualHeight) return;
            var target = hit?.Tag as RowCaret ?? active;
            var x = hit == null ? local.X : e.GetPosition(hit).X;
            var caret = target.Raw(CreateTextLines(hit?.FontWeight).GetCaretOffset(target.DisplayText, x, target.Inline, target.Start));
            if (caret == anchor && !documentSelection) return;
            if (Mouse.Captured != viewer) viewer.CaptureMouse();
            SelectDocument(anchor, caret);
            e.Handled = true;
        };
        PreviewMouseLeftButtonUp += (_, _) =>
        {
            mouseAnchor = null;
            if (Mouse.Captured == viewer) viewer.ReleaseMouseCapture();
        };
        AddHandler(CommandManager.PreviewExecutedEvent, new ExecutedRoutedEventHandler((_, e) =>
        {
            if (IsEditing && (e.Command == EditingCommands.ToggleBold || e.Command == EditingCommands.ToggleItalic))
            {
                ApplyFormat(e.Command == EditingCommands.ToggleBold ? NoteFormat.Kind.Bold : NoteFormat.Kind.Italic, CurrentSelection());
                e.Handled = true;
            }
            else if (IsEditing && e.Command == EditingCommands.ToggleUnderline)
                e.Handled = true;
            else if (e.Command == ApplicationCommands.SelectAll && !EditingPlainText && IsKeyboardFocusWithin && Text.Length > 0)
            {
                if (!IsEditing) BeginEdit(0);
                if (EditingPlainText) editor.SelectAll();
                else SelectDocument(0, Text.Length);
                e.Handled = true;
            }
            else if (documentSelection && (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut))
            {
                var (start, length) = CurrentSelection();
                var plain = SelectedText(start, start + length);
                if (plain.Length > 0)
                {
                    try { Clipboard.SetText(plain); }
                    catch (System.Runtime.InteropServices.ExternalException) { e.Handled = true; return; }
                }
                if (e.Command == ApplicationCommands.Cut) ReplaceSelection("");
                e.Handled = true;
            }
            else if (documentSelection && e.Command == ApplicationCommands.Paste && Clipboard.ContainsText())
            {
                ReplaceSelection(Clipboard.GetText());
                e.Handled = true;
            }
        }));
    }

    private void BeginMouseSelection(int offset) => mouseAnchor = offset;

    private TextBlock? CaptionAt(Point point)
    {
        TextBlock? closest = null;
        var distance = double.PositiveInfinity;
        foreach (var caption in EditableCaptions())
        {
            var origin = caption.TranslatePoint(new Point(), lines);
            var dy = Math.Max(origin.Y - point.Y, point.Y - origin.Y - caption.ActualHeight);
            var dx = Math.Max(origin.X - point.X, point.X - origin.X - caption.ActualWidth);
            var delta = Math.Max(0, dy) * 1000 + Math.Max(0, dx);
            if (delta >= distance) continue;
            distance = delta;
            closest = caption;
        }
        if (blockEditor is { } field)
        {
            var origin = field.TranslatePoint(new Point(), lines);
            if (point.Y >= origin.Y && point.Y < origin.Y + field.ActualHeight &&
                (blockPiece?.Cell != true || point.X >= origin.X && point.X < origin.X + field.ActualWidth))
                return null;
        }
        return closest;
    }

    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (EditingPlainText) return false;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && Text.Length > 0)
        {
            if (!IsEditing) BeginEdit(0);
            if (EditingPlainText) editor.SelectAll();
            else SelectDocument(0, Text.Length);
        }
        else if (documentSelection && (e.Key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
            ReplaceSelection("");
        else if (blockEditor is { } field && blockPiece is { } active && e.Key is Key.Up or Key.Down &&
            Keyboard.Modifiers == ModifierKeys.Shift)
        {
            var y = field.TranslatePoint(new Point(), lines).Y;
            var current = documentSelection ? selectionCaret : active.Raw(field.CaretIndex);
            var direction = e.Key == Key.Up ? -1 : 1;
            var start = documentSelection ? current : active.Raw(0);
            var next = EditableCaptions().Select(c => (Row: (RowCaret)c.Tag, Y: c.TranslatePoint(new Point(), lines).Y))
                .Where(c => documentSelection ? direction * (c.Row.Raw(0) - start) > 0 : direction * (c.Y - y) > .5)
                .OrderBy(c => Math.Abs(c.Row.Raw(0) - start)).FirstOrDefault();
            if (next.Row == null) return false;
            keyboardAnchor ??= current;
            SelectDocument(keyboardAnchor.Value, next.Row.Raw(Math.Min(field.CaretIndex, next.Row.Length)));
            var target = EditableCaptions().FirstOrDefault(c => ReferenceEquals(c.Tag, next.Row));
            target?.BringIntoView();
        }
        else
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)
                ClearSelection();
            return false;
        }
        return e.Handled = true;
    }

    private void ReplaceSelection(string replacement)
    {
        var (start, length) = CurrentSelection();
        var edit = NoteFormat.ReplaceDocumentSelection(Text, start, length, replacement);
        EndEdit();
        ApplyDocumentEdit(edit);
        BeginEdit(lastSelection!.Value.Start);
    }

    private string SelectedText(int start, int end)
    {
        var pieces = EditableCaptions().Select(c => (RowCaret)c.Tag).ToList();
        if (blockPiece != null) pieces.Add(blockPiece);
        return string.Join(Environment.NewLine, pieces.OrderBy(p => p.Source).ThenBy(p => p.Start)
            .Where(p => p.Visible(start) < p.Visible(end))
            .GroupBy(p => NoteFormat.LineStart(Text, p.Source))
            .Select(line => string.Join("\t", line.GroupBy(p => p.Source).Select(part =>
                string.Concat(part.Select(p => p.DisplayText[p.Visible(start)..p.Visible(end)]))))));
    }
}
