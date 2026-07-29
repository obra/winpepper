# ASR Stall Instrumentation Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Instrument the streaming ASR finish path so the next intermittent
multi-second `asr=` stall is fully root-causable from logs alone, plus one
low-risk allocation fix (pooled per-frame copy). Instrumentation-only — this
plan must NOT attempt to fix the stall itself.

**Architecture:** All new measurement policy lives in pure, Linux-tested code
(`DictationTimingSummary` in Core; counters/stats in `Winpepper.Asr` and
`Winpepper.Cleanup`). The Windows-only `PipelineHost` only *stamps* values onto
the existing per-dictation timing summary (house pattern:
policy-in-Core-tested, wiring-in-App-gate-compiled — PipelineHost has no
tests by design). Stats flow out-of-band via properties (mirroring the
existing `DrainTimedOut`/`PumpCompletion` precedent), because
`TranscriptionResult` has no metadata slot and the most interesting case
(drain-timeout abandon) returns `null`.

**Tech Stack:** .NET 9, xUnit v3 (in-process runner via `dotnet exec`, never
`dotnet test`), Shouldly (except `NemotronStreamingTranscriberTests.cs`, which
uses bare `Assert` — match each file's local style), `ArrayPool<float>`,
`Interlocked`/`Volatile`, `Environment.TickCount64`, `GC.CollectionCount`,
`GC.GetTotalPauseDuration()` (verified available + monotonic on .NET 9).

## Global Constraints

- All pure logic must be Linux-testable; Windows-only code compiles under the gate.
- All tests green before EVERY commit: run `./scripts/linux-tests.sh` (NEVER `dotnet test`) — expect `LINUX SUITE: GREEN`.
- Full Windows gate before considering done: `./scripts/windows-gate.sh` from WSL (~10 min) — expect `GATE: GREEN`. UNC MSB4025 and vsock interop flakes are known transients — retry.
- Never mix Linux- and Windows-side builds in the same `bin/`/`obj/` (the gate pre-cleans; never run `linux-tests.sh` concurrently with the gate).
- Amplifier co-author attribution on every commit (exact trailer in Task 1, Step 5 — reuse verbatim on all commits).
- Do NOT push to origin — leave branch `feat/asr-stall-instrumentation` local; the root session merges.
- INSTRUMENTATION-ONLY plus the one allocation fix (Task 5). Do NOT change GC settings, thread priorities, drain deadlines, or attempt any stall fix.
- Log-volume discipline: do NOT log per-native-call lines below the existing 3000 ms warn threshold; per-session aggregates only.
- Do NOT touch existing budgets other than adding `AsrWaitBudgetMs` (the known 5s-total-vs-8s-batch tension is a separate later recalibration pass).
- Working directory for all commands: `/home/dan/code/winpepper/.worktrees/asr-stall-instrumentation`.

---

## Investigation Context (record only — do not re-litigate)

**Symptom:** dictation timing lines show `asr=` typically 130–500 ms
(streaming, nemotron-streaming-en) but intermittently ~3000 ms (2026-07-29
08:58 `asr=3062ms`, 09:04 `asr=2864ms`); on 2026-07-28 evening, single native
`stream feed` calls of 4578/13335/14269 ms, twice tripping the 10 s
drain-timeout abandon to batch.

**Confirmed mechanism:** on the streaming path, `asr=` (PipelineHost
`transcribeSw`, hold arm ~`:593`–`:684`, toggle twin ~`:1086`–`:1177`) folds
together (1) `StreamingDictationSession.FinishAsync`'s wait for the pump to
drain the queued-frame backlog (`_pump.WaitAsync`) and (2) the nemotron tail
feed + finalize native calls (`NemotronStreamingTranscriber.Session.FinishAsync`,
`TimedNativeCall` with a 3000 ms warn threshold). If native `Feed()` calls run
slower than real time during recording, frames back up in the unbounded
channel and `FinishAsync` pays the catch-up — invisible today because no field
separates the two, and `TimedNativeCall` is blind below 3000 ms.

**Refuted (do not re-investigate):** trim interplay (trimmed buffer never
re-decoded on the streaming happy path); GPU/idle warm-up (engine is CPU-only,
loaded once per process via `NemotronEngineHolder`).

**Open question this instrumentation must disambiguate:** WHY individual
CPU-side native calls intermittently stall — candidates: (a) GC pauses +
per-frame allocation churn (`frame.ToArray()` per frame), (b) OS thread
deprioritization / efficiency-mode throttling of the pump's `Task.Run` thread,
(c) background CPU contention — NOTABLY the cleanup-model pre-warm
(`CleanupBackendHolder` background load + warm-up inference, landed 07-28) and
boot pre-warm, which do heavy CPU work concurrently with dictation.

**REGRESSION HYPOTHESIS (owner-flagged; recorded as context, act on it only
via instrumentation):** the owner believes this is a REGRESSION that appeared
2026-07-28. That day landed: cleanup live-swap with background pre-warm
(boot + promote), deadline-based injection pacing + hotkey-hook
injected-event fast-path, pending-paste council hardening,
pill/silence/observability. The most plausible regression vector is the
cleanup pre-warm's concurrent CPU load (`LoadCore` blocks a thread-pool
thread ~1.3–2.2 s: synchronous native GGUF load + synchronous warm-up
inference). The `prewarm_active=` marker + prewarm start/finish INF lines
below are designed so the correlation can be read directly from logs; a later
fix cycle will use that data.

## Timing-Line Schema Change Note

- `drain=` is RENAMED to `mic_stop=` (it measures `WarmRecorder.StopSession()` —
  mic buffer copy + streaming-tee teardown — NOT any ASR-side drain; "drain"
  collides with the streaming drain-deadline concept folded into `asr=`).
  Consumers grepped repo-wide: only `DictationTimingSummaryTests.cs` consumes
  the literal. `docs/plans/2026-07-28-pill-silence-observability.md` contains
  historical copies — it is a completed plan (a record of what shipped), NOT a
  live consumer, and is deliberately left untouched; this note is the schema
  changelog.
- New OPTIONAL fields (omitted when null, so old-log grep patterns keep
  working): `asr_wait=`, `asr_native=`, `backlog=`, `backlog_ms=`,
  `native_calls=`, `native_total=`, `native_max=`, `native_over250=`,
  `gc=g0/g1/g2`, `gc_pause=` (ms delta of `GC.GetTotalPauseDuration()` —
  measures actual GC pause TIME; counts alone can't convey magnitude),
  `prewarm_active=true|false`. `asr=` remains the total for continuity.
- New budget: `asr_wait` > 500 ms emits the existing
  `slow dictation stage` WRN — backlog buildup becomes grep-able without
  waiting for a total-stage overrun. INTERPRETATION CAVEAT (validated):
  `asr_wait=` spans `_pump.WaitAsync`, and the pump task includes session
  STARTUP work (cold factory/model load, cloud connect) when the session was
  still starting at stop — the deadline logic deliberately keeps the full
  deadline for still-starting sessions. So a large `asr_wait` with SMALL
  `backlog_ms` and a nearby engine/model-load INF is a cold start, not slow
  feeds; genuine backlog buildup shows `backlog_ms` comparable to `rec=`.
- RESIDUE DERIVATION (validated): on `asr_mode=streaming` lines,
  `asr − asr_wait − asr_native` = coordinator overhead + the inner session's
  native `stream dispose` (which runs after the `asr_native` stopwatch stops
  and after the native-aggregate snapshot, so the line's `native_*` fields
  deliberately do NOT include it). Every other residue component is
  code-bounded ≤ ~1 s and typically ~0 (the settings re-read at the window
  head is a per-call disk read with a 15–30 ms retry tail — a caveat, not
  seconds), so a multi-second residue localizes to the native stream dispose —
  which ALSO fires the existing ≥3000 ms `TimedNativeCall` WRN now that
  Task 3 wraps it.
- `backlog_ms` is computed from SAMPLES (queued samples / 16 per ms @ 16 kHz),
  not frames × nominal duration: real frames are ~50 ms (800 samples) but the
  session pre-roll frame is ~500 ms (8000 samples), so a sample count is the
  accurate backlog measure. `backlog=` stays the raw frame count.
- `asr_native=` spans the inner session's `FinishAsync` — on the streaming
  happy path that is exactly the tail feed + finalize; when the transcriber
  falls back to batch internally it includes the batch transcription
  (`asr_mode=batch` on the same line reveals that case).

## File Structure

| File | Role |
|---|---|
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` | MODIFY — rename `drain=`→`mic_stop=`; add new fields + `asr_wait` budget (pure, Linux-tested) |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` | MODIFY — rename + new-field/budget tests |
| `src/Winpepper.Asr/Transcription/NativeCallStats.cs` | CREATE — `NativeCallStats` record + `INativeCallStatsSource` interface |
| `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` | MODIFY — per-session native-call aggregates in `TimedNativeCall` |
| `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs` | MODIFY — aggregate-stats tests (bare `Assert` style) |
| `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs` | MODIFY — backlog counters, `StreamingFinishStats`, pooled frame copy |
| `src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs` | MODIFY — `PushAsync` buffer-lifetime contract doc (Task 5 only) |
| `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` | MODIFY — FinishStats + pooling tests (Shouldly style) |
| `src/Winpepper.Cleanup/CleanupBackendHolder.cs` | MODIFY — prewarm in-flight tracking + start/finish INF logs |
| `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs` | MODIFY — prewarm flag + log tests |
| `tests/Winpepper.Cleanup.Tests/Fakes/CollectingLogger.cs` | MODIFY — also collect INF lines (find via `grep -rn "class CollectingLogger" tests/`) |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | MODIFY — Windows-only stamping (GC baseline, dict-start ticks, FinishStats, prewarm flag); NO tests (house pattern) |

Task order: 1 → 2 → 3 → 4 → 5 → 6 → 7 → 8. Task 4 depends on Task 3's types;
Task 5 depends on Task 4; Task 7 depends on 1–4 and 6.

---

### Task 1: Rename `drain=` → `mic_stop=` (field-name trap removal)

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (lines 27, 46, 70, 92)
- Modify: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` (lines 16, 41, 58, 91 + any other in-file `drain`/`Drain` hits)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (lines ~536–539 hold arm, ~1030–1033 toggle arm)

**Interfaces:**
- Consumes: existing `DictationTimingSummary` (`DrainMs`, `DrainBudgetMs`, emitted key `drain`).
- Produces: `public int? MicStopMs { get; set; }`, `public const int MicStopBudgetMs = 500;`, emitted key `mic_stop`, overrun stage name `"mic_stop"`. Task 7 and the timing line rely on these exact names.

Background: `drain=` today measures ONLY `_warmRecorder.StopSession()` (mic
buffer copy + streaming-tee teardown). "drain" is used elsewhere in
PipelineHost/StreamingDictationSession to mean the streaming post-stop drain
deadline — a different concept currently folded into `asr=`. The rename frees
the word for the real ASR-drain instrumentation (`asr_wait=`).

- [ ] **Step 1: Update the tests to the new names (this is the RED step — the project will not compile, which is the rename's failing state)**

In `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

1. In the `Full()` builder (line ~16): `DrainMs = 42,` → `MicStopMs = 42,`
2. In `FormatLine_FullDictation_IsOneParseableKeyValueLine` (line ~41): change the expected-line literal segment `" rec=3512ms drain=42ms trim=8ms"` → `" rec=3512ms mic_stop=42ms trim=8ms"`
3. In the silent-drop test (line ~58): `DrainMs = 30,` → `MicStopMs = 30,` — and if the same test asserts a `drain=` literal (e.g. `line.ShouldContain("drain=30ms")`), change it to `mic_stop=30ms`.
4. In the at-budget test (line ~91): `s.DrainMs = DictationTimingSummary.DrainBudgetMs;` → `s.MicStopMs = DictationTimingSummary.MicStopBudgetMs;`
5. Sweep for stragglers: `grep -n -i "drain" tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` — update every remaining hit (property, const, string literal, and any `new StageOverrun("drain", ...)` expectation → `"mic_stop"`).

- [ ] **Step 2: Verify the failure**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/asr-stall-instrumentation
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
```
Expected: FAIL with CS0117 / CS1061 (`'DictationTimingSummary' does not contain a definition for 'MicStopMs'`).

- [ ] **Step 3: Rename in `DictationTimingSummary.cs`**

Four edits:

```csharp
// line ~27 — const (keep the comment, clarify what it measures):
public const int MicStopBudgetMs = 500;       // provisional: mic buffer copy + tee teardown (WarmRecorder.StopSession)

// line ~46 — property:
public int? MicStopMs { get; set; }

// line ~70 — FormatLine():
AppendCoreMs(sb, "mic_stop", MicStopMs);

// line ~92 — Overruns():
Check(list, "mic_stop", MicStopMs, MicStopBudgetMs);
```

Then `grep -n -i "drain" src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` — expect zero hits.

- [ ] **Step 4: Rename the two PipelineHost assignment sites**

In `src/Winpepper.App/Hosting/PipelineHost.cs` (hold arm ~:536–539, toggle arm ~:1030–1033), rename the property use AND the local stopwatch for clarity:

```csharp
// hold arm:
var micStopSw = System.Diagnostics.Stopwatch.StartNew();
var samples = _warmRecorder!.StopSession();
micStopSw.Stop();
timing.MicStopMs = (int)micStopSw.ElapsedMilliseconds;

// toggle arm (locals carry the 2 suffix):
var micStopSw2 = System.Diagnostics.Stopwatch.StartNew();
var samples2 = _warmRecorder!.StopSession();
micStopSw2.Stop();
timing2.MicStopMs = (int)micStopSw2.ElapsedMilliseconds;
```

Do NOT touch the other "drain" occurrences in PipelineHost (`drain deadline`,
`drain timeout` comments/logs around :618–:651 and toggle twins) — those refer
to the genuine streaming drain and stay. `grep -n "DrainMs" src/` must return
zero hits afterwards.

- [ ] **Step 5: Run Linux tests, then commit**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` (PipelineHost is not built on Linux; its compile is proven by the Task 8 gate — the edit is a mechanical property rename).

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
refactor(core): rename timing field drain= to mic_stop=

drain= measured only WarmRecorder.StopSession (mic buffer copy + tee
teardown), colliding with the streaming drain-deadline concept that is
actually folded into asr=. Frees "drain" wording for the real ASR-drain
instrumentation (asr_wait=). Schema change noted in
docs/plans/2026-07-29-asr-stall-instrumentation.md; historical plan docs
deliberately untouched.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

(Reuse this exact trailer block on every later commit.)

---

### Task 2: New timing-line fields + `asr_wait` budget (pure Core)

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: Task 1's renamed members.
- Produces (Task 7 stamps these — exact names/types):
  `int? AsrWaitMs`, `int? AsrNativeMs`, `int? BacklogFrames`, `int? BacklogMs`,
  `int? NativeCalls`, `int? NativeTotalMs`, `int? NativeMaxMs`, `int? NativeOver250`,
  `int? GcGen0`, `int? GcGen1`, `int? GcGen2`, `int? GcPauseMs`, `bool? PrewarmActive`,
  `public const int AsrWaitBudgetMs = 500`. All render only when set.

- [ ] **Step 1: Write the failing tests**

In `DictationTimingSummaryTests.cs`:

1. Extend the `Full()` builder — insert after `AsrModel = "nemotron-streaming-en",`:

```csharp
        AsrWaitMs = 95,
        AsrNativeMs = 210,
        BacklogFrames = 2,
        BacklogMs = 100,
        NativeCalls = 74,
        NativeTotalMs = 1900,
        NativeMaxMs = 180,
        NativeOver250 = 0,
```

and after `InjectPacingMs = 798,`:

```csharp
        GcGen0 = 1,
        GcGen1 = 0,
        GcGen2 = 0,
        GcPauseMs = 12,
        PrewarmActive = true,
```

2. Update the exact-line expectation in `FormatLine_FullDictation_IsOneParseableKeyValueLine` to (single line, shown wrapped here — keep it one string or the existing concatenation style):

```
session=11111111-2222-3333-4444-555555555555 kind=hold outcome=completed rec=3512ms mic_stop=42ms trim=8ms trim_removed=1200ms asr=812ms asr_mode=streaming asr_model=nemotron-streaming-en asr_wait=95ms asr_native=210ms backlog=2 backlog_ms=100ms native_calls=74 native_total=1900ms native_max=180ms native_over250=0 corrections=2ms cleanup=640ms cleanup_path=Llm cleanup_model=qwen2.5-1.5b inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms gc=1/0/0 gc_pause=12ms prewarm_active=true total=2354ms
```

3. Add new facts (Shouldly, `Subject_Scenario_Expected` naming):

```csharp
    [Fact]
    public void FormatLine_NewDiagnosticFields_AreOmittedWhenNull()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "hold",
        };

        var line = s.FormatLine();

        line.ShouldNotContain("asr_wait=");
        line.ShouldNotContain("asr_native=");
        line.ShouldNotContain("backlog");
        line.ShouldNotContain("native_");
        line.ShouldNotContain("gc=");
        line.ShouldNotContain("gc_pause=");
        line.ShouldNotContain("prewarm_active=");
    }

    [Fact]
    public void FormatLine_GcTriple_RendersWhenAnyGenIsSet()
    {
        var s = Full();
        s.GcGen0 = 3;
        s.GcGen1 = null;
        s.GcGen2 = null;

        s.FormatLine().ShouldContain("gc=3/0/0");
    }

    [Fact]
    public void Overruns_AsrWaitOverBudget_Warns()
    {
        var s = Full();
        s.AsrWaitMs = DictationTimingSummary.AsrWaitBudgetMs + 1;

        s.Overruns().ShouldContain(new StageOverrun(
            "asr_wait", DictationTimingSummary.AsrWaitBudgetMs + 1, DictationTimingSummary.AsrWaitBudgetMs));
    }

    [Fact]
    public void Overruns_AsrWaitAtBudget_IsClean()
    {
        var s = Full();
        s.AsrWaitMs = DictationTimingSummary.AsrWaitBudgetMs;

        s.Overruns().ShouldNotContain(o => o.Stage == "asr_wait");
    }
```

Note: `Full()` sets `AsrWaitMs = 95` (under budget), so pre-existing
`Overruns_*` facts are unaffected. `AsrNativeMs`, backlog, native and GC
fields carry NO budgets.

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
```
Expected: FAIL — CS0117 (`AsrWaitMs` etc. not defined).

- [ ] **Step 3: Implement in `DictationTimingSummary.cs`**

1. Add the budget const to the budgets block (after `AsrBatchBudgetMs`):

```csharp
    public const int AsrWaitBudgetMs = 500;       // asr_wait: _pump.WaitAsync after stop. >500 ms means EITHER
                                                  // native feeds ran slower than real time during recording
                                                  // (frames backed up in the unbounded channel; backlog_ms will
                                                  // be large) OR the session was still starting at stop (cold
                                                  // factory/model load, cloud connect; backlog_ms small, load
                                                  // INFs nearby). The WRN is a flag to look, not a verdict.
```

2. Add properties after `public string? AsrModel { get; set; }`:

```csharp
    public int? AsrWaitMs { get; set; }         // FinishAsync: _pump.WaitAsync backlog drain
    public int? AsrNativeMs { get; set; }       // FinishAsync: inner session finish (tail feed + finalize)
    public int? BacklogFrames { get; set; }     // frames queued but not yet pumped at finish entry
    public int? BacklogMs { get; set; }         // queued samples / 16 (16 kHz mono)
    public int? NativeCalls { get; set; }       // per-session native call aggregates (NativeCallStats)
    public int? NativeTotalMs { get; set; }
    public int? NativeMaxMs { get; set; }
    public int? NativeOver250 { get; set; }
```

and after `public int? InjectPacingMs { get; set; }`:

```csharp
    public int? GcGen0 { get; set; }            // GC.CollectionCount deltas, recording start -> emit
    public int? GcGen1 { get; set; }
    public int? GcGen2 { get; set; }
    public int? GcPauseMs { get; set; }         // GC.GetTotalPauseDuration() delta, recording start -> emit:
                                                // actual GC pause TIME (counts can't convey magnitude)
    public bool? PrewarmActive { get; set; }    // cleanup pre-warm overlapped this dictation
```

3. In `FormatLine()`, insert after `AppendOptStr(sb, "asr_model", AsrModel);`:

```csharp
        AppendOptMs(sb, "asr_wait", AsrWaitMs);
        AppendOptMs(sb, "asr_native", AsrNativeMs);
        AppendOptNum(sb, "backlog", BacklogFrames);
        AppendOptMs(sb, "backlog_ms", BacklogMs);
        AppendOptNum(sb, "native_calls", NativeCalls);
        AppendOptMs(sb, "native_total", NativeTotalMs);
        AppendOptMs(sb, "native_max", NativeMaxMs);
        AppendOptNum(sb, "native_over250", NativeOver250);
```

and insert after `AppendOptMs(sb, "inject_pace", InjectPacingMs);` (before the `total` append):

```csharp
        if (GcGen0 is not null || GcGen1 is not null || GcGen2 is not null)
            sb.Append(" gc=").Append(GcGen0 ?? 0).Append('/').Append(GcGen1 ?? 0).Append('/').Append(GcGen2 ?? 0);
        AppendOptMs(sb, "gc_pause", GcPauseMs);
        if (PrewarmActive is bool prewarm)
            sb.Append(" prewarm_active=").Append(prewarm ? "true" : "false");
```

4. In `Overruns()`, insert directly after the per-mode `asr` check:

```csharp
        Check(list, "asr_wait", AsrWaitMs, AsrWaitBudgetMs);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "feat(core): asr_wait/asr_native/backlog/native/gc/prewarm fields on the dictation timing line" -m "asr= stays the total; new fields are optional (omitted when null). asr_wait gets its own 500 ms budget so backlog buildup is grep-able as a WRN without a total-stage overrun." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 3: Always-on per-session native-call aggregates (nemotron)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/NativeCallStats.cs`
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: existing `TimedNativeCall` (`private T TimedNativeCall<T>(string op, Func<T> call)`, every call site already inside `lock (_nativeGate)`); existing test doubles `FakeTranscribeCppEngine` (`FeedDelay`, `FeedGate` seams), `FakeTranscriber.Returning(...)`, `Samples(n)` helper — all already in the test file/project.
- Produces (Task 4 consumes): `public sealed record NativeCallStats(int Count, int TotalMs, int MaxMs, int CountOver250Ms)` and `public interface INativeCallStatsSource { NativeCallStats NativeCallStats { get; } }` in namespace `Winpepper.Asr.Transcription`; nemotron's `Session` implements `INativeCallStatsSource`.

Log-volume discipline: NO new log lines in this task. The existing ≥3000 ms
`TimedNativeCall` warnings are untouched (their tests keep passing); wrapping
the stream dispose (Step 3 item 5) merely extends that EXISTING warn mechanism
to one more op — it emits nothing below the threshold. The aggregate is what
distinguishes "one 2.9 s call" from "many 250 ms calls".

- [ ] **Step 1: Write the failing tests**

Append to `NemotronStreamingTranscriberTests.cs` (this file uses bare xUnit `Assert`, not Shouldly — keep that style):

```csharp
    [Fact]
    public async Task Session_AggregatesNativeCallStats_AcrossPushAndFinish()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en");
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken); // stream begin + 1 feed
        await s.FinishAsync(Samples(2560), TestContext.Current.CancellationToken); // finalize (buffer empty: no tail feed)

        var stats = Assert.IsAssignableFrom<INativeCallStatsSource>(s).NativeCallStats;
        Assert.Equal(3, stats.Count); // begin + feed + finalize
        Assert.Equal(0, stats.CountOver250Ms);
        Assert.True(stats.MaxMs <= stats.TotalMs);
    }

    [Fact]
    public async Task Session_CountsCallsAtOrOver250msAsSlow()
    {
        var engine = new FakeTranscribeCppEngine { FeedDelay = TimeSpan.FromMilliseconds(300) };
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en");
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken); // one ~300 ms feed

        var stats = ((INativeCallStatsSource)s).NativeCallStats;
        Assert.True(stats.CountOver250Ms >= 1);
        Assert.True(stats.MaxMs >= 250);
        Assert.True(stats.Count >= 2); // begin + feed at minimum
    }

    [Fact]
    public async Task DisposeAsync_CountsStreamDisposeAsNativeCall()
    {
        var engine = new FakeTranscribeCppEngine();
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en");
        var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);
        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken); // stream begin + feed
        var before = ((INativeCallStatsSource)s).NativeCallStats.Count;

        await s.DisposeAsync();

        var after = ((INativeCallStatsSource)s).NativeCallStats.Count;
        Assert.Equal(before + 1, after); // "stream dispose" is a timed native call
    }
```

(If `FeedDelay` in `FakeTranscribeCppEngine` also delays `BeginStream`, the
`>= 1` / `>= 250` assertions still hold — they are deliberately tolerant.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
```
Expected: FAIL — CS0246 (`INativeCallStatsSource` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/NativeCallStats.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Per-dictation aggregate of the synchronous native streaming calls
/// (stream begin / feed / finalize). Complements the >=3 s TimedNativeCall
/// warnings: below that threshold calls are individually silent by design
/// (log-volume discipline), so the aggregate is what distinguishes "one
/// 2.9 s call" from "many 250 ms calls" after the fact.</summary>
/// <param name="Count">Total native calls this session.</param>
/// <param name="TotalMs">Sum of all native call durations.</param>
/// <param name="MaxMs">Slowest single call.</param>
/// <param name="CountOver250Ms">Calls taking >= 250 ms — already ~1.5x
/// real time for a 160 ms feed chunk, i.e. pathological yet silent today.</param>
public sealed record NativeCallStats(int Count, int TotalMs, int MaxMs, int CountOver250Ms);

/// <summary>Optional side-channel on a streaming session that aggregates
/// native-call timings. Probed with <c>as</c> by StreamingDictationSession at
/// finish; sessions with no native calls simply don't implement it.</summary>
public interface INativeCallStatsSource
{
    /// <summary>Thread-safe snapshot of the aggregates so far.</summary>
    NativeCallStats NativeCallStats { get; }
}
```

In `NemotronStreamingTranscriber.cs`:

1. The nested class declaration gains the interface:

```csharp
    private sealed class Session : IStreamingTranscriptionSession, INativeCallStatsSource
```

2. Add fields beside `_buffered`/`_streamed` (all mutated only inside
`lock (_nativeGate)` — every `TimedNativeCall` call site already holds it —
so plain fields are safe):

```csharp
        // Native-call aggregates: mutated in TimedNativeCall's finally, which
        // always runs under _nativeGate (every call site holds it), so plain
        // fields need no interlocking; the snapshot getter takes the gate.
        private int _nativeCalls;
        private long _nativeTotalMs;
        private long _nativeMaxMs;
        private int _nativeOver250;
        internal const int SlowNativeCallMs = 250;
```

3. Add the snapshot property inside `Session`:

```csharp
        public NativeCallStats NativeCallStats
        {
            get
            {
                lock (_nativeGate)
                {
                    return new NativeCallStats(_nativeCalls, (int)_nativeTotalMs, (int)_nativeMaxMs, _nativeOver250);
                }
            }
        }
```

4. Extend `TimedNativeCall`'s `finally` (keep everything already there — the
watchdog cancel and the ≥ threshold warning are unchanged; the `finally` runs
even when the native call throws, so corrupt paths still count):

```csharp
            finally
            {
                watchdogCts.Cancel();
                nativeSw.Stop();
                var elapsedMs = nativeSw.ElapsedMilliseconds;
                _nativeCalls++;
                _nativeTotalMs += elapsedMs;
                if (elapsedMs > _nativeMaxMs) _nativeMaxMs = elapsedMs;
                if (elapsedMs >= SlowNativeCallMs) _nativeOver250++;
                if (nativeSw.Elapsed >= _nativeCallWarnAfter)
                    _log?.LogWarning(
                        "nemotron native {Op} took {ElapsedMs} ms; a call this slow stalls the streaming pump until it returns",
                        op, (int)elapsedMs);
            }
```

5. Wrap the native stream dispose in `Session.DisposeAsync` (validated gap:
today `_stream?.Dispose()` is a raw P/Invoke into the same library that
produced 4.5–14 s single calls in the wild, running INSIDE the `asr=` window
but outside every timed span). Keep the existing body/semantics — take
`_nativeGate` exactly as today, only route the `Dispose()` call through
`TimedNativeCall`:

```csharp
            lock (_nativeGate)
            {
                if (_stream is { } stream) // capture a local: null-state analysis doesn't flow into lambdas (repo builds with WarningsAsErrors=nullable)
                    TimedNativeCall("stream dispose", () => { stream.Dispose(); return true; });
                // ... any existing null-out / state updates unchanged ...
            }
```

(If `TimedNativeCall` has a void/`Action` overload, use it; otherwise the
dummy-return `Func<T>` form above matches the existing call-site style. NOTE:
this feeds the ≥3000 ms WRN and the per-session counters, but the timing
LINE's `native_*` aggregates deliberately exclude it — Task 4 snapshots
`NativeCallStats` before dispose runs. Attribution for a slow dispose comes
from the WRN + the residue derivation in the Schema Change Note.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` — including the 3 pre-existing `TimedNativeCall` warning tests, unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/NativeCallStats.cs src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs
git commit -m "feat(asr): per-session native-call stats aggregate on nemotron streaming session" -m "TimedNativeCall now counts every call (count/total/max/over-250ms) under the existing native gate; no per-call logging below the 3 s warn threshold. Exposed via INativeCallStatsSource for the timing line." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 4: `StreamingFinishStats` — asr_wait/asr_native spans + backlog counters

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`

**Interfaces:**
- Consumes: Task 3's `NativeCallStats` / `INativeCallStatsSource`; existing test doubles in the test file (`RecordingStreamingTranscriber`, `PermanentlyWedgedTranscriber` with `Session.Unwedge()`).
- Produces (Task 7 consumes):

```csharp
public sealed record StreamingFinishStats(
    int AsrWaitMs,
    int? AsrNativeMs,
    int BacklogFrames,
    int BacklogMs,
    NativeCallStats? NativeCallStats);
```
  and `public StreamingFinishStats? FinishStats { get; private set; }` on
  `StreamingDictationSession`, set by `FinishAsync` on every path (including
  drain-timeout abandon, where the result is `null`).

Hazards: tests `AbandonWithQueuedFrames_PumpDrainsWithoutError` and
`TeardownCancel_MidDrain_IsBenign_NoPumpFailureWarning` assert
`log.Warnings.ShouldBeEmpty()` — this task adds NO logging at all, so they
stay green. Do not read `NativeCallStats` on the drain-timeout path: the
snapshot getter takes `_nativeGate`, which a wedged native call is holding.

- [ ] **Step 1: Write the failing tests**

Append to `StreamingDictationSessionTests.cs` (Shouldly style; add a local fake at the bottom of the class):

```csharp
    [Fact]
    public async Task FinishAsync_ReportsBacklogAndSpans_InFinishStats()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var gate = new TaskCompletionSource<IStreamingTranscriber?>();
        var session = StreamingDictationSession.Start(
            _ => gate.Task, NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);
        session.OnFrame(new float[800]);
        session.OnFrame(new float[800]);

        // FinishAsync runs synchronously up to its first await, capturing the
        // backlog BEFORE the transcriber (and thus the pump's pushes) exists.
        var finish = session.FinishAsync(new float[9], TestContext.Current.CancellationToken);
        gate.SetResult(transcriber);

        (await finish).ShouldNotBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.BacklogFrames.ShouldBe(3);
        stats.BacklogMs.ShouldBe(150); // 2400 samples / 16 per ms
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(0);
        stats.AsrNativeMs.ShouldNotBeNull();
    }

    [Fact]
    public async Task FinishAsync_DrainTimeout_StillReportsWaitAndBacklog()
    {
        var transcriber = new PermanentlyWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push
        session.OnFrame(new float[800]); // stays queued behind the wedge

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrWaitMs.ShouldBeGreaterThanOrEqualTo(150); // paid the ~200 ms deadline
        stats.AsrNativeMs.ShouldBeNull();                  // inner finish never ran
        stats.NativeCallStats.ShouldBeNull();              // never probed on abandon (gate may be wedged)
        // Frame 1 may or may not have been dequeued by the pump before
        // FinishAsync captured the backlog — both are legitimate.
        stats.BacklogFrames.ShouldBeInRange(1, 2);

        transcriber.Session.Unwedge();
        await session.PumpCompletion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FinishAsync_SurfacesNativeCallStats_WhenSessionExposesThem()
    {
        var transcriber = new StatsExposingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).ShouldNotBeNull();

        session.FinishStats.ShouldNotBeNull()
            .NativeCallStats.ShouldBe(new NativeCallStats(7, 900, 400, 2));
    }

    [Fact]
    public async Task FinishAsync_NullFactory_StillSetsFinishStats()
    {
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(null),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        var stats = session.FinishStats.ShouldNotBeNull();
        stats.AsrNativeMs.ShouldBeNull(); // no session ever materialized
    }

    private sealed class StatsExposingTranscriber : IStreamingTranscriber
    {
        public string ModelName => "stats";
        public StatsSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class StatsSession : IStreamingTranscriptionSession, INativeCallStatsSource
        {
            public NativeCallStats NativeCallStats { get; } = new(7, 900, 400, 2);
            public ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct) => ValueTask.CompletedTask;
            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => Task.FromResult(new TranscriptionResult("OK", "stats"));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
```

(If Shouldly's `ShouldNotBeNull()` chaining reads awkwardly in the third test,
split into two statements — behavior is what matters.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
```
Expected: FAIL — CS1061 (`FinishStats` not defined).

- [ ] **Step 3: Implement in `StreamingDictationSession.cs`**

1. Above the class, add the record:

```csharp
/// <summary>Out-of-band per-finish metrics, mirroring the DrainTimedOut /
/// PumpCompletion precedent: set by FinishAsync on EVERY path — including the
/// drain-timeout abandon, where no TranscriptionResult exists and a
/// result-attached metadata slot could never carry them. AsrWaitMs is the
/// pump wait (_pump.WaitAsync) — usually the backlog drain, but it also spans
/// session STARTUP work (cold factory/model load, cloud connect) when the
/// session was still starting at stop, so read it with backlog_ms.
/// AsrNativeMs spans the inner
/// session's FinishAsync — tail feed + finalize on the streaming happy path;
/// includes batch-fallback time when the transcriber falls back internally
/// (asr_mode=batch on the timing line reveals that case). Backlog is what was
/// queued-but-not-yet-pumped at finish entry (frames, and samples/16 as ms —
/// samples because the pre-roll frame is oversized).</summary>
public sealed record StreamingFinishStats(
    int AsrWaitMs,
    int? AsrNativeMs,
    int BacklogFrames,
    int BacklogMs,
    NativeCallStats? NativeCallStats);
```

2. Add fields to the class (beside `_pumpError`):

```csharp
    private const int SamplesPerMs = 16; // mono 16 kHz

    // Queue-depth counters: incremented on successful TryWrite (capture
    // thread), decremented at every dequeue site (pump thread). Interlocked
    // because writer and reader are different threads; read once at finish.
    private int _queuedFrames;
    private long _queuedSamples;
```

3. Add the property (beside `DrainTimedOut`):

```csharp
    /// <summary>Per-finish metrics; non-null after FinishAsync returns
    /// (any outcome). See <see cref="StreamingFinishStats"/>.</summary>
    public StreamingFinishStats? FinishStats { get; private set; }
```

4. Replace `OnFrame`:

```csharp
    /// <summary>Called from the recorder's FramesAvailable event. Copies the frame
    /// (the recorder may reuse its buffer) and never blocks the capture thread.</summary>
    public void OnFrame(ReadOnlyMemory<float> frame)
    {
        var copy = frame.ToArray();
        if (_frames.Writer.TryWrite(copy)) // TryWrite is false after completion — silent drop
        {
            Interlocked.Increment(ref _queuedFrames);
            Interlocked.Add(ref _queuedSamples, copy.Length);
        }
    }
```

5. In the pump, decrement at every dequeue site. The main loop becomes:

```csharp
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -frame.Length);
                    await session.PushAsync(frame, ct);
                    _anyPushCompleted = true; // keys FinishAsync's drain-deadline choice
                }
```

the null-transcriber drain becomes:

```csharp
                    // No provider available: drain and drop so nothing accumulates.
                    await foreach (var dropped in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    {
                        Interlocked.Decrement(ref _queuedFrames);
                        Interlocked.Add(ref _queuedSamples, -dropped.Length);
                    }
                    return;
```

and the catch-block leftover drain becomes:

```csharp
                while (_frames.Reader.TryRead(out var leftover)) // unblock nothing-in-particular; drop leftovers
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -leftover.Length);
                }
```

6. Rework `FinishAsync` — full replacement (existing comments preserved; the
only behavior additions are the stopwatch reads, backlog capture, and
`FinishStats` assignments):

```csharp
    public async Task<TranscriptionResult?> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
    {
        // Backlog snapshot BEFORE completing the writer: frames queued but not
        // yet pumped. asr_wait below is the price of draining exactly this.
        var backlogFrames = Volatile.Read(ref _queuedFrames);
        var backlogMs = (int)(Interlocked.Read(ref _queuedSamples) / SamplesPerMs);
        _frames.Writer.TryComplete();
        // Short-circuit ONLY a session that actually started and still
        // completed zero pushes; a session still starting (cloud connect,
        // cold factory load) keeps the full deadline.
        var deadline = _anyPushCompleted || !_sessionStarted
            ? _drainDeadline
            : TimeSpan.FromTicks(Math.Min(_drainDeadline.Ticks, ZeroPushDrainDeadline.Ticks));
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _pump.WaitAsync(deadline, ct); // TimeoutException on a wedged drain
        }
        catch (TimeoutException)
        {
            // A wedged push HANGS rather than throws (half-dead socket send,
            // or a stuck synchronous native P/Invoke), so no exception-based
            // fallback inside the session/wrapper can fire. Bound the whole
            // post-stop wait HERE: abandon the session and return null so the
            // caller's late path transcribes fullAudio (bounded, batch). The
            // session dispose runs in the BACKGROUND: awaiting it inline can
            // block for as long as the wedged native call takes (observed
            // ~16 s in the wild — dispose cannot interrupt a P/Invoke, it just
            // queues behind the session's native gate). Callers that see
            // DrainTimedOut coordinate shared-native disposal via
            // PumpCompletion, exactly as before.
            // Stats note: never probe INativeCallStatsSource here — its
            // snapshot takes the native gate, which the wedged call is holding.
            waitSw.Stop();
            DrainTimedOut = true;
            FinishStats = new StreamingFinishStats(
                (int)waitSw.ElapsedMilliseconds, null, backlogFrames, backlogMs, null);
            _log.LogWarning(
                "streaming drain exceeded {DrainDeadline}; abandoning streaming session, batch path takes over",
                deadline);
            _ = ScheduleAbandonedSessionDispose();
            return null;
        }
        waitSw.Stop();
        var asrWaitMs = (int)waitSw.ElapsedMilliseconds;
        // Captured on the pump task; rethrow via ExceptionDispatchInfo so the
        // original stack trace survives this cross-thread rethrow.
        if (_pumpError is not null)
        {
            FinishStats = new StreamingFinishStats(asrWaitMs, null, backlogFrames, backlogMs, null);
            ExceptionDispatchInfo.Capture(_pumpError).Throw();
        }
        var session = _session;
        if (session is null)
        {
            FinishStats = new StreamingFinishStats(asrWaitMs, null, backlogFrames, backlogMs, null);
            return null;
        }
        var nativeSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            return await session.FinishAsync(fullAudio, ct);
        }
        finally
        {
            // Ordering: finish first, then dispose (FallbackStreamingTranscriber's
            // push-after-dispose guard documents why). The finally keeps the
            // session from leaking when FinishAsync throws — that exception
            // deliberately propagates to the pipeline (batch parity).
            nativeSw.Stop();
            FinishStats = new StreamingFinishStats(
                asrWaitMs,
                (int)nativeSw.ElapsedMilliseconds,
                backlogFrames,
                backlogMs,
                (session as INativeCallStatsSource)?.NativeCallStats);
            await DisposeSessionAsync();
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` — all 16 pre-existing StreamingDictationSession facts plus the 4 new ones.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
git commit -m "feat(asr): streaming finish stats -- asr_wait/asr_native spans + queued-frame backlog" -m "FinishStats is out-of-band (DrainTimedOut precedent) so the drain-timeout abandon path reports too. Backlog via Interlocked counters (Channel Reader.Count not relied on); backlog_ms from samples/16 because the pre-roll frame is oversized." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 5: Allocation fix — pooled per-frame copy (the ONE behavior change)

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`
- Modify: `src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs` (doc contract only)
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`

**Interfaces:**
- Consumes: Task 4's counters and pump structure.
- Produces: no API change — `OnFrame(ReadOnlyMemory<float>)`,
  `PushAsync(ReadOnlyMemory<float>, ct)` signatures unchanged. Internal
  channel element becomes a private `PooledFrame` struct.

**Frame lifetime reasoning (verified against the code — record in the commit):**

- *Producer side:* every array reaching `OnFrame` is freshly allocated per
  capture callback (`WasapiCaptureSource.DecodeToMono`/`Resample` `new float[...]`,
  `WarmCaptureBuffer.StartSession` `_session.ToArray()`); the producer never
  reuses it. Today's `ToArray()` is therefore defensive-only — BUT the copy
  contract is pinned by `OnFrame_CopiesTheFrame_BeforeTheRecorderReusesItsBuffer`
  and the interface doc ("the recorder may reuse its buffer"), so we KEEP a
  copy and merely make it pooled. (Spec explicitly blesses this: "a pooled
  copy at enqueue is still fine since we copy anyway today".)
- *Consumer side (the actual pooling hazard — may the pump return the buffer
  after `PushAsync` completes?):* every `IStreamingTranscriptionSession.PushAsync`
  implementation fully consumes the samples before its returned ValueTask
  completes: `NemotronStreamingTranscriber.Session` copies into its fixed
  `float[2560]` under `_nativeGate`; `BatchStreamingAdapter` ignores the
  buffer; `AssemblyAiStreamingSession.BufferSamples` copies the span into its
  own `List<float> _sendBuffer` before returning/flushing; 
  `ParakeetStreamingSession` transforms to mel frames synchronously;
  `FallbackStreamingTranscriber.Session` only delegates to one of the above.
  No implementation retains the incoming memory. Returning the buffer after
  `await PushAsync(...)` is therefore safe. Step 4 adds this as an explicit
  interface contract so future implementations stay safe.

- [ ] **Step 1: Write the failing test**

The change must be invisible except for allocations, so the existing suite is
the main pin. Add ONE new fact guarding the length-slicing hazard (pooled
arrays may be larger than the frame):

```csharp
    [Fact]
    public async Task OnFrame_PooledCopy_PreservesExactLengthAndContent()
    {
        var transcriber = new RecordingStreamingTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        var frame = new float[800];
        for (var i = 0; i < frame.Length; i++) frame[i] = i;
        session.OnFrame(frame);
        session.OnFrame(new float[] { 1f, 2f, 3f }); // pool rounds the rented array up

        await session.FinishAsync(new float[1], TestContext.Current.CancellationToken);

        transcriber.Session.Pushed[0].Length.ShouldBe(800); // NOT the pool bucket size
        transcriber.Session.Pushed[0][799].ShouldBe(799f);
        transcriber.Session.Pushed[1].ShouldBe(new[] { 1f, 2f, 3f });
    }
```

- [ ] **Step 2: Run the new test to see it pass against the CURRENT code, then hold it as the pin**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*StreamingDictationSession*" -notrait "Platform=Windows"
```
Expected: PASS (it pins behavior that must survive the refactor — this is a
behavior-preserving change, so "red" here is the refactor breaking any pin,
not a new failing assertion).

- [ ] **Step 3: Implement the pooled copy**

In `StreamingDictationSession.cs`:

1. Add `using System.Buffers;` to the usings.

2. Add a private struct (below the fields):

```csharp
    /// <summary>A rented buffer + its real length (ArrayPool rounds up).
    /// Ownership: OnFrame rents; whichever dequeue site consumes it returns it.
    /// Frames never dequeued (channel dropped with the object) are simply
    /// collected — ArrayPool does not require returns for correctness.</summary>
    private readonly struct PooledFrame
    {
        public PooledFrame(float[] buffer, int length) { Buffer = buffer; Length = length; }
        public float[] Buffer { get; }
        public int Length { get; }
        public ReadOnlyMemory<float> Memory => Buffer.AsMemory(0, Length);
    }
```

3. Change the channel declaration:

```csharp
    private readonly Channel<PooledFrame> _frames = Channel.CreateUnbounded<PooledFrame>(
        new UnboundedChannelOptions { SingleReader = true });
```

4. Replace `OnFrame`:

```csharp
    /// <summary>Called from the recorder's FramesAvailable event. Copies the
    /// frame into a POOLED buffer (defensive copy kept — the recorder contract
    /// allows buffer reuse — but without the per-frame float[] churn that
    /// feeds GC-pause suspicion: ~20 x 800-float allocations/s previously).
    /// Never blocks the capture thread.</summary>
    public void OnFrame(ReadOnlyMemory<float> frame)
    {
        var buffer = ArrayPool<float>.Shared.Rent(frame.Length);
        frame.Span.CopyTo(buffer);
        if (_frames.Writer.TryWrite(new PooledFrame(buffer, frame.Length)))
        {
            Interlocked.Increment(ref _queuedFrames);
            Interlocked.Add(ref _queuedSamples, frame.Length);
        }
        else
        {
            ArrayPool<float>.Shared.Return(buffer); // TryWrite false after completion — silent drop
        }
    }
```

5. Update every dequeue site to return buffers. Main pump loop:

```csharp
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -frame.Length);
                    try
                    {
                        await session.PushAsync(frame.Memory, ct);
                    }
                    finally
                    {
                        // Safe: every PushAsync implementation consumes the
                        // samples before its ValueTask completes (contract on
                        // IStreamingTranscriptionSession.PushAsync).
                        ArrayPool<float>.Shared.Return(frame.Buffer);
                    }
                    _anyPushCompleted = true; // keys FinishAsync's drain-deadline choice
                }
```

Null-transcriber drain:

```csharp
                    // No provider available: drain and drop so nothing accumulates.
                    await foreach (var dropped in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    {
                        Interlocked.Decrement(ref _queuedFrames);
                        Interlocked.Add(ref _queuedSamples, -dropped.Length);
                        ArrayPool<float>.Shared.Return(dropped.Buffer);
                    }
                    return;
```

Catch-block leftover drain:

```csharp
                while (_frames.Reader.TryRead(out var leftover)) // unblock nothing-in-particular; drop leftovers
                {
                    Interlocked.Decrement(ref _queuedFrames);
                    Interlocked.Add(ref _queuedSamples, -leftover.Length);
                    ArrayPool<float>.Shared.Return(leftover.Buffer);
                }
```

- [ ] **Step 4: Document the buffer-lifetime contract on the seam**

In `src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs`, extend the
`PushAsync` doc comment (keep the existing text, append):

```csharp
    /// <summary>Feed mono 16 kHz float samples captured during recording. May do
    /// heavy work (inference / network sends) — callers pump from a background task.
    /// CONTRACT: a push arriving after DisposeAsync must be a benign no-op — the
    /// coordinator's pump legitimately drains queued frames after an abandon.
    /// CONTRACT: the samples are only valid until the returned ValueTask
    /// completes — the caller returns the buffer to a pool afterwards.
    /// Implementations that retain audio past completion must copy it first
    /// (all current implementations already copy or fully consume synchronously).</summary>
    ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);
```

- [ ] **Step 5: Run the full Linux suite (the existing streaming tests are the behavior pin)**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` — in particular
`OnFrame_CopiesTheFrame_BeforeTheRecorderReusesItsBuffer` (copy contract),
`FramesQueuedBeforeTheSessionIsReady_AreDeliveredInOrder` (ordering),
`FramesAfterFinish_AreDroppedSilently` (post-complete drop), all Task 4 facts,
and the new length/content pin.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs src/Winpepper.Asr/Transcription/IStreamingTranscriber.cs tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
git commit -m "perf(asr): pooled per-frame copy in StreamingDictationSession (ArrayPool)" -m "Keeps the defensive-copy contract (test-pinned) while eliminating ~20 float[800] allocations/s of GC churn. Lifetime verified: no PushAsync implementation (nemotron/batch/assemblyai/parakeet/fallback) retains the buffer past ValueTask completion; contract now documented on the seam. Buffers returned at every dequeue site." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 6: Cleanup pre-warm markers — in-flight tracking + start/finish INF logs

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupBackendHolder.cs`
- Modify: `tests/Winpepper.Cleanup.Tests/Fakes/CollectingLogger.cs` (locate with `grep -rn "class CollectingLogger" tests/` — it currently records Warnings only)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs`

**Interfaces:**
- Consumes: existing holder internals (`_pending`, `StartPrewarmLocked`, `LoadCore`, `_gate`); test `Harness` (fields `Desired`, `FactoryGate`, `Log`; helper `DictateUntilLoaded(string)`).
- Produces (Task 7 consumes): `public bool WasPrewarmActiveSince(long sinceTickCount64)` on `CleanupBackendHolder` — lock-free, `Environment.TickCount64`-based.

**Verification note (spec asked us to check for duplicates):** there are NO
existing pre-warm start/finish logs. `StartPrewarmLocked` and `LoadCore` are
silent on the happy path; the only INF proof is `"Cleanup model loaded (swap
#{Generation}): ..."` which fires at the NEXT dictation's seam, and
`LlamaCleanupBackend`'s `"Loading cleanup model: {Path}"` /
`"Cleanup model loaded."` which bracket only the native load, carry a path
(not the resolved model name) and no duration; the warm-up half is DBG-only.
The new lines wrap the whole verify+load+warm-up span — not duplication.

**Threading note:** do NOT add `_gate` acquisition to the dictation path —
`Dispose()` holds `_gate` across a bounded 5 s `_pending.Load.Wait()`, so a
lock-taking probe is not free. Use `Volatile`/`Interlocked` fields instead.

- [ ] **Step 1: Extend `CollectingLogger` to also collect INF lines**

The prewarm start/finish lines are INF; the logger currently records Warning
only. Keep `Warnings` semantics untouched (other tests assert on it):

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>Collects warning log lines so tests can assert observability
/// (e.g. window-context truncation) is LOUD, not silent. Also collects
/// Information lines (prewarm start/finish markers).</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
        else if (logLevel == LogLevel.Information)
            Infos.Add(formatter(state, exception));
    }
}
```

(Tests read `Infos` only after the load task is observed complete via
`DictateUntilLoaded`, so the unsynchronized `List` is safe — same discipline
the existing `Warnings` usage relies on.)

- [ ] **Step 2: Write the failing tests**

Append to `CleanupBackendHolderTests.cs` (Shouldly style, reusing `Harness`):

```csharp
    [Fact]
    public void WasPrewarmActiveSince_NoPrewarmEver_IsFalse()
    {
        var h = new Harness { Desired = "model-a" };

        h.Holder.WasPrewarmActiveSince(0).ShouldBeFalse();
    }

    [Fact]
    public void WasPrewarmActiveSince_WhileLoadInFlight_IsTrue()
    {
        // Local gate variable: FactoryGate is a nullable field and the repo
        // builds with WarningsAsErrors=nullable.
        using var gate = new ManualResetEventSlim(false);
        var h = new Harness { Desired = "model-a", FactoryGate = gate };
        try
        {
            h.Holder.RequestPrewarm(); // load blocks on the factory gate

            h.Holder.WasPrewarmActiveSince(Environment.TickCount64).ShouldBeTrue();
        }
        finally
        {
            gate.Set(); // release so the background task can finish
        }
        h.DictateUntilLoaded("model-a"); // drain the load before the test ends
    }

    [Fact]
    public void WasPrewarmActiveSince_AfterCompletion_ReflectsTheWindow()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a"); // adoption implies the load task completed

        h.Holder.WasPrewarmActiveSince(0).ShouldBeTrue();                               // window overlaps the prewarm
        h.Holder.WasPrewarmActiveSince(Environment.TickCount64 + 1000).ShouldBeFalse(); // window strictly after it
    }

    [Fact]
    public void Prewarm_LogsStartAndFinish_WithDurationAndModelName()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Log.Infos.ShouldContain(m => m.Contains("cleanup prewarm started") && m.Contains("model-a"));
        h.Log.Infos.ShouldContain(m => m.Contains("cleanup prewarm finished") && m.Contains("model-a") && m.Contains("ms"));
    }
```

(`Harness.FactoryGate` is a public `ManualResetEventSlim?` field — settable
via object initializer; if the existing wedge-simulation tests in the same
file use a different idiom, match it.)

- [ ] **Step 3: Run tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q
```
Expected: FAIL — CS1061 (`WasPrewarmActiveSince` not defined).

- [ ] **Step 4: Implement in `CleanupBackendHolder.cs`**

1. Add fields (beside `_pending`):

```csharp
    // Prewarm-activity markers for the dictation timing line. Lock-free on
    // purpose: the dictation path must never contend with _gate (Dispose
    // holds it across a bounded 5 s pending-load wait). In-flight count is
    // bumped under _gate in StartPrewarmLocked BEFORE the Task.Run, and
    // dropped in LoadCore's finally; the end-ticks stamp uses
    // Environment.TickCount64 (monotonic, matches PipelineHost's window start).
    private int _prewarmInFlight;
    private long _prewarmLastEndTicks = long.MinValue;
```

2. Add the public query (after `LoadedModelName`):

```csharp
    /// <summary>True when a background pre-warm (model load + warm-up
    /// inference) was in flight at any point between
    /// <paramref name="sinceTickCount64"/> (an Environment.TickCount64
    /// reading) and now. Lock-free — safe on the dictation path.</summary>
    public bool WasPrewarmActiveSince(long sinceTickCount64)
    {
        if (Volatile.Read(ref _prewarmInFlight) > 0) return true;
        return Interlocked.Read(ref _prewarmLastEndTicks) >= sinceTickCount64;
    }
```

3. In `StartPrewarmLocked`, change the task-creation tail (last two lines of the method):

```csharp
        var captured = target;
        Interlocked.Increment(ref _prewarmInFlight); // under _gate, before the task exists — no in-flight gap
        _pending = new PendingPrewarm(captured, Task.Run(() => LoadCore(captured)));
```

4. In `LoadCore`, add the stopwatch + start INF at the top, the finish INF on
the success return, and a `finally` on the EXISTING outer try (the one whose
`catch (Exception ex)` logs "failed to load"). Shape:

```csharp
    private PrewarmResult? LoadCore(CleanupModelTarget target)
    {
        var prewarmSw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation("cleanup prewarm started: {ModelName}", target.ResolvedName);
        try
        {
            // ... existing body unchanged (gguf-null check, _verifyReady,
            //     _backendFactory, runner, inner try/catch with warm-up) ...
            //     EXCEPT: immediately before the existing
            //     `return new PrewarmResult(backend, runner);` add:
            _log.LogInformation(
                "cleanup prewarm finished: {ModelName} in {ElapsedMs} ms (load + warm-up)",
                target.ResolvedName, (int)prewarmSw.ElapsedMilliseconds);
            return new PrewarmResult(backend, runner);
        }
        catch (Exception ex)
        {
            // ... existing "failed to load" WRN unchanged (model name already
            //     present; duration derivable from the start INF timestamp) ...
        }
        finally
        {
            Interlocked.Exchange(ref _prewarmLastEndTicks, Environment.TickCount64);
            Interlocked.Decrement(ref _prewarmInFlight);
        }
    }
```

The two early-`return null` paths (no gguf, failed verification) exit through
the `finally` too — the in-flight window covers verify+load+warm exactly.

- [ ] **Step 5: Run tests to verify they pass**

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` — all pre-existing `CleanupBackendHolderTests` facts (which assert on `Warnings`) unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupBackendHolder.cs tests/Winpepper.Cleanup.Tests
git commit -m "feat(cleanup): prewarm in-flight tracking + start/finish INF logs with duration" -m "WasPrewarmActiveSince(TickCount64) is lock-free (never contends with Dispose's bounded gate wait). Start/finish INF lines carry model name + elapsed; verified no such logs existed (LlamaCleanupBackend's bracket only the native load, path-only, no duration). Feeds the prewarm_active= regression marker on the timing line." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 7: PipelineHost wiring — stamp everything onto the timing line (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (both arms: hold ~`:500`–`:700`, toggle ~`:995`–`:1195`; `EmitTimingSummary` ~`:1486`)

**Interfaces:**
- Consumes: `DictationTimingSummary` fields (Task 2), `StreamingDictationSession.FinishStats` (Task 4), `CleanupBackendHolder.WasPrewarmActiveSince(long)` (Task 6), existing `_cleanupHolder` field (already injected — no new wiring), `GC.CollectionCount(int)`, `Environment.TickCount64`.
- Produces: the populated `dictation timing` INF line + `slow dictation stage asr_wait` WRNs in production logs.

**Testing note (house pattern, not a deferral):** PipelineHost has zero tests
by design — policy lives in Core/Asr/Cleanup and is Linux-tested above; this
task is pure stamping. Its compile is proven by the Task 8 Windows gate, and
the observable production outcome is the enriched timing line itself.

Line anchors below are pre-plan positions and will have drifted slightly by
Task 1's edits — locate by the quoted code, not the number. The toggle arm
mirrors the hold arm with `2`-suffixed locals (`timing2`, `streaming2` etc.) —
apply every edit to BOTH arms, matching the actual local names.

- [ ] **Step 1: Add host fields**

Near the other private fields (e.g. beside `_recordStopwatch`):

```csharp
    // Dictation-window baselines for the timing line: GC deltas + prewarm
    // overlap span recording start -> emit. Safe as host fields: the run
    // loop is serial (one dictation fully processed before the next).
    private long _dictStartTicks;
    private int _gcGen0AtStart;
    private int _gcGen1AtStart;
    private int _gcGen2AtStart;
    private System.TimeSpan _gcPauseAtStart;
```

- [ ] **Step 2: Capture baselines at recording start (BOTH arms)**

Immediately after `_recordStopwatch = System.Diagnostics.Stopwatch.StartNew();`
(hold arm ~`:506`, toggle arm ~`:1000`):

```csharp
                _dictStartTicks = Environment.TickCount64;
                _gcGen0AtStart = GC.CollectionCount(0);
                _gcGen1AtStart = GC.CollectionCount(1);
                _gcGen2AtStart = GC.CollectionCount(2);
                _gcPauseAtStart = GC.GetTotalPauseDuration();
```

- [ ] **Step 3: Add a stamping helper for FinishStats**

Near `NoteStreamingReleased` (~`:1444`):

```csharp
    /// <summary>Copy the streaming coordinator's out-of-band finish metrics
    /// onto the timing summary (asr_wait/asr_native split of asr=, queued
    /// backlog, native-call aggregates). Null-safe: no-op when streaming
    /// never existed or FinishAsync never ran.</summary>
    private static void StampStreamingFinishStats(
        Winpepper.Core.Diagnostics.DictationTimingSummary timing,
        Winpepper.Asr.Transcription.StreamingDictationSession? streaming)
    {
        if (streaming?.FinishStats is not { } fs) return;
        timing.AsrWaitMs = fs.AsrWaitMs;
        timing.AsrNativeMs = fs.AsrNativeMs;
        timing.BacklogFrames = fs.BacklogFrames;
        timing.BacklogMs = fs.BacklogMs;
        if (fs.NativeCallStats is { } ns)
        {
            timing.NativeCalls = ns.Count;
            timing.NativeTotalMs = ns.TotalMs;
            timing.NativeMaxMs = ns.MaxMs;
            timing.NativeOver250 = ns.CountOver250Ms;
        }
    }
```

- [ ] **Step 4: Call the helper at the asr stamping sites (BOTH arms, 2 sites each)**

(a) Normal path — directly after `timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;` (hold ~`:688`, toggle twin `timing2` ~`:1181`):

```csharp
                StampStreamingFinishStats(timing, streaming);
```

(b) ASR-failed terminal path — inside the `(!localReady && !cloudSelected) || asrNow is null` block, after `timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;` and before `EmitTimingSummary(timing);` (hold ~`:673`, toggle ~`:1166`):

```csharp
                        StampStreamingFinishStats(timing, streaming);
```

(the `streaming` local — grabbed-and-nulled from `_streamingSession` — is in
scope at both sites in each arm; the toggle arm's local may be `streaming2`).

- [ ] **Step 5: Stamp GC deltas + prewarm overlap in `EmitTimingSummary`**

`EmitTimingSummary` is the single funnel every terminal path calls (6 call
sites), so stamping here covers silent/failed/normal in both arms:

```csharp
    private void EmitTimingSummary(Winpepper.Core.Diagnostics.DictationTimingSummary timing)
    {
        // Window = recording start -> emit. prewarm_active correlates the
        // 07-28 regression suspect (cleanup pre-warm CPU load concurrent
        // with dictation) directly on the line; gc= deltas test the
        // GC-pause/allocation-churn hypothesis. Zero-cost reads.
        timing.GcGen0 = GC.CollectionCount(0) - _gcGen0AtStart;
        timing.GcGen1 = GC.CollectionCount(1) - _gcGen1AtStart;
        timing.GcGen2 = GC.CollectionCount(2) - _gcGen2AtStart;
        // GetTotalPauseDuration: cumulative process-wide GC pause time,
        // monotonic by construction (verified on .NET 9; includes background
        // GC's STW pauses, excludes its concurrent portion).
        timing.GcPauseMs = (int)(GC.GetTotalPauseDuration() - _gcPauseAtStart).TotalMilliseconds;
        timing.PrewarmActive = _cleanupHolder.WasPrewarmActiveSince(_dictStartTicks);
        _log.LogInformation("dictation timing {Summary}", timing.FormatLine());
        foreach (var o in timing.Overruns())
        {
            _log.LogWarning(
                "slow dictation stage {Stage}: {ActualMs} ms (budget {BudgetMs} ms), session {SessionId}",
                o.Stage, o.ActualMs, o.BudgetMs, timing.SessionId);
        }
    }
```

(If `_cleanupHolder` is declared nullable in this file, guard:
`if (_cleanupHolder is not null) timing.PrewarmActive = ...;` — leaving
`PrewarmActive` null omits the field, which is correct.)

- [ ] **Step 6: Sanity-check and run Linux tests**

- `grep -n "StampStreamingFinishStats" src/Winpepper.App/Hosting/PipelineHost.cs` → expect 5 hits (1 definition + 4 call sites).
- `grep -n "GcGen0AtStart\|_dictStartTicks" src/Winpepper.App/Hosting/PipelineHost.cs` → expect definitions + 2 capture sites (one per arm).

Run: `./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` (unchanged — PipelineHost is not built on Linux; the Task 8 gate proves this task compiles).

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): stamp streaming finish stats, GC deltas, prewarm overlap onto the timing line" -m "asr= now decomposes on the line into asr_wait/asr_native + backlog + native-call aggregates; gc=g0/g1/g2 and prewarm_active= cover the GC-churn and concurrent-prewarm stall hypotheses for the 07-28 regression window. Wiring only -- policy is Linux-tested in Core/Asr/Cleanup." -m "🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 8: Full verification — Linux suite + Windows gate

**Files:**
- None (verification only; fix-up commits if the gate finds anything).

**Interfaces:**
- Consumes: everything above.
- Produces: `LINUX SUITE: GREEN` + `GATE: GREEN` evidence.

- [ ] **Step 1: Run the Linux suite from a clean tree**

```bash
cd /home/dan/code/winpepper/.worktrees/asr-stall-instrumentation
git status --short   # expect empty (everything committed)
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 2: Run the Windows gate (from WSL, ~10 min; it pre-cleans all bin/obj — never run linux-tests.sh concurrently)**

```bash
./scripts/windows-gate.sh
```
Expected: `GATE: GREEN` (12 runs green; app build proves the PipelineHost
edits from Tasks 1 and 7 compile). Known transients — UNC MSB4025 parse
errors and vsock interop flakes — are NOT failures: retry the gate. Llama
cleanup tests may self-skip when the qwen GGUF is absent on the host; skips
keep the gate green.

- [ ] **Step 3: Fix anything real the gate finds**

If the gate finds a genuine compile/test failure (most likely: drifted line
anchors in PipelineHost or a missed `DrainMs` reference), fix minimally,
re-run `./scripts/linux-tests.sh` AND `./scripts/windows-gate.sh`, and commit
the fix with a focused message + the standard Amplifier trailer.

- [ ] **Step 4: Final state check — do NOT push**

```bash
git log --oneline main..HEAD   # expect the ~7 task commits
git status --short             # expect empty
```
Leave the branch local; the root session merges.

---

## Verification: what the logs look like afterwards

A healthy streaming dictation:

```
[INF] dictation timing session=... kind=hold outcome=completed rec=4200ms mic_stop=3ms trim=6ms asr=310ms asr_mode=streaming asr_model=nemotron-streaming-en asr_wait=120ms asr_native=185ms backlog=2 backlog_ms=100ms native_calls=27 native_total=800ms native_max=160ms native_over250=0 corrections=1ms cleanup=500ms ... gc=1/0/0 gc_pause=2ms prewarm_active=false total=1400ms
```

The stall signature this instrumentation exists to catch (e.g. the 08:58
`asr=3062ms` case) will now show WHERE the 3 s went — a large `asr_wait` +
large `backlog_ms` (comparable to `rec=`) + elevated `native_over250` means
feeds ran slower than real time during recording; a large `asr_wait` with
SMALL `backlog_ms` and a nearby engine/model-load INF is a cold start, not
slow feeds (validated: the pump wait spans session startup). A large
`gc_pause=` delta (the actual GC pause time — counts alone can't convey
magnitude) with high `native_max` points at GC pauses;
`prewarm_active=true` on exactly the stalled lines confirms the 07-28
pre-warm-contention regression hypothesis. A multi-second RESIDUE
(`asr − asr_wait − asr_native`) localizes to the native `stream dispose`
(see the Schema Change Note; a ≥3 s dispose also fires the TimedNativeCall
WRN). Two known unmarked residual suspects if a stalled line shows
`prewarm_active=false gc_pause≈0`: a Models-page ASR verify (cold ~1.1 GB
SHA-256 in Task.Run) and a History Lab rerun — both user-triggered and
pre-dating 07-28. Grep handles:
`dictation timing`, `slow dictation stage asr_wait`, `cleanup prewarm started`,
`cleanup prewarm finished`, `nemotron native stream dispose`.
