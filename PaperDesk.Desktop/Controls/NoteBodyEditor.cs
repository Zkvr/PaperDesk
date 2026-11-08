using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.TextFormatting;

namespace PaperDesk.Desktop.Controls;

/// <summary>A line-scrolling note with clickable tasks and an inline text editor.</summary>
public sealed class NoteBodyEditor : UserControl
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
        get => blockEditor is { } input ? Math.Clamp(blockSource + input.CaretIndex, 0, Text.Length) : editor.CaretIndex;
        set { if (blockEditor is { } input) input.CaretIndex = Math.Clamp(value - blockSource, 0, input.Text.Length); else editor.CaretIndex = value; }
    }
    internal bool EditingPlainText => IsEditing && blockEditor == null;

    private readonly TextBox editor;
    private readonly WholeLinePanel lines;
    private readonly ScrollViewer viewer;
    private bool updating, toggling;
    private double formattedWidth = -1;
    private int? resumeAt;
    private (int Start, int Length)? lastSelection;
    private readonly HashSet<int> openSpoilers = new();
    private ContextMenu? formatMenu;
    private Window? owner;
    private NotePictures? pictures;
    private TextWrapRegion? wrap;
    private TextBox? blockEditor, titleBox;
    private TextBlock? blockCaption;
    private int blockSource;
    private bool writingBlock, switchingEditor;

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
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F2) { BeginEdit(); e.Handled = true; }
            else if (IsEditing && (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)))
            {
                EndEdit();
                Focus();
                e.Handled = true;
            }
            else if (blockEditor is { } field && e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                var start = Math.Clamp(blockSource + field.SelectionStart, 0, Text.Length);
                var length = Math.Min(field.SelectionLength, Text.Length - start);
                var cell = (blockCaption?.Tag as RowCaret)?.Cell == true;
                var nextCell = cell ? EditableCaptions().Where(c => (c.Tag as RowCaret)?.Cell == true)
                    .Select(c => (RowCaret)c.Tag).FirstOrDefault(c => c.Source > blockSource) : null;
                EndEdit();
                if (cell)
                {
                    if (nextCell != null) BeginEdit(nextCell.Source);
                    else Focus();
                }
                else
                {
                    var change = length == 0 ? NoteFormat.ContinueList(Text, start) : null;
                    ApplyDocumentEdit(change ?? new NoteFormat.Edit(start, length, "\n", 1));
                    BeginEdit(lastSelection!.Value.Start);
                }
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
        IsKeyboardFocusWithinChanged += (_, _) => { if (!IsKeyboardFocusWithin && formatMenu?.IsOpen != true) EndEdit(); };
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

    internal void OpenFormatMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        formatMenu = menu;
        var selection = blockEditor is { } field ? (blockSource + field.SelectionStart, field.SelectionLength) :
            IsEditing ? (editor.SelectionStart, editor.SelectionLength) : lastSelection ?? (Text.Length, 0);
        foreach (var (kind, caption) in new[]
        {
            (NoteFormat.Kind.Bullet, L.T("•  Маркированный список")), (NoteFormat.Kind.Number, L.T("1.  Нумерованный список")),
            (NoteFormat.Kind.Task, L.T("☐  Чекбокс")), (NoteFormat.Kind.Table, L.T("▦  Таблица")), (NoteFormat.Kind.Spoiler, L.T("▸  Спойлер")),
            (NoteFormat.Kind.Heading, L.T("¶  Подзаголовок")), (NoteFormat.Kind.Quote, L.T("❞  Цитата")),
            (NoteFormat.Kind.Bold, L.T("Жирный")), (NoteFormat.Kind.Italic, L.T("Курсив")),
            (NoteFormat.Kind.Strike, L.T("Зачёркнутый")), (NoteFormat.Kind.Code, L.T("Код"))
        })
        {
            var item = new MenuItem { Header = caption };
            if (kind == NoteFormat.Kind.Bold) item.FontWeight = FontWeights.Bold;
            if (kind == NoteFormat.Kind.Italic) item.FontStyle = FontStyles.Italic;
            item.Click += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (formatMenu != menu || !IsEnabled || !IsVisible) return;
                formatMenu = null;
                var start = Math.Min(selection.Item1, Text.Length);
                var length = Math.Min(selection.Item2, Text.Length - start);
                EndEdit();
                ApplyDocumentEdit(NoteFormat.Apply(Text, start, length, kind));
                Focus();
            });
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() => { if (!IsKeyboardFocusWithin && formatMenu?.IsOpen != true) EndEdit(); });
        menu.IsOpen = true;
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
        SetCurrentValue(TextProperty, Text[..change.Start] + change.Text + Text[(change.Start + change.Length)..]);
        lastSelection = (change.Start + change.SelectStart, change.SelectLength);
        resumeAt = lastSelection.Value.Start;
        InvalidateLines();
    }

    private void EditOnClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element != null && element != viewer;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or ScrollBar or Thumb or TextBox) return;
            if (UseVisualEditing() && element is TextBlock { Tag: RowCaret piece } target)
            {
                var clickedAt = CreateTextLines().GetCaretOffset(piece.DisplayText, e.GetPosition(target).X);
                BeginBlock(target, piece, clickedAt);
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
                BeginEdit(offset);
                e.Handled = true;
                return;
            }
        }
        BeginEdit();
        e.Handled = true;
    }

    private static void TextUpdated(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        var self = (NoteBodyEditor)obj;
        if (self.editor == null) return;
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

        if (!self.IsEditing && !self.toggling)
        {
            self.lastSelection = null;
            self.openSpoilers.Clear();
            self.InvalidateLines();
            self.viewer.ScrollToHome();
        }
        self.TextChanged?.Invoke(self, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
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
    internal void ReflowPictures() => InvalidateLines();

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
        internal string DisplayText => Inline.Text.Substring(Start, Length);
        internal int Raw(int offset) => Source + Inline.SourceOffset(Start + offset);
    }

    private static bool HasVisualBlocks(string text) =>
        Checklist.Read(text).Count > 0 || new PaperInline(text).HasMarkup || text.Split('\n').Any(line =>
            line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal) ||
            line.StartsWith("> ", StringComparison.Ordinal) || NoteSpoilers.Label(line) != null || TableCells(line) != null);

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
        var original = Text;
        var field = new TextBox
        {
            Style = (Style)Application.Current.FindResource("PaperEditor"),
            Text = piece.DisplayText, FontFamily = caption.FontFamily, FontSize = caption.FontSize,
            FontWeight = caption.FontWeight, Foreground = caption.Foreground,
            Padding = new Thickness(0), Margin = caption.Margin, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            TextWrapping = TextWrapping.NoWrap
        };
        blockEditor = field;
        blockCaption = caption;
        blockSource = piece.Raw(0);
        SetValue(IsEditingPropertyKey, true);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
        SwapCaption(caption, field);
        field.TextChanged += (_, _) =>
        {
            if (blockEditor != field) return;
            var before = piece.DisplayText;
            var after = field.Text.Replace('\r', ' ').Replace('\n', ' ');
            if (piece.Cell) after = after.Replace('|', '¦');
            var prefix = 0;
            while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
            var beforeEnd = before.Length;
            var afterEnd = after.Length;
            while (beforeEnd > prefix && afterEnd > prefix && before[beforeEnd - 1] == after[afterEnd - 1])
            { beforeEnd--; afterEnd--; }
            var start = piece.Raw(prefix);
            var end = beforeEnd == prefix ? start : piece.Raw(beforeEnd - 1) + 1;
            writingBlock = true;
            var insertion = after[prefix..afterEnd];
            if (before.Length == 0 && insertion.Length > 0) insertion = piece.EmptyPrefix + insertion;
            try { SetCurrentValue(TextProperty, original[..start] + insertion + original[end..]); }
            finally { writingBlock = false; }
        };
        DataObject.AddPastingHandler(field, (_, e) =>
        {
            if (blockEditor != field || piece.Cell || e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted ||
                !pasted.Contains('\n')) return;
            var start = Math.Clamp(blockSource + field.SelectionStart, 0, Text.Length);
            var length = Math.Min(field.SelectionLength, Text.Length - start);
            e.CancelCommand();
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
        lines.Children.Clear();
        wrap = null;
        lines.LineHeight = Math.Ceiling(FontSize * FontFamily.LineSpacing) + 2;
        var glyphSize = Math.Round(FontSize * .9);
        var inset = glyphSize + 6;
        var tasks = Checklist.Read(Text).ToDictionary(task => task.LineIndex);
        var paragraphs = Text.Split('\n');
        var flow = pictures?.TextFrames.OrderBy(picture => picture.Layout.Anchor).ToArray() ?? [];
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
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var lineOffset = sourceOffset;
            InsertPictures(index, lineOffset);
            sourceOffset += paragraphs[index].Length + 1;
            var paragraph = paragraphs[index].TrimEnd('\r');
            if (insideSpoiler && NoteSpoilers.Closes(paragraph))
            {
                insideSpoiler = spoilerHidden = false;
                continue;
            }
            if (!insideSpoiler && NoteSpoilers.Label(paragraph) is { } spoiler)
            {
                insideSpoiler = true;
                spoilerHidden = !openSpoilers.Contains(lineOffset);
                if (lineOffset <= resumeAt) resumeLine = lines.Children.Count;
                AddSpoiler(spoiler, lineOffset, paragraph, spoilerHidden);
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
                AddTableRow(header, width, lineOffset, true, formatter);
                sourceOffset += paragraphs[++index].Length + 1;
                while (index + 1 < paragraphs.Length && TableCells(paragraphs[index + 1]) is { } cells && cells.Length == header.Length)
                {
                    if (sourceOffset <= resumeAt) resumeLine = lines.Children.Count;
                    AddTableRow(cells, width, sourceOffset, false, formatter);
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
            }

            var inline = new PaperInline(label);
            var taskLabels = task == null ? null : new List<TextBlock>();
            var font = weight == FontWeights.Normal ? wrapping : CreateTextLines(weight);
            var decoration = task != null || marker.Length > 0 ? inset : 0;
            foreach (var (offset, text) in font.Wrap(formatter, inline.Text, () => (wrap?.Width ?? width) - decoration, inline))
            {
                var caret = lineOffset + prefix + inline.SourceOffset(offset);
                if (caret <= resumeAt) resumeLine = lines.Children.Count;
                var row = new Grid { Height = lines.LineHeight, Tag = caret, Background = Brushes.Transparent };
                var caption = Caption(weight);
                caption.Margin = new Thickness(decoration, 0, 0, 0);
                caption.Tag = new RowCaret(lineOffset + prefix, offset, text.Length, inline);
                inline.Write(caption, offset, text.Length);
                row.Children.Add(caption);
                if (task != null && taskLabels != null)
                {
                    caption.Opacity = task.IsChecked ? .72 : 1;
                    taskLabels.Add(caption);
                    if (offset == 0) row.Children.Add(CreateCheck(task, glyphSize, taskLabels));
                }
                if (offset == 0 && marker.Length > 0)
                    row.Children.Add(new TextBlock { Text = marker, Width = inset, FontSize = FontSize, Foreground = Foreground,
                        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
                AddTextRow(row);
            }
        }
        InsertPictures(paragraphs.Length, Text.Length);
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
        toggle.Click += (_, _) =>
        {
            if (!openSpoilers.Add(offset)) openSpoilers.Remove(offset);
            resumeAt = offset;
            InvalidateLines();
        };
        var labelOffset = source.IndexOf(":::spoiler", StringComparison.Ordinal) + ":::spoiler".Length;
        while (labelOffset < source.Length && char.IsWhiteSpace(source[labelOffset])) labelOffset++;
        var title = new PaperInline(source[labelOffset..].TrimEnd());
        var caption = Caption(FontWeights.SemiBold);
        title.Write(caption, 0, title.Text.Length);
        if (title.Text.Length == 0) caption.Text = label;
        caption.Tag = new RowCaret(offset + labelOffset, 0, title.Text.Length, title, EmptyPrefix: " ");
        var row = new Grid { Height = lines.LineHeight, Tag = offset, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(caption, 1);
        row.Children.Add(toggle);
        row.Children.Add(caption);
        AddTextRow(row);
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

    private void AddTableRow(string[] cells, double width, int source, bool header, TextFormatter formatter)
    {
        var weight = header ? FontWeights.SemiBold : FontWeights.Normal;
        var font = CreateTextLines(weight);
        var content = cells.Select(cell => new PaperInline(cell)).ToArray();
        var wrapped = content.Select(inline => font.Wrap(formatter, inline.Text, width / cells.Length - 10, inline).ToArray()).ToArray();
        var borderBrush = PaperPalette.Brush("#736246");
        var height = wrapped.Max(cell => cell.Length);
        for (var line = 0; line < height; line++)
        {
            var row = new Grid { Height = lines.LineHeight, Tag = source, Background = Brushes.Transparent };
            for (var column = 0; column < cells.Length; column++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var caption = Caption(weight);
                var cellOffset = TableCellOffset(Text, source, column);
                if (line < wrapped[column].Length)
                {
                    var (offset, text) = wrapped[column][line];
                    content[column].Write(caption, offset, text.Length);
                    caption.Tag = new RowCaret(cellOffset, offset, text.Length, content[column], Cell: true);
                }
                var cell = new Border
                {
                    Child = caption, Padding = new Thickness(4, 0, 4, 0), BorderBrush = borderBrush, ClipToBounds = true,
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
        if (IsEditing) return;
        if (UseVisualEditing())
        {
            viewer.UpdateLayout();
            var target = EditableCaptions().OrderBy(c =>
            {
                var piece = (RowCaret)c.Tag;
                return Math.Abs(offset - Math.Clamp(offset, piece.Raw(0), piece.Raw(piece.Length)));
            }).FirstOrDefault();
            if (target?.Tag is RowCaret row)
            {
                BeginBlock(target, row, offset - row.Raw(0));
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
        if (!IsEditing) return;
        if (blockEditor is { } field && blockCaption is { } caption)
        {
            lastSelection = (Math.Clamp(blockSource + field.SelectionStart, 0, Text.Length), field.SelectionLength);
            resumeAt = CaretIndex;
            blockEditor = null;
            blockCaption = null;
            SetValue(IsEditingPropertyKey, false);
            SwapCaption(field, caption);
            IsEditingChanged?.Invoke(this, EventArgs.Empty);
            InvalidateLines();
            return;
        }
        SetValue(IsEditingPropertyKey, false);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
        lastSelection = (editor.SelectionStart, editor.SelectionLength);
        resumeAt = editor.CaretIndex;
        editor.Visibility = Visibility.Collapsed;
        viewer.Visibility = Visibility.Visible;
        InvalidateLines();
    }

    internal void ResetNavigation()
    {
        if (formatMenu != null) formatMenu.IsOpen = false;
        formatMenu = null;
        EndEdit();
        lastSelection = null;
        openSpoilers.Clear();
        InvalidateLines();
        resumeAt = null;
        editor.Select(0, 0);
        editor.ScrollToHome();
        viewer.ScrollToHome();
        editor.IsUndoEnabled = false;
        editor.IsUndoEnabled = true;
    }

    public void FocusForInput() { if (string.IsNullOrEmpty(Text)) BeginEdit(); else Focus(); }

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
