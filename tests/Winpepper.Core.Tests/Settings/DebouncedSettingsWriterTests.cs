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
        await Task.Delay(200);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("dev19");
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
