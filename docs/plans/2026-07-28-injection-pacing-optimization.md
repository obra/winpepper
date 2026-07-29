# Injection Pacing Optimization Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make the paced text injection sleep only the *remainder* of each
inter-chunk period (deadline-based pacing) so real pastes run ~2x faster
while provably never exceeding the bleed-safety feed ceiling, and add a
standard-practice injected-event fast-path to the low-level keyboard hook.

**Architecture:** A new tiny `DeadlinePacer` class owns the
`max(0, period - measured elapsed)` remainder math — with the period scaled
per chunk (`TextInjector.PeriodMsForChunk`: `ceil(units * 14 / 8)` ms, so a
9-unit surrogate-straddle chunk gets 16 ms; stage-2 ledger A7) — behind the
existing `Action<int>` sleep seam plus a new injectable monotonic-clock seam
on `TextInjector`. `GuardedInjectionRun`'s contract is untouched (the pause
stays a parameterless `Action`). Separately, `HotkeyHook.TryProcessKey`
gains an early pass-through for `LLKHF_INJECTED` events that preserves the
chord-recorder raw-capture contract.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly, Win32 `SendInput` /
`WH_KEYBOARD_LL`, high-resolution waitable timer (`PacingWaiter`).

## Global Constraints

- Base: current `main` (`9671a84`), worktree branch
  `perf/injection-pacing-optimization` at
  `/home/dan/code/winpepper/.worktrees/injection-pacing-optimization`.
- **Linux suite green before EVERY commit:** `./scripts/linux-tests.sh`
  must end `LINUX SUITE: GREEN` (AGENTS.md). Never `dotnet test`.
- **Full Windows gate before ANY push:** `./scripts/windows-gate.sh` must
  end `GATE: GREEN` (AGENTS.md). Known transient UNC
  `retry should be performed` failures mean re-run quietly, not a code
  failure. Any `Winpepper.Cleanup.Tests` model-eval failures are owned by
  the parallel cleanup stream — verify they match the known set; everything
  else must be green.
- **Bleed-safety invariant (the property CHANGE 1 must provably keep):**
  actual feed <= `TargetFeedUnitsPerSecond` (= 600 UTF-16 units/s) for EVERY
  chunk size `InjectionChunker.Split` can emit. The chunker extends a chunk
  to 9 units rather than split a surrogate pair (pinned by
  `InjectionChunkerTests`), and a fixed 14 ms period would let sustained
  9-unit chunks feed ~643 units/s (stage-2 ledger A7) — so the per-chunk
  period is `ceil(unitsSent * InterChunkPauseMs / ChunkCodeUnits)` ms:
  8-unit chunks 14 ms (unchanged), 9-unit chunks 16 ms (~562 units/s, 1 ms
  margin over the 15 ms the ceiling strictly needs). The constant's CEILING
  semantics are unchanged; only dead time is removed.
- **Guard cadence:** the per-chunk halt predicate (foreground / modifier /
  mouse / hwnd-0 checks) runs exactly as today — once per chunk period; the
  guard-check frequency must not drop below one check per
  `InterChunkPauseMs`-worth of feed.
- **Preserve exactly as shipped:** full-text pending-paste semantics;
  fail-open/park polarity (hwnd-0 parks, elevation parks,
  unobservable-probe injects); no clipboard use.
- `TargetFeedUnitsPerSecond` stays `600`; `ChunkCodeUnits` stays `8`;
  `InterChunkPauseMs` stays ceiling-derived `14` (it is the 8-unit chunk's
  period; larger straddle chunks get the scaled period via
  `PeriodMsForChunk`).
- `ModifierGuard.WaitForRelease` and both release-wait preludes are NOT
  compensated — deadline pacing applies ONLY to the inter-chunk pause call
  site.
- Commits: Conventional Commits with scope (`feat(injection):`,
  `perf(hotkeys):`, `test(injection):`), no AI attribution trailers,
  explicit `git add <paths>`.
- README.md stays the only end-user markdown doc; this plan is a working
  doc under `docs/plans/`.

## HALT CONDITIONS (explicit stop conditions from the user)

HALT the run and report (do not work around) if:

1. Deadline-based pacing cannot preserve the bleed ceiling
   (feed <= 600 units/s) together with the guard-check cadence — e.g. the
   ceiling-remainder proof in Task 1 turns out not to hold.
2. The send-cost measurement is contradicted in a way that invalidates the
   design — e.g. the Task 3/Task 5 gate-host sentinel measures per-chunk
   periods wildly inconsistent with the recorded facts below, or the
   sentinel cannot be made deterministic without widening thresholds.
3. A Windows pacing sentinel fails at the gate: **STOP and report** — never
   widen a threshold or swap in a spin-wait without explicit approval (this
   instruction is already embedded in the sentinel's XML doc; keep it).

NOT a halt: if Task 4's in-code validation shows the hook design depends on
injected events beyond what Task 4 handles, **DROP CHANGE 2** with the
reason recorded in the task's commit (an acceptable outcome per the spec)
and continue with Task 5.

## Measured Facts (live probes on the user's real Windows host, 2026-07-28 — record-of-truth for this plan)

- A `SendInput` batch of `KEYEVENTF_UNICODE` down/up events costs
  **~0.85–0.92 ms PER EVENT** on this machine with Winpepper stopped
  (100 events → 88 ms, 400 → 338 ms, 800 → 739 ms; linear, i.e. per-event
  not per-call). With Winpepper running it rises to **~1.08–1.13 ms/event**
  (Winpepper's hook adds only ~0.2 ms/event — NOT the dominant cost; other
  low-level hooks / the environment dominate).
- Therefore each 8-char injection chunk (16 events) costs **~14–18 ms in
  the SendInput call ITSELF**.
- The current pacing design (`TargetFeedUnitsPerSecond=600` →
  `InterChunkPauseMs=14`, `TextInjector.cs`) assumes the send is ~free and
  sleeps the FULL 14 ms after each chunk. Actual per-chunk period is
  therefore ~28–32 ms → actual feed **~250–285 chars/s, roughly HALF the
  571/s design point**. Independent evidence: a 458-char merged paste
  measured ~1.6 s in a previous run (vs ~0.8 s design).
- Historical note: an old production log recorded 3 ms for 112 chars
  (~13 µs/event), and the original validation ledger assumed
  queue-insertion ~µs/call. The machine's current input path is ~70x slower
  than that assumption. **The design must pace by MEASURED elapsed time,
  not assumptions.**
- Render-rate record (commit 9671a84, measured on the real host with
  Winpepper's hook active): WinForms TextBox ~633 chars/s, RichTextBox
  ~1,163 chars/s; no measurable REAL target consumed slower than the 571
  units/s nominal feed (stage-2 ledger A3). Sub-571 pathological targets are
  explicitly owned by the mid-paste guards, not by pacing.

Expected effect of CHANGE 1: actual feed rises from ~285/s to ~571/s
nominal (~2x faster pastes) while never exceeding the 600 units/s ceiling.
Expected effect of CHANGE 2 (honest): up to ~0.2 ms/event saved on this
machine (~3 ms per 16-event chunk) and every OTHER app's synthetic input
un-taxed; under deadline pacing the saving is absorbed into the remainder
sleep whenever send+hook < 14 ms — it is user-visible only when the chunk
cost meets/exceeds the period, which the measurements say IS the actual
regime here (14–18 ms/chunk).

## Scope Check

Two changes, one subsystem story: both attack the same measured per-chunk
cost on the same injection path, share the same measurement basis, and are
verified by the same Windows gate. One plan.

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Platform/Injection/DeadlinePacer.cs` | Create | Ceiling-remainder period math (the ONLY new time logic) |
| `tests/Winpepper.Platform.Tests/Injection/DeadlinePacerTests.cs` | Create | Remainder math + bleed-ceiling invariant pins (Linux) |
| `src/Winpepper.Platform/Injection/TextInjector.cs` | Modify | `monotonicMs` seam, pause call-site swap, XML-doc ledger update |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` | Modify | Retune 2 sleeps-list pins (semantic change: requested pauses → requested remainders), add 2 deadline tests |
| `src/Winpepper.Platform/Injection/PacingWaiter.cs` | Modify (doc only) | One stale doc sentence about the 14 ms production pause |
| `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs` | Modify | Replace raw-sleep-floor sentinel with a per-chunk PERIOD-floor sentinel through the injector |
| `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` | Modify | Injected fast-path in `TryProcessKey` + removal of now-dead `isInjected` guards |
| `tests/Winpepper.Platform.Tests/Hotkeys/InjectedEventFastPathTests.cs` | Create | RED tests for new fast-path behavior + characterization pins |

**Not touched:** `GuardedInjectionRun.cs` (pause stays a parameterless
`Action`; `GuardedInjectionRunTests` must keep passing unmodified),
`InjectionChunker.cs`, `ModifierGuard.cs`, `MouseButtonGuard.cs`,
`MidPasteDecider.cs`, `ElevatedTargetDecider.cs`/`ElevationProbe.cs`,
`ClipboardFallback.cs`, `ChordRecorder.cs`, `KeyboardHookNative.cs`,
`PipelineHost.cs`, all pending-paste state machines in `Winpepper.Core`.

## Test Commands

- Full Linux suite (before every commit):
  `cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization && ./scripts/linux-tests.sh`
  → must end `LINUX SUITE: GREEN`.
- Single test project quickly during a task (same runner the script uses):
  ```bash
  cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
  dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
  dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
  ```
  (To filter to one class add: `-class "Winpepper.Platform.Tests.Injection.DeadlinePacerTests"`.)
- Windows gate (before push; Task 5): `./scripts/windows-gate.sh` → `GATE: GREEN`.

---

### Task 1: DeadlinePacer — ceiling-remainder period math

**Files:**
- Create: `src/Winpepper.Platform/Injection/DeadlinePacer.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/DeadlinePacerTests.cs`

**Interfaces:**
- Consumes: `TextInjector.InterChunkPauseMs` (`internal const int`, = 14) and
  `TextInjector.TargetFeedUnitsPerSecond` (`internal const int`, = 600) from
  `src/Winpepper.Platform/Injection/TextInjector.cs` (tests only).
- Produces (Task 2 relies on these EXACT signatures):
  `internal sealed class DeadlinePacer` in namespace
  `Winpepper.Platform.Injection` with
  `public DeadlinePacer(int periodMs, Action<int> sleep, Func<double> monotonicMs)`
  (period accounting starts at construction; `periodMs` is the DEFAULT
  period), `public void PauseForNextChunk()` (uses the default period), and
  `public void PauseForNextChunk(int periodMs)` (per-call period override —
  Task 2 passes the scaled per-chunk period for 9-unit straddle chunks;
  stage-2 ledger A7).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/DeadlinePacerTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class DeadlinePacerTests
{
    [Theory]
    [InlineData(0.0, 14)]  // free send => full pause (old behavior is the degenerate case)
    [InlineData(5.0, 9)]
    [InlineData(5.5, 9)]   // ceil(8.5) = 9: round UP, never undershoot the period
    [InlineData(13.2, 1)]
    public void PauseForNextChunk_SleepsTheCeilingRemainder(double elapsedMs, int expectedSleep)
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        sleeps.ShouldBe(new[] { expectedSleep });
    }

    [Theory]
    [InlineData(14.0)]
    [InlineData(20.0)]
    public void PauseForNextChunk_WorkAtOrPastThePeriod_DoesNotSleep(double elapsedMs)
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        // The feed is then throttled by SendInput itself, which is
        // inherently at or below the safe rate.
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void PeriodAccounting_RestartsAtTheEndOfEachPause()
    {
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, ms => { sleeps.Add(ms); now += ms; }, () => now);

        now += 5.0;                // chunk 1 "send" costs 5 ms
        pacer.PauseForNextChunk(); // sleeps 9 -> clock 14, period restarts
        now += 5.0;                // chunk 2 "send" costs 5 ms
        pacer.PauseForNextChunk(); // must again be 9 (not 14 - 19)

        sleeps.ShouldBe(new[] { 9, 9 });
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(5.5)]
    [InlineData(13.9)]
    [InlineData(14.0)]
    [InlineData(50.0)]
    public void BleedCeiling_Invariant_WorkPlusSleepNeverUndershootsThePeriod(double elapsedMs)
    {
        // THE invariant CHANGE 1 must provably keep (standard 8-unit
        // chunks; the 9-unit straddle sibling test below covers the scaled
        // period): per-chunk period
        // (send + sleep) >= InterChunkPauseMs, so nominal feed
        // <= ChunkCodeUnits/InterChunkPauseMs = ~571 units/s
        // <= TargetFeedUnitsPerSecond (600). Ceiling rounding is what makes
        // this hold for fractional elapsed values.
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(TextInjector.InterChunkPauseMs, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk();

        (elapsedMs + sleeps.Sum()).ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs);
    }

    [Fact]
    public void PauseForNextChunk_PerCallPeriod_OverridesTheDefault()
    {
        // A 9-unit surrogate-straddle chunk gets a scaled 16 ms period
        // (stage-2 ledger A7); the pacer must honor the per-call value.
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(14, sleeps.Add, () => now);

        now = 5.0;
        pacer.PauseForNextChunk(16);

        sleeps.ShouldBe(new[] { 11 }); // 16 - 5
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(14.9)]
    [InlineData(15.0)]
    [InlineData(15.9)]
    public void BleedCeiling_Invariant_HoldsForNineUnitStraddleChunkPeriods(double elapsedMs)
    {
        // A 9-unit chunk needs a period of at least 9 * 1000 / 600 = 15 ms
        // to keep feed <= 600 units/s; the scaled period
        // ceil(9 * InterChunkPauseMs / ChunkCodeUnits) = 16 ms provides it
        // with 1 ms margin (stage-2 ledger A7).
        var sleeps = new List<int>();
        var now = 0.0;
        var pacer = new DeadlinePacer(TextInjector.InterChunkPauseMs, sleeps.Add, () => now);

        now = elapsedMs;
        pacer.PauseForNextChunk(16);

        (elapsedMs + sleeps.Sum()).ShouldBeGreaterThanOrEqualTo(15.0);
    }

    [Fact]
    public void Ctor_RejectsNonPositivePeriod()
    {
        Should.Throw<ArgumentOutOfRangeException>(
            () => new DeadlinePacer(0, _ => { }, () => 0.0));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **build FAILS** with `CS0246: The type or namespace name
'DeadlinePacer' could not be found` (a compile failure is this step's RED).

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/DeadlinePacer.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Deadline-based inter-chunk pacing for the guarded injection send.
/// Measured on the production host (2026-07-28): a SendInput batch of
/// KEYEVENTF_UNICODE down/up events costs ~0.85-1.13 ms PER EVENT, so an
/// 8-code-unit chunk (16 events) costs ~14-18 ms in the SendInput call
/// itself. Sleeping the FULL InterChunkPauseMs after such a send (the old
/// design, which assumed queue-insertion ~us/call) roughly HALVED the real
/// feed (~250-285 units/s against the 571 units/s design point). This pacer
/// sleeps only the REMAINDER of each period: max(0, periodMs - elapsed),
/// where elapsed is measured from the end of the previous pause (so it
/// covers the guard probes AND the send). The remainder is CEILING-rounded,
/// so elapsed + sleep can never undershoot the period -- the bleed-safety
/// ceiling (feed &lt;= TextInjector.TargetFeedUnitsPerSecond) is preserved
/// by construction GIVEN the sleep primitive's error mode is delay:
/// Win32 frames waitable-timer inaccuracy as expiration DELAYS (never-early
/// is not contractual -- stage-2 ledger A1), and the design carries real
/// margin (an 8-unit chunk's 14 ms period exceeds the 13.34 ms the 600
/// ceiling strictly needs by 0.67 ms; a 9-unit chunk's scaled 16 ms exceeds
/// its 15 ms need by 1 ms), absorbing sub-ms jitter. The gate's 5 ms probe
/// pins the high-res timer path; the Thread.Sleep fail-safe is NOT
/// never-early below the ~15.6 ms clock resolution (documented "may sleep
/// less"), which is why a broken timer path is a STOP-and-report gate
/// failure, never a production regime. Chunks larger than the standard
/// 8 units (a surrogate-straddle chunk is 9) must pass their scaled period
/// per call (TextInjector.PeriodMsForChunk; stage-2 ledger A7).
/// If the work alone takes &gt;= the period, no sleep is issued:
/// the feed is then throttled by SendInput itself, inherently at or below
/// the safe rate. Guard cadence is unchanged: the halt predicate still runs
/// once per chunk, i.e. at least once per periodMs-worth of feed.
/// </summary>
internal sealed class DeadlinePacer
{
    private readonly int _periodMs;
    private readonly Action<int> _sleep;
    private readonly Func<double> _monotonicMs;
    private double _periodStartMs;

    /// <param name="periodMs">Minimum per-chunk period (work + sleep).</param>
    /// <param name="sleep">Sleep primitive (production: PacingWaiter.Wait).</param>
    /// <param name="monotonicMs">Monotonic millisecond clock. Period
    /// accounting starts at construction, so construct immediately before
    /// the first chunk is sent.</param>
    public DeadlinePacer(int periodMs, Action<int> sleep, Func<double> monotonicMs)
    {
        if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
        _periodMs = periodMs;
        _sleep = sleep;
        _monotonicMs = monotonicMs;
        _periodStartMs = monotonicMs();
    }

    /// <summary>
    /// Sleep the ceiling-rounded remainder of the current period (zero when
    /// the work since the last pause already consumed it), then start the
    /// next period at the end of the sleep. Uses the constructor's default
    /// period (the standard 8-unit chunk).
    /// </summary>
    public void PauseForNextChunk() => PauseForNextChunk(_periodMs);

    /// <summary>
    /// Same, with a per-call period: a chunk larger than the standard
    /// 8 units (InjectionChunker emits 9-unit chunks rather than split a
    /// surrogate pair) needs a proportionally longer period to stay under
    /// the feed ceiling (stage-2 ledger A7).
    /// </summary>
    public void PauseForNextChunk(int periodMs)
    {
        var elapsedMs = _monotonicMs() - _periodStartMs;
        var remainderMs = (int)Math.Ceiling(periodMs - elapsedMs);
        if (remainderMs > 0) _sleep(remainderMs);
        _periodStartMs = _monotonicMs();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.DeadlinePacerTests"
```

Expected: all DeadlinePacerTests PASS (`Failed: 0`, `Errors: 0`).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/DeadlinePacer.cs tests/Winpepper.Platform.Tests/Injection/DeadlinePacerTests.cs
git commit -m "feat(injection): add DeadlinePacer -- ceiling-remainder inter-chunk period math

Measured 2026-07-28 on the production host: SendInput costs ~0.85-1.13 ms
per KEYEVENTF_UNICODE event (~14-18 ms per 16-event chunk), so the old
full-pause design halved the real feed. DeadlinePacer sleeps only
max(0, ceil(period - measured elapsed)); the ceiling rounding preserves the
bleed-safety period floor by construction given the sleep primitive's error
mode is delay (Win32 frames waitable-timer inaccuracy as expiration delays;
stage-2 ledger A1), with 0.67-1 ms of per-period margin absorbing sub-ms
jitter. A per-call period overload serves 9-unit surrogate-straddle chunks
(ceil(9*14/8) = 16 ms keeps feed <= 600 units/s where a fixed 14 ms would
allow ~643; stage-2 ledger A7). Not yet wired into TextInjector.

Linux suite green."
```

---

### Task 2: Wire deadline pacing into TextInjector (seam, call site, docs, Linux test retune)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (constants XML
  docs at lines 37–75; fields/ctor at 77–104; `TryInjectGuarded` doc at
  124–129 and body at 205–213)
- Modify: `src/Winpepper.Platform/Injection/PacingWaiter.cs` (XML doc only)
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

(Line numbers are pre-change anchors; locate by the quoted code if drifted.)

**Interfaces:**
- Consumes: `DeadlinePacer(int periodMs, Action<int> sleep, Func<double> monotonicMs)`
  / `void PauseForNextChunk()` from Task 1.
- Produces: `TextInjector` constructor gains ONE new optional parameter,
  appended LAST so every existing call site stays valid:
  `Func<double>? monotonicMs = null` (monotonic milliseconds; production
  default is Stopwatch-based). Tasks 3's sentinel relies on this default.
  Also produces `internal static int PeriodMsForChunk(string chunk)`
  (= `ceil(chunk.Length * InterChunkPauseMs / (double)ChunkCodeUnits)`;
  8-unit chunks -> 14 ms, 9-unit straddle chunks -> 16 ms; stage-2 ledger
  A7), used by the pause call site and tests.
  The `Action<int> sleep` seam's inter-chunk values now mean **requested
  remainders**, not requested pauses (prelude poll values are unchanged).

- [ ] **Step 1: Retune the two sleeps-list pins and add the two failing deadline tests**

In `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`:

(a) Replace the whole test `Guarded_Paces_Between_Chunks_Only` (currently
lines 189–204, assertion `sleeps.ShouldBe(new[] { 14, ... })`) with:

```csharp
    [Fact]
    public void Guarded_Paces_Between_Chunks_Only()
    {
        // SEMANTIC CHANGE (deadline pacing, 2026-07-28): the values recorded
        // through the sleep seam are now the requested REMAINDERS of each
        // 14 ms period, not fixed requested pauses. With a frozen monotonic
        // clock the measured elapsed is 0, so every remainder is the full
        // InterChunkPauseMs -- which keeps this the canonical
        // "pauses run between chunks only" pin.
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: sleeps.Add,
            monotonicMs: () => 0.0);
        var text = new string('a', 96); // 12 chunks => exactly 11 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14 }); // 11 x TextInjector.InterChunkPauseMs remainders (frozen clock => elapsed 0)
    }
```

(b) In `Guarded_PillClick_ButtonStillDownAtStart_WaitsForRelease_ThenSendsAll`
(currently lines 262–288): add `monotonicMs: () => 0.0` as a final
constructor argument after `sleep: ms => { sleeps.Add(ms); if (sleeps.Count >= 3) buttonDown = false; }`,
and replace the comment line
`// Three 15 ms release-wait polls, then the single 14 ms inter-chunk pause.`
with
`// Three 15 ms release-wait polls, then the single inter-chunk REMAINDER (frozen clock => full 14 ms).`
The assertion `sleeps.ShouldBe(new[] { 15, 15, 15, 14 });` stays as-is.

(c) Append these three new tests to the class (before its closing brace):

```csharp
    [Fact]
    public void Guarded_DeadlinePacing_SleepsOnlyTheRemainderOfThePeriod()
    {
        // Deadline pacing (2026-07-28): the pause accounts for the send
        // call's own measured duration. A 5 ms send inside a 14 ms period
        // leaves a 9 ms remainder.
        var sleeps = new List<int>();
        var now = 0.0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => { now += 5.0; return true; }, // each send "costs" 5 ms
            sleep: ms => { sleeps.Add(ms); now += ms; },
            monotonicMs: () => now);
        var text = new string('a', 24); // 3 chunks => 2 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 9, 9 }); // 14 - 5 measured send ms, per period
    }

    [Fact]
    public void Guarded_SendSlowerThanPeriod_SleepsZeroTimes_GuardCadenceUnchanged()
    {
        // When SendInput alone takes >= the 14 ms period, the injector must
        // not add ANY sleep (the send itself throttles the feed at or below
        // the safe rate) -- and the halt-predicate cadence must not change:
        // still exactly one foreground check per chunk plus the baseline.
        var sleeps = new List<int>();
        var now = 0.0;
        var hwndReads = 0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => { hwndReads++; return 42; },
            sendChunk: _ => { now += 20.0; return true; }, // send alone exceeds the period
            sleep: ms => { sleeps.Add(ms); now += ms; },
            monotonicMs: () => now);
        var text = new string('a', 24); // 3 chunks

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBeEmpty();
        hwndReads.ShouldBe(4); // 1 baseline at entry + 1 mid-paste check per chunk
    }

    [Fact]
    public void Guarded_NineUnitStraddleChunks_GetTheScaledPeriodRemainder()
    {
        // InjectionChunker extends a chunk to 9 units rather than split a
        // surrogate pair. A fixed 14 ms period would let sustained 9-unit
        // chunks feed ~643 units/s > 600, so the period scales per chunk:
        // ceil(9 * 14 / 8) = 16 ms (stage-2 ledger A7). Frozen clock =>
        // the full scaled period is requested through the sleep seam.
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: sleeps.Add,
            monotonicMs: () => 0.0);
        var block = "a\U0001F600\U0001F600\U0001F600\U0001F600"; // 9 units: 1 BMP char + 4 surrogate pairs
        var text = block + block + block; // 3 straddle chunks of 9 units => 2 inter-chunk pauses

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 16, 16 }); // TextInjector.PeriodMsForChunk(9-unit chunk) remainders
    }
```

- [ ] **Step 2: Run the injection tests to verify the new/changed tests fail**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **build FAILS** with `CS1739` / `CS1503` (no `monotonicMs`
parameter on `TextInjector`). That compile failure is this step's RED.

- [ ] **Step 3: Implement the TextInjector changes**

All edits in `src/Winpepper.Platform/Injection/TextInjector.cs`.

(a) After the field `private readonly Func<long, ForegroundElevation> _foregroundElevation;`
(line 82) add:

```csharp
    private readonly Func<double> _monotonicMs;
```

(b) Replace the constructor (lines 90–104) with (ONLY additions: the last
parameter and the last assignment):

```csharp
    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null,
        Action<int>? sleep = null,
        Func<long, ForegroundElevation>? foregroundElevation = null,
        Func<double>? monotonicMs = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
        _sleep = sleep ?? PacingWaiter.Wait;
        _foregroundElevation = foregroundElevation ?? ElevationProbe.Probe;
        _monotonicMs = monotonicMs ?? DefaultMonotonicMs;
    }
```

(c) Next to `DefaultKeyProbe` / `DefaultForegroundProbe` (after line 122)
add:

```csharp
    /// <summary>Monotonic milliseconds (Stopwatch-based; immune to wall-clock changes).</summary>
    private static double DefaultMonotonicMs()
        => System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
           / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Per-chunk minimum period: <see cref="InterChunkPauseMs"/> scaled by
    /// the chunk's actual code-unit count. InjectionChunker extends a chunk
    /// to <see cref="ChunkCodeUnits"/>+1 = 9 units rather than split a
    /// surrogate pair; a fixed 14 ms period would let sustained 9-unit
    /// chunks feed ~643 units/s &gt; 600 (stage-2 ledger A7).
    /// ceil(8 * 14 / 8) = 14 (unchanged); ceil(9 * 14 / 8) = 16 (~562
    /// units/s, 1 ms margin over the 15 ms the 600 ceiling strictly needs).
    /// </summary>
    internal static int PeriodMsForChunk(string chunk)
        => (int)Math.Ceiling(chunk.Length * (double)InterChunkPauseMs / ChunkCodeUnits);
```

(d) In `TryInjectGuarded`, replace these two lines (205–213 region):

```csharp
        var chunks = InjectionChunker.Split(text, ChunkCodeUnits);
```
with
```csharp
        var chunks = InjectionChunker.Split(text, ChunkCodeUnits);
        // Deadline pacing: period accounting starts NOW, so the first
        // chunk's guard probes + send count toward the first period.
        var pacer = new DeadlinePacer(InterChunkPauseMs, _sleep, _monotonicMs);
        // Pause k follows chunks[k]; its period scales with THAT chunk's
        // unit count (9-unit straddle chunks get 16 ms -- stage-2 ledger A7).
        var pausedChunks = 0;
```
and
```csharp
            pauseBetweenChunks: () => _sleep(InterChunkPauseMs),
```
with
```csharp
            pauseBetweenChunks: () => pacer.PauseForNextChunk(PeriodMsForChunk(chunks[pausedChunks++])),
```

(e) Replace the `TargetFeedUnitsPerSecond` XML doc (lines 37–53) with:

```csharp
    /// <summary>
    /// CEILING on the guarded send's feed rate, in UTF-16 code units per
    /// second. Chosen to match the observed render rate of slow-rendering
    /// target apps (~600 chars/s): when feed &lt;= render, the
    /// queued-but-undelivered BACKLOG cannot grow, so a mid-paste window
    /// switch can leak at most the true in-flight chunk
    /// (&lt;= <see cref="ChunkCodeUnits"/>). The previous 1600 units/s
    /// design point fed slow apps ~2.5x faster than they rendered; the
    /// growing backlog followed focus on a human click-switch and sprayed
    /// dozens of characters (paste-path-hardening, 2026-07-27 -- a
    /// deliberate, owner-approved supersession of the bleed-hardening
    /// plan's "&gt;= 1600 nominal" feed-rate floor).
    /// Semantics under deadline pacing (2026-07-28): this remains a
    /// bleed-safety CEILING the feed may approach but never exceed. The
    /// pacer subtracts the MEASURED send time from each pause (see
    /// <see cref="DeadlinePacer"/>), so the actual feed sits near the 571
    /// units/s nominal instead of the ~250-285 units/s the old full-pause
    /// design delivered on this host, where the SendInput call itself costs
    /// ~1 ms/event (measured 2026-07-28; a 458-char paste took ~1.6 s
    /// against the ~0.8 s design point).
    /// </summary>
```

(f) Replace the `InterChunkPauseMs` XML doc (lines 56–73) with (the
constant's expression is unchanged):

```csharp
    /// <summary>
    /// Minimum per-chunk PERIOD (send + sleep) for the guarded send,
    /// derived from <see cref="TargetFeedUnitsPerSecond"/> by CEILING
    /// division: ceil(8 * 1000 / 600) = 14 ms, i.e. ~571 code units/s
    /// nominal -- rounded UP so the nominal feed can never EXCEED the
    /// target (truncating division gives 13 ms = ~615 units/s, above the
    /// claimed render rate, and the backlog would grow again; stage-2
    /// ledger A1). Load-bearing measurement (2026-07-28, live probes on the
    /// production host): SendInput with KEYEVENTF_UNICODE events is NOT
    /// queue-insertion cheap here -- it costs ~0.85-1.13 ms PER EVENT
    /// (linear in events, so ~14-18 ms per 16-event chunk; other low-level
    /// hooks in the environment dominate; Winpepper's own hook adds only
    /// ~0.2 ms/event). An old production log once recorded ~13 us/event, so
    /// the original "queue-insertion (~us per call)" ledger assumption (A1)
    /// is stale for this machine -- which is why pacing is DEADLINE-based:
    /// each chunk sleeps only max(0, ceil(period - measured elapsed)) via
    /// <see cref="DeadlinePacer"/>, where period is scaled per chunk
    /// (<see cref="PeriodMsForChunk"/>: 14 ms for 8-unit chunks, 16 ms for
    /// the 9-unit surrogate-straddle chunks the chunker emits rather than
    /// split a pair -- a fixed 14 ms would let those feed ~643 units/s;
    /// stage-2 ledger A7). The ceiling rounding means the period cannot
    /// undershoot its floor, and a send that alone exceeds the period
    /// sleeps zero -- SendInput itself then throttles the feed below the
    /// ceiling. The pace stays real through PacingWaiter (the production
    /// sleep default): Win32 frames waitable-timer inaccuracy as expiration
    /// DELAYS, and the periods carry 0.67-1 ms of margin over what the 600
    /// ceiling strictly needs, absorbing sub-ms jitter (stage-2 ledger A1).
    /// The Thread.Sleep fail-safe is NOT never-early (documented to
    /// possibly sleep LESS than requested below the ~15.6 ms clock
    /// resolution); a broken timer path is caught by the gate's 5 ms probe
    /// -- STOP and report, never a production regime.
    /// The per-chunk PERIOD floor is pinned on the gate host by
    /// InterChunkPacingWindowsTests.
    /// </summary>
```

(g) In the `TryInjectGuarded` XML doc, replace (lines 125–129):

```csharp
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, pausing
    /// <see cref="InterChunkPauseMs"/> between chunks (pacing is what makes
    /// the guard able to observe a human halt gesture at all -- an unpaced
    /// loop is queue-insertion-fast and finishes in milliseconds) and
```
with
```csharp
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, enforcing a per-chunk
    /// PERIOD of at least <see cref="InterChunkPauseMs"/> -- the send's own
    /// measured duration counts toward the period and only the remainder is
    /// slept (<see cref="DeadlinePacer"/>); the paced period is what lets
    /// the guard observe a human halt gesture at a bounded cadence -- and
```

(h) In `src/Winpepper.Platform/Injection/PacingWaiter.cs`, replace the doc
sentence (lines 14–19 of the file's XML comment):

```csharp
/// risk. At the current 14 ms production pause
/// (TextInjector.InterChunkPauseMs, render-rate pacing) the Thread.Sleep
/// fail-safe (~15.6 ms) overshoots by only ~11%, but the high-res timer
/// keeps the pace deliberate, and the fixed 5 ms probe in
/// InterChunkPacingWindowsTests still proves the fast path engages on the
/// gate host. Fail-safe: if the timer cannot be created or set, falls back
```
with
```csharp
/// risk. Under deadline pacing (DeadlinePacer, 2026-07-28) production waits
/// are the REMAINDER of the per-chunk period (typically ~0-9 ms on the
/// measured host, where the send itself costs ~14-18 ms/chunk); the
/// high-res timer keeps the pace deliberate (its documented error mode is
/// expiration DELAY), and the fixed 5 ms probe in
/// InterChunkPacingWindowsTests still proves the fast path engages on the
/// gate host -- which matters because the Thread.Sleep fail-safe is NOT
/// never-early for sub-resolution waits (documented "may sleep less" below
/// the ~15.6 ms clock tick; stage-2 ledger A1) and must never be the
/// production regime. Fail-safe: if the timer cannot be created or set, falls back
```

- [ ] **Step 4: Run the injection tests to verify everything passes**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: PASS (`Failed: 0`). Pay attention that
`GuardedInjectionRunTests.Pause_Runs_Between_Chunks_Never_Before_The_First`
and ALL prelude tests (`Guarded_ModifierWait_UsesInjectedSleep_NeverWallClock`
`{15,15}`, `Guarded_ButtonHeldPastTimeout... Sum()==1500`) pass UNMODIFIED —
they prove the preludes and `GuardedInjectionRun`'s contract were not
disturbed.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/TextInjector.cs src/Winpepper.Platform/Injection/PacingWaiter.cs tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(injection): deadline-based pacing -- subtract measured send time from the inter-chunk pause

Measured 2026-07-28: SendInput itself costs ~14-18 ms per 8-char chunk on
this host, so the old unconditional 14 ms sleep halved the real feed
(~250-285 units/s vs the 571 design point; a 458-char paste measured
~1.6 s vs ~0.8 s design). TextInjector now feeds each chunk through
DeadlinePacer: sleep only max(0, ceil(period - elapsed)); zero when the send
alone covers the period. The period scales per chunk (PeriodMsForChunk:
14 ms for 8-unit chunks, 16 ms for the 9-unit surrogate-straddle chunks the
chunker emits rather than split a pair -- a fixed 14 ms would allow ~643
units/s > 600; stage-2 ledger A7). The bleed ceiling (feed <= 600 units/s)
is preserved by the ceiling rounding plus 0.67-1 ms per-period margin
(stage-2 ledger A1); the halt predicate still runs once per chunk. The Action<int> sleep seam's inter-chunk values now mean requested
REMAINDERS -- Linux pins retuned deliberately with a frozen injected
monotonic clock; prelude polling (ModifierGuard.WaitForRelease) untouched.
Constant XML docs updated: the stale queue-insertion (~us/call) assumption
is superseded by the 2026-07-28 measurements.

Linux suite green."
```

---

### Task 3: Retune the Windows pacing sentinel to prove the PERIOD floor

**Files:**
- Modify: `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs`
  (73-line file; keep the 5 ms probe, replace the production-pace test and
  the class XML doc)

**Interfaces:**
- Consumes: `TextInjector` constructor seams from Task 2 (notably that
  `sleep` and `monotonicMs` left null use production
  `PacingWaiter.Wait` + Stopwatch clock), `TextInjector.ChunkCodeUnits`,
  `TextInjector.InterChunkPauseMs`, `InjectionRunOutcome.Completed`,
  `ForegroundElevation.NotElevated` (enum in `Winpepper.Platform.Injection`).
- Produces: nothing consumed later; this sentinel EXECUTES only in Task 5's
  Windows gate.

**Design note (margins — a prior council flagged the old sentinel sitting
within one timer quantum of its boundary):** the retuned assertion averages
40 periods and places the boundary where the FAILURE modes live, not where
the pass mode lives. Compensated expectation ≈ 14.2–14.7 ms/period
(waitable timer overshoot +0.2–0.4 ms, never early). Uncompensated
(regression to full-pause) ≈ 19.2+ ms; Thread.Sleep-fallback ≈ 20.6 ms.
Floor `>= 14 - 0.5` (existing half-ms grace convention); ceiling `< 17.0`
leaves ~2.3 ms margin above the pass mode and ~2.2 ms below the nearest
failure mode.

- [ ] **Step 1: Rewrite the sentinel file**

Replace the entire contents of
`tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs`
with:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for injection pacing (retuned for deadline
/// pacing, 2026-07-28, superseding the paste-path-hardening raw-sleep-floor
/// sentinel). Two independent checks:
/// (1) a FIXED 5 ms probe that proves the high-resolution waitable timer
/// engages at all on this host -- Thread.Sleep(5) quantizes to ~15.6 ms
/// (ledger V1), so 10 ms cleanly separates the paths; a failure here means
/// the timer path is broken: STOP and report, do not widen the threshold or
/// swap in a spin-wait without explicit approval. The 5 ms probe is kept
/// SEPARATE from the production pace on purpose: near the 14 ms period the
/// high-res timer and the Sleep fallback are nearly indistinguishable on a
/// noisy host, so a production-pace measurement has no discriminating power
/// for timer health.
/// (2) a production-pace check that proves the per-chunk PERIOD floor AND
/// the send-time compensation through the real injector: with a simulated
/// 5 ms send, the average period must stay &gt;= InterChunkPauseMs (bleed
/// safety: feed &lt;= ~571 &lt;= 600 units/s) yet clearly BELOW what an
/// uncompensated full-pause injector would burn (~19.2 ms). Averaged over
/// 40 periods so single timer-quantum noise cannot trip either boundary.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class InterChunkPacingWindowsTests
{
    [Fact]
    public void PacingWaiter_5msProbe_HighResolutionTimerEngages()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) PacingWaiter.Wait(5);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PacingWaiter.Wait(5);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Measured 5.22-5.37 ms avg on the gate host (0/240 samples >= 10 ms;
        // bleed-hardening ledger B1). 10 ms cleanly separates the high-res
        // path from the ~15.6 ms legacy-quantum Thread.Sleep fallback.
        avgMs.ShouldBeLessThan(10.0);
    }

    [Fact]
    public void Injector_ProductionPace_PeriodFloor_SendTimeCompensated()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Simulated send cost, burned by a Stopwatch busy-wait (precise,
        // unlike Thread.Sleep). 5 ms sits inside the 14 ms period so BOTH
        // properties are observable: a nonzero remainder must be slept, and
        // the send time must be deducted from it.
        const double simulatedSendMs = 5.0;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => { BusyWaitMs(simulatedSendMs); return true; },
            foregroundElevation: _ => ForegroundElevation.NotElevated);
        // sleep and monotonicMs stay at PRODUCTION defaults:
        // PacingWaiter.Wait + the Stopwatch-based monotonic clock.

        // Warm-up (JIT + timer state).
        injector.TryInjectGuarded(new string('a', 3 * TextInjector.ChunkCodeUnits))
            .ShouldBe(InjectionRunOutcome.Completed);

        const int periods = 40; // 41 chunks => 40 inter-chunk periods
        var text = new string('a', (periods + 1) * TextInjector.ChunkCodeUnits);
        var sw = Stopwatch.StartNew();
        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);
        sw.Stop();

        var avgPeriodMs = sw.Elapsed.TotalMilliseconds / periods;
        // FLOOR (bleed safety): every period is send + ceiling-rounded
        // remainder sleep, and the waitable timer's documented error mode
        // is expiration DELAY (never-early is not contractual, but early
        // firing has never been observed on this host and the period
        // carries 0.67 ms margin -- stage-2 ledger A1), so the per-period
        // floor holds on real timers (this assertion is its empirical pin);
        // half-millisecond grace for measurement noise (same convention as
        // the retired raw-sleep floor).
        avgPeriodMs.ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs - 0.5);
        // CEILING (compensation proof): an UNcompensated injector burns
        // simulatedSendMs + a full 14 ms sleep ~= 19.2+ ms/period (and the
        // Thread.Sleep fallback ~= 20.6). Compensated expectation is
        // ~14.2-14.7 ms, so 17.0 leaves ~2.3 ms of margin on a noisy host
        // while cleanly separating both failure modes. A failure at
        // ~19-21 ms means compensation is not happening or the high-res
        // timer is broken (cross-check the 5 ms probe): STOP and report --
        // do not widen the threshold without explicit approval.
        avgPeriodMs.ShouldBeLessThan(17.0);
    }

    private static void BusyWaitMs(double ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalMilliseconds < ms) Thread.SpinWait(64);
    }
}
```

- [ ] **Step 2: Verify it compiles and the Linux suite still filters it**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
./scripts/linux-tests.sh
```

Expected: build succeeds; `LINUX SUITE: GREEN` (the class is excluded by
`-notrait "Platform=Windows"`; its runtime `if (!OperatingSystem.IsWindows()) return;`
is the belt-and-braces second gate). The sentinel EXECUTES in Task 5.

- [ ] **Step 3: Commit**

```bash
git add tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs
git commit -m "test(injection): retune Windows pacing sentinel to prove the per-chunk PERIOD floor

The old production-pace sentinel pinned PacingWaiter.Wait(14) directly (a
raw-sleep floor) and was blind to the injector's cycle time -- under
deadline pacing it would have kept passing with zero signal. The retuned
sentinel drives TryInjectGuarded with a busy-wait 5 ms fake send over
production PacingWaiter + Stopwatch clock and asserts the 40-period average
in [13.5, 17.0): the floor proves bleed safety on real timers, the ceiling
proves send-time compensation (uncompensated ~= 19.2+ ms). Boundaries sit
>= 2 ms from both the pass mode and the nearest failure mode (prior council
flagged the old one-quantum margin). The 5 ms high-res-timer probe is kept
unchanged. Runs at the Windows gate only.

Linux suite green (sentinel trait-filtered)."
```

---

### Task 4: Hook fast-path for injected events (CHANGE 2, evidence-gated)

**Files:**
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` (`TryProcessKey`,
  lines 97–248 region)
- Test: `tests/Winpepper.Platform.Tests/Hotkeys/InjectedEventFastPathTests.cs` (create)

**Interfaces:**
- Consumes (all existing, verified 2026-07-28):
  - `public bool HotkeyHook.TryProcessKey(int vk, bool down, out HotkeyEvent? evt, int scanCode = 0, bool isInjected = false)` — the test seam; the native `HookCallback` already decodes `LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED` into `isInjected` at `HotkeyHook.cs:503` and needs NO change.
  - `public IDisposable BeginRawCapture(Action<RawKeyTransition> sink)`
  - `public readonly record struct RawKeyTransition(int VirtualKey, int ScanCode, bool IsDown, bool IsInjected, bool IsRepeat)`
  - `HotkeyHook` ctor seams: `keyPhysicallyDown`, `beforeLongPressSpaceAdmission`.
- Produces: no new public surface. Behavior contract: injected events pass
  through (`return false`, `evt == null`), never mutate hook key-state, and
  are still forwarded to an active raw-capture sink with `IsInjected: true`.

**Load-bearing validation — findings already gathered (2026-07-28 code
survey), re-verify in Step 1:**

- (a) The hook does **NOT** already skip injected events. `isInjected` is
  read at `HotkeyHook.cs:503` and threaded into `TryProcessKey`, where it
  only guards `_passedThroughKeys` bookkeeping at 5 sites
  (`:143, :174, :179, :200, :238`) and is forwarded to the raw-capture sink
  (`:134`). So CHANGE 2 is live, not moot.
- (b) Nothing in the hook's correctness depends on seeing injected events —
  with two findings that shape the design:
  1. Chord matching folds `_modifiers` over the event stream
     (`UpdateModifierState`, called unconditionally at `:116`), NOT
     `GetAsyncKeyState` (the async probe is used only to self-heal). Our own
     `NeutralizeHeldModifiers` injects generic-VK KEYUPs
     `{0x10, 0x11, 0x12, 0x5B, 0x5C}`; `ModifierForVirtualKey` maps only
     `0xA0–0xA5, 0x5B, 0x5C`, so injected generic Ctrl/Shift/Alt ups are
     inert *by accident*, but injected **LWin/RWin ups DO clear
     `_modifiers`** and can fire a spurious `HoldUp` on a Win-containing
     hold chord while the key is still physically down — a latent wedge the
     fast-path FIXES. (Whether Windows normalizes injected generic
     `VK_SHIFT` to L/R in the LL proc is unmeasured; the fast-path makes the
     question moot for hook state either way.)
  2. `ChordRecorder.OnRawKey` (`ChordRecorder.cs:139`) consumes
     `transition.IsInjected` to IGNORE injected transitions — so the
     raw-capture sink must KEEP receiving them for that contract to stay
     live. The fast-path therefore forwards to the sink before returning.
     (`_captureKeysDown` intentionally stops tracking injected downs; today
     such entries are self-healed anyway because `GetAsyncKeyState` reads
     them as up.)
- (c) Safe to implement per the design below. Honest expected saving: the
  fast-path skips `RecoverIfReleased` (1 `GetAsyncKeyState`),
  `UpdateModifierState`, `PruneStaleKeys` (an N-entry `GetAsyncKeyState`
  sweep over three dictionaries) and all dictionary/set ops — most, but not
  all, of the measured ~0.2 ms/event (the `HookCallback` marshalling and
  heartbeat write remain).
- (d) Stage-2 validation (2026-07-28 ledger A4/A5/A6): LLKHF_INJECTED is
  set for ALL SendInput-generated LL-hook events (incl. KEYEVENTF_UNICODE
  and KEYUPs; the decode at `HotkeyHook.cs:503` matches the documented bit
  layout exactly), every hook recovery path is next-PHYSICAL-event- or
  timer-driven (deferring bookkeeping across an injected-only paste reduces
  to the accepted quiet-period baseline), and every KNOWN chord source is
  physical (the trigger pedal is firmware-programmed HID; the repo shows no
  remapper/macro layer). Accepted residual A4r: an UNKNOWN injection-based
  chord source (remapper / RDP / on-screen keyboard) would be ignored by
  the fast-path; if a chord ever stops working for such a source, the
  recorded fallback design is a `dwExtraInfo` self-tag (TextInjector
  currently sends `ExtraInfo = IntPtr.Zero`) — skip only OUR OWN injections
  instead. The raw-capture sink forward in Step 4(a) is a load-bearing
  invariant — do not remove it.

- [ ] **Step 1: Re-verify the validation findings in code (drop-gate)**

Open `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` and confirm:
`UpdateModifierState(vk, down);` is called unconditionally near the top of
`TryProcessKey` (line ~116); the only `isInjected` uses inside
`TryProcessKey` are the `_passedThroughKeys` guards at ~143, ~174, ~179,
~200, ~238 and the sink forward at ~134; there is no injected early-out.
Also confirm `ChordRecorder.cs:139` still reads
`transition.IsInjected || transition.IsRepeat` to ignore.

**If any of this does not hold** (e.g. an early-out already exists, or a new
sink consumer requires hook state updates from injected events): **DROP this
task** — make no code change, and instead commit a note appended to this
plan document under a `## CHANGE 2 dropped` heading recording exactly which
finding failed, then proceed to Task 5. Otherwise continue.

- [ ] **Step 2: Write the tests (3 RED for new behavior, 3 pins)**

Create `tests/Winpepper.Platform.Tests/Hotkeys/InjectedEventFastPathTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// Pins the injected-event fast-path (standard WH_KEYBOARD_LL practice,
/// 2026-07-28): synthetic events (LLKHF_INJECTED) pass straight through --
/// they never match chords, never mutate key-state tracking, and are never
/// swallowed. Winpepper's own injection stream (KEYEVENTF_UNICODE text plus
/// NeutralizeHeldModifiers KEYUPs) is the dominant producer; the fast-path
/// removes the hook's per-event tax (~0.2 ms/event measured on the
/// production host) from every injected keystroke system-wide. The chord
/// recorder still receives injected transitions and filters them itself.
/// </summary>
public class InjectedEventFastPathTests
{
    private const int VK_LCONTROL = 0xA2;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_LWIN = 0x5B;
    private const int VK_SPACE = 0x20;
    private const int VK_PACKET = 0xE7; // KEYEVENTF_UNICODE arrives as VK_PACKET

    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                      string toggle = "Ctrl+Shift+Space",
                                      string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(), keyPhysicallyDown: _ => true);

    [Fact] // RED before the fast-path
    public void InjectedWinKeyUp_DuringActiveHold_DoesNotEndTheHold()
    {
        // NeutralizeHeldModifiers sends KEYEVENTF_KEYUP for physically-held
        // modifiers, including generic VK_LWIN/VK_RWIN. Before the
        // fast-path, that injected KEYUP cleared _modifiers (the event-
        // stream fold) and ended a Win-containing hold chord spuriously
        // while the key was still physically down.
        var hook = NewHook(hold: "Ctrl+Win");

        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LWIN, down: true, out var holdDown).ShouldBeFalse();
        holdDown!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Our own neutralization KEYUP: must be ignored.
        hook.TryProcessKey(VK_LWIN, down: false, out var injectedUp, isInjected: true)
            .ShouldBeFalse();
        injectedUp.ShouldBeNull();

        // The PHYSICAL release still ends the hold, exactly once.
        hook.TryProcessKey(VK_LWIN, down: false, out var physicalUp).ShouldBeFalse();
        physicalUp!.Kind.ShouldBe(HotkeyEventKind.HoldUp);
    }

    [Fact] // RED before the fast-path
    public void InjectedChord_DoesNotFireToggle_AndIsNeverSwallowed()
    {
        // Synthetic input (any process's SendInput) must not trigger
        // hotkeys and must never be swallowed.
        var hook = NewHook();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _, isInjected: true).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT, down: true, out _, isInjected: true).ShouldBeFalse();
        hook.TryProcessKey(VK_SPACE, down: true, out var evt, isInjected: true).ShouldBeFalse();

        evt.ShouldBeNull();
    }

    [Fact] // RED before the fast-path
    public void InjectedSpaceDown_DoesNotStartALongPressSpaceHold()
    {
        var admissions = 0;
        var hook = new HotkeyHook(HotkeyChord.Parse("Space"), HotkeyChord.Parse("F24"),
            HotkeyChord.Parse("Esc"), new NullLogger<HotkeyHook>(),
            keyPhysicallyDown: _ => true,
            beforeLongPressSpaceAdmission: () => admissions++);

        hook.TryProcessKey(VK_SPACE, down: true, out var evt, isInjected: true).ShouldBeFalse();

        evt.ShouldBeNull();
        admissions.ShouldBe(0);
    }

    [Fact] // pin: passes before AND after -- makes the accidental inertness deliberate
    public void InjectedGenericModifierKeyUps_DoNotDisturbPhysicalModifierState()
    {
        // NeutralizeHeldModifiers sends generic VKs {0x10, 0x11, 0x12, 0x5B,
        // 0x5C}. None of them may perturb the fold over PHYSICAL events: a
        // physically-completed toggle chord must still fire afterwards.
        var hook = NewHook();

        hook.TryProcessKey(VK_LCONTROL, down: true, out _);
        hook.TryProcessKey(VK_LSHIFT, down: true, out _);

        foreach (var vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            hook.TryProcessKey(vk, down: false, out _, isInjected: true).ShouldBeFalse();

        hook.TryProcessKey(VK_SPACE, down: true, out var evt).ShouldBeTrue(); // toggle trigger key swallowed
        evt!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact] // pin: passes before AND after
    public void InjectedUnicodeTextStream_PassesThrough_WithNoEvents()
    {
        // The paste stream: one 8-code-unit chunk = 16 injected VK_PACKET events.
        var hook = NewHook();

        for (var i = 0; i < 8; i++)
        {
            hook.TryProcessKey(VK_PACKET, down: true, out var d, isInjected: true).ShouldBeFalse();
            d.ShouldBeNull();
            hook.TryProcessKey(VK_PACKET, down: false, out var u, isInjected: true).ShouldBeFalse();
            u.ShouldBeNull();
        }
    }

    [Fact] // pin: passes before AND after -- the raw-capture contract stays live
    public void RawCapture_StillReceivesInjectedTransitions_MarkedInjected()
    {
        // ChordRecorder filters injected transitions ITSELF
        // (ChordRecorder.OnRawKey ignores IsInjected), so the hook must keep
        // forwarding them to an active sink.
        var hook = NewHook();
        var seen = new List<RawKeyTransition>();
        using var lease = hook.BeginRawCapture(seen.Add);

        hook.TryProcessKey(0x41, down: true, out _, scanCode: 30, isInjected: true)
            .ShouldBeFalse();

        seen.Count.ShouldBe(1);
        seen[0].VirtualKey.ShouldBe(0x41);
        seen[0].IsDown.ShouldBeTrue();
        seen[0].IsInjected.ShouldBeTrue();
    }
}
```

- [ ] **Step 3: Run the new tests to verify the RED/pin split**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Hotkeys.InjectedEventFastPathTests"
```

Expected: exactly the 3 tests marked `// RED before the fast-path` FAIL
(`InjectedWinKeyUp...` gets a non-null HoldUp; `InjectedChord...` fires
Toggle and swallows Space; `InjectedSpaceDown...` records an admission);
the 3 pins PASS. If a *pin* fails, the validation findings were wrong —
STOP, re-run Step 1's drop-gate analysis, and if the hook genuinely depends
on injected events, drop CHANGE 2 per the gate.

- [ ] **Step 4: Implement the fast-path**

All edits in `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`, inside
`TryProcessKey`.

(a) Immediately after `var bindings = Volatile.Read(ref _bindings);`
(line ~103, before the `_spaceHold.RecoverIfReleased();` block) insert:

```csharp
        // Injected fast-path (standard WH_KEYBOARD_LL practice, 2026-07-28):
        // synthetic events (LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED --
        // SendInput from ANY process, including our own TextInjector and
        // NeutralizeHeldModifiers) never participate in chord matching or
        // key-state tracking; they pass straight through. This (a) removes
        // the hook's per-event tax (~0.2 ms/event measured on the production
        // host) from every injected keystroke system-wide, and (b) fixes a
        // latent wedge: our own neutralization KEYUP for a physically-held
        // Win key used to clear _modifiers and end a Win-containing hold
        // chord spuriously. The chord recorder still receives injected
        // transitions (it filters them itself via RawKeyTransition.IsInjected),
        // so recording-mode behavior is contract-identical. _captureKeysDown
        // intentionally no longer tracks injected downs -- such entries were
        // self-healed anyway (GetAsyncKeyState reads them as up).
        if (isInjected)
        {
            var rawCaptureForInjected = Volatile.Read(ref _rawCapture);
            if (rawCaptureForInjected is not null)
            {
                try
                {
                    rawCaptureForInjected.Sink(
                        new RawKeyTransition(vk, scanCode, down, IsInjected: true, IsRepeat: false));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Raw hotkey capture callback failed");
                }
            }
            return false; // never swallow synthetic input
        }
```

(b) Remove the now-dead `isInjected` guards (unreachable for injected
events after (a)) — five mechanical edits:

1. Raw-capture branch (~lines 142–148): replace
   ```csharp
            if (!isInjected)
            {
                if (down && _passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
                else if (!down)
                    _passedThroughKeys.Remove(vk);
            }
   ```
   with
   ```csharp
            if (down && _passedThroughKeys.ContainsKey(vk))
                _passedThroughKeys[vk] = now;
            else if (!down)
                _passedThroughKeys.Remove(vk);
   ```
2. Suspend/drain down path (~173–174): replace
   ```csharp
                if (!isInjected && _passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
   ```
   with
   ```csharp
                if (_passedThroughKeys.ContainsKey(vk))
                    _passedThroughKeys[vk] = now;
   ```
3. Suspend/drain up path (~178): replace
   `if (!isInjected) _passedThroughKeys.Remove(vk);`
   with `_passedThroughKeys.Remove(vk);`
4. Passed-through ownership block (~199): delete the line
   `if (isInjected) return false;`
5. Readiness-gated block (~237): replace
   `if (down && !isInjected) _passedThroughKeys[vk] = now;`
   with `if (down) _passedThroughKeys[vk] = now;`

The `isInjected` parameter itself stays (consumed by the fast-path); the
`HookCallback` decode at line ~503 and `KeyboardHookNative` constants are
untouched.

- [ ] **Step 5: Run the hook tests, then the full class matrix**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: PASS (`Failed: 0`) — all 6 new tests plus the entire existing
Hotkeys suite (`HotkeyHookLogicTests`, `LongPressSpaceHookTests`,
`ModifierPassthroughTests`, `SwallowSelfHealTests`,
`CaptureDrainSelfHealTests`, `RawCaptureTests`, `ChordRecorderTests`,
`HotkeyHookReinstallTests`), which pin that PHYSICAL-event behavior is
byte-for-byte unchanged.

- [ ] **Step 6: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Hotkeys/HotkeyHook.cs tests/Winpepper.Platform.Tests/Hotkeys/InjectedEventFastPathTests.cs
git commit -m "perf(hotkeys): injected-event fast-path in the low-level hook

Injected events (LLKHF_INJECTED / LLKHF_LOWER_IL_INJECTED) now pass
straight through TryProcessKey: no chord matching, no key-state mutation,
never swallowed. Load-bearing validation (2026-07-28 code survey, pinned by
tests): chord state folds over the event stream, and our own
NeutralizeHeldModifiers KEYUP for a physically-held Win key used to clear
_modifiers and could end a Win-containing hold spuriously -- fixed by the
skip. The raw-capture sink still receives injected transitions
(ChordRecorder filters them itself), keeping recording-mode behavior
contract-identical; the now-dead per-branch isInjected guards are removed.
Honest expected saving: most of the measured ~0.2 ms/event hook tax on this
host (~3 ms per 16-event chunk; marshalling/heartbeat in HookCallback
remain), plus every other app's synthetic input is un-taxed.

Linux suite green."
```

---

### Task 5: Windows gate — execute the retuned sentinels on the real host

**Files:** none (verification only; run before any push, per AGENTS.md).

**Interfaces:**
- Consumes: everything committed by Tasks 1–4; `./scripts/windows-gate.sh`
  (runs from WSL against the Windows host over the `\\wsl.localhost` UNC
  path; 12 project/TFM test runs; 20–40+ min — use a generous timeout and
  do not run concurrently with `linux-tests.sh`).

- [ ] **Step 1: Run the gate**

```bash
cd /home/dan/code/winpepper/.worktrees/injection-pacing-optimization
./scripts/windows-gate.sh
```

Expected: exit 0 and `GATE: GREEN`. This is where
`InterChunkPacingWindowsTests` actually executes (both TFM runs) — the
5 ms probe must pass, and
`Injector_ProductionPace_PeriodFloor_SendTimeCompensated` must land in
[13.5, 17.0) ms average period, proving deadline compensation on the real
host timers.

- [ ] **Step 2: Interpret failures per the house rules**

- A transient UNC failure mentioning `retry should be performed`: re-run
  the gate quietly; it is not a code failure.
- `Winpepper.Cleanup.Tests` model-eval failures: owned by the parallel
  cleanup stream — verify the failing test names are confined to
  `Winpepper.Cleanup.Tests` model-eval tests; if so they do not block this
  plan. Everything else must be green.
- `Hook_Installs_And_DisposesCleanly` may TIMEOUT headless (known caveat in
  the gate script header — needs an interactive unlocked desktop); treat
  per the script's documented expectations, not as a regression from this
  plan.
- If `Injector_ProductionPace_PeriodFloor_SendTimeCompensated` fails at
  ~19–21 ms average: compensation is not happening — **HALT and report**
  (halt condition 2/3). If it fails just under 13.5 or just over 17.0:
  **STOP and report** the measured value; do not widen the threshold.

- [ ] **Step 3: Record the result**

No commit from this task if green (the gate is a pre-push check). If the
gate is green, the plan is complete and the branch is push-ready.

---

## Self-Review (performed at authoring time, 2026-07-28)

1. **Spec coverage:** CHANGE 1 deadline pacing → Tasks 1–2 (remainder math,
   seam, call site, ceiling proof, guard cadence pin); XML docs on both
   constants → Task 2 steps 3e/3f; Windows sentinel retune to a PERIOD
   floor with deliberate margins → Task 3; Linux sleeps-seam semantic
   retune, documented → Task 2 step 1; CHANGE 2 validation gates (a)/(b)/(c)
   → Task 4 findings + Step 1 drop-gate + pins; honest saving recorded →
   Task 4 commit body; measured facts recorded → dedicated section;
   AGENTS.md gates & known transients → Global Constraints + Task 5;
   preserve list & stop conditions → Global Constraints + HALT CONDITIONS.
   No gaps found; no unresolved coverage gaps.
2. **No silent deferrals:** every behavior lands in production code within
   this plan; the only test doubles are constructor seams that already exist
   in production (`sleep`, `sendChunk`, `isKeyDown`, `foregroundHwnd`,
   `foregroundElevation`) plus the new `monotonicMs` seam whose production
   default (`Stopwatch`) is exercised on the real host by Task 3's sentinel
   at Task 5's gate. The skipped-by-design SendInput integration test
   (`TextInjectorIntegrationTests`) predates this plan and is unchanged.
3. **Placeholder scan:** none — every code step carries complete code;
   every command carries expected output.
4. **Type consistency:** `DeadlinePacer(int, Action<int>, Func<double>)` /
   `PauseForNextChunk()` match between Task 1 (produces) and Tasks 2–3
   (consume); `monotonicMs` is appended LAST on the `TextInjector` ctor in
   Task 2 and used by name in Tasks 2–3 tests;
   `RawKeyTransition(int VirtualKey, int ScanCode, bool IsDown, bool IsInjected, bool IsRepeat)`
   and `BeginRawCapture(Action<RawKeyTransition>)` verified against source
   on 2026-07-28.
5. **Stage-2 revision (2026-07-28, load-bearing validation ledger at
   `.worktrees/.the-usual-logs/injection-pacing-optimization/load-bearing-ledger.md`):**
   two falsified assumptions fixed in place. (A7) The chunker emits 9-unit
   surrogate-straddle chunks (pinned by `InjectionChunkerTests`), so a fixed
   14 ms period allowed a sustained ~643 units/s counterexample — fixed with
   per-chunk scaled periods (`PeriodMsForChunk`, `PauseForNextChunk(int)`),
   covered by 3 new/updated tests in Tasks 1–2. (A1) "Waitable timer never
   fires early" is not contractual and the `Thread.Sleep` fail-safe is
   documented to possibly undershoot sub-resolution waits — all "only ever
   overshoots"/unqualified-"provably" wording replaced with the explicit
   premises (delay-mode error + 0.67–1 ms per-period margins + the gate's
   5 ms probe as the timer-path check). Re-checked after editing: type
   consistency across Tasks 1–3 holds (`PauseForNextChunk(int)` produced in
   Task 1, consumed in Task 2; `PeriodMsForChunk` produced in Task 2 Step 3c,
   consumed by the Step 3d call site; the Task 3 sentinel is unaffected —
   all-`'a'` text yields 8-unit chunks and unchanged 14 ms periods); every
   new test carries complete code and expected outcomes; no placeholders; no
   silent deferrals (the scaled-period behavior lands in production code in
   Task 2 and is pinned on Linux with a frozen clock). Verified findings
   recorded: render-rate measurements (9671a84) added to Measured Facts;
   Task 4 findings gained item (d) (stage-2 verification + accepted residual
   A4r with the `dwExtraInfo` fallback design).
