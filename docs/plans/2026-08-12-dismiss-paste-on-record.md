# Dismiss Pending Paste on Recording Start Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** When the user starts a new recording, any parked "Click to paste"
pending paste is dismissed immediately and permanently — the pill never
returns to the old park after that dictation, and a park from the new
dictation holds only the new text.

**Architecture:** The pending-paste slot lives in Core's `PendingPasteState`,
owned by `SessionViewModel`. The single behavioral change point is
`SessionViewModel.OnEngineStateChanged`'s Recording arm (Core, net9.0,
Linux-testable): the engine only reaches Recording from Idle, so that arm
fires exactly when a new dictation starts. Two App-layer log lines in
`PipelineHost` that currently say "retained / will append" are corrected to
say the park is dismissed (Windows-only compile; verified via the documented
WSL→Windows build script).

**Tech Stack:** C# / .NET 9, WinUI 3 (App layer only), xUnit v3 + Shouldly
tests.

**Policy context (read first):** Today a parked pending paste deliberately
SURVIVES a new recording (council decision 2026-07-28: "preserve/append or
fail loud — never silently drop", owner-approved at the time). This task is
the owner deliberately REVERSING that decision: "Dismiss the click to paste
as soon as a new recording starts." Every test and comment repinned below is
an intentional policy reversal, dated 2026-08-12, not an accidental
regression. Historical plan docs under `docs/plans/` are NOT edited.

## Global Constraints

- **Test gates (repo `AGENTS.md`):** Before EVERY commit run the Linux
  suite `./scripts/linux-tests.sh` and require `LINUX SUITE: GREEN` (it
  builds all 9 test projects `-c Release` net9.0 plus the asr-latency-bench
  compile gate and runs each via the xUnit v3 in-process runner). Never
  `dotnet test` (VSTest host is unreliable). Full Windows verification
  (all 13 project/TFM runs) happens only via `./scripts/windows-gate.sh`;
  do not hand-roll `dotnet build` of the App from WSL — App builds go
  through `scripts/build-app-windows-from-wsl.sh` only.
- **Never mix Linux- and Windows-side `bin/`/`obj`** in the same tree; the
  helper scripts clean automatically when switching sides.
- The pending slot is **in-memory only**; never persist it, never touch the
  clipboard (typing is SendInput keystrokes; the pill is the surface).
- Pill status copy must stay ≤ 32 chars at FontSize 13 in the 300-DIP pill
  (pinned by `PendingCopy_FitsThePillBudget`). This plan changes NO copy.
- SDK: repo-local .NET SDK 9.0.100 at `/home/dan/code/winpepper/.dotnet`.
  Prefix focused `dotnet` commands with:
  `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"`
  (`./scripts/linux-tests.sh` sets these itself).
- **xUnit v3 zero-match trap (measured, validator LB-3):** a `-namespace`
  (or any) filter matching ZERO tests exits 0 and prints only `Total: 0` —
  the summary OMITS the `Errors/Failed/...` tokens entirely. Any focused
  pass/fail check below therefore requires BOTH exit 0 AND a summary line
  containing `Total:` with a non-zero count AND `Failed: 0`. An exit-code-
  only or `grep "Failed: 0"`-only check silently "passes" on zero matches.
- Work only in the worktree `/home/dan/code/winpepper/.worktrees/dismiss-paste-on-record`
  on branch `the-usual/dismiss-paste-on-record`.

---

### Task 1: Dismiss the pending park in Core when recording starts, repin the policy tests

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (Recording arm at 502-514; stale policy comments at 258-266, 383-387, 440-450, 531-546)
- Modify: `src/Winpepper.Core/Pending/PendingPasteState.cs` (doc header 3-16 only; NO behavior change)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs` (repin 4 tests)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs` (repin 1 test, add 1 boundary pin)

**Interfaces:**
- Consumes: `PendingPasteState.Discard()` (`src/Winpepper.Core/Pending/PendingPasteState.cs:54`, idempotent, existing), `SessionEngine` transition `(Idle, StartRequested) → Recording` (`src/Winpepper.Core/Sessions/SessionEngine.cs:19`, the ONLY way Recording is reached), `SessionViewModel.OnEngineStateChanged` Recording arm (`SessionViewModel.cs:502-514`, marshalled via `_ui.Post`).
- Produces: New Core invariant — entering the Recording engine state always discards any held pending slot. No new public API; `HoldOrAppend`/`Discard`/`EnterPendingPaste` signatures unchanged.

- [ ] **Step 1: Rewrite the pinned tests for the new policy (failing)**

In `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`, replace the body and comment of four existing tests (keep all other tests in both files byte-identical):

Replace `NewDictation_RetainsPending` (currently lines 50-66) with:

```csharp
    [Fact]
    public void NewDictation_DismissesPending()
    {
        // DELIBERATE PIN REVISION (owner directive 2026-08-12: "Dismiss the
        // click to paste as soon as a new recording starts"; supersedes the
        // council 2026-07-28 preserve/append pin). Pressing the pedal again
        // now DECLARES the held text abandoned: the park is discarded at
        // recording start.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"));

        engine.Apply(SessionEvent.StartRequested); // Recording

        vm.HasPendingPaste.ShouldBeFalse();
        vm.PendingPasteText.ShouldBe(string.Empty);
        vm.Stage.ShouldBe(SessionStage.Recording); // dictation UX unchanged
        vm.StatusText.ShouldBe("Recording...");
    }
```

Replace `ParkSurvivesDictation_EngineIdle_RestoresPendingPillAndCopy` (currently lines 68-85) with:

```csharp
    [Fact]
    public void DismissedPark_DoesNotReturn_WhenDictationEnds()
    {
        // The dismissal is final: when the interrupting dictation ends
        // (here: cancelled -- CancelRequested drives the engine straight
        // back to Idle), the pill must NOT resurrect the old park; it
        // settles to the ordinary Idle/"Ready" presentation.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"), PendingPasteReason.ElevatedTarget);
        engine.Apply(SessionEvent.StartRequested);

        engine.Apply(SessionEvent.CancelRequested); // engine -> Idle

        vm.HasPendingPaste.ShouldBeFalse();
        vm.PendingPasteText.ShouldBe(string.Empty);
        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }
```

Replace `SecondPark_Appends_AndOneClickPastesEverything` (currently lines 87-102) with:

```csharp
    [Fact]
    public void SecondPark_AfterDismissal_HoldsOnlyNewText_OneClickPastesIt()
    {
        // With dismissal at recording start, a dictation that parks always
        // parks into an EMPTY slot: no append of abandoned text. One click
        // pastes exactly this dictation's text.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("first thought.", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested);   // new dictation; park DISMISSED
        engine.Apply(SessionEvent.CancelRequested);  // back to Idle for clarity

        vm.EnterPendingPaste("second thought.", T(2, "b")); // this dictation parked

        vm.PendingPasteText.ShouldBe("second thought.");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.NotifyPasteAttempted(injected: true).ShouldBeTrue(); // ONE click
        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }
```

Replace `ErrorReport_MidDictation_WhilePending_StillTakesThePill` (currently lines 152-173) with:

```csharp
    [Fact]
    public void ErrorReport_MidDictation_AfterDismissedPark_StillTakesThePill()
    {
        // Dismissal must not defang error presentation: a dictation started
        // over a held park discards the park at start, and a mid-dictation
        // EVENT error must still present (OnBusReport's idle guard scopes by
        // engine state; the slot is already empty by now).
        var (vm, engine) = NewVm();
        var bus = new ErrorBus();
        vm.AttachErrorBus(bus);
        vm.EnterPendingPaste("saved text", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested); // Recording: park dismissed

        bus.Report(ErrorStage.Unknown, new InvalidOperationException("pipeline blew up"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);      // presented, not swallowed
        vm.HasPendingPaste.ShouldBeFalse();         // dismissed at start
        vm.PendingPasteText.ShouldBe(string.Empty);
    }
```

In `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`, replace `NotifyError_While_Pending_Still_Takes_The_Pill_Then_Restores_The_Pending_Pill` (currently lines 194-225) with these two tests:

```csharp
    [Fact]
    public void NotifyError_AfterDismissedPark_Takes_The_Pill_Then_Settles_To_Ready()
    {
        // Mirrors the real call site: PipelineHost applies SessionEvent.Failed
        // FIRST (engine back at Idle), THEN NotifyError. A park created
        // BEFORE this dictation was dismissed at recording start
        // (2026-08-12 owner directive), so after the error's self-clear the
        // resync settles to plain Idle/"Ready" -- there is no park left to
        // hand the pill back to.
        //
        // DISCRIMINATING: still the squatting-pill regression pin --
        // ReleasePillIfUnchanged must release when the generation matches;
        // with an empty slot, Stage=Error must not survive the 6 s hold.
        var (vm, engine, _, delays) = NewVm();
        vm.EnterPendingPaste("saved text", new Winpepper.Core.Pending.InjectionTarget { WindowHandle = 1, ElementId = "a" });
        StartDictation(engine);
        engine.Apply(SessionEvent.Failed); // engine -> Idle; no park to restore

        vm.NotifyError("pipeline blew up");

        vm.Stage.ShouldBe(SessionStage.Error);          // presented, not swallowed
        vm.StatusText.ShouldBe("Error: pipeline blew up");

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Idle);   // settles to Ready -- nothing parked
        vm.StatusText.ShouldBe("Ready");
        vm.HasPendingPaste.ShouldBeFalse();
    }

    [Fact]
    public void NotifyError_WithHeldPark_SelfClear_Restores_The_Pending_Pill()
    {
        // Boundary pin for the 2026-08-12 dismissal policy: dismissal happens
        // ONLY at recording start. A park that is still held (no new
        // dictation ever started) keeps its pill across a transient error:
        // NotifyError presents, then the self-clear resync hands the pill
        // back to the park. This test passes both before and after the
        // policy change -- it fails if dismissal is implemented anywhere
        // OTHER than the Recording arm (e.g. a blanket Idle-arm discard).
        var (vm, _, _, delays) = NewVm();
        vm.EnterPendingPaste("saved text", new Winpepper.Core.Pending.InjectionTarget { WindowHandle = 1, ElementId = "a" });

        vm.NotifyError("pipeline blew up");

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error: pipeline blew up");

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.PendingPaste);   // park gets its pill back
        vm.StatusText.ShouldBe("Click to paste");
        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("saved text");     // the park itself is untouched
    }
```

- [ ] **Step 2: Run the rewritten tests and verify the intended failures**

Run:

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -namespace Winpepper.Core.Tests.ViewModels
```

Expected: the namespace run reports a non-zero `Total:` (the ViewModels namespace holds 183 tests before this task's test edits, 184 after — a `Total: 0` run means a filter typo, NOT a result; see the xUnit zero-match trap in Global Constraints) and exactly five FAILs, each for the missing behavior (not setup errors):
- `NewDictation_DismissesPending` fails at `vm.HasPendingPaste.ShouldBeFalse()` (park still held).
- `DismissedPark_DoesNotReturn_WhenDictationEnds` fails at `vm.HasPendingPaste.ShouldBeFalse()` (park restored at Idle).
- `SecondPark_AfterDismissal_HoldsOnlyNewText_OneClickPastesIt` fails at `vm.PendingPasteText.ShouldBe("second thought.")` (actual `"first thought. second thought."`).
- `NotifyError_AfterDismissedPark_Takes_The_Pill_Then_Settles_To_Ready` fails at the post-`FireAll` `vm.Stage.ShouldBe(SessionStage.Idle)` (actual `PendingPaste`).
- `ErrorReport_MidDictation_AfterDismissedPark_StillTakesThePill` fails at `vm.HasPendingPaste.ShouldBeFalse()`.
- `NotifyError_WithHeldPark_SelfClear_Restores_The_Pending_Pill` PASSES already (boundary pin; no dismissal exists yet).

(That is five failing tests: the four renamed pins in SessionViewModelPendingTests plus the one renamed pin in SessionViewModelErrorLifecycleTests.)

- [ ] **Step 3: Add the minimal production implementation**

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`, replace the Recording arm of `OnEngineStateChanged` (currently lines 502-514):

```csharp
                case SessionState.Recording:
                    // A new dictation DISMISSES any held park (owner
                    // directive 2026-08-12: "dismiss the click to paste as
                    // soon as a new recording starts"; supersedes the
                    // council 2026-07-28 preserve/append policy). Starting
                    // to talk again declares the deferred text abandoned.
                    // Discard idempotently BEFORE this dictation can
                    // end-park into EnterPendingPaste, so a later park
                    // holds only THIS dictation's text.
                    _pending.Discard();
                    _stopwatch.Restart();
                    _cpuBaseline = SystemTimesSampler?.Invoke();
                    _cpuTicksSinceStart = 0;
                    _cpuPeggedState = 0;
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
```

Also update the four stale policy comments in the same file (comment-only edits, no behavior change):

1. OnBusReport guard comment (currently lines 262-266) — replace:

```csharp
        // NOT-in-flight since parks survive dictations (council 2026-07-28):
        // an error DURING a dictation started over a held park must still
        // present -- an unconditional return here silently dropped that
        // dictation's failure.
```

with:

```csharp
        // NOT-in-flight: a park can still coincide with an in-flight engine
        // state when the CURRENT dictation has just parked at its end
        // (EnterPendingPaste fires before InjectionCompleted); an error then
        // must still present -- an unconditional return here silently
        // dropped that dictation's failure (2026-07-24 class). Pre-dictation
        // parks no longer reach this code mid-dictation: they are dismissed
        // at recording start (owner directive 2026-08-12).
```

2. ReleasePillIfUnchanged note (currently lines 383-387) — replace:

```csharp
        // NOTE: no HasPending early-return here. Since parks survive dictations
        // (council 2026-07-28) an error CAN own the pill while a park is held;
```

with:

```csharp
        // NOTE: no HasPending early-return here. A same-dictation park
        // (created at its own dictation's end) CAN coexist with an error
        // owning the pill;
```

3. NotifyError doc tail (currently lines 445-450) — replace:

```csharp
    /// INTENTIONALLY NOT pending-scoped either: the single production caller
    /// is a real per-dictation failure, never a background report. Since
    /// parks survive dictations (council 2026-07-28), a HasPending guard
    /// here silently dropped the failure of any dictation started over a
    /// held park. The park is not lost: the self-clear resync restores the
    /// PENDING pill after the error's hold.
```

with:

```csharp
    /// INTENTIONALLY NOT pending-scoped either: the single production caller
    /// is a real per-dictation failure, never a background report. With a
    /// HasPending guard here, a park held at that moment silently swallowed
    /// that failure (no pill error; Unknown never toasts). A surviving park
    /// (one this dictation parked itself; pre-dictation parks were dismissed
    /// at recording start, owner directive 2026-08-12) is not lost: the
    /// self-clear resync restores the PENDING pill after the error's hold.
```

4. Idle-arm comment (currently lines 533-537) — replace:

```csharp
                    // A held park survives dictations: returning to engine
                    // Idle with a held slot must RESTORE the PENDING pill
                    // (stage + reason-correct copy) -- not leave the last
                    // in-flight copy ("Inserting...") on screen and not
                    // auto-hide the pill.
```

with:

```csharp
                    // A slot held at engine Idle belongs to the dictation
                    // that just ended (a PRE-dictation park was dismissed at
                    // recording start): RESTORE the PENDING pill (stage +
                    // reason-correct copy) -- do not leave the last
                    // in-flight copy ("Inserting...") on screen and do not
                    // auto-hide the pill.
```

In `src/Winpepper.Core/Pending/PendingPasteState.cs`, replace the class doc header (currently lines 3-16) with (comment-only; `HoldOrAppend`/`Discard`/`OnPasteAttempted` code and the append separator are all unchanged):

```csharp
/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when the
/// paste could not be delivered (focus moved, halt gesture, elevated target,
/// no observable foreground). NEVER persisted to disk -- history archiving
/// is a separate, unchanged feature. Lifecycle:
/// None -> Pending(text,target,reason)
/// -> consumed (successful pill-click paste)
/// | DISMISSED when the user starts a NEW dictation (owner directive
///   2026-08-12: "dismiss the click to paste as soon as a new recording
///   starts" -- starting to talk again declares the deferred text abandoned;
///   supersedes the council 2026-07-28 preserve/append policy)
/// | app exit (memory-only).
/// HoldOrAppend KEEPS its never-replace append semantics for an occupied
/// slot as a defensive component guarantee, although production paths now
/// always park into an empty slot (the Recording-arm discard runs first).
/// Cancel preserves the slot: a cancel happens mid-dictation, and any park
/// then alive belongs to that same dictation.
/// </summary>
```

- [ ] **Step 4: Run the focused tests**

Run:

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -namespace Winpepper.Core.Tests.ViewModels
```

Expected: PASS — exit 0 AND a summary line with `Total: 184` (non-zero, per the xUnit zero-match trap in Global Constraints) AND `Errors: 0` AND `Failed: 0` for the namespace run (all five previously failing pins now green, the boundary pin still green, and no other ViewModels test regressed).

- [ ] **Step 5: Refactor while green**

No code refactor needed: the implementation is one idempotent `_pending.Discard()` call plus comment updates. Verify by hand that no lingering `council 2026-07-28` / "parks survive" / "retained across" phrasing remains in `src/Winpepper.Core/`:

Run: `grep -rn "council 2026-07-28\|survives dictations\|survives a new dictation\|retained across" src/Winpepper.Core/`

Expected: no matches.

- [ ] **Step 6: Run impacted-test verification**

The change is inside `SessionViewModel`/`PendingPasteState`, used by every Core VM test and by App/Platform code paths that consume the VM. The impacted set is the whole `Winpepper.Core.Tests` project plus the platform pending-paste-adjacent tests; the clean superset available on Linux is the full Linux suite, which also satisfies the pre-commit gate:

Run: `./scripts/linux-tests.sh`

Expected: `linux-tests grand total: 1962 tests` then `LINUX SUITE: GREEN` (baseline was 1961; this task only renames four pins in SessionViewModelPendingTests, renames one and adds one in SessionViewModelErrorLifecycleTests → Core.Tests 503 → 504, +1 net new test). The hard requirement is GREEN with Errors: 0, Failed: 0; record the observed grand total.

- [ ] **Step 7: Commit the task**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs src/Winpepper.Core/Pending/PendingPasteState.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs
git commit -m "feat(core): dismiss the pending click-to-paste park when a new recording starts"
```

---

### Task 2: Correct the PipelineHost retention log lines (App layer, Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs:580-583` (HoldDown arm) and `src/Winpepper.App/Hosting/PipelineHost.cs:1204-1207` (Toggle arm)

**Interfaces:**
- Consumes: the Task 1 Core behavior (`_vm.HasPendingPaste`/`_vm.PendingPasteText` read BEFORE `_engine.Apply(SessionEvent.StartRequested)` still see the about-to-be-dismissed park, since the discard is posted to the UI queue by the engine event).
- Produces: truth-in-logging only; no behavior change, no new API, no testable surface on Linux (PipelineHost is `#if WINDOWS`, `net9.0-windows`; no test drives its hotkey arms — verified by survey).

- [ ] **Step 1: Establish the stale evidence (why no failing unit test exists)**

No xUnit test can pin these strings: `PipelineHost.HandleHotkey` is App-layer Windows-only code with no test coverage of its start arms (survey: `docs` explorer report `plan-recording-start-flow.md` §5). The failing artifact is the log text itself, which after Task 1 asserts the opposite of the code's behavior. Confirm the two stale lines exist:

Run: `grep -n "retained across new dictation" src/Winpepper.App/Hosting/PipelineHost.cs`

Expected: exactly two matches, at lines 582 and 1206, each saying the park is "retained" and "a park during this dictation will append" — now false.

- [ ] **Step 2: Replace both log lines**

At `src/Winpepper.App/Hosting/PipelineHost.cs:580-583` replace:

```csharp
                if (_vm.HasPendingPaste)
                    _log.LogInformation(
                        "Pending paste retained across new dictation ({Chars} chars held; a park during this dictation will append)",
                        _vm.PendingPasteText.Length);
```

with:

```csharp
                if (_vm.HasPendingPaste)
                    _log.LogInformation(
                        "Pending paste dismissed on new dictation ({Chars} chars discarded)",
                        _vm.PendingPasteText.Length);
```

At `src/Winpepper.App/Hosting/PipelineHost.cs:1204-1207` replace:

```csharp
                    if (_vm.HasPendingPaste)
                        _log.LogInformation(
                            "Pending paste retained across new dictation ({Chars} chars held; a park during this dictation will append)",
                            _vm.PendingPasteText.Length);
```

with:

```csharp
                    if (_vm.HasPendingPaste)
                        _log.LogInformation(
                            "Pending paste dismissed on new dictation ({Chars} chars discarded)",
                            _vm.PendingPasteText.Length);
```

(The reads of `_vm.HasPendingPaste`/`_vm.PendingPasteText` stay BEFORE `_engine.Apply(...)`, so the logged char count is the dismissed park's length.)

- [ ] **Step 3: Verify the stale strings are gone and the new ones are in place**

Run: `grep -n "retained across new dictation\|dismissed on new dictation" src/Winpepper.App/Hosting/PipelineHost.cs`

Expected: zero "retained across new dictation" matches; exactly two "dismissed on new dictation" matches (one in each hotkey arm).

- [ ] **Step 4: Compile-verify the App layer on the Windows host**

Run: `scripts/build-app-windows-from-wsl.sh`

Expected: exit 0, final line `BUILD OK` (the script pre-cleans `bin/`/`obj`, builds `Winpepper.App` Release with `-m:1 -p:UseSharedCompilation=false -p:UseXamlCompilerExecutable=true` via powershell.exe interop, retrying only known transient UNC races). This is the documented App-build path; it never installs the MSI and never launches the app. Capture the run's `artifacts/build-app-windows/run-*/` directory path as evidence.

- [ ] **Step 5: Confirm the Linux suite is still green (pre-commit gate)**

The edit touched App-only code, but the repo rule requires a green Linux run for every commit. Step 4's Windows App build left Windows-built intermediates in `src/*/bin`+`src/*/obj`, and `linux-tests.sh` does NOT clean — running it over Windows intermediates risks deterministic CS0006 failures. Clean the mixed directories first (same clean `windows-gate.sh` performs, mirrored from `scripts/test-windows-from-wsl.sh:36`), then run the suite:

Run:

```bash
find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
./scripts/linux-tests.sh
```

Expected: clean completes silently (exit 0); then `LINUX SUITE: GREEN` with the same grand total recorded in Task 1 Step 6.

- [ ] **Step 6: Commit the task**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "fix(app): log pending-paste dismissal (not retention) when a new dictation starts"
```

---

## Post-plan verification (not a task)

- The full Windows gate `./scripts/windows-gate.sh` remains the pre-push requirement per `AGENTS.md`; this plan's changes are Core-behavior + an App log string, and Task 2 Step 4 already compiles the App on the Windows host. The gate is expected to run before any push of this branch.
- Evidence that survives the run: focused red/green runs, Task 1 + Task 2 `linux-tests.sh` GREEN receipts, and the Step 4 build-artifacts directory.
