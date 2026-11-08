using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PaperDesk.Desktop.Controls;

public sealed class NotePictures : Canvas
{
    private NoteStore? store;
    private Guid? noteId;
    private NoteBodyEditor? body;
    private bool editable;
    private IReadOnlyList<(Guid Id, NoteImageLayout? Layout)> saved = [];
    private readonly List<Picture> pictures = [];
    private Guid? selected;
    private Window? preview;

    public NotePictures()
    {
        ClipToBounds = true;
        Focusable = false;
        SizeChanged += (_, _) => { foreach (var picture in pictures) Place(picture); };
        Unloaded += (_, _) => preview?.Close();
    }

    internal void ConnectDrop(FrameworkElement paper, NoteBodyEditor editor)
    {
        body = editor;
        paper.AllowDrop = true;
        paper.PreviewDragOver += (_, e) =>
        {
            if (!editable || !CanAttach(e.Data)) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
        paper.PreviewDrop += (_, e) =>
        {
            if (!TryAttach(e.Data, e.GetPosition(this))) return;
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
    }

    internal bool Paste()
    {
        try { return Clipboard.GetDataObject() is { } data && TryAttach(data); }
        catch (ExternalException) { ShowError("Буфер обмена занят. Попробуйте ещё раз."); return true; }
    }

    internal void Bind(NoteStore nextStore, Guid? nextId, bool canEdit)
    {
        var nextPictures = nextId is Guid id ? nextStore.ImageLayouts(id) : [];
        var changedNote = store != nextStore || noteId != nextId;
        if (!changedNote && canEdit == editable && saved.SequenceEqual(nextPictures)) return;
        if (changedNote) { preview?.Close(); selected = null; }
        store = nextStore;
        noteId = nextId;
        editable = canEdit;
        saved = nextPictures;
        Children.Clear();
        pictures.Clear();
        if (nextId is not Guid note) return;
        foreach (var (image, position) in saved)
        {
            if (nextStore.ReadImage(note, image) is not { } png || Decode(png, 768) is not { } bitmap) continue;
            var index = pictures.Count;
            var textHeight = body is { Text.Length: > 0 } editor
                ? (editor.Text.Count(c => c == '\n') + 1) * (editor.FontSize * 1.3) : 0;
            var top = ActualHeight > 0 ? Math.Clamp((textHeight + 20) / ActualHeight, .12, .52) : .14;
            var layout = position ?? new NoteImageLayout(.18 + index % 3 * .06, top + index * .06, .64);
            var picture = new Picture(image, layout, (double)bitmap.PixelWidth / bitmap.PixelHeight);
            var content = new Grid();
            content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill, IsHitTestVisible = false });
            picture.Frame.Child = content;
            picture.Frame.Tag = image;
            var move = new Thumb { Template = (ControlTemplate)FindResource("PictureMove"), Cursor = canEdit ? Cursors.SizeAll : Cursors.Hand };
            var hint = L.T(canEdit ? "Перетащите изображение. Двойной щелчок — открыть." : "Открыть изображение");
            move.ToolTip = hint;
            System.Windows.Automation.AutomationProperties.SetName(move, hint);
            move.PreviewMouseLeftButtonDown += (_, e) =>
            {
                Select(picture.Id);
                if (e.ClickCount < 2) return;
                e.Handled = true;
                OpenBound(nextStore, note, image);
            };
            content.Children.Add(move);
            if (canEdit)
            {
                var resize = picture.Resize;
                resize.Template = (ControlTemplate)FindResource("PictureResize");
                resize.ToolTip = L.T("Изменить размер изображения");
                System.Windows.Automation.AutomationProperties.SetName(resize, L.T("Изменить размер изображения"));
                move.DragDelta += (_, e) => MovePicture(picture, e.HorizontalChange, e.VerticalChange);
                resize.DragDelta += (_, e) => ResizePicture(picture, e.HorizontalChange, e.VerticalChange);
                move.DragCompleted += (_, e) => FinishMove(nextStore, note, picture, e.Canceled);
                resize.DragCompleted += (_, e) => FinishMove(nextStore, note, picture, e.Canceled);
                content.Children.Add(resize);
            }
            var menu = new ContextMenu();
            AddMenuItem(menu, "Открыть изображение", () => OpenBound(nextStore, note, image));
            AddMenuItem(menu, "Копировать изображение", () =>
            {
                if (ReadBoundImage(nextStore, note, image) is { } current) Copy(Decode(current));
            });
            if (editable) AddMenuItem(menu, "Удалить изображение", () => Remove(nextStore, note, image));
            picture.Frame.ContextMenu = menu;
            pictures.Add(picture);
            Children.Add(picture.Frame);
            Place(picture);
        }
        Select(selected);
    }

    private sealed class Picture(Guid id, NoteImageLayout layout, double ratio)
    {
        internal Guid Id { get; } = id;
        internal double Ratio { get; } = ratio;
        internal NoteImageLayout Layout { get; set; } = layout;
        internal NoteImageLayout Saved { get; set; } = layout;
        internal Border Frame { get; } = new();
        internal Thumb Resize { get; } = new()
        {
            Width = 22, Height = 22, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.SizeNWSE
        };
    }

    private void Select(Guid? id)
    {
        selected = id;
        foreach (var picture in pictures)
        {
            var focused = editable && picture.Id == id;
            picture.Resize.Visibility = focused ? Visibility.Visible : Visibility.Collapsed;
            picture.Frame.BorderBrush = focused ? PaperPalette.Brush("#694C26") : null;
            picture.Frame.BorderThickness = new Thickness(1);
            SetZIndex(picture.Frame, focused ? 1 : 0);
        }
    }

    private void Place(Picture picture)
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        var layout = picture.Layout;
        var width = Math.Min(layout.Width * ActualWidth, ActualHeight * picture.Ratio);
        var height = width / picture.Ratio;
        picture.Frame.Width = width;
        picture.Frame.Height = height;
        SetLeft(picture.Frame, Math.Clamp(layout.X * ActualWidth, 0, Math.Max(0, ActualWidth - width)));
        SetTop(picture.Frame, Math.Clamp(layout.Y * ActualHeight, 0, Math.Max(0, ActualHeight - height)));
    }

    private void MovePicture(Picture picture, double dx, double dy)
    {
        var frame = picture.Frame;
        var x = Math.Clamp(GetLeft(frame) + dx, 0, Math.Max(0, ActualWidth - frame.Width));
        var y = Math.Clamp(GetTop(frame) + dy, 0, Math.Max(0, ActualHeight - frame.Height));
        picture.Layout = picture.Layout with { X = x / ActualWidth, Y = y / ActualHeight };
        Place(picture);
    }

    private void ResizePicture(Picture picture, double dx, double dy)
    {
        var frame = picture.Frame;
        var x = GetLeft(frame);
        var y = GetTop(frame);
        var limit = Math.Max(0, Math.Min(ActualWidth - x, (ActualHeight - y) * picture.Ratio));
        var delta = Math.Abs(dx) >= Math.Abs(dy * picture.Ratio) ? dx : dy * picture.Ratio;
        var width = Math.Clamp(frame.Width + delta, Math.Min(32, limit), limit);
        picture.Layout = new NoteImageLayout(x / ActualWidth, y / ActualHeight, width / ActualWidth).Bounded();
        Place(picture);
    }

    private void FinishMove(NoteStore target, Guid note, Picture picture, bool canceled)
    {
        if (canceled) { picture.Layout = picture.Saved; Place(picture); return; }
        if (target != store || noteId != note || picture.Layout == picture.Saved) return;
        target.SaveImageLayout(note, picture.Id, picture.Layout);
        picture.Saved = picture.Layout;
        saved = saved.Select(entry => entry.Id == picture.Id ? (entry.Id, (NoteImageLayout?)picture.Layout) : entry).ToArray();
    }

    private void OpenBound(NoteStore target, Guid note, Guid image)
    {
        if (ReadBoundImage(target, note, image) is { } png) OpenImage(png, target, note, image);
    }

    private byte[]? ReadBoundImage(NoteStore target, Guid note, Guid image) =>
        store == target && noteId == note ? target.ReadImage(note, image) : null;

    internal static bool CanAttach(IDataObject data) => data.GetDataPresent(DataFormats.Bitmap) ||
        data.GetDataPresent("PNG", false) || BrowserImageDrop.HasImageFile(data) ||
        data.GetDataPresent(DataFormats.FileDrop, false) &&
        data.GetData(DataFormats.FileDrop, false) is string[] paths && paths.Any(IsImageFile);

    internal bool TryAttach(IDataObject data, Point? drop = null)
    {
        if (!editable || store == null || noteId is not Guid note) return false;
        try
        {
            if (!CanAttach(data)) return false;
            var position = drop is { } point && ActualWidth > 0 && ActualHeight > 0
                ? new NoteImageLayout(point.X / ActualWidth - .32, point.Y / ActualHeight - .12, .64).Bounded() : null;
            if (data.GetData(DataFormats.FileDrop, false) is string[] paths && paths.Any(IsImageFile))
                AttachFiles(paths.Where(IsImageFile).ToArray(), position);
            else if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                Attach(store, note, [Encode(bitmap)], position);
            else if (data.GetData("PNG", false) is byte[] bytes)
            {
                using var input = new MemoryStream(bytes, writable: false);
                Attach(store, note, [ReadFile(input)], position);
            }
            else if (data.GetData("PNG", false) is Stream input)
                Attach(store, note, [ReadFile(input)], position);
            else if (BrowserImageDrop.HasImageFile(data) && BrowserImageDrop.Read(data) is { } browserBytes)
            {
                using var browserImage = new MemoryStream(browserBytes, writable: false);
                Attach(store, note, [ReadFile(browserImage)], position);
            }
            else
                ShowError("Не удалось прочитать изображение.");
        }
        catch (Exception error) when (IsImageError(error)) { ShowError("Не удалось прочитать изображение."); }
        return true;
    }

    internal void Browse()
    {
        if (!editable || store == null || noteId is not Guid note) return;
        var profile = store;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Добавить изображения"), Multiselect = true,
            Filter = L.T("Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp")
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true && profile == store && note == noteId && editable)
            AttachFiles(dialog.FileNames);
    }

    private void AttachFiles(string[] paths, NoteImageLayout? position = null)
    {
        if (store == null || noteId is not Guid note || paths.Length == 0) return;
        if (paths.Length > NoteImage.MaxPerNote - saved.Count) { ShowError("В заметке помещается до восьми изображений."); return; }
        try
        {
            var images = new List<byte[]>();
            foreach (var path in paths)
            {
                if (!IsImageFile(path)) throw new InvalidDataException();
                using var input = File.OpenRead(path);
                images.Add(ReadFile(input));
            }
            Attach(store, note, images, position);
        }
        catch (Exception error) when (IsImageError(error)) { ShowError("Не удалось прочитать изображение."); }
    }

    private void Attach(NoteStore target, Guid note, IReadOnlyList<byte[]> images, NoteImageLayout? position)
    {
        if (!target.AddImages(note, images, position)) { ShowError("В заметке помещается до восьми изображений."); return; }
        Bind(target, note, editable);
        if (pictures.LastOrDefault() is { } picture) Select(picture.Id);
    }

    private void Remove(NoteStore target, Guid note, Guid image)
    {
        if (!editable || store != target || noteId != note) return;
        target.RemoveImage(note, image);
        preview?.Close();
        Bind(target, note, editable);
    }

    private static bool IsImageFile(string path) => path is { Length: > 3 } && path[1] == ':' && Path.IsPathFullyQualified(path) &&
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    private static byte[] ReadFile(Stream input)
    {
        if (!input.CanSeek || input.Length < 12 || input.Length > BrowserImageDrop.MaxInputBytes) throw new InvalidDataException();
        input.Position = 0;
        Span<byte> header = stackalloc byte[12];
        input.ReadExactly(header);
        if (!(header[0] == 137 && header[1..4].SequenceEqual("PNG"u8) ||
            header[..2].SequenceEqual("BM"u8) || header[..4].SequenceEqual("GIF8"u8) ||
            header[0] == 255 && header[1] == 216 || header[..4].SequenceEqual("RIFF"u8) && header[8..].SequenceEqual("WEBP"u8)))
            throw new InvalidDataException();
        input.Position = 0;
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException();
        return Encode(decoder.Frames[0]);
    }

    private static byte[] Encode(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 48_000_000) throw new InvalidDataException();
        var scale = Math.Min(1d, (double)NoteImage.MaxPixelsPerSide / Math.Max(width, height));
        for (var attempt = 0; attempt < 2; attempt++, scale *= .7)
        {
            BitmapSource resized = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0)));
            using var output = new MemoryStream();
            encoder.Save(output);
            var png = output.ToArray();
            if (NoteImage.IsValid(png)) return png;
        }
        throw new InvalidDataException();
    }

    private static BitmapSource? Decode(byte[] png, int width = 0)
    {
        if (!NoteImage.IsValid(png)) return null;
        try
        {
            using var stream = new MemoryStream(png, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            var pixelWidth = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            var pixelHeight = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            if (width > 0 && Math.Max(pixelWidth, pixelHeight) > width)
            {
                if (pixelWidth >= pixelHeight) bitmap.DecodePixelWidth = width;
                else bitmap.DecodePixelHeight = width;
            }
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception error) when (IsImageError(error)) { return null; }
    }

    private void OpenImage(byte[] png, NoteStore target, Guid note, Guid id)
    {
        if (Decode(png) is not { } bitmap) { ShowError("Не удалось прочитать изображение."); return; }
        preview?.Close();
        var owner = Window.GetWindow(this);
        var window = new Window
        {
            Style = (Style)Application.Current.FindResource(typeof(Window)),
            Owner = owner, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = Math.Min(900, SystemParameters.WorkArea.Width - 24),
            Height = Math.Min(720, SystemParameters.WorkArea.Height - 24), Title = L.T("Изображение")
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 10) };
        var copy = new Button { Content = L.T("Копировать изображение") };
        copy.Click += (_, _) => Copy(bitmap);
        actions.Children.Add(copy);
        if (editable)
        {
            var remove = new Button { Content = L.T("Удалить изображение") };
            remove.Click += (_, _) => Remove(target, note, id);
            actions.Children.Add(remove);
        }
        var layout = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        layout.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, Margin = new Thickness(12) });
        window.Content = layout;
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) window.Close();
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Copy(bitmap);
        };
        window.Closed += (_, _) => { if (preview == window) preview = null; };
        preview = window;
        window.Show();
    }

    private void Copy(BitmapSource? bitmap)
    {
        try
        {
            if (bitmap != null) Clipboard.SetImage(bitmap);
            else ShowError("Не удалось прочитать изображение.");
        }
        catch (ExternalException) { ShowError("Буфер обмена занят. Попробуйте ещё раз."); }
    }

    private static bool IsImageError(Exception error) => error is IOException or UnauthorizedAccessException
        or NotSupportedException or ArgumentException or FormatException or ExternalException;

    private static void AddMenuItem(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = L.T(label) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void ShowError(string message) => MessageBox.Show(Window.GetWindow(this), L.T(message), "PaperDesk");
}
