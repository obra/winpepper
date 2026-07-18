namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// A keyboard chord such as "RightCtrl+RightShift" or "Ctrl+Shift+Space".
/// Strict parser: modifier names are case-sensitive to avoid ambiguity with
/// key letters.
/// </summary>
public sealed record HotkeyChord(Modifier Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, Modifier> ModifierMap = new()
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

    // Subset of common keys, expand as needed. Names map to Windows VK_* codes.
    private static readonly Dictionary<string, int> KeyMap = BuildKeyMap();

    private static Dictionary<string, int> BuildKeyMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"]  = 0x20,
            ["Esc"]    = 0x1B,
            ["Escape"] = 0x1B,
            ["Tab"]    = 0x09,
            ["Enter"]  = 0x0D,
            ["Back"]   = 0x08,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"]   = 0x24,
            ["End"]    = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Left"]   = 0x25,
            ["Up"]     = 0x26,
            ["Right"]  = 0x27,
            ["Down"]   = 0x28,
        };
        for (var i = 1; i <= 12; i++) { map[$"F{i}"] = 0x70 + i - 1; }
        for (var c = 'A'; c <= 'Z'; c++) { map[c.ToString()] = c; }
        for (var c = '0'; c <= '9'; c++) { map[c.ToString()] = c; }
        return map;
    }

    public static HotkeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty chord.");

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Any(string.IsNullOrEmpty))
            throw new FormatException($"Empty token in chord '{text}'.");

        Modifier mods = Modifier.None;
        int? key = null;

        foreach (var part in parts)
        {
            if (ModifierMap.TryGetValue(part, out var m))
            {
                mods |= m;
            }
            else if (KeyMap.TryGetValue(part, out var k))
            {
                if (key.HasValue)
                    throw new FormatException($"Chord '{text}' has more than one non-modifier key.");
                key = k;
            }
            else
            {
                throw new FormatException($"Unknown token '{part}' in chord '{text}'.");
            }
        }

        // Modifier-only chord is allowed (key=0 means "match on modifier release/press only").
        return new HotkeyChord(mods, key ?? 0);
    }

    /// <summary>
    /// True when the supplied key + modifier state satisfies this chord.
    /// If <see cref="VirtualKey"/> is 0, only modifiers are compared.
    /// </summary>
    public bool Matches(int virtualKey, Modifier currentModifiers)
    {
        if (Modifiers != Modifier.None)
        {
            // Caller's modifier set must include exactly our required modifiers.
            // "Ctrl" matches if either LeftCtrl or RightCtrl is down; do that by
            // checking each pair-group.
            if (!ModifiersSatisfied(currentModifiers)) return false;
        }
        return VirtualKey == 0 || virtualKey == VirtualKey;
    }

    private bool ModifiersSatisfied(Modifier current)
    {
        // Check each modifier group (Ctrl/Shift/Alt/Win) the chord requires.
        if (!GroupSatisfied(current, Modifier.Ctrl,  Modifier.LeftCtrl,  Modifier.RightCtrl))  return false;
        if (!GroupSatisfied(current, Modifier.Shift, Modifier.LeftShift, Modifier.RightShift)) return false;
        if (!GroupSatisfied(current, Modifier.Alt,   Modifier.LeftAlt,   Modifier.RightAlt))   return false;
        if (!GroupSatisfied(current, Modifier.Win,   Modifier.LeftWin,   Modifier.RightWin))   return false;

        // No modifier from a group not required should be down.
        var requiredGroups = Modifier.None;
        if (HasAny(Modifier.Ctrl))  requiredGroups |= Modifier.Ctrl;
        if (HasAny(Modifier.Shift)) requiredGroups |= Modifier.Shift;
        if (HasAny(Modifier.Alt))   requiredGroups |= Modifier.Alt;
        if (HasAny(Modifier.Win))   requiredGroups |= Modifier.Win;

        var currentGroups =
            (HasAny(current, Modifier.Ctrl)  ? Modifier.Ctrl  : Modifier.None) |
            (HasAny(current, Modifier.Shift) ? Modifier.Shift : Modifier.None) |
            (HasAny(current, Modifier.Alt)   ? Modifier.Alt   : Modifier.None) |
            (HasAny(current, Modifier.Win)   ? Modifier.Win   : Modifier.None);
        if (currentGroups != requiredGroups) return false;

        return true;
    }

    private bool GroupSatisfied(Modifier current, Modifier group, Modifier left, Modifier right)
    {
        var requiredInGroup = Modifiers & group;
        if (requiredInGroup == Modifier.None) return true;

        var leftReq  = (requiredInGroup & left)  != Modifier.None;
        var rightReq = (requiredInGroup & right) != Modifier.None;
        var leftDown  = (current & left)  != Modifier.None;
        var rightDown = (current & right) != Modifier.None;

        if (leftReq && rightReq)
        {
            // "Ctrl" (group flag, i.e. both sides) => either side acceptable.
            return leftDown || rightDown;
        }

        // Side-specific: required side must be down; the other side must NOT be down.
        if (leftReq  && (!leftDown  || rightDown)) return false;
        if (rightReq && (!rightDown || leftDown))  return false;
        return true;
    }

    private bool HasAny(Modifier m) => (Modifiers & m) != Modifier.None;
    private static bool HasAny(Modifier source, Modifier mask) => (source & mask) != Modifier.None;

    public override string ToString()
    {
        var parts = new List<string>();
        AppendGroup(parts, Modifier.Ctrl,  Modifier.LeftCtrl,  Modifier.RightCtrl,  "Ctrl",  "LeftCtrl",  "RightCtrl");
        AppendGroup(parts, Modifier.Shift, Modifier.LeftShift, Modifier.RightShift, "Shift", "LeftShift", "RightShift");
        AppendGroup(parts, Modifier.Alt,   Modifier.LeftAlt,   Modifier.RightAlt,   "Alt",   "LeftAlt",   "RightAlt");
        AppendGroup(parts, Modifier.Win,   Modifier.LeftWin,   Modifier.RightWin,   "Win",   "LeftWin",   "RightWin");

        if (VirtualKey != 0)
        {
            var keyName = ReverseKeyName(VirtualKey);
            parts.Add(keyName);
        }
        return string.Join("+", parts);
    }

    private void AppendGroup(List<string> parts, Modifier group, Modifier left, Modifier right,
                              string groupName, string leftName, string rightName)
    {
        // If both sides set, this is the group flag (e.g. "Ctrl"). Output the group name to
        // preserve round-trip semantics.
        if ((Modifiers & group) == group) { parts.Add(groupName); return; }
        if ((Modifiers & left)  != Modifier.None) parts.Add(leftName);
        else if ((Modifiers & right) != Modifier.None) parts.Add(rightName);
    }

    private static string ReverseKeyName(int vk) => vk switch
    {
        0x20 => "Space", 0x1B => "Esc", 0x09 => "Tab", 0x0D => "Enter",
        0x08 => "Back", 0x2D => "Insert", 0x2E => "Delete",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"VK_0x{vk:X2}",
    };
}
