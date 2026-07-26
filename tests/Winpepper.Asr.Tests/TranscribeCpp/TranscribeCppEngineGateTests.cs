using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public class TranscribeCppEngineGateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wp-eng-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Load_fails_loud_when_contract_json_is_missing()
    {
        var ex = Assert.Throws<TranscribeCppException>(
            () => TranscribeCppEngine.Load(_dir, Path.Combine(_dir, "m.gguf")));
        Assert.Contains("contract.json", ex.Message);
    }

    [Fact]
    public void Load_fails_loud_on_contract_mismatch_before_touching_the_native_library()
    {
        File.WriteAllText(Path.Combine(_dir, "contract.json"),
            """{"version":"9.9.9","header_hash":"0000000000000000"}""");
        var ex = Assert.Throws<TranscribeCppException>(
            () => TranscribeCppEngine.Load(_dir, Path.Combine(_dir, "m.gguf")));
        Assert.Contains("9.9.9", ex.Message);   // message names the found version
        Assert.Contains("0.1.3", ex.Message);   // and the required one
    }

    [Fact]
    public void NemotronStreamingModel_IsInstalled_requires_all_three_files()
    {
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        var modelDir = Path.Combine(_dir, "nemotron-streaming-en");
        var runtime = Path.Combine(modelDir, "runtime", "transcribe-native-windows-x86_64-cpu-vulkan");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(modelDir, NemotronStreamingModel.GgufFileName), "x");
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        File.WriteAllText(Path.Combine(runtime, "transcribe.dll"), "x");
        Assert.False(NemotronStreamingModel.IsInstalled(_dir));
        File.WriteAllText(Path.Combine(runtime, "contract.json"), "{}");
        Assert.True(NemotronStreamingModel.IsInstalled(_dir));
    }
}
