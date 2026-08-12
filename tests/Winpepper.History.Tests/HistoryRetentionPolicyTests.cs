using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryRetentionPolicyTests
{
    [Fact]
    public void FromSettings_Defaults_MapToCurrentRetention()
    {
        var policy = HistoryRetentionPolicy.FromSettings(new AppSettings());

        policy.MaxEntries.ShouldBe(100);
        policy.MaxAgeDays.ShouldBe(30);
        policy.MaxAge.ShouldBe(TimeSpan.FromDays(30));
    }

    [Fact]
    public void FromSettings_CustomValues_RoundTrip()
    {
        var policy = HistoryRetentionPolicy.FromSettings(new AppSettings
        {
            HistoryMaxEntries = 5,
            HistoryMaxAgeDays = 7,
        });

        policy.MaxEntries.ShouldBe(5);
        policy.MaxAgeDays.ShouldBe(7);
        policy.MaxAge.ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void FromSettings_NullAge_MeansKeepForever()
    {
        var policy = HistoryRetentionPolicy.FromSettings(new AppSettings
        {
            HistoryMaxAgeDays = null,
        });

        policy.MaxAgeDays.ShouldBeNull();
        policy.MaxAge.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50_000, 10_000)]
    public void FromSettings_ClampsEntryLimit(int configured, int expected)
    {
        HistoryRetentionPolicy.FromSettings(new AppSettings
        {
            HistoryMaxEntries = configured,
        }).MaxEntries.ShouldBe(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 36_500)]
    public void FromSettings_ClampsAgeLimit(int configured, int expected)
    {
        var policy = HistoryRetentionPolicy.FromSettings(new AppSettings
        {
            HistoryMaxAgeDays = configured,
        });

        policy.MaxAgeDays.ShouldBe(expected);
        policy.MaxAge.ShouldBe(TimeSpan.FromDays(expected));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(int.MaxValue, 36_500)]
    public void MaxAge_ClampsDirectlyConstructedPolicy(int configured, int expected)
    {
        var policy = new HistoryRetentionPolicy { MaxAgeDays = configured };

        Should.NotThrow(() => _ = policy.MaxAge);
        policy.MaxAge.ShouldBe(TimeSpan.FromDays(expected));
    }
}
