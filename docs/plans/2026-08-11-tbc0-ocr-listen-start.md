# Start OCR Prefetch at Listen-Start Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** The window-context prefetch (UIA read with OCR fallback) launches the moment
listening starts — on both the hold and toggle paths — so the snapshot is ready before
transcription finishes and cleanup no longer waits for it after the user stops speaking.

**Architecture:** A new pure policy class (`WindowContextListenStartPolicy`, mirroring
the `WindowContextPrefetchGate` precedent, commit 0bbeceb) holds the start-time launch
decision and is unit-tested on Linux. The Windows-only wiring in `PipelineHost.cs`
(relocate the gated `_ctxCoordinator.Start(_ctxHwndAtStart)` from each stop arm to each
start arm, keep a per-dictation handle field, add one launch log line) stays surgical.
New `ctx_wait=` timing telemetry (measured inside `CleanupRunner`, surfaced on
`CleanupResult`, formatted by pure `DictationTimingSummary`) makes the post-stop latency
reduction directly measurable per dictation.

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
cd <worktree>
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
cd <worktree>
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
`WindowContextWait = 50 ms`, so construct explicit options per case):**
- CleanupRunner (Linux): context task completing after ~250 ms
  (`Task.Run(async () => { await Task.Delay(250); return "ctx"; })`) with
  `WindowContextWait = TimeSpan.FromSeconds(2)` and a fake backend returning a
  plausible cleanup → `ConsumedWindowContext == true`, `WindowContextWaitMs` in [50, 450].
- CleanupRunner: context task that never completes with `WindowContextWait = 500 ms` →
  `ConsumedWindowContext == false`, `WindowContextWaitMs` in [450, 700] (full budget
  waited; upper bound tolerates timer coarseness).
- CleanupRunner: already-complete context task (`Task.FromResult<string?>("ctx")`) →
  `WindowContextWaitMs < 100`, consumed true.
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
cd <worktree>
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
cd <worktree>
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
cd <worktree>
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

### Task 3: PipelineHost wiring — launch at listen-start (both arms) + lifecycle hygiene

**Requirements served:** R1, R2, R4, R5

**Behavior:**

- `PipelineHost` (src/Winpepper.App/Hosting/PipelineHost.cs, #if WINDOWS) gains one
  per-dictation field beside `_ctxHwndAtStart` (:107), plus ONE private helper (single
  home for the launch so the two start arms cannot drift — review-round-1 hardening):

  ```csharp
  // tbc0: the listen-start-launched prefetch for THIS dictation (null when the
  // policy skipped). BOTH start arms assign it (via TryLaunchListenStartPrefetch);
  // BOTH stop arms + the cancel arm clear it — same discipline as
  // _lastSessionPrerollMs.
  private Winpepper.Platform.WindowContext.WindowContextPrefetchHandle? _ctxPrefetchAtStart;

  /// <summary>tbc0: launch this dictation's window-context prefetch AT LISTEN-START
  /// (supersedes 1a's stop-launch; primary ASR runs in the subprocess worker since
  /// fb1f538, so the burst no longer starves in-process native calls). The gate —
  /// including the cleanup-disabled skip — is evaluated HERE on start-time
  /// settings; a mid-dictation settings change applies from the next dictation
  /// (staleness ruling, kata tbc0). Call ONLY after
  /// _ctxCoordinator?.OnRecordingStart() and the _ctxHwndAtStart capture, so the
  /// new handle is not cancelled by its own dictation's start and reads the
  /// right window. Returns null when the coordinator is absent or the policy
  /// skips (ctx_src/ctx_wait stay omitted, exactly as when the feature is off).
  /// </summary>
  private Winpepper.Platform.WindowContext.WindowContextPrefetchHandle? StartListenStartPrefetch(
      AppSettings settingsAtStart)
  {
      if (_ctxCoordinator is null
          || !Winpepper.Cleanup.WindowContextListenStartPolicy.ShouldStart(
              settingsAtStart.CleanupEnabled,
              settingsAtStart.CleanupWindowContextEnabled,
              _activeCleanupPromptFormat?.Invoke(),
              _ctxHwndAtStart != IntPtr.Zero))
      {
          return null;
      }
      var handle = _ctxCoordinator.Start(_ctxHwndAtStart);
      _log.LogInformation("window-context prefetch started at listen-start {SessionId}", _currentSessionId);
      return handle;
  }
  ```

  `AppSettings` resolves via the existing `using Winpepper.Core.Settings;` (:8). The
  `_ctxCoordinator is null` check lives HERE (not in the policy) so nullable-flow
  analysis proves the `_ctxCoordinator.Start(...)` deref safe
  (Directory.Build.props promotes nullable warnings to errors).

- HOLD start arm — after `_ctxHwndAtStart = ...ForegroundWindow.Handle();` (:675),
  reusing the arm's existing settings snapshot (`settingsForStream`, :590):

  ```csharp
  // tbc0: listen-start launch (supersedes 1a's stop-launch) — see StartListenStartPrefetch.
  _ctxPrefetchAtStart = StartListenStartPrefetch(settingsForStream);
  ```

- TOGGLE start arm — identical one-liner after `_ctxHwndAtStart = ...` (:1302), passing
  `settingsForStream2` (:1217).

- Cancel arm (:1166-1185): next to `_ctxCoordinator?.CancelAndClear();` (:1169) add
  `_ctxPrefetchAtStart = null;` (handle is dead; field must not leak into the next
  dictation).

- HOLD stop arm: replace the at-stop launch block (:706-722 — the "1a: launch the
  window-context prefetch AT STOP" comment, `ctxPrefetch = null`, the `settingsAtStop`
  read, the prompt-format comment, the gate `if` + `_ctxCoordinator.Start`) with:

  ```csharp
  // tbc0: the prefetch was launched at listen-start (start arm); consume it here.
  var ctxPrefetch = _ctxPrefetchAtStart;
  _ctxPrefetchAtStart = null;
  ```

  Keep the local name `ctxPrefetch` so ALL downstream code (:933-971) is untouched.
  `settingsAtStop` was read ONLY for the gate — remove it with the block.

- TOGGLE stop arm: same replacement at :1333-1349 (locals `ctxPrefetch2`,
  `settingsAtStop2`); downstream :1556-1594 untouched.

- In BOTH stop arms, after the successful cleanup run where `timing.CtxSrc` is stamped
  (hold :970-971, toggle :1593-1594), add `timing.CtxWaitMs = result.WindowContextWaitMs;`
  / `timing2.CtxWaitMs = result2.WindowContextWaitMs;` respectively, with a one-line
  comment ("tbc0: ≈0 once the prefetch launches at listen-start"). Catch paths: no stamp
  (matches ctx_src behavior).

- Also update the stale NOTE comment at :103-106 next to `_ctxCoordinator` ("prefetch
  launched at recording STOP") to state the new listen-start launch truthfully
  (lifecycle still owned by the coordinator).

- `Dispose` (:1945-1995): inside the lifecycle-gate body, right after
  `_hotkeyReadiness.Disable();`, add:

  ```csharp
  // tbc0: with listen-start launch, a teardown mid-recording would otherwise
  // leave a running prefetch burst (stop-launch left one only stop→consume).
  _ctxCoordinator?.CancelAndClear();
  ```

- Coordinator docs (pure file, compiles on Linux — no #if): update the two STALE timing
  statements to the new contract: `WindowContextPrefetchCoordinator` class summary
  ("after the move to recording-stop" → listen-start launch per kata tbc0) and `Start`'s
  docstring ("Call at recording STOP" → "Call at recording START, after OnRecordingStart,
  against the start-captured hwnd; the returned handle is consumed at stop"). No API
  change.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (#if WINDOWS — verified by the
  Windows gate, not by Linux builds)
- Modify: `src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs`
  (docstrings only)
- Test: `tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchCoordinatorTests.cs`

**Interfaces:**
- Consumes: `WindowContextListenStartPolicy.ShouldStart(bool, bool, string?, bool)`
  (Task 1); `CleanupResult.WindowContextWaitMs` + `DictationTimingSummary.CtxWaitMs`
  (Task 2); existing `_ctxCoordinator`, `_ctxHwndAtStart`, `settingsForStream(2)`,
  `_activeCleanupPromptFormat` (:135), `ForegroundWindow.Handle()`.
- Produces: `_ctxPrefetchAtStart` field discipline; `StartListenStartPrefetch(AppSettings)`;
  the listen-start launch `LogInformation` line (acceptance evidence for R1).

**Test cases:**
- New named Linux tests in `WindowContextPrefetchCoordinatorTests.cs` pinning the NEW wiring
  contract (these pass against the unchanged coordinator — it is timing-agnostic; their
  value is pinning the wiring contract, see the documented no-Linux-RED exception below):
  - `StartLaunchedAtRecordingStart_IsNotCancelledByOwnStart_AndStampsUia`:
    `OnRecordingStart()` → `Start(hwnd)` (listen-start order) → consume:
    `handle.CancellationRequested == false`, `coordinator.Current` is the handle, the task
    completes, and `WindowContextStamp.CtxSrc(consumedWindowContext: true, handle.Task)`
    == `"uia"`.
  - `StartLaunchedAtRecordingStart_NextRecordingStartStillCancelsIt`: the first
    (never-completing) prefetch is cancelled by the SECOND `OnRecordingStart()`
    (1a ruling preserved under the new launch point).
- Existing coordinator tests must pass unchanged (their OnRecordingStart→Start… sequences
  remain legal lifecycle shapes).
- Structural wiring assertions (executable static checks compensating the un-Linux-testable
  arms — run in Task 3 Step 5, evidence in the task report):
  - `grep -c "StartListenStartPrefetch(" src/Winpepper.App/Hosting/PipelineHost.cs` == 3
    (helper definition + BOTH start-arm calls);
  - `grep -c "_ctxPrefetchAtStart = " src/.../PipelineHost.cs` == 5 (2 start assigns,
    2 stop clears, 1 cancel clear);
  - `grep -n "settingsAtStop" src/.../PipelineHost.cs` → NO matches (stop-time gate reads
    gone from both arms);
  - `grep -c "_ctxCoordinator.Start(" src/.../PipelineHost.cs` == 1 (only inside the helper);
  - `grep -c "CtxWaitMs = " src/.../PipelineHost.cs` == 2 (both stop arms stamp);
  - `grep -c "window-context prefetch started at listen-start" src/.../PipelineHost.cs` == 1
    (one log site, inside the helper).

**Why the arms are not runtime-tested on Linux (documented exception):** PipelineHost is
`#if WINDOWS`, constructs a real `HotkeyHook`, and no existing test constructs it (no
precedent); the repo's established verification for this exact file and these exact arms
(precedent: ca20aa2, which moved the launch the other way) is pure lifecycle/policy tests
+ review + the Windows gate. This plan adds: the single-home helper (both arms call it,
so the drift-prone edit cannot diverge), executable structural greps, and owner
live-dictation timing-line readout (Task 4).

- [ ] **Step 1: Write the failing behavioral test**

Add the two coordinator contract tests listed above. They exercise only the pure
coordinator — they PASS against the current code; their value is pinning the wiring
contract. Since RED is unavailable for a pure wiring change (PipelineHost is #if WINDOWS),
record this explicitly in the implementer report and rely on the Windows gate + structural
greps for the wiring's evidence (usual-test-driven-development's documented exception for
changes with no useful Linux Red step).

- [ ] **Step 2: Run the test**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd <worktree>
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -class Winpepper.Platform.Tests.WindowContext.WindowContextPrefetchCoordinatorTests
```

Expected: PASS (contract pin; documented no-RED exception). Fallback if `-class` errors:
run the whole assembly with `-notrait "Platform=Windows"` (383 baseline + 2 = 385).

- [ ] **Step 3: Add the minimal production implementation**

Apply the PipelineHost edits exactly as enumerated above (field, `StartListenStartPrefetch`
helper, both start-arm one-liners, cancel line, both stop-arm replacements, both
`CtxWaitMs` stamps, stale NOTE comment at :103-106, Dispose hygiene) and the two
coordinator docstring updates. Do NOT touch anything else in the pipeline bodies
(233p overlap). Diff budget for PipelineHost.cs: roughly +55/−30 lines.

- [ ] **Step 4: Run the focused test**

Same command block as Step 2. Expected: PASS, 0 failures.

- [ ] **Step 5: Refactor + verify structure while green**

No behavior refactor — surgical by constraint (R5). Instead, RUN the structural wiring
assertions from the Test cases section (the six greps) and paste their output into the
task report, then self-review the diff exclusively for: both-arms symmetry, no
`settingsAtStop(2)` uses left, downstream locals (`ctxPrefetch`/`ctxPrefetch2`) intact,
no accidental drift of neighbor lines (maximizes 233p/8kg3 merge hygiene).

- [ ] **Step 6: Run broader verification (pre-commit suite)**

Run:
```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd <worktree>
./scripts/linux-tests.sh
```

Expected: exit 0, `LINUX SUITE: GREEN`. (PipelineHost changes are invisible to Linux;
green proves the shared-file edits — coordinator docstrings, all Task-1/2 code — are
clean.) Commit only when green.

- [ ] **Step 7: Commit the task**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs src/Winpepper.Platform/WindowContext/WindowContextPrefetchCoordinator.cs tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchCoordinatorTests.cs
git commit -m "feat(app): launch window-context prefetch at listen-start, consume at stop (both hotkey arms) (kata tbc0)"
```

---

### Task 4: Full verification, measured before/after wait evidence, acceptance note

**Requirements served:** R1, R2, R3, R4, R5

**Behavior:**
- New IntegrationTests file `tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs`
  (Linux-runnable; real code path: real `WindowContextPrefetchCoordinator` + real
  `CleanupRunner` with the fake backend pattern from WindowContextConsumedStampTests)
  MEASURES the wait the change removes, in the two timing shapes — this is the
  mechanism-level "measured reduction" evidence for acceptance #2 (end-to-end live
  confirmation stays with the owner's timing lines, procedure stated in the evidence
  note; an agent cannot dictate into the live app):
  - `StopLaunchShape_ContextOutlivesAsrFinish_CleanupWaitsTheRemainder`: context task
    completing 400 ms after RunAsync entry (today's shape: prefetch (~hundreds of ms)
    launched at stop still running past a fast streaming finish), generous wait budget
    → consumed true; record `WindowContextWaitMs` (expect ≈400 ms; assert [300, 600]).
  - `ListenStartShape_ContextReadyAtCleanupStart_CleanupWaitsNothing`: context task
    completed before RunAsync entry (the new shape: launched at listen-start, utterance
    outlived it) → consumed true; record + assert `WindowContextWaitMs < 50`.
  - `StopLaunchShape_TightBudget_ContextDropped`: 400 ms-late task with the production
    500 ms budget cut short (e.g. 200 ms — a streaming finish faster than the prefetch)
    → consumed false (ctx_src=none today); record `WindowContextWaitMs` ≈ 200.
  Each test writes its measured wait via `ITestOutputHelper`; the implementer copies the
  three numbers into the acceptance evidence note.
- Then the complete verification receipt + the acceptance-evidence note.

**Files:**
- Test: `tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs`
- Logs (not committed): `<logs>/reports/acceptance-evidence.md`

**Interfaces:**
- Consumes: Task 2's `WindowContextWaitMs`; the existing EchoBackend pattern in
  `tests/Winpepper.IntegrationTests/WindowContextConsumedStampTests.cs`.
- Produces: pursuit-level numbers for R3's acceptance evidence.

**Test cases:** the three latency-shape tests above, plus the complete verification:
- Linux: `./scripts/linux-tests.sh` → exit 0, `LINUX SUITE: GREEN`, counts recorded.
- Windows: `./scripts/windows-gate.sh` from the worktree → exit 0 + `GATE: GREEN`.

- [ ] **Step 1: Write the failing measurement tests**

Write `WindowContextListenStartLatencyTests.cs` with the three tests. Use
`WindowContextPrefetchCoordinator` driven in the listen-start/stop of the simulated
shapes and a real `CleanupRunner` whose fake backend echoes the transcript (mirror
WindowContextConsumedStampTests); supply the delayed/ready tasks as `RunAsync`'s
`windowContextTask` directly (pure `Task<string?>`s — the shapes capture the TIMING,
exactly what the wiring change alters).

- [ ] **Step 2: Run the measurement tests**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd <worktree>
dotnet build tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.IntegrationTests/bin/Release/net9.0/Winpepper.IntegrationTests.dll -class Winpepper.IntegrationTests.WindowContextListenStartLatencyTests
```
Expected: build OK + 3 PASSING tests (Task 2 already landed the property).
Anti-vacuity (state in the task report): every assertion is a numeric bound on
`WindowContextWaitMs`; if the Task-2 measurement were absent/broken the property would
be null and the bounds assertions would fail — the tests cannot pass vacuously.

- [ ] **Step 3: Record the measured numbers**

Run the three tests with `-class` (fallback: whole assembly), capture each measured wait
from the test output, and write `<logs>/reports/acceptance-evidence.md`: map each kata
acceptance item to evidence — (1) OCR begins at listening start: the [INF] launch line
(grep name + one example), policy + coordinator contract tests, gate-compiled wiring;
(2) measured reduction: the three shape numbers (before-shape wait ≈400 ms / budget-miss
≈200 ms; after-shape wait <50 ms), and the owner live-dictation readout procedure: verify
`ctx_wait=` ≈ 0 on real `dictation timing` lines with cleanup+context enabled, with
`native_max <= 250` / `native_over250 = 0` unchanged; (3) no OCR when cleanup disabled:
`WindowContextListenStartPolicyTests` cleanup-false case + delegation to
`WindowContextPrefetchGate`; plus the recorded R4 side effects (start-time gate
evaluation, hwnd-zero ctx_src omission, silent-tap burst).

- [ ] **Step 4: Commit the measurement tests** (so both gates verify the exact committed
  HEAD the final delta review inspects):

```bash
git add tests/Winpepper.IntegrationTests/WindowContextListenStartLatencyTests.cs
git commit -m "test(integration): measure window-context wait under stop-launch vs listen-start timing shapes (kata tbc0)"
```

- [ ] **Step 5: Run the Linux suite against committed HEAD**

```bash
export DOTNET_ROOT=/.dotnet; export PATH=/.dotnet:$PATH
cd <worktree>
./scripts/linux-tests.sh
```
Expected: exit 0, `LINUX SUITE: GREEN`; record HEAD SHA + all counts in the task report.

- [ ] **Step 6: Run the Windows gate against the same committed HEAD**
`./scripts/windows-gate.sh` (20–30 min). Expected: exit 0 + `GATE: GREEN`. Record receipts.
(The acceptance-evidence note lives in the logs, uncommitted.)

---

## Self-review notes (planning stage)

- Spec coverage: R1→Tasks 3,4; R2→Tasks 1,3,4; R3→Tasks 2,3,4; R4→Tasks 1,3; R5→all.
  Every task serves named requirements; no task serves none.
- No silent deferrals: no mocks/stubs stand in for production behavior — CleanupRunner
  tests run the real runner with a fake BACKEND (existing repo fixture pattern), and the
  PipelineHost wiring is verified by the Windows gate rather than a Linux-only stand-in.
- Task 3's Linux Red step is genuinely unavailable (pure wiring pin, Windows-only
  production edit); documented as the skill's no-useful-Red exception with the Windows
  gate as the compensating gate.
- Known internal consistency: Task 3 depends on Task 1 (`ShouldStart`) and Task 2
  (`WindowContextWaitMs`, `CtxWaitMs`); execute in order.
UNRESOLVED COVERAGE GAPS: none.
