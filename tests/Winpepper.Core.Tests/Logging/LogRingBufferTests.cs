using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class LogRingBufferTests
{
    [Fact]
    public void Append_Preserves_Insertion_Order_Below_Capacity()
    {
        var buf = new LogRingBuffer(capacity: 5);
        for (var i = 0; i < 3; i++)
            buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"line {i}"));

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(3);
        snap[0].Message.ShouldBe("line 0");
        snap[2].Message.ShouldBe("line 2");
    }

    [Fact]
    public void Append_Evicts_Oldest_When_Capacity_Exceeded()
    {
        var buf = new LogRingBuffer(capacity: 3);
        for (var i = 0; i < 6; i++)
            buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"line {i}"));

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(3);
        snap[0].Message.ShouldBe("line 3");
        snap[1].Message.ShouldBe("line 4");
        snap[2].Message.ShouldBe("line 5");
    }

    [Fact]
    public void Appended_Event_Fires_Per_Append()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var heard = 0;
        buf.Appended += _ => heard++;
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "x"));
        buf.Append(new LogTailEntry(DateTime.UtcNow, "WRN", "y"));
        heard.ShouldBe(2);
    }

    [Fact]
    public void Snapshot_Is_Defensive_Copy()
    {
        var buf = new LogRingBuffer(capacity: 5);
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "a"));
        var snap = buf.Snapshot();
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "b"));
        snap.Count.ShouldBe(1); // stale snapshot unchanged
    }
}
