# Cleanup/ASR Contention — Single Combined Run Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Land, on one branch, everything needed to stop the cleanup LLM from
degrading live transcription: the remaining evidence work from the approved plan
(0c verdict + waiver record), new zero-cost resource instrumentation (page
faults, memory, threads, handles, system CPU, gate-wait visibility), three
verified native-memory leak fixes, the two approved contention fixes (prefetch
moved to recording-stop with full cancellation policy; cleanup thread cap), and
the two wedge-cascade fixes (batch routing after an abandon; early drain abandon
when the in-flight native call has already exceeded the drain budget).

**Architecture:** All pure logic (timing-line formatting, system-CPU math,
prefetch cancellation policy, ctx_src stamping, batch-routing decision, early
drain-abandon decision) lives in
Linux-testable classes (`Winpepper.Core`, `Winpepper.Platform`, `Winpepper.Asr`
net9.0 TFM). Windows-only code (`PipelineHost`, `LlamaCleanupBackend`,
`OcrFallback`, P/Invoke call paths) does nothing but sample values and delegate
to the pure classes; it is compile-verified by `./scripts/windows-gate.sh`.
Findings and measurements go to the committed evidence file.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly (`dotnet exec <dll>`, NEVER
`dotnet test`), LLamaSharp 0.27.0 (Windows TFM only), `[LibraryImport]` Win32
interop in `Winpepper.Platform`.

## Global Constraints

- **Governing document:** `docs/plans/2026-07-29-cleanup-asr-contention-fix.md`
  (committed at `8f5db7d`, APPROVED FOR EXECUTION) is authoritative for items it
  covers. It is READ-ONLY. All findings go to
  `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append; never
  clobber existing sections).
- **Owner supersession (2026-07-30):** everything lands in ONE run. The approved
  plan's steps 2–3 owner gate and the 0a experiment are WAIVED; the
  baseline-relative context-quality criterion is DOWNGRADED to reporting. Task 1
  records these waivers in the evidence file.
- **Mismatch rule (binding, from the approved plan):** line numbers and file
  paths in this document are pointers, not claims. If code is not at the stated
  location, search by filename or symbol under `src/` (of THIS worktree), verify
  the claimed substance, record the corrected location in the evidence file, and
  continue. STOP only when the claimed code or behavior cannot be found anywhere
  under `src/`, or contradicts the claim in substance.
- **Worktree:** all work happens inside
  `/home/dan/code/winpepper/.worktrees/cleanup-asr-contention-run1` (branch
  `feat/cleanup-asr-contention-run1`). All paths below are relative to that
  worktree root. Never read or edit files in any OTHER checkout of this repo.
- **Already landed on this branch (do NOT re-implement):** 0b's three fields —
  `ctx_src` (consume-time), `over250_at` (cap 16 + overflow, unclamped),
  `proc_cpu_ms` (start → StopRequested) — are committed (`1b6d5b4`, `74f1467`,
  `64a5828`, `2e59eb3`) with Linux tests. Task 1 verifies them; later tasks
  build on them.
- **Tests:** `./scripts/linux-tests.sh` must print `LINUX SUITE: GREEN` before
  EVERY commit. NEVER `dotnet test`. Full `./scripts/windows-gate.sh` must print
  `GATE: GREEN` before the run is done (Task 11). UNC `MSB4025` and vsock
  interop failures are known transient flakes — retry the gate. Never mix Linux-
  and Windows-side builds in the same `bin/`/`obj/` (the scripts handle this).
- **Both arms rule:** `PipelineHost.cs` has two near-identical hotkey arms —
  hold (`HoldDown`/`HoldUp`) and toggle — with `2`-suffixed locals in the toggle
  clone. EVERY PipelineHost change lands in BOTH arms.
- **Golden-string rule:** any new `DictationTimingSummary` field changes the
  golden line asserted at
  `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs:58-67`
  — update fixture + expected string in the SAME step.
- **Zero-cost instrumentation discipline:** a handful of cheap reads at
  recording start/stop only; all formatting in the pure `DictationTimingSummary`
  helper; never sample at summary-emit time.
- **Zero behavior change for instrumentation tasks** (2, 3, 4): measurement and
  data carriers only. In particular do NOT touch the `windowContextUsed` prompt
  sniff (`result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>")`) in
  `PipelineHost` — it feeds History.
- **Commits:** Conventional Commits style, focused and atomic, each with the
  trailer `Co-authored-by: Amplifier <amplifier@users.noreply.github.com>`.
- **Do NOT push to origin.** The branch stays local; the root session merges,
  gates, and installs.
- **Owner-only sign-off:** any validation requiring real spoken dictations is
  performed later by the owner. This run records expectations and pre-computed
  tallies in the evidence file (Task 11), nothing more.

## File Structure

| File | Role |
|---|---|
| `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append) | Waivers, 0c verdict, investigation summary, baselines, gate/bench results. |
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (modify) | New pure fields `pf/mem/thr/hnd/sys_cpu` + `SystemCpuPercent` math. |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` (modify) | Tests + golden-line update. |
| `src/Winpepper.Platform/Diagnostics/ProcessResourceSampler.cs` (create) | `[LibraryImport]` GetProcessMemoryInfo + GetSystemTimes, null off-Windows. |
| `tests/Winpepper.Platform.Tests/Diagnostics/ProcessResourceSamplerTests.cs` (create) | Off-Windows null contract. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` (modify) | Resource sampling at start/stop; prefetch relocation; route-guard wiring (both arms). |
| `src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs` (modify) | Per-call `out int gateWaitMs` on `BeginStream`/`TranscribeBatch`. |
| `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs` (modify) | Measure gate wait in `BeginStream`/`TranscribeBatch`. |
| `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` (modify) | Split gate wait out of stream-begin native stats; INF log. |
| `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` (modify) | Report a configurable per-call `gateWaitMs`. |
| `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs` (modify) | Gate-wait exclusion test. |
| `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` (modify) | C1 pipeline dispose, C3 executor hoist, D2 thread cap. |
| `src/Winpepper.Platform/WindowContext/OcrFallback.cs` (modify) | C2 SoftwareBitmap dispose on all paths. |
| `src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs` (create) | Per-dictation CTS lifecycle (1a design details a/b/d). |
| `src/Winpepper.Platform/WindowContext/WindowContextStamp.cs` (create) | Pure consume-time ctx_src mapping (extracted from PipelineHost). |
| `tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchCoordinatorTests.cs` (create) | The two named race tests + policy tests. |
| `tests/Winpepper.Platform.Tests/WindowContext/WindowContextStampTests.cs` (create) | Pure mapping tests. |
| `tests/Winpepper.IntegrationTests/WindowContextConsumedStampTests.cs` (create) | 1a(c): real-CTS path asserts consumed stamp `ctx_src=uia`. |
| `src/Winpepper.Asr/Transcription/StreamingRouteGuard.cs` (create) | E1 pure routing decision. |
| `tests/Winpepper.Asr.Tests/Transcription/StreamingRouteGuardTests.cs` (create) | E1 decision tests. |
| `src/Winpepper.Asr/Transcription/DrainAbandonPolicy.cs` (create) | E2 pure early-abandon decision. |
| `src/Winpepper.Asr/Transcription/NativeCallStats.cs` (modify) | E2 `INativeCallInFlightSource` (lock-free in-flight probe). |
| `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs` (modify) | E2 early-abandon branch in `FinishAsync`. |
| `tests/Winpepper.Asr.Tests/Transcription/DrainAbandonPolicyTests.cs` (create) | E2 decision tests. |
| `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` (modify) | E2 immediate-abandon + no-early-abandon session tests. |

---

### Task 1: Evidence file — verify landed 0b, record owner waivers, settle 0c with quoted source, investigation summary, baselines

**Files:**
- Modify (append): `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`

**Interfaces:**
- Consumes: the committed 0b work (`git log 1b6d5b4 74f1467 64a5828 2e59eb3`),
  the read-only approved plan, the public LLamaSharp v0.27.0 source tag.
- Produces: the evidence file sections every later task appends under; the 0c
  verdict that Task 5 cites in its code comment.

- [ ] **Step 1: Verify the already-landed 0b instrumentation (mismatch-rule check)**

Run each and confirm non-empty output:

```bash
cd /home/dan/code/winpepper/.worktrees/cleanup-asr-contention-run1
grep -n "ConsumedWindowContext" src/Winpepper.Cleanup/CleanupResult.cs
grep -n "Over250AtMs\|StampOver250" src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs
grep -n "Over250ListCap" src/Winpepper.Asr/Transcription/NativeCallStats.cs
grep -n "ProcCpuMs\|CtxSrc" src/Winpepper.App/Hosting/PipelineHost.cs | head -8
```

Expected: `CleanupResult.cs:18` declares `public bool? ConsumedWindowContext`;
`DictationTimingSummary` has `Over250AtMs`/`StampOver250`; `NativeCallStats`
declares `Over250ListCap = 16`; `PipelineHost` stamps `CtxSrc` and `ProcCpuMs`
in both arms. If any is missing, STOP and report — the branch state contradicts
this plan's premise.

- [ ] **Step 2: Fetch the official LLamaSharp v0.27.0 StatelessExecutor source**

```bash
mkdir -p /tmp/llamasharp-0c
curl -fsSL -o /tmp/llamasharp-0c/LLamaStatelessExecutor.cs \
  https://raw.githubusercontent.com/SciSharp/LLamaSharp/v0.27.0/LLama/LLamaStatelessExecutor.cs
grep -n "Context" /tmp/llamasharp-0c/LLamaStatelessExecutor.cs | head -30
```

Expected: the constructor creates a context and immediately disposes it
(lines showing `Context = _weights.CreateContext(...)` followed by
`Context.Dispose()`), and `InferAsync` creates its working context inside a
`using` (e.g. `using var context = _weights.CreateContext(...)`). Note the
exact line numbers — they go verbatim into the evidence file. If the fetch
fails (offline), try
`https://raw.githubusercontent.com/SciSharp/LLamaSharp/v0.27.0/LLama/StatelessExecutor.cs`
(the file was named both ways across releases); if neither is reachable and
`ilspycmd` is unavailable, write "could not verify" in Step 3 and treat 1d as
NOT triggered — never settle this from memory.

- [ ] **Step 3: Append the evidence sections**

Append to `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (after the
existing sections; replace the pending `## 0c` placeholder content in place if
one exists, keeping its heading):

```markdown
## Owner supersession — 2026-07-30 (single combined run)

Recorded per the owner's 2026-07-30 order, which supersedes the approved plan's
"How this gets executed" numbered sequence:

1. WAIVED: the step-2/3 owner gate (baseline collection + explicit approval
   stop). Everything (0b remnants, 1a, 1b, leak fixes, new instrumentation,
   wedge-cascade fix) lands in this ONE run on one branch.
2. WAIVED: experiment 0a (window-context OFF branch). Superseded by stronger
   log evidence: dictations where the cleanup LLM was bypassed but the
   recording-start prefetch ran are degraded anyway (e.g. native_max=3960 ms
   with cleanup=0 ms), confirming the prefetch mechanism directly.
3. DOWNGRADED to REPORTING: the baseline-relative context-quality criterion
   (uia/ocr shares each dropping < 10 pp vs a pre-fix baseline). Because 0b and
   1a land together, no pre-fix ctx_src baseline can exist. Instead: report the
   raw ctx_src counts (uia / ocr / none) over the owner's post-install
   dictations.

## 0c — RESOLVED: Planner B correct; 1d NOT TRIGGERED

Verified against the official LLamaSharp v0.27.0 tag
(LLama/LLamaStatelessExecutor.cs fetched from GitHub raw):

- Constructor: creates a context and immediately disposes it —
  <quote the exact lines with their line numbers here>
- InferAsync: the working context is created inside a `using` and disposed
  deterministically when the async enumerator completes —
  <quote the exact lines with their line numbers here>

Verdict: nothing native lingers between generations awaiting GC finalization.
Plan 1d (explicit context disposal after each generation) is NOT TRIGGERED.
(The per-generation constructor churn is still real waste — fixed separately
as leak-fix C3, executor hoisted to a field.)

## Investigation summary — 2026-07-30 stall investigation

- Confirmed shape: EPISODIC contention (recording-start prefetch + cleanup
  inference competing with live streaming ASR) plus a WEDGE CASCADE amplifier:
  a wedged native call causes the 10 s drain abandon; the abandoned stream's
  dispose queues behind the wedged call while holding the engine-wide compute
  gate, so the NEXT dictation's BeginStream blocks up to 5 s and then
  batch-degrades — one wedge becomes a multi-dictation hang.
- Refuted hypotheses: native context/thread accumulation across dictations,
  ArrayPool growth at scale, GC pressure as primary cause.
- Confirmed native-memory leaks: (C1) DefaultSamplingPipeline created per
  generation and never disposed (LlamaCleanupBackend.cs:97-105; upstream
  BaseSamplingPipeline's native sampler chain is a finalizer-backed
  SafeHandle, so non-disposal means delayed, finalizer-dependent,
  non-deterministic reclamation — native memory exerts no managed-heap
  pressure, so undisposed chains pile up between collections; deterministic
  disposal is still the right fix);
  (C2) SoftwareBitmap created in OcrFallback.cs:102-108, consumed at :54,
  never disposed on any path (~width*height*4 bytes, ~33 MB per 4K-window
  OCR-path dictation); (C3) a fresh StatelessExecutor per generation whose
  0.27 constructor creates AND immediately disposes a throwaway context —
  doubled per-generation Vulkan context churn.
- Gate-wait attribution bug (B4): the "stream begin took X ms" log and the
  native_* stats book compute-gate wait as native call time. Mismatch-rule
  correction: the claimed site TranscribeCppEngine.cs:206-220/:270 has no
  timing/logging; the wrapper actually lives in
  NemotronStreamingTranscriber.EnsureStream (:224-228) + TimedNativeCall
  (:230-277), and the 5 s gate wait itself is inside
  TranscribeCppEngine.BeginStream (:209).

## Memory baseline (pre-fix, owner's machine, 2026-07-30)

At 5.9 h uptime: ~3.0 GB private / ~1.5 GB working set / 167 threads /
~2000 handles. Post-install expectation: mem= private should be flat across a
30-dictation session (see the validation-expectations section appended at the
end of this run).
```

Fill both `<quote ...>` placeholders with the actual quoted lines + line
numbers from Step 2 before committing — committing them unfilled is a task
failure (unless the "could not verify" branch fired, in which case write that
sentence and delete the quote bullets).

- [ ] **Step 4: Linux suite green**

Run: `./scripts/linux-tests.sh` (from the worktree root)
Expected: final line `LINUX SUITE: GREEN`

- [ ] **Step 5: Commit**

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — owner waivers, 0c verdict (StatelessExecutor), investigation summary, baseline" \
  -m "0c RESOLVED from the official v0.27.0 tag (quoted); 1d NOT triggered. Single-run supersession, 0a waiver, ctx_src criterion downgrade recorded." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 2: Timing-line fields `pf` / `mem` / `thr` / `hnd` / `sys_cpu` + pure system-CPU math

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: existing `DictationTimingSummary` (fields end at `ProcCpuMs` :84,
  `FormatLine()` renders `proc_cpu_ms` at :134 just before `total`).
- Produces: `int? PageFaults`, `int? MemPrivMb`, `int? MemWsMb`,
  `int? ThreadCount`, `int? HandleCount`, `int? SysCpuPct` properties and
  `public static int? SystemCpuPercent(long idleDelta, long kernelDelta, long userDelta)`
  — Task 3 stamps these from PipelineHost.

Validation note (2026-07-30, official GetSystemTimes docs): the semantics are
DOC-CONFIRMED — kernel time INCLUDES idle ("This time value also includes the
amount of time the system has been idle."), so
`busy = (kernel − idle) + user` is correct verbatim; the truth-table tests
below encode it (a mere `kernel + user > 0` check could never catch a wrong
subtraction).

- [ ] **Step 1: Write the failing tests**

Append inside the `DictationTimingSummaryTests` class:

```csharp
    [Fact]
    public void FormatLine_ResourceFields_RenderAsPlainKeyValues()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.PageFaults = 418;
        t.MemPrivMb = 3061;
        t.MemWsMb = 1542;
        t.ThreadCount = 167;
        t.HandleCount = 2003;
        t.SysCpuPct = 37;
        var line = t.FormatLine();
        line.ShouldContain(" pf=418");
        line.ShouldContain(" mem=3061/1542");
        line.ShouldContain(" thr=167");
        line.ShouldContain(" hnd=2003");
        line.ShouldContain(" sys_cpu=37");
    }

    [Fact]
    public void FormatLine_ResourceFields_OmittedWhenNull()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        var line = t.FormatLine();
        line.ShouldNotContain(" pf=");
        line.ShouldNotContain(" mem=");
        line.ShouldNotContain(" thr=");
        line.ShouldNotContain(" hnd=");
        line.ShouldNotContain(" sys_cpu=");
    }

    [Theory]
    [InlineData(900, 1000, 0, 10)]   // busy = (1000-900)+0 = 100 of 1000
    [InlineData(0, 500, 500, 100)]   // fully busy
    [InlineData(1000, 1000, 0, 0)]   // fully idle
    public void SystemCpuPercent_ComputesBusyShareOfTotal(long idle, long kernel, long user, int expected)
    {
        DictationTimingSummary.SystemCpuPercent(idle, kernel, user).ShouldBe(expected);
    }

    [Fact]
    public void SystemCpuPercent_InvalidWindow_ReturnsNull()
    {
        DictationTimingSummary.SystemCpuPercent(0, 0, 0).ShouldBeNull();      // empty window
        DictationTimingSummary.SystemCpuPercent(2000, 1000, 0).ShouldBeNull(); // busy < 0 (clock skew)
    }
```

Also update the golden fixture and golden line IN THIS STEP (they will fail
until Step 3): in `Full()` add after `ProcCpuMs = 1875,`:

```csharp
        PageFaults = 418,
        MemPrivMb = 3061,
        MemWsMb = 1542,
        ThreadCount = 167,
        HandleCount = 2003,
        SysCpuPct = 37,
```

and in `FormatLine_FullDictation_IsOneParseableKeyValueLine` change the line

```csharp
              + " gc=1/0/0 gc_pause=12ms prewarm_active=true proc_cpu_ms=1875"
```

to

```csharp
              + " gc=1/0/0 gc_pause=12ms prewarm_active=true proc_cpu_ms=1875"
              + " pf=418 mem=3061/1542 thr=167 hnd=2003 sys_cpu=37"
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with CS0117/CS1061 ("'DictationTimingSummary' does not
contain a definition for 'PageFaults'") — the compile failure IS the red state.

- [ ] **Step 3: Implement**

In `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`, after the
`ProcCpuMs` property (:84), add:

```csharp
    public int? PageFaults { get; set; }   // B1: page-fault count delta, recording start -> StopRequested
    public int? MemPrivMb { get; set; }    // B2: private bytes MB, sampled once at recording start
    public int? MemWsMb { get; set; }      // B2: working set MB, sampled once at recording start
    public int? ThreadCount { get; set; }  // B2: process thread count at recording start
    public int? HandleCount { get; set; }  // B2: process handle count at recording start
    public int? SysCpuPct { get; set; }    // B3: system-wide CPU % over the recording window (GetSystemTimes delta)
```

In `FormatLine()`, after `AppendOptNum(sb, "proc_cpu_ms", ProcCpuMs);` (:134)
and before `AppendCoreMs(sb, "total", TotalMs);`, add:

```csharp
        AppendOptNum(sb, "pf", PageFaults);
        if (MemPrivMb is not null || MemWsMb is not null)
            sb.Append(" mem=").Append(MemPrivMb ?? 0).Append('/').Append(MemWsMb ?? 0);
        AppendOptNum(sb, "thr", ThreadCount);
        AppendOptNum(sb, "hnd", HandleCount);
        AppendOptNum(sb, "sys_cpu", SysCpuPct);
```

After the existing `StampOver250` method (:142-149), add:

```csharp
    /// <summary>B3: system-wide CPU percent over the recording window, from two
    /// GetSystemTimes samples (100 ns FILETIME units). Windows' kernel time
    /// INCLUDES idle time (doc-confirmed 2026-07-30: "This time value also
    /// includes the amount of time the system has been idle."), so
    /// busy = (kernel - idle) + user. Null when the window is empty or
    /// inconsistent (first sample failed, clock skew).</summary>
    public static int? SystemCpuPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;
        if (total <= 0) return null;
        var busy = kernelDelta - idleDelta + userDelta;
        if (busy < 0) return null;
        return (int)(busy * 100 / total);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0` and `Failed: 0` (including the updated golden-line test).

- [ ] **Step 5: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "feat(core): pf/mem/thr/hnd/sys_cpu on the dictation timing line formatter" \
  -m "Pure fields + SystemCpuPercent math (kernel includes idle). Golden line updated in lockstep." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 3: Windows sampling — `ProcessResourceSampler` P/Invokes + PipelineHost wiring (both arms)

**Files:**
- Create: `src/Winpepper.Platform/Diagnostics/ProcessResourceSampler.cs`
- Create: `tests/Winpepper.Platform.Tests/Diagnostics/ProcessResourceSamplerTests.cs`
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (start blocks ~:514-522 /
  ~:1034-1042; stop blocks ~:538-551 / ~:1058-1071; fields near :70)

**Interfaces:**
- Consumes: Task 2's `DictationTimingSummary` fields + `SystemCpuPercent`.
- Produces: `Winpepper.Platform.Diagnostics.ProcessResourceSampler` with
  `public static uint? PageFaultCount()` and
  `public static SystemTimesSample? SystemTimes()` where
  `public readonly record struct SystemTimesSample(long Idle100ns, long Kernel100ns, long User100ns)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Platform.Tests/Diagnostics/ProcessResourceSamplerTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Diagnostics;
using Xunit;

namespace Winpepper.Platform.Tests.Diagnostics;

public class ProcessResourceSamplerTests
{
    [Fact]
    public void OffWindows_ReturnsNull_NeverThrows()
    {
        if (OperatingSystem.IsWindows()) return; // Windows behavior is gate-verified
        ProcessResourceSampler.PageFaultCount().ShouldBeNull();
        ProcessResourceSampler.SystemTimes().ShouldBeNull();
    }

    [Fact]
    public void OnWindows_ReturnsValues_AndSysCpuIsPlausible()
    {
        if (!OperatingSystem.IsWindows()) return;
        // A cb/layout mistake makes GetProcessMemoryInfo return false -> null.
        ProcessResourceSampler.PageFaultCount().ShouldNotBeNull();
        var s0 = ProcessResourceSampler.SystemTimes();
        s0.ShouldNotBeNull();
        Thread.Sleep(50);
        var s1 = ProcessResourceSampler.SystemTimes();
        s1.ShouldNotBeNull();
        // Plausibility bound (upgraded 2026-07-30): `kernel + user > 0` alone
        // cannot catch a wrong busy subtraction. A real two-sample window
        // must satisfy the doc-confirmed structural invariants — kernel
        // INCLUDES idle, so 0 <= busy <= total and 0 <= sys_cpu <= 100.
        var idleD = s1!.Value.Idle100ns - s0!.Value.Idle100ns;
        var kernelD = s1.Value.Kernel100ns - s0.Value.Kernel100ns;
        var userD = s1.Value.User100ns - s0.Value.User100ns;
        var busy = kernelD - idleD + userD;
        var total = kernelD + userD;
        total.ShouldBeGreaterThan(0);
        busy.ShouldBeGreaterThanOrEqualTo(0); // idleΔ <= kernelΔ (kernel includes idle)
        (busy * 100 / total).ShouldBeInRange(0, 100); // same math as SystemCpuPercent
    }

    [Fact]
    public void MemoryCountersStruct_CbRoundTrip_MatchesDocumentedLayout()
    {
        // PROCESS_MEMORY_COUNTERS_EX: 2 DWORDs + 9 SIZE_Ts = 80 bytes on x64
        // (doc-verified layout; the 2 leading DWORDs make the SIZE_Ts
        // naturally 8-aligned — no hidden padding). cb and the function's cb
        // argument must both carry this exact value or the call fails/truncates.
        if (!Environment.Is64BitProcess) return;
        System.Runtime.InteropServices.Marshal
            .SizeOf<ProcessResourceSampler.PROCESS_MEMORY_COUNTERS_EX>()
            .ShouldBe(80);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with CS0246 ("The type or namespace name
'ProcessResourceSampler' could not be found").

- [ ] **Step 3: Implement the sampler**

Create `src/Winpepper.Platform/Diagnostics/ProcessResourceSampler.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Diagnostics;

/// <summary>Cheap per-recording resource reads for the dictation timing line
/// (page faults, system-wide CPU). Same LibraryImport style as
/// PacingWaiterNative: compiles on BOTH TFMs; every method returns null
/// off-Windows or on API failure so callers omit the field rather than fail a
/// dictation. Called only at recording start and at the stop request — never
/// on a hot path.</summary>
public static partial class ProcessResourceSampler
{
    public readonly record struct SystemTimesSample(long Idle100ns, long Kernel100ns, long User100ns);

    /// <summary>Process-lifetime page-fault count via GetProcessMemoryInfo
    /// (psapi). Callers diff two reads to get the recording-window delta.</summary>
    public static uint? PageFaultCount()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var counters = new PROCESS_MEMORY_COUNTERS_EX
        {
            cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>(),
        };
        return GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb)
            ? counters.PageFaultCount
            : null;
    }

    /// <summary>System-wide idle/kernel/user FILETIMEs in 100 ns units.
    /// Kernel INCLUDES idle — DictationTimingSummary.SystemCpuPercent does the
    /// subtraction. Caveat (doc-confirmed): on machines with more than 64
    /// logical processors, GetSystemTimes sums only the calling thread's
    /// primary processor group — sys_cpu then reflects one group, not the
    /// whole machine. Negligible on the target boxes.</summary>
    public static SystemTimesSample? SystemTimes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        return GetSystemTimes(out var idle, out var kernel, out var user)
            ? new SystemTimesSample(idle, kernel, user)
            : null;
    }

    // internal (not private) so the layout/cb round-trip test can assert
    // Marshal.SizeOf == 80 on x64 (2 DWORDs + 9 SIZE_Ts, doc-verified).
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;     // documented ALWAYS ZERO on Win7/2008R2 and earlier — never report this
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;      // use THIS for private/commit bytes if ever read from the struct
    }

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(
        IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX counters, uint cb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(
        out long idleTime, out long kernelTime, out long userTime);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Wire PipelineHost (both arms)**

In `src/Winpepper.App/Hosting/PipelineHost.cs` add fields next to
`_procCpuAtStart` (~:70):

```csharp
    private uint? _pfAtStart;                                                            // B1
    private Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimesSample? _sysTimesAtStart; // B3
    private int? _memPrivMbAtStart;                                                      // B2
    private int? _memWsMbAtStart;
    private int? _thrAtStart;
    private int? _hndAtStart;
```

**Hold arm, recording start** (~:521): replace the single line
`_procCpuAtStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;`
with:

```csharp
                var procAtStart = System.Diagnostics.Process.GetCurrentProcess();
                _procCpuAtStart = procAtStart.TotalProcessorTime;
                // B2: point-in-time resource snapshot at recording start.
                _memPrivMbAtStart = (int)(procAtStart.PrivateMemorySize64 / (1024 * 1024));
                _memWsMbAtStart = (int)(procAtStart.WorkingSet64 / (1024 * 1024));
                _thrAtStart = procAtStart.Threads.Count;
                _hndAtStart = procAtStart.HandleCount;
                // B1/B3: baselines for stop-time deltas.
                _pfAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount();
                _sysTimesAtStart = Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes();
```

**Toggle arm, recording start** (~:1041): identical replacement, with the local
named `procAtStart2`.

**Hold arm, stop** (~:551): after `timing.ProcCpuMs = ...`, add:

```csharp
                timing.MemPrivMb = _memPrivMbAtStart;
                timing.MemWsMb = _memWsMbAtStart;
                timing.ThreadCount = _thrAtStart;
                timing.HandleCount = _hndAtStart;
                if (_pfAtStart is uint pf0
                    && Winpepper.Platform.Diagnostics.ProcessResourceSampler.PageFaultCount() is uint pf1)
                    timing.PageFaults = (int)(pf1 - pf0);
                if (_sysTimesAtStart is { } st0
                    && Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes() is { } st1)
                    timing.SysCpuPct = Winpepper.Core.Diagnostics.DictationTimingSummary.SystemCpuPercent(
                        st1.Idle100ns - st0.Idle100ns,
                        st1.Kernel100ns - st0.Kernel100ns,
                        st1.User100ns - st0.User100ns);
```

**Toggle arm, stop** (~:1071): identical block on `timing2`, locals `pf0_2`,
`pf1_2`, `st0_2`, `st1_2` (C# pattern locals can't collide across arms of one
switch — if the compiler complains, suffix them `2`).

- [ ] **Step 6: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN` (PipelineHost is
`#if WINDOWS`-only; the gate in Task 11 compile-verifies it), then:

```bash
git add src/Winpepper.Platform/Diagnostics/ProcessResourceSampler.cs tests/Winpepper.Platform.Tests/Diagnostics/ProcessResourceSamplerTests.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): sample page faults, memory, threads, handles, system CPU per recording" \
  -m "GetProcessMemoryInfo + GetSystemTimes via LibraryImport in Winpepper.Platform; sampled at recording start and StopRequested only (both hotkey arms); pure math + formatting in DictationTimingSummary." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 4: B4 — measure and log compute-gate wait separately from native stream-begin time

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs`
- Modify: `src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs` (`BeginStream`
  ~:201-221, `TranscribeBatch` ~:266-272)
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
  (`EnsureStream` ~:224-228, `TimedNativeCall` ~:230-277)
- Modify: `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: existing `_computeGate` / `s_gateTimeout` in `TranscribeCppEngine`
  (:52-53), `TimedNativeCall` aggregates, `NativeCallStats`.
- Produces: a trailing `out int gateWaitMs` parameter on
  `ITranscribeCppEngine.BeginStream` and `.TranscribeBatch` (the gate wait is
  RETURNED PER-CALL; written by the engine immediately after the gate wait
  completes and BEFORE the gate-timeout throw, so — `out` being by-ref — the
  value is valid to the caller on both return and throw) and
  `private void RecordNativeSample(string op, long startTick, long elapsedMs)`
  inside `NemotronStreamingTranscriber.Session`.

Why per-call, not a mutable engine property (assumption falsified
2026-07-30): calls on the shared singleton engine DO overlap — a wedged
`BeginStream` returning ~16 s late would read the NEXT dictation's 5000 ms
gate-timeout write off a shared `LastGateWaitMs` slot (the cancel path
orphans the pump without `FinishAsync`, so the orphan's call runs concurrently
with the next dictation's), corrupting `native_max`/`over250_at` — the
adjudication evidence. A per-call return has no shared mutable slot and needs
no happens-before argument.

Mismatch-rule note (already recorded in the evidence file by Task 1): the spec
pointed at `TranscribeCppEngine.cs:206-220/:270` for the "booked as native
time" logging; the wrapper actually lives in
`NemotronStreamingTranscriber.EnsureStream`/`TimedNativeCall`. The fix below
touches both layers: the engine MEASURES the gate wait (only it can), the
transcriber SUBTRACTS it from native stats and LOGS it.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`,
add (mirror the setup of the existing over-250 test around `:303` —
`StreamBegin_...`-style tests already construct a session over
`FakeTranscribeCppEngine`; reuse exactly that construction. Adapt fake/session
member names to the existing fake's API per the mismatch rule — the substance
below is binding, the names are pointers):

```csharp
    [Fact]
    public async Task StreamBegin_GateWait_IsExcludedFromNativeStats()
    {
        // The fake's BeginStream blocks 600 ms and reports that the ENTIRE
        // 600 ms was compute-gate wait. With B4, native stats must see ~0 ms
        // for stream begin: no over-250 entry, native_max well under 250.
        var engine = new FakeTranscribeCppEngine
        {
            BeginStreamDelay = TimeSpan.FromMilliseconds(600),
            GateWaitMsToReport = 600, // returned per-call via BeginStream's out parameter
        };
        var session = CreateSession(engine); // same helper/pattern as the :303 over250 test
        await session.PushAsync(OneChunk(), TestContext.Current.CancellationToken); // forces EnsureStream
        var stats = session.NativeCallStats;
        Assert.Equal(0, stats.CountOver250Ms);
        Assert.True(stats.MaxMs < 250,
            $"gate wait leaked into native stats: MaxMs={stats.MaxMs}");
    }
```

In `FakeTranscribeCppEngine.cs`, add the members the test needs:

```csharp
    public TimeSpan BeginStreamDelay { get; set; } = TimeSpan.Zero;
    public int GateWaitMsToReport { get; set; }
```

update the fake's `BeginStream` (and `TranscribeBatch`) signatures to the new
interface shape (trailing `out int gateWaitMs`), and at the top of the fake's
`BeginStream` implementation:

```csharp
        if (BeginStreamDelay > TimeSpan.Zero) Thread.Sleep(BeginStreamDelay);
        gateWaitMs = GateWaitMsToReport;
```

(`TranscribeBatch` in the fake just sets `gateWaitMs = 0;` unless a test needs
otherwise.)

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS — the fake's `BeginStream` signature does not yet match
the changed interface (and the test passes `GateWaitMsToReport` nowhere);
after the Step 3 signature change lands the TEST fails (`CountOver250Ms` is
1: the 600 ms begin is booked as native) until Step 4's transcriber change
lands. Record both red states.

- [ ] **Step 3: Change the signatures + engine measurement (per-call gate wait)**

`ITranscribeCppEngine.cs`: add a trailing `out int gateWaitMs` parameter to
`BeginStream` (:27) and `TranscribeBatch` (an `out` parameter cannot follow an
optional one — drop the `= null` default on `language` if needed; call sites
already pass it explicitly):

```csharp
    /// <summary>B4 (gateWaitMs): how long THIS call spent waiting on the
    /// compute gate before any native work started, returned PER CALL — never
    /// a shared mutable slot: calls on the singleton engine can overlap (a
    /// cancel-orphaned pump's wedged BeginStream vs the next dictation's), so
    /// a read-after-call property would mis-attribute another call's gate
    /// wait. The engine writes this immediately after the gate wait completes
    /// and BEFORE the gate-timeout throw; `out` is by-ref, so the value is
    /// valid to the caller on both return and throw. Lets the caller book
    /// queueing behind a prior stream separately from native compute.</summary>
    ITranscribeCppStream BeginStream(int attContextRight, string? language, out int gateWaitMs);
```

`TranscribeCppEngine.cs`: in `BeginStream` (~:209) replace:

```csharp
        if (!_computeGate.Wait(s_gateTimeout))
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
```

with:

```csharp
        var gateSw = System.Diagnostics.Stopwatch.StartNew();
        var acquired = _computeGate.Wait(s_gateTimeout);
        gateSw.Stop();
        gateWaitMs = (int)gateSw.ElapsedMilliseconds; // per-call, valid even on the throw below
        if (!acquired)
            throw new TranscribeCppException(
                "another transcription is still active on the engine (compute gate timeout)");
```

Apply the IDENTICAL replacement to the same `_computeGate.Wait(s_gateTimeout)`
block in `TranscribeBatch` (~:270-272), with its own trailing
`out int gateWaitMs`.

If any other class implements `ITranscribeCppEngine` (search
`grep -rn ": ITranscribeCppEngine" src/ tests/`), update its signatures too
(wrappers pass the `out` value straight through from the inner engine). Fix
any `TranscribeBatch` call sites the compiler finds with `out _` where the
value is unused.

- [ ] **Step 4: Split the gate wait out in the transcriber**

In `NemotronStreamingTranscriber.Session`, extract the stats mutation from
`TimedNativeCall`'s `finally` into a new method (verbatim move — behavior of
the other call sites is unchanged):

```csharp
        private void RecordNativeSample(string op, long startTick, long elapsedMs)
        {
            _nativeCalls++;
            _nativeTotalMs += elapsedMs;
            if (elapsedMs > _nativeMaxMs) _nativeMaxMs = elapsedMs;
            if (elapsedMs >= SlowNativeCallMs)
            {
                _nativeOver250++;
                if (_over250StartTicks.Count < NativeCallStats.Over250ListCap)
                    _over250StartTicks.Add(startTick);
                else
                    _over250Overflow++;
            }
            if (elapsedMs >= _nativeCallWarnAfter.TotalMilliseconds)
                _log?.LogWarning(
                    "nemotron native {Op} took {ElapsedMs} ms; a call this slow stalls the streaming pump until it returns",
                    op, (int)elapsedMs);
        }
```

`TimedNativeCall`'s `finally` becomes:

```csharp
            finally
            {
                watchdogCts.Cancel();
                nativeSw.Stop();
                RecordNativeSample(op, startTick, nativeSw.ElapsedMilliseconds);
            }
```

Replace `EnsureStream` (:224-228) with:

```csharp
        private void EnsureStream()
        {
            if (_stream is not null) return;
            var engine = _engineProvider();
            var startTick = Environment.TickCount64;
            var sw = Stopwatch.StartNew();
            using var watchdogCts = new CancellationTokenSource();
            _ = WarnWhenStillRunningAsync("stream begin", watchdogCts.Token);
            // B4: written by the engine BEFORE the gate-timeout throw (out is
            // by-ref), so the finally sees THIS call's gate wait on both
            // return and throw; 0 if the engine threw before the gate wait.
            var gateWaitMs = 0;
            try
            {
                _stream = engine.BeginStream(_attContextRight, _language, out gateWaitMs);
            }
            finally
            {
                watchdogCts.Cancel();
                sw.Stop();
                // B4: the engine returns THIS call's gate wait per-call (no
                // shared slot to mis-read under overlapping calls); subtract
                // it so native_* stats (and over250_at) measure compute, not
                // queueing behind a prior stream's undisposed session.
                var gateWait = Math.Max(0, gateWaitMs);
                var nativeMs = Math.Max(0, sw.ElapsedMilliseconds - gateWait);
                RecordNativeSample("stream begin", startTick + gateWait, nativeMs);
                if (gateWait > 0)
                    _log?.LogInformation(
                        "stream begin: compute-gate wait {GateWaitMs} ms, native {NativeMs} ms",
                        gateWait, (int)nativeMs);
            }
        }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0` — including all pre-existing
`NemotronStreamingTranscriberTests` (the refactor must not change other call
sites' stats).

- [ ] **Step 6: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Asr/TranscribeCpp/ITranscribeCppEngine.cs src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs
git commit -m "feat(asr): measure and log compute-gate wait separately from native stream-begin time" \
  -m "BeginStream/TranscribeBatch return the gate wait PER CALL (out int gateWaitMs — a mutable engine property would mis-attribute across overlapping calls: a cancel-orphaned wedged BeginStream returning ~16 s late would read the next dictation's 5000 ms gate-timeout write); EnsureStream subtracts it from native_* stats and logs 'compute-gate wait X ms, native Y ms' at INF. Gate contention is no longer booked as native compute in over250_at." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 5: C1 + C3 — LlamaCleanupBackend: dispose the sampling pipeline per generation; hoist the StatelessExecutor to a field

**Files:**
- Modify: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` (ctor :43-51,
  `GenerateAsync` :88-105)

**Interfaces:**
- Consumes: Task 1's recorded 0c verdict (per-generation constructor churn is
  pure waste; nothing lingers).
- Produces: unchanged public surface (`ILlamaCleanupBackend.GenerateAsync`,
  `WarmAsync`, `Dispose`); internally a single `_executor` field.

This file is entirely `#if WINDOWS` — it cannot fail Linux tests. TDD here is
"compile + existing Windows integration tests via the Task 11 gate"; the steps
are edit → suite → commit.

C3 validation notes (source-verified 2026-07-30 against the pinned v0.27.0
tag; load-bearing — keep true):

- Hoist safety is proven, not assumed: `batch.Clear()` runs at the top of
  EVERY prompt-decode chunk (`SafeLLamaContextHandle.cs:721`), and
  `LLamaBatch.Clear()` (`LLamaBatch.cs:259-272`) is a FULL wipe (token count,
  logit positions, and `Array.Clear` of every buffer) — stale `_batch` state
  from a prior call is destroyed before the first native decode of the next
  call.
- The `_gate` SemaphoreSlim(1,1) serialization is LOAD-BEARING for the
  hoisted executor: `_batch` and the `Context` property are NOT
  concurrency-safe. Never remove or weaken the gate.
- Never mutate the shared `ModelParams` instance after construction: its
  values (`Threads`/`BatchThreads` etc.) are re-read LIVE at every
  per-generation context creation, so a later mutation silently changes
  generation-time behavior.

- [ ] **Step 1: Hoist the executor (C3)**

Add a field after `_promptFormat` (:23):

```csharp
    private readonly StatelessExecutor _executor;
```

In the constructor, after `_log.LogInformation("Cleanup model loaded.");`
(:50), add:

```csharp
        // C3: ONE executor per backend, not one per generation. Safe ONLY
        // because _gate (SemaphoreSlim(1,1)) serializes GenerateAsync — the
        // executor's _batch/Context are not concurrency-safe (v0.27.0 source:
        // batch.Clear() fully wipes stale state at the top of every
        // prompt-decode chunk, SafeLLamaContextHandle.cs:721 /
        // LLamaBatch.cs:259-272) — and ApplyTemplate=false is constant.
        // LLamaSharp 0.27's StatelessExecutor ctor creates and immediately
        // disposes a throwaway context (verified against the official
        // v0.27.0 tag — see docs/plans/2026-07-29-cleanup-asr-contention-evidence.md,
        // section "0c — RESOLVED"), so per-call construction doubled
        // per-generation Vulkan context churn for nothing.
        _executor = new StatelessExecutor(_weights, _params, _log)
        {
            ApplyTemplate = false,
        };
```

In `GenerateAsync`, DELETE the per-call construction (:88-91):

```csharp
            var executor = new StatelessExecutor(_weights, _params, _log)
            {
                ApplyTemplate = false,
            };
```

and change the inference line (:114) from `executor.InferAsync(...)` to
`_executor.InferAsync(...)`.

- [ ] **Step 2: Dispose the sampling pipeline per generation (C1)**

Change line :97 from

```csharp
            var pipeline = new DefaultSamplingPipeline
```

to

```csharp
            // C1: BaseSamplingPipeline owns a native llama.cpp sampler chain
            // via a finalizer-backed SafeHandle (ownsHandle: true). Undisposed,
            // reclamation is delayed, finalizer-dependent, and non-deterministic
            // — and native memory exerts no managed-heap pressure, so undisposed
            // chains can pile up between collections. 'using' makes the free
            // deterministic per generation.
            using var pipeline = new DefaultSamplingPipeline
```

(the `using var` disposes at the end of the `try` block, after the
`await foreach` has finished consuming it and before `_gate.Release()` — the
pipeline is not used after the loop).

- [ ] **Step 3: Linux suite green (proves nothing shared broke)**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN`. (Compile proof for this `#if WINDOWS` file
comes from Task 11's Windows gate, which also runs
`LlamaCleanupBackendIntegrationTests` when the qwen GGUF is present.)

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.Cleanup/LlamaCleanupBackend.cs
git commit -m "fix(cleanup): dispose sampling pipeline per generation; reuse one StatelessExecutor" \
  -m "C1: DefaultSamplingPipeline's native sampler chain is a finalizer-backed SafeHandle — undisposed, reclamation is delayed, finalizer-dependent, and non-deterministic; 'using' per generation makes it deterministic. C3: executor hoisted to a ctor field (gate-serialized — load-bearing; ApplyTemplate constant); halves per-generation Vulkan context churn per the 0c evidence." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 6: C2 — OcrFallback: dispose the SoftwareBitmap on ALL paths

**Files:**
- Modify: `src/Winpepper.Platform/WindowContext/OcrFallback.cs` (`CaptureAsync`
  :42-76)

**Interfaces:**
- Consumes: nothing new. Produces: unchanged public surface.

Why NOT `finally { swBitmap.Dispose(); }` (design falsified 2026-07-30): the
CsWinRT `AsTask(ct)` bridge registers ct → `TrySetCanceled` EAGERLY (shipped
`WinRT.Runtime` 2.2.0.48161, via CsWinRT PR #1539), so on cancellation the
awaited Task goes terminal while the native `OcrEngine.RecognizeAsync`
operation can still be Running and READING the SoftwareBitmap —
`IAsyncInfo.Cancel()` is only a request. A finally-dispose on the awaiting
frame would close the pixel buffer under a live native read on D1's ROUTINE
per-dictation-CTS cancel path (every rapid re-dictation). Disposal must
instead be owned by a continuation that observes the WinRT op's OWN terminal
state, with a TOKEN-LESS `AsTask()` (the bridge then completes ONLY from the
op's Completed handler) and `WaitAsync(ct)` keeping the caller's prompt
unblock.

Residual (accepted, unverifiable from public sources): that `OcrEngine`'s
native implementation stops reading the input bitmap strictly before firing
`Completed` is contract-conformant WinRT behavior but cannot be proven
(closed-source Windows.Media.Ocr).

- [ ] **Step 1: Cheap ct check before the capture stage**

The capture stage (`GetClientRect`/size checks +
`CaptureWindowToSoftwareBitmap`, i.e. `PrintWindow` + `LockBits` + buffer copy
+ `CreateCopyFromBuffer`) currently never observes `ct` at all. At the top of
`CaptureAsync`, before the size checks and the capture call (:35), add:

```csharp
        // D1: the capture stage (PrintWindow etc.) is blocking native work
        // that cannot observe ct mid-call — at least skip it entirely when
        // this dictation's prefetch is already cancelled.
        ct.ThrowIfCancellationRequested();
```

- [ ] **Step 2: Continuation-owned dispose + advisory cancel for the recognize stage**

In `CaptureAsync`, after `if (swBitmap is null) return WindowContextResult.Empty;`
(:42):

1. On the no-engine early return (engine is null, :44-49), the native op never
   saw the bitmap — dispose inline before returning:

```csharp
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                _log.LogDebug("OcrEngine.TryCreateFromUserProfileLanguages returned null; no OCR languages installed");
                swBitmap.Dispose(); // C2: no native op ever touched it — safe to close here
                return WindowContextResult.Empty;
            }
```

2. Replace the recognize await (:54 today,
   `ocr = await engine.RecognizeAsync(swBitmap).AsTask(ct).ConfigureAwait(false);`)
   and its surrounding try/catch with:

```csharp
            OcrResult ocr;
            try
            {
                // C2: dispose is owned by a CONTINUATION on a TOKEN-LESS
                // AsTask(): that Task reaches terminal state ONLY via the
                // WinRT op's Completed handler, so the bitmap is never closed
                // while the native op may still read it. Passing ct to
                // AsTask would be a use-after-close: the CsWinRT bridge
                // (WinRT.Runtime 2.2.0) cancels the TASK eagerly while the
                // native op keeps running (Cancel() is advisory).
                var op = engine.RecognizeAsync(swBitmap);
                var opTask = op.AsTask(); // NO token — terminal only from Completed
                using var reg = ct.Register(() =>
                {
                    try { op.Cancel(); } catch { /* COM disconnect */ }
                });
                _ = opTask.ContinueWith(
                    _ => swBitmap.Dispose(), // runs only after the op is observably terminal
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                // WaitAsync(ct): the CALLER still unblocks promptly on ct
                // (D1's routine cancellation stays fast); the op + dispose
                // continuation finish in the background on their own schedule.
                ocr = await opTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // prompt cancel for the caller; dispose continuation still pending
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "OcrEngine.RecognizeAsync threw");
                return WindowContextResult.Empty;
            }
```

   (If the existing catch shape differs — e.g. no OCE distinction today —
   preserve the existing non-cancel behavior per the mismatch rule; the
   binding substance is: token-less `AsTask()`, `ct.Register(op.Cancel)`,
   dispose in a continuation on the op's own task, caller unblocked via
   `WaitAsync(ct)` or equivalent. A synchronous throw from `RecognizeAsync`
   itself leaves the bitmap to the finalizer — rare failure path, accepted.)

3. The lines/text processing and the success/empty returns after the await
   are UNCHANGED (the bitmap is not used after recognition; its dispose rides
   the continuation).

- [ ] **Step 3: Linux suite green**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN` (this file is
`#if WINDOWS`; the Task 11 gate compiles it and runs `OcrIntegrationTests` on
the Windows TFM).

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.Platform/WindowContext/OcrFallback.cs
git commit -m "fix(platform): dispose the OCR SoftwareBitmap on all paths" \
  -m "Created at CaptureWindowToSoftwareBitmap, consumed by RecognizeAsync, previously never disposed — native WinRT memory (~33 MB per 4K-window OCR dictation). Dispose is continuation-owned on a token-less AsTask() (the CsWinRT AsTask(ct) bridge cancels the Task eagerly while the native op may still read the bitmap — a finally-dispose would be use-after-close on the routine cancel path); ct.Register(op.Cancel) keeps cancellation advisory to the op, WaitAsync(ct) keeps the caller's prompt unblock, and a cheap ct check now guards the capture stage." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 7: D2 — cap cleanup inference threads (named constant), judged on the cleanup bench

**Files:**
- Modify: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` (ctor `_params`
  init :43-47)
- Modify (append): `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`

**Interfaces:**
- Consumes: Task 5's edited constructor.
- Produces: `private static readonly int CleanupInferenceThreads` — the ONLY
  thread knob; all three backend construction sites (AppShell, History Lab,
  bench) inherit it automatically because they all flow through this ctor.

- [ ] **Step 1: Add the named constant and set the params**

Add above the constructor:

```csharp
    /// <summary>1b thread cap, tightened per the 2026-07-30 owner order:
    /// LLamaSharp 0.27's DEFAULT Threads is already ProcessorCount/2 (=16 on
    /// the owner's box), so the approved plan's max(1, ProcessorCount/2)
    /// would have been a no-op. The model is fully GPU-offloaded
    /// (GpuLayerCount=999) — CPU threads mainly drive graph orchestration —
    /// so cap LOW to bound the CPU burst that competes with live streaming
    /// ASR. Judged ONLY on scripts/run-cleanup-bench-windows.sh: median
    /// latency <= 1000 ms and unchanged eval outcomes.</summary>
    private static readonly int CleanupInferenceThreads =
        Math.Min(4, Math.Max(1, Environment.ProcessorCount / 4));
```

Change the `_params` initializer (:43-47) to:

```csharp
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount, // Vulkan backend picks the first device.
            Threads = CleanupInferenceThreads,
            BatchThreads = CleanupInferenceThreads,
        };
```

Mismatch note: LLamaSharp 0.27 `ModelParams` carries `Threads`/`BatchThreads`
as nullable ints — if the property names differ in the resolved package
(check `~/.nuget/packages/llamasharp/0.27.0/lib/`), set the equivalent
context-params members and record the correction in the evidence file.

Rationale source-confirmed (2026-07-30, pinned v0.27.0 source): `Threads=null`
resolves in `IContextParamsExtensions.Threads()` to
`Math.Max(Environment.ProcessorCount / 2, 1)` = 16 on the owner's 32-LP box,
inside `ToLlamaContextParams` — i.e. at CONTEXT CREATION, per generation; and
the `ModelParams` set here propagate to EVERY per-generation context
(`ToLlamaContextParams` re-reads them live on each `CreateContext`), so the
cap governs generation-time compute, not merely model load. (Corollary of the
live re-read: never mutate the shared `ModelParams` after construction — see
Task 5's C3 notes.)

- [ ] **Step 2: Linux suite green, then commit the code change**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Cleanup/LlamaCleanupBackend.cs
git commit -m "perf(cleanup): cap cleanup inference threads via named constant (min(4, cores/4))" \
  -m "1b as amended: 0.27's default is already cores/2, so cap lower. Model is fully GPU-offloaded; CPU threads mainly drive the graph. Judged only on the cleanup bench." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

- [ ] **Step 3: Run the cleanup bench (the 1b gate)**

```bash
./scripts/run-cleanup-bench-windows.sh --passes 3
```

(Long-running: allow up to 2 hours. Final line:
`run-cleanup-bench-windows: done -- results in artifacts/cleanup-bench/<stamp>/ ...`.)

Bench notes (2026-07-30):
- The bench judges the REGISTRY-DEFAULT cleanup model
  (`qwen2.5-0.5b-instruct-q4_k_m` — no `--model` flag), the SAME model as the
  prior committed 2026-07-27 record (p50 334 ms), NOT the app's
  currently-selected `sotto-cleanup-lfm25-350m-q8_0`. That is DELIBERATE, for
  comparability with the committed baseline — do NOT read this step's ladder
  decisions (raise the cap to 6, then 8) as covering the active model; they
  cover the registry default only.
- NEVER run this bench concurrently with `./scripts/windows-gate.sh` or
  `./scripts/linux-tests.sh` — the bench pre-cleans `src/*/bin,obj` and would
  corrupt a concurrent build.

Then read `artifacts/cleanup-bench/<stamp>/results.md` and verify BOTH:
1. median latency <= 1000 ms;
2. evaluation outcomes unchanged — every eval case that passes in the most
   recent recorded bench (search `docs/plans/` for the latest committed
   cleanup-bench numbers, e.g. the 2026-07-27/28 cleanup-model docs) still
   passes. If no prior record is discoverable, all eval cases passing IS the
   criterion, and note that in the evidence file.

If either fails: raise the cap stepwise (6, then 8 — edit the constant, one
commit per attempt, re-run the bench) until both hold; if `min(4, cores/4)`
through `8` all fail the median, STOP and report — the owner decides.

- [ ] **Step 4: Record the bench numbers and commit**

Append to the evidence file:

```markdown
## 1b — thread cap bench (run-cleanup-bench-windows.sh)

Cap: Threads = BatchThreads = min(4, max(1, ProcessorCount/4)).
Results (artifacts/cleanup-bench/<stamp>/results.md):
- median latency: <N> ms (criterion: <= 1000 ms) — PASS/FAIL
- eval outcomes: <summary, e.g. "all N cases pass; identical to the 2026-07-2x record"> — PASS/FAIL
```

Fill `<stamp>`, `<N>`, and the outcomes with the real values. Then:

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — cleanup bench results for the 1b thread cap" \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 8: D1 (pure half) — WindowContextPrefetchCoordinator + pure ctx_src stamp, with the two named race tests

**Files:**
- Create: `src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs`
- Create: `src/Winpepper.Platform/WindowContext/WindowContextStamp.cs`
- Test: `tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchCoordinatorTests.cs`
- Test: `tests/Winpepper.Platform.Tests/WindowContext/WindowContextStampTests.cs`
- Test: `tests/Winpepper.IntegrationTests/WindowContextConsumedStampTests.cs`
  (+ add `ProjectReference`s to `Winpepper.Platform` and `Winpepper.Cleanup` in
  `tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj` if not
  already present)

**Interfaces:**
- Consumes: `WindowContextPrefetch.StartAsync(IntPtr, CancellationToken)` →
  `Task<WindowContextResult>`; `WindowContextResult.Source`
  (`Empty|Uia|Ocr`); `CleanupRunner.RunAsync(...)` and
  `CleanupResult.ConsumedWindowContext` (bool?).
- Produces (Task 9 wires these into PipelineHost):
  - `WindowContextPrefetchHandle` with `Task<WindowContextResult> Task`,
    `CancellationToken Token`, `bool CancellationRequested`.
  - `WindowContextPrefetchCoordinator` with ctor
    `(Func<IntPtr, CancellationToken, Task<WindowContextResult>> start)`,
    `void OnRecordingStart()`, `WindowContextPrefetchHandle Start(IntPtr hwndAtStart)`,
    `void CancelAndClear()`, `WindowContextPrefetchHandle? Current`.
  - `static string? WindowContextStamp.CtxSrc(bool? consumedWindowContext, Task<WindowContextResult>? prefetchTask)`.

- [ ] **Step 1: Write the failing tests — the two NAMED race cases + policy tests**

Create `tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchCoordinatorTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextPrefetchCoordinatorTests
{
    // 1a(d) named race case 1: rapid re-dictation (N+1 starts < 2 s after N).
    [Fact]
    public async Task RapidRedictation_PriorPrefetchCancelledAtNextStart_StampsNone_DistinctCts()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                // Dictation N's prefetch: never completes on its own; goes
                // cancelled when its per-dictation token fires.
                var tcs = new TaskCompletionSource<WindowContextResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            return Task.FromResult(WindowContextResult.FromUia("next dictation context"));
        });

        // Dictation N: recording start, then stop -> prefetch launched, still running.
        coordinator.OnRecordingStart();
        var handleN = coordinator.Start(new IntPtr(1));
        handleN.Task.IsCompleted.ShouldBeFalse();

        // Dictation N+1's RECORDING START: per the 1a ruling, N's prefetch is
        // cancelled NOW (live speech wins over a stale context fetch).
        coordinator.OnRecordingStart();

        // Named observable 1: at N+1's recording start, N's prefetch is no
        // longer running (cancellation requested; task reaches completed).
        handleN.CancellationRequested.ShouldBeTrue();
        await Task.WhenAny(handleN.Task, Task.Delay(2000));
        handleN.Task.IsCompleted.ShouldBeTrue();
        handleN.Task.IsCompletedSuccessfully.ShouldBeFalse();

        // Named observable 2: N stamps ctx_src=none when cancelled — an
        // accepted, counted loss (consume-time semantics: the runner saw a
        // completed-but-cancelled task).
        WindowContextStamp.CtxSrc(consumedWindowContext: true, handleN.Task).ShouldBe("none");

        // Named observable 3: N and N+1 hold DISTINCT CancellationTokenSource
        // instances (observed via distinct tokens).
        var handleN1 = coordinator.Start(new IntPtr(2));
        handleN.Token.Equals(handleN1.Token).ShouldBeFalse();
        handleN1.CancellationRequested.ShouldBeFalse();
    }

    // 1a(d) named race case 2: silence-drop-then-dictate.
    [Fact]
    public async Task SilenceDropThenDictate_DroppedPrefetchCancelled_NothingObservableInNextContext()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                var tcs = new TaskCompletionSource<WindowContextResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                // If the dropped dictation's prefetch were EVER allowed to
                // finish, it would produce this marker text.
                ct.Register(() => tcs.TrySetCanceled(ct));
                _ = Task.Delay(5000, CancellationToken.None).ContinueWith(
                    _ => tcs.TrySetResult(WindowContextResult.FromUia("SECRET-FROM-DROPPED")),
                    TaskScheduler.Default);
                return tcs.Task;
            }
            return Task.FromResult(WindowContextResult.FromUia("fresh context"));
        });

        // Dictation D: start, stop -> prefetch launched, then D is dropped as silent.
        coordinator.OnRecordingStart();
        var dropped = coordinator.Start(new IntPtr(1));
        coordinator.CancelAndClear();

        // Named observable 1: the dropped dictation's prefetch was cancelled.
        dropped.CancellationRequested.ShouldBeTrue();
        coordinator.Current.ShouldBeNull();

        // Next dictation: nothing from the dropped prefetch is observable in
        // its context (named observable 2).
        coordinator.OnRecordingStart();
        var next = coordinator.Start(new IntPtr(2));
        var result = await next.Task;
        result.Text.ShouldBe("fresh context");
        result.Text.ShouldNotContain("SECRET-FROM-DROPPED");
    }

    [Fact]
    public void OnRecordingStart_CompletedPriorPrefetch_IsNotCancelled_JustCleared()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.FromUia("done")));
        coordinator.OnRecordingStart();
        var handle = coordinator.Start(new IntPtr(1));
        handle.Task.IsCompletedSuccessfully.ShouldBeTrue();

        coordinator.OnRecordingStart();
        handle.CancellationRequested.ShouldBeFalse(); // finished work is not disturbed
        coordinator.Current.ShouldBeNull();
    }

    [Fact]
    public void CancelAndClear_WithNoPrefetch_IsANoOp()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.Empty));
        coordinator.CancelAndClear();
        coordinator.Current.ShouldBeNull();
    }

    [Fact]
    public void Start_PassesTheHwndCapturedAtRecordingStart()
    {
        IntPtr seen = IntPtr.Zero;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            seen = hwnd;
            return Task.FromResult(WindowContextResult.Empty);
        });
        coordinator.Start(new IntPtr(0x1234));
        seen.ShouldBe(new IntPtr(0x1234)); // 1a(b): the start-captured target, not a re-read
    }
}
```

Create `tests/Winpepper.Platform.Tests/WindowContext/WindowContextStampTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextStampTests
{
    [Fact]
    public void NullConsumed_OmitsField() =>
        WindowContextStamp.CtxSrc(null, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBeNull();

    [Fact]
    public void NotConsumedInTime_IsNone_RegardlessOfWhatItLaterProduced() =>
        WindowContextStamp.CtxSrc(false, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBe("none");

    [Fact]
    public void ConsumedUia_IsUia() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBe("uia");

    [Fact]
    public void ConsumedOcr_IsOcr() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.FromOcr("x", 0.9))).ShouldBe("ocr");

    [Fact]
    public void ConsumedEmpty_IsNone() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.Empty)).ShouldBe("none");

    [Fact]
    public void ConsumedButCancelled_IsNone()
    {
        var cancelled = Task.FromCanceled<WindowContextResult>(new CancellationToken(canceled: true));
        WindowContextStamp.CtxSrc(true, cancelled).ShouldBe("none");
    }

    [Fact]
    public void NoTaskAtAll_IsNone_WhenARunnerSomehowReportsConsumption() =>
        WindowContextStamp.CtxSrc(true, null).ShouldBe("none");
}
```

Create `tests/Winpepper.IntegrationTests/WindowContextConsumedStampTests.cs`
(this is 1a(c): the REAL per-dictation-CTS creation path feeding a REAL
CleanupRunner, asserting the CONSUMED stamp — not merely non-empty prefetch
output):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.IntegrationTests;

public class WindowContextConsumedStampTests
{
    private sealed class EchoBackend : ILlamaCleanupBackend
    {
        public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
            string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
            => Task.FromResult(rawTranscript);
    }

    // 1a(c): exercises the real path where the per-dictation CTS is created
    // (coordinator.Start), and asserts the timing line's CONSUMED stamp reads
    // ctx_src=uia for a normal dictation. Guards the trap where a cancelled
    // token makes the prefetch quietly return empty: latency looks great,
    // context quality silently dies.
    [Fact]
    public async Task NormalDictation_RealCtsPath_ConsumedStampReadsUia()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) =>
            {
                ct.IsCancellationRequested.ShouldBeFalse(); // the fresh CTS must not be pre-cancelled
                return Task.FromResult(WindowContextResult.FromUia(new string('x', 400)));
            });
        coordinator.OnRecordingStart();
        var handle = coordinator.Start(new IntPtr(42));

        // Same projection PipelineHost uses to adapt the prefetch for the runner.
        var ctxTextTask = handle.Task.ContinueWith(
            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var runner = new CleanupRunner(new EchoBackend(), NullLogger<CleanupRunner>.Instance);
        var result = await runner.RunAsync(
            rawTranscript: "please clean up this perfectly ordinary transcript",
            corrections: CorrectionsData.Empty,
            windowContextTask: ctxTextTask,
            options: new CleanupOptions { Enabled = true, WindowContextEnabled = true },
            ct: CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(true);
        WindowContextStamp.CtxSrc(result.ConsumedWindowContext, handle.Task).ShouldBe("uia");
    }
}
```

Mismatch notes for this test file (adapt names, keep substance, record
corrections in the evidence file): `ILlamaCleanupBackend`'s method list, the
`CleanupRunner` ctor (backend, logger[, omitPromptExample]), and
`CleanupOptions` property names (`Enabled`, `WindowContextEnabled`) — read
them from `src/Winpepper.Cleanup/` and `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`,
which already constructs all three. If `Winpepper.IntegrationTests.csproj`
lacks `ProjectReference`s to `..\..\src\Winpepper.Platform\Winpepper.Platform.csproj`
or `..\..\src\Winpepper.Cleanup\Winpepper.Cleanup.csproj`, add them.

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with CS0246 for `WindowContextPrefetchCoordinator` /
`WindowContextStamp`.

- [ ] **Step 3: Implement the coordinator and the stamp**

Create `src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs`:

```csharp
namespace Winpepper.Platform.WindowContext;

/// <summary>One dictation's window-context prefetch: the task plus its
/// per-dictation cancellation. Created only by
/// <see cref="WindowContextPrefetchCoordinator.Start"/>.</summary>
public sealed class WindowContextPrefetchHandle
{
    private readonly CancellationTokenSource _cts;

    internal WindowContextPrefetchHandle(Task<WindowContextResult> task, CancellationTokenSource cts)
    {
        Task = task;
        _cts = cts;
        Token = cts.Token;
    }

    public Task<WindowContextResult> Task { get; }

    /// <summary>This dictation's own token — 1a(a)/(d): every dictation gets a
    /// DISTINCT CancellationTokenSource.</summary>
    public CancellationToken Token { get; }

    public bool CancellationRequested => Token.IsCancellationRequested;

    internal void Cancel()
    {
        if (!Task.IsCompleted) _cts.Cancel();
    }
}

/// <summary>1a: owns the window-context prefetch lifecycle after the move to
/// recording-stop. Per-dictation CancellationTokenSource, cancelled on
/// silence-drop and teardown (<see cref="CancelAndClear"/>) and — per the
/// approved plan's RULING — by the NEXT dictation's recording start
/// (<see cref="OnRecordingStart"/>): live speech wins over a stale context
/// fetch; the prior dictation takes the no-context path and stamps
/// ctx_src=none, an accepted, counted loss. Single caller (the serialized
/// hotkey loop) by contract — no locking. The CTSes carry no timers, so not
/// disposing them is benign.</summary>
public sealed class WindowContextPrefetchCoordinator
{
    private readonly Func<IntPtr, CancellationToken, Task<WindowContextResult>> _start;
    private WindowContextPrefetchHandle? _current;

    public WindowContextPrefetchCoordinator(
        Func<IntPtr, CancellationToken, Task<WindowContextResult>> start)
    {
        _start = start;
    }

    /// <summary>The latest launched prefetch, if any (null after
    /// <see cref="CancelAndClear"/> / <see cref="OnRecordingStart"/>).</summary>
    public WindowContextPrefetchHandle? Current => _current;

    /// <summary>Call at every recording START. Cancels a prior dictation's
    /// still-running prefetch (the 1a ruling); a completed one is left alone
    /// and merely cleared.</summary>
    public void OnRecordingStart()
    {
        var prior = _current;
        _current = null;
        prior?.Cancel();
    }

    /// <summary>Call at recording STOP: launch the prefetch against the
    /// window captured at recording start — 1a(b): never re-read focus at
    /// stop, or a mid-recording focus change feeds the WRONG window's content
    /// to the cleanup model.</summary>
    public WindowContextPrefetchHandle Start(IntPtr hwndAtStart)
    {
        var cts = new CancellationTokenSource();
        var handle = new WindowContextPrefetchHandle(_start(hwndAtStart, cts.Token), cts);
        _current = handle;
        return handle;
    }

    /// <summary>Call on silence-drop, session cancel, and teardown — 1a(a):
    /// without this, every silence-dropped dictation would leave a full OCR
    /// burst running.</summary>
    public void CancelAndClear()
    {
        var prior = _current;
        _current = null;
        prior?.Cancel();
    }
}
```

Create `src/Winpepper.Platform/WindowContext/WindowContextStamp.cs`:

```csharp
namespace Winpepper.Platform.WindowContext;

/// <summary>Pure consume-time ctx_src mapping for the dictation timing line
/// (0b semantics), extracted from PipelineHost's two duplicated arms so the
/// 1a cancellation policy is Linux-testable end to end. "none" whenever the
/// prefetch was not complete when CleanupRunner stopped waiting (consumed ==
/// false) or completed cancelled/faulted/empty; otherwise the Source.</summary>
public static class WindowContextStamp
{
    public static string? CtxSrc(bool? consumedWindowContext, Task<WindowContextResult>? prefetchTask)
        => consumedWindowContext switch
        {
            null => null,   // no context task supplied/enabled -> omit the field
            false => "none",
            true => prefetchTask is { IsCompletedSuccessfully: true } done
                ? done.Result.Source switch
                {
                    WindowContextSource.Uia => "uia",
                    WindowContextSource.Ocr => "ocr",
                    _ => "none",
                }
                : "none",
        };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
dotnet build tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.IntegrationTests/bin/Release/net9.0/Winpepper.IntegrationTests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0` in both runs.

- [ ] **Step 5: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs src/Winpepper.Platform/WindowContext/WindowContextStamp.cs tests/Winpepper.Platform.Tests/WindowContext/ tests/Winpepper.IntegrationTests/
git commit -m "feat(platform): window-context prefetch coordinator with per-dictation cancellation + pure ctx_src stamp" \
  -m "1a design details (a)/(b)/(d) as pure Linux-tested policy: per-dictation CTS, cancel on silence-drop/teardown, new-recording-start cancels a prior still-running prefetch (ruling), hwnd captured at start. Includes the two named race tests and the 1a(c) consumed-stamp ctx_src=uia test." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 9: D1 (wiring half) — move the prefetch launch from recording-start to recording-stop in BOTH PipelineHost arms

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`:
  fields :56-58; ctor :128-129; hold start ~:524-536; hold stop ~:551 (after
  Task 3's block); hold consumption ~:748-756 and ctx_src ~:793-805; hold
  silence-drop :595; hold teardown :982; Cancel case :985-996; toggle mirrors
  (~:1044-1056, ~:1071, ~:1264-1272, ~:1309-1321, :1114, :1496, and the
  toggle-side cancel/teardown).

**Interfaces:**
- Consumes: Task 8's `WindowContextPrefetchCoordinator`,
  `WindowContextPrefetchHandle`, `WindowContextStamp`.
- Produces: no new surface; behavior change 1a.

This file is Windows-only; correctness of the policy is proven by Task 8's
Linux tests, compilation by Task 11's gate. Steps: edit → suite → commit.

- [ ] **Step 1: Replace the field and ctor wiring**

Replace the two fields (:57-58)

```csharp
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetch? _windowContext; // PLAN2-TYPE
    private Task<Winpepper.Platform.WindowContext.WindowContextResult>? _ctxPrefetchTask;    // PLAN2-TYPE
```

with

```csharp
    // 1a: prefetch launched at recording STOP; lifecycle (per-dictation CTS,
    // cancellation ruling) owned by the coordinator. Hwnd captured at START.
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetchCoordinator? _ctxCoordinator;
    private IntPtr _ctxHwndAtStart = IntPtr.Zero;
```

and in the ctor replace `_windowContext = windowContext;` (:129) with

```csharp
        _ctxCoordinator = windowContext is null
            ? null
            : new Winpepper.Platform.WindowContext.WindowContextPrefetchCoordinator(windowContext.StartAsync);
```

Fix any other `_windowContext` / `_ctxPrefetchTask` references the compiler
finds — after this task there must be ZERO references to either
(`grep -n "_ctxPrefetchTask\|_windowContext" src/Winpepper.App/Hosting/PipelineHost.cs`
returns nothing).

- [ ] **Step 2: Recording START (both arms) — cancel prior, capture hwnd, do NOT launch**

Hold arm: replace the whole launch block (:524-536, the comment through the
`if (...) { var hwnd = ...; _ctxPrefetchTask = ...; }` braces) with:

```csharp
                // 1a ruling: a prior dictation's still-running prefetch dies
                // the moment new speech starts — live speech wins over a
                // stale context fetch; the prior dictation stamps
                // ctx_src=none, an accepted counted loss.
                _ctxCoordinator?.OnRecordingStart();
                // 1a(b): capture the dictated-into window NOW; the relocated
                // prefetch (at stop) must read THIS window, not whatever has
                // focus by then.
                _ctxHwndAtStart = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
```

(Keep `_targetAtStart = CaptureTarget();` untouched immediately above. If
`InjectionTarget` already exposes the window handle, use
`_targetAtStart.<handle-member>` instead of the new capture and record that in
the evidence file — the binding substance is "captured at recording start".)

Toggle arm (:1044-1056): identical replacement. Delete the now-unused
`settingsAtStart` / `settingsAtStart2` locals if nothing else reads them.

- [ ] **Step 3: Recording STOP (both arms) — launch via the coordinator**

Hold arm: immediately after Task 3's resource-stamping block (which follows
`timing.ProcCpuMs = ...` at ~:551), add:

```csharp
                // 1a: launch the window-context prefetch AT STOP — it now
                // overlaps mic-stop, trimming, and transcription finish
                // instead of competing with live streaming ASR. Gated on LIVE
                // settings so a Cleanup-tab change applies to this dictation.
                Winpepper.Platform.WindowContext.WindowContextPrefetchHandle? ctxPrefetch = null;
                var settingsAtStop = _settingsProvider();
                if (_ctxCoordinator is not null
                    && settingsAtStop.CleanupEnabled
                    && settingsAtStop.CleanupWindowContextEnabled)
                {
                    ctxPrefetch = _ctxCoordinator.Start(_ctxHwndAtStart);
                }
```

Toggle arm: same block after its resource stamping (~:1071), locals
`ctxPrefetch2` / `settingsAtStop2`.

- [ ] **Step 4: Consumption + stamping (both arms) — use the local handle and the pure stamp**

Hold arm consumption (:748-756): replace

```csharp
                      Task<string?>? ctxTextTask = null;
                      if (_ctxPrefetchTask is not null)
                      {
                          ctxTextTask = _ctxPrefetchTask.ContinueWith(
                              t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                              ct,
                              TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
                      }
```

with

```csharp
                      Task<string?>? ctxTextTask = null;
                      if (ctxPrefetch is not null)
                      {
                          ctxTextTask = ctxPrefetch.Task.ContinueWith(
                              t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                              ct,
                              TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);
                      }
```

Hold arm ctx_src stamping (:790-805): replace the whole
`timing.CtxSrc = result.ConsumedWindowContext switch { ... };` expression
(including its `// 0b consume-time ctx_src:` comment) with:

```csharp
                          // 0b consume-time ctx_src, via the pure Linux-tested
                          // stamp (see WindowContextStamp).
                          timing.CtxSrc = Winpepper.Platform.WindowContext.WindowContextStamp.CtxSrc(
                              result.ConsumedWindowContext, ctxPrefetch?.Task);
```

Toggle arm: mirror both replacements (:1264-1272 uses `ctxPrefetch2`,
`ctxTextTask2`; :1306-1321 stamps `timing2.CtxSrc` from
`result2.ConsumedWindowContext` and `ctxPrefetch2?.Task`).

- [ ] **Step 5: Cancellation sites (both arms) — silence-drop, teardown, Cancel**

- Hold silence-drop: replace `_ctxPrefetchTask = null;` (:595) with
  `_ctxCoordinator?.CancelAndClear();`
- Hold normal teardown: replace `_ctxPrefetchTask = null;` (:982) with
  `// prefetch handle is per-dictation (local); the coordinator's reference is
  // cleared by the next OnRecordingStart.` (i.e. just delete the line, keep a
  one-line comment).
- Cancel case (:985-996): after `_engine.Apply(SessionEvent.CancelRequested);`
  add `_ctxCoordinator?.CancelAndClear();` — 1a(a) teardown cancel (this also
  fixes the pre-existing bug where Cancel never cleared the prefetch).
- Toggle mirrors: silence-drop :1114 and teardown :1496 get the same
  treatment; if the toggle arm has its own cancel/teardown branch, add
  `_ctxCoordinator?.CancelAndClear();` there too. Any OTHER site that assigns
  `_ctxPrefetchTask` (the Step 1 grep finds them all) becomes either
  `CancelAndClear()` (abandonment paths) or a deletion (post-consumption
  clears).

- [ ] **Step 6: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): move window-context prefetch from recording start to stop (both arms)" \
  -m "1a: prefetch no longer competes with live streaming ASR; it overlaps mic-stop/trim/finish and CleanupRunner's 500 ms wait. Per-dictation CTS via WindowContextPrefetchCoordinator; cancelled on silence-drop, Cancel, and by the next dictation's recording start (ruling); hwnd captured at start per 1a(b); ctx_src stamped via the pure WindowContextStamp." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 10: E1 — wedge-cascade fix: route dictations to batch while an abandoned wedged stream still holds the compute gate

**Files:**
- Create: `src/Winpepper.Asr/Transcription/StreamingRouteGuard.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/StreamingRouteGuardTests.cs`
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (streaming-session
  creation ~:480-502 hold / ~:1000-1033 toggle; the
  `finally { ... NoteStreamingReleased(streaming); }` blocks ~:524-531 hold /
  toggle clone ~:1139-1219 region)

**Interfaces:**
- Consumes: `StreamingDictationSession.DrainTimedOut` (bool) and
  `StreamingDictationSession.PumpCompletion` (Task) — both existing.
- Produces: `StreamingRouteGuard` with `void NoteAbandoned(Task pumpCompletion)`,
  `void NoteDisposeOutcome(bool drainTimedOut, Task pumpCompletion)` (the
  single post-dispose predicate every dispose site calls), and
  `bool TryClaimStreaming(out string? blockReason)`.

The invariants this preserves: callers stay serialized (the guard is touched
only from the hotkey loop); the batch fallback contract is untouched — a
dictation with no streaming session simply takes the EXISTING late batch path
(`maybeTranscription is null` branch), exactly as when streaming is disabled by
settings.

What `PumpCompletion` actually is (restated honestly, 2026-07-30 validation):
a CONSERVATIVE, ONE-SIDED-SAFE over-approximation of compute-gate
availability — NOT an exact proxy. It may block-to-batch while the gate is
actually free (e.g. a pump stuck in the transcriber factory /
`StartSessionAsync` before any stream exists, or a wedged socket transcriber
— acceptable: batch degradation, never a hang). It NEVER clears while the
gate is held for the targeted nemotron wedge: pump-incomplete is NECESSARY
for a wedged-call-held gate on that path, and the pump-complete →
gate-release window is milliseconds (thread-pool scheduling + monitor handoff
+ one `transcribe_session_free`), absorbed by BeginStream's 5 s gate timeout.
`NativeStream.Dispose` releases the gate in a `finally`, so even a THROWING
native free cannot skip the release.

Coverage gap closed by design (2026-07-30): the cancel, silence-drop, and
teardown paths call the session's `DisposeAsync` WITHOUT `FinishAsync`;
`DisposeAsync` waits the pump only ~5 s and then deliberately orphans it —
with `DrainTimedOut == false`. A wedged gate-holding pump orphaned that way
would bypass a DrainTimedOut-only `NoteAbandoned` and the next dictation
would stream straight into the 5 s gate timeout — the exact cascade E1 exists
to kill, entering via cancel. Fix: note the abandon whenever ANY
`DisposeAsync` returns with `PumpCompletion` still incomplete, regardless of
`DrainTimedOut` — that is `NoteDisposeOutcome`, wired at EVERY dispose site.

Residual (accepted; log-instrumented, not provable statically):
`transcribe_session_free` returning promptly once the wedged compute call has
returned is assumed — a hung free would permanently false-clear nothing here
(the pump stays incomplete) but WOULD hold the gate forever; the `stream
dispose` TimedNativeCall 3 s still-running WRN is the field instrument.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/Transcription/StreamingRouteGuardTests.cs`:

```csharp
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class StreamingRouteGuardTests
{
    [Fact]
    public void NoAbandon_StreamingIsAllowed()
    {
        var guard = new StreamingRouteGuard();
        Assert.True(guard.TryClaimStreaming(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void AbandonedPumpStillWedged_RoutesToBatch_WithReason()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);

        Assert.False(guard.TryClaimStreaming(out var reason));
        Assert.NotNull(reason);
        Assert.Contains("drain timeout", reason);
    }

    [Fact]
    public void AbandonedPumpCompleted_StreamingResumes_AndStaysResumed()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);
        wedged.SetResult(); // the wedged native call finally returned

        Assert.True(guard.TryClaimStreaming(out _));
        Assert.True(guard.TryClaimStreaming(out _)); // cleared permanently
    }

    [Fact]
    public void AbandonedPumpFaulted_CountsAsCompleted_StreamingResumes()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);
        wedged.SetException(new InvalidOperationException("pump error"));

        Assert.True(guard.TryClaimStreaming(out _)); // the call RETURNED; gate is releasable
    }

    [Fact]
    public void SecondAbandon_LatestWedgeWins()
    {
        var guard = new StreamingRouteGuard();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        guard.NoteAbandoned(first.Task);
        guard.NoteAbandoned(second.Task);
        first.SetResult();

        Assert.False(guard.TryClaimStreaming(out _)); // still blocked on the latest wedge
        second.SetResult();
        Assert.True(guard.TryClaimStreaming(out _));
    }

    // E1 coverage-gap fix: cancel/silence-drop/teardown call DisposeAsync
    // WITHOUT FinishAsync, so DrainTimedOut stays false while a wedged
    // gate-holding pump is orphaned — the abandon must key off
    // PumpCompletion-incomplete, not DrainTimedOut alone.
    [Fact]
    public void DisposeOutcome_PumpIncompleteWithoutDrainTimeout_RoutesToBatch()
    {
        var guard = new StreamingRouteGuard();
        var orphaned = new TaskCompletionSource();
        guard.NoteDisposeOutcome(drainTimedOut: false, orphaned.Task); // the cancel-path orphan

        Assert.False(guard.TryClaimStreaming(out var reason));
        Assert.NotNull(reason);
        orphaned.SetResult(); // the wedged call finally returned
        Assert.True(guard.TryClaimStreaming(out _));
    }

    [Fact]
    public void DisposeOutcome_PumpCompleteWithoutDrainTimeout_DoesNotBlock()
    {
        var guard = new StreamingRouteGuard();
        guard.NoteDisposeOutcome(drainTimedOut: false, Task.CompletedTask); // healthy dispose

        Assert.True(guard.TryClaimStreaming(out _));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with CS0246 ("'StreamingRouteGuard' could not be found").

- [ ] **Step 3: Implement the guard**

Create `src/Winpepper.Asr/Transcription/StreamingRouteGuard.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>E1 wedge-cascade breaker. After a drain-timeout abandon, the
/// abandoned stream's native dispose queues BEHIND the wedged call while the
/// engine-wide compute gate stays held — so the NEXT dictation's BeginStream
/// would block up to 5 s on the gate and then batch-fallback anyway, turning
/// one wedged native call into a multi-dictation hang. This guard routes
/// subsequent dictations straight to the existing batch path until the
/// abandoned pump's completion has ACTUALLY completed. PumpCompletion is a
/// CONSERVATIVE, ONE-SIDED-SAFE over-approximation of gate availability, not
/// an exact proxy: it can block-to-batch while the gate is free (factory/
/// StartSessionAsync stalls, socket transcribers — batch degradation, never a
/// hang) but never clears while the gate is held for the targeted nemotron
/// wedge; the pump-complete → gate-release window is milliseconds and is
/// absorbed by BeginStream's 5 s gate timeout (NativeStream.Dispose releases
/// the gate in a finally — throw-proof). Pure decision logic; touched only
/// from the serialized hotkey loop, so no locking. Linux-tested.</summary>
public sealed class StreamingRouteGuard
{
    private Task? _abandonedPump;

    /// <summary>Record a drain-timeout abandon. The latest wedge wins:
    /// streaming resumes only when the most recent abandoned pump completes.</summary>
    public void NoteAbandoned(Task pumpCompletion) => _abandonedPump = pumpCompletion;

    /// <summary>Call after EVERY streaming-session DisposeAsync returns —
    /// finish finally, cancel, silence-drop, teardown. Notes the abandon when
    /// the drain timed out OR the pump is still incomplete: the cancel-path
    /// dispose orphans a wedged gate-holding pump after ~5 s with
    /// DrainTimedOut still false, and keying off DrainTimedOut alone would
    /// let the wedge cascade re-enter via cancel.</summary>
    public void NoteDisposeOutcome(bool drainTimedOut, Task pumpCompletion)
    {
        if (drainTimedOut || !pumpCompletion.IsCompleted)
            NoteAbandoned(pumpCompletion);
    }

    /// <summary>True when streaming may start for the next dictation. False
    /// (with a loggable reason) while a previously abandoned pump is still
    /// stuck inside a native call; a completed (or faulted — the call
    /// RETURNED either way) pump clears the block permanently.</summary>
    public bool TryClaimStreaming(out string? blockReason)
    {
        var pump = _abandonedPump;
        if (pump is null || pump.IsCompleted)
        {
            _abandonedPump = null;
            blockReason = null;
            return true;
        }
        blockReason = "a prior streaming session was abandoned on drain timeout and its wedged native call has not returned; routing to the batch path instead of blocking on the compute gate";
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0`.

- [ ] **Step 5: Wire PipelineHost (both arms)**

Add a field near the other per-pipeline state:

```csharp
    // E1: routes dictations to batch while an abandoned wedged stream still
    // holds the compute gate. Hotkey-loop-only by contract.
    private readonly Winpepper.Asr.Transcription.StreamingRouteGuard _routeGuard = new();
```

Hold arm, streaming-session creation (~:480-497): wrap the existing creation in
the guard, logging the routing decision at INF with the reason:

```csharp
                var settingsForStream = _settingsProvider();
                if (settingsForStream.StreamingEnabled)
                {
                    if (_routeGuard.TryClaimStreaming(out var routeBlockReason))
                    {
                        _streamingSession = Winpepper.Asr.Transcription.StreamingDictationSession.Start(
                            /* ... existing arguments UNCHANGED ... */);
                    }
                    else
                    {
                        // E1: leave _streamingSession null — the existing
                        // late batch path takes over at stop, same as when
                        // streaming is disabled by settings.
                        _log.LogInformation(
                            "streaming routed to batch for this dictation: {Reason}", routeBlockReason);
                    }
                }
                else
                {
                    _log.LogDebug("streaming disabled by settings; batch transcription will run at stop");
                }
```

(The `StreamingDictationSession.Start(...)` argument list is the existing code
verbatim — only the `if (_routeGuard...)` wrapper and the `else` INF log are
new.) Toggle arm (~:1000-1033): identical wrapper, `routeBlockReason2`.

Hold arm, the finish `finally` (~:524-531): extend

```csharp
                    finally
                    {
                        await streaming.DisposeAsync();
                        NoteStreamingReleased(streaming);
                    }
```

to

```csharp
                    finally
                    {
                        await streaming.DisposeAsync();
                        NoteStreamingReleased(streaming);
                        // E1: a drain-timeout abandon leaves the wedged pump
                        // holding the compute gate via the queued dispose —
                        // route later dictations to batch until it completes.
                        // NoteDisposeOutcome also catches the orphan case
                        // (pump still incomplete with DrainTimedOut false).
                        _routeGuard.NoteDisposeOutcome(
                            streaming.DrainTimedOut, streaming.PumpCompletion);
                    }
```

Toggle arm: same extension in its clone.

Then close the CANCEL-PATH coverage gap: at EVERY OTHER site that disposes a
streaming session without `FinishAsync` — the Cancel case (~:985-995), the
silence-drop path (~:596-602; toggle clone ~:1115-1120), and teardown
(~:1652-1657), in BOTH arms (find them all with
`grep -n "DisposeSessionAsync\|_streamingSession" src/Winpepper.App/Hosting/PipelineHost.cs`
plus any dispose helper they share) — add the same call immediately after the
dispose completes:

```csharp
                // E1 coverage gap: cancel/silence-drop/teardown orphan a
                // wedged pump after ~5 s with DrainTimedOut still false; a
                // gate-holding orphan must still arm the batch routing or the
                // cascade re-enters via cancel.
                _routeGuard.NoteDisposeOutcome(session.DrainTimedOut, session.PumpCompletion);
```

(adapting the local variable name per site; if the arms share one
dispose-session helper, put the call there ONCE and note that in the evidence
file).

- [ ] **Step 6: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Asr/Transcription/StreamingRouteGuard.cs tests/Winpepper.Asr.Tests/Transcription/StreamingRouteGuardTests.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(asr): route dictations to batch while an abandoned wedged stream still holds the compute gate" \
  -m "E1: after a DrainTimedOut abandon the next BeginStream blocked up to 5 s on the gate and batch-degraded anyway — one wedge became a multi-dictation hang. StreamingRouteGuard (pure, Linux-tested) skips streaming until the abandoned pump completes (a conservative one-sided-safe over-approximation of gate availability); NoteDisposeOutcome also arms the guard when cancel/silence-drop/teardown orphan a still-incomplete pump with DrainTimedOut false, closing the cancel-path cascade entrance. Routing decision logged at INF with the reason; batch fallback contract and serialized-caller invariants unchanged." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 11: E2 — abandon the drain immediately when the in-flight native call has already exceeded the drain budget

**Files:**
- Create: `src/Winpepper.Asr/Transcription/DrainAbandonPolicy.cs`
- Modify: `src/Winpepper.Asr/Transcription/NativeCallStats.cs` (add
  `INativeCallInFlightSource` beside `INativeCallStatsSource` ~:32-36)
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
  (`TimedNativeCall` and `EnsureStream` — the POST-Task-4 shape of both)
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`
  (`FinishAsync` ~:237-315; the drain wait is the single
  `await _pump.WaitAsync(deadline, ct)` at ~:253)
- Create: `tests/Winpepper.Asr.Tests/Transcription/DrainAbandonPolicyTests.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: Task 4's post-change `TimedNativeCall`/`EnsureStream` in
  `NemotronStreamingTranscriber.Session`; the existing drain machinery in
  `StreamingDictationSession.FinishAsync` (`_drainDeadline` ~:54 assigned :100,
  effective `deadline` computed ~:247-249, abandon block ~:255-279,
  `ScheduleAbandonedSessionDispose()` ~:326-332, 5-arg
  `StreamingFinishStats(asrWaitMs, asrNativeMs, backlogFrames, backlogMs,
  nativeCallStats)`).
- Produces: `INativeCallInFlightSource` with
  `TimeSpan? NativeCallInFlightElapsed { get; }` (lock-free, null when no
  native call is in flight) and
  `DrainAbandonPolicy.ShouldAbandonImmediately(TimeSpan? inFlightElapsed,
  TimeSpan drainBudget) : bool`.
- E1 interplay (no extra wiring): the early abandon sets the SAME
  `DrainTimedOut` flag as the timeout abandon, so Task 10's
  `StreamingRouteGuard.NoteDisposeOutcome(...)` call in PipelineHost (which
  notes the abandon on `DrainTimedOut` OR an incomplete pump) fires for this
  path too, unchanged. This coupling is also why the A15 futility floor below
  matters: a wrong early abandon would arm E1's blockade off a non-wedge.

Why: every wedged batch fallback today shows `asr_wait` pegged at
10013-10024 ms — `FinishAsync` waits out the full 10 s deadline even when the
in-flight native call had ALREADY been running longer than the whole budget at
stop time, so the drain could not possibly complete. Total cost 13-18 s per
dictation. A native call cannot be aborted; the win is to stop WAITING for it
when the wait is provably futile. Expected saving: up to ~10 s per
E2-triggered dictation — an UPPER BOUND: the contention stretch of the
concurrent batch is unmeasured (field-bounded ≤ ~2×; Task 12's owner
comparison quantifies it).

Two hard constraints (load-bearing, from the code):
1. The probe must be LOCK-FREE. The existing
   `INativeCallStatsSource.NativeCallStats` snapshot takes the session's
   `_nativeGate` — the very lock the wedged call is holding — which is why
   `FinishAsync`'s abandon path explicitly never probes it
   (`StreamingDictationSession.cs` ~:268-269). The new interface reads ONLY a
   volatile field.
2. Wrapper transparency degrades safely: the coordinator probes its
   `_session` with a soft cast (`as INativeCallInFlightSource`), exactly like
   the existing `as INativeCallStatsSource` probe at ~:312. A wrapper session
   that does not implement/forward the interface yields null → no early
   abandon → today's behavior.

- [ ] **Step 1: Write the failing pure-policy tests**

Create `tests/Winpepper.Asr.Tests/Transcription/DrainAbandonPolicyTests.cs`
(match the namespace of the neighboring test files in that folder):

```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class DrainAbandonPolicyTests
{
    [Fact]
    public void NoCallInFlight_NeverAbandonsEarly()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            null, TimeSpan.FromSeconds(10)).ShouldBeFalse();

    [Fact]
    public void InFlightBelowBudget_WaitsOutTheDeadline()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(9.9), TimeSpan.FromSeconds(10)).ShouldBeFalse();

    [Fact]
    public void InFlightAtBudget_AbandonsImmediately()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)).ShouldBeTrue();

    [Fact]
    public void InFlightFarPastBudget_AbandonsImmediately()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(10)).ShouldBeTrue();

    // A15 pins: `elapsed >= budget` bounds the PAST, not the REMAINING time.
    // With the zero-push shortcut the effective deadline shrinks to ~1.5 s;
    // healthy 2.9-3.96 s calls (observed and RECOVERED in the field) must
    // never be abandoned there — abandon iff
    // elapsed >= max(effectiveDeadline, MinInFlightForFutility).
    [Fact]
    public void ShortEffectiveDeadline_HealthyClassCallInFlight_DoesNotAbandon()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(1.5)).ShouldBeFalse();

    [Fact]
    public void ShortEffectiveDeadline_RecoveredClassCall_DoesNotAbandon()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1.5)).ShouldBeFalse(); // native_max=3960 ms was a recovered dictation

    [Fact]
    public void ShortEffectiveDeadline_PastTheFutilityFloor_Abandons()
        => DrainAbandonPolicy.ShouldAbandonImmediately(
            TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(1.5)).ShouldBeTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS — `DrainAbandonPolicy` does not exist.

- [ ] **Step 3: Implement the pure policy + the lock-free interface**

Create `src/Winpepper.Asr/Transcription/DrainAbandonPolicy.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>E2: the drain's early-abandon decision, pure so it is
/// Linux-testable. A wedged native call cannot be aborted; when the CURRENT
/// in-flight call has already been running at least as long as the FULL
/// drain budget, waiting the budget out buys nothing — the pump cannot
/// possibly drain in time. Abandon immediately so the caller's late batch
/// path starts up to a full deadline sooner (observed pre-fix: asr_wait
/// pegged at 10013-10024 ms on every wedged batch fallback; 13-18 s total
/// per dictation; the saving is an upper bound — contention stretch of the
/// concurrent batch unmeasured, field-bounded ≤ ~2×).
/// Semantics: abandon iff inFlightElapsed >= max(effectiveDeadline,
/// MinInFlightForFutility). `elapsed >= effectiveDeadline` alone is NOT a
/// futility proof — it bounds the PAST, not the REMAINING time — and the
/// zero-push shortcut shrinks the effective deadline to ~1.5 s, where
/// healthy 2.9-3.96 s calls (observed and recovered in the field) would be
/// wrongly abandoned AND arm E1's blockade off a non-wedge.</summary>
public static class DrainAbandonPolicy
{
    /// <summary>Futility floor == the full 10 s drain budget: >= the 5 s
    /// compute-gate timeout + the observed healthy native max (~4 s), while
    /// real wedges run >= 15 s. Since every effective deadline is <= the
    /// full budget, this reduces to full-budget-only in production — E2
    /// fires exactly on its motivating evidence (in-flight >= 10 s at stop)
    /// and never on the zero-push 1.5 s shortcut (whose max waste is 1.5 s
    /// regardless).</summary>
    public static readonly TimeSpan MinInFlightForFutility = TimeSpan.FromSeconds(10);

    public static bool ShouldAbandonImmediately(
        TimeSpan? inFlightElapsed, TimeSpan drainBudget)
        => inFlightElapsed is { } elapsed
            && elapsed >= drainBudget
            && elapsed >= MinInFlightForFutility;
}
```

In `src/Winpepper.Asr/Transcription/NativeCallStats.cs`, next to
`INativeCallStatsSource` (~:32-36), add:

```csharp
/// <summary>E2: LOCK-FREE view of the CURRENT in-flight native call, for the
/// drain's early-abandon decision. MUST never take the session's native gate
/// (contrast <see cref="INativeCallStatsSource.NativeCallStats"/>, whose
/// snapshot does): the consumer probes it precisely when a wedged native call
/// may be holding that gate. Null when no native call is in flight.</summary>
public interface INativeCallInFlightSource
{
    TimeSpan? NativeCallInFlightElapsed { get; }
}
```

- [ ] **Step 4: Run the policy tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0` (all four new policy tests pass).

- [ ] **Step 5: Write the failing transcriber test (in-flight elapsed is observable while wedged)**

In `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`,
mirror the construction of the existing watchdog test
`Wedged_native_call_logs_a_still_running_warning_before_it_returns` (~:210-241
— `FakeTranscribeCppEngine.FeedGate` is the deterministic native wedge; adapt
helper names per the mismatch rule):

```csharp
    [Fact]
    public async Task Wedged_native_call_exposes_in_flight_elapsed_lock_free()
    {
        using var gate = new ManualResetEventSlim(false);
        var engine = new FakeTranscribeCppEngine { FeedGate = gate };
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en",
            new CapturingLogger(), nativeCallWarnAfter: TimeSpan.FromSeconds(30));
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);
        var inFlight = (INativeCallInFlightSource)s; // cast fails until Step 6

        var push = Task.Run(() => s.PushAsync(Samples(2560),
            TestContext.Current.CancellationToken).AsTask()); // exactly one native feed, wedged on the gate

        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (inFlight.NativeCallInFlightElapsed is null && DateTime.UtcNow < giveUp)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.NotNull(inFlight.NativeCallInFlightElapsed); // observable WHILE the call is wedged
        gate.Set(); // unwedge the native call

        await push;
        Assert.Null(inFlight.NativeCallInFlightElapsed); // cleared once the call returned
    }
```

And the A16 pin — gate-wait-only elapsed must NEVER read as in-flight native
time (add `public ManualResetEventSlim? BeginStreamGate { get; set; }` to
`FakeTranscribeCppEngine` with `BeginStreamGate?.Wait();` at the top of its
`BeginStream`, mirroring the existing `FeedGate` pattern):

```csharp
    [Fact]
    public async Task Blocked_BeginStream_does_not_report_in_flight_elapsed()
    {
        // A16: the in-flight marker is deliberately NOT armed across
        // EnsureStream/BeginStream — BeginStream's ≤5 s compute-gate wait
        // would otherwise read as native in-flight time exactly in the
        // zero-push phase (EnsureStream runs on the FIRST push). While the
        // fake's BeginStream is blocked (stand-in for a pure gate wait), the
        // probe must stay null.
        using var beginGate = new ManualResetEventSlim(false);
        var engine = new FakeTranscribeCppEngine { BeginStreamGate = beginGate };
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en",
            new CapturingLogger(), nativeCallWarnAfter: TimeSpan.FromSeconds(30));
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);
        var inFlight = (INativeCallInFlightSource)s;

        var push = Task.Run(() => s.PushAsync(Samples(2560),
            TestContext.Current.CancellationToken).AsTask()); // first push -> EnsureStream -> blocked BeginStream

        await Task.Delay(300, TestContext.Current.CancellationToken); // let the push reach BeginStream
        Assert.Null(inFlight.NativeCallInFlightElapsed); // gate-wait/begin time is NOT in-flight native time
        beginGate.Set();
        await push;
        Assert.Null(inFlight.NativeCallInFlightElapsed);
    }
```

Run (same two commands as Step 4). Expected: the new tests FAIL — the cast to
`INativeCallInFlightSource` throws `InvalidCastException` (the session does
not implement it yet).

- [ ] **Step 6: Implement the in-flight marker in the transcriber**

In `NemotronStreamingTranscriber.Session` (post-Task-4 shape):

1. Add `INativeCallInFlightSource` to the class's interface list (it already
   implements `IStreamingTranscriptionSession` and `INativeCallStatsSource`).

2. Add the field and property:

```csharp
        /// <summary>Environment.TickCount64 when the CURRENT native call
        /// started; 0 = no call in flight. Volatile, never under _nativeGate:
        /// the drain's early-abandon probe reads it while a wedged call may
        /// be HOLDING that gate (see INativeCallInFlightSource).</summary>
        private long _inFlightSinceTick;

        public TimeSpan? NativeCallInFlightElapsed
        {
            get
            {
                var since = Volatile.Read(ref _inFlightSinceTick);
                return since == 0
                    ? null
                    : TimeSpan.FromMilliseconds(Environment.TickCount64 - since);
            }
        }
```

3. In `TimedNativeCall`, immediately after the existing
   `var startTick = Environment.TickCount64;` line, add:

```csharp
            Volatile.Write(ref _inFlightSinceTick, Math.Max(1, startTick)); // Max(1): 0 is the "no call" sentinel
```

   and add this as the FIRST line of its `finally` block (before
   `watchdogCts.Cancel();`):

```csharp
                Volatile.Write(ref _inFlightSinceTick, 0);
```

4. Do NOT arm the marker in `EnsureStream` (A16, falsified 2026-07-30 — this
   is deliberate, not an omission): `EnsureStream` stamps its start tick
   BEFORE `engine.BeginStream`, whose ≤5 s compute-gate wait happens FIRST
   inside the engine — so a marker there would count pure queueing as native
   in-flight time, and it would do so exactly in the zero-push phase
   (`EnsureStream` runs on the FIRST push, precisely when the effective
   deadline is shortest). Task 4's per-call `out gateWaitMs` cannot help the
   LIVE probe: it exists only after the call returns. The marker lives ONLY
   in `TimedNativeCall`, whose ops — feed/finalize/dispose — never wait on
   the compute gate (the stream already holds it). Cost: a wedged
   `BeginStream` gets no early abandon and degrades safely to today's
   full-deadline behavior (never a wrong abandon; begin-wedges were not the
   motivating evidence).

5. Wrapper transparency: run
   `grep -rn "INativeCallStatsSource" src/ tests/`. For every WRAPPER session
   class that implements or forwards `INativeCallStatsSource` to an inner
   session (e.g. `FallbackStreamingTranscriber`'s session, if it does),
   forward `INativeCallInFlightSource` in exactly the same way (implement the
   interface, delegate the property to
   `(inner as INativeCallInFlightSource)?.NativeCallInFlightElapsed`). If a
   wrapper does NOT forward the stats interface, add nothing there — the
   coordinator's soft cast degrades to null (today's behavior) by design.

Run (same two commands as Step 4). Expected: `Errors: 0`, `Failed: 0` —
including the Step 5 test and all pre-existing transcriber tests.

- [ ] **Step 7: Write the failing session-level tests (immediate abandon + no-early-abandon guard)**

In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`, add a fake
alongside the existing wedge fakes (copy the `WedgesOnSecondPushTranscriber`
first-push-completes pattern at ~:498-521 so the ZERO-PUSH 1.5 s shortcut
provably does NOT apply — the FULL deadline must be in force for the test to
mean anything):

```csharp
    // E2: streaming genuinely underway (first push completed), then the
    // second push wedges INSIDE a native call that reports it has ALREADY
    // been running longer than the drain budget — the drain must abandon
    // immediately instead of waiting out the full deadline.
    private sealed class InFlightPastBudgetTranscriber : IStreamingTranscriber
    {
        public string ModelName => "in-flight-past-budget";
        public WedgingSession Session { get; } = new();
        public Task<IStreamingTranscriber?> Self() => Task.FromResult<IStreamingTranscriber?>(this);
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class WedgingSession : IStreamingTranscriptionSession, INativeCallInFlightSource
        {
            private readonly TaskCompletionSource _wedge = new();
            private readonly TaskCompletionSource _wedgedPushStarted = new();
            private int _pushes;

            /// <summary>What the wedged "native call" claims its elapsed is.</summary>
            public TimeSpan? InFlightToReport { get; set; } = TimeSpan.FromSeconds(60);

            /// <summary>Which push wedges. Default 2: the first push COMPLETES,
            /// so the zero-push short deadline is NOT what bounds those tests.
            /// Set 1 for the zero-completed-pushes (1.5 s shortcut) pairing.</summary>
            public int WedgeOnPush { get; set; } = 2;

            /// <summary>Completes when the wedging push has started.</summary>
            public Task WedgedPushStarted => _wedgedPushStarted.Task;

            public TimeSpan? NativeCallInFlightElapsed
                => _wedgedPushStarted.Task.IsCompleted ? InFlightToReport : null;

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _pushes) < WedgeOnPush) return; // earlier pushes succeed
                _wedgedPushStarted.TrySetResult();
                await _wedge.Task; // the wedged native feed
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on an abandoned session");

            public ValueTask DisposeAsync()
            {
                _wedge.TrySetResult(); // socket-style: dispose unwedges the push
                return ValueTask.CompletedTask;
            }
        }
    }
```

(Adapt member lists to `IStreamingTranscriber`/`IStreamingTranscriptionSession`
exactly as the neighboring fakes in this file do — mismatch rule; e.g. drop the
`Self()` helper if the neighboring fakes inline the factory lambda.)

Then the two tests (same file — copy the loud-guard structure of
`ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline` ~:467-496):

```csharp
    [Fact]
    public async Task InFlightCallPastDrainBudget_AtFinish_AbandonsImmediately()
    {
        var transcriber = new InFlightPastBudgetTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(30)); // FULL deadline: must NOT be waited out
        session.OnFrame(new float[800]); // first push — completes, full deadline applies
        session.OnFrame(new float[800]); // second push — wedges, reports 60 s in flight
        await transcriber.Session.WedgedPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // The in-flight call already exceeds the 30 s budget, so FinishAsync
        // must abandon IMMEDIATELY. The 10 s guard sits between "immediate"
        // and the 30 s deadline so waiting the deadline out fails loudly.
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue(); // same contract as a timed-out drain (E1 routing keys off this)
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrWaitMs.ShouldBeLessThan(5000); // did not pay the 30 s deadline
        stats.NativeCallStats.ShouldBeNull();   // never probed on abandon (gate may be wedged)

        // The background abandon dispose unwedges this socket-style fake.
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InFlightCallBelowDrainBudget_AtFinish_WaitsOutTheDeadline()
    {
        var transcriber = new InFlightPastBudgetTranscriber();
        transcriber.Session.InFlightToReport = TimeSpan.FromMilliseconds(50); // well under budget
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]);
        session.OnFrame(new float[800]);
        await transcriber.Session.WedgedPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(150); // paid the ~200 ms deadline — NO early abandon

        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ZeroPushShortDeadline_HealthyClassCallInFlight_WaitsTheWindowOut()
    {
        // A15 pin: zero COMPLETED pushes shrinks the effective deadline to
        // ~1.5 s. A call 2.5 s in flight is a healthy-class call (2.9-3.96 s
        // observed and recovered in the field) — FinishAsync must NOT abandon
        // at t=0 (which would also arm E1's blockade off a non-wedge); it
        // waits the short window out: abandon iff
        // elapsed >= max(effectiveDeadline, MinInFlightForFutility).
        var transcriber = new InFlightPastBudgetTranscriber();
        transcriber.Session.WedgeOnPush = 1; // FIRST push wedges: zero completed pushes
        transcriber.Session.InFlightToReport = TimeSpan.FromSeconds(2.5);
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(10)); // full budget; zero-push shortcut applies
        session.OnFrame(new float[800]); // first push — wedges immediately
        await transcriber.Session.WedgedPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue(); // the 1.5 s window expired normally (dispose unwedges the fake)
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(1000); // paid the ~1.5 s zero-push window — NO abandon at t=0

        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
```

Run (same two commands as Step 4). Expected:
`InFlightCallPastDrainBudget_AtFinish_AbandonsImmediately` FAILS with a
`TimeoutException` from the 10 s guard (the drain still waits the full 30 s);
`InFlightCallBelowDrainBudget_AtFinish_WaitsOutTheDeadline` already passes
(it pins today's behavior so the fix cannot overreach).

- [ ] **Step 8: Implement the early-abandon branch in FinishAsync**

In `StreamingDictationSession.FinishAsync`, insert between the effective
`deadline` computation (~:247-249) and the
`var waitSw = System.Diagnostics.Stopwatch.StartNew();` line:

```csharp
        // E2: a native call cannot be aborted. If the CURRENT in-flight
        // native call has already run at least the FULL drain budget (the
        // policy floors the check at MinInFlightForFutility, so the zero-push
        // 1.5 s shortcut can never trigger it against a healthy 2.9-3.96 s
        // call), the pump cannot possibly drain in time — waiting just delays
        // the caller's late batch path by the full deadline (observed
        // pre-fix: asr_wait pegged at ~10 s on every wedged batch fallback).
        // The probe is LOCK-FREE by contract (INativeCallInFlightSource) —
        // never NativeCallStats here: its snapshot takes the native gate the
        // wedged call is holding. A session/wrapper that does not expose the
        // interface yields null → no early abandon (previous behavior).
        var inFlightElapsed = (_session as INativeCallInFlightSource)?.NativeCallInFlightElapsed;
        if (DrainAbandonPolicy.ShouldAbandonImmediately(inFlightElapsed, deadline))
        {
            DrainTimedOut = true; // same contract as the timeout abandon below
            FinishStats = new StreamingFinishStats(0, null, backlogFrames, backlogMs, null);
            _log.LogWarning(
                "streaming drain abandoned immediately: in-flight native call already running {InFlightMs} ms, past the {DrainDeadline} drain budget; batch path takes over",
                (int)inFlightElapsed!.Value.TotalMilliseconds, deadline);
            _ = ScheduleAbandonedSessionDispose();
            return null;
        }
```

Then ONE further, log-only extension (A12 accepted-risk observability): the
committed record cannot show whether wedges typically pre-date the stop by
≥ the drain budget — only field WRNs can. Extend the EXISTING timeout-abandon
WRN in the `catch (TimeoutException)` block ("streaming drain exceeded
{DrainDeadline}; abandoning streaming session, batch path takes over",
`StreamingDictationSession.cs` ~:274-276) to also log the probed
in-flight-elapsed-at-stop, reusing the `inFlightElapsed` local computed ABOVE
the wait (i.e. the value AT STOP, not after the deadline):

```csharp
            _log.LogWarning(
                "streaming drain exceeded {DrainDeadline}; abandoning streaming session, batch path takes over (in-flight native call at stop: {InFlightAtStopMs} ms)",
                deadline, (long?)inFlightElapsed?.TotalMilliseconds ?? -1);
```

With the Step 8 early-abandon WRN already carrying `{InFlightMs}`, EVERY
abandon path now reports elapsed-at-stop. WRN-only: zero behavior change, no
timing-line schema change, no golden-string impact, lock-free by contract.
Post-merge, the first owner session settles A12 directly (any pegged-drain
WRN with in-flight-at-stop < 10000 is a live counterexample) and bounds A13
(whether ≥-budget calls ever return quickly).

Nothing else in the method changes: the healthy path keeps its single
`await _pump.WaitAsync(deadline, ct)` and `waitSw`-based `AsrWaitMs`
measurement, and the `catch (TimeoutException)` abandon block stays exactly
as-is (plus the WRN field above) for wedges that had NOT yet exceeded the
budget at stop time.

- [ ] **Step 9: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0`, `Failed: 0` — both Step 7 tests, the Step 5 transcriber
test, the Step 1 policy tests, and every pre-existing drain test
(`WedgedPush_DrainDeadlineExpires_...`,
`ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline`,
`FinishAsync_DrainTimeout_StillReportsWaitAndBacklog`, ...) unchanged.

- [ ] **Step 10: Linux suite green, then commit**

Run `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`, then:

```bash
git add src/Winpepper.Asr/Transcription/DrainAbandonPolicy.cs src/Winpepper.Asr/Transcription/NativeCallStats.cs src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs src/Winpepper.Asr/Transcription/StreamingDictationSession.cs tests/Winpepper.Asr.Tests/Transcription/DrainAbandonPolicyTests.cs tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
# plus any wrapper file touched in Step 6.5 (e.g. src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs)
git commit -m "feat(asr): abandon the drain immediately when the in-flight native call already exceeds the budget" \
  -m "E2: FinishAsync burned the full 10 s deadline even when the wedged native call had already been running longer than the whole budget (asr_wait pegged at 10013-10024 ms on every wedged batch fallback; 13-18 s per dictation; saving is an upper bound — concurrent-batch contention stretch field-bounded <= ~2x). NemotronStreamingTranscriber exposes a LOCK-FREE in-flight-since tick (never under the native gate; NOT armed across BeginStream, whose gate wait is queueing, not compute); pure DrainAbandonPolicy (Linux-tested) decides with a full-drain-budget futility floor so the zero-push 1.5 s shortcut can never abandon a healthy 2.9-3.96 s call; every abandon WRN now logs in-flight-elapsed-at-stop; the early abandon takes the exact same DrainTimedOut path, so E1's batch routing fires for it too." \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 12: Final gates + evidence wrap-up (validation expectations for the owner)

**Files:**
- Modify (append): `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`

- [ ] **Step 1: Full Linux suite**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN`. Record the grand-total test count.

- [ ] **Step 2: Full Windows gate**

```bash
./scripts/windows-gate.sh
```

(Allow up to 60 minutes.) Expected: exit 0 with `GATE: GREEN` (all 12
project/TFM runs `Errors: 0 Failed: 0`; `Winpepper.App build: OK` — this is the
FIRST compile of every `#if WINDOWS` change in Tasks 3, 5, 6, 7, 9, 10). Known
transient flakes — UNC `MSB4025` parse errors and vsock interop failures —
warrant a retry, up to 3 attempts. A REAL compile error in changed files must
be fixed (amend or follow-up `fix:` commit, then re-run BOTH suites).

- [ ] **Step 3: Append the closing evidence sections**

Append to the evidence file (fill every `<...>` with real values):

```markdown
## Run gates — 2026-07-30 single combined run

- Linux suite: GREEN, <N> tests.
- Windows gate: GATE: GREEN (<attempts> attempt(s); flakes retried: <list or "none">).
- Branch: feat/cleanup-asr-contention-run1, NOT pushed (root session merges/gates/installs).

## Post-install validation expectations (owner-attested later, NOT by this run)

From the approved plan's Validation section, minus the waived baseline
comparison; evaluated on the FIRST 30 filtered (prewarm_active=false),
non-excluded timing lines in log order, from
%LOCALAPPDATA%\winpepper\logs\winpepper-YYYYMMDD.log:

- native_over250 = 0 on >= 28 of 30 lines; for the (<= 2) tolerated lines,
  list every exceedance with its over250_at offsets here (reporting).
- native_max <= 250 ms on those same >= 28 lines.
- asr_wait p95 < 500 ms.
- total= p95 <= 5000 ms.
- ctx_src REPORTING (waived criterion): raw uia / ocr / none counts over the
  post-install dictations.
- cleanup= medians before/after from real dictations (reporting).
- NEW: mem= private-MB flat across a 30-dictation session (pre-fix baseline:
  ~3.0 GB private / ~1.5 GB WS / 167 threads / ~2000 handles at 5.9 h uptime);
  pf= deltas not growing dictation-over-dictation; sys_cpu= interpretable
  against cleanup-off dictations.
- Wedge-cascade check: any "streaming routed to batch for this dictation"
  INF lines should coincide with a preceding drain-timeout WRN, and the
  FOLLOWING dictation should not show a ~5000 ms stream-begin gate wait.
- Early-abandon check (E2): wedged batch fallbacks should no longer show
  asr_wait pegged at ~10000 ms when the wedge began mid-recording — expect
  either a normal timed-out drain or an "abandoned immediately" WRN with
  asr_wait ~0 on that line. ADDITIONALLY, on early-abandon lines compare
  `asr=` (asr_mode=batch) against the no-wedge batch baseline
  (p50 3.2-3.6 s / p90 6.0-7.0 s, from the pill-silence evidence) to bound
  the contention stretch of a batch running concurrently with a wedged
  native call: with-wedge batch p50 <= ~2x the no-wedge p50 confirms the
  concurrent-progress assumption (A14) quantitatively; E2's "up to ~10 s
  saved" is an upper bound until then.
- Elapsed-at-stop WRN data (A12/A13): every abandon WRN now logs the probed
  in-flight-elapsed-at-stop. Check (i) whether wedges typically pre-date the
  stop by >= the 10 s drain budget (A12 — any pegged-drain WRN with
  in-flight-at-stop < 10000 is a live counterexample; the split between the
  two WRNs gives the onset distribution), and (ii) whether calls already
  >= budget in flight at stop ever return quickly afterwards (A13 — each
  such quick return is a dictation E2 traded from a salvageable streaming
  result to batch).
- Residuals for owner observation (assumed by this run's designs, not
  provable from the committed record):
  - ParakeetSession DirectML EP presence — `UsingDirectML` is set but never
    logged; a CPU-fallback batch would compete for cores with a wedged call
    and sharpen contention.
  - Batch-of-full-audio transcription quality on abandoned dictations (E2
    sends users there sooner/more often; text is never lost, quality is
    unvalidated).
  - The un-cancellable OCR tail (PrintWindow capture + recognize work before
    the advisory Cancel is honored) staying short enough not to matter for
    ASR contention.
  - transcribe_session_free never hanging after the compute call returns —
    a hang would hold the compute gate forever (permanent gate-timeout on
    every later dictation); the "stream dispose" still-running WRN is the
    instrument.
- Outlier rule (unchanged): exclude only when asr_wait > 2000 AND
  backlog_ms > 2000 AND native_max > 2000; excluded lines are counted and
  listed here with offsets adjudicated against prefetch/prewarm/GC windows.
- 1c re-open trigger (unchanged, parked): reconsider only if dictations
  starting < 2 s after a prewarm began show native_over250 > 0 on more than
  2 of the first 10 such dictations in log order.
```

- [ ] **Step 4: Linux suite green, then commit**

```bash
./scripts/linux-tests.sh   # LINUX SUITE: GREEN
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — gates green, post-install validation expectations" \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

- [ ] **Step 5: Final check — branch is complete and local**

```bash
git status --short           # expect: empty
git log --oneline main..HEAD # expect: the pre-existing 0b/plan commits plus this plan's commits, in order
```

Do NOT push.

---

## Self-Review Notes (performed at plan-writing time)

- **Spec coverage:** A1–A3 = pre-landed, verified by Task 1. B1–B3 = Tasks 2–3.
  B4 = Task 4. C1/C3 = Task 5, C2 = Task 6. D1 = Tasks 8–9 (all four design
  details: (a) per-dictation CTS + cancel on silence-drop/Cancel/teardown +
  the new-recording-start ruling — coordinator + Task 9 Step 5; (b)
  `_ctxHwndAtStart` captured at start — Task 9 Step 2 + coordinator test;
  (c) consumed-stamp `ctx_src=uia` real-CTS-path test — Task 8's
  `WindowContextConsumedStampTests`; (d) both named race tests with named
  observables — Task 8's coordinator tests). D2 = Task 7 (named constant,
  lower cap with rationale, bench-judged). E1 = Task 10. E2 = Task 11
  (lock-free `INativeCallInFlightSource` probe + pure `DrainAbandonPolicy` +
  immediate-abandon branch in `FinishAsync`, sharing the `DrainTimedOut`
  contract so E1's routing covers it). F1–F3 = Task 1; bench numbers and
  validation expectations = Tasks 7/12. No known coverage gaps; no
  requirement deferred.
- **No silent deferrals:** the only stub-like element is `EchoBackend` in Task
  8's integration test — production cleanup behavior is exercised by the real
  `CleanupRunner` (the unit under test is consumption timing + stamping, not
  the LLM), and the real backend is covered by the Windows-gate integration
  tests. Owner-dictation validation is explicitly owner-attested later per the
  binding rules — recorded as expectations, not silently dropped.
- **Type consistency check:** `SystemCpuPercent(long, long, long) : int?` used
  identically in Tasks 2 and 3; `SystemTimesSample(Idle100ns, Kernel100ns,
  User100ns)` field names match between Tasks 3's sampler and wiring;
  `WindowContextPrefetchHandle.Task/Token/CancellationRequested`,
  `WindowContextPrefetchCoordinator.OnRecordingStart/Start/CancelAndClear/Current`,
  and `WindowContextStamp.CtxSrc(bool?, Task<WindowContextResult>?)` match
  between Tasks 8 and 9; `StreamingRouteGuard.NoteAbandoned(Task)` /
  `NoteDisposeOutcome(bool, Task)` / `TryClaimStreaming(out string?)` match
  between Task 10's tests and wiring;
  the per-call `out int gateWaitMs` shape matches between interface, engine,
  fake, and `EnsureStream`;
  `INativeCallInFlightSource.NativeCallInFlightElapsed` and
  `DrainAbandonPolicy.ShouldAbandonImmediately(TimeSpan?, TimeSpan)` match
  between Task 11's interface/policy definitions, the transcriber
  implementation, the `FinishAsync` probe, and all three test files.

### Load-bearing validation fixes (2026-07-30)

Applied after the load-bearing assumption validation pass (validator reports
V1–V8); the plan's self-review discipline was re-run over every edited task.

- **Task 2/3 (A7 confirmed + hardening):** GetSystemTimes semantics noted as
  doc-confirmed (kernel INCLUDES idle → busy = (kernel−idle)+user); Task 3's
  Windows gate test upgraded from `kernel+user > 0` to a two-sample
  plausibility bound (0 ≤ busy ≤ total, 0 ≤ sys_cpu ≤ 100); new cb/layout
  round-trip test (PROCESS_MEMORY_COUNTERS_EX = 80 bytes x64; struct made
  `internal` for the test); struct comments pin `PrivateUsage` (never
  `PagefileUsage`, documented zero on older OS); `SystemTimes()` doc comment
  records the >64-LP processor-group caveat.
- **Task 4 (A6 FALSIFIED):** the mutable `LastGateWaitMs` engine property +
  read-after-call protocol replaced by a per-call trailing `out int
  gateWaitMs` on `BeginStream`/`TranscribeBatch` (written before the
  gate-timeout throw; `out` is by-ref, so valid on return AND throw).
  Witness recorded: a cancel-orphaned wedged BeginStream returning ~16 s late
  would read the next dictation's 5000 ms gate-timeout write on the shared
  singleton engine. Fake, tests, EnsureStream, commit message, File Structure
  table, and the Self-Review type-consistency entry all updated in lockstep.
- **Task 5 (A2 overstated + A1 confirmed):** C1 wording softened everywhere
  (planned code comment, commit message, Task 1's investigation-summary
  evidence text): the chain is a finalizer-backed SafeHandle — non-disposal
  means delayed, finalizer-dependent, non-deterministic reclamation, not a
  process-lifetime leak; the `using` lands regardless. C3 notes added: hoist
  safety source-verified on v0.27.0 (`batch.Clear()` full wipe,
  SafeLLamaContextHandle.cs:721 / LLamaBatch.cs:259-272); the `_gate`
  SemaphoreSlim(1,1) is LOAD-BEARING (never remove); never mutate the shared
  `ModelParams` after construction (values re-read live per context).
- **Task 6 (A3b FALSIFIED):** `finally { swBitmap.Dispose(); }` replaced —
  CsWinRT's `AsTask(ct)` bridge (shipped WinRT.Runtime 2.2.0.48161) cancels
  the Task EAGERLY while the native op may still read the bitmap, so
  finally-dispose was a use-after-close on the routine cancel path. New
  design: token-less `AsTask()` (terminal only via Completed),
  `ct.Register(op.Cancel)` advisory cancel, dispose in a continuation on the
  op's own task, caller unblocked via `WaitAsync(ct)`; plus a cheap
  `ct.ThrowIfCancellationRequested()` before the (never-ct-checked) capture
  stage. Residual recorded: OcrEngine stopping input reads before Completed
  is contract-conformant but unverifiable.
- **Task 7 (A9/A11 confirmed + A10 notes):** D2 rationale marked
  source-confirmed (Threads=null → Math.Max(ProcessorCount/2,1)=16 at context
  creation; params propagate per generation). Bench notes added: judges the
  REGISTRY-DEFAULT qwen2.5-0.5b (comparable to the committed 2026-07-27
  record, p50 334 ms), NOT the active sotto-cleanup-lfm25 model — deliberate;
  never run concurrently with windows-gate.sh / linux-tests.sh (pre-cleans
  src/*/bin,obj).
- **Task 10 (A5i falsified as biconditional; design survives):** rationale
  restated honestly — PumpCompletion is a CONSERVATIVE ONE-SIDED-SAFE
  over-approximation of gate availability (may block while free; never
  clears while held for the nemotron wedge; ms release window absorbed by
  the 5 s gate timeout; gate release is finally-guarded). Coverage gap fixed
  as a design element: new `NoteDisposeOutcome(drainTimedOut,
  pumpCompletion)` notes the abandon whenever ANY DisposeAsync returns with
  the pump still incomplete (cancel/silence-drop/teardown orphans included),
  wired at every dispose site in both arms, with two new pure tests.
  Residual recorded: `transcribe_session_free` never hanging post-compute is
  assumed (log-instrumented).
- **Task 11 (A15/A16 FALSIFIED; A12/A13 inconclusive → observability; A14
  bounded):** predicate now abandons iff inFlightElapsed ≥
  max(effectiveDeadline, `MinInFlightForFutility` = 10 s full drain budget) —
  never fires on the zero-push 1.5 s shortcut against healthy 2.9-3.96 s
  calls (three new policy pins + a zero-push session test; fake gains
  `WedgeOnPush`, `SecondPushStarted` renamed `WedgedPushStarted`). Step 6.4's
  EnsureStream marker writes DROPPED (gate wait would read as in-flight time
  exactly in the zero-push phase; begin-wedges degrade to today's
  full-deadline behavior) with a gate-wait-never-abandons pin test. The
  timeout-abandon WRN now logs in-flight-elapsed-at-stop (WRN-only, zero
  behavior change) so field data settles A12/A13. Latency figures labeled
  "upper bound — contention stretch of the concurrent batch unmeasured
  (field-bounded ≤ ~2×)".
- **Task 12:** post-install expectations extended: early-abandon `asr=` vs
  the no-wedge batch baseline (p50 3.2-3.6 s / p90 6.0-7.0 s) to bound
  contention stretch (A14); elapsed-at-stop WRN adjudication for A12/A13;
  residuals for owner observation (DirectML EP presence, abandoned-dictation
  batch quality, un-cancellable OCR tail, session_free post-compute hang).

Item-by-item self-review outcome:

- Code claims vs repo reality: spot-checked anchors hold —
  `TranscribeCppEngine.cs:53/:209/:270` (gate timeout + both Wait sites),
  `ITranscribeCppEngine.cs:27` (`BeginStream(int, string? = null)` — the new
  `out` param requires dropping the optional default, noted in Task 4 Step 3),
  `NemotronStreamingTranscriber.cs:111/:162/:224-228` (EnsureStream on first
  push), `StreamingDictationSession.cs:76/:220/:230/:247-249/:253/:274-276/:354`
  (zero-push deadline, DrainTimedOut, PumpCompletion, drain wait, timeout WRN,
  orphaning dispose), `OcrFallback.cs:32-42/:54` (bitmap creation + AsTask(ct)),
  `FakeTranscribeCppEngine.cs:25/:27` (FeedGate pattern the new
  BeginStreamGate mirrors). Upstream/API claims carry validator citations.
- Test names/scaffolding: existing referenced tests unchanged
  (`Wedged_native_call_logs_a_still_running_warning...`,
  `ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline`, the :303 over-250
  pattern); renames are confined to plan-introduced (not-yet-written)
  scaffolding (`WedgedPushStarted`), updated at every reference.
- Golden strings: untouched — no new timing-line field; the A12 extension is
  WRN-log-only, and Task 2's golden-line edit is exactly as before.
- Cross-task consistency: Task 4's per-call `out gateWaitMs` ↔ Task 11 Step
  6.4's rationale (live probe cannot use a post-call value); Task 10's
  `NoteDisposeOutcome` ↔ Task 11's E1-interplay note (DrainTimedOut OR
  incomplete pump); Task 5's ModelParams-mutation note ↔ Task 7's live
  re-read corollary; Task 11's upper-bound label ↔ Task 12's A14 comparison.
- Global constraints: intact — Tasks 2/3/4 remain measurement-only (zero
  behavior change); the E2 probe still reads ONLY the volatile field and
  never takes `_nativeGate` (the A15/A16 fixes are policy/placement-level);
  both-arms rule preserved in the new Task 10 wiring text.
- Task numbering / cross-references: intact — Task 6's steps renumbered
  internally (now 1-4) with no external step-number references to Task 6;
  all other task and step numbers unchanged; the Spec-coverage map above
  still reads correctly (B4 = Task 4, C1/C3 = Task 5, C2 = Task 6, D2 =
  Task 7, E1 = Task 10, E2 = Task 11).
