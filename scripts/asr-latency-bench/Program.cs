// ASR post-stop latency benchmark. Measures wall time from "recording stopped"
// to "final transcript available" — the user-perceived transcription time
// (production's HistoryTimings.TranscribeMs window).
//
// sim-* scenarios exercise the REAL production pipeline classes with the
// compute/network edge replaced by a documented delay model (the local ONNX
// model cannot run on Linux). real-remote-* scenarios hit the real AssemblyAI
// API and run only when ASSEMBLYAI_API_KEY is set.
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr;
using Winpepper.Asr.Transcription;

const int AudioSeconds = 10;
const double LocalRtf = 0.30;              // assumed local realtime factor (documented in results)
var uploadTime = TimeSpan.FromMilliseconds(400);   // ~320 KB WAV upload assumption
var processingTime = TimeSpan.FromSeconds(3.0);    // cloud batch processing for a 10 s clip

var requested = args.Length > 0 ? args : new[]
{
    "sim-local-batch", "sim-local-stream",
    "sim-remote-batch", "sim-remote-stream",
    "real-remote-batch", "real-remote-stream",
};
var rows = new List<(string Scenario, string Kind, long PostStopMs)>();

foreach (var scenario in requested)
{
    switch (scenario)
    {
        case "sim-local-batch":
        {
            var audio = SynthesizeAudio(AudioSeconds);
            var paced = new PacedTranscriber("parakeet-sim", TimeSpan.FromSeconds(AudioSeconds * LocalRtf));
            var sw = Stopwatch.StartNew();
            await paced.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "simulated", sw.ElapsedMilliseconds));
            break;
        }
        case "sim-remote-batch":
        {
            // REAL AssemblyAiTranscriber (production upload/create/poll loop),
            // paced fake client for the network edge.
            var audio = SynthesizeAudio(AudioSeconds);
            var transcriber = new AssemblyAiTranscriber(
                new PacedAssemblyAiClient(uploadTime, processingTime),
                new BenchKeyStore("sim-key"),
                new AssemblyAiOptions(),
                NullLogger<AssemblyAiTranscriber>.Instance);
            var sw = Stopwatch.StartNew();
            await transcriber.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "simulated", sw.ElapsedMilliseconds));
            break;
        }
        case "real-remote-batch":
        {
            var key = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine($"{scenario}: SKIPPED (ASSEMBLYAI_API_KEY not set)");
                break;
            }
            var audio = SynthesizeAudio(AudioSeconds);
            var opts = new AssemblyAiOptions { CloudDeadline = TimeSpan.FromSeconds(30) };
            var client = new AssemblyAiClient(
                new HttpClient(), () => key, opts, NullLogger<AssemblyAiClient>.Instance);
            var transcriber = new AssemblyAiTranscriber(
                client, new BenchKeyStore(key), opts, NullLogger<AssemblyAiTranscriber>.Instance);
            var sw = Stopwatch.StartNew();
            var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "REAL network", sw.ElapsedMilliseconds));
            Console.WriteLine($"  (transcript: \"{result.Text}\")");
            break;
        }
        case "sim-local-stream":
        {
            // REAL production pipeline (StreamingDictationSession +
            // ParakeetStreamingTranscriber + chunked mel/decode) with the ONNX
            // encoder edge replaced by the same RTF delay model as sim-local-batch.
            var audio = SynthesizeAudio(AudioSeconds);
            var backend = new PacedParakeetBackend(LocalRtf);
            var batch = new PacedTranscriber("parakeet-sim", TimeSpan.FromSeconds(AudioSeconds * LocalRtf));
            var streaming = new ParakeetStreamingTranscriber(
                backend, batch, "parakeet-sim", PreprocessorConfig.ParakeetTdtV3);
            rows.Add((scenario, "simulated", await MeasureStreaming(streaming, audio)));
            break;
        }
        case "sim-remote-stream":
        {
            // REAL AssemblyAiStreamingTranscriber/session over a paced fake socket
            // (final turn ~300 ms after Terminate — measured Universal-Streaming
            // immediate-finalization order of magnitude).
            var audio = SynthesizeAudio(AudioSeconds);
            var streaming = new AssemblyAiStreamingTranscriber(
                () => new PacedFakeSocket(finalizeDelay: TimeSpan.FromMilliseconds(300)),
                // Zero-pushed REST batch fallback (Task 7 / A9) — never used here:
                // MeasureStreaming pushes frames at realtime, so _pushedSamples > 0.
                new PacedTranscriber("assemblyai-batch-sim", TimeSpan.Zero),
                new BenchKeyStore("sim-key"), new AssemblyAiOptions(),
                NullLogger<AssemblyAiStreamingTranscriber>.Instance);
            rows.Add((scenario, "simulated", await MeasureStreaming(streaming, audio)));
            break;
        }
        case "real-remote-stream":
        {
            var key = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
            {
                Console.WriteLine($"{scenario}: SKIPPED (ASSEMBLYAI_API_KEY not set)");
                break;
            }
            var audio = SynthesizeAudio(AudioSeconds);
            var streaming = new AssemblyAiStreamingTranscriber(
                () => new ClientStreamingWebSocket(),
                // Zero-pushed REST batch fallback — never used (realtime pacing).
                new PacedTranscriber("assemblyai-batch-sim", TimeSpan.Zero),
                new BenchKeyStore(key), new AssemblyAiOptions(),
                NullLogger<AssemblyAiStreamingTranscriber>.Instance);
            rows.Add((scenario, "REAL network", await MeasureStreaming(streaming, audio)));
            break;
        }
        default:
            Console.WriteLine($"{scenario}: unknown scenario");
            break;
    }
}

Console.WriteLine();
Console.WriteLine("| scenario | kind | audio | post-stop latency (ms) |");
Console.WriteLine("|---|---|---|---|");
foreach (var (s, kind, ms) in rows)
    Console.WriteLine($"| {s} | {kind} | {AudioSeconds} s | {ms} |");

// --- helper functions and classes ---

static float[] SynthesizeAudio(int seconds)
{
    // Tone sweep + noise: enough energy that real remote runs return timing
    // representative of speech-length audio (transcript text is irrelevant).
    var n = seconds * 16000;
    var rng = new Random(42);
    var audio = new float[n];
    for (var i = 0; i < n; i++)
    {
        var t = i / 16000.0;
        var freq = 200 + 100 * Math.Sin(2 * Math.PI * 0.5 * t);
        audio[i] = (float)(0.25 * Math.Sin(2 * Math.PI * freq * t)
                           + 0.05 * (rng.NextDouble() * 2 - 1));
    }
    return audio;
}

// Simulates a live dictation: frames pushed in real time (50 ms cadence) through
// the REAL coordinator, then measures stop -> final transcript.
static async Task<long> MeasureStreaming(IStreamingTranscriber transcriber, float[] audio)
{
    // Explicit drain deadline (same value as the coordinator's default) so the
    // bound on the REAL network scenario is visible in bench output instead of
    // silently in play — a wedged drain caps the measured post-stop wait here.
    var drainDeadline = TimeSpan.FromSeconds(10);
    Console.WriteLine($"  (drain deadline: {drainDeadline.TotalSeconds:0} s)");
    await using var session = StreamingDictationSession.Start(
        _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
        NullLogger.Instance, CancellationToken.None, drainDeadline);
    const int frame = 800; // 50 ms
    for (var i = 0; i < audio.Length; i += frame)
    {
        session.OnFrame(audio.AsMemory(i, Math.Min(frame, audio.Length - i)));
        await Task.Delay(50);
    }
    var sw = Stopwatch.StartNew();
    var result = await session.FinishAsync(audio, CancellationToken.None);
    var ms = sw.ElapsedMilliseconds;
    if (result is null)
        throw new InvalidOperationException(
            $"no transcript (no transcriber materialized, or the {drainDeadline.TotalSeconds:0} s drain deadline expired)");
    return ms;
}

sealed class PacedTranscriber : ITranscriber
{
    private readonly TimeSpan _cost;
    public PacedTranscriber(string modelName, TimeSpan cost) { ModelName = modelName; _cost = cost; }
    public string ModelName { get; }
    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        await Task.Delay(_cost, ct);
        return new TranscriptionResult("simulated transcript", ModelName);
    }
}

sealed class PacedAssemblyAiClient : IAssemblyAiClient
{
    private readonly TimeSpan _uploadTime;
    private readonly TimeSpan _processingTime;
    private DateTime _createdAt;
    public PacedAssemblyAiClient(TimeSpan uploadTime, TimeSpan processingTime)
    { _uploadTime = uploadTime; _processingTime = processingTime; }
    public async Task<string> UploadAsync(byte[] audio, CancellationToken ct)
    { await Task.Delay(_uploadTime, ct); return "https://sim/upload"; }
    public Task<string> CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct)
    { _createdAt = DateTime.UtcNow; return Task.FromResult("sim-id"); }
    public Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
        => Task.FromResult(DateTime.UtcNow - _createdAt >= _processingTime
            ? new AssemblyAiTranscript("completed", "simulated transcript", 0.9, null, null)
            : new AssemblyAiTranscript("processing", null, null, null, null));
    public Task<bool> ValidateKeyAsync(CancellationToken ct) => Task.FromResult(true);
    public Task DeleteTranscriptAsync(string id, CancellationToken ct) => Task.CompletedTask;
}

sealed class BenchKeyStore : IAssemblyAiKeyStore
{
    private readonly string _key;
    public BenchKeyStore(string key) => _key = key;
    public bool HasKey => true;
    public void Save(string apiKey) { }
    public string? Load() => _key;
    public void Clear() { }
}

/// <summary>IParakeetBackend whose Encode costs rtf x chunk-audio-seconds (the
/// same realtime-factor assumption as sim-local-batch); decode steps are free.</summary>
sealed class PacedParakeetBackend : IParakeetBackend
{
    private readonly double _rtf;
    public PacedParakeetBackend(double rtf) => _rtf = rtf;
    public int VocabSize => 8;
    public int BlankId => 7;
    public int DecoderHiddenLayers => 2;
    public int DecoderHiddenDim => 4;

    public EncoderOutput Encode(float[,] melFrames)
    {
        var tIn = melFrames.GetLength(0);
        Thread.Sleep(TimeSpan.FromSeconds(_rtf * tIn / 100.0)); // 100 mel frames per audio second
        // MUST be the exact output-length function floor((T-1)/8)+1 that
        // ParakeetStreamingSession.EncodeAndDecode asserts on every encode
        // (a proportional tIn/8 diverges on the second chunk: T=300 -> 37 vs 38,
        // silently corrupting the session and falling back to the 3 s batch fake).
        var tOut = (tIn - 1) / 8 + 1;
        return new EncoderOutput(new float[2 * tOut], tOut, 2, tOut);
    }

    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        var logits = new float[8 + 5];
        logits[BlankId] = 10f;
        logits[8 + 1] = 10f;
        return new DecoderJointResult(logits, stateH, stateC);
    }

    public string DecodeTokens(IEnumerable<int> tokenIds) => "simulated transcript";
}

/// <summary>Paced fake AssemblyAI streaming socket: replies with a final Turn +
/// Termination <c>finalizeDelay</c> after the Terminate message arrives.</summary>
sealed class PacedFakeSocket : IStreamingWebSocket
{
    private readonly TimeSpan _finalizeDelay;
    private readonly System.Threading.Channels.Channel<string?> _incoming =
        System.Threading.Channels.Channel.CreateUnbounded<string?>();
    public PacedFakeSocket(TimeSpan finalizeDelay) => _finalizeDelay = finalizeDelay;
    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct) => Task.CompletedTask;
    public Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct) => Task.CompletedTask;
    public async Task SendTextAsync(string json, CancellationToken ct)
    {
        if (json.Contains("Terminate"))
        {
            await Task.Delay(_finalizeDelay, ct);
            _incoming.Writer.TryWrite("{\"type\":\"Turn\",\"turn_order\":0,\"end_of_turn\":true,\"transcript\":\"simulated transcript\"}");
            _incoming.Writer.TryWrite("{\"type\":\"Termination\"}");
        }
    }
    public async Task<string?> ReceiveTextAsync(CancellationToken ct) => await _incoming.Reader.ReadAsync(ct);
    public ValueTask DisposeAsync() { _incoming.Writer.TryWrite(null); return ValueTask.CompletedTask; }
}
