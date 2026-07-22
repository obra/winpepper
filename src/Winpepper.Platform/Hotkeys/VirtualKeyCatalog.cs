namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Shared vocabulary for configured chords, low-level hook events, and the UI
/// recorder. Virtual-key names are canonical on output while selected Windows
/// aliases remain accepted on input.
/// </summary>
public static class VirtualKeyCatalog
{
    public const int Application = 0x5D;
    public const int Space = 0x20;
    public const int F1 = 0x70;
    public const int F23 = 0x86;
    public const int F24 = 0x87;

    public static readonly HotkeyChord CopilotChord =
        new(Modifier.LeftShift | Modifier.LeftWin, F23);

    private static readonly Dictionary<string, Modifier> Modifiers = new()
    {
        ["LeftCtrl"]   = Modifier.LeftCtrl,
        ["RightCtrl"]  = Modifier.RightCtrl,
        ["Ctrl"]       = Modifier.Ctrl,
        ["LeftShift"]  = Modifier.LeftShift,
        ["RightShift"] = Modifier.RightShift,
        ["Shift"]      = Modifier.Shift,
        ["LeftAlt"]    = Modifier.LeftAlt,
        ["RightAlt"]   = Modifier.RightAlt,
        ["Alt"]        = Modifier.Alt,
        ["LeftWin"]    = Modifier.LeftWin,
        ["RightWin"]   = Modifier.RightWin,
        ["Win"]        = Modifier.Win,
    };

    private static readonly Dictionary<string, int> Keys = BuildKeys();

    private static Dictionary<string, int> BuildKeys()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = Space,
            ["Esc"] = 0x1B,
            ["Escape"] = 0x1B,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Back"] = 0x08,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28,
            ["Application"] = Application,
            ["Menu"] = Application,
            ["Apps"] = Application,
            ["ContextMenu"] = Application,
        };
        for (var i = 1; i <= 24; i++) map[$"F{i}"] = F1 + i - 1;
        for (var c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;
        for (var c = '0'; c <= '9'; c++) map[c.ToString()] = c;
        return map;
    }

    public static bool TryParseModifier(string name, out Modifier modifier)
        => Modifiers.TryGetValue(name, out modifier);

    public static bool TryParseKey(string name, out int virtualKey)
        => Keys.TryGetValue(name, out virtualKey);

    public static bool TryParseChordAlias(string text, out HotkeyChord chord)
    {
        if (string.Equals(text, "Copilot", StringComparison.OrdinalIgnoreCase))
        {
            chord = CopilotChord;
            return true;
        }

        chord = null!;
        return false;
    }

    public static string NameForKey(int virtualKey) => virtualKey switch
    {
        Space => "Space", 0x1B => "Esc", 0x09 => "Tab", 0x0D => "Enter",
        0x08 => "Back", 0x2D => "Insert", 0x2E => "Delete",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        Application => "Application",
        >= F1 and <= F24 => $"F{virtualKey - F1 + 1}",
        >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),
        >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),
        _ => $"VK_0x{virtualKey:X2}",
    };

    public static bool TryGetRecordableKeyName(int virtualKey, out string? name)
    {
        name = NameForKey(virtualKey);
        if (name.StartsWith("VK_", StringComparison.Ordinal))
        {
            name = null;
            return false;
        }
        return true;
    }

    public static bool IsDedicatedBareKey(int virtualKey)
        => virtualKey is >= F1 and <= F24 or Application;

    public static Modifier ModifierForVirtualKey(int virtualKey) => virtualKey switch
    {
        0xA2 => Modifier.LeftCtrl,
        0xA3 => Modifier.RightCtrl,
        0xA0 => Modifier.LeftShift,
        0xA1 => Modifier.RightShift,
        0xA4 => Modifier.LeftAlt,
        0xA5 => Modifier.RightAlt,
        0x5B => Modifier.LeftWin,
        0x5C => Modifier.RightWin,
        _ => Modifier.None,
    };

    public static string FormatModifierPrefix(Modifier modifiers)
    {
        var parts = new List<string>();
        AppendGroup(parts, modifiers, Modifier.Ctrl, Modifier.LeftCtrl, Modifier.RightCtrl,
            "Ctrl", "LeftCtrl", "RightCtrl");
        AppendGroup(parts, modifiers, Modifier.Shift, Modifier.LeftShift, Modifier.RightShift,
            "Shift", "LeftShift", "RightShift");
        AppendGroup(parts, modifiers, Modifier.Alt, Modifier.LeftAlt, Modifier.RightAlt,
            "Alt", "LeftAlt", "RightAlt");
        AppendGroup(parts, modifiers, Modifier.Win, Modifier.LeftWin, Modifier.RightWin,
            "Win", "LeftWin", "RightWin");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    private static void AppendGroup(List<string> parts, Modifier modifiers,
        Modifier group, Modifier left, Modifier right,
        string groupName, string leftName, string rightName)
    {
        if ((modifiers & group) == group) { parts.Add(groupName); return; }
        if ((modifiers & left) != 0) parts.Add(leftName);
        else if ((modifiers & right) != 0) parts.Add(rightName);
    }
}
