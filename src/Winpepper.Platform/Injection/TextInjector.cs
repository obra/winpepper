using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

public sealed class TextInjector
{
    /// <summary>How long to wait for the user to release held modifiers.</summary>
    private const int ModifierWaitTimeoutMs = 1500;
    private const int ModifierWaitPollMs = 15;

    private readonly ILogger<TextInjector> _log;
    private readonly Func<int, bool> _isKeyDown;

    public TextInjector(ILogger<TextInjector> log, Func<int, bool>? isKeyDown = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
    }

    private static bool DefaultKeyProbe(int vk)
        => OperatingSystem.IsWindows()
           && (Winpepper.Platform.Hotkeys.KeyboardHookNative.GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool TryInject(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;

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

        var inputs = BuildKeyDownUpInputs(ToCodeUnits(text));
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
