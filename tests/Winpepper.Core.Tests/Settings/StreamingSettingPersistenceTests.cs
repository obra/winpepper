using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

/// <summary>
/// End-to-end pin of the streaming-toggle persistence chain exactly as
/// ModelsPage wires it: ToggleSwitch.Toggled -> fire-and-forget
/// `_ = writer.QueueAndFlushAsync(s => s with { StreamingEnabled = isOn })`
/// (direct-write, no ViewModel) -> DebouncedSettingsWriter -> SettingsStore
/// on disk. Same chain-pinning rationale as CleanupSettingsPersistenceTests:
/// a fake persist callback here would pass while the real wiring stayed dead.
/// </summary>
public class StreamingSettingPersistenceTests : IDisposable
{
    private readonly string _path;
    public StreamingSettingPersistenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public async Task Toggle_Persists_Through_DebouncedWriter_To_Store()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromMilliseconds(50));

        // The EXACT ModelsPage Toggled-handler shape: fire-and-forget
        // QueueAndFlushAsync with a `with`-mutation of StreamingEnabled.
        var isOn = false; // user flips the switch off
        _ = writer.QueueAndFlushAsync(s => s with { StreamingEnabled = isOn });

        // The handler is fire-and-forget, so ITS QueueAndFlushAsync can claim
        // the dirty flag and still be mid-write when our own FlushAsync returns
        // having found nothing to do. Flush, then poll for the write to land
        // (same bounded-poll precedent as DebouncedSettingsWriterTests).
        await writer.FlushAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        AppSettings loaded = new();
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_path))
            {
                loaded = new SettingsStore(_path).Load();
                if (!loaded.StreamingEnabled) break;
            }
            await Task.Delay(20);
        }
        loaded.StreamingEnabled.ShouldBeFalse();
        // Neighboring settings ride along untouched at their defaults.
        loaded.AsrProvider.ShouldBe("local");
        loaded.AssemblyAiKeytermsEnabled.ShouldBeFalse();
    }
}
