using Winpepper.Core.ViewModels;

namespace Winpepper.Platform.Hotkeys;

public sealed class PlatformHotkeyValidator : IHotkeyValidator
{
    public string? Validate(string chord)
    {
        HotkeyChord parsed;
        try { parsed = HotkeyChord.Parse(chord); }
        catch (FormatException ex) { return ex.Message; }
        return HotkeyConflicts.Describe(parsed);
    }

    public bool Clash(string a, string b)
    {
        try { return HotkeyConflicts.HoldAndToggleClash(HotkeyChord.Parse(a), HotkeyChord.Parse(b)); }
        catch { return false; }
    }
}
