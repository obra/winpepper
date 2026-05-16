using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorBusTests
{
    [Fact]
    public void Report_Notifies_Active_Subscribers()
    {
        var bus = new ErrorBus(capacity: 10);
        var received = new List<ErrorRecord>();
        using var _ = bus.Subscribe(received.Add);

        bus.Report(ErrorStage.Asr, new InvalidOperationException("model load"), Guid.NewGuid());

        received.Count.ShouldBe(1);
        received[0].Stage.ShouldBe(ErrorStage.Asr);
        received[0].Message.ShouldBe("model load");
    }

    [Fact]
    public void Recent_Returns_Newest_First_Capped_At_Capacity()
    {
        var bus = new ErrorBus(capacity: 3);
        var sid = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            bus.Report(ErrorStage.Cleanup, new Exception($"e{i}"), sid);

        var recent = bus.Recent();
        recent.Count.ShouldBe(3);
        recent[0].Message.ShouldBe("e4");
        recent[2].Message.ShouldBe("e2");
    }

    [Fact]
    public void MostRecent_Returns_Null_When_Empty()
    {
        var bus = new ErrorBus(capacity: 10);
        bus.MostRecent().ShouldBeNull();
    }

    [Fact]
    public void Subscribe_Disposing_Stops_Notifications()
    {
        var bus = new ErrorBus(capacity: 10);
        var received = new List<ErrorRecord>();
        var sub = bus.Subscribe(received.Add);
        bus.Report(ErrorStage.Injection, new Exception("a"), Guid.NewGuid());
        sub.Dispose();
        bus.Report(ErrorStage.Injection, new Exception("b"), Guid.NewGuid());
        received.Count.ShouldBe(1);
    }
}
