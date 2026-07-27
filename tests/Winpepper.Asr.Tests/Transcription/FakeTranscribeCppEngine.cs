using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Tests.Transcription;

/// <summary>Scripted fake for the transcribe.cpp engine (the FakeParakeetBackend
/// pattern: streaming logic stays Linux-testable).</summary>
public sealed class FakeTranscribeCppEngine : ITranscribeCppEngine
{
    public string ModelName => "fake-nemotron";
    public int BeginStreamCalls;
    public bool ThrowOnBeginStream;
    public FakeStream? LastStream;
    public readonly List<string?> BeginStreamLanguages = new();
    public string? LastBatchLanguage;

    public string FinalText = "hello world final";
    public bool FinalWasTruncated;
    public bool ThrowOnFeed;
    public bool ThrowOnFinalize;

    public ITranscribeCppStream BeginStream(int attContextRight, string? language = null)
    {
        BeginStreamCalls++;
        BeginStreamLanguages.Add(language);
        if (ThrowOnBeginStream) throw new TranscribeCppException("fake begin failure");
        LastStream = new FakeStream(this) { AttContextRight = attContextRight };
        return LastStream;
    }

    public string TranscribeBatch(float[] mono16k, string? language = null)
    {
        LastBatchLanguage = language;
        return "fake-batch";
    }
    public void Dispose() => Disposed = true;
    public bool Disposed;

    public sealed class FakeStream : ITranscribeCppStream
    {
        private readonly FakeTranscribeCppEngine _e;
        public FakeStream(FakeTranscribeCppEngine e) => _e = e;
        public int AttContextRight;
        public readonly List<int> FeedCounts = new();
        public bool Finalized;
        public bool Disposed;

        public string? Feed(float[] samples, int count)
        {
            if (_e.ThrowOnFeed) throw new TranscribeCppException("fake feed failure");
            FeedCounts.Add(count);
            return $"committed after {FeedCounts.Count} feeds";
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            if (_e.ThrowOnFinalize) throw new TranscribeCppException("fake finalize failure");
            Finalized = true;
            return (_e.FinalText, _e.FinalWasTruncated);
        }

        public void Dispose() => Disposed = true;
    }
}
