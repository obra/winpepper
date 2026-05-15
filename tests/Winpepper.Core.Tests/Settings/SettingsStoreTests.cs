using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _path;
    public SettingsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s.Schema.ShouldBe(1);
        s.MicDeviceId.ShouldBe("");
        s.AsrModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        s.PlaySounds.ShouldBeTrue();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s = s with { MicDeviceId = "{abc-123}", PlaySounds = false };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("{abc-123}");
        loaded.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void Load_BadJson_FallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var s = new SettingsStore(_path).Load();
        s.Schema.ShouldBe(1);
    }

    [Fact]
    public void Save_Uses_AtomicWrite()
    {
        var store = new SettingsStore(_path);
        store.Save(store.Load());
        Path.GetDirectoryName(_path)!
            .Pipe(d => Directory.GetFiles(d, $"{Path.GetFileName(_path)}.tmp-*"))
            .Length.ShouldBe(0);
    }
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn input, Func<TIn, TOut> fn) => fn(input);
}
