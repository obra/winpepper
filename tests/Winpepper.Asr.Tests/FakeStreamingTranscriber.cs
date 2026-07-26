using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Configurable IStreamingTranscriber double with a scripted session.</summary>
public sealed class FakeStreamingTranscriber : IStreamingTranscriber
{
    public FakeStreamingTranscriber(string modelName) => ModelName = modelName;

    public string ModelName { get; }
    public Exception? ThrowOnStart { get; set; }
    public Exception? ThrowOnPush { get; set; }
    public Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>>? OnFinish { get; set; }
    public FakeSession? LastSession { get; private set; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
    {
        if (ThrowOnStart is not null) throw ThrowOnStart;
        LastSession = new FakeSession(this);
        return Task.FromResult<IStreamingTranscriptionSession>(LastSession);
    }

    public sealed class FakeSession : IStreamingTranscriptionSession
    {
        private readonly FakeStreamingTranscriber _owner;
        public int Pushes { get; private set; }
        public bool Disposed { get; private set; }
        internal FakeSession(FakeStreamingTranscriber owner) => _owner = owner;

        public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            if (_owner.ThrowOnPush is not null) return ValueTask.FromException(_owner.ThrowOnPush);
            Pushes++;
            return ValueTask.CompletedTask;
        }

        public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
            => _owner.OnFinish is not null
                ? _owner.OnFinish(fullAudio, ct)
                : Task.FromResult(new TranscriptionResult("CLOUD", _owner.ModelName));

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
