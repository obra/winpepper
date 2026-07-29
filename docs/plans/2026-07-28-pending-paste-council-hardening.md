# Pending-Paste Council Hardening Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the discard-on-new-dictation trapdoor (parked text is now retained
and appended, never silently dropped) and close the compound hwnd==0 blind spot
(a 0 foreground hwnd at injection start or mid-paste now parks the full text
instead of blind-injecting, with a log line + counter for every observation).

**Architecture:** Change B lives in the pure, Linux-testable injection
primitives of `Winpepper.Platform/Injection` (a new `NoForeground` run outcome,
an at-start park in `TextInjector`, a fail-safe `MidPasteDecider`, and a small
occurrence meter). Change A lives in `Winpepper.Core` (the `PendingPasteState`
slot gains append-not-replace semantics and carries its reason; the
`SessionViewModel` stops discarding on new dictation and restores the pending
pill when the engine returns to idle). `PipelineHost` (Windows-only, untestable
on Linux — the repo has zero PipelineHost tests by established pattern) gets
only thin wiring: two log-line updates and one new outcome arm in each of its
three existing outcome chains.

**Tech Stack:** C# / .NET 9 (`net9.0` + `net9.0-windows10.0.19041.0` TFMs),
xUnit v3 + Shouldly (hand-rolled fakes, no mocking library), MEL `ILogger<T>`
logging, WinUI (App layer).

## Global Constraints

- **Pre-commit gate (AGENTS.md):** run `./scripts/linux-tests.sh` from the
  worktree root before EVERY commit; it must exit 0 and print
  `LINUX SUITE: GREEN`.
- **Pre-push gate (AGENTS.md):** run `./scripts/windows-gate.sh` from WSL
  before any push; it must exit 0 and print `GATE: GREEN`. Budget 20–40 min.
- **Known out-of-scope gate results:** `Winpepper.Cleanup.Tests` model-eval
  cases listed in `tests/Winpepper.Cleanup.Tests/CleanupEvalCases.cs:95`
  (`KnownFailingBaseline`) surface as **skips** labeled
  `KNOWN-FAILING baseline (<model>/<case>)`: Slot0
  `qwen2.5-0.5b-instruct-q4_k_m` × {trap-joke-request, trap-email-help,
  trap-repeat-request, corr-recipient-scratch, trap-poem-request} and Slot3
  `sotto-cleanup-lfm25-350m-q8_0` × {corr-recipient-scratch,
  corr-deadline-nevermind, corr-registry-example, filler-uh-sortof}. Those
  skips (and ONLY those, owned by the parallel cleanup-bakeoff stream) are
  acceptable; every actual failure outside that set must be fixed or the run
  is red. The Windows gate can also fail transiently with UNC
  `retry should be performed` I/O errors when other builds run concurrently —
  re-run in a quiet window; do not chase as a code failure.
- **Test runner:** build test projects with `-c Release` and run via the xUnit
  v3 in-process runner (`dotnet exec <built dll>`), never `dotnet test`.
- **Pill copy budget:** ~32 characters max at FontSize 13 in the fixed 300-DIP
  pill (ledger A8, `docs/plans/2026-07-27-paste-path-hardening.md:144-148`);
  longer copy silently ellipsizes. Current copies: `Click to paste` (14),
  `Admin window - switch & click` (29). This plan introduces NO new copy.
- **Full-text park semantics:** the pending slot always holds the COMPLETE
  text (never a remainder). With append, it holds the complete concatenation.
- **No clipboard use anywhere. No toast and no ErrorBus report for normal
  park flows** (parks are surfaced by the pill only).
- **Per-chunk halt predicate structure preserved:** the
  physical-input-down check, its ordering before the foreground check, and
  the pacing-between-chunks-only rule in `GuardedInjectionRun` are untouched;
  this plan refines ONLY the hwnd==0 arm.
- **`PipelineHost.cs` is large and heavily edited — follow its existing
  patterns exactly** (fully-qualified type names, comment style, mirrored
  hold/toggle arms).
- **Deliberate constraint supersession (owner-approved, council-mandated):**
  the prior pins `hwnd 0 => inject, fail-open` (decider tests) and
  `a new dictation discards any pending paste` (Rule 5 of the 2026-07-21
  pending-paste plan) are deliberately revised by this plan. Retuned tests
  must say so in their comments.

---

## Evidence: the load-bearing hwnd==0 probe (2026-07-28)

The council's at-start polarity (park on hwnd==0) was 5-1 with a dissent
claiming at-start zeros are common benign transients right after a user click.
The spec required measuring before shipping. Probe: tight-loop polling of
`GetForegroundWindow()` on the real Windows host via `powershell.exe` interop
from WSL (script and raw output preserved at
`.worktrees/.the-usual-logs/pending-paste-council-hardening/fg-probe.ps1` and
`fg-probe-output*.txt`; two independent runs).

| Scenario | Run 1 | Run 2 |
|---|---|---|
| (a) At rest, 5 s tight poll | **0 zeros** / 106,774,079 samples | **0 zeros** / 103,475,180 samples |
| (b) Rapid Alt-Tab cycling (10 cycles), 4 s poll | 27 zero-runs, max **3.69 ms** each | 30 zero-runs, max 1.21 ms |
| (c1) Programmatic `SetForegroundWindow` flips (12), 4 s poll | 12 zero-runs, max 1.45 ms | 10 zero-runs, max 0.50 ms |
| (c2) Real mouse clicks changing focus (12), 4.5 s poll | inconclusive (windows stacked; focus never changed) | 12 zero-runs, max **1.64 ms** |

**Interpretation:** `GetForegroundWindow()==0` occurs ONLY inside 0.3–3.7 ms
bursts during a focus transition, and NEVER at rest. In the benign
click-then-paste flow, injection starts hundreds of milliseconds to seconds
after the click — far outside the ≤4 ms transient window — so a spurious
at-start park requires injection start to coincide with a focus flip within
single-digit milliseconds. **At-start 0-readings are rare in benign flows ⇒
ship the council majority polarity: PARK at start on hwnd==0.** The probe also
confirms the mid-stream hazard: every focus change during the ~0.8 s paced
send produces 0-readings the per-chunk guard can observe, which is exactly
the moment the unanimous mid-stream halt protects. The occurrence counter +
log lines (Task 1/2/3) exist so this polarity can be re-evaluated with field
data.

## Design decisions (Change A shape selection)

The council constraint, verbatim: **"preserve/append or fail loud — never
silently drop."** Candidate shapes evaluated:

- **(i) append/merge — CHOSEN.** A dictation that parks while a park is held
  APPENDS to the slot; a new dictation never discards the slot. Fully
  preserves text (strictly stronger than "fail loud"), needs no new pill
  copy (stays inside the 32-char budget by not changing copy at all), no
  toast/ErrorBus, and reuses the existing pill affordance: one click pastes
  everything, oldest first.
- (ii) replace-but-loud: still destroys text (history-fallback, which the
  council already ruled insufficient), and a "loud" surface would need either
  a new pill flash mechanism (none exists — `PillAnimationMode` has only
  `VoiceLevel`/`Thinking`/`None`) or toast/ErrorBus in a normal flow
  (forbidden). Rejected.
- (iii) block/queue: blocking a new dictation because old text is parked
  punishes the recovery gesture even harder than the trapdoor. Rejected.

Refinement of (i): when the new dictation's injection SUCCEEDS, the new text
pastes live as today and the old park is simply retained (pill returns to its
pending state afterward). Appending happens only when the new dictation ALSO
parks. This keeps live paste working while parked and still guarantees no
text is ever dropped.

Consequences accepted and documented:
- **Separator = single space**, not newline: injected text is typed as
  keystrokes and Enter submits in many chat inputs — a newline could fire a
  half-composed message. Dictation cleanup already ends utterances with
  punctuation, so space-joined segments read naturally.
- **A park can now only be cleared by pasting it** (or app exit; cancel
  already preserved it). No silent-drop path remains, which is the point.
- **Transient EVENT errors are recorded but not pill-flashed while a park is
  held AT IDLE** (the VM's `HasPending` presentation guards, now scoped): the
  clickable park affordance outranks a 6-second error flash while the user is
  in the pill-click retry loop. CORRECTION (fresh-eyes delta review,
  2026-07-28): the original claim here — "pre-existing behavior, now merely
  longer-lived" — was wrong. Retention made the unconditional guards NEWLY
  reachable mid-dictation, silently dropping the failure of any dictation
  started over a held park. Fixed: `OnBusReport`'s guard is idle-scoped,
  `NotifyError`'s guard is removed, and the error self-clear resync restores
  the PENDING pill (reason-correct copy) instead of stranding `Stage=Error`.
- **Merged pastes take proportionally longer, accepted** (load-bearing
  ledger A16, 2026-07-28): paste duration scales linearly at ~1.75 ms/char
  (`ChunkCodeUnits=8`, `TargetFeedUnitsPerSecond=600` ⇒ 14 ms inter-chunk
  pause), so a typical two-dictation merge ≈ 1.6 s. A mid-paste interrupt of
  a merged text re-parks the FULL concatenation and a retry retypes the
  already-delivered prefix — the same pre-existing full-text-park behavior
  as single pastes, only linearly larger; the failure mode is duplicated
  visible text (recoverable), never loss. A segment-queue/delivered-offset
  model was considered and rejected: it redesigns the slot and pill
  semantics and violates the owner-approved full-text park constraint. The
  meter + log field data allow this call to be revisited.

## File structure

| File | Change | Responsibility |
|---|---|---|
| `src/Winpepper.Platform/Injection/HwndZeroMeter.cs` | Create | Thread-safe at-start/mid-stream hwnd==0 occurrence counter |
| `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs` | Modify | New `NoForeground` outcome |
| `src/Winpepper.Platform/Injection/TextInjector.cs` | Modify | At-start hwnd==0 park; meter + log wiring (at-start and mid-stream) |
| `src/Winpepper.Platform/Injection/MidPasteDecider.cs` | Modify | hwnd==0 (either side) ⇒ Halt (fail-safe) |
| `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` | Modify | Per-chunk hwnd read into a local + `onZeroForeground` observation callback |
| `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs` | Modify | hwnd==0 ⇒ Park (defense in depth); doc comments distinguish the two fail policies |
| `src/Winpepper.Core/Pending/PendingPasteState.cs` | Modify | `HoldOrAppend` (append-not-replace) + `Reason` property |
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` | Modify | No discard on Recording; Idle arm restores pending pill; `EnterPendingPaste` appends |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | Retained-park log lines (both arms); `NoForeground` arm in all 3 outcome chains |
| `tests/Winpepper.Platform.Tests/Injection/HwndZeroMeterTests.cs` | Create | Meter behavior |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` | Modify | Retune hwnd-0 pin; new at-start park + meter + off-Windows tests |
| `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs` | Modify | Retune both hwnd-0 pins to Halt |
| `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs` | Modify | Retune baseline-0 pin; new live-baseline probe-goes-0 + callback tests |
| `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs` | Modify | Retune hwnd-0 pin to Park |
| `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs` | Modify | Retune replace pin to append; reason tests |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs` | Modify | Retune discard pin to retention; append, restore, copy-budget tests |

All Platform/Core changes are Linux-TDD-able. `Winpepper.Platform.Tests` has
`InternalsVisibleTo` from `Winpepper.Platform` (existing tests already read
`TextInjector.ChunkCodeUnits`), so `internal` members are test-visible.

All commands below run from the worktree root:
`/home/dan/code/winpepper/.worktrees/pending-paste-council-hardening`

---

### Task 1: `HwndZeroMeter` — the occurrence counter

**Files:**
- Create: `src/Winpepper.Platform/Injection/HwndZeroMeter.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/HwndZeroMeterTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public sealed class HwndZeroMeter` in namespace
  `Winpepper.Platform.Injection` with `long AtStartCount { get; }`,
  `long MidStreamCount { get; }`, `long RecordAtStart()`,
  `long RecordMidStream()` (each `Record*` returns the new running count).
  Task 2 and Task 3 wire it into `TextInjector`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Platform.Tests/Injection/HwndZeroMeterTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class HwndZeroMeterTests
{
    [Fact]
    public void Fresh_Meter_HasZeroCounts()
    {
        var meter = new HwndZeroMeter();
        meter.AtStartCount.ShouldBe(0);
        meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void RecordAtStart_IncrementsOnlyAtStart_AndReturnsRunningCount()
    {
        var meter = new HwndZeroMeter();
        meter.RecordAtStart().ShouldBe(1);
        meter.RecordAtStart().ShouldBe(2);
        meter.AtStartCount.ShouldBe(2);
        meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void RecordMidStream_IncrementsOnlyMidStream_AndReturnsRunningCount()
    {
        var meter = new HwndZeroMeter();
        meter.RecordMidStream().ShouldBe(1);
        meter.MidStreamCount.ShouldBe(1);
        meter.AtStartCount.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
```
Expected: BUILD FAILS with `CS0246: The type or namespace name 'HwndZeroMeter' could not be found` (a compile failure IS the red state for a new type).

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Platform/Injection/HwndZeroMeter.cs`:

```csharp
using System.Threading;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Process-lifetime occurrence counter for GetForegroundWindow() == 0
/// observations on the injection path, kept separately for the at-start
/// pre-check and the mid-stream per-chunk guard. Exists so the park-on-0
/// polarity (council majority 5-1, probe-gated 2026-07-28: 0-readings occur
/// only in 0.3-3.7 ms bursts during focus transitions, never at rest) can be
/// re-evaluated with field data -- every increment is paired with a log line
/// carrying the running count. Thread-safe: hotkey-arm injections and
/// UI-thread pill-click retries can race.
/// </summary>
public sealed class HwndZeroMeter
{
    private long _atStart;
    private long _midStream;

    /// <summary>Observations of hwnd == 0 at injection start.</summary>
    public long AtStartCount => Interlocked.Read(ref _atStart);

    /// <summary>Observations of hwnd == 0 by the per-chunk mid-paste guard.</summary>
    public long MidStreamCount => Interlocked.Read(ref _midStream);

    /// <summary>Record an at-start observation; returns the new running count.</summary>
    public long RecordAtStart() => Interlocked.Increment(ref _atStart);

    /// <summary>Record a mid-stream observation; returns the new running count.</summary>
    public long RecordMidStream() => Interlocked.Increment(ref _midStream);
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.HwndZeroMeterTests
```
Expected: 3 passed, 0 failed.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: exit 0, prints `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/HwndZeroMeter.cs tests/Winpepper.Platform.Tests/Injection/HwndZeroMeterTests.cs
git commit -m "feat(platform): hwnd==0 occurrence meter for injection-path field data"
```

---

### Task 2: Park at injection start when the foreground hwnd is 0

**Files:**
- Modify: `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs` (enum, members end near line 30)
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (`TryInjectGuarded`, ~line 147; `DefaultForegroundProbe` doc, ~line 104)
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: `HwndZeroMeter` from Task 1.
- Produces: `InjectionRunOutcome.NoForeground` (new enum member — Task 7's
  PipelineHost arms depend on this exact name) and
  `internal HwndZeroMeter Meter { get; }` on `TextInjector` (Task 3's
  mid-stream tests read `Meter.MidStreamCount`; tests here read
  `Meter.AtStartCount`).

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`,
REPLACE the method `Guarded_UnknownBaseline_FailOpen_SendsEverything`
(currently at lines 84–96, pinning `foregroundHwnd: () => 0` ⇒ `Completed`)
with the following, and ADD the two new methods after it. The existing
`NewInjector(Func<long> foregroundHwnd, Func<string, bool> sendChunk)` helper
at the top of the file is reused as-is.

```csharp
    [Fact]
    public void Guarded_ZeroForegroundAtStart_Parks_NothingSent_NoWaits()
    {
        // DELIBERATE PIN REVISION (council 5-1, probe-gated 2026-07-28,
        // supersedes the paste-path-hardening fail-open pin): a 0 foreground
        // hwnd at send start means the foreground is unobservable at exactly
        // the moment we are about to type -- blind-injecting can silently
        // lose the whole text, while a park is a visible one-click detour.
        // Probe evidence: 0-readings never occur at rest; they occur only in
        // 0.3-3.7 ms bursts during focus transitions. The park must land
        // BEFORE the modifier/mouse release-wait preludes (no sleeps) and
        // before any send.
        var sent = new List<string>();
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => true,           // everything held: proves no prelude ran
            foregroundHwnd: () => 0,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add);

        injector.TryInjectGuarded(new string('a', 80))
            .ShouldBe(InjectionRunOutcome.NoForeground);

        sent.ShouldBeEmpty();
        sleeps.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_ZeroForegroundAtStart_CountsAtStartOccurrences()
    {
        var injector = NewInjector(() => 0, c => true);

        injector.TryInjectGuarded("abc");
        injector.TryInjectGuarded("def");

        injector.Meter.AtStartCount.ShouldBe(2);
        injector.Meter.MidStreamCount.ShouldBe(0);
    }

    [Fact]
    public void Guarded_DefaultForegroundProbe_OffWindows_ParksAtStart()
    {
        // Off-Windows-only pin: on the Windows gate's interactive desktop the
        // real DefaultForegroundProbe returns a live nonzero hwnd, so the run
        // would proceed (Completed / BlockedElevated) and the assertion below
        // would fail. Same guard pattern as
        // ElevationProbeTests.Probe_OffWindows_ReturnsUnknown_FailOpen.
        if (OperatingSystem.IsWindows()) return;

        // The production default probe returns 0 unconditionally off-Windows
        // (TextInjector.DefaultForegroundProbe). Under the new fail-safe
        // polarity an unseamed injector therefore PARKS off-Windows instead
        // of injecting blind -- pinned deliberately so the off-Windows
        // default flip is a documented decision, not an accident. Production
        // is Windows-only; every Linux test that wants a send seams
        // foregroundHwnd explicitly.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });

        injector.TryInjectGuarded("abc").ShouldBe(InjectionRunOutcome.NoForeground);
        sent.ShouldBeEmpty();
    }
```

Note: if any OTHER test in this file constructs an injector with
`foregroundHwnd: () => 0` and expects a send, retune it the same way — at
HEAD only the replaced method does.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
```
Expected: BUILD FAILS with `CS0117: 'InjectionRunOutcome' does not contain a definition for 'NoForeground'` (and `'TextInjector' does not contain a definition for 'Meter'`).

- [ ] **Step 3: Implement**

3a. In `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs`, add a new
member after `BlockedElevated` (keep the existing members and their docs
untouched):

```csharp
    /// <summary>
    /// GetForegroundWindow() returned 0 at send start: the foreground was
    /// unobservable at exactly the moment we were about to type. NOTHING was
    /// typed -- not even the modifier-neutralizing KEYUPs. FAIL-SAFE, the
    /// deliberate opposite of the probe/elevation fail-open bias
    /// (ForegroundElevation.Unknown => inject): probe evidence
    /// (pending-paste-council-hardening, 2026-07-28) shows hwnd==0 occurs
    /// ONLY during focus transitions -- exactly the dangerous moment -- and a
    /// park is a visible one-click detour while a blind inject can be
    /// invisible, unrecoverable loss. The caller must park the FULL text as
    /// a pending paste with the default "Click to paste" copy. Not an error
    /// (no ErrorBus) -- the pill is the surface.
    /// </summary>
    NoForeground,
```

3b. In `src/Winpepper.Platform/Injection/TextInjector.cs`:

Add the meter property next to the other instance fields (after
`_foregroundElevation`, ~line 82):

```csharp
    /// <summary>
    /// hwnd==0 occurrence counts (at-start vs mid-stream), for field
    /// re-evaluation of the park-on-0 polarity. Internal for tests.
    /// </summary>
    internal HwndZeroMeter Meter { get; } = new();
```

In `TryInjectGuarded`, insert the at-start check immediately after the
`hwndAtSendStart` capture and BEFORE the `ElevatedTargetDecider` pre-check
(the capture is `var hwndAtSendStart = _foregroundHwnd();` at ~line 151):

```csharp
        var hwndAtSendStart = _foregroundHwnd();
        // No observable foreground at send start (council majority polarity,
        // probe-gated 2026-07-28): park the FULL text instead of typing into
        // an unknown window. Fail-SAFE -- deliberately opposite to the
        // probe/elevation fail-open below, see InjectionRunOutcome.NoForeground.
        if (hwndAtSendStart == 0)
        {
            var atStartZeroCount = Meter.RecordAtStart();
            _log.LogWarning(
                "Foreground hwnd is 0 at injection start (occurrence #{Count}); not typing -- parking the full text ({Chars} chars)",
                atStartZeroCount, text.Length);
            return InjectionRunOutcome.NoForeground;
        }
```

3c. Update the `DefaultForegroundProbe` doc comment (~line 104) to document
the off-Windows consequence:

```csharp
    /// <summary>
    /// Foreground HWND as Int64; 0 when unknown (non-Windows, or the call
    /// fails). NOTE: since the park-on-0 polarity (2026-07-28) an unseamed
    /// TryInjectGuarded returns NoForeground (parks) whenever this yields 0
    /// -- including unconditionally off-Windows. Fail-safe by design;
    /// production injection is Windows-only.
    /// </summary>
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests
```
Expected: all tests in the class pass (the three new/replaced ones included), 0 failed.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/InjectionRunOutcome.cs src/Winpepper.Platform/Injection/TextInjector.cs tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(platform): park full text when foreground hwnd is 0 at injection start (probe-gated polarity)"
```

---

### Task 3: Halt mid-paste on hwnd==0, with observation callback

**Files:**
- Modify: `src/Winpepper.Platform/Injection/MidPasteDecider.cs` (31-line file, rewritten below)
- Modify: `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` (per-chunk loop, ~lines 24–53)
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (the `GuardedInjectionRun.Execute` call, ~line 180)
- Test: `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: `HwndZeroMeter` (Task 1) via `TextInjector.Meter` (Task 2);
  `InjectionRunOutcome.Interrupted` (existing — PipelineHost already parks
  full text on it, so mid-stream needs NO PipelineHost change).
- Produces: `MidPasteDecider.Decide(long, long)` returning `Halt` when either
  handle is 0; `GuardedInjectionRun.Execute(..., Action? onZeroForeground = null)`
  — a new trailing optional parameter invoked once per chunk-check that
  observes `currentForegroundHwnd() == 0` (at most once per run, since the
  same check then halts).

- [ ] **Step 1: Write the failing tests**

1a. In `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs`,
REPLACE the two fail-open pins `UnknownBaseline_Continues_FailOpen` (lines
23–29) and `UnknownCurrent_Continues_FailOpen` (lines 31–38) with:

```csharp
    [Fact]
    public void ZeroBaseline_Halts_FailSafe()
    {
        // DELIBERATE PIN REVISION (council, probe-gated 2026-07-28,
        // supersedes the midpaste-focus-fallback fail-open pin): with no
        // baseline we cannot know the foreground is still the user's chosen
        // target, and typing blind can silently lose text. Halt parks the
        // FULL text. In production a 0 baseline no longer reaches this
        // decider (TextInjector parks at start first) -- this arm is the
        // fail-safe default for any direct caller.
        MidPasteDecider.Decide(hwndAtSendStart: 0, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Halt);
    }

    [Fact]
    public void ZeroCurrent_Halts_FailSafe()
    {
        // The per-chunk probe read 0 mid-run (focus transition, lock screen,
        // secure desktop / UAC prompt): exactly the dangerous moment
        // (unanimous council finding). Stop typing; the caller parks the
        // FULL text and one pill click recovers it.
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 0)
            .ShouldBe(MidPasteDecision.Halt);
    }
```

1b. In `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`,
REPLACE `FailOpen_UnknownBaseline_SendsEverything` (lines 82–96) with the
first test below and ADD the second and third:

```csharp
    [Fact]
    public void ZeroBaseline_Halts_NothingSent()
    {
        // DELIBERATE PIN REVISION (2026-07-28): baseline 0 used to disable
        // the guard (fail-open); it now halts before the first chunk
        // (fail-safe) so nothing is typed into an unverifiable foreground.
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb" },
            hwndAtSendStart: 0,
            currentForegroundHwnd: () => 99,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBeEmpty();
    }

    [Fact]
    public void ProbeGoesToZero_MidRun_Halts_AfterPrefixOnly()
    {
        // Live baseline, per-chunk probe returns 0 from chunk 2 on: the run
        // must stop with only a strict prefix sent (the caller parks the
        // FULL text). Closes the coverage gap where hwnd-going-to-0 mid-run
        // was pinned only at the decider unit level.
        var sent = new List<string>();
        var probes = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => ++probes == 1 ? 42L : 0L,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBe(new[] { "aa" });
    }

    [Fact]
    public void ProbeGoesToZero_MidRun_InvokesZeroObserver_ExactlyOnce()
    {
        var zeroObservations = 0;
        var probes = 0;
        GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => ++probes == 1 ? 42L : 0L,
            sendChunk: c => true,
            onZeroForeground: () => zeroObservations++);

        zeroObservations.ShouldBe(1); // the observing check halts the run
    }
```

1c. In `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`,
ADD (end-to-end pin that the injector wires the mid-stream meter):

```csharp
    [Fact]
    public void Guarded_ProbeGoesToZero_MidSend_Interrupts_AndCountsMidStream()
    {
        var sent = new List<string>();
        var probes = 0;
        var injector = NewInjector(
            () => ++probes == 1 ? 42L : 0L,   // start check sees 42; per-chunk sees 0
            c => { sent.Add(c); return true; });
        var text = new string('a', 24);       // 3 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        string.Concat(sent).Length.ShouldBeLessThan(text.Length);
        injector.Meter.MidStreamCount.ShouldBe(1);
        injector.Meter.AtStartCount.ShouldBe(0);
    }
```

(Probe-call accounting: call 1 is `TryInjectGuarded`'s own at-start capture
returning 42; every per-chunk read returns 0, so the chunk-1 guard halts with
nothing sent — `sent` may be empty, which satisfies the strict-less-than
assertion.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
```
Expected: BUILD FAILS on the `onZeroForeground:` named argument
(`CS1739: The best overload for 'Execute' does not have a parameter named 'onZeroForeground'`).
Fix nothing yet — the decider tests would fail at runtime too
(`Decide(0,99)` still returns `Continue`).

- [ ] **Step 3: Implement**

3a. Rewrite `src/Winpepper.Platform/Injection/MidPasteDecider.cs` (entire
file):

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>Per-chunk continue/halt outcome while a paste is in flight.</summary>
public enum MidPasteDecision
{
    /// <summary>Same window positively still foreground: keep typing.</summary>
    Continue,

    /// <summary>
    /// Foreground moved to a different window, or the foreground is
    /// unobservable (either handle 0): stop typing.
    /// </summary>
    Halt,
}

/// <summary>
/// Pure mid-paste decision: is the window we started typing into still the
/// foreground window? Continue is chosen ONLY when both handles are known
/// AND equal. hwnd==0 on either side halts -- FAIL-SAFE, deliberately the
/// OPPOSITE bias from probe/elevation observation failures
/// (ForegroundElevation.Unknown => inject, unchanged): GetForegroundWindow()
/// returning 0 correlates with exactly the dangerous moment. Probe evidence
/// (pending-paste-council-hardening, 2026-07-28): 0-readings occur only in
/// 0.3-3.7 ms bursts during focus transitions and never at rest, and the
/// ~0.8 s paced send window makes catching one mid-run realistic. A halt
/// parks the FULL text (visible one-click recovery); typing into an unknown
/// foreground can silently lose it. Supersedes the 2026-07-26
/// midpaste-focus-fallback fail-open pins (owner-approved). Compares raw
/// HWNDs (not UIA element identity) because this runs between every send
/// chunk and must stay cheap.
/// </summary>
public static class MidPasteDecider
{
    public static MidPasteDecision Decide(long hwndAtSendStart, long hwndNow)
    {
        if (hwndAtSendStart == 0 || hwndNow == 0) return MidPasteDecision.Halt;
        return hwndNow == hwndAtSendStart
            ? MidPasteDecision.Continue
            : MidPasteDecision.Halt;
    }
}
```

3b. In `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`, add the
trailing optional parameter and store the per-chunk read in a local. The
signature becomes:

```csharp
    public static InjectionRunOutcome Execute(
        IReadOnlyList<string> chunks,
        long hwndAtSendStart,
        Func<long> currentForegroundHwnd,
        Func<string, bool> sendChunk,
        Func<bool>? physicalInputDown = null,
        Action? pauseBetweenChunks = null,
        Action? onZeroForeground = null)
```

and the foreground check inside the loop (currently
`if (MidPasteDecider.Decide(hwndAtSendStart, currentForegroundHwnd()) == MidPasteDecision.Halt)`)
becomes:

```csharp
            var hwndNow = currentForegroundHwnd();
            // Observation hook BEFORE the decision: a 0 read is exactly what
            // the park-on-0 polarity wants field data on (it also halts just
            // below, so this fires at most once per run).
            if (hwndNow == 0) onZeroForeground?.Invoke();
            if (MidPasteDecider.Decide(hwndAtSendStart, hwndNow)
                == MidPasteDecision.Halt)
            {
                return InjectionRunOutcome.Interrupted;
            }
```

The physical-input check, its position before the foreground check, the
`if (i > 0) pauseBetweenChunks?.Invoke();` pacing rule, and the send-failure
arm are NOT touched. Extend the class doc comment's check list sentence with:
"…asks <see cref="MidPasteDecider"/> whether the window we started typing
into is still positively foreground (a 0 read halts fail-safe and is reported
via <c>onZeroForeground</c> for field counting)."

3c. In `src/Winpepper.Platform/Injection/TextInjector.cs`, extend the
`GuardedInjectionRun.Execute(...)` call (~line 180) with the new argument
after `pauseBetweenChunks:`:

```csharp
            pauseBetweenChunks: () => _sleep(InterChunkPauseMs),
            onZeroForeground: () =>
            {
                var midStreamZeroCount = Meter.RecordMidStream();
                _log.LogWarning(
                    "Foreground hwnd read 0 mid-paste (occurrence #{Count}); halting -- the full text will be parked",
                    midStreamZeroCount);
            });
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.MidPasteDeciderTests
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.GuardedInjectionRunTests
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests
```
Expected: all pass, 0 failed in each class.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/MidPasteDecider.cs src/Winpepper.Platform/Injection/GuardedInjectionRun.cs src/Winpepper.Platform/Injection/TextInjector.cs tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(platform): halt mid-paste on hwnd==0 (fail-safe) and count/log every observation"
```

---

### Task 4: `ElevatedTargetDecider` — hwnd==0 parks (defense in depth)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs` (line 53 arm + class doc, lines 37–48)
- Test: `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs`

**Interfaces:**
- Consumes: nothing new. In production this arm is unreachable —
  `TextInjector.TryInjectGuarded` returns `NoForeground` (Task 2) before
  consulting this decider — it exists so no future caller can blind-inject
  on a 0 hwnd.
- Produces: `ElevatedTargetDecider.Decide(0, *) == ElevatedTargetDecision.Park`.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs`,
REPLACE `UnknownHwnd_Injects_FailOpen_EvenIfProbeClaimsElevated` (lines
36–44) with:

```csharp
    [Fact]
    public void ZeroHwnd_Parks_FailSafe_RegardlessOfProbeResult()
    {
        // DELIBERATE PIN REVISION (council 5-1, probe-gated 2026-07-28,
        // supersedes the paste-path-hardening fail-open pin): an absent
        // foreground hwnd now PARKS instead of blind-injecting. Normally
        // unreachable -- TextInjector returns NoForeground before consulting
        // this decider -- kept as defense in depth. Contrast the UNCHANGED
        // fail-open next door: a KNOWN hwnd with an unobservable elevation
        // probe still injects (KnownHwnd_UnknownElevation_Injects_FailOpen).
        ElevatedTargetDecider.Decide(hwndAtSendStart: 0, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Park);
    }
```

The three other tests in the file (`KnownHwnd_Elevated_Parks`,
`KnownHwnd_NotElevated_Injects`, `KnownHwnd_UnknownElevation_Injects_FailOpen`)
stay exactly as they are — they pin the UNCHANGED half of the policy.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.ElevatedTargetDeciderTests
```
Expected: 1 failed — `ZeroHwnd_Parks_FailSafe_RegardlessOfProbeResult`,
Shouldly message `should be Park but was Inject`.

- [ ] **Step 3: Implement**

In `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs`, change line 53
from
`if (hwndAtSendStart == 0) return ElevatedTargetDecision.Inject; // foreground unobservable: fail open`
to:

```csharp
        if (hwndAtSendStart == 0) return ElevatedTargetDecision.Park; // foreground ABSENT: fail safe (see class doc)
```

and REPLACE the class-level doc comment (lines 37–48, the block starting
"Pure pre-injection decision…") with:

```csharp
/// <summary>
/// Pure pre-injection decision: is the window we are about to type into an
/// elevated (higher-integrity) process? Windows UIPI silently drops SendInput
/// to elevated windows while reporting success (MSDN: "neither GetLastError
/// nor the return value will indicate the failure was caused by UIPI
/// blocking"), so injecting would consume the text with nothing delivered.
/// Two DISTINCT failure policies (council, 2026-07-28):
/// - Probe/elevation unobservable (hwnd known, ForegroundElevation.Unknown):
///   INJECT -- unchanged fail-open; a transient probe failure must not
///   regress the common path. Same bias as PendingPasteDecider.
/// - Foreground hwnd ABSENT (0): PARK -- fail-safe; there is no window to
///   verify anything against and hwnd==0 correlates with focus transitions
///   (probe evidence, 2026-07-28). Normally unreachable: TextInjector
///   returns NoForeground before consulting this decider; this arm is
///   defense in depth for any other caller.
/// </summary>
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.ElevatedTargetDeciderTests
```
(after rebuilding: `dotnet build tests/Winpepper.Platform.Tests -c Release -f net9.0`)
Expected: 4 passed, 0 failed.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs
git commit -m "feat(platform): elevated-target decider parks on absent foreground hwnd (defense in depth)"
```

---

### Task 5: `PendingPasteState` — append, never silently replace; carry the reason

**Files:**
- Modify: `src/Winpepper.Core/Pending/PendingPasteState.cs` (45-line file, rewritten below)
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (`EnterPendingPaste` body only, ~line 161)
- Test: `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs`

**Interfaces:**
- Consumes: existing `InjectionTarget`, `PendingPasteReason` (both in
  `Winpepper.Core.Pending`).
- Produces: on `PendingPasteState` —
  `void HoldOrAppend(string text, InjectionTarget target, PendingPasteReason reason)`
  (REPLACES `SetPending(string, InjectionTarget)`; sole production caller is
  `SessionViewModel.EnterPendingPaste`, updated in this task) and
  `PendingPasteReason Reason { get; }`. Task 6's Idle-arm restore reads
  `Reason`. `HasPending`, `PendingText`, `Target`, `Discard()`,
  `OnPasteAttempted(bool)` keep their exact existing signatures.

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs`, REPLACE
`SetPending_HoldsTextAndTarget` (line 22) and `SetPending_ReplacesExisting`
(line 32) with the four tests below, and update the remaining tests'
`SetPending(...)` calls to
`HoldOrAppend(..., PendingPasteReason.Interrupted)` (the file's other tests —
`Fresh_HasNoPending`, `Discard_ClearsSlot`, `Discard_IsIdempotent`, and the
three `OnPasteAttempted_*` tests — keep their assertions unchanged). Use the
same target-construction helper style already in the file.

```csharp
    [Fact]
    public void HoldOrAppend_Fresh_HoldsTextTargetAndReason()
    {
        var state = new PendingPasteState();
        var target = new InjectionTarget { WindowHandle = 42, ElementId = "el" };

        state.HoldOrAppend("hello world", target, PendingPasteReason.ElevatedTarget);

        state.HasPending.ShouldBeTrue();
        state.PendingText.ShouldBe("hello world");
        state.Target.ShouldBe(target);
        state.Reason.ShouldBe(PendingPasteReason.ElevatedTarget);
    }

    [Fact]
    public void HoldOrAppend_Occupied_Appends_WithSpaceSeparator()
    {
        // DELIBERATE PIN REVISION (council 2026-07-28, "preserve/append or
        // fail loud -- never silently drop"; supersedes the 2026-07-21
        // pending-paste plan's replace semantics): an occupied slot APPENDS,
        // oldest first, so one pill click pastes everything. Separator is a
        // SPACE, not a newline -- injected text is typed as keystrokes and
        // Enter submits in many chat inputs.
        var state = new PendingPasteState();
        state.HoldOrAppend("first thought.", new InjectionTarget { WindowHandle = 1, ElementId = "a" },
            PendingPasteReason.ElevatedTarget);

        var newTarget = new InjectionTarget { WindowHandle = 2, ElementId = "b" };
        state.HoldOrAppend("second thought.", newTarget, PendingPasteReason.Interrupted);

        state.PendingText.ShouldBe("first thought. second thought.");
        state.Target.ShouldBe(newTarget);                       // latest context wins
        state.Reason.ShouldBe(PendingPasteReason.Interrupted);  // latest reason drives the copy
        state.HasPending.ShouldBeTrue();
    }

    [Fact]
    public void HoldOrAppend_Occupied_EmptyIncoming_KeepsExistingText()
    {
        var state = new PendingPasteState();
        state.HoldOrAppend("kept", new InjectionTarget { WindowHandle = 1, ElementId = "a" },
            PendingPasteReason.Interrupted);

        state.HoldOrAppend("", new InjectionTarget { WindowHandle = 2, ElementId = "b" },
            PendingPasteReason.Interrupted);

        state.PendingText.ShouldBe("kept"); // never degrade held text
        state.HasPending.ShouldBeTrue();
    }

    [Fact]
    public void Discard_ResetsReasonToDefault()
    {
        var state = new PendingPasteState();
        state.HoldOrAppend("t", new InjectionTarget { WindowHandle = 1, ElementId = "a" },
            PendingPasteReason.ElevatedTarget);

        state.Discard();

        state.Reason.ShouldBe(PendingPasteReason.Interrupted);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0
```
Expected: BUILD FAILS with `CS1061: 'PendingPasteState' does not contain a definition for 'HoldOrAppend'`.

- [ ] **Step 3: Implement**

3a. Rewrite `src/Winpepper.Core/Pending/PendingPasteState.cs` (entire file):

```csharp
namespace Winpepper.Core.Pending;

/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when the
/// paste could not be delivered (focus moved, halt gesture, elevated target,
/// no observable foreground). NEVER persisted to disk -- history archiving
/// is a separate, unchanged feature. Lifecycle:
/// None -> Pending(text,target,reason) [-> Pending(text + ' ' + more, ...)]*
/// -> consumed (successful pill-click paste) | app exit (memory-only).
/// A new dictation NEVER discards the slot and cancel preserves it (council
/// constraint, 2026-07-28: preserve/append or fail loud -- never silently
/// drop; supersedes Rule 5 of the 2026-07-21 pending-paste plan,
/// owner-approved). A dictation that parks while the slot is occupied
/// APPENDS, so one pill click pastes everything, oldest first, always the
/// COMPLETE text -- never a remainder.
/// </summary>
public sealed class PendingPasteState
{
    /// <summary>
    /// Separator between appended dictations. A space, not a newline:
    /// injected text is typed as keystrokes and Enter submits in many chat
    /// inputs -- a newline could fire a half-composed message.
    /// </summary>
    internal const string AppendSeparator = " ";

    public bool HasPending { get; private set; }
    public string PendingText { get; private set; } = string.Empty;
    public InjectionTarget Target { get; private set; } = InjectionTarget.Empty;

    /// <summary>Why the LATEST park happened -- selects the pill copy.</summary>
    public PendingPasteReason Reason { get; private set; } = PendingPasteReason.Interrupted;

    /// <summary>
    /// Hold text as pending. Empty slot: takes the text as-is. Occupied
    /// slot: APPENDS (never replaces -- no dictation is ever silently
    /// dropped). Target and Reason always track the LATEST park (freshest
    /// context for the pill copy).
    /// </summary>
    public void HoldOrAppend(string text, InjectionTarget target, PendingPasteReason reason)
    {
        ArgumentNullException.ThrowIfNull(target);
        var incoming = text ?? string.Empty;
        if (HasPending && PendingText.Length > 0 && incoming.Length > 0)
            PendingText = PendingText + AppendSeparator + incoming;
        else if (!HasPending || incoming.Length > 0)
            PendingText = incoming;
        // else: occupied slot + empty incoming -- keep the held text.
        Target = target;
        Reason = reason;
        HasPending = true;
    }

    /// <summary>Clear the slot (successful paste, or app exit). Idempotent.</summary>
    public void Discard()
    {
        HasPending = false;
        PendingText = string.Empty;
        Target = InjectionTarget.Empty;
        Reason = PendingPasteReason.Interrupted;
    }

    /// <summary>
    /// Apply the outcome of a pill-click paste attempt. On success the slot is
    /// consumed (cleared). On failure the slot is KEPT so the user can click
    /// again. Returns true when the slot was consumed.
    /// </summary>
    public bool OnPasteAttempted(bool injected)
    {
        if (!HasPending) return false;
        if (injected) { Discard(); return true; }
        return false;
    }
}
```

3b. In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`, update the ONE
production caller — the `EnterPendingPaste` body (~line 164) changes from
`_pending.SetPending(text, target);` to:

```csharp
        _pending.HoldOrAppend(text, target, reason);
```

(The rest of `EnterPendingPaste` — `Stage = SessionStage.PendingPaste;
StatusText = PendingStatusFor(reason);` — is unchanged, and so is the
`_ui.Post(...)` wrapper the whole body runs inside: the slot's thread safety
relies on every mutation being UI-marshaled, so change ONLY the `SetPending`
line and leave the wrapper intact. Behavior for a fresh slot is identical;
the append path only becomes reachable in Task 6 when the Recording-arm
discard is removed.)

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows" -class Winpepper.Core.Tests.Pending.PendingPasteStateTests
```
Expected: all tests in the class pass, 0 failed.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` (the VM discard behavior is untouched so far,
so `SessionViewModelPendingTests` still passes unmodified).

```bash
git add src/Winpepper.Core/Pending/PendingPasteState.cs src/Winpepper.Core/ViewModels/SessionViewModel.cs tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs
git commit -m "feat(core): pending-paste slot appends instead of silently replacing; carries the park reason"
```

---

### Task 6: `SessionViewModel` — retain parks across dictations, restore the pending pill

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs`
  (`OnEngineStateChanged`, ~lines 423–467)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`

**Interfaces:**
- Consumes: `PendingPasteState.Reason` and `HoldOrAppend` from Task 5;
  existing `PendingStatusFor(PendingPasteReason)` private helper.
- Produces: the user-visible Change A behavior — a new dictation retains the
  park; parks merge; the pending pill (stage + reason-correct copy) is
  restored whenever the engine returns to Idle with a held slot. Task 7's
  log-line change describes this behavior; no new public API.

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`,
REPLACE `NewDictation_DiscardsPending` (lines 50–59) with the first test
below and ADD the other three. Reuse the file's existing `NewVm()` and
`T(long, string)` helpers exactly as the surrounding tests do.

```csharp
    [Fact]
    public void NewDictation_RetainsPending()
    {
        // DELIBERATE PIN REVISION (council 2026-07-28, all 6 lenses:
        // "preserve/append or fail loud -- never silently drop"; supersedes
        // Rule 5 of the 2026-07-21 pending-paste plan, owner-approved). The
        // old trapdoor: pressing the pedal again -- the most natural recovery
        // gesture -- destroyed the very text the park saved.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"));

        engine.Apply(SessionEvent.StartRequested); // Recording

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("saved text");
        vm.Stage.ShouldBe(SessionStage.Recording); // dictation UX unchanged
    }

    [Fact]
    public void ParkSurvivesDictation_EngineIdle_RestoresPendingPillAndCopy()
    {
        // After the retained park's dictation finishes (here: cancelled --
        // CancelRequested drives the engine straight back to Idle), the pill
        // must return to the PENDING presentation with the reason-correct
        // copy, not linger on the last in-flight stage and not auto-hide.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"), PendingPasteReason.ElevatedTarget);
        engine.Apply(SessionEvent.StartRequested);

        engine.Apply(SessionEvent.CancelRequested); // engine -> Idle

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("saved text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Admin window - switch & click");
    }

    [Fact]
    public void SecondPark_Appends_AndOneClickPastesEverything()
    {
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("first thought.", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested);   // new dictation; park retained
        engine.Apply(SessionEvent.CancelRequested);  // back to Idle for clarity

        vm.EnterPendingPaste("second thought.", T(2, "b")); // this dictation parked too

        vm.PendingPasteText.ShouldBe("first thought. second thought.");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.NotifyPasteAttempted(injected: true).ShouldBeTrue(); // ONE click, everything
        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Theory]
    [InlineData(PendingPasteReason.Interrupted)]
    [InlineData(PendingPasteReason.ElevatedTarget)]
    public void PendingCopy_FitsThePillBudget(PendingPasteReason reason)
    {
        // ~32 chars max at FontSize 13 in the fixed 300-DIP pill (ledger A8,
        // docs/plans/2026-07-27-paste-path-hardening.md:144-148): anything
        // longer silently ellipsizes. Guards every current and future copy.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("t", T(1, "a"), reason);

        vm.StatusText.Length.ShouldBeLessThanOrEqualTo(32);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows" -class Winpepper.Core.Tests.ViewModels.SessionViewModelPendingTests
```
Expected: FAIL — `NewDictation_RetainsPending` (`HasPendingPaste should be
True but was False`), `ParkSurvivesDictation_...` and `SecondPark_...`
likewise; `PendingCopy_FitsThePillBudget` passes already (both copies fit) —
that is fine, it is a guard pin.

- [ ] **Step 3: Implement**

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`,
`OnEngineStateChanged` (~lines 423–467):

3a. In the `SessionState.Recording` arm, DELETE the line
`_pending.Discard(); // Rule 5: a new dictation discards any pending paste.`
and put this comment in its place:

```csharp
                case SessionState.Recording:
                    // A held park deliberately SURVIVES a new dictation
                    // (council 2026-07-28: preserve/append or fail loud --
                    // never silently drop; supersedes Rule 5 of the
                    // 2026-07-21 pending-paste plan, owner-approved). If this
                    // dictation also parks, EnterPendingPaste appends.
                    _stopwatch.Restart();
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
```

3b. REPLACE the `SessionState.Idle` arm (currently
`if (_pending.HasPending) break;` then the Stage/StatusText lines) with:

```csharp
                case SessionState.Idle:
                    _stopwatch.Stop();
                    // A held park survives dictations: returning to engine
                    // Idle with a held slot must RESTORE the PENDING pill
                    // (stage + reason-correct copy) -- not leave the last
                    // in-flight copy ("Inserting...") on screen and not
                    // auto-hide the pill.
                    if (_pending.HasPending)
                    {
                        Stage = SessionStage.PendingPaste;
                        StatusText = PendingStatusFor(_pending.Reason);
                        break;
                    }
                    Stage = SessionStage.Idle;
                    StatusText = "Ready";
                    break;
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows" -class Winpepper.Core.Tests.ViewModels.SessionViewModelPendingTests
```
Expected: all tests in the class pass (including the pre-existing
`EngineIdle_WhilePending_KeepsPendingStage`, which the new Idle arm satisfies
by re-asserting `PendingPaste` + the reason copy), 0 failed.

- [ ] **Step 5: Run the Linux suite and commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs
git commit -m "feat(core): retain parked paste across new dictations; append on double-park; restore pending pill on idle"
```

---

### Task 7: `PipelineHost` wiring — retained-park logging + `NoForeground` arms

`PipelineHost.cs` is `#if WINDOWS` with zero unit tests (established repo
pattern — no test project references `Winpepper.App`). All testable logic
already landed in Tasks 1–6; this task is thin wiring that the Windows gate
compiles and the Task 8 smoke checklist exercises. Follow the file's existing
patterns EXACTLY (fully-qualified type names, mirrored hold/toggle arms,
comment style).

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — `TryPastePending`
  (~lines 382–437), hold-arm start (~lines 445–448), hold-arm outcome chain
  (~lines 736–766), toggle-arm start (~lines 850–853), toggle-arm outcome
  chain (~lines 1134–1164). (Line refs audited against HEAD 2026-07-28:
  the three `TryInjectGuarded` calls sit at ~397, ~736, ~1134 and each
  chain's `injected` local is `outcome == ...Completed` on the next line.)

**Interfaces:**
- Consumes: `InjectionRunOutcome.NoForeground` (Task 2);
  `_vm.EnterPendingPaste(string, InjectionTarget, PendingPasteReason)` and
  `_vm.ShowPendingPasteStatus(PendingPasteReason)` (existing, semantics
  extended by Tasks 5–6); `_vm.PendingPasteText` (existing).
- Produces: nothing consumed by later tasks — this is the leaf wiring.

- [ ] **Step 1: Update the two dictation-start log sites (Change A)**

The hold arm currently reads (PipelineHost.cs ~445–447):

```csharp
                if (_vm.HasPendingPaste)
                    _log.LogInformation("Pending paste discarded unpasted");
```

Replace it (and the byte-identical block in the toggle arm, ~850–852) with:

```csharp
                if (_vm.HasPendingPaste)
                    _log.LogInformation(
                        "Pending paste retained across new dictation ({Chars} chars held; a park during this dictation will append)",
                        _vm.PendingPasteText.Length);
```

Both arms MUST be changed identically. The read still sits before
`_engine.Apply(SessionEvent.StartRequested)` (harmless now, but it keeps the
log truthful at the moment of the decision).

- [ ] **Step 2: Add the `NoForeground` arm to the hold-arm outcome chain (Change B)**

In the hold arm's injection block, the chain currently goes
`Interrupted` → `BlockedElevated` → `else if (!injected)` (the SendFailed/
ErrorBus arm). Insert a new arm BETWEEN `BlockedElevated` and
`else if (!injected)` — this is essential: without it, `NoForeground` would
fall into the `!injected` arm and fire a spurious ErrorBus report (forbidden
for normal park flows):

```csharp
                        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.NoForeground)
                        {
                            // No observable foreground at send start
                            // (hwnd==0; probe-gated park polarity, council
                            // 2026-07-28): nothing was typed. Park the WHOLE
                            // transcription with the default copy. Not an
                            // error: no ErrorBus report, no toast -- the
                            // pill is the surface.
                            _vm.EnterPendingPaste(final, _targetAtStart);
                            _log.LogInformation(
                                "No observable foreground at injection start; held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
```

- [ ] **Step 3: Mirror the arm in the toggle-arm outcome chain**

Same insertion point in the toggle arm's chain (~lines 1134–1164), using that
arm's locals: `outcome2` and `final2` (everything else identical).

- [ ] **Step 4: Add the `NoForeground` arm to `TryPastePending`**

In `TryPastePending` (~lines 382–437), insert a new `else if` between the
existing `Interrupted` log arm and the final
`else _log.LogWarning("Pending paste injection failed");`:

```csharp
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.NoForeground)
            // No observable foreground at click time (hwnd==0 transient):
            // nothing was typed; the slot keeps the FULL text for another
            // click. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste deferred: no observable foreground at click time; slot kept with full text");
```

No other change in the method: the existing
`if (!injected) _vm.ShowPendingPasteStatus(... BlockedElevated ? ElevatedTarget : Interrupted)`
tail already restores the default copy for `NoForeground`, and
`_vm.NotifyPasteAttempted(injected)` already keeps the slot.

- [ ] **Step 5: Run the Linux suite and commit**

`PipelineHost.cs` does not compile on Linux (`#if WINDOWS`), so the Linux
suite proves only that nothing shared broke; the Windows gate in Task 8
compiles and runs this file's project. Per AGENTS.md, Windows-only commits
still require a green Linux run.

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): retained-park logging and NoForeground park wiring in PipelineHost"
```

---

### Task 8: Full Windows gate + smoke verification

**Files:** none created or modified (fix-forward only if the gate finds a
real failure).

**Interfaces:**
- Consumes: all prior tasks' commits.
- Produces: the pre-push evidence required by AGENTS.md.

- [ ] **Step 1: Run the full Windows gate from WSL**

```bash
./scripts/windows-gate.sh
```
Expected: exit 0 and `GATE: GREEN` (budget 20–40 min; logs land in
`artifacts/windows-gate/`).

- [ ] **Step 2: Triage any non-green result against the known set**

Acceptable, out of scope (do NOT chase): the `Winpepper.Cleanup.Tests`
KNOWN-FAILING baseline skips listed in Global Constraints (Slot0 qwen trap
cases, Slot3 sotto-cleanup-lfm25-350m cases), and transient UNC
`retry should be performed` I/O errors (re-run the gate in a quiet window).
ANY other failure — especially in `Winpepper.Platform.Tests`,
`Winpepper.Core.Tests`, or the `Winpepper.App` build — must be fixed
(fix-forward with its own tested, committed change) and the gate re-run
until green.

- [ ] **Step 3: Manual Windows smoke checklist (record results in the PR/branch notes)**

These exercise the PipelineHost wiring that has no automated coverage
(established repo pattern — verified by gate compile + manual smoke):

1. **Trapdoor fixed (Change A):** dictate, let it park (e.g. switch windows
   mid-paste), then press the pedal and dictate AGAIN, letting the second one
   park too (stay on the admin window). Expect: pill still shows a pending
   state; log shows `Pending paste retained across new dictation`; ONE pill
   click into Notepad pastes BOTH dictations, oldest first, space-separated;
   pill hides.
2. **Retention across a successful dictation:** park once, then dictate into
   Notepad normally. Expect: new text pastes live; afterwards the pill
   RETURNS to the pending presentation with the correct copy; clicking it
   pastes the ORIGINAL parked text.
3. **hwnd==0 observability (Change B):** normal dictations and pastes;
   inspect the log for any
   `Foreground hwnd is 0 at injection start (occurrence #N)` /
   `Foreground hwnd read 0 mid-paste (occurrence #N)` lines. Expect: none in
   calm use (matches the probe: zeros never occur at rest). If one appears,
   confirm the paste PARKED (pill shows `Click to paste`) and one click
   recovered the full text. Known-legitimate exception: a park coinciding
   with a USER-initiated window activation (e.g. opening settings from the
   tray exactly as a dictation completes) is correct fail-safe behavior —
   focus genuinely moved — not a bug (ledger N3).
4. **Elevated park still works (regression):** focus an elevated terminal,
   dictate. Expect: nothing typed, pill shows
   `Admin window - switch & click` (renders fully, no ellipsis), click into
   Notepad pastes the full text.

- [ ] **Step 4: Done — hand off**

The branch is ready for the workflow's review stages once the gate is green
and the smoke checklist has no unexplained deviation. Push only after
`GATE: GREEN` (AGENTS.md).

---

## Self-review record (author)

- **Spec coverage:** Change A trapdoor → Tasks 5–7 (append chosen, rationale
  in Design decisions; no silent drop remains). Change B (1) mid-stream halt
  → Task 3; (2) at-start park, 5-1 majority → Task 2 (probe run BEFORE
  planning, evidence section above; benign-flow zeros rare ⇒ majority
  polarity ships, no data-gated fallback needed); (3) log line + counter,
  at-start and mid-stream separately → Tasks 1–3; test-pin supersession →
  Tasks 2, 3, 4, 5, 6 with comments naming the revision; fail-open doc-comment
  split (probe unobservable ⇒ inject vs foreground absent ⇒ park) → Tasks
  2/3/4; bundling rider satisfied — both changes in this one branch/plan.
  Copy budget → no new copy + Task 6 budget pin. Constraints (no clipboard,
  no toast/ErrorBus for normal flows, full-text semantics, per-chunk
  predicate structure preserved, PipelineHost patterns) → held throughout;
  the explicit `NoForeground` arms exist precisely to avoid ErrorBus
  misclassification. Stop condition: no fundamental problem found — probe
  data cleanly supports the majority polarity, and append preserves full-text
  semantics without workaround.
- **No silent deferrals:** PipelineHost wiring has no unit-test seam in this
  repo (zero PipelineHost tests by pattern); its observable production
  outcomes are named in the Task 8 smoke checklist and its logic-bearing
  parts are pinned at the VM/injector level (Tasks 2–6). No stub, mock, or
  TODO stands in for any user-facing behavior.
- **Placeholder scan:** no TBD/TODO/"handle edge cases"/"similar to Task N";
  every code step shows the code; every command has expected output.
- **Type consistency:** `HwndZeroMeter.RecordAtStart/RecordMidStream/
  AtStartCount/MidStreamCount` (Tasks 1→2→3), `InjectionRunOutcome.
  NoForeground` (Tasks 2→7), `GuardedInjectionRun.Execute(..., Action?
  onZeroForeground = null)` (Task 3 both sides), `PendingPasteState.
  HoldOrAppend(string, InjectionTarget, PendingPasteReason)` + `Reason`
  (Tasks 5→6), `EnterPendingPaste(string, InjectionTarget,
  PendingPasteReason = Interrupted)` unchanged signature (Tasks 5–7) — all
  cross-checked. Green-at-every-commit ordering verified: Task 2 retunes the
  injector-level hwnd-0 pin before Task 3 flips the decider that the
  loop-level pin depends on.
