using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.TextFormatting;

namespace PaperDesk.Desktop.Controls;

/// <summary>A line-scrolling note with clickable tasks and an inline text editor.</summary>
public sealed partial class NoteBodyEditor : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NoteBodyEditor), new FrameworkPropertyMetadata(
            "", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TextUpdated));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public event TextChangedEventHandler? TextChanged;
    internal event EventHandler? IsEditingChanged;
    private static readonly DependencyPropertyKey IsEditingPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsEditing), typeof(bool), typeof(NoteBodyEditor), new PropertyMetadata(false));
    public static readonly DependencyProperty IsEditingProperty = IsEditingPropertyKey.DependencyProperty;
    public bool IsEditing => (bool)GetValue(IsEditingProperty);
    public int CaretIndex
    {
        get => blockEditor is { } input && blockPiece is { } piece ? piece.Raw(input.CaretIndex) : editor.CaretIndex;
        set { if (blockEditor is { } input && blockPiece is { } piece) input.CaretIndex = piece.Visible(value); else editor.CaretIndex = value; }
    }
    internal bool EditingPlainText => !IsWebActive && IsEditing && blockEditor == null;

    private readonly TextBox editor;
    private readonly WholeLinePanel lines;
    private readonly ScrollViewer viewer;
    private bool updating, toggling;
    private double formattedWidth = -1;
    private string renderedText = "";
    private int? resumeAt;
    private (int Start, int Length)? lastSelection;
    private readonly HashSet<int> openSpoilers = new();
    private ContextMenu? formatMenu;
    private Window? owner;
    private NotePictures? pictures;
    private TextWrapRegion? wrap;
    private PaperLineInput? blockEditor;
    private TextBox? titleBox;
    private TextBlock? blockCaption;
    private RowCaret? blockPiece;
    private PaperInline.Style pendingStyle;
    private bool writingBlock, switchingEditor, applyingEdit;

    public NoteBodyEditor()
    {
        Focusable = true;
        Foreground = PaperPalette.Brush("#302D29");
        Resources["ScrollThumbBrush"] = new SolidColorBrush(Color.FromArgb(100,109,96,70));
        editor = new TextBox
        {
            Style = (Style)Application.Current.FindResource("PaperEditor"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed, Padding = new Thickness(1,2,1,5)
        };
        lines = new WholeLinePanel(Reflow);
        viewer = new ScrollViewer
        {
            Content = lines, CanContentScroll = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
            Background = Brushes.Transparent, Padding = new Thickness(1,1,4,1)
        };
        viewer.ScrollChanged += (_, _) => pictures?.RefreshOverlays();
        var root = new Grid();
        root.Children.Add(viewer);
        root.Children.Add(editor);
        Content = root;
        SizeChanged += (_, e) => { if (e.HeightChanged && pictures?.TextFrames.Any() == true) InvalidateLines(); };

        editor.TextChanged += (_,_) =>
        {
            if (updating) return;
            SetCurrentValue(TextProperty, editor.Text);
            if (!EditingPlainText || switchingEditor || !HasVisualBlocks(Text)) return;
            switchingEditor = true;
            Dispatcher.BeginInvoke(() =>
            {
                switchingEditor = false;
                if (!EditingPlainText || !HasVisualBlocks(Text)) return;
                var caret = editor.CaretIndex;
                EndEdit();
                BeginEdit(caret);
            });
        };
        viewer.MouseLeftButtonDown += EditOnClick;
        ConnectSelection();
        PreviewKeyDown += (_, e) =>
        {
            if (HandleSelectionKey(e)) return;
            if (e.Key == Key.F2) { BeginEdit(); e.Handled = true; }
            else if (IsEditing && (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)))
            {
                EndEdit();
                Focus();
                e.Handled = true;
            }
            else if (blockEditor is { SelectionLength: 0, CaretIndex: 0 } && e.Key == Key.Back &&
                blockPiece is { Cell: false } piece && piece.Start == 0 && NoteFormat.RemoveListMarker(Text, piece.Source) is { } remove)
            {
                EndEdit();
                ApplyDocumentEdit(remove);
                BeginEdit(lastSelection!.Value.Start);
                e.Handled = true;
            }
            else if (blockEditor is { SelectionLength: 0, CaretIndex: 0 } && e.Key == Key.Back &&
                blockPiece?.Spoiler is int spoiler && NoteFormat.UnwrapSpoiler(Text, spoiler) is { } unwrap)
            {
                EndEdit();
                ApplyDocumentEdit(unwrap);
                BeginEdit(lastSelection!.Value.Start);
                e.Handled = true;
            }
            else if (blockEditor is { } field && blockPiece is { } active && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                InsertLine(active, field);
                e.Handled = true;
            }
            else if (blockEditor != null && e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift && titleBox != null)
            {
                EndEdit();
                titleBox.Focus();
                e.Handled = true;
            }
            else if (IsEditing && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None &&
                editor.SelectionLength == 0 && NoteFormat.ContinueList(Text, editor.CaretIndex) is { } next)
            {
                ApplyEdit(next);
                e.Handled = true;
            }
        };
        PreviewTextInput += (_, e) =>
        {
            if (documentSelection && !string.IsNullOrEmpty(e.Text))
            {
                ReplaceSelection(e.Text);
                e.Handled = true;
                return;
            }
            if (pendingStyle != PaperInline.Style.Plain && !string.IsNullOrEmpty(e.Text))
            {
                var (start, length) = CurrentSelection();
                var styled = PaperInline.Decorate(e.Text, pendingStyle);
                pendingStyle = PaperInline.Style.Plain;
                var visibleEnd = new PaperInline(styled).EndOffset(e.Text.Length);
                EndEdit();
                ApplyDocumentEdit(new NoteFormat.Edit(start, length, styled, visibleEnd));
                BeginEdit(lastSelection!.Value.Start);
                e.Handled = true;
                return;
            }
            if (IsEditing || !IsKeyboardFocused || string.IsNullOrEmpty(e.Text)) return;
            var selection = lastSelection ?? (Text.Length, 0);
            BeginEdit(selection.Item1);
            if (blockEditor is { } field) field.SelectedText = e.Text;
            else if (EditingPlainText)
            {
                editor.Select(Math.Min(selection.Item1, Text.Length), Math.Min(selection.Item2, Math.Max(0, Text.Length - selection.Item1)));
                editor.SelectedText = e.Text;
            }
            e.Handled = true;
        };
        IsKeyboardFocusWithinChanged += (_, _) => { if (!IsWebActive && !IsKeyboardFocusWithin && formatMenu?.IsOpen != true) EndEdit(); };
        Loaded += (_, _) =>
        {
            owner = Window.GetWindow(this);
            if (owner != null) owner.Deactivated += OwnerDeactivated;
        };
        Unloaded += (_, _) =>
        {
            if (owner != null) owner.Deactivated -= OwnerDeactivated;
            owner = null;
            EndEdit();
        };
        ToolTip = L.T("Щёлкните по тексту, чтобы писать. Щёлкните по квадратику, чтобы отметить пункт.");
    }

    private void InsertLine(RowCaret piece, PaperLineInput field)
    {
        if (documentSelection)
        {
            ReplaceSelection(NoteFormat.Newline(Text));
            return;
        }
        if (piece.Table is { } table)
        {
            var next = EditableCaptions().Select(c => (RowCaret)c.Tag)
                .FirstOrDefault(p => p.Table is { } cell && cell.Header == table.Header &&
                    (cell.Row > table.Row || cell.Row == table.Row && cell.Column > table.Column));
            var rowEnd = NoteFormat.LineEnd(Text, table.Row);
            var target = next == null ? NoteFormat.AfterLine(Text, rowEnd) : RebaseOffset(renderedText, next.Raw(0));
            EndEdit();
            if (next == null && rowEnd == Text.Length)
            {
                var lineBreak = NoteFormat.Newline(Text);
                ApplyDocumentEdit(new NoteFormat.Edit(rowEnd, 0, lineBreak, lineBreak.Length));
                target += lineBreak.Length;
            }
            BeginEdit(target);
            return;
        }
        if (piece.Spoiler is int spoiler)
        {
            EditSpoilerBody(spoiler);
            return;
        }
        var (start, length) = CurrentSelection();
        var visibleStart = piece.Start + field.SelectionStart;
        var atEnd = length == 0 && visibleStart == piece.Inline.Text.Length;
        if (atEnd) start = piece.Source + piece.Inline.RawLength;
        var change = length == 0 ? NoteFormat.ContinueList(Text, start) : null;
        var newline = NoteFormat.Newline(Text);
        if (change == null && !atEnd && piece.Inline.HasMarkup)
        {
            var replacement = piece.Inline.Replace(visibleStart, field.SelectionLength, newline);
            change = new NoteFormat.Edit(piece.Source, piece.Inline.RawLength, replacement, replacement.IndexOf('\n') + 1);
        }
        EndEdit();
        ApplyDocumentEdit(change ?? new NoteFormat.Edit(start, length, newline, newline.Length));
        BeginEdit(lastSelection!.Value.Start);
    }

    private void EditSpoilerBody(int spoiler)
    {
        EndEdit();
        var headerEnd = NoteFormat.LineEnd(Text, spoiler);
        var bodyStart = NoteFormat.AfterLine(Text, headerEnd);
        var missingLine = bodyStart == headerEnd;
        if (missingLine || NoteSpoilers.Closes(Text[bodyStart..NoteFormat.LineEnd(Text, bodyStart)]))
        {
            var newline = NoteFormat.Newline(Text);
            ApplyDocumentEdit(new NoteFormat.Edit(bodyStart, 0, newline, missingLine ? newline.Length : 0));
            bodyStart = lastSelection!.Value.Start;
        }
        openSpoilers.Add(spoiler);
        InvalidateLines();
        BeginEdit(bodyStart);
    }

    private void OwnerDeactivated(object? sender, EventArgs e) { if (formatMenu?.IsOpen != true) EndEdit(); }

    internal void ConnectTitle(TextBox title)
    {
        titleBox = title;
        title.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None || !IsEnabled) return;
            BeginEdit(0);
            e.Handled = true;
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.Shift) return;
            title.Focus();
            e.Handled = true;
        };
    }

    private void ApplyFormat(NoteFormat.Kind kind, (int Start, int Length) selection)
    {
        var start = Math.Clamp(selection.Start, 0, Text.Length);
        var length = Math.Clamp(selection.Length, 0, Text.Length - start);
        EndEdit();
        var style = NoteFormat.InlineStyle(kind);
        if (length == 0 && style != PaperInline.Style.Plain)
        {
            pendingStyle ^= style;
            BeginEdit(start);
            return;
        }
        pendingStyle = PaperInline.Style.Plain;
        ApplyDocumentEdit(NoteFormat.Apply(Text, start, length, kind));
        RestoreSelection();
    }

    private void ApplyEdit(NoteFormat.Edit change)
    {
        editor.BeginChange();
        try
        {
            editor.Select(change.Start, change.Length);
            editor.SelectedText = change.Text;
            editor.Select(change.Start + change.SelectStart, change.SelectLength);
        }
        finally { editor.EndChange(); }
    }

    private void ApplyDocumentEdit(NoteFormat.Edit change)
    {
        applyingEdit = true;
        try { SetCurrentValue(TextProperty, Text[..change.Start] + change.Text + Text[(change.Start + change.Length)..]); }
        finally { applyingEdit = false; }
        if (change.OpenSpoilerAt is int spoiler) openSpoilers.Add(spoiler);
        documentSelection = false;
        lastTable = null;
        lastSelection = (change.Start + change.SelectStart, change.SelectLength);
        resumeAt = lastSelection.Value.Start;
        InvalidateLines();
    }

    private void EditOnClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element != null && element != viewer;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or ScrollBar or Thumb or TextBoxBase) return;
            if (element is Border { Child: PaperLineInput field } && ReferenceEquals(field, blockEditor))
            {
                field.Focus();
                e.Handled = true;
                return;
            }
            var target = element as TextBlock ?? (element as Border)?.Child as TextBlock;
            if (target?.Tag is RowCaret piece && UseVisualEditing())
            {
                var clickedAt = CreateTextLines(target.FontWeight).GetCaretOffset(piece.DisplayText, e.GetPosition(target).X, piece.Inline, piece.Start);
                var offsetAtClick = RebaseOffset(renderedText, piece.Raw(clickedAt));
                if (renderedText == Text) BeginBlock(target, piece, clickedAt);
                else
                {
                    EndEdit();
                    BeginEdit(offsetAtClick);
                }
                BeginMouseSelection(offsetAtClick);
                e.Handled = true;
                return;
            }
            if (element is Grid { Tag: int offset } row && row.Children.Count > 0)
            {
                if (row.Children[0] is TextBlock caption)
                {
                    if (caption.Tag is RowCaret caret)
                    {
                        var at = CreateTextLines().GetCaretOffset(caret.Inline.Text.Substring(caret.Start, caret.Length), e.GetPosition(caption).X);
                        offset = caret.Source + caret.Inline.SourceOffset(caret.Start + at);
                    }
                    else offset += CreateTextLines().GetCaretOffset(caption.Text, e.GetPosition(caption).X);
                }
                var rebased = RebaseOffset(renderedText, offset);
                EndEdit();
                BeginEdit(rebased);
                e.Handled = true;
                return;
            }
        }
        BeginEdit();
        e.Handled = true;
    }

    private int RebaseOffset(string previous, int offset)
    {
        if (previous == Text) return offset;
        var prefix = 0;
        var current = Text;
        while (prefix < previous.Length && prefix < current.Length && previous[prefix] == current[prefix]) prefix++;
        if (offset < prefix) return offset;
        var oldEnd = previous.Length;
        var newEnd = current.Length;
        while (oldEnd > prefix && newEnd > prefix && previous[oldEnd - 1] == current[newEnd - 1]) { oldEnd--; newEnd--; }
        return offset >= oldEnd ? offset + newEnd - oldEnd : prefix;
    }

    private static void TextUpdated(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        var self = (NoteBodyEditor)obj;
        if (self.editor == null) return;
        self.QueueWebSync();
        if (self.blockEditor != null && !self.writingBlock) self.EndEdit();
        self.updating = true;
        try
        {
            var text = (string)args.NewValue;
            if (self.editor.Text != text)
            {
                var caret = self.editor.CaretIndex;
                self.editor.Text = text;
                self.editor.CaretIndex = Math.Min(caret, text.Length);
            }
        }
        finally { self.updating = false; }

        if (self.writingBlock || self.applyingEdit)
            self.UpdateSpoilers((string)args.OldValue, (string)args.NewValue);
        else if (!self.IsEditing && !self.toggling)
        {
            self.lastSelection = null;
            self.lastTable = null;
            self.documentSelection = false;
            self.pendingStyle = PaperInline.Style.Plain;
            self.openSpoilers.Clear();
            self.InvalidateLines();
            self.viewer.ScrollToHome();
        }
        self.TextChanged?.Invoke(self, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }

    private void UpdateSpoilers(string previous, string current)
    {
        if (openSpoilers.Count == 0) return;
        var start = 0;
        while (start < previous.Length && start < current.Length && previous[start] == current[start]) start++;
        var oldEnd = previous.Length;
        var newEnd = current.Length;
        while (oldEnd > start && newEnd > start && previous[oldEnd - 1] == current[newEnd - 1]) { oldEnd--; newEnd--; }
        var offsets = openSpoilers.ToArray();
        openSpoilers.Clear();
        foreach (var offset in offsets)
        {
            var next = offset >= oldEnd ? offset + newEnd - oldEnd : offset;
            if (IsSpoilerAt(next)) openSpoilers.Add(next);
            else if (IsSpoilerAt(offset)) openSpoilers.Add(offset);
        }

        bool IsSpoilerAt(int index) => index >= 0 && index < current.Length && (index == 0 || current[index - 1] == '\n') &&
            NoteSpoilers.Label(current[index..NoteFormat.LineEnd(current, index)]) != null;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (editor == null || (e.Property != FontSizeProperty && e.Property != FontFamilyProperty)) return;
        editor.FontFamily = FontFamily;
        editor.FontSize = FontSize;
        InvalidateLines();
    }

    private void InvalidateLines()
    {
        formattedWidth = -1;
        lines.InvalidateMeasure();
    }

    internal void ConnectPictures(NotePictures layer) => pictures = layer;
    internal void ReflowPictures()
    {
        QueueWebSync();
        if (!IsWebActive) InvalidateLines();
    }

    internal (double Offset, double Height) PictureViewport()
    {
        if (lines.Children.Count == 0) return (0, ActualHeight);
        var first = VisualTreeHelper.GetOffset(lines.Children[0]).Y;
        var last = lines.Children[lines.Children.Count - 1];
        var bottom = VisualTreeHelper.GetOffset(last).Y + last.RenderSize.Height;
        return (Math.Max(0, -first), Math.Max(ActualHeight, bottom - first));
    }

    internal int PictureAnchor(bool useCaret = true) => Text.Length == 0 ? 0 :
        Text.AsSpan(0, useCaret && IsEditing ? Math.Clamp(CaretIndex, 0, Text.Length) : Text.Length).Count('\n') + 1;

    internal bool ScrollOverPicture(int delta)
    {
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta) { RoutedEvent = MouseWheelEvent };
        viewer.RaiseEvent(wheel);
        return wheel.Handled;
    }

    internal int ParagraphAt(Point point)
    {
        if (point.Y < 0 || Text.Length == 0) return 0;
        if (EditingPlainText) return Text.AsSpan(0, Math.Min(Text.Length, editor.CaretIndex)).Count('\n');
        for (var hit = InputHitTest(point) as DependencyObject; hit != null && hit != this;
             hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit))
            if (hit is Grid { Tag: int offset })
                return Text.AsSpan(0, Math.Clamp(offset, 0, Text.Length)).Count('\n');
        return Text.Count(c => c == '\n') + 1;
    }

    private sealed class TextWrapRegion(StackPanel rows, double width, int count)
    {
        internal StackPanel Rows { get; } = rows;
        internal double Width { get; } = width;
        internal int Remaining { get; set; } = count;
    }

    private void AddTextRow(FrameworkElement row)
    {
        if (wrap == null) { lines.Children.Add(row); return; }
        wrap.Rows.Children.Add(row);
        if (--wrap.Remaining == 0) wrap = null;
    }

    private void AddFlowImage(Border frame, NoteImageLayout layout, double ratio, double width, int offset)
    {
        if (frame.Parent is Panel previous) previous.Children.Remove(frame);
        wrap = null;
        var beside = layout.Mode == ImageTextMode.Wrap && width >= 110;
        var maximumWidth = beside ? Math.Min(width - 64, width * .67) : width;
        var maximumHeight = Math.Max(lines.LineHeight, ActualHeight - lines.LineHeight * 2);
        var imageWidth = Math.Min(Math.Min(maximumWidth, width * layout.Width), maximumHeight * ratio);
        frame.Width = Math.Max(1, imageWidth);
        frame.Height = frame.Width / ratio;
        frame.VerticalAlignment = VerticalAlignment.Top;
        var rowCount = Math.Max(1, (int)Math.Ceiling((frame.Height + 7) / lines.LineHeight));
        var section = new Grid { Height = rowCount * lines.LineHeight, Tag = offset };
        if (beside)
        {
            const double gap = 8;
            var imageOnRight = layout.X >= .5;
            var pictureColumn = imageOnRight ? 1 : 0;
            var textWidth = Math.Max(1, width - frame.Width - gap);
            var pictureWidth = new GridLength(frame.Width + gap);
            section.ColumnDefinitions.Add(new ColumnDefinition { Width = imageOnRight ? new GridLength(1, GridUnitType.Star) : pictureWidth });
            section.ColumnDefinitions.Add(new ColumnDefinition { Width = imageOnRight ? pictureWidth : new GridLength(1, GridUnitType.Star) });
            frame.HorizontalAlignment = imageOnRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Grid.SetColumn(frame, pictureColumn);
            var rows = new StackPanel();
            Grid.SetColumn(rows, 1 - pictureColumn);
            section.Children.Add(rows);
            wrap = new TextWrapRegion(rows, textWidth, rowCount);
        }
        else
        {
            frame.HorizontalAlignment = HorizontalAlignment.Center;
        }
        section.Children.Add(frame);
        lines.Children.Add(section);
    }

    private PaperTextLines CreateTextLines(FontWeight? weight = null) => new(
        new Typeface(FontFamily, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip, Foreground);

    private sealed record RowCaret(int Source, int Start, int Length, PaperInline Inline, bool Cell = false, string EmptyPrefix = "")
    {
        internal TableLocation? Table { get; init; }
        internal int? Spoiler { get; init; }
        internal string DisplayText => Inline.Text.Substring(Start, Length);
        internal int Raw(int offset) => Source + (offset == Length && Length > 0 ? Inline.EndOffset(Start + offset) : Inline.SourceOffset(Start + offset));
        internal int Visible(int raw) => Math.Clamp(Inline.VisibleOffset(raw - Source) - Start, 0, Length);
    }

    private static bool HasVisualBlocks(string text) =>
        Checklist.Read(text).Count > 0 || new PaperInline(text).HasMarkup || text.Split('\n').Any(line =>
            line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("> ", StringComparison.Ordinal) || NoteFormat.NumberPrefix(line) > 0 || NoteSpoilers.Label(line) != null || TableCells(line) != null);

    private bool UseVisualEditing() => pictures?.TextFrames.Any() == true || HasVisualBlocks(Text);

    private IEnumerable<TextBlock> EditableCaptions() => Captions(lines);

    private static IEnumerable<TextBlock> Captions(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock { Tag: RowCaret } caption) yield return caption;
            foreach (var nested in Captions(child)) yield return nested;
        }
    }

    private void BeginBlock(TextBlock caption, RowCaret piece, int caret)
    {
        if (blockEditor != null) EndEdit();
        if (EditingPlainText) EndEdit();
        if (caption.Parent is not (Panel or Border)) return;
        var field = new PaperLineInput
        {
            FontFamily = caption.FontFamily, FontSize = caption.FontSize,
            FontWeight = caption.FontWeight, Foreground = caption.Foreground,
            CaretBrush = Foreground, Margin = caption.Margin, VerticalContentAlignment = VerticalAlignment.Center,
            ContextMenu = caption.ContextMenu
        };
        field.SetRuns(piece.Inline, piece.Start, piece.Length);
        blockEditor = field;
        blockCaption = caption;
        blockPiece = piece;
        SetValue(IsEditingPropertyKey, true);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
        SwapCaption(caption, field);
        field.TextChanged += (_, _) =>
        {
            if (blockEditor != field || writingBlock || blockPiece is not { } current) return;
            var before = current.DisplayText;
            var after = field.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (current.Cell) after = after.Replace('|', '¦');
            var prefix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            var beforeEnd = before.Length;
            var afterEnd = after.Length;
            while (beforeEnd > prefix && afterEnd > prefix && before[beforeEnd - 1] == after[afterEnd - 1])
            { beforeEnd--; afterEnd--; }
            var insertion = after[prefix..afterEnd];
            var changed = current.Inline.Replace(current.Start + prefix, beforeEnd - prefix, insertion);
            var inline = new PaperInline(changed);
            writingBlock = true;
            try
            {
                SetCurrentValue(TextProperty, Text[..current.Source] + current.EmptyPrefix + changed + Text[(current.Source + current.Inline.RawLength)..]);
                var visible = Math.Clamp(before.Length + inline.Text.Length - current.Inline.Text.Length, 0, inline.Text.Length - current.Start);
                blockPiece = current with { Source = current.Source + current.EmptyPrefix.Length, Inline = inline, Length = visible, EmptyPrefix = "" };
                var caretIndex = field.CaretIndex;
                if (field.Text != blockPiece.DisplayText)
                {
                    field.SetRuns(inline, blockPiece.Start, visible);
                    field.CaretIndex = Math.Clamp(caretIndex + field.Text.Length - after.Length, 0, field.Text.Length);
                }
            }
            finally { writingBlock = false; }
        };
        DataObject.AddPastingHandler(field, (_, e) =>
        {
            if (blockEditor != field || e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) { e.CancelCommand(); return; }
            if (!pasted.Contains('\n') && !pasted.Contains('\r'))
            {
                e.FormatToApply = DataFormats.UnicodeText;
                return;
            }
            var (start, length) = CurrentSelection();
            e.CancelCommand();
            if (piece.Cell)
            {
                field.SelectedText = pasted.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                return;
            }
            EndEdit();
            ApplyDocumentEdit(new NoteFormat.Edit(start, length, pasted, pasted.Length));
            BeginEdit(lastSelection!.Value.Start);
        });
        field.Focus();
        field.CaretIndex = Math.Clamp(caret, 0, field.Text.Length);
    }

    private static void SwapCaption(FrameworkElement before, FrameworkElement after)
    {
        if (before.Parent is Border cell) cell.Child = after;
        else if (before.Parent is Panel row)
        {
            var index = row.Children.IndexOf(before);
            Grid.SetColumn(after, Grid.GetColumn(before));
            row.Children.RemoveAt(index);
            row.Children.Insert(index, after);
        }
    }

    private void Reflow(double width)
    {
        if (blockEditor != null || width <= 0 || !double.IsFinite(width) || Math.Abs(width - formattedWidth) < .01) return;
        formattedWidth = width;
        renderedText = Text;
        lines.Children.Clear();
        wrap = null;
        lines.LineHeight = Math.Ceiling(FontSize * FontFamily.LineSpacing) + 2;
        var glyphSize = Math.Round(FontSize * .9);
        var inset = glyphSize + 6;
        var tasks = Checklist.Read(Text).ToDictionary(task => task.LineIndex);
        var paragraphs = Text.Split('\n');
        var flow = pictures?.TextFrames.OrderBy(picture => picture.Layout.Anchor)
            .ThenBy(picture => picture.Layout.Mode == ImageTextMode.Wrap).ToArray() ?? [];
        var pictureIndex = 0;
        void InsertPictures(int paragraph, int offset)
        {
            while (pictureIndex < flow.Length && Math.Min(flow[pictureIndex].Layout.Anchor, paragraphs.Length) <= paragraph)
            {
                var picture = flow[pictureIndex++];
                AddFlowImage(picture.Frame, picture.Layout, picture.Ratio, width, offset);
            }
        }
        using var formatter = TextFormatter.Create();
        var wrapping = CreateTextLines();

        var sourceOffset = 0;
        var resumeLine = 0;
        var insideSpoiler = false;
        var spoilerHidden = false;
        int? emptySpoilerBody = null;
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var lineOffset = sourceOffset;
            InsertPictures(index, lineOffset);
            sourceOffset += paragraphs[index].Length + 1;
            var paragraph = paragraphs[index].TrimEnd('\r');
            if (insideSpoiler && NoteSpoilers.Closes(paragraph))
            {
                insideSpoiler = spoilerHidden = false;
                emptySpoilerBody = null;
                continue;
            }
            if (!insideSpoiler && NoteSpoilers.Label(paragraph) is { } spoiler)
            {
                insideSpoiler = true;
                spoilerHidden = !openSpoilers.Contains(lineOffset);
                if (lineOffset <= resumeAt) resumeLine = lines.Children.Count;
                AddSpoiler(spoiler, lineOffset, paragraph, spoilerHidden);
                if (!spoilerHidden)
                {
                    var next = index + 1;
                    if (next == paragraphs.Length || NoteSpoilers.Closes(paragraphs[next]))
                        AddSpoilerPrompt(lineOffset);
                    else
                    {
                        while (next < paragraphs.Length && string.IsNullOrWhiteSpace(paragraphs[next])) next++;
                        if (next == paragraphs.Length || NoteSpoilers.Closes(paragraphs[next]))
                            emptySpoilerBody = sourceOffset;
                    }
                }
                continue;
            }
            if (spoilerHidden) continue;

            if (index + 1 < paragraphs.Length && TableCells(paragraph) is { } header &&
                TableCells(paragraphs[index + 1]) is { } divider && divider.Length == header.Length &&
                IsTableDivider(divider))
            {
                // Keep a table at its full width once a wrapped picture has reserved its rows.
                wrap = null;
                if (lineOffset <= resumeAt) resumeLine = lines.Children.Count;
                AddTableRow(header, width, lineOffset, lineOffset, formatter);
                sourceOffset += paragraphs[++index].Length + 1;
                while (index + 1 < paragraphs.Length && TableCells(paragraphs[index + 1]) is { } cells && cells.Length == header.Length)
                {
                    if (sourceOffset <= resumeAt) resumeLine = lines.Children.Count;
                    AddTableRow(cells, width, sourceOffset, lineOffset, formatter);
                    sourceOffset += paragraphs[++index].Length + 1;
                }
                continue;
            }

            tasks.TryGetValue(index, out var task);
            var label = task?.Label ?? paragraph;
            var prefix = paragraph.Length - label.Length;
            var weight = FontWeights.Normal;
            var marker = "";
            if (task == null)
            {
                if (label.StartsWith("## ", StringComparison.Ordinal))
                {
                    label = label[3..];
                    prefix = 3;
                    weight = FontWeights.SemiBold;
                }
                else if (label.StartsWith("- ", StringComparison.Ordinal) || label.StartsWith("> ", StringComparison.Ordinal))
                {
                    marker = label[0] == '-' ? "•" : "│";
                    label = label[2..];
                    prefix = 2;
                }
                else if (NoteFormat.NumberPrefix(label) is var number && number > 0)
                {
                    marker = label[..number].Trim();
                    label = label[number..];
                    prefix = number;
                }
            }

            var inline = new PaperInline(label);
            var taskLabels = task == null ? null : new List<TextBlock>();
            var font = weight == FontWeights.Normal ? wrapping : CreateTextLines(weight);
            var decoration = task != null ? inset : marker.Length == 0 ? 0 : Math.Max(inset, Math.Ceiling(FontSize * .65 * marker.Length + 5));
            foreach (var (offset, text) in font.Wrap(formatter, inline.Text, () => (wrap?.Width ?? width) - decoration, inline))
            {
                var caret = lineOffset + prefix + inline.SourceOffset(offset);
                if (caret <= resumeAt) resumeLine = lines.Children.Count;
                var row = new Grid { Height = lines.LineHeight, Tag = caret, Background = Brushes.Transparent };
                var caption = Caption(weight);
                caption.Margin = new Thickness(decoration, 0, 0, 0);
                caption.Tag = new RowCaret(lineOffset + prefix, offset, text.Length, inline);
                inline.Write(caption, offset, text.Length);
                if (emptySpoilerBody == lineOffset && offset == 0)
                {
                    caption.Text = L.T("Написать внутри…");
                    caption.Opacity = .68;
                }
                row.Children.Add(caption);
                if (task != null && taskLabels != null)
                {
                    caption.Opacity = task.IsChecked ? .72 : 1;
                    taskLabels.Add(caption);
                    if (offset == 0) row.Children.Add(CreateCheck(task, glyphSize, taskLabels));
                }
                if (offset == 0 && marker.Length > 0)
                    row.Children.Add(new TextBlock { Text = marker, Width = decoration, FontSize = FontSize, Foreground = Foreground,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
                AddTextRow(row);
            }
        }
        InsertPictures(paragraphs.Length, Text.Length);
        if (documentSelection) PaintSelection();
        if (resumeAt != null)
        {
            resumeAt = null;
            viewer.ScrollToVerticalOffset(resumeLine);
        }
    }

    private TextBlock Caption(FontWeight weight) => new()
    {
        FontFamily = FontFamily, FontSize = FontSize, FontWeight = weight, Foreground = Foreground,
        Cursor = Cursors.IBeam, VerticalAlignment = VerticalAlignment.Center
    };

    private void AddSpoiler(string label, int offset, string source, bool hidden)
    {
        var toggle = new Button
        {
            Content = hidden ? "▸" : "▾",
            Style = (Style)Application.Current.FindResource("PaperButton"), Height = lines.LineHeight, MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0), Padding = new Thickness(4, 0, 4, 0),
            FontFamily = FontFamily, FontSize = FontSize,
            ToolTip = hidden ? L.T("Показать скрытый текст") : L.T("Свернуть спойлер")
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, label + (hidden ? L.T(", раскрыть") : L.T(", свернуть")));
        var snapshot = renderedText;
        toggle.Click += (_, _) =>
        {
            var currentOffset = RebaseOffset(snapshot, offset);
            EndEdit();
            if (!openSpoilers.Add(currentOffset)) openSpoilers.Remove(currentOffset);
            resumeAt = currentOffset;
            InvalidateLines();
        };
        var labelOffset = source.IndexOf(":::spoiler", StringComparison.Ordinal) + ":::spoiler".Length;
        while (labelOffset < source.Length && char.IsWhiteSpace(source[labelOffset])) labelOffset++;
        var title = new PaperInline(source[labelOffset..].TrimEnd());
        var caption = Caption(FontWeights.SemiBold);
        title.Write(caption, 0, title.Text.Length);
        if (title.Text.Length == 0) caption.Text = label;
        caption.Tag = new RowCaret(offset + labelOffset, 0, title.Text.Length, title,
            EmptyPrefix: labelOffset == source.Length && !source.EndsWith(' ') ? " " : "") { Spoiler = offset };
        var row = new Grid { Height = lines.LineHeight, Tag = offset, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(caption, 1);
        row.Children.Add(toggle);
        row.Children.Add(caption);
        AddTextRow(row);
    }

    private void AddSpoilerPrompt(int offset)
    {
        var snapshot = renderedText;
        var prompt = new Button
        {
            Content = L.T("Написать внутри…"), Style = (Style)FindResource("PaperButton"),
            FontFamily = FontFamily, FontSize = FontSize, Foreground = Foreground, Opacity = .68,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(1, 0, 5, 0),
            Height = lines.LineHeight, MinHeight = 0
        };
        prompt.Click += (_, _) => EditSpoilerBody(RebaseOffset(snapshot, offset));
        AddTextRow(prompt);
    }

    internal static string[]? TableCells(string line)
    {
        line = line.Trim();
        if (line.Length < 3 || line[0] != '|' || line[^1] != '|') return null;
        var cells = line[1..^1].Split('|', StringSplitOptions.TrimEntries);
        return cells.Length is >= 2 and <= 12 ? cells : null;
    }
    internal static bool IsTableDivider(IEnumerable<string> cells) =>
        cells.All(cell => cell.Trim(':') is { Length: >= 3 } rule && rule.All(c => c == '-'));

    private void AddTableRow(string[] cells, double width, int source, int headerOffset, TextFormatter formatter)
    {
        var header = source == headerOffset;
        var weight = header ? FontWeights.SemiBold : FontWeights.Normal;
        var font = CreateTextLines(weight);
        var content = cells.Select(cell => new PaperInline(cell)).ToArray();
        var inset = Math.Clamp(width / cells.Length * .045, 2, 8);
        var wrapped = content.Select(inline => font.Wrap(formatter, inline.Text, width / cells.Length - inset * 2 - 2, inline).ToArray()).ToArray();
        var borderBrush = PaperPalette.Brush("#65605242");
        var headerFill = PaperPalette.Brush("#14564B35");
        var height = wrapped.Max(cell => cell.Length);
        for (var line = 0; line < height; line++)
        {
            var row = new Grid { Height = lines.LineHeight, Tag = source, Background = Brushes.Transparent, SnapsToDevicePixels = true };
            for (var column = 0; column < cells.Length; column++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var caption = Caption(weight);
                var cellOffset = TableCellOffset(Text, source, column);
                var location = new TableLocation(headerOffset, source, column);
                caption.ContextMenu = TableMenu(location, cells.Length);
                var (offset, text) = line < wrapped[column].Length ? wrapped[column][line] : (content[column].Text.Length, "");
                content[column].Write(caption, offset, text.Length);
                caption.Tag = new RowCaret(cellOffset, offset, text.Length, content[column], Cell: true) { Table = location };
                var cell = new Border
                {
                    Child = caption, Padding = new Thickness(inset, 0, inset, 0), BorderBrush = borderBrush, ClipToBounds = true,
                    Background = header ? headerFill : Brushes.Transparent, ContextMenu = caption.ContextMenu,
                    BorderThickness = new Thickness(column == 0 ? 1 : 0, header && line == 0 ? 1 : 0, 1, line == height - 1 ? 1 : 0)
                };
                Grid.SetColumn(cell, column);
                row.Children.Add(cell);
            }
            lines.Children.Add(row);
        }
    }

    private static int TableCellOffset(string text, int row, int column)
    {
        var end = text.IndexOf('\n', row);
        if (end < 0) end = text.Length;
        var separator = row;
        for (var index = 0; index <= column; index++)
        {
            var next = text.IndexOf('|', separator, end - separator);
            if (next < 0) return row;
            separator = next + 1;
        }
        while (separator < end && char.IsWhiteSpace(text[separator])) separator++;
        return separator;
    }

    private CheckBox CreateCheck(ChecklistItem task, double size, List<TextBlock> labels)
    {
        var check = new CheckBox
        {
            IsChecked = task.IsChecked, Style = (Style)Application.Current.FindResource("PaperCheck"),
            Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        System.Windows.Automation.AutomationProperties.SetName(check, task.Label);
        check.Click += (_, _) =>
        {
            var isChecked = check.IsChecked == true;
            toggling = true;
            try { Text = Checklist.SetChecked(Text, task.LineIndex, isChecked); }
            finally { toggling = false; }
            foreach (var label in labels) label.Opacity = isChecked ? .72 : 1;
        };
        return check;
    }

    public void BeginEdit() => BeginEdit(Text.Length);

    private void BeginEdit(int offset)
    {
        if (IsWebActive)
        {
            webEditor?.Focus();
            SendWebCommand("focus");
            return;
        }
        if (IsEditing) return;
        offset = Math.Clamp(offset, 0, Text.Length);
        if (UseVisualEditing())
        {
            viewer.UpdateLayout();
            var lineStart = NoteFormat.LineStart(Text, offset);
            var lineEnd = NoteFormat.LineEnd(Text, offset);
            var target = EditableCaptions().OrderBy(c =>
            {
                var source = ((RowCaret)c.Tag).Source;
                return source < lineStart || source > lineEnd;
            }).ThenBy(c =>
            {
                var piece = (RowCaret)c.Tag;
                return Math.Abs(offset - Math.Clamp(offset, piece.Raw(0), piece.Raw(piece.Length)));
            }).FirstOrDefault();
            if (target?.Tag is RowCaret row)
            {
                BeginBlock(target, row, row.Visible(offset));
                return;
            }
        }
        SetValue(IsEditingPropertyKey, true);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
        viewer.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        editor.FontFamily = FontFamily;
        editor.FontSize = FontSize;
        editor.Focus();
        editor.Select(Math.Clamp(offset, 0, editor.Text.Length), 0);
        var line = editor.GetLineIndexFromCharacterIndex(editor.CaretIndex);
        if (line >= 0) editor.ScrollToLine(line);
    }

    public void EndEdit()
    {
        if (IsWebActive)
        {
            SendWebCommand("blur");
            SetWebEditing(false);
            return;
        }
        if (!IsEditing) return;
        if (blockEditor is { } field && blockCaption is { } caption)
        {
            lastSelection = CurrentSelection();
            lastTable = blockPiece?.Table;
            resumeAt = CaretIndex;
            blockEditor = null;
            blockCaption = null;
            blockPiece = null;
            SetValue(IsEditingPropertyKey, false);
            SwapCaption(field, caption);
            IsEditingChanged?.Invoke(this, EventArgs.Empty);
            InvalidateLines();
            return;
        }
        SetValue(IsEditingPropertyKey, false);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
        lastSelection = (editor.SelectionStart, editor.SelectionLength);
        lastTable = null;
        resumeAt = editor.CaretIndex;
        editor.Visibility = Visibility.Collapsed;
        viewer.Visibility = Visibility.Visible;
        InvalidateLines();
    }

    internal void ResetNavigation()
    {
        navigationRevision++;
        if (formatMenu != null) formatMenu.IsOpen = false;
        formatMenu = null;
        EndEdit();
        lastSelection = null;
        lastTable = null;
        documentSelection = false;
        pendingStyle = PaperInline.Style.Plain;
        openSpoilers.Clear();
        InvalidateLines();
        resumeAt = null;
        editor.Select(0, 0);
        editor.ScrollToHome();
        viewer.ScrollToHome();
        editor.IsUndoEnabled = false;
        editor.IsUndoEnabled = true;
    }

    public void FocusForInput() { if (IsWebActive || string.IsNullOrEmpty(Text)) BeginEdit(); else Focus(); }

    private sealed class WholeLinePanel(Action<double> reflow) : StackPanel
    {
        public double LineHeight { get; set; } = 1;

        protected override Size MeasureOverride(Size constraint)
        {
            reflow(constraint.Width);
            return base.MeasureOverride(new Size(constraint.Width, WholeHeight(constraint.Height)));
        }

        protected override Size ArrangeOverride(Size arrangeSize)
        {
            var height = WholeHeight(arrangeSize.Height);
            base.ArrangeOverride(new Size(arrangeSize.Width, height));
            Clip = new RectangleGeometry(new Rect(0, 0, arrangeSize.Width, height));
            return arrangeSize;
        }

        private double WholeHeight(double height) => Math.Floor(height / LineHeight) * LineHeight;
    }
}
