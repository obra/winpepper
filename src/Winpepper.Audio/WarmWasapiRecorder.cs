#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single capture runs for
/// the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll).
///
/// All lifecycle/concurrency/ring/fault logic lives in the pure-managed,
/// Linux-tested <see cref="WarmCaptureCoordinator"/> behind the
/// <see cref="ICaptureSource"/> seam, and all recovery DECISIONS live in the
/// pure <see cref="CaptureRecoveryPolicy"/>. This class is the thin Windows
/// shell that supplies the NAudio <see cref="WasapiCaptureSource"/> factory,
/// re-resolves the default input device on session start, and subscribes to
/// endpoint notifications so a device that comes back after a sleep/resume
/// rebuilds capture WITHOUT waiting for the next hotkey press (2026-07-24
/// incident).
///
/// RECOVERY IS PROVEN BY FRAMES, not by IsRunning: NAudio starts the WASAPI
/// pump asynchronously, so "IsRunning right after a rebuild" only proves
/// AudioClient.Initialize succeeded (the stream can fault ms later - the
/// incident's own 0x88890004). Rebuilds are DRIVEN by endpoint events, by a
/// bounded one-shot retry after a failed rebuild, and by the session-start
/// force-start; the recovery SIGNAL fires exactly once per failing episode, on
/// the first observed non-empty frame from the live (epoch-guarded) source.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    private const int RingCapacitySamples = SampleRate16k; // ~1 s of history

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly WarmCaptureCoordinator _coordinator;
    private readonly CaptureRecoveryPolicy _recovery = new();
    private readonly AudioEndpointWatcher? _endpointWatcher;
    // Set FIRST in Dispose (Volatile.Write) so a scheduled retry that fires
    // during teardown never touches a disposed coordinator.
    private int _disposed;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception>? CaptureFaulted;
    public event Action? CaptureRecovered;

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null, ILogger? log = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        _log = log;
        _coordinator = new WarmCaptureCoordinator(
            _buffer,
            sourceFactory: () => new WasapiCaptureSource(_deviceId, _log));
        _coordinator.FramesAvailable += f => FramesAvailable?.Invoke(f);
        // FRAMES-DRIVEN CLEARING: the one signal that cannot lie.
        _coordinator.FrameObserved += OnFrameObserved;
        _coordinator.CaptureFaulted += ex =>
        {
            // Capture is (or may be) down: arm recovery so the next endpoint
            // event actually retries. The coordinator raises this even when
            // its own in-lock retry SUCCEEDED (load-bearing for observability,
            // pinned by an existing test - do NOT change it); in that
            // self-healed case the very next frame clears the false arming
            // within ~50 ms, before any endpoint event could act on it.
            _recovery.NoteFault();
            CaptureFaulted?.Invoke(ex);
            // RECONCILE - closes the arm/report race, which is otherwise a
            // PERMANENT tray error on a healthy microphone (the exact defect
            // this plan exists to remove, relocated from the pill to the tray).
            //
            // The race: NoteFramesObserved() is one-shot per failing episode,
            // and the live capture thread delivers a frame every ~50 ms. In the
            // self-healed case a NEW source is already pumping when NoteFault()
            // arms above, so a frame can consume the recovery signal WHILE the
            // Invoke above is still walking _log.LogError + the ErrorBus's
            // synchronous subscriber fan-out. That early recovery reaches an
            // EMPTY condition map (NotifyConditionRecovered's
            // `_activeConditions.Remove(stage)` returns false and it returns
            // silently), and EnterCondition then records the condition with the
            // policy already healthy - so no future frame can ever emit another
            // recovery, and NOTHING else is allowed to clear a condition.
            //
            // Why re-reading the flag HERE is sufficient: the Invoke above is
            // synchronous all the way to the VM's _ui.Post, so by the time it
            // returns the EnterCondition callback is ENQUEUED. Anything we post
            // now lands after it. Both interleavings are then covered, because
            // _failing only ever goes true->false once per episode under the
            // policy's lock:
            //   frame BEFORE this read -> IsFailing is false -> we raise the
            //     recovery here, enqueued after the entry -> condition clears.
            //   frame AFTER this read  -> IsFailing is true, so that frame's
            //     NoteFramesObserved() still returns true -> OnFrameObserved
            //     raises, also after the entry -> condition clears.
            // A duplicate CaptureRecovered is harmless: clearing an absent
            // condition is a silent no-op at the VM (load-bearing - see Task 3).
            // No log line here: OnFrameObserved already emitted the single
            // "Microphone capture recovered (frames observed)" line for this
            // episode, and Task 9's gate expects exactly that one line.
            if (!_recovery.IsFailing) CaptureRecovered?.Invoke();
        };

        try
        {
            _endpointWatcher = new AudioEndpointWatcher(OnCaptureEndpointChanged, _log);
        }
        catch (Exception ex)
        {
            // Non-fatal: we simply fall back to the session-start recovery seam.
            _log?.LogWarning(ex, "audio endpoint notifications unavailable; recovery falls back to session start");
        }

        if (_prewarm)
        {
            _coordinator.EnsureStarted();
            // A prewarm start that never came up (e.g. no default endpoint at
            // boot) is a failing state the endpoint watcher should retry.
            if (!_coordinator.IsRunning) _recovery.NoteFault();
        }
    }

    public void StartSession(int includePrerollMs)
    {
        // Follow the default input device: if it drifted since the warm stream
        // was built, rebuild on the new endpoint (clears the ring too).
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode, or a previously faulted warm stream: (re)start now. force
        // bypasses the fault backoff because the user explicitly asked to record.
        // NO recovery signal is raised here: IsRunning right after a start only
        // proves AudioClient.Initialize succeeded (NAudio starts the WASAPI
        // pump asynchronously), so clearing waits for the first non-empty
        // frame - which a genuinely restarted stream delivers within ~50 ms
        // via OnFrameObserved. The session-start seam still DRIVES recovery;
        // it just no longer CLAIMS it.
        _coordinator.EnsureStarted(force: true);
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        var preroll = _buffer.StartSession(prerollSamples);
        // The seeded pre-roll never flows through Ingest during the session, so
        // raise it here — otherwise streaming consumers (Task 10's frame tee)
        // would be missing the dictation's first ~500 ms. The level meter also
        // subscribes; one larger frame at session start is harmless.
        if (preroll.Length > 0) FramesAvailable?.Invoke(preroll);
    }

    /// <summary>
    /// Runs on the coordinator's frame-ingest path for every non-empty frame
    /// of the LIVE (epoch-guarded) source. The FIRST such frame of a failing
    /// episode is the recovery: proof the WASAPI pump is delivering audio
    /// end-to-end, which neither IsRunning nor a validity probe can give.
    /// NoteFramesObserved is a cheap no-op (false) on every frame of a healthy
    /// stream. This is the only place the recovery LOG LINE is emitted, and the
    /// only place the one-shot signal is consumed; the CaptureFaulted handler
    /// re-raises CaptureRecovered (without logging) when a frame consumed the
    /// signal before the condition existed - see the ordering invariant there.
    /// </summary>
    private void OnFrameObserved()
    {
        if (!_recovery.NoteFramesObserved()) return;
        _log?.LogInformation("Microphone capture recovered (frames observed)");
        CaptureRecovered?.Invoke();
    }

    /// <summary>
    /// A capture endpoint arrived or the default changed. Runs on a thread-pool
    /// thread (never the COM callback thread - see
    /// <see cref="AudioEndpointWatcher"/>); several such handlers can run
    /// CONCURRENTLY, which is why every decision is behind the policy's lock.
    /// Only acts when capture is known to be failing: a healthy warm stream
    /// keeps running, and the existing session-start drift check still follows
    /// the default device. AttemptRebuild applies two further guards (live
    /// dictation, prewarm off) that defer the rebuild rather than destroy audio
    /// or open the mic at idle.
    /// </summary>
    private void OnCaptureEndpointChanged()
    {
        if (!_recovery.IsFailing) return;
        if (!_recovery.ShouldRebuild())
        {
            // Endpoint events burst on resume: leading edge only.
            _log?.LogDebug("Endpoint event suppressed by the rebuild debounce");
            return;
        }
        AttemptRebuild("device change");
    }

    /// <summary>
    /// One rebuild attempt plus the bounded one-shot retry. The retry is NOT a
    /// validity probe and does NOT clear anything: it re-runs the recovery
    /// (Rebuild) and lets success clear the condition via frames - the plan's
    /// endorsed "retry the recovery and let success clear it". Without it, a
    /// resume whose notification burst ends before the endpoint is usable (a
    /// default-device change is documented as exactly three back-to-back
    /// calls) would stall forever - the incident's exact symptom.
    /// </summary>
    private void AttemptRebuild(string trigger)
    {
        // Two states in which a rebuild would do more harm than the fault it is
        // healing. Checked on EVERY attempt, not just the first: the scheduled
        // retry lands hundreds of ms later, when either can have become true.
        //
        //   1. A dictation is in flight. Rebuild() calls _buffer.Clear()
        //      (WarmCaptureCoordinator.cs:88), which would destroy the audio
        //      the user is speaking right now.
        //   2. Prewarm is off. Rebuild() unconditionally StartLocked()s a
        //      source, so in cold mode it would open the microphone at idle -
        //      lighting the OS mic-in-use indicator for a user who turned the
        //      warm mic OFF - and leave it open until the next StopSession.
        //      Nothing is running at idle in cold mode, so there is also
        //      nothing here to recover.
        //
        // Recovery is DEFERRED, not abandoned: the next StartSession runs
        // EnsureStarted(force: true) and the first non-empty frame clears the
        // condition via OnFrameObserved. Deliberately does NOT consume retry
        // budget (nothing was attempted) and does NOT clear anything.
        if (_buffer.IsSessionActive)
        {
            _log?.LogDebug(
                "Microphone rebuild ({Trigger}) deferred: a dictation is in flight", trigger);
            return;
        }
        if (!_prewarm)
        {
            _log?.LogDebug(
                "Microphone rebuild ({Trigger}) deferred: prewarm is off, so no capture runs at idle",
                trigger);
            return;
        }

        _coordinator.Rebuild();
        if (_coordinator.IsRunning)
        {
            // Success is NOT claimed here: the pump starts asynchronously and
            // can still fault ms from now (0x88890004 - the incident's own
            // signature). The first non-empty frame clears via OnFrameObserved.
            return;
        }

        // The no-endpoint failure leg (0x80070490) IS synchronous, so failure
        // detection here is sound. The exception already reached the ErrorBus
        // via CaptureFaulted; both log lines below are content-free.
        _recovery.NoteRebuildFailed();
        if (_recovery.TryScheduleRetry(out var delay, out var ticket))
        {
            _log?.LogWarning(
                "Microphone rebuild ({Trigger}) did not succeed; one-shot retry in {DelayMs} ms",
                trigger, (int)delay.TotalMilliseconds);
            _ = Task.Delay(delay).ContinueWith(
                _ =>
                {
                    // TryClaimRetry makes the timer single-use and strands it
                    // when a newer event or a recovery superseded it; the
                    // _disposed gate strands it across teardown.
                    if (Volatile.Read(ref _disposed) == 0 && _recovery.TryClaimRetry(ticket))
                        AttemptRebuild("scheduled retry");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _log?.LogWarning(
                "Microphone rebuild ({Trigger}) did not succeed; retry budget spent, waiting for the next device event",
                trigger);
        }
    }

    private void RebuildIfDefaultChanged()
    {
        if (!_coordinator.IsRunning) return; // nothing live; EnsureStarted picks the current default
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            if (current.ID != _coordinator.ActiveDeviceId)
                _coordinator.Rebuild();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "default-device recheck failed; keeping current warm stream");
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) _coordinator.StopCapture(); // cold mode tears down between sessions
        return samples;
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, 1);    // strand any scheduled retry FIRST
        _endpointWatcher?.Dispose();         // then stop endpoint callbacks
        _coordinator.Dispose();              // then tear capture down
    }
}
#endif
