using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

/// <summary>
/// End-to-end pin of the Cleanup-tab persistence chain exactly as AppShell
/// wires it: ToggleSwitch -> CleanupSettingsViewModel -> persist callback
/// (`c => _ = writer.QueueAndFlushAsync(c.ApplyTo)`) -> DebouncedSettingsWriter
/// -> SettingsStore on disk. This is the chain whose missing link made the
/// toggle cosmetic in the first place — a fake persist callback here would
/// pass while the real wiring stayed dead.
/// </summary>
public class CleanupSettingsPersistenceTests : IDisposable
{
    private readonly string _path;
    public CleanupSettingsPersistenceTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public async Task Toggle_Persists_Through_Writer_To_Store()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromMilliseconds(50));

        // The EXACT AppShell lambda shape (AppShell.Create): fire-and-forget
        // QueueAndFlushAsync(c.ApplyTo).
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.FromSettings(store.Load()),
            c => _ = writer.QueueAndFlushAsync(c.ApplyTo));

        vm.Enabled = false;

        // The callback is fire-and-forget, so ITS QueueAndFlushAsync can claim
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
                if (!loaded.CleanupEnabled) break;
            }
            await Task.Delay(20);
        }
        loaded.CleanupEnabled.ShouldBeFalse();
        // The other five cleanup fields ride along untouched at their defaults.
        loaded.CleanupWindowContextEnabled.ShouldBeFalse();
        loaded.CleanupProfile.ShouldBe("Ordinary");
        loaded.CleanupCustomPrompt.ShouldBe("");
        loaded.CleanupMaxNewTokens.ShouldBe(512);
        loaded.CleanupTimeoutMs.ShouldBe(15000);
    }
}
