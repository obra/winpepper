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
using Winpepper.Asr.Transcription;

const int AudioSeconds = 10;
const double LocalRtf = 0.30;              // assumed local realtime factor (documented in results)
var uploadTime = TimeSpan.FromMilliseconds(400);   // ~320 KB WAV upload assumption
var processingTime = TimeSpan.FromSeconds(3.0);    // cloud batch processing for a 10 s clip

var requested = args.Length > 0 ? args : new[] { "sim-local-batch", "sim-remote-batch", "real-remote-batch" };
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

// --- helpers -------------------------------------------------------------

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
