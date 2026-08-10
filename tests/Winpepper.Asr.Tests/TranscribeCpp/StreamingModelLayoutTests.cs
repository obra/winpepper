using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public sealed class StreamingModelLayoutTests
{
    [Fact]
    public void English_MatchesTheLegacyNemotronStreamingModelConstants()
    {
        StreamingModelLayout.English.Name.ShouldBe("nemotron-streaming-en");
        StreamingModelLayout.English.GgufFileName.ShouldBe("nemotron-speech-streaming-en-0.6b-Q8_0.gguf");
        StreamingModelLayout.English.Language.ShouldBeNull();
        NemotronStreamingModel.Name.ShouldBe(StreamingModelLayout.English.Name);
        NemotronStreamingModel.ModelFileRelative.ShouldBe(StreamingModelLayout.English.ModelFileRelative);
        NemotronStreamingModel.RuntimeDirRelative.ShouldBe(StreamingModelLayout.English.RuntimeDirRelative);
    }

    [Fact]
    public void Multilingual_UsesNullAutodetectLanguage_AndItsOwnDir()
    {
        var m = StreamingModelLayout.Multilingual;
        m.Name.ShouldBe("nemotron-streaming-multi");
        m.Language.ShouldBeNull(); // TRUE null = autodetect; "auto" is rejected by the v0.1.3 gate
        m.ModelFileRelative.ShouldBe(Path.Combine("nemotron-streaming-multi", "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf"));
        m.RuntimeDirRelative.ShouldBe(Path.Combine("nemotron-streaming-multi", "runtime", StreamingModelLayout.TarballTopLevelDir));
    }

    [Theory]
    [InlineData(null, "nemotron-streaming-en")]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-en")]
    [InlineData("nemotron-streaming-multi", "nemotron-streaming-multi")]
    [InlineData("unknown-model", "nemotron-streaming-en")]
    public void For_ResolvesKnownNamesAndDefaultsToEnglish(string? name, string expected)
        => StreamingModelLayout.For(name).Name.ShouldBe(expected);

    [Fact]
    public void IsInstalled_RequiresGgufDllAndContract()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sml-{Guid.NewGuid():N}");
        try
        {
            var m = StreamingModelLayout.Multilingual;
            m.IsInstalled(root).ShouldBeFalse();
            Directory.CreateDirectory(Path.GetDirectoryName(m.GgufPath(root))!);
            Directory.CreateDirectory(m.RuntimeDir(root));
            File.WriteAllText(m.GgufPath(root), "x");
            m.IsInstalled(root).ShouldBeFalse();
            File.WriteAllText(Path.Combine(m.RuntimeDir(root), "transcribe.dll"), "x");
            File.WriteAllText(Path.Combine(m.RuntimeDir(root), "contract.json"), "{}");
            m.IsInstalled(root).ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
