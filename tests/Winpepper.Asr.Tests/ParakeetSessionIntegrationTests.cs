using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

[Trait("Platform", "Windows")]
public class ParakeetSessionIntegrationTests
{
    private static string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winpepper", "models", "parakeet-tdt-0.6b-v3");

    private static float[] LoadWavMonoF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int i = 0;
        while (i < bytes.Length - 4 && !(bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a'))
            i++;
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
    public void Transcribe_PureTone_ReturnsSomething()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(Directory.Exists(ModelDir),
            $"Parakeet model not present at {ModelDir}; run scripts/download-parakeet.ps1");

        var wav = LoadWavMonoF32(Path.Combine(AppContext.BaseDirectory, "fixtures", "tone-440hz-1s.wav"));
        using var session = new ParakeetSession(ModelDir);
        var result = session.Transcribe(wav);
        result.ShouldNotBeNull();
    }
}
