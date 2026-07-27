# Multi-Model ASR Eval (Resources + Convergence + Comparison) Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Extend the ASR eval bench (`scripts/asr-latency-bench`, corpus mode) so three speech models — production `nemotron-streaming-en` (streaming), `nemotron-3.5-asr-streaming-0.6b` (streaming, requires `--language en-US`), and `Qwen3-ASR-1.7B` (batch-only) — can be profiled serially with per-run resource utilization, statistical convergence over repeated corpus passes, per-model result directories, and a comparison aggregator.

**Architecture:** All new decision logic (convergence math, per-pass aggregation, resource math, model-dir resolution, comparison aggregation, arg validation) goes into BCL-only files under `scripts/asr-latency-bench/` that are `Compile Include`'d into `tests/Winpepper.Asr.Tests` — that is the established pattern that makes bench logic testable by the Linux gate. `Program.cs` (untestable by design) only wires tested pieces together: the corpus case gains a pass loop, a batch-only branch, and resource capture. The single production change is minimal: the transcribe.cpp binding gains the v0.1.3 `transcribe_run_params` struct so a `language` hint can be passed to `transcribe_run` / `transcribe_stream_begin` (today both receive `IntPtr.Zero`; passing `null` language keeps that byte-identical behavior, so the production model path is untouched). A new driver script runs one model per process (required anyway: transcribe.cpp pins one runtime dir per process) and writes to `artifacts/asr-eval/<model-name>/`.

**Tech Stack:** .NET 9, xUnit v3 + Shouldly, System.Text.Json, System.Diagnostics.Process, bash driver over `powershell.exe` WSL→Windows interop, transcribe.cpp 0.1.3 native runtime.

## Global Constraints

Copied from the spec and the repo's binding conventions — every task's requirements implicitly include ALL of these:

- **Tests green before EVERY commit.** Run `./scripts/linux-tests.sh` from the worktree root; green = exit 0 and it prints `LINUX SUITE: GREEN`. First export the SDK env once per shell:
  ```bash
  export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
  export PATH="$DOTNET_ROOT:$PATH"
  ```
- **Never `dotnet test`** — build test projects with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, run with `dotnet exec <built dll> -notrait "Platform=Windows"` (xUnit v3 in-process runner).
- **Do NOT push.** The Windows gate (`./scripts/windows-gate.sh`) is a pre-push requirement only and is out of scope here.
- **Never mix Linux- and Windows-side builds in the same `bin/`/`obj/`** (CS0006 corruption). After building the bench on Linux for a compile check, `rm -rf scripts/asr-latency-bench/bin scripts/asr-latency-bench/obj` before any Windows driver run (the driver also pre-cleans). Never run a Windows driver script and `linux-tests.sh` concurrently.
- **`scripts/asr-latency-bench` stays OUT of `winpepper.sln`.** Do not add it.
- **Privacy (hard rule):** corpus WAVs/reference transcripts (`C:\Users\dan\winpepper-evals\corpus-v1`, 65 clips) and model files never land in git. `results.json` (contains transcript text) only ever goes under gitignored `artifacts/`. `results.md`, `comparison.json`, and any evidence doc contain numbers + clip ids only — no transcript/reference text. Unit tests use synthetic text only.
- **`%LOCALAPPDATA%\winpepper` is READ-ONLY** (`/mnt/c/Users/dan/AppData/Local/winpepper`). Copy FROM it (e.g. runtime DLLs to the eval models dir); never write INTO it.
- **BCL-only linked-file rule:** any file under `scripts/` that is `Compile Include`'d into `tests/Winpepper.Asr.Tests` must use only `System.*` types (no project/package references).
- **Production code changes minimal:** only the language pass-through in `src/Winpepper.Asr` (Tasks 1–2), covered by the existing test suite plus new tests.
- **Plain naming:** "model", "streaming" vs "batch", "passes", "converged". No invented jargon.
- **Docs:** README.md untouched. The only markdown added is this plan and the evidence doc, both under `docs/plans/` (working/agent docs — allowed).
- **Commits:** conventional style (`feat(...)`, `fix(...)`, `docs(...)`, `test(...)`), focused and atomic.
- **Leave untracked `.kata.toml` and `.opencode/` alone** — never add, remove, or commit them.
- Candidate model GGUFs are at `/mnt/c/Users/dan/winpepper-evals/models/` (Windows: `C:\Users\dan\winpepper-evals\models\`). `Qwen3-ASR-1.7B-Q8_0.gguf` is complete when its size is exactly `2185030624` bytes — verify before use, skip gracefully with a clear message if not.

## Verified facts the tasks rely on (do not re-derive)

- `transcribe.h` v0.1.3 (the runtime's contract, header_hash `86b16dd97ad1cb58`) declares, verbatim (fetched from `https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.1.3/include/transcribe.h`):
  ```c
  enum { ... TRANSCRIBE_ABI_SESSION_PARAMS = 1, TRANSCRIBE_ABI_RUN_PARAMS = 2, ... };

  struct transcribe_run_params {
      uint64_t struct_size;
      transcribe_task               task;         /* C enum = int32 */
      transcribe_timestamp_kind     timestamps;   /* C enum = int32 */
      enum transcribe_pnc_mode      pnc;          /* C enum = int32 */
      enum transcribe_itn_mode      itn;          /* C enum = int32 */
      const char *                  language;     /* BCP-47-ish code, NULL = autodetect/model default */
      const char *                  target_language;
      bool                          keep_special_tags;  /* default false = tags stripped */
      const struct transcribe_ext * family;
      int32_t                       spec_k_drafts;
  };
  TRANSCRIBE_API void transcribe_run_params_init(struct transcribe_run_params * params);
  ```
  x64 layout: offsets 0/8/12/16/20/24/32/40/48/56, total size **64** bytes (8-byte alignment; 7 bytes padding after the 1-byte bool, 4 tail padding).
  The header guarantees **string-pointer lifetime is caller-owned and copied before the API call returns — including `transcribe_stream_begin`** ("the dispatcher copies these strings into session-owned storage at begin"), so language buffers may be freed immediately after `transcribe_run`/`transcribe_stream_begin` return.
  `transcribe_session_params` has NO language field (only `struct_size, n_threads, kv_type, n_ctx`) — language goes through **run params** for both batch and streaming; `transcribe_session_init` stays `IntPtr.Zero`.
  `keep_special_tags` default false already strips language tags — nothing to do for the "tags stripped by default" requirement.
- The binding today passes `IntPtr.Zero` for run params at `transcribe_run` (`TranscribeCppEngine.cs:245`) and `transcribe_stream_begin` (`TranscribeCppEngine.cs:213`). Existing ABI ids bound: `0=model_load_params, 3=stream_params, 4=capabilities, 9=stream_update, 10=stream_text` (`TranscribeCppNative.cs`).
- `TranscribeCppEngine.Load(string runtimeDir, string modelPath, Action<string>? logWarning = null)`; a second `Load` with a **different** `runtimeDir` in the same process throws (`restart required`) — hence one model per process, serial driver runs.
- The `corpus` scenario loads **two** engine instances (primary + fallback) because `BeginStream` holds the engine-wide `SemaphoreSlim(1,1)` compute gate for the stream's whole lifetime, so a same-engine batch fallback inside `FinishAsync` deadlocks-to-throw. Preserve this for streaming mode; batch-only mode needs only ONE engine.
- Corpus flow anchors in `scripts/asr-latency-bench/Program.cs`: arg parse loop `:31-46`, corpus case `:302-476`, clip enumeration `manifest.Entries.Where(e => !e.Exclude)` `:336`, batch parity `corpusEngine.TranscribeBatch(wavAudio)` (untrimmed audio), streaming replay via `EvalFraming.Segments` + `session.FinishAsync(trimResult.Trimmed, ...)` (trimmed — deliberate production-faithful asymmetry, do NOT "fix"), results write `:455-474`.
- Existing records (`scripts/asr-latency-bench/EvalResults.cs`):
  ```csharp
  public sealed record ClipResult(
      string Id, double AudioSeconds, bool ExpectedSilent, bool HasReference,
      string Reference, string StreamText, string BatchText,
      double? Wer, double? Cer, bool? SilentPass,
      IReadOnlyList<long> FinishMsRuns,
      bool FellBack, int FellBackCount, bool Truncated, bool TrimmedSilent,
      string BatchParityDiff, string? Error = null);
  public sealed record EvalRunInfo(
      string Corpus, string SpeechModel, string TranscribeCppVersion, string DateUtc, int Repeats);
  public sealed record EvalSummary(
      int ClipCount, int ScoredCount, double? MeanWer, double? MedianWer, double? MeanCer,
      long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
      int FallbackCount, int TruncatedCount, int SilentClipCount, int SilentPassCount, int FailedCount);
  public sealed record EvalReport(EvalRunInfo Info, EvalSummary Summary, IReadOnlyList<ClipResult> Clips);
  ```
  plus `EvalResults.Percentile(IReadOnlyList<double> sortedAscending, double q)` (nearest-rank, `ceil(q*n)-1`), `EvalResults.Summarize(clips)`, `ToJson`, `ToMarkdown` (text-free by contract), private `JsonOpts` (camelCase, indented).
- Production model dir (read-only) layout the eval dirs mirror:
  ```
  <model-dir>/
    <exactly one>.gguf
    runtime/transcribe-native-windows-x86_64-cpu-vulkan/{transcribe.dll, contract.json, ggml-*.dll}
  ```
- The bench project builds standalone: `dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release -p:EnableWindowsTargeting=true`. It is not built by `linux-tests.sh`, so every task that touches `Program.cs` includes an explicit Linux compile check.

## Scope Check

This is one subsystem (the eval bench plus its one engine seam), so one plan. The plan produces working, testable software at every task boundary; the Windows proof runs at the end are the system-level test.

## File Structure

| File | Status | Responsibility |
|---|---|---|
| `src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs` | Modify | Add `RunParams` struct (mirror of `transcribe_run_params`), `transcribe_run_params_init` P/Invoke, `ABI_RUN_PARAMS = 2` |
| `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs` | Modify | ABI size gate for `RunParams`; build/pass run params when a language is given in `TranscribeBatch`/`BeginStream` |
| `src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs` | Modify | `BeginStream(int, string?)` / `TranscribeBatch(float[], string?)` optional language |
| `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` | Modify | Optional `language` ctor param, forwarded to `BeginStream` |
| `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs` | Modify | `RunParams` size/offset assertions |
| `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` | Modify | Record languages passed to `BeginStream`/`TranscribeBatch` |
| `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs` | Modify | Language-forwarding test |
| `scripts/asr-latency-bench/Convergence.cs` | Create (BCL-only) | 95% CI math, per-pass convergence points, converged rule, median |
| `scripts/asr-latency-bench/ResourceUsage.cs` | Create (BCL-only) | CPU-time/peak-working-set capture, delta, RTF, MB conversion |
| `scripts/asr-latency-bench/EvalPasses.cs` | Create (BCL-only) | `PassSummary` record + per-pass aggregation |
| `scripts/asr-latency-bench/ModelDirLayout.cs` | Create (BCL-only) | Resolve `--model-dir` → (gguf path, runtime dir) |
| `scripts/asr-latency-bench/EvalComparison.cs` | Create (BCL-only) | Read N `EvalReport`s → `comparison.json` |
| `scripts/asr-latency-bench/BenchArgs.cs` | Modify (BCL-only) | Validators for `--max-clips`, `--time-budget-minutes`, `--min-passes`/`--max-passes` |
| `scripts/asr-latency-bench/EvalResults.cs` | Modify (BCL-only) | Extended records (mode, language, passes, converged, resources, transcript stability, batch times), mode-aware `Summarize`, extended `ToJson`/`ToMarkdown` |
| `scripts/asr-latency-bench/Program.cs` | Modify | New flags; corpus pass loop with batch-only branch, resource capture, convergence; `compare` scenario |
| `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` | Modify | `Compile Include` links for the five new BCL files |
| `tests/Winpepper.Asr.Tests/ConvergenceTests.cs` | Create | Convergence math tests |
| `tests/Winpepper.Asr.Tests/ResourceUsageTests.cs` | Create | Resource math tests |
| `tests/Winpepper.Asr.Tests/EvalPassesTests.cs` | Create | Per-pass aggregation tests |
| `tests/Winpepper.Asr.Tests/ModelDirLayoutTests.cs` | Create | Model-dir resolution tests (temp dirs) |
| `tests/Winpepper.Asr.Tests/EvalComparisonTests.cs` | Create | Comparison aggregation tests |
| `tests/Winpepper.Asr.Tests/BenchArgsTests.cs` | Modify | New validator tests |
| `tests/Winpepper.Asr.Tests/EvalResultsTests.cs` | Modify | Mode marking, batch-time summaries, passes/trace JSON tests |
| `scripts/setup-asr-eval-models.sh` | Create | Stage per-model eval dirs under `C:\Users\dan\winpepper-evals\models\<name>\` |
| `scripts/run-asr-model-eval-windows.sh` | Create | Parameterized driver: model dir/name/language/batch-only/budget → `artifacts/asr-eval/<model-name>/` |
| `docs/plans/2026-07-27-asr-model-comparison-evidence.md` | Create (Task 12) | Proof-run evidence: numbers + clip ids only |

---

### Task 1: `transcribe_run_params` in the native binding

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs`
- Modify: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs` (ABI gate only)
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `TranscribeCppNative.RunParams` (struct, fields below), `TranscribeCppNative.transcribe_run_params_init(ref RunParams)`, `TranscribeCppNative.ABI_RUN_PARAMS == 2`. Task 2 builds on these.

- [ ] **Step 1: (One-time verification, no code) Confirm the header facts**

Run:
```bash
curl -fsSL https://raw.githubusercontent.com/handy-computer/transcribe.cpp/v0.1.3/include/transcribe.h -o /tmp/transcribe-0.1.3.h
grep -n "TRANSCRIBE_ABI_RUN_PARAMS" /tmp/transcribe-0.1.3.h
sed -n '/^struct transcribe_run_params {/,/^};/p' /tmp/transcribe-0.1.3.h
```
Expected: `TRANSCRIBE_ABI_RUN_PARAMS        = 2,` and the struct exactly as quoted in "Verified facts" above (fields in order: `struct_size, task, timestamps, pnc, itn, language, target_language, keep_special_tags, family, spec_k_drafts`). If it differs, STOP and reconcile the offsets below against the real header before writing any code.

- [ ] **Step 2: Write the failing struct-layout test**

Open `tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs`, find the existing size/offset tests (they assert `Marshal.SizeOf`/`Marshal.OffsetOf` for the five gated structs and `ParakeetStreamExt`), and add alongside them, in the same class and style:

```csharp
[Fact]
public void RunParams_layout_matches_transcribe_h_v013()
{
    Marshal.SizeOf<TranscribeCppNative.RunParams>().ShouldBe(64);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.struct_size)).ToInt32().ShouldBe(0);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.task)).ToInt32().ShouldBe(8);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.timestamps)).ToInt32().ShouldBe(12);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.pnc)).ToInt32().ShouldBe(16);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.itn)).ToInt32().ShouldBe(20);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.language)).ToInt32().ShouldBe(24);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.target_language)).ToInt32().ShouldBe(32);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.keep_special_tags)).ToInt32().ShouldBe(40);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.family)).ToInt32().ShouldBe(48);
    Marshal.OffsetOf<TranscribeCppNative.RunParams>(nameof(TranscribeCppNative.RunParams.spec_k_drafts)).ToInt32().ShouldBe(56);
    TranscribeCppNative.ABI_RUN_PARAMS.ShouldBe(2);
}
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0117: 'TranscribeCppNative' does not contain a definition for 'RunParams'` (a compile failure is the failing state for layout tests).

- [ ] **Step 4: Declare the struct + P/Invoke in `TranscribeCppNative.cs`**

Add next to the existing `ABI_*` constants:
```csharp
public const int ABI_RUN_PARAMS = 2;
```
Add next to the existing structs (`ModelLoadParams`, `StreamParams`, ...), following the file's documented marshaling rules (explicit layout, `size_t`→`ulong` for struct_size, C enum→`int`, C `bool` field→`byte`, `const char*`→`IntPtr`):
```csharp
/// <summary>Mirror of struct transcribe_run_params (transcribe.h v0.1.3, ABI id 2).
/// x64: 4-byte C enums, 8-byte pointers, 1-byte bool at 40 (7 pad), total 64 bytes.
/// language/target_language are caller-owned UTF-8; the library copies them before
/// transcribe_run / transcribe_stream_begin RETURNS (header-documented), so buffers
/// may be freed immediately after the call.</summary>
[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct RunParams
{
    [FieldOffset(0)]  public ulong  struct_size;
    [FieldOffset(8)]  public int    task;               // transcribe_task
    [FieldOffset(12)] public int    timestamps;         // transcribe_timestamp_kind
    [FieldOffset(16)] public int    pnc;                // transcribe_pnc_mode
    [FieldOffset(20)] public int    itn;                // transcribe_itn_mode
    [FieldOffset(24)] public IntPtr language;           // const char* UTF-8; Zero = autodetect/model default
    [FieldOffset(32)] public IntPtr target_language;    // const char*; Zero
    [FieldOffset(40)] public byte   keep_special_tags;  // C bool; 0 = strip tags (default)
    [FieldOffset(48)] public IntPtr family;             // const struct transcribe_ext*
    [FieldOffset(56)] public int    spec_k_drafts;
}

[DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
public static extern void transcribe_run_params_init(ref RunParams p);
```

- [ ] **Step 5: Add `RunParams` to the ABI struct-size gate in `TranscribeCppEngine.Load`**

In `TranscribeCppEngine.cs`, locate the existing ABI struct-size gate (it verifies `transcribe_abi_struct_size(id)` against `Marshal.SizeOf<T>()` for all five marshaled structs, non-short-circuit, collecting mismatches before throwing). Add a sixth entry for `(TranscribeCppNative.ABI_RUN_PARAMS, Marshal.SizeOf<TranscribeCppNative.RunParams>())` with the name `"run_params"`, following the exact pattern of the existing five entries (same collection, same error formatting). Also update the class doc comment's gate description from "all 5 marshaled structs" to "all 6 marshaled structs".

- [ ] **Step 6: Run the test to verify it passes**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS — summary line ends `Errors: 0` and `Failed: 0`, including `RunParams_layout_matches_transcribe_h_v013`.

- [ ] **Step 7: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.
```bash
git add src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs \
        src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs \
        tests/Winpepper.Asr.Tests/TranscribeCpp/TranscribeCppStructLayoutTests.cs
git commit -m "feat(asr): bind transcribe.cpp run_params (ABI id 2) with size gate"
```

---

### Task 2: Language pass-through: engine, interface, streaming transcriber

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs`
- Modify: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs`
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
- Modify: `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: `TranscribeCppNative.RunParams`, `transcribe_run_params_init` (Task 1).
- Produces (Tasks 9 relies on these exact signatures):
  ```csharp
  // ITranscribeCppEngine
  ITranscribeCppStream BeginStream(int attContextRight, string? language = null);
  string TranscribeBatch(float[] mono16k, string? language = null);
  // NemotronStreamingTranscriber ctor
  public NemotronStreamingTranscriber(
      Func<ITranscribeCppEngine> engineProvider,
      ITranscriber batchFallback,
      string modelName,
      ILogger? log = null,
      int attContextRight = 13,
      string? language = null)
  ```
  Semantics: `language == null` ⇒ native calls receive `IntPtr.Zero` run params, byte-identical to today (production path unchanged).

- [ ] **Step 1: Write the failing language-forwarding tests**

In `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs`, extend the fake: change its `BeginStream`/`TranscribeBatch` to the new signatures and record languages. Add fields following the fake's existing recording style (`BeginStreamCalls`, `AttContextRight`, ...):

```csharp
public readonly List<string?> BeginStreamLanguages = new();
public string? LastBatchLanguage;
```
and inside `BeginStream(int attContextRight, string? language = null)` add `BeginStreamLanguages.Add(language);`; inside `TranscribeBatch(float[] mono16k, string? language = null)` add `LastBatchLanguage = language;`.

In `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`, add (mirroring the file's existing arrange/act style — it drives the transcriber via `StartSessionAsync` + `PushAsync`/`FinishAsync` against the fake; copy the arrangement of an existing happy-path test):

```csharp
[Fact]
public async Task Language_is_forwarded_to_BeginStream()
{
    var engine = new FakeTranscribeCppEngine { FinalText = "hello world" };
    var sut = new NemotronStreamingTranscriber(
        () => engine, new FakeBatchTranscriber(), "nemotron-3.5-asr-streaming-0.6b",
        log: null, attContextRight: 13, language: "en-US");
    var session = await sut.StartSessionAsync(CancellationToken.None);
    await session.PushAsync(new float[2560], CancellationToken.None);
    await session.FinishAsync(new float[2560], CancellationToken.None);
    engine.BeginStreamLanguages.ShouldBe(new string?[] { "en-US" });
}

[Fact]
public async Task Default_language_is_null()
{
    var engine = new FakeTranscribeCppEngine { FinalText = "hello world" };
    var sut = new NemotronStreamingTranscriber(
        () => engine, new FakeBatchTranscriber(), "nemotron-streaming-en");
    var session = await sut.StartSessionAsync(CancellationToken.None);
    await session.PushAsync(new float[2560], CancellationToken.None);
    await session.FinishAsync(new float[2560], CancellationToken.None);
    engine.BeginStreamLanguages.ShouldBe(new string?[] { null });
}
```
NOTE: `FakeBatchTranscriber` here stands for whatever `ITranscriber` fake the existing tests in this file already use for the `batchFallback` argument — reuse that exact type/arrangement rather than inventing a new one. Same for the session-driving calls: match the file's existing method names exactly (the session interface is `IStreamingTranscriptionSession`; use the same push/finish calls the neighboring tests use).

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS — `NemotronStreamingTranscriber` has no `language` parameter and the fake's overrides no longer match `ITranscribeCppEngine` (once Step 1's fake edit is in, the interface mismatch also fails).

- [ ] **Step 3: Implement**

3a. `ITranscribeCppEngine.cs` — change the two members (keep existing doc comments, extend them):
```csharp
/// attContextRight in encoder frames: {13,6,1,0} = {1040,480,80,0} ms.
/// language: optional source-language hint (BCP-47-ish, e.g. "en-US"); null = model default/autodetect.
ITranscribeCppStream BeginStream(int attContextRight, string? language = null);
/// Offline single-utterance transcription on a dedicated native session.
string TranscribeBatch(float[] mono16k, string? language = null);
```

3b. `TranscribeCppEngine.cs` — add two private static helpers:
```csharp
/// <summary>Marshals a transcribe_run_params carrying a language hint. Returns
/// (Zero, Zero) for a null language so callers pass IntPtr.Zero exactly as before.
/// Free with FreeRunParams immediately after the native call returns — the header
/// guarantees run params and their strings are copied before transcribe_run /
/// transcribe_stream_begin return.</summary>
private static (IntPtr Params, IntPtr Lang) AllocRunParams(string? language)
{
    if (language is null) return (IntPtr.Zero, IntPtr.Zero);
    var rp = new TranscribeCppNative.RunParams();
    TranscribeCppNative.transcribe_run_params_init(ref rp);
    var pLang = Marshal.StringToCoTaskMemUTF8(language);
    rp.language = pLang;
    var pRp = Marshal.AllocHGlobal(Marshal.SizeOf<TranscribeCppNative.RunParams>());
    Marshal.StructureToPtr(rp, pRp, fDeleteOld: false);
    return (pRp, pLang);
}

private static void FreeRunParams((IntPtr Params, IntPtr Lang) rp)
{
    if (rp.Params != IntPtr.Zero) Marshal.FreeHGlobal(rp.Params);
    if (rp.Lang != IntPtr.Zero) Marshal.FreeCoTaskMem(rp.Lang);
}
```

3c. `TranscribeBatch` (currently ~`:231-257`): change the signature to `public string TranscribeBatch(float[] mono16k, string? language = null)` and replace the `transcribe_run(session, mono16k, mono16k.Length, IntPtr.Zero)` call with:
```csharp
var runParams = AllocRunParams(language);
int stRun;
try { stRun = TranscribeCppNative.transcribe_run(session, mono16k, mono16k.Length, runParams.Params); }
finally { FreeRunParams(runParams); }
```
Leave everything else (compute gate, session init with `IntPtr.Zero`, `transcribe_full_text`, `transcribe_session_free`, error handling) exactly as it is.

3d. `BeginStream` (currently ~`:180-220`): change the signature to `public ITranscribeCppStream BeginStream(int attContextRight, string? language = null)` and replace the `transcribe_stream_begin(session, IntPtr.Zero, pSp)` call with:
```csharp
var runParams = AllocRunParams(language);
int stBegin;
try { stBegin = TranscribeCppNative.transcribe_stream_begin(session, runParams.Params, pSp); }
finally { FreeRunParams(runParams); }
```
Do NOT change the lifetime handling of `pExt`/`pSp` (they must outlive the stream as today); only the run-params pointer is freed immediately (header-guaranteed copy at begin).

3e. `NemotronStreamingTranscriber.cs`: add ctor param `string? language = null` (after `attContextRight`), store `_language`, thread it into the inner `Session` (alongside `_attContextRight`), and change the single `BeginStream` call in `EnsureStream()` to `_engineProvider().BeginStream(_attContextRight, _language)`.

3f. `FakeTranscribeCppEngine.cs`: already updated in Step 1.

- [ ] **Step 4: Run to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS with `Errors: 0`, `Failed: 0` — including all pre-existing `NemotronStreamingTranscriberTests` (default-parameter call sites keep compiling unchanged).

- [ ] **Step 5: Prove the whole repo still compiles (the interface changed)**

```bash
./scripts/linux-tests.sh
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release -p:EnableWindowsTargeting=true
rm -rf scripts/asr-latency-bench/bin scripts/asr-latency-bench/obj
```
Expected: `LINUX SUITE: GREEN` and the bench builds with 0 errors (its `EngineBatchTranscriber`/`corpus` call sites use the defaults).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs \
        src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs \
        src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs \
        tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs
git commit -m "feat(asr): optional language hint through TranscribeBatch/BeginStream (run_params)"
```

---

### Task 3: Convergence math (`Convergence.cs`)

**Files:**
- Create: `scripts/asr-latency-bench/Convergence.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add link)
- Test: `tests/Winpepper.Asr.Tests/ConvergenceTests.cs`

**Interfaces:**
- Consumes: `EvalResults.Percentile(IReadOnlyList<double>, double)` (existing).
- Produces (Tasks 6, 8, 9 rely on these):
  ```csharp
  namespace AsrLatencyBench;
  public sealed record ConvergencePoint(
      int Pass, double MeanMs, double CiHalfWidthMs, double RatioToMean, bool Precise);
  public static class Convergence
  {
      public const double PreciseRatio = 0.05;
      public static double Mean(IReadOnlyList<double> values);
      public static double SampleStdDev(IReadOnlyList<double> values);   // n-1 denominator; 0 when n < 2
      public static double CiHalfWidth95(IReadOnlyList<double> values);  // 1.96 * sd / sqrt(n); 0 when n < 2
      public static double Median(IReadOnlyList<double> values);         // nearest-rank via EvalResults.Percentile
      public static ConvergencePoint Evaluate(int pass, IReadOnlyList<double> perClipMedians);
      public static bool Converged(IReadOnlyList<ConvergencePoint> trace); // last TWO points Precise
  }
  ```

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/ConvergenceTests.cs`:
```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ConvergenceTests
{
    [Fact]
    public void SampleStdDev_matches_known_value()
    {
        // values 2,4,4,4,5,5,7,9: mean 5, sample variance 32/7
        Convergence.SampleStdDev(new double[] { 2, 4, 4, 4, 5, 5, 7, 9 })
            .ShouldBe(Math.Sqrt(32.0 / 7.0), 1e-9);
    }

    [Fact]
    public void SampleStdDev_is_zero_for_fewer_than_two_values()
    {
        Convergence.SampleStdDev(new double[] { 42 }).ShouldBe(0);
        Convergence.SampleStdDev(Array.Empty<double>()).ShouldBe(0);
    }

    [Fact]
    public void CiHalfWidth95_is_196_sd_over_sqrt_n()
    {
        var values = new double[] { 90, 100, 110 };   // sd = 10, n = 3
        Convergence.CiHalfWidth95(values).ShouldBe(1.96 * 10 / Math.Sqrt(3), 1e-9);
    }

    [Fact]
    public void Median_uses_nearest_rank()
    {
        Convergence.Median(new double[] { 30, 10, 20 }).ShouldBe(20);
        Convergence.Median(new double[] { 10, 20 }).ShouldBe(10); // nearest-rank ceil(0.5*2)-1 = index 0
    }

    [Fact]
    public void Evaluate_identical_medians_is_precise()
    {
        var p = Convergence.Evaluate(3, new double[] { 100, 100, 100, 100 });
        p.Pass.ShouldBe(3);
        p.MeanMs.ShouldBe(100);
        p.CiHalfWidthMs.ShouldBe(0);
        p.RatioToMean.ShouldBe(0);
        p.Precise.ShouldBeTrue();
    }

    [Fact]
    public void Evaluate_wide_spread_is_not_precise()
    {
        // sd = 100/sqrt(... ) — ratio far above 0.05
        var p = Convergence.Evaluate(1, new double[] { 50, 150 });
        p.Precise.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_zero_or_negative_mean_is_never_precise()
    {
        Convergence.Evaluate(1, new double[] { 0, 0, 0 }).Precise.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_single_clip_is_never_precise()
    {
        // n < 2: no spread information, must not converge on it
        Convergence.Evaluate(1, new double[] { 100 }).Precise.ShouldBeFalse();
    }

    [Fact]
    public void Converged_requires_two_consecutive_precise_points()
    {
        ConvergencePoint P(int pass, bool precise) => new(pass, 100, precise ? 1 : 50, precise ? 0.01 : 0.5, precise);
        Convergence.Converged(new[] { P(1, true) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, false), P(2, true) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, true), P(2, false) }).ShouldBeFalse();
        Convergence.Converged(new[] { P(1, true), P(2, true) }).ShouldBeTrue();
        Convergence.Converged(new[] { P(1, false), P(2, true), P(3, true) }).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Add the csproj link, run to verify failure**

In `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`, inside the existing `<ItemGroup>` of `Compile Include` links (lines ~21-31), add:
```xml
<Compile Include="..\..\scripts\asr-latency-bench\Convergence.cs" Link="Bench\Convergence.cs" />
```
Run:
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (`Convergence.cs` does not exist yet).

- [ ] **Step 3: Implement `scripts/asr-latency-bench/Convergence.cs`**

```csharp
namespace AsrLatencyBench;

/// <summary>One pass's convergence measurement over the pooled per-clip medians
/// of the mode's speed metric (streaming: post-stop latency ms; batch: batch
/// transcribe ms). Precise = the 95% confidence-interval half-width of the mean
/// is below 5% of the mean. The run has converged when two CONSECUTIVE passes
/// are precise.</summary>
public sealed record ConvergencePoint(
    int Pass, double MeanMs, double CiHalfWidthMs, double RatioToMean, bool Precise);

public static class Convergence
{
    /// <summary>CI half-width must be below this fraction of the mean.</summary>
    public const double PreciseRatio = 0.05;

    public static double Mean(IReadOnlyList<double> values)
        => values.Count == 0 ? 0 : values.Average();

    /// <summary>Sample standard deviation (n-1). 0 when fewer than 2 values.</summary>
    public static double SampleStdDev(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = Mean(values);
        var sumSq = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSq / (values.Count - 1));
    }

    /// <summary>95% confidence-interval half-width of the mean, normal
    /// approximation: 1.96 * sd / sqrt(n). 0 when fewer than 2 values.</summary>
    public static double CiHalfWidth95(IReadOnlyList<double> values)
        => values.Count < 2 ? 0 : 1.96 * SampleStdDev(values) / Math.Sqrt(values.Count);

    /// <summary>Nearest-rank median (same convention as EvalResults.Percentile).</summary>
    public static double Median(IReadOnlyList<double> values)
        => EvalResults.Percentile(values.OrderBy(v => v).ToArray(), 0.5);

    public static ConvergencePoint Evaluate(int pass, IReadOnlyList<double> perClipMedians)
    {
        var mean = Mean(perClipMedians);
        var half = CiHalfWidth95(perClipMedians);
        var ratio = mean <= 0 ? double.PositiveInfinity : half / mean;
        var precise = perClipMedians.Count >= 2 && mean > 0 && ratio < PreciseRatio;
        return new ConvergencePoint(pass, mean, half, ratio, precise);
    }

    public static bool Converged(IReadOnlyList<ConvergencePoint> trace)
        => trace.Count >= 2 && trace[^1].Precise && trace[^2].Precise;
}
```
NOTE: `RatioToMean` may be `double.PositiveInfinity`; JSON serialization of the trace must therefore serialize a finite value — `Evaluate` stays as-is, and Task 6's `ToJson` uses `JsonNumberHandling.AllowNamedFloatingPointLiterals` (spelled out there).

- [ ] **Step 4: Run to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/Convergence.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/ConvergenceTests.cs
git commit -m "feat(bench): convergence rule - 95% CI half-width below 5% of mean, two consecutive passes"
```

---

### Task 4: Resource utilization (`ResourceUsage.cs`)

**Files:**
- Create: `scripts/asr-latency-bench/ResourceUsage.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add link)
- Test: `tests/Winpepper.Asr.Tests/ResourceUsageTests.cs`

**Interfaces:**
- Consumes: `System.Diagnostics.Process` (BCL).
- Produces (Tasks 5, 9 rely on these):
  ```csharp
  namespace AsrLatencyBench;
  public sealed record ResourceSample(double CpuSeconds, long PeakWorkingSetBytes);
  public static class ResourceUsage
  {
      public static ResourceSample Capture();                            // current process
      public static double CpuDelta(ResourceSample before, ResourceSample after); // clamped >= 0
      public static double Rtf(double processingSeconds, double audioSeconds);    // 0 when audioSeconds <= 0
      public static double ToMb(long bytes);
  }
  ```
  Semantics: `CpuSeconds` = `Process.TotalProcessorTime.TotalSeconds` (user + privileged); `PeakWorkingSetBytes` = `Process.PeakWorkingSet64` (process-lifetime peak, monotonic). GPU/Vulkan usage is NOT measured (noted in results, Task 6).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/ResourceUsageTests.cs`:
```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ResourceUsageTests
{
    [Fact]
    public void CpuDelta_is_after_minus_before()
    {
        var before = new ResourceSample(1.5, 100);
        var after = new ResourceSample(2.75, 200);
        ResourceUsage.CpuDelta(before, after).ShouldBe(1.25, 1e-9);
    }

    [Fact]
    public void CpuDelta_clamps_negative_to_zero()
    {
        ResourceUsage.CpuDelta(new ResourceSample(5, 0), new ResourceSample(4, 0)).ShouldBe(0);
    }

    [Theory]
    [InlineData(2.0, 10.0, 0.2)]
    [InlineData(10.0, 10.0, 1.0)]
    [InlineData(1.0, 0.0, 0.0)]   // no audio -> 0, never divide by zero
    [InlineData(1.0, -1.0, 0.0)]
    public void Rtf_is_processing_over_audio(double processing, double audio, double expected)
    {
        ResourceUsage.Rtf(processing, audio).ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void ToMb_converts_bytes()
    {
        ResourceUsage.ToMb(3 * 1024 * 1024).ShouldBe(3.0, 1e-9);
    }

    [Fact]
    public void Capture_returns_live_nonnegative_values_and_is_monotonic()
    {
        var a = ResourceUsage.Capture();
        // burn a little CPU so the delta is observable
        var x = 0.0;
        for (var i = 0; i < 5_000_000; i++) x += Math.Sqrt(i);
        x.ShouldBeGreaterThan(0);
        var b = ResourceUsage.Capture();
        a.CpuSeconds.ShouldBeGreaterThanOrEqualTo(0);
        a.PeakWorkingSetBytes.ShouldBeGreaterThan(0);
        b.CpuSeconds.ShouldBeGreaterThanOrEqualTo(a.CpuSeconds);
        b.PeakWorkingSetBytes.ShouldBeGreaterThanOrEqualTo(a.PeakWorkingSetBytes);
    }
}
```

- [ ] **Step 2: Add link, run to verify failure**

Add to the same `<ItemGroup>` in `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`:
```xml
<Compile Include="..\..\scripts\asr-latency-bench\ResourceUsage.cs" Link="Bench\ResourceUsage.cs" />
```
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (file missing).

- [ ] **Step 3: Implement `scripts/asr-latency-bench/ResourceUsage.cs`**

```csharp
namespace AsrLatencyBench;

/// <summary>A point-in-time reading of the CURRENT process's resource use.
/// CpuSeconds = total processor time (user + privileged). PeakWorkingSetBytes
/// is the process-lifetime peak (monotonic; it cannot be reset per clip).
/// GPU/Vulkan usage is not measured by this type at all.</summary>
public sealed record ResourceSample(double CpuSeconds, long PeakWorkingSetBytes);

public static class ResourceUsage
{
    public static ResourceSample Capture()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        return new ResourceSample(p.TotalProcessorTime.TotalSeconds, p.PeakWorkingSet64);
    }

    public static double CpuDelta(ResourceSample before, ResourceSample after)
        => Math.Max(0, after.CpuSeconds - before.CpuSeconds);

    /// <summary>Real-time factor: processing seconds per second of audio.
    /// 0 when there is no audio (never divides by zero).</summary>
    public static double Rtf(double processingSeconds, double audioSeconds)
        => audioSeconds <= 0 ? 0 : processingSeconds / audioSeconds;

    public static double ToMb(long bytes) => bytes / (1024.0 * 1024.0);
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/ResourceUsage.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/ResourceUsageTests.cs
git commit -m "feat(bench): per-run resource capture - process CPU time, peak working set, RTF"
```

---

### Task 5: Per-pass aggregation (`EvalPasses.cs`)

**Files:**
- Create: `scripts/asr-latency-bench/EvalPasses.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add link)
- Test: `tests/Winpepper.Asr.Tests/EvalPassesTests.cs`

**Interfaces:**
- Consumes: `EvalResults.Percentile` (existing), `ResourceUsage.ToMb` (Task 4).
- Produces (Tasks 6, 9 rely on these):
  ```csharp
  namespace AsrLatencyBench;
  public sealed record PassSummary(
      int Pass,
      long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
      double CpuSeconds, double PeakMemoryMb, double MeanRtf,
      double? MeanWer, int FailedCount);
  public static class EvalPasses
  {
      public static PassSummary Summarize(
          int pass,
          IReadOnlyList<double> latenciesMs,   // this pass's per-clip speed samples, already > 0 filtered
          IReadOnlyList<double> rtfs,
          IReadOnlyList<double> wers,
          double cpuSeconds,
          long peakWorkingSetBytes,
          int failedCount);
  }
  ```
  "Latency" here means the mode's speed metric: streaming = post-stop latency (FinishAsync ms); batch = whole-file batch transcribe ms.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/EvalPassesTests.cs`:
```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class EvalPassesTests
{
    [Fact]
    public void Summarize_computes_percentiles_cpu_memory_rtf_wer()
    {
        var s = EvalPasses.Summarize(
            pass: 2,
            latenciesMs: new double[] { 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000 },
            rtfs: new double[] { 0.1, 0.3 },
            wers: new double[] { 0.10, 0.20 },
            cpuSeconds: 12.3456,
            peakWorkingSetBytes: 2L * 1024 * 1024 * 1024,
            failedCount: 1);
        s.Pass.ShouldBe(2);
        s.LatencyP50Ms.ShouldBe(500);   // nearest-rank ceil(0.5*10)-1 = index 4
        s.LatencyP90Ms.ShouldBe(900);   // ceil(0.9*10)-1 = index 8
        s.LatencyMaxMs.ShouldBe(1000);
        s.CpuSeconds.ShouldBe(12.346);  // rounded to 3 decimals
        s.PeakMemoryMb.ShouldBe(2048.0);
        s.MeanRtf.ShouldBe(0.2);
        s.MeanWer.ShouldBe(0.15);
        s.FailedCount.ShouldBe(1);
    }

    [Fact]
    public void Summarize_handles_empty_inputs()
    {
        var s = EvalPasses.Summarize(1, Array.Empty<double>(), Array.Empty<double>(),
            Array.Empty<double>(), 0, 0, 0);
        s.LatencyP50Ms.ShouldBe(0);
        s.LatencyMaxMs.ShouldBe(0);
        s.MeanRtf.ShouldBe(0);
        s.MeanWer.ShouldBeNull();
    }

    [Fact]
    public void Summarize_sorts_latencies_itself()
    {
        var s = EvalPasses.Summarize(1, new double[] { 900, 100, 500 },
            Array.Empty<double>(), Array.Empty<double>(), 0, 0, 0);
        s.LatencyP50Ms.ShouldBe(500);
        s.LatencyMaxMs.ShouldBe(900);
    }
}
```

- [ ] **Step 2: Add link, run to verify failure**

Add to the csproj `<ItemGroup>`:
```xml
<Compile Include="..\..\scripts\asr-latency-bench\EvalPasses.cs" Link="Bench\EvalPasses.cs" />
```
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (file missing).

- [ ] **Step 3: Implement `scripts/asr-latency-bench/EvalPasses.cs`**

```csharp
namespace AsrLatencyBench;

/// <summary>Aggregates for one full pass over the corpus. "Latency" is the
/// mode's speed metric: streaming = post-stop latency (FinishAsync ms);
/// batch = whole-file batch transcribe ms.</summary>
public sealed record PassSummary(
    int Pass,
    long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
    double CpuSeconds, double PeakMemoryMb, double MeanRtf,
    double? MeanWer, int FailedCount);

public static class EvalPasses
{
    public static PassSummary Summarize(
        int pass,
        IReadOnlyList<double> latenciesMs,
        IReadOnlyList<double> rtfs,
        IReadOnlyList<double> wers,
        double cpuSeconds,
        long peakWorkingSetBytes,
        int failedCount)
    {
        var sorted = latenciesMs.OrderBy(v => v).ToArray();
        return new PassSummary(
            Pass: pass,
            LatencyP50Ms: (long)EvalResults.Percentile(sorted, 0.5),
            LatencyP90Ms: (long)EvalResults.Percentile(sorted, 0.9),
            LatencyMaxMs: sorted.Length == 0 ? 0 : (long)sorted[^1],
            CpuSeconds: Math.Round(cpuSeconds, 3),
            PeakMemoryMb: Math.Round(ResourceUsage.ToMb(peakWorkingSetBytes), 1),
            MeanRtf: rtfs.Count == 0 ? 0 : Math.Round(rtfs.Average(), 4),
            MeanWer: wers.Count == 0 ? null : wers.Average(),
            FailedCount: failedCount);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Same build + exec commands as Task 4 Step 4. Expected: PASS, `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalPasses.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/EvalPassesTests.cs
git commit -m "feat(bench): per-pass aggregation - latency percentiles, CPU s, peak MB, mean RTF, WER"
```

---

### Task 6: Results schema: mode, passes, convergence trace, resources

**Files:**
- Modify: `scripts/asr-latency-bench/EvalResults.cs`
- Test: `tests/Winpepper.Asr.Tests/EvalResultsTests.cs`

**Interfaces:**
- Consumes: `PassSummary` (Task 5), `ConvergencePoint` (Task 3).
- Produces (Tasks 8, 9 rely on these EXACT shapes). All additions are optional parameters APPENDED to the records, so every existing positional construction keeps compiling:
  ```csharp
  public sealed record ClipResult(
      /* ...all existing params unchanged through...*/ string? Error = null,
      IReadOnlyList<long>? BatchMsRuns = null,   // whole-file batch transcribe ms per pass
      double CpuSeconds = 0,                     // total process CPU s this clip, all passes
      double MeanRtf = 0,
      bool TranscriptStable = true);             // scored transcript identical across passes

  public sealed record EvalRunInfo(
      /* ...existing 5 params unchanged... */,
      string Mode = "streaming",                 // "streaming" | "batch"
      string? Language = null,
      int Passes = 1,
      bool Converged = false,
      string ResourceNote = EvalResults.ResourceNote);

  public sealed record EvalSummary(
      /* ...existing 13 params unchanged... */,
      double CpuSecondsTotal = 0,
      double PeakMemoryMb = 0,
      double MeanRtf = 0,
      int UnstableTranscriptCount = 0);

  public sealed record EvalReport(
      EvalRunInfo Info, EvalSummary Summary, IReadOnlyList<ClipResult> Clips,
      IReadOnlyList<PassSummary>? Passes = null,
      IReadOnlyList<ConvergencePoint>? ConvergenceTrace = null);

  public static class EvalResults
  {
      public const string ResourceNote =
          "resources are process CPU time and peak working set only; GPU/Vulkan usage is not separately measured. " +
          "RTF = processing time / audio duration (streaming: process CPU seconds; batch: batch transcribe wall time)";
      public static readonly JsonSerializerOptions JsonOpts; // was private -> make public (Task 8 deserializes with it)
      public static EvalSummary Summarize(IReadOnlyList<ClipResult> clips,
          string mode = "streaming", double cpuSecondsTotal = 0, double peakMemoryMb = 0);
      public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary);          // existing, kept
      public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary,
          IReadOnlyList<PassSummary> passes, IReadOnlyList<ConvergencePoint> convergenceTrace);                     // new overload
      // Percentile, ToMarkdown as before (ToMarkdown gains mode/pass lines, stays text-free)
  }
  ```
  Mode-aware `Summarize` semantics: when `mode == "batch"`, latency percentiles pool `BatchMsRuns` instead of `FinishMsRuns` (still `> 0`-filtered, flattened across clips and passes); `MeanRtf` = average of clips' `MeanRtf` where `> 0`; `UnstableTranscriptCount` = count of clips with `TranscriptStable == false`; `CpuSecondsTotal`/`PeakMemoryMb` are passed through from the caller.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/EvalResultsTests.cs` (match its existing helper style for constructing `ClipResult`s — it builds them positionally; reuse its patterns):
```csharp
[Fact]
public void Summarize_batch_mode_pools_batch_times()
{
    var clip = new ClipResult(
        "c1", 10.0, false, true, "ref text", "", "batch text",
        0.1, 0.05, null,
        FinishMsRuns: Array.Empty<long>(),
        FellBack: false, FellBackCount: 0, Truncated: false, TrimmedSilent: false,
        BatchParityDiff: "", Error: null,
        BatchMsRuns: new long[] { 800, 1200 },
        CpuSeconds: 3.5, MeanRtf: 0.4, TranscriptStable: true);
    var s = EvalResults.Summarize(new[] { clip }, mode: "batch", cpuSecondsTotal: 3.5, peakMemoryMb: 1234.5);
    s.LatencyP50Ms.ShouldBe(800);
    s.LatencyMaxMs.ShouldBe(1200);
    s.CpuSecondsTotal.ShouldBe(3.5);
    s.PeakMemoryMb.ShouldBe(1234.5);
    s.MeanRtf.ShouldBe(0.4);
}

[Fact]
public void Summarize_counts_unstable_transcripts()
{
    ClipResult Clip(string id, bool stable) => new(
        id, 5.0, false, true, "ref", "hyp", "hyp", 0.0, 0.0, null,
        new long[] { 100 }, false, 0, false, false, "", null,
        BatchMsRuns: new long[] { 100 }, CpuSeconds: 1, MeanRtf: 0.1, TranscriptStable: stable);
    var s = EvalResults.Summarize(new[] { Clip("a", true), Clip("b", false) });
    s.UnstableTranscriptCount.ShouldBe(1);
}

[Fact]
public void ToJson_marks_mode_language_passes_converged_and_trace()
{
    var info = new EvalRunInfo("corpus-v1", "qwen3-asr-1.7b", "0.1.3", "2026-07-27", 1,
        Mode: "batch", Language: null, Passes: 3, Converged: true);
    var clips = Array.Empty<ClipResult>();
    var summary = EvalResults.Summarize(clips, mode: "batch");
    var passes = new[] { new PassSummary(1, 500, 900, 1000, 12.3, 2048.0, 0.4, 0.15, 0) };
    var trace = new[] { new ConvergencePoint(1, 500, 20, 0.04, true) };
    var json = EvalResults.ToJson(info, clips, summary, passes, trace);
    json.ShouldContain("\"mode\": \"batch\"");
    json.ShouldContain("\"passes\"");
    json.ShouldContain("\"converged\": true");
    json.ShouldContain("\"convergenceTrace\"");
    json.ShouldContain("\"ciHalfWidthMs\"");
    json.ShouldContain("\"resourceNote\"");
    json.ShouldContain("\"cpuSeconds\"");
    json.ShouldContain("\"peakMemoryMb\"");
}

[Fact]
public void ToJson_streaming_mode_marks_streaming_and_language()
{
    var info = new EvalRunInfo("corpus-v1", "nemotron-3.5-asr-streaming-0.6b", "0.1.3", "2026-07-27", 1,
        Mode: "streaming", Language: "en-US");
    var json = EvalResults.ToJson(info, Array.Empty<ClipResult>(), EvalResults.Summarize(Array.Empty<ClipResult>()));
    json.ShouldContain("\"mode\": \"streaming\"");
    json.ShouldContain("\"language\": \"en-US\"");
}

[Fact]
public void ToJson_serializes_infinite_ratio_without_throwing()
{
    var trace = new[] { new ConvergencePoint(1, 0, 0, double.PositiveInfinity, false) };
    var json = EvalResults.ToJson(
        new EvalRunInfo("c", "m", "0.1.3", "2026-07-27", 1),
        Array.Empty<ClipResult>(), EvalResults.Summarize(Array.Empty<ClipResult>()),
        Array.Empty<PassSummary>(), trace);
    json.ShouldContain("\"Infinity\"");
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (new record parameters / overload / `ResourceNote` missing).

- [ ] **Step 3: Implement in `scripts/asr-latency-bench/EvalResults.cs`**

3a. Append the optional parameters to `ClipResult`, `EvalRunInfo`, `EvalSummary`, `EvalReport` exactly as in the Interfaces block (existing parameters and order untouched; new ones appended with the defaults shown).

3b. Add the constant and make `JsonOpts` public with named-float handling (needed to serialize `RatioToMean = Infinity`):
```csharp
public const string ResourceNote =
    "resources are process CPU time and peak working set only; GPU/Vulkan usage is not separately measured. " +
    "RTF = processing time / audio duration (streaming: process CPU seconds; batch: batch transcribe wall time)";

public static readonly JsonSerializerOptions JsonOpts = new()
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
};
```
(Keep the field name `JsonOpts`; delete the old `private` modifier.)

3c. Extend `Summarize`:
```csharp
public static EvalSummary Summarize(IReadOnlyList<ClipResult> clips,
    string mode = "streaming", double cpuSecondsTotal = 0, double peakMemoryMb = 0)
{
    // ...existing wers/cers/silent computation unchanged...
    var latencySource = mode == "batch"
        ? clips.SelectMany(c => c.BatchMsRuns ?? Array.Empty<long>())
        : clips.SelectMany(c => c.FinishMsRuns);
    var latencies = latencySource.Where(ms => ms > 0)
        .Select(ms => (double)ms).OrderBy(v => v).ToArray();
    var rtfs = clips.Where(c => c.MeanRtf > 0).Select(c => c.MeanRtf).ToArray();
    return new EvalSummary(
        /* ...existing 13 arguments exactly as before, using the new `latencies`... */,
        CpuSecondsTotal: Math.Round(cpuSecondsTotal, 3),
        PeakMemoryMb: Math.Round(peakMemoryMb, 1),
        MeanRtf: rtfs.Length == 0 ? 0 : Math.Round(rtfs.Average(), 4),
        UnstableTranscriptCount: clips.Count(c => !c.TranscriptStable));
}
```

3d. Add the `ToJson` overload (keep the 3-arg one delegating):
```csharp
public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary)
    => JsonSerializer.Serialize(new EvalReport(info, summary, clips), JsonOpts);

public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary,
    IReadOnlyList<PassSummary> passes, IReadOnlyList<ConvergencePoint> convergenceTrace)
    => JsonSerializer.Serialize(new EvalReport(info, summary, clips, passes, convergenceTrace), JsonOpts);
```

3e. Extend `ToMarkdown` header bullets (before the clip table; still numbers only, no transcript text): add
```csharp
sb.AppendLine($"- mode: {info.Mode}{(info.Language is null ? "" : $" (language {info.Language})")}");
sb.AppendLine($"- passes: {info.Passes}, converged: {(info.Converged ? "yes" : "no")}");
sb.AppendLine($"- CPU: {summary.CpuSecondsTotal:F1} s total, peak memory: {summary.PeakMemoryMb:F0} MB, mean RTF: {summary.MeanRtf:F3}");
```
(match the file's existing StringBuilder variable name and bullet formatting).

- [ ] **Step 4: Run to verify pass — including ALL pre-existing EvalResults tests**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, `Errors: 0`, `Failed: 0`. The pre-existing pins (camelCase keys, `finishMsRuns`, `ShouldNotContain("secret")`, error-row rendering) must all still pass untouched.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalResults.cs tests/Winpepper.Asr.Tests/EvalResultsTests.cs
git commit -m "feat(bench): results schema - mode, language, passes, convergence trace, resource aggregates"
```

---

### Task 7: CLI validation + model-dir resolution

**Files:**
- Modify: `scripts/asr-latency-bench/BenchArgs.cs`
- Create: `scripts/asr-latency-bench/ModelDirLayout.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add link)
- Test: `tests/Winpepper.Asr.Tests/BenchArgsTests.cs`, `tests/Winpepper.Asr.Tests/ModelDirLayoutTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (Task 9 relies on these):
  ```csharp
  // BenchArgs additions (same pattern as ValidateRepeats: null = ok, string = error text)
  public static string? ValidateMaxClips(int maxClips);              // >= 0; 0 = all clips
  public static string? ValidateTimeBudgetMinutes(double minutes);   // >= 0; 0 = no budget
  public static string? ValidatePasses(int minPasses, int maxPasses);// min >= 1; max == 0 (unlimited) or >= min

  // ModelDirLayout
  public static class ModelDirLayout
  {
      public sealed record Resolved(string GgufPath, string RuntimeDir);
      public static Resolved Resolve(string modelDir); // throws InvalidOperationException with precise reason
  }
  ```
  `Resolve` accepts a dir containing exactly one `*.gguf` at its root plus either `runtime/transcribe.dll` (flat) or `runtime/<subdir>/transcribe.dll` (production mirror, e.g. `runtime/transcribe-native-windows-x86_64-cpu-vulkan/`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/BenchArgsTests.cs` (mirroring its existing Theory style for `ValidateRepeats`):
```csharp
[Theory]
[InlineData(0)]
[InlineData(5)]
public void ValidateMaxClips_accepts_zero_or_positive(int v)
    => BenchArgs.ValidateMaxClips(v).ShouldBeNull();

[Fact]
public void ValidateMaxClips_rejects_negative()
    => BenchArgs.ValidateMaxClips(-1).ShouldNotBeNull();

[Theory]
[InlineData(0.0)]
[InlineData(55.0)]
public void ValidateTimeBudgetMinutes_accepts_zero_or_positive(double v)
    => BenchArgs.ValidateTimeBudgetMinutes(v).ShouldBeNull();

[Fact]
public void ValidateTimeBudgetMinutes_rejects_negative()
    => BenchArgs.ValidateTimeBudgetMinutes(-0.1).ShouldNotBeNull();

[Theory]
[InlineData(1, 0)]   // unlimited max
[InlineData(2, 2)]
[InlineData(2, 10)]
public void ValidatePasses_accepts_valid_combinations(int min, int max)
    => BenchArgs.ValidatePasses(min, max).ShouldBeNull();

[Theory]
[InlineData(0, 0)]   // min must be >= 1
[InlineData(3, 2)]   // max below min
[InlineData(1, -1)]  // negative max
public void ValidatePasses_rejects_invalid_combinations(int min, int max)
    => BenchArgs.ValidatePasses(min, max).ShouldNotBeNull();
```

Create `tests/Winpepper.Asr.Tests/ModelDirLayoutTests.cs`:
```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class ModelDirLayoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("modeldir-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(string relative)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void Resolve_finds_gguf_and_nested_runtime_dir()
    {
        Touch("model.gguf");
        Touch(Path.Combine("runtime", "transcribe-native-windows-x86_64-cpu-vulkan", "transcribe.dll"));
        var r = ModelDirLayout.Resolve(_dir);
        r.GgufPath.ShouldBe(Path.Combine(_dir, "model.gguf"));
        r.RuntimeDir.ShouldBe(Path.Combine(_dir, "runtime", "transcribe-native-windows-x86_64-cpu-vulkan"));
    }

    [Fact]
    public void Resolve_accepts_flat_runtime_dir()
    {
        Touch("model.gguf");
        Touch(Path.Combine("runtime", "transcribe.dll"));
        ModelDirLayout.Resolve(_dir).RuntimeDir.ShouldBe(Path.Combine(_dir, "runtime"));
    }

    [Fact]
    public void Resolve_rejects_zero_or_multiple_ggufs()
    {
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("exactly one");
        Touch("a.gguf");
        Touch("b.gguf");
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("exactly one");
    }

    [Fact]
    public void Resolve_rejects_missing_runtime()
    {
        Touch("model.gguf");
        Should.Throw<InvalidOperationException>(() => ModelDirLayout.Resolve(_dir))
            .Message.ShouldContain("transcribe.dll");
    }
}
```

- [ ] **Step 2: Add link, run to verify failure**

Add to the csproj `<ItemGroup>`:
```xml
<Compile Include="..\..\scripts\asr-latency-bench\ModelDirLayout.cs" Link="Bench\ModelDirLayout.cs" />
```
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (missing file + missing validators).

- [ ] **Step 3: Implement**

Append to `scripts/asr-latency-bench/BenchArgs.cs` (same class, same style as `ValidateRepeats`):
```csharp
public static string? ValidateMaxClips(int maxClips)
    => maxClips >= 0 ? null : $"--max-clips must be >= 0 (0 = all clips), got {maxClips}";

public static string? ValidateTimeBudgetMinutes(double minutes)
    => minutes >= 0 ? null : $"--time-budget-minutes must be >= 0 (0 = no budget), got {minutes}";

public static string? ValidatePasses(int minPasses, int maxPasses)
{
    if (minPasses < 1) return $"--min-passes must be >= 1, got {minPasses}";
    if (maxPasses < 0) return $"--max-passes must be >= 0 (0 = unlimited), got {maxPasses}";
    if (maxPasses != 0 && maxPasses < minPasses)
        return $"--max-passes ({maxPasses}) must be 0 or >= --min-passes ({minPasses})";
    return null;
}
```

Create `scripts/asr-latency-bench/ModelDirLayout.cs`:
```csharp
namespace AsrLatencyBench;

/// <summary>Resolves a per-model eval directory that mirrors the production
/// model layout: exactly one *.gguf at the root, plus a runtime dir holding
/// transcribe.dll — either runtime/transcribe.dll (flat) or
/// runtime/&lt;tarball-top-dir&gt;/transcribe.dll (production mirror).</summary>
public static class ModelDirLayout
{
    public sealed record Resolved(string GgufPath, string RuntimeDir);

    public static Resolved Resolve(string modelDir)
    {
        if (!Directory.Exists(modelDir))
            throw new InvalidOperationException($"model dir not found: {modelDir}");
        var ggufs = Directory.GetFiles(modelDir, "*.gguf");
        if (ggufs.Length != 1)
            throw new InvalidOperationException(
                $"model dir must contain exactly one .gguf at its root, found {ggufs.Length}: {modelDir}");
        var runtimeRoot = Path.Combine(modelDir, "runtime");
        string? runtimeDir = null;
        if (File.Exists(Path.Combine(runtimeRoot, "transcribe.dll")))
            runtimeDir = runtimeRoot;
        else if (Directory.Exists(runtimeRoot))
            runtimeDir = Directory.GetDirectories(runtimeRoot)
                .OrderBy(d => d, StringComparer.Ordinal)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "transcribe.dll")));
        if (runtimeDir is null)
            throw new InvalidOperationException(
                $"no transcribe.dll under {runtimeRoot} (expected runtime/transcribe.dll or runtime/<dir>/transcribe.dll)");
        return new Resolved(ggufs[0], runtimeDir);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Same build + exec commands as before. Expected: PASS, `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/BenchArgs.cs scripts/asr-latency-bench/ModelDirLayout.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/BenchArgsTests.cs tests/Winpepper.Asr.Tests/ModelDirLayoutTests.cs
git commit -m "feat(bench): validators for max-clips/time-budget/passes and model-dir resolution"
```

---

### Task 8: Comparison aggregator (`EvalComparison.cs` + `compare` scenario)

**Files:**
- Create: `scripts/asr-latency-bench/EvalComparison.cs`
- Modify: `scripts/asr-latency-bench/Program.cs` (new `--results-root` flag + `compare` case)
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add link)
- Test: `tests/Winpepper.Asr.Tests/EvalComparisonTests.cs`

**Interfaces:**
- Consumes: `EvalReport`, `EvalRunInfo`, `EvalSummary`, `EvalResults.JsonOpts`, `EvalResults.ToJson` shapes (Task 6), `ConvergencePoint` (Task 3), `PassSummary` (Task 5).
- Produces:
  ```csharp
  namespace AsrLatencyBench;
  public sealed record ModelComparisonEntry(
      string Model, string Mode, string? Language, int Passes, bool Converged,
      int ClipCount, int ScoredCount, double? MeanWer, double? MedianWer, double? MeanCer,
      long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
      double CpuSecondsTotal, double PeakMemoryMb, double MeanRtf,
      int FallbackCount, int TruncatedCount, int FailedCount, int UnstableTranscriptCount,
      IReadOnlyList<ConvergencePoint> ConvergenceTrace,
      IReadOnlyList<PassSummary> PassSummaries);
  public sealed record ComparisonReport(
      string DateUtc, string Corpus, string ResourceNote,
      IReadOnlyList<ModelComparisonEntry> Models);
  public static class EvalComparison
  {
      public static EvalReport Parse(string resultsJson);           // JsonSerializer.Deserialize with EvalResults.JsonOpts
      public static ModelComparisonEntry FromReport(EvalReport report);
      public static ComparisonReport Build(IReadOnlyList<EvalReport> reports, string dateUtc);
      public static string ToJson(ComparisonReport report);         // EvalResults.JsonOpts; contains NO transcript text
  }
  ```
  CLI: `AsrLatencyBench.dll compare --results-root <dir> --out <dir>` finds every `results.json` under `<dir>` (recursive), writes `<out>/comparison.json`, exit 2 when none found.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/EvalComparisonTests.cs`:
```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public class EvalComparisonTests
{
    private static EvalReport MakeReport(string model, string mode, string? language, bool converged)
    {
        var info = new EvalRunInfo("corpus-v1", model, "0.1.3", "2026-07-27", 1,
            Mode: mode, Language: language, Passes: 2, Converged: converged);
        var clips = new[]
        {
            new ClipResult("c1", 10.0, false, true, "synthetic reference", "synthetic hyp", "synthetic hyp",
                0.25, 0.10, null, new long[] { 400 }, false, 0, false, false, "", null,
                BatchMsRuns: new long[] { 900 }, CpuSeconds: 2.0, MeanRtf: 0.2, TranscriptStable: true),
        };
        var summary = EvalResults.Summarize(clips, mode, cpuSecondsTotal: 2.0, peakMemoryMb: 1500.0);
        return new EvalReport(info, summary, clips,
            new[] { new PassSummary(1, 400, 400, 400, 1.0, 1400.0, 0.2, 0.25, 0),
                    new PassSummary(2, 400, 400, 400, 1.0, 1500.0, 0.2, 0.25, 0) },
            new[] { new ConvergencePoint(1, 400, 10, 0.025, true),
                    new ConvergencePoint(2, 400, 10, 0.025, true) });
    }

    [Fact]
    public void Roundtrips_results_json_through_Parse()
    {
        var report = MakeReport("nemotron-streaming-en", "streaming", null, true);
        var json = EvalResults.ToJson(report.Info, report.Clips, report.Summary,
            report.Passes!, report.ConvergenceTrace!);
        var parsed = EvalComparison.Parse(json);
        parsed.Info.SpeechModel.ShouldBe("nemotron-streaming-en");
        parsed.Info.Mode.ShouldBe("streaming");
        parsed.Clips.Count.ShouldBe(1);
        parsed.Passes!.Count.ShouldBe(2);
        parsed.ConvergenceTrace!.Count.ShouldBe(2);
    }

    [Fact]
    public void Build_aligns_models_and_carries_key_numbers()
    {
        var reports = new[]
        {
            MakeReport("qwen3-asr-1.7b", "batch", null, false),
            MakeReport("nemotron-3.5-asr-streaming-0.6b", "streaming", "en-US", true),
        };
        var c = EvalComparison.Build(reports, "2026-07-27");
        c.Corpus.ShouldBe("corpus-v1");
        c.Models.Count.ShouldBe(2);
        c.Models.Select(m => m.Model).ShouldBe(new[]
            { "nemotron-3.5-asr-streaming-0.6b", "qwen3-asr-1.7b" }); // sorted by model name
        var nem = c.Models[0];
        nem.Mode.ShouldBe("streaming");
        nem.Language.ShouldBe("en-US");
        nem.Converged.ShouldBeTrue();
        nem.MeanWer.ShouldBe(0.25);
        nem.LatencyP50Ms.ShouldBe(400);
        nem.CpuSecondsTotal.ShouldBe(2.0);
        nem.PeakMemoryMb.ShouldBe(1500.0);
        nem.ConvergenceTrace.Count.ShouldBe(2);
        var qwen = c.Models[1];
        qwen.Mode.ShouldBe("batch");
        qwen.LatencyP50Ms.ShouldBe(900); // batch mode pools batch times
    }

    [Fact]
    public void ToJson_contains_no_transcript_text()
    {
        var c = EvalComparison.Build(new[] { MakeReport("m", "streaming", null, false) }, "2026-07-27");
        var json = EvalComparison.ToJson(c);
        json.ShouldNotContain("synthetic reference");
        json.ShouldNotContain("synthetic hyp");
        json.ShouldContain("\"models\"");
        json.ShouldContain("\"converged\"");
    }
}
```

- [ ] **Step 2: Add link, run to verify failure**

Add to the csproj `<ItemGroup>`:
```xml
<Compile Include="..\..\scripts\asr-latency-bench\EvalComparison.cs" Link="Bench\EvalComparison.cs" />
```
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (file missing).

- [ ] **Step 3: Implement `scripts/asr-latency-bench/EvalComparison.cs`**

```csharp
using System.Text.Json;

namespace AsrLatencyBench;

/// <summary>One model's row in the cross-model comparison. Numbers only —
/// no transcript or reference text ever lands in comparison.json.</summary>
public sealed record ModelComparisonEntry(
    string Model, string Mode, string? Language, int Passes, bool Converged,
    int ClipCount, int ScoredCount, double? MeanWer, double? MedianWer, double? MeanCer,
    long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs,
    double CpuSecondsTotal, double PeakMemoryMb, double MeanRtf,
    int FallbackCount, int TruncatedCount, int FailedCount, int UnstableTranscriptCount,
    IReadOnlyList<ConvergencePoint> ConvergenceTrace,
    IReadOnlyList<PassSummary> PassSummaries);

public sealed record ComparisonReport(
    string DateUtc, string Corpus, string ResourceNote,
    IReadOnlyList<ModelComparisonEntry> Models);

public static class EvalComparison
{
    public static EvalReport Parse(string resultsJson)
        => JsonSerializer.Deserialize<EvalReport>(resultsJson, EvalResults.JsonOpts)
           ?? throw new InvalidOperationException("results.json parsed to null");

    public static ModelComparisonEntry FromReport(EvalReport r) => new(
        Model: r.Info.SpeechModel,
        Mode: r.Info.Mode,
        Language: r.Info.Language,
        Passes: r.Info.Passes,
        Converged: r.Info.Converged,
        ClipCount: r.Summary.ClipCount,
        ScoredCount: r.Summary.ScoredCount,
        MeanWer: r.Summary.MeanWer,
        MedianWer: r.Summary.MedianWer,
        MeanCer: r.Summary.MeanCer,
        LatencyP50Ms: r.Summary.LatencyP50Ms,
        LatencyP90Ms: r.Summary.LatencyP90Ms,
        LatencyMaxMs: r.Summary.LatencyMaxMs,
        CpuSecondsTotal: r.Summary.CpuSecondsTotal,
        PeakMemoryMb: r.Summary.PeakMemoryMb,
        MeanRtf: r.Summary.MeanRtf,
        FallbackCount: r.Summary.FallbackCount,
        TruncatedCount: r.Summary.TruncatedCount,
        FailedCount: r.Summary.FailedCount,
        UnstableTranscriptCount: r.Summary.UnstableTranscriptCount,
        ConvergenceTrace: r.ConvergenceTrace ?? Array.Empty<ConvergencePoint>(),
        PassSummaries: r.Passes ?? Array.Empty<PassSummary>());

    public static ComparisonReport Build(IReadOnlyList<EvalReport> reports, string dateUtc)
    {
        var corpora = reports.Select(r => r.Info.Corpus).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        return new ComparisonReport(
            DateUtc: dateUtc,
            Corpus: string.Join("+", corpora),
            ResourceNote: EvalResults.ResourceNote,
            Models: reports.Select(FromReport)
                .OrderBy(m => m.Model, StringComparer.Ordinal).ToArray());
    }

    public static string ToJson(ComparisonReport report)
        => JsonSerializer.Serialize(report, EvalResults.JsonOpts);
}
```

- [ ] **Step 4: Run unit tests to verify pass**

Same build + exec commands as before. Expected: PASS, `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Wire the `compare` scenario into `Program.cs`**

5a. In the arg parse loop (`Program.cs:33-45`), add alongside the other cases:
```csharp
case "--results-root": resultsRoot = args[++argIdx]; break;
```
and declare with the other option defaults near the top: `string? resultsRoot = null;`

5b. In the scenario `switch`, add before the `default` case:
```csharp
case "compare":
{
    if (resultsRoot is null)
    {
        Console.WriteLine("compare: SKIPPED (--results-root not set)");
        break;
    }
    var files = Directory.Exists(resultsRoot)
        ? Directory.GetFiles(resultsRoot, "results.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToArray()
        : Array.Empty<string>();
    if (files.Length == 0)
    {
        Console.Error.WriteLine($"compare: no results.json found under {resultsRoot}");
        Environment.ExitCode = 2;
        break;
    }
    var reports = files.Select(f => EvalComparison.Parse(File.ReadAllText(f))).ToList();
    var comparison = EvalComparison.Build(reports, DateTime.UtcNow.ToString("yyyy-MM-dd"));
    Directory.CreateDirectory(outDir);
    var comparisonPath = Path.Combine(outDir, "comparison.json");
    File.WriteAllText(comparisonPath, EvalComparison.ToJson(comparison));
    Console.WriteLine($"compare: wrote {comparisonPath} ({reports.Count} models: {string.Join(", ", comparison.Models.Select(m => m.Model))})");
    break;
}
```

- [ ] **Step 6: Compile check + full suite + commit**

```bash
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release -p:EnableWindowsTargeting=true
rm -rf scripts/asr-latency-bench/bin scripts/asr-latency-bench/obj
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalComparison.cs scripts/asr-latency-bench/Program.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/EvalComparisonTests.cs
git commit -m "feat(bench): compare scenario - aggregate per-model results.json into comparison.json"
```

---

### Task 9: Corpus mode: model parameterization, batch-only, pass loop, resources, convergence

**Files:**
- Modify: `scripts/asr-latency-bench/Program.cs` (arg parsing + the `corpus` case, `Program.cs:302-476`)

**Interfaces:**
- Consumes: everything from Tasks 2–7:
  `ModelDirLayout.Resolve(string) -> Resolved(GgufPath, RuntimeDir)`;
  `BenchArgs.ValidateMaxClips/ValidateTimeBudgetMinutes/ValidatePasses`;
  `engine.TranscribeBatch(float[], string?)`, `new NemotronStreamingTranscriber(engineProvider, batchFallback, modelName, log, attContextRight: 13, language: language)`;
  `ResourceUsage.Capture/CpuDelta/Rtf/ToMb`; `EvalPasses.Summarize(...) -> PassSummary`;
  `Convergence.Median/Evaluate/Converged`; `EvalResults.Summarize(clips, mode, cpuTotal, peakMb)` and 5-arg `ToJson`.
- Produces: the corpus CLI contract the driver (Task 11) invokes:
  ```
  AsrLatencyBench.dll corpus
      --corpus <dir>
      (--model-dir <dir> | --nemotron-model <gguf> --nemotron-runtime <dir>)
      [--model-name <name>] [--language <code>] [--batch-only]
      [--max-clips N] [--time-budget-minutes N] [--min-passes N] [--max-passes N]
      [--out <dir>]
  ```
  Defaults: `--model-name` = gguf filename sans extension; `--time-budget-minutes 55`; `--min-passes 2`; `--max-passes 0` (unlimited); `--max-clips 0` (all). Stop rule after each pass: converged (needs `passes >= min-passes` and two consecutive precise points) OR `max-passes` reached OR time budget spent. Back-compat: `--repeats N` (N>1) with no explicit `--max-passes` acts as `--max-passes N`; the per-clip inner repeat loop is REMOVED (each clip runs once per pass; `info.Repeats` now records the pass count actually completed).

- [ ] **Step 1: Add the new options to the parse loop and validate them**

In the parse loop (`Program.cs:33-45`) add:
```csharp
case "--model-name": modelNameArg = args[++argIdx]; break;
case "--language": language = args[++argIdx]; break;
case "--batch-only": batchOnly = true; break;
case "--max-clips": maxClips = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
case "--time-budget-minutes": timeBudgetMinutes = double.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
case "--min-passes": minPasses = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
case "--max-passes": maxPasses = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); maxPassesSet = true; break;
```
Declarations with the other defaults near the top of `Program.cs`:
```csharp
string? modelNameArg = null;
string? language = null;
var batchOnly = false;
var maxClips = 0;
var timeBudgetMinutes = 55.0;
var minPasses = 2;
var maxPasses = 0;
var maxPassesSet = false;
```
After the existing `--repeats` validation gate (`Program.cs:47-55`), add identical-shaped gates (each prints the error and sets `Environment.ExitCode = 2; return;`):
```csharp
if (BenchArgs.ValidateMaxClips(maxClips) is { } maxClipsError) { Console.Error.WriteLine(maxClipsError); Environment.ExitCode = 2; return; }
if (BenchArgs.ValidateTimeBudgetMinutes(timeBudgetMinutes) is { } budgetError) { Console.Error.WriteLine(budgetError); Environment.ExitCode = 2; return; }
if (BenchArgs.ValidatePasses(minPasses, maxPasses) is { } passesError) { Console.Error.WriteLine(passesError); Environment.ExitCode = 2; return; }
if (!maxPassesSet && repeats > 1) maxPasses = repeats;   // back-compat: old drivers pass --repeats N
```

- [ ] **Step 2: Restructure the `corpus` case**

Replace the body of `case "corpus":` (`Program.cs:302-476`) with the structure below. Reuse the existing pieces verbatim where noted — the WAV/reference loading, silence trim, framing/pacing loop, WER/CER block, and the two-engine comment block all already exist in the current body; they move inside the new loops unchanged.

```csharp
case "corpus":
{
    // ---- resolve model inputs: --model-dir wins over --nemotron-model/--nemotron-runtime
    var corpusGguf = nemotronModel;
    var corpusRuntime = nemotronRuntime;
    if (modelDir is not null && corpusDir is not null && (nemotronModel is null || nemotronRuntime is null))
    {
        var resolved = ModelDirLayout.Resolve(modelDir);
        corpusGguf = resolved.GgufPath;
        corpusRuntime = resolved.RuntimeDir;
    }
    if (corpusDir is null || corpusGguf is null || corpusRuntime is null)
    { Console.WriteLine("corpus: SKIPPED (need --corpus plus --model-dir or --nemotron-model/--nemotron-runtime)"); break; }
    // keep the existing manifest.json / model file / transcribe.dll existence checks, using corpusGguf/corpusRuntime

    var modelName = modelNameArg ?? Path.GetFileNameWithoutExtension(corpusGguf);
    var mode = batchOnly ? "batch" : "streaming";
    Console.WriteLine($"# corpus: model={modelName} mode={mode} language={language ?? "(none)"} " +
        $"maxClips={maxClips} minPasses={minPasses} maxPasses={maxPasses} timeBudgetMinutes={timeBudgetMinutes}");

    using var corpusEngine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
        corpusRuntime, corpusGguf, msg => Console.WriteLine($"# nem-log: {msg}"));
    // Streaming mode needs the SECOND engine as the fallback (compute-gate deadlock
    // otherwise -- keep the existing explanatory comment block verbatim).
    // Batch-only mode never opens a stream, so ONE engine suffices (saves ~1 model of RAM).
    using var fallbackEngine = batchOnly ? null : Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
        corpusRuntime, corpusGguf, msg => Console.WriteLine($"# nem-fallback-log: {msg}"));

    var manifest = AsrEvalCorpus.CorpusManifest.Load(manifestPath);
    var entries = manifest.Entries.Where(e => !e.Exclude).ToList();
    if (maxClips > 0) entries = entries.Take(maxClips).ToList();

    // ---- per-clip accumulators, pooled across passes
    var tallies = entries.ToDictionary(e => e.Id, e => new ClipTally());
    var passSummaries = new List<PassSummary>();
    var trace = new List<ConvergencePoint>();
    var runClock = System.Diagnostics.Stopwatch.StartNew();
    var pass = 0;
    var converged = false;

    while (true)
    {
        pass++;
        var passLatencies = new List<double>();   // this pass's speed metric samples (> 0)
        var passRtfs = new List<double>();
        var passWers = new List<double>();
        var passFailed = 0;
        var passStart = ResourceUsage.Capture();

        foreach (var entry in entries)
        {
            var tally = tallies[entry.Id];
            if (tally.Error is not null) continue;   // a clip that failed once stays failed
            try
            {
                // ---- existing audio + reference loading, unchanged:
                // wavAudio, refPath, hasReference, referenceText
                var before = ResourceUsage.Capture();

                // (a) whole-file batch transcribe (parity reference in streaming mode;
                //     THE speed metric in batch mode). Untrimmed audio, as today.
                var swBatch = System.Diagnostics.Stopwatch.StartNew();
                var batchText = corpusEngine.TranscribeBatch(wavAudio, language);
                swBatch.Stop();
                tally.BatchMs.Add(swBatch.ElapsedMilliseconds);

                var runText = batchText;
                long finishMs = 0;
                var runFellBack = false;
                var runTruncated = false;
                if (!batchOnly)
                {
                    // ---- existing streaming replay, unchanged except:
                    //  * NemotronStreamingTranscriber gets (…, modelName, nemLog,
                    //    attContextRight: 13, language: language) and the fallback probe
                    //    wraps an EngineBatchTranscriber over fallbackEngine as today
                    //  * the trim/framing/pacing/FinishAsync code moves here verbatim
                    //  * finishMs, runText (stream text), runFellBack, runTruncated set as today
                    if (finishMs > 0) tally.FinishMs.Add(finishMs);
                }

                var after = ResourceUsage.Capture();
                var cpu = ResourceUsage.CpuDelta(before, after);
                tally.CpuSeconds += cpu;
                var audioSeconds = wavAudio.Length / 16000.0;
                // RTF: batch = transcribe wall time / audio; streaming = process CPU s / audio
                // (streaming wall time is real-time paced, so CPU is the honest processing cost).
                var rtf = batchOnly
                    ? ResourceUsage.Rtf(swBatch.Elapsed.TotalSeconds, audioSeconds)
                    : ResourceUsage.Rtf(cpu, audioSeconds);
                tally.Rtfs.Add(rtf);
                passRtfs.Add(rtf);
                var speedMs = batchOnly ? swBatch.ElapsedMilliseconds : finishMs;
                if (speedMs > 0) passLatencies.Add(speedMs);

                if (pass == 1)
                {
                    tally.FirstPass(entry, wavAudio, hasReference, referenceText, batchText, runText);
                    // WER/CER/silentPass on the SCORED text (batch mode scores batchText,
                    // streaming scores stream text) -- the existing metric block, with
                    // streamText replaced by tally.ScoredText
                }
                else if (!string.Equals(batchOnly ? batchText : runText, tally.ScoredText, StringComparison.Ordinal))
                {
                    tally.TranscriptStable = false;   // deterministic decode should make this impossible
                    Console.Error.WriteLine($"# corpus[{entry.Id}]: transcript changed on pass {pass} -- decode not deterministic?");
                }
                if (tally.Wer is not null) passWers.Add(tally.Wer.Value);
                tally.FellBack |= runFellBack;
                if (runFellBack) tally.FellBackCount++;
                tally.Truncated |= runTruncated;
            }
            catch (Exception ex)
            {
                tally.Error = $"{ex.GetType().Name}: {ex.Message}";
                passFailed++;
                Console.Error.WriteLine($"corpus[{entry.Id}] ERROR: {tally.Error}");
            }
        }

        var passEnd = ResourceUsage.Capture();
        passSummaries.Add(EvalPasses.Summarize(pass, passLatencies, passRtfs, passWers,
            ResourceUsage.CpuDelta(passStart, passEnd), passEnd.PeakWorkingSetBytes,
            tallies.Values.Count(t => t.Error is not null)));

        // convergence over pooled per-clip medians of the mode's speed metric
        var medians = tallies.Values
            .Select(t => batchOnly ? t.BatchMs : t.FinishMs)
            .Where(samples => samples.Count > 0)
            .Select(samples => Convergence.Median(samples.Select(ms => (double)ms).ToArray()))
            .ToArray();
        trace.Add(Convergence.Evaluate(pass, medians));
        converged = pass >= minPasses && Convergence.Converged(trace);
        Console.WriteLine($"# corpus: pass {pass} done -- mean {trace[^1].MeanMs:F0} ms, " +
            $"CI half-width {trace[^1].CiHalfWidthMs:F1} ms ({trace[^1].RatioToMean:P1}), precise={trace[^1].Precise}, converged={converged}");

        if (converged) break;
        if (maxPasses > 0 && pass >= maxPasses) break;
        if (timeBudgetMinutes > 0 && runClock.Elapsed >= TimeSpan.FromMinutes(timeBudgetMinutes)) break;
    }

    // ---- build ClipResults from tallies (order = entries order)
    var clipResults = entries.Select(e =>
    {
        var t = tallies[e.Id];
        if (t.Error is not null && t.FinishMs.Count == 0 && t.BatchMs.Count == 0)
            return new ClipResult(e.Id, 0, e.ExpectedSilent, false, "", "", "", null, null, null,
                Array.Empty<long>(), false, 0, false, false, "", t.Error);
        return new ClipResult(
            e.Id, t.AudioSeconds, e.ExpectedSilent, t.HasReference,
            t.Reference, t.StreamText, t.BatchText,
            t.Wer, t.Cer, t.SilentPass,
            t.FinishMs, t.FellBack, t.FellBackCount, t.Truncated, t.TrimmedSilent,
            t.ParityDiff, t.Error,
            BatchMsRuns: t.BatchMs,
            CpuSeconds: Math.Round(t.CpuSeconds, 3),
            MeanRtf: t.Rtfs.Count == 0 ? 0 : Math.Round(t.Rtfs.Average(), 4),
            TranscriptStable: t.TranscriptStable);
    }).ToList();

    var totalCpu = clipResults.Sum(c => c.CpuSeconds);
    var peakMb = ResourceUsage.ToMb(ResourceUsage.Capture().PeakWorkingSetBytes);
    var runInfo = new EvalRunInfo(
        Path.GetFileName(Path.TrimEndingDirectorySeparator(corpusDir)),
        modelName,
        Winpepper.Asr.TranscribeCpp.TranscribeCppContract.RequiredVersion,
        DateTime.UtcNow.ToString("yyyy-MM-dd"),
        pass,                             // Repeats now records completed passes
        Mode: mode, Language: language, Passes: pass, Converged: converged);
    var evalSummary = EvalResults.Summarize(clipResults, mode, totalCpu, peakMb);
    if (evalSummary.UnstableTranscriptCount > 0)
        Console.Error.WriteLine($"corpus: WARNING {evalSummary.UnstableTranscriptCount} clip(s) changed transcript across passes (expected deterministic decode)");
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "results.json"),
        EvalResults.ToJson(runInfo, clipResults, evalSummary, passSummaries, trace));
    var resultsMd = EvalResults.ToMarkdown(runInfo, clipResults, evalSummary);
    File.WriteAllText(Path.Combine(outDir, "results.md"), resultsMd);
    Console.WriteLine(); Console.WriteLine(resultsMd);
    if (evalSummary.FailedCount > 0)
    { Console.Error.WriteLine($"corpus: {evalSummary.FailedCount} clip(s) FAILED"); Environment.ExitCode = 1; }
    break;
}
```
Add the bench-local accumulator with the other `sealed class` helpers at the bottom of `Program.cs`:
```csharp
sealed class ClipTally
{
    public double AudioSeconds; public bool HasReference; public bool TrimmedSilent;
    public string Reference = ""; public string StreamText = ""; public string BatchText = "";
    public string ScoredText = "";                    // pass-1 transcript of the scored mode
    public double? Wer; public double? Cer; public bool? SilentPass;
    public List<long> FinishMs = new(); public List<long> BatchMs = new();
    public double CpuSeconds; public List<double> Rtfs = new();
    public bool FellBack; public int FellBackCount; public bool Truncated;
    public bool TranscriptStable = true; public string? Error; public string ParityDiff = "";
}
```
Implementation notes (bind these decisions):
- `ClipTally.FirstPass(...)` in the sketch is shorthand — inline the assignments (`AudioSeconds`, `HasReference`, `Reference`, `BatchText`, `StreamText`, `ScoredText = batchOnly ? batchText : runText`, `TrimmedSilent`, `ParityDiff`, and the WER/CER/SilentPass block) directly at the `pass == 1` site rather than adding a method, matching the current inline style.
- The pass-summary `failedCount` is simply `tallies.Values.Count(t => t.Error is not null)` at pass end — drop the `CountedFailedBeforeThisPass` shorthand from the sketch.
- In batch-only mode there is no `parityDiff` (no stream text): set `ParityDiff = ""` and `StreamText = ""`.
- In streaming mode the scored text and metrics stay EXACTLY as today (scored on stream text from pass 1; parity diff batch-vs-stream from pass 1).
- Keep the per-clip `try/catch` so one bad clip never kills the run; a clip that errors is skipped on later passes.
- `NemotronStreamingTranscriber` construction becomes:
  ```csharp
  var streaming = new NemotronStreamingTranscriber(
      () => corpusEngine, probe, modelName, nemLog, attContextRight: 13, language: language);
  ```
  (model name literal `"nemotron-streaming-en"` replaced by the `modelName` variable).

- [ ] **Step 3: Compile check on Linux (the only automated gate for `Program.cs`)**

```bash
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release -p:EnableWindowsTargeting=true
rm -rf scripts/asr-latency-bench/bin scripts/asr-latency-bench/obj
```
Expected: 0 errors.

- [ ] **Step 4: Full suite (all BCL logic it wires is already unit-tested)**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 5: Commit**

```bash
git add scripts/asr-latency-bench/Program.cs
git commit -m "feat(bench): corpus mode - model parameterization, batch-only, pass loop with convergence and resource capture"
```

---

### Task 10: Model staging script (`setup-asr-eval-models.sh`)

**Files:**
- Create: `scripts/setup-asr-eval-models.sh` (mode 755)

**Interfaces:**
- Consumes: downloaded GGUFs at `/mnt/c/Users/dan/winpepper-evals/models/`; production runtime at `/mnt/c/Users/dan/AppData/Local/winpepper/models/nemotron-streaming-en/runtime/transcribe-native-windows-x86_64-cpu-vulkan/` (READ from only).
- Produces: per-model dirs consumable by `ModelDirLayout.Resolve`:
  ```
  /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b/
      nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf
      runtime/transcribe-native-windows-x86_64-cpu-vulkan/{transcribe.dll, contract.json, ggml-*.dll, ...}
  /mnt/c/Users/dan/winpepper-evals/models/qwen3-asr-1.7b/            (only when download complete)
      Qwen3-ASR-1.7B-Q8_0.gguf
      runtime/transcribe-native-windows-x86_64-cpu-vulkan/...
  ```
  Exit 0 always when nemotron-3.5 staged; prints a loud clear `SKIPPED (incomplete download: <actual> of 2185030624 bytes)` line for Qwen when its size mismatches and still exits 0 (partial staging is expected and recorded).

- [ ] **Step 1: Write the script**

Create `scripts/setup-asr-eval-models.sh`:
```bash
#!/usr/bin/env bash
# Stage per-model eval directories under C:\Users\dan\winpepper-evals\models\<model-name>\
# mirroring the production layout (one gguf at the root + runtime/<tarball-dir>/ DLLs) so
# the bench's --model-dir can consume them.
#
# Host safety: READS the production runtime under %LOCALAPPDATA%\winpepper\models
# (never writes there); writes only under C:\Users\dan\winpepper-evals\models\.
# Idempotent: skips work that is already done.
#
# Usage: ./scripts/setup-asr-eval-models.sh
set -euo pipefail

EVAL_MODELS=/mnt/c/Users/dan/winpepper-evals/models
PROD_RUNTIME="/mnt/c/Users/dan/AppData/Local/winpepper/models/nemotron-streaming-en/runtime/transcribe-native-windows-x86_64-cpu-vulkan"
RUNTIME_SUBDIR="runtime/transcribe-native-windows-x86_64-cpu-vulkan"
QWEN_EXPECTED_BYTES=2185030624

[[ -f "$PROD_RUNTIME/transcribe.dll" ]] || {
  echo "setup-asr-eval-models: production runtime not found at $PROD_RUNTIME" >&2; exit 2; }

stage_runtime() { # stage_runtime <model-dir>
  local dst="$1/$RUNTIME_SUBDIR"
  if [[ -f "$dst/transcribe.dll" ]]; then echo "  runtime already staged: $dst"; return 0; fi
  mkdir -p "$dst"
  cp -r "$PROD_RUNTIME/." "$dst/"
  echo "  runtime copied FROM production (read-only source) -> $dst"
}

stage_model() { # stage_model <model-name> <source-gguf-filename>
  local name="$1" gguf="$2"
  local src="$EVAL_MODELS/$gguf" dir="$EVAL_MODELS/$name"
  echo "== $name =="
  [[ -f "$src" || -f "$dir/$gguf" ]] || { echo "  SKIPPED (gguf not found: $src)"; return 0; }
  mkdir -p "$dir"
  if [[ ! -f "$dir/$gguf" ]]; then
    cp "$src" "$dir/$gguf"
    echo "  gguf copied -> $dir/$gguf"
  else
    echo "  gguf already staged: $dir/$gguf"
  fi
  stage_runtime "$dir"
}

# 1) nemotron-3.5 (streaming candidate) -- download known complete
stage_model "nemotron-3.5-asr-streaming-0.6b" "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf"

# 2) Qwen3-ASR (batch-only candidate) -- verify the download is complete first
QWEN_SRC="$EVAL_MODELS/Qwen3-ASR-1.7B-Q8_0.gguf"
QWEN_DIR="$EVAL_MODELS/qwen3-asr-1.7b"
echo "== qwen3-asr-1.7b =="
qwen_size=0
[[ -f "$QWEN_DIR/Qwen3-ASR-1.7B-Q8_0.gguf" ]] && qwen_size=$(stat -c%s "$QWEN_DIR/Qwen3-ASR-1.7B-Q8_0.gguf")
[[ "$qwen_size" -ne "$QWEN_EXPECTED_BYTES" && -f "$QWEN_SRC" ]] && qwen_size=$(stat -c%s "$QWEN_SRC")
if [[ "$qwen_size" -eq "$QWEN_EXPECTED_BYTES" ]]; then
  stage_model "qwen3-asr-1.7b" "Qwen3-ASR-1.7B-Q8_0.gguf"
else
  echo "  SKIPPED (incomplete download: ${qwen_size} of ${QWEN_EXPECTED_BYTES} bytes) -- rerun when complete"
fi

echo "setup-asr-eval-models: done"
```

- [ ] **Step 2: Run it and verify the staged layout**

```bash
chmod +x scripts/setup-asr-eval-models.sh
./scripts/setup-asr-eval-models.sh
ls /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b/
ls /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b/runtime/transcribe-native-windows-x86_64-cpu-vulkan/ | head
```
Expected: nemotron-3.5 dir contains the gguf + a runtime dir with `transcribe.dll`, `contract.json`, `ggml-*.dll`. Qwen: staged the same way if the size check passed (it was `2185030624` at plan time), otherwise the loud SKIPPED line. Verify nothing was written under `/mnt/c/Users/dan/AppData/Local/winpepper` (`ls -la` timestamps unchanged).

- [ ] **Step 3: Full suite (unchanged code, cheap insurance) + commit**

```bash
./scripts/linux-tests.sh
git add scripts/setup-asr-eval-models.sh
git commit -m "feat(scripts): stage per-model ASR eval dirs mirroring the production layout"
```

---

### Task 11: Driver script (`run-asr-model-eval-windows.sh`)

**Files:**
- Create: `scripts/run-asr-model-eval-windows.sh` (mode 755)

**Interfaces:**
- Consumes: the bench corpus CLI (Task 9), the staged model dirs (Task 10), and the existing WSL→Windows plumbing conventions of `scripts/run-asr-eval-windows.sh` (pre-clean, UNC build, %TEMP% staging, `ps_run`, results.json loud-fail guard — copy those blocks; the existing script stays untouched).
- Produces:
  ```
  ./scripts/run-asr-model-eval-windows.sh <corpus-dir-wsl> --model-dir <wsl-dir> --model-name <name> \
      [--language <code>] [--batch-only] [--time-budget-minutes N] [--min-passes N] [--max-passes N] [--max-clips N]
  ```
  Results + logs land in `artifacts/asr-eval/<model-name>/` (`results.json`, `results.md`, `build.log`, `stage.log`, `corpus.log`) so serial runs of different models never overwrite each other. Missing `results.json` after the run ⇒ loud failure, exit 3 (same guard as the existing driver).

- [ ] **Step 1: Write the script**

Create `scripts/run-asr-model-eval-windows.sh` (structure copied from `scripts/run-asr-eval-windows.sh`; differences are the arg parsing, the per-model out dir, and the bench flags):
```bash
#!/usr/bin/env bash
# Multi-model ASR corpus eval driver: build the bench with the Windows dotnet over
# the \\wsl.localhost UNC path, stage to %TEMP%, run the corpus scenario for ONE
# model (one model per process -- transcribe.cpp pins one runtime dir per process),
# and collect results into artifacts/asr-eval/<model-name>/ so serial runs of
# different models never overwrite each other.
#
# Host safety: only host writes are %TEMP% staging/results dirs and NuGet restore.
# Reads (never writes) the corpus dir and the model dir.
#
# Usage: ./scripts/run-asr-model-eval-windows.sh <corpus-dir-wsl> --model-dir <wsl-dir> --model-name <name> \
#          [--language <code>] [--batch-only] [--time-budget-minutes N] [--min-passes N] [--max-passes N] [--max-clips N]
#   e.g. ./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
#          --model-dir /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b \
#          --model-name nemotron-3.5-asr-streaming-0.6b --language en-US --max-clips 5 --max-passes 1
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORPUS_WSL="${1:?usage: run-asr-model-eval-windows.sh <corpus-dir> --model-dir <dir> --model-name <name> [options]}"
shift

MODEL_DIR_WSL="" MODEL_NAME="" LANGUAGE="" BATCH_ONLY=0
TIME_BUDGET="" MIN_PASSES="" MAX_PASSES="" MAX_CLIPS=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model-dir) MODEL_DIR_WSL="$2"; shift 2 ;;
    --model-name) MODEL_NAME="$2"; shift 2 ;;
    --language) LANGUAGE="$2"; shift 2 ;;
    --batch-only) BATCH_ONLY=1; shift ;;
    --time-budget-minutes) TIME_BUDGET="$2"; shift 2 ;;
    --min-passes) MIN_PASSES="$2"; shift 2 ;;
    --max-passes) MAX_PASSES="$2"; shift 2 ;;
    --max-clips) MAX_CLIPS="$2"; shift 2 ;;
    *) echo "run-asr-model-eval-windows: unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -n "$MODEL_DIR_WSL" && -n "$MODEL_NAME" ]] || { echo "run-asr-model-eval-windows: --model-dir and --model-name are required" >&2; exit 2; }
[[ -f "$CORPUS_WSL/manifest.json" ]] || { echo "run-asr-model-eval-windows: no manifest.json in $CORPUS_WSL" >&2; exit 2; }
[[ -d "$MODEL_DIR_WSL" ]] || { echo "run-asr-model-eval-windows: model dir not found: $MODEL_DIR_WSL" >&2; exit 2; }

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-asr-model-eval-windows: powershell.exe not found at $PS" >&2; exit 2; }
UNC_ROOT="$(wslpath -w "$HERE")"
CORPUS_WIN="$(wslpath -w "$CORPUS_WSL")"
MODEL_DIR_WIN="$(wslpath -w "$MODEL_DIR_WSL")"
OUT="$HERE/artifacts/asr-eval/$MODEL_NAME"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Pre-clean cross-OS bin/obj (CS0006 guard) ==="
rm -rf "$HERE"/scripts/asr-latency-bench/bin "$HERE"/scripts/asr-latency-bench/obj \
       "$HERE"/src/*/bin "$HERE"/src/*/obj

echo "=== [2/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [3/4] Stage bench output to %TEMP%\\winpepper-asr-eval ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-asr-eval'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [4/4] Run the corpus eval for model $MODEL_NAME ==="
BENCH_FLAGS="--corpus '$CORPUS_WIN' --model-dir '$MODEL_DIR_WIN' --model-name '$MODEL_NAME'"
[[ -n "$LANGUAGE" ]]    && BENCH_FLAGS+=" --language '$LANGUAGE'"
[[ "$BATCH_ONLY" -eq 1 ]] && BENCH_FLAGS+=" --batch-only"
[[ -n "$TIME_BUDGET" ]] && BENCH_FLAGS+=" --time-budget-minutes $TIME_BUDGET"
[[ -n "$MIN_PASSES" ]]  && BENCH_FLAGS+=" --min-passes $MIN_PASSES"
[[ -n "$MAX_PASSES" ]]  && BENCH_FLAGS+=" --max-passes $MAX_PASSES"
[[ -n "$MAX_CLIPS" ]]   && BENCH_FLAGS+=" --max-clips $MAX_CLIPS"
corpus_status=0
ps_run 7200 "$OUT/corpus.log" "
  \$res = Join-Path \$env:TEMP 'winpepper-asr-eval-results-$MODEL_NAME'
  if (Test-Path \$res) { Remove-Item -Recurse -Force \$res }
  Set-Location (Join-Path \$env:TEMP 'winpepper-asr-eval')
  dotnet exec AsrLatencyBench.dll corpus $BENCH_FLAGS --out \$res" || corpus_status=$?

# Collect results (results.json contains transcript text -- artifacts/ is gitignored).
WIN_TEMP_WSL="$(wslpath "$("$PS" -NoProfile -Command 'Write-Output $env:TEMP' | tr -d '\r')")"
RESULTS_WSL="$WIN_TEMP_WSL/winpepper-asr-eval-results-$MODEL_NAME"
if [[ ! -f "$RESULTS_WSL/results.json" ]]; then
  echo "run-asr-model-eval-windows: FAILED -- expected $RESULTS_WSL/results.json but no results were produced." >&2
  echo "run-asr-model-eval-windows: run was skipped or died before writing results; check $OUT/corpus.log" >&2
  if [[ "$corpus_status" -ne 0 ]]; then exit "$corpus_status"; fi
  exit 3
fi
cp -r "$RESULTS_WSL/." "$OUT/"
if [[ "$corpus_status" -ne 0 ]]; then
  echo "run-asr-model-eval-windows: eval reported failed clips (exit $corpus_status) -- results still collected in $OUT" >&2
  exit "$corpus_status"
fi
echo "run-asr-model-eval-windows: done -- results in $OUT (results.md, results.json), logs alongside"
```

- [ ] **Step 2: Static verification**

```bash
chmod +x scripts/run-asr-model-eval-windows.sh
bash -n scripts/run-asr-model-eval-windows.sh
./scripts/run-asr-model-eval-windows.sh 2>&1 | head -2 || true
./scripts/run-asr-model-eval-windows.sh /tmp --model-dir /tmp 2>&1 | tail -1 || true
```
Expected: `bash -n` silent (syntax ok); first invocation prints the usage error; second prints the `--model-name`/manifest error path (exit 2). (The real end-to-end run is Task 12.)

- [ ] **Step 3: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/run-asr-model-eval-windows.sh
git commit -m "feat(scripts): per-model ASR eval driver writing to artifacts/asr-eval/<model-name>/"
```

---

### Task 12: Windows proof runs (3 models) + comparison + evidence

**Files:**
- Create: `docs/plans/2026-07-27-asr-model-comparison-evidence.md`
- (Run outputs land only under gitignored `artifacts/asr-eval/`)

**Interfaces:**
- Consumes: everything. This is the system-level proof required by the spec: one small run per model (5 clips, 1 pass), NOT full hour-long profiles.

- [ ] **Step 1: Proof run — production model (streaming)**

```bash
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/AppData/Local/winpepper/models/nemotron-streaming-en \
  --model-name nemotron-streaming-en \
  --max-clips 5 --max-passes 1
```
(The production dir READ-ONLY satisfies `ModelDirLayout.Resolve`: one gguf at root + `runtime/transcribe-native-windows-x86_64-cpu-vulkan/transcribe.dll`. The extra `.tar.gz` files at its root are not `*.gguf` so they don't trip the exactly-one rule.)
Expected: exit 0; `artifacts/asr-eval/nemotron-streaming-en/results.json` exists. Timeout guidance: allow ~15–20 min (build + model load + 5 real-time-paced clips).

- [ ] **Step 2: Proof run — nemotron-3.5 (streaming, language en-US)**

```bash
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b \
  --model-name nemotron-3.5-asr-streaming-0.6b --language en-US \
  --max-clips 5 --max-passes 1
```
Expected: exit 0; `artifacts/asr-eval/nemotron-3.5-asr-streaming-0.6b/results.json` exists. If the native library rejects the run params (ABI gate throw at Load or `TRANSCRIBE_ERR_*` at run), STOP and re-verify the `RunParams` layout against `/tmp/transcribe-0.1.3.h` before proceeding — do not paper over it.

- [ ] **Step 3: Proof run — Qwen3-ASR (batch-only)**

First verify the staged model exists (Task 10 output). If Task 10 reported `SKIPPED (incomplete download...)`, re-run `./scripts/setup-asr-eval-models.sh`; if still incomplete, SKIP this step and record that fact verbatim in the evidence doc — that is the spec's sanctioned graceful skip.
```bash
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/winpepper-evals/models/qwen3-asr-1.7b \
  --model-name qwen3-asr-1.7b --batch-only \
  --max-clips 5 --max-passes 1
```
Expected: exit 0; `artifacts/asr-eval/qwen3-asr-1.7b/results.json` exists. This model is a 1.7B audio-LLM — expect noticeably slower batch times and higher peak memory; allow ~30 min.

- [ ] **Step 4: Sanity-check the three results files**

```bash
for m in nemotron-streaming-en nemotron-3.5-asr-streaming-0.6b qwen3-asr-1.7b; do
  f="artifacts/asr-eval/$m/results.json"
  [[ -f "$f" ]] && python3 -c "
import json,sys
r = json.load(open('$f'))
info, s = r['info'], r['summary']
print('$m', 'mode=', info['mode'], 'language=', info.get('language'), 'passes=', info['passes'],
      'converged=', info['converged'], 'clips=', s['clipCount'], 'meanWer=', s['meanWer'],
      'p50ms=', s['latencyP50Ms'], 'cpuS=', s['cpuSecondsTotal'], 'peakMB=', s['peakMemoryMb'],
      'meanRtf=', s['meanRtf'], 'unstable=', s['unstableTranscriptCount'])
" || echo "$m: results.json MISSING"
done
```
Expected sanity bars (record actuals in the evidence doc):
- `mode` = `streaming`, `streaming`, `batch` respectively; `language` = `null`, `en-US`, `null`.
- `passes` = 1, `converged` = false (one pass cannot converge), `clips` = 5.
- `meanWer` between 0 and 1 for the streaming models (production model should roughly match its recorded corpus baseline); `latencyP50Ms > 0`; `cpuSecondsTotal > 0`; `peakMemoryMb > 500`; `meanRtf > 0`.
- `unstableTranscriptCount` = 0.
Also confirm `passes` and `convergenceTrace` arrays exist with 1 entry each: `python3 -c "import json; r=json.load(open('artifacts/asr-eval/nemotron-streaming-en/results.json')); print(len(r['passes']), len(r['convergenceTrace']))"` → `1 1`.

- [ ] **Step 5: Run the comparison aggregator over the three runs**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release -p:EnableWindowsTargeting=true
dotnet exec scripts/asr-latency-bench/bin/Release/net9.0/AsrLatencyBench.dll compare \
  --results-root artifacts/asr-eval --out artifacts/asr-eval
python3 -c "
import json
c = json.load(open('artifacts/asr-eval/comparison.json'))
for m in c['models']:
    print(m['model'], m['mode'], m['passes'], m['converged'], m['meanWer'], m['latencyP50Ms'], m['cpuSecondsTotal'], m['peakMemoryMb'])
"
rm -rf scripts/asr-latency-bench/bin scripts/asr-latency-bench/obj
```
Expected: `comparison.json` lists the models that ran (3, or 2 + a note if Qwen was skipped), aligned rows, no transcript text (spot-check: `grep -c reference artifacts/asr-eval/comparison.json` finds no transcript strings — the only allowed hits are field names).

- [ ] **Step 6: Write the evidence doc (numbers + clip ids ONLY)**

Create `docs/plans/2026-07-27-asr-model-comparison-evidence.md` containing:
- The exact three driver commands used.
- Each model's `results.md` content (it is text-free by contract) or the Step 4 summary lines.
- The Step 5 comparison table output.
- Qwen download/skip status if applicable, verbatim.
- One line noting: full profiles (55-minute budget, convergence) are the caller's follow-up; this proves the tooling end to end.
NEVER paste transcript or reference text; `results.json` stays in `artifacts/` only.

- [ ] **Step 7: Full suite + final commit**

```bash
./scripts/linux-tests.sh
git add docs/plans/2026-07-27-asr-model-comparison-evidence.md
git commit -m "docs(evidence): proof runs for 3-model ASR eval - per-model results + comparison"
git status --short   # verify: no corpus/model/artifact files staged; .kata.toml and .opencode/ untouched
```

---

## Self-Review (completed by the plan author)

**Spec coverage:**
1. Model parameterization (`--model-dir`, `--model-name`, `--language`, `--batch-only`, mode marked in results) → Tasks 7, 9, 6.
2. Resource utilization (CPU delta user+privileged, peak working set, RTF, per-pass aggregates, GPU-not-measured note) → Tasks 4, 5, 6, 9.
3. Convergence mode (`--time-budget-minutes` default 55, `--min-passes 2`, 95% CI half-width of pooled per-clip medians < 5% of mean for two consecutive passes, WER-stability verification and flagging, passes/converged/per-pass aggregates/trace in JSON) → Tasks 3, 6, 9.
4. Driver script with per-model output dirs → Task 11 (plus staging in Task 10).
5. Comparison aggregator → Task 8.
6. Proof: unit tests (convergence CI math ✓ Task 3, per-pass aggregation ✓ Task 5, batch-vs-streaming mode marking ✓ Task 6, comparison aggregation ✓ Task 8, plus resource math, validators, model-dir resolution, language forwarding) and three small Windows runs ✓ Task 12 with Qwen graceful-skip handling.
7. nemotron-3.5 mandatory language: engine binding gains run-params language (Tasks 1–2, header-verified layout, ABI size gate fails loud on mismatch); att-context 13 is valid for both streaming models (3.5 supports {0,3,6,13}); tag stripping is the 0.1.3 default (`keep_special_tags=false`) — nothing to do.

**No silent deferrals:** every user-facing behavior lands as production tooling behavior proven by the Task 12 real-model runs; stubs/fakes appear only in unit tests of pure logic, and each such piece is exercised for real in Task 12 (language → nemotron-3.5 run; batch-only → Qwen run; resources/convergence fields → Step 4 sanity checks; comparison → Step 5). The only conditional is the spec's own sanctioned graceful skip for an incomplete Qwen download, recorded in evidence. No UNRESOLVED COVERAGE GAPS.

**Placeholder scan:** the two shorthand markers in Task 9's sketch (`FirstPass`, `CountedFailedBeforeThisPass`) are explicitly resolved in the "Implementation notes" that bind the inline form; existing-code blocks that "move unchanged" are anchored to exact current line ranges. No TBD/TODO items remain.

**Type consistency:** `ConvergencePoint(Pass, MeanMs, CiHalfWidthMs, RatioToMean, Precise)` is used identically in Tasks 3, 6, 8, 9; `PassSummary` 9-field shape identical in Tasks 5, 6, 8, 9; `ClipResult` extensions (`BatchMsRuns`, `CpuSeconds`, `MeanRtf`, `TranscriptStable`) match between Tasks 6, 8 (tests), and 9 (construction); `BeginStream(int, string?)`/`TranscribeBatch(float[], string?)`/transcriber ctor match between Tasks 2 and 9; `ModelDirLayout.Resolved(GgufPath, RuntimeDir)` matches between Tasks 7 and 9; `EvalResults.JsonOpts` made public in Task 6 and consumed in Task 8.
