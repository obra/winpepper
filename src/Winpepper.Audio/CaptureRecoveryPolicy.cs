namespace Winpepper.Audio;

/// <summary>
/// Pure decision logic for endpoint-event-driven microphone recovery
/// (2026-07-24 sleep/resume incident): mid-resume the warm stream faults and
/// the immediate rebuild fails because no default capture endpoint exists yet.
/// Something must retry when a device comes back - and nothing did, because the
/// only remaining seam was the next hotkey press.
///
/// Four decisions live here so they can be unit-tested on Linux while the COM
/// notification client stays a thin Windows shell:
///
///  * IS a retry warranted at all (<see cref="IsFailing"/>)? A healthy warm
///    stream is left alone; the session-start default-device drift check
///    already follows the default endpoint for the "changed the default, then
///    dictated" case.
///  * SHOULD this device event drive a rebuild now (<see cref="ShouldRebuild"/>)?
///    Endpoint notifications arrive in bursts on resume, so only the leading
///    edge of a burst acts.
///  * SHOULD a failed rebuild be retried (<see cref="TryScheduleRetry"/> /
///    <see cref="TryClaimRetry"/>)? A default-device change is documented as
///    exactly THREE back-to-back notifications (one per role) - trivially
///    inside one debounce window - and nothing is documented after the burst
///    settles, so leading-edge-only with no trailing action can stall forever
///    (the incident's exact symptom; OBS retries device-invalidated on a
///    timer, Chromium and cubeb bypass their debounce on device change). The
///    retry is BOUNDED (<see cref="MaxScheduledRetries"/> per endpoint event,
///    refilled by each fresh event) and is NOT a timer-clear or a validity
///    probe: it re-runs the recovery and lets success clear - the taxonomy's
///    own endorsed sentence.
///  * WAS this a recovery (<see cref="NoteFramesObserved"/>)? Clearing is
///    FRAMES-driven, NOT IsRunning-driven: NAudio 2.2.1's
///    WasapiCapture.StartRecording returns after InitializeCaptureDevice()
///    and starts IAudioClient on the capture thread LATER, so "IsRunning right
///    after a rebuild" proves only that Initialize succeeded - the stream can
///    fault milliseconds later (0x88890004, the incident's signature) or stay
///    "Capturing" delivering nothing. The first observed NON-EMPTY frame from
///    the live source cannot lie, and it is the ONLY signal that clears the
///    microphone CONDITION.
///
/// EVERY member takes the private lock: the writers are NAudio capture
/// threads, MULTIPLE CONCURRENT thread-pool endpoint handlers (the COM
/// callbacks are de-serialized by the marshaling hand-off), and the pipeline
/// thread. Volatile/Interlocked are insufficient - the decisions are compound
/// read-modify-writes and DateTime? is a multi-word field whose assignment is
/// not atomic (ECMA-335 §I.12.6.6). All operations are O(1); the per-frame
/// call is an uncontended lock acquisition.
/// </summary>
public sealed class CaptureRecoveryPolicy
{
    /// <summary>Endpoint notifications burst on resume; act on the leading
    /// edge only. (Owner-agreed, unchanged: 500 ms.)</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay before the one-shot retry armed by a failed rebuild.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Scheduled retries per endpoint event. Each fresh event refills
    /// the budget, so a persistent outage keeps converging while a dead
    /// endpoint cannot spin an unbounded timer chain.</summary>
    public const int MaxScheduledRetries = 5;

    private readonly object _gate = new();
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryDelay;
    private readonly Func<DateTime> _clock;
    private DateTime? _lastRebuildUtc;
    private bool _failing;
    private int _retryBudget;
    // Epoch for scheduled retries: ShouldRebuild, NoteFramesObserved and a
    // successful TryClaimRetry all bump it, so any timer holding an older
    // ticket strands (single-use, superseded-by-newer-event, and
    // stranded-by-recovery all fall out of this one counter).
    private long _retryTicket;

    public CaptureRecoveryPolicy(TimeSpan? debounce = null, Func<DateTime>? clock = null,
                                 TimeSpan? retryDelay = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True while capture is known to be down (fault, or a failed rebuild).</summary>
    public bool IsFailing { get { lock (_gate) return _failing; } }

    /// <summary>Capture faulted or failed to start: arm recovery.</summary>
    public void NoteFault() { lock (_gate) _failing = true; }

    /// <summary>
    /// True when this device event should drive a rebuild now. Records the
    /// attempt time (so a burst of notifications produces exactly one rebuild),
    /// refills the retry budget, and bumps the retry ticket (a fresh endpoint
    /// event supersedes any pending scheduled retry).
    /// </summary>
    public bool ShouldRebuild()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastRebuildUtc is { } last && now - last < _debounce) return false;
            _lastRebuildUtc = now;
            _retryBudget = MaxScheduledRetries;
            _retryTicket++;
            return true;
        }
    }

    /// <summary>A (re)build attempt failed: capture is (still) down.</summary>
    public void NoteRebuildFailed() { lock (_gate) _failing = true; }

    /// <summary>
    /// After a failed rebuild: true when a one-shot retry should be scheduled,
    /// handing out the delay and the ticket the timer must later claim.
    /// Bounded by <see cref="MaxScheduledRetries"/> per endpoint event.
    /// </summary>
    public bool TryScheduleRetry(out TimeSpan delay, out long ticket)
    {
        lock (_gate)
        {
            delay = _retryDelay;
            ticket = _retryTicket;
            if (!_failing || _retryBudget <= 0) return false;
            _retryBudget--;
            return true;
        }
    }

    /// <summary>
    /// Called by the timer when it fires: true when the retry may run. Single
    /// use - a successful claim bumps the ticket, so a duplicate timer
    /// strands. False when superseded by a newer endpoint event or when
    /// recovery already happened (a stale timer must never rebuild a healthy
    /// stream). A claimed retry IS a rebuild attempt, so it restarts the
    /// debounce window too.
    /// </summary>
    public bool TryClaimRetry(long ticket)
    {
        lock (_gate)
        {
            if (!_failing) return false;
            if (ticket != _retryTicket) return false;
            _retryTicket++;
            _lastRebuildUtc = _clock();
            return true;
        }
    }

    /// <summary>
    /// A non-empty frame was observed from the LIVE source. Returns true
    /// exactly once per failing episode - THE recovery signal, and the only
    /// thing that clears the microphone condition. Also strands any pending
    /// scheduled retry (there is nothing left to retry). Cheap no-op (false)
    /// on every frame of a healthy stream.
    /// </summary>
    public bool NoteFramesObserved()
    {
        lock (_gate)
        {
            if (!_failing) return false;
            _failing = false;
            _retryTicket++;
            return true;
        }
    }
}
