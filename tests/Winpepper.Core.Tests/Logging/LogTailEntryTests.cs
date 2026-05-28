using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class LogTailEntryTests
{
    [Fact]
    public void TimestampLocal_Converts_From_Utc()
    {
        var utc = new DateTime(2026, 5, 28, 11, 21, 54, DateTimeKind.Utc);
        var entry = new LogTailEntry(utc, "INF", "anything");

        entry.TimestampLocal.ShouldBe(utc.ToLocalTime());
        entry.TimestampLocal.Kind.ShouldBe(DateTimeKind.Local);
    }

    [Fact]
    public void TimestampLocal_Treats_Unspecified_Kind_As_Utc()
    {
        // RingBufferSink writes Timestamp.UtcDateTime, which always has Kind=Utc;
        // but if anything ever constructs a LogTailEntry with Kind=Unspecified
        // (e.g. via deserialization), it must not silently be displayed as local.
        var unspec = new DateTime(2026, 5, 28, 11, 21, 54, DateTimeKind.Unspecified);
        var entry = new LogTailEntry(unspec, "INF", "anything");

        var expected = DateTime.SpecifyKind(unspec, DateTimeKind.Utc).ToLocalTime();
        entry.TimestampLocal.ShouldBe(expected);
    }

    [Fact]
    public void TimestampLocal_Passes_Local_Through_Unchanged()
    {
        var local = new DateTime(2026, 5, 28, 23, 21, 54, DateTimeKind.Local);
        var entry = new LogTailEntry(local, "INF", "anything");

        entry.TimestampLocal.ShouldBe(local);
    }
}
