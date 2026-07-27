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

    [Fact]
    public async Task Flush_Applies_Queued_Mutators_In_Order()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "a" });
        writer.Queue(s => s with { MicDeviceId = s.MicDeviceId + "b" });
        await writer.FlushAsync();
        new SettingsStore(_path).Load().MicDeviceId.ShouldBe("ab");
    }

    [Fact]
    public async Task Flush_QueuedMutator_Wins_Over_OutOfBand_Write_On_The_Same_Field()
    {
        // A queued mutator is newer intent than an out-of-band write to the
        // SAME field: replay-on-fresh-load applies it last, so it wins.
        // Accepted edge (verified during planning): a hand edit of
        // settings.json landing during the <=400 ms debounce window loses a
        // same-field conflict to the queued mutator — after Task 4 no in-app
        // runtime conflict pair exists at all, so this pins the writer's
        // deterministic replay contract, not a live product conflict.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        store.Save(store.Load() with { CleanupModelName = "out-of-band" });
        await writer.QueueAndFlushAsync(s => s with { CleanupModelName = "queued-wins" });
        new SettingsStore(_path).Load().CleanupModelName.ShouldBe("queued-wins");
    }

    [Fact]
    public void Dispose_Flush_Preserves_OutOfBand_Changes()
    {
        // App shutdown flushes via Dispose (AppShell.cs:580). At HEAD that
        // alone clobbered a direct save even with no intervening toggle.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { WindowWidth = 777 });
        store.Save(store.Load() with { CleanupEnabled = false }); // out-of-band, after queueing
        writer.Dispose(); // synchronous flush

        var final = new SettingsStore(_path).Load();
        final.WindowWidth.ShouldBe(777);
        final.CleanupEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Flush_SkipsAndKeepsMutations_WhenSettingsFileIsUnreadable()
    {
        // A DIRECTORY at the settings path is a deterministic, cross-platform
        // "file exists but cannot be read": File.ReadAllText on it throws
        // UnauthorizedAccessException on both Windows and Linux under .NET 9
        // (and TryLoadCurrent also returns false for a persistent
        // IOException, should a runtime surface it that way). Note
        // File.Exists is FALSE for a directory — which is exactly why
        // TryLoadCurrent reads without Load()'s File.Exists pre-check.
        // The flush must SKIP (write nothing) and KEEP the queued mutation.
        var store = new SettingsStore(_path);
        Directory.CreateDirectory(_path);
        try
        {
            using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
            writer.Queue(s => s with { MicDeviceId = "kept-mutation" });
            await writer.FlushAsync(); // degraded load -> flush skipped

            Directory.Exists(_path).ShouldBeTrue(); // still the directory
            File.Exists(_path).ShouldBeFalse();     // nothing was written

            Directory.Delete(_path);                // path is healthy again
            store.Save(new AppSettings { WindowWidth = 555 });

            await writer.FlushAsync();              // the KEPT mutation now applies

            var final = new SettingsStore(_path).Load();
            final.MicDeviceId.ShouldBe("kept-mutation");
            final.WindowWidth.ShouldBe(555); // applied over the fresh valid file
        }
        finally
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    [Fact]
    public async Task Flush_AppliesRemainingMutators_WhenOneThrows()
    {
        // One bad lambda must not destroy sibling changes: the thrower is
        // DROPPED, the rest of the batch still applies, and the writer
        // stays usable. (Task 3 adds the ERR log line for the drop.)
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "x-applied" });          // m1: field X
        writer.Queue(s => throw new InvalidOperationException("boom"));   // m2: dropped
        writer.Queue(s => s with { WindowWidth = 424 });                  // m3: field Y — must still apply
        await writer.FlushAsync();

        var final = new SettingsStore(_path).Load();
        final.MicDeviceId.ShouldBe("x-applied");
        final.WindowWidth.ShouldBe(424);

        // Writer still usable after a throwing mutator.
        await writer.QueueAndFlushAsync(s => s with { WindowHeight = 200 });
        new SettingsStore(_path).Load().WindowHeight.ShouldBe(200);
    }
}
