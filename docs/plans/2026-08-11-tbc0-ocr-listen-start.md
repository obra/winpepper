# Start OCR Prefetch at Listen-Start Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** The window-context prefetch (UIA read with OCR fallback) launches the moment
listening starts — on both the hold and toggle paths — so the snapshot is ready before
transcription finishes and cleanup no longer waits for it after the user stops speaking.

**Architecture:** Two new pure types (the `WindowContextPrefetchGate` precedent, commit
0bbeceb): `WindowContextListenStartPolicy` (Winpepper.Cleanup) holds the start-time launch
DECISION; `WindowContextListenStartSequencer` (Winpepper.Platform, over the existing
coordinator) owns the per-dictation handle BOOK — launch-and-remember at listen-start,
take-and-clear at stop, clear on cancel/drop/teardown. Both are unit-tested on Linux; the
sequencer's tests pin the sequencing BEHAVIOR (launch occurs during RecordingStarted and
never during RecordingStopped; a disabled dictation launches nothing) against the real
production object the arms delegate to. The Windows-only `PipelineHost.cs` wiring shrinks
to a single private helper (policy gate + one log line) called once per start arm plus
one-line sequencer delegations per stop/cancel/drop/dispose site — location-pinned by
region-aware structural greps and compiled by the Windows gate. New `ctx_wait=` timing
telemetry (measured inside `CleanupRunner`, surfaced on `CleanupResult`, formatted by pure
`DictationTimingSummary`) makes the post-stop wait reduction directly measurable, and the
Task-4 regime tests measure it through the real coordinator + runner.

**Tech Stack:** C# / .NET 9, xUnit v3 (in-process runner via `dotnet exec`), Serilog-style
`ILogger` structure logging.

## Global Constraints

- AGENTS.md: before EVERY commit the Linux pure-managed suite must be green
  (`scripts/linux-tests.sh`; builds `-c Release`, runs via `dotnet exec <built test
  dll>` — NEVER `dotnet test`) — this includes docs-only commits (a fresh green run must
  exist against the tree being committed). Before finishing, the Windows gate
  (`scripts/windows-gate.sh`, 20–30 min timeout) must exit 0 with `GATE: GREEN`.
- SDK: bare `dotnet` is NOT on this environment's PATH. Every command block below starts
  with the two exports; run each block as a whole (or export once per shell):
  `export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH`
- `PipelineHost.cs` is `#if WINDOWS`: Linux never compiles it; pure decision logic lives
  in cross-target projects (Winpepper.Cleanup, Winpepper.Core, Winpepper.Platform) and is
  unit-tested on Linux (precedent: `WindowContextPrefetchGate`, 0bbeceb).
- OVERLAP: kata 8kg3 (gate prefetch on CleanupEnabled) and kata 233p (deduplicate the
  hold/toggle bodies) touch the same ~200-line pipeline bodies in parallel worktrees.
  The PipelineHost diff must be surgical: no dedup refactor, no reordering of unrelated
  statements, downstream consume code untouched.
- NEVER commit `.kata.toml`, `.opencode/`, model/corpus/artifact files; never push, merge,
  PR, install MSIs, launch/kill Winpepper.exe, or write `%LOCALAPPDATA%\winpepper`.
- The cleanup-disabled skip is non-negotiable: NO OCR/UIA work when cleanup is disabled
  (same predicate as today, evaluated at start).

## Requirements

- **R1 — Outcome:** OCR/window-context prefetch begins at listening start on BOTH
  hotkey paths (hold `HoldDown`, toggle `Toggle`-while-Idle), evidenced by a per-dictation
  log line at launch and by timing evidence.
- **R2 — Constraint:** no prefetch work unless the SAME gate as today passes
  (`WindowContextPrefetchGate.ShouldPrefetch(cleanupEnabled, windowContextEnabled,
  activePromptFormat)`), now evaluated at start; plus skip when there is no start-captured
  hwnd (behavior-equivalent to today's Empty short-circuit, minus a wasted task).
- **R3 — Evidence:** measured post-stop latency reduction when cleanup+context enabled:
  new `ctx_wait=` field on the dictation timing line (≈0 ms whenever the utterance
  outlived the prefetch; today's exposure is up to the 500 ms `WindowContextWait` budget),
  with the July contention guards (`native_max`, `native_over250`, `over250_at`) retained
  unchanged so a regression is visible.
- **R4 — Decided ruling (staleness):** the snapshot belongs to the target window at
  listen-start — BOTH the hwnd (already start-captured, PipelineHost.cs:675/:1302) and
  the CONTENT (newly start-captured by this change) — and is kept even if the user
  switches windows mid-recording (the listen-start window is the paste target).
  Side effect accepted and recorded: a Cleanup-tab toggle mid-dictation applies from the
  NEXT dictation (a starting-early prefetch cannot be gated on future settings); an
  accidental silent tap may launch one prefetch burst that trim-drop then cancels
  (existing `CancelAndClear`), a CPU-only cost consistent with "recording time is free
  concurrency".
  Diagnostic-only telemetry change, recorded deliberately (review round 1): a start with
  NO foreground window (hwnd == 0) now supplies NO context task, so `ctx_src` is OMITTED
  where the at-stop launch used to supply an instantly-completed Empty task and stamp
  `ctx_src=none`. Both spellings mean "no context"; the skip avoids a wasted task.
- **R5 — Constraint:** Linux-testable pure decision logic; surgical Windows wiring;
  no dedup of the hold/toggle bodies (233p owns that).

---

### Task 1: Pure listen-start launch policy (`WindowContextListenStartPolicy`)

**Requirements served:** R1, R2, R4, R5

**Behavior:**
- New `public static class WindowContextListenStartPolicy` in `src/Winpepper.Cleanup/`
  with one method:

  ```csharp
  public static bool ShouldStart(
      bool cleanupEnabled,
      bool windowContextEnabled,
      string? activePromptFormat,
      bool hwndAtStartNonZero)
      => hwndAtStartNonZero
         && WindowContextPrefetchGate.ShouldPrefetch(cleanupEnabled, windowContextEnabled, activePromptFormat);
  ```

  (The coordinator-null concern is NOT in the policy: it stays in the wiring as an
  inline `_ctxCoordinator is not null` check — see Task 3 — so the C# nullable-flow
  analysis (WarningsAsErrors=nullable per Directory.Build.props) can prove the launch
  site's `_ctxCoordinator.Start(...)` is safe.)

- Docstring records the sequencing ruling (launch exactly once per dictation, at
  listen-start; never at stop), the staleness ruling (R4) INCLUDING the hwnd-zero
  telemetry note (no task supplied → `ctx_src` omitted rather than `none`), and that
  the cleanup-disabled / raw-io skips are inherited from `WindowContextPrefetchGate`
  by delegation (single policy home — 8kg3's gate semantics are preserved, not copied).

**Files:**
- Create: `src/Winpepper.Cleanup/WindowContextListenStartPolicy.cs`
- Test: `tests/Winpepper.Cleanup.Tests/WindowContextListenStartPolicyTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Cleanup.WindowContextPrefetchGate.ShouldPrefetch(bool, bool,
  string?)` (existing, src/Winpepper.Cleanup/WindowContextPrefetchGate.cs:14-18),
  `PromptFormatCapabilities.CarriesSystemPrompt` (transitively).
- Produces: `WindowContextListenStartPolicy.ShouldStart(bool, bool, string?, bool)`
  → Task 3's wiring.

**Test cases (file `WindowContextListenStartPolicyTests.cs`, xUnit `[Fact]`s; classes
namespace `Winpepper.Cleanup.Tests`):**
- all-enabled (`cleanupEnabled: true, windowContextEnabled: true, "chatml", true`) → `true`.
- cleanup disabled (`cleanupEnabled: false`, rest enabled) → `false`  *(8kg3 skip)*.
- window-context disabled → `false`.
- raw-io prompt format (`activePromptFormat: CleanupPromptFormatter.RawIo` — the
  constant, value "raw-io", CleanupPromptFormatter.cs:43) → `false`.
- null prompt format → `true` (null behaves as allowed — PromptFormatCapabilities docs).
- hwnd zero (`hwndAtStartNonZero: false`, all else enabled) → `false`.

- [ ] **Step 1: Write the failing behavioral test**

Write `WindowContextListenStartPolicyTests.cs` with the six cases above.

- [ ] **Step 2: Run the test and verify the intended failure**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -class Winpepper.Cleanup.Tests.WindowContextListenStartPolicyTests
```

Expected: FAIL to build/run — `WindowContextListenStartPolicy` does not exist (CS0103);
this proves the tests reference genuinely missing behavior. (If `-class` filtering errors,
run the whole assembly: append `-notrait "Platform=Windows"` without `-class`.)

- [ ] **Step 3: Add the minimal production implementation**

Create `src/Winpepper.Cleanup/WindowContextListenStartPolicy.cs` exactly as specified
above (no #if guards — pure, cross-target), including the ruling docstring.

- [ ] **Step 4: Run the focused test**

Same command block as Step 2. Expected: build OK, 6/6 within the class (or the whole
assembly with `-notrait` showing 0 failures).

- [ ] **Step 5: Refactor while green**

None needed — the method is a single composed predicate (the gate stays the single
policy home; no new abstraction).

- [ ] **Step 6: Run broader verification (the pre-commit suite the AGENTS rule requires)**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
./scripts/linux-tests.sh
```

Expected: exit 0, `LINUX SUITE: GREEN` (baseline 1854 + 6 new policy tests = 1860).
Commit only when green.

- [ ] **Step 7: Commit the task**

```bash
git add src/Winpepper.Cleanup/WindowContextListenStartPolicy.cs tests/Winpepper.Cleanup.Tests/WindowContextListenStartPolicyTests.cs
git commit -m "feat(cleanup): WindowContextListenStartPolicy — pure listen-start prefetch launch decision (kata tbc0)"
```

---

### Task 2: `ctx_wait` wait-measurement telemetry (CleanupRunner → CleanupResult → timing line)

**Requirements served:** R3

**Behavior:**
- `CleanupResult` gains `public int? WindowContextWaitMs { get; init; }` — ms the runner
  actually waited inside the bounded window-context wait; null when no wait ran (no
  context task supplied, feature disabled, or the top bypass returned before the wait).
  Lives beside the existing `ConsumedWindowContext` (same consume-site semantics).
- `CleanupRunner.RunAsync` measures ONLY the wait (`Stopwatch` around the existing
  `Task.WhenAny(windowContextTask, Task.Delay(options.WindowContextWait, ct))` block,
  src/Winpepper.Cleanup/CleanupRunner.cs:79-101): local `int? ctxWaitMs = null;` set from
  the stopwatch right after the WhenAny completes (both branches: consumed and
  budget-expired/exception — record the elapsed wait regardless of outcome). Every
  existing `with { ConsumedWindowContext = consumedWindowContext }` site
  (CleanupRunner.cs:74, :139, :144, :154, :158, :173, :180, :197, :204, :210) and the
  final success return's initializer block (:218-224) gain
  `WindowContextWaitMs = ctxWaitMs`. `Finalize` is untouched (the bypass-at-top site
  passes `ctxWaitMs` = null, matching ConsumedWindowContext = null).
- `DictationTimingSummary` gains `public int? CtxWaitMs { get; set; }` with a docstring
  ("ms cleanup actually waited for the window-context prefetch inside its 500 ms budget;
  null when no context task was supplied or cleanup never ran; ≈0 once the prefetch
  launches at listen-start — kata tbc0") and one FormatLine addition directly after the
  `ctx_src` AppendOptStr: `AppendOptMs(sb, "ctx_wait", CtxWaitMs);`.

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupResult.cs`
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` (wait block + the `with` sites)
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (property + FormatLine)
- Test: `tests/Winpepper.Cleanup.Tests/` (add cases to the existing CleanupRunner test file
  that already drives `RunAsync` with a fake backend — find it, reuse its fixtures)
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: existing `CleanupRunner.RunAsync` wait block; `DictationTimingSummary.FormatLine`.
- Produces: `CleanupResult.WindowContextWaitMs` (int?), `DictationTimingSummary.CtxWaitMs`
  (int?), rendered as `ctx_wait=` — Task 3's wiring stamps `timing.CtxWaitMs =
  result.WindowContextWaitMs`.

**Test cases (extend `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` using its
existing `NewRunner`/`FakeLlamaCleanupBackend` — note its `DefaultOptions()` sets
`WindowContextWait = 50 ms`, so construct explicit options per case. Upper bounds are
deliberately generous — timer completion has no guaranteed upper scheduling bound under
load; the lower bounds carry the signal):**
- CleanupRunner: context task completing after ~250 ms
  (`Task.Run(async () => { await Task.Delay(250); return "ctx"; })`) with
  `WindowContextWait = TimeSpan.FromSeconds(2)` and a fake backend returning a
  plausible cleanup → `ConsumedWindowContext == true`, `WindowContextWaitMs` in [50, 1500].
- CleanupRunner: context task that never completes with `WindowContextWait = 500 ms` →
  `ConsumedWindowContext == false`, `WindowContextWaitMs` in [400, 1500] (full budget
  waited ± scheduler generosity).
- CleanupRunner: already-complete context task (`Task.FromResult<string?>("ctx")`) →
  `WindowContextWaitMs < 250` (signal: far below any realistic prefetch remainder),
  consumed true.
- CleanupRunner: FAULTED context task (a task that throws before the budget) →
  `ConsumedWindowContext == false`, `WindowContextWaitMs` non-null (≈0 — the wait
  resolved immediately through the exception branch), result is the raw transcript.
- CleanupRunner: fallback AFTER the wait is stamped correctly — fake backend THROWS;
  context task completes after ~120 ms, budget 2 s → path `FallbackBackendError` carries
  `WindowContextWaitMs` in [20, 1500] (proves the `with`-site propagation on the
  exception returns).
- CleanupRunner: null context task → `WindowContextWaitMs == null`,
  `ConsumedWindowContext == null` (extends the existing null-supplied test at
  CleanupRunnerTests.cs:485).
- CleanupRunner: cleanup disabled (`CleanupOptions.Enabled = false`) with a context task
  supplied → bypass return has `WindowContextWaitMs == null` (no wait ran).
- DictationTimingSummary (`tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`):
  `CtxWaitMs = null` → line has no `ctx_wait=`; `CtxWaitMs = 37` → line contains
  ` ctx_wait=37ms` positioned right after `ctx_src` when both present (assert via
  `IndexOf` ordering or the exact-substring style the existing tests use).

- [ ] **Step 1: Write the failing behavioral test**

Add the CleanupRunner cases and the two DictationTimingSummary cases. First read the
existing CleanupRunner test file to reuse its fake backend (a backend whose
`GenerateAsync` returns text that passes the runner's plausibility gates — the existing
tests already construct one) and its `CleanupOptions` construction.

- [ ] **Step 2: Run the test and verify the intended failure**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: build FAIL (CS1061: CleanupResult has no `WindowContextWaitMs`) — missing
behavior, not a setup accident. (If the DictationTimingSummary tests were written first,
same class of failure on `CtxWaitMs`; either order is fine — build both test projects and
confirm both failures are the missing members.)

- [ ] **Step 3: Add the minimal production implementation**

Apply the three production edits described above (CleanupResult property; CleanupRunner
stopwatch + `with`-site property; DictationTimingSummary property + one FormatLine line).

- [ ] **Step 4: Run the focused test**

Run: build both test projects, then
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```

Expected: PASS, 0 failures both.

- [ ] **Step 5: Refactor while green**

Check the edited `with` sites for accidental drift (all mention both properties);
otherwise no refactor — the diff is deliberately mechanical.

- [ ] **Step 6: Run broader verification**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
./scripts/linux-tests.sh
```

Expected: exit 0, `LINUX SUITE: GREEN` (1854 baseline + Task-1's 6 + this task's new tests).
Commit only when green.

- [ ] **Step 7: Commit the task**

```bash
git add src/Winpepper.Cleanup/CleanupResult.cs src/Winpepper.Cleanup/CleanupRunner.cs src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Cleanup.Tests tests/Winpepper.Core.Tests
git commit -m "feat(cleanup,core): ctx_wait telemetry — measure the bounded window-context wait per dictation (kata tbc0)"
```

---

### Task 3: Pure listen-start sequencer + PipelineHost delegation wiring

**Requirements served:** R1, R2, R4, R5

**Behavior:**

- New pure type `WindowContextListenStartSequencer` in
  `src/Winpepper.Platform/WindowContext/WindowContextListenStartSequencer.cs` (no #if —
  Platform references Core only, so the launch DECISION (Cleanup layer) arrives as a
  bool and the sequencer owns the per-dictation handle BOOK over the existing
  coordinator):

  ```csharp
  namespace Winpepper.Platform.WindowContext;

  /// <summary>tbc0: owns the listen-start launch / stop-consume handle book so
  /// PipelineHost's two hotkey arms are one-line delegations (unit-tested here on
  /// Linux; the arms themselves are #if WINDOWS). Launch happens HERE, at
  /// RecordingStarted — never at RecordingStopped. The launch DECISION is evaluated
  /// by the caller (Winpepper.Cleanup.WindowContextListenStartPolicy) and arrives as
  /// <paramref name="startPrefetch"/>; the coordinator lifecycle (cancel-prior,
  /// cancel-on-drop) stays with <see cref="WindowContextPrefetchCoordinator"/>.
  /// Single caller (the serialized hotkey loop) by contract — no locking.</summary>
  public sealed class WindowContextListenStartSequencer
  {
      private readonly WindowContextPrefetchCoordinator _coordinator;
      private WindowContextPrefetchHandle? _launched;

      public WindowContextListenStartSequencer(WindowContextPrefetchCoordinator coordinator)
          => _coordinator = coordinator;

      /// <summary>Call at listen-start, AFTER OnRecordingStart() and the hwnd capture.
      /// Launches (and books) only when <paramref name="startPrefetch"/> is true;
      /// otherwise launches nothing and books null.</summary>
      public WindowContextPrefetchHandle? RecordingStarted(bool startPrefetch, IntPtr hwndAtStart)
      {
          _launched = startPrefetch ? _coordinator.Start(hwndAtStart) : null;
          return _launched;
      }

      /// <summary>Call at stop: hands the listen-start handle to the consume path
      /// exactly once and clears the book. Never launches.</summary>
      public WindowContextPrefetchHandle? RecordingStopped()
      {
          var h = _launched;
          _launched = null;
          return h;
      }

      /// <summary>Clear the book without consuming (cancel / silence-drop / teardown).
      /// The underlying task, if any, is cancelled by the caller's existing
      /// coordinator.CancelAndClear() discipline.</summary>
      public void Clear() => _launched = null;
  }
  ```

- PipelineHost (src/Winpepper.App/Hosting/PipelineHost.cs, #if WINDOWS) changes — all
  sites are one-line DELEGATIONS; the only added structure is one private helper:

  1. Next to `_ctxCoordinator` (:103-107), replace the stale NOTE comment
     ("prefetch launched at recording STOP ...") with the tbc0 truth (launch at
     listen-start via the sequencer; lifecycle still owned by the coordinator) and add:

     ```csharp
     // tbc0: per-dictation listen-start launch/consume book over _ctxCoordinator
     // (null when the coordinator is absent). Both arms delegate; the sequencing
     // behavior is Linux-tested in WindowContextListenStartSequencerTests.
     private readonly Winpepper.Platform.WindowContext.WindowContextListenStartSequencer? _ctxSequencer;
     ```

  2. Ctor (:211-213): where `_ctxCoordinator` is assigned, also
     `_ctxSequencer = _ctxCoordinator is null ? null : new(_ctxCoordinator);`
     (nullable-flow clean; `_ctxCoordinator` is already fully-qualified there).

  3. One private helper (place next to CaptureTarget, :463-473):

     ```csharp
     /// <summary>tbc0: evaluate the listen-start policy on the start-time settings
     /// snapshot and delegate the launch (+book) to the sequencer. Call ONLY after
     /// _ctxCoordinator?.OnRecordingStart() and the _ctxHwndAtStart capture. The
     /// single launch LogInformation lives here (one site, R1 evidence).</summary>
     private void LaunchPrefetchAtListenStart(AppSettings settingsAtStart)
     {
         if (_ctxSequencer is null) return;
         var handle = _ctxSequencer.RecordingStarted(
             Winpepper.Cleanup.WindowContextListenStartPolicy.ShouldStart(
                 settingsAtStart.CleanupEnabled,
                 settingsAtStart.CleanupWindowContextEnabled,
                 _activeCleanupPromptFormat?.Invoke(),
                 _ctxHwndAtStart != IntPtr.Zero),
             _ctxHwndAtStart);
         if (handle is not null)
             _log.LogInformation("window-context prefetch started at listen-start {SessionId}", _currentSessionId);
     }
     ```

  4. HOLD start arm — after `_ctxHwndAtStart = ...ForegroundWindow.Handle();` (:675):

     ```csharp
     // tbc0: launch at listen-start (supersedes 1a's stop-launch) — see LaunchPrefetchAtListenStart.
     LaunchPrefetchAtListenStart(settingsForStream);
     ```

  5. TOGGLE start arm — identical one-liner after `_ctxHwndAtStart = ...` (:1302),
     passing `settingsForStream2` (:1217).

  6. Cancel arm (:1166-1185): next to `_ctxCoordinator?.CancelAndClear();` (:1169) add
     `_ctxSequencer?.Clear();`.

  7. HOLD stop arm: replace the at-stop launch block (:706-722 — the "1a: launch the
     window-context prefetch AT STOP" comment, `ctxPrefetch = null`, the
     `settingsAtStop` read, the prompt-format comment, the gate `if` +
     `_ctxCoordinator.Start`) with:

     ```csharp
     // tbc0: the prefetch was launched at listen-start (start arm); consume it here.
     var ctxPrefetch = _ctxSequencer?.RecordingStopped();
     ```

     Keep the local name `ctxPrefetch` so ALL downstream code (:933-971) is untouched
     (`WindowContextPrefetchHandle?` — same nullable shape as before).
     `settingsAtStop` was read ONLY for the gate — remove it with the block.

  8. TOGGLE stop arm: same replacement at :1333-1349 (`var ctxPrefetch2 = ...`).

  9. BOTH silence-drop paths (:766, :1392): next to each
     `_ctxCoordinator?.CancelAndClear();` add `_ctxSequencer?.Clear();` (book hygiene:
     the dropped dictation's handle must not linger until the next start).

  10. In BOTH stop arms, after the successful cleanup run where `timing.CtxSrc` is
      stamped (hold :970-971, toggle :1593-1594), add
      `timing.CtxWaitMs = result.WindowContextWaitMs;` /
      `timing2.CtxWaitMs = result2.WindowContextWaitMs;` with a one-line comment
      ("tbc0: ≈0 once the prefetch launches at listen-start"). Catch paths: no stamp
      (matches ctx_src).

  11. `Dispose` (:1945-1995): inside the lifecycle-gate body, right after
      `_hotkeyReadiness.Disable();` (:1947), add:

      ```csharp
      // tbc0: with listen-start launch, a teardown mid-recording would otherwise
      // leave a running prefetch burst (stop-launch left one only stop→consume).
      _ctxCoordinator?.CancelAndClear();
      _ctxSequencer?.Clear();
      ```

  12. Coordinator docstrings (pure file, no #if): update the two STALE timing
      statements: class `<summary>` ("after the move to recording-stop" → listen-start
      launch via `WindowContextListenStartSequencer`, kata tbc0; lifecycle text kept)
      and `Start`'s docstring ("Call at recording STOP: ..." → "Call at recording
      START, after OnRecordingStart, against the start-captured hwnd; the returned
      handle is consumed at stop — normally via WindowContextListenStartSequencer").
      No code change.

**Files:**
- Create: `src/Winpepper.Platform/WindowContext/WindowContextListenStartSequencer.cs`
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (#if WINDOWS — verified by the
  Windows gate, not by Linux builds)
- Modify: `src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs`
  (docstrings only)
- Test: `tests/Winpepper.Platform.Tests/WindowContext/WindowContextListenStartSequencerTests.cs`

**Interfaces:**
- Consumes: `WindowContextListenStartPolicy.ShouldStart(bool, bool, string?, bool)`
  (Task 1 — evaluated in PipelineHost's helper; Cleanup is reachable from App, which
  references everything); `CleanupResult.WindowContextWaitMs` +
  `DictationTimingSummary.CtxWaitMs` (Task 2); the existing
  `WindowContextPrefetchCoordinator` API (unchanged); `_ctxCoordinator`, `_ctxHwndAtStart`,
  `settingsForStream(2)`, `_activeCleanupPromptFormat` (:135), `ForegroundWindow.Handle()`.
- Produces: `WindowContextListenStartSequencer` (RecordingStarted/RecordingStopped/Clear)
  — also consumed by Task 4's regime measurement; `LaunchPrefetchAtListenStart(AppSettings)`;
  the listen-start launch `LogInformation` (R1 evidence).

**Test cases (`WindowContextListenStartSequencerTests.cs` — REAL behavioral coverage of the
production sequencing object; these FAIL before the class exists, a genuine RED):**
- `RecordingStarted_WithStartTrue_LaunchesNow_AndRecordingStoppedHandsItOverOnce`: a spy
  start-func records invocation count/moment; after RecordingStarted(true, hwnd): the spy
  ran exactly once; RecordingStopped() returns THAT handle (same reference) and a second
  RecordingStopped() returns null.
- `RecordingStarted_WithStartFalse_LaunchesNothing_AndStopGetsNull`: spy count stays 0
  across RecordingStarted(false, hwnd) + RecordingStopped() → null. *(The wiring-level
  "no OCR work when cleanup is disabled" evidence: PipelineHost maps
  ShouldStart(cleanupEnabled: false, ...) → false; the proof that false → no launch lives
  here; the proof that cleanup-false → false lives in Task 1.)*
- `StoppedHandle_UnaffectedByNextOnRecordingStart_WhenCompleted` (plus ruling): first
  dictation's completed handle survives the next OnRecordingStart UNcancelled; a
  never-completing first handle is cancelled by it (existing 1a ruling, preserved under
  listen-start timing; drive via the real coordinator underneath the sequencer).
- `RecordingStarted_Again_OverwritesTheBook` (rapid re-dictation shape): two starts,
  second handle returned by RecordingStopped; first handled per coordinator ruling.
- `Clear_DropsTheBook`: RecordingStarted(true) → Clear() → RecordingStopped() == null.
- The launch-moment assertion inside test 1 uses ordering evidence (invoke log entries
  "start-called" recorded by the spy vs the test's own sequencing notes) — the class
  has no stop-time Start call path at all: RecordingStopped() contains no start-func
  invocation (assert spy count stays 1 across RecordingStopped).
- Regional structural wiring assertions (Task 3 Step 5 — location-aware, pasted into the
  task report):
  - `awk '/case HotkeyEventKind.HoldDown:/,/case HotkeyEventKind.HoldUp:/' src/Winpepper.App/Hosting/PipelineHost.cs | grep -c "LaunchPrefetchAtListenStart(settingsForStream)"` == 1
  - `awk '/case HotkeyEventKind.Toggle:/,0' src/Winpepper.App/Hosting/PipelineHost.cs | grep -c "LaunchPrefetchAtListenStart(settingsForStream2)"` == 1
  - `awk '/case HotkeyEventKind.HoldUp:/,/case HotkeyEventKind.Cancel:/' src/Winpepper.App/Hosting/PipelineHost.cs | grep -c "ctxSequencer?.RecordingStopped()"` == 1
    and `... | grep -c "settingsAtStop"` == 0
  - `awk '/case HotkeyEventKind.Toggle:/,0' src/Winpepper.App/Hosting/PipelineHost.cs | grep -c "ctxSequencer?.RecordingStopped()"` == 1 and settingsAtStop2 == 0 in that region
  - `grep -c "ctxSequencer?.Clear()" src/Winpepper.App/Hosting/PipelineHost.cs` == 4
    (cancel + both silence-drops + dispose)
  - `grep -c "window-context prefetch started at listen-start" src/Winpepper.App/Hosting/PipelineHost.cs` == 1
  - `grep -c "RecordingStarted(" src/Winpepper.App/Hosting/PipelineHost.cs` == 1 (helper only)

**Why the arms are not runtime-tested on Linux (documented exception):** PipelineHost is
`#if WINDOWS`, constructs a real `HotkeyHook`, and no existing test constructs it (no
precedent); the repo's established verification for this exact file and these exact arms
(precedent: ca20aa2) is pure tests + review + the Windows gate. Here the SEQUENCING
itself is now pure and behavior-tested (the arms are one-line delegations), plus
region-aware structural greps pin each delegation's location and argument.

- [ ] **Step 1: Write the failing behavioral tests**

Write `WindowContextListenStartSequencerTests.cs` (namespace
`Winpepper.Platform.Tests.WindowContext`, Shouldly + xUnit, real
`WindowContextPrefetchCoordinator` under the sequencer with spy/never-completing start
funcs as specified).

- [ ] **Step 2: Run the tests and verify the intended failure**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected RED: build FAIL — `WindowContextListenStartSequencer` does not exist (missing
type errors), proving the tests reference genuinely missing behavior.

- [ ] **Step 3: Add the minimal production implementation**

Create the sequencer exactly as specified; apply the twelve PipelineHost edits; apply the
two coordinator docstring updates. Do NOT touch anything else in the pipeline bodies
(233p overlap). Diff budget: PipelineHost.cs roughly +60/−30; sequencer +85; tests +140.

- [ ] **Step 4: Run the focused test**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -class Winpepper.Platform.Tests.WindowContext.WindowContextListenStartSequencerTests
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```
Expected: class pass + whole assembly 0 failures (383 baseline + 5 = 388). Fallback if
`-class` errors: use only the whole-assembly run.

- [ ] **Step 5: Refactor + verify structure while green**

No behavior refactor — surgical by constraint (R5). RUN the regional structural wiring
assertions listed above; paste outputs into the task report. Then self-review the diff
exclusively for: both-arms symmetry, no `settingsAtStop(2)` left, downstream locals
(`ctxPrefetch`/`ctxPrefetch2`) intact, no accidental drift of neighbor lines.

---

### Task 4: Regime-measured wait evidence + full verification + acceptance note

**Requirements served:** R1, R2, R3, R4, R5

**Behavior:**
- New IntegrationTests file `tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs`
  (Linux-runnable, no #if) drives the REAL production objects —
  `WindowContextListenStartSequencer` + `WindowContextPrefetchCoordinator` +
  `CleanupRunner` (fake backend per the existing WindowContextConsumedStampTests
  pattern) — through the two launch-time REGIMES, measuring the runner's actual
  `WindowContextWaitMs` under each. A real `Task.Delay`-shaped standing-in for the
  UIA/OCR burst is inherent: real OCR cannot run on Linux CI, and the same tests run
  again on the Windows gate. Each test prints its measurement via `ITestOutputHelper`;
  the implementer copies them into the acceptance evidence note. Upper bounds are
  generous (scheduler tolerance); lower bounds carry the signal:
  - `StopLaunchRegime_PrefetchOutlivesAsrFinish_CleanupWaitsTheRemainder`: simulate today —
    do NOT launch at "listen-start"; at "stop" launch a 700 ms context task via the
    coordinator; simulate a 350 ms streaming finish (`await Task.Delay(350)`); then run
    the runner → consumed true; measured `WindowContextWaitMs` ≈ 350; assert [250, 1500].
  - `ListenStartRegime_PrefetchReadyAtCleanupStart_CleanupWaitsNothing`: simulate the new
    regime THROUGH the sequencer — `RecordingStarted(true, hwnd)` with a 700 ms task,
    `await Task.Delay(1850)` (utterance + finish), `RecordingStopped()` → runner →
    consumed true; `WindowContextWaitMs < 250`.
  - `StopLaunchRegime_FastFinish_DropsContextAfterBudget`: 700 ms task launched at "stop",
    350 ms finish, wait budget 400 ms → consumed false; `WindowContextWaitMs` in
    [300, 1500] (today's bounded wait-and-drop tail).
  The deltas (≈350 ms → <250 ms; the dropped-context case eliminated whenever the
  utterance outlives the prefetch) are the mechanism-level measured reduction. Honest
  framing for the note: an agent cannot dictate into the live app, so the end-to-end
  confirmation is the owner's timing-line readout (procedure included), and these numbers
  are the mechanism measurement on production code.
- Then the complete verification receipt + the acceptance-evidence note.

**Files:**
- Test: `tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs`
- Logs (not committed): `<logs>/reports/acceptance-evidence.md`

**Interfaces:**
- Consumes: Task 2's `WindowContextWaitMs`; Task 3's `WindowContextListenStartSequencer`;
  the existing EchoBackend / fixture pattern in
  `tests/Winpepper.IntegrationTests/WindowContextConsumedStampTests.cs`.
- Produces: measured numbers for R3's acceptance evidence.

**Test cases:** the three regime tests above, plus the complete verification:
- Linux: `./scripts/linux-tests.sh` → exit 0, `LINUX SUITE: GREEN`, counts recorded.
- Windows: `./scripts/windows-gate.sh` from the worktree → exit 0 + `GATE: GREEN`.

- [ ] **Step 1: Write the measurement tests**

Write `WindowContextListenStartLatencyTests.cs` with the three regime tests. Raw
transcripts ≥4 words; backend output shares their content words (passes the runner's
plausibility gates); `WindowContextEnabled = true`; explicit `WindowContextWait` per
case (2 s / 2 s / 400 ms).

- [ ] **Step 2: Run the measurement tests**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
dotnet build tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.IntegrationTests/bin/Release/net9.0/Winpepper.IntegrationTests.dll -class Winpepper.IntegrationTests.WindowContextListenStartLatencyTests
```
Expected: build OK + 3 passing; capture the three measured waits from the test output
(fallback if `-class` errors: whole assembly with `-notrait "Platform=Windows"`;
baseline 4 + 3 = 7). Anti-vacuity (state in the task report): every assertion is a
numeric bound on the runner-measured `WindowContextWaitMs`; without Task 2/3 the file
doesn't compile, and a broken measurement (null) falls outside every bound — the tests
cannot pass vacuously.

- [ ] **Step 3: Write the acceptance-evidence note** to
`/home/dan/code/winpepper/.worktrees/.the-usual-logs/tbc0-ocr-listen-start/reports/acceptance-evidence.md`
mapping kata tbc0 acceptance → evidence:
(1) OCR begins at listening start: the [INF] launch line; sequencer behavior tests
(launch recorded during RecordingStarted, none during RecordingStopped); region greps
pinning both arms' delegations; gate-compiled wiring;
(2) measured reduction: the three regime numbers, plus the owner live-dictation readout
procedure (grep `dictation timing` for `ctx_wait=` ≈ 0 with cleanup+context enabled;
confirm `native_max <= 250` / `native_over250 = 0` unchanged);
(3) no OCR when cleanup disabled: Task-1 cleanup-false policy case + sequencer
start-false-launches-nothing test + delegated `WindowContextPrefetchGate`;
and the recorded R4 rulings/side effects (start-time gate evaluation, hwnd-zero ctx_src
omission, silent-tap burst, dispose hygiene).

- [ ] **Step 4: Run the Linux suite (pre-commit gate for this task's commit)**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd /home/dan/code/winpepper/.worktrees/tbc0-ocr-listen-start
./scripts/linux-tests.sh
```
Expected: exit 0, `LINUX SUITE: GREEN`; record all 9 counts + grand total. Commit only
when green.

- [ ] **Step 5: Commit the measurement tests**

```bash
git add tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs
git commit -m "test(integration): measure window-context wait under stop-launch vs listen-start regimes (kata tbc0)"
```

- [ ] **Step 6: Run both gates against the committed HEAD**
- Linux suite re-run at the committed HEAD (fast: incremental) — record HEAD SHA + counts.
- `./scripts/windows-gate.sh` (20–30 min) → exit 0 + `GATE: GREEN`; record receipts.
(The acceptance-evidence note lives in the logs, uncommitted. The committed HEAD verified
here must equal the HEAD the final delta review inspects.)

---

## Self-review notes (planning stage)

- Spec coverage: R1→Tasks 3,4; R2→Tasks 1,3,4; R3→Tasks 2,3,4; R4→Tasks 1,3; R5→all.
  Every task serves named requirements; no task serves none.
- No silent deferrals: no mocks/stubs stand in for production behavior — CleanupRunner
  tests run the real runner with a fake BACKEND (existing repo fixture pattern); the
  sequencing itself is a pure production object with genuine behavioral tests (real RED);
  the PipelineHost arms are one-line delegations verified by region greps + gate.
- Genuine RED exists for every pure component (policy, runner telemetry, timing format,
  sequencer). Only the PipelineHost delegation lines themselves have no Linux Red
  (precedent-documented, compensated by region greps + Windows gate + review) — recorded
  as the no-useful-Red exception.
- Known internal consistency: Task 3 depends on Task 1 (`ShouldStart`) and Task 2
  (`WindowContextWaitMs`, `CtxWaitMs`); Task 4 depends on all three. Execute in order.
- Review-round-1/round-2 fixes folded in: SDK-prefixed self-contained commands; full
  suite required before EVERY commit (incl. docs commits and Task 4); nullable-safe
  helper (coordinator-null check in wiring, not the policy); sequencing moved into pure
  `WindowContextListenStartSequencer` with real behavioral tests; measured regime
  before/after evidence through real production objects; generous upper timer bounds
  (lower bounds carry the signal); hwnd-zero `ctx_src` omission recorded deliberately.
UNRESOLVED COVERAGE GAPS: none.
