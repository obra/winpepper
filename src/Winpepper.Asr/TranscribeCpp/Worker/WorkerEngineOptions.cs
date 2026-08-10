namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>Per-op RPC deadlines. A native call that exceeds its deadline is
/// treated as wedged: the worker is killed and the call throws
/// TranscribeCppException (the existing batch-fallback trigger). Feed's 10 s
/// mirrors the drain budget; BeginStream covers the engine's 5 s gate wait
/// plus native begin; Load covers the ~0.9 s model load with cold-IO headroom.
/// BatchTimeout is the FLOOR of the per-call batch deadline: the engine
/// raises it to max(BatchTimeout, 30 s + 2 s per audio-second) so cap-sized
/// dictations are not killed mid-compute (a cap-sized batch measured ~106 s
/// on the dev host vs a fixed 120 s — only 1.13x headroom).</summary>
public sealed record WorkerEngineOptions
{
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BeginStreamTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FinalizeTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(120); // floor; see summary
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
