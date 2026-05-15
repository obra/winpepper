using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

public sealed class TextInjector
{
    private readonly ILogger<TextInjector> _log;
    public TextInjector(ILogger<TextInjector> log) => _log = log;

    public bool TryInject(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
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
