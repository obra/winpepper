using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class HistoryRetentionSettingsPersistenceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public async Task RetentionSettings_PersistThroughDebouncedWriter_ToStore()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));

        await writer.QueueAndFlushAsync(s => s with
        {
            HistoryStoreAudioEnabled = false,
            HistoryMaxEntries = 7,
            HistoryMaxAgeDays = null,
        });

        var loaded = new SettingsStore(_path).Load();
        loaded.HistoryStoreAudioEnabled.ShouldBeFalse();
        loaded.HistoryMaxEntries.ShouldBe(7);
        loaded.HistoryMaxAgeDays.ShouldBeNull();
        loaded.StreamingEnabled.ShouldBeTrue();
        loaded.AsrProvider.ShouldBe("local");
    }

    [Fact]
    public void Load_PreRetentionSettingsFile_UsesRetentionDefaults()
    {
        File.WriteAllText(_path, """
            {
              "schema": 1,
              "micDeviceId": "legacy-mic",
              "prewarmMicEnabled": false
            }
            """);

        var loaded = new SettingsStore(_path).Load();

        loaded.MicDeviceId.ShouldBe("legacy-mic");
        loaded.PrewarmMicEnabled.ShouldBeFalse();
        loaded.HistoryStoreAudioEnabled.ShouldBeTrue();
        loaded.HistoryMaxEntries.ShouldBe(100);
        loaded.HistoryMaxAgeDays.ShouldBe(30);
    }
}
