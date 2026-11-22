using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using PaperDesk.Desktop.Controls;
using PaperDesk.Desktop.Views;
using PaperDesk.Desktop.Win32Interop;

namespace PaperDesk.Desktop.Services;

public sealed partial class DeskController : IDisposable
{
    public string DataRoot { get; }
    public Settings Settings { get; private set; }
    internal bool UseNativeEditor { get; }
    public NoteStore Store { get; private set; } = null!;
    public string Status { get; private set; } = L.T("Локальный режим");
    public string StorageLocation => L.T("На этом компьютере");
    public event Action? Changed;
    private readonly Dictionary<Guid, StickyWindow> stickies = new();
    private readonly HashSet<Guid> minimizedStacks = [];
    private StickyWindow? draggedSheet, revealedSheet, dropTarget;
    public string HotkeyWarning { get; private set; } = "";
    private readonly Forms.NotifyIcon tray;
    private readonly HotkeyHost hotkey;
    private readonly HotkeyHost summonHotkey;
    private ManagerWindow? manager;
    private SettingsWindow? settingsWindow;
    private readonly Dictionary<(NoteStore, Guid), UnsavedNoteWindow> unsavedEdits = [];
    internal bool HasUnsavedEdits => unsavedEdits.Count > 0;
    private readonly DispatcherTimer refresh;
    private readonly DispatcherTimer backupTimer;
    private Task? backupTask;
    private long changesSinceStart, backupRevision;
    internal string BackupFolder { get; private set; } = "";
    private readonly object refreshGate = new();
    private readonly HashSet<Guid> editedNotes = [];
    private bool refreshQueued, fullRefresh;
    internal IReadOnlyList<Guid>? RefreshingEdits { get; private set; }
    private bool disposed;
    private IReadOnlyList<Note>? undo;
    private bool notesSuppressed;
    public bool CanUndo => undo != null;

    public DeskController(string root)
    {
        DataRoot = root; Directory.CreateDirectory(root);
        backupTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        backupTimer.Tick += (_, _) => QueueBackup();
        refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
        refresh.Tick += (_, _) =>
        {
            refresh.Stop();
            Guid[]? edits;
            lock (refreshGate)
            {
                edits = fullRefresh ? null : editedNotes.ToArray();
                editedNotes.Clear();
                fullRefresh = refreshQueued = false;
            }
            if (disposed) return;
            if (edits == null) ReconcileWindows();
            else foreach (var id in edits)
                if (stickies.TryGetValue(id, out var window) && !window.IsCrumpling) window.RefreshEditedContent();
            RefreshingEdits = edits;
            try { Changed?.Invoke(); }
            finally { RefreshingEdits = null; }
        };
        var settingsPath = Path.Combine(root, "settings.json");
        var newProfile = !File.Exists(settingsPath) && !Directory.Exists(Path.Combine(root, "profiles"));
        var saved = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        Settings = saved is null ? new Settings() : Json.Read<Settings>(saved);
        var (profile, owner) = OfflineProfile.Resolve(root, saved, Settings.LocalProfile);
        Settings = Settings with { LocalProfile = owner == Guid.Empty ? null : profile };
        UseNativeEditor = Settings.NativeEditor;
        ThemePalette.Apply(Settings.Theme);
        OpenStore(profile, owner);
        if (newProfile)
        {
            Settings = Settings with { WelcomePending = true };
            SaveSettings();
        }
        var menu = new TrayMenu();
        tray = new Forms.NotifyIcon { Text = "PaperDesk", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        menu.Items.Add(L.T("Новый листик"), null, (_, _) => NewNote());
        menu.Items.Add(L.T("Все заметки"), null, (_, _) => ShowManager());
        var visibility = menu.Items.Add(L.T("Скрыть листики"), null, (_, _) => ToggleDesktopNotes());
        menu.Items.Add(L.T("Параметры"), null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход"), null, (_, _) => Exit());
        foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
            item.Padding = new Forms.Padding(8, 3, 14, 3);
        menu.Opening += (_, _) =>
        {
            menu.ApplyTheme(ThemePalette.Find(Settings.Theme));
            visibility.Text = L.T(notesSuppressed || (minimizedStacks.Count > 0 && !stickies.Values.Any(w => w.IsVisible))
                ? "Вернуть листики" : "Скрыть листики");
            visibility.Enabled = notesSuppressed || stickies.Count > 0;
        };
        tray.DoubleClick += (_, _) => ShowManager();
        hotkey = new HotkeyHost(NewNote);
        summonHotkey = new HotkeyHost(SummonNotes);
        RegisterAtStartup(hotkey, Settings.Hotkey, L.T("Новый листик"));
        RegisterAtStartup(summonHotkey, Settings.SummonHotkey, L.T("Вызвать заметки"));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerModeChanged;
        SystemParameters.StaticPropertyChanged += SystemParametersChanged;
    }
    private void RegisterAtStartup(HotkeyHost host, string shortcut, string action)
    {
        try { host.Register(shortcut); }
        catch (Exception ex) { HotkeyWarning += $"{action} — {ex.Message}\n"; }
    }
    public void Start()
    {
        backupTimer.Start();
        notesSuppressed = !Settings.ShowNotesAtStartup;
        ReconcileWindows();
        Status = L.T("Локальный режим • сохранено на диске");
    }
    private void OpenStore(string profile, Guid user)
    {
        undo = null;
        var folder = Path.Combine(DataRoot, "profiles", profile);
        Store = new NoteStore(Path.Combine(folder, "notes.db"), user);
        BackupFolder = Path.Combine(folder, "backups");
        Interlocked.Increment(ref changesSinceStart);
        Store.Updated += StoreChanged;
    }
    private void StoreChanged(Guid? editedId)
    {
        Interlocked.Increment(ref changesSinceStart);
        QueueRefresh(editedId);
    }

    private void QueueBackup()
    {
        var revision = Interlocked.Read(ref changesSinceStart);
        if (disposed || revision == Interlocked.Read(ref backupRevision) || backupTask is { IsCompleted: false }) return;
        var store = Store;
        var folder = BackupFolder;
        backupTask = Task.Run(() =>
        {
            try
            {
                if (ProfileBackups.Capture(store, folder, DateTimeOffset.UtcNow))
                    Interlocked.Exchange(ref backupRevision, revision);
            }
            catch (ObjectDisposedException) { } // The application closed before the backup began.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                ErrorLog.Write(DataRoot, ErrorLog.Area.Backup, error);
            }
        });
    }

    private void QueueRefresh(Guid? editedId = null)
    {
        lock (refreshGate)
        {
            if (editedId is Guid id) editedNotes.Add(id);
            else fullRefresh = true;
            if (refreshQueued) return;
            refreshQueued = true;
        }
        if (refresh.Dispatcher.CheckAccess()) Schedule();
        else refresh.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)Schedule);

        void Schedule()
        {
            if (!disposed) refresh.Start();
        }
    }
    public void UpdateSettings(Settings value)
    {
        var previous = hotkey.Shortcut;
        hotkey.Register(value.Hotkey);
        try { summonHotkey.Register(value.SummonHotkey); }
        catch
        {
            if (previous == null) hotkey.Clear();
            else hotkey.Register(previous);
            throw;
        }
        HotkeyWarning = "";
        if (Settings.StackedNotes != value.StackedNotes) { FlushWindows(); CloseStickies(); minimizedStacks.Clear(); }
        Settings = value; SaveSettings();
        ApplyMotionPreference();
        using var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
        if (value.AutoStart) run.SetValue("PaperDesk", "\"" + Environment.ProcessPath + "\" --startup --data \"" + DataRoot + "\""); 
        else if (run.GetValue("PaperDesk") is string command && command.StartsWith("\"" + Environment.ProcessPath + "\" ", StringComparison.OrdinalIgnoreCase))
            run.DeleteValue("PaperDesk", false);
        foreach (var w in stickies.Values) w.ApplyLayer();
        ReconcileWindows();
        Changed?.Invoke();
    }
    internal void SetTheme(string id)
    {
        var theme = ThemePalette.Find(id);
        if (theme.Id == Settings.Theme) return;
        var updated = Settings with { Theme = theme.Id };
        SaveSettings(updated);
        Settings = updated;
        ThemePalette.Apply(theme.Id);
    }

    private void SaveSettings(Settings? value = null) { var p = Path.Combine(DataRoot, "settings.json"); File.WriteAllText(p + ".tmp", Json.Write(value ?? Settings)); File.Move(p + ".tmp", p, true); }
    internal void DismissWelcome()
    {
        if (!Settings.WelcomePending) return;
        Settings = Settings with { WelcomePending = false };
        SaveSettings();
    }
    internal Guid? CreateWelcomeExample()
    {
        if (!Settings.WelcomePending) return null;
        var note = new Note
        {
            Title = L.T("Первый листик"),
            Body = L.T("- [ ] Отметьте этот пункт\n- [ ] Покажите листик на рабочем столе\n- [ ] Перетащите за верхнюю ручку на другой листик, чтобы собрать стопку\n\nПотяните нижнюю ручку, чтобы отделить листик. Эту заметку можно изменить или отправить в корзину.")
        };
        Store.Save(note);
        DismissWelcome();
        return note.Id;
    }
    internal Guid CreateNote()
    {
        var note = new Note();
        Store.Save(note);
        return note.Id;
    }

    public void NewNote()
    {
        var id = CreateNote();
        var count = stickies.Count;
        Store.SavePlacement(new Placement { NoteId = id, Visible = true, X = 110 + count % 7 * 28, Y = 110 + count % 7 * 28 });
        ShowSticky(id);
    }
    public void ShowSticky(Guid id)
    {
        var n = Store.Find(id); if (n == null || n.Note.DeletedAt != null || n.Note.ArchivedAt != null) return;
        notesSuppressed = false;
        var placement = Store.GetPlacement(id);
        var group = SurfaceStack(placement);
        foreach (var leaving in stickies.Values.Where(w => w.IsCrumpling && (group != null ? w.StackId == group : w.NoteId == id)).ToList())
            leaving.CloseFromController();
        Store.SavePlacement(placement with { Visible = true });
        if (group is Guid stackId)
        {
            minimizedStacks.Remove(stackId);
            SelectTop(stackId, id);
        }
        ReconcileWindows();
        if (stickies.TryGetValue(id, out var window)) { window.Activate(); window.FocusEditor(); }
    }

    internal bool PlaceOnDesktop(Guid id, Point screenPoint, Matrix fromDevice, Point? grip = null)
    {
        if (draggedSheet != null || Store.Find(id)?.Note is not { ArchivedAt: null, DeletedAt: null }) return false;
        if (stickies.TryGetValue(id, out var previous))
        {
            if (previous.IsCrumpling) return false;
            previous.Flush();
        }

        var p = Store.GetPlacement(id);
        var size = new Size(Math.Clamp(p.Width, StickyLayout.MinWidth, 1600), Math.Clamp(p.Height, StickyLayout.MinHeight, 1400));
        var bounds = StickyLayout.DesktopDropBounds(screenPoint, fromDevice, size, grip);
        Store.SavePlacement(p with { Visible = true, StackId = null, X = bounds.Left, Y = bounds.Top, Width = size.Width, Height = size.Height });
        notesSuppressed = false;
        ReconcileWindows(animateOpening: false);
        if (stickies.TryGetValue(id, out var window))
        {
            var toDevice = fromDevice;
            toDevice.Invert();
            var pixel = toDevice.Transform(bounds.TopLeft);
            Native.MoveWithoutActivate(new WindowInteropHelper(window).Handle, (int)Math.Round(pixel.X), (int)Math.Round(pixel.Y));
        }
        Changed?.Invoke();
        return true;
    }

    internal StickyWindow? FindCardDropTarget(Guid id, Native.CursorPoint point)
    {
        if (!Settings.StackedNotes || draggedSheet != null) return null;
        var hit = Native.WindowFromPoint(point);
        // On the desktop layer Explorer is the root HWND. Look for the sheet along the parent chain.
        const uint parentWindow = 1;
        for (var handle = hit; handle != 0; handle = Native.GetAncestor(handle, parentWindow))
        {
            if (HwndSource.FromHwnd(handle)?.RootVisual is not StickyWindow window) continue;
            return window.NoteId != id && stickies.GetValueOrDefault(window.NoteId) == window && window.IsVisible &&
                !window.IsCrumpling && window.PaperScreenBounds().Contains(new Point(point.X, point.Y)) && IsLiveSheet(window)
                ? window : null;
        }
        return null;
    }

    internal bool StackCard(Guid id, StickyWindow target, StickyWindow? preview = null)
    {
        if (!Settings.StackedNotes || draggedSheet != null || id == target.NoteId ||
            !target.IsVisible || target.IsCrumpling || !ReferenceEquals(stickies.GetValueOrDefault(target.NoteId), target) ||
            !IsLiveSheet(target) || Store.Find(id)?.Note is not { ArchivedAt: null, DeletedAt: null }) return false;
        if (stickies.TryGetValue(id, out var previous))
        {
            if (previous.IsCrumpling) return false;
            previous.Flush();
        }

        var placements = new List<Placement>();
        var stack = StackForDrop(target, id, placements);
        placements.Add(Store.GetPlacement(id) with { Visible = true, StackId = stack.Id });
        Store.SavePlacements(placements, stack);
        var previousTop = target.NoteId;
        target.StackId = stack.Id;
        ReconcileWindows(animateOpening: false);
        if (preview != null) target.SettleOnStack(previousTop, preview.PaperScreenBounds(), PaperPose.Flat);
        Changed?.Invoke();
        return true;
    }

    private DesktopStack StackForDrop(StickyWindow target, Guid topNoteId, List<Placement> placements, Guid? movingStack = null)
    {
        target.Flush();
        Guid[] front = movingStack is Guid sourceGroup ? FromFace(sourceGroup, topNoteId) : [topNoteId];
        Guid[] back = target.StackId is Guid targetGroup ? FromFace(targetGroup, target.NoteId) : [target.NoteId];
        var order = front.Concat(back).Distinct().ToArray();
        if ((target.StackId ?? movingStack) is Guid existing)
        {
            if (target.StackId == null)
                placements.Add(Store.GetPlacement(target.NoteId) with { StackId = existing });
            return Stack(existing) with
            {
                ActiveNoteId = topNoteId, SheetOrder = order, X = target.Left, Y = target.Top, Width = target.Width, Height = target.Height
            };
        }

        var group = Guid.NewGuid();
        var names = StackMenu.NonemptyStacks(Store).Select(stack => stack.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var number = 1;
        while (names.Contains(L.F("Стопка {0}", number))) number++;
        placements.Add(Store.GetPlacement(target.NoteId) with { StackId = group });
        return new DesktopStack(group, L.F("Стопка {0}", number), target.Left, target.Top,
            target.Width, target.Height, topNoteId) { SheetOrder = order };
    }

    private Guid? SurfaceStack(Placement p) => Settings.StackedNotes ? p.StackId : null;
    public Guid? SurfaceStack(Guid noteId) => SurfaceStack(Store.GetPlacement(noteId));
    internal bool IsOnDesktop(Placement placement) => placement.Visible && !notesSuppressed &&
        !(SurfaceStack(placement) is Guid group && minimizedStacks.Contains(group));
    public DesktopStack Stack(Guid id) => Store.GetStack(id) ?? new DesktopStack(id, "", Settings.StackX,
        Settings.StackY, Settings.StackWidth, Settings.StackHeight, id == Guid.Empty ? Settings.StackActiveNoteId : Guid.Empty);
    public string StackName(Guid id) => id == Guid.Empty ? L.T("Общая стопка") : Stack(id).Name;

    private IEnumerable<(Note Note, Placement Placement)> VisibleNotes()
    {
        var placements = Store.AllPlacements();
        foreach (var row in Store.All())
            if (row.Note is { ArchivedAt: null, DeletedAt: null } note && placements.TryGetValue(note.Id, out var p) && p.Visible)
                yield return (note, p);
    }
    public IReadOnlyList<Note> StackNotes(Guid stackId) =>
        OrderStack(stackId, VisibleNotes().Where(row => row.Placement.StackId == stackId).Select(row => row.Note));

    private IReadOnlyList<Note> OrderStack(Guid stackId, IEnumerable<Note> notes)
    {
        var byDate = NoteStack.Order(notes);
        if (Stack(stackId).SheetOrder is not { Length: > 0 } order) return byDate;
        var remaining = byDate.ToDictionary(note => note.Id);
        var ordered = new List<Note>(byDate.Count);
        foreach (var id in order)
            if (remaining.Remove(id, out var note)) ordered.Add(note);
        ordered.AddRange(byDate.Where(note => remaining.ContainsKey(note.Id)));
        return ordered;
    }

    private Guid[] FromFace(Guid group, Guid face)
    {
        var placements = Store.AllPlacements();
        var ordered = OrderStack(group, Store.All()
            .Where(row => placements.GetValueOrDefault(row.Note.Id)?.StackId == group)
            .Select(row => row.Note)).Select(note => note.Id).ToArray();
        var start = Array.IndexOf(ordered, face);
        return start < 0 ? [face, .. ordered] : [.. ordered.Skip(start), .. ordered.Take(start)];
    }

    internal void BeginSheetDrag(StickyWindow source, bool peel)
    {
        if (!Settings.StackedNotes) return;
        draggedSheet = source;
        if (!peel || source.StackId is not Guid group) return;
        var notes = StackNotes(group);
        if (notes.Count < 2) return;
        var next = NoteStack.NextInOrder(notes, source.NoteId, 1);
        var under = new StickyWindow(this, next, animateOpening: false)
        {
            ShowActivated = false,
            IsHitTestVisible = false
        };
        revealedSheet = under;
        under.RefreshContent(notes.Where(note => note.Id != source.NoteId).ToList());
        under.Show();
        if (draggedSheet != source) return;
        under.PlaceBehind(source);
        source.ShowLooseFace();
    }

    internal void PreviewSheetDrop(StickyWindow source, Point pointer)
    {
        if (draggedSheet != source) return;
        var target = FindDropTarget(source, pointer);
        if (target == dropTarget) return;
        dropTarget?.HighlightDrop(false);
        dropTarget = target;
        dropTarget?.HighlightDrop(true);
    }

    internal Vector? SnapOffset(StickyWindow source, Rect bounds)
    {
        if (!Settings.SnapNotes || dropTarget != null || bounds.IsEmpty) return null;
        var neighbours = stickies.Values.Where(window => window != source && window.IsVisible && !window.IsCrumpling)
            .Select(window => window.SheetFrameBounds());
        var dpi = VisualTreeHelper.GetDpi(source).DpiScaleX;
        return PaperDrag.SnapOffset(bounds, neighbours, 8 * dpi, 12 * dpi);
    }

    private StickyWindow? FindDropTarget(StickyWindow source, Point pointer) =>
        stickies.Values.Append(revealedSheet).OfType<StickyWindow>()
            .Where(window => window != source && window.IsVisible && !window.IsCrumpling)
            .Select(window => (Window: window, Bounds: window.PaperScreenBounds()))
            .Where(row => row.Bounds.Contains(pointer))
            .OrderBy(row => (pointer - new Point(row.Bounds.X + row.Bounds.Width / 2, row.Bounds.Y + row.Bounds.Height / 2)).LengthSquared)
            .Select(row => row.Window).FirstOrDefault(IsLiveSheet);

    internal void FinishSheetDrag(StickyWindow source, Point? pointer, Rect origin, bool peel)
    {
        if (draggedSheet != source) return;
        var target = pointer is Point dropPoint ? FindDropTarget(source, dropPoint) : null;
        var revealed = revealedSheet;
        dropTarget?.HighlightDrop(false);
        draggedSheet = revealedSheet = dropTarget = null;
        var placement = Store.GetPlacement(source.NoteId);
        if (pointer == null || (revealed != null && target == revealed) || !IsLiveSheet(source))
        {
            revealed?.CloseFromController();
            source.Left = origin.Left;
            source.Top = origin.Top;
            ReconcileWindows(animateOpening: false);
            return;
        }

        if (target == null && revealed == null)
        {
            ReconcileWindows(animateOpening: false);
            return;
        }

        var placements = new List<Placement>();
        var stacks = new List<DesktopStack>();
        Guid? destination = null;
        if (target != null)
        {
            var movingStack = peel ? null : source.StackId;
            var stack = StackForDrop(target, source.NoteId, placements, movingStack);
            destination = stack.Id;
            stacks.Add(stack);
            if (movingStack is Guid group && group != stack.Id)
            {
                var existing = Store.AllPlacements();
                foreach (var row in Store.All())
                    if (row.Note.Id != source.NoteId && row.Note is { ArchivedAt: null, DeletedAt: null } &&
                        existing.GetValueOrDefault(row.Note.Id) is { } member && member.StackId == group)
                        placements.Add(member with { StackId = stack.Id });
            }
        }
        placements.Add(placement with
        {
            StackId = destination, X = source.Left, Y = source.Top, Width = source.Width, Height = source.Height
        });
        if (revealed?.StackId is Guid originStack)
            stacks.Add(Stack(originStack) with { ActiveNoteId = revealed.NoteId });

        try { Store.SavePlacements(placements, stacks.ToArray()); }
        catch
        {
            revealed?.CloseFromController();
            source.Left = origin.Left;
            source.Top = origin.Top;
            ReconcileWindows(animateOpening: false);
            throw;
        }

        var incomingBounds = source.PaperScreenBounds();
        var incomingPose = source.CurrentPaperPose;
        var previousTop = target?.NoteId;
        if (target != null)
        {
            // Keep the destination HWND and its editor in place as the top note changes.
            source.CloseFromController();
            target.StackId = destination;
        }
        else source.StackId = destination;
        if (revealed != null)
        {
            revealed.IsHitTestVisible = true;
            RegisterSticky(revealed);
        }
        ReconcileWindows(animateOpening: false);
        if (target != null && previousTop is Guid oldTop) target.SettleOnStack(oldTop, incomingBounds, incomingPose);
    }

    private bool IsLiveSheet(StickyWindow window) => Store.GetPlacement(window.NoteId).Visible &&
        Store.Find(window.NoteId)?.Note is { ArchivedAt: null, DeletedAt: null };

    public Placement WindowPlacement(Guid id, Guid? stackId)
    {
        var p = Store.GetPlacement(id);
        if (stackId is not Guid group) return p;
        var stack = Stack(group);
        return p with { X = stack.X, Y = stack.Y, Width = stack.Width, Height = stack.Height };
    }
    public void SaveStackBounds(Guid id, double x, double y, double width, double height)
    {
        var stack = Stack(id);
        if (Math.Abs(stack.X - x) + Math.Abs(stack.Y - y) + Math.Abs(stack.Width - width) + Math.Abs(stack.Height - height) < .5) return;
        Store.SaveStack(stack with { X = x, Y = y, Width = width, Height = height });
    }
    private void SelectTop(Guid stackId, Guid id)
    {
        var stack = Stack(stackId);
        if (stack.ActiveNoteId != id) Store.SaveStack(stack with { ActiveNoteId = id });
    }

    public Guid CreateStack(string name)
    {
        var id = Guid.NewGuid();
        var bounds = FreeSpace(330, 370, OccupiedBounds());
        Store.SaveStack(new DesktopStack(id, name, bounds.X, bounds.Y, bounds.Width, bounds.Height));
        return id;
    }

    internal void RenameStack(Guid id, string name)
    {
        if (Store.GetStack(id) is not { } stack || string.IsNullOrWhiteSpace(name) || stack.Name == name) return;
        Store.SaveStack(stack with { Name = name });
        ReconcileWindows();
        Changed?.Invoke();
    }

    public void AssignStack(IReadOnlyList<Guid> ids, Guid? target)
    {
        if (!Settings.StackedNotes || (target is Guid stack && stack != Guid.Empty && Store.GetStack(stack) == null)) return;
        var notes = ids.Distinct().Where(id => Store.Find(id)?.Note is { ArchivedAt: null, DeletedAt: null }).ToList();
        if (notes.Count == 0) return;
        FlushWindows();
        notesSuppressed = false;
        var occupied = OccupiedBounds();
        var placements = new List<Placement>();
        foreach (var id in notes)
        {
            var p = Store.GetPlacement(id);
            if (target == null && (p.StackId != null || !p.Visible))
            {
                var bounds = FreeSpace(p.Width, p.Height, occupied);
                occupied.Add(bounds);
                p = p with { X = bounds.X, Y = bounds.Y };
            }
            placements.Add(p with { Visible = true, StackId = target });
        }
        if (target is Guid group)
        {
            var destination = Stack(group);
            var order = notes.Concat(FromFace(group, destination.ActiveNoteId)).Where(id => id != Guid.Empty).Distinct().ToArray();
            Store.SavePlacements(placements, destination with { ActiveNoteId = notes[0], SheetOrder = order });
            minimizedStacks.Remove(group);
        }
        else Store.SavePlacements(placements);
        ReconcileWindows();
    }

    internal void ExtractFromStack(IEnumerable<Guid> ids) =>
        AssignStack(ids.Where(id => Store.GetPlacement(id).StackId.HasValue).ToArray(), null);

    private List<Rect> OccupiedBounds() => stickies.Values.Where(w => w.IsVisible)
        .Select(w => new Rect(w.Left, w.Top, w.Width, w.Height)).ToList();

    private static Rect FreeSpace(double width, double height, List<Rect> occupied)
    {
        var area = SystemParameters.WorkArea;
        Rect? best = null;
        var leastOverlap = double.PositiveInfinity;
        for (var y = area.Top + 32; y <= Math.Max(area.Top + 32, area.Bottom - height); y += 40)
            for (var x = area.Left + 32; x <= Math.Max(area.Left + 32, area.Right - width); x += 40)
            {
                var candidate = new Rect(x, y, width, height);
                var overlap = occupied.Sum(rect =>
                {
                    rect.Inflate(12, 12);
                    rect.Intersect(candidate);
                    return rect.IsEmpty ? 0 : rect.Width * rect.Height;
                });
                if (overlap == 0) return candidate;
                if (overlap < leastOverlap) { best = candidate; leastOverlap = overlap; }
            }
        return best ?? new Rect(area.Left + 32, area.Top + 32, width, height);
    }

    public void ShowStack(Guid group) => RevealStack(group);

    private bool RevealStack(Guid group, IReadOnlySet<Guid>? hideGroups = null)
    {
        if (!Settings.StackedNotes) return false;
        var placements = Store.AllPlacements();
        var members = Store.All().Where(row => row.Note is { ArchivedAt: null, DeletedAt: null })
            .Select(row => placements.GetValueOrDefault(row.Note.Id) ?? new Placement { NoteId = row.Note.Id, StackId = null })
            .Where(p => p.StackId == group).ToList();
        if (members.Count == 0) return false;
        var changes = members.Where(p => !p.Visible).Select(p => p with { Visible = true });
        if (hideGroups is { Count: > 0 })
        {
            foreach (var window in stickies.Values.Where(window => window.StackId is Guid id && hideGroups.Contains(id)).ToArray())
                window.Flush();
            changes = changes.Concat(placements.Values.Where(p => p.Visible && p.StackId is Guid id && hideGroups.Contains(id))
                .Select(p => p with { Visible = false }));
        }
        Store.SavePlacements(changes);
        notesSuppressed = false;
        minimizedStacks.Remove(group);
        ReconcileWindows();
        var top = stickies.Values.FirstOrDefault(w => w.StackId == group && w.IsVisible);
        if (top == null) return false;
        top.RaiseForWork();
        top.Activate();
        return true;
    }
    public void MoveStack(Guid active, int direction)
    {
        if (SurfaceStack(active) is not Guid group || Stack(group).ActiveNoteId != active) return;
        TurnTo(group, NoteStack.NextInOrder(StackNotes(group), active, direction), Math.Sign(direction));
    }

    public void SelectStackNote(Guid id)
    {
        if (SurfaceStack(id) is not Guid group) return;
        var notes = StackNotes(group).ToList();
        var current = notes.FindIndex(note => note.Id == Stack(group).ActiveNoteId);
        var selected = notes.FindIndex(note => note.Id == id);
        if (selected >= 0) TurnTo(group, id, selected.CompareTo(current));
    }

    private void TurnTo(Guid group, Guid id, int direction)
    {
        if (minimizedStacks.Contains(group) || id == Guid.Empty || id == Stack(group).ActiveNoteId ||
            stickies.Values.Any(w => w.StackId == group && w.IsCrumpling)) return;
        SelectTop(group, id);
        ReconcileWindows(group, direction);
    }
    public void HideSticky(Guid id) { if (stickies.TryGetValue(id, out var w)) w.Flush(); Store.SavePlacement(Store.GetPlacement(id) with { Visible = false }); ReconcileWindows(); }
    public void CloseStack(Guid group)
    {
        if (!Settings.StackedNotes) return;
        foreach (var w in stickies.Values.Where(w => w.StackId == group)) w.Flush();
        var placements = StackNotes(group).Select(note => Store.GetPlacement(note.Id) with { Visible = false }).ToList();
        Store.SavePlacements(placements);
        minimizedStacks.Remove(group);
        ReconcileWindows();
    }
    public void MinimizeStack(Guid group)
    {
        if (!Settings.StackedNotes || !stickies.Values.Any(w => w.StackId == group)) return;
        minimizedStacks.Add(group);
        foreach (var window in stickies.Values.Where(w => w.StackId == group)) { window.Flush(); window.Hide(); }
        Changed?.Invoke();
    }

    public void SummonNotes()
    {
        notesSuppressed = false;
        minimizedStacks.Clear();
        ReconcileWindows();
        Changed?.Invoke();
        if (stickies.Count == 0) { ShowManager(); return; }

        var active = stickies.Values.FirstOrDefault(window => window.IsActive) ?? stickies.Values.First();
        foreach (var window in stickies.Values) window.RaiseForWork();
        active.Activate();
        active.FocusEditor();
    }

    public void ChangeState(Guid id, string action, bool animateSticky = true)
    {
        stickies.TryGetValue(id, out var window);
        window?.Flush();
        var note = Store.Find(id)?.Note;
        if (note == null || (action == "trash" && note.DeletedAt != null)) return;
        var stackId = window?.StackId;
        var successor = stackId is Guid group && Stack(group).ActiveNoteId == id && action == "trash"
            ? NoteStack.NextInOrder(StackNotes(group), id, 1) : Guid.Empty;
        if (successor == id) successor = Guid.Empty;
        var changed = Store.ChangeStates([id], action);
        if (changed.Count == 0) return;
        undo = changed;
        if (successor != Guid.Empty && stackId is Guid nextGroup) SelectTop(nextGroup, successor);
        if (animateSticky && action == "trash" && window != null)
        {
            window.PlayCrumple(successor, revealed =>
            {
                if (revealed && stackId is Guid currentGroup && Stack(currentGroup).ActiveNoteId == successor &&
                    !stickies.ContainsKey(successor) && StackNotes(currentGroup).Any(next => next.Id == successor) &&
                    window.AdoptRevealedNote(successor))
                {
                    stickies.Remove(id);
                    stickies.Add(successor, window);
                }
                ReconcileWindows(animateOpening: !revealed);
            });
            return;
        }
        ReconcileWindows();
    }
    public void ChangeStates(IReadOnlyList<Guid> ids, string action)
    {
        if (ids.Count == 1) { ChangeState(ids[0], action, animateSticky: false); return; }
        FlushWindows();
        var changed = Store.ChangeStates(ids, action);
        if (changed.Count > 0) undo = changed;
        ReconcileWindows();
    }

    public Task PurgeTrash(Guid[] ids)
    {
        if (ids.Length == 0) return Task.CompletedTask;
        Store.PurgeTrash(ids);
        ReconcileWindows();
        return Task.CompletedTask;
    }


    public void Undo()
    {
        if (undo == null) return;
        Store.RestoreStates(undo);
        undo = null;
        ReconcileWindows();
    }
    internal void ToggleDesktopNotes()
    {
        if (!notesSuppressed && minimizedStacks.Count > 0 && !stickies.Values.Any(w => w.IsVisible))
        {
            SummonNotes();
            return;
        }
        notesSuppressed = !notesSuppressed;
        if (notesSuppressed)
        {
            FlushWindows();
            CloseStickies();
        }
        else ReconcileWindows(animateOpening: false);
        Changed?.Invoke();
    }
    public void ShowManager() { if (manager == null) { manager = new ManagerWindow(this); manager.Closed += (_, _) => manager = null; } manager.Show(); manager.WindowState = WindowState.Normal; manager.Activate(); }
    public void ShowSettings()
    {
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        if (!settingsWindow.IsVisible) settingsWindow.Show();
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }
    public void FlushWindows() { foreach (var w in stickies.Values.ToList()) w.Flush(); }

    internal NoteDraft? UnsavedDraft(NoteStore store, Guid id) => unsavedEdits.GetValueOrDefault((store, id))?.Draft;

    internal void SaveEditorText(NoteStore store, Guid id, string? title, string? body, string shownTitle, string shownBody,
        IReadOnlyDictionary<Guid, NoteImageLayout>? layouts = null)
    {
        if (title == null && body == null) return;
        var draft = new NoteDraft(title, body, shownTitle, shownBody, layouts);
        var pending = UnsavedDraft(store, id);
        SaveDraft(store, id, pending == null ? draft : pending.Add(draft));
    }

    private void SaveDraft(NoteStore store, Guid id, NoteDraft draft)
    {
        var key = (store, id);
        var existing = unsavedEdits.GetValueOrDefault(key);
        try
        {
            if (store != Store || !store.SaveText(id, draft.Title, draft.Body, draft.ShownTitle, draft.ShownBody, draft.Layouts))
                throw new IOException("The active profile no longer contains the note.");
            existing?.Complete();
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            if (existing != null) { existing.UpdateDraft(draft); return; }
            ErrorLog.Write(DataRoot, ErrorLog.Area.Save, error);
            var recovery = new UnsavedNoteWindow(draft, () =>
            {
                if (unsavedEdits.TryGetValue(key, out var window)) SaveDraft(store, id, window.Draft);
            });
            unsavedEdits.Add(key, recovery);
            recovery.Closed += (_, _) => unsavedEdits.Remove(key);
            recovery.Show();
        }
    }
    internal void PrepareForExit()
    {
        foreach (var window in stickies.Values) window.PreservePlacementOnClose();
        revealedSheet?.PreservePlacementOnClose();
    }
    internal void Exit()
    {
        if (HasUnsavedEdits &&
            MessageBox.Show(L.T("Есть несохранённые черновики. Завершить работу PaperDesk?"), "PaperDesk", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var window in unsavedEdits.Values.ToArray()) window.Complete();
        PrepareForExit();
        Application.Current.Shutdown();
    }
    private void CloseStickies()
    {
        dropTarget?.HighlightDrop(false);
        var revealed = revealedSheet;
        draggedSheet = revealedSheet = dropTarget = null;
        revealed?.CloseFromController();
        foreach (var window in stickies.Values.ToList()) window.CloseFromController();
        stickies.Clear();
    }
    private void ReconcileWindows(Guid? turningStack = null, int turnDirection = 0, bool animateOpening = true)
    {
        if (notesSuppressed || draggedSheet != null) return;
        var groups = VisibleNotes().ToLookup(row => SurfaceStack(row.Placement), row => row.Note);
        var loose = groups[null].Select(note => note.Id).ToHashSet();
        var groupedNotes = groups.Where(group => group.Key.HasValue)
            .ToDictionary(group => group.Key!.Value, group => OrderStack(group.Key!.Value, group));
        var moved = groups.SelectMany(group => group.Select(note => (note.Id, group.Key)))
            .ToDictionary(row => row.Id, row => row.Key);
        foreach (var w in stickies.Values.ToList())
        {
            if (w.IsCrumpling) continue;
            if ((moved.TryGetValue(w.NoteId, out var destination) && destination != w.StackId)
                || (w.StackId is Guid group ? !groupedNotes.ContainsKey(group) : !loose.Contains(w.NoteId)))
                w.CloseFromController();
        }
        minimizedStacks.RemoveWhere(group => !groupedNotes.ContainsKey(group));
        var currentStacks = stickies.Values.Where(w => w.StackId.HasValue).ToDictionary(w => w.StackId!.Value);
        foreach (var (group, notes) in groupedNotes)
        {
            currentStacks.TryGetValue(group, out var top);
            if (top?.IsCrumpling == true) continue;
            var activeId = Stack(group).ActiveNoteId;
            var active = notes.FirstOrDefault(note => note.Id == activeId) ?? notes[0];
            if (active.Id != activeId) SelectTop(group, active.Id);
            if (top != null && top.NoteId != active.Id)
            {
                var previous = top.NoteId;
                if (top.TrySwitchNote(active.Id, turningStack == group ? turnDirection : 0))
                {
                    stickies.Remove(previous);
                    stickies.Add(active.Id, top);
                }
                else { top.CloseFromController(); top = null; }
            }
            top ??= CreateSticky(active.Id, animateOpening);
            if (!minimizedStacks.Contains(group) && !top.IsVisible) top.Show();
            top.RefreshContent(notes);
        }
        foreach (var id in loose)
        {
            if (!stickies.TryGetValue(id, out var window)) CreateSticky(id, animateOpening).Show();
            else if (!window.IsCrumpling) window.RefreshContent();
        }
    }
    private StickyWindow CreateSticky(Guid id, bool animateOpening)
    {
        var window = new StickyWindow(this, id, animateOpening);
        RegisterSticky(window);
        return window;
    }
    private void RegisterSticky(StickyWindow window)
    {
        stickies.Add(window.NoteId, window);
        window.Closed += (_, _) => stickies.Remove(window.NoteId);
    }
    private void DisplayChanged(object? sender, EventArgs e) => RestoreVisibleWindows();
    private void PowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) RestoreVisibleWindows();
    }

    private void SystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
            refresh.Dispatcher.BeginInvoke((Action)ApplyMotionPreference);
    }

    private void ApplyMotionPreference()
    {
        if (disposed || (Settings.Animations && SystemParameters.ClientAreaAnimation)) return;
        foreach (var window in stickies.Values.ToArray()) window.StopMotion();
        revealedSheet?.StopMotion();
        manager?.StopMotion();
    }
    private void RestoreVisibleWindows()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            foreach (var window in stickies.Values)
            {
                window.ApplyLayer();
                window.EnsureOnScreen();
            }
        });
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; refresh.Stop(); backupTimer.Stop(); FlushWindows(); CloseStickies(); tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); hotkey.Dispose(); summonHotkey.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerModeChanged;
        SystemParameters.StaticPropertyChanged -= SystemParametersChanged;
        Store.Dispose();
    }
}
