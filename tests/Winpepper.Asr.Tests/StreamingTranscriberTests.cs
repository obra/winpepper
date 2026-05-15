using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingTranscriberTests
{
    [Fact]
    public void FeedChunks_AccumulatesAllSamples()
    {
        var t = new StreamingTranscriber(_ => new ParakeetTranscript("ignored", [], [], []));
        t.FeedChunk(new float[1000]);
        t.FeedChunk(new float[2000]);
        t.TotalSamples.ShouldBe(3000);
    }

    [Fact]
    public void Flush_RunsTranscribeOnAccumulatedSamples()
    {
        var sawSamples = 0;
        var t = new StreamingTranscriber(s =>
        {
            sawSamples = s.Length;
            return new ParakeetTranscript("hello world", [], [], []);
        });
        t.FeedChunk(new float[16000]);
        var result = t.Flush();
        sawSamples.ShouldBe(16000);
        result.Text.ShouldBe("hello world");
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var t = new StreamingTranscriber(_ => new ParakeetTranscript("", [], [], []));
        t.FeedChunk(new float[5000]);
        t.Reset();
        t.TotalSamples.ShouldBe(0);
    }
}
