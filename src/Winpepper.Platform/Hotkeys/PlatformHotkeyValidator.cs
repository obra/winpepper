using Winpepper.Core.ViewModels;

namespace Winpepper.Platform.Hotkeys;

public sealed class PlatformHotkeyValidator : IHotkeyValidator
{
    // Cancel is always Esc (see AppShell / ChordRecorder); a trigger may never
    // reuse it.
    private static readonly HotkeyChord CancelChord = HotkeyChord.Parse("Esc");

    public string? Validate(string chord)
    {
        HotkeyChord parsed;
        try { parsed = HotkeyChord.Parse(chord); }
        catch (FormatException ex) { return ex.Message; }

        var conflict = HotkeyConflicts.Describe(parsed);
        if (conflict is not null) return conflict;

        // Reject modifier-less common-key triggers (they'd be swallowed globally)
        // and any trigger that reuses the Cancel key.
        return HotkeyChord.ValidateTriggerBinding(parsed, CancelChord);
    }

    public bool Clash(string a, string b)
    {
        try { return HotkeyConflicts.HoldAndToggleClash(HotkeyChord.Parse(a), HotkeyChord.Parse(b)); }
        catch { return false; }
    }
}
