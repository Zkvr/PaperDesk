
namespace PaperDesk.Desktop.Services;

public record Settings
{
    public string SupabaseUrl { get; init; } = "";
    public string PublishableKey { get; init; } = "";
    public Guid LastUserId { get; init; }
    public bool DesktopLayer { get; init; }
    public bool Animations { get; init; } = true;
    public bool NativeEditor { get; init; }
    public bool SnapNotes { get; init; }
    public string Theme { get; init; } = "graphite";
    public string Hotkey { get; init; } = "Ctrl+Alt+N";
    public string SummonHotkey { get; init; } = "Ctrl+Alt+Space";
    public bool AutoStart { get; init; }
    public bool StartInTray { get; init; } = true;
    public bool WelcomePending { get; init; }
    public bool ShowNotesAtStartup { get; init; } = true;
    public bool StackedNotes { get; init; } = true;
    public Guid StackActiveNoteId { get; init; }
    public double StackX { get; init; } = 160;
    public double StackY { get; init; } = 130;
    public double StackWidth { get; init; } = 330;
    public double StackHeight { get; init; } = 370;
}

