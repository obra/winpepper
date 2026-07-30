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

## 0c — StatelessExecutor native-context lifetime (Planner B vs Planner C)

(pending — the finding MUST quote LLamaSharp v0.27.0 file and line numbers, from
either the official GitHub tag or a decompile of the NuGet-resolved assembly at
`~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.dll`. If neither is
possible, write "could not verify" — which means step 1d is NOT triggered. Never
settle this from memory.)

## Owner sections (NOT Run 1 — the owner fills these after install)

- ctx_src baseline (FIRST 20 filtered, non-excluded timing lines with window
  context ON, in log order): pending owner.
- 0a branch (window context OFF until 20 qualifying lines; CONFIRMED / NO CHANGE /
  PARTIAL, counted once over the first 20 qualifying lines): pending owner.
- Excluded-outlier tally + over250_at adjudication against logged prefetch /
  prewarm / GC windows: pending owner.
