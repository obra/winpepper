namespace Winpepper.Asr.Transcription;

/// <summary>E1 wedge-cascade breaker. After a drain-timeout abandon, the
/// abandoned stream's native dispose queues BEHIND the wedged call while the
/// engine-wide compute gate stays held — so the NEXT dictation's BeginStream
/// would block up to 5 s on the gate and then batch-fallback anyway, turning
/// one wedged native call into a multi-dictation hang. This guard routes
/// subsequent dictations straight to the existing batch path until the
/// abandoned pump's completion has ACTUALLY completed. PumpCompletion is a
/// CONSERVATIVE, ONE-SIDED-SAFE over-approximation of gate availability, not
/// an exact proxy: it can block-to-batch while the gate is free (factory/
/// StartSessionAsync stalls, socket transcribers — batch degradation, never a
/// hang) but never clears while the gate is held for the targeted nemotron
/// wedge; the pump-complete → gate-release window is milliseconds and is
/// absorbed by BeginStream's 5 s gate timeout (NativeStream.Dispose releases
/// the gate in a finally — throw-proof). Pure decision logic; touched only
/// from the serialized hotkey loop, so no locking. Linux-tested.</summary>
public sealed class StreamingRouteGuard
{
    private Task? _abandonedPump;

    /// <summary>Record a drain-timeout abandon. The latest wedge wins:
    /// streaming resumes only when the most recent abandoned pump completes.</summary>
    public void NoteAbandoned(Task pumpCompletion) => _abandonedPump = pumpCompletion;

    /// <summary>Call after EVERY streaming-session DisposeAsync returns —
    /// finish finally, cancel, silence-drop, teardown. Notes the abandon when
    /// the drain timed out OR the pump is still incomplete: the cancel-path
    /// dispose orphans a wedged gate-holding pump after ~5 s with
    /// DrainTimedOut still false, and keying off DrainTimedOut alone would
    /// let the wedge cascade re-enter via cancel.</summary>
    public void NoteDisposeOutcome(bool drainTimedOut, Task pumpCompletion)
    {
        if (drainTimedOut || !pumpCompletion.IsCompleted)
            NoteAbandoned(pumpCompletion);
    }

    /// <summary>True when streaming may start for the next dictation. False
    /// (with a loggable reason) while a previously abandoned pump is still
    /// stuck inside a native call; a completed (or faulted — the call
    /// RETURNED either way) pump clears the block permanently.</summary>
    public bool TryClaimStreaming(out string? blockReason)
    {
        var pump = _abandonedPump;
        if (pump is null || pump.IsCompleted)
        {
            _abandonedPump = null;
            blockReason = null;
            return true;
        }
        blockReason = "a prior streaming session was abandoned on drain timeout and its wedged native call has not returned; routing to the batch path instead of blocking on the compute gate";
        return false;
    }
}
