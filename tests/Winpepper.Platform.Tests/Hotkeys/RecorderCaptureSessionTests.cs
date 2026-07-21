using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class RecorderCaptureSessionTests
{
    [Fact]
    public void SecondRecorderCannotStealFirstLeaseOrDisposeIt()
    {
        using var hook = new HotkeyHook(
            HotkeyChord.Parse("F23"), HotkeyChord.Parse("F24"), HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true);
        using var first = new RecorderCaptureSession(hook.BeginRawCapture);
        using var second = new RecorderCaptureSession(hook.BeginRawCapture);
        var firstEvents = new List<RawKeyTransition>();

        first.TryBegin(firstEvents.Add, out var firstError).ShouldBeTrue();
        firstError.ShouldBeNull();
        second.TryBegin(_ => { }, out var secondError).ShouldBeFalse();
        secondError.ShouldNotBeNullOrWhiteSpace();

        second.End();
        hook.TryProcessKey(0x41, true, out _);
        firstEvents.Count.ShouldBe(1);

        first.End();
        second.TryBegin(_ => { }, out secondError).ShouldBeTrue();
        secondError.ShouldBeNull();
    }
}
