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
    /// Pause between guarded send chunks. Load-bearing (validation ledger, A1):
    /// SendInput is queue-insertion (~µs per call), so an UNPACED loop finishes
    /// in single-digit milliseconds and the mid-paste guard could never observe
    /// a human focus change. 5 ms per 8-unit chunk = 1600 code units/s nominal
    /// -- the same design point as the original 32/20 ms tuning -- and the
    /// guard now runs 4x more often, shrinking the worst-case bleed into a
    /// newly focused window from ~32 to ~8 units. The 5 ms pace is real only
    /// through PacingWaiter (the production sleep default): Thread.Sleep(5)
    /// measurably quantizes to ~15.5 ms (bleed-hardening ledger, V1), which
    /// would throttle the feed to ~513 units/s.
    /// </summary>
    internal const int InterChunkPauseMs = 5;

    private readonly ILogger<TextInjector> _log;
    private readonly Func<int, bool> _isKeyDown;
    private readonly Func<long> _foregroundHwnd;
    private readonly Func<string, bool> _sendChunk;
    private readonly Action<int> _sleep;

    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null,
        Action<int>? sleep = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
        _sleep = sleep ?? PacingWaiter.Wait;
    }

    private static bool DefaultKeyProbe(int vk)
        => OperatingSystem.IsWindows()
           && (Winpepper.Platform.Hotkeys.KeyboardHookNative.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Foreground HWND as Int64; 0 when unknown (non-Windows, or the call fails).</summary>
    private static long DefaultForegroundProbe()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return SendInputNative.GetForegroundWindow().ToInt64(); }
        catch { return 0; }
    }

    /// <summary>
    /// Interruptible paste: types the text in chunks of
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, pausing
    /// <see cref="InterChunkPauseMs"/> between chunks (pacing is what makes
    /// the guard able to observe a human halt gesture at all -- an unpaced
    /// loop is queue-insertion-fast and finishes in milliseconds) and
    /// checking before every chunk that (a) no physical modifier has gone
    /// down (the leading edge of Alt+Tab -- injected Unicode is delivered
    /// with the current physical modifier state applied), (b) no physical
    /// mouse button has gone down (the leading edge of a click-to-switch --
    /// the button is down BEFORE the foreground flips), and (c) the window
    /// that was foreground when this method was entered is STILL foreground.
    /// If any check trips, the remaining chunks are not sent and
    /// <see cref="InjectionRunOutcome.Interrupted"/> is returned so the
    /// caller can hold the WHOLE original text as a pending paste.
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
    /// fabricate a click. Fail-open: if the foreground window cannot be
    /// determined (probe returns 0) the HWND guard is disabled, and a
    /// key/button probe that cannot observe reports "up" and never halts.
    /// </summary>
    public InjectionRunOutcome TryInjectGuarded(string text)
    {
        if (string.IsNullOrEmpty(text)) return InjectionRunOutcome.Completed;

        var hwndAtSendStart = _foregroundHwnd();
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
        var outcome = GuardedInjectionRun.Execute(
            chunks,
            hwndAtSendStart,
            _foregroundHwnd,
            _sendChunk,
            physicalInputDown: () => ModifierGuard.AnyDown(_isKeyDown)
                                     || MouseButtonGuard.AnyDown(_isKeyDown),
            pauseBetweenChunks: () => _sleep(InterChunkPauseMs));
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
