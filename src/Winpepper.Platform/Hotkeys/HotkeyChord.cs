namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// A keyboard chord such as "RightCtrl+RightShift" or "Ctrl+Shift+Space".
/// Strict parser: modifier names are case-sensitive to avoid ambiguity with
/// key letters.
/// </summary>
public sealed record HotkeyChord(Modifier Modifiers, int VirtualKey)
{
    public static HotkeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty chord.");
        if (VirtualKeyCatalog.TryParseChordAlias(text, out var alias)) return alias;

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Any(string.IsNullOrEmpty))
            throw new FormatException($"Empty token in chord '{text}'.");

        Modifier mods = Modifier.None;
        int? key = null;

        foreach (var part in parts)
        {
            if (VirtualKeyCatalog.TryParseModifier(part, out var m))
            {
                mods |= m;
            }
            else if (VirtualKeyCatalog.TryParseKey(part, out var k))
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

    // Only function keys and the Application key are conventional as bare
    // (modifier-less) global hotkeys; ordinary keys would be swallowed while
    // bound. Space is handled separately by the explicit dual-role policy.
    private static bool IsBareTriggerAllowed(int vk) => VirtualKeyCatalog.IsDedicatedBareKey(vk);

    /// <summary>
    /// Policy gate for hold/toggle trigger bindings. Returns null when the
    /// binding is safe, otherwise a human-readable reason it was rejected.
    /// Enforced both in the settings UI validator and when loading a hand-edited
    /// settings file. Rules:
    ///  * A modifier-only chord (VirtualKey == 0) is safe - its trigger is a
    ///    modifier, which the hook never swallows.
    ///  * A chord with modifiers plus a non-modifier key is safe.
    ///  * A bare (modifier-less) non-modifier key is rejected UNLESS it is an
    ///    F-key, because the hook swallows the trigger and a bare common key
    ///    (Esc/Tab/Enter/Space/letter/digit/arrow/...) would then be dead
    ///    system-wide.
    ///  * The trigger key may never equal the Cancel chord's key, so the
    ///    pass-through Cancel/Esc key can never be turned into a swallowed trigger.
    /// </summary>
    public static string? ValidateTriggerBinding(
        HotkeyChord chord, HotkeyChord cancel, bool allowLongPressSpace = false)
    {
        ArgumentNullException.ThrowIfNull(chord);
        ArgumentNullException.ThrowIfNull(cancel);

        if (chord.VirtualKey != 0 && cancel.VirtualKey != 0 && chord.VirtualKey == cancel.VirtualKey)
            return $"'{chord}' uses the Cancel key, which must stay available system-wide.";

        if (chord.VirtualKey == 0) return null;                  // modifier-only: safe
        if (chord.Modifiers != Modifier.None) return null;       // modifier + key: safe
        if (IsBareTriggerAllowed(chord.VirtualKey)) return null; // bare F-key: safe
        if (allowLongPressSpace && chord.VirtualKey == VirtualKeyCatalog.Space) return null;

        return $"'{chord}' has no modifier and would be swallowed system-wide. " +
               "Add a modifier (e.g. Ctrl+Shift+...) or use an F-key.";
    }

    /// <summary>
    /// Parses a configured hold/toggle chord and enforces
    /// <see cref="ValidateTriggerBinding"/>. If the value cannot be parsed or is
    /// an unsafe trigger, returns <paramref name="defaultChord"/> parsed and
    /// invokes <paramref name="onRejected"/> with a warning message, so a
    /// hand-edited settings file can never bind a swallowed common key.
    /// </summary>
    public static HotkeyChord ParseTriggerOrDefault(
        string configured, string defaultChord, HotkeyChord cancel,
        Action<string>? onRejected = null, bool allowLongPressSpace = false)
    {
        HotkeyChord parsed;
        try
        {
            parsed = Parse(configured);
        }
        catch (FormatException ex)
        {
            onRejected?.Invoke($"Hotkey '{configured}' is invalid ({ex.Message}); using default '{defaultChord}'.");
            return Parse(defaultChord);
        }

        var reason = ValidateTriggerBinding(parsed, cancel, allowLongPressSpace);
        if (reason is not null)
        {
            onRejected?.Invoke($"Hotkey '{configured}' rejected: {reason} Using default '{defaultChord}'.");
            return Parse(defaultChord);
        }

        return parsed;
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
        if (this == VirtualKeyCatalog.CopilotChord) return "Copilot";

        var parts = new List<string>();
        AppendGroup(parts, Modifier.Ctrl,  Modifier.LeftCtrl,  Modifier.RightCtrl,  "Ctrl",  "LeftCtrl",  "RightCtrl");
        AppendGroup(parts, Modifier.Shift, Modifier.LeftShift, Modifier.RightShift, "Shift", "LeftShift", "RightShift");
        AppendGroup(parts, Modifier.Alt,   Modifier.LeftAlt,   Modifier.RightAlt,   "Alt",   "LeftAlt",   "RightAlt");
        AppendGroup(parts, Modifier.Win,   Modifier.LeftWin,   Modifier.RightWin,   "Win",   "LeftWin",   "RightWin");

        if (VirtualKey != 0)
        {
            var keyName = VirtualKeyCatalog.NameForKey(VirtualKey);
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

}
