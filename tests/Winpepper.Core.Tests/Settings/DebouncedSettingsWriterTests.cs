using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class DebouncedSettingsWriterTests : IDisposable
{
    private readonly string _path;
    public DebouncedSettingsWriterTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public async Task Queue_Coalesces_Bursts_Into_One_Write()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < 20; i++)
        {
            // Mutators now execute at FLUSH time (mutator-replay fix), and a
            // for-loop variable is a single shared variable — without this
            // per-iteration copy every deferred mutator would read i == 20.
            var id = $"dev{i}";
            writer.Queue(s => s with { MicDeviceId = id });
        }

        // Poll for the debounced write to land. 50 ms debounce + the file
        // write itself usually settles in ~100 ms, but a contended CI runner
        // can need longer. Cap at 5 s; legitimate failures still trip.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string? latest = null;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_path))
            {
                latest = new SettingsStore(_path).Load().MicDeviceId;
                if (latest == "dev19") break;
            }
            await Task.Delay(20);
        }
        latest.ShouldBe("dev19");
    }

    [Fact]
    public async Task FlushAsync_Forces_Immediate_Write()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "forced" });
        await writer.FlushAsync();
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("forced");
    }

    [Fact]
    public async Task QueueAndFlushAsync_Writes_Immediately_Without_Debounce()
    {
        var store = new SettingsStore(_path);
        // 30 s debounce: only an immediate flush can make this land in time.
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));

        await writer.QueueAndFlushAsync(s => s with { MicDeviceId = "flushed-now" });

        new SettingsStore(_path).Load().MicDeviceId.ShouldBe("flushed-now");
    }

    [Fact]
    public async Task Dispose_Flushes_Pending_Writes()
    {
        var store = new SettingsStore(_path);
        var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "disposed" });
        writer.Dispose();
        await Task.Delay(50);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("disposed");
    }

    [Fact]
    public async Task Flush_PreservesChangesWrittenOutsideTheWriter()
    {
        // The shape of the production outage that lost 327 dictations
        // (7/25-7/26):
        // 1. app boots (writer constructed over the settings file),
        // 2. settings.json changes OUT-OF-BAND -- in production this was a
        //    direct edit of the file flipping cleanupEnabled (all in-app
        //    write paths were excluded by the forensics; the in-app
        //    ModelsPage/HistoryDetailPage direct saves are latent same-class
        //    bugs, closed in Task 4, but were NOT this outage's writer) --
        //    modeled here as a direct SettingsStore.Save of two fields,
        // 3. an UNRELATED write (MainWindow resize) flushes the writer,
        //    whose stale construction-time snapshot reverts step 2. That
        //    revert is the CONFIRMED perpetuating mechanism (two observed
        //    revert signatures with no app restart between).
        // The out-of-band changes must survive step 3: replay over a fresh
        // load survives ANY out-of-band write.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        using var writer = new DebouncedSettingsWriter(store); // HEAD snapshots disk here

        store.Save(store.Load() with
        {
            CleanupModelName = "promoted-model",
            CleanupEnabled = false
        }); // out-of-band write (same class as a hand edit of settings.json, or the latent ModelsPage:46 / HistoryDetailPage:73 bypasses)

        await writer.QueueAndFlushAsync(s => s with { WindowWidth = 999 }); // MainWindow resize

        var final = store.Load();
        final.CleanupModelName.ShouldBe("promoted-model"); // FAILS at HEAD
        final.CleanupEnabled.ShouldBeFalse();              // FAILS at HEAD
        final.WindowWidth.ShouldBe(999);                   // passes at HEAD
    }
}
