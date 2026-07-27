using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

public sealed class TextInjector
{
    /// <summary>How long to wait for the user to release held modifiers.</summary>
    private const int ModifierWaitTimeoutMs = 1500;
    private const int ModifierWaitPollMs = 15;

    /// <summary>UTF-16 code units per guarded send chunk (Task: mid-paste focus fallback).</summary>
    internal const int ChunkCodeUnits = 32;

    /// <summary>
    /// Pause between guarded send chunks. Load-bearing (validation ledger, A1):
    /// SendInput is queue-insertion (~µs per call), so an UNPACED loop finishes
    /// in single-digit milliseconds and the mid-paste guard could never observe
    /// a human focus change. 20 ms/chunk ≈ 1600 code units/s -- far faster than
    /// any typist, slow enough that a long paste spans the human reaction
    /// window (a 1000-unit paste ≈ 0.6 s).
    /// </summary>
    internal const int InterChunkPauseMs = 20;

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
        _sleep = sleep ?? Thread.Sleep;
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

    public bool TryInject(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        NeutralizeHeldModifiers();
        return _sendChunk(text);
    }

    private void NeutralizeHeldModifiers()
    {
        // A physically-held modifier (e.g. Ctrl still down from the dictation
        // chord, or held while clicking the pending-paste pill) is applied by
        // the target app to every injected character — turning the text into
        // control characters / accelerator shortcuts. Wait briefly for release;
        // if the user keeps holding, synthesize releases (KEYUP only — never
        // re-press, so their eventual physical release is a harmless no-op).
        if (!ModifierGuard.WaitForRelease(() => ModifierGuard.AnyDown(_isKeyDown),
                ModifierWaitTimeoutMs, ModifierWaitPollMs, Thread.Sleep))
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
