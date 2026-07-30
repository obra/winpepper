# Evidence: stop the cleanup model from slowing live transcription — Run 1

Companion evidence file for `docs/plans/2026-07-29-cleanup-asr-contention-fix.md`
(APPROVED FOR EXECUTION 2026-07-29, committed at `8f5db7d`, read-only during
execution). Created as the run-1 executor's first action, per the plan's binding
rules. All findings, measurements, corrected file locations, and 0c's quoted
evidence land here.

Run 1 scope: step 0b (three new dictation-timing-line fields) + step 0c
(StatelessExecutor lifetime evidence). Instrumentation only — no behavior change.

---

## Mismatch-rule log (corrected file/line pointers)

- 2026-07-29 (pre-implementation survey at `8f5db7d`): every plan-claimed pointer
  was re-checked and matches in substance:
  - `src/Winpepper.Cleanup/LlamaCleanupBackend.cs:88` — fresh `StatelessExecutor`
    per generation (construction spans :88–91). MATCH.
  - `LlamaCleanupBackend.cs:43–47` — `ModelParams` sets only `ContextSize` and
    `GpuLayerCount`; no `Threads`/`BatchThreads`. MATCH.
  - `src/Winpepper.App/Hosting/PipelineHost.cs` hold-arm prefetch launch — observed
    at :522–535 (plan says ~:526–534; within the plan's "~" tolerance). MATCH.
  - Toggle-arm prefetch launch — observed at :1023–1036 (plan says ~:1027–1035).
    MATCH.
  - `StopRequested` handling — hold arm :538, toggle arm :1039, exactly as claimed.
    MATCH.
  - `src/Winpepper.Platform/WindowContext/WindowContextPrefetch.cs:32–56` — UIA
    first, OCR fallback; `WindowContextResult.Source` carries Uia/Ocr/Empty and is
    never logged today. MATCH.
  - `src/Winpepper.Cleanup/CleanupRunner.cs` — bounded window-context wait at
    :75–98; the "stopped waiting" else-branch at :88–92. MATCH.
- (append further corrections here as implementation proceeds)

## 0b — instrumentation added

- Consume-time indicator (`src/Winpepper.Cleanup/`): `CleanupResult` gained
  init-only `ConsumedWindowContext` (null = no task/disabled, false = task not
  complete when the runner stopped waiting, true = complete within the bounded
  wait). Set in `CleanupRunner.RunAsync`'s window-context wait; threaded onto
  all 11 returns via `with`. Input signature of RunAsync unchanged.
- Formatter (`src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`): added
  `Over250AtMs`/`Over250Overflow`/`CtxSrc`/`ProcCpuMs` + `StampOver250(...)`.
- Capture (`src/Winpepper.Asr/Transcription/`): `NativeCallStats` gained
  `Over250StartTicks` (absolute TickCount64 at call START, cap 16 via
  `NativeCallStats.Over250ListCap`) + `Over250Overflow`;
  `NemotronStreamingTranscriber.Session.TimedNativeCall` records them under
  `_nativeGate`. Rides the existing `StreamingFinishStats` chain unchanged;
  absent on the drain-timeout/abandon path by design (stats never probed there).
  Line grammar: `over250_at=[a,b,...]` (first 16, unclamped ms offsets from
  recording start) with `+N` overflow suffix; `ctx_src=uia|ocr|none` after
  `cleanup_model=`; `proc_cpu_ms=<n>` after `prewarm_active=`. All omitted when
  null. No budget/Overruns changes.
- Stamping (`src/Winpepper.App/Hosting/PipelineHost.cs`, both arms):
  `proc_cpu_ms` = Process.TotalProcessorTime delta sampled at recording start
  and at StopRequested (hold :538-area, toggle :1039-area; never at emit);
  `over250_at` stamped via StampStreamingFinishStats(+ _dictStartTicks) ->
  DictationTimingSummary.StampOver250; `ctx_src` mapped from
  CleanupResult.ConsumedWindowContext + _ctxPrefetchTask.Result.Source.
  The legacy windowContextUsed prompt sniff is intentionally untouched.

## 0c — RESOLVED: Planner B correct; 1d NOT TRIGGERED

Verified against the official LLamaSharp v0.27.0 tag
(LLama/LLamaStatelessExecutor.cs fetched from GitHub raw):

- Constructor: creates a context and immediately disposes it —
  line 66: `Context = _weights.CreateContext(_params, logger);`
  line 67: `Context.Dispose();`
- InferAsync: the working context is created inside a `using` and disposed
  deterministically when the async enumerator completes —
  line 78: `// Create an inference context which will be disposed when this method exits`
  line 79: `using var context = _weights.CreateContext(_params, _logger);`
  line 80: `Context = context;`

Verdict: nothing native lingers between generations awaiting GC finalization.
Plan 1d (explicit context disposal after each generation) is NOT TRIGGERED.
(The per-generation constructor churn is still real waste — fixed separately
as leak-fix C3, executor hoisted to a field.)

## Owner sections (NOT Run 1 — the owner fills these after install)

- ctx_src baseline (FIRST 20 filtered, non-excluded timing lines with window
  context ON, in log order): pending owner.
- 0a branch (window context OFF until 20 qualifying lines; CONFIRMED / NO CHANGE /
  PARTIAL, counted once over the first 20 qualifying lines): pending owner.
- Excluded-outlier tally + over250_at adjudication against logged prefetch /
  prewarm / GC windows: pending owner.

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
