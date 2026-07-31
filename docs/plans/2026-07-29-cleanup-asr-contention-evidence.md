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

## 1b — thread cap bench (run-cleanup-bench-windows.sh)

Cap: Threads = BatchThreads = min(4, max(1, ProcessorCount/4)).
Results (artifacts/cleanup-bench/20260730-115010/results.md):
- median latency: 284 ms (criterion: <= 1000 ms) — PASS
- eval outcomes: all 18 eval cases ran with zero errors (Failed: 0), per-case
  paths identical to the latest committed qwen record (bake-off run
  `20260727-214235`, docs/plans/2026-07-27-cleanup-model-bakeoff-prep.md §7):
  17 Llm + `trap-poem-request` FallbackImplausible (the known
  `KnownFailingBaseline` plausibility-guard rejection) — PASS

Notes: registry-default model `qwen2.5-0.5b-instruct-q4_k_m`, 3 passes,
seed 42; 118 statements, Llm calls 306 — p50 284 ms / p95 461 ms / mean
310.8 ms; paths Llm=306, BypassShort=24, FallbackImplausible=24; model load
1861 ms, warm 223 ms. Median IMPROVED vs both committed qwen records (334 ms
morning baseline `20260727-115614`, 445 ms bake-off `20260727-214235`) — the
cap costs nothing on this fully-GPU-offloaded model. Path-count differences
vs the 2026-07-27 runs (BypassShort 24 vs 48) come from the documented
history-corpus drift, not the cap; the 18 committed eval cases are the
stable comparable subset and match exactly. No ladder escalation (6/8)
needed. LLamaSharp 0.27 `ModelParams` carries `Threads`/`BatchThreads` as
declared — no property-name correction required (verified against the
resolved package XML docs).

## Run gates — 2026-07-30 single combined run

- Linux suite: GREEN, 1583 tests. (Second attempt; the first attempt hit 1
  failure in the pre-existing, branch-untouched load-sensitive test
  `CleanupBackendHolderTests.FailedVerification_KeepsCurrentModel_AndNeverConstructsBackend`
  — reproduced only under artificial 4-way concurrent CPU load, green on 5
  quiet reruns. Not one of the two known gate flakes; noted for the record.)
- Windows gate: GATE: GREEN (1 attempt(s); flakes retried: none).
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

## Raw-io steering investigation — UI honesty follow-up (2026-07-31)

- Empirically confirmed 2026-07-30/31: the sotto raw-io model ignores
  every in-prompt steering channel (context blocks, vocabulary hints,
  few-shot); its training data contains no context fields. Experiments
  at `~/models-work/dbg/steer2/`.
- Structural cause: `CleanupPromptFormatter.Build`'s raw-io arm
  (`CleanupPromptFormatter.cs:101-109`) builds the prompt from ONLY the
  transcript — the system prompt assembled by `PromptBuilder.BuildSystem`
  (profile, custom prompt, corrections vocabulary, window context) is
  discarded.
- The UI now reflects it: while a raw-io model is active, the Cleanup
  tab grays out profile / custom prompt / window context with a
  plain-language note (`PromptFormatCapabilities`, single source of
  truth), and PipelineHost's window-context prefetch is gated on the
  same capability (`WindowContextPrefetchGate`) — no UIA walk runs for
  a model that cannot consume it.
- Max-new-tokens under raw-io: the setting is a cap that the raw-io
  floor overrides — `CleanupPromptFormatter.ApplyMinNewTokensFloor`
  (invoked from `LlamaCleanupBackend.cs:114`) is
  `Math.Max(cap, 900)` (`RawIoMinNewTokensFloor`,
  `CleanupPromptFormatter.cs:46`), so effective tokens =
  `max(900, min(clamp(setting,64,4096), ceil(chars*2)))`. At the
  shipped default 512 the setting is inert under raw-io; it takes
  effect only above 900 with transcripts > ~450 chars. The slider
  therefore stays ENABLED. Still fully effective under raw-io:
  cleanup enabled, timeout, model selection.
