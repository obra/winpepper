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
    public bool IsRecording { get; private set; }

    /// <summary>The chord captured by the most recent Committed transition.</summary>
    public string? CommittedChord { get; private set; }

    public void Begin()
    {
        IsRecording = true;
        CommittedChord = null;
    }

    /// <summary>Cancels an in-flight recording. Returns false when idle (no-op).</summary>
    public bool Cancel()
    {
        if (!IsRecording) return false;
        IsRecording = false;
        return true;
    }

    /// <summary>
    /// Feeds one key press. <paramref name="keyName"/> is null for keys that
    /// cannot finish a chord; <paramref name="modifierPrefix"/> is the
    /// "LeftCtrl+LeftShift+"-style prefix of currently held modifiers. Esc
    /// always cancels — it doubles as the global cancel hotkey, so it can
    /// never be recorded as part of a chord.
    /// </summary>
    public ChordKeyResult OnKey(string? keyName, string modifierPrefix, bool isEscape)
    {
        if (!IsRecording) return ChordKeyResult.Ignored;
        if (isEscape)
        {
            IsRecording = false;
            return ChordKeyResult.Cancelled;
        }
        if (keyName is null) return ChordKeyResult.Ignored;

        var chord = modifierPrefix + keyName;
        try
        {
            HotkeyChord.Parse(chord);
        }
        catch (FormatException)
        {
            return ChordKeyResult.Invalid;
        }

        CommittedChord = chord;
        IsRecording = false;
        return ChordKeyResult.Committed;
    }
}
