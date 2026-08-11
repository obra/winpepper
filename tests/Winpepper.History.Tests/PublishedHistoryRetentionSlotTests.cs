using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.History.Tests;

public sealed class PublishedHistoryRetentionSlotTests
{
    [Fact]
    public void GetSnapshot_ReturnsThePairFromTheLatestComponentPublishes()
    {
        var slot = PublishedHistoryRetentionSlot.FromSettings(new AppSettings
        {
            HistoryStoreAudioEnabled = true,
            HistoryMaxEntries = 10,
            HistoryMaxAgeDays = 20,
        });
        var policyB = new HistoryRetentionPolicy
        {
            MaxEntries = 30,
            MaxAgeDays = 40,
        };

        slot.PublishAudio(storeAudio: false);
        slot.PublishPolicy(policyB);

        var snapshot = slot.GetSnapshot();

        snapshot.ShouldBe((StoreAudio: false, Policy: policyB));
        // GetSnapshot makes the former two-read race unrepresentable at this API:
        // the pair comes from one locked read, so no flaky hammer test is needed.
    }
}
