using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaperDesk.Core;

public record Note
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OwnerId { get; init; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public long Revision { get; init; }
    public DateTimeOffset? ArchivedAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PurgedAt { get; init; }
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Body);
}

public record LocalNote(Note Note, long Generation, bool Dirty, Note? Conflict);
public record Outgoing(Guid OperationId, Note Note, long ExpectedRevision, long Generation, bool Rejected = false);
public record PushResult(string Status, Note? Note);
public record Placement
{
    public Guid NoteId { get; init; }
    // Missing in older profiles: common stack. Null explicitly means a loose sheet.
    public Guid? StackId { get; init; } = Guid.Empty;
    public double X { get; init; } = 140;
    public double Y { get; init; } = 140;
    public double Width { get; init; } = 310;
    public double Height { get; init; } = 330;
    public bool Visible { get; init; }
    public bool Topmost { get; init; }
    public string Color { get; init; } = "#FFF1AA";
    public string Font { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 17;
    public bool Favorite { get; init; }
    public double Opacity { get; init; } = 1;
}

public record DesktopStack(Guid Id, string Name, double X, double Y, double Width, double Height, Guid ActiveNoteId = default)
{
    public const int MaxNameLength = 64;
}

public record Backup(int Version, DateTimeOffset ExportedAt, List<Note> Notes, List<Placement> Placements,
    List<Note> RecoveryVersions, List<DesktopStack>? Stacks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<NoteImage>? Images = null);

public sealed record NoteImageLayout(double X, double Y, double Width)
{
    public NoteImageLayout Bounded() => new(Clamp(X, 0, 1, .18), Clamp(Y, 0, 1, .14), Clamp(Width, .08, 1, .64));

    private static double Clamp(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

public record NoteImage(Guid NoteId, Guid Id, byte[] Png,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NoteImageLayout? Layout = null)
{
    public const int MaxPerNote = 8;
    public const int MaxPixelsPerSide = 1600;
    public const int MaxBytes = 6 * 1024 * 1024;

    public static bool IsValid(byte[]? png) => png is { Length: >= 45 and <= MaxBytes } &&
        png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8, 4)) == 13 &&
        png.AsSpan(12, 4).SequenceEqual("IHDR"u8) &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) is > 0 and <= MaxPixelsPerSide &&
        System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) is > 0 and <= MaxPixelsPerSide;
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new InvalidDataException(L.T("Пустые данные JSON"));
}

public interface INoteStore
{
    IReadOnlyList<LocalNote> All();
    LocalNote? Find(Guid id);
    void Save(Note note);
    IReadOnlyList<Outgoing> Pending();
    IReadOnlySet<Guid> RejectedNotes();
    void MarkRejected(Outgoing operation);
    void Acknowledge(Outgoing operation, Note server);
    void SetConflict(Outgoing operation, Note server);
    void ApplyRemote(Note server);
}

public interface ICloudNotes
{
    Task<PushResult> PushAsync(Outgoing operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<Note>> FetchAsync(CancellationToken cancellationToken);
}
