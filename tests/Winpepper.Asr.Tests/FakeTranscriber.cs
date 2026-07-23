using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>A configurable ITranscriber test double.</summary>
public sealed class FakeTranscriber : ITranscriber
{
    private readonly Func<Task<TranscriptionResult>> _behavior;
    public int Calls { get; private set; }

    public FakeTranscriber(string modelName, Func<Task<TranscriptionResult>> behavior)
    {
        ModelName = modelName;
        _behavior = behavior;
    }

    public static FakeTranscriber Returning(string modelName, string text)
        => new(modelName, () => Task.FromResult(new TranscriptionResult(text, modelName)));

    public static FakeTranscriber Throwing(string modelName, Exception ex)
        => new(modelName, () => throw ex);

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        Calls++;
        return _behavior();
    }
}
