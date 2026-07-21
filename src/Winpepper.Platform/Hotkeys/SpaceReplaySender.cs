using System.Runtime.InteropServices;
using Winpepper.Platform.Injection;

namespace Winpepper.Platform.Hotkeys;

public readonly record struct SpaceReplayResult(
    bool Success,
    uint InitialInputsSent,
    bool RepairAttempted,
    bool RepairSucceeded)
{
    public static SpaceReplayResult Succeeded => new(true, 2, false, false);
}

/// <summary>Builds a marked Space down/up pair and repairs a partial down send.</summary>
internal static class SpaceReplaySender
{
    public static SpaceReplayResult Send(Func<SendInputNative.INPUT[], uint> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        var pair = BuildPair();
        var sent = send(pair);
        if (sent == 2) return SpaceReplayResult.Succeeded;
        if (sent != 1) return new(false, sent, false, false);

        // SendInput preserves array order. A partial result of one means the
        // marked key-down was inserted but its key-up was not. Immediately
        // make a best-effort standalone key-up repair.
        var repairSent = send(new[] { pair[1] });
        return new(false, sent, true, repairSent == 1);
    }

    public static SpaceReplayResult SendToWindows()
    {
        if (!OperatingSystem.IsWindows()) return new(false, 0, false, false);
        return Send(inputs => SendInputNative.SendInput(
            (uint)inputs.Length, inputs, Marshal.SizeOf<SendInputNative.INPUT>()));
    }

    private static SendInputNative.INPUT[] BuildPair() =>
    [
        new SendInputNative.INPUT
        {
            Type = SendInputNative.INPUT_KEYBOARD,
            Keyboard = new SendInputNative.KEYBDINPUT
            {
                Vk = VirtualKeyCatalog.Space,
                ExtraInfo = HotkeyHook.SpaceReplayExtraInfo,
            },
        },
        new SendInputNative.INPUT
        {
            Type = SendInputNative.INPUT_KEYBOARD,
            Keyboard = new SendInputNative.KEYBDINPUT
            {
                Vk = VirtualKeyCatalog.Space,
                Flags = SendInputNative.KEYEVENTF_KEYUP,
                ExtraInfo = HotkeyHook.SpaceReplayExtraInfo,
            },
        },
    ];
}
