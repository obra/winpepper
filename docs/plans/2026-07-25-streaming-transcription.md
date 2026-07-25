# Streaming Transcription Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Convert both transcription paths (local Parakeet ONNX and remote AssemblyAI) from batch (all audio processed after recording stops) to streaming (audio processed while the user is still speaking), so the perceived post-stop transcription latency drops dramatically — and prove it with before/after latency measurements.

**Architecture:** A new streaming seam (`IStreamingTranscriber` / `IStreamingTranscriptionSession`) sits beside the existing batch `ITranscriber`. Audio frames already flow through `IWarmAudioRecorder.FramesAvailable` (~50 ms mono 16 kHz float frames); a per-dictation coordinator tees those frames into a streaming session during recording. The local path streams by chunked encoder inference (incremental log-mel + running normalization + left-context chunk encoding + a carried greedy-TDT decoder state). The remote path streams over AssemblyAI's Universal-Streaming v3 WebSocket. Every streaming session guarantees `FinishAsync(fullAudio)` returns the transcript of the whole dictation — sessions recover internally (batch retry / local fallback) so reliability never regresses from today.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly, Microsoft.ML.OnnxRuntime (existing), `System.Net.WebSockets.ClientWebSocket`, `System.Threading.Channels`.

## Global Constraints

- Audio is always **mono 16 kHz float32** samples in `[-1, +1]`.
- ASR provider setting values are exactly `"local"` and `"assemblyai"` (`AppSettings.AsrProvider`, `src/Winpepper.Core/Settings/AppSettings.cs:18`).
- Cloud result model names MUST keep the `"assemblyai/"` prefix — `CloudProvider.IsCloud` (`src/Winpepper.Asr/Transcription/CloudProvider.cs`) gates the cleanup-LLM skip on it.
- AssemblyAI streaming endpoint: `wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true`; auth is the raw API key in the `Authorization` header (no `Bearer` prefix). Audio messages are binary PCM16LE, minimum 50 ms (800 samples / 1600 bytes) per message, maximum 1000 ms.
- Cloud deadline default 10 s, user-clamped to [5, 30] (`AssemblyAiOptions.ClampDeadline`). The deadline budget covers the **post-stop** cloud wait, exactly as `FallbackTranscriber` owns it today.
- **Never log the API key.**
- **Testing on Linux (this environment):** provision via `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"`. Build each test project with `dotnet build tests/<P>/<P>.csproj -c Release -f net9.0` and run with `dotnet exec tests/<P>/bin/Release/net9.0/<P>.dll`. **NEVER use `dotnet test`. NEVER build `winpepper.sln` on Linux.** All tests green before every commit; the full 9-project suite before finishing a task.
- **FULL SUITE GATE** (run from the worktree root; check the runner's exit status, never pipe to `tail` directly):

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/streaming-transcription
for proj in Winpepper.Platform.Tests Winpepper.Models.Tests Winpepper.History.Tests \
            Winpepper.IntegrationTests Winpepper.Corrections.Tests Winpepper.Core.Tests \
            Winpepper.Cleanup.Tests Winpepper.Audio.Tests Winpepper.Asr.Tests; do
  dotnet build "tests/$proj/$proj.csproj" -c Release -f net9.0 > "/tmp/build-$proj.log" 2>&1 \
    || { echo "BUILD FAIL: $proj"; tail -30 "/tmp/build-$proj.log"; exit 1; }
  dotnet exec "tests/$proj/bin/Release/net9.0/$proj.dll" > "/tmp/run-$proj.log" 2>&1 \
    || { echo "TEST FAIL: $proj"; tail -30 "/tmp/run-$proj.log"; exit 1; }
done; echo "ALL GREEN"
```

- **Windows-only code** (`Winpepper.App` incl. `PipelineHost`/`AppShell`, and `#if WINDOWS` blocks like `WarmWasapiRecorder`) cannot be compiled or executed on Linux. A green Linux run is necessary but NOT sufficient for those edits — keep them minimal, mirror every stop-arm change in BOTH duplicated `PipelineHost` arms (HoldUp and Toggle-stop), and copy existing code patterns exactly.
- `README.md` is the only end-user markdown doc; files under `docs/plans/` are working/agent docs and are fine.
- Repo test style: xUnit v3 `[Fact]`/`[Theory]`, Shouldly assertions, `Subject_Condition_ExpectedOutcome` naming, `NullLogger<T>.Instance` for loggers, hand-rolled `public sealed` fakes (no mocking library).
- Commits: focused and atomic, conventional-commit style messages (`feat:`, `test:`, `refactor:`, `docs:`, `chore:`).

## Latency-Evidence Strategy (user requirement)

The user requires transcription time to be clocked **BEFORE** any changes (baseline) and **AFTER** streaming lands, with both numbers reported. The metric is **post-stop latency**: wall time from "recording stopped" to "final transcript available" — this is what the user perceives as "transcription time" (`HistoryTimings.TranscribeMs` measures exactly this window in production).

Environment constraints shape how we measure honestly:
- The local ONNX model cannot run on Linux (DirectML native lib is Windows-only; the model itself lives in `%LOCALAPPDATA%`). Local-path numbers therefore come from a **paced simulation that exercises the real production pipeline classes** with only the ONNX edge replaced by a delay model (documented realtime-factor assumption).
- The remote path is pure managed HTTP/WebSocket and **can run for real from Linux** when `ASSEMBLYAI_API_KEY` is set in the environment; the benchmark runs real-network scenarios whenever the key is present and clearly labels simulated vs real numbers.
- On Windows, production `TranscribeMs` in the history archive reflects the same post-stop window, so the improvement is directly observable after merge.

Task 1 builds the benchmark tool and records the BEFORE numbers into `docs/plans/2026-07-25-streaming-transcription-bench.md` (committed). Task 11 extends the tool with streaming scenarios, records the AFTER numbers into the same document, and commits the comparison. Both runs happen inside this workflow, and the final report quotes both tables.

---

### Task 1: Baseline latency benchmark (BEFORE numbers)

**Files:**
- Create: `scripts/asr-latency-bench/AsrLatencyBench.csproj`
- Create: `scripts/asr-latency-bench/Program.cs`
- Create: `docs/plans/2026-07-25-streaming-transcription-bench.md`

**Interfaces:**
- Consumes: `ITranscriber`, `AssemblyAiTranscriber`, `IAssemblyAiClient`, `AssemblyAiClient`, `IAssemblyAiKeyStore`, `AssemblyAiOptions`, `AssemblyAiTranscript` — all existing, from `Winpepper.Asr`.
- Produces: `scripts/asr-latency-bench` console tool with scenario names `sim-local-batch`, `sim-remote-batch`, `real-remote-batch` (Task 11 adds `sim-local-stream`, `sim-remote-stream`, `real-remote-stream`); the committed bench-results doc.

- [ ] **Step 1: Create the benchmark project**

Create `scripts/asr-latency-bench/AsrLatencyBench.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AsrLatencyBench</RootNamespace>
    <AssemblyName>AsrLatencyBench</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Asr\Winpepper.Asr.csproj" />
  </ItemGroup>
</Project>
```

Note: root `Directory.Build.props` / `Directory.Packages.props` apply; do NOT add package versions to this csproj, and do NOT add the project to `winpepper.sln`.

- [ ] **Step 2: Write the benchmark program**

Create `scripts/asr-latency-bench/Program.cs`:

```csharp
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
```

Adjust `using` lines / `NullLogger` references if the build reports missing namespaces (`Microsoft.Extensions.Logging.Abstractions` is available transitively via `Winpepper.Asr`).

- [ ] **Step 3: Build and run the baseline benchmark**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/streaming-transcription
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release
dotnet run --project scripts/asr-latency-bench -c Release
```

Expected: a markdown table with `sim-local-batch` ≈ 3000 ms, `sim-remote-batch` ≈ 4200–5200 ms (upload 400 ms + first-poll grace 750 ms + processing 3000 ms rounded up to the 1 s poll grid), `real-remote-batch` either SKIPPED or a real millisecond number. If `ASSEMBLYAI_API_KEY` is set in the environment, the real number MUST be captured.

- [ ] **Step 4: Record the BEFORE numbers**

Create `docs/plans/2026-07-25-streaming-transcription-bench.md` with the tool's actual output pasted in (replace the `<measured>` placeholders with the real numbers from Step 3 — do NOT commit placeholders):

```markdown
# Streaming Transcription — Post-Stop Latency Evidence

Metric: **post-stop latency** — wall ms from "recording stopped" to "final
transcript available" (production `HistoryTimings.TranscribeMs` window).
Tool: `scripts/asr-latency-bench` (`dotnet run --project scripts/asr-latency-bench -c Release`).

Simulation assumptions (documented, identical for BEFORE and AFTER runs):
local realtime factor 0.30; cloud upload 400 ms; cloud batch processing 3.0 s
for a 10 s clip; AssemblyAI first-poll grace 750 ms + 1 s poll grid
(production `AssemblyAiOptions` values). `sim-*` rows exercise the real
production pipeline classes with only the ONNX/network edge replaced by these
delay models; `real-*` rows hit the real AssemblyAI API (run when
`ASSEMBLYAI_API_KEY` is set).

## BEFORE (batch architecture) — recorded 2026-07-25

| scenario | kind | audio | post-stop latency (ms) |
|---|---|---|---|
| sim-local-batch | simulated | 10 s | <measured> |
| sim-remote-batch | simulated | 10 s | <measured> |
| real-remote-batch | REAL network | 10 s | <measured or SKIPPED — no API key> |

## AFTER (streaming architecture)

_To be recorded by the final task of this plan._
```

- [ ] **Step 5: Run the full suite gate** (nothing production changed, but prove the tree is green before the first commit) — run the FULL SUITE GATE block from Global Constraints. Expected: `ALL GREEN`.

- [ ] **Step 6: Commit**

```bash
git add scripts/asr-latency-bench docs/plans/2026-07-25-streaming-transcription-bench.md
git commit -m "chore(bench): add ASR post-stop latency benchmark and record batch baseline"
```

---

### Task 2: Streaming transcription seam + batch adapter

**Files:**
- Create: `src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs`
- Create: `src/Winpepper.Asr/Transcription/BatchStreamingAdapter.cs`
- Delete: `src/Winpepper.Asr/StreamingTranscriber.cs` (superseded buffer-then-flush stub, not wired into production)
- Delete: `tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs`
- Test: `tests/Winpepper.Asr.Tests/BatchStreamingAdapterTests.cs`

**Interfaces:**
- Consumes: `ITranscriber`, `TranscriptionResult` (`src/Winpepper.Asr/Transcription/ITranscriber.cs`), `FakeTranscriber` (existing test double).
- Produces (used by Tasks 6–11):
  - `IStreamingTranscriptionSession : IAsyncDisposable` with `ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)` and `Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)`
  - `IStreamingTranscriber` with `string ModelName { get; }` and `Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)`
  - `BatchStreamingAdapter(ITranscriber inner) : IStreamingTranscriber` — the seam's executable contract specification (its test pins the "FinishAsync(fullAudio) is authoritative" rule every later implementation must honor) and the drop-in for any batch-only provider

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/BatchStreamingAdapterTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class BatchStreamingAdapterTests
{
    [Fact]
    public void ModelName_PassesThrough()
    {
        var adapter = new BatchStreamingAdapter(FakeTranscriber.Returning("m1", "hi"));
        adapter.ModelName.ShouldBe("m1");
    }

    [Fact]
    public async Task Finish_TranscribesTheFullBuffer_IgnoringPushedFrames()
    {
        ReadOnlyMemory<float> seen = default;
        var inner = new FakeTranscriber("m1", () => Task.FromResult(new TranscriptionResult("hello", "m1")));
        var adapter = new BatchStreamingAdapter(new CapturingTranscriber(inner, m => seen = m));

        await using var session = await adapter.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[123], TestContext.Current.CancellationToken); // ignored
        var full = new float[456];
        var result = await session.FinishAsync(full, TestContext.Current.CancellationToken);

        result.Text.ShouldBe("hello");
        seen.Length.ShouldBe(456); // FinishAsync's fullAudio is authoritative
        inner.Calls.ShouldBe(1);
    }

    /// <summary>Records the buffer handed to the wrapped transcriber.</summary>
    private sealed class CapturingTranscriber : ITranscriber
    {
        private readonly ITranscriber _inner;
        private readonly Action<ReadOnlyMemory<float>> _capture;
        public CapturingTranscriber(ITranscriber inner, Action<ReadOnlyMemory<float>> capture)
        { _inner = inner; _capture = capture; }
        public string ModelName => _inner.ModelName;
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { _capture(mono16k); return _inner.TranscribeAsync(mono16k, ct); }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/streaming-transcription
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246: The type or namespace name 'BatchStreamingAdapter' could not be found`.

- [ ] **Step 3: Implement the seam**

Create `src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>
/// One dictation's streaming transcription session. Created at recording start;
/// audio is pushed as it is captured; FinishAsync is called at recording stop.
///
/// CONTRACT: FinishAsync(fullAudio) must always return the transcript of the
/// ENTIRE dictation. Implementations that received zero pushed samples MUST
/// transcribe fullAudio from scratch, and implementations whose streaming state
/// became unusable (mid-stream failure) MUST recover internally (e.g. a batch
/// retry) — the pipeline relies on this so reliability never regresses.
/// </summary>
public interface IStreamingTranscriptionSession : IAsyncDisposable
{
    /// <summary>Feed mono 16 kHz float samples captured during recording. May do
    /// heavy work (inference / network sends) — callers pump from a background task.</summary>
    ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);

    /// <summary>Signal end-of-audio and await the final transcript.
    /// <paramref name="fullAudio"/> is the complete (silence-trimmed) session buffer.</summary>
    Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct);
}

/// <summary>Streaming counterpart of <see cref="ITranscriber"/>: one session per dictation.</summary>
public interface IStreamingTranscriber
{
    /// <summary>The model identifier this transcriber would report on success.</summary>
    string ModelName { get; }

    Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct);
}
```

Create `src/Winpepper.Asr/Transcription/BatchStreamingAdapter.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>
/// Adapts a batch <see cref="ITranscriber"/> to the streaming seam. Pushed audio
/// is ignored — the pipeline hands the authoritative full buffer to FinishAsync —
/// so this adapter preserves batch behavior exactly. Used when a provider has no
/// streaming implementation and as the stop-time fallback path.
/// </summary>
public sealed class BatchStreamingAdapter : IStreamingTranscriber
{
    private readonly ITranscriber _inner;

    public BatchStreamingAdapter(ITranscriber inner) => _inner = inner;

    public string ModelName => _inner.ModelName;

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(new Session(_inner));

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly ITranscriber _inner;
        internal Session(ITranscriber inner) => _inner = inner;

        public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            => ValueTask.CompletedTask;

        public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
            => _inner.TranscribeAsync(fullAudio, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

Delete `src/Winpepper.Asr/StreamingTranscriber.cs` and `tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs` (the stub's own doc comment says it exists only until true streaming lands; nothing in `src/` references it — verify with `grep -rn "StreamingTranscriber" src/` which must return only the new files).

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.BatchStreamingAdapterTests"
```

Expected: PASS (2 tests).

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block from Global Constraints. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs \
        src/Winpepper.Asr/Transcription/BatchStreamingAdapter.cs \
        tests/Winpepper.Asr.Tests/BatchStreamingAdapterTests.cs
git rm src/Winpepper.Asr/StreamingTranscriber.cs tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs
git commit -m "feat(asr): add streaming transcription seam with batch adapter"
```

---

### Task 3: Pre-roll audio reaches streaming consumers

The recorder seeds ~500 ms of pre-roll into the session buffer at `StartSession`, but that audio never flows through `FramesAvailable` — a streaming consumer would silently miss the first half-second (often the first word). Fix: `WarmCaptureBuffer.StartSession` returns the seeded pre-roll; the Windows recorder raises it as one frame.

**Files:**
- Modify: `src/Winpepper.Audio/WarmCaptureBuffer.cs` (`StartSession` returns `float[]`)
- Modify: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (raise the returned pre-roll; `#if WINDOWS` — cannot be compile-verified on Linux, keep the edit exact)
- Test: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs` (add cases)

**Interfaces:**
- Consumes: existing `WarmCaptureBuffer` (`Ingest`, `StartSession`, `StopSession`).
- Produces: `public float[] StartSession(int prerollSamples)` on `WarmCaptureBuffer` — returns the seeded pre-roll (empty array when none). `IWarmAudioRecorder.StartSession(int)` is unchanged (stays `void`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs` (inside the existing `WarmCaptureBufferTests` class, reusing its `Ramp` helper):

```csharp
    [Fact]
    public void StartSession_ReturnsTheSeededPreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 15)); // ring keeps 5..14

        var preroll = buf.StartSession(prerollSamples: 10);

        preroll.ShouldBe(Ramp(5, 10)); // exactly what StopSession will lead with
        buf.StopSession().ShouldBe(Ramp(5, 10));
    }

    [Fact]
    public void StartSession_NoRingHistory_ReturnsEmptyPreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        var preroll = buf.StartSession(prerollSamples: 10);
        preroll.ShouldBeEmpty();
    }

    [Fact]
    public void StartSession_ZeroPrerollRequested_ReturnsEmpty()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 5));
        var preroll = buf.StartSession(prerollSamples: 0);
        preroll.ShouldBeEmpty();
    }
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0815`/`CS0029` (cannot assign `void` to an implicitly-typed variable) — `StartSession` currently returns `void`.

- [ ] **Step 3: Implement**

In `src/Winpepper.Audio/WarmCaptureBuffer.cs`, replace the `StartSession` method (currently lines 55–67) with:

```csharp
    /// <summary>
    /// Begin a session, seeding up to <paramref name="prerollSamples"/> from the
    /// ring. Returns the seeded pre-roll (empty when none) so the recorder can
    /// raise it through FramesAvailable — streaming consumers must observe the
    /// same audio that StopSession will return, and the pre-roll never flows
    /// through Ingest during the session.
    /// </summary>
    public float[] StartSession(int prerollSamples)
    {
        if (prerollSamples < 0) prerollSamples = 0;
        lock (_lock)
        {
            _session.Clear();
            var take = Math.Min(prerollSamples, _ring.Count);
            if (take > 0)
                _session.AddRange(_ring.GetRange(_ring.Count - take, take));
            _active = true;
            _sessionWasSilent = false;
            return take > 0 ? _session.ToArray() : Array.Empty<float>();
        }
    }
```

In `src/Winpepper.Audio/WarmWasapiRecorder.cs`, inside `public void StartSession(int includePrerollMs)` (lines ~135–151), replace the last two lines of the method body:

```csharp
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
```

with:

```csharp
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        var preroll = _buffer.StartSession(prerollSamples);
        // The seeded pre-roll never flows through Ingest during the session, so
        // raise it here — otherwise streaming consumers (Task 10's frame tee)
        // would be missing the dictation's first ~500 ms. The level meter also
        // subscribes; one larger frame at session start is harmless.
        if (preroll.Length > 0) FramesAvailable?.Invoke(preroll);
```

This file is `#if WINDOWS` — Linux cannot compile it. Double-check the edit by eye against the surrounding code; the Windows build will verify it.

Check for other `WarmCaptureBuffer.StartSession` callers: `grep -rn "\.StartSession(" src/ tests/` — for each caller that ignores the return value, no change is needed (a discarded return value is legal C#). Update any caller that the compiler flags.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -class "Winpepper.Audio.Tests.WarmCaptureBufferTests"
```

Expected: PASS (all existing + 3 new).

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Audio/WarmCaptureBuffer.cs src/Winpepper.Audio/WarmWasapiRecorder.cs \
        tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs
git commit -m "feat(audio): surface the seeded pre-roll through FramesAvailable"
```

---

### Task 4: Streaming log-mel extraction + running normalization

Batch `MelFeatureExtractor.Extract` (`src/Winpepper.Asr/MelFeatureExtractor.cs`) does: (1) whole-buffer preemphasis, (2) centered framing with `NFft/2` zero padding + per-frame DFT→mel→log, (3) per-utterance mean/std normalization (ddof=1). Steps 1–2 are exactly reproducible incrementally (frame *t* needs only samples up to `t*Hop + NFft/2`); step 3 is inherently global, so streaming uses **running statistics** (equal to batch when all frames arrive before the first normalize).

**Files:**
- Modify: `src/Winpepper.Asr/MelFeatureExtractor.cs` (widen 3 private static helpers + 3 private consts to `internal`)
- Create: `src/Winpepper.Asr/StreamingLogMelExtractor.cs`
- Create: `src/Winpepper.Asr/RunningMelNormalizer.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingMelTests.cs`

**Interfaces:**
- Consumes: `PreprocessorConfig` (`FeatureSize=128, HopLength=160, NFft=512, WinLength=400, Preemphasis=0.97, SamplingRate=16000`), `MelFeatureExtractor.Extract(ReadOnlySpan<float>) : float[,]`.
- Produces (used by Task 6):
  - `StreamingLogMelExtractor(PreprocessorConfig config)` with `void Push(ReadOnlySpan<float> samples)`, `void Drain(List<double[]> sink)`, `void Finish()`
  - `RunningMelNormalizer(int featureSize)` with `void Add(IReadOnlyList<double[]> frames)`, `float[,] Normalize(IReadOnlyList<double[]> frames)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/StreamingMelTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingMelTests
{
    private static float[] RandomAudio(int samples, int seed = 7)
    {
        var rng = new Random(seed);
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = (float)(rng.NextDouble() * 0.8 - 0.4);
        return a;
    }

    private static List<double[]> StreamAll(float[] audio, int chunkSize)
    {
        var extractor = new StreamingLogMelExtractor(PreprocessorConfig.ParakeetTdtV3);
        var frames = new List<double[]>();
        for (var i = 0; i < audio.Length; i += chunkSize)
        {
            extractor.Push(audio.AsSpan(i, Math.Min(chunkSize, audio.Length - i)));
            extractor.Drain(frames);
        }
        extractor.Finish();
        extractor.Drain(frames);
        return frames;
    }

    [Theory]
    [InlineData(1600)]   // 100 ms chunks
    [InlineData(800)]    // 50 ms — the recorder's real cadence
    [InlineData(333)]    // odd size, never aligned to hop
    public void Streaming_MatchesBatch_ExactlyRegardlessOfChunking(int chunkSize)
    {
        var config = PreprocessorConfig.ParakeetTdtV3;
        var audio = RandomAudio(16000 * 2 + 137); // 2 s + odd tail

        var streamed = StreamAll(audio, chunkSize);
        var normalizer = new RunningMelNormalizer(config.FeatureSize);
        normalizer.Add(streamed);
        var streamedNormalized = normalizer.Normalize(streamed);

        var batch = new MelFeatureExtractor(config).Extract(audio);

        streamed.Count.ShouldBe(batch.GetLength(0)); // len/hop + 1 frames
        for (var t = 0; t < streamed.Count; t++)
            for (var m = 0; m < config.FeatureSize; m++)
                ((double)streamedNormalized[t, m]).ShouldBe(batch[t, m], 1e-4,
                    $"frame {t}, mel {m}");
    }

    [Fact]
    public void Streaming_MidStream_OnlyEmitsFramesWithFullRightContext()
    {
        var config = PreprocessorConfig.ParakeetTdtV3;
        var extractor = new StreamingLogMelExtractor(config);
        var frames = new List<double[]>();

        // Frame t needs samples through t*Hop + NFft/2. With exactly NFft/2
        // samples pushed only frame 0 is computable.
        extractor.Push(RandomAudio(config.NFft / 2));
        extractor.Drain(frames);
        frames.Count.ShouldBe(1);
    }

    [Fact]
    public void Drain_IsIncremental_NeverReEmitsFrames()
    {
        var audio = RandomAudio(16000);
        var extractor = new StreamingLogMelExtractor(PreprocessorConfig.ParakeetTdtV3);
        var a = new List<double[]>();
        extractor.Push(audio);
        extractor.Drain(a);
        var countAfterFirstDrain = a.Count;
        extractor.Drain(a);
        a.Count.ShouldBe(countAfterFirstDrain);
    }

    [Fact]
    public void RunningNormalizer_WithAllFramesUpFront_EqualsBatchNormalization()
    {
        // Covered numerically by the exactness theory above; this pins the shape
        // and the ddof=1 divisor for a tiny hand-checkable input.
        var normalizer = new RunningMelNormalizer(featureSize: 1);
        var frames = new List<double[]> { new[] { 1.0 }, new[] { 3.0 } };
        normalizer.Add(frames);
        var norm = normalizer.Normalize(frames);
        // mean 2, ddof=1 variance ((1)^2+(1)^2)/1 = 2, std ~1.41421 + 1e-5
        ((double)norm[0, 0]).ShouldBe(-0.70710, 1e-3);
        ((double)norm[1, 0]).ShouldBe(0.70710, 1e-3);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `StreamingLogMelExtractor`.

- [ ] **Step 3: Implement**

In `src/Winpepper.Asr/MelFeatureExtractor.cs`, change visibility only (no logic changes):
- line 16–18: `private const double MelOffset ...`, `Epsilon`, `MelMin` → `internal const`
- line 91: `private static void HandRolledRfftPower` → `internal static void HandRolledRfftPower`
- line 108: `private static double[] BuildHannWindow` → `internal static double[] BuildHannWindow`
- line 117: `private static double[][] BuildSlaneyMelFilters` → `internal static double[][] BuildSlaneyMelFilters`

Create `src/Winpepper.Asr/StreamingLogMelExtractor.cs`:

```csharp
namespace Winpepper.Asr;

/// <summary>
/// Incremental log-mel extractor producing frames EXACTLY equal (pre-normalization)
/// to MelFeatureExtractor's steps 1–2 over the same total audio, regardless of how
/// the audio is chunked. Frame t is centered at sample t*Hop and needs samples up
/// to t*Hop + NFft/2 (exclusive); mid-stream only frames whose full right context
/// has arrived are emitted, and Finish() zero-pads the tail exactly like the batch
/// path so the total frame count matches batch (totalSamples/Hop + 1).
/// </summary>
public sealed class StreamingLogMelExtractor
{
    private readonly PreprocessorConfig _config;
    private readonly double[] _window;
    private readonly double[][] _melFilters;
    private readonly List<float> _raw = new(); // unconsumed raw samples; _raw[0] is global index _rawStart
    private long _rawStart;
    private long _totalSamples;
    private long _nextFrame;
    private bool _finished;

    public StreamingLogMelExtractor(PreprocessorConfig config)
    {
        _config = config;
        _window = MelFeatureExtractor.BuildHannWindow(config.NFft, config.WinLength);
        _melFilters = MelFeatureExtractor.BuildSlaneyMelFilters(config.NFft, config.FeatureSize, config.SamplingRate);
    }

    public void Push(ReadOnlySpan<float> samples)
    {
        if (_finished) throw new InvalidOperationException("Push after Finish");
        foreach (var s in samples) _raw.Add(s);
        _totalSamples += samples.Length;
    }

    /// <summary>After Finish(), Drain emits the zero-right-padded tail frames.</summary>
    public void Finish() => _finished = true;

    /// <summary>Append every frame computable so far (double[FeatureSize] each) to <paramref name="sink"/>.</summary>
    public void Drain(List<double[]> sink)
    {
        var pad = _config.NFft / 2;
        while (true)
        {
            var frameStart = _nextFrame * _config.HopLength - pad; // global; < 0 near utterance start
            var frameEnd = frameStart + _config.NFft;              // exclusive
            if (!_finished && frameEnd > _totalSamples) return;    // needs future audio
            if (_finished && _nextFrame > _totalSamples / _config.HopLength) return; // batch: len/hop + 1 frames
            sink.Add(ComputeFrame(frameStart));
            _nextFrame++;
            TrimConsumed(pad);
        }
    }

    private double[] ComputeFrame(long frameStart)
    {
        var n = _config.NFft;
        var frame = new double[n];
        for (var k = 0; k < n; k++)
            frame[k] = Preemphasized(frameStart + k) * _window[k];

        var nBins = n / 2 + 1;
        var power = new double[nBins];
        MelFeatureExtractor.HandRolledRfftPower(frame, n, power);

        var mel = new double[_config.FeatureSize];
        for (var m = 0; m < _config.FeatureSize; m++)
        {
            double acc = 0.0;
            var filter = _melFilters[m];
            for (var k = 0; k < nBins; k++) acc += power[k] * filter[k];
            mel[m] = Math.Log(Math.Max(acc + MelFeatureExtractor.MelOffset, MelFeatureExtractor.MelMin));
        }
        return mel;
    }

    // Batch preemphasis is x[j] -= p * x[j-1] for j >= 1 with x[0] unchanged, and
    // the NFft/2 zero padding is added AFTER preemphasis — so out-of-range indices
    // are exact zeros and in-range values depend on at most one previous raw sample.
    private double Preemphasized(long g)
    {
        if (g < 0 || g >= _totalSamples) return 0.0;
        var raw = (double)RawAt(g);
        return g == 0 ? raw : raw - _config.Preemphasis * RawAt(g - 1);
    }

    private float RawAt(long g) => _raw[(int)(g - _rawStart)];

    private void TrimConsumed(int pad)
    {
        // Keep everything the NEXT frame (and its preemphasis lookback) needs.
        var keepFrom = _nextFrame * _config.HopLength - pad - 1;
        if (keepFrom <= _rawStart) return;
        var drop = (int)Math.Min(keepFrom - _rawStart, _raw.Count);
        if (drop > 0) { _raw.RemoveRange(0, drop); _rawStart += drop; }
    }
}
```

Create `src/Winpepper.Asr/RunningMelNormalizer.cs`:

```csharp
namespace Winpepper.Asr;

/// <summary>
/// Streaming replacement for MelFeatureExtractor's per-utterance normalization
/// (step 3). Batch normalization needs the WHOLE utterance's mean/std, which a
/// streaming path cannot have; this uses running statistics over every log-mel
/// frame seen so far (same ddof=1 convention and epsilon). When all frames are
/// Add()ed before the first Normalize call the output equals batch (up to
/// one-pass vs two-pass variance rounding).
/// </summary>
public sealed class RunningMelNormalizer
{
    private const double Epsilon = 1e-5; // matches MelFeatureExtractor.Epsilon

    private readonly int _featureSize;
    private long _count;
    private readonly double[] _sum;
    private readonly double[] _sumSq;

    public RunningMelNormalizer(int featureSize)
    {
        _featureSize = featureSize;
        _sum = new double[featureSize];
        _sumSq = new double[featureSize];
    }

    public void Add(IReadOnlyList<double[]> frames)
    {
        foreach (var f in frames)
        {
            for (var m = 0; m < _featureSize; m++)
            {
                _sum[m] += f[m];
                _sumSq[m] += f[m] * f[m];
            }
            _count++;
        }
    }

    /// <summary>Normalize <paramref name="frames"/> with the CURRENT running stats → [T, featureSize].</summary>
    public float[,] Normalize(IReadOnlyList<double[]> frames)
    {
        var output = new float[frames.Count, _featureSize];
        var divisor = _count > 1 ? _count - 1 : 1;
        for (var m = 0; m < _featureSize; m++)
        {
            var mean = _count > 0 ? _sum[m] / _count : 0.0;
            var variance = Math.Max((_sumSq[m] - _count * mean * mean) / divisor, 0.0);
            var invStd = 1.0 / (Math.Sqrt(variance) + Epsilon);
            for (var t = 0; t < frames.Count; t++)
                output[t, m] = (float)((frames[t][m] - mean) * invStd);
        }
        return output;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.StreamingMelTests"
```

Expected: PASS. (The exactness theory runs the O(n²) DFT over ~2 s of audio three times — allow ~a minute.) If the exactness comparison fails at the very first or last frames only, re-check the `Preemphasized` boundary conditions and the `Drain` finish condition against the batch math above.

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/MelFeatureExtractor.cs src/Winpepper.Asr/StreamingLogMelExtractor.cs \
        src/Winpepper.Asr/RunningMelNormalizer.cs tests/Winpepper.Asr.Tests/StreamingMelTests.cs
git commit -m "feat(asr): incremental log-mel extraction with running normalization"
```

---

### Task 5: Parakeet backend seam + pure TDT greedy decoder

`ParakeetSession.GreedyDecode` (`src/Winpepper.Asr/ParakeetSession.cs:134-215`) fuses the decode-loop policy with the ONNX calls, making it untestable off-Windows and unusable across chunks. Split it: `IParakeetBackend` abstracts the two ONNX models; `TdtGreedyDecoder` is the pure loop with a carried `TdtDecoderState`. Batch `Transcribe` is rewired through the same primitives (no behavior change; the loop is an exact port).

**Files:**
- Create: `src/Winpepper.Asr/ParakeetBackend.cs`
- Create: `src/Winpepper.Asr/TdtGreedyDecoder.cs`
- Modify: `src/Winpepper.Asr/ParakeetSession.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeParakeetBackend.cs`
- Test: `tests/Winpepper.Asr.Tests/TdtGreedyDecoderTests.cs`

**Interfaces:**
- Consumes: `ParakeetSession` internals (`_encoder`, `_decoderJoint`, `_vocab`, `_features`), `Vocabulary` (`Size`, `BlankId`, `Decode(IEnumerable<int>)`), `ParakeetTranscript(string Text, IReadOnlyList<int> TokenIds, IReadOnlyList<int> FrameIndices, IReadOnlyList<int> Durations)`.
- Produces (used by Tasks 6, 10, 11):
  - `readonly record struct EncoderOutput(float[] Data, int ValidLen, int Dim, int Frames)` — `Data` laid out `[Dim, Frames]` row-major (`Data[d * Frames + t]`)
  - `sealed record DecoderJointResult(float[] Logits, float[] StateH, float[] StateC)`
  - `interface IParakeetBackend { int VocabSize {get;} int BlankId {get;} int DecoderHiddenLayers {get;} int DecoderHiddenDim {get;} EncoderOutput Encode(float[,] melFrames); DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC); string DecodeTokens(IEnumerable<int> tokenIds); }`
  - `sealed class TdtDecoderState` (`StateH`, `StateC`, `LastToken`, `CarryAdvance`; ctor `(int hiddenLayers, int hiddenDim, int blankId)`)
  - `static class TdtGreedyDecoder { const int MaxTokensPerStep = 10; static void Decode(IParakeetBackend backend, EncoderOutput enc, TdtDecoderState state, List<int> tokens, List<int> frameIndices, List<int> durations, int startFrame = 0, int frameIndexOffset = 0); }`
  - `ParakeetSession : IParakeetBackend` (in addition to `IDisposable`)
  - test double `FakeParakeetBackend`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/FakeParakeetBackend.cs`:

```csharp
using Winpepper.Asr;

namespace Winpepper.Asr.Tests;

/// <summary>
/// Frame-local fake backend: Encode passes mel frame t's first component through
/// as encoder frame t (subsampling configurable), and DecodeJoint behavior is
/// scripted per call. Records calls so tests can assert chunking/state mechanics.
/// </summary>
public sealed class FakeParakeetBackend : IParakeetBackend
{
    public int VocabSize { get; init; } = 8; // tokens 0..6, blank = 7
    public int BlankId => VocabSize - 1;
    public int DecoderHiddenLayers => 2;
    public int DecoderHiddenDim => 4;
    public int SubsamplingFactor { get; init; } = 1;
    public int DurationBins { get; init; } = 5;

    public List<int> EncodeMelFrameCounts { get; } = new();
    public List<(float FirstComponent, int LastToken)> JointCalls { get; } = new();

    /// <summary>Optional scripted joint. Args: encoder frame, lastToken. Default: always blank, advance 1.</summary>
    public Func<float[], int, DecoderJointResult>? Joint { get; init; }

    /// <summary>Optional Encode override for failure injection (called with mel frame count).</summary>
    public Action<int>? OnEncode { get; init; }

    public EncoderOutput Encode(float[,] melFrames)
    {
        var tIn = melFrames.GetLength(0);
        OnEncode?.Invoke(tIn);
        EncodeMelFrameCounts.Add(tIn);
        var tOut = Math.Max(1, tIn / SubsamplingFactor);
        const int dim = 2;
        var data = new float[dim * tOut];
        for (var t = 0; t < tOut; t++)
        {
            data[0 * tOut + t] = melFrames[t * SubsamplingFactor, 0];
            data[1 * tOut + t] = t;
        }
        return new EncoderOutput(data, tOut, dim, tOut);
    }

    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        JointCalls.Add((encoderFrame[0], lastToken));
        if (Joint is not null) return Joint(encoderFrame, lastToken);
        var logits = new float[VocabSize + DurationBins];
        logits[BlankId] = 10f;          // blank wins
        logits[VocabSize + 1] = 10f;    // duration 1
        return new DecoderJointResult(logits, stateH, stateC);
    }

    public string DecodeTokens(IEnumerable<int> tokenIds) => string.Join(",", tokenIds);

    /// <summary>Build a joint result emitting <paramref name="token"/> with duration <paramref name="dur"/>.</summary>
    public DecoderJointResult Emit(int token, int dur, float[]? h = null, float[]? c = null)
    {
        var logits = new float[VocabSize + DurationBins];
        logits[token] = 10f;
        logits[VocabSize + dur] = 10f;
        return new DecoderJointResult(
            logits,
            h ?? new float[DecoderHiddenLayers * DecoderHiddenDim],
            c ?? new float[DecoderHiddenLayers * DecoderHiddenDim]);
    }
}
```

Create `tests/Winpepper.Asr.Tests/TdtGreedyDecoderTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class TdtGreedyDecoderTests
{
    private static EncoderOutput Enc(int frames)
    {
        const int dim = 2;
        var data = new float[dim * frames];
        for (var t = 0; t < frames; t++) { data[t] = t; data[frames + t] = t; }
        return new EncoderOutput(data, frames, dim, frames);
    }

    private static TdtDecoderState NewState(FakeParakeetBackend b)
        => new(b.DecoderHiddenLayers, b.DecoderHiddenDim, b.BlankId);

    [Fact]
    public void Blank_AdvancesOneFrame_WithoutEmitting()
    {
        var backend = new FakeParakeetBackend(); // default: blank, dur 1
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(4), NewState(backend), tokens, fi, du);
        tokens.ShouldBeEmpty();
        backend.JointCalls.Count.ShouldBe(4);
    }

    [Fact]
    public void NonBlank_EmitsToken_AndAdoptsState()
    {
        var backend0 = new FakeParakeetBackend();
        var newH = new float[8]; newH[0] = 42f;
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => last == 7 /*blank start*/
                ? backend0.Emit(3, 1, h: newH)
                : backend0.Emit(7, 1),
        };
        var state = NewState(backend);
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du);
        tokens.ShouldBe(new[] { 3 });
        fi.ShouldBe(new[] { 0 });
        state.LastToken.ShouldBe(3);
        state.StateH[0].ShouldBe(42f);
    }

    [Fact]
    public void DurationHead_SkipsFrames()
    {
        var backend0 = new FakeParakeetBackend();
        var calls = 0;
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => { calls++; return backend0.Emit(2, 3); }, // always dur 3
        };
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(9), NewState(backend), tokens, fi, du);
        calls.ShouldBe(3);                 // frames 0, 3, 6
        fi.ShouldBe(new[] { 0, 3, 6 });
        du.ShouldBe(new[] { 3, 3, 3 });
    }

    [Fact]
    public void ZeroDuration_EmissionsCappedByMaxTokensPerStep()
    {
        var backend0 = new FakeParakeetBackend();
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => backend0.Emit(1, 0), // emit forever at frame 0
        };
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(1), NewState(backend), tokens, fi, du);
        tokens.Count.ShouldBe(TdtGreedyDecoder.MaxTokensPerStep);
    }

    [Fact]
    public void CarryAdvance_ContinuesTheSkipIntoTheNextSegment()
    {
        var backend0 = new FakeParakeetBackend();
        var backend = new FakeParakeetBackend
        {
            Joint = (frame, last) => backend0.Emit(2, 4), // dur 4 from frame 0
        };
        var state = NewState(backend);
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();

        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du); // t: 0 -> 4, limit 3
        state.CarryAdvance.ShouldBe(1);

        backend.JointCalls.Clear();
        TdtGreedyDecoder.Decode(backend, Enc(3), state, tokens, fi, du, frameIndexOffset: 3);
        backend.JointCalls.Count.ShouldBe(1);       // starts at local frame 1 (carry), jumps past end
        fi[1].ShouldBe(4);                          // global index = offset 3 + local 1
    }

    [Fact]
    public void StartFrame_SkipsDiscardedContextFrames()
    {
        var backend = new FakeParakeetBackend(); // blank, dur 1
        var tokens = new List<int>(); var fi = new List<int>(); var du = new List<int>();
        TdtGreedyDecoder.Decode(backend, Enc(6), NewState(backend), tokens, fi, du, startFrame: 2);
        backend.JointCalls.Count.ShouldBe(4); // frames 2..5
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `IParakeetBackend` / `TdtGreedyDecoder`.

- [ ] **Step 3: Implement the seam and the pure decoder**

Create `src/Winpepper.Asr/ParakeetBackend.cs`:

```csharp
namespace Winpepper.Asr;

/// <summary>Encoder output laid out [Dim, Frames] row-major: Data[d * Frames + t].</summary>
public readonly record struct EncoderOutput(float[] Data, int ValidLen, int Dim, int Frames);

/// <summary>One decoder_joint step's outputs (fresh arrays each call).</summary>
public sealed record DecoderJointResult(float[] Logits, float[] StateH, float[] StateC);

/// <summary>
/// Seam over the two ONNX models so the greedy TDT decode loop and the chunked
/// streaming session are pure and Linux-testable. Implemented by ParakeetSession.
/// </summary>
public interface IParakeetBackend
{
    int VocabSize { get; }
    int BlankId { get; }
    int DecoderHiddenLayers { get; }
    int DecoderHiddenDim { get; }

    /// <summary>Run the encoder over [T, FeatureSize] normalized mel frames.</summary>
    EncoderOutput Encode(float[,] melFrames);

    /// <summary>Run one decoder_joint step for a single encoder frame (length Dim).</summary>
    DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC);

    string DecodeTokens(IEnumerable<int> tokenIds);
}
```

Create `src/Winpepper.Asr/TdtGreedyDecoder.cs`:

```csharp
namespace Winpepper.Asr;

/// <summary>Greedy TDT decode state carried across encoder segments (chunks).</summary>
public sealed class TdtDecoderState
{
    public float[] StateH { get; set; }
    public float[] StateC { get; set; }
    public int LastToken { get; set; }

    /// <summary>Frame-advance overshoot left over from the previous segment: the TDT
    /// duration head can skip past a segment's end; the skip continues into the next.</summary>
    public int CarryAdvance { get; set; }

    public TdtDecoderState(int hiddenLayers, int hiddenDim, int blankId)
    {
        StateH = new float[hiddenLayers * 1 * hiddenDim];
        StateC = new float[hiddenLayers * 1 * hiddenDim];
        LastToken = blankId;
    }
}

/// <summary>
/// Pure greedy TDT decode loop — an exact port of the former
/// ParakeetSession.GreedyDecode — parameterized over a backend and a carried
/// state so it can run over a whole utterance (batch) or over successive
/// encoder segments (streaming).
/// </summary>
public static class TdtGreedyDecoder
{
    public const int MaxTokensPerStep = 10;

    /// <summary>
    /// Decode encoder frames [startFrame + state.CarryAdvance, min(Frames, ValidLen))
    /// of <paramref name="enc"/>, mutating <paramref name="state"/> and appending to
    /// the token lists. <paramref name="frameIndexOffset"/> is added to recorded
    /// frame indices so streaming callers report utterance-global positions.
    /// </summary>
    public static void Decode(
        IParakeetBackend backend,
        EncoderOutput enc,
        TdtDecoderState state,
        List<int> tokens,
        List<int> frameIndices,
        List<int> durations,
        int startFrame = 0,
        int frameIndexOffset = 0)
    {
        var vocabSize = backend.VocabSize;
        var blankId = backend.BlankId;
        var limit = Math.Min(enc.Frames, enc.ValidLen);

        var t = startFrame + state.CarryAdvance;
        state.CarryAdvance = 0;
        var emitted = 0;
        var frameBuf = new float[enc.Dim];

        while (t < limit)
        {
            for (var k = 0; k < enc.Dim; k++) frameBuf[k] = enc.Data[k * enc.Frames + t];
            var step = backend.DecodeJoint(frameBuf, state.LastToken, state.StateH, state.StateC);
            var flat = step.Logits;

            var bestToken = 0; var bestVal = float.NegativeInfinity;
            for (var i = 0; i < vocabSize; i++)
                if (flat[i] > bestVal) { bestVal = flat[i]; bestToken = i; }

            var durCount = flat.Length - vocabSize;
            var bestDur = 0; var bestDurVal = float.NegativeInfinity;
            for (var i = 0; i < durCount; i++)
                if (flat[vocabSize + i] > bestDurVal) { bestDurVal = flat[vocabSize + i]; bestDur = i; }

            if (bestToken != blankId)
            {
                tokens.Add(bestToken);
                frameIndices.Add(frameIndexOffset + t);
                durations.Add(bestDur);
                state.LastToken = bestToken;
                emitted++;
                state.StateH = step.StateH;
                state.StateC = step.StateC;
            }

            if (bestDur > 0)
            {
                t += bestDur;
                emitted = 0;
            }
            else if (bestToken == blankId || emitted >= MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
        }

        state.CarryAdvance = Math.Max(0, t - limit);
    }
}
```

Modify `src/Winpepper.Asr/ParakeetSession.cs`:

1. Class declaration (line 13): `public sealed class ParakeetSession : IParakeetBackend, IDisposable`
2. Below the existing private consts (lines 15–17), add the interface surface:

```csharp
    public int VocabSize => _vocab.Size;
    public int BlankId => _vocab.BlankId;
    int IParakeetBackend.DecoderHiddenLayers => DecoderHiddenLayers;
    int IParakeetBackend.DecoderHiddenDim => DecoderHiddenDim;

    public string DecodeTokens(IEnumerable<int> tokenIds) => _vocab.Decode(tokenIds);
```

3. Replace `Transcribe` (lines 93–98) with:

```csharp
    public ParakeetTranscript Transcribe(ReadOnlySpan<float> samples16k)
    {
        var features = _features.Extract(samples16k); // [T, 128]
        var enc = Encode(features);
        var state = new TdtDecoderState(DecoderHiddenLayers, DecoderHiddenDim, _vocab.BlankId);
        var tokens = new List<int>();
        var frameIndices = new List<int>();
        var durations = new List<int>();
        TdtGreedyDecoder.Decode(this, enc, state, tokens, frameIndices, durations);
        return new ParakeetTranscript(_vocab.Decode(tokens), tokens, frameIndices, durations);
    }
```

4. Replace `RunEncoder` (lines 100–132) with a public `Encode` — identical body, new signature and return type:

```csharp
    public EncoderOutput Encode(float[,] features)
    {
        var time = features.GetLength(0);
        var feat = features.GetLength(1);

        // Encoder expects [batch=1, feature_size, time].
        var input = new float[1 * feat * time];
        for (var t = 0; t < time; t++)
            for (var f = 0; f < feat; f++)
                input[f * time + t] = features[t, f];

        var audioSignal = new DenseTensor<float>(input, new[] { 1, feat, time });
        var length = new DenseTensor<long>(new long[] { time }, new[] { 1 });

        using var results = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", audioSignal),
            NamedOnnxValue.CreateFromTensor("length", length),
        });

        var outTensor = results.First(r => r.Name == "outputs").AsTensor<float>();
        var lengths   = results.First(r => r.Name == "encoded_lengths").AsTensor<long>();

        // Encoder outputs [B=1, D=1024, T'] for Parakeet TDT v3.
        var b = (int)outTensor.Dimensions[0];
        var d = (int)outTensor.Dimensions[1];
        var tprime = (int)outTensor.Dimensions[2];
        if (b != 1) throw new InvalidOperationException("Batch != 1");
        var flat = new float[d * tprime];
        var idx = 0;
        foreach (var v in outTensor) flat[idx++] = v;
        return new EncoderOutput(flat, (int)lengths[0], d, tprime);
    }
```

5. Replace the whole `GreedyDecode` method (lines 134–215) with the extracted per-frame ONNX call (note: unlike the old loop, output states are copied on every step, not just non-blank ones — 2×2×640 floats per frame, negligible next to the ONNX run itself):

```csharp
    public DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        var encFrame = new DenseTensor<float>(encoderFrame, new[] { 1, encoderFrame.Length, 1 });
        var targets = new DenseTensor<int>(new[] { lastToken }, new[] { 1, 1 });
        var targetLen = new DenseTensor<int>(new[] { 1 }, new[] { 1 });
        var sh = new DenseTensor<float>(stateH, new[] { DecoderHiddenLayers, 1, DecoderHiddenDim });
        var sc = new DenseTensor<float>(stateC, new[] { DecoderHiddenLayers, 1, DecoderHiddenDim });

        using var results = _decoderJoint.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("encoder_outputs", encFrame),
            NamedOnnxValue.CreateFromTensor("targets", targets),
            NamedOnnxValue.CreateFromTensor("target_length", targetLen),
            NamedOnnxValue.CreateFromTensor("input_states_1", sh),
            NamedOnnxValue.CreateFromTensor("input_states_2", sc),
        });

        var logits = results.First(r => r.Name == "outputs").AsTensor<float>();
        var flat = new float[logits.Length];
        var idx = 0;
        foreach (var v in logits) flat[idx++] = v;

        var newH = results.First(r => r.Name == "output_states_1").AsTensor<float>();
        var newC = results.First(r => r.Name == "output_states_2").AsTensor<float>();
        var h = new float[newH.Length]; var hi = 0; foreach (var v in newH) h[hi++] = v;
        var c = new float[newC.Length]; var ci = 0; foreach (var v in newC) c[ci++] = v;
        return new DecoderJointResult(flat, h, c);
    }
```

Also delete the now-unused `MaxTokensPerStep` const from `ParakeetSession` (line 15) — it lives on `TdtGreedyDecoder` now.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.TdtGreedyDecoderTests"
```

Expected: PASS (6 tests). Note the Windows-only `ParakeetSessionIntegrationTests` continues to pin the real-model batch path on Windows; the Linux gate proves the pure loop.

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/ParakeetBackend.cs src/Winpepper.Asr/TdtGreedyDecoder.cs \
        src/Winpepper.Asr/ParakeetSession.cs tests/Winpepper.Asr.Tests/FakeParakeetBackend.cs \
        tests/Winpepper.Asr.Tests/TdtGreedyDecoderTests.cs
git commit -m "refactor(asr): extract IParakeetBackend seam and pure TDT greedy decoder"
```

---

### Task 6: Local streaming session (chunked Parakeet inference)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs`
- Create: `src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs`

**Interfaces:**
- Consumes: `IParakeetBackend`, `TdtDecoderState`, `TdtGreedyDecoder` (Task 5); `StreamingLogMelExtractor`, `RunningMelNormalizer` (Task 4); `IStreamingTranscriptionSession`, `IStreamingTranscriber`, `TranscriptionResult` (Task 2); `PreprocessorConfig`; `FakeParakeetBackend`, `FakeTranscriber` (test doubles).
- Produces (used by Tasks 10, 11):
  - `ParakeetStreamingSession(IParakeetBackend backend, string modelName, PreprocessorConfig config, Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback, int chunkMelFrames = 200, int leftContextMelFrames = 100, ILogger? log = null) : IStreamingTranscriptionSession`
  - `ParakeetStreamingTranscriber(IParakeetBackend backend, ITranscriber batchFallback, string modelName, PreprocessorConfig config, ILogger? log = null) : IStreamingTranscriber`

Behavioral contract (documented deviations from batch, inherent to any streaming ASR):
- features normalized with RUNNING stats (batch uses whole-utterance stats);
- the encoder sees `leftContextMelFrames` of re-encoded left context and no right context;
- LEADING SILENCE IS GATED: pushed frames before the first frame whose RMS crosses the batch trimmer's absolute silence floor are skipped (never fed to the mel extractor). Parakeet-TDT deterministically deletes tokens around silence (NeMo-Speech #15757: 400 ms of trailing zeros → empty transcript; FluidAudio #746: 0.4–0.6 s leading silence → zero tokens), and the 500 ms pre-roll is mostly silence; the batch path trims first (`PipelineHost.TrimForTranscription` → `Winpepper.Audio.SilenceTrimmer.Trim`, PipelineHost.cs ~line 952) but the streaming tee otherwise would not. The residual trailing-silence risk is covered by the batch-fallback contract plus the mandatory Windows post-merge batch-vs-streamed comparison (Task 10);
- dictations shorter than one chunk never stream — `FinishAsync` takes the exact batch path, bit-identical to today;
- any streaming failure (mid-stream or at finish) falls back to the batch path over `fullAudio` — reliability never regresses.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ParakeetStreamingSessionTests
{
    private const int Hop = 160; // PreprocessorConfig.ParakeetTdtV3.HopLength

    private static float[] Audio(int samples)
    {
        var rng = new Random(3);
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = (float)(rng.NextDouble() * 0.6 - 0.3);
        return a;
    }

    private static ParakeetStreamingSession NewSession(
        FakeParakeetBackend backend,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>>? fallback = null,
        int chunk = 50, int context = 20)
        => new(
            backend, "parakeet-test", PreprocessorConfig.ParakeetTdtV3,
            fallback ?? ((_, _) => Task.FromResult(new TranscriptionResult("BATCH", "parakeet-test"))),
            chunkMelFrames: chunk, leftContextMelFrames: context);

    [Fact]
    public async Task ZeroPushedAudio_FinishUsesTheBatchFallback()
    {
        ReadOnlyMemory<float> seen = default;
        var session = NewSession(new FakeParakeetBackend(),
            (audio, _) => { seen = audio; return Task.FromResult(new TranscriptionResult("BATCH", "m")); });

        var result = await session.FinishAsync(Audio(1234), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("BATCH");
        seen.Length.ShouldBe(1234);
    }

    [Fact]
    public async Task ShortDictation_NoChunkEncoded_UsesTheBatchFallback()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 1000); // needs ~1000 mel frames per chunk
        await session.PushAsync(Audio(Hop * 100), TestContext.Current.CancellationToken); // ~100 frames

        var result = await session.FinishAsync(Audio(Hop * 100), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("BATCH");
        backend.EncodeMelFrameCounts.ShouldBeEmpty(); // nothing streamed
    }

    [Fact]
    public async Task LongDictation_EncodesChunksDuringPush_AndOnlyTheTailAtFinish()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // ~120 mel frames of audio → two 50-frame chunks encode during push.
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        backend.EncodeMelFrameCounts.Count.ShouldBe(2);
        backend.EncodeMelFrameCounts[0].ShouldBe(50);        // first chunk: no context yet
        backend.EncodeMelFrameCounts[1].ShouldBe(20 + 50);   // context + chunk

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        backend.EncodeMelFrameCounts.Count.ShouldBe(3);      // exactly one tail encode
        backend.EncodeMelFrameCounts[2].ShouldBeLessThan(20 + 50); // tail is smaller than a full chunk
        result.ProviderModelName.ShouldBe("parakeet-test");
    }

    [Fact]
    public async Task DecoderState_CarriesAcrossChunks()
    {
        var backend0 = new FakeParakeetBackend();
        FakeParakeetBackend backend = null!;
        backend = new FakeParakeetBackend
        {
            // Emit token 2 once per segment start, then blanks — LastToken should
            // stay 2 across chunk boundaries.
            Joint = (frame, last) => last == backend.BlankId
                ? backend0.Emit(2, 1)
                : backend0.Emit(backend.BlankId, 1),
        };
        var session = NewSession(backend, chunk: 50, context: 0);
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);

        // After the first emission every later joint call must see LastToken == 2.
        backend.JointCalls.Skip(1).ShouldAllBe(call => call.LastToken == 2);
    }

    [Fact]
    public async Task LeadingSilence_IsGated_NotFedToTheEncoder()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        // ~60 mel frames of pure silence, then ~60 frames of speech-level audio.
        await session.PushAsync(new float[Hop * 60], TestContext.Current.CancellationToken);
        await session.PushAsync(Audio(Hop * 60), TestContext.Current.CancellationToken);

        // Ungated, ~120 frames would have produced two 50-frame chunk encodes;
        // gated, only the ~60 post-onset frames exist -> exactly one encode.
        backend.EncodeMelFrameCounts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task MidStreamEncoderFailure_FallsBackToBatchAtFinish()
    {
        var calls = 0;
        var backend = new FakeParakeetBackend
        {
            OnEncode = _ => { if (++calls == 2) throw new InvalidOperationException("onnx died"); },
        };
        var session = NewSession(backend, chunk: 50, context: 0);
        await session.PushAsync(Audio(Hop * 120), TestContext.Current.CancellationToken); // 2nd chunk throws inside

        var result = await session.FinishAsync(Audio(Hop * 120), TestContext.Current.CancellationToken);
        result.Text.ShouldBe("BATCH");
    }

    [Fact]
    public async Task Transcriber_StartsAFreshSessionPerDictation()
    {
        var backend = new FakeParakeetBackend();
        var transcriber = new ParakeetStreamingTranscriber(
            backend, FakeTranscriber.Returning("parakeet-test", "BATCH"),
            "parakeet-test", PreprocessorConfig.ParakeetTdtV3);
        transcriber.ModelName.ShouldBe("parakeet-test");

        await using var s1 = await transcriber.StartSessionAsync(TestContext.Current.CancellationToken);
        await using var s2 = await transcriber.StartSessionAsync(TestContext.Current.CancellationToken);
        s1.ShouldNotBeSameAs(s2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `ParakeetStreamingSession`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Streaming local transcription. Log-mel frames are computed incrementally as
/// audio arrives; every chunkMelFrames of NEW frames the encoder runs over
/// [left context + chunk] (running-stats normalization), the context's encoder
/// frames are discarded, and the greedy TDT decoder consumes the rest with its
/// state carried across chunks. At stop time only the tail remains, so post-stop
/// latency ≈ cost(tail) instead of cost(whole recording).
///
/// Deliberate deviations from batch (inherent to streaming ASR): running-stats
/// normalization instead of whole-utterance stats, and limited left / no right
/// encoder context. Dictations shorter than one chunk never stream — FinishAsync
/// takes the exact batch path via <c>batchFallback</c>, and ANY streaming failure
/// also lands on <c>batchFallback(fullAudio)</c>, so reliability never regresses.
/// </summary>
public sealed class ParakeetStreamingSession : IStreamingTranscriptionSession
{
    private readonly IParakeetBackend _backend;
    private readonly string _modelName;
    private readonly PreprocessorConfig _config;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> _batchFallback;
    private readonly int _chunkMelFrames;
    private readonly int _leftContextMelFrames;
    private readonly ILogger? _log;

    private readonly StreamingLogMelExtractor _mel;
    private readonly RunningMelNormalizer _normalizer;
    private readonly List<double[]> _pending = new(); // log-mel frames not yet encoded
    private readonly List<double[]> _context = new(); // trailing already-encoded frames
    private readonly TdtDecoderState _state;
    private readonly List<int> _tokens = new();
    private readonly List<int> _frameIndices = new();
    private readonly List<int> _durations = new();
    private int _globalEncFrames;
    private int? _subsamplingFactor; // derived from the first encode's actual output
    private bool _speechSeen;        // leading-silence gate latch
    private bool _streamed;
    private bool _corrupt;

    /// <summary>
    /// Frame-RMS floor for the leading-silence gate. Mirrors the batch trimmer's
    /// absolute silence floor (Winpepper.Audio.SilenceTrimmer.ThresholdAbsFloor,
    /// 0.002 — duplicated here because Winpepper.Asr does not reference
    /// Winpepper.Audio). The batch path trims silence before ASR
    /// (PipelineHost.TrimForTranscription → SilenceTrimmer.Trim); the streaming
    /// path must gate leading silence too, because Parakeet-TDT deterministically
    /// deletes tokens around silence (NeMo-Speech #15757; FluidAudio #746) and
    /// the 500 ms pre-roll is mostly silence.
    /// </summary>
    private const double LeadingSilenceRmsFloor = 0.002;

    public ParakeetStreamingSession(
        IParakeetBackend backend,
        string modelName,
        PreprocessorConfig config,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback,
        int chunkMelFrames = 200,        // 2 s of audio (100 mel frames/s at hop 160) — small
                                         // chunks keep the post-stop TAIL small; the extra
                                         // context re-encoding all happens during recording
        int leftContextMelFrames = 100,  // 1 s of context re-encoded per chunk
        ILogger? log = null)
    {
        _backend = backend;
        _modelName = modelName;
        _config = config;
        _batchFallback = batchFallback;
        _chunkMelFrames = chunkMelFrames;
        _leftContextMelFrames = leftContextMelFrames;
        _log = log;
        _mel = new StreamingLogMelExtractor(config);
        _normalizer = new RunningMelNormalizer(config.FeatureSize);
        _state = new TdtDecoderState(backend.DecoderHiddenLayers, backend.DecoderHiddenDim, backend.BlankId);
    }

    public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (_corrupt) return ValueTask.CompletedTask;
        if (!_speechSeen)
        {
            // Leading-silence gate: skip whole pushed frames (never fed to the
            // mel extractor — they would also pollute the running normalizer's
            // stats) until the first frame with speech-level energy.
            if (Rms(mono16k.Span) < LeadingSilenceRmsFloor) return ValueTask.CompletedTask;
            _speechSeen = true;
        }
        try
        {
            _mel.Push(mono16k.Span);
            _mel.Drain(_pending);
            while (_pending.Count >= _chunkMelFrames)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = _pending.GetRange(0, _chunkMelFrames);
                _pending.RemoveRange(0, _chunkMelFrames);
                EncodeAndDecode(chunk);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _corrupt = true;
            _log?.LogWarning(ex, "streaming local ASR failed mid-dictation; will batch-transcribe at stop");
        }
        return ValueTask.CompletedTask;
    }

    public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        if (_corrupt || !_streamed)
            return await _batchFallback(fullAudio, ct);
        try
        {
            return await Task.Run(() =>
            {
                _mel.Finish();
                _mel.Drain(_pending);
                if (_pending.Count > 0)
                {
                    var tail = new List<double[]>(_pending);
                    _pending.Clear();
                    EncodeAndDecode(tail);
                }
                return new TranscriptionResult(_backend.DecodeTokens(_tokens), _modelName);
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "streaming local ASR failed at stop; batch-transcribing the full buffer");
            return await _batchFallback(fullAudio, ct);
        }
    }

    private void EncodeAndDecode(List<double[]> chunk)
    {
        _normalizer.Add(chunk);
        var withContext = new List<double[]>(_context.Count + chunk.Count);
        withContext.AddRange(_context);
        withContext.AddRange(chunk);

        var features = _normalizer.Normalize(withContext); // [ctx+chunk, FeatureSize]
        var enc = _backend.Encode(features);

        // Discard the context's encoder frames using the encoder's EXACT
        // output-length function: for input length T this export family produces
        // floor((T-1)/F) + 1 frames (F = subsampling factor; 8 for Parakeet-TDT,
        // per onnx-asr's nemo.py). F is derived once from the first encode's
        // actual output — never hardcoded — and re-asserted on every encode.
        // A proportional Math.Round diverges at banker's-rounding midpoints
        // (e.g. ctx=100, tail=4: round(12.5) = 12 vs the exact 13), which would
        // double-decode a boundary frame; the exact form eliminates the class.
        _subsamplingFactor ??= (withContext.Count - 1) / Math.Max(1, enc.Frames - 1);
        var factor = _subsamplingFactor.Value;
        if ((withContext.Count - 1) / factor + 1 != enc.Frames)
            throw new InvalidOperationException(
                $"encoder output length {enc.Frames} != floor((T-1)/{factor})+1 for T={withContext.Count}");
        var discard = _context.Count == 0 ? 0 : (_context.Count - 1) / factor + 1;

        TdtGreedyDecoder.Decode(_backend, enc, _state, _tokens, _frameIndices, _durations,
            startFrame: discard, frameIndexOffset: _globalEncFrames - discard);
        _globalEncFrames += enc.Frames - discard;
        _streamed = true;

        _context.AddRange(chunk);
        if (_context.Count > _leftContextMelFrames)
            _context.RemoveRange(0, _context.Count - _leftContextMelFrames);
        if (_leftContextMelFrames == 0) _context.Clear();
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0.0;
        var sum = 0.0;
        foreach (var s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Create `src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>Starts one ParakeetStreamingSession per dictation over the shared
/// local backend (ParakeetSession implements IParakeetBackend).</summary>
public sealed class ParakeetStreamingTranscriber : IStreamingTranscriber
{
    private readonly IParakeetBackend _backend;
    private readonly ITranscriber _batchFallback;
    private readonly PreprocessorConfig _config;
    private readonly ILogger? _log;

    public ParakeetStreamingTranscriber(
        IParakeetBackend backend,
        ITranscriber batchFallback,
        string modelName,
        PreprocessorConfig config,
        ILogger? log = null)
    {
        _backend = backend;
        _batchFallback = batchFallback;
        ModelName = modelName;
        _config = config;
        _log = log;
    }

    public string ModelName { get; }

    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(new ParakeetStreamingSession(
            _backend, ModelName, _config,
            (audio, ct2) => _batchFallback.TranscribeAsync(audio, ct2),
            log: _log));
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.ParakeetStreamingSessionTests"
```

Expected: PASS (7 tests). If `LongDictation_...` sees a different chunk count, recompute: `Hop * 120` samples produce `120 + 1` mel frames total, but mid-stream only frames with full right context (~119) are drained during the push — two 50-frame chunks encode, ~19–21 frames remain for the tail. (The other tests' synthetic `Audio()` has speech-level energy from sample 0 — RMS ≈ 0.17, far above the 0.002 gate floor — so the leading-silence gate never triggers in them; only `LeadingSilence_IsGated_...` exercises it.)

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs \
        src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs
git commit -m "feat(asr): chunked streaming inference for the local Parakeet path"
```

---

### Task 7: AssemblyAI Universal-Streaming client (remote path)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/Pcm16.cs`
- Modify: `src/Winpepper.Asr/Transcription/PcmWavEncoder.cs` (reuse `Pcm16.SampleToPcm16` — DRY)
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs` (add `StreamingBaseUrl`)
- Create: `src/Winpepper.Asr/Transcription/StreamingWebSocket.cs` (`IStreamingWebSocket` + real `ClientStreamingWebSocket`)
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiStreamingTranscriber.cs` (transcriber + session)
- Create: `tests/Winpepper.Asr.Tests/FakeStreamingWebSocket.cs`
- Test: `tests/Winpepper.Asr.Tests/Pcm16Tests.cs`, `tests/Winpepper.Asr.Tests/AssemblyAiStreamingTests.cs`

**Interfaces:**
- Consumes: `IStreamingTranscriber`/`IStreamingTranscriptionSession` (Task 2), `ITranscriber` (the injected cloud REST batch fallback; `AssemblyAiTranscriber` in production, `FakeTranscriber` in tests), `IAssemblyAiKeyStore` (`HasKey`, `Load()`), `AssemblyAiOptions`, `AssemblyAiException`, `TranscriptionResult`.
- Produces (used by Tasks 8, 10, 11):
  - `static class Pcm16 { static short SampleToPcm16(float sample); static byte[] FromFloats(ReadOnlySpan<float> samples); }`
  - `interface IStreamingWebSocket : IAsyncDisposable { Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct); Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct); Task SendTextAsync(string json, CancellationToken ct); Task<string?> ReceiveTextAsync(CancellationToken ct); }`
  - `ClientStreamingWebSocket : IStreamingWebSocket`
  - `AssemblyAiStreamingTranscriber(Func<IStreamingWebSocket> socketFactory, ITranscriber batchFallback, IAssemblyAiKeyStore keyStore, AssemblyAiOptions options, ILogger<AssemblyAiStreamingTranscriber> logger) : IStreamingTranscriber` with `ModelName == "assemblyai/universal-streaming"`. `batchFallback` is the existing cloud REST batch path (`AssemblyAiTranscriber` in production) used when a session finishes with ZERO pushed samples — see the A9 note in Protocol facts below.
  - `AssemblyAiStreamingSession(IStreamingWebSocket socket, string modelName, Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback, ILogger log) : IStreamingTranscriptionSession` (public ctor for tests)
  - `AssemblyAiOptions.StreamingBaseUrl` (default `"wss://streaming.assemblyai.com"`)
  - test double `FakeStreamingWebSocket`

Protocol facts (AssemblyAI Universal-Streaming v3): connect to `{StreamingBaseUrl}/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true` with the raw API key in the `Authorization` header. Send binary PCM16LE messages of 50–1000 ms. Server sends JSON text messages: `{"type":"Begin",...}`, `{"type":"Turn","turn_order":N,"end_of_turn":bool,"turn_is_formatted":bool,"transcript":"..."}` (repeatedly, transcript grows; a formatted final Turn re-delivers the same `turn_order`), and after the client sends `{"type":"Terminate"}` the server flushes any pending audio (possibly one more Turn) then sends `{"type":"Termination"}` and closes (the receive loop also tolerates the legacy `"SessionTerminated"` name defensively). Final transcript = the latest `transcript` per `turn_order`, joined in `turn_order` order. Errors arrive as `{"type":"Error","error":"..."}`; a socket close WITHOUT a prior `Termination` or `Error` is abnormal and must be surfaced as an error too (so the fallback wrapper engages instead of returning a silently truncated transcript).

A9 — the streaming socket is NOT a batch-equivalent path: the server throttles ingest to ~1.25× realtime and kills sessions with error `3007` once too much audio is buffered ahead of processing, so "burst the whole buffer then Terminate" is both slower than the REST batch API and not completeness-guaranteed. Terminate's tail flush of the ≤1 s in-flight remainder IS documented and is retained. Therefore `FinishAsync` with ZERO pushed samples (a session that only materialized at stop time) delegates to the injected cloud batch REST fallback (`AssemblyAiTranscriber`) — behavior identical to today for late-materialized sessions.

Keyterms/corrections: `custom_spelling` is batch-only, but `keyterms_prompt` IS supported on Universal-Streaming v3 — as a connection query param whose value is a single JSON-encoded array, or mid-stream via an `UpdateConfiguration` message (≤100 terms, ≤50 chars each, extra cost ~$0.04/hr). DECISION: wiring keyterms into the streaming connect is DEFERRED — user corrections remain guaranteed by the deterministic corrections pass in cleanup for cloud results (see `CloudProvider` doc), and the zero-pushed REST fallback still applies them via its extras provider. Hazard for whoever picks it up: unrecognized/malformed query params are SILENTLY IGNORED by the server — verify the setting took via the `Begin` message's configuration echo. Also note: the user's `AssemblyAiModel` setting does not apply to v3 streaming (the model is fixed — universal-streaming); `speech_model`-style query support may be added later if the API offers it.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/Pcm16Tests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class Pcm16Tests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, short.MaxValue)]
    [InlineData(-1f, short.MinValue)]
    [InlineData(2f, short.MaxValue)]    // clamped
    [InlineData(-2f, short.MinValue)]   // clamped
    public void SampleToPcm16_ConvertsAndClamps(float input, short expected)
        => Pcm16.SampleToPcm16(input).ShouldBe(expected);

    [Fact]
    public void FromFloats_ProducesLittleEndianPairs()
    {
        var bytes = Pcm16.FromFloats(new[] { 0f, 1f });
        bytes.ShouldBe(new byte[] { 0x00, 0x00, 0xFF, 0x7F });
    }

    [Fact]
    public void FromFloats_MatchesTheWavEncoderDataSection()
    {
        var samples = new[] { 0.5f, -0.25f, 0.99f };
        var wav = PcmWavEncoder.EncodeMono16k(samples);
        var raw = Pcm16.FromFloats(samples);
        wav.Skip(44).ToArray().ShouldBe(raw); // WAV header is 44 bytes
    }
}
```

Create `tests/Winpepper.Asr.Tests/FakeStreamingWebSocket.cs`:

```csharp
using System.Threading.Channels;
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Scripted WebSocket double: records sends, replays queued server messages.</summary>
public sealed class FakeStreamingWebSocket : IStreamingWebSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public Uri? ConnectedUri { get; private set; }
    public string? ApiKey { get; private set; }
    public List<byte[]> BinaryFrames { get; } = new();
    public List<string> TextFrames { get; } = new();
    public Exception? ThrowOnConnect { get; set; }
    public Exception? ThrowOnSendBinary { get; set; }

    /// <summary>When true (default), a Terminate send auto-queues the server's termination reply.</summary>
    public bool AutoTerminate { get; set; } = true;

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        if (ThrowOnConnect is not null) throw ThrowOnConnect;
        ConnectedUri = uri;
        ApiKey = apiKey;
        return Task.CompletedTask;
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        if (ThrowOnSendBinary is not null) throw ThrowOnSendBinary;
        BinaryFrames.Add(audio.ToArray());
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string json, CancellationToken ct)
    {
        TextFrames.Add(json);
        if (AutoTerminate && json.Contains("Terminate"))
            _incoming.Writer.TryWrite("{\"type\":\"Termination\",\"audio_duration_seconds\":1}");
        return Task.CompletedTask;
    }

    public void EnqueueServerMessage(string json) => _incoming.Writer.TryWrite(json);
    public void CloseFromServer() => _incoming.Writer.TryWrite(null);

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
        => await _incoming.Reader.ReadAsync(ct);

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryWrite(null); // unblock a pending receive
        return ValueTask.CompletedTask;
    }
}
```

Create `tests/Winpepper.Asr.Tests/AssemblyAiStreamingTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class AssemblyAiStreamingTests
{
    private sealed class StubKeyStore : IAssemblyAiKeyStore
    {
        private readonly string? _key;
        public StubKeyStore(string? key) => _key = key;
        public bool HasKey => _key is not null;
        public void Save(string apiKey) { }
        public string? Load() => _key;
        public void Clear() { }
    }

    private static AssemblyAiStreamingTranscriber NewTranscriber(
        FakeStreamingWebSocket socket, string? key = "k-123", ITranscriber? batchFallback = null)
        => new(() => socket,
            batchFallback ?? FakeTranscriber.Returning("assemblyai/slam-1", "BATCH-REST"),
            new StubKeyStore(key), new AssemblyAiOptions(),
            NullLogger<AssemblyAiStreamingTranscriber>.Instance);

    [Fact]
    public async Task Start_ConnectsWithKeyAndStreamingUri()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        socket.ApiKey.ShouldBe("k-123");
        socket.ConnectedUri!.ToString().ShouldBe(
            "wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true");
    }

    [Fact]
    public async Task Start_WithoutKey_ThrowsAuthError()
    {
        var ex = await Should.ThrowAsync<AssemblyAiException>(
            () => NewTranscriber(new FakeStreamingWebSocket(), key: null)
                .StartSessionAsync(TestContext.Current.CancellationToken));
        ex.IsAuthError.ShouldBeTrue();
    }

    [Fact]
    public async Task Push_CoalescesToAtLeast50MsBinaryMessages()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);

        await session.PushAsync(new float[400], TestContext.Current.CancellationToken); // 25 ms — buffered
        socket.BinaryFrames.ShouldBeEmpty();
        await session.PushAsync(new float[400], TestContext.Current.CancellationToken); // now 50 ms
        socket.BinaryFrames.Count.ShouldBe(1);
        socket.BinaryFrames[0].Length.ShouldBe(1600); // 800 samples * 2 bytes
    }

    [Fact]
    public async Task Finish_SendsTerminate_AndAssemblesTurnsInOrder()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);

        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":0,\"end_of_turn\":true,\"turn_is_formatted\":false,\"transcript\":\"hello world\"}");
        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":0,\"end_of_turn\":true,\"turn_is_formatted\":true,\"transcript\":\"Hello, world.\"}");
        socket.EnqueueServerMessage("{\"type\":\"Turn\",\"turn_order\":1,\"end_of_turn\":true,\"turn_is_formatted\":true,\"transcript\":\"Second turn.\"}");

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        socket.TextFrames.ShouldContain(t => t.Contains("\"Terminate\""));
        result.Text.ShouldBe("Hello, world. Second turn."); // formatted replaces unformatted; ordered by turn_order
        result.ProviderModelName.ShouldBe("assemblyai/universal-streaming");
    }

    [Fact]
    public async Task Finish_WithZeroPushedAudio_DelegatesToTheBatchFallback()
    {
        // A9: bursting the buffer over the socket is throttled to ~1.25x realtime
        // server-side (and can be killed with error 3007), so the zero-pushed
        // path must delegate to the cloud batch REST transcriber instead.
        ReadOnlyMemory<float> seen = default;
        var fallback = new CapturingBatchTranscriber(m => seen = m);
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket, batchFallback: fallback)
            .StartSessionAsync(TestContext.Current.CancellationToken);

        var result = await session.FinishAsync(new float[40000], TestContext.Current.CancellationToken); // 2.5 s

        result.Text.ShouldBe("BATCH-REST");
        seen.Length.ShouldBe(40000);            // the fallback got the whole buffer
        fallback.Calls.ShouldBe(1);
        socket.BinaryFrames.ShouldBeEmpty();    // nothing was burst over the socket
    }

    [Fact]
    public async Task UnexpectedSocketClose_WithoutTermination_SurfacesAsErrorAtFinish()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);

        socket.CloseFromServer(); // closes WITHOUT a prior Termination or Error

        // Give the receive loop a beat to consume the close, then finish.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<AssemblyAiException>(
            () => session.FinishAsync(new float[800], TestContext.Current.CancellationToken));
    }

    /// <summary>Records the buffer handed to the batch fallback.</summary>
    private sealed class CapturingBatchTranscriber : ITranscriber
    {
        private readonly Action<ReadOnlyMemory<float>> _capture;
        public int Calls { get; private set; }
        public CapturingBatchTranscriber(Action<ReadOnlyMemory<float>> capture) => _capture = capture;
        public string ModelName => "assemblyai/slam-1";
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { Calls++; _capture(mono16k); return Task.FromResult(new TranscriptionResult("BATCH-REST", ModelName)); }
    }

    [Fact]
    public async Task ServerError_SurfacesAsAssemblyAiExceptionAtFinish()
    {
        var socket = new FakeStreamingWebSocket();
        await using var session = await NewTranscriber(socket).StartSessionAsync(TestContext.Current.CancellationToken);
        socket.EnqueueServerMessage("{\"type\":\"Error\",\"error\":\"bad audio\"}");

        // Give the receive loop a beat to consume the error, then finish.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<AssemblyAiException>(
            () => session.FinishAsync(new float[800], TestContext.Current.CancellationToken));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `Pcm16` / `IStreamingWebSocket`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/Pcm16.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Float [-1,+1] → PCM16LE conversion shared by the WAV encoder (batch
/// upload) and the streaming WebSocket (raw binary frames).</summary>
public static class Pcm16
{
    public static short SampleToPcm16(float sample)
    {
        var clamped = Math.Clamp(sample, -1.0f, 1.0f);
        return clamped >= 0f
            ? (short)Math.Round(clamped * short.MaxValue)
            : (short)Math.Round(clamped * -(double)short.MinValue);
    }

    public static byte[] FromFloats(ReadOnlySpan<float> samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var pcm = SampleToPcm16(samples[i]);
            bytes[i * 2] = (byte)(pcm & 0xFF);
            bytes[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }
        return bytes;
    }
}
```

In `src/Winpepper.Asr/Transcription/PcmWavEncoder.cs`, replace the per-sample conversion loop:

```csharp
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1.0f, 1.0f);
            short pcm;
            if (clamped >= 0f) pcm = (short)Math.Round(clamped * short.MaxValue);
            else pcm = (short)Math.Round(clamped * -(double)short.MinValue);
            w.Write(pcm);
        }
```

with:

```csharp
        foreach (var s in samples)
            w.Write(Pcm16.SampleToPcm16(s)); // BinaryWriter is little-endian, same as Pcm16.FromFloats
```

In `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`, add below `BaseUrl` (line 5):

```csharp
    public string StreamingBaseUrl { get; init; } = "wss://streaming.assemblyai.com";
```

Create `src/Winpepper.Asr/Transcription/StreamingWebSocket.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>Thin seam over ClientWebSocket so AssemblyAiStreamingSession is testable.</summary>
public interface IStreamingWebSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct);
    Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct);
    Task SendTextAsync(string json, CancellationToken ct);

    /// <summary>Next complete text message, or null when the server closed the socket.</summary>
    Task<string?> ReceiveTextAsync(CancellationToken ct);
}

/// <summary>Real WebSocket. Network-facing; exercised by the latency benchmark's
/// real-remote scenario and by production — unit tests use the fake.</summary>
public sealed class ClientStreamingWebSocket : IStreamingWebSocket
{
    private readonly ClientWebSocket _ws = new();

    public Task ConnectAsync(Uri uri, string apiKey, CancellationToken ct)
    {
        _ws.Options.SetRequestHeader("Authorization", apiKey); // raw key — no Bearer prefix
        return _ws.ConnectAsync(uri, ct);
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> audio, CancellationToken ct)
        => _ws.SendAsync(audio, WebSocketMessageType.Binary, endOfMessage: true, ct).AsTask();

    public Task SendTextAsync(string json, CancellationToken ct)
        => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct).AsTask();

    public async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closeCts.Token);
            }
        }
        catch { /* best-effort close */ }
        _ws.Dispose();
    }
}
```

Create `src/Winpepper.Asr/Transcription/AssemblyAiStreamingTranscriber.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI Universal-Streaming (v3 WebSocket) transcriber. Audio streams as
/// raw PCM16LE binary messages while the user is still speaking; on stop a
/// Terminate message flushes the session and the final transcript is assembled
/// from the latest transcript per turn_order. A session that finishes with ZERO
/// pushed samples delegates to <paramref name="batchFallback"/> (the cloud REST
/// batch path) instead of bursting the buffer over the socket — the server
/// throttles ingest to ~1.25x realtime and errors (3007) past a buffered
/// backlog, so bursting is slower than REST and not completeness-guaranteed.
/// Never logs the API key. FallbackStreamingTranscriber (Task 8) owns
/// retries/local fallback — failures here throw AssemblyAiException.
/// </summary>
public sealed class AssemblyAiStreamingTranscriber : IStreamingTranscriber
{
    private readonly Func<IStreamingWebSocket> _socketFactory;
    private readonly ITranscriber _batchFallback; // cloud REST batch (AssemblyAiTranscriber)
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger _log;

    public AssemblyAiStreamingTranscriber(
        Func<IStreamingWebSocket> socketFactory,
        ITranscriber batchFallback,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiStreamingTranscriber> logger)
    {
        _socketFactory = socketFactory;
        _batchFallback = batchFallback;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
    }

    public string ModelName => "assemblyai/universal-streaming";

    public async Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);
        var key = _keyStore.Load()
            ?? throw new AssemblyAiException("AssemblyAI API key unreadable.", isAuthError: true);

        var socket = _socketFactory();
        var uri = new Uri($"{_opts.StreamingBaseUrl}/v3/ws?sample_rate=16000&encoding=pcm_s16le&format_turns=true");
        await socket.ConnectAsync(uri, key, ct);
        _log.LogInformation("AssemblyAI streaming session connected");
        return new AssemblyAiStreamingSession(
            socket, ModelName,
            (audio, ct2) => _batchFallback.TranscribeAsync(audio, ct2),
            _log);
    }
}

public sealed class AssemblyAiStreamingSession : IStreamingTranscriptionSession
{
    private const int MinSendSamples = 800;      // 50 ms at 16 kHz — the API's minimum message

    private readonly IStreamingWebSocket _socket;
    private readonly string _modelName;
    private readonly Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> _batchFallback;
    private readonly ILogger _log;
    private readonly Task _receiveLoop;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly object _turnLock = new();
    private readonly SortedDictionary<int, string> _turns = new(); // turn_order → latest transcript
    private readonly TaskCompletionSource _terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<float> _sendBuffer = new();
    private long _pushedSamples;
    private bool _sawTermination; // written only by the receive loop
    private volatile Exception? _serverError;

    public AssemblyAiStreamingSession(
        IStreamingWebSocket socket,
        string modelName,
        Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback,
        ILogger log)
    {
        _socket = socket;
        _modelName = modelName;
        _batchFallback = batchFallback;
        _log = log;
        _receiveLoop = Task.Run(ReceiveLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var json = await _socket.ReceiveTextAsync(_loopCts.Token);
                if (json is null)
                {
                    // Socket closed. Without a prior Termination (or Error) this
                    // is an ABNORMAL close — surface it so the fallback wrapper
                    // engages instead of returning a truncated transcript.
                    if (!_sawTermination && _serverError is null)
                        _serverError = new AssemblyAiException(
                            "AssemblyAI streaming connection closed unexpectedly.");
                    return;
                }
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "Turn":
                    {
                        var order = root.TryGetProperty("turn_order", out var o) ? o.GetInt32() : 0;
                        var transcript = root.TryGetProperty("transcript", out var tr)
                            ? tr.GetString() ?? "" : "";
                        lock (_turnLock) _turns[order] = transcript;
                        break;
                    }
                    case "Termination":
                    case "SessionTerminated": // legacy name, tolerated defensively
                        _sawTermination = true;
                        return;
                    case "Error":
                    {
                        var msg = root.TryGetProperty("error", out var e) ? e.GetString() : json;
                        _serverError = new AssemblyAiException($"AssemblyAI streaming error: {msg}");
                        return;
                    }
                    default:
                        break; // Begin & friends
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _serverError = ex is AssemblyAiException ? ex
                : new AssemblyAiException("AssemblyAI streaming receive failed.", inner: ex);
        }
        finally
        {
            _terminated.TrySetResult();
        }
    }

    public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (_serverError is not null) return ValueTask.FromException(_serverError);
        BufferSamples(mono16k.Span);
        _pushedSamples += mono16k.Length;
        if (_sendBuffer.Count < MinSendSamples) return ValueTask.CompletedTask;
        var chunk = _sendBuffer.ToArray();
        _sendBuffer.Clear();
        return new ValueTask(_socket.SendBinaryAsync(Pcm16.FromFloats(chunk), ct));
    }

    private void BufferSamples(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples) _sendBuffer.Add(s);
    }

    public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        ThrowIfFailed();

        if (_pushedSamples == 0)
        {
            // Session materialized only at stop time (nothing streamed). Bursting
            // the whole buffer over the socket is NOT batch-equivalent: the server
            // throttles ingest to ~1.25x realtime and errors (3007) past a
            // buffered backlog, so it is both slower than REST and not
            // completeness-guaranteed. Delegate to the cloud batch REST path —
            // behavior identical to today for late-materialized sessions.
            return await _batchFallback(fullAudio, ct);
        }

        if (_sendBuffer.Count > 0)
        {
            // Terminate's tail flush of the <=1 s in-flight remainder IS
            // documented — only this residual is sent here.
            var tail = _sendBuffer.ToArray();
            _sendBuffer.Clear();
            await SendPadded(tail, ct);
        }

        await _socket.SendTextAsync("{\"type\":\"Terminate\"}", ct);
        await _terminated.Task.WaitAsync(ct);
        ThrowIfFailed();

        string text;
        int turnCount;
        lock (_turnLock)
        {
            turnCount = _turns.Count;
            text = string.Join(" ", _turns.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
        }
        _log.LogInformation("AssemblyAI streaming session finished ({Turns} turns)", turnCount);
        return new TranscriptionResult(text.Trim(), _modelName);
    }

    private Task SendPadded(ReadOnlyMemory<float> samples, CancellationToken ct)
    {
        // Messages under 50 ms are rejected; zero-pad the final sliver.
        if (samples.Length >= MinSendSamples)
            return _socket.SendBinaryAsync(Pcm16.FromFloats(samples.Span), ct);
        var padded = new float[MinSendSamples];
        samples.Span.CopyTo(padded);
        return _socket.SendBinaryAsync(Pcm16.FromFloats(padded), ct);
    }

    private void ThrowIfFailed()
    {
        if (_serverError is not null) throw _serverError;
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try { await _socket.DisposeAsync(); } catch { /* best-effort */ }
        try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _loopCts.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -class "Winpepper.Asr.Tests.Pcm16Tests" -class "Winpepper.Asr.Tests.AssemblyAiStreamingTests"
```

Expected: PASS (11 tests). If `ServerError_...` or `UnexpectedSocketClose_...` is flaky, replace the `Task.Delay(50, ...)` with a poll loop waiting until the session observes the error (up to 2 s) — never leave a raw sleep race in place.

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/Pcm16.cs src/Winpepper.Asr/Transcription/PcmWavEncoder.cs \
        src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs \
        src/Winpepper.Asr/Transcription/StreamingWebSocket.cs \
        src/Winpepper.Asr/Transcription/AssemblyAiStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/FakeStreamingWebSocket.cs tests/Winpepper.Asr.Tests/Pcm16Tests.cs \
        tests/Winpepper.Asr.Tests/AssemblyAiStreamingTests.cs
git commit -m "feat(asr): AssemblyAI Universal-Streaming v3 websocket transcriber"
```

---

### Task 8: FallbackStreamingTranscriber (cloud primary, local safety net)

Streaming analog of `FallbackTranscriber` (`src/Winpepper.Asr/Transcription/FallbackTranscriber.cs`) with identical policy: user cancellation rethrows; ANY other failure (connect, mid-stream push, finish, or the owned cloud deadline on the post-stop wait) lands on local batch transcription of the full buffer; invalid-model 400s additionally raise the config error. (Distinct from Task 7's zero-pushed cloud-REST delegation, which happens INSIDE `AssemblyAiStreamingSession.FinishAsync` under this wrapper's deadline — this wrapper's LOCAL safety-net policy is unchanged.)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeStreamingTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: `IStreamingTranscriber`/`IStreamingTranscriptionSession` (Task 2), `ITranscriber`, `AssemblyAiException`, `AssemblyAiErrors.IsInvalidModel`, `FakeTranscriber`.
- Produces (used by Task 10):
  - `FallbackStreamingTranscriber(IStreamingTranscriber primary, ITranscriber local, ILogger<FallbackStreamingTranscriber> logger, Action<string>? onFallback = null, TimeSpan? cloudDeadline = null, Action<string>? onConfigError = null, Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null) : IStreamingTranscriber`
  - test double `FakeStreamingTranscriber`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/FakeStreamingTranscriber.cs`:

```csharp
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
```

Create `tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class FallbackStreamingTranscriberTests
{
    private static FallbackStreamingTranscriber Wrap(
        FakeStreamingTranscriber primary, FakeTranscriber local,
        Action<string>? onFallback = null, Action<string>? onConfigError = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
        => new(primary, local, NullLogger<FallbackStreamingTranscriber>.Instance,
            onFallback: onFallback, cloudDeadline: TimeSpan.FromSeconds(10),
            onConfigError: onConfigError,
            scheduleDeadline: scheduleDeadline ?? ((_, _) => { }));

    [Fact]
    public async Task HappyPath_ReturnsTheCloudResult()
    {
        var primary = new FakeStreamingTranscriber("assemblyai/universal-streaming");
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);
        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("CLOUD");
        local.Calls.ShouldBe(0);
        f.ModelName.ShouldBe("assemblyai/universal-streaming");
    }

    [Fact]
    public async Task StartFailure_FallsBackToLocalAtFinish()
    {
        string? notice = null;
        var primary = new FakeStreamingTranscriber("cloud") { ThrowOnStart = new AssemblyAiException("connect refused") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, onFallback: n => notice = n);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken); // must NOT throw
        var result = await session.FinishAsync(new float[100], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("LOCAL");
        local.Calls.ShouldBe(1);
        notice.ShouldNotBeNull();
    }

    [Fact]
    public async Task MidStreamPushFailure_IsSwallowed_AndLocalRunsAtFinish()
    {
        var primary = new FakeStreamingTranscriber("cloud") { ThrowOnPush = new AssemblyAiException("socket died") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken); // must NOT throw
        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task FinishFailure_FallsBackToLocal()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = (_, _) => throw new AssemblyAiException("processing failed") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task CloudDeadline_FiresOnThePostStopWait_ThenLocalRuns()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        {
            OnFinish = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); },
        };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, scheduleDeadline: (cts, _) => cts.Cancel()); // deadline fires immediately

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task UserCancellation_Rethrows_WithoutRunningLocal()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); } };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        using var userCts = new CancellationTokenSource();
        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        var finish = session.FinishAsync(new float[800], userCts.Token);
        userCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => finish);
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidModel400_RaisesConfigError_AndFallsBack()
    {
        string? configError = null;
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = (_, _) => throw new AssemblyAiException("unsupported model", statusCode: 400) };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, onConfigError: msg => configError = msg);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
        configError.ShouldNotBeNull();
    }

    private sealed class UnreachableException : Exception { }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `FallbackStreamingTranscriber`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Streaming analog of <see cref="FallbackTranscriber"/>: streams to the cloud
/// during recording; on ANY non-user-cancellation failure (connect, mid-stream
/// push, finish, or the owned cloud deadline on the post-stop wait) it
/// batch-transcribes the full buffer locally so the user always gets their
/// dictation. Invalid-model 400s additionally raise a config error.
/// </summary>
public sealed class FallbackStreamingTranscriber : IStreamingTranscriber
{
    private readonly IStreamingTranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackStreamingTranscriber> _log;
    private readonly Action<string>? _onFallback;
    private readonly TimeSpan _cloudDeadline;
    private readonly Action<string>? _onConfigError;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleDeadline;

    public FallbackStreamingTranscriber(
        IStreamingTranscriber primary,
        ITranscriber local,
        ILogger<FallbackStreamingTranscriber> logger,
        Action<string>? onFallback = null,
        TimeSpan? cloudDeadline = null,
        Action<string>? onConfigError = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
    {
        _primary = primary;
        _local = local;
        _log = logger;
        _onFallback = onFallback;
        _cloudDeadline = cloudDeadline ?? TimeSpan.FromSeconds(10);
        _onConfigError = onConfigError;
        _scheduleDeadline = scheduleDeadline ?? ((cts, d) => cts.CancelAfter(d));
    }

    public string ModelName => _primary.ModelName;

    public async Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
    {
        IStreamingTranscriptionSession? inner = null;
        Exception? startError = null;
        try
        {
            inner = await _primary.StartSessionAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the USER aborted — do not degrade to a failed-mode session
        }
        catch (Exception ex)
        {
            startError = ex;
            _log.LogWarning(ex, "Cloud streaming session failed to start; local fallback will run at stop");
        }
        return new Session(this, inner, startError);
    }

    private sealed class Session : IStreamingTranscriptionSession
    {
        private readonly FallbackStreamingTranscriber _owner;
        private readonly IStreamingTranscriptionSession? _inner;
        private Exception? _failure;

        internal Session(FallbackStreamingTranscriber owner, IStreamingTranscriptionSession? inner, Exception? startError)
        {
            _owner = owner;
            _inner = inner;
            _failure = startError;
        }

        public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            if (_failure is not null || _inner is null) return;
            try
            {
                await _inner.PushAsync(mono16k, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failure = ex;
                _owner._log.LogWarning(ex, "Cloud streaming failed mid-dictation; local fallback will run at stop");
            }
        }

        public async Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
        {
            if (_failure is null && _inner is not null)
            {
                using var cloudCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _owner._scheduleDeadline(cloudCts, _owner._cloudDeadline);
                try
                {
                    return await _inner.FinishAsync(fullAudio, cloudCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // the USER aborted the dictation — do not run local as well
                }
                catch (Exception ex)
                {
                    _failure = ex; // deadline (cloudCts, not ct) or a cloud failure
                }
            }

            var reason = _failure!;
            if (reason is AssemblyAiException aai && AssemblyAiErrors.IsInvalidModel(aai))
            {
                _owner._log.LogWarning("AssemblyAI model appears invalid; surfacing config error and falling back");
                _owner._onConfigError?.Invoke(aai.Message);
            }
            else
            {
                _owner._log.LogWarning(reason, "Cloud streaming failed or timed out; falling back to local ASR");
            }
            _owner._onFallback?.Invoke(reason.Message);
            return await _owner._local.TranscribeAsync(fullAudio, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (_inner is not null) await _inner.DisposeAsync();
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.FallbackStreamingTranscriberTests"
```

Expected: PASS (7 tests).

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/FakeStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs
git commit -m "feat(asr): streaming cloud transcription with local batch fallback"
```

---

### Task 9: StreamingDictationSession coordinator

Per-dictation glue between the recorder's `FramesAvailable` event and a streaming session. Pure managed; keeps `PipelineHost`'s Windows-only edits (Task 10) to a handful of lines.

Pump behavior note (A4/N6): the frame channel is unbounded and the pump applies no backpressure, so a slower-than-realtime encoder (local RTF ≥ 1 on very weak hardware) degrades post-stop latency — frames queue and the tail grows — but never correctness: the batch fallback fires only on exceptions, and `FinishAsync(fullAudio)` remains authoritative. Post-merge, production `TranscribeMs` makes any such slowness directly observable.

**Files:**
- Create: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`

**Interfaces:**
- Consumes: `IStreamingTranscriber`/`IStreamingTranscriptionSession`, `TranscriptionResult`, `FakeStreamingTranscriber` (Task 8's double), `System.Threading.Channels`.
- Produces (used by Tasks 10, 11):
  - `static StreamingDictationSession Start(Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory, ILogger log, CancellationToken ct)`
  - `void OnFrame(ReadOnlyMemory<float> frame)` — capture-thread-safe, copies, never blocks
  - `Task<TranscriptionResult?> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)` — null when the factory returned null (no provider); rethrows unrecovered pump errors (parity with batch `TranscribeAsync` exceptions)
  - `ValueTask DisposeAsync()` — abandon path (silence-drop / cancel); never throws

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingDictationSessionTests
{
    private sealed class RecordingStreamingTranscriber : IStreamingTranscriber
    {
        public string ModelName => "rec";
        public RecordingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class RecordingSession : IStreamingTranscriptionSession
        {
            public List<float[]> Pushed { get; } = new();
            public ReadOnlyMemory<float> FinishAudio { get; private set; }
            public bool Disposed { get; private set; }

            public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            { Pushed.Add(mono16k.ToArray()); return ValueTask.CompletedTask; }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
            { FinishAudio = fullAudio; return Task.FromResult(new TranscriptionResult("OK", "rec")); }

            public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task FramesQueuedBeforeTheSessionIsReady_AreDeliveredInOrder()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var gate = new TaskCompletionSource<IStreamingTranscriber?>();
        var session = StreamingDictationSession.Start(
            _ => gate.Task, NullLogger.Instance, TestContext.Current.CancellationToken);

        session.OnFrame(new float[] { 1f });
        session.OnFrame(new float[] { 2f });
        gate.SetResult(transcriber); // transcriber becomes ready AFTER frames arrived
        session.OnFrame(new float[] { 3f });

        var result = await session.FinishAsync(new float[9], TestContext.Current.CancellationToken);

        result!.Text.ShouldBe("OK");
        transcriber.Session.Pushed.Select(f => f[0]).ShouldBe(new[] { 1f, 2f, 3f });
        transcriber.Session.FinishAudio.Length.ShouldBe(9);
        transcriber.Session.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task OnFrame_CopiesTheFrame_BeforeTheRecorderReusesItsBuffer()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        var buffer = new float[] { 42f };
        session.OnFrame(buffer);
        buffer[0] = -1f; // recorder reuses its buffer

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);
        transcriber.Session.Pushed[0][0].ShouldBe(42f);
    }

    [Fact]
    public async Task NullFactory_FinishReturnsNull()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(null),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]); // dropped silently

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Dispose_AbandonsWithoutTranscribing_AndNeverThrows()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);

        await session.DisposeAsync();

        transcriber.Session.Disposed.ShouldBeTrue();
        transcriber.Session.FinishAudio.Length.ShouldBe(0); // FinishAsync never ran
    }

    [Fact]
    public async Task FactoryException_SurfacesAtFinish()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromException<IStreamingTranscriber?>(new InvalidOperationException("boom")),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);

        await Should.ThrowAsync<InvalidOperationException>(
            () => session.FinishAsync(new float[10], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FramesAfterFinish_AreDroppedSilently()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);
        session.OnFrame(new float[5]); // must not throw
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
```

Expected: build FAILS with `CS0246` for `StreamingDictationSession`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`:

```csharp
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Per-dictation glue between the audio frame event and a streaming session.
/// Frames are copied into an unbounded channel on the capture thread (never
/// blocking it) and pumped into the session on a background task. The session
/// may not exist yet when the first frames arrive — the transcriber factory
/// (model ensure + build) runs concurrently on the pump — so frames queue until
/// it is ready. FinishAsync completes the pump and returns the final transcript,
/// or null when no transcriber materialized (caller uses the batch-adapter path).
/// </summary>
public sealed class StreamingDictationSession : IAsyncDisposable
{
    private readonly Channel<float[]> _frames = Channel.CreateUnbounded<float[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;
    private IStreamingTranscriptionSession? _session;
    private Exception? _pumpError;

    private StreamingDictationSession(
        Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
        ILogger log,
        CancellationToken ct)
    {
        _pump = Task.Run(async () =>
        {
            try
            {
                var transcriber = await transcriberFactory(ct);
                if (transcriber is null)
                {
                    // No provider available: drain and drop so nothing accumulates.
                    await foreach (var _ in _frames.Reader.ReadAllAsync(CancellationToken.None)) { }
                    return;
                }
                _session = await transcriber.StartSessionAsync(ct);
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    await _session.PushAsync(frame, ct);
            }
            catch (Exception ex)
            {
                _pumpError = ex;
                log.LogWarning(ex, "streaming dictation pump failed");
                while (_frames.Reader.TryRead(out _)) { } // unblock nothing-in-particular; drop leftovers
            }
        }, CancellationToken.None);
    }

    public static StreamingDictationSession Start(
        Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
        ILogger log,
        CancellationToken ct)
        => new(transcriberFactory, log, ct);

    /// <summary>Called from the recorder's FramesAvailable event. Copies the frame
    /// (the recorder may reuse its buffer) and never blocks the capture thread.</summary>
    public void OnFrame(ReadOnlyMemory<float> frame)
        => _frames.Writer.TryWrite(frame.ToArray()); // TryWrite is false after completion — silent drop

    /// <summary>Stop pumping and get the final transcript. Null when no transcriber
    /// materialized. Rethrows an unrecovered pump failure — parity with today, where
    /// a batch TranscribeAsync exception also propagates to the pipeline.</summary>
    public async Task<TranscriptionResult?> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        _frames.Writer.TryComplete();
        await _pump.WaitAsync(ct);
        if (_pumpError is not null) throw _pumpError;
        if (_session is null) return null;
        var result = await _session.FinishAsync(fullAudio, ct);
        await _session.DisposeAsync();
        _session = null;
        return result;
    }

    /// <summary>Abandon the dictation (silence-drop / cancel): stop the pump and
    /// dispose the session without transcribing. Never throws.</summary>
    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        try { await _pump; } catch { /* abandoned */ }
        if (_session is not null)
        {
            try { await _session.DisposeAsync(); } catch { /* abandoned */ }
            _session = null;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.StreamingDictationSessionTests"
```

Expected: PASS (6 tests).

- [ ] **Step 5: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs \
        tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
git commit -m "feat(asr): per-dictation streaming coordinator between frame events and sessions"
```

---

### Task 10: Pipeline wiring (AppShell + PipelineHost — Windows-only)

Wire streaming into the app: build streaming transcribers, start a `StreamingDictationSession` at recording start, tee `FramesAvailable` into it, and replace the stop-time `TranscribeAsync` with `FinishAsync`. **This code is `#if WINDOWS`/WinUI and CANNOT be compiled on Linux** — copy patterns exactly, mirror every stop-arm edit in BOTH duplicated arms (HoldUp lines ~429–564 and Toggle-stop lines ~705–844), and re-read each edit against its sibling arm before committing. **Line anchors throughout this task are approximate (the file drifts between revisions); executors MUST key on the QUOTED code anchors, never on line numbers.** `HistoryTimings.TranscribeMs` (measured around the new `FinishAsync`) automatically becomes the post-stop perceived latency on Windows.

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (`BuildTranscriber` → `BuildStreamingTranscriber`, ~lines 416–464; PipelineHost construction lambda ~lines 273–291, the transcriber lambda itself at ~284–286)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (field/ctor types lines ~63/~86; frame tee ~line 296; both start arms — insert BEFORE `_warmRecorder!.StartSession(...)` at ~408 / ~683; both stop arms ~471–501 / ~745–769; both silent-drop paths ~436–469 / ~711–743; Cancel case ~lines 670–673)

**Interfaces:**
- Consumes: `ParakeetStreamingTranscriber`, `AssemblyAiStreamingTranscriber`, `ClientStreamingWebSocket`, `FallbackStreamingTranscriber`, `StreamingDictationSession`, `IStreamingTranscriber` (Tasks 6–9); `ParakeetSession : IParakeetBackend` (Task 5); pre-roll frames (Task 3).
- Produces: `AppShell.BuildStreamingTranscriber(ParakeetSession local, string loadedModelName, AppSettings settings, Action<string> onFallback, IAssemblyAiClient client, IAssemblyAiKeyStore keyStore, AssemblyAiOptions options, CorrectionStore? correctionStore, ErrorBus errorBus, ILoggerFactory loggerFactory) : IStreamingTranscriber` — the SAME parameter list as today's `BuildTranscriber`: the cloud REST batch transcriber (with its corrections/extras snapshot) is still constructed, because it is the streaming session's zero-pushed batch fallback (Task 7 / A9). Note the streaming connect itself sends no keyterms: `keyterms_prompt` DOES exist on v3 streaming but wiring it is deferred (see Task 7's Protocol facts); user corrections stay guaranteed by cleanup's deterministic corrections pass — see `CloudProvider` doc comment.

- [ ] **Step 1: Replace `AppShell.BuildTranscriber` with `BuildStreamingTranscriber`**

In `src/Winpepper.App/Hosting/AppShell.cs`, replace the entire static `BuildTranscriber` method (lines ~407–464 including its doc comment) with:

```csharp
    /// <summary>
    /// Builds the streaming transcriber for a dictation. When AssemblyAI is
    /// selected the cloud streaming provider is wrapped in a
    /// FallbackStreamingTranscriber so any failure lands on the local Parakeet
    /// session (batch). Otherwise the local chunked-streaming transcriber is
    /// used. Static, taking its dependencies explicitly, so the pipeline can
    /// invoke it through an injected delegate without holding an AppShell
    /// instance. NOTE: the streaming connect sends no keyterms — v3 streaming
    /// DOES support keyterms_prompt but wiring it is deferred (Task 7 Protocol
    /// facts); custom_spelling is batch-only. User corrections still apply via
    /// cleanup's deterministic corrections pass, and the cloud REST batch
    /// transcriber built below (the zero-pushed fallback) keeps its extras.
    /// </summary>
    public static Winpepper.Asr.Transcription.IStreamingTranscriber BuildStreamingTranscriber(
        Winpepper.Asr.ParakeetSession local,
        string loadedModelName,
        AppSettings settings,
        Action<string> onFallback,
        Winpepper.Asr.Transcription.IAssemblyAiClient client,
        Winpepper.Asr.Transcription.IAssemblyAiKeyStore keyStore,
        Winpepper.Asr.Transcription.AssemblyAiOptions options,
        Winpepper.Corrections.CorrectionStore? correctionStore,
        Winpepper.Core.Errors.ErrorBus errorBus,
        ILoggerFactory loggerFactory)
    {
        var localBatch = new Winpepper.Asr.Transcription.ParakeetTranscriber(
            local, loadedModelName);
        var localStreaming = new Winpepper.Asr.Transcription.ParakeetStreamingTranscriber(
            local, localBatch, loadedModelName, Winpepper.Asr.PreprocessorConfig.ParakeetTdtV3,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.ParakeetStreamingTranscriber>());

        if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
            return localStreaming;

        // Snapshot corrections into request extras at build time; keyterms only
        // when opted in — copied verbatim from today's BuildTranscriber. The REST
        // batch transcriber is the streaming session's zero-pushed fallback (A9).
        Winpepper.Asr.Transcription.AssemblyAiRequestExtras Extras()
        {
            var data = correctionStore?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;
            return Winpepper.Asr.Transcription.CorrectionSpellingMapper.ToExtras(data, options.KeytermsEnabled);
        }

        var cloudBatch = new Winpepper.Asr.Transcription.AssemblyAiTranscriber(
            client, keyStore, options,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiTranscriber>(),
            extrasProvider: Extras);

        var cloud = new Winpepper.Asr.Transcription.AssemblyAiStreamingTranscriber(
            () => new Winpepper.Asr.Transcription.ClientStreamingWebSocket(),
            cloudBatch, keyStore, options,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiStreamingTranscriber>());

        return new Winpepper.Asr.Transcription.FallbackStreamingTranscriber(
            cloud, localBatch,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.FallbackStreamingTranscriber>(),
            onFallback: onFallback,
            cloudDeadline: options.CloudDeadline,
            onConfigError: msg => errorBus.Report(
                // Models, NOT Asr: this fires per dictation attempt and the
                // dictation then SUCCEEDS via local fallback, so it is a
                // per-attempt EVENT. At Asr it would classify as a CONDITION
                // whose only clearing seam (local model Load/Swap success)
                // never runs for a cloud user - a permanent tray error while
                // every dictation works. Behavior is otherwise identical:
                // ErrorDeepLink maps Asr and Models both to "models"/"Open
                // Models tab" and ErrorToastPolicy toasts both.
                Winpepper.Core.Errors.ErrorStage.Models,
                new InvalidOperationException(
                    $"AssemblyAI model rejected ({settings.AssemblyAiModel}). Check the model setting. {msg}"),
                Guid.Empty)); // config-level error, not tied to a capture session
    }
```

Update the PipelineHost construction lambda (~lines 284–286 — the `AppShell.BuildTranscriber` call inside the `new PipelineHost(...)` argument list; keep every argument, only the method name changes):

```csharp
                                         (local, loadedModelName, s, onFallback) => AppShell.BuildStreamingTranscriber(
                                             local, loadedModelName, s, onFallback, aaiClient, aaiKeyStore, aaiOptions,
                                             correctionStore, errorBus, factory),
```

Verify no other caller remains: `grep -rn "BuildTranscriber" src/ tests/` must return nothing.

- [ ] **Step 2: PipelineHost — field/ctor types and the frame tee**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

(a) Field (line ~63) — change the delegate's return type:

```csharp
    private readonly Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> _buildTranscriber;
```

and the matching constructor parameter (line ~86):

```csharp
        Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> transcriberFactory,
```

(b) Add two fields next to the existing `_frameHandler` field:

```csharp
    private Winpepper.Asr.Transcription.StreamingDictationSession? _streamingSession;
    private Action<ReadOnlyMemory<float>>? _streamFrameHandler;
```

(c) Frame tee — immediately after `recorder.FramesAvailable += _frameHandler;` (line ~296; the quoted line is unique in the file — anchor on it, not the number), add:

```csharp
                // Streaming tee: a permanent handler that forwards frames to the
                // current dictation's streaming session (null outside dictations,
                // so this is a no-op at idle). OnFrame copies and never blocks.
                _streamFrameHandler = frame => _streamingSession?.OnFrame(frame);
                recorder.FramesAvailable += _streamFrameHandler;
```

- [ ] **Step 3: PipelineHost — start streaming at recording start (BOTH start arms)**

In the **HoldDown arm**, immediately BEFORE the line `_warmRecorder!.StartSession(includePrerollMs: 500);` (~line 408, right after `_sounds.PlayStart();`), add the block below. The session MUST exist before `StartSession` runs: `WarmWasapiRecorder.StartSession` raises the 500 ms pre-roll SYNCHRONOUSLY through `FramesAvailable` (Task 3), so with a null `_streamingSession` the tee's null-conditional silently drops it and the cloud stream permanently loses the dictation's first ~500 ms (a cloud session never re-sends earlier audio). Creating the session early is safe: frames queue in the coordinator's channel until the factory completes (Task 9). Note `settingsAtStart` is only captured LATER (in the prefetch block), so this block takes its OWN settings snapshot via `_settingsProvider()`:

```csharp
                // Start the streaming dictation session BEFORE StartSession —
                // StartSession raises the 500 ms pre-roll synchronously through
                // FramesAvailable, so the session must already exist (frames
                // queue in the coordinator until the factory completes) or the
                // cloud stream permanently loses the first ~500 ms. The factory
                // runs on a background pump so recording start stays instant;
                // model ensure is silent here (reportErrors: false) — the stop
                // arm's late path re-runs the check with today's exact error UX.
                var settingsForStream = _settingsProvider();
                _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                    ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                    {
                        var cloudSel = string.Equals(settingsForStream.AsrProvider, "assemblyai",
                            StringComparison.OrdinalIgnoreCase);
                        var ready = TryEnsureAsrModel(reportErrors: false);
                        if ((!ready && !cloudSel) || _asr is null) return null;
                        return _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsForStream, notice =>
                            _ = _toasts.ShowAsync(
                                "Winpepper",
                                "Cloud transcription unavailable — used local speech recognition instead.",
                                Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                                TimeSpan.FromSeconds(6)));
                    }, ct2),
                    _log, ct);
```

Mirror the same block in the **Toggle-start arm** — immediately BEFORE its `_warmRecorder!.StartSession(includePrerollMs: 500);` line (~line 683, right after its `_sounds.PlayStart();`) — with `2`-suffixed names (its own snapshot, since `settingsAtStart2` is also only captured later):

```csharp
                    // (same comment as the HoldDown arm: create BEFORE StartSession
                    // so the synchronously-raised pre-roll is not dropped)
                    var settingsForStream2 = _settingsProvider();
                    _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                        ct2 => Task.Run<Winpepper.Asr.Transcription.IStreamingTranscriber?>(() =>
                        {
                            var cloudSel2 = string.Equals(settingsForStream2.AsrProvider, "assemblyai",
                                StringComparison.OrdinalIgnoreCase);
                            var ready2 = TryEnsureAsrModel(reportErrors: false);
                            if ((!ready2 && !cloudSel2) || _asr is null) return null;
                            return _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsForStream2, notice =>
                                _ = _toasts.ShowAsync(
                                    "Winpepper",
                                    "Cloud transcription unavailable — used local speech recognition instead.",
                                    Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                                    TimeSpan.FromSeconds(6)));
                        }, ct2),
                        _log, ct);
```

IMPORTANT: if `TryEnsureAsrModel` is not safe to call off the hotkey thread (check its body for UI-thread assumptions before wiring — it performs model load/swap and error reporting), keep the call but confirm `reportErrors: false` bypasses any UI dispatch; if it cannot run off-thread, return the transcriber only when `_asr` is ALREADY loaded (`_asrSwap.LoadedModelName is not null`) and skip the ensure — the stop arm's ensure still guarantees correctness via the null-coordinator batch path.

- [ ] **Step 4: PipelineHost — finish streaming at stop (BOTH stop arms)**

**HoldUp arm.** Replace lines ~471–501 (from `var transcribeSw = ...` through `transcribeSw.Stop();`) with the block below. ORDERING IS LOAD-BEARING (A5): when a streaming session exists, `streaming.FinishAsync(trimmed, ct)` runs FIRST, before ANY `TryEnsureAsrModel` call — the ensure's swap branch disposes the `ParakeetSession` the streaming transcriber still holds (`old?.Dispose()` at PipelineHost.cs:228–232, under `_startGate`; nothing gates swaps on engine state), so an ensure racing an in-flight session is a use-after-dispose on a live ONNX session. With this ordering the only ensure during a dictation is the start-arm factory's own, which completes BEFORE the session captures the model. `TryEnsureAsrModel` + the `_asr is null` guard + the failure early-exit run ONLY on the late path (no streaming session, or streaming returned null). Accepted consequence: a model selection changed mid-dictation takes effect on the NEXT dictation.

```csharp
                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                var settingsNow = _settingsProvider();
                var cloudSelected = string.Equals(
                    settingsNow.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                Action<string> fallbackNotice = notice =>
                    _ = _toasts.ShowAsync(
                        "Winpepper",
                        "Cloud transcription unavailable — used local speech recognition instead.",
                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                        TimeSpan.FromSeconds(6));
                // Finish the streaming session FIRST — before ANY TryEnsureAsrModel
                // call: the ensure's swap branch disposes the ParakeetSession the
                // streaming transcriber still holds (no engine-state gating), so
                // no ensure may run while a session is in flight. The factory's
                // own ensure (start arm) ran before the session captured the
                // model. A mid-dictation model change applies to the NEXT dictation.
                var streaming = _streamingSession;
                _streamingSession = null;
                Winpepper.Asr.Transcription.TranscriptionResult? maybeTranscription = null;
                if (streaming is not null)
                    maybeTranscription = await streaming.FinishAsync(trimmed, ct);
                if (maybeTranscription is null)
                {
                    // Late path: no streaming session materialized, or its factory
                    // returned null (no provider at start). Run today's ensure +
                    // error UX, then the batch-equivalent path via the streaming
                    // seam — behavior identical to today.
                    // Provider-aware (req 6): a failed LOCAL swap never skips or
                    // aborts a CLOUD dictation; soften its error surface.
                    var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                    if ((!localReady && !cloudSelected) || _asr is null)
                    {
                        if (streaming is not null)
                        {
                            await streaming.DisposeAsync(); // no-op after FinishAsync; never throws
                        }
                        // Terminal-state early-exit (S2): never bare-return — drive
                        // the engine back so the next dictation can start.
                        _engine.Apply(SessionEvent.Failed);
                        if (cloudSelected && _asr is null)
                        {
                            // Cloud selected but no local session exists at all (the
                            // fallback wrapper needs one): surface this rare case.
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                                new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                                Guid.Empty);
                        }
                        _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                        return;
                    }
                    var transcriber = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow, fallbackNotice);
                    await using var lateSession = await transcriber.StartSessionAsync(ct);
                    maybeTranscription = await lateSession.FinishAsync(trimmed, ct);
                }
                var transcription = maybeTranscription;
                transcribeSw.Stop();
```

**Toggle-stop arm.** Apply the identical replacement to lines ~745–769 with the `2`-suffixed variables (`transcribeSw2`, `settingsNow2`, `cloudSelected2`, `fallbackNotice2`, `streaming2`, `maybeTranscription2`, `localReady2`, `lateSession2`, `trimmed2`, `transcription2`) — same logic, token-for-token, including the same FinishAsync-before-any-ensure ordering and the late-path early-exit's `streaming2` dispose.

- [ ] **Step 5: PipelineHost — abandon paths (BOTH silent-drop paths + Cancel)**

In BOTH silent-drop blocks (`if (trimmed is null) { ... }` at ~436–469 and `if (trimmed2 is null) { ... }` at ~711–743), immediately before the existing `_recordStopwatch = null; break;` lines, add:

```csharp
                    if (_streamingSession is not null)
                    {
                        await _streamingSession.DisposeAsync();
                        _streamingSession = null;
                    }
```

In the `HotkeyEventKind.Cancel` case (~lines 670–673 — the ONLY `case HotkeyEventKind.Cancel:` in the file; anchor on the quoted body, not the number), extend to:

```csharp
            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _ = _warmRecorder?.StopSession();
                if (_streamingSession is not null)
                {
                    await _streamingSession.DisposeAsync();
                    _streamingSession = null;
                }
                break;
```

- [ ] **Step 6: Verify what CAN be verified on Linux**

`Winpepper.App` cannot compile here — but every project it depends on can, and the full suite must stay green:

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

Then re-read both PipelineHost arms side by side (`sed -n '396,580p'` and `sed -n '680,860p'` on the file — adjust to the drifted line numbers) and confirm the two arms received token-equivalent edits, INCLUDING: the start-arm session creation sits BEFORE `_warmRecorder!.StartSession(...)` in both arms, and both stop arms call `streaming.FinishAsync(...)`/`streaming2.FinishAsync(...)` before any `TryEnsureAsrModel`. Also run `grep -n "_streamingSession" src/Winpepper.App/Hosting/PipelineHost.cs` — expected hits: 2 field/handler lines, 2 start-arm assignments, 4 stop-arm consume lines (`var streaming = _streamingSession;` + `_streamingSession = null;` per arm), 2 silent-drop dispose blocks (3 lines each), 1 cancel dispose block (3 lines).

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Hosting/AppShell.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): stream dictation audio into the transcriber during recording"
```

Note for the reviewer and the final report — the Windows verification is a HARD REQUIREMENT, not a nice-to-have. These files are Windows-only; the Linux gate proves the rest of the tree. Post-merge, on Windows, the following MUST be done:

1. Windows build + `ParakeetSessionIntegrationTests` + a manual dictation (`TranscribeMs` in `%LOCALAPPDATA%\winpepper\history\index.json` shows the production post-stop latency).
2. **Batch-vs-streamed transcript comparison on the real model** (the accepted mitigation for the unverifiable chunked-quality assumption, A1): run the SAME recordings (real archived dictations of varying lengths) through both the batch path and the streamed path and diff the transcripts. Investigate any material quality gap BEFORE enabling streaming by default — chunked no-right-context inference of this offline export has zero measured quality evidence, and this comparison is what stands in for it. It also covers the residual trailing-silence risk the leading-silence gate (Task 6) does not.
3. Latency observability: a slower-than-realtime encoder degrades latency but never correctness (the fallback fires only on exceptions) — production `TranscribeMs` makes any such regression observable here.

---

### Task 11: AFTER benchmark — record and commit the before/after evidence

**Files:**
- Modify: `scripts/asr-latency-bench/Program.cs` (add streaming scenarios)
- Modify: `docs/plans/2026-07-25-streaming-transcription-bench.md` (AFTER table + comparison)

**Interfaces:**
- Consumes: `StreamingDictationSession`, `ParakeetStreamingTranscriber`, `AssemblyAiStreamingTranscriber`, `ClientStreamingWebSocket`, `IStreamingWebSocket`, `IParakeetBackend`, `EncoderOutput`, `DecoderJointResult`, `PreprocessorConfig`, `Pcm16` (Tasks 4–9), plus everything Task 1 used.
- Produces: scenarios `sim-local-stream`, `sim-remote-stream`, `real-remote-stream`; the committed AFTER numbers.

- [ ] **Step 1: Add the streaming scenarios**

In `scripts/asr-latency-bench/Program.cs`, extend the default scenario list:

```csharp
var requested = args.Length > 0 ? args : new[]
{
    "sim-local-batch", "sim-local-stream",
    "sim-remote-batch", "sim-remote-stream",
    "real-remote-batch", "real-remote-stream",
};
```

Add the new cases to the `switch` (before `default:`):

```csharp
        case "sim-local-stream":
        {
            // REAL production pipeline (StreamingDictationSession +
            // ParakeetStreamingTranscriber + chunked mel/decode) with the ONNX
            // encoder edge replaced by the same RTF delay model as sim-local-batch.
            var audio = SynthesizeAudio(AudioSeconds);
            var backend = new PacedParakeetBackend(LocalRtf);
            var batch = new PacedTranscriber("parakeet-sim", TimeSpan.FromSeconds(AudioSeconds * LocalRtf));
            var streaming = new ParakeetStreamingTranscriber(
                backend, batch, "parakeet-sim", Winpepper.Asr.PreprocessorConfig.ParakeetTdtV3);
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
```

Add the shared measurement helper and the two new fakes at the bottom of the file (with the other helpers):

```csharp
// Simulates a live dictation: frames pushed in real time (50 ms cadence) through
// the REAL coordinator, then measures stop -> final transcript.
static async Task<long> MeasureStreaming(IStreamingTranscriber transcriber, float[] audio)
{
    var session = StreamingDictationSession.Start(
        _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
        NullLogger.Instance, CancellationToken.None);
    const int frame = 800; // 50 ms
    for (var i = 0; i < audio.Length; i += frame)
    {
        session.OnFrame(audio.AsMemory(i, Math.Min(frame, audio.Length - i)));
        await Task.Delay(50);
    }
    var sw = Stopwatch.StartNew();
    var result = await session.FinishAsync(audio, CancellationToken.None);
    var ms = sw.ElapsedMilliseconds;
    if (result is null) throw new InvalidOperationException("no transcriber materialized");
    return ms;
}

/// <summary>IParakeetBackend whose Encode costs rtf x chunk-audio-seconds (the
/// same realtime-factor assumption as sim-local-batch); decode steps are free.</summary>
sealed class PacedParakeetBackend : Winpepper.Asr.IParakeetBackend
{
    private readonly double _rtf;
    public PacedParakeetBackend(double rtf) => _rtf = rtf;
    public int VocabSize => 8;
    public int BlankId => 7;
    public int DecoderHiddenLayers => 2;
    public int DecoderHiddenDim => 4;

    public Winpepper.Asr.EncoderOutput Encode(float[,] melFrames)
    {
        var tIn = melFrames.GetLength(0);
        Thread.Sleep(TimeSpan.FromSeconds(_rtf * tIn / 100.0)); // 100 mel frames per audio second
        var tOut = Math.Max(1, tIn / 8);
        return new Winpepper.Asr.EncoderOutput(new float[2 * tOut], tOut, 2, tOut);
    }

    public Winpepper.Asr.DecoderJointResult DecodeJoint(float[] encoderFrame, int lastToken, float[] stateH, float[] stateC)
    {
        var logits = new float[8 + 5];
        logits[BlankId] = 10f;
        logits[8 + 1] = 10f;
        return new Winpepper.Asr.DecoderJointResult(logits, stateH, stateC);
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
```

Add the needed `using Winpepper.Asr;` / fully-qualified references and `using Microsoft.Extensions.Logging.Abstractions;` as the compiler directs.

- [ ] **Step 2: Run the full benchmark**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/streaming-transcription
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release
dotnet run --project scripts/asr-latency-bench -c Release
```

Expected shape (each streaming scenario takes ~10 s wall for the paced recording itself; the reported number is only the post-stop wait):
- `sim-local-stream` ≈ 400–1000 ms (post-stop tail = remaining ≤ 2 s chunk + 1 s re-encoded context at RTF 0.3, i.e. ≤ ~900 ms) vs `sim-local-batch` ≈ 3000 ms;
- `sim-remote-stream` ≈ 300–500 ms vs `sim-remote-batch` ≈ 4200–5200 ms;
- `real-remote-stream` (when the key is set) — typically well under 1 s vs multi-second `real-remote-batch`.

If a streaming number is NOT dramatically lower than its batch counterpart, stop and investigate (the whole feature exists for this) — do not record numbers that contradict the claim without a root-cause note.

- [ ] **Step 3: Record the AFTER numbers and the comparison**

In `docs/plans/2026-07-25-streaming-transcription-bench.md`, replace the `## AFTER` placeholder section with the real output:

```markdown
## AFTER (streaming architecture) — recorded 2026-07-25

| scenario | kind | audio | post-stop latency (ms) |
|---|---|---|---|
| sim-local-batch | simulated | 10 s | <measured> |
| sim-local-stream | simulated | 10 s | <measured> |
| sim-remote-batch | simulated | 10 s | <measured> |
| sim-remote-stream | simulated | 10 s | <measured> |
| real-remote-batch | REAL network | 10 s | <measured or SKIPPED> |
| real-remote-stream | REAL network | 10 s | <measured or SKIPPED> |

## Comparison (perceived transcription time, 10 s dictation)

| path | BEFORE (batch) | AFTER (streaming) | reduction |
|---|---|---|---|
| local | <before> ms | <after> ms | <x>% |
| remote | <before> ms | <after> ms | <x>% |

On Windows, production `HistoryTimings.TranscribeMs` (history archive) measures
this same post-stop window around the new FinishAsync call, so the improvement
is directly observable in real dictations after merge.
```

Fill in every `<measured>`/`<before>`/`<after>`/`<x>` with the actual numbers — committing placeholders is a plan failure.

- [ ] **Step 4: Full suite gate + commit**

Run the FULL SUITE GATE block. Expected: `ALL GREEN`.

```bash
git add scripts/asr-latency-bench/Program.cs docs/plans/2026-07-25-streaming-transcription-bench.md
git commit -m "chore(bench): record streaming vs batch post-stop latency evidence"
```

The final report MUST quote both the BEFORE and AFTER tables from `docs/plans/2026-07-25-streaming-transcription-bench.md`.
