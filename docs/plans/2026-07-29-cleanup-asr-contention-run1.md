# Cleanup/ASR Contention — Run 1 Instrumentation Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ship Run 1 of the approved plan `docs/plans/2026-07-29-cleanup-asr-contention-fix.md`
(committed at `8f5db7d`): three new dictation-timing-line fields — `ctx_src`,
`over250_at`, `proc_cpu_ms` (step 0b) — plus evidence settling the LLamaSharp
`StatelessExecutor` native-context lifetime question (step 0c). Instrumentation
only; zero behavior change.

**Architecture:** All formatting for the three new fields lives in the pure
`DictationTimingSummary` helper (Linux-tested). Raw data rides existing seams:
slow-native-call start ticks are captured in `NemotronStreamingTranscriber.Session`
and flow through the existing `NativeCallStats → StreamingFinishStats →
StampStreamingFinishStats` chain; the consume-time window-context indicator is a
new `init` property on `CleanupRunner`'s RESULT record (its input signature stays
narrow); `PipelineHost` (Windows-only, untestable on Linux) does nothing but
sample two values and map indicator + `WindowContextResult.Source` to a string.
Step 0c is pure evidence-gathering into the evidence file — no code.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly (test runner: `dotnet exec <dll>`,
NEVER `dotnet test`), LLamaSharp 0.27.0 (NuGet, Windows TFM only).

## Global Constraints

- **Scope is Run 1 ONLY:** steps 0b + 0c of the approved plan. Do NOT implement
  1a, 1b, 1c, 1d, or anything from Phase 2 — those are Run 2, gated behind an
  owner-only step.
- **Zero behavior change.** Every production edit adds measurement or a data
  carrier. In particular: do NOT touch the existing `windowContextUsed` prompt
  sniff (`result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>")`) in
  `PipelineHost` — it feeds History and replacing it would be a behavior change.
- **The approved plan file is read-only.** All findings go to
  `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (already created and
  committed as the run-1 executor's first action; tasks below APPEND to it).
- **Mismatch rule (binding, from the approved plan):** line numbers and file
  paths in this document are pointers, not claims. If code is not at the stated
  location, search by filename or symbol under `src/` only, verify the claimed
  substance, record the corrected location in the evidence file, and continue.
  STOP only when the claimed code or behavior cannot be found anywhere under
  `src/`, or contradicts the claim in substance.
- **Paths:** all paths are relative to THIS worktree's root
  (`/home/dan/code/winpepper/.worktrees/cleanup-asr-contention-run1`). Work only
  under `src/`, `tests/`, `scripts/`, plus the evidence file under `docs/plans/`.
  Never read or edit files in any OTHER checkout or worktree.
- **Test gates (AGENTS.md, binding):** `./scripts/linux-tests.sh` must print
  `LINUX SUITE: GREEN` before EVERY commit. NEVER use `dotnet test`. Before the
  branch is declared done: `./scripts/windows-gate.sh` must print `GATE: GREEN`
  (UNC MSB4025 and vsock interop failures are known transient flakes — retry the
  gate). Never mix Linux- and Windows-side builds in the same `bin/`/`obj/`.
- **Commits:** conventional-commit subjects, each with Amplifier co-author
  attribution: `-m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"`.
- **Do NOT push to origin.** Leave branch `feat/cleanup-asr-contention-run1`
  local; the root session merges and installs.
- **Shell setup** (once per shell, if `dotnet` is not on PATH):
  `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"`.
- **New timing-line grammar (exact, all three optional — omitted when null):**
  - `over250_at=[1180,3420]` — ms offsets from recording start of each native
    call ≥ 250 ms, unclamped, first 16 only; `+N` suffix when more were dropped:
    `over250_at=[...]+3`. Placed immediately after `native_over250=`. Unclamped
    cuts BOTH ways: offsets may exceed the stop request (post-stop calls are
    evidence), and in rare cold-start races may be slightly NEGATIVE — the
    streaming session's `Start()` runs a few lines before `_dictStartTicks` is
    stamped, so a slow first native call can begin just before the stamp.
    Negative offsets are valid evidence, not bugs; do not clamp.
  - `ctx_src=uia|ocr|none` — placed immediately after `cleanup_model=`.
  - `proc_cpu_ms=1875` — placed immediately after `prewarm_active=`, before `total=`.

---

## File Structure

| File | Role in this plan |
|---|---|
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (modify) | Pure formatter: 4 new nullable properties (`Over250AtMs`, `Over250Overflow`, `CtxSrc`, `ProcCpuMs`), rendering in `FormatLine()`, and the pure `StampOver250(...)` tick→offset converter. No budget/`Overruns()` changes. |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` (modify) | Golden-line, omission, composite-rendering, and converter tests. |
| `src/Winpepper.Asr/Transcription/NativeCallStats.cs` (modify) | Record gains two optional components: `IReadOnlyList<long>? Over250StartTicks`, `int Over250Overflow` (defaults keep all existing construction sites compiling). |
| `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` (modify) | `Session.TimedNativeCall` captures `Environment.TickCount64` at call start for calls ≥ 250 ms; capped list of 16 + overflow counter; snapshot getter copies them out. |
| `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs` (modify) | Capture + cap/overflow tests using the existing `FakeTranscribeCppEngine` `FeedDelay` seam. |
| `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` (modify) | One propagation test: ticks survive the `StreamingFinishStats` ride (no production change needed there — `NativeCallStats` is embedded whole). |
| `src/Winpepper.Cleanup/CleanupResult.cs` (modify) | `bool? ConsumedWindowContext { get; init; }` on the result record. |
| `src/Winpepper.Cleanup/CleanupRunner.cs` (modify) | Sets the indicator in the bounded window-context wait; threads it onto every `RunAsync` return via `with`. |
| `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (modify) | Indicator semantics tests (null / true / false / survives fallback). |
| `src/Winpepper.App/Hosting/PipelineHost.cs` (modify; `#if WINDOWS`, no unit tests possible) | Sample `Process.TotalProcessorTime` at recording start + StopRequested (both arms); map indicator + `WindowContextResult.Source` → `CtxSrc` (both arms); pass `_dictStartTicks` into `StampStreamingFinishStats` for the over250 stamp. |
| `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append) | 0b implementation notes; 0c quoted evidence + verdict; mismatch-rule corrections. |

No new files are created (all changes extend existing homes; the evidence file
already exists).

---

### Task 1: Timing-line formatter — the three new fields (pure helper)

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (fields ~:47–81, `FormatLine()` ~:83–120; append helpers ~:140–168)
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` (`Full()` builder ~:10–45, golden test ~:48–63, null-omission test ~:201+)

**Interfaces:**
- Consumes: existing `AppendOptNum` / `AppendOptStr` private helpers; nothing from other tasks.
- Produces (later tasks rely on these exact members of `DictationTimingSummary`):
  - `public IReadOnlyList<int>? Over250AtMs { get; set; }`
  - `public int? Over250Overflow { get; set; }`
  - `public string? CtxSrc { get; set; }`
  - `public int? ProcCpuMs { get; set; }`
  - `public void StampOver250(IReadOnlyList<long> startTicks, int overflowCount, long recordingStartTicks)`

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

(a) Extend the `Full()` builder (~:10–45) with these property values, next to the
fields they relate to, matching the builder's existing style (object-initializer
or assignment — keep whichever the file uses):

```csharp
        // Over-250 diagnostics — keep the fixture self-consistent: max and the
        // over-250 count must agree with the offsets list.
        NativeMaxMs = 620,          // was 180
        NativeOver250 = 2,          // was 0
        Over250AtMs = new[] { 1180, 3420 },
        Over250Overflow = 0,
        CtxSrc = "uia",
        ProcCpuMs = 1875,
```

(b) Update the golden test `FormatLine_FullDictation_IsOneParseableKeyValueLine`
(~:48–63) — the expected string becomes exactly:

```csharp
        line.ShouldBe(
            "session=11111111-2222-3333-4444-555555555555 kind=hold outcome=completed"
            + " rec=3512ms mic_stop=42ms trim=8ms trim_removed=1200ms"
            + " asr=812ms asr_mode=streaming asr_model=nemotron-streaming-en"
            + " asr_wait=95ms asr_native=210ms backlog=2 backlog_ms=100ms"
            + " native_calls=74 native_total=1900ms native_max=620ms native_over250=2 over250_at=[1180,3420]"
            + " corrections=2ms cleanup=640ms cleanup_path=Llm cleanup_model=qwen2.5-1.5b ctx_src=uia"
            + " inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms"
            + " gc=1/0/0 gc_pause=12ms prewarm_active=true proc_cpu_ms=1875"
            + " total=2354ms");
```

(Mismatch rule: if the current golden string in the file differs from the base
shown here — e.g. a field was added since this plan was written — apply the SAME
three insertions to whatever the current string is: `over250_at=[1180,3420]`
after `native_over250=2`, `ctx_src=uia` after `cleanup_model=...`, and
`proc_cpu_ms=1875` after `prewarm_active=true`. Update `native_max`/`native_over250`
values as shown. Record any such correction in the evidence file.)

(c) Extend the null-omission test `FormatLine_NewDiagnosticFields_AreOmittedWhenNull`
(~:201+) — after its existing assertions, add:

```csharp
        line.ShouldNotContain("over250_at=");
        line.ShouldNotContain("ctx_src=");
        line.ShouldNotContain("proc_cpu_ms=");
```

(If that test constructs a minimal summary, the three new properties default to
null already — no arrange change needed.)

(d) Add four new facts at the end of the class:

```csharp
    [Fact]
    public void FormatLine_Over250_RendersOverflowSuffix_OnlyWhenPositive()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.Over250AtMs = new[] { 300, 5100 };
        t.Over250Overflow = 3;
        t.FormatLine().ShouldContain(" over250_at=[300,5100]+3");

        t.Over250Overflow = 0;
        t.FormatLine().ShouldContain(" over250_at=[300,5100]");
        t.FormatLine().ShouldNotContain("]+");
    }

    [Fact]
    public void FormatLine_Over250_EmptyList_IsOmitted()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.Over250AtMs = Array.Empty<int>();
        t.Over250Overflow = 0;
        t.FormatLine().ShouldNotContain("over250_at=");
    }

    [Fact]
    public void StampOver250_ConvertsTicksToOffsets_Unclamped()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        // Recording started at tick 10_000. Third entry is AFTER the stop
        // request — offsets are unclamped on purpose (post-stop offsets are
        // themselves evidence, per the approved plan's 0b).
        t.StampOver250(new long[] { 10_300, 12_000, 19_999 }, overflowCount: 1, recordingStartTicks: 10_000);
        t.Over250AtMs.ShouldBe(new[] { 300, 2000, 9999 });
        t.Over250Overflow.ShouldBe(1);
        t.FormatLine().ShouldContain(" over250_at=[300,2000,9999]+1");
    }

    [Fact]
    public void FormatLine_CtxSrcAndProcCpu_RenderAsPlainKeyValues()
    {
        var t = new DictationTimingSummary { SessionId = Guid.Empty, Kind = "hold" };
        t.CtxSrc = "none";
        t.ProcCpuMs = 42;
        var line = t.FormatLine();
        line.ShouldContain(" ctx_src=none");
        line.ShouldContain(" proc_cpu_ms=42");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS — `'DictationTimingSummary' does not contain a definition
for 'Over250AtMs'` (and the sibling properties). A compile failure IS the red
state here.

- [ ] **Step 3: Implement the formatter changes**

In `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`:

(a) Add to the property block (after `public bool? PrewarmActive { get; set; }`):

```csharp
    public IReadOnlyList<int>? Over250AtMs { get; set; }   // 0b: ms offsets from recording start of native calls >= 250 ms; capped upstream at 16 entries
    public int? Over250Overflow { get; set; }              // 0b: over-250 events beyond the 16-entry cap
    public string? CtxSrc { get; set; }                    // 0b: window context the cleanup LLM ACTUALLY consumed: uia|ocr|none (consume-time semantics)
    public int? ProcCpuMs { get; set; }                    // 0b: Process.TotalProcessorTime delta, recording start -> StopRequested (NOT emit)
```

(b) In `FormatLine()`, insert immediately after
`AppendOptNum(sb, "native_over250", NativeOver250);`:

```csharp
        if (Over250AtMs is { Count: > 0 } over250)
        {
            sb.Append(" over250_at=[");
            for (var i = 0; i < over250.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(over250[i]);
            }
            sb.Append(']');
            if (Over250Overflow is int extra and > 0) sb.Append('+').Append(extra);
        }
```

(c) In `FormatLine()`, insert immediately after
`AppendOptStr(sb, "cleanup_model", CleanupModel);`:

```csharp
        AppendOptStr(sb, "ctx_src", CtxSrc);
```

(d) In `FormatLine()`, insert immediately after the `prewarm_active` block
(`if (PrewarmActive is bool prewarm) ...`), before `AppendCoreMs(sb, "total", TotalMs);`:

```csharp
        AppendOptNum(sb, "proc_cpu_ms", ProcCpuMs);
```

(e) Add the converter method after `FormatLine()` (before `Overruns()`):

```csharp
    /// <summary>0b: convert absolute <see cref="Environment.TickCount64"/> stamps of
    /// slow native calls into ms offsets from recording start. Offsets are UNCLAMPED
    /// on purpose — values after the stop request are themselves evidence.</summary>
    public void StampOver250(IReadOnlyList<long> startTicks, int overflowCount, long recordingStartTicks)
    {
        var offsets = new int[startTicks.Count];
        for (var i = 0; i < startTicks.Count; i++)
            offsets[i] = (int)(startTicks[i] - recordingStartTicks);
        Over250AtMs = offsets;
        Over250Overflow = overflowCount;
    }
```

Do NOT touch the budget constants or `Overruns()` — the new fields have no budgets.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true \
  && dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait Platform=Windows
```

Expected: summary line ends with `Errors: 0` and `Failed: 0`.

- [ ] **Step 5: Append the 0b formatter notes to the evidence file**

Append under `## 0b — instrumentation added` in
`docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`:

```markdown
- Formatter (`src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`): added
  `Over250AtMs`/`Over250Overflow`/`CtxSrc`/`ProcCpuMs` + `StampOver250(...)`.
  Line grammar: `over250_at=[a,b,...]` (first 16, unclamped ms offsets from
  recording start) with `+N` overflow suffix; `ctx_src=uia|ocr|none` after
  `cleanup_model=`; `proc_cpu_ms=<n>` after `prewarm_active=`. All omitted when
  null. No budget/Overruns changes.
```

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs \
        tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs \
        docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "feat(core): over250_at, ctx_src, proc_cpu_ms on the dictation timing line formatter" \
           -m "Run 1 / step 0b of docs/plans/2026-07-29-cleanup-asr-contention-fix.md. Pure formatting only; producers land in follow-up commits." \
           -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 2: Capture slow-native-call start ticks in the streaming transcriber

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/NativeCallStats.cs` (record at ~:13)
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` (Session fields ~:76–86, snapshot getter ~:208–217, `TimedNativeCall` ~:235–255)
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` (fakes ~:686–700)

**Interfaces:**
- Consumes: nothing from Task 1 (independent seam).
- Produces (Task 4 relies on these exact members):
  - `public sealed record NativeCallStats(int Count, int TotalMs, int MaxMs, int CountOver250Ms, IReadOnlyList<long>? Over250StartTicks = null, int Over250Overflow = 0);`
    — `Over250StartTicks` are absolute `Environment.TickCount64` values at the
    START of each native call that took ≥ 250 ms, first 16 only; `Over250Overflow`
    counts the rest. Defaults keep every existing `new NativeCallStats(a,b,c,d)`
    call site compiling and record-equal.
  - No `StreamingFinishStats` change: it already embeds `NativeCallStats?` whole,
    so the new components ride along automatically.

- [ ] **Step 1: Write the failing tests**

(a) In `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`,
add a new fact right after the existing `Session_CountsCallsAtOrOver250msAsSlow`
(~:287–300; it arranges `FakeTranscribeCppEngine { FeedDelay = TimeSpan.FromMilliseconds(300) }`
and pushes one 2560-sample chunk = exactly one slow feed). This file uses xUnit
`Assert` style and the private helper `Samples(int)`:

```csharp
    [Fact]
    public async Task Session_RecordsOver250StartTicks_Absolute()
    {
        var engine = new FakeTranscribeCppEngine { FeedDelay = TimeSpan.FromMilliseconds(300) };
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en");
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        var before = Environment.TickCount64;
        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken); // one ~300 ms feed
        var after = Environment.TickCount64;

        var stats = ((INativeCallStatsSource)s).NativeCallStats;
        Assert.True(stats.CountOver250Ms >= 1);
        Assert.NotNull(stats.Over250StartTicks);
        Assert.Equal(stats.CountOver250Ms, stats.Over250StartTicks!.Count);
        Assert.All(stats.Over250StartTicks, tick => Assert.InRange(tick, before, after));
        Assert.Equal(0, stats.Over250Overflow);
    }
```

(b) In the same file, add a cap test — 17 pushes of one slow feed each
(~5 s wall clock at 300 ms per feed; acceptable):

```csharp
    [Fact]
    public async Task Session_CapsOver250StartTicksAt16_AndCountsOverflow()
    {
        var engine = new FakeTranscribeCppEngine { FeedDelay = TimeSpan.FromMilliseconds(300) };
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en");
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < NativeCallStats.Over250ListCap + 1; i++)
            await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken);

        var stats = ((INativeCallStatsSource)s).NativeCallStats;
        Assert.Equal(17, stats.CountOver250Ms);   // 17 slow feeds; begin is fast
        Assert.NotNull(stats.Over250StartTicks);
        Assert.Equal(NativeCallStats.Over250ListCap, stats.Over250StartTicks!.Count);
        Assert.Equal(1, stats.Over250Overflow);
    }
```

(c) In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`: the
`StatsExposingTranscriber.StatsSession` fake (~:685–700) currently hardcodes
`public NativeCallStats NativeCallStats { get; } = new(7, 900, 400, 2);` —
make it settable so the default stays identical:

```csharp
        public NativeCallStats NativeCallStats { get; set; } = new(7, 900, 400, 2);
```

then add the propagation test next to
`FinishAsync_SurfacesNativeCallStats_WhenSessionExposesThem` (~:656–668; this
file uses Shouldly):

```csharp
    [Fact]
    public async Task FinishAsync_PropagatesOver250Ticks_ThroughFinishStats()
    {
        var transcriber = new StatsExposingTranscriber();
        transcriber.Session.NativeCallStats = new NativeCallStats(7, 900, 400, 2,
            Over250StartTicks: new long[] { 100_000, 100_400 }, Over250Overflow: 3);
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken);
        session.OnFrame(new float[800]);

        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).ShouldNotBeNull();

        var ns = session.FinishStats.ShouldNotBeNull().NativeCallStats.ShouldNotBeNull();
        ns.Over250StartTicks.ShouldBe(new long[] { 100_000, 100_400 });
        ns.Over250Overflow.ShouldBe(3);
    }
```

CAUTION: `NativeCallStats` record equality compares the new list component BY
REFERENCE (default record semantics for a list-typed positional parameter). The
existing test `FinishAsync_SurfacesNativeCallStats_WhenSessionExposesThem`
compares whole records with `ShouldBe(new NativeCallStats(7, 900, 400, 2))` —
that still passes because both sides default the list to `null`. Do not change
the fake's DEFAULT stats value; assert list contents property-by-property (as
above), never via whole-record equality with a non-null list.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS — `'NativeCallStats' does not contain a definition for
'Over250StartTicks'` / no `Over250ListCap`.

- [ ] **Step 3: Implement the capture**

(a) `src/Winpepper.Asr/Transcription/NativeCallStats.cs` — replace the record
declaration (keep the file's existing doc comments and the
`INativeCallStatsSource` interface untouched):

```csharp
/// <summary>Aggregates over one streaming session's native calls.
/// <paramref name="Over250StartTicks"/>: absolute Environment.TickCount64 at the
/// START of each native call that took >= 250 ms, first
/// <see cref="Over250ListCap"/> only (bounded memory); <paramref name="Over250Overflow"/>
/// counts the rest. Consumers convert to recording-start offsets
/// (DictationTimingSummary.StampOver250).</summary>
public sealed record NativeCallStats(
    int Count,
    int TotalMs,
    int MaxMs,
    int CountOver250Ms,
    IReadOnlyList<long>? Over250StartTicks = null,
    int Over250Overflow = 0)
{
    public const int Over250ListCap = 16;
}
```

(b) `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`, inside
the nested `Session` class — add two fields next to the existing aggregates
(`_nativeOver250`, ~:76–86):

```csharp
        private readonly List<long> _over250StartTicks = new();
        private int _over250Overflow;
```

(c) In `TimedNativeCall<T>(string op, Func<T> call)` (~:235–255): capture the
start tick as the first statement, next to the stopwatch:

```csharp
            var startTick = Environment.TickCount64;
            var nativeSw = Stopwatch.StartNew();
```

and in the `finally` block replace
`if (elapsedMs >= SlowNativeCallMs) _nativeOver250++;` with:

```csharp
                if (elapsedMs >= SlowNativeCallMs)
                {
                    _nativeOver250++;
                    if (_over250StartTicks.Count < NativeCallStats.Over250ListCap)
                        _over250StartTicks.Add(startTick);
                    else
                        _over250Overflow++;
                }
```

(These fields are mutated only in `TimedNativeCall`'s `finally`, which always
runs under `_nativeGate` — same invariant as the existing aggregates; no extra
locking needed.)

(d) Update the snapshot getter (~:208–217) — it already takes `_nativeGate`;
copy the list out so the snapshot stays immutable:

```csharp
        public NativeCallStats NativeCallStats
        {
            get
            {
                lock (_nativeGate)
                {
                    return new NativeCallStats(
                        _nativeCalls, (int)_nativeTotalMs, (int)_nativeMaxMs, _nativeOver250,
                        _over250StartTicks.Count > 0 ? _over250StartTicks.ToArray() : null,
                        _over250Overflow);
                }
            }
        }
```

No change to `StreamingDictationSession.cs` — `FinishAsync` already embeds the
whole `NativeCallStats` record into `StreamingFinishStats`, including the
drain-timeout path where stats are deliberately never probed (over250 ticks are
absent there, same as `native_over250` today).

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true \
  && dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait Platform=Windows
```

Expected: `Errors: 0`, `Failed: 0` (including all pre-existing
NemotronStreamingTranscriber and StreamingDictationSession tests).

- [ ] **Step 5: Append the capture notes to the evidence file**

Append under `## 0b — instrumentation added`:

```markdown
- Capture (`src/Winpepper.Asr/Transcription/`): `NativeCallStats` gained
  `Over250StartTicks` (absolute TickCount64 at call START, cap 16 via
  `NativeCallStats.Over250ListCap`) + `Over250Overflow`;
  `NemotronStreamingTranscriber.Session.TimedNativeCall` records them under
  `_nativeGate`. Rides the existing `StreamingFinishStats` chain unchanged;
  absent on the drain-timeout/abandon path by design (stats never probed there).
```

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/NativeCallStats.cs \
        src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs \
        tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs \
        docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "feat(asr): record start ticks of native calls >= 250 ms (cap 16 + overflow)" \
           -m "Run 1 / step 0b: raw data for over250_at. Absolute ticks; offset conversion happens at stamp time." \
           -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 3: Consume-time window-context indicator on CleanupRunner's result

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupResult.cs` (record at ~:4–9)
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` (`RunAsync` ~:47–211; window-context wait ~:75–98; 11 return statements)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`

**Interfaces:**
- Consumes: nothing from Tasks 1–2 (independent seam). `RunAsync`'s INPUT
  signature stays exactly as-is (binding: the approved plan requires the
  indicator on the RESULT, keeping the input narrow).
- Produces (Task 4 relies on this exact member):
  - `CleanupResult.ConsumedWindowContext` — `bool?`, `init`-only:
    - `null` — no window-context task was supplied, or the feature was disabled
      (`options.WindowContextEnabled == false` or `windowContextTask == null`).
    - `false` — a task was supplied but was NOT complete at the moment the
      runner stopped waiting (regardless of what it later produced).
    - `true` — the task was complete within the bounded wait and its value
      (possibly null text) fed the prompt build. NOTE: a completed-but-faulted
      task still counts as `true` here — PipelineHost resolves faulted prefetches
      to `ctx_src=none` via `IsCompletedSuccessfully` (Task 4).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (same class; reuse
its `NewRunner` / `DefaultOptions` helpers and the existing
`FakeLlamaCleanupBackend`):

```csharp
    [Fact]
    public async Task Run_ConsumedWindowContext_IsNull_WhenNoContextTaskSupplied()
    {
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);

        var result = await runner.RunAsync("cleaned up this sentence",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.ConsumedWindowContext.ShouldBeNull();
    }

    [Fact]
    public async Task Run_ConsumedWindowContext_IsNull_WhenFeatureDisabled_EvenWithTask()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        // DefaultOptions() has WindowContextEnabled = false.
        var result = await runner.RunAsync("cleaned up this sentence",
            CorrectionsData.Empty, ready, DefaultOptions(), CancellationToken.None);

        result.ConsumedWindowContext.ShouldBeNull();
    }

    [Fact]
    public async Task Run_ConsumedWindowContext_IsTrue_WhenContextReadyInTime()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };

        var result = await runner.RunAsync("cleaned up this sentence",
            CorrectionsData.Empty, ready, opts, CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(true);
    }

    [Fact]
    public async Task Run_ConsumedWindowContext_IsFalse_WhenContextArrivesTooLate()
    {
        var tcs = new TaskCompletionSource<string?>();
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(50),
        };

        var result = await runner.RunAsync("cleaned up this sentence",
            CorrectionsData.Empty, tcs.Task, opts, CancellationToken.None);

        result.ConsumedWindowContext.ShouldBe(false);
        // Completing afterwards must not retroactively change the verdict —
        // consume-time semantics, not produce-time.
        tcs.SetResult("too late");
        result.ConsumedWindowContext.ShouldBe(false);
    }

    [Fact]
    public async Task Run_ConsumedWindowContext_SurvivesFallbackPaths()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Throw = new InvalidOperationException("boom") };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };

        var result = await runner.RunAsync("cleaned up this sentence",
            CorrectionsData.Empty, ready, opts, CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.ConsumedWindowContext.ShouldBe(true);
    }

    [Fact]
    public async Task Run_ConsumedWindowContext_IsNull_OnShortTranscriptBypass()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };

        // 3 words -> BypassShort fires BEFORE the window-context wait.
        var result = await runner.RunAsync("only three words",
            CorrectionsData.Empty, ready, opts, CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassShort);
        result.ConsumedWindowContext.ShouldBeNull();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS — `'CleanupResult' does not contain a definition for
'ConsumedWindowContext'`.

- [ ] **Step 3: Implement the indicator**

(a) `src/Winpepper.Cleanup/CleanupResult.cs` — extend the record with a body
(positional components unchanged, so all existing construction sites compile):

```csharp
/// <summary>Outcome of one <c>CleanupRunner</c> invocation.</summary>
public sealed record CleanupResult(
    string CleanedText,
    CleanupPath Path,
    string RawModelOutput,
    string AssembledPrompt,
    TimeSpan Elapsed)
{
    /// <summary>0b consume-time indicator for the timing line's <c>ctx_src</c>.
    /// null = no window-context task supplied / feature disabled (field omitted);
    /// false = a task was supplied but was NOT complete when the runner stopped
    /// waiting (regardless of what it later produced);
    /// true = the task was complete within the bounded wait and its value fed
    /// the prompt build (a faulted-but-complete task still counts — the caller
    /// resolves faults to "none" via IsCompletedSuccessfully).</summary>
    public bool? ConsumedWindowContext { get; init; }
}
```

(b) `src/Winpepper.Cleanup/CleanupRunner.cs`, in `RunAsync`:

Declare the local immediately after `var sw = Stopwatch.StartNew();` (~:53):

```csharp
        bool? consumedWindowContext = null;
```

In the bounded window-context wait (~:75–98), set it (the block's shape today —
apply the two marked insertions):

```csharp
        string? windowContext = null;
        if (options.WindowContextEnabled && windowContextTask is not null)
        {
            consumedWindowContext = false;                       // <- INSERT
            try
            {
                var completed = await Task.WhenAny(windowContextTask,
                                                   Task.Delay(options.WindowContextWait, ct))
                                          .ConfigureAwait(false);
                if (completed == windowContextTask)
                {
                    consumedWindowContext = true;                // <- INSERT
                    windowContext = await windowContextTask.ConfigureAwait(false);
                }
                else
                {
                    _log.LogDebug("Window-context prefetch exceeded {Budget}ms; proceeding without it",
                        options.WindowContextWait.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Window-context prefetch failed; proceeding without it");
            }
        }
```

Thread the local onto EVERY return of `RunAsync`. There are 11 returns: 10 of
the form `return Finalize(...);` (bypass ~:72, timeout ~:134, backend-threw
~:139, empty ~:147, ellipsis ~:150, implausible ×4 ~:164/:170/:186/:192,
known-example ~:197) and 1 direct construction (success, ~:204–211). Mechanical
transformation, identical for all 10 Finalize sites:

```csharp
            return Finalize(rawTranscript, "", corrections, assembledPrompt: "", path, sw)
                with { ConsumedWindowContext = consumedWindowContext };
```

(same `with { ConsumedWindowContext = consumedWindowContext }` suffix on each of
the 10, keeping each site's own arguments untouched), and on the success return
add the property to the initializer:

```csharp
        return new CleanupResult(
            CleanedText: withCorrections,
            Path: chosenPath,
            RawModelOutput: raw,
            AssembledPrompt: assembled,
            Elapsed: sw.Elapsed)
        {
            ConsumedWindowContext = consumedWindowContext,
        };
```

(`Finalize` itself stays untouched. Note `sw.Stop()` runs inside `Finalize`
before it returns, so the `with` copy afterwards changes no timing semantics —
`Elapsed` is already fixed in the copied record.)

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true \
  && dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait Platform=Windows
```

Expected: `Errors: 0`, `Failed: 0` (all 34 pre-existing `RunAsync` call sites in
`CleanupRunnerTests` still green, plus the 6 new facts).

- [ ] **Step 5: Append the indicator notes to the evidence file**

Append under `## 0b — instrumentation added`:

```markdown
- Consume-time indicator (`src/Winpepper.Cleanup/`): `CleanupResult` gained
  init-only `ConsumedWindowContext` (null = no task/disabled, false = task not
  complete when the runner stopped waiting, true = complete within the bounded
  wait). Set in `CleanupRunner.RunAsync`'s window-context wait; threaded onto
  all 11 returns via `with`. Input signature of RunAsync unchanged.
```

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Cleanup/CleanupResult.cs src/Winpepper.Cleanup/CleanupRunner.cs \
        tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs \
        docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "feat(cleanup): consume-time window-context indicator on CleanupResult" \
           -m "Run 1 / step 0b: surfaces whether the prefetch was complete when the runner stopped waiting, for ctx_src stamping. Input signature unchanged." \
           -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 4: PipelineHost stamping — proc_cpu_ms, ctx_src, over250_at (both arms)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — recording-start blocks
  (hold ~:513–520 within :484–535; toggle ~:1014–1021 within :985–1036), stop
  blocks (hold ~:536–546; toggle ~:1037–1047), cleanup result handling (hold
  ~:713–799; toggle mirror ~:1213–1296), `StampStreamingFinishStats` ~:1471–1491
  and its 4 call sites, backing fields ~:61–79.

**Interfaces:**
- Consumes:
  - `DictationTimingSummary.ProcCpuMs`, `.CtxSrc`, `.StampOver250(...)` (Task 1)
  - `NativeCallStats.Over250StartTicks` / `.Over250Overflow` (Task 2)
  - `CleanupResult.ConsumedWindowContext` (Task 3)
  - existing `_dictStartTicks` (`Environment.TickCount64` stamped at recording
    start) and `_ctxPrefetchTask` (`Task<WindowContextResult>?`)
- Produces: log-line data only. NOTE: this file is entirely `#if WINDOWS` — it
  does not compile on the Linux gate and has no unit tests. Keep every addition
  to trivial reads/assignments; the Windows gate in Task 6 is the compile check.
- **Both arms rule:** the hold arm and toggle arm are deliberate byte-parallel
  duplicates (toggle uses `2`-suffixed locals: `timing2`, `result2`,
  `ctxTextTask2`). EVERY edit below lands twice.

- [ ] **Step 1: proc_cpu_ms — start sample (both arms)**

Add a backing field next to `_gcPauseAtStart` (~:69):

```csharp
    private System.TimeSpan _procCpuAtStart;
```

In BOTH recording-start blocks, immediately after
`_gcPauseAtStart = GC.GetTotalPauseDuration();` (hold ~:519, toggle ~:1020), add:

```csharp
                _procCpuAtStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
```

- [ ] **Step 2: proc_cpu_ms — stop sample at StopRequested (both arms; NOT at emit)**

The approved plan is explicit: sample at the StopRequested handling (hold :538,
toggle :1039), NEVER at the summary-emit point (`EmitTimingSummary`, ~:1537) —
that runs after cleanup inference and would make the field a useless constant.

Hold arm — immediately after `_recordStopwatch?.Stop();` (~:539) add:

```csharp
                var procCpuAtStop = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
```

then, right after `timing.RecordMs = (int?)_recordStopwatch?.ElapsedMilliseconds;`
(~:547) add:

```csharp
                timing.ProcCpuMs = (int)(procCpuAtStop - _procCpuAtStart).TotalMilliseconds;
```

Toggle arm — mirror after `_recordStopwatch?.Stop();` (~:1040) with
`procCpuAtStop2` and `timing2.ProcCpuMs = (int)(procCpuAtStop2 - _procCpuAtStart).TotalMilliseconds;`
after `timing2.RecordMs = ...` (~:1048).

- [ ] **Step 3: over250_at — thread `_dictStartTicks` through the stamp helper**

Change `StampStreamingFinishStats` (~:1471–1491) to accept the recording-start
tick and stamp the offsets:

```csharp
    private static void StampStreamingFinishStats(
        Winpepper.Core.Diagnostics.DictationTimingSummary timing,
        Winpepper.Asr.Transcription.StreamingDictationSession? streaming,
        long dictStartTicks)
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
            if (ns.Over250StartTicks is { Count: > 0 })
                timing.StampOver250(ns.Over250StartTicks, ns.Over250Overflow, dictStartTicks);
        }
    }
```

Update ALL FOUR call sites (two per arm: the ASR-unavailable early-return path
and the normal path — search for `StampStreamingFinishStats(`) to pass
`_dictStartTicks` as the third argument, e.g.
`StampStreamingFinishStats(timing, streaming, _dictStartTicks);` (hold) and the
`timing2` mirror (toggle).

- [ ] **Step 4: ctx_src — map indicator + Source to the string (both arms)**

Hold arm — inside the cleanup `try` block, immediately after the existing line
`windowContextUsed = ctxTextTask is not null && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");`
(~:784–785; leave that line exactly as-is), add:

```csharp
                        // 0b consume-time ctx_src: "none" whenever the prefetch was
                        // not complete when CleanupRunner stopped waiting, regardless
                        // of what it later produced; otherwise the prefetch's Source.
                        timing.CtxSrc = result.ConsumedWindowContext switch
                        {
                            null => null,   // no context task supplied/enabled -> omit the field
                            false => "none",
                            true => _ctxPrefetchTask is { IsCompletedSuccessfully: true } ctxDone
                                ? ctxDone.Result.Source switch
                                {
                                    Winpepper.Platform.WindowContext.WindowContextSource.Uia => "uia",
                                    Winpepper.Platform.WindowContext.WindowContextSource.Ocr => "ocr",
                                    _ => "none",
                                }
                                : "none",
                        };
```

Toggle arm — identical block after the `windowContextUsed2 = ...` line
(~:1289–1290), using `timing2` and `result2` (`_ctxPrefetchTask` is the shared
field in both arms).

Correctness note for the reviewer: when `ConsumedWindowContext == true`, the
runner's `ctxTextTask` (a `ContinueWith` projection of `_ctxPrefetchTask`) had
completed, so `_ctxPrefetchTask` is complete too — reading `.Result` here cannot
block. A faulted prefetch fails `IsCompletedSuccessfully` and maps to `"none"`;
a successful-but-empty prefetch has `Source == Empty` and also maps to `"none"`.
The cleanup `catch` branch leaves `CtxSrc` null (no result to consult) — the
field is simply omitted on cleanup-exception lines.

- [ ] **Step 5: Prove no shared code broke, then commit**

PipelineHost cannot be compiled or tested on Linux (`#if WINDOWS`); the Windows
gate in Task 6 is its compile verification. Re-read your two arms' diffs
side-by-side and confirm they are symmetric (`timing`↔`timing2`,
`result`↔`result2`, `procCpuAtStop`↔`procCpuAtStop2`), then:

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (proves the shared projects the App references
still build and pass).

Append under `## 0b — instrumentation added` in the evidence file:

```markdown
- Stamping (`src/Winpepper.App/Hosting/PipelineHost.cs`, both arms):
  `proc_cpu_ms` = Process.TotalProcessorTime delta sampled at recording start
  and at StopRequested (hold :538-area, toggle :1039-area; never at emit);
  `over250_at` stamped via StampStreamingFinishStats(+ _dictStartTicks) ->
  DictationTimingSummary.StampOver250; `ctx_src` mapped from
  CleanupResult.ConsumedWindowContext + _ctxPrefetchTask.Result.Source.
  The legacy windowContextUsed prompt sniff is intentionally untouched.
```

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs \
        docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "feat(app): stamp ctx_src, over250_at, proc_cpu_ms onto the dictation timing line" \
           -m "Run 1 / step 0b complete: consume-time ctx_src semantics, unclamped over250 offsets from recording start, CPU delta sampled start->StopRequested (both hotkey arms)." \
           -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 5: Step 0c — settle the StatelessExecutor lifetime question with quoted evidence

**Files:**
- Modify (append): `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`
- Read-only references: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` (:88–91
  fresh `StatelessExecutor` per generation; class doc :10–15 claims the
  `LLamaContext` is per-process — the contradiction 0c settles)

**Interfaces:**
- Consumes: nothing from other tasks. NO production code changes in this task.
- Produces: the evidence-file verdict that gates Run 2's step 1d:
  - "Planner C right (native context lingers until finalization)" → 1d IS
    triggered (for Run 2 — do NOT implement it now), or
  - "Planner B right (nothing native lingers between generations)" → 1d NOT
    triggered, or
  - "could not verify" → 1d NOT triggered.

**The question (from the approved plan, verbatim):** Planner B says
StatelessExecutor's constructor creates and immediately disposes its native
context, and each generation's working context is disposed via `using` — so
nothing native lingers between generations. Planner C says a native context DOES
outlive each generation until the garbage collector finalizes it. Both, either,
or neither may be wrong. Never settle this from memory — evidence only.

- [ ] **Step 1: Try the official LLamaSharp v0.27.0 source first**

```bash
mkdir -p /tmp/llamasharp-0c
curl -fsSL -o /tmp/llamasharp-0c/LLamaStatelessExecutor.cs \
  https://raw.githubusercontent.com/SciSharp/LLamaSharp/v0.27.0/LLama/LLamaStatelessExecutor.cs \
  && echo FETCHED || echo FETCH_FAILED
```

(NOTE — verified 2026-07-29: the file is `LLama/LLamaStatelessExecutor.cs`; the
path `LLama/StatelessExecutor.cs` 404s. The class inside is `StatelessExecutor`.
Tag `v0.27.0` resolves to commit `7cbbc45e421d55794d5050d126e0b96511007007`,
which exactly matches the `<repository commit="...">` stamp in the cached
`~/.nuget/packages/llamasharp/0.27.0/llamasharp.nuspec` — so the tag source is
faithful to the NuGet binary; cite this sha match in the evidence file as the
source-fidelity note.)

Expected: `FETCHED` and a non-empty C# file. If `FETCH_FAILED` (no network, tag
missing), go to Step 2. If FETCHED, also pull the files StatelessExecutor's
context lifetime depends on, as needed to follow the code (same raw-URL pattern,
path `LLama/<File>.cs` or `LLama/Native/<File>.cs`) — typically `LLamaContext.cs`
and, if referenced, `LLamaWeights.cs` / `Native/SafeLLamaContextHandle.cs` /
`Native/SafeLLamaHandleBase.cs` (the SafeHandle base — needed to trace whether
`LLamaContext.Dispose()` releases the NATIVE handle deterministically).

- [ ] **Step 2 (fallback): decompile the NuGet-resolved assembly**

Only if Step 1 failed:

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"
dotnet tool install --global ilspycmd 2>/dev/null || true
export PATH="$PATH:$HOME/.dotnet/tools"
ilspycmd ~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.dll \
  -t LLama.StatelessExecutor > /tmp/llamasharp-0c/StatelessExecutor.decompiled.cs \
  && echo DECOMPILED || echo DECOMPILE_FAILED
ilspycmd ~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.dll \
  -t LLama.LLamaContext > /tmp/llamasharp-0c/LLamaContext.decompiled.cs || true
```

Expected: `DECOMPILED` and readable C#. If BOTH Step 1 and Step 2 fail, write
the honest outcome in the evidence file — verbatim verdict: `could not verify`
— note that step 1d is therefore NOT triggered, and skip to Step 4.

- [ ] **Step 3: Read the code and answer the two specific questions**

Read the obtained `StatelessExecutor` source and answer, with quotes:

1. Does the CONSTRUCTOR create a native context, and is it disposed before the
   constructor returns (e.g. `context.Dispose()` / `using` inside the ctor)?
2. Inside the inference method (`InferAsync` or its core), is the per-generation
   `LLamaContext` created and disposed deterministically (`using` /
   `try/finally` → Planner B), or does it escape to a field/finalizer so native
   memory lingers until GC finalization after the generation completes
   (→ Planner C)?

Decide the verdict: **Planner B right**, **Planner C right**, **both partially
right** (state exactly which halves), or **could not verify**. The verdict must
follow ONLY from the quoted code, not from memory or documentation.

- [ ] **Step 4: Record the finding in the evidence file**

Replace the `(pending ...)` placeholder under
`## 0c — StatelessExecutor native-context lifetime (Planner B vs Planner C)` with:

```markdown
- Source of evidence: <official GitHub tag v0.27.0 raw file | ilspycmd decompile
  of ~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.dll | could not verify>
- Quoted evidence (file + line numbers REQUIRED; for a decompile, line numbers
  are of the decompiled output, stated as such):

  ```csharp
  // LLama/LLamaStatelessExecutor.cs:<n>-<m> (v0.27.0)
  <the constructor's context handling, verbatim>
  ```

  ```csharp
  // LLama/LLamaStatelessExecutor.cs:<n>-<m> (v0.27.0)
  <the per-generation context creation/disposal inside InferAsync, verbatim>
  ```

- Verdict: <Planner B right | Planner C right | both partially right: ... | could not verify>
- Consequence for Run 2's step 1d: <TRIGGERED — a native context really does
  linger past each generation | NOT triggered>
- Cross-check against this repo: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs:88-91`
  constructs a fresh StatelessExecutor per generation inside the `_gate` critical
  section; the class doc at :10-15 claims a per-process LLamaContext —
  <which claim the evidence supports, one sentence>.
```

(Angle-bracket slots are for the ACTUAL findings — fill every one; quote real
code, never paraphrase.)

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (no code changed; this run satisfies the
before-every-commit gate).

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(evidence): 0c verdict on StatelessExecutor native-context lifetime" \
           -m "Run 1 / step 0c: quoted v0.27.0 evidence settling Planner B vs C; gates Run 2's 1d." \
           -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 6: Windows gate, evidence wrap-up, branch done (no push)

**Files:**
- Modify (append, only if corrections exist): `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`

**Interfaces:**
- Consumes: all previous tasks' commits on `feat/cleanup-asr-contention-run1`.
- Produces: `GATE: GREEN` proof; a local branch ready for the root session to
  merge and install. NOTHING is pushed.

- [ ] **Step 1: Reconcile the mismatch-rule log**

Review the evidence file's `## Mismatch-rule log`: if any task found code at a
different location than this plan stated, ensure the corrected location was
recorded there (append any that were missed). If none, append:

```markdown
- 2026-07-29 (post-implementation): no further corrections — all pointers in the
  run-1 implementation plan matched within tolerance.
```

- [ ] **Step 2: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 3: Full Windows gate (retry known flakes)**

```bash
./scripts/windows-gate.sh
```

Expected: exit 0 with `GATE: GREEN` (~12 min; use a 20–30 min timeout). This is
also the ONLY compile verification for Task 4's `#if WINDOWS` PipelineHost
edits. Prerequisites (verified 2026-07-29, when the gate ran GREEN at base):
the Windows host desktop must be unlocked/interactive (hook tests TIME OUT on a
locked/headless desktop — the gate script itself documents this), and NEVER run
the gate concurrently with `./scripts/linux-tests.sh` (the gate pre-cleans all
`bin/`/`obj/`). Known transient flakes — retry the whole gate (up to 3 attempts)
if the failure is one of: UNC path `MSB4025` build errors, vsock/`powershell.exe`
interop connection failures, or hook-test TIMEOUTs (re-check the desktop is
unlocked before retrying that class). A REAL test failure or a compile error in
`PipelineHost.cs` is NOT a flake: fix it, re-run the Linux suite, commit the fix
(same message discipline as Task 4), and re-run the gate.

- [ ] **Step 4: Commit the wrap-up (only if the evidence file changed)**

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git diff --cached --quiet || git commit \
  -m "docs(evidence): run-1 wrap-up — mismatch log reconciled, gates green" \
  -m "Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

- [ ] **Step 5: Final state check — and STOP**

```bash
git status --short   # expected: clean
git log --oneline 8f5db7d..HEAD   # expected: the 4-6 commits from Tasks 1-6
```

Do NOT push. Do NOT start 0a, 1a, 1b, 1d, or Phase 2 — the next step belongs to
the owner (baseline collection + 0a + the explicit gate). The root session
merges and installs.
