using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using PaperDesk.Desktop.Services;
using PaperDesk.Desktop.Win32Interop;
using PaperDesk.Desktop.Controls;

namespace PaperDesk.Desktop.Views;

public partial class StickyWindow : Window
{
    private readonly DeskController desk;
    public Guid NoteId { get; private set; }
    public Guid? StackId { get; internal set; }
    internal static readonly Point DragPin = new(.5, .07);
    private bool loading = true, controllerClose;
    private Guid displayedNoteId;
    private string shownTitle = "", shownBody = "";
    public bool IsRaised { get; private set; }
    private long raisedAt;
    private readonly DispatcherTimer layerTimer;
    private readonly DispatcherTimer positionTimer;
    private readonly PaperDragResponse dragResponse;
    private readonly PaperDrag paperDrag;
    private bool dragging, peeling;
    private Popup? tools;
    private Vector rearPull1, rearPull2;
    private static readonly DependencyPropertyKey IsToolsOpenPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsToolsOpen), typeof(bool), typeof(StickyWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsToolsOpenProperty = IsToolsOpenPropertyKey.DependencyProperty;
    public bool IsToolsOpen => (bool)GetValue(IsToolsOpenProperty);

    public StickyWindow(DeskController controller, Guid id, bool animateOpening = true)
    {
        desk = controller; NoteId = id; StackId = desk.SurfaceStack(id);
        InitializeComponent(); var p = desk.WindowPlacement(id, StackId);
        Left = p.X; Top = p.Y; Width = Math.Clamp(p.Width, MinWidth, 1600); Height = Math.Clamp(p.Height, MinHeight, 1400); Opacity = Math.Clamp(p.Opacity, .94, 1);
        BodyBox.ConnectTitle(TitleBox);
        BodyBox.ConnectFormatMenu(FormatButton, Paper);
        Pictures.ConnectDrop(Paper, BodyBox);
        if (Application.Current is App) BodyBox.EnableWebEditor(desk, () => desk.NewNote());
        ApplyStyle(p); RefreshContent(); loading = false;
        positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        positionTimer.Tick += (_, _) => { positionTimer.Stop(); SavePosition(); };
        LocationChanged += (_, _) => SchedulePosition(); SizeChanged += (_, _) => { ApplyLayout(); SchedulePosition(); };
        dragResponse = new PaperDragResponse(PaintPull);
        paperDrag = new PaperDrag(this, DragHandle, PeelHandle, DragStarted, DragMoved, DragFinished);
        Loaded += (_, _) => { EnsureOnScreen(); ApplyLayer(); };
        var hasRendered = false;
        ContentRendered += (_, _) =>
        {
            if (hasRendered || !IsVisible) return;
            hasRendered = true;
            if (animateOpening) RevealPaper();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseTools();
            else if (hasRendered) { EnsureOnScreen(); RevealPaper(); }
        };
        layerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        layerTimer.Tick += (_, _) =>
        {
            if (IsRaised && (!IsVisible || (Environment.TickCount64 - raisedAt >= 2000 && !DesktopLayer.ForegroundIsPaperDesk())))
                IsRaised = false;
            ApplyLayer();
        };
        layerTimer.Start();
        Closing += (_, _) =>
        {
            CloseTools();
            paperDrag.Cancel();
            dragResponse.Stop();
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
    private void ApplyStyle(Placement p) { Paper.Fill = PaperPalette.Brush(p.Color); ColorDot.Fill = PaperPalette.Brush(p.Color); BodyBox.FontFamily = new FontFamily(p.Font); BodyBox.FontSize = Math.Clamp(p.FontSize, 12, 32); BodyBox.QueueWebSync(); Opacity = Math.Clamp(p.Opacity, .94, 1); PinButton.Background = p.Topmost ? new SolidColorBrush(Color.FromArgb(40,80,65,28)) : Brushes.Transparent; PinButton.ToolTip = p.Topmost ? L.T("Открепить от переднего плана") : L.T("Закрепить поверх окон"); }
    public void RefreshContent(IReadOnlyList<Note>? notes = null) => DisplayNote(NoteId, notes);

    private bool DisplayNote(Guid id, IReadOnlyList<Note>? notes = null)
    {
        var row = desk.Store.Find(id);
        if (row == null) return false;
        loading = true;
        var changedSheet = displayedNoteId != id;
        displayedNoteId = id;
        if (TitleBox.Text != row.Note.Title) TitleBox.Text = row.Note.Title;
        if (BodyBox.Text != row.Note.Body) { var caret = BodyBox.CaretIndex; BodyBox.Text = row.Note.Body; BodyBox.CaretIndex = Math.Min(caret, BodyBox.Text.Length); }
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
        MinimizeButton.Visibility = StackId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        PeelHandle.Visibility = desk.Settings.StackedNotes ? Visibility.Visible : Visibility.Collapsed;
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
        desk.Store.SaveText(NoteId, TitleBox.Text == shownTitle ? null : TitleBox.Text,
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
    }

    public void RaiseForWork()
    {
        IsRaised = true;
        raisedAt = Environment.TickCount64;
        ApplyLayer();
    }
    public void CloseFromController() { controllerClose = true; Close(); }
    private void ApplyLayout()
    {
        var compact=StickyLayout.IsCompact(Width,Height);
        var stacked=StackBack1.Visibility==Visibility.Visible;
        StickyLayout.Apply(SheetContent,compact);
        Paper.Margin=compact?new Thickness(8,8,8,stacked?20:8):new Thickness(14,14,14,stacked?30:14);
        StackBack1.Margin=compact?new Thickness(13,15,5,10):new Thickness(21,26,9,12);
        StackBack2.Margin=compact?new Thickness(18,21,2,3):new Thickness(28,34,5,5);
    }
    private bool MotionEnabled => desk.Settings.Animations && SystemParameters.ClientAreaAnimation;

    private static DoubleAnimation Settle(double from, double to, int milliseconds) => new(
        from, to, TimeSpan.FromMilliseconds(milliseconds))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        FillBehavior = FillBehavior.Stop
    };

    private void RevealPaper()
    {
        if (!MotionEnabled) return;
        ResetPaperMotion();
        Paper.RenderTransformOrigin = new Point(.5, .28);
        const int duration = 360;
        // The face stays solid while the edge uncurls and the shadow settles onto the stack.
        AnimateCurl(.95, 0, duration);
        PaperSlide.BeginAnimation(TranslateTransform.YProperty, Settle(-12, 0, duration));
        PaperTilt.BeginAnimation(RotateTransform.AngleProperty, Settle(-2.4, 0, duration));
        PaperScale.BeginAnimation(ScaleTransform.ScaleXProperty, Settle(.94, 1, duration));
        PaperScale.BeginAnimation(ScaleTransform.ScaleYProperty, Settle(.89, 1, duration));
        PaperShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ShadowDepthProperty, Settle(15, 7, duration));
        ResizeCorner.BeginAnimation(OpacityProperty, Settle(0, 1, duration));
    }

    internal void DragStarted(bool wholeStack)
    {
        SavePosition();
        positionTimer.Stop();
        dragging = true;
        peeling = !wholeStack && desk.Settings.StackedNotes;
        CloseTools();
        BodyBox.EndEdit();
        Flush();
        if (!desk.Settings.DesktopLayer) Topmost = true;
        if (MotionEnabled)
        {
            ResetPaperMotion();
            Paper.RenderTransformOrigin = DragPin;
            dragResponse.Grab();
        }
        desk.BeginSheetDrag(this, peeling);
    }

    internal void DragMoved(Point pointer, Vector velocity)
    {
        dragResponse.Follow(velocity);
        desk.PreviewSheetDrop(this, pointer);
    }

    internal void DragFinished(Point? pointer, Rect origin)
    {
        dragResponse.Release();
        try { desk.FinishSheetDrag(this, pointer, origin, peeling); }
        finally
        {
            dragging = false;
            if (IsVisible)
            {
                ApplyLayer();
                if (pointer != null) SavePosition();
            }
        }
    }

    internal void ShowLooseFace()
    {
        StackNavigation.Visibility = StackBack1.Visibility = StackBack2.Visibility = MinimizeButton.Visibility = Visibility.Collapsed;
        ApplyLayout();
    }

    internal Rect PaperScreenBounds()
    {
        if (Paper.ActualWidth <= 0 || Paper.ActualHeight <= 0 || PresentationSource.FromVisual(Paper) == null) return Rect.Empty;
        return new Rect(Paper.PointToScreen(new Point()), Paper.PointToScreen(new Point(Paper.ActualWidth, Paper.ActualHeight)));
    }

    internal void HighlightDrop(bool highlight) => DropOutline.Visibility = highlight ? Visibility.Visible : Visibility.Collapsed;

    internal void PlaceBehind(StickyWindow source)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var above = new WindowInteropHelper(source).Handle;
        Native.SetWindowPos(handle, above, 0, 0, 0, 0, 0x0013); // Keep bounds and focus.
    }

    private void PaintPull(Vector pull, double grip)
    {
        var strength = pull.Length;
        var paperPull = peeling ? new Vector(pull.X, Math.Min(pull.Y, -.65 * grip)) : pull;
        Paper.DragPull = paperPull;
        PaperSlide.X = PaperSlide.Y = 0;
        PaperScale.ScaleX = 1 - .055 * Math.Abs(pull.X) - .020 * strength;
        PaperScale.ScaleY = 1 - .075 * Math.Abs(pull.Y) - .023 * strength - .014 * grip;
        PaperTilt.Angle = SwingAngle(pull.X, Paper.ActualWidth, Paper.ActualHeight, Paper.Margin, PaperScale.ScaleX, PaperScale.ScaleY);
        rearPull1 = pull == default && grip == 0 ? default : rearPull1 + (pull - rearPull1) * .45;
        rearPull2 = pull == default && grip == 0 ? default : rearPull2 + (rearPull1 - rearPull2) * .35;
        PaintRear(StackBack1, BackScale1, BackTilt1, rearPull1, .83);
        PaintRear(StackBack2, BackScale2, BackTilt2, rearPull2, .67);
        PaperShadow.ShadowDepth = 7 + Math.Round(5 * strength + 3 * grip);
        var gripInset = -12 * PaperSurface.BottomRightDragLift(paperPull);
        GripSlide.X = GripSlide.Y = gripInset;
    }

    private void PaintRear(Border sheet, ScaleTransform scale, RotateTransform tilt, Vector pull, double weight)
    {
        if (sheet.Visibility != Visibility.Visible || pull == default)
        {
            scale.ScaleX = scale.ScaleY = 1;
            tilt.Angle = 0;
            return;
        }

        // All sheets swing about the front sheet's pin, expressed in each sheet's coordinates.
        scale.CenterX = tilt.CenterX = Paper.Margin.Left + Paper.ActualWidth * DragPin.X - sheet.Margin.Left;
        scale.CenterY = tilt.CenterY = Paper.Margin.Top + Paper.ActualHeight * DragPin.Y - sheet.Margin.Top;
        scale.ScaleX = 1 - weight * (.025 * pull.Length + .05 * Math.Abs(pull.X));
        scale.ScaleY = 1 - weight * (.03 * pull.Length + .06 * Math.Abs(pull.Y));
        var angle = SwingAngle(pull.X, Paper.ActualWidth, Paper.ActualHeight, Paper.Margin, scale.ScaleX, scale.ScaleY) * weight;
        // The rear corners sit closer to the window edges. Reduce their swing before they clip.
        var rect = scale.TransformBounds(new Rect(0, 0, sheet.ActualWidth, sheet.ActualHeight));
        for (var i = 0; i < 7; i++)
        {
            var rotation = Matrix.Identity;
            rotation.RotateAt(angle, tilt.CenterX, tilt.CenterY);
            var bounds = Rect.Transform(rect, rotation);
            if (bounds.Left + sheet.Margin.Left >= 0 && bounds.Right + sheet.Margin.Left <= ActualWidth &&
                bounds.Top + sheet.Margin.Top >= 0 && bounds.Bottom + sheet.Margin.Top <= ActualHeight)
            {
                tilt.Angle = angle;
                return;
            }
            angle *= .65;
        }
        tilt.Angle = 0;
    }

    internal static double SwingAngle(double horizontal, double width, double height, Thickness margin, double scaleX, double scaleY)
    {
        var halfWidth = width * .5;
        var swingRoom = Math.Min(margin.Left, margin.Right) + halfWidth * (1 - scaleX);
        var upperRoom = margin.Top + height * DragPin.Y * (1 - scaleY);
        var limit = Math.Min(Math.Atan2(swingRoom, height * (1 - DragPin.Y) * scaleY), Math.Atan2(upperRoom, halfWidth * scaleX));
        return Math.Min(8, limit * 180 / Math.PI) * horizontal;
    }

    private void ResetPaperMotion()
    {
        dragResponse.Stop();
        Paper.BeginAnimation(PaperSurface.CurlLiftProperty, null);
        Paper.CurlLift = 0;
        PaperSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PaperSlide.BeginAnimation(TranslateTransform.YProperty, null);
        PaperTilt.BeginAnimation(RotateTransform.AngleProperty, null);
        PaperScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PaperScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PaperShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ShadowDepthProperty, null);
        GripSlide.BeginAnimation(TranslateTransform.XProperty, null);
        GripSlide.BeginAnimation(TranslateTransform.YProperty, null);
        ResizeCorner.BeginAnimation(OpacityProperty, null);
        PaintPull(default, 0);
    }

    private void AnimateCurl(double from, double to, int duration)
    {
        Paper.BeginAnimation(PaperSurface.CurlLiftProperty, Settle(from, to, duration));
        // Keep the grip on the folded corner as the sheet's outline changes.
        var inset = Settle(-12 * from, -12 * to, duration);
        GripSlide.BeginAnimation(TranslateTransform.XProperty, inset);
        GripSlide.BeginAnimation(TranslateTransform.YProperty, inset);
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
        {
            // A short settle over the stationary stack, without the opening curl or a fade.
            PaperSlide.BeginAnimation(TranslateTransform.XProperty, Settle(Math.Sign(direction) * 5, 0, 145));
            PaperShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ShadowDepthProperty, Settle(10, 7, 145));
        }
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
        Crumple.Margin = Paper.Margin;
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
        OpenTools((FrameworkElement)sender,true);
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
        OpenTools((FrameworkElement)sender,false);
    }
    private void ResizeCornerDrag(object sender,System.Windows.Controls.Primitives.DragDeltaEventArgs e){Width=Math.Clamp(Width+e.HorizontalChange,MinWidth,1600);Height=Math.Clamp(Height+e.VerticalChange,MinHeight,1400);}
    private void SetNoteSize(Size size){Width=Math.Clamp(size.Width,MinWidth,1600);Height=Math.Clamp(size.Height,MinHeight,1400);SavePosition();}
    private void OpenTools(FrameworkElement target, bool colorsOnly) =>
        ShowTools(StickyTools.Create(desk, NoteId, target, ApplyStyle, colorsOnly, SetNoteSize));

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
