// ASR post-stop latency benchmark. Measures wall time from "recording stopped"
// to "final transcript available" — the user-perceived transcription time
// (production's HistoryTimings.TranscribeMs window).
//
// sim-* scenarios exercise the REAL production pipeline classes with the
// compute/network edge replaced by a documented delay model (the local ONNX
// model cannot run on Linux). real-remote-* scenarios hit the real AssemblyAI
// API and run only when ASSEMBLYAI_API_KEY is set.
using System.Diagnostics;
using AsrLatencyBench;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr;
using Winpepper.Asr.Transcription;

const int AudioSeconds = 10;
const double LocalRtf = 0.30;              // assumed local realtime factor (documented in results)
var uploadTime = TimeSpan.FromMilliseconds(400);   // ~320 KB WAV upload assumption
var processingTime = TimeSpan.FromSeconds(3.0);    // cloud batch processing for a 10 s clip

var wavPaths = new List<string>();
string? modelDir = null;
string? nemotronModel = null;
string? nemotronRuntime = null;
var gain = 1.0;
var leadSilenceMs = 0;
string? corpusDir = null;
var outDir = "artifacts/asr-eval-results"; // default INSIDE gitignored artifacts/: results.json contains transcript text, and a bare "asr-eval-results/" is NOT gitignored
var repeats = 1;
var scenarioArgs = new List<string>();
for (var argIdx = 0; argIdx < args.Length; argIdx++)
{
    switch (args[argIdx])
    {
        case "--wav": wavPaths.Add(args[++argIdx]); break;
        case "--model-dir": modelDir = args[++argIdx]; break;
        case "--nemotron-model": nemotronModel = args[++argIdx]; break;
        case "--nemotron-runtime": nemotronRuntime = args[++argIdx]; break;
        case "--gain": gain = double.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--lead-silence-ms": leadSilenceMs = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--corpus": corpusDir = args[++argIdx]; break;
        case "--out": outDir = args[++argIdx]; break;
        case "--repeats": repeats = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
        default: scenarioArgs.Add(args[argIdx]); break;
    }
}
var requested = scenarioArgs.Count > 0 ? scenarioArgs.ToArray() : new[]
{
    "sim-local-batch", "sim-local-stream",
    "sim-remote-batch", "sim-remote-stream",
    "real-remote-batch", "real-remote-stream",
};
var audio = wavPaths.Count > 0
    ? BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPaths[0]), gain, leadSilenceMs)
    : SynthesizeAudio(AudioSeconds);
var audioSeconds = audio.Length / 16000.0;
var rows = new List<(string Scenario, string Kind, double AudioSeconds, long PostStopMs)>();

foreach (var scenario in requested)
{
    switch (scenario)
    {
        case "sim-local-batch":
        {
            var paced = new PacedTranscriber("parakeet-sim", TimeSpan.FromSeconds(AudioSeconds * LocalRtf));
            var sw = Stopwatch.StartNew();
            await paced.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "simulated", audioSeconds, sw.ElapsedMilliseconds));
            break;
        }
        case "sim-remote-batch":
        {
            // REAL AssemblyAiTranscriber (production upload/create/poll loop),
            // paced fake client for the network edge.
            var transcriber = new AssemblyAiTranscriber(
                new PacedAssemblyAiClient(uploadTime, processingTime),
                new BenchKeyStore("sim-key"),
                new AssemblyAiOptions(),
                NullLogger<AssemblyAiTranscriber>.Instance);
            var sw = Stopwatch.StartNew();
            await transcriber.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "simulated", audioSeconds, sw.ElapsedMilliseconds));
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
            var opts = new AssemblyAiOptions { CloudDeadline = TimeSpan.FromSeconds(30) };
            var client = new AssemblyAiClient(
                new HttpClient(), () => key, opts, NullLogger<AssemblyAiClient>.Instance);
            var transcriber = new AssemblyAiTranscriber(
                client, new BenchKeyStore(key), opts, NullLogger<AssemblyAiTranscriber>.Instance);
            var sw = Stopwatch.StartNew();
            var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);
            rows.Add((scenario, "REAL network", audioSeconds, sw.ElapsedMilliseconds));
            Console.WriteLine($"  (transcript: \"{result.Text}\")");
            break;
        }
        case "sim-local-stream":
        {
            // REAL production pipeline (StreamingDictationSession +
            // ParakeetStreamingTranscriber + chunked mel/decode) with the ONNX
            // encoder edge replaced by the same RTF delay model as sim-local-batch.
            var backend = new PacedParakeetBackend(LocalRtf);
            var batch = new PacedTranscriber("parakeet-sim", TimeSpan.FromSeconds(AudioSeconds * LocalRtf));
            var streaming = new ParakeetStreamingTranscriber(
                backend, batch, "parakeet-sim", PreprocessorConfig.ParakeetTdtV3);
            rows.Add((scenario, "simulated", audioSeconds, await MeasureStreaming(streaming, audio)));
            break;
        }
        case "sim-remote-stream":
        {
            // REAL AssemblyAiStreamingTranscriber/session over a paced fake socket
            // (final turn ~300 ms after Terminate — measured Universal-Streaming
            // immediate-finalization order of magnitude).
            var streaming = new AssemblyAiStreamingTranscriber(
                () => new PacedFakeSocket(finalizeDelay: TimeSpan.FromMilliseconds(300)),
                // Zero-pushed REST batch fallback (Task 7 / A9) — never used here:
                // MeasureStreaming pushes frames at realtime, so _pushedSamples > 0.
                new PacedTranscriber("assemblyai-batch-sim", TimeSpan.Zero),
                new BenchKeyStore("sim-key"), new AssemblyAiOptions(),
                NullLogger<AssemblyAiStreamingTranscriber>.Instance);
            rows.Add((scenario, "simulated", audioSeconds, await MeasureStreaming(streaming, audio)));
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
            var streaming = new AssemblyAiStreamingTranscriber(
                () => new ClientStreamingWebSocket(),
                // Zero-pushed REST batch fallback — never used (realtime pacing).
                new PacedTranscriber("assemblyai-batch-sim", TimeSpan.Zero),
                new BenchKeyStore(key), new AssemblyAiOptions(),
                NullLogger<AssemblyAiStreamingTranscriber>.Instance);
            rows.Add((scenario, "REAL network", audioSeconds, await MeasureStreaming(streaming, audio)));
            break;
        }
        case "real-local":
        {
            if (modelDir is null || wavPaths.Count == 0)
            {
                Console.WriteLine("real-local: SKIPPED (requires --model-dir and at least one --wav)");
                break;
            }
            if (!ParakeetSession.ModelFilesPresent(modelDir))
            {
                Console.WriteLine($"real-local: SKIPPED (model files not found in {modelDir})");
                break;
            }
            using var session = new ParakeetSession(modelDir);
            Console.WriteLine($"# real-local: UsingDirectML={session.UsingDirectML}");
            var realBatch = new ParakeetTranscriber(session, "parakeet-tdt-0.6b-v3");
            foreach (var wavPath in wavPaths)
            {
                var name = Path.GetFileName(wavPath);
                var wavAudio = BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPath), gain, leadSilenceMs);
                var seconds = wavAudio.Length / 16000.0;
                var (rms, peak, maxFrameRms) = BenchAudio.Stats(wavAudio);
                Console.WriteLine(
                    $"# {name}: {seconds:F1}s gain={gain} leadSilenceMs={leadSilenceMs} " +
                    $"rms={rms:F4} peak={peak:F4} maxFrameRms={maxFrameRms:F4}");

                // Batch: whole buffer through ParakeetSession; post-stop latency is the
                // full transcription time (nothing was processed before "stop").
                var swBatch = Stopwatch.StartNew();
                var batchResult = await realBatch.TranscribeAsync(wavAudio, CancellationToken.None);
                swBatch.Stop();
                rows.Add(($"real-local-batch {name}", "REAL local", seconds, swBatch.ElapsedMilliseconds));
                Console.WriteLine($"# batch[{name}]: \"{batchResult.Text}\"");

                // Streaming: ParakeetStreamingSession fed 50 ms frames at real-time
                // pace; post-stop latency is FinishAsync only. The batchFallback flag
                // proves the run genuinely streamed (FinishAsync silently falls back
                // on any streaming failure, which would fake a plausible number).
                var fellBack = false;
                var sessionLog = new CollectingLogger();
                await using var streaming = new ParakeetStreamingSession(
                    session, "parakeet-tdt-0.6b-v3", PreprocessorConfig.ParakeetTdtV3,
                    (mem, ct) => { fellBack = true; return realBatch.TranscribeAsync(mem, ct); },
                    log: sessionLog);
                const int frame = 800; // 50 ms at 16 kHz
                for (var i = 0; i < wavAudio.Length; i += frame)
                {
                    await streaming.PushAsync(
                        wavAudio.AsMemory(i, Math.Min(frame, wavAudio.Length - i)), CancellationToken.None);
                    await Task.Delay(50);
                }
                var swStream = Stopwatch.StartNew();
                var streamResult = await streaming.FinishAsync(wavAudio, CancellationToken.None);
                swStream.Stop();
                rows.Add(($"real-local-stream {name}", "REAL local", seconds, swStream.ElapsedMilliseconds));
                Console.WriteLine($"# stream[{name}]: fellBackToBatch={fellBack} \"{streamResult.Text}\"");
                foreach (var logLine in sessionLog.Lines)
                    Console.WriteLine($"# log[{name}]: {logLine}");

                var diff = TranscriptDiff.Summarize(batchResult.Text, streamResult.Text);
                Console.WriteLine($"# diff[{name}]: {diff.Describe()}");
            }
            break;
        }
        case "real-nemotron-stream":
        {
            if (nemotronModel is null || nemotronRuntime is null || wavPaths.Count == 0)
            {
                Console.WriteLine("real-nemotron-stream: SKIPPED (requires --nemotron-model, --nemotron-runtime and at least one --wav)");
                break;
            }
            if (!File.Exists(nemotronModel) || !File.Exists(Path.Combine(nemotronRuntime, "transcribe.dll")))
            {
                Console.WriteLine($"real-nemotron-stream: SKIPPED (model or runtime not found)");
                break;
            }
            using var engine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-log: {msg}"));
            Console.WriteLine($"# real-nemotron-stream: engine loaded (CPU backend, att_context_right=13)");

            // Optional TDT batch reference for the quality characterization.
            ParakeetSession? tdtSession = null;
            ParakeetTranscriber? tdtBatch = null;
            if (modelDir is not null && ParakeetSession.ModelFilesPresent(modelDir))
            {
                tdtSession = new ParakeetSession(modelDir);
                tdtBatch = new ParakeetTranscriber(tdtSession, "parakeet-tdt-0.6b-v3");
            }
            try
            {
                foreach (var wavPath in wavPaths)
                {
                    var name = Path.GetFileName(wavPath);
                    var wavAudio = BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPath), gain, leadSilenceMs);
                    var seconds = wavAudio.Length / 16000.0;

                    // (a) nemotron BATCH — the parity reference (same engine, offline).
                    var swNb = Stopwatch.StartNew();
                    var nemBatchText = engine.TranscribeBatch(wavAudio);
                    swNb.Stop();
                    rows.Add(($"nem-batch {name}", "REAL nemotron", seconds, swNb.ElapsedMilliseconds));
                    Console.WriteLine($"# nem-batch[{name}]: \"{nemBatchText}\"");

                    // (b) nemotron STREAMED through the REAL production stack:
                    // NemotronStreamingTranscriber inside StreamingDictationSession,
                    // 50 ms frames at real-time pace (the manual-equivalent check).
                    var fellBack = false;
                    var fallbackProbe = new ProbeTranscriber(() => fellBack = true, tdtBatch);
                    var streaming = new NemotronStreamingTranscriber(
                        () => engine, fallbackProbe, "nemotron-streaming-en");
                    await using var session = StreamingDictationSession.Start(
                        _ => Task.FromResult<IStreamingTranscriber?>(streaming),
                        NullLogger.Instance, CancellationToken.None, TimeSpan.FromSeconds(10));
                    const int frame = 800; // 50 ms
                    for (var i = 0; i < wavAudio.Length; i += frame)
                    {
                        session.OnFrame(wavAudio.AsMemory(i, Math.Min(frame, wavAudio.Length - i)));
                        await Task.Delay(50);
                    }
                    var swStream = Stopwatch.StartNew();
                    var streamResult = await session.FinishAsync(wavAudio, CancellationToken.None);
                    swStream.Stop();
                    if (streamResult is null) throw new InvalidOperationException("no transcript from coordinator");
                    rows.Add(($"nem-stream {name}", "REAL nemotron", seconds, swStream.ElapsedMilliseconds));
                    Console.WriteLine($"# nem-stream[{name}]: fellBackToBatch={fellBack} \"{streamResult.Text}\"");

                    // (c) parity bar: streamed-nemotron == batch-nemotron.
                    var parity = TranscriptDiff.Summarize(nemBatchText, streamResult.Text);
                    Console.WriteLine($"# diff-parity[{name}]: {parity.Describe()}");

                    // (d) honest characterization vs the TDT ONNX batch engine.
                    if (tdtBatch is not null)
                    {
                        var tdtText = (await tdtBatch.TranscribeAsync(wavAudio, CancellationToken.None)).Text;
                        Console.WriteLine($"# tdt-batch[{name}]: \"{tdtText}\"");
                        var vsTdt = TranscriptDiff.Summarize(tdtText, streamResult.Text);
                        Console.WriteLine($"# diff-vs-tdt[{name}]: {vsTdt.Describe()}");
                    }
                }
            }
            finally
            {
                tdtSession?.Dispose();
            }
            break;
        }
        case "corpus":
        {
            if (corpusDir is null || nemotronModel is null || nemotronRuntime is null)
            {
                Console.WriteLine("corpus: SKIPPED (requires --corpus, --nemotron-model and --nemotron-runtime)");
                break;
            }
            var manifestPath = Path.Combine(corpusDir, "manifest.json");
            if (!File.Exists(manifestPath) || !File.Exists(nemotronModel)
                || !File.Exists(Path.Combine(nemotronRuntime, "transcribe.dll")))
            {
                Console.WriteLine("corpus: SKIPPED (manifest, model or runtime not found)");
                break;
            }
            var manifest = AsrEvalCorpus.CorpusManifest.Load(manifestPath);
            using var corpusEngine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-log: {msg}"));
            // SECOND engine instance (same model + runtime, its OWN compute gate),
            // used ONLY as the streaming sessions' batch fallback via
            // EngineBatchTranscriber. Do NOT "simplify" this back to one engine:
            // during FinishAsync the primary engine's native stream still HOLDS the
            // engine-wide SemaphoreSlim(1,1) compute gate (acquired
            // TranscribeCppEngine.cs:177, released only in NativeStream.Dispose at
            // :336, whose sole caller is Session.DisposeAsync() at
            // NemotronStreamingTranscriber.cs:174, which StreamingDictationSession
            // invokes only AFTER FinishAsync returns, StreamingDictationSession.cs:120-121).
            // A same-engine fallback awaited inside FinishAsync would stall 5 s at the
            // gate wait (TranscribeCppEngine.cs:235) and throw TranscribeCppException
            // on EVERY fallback clip. Cost: ~700 MB extra model RAM, bench-only, accepted.
            using var fallbackEngine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-fallback-log: {msg}"));
            Console.WriteLine($"# corpus: engines loaded (primary + fallback), {manifest.Entries.Count} manifest entries, repeats={repeats}");

            var clipResults = new List<ClipResult>();
            foreach (var entry in manifest.Entries.Where(e => !e.Exclude))
            {
                // One bad clip must not destroy the whole eval: any per-clip failure
                // (including a null coordinator result) becomes an error row, and
                // results.json/results.md are still written after the loop.
                try
                {
                    var wavAudio = BenchAudio.ReadMono16k(Path.Combine(corpusDir, entry.WavPath));
                    var refPath = AsrEvalCorpus.ReferencePlanner.ReferencePath(corpusDir, entry);
                    var hasReference = File.Exists(refPath);
                    var referenceText = hasReference ? File.ReadAllText(refPath).Trim() : "";

                    // (a) batch parity reference: the PRIMARY engine, offline over the
                    //     full clip. Gate-safe: it runs before any streaming session
                    //     exists on this engine, so its compute gate is free.
                    var batchText = corpusEngine.TranscribeBatch(wavAudio);

                    // (b) production passes silence-trimmed audio to FinishAsync (PipelineHost.cs:554);
                    //     streamed frames stay untrimmed -- same asymmetry as production.
                    var trimResult = Winpepper.Audio.SilenceTrimmer.Trim(wavAudio);

                    var streamText = "";
                    var fellBack = false;
                    var truncated = false;
                    var finishRuns = new List<long>();
                    for (var run = 0; run < repeats; run++)
                    {
                        var runFellBack = false;
                        // Fallback runs on the SECOND engine -- see the fallbackEngine comment above.
                        var probe = new ProbeTranscriber(() => runFellBack = true, new EngineBatchTranscriber(fallbackEngine));
                        var nemLog = new ListLogger();
                        var streaming = new NemotronStreamingTranscriber(
                            () => corpusEngine, probe, "nemotron-streaming-en", nemLog);
                        await using var session = StreamingDictationSession.Start(
                            _ => Task.FromResult<IStreamingTranscriber?>(streaming),
                            NullLogger.Instance, CancellationToken.None, TimeSpan.FromSeconds(10));

                        // Production sends one ~500 ms preroll burst at session start, then
                        // steady 50 ms frames (WarmWasapiRecorder.cs:144-147, PipelineHost.cs:455).
                        // Stopwatch-scheduled pacing: steady frame s is due at s*50 ms, so
                        // cumulative timing stays true to real time (no Task.Delay drift).
                        var segments = EvalFraming.Segments(wavAudio.Length);
                        var pacer = Stopwatch.StartNew();
                        for (var s = 0; s < segments.Count; s++)
                        {
                            if (s > 0)
                            {
                                var waitMs = s * 50L - pacer.ElapsedMilliseconds;
                                if (waitMs > 0) await Task.Delay((int)waitMs);
                            }
                            var (segOffset, segLength) = segments[s];
                            session.OnFrame(wavAudio.AsMemory(segOffset, segLength));
                        }

                        long finishMs;
                        string runText;
                        if (trimResult.IsSilent)
                        {
                            // Production drops silent dictations before transcription
                            // (PipelineHost TrimForTranscription returns null): no text, no latency sample.
                            runText = "";
                            finishMs = 0;
                        }
                        else
                        {
                            var swFinish = Stopwatch.StartNew();
                            var finishResult = await session.FinishAsync(trimResult.Trimmed, CancellationToken.None);
                            swFinish.Stop();
                            if (finishResult is null)
                                throw new InvalidOperationException(
                                    $"corpus[{entry.Id}]: no transcript from coordinator"); // caught below -> error row, run continues
                            runText = finishResult.Text;
                            finishMs = swFinish.ElapsedMilliseconds;
                        }
                        finishRuns.Add(finishMs);
                        if (run == 0)
                        {
                            // Accuracy and flags from the first run; later runs only add latency samples.
                            streamText = runText;
                            fellBack = runFellBack;
                            truncated = nemLog.Lines.Any(l => l.Contains("was_truncated", StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    double? wer = null;
                    double? cer = null;
                    bool? silentPass = null;
                    if (entry.ExpectedSilent)
                    {
                        silentPass = EvalMetrics.SilentPass(streamText);
                    }
                    else if (hasReference)
                    {
                        wer = EvalMetrics.Wer(referenceText, streamText).Rate;
                        cer = EvalMetrics.Cer(referenceText, streamText).Rate;
                    }
                    var parityDiff = TranscriptDiff.Summarize(batchText, streamText).Describe();
                    clipResults.Add(new ClipResult(
                        entry.Id, wavAudio.Length / 16000.0, entry.ExpectedSilent, hasReference,
                        referenceText, streamText, batchText, wer, cer, silentPass,
                        finishRuns, fellBack, truncated, trimResult.IsSilent, parityDiff));
                    Console.WriteLine($"# corpus[{entry.Id}]: fellBack={fellBack} truncated={truncated} " +
                        $"wer={(wer is null ? "n/a" : wer.Value.ToString("F3"))} finishMs={finishRuns[0]} parity: {parityDiff}");
                }
                catch (Exception ex)
                {
                    // Error row: empty texts/metrics; the exception text goes only to
                    // results.json (gitignored artifacts/) and the run log -- results.md
                    // shows an ERROR marker and counts, never the message.
                    clipResults.Add(new ClipResult(
                        entry.Id, 0.0, entry.ExpectedSilent, HasReference: false,
                        Reference: "", StreamText: "", BatchText: "", Wer: null, Cer: null, SilentPass: null,
                        FinishMsRuns: Array.Empty<long>(), FellBack: false, Truncated: false,
                        TrimmedSilent: false, BatchParityDiff: "",
                        Error: $"{ex.GetType().Name}: {ex.Message}"));
                    Console.Error.WriteLine($"# corpus[{entry.Id}]: FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }

            var runInfo = new EvalRunInfo(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(corpusDir)),
                Path.GetFileNameWithoutExtension(nemotronModel),
                Winpepper.Asr.TranscribeCpp.TranscribeCppContract.RequiredVersion,
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                repeats);
            var evalSummary = EvalResults.Summarize(clipResults);
            // ALWAYS write both files -- even when clips failed -- then report failures.
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "results.json"), EvalResults.ToJson(runInfo, clipResults, evalSummary));
            var resultsMd = EvalResults.ToMarkdown(runInfo, clipResults, evalSummary);
            File.WriteAllText(Path.Combine(outDir, "results.md"), resultsMd);
            Console.WriteLine();
            Console.WriteLine(resultsMd);
            if (evalSummary.FailedCount > 0)
            {
                // Results are already on disk; the non-zero exit only flags the failures.
                Console.Error.WriteLine($"# corpus: {evalSummary.FailedCount} clip(s) FAILED -- results written to {outDir}, exiting non-zero");
                Environment.ExitCode = 1;
            }
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
foreach (var r in rows)
    Console.WriteLine($"| {r.Scenario} | {r.Kind} | {r.AudioSeconds:F1} s | {r.PostStopMs} |");

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

sealed class ProbeTranscriber : ITranscriber
{
    private readonly Action _onCalled;
    private readonly ITranscriber? _inner;
    public ProbeTranscriber(Action onCalled, ITranscriber? inner) { _onCalled = onCalled; _inner = inner; }
    public string ModelName => _inner?.ModelName ?? "fallback-probe";
    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
    {
        _onCalled();
        return _inner is not null
            ? _inner.TranscribeAsync(audio, ct)
            : Task.FromResult(new TranscriptionResult("", ModelName));
    }
}

/// <summary>Batch fallback for the corpus eval: a SECOND nemotron engine instance
/// (same model + runtime, its OWN compute gate), offline. Must NOT be the primary
/// engine: during FinishAsync the primary's native stream still holds its compute
/// gate (acquired TranscribeCppEngine.cs:177, released only in NativeStream.Dispose
/// at :336 via Session.DisposeAsync, NemotronStreamingTranscriber.cs:174, which runs
/// only AFTER FinishAsync returns, StreamingDictationSession.cs:120-121) -- a
/// same-engine fallback would stall 5 s and throw at the gate wait (:235).
/// Wrapped in ProbeTranscriber so a fallback is recorded per clip and still yields text.</summary>
sealed class EngineBatchTranscriber : ITranscriber
{
    private readonly Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine _engine;
    public EngineBatchTranscriber(Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine engine) => _engine = engine;
    public string ModelName => "nemotron-batch";
    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
        => Task.FromResult(new TranscriptionResult(_engine.TranscribeBatch(audio.ToArray()), ModelName));
}

/// <summary>Collects NemotronStreamingTranscriber log lines so the corpus eval can
/// detect the "stream reports was_truncated" fallback reason.</summary>
sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Lines.Add(formatter(state, exception));
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
    private bool _emitNext;
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
        _emitNext = true; // first decode step after each encode emits one token
        return new EncoderOutput(new float[2 * tOut], tOut, 2, tOut);
    }

    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        var logits = new float[8 + 5];
        // One non-blank token per encode: an all-blank stream would trip the
        // session's blank-collapse guard and fall back to the 3 s batch fake,
        // which would fake sim-local-stream's post-stop latency number.
        logits[_emitNext ? 0 : BlankId] = 10f;
        _emitNext = false;
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

/// <summary>Captures ParakeetStreamingSession log lines so the bench can print
/// InteriorSilenceSkipper skip stats and fallback warnings inline.</summary>
sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : " :: " + exception.Message)}");
}
