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
            writer.Queue(s => s with { MicDeviceId = $"dev{i}" });

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
}
