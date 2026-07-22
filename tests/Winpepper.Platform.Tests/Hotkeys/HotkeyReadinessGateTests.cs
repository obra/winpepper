using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyReadinessGateTests
{
    [Fact]
    public void DisabledGateDrainsWithoutAcceptingTrigger()
    {
        var gate = new HotkeyReadinessGate();
        gate.IsEnabled.ShouldBeFalse();
        gate.ShouldHandle(new HotkeyEvent(HotkeyEventKind.Toggle,
            DateTimeOffset.Parse("2026-07-21T12:00:00Z"))).ShouldBeFalse();
    }

    [Fact]
    public void EnablingRejectsStaleQueuedEventsAndAcceptsNewEvents()
    {
        var gate = new HotkeyReadinessGate();
        var readyAt = DateTimeOffset.Parse("2026-07-21T12:00:00Z");
        gate.Enable(readyAt);
        gate.IsEnabled.ShouldBeTrue();

        gate.ShouldHandle(new HotkeyEvent(HotkeyEventKind.Toggle,
            readyAt.AddTicks(-1))).ShouldBeFalse();
        gate.ShouldHandle(new HotkeyEvent(HotkeyEventKind.Toggle,
            readyAt)).ShouldBeTrue();

        gate.Disable();
        gate.IsEnabled.ShouldBeFalse();
    }
}
