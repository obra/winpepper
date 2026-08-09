# Nemotron-First Local ASR Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make the Nemotron streaming model the primary speech model (installed during onboarding via a model picker), run transcribe.cpp in a killable/restartable worker subprocess, demote Parakeet to an optional backup, and keep every existing install working unchanged.

**Architecture:** The native transcribe.cpp engine moves out of the app process into a worker subprocess (`Winpepper.exe --transcribe-worker`) reached through a new `WorkerProcessEngine : ITranscribeCppEngine` proxy (length-prefixed binary frames over stdin/stdout, per-op timeouts, kill→respawn→retry). Because the proxy keeps the existing `ITranscribeCppEngine` seam, `NemotronStreamingTranscriber`, `StreamingRouteGuard`, and all their tests are unchanged. A production `NemotronBatchTranscriber : ITranscriber` (port of the bench's `EngineBatchTranscriber`) becomes the batch/fallback workhorse; `PipelineHost`/`AppShell` are decoupled from `ParakeetSession` so dictation works with no Parakeet on disk. Onboarding Step 3 becomes the approved model-picker (English/Multilingual radio + Backup/Cleanup checkboxes) with background downloads and a verification-gated Test-Dictation step.

**Tech Stack:** C# / .NET 9, WinUI 3 (Windows-only app layer), xUnit v3 in-process runner + Shouldly, WiX MSI, transcribe.cpp v0.1.3 native runtime, ONNX Runtime (Parakeet), hand-rolled INPC MVVM (no toolkit, no DI container).

## Global Constraints

- Worktree root (all paths below are relative to it): `/home/dan/code/winpepper/.worktrees/nemotron-first-asr`, branch `feat/nemotron-first-asr`.
- **Before EVERY commit:** the Linux suite must be green — run `./scripts/linux-tests.sh` from the worktree root and require the final line `LINUX SUITE: GREEN` (0 failures across all 9 test projects). Baseline at branch point: 1790 tests.
- **Before push (final task):** `./scripts/windows-gate.sh` from WSL must exit 0 with `GATE: GREEN`. Budget 20–40 minutes; run in background with polling; never run concurrently with `linux-tests.sh`. Expect `Skipped > 0` (model-presence skips) — that is green.
- **Never use `dotnet test`.** Build with `dotnet build <csproj> -c Release -f net9.0 -p:EnableWindowsTargeting=true`, run with `dotnet exec <built dll> -notrait "Platform=Windows"`. `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"` first (SDK 9.0.100 lives in the MAIN checkout, not the worktree).
- **Never mix Linux and Windows builds in the same `bin/`/`obj/`** — the helper scripts pre-clean; do not hand-run Windows builds between Linux runs.
- Repo builds with `Nullable=enable` and `WarningsAsErrors=nullable` — unguarded nullable use is a build error.
- Tests: xUnit v3 + Shouldly; hand-rolled fakes (no mocking library); Windows-only tests get `[Trait("Platform","Windows")]` + `OperatingSystem.IsWindows()` guards; deterministic wedge tests use `ManualResetEventSlim` gates (never `Task.Delay` timing margins).
- `docs/designs/2026-08-08-model-picker-prototype.html` is the authoritative visual/behavior spec for the picker. Keep it in the repo; never ship it.
- **Do NOT invent SHA-256 hashes or byte sizes.** The multilingual model's values in this plan were read from the Hugging Face API (LFS oid = file SHA-256): `nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf`, `SizeBytes = 751,094,240`, `Sha256 = b94545b313b3223fda7b2857a52681da813935c2127643d1e9ff0c23d988089c` (from `https://huggingface.co/api/models/handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf/tree/main`, fetched 2026-08-08). Task 10 re-verifies them against the API before committing.
- Q8_0 quantization only for Nemotron models. `StreamingEnabled` stays default `true`. `StreamingEnabled=false` must work via Nemotron batch.
- README.md is the only end-user markdown doc; this plan is a working/agent doc.
- Keep commits focused and atomic; conventional-commit style messages as shown per task.
- Existing settings.json files must keep working: `AsrModelName` boot repair (`AppShell.cs:80-92`) must survive every change; new settings fields must have safe defaults; removed fields are simply ignored by `System.Text.Json`.

## File Structure (what gets created/modified, by responsibility)

New cross-platform code (Linux-testable, in `src/Winpepper.Asr/`):
- `TranscribeCpp/Worker/WorkerProtocol.cs` — framed binary wire codec (`WorkerOp`, `WorkerWire`).
- `TranscribeCpp/Worker/TranscribeWorkerLoop.cs` — worker-side request loop hosting the real engine.
- `TranscribeCpp/Worker/WorkerProcess.cs` — `IWorkerProcess`/`IWorkerProcessFactory` abstraction.
- `TranscribeCpp/Worker/WorkerEngineOptions.cs` — per-op timeout knobs.
- `TranscribeCpp/Worker/WorkerRestartPolicy.cs` — pure kill→respawn budget/cooldown.
- `TranscribeCpp/Worker/WorkerProcessEngine.cs` — client-side `ITranscribeCppEngine` proxy with supervision.
- `TranscribeCpp/Worker/ExeWorkerProcess.cs` — real `System.Diagnostics.Process` implementation.
- `TranscribeCpp/StreamingModelLayout.cs` — per-streaming-model on-disk layout (English + Multilingual); `NemotronStreamingModel` becomes a thin shim over the English layout.
- `Transcription/NemotronBatchTranscriber.cs` — production port of the bench batch adapter.
- `Transcription/IDisposableTranscriber.cs` — `ITranscriber + IDisposable` seam for the pipeline's optional local batch model.
- `Transcription/LocalStreamingTranscriberFactory.cs` — pure composition of the local transcriber ladder (Linux-testable so `AppShell` stays thin).

Modified app layer (`#if WINDOWS`, verified by the gate):
- `src/Winpepper.App/Program.cs` — `--transcribe-worker` verb.
- `src/Winpepper.App/Hosting/NemotronEngineHolder.cs` — worker-backed, no permanent latch, selected-model aware.
- `src/Winpepper.App/Hosting/PipelineHost.cs` — `_asr` retyped, Parakeet optional, gates reworked (both hotkey arms).
- `src/Winpepper.App/Hosting/AppShell.cs` — wiring, startup gate, auto-install condition, `BuildStreamingTranscriber` rework.
- `src/Winpepper.App/Services/ModelsServices.cs` — `VerifyPrimarySpeechReadyAsync`, streaming-model helpers.
- `src/Winpepper.App/Services/OnboardingModelProvisioner.cs` (new) — background multi-model downloads for onboarding.
- `src/Winpepper.App/Views/OnboardingPage.xaml{,.cs}` — the model-picker step.
- `src/Winpepper.App/Views/ModelsPage.xaml{,.cs}` — streaming promote + copy.
- `src/Winpepper.App/Views/RecordingPage.xaml.cs` — autostart shadow-setting removal.
- `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs` — rerun picker includes streaming models.

Modified shared bricks:
- `src/Winpepper.Models/ModelRegistry.cs` — multilingual entry + `ResolveOrDefault` streaming default.
- `src/Winpepper.Models/StreamingAutoInstaller.cs` — selected-model aware (upgrade path).
- `src/Winpepper.Models/DownloadBatchPlanner.cs` (new) — pure ordering/percent math for onboarding downloads.
- `src/Winpepper.Core/Settings/AppSettings.cs` — `StreamingModelName`, picker-choice flags; `AutostartEnabled` removed.
- `src/Winpepper.Core/Settings/StreamingModelSelectionSlot.cs` (new).
- `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs` — picker state + background-download gating.
- `src/Winpepper.Core/ViewModels/IOnboardingModelProvisioner.cs` (new) + `ModelPickerCatalog`.
- `src/Winpepper.Core/ViewModels/AsrPipelineStartupGate.cs` — verify delegate instead of `IAsrProvisioningService`.
- `src/Winpepper.History/Lab/LocalTranscriptionRerunService.cs` (new, replaces `ParakeetTranscriptionRerunService.cs`) + `RerunModelRouter.cs`.

Deleted: `src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs`, `ParakeetStreamingSession.cs`, `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (+ orphaned mel helpers if verified orphaned — Task 19).

### Naming and role decisions locked by this plan

- Registry id of the multilingual model: **`nemotron-streaming-multi`** (`ModelRegistry.MultilingualStreamingAsrName`); file `nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf`; language hint `null`. The literal `"auto"` is rejected by the v0.1.3 dispatcher gate (exact strcmp against the GGUF's `general.languages`, which lacks `"auto"`); autodetect is a TRUE null (never `string.Empty` — the P/Invoke layer maps null to `IntPtr.Zero`); validated 2026-08-08.
- New setting `AppSettings.StreamingModelName`, default `"nemotron-streaming-en"` — which Nemotron is primary. Existing settings.json files lack the field → default → English → upgrades keep streaming exactly as today.
- `AsrModelName` keeps meaning "the (optional, backup) batch ONNX model" and keeps default `parakeet-tdt-0.6b-v3`; boot repair is untouched. A missing Parakeet is no longer an error.
- `NemotronBatchTranscriber.ModelName` = `<streaming name> + "-batch"` (e.g. `nemotron-streaming-en-batch`). It MUST differ from the streaming model name: `PipelineHost` classifies `asr_mode=streaming` by streaming-name matching, and a batch result must be classified/budgeted as batch. Task 13 updates that classifier (both hotkey arms, `PipelineHost.cs:909-917` HOLD and `~:1547-1550` TOGGLE) from an exact match against the English constant to name-set membership via `StreamingModelLayout.For(...)`, so BOTH streaming models (English AND Multilingual) stamp `asr_mode=streaming` and are budgeted with the 2 s streaming budget; `-batch` names still classify as batch.
- Batch fallback ladder everywhere local batch is needed: **Nemotron batch first, Parakeet second (only when installed)**, composed with the existing `FallbackTranscriber` (`src/Winpepper.Asr/Transcription/FallbackTranscriber.cs:26-33`, ctor `(ITranscriber primary, ITranscriber local, ILogger<FallbackTranscriber> logger, Action<string>? onFallback = null, ...)`).
- Picker sizes are computed from real registry bytes (MB = bytes/1,000,000; display rounded to nearest 10 with `~`), NOT the prototype's mock numbers: English ~760 MB (755,608,086 B incl. 26 MB runtime), Multilingual ~780 MB (777,052,150 B), Backup ~670 MB (670,479,942 B), Text cleanup ~490 MB (491,400,032 B). Total format: `>= 1000 MB → one-decimal GB (divide by 1000)`, else `N MB` — the prototype's 1000/1024 asymmetry is consciously fixed (its own spec flags this as a decision point). All other copy is verbatim from the prototype.
- Background-download contradiction in the prototype (footnote promises background, JS blocks) is resolved per the approved spec: clicking **Download & continue** starts downloads in the background and advances immediately to Test dictation, which gates on the chosen speech model verifying + the pipeline starting.

---

### Task 1: Verify the design prototype is tracked and record the green baseline

**Files:**
- Verify tracked: `docs/designs/2026-08-08-model-picker-prototype.html` (already committed on this branch)

**Interfaces:**
- Consumes: nothing.
- Produces: confirmation that the prototype is tracked in git (later tasks reference it); a recorded baseline test count.

- [ ] **Step 1: Run the Linux suite to record the baseline**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: final line `LINUX SUITE: GREEN`. Note the total test count (baseline ≈ 1790).

- [ ] **Step 2: Verify the prototype is tracked and the worktree is clean**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
git ls-files --error-unmatch docs/designs/2026-08-08-model-picker-prototype.html
git status --porcelain
```
Expected: the first command prints the path and exits 0 (the prototype was committed to this branch during plan review); the second prints nothing. If the first command fails, `git add` the file and commit it with `docs(designs): add onboarding model-picker prototype (authoritative visual spec)` before proceeding.

---

### Task 2: Worker wire protocol codec

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProtocol.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProtocolTests.cs`

**Interfaces:**
- Consumes: nothing (pure BCL).
- Produces (used by Tasks 3, 5):
  - `enum WorkerOp : byte { Load = 1, BeginStream = 2, Feed = 3, FinalizeStream = 4, DisposeStream = 5, TranscribeBatch = 6, Shutdown = 7, Ok = 100, LoadOk = 101, BeginStreamOk = 102, FeedOk = 103, FinalizeOk = 104, BatchOk = 105, Error = 110 }`
  - `static class WorkerWire` with:
    - `void WriteFrame(Stream s, WorkerOp op, byte[] payload)` / `(WorkerOp Op, byte[] Payload) ReadFrame(Stream s)` (WriteFrame throws `InvalidDataException` on payload > 64 MiB BEFORE writing anything — an oversize frame is lethal to the peer's reader; ReadFrame throws `EndOfStreamException` on EOF, `InvalidDataException` on length > 64 MiB)
    - `void WriteString(BinaryWriter w, string? value)` / `string? ReadString(BinaryReader r)` (length `-1` = null, UTF-8)
    - `void WriteFloats(BinaryWriter w, float[] samples, int count)` / `float[] ReadFloats(BinaryReader r)`
  - Payload schemas (documented in the file header, binding for Tasks 3/5):
    - `Load` = string runtimeDir, string ggufPath → `LoadOk` = string modelName
    - `BeginStream` = int32 attContextRight, string? language → `BeginStreamOk` = int32 gateWaitMs
    - `Feed` = floats → `FeedOk` = string? committedText
    - `FinalizeStream` = (empty) → `FinalizeOk` = string text, bool wasTruncated
    - `DisposeStream` = (empty) → `Ok` (idempotent)
    - `TranscribeBatch` = string? language, floats → `BatchOk` = int32 gateWaitMs, string text
    - `Shutdown` = (empty) → `Ok`, then the worker exits
    - `Error` = int32 gateWaitMs, string exceptionTypeName, string message

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProtocolTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Frame_RoundTrips_OpAndPayload()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.Feed, new byte[] { 1, 2, 3 });
        ms.Position = 0;
        var (op, payload) = WorkerWire.ReadFrame(ms);
        op.ShouldBe(WorkerOp.Feed);
        payload.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Frame_EmptyPayload_RoundTrips()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.FinalizeStream, Array.Empty<byte>());
        ms.Position = 0;
        var (op, payload) = WorkerWire.ReadFrame(ms);
        op.ShouldBe(WorkerOp.FinalizeStream);
        payload.ShouldBeEmpty();
    }

    [Fact]
    public void ReadFrame_OnEof_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        Should.Throw<EndOfStreamException>(() => WorkerWire.ReadFrame(ms));
    }

    [Fact]
    public void ReadFrame_OnTruncatedPayload_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.Feed, new byte[] { 1, 2, 3, 4 });
        var truncated = new MemoryStream(ms.ToArray(), 0, (int)ms.Length - 2);
        Should.Throw<EndOfStreamException>(() => WorkerWire.ReadFrame(truncated));
    }

    [Fact]
    public void ReadFrame_OnInsaneLength_ThrowsInvalidData()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)WorkerOp.Feed);
        ms.Write(BitConverter.GetBytes(int.MaxValue));
        ms.Position = 0;
        Should.Throw<InvalidDataException>(() => WorkerWire.ReadFrame(ms));
    }

    [Fact]
    public void WriteFrame_OversizePayload_Throws_WithoutWriting()
    {
        // A frame the peer would fatally reject must never leave the writer.
        // Allocating MaxPayloadBytes+1 (~65 MiB) once in a unit test is
        // wasteful but acceptable.
        using var ms = new MemoryStream();
        var oversize = new byte[WorkerWire.MaxPayloadBytes + 1];
        Should.Throw<InvalidDataException>(() => WorkerWire.WriteFrame(ms, WorkerOp.TranscribeBatch, oversize));
        ms.Length.ShouldBe(0); // nothing written — the connection stays usable
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello — em dash and ünïcode")]
    public void String_RoundTrips_IncludingNull(string? value)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            WorkerWire.WriteString(w, value);
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        WorkerWire.ReadString(r).ShouldBe(value);
    }

    [Fact]
    public void Floats_RoundTrip_RespectingCount()
    {
        var samples = new float[] { 0.5f, -1f, 0.25f, 99f };
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            WorkerWire.WriteFloats(w, samples, count: 3); // only the first 3
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        WorkerWire.ReadFloats(r).ShouldBe(new float[] { 0.5f, -1f, 0.25f });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0246: The type or namespace name 'WorkerWire' could not be found` (compile failure is the RED state for a not-yet-existing type).

- [ ] **Step 3: Implement the codec**

Create `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProtocol.cs`:

```csharp
using System.Text;

namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Wire opcodes for the transcribe.cpp worker subprocess. Requests are 1-7;
/// responses are 100+. One request always yields exactly one response.
///
/// Payload schemas (BinaryWriter/BinaryReader little-endian; strings are
/// int32 byte length (-1 = null) + UTF-8 bytes; floats are int32 count + raw
/// IEEE-754 bytes):
///   Load            = string runtimeDir, string ggufPath      -> LoadOk = string modelName
///   BeginStream     = int32 attContextRight, string? language -> BeginStreamOk = int32 gateWaitMs
///   Feed            = floats                                  -> FeedOk = string? committedText
///   FinalizeStream  = (empty)                                 -> FinalizeOk = string text, bool wasTruncated
///   DisposeStream   = (empty)                                 -> Ok (idempotent)
///   TranscribeBatch = string? language, floats                -> BatchOk = int32 gateWaitMs, string text
///   Shutdown        = (empty)                                 -> Ok, then the worker exits
///   Error           = int32 gateWaitMs, string exceptionTypeName, string message
/// </summary>
public enum WorkerOp : byte
{
    Load = 1,
    BeginStream = 2,
    Feed = 3,
    FinalizeStream = 4,
    DisposeStream = 5,
    TranscribeBatch = 6,
    Shutdown = 7,

    Ok = 100,
    LoadOk = 101,
    BeginStreamOk = 102,
    FeedOk = 103,
    FinalizeOk = 104,
    BatchOk = 105,
    Error = 110,
}

/// <summary>Length-prefixed binary framing: [byte op][int32 LE payloadLen][payload].</summary>
public static class WorkerWire
{
    /// <summary>Sanity cap: the largest legal payload is a full dictation's
    /// batch audio (minutes of 16 kHz float32); 64 MiB ≈ 17 minutes.</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    public static void WriteFrame(Stream s, WorkerOp op, byte[] payload)
    {
        // Guard the WRITE side too: an oversize frame is lethal to the peer
        // (its ReadFrame throws InvalidDataException and the process dies).
        // Failing here, before any bytes hit the stream, protects the peer
        // and leaves this connection usable.
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException(
                $"worker frame payload length {payload.Length} exceeds the {MaxPayloadBytes} cap; refusing to write a frame the peer would fatally reject");
        Span<byte> header = stackalloc byte[5];
        header[0] = (byte)op;
        BitConverter.TryWriteBytes(header[1..], payload.Length);
        s.Write(header);
        s.Write(payload, 0, payload.Length);
        s.Flush();
    }

    public static (WorkerOp Op, byte[] Payload) ReadFrame(Stream s)
    {
        Span<byte> header = stackalloc byte[5];
        ReadExactly(s, header);
        var op = (WorkerOp)header[0];
        var len = BitConverter.ToInt32(header[1..]);
        if (len < 0 || len > MaxPayloadBytes)
            throw new InvalidDataException($"worker frame payload length {len} is outside [0, {MaxPayloadBytes}]");
        var payload = new byte[len];
        if (len > 0) ReadExactly(s, payload);
        return (op, payload);
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = s.Read(buffer[read..]);
            if (n == 0) throw new EndOfStreamException("worker stream closed mid-frame");
            read += n;
        }
    }

    public static void WriteString(BinaryWriter w, string? value)
    {
        if (value is null) { w.Write(-1); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    public static string? ReadString(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len == -1) return null;
        if (len < 0 || len > MaxPayloadBytes) throw new InvalidDataException($"string length {len} out of range");
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    public static void WriteFloats(BinaryWriter w, float[] samples, int count)
    {
        w.Write(count);
        for (var i = 0; i < count; i++) w.Write(samples[i]);
    }

    public static float[] ReadFloats(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxPayloadBytes / sizeof(float))
            throw new InvalidDataException($"float count {count} out of range");
        var samples = new float[count];
        for (var i = 0; i < count; i++) samples[i] = r.ReadSingle();
        return samples;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProtocolTests"
```
Expected: all 8 facts PASS (`Errors: 0`, `Failed: 0`).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProtocol.cs tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProtocolTests.cs
git commit -m "feat(asr): worker wire protocol codec for the transcribe.cpp subprocess"
```

---

### Task 3: Worker-side request loop

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/TranscribeWorkerLoop.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/TranscribeWorkerLoopTests.cs`

**Interfaces:**
- Consumes: `WorkerOp`/`WorkerWire` (Task 2); `ITranscribeCppEngine`/`ITranscribeCppStream` (`src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs`); `TranscribeCppException` (`src/Winpepper.Asr/TranscribeCpp/TranscribeCppContract.cs`); test fake `Winpepper.Asr.Tests.Transcription.FakeTranscribeCppEngine` (`tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` — scriptable: `FinalText`, `ThrowOnBeginStream/Feed/Finalize`, `GateWaitMsToReport`, `LastBatchLanguage`, `BeginStreamLanguages`, nested `FakeStream` with `FeedCounts`, `Disposed`).
- Produces (used by Tasks 5, 7): `public static class TranscribeWorkerLoop { public static int Run(Stream input, Stream output, Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log); }` — single-threaded; hosts at most one engine and one stream; `TranscribeBatch` auto-disposes any open stream first (this replaces the bench's second-engine workaround for the compute-gate trap); returns 0 on `Shutdown` or clean EOF; all handler exceptions map to `Error` frames (`TranscribeCppException` keeps its type name so the client can rethrow it faithfully).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/TranscribeWorkerLoopTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Winpepper.Asr.Tests.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class TranscribeWorkerLoopTests
{
    /// <summary>Runs the loop over in-memory request/response buffers:
    /// requests are pre-written into the input stream, then the loop is run
    /// to completion (EOF or Shutdown), then responses are read back.</summary>
    private static List<(WorkerOp Op, byte[] Payload)> RunScript(
        FakeTranscribeCppEngine engine, params (WorkerOp Op, byte[] Payload)[] requests)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        foreach (var (op, payload) in requests) WorkerWire.WriteFrame(input, op, payload);
        input.Position = 0;
        var exit = TranscribeWorkerLoop.Run(input, output, (_, _) => engine, _ => { });
        exit.ShouldBe(0);
        output.Position = 0;
        var responses = new List<(WorkerOp, byte[])>();
        while (output.Position < output.Length) responses.Add(WorkerWire.ReadFrame(output));
        return responses;
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static byte[] LoadPayload() => Payload(w =>
    {
        WorkerWire.WriteString(w, "/runtime");
        WorkerWire.WriteString(w, "/model.gguf");
    });

    [Fact]
    public void Load_RespondsWithModelName()
    {
        var engine = new FakeTranscribeCppEngine();
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()));
        rs.Count.ShouldBe(1);
        rs[0].Op.ShouldBe(WorkerOp.LoadOk);
        using var r = new BinaryReader(new MemoryStream(rs[0].Payload));
        WorkerWire.ReadString(r).ShouldBe(engine.ModelName);
    }

    [Fact]
    public void BeginStream_Feed_Finalize_Dispose_FullSessionRoundTrip()
    {
        var engine = new FakeTranscribeCppEngine { FinalText = "hello from worker", GateWaitMsToReport = 7 };
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, "en-US"); });
        var feed = Payload(w => WorkerWire.WriteFloats(w, new float[2560], 2560));
        var rs = RunScript(engine,
            (WorkerOp.Load, LoadPayload()),
            (WorkerOp.BeginStream, begin),
            (WorkerOp.Feed, feed),
            (WorkerOp.FinalizeStream, Array.Empty<byte>()),
            (WorkerOp.DisposeStream, Array.Empty<byte>()));

        rs.Select(x => x.Op).ShouldBe(new[]
            { WorkerOp.LoadOk, WorkerOp.BeginStreamOk, WorkerOp.FeedOk, WorkerOp.FinalizeOk, WorkerOp.Ok });

        using var beginR = new BinaryReader(new MemoryStream(rs[1].Payload));
        beginR.ReadInt32().ShouldBe(7); // gateWaitMs surfaced per call

        using var finR = new BinaryReader(new MemoryStream(rs[3].Payload));
        WorkerWire.ReadString(finR).ShouldBe("hello from worker");
        finR.ReadBoolean().ShouldBeFalse();

        engine.BeginStreamLanguages.ShouldBe(new[] { "en-US" });
        engine.LastStream!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void TranscribeBatch_WhileStreamOpen_DisposesTheStreamFirst()
    {
        // The compute-gate trap: a batch on the same engine while a stream is
        // open would deadlock on the engine-wide gate. The worker's contract
        // is to dispose the open stream (releasing the gate) before batch.
        var engine = new FakeTranscribeCppEngine();
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        var batch = Payload(w => { WorkerWire.WriteString(w, null); WorkerWire.WriteFloats(w, new float[16], 16); });
        var rs = RunScript(engine,
            (WorkerOp.Load, LoadPayload()),
            (WorkerOp.BeginStream, begin),
            (WorkerOp.TranscribeBatch, batch));

        rs[2].Op.ShouldBe(WorkerOp.BatchOk);
        engine.LastStream!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void EngineThrow_MapsToErrorFrame_PreservingTranscribeCppExceptionType()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnBeginStream = true };
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.BeginStream, begin));

        rs[1].Op.ShouldBe(WorkerOp.Error);
        using var r = new BinaryReader(new MemoryStream(rs[1].Payload));
        r.ReadInt32(); // gateWaitMs
        WorkerWire.ReadString(r).ShouldBe(nameof(TranscribeCppException));
    }

    [Fact]
    public void RequestBeforeLoad_ReturnsError_NotCrash()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = Payload(w => { WorkerWire.WriteString(w, null); WorkerWire.WriteFloats(w, new float[4], 4); });
        var rs = RunScript(engine, (WorkerOp.TranscribeBatch, batch));
        rs[0].Op.ShouldBe(WorkerOp.Error);
    }

    [Fact]
    public void Shutdown_RespondsOk_DisposesEngine_AndExitsZero()
    {
        var engine = new FakeTranscribeCppEngine();
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.Shutdown, Array.Empty<byte>()));
        rs[1].Op.ShouldBe(WorkerOp.Ok);
        engine.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void CleanEof_DisposesEngineAndStream_AndExitsZero()
    {
        var engine = new FakeTranscribeCppEngine();
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.BeginStream, begin));
        engine.LastStream!.Disposed.ShouldBeTrue(); // crashed/vanished client frees the gate
        engine.Disposed.ShouldBeTrue();
    }
}
```

Note: if `FakeTranscribeCppEngine` lacks a `Disposed` property on the engine itself (check `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` — it has `Disposed` per its summary; if it is only on `FakeStream`, add `public bool Disposed { get; private set; }` set from `Dispose()` to the fake in this task).

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAILS with `CS0246 ... 'TranscribeWorkerLoop'`.

- [ ] **Step 3: Implement the loop**

Create `src/Winpepper.Asr/TranscribeCpp/Worker/TranscribeWorkerLoop.cs`:

```csharp
namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Worker-side request loop. Hosts ONE engine and at most ONE stream,
/// single-threaded (transcribe.cpp allows one compute in flight per model, so
/// serialization is free correctness). Every request produces exactly one
/// response frame. TranscribeBatch auto-disposes an open stream first: the
/// engine-wide compute gate is held for a stream's lifetime, so a same-engine
/// batch while a stream is open would stall 5 s and throw (the bench's
/// documented trap, scripts/asr-latency-bench/Program.cs:769-776) — worker
/// restart/dispose is the subprocess replacement for the bench's second
/// engine. EOF (client died) and Shutdown both dispose stream + engine so a
/// vanished client can never leave the gate held.
/// </summary>
public static class TranscribeWorkerLoop
{
    public static int Run(Stream input, Stream output,
        Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log)
    {
        ITranscribeCppEngine? engine = null;
        ITranscribeCppStream? stream = null;
        try
        {
            while (true)
            {
                WorkerOp op;
                byte[] payload;
                try { (op, payload) = WorkerWire.ReadFrame(input); }
                catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
                {
                    log("worker input closed; exiting");
                    return 0;
                }

                if (op == WorkerOp.Shutdown)
                {
                    WorkerWire.WriteFrame(output, WorkerOp.Ok, Array.Empty<byte>());
                    return 0;
                }

                byte[] response;
                WorkerOp responseOp;
                try
                {
                    (responseOp, response) = Handle(op, payload, engineFactory, log, ref engine, ref stream);
                }
                catch (Exception ex)
                {
                    (responseOp, response) = (WorkerOp.Error, ErrorPayload(0, ex));
                }
                try { WorkerWire.WriteFrame(output, responseOp, response); }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    log("worker output closed; exiting");
                    return 0;
                }
            }
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* releasing on the way out */ }
            try { engine?.Dispose(); } catch { /* releasing on the way out */ }
        }
    }

    private static (WorkerOp, byte[]) Handle(WorkerOp op, byte[] payload,
        Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log,
        ref ITranscribeCppEngine? engine, ref ITranscribeCppStream? stream)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);

        switch (op)
        {
            case WorkerOp.Load:
            {
                var runtimeDir = WorkerWire.ReadString(r)!;
                var ggufPath = WorkerWire.ReadString(r)!;
                engine?.Dispose();
                engine = engineFactory(runtimeDir, ggufPath);
                var modelName = engine.ModelName; // copy to a local: `engine` is a ref parameter and cannot be captured in the lambda (CS1628)
                return (WorkerOp.LoadOk, Build(w => WorkerWire.WriteString(w, modelName)));
            }
            case WorkerOp.BeginStream:
            {
                RequireEngine(engine);
                var attContextRight = r.ReadInt32();
                var language = WorkerWire.ReadString(r);
                stream?.Dispose();
                var gateWaitMs = 0;
                try { stream = engine!.BeginStream(attContextRight, language, out gateWaitMs); }
                catch (Exception ex) { return (WorkerOp.Error, ErrorPayload(gateWaitMs, ex)); }
                var wait = gateWaitMs;
                return (WorkerOp.BeginStreamOk, Build(w => w.Write(wait)));
            }
            case WorkerOp.Feed:
            {
                RequireStream(stream);
                var samples = WorkerWire.ReadFloats(r);
                var committed = stream!.Feed(samples, samples.Length);
                return (WorkerOp.FeedOk, Build(w => WorkerWire.WriteString(w, committed)));
            }
            case WorkerOp.FinalizeStream:
            {
                RequireStream(stream);
                var (text, wasTruncated) = stream!.Finalize();
                return (WorkerOp.FinalizeOk, Build(w => { WorkerWire.WriteString(w, text); w.Write(wasTruncated); }));
            }
            case WorkerOp.DisposeStream:
            {
                stream?.Dispose();
                stream = null;
                return (WorkerOp.Ok, Array.Empty<byte>());
            }
            case WorkerOp.TranscribeBatch:
            {
                RequireEngine(engine);
                var language = WorkerWire.ReadString(r);
                var samples = WorkerWire.ReadFloats(r);
                if (stream is not null)
                {
                    log("batch requested while a stream is open; disposing the stream to release the compute gate");
                    stream.Dispose();
                    stream = null;
                }
                var gateWaitMs = 0;
                string text;
                try { text = engine!.TranscribeBatch(samples, language, out gateWaitMs); }
                catch (Exception ex) { return (WorkerOp.Error, ErrorPayload(gateWaitMs, ex)); }
                var wait = gateWaitMs;
                return (WorkerOp.BatchOk, Build(w => { w.Write(wait); WorkerWire.WriteString(w, text); }));
            }
            default:
                throw new InvalidOperationException($"unknown worker request op {op}");
        }
    }

    private static void RequireEngine(ITranscribeCppEngine? engine)
    {
        if (engine is null) throw new InvalidOperationException("worker engine not loaded (send Load first)");
    }

    private static void RequireStream(ITranscribeCppStream? stream)
    {
        if (stream is null) throw new InvalidOperationException("no open stream (send BeginStream first)");
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static byte[] ErrorPayload(int gateWaitMs, Exception ex) => Build(w =>
    {
        w.Write(gateWaitMs);
        WorkerWire.WriteString(w, ex.GetType().Name);
        WorkerWire.WriteString(w, ex.Message);
    });
}
```

- [ ] **Step 4: Run the new tests, expect PASS**

```bash
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Asr.Tests.TranscribeCpp.Worker.TranscribeWorkerLoopTests"
```
Expected: all facts PASS.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add -A src/Winpepper.Asr/TranscribeCpp/Worker tests/Winpepper.Asr.Tests/TranscribeCpp/Worker tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs
git commit -m "feat(asr): transcribe.cpp worker-side request loop with gate-safe batch"
```

---

### Task 4: Worker restart policy (pure)

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerRestartPolicy.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerRestartPolicyTests.cs`

**Interfaces:**
- Consumes: nothing (pure; injectable clock).
- Produces (used by Task 5): `public sealed class WorkerRestartPolicy { public WorkerRestartPolicy(int maxConsecutiveFailures = 3, TimeSpan? cooldown = null, Func<long>? nowMs = null); public bool CanAttempt(); public void NoteFailure(); public void NoteSuccess(); }` — after `maxConsecutiveFailures` consecutive failures, `CanAttempt()` is false until `cooldown` (default 60 s) has elapsed since the last failure; one attempt is then allowed per elapsed cooldown window; `NoteSuccess()` resets everything. This replaces `NemotronEngineHolder`'s latch-forever with kill→respawn→retry-with-backoff.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerRestartPolicyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerRestartPolicyTests
{
    [Fact]
    public void FreshPolicy_AllowsAttempts()
    {
        var p = new WorkerRestartPolicy();
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void FailuresBelowBudget_StillAllowAttempts()
    {
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 3, nowMs: () => 0);
        p.NoteFailure();
        p.NoteFailure();
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void BudgetExhausted_BlocksUntilCooldownElapses()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 3, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        p.NoteFailure(); p.NoteFailure(); p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        now = 59_999;
        p.CanAttempt().ShouldBeFalse();
        now = 60_000;
        p.CanAttempt().ShouldBeTrue(); // one attempt per cooldown window
    }

    [Fact]
    public void FailureAfterCooldownAttempt_BlocksAgainForAnotherCooldown()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        now = 60_000;
        p.CanAttempt().ShouldBeTrue();
        p.NoteFailure(); // the retry failed too
        now = 60_001;
        p.CanAttempt().ShouldBeFalse();
        now = 120_000;
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void Success_ResetsTheBudget()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 2, nowMs: () => now);
        p.NoteFailure(); p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        p.NoteSuccess();
        p.CanAttempt().ShouldBeTrue();
        p.NoteFailure();
        p.CanAttempt().ShouldBeTrue(); // count restarted from zero
    }
}
```

- [ ] **Step 2: Run to verify failure** — same build command as Task 2 Step 2; expected `CS0246 ... 'WorkerRestartPolicy'`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerRestartPolicy.cs`:

```csharp
namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Kill→respawn→retry budget for the worker engine. Replaces the old
/// NemotronEngineHolder latch-forever: a broken runtime still fails loudly
/// per dictation, but a transient wedge (or a fixed install) recovers without
/// an app restart. After N consecutive failures, one attempt is allowed per
/// cooldown window (default 60 s) until a success resets the count.
/// Not thread-safe on its own — WorkerProcessEngine calls it under its RPC lock.
/// </summary>
public sealed class WorkerRestartPolicy
{
    private readonly int _maxConsecutiveFailures;
    private readonly long _cooldownMs;
    private readonly Func<long> _nowMs;
    private int _consecutiveFailures;
    private long _lastFailureMs;

    public WorkerRestartPolicy(int maxConsecutiveFailures = 3, TimeSpan? cooldown = null, Func<long>? nowMs = null)
    {
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _cooldownMs = (long)(cooldown ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public bool CanAttempt()
        => _consecutiveFailures < _maxConsecutiveFailures
           || _nowMs() - _lastFailureMs >= _cooldownMs;

    public void NoteFailure()
    {
        _consecutiveFailures++;
        _lastFailureMs = _nowMs();
    }

    public void NoteSuccess() => _consecutiveFailures = 0;
}
```

- [ ] **Step 4: Run the new tests, expect PASS** (`-class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerRestartPolicyTests"`).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.Asr/TranscribeCpp/Worker/WorkerRestartPolicy.cs tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerRestartPolicyTests.cs
git commit -m "feat(asr): worker restart budget/cooldown policy"
```

---

### Task 5: Client-side worker engine proxy with supervision

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcess.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerEngineOptions.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs` (test double)
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs`

**Interfaces:**
- Consumes: Tasks 2–4; `ITranscribeCppEngine`, `TranscribeCppException`; `NemotronStreamingTranscriber` (unchanged, for the end-to-end wedge test); `FakeTranscribeCppEngine`.
- Produces (used by Tasks 6, 7):

```csharp
namespace Winpepper.Asr.TranscribeCpp.Worker;

public interface IWorkerProcess : IDisposable
{
    Stream Input { get; }    // client writes requests here (worker's stdin)
    Stream Output { get; }   // client reads responses here (worker's stdout)
    bool HasExited { get; }
    void Kill();
}

public interface IWorkerProcessFactory
{
    IWorkerProcess Start();
}

public sealed record WorkerEngineOptions
{
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BeginStreamTimeout { get; init; } = TimeSpan.FromSeconds(15); // 5 s gate + native begin headroom
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FinalizeTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(120); // FLOOR — the engine raises the per-call batch deadline to max(this, 30 s + 2 s per audio-second)
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

public sealed class WorkerProcessEngine : ITranscribeCppEngine
{
    public WorkerProcessEngine(IWorkerProcessFactory factory, string runtimeDir, string ggufPath,
        string modelName, WorkerEngineOptions? options = null,
        WorkerRestartPolicy? restartPolicy = null, Action<string>? log = null);
    public string ModelName { get; }   // the passed modelName (e.g. "nemotron-streaming-en")
    public ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs);
    public string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs);
    public void Dispose();             // best-effort Shutdown RPC, then Kill; latches disposed (later ops throw ObjectDisposedException, never respawn)
}
```

  Supervision contract (binding): every RPC failure (timeout, process exit, EOF, protocol error) kills the worker, invalidates any open stream proxy (its later Feed/Finalize throw `TranscribeCppException`; its Dispose is a no-op), and throws `TranscribeCppException` to the caller. The NEXT engine call respawns + reloads lazily, subject to `WorkerRestartPolicy`. Worker `Error` frames whose type name is `TranscribeCppException` rethrow as `TranscribeCppException`; other worker exceptions rethrow as `TranscribeCppException` with the type name prefixed in the message. All RPCs are serialized under one client-side lock. Log lines (via the `log` callback): `"speech worker started"`, `"speech worker load ok ({model})"`, `"speech worker killed: {op} timed out after {ms} ms"`, `"speech worker died: {reason}"`, `"speech worker restart blocked by budget; next attempt after cooldown"`.

  Additional contracts (2026-08-08 validation hardening): (a) **disposed stays dead** — `Dispose()` sets a `_disposed` latch under the RPC lock; `EnsureWorkerLocked` (and thus every later op) throws `ObjectDisposedException(nameof(WorkerProcessEngine))` and never respawns (a live dictation's captured old engine fails over through the batch ladder instead of resurrecting an old-layout worker). (b) **oversize pre-check** — `TranscribeBatch` checks the encoded floats payload against `WorkerWire.MaxPayloadBytes` BEFORE any RPC and throws `InvalidOperationException("dictation too long for the local batch engine (> ~17 minutes); shorten the recording")` without touching the worker (the ladder's `FallbackTranscriber` catches it → Parakeet when installed). (c) **length-aware batch deadline** — the batch RPC uses `max(BatchTimeout, 30 s + 2 s per audio-second)` instead of the fixed `BatchTimeout` (a cap-sized batch measured ~106 s on the dev host vs the fixed 120 s — only 1.13× headroom; 2 s/audio-second covers worst-case RTF≈2 low-end hardware). (d) **A1 residual, accepted (text corrected 2026-08-09)**: killing a worker wedged in kernel-mode I/O may delay its actual exit and leak one blocked threadpool thread per kill. The Task 6 Job Object does NOT reap such zombies at app exit — the supervised path closes the per-worker job handle at kill time (`KillLocked` → `ExeWorkerProcess.Dispose`), so KILL_ON_JOB_CLOSE fires then, and a kernel-wedged worker that survives it can linger until the kernel operation completes or the OS cleans up; the job's at-exit guarantee covers only parent CRASH (the kernel closes the orphaned handle). All job guarantees are conditional on the bind succeeding — bind failures are logged as of the 2026-08-09 fix batch. The respawn side is genuinely bounded as of the 2026-08-09 fix batch (`docs/plans/2026-08-09-nemotron-first-asr.md`): operation-phase kills and between-RPC worker deaths charge the 3-strike/60 s restart budget, and only a completed operation RPC (finished batch / finalized stream) resets it.

- [ ] **Step 1: Write the in-process channel test double**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs`:

```csharp
using System.IO.Pipes;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

/// <summary>
/// Runs the REAL TranscribeWorkerLoop on a background thread over anonymous
/// pipes — full client↔worker integration without a child process. Kill()
/// tears the pipes down WRITE-ENDS-FIRST. NOTE this is NOT identical to real
/// process death: killing a real child closes the PEER's (child's) ends, and
/// only peer-WRITE-end closure unblocks a blocked read. On Unix, disposing a
/// read end with an in-flight blocked read blocks the DISPOSER until the peer
/// write end closes (socket-wrapped pipe fds) — hence the strict order below
/// (V7: 300/300 clean vs a deterministic deadlock with read-ends-first).
/// The factory counts started channels so tests can assert respawns.
/// </summary>
public sealed class InProcessWorkerChannel : IWorkerProcess
{
    private readonly AnonymousPipeServerStream _toWorker;
    private readonly AnonymousPipeClientStream _workerIn;
    private readonly AnonymousPipeServerStream _fromWorker;
    private readonly AnonymousPipeClientStream _workerOut;
    private readonly Thread _thread;
    private volatile bool _exited;

    public InProcessWorkerChannel(Func<ITranscribeCppEngine> engineFactory)
    {
        _toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        _workerIn = new AnonymousPipeClientStream(PipeDirection.In, _toWorker.ClientSafePipeHandle);
        _fromWorker = new AnonymousPipeServerStream(PipeDirection.In);
        _workerOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromWorker.ClientSafePipeHandle);
        _thread = new Thread(() =>
        {
            try { TranscribeWorkerLoop.Run(_workerIn, _workerOut, (_, _) => engineFactory(), _ => { }); }
            catch { /* pipe torn down by Kill */ }
            finally { _exited = true; }
        }) { IsBackground = true };
        _thread.Start();
    }

    public Stream Input => _toWorker;
    public Stream Output => _fromWorker;
    public bool HasExited => _exited;

    public void Kill()
    {
        _exited = true;
        // WRITE ends FIRST — each unblocks the opposite side's blocked read
        // (EOF / IO fault). Disposing a read end while a read is in flight
        // would block THIS thread until its peer write end closes (V7).
        _toWorker.Dispose();   // client->worker write end: the worker's ReadFrame EOFs
        _workerOut.Dispose();  // worker->client write end: the client's deadline'd ReadFrame unblocks
        _fromWorker.Dispose();
        _workerIn.Dispose();
    }

    public void Dispose() => Kill();
}

public sealed class InProcessWorkerChannelFactory : IWorkerProcessFactory
{
    private readonly Func<ITranscribeCppEngine> _engineFactory;
    public InProcessWorkerChannelFactory(Func<ITranscribeCppEngine> engineFactory) => _engineFactory = engineFactory;
    public int Started { get; private set; }
    public InProcessWorkerChannel? Last { get; private set; }
    public IWorkerProcess Start()
    {
        Started++;
        Last = new InProcessWorkerChannel(_engineFactory);
        return Last;
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerProcessEngineTests
{
    private static readonly WorkerEngineOptions FastTimeouts = new()
    {
        LoadTimeout = TimeSpan.FromSeconds(5),
        BeginStreamTimeout = TimeSpan.FromSeconds(5),
        FeedTimeout = TimeSpan.FromMilliseconds(300),
        FinalizeTimeout = TimeSpan.FromSeconds(5),
        BatchTimeout = TimeSpan.FromSeconds(5),
        DisposeTimeout = TimeSpan.FromMilliseconds(300),
    };

    private static WorkerProcessEngine Engine(InProcessWorkerChannelFactory factory,
        WorkerRestartPolicy? policy = null)
        => new(factory, "/runtime", "/model.gguf", "nemotron-streaming-en", FastTimeouts, policy);

    [Fact]
    public void StreamRoundTrip_ThroughRealLoop_ReturnsFinalTextAndGateWait()
    {
        var fake = new FakeTranscribeCppEngine { FinalText = "worker says hi", GateWaitMsToReport = 3 };
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);

        using var stream = engine.BeginStream(13, "en-US", out var gateWaitMs);
        gateWaitMs.ShouldBe(3);
        stream.Feed(new float[2560], 2560);
        var (text, truncated) = stream.Finalize();
        text.ShouldBe("worker says hi");
        truncated.ShouldBeFalse();
        factory.Started.ShouldBe(1);
    }

    [Fact]
    public void Batch_RoundTrip_ReturnsText()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        var text = engine.TranscribeBatch(new float[16], null, out _);
        text.ShouldNotBeNull();
    }

    [Fact]
    public void WorkerException_SurfacesAsTranscribeCppException()
    {
        var fake = new FakeTranscribeCppEngine { ThrowOnBeginStream = true };
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        Should.Throw<TranscribeCppException>(() => engine.BeginStream(13, null, out _));
    }

    [Fact]
    public void WedgedFeed_TimesOut_KillsWorker_AndNextCallRespawns()
    {
        using var feedGate = new ManualResetEventSlim(false); // deterministic wedge
        var first = true;
        InProcessWorkerChannelFactory factory = null!;
        factory = new InProcessWorkerChannelFactory(() =>
        {
            if (first) { first = false; return new FakeTranscribeCppEngine { FeedGate = feedGate }; }
            return new FakeTranscribeCppEngine(); // the respawned worker is healthy
        });

        using var engine = Engine(factory);
        using var stream = engine.BeginStream(13, null, out _);

        Should.Throw<TranscribeCppException>(() => stream.Feed(new float[2560], 2560)); // times out at 300 ms
        factory.Last!.HasExited.ShouldBeTrue(); // the wedged worker was killed

        // Next engine call transparently respawns a fresh worker:
        var text = engine.TranscribeBatch(new float[16], null, out _);
        text.ShouldNotBeNull();
        factory.Started.ShouldBe(2);
        feedGate.Set(); // release the wedged background thread
    }

    [Fact]
    public void StreamProxy_AfterWorkerDeath_ThrowsOnUse_AndDisposeIsBenign()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        using var engine = Engine(factory);
        var stream = engine.BeginStream(13, null, out _);
        factory.Last!.Kill();

        Should.Throw<TranscribeCppException>(() => stream.Finalize());
        Should.NotThrow(() => stream.Dispose());
    }

    [Fact]
    public void RestartBudgetExhausted_ThrowsWithoutSpawning()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        var factory = new InProcessWorkerChannelFactory(FailingEngine);
        using var engine = Engine(factory, policy);

        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        var spawnsAfterFirstFailure = factory.Started;

        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        factory.Started.ShouldBe(spawnsAfterFirstFailure); // budget blocked the respawn

        now = 60_000;
        Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[4], null, out _));
        factory.Started.ShouldBe(spawnsAfterFirstFailure + 1); // cooldown elapsed -> one retry

        static ITranscribeCppEngine FailingEngine() => throw new TranscribeCppException("model load failed");
    }

    [Fact]
    public void Dispose_ThenCall_ThrowsObjectDisposed_AndDoesNotRespawn()
    {
        var fake = new FakeTranscribeCppEngine();
        var factory = new InProcessWorkerChannelFactory(() => fake);
        var engine = Engine(factory);
        engine.TranscribeBatch(new float[16], null, out _); // spawn + load once (also settles the reader before Kill)
        var startedBeforeDispose = factory.Started;

        engine.Dispose();

        Should.Throw<ObjectDisposedException>(() => engine.TranscribeBatch(new float[16], null, out _));
        Should.Throw<ObjectDisposedException>(() => engine.BeginStream(13, null, out _));
        factory.Started.ShouldBe(startedBeforeDispose); // a disposed engine NEVER respawns a worker
    }

    [Fact]
    public void Batch_OversizeAudio_ThrowsInvalidOperation_WithoutTouchingTheWorker()
    {
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory);
        // Just over the 64 MiB frame cap (~17 min at 16 kHz). One ~67 MB array
        // in a unit test is wasteful but acceptable.
        var oversize = new float[WorkerWire.MaxPayloadBytes / sizeof(float) + 1];
        var ex = Should.Throw<InvalidOperationException>(() => engine.TranscribeBatch(oversize, null, out _));
        ex.Message.ShouldContain("dictation too long");
        factory.Started.ShouldBe(0); // the pre-check fired before any spawn/RPC
    }

    /// <summary>The headline scenario the subprocess exists for: a wedged
    /// native feed no longer wedges the app — the streaming transcriber falls
    /// back to batch on a FRESH worker and the dictation still yields text.</summary>
    [Fact]
    public async Task EndToEnd_WedgedStream_FallsBackToNemotronBatch_OnFreshWorker()
    {
        using var feedGate = new ManualResetEventSlim(false);
        var first = true;
        var factory = new InProcessWorkerChannelFactory(() =>
        {
            if (first) { first = false; return new FakeTranscribeCppEngine { FeedGate = feedGate }; }
            return new FakeTranscribeCppEngine(); // the respawned worker is healthy
        });
        using var engine = Engine(factory);
        var batch = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-en-batch");
        var streaming = new NemotronStreamingTranscriber(() => engine, batch, "nemotron-streaming-en",
            NullLogger<NemotronStreamingTranscriber>.Instance);

        await using var session = await streaming.StartSessionAsync(CancellationToken.None);
        await session.PushAsync(new float[2560], CancellationToken.None); // wedges -> times out -> corrupt
        var result = await session.FinishAsync(new float[2560], CancellationToken.None);

        result.ProviderModelName.ShouldBe("nemotron-streaming-en-batch"); // batch fallback produced it
        factory.Started.ShouldBe(2); // wedged worker killed, fresh worker served the batch
        feedGate.Set();
    }
}
```

Notes for the implementer:
- The commented awkwardness inside `WedgedFeed_TimesOut_...` shows intent; write it cleanly with a `first` flag exactly like `EndToEnd_WedgedStream_...` does. Keep the `ManualResetEventSlim` pattern (30 s-bounded waits inside the fake already prevent runner hangs).
- Kill-based scenarios (worker death, dispose) must run AFTER a first successful RPC so the channel's reader is settled — avoids a rare cold-start race in the pipe double (V7: 2/600 hangs only when a reader's very first read raced Kill). The tests above already follow this.
- The length-aware batch deadline makes the EFFECTIVE batch deadline under `FastTimeouts` `max(5 s, ~30 s)` — none of these tests wedge a BATCH call (only Feed wedges, bounded by `FeedTimeout`), so nothing changes; do not add a batch-wedge test against `FastTimeouts`. The deadline computation is private — the oversize pre-check test above is the unit-level proof for the A9 fix.
- The end-to-end test needs `NemotronBatchTranscriber`, which is written in Task 8. Add this ONE test in Task 8 instead if you prefer strictly compiling tests per task — but it must exist by the end of Task 8. Everything else in this file compiles in this task.

- [ ] **Step 3: Run to verify failure** — build `Winpepper.Asr.Tests`; expected `CS0246` for `WorkerProcessEngine`/`IWorkerProcess`. (Comment out the end-to-end test until Task 8 if you deferred it.)

- [ ] **Step 4: Implement `WorkerProcess.cs`, `WorkerEngineOptions.cs`, `WorkerProcessEngine.cs`**

`src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcess.cs`:

```csharp
namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>A running worker's stdio + lifecycle, abstracted so supervision
/// logic is testable without child processes (see InProcessWorkerChannel in
/// tests and ExeWorkerProcess for the real thing).</summary>
public interface IWorkerProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    bool HasExited { get; }
    void Kill();
}

public interface IWorkerProcessFactory
{
    IWorkerProcess Start();
}
```

`src/Winpepper.Asr/TranscribeCpp/Worker/WorkerEngineOptions.cs`:

```csharp
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
```

`src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs`:

```csharp
namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Client-side ITranscribeCppEngine over a worker subprocess. Keeps the
/// existing engine seam so NemotronStreamingTranscriber, StreamingRouteGuard,
/// and their tests are untouched; what changes is that a wedged native call
/// is now KILLABLE (RPC deadline -> Kill) and the engine RESTARTABLE (lazy
/// respawn on the next call, bounded by WorkerRestartPolicy).
///
/// Failure contract: any RPC failure kills the worker, invalidates open
/// stream proxies, and throws TranscribeCppException — the exact exception
/// the in-process engine used, so every existing fallback path just works.
/// Two deliberate exceptions: Dispose() latches the engine DEAD (later calls
/// throw ObjectDisposedException and never respawn — a retained reference
/// across a model swap must not resurrect an old-layout worker), and the
/// oversize batch pre-check throws InvalidOperationException WITHOUT touching
/// the worker (the ladder's FallbackTranscriber routes it to Parakeet).
/// </summary>
public sealed class WorkerProcessEngine : ITranscribeCppEngine
{
    private readonly IWorkerProcessFactory _factory;
    private readonly string _runtimeDir;
    private readonly string _ggufPath;
    private readonly WorkerEngineOptions _options;
    private readonly WorkerRestartPolicy _restartPolicy;
    private readonly Action<string>? _log;
    private readonly object _rpcGate = new();

    private IWorkerProcess? _proc;
    private int _generation; // bumped on every kill; stream proxies check it
    private bool _disposed;  // set by Dispose(); a disposed engine never respawns (V5/A10)

    public WorkerProcessEngine(IWorkerProcessFactory factory, string runtimeDir, string ggufPath,
        string modelName, WorkerEngineOptions? options = null,
        WorkerRestartPolicy? restartPolicy = null, Action<string>? log = null)
    {
        _factory = factory;
        _runtimeDir = runtimeDir;
        _ggufPath = ggufPath;
        ModelName = modelName;
        _options = options ?? new WorkerEngineOptions();
        _restartPolicy = restartPolicy ?? new WorkerRestartPolicy();
        _log = log;
    }

    public string ModelName { get; }

    public ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs)
    {
        lock (_rpcGate)
        {
            EnsureWorkerLocked();
            var payload = Build(w => { w.Write(attContextRight); WorkerWire.WriteString(w, language); });
            var (op, response) = RpcLocked(WorkerOp.BeginStream, payload, _options.BeginStreamTimeout);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out gateWaitMs);
            gateWaitMs = r.ReadInt32();
            return new WorkerStream(this, _generation);
        }
    }

    public string TranscribeBatch(float[] mono16k, string? language, out int gateWaitMs)
    {
        // Oversize pre-check BEFORE any RPC or spawn: a frame above the wire
        // cap would kill the worker (fatal InvalidDataException in its reader).
        // Throwing InvalidOperationException (not TranscribeCppException) here
        // lets the ladder's FallbackTranscriber route to Parakeet when installed.
        if ((long)mono16k.Length * sizeof(float) + 64 > WorkerWire.MaxPayloadBytes)
            throw new InvalidOperationException(
                "dictation too long for the local batch engine (> ~17 minutes); shorten the recording");
        lock (_rpcGate)
        {
            EnsureWorkerLocked();
            var payload = Build(w => { WorkerWire.WriteString(w, language); WorkerWire.WriteFloats(w, mono16k, mono16k.Length); });
            // Length-aware deadline: BatchTimeout is a FLOOR. A cap-sized batch
            // measured ~106 s on the dev host vs the fixed 120 s (1.13x headroom);
            // 2 s per audio-second covers worst-case RTF~2 low-end hardware.
            var batchDeadline = TimeSpan.FromSeconds(Math.Max(
                _options.BatchTimeout.TotalSeconds, 30 + 2.0 * (mono16k.Length / 16000.0)));
            var (op, response) = RpcLocked(WorkerOp.TranscribeBatch, payload, batchDeadline);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out gateWaitMs);
            gateWaitMs = r.ReadInt32();
            return WorkerWire.ReadString(r) ?? "";
        }
    }

    public void Dispose()
    {
        lock (_rpcGate)
        {
            if (_disposed) return;
            _disposed = true; // latch: EnsureWorkerLocked refuses to respawn from now on
            if (_proc is { HasExited: false })
            {
                try { RpcLocked(WorkerOp.Shutdown, Array.Empty<byte>(), _options.DisposeTimeout); }
                catch { /* shutdown is best-effort; Kill below is the guarantee */ }
            }
            KillLocked("dispose");
        }
    }

    // ---- internals -------------------------------------------------------

    private void EnsureWorkerLocked()
    {
        // A disposed engine must stay dead: without this latch a retained
        // reference (e.g. a live dictation captured across a model swap)
        // would silently respawn a worker for the OLD layout (V5/A10).
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerProcessEngine));
        if (_proc is { HasExited: false }) return;
        if (!_restartPolicy.CanAttempt())
        {
            _log?.Invoke("speech worker restart blocked by budget; next attempt after cooldown");
            throw new TranscribeCppException("speech worker restart budget exhausted; retrying after cooldown");
        }
        try
        {
            _proc = _factory.Start();
            _log?.Invoke("speech worker started");
            var payload = Build(w => { WorkerWire.WriteString(w, _runtimeDir); WorkerWire.WriteString(w, _ggufPath); });
            var (op, response) = RpcLocked(WorkerOp.Load, payload, _options.LoadTimeout);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out _);
            var loadedName = WorkerWire.ReadString(r);
            _restartPolicy.NoteSuccess();
            _log?.Invoke($"speech worker load ok ({loadedName})");
        }
        catch (Exception e)
        {
            _restartPolicy.NoteFailure();
            KillLocked($"load failed: {e.Message}");
            throw e as TranscribeCppException
                  ?? new TranscribeCppException($"speech worker failed to start: {e.Message}");
        }
    }

    /// <summary>One request -> one response, bounded by a deadline. On ANY
    /// failure the worker is killed (a connection that missed a deadline can
    /// never be reused: a late response would answer the wrong request).</summary>
    private (WorkerOp Op, byte[] Payload) RpcLocked(WorkerOp op, byte[] payload, TimeSpan timeout)
    {
        var proc = _proc ?? throw new TranscribeCppException("speech worker is not running");
        try
        {
            WorkerWire.WriteFrame(proc.Input, op, payload);
            var read = Task.Run(() => WorkerWire.ReadFrame(proc.Output));
            if (!read.Wait(timeout))
            {
                KillLocked($"{op} timed out after {(int)timeout.TotalMilliseconds} ms");
                throw new TranscribeCppException(
                    $"speech worker did not respond to {op} within {(int)timeout.TotalSeconds} s; worker killed and will restart on the next call");
            }
            return read.Result;
        }
        catch (TranscribeCppException) { throw; }
        catch (Exception e)
        {
            var inner = (e as AggregateException)?.InnerException ?? e;
            KillLocked($"{op} failed: {inner.Message}");
            throw new TranscribeCppException($"speech worker connection failed during {op}: {inner.Message}");
        }
    }

    private void KillLocked(string reason)
    {
        if (_proc is null) return;
        _log?.Invoke($"speech worker killed: {reason}");
        _generation++;
        try { _proc.Kill(); } catch { /* already dead */ }
        try { _proc.Dispose(); } catch { /* already dead */ }
        _proc = null;
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] payload)
        => new(new MemoryStream(payload), System.Text.Encoding.UTF8);

    private static TranscribeCppException ReadError(BinaryReader r, out int gateWaitMs)
    {
        gateWaitMs = r.ReadInt32();
        var type = WorkerWire.ReadString(r);
        var message = WorkerWire.ReadString(r) ?? "unknown worker error";
        return type == nameof(TranscribeCppException)
            ? new TranscribeCppException(message)
            : new TranscribeCppException($"{type}: {message}");
    }

    /// <summary>Per-dictation stream proxy. Bound to the worker generation it
    /// was created under: after a kill/respawn it throws on use (the stream
    /// state died with the worker) and disposes as a no-op.</summary>
    private sealed class WorkerStream : ITranscribeCppStream
    {
        private readonly WorkerProcessEngine _owner;
        private readonly int _generation;
        private bool _disposed;

        internal WorkerStream(WorkerProcessEngine owner, int generation)
        {
            _owner = owner;
            _generation = generation;
        }

        public string? Feed(float[] samples, int count)
        {
            lock (_owner._rpcGate)
            {
                ThrowIfLostLocked();
                var payload = Build(w => WorkerWire.WriteFloats(w, samples, count));
                var (op, response) = _owner.RpcLocked(WorkerOp.Feed, payload, _owner._options.FeedTimeout);
                using var r = Reader(response);
                if (op == WorkerOp.Error) throw ReadError(r, out _);
                return WorkerWire.ReadString(r);
            }
        }

        public (string Text, bool WasTruncated) Finalize()
        {
            lock (_owner._rpcGate)
            {
                ThrowIfLostLocked();
                var (op, response) = _owner.RpcLocked(WorkerOp.FinalizeStream, Array.Empty<byte>(), _owner._options.FinalizeTimeout);
                using var r = Reader(response);
                if (op == WorkerOp.Error) throw ReadError(r, out _);
                return (WorkerWire.ReadString(r) ?? "", r.ReadBoolean());
            }
        }

        public void Dispose()
        {
            lock (_owner._rpcGate)
            {
                if (_disposed) return;
                _disposed = true;
                if (_generation != _owner._generation || _owner._proc is not { HasExited: false })
                    return; // the stream died with its worker; nothing to release
                try { _owner.RpcLocked(WorkerOp.DisposeStream, Array.Empty<byte>(), _owner._options.DisposeTimeout); }
                catch { /* a failed dispose already killed the worker, which also frees the gate */ }
            }
        }

        private void ThrowIfLostLocked()
        {
            if (_disposed) throw new TranscribeCppException("stream already disposed");
            if (_generation != _owner._generation || _owner._proc is not { HasExited: false })
                throw new TranscribeCppException("stream lost: the speech worker was restarted");
        }
    }
}
```

- [ ] **Step 5: Run the new tests, expect PASS** (`-class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProcessEngineTests"`).

- [ ] **Step 6: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add -A src/Winpepper.Asr/TranscribeCpp/Worker tests/Winpepper.Asr.Tests/TranscribeCpp/Worker
git commit -m "feat(asr): worker-process engine proxy with kill/respawn supervision"
```

---

### Task 6: Real subprocess implementation

**Files:**
- Create: `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/ExeWorkerProcessTests.cs`

**Interfaces:**
- Consumes: `IWorkerProcess`/`IWorkerProcessFactory` (Task 5).
- Produces (used by Task 7):

```csharp
public sealed class ExeWorkerProcess : IWorkerProcess
{
    public static ExeWorkerProcess Start(System.Diagnostics.ProcessStartInfo psi, Action<string>? onStderrLine = null);
    // Input = process StandardInput.BaseStream; Output = StandardOutput.BaseStream;
    // stderr lines forwarded to onStderrLine; Kill() = Process.Kill(entireProcessTree: true).
    // On Windows the child is additionally bound to a Job Object with
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE at start (handle held for the worker's
    // lifetime); no-op on Linux so the Linux tests are unaffected.
}

public sealed class ExeWorkerProcessFactory : IWorkerProcessFactory
{
    public ExeWorkerProcessFactory(Func<System.Diagnostics.ProcessStartInfo> psi, Action<string>? onStderrLine = null);
    public IWorkerProcess Start();
}
```

- [ ] **Step 1: Write the failing tests** (portable: a long-sleeping child on both OSes)

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/ExeWorkerProcessTests.cs`:

```csharp
using System.Diagnostics;
using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class ExeWorkerProcessTests
{
    /// <summary>A child that lives ~60 s unless killed, with stdio redirected.
    /// Windows: `cmd /c ping` (n pings ≈ n-1 seconds); Linux: `sleep`.</summary>
    private static ProcessStartInfo Sleeper() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > NUL")
        : new ProcessStartInfo("/bin/sleep", "60");

    [Fact]
    public void Start_SpawnsALiveProcess_WithUsableStdio()
    {
        using var p = ExeWorkerProcess.Start(Sleeper());
        p.HasExited.ShouldBeFalse();
        p.Input.CanWrite.ShouldBeTrue();
        p.Output.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void Kill_TerminatesTheProcess_AndPendingReadsComplete()
    {
        using var p = ExeWorkerProcess.Start(Sleeper());
        var pending = Task.Run(() => p.Output.Read(new byte[16], 0, 16));
        p.Kill();
        // Exit is observable...
        SpinWait.SpinUntil(() => p.HasExited, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        // ...and the blocked stdout read unblocks (EOF => 0, or an IO fault) —
        // this is what lets WorkerProcessEngine's deadline'd read complete.
        var completed = pending.Wait(TimeSpan.FromSeconds(5));
        completed.ShouldBeTrue();
    }

    [Fact]
    public void HasExited_TracksNaturalExit()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        using var p = ExeWorkerProcess.Start(psi);
        SpinWait.SpinUntil(() => p.HasExited, TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure** — build; expected `CS0246 ... 'ExeWorkerProcess'`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>Real child-process IWorkerProcess: redirected stdio, stderr lines
/// forwarded to a log callback, kill = whole process tree (ggml may spawn
/// nothing today, but the tree kill is free insurance). On Windows the child
/// is additionally bound to a Job Object with KILL_ON_JOB_CLOSE: a worker
/// wedged in native code never sees stdin EOF, so a parent CRASH would orphan
/// a ~700 MB process; the job binding also reaps kernel-wedge zombies at app
/// exit (the kernel closes the job handle with the parent). No-op on Linux,
/// so the Linux tests are unaffected.</summary>
public sealed class ExeWorkerProcess : IWorkerProcess
{
    private readonly Process _process;
    private readonly nint _jobHandle; // Windows Job Object; 0 elsewhere. Held for the worker's lifetime.

    private ExeWorkerProcess(Process process, nint jobHandle)
    {
        _process = process;
        _jobHandle = jobHandle;
    }

    public static ExeWorkerProcess Start(ProcessStartInfo psi, Action<string>? onStderrLine = null)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start worker process '{psi.FileName}'");
        if (onStderrLine is not null)
        {
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderrLine(e.Data); };
            process.BeginErrorReadLine();
        }
        else
        {
            // Drain stderr so the child can never block on a full pipe.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
        }
        var jobHandle = OperatingSystem.IsWindows() ? WindowsJob.BindKillOnClose(process) : 0;
        return new ExeWorkerProcess(process, jobHandle);
    }

    public Stream Input => _process.StandardInput.BaseStream;
    public Stream Output => _process.StandardOutput.BaseStream;
    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
        if (_jobHandle != 0) WindowsJob.Close(_jobHandle); // closing the job kills any survivor
    }
}

/// <summary>Minimal Job Object P/Invoke: CreateJobObject + SetInformationJobObject
/// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) + AssignProcessToJobObject. Failures are
/// tolerated (returns 0): the EOF/kill paths still supervise the worker; the job
/// is the belt-and-braces guarantee for parent CRASH and kernel-wedged workers.</summary>
internal static class WindowsJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    internal static nint BindKillOnClose(Process process)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0) return 0;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    internal static void Close(nint handle) => CloseHandle(handle);
}

public sealed class ExeWorkerProcessFactory : IWorkerProcessFactory
{
    private readonly Func<ProcessStartInfo> _psi;
    private readonly Action<string>? _onStderrLine;

    public ExeWorkerProcessFactory(Func<ProcessStartInfo> psi, Action<string>? onStderrLine = null)
    {
        _psi = psi;
        _onStderrLine = onStderrLine;
    }

    public IWorkerProcess Start() => ExeWorkerProcess.Start(_psi(), _onStderrLine);
}
```

> **Correction (2026-08-09):** the class comment in the listing above claims
> the job binding "also reaps kernel-wedge zombies at app exit" — that is the
> same false claim corrected in clause (d) (line 934): in the supervised path
> the per-worker job handle is closed at kill time (`KillLocked` →
> `ExeWorkerProcess.Dispose`), so KILL_ON_JOB_CLOSE fires then, not at app
> exit; the job's at-exit guarantee covers only parent crash. The listing is
> preserved verbatim as the historical record.

- [ ] **Step 4: Run the new tests, expect PASS** (`-class "Winpepper.Asr.Tests.TranscribeCpp.Worker.ExeWorkerProcessTests"`).

Note on test coverage: the Linux tests cannot exercise the job object (`WindowsJob.BindKillOnClose` is only called under `OperatingSystem.IsWindows()`); the windows gate (Task 22) compiles and links it. `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` behavior itself is a kernel guarantee — deliberately left untested here.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/ExeWorkerProcessTests.cs
git commit -m "feat(asr): real subprocess IWorkerProcess implementation"
```

---

### Task 7: Worker entry-point verb + worker-backed engine holder

**Files:**
- Modify: `src/Winpepper.App/Program.cs` (add the `--transcribe-worker` verb before any WinUI init, mirroring the existing `--selftest` at lines 14–20)
- Modify: `src/Winpepper.App/Hosting/NemotronEngineHolder.cs` (worker-backed; latch removed)

**Interfaces:**
- Consumes: `TranscribeWorkerLoop` (Task 3), `WorkerProcessEngine`/`ExeWorkerProcessFactory` (Tasks 5–6), `TranscribeCppEngine.Load` (unchanged), `NemotronStreamingModel` (unchanged in this task).
- Produces: `NemotronEngineHolder.TryGet()` keeps its exact signature (`ITranscribeCppEngine?`) — `AppShell` wiring is untouched in this task. New optional ctor param `Func<StreamingModelLayout, ITranscribeCppEngine>? engineFactory` is NOT added yet (Task 13 threads model selection); this task keeps the holder hard-wired to `NemotronStreamingModel` but returns a `WorkerProcessEngine` instead of an in-process engine, and drops the permanent failure latch (the restart policy inside the engine handles storms).
- NOTE: `Winpepper.App` is Windows-only (`#if WINDOWS`, not built by the Linux suite). The Linux suite proves nothing shared broke; the final windows-gate (Task 22) proves this file set compiles and passes.

- [ ] **Step 1: Add the worker verb to `Program.cs`**

In `src/Winpepper.App/Program.cs`, immediately AFTER the `--selftest` block (which ends `return SelftestProbe.Run(Console.WriteLine);` around line 20) and BEFORE the `--tray` handling, insert:

```csharp
        // Transcribe worker: hosts the native transcribe.cpp engine for the
        // parent app so a wedged native call is killable and the engine
        // restartable. Must run BEFORE WinRT/WinUI init: it is a plain
        // console loop over stdin/stdout. The parent supplies runtime/model
        // paths via the Load request; stderr carries worker logs.
        if (args.Any(a => a.Equals("--transcribe-worker", StringComparison.OrdinalIgnoreCase)))
        {
            // Suppress WER UI: a native crash must exit the worker promptly
            // (parent sees EOF -> kill/respawn) instead of wedging invisibly
            // on an error dialog.
            _ = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);

            // Main is [STAThread]; the worker's blocking native loop must not
            // inherit an STA — run it on a dedicated MTA foreground thread
            // and join (SetApartmentState is a no-op where unsupported).
            var exitCode = 0;
            var loop = new Thread(() =>
            {
                exitCode = Winpepper.Asr.TranscribeCpp.Worker.TranscribeWorkerLoop.Run(
                    Console.OpenStandardInput(),
                    Console.OpenStandardOutput(),
                    (runtimeDir, ggufPath) => Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                        runtimeDir, ggufPath, msg => Console.Error.WriteLine($"[transcribe-worker] {msg}")),
                    msg => Console.Error.WriteLine($"[transcribe-worker] {msg}"));
            }) { IsBackground = false };
            loop.SetApartmentState(System.Threading.ApartmentState.MTA);
            loop.Start();
            loop.Join();
            return exitCode;
        }
```

Add the WER-suppression P/Invoke as private members of `Program` (one declaration + constants):

```csharp
    // Worker-verb WER suppression: without it a native AV in transcribe.dll
    // can pop an (invisible, CreateNoWindow) WER dialog and wedge the worker
    // instead of exiting so the parent supervises it.
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint uMode);
    private const uint SEM_FAILCRITICALERRORS = 0x0001;
    private const uint SEM_NOGPFAULTERRORBOX = 0x0002;
```

- [ ] **Step 2: Rewrite `NemotronEngineHolder`**

Replace the body of `src/Winpepper.App/Hosting/NemotronEngineHolder.cs` (currently 56 lines; class doc + `TryGet` with `_failedPermanently` latch) with:

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

namespace Winpepper.App.Hosting;

/// <summary>
/// Process-wide lazy holder for the transcribe.cpp engine, now hosted in a
/// worker SUBPROCESS (Winpepper.exe --transcribe-worker). Not-installed is
/// re-checked every call so installing the model takes effect without a
/// restart. There is NO permanent failure latch anymore: the worker engine's
/// own restart policy (3 consecutive failures -> 60 s cooldown) bounds retry
/// storms, and a wedged or crashed worker recovers on a later dictation.
/// The engine object itself is cheap (the ~0.9 s model load happens inside
/// the worker, lazily, on first use) and is kept for the process lifetime.
/// </summary>
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;

    public NemotronEngineHolder(string modelsRoot, ILogger log)
    {
        _modelsRoot = modelsRoot;
        _log = log;
    }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            if (!NemotronStreamingModel.IsInstalled(_modelsRoot)) return null;
            return _engine ??= CreateWorkerEngine();
        }
    }

    private ITranscribeCppEngine CreateWorkerEngine()
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own executable path for the transcribe worker");
        var factory = new ExeWorkerProcessFactory(
            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker"),
            line => _log.LogWarning("{TranscribeCppLog}", line));
        _log.LogInformation("transcribe.cpp worker engine created ({Model})", NemotronStreamingModel.Name);
        return new WorkerProcessEngine(
            factory,
            NemotronStreamingModel.RuntimeDir(_modelsRoot),
            NemotronStreamingModel.GgufPath(_modelsRoot),
            NemotronStreamingModel.Name,
            log: msg => _log.LogInformation("{WorkerSupervision}", msg));
    }
}
#endif
```

- [ ] **Step 3: Run the full Linux suite** (`./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`; App is not in the Linux build, this proves shared code is intact).

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Program.cs src/Winpepper.App/Hosting/NemotronEngineHolder.cs
git commit -m "feat(app): --transcribe-worker verb; worker-backed NemotronEngineHolder without permanent latch"
```

- [ ] **Step 5 (verification note for the gate):** Task 22's windows-gate build compiles this; additionally, a manual smoke on Windows (optional, post-merge): run the installed `Winpepper.exe --transcribe-worker` from a console — it must sit reading stdin (Ctrl+C to exit) rather than opening a window.

---

### Task 8: Production Nemotron batch adapter

**Files:**
- Create: `src/Winpepper.Asr/Transcription/NemotronBatchTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronBatchTranscriberTests.cs`
- Test (enable now if deferred in Task 5): the `EndToEnd_WedgedStream_FallsBackToNemotronBatch_OnFreshWorker` fact in `WorkerProcessEngineTests.cs`

**Interfaces:**
- Consumes: `ITranscribeCppEngine.TranscribeBatch` (unchanged); bench reference `scripts/asr-latency-bench/Program.cs:777-788` (`EngineBatchTranscriber`).
- Produces (used by Tasks 9, 13, 15): `public sealed class NemotronBatchTranscriber : ITranscriber { public NemotronBatchTranscriber(Func<ITranscribeCppEngine?> engineProvider, string modelName, string? language = null, Microsoft.Extensions.Logging.ILogger? log = null); public string ModelName { get; } public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct); }`
- Hardening over the bench version (the bench's port notes, closed here): runs the blocking native call on the thread pool (`Task.Run`), observes `ct` before starting, logs `gateWaitMs` when > 0, resolves the engine per call (null → `InvalidOperationException("local speech engine unavailable")`), and takes an explicit `modelName` (never the streaming model's name — see Global Constraints on `asr_mode` classification).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/Transcription/NemotronBatchTranscriberTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class NemotronBatchTranscriberTests
{
    [Fact]
    public async Task Transcribes_ViaEngineBatch_WithLanguageHint_AndReportsItsOwnModelName()
    {
        var engine = new FakeTranscribeCppEngine();
        // Pass-through proof with a realistic locale (autodetect is a TRUE
        // null hint, not the string "auto" — the v0.1.3 gate rejects "auto").
        var t = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-multi-batch", language: "en-US");

        var result = await t.TranscribeAsync(new float[128], TestContext.Current.CancellationToken);

        result.ProviderModelName.ShouldBe("nemotron-streaming-multi-batch");
        result.Text.ShouldNotBeNull();
        engine.LastBatchLanguage.ShouldBe("en-US");
    }

    [Fact]
    public async Task NullEngine_Throws_InvalidOperation()
    {
        var t = new NemotronBatchTranscriber(() => null, "nemotron-streaming-en-batch");
        await Should.ThrowAsync<InvalidOperationException>(
            () => t.TranscribeAsync(new float[4], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PreCancelledToken_DoesNotTouchTheEngine()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = new FakeTranscribeCppEngine();
        var t = new NemotronBatchTranscriber(() => engine, "nemotron-streaming-en-batch");
        await Should.ThrowAsync<OperationCanceledException>(() => t.TranscribeAsync(new float[4], cts.Token));
    }
}
```

(If `FakeTranscribeCppEngine.TranscribeBatch` does not already record `LastBatchLanguage`, it does per Task 3's usage — verify and extend the fake if needed.)

- [ ] **Step 2: Run to verify failure** — build; expected `CS0246 ... 'NemotronBatchTranscriber'`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/NemotronBatchTranscriber.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Batch (offline, whole-utterance) transcription over the transcribe.cpp
/// engine — the production port of the bench's EngineBatchTranscriber. Serves
/// the StreamingEnabled=false path, the post-worker-restart failure fallback,
/// and every seam that previously required a ParakeetTranscriber.
/// ModelName must NOT equal the streaming model's name: PipelineHost
/// classifies asr_mode=streaming by exact name match, and a batch result must
/// be booked as batch (different latency budget, honest history stamps).
/// With the engine in a worker subprocess there is no compute-gate deadlock:
/// the worker auto-disposes an open stream before a batch, and a wedged
/// worker was already killed before this fallback runs.
/// </summary>
public sealed class NemotronBatchTranscriber : ITranscriber
{
    private readonly Func<ITranscribeCppEngine?> _engineProvider;
    private readonly string? _language;
    private readonly ILogger? _log;

    public NemotronBatchTranscriber(Func<ITranscribeCppEngine?> engineProvider, string modelName,
        string? language = null, ILogger? log = null)
    {
        _engineProvider = engineProvider;
        ModelName = modelName;
        _language = language;
        _log = log;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var engine = _engineProvider()
                ?? throw new InvalidOperationException(
                    "local speech engine unavailable (model not installed or worker restarting)");
            var text = engine.TranscribeBatch(mono16k.ToArray(), _language, out var gateWaitMs);
            if (gateWaitMs > 0)
                _log?.LogInformation("nemotron batch: compute-gate wait {GateWaitMs} ms", gateWaitMs);
            return new TranscriptionResult(text, ModelName);
        }, ct);
}
```

- [ ] **Step 4: Run the new tests (and the re-enabled end-to-end wedge test), expect PASS**

```bash
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Asr.Tests.Transcription.NemotronBatchTranscriberTests"
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProcessEngineTests"
```

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.Asr/Transcription/NemotronBatchTranscriber.cs tests/Winpepper.Asr.Tests/Transcription/NemotronBatchTranscriberTests.cs tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs
git commit -m "feat(asr): production NemotronBatchTranscriber (bench EngineBatchTranscriber port)"
```

---

### Task 9: Local streaming-transcriber factory (the composition ladder, Linux-testable)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/LocalStreamingTranscriberFactory.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/LocalStreamingTranscriberFactoryTests.cs`

**Interfaces:**
- Consumes: `NemotronBatchTranscriber` (Task 8); existing `NemotronStreamingTranscriber` (ctor `(Func<ITranscribeCppEngine> engineProvider, ITranscriber batchFallback, string modelName, ILogger? log = null, int attContextRight = 13, string? language = null, TimeSpan? nativeCallWarnAfter = null)`); existing `BatchStreamingAdapter(ITranscriber inner)`; existing `FallbackTranscriber(ITranscriber primary, ITranscriber local, ILogger<FallbackTranscriber> logger, Action<string>? onFallback = null, ...)`.
- Produces (used by Task 13 — `AppShell.BuildStreamingTranscriber` delegates its LOCAL branch here so the ladder is Linux-tested):

```csharp
namespace Winpepper.Asr.Transcription;

public static class LocalStreamingTranscriberFactory
{
    /// <summary>The local batch ladder: Nemotron batch first; Parakeet second
    /// (only when a Parakeet transcriber exists). Never null.</summary>
    public static ITranscriber BuildBatchLadder(
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory);

    /// <summary>The full local streaming transcriber for one dictation.</summary>
    public static IStreamingTranscriber Build(
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        bool streamingEnabled,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory);
}
```

  Selection rules (binding):
  1. `BuildBatchLadder`: `nemotronBatch = new NemotronBatchTranscriber(nemotronEngine, streamingModelName + "-batch", streamingLanguage, logger)`. If `parakeetBatch is null` → return `nemotronBatch`. Else → `new FallbackTranscriber(primary: nemotronBatch, local: parakeetBatch, loggerFactory.CreateLogger<FallbackTranscriber>())` (Parakeet steps in only when the Nemotron engine has trouble — the approved backup role).
  2. `Build`: `ladder = BuildBatchLadder(...)`. If `!streamingEnabled` → `new BatchStreamingAdapter(ladder)` (this is the `StreamingEnabled=false` → Nemotron-batch requirement, without noisy zero-push fallback warnings). Else resolve `var engine = nemotronEngine();` — if `engine is null` (model not installed) → `new BatchStreamingAdapter(ladder)`; else → `new NemotronStreamingTranscriber(() => engine, ladder, streamingModelName, loggerFactory.CreateLogger<NemotronStreamingTranscriber>(), language: streamingLanguage)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/Transcription/LocalStreamingTranscriberFactoryTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Tests.Transcription;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class LocalStreamingTranscriberFactoryTests
{
    private sealed class FixedTranscriber : ITranscriber
    {
        public int Calls;
        public FixedTranscriber(string name, string text) { ModelName = name; Text = text; }
        public string ModelName { get; }
        public string Text { get; }
        public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        { Calls++; return Task.FromResult(new TranscriptionResult(Text, ModelName)); }
    }

    [Fact]
    public void StreamingEnabled_WithEngine_BuildsNemotronStreaming()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ShouldBeOfType<NemotronStreamingTranscriber>();
        t.ModelName.ShouldBe("nemotron-streaming-en");
    }

    [Fact]
    public void StreamingEnabled_NoEngine_FallsToBatchAdapter()
    {
        var t = LocalStreamingTranscriberFactory.Build(
            () => null, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ShouldBeOfType<BatchStreamingAdapter>();
        t.ModelName.ShouldBe("nemotron-streaming-en-batch");
    }

    [Fact]
    public async Task StreamingDisabled_UsesNemotronBatch_EvenWithEngineAvailable()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, parakeetBatch: null, "nemotron-streaming-en", null,
            streamingEnabled: false, NullLoggerFactory.Instance);
        t.ShouldBeOfType<BatchStreamingAdapter>();
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);
        var r = await s.FinishAsync(new float[64], TestContext.Current.CancellationToken);
        r.ProviderModelName.ShouldBe("nemotron-streaming-en-batch"); // Nemotron serves streaming-off
    }

    [Fact]
    public async Task Ladder_NemotronHealthy_ParakeetNotCalled()
    {
        var engine = new FakeTranscribeCppEngine();
        var parakeet = new FixedTranscriber("parakeet-tdt-0.6b-v3", "parakeet text");
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => engine, parakeet, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        var r = await ladder.TranscribeAsync(new float[64], TestContext.Current.CancellationToken);
        r.ProviderModelName.ShouldBe("nemotron-streaming-en-batch");
        parakeet.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Ladder_NemotronUnavailable_ParakeetStepsIn()
    {
        var parakeet = new FixedTranscriber("parakeet-tdt-0.6b-v3", "parakeet text");
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => null, parakeet, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        var r = await ladder.TranscribeAsync(new float[64], TestContext.Current.CancellationToken);
        r.Text.ShouldBe("parakeet text");
        r.ProviderModelName.ShouldBe("parakeet-tdt-0.6b-v3");
    }

    [Fact]
    public async Task Ladder_NoParakeet_NemotronUnavailable_FailsLoudly()
    {
        var ladder = LocalStreamingTranscriberFactory.BuildBatchLadder(
            () => null, parakeetBatch: null, "nemotron-streaming-en", null, NullLoggerFactory.Instance);
        await Should.ThrowAsync<InvalidOperationException>(
            () => ladder.TranscribeAsync(new float[8], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MultilingualModel_Builds_WithNullAutodetectLanguage()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = LocalStreamingTranscriberFactory.Build(
            () => engine, null, "nemotron-streaming-multi", null,
            streamingEnabled: true, NullLoggerFactory.Instance);
        t.ModelName.ShouldBe("nemotron-streaming-multi");
        // The multilingual layout's language hint is a TRUE null (autodetect
        // via the model's auto prompt slot; the literal "auto" is rejected by
        // the v0.1.3 language gate). Language plumb-through is proven
        // behaviorally in NemotronStreamingTranscriberTests via
        // BeginStreamLanguages; here the construction path is what's under test.
    }
}
```

- [ ] **Step 2: Run to verify failure** — build; expected `CS0246 ... 'LocalStreamingTranscriberFactory'`.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/LocalStreamingTranscriberFactory.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Pure composition of the LOCAL transcriber ladder, extracted from
/// AppShell.BuildStreamingTranscriber so the selection rules are
/// Linux-testable (AppShell is #if WINDOWS and untestable).
///
/// Roles (2026-08 nemotron-first): the Nemotron model selected in settings is
/// PRIMARY (streaming when enabled+installed, batch otherwise). Parakeet is
/// an OPTIONAL BACKUP: it joins the batch ladder only when installed, and
/// only fires when the Nemotron engine has trouble (worker restarting, model
/// missing, native failure). With no Parakeet and no Nemotron the ladder
/// fails loudly — the pipeline gates on primary availability before this runs.
/// </summary>
public static class LocalStreamingTranscriberFactory
{
    public static ITranscriber BuildBatchLadder(
        Func<ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        ILoggerFactory loggerFactory)
    {
        var nemotronBatch = new NemotronBatchTranscriber(
            nemotronEngine, streamingModelName + "-batch", streamingLanguage,
            loggerFactory.CreateLogger<NemotronBatchTranscriber>());
        if (parakeetBatch is null) return nemotronBatch;
        return new FallbackTranscriber(
            nemotronBatch, parakeetBatch,
            loggerFactory.CreateLogger<FallbackTranscriber>());
    }

    public static IStreamingTranscriber Build(
        Func<ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        bool streamingEnabled,
        ILoggerFactory loggerFactory)
    {
        var ladder = BuildBatchLadder(nemotronEngine, parakeetBatch, streamingModelName, streamingLanguage, loggerFactory);

        if (!streamingEnabled)
            return new BatchStreamingAdapter(ladder); // Nemotron serves StreamingEnabled=false

        var engine = nemotronEngine();
        if (engine is null)
            return new BatchStreamingAdapter(ladder); // model not installed yet

        return new NemotronStreamingTranscriber(
            () => engine, ladder, streamingModelName,
            loggerFactory.CreateLogger<NemotronStreamingTranscriber>(),
            language: streamingLanguage);
    }
}
```

Note: `FallbackTranscriber`'s ctor is `(ITranscriber primary, ITranscriber local, ILogger<FallbackTranscriber> logger, Action<string>? onFallback = null, TimeSpan? cloudDeadline = null, ...)`. Its ~10 s deadline only bounds primaries that FAIL/THROW: it cancels a linked token which the batch adapter observes only BEFORE the native call starts (`ct.ThrowIfCancellationRequested()` at the top of the `Task.Run` lambda) — an in-flight healthy-but-slow Nemotron batch returns its OWN result, bounded by the worker's length-aware batch deadline (`max(BatchTimeout, 30 s + 2 s per audio-second)`, Task 5), NOT by 10 s. Parakeet steps in on THROWN failures: engine unavailable, restart-budget exhaustion, oversize dictation (>~17 min pre-check), or a worker kill (`TranscribeCppException`). Do not expect or document a 10 s yield-to-Parakeet for slow-but-healthy batches — that mechanism does not exist (V5/A11).

- [ ] **Step 4: Run the new tests, expect PASS** (`-class "Winpepper.Asr.Tests.Transcription.LocalStreamingTranscriberFactoryTests"`).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.Asr/Transcription/LocalStreamingTranscriberFactory.cs tests/Winpepper.Asr.Tests/Transcription/LocalStreamingTranscriberFactoryTests.cs
git commit -m "feat(asr): local transcriber ladder factory (nemotron-first, parakeet backup)"
```

---

### Task 10: Registry — multilingual Nemotron entry, per-model layout, streaming default

**Files:**
- Modify: `src/Winpepper.Models/ModelRegistry.cs`
- Create: `src/Winpepper.Asr/TranscribeCpp/StreamingModelLayout.cs`
- Modify: `src/Winpepper.Asr/TranscribeCpp/NemotronStreamingModel.cs` (delegate to the English layout)
- Modify: `scripts/verify-model-hashes.ps1` (add the new URL to its `$files` array)
- Test: `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs` (extend/update)
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/StreamingModelLayoutTests.cs` (new)
- Test: `tests/Winpepper.IntegrationTests/NemotronLayoutContractTests.cs` (extend to both models)

**Interfaces:**
- Consumes: `ModelDescriptor`/`ModelFile` records (`src/Winpepper.Models/ModelDescriptor.cs`, `ModelFile.cs`).
- Produces (used by Tasks 11, 13, 15, 17, 18):
  - `ModelRegistry.MultilingualStreamingAsrName = "nemotron-streaming-multi"` (new `public const string`).
  - Registry entry for the multilingual model (exact values below).
  - `ModelRegistry.ResolveOrDefault(name, ModelKind.StreamingAsr)` no longer throws — it resolves known streaming names and defaults to `StreamingAsrName` (`nemotron-streaming-en`). This is the upgrade-path repair for `StreamingModelName`.
  - `StreamingModelLayout` (namespace `Winpepper.Asr.TranscribeCpp`):

```csharp
public sealed record StreamingModelLayout(string Name, string GgufFileName, string? Language)
{
    public const string TarballTopLevelDir = "transcribe-native-windows-x86_64-cpu-vulkan";
    public static readonly StreamingModelLayout English =
        new("nemotron-streaming-en", "nemotron-speech-streaming-en-0.6b-Q8_0.gguf", Language: null);
    public static readonly StreamingModelLayout Multilingual =
        new("nemotron-streaming-multi", "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf", Language: null); // null = autodetect (the model's auto prompt); the literal "auto" is rejected by the v0.1.3 language gate
    public static StreamingModelLayout For(string? name)
        => name == Multilingual.Name ? Multilingual : English;   // unknown/null -> English (safe default)
    public string ModelFileRelative => Path.Combine(Name, GgufFileName);
    public string RuntimeDirRelative => Path.Combine(Name, "runtime", TarballTopLevelDir);
    public string GgufPath(string modelsRoot) => Path.Combine(modelsRoot, ModelFileRelative);
    public string RuntimeDir(string modelsRoot) => Path.Combine(modelsRoot, RuntimeDirRelative);
    public bool IsInstalled(string modelsRoot)
        => File.Exists(GgufPath(modelsRoot))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "transcribe.dll"))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "contract.json"));
}
```

  - `NemotronStreamingModel` keeps its exact public surface (`Name`, `GgufFileName`, `TarballTopLevelDir`, `ModelFileRelative`, `RuntimeDirRelative`, `GgufPath`, `RuntimeDir`, `IsInstalled`) but each member delegates to `StreamingModelLayout.English` (e.g. `public const string` fields become `public static string Name => StreamingModelLayout.English.Name;` — CAUTION: `Name`/`GgufFileName`/`TarballTopLevelDir` are `const` today and `const` cannot delegate; change them to `public static readonly string`/expression-bodied `static` properties and fix any `const`-requiring usage sites — grep for `NemotronStreamingModel.Name` used in attributes or switch patterns; there are none expected, it is used as a plain value).

- [ ] **Step 1: Re-verify the multilingual hash/size against the Hugging Face API (do NOT skip)**

Run:
```bash
curl -s "https://huggingface.co/api/models/handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf/tree/main" \
  | python3 -c "import json,sys; [print(f['path'], f['size'], f['lfs']['oid']) for f in json.load(sys.stdin) if f['path'].endswith('Q8_0.gguf')]"
```
Expected output (must match EXACTLY what goes into the registry; if it differs, use the API values and update this plan's numbers everywhere they appear):
```
nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf 751094240 b94545b313b3223fda7b2857a52681da813935c2127643d1e9ff0c23d988089c
```
(LFS `oid` is the SHA-256 of the file content. Belt-and-braces: the downloader hard-verifies size+SHA-256 on install, so a wrong value fails loudly, never silently.)

- [ ] **Step 2: Write the failing tests**

In `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs`:
- UPDATE the existing `ResolveOrDefault_throws_for_StreamingAsr`-style test (the catalog test asserting `ResolveOrDefault(null, StreamingAsr)` throws `ArgumentOutOfRangeException`) to the new contract, and ADD multilingual assertions:

```csharp
    [Fact]
    public void ResolveOrDefault_StreamingAsr_DefaultsToEnglish()
    {
        var r = new ModelRegistry();
        r.ResolveOrDefault(null, ModelKind.StreamingAsr).Name.ShouldBe(ModelRegistry.StreamingAsrName);
        r.ResolveOrDefault("garbage-name", ModelKind.StreamingAsr).Name.ShouldBe(ModelRegistry.StreamingAsrName);
        r.ResolveOrDefault(ModelRegistry.MultilingualStreamingAsrName, ModelKind.StreamingAsr)
            .Name.ShouldBe(ModelRegistry.MultilingualStreamingAsrName);
        // A streaming name is still never resolvable as a batch Asr selection:
        r.ResolveOrDefault(ModelRegistry.MultilingualStreamingAsrName, ModelKind.Asr)
            .Name.ShouldBe(ModelRegistry.DefaultAsrName);
    }

    [Fact]
    public void Registry_contains_the_multilingual_nemotron_streaming_model()
    {
        var d = new ModelRegistry().Find(ModelRegistry.MultilingualStreamingAsrName);
        d.ShouldNotBeNull();
        d.Kind.ShouldBe(ModelKind.StreamingAsr);
        d.InstallDirRelative.ShouldBe("nemotron-streaming-multi");
        d.Files.Count.ShouldBe(2);
        var gguf = d.Files[0];
        gguf.RelativePath.ShouldBe("nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf");
        gguf.SizeBytes.ShouldBe(751_094_240);
        gguf.Sha256.ShouldBe("b94545b313b3223fda7b2857a52681da813935c2127643d1e9ff0c23d988089c");
        gguf.Url.ShouldBe("https://huggingface.co/handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf/resolve/main/nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf");
        var runtime = d.Files[1];
        runtime.ExtractToRelative.ShouldBe("runtime");
        runtime.Url.ShouldStartWith("https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/");
        runtime.SizeBytes.ShouldBe(25_957_910);
    }
```
- Also update the existing `No StreamingAsr-kind descriptor may appear in ByKind(Asr)` style assertions if they enumerate streaming entries exactly (there are now TWO streaming entries).

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/StreamingModelLayoutTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp;

public sealed class StreamingModelLayoutTests
{
    [Fact]
    public void English_MatchesTheLegacyNemotronStreamingModelConstants()
    {
        StreamingModelLayout.English.Name.ShouldBe("nemotron-streaming-en");
        StreamingModelLayout.English.GgufFileName.ShouldBe("nemotron-speech-streaming-en-0.6b-Q8_0.gguf");
        StreamingModelLayout.English.Language.ShouldBeNull();
        NemotronStreamingModel.Name.ShouldBe(StreamingModelLayout.English.Name);
        NemotronStreamingModel.ModelFileRelative.ShouldBe(StreamingModelLayout.English.ModelFileRelative);
        NemotronStreamingModel.RuntimeDirRelative.ShouldBe(StreamingModelLayout.English.RuntimeDirRelative);
    }

    [Fact]
    public void Multilingual_UsesNullAutodetectLanguage_AndItsOwnDir()
    {
        var m = StreamingModelLayout.Multilingual;
        m.Name.ShouldBe("nemotron-streaming-multi");
        m.Language.ShouldBeNull(); // TRUE null = autodetect; "auto" is rejected by the v0.1.3 gate
        m.ModelFileRelative.ShouldBe(Path.Combine("nemotron-streaming-multi", "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf"));
        m.RuntimeDirRelative.ShouldBe(Path.Combine("nemotron-streaming-multi", "runtime", StreamingModelLayout.TarballTopLevelDir));
    }

    [Theory]
    [InlineData(null, "nemotron-streaming-en")]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-en")]
    [InlineData("nemotron-streaming-multi", "nemotron-streaming-multi")]
    [InlineData("unknown-model", "nemotron-streaming-en")]
    public void For_ResolvesKnownNamesAndDefaultsToEnglish(string? name, string expected)
        => StreamingModelLayout.For(name).Name.ShouldBe(expected);

    [Fact]
    public void IsInstalled_RequiresGgufDllAndContract()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sml-{Guid.NewGuid():N}");
        try
        {
            var m = StreamingModelLayout.Multilingual;
            m.IsInstalled(root).ShouldBeFalse();
            Directory.CreateDirectory(Path.GetDirectoryName(m.GgufPath(root))!);
            Directory.CreateDirectory(m.RuntimeDir(root));
            File.WriteAllText(m.GgufPath(root), "x");
            m.IsInstalled(root).ShouldBeFalse();
            File.WriteAllText(Path.Combine(m.RuntimeDir(root), "transcribe.dll"), "x");
            File.WriteAllText(Path.Combine(m.RuntimeDir(root), "contract.json"), "{}");
            m.IsInstalled(root).ShouldBeTrue();
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

In `tests/Winpepper.IntegrationTests/NemotronLayoutContractTests.cs`: extend the registry↔layout lockstep to the multilingual entry (same shape as the existing English assertions — descriptor gguf `RelativePath` == `StreamingModelLayout.Multilingual.GgufFileName`, `InstallDirRelative` == `Multilingual.Name`, runtime extract target prefix matches `RuntimeDirRelative`).

- [ ] **Step 3: Run to verify failures** — build `Winpepper.Models.Tests`, `Winpepper.Asr.Tests`, `Winpepper.IntegrationTests`; expected `CS0246`/`CS0117` (`MultilingualStreamingAsrName`, `StreamingModelLayout`).

- [ ] **Step 4: Implement**

(a) `src/Winpepper.Models/ModelRegistry.cs`:
- Add after `public const string StreamingAsrName = "nemotron-streaming-en";`:
```csharp
    public const string MultilingualStreamingAsrName = "nemotron-streaming-multi";
```
- Append to the `_all` initializer, directly after the existing `StreamingAsrName` descriptor:
```csharp
            new ModelDescriptor
            {
                Name = MultilingualStreamingAsrName,
                Kind = ModelKind.StreamingAsr,
                DisplayName = "Nemotron 3.5 Speech Streaming (0.6B, Q8_0 GGUF, Multilingual)",
                InstallDirRelative = "nemotron-streaming-multi",
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf",
                        Url = "https://huggingface.co/handy-computer/nemotron-3.5-asr-streaming-0.6b-gguf/resolve/main/nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf",
                        // Size+SHA-256 read from the HF API (LFS oid) 2026-08-08 and
                        // re-verified in this task; the downloader hard-verifies on install.
                        Sha256 = "b94545b313b3223fda7b2857a52681da813935c2127643d1e9ff0c23d988089c",
                        SizeBytes = 751_094_240,
                    },
                    new ModelFile
                    {
                        RelativePath = "transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz",
                        Url = "https://github.com/handy-computer/transcribe.cpp/releases/download/v0.1.3/transcribe-native-0.1.3-windows-x86_64-cpu-vulkan.tar.gz",
                        Sha256 = "9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5",
                        SizeBytes = 25_957_910,
                        ExtractToRelative = "runtime",
                    },
                },
            },
```
- In `ResolveOrDefault` replace the `ModelKind.StreamingAsr => throw ...` arm with:
```csharp
            ModelKind.StreamingAsr => StreamingAsrName,
```
and update the method's doc comment (it currently says streaming has no default).
- Update the `ModelKind` enum doc comment on `StreamingAsr` (`src/Winpepper.Models/ModelKind.cs`) — it says "Never selectable as AsrModelName; auto-installed in the background on first run". New text:
```csharp
    /// <summary>Streaming ASR engine (transcribe.cpp GGUF + native runtime).
    /// The PRIMARY speech model since 2026-08 (nemotron-first): selected via
    /// AppSettings.StreamingModelName (English default, Multilingual optional),
    /// installed from the onboarding model picker on new installs and by
    /// StreamingAutoInstaller on upgrades. Still never valid as AsrModelName —
    /// that setting names the optional Parakeet batch/backup model.</summary>
```

(b) Create `src/Winpepper.Asr/TranscribeCpp/StreamingModelLayout.cs` with the record from the Interfaces block above (verbatim, plus a file-level doc comment noting the runtime tarball is shared between both models and extracts per-model-dir).

(c) Rewrite `src/Winpepper.Asr/TranscribeCpp/NemotronStreamingModel.cs` as a shim:

```csharp
namespace Winpepper.Asr.TranscribeCpp;

/// <summary>Back-compat shim over <see cref="StreamingModelLayout.English"/>.
/// Prefer StreamingModelLayout for anything model-selection aware.</summary>
public static class NemotronStreamingModel
{
    public static string Name => StreamingModelLayout.English.Name;
    public static string GgufFileName => StreamingModelLayout.English.GgufFileName;
    public static string TarballTopLevelDir => StreamingModelLayout.TarballTopLevelDir;
    public static string ModelFileRelative => StreamingModelLayout.English.ModelFileRelative;
    public static string RuntimeDirRelative => StreamingModelLayout.English.RuntimeDirRelative;
    public static string GgufPath(string modelsRoot) => StreamingModelLayout.English.GgufPath(modelsRoot);
    public static string RuntimeDir(string modelsRoot) => StreamingModelLayout.English.RuntimeDir(modelsRoot);
    public static bool IsInstalled(string modelsRoot) => StreamingModelLayout.English.IsInstalled(modelsRoot);
}
```
If any call site fails to compile because it needed a `const` (e.g. attribute/`case` label), keep that one member `const` with the literal string and add a `StreamingModelLayoutTests` assertion pinning it equal to `StreamingModelLayout.English` (the layout tests above already pin `Name`).

(d) `scripts/verify-model-hashes.ps1`: add the multilingual gguf URL to the `$files` array at the top (same shape as the existing 9 entries).

- [ ] **Step 5: Run the updated test classes, expect PASS**, then the full Linux suite.

```bash
dotnet exec "$R/tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Models.Tests.ModelRegistryCatalogTests"
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows" -class "Winpepper.Asr.Tests.TranscribeCpp.StreamingModelLayoutTests"
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```

- [ ] **Step 6: Commit**

```bash
git add -A src/Winpepper.Models src/Winpepper.Asr/TranscribeCpp scripts/verify-model-hashes.ps1 tests/
git commit -m "feat(models): multilingual Nemotron 3.5 registry entry, StreamingModelLayout, streaming default resolution"
```

---

### Task 11: Settings — StreamingModelName, picker-choice flags, selection slot, upgrade-safe auto-installer

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs`
- Create: `src/Winpepper.Core/Settings/StreamingModelSelectionSlot.cs`
- Modify: `src/Winpepper.Models/StreamingAutoInstaller.cs`
- Test: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs` (extend)
- Test: `tests/Winpepper.Core.Tests/Settings/StreamingModelSelectionSlotTests.cs` (new)
- Test: `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs` (extend)

**Interfaces:**
- Consumes: `ModelRegistry.MultilingualStreamingAsrName` (Task 10).
- Produces (used by Tasks 13, 14, 17, 18):
  - `AppSettings` additions (place next to `AsrModelName` at the top ASR block):
```csharp
    // Primary local speech model (StreamingAsr kind). Streams while you speak
    // when StreamingEnabled; serves batch otherwise. Missing/unknown values
    // resolve to the English default via ModelRegistry.ResolveOrDefault, so
    // pre-2026-08 settings.json files (no field) keep today's behavior.
    public string StreamingModelName { get; init; } = "nemotron-streaming-en";

    // Onboarding model-picker choices (persisted at Step-3 advance so an
    // interrupted first run resumes with the same download scope).
    public bool OnboardingBackupModelChosen { get; init; } = false;
    public bool OnboardingCleanupModelChosen { get; init; } = false;
```
  - `StreamingModelSelectionSlot` — identical shape to `AsrModelSelectionSlot` (`src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs`: volatile string?, `Publish(string?)`, `Read()`), with a doc comment naming the streaming role.
  - `StreamingAutoInstaller.StartAsync(bool streamingEnabled, string? selectedModelName, CancellationToken ct)` — NEW overload (keep the old 2-arg signature delegating with `selectedModelName: null` so existing tests compile); the run resolves its descriptor per call: `registry.Find(selectedModelName ?? "") ?? registry.Find(ModelRegistry.StreamingAsrName)` (constructor keeps requiring the English descriptor to exist). This keeps upgrades correct when the user picked Multilingual.

- [ ] **Step 1: Write the failing tests**

Extend `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`:

```csharp
    [Fact]
    public void Defaults_StreamingModelName_IsEnglishNemotron()
    {
        var s = new AppSettings();
        s.StreamingModelName.ShouldBe("nemotron-streaming-en");
        s.OnboardingBackupModelChosen.ShouldBeFalse();
        s.OnboardingCleanupModelChosen.ShouldBeFalse();
    }

    [Fact]
    public void Load_LegacySettingsJson_WithoutStreamingModelName_DefaultsToEnglish()
    {
        // Upgrade path: a pre-nemotron-first settings.json has no
        // streamingModelName key and MUST keep streaming with the English model.
        var dir = Directory.CreateTempSubdirectory("settings-upgrade");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """
                {
                  "schema": 1,
                  "asrModelName": "parakeet-tdt-0.6b-v3",
                  "streamingEnabled": true,
                  "onboardingCompleted": true
                }
                """);
            var store = new SettingsStore(path);
            var s = store.Load();
            s.StreamingModelName.ShouldBe("nemotron-streaming-en");
            s.AsrModelName.ShouldBe("parakeet-tdt-0.6b-v3"); // untouched
            s.StreamingEnabled.ShouldBeTrue();
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void RoundTrip_PersistsStreamingModelName_AndPickerChoices()
    {
        var dir = Directory.CreateTempSubdirectory("settings-rt");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var store = new SettingsStore(path);
            store.Save(new AppSettings
            {
                StreamingModelName = "nemotron-streaming-multi",
                OnboardingBackupModelChosen = true,
                OnboardingCleanupModelChosen = true,
            });
            var loaded = store.Load();
            loaded.StreamingModelName.ShouldBe("nemotron-streaming-multi");
            loaded.OnboardingBackupModelChosen.ShouldBeTrue();
            loaded.OnboardingCleanupModelChosen.ShouldBeTrue();
        }
        finally { dir.Delete(recursive: true); }
    }
```
(Match the file's existing temp-dir/`SettingsStore` construction idiom — read the top of `SettingsStoreTests.cs` first and reuse its helpers if it has them; the ctor takes the json path.)

Create `tests/Winpepper.Core.Tests/Settings/StreamingModelSelectionSlotTests.cs` (mirror `AsrModelSelectionSlotTests.cs`):

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public sealed class StreamingModelSelectionSlotTests
{
    [Fact]
    public void Read_BeforeAnyPublish_ReturnsNull()
        => new StreamingModelSelectionSlot().Read().ShouldBeNull();

    [Fact]
    public void Publish_ThenRead_RoundTrips_LastWriteWins()
    {
        var slot = new StreamingModelSelectionSlot();
        slot.Publish("nemotron-streaming-en");
        slot.Publish("nemotron-streaming-multi");
        slot.Read().ShouldBe("nemotron-streaming-multi");
    }
}
```

Extend `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs` (reuse its existing fake-downloader/temp-root fixtures — read the file's existing test shape first):

```csharp
    [Fact]
    public async Task StartAsync_WithSelectedMultilingual_DownloadsTheMultilingualDescriptor()
    {
        // Arrange exactly like the existing download-when-missing test, then:
        await installer.StartAsync(streamingEnabled: true,
            selectedModelName: ModelRegistry.MultilingualStreamingAsrName, CancellationToken.None);
        downloader.Downloaded.Select(d => d.Name)
            .ShouldBe(new[] { ModelRegistry.MultilingualStreamingAsrName });
    }

    [Fact]
    public async Task StartAsync_WithUnknownSelectedName_FallsBackToEnglish()
    {
        await installer.StartAsync(streamingEnabled: true,
            selectedModelName: "no-such-model", CancellationToken.None);
        downloader.Downloaded.Select(d => d.Name)
            .ShouldBe(new[] { ModelRegistry.StreamingAsrName });
    }
```

- [ ] **Step 2: Run to verify failures** — build `Winpepper.Core.Tests` + `Winpepper.Models.Tests`; expected `CS0117` (`StreamingModelName`), `CS0246` (`StreamingModelSelectionSlot`), missing overload.

- [ ] **Step 3: Implement**

(a) `AppSettings.cs`: add the three properties (code in Interfaces block) in the ASR section after `AsrModelName`.

(b) Create `src/Winpepper.Core/Settings/StreamingModelSelectionSlot.cs`:

```csharp
namespace Winpepper.Core.Settings;

/// <summary>
/// Thread-safe in-memory source of truth for the DESIRED streaming (primary)
/// speech model name — the streaming analog of <see cref="AsrModelSelectionSlot"/>.
/// UI promote callbacks Publish the raw name (persistence to settings.json is
/// durability only); the engine holder Reads it per dictation. Volatile
/// reference: single word publication, last-write-wins.
/// </summary>
public sealed class StreamingModelSelectionSlot
{
    private volatile string? _desired;
    public void Publish(string? modelName) => _desired = modelName;
    public string? Read() => _desired;
}
```

(c) `StreamingAutoInstaller.cs`: change the resolved-at-ctor descriptor into per-run resolution. Keep the ctor's existence check on the English descriptor. Change `StartAsync`:

```csharp
    public Task StartAsync(bool streamingEnabled, CancellationToken ct)
        => StartAsync(streamingEnabled, selectedModelName: null, ct);

    /// <summary>Begin or join. selectedModelName is the user's primary speech
    /// model (AppSettings.StreamingModelName); unknown/null falls back to the
    /// English default so upgrades never stall on a bad name.</summary>
    public Task StartAsync(bool streamingEnabled, string? selectedModelName, CancellationToken ct)
```
and inside the run, resolve `var descriptor = (_registry.Find(selectedModelName ?? "") is { Kind: ModelKind.StreamingAsr } found ? found : _registry.Find(ModelRegistry.StreamingAsrName)!) ;` then use `descriptor` where the ctor-resolved field was used (`IsInstalledAndExtracted`, the download call). Adjust the private field/ctor accordingly (keep `_registry`).

- [ ] **Step 4: Run the three test classes, expect PASS; then full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src/Winpepper.Core/Settings src/Winpepper.Models/StreamingAutoInstaller.cs tests/
git commit -m "feat(settings,models): StreamingModelName + picker flags + selection slot; auto-installer honors the selected model"
```

---

### Task 12: `IDisposableTranscriber` seam + startup-gate generalization

**Files:**
- Create: `src/Winpepper.Asr/Transcription/IDisposableTranscriber.cs`
- Modify: `src/Winpepper.Asr/Transcription/ParakeetTranscriber.cs`
- Modify: `src/Winpepper.Core/ViewModels/AsrPipelineStartupGate.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/AsrPipelineStartupGateTests.cs` (update)
- Test: `tests/Winpepper.Asr.Tests/Transcription/ParakeetTranscriberDisposalTests.cs` (new, small)

**Interfaces:**
- Consumes: `ParakeetSession` (unchanged), `ITranscriber`.
- Produces (used by Task 13):
  - `public interface IDisposableTranscriber : ITranscriber, IDisposable { }` (namespace `Winpepper.Asr.Transcription`) — "a loaded local batch model the pipeline owns and must dispose on swap/teardown".
  - `ParakeetTranscriber : IDisposableTranscriber` with `ownsSession: bool` ctor flag: `public ParakeetTranscriber(ParakeetSession session, string modelName, bool ownsSession = false)`; `Dispose()` disposes the session iff `ownsSession`. Existing call sites compile unchanged (default false).
  - `AsrPipelineStartupGate` ctor becomes `(Func<CancellationToken, Task<bool>> verifyPrimaryReady, Func<bool> tryStartPipeline, Action? onNotReady = null)`; `TryStartAsync` awaits the delegate instead of `IAsrProvisioningService.VerifyReadyAsync`. Rationale: at boot the gate must accept EITHER an installed+extracted selected Nemotron OR a hash-verified Parakeet — a policy the App layer composes (Task 13's `ModelsServices.VerifyPrimarySpeechReadyAsync`); the Core gate just needs a verify function.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/Transcription/ParakeetTranscriberDisposalTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public sealed class ParakeetTranscriberDisposalTests
{
    [Fact]
    public void ParakeetTranscriber_IsAnIDisposableTranscriber()
        => typeof(IDisposableTranscriber).IsAssignableFrom(typeof(ParakeetTranscriber)).ShouldBeTrue();

    // Behavior note: constructing a ParakeetSession needs real ONNX files, so
    // owned-session disposal is proven by the Windows-trait integration tests
    // and the type-level contract here. Dispose(ownsSession: false) must be a
    // no-op by construction — enforced by code review of the 10-line class.
}
```

Update `tests/Winpepper.Core.Tests/ViewModels/AsrPipelineStartupGateTests.cs`: replace `IAsrProvisioningService` fakes with delegates. For each existing test, the arrange becomes e.g.:

```csharp
    var gate = new AsrPipelineStartupGate(_ => Task.FromResult(true), () => true);
    (await gate.TryStartAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
```
and the not-ready case:
```csharp
    var notReadyCalls = 0;
    var gate = new AsrPipelineStartupGate(_ => Task.FromResult(false), () => true, () => notReadyCalls++);
    (await gate.TryStartAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    notReadyCalls.ShouldBe(1);
```
Preserve every behavioral case the file already covers (verify-false → onNotReady + no pipeline start; verify-true + start-false → false; exceptions propagate).

- [ ] **Step 2: Run to verify failures** — expected `CS0246 ... 'IDisposableTranscriber'` and ctor-mismatch errors in the gate tests.

- [ ] **Step 3: Implement**

`src/Winpepper.Asr/Transcription/IDisposableTranscriber.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>A loaded, owned local batch ASR model usable as an ITranscriber.
/// PipelineHost holds its optional Parakeet backup through this seam so it
/// can dispose it on swap/teardown without knowing the concrete model type.</summary>
public interface IDisposableTranscriber : ITranscriber, IDisposable { }
```

`ParakeetTranscriber.cs` (whole file, currently 22 lines):

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Adapts the local ONNX Parakeet session to the ITranscriber seam.
/// With ownsSession=true (PipelineHost's loader) disposing the transcriber
/// disposes the underlying session; the default false preserves the legacy
/// borrow semantics for any other call site.</summary>
public sealed class ParakeetTranscriber : IDisposableTranscriber
{
    private readonly ParakeetSession _session;
    private readonly bool _ownsSession;

    public ParakeetTranscriber(ParakeetSession session, string modelName, bool ownsSession = false)
    {
        _session = session;
        ModelName = modelName;
        _ownsSession = ownsSession;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            var transcript = _session.Transcribe(mono16k.Span);
            return new TranscriptionResult(transcript.Text, ModelName);
        }, ct);

    public void Dispose()
    {
        if (_ownsSession) _session.Dispose();
    }
}
```

`AsrPipelineStartupGate.cs` (whole file):

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>Starts dictation only after authoritative verification of the
/// PRIMARY speech model. The verify policy is injected: since nemotron-first,
/// "primary ready" means the selected streaming model is installed+extracted,
/// OR the optional Parakeet backup passes size+SHA-256 (ModelsServices
/// composes this; see VerifyPrimarySpeechReadyAsync). The invariant stands:
/// a merely loadable stale model must not enter PipelineHost.</summary>
public sealed class AsrPipelineStartupGate
{
    private readonly Func<CancellationToken, Task<bool>> _verifyPrimaryReady;
    private readonly Func<bool> _tryStartPipeline;
    private readonly Action? _onNotReady;

    public AsrPipelineStartupGate(
        Func<CancellationToken, Task<bool>> verifyPrimaryReady,
        Func<bool> tryStartPipeline,
        Action? onNotReady = null)
    {
        _verifyPrimaryReady = verifyPrimaryReady ?? throw new ArgumentNullException(nameof(verifyPrimaryReady));
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
        _onNotReady = onNotReady;
    }

    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        if (!await _verifyPrimaryReady(ct))
        {
            _onNotReady?.Invoke();
            return false;
        }
        return _tryStartPipeline();
    }
}
```

NOTE: `AppShell.cs:449-459` constructs the gate with `ModelsServices` (an `IAsrProvisioningService`) — that call site now fails to compile on Windows until Task 13 rewires it. That is expected and confined to `Winpepper.App` (not in the Linux build); Task 13 lands before any gate run. Do not "fix" it here.

- [ ] **Step 4: Run the updated classes, expect PASS; then full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src/Winpepper.Asr/Transcription src/Winpepper.Core/ViewModels/AsrPipelineStartupGate.cs tests/
git commit -m "refactor(asr,core): IDisposableTranscriber seam; startup gate takes a verify delegate (App rewired next commit)"
```

---

### Task 13: Decouple `PipelineHost` and `AppShell` from Parakeet (the big one)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (whole ASR seam; `#if WINDOWS`, ~2030 lines — every edit site listed below, both hotkey arms)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (`BuildStreamingTranscriber`, PipelineHost construction, startup gate, auto-install selected name)
- Modify: `src/Winpepper.App/Services/ModelsServices.cs` (`VerifyPrimarySpeechReadyAsync`)
- Modify: `src/Winpepper.App/Hosting/NemotronEngineHolder.cs` (selected-model aware)

**Interfaces:**
- Consumes: `IDisposableTranscriber` + `ParakeetTranscriber(ownsSession: true)` (Task 12), `LocalStreamingTranscriberFactory` (Task 9), `StreamingModelLayout` (Task 10), `StreamingModelSelectionSlot` + `AppSettings.StreamingModelName` (Task 11), reworked `AsrPipelineStartupGate` (Task 12).
- Produces:
  - `PipelineHost` field `_asr` typed `Winpepper.Asr.Transcription.IDisposableTranscriber?`; factory field/param typed `Func<Winpepper.Asr.Transcription.ITranscriber?, string?, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber>` (first param NULLABLE = the optional Parakeet backup; second = its loaded model name or null).
  - Two new `PipelineHost` ctor params inserted immediately after `Func<string, bool> isAsrModelReady`:
    - `Func<string, string, Winpepper.Asr.Transcription.IDisposableTranscriber> loadBatchAsr` (args: modelDir, modelName)
    - `Func<bool> isPrimarySpeechReady`
  - `ModelsServices` addition: `public Task<bool> VerifyPrimarySpeechReadyAsync(string streamingModelName, CancellationToken ct)` and `public bool IsStreamingModelInstalled(string name)`.
  - `NemotronEngineHolder` ctor gains `Func<string>? selectedStreamingModelName = null`; `TryGet()` resolves `StreamingModelLayout.For(selected())`, and when the selection CHANGES to a different installed layout it disposes the old `WorkerProcessEngine` (killing its worker) and creates a fresh one for the new layout (keep-old-if-new-not-installed semantics, mirroring `AsrModelSwapState`). Expose `public StreamingModelLayout CurrentLayout { get; }` (last resolved) for the language hint.
- NOTE: all files here are `#if WINDOWS`. The Linux suite proves shared bricks; the behavioral safety net is (a) the pure ladder/gate/policy tests from Tasks 9–12, (b) the windows-gate build + full Windows test run in Task 22. Work in small compiles: after each numbered edit below, `dotnet build` is not possible on Linux for this project — rely on careful matching of the quoted regions, which were verified against HEAD `080e4f1`.

- [ ] **Step 1: `ModelsServices` — primary-speech verification**

Add to `src/Winpepper.App/Services/ModelsServices.cs` (below `VerifyCleanupModelReady`):

```csharp
    /// <summary>True when the named streaming model is fully installed with
    /// its runtime archive extracted (descriptor check: files non-empty +
    /// extraction marker) AND the engine layout's concrete runtime files are
    /// present (layout check: gguf + transcribe.dll + contract.json).
    /// Requiring BOTH keeps this gate in lockstep with the engine holder's
    /// own predicate (StreamingModelLayout.IsInstalled): the gate can never
    /// pass while NemotronEngineHolder.TryGet() would return null — e.g.
    /// transcribe.dll/contract.json deleted post-install by AV quarantine or
    /// manual cleanup, the sticky "verified but engine unavailable" state
    /// (V6/A18). The REVERSE divergence (layout-true/descriptor-false, e.g. a
    /// missing extraction marker) intentionally stays conservative — the
    /// Models page repair path handles it.</summary>
    public bool IsStreamingModelInstalled(string name)
        => Registry.Find(name) is { Kind: ModelKind.StreamingAsr } d
           && d.IsFullyInstalledAndExtracted(ModelsRoot)
           && Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(name).IsInstalled(ModelsRoot);

    /// <summary>Boot/onboarding gate for nemotron-first: the PRIMARY speech
    /// model is ready when the selected streaming model is installed+extracted,
    /// OR the (optional, backup) Parakeet descriptor passes full size+SHA-256
    /// verification. Preserves the invariant that a merely loadable stale
    /// Parakeet cannot satisfy the gate.</summary>
    public async Task<bool> VerifyPrimarySpeechReadyAsync(string streamingModelName, CancellationToken ct)
    {
        if (IsStreamingModelInstalled(streamingModelName)) return true;
        return await VerifyReadyAsync(ct);
    }
```

- [ ] **Step 2: `NemotronEngineHolder` — selected-model aware**

Replace the Task-7 holder body with:

```csharp
public sealed class NemotronEngineHolder
{
    private readonly string _modelsRoot;
    private readonly ILogger _log;
    private readonly Func<string> _selectedStreamingModelName;
    private readonly object _gate = new();
    private ITranscribeCppEngine? _engine;
    private StreamingModelLayout _currentLayout = StreamingModelLayout.English;

    public NemotronEngineHolder(string modelsRoot, ILogger log, Func<string>? selectedStreamingModelName = null)
    {
        _modelsRoot = modelsRoot;
        _log = log;
        _selectedStreamingModelName = selectedStreamingModelName ?? (() => StreamingModelLayout.English.Name);
    }

    /// <summary>The layout of the engine TryGet would currently serve —
    /// consumers read Language from it for the per-dictation hint.</summary>
    public StreamingModelLayout CurrentLayout { get { lock (_gate) return _currentLayout; } }

    public ITranscribeCppEngine? TryGet()
    {
        lock (_gate)
        {
            var desired = StreamingModelLayout.For(_selectedStreamingModelName());
            // Swap only when the DESIRED layout differs AND is installed —
            // keep-old-on-missing, mirroring AsrModelSwapState semantics.
            if (desired.Name != _currentLayout.Name && desired.IsInstalled(_modelsRoot))
            {
                _log.LogInformation("streaming model swap: {Old} -> {New} (worker restart)",
                    _currentLayout.Name, desired.Name);
                _engine?.Dispose(); // kills the old worker
                _engine = null;
                _currentLayout = desired;
            }
            if (!_currentLayout.IsInstalled(_modelsRoot))
            {
                // Initial selection may point at a not-yet-installed model
                // while the English one exists (or vice versa) — serve the
                // installed desired target if we have never loaded anything.
                if (_engine is null && desired.IsInstalled(_modelsRoot)) _currentLayout = desired;
                else if (_engine is null) return null;
            }
            return _engine ??= CreateWorkerEngine(_currentLayout);
        }
    }

    private ITranscribeCppEngine CreateWorkerEngine(StreamingModelLayout layout)
    {
        var exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("cannot resolve own executable path for the transcribe worker");
        var factory = new ExeWorkerProcessFactory(
            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker"),
            line => _log.LogWarning("{TranscribeCppLog}", line));
        _log.LogInformation("transcribe.cpp worker engine created ({Model})", layout.Name);
        return new WorkerProcessEngine(
            factory, layout.RuntimeDir(_modelsRoot), layout.GgufPath(_modelsRoot), layout.Name,
            log: msg => _log.LogInformation("{WorkerSupervision}", msg));
    }
}
```

Note (V5/A10): because Task 5's `WorkerProcessEngine` latches on Dispose, a live dictation whose streaming session captured the OLD engine across this swap now fails over cleanly through the batch ladder (`ObjectDisposedException` → `FallbackTranscriber` → Parakeet-or-error) instead of resurrecting an old-layout worker.

- [ ] **Step 3: `PipelineHost` — retype the seam (all sites, BOTH arms)**

Apply, in order (line numbers are HEAD-080e4f1 anchors; match on the quoted code, not the numbers):

1. Field (line 29): `private ParakeetSession? _asr;` → `private Winpepper.Asr.Transcription.IDisposableTranscriber? _asr;`
2. Factory field (line 136): →
```csharp
    private readonly Func<Winpepper.Asr.Transcription.ITranscriber?, string?, AppSettings, Action<string>, Winpepper.Asr.Transcription.IStreamingTranscriber> _buildTranscriber;
```
3. Ctor (lines 143-166): change the `transcriberFactory` param to the type above, and insert after `Func<string, bool> isAsrModelReady,`:
```csharp
        Func<string, string, Winpepper.Asr.Transcription.IDisposableTranscriber> loadBatchAsr,
        Func<bool> isPrimarySpeechReady,
```
   with matching `private readonly` fields `_loadBatchAsr`/`_isPrimarySpeechReady` assigned next to `_buildTranscriber = transcriberFactory;` (line 212).
4. `TryEnsureAsrModel` (lines 301-379) — replace the whole method with:

```csharp
    /// <summary>Nemotron-first semantics: the Parakeet ONNX model is an
    /// OPTIONAL BACKUP. This method (a) loads/swaps/disposes the backup
    /// exactly as before when its files are verified-present (keep-old-on-
    /// failure, orphan-guarded dispose), but a MISSING backup is no longer an
    /// error; and (b) returns whether a LOCAL dictation can proceed at all:
    /// true when the primary streaming model is ready OR a backup session is
    /// loaded. Cloud dictations don't require it (callers pass cloudSelected).</summary>
    private bool TryEnsureAsrModel(bool reportErrors = true)
    {
        lock (_startGate)
        {
            var desired = _resolveAsrModelName(_desiredAsrModel());
            var desiredDir = _resolveModelDir(desired);
            var ready = _isAsrModelReady(desired);
            var action = _asrSwap.Plan(desired, ready);

            switch (action)
            {
                case Winpepper.Core.Asr.AsrSwapAction.KeepCurrent:
                    break;

                case Winpepper.Core.Asr.AsrSwapAction.CannotStart:
                    // Backup not installed/verified: fine — Nemotron is primary.
                    _log.LogDebug("backup ASR model {Model} not verified-ready in {ModelDir}; continuing without a backup",
                        desired, desiredDir);
                    break;

                case Winpepper.Core.Asr.AsrSwapAction.Load:
                case Winpepper.Core.Asr.AsrSwapAction.Swap:
                    try
                    {
                        var previousModel = _asrSwap.LoadedModelName;
                        var fresh = _loadBatchAsr(desiredDir, desired);
                        var old = _asr;
                        _asr = fresh;
                        _asrSwap.CommitLoad(desired);
                        // Under _startGate; idempotent. Routed through the orphan
                        // guard: an abandoned streaming pump may still be executing
                        // a native call on the old session (RunOrDefer never blocks).
                        if (old is not null) _orphanGuard.RunOrDefer(old.Dispose);
                        _log.LogInformation(
                            "backup ASR model loaded (swap #{Generation}): {Previous} -> {Model}",
                            _asrSwap.Generation, previousModel ?? "(none)", desired);
                        _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Asr);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Failed to load backup ASR model {Model} from {ModelDir}; keeping previous session",
                            desired, desiredDir);
                        if (reportErrors)
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
                        // keep-old-on-failure; fall through to primary check
                    }
                    break;
            }

            // A LOCAL dictation needs at least one of: primary streaming model
            // ready, or a loaded backup session.
            if (_asr is not null || _isPrimarySpeechReady()) return true;

            _log.LogWarning("no local speech model available (primary not installed, no backup loaded)");
            if (reportErrors)
            {
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                    new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                    Guid.Empty);
            }
            return false;
        }
    }
```
5. Streaming factory gate, HOLD arm (lines 607-640; the gate is 617-619). Replace:
```csharp
                                var ready = TryEnsureAsrModel(reportErrors: false);
                                if ((!ready && !cloudSel) || _asr is null) return null;
                                return _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsForStream, notice =>
```
with:
```csharp
                                var ready = TryEnsureAsrModel(reportErrors: false);
                                if (!ready && !cloudSel) return null;
                                return _buildTranscriber(_asr, _asrSwap.LoadedModelName, settingsForStream, notice =>
```
6. Streaming factory gate, TOGGLE arm (lines 1242-1270, `2`-suffixed locals at 1252-1254): the identical replacement with `ready2`/`cloudSel2`/`settingsForStream2`.
7. Late/batch gate, HOLD arm (lines 851-901). Replace:
```csharp
                    var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                    var asrNow = _asr;
                    if ((!localReady && !cloudSelected) || asrNow is null)
                    {
```
with:
```csharp
                    var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                    var asrNow = _asr;
                    if (!localReady && !cloudSelected)
                    {
```
   and DELETE the now-unreachable inner special case (lines ~852-859):
```csharp
                        if (cloudSelected && asrNow is null)
                        {
                            // Cloud selected but no local session exists at all (the
                            // fallback wrapper needs one): surface this rare case.
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                                new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                                Guid.Empty);
                        }
```
   (the cloud path no longer needs a live local session — the factory substitutes the Nemotron batch ladder). Then the factory call `var transcriber = _buildTranscriber(asrNow, _asrSwap.LoadedModelName!, settingsNow, fallbackNotice);` → `var transcriber = _buildTranscriber(asrNow, _asrSwap.LoadedModelName, settingsNow, fallbackNotice);`
8. Late/batch gate, TOGGLE arm (lines 1504-1534, `asrNow2` at 1506, factory call at 1532): the identical replacement.
9. Boot gate (line 386, in `TryStartCore`): unchanged code (`if (!TryEnsureAsrModel()) return false;`) — semantics now come from the new method (primary OR backup).
10. Teardown (lines 2012-2016): unchanged code — `_asr` is `IDisposableTranscriber` and `asrAtTeardown.Dispose` still compiles via the orphan guard.
11. Mode classification — EDIT BOTH ARMS (previously listed here as untouched; V5/A7 proved the exact-match misclassifies every multilingual streamed result as `batch`, silently checking it against the 8 s batch budget instead of 2 s). At the HOLD arm (`PipelineHost.cs:909-917`) and the TOGGLE arm (`~:1547-1550`, `producedModelName2` locals), replace the exact match against `NemotronStreamingModel.Name` with name-set membership:
```csharp
                    // Streaming iff the produced name IS a known streaming layout name.
                    // For() maps unknown names to English, so only exact streaming names
                    // classify as streaming; "-batch" names stay batch.
                    var isStreaming = Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(producedModelName).Name == producedModelName;
                    timing.AsrMode =
                        isStreaming ? "streaming"
                        : Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName) ? "cloud"
                        : "batch";
```
    (toggle arm identical with `producedModelName2`). Both streaming models now stamp `asr_mode=streaming` and get the 2 s streaming budget.
12. Preserve untouched: `StreamingRouteGuard` wiring, `OrphanedPumpGuard` registrations, timing stamps, the finish-before-ensure ordering comments (814-819 / 1448-1453 — update the words "ParakeetSession" to "backup session" in those two comments).

- [ ] **Step 4: `AppShell` — wiring**

1. `BuildStreamingTranscriber` (lines 543-630) — replace the signature + local branch; keep the cloud branch's extras/deadline/config-error code verbatim:

```csharp
    /// <summary>
    /// Builds the streaming transcriber for a dictation. Local: nemotron-first
    /// via LocalStreamingTranscriberFactory (streaming when enabled+installed;
    /// Nemotron batch otherwise; optional Parakeet backup as the second ladder
    /// rung). Cloud (AssemblyAI): wrapped in FallbackStreamingTranscriber over
    /// the same local batch ladder. Static, dependencies explicit, invoked
    /// through PipelineHost's injected delegate.
    /// </summary>
    public static Winpepper.Asr.Transcription.IStreamingTranscriber BuildStreamingTranscriber(
        Winpepper.Asr.Transcription.ITranscriber? parakeetBackup,   // null when not installed
        string? backupModelName,                                    // loaded backup name or null
        AppSettings settings,
        Action<string> onFallback,
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?>? nemotronEngine,
        string streamingModelName,
        string? streamingLanguage,
        Winpepper.Asr.Transcription.IAssemblyAiClient client,
        Winpepper.Asr.Transcription.IAssemblyAiKeyStore keyStore,
        Winpepper.Asr.Transcription.AssemblyAiOptions options,
        Winpepper.Corrections.CorrectionStore? correctionStore,
        Winpepper.Core.Errors.ErrorBus errorBus,
        ILoggerFactory loggerFactory)
    {
        Func<Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?> engine = () => nemotronEngine?.Invoke();

        if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
        {
            return Winpepper.Asr.Transcription.LocalStreamingTranscriberFactory.Build(
                engine, parakeetBackup, streamingModelName, streamingLanguage,
                settings.StreamingEnabled, loggerFactory);
        }

        var localBatch = Winpepper.Asr.Transcription.LocalStreamingTranscriberFactory.BuildBatchLadder(
            engine, parakeetBackup, streamingModelName, streamingLanguage, loggerFactory);

        // ... KEEP the existing Extras() local function, cloudBatch, cloud
        // construction verbatim (lines 597-609), then:
        return new Winpepper.Asr.Transcription.FallbackStreamingTranscriber(
            cloud, localBatch,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.FallbackStreamingTranscriber>(),
            onFallback: onFallback,
            cloudDeadline: options.CloudDeadline,
            onConfigError: /* keep the existing errorBus.Report lambda verbatim */);
    }
```
   Also delete the stale comment block about `ParakeetStreamingTranscriber` (lines ~576-581) — the factory now documents selection (final comment cleanup happens again in Task 19; removing it here is fine since this region is rewritten).
2. `PipelineHost` construction (lines 340-359): add the two new args after `name => modelsServices.VerifyAsrModelReady(name),`:
```csharp
                                         (dir, name) => new Winpepper.Asr.Transcription.ParakeetTranscriber(
                                             new Winpepper.Asr.ParakeetSession(dir), name, ownsSession: true),
                                         () => modelsServices.IsStreamingModelInstalled(streamingSelection.Read()
                                             ?? settings.StreamingModelName),
```
   and change the factory lambda to:
```csharp
                                         (backup, backupName, s, onFallback) =>
                                         {
                                             var layout = nemotronHolder.CurrentLayout;
                                             return AppShell.BuildStreamingTranscriber(
                                                 backup, backupName, s, onFallback, () => nemotronHolder.TryGet(),
                                                 layout.Name, layout.Language,
                                                 aaiClient, aaiKeyStore, aaiOptions,
                                                 correctionStore, errorBus, factory);
                                         },
```
3. Streaming selection slot: next to the existing `var asrSelection = new Winpepper.Core.Settings.AsrModelSelectionSlot();` (AppShell.cs:~101, just after the `NemotronEngineHolder` construction site — the slot must be declared BEFORE the holder so reorder to slot-then-holder) add:
```csharp
        var streamingSelection = new Winpepper.Core.Settings.StreamingModelSelectionSlot();
        streamingSelection.Publish(settings.StreamingModelName); // seed with the persisted boot value
```
   expose it like `AsrModelSelection` (add `public Winpepper.Core.Settings.StreamingModelSelectionSlot StreamingModelSelection { get; }` + ctor plumbing, mirroring the ASR slot at `AppShell.cs:47-52`), and construct the holder with it:
```csharp
        var nemotronHolder = new NemotronEngineHolder(
            modelsServices.ModelsRoot, factory.CreateLogger<NemotronEngineHolder>(),
            () => streamingSelection.Read() ?? settings.StreamingModelName);
```
   Also do a boot repair for `StreamingModelName` right after the existing `AsrModelName` repair block (lines 78-92), using the same one-sanctioned-save pattern ONLY if the name is unknown:
```csharp
        var streamingResolved = modelsServices.Registry.ResolveOrDefault(
            settings.StreamingModelName, Winpepper.Models.ModelKind.StreamingAsr).Name;
        if (!string.Equals(settings.StreamingModelName, streamingResolved, StringComparison.Ordinal))
        {
            factory.CreateLogger("Winpepper.App").LogWarning(
                "Unknown streaming model {ConfiguredModel}; restored default {DefaultModel}",
                settings.StreamingModelName, streamingResolved);
            settings = settings with { StreamingModelName = streamingResolved };
            store.Save(settings); // same boot-time window as the AsrModelName repair above
        }
```
4. Startup gate (lines 449-466): construct with the new delegate:
```csharp
            var startupGate = new AsrPipelineStartupGate(
                ct => ModelsServices.VerifyPrimarySpeechReadyAsync(
                    StreamingModelSelection.Read() ?? Settings.StreamingModelName, ct),
                Pipeline.TryStart,
                onNotReady: () => ErrorBus.Report(
                    Winpepper.Core.Errors.ErrorStage.Asr,
                    new FileNotFoundException(
                        "Speech model is missing or failed verification. Open Models to download or repair it."),
                    Guid.Empty));
```
5. Auto-install call (line ~551): `await StreamingAutoInstaller.StartAsync(settings.StreamingEnabled, CancellationToken.None);` → `await StreamingAutoInstaller.StartAsync(settings.StreamingEnabled, settings.StreamingModelName, CancellationToken.None);` and update the surrounding comment (the `!OnboardingCompleted` deferral stays — on new installs onboarding now owns the install; the comment's "~1.1 GB v3 download" wording is refreshed in Task 21).
6. Boot reconciliation for picker-chosen optional models, right next to the auto-install call (V6/A17: nothing else ever completes an interrupted backup/cleanup download — the flags' only other readers live in the onboarding page, unreachable once `OnboardingCompleted`): when `settings.OnboardingCompleted` and a picker-chosen optional model is missing, kick the onboarding provisioner's `StartDownloads` in the background with the persisted scope (same join/no-op semantics; resumes the `.partial` sidecars):
```csharp
        // V6/A17: picker-chosen optional downloads interrupted by app exit
        // would otherwise never complete (the onboarding page is the only
        // other initiator and it is unreachable once onboarding completes).
        if (settings.OnboardingCompleted)
        {
            bool Missing(string name) => modelsServices.Registry.Find(name) is { } d
                && !d.IsFullyInstalledAndExtracted(modelsServices.ModelsRoot);
            if ((settings.OnboardingBackupModelChosen && Missing(settings.AsrModelName))
                || (settings.OnboardingCleanupModelChosen && Missing(settings.CleanupModelName)))
            {
                var scope = new List<string> { settings.StreamingModelName };
                if (settings.OnboardingBackupModelChosen) scope.Add(settings.AsrModelName);
                if (settings.OnboardingCleanupModelChosen) scope.Add(settings.CleanupModelName);
                onboardingProvisioner.StartDownloads(scope, settings.StreamingModelName);
            }
        }
```
   (The `OnboardingModelProvisioner` type lands in Task 18 — `Winpepper.App` is not built by the Linux suite and the windows gate runs at Task 22, matching the plan's existing Task 17→18 compile window. If you prefer strictly compiling per task, add this block in Task 18's AppShell step instead; it MUST exist by the end of Task 18.)

- [ ] **Step 5: Run the full Linux suite** (shared bricks untouched by this task must stay green): `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App src/Winpepper.Asr
git commit -m "feat(app): nemotron-first pipeline — Parakeet optional backup, worker-selected streaming model, primary-aware gates"
```

---

### Task 14: Models page — streaming model becomes selectable

**Files:**
- Modify: `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs` (streaming card: real selection + promote)
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` (streaming promote callback publishes slot + persists)
- Test: `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs` (extend)

**Interfaces:**
- Consumes: `StreamingModelSelectionSlot`, `AppSettings.StreamingModelName` (Task 11), `AppShell.StreamingModelSelection` (Task 13).
- Produces: `ModelsTabViewModel` ctor params `string currentStreamingName` and `Action<string> promoteStreaming` replacing the pinned `ModelRegistry.StreamingAsrName` + no-op promote (`ModelsTabViewModel.cs:42-47`). The streaming card's `CommitSelection()` now works like the ASR card's.

- [ ] **Step 1: Write the failing tests** — extend `ModelsTabViewModelStreamingTests.cs`:

```csharp
    [Fact]
    public void StreamingCard_ListsBothNemotronModels_AndSelectsTheCurrentOne()
    {
        var vm = CreateVm(currentStreamingName: ModelRegistry.MultilingualStreamingAsrName); // adapt to the file's builder
        vm.StreamingCard.Available.Select(d => d.Name).ShouldBe(
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.MultilingualStreamingAsrName });
        vm.StreamingCard.SelectedName.ShouldBe(ModelRegistry.MultilingualStreamingAsrName);
    }

    [Fact]
    public void StreamingCard_CommitSelection_InvokesThePromoteCallback()
    {
        string? promoted = null;
        var vm = CreateVm(promoteStreaming: n => promoted = n);
        vm.StreamingCard.SelectedName = ModelRegistry.MultilingualStreamingAsrName;
        vm.StreamingCard.CommitSelection();
        promoted.ShouldBe(ModelRegistry.MultilingualStreamingAsrName);
    }
```
Update the existing `StreamingCard_lists_exactly_the_nemotron_descriptor` test (now two descriptors) and every `ModelsTabViewModelTests` ctor call for the two new params (pass `ModelRegistry.StreamingAsrName` and `_ => { }` where the old behavior is wanted).

- [ ] **Step 2: Run to verify failures** (ctor mismatch + list count).

- [ ] **Step 3: Implement**

- `ModelsTabViewModel` ctor: insert `string currentStreamingName, Action<string> promoteStreaming` after `currentCleanupName` (before the promote callbacks) or adjacent to them matching the file's parameter grouping; build the streaming card with `selected: currentStreamingName, promote: promoteStreaming` instead of the pinned name + `_ => { }` (edit at `ModelsTabViewModel.cs:42-47`).
- `ModelsPage.xaml.cs`: where the view model is constructed (promote callbacks region `:46-59`), pass `currentStreamingName: shell.Settings.StreamingModelName` (or the slot's current read) and:
```csharp
            promoteStreaming: name =>
            {
                shell.StreamingModelSelection.Publish(name);                     // effective on the next dictation (worker swap)
                _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { StreamingModelName = name }); // durability
            },
```
  Update the no-op-promote comment at `ModelsPage.xaml.cs:158-170` accordingly. Keep the install/verify surfaces unchanged (`IsFullyInstalledAndExtracted` label logic already handles any streaming descriptor).

- [ ] **Step 4: Run `Winpepper.Models.Tests`, expect PASS; full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Models src/Winpepper.App/Views/ModelsPage.xaml.cs tests/Winpepper.Models.Tests
git commit -m "feat(models): streaming model selectable on the Models page (promote -> slot + settings)"
```

---

### Task 15: History Lab rerun without Parakeet

**Files:**
- Create: `src/Winpepper.History/Lab/RerunModelRouter.cs`
- Create: `src/Winpepper.History/Lab/LocalTranscriptionRerunService.cs`
- Delete: `src/Winpepper.History/Lab/ParakeetTranscriptionRerunService.cs`
- Modify: `src/Winpepper.App/Services/HistoryServices.cs` (inject the rerun service)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (construct `HistoryServices` with the engine hookup)
- Modify: `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs` (picker includes streaming models; promote guarded to Asr kind)
- Modify: `src/Winpepper.History/Lab/ITranscriptionRerunService.cs` (doc comment only — no longer "Runs Parakeet")
- Test: `tests/Winpepper.History.Tests/Lab/RerunModelRouterTests.cs` (new)

**Interfaces:**
- Consumes: `ITranscribeCppEngine` (batch), `StreamingModelLayout.For` (Task 10), `ParakeetSession.ModelFilesPresent(dir)` (existing static), `WavWriter.ReadMono16kInt16`, `TranscriptionRerunResult { ModelName, Text, Elapsed }` (existing record), `ITranscriptionRerunService.RerunAsync(wavPath, modelName, modelDirectory, ct)` (unchanged signature).
- Produces:

```csharp
namespace Winpepper.History.Lab;

/// <summary>Pure routing for a rerun request (Linux-tested).</summary>
public static class RerunModelRouter
{
    public enum Route { NemotronBatch, ParakeetSession, NotInstalled }
    public static Route Decide(bool isStreamingModelName, bool parakeetFilesPresent)
        => isStreamingModelName ? Route.NemotronBatch
         : parakeetFilesPresent ? Route.ParakeetSession
         : Route.NotInstalled;

    /// <summary>An engine may only serve a rerun for the model it actually
    /// loaded. Guards the wrong-model hazard: the shared holder engine serves
    /// whichever streaming model is CURRENTLY SELECTED for dictation, while
    /// the rerun stamps its result with the PICKED model name.</summary>
    public static bool EngineServes(string? engineModelName, string requestedModelName)
        => engineModelName is not null && engineModelName == requestedModelName;
}

// #if WINDOWS (like the old service)
public sealed class LocalTranscriptionRerunService : ITranscriptionRerunService
{
    public LocalTranscriptionRerunService(
        Func<string, Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine?> nemotronEngineFor, // name-keyed: returns an engine that serves EXACTLY the requested model, else null
        Func<string, bool> isStreamingModelName);
    // RerunAsync: read WAV; Decide(...); NemotronBatch -> nemotronEngineFor(modelName).TranscribeBatch
    //   (language from StreamingModelLayout.For(modelName).Language; null engine ->
    //   InvalidOperationException("Speech engine for '<name>' is unavailable. Select it as the speech model in Settings > Models (installing it if needed), then rerun."));
    // ParakeetSession -> using var session = new ParakeetSession(modelDirectory); session.Transcribe(...);
    // NotInstalled -> InvalidOperationException($"Model '{modelName}' is not installed. Download it from Settings > Models.")
}
```
- `HistoryServices` ctor becomes `HistoryServices(string historyRoot, ITranscriptionRerunService transcriptionRerun)`; `AppShell.Create()` passes:

```csharp
new LocalTranscriptionRerunService(
    name =>
    {
        var engine = nemotronHolder.TryGet(); // serves the CURRENTLY SELECTED streaming model
        return Winpepper.History.Lab.RerunModelRouter.EngineServes(engine?.ModelName, name) ? engine : null;
    },
    name => modelsServices.Registry.Find(name)?.Kind == Winpepper.Models.ModelKind.StreamingAsr)
```

  The name guard is load-bearing (do NOT wire `() => nemotronHolder.TryGet()` directly): the holder's engine serves whichever streaming model is currently selected for dictation, so without the guard, picking the non-active Nemotron in the History Lab would silently transcribe with the ACTIVE model while stamping the picked name (both layouts' `Language` is `null`, so nothing else differentiates the call) — and would even "succeed" when the picked model isn't installed at all. With the guard, a picked streaming model that the shared engine doesn't serve gets a null engine and surfaces the actionable inline error (the approved "clear unavailable state"); comparing against the RETURNED engine's `ModelName` (not `CurrentLayout`) makes the check immune to holder-state staleness.
- `HistoryDetailPage.xaml.cs:57` picker source: `models.Registry.ByKind(ModelKind.Asr).Concat(models.Registry.ByKind(ModelKind.StreamingAsr))`. The "Promote as default" handler (`:63-67`) is guarded: only invoke for descriptors whose `Kind == ModelKind.Asr` (a streaming name must not be published into `AsrModelSelection`; disable/hide the promote button when a streaming model is selected in the panel).
- Rerun error surface: `RerunPanelViewModel.Runner` is awaited by the panel — check `src/Winpepper.History/ViewModels/RerunPanelViewModel.cs` for its exception handling; if the runner's exceptions are unhandled, wrap the runner lambda in `HistoryDetailViewModel` (lines 35-42) with `try { ... } catch (InvalidOperationException e) { return $"[{e.Message}]"; }` so a missing model shows a clear inline message instead of crashing (this IS the approved "clear 'model not installed' state").

- [ ] **Step 1: Write the failing router tests**

Create `tests/Winpepper.History.Tests/Lab/RerunModelRouterTests.cs`:

```csharp
using Shouldly;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public sealed class RerunModelRouterTests
{
    [Theory]
    [InlineData(true,  true,  RerunModelRouter.Route.NemotronBatch)]
    [InlineData(true,  false, RerunModelRouter.Route.NemotronBatch)]
    [InlineData(false, true,  RerunModelRouter.Route.ParakeetSession)]
    [InlineData(false, false, RerunModelRouter.Route.NotInstalled)]
    public void Decide_RoutesByKindThenPresence(bool streaming, bool filesPresent, RerunModelRouter.Route expected)
        => RerunModelRouter.Decide(streaming, filesPresent).ShouldBe(expected);

    [Theory]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-en",    true)]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-multi", false)]
    [InlineData(null,                    "nemotron-streaming-multi", false)]
    public void EngineServes_RequiresExactModelMatch(string? engineModelName, string requested, bool expected)
        => RerunModelRouter.EngineServes(engineModelName, requested).ShouldBe(expected);
}
```

- [ ] **Step 2: Run to verify failure** (`CS0246 ... 'RerunModelRouter'`).

- [ ] **Step 3: Implement** — router (code above), then the service:

```csharp
#if WINDOWS
using System.Diagnostics;
using Winpepper.Asr;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.History.Lab;

/// <summary>Reruns a history WAV against a locally installed model: Nemotron
/// (batch, via a NAME-KEYED engine provider that must return an engine serving
/// exactly the requested model, or null) or Parakeet (fresh ONNX session per
/// call). Missing/unavailable models fail with an actionable message instead
/// of a raw FileNotFoundException or a silent wrong-model transcript.
/// Replaces ParakeetTranscriptionRerunService.</summary>
public sealed class LocalTranscriptionRerunService : ITranscriptionRerunService
{
    private readonly Func<string, ITranscribeCppEngine?> _nemotronEngineFor;
    private readonly Func<string, bool> _isStreamingModelName;

    public LocalTranscriptionRerunService(
        Func<string, ITranscribeCppEngine?> nemotronEngineFor,
        Func<string, bool> isStreamingModelName)
    {
        _nemotronEngineFor = nemotronEngineFor;
        _isStreamingModelName = isStreamingModelName;
    }

    public Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var samples = WavWriter.ReadMono16kInt16(wavPath);
            var route = RerunModelRouter.Decide(
                _isStreamingModelName(modelName),
                ParakeetSession.ModelFilesPresent(modelDirectory));
            var sw = Stopwatch.StartNew();
            string text;
            switch (route)
            {
                case RerunModelRouter.Route.NemotronBatch:
                    // Name-keyed: the provider returns null unless the shared
                    // engine serves EXACTLY modelName (see AppShell wiring) —
                    // never transcribe with a different model than we stamp.
                    var engine = _nemotronEngineFor(modelName)
                        ?? throw new InvalidOperationException(
                            $"Speech engine for '{modelName}' is unavailable. Select it as the speech model in Settings > Models (installing it if needed), then rerun.");
                    text = engine.TranscribeBatch(samples, StreamingModelLayout.For(modelName).Language, out _);
                    break;
                case RerunModelRouter.Route.ParakeetSession:
                    using (var session = new ParakeetSession(modelDirectory))
                        text = session.Transcribe(samples).Text;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Model '{modelName}' is not installed. Download it from Settings > Models.");
            }
            sw.Stop();
            return new TranscriptionRerunResult { ModelName = modelName, Text = text, Elapsed = sw.Elapsed };
        }, ct);
    }
}
#endif
```
Then: delete `ParakeetTranscriptionRerunService.cs`; update `HistoryServices` ctor + `AppShell` construction + `HistoryDetailPage` picker/promote guard + `ITranscriptionRerunService` doc comment; add the VM runner guard per the Interfaces note. Caveat to note in code: a rerun issued DURING a live streaming dictation shares the worker — the worker will dispose the live stream to serve the batch (dictation falls back to batch). Document with a one-line comment at the engine call.

- [ ] **Step 4: Run `Winpepper.History.Tests`, expect PASS; full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src/Winpepper.History src/Winpepper.App tests/Winpepper.History.Tests
git commit -m "feat(history): model-agnostic local rerun service (nemotron batch or parakeet; clear not-installed state)"
```

---

### Task 16: Onboarding provisioning seam (Core interface + pure planner)

**Files:**
- Create: `src/Winpepper.Core/ViewModels/IOnboardingModelProvisioner.cs`
- Create: `src/Winpepper.Models/DownloadBatchPlanner.cs`
- Test: `tests/Winpepper.Models.Tests/DownloadBatchPlannerTests.cs`

**Interfaces:**
- Consumes: `ModelRegistry`/`ModelDescriptor` (Models); nothing platform-specific.
- Produces (used by Tasks 17, 18):

```csharp
// src/Winpepper.Core/ViewModels/IOnboardingModelProvisioner.cs
namespace Winpepper.Core.ViewModels;

public sealed record OnboardingDownloadState(
    double ProgressPercent,        // 0..100 aggregate across the batch, byte-weighted
    string StatusText,             // e.g. "Downloading English speech model…", "All models verified — ready to dictate."
    string? Error,                 // sticky until the next StartDownloads
    bool SpeechModelReady);        // true only after the speech model's FILES verified AND a one-shot
                                   // ENGINE LOAD PROBE succeeded (spawn worker -> Load -> dispose);
                                   // only then may the pipeline start

/// <summary>Background, multi-model onboarding downloads. StartDownloads never
/// throws and never blocks the caller; it downloads the SPEECH model first
/// (it gates Test dictation), then the optional models, publishing progress
/// via StateChanged. SpeechModelReady requires BOTH file verification
/// (size + SHA-256 + extraction) AND a successful one-shot ENGINE LOAD PROBE
/// (spawn a worker for the selected layout, issue Load, dispose) — file checks
/// alone cannot see a missing VC++ redistributable, a model/runtime ABI
/// mismatch, or a worker spawn failure, so this closes the "onboarding says
/// ready but the first dictation fails" hole (V6/A16). On probe failure the
/// provisioner publishes a sticky Error with actionable text. Calling
/// StartDownloads again while a run is active is a no-op join; calling it
/// after a failure retries. The underlying downloads survive the caller
/// navigating away (coordinator/downloader semantics).</summary>
public interface IOnboardingModelProvisioner
{
    OnboardingDownloadState State { get; }
    event EventHandler<OnboardingDownloadState>? StateChanged;
    void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName);
}
```

```csharp
// src/Winpepper.Models/DownloadBatchPlanner.cs
namespace Winpepper.Models;

public static class DownloadBatchPlanner
{
    /// <summary>Resolve names -> descriptors, drop unknown/manual-only/already
    /// installed(AndExtracted), order the speech model first.</summary>
    public static IReadOnlyList<ModelDescriptor> Plan(
        ModelRegistry registry, string installRoot,
        IReadOnlyList<string> names, string speechModelName);

    /// <summary>Byte-weighted aggregate percent across the WHOLE batch
    /// (including already-installed members as complete).</summary>
    public static double AggregatePercent(
        IReadOnlyList<(long TotalBytes, long DoneBytes)> perDescriptor);
}
```

- [ ] **Step 1: Write the failing planner tests**

Create `tests/Winpepper.Models.Tests/DownloadBatchPlannerTests.cs`:

```csharp
using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public sealed class DownloadBatchPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"planner-{Guid.NewGuid():N}");
    public DownloadBatchPlannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Plan_OrdersSpeechFirst_AndDropsUnknownNames()
    {
        var r = new ModelRegistry();
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { ModelRegistry.DefaultCleanupName, ModelRegistry.StreamingAsrName, "nonsense" },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.DefaultCleanupName });
    }

    [Fact]
    public void Plan_SkipsFullyInstalledDescriptors()
    {
        var r = new ModelRegistry();
        var cleanup = r.Find(ModelRegistry.DefaultCleanupName)!;
        // Materialize the cleanup files at their exact sizes so IsFullyInstalled is true.
        foreach (var f in cleanup.Files)
        {
            var path = Path.Combine(_root, cleanup.InstallDirRelative, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            fs.SetLength(f.SizeBytes);
        }
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.DefaultCleanupName },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(new[] { ModelRegistry.StreamingAsrName });
    }

    [Fact]
    public void Plan_SkipsManualInstallOnly()
    {
        var r = new ModelRegistry();
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { "sotto-cleanup-lfm25-350m-q8_0", ModelRegistry.StreamingAsrName },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(new[] { ModelRegistry.StreamingAsrName });
    }

    [Theory]
    [InlineData(0, 0)]
    public void AggregatePercent_EmptyBatch_Is100(long _, long __)
        => DownloadBatchPlanner.AggregatePercent(Array.Empty<(long, long)>()).ShouldBe(100);

    [Fact]
    public void AggregatePercent_IsByteWeighted_AndClamped()
    {
        DownloadBatchPlanner.AggregatePercent(new[] { (100L, 100L), (300L, 0L) }).ShouldBe(25);
        DownloadBatchPlanner.AggregatePercent(new[] { (100L, 150L) }).ShouldBe(100); // overshoot clamps
    }
}
```

- [ ] **Step 2: Run to verify failure** (`CS0246 ... 'DownloadBatchPlanner'`).

- [ ] **Step 3: Implement**

`src/Winpepper.Models/DownloadBatchPlanner.cs`:

```csharp
namespace Winpepper.Models;

/// <summary>Pure planning math for the onboarding download batch —
/// Linux-tested; the App-side OnboardingModelProvisioner drives it.</summary>
public static class DownloadBatchPlanner
{
    public static IReadOnlyList<ModelDescriptor> Plan(
        ModelRegistry registry, string installRoot,
        IReadOnlyList<string> names, string speechModelName)
    {
        var unique = names.Distinct(StringComparer.Ordinal);
        return unique
            .Select(registry.Find)
            .Where(d => d is not null).Select(d => d!)
            .Where(d => !d.ManualInstallOnly)
            .Where(d => !d.IsFullyInstalledAndExtracted(installRoot))
            .OrderBy(d => d.Name == speechModelName ? 0 : 1)
            .ToList();
    }

    public static double AggregatePercent(IReadOnlyList<(long TotalBytes, long DoneBytes)> perDescriptor)
    {
        var total = perDescriptor.Sum(p => p.TotalBytes);
        if (total <= 0) return 100;
        var done = perDescriptor.Sum(p => Math.Clamp(p.DoneBytes, 0, p.TotalBytes));
        return Math.Clamp(100.0 * done / total, 0, 100);
    }
}
```
And create `src/Winpepper.Core/ViewModels/IOnboardingModelProvisioner.cs` with the record + interface from the Interfaces block (verbatim).

- [ ] **Step 4: Run `Winpepper.Models.Tests` planner class, expect PASS; full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/IOnboardingModelProvisioner.cs src/Winpepper.Models/DownloadBatchPlanner.cs tests/Winpepper.Models.Tests/DownloadBatchPlannerTests.cs
git commit -m "feat(core,models): onboarding model-provisioner seam + pure download-batch planner"
```

---

### Task 17: OnboardingViewModel — picker state, background downloads, verified Test-Dictation gate

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Create: `src/Winpepper.Core/ViewModels/ModelPickerCatalog.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs` (rework the DownloadModels/TestDictation cases)
- Test: `tests/Winpepper.Core.Tests/ViewModels/OnboardingModelPickerTests.cs` (new)

**Interfaces:**
- Consumes: `IOnboardingModelProvisioner`/`OnboardingDownloadState` (Task 16), `ISettingsWriter`, `AppSettings.StreamingModelName`/`OnboardingBackupModelChosen`/`OnboardingCleanupModelChosen` (Task 11).
- Produces (used by Task 18):

```csharp
// src/Winpepper.Core/ViewModels/ModelPickerCatalog.cs
namespace Winpepper.Core.ViewModels;

/// <summary>Registry facts the picker needs, supplied by the page (Core has
/// no Models reference by design — names+bytes travel as plain data).</summary>
public sealed record ModelPickerCatalog(
    string EnglishName, long EnglishBytes,
    string MultilingualName, long MultilingualBytes,
    string BackupName, long BackupBytes,
    string CleanupName, long CleanupBytes);
```

  `OnboardingViewModel` NEW ctor (replaces the old one — `IAsrProvisioningService` is dropped; the page updates in Task 18; between the two commits `Winpepper.App` does not compile on Windows, which is acceptable since the gate runs at Task 22 — note it in the commit message):
```csharp
public OnboardingViewModel(
    ISettingsWriter writer,
    Func<bool> tryStartPipeline,
    IHotkeyValidator validator,
    IOnboardingModelProvisioner modelProvisioner,
    ModelPickerCatalog catalog)
```
  New/changed public members (binding for the page and tests):
  - `bool MultilingualSelected { get; set; }` (default false = English), `bool BackupModelSelected { get; set; }`, `bool CleanupModelSelected { get; set; }` — setters `Raise()` + `Raise(nameof(TotalDownloadText))`.
  - `string SelectedSpeechModelName => MultilingualSelected ? _catalog.MultilingualName : _catalog.EnglishName;`
  - `string TotalDownloadText` — `"Total download: " + FormatTotal(...)` where per-item MB = `(int)Math.Round(bytes / 1_000_000.0)`; total = speech MB + checked options' MB; `FormatTotal`: `totalMb >= 1000 ? $"{totalMb / 1000.0:0.0} GB" : $"{totalMb} MB"`.
  - `static string SizeLabel(long bytes)` → `"~{mb rounded to nearest 10} MB"` (`~760 MB`, `~780 MB`, `~670 MB`, `~490 MB`) — the page uses it for the four card size labels.
  - `bool SpeechModelVerified { get; }` (private set; raises `CanAdvance`).
  - `CanAdvance`: `DownloadModels => true` (click always allowed; downloads are background), `TestDictation => _testDictationDone && SpeechModelVerified`.
  - `CanRetry`: `Step == TestDictation && !SpeechModelVerified && DownloadError is not null`; `void RetryDownloads()` re-issues `StartDownloads` with the persisted selection.
  - `AdvanceAsync` `DownloadModels` case: persist `StreamingModelName = SelectedSpeechModelName`, `OnboardingBackupModelChosen`, `OnboardingCleanupModelChosen` via `QueueAndFlushAsync`; call `_modelProvisioner.StartDownloads(BuildDownloadNames(), SelectedSpeechModelName)`; `Step = TestDictation` immediately. `BuildDownloadNames()` = `[speech] + (backup? [_catalog.BackupName]) + (cleanup? [_catalog.CleanupName])`.
  - `InitializeFrom(settings, persistedMicPresent, modelsResolved)`: additionally hydrate `MultilingualSelected = settings.StreamingModelName == _catalog.MultilingualName`, `BackupModelSelected = settings.OnboardingBackupModelChosen`, `CleanupModelSelected = settings.OnboardingCleanupModelChosen`; and when the resolved starting step is `TestDictation` (or later resume with pending optional downloads), call `_modelProvisioner.StartDownloads(...)` with the persisted selection so verification re-runs and interrupted downloads resume.
  - Provisioner state handler: `DownloadProgressPercent = state.ProgressPercent; DownloadStatus = state.StatusText; if (state.Error is not null) DownloadError = state.Error;` and on the FIRST `state.SpeechModelReady == true`: `SpeechModelVerified = _tryStartPipeline();` — if the pipeline fails to start, `DownloadError = "The dictation pipeline could not start. Retry after checking the speech model."` and `SpeechModelVerified` stays false (Retry re-attempts both).
  - The old `ProvisionAndStartPipelineAsync`, `ApplyProvisioningState`, `AsrProvisioningState` plumbing, and the stale Back/Skip doc comments (`:52`, `:198-203`) are removed/rewritten.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/OnboardingModelPickerTests.cs` (new file; reuse `OnboardingViewModelTests`' `FakeWriter`/`PermissiveValidator` fakes by making them `internal` in that file or duplicating minimal local copies):

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public sealed class OnboardingModelPickerTests
{
    private static readonly ModelPickerCatalog Catalog = new(
        EnglishName: "nemotron-streaming-en", EnglishBytes: 755_608_086,
        MultilingualName: "nemotron-streaming-multi", MultilingualBytes: 777_052_150,
        BackupName: "parakeet-tdt-0.6b-v3", BackupBytes: 670_479_942,
        CleanupName: "qwen2.5-0.5b-instruct-q4_k_m", CleanupBytes: 491_400_032);

    private sealed class FakeProvisioner : IOnboardingModelProvisioner
    {
        public OnboardingDownloadState State { get; private set; } =
            new(0, "Waiting", null, SpeechModelReady: false);
        public event EventHandler<OnboardingDownloadState>? StateChanged;
        public List<(IReadOnlyList<string> Names, string Speech)> Starts { get; } = new();
        public void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName)
            => Starts.Add((modelNames, speechModelName));
        public void Publish(OnboardingDownloadState s) { State = s; StateChanged?.Invoke(this, s); }
    }

    private static (OnboardingViewModel Vm, FakeProvisioner Prov, FakeWriter Writer, List<bool> PipelineStarts)
        CreateAtDownloadStep(bool pipelineStartResult = true)
    {
        var prov = new FakeProvisioner();
        var writer = new FakeWriter();
        var starts = new List<bool>();
        var vm = new OnboardingViewModel(writer, () => { starts.Add(pipelineStartResult); return pipelineStartResult; },
            new PermissiveValidator(), prov, Catalog);
        vm.SelectedMicDeviceId = "mic-1";
        vm.AdvanceAsync().GetAwaiter().GetResult();  // PickMic -> PickHotkeys
        vm.AdvanceAsync().GetAwaiter().GetResult();  // PickHotkeys -> DownloadModels
        return (vm, prov, writer, starts);
    }

    [Fact]
    public void Defaults_EnglishSelected_OptionsUnchecked_TotalIsSpeechOnly()
    {
        var (vm, _, _, _) = CreateAtDownloadStep();
        vm.MultilingualSelected.ShouldBeFalse();
        vm.BackupModelSelected.ShouldBeFalse();
        vm.CleanupModelSelected.ShouldBeFalse();
        vm.TotalDownloadText.ShouldBe("Total download: 756 MB");
    }

    [Theory]
    [InlineData(false, false, false, "Total download: 756 MB")]
    [InlineData(false, true,  false, "Total download: 1.4 GB")]   // 756+670=1426
    [InlineData(false, false, true,  "Total download: 1.2 GB")]   // 756+491=1247
    [InlineData(false, true,  true,  "Total download: 1.9 GB")]   // 1917
    [InlineData(true,  false, false, "Total download: 777 MB")]
    [InlineData(true,  true,  true,  "Total download: 1.9 GB")]   // 777+670+491=1938
    public void TotalDownload_SumsSelectedItems(bool multi, bool backup, bool cleanup, string expected)
    {
        var (vm, _, _, _) = CreateAtDownloadStep();
        vm.MultilingualSelected = multi;
        vm.BackupModelSelected = backup;
        vm.CleanupModelSelected = cleanup;
        vm.TotalDownloadText.ShouldBe(expected);
    }

    [Fact]
    public void SizeLabels_RoundToNearestTenWithTilde()
    {
        OnboardingViewModel.SizeLabel(755_608_086).ShouldBe("~760 MB");
        OnboardingViewModel.SizeLabel(777_052_150).ShouldBe("~780 MB");
        OnboardingViewModel.SizeLabel(670_479_942).ShouldBe("~670 MB");
        OnboardingViewModel.SizeLabel(491_400_032).ShouldBe("~490 MB");
    }

    [Fact]
    public async Task Advance_PersistsChoices_StartsBackgroundDownloads_AndMovesToTestDictation()
    {
        var (vm, prov, writer, _) = CreateAtDownloadStep();
        vm.MultilingualSelected = true;
        vm.BackupModelSelected = true;

        await vm.AdvanceAsync();

        vm.Step.ShouldBe(OnboardingStep.TestDictation);      // advance is IMMEDIATE (background download)
        prov.Starts.Count.ShouldBe(1);
        prov.Starts[0].Speech.ShouldBe("nemotron-streaming-multi");
        prov.Starts[0].Names.ShouldBe(new[] { "nemotron-streaming-multi", "parakeet-tdt-0.6b-v3" });
        var s = writer.Applied(new AppSettings());           // FakeWriter applies queued mutators
        s.StreamingModelName.ShouldBe("nemotron-streaming-multi");
        s.OnboardingBackupModelChosen.ShouldBeTrue();
        s.OnboardingCleanupModelChosen.ShouldBeFalse();
    }

    [Fact]
    public async Task TestDictation_GatesOnSpeechVerifiedAndPipelineStart()
    {
        var (vm, prov, _, pipelineStarts) = CreateAtDownloadStep();
        await vm.AdvanceAsync();                              // -> TestDictation, downloads running
        vm.TestDictationDone = true;
        vm.CanAdvance.ShouldBeFalse();                        // model not verified yet
        pipelineStarts.ShouldBeEmpty();

        prov.Publish(new OnboardingDownloadState(100, "All models verified — ready to dictate.", null, SpeechModelReady: true));

        vm.SpeechModelVerified.ShouldBeTrue();
        pipelineStarts.Count.ShouldBe(1);
        vm.CanAdvance.ShouldBeTrue();

        await vm.AdvanceAsync();                              // Finish
        vm.Step.ShouldBe(OnboardingStep.Done);
    }

    [Fact]
    public async Task PipelineStartFailure_BlocksFinish_AndRetryReruns()
    {
        var (vm, prov, _, _) = CreateAtDownloadStep(pipelineStartResult: false);
        await vm.AdvanceAsync();
        prov.Publish(new OnboardingDownloadState(100, "ready", null, SpeechModelReady: true));

        vm.SpeechModelVerified.ShouldBeFalse();
        vm.DownloadError.ShouldNotBeNull();
        vm.TestDictationDone = true;
        vm.CanAdvance.ShouldBeFalse();
        vm.CanRetry.ShouldBeTrue();

        vm.RetryDownloads();
        prov.Starts.Count.ShouldBe(2);
    }

    [Fact]
    public void Resume_AtTestDictation_RehydratesSelection_AndRestartsDownloadsForVerification()
    {
        var prov = new FakeProvisioner();
        var vm = new OnboardingViewModel(new FakeWriter(), () => true, new PermissiveValidator(), prov, Catalog);
        var settings = new AppSettings
        {
            MicDeviceId = "mic-1",
            StreamingModelName = "nemotron-streaming-multi",
            OnboardingBackupModelChosen = true,
        };
        vm.InitializeFrom(settings, persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
        vm.MultilingualSelected.ShouldBeTrue();
        prov.Starts.Count.ShouldBe(1); // verification/resume kick
        prov.Starts[0].Names.ShouldBe(new[] { "nemotron-streaming-multi", "parakeet-tdt-0.6b-v3" });
    }
}
```
Then rework `OnboardingViewModelTests.cs`: update every `new OnboardingViewModel(...)` to the new ctor (its `FakeProvisioner : IAsrProvisioningService` becomes a `IOnboardingModelProvisioner` fake or is deleted); the five DownloadModels-behavior tests (`DownloadModels_AdvancesOnlyAfterVerifiedReadinessAndPipelineStart`, failure/retry/verify-false/pipeline-false cases, `DownloadModels_CannotSkipIntoTestDictation`) are superseded by the picker tests above — DELETE them and keep/adapt the mic/hotkey/persistence/step-resume tests. `FakeWriter` must expose an `Applied(AppSettings seed)` helper that folds its captured mutators over a seed (add it if missing).

- [ ] **Step 2: Run to verify failures** — build `Winpepper.Core.Tests`; expected ctor/type errors.

- [ ] **Step 3: Implement the VM changes** exactly per the Interfaces block. Implementation notes:
  - Keep the hand-rolled `Raise([CallerMemberName])` idiom; raise `CanAdvance`/`CanRetry`/`TotalDownloadText` from the relevant setters.
  - `SizeLabel`: `var mb = (int)Math.Round(bytes / 1_000_000.0); var rounded = (int)(Math.Round(mb / 10.0) * 10); return $"~{rounded} MB";`
  - Total: `var totalMb = (int)Math.Round(SelectedSpeechBytes / 1_000_000.0) + (BackupModelSelected ? BackupMb : 0) + ...` — compute each item's MB with the SAME `Math.Round(bytes/1e6)` before summing (the tests' 756/777/670/491 values pin this).
  - Subscribe `modelProvisioner.StateChanged` in the ctor; unsubscribe in `Dispose()` (replacing the old provisioner unsubscribe).
  - `SpeechModelVerified` must only attempt `_tryStartPipeline()` once per `SpeechModelReady` rising edge (guard with a bool reset by `RetryDownloads`).

- [ ] **Step 4: Run the two test classes, expect PASS; full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): onboarding model-picker state, background downloads, verified test-dictation gate (App page rewired next commit)"
```

---

### Task 18: Onboarding page — picker UI + App provisioner implementation

**Files:**
- Create: `src/Winpepper.App/Services/OnboardingModelProvisioner.cs`
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml`
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml.cs`
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (construct + expose the provisioner)

**Interfaces:**
- Consumes: `IOnboardingModelProvisioner`/`ModelPickerCatalog` (Tasks 16–17), `DownloadBatchPlanner` (Task 16), `ModelsServices` (`IDownloader` face + `ModelsRoot` + `Registry`), `ModelsTabViewModel.SharedOperationGateFor` (existing cross-component download gate), `ModelFilesVerifier.VerifyAsync(descriptor, root, ct)` (existing), `TarGzExtractor.IsExtracted` via `descriptor.IsFullyInstalledAndExtracted`.
- Produces: `AppShell.OnboardingProvisioner` (`IOnboardingModelProvisioner`) property; the finished Step-3 picker page. This commit restores `Winpepper.App` compilation after Task 17.

- [ ] **Step 1: Implement `OnboardingModelProvisioner`**

Create `src/Winpepper.App/Services/OnboardingModelProvisioner.cs`:

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Core.ViewModels;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Services;

/// <summary>
/// Background multi-model downloads for onboarding. Speech model first (it
/// gates Test dictation), optional models after. Serializes with the Models
/// page and StreamingAutoInstaller via the shared per-downloader operation
/// gate, so nothing double-downloads. Never throws; errors surface in State.
/// "Verified" for the speech model = per-file size + SHA-256 + extraction
/// (the bar the old blocking Step 3 enforced) PLUS a one-shot ENGINE LOAD
/// PROBE (spawn worker -> Load -> dispose, injected as a delegate so this
/// class stays testable) — file checks cannot see a missing VC++
/// redistributable, an ABI mismatch, or a spawn failure (V6/A16).
/// StateChanged is raised on the UI thread via the DispatcherQueue captured
/// at construction: WinUI bindings are thread-affine, and subscribers (the
/// onboarding VM) mutate bound properties — raising from the download thread
/// would risk RPC_E_WRONG_THREAD (V2/A12).
/// </summary>
public sealed class OnboardingModelProvisioner : IOnboardingModelProvisioner
{
    private readonly ModelsServices _models;
    private readonly ILogger _log;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;
    private readonly Func<string, CancellationToken, Task<bool>> _engineLoadProbe;
    private readonly object _gate = new();
    private Task? _run;
    private OnboardingDownloadState _state = new(0, "Waiting to download", null, false);

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.Ordinal)
    {
        [ModelRegistry.StreamingAsrName] = "English speech model",
        [ModelRegistry.MultilingualStreamingAsrName] = "Multilingual speech model",
        [ModelRegistry.DefaultAsrName] = "backup speech model",
        [ModelRegistry.DefaultCleanupName] = "text cleanup model",
    };

    public OnboardingModelProvisioner(ModelsServices models, ILogger log,
        Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue,
        Func<string, CancellationToken, Task<bool>> engineLoadProbe)
    {
        _models = models;
        _log = log;
        _dispatcherQueue = dispatcherQueue;   // captured at construction (AppShell.Create runs on the UI thread)
        _engineLoadProbe = engineLoadProbe;   // AppShell injects the worker-engine-based probe
    }

    public OnboardingDownloadState State { get { lock (_gate) return _state; } }
    public event EventHandler<OnboardingDownloadState>? StateChanged;

    public void StartDownloads(IReadOnlyList<string> modelNames, string speechModelName)
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) return; // join the active run
            _run = Task.Run(() => RunAsync(modelNames.ToArray(), speechModelName));
        }
    }

    private async Task RunAsync(IReadOnlyList<string> names, string speechModelName)
    {
        try
        {
            var registry = _models.Registry;
            var root = _models.ModelsRoot;
            var plan = DownloadBatchPlanner.Plan(registry, root, names, speechModelName);
            // Track the WHOLE selection (installed members count as done bytes).
            var selection = names.Select(registry.Find).Where(d => d is not null).Select(d => d!).ToList();
            var done = selection.ToDictionary(d => d.Name,
                d => plan.Any(p => p.Name == d.Name) ? 0L : d.TotalSizeBytes);

            var opGate = ModelsTabViewModel.SharedOperationGateFor(_models);
            foreach (var descriptor in plan)
            {
                Publish(Percent(selection, done), $"Downloading {Friendly(descriptor)}…", null, false);
                await opGate.WaitAsync();
                try
                {
                    var progress = new Progress<DownloadProgress>(p =>
                    {
                        // per-file bytes -> per-descriptor tally (sum file dones)
                        lock (_gate) { done[descriptor.Name] = TallyFor(descriptor, p, done[descriptor.Name]); }
                        Publish(Percent(selection, done), $"Downloading {Friendly(descriptor)}…", null,
                            SpeechReadyNow(speechModelName));
                    });
                    await _models.DownloadAsync(descriptor, root, progress, CancellationToken.None);
                    done[descriptor.Name] = descriptor.TotalSizeBytes;
                }
                finally { opGate.Release(); }

                if (descriptor.Name == speechModelName)
                {
                    Publish(Percent(selection, done), "Verifying speech model…", null, false);
                    var error = await VerifySpeechDeepAsync(speechModelName);
                    if (error is not null)
                    {
                        Publish(Percent(selection, done), "Speech model failed verification.", error, false);
                        return;
                    }
                    Publish(Percent(selection, done), "Speech model ready — keep going while the rest downloads.", null, true);
                }
            }

            // Plan may be empty (everything installed) — still verify + probe the speech model.
            if (!State.SpeechModelReady)
            {
                var error = await VerifySpeechDeepAsync(speechModelName);
                if (error is not null)
                {
                    Publish(100, "Speech model failed verification.", error, false);
                    return;
                }
            }
            Publish(100, "All models verified — ready to dictate.", null, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "onboarding model download failed");
            Publish(State.ProgressPercent, "Download failed.", ex.Message, State.SpeechModelReady);
        }
    }

    /// <summary>Speech readiness = files verified AND a one-shot ENGINE LOAD
    /// PROBE (spawn a worker for the selected layout, issue Load, dispose).
    /// File checks alone cannot see a missing VC++ redistributable, a
    /// model/runtime ABI mismatch, or a worker spawn failure (V6/A16) — the
    /// probe closes the "onboarding says ready but the first dictation
    /// fails" hole. Returns null when ready, else sticky actionable error text.</summary>
    private async Task<string?> VerifySpeechDeepAsync(string speechModelName)
    {
        var d = _models.Registry.Find(speechModelName);
        if (d is null) return "The speech model could not be verified. Retry the download.";
        var filesOk = await ModelFilesVerifier.VerifyAsync(d, _models.ModelsRoot, CancellationToken.None)
                      && (d.Kind != ModelKind.StreamingAsr || d.IsFullyInstalledAndExtracted(_models.ModelsRoot));
        if (!filesOk) return "The speech model could not be verified. Retry the download.";
        Publish(State.ProgressPercent, "Checking the speech engine…", null, false);
        var probeOk = await _engineLoadProbe(speechModelName, CancellationToken.None);
        if (!probeOk)
            return $"The {Friendly(d)} downloaded and verified, but its speech engine failed to load. " +
                   "Open Settings > Models to repair it. A missing Microsoft Visual C++ x64 Redistributable " +
                   "is the most common cause.";
        return null;
    }

    private bool SpeechReadyNow(string speechModelName) => State.SpeechModelReady; // monotonic within a run

    private static string Friendly(ModelDescriptor d)
        => FriendlyNames.TryGetValue(d.Name, out var n) ? n : d.DisplayName;

    private static double Percent(IReadOnlyList<ModelDescriptor> selection, IReadOnlyDictionary<string, long> done)
        => DownloadBatchPlanner.AggregatePercent(
            selection.Select(d => (d.TotalSizeBytes, done.GetValueOrDefault(d.Name))).ToList());

    private static long TallyFor(ModelDescriptor d, DownloadProgress p, long previous)
    {
        // DownloadProgress is per-file; approximate the descriptor tally as
        // completed-files bytes + current file's BytesDownloaded. Files
        // download sequentially, so summing monotonically is safe:
        var precedingBytes = 0L;
        foreach (var f in d.Files)
        {
            if (f.RelativePath == p.FileRelativePath)
                return Math.Max(previous, precedingBytes + Math.Clamp(p.BytesDownloaded, 0, f.SizeBytes));
            precedingBytes += f.SizeBytes;
        }
        return previous;
    }

    private void Publish(double percent, string status, string? error, bool speechReady)
    {
        OnboardingDownloadState s;
        lock (_gate)
        {
            // SpeechModelReady is MONOTONIC within a run: once true it stays
            // true (later optional-model errors must not re-lock the gate).
            var ready = speechReady || _state.SpeechModelReady;
            s = _state = new OnboardingDownloadState(percent, status, error, ready);
        }
        // WinUI bindings are thread-affine: subscribers (the onboarding VM)
        // mutate bound properties, so StateChanged must be raised on the UI
        // thread — anything else risks RPC_E_WRONG_THREAD (V2/A12).
        _dispatcherQueue.TryEnqueue(() => StateChanged?.Invoke(this, s));
    }
}
#endif
```
(Verify `ModelFilesVerifier.VerifyAsync`'s exact signature in `src/Winpepper.Models/` — `ModelsServices.VerifyCleanupModelReady` calls `ModelFilesVerifier.VerifyAsync(descriptor, ModelsRoot, CancellationToken.None)`, so match that. A NEW `StartDownloads` run starts from `RunAsync`'s own flow, whose first `Publish(..., speechReady: false)` happens before any `_state` reuse — reset `_state` to a fresh non-ready state at the top of `RunAsync` so a retry re-verifies rather than inheriting a stale `SpeechModelReady`.)

In `AppShell`: construct after `modelsServices` — the ctor takes the UI-thread `DispatcherQueue` (`AppShell.Create` runs on the UI thread) and a load-probe delegate built on the worker engine machinery (injected so the provisioner stays testable):

```csharp
        var onboardingProvisioner = new Winpepper.App.Services.OnboardingModelProvisioner(
            modelsServices,
            factory.CreateLogger<Winpepper.App.Services.OnboardingModelProvisioner>(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            engineLoadProbe: (name, ct) => Task.Run(() =>
            {
                // One-shot engine load probe: spawn a worker for the selected
                // layout, force the Load RPC (a tiny batch drives spawn+Load),
                // dispose. Failure -> the provisioner publishes the sticky
                // redist/repair error instead of a false "ready".
                try
                {
                    var layout = Winpepper.Asr.TranscribeCpp.StreamingModelLayout.For(name);
                    var exe = Environment.ProcessPath
                        ?? throw new InvalidOperationException("no process path for the probe worker");
                    using var probe = new Winpepper.Asr.TranscribeCpp.Worker.WorkerProcessEngine(
                        new Winpepper.Asr.TranscribeCpp.Worker.ExeWorkerProcessFactory(
                            () => new System.Diagnostics.ProcessStartInfo(exe, "--transcribe-worker")),
                        layout.RuntimeDir(modelsServices.ModelsRoot),
                        layout.GgufPath(modelsServices.ModelsRoot),
                        layout.Name);
                    probe.TranscribeBatch(new float[1600], layout.Language, out _); // 0.1 s of silence
                    return true;
                }
                catch { return false; }
            }, ct));
```

expose `public Winpepper.Core.ViewModels.IOnboardingModelProvisioner OnboardingProvisioner { get; }` via the ctor like the other services. (This same instance serves Task 13's boot reconciliation for interrupted optional downloads.)

- [ ] **Step 2: Replace the Step-3 XAML**

In `src/Winpepper.App/Views/OnboardingPage.xaml`:
- Widen the content column: `<StackPanel MaxWidth="560" ...>` → `MaxWidth="720"` (the two-up radio grid needs it; prototype window is 720px).
- Replace the ENTIRE `DownloadPanel` StackPanel (lines 87-116, the card with the `E896` glyph) with:

```xml
                <StackPanel x:Name="DownloadPanel" Visibility="Collapsed" Spacing="8">
                    <TextBlock Text="Step 3 of 4"
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                               HorizontalAlignment="Center" />
                    <TextBlock Text="Choose your models"
                               Style="{ThemeResource SubtitleTextBlockStyle}"
                               HorizontalAlignment="Center" />
                    <TextBlock Text="Winpepper downloads speech models once, then works fully offline. You can change any of this later in Settings › Models."
                               Style="{ThemeResource BodyTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                               TextWrapping="Wrap" TextAlignment="Center" />

                    <TextBlock Margin="0,8,0,0" TextWrapping="Wrap">
                        <Run Text="Speech model" FontWeight="SemiBold" />
                        <Run Text=" — transcribes while you speak. Pick one." Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </TextBlock>
                    <Grid ColumnSpacing="12">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="*" />
                        </Grid.ColumnDefinitions>
                        <Border Grid.Column="0" x:Name="EnglishCard"
                                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                                BorderThickness="1" CornerRadius="8" Padding="14,12">
                            <RadioButton x:Name="EnglishRadio" GroupName="SpeechModel" IsChecked="True"
                                         AutomationProperties.AutomationId="OnboardingSpeechEnglishRadio">
                                <StackPanel Spacing="3">
                                    <StackPanel Orientation="Horizontal" Spacing="10">
                                        <TextBlock Text="English" FontWeight="SemiBold" />
                                        <Border Background="{ThemeResource AccentFillColorSelectedTextBackgroundBrush}"
                                                CornerRadius="10" Padding="9,2">
                                            <TextBlock Text="Recommended" Style="{ThemeResource CaptionTextBlockStyle}"
                                                       Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" />
                                        </Border>
                                    </StackPanel>
                                    <TextBlock Text="Best English accuracy."
                                               Style="{ThemeResource CaptionTextBlockStyle}"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                               TextWrapping="Wrap" />
                                    <TextBlock x:Name="EnglishSizeText" FontWeight="SemiBold" Margin="0,6,0,0"
                                               Style="{ThemeResource CaptionTextBlockStyle}" />
                                </StackPanel>
                            </RadioButton>
                        </Border>
                        <Border Grid.Column="1" x:Name="MultilingualCard"
                                Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                                BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                                BorderThickness="1" CornerRadius="8" Padding="14,12">
                            <RadioButton x:Name="MultilingualRadio" GroupName="SpeechModel"
                                         AutomationProperties.AutomationId="OnboardingSpeechMultilingualRadio">
                                <StackPanel Spacing="3">
                                    <TextBlock Text="Multilingual" FontWeight="SemiBold" />
                                    <TextBlock Text="40 languages, auto-detected. Slightly lower English accuracy."
                                               Style="{ThemeResource CaptionTextBlockStyle}"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                               TextWrapping="Wrap" />
                                    <TextBlock x:Name="MultilingualSizeText" FontWeight="SemiBold" Margin="0,6,0,0"
                                               Style="{ThemeResource CaptionTextBlockStyle}" />
                                </StackPanel>
                            </RadioButton>
                        </Border>
                    </Grid>

                    <TextBlock Text="Options" FontWeight="SemiBold" Margin="0,10,0,0" />
                    <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                            BorderThickness="1" CornerRadius="8" Padding="14,12">
                        <CheckBox x:Name="BackupCheck" AutomationProperties.AutomationId="OnboardingBackupModelCheckBox">
                            <Grid ColumnSpacing="10">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <StackPanel Grid.Column="0" Spacing="3">
                                    <TextBlock Text="Backup speech model" FontWeight="SemiBold" />
                                    <TextBlock Text="Slower — transcribes after you stop speaking. Steps in automatically if the main speech engine ever has trouble."
                                               Style="{ThemeResource CaptionTextBlockStyle}"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                               TextWrapping="Wrap" />
                                </StackPanel>
                                <TextBlock Grid.Column="1" x:Name="BackupSizeText"
                                           Style="{ThemeResource CaptionTextBlockStyle}"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                            </Grid>
                        </CheckBox>
                    </Border>
                    <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                            BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                            BorderThickness="1" CornerRadius="8" Padding="14,12">
                        <CheckBox x:Name="CleanupCheck" AutomationProperties.AutomationId="OnboardingCleanupModelCheckBox">
                            <Grid ColumnSpacing="10">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="Auto" />
                                </Grid.ColumnDefinitions>
                                <StackPanel Grid.Column="0" Spacing="3">
                                    <TextBlock Text="Text cleanup" FontWeight="SemiBold" />
                                    <TextBlock Text="Tidies what you dictate — punctuation, capitalization, and filler words — before it's typed into your apps."
                                               Style="{ThemeResource CaptionTextBlockStyle}"
                                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                               TextWrapping="Wrap" />
                                </StackPanel>
                                <TextBlock Grid.Column="1" x:Name="CleanupSizeText"
                                           Style="{ThemeResource CaptionTextBlockStyle}"
                                           Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                            </Grid>
                        </CheckBox>
                    </Border>

                    <TextBlock Text="Downloads happen in the background — you can keep setting up while they run."
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    <TextBlock x:Name="TotalDownloadText"
                               AutomationProperties.AutomationId="OnboardingTotalDownloadText" />
                </StackPanel>
```
- Move the download status/progress/error trio INTO `TestPanel` (they now render during Test dictation), inserting before the `TestBox`:

```xml
                            <TextBlock x:Name="DownloadStatusText"
                                       AutomationProperties.AutomationId="OnboardingDownloadStatusText"
                                       Style="{ThemeResource CaptionTextBlockStyle}"
                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                            <ProgressBar x:Name="DownloadProgress" AutomationProperties.AutomationId="OnboardingDownloadProgressBar" Minimum="0" Maximum="100" Visibility="Collapsed" />
                            <TextBlock x:Name="DownloadErrorText"
                                       AutomationProperties.AutomationId="OnboardingDownloadErrorText"
                                       Foreground="{ThemeResource SystemFillColorCriticalBrush}"
                                       TextWrapping="Wrap"
                                       Visibility="Collapsed" />
                            <Button x:Name="RetryDownloadButton"
                                    AutomationProperties.AutomationId="OnboardingRetryDownloadButton"
                                    Content="Retry download" Visibility="Collapsed" Click="OnRetryDownload" />
```

- [ ] **Step 3: Rewire the code-behind**

In `OnboardingPage.xaml.cs`:
- VM construction (OnNavigatedTo):
```csharp
        var registry = shell.ModelsServices.Registry;
        var catalog = new ModelPickerCatalog(
            Winpepper.Models.ModelRegistry.StreamingAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.StreamingAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.MultilingualStreamingAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.MultilingualStreamingAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.DefaultAsrName,
            registry.Find(Winpepper.Models.ModelRegistry.DefaultAsrName)!.TotalSizeBytes,
            Winpepper.Models.ModelRegistry.DefaultCleanupName,
            registry.Find(Winpepper.Models.ModelRegistry.DefaultCleanupName)!.TotalSizeBytes);
        _vm = new OnboardingViewModel(
            shell.SettingsWriter,
            shell.Pipeline.TryStart,
            new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator(),
            shell.OnboardingProvisioner,
            catalog);
```
- Resume scope (replace the `MissingModelsResolver` call at lines 45-49):
```csharp
        var scope = new List<string> { settings.StreamingModelName };
        if (settings.OnboardingBackupModelChosen) scope.Add(settings.AsrModelName);
        if (settings.OnboardingCleanupModelChosen) scope.Add(settings.CleanupModelName);
        var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
            shell.ModelsServices.Registry.All, shell.ModelsServices.ModelsRoot, scope);
        var modelsResolved = missing.Count == 0;
```
- Picker wiring (after the hotkey wiring):
```csharp
        EnglishRadio.Checked      += (_, _) => { _vm.MultilingualSelected = false; };
        MultilingualRadio.Checked += (_, _) => { _vm.MultilingualSelected = true; };
        BackupCheck.Checked   += (_, _) => { _vm.BackupModelSelected = true; };
        BackupCheck.Unchecked += (_, _) => { _vm.BackupModelSelected = false; };
        CleanupCheck.Checked   += (_, _) => { _vm.CleanupModelSelected = true; };
        CleanupCheck.Unchecked += (_, _) => { _vm.CleanupModelSelected = false; };
        EnglishSizeText.Text = OnboardingViewModel.SizeLabel(catalog.EnglishBytes);
        MultilingualSizeText.Text = OnboardingViewModel.SizeLabel(catalog.MultilingualBytes);
        BackupSizeText.Text = OnboardingViewModel.SizeLabel(catalog.BackupBytes);
        CleanupSizeText.Text = OnboardingViewModel.SizeLabel(catalog.CleanupBytes);
        // Hydrate picker controls from the VM (InitializeFrom ran above):
        MultilingualRadio.IsChecked = _vm.MultilingualSelected;
        EnglishRadio.IsChecked = !_vm.MultilingualSelected;
        BackupCheck.IsChecked = _vm.BackupModelSelected;
        CleanupCheck.IsChecked = _vm.CleanupModelSelected;
```
- In `OnAdvance`, BEFORE `await _vm.AdvanceAsync(...)`, publish the picked streaming model into the live slot so the engine holder and the primary-ready gate see it immediately (the settings write inside `AdvanceAsync` is durability only — the slot is the cross-thread transport, exactly like ASR promote):
```csharp
        if (_vm.Step == OnboardingStep.DownloadModels && _shell is not null)
        {
            _shell.StreamingModelSelection.Publish(_vm.SelectedSpeechModelName);
        }
```
- `RefreshButtons()`: button labels become
```csharp
        AdvanceButton.Content = _vm.Step switch
        {
            OnboardingStep.TestDictation => "Finish",
            OnboardingStep.DownloadModels => "Download & continue",
            _ => "Next",
        };
```
  total text: `TotalDownloadText.Text = _vm.TotalDownloadText;`; progress visibility keys on `TestDictation` now:
```csharp
        DownloadProgress.Value = _vm.DownloadProgressPercent;
        DownloadProgress.IsIndeterminate = false;
        DownloadProgress.Visibility = _vm.Step == OnboardingStep.TestDictation && !_vm.SpeechModelVerified
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadStatusText.Text = _vm.DownloadStatus;
        RetryDownloadButton.Visibility = _vm.CanRetry ? Visibility.Visible : Visibility.Collapsed;
```
  (error text lines unchanged). Remove the old `if (_vm.Step == OnboardingStep.DownloadModels) DownloadProgress.Visibility = ...` block from `OnAdvance`.
- Add the retry handler:
```csharp
    private void OnRetryDownload(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => _vm?.RetryDownloads();
```
- IMPORTANT (`OnNavigatedFrom`): the page disposes the VM, but the provisioner lives on `AppShell` — background downloads keep running and re-attach on re-navigation. No change needed beyond what exists; verify no new subscription leaks (VM unsubscribes in `Dispose`).

- [ ] **Step 4: Full Linux suite (shared bricks), then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add src/Winpepper.App
git commit -m "feat(app): onboarding Step 3 model picker with background downloads (per approved prototype)"
```

---

### Task 19: Delete the dead Parakeet chunked-streaming code

**Files:**
- Delete: `src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs` (35 lines)
- Delete: `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` (287 lines)
- Delete: `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (422 lines)
- Modify: `src/Winpepper.Asr/InteriorSilenceSkipper.cs` (line 9 comment references `ParakeetStreamingSession.LeadingSilenceRmsFloor`)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (only if any comment referencing `ParakeetStreamingTranscriber` survived Task 13's rewrite of `BuildStreamingTranscriber` — grep and fix)
- Conditionally delete (Step 2 decides): `src/Winpepper.Asr/StreamingLogMelExtractor.cs`, `src/Winpepper.Asr/RunningMelNormalizer.cs`, `tests/Winpepper.Asr.Tests/StreamingMelTests.cs`
- Conditionally modify (Step 2 decides): `tests/Winpepper.Asr.Tests/InteriorSilenceSkipperTests.cs` — delete its single mel-using test method (it constructs both mel classes as a batch-equivalence oracle)
- KEEP: `tests/Winpepper.Asr.Tests/FakeParakeetBackend.cs` (`TdtGreedyDecoderTests` needs it — 11 references)

**Interfaces:**
- Consumes/Produces: nothing — pure deletion. Prior evidence: zero production construction sites (the only `src/` references are the two comment mentions above); disable rationale documented at `ParakeetStreamingSession.cs:19-33` (model-level blank-collapse on chunked int8 TDT).

- [ ] **Step 1: Delete the three files**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
git rm src/Winpepper.Asr/Transcription/ParakeetStreamingTranscriber.cs \
       src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs \
       tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs
```

- [ ] **Step 2: Check the mel-helper orphans**

```bash
grep -rn "StreamingLogMelExtractor\|RunningMelNormalizer" src/ tests/ --include=*.cs
```
Expected after Step 1 (verified 2026-08-08): hits only in (a) `src/Winpepper.Asr/StreamingLogMelExtractor.cs` and `src/Winpepper.Asr/RunningMelNormalizer.cs` themselves, (b) `tests/Winpepper.Asr.Tests/StreamingMelTests.cs` (every test in that file exercises the mel pair — delete whole), and (c) `tests/Winpepper.Asr.Tests/InteriorSilenceSkipperTests.cs:~235,~250` — both inside the single test method `GatedStreamingMel_EqualsBatchMel_OverTheKeptConcatenation` (≈ lines 226–260), which uses the mel pair as an oracle to prove skip-before-mel equivalence for the chunked pipeline deleted in Step 1. If the expectation holds, the pair is production-dead (their only production consumer was the deleted session):
```bash
git rm src/Winpepper.Asr/StreamingLogMelExtractor.cs src/Winpepper.Asr/RunningMelNormalizer.cs tests/Winpepper.Asr.Tests/StreamingMelTests.cs
```
Then edit `tests/Winpepper.Asr.Tests/InteriorSilenceSkipperTests.cs` and delete the entire `GatedStreamingMel_EqualsBatchMel_OverTheKeptConcatenation` test method (it cannot compile once the mel classes are gone; the file's other tests and all shared helpers stand alone without it — verified 2026-08-08). Do NOT remove any shared helper the method calls (`Concat`/`Speech`/`Silence` etc.) — each has other callers. Re-run the Step 2 grep afterwards; expected: zero hits.
If ANY consumer other than (a)–(c) shows up in `src/` or `tests/`, keep all three files AND the test method, and note the consumer in the commit message instead.

- [ ] **Step 3: Fix the two comment references**

- `src/Winpepper.Asr/InteriorSilenceSkipper.cs:9`: reword the comment to state the RMS floor's value/provenance inline without naming the deleted class (e.g. "matches the leading-silence RMS floor the retired chunked-TDT streaming session used (2026-07-25 evidence)").
- `grep -rn "ParakeetStreaming" src/` — fix any remaining comment (AppShell's was rewritten in Task 13; confirm none remain).

- [ ] **Step 4: Build + run the Asr tests, then the full Linux suite**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows"
cd "$R" && ./scripts/linux-tests.sh
```
Expected: green; total test count drops by the deleted tests' count (the deleted files' facts plus the one `InteriorSilenceSkipperTests` method removed in Step 2, if that branch was taken).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore(asr): delete dead chunked-TDT Parakeet streaming code and its tests"
```

---

### Task 20: Autostart — registry becomes the single source of truth

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (delete `AutostartEnabled`)
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs` (delete the shadow-setting write)
- Test: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs` (update)

**Interfaces:**
- Consumes: the audit facts — `AppSettings.AutostartEnabled` (default `false`) has exactly ONE production reference: the WRITE at `RecordingPage.xaml.cs:89`. No production reader exists; the toggle DISPLAYS `_shell.Autostart.IsEnabled()` (the HKCU Run key) at `RecordingPage.xaml.cs:70`; the MSI seeds the key ON for fresh installs (`packaging/winpepper.wxs:166-176`, upgrade-preserving via `WINPEPPER_RUNKEY_PREEXISTS`). The persisted setting therefore disagrees with reality from minute zero and can only drift.
- Produces: no `AutostartEnabled` anywhere; the registry Run key is the single source of truth (read lazily by the toggle, written by Enable/Disable, seeded by the MSI). Old settings.json files containing `"autostartEnabled"` load fine (`System.Text.Json` ignores unknown members) — add a test proving it.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`:
- DELETE the three `AutostartEnabled` references (`:67` defaults assert, `:79` round-trip write, `:86` round-trip assert).
- ADD:
```csharp
    [Fact]
    public void Load_LegacySettingsJson_WithAutostartEnabled_IsIgnoredNotFatal()
    {
        var dir = Directory.CreateTempSubdirectory("settings-autostart");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """{ "schema": 1, "autostartEnabled": true }""");
            var store = new SettingsStore(path);
            var s = store.Load(); // must not throw; field simply ignored
            s.OnboardingCompleted.ShouldBeFalse();
        }
        finally { dir.Delete(recursive: true); }
    }
```

- [ ] **Step 2: Run to verify failure** — the deleted asserts leave `CS0117 'AppSettings' does not contain a definition for 'AutostartEnabled'` only AFTER Step 3; at this point the new test passes trivially, so the RED state is the compile error after removing the property. Proceed to Step 3 and treat "old tests referencing the property fail to compile" as the verification that all references are found.

- [ ] **Step 3: Implement**

- `AppSettings.cs`: delete `public bool AutostartEnabled { get; init; } = false;` (line 77) and its comment.
- `RecordingPage.xaml.cs` (lines 86-90): delete the shadow write:
```csharp
            // DELETE these three lines:
            var isOn = AutostartToggle.IsOn;
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = isOn });
```
  and its "Capture on the UI thread" comment; add above the toggle wiring:
```csharp
        // Autostart state lives in HKCU\...\Run ONLY (the MSI seeds it; the
        // toggle reads/writes it). There is deliberately no settings.json
        // mirror: a write-only shadow drifts (fresh install: key ON, old
        // setting false) and nothing ever read it.
```
- `grep -rn "AutostartEnabled" src/ tests/ docs/plans/2026-08-08-nemotron-first-asr.md` — src/tests must be clean (docs/plans hits are fine).

- [ ] **Step 4: Run `SettingsStoreTests`, expect PASS; full Linux suite.**

- [ ] **Step 5: Commit**

```bash
git add -A src/Winpepper.Core/Settings src/Winpepper.App/Views/RecordingPage.xaml.cs tests/Winpepper.Core.Tests
git commit -m "fix(settings): drop the write-only AutostartEnabled shadow — HKCU Run key is the single source of truth"
```

---

### Task 21: Copy/docs sweep — honest sizes everywhere

**Files:**
- Modify: `README.md` (lines 53, 67, 80, 111 — the download/disk claims; plus the license line and any redist-fallback claim)
- Modify: `THIRD-PARTY-NOTICES.md` (OpenMDW-1.1 entry for the multilingual model)
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` (line 209 "about 720 MB")
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (comment blocks near the auto-install: "~1.1 GB v3" / "~730 MB", lines ~490-494 pre-edit)
- Verify only: `grep -rn "670 MB\|1.2 GB\|~730\|~1.1 GB" src/ README.md`

**Interfaces:** none — copy only. Size convention (locked): decimal MB from registry `TotalSizeBytes` (incl. runtime tarballs), rounded to nearest 10 with `~` for approximations. English Nemotron ≈ 760 MB, Multilingual ≈ 780 MB, Parakeet ≈ 670 MB, Qwen cleanup ≈ 490 MB.

- [ ] **Step 1: Apply the copy edits**

- `README.md:53` — `about 1.2 GB, once` → `about 760 MB once (more if you add the optional backup or cleanup models)`
- `README.md:67` — keep the `(~720 MB, English only, NVIDIA Open Model License)` line's structure but correct to `(~760 MB, English by default — a multilingual variant is available; NVIDIA Open Model License for the English model, OpenMDW-1.1 for the multilingual one)`. The multilingual variant is NOT under the NVIDIA Open Model License — its governing terms are OpenMDW-1.1 (V4/A20; HF model card + GGUF README, verified 2026-08-08).
- `README.md:80` — `(~1 GB of memory)` stays (runtime RAM, still accurate). While editing this region (README:79-80): if the surrounding text claims that machines WITHOUT the Microsoft Visual C++ x64 Redistributable gracefully fall back to Parakeet, correct it — ONNX Runtime needs the SAME redistributable (V8), so local dictation requires the Microsoft Visual C++ x64 Redistributable, full stop.
- `THIRD-PARTY-NOTICES.md` — ADD an OpenMDW-1.1 entry for the multilingual model (`nemotron-3.5-asr-streaming-0.6b` weights), mirroring the existing "Nemotron Speech Streaming model weights" NVIDIA-OML entry's pattern: models are NOT redistributed with the app, users download directly from Hugging Face, notice included preemptively. Do not invent license text obligations — follow the existing entry's structure.
- Follow-up note (out of scope for this plan, record for the release backlog): the MSI should chain the Microsoft Visual C++ x64 Redistributable (`vc_redist.x64.exe`) in a future release — BOTH local engines (transcribe.cpp and ONNX Runtime) require it, and the onboarding load probe (Task 18) only reports the problem, it cannot fix it.
- `README.md:111` — `About 2 GB of free disk space (roughly 700 MB for the app, 1.2 GB for the speech models)` → `About 1.5 GB of free disk space for a default install (roughly 700 MB for the app, 760 MB for the speech model); up to ~3 GB with the optional backup speech and text-cleanup models`
- `ModelsPage.xaml:209` — `about 720 MB` → `about 760 MB` (and if the sentence names "the streaming model", generalize to "each speech model (about 760–780 MB)").
- `AppShell.cs` auto-install comments: replace "~1.1 GB v3" with "the onboarding model download" and "~730 MB" with "~760 MB" (the deferral comment's rationale text should now say onboarding owns the first-run install and the auto-installer covers upgrades).
- Onboarding copy was replaced wholesale in Task 18 (the sole `~670 MB` string lived there); confirm:
```bash
grep -rn "~670 MB" src/ && echo "FAIL: stale copy" || echo "clean"
grep -rn "1.2 GB" README.md && echo "FAIL: stale copy" || echo "clean"
```
Expected: `clean` twice.

- [ ] **Step 2: Full Linux suite (copy edits can't break it, but the rule is every commit), then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
git add README.md THIRD-PARTY-NOTICES.md src/Winpepper.App
git commit -m "docs: correct download-size and license claims for nemotron-first defaults"
```

---

### Task 22: Final verification — full Linux suite + Windows gate

**Files:** none (verification only; fix-forward commits allowed if the gate finds issues).

- [ ] **Step 1: Full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 2: Windows gate (from WSL, background, 20–40 min budget)**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
nohup ./scripts/windows-gate.sh > /tmp/windows-gate-nemotron.log 2>&1 &
# Poll until done:
tail -f /tmp/windows-gate-nemotron.log
```
Expected: exit 0, final line `GATE: GREEN` (non-zero `Skipped` counts are normal — model-presence skips). Never run `linux-tests.sh` concurrently. On RED: read `artifacts/windows-gate/*.log`, fix, commit (`fix(gate): ...` with a green Linux run first), re-run the gate.

- [ ] **Step 3: Sanity checklist against the branch (grep-level, no new code)**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
# Parakeet is optional: no unconditional ParakeetSession construction outside the loader lambda + rerun service:
grep -rn "new ParakeetSession" src/
# Expected: AppShell.cs (inside the loadBatchAsr lambda) and LocalTranscriptionRerunService.cs only.
# The streaming name is never a hardcoded literal outside layout/registry/tests:
grep -rn '"nemotron-streaming-en"' src/ | grep -v StreamingModelLayout | grep -v ModelRegistry
# Old post-onboarding silent install still deferred for new installs, selected-name aware for upgrades:
grep -n "OnboardingCompleted" src/Winpepper.App/Hosting/AppShell.cs
grep -n "StartAsync(settings.StreamingEnabled, settings.StreamingModelName" src/Winpepper.App/Hosting/AppShell.cs
```

Non-blocking recommendation (A6 residual, not a gate condition): before SHIPPING the multilingual picker option, run one Windows eval of the multilingual gguf with `null` language (autodetect) through the existing eval harness (see `docs/plans/2026-07-27-asr-model-comparison-evidence.md`) to confirm autodetect-mode latency matches the measured en-US parity (V9 predicted ≈parity from publisher benchmarks; one measured run closes it).

- [ ] **Step 4: Record the result** — note the gate's summary (project/TFM pass counts, skips) in the final commit message if any fix-forward commits were needed; otherwise no commit (the branch is ready for review/PR — pushing is the workflow's decision, and the gate being GREEN satisfies the pre-push rule).

---

## Verification Matrix (spec item → proof)

| Spec item | Covering tasks | Production outcome proven by |
|---|---|---|
| 1. Subprocess isolation (kill/respawn/retry; observability preserved; guard semantics intact) | 2–7 | `WorkerProcessEngineTests` (timeout→kill→respawn, budget, dispose-latch, oversize pre-check), `TranscribeWorkerLoopTests` (gate-safe batch, EOF frees gate), end-to-end wedge test (Task 5/8) proving a wedged feed no longer hangs and the SAME dictation still yields text; `NemotronStreamingTranscriber`/`StreamingRouteGuard` and their suites untouched (wedge logs preserved by construction; the `asr_mode` classifier is NOT preserved-by-construction — Task 13 explicitly rewrites it to streaming-name-set membership so both streaming models stamp `asr_mode=streaming`); validation hardening: write-side oversize frame guard (Task 2), dispose latch + length-aware batch deadline (Task 5), Windows job object (Task 6), MTA/WER-suppressed worker verb (Task 7); worker lifecycle log lines specified in Task 5 |
| 2. Nemotron batch adapter in production (streaming-off path, post-restart fallback, replaces ParakeetTranscriber-required paths) | 8, 9, 13 | `NemotronBatchTranscriberTests`; `LocalStreamingTranscriberFactoryTests.StreamingDisabled_UsesNemotronBatch...`; ladder tests; end-to-end wedge test |
| 3. Pipeline decoupled from Parakeet (no-Parakeet dictation, streaming starts w/o session, AssemblyAI local fallback = Nemotron batch, History rerun graceful, ResolveOrDefault:209 lifted) | 9, 10, 12, 13, 15 | Ladder tests (no-Parakeet paths); registry `ResolveOrDefault_StreamingAsr_DefaultsToEnglish`; gate-delegate tests; `RerunModelRouterTests`; PipelineHost gate edits verified by windows-gate build + Task 22 grep checklist |
| 4. Registry entries + roles (multilingual entry, ONE Nemotron primary, Parakeet optional backup, cleanup optional, Q8_0 only) | 10, 11 | `Registry_contains_the_multilingual_nemotron_streaming_model` (real HF-API-sourced hash/size, re-verified in-task), catalog tests, settings defaults/round-trip tests |
| 5. Onboarding model picker (prototype copy/behavior, live total, background downloads, verified Test-dictation gate, resumable machinery kept, silent auto-install removed for new installs) | 16, 17, 18 | `OnboardingModelPickerTests` (defaults, totals table, advance→background+TestDictation, verified gate, retry, resume); `DownloadBatchPlannerTests`; XAML copy verbatim from prototype (Task 18); coordinator/downloader reuse via `ModelsServices.DownloadAsync` + shared op gate; new-install deferral branch in AppShell unchanged (onboarding now owns first-run install); `SpeechModelReady` = files verified AND one-shot engine load probe (Task 18) — closes the redist/ABI/spawn "ready but first dictation fails" hole; Task 13's boot reconciliation resumes picker-chosen optional downloads interrupted by app exit |
| 6. Existing installs unchanged (Parakeet keeps batch-fallback role; AsrModelName repair intact; settings migrate) | 9, 11, 13 | `Load_LegacySettingsJson_WithoutStreamingModelName_DefaultsToEnglish`; ladder test `Ladder_NemotronUnavailable_ParakeetStepsIn`; boot-repair block untouched (Task 13 only ADDS a streaming repair beside it); auto-installer selected-name fallback tests |
| 7. Dead code deleted | 19 | `git rm` + grep verification + green suite |
| 8. Autostart reconciliation | 20 | Setting removed (nothing left to drift); legacy-json ignore test; registry read/write path unchanged and MSI-compatible |
| 9. Docs/copy | 18, 21 | grep gates (`~670 MB`, `1.2 GB` absent); README/ModelsPage edits; multilingual license corrected to OpenMDW-1.1 (README + new THIRD-PARTY-NOTICES.md entry); redist requirement stated honestly (no Parakeet-without-redist claim) |
| 10. Tests (worker supervision, batch adapter, picker VM incl. radio invariant, upgrade-path resolution) | throughout | Tasks 2–6 (supervision), 8 (adapter), 17 (picker: the radio guarantees exactly-one-speech-model — `SelectedSpeechModelName` is total, no empty state exists), 10+11 (upgrade resolution) |

## Known deliberate deviations from the prototype (approved-by-spec resolutions)

1. **Sizes are real, not the mock's 722/670/469** — the spec forbids invented sizes; labels derive from registry bytes (`SizeLabel`), totals from the same numbers. The prototype's own spec (§9.5) demands verifying sizes before hard-coding.
2. **Background downloads** — footnote behavior implemented (advance immediately; progress + verification gate live on the Test-dictation step). The prototype JS's blocking simulation is explicitly "Not production code".
3. **GB formatting uses ÷1000** — the prototype's 1000-threshold/1024-divisor asymmetry is fixed (its §9.4 flags this as a conscious decision point).
4. **A "Retry download" button exists on the Test-dictation step** — the prototype defines no error states (§9.6); the existing onboarding retry affordance is preserved in the new location.
5. **Progress-line copy** fixes the prototype's doubled-"model" bug via explicit friendly names (its §2 copy-quirk note recommends exactly this).
6. **Speech readiness includes a one-shot engine load probe** — the prototype's Step 3 promises only downloads; `SpeechModelReady` additionally requires a worker load probe (spawn→Load→dispose) plus the "Checking the speech engine…" status line, so "ready" can never precede a loadable engine (a missing VC++ x64 Redistributable, ABI mismatch, or spawn failure surfaces in onboarding with actionable copy instead of at the first dictation — V6/A16).

### Residual label key (added 2026-08-09)

The validation ledger for this branch (workflow logs, not committed to the
repo) tracks assumptions as `A<n>` rows and validators as `V<n>`. The two
labels that have been confused:

- **A15 (evidence: validator V8)** — the accepted VC++-redistributable
  DEPLOYMENT residual: the MSI does not chain `vc_redist.x64.exe`, so a
  machine without the Microsoft Visual C++ x64 Redistributable hard-fails
  local dictation (both transcribe.cpp and ONNX Runtime import the same
  CRT). Recorded follow-up: chain the redist in a future MSI (see the
  release-backlog note above).
- **V6/A16** — the falsified-and-FIXED onboarding readiness assumption:
  file verification alone was a lying readiness proxy; `SpeechModelReady`
  additionally requires the one-shot engine load probe. The probe SURFACES
  a missing redist; the A15 residual is that nothing INSTALLS it.

An earlier recap filed the VC++-redist residual under the wrong id; read any
`V6/A16` citation in this file as the probe fix, and the accepted redist
residual as A15/V8. The 2026-08-09 evidence doc restates this key in-repo.
