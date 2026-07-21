using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

public class RawCaptureTests
{
    private static HotkeyHook NewHook()
        => new(HotkeyChord.Parse("Alt+Win"), HotkeyChord.Parse("F24"),
            HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
            keyPhysicallyDown: _ => true);

    [Fact]
    public void CaptureLease_ForwardsRawTransitionsWithoutFiringConfiguredTriggers()
    {
        var hook = NewHook();
        var captured = new List<RawKeyTransition>();
        using var lease = hook.BeginRawCapture(captured.Add);

        hook.TryProcessKey(VK_LMENU, down: true, out var first).ShouldBeFalse();
        hook.TryProcessKey(VK_LWIN, down: true, out var second).ShouldBeFalse();
        hook.TryProcessKey(VK_LWIN, down: false, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LMENU, down: false, out _).ShouldBeFalse();

        first.ShouldBeNull();
        second.ShouldBeNull();
        captured.Select(e => (e.VirtualKey, e.IsDown)).ShouldBe(new[]
        {
            (VK_LMENU, true), (VK_LWIN, true), (VK_LWIN, false), (VK_LMENU, false),
        });
    }

    [Fact]
    public void CaptureAndDrainRemainAvailableWhileNormalTriggersAreDisabled()
    {
        var enabled = false;
        var hook = new HotkeyHook(
            HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true,
            normalTriggersEnabled: () => enabled);
        var captured = new List<RawKeyTransition>();
        var lease = hook.BeginRawCapture(captured.Add);

        hook.TryProcessKey(0x87, true, out var capturedEvent).ShouldBeFalse();
        capturedEvent.ShouldBeNull();
        captured.Count.ShouldBe(1);
        lease.Dispose();

        hook.TryProcessKey(0x87, true, out var drainRepeat).ShouldBeFalse();
        hook.TryProcessKey(0x87, false, out var drainUp).ShouldBeFalse();
        drainRepeat.ShouldBeNull();
        drainUp.ShouldBeNull();

        enabled = true;
        hook.TryProcessKey(0x87, true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void CaptureLease_IsExclusiveAndDisposeRestoresNormalProcessing()
    {
        var hook = NewHook();
        var lease = hook.BeginRawCapture(_ => { });
        Should.Throw<InvalidOperationException>(() => hook.BeginRawCapture(_ => { }));

        lease.Dispose();
        lease.Dispose();

        hook.TryProcessKey(0x87, down: true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void CaptureMarksTypematicDownAsRepeat()
    {
        var hook = NewHook();
        var captured = new List<RawKeyTransition>();
        using var lease = hook.BeginRawCapture(captured.Add);

        hook.TryProcessKey(0x87, down: true, out _);
        hook.TryProcessKey(0x87, down: true, out _);

        captured[0].IsRepeat.ShouldBeFalse();
        captured[1].IsRepeat.ShouldBeTrue();
    }

    [Fact]
    public void RecorderCompletesAltWinFromRawHookEvents()
    {
        var recorder = new ChordRecorder();
        recorder.Begin();

        recorder.OnRawKey(new RawKeyTransition(VK_LMENU, 0, true, false, false));
        recorder.OnRawKey(new RawKeyTransition(VK_LWIN, 0, true, false, false));
        recorder.OnRawKey(new RawKeyTransition(VK_LWIN, 0, false, false, false))
            .ShouldBe(ChordKeyResult.Committed);

        recorder.CommittedChord.ShouldBe("LeftAlt+LeftWin");
    }

    [Theory]
    [InlineData(0x87, "F24")]
    [InlineData(0x5D, "Application")]
    public void RecorderCompletesDedicatedKeyFromRawHookEvent(int virtualKey, string expected)
    {
        var recorder = new ChordRecorder();
        recorder.Begin();

        recorder.OnRawKey(new RawKeyTransition(virtualKey, 0, true, false, false))
            .ShouldBe(ChordKeyResult.Committed);
        recorder.CommittedChord.ShouldBe(expected);
    }

    [Fact]
    public void RecorderCanonicalizesCopilotHardwareSequence()
    {
        var recorder = new ChordRecorder();
        recorder.Begin();
        recorder.OnRawKey(new RawKeyTransition(VK_LSHIFT, 0, true, false, false));
        recorder.OnRawKey(new RawKeyTransition(VK_LWIN, 0, true, false, false));

        recorder.OnRawKey(new RawKeyTransition(0x86, 0, true, false, false))
            .ShouldBe(ChordKeyResult.Committed);
        recorder.CommittedChord.ShouldBe("Copilot");
    }
}
