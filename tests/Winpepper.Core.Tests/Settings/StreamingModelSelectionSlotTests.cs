using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public sealed class StreamingModelSelectionSlotTests
{
    [Fact]
    public void Read_BeforeAnyPublish_ReturnsNull()
        => new StreamingModelSelectionSlot().Read().ShouldBeNull();

    [Fact]
    public void Publish_ThenRead_RoundTrips_LastWriteWins()
    {
        var slot = new StreamingModelSelectionSlot();
        slot.Publish("nemotron-streaming-en");
        slot.Publish("nemotron-streaming-multi");
        slot.Read().ShouldBe("nemotron-streaming-multi");
    }
}
