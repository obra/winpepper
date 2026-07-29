using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

public sealed class TextInjector
{
    /// <summary>How long to wait for the user to release held modifiers.</summary>
    private const int ModifierWaitTimeoutMs = 1500;
    private const int ModifierWaitPollMs = 15;

    /// <summary>
    /// How long to wait for a physically-held mouse button to be released
    /// before the guarded send starts. The pending-paste pill fires on
    /// PointerPressed (the button-DOWN edge) and TryPastePending runs
    /// synchronously inside that handler, so at entry the initiating button
    /// is still down -- without this wait the mouse half of the halt
    /// predicate would self-cancel every pill click (deterministically, not
    /// as a race). GetAsyncKeyState reads physical device state, so the
    /// release is observable even though the blocked UI thread never pumps
    /// the pointer-up message. Unlike modifiers there is no safe
    /// neutralization on timeout (a synthesized button-up would fabricate a
    /// click), so a button still held past this budget aborts the run and
    /// the text stays pending.
    /// </summary>
    private const int MouseWaitTimeoutMs = 1500;
    private const int MouseWaitPollMs = 15;

    /// <summary>
    /// UTF-16 code units per guarded send chunk. Also the worst-case bleed
    /// bound: at most ~one in-flight chunk can land in a newly focused window
    /// when the user switches mid-paste (mid-paste focus fallback, AD-1 --
    /// hardened from 32 to 8 by the bleed-hardening task).
    /// </summary>
    internal const int ChunkCodeUnits = 8;

    /// <summary>
    /// CEILING on the guarded send's feed rate, in UTF-16 code units per
    /// second. Chosen to match the observed render rate of slow-rendering
    /// target apps (~600 chars/s): when feed &lt;= render, the
    /// queued-but-undelivered BACKLOG cannot grow, so a mid-paste window
    /// switch can leak at most the true in-flight chunk
    /// (&lt;= <see cref="ChunkCodeUnits"/>). The previous 1600 units/s
    /// design point fed slow apps ~2.5x faster than they rendered; the
    /// growing backlog followed focus on a human click-switch and sprayed
    /// dozens of characters (paste-path-hardening, 2026-07-27 -- a
    /// deliberate, owner-approved supersession of the bleed-hardening
    /// plan's "&gt;= 1600 nominal" feed-rate floor).
    /// Semantics under deadline pacing (2026-07-28): this remains a
    /// bleed-safety CEILING the feed may approach but never exceed. The
    /// pacer subtracts the MEASURED send time from each pause (see
    /// <see cref="DeadlinePacer"/>), so the actual feed sits near the 571
    /// units/s nominal instead of the ~250-285 units/s the old full-pause
    /// design delivered on this host, where the SendInput call itself costs
    /// ~1 ms/event (measured 2026-07-28; a 458-char paste took ~1.6 s
    /// against the ~0.8 s design point).
    /// </summary>
    internal const int TargetFeedUnitsPerSecond = 600;

    /// <summary>
    /// Minimum per-chunk PERIOD (send + sleep) for the guarded send,
    /// derived from <see cref="TargetFeedUnitsPerSecond"/> by CEILING
    /// division: ceil(8 * 1000 / 600) = 14 ms, i.e. ~571 code units/s
    /// nominal -- rounded UP so the nominal feed can never EXCEED the
    /// target (truncating division gives 13 ms = ~615 units/s, above the
    /// claimed render rate, and the backlog would grow again; stage-2
    /// ledger A1). Load-bearing measurement (2026-07-28, live probes on the
    /// production host): SendInput with KEYEVENTF_UNICODE events is NOT
    /// queue-insertion cheap here -- it costs ~0.85-1.13 ms PER EVENT
    /// (linear in events, so ~14-18 ms per 16-event chunk; other low-level
    /// hooks in the environment dominate; Winpepper's own hook adds only
    /// ~0.2 ms/event). An old production log once recorded ~13 us/event, so
    /// the original "queue-insertion (~us per call)" ledger assumption (A1)
    /// is stale for this machine -- which is why pacing is DEADLINE-based:
    /// each chunk sleeps only max(0, ceil(period - measured elapsed)) via
    /// <see cref="DeadlinePacer"/>, where period is scaled per chunk
    /// (<see cref="PeriodMsForChunk"/>: 14 ms for 8-unit chunks, 16 ms for
    /// the 9-unit surrogate-straddle chunks the chunker emits rather than
    /// split a pair -- a fixed 14 ms would let those feed ~643 units/s;
    /// stage-2 ledger A7). The ceiling rounding means the period cannot
    /// undershoot its floor, and a send that alone exceeds the period
    /// sleeps zero -- SendInput itself then throttles the feed below the
    /// ceiling. The pace stays real through PacingWaiter (the production
    /// sleep default): Win32 frames waitable-timer inaccuracy as expiration
    /// DELAYS, and the periods carry 0.67-1 ms of margin over what the 600
    /// ceiling strictly needs, absorbing sub-ms jitter (stage-2 ledger A1).
    /// The Thread.Sleep fail-safe is NOT never-early (documented to
    /// possibly sleep LESS than requested below the ~15.6 ms clock
    /// resolution); a broken timer path is caught by the gate's 5 ms probe
    /// -- STOP and report, never a production regime.
    /// The per-chunk PERIOD floor is pinned on the gate host by
    /// InterChunkPacingWindowsTests.
    /// </summary>
    internal const int InterChunkPauseMs =
        (ChunkCodeUnits * 1000 + TargetFeedUnitsPerSecond - 1) / TargetFeedUnitsPerSecond; // ceiling: feed <= target

    private readonly ILogger<TextInjector> _log;
    private readonly Func<int, bool> _isKeyDown;
    private readonly Func<long> _foregroundHwnd;
    private readonly Func<string, bool> _sendChunk;
    private readonly Action<int> _sleep;
    private readonly Func<long, ForegroundElevation> _foregroundElevation;
    private readonly Func<double> _monotonicMs;

    /// <summary>
    /// hwnd==0 occurrence counts (at-start vs mid-stream), for field
    /// re-evaluation of the park-on-0 polarity. Internal for tests.
    /// </summary>
    internal HwndZeroMeter Meter { get; } = new();

    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null,
        Action<int>? sleep = null,
        Func<long, ForegroundElevation>? foregroundElevation = null,
        Func<double>? monotonicMs = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
        _sleep = sleep ?? PacingWaiter.Wait;
        _foregroundElevation = foregroundElevation ?? ElevationProbe.Probe;
        _monotonicMs = monotonicMs ?? DefaultMonotonicMs;
    }

    private static bool DefaultKeyProbe(int vk)
        => OperatingSystem.IsWindows()
           && (Winpepper.Platform.Hotkeys.KeyboardHookNative.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Foreground HWND as Int64; 0 when unknown (non-Windows, or the call
    /// fails). NOTE: since the park-on-0 polarity (2026-07-28) an unseamed
    /// TryInjectGuarded returns NoForeground (parks) whenever this yields 0
    /// -- including unconditionally off-Windows. Fail-safe by design;
    /// production injection is Windows-only.
    /// </summary>
    private static long DefaultForegroundProbe()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return SendInputNative.GetForegroundWindow().ToInt64(); }
        catch { return 0; }
    }

    /// <summary>Monotonic milliseconds (Stopwatch-based; immune to wall-clock changes).</summary>
    private static double DefaultMonotonicMs()
        => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
           / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Per-chunk minimum period: <see cref="InterChunkPauseMs"/> scaled by
    /// the chunk's actual code-unit count. InjectionChunker extends a chunk
    /// to <see cref="ChunkCodeUnits"/>+1 = 9 units rather than split a
    /// surrogate pair; a fixed 14 ms period would let sustained 9-unit
    /// chunks feed ~643 units/s &gt; 600 (stage-2 ledger A7).
    /// ceil(8 * 14 / 8) = 14 (unchanged); ceil(9 * 14 / 8) = 16 (~562
    /// units/s, 1 ms margin over the 15 ms the 600 ceiling strictly needs).
    /// </summary>
    internal static int PeriodMsForChunk(string chunk)
        => (int)Math.Ceiling(chunk.Length * (double)InterChunkPauseMs / ChunkCodeUnits);

    /// <summary>
    /// Interruptible paste: types the text in chunks of
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, enforcing a per-chunk
    /// PERIOD of at least <see cref="InterChunkPauseMs"/> -- the send's own
    /// measured duration counts toward the period and only the remainder is
    /// slept (<see cref="DeadlinePacer"/>); the paced period is what lets
    /// the guard observe a human halt gesture at a bounded cadence -- and
    /// checking before every chunk that (a) no physical modifier has gone
    /// down (the leading edge of Alt+Tab -- injected Unicode is delivered
    /// with the current physical modifier state applied), (b) no physical
    /// mouse button has gone down (the leading edge of a click-to-switch --
    /// the button is down BEFORE the foreground flips), and (c) the window
    /// that was foreground when this method was entered is STILL foreground.
    /// If any check trips, the remaining chunks are not sent and
    /// <see cref="InjectionRunOutcome.Interrupted"/> is returned so the
    /// caller can hold the WHOLE original text as a pending paste.
    /// Before anything else -- even the preludes -- the foreground window's
    /// process elevation is probed once: an elevated (UIPI-protected) target
    /// returns <see cref="InjectionRunOutcome.BlockedElevated"/> with nothing
    /// typed, because SendInput to such a window is silently dropped while
    /// reporting success (MSDN); the caller parks the FULL text.
    /// The baseline is captured at method entry -- BEFORE the modifier
    /// release-wait (up to 1500 ms) and the mouse release-wait (up to
    /// 1500 ms) -- so a focus change during either wait is caught before the
    /// first keystroke. The modifier check cannot re-trip on its prelude's
    /// timeout: NeutralizeHeldModifiers synthesizes KEYUPs, so after it
    /// returns the observable modifier state is up. The mouse check cannot
    /// self-trip on the pill click that requested the paste: the mouse
    /// prelude waits for the initiating button's release before the run
    /// starts, and a button still held past the timeout ABORTS the run
    /// (Interrupted; the pending slot keeps the full text) because there is
    /// no safe mouse neutralization -- a synthesized button-up would
    /// fabricate a click. Foreground polarity is fail-SAFE (park-on-0,
    /// 2026-07-28): a 0 foreground read parks the FULL text at start
    /// (<see cref="InjectionRunOutcome.NoForeground"/>) and halts mid-stream
    /// (<see cref="InjectionRunOutcome.Interrupted"/>); the meter counts
    /// both. Key/button probes remain fail-open: a probe that cannot observe
    /// reports "up" and never halts.
    /// </summary>
    public InjectionRunOutcome TryInjectGuarded(string text)
    {
        if (string.IsNullOrEmpty(text)) return InjectionRunOutcome.Completed;

        var hwndAtSendStart = _foregroundHwnd();
        // No observable foreground at send start (council majority polarity,
        // probe-gated 2026-07-28): park the FULL text instead of typing into
        // an unknown window. Fail-SAFE -- deliberately opposite to the
        // probe/elevation fail-open below, see InjectionRunOutcome.NoForeground.
        if (hwndAtSendStart == 0)
        {
            var atStartZeroCount = Meter.RecordAtStart();
            _log.LogWarning(
                "Foreground hwnd is 0 at injection start (occurrence #{Count}); not typing -- parking the full text ({Chars} chars)",
                atStartZeroCount, text.Length);
            return InjectionRunOutcome.NoForeground;
        }
        // UIPI pre-check (paste-path-hardening): SendInput into an elevated
        // window is silently dropped while reporting success, so a run
        // against an elevated target would consume the text with nothing
        // delivered. Park instead -- BEFORE any synthesis (even the
        // modifier-neutralizing KEYUPs) and before the release-wait
        // preludes. Fail-open for an unobservable ELEVATION only (Unknown => inject); an absent foreground already parked above.
        if (ElevatedTargetDecider.Decide(hwndAtSendStart, _foregroundElevation(hwndAtSendStart))
            == ElevatedTargetDecision.Park)
        {
            _log.LogInformation(
                "Foreground window is elevated (UIPI would silently drop SendInput); not typing -- holding the full text as pending ({Chars} chars)",
                text.Length);
            return InjectionRunOutcome.BlockedElevated;
        }
        NeutralizeHeldModifiers();
        // Mouse prelude: never START typing while a button is physically
        // down (the pill click that requested this paste is the common
        // case). Timeout => abort, keep the text pending -- never spray.
        if (!ModifierGuard.WaitForRelease(() => MouseButtonGuard.AnyDown(_isKeyDown),
                MouseWaitTimeoutMs, MouseWaitPollMs, _sleep))
        {
            _log.LogInformation(
                "Mouse button still held {Timeout}ms after injection was requested; not typing -- text stays pending",
                MouseWaitTimeoutMs);
            return InjectionRunOutcome.Interrupted;
        }
        var chunks = InjectionChunker.Split(text, ChunkCodeUnits);
        // Deadline pacing: period accounting starts NOW, so the first
        // chunk's guard probes + send count toward the first period.
        var pacer = new DeadlinePacer(InterChunkPauseMs, _sleep, _monotonicMs);
        // Pause k follows chunks[k]; its period scales with THAT chunk's
        // unit count (9-unit straddle chunks get 16 ms -- stage-2 ledger A7).
        var pausedChunks = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks,
            hwndAtSendStart,
            _foregroundHwnd,
            _sendChunk,
            physicalInputDown: () => ModifierGuard.AnyDown(_isKeyDown)
                                     || MouseButtonGuard.AnyDown(_isKeyDown),
            pauseBetweenChunks: () => pacer.PauseForNextChunk(PeriodMsForChunk(chunks[pausedChunks++])),
            onZeroForeground: () =>
            {
                var midStreamZeroCount = Meter.RecordMidStream();
                _log.LogWarning(
                    "Foreground hwnd read 0 mid-paste (occurrence #{Count}); halting -- the full text will be parked",
                    midStreamZeroCount);
            });
        if (outcome == InjectionRunOutcome.Interrupted)
            _log.LogInformation("Injection interrupted: foreground window, physical modifier, or mouse button state changed mid-paste");
        return outcome;
    }

    public bool TryInject(string text)
        => TryInjectGuarded(text) == InjectionRunOutcome.Completed;

    private void NeutralizeHeldModifiers()
    {
        // A physically-held modifier (e.g. Ctrl still down from the dictation
        // chord, or held while clicking the pending-paste pill) is applied by
        // the target app to every injected character — turning the text into
        // control characters / accelerator shortcuts. Wait briefly for release;
        // if the user keeps holding, synthesize releases (KEYUP only — never
        // re-press, so their eventual physical release is a harmless no-op).
        if (!ModifierGuard.WaitForRelease(() => ModifierGuard.AnyDown(_isKeyDown),
                ModifierWaitTimeoutMs, ModifierWaitPollMs, _sleep))
        {
            var held = ModifierGuard.HeldModifiers(_isKeyDown);
            _log.LogInformation(
                "Modifiers still held {Timeout}ms after injection was requested; neutralizing {Count} key(s) before typing",
                ModifierWaitTimeoutMs, held.Count);
            var releases = ModifierGuard.BuildKeyUpInputs(held);
            var released = SendInputNative.SendInput(
                (uint)releases.Length, releases, Marshal.SizeOf<SendInputNative.INPUT>());
            if (released != (uint)releases.Length)
                _log.LogWarning("Modifier neutralization partial send: requested {Req}, sent {Sent}",
                    releases.Length, released);
        }
    }

    private bool SendChunkViaSendInput(string chunk)
    {
        var inputs = BuildKeyDownUpInputs(ToCodeUnits(chunk));
        var sent = SendInputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<SendInputNative.INPUT>());
        if (sent != (uint)inputs.Length)
        {
            _log.LogWarning("SendInput partial send: requested {Req}, sent {Sent}, err 0x{Err:X}",
                inputs.Length, sent, Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    /// <summary>UTF-16 code units (so emoji => surrogate pair, each unit sent separately).</summary>
    internal static ushort[] ToCodeUnits(string text)
    {
        var arr = new ushort[text.Length];
        for (var i = 0; i < text.Length; i++) arr[i] = text[i];
        return arr;
    }

    internal static SendInputNative.INPUT[] BuildKeyDownUpInputs(ReadOnlySpan<ushort> codeUnits)
    {
        var inputs = new SendInputNative.INPUT[codeUnits.Length * 2];
        for (var i = 0; i < codeUnits.Length; i++)
        {
            inputs[i * 2] = new SendInputNative.INPUT
            {
                Type = SendInputNative.INPUT_KEYBOARD,
                Keyboard = new SendInputNative.KEYBDINPUT
                {
                    Vk = 0,
                    Scan = codeUnits[i],
                    Flags = SendInputNative.KEYEVENTF_UNICODE,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };
            inputs[i * 2 + 1] = new SendInputNative.INPUT
            {
                Type = SendInputNative.INPUT_KEYBOARD,
                Keyboard = new SendInputNative.KEYBDINPUT
                {
                    Vk = 0,
                    Scan = codeUnits[i],
                    Flags = SendInputNative.KEYEVENTF_UNICODE | SendInputNative.KEYEVENTF_KEYUP,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };
        }
        return inputs;
    }
}
