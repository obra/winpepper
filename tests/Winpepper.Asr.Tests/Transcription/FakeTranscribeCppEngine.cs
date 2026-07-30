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
    public TimeSpan FeedDelay; // simulates a slow native transcribe_stream_feed

    /// <summary>When set, Feed blocks until the gate is released — a deterministic
    /// wedge for watchdog tests (condition-synchronized, no timing margins). The
    /// wait is bounded so a failing test cannot hang the runner.</summary>
    public ManualResetEventSlim? FeedGate;

    /// <summary>When set, BeginStream blocks until the gate is released — a
    /// deterministic stand-in for a pure compute-gate wait (A16 tests). The
    /// wait is bounded so a failing test cannot hang the runner.</summary>
    public ManualResetEventSlim? BeginStreamGate { get; set; }

    /// <summary>Simulates a slow BeginStream (e.g. blocked on the compute gate).</summary>
    public TimeSpan BeginStreamDelay { get; set; } = TimeSpan.Zero;
    /// <summary>What BeginStream reports as its per-call compute-gate wait (B4).</summary>
    public int GateWaitMsToReport { get; set; }

    public ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs)
    {
        BeginStreamGate?.Wait(TimeSpan.FromSeconds(30));
        if (BeginStreamDelay > TimeSpan.Zero) Thread.Sleep(BeginStreamDelay);
        gateWaitMs = GateWaitMsToReport;
        BeginStreamCalls++;
        BeginStreamLanguages.Add(language);
        if (ThrowOnBeginStream) throw new TranscribeCppException("fake begin failure");
        LastStream = new FakeStream(this) { AttContextRight = attContextRight };
        return LastStream;
    }

    public string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs)
    {
        gateWaitMs = 0;
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
            _e.FeedGate?.Wait(TimeSpan.FromSeconds(30));
            if (_e.FeedDelay > TimeSpan.Zero) Thread.Sleep(_e.FeedDelay);
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
