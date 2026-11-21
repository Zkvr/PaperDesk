using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Win32Interop;
using PaperDesk.Desktop.Controls;

namespace PaperDesk.Desktop.Views;

public partial class StickyWindow : Window
{
    private readonly DeskController desk;
    public Guid NoteId { get; private set; }
    public Guid? StackId { get; internal set; }
    private bool loading = true, controllerClose;
    private Guid displayedNoteId;
    private string shownTitle = "", shownBody = "";
    public bool IsRaised { get; private set; }
    private long raisedAt;
    private readonly DispatcherTimer layerTimer;
    private readonly DispatcherTimer positionTimer;
    private readonly PaperDragResponse dragResponse;
    private readonly PaperDrag paperDrag;
    private bool dragging, peeling, projectingPeel;
    private static readonly Pen DragCue = Cue(Color.FromRgb(112, 89, 50), 115, 2);
    private static readonly Pen SnapCue = Cue(Color.FromRgb(38, 98, 87), 220, 2);
    private static readonly Pen DropCue = Cue(Color.FromRgb(154, 109, 41), 255, 3);
    private Pen? dragCue;
    private bool dropHighlighted;
    private string? shadowStyle;
    private double shadowOpacity;
    private Popup? tools;
    private static readonly DependencyPropertyKey IsToolsOpenPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsToolsOpen), typeof(bool), typeof(StickyWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsToolsOpenProperty = IsToolsOpenPropertyKey.DependencyProperty;
    public bool IsToolsOpen => (bool)GetValue(IsToolsOpenProperty);

    public StickyWindow(DeskController controller, Guid id, bool animateOpening = true, bool cardPreview = false)
    {
        desk = controller; NoteId = id; StackId = cardPreview ? null : desk.SurfaceStack(id);
        InitializeComponent(); var p = desk.WindowPlacement(id, StackId);
        Paper.ClipContentToPaper = true;
        Left = p.X; Top = p.Y; Width = Math.Clamp(p.Width, MinWidth, 1600); Height = Math.Clamp(p.Height, MinHeight, 1400);
        BodyBox.ConnectTitle(TitleBox);
        BodyBox.ConnectFormatMenu(FormatButton, Paper);
        Pictures.ConnectDrop(Paper, BodyBox);
        if (!cardPreview && Application.Current is App) BodyBox.EnableWebEditor(desk, () => desk.NewNote());
        ApplyStyle(p); RefreshContent(); loading = false;
        positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        layerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        dragResponse = new PaperDragResponse(PaintFlex);
        BentFace.IsVisibleChanged += (_, _) =>
        {
            if (BentFace.IsVisible) Paper.Pose = PaperPose.Flat;
            FlatFace.Opacity = BentFace.IsVisible ? 0 : 1;
        };
        paperDrag = new PaperDrag(this, Paper, DragHandle, PeelHandle,
            (whole, grip) => DragStarted(whole, grip), DragMoved, DragFinished);
        if (cardPreview) return; // A manager drag only displays the sheet. It never saves placement or joins the desktop layer.
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        LocationChanged += (_, _) => SchedulePosition(); SizeChanged += (_, _) => { ApplyLayout(); SchedulePosition(); };
        Loaded += (_, _) => { EnsureOnScreen(); ApplyLayer(); };
        var hasRendered = false;
        ContentRendered += (_, _) =>
        {
            if (hasRendered || !IsVisible) return;
            hasRendered = true;
            if (animateOpening) RevealPaper();
        };
        layerTimer.Tick += (_, _) =>
        {
            if (IsRaised && (!IsVisible || (Environment.TickCount64 - raisedAt >= 2000 && !DesktopLayer.ForegroundIsPaperDesk())))
                IsRaised = false;
            ApplyLayer();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                CloseTools();
                layerTimer.Stop();
                StopMotion();
            }
            else if (hasRendered) { EnsureOnScreen(); ApplyLayer(); RevealPaper(); }
        };
        Closing += (_, _) =>
        {
            CloseTools();
            paperDrag.Cancel();
            ResetPaperMotion();
            Crumple.Cancel();
            layerTimer.Stop();
            positionTimer.Stop();
            Flush();
            SavePosition();
            if (!controllerClose) desk.Store.SavePlacement(desk.Store.GetPlacement(NoteId) with { Visible = false });
        };
        PreviewKeyDown += (_, e) =>
        {
            if (IsCrumpling) { e.Handled = true; return; }
            if (BodyBox.IsEditing && e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            { BodyBox.OpenFormatMenu(); e.Handled = true; return; }
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { desk.NewNote(); e.Handled = true; }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) e.Handled = Pictures.Paste();
            else if (StackId.HasValue && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.PageDown or Key.PageUp)
            { desk.MoveStack(NoteId, e.Key == Key.PageDown ? 1 : -1); e.Handled = true; }
        };
    }
    private void ApplyStyle(Placement p)
    {
        Paper.ApplyAppearance(p);
        StackBack1.CornerRadius = StackBack2.CornerRadius = new CornerRadius(p.Corners == "square" ? 0 : 10);
        if (shadowStyle != p.Shadow)
        {
            shadowStyle = p.Shadow;
            Paper.ApplyShadow(PaperShadow, p.Shadow);
            shadowOpacity = PaperShadow.Opacity;
            FitPaperShadow();
        }
        ColorDot.Fill = Paper.Fill;
        BodyBox.FontFamily = new FontFamily(p.Font);
        BodyBox.FontSize = Math.Clamp(p.FontSize, 12, 32);
        BodyBox.QueueWebSync();
        PinButton.Background = p.Topmost ? new SolidColorBrush(Color.FromArgb(40, 80, 65, 28)) : Brushes.Transparent;
        PinButton.ToolTip = p.Topmost ? L.T("Открепить от переднего плана") : L.T("Закрепить поверх окон");
    }
    public void RefreshContent(IReadOnlyList<Note>? notes = null) => DisplayNote(NoteId, notes);
    internal void RefreshEditedContent() => DisplayNote(NoteId, textOnly: true);

    private bool DisplayNote(Guid id, IReadOnlyList<Note>? notes = null, bool textOnly = false)
    {
        var row = desk.Store.Find(id);
        if (row == null) return false;
        loading = true;
        var changedSheet = displayedNoteId != id;
        displayedNoteId = id;
        var draft = desk.UnsavedDraft(desk.Store, id);
        var title = draft?.Title ?? row.Note.Title;
        var body = draft?.Body ?? row.Note.Body;
        if (TitleBox.Text != title) TitleBox.Text = title;
        if (BodyBox.Text != body) { var caret = BodyBox.CaretIndex; BodyBox.Text = body; BodyBox.CaretIndex = Math.Min(caret, BodyBox.Text.Length); }
        Pictures.Bind(desk.Store, id, row.Note is { ArchivedAt: null, DeletedAt: null });
        if (changedSheet)
        {
            TitleBox.Select(0, 0);
            TitleBox.ScrollToHome();
            TitleBox.IsUndoEnabled = false;
            TitleBox.IsUndoEnabled = true;
            BodyBox.ResetNavigation();
        }
        shownTitle = TitleBox.Text;
        shownBody = BodyBox.Text;
        ConflictWarning.Visibility = row.Conflict != null ? Visibility.Visible : Visibility.Collapsed;
        if (textOnly) { BodyBox.QueueWebSync(); loading = false; return true; }
        MinimizeButton.Visibility = StackId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ApplyStyle(desk.Store.GetPlacement(id));
        var stack = notes ?? (StackId is Guid group ? desk.StackNotes(group) : []);
        var named = StackId is Guid namedId && namedId != Guid.Empty;
        var stacked = stack.Count > 1;
        StackNavigation.Visibility = stacked || named ? Visibility.Visible : Visibility.Collapsed;
        StackBack1.Visibility=stacked?Visibility.Visible:Visibility.Collapsed;
        StackBack2.Visibility=stacked&&stack.Count>2?Visibility.Visible:Visibility.Collapsed;
        PreviousSheet.Visibility = NextSheet.Visibility = stacked ? Visibility.Visible : Visibility.Hidden;
        var colors=PaperStack.ColorsBehind(stack,id,noteId=>desk.Store.GetPlacement(noteId).Color);
        if(colors.Count>0)StackBack1.Background=PaperPalette.Brush(colors[0]);
        if(colors.Count>1)StackBack2.Background=PaperPalette.Brush(colors[1]);
        var number = stack.ToList().FindIndex(n => n.Id == id) + 1;
        var name = named ? desk.StackName(StackId!.Value) : "";
        var count = stacked ? $"{number} / {stack.Count}  ⌄" : "⌄";
        if (named)
        {
            var label = new DockPanel();
            var indicator = new TextBlock { Text = count, Margin = new Thickness(5, 0, 0, 0) };
            DockPanel.SetDock(indicator, Dock.Right);
            label.Children.Add(indicator);
            label.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            StackLabel.Content = label;
            StackLabel.ToolTip = name + " · " + L.T("Выбрать листик из стопки");
        }
        else StackLabel.Content = count;
        System.Windows.Automation.AutomationProperties.SetName(StackLabel,
            (named ? name + ". " : "") + L.F("Листик {0} из {1}. Выбрать листик", number, stack.Count));
        ApplyLayout();
        loading = false;
        return true;
    }
    public void FocusEditor() => Dispatcher.BeginInvoke(() => { if (IsVisible && !IsCrumpling) BodyBox.FocusForInput(); });
    private void TextEdited(object sender, TextChangedEventArgs e) { if (!loading) Flush(); }
    public void Flush()
    {
        if (loading || displayedNoteId != NoteId) return;
        desk.SaveEditorText(desk.Store, NoteId, TitleBox.Text == shownTitle ? null : TitleBox.Text,
            BodyBox.Text == shownBody ? null : BodyBox.Text, shownTitle, shownBody);
        shownTitle = TitleBox.Text;
        shownBody = BodyBox.Text;
    }
    private void SchedulePosition() { if (!loading && !dragging) { positionTimer.Stop(); positionTimer.Start(); } }
    private void SavePosition() { if (!IsLoaded || dragging) return; if (StackId is Guid group) { desk.SaveStackBounds(group, Left, Top, Width, Height); return; } var p = desk.Store.GetPlacement(NoteId); if (Math.Abs(p.X - Left) + Math.Abs(p.Y - Top) + Math.Abs(p.Width - Width) + Math.Abs(p.Height - Height) > .5) desk.Store.SavePlacement(p with { X = Left, Y = Top, Width = Width, Height = Height }); }
    public void EnsureOnScreen()
    {
        if (!IsVisible || dragging || PresentationSource.FromVisual(this) is not HwndSource source) return;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary == null || !Native.GetWindowRect(source.Handle, out var bounds)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var title = new Rect(bounds.Left, bounds.Top, Math.Min(Width, 120) * dpi.DpiScaleX, 40 * dpi.DpiScaleY);
        var workAreas = screens.Select(screen => AsRect(screen.WorkingArea));
        var primaryArea = AsRect(primary.WorkingArea);
        if (RecoveryPosition(title, workAreas, primaryArea) is not { } destination) return;
        Native.MoveWithoutActivate(source.Handle, (int)destination.X, (int)destination.Y);

        // A move to another monitor can change the scale synchronously.
        dpi = VisualTreeHelper.GetDpi(this);
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, (primaryArea.Right - destination.X) / dpi.DpiScaleX));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, (primaryArea.Bottom - destination.Y) / dpi.DpiScaleY));
    }

    internal static Point? RecoveryPosition(Rect title, IEnumerable<Rect> workAreas, Rect primaryArea)
        => workAreas.Any(area => area.Contains(title)) ? null : new Point(primaryArea.Left + 40, primaryArea.Top + 40);

    private static Rect AsRect(System.Drawing.Rectangle area) => new(area.Left, area.Top, area.Width, area.Height);
    public void ApplyLayer()
    {
        if (!IsLoaded) return;
        var placement = desk.Store.GetPlacement(NoteId);
        var useDesktopLayer = desk.Settings.DesktopLayer && !placement.Topmost && !IsRaised;
        var attached = DesktopLayer.Attach(this, useDesktopLayer);
        Topmost = (dragging && !useDesktopLayer) || IsRaised || (placement.Topmost && !DesktopLayer.FullScreenOtherApp());
        DragHandle.ToolTip = !attached ? L.T("Слой рабочего стола недоступен") :
            StackId.HasValue ? L.T("Переместить стопку") : L.T("Переместить листик");
        if (IsVisible && (useDesktopLayer || placement.Topmost || IsRaised)) layerTimer.Start();
        else layerTimer.Stop();
    }

    public void RaiseForWork()
    {
        IsRaised = true;
        raisedAt = Environment.TickCount64;
        ApplyLayer();
    }
    internal void PreservePlacementOnClose() => controllerClose = true;
    public void CloseFromController() { PreservePlacementOnClose(); Close(); }
    private void ApplyLayout()
    {
        var compact=StickyLayout.IsCompact(Width,Height);
        StickyLayout.Apply(SheetContent,compact);
        SheetFrame.Margin = StickyLayout.FaceMargin(Width, Height);
        StackBack1.Margin=compact?new Thickness(13,15,5,10):new Thickness(21,26,9,12);
        StackBack2.Margin=compact?new Thickness(18,21,2,3):new Thickness(28,34,5,5);
        FitPaperShadow();
    }

    private void FitPaperShadow()
    {
        var margin = Math.Min(SheetFrame.Margin.Left, SheetFrame.Margin.Top);
        var (blur, depth, _) = PaperSurface.ShadowStyle(shadowStyle ?? "soft");
        PaperShadow.BlurRadius = Math.Min(blur, margin * .64);
        PaperShadow.ShadowDepth = Math.Min(depth, margin * .23);
    }
    private bool MotionEnabled => desk.Settings.Animations && SystemParameters.ClientAreaAnimation;

    internal void SettleOnStack()
    {
        if (!MotionEnabled || !IsVisible) return;
        ResetPaperMotion();
        // The glued edge touches first. The free corner relaxes onto the stationary backing.
        dragResponse.Land(new PaperPose(new Point(.78, .14), new Vector(.32, -.24), .76, default, default));
        var slide = new TranslateTransform();
        SheetFrame.RenderTransform = slide;
        var landing = new System.Windows.Media.Animation.DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(230))
        {
            DecelerationRatio = .85, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop
        };
        slide.BeginAnimation(TranslateTransform.YProperty, landing);
    }

    private void RevealPaper()
    {
        if (!MotionEnabled || dragging) return;
        ResetPaperMotion();
        dragResponse.Land(new PaperPose(new Point(.48, .12), new Vector(-.32, -.28), .62, default, default));
    }

    internal void DragStarted(bool wholeStack, Point? grip = null)
    {
        SavePosition();
        positionTimer.Stop();
        dragging = true;
        peeling = !wholeStack;
        CloseTools();
        BodyBox.EndEdit();
        Flush();
        if (!desk.Settings.DesktopLayer) Topmost = true;
        dragCue = DragCue;
        UpdateOutline();
        desk.BeginSheetDrag(this, peeling);
        ResetPaperMotion();
        if (peeling && MotionEnabled && dragging && IsVisible)
        {
            projectingPeel = true;
            Paper.UpdateLayout();
            var anchor = grip is Point point
                ? new Point(Math.Clamp(point.X / Math.Max(1, Paper.ActualWidth), 0, 1), Math.Clamp(point.Y / Math.Max(1, Paper.ActualHeight), 0, 1))
                : new Point(.5, .96);
            dragResponse.Grab(anchor, true);
        }
    }

    internal void DragMoved(Point pointer, Vector velocity, double fraction = 0, double lateral = 0, Point? proposed = null)
    {
        dragResponse.Follow(velocity, fraction, lateral);
        if (paperDrag.InContact) return;
        desk.PreviewSheetDrop(this, pointer);
        var handle = new WindowInteropHelper(this).Handle;
        if (!Native.GetWindowRect(handle, out var window)) return;
        var current = new Point(window.Left, window.Top);
        var position = proposed ?? current;
        var frame = SheetFrameBounds();
        if (frame.IsEmpty) return;
        frame.Offset(position - current);
        var snap = desk.SnapOffset(this, frame);
        position += snap ?? default;
        var x = (int)Math.Round(position.X);
        var y = (int)Math.Round(position.Y);
        if (x != window.Left || y != window.Top) Native.MoveWithoutActivate(handle, x, y);
        dragCue = snap.HasValue ? SnapCue : DragCue;
        UpdateOutline();
    }

    internal void DragFinished(Point? pointer, Rect origin)
    {
        dragResponse.Release();
        try { desk.FinishSheetDrag(this, pointer, origin, peeling); }
        finally
        {
            dragging = false;
            dragCue = null;
            UpdateOutline();
            if (IsVisible)
            {
                ApplyLayer();
                if (pointer != null) SavePosition();
            }
        }
    }

    internal void ShowLooseFace()
    {
        // Keep the controls and editor layout stable until the gesture commits.
        StackBack1.Visibility = StackBack2.Visibility = Visibility.Collapsed;
    }

    internal Rect PaperScreenBounds()
    {
        if (Paper.ActualWidth <= 0 || Paper.ActualHeight <= 0 || PresentationSource.FromVisual(Paper) == null) return Rect.Empty;
        return new Rect(Paper.PointToScreen(new Point()), Paper.PointToScreen(new Point(Paper.ActualWidth, Paper.ActualHeight)));
    }

    internal Rect SheetFrameBounds()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || !Native.GetWindowRect(source.Handle, out var bounds))
            return Rect.Empty;
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            new Point(bounds.Left + SheetFrame.Margin.Left * dpi.DpiScaleX, bounds.Top + SheetFrame.Margin.Top * dpi.DpiScaleY),
            new Point(bounds.Right - SheetFrame.Margin.Right * dpi.DpiScaleX, bounds.Bottom - SheetFrame.Margin.Bottom * dpi.DpiScaleY));
    }

    internal void HighlightDrop(bool highlight)
    {
        dropHighlighted = highlight;
        UpdateOutline();
    }

    private void UpdateOutline() => Paper.Outline = dropHighlighted ? DropCue : dragCue;

    private static Pen Cue(Color color, byte alpha, double thickness)
    {
        var pen = new Pen(ThemePalette.Brush(color, alpha), thickness);
        pen.Freeze();
        return pen;
    }

    internal void PlaceBehind(StickyWindow source)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var above = new WindowInteropHelper(source).Handle;
        Native.SetWindowPos(handle, above, 0, 0, 0, 0, 0x0013); // Keep bounds and focus.
    }

    private void PaintFlex(PaperPose pose)
    {
        var flex = pose.Flex;
        var strength = Math.Clamp(flex.Length, 0, 1);
        var peel = Math.Clamp(pose.Peel, 0, 1);
        if (projectingPeel && (peel > .002 || strength > .002) && Paper.ActualWidth > 0 && Paper.ActualHeight > 0)
        {
            Paper.Effect = null;
            BentFace.Show(Paper, pose, shadowOpacity > 0, BodyBox);
            BentFace.Effect = shadowOpacity > 0 ? PaperShadow : null;
            // The live 2D face stays legible while the browser provides its first texture.
            Paper.Pose = PaperPose.Flat;
        }
        else
        {
            BentFace.Clear();
            BentFace.Effect = null;
            Paper.Pose = projectingPeel ? PaperPose.Flat : pose;
            Paper.Effect = shadowOpacity > 0 ? PaperShadow : null;
        }
        if (shadowOpacity > 0) PaperShadow.Opacity = shadowOpacity + .085 * strength + .12 * peel;
    }

    private void ResetPaperMotion()
    {
        SheetFrame.RenderTransform = Transform.Identity;
        projectingPeel = false;
        dragResponse.Stop();
        PaintFlex(PaperPose.Flat);
    }

    internal void StopMotion()
    {
        ResetPaperMotion();
        Crumple.FinishNow();
    }

    public bool IsCrumpling => Crumple.IsPlaying;

    internal bool TrySwitchNote(Guid id, int direction)
    {
        if (IsCrumpling || id == NoteId) return false;
        CloseTools();
        BodyBox.EndEdit();
        Flush();
        ResetPaperMotion();
        if (!DisplayNote(id)) return false;
        var switched = AdoptRevealedNote(id);
        if (switched && direction != 0 && IsVisible && MotionEnabled)
            dragResponse.Land(new PaperPose(new Point(direction > 0 ? .18 : .82, .18),
                new Vector(Math.Sign(direction) * .52, -.13), .18, default, default));
        return switched;
    }

    internal bool AdoptRevealedNote(Guid successor)
    {
        if (displayedNoteId != successor || successor == NoteId) return false;
        NoteId = successor;
        ApplyLayer();
        return true;
    }

    public void PlayCrumple(Guid successor, Action<bool> finished)
    {
        if (!IsVisible || !MotionEnabled || IsCrumpling) { finished(false); return; }
        CloseTools();
        BodyBox.EndEdit();
        ResetPaperMotion();
        // Freeze the departing sheet's position; the revealed sheet can have different margins.
        Crumple.Margin = SheetFrame.Margin;
        var revealed = false;
        if (!Crumple.TryPlay(Paper, () =>
        {
            Paper.Opacity = 1;
            Paper.IsHitTestVisible = StackBack1.IsHitTestVisible = StackBack2.IsHitTestVisible = true;
            finished(revealed);
        }, desktopDrop: true)) { finished(false); return; }
        Paper.IsHitTestVisible = StackBack1.IsHitTestVisible = StackBack2.IsHitTestVisible = false;
        revealed = successor != Guid.Empty && DisplayNote(successor);
        if (!revealed)
        {
            Paper.Opacity = 0;
            StackBack1.Visibility = StackBack2.Visibility = Visibility.Collapsed;
        }
    }
    private void PinClick(object sender, RoutedEventArgs e) { var p = desk.Store.GetPlacement(NoteId); var next = p with { Topmost = !p.Topmost }; desk.Store.SavePlacement(next); ApplyStyle(next); ApplyLayer(); }
    private void ColorClick(object sender, RoutedEventArgs e)
    {
        ShowTools(StickyTools.CreateAppearance(desk, NoteId, (FrameworkElement)sender, this, PlacementMode.Right, ApplyStyle, SetNoteSize));
    }
    private void NewClick(object sender, RoutedEventArgs e) => desk.NewNote();
    private void MinimizeStackClick(object sender, RoutedEventArgs e) { if (StackId is Guid group) desk.MinimizeStack(group); }
    private void ConflictClick(object sender, RoutedEventArgs e) => desk.ShowManager();
    private void FormatClick(object sender, RoutedEventArgs e) => BodyBox.OpenFormatMenu();
    private void PreviousStackClick(object sender,RoutedEventArgs e) => desk.MoveStack(NoteId,-1);
    private void NextStackClick(object sender,RoutedEventArgs e) => desk.MoveStack(NoteId,1);
    private void SecondStackClick(object sender,RoutedEventArgs e) => desk.MoveStack(NoteId,2);
    private void StackMenuClick(object sender,RoutedEventArgs e)
    {
        ShowTools(StickyTools.CreateStackPicker(desk, NoteId, (FrameworkElement)sender));
    }
    private void HideClick(object sender, RoutedEventArgs e) => desk.HideSticky(NoteId);
    private void TrashClick(object sender, RoutedEventArgs e) => desk.ChangeState(NoteId, "trash");
    private void MenuClick(object sender, RoutedEventArgs e)
    {
        ShowTools(StickyTools.CreateActions(desk, NoteId, (FrameworkElement)sender, ApplyStyle));
    }
    private void ResizeCornerDrag(object sender,System.Windows.Controls.Primitives.DragDeltaEventArgs e){Width=Math.Clamp(Width+e.HorizontalChange,MinWidth,1600);Height=Math.Clamp(Height+e.VerticalChange,MinHeight,1400);}
    private void SetNoteSize(Size size){Width=Math.Clamp(size.Width,MinWidth,1600);Height=Math.Clamp(size.Height,MinHeight,1400);SavePosition();}

    private void ShowTools(Popup popup)
    {
        CloseTools();
        tools = popup;
        popup.Closed += (_, _) =>
        {
            if (tools != popup) return;
            tools = null;
            SetValue(IsToolsOpenPropertyKey, false);
        };
        SetValue(IsToolsOpenPropertyKey, true);
        popup.IsOpen = true;
    }

    private void CloseTools()
    {
        var popup = tools;
        tools = null;
        if (popup != null) popup.IsOpen = false;
        SetValue(IsToolsOpenPropertyKey, false);
    }
}
