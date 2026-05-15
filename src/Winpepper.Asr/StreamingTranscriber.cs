namespace Winpepper.Asr;

/// <summary>
/// Buffers audio samples during recording. On <see cref="Flush"/>, calls the
/// supplied transcribe function once with all collected samples.
///
/// Plan 2 will replace this with true window-by-window streaming inside
/// <see cref="ParakeetSession"/>; today the encoder is run in one shot for
/// correctness reasons.
/// </summary>
public sealed class StreamingTranscriber
{
    private readonly Func<float[], ParakeetTranscript> _transcribe;
    private readonly List<float> _buffer = new();

    public StreamingTranscriber(Func<float[], ParakeetTranscript> transcribe)
    {
        _transcribe = transcribe;
    }

    public int TotalSamples => _buffer.Count;

    public void FeedChunk(ReadOnlySpan<float> samples)
    {
        for (var i = 0; i < samples.Length; i++) _buffer.Add(samples[i]);
    }

    public ParakeetTranscript Flush()
    {
        var arr = _buffer.ToArray();
        return _transcribe(arr);
    }

    public void Reset() => _buffer.Clear();
}
