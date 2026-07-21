namespace Winpepper.Platform.Hotkeys;

/// <summary>Result of feeding one key press to <see cref="ChordRecorder"/>.</summary>
public enum ChordKeyResult
{
    /// <summary>Not recording, or the key cannot finish a chord (bare modifier / unmapped key).</summary>
    Ignored,
    /// <summary>Esc pressed — recording stopped, previous chord stands.</summary>
    Cancelled,
    /// <summary>The combination did not parse; recording stays armed for another try.</summary>
    Invalid,
    /// <summary>A valid chord was captured; see <see cref="ChordRecorder.CommittedChord"/>.</summary>
    Committed,
}

/// <summary>
/// UI-free state machine behind the hotkey "Record" box (issue #11). The
/// control translates WinUI key events into (keyName, modifierPrefix) pairs;
/// this class owns the recording state so the cancel/commit transitions are
/// unit-testable without a UI thread.
/// </summary>
public sealed class ChordRecorder
{
    private string? _modifierOnlyCandidate;
    private Modifier _rawModifiers;

    public bool IsRecording { get; private set; }

    /// <summary>The chord captured by the most recent Committed transition.</summary>
    public string? CommittedChord { get; private set; }

    public void Begin()
    {
        IsRecording = true;
        CommittedChord = null;
        _modifierOnlyCandidate = null;
        _rawModifiers = Modifier.None;
    }

    /// <summary>Cancels an in-flight recording. Returns false when idle (no-op).</summary>
    public bool Cancel()
    {
        if (!IsRecording) return false;
        IsRecording = false;
        _modifierOnlyCandidate = null;
        _rawModifiers = Modifier.None;
        return true;
    }

    /// <summary>
    /// Remembers the modifier-only chord currently held by the user. It is not
    /// committed until a modifier is released, which gives the user time to
    /// press a multi-modifier chord such as LeftCtrl+LeftShift.
    /// </summary>
    public ChordKeyResult OnModifierKeyDown(string modifierPrefix)
    {
        if (!IsRecording) return ChordKeyResult.Ignored;

        var chord = modifierPrefix.TrimEnd('+');
        if (chord.Length == 0) return ChordKeyResult.Ignored;

        try
        {
            if (HotkeyChord.Parse(chord).VirtualKey != 0)
                return ChordKeyResult.Invalid;
        }
        catch (FormatException)
        {
            return ChordKeyResult.Invalid;
        }

        _modifierOnlyCandidate = chord;
        return ChordKeyResult.Ignored;
    }

    /// <summary>
    /// Commits the largest modifier-only chord observed during this recording
    /// when the user starts releasing it.
    /// </summary>
    public ChordKeyResult OnModifierKeyUp()
    {
        if (!IsRecording || _modifierOnlyCandidate is null)
            return ChordKeyResult.Ignored;

        CommittedChord = _modifierOnlyCandidate;
        IsRecording = false;
        _modifierOnlyCandidate = null;
        return ChordKeyResult.Committed;
    }

    /// <summary>
    /// Feeds one key press. <paramref name="keyName"/> is null for keys that
    /// cannot finish a chord; <paramref name="modifierPrefix"/> is the
    /// "LeftCtrl+LeftShift+"-style prefix of currently held modifiers. Esc
    /// always cancels local chord recording, so it can never be recorded as
    /// part of a chord.
    /// </summary>
    public ChordKeyResult OnKey(string? keyName, string modifierPrefix, bool isEscape)
    {
        if (!IsRecording) return ChordKeyResult.Ignored;
        if (isEscape)
        {
            IsRecording = false;
            _modifierOnlyCandidate = null;
            return ChordKeyResult.Cancelled;
        }
        if (keyName is null)
        {
            // An unmapped non-modifier key means the remembered modifier state
            // is no longer a reliable representation of the intended chord.
            _modifierOnlyCandidate = null;
            return ChordKeyResult.Ignored;
        }

        var chord = modifierPrefix + keyName;
        try
        {
            HotkeyChord.Parse(chord);
        }
        catch (FormatException)
        {
            _modifierOnlyCandidate = null;
            return ChordKeyResult.Invalid;
        }

        CommittedChord = HotkeyChord.Parse(chord).ToString();
        IsRecording = false;
        _modifierOnlyCandidate = null;
        _rawModifiers = Modifier.None;
        return ChordKeyResult.Committed;
    }

    /// <summary>
    /// Feeds a transition from the low-level hook. This path is independent of
    /// WinUI focus and retains the raw left/right modifier identity.
    /// </summary>
    public ChordKeyResult OnRawKey(RawKeyTransition transition)
    {
        if (!IsRecording || transition.IsInjected || transition.IsRepeat)
            return ChordKeyResult.Ignored;

        var modifier = VirtualKeyCatalog.ModifierForVirtualKey(transition.VirtualKey);
        if (modifier != Modifier.None)
        {
            if (transition.IsDown)
            {
                _rawModifiers |= modifier;
                return OnModifierKeyDown(VirtualKeyCatalog.FormatModifierPrefix(_rawModifiers));
            }

            var result = OnModifierKeyUp();
            _rawModifiers &= ~modifier;
            return result;
        }

        if (!transition.IsDown) return ChordKeyResult.Ignored;
        VirtualKeyCatalog.TryGetRecordableKeyName(transition.VirtualKey, out var keyName);
        return OnKey(keyName, VirtualKeyCatalog.FormatModifierPrefix(_rawModifiers),
            transition.VirtualKey == 0x1B);
    }
}
