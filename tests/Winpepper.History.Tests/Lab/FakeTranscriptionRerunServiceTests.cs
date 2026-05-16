using Shouldly;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public class FakeTranscriptionRerunServiceTests : IDisposable
{
    private readonly string _dir;
    public FakeTranscriptionRerunServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rerun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task RerunAsync_ReturnsCannedText()
    {
        var wav = Path.Combine(_dir, "t.wav");
        WavWriter.WriteMono16kInt16(wav, new float[16]);

        var svc = new FakeTranscriptionRerunService((path, m) => $"canned for {m}");
        var result = await svc.RerunAsync(wav, "parakeet-test", _dir, CancellationToken.None);

        result.Text.ShouldBe("canned for parakeet-test");
        result.ModelName.ShouldBe("parakeet-test");
    }

    [Fact]
    public async Task RerunAsync_MissingWav_Throws()
    {
        var svc = new FakeTranscriptionRerunService();
        await Should.ThrowAsync<FileNotFoundException>(() =>
            svc.RerunAsync(Path.Combine(_dir, "missing.wav"), "m", _dir, CancellationToken.None));
    }
}
