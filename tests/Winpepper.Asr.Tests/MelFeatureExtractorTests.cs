using System.Text.Json;
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class MelFeatureExtractorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static float[] ReadWavMonoF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int i = 0;
        while (i < bytes.Length - 4 && !(bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a'))
            i++;
        if (i >= bytes.Length - 4) throw new InvalidDataException("no data chunk");
        var size = BitConverter.ToInt32(bytes, i + 4);
        var dataStart = i + 8;
        var sampleCount = size / 2;
        var samples = new float[sampleCount];
        for (var s = 0; s < sampleCount; s++)
        {
            short v = BitConverter.ToInt16(bytes, dataStart + s * 2);
            samples[s] = v / 32768f;
        }
        return samples;
    }

    [Fact]
    public void Extract_MatchesPythonReference_FirstSixFrames()
    {
        var wav = ReadWavMonoF32(FixturePath("tone-440hz-1s.wav"));
        var reference = JsonSerializer.Deserialize<MelReference>(
            File.ReadAllText(FixturePath("tone-440hz-1s.mel.json")))!;

        var features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3).Extract(wav);

        features.GetLength(0).ShouldBe(reference.Shape[0]);
        features.GetLength(1).ShouldBe(reference.Shape[1]);

        for (var t = 0; t < 6; t++)
            for (var m = 0; m < reference.Shape[1]; m++)
                features[t, m].ShouldBe(reference.FirstSixFrames[t][m], tolerance: 1e-3);
    }

    [Fact]
    public void Extract_MatchesPythonReference_LastFrame()
    {
        var wav = ReadWavMonoF32(FixturePath("tone-440hz-1s.wav"));
        var reference = JsonSerializer.Deserialize<MelReference>(
            File.ReadAllText(FixturePath("tone-440hz-1s.mel.json")))!;

        var features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3).Extract(wav);

        var t = features.GetLength(0) - 1;
        for (var m = 0; m < reference.Shape[1]; m++)
            features[t, m].ShouldBe(reference.LastFrame[m], tolerance: 1e-3);
    }

    private sealed record MelReference(
        [property: System.Text.Json.Serialization.JsonPropertyName("shape")] int[] Shape,
        [property: System.Text.Json.Serialization.JsonPropertyName("first_six_frames")] float[][] FirstSixFrames,
        [property: System.Text.Json.Serialization.JsonPropertyName("last_frame")] float[] LastFrame);
}
