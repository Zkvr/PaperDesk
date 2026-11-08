using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PaperDesk.Desktop.Services;

namespace PaperDesk.Desktop.Controls;

public sealed partial class NoteBodyEditor
{
    private const string EditorOrigin = "https://paperdesk-editor.invalid";
    private const string EditorPage = EditorOrigin + "/index.html";
    private const int MaxWebMessage = (BrowserImageDrop.MaxInputBytes + 2) / 3 * 4 + 4096;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private WebView2CompositionControl? webEditor;
    private readonly List<WebDocument> webDocuments = [];
    private DeskController? webDesk;
    private Action? webNewNote;
    private bool webQueued, webUnavailable;
    internal bool IsWebActive { get; private set; }

    internal void EnableWebEditor(DeskController desk, Action newNote)
    {
        webDesk = desk;
        webNewNote = newNote;
        Loaded += (_, _) => QueueWebSync();
        IsVisibleChanged += (_, _) => QueueWebSync();
        Unloaded += (_, _) => DisposeWebEditor();
        QueueWebSync();
    }

    internal void QueueWebSync()
    {
        if (webDesk == null || webQueued || webUnavailable) return;
        webQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            webQueued = false;
            if (!IsLoaded || !IsVisible) return;
            if (webEditor == null && pictures?.WebNote != null) _ = StartWebEditor();
            if (IsWebActive) SyncWebDocument();
        });
    }

    private async Task StartWebEditor()
    {
        var host = new WebView2CompositionControl
        {
            Visibility = Visibility.Hidden,
            DefaultBackgroundColor = System.Drawing.Color.Transparent,
            // Closing the manager preview must never resize the D3D capture to zero.
            MinWidth = 1,
            MinHeight = 1,
        };
        webEditor = host;
        ((Grid)Content).Children.Add(host);
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-background-networking --disable-component-update",
            };
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(webDesk!.DataRoot, "web-editor"), options: options);
            if (webEditor != host) return;
            await host.EnsureCoreWebView2Async(environment);
            if (webEditor != host) return;
            var core = host.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != EditorPage;
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) => FailWebEditor($"process {e.ProcessFailedKind}");
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) FailWebEditor($"navigation {e.WebErrorStatus}");
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += ServeWebResource;
            core.WebMessageReceived += ReceiveWebMessage;
            host.Source = new Uri(EditorPage);
            await Task.Delay(TimeSpan.FromSeconds(12));
            if (webEditor == host && !IsWebActive) FailWebEditor("page did not start");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or IOException or
            UnauthorizedAccessException or WebView2RuntimeNotFoundException)
        {
            if (webEditor == host) FailWebEditor($"{error.GetType().Name} 0x{error.HResult:X8}");
        }
    }

    private void ServeWebResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;
        Stream? stream = null;
        var contentType = "text/plain";
        if (e.Request.Method == "GET" && e.Request.Uri.StartsWith(EditorOrigin + "/", StringComparison.Ordinal))
        {
            var path = e.Request.Uri[(EditorOrigin.Length + 1)..];
            contentType = path switch
            {
                "index.html" => "text/html; charset=utf-8",
                "editor.css" => "text/css; charset=utf-8",
                "app.bundle.mjs" => "text/javascript; charset=utf-8",
                _ => contentType,
            };
            try
            {
                if (contentType != "text/plain") stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "WebEditor", path));
                else if (path.StartsWith("picture/", StringComparison.Ordinal) && Guid.TryParseExact(path[8..], "D", out var imageId) &&
                    pictures?.WebNote is { } note && note.Store == webDesk?.Store &&
                    note.Store.ReadImage(note.Id, imageId) is { } png)
                {
                    stream = new MemoryStream(png, writable: false);
                    contentType = "image/png";
                }
            }
            catch (IOException) { }
        }
        e.Response = core.Environment.CreateWebResourceResponse(stream ?? Stream.Null, stream == null ? 404 : 200,
            stream == null ? "Not Found" : "OK", $"Content-Type: {contentType}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
    }

    private void FailWebEditor(string reason)
    {
        System.Diagnostics.Trace.TraceWarning("PaperDesk embedded editor fell back to WPF ({0}).", reason);
        webUnavailable = true;
        DisposeWebEditor();
    }

    private void DisposeWebEditor()
    {
        if (webEditor is not { } host) return;
        webEditor = null;
        webDocuments.Clear();
        if (IsWebActive)
        {
            IsWebActive = false;
            SetWebEditing(false);
            viewer.Visibility = Visibility.Visible;
            pictures?.UpdateVisibility();
            InvalidateLines();
        }
        ((Grid)Content).Children.Remove(host);
        host.Dispose();
    }

    private void SyncWebDocument()
    {
        if (webEditor is not { CoreWebView2: { } core } host) return;
        var note = pictures?.WebNote;
        if (note == null)
        {
            host.Visibility = Visibility.Hidden;
            return;
        }
        var (store, id, editable) = note.Value;
        if (store != webDesk?.Store || store.Find(id) is not { } row) return;
        host.Visibility = Visibility.Visible;
        var document = webDocuments.LastOrDefault();
        if (document == null || document.Store != store || document.Id != id)
        {
            document = new WebDocument(store, id, row.Note.Body);
            webDocuments.Add(document);
            if (webDocuments.Count > 4) webDocuments.RemoveAt(0);
        }
        document.Body = row.Note.Body;
        var payload = WebLoad(document, editable);
        if (payload == document.LastLoad) return;
        document.LastLoad = payload;
        core.PostWebMessageAsJson(payload);
    }

    private string WebLoad(WebDocument document, bool editable) => JsonSerializer.Serialize(new
    {
        type = "load", revision = document.Revision, body = document.Body,
        images = document.Store.ImageLayouts(document.Id).Select(picture => WebPicture.From(picture.Id, picture.Layout)).ToArray(),
        editable, font = FontFamily.Source, fontSize = FontSize,
        russian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
    }, WebJson);

    private void SendWebCommand(string type)
    {
        if (!IsWebActive || webEditor?.CoreWebView2 is not { } core || webDocuments.LastOrDefault() is not { } document) return;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type, revision = document.Revision }, WebJson));
    }

    private void SetWebEditing(bool editing)
    {
        if (editing == IsEditing) return;
        SetValue(IsEditingPropertyKey, editing);
        IsEditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReceiveWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != EditorPage || e.WebMessageAsJson.Length > MaxWebMessage || webEditor == null) return;
        WebMessage? message;
        try { message = JsonSerializer.Deserialize<WebMessage>(e.WebMessageAsJson, WebJson); }
        catch (JsonException) { return; }
        if (message?.Type == "ready" && !IsWebActive)
        {
            EndEdit();
            IsWebActive = true;
            ToolTip = null;
            viewer.Visibility = editor.Visibility = Visibility.Collapsed;
            pictures?.UpdateVisibility();
            SyncWebDocument();
            return;
        }
        if (message == null || webDocuments.Find(document => document.Revision == message.Revision) is not { } current ||
            current.Store != webDesk?.Store || current.Store.Find(current.Id) is not { } row) return;
        var editable = row.Note is { ArchivedAt: null, DeletedAt: null };
        if (message.Type == "change" && editable && message.Body != null)
        {
            var layouts = ReadWebLayouts(current, message.Body, message.Images);
            var shownBody = message.Before ?? current.Body;
            current.Store.SaveText(current.Id, null, message.Body, "", shownBody, layouts);
            current.Body = message.Body;
            current.LastLoad = WebLoad(current, editable);
            return;
        }
        if (pictures?.WebNote is not { } active || active.Store != current.Store || active.Id != current.Id ||
            current != webDocuments.LastOrDefault()) return;
        var anchor = Math.Clamp(message.Anchor, 0, row.Note.Body.AsSpan().Count('\n') + 1);
        switch (message.Type)
        {
            case "focus": SetWebEditing(message.Editing); break;
            case "title": titleBox?.Focus(); break;
            case "newNote": webNewNote?.Invoke(); break;
            case "image" when message.Id is Guid image:
                pictures.WebPicture(image, message.Action ?? "");
                break;
            case "paste" when editable: pictures.Paste(anchor); break;
            case "attach" when editable && message.Data != null:
                try
                {
                    var bytes = Convert.FromBase64String(message.Data);
                    if (bytes.Length <= BrowserImageDrop.MaxInputBytes) pictures.TryAttach(new DataObject("PNG", bytes), anchor: anchor);
                }
                catch (FormatException) { }
                break;
        }
    }

    private static IReadOnlyDictionary<Guid, NoteImageLayout> ReadWebLayouts(WebDocument document, string text, WebPicture[]? pictures)
    {
        var result = new Dictionary<Guid, NoteImageLayout>();
        if (pictures is not { Length: > 0 and <= NoteImage.MaxPerNote }) return result;
        var lines = text.AsSpan().Count('\n') + 1;
        var known = document.Store.ImageLayouts(document.Id).ToDictionary(item => item.Id, item => item.Layout);
        foreach (var picture in pictures)
        {
            if (picture == null || !known.TryGetValue(picture.Id, out var old) ||
                !Enum.TryParse<ImageTextMode>(picture.Mode, true, out var mode) || !Enum.IsDefined(mode)) continue;
            var layout = new NoteImageLayout(picture.X, old?.Y ?? .14, picture.Width, mode, Math.Clamp(picture.Anchor, 0, lines)).Bounded();
            if (old != layout) result[picture.Id] = layout;
        }
        return result;
    }

    private sealed class WebDocument(NoteStore store, Guid id, string body)
    {
        public Guid Revision { get; } = Guid.NewGuid();
        public NoteStore Store { get; } = store;
        public Guid Id { get; } = id;
        public string Body { get; set; } = body;
        public string? LastLoad { get; set; }
    }

    private sealed record WebMessage(string Type, Guid Revision, string? Body, string? Before, WebPicture[]? Images,
        bool Editing, Guid? Id, string? Action, string? Data, int Anchor);

    private sealed record WebPicture(Guid Id, string Mode, double X, double Y, double Width, int Anchor)
    {
        public static WebPicture From(Guid id, NoteImageLayout? saved)
        {
            var layout = saved ?? new NoteImageLayout(.18, .14, .64, ImageTextMode.Inline);
            return new(id, layout.Mode.ToString().ToLowerInvariant(), layout.X, layout.Y, layout.Width, layout.Anchor);
        }
    }
}
