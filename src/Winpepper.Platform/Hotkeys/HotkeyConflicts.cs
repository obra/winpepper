namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Warns when the user picks a chord that collides with well-known Windows
/// shortcuts. Returns a human-readable description of the conflict, or null
/// when the chord is safe.
/// </summary>
public static class HotkeyConflicts
{
    private static readonly Dictionary<string, string> KnownConflicts = new()
    {
        ["Ctrl+C"]   = "Copy",
        ["Ctrl+V"]   = "Paste",
        ["Ctrl+X"]   = "Cut",
        ["Ctrl+Z"]   = "Undo",
        ["Ctrl+Y"]   = "Redo",
        ["Ctrl+A"]   = "Select All",
        ["Ctrl+S"]   = "Save",
        ["Ctrl+P"]   = "Print",
        ["Ctrl+F"]   = "Find",
        ["Alt+F4"]   = "Close window",
        ["Alt+Tab"]  = "Switch window",
        ["Win+L"]    = "Lock screen",
        ["Win+D"]    = "Show desktop",
        ["Win+E"]    = "File Explorer",
        ["Win+R"]    = "Run dialog",
        ["Win+Tab"]  = "Task view",
        ["Ctrl+Esc"] = "Start menu",
    };

    public static string? Describe(HotkeyChord chord)
    {
        var key = chord.ToString();
        return KnownConflicts.TryGetValue(key, out var name) ? $"Conflicts with {name}" : null;
    }

    public static bool HoldAndToggleClash(HotkeyChord hold, HotkeyChord toggle)
        => hold.ToString() == toggle.ToString();
}
