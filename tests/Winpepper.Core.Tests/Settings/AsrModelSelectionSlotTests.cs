using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class AsrModelSelectionSlotTests
{
    [Fact]
    public void Read_BeforeAnyPublish_ReturnsNull()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Read().ShouldBeNull();
    }

    [Fact]
    public void Read_AfterPublish_ReturnsPublishedName()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Publish("parakeet-tdt-0.6b-v2");

        slot.Read().ShouldBe("parakeet-tdt-0.6b-v2");
    }

    [Fact]
    public void Publish_LatestWriteWins()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Publish("model-a");
        slot.Publish("model-b");

        slot.Read().ShouldBe("model-b");
    }

    [Fact]
    public void Publish_FromAnotherThread_IsVisibleToReader()
    {
        var slot = new AsrModelSelectionSlot();

        var publisher = new Thread(() => slot.Publish("model-a"));
        publisher.Start();
        publisher.Join();

        slot.Read().ShouldBe("model-a");
    }
}
