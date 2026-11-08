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
    private static readonly DependencyPropertyKey IsEditingPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsEditing), typeof(bool), typeof(NoteBodyEditor), new PropertyMetadata(false));
    public static readonly DependencyProperty IsEditingProperty = IsEditingPropertyKey.DependencyProperty;
    public bool IsEditing => (bool)GetValue(IsEditingProperty);
    public int CaretIndex { get => editor.CaretIndex; set => editor.CaretIndex = value; }

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

        editor.TextChanged += (_,_) => { if (!updating) SetCurrentValue(TextProperty, editor.Text); };
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
            editor.Select(Math.Min(selection.Item1, Text.Length), Math.Min(selection.Item2, Math.Max(0, Text.Length - selection.Item1)));
            editor.SelectedText = e.Text;
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
        var selection = IsEditing ? (editor.SelectionStart, editor.SelectionLength) : lastSelection ?? (Text.Length, 0);
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
                BeginEdit(start);
                ApplyEdit(NoteFormat.Apply(Text, start, length, kind));
                EndEdit();
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

    private void EditOnClick(object sender, MouseButtonEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element != null && element != viewer;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is ButtonBase or ScrollBar) return;
            if (element is Grid { Tag: int offset } row)
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

    private PaperTextLines CreateTextLines(FontWeight? weight = null) => new(
        new Typeface(FontFamily, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        FontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip, Foreground);

    private sealed record RowCaret(int Source, int Start, int Length, PaperInline Inline);

    private void Reflow(double width)
    {
        if (width <= 0 || !double.IsFinite(width) || Math.Abs(width - formattedWidth) < .01) return;
        formattedWidth = width;
        lines.Children.Clear();
        lines.LineHeight = Math.Ceiling(FontSize * FontFamily.LineSpacing) + 2;
        var glyphSize = Math.Round(FontSize * .9);
        var inset = glyphSize + 6;
        var tasks = Checklist.Read(Text).ToDictionary(task => task.LineIndex);
        var paragraphs = Text.Split('\n');
        using var formatter = TextFormatter.Create();
        var wrapping = CreateTextLines();

        var sourceOffset = 0;
        var resumeLine = 0;
        var insideSpoiler = false;
        var spoilerHidden = false;
        for (var index = 0; index < paragraphs.Length; index++)
        {
            var lineOffset = sourceOffset;
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
                AddSpoiler(spoiler, lineOffset, spoilerHidden);
                continue;
            }
            if (spoilerHidden) continue;

            if (index + 1 < paragraphs.Length && TableCells(paragraph) is { } header &&
                TableCells(paragraphs[index + 1]) is { } divider && divider.Length == header.Length &&
                IsTableDivider(divider))
            {
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
            if (task == null)
            {
                if (label.StartsWith("## ", StringComparison.Ordinal))
                {
                    label = label[3..];
                    prefix = 3;
                    weight = FontWeights.SemiBold;
                }
                else if (label.StartsWith("- ", StringComparison.Ordinal)) label = "• " + label[2..];
                else if (label.StartsWith("> ", StringComparison.Ordinal)) label = "│ " + label[2..];
            }

            var inline = new PaperInline(label);
            var taskLabels = task == null ? null : new List<TextBlock>();
            var font = weight == FontWeights.Normal ? wrapping : CreateTextLines(weight);
            foreach (var (offset, text) in font.Wrap(formatter, inline.Text, width - (task == null ? 0 : inset), inline))
            {
                var caret = lineOffset + prefix + inline.SourceOffset(offset);
                if (caret <= resumeAt) resumeLine = lines.Children.Count;
                var row = new Grid { Height = lines.LineHeight, Tag = caret, Background = Brushes.Transparent };
                var caption = Caption(weight);
                caption.Margin = new Thickness(task == null ? 0 : inset, 0, 0, 0);
                caption.Tag = new RowCaret(lineOffset + prefix, offset, text.Length, inline);
                inline.Write(caption, offset, text.Length);
                row.Children.Add(caption);
                if (task != null && taskLabels != null)
                {
                    caption.Opacity = task.IsChecked ? .72 : 1;
                    taskLabels.Add(caption);
                    if (offset == 0) row.Children.Add(CreateCheck(task, glyphSize, taskLabels));
                }
                lines.Children.Add(row);
            }
        }
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

    private void AddSpoiler(string label, int offset, bool hidden)
    {
        var toggle = new Button
        {
            Content = new TextBlock { Text = (hidden ? "▸  " : "▾  ") + label, TextTrimming = TextTrimming.CharacterEllipsis },
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
        lines.Children.Add(toggle);
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
                if (line < wrapped[column].Length)
                {
                    var (offset, text) = wrapped[column][line];
                    content[column].Write(caption, offset, text.Length);
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
        SetValue(IsEditingPropertyKey, true);
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
        SetValue(IsEditingPropertyKey, false);
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
