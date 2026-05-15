using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

[Trait("Platform", "Windows")]
public class HotkeyHookIntegrationTests
{
    [Fact]
    public async Task Hook_Installs_And_DisposesCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var hook = new HotkeyHook(
            HotkeyChord.Parse("RightCtrl+RightShift"),
            HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>());

        hook.Start();
        await Task.Delay(200);
        // Just confirm we can dispose without hanging.
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT { [FieldOffset(0)] public int Type; [FieldOffset(8)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [Fact(Skip = "Manual: requires injecting Esc via SendInput, captured by our own hook. Enable when on the VM.")]
    public async Task Hook_ObservesSyntheticEscKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var hook = new HotkeyHook(
            HotkeyChord.Parse("RightCtrl+RightShift"),
            HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>());

        hook.Start();

        var inputs = new[]
        {
            new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Vk = 0x1B, Flags = 0 } },
            new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Vk = 0x1B, Flags = 2 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()).ShouldBe(2u);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var got = await hook.Events.ReadAsync(cts.Token);
        got.Kind.ShouldBe(HotkeyEventKind.Cancel);
    }
}
