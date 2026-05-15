namespace Winpepper.Platform.Hotkeys;

public enum HotkeyEventKind
{
    HoldDown,
    HoldUp,
    Toggle,
    Cancel,
}

public sealed record HotkeyEvent(HotkeyEventKind Kind, DateTimeOffset Timestamp);
