# Paste Path Hardening Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Two hardening changes to the text-injection paste path: (1) detect an
elevated (UIPI-protected) foreground window before ANY injection run and park
the full text as a pending paste with a distinct pill status instead of
silently losing it, and (2) slow the inter-chunk pacing so the feed rate
matches slow-rendering apps' ~600 chars/s render rate, killing the bleed
backlog that sprays queued characters into a newly focused window on a
mid-paste switch.

**Architecture:** Both changes extend the existing guarded-injection
architecture: pure, Linux-testable deciders in `Winpepper.Platform/Injection/`
with injectable probe seams on `TextInjector` (exactly like `MidPasteDecider`
+ the `foregroundHwnd` seam), Win32 access in a per-surface
`LibraryImport` native class, and thin wiring in `PipelineHost`'s three
injection call sites. The pacing change is a constant retune plus doc/test
updates — the chunking loop, halt predicate, and `PacingWaiter` are unchanged.

**Tech Stack:** C# / .NET 9 (`net9.0;net9.0-windows10.0.19041.0` multi-target),
Win32 P/Invoke via `[LibraryImport]`, xUnit v3 (in-process runner via
`dotnet exec`, never `dotnet test`), Shouldly assertions, hand-rolled
`Func<>`/`Action<>` fakes (no mocking library).

## Global Constraints

- **Full-text pending-paste semantics:** interruption or elevated-target park
  ALWAYS holds the ENTIRE transcription in the pending slot — never a
  remainder. (Spec, verbatim constraint.)
- **Fail-open for transient observation failures:** "foreground gone /
  unobservable" ⇒ inject (existing behavior, preserved). "Foreground
  observable and elevated" (including access-denied on the process/token —
  the conservative reading) ⇒ park (new behavior). (Spec, verbatim
  constraint.)
- **All Linux tests green before every commit:** `./scripts/linux-tests.sh`
  must print `LINUX SUITE: GREEN`. Never use `dotnet test` (AGENTS.md — the
  VSTest host is unreliable on this machine); build `-c Release` then
  `dotnet exec <test dll>`.
- **Full Windows gate before any push:** `./scripts/windows-gate.sh` must
  print `GATE: GREEN`. It can fail transiently with UNC
  `retry should be performed` I/O errors if other builds run concurrently —
  if that signature appears, re-run in a quiet window or an isolated
  worktree; do NOT treat it as a code failure.
- **Deliberate constraint supersession (owner-approved in this task's spec):**
  the bleed-hardening plan's global constraint "feed rate floor ≥1600 nominal
  code units/s" and its HALT CONDITION 1 sentinel framing are superseded. The
  new design point is `TargetFeedUnitsPerSecond = 600` (~571 units/s nominal
  at 8-unit chunks / 14 ms pace — the pause is CEILING-derived from the 600
  target so the nominal feed never EXCEEDS it; stage-2 load-bearing ledger
  A1). The spec explicitly instructs: "the existing
  Windows pacing sentinel test must be retuned to prove the NEW floor, not
  the old one."
- **UX cost, quantified honestly:** a 458-char paste goes from ~0.3 s to
  ~0.8 s of send time (458 / 571 ≈ 0.80 s). In slow-rendering apps perceived
  duration is unchanged (the app remains the bottleneck). The pill-click
  UI-thread stall grows proportionally (predecessor ledger AD-3 accepted
  this; the follow-up if jank appears is background dispatch, NOT removing
  pacing — out of scope here).
- **Per-chunk halt predicate unchanged:** foreground/modifier/mouse-button
  checks in `GuardedInjectionRun` are not modified.
- `README.md` is the only end-user markdown doc; this plan under
  `docs/plans/` is a working/agent doc.
- **Explicit halt condition from the user:** if a FUNDAMENTAL problem with
  either change is identified during execution (e.g. the elevation query is
  not reliably possible from medium IL, the check cannot be made without
  breaking fail-open, or render-rate pacing provably cannot reduce redirect
  bleed), HALT and report — do not plan around it with a workaround.
- Keep commits focused and atomic; conventional commit messages.

## Load-Bearing Validation — ALREADY PERFORMED (2026-07-27)

The spec required three probes on the real Windows host before planning.
All three ran (medium-IL `powershell.exe` from WSL, user `DANDESKTOP\dan`,
`isAdminRole=False`; raw output at
`.worktrees/.the-usual-logs/paste-path-hardening/probe-results.txt`, script at
`elev-probe.ps1` alongside):

- **(a) Medium IL CAN query TokenElevation on an elevated process — CONFIRMED.**
  The full `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` →
  `OpenProcessToken(TOKEN_QUERY)` → `GetTokenInformation(TokenElevation)`
  chain succeeded against an elevated user-session process
  (`remoting_host pid=5716 : elevated=True`). SYSTEM/service processes
  (winlogon, lsass, svchost, dwm, …) deny `OpenProcess` with err 5
  (ACCESS_DENIED) — which the spec-mandated conservative mapping treats as
  **Elevated ⇒ park**, so BOTH observed behaviors produce the correct park
  outcome for protected targets. No fundamental problem; no halt.
- **(b) Normal processes return not-elevated — CONFIRMED.** Every normal
  user app probed (`explorer`, `chrome`, `conhost`, `cmd`, `OpenConsole`,
  `1Password`, `sihost`, …) returned `elevated=False` — so the conservative
  denial mapping does NOT false-park ordinary targets.
- **(c) Per-call cost — CONFIRMED ≪ 5 ms budget.** 200-call averages:
  0.0032 ms/call (full success chain, explorer), 0.0009 ms/call (early
  denial exit, winlogon). The check runs once per injection start.

Pacing math (change 2) is sound: backlog grows at (feed − render); at
~571 units/s feed vs the ~600 units/s claimed render rate, growth is ≤ zero,
collapsing worst-case bleed toward the true in-flight chunk (≤ 8 code
units). The 14 ms pace is achievable — the existing `PacingWaiter`
high-resolution timer measured 5.22–5.37 ms on a 5 ms request
(bleed-hardening ledger B1), and 14 ms is coarser, not finer. **No
unresolved coverage gaps; no halt-worthy findings.**

## Load-Bearing Validation — STAGE 2 (workflow validation pass, 2026-07-27)

A second validation wave ran after planning; full ledger at
`.worktrees/.the-usual-logs/paste-path-hardening/load-bearing-ledger.md`
(8 verified, 1 falsified→fixed, 2 accepted). Findings already folded into
this plan:

- **A11 verified (docs, verbatim):** MS Learn `SendInput` reference: "This
  function fails when it is blocked by UIPI. Note that neither GetLastError
  nor the return value will indicate the failure was caused by UIPI
  blocking." The pre-check architecture (Tasks 2–4) is necessary, not
  optional.
- **A4/A3/A6 verified (HWND-first host probe, `hwnd-probe.ps1`):**
  `GetWindowThreadProcessId` succeeded on **877/877** windows including
  SYSTEM (dwm) and elevated-process windows — the fail-open branch never
  fired; the park/paste split was exactly right (135 park windows, all
  genuinely elevated/SYSTEM; 742 paste windows, zero ordinary apps
  false-parked); elevated consoles report `elevated=True` whether the HWND
  resolves to the client or the host. Accepted residuals (ledger W1–W3,
  D1): ApplicationFrameHost-owned UWP windows probe as AFH (medium-IL,
  fail-open — input flows via AFH); UIAccess processes are a rare blind
  spot; Win11 sudo-inline consoles probe medium and genuinely accept input
  (correct inject).
- **A2 verified (mechanism):** official docs confirm the system input queue
  routes each event to the focus window at DEQUEUE time — a
  queued-undelivered backlog CAN follow a focus change. Whether a given
  backlog redirects, drops, or lands in the old window varies; pacing
  shrinks the backlog to ~zero, which fixes the incident under **every**
  observed fate, and the per-chunk halt + full-text park prevents loss.
- **A1 accepted (ledger decision):** the ~600 chars/s render figure traces
  to a user eyeball estimate, unmeasured, and 8×1000/600 truncates to a
  13 ms pause = ~615 u/s nominal — ABOVE the claimed render rate, so the
  "backlog cannot grow" invariant would be arithmetically false. Decision:
  derive the pause by **ceiling division** (= 14 ms, ~571 u/s ≤ 600),
  making the invariant hold against the claimed rate. A live render-rate
  measurement needs owner approval (protocol preserved in
  `reports/validator-A1.md` §6).
- **A8 falsified → fixed:** the pill is a fixed 300-DIP window
  (`StatusPillLayout.cs:11`) whose text budget is ~209 DIP ≈ ~32 chars at
  FontSize 13 with `CharacterEllipsis`; the original 57-char admin copy
  lost its actionable half to elision. Copy shortened to
  `Admin window - switch & click` (29 chars) throughout Tasks 5–7.
- **A9 verified, guard is load-bearing:** `ci.yml`'s windows job filters
  tests by NAME only, so the new `Platform=Windows`-trait sentinels DO run
  on `windows-latest`, which executes **elevated** (UAC disabled). The
  `Assert.SkipWhen(Environment.IsPrivilegedProcess, ...)` guards in
  `ElevationProbeWindowsTests` are therefore required for CI green — never
  remove them.
- **A7/A5 verified:** no code path clobbers `StatusText` while a pending
  slot is held (setter private; all 15 writers audited — but the safety
  rests on the `HasPending` guards, and `NotifyPasteAttempted` is the one
  VM method not marshalled via `_ui.Post`: keep Task 6's exact call
  order); winpepper ships `asInvoker`, per-user MSI, HKCU autostart — it
  runs non-elevated (UAC-disabled admin machines degrade to a conservative
  park, never loss).

## Scope Check

Both changes touch the same subsystem (the guarded injection path in
`Winpepper.Platform/Injection/` + its `PipelineHost` call sites) and share
test files; one plan. The pacing change (Task 1) is independent of the
elevation change (Tasks 2–6) and lands first as a self-contained deliverable.

## File Structure

| File | Task | Responsibility |
|---|---|---|
| `src/Winpepper.Platform/Injection/TextInjector.cs` (modify) | 1, 4 | Constants retune + docs; new elevation seam + pre-check |
| `src/Winpepper.Platform/Injection/PacingWaiter.cs` (modify) | 1 | Doc comment retune (5 ms references) |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` (modify) | 1, 4 | Retuned pacing pins; new elevated-target tests |
| `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs` (modify) | 1 | Retuned Windows pacing sentinels (new floor) |
| `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs` (create) | 2 | Pure pre-injection decision + `ForegroundElevation` / `ElevatedTargetDecision` enums |
| `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs` (create) | 2 | Decider unit tests (Linux) |
| `src/Winpepper.Platform/Injection/ElevationNative.cs` (create) | 3 | `LibraryImport` surface: GetWindowThreadProcessId / OpenProcess / OpenProcessToken / GetTokenInformation / CloseHandle |
| `src/Winpepper.Platform/Injection/ElevationProbe.cs` (create) | 3 | Managed probe: HWND → `ForegroundElevation`, conservative-denial + fail-open mapping |
| `tests/Winpepper.Platform.Tests/Injection/ElevationProbeTests.cs` (create) | 3 | Linux fail-open tests |
| `tests/Winpepper.Platform.Tests/Injection/ElevationProbeWindowsTests.cs` (create) | 3 | Windows-gate sentinels (chain works, denial maps, cost budget) |
| `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs` (modify) | 4 | New `BlockedElevated` member |
| `src/Winpepper.Core/Pending/PendingPasteReason.cs` (create) | 5 | Reason enum selecting pill copy |
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (modify) | 5 | Reason-aware `EnterPendingPaste` + `ShowPendingPasteStatus` |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs` (modify) | 5 | Elevated pill-copy tests |
| `src/Winpepper.App/Hosting/PipelineHost.cs` (modify) | 6 | `BlockedElevated` branches at all 3 injection sites + `TryPastePending` status sync |

Design notes locked in here:

- **No new `SessionStage`.** The pill's clickability, visuals, and
  never-auto-hide behavior all key off `Stage == SessionStage.PendingPaste`
  (`StatusPillWindow.OnVmChanged`), and `StatusText` renders verbatim into
  the pill. A new stage would force changes in `PillAnimationMap`,
  `SessionStages`, and the `OnVmChanged` arms for zero behavioral gain. The
  elevated park reuses `PendingPaste` with different status copy.
- **The elevation check lives inside `TextInjector.TryInjectGuarded`**, right
  after the `hwndAtSendStart` capture and BEFORE `NeutralizeHeldModifiers()`
  — so a blocked target never receives even the modifier-neutralizing
  KEYUPs, all three entry points (hold arm, toggle arm, pill-click retry)
  are covered by one site, and the check uses the same HWND the mid-paste
  guard baselines against. A distinct `InjectionRunOutcome.BlockedElevated`
  gives `PipelineHost` the signal for distinct pill copy/logging. All three
  `PipelineHost` sites MUST be updated in the same task the enum member
  ships in a release — until Task 6, `BlockedElevated` cannot be produced in
  production because Tasks 4–5 only wire the Platform/Core layers and the
  probe defaults are only exercised on Windows (the App is only built by the
  Windows gate, which runs in Task 7 after all wiring is complete).
- **Conservative-denial mapping:** after the window's PID is obtained, ANY
  failure of `OpenProcess`/`OpenProcessToken`/`GetTokenInformation` maps to
  `Elevated` (⇒ park). Parking never loses text — the slot holds the full
  transcription and the pill stays clickable. Fail-open (`Unknown` ⇒ inject)
  applies only to "foreground gone/unobservable": HWND is 0, the window no
  longer resolves to a PID, non-Windows, or an unexpected managed exception.

---

### Task 1: Render-rate pacing retune (5 ms → 14 ms, ≤600 units/s feed)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs:29-49` (constants + XML docs)
- Modify: `src/Winpepper.Platform/Injection/PacingWaiter.cs:1-17` (doc comment)
- Modify: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs:118-133,146-156,183-209`
- Modify: `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs` (whole file)

**Interfaces:**
- Consumes: existing `TextInjector.ChunkCodeUnits` (= 8, unchanged),
  `PacingWaiter.Wait(int ms)` (unchanged).
- Produces: `internal const int TextInjector.TargetFeedUnitsPerSecond = 600`
  and `internal const int TextInjector.InterChunkPauseMs` now COMPUTED by
  CEILING division as
  `(ChunkCodeUnits * 1000 + TargetFeedUnitsPerSecond - 1) / TargetFeedUnitsPerSecond`
  (= 14 — truncating division would give 13 ms = ~615 units/s, ABOVE the
  600 target; ledger A1). Later tasks and
  tests reference `TextInjector.InterChunkPauseMs` symbolically.

- [ ] **Step 1: Retune the three Linux test pins (write the failing tests)**

In `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`:

(1) In `Guarded_Paces_Between_Chunks_Only` (line ~132), replace:

```csharp
        sleeps.ShouldBe(new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }); // 11 x TextInjector.InterChunkPauseMs
```

with:

```csharp
        sleeps.ShouldBe(new[] { 14, 14, 14, 14, 14, 14, 14, 14, 14, 14, 14 }); // 11 x TextInjector.InterChunkPauseMs (render-rate pace)
```

(2) Replace the whole `DesignPoint_FeedRateFloor_And_BleedBound` method
(lines ~146-156) with:

```csharp
    [Fact]
    public void DesignPoint_FeedRateCeiling_And_BleedBound()
    {
        // Spec constraint (paste-path-hardening, 2026-07-27): the nominal
        // feed rate must stay AT OR BELOW TargetFeedUnitsPerSecond so the
        // queued-but-undelivered backlog cannot grow against slow-rendering
        // apps (~600 chars/s claimed render rate) -- a mid-paste window
        // switch then leaks at most the true in-flight chunk. The pause is
        // CEILING-derived for exactly this reason: truncating division gave
        // 13 ms = ~615 units/s, ABOVE the target (stage-2 ledger A1). This
        // DELIBERATELY SUPERSEDES the bleed-hardening plan's ">= 1600"
        // floor (owner-approved). The feed must not collapse either, and
        // the worst-case bleed bound (<= 1 in-flight chunk, prior ledger
        // AD-1) must not regress past 8 code units.
        var nominalFeed = TextInjector.ChunkCodeUnits * 1000 / TextInjector.InterChunkPauseMs; // 571 at 8/14ms
        nominalFeed.ShouldBeLessThanOrEqualTo(TextInjector.TargetFeedUnitsPerSecond); // never exceed the render-rate target
        nominalFeed.ShouldBeGreaterThanOrEqualTo(500); // sanity floor: still responsive in fast apps
        TextInjector.ChunkCodeUnits.ShouldBeLessThanOrEqualTo(8);
    }
```

(3) In `Guarded_PillClick_ButtonStillDownAtStart_WaitsForRelease_ThenSendsAll`
(lines ~207-208), replace:

```csharp
        // Three 15 ms release-wait polls, then the single 5 ms inter-chunk pause.
        sleeps.ShouldBe(new[] { 15, 15, 15, 5 });
```

with:

```csharp
        // Three 15 ms release-wait polls, then the single 14 ms inter-chunk pause.
        sleeps.ShouldBe(new[] { 15, 15, 15, 14 });
```

- [ ] **Step 2: Build to verify the retuned tests cannot compile against the OLD constants**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — CS0117: `TextInjector` does not contain a definition
for `TargetFeedUnitsPerSecond` (Step 1's replacement test references it, but
the constant does not exist until Step 3). Do NOT run the test DLL at this
step: the build failed, so any binaries on disk are stale leftovers from a
previous build and a run against them would report a misleading PASS. The
behavioral red assertions (14 ms vs 5 ms pause, feed <= 600 target, trailing
14 ms) are proven by these same tests going green in Step 5 only after
Step 3's retune — with the OLD constants they cannot even compile.

- [ ] **Step 3: Retune the production constants and XML docs**

In `src/Winpepper.Platform/Injection/TextInjector.cs`, replace the block at
lines 29–49 (the `ChunkCodeUnits` doc+const through the `InterChunkPauseMs`
doc+const) with:

```csharp
    /// <summary>
    /// UTF-16 code units per guarded send chunk. Also the worst-case bleed
    /// bound: at most ~one in-flight chunk can land in a newly focused window
    /// when the user switches mid-paste (mid-paste focus fallback, AD-1 --
    /// hardened from 32 to 8 by the bleed-hardening task).
    /// </summary>
    internal const int ChunkCodeUnits = 8;

    /// <summary>
    /// Target feed rate for the guarded send, in UTF-16 code units per
    /// second. Chosen to match the observed render rate of slow-rendering
    /// target apps (~600 chars/s): when feed &lt;= render, the
    /// queued-but-undelivered BACKLOG cannot grow, so a mid-paste window
    /// switch can leak at most the true in-flight chunk
    /// (&lt;= <see cref="ChunkCodeUnits"/>) into the newly focused window.
    /// The previous 1600 units/s design point fed slow apps ~2.5x faster
    /// than they rendered; the growing backlog followed focus on a human
    /// click-switch and sprayed dozens of characters (paste-path-hardening,
    /// 2026-07-27 -- a deliberate, owner-approved supersession of the
    /// bleed-hardening plan's ">= 1600 nominal" feed-rate floor; chunk-size
    /// reduction attacked the wrong term). UX cost, quantified: a 458-char
    /// paste takes ~0.8 s of send time instead of ~0.3 s; in slow-rendering
    /// apps the perceived duration is unchanged (the app remains the
    /// bottleneck).
    /// </summary>
    internal const int TargetFeedUnitsPerSecond = 600;

    /// <summary>
    /// Pause between guarded send chunks, derived from
    /// <see cref="TargetFeedUnitsPerSecond"/> by CEILING division:
    /// ceil(8 * 1000 / 600) = 14 ms, i.e. ~571 code units/s nominal --
    /// rounded UP so the nominal feed can never EXCEED the target
    /// (truncating division gives 13 ms = ~615 units/s, above the claimed
    /// render rate, and the backlog would grow again; stage-2 ledger A1).
    /// Load-bearing (validation ledger, A1):
    /// SendInput is queue-insertion (~us per call), so an UNPACED loop
    /// finishes in single-digit milliseconds and the mid-paste guard could
    /// never observe a human focus change. The pace is real only through
    /// PacingWaiter (the production sleep default): Thread.Sleep quantizes
    /// to the legacy ~15.6 ms timer resolution (bleed-hardening ledger, V1),
    /// which would slow the feed to ~513 units/s -- tolerable at this design
    /// point, but the high-resolution timer keeps the pace deliberate rather
    /// than accidental (its health is pinned by the 5 ms probe in
    /// InterChunkPacingWindowsTests).
    /// </summary>
    internal const int InterChunkPauseMs =
        (ChunkCodeUnits * 1000 + TargetFeedUnitsPerSecond - 1) / TargetFeedUnitsPerSecond; // ceiling: feed <= target
```

- [ ] **Step 4: Retune the PacingWaiter doc comment**

In `src/Winpepper.Platform/Injection/PacingWaiter.cs`, replace the class-level
`<summary>` doc comment (lines 3–17, everything between `namespace ...;` and
`internal static class PacingWaiter`) with:

```csharp
/// <summary>
/// Production pacing primitive for the guarded injection send (the default
/// behind TextInjector's injectable sleep seam). Thread.Sleep CANNOT pace
/// millisecond-precise waits: measured on the Windows gate host it quantizes
/// to the legacy ~15.6 ms timer resolution (Sleep(5) averaged ~15.5 ms; even
/// the old shipped Sleep(20) really waited ~31 ms; bleed-hardening ledger,
/// V1). A high-resolution waitable timer
/// (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Win10 1803+) measured 5.2-5.4 ms
/// per 5 ms wait WITHOUT raising the process timer resolution (no
/// timeBeginPeriod; ledger B1/B3) -- so it is not exposed to the Win11
/// occluded-window resolution revocation a raised-resolution Sleep would
/// risk. At the current 14 ms production pause
/// (TextInjector.InterChunkPauseMs, render-rate pacing) the Thread.Sleep
/// fail-safe (~15.6 ms) overshoots by only ~11%, but the high-res timer
/// keeps the pace deliberate, and the fixed 5 ms probe in
/// InterChunkPacingWindowsTests still proves the fast path engages on the
/// gate host. Fail-safe: if the timer cannot be created or set, falls back
/// to Thread.Sleep -- pacing gets coarser (feed slower) but nothing breaks.
/// </summary>
```

- [ ] **Step 5: Run the class to verify the Linux tests pass**

Same build command as Step 2 (which must now SUCCEED), then run the class:

```bash
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests
```

Expected: PASS (`Errors: 0, Failed: 0` for the class run).

- [ ] **Step 6: Rewrite the Windows pacing sentinel to prove the NEW floor**

Replace the ENTIRE contents of
`tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs` with:

```csharp
using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for injection pacing (retuned by
/// paste-path-hardening, 2026-07-27, superseding the bleed-hardening
/// sentinel with explicit owner approval). Two independent checks:
/// (1) a FIXED 5 ms probe that proves the high-resolution waitable timer
/// engages at all on this host -- Thread.Sleep(5) quantizes to ~15.6 ms
/// (ledger V1), so 10 ms cleanly separates the paths; a failure here means
/// the timer path is broken: STOP and report, do not widen the threshold or
/// swap in a spin-wait without explicit approval. The 5 ms probe is kept
/// SEPARATE from the production pause on purpose: at 14 ms the high-res
/// timer (~13.2 ms) and the Sleep fallback (~15.6 ms) are nearly
/// indistinguishable on a noisy host, so a production-pace measurement has
/// no discriminating power for timer health.
/// (2) a production-pace check that proves the NEW render-rate floor: the
/// inter-chunk wait really is at least InterChunkPauseMs, so the feed rate
/// stays at or below ~571 code units/s and the bleed backlog cannot build
/// against slow-rendering (~600 chars/s) target apps.
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
    public void PacingWaiter_ProductionPace_WaitsAtLeastTheRequestedPause()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) PacingWaiter.Wait(TextInjector.InterChunkPauseMs);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // THE new floor (paste-path-hardening): the pace must really be at
        // least ~14 ms so the feed stays at or below ~571 code units/s and
        // backlog cannot grow against slow-rendering apps. Half-millisecond
        // grace for timer coalescing on a noisy host.
        avgMs.ShouldBeGreaterThanOrEqualTo(TextInjector.InterChunkPauseMs - 0.5);
        // Sanity ceiling: even the Thread.Sleep fallback lands ~15.6 ms;
        // past 20 ms something new is broken (feed < 400 units/s).
        avgMs.ShouldBeLessThan(20.0);
    }
}
```

Note: this file is `[Trait("Platform", "Windows")]` — it does NOT run on
Linux. It is proven by the Windows gate in Task 7 (and CI's windows runner).

- [ ] **Step 7: Run the full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 8: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
git add src/Winpepper.Platform/Injection/TextInjector.cs \
        src/Winpepper.Platform/Injection/PacingWaiter.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs \
        tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs
git commit -m "feat(injection): retune inter-chunk pacing to the ~600 units/s render-rate design point

Feed at 1600 units/s outran slow apps' ~600 chars/s render rate; the queued
backlog followed focus on a mid-paste click-switch and sprayed dozens of
chars. TargetFeedUnitsPerSecond=600 (14 ms ceiling-derived pace, 8-unit
chunks; nominal 571 <= 600) makes backlog growth <= zero, collapsing
worst-case bleed toward the in-flight chunk.
Deliberate, owner-approved supersession of the bleed-hardening >=1600 floor;
Windows sentinel retuned to prove the NEW floor (5 ms probe keeps timer-health
discrimination)."
```

---

### Task 2: Pure `ElevatedTargetDecider` (fail-open decision component)

**Files:**
- Create: `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces (used by Tasks 3–4):
  - `public enum ForegroundElevation { NotElevated, Elevated, Unknown }`
  - `public enum ElevatedTargetDecision { Inject, Park }`
  - `public static class ElevatedTargetDecider` with
    `public static ElevatedTargetDecision Decide(long hwndAtSendStart, ForegroundElevation elevation)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class ElevatedTargetDeciderTests
{
    [Fact]
    public void KnownHwnd_Elevated_Parks()
    {
        // The one new behavior: foreground positively observable AND elevated
        // => never inject (UIPI would silently drop every keystroke while
        // reporting success); park the full text instead.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Park);
    }

    [Fact]
    public void KnownHwnd_NotElevated_Injects()
    {
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.NotElevated)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }

    [Fact]
    public void KnownHwnd_UnknownElevation_Injects_FailOpen()
    {
        // Transient observation failure (window died mid-probe, probe threw):
        // preserve today's behavior -- inject. Same fail-open bias as
        // MidPasteDecider / PendingPasteDecider / MouseButtonGuard.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 42, ForegroundElevation.Unknown)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }

    [Fact]
    public void UnknownHwnd_Injects_FailOpen_EvenIfProbeClaimsElevated()
    {
        // No observable foreground at all (probe returned 0): the HWND guard
        // is disabled today and this check must not regress that. A probe
        // result for hwnd 0 is meaningless; fail open takes precedence.
        ElevatedTargetDecider.Decide(hwndAtSendStart: 0, ForegroundElevation.Elevated)
            .ShouldBe(ElevatedTargetDecision.Inject);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — `ElevatedTargetDecider`, `ForegroundElevation`,
`ElevatedTargetDecision` do not exist (CS0246). (For a not-yet-existing type,
the compile error IS the red step in this codebase's TDD convention.)

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>Foreground-window elevation as observed at injection start.</summary>
public enum ForegroundElevation
{
    /// <summary>Positively determined NOT elevated: safe to inject.</summary>
    NotElevated,

    /// <summary>
    /// Positively elevated, or access to the process/token was DENIED.
    /// Denial is read conservatively as elevated: probe evidence
    /// (paste-path-hardening, 2026-07-27) shows normal user apps are always
    /// queryable from medium IL, while protected/elevated processes deny
    /// OpenProcess -- and parking never loses text, whereas injecting into a
    /// UIPI-protected window silently loses all of it.
    /// </summary>
    Elevated,

    /// <summary>
    /// Could not observe (non-Windows, window gone before/while probing, or
    /// an unexpected probe failure): transient observation failure, handled
    /// fail-open like every other guard probe.
    /// </summary>
    Unknown,
}

/// <summary>Pre-injection decision for an elevated foreground target.</summary>
public enum ElevatedTargetDecision
{
    /// <summary>Proceed with the injection run.</summary>
    Inject,

    /// <summary>Do not inject; park the FULL text as a pending paste.</summary>
    Park,
}

/// <summary>
/// Pure pre-injection decision: is the window we are about to type into an
/// elevated (higher-integrity) process? Windows UIPI silently drops SendInput
/// to elevated windows while reporting success (MSDN: "neither GetLastError
/// nor the return value will indicate the failure was caused by UIPI
/// blocking"), so injecting would consume the text with nothing delivered.
/// Park is chosen ONLY when the foreground is positively observable
/// (hwnd != 0) AND its elevation is Elevated. An unknown HWND or unknown
/// elevation keeps today's fail-open behavior: inject. Same bias as
/// MidPasteDecider / PendingPasteDecider: never regress into holding when we
/// simply failed to observe.
/// </summary>
public static class ElevatedTargetDecider
{
    public static ElevatedTargetDecision Decide(long hwndAtSendStart, ForegroundElevation elevation)
    {
        if (hwndAtSendStart == 0) return ElevatedTargetDecision.Inject; // foreground unobservable: fail open
        return elevation == ForegroundElevation.Elevated
            ? ElevatedTargetDecision.Park
            : ElevatedTargetDecision.Inject;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.ElevatedTargetDeciderTests
```

Expected: PASS, 4 tests, `Errors: 0, Failed: 0`.

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. Then:

```bash
git add src/Winpepper.Platform/Injection/ElevatedTargetDecider.cs \
        tests/Winpepper.Platform.Tests/Injection/ElevatedTargetDeciderTests.cs
git commit -m "feat(injection): add pure ElevatedTargetDecider with fail-open bias

Park only when the foreground is positively observable AND elevated;
unknown hwnd or unknown elevation keeps today's inject behavior (same
fail-open rule as MidPasteDecider/PendingPasteDecider)."
```

---

### Task 3: `ElevationNative` P/Invoke surface + `ElevationProbe`

**Files:**
- Create: `src/Winpepper.Platform/Injection/ElevationNative.cs`
- Create: `src/Winpepper.Platform/Injection/ElevationProbe.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/ElevationProbeTests.cs` (Linux)
- Test: `tests/Winpepper.Platform.Tests/Injection/ElevationProbeWindowsTests.cs` (Windows gate)

**Interfaces:**
- Consumes: `ForegroundElevation` (Task 2).
- Produces (used by Task 4):
  - `internal static class ElevationProbe` with
    `public static ForegroundElevation Probe(long hwnd)` and
    `internal static ForegroundElevation ProbeProcessId(uint pid)`.

- [ ] **Step 1: Verify the `InternalsVisibleTo` grant exists**

Tests already read `TextInjector.ChunkCodeUnits` (internal), so the grant
exists — confirm where, so the new `internal` types are covered by the same
grant:

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
grep -rn "InternalsVisibleTo" src/Winpepper.Platform/
```

Expected: one match granting `Winpepper.Platform.Tests` (either in a `.cs`
attribute file or as an `<InternalsVisibleTo>`/`<AssemblyAttribute>` item in
`src/Winpepper.Platform/Winpepper.Platform.csproj`). No action needed unless
missing (it will not be — existing tests depend on it).

- [ ] **Step 2: Write the failing Linux tests**

Create `tests/Winpepper.Platform.Tests/Injection/ElevationProbeTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Linux-runnable coverage for the elevation probe's fail-open envelope.
/// The real Win32 chain is pinned by ElevationProbeWindowsTests on the gate.
/// </summary>
public sealed class ElevationProbeTests
{
    [Fact]
    public void Probe_ZeroHwnd_ReturnsUnknown_FailOpen()
    {
        // No observable foreground window: transient observation failure.
        ElevationProbe.Probe(0).ShouldBe(ForegroundElevation.Unknown);
    }

    [Fact]
    public void Probe_OffWindows_ReturnsUnknown_FailOpen()
    {
        // On non-Windows the probe can never observe; it must fail open like
        // TextInjector.DefaultForegroundProbe (returns 0) rather than park.
        if (OperatingSystem.IsWindows()) return;
        ElevationProbe.Probe(42).ShouldBe(ForegroundElevation.Unknown);
    }
}
```

- [ ] **Step 3: Run to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — `ElevationProbe` does not exist (CS0246).

- [ ] **Step 4: Write `ElevationNative`**

Create `src/Winpepper.Platform/Injection/ElevationNative.cs` (same
`LibraryImport` style as `SendInputNative`/`PacingWaiterNative`; compiles on
both TFMs, only ever invoked behind `OperatingSystem.IsWindows()` at the call
site — no `#if WINDOWS` in the Injection folder):

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Win32 surface for the foreground-window elevation probe
/// (paste-path-hardening). Same LibraryImport style as SendInputNative --
/// compiles on both TFMs; only ever invoked behind
/// OperatingSystem.IsWindows().
/// </summary>
internal static partial class ElevationNative
{
    /// <summary>Minimal access that succeeds across integrity levels for ordinary processes.</summary>
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Token access required by GetTokenInformation.</summary>
    public const uint TOKEN_QUERY = 0x0008;

    /// <summary>TOKEN_INFORMATION_CLASS.TokenElevation (a single DWORD: nonzero = elevated).</summary>
    public const int TokenElevationClass = 20;

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);
}
```

- [ ] **Step 5: Write `ElevationProbe`**

Create `src/Winpepper.Platform/Injection/ElevationProbe.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Managed elevation probe for the foreground window at injection start
/// (TextInjector's production default behind the foregroundElevation seam).
/// Chain: HWND -> GetWindowThreadProcessId ->
/// OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) ->
/// OpenProcessToken(TOKEN_QUERY) -> GetTokenInformation(TokenElevation).
/// Validated on the gate host (paste-path-hardening probe evidence,
/// 2026-07-27): the full chain succeeds from medium IL against normal apps
/// (NotElevated) and against elevated user-session processes (Elevated);
/// SYSTEM/protected processes deny OpenProcess (err 5). Mapping:
/// - Window unobservable (hwnd 0, no PID, non-Windows, unexpected
///   exception) => Unknown -- the transient-observation fail-open bucket.
/// - PID obtained but ANY of OpenProcess / OpenProcessToken /
///   GetTokenInformation fails => Elevated -- the conservative bucket:
///   denial usually IS elevation, and parking never loses text while a
///   UIPI-swallowed SendInput loses all of it. (A process dying between the
///   PID lookup and OpenProcess also lands here; parking is still safe.)
/// Cost: ~3 us per call measured (budget &lt; 5 ms, once per injection start).
/// </summary>
internal static class ElevationProbe
{
    public static ForegroundElevation Probe(long hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return ForegroundElevation.Unknown;
        try
        {
            if (ElevationNative.GetWindowThreadProcessId((IntPtr)hwnd, out var pid) == 0 || pid == 0)
                return ForegroundElevation.Unknown; // window gone: observation failure -> fail open
            return ProbeProcessId(pid);
        }
        catch
        {
            return ForegroundElevation.Unknown; // unexpected managed failure: fail open
        }
    }

    internal static ForegroundElevation ProbeProcessId(uint pid)
    {
        var process = ElevationNative.OpenProcess(
            ElevationNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return ForegroundElevation.Elevated; // denied => conservative park
        try
        {
            if (!ElevationNative.OpenProcessToken(process, ElevationNative.TOKEN_QUERY, out var token))
                return ForegroundElevation.Elevated;
            try
            {
                if (!ElevationNative.GetTokenInformation(
                        token, ElevationNative.TokenElevationClass,
                        out var elevation, sizeof(int), out _))
                {
                    return ForegroundElevation.Elevated;
                }
                return elevation != 0
                    ? ForegroundElevation.Elevated
                    : ForegroundElevation.NotElevated;
            }
            finally
            {
                ElevationNative.CloseHandle(token);
            }
        }
        finally
        {
            ElevationNative.CloseHandle(process);
        }
    }
}
```

- [ ] **Step 6: Run the Linux tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.ElevationProbeTests
```

Expected: PASS, 2 tests, `Errors: 0, Failed: 0`.

- [ ] **Step 7: Write the Windows-gate sentinels**

Create `tests/Winpepper.Platform.Tests/Injection/ElevationProbeWindowsTests.cs`:

```csharp
using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinels for the elevation probe, pinning the
/// paste-path-hardening probe evidence (2026-07-27): from medium IL the
/// TokenElevation chain succeeds against normal user processes
/// (NotElevated) and elevated user-session processes (Elevated observed on
/// the gate host); SYSTEM/protected processes deny OpenProcess (err 5),
/// which the probe maps to Elevated -- the conservative park that never
/// loses text. Measured cost ~3 us/call (budget &lt; 5 ms per injection
/// start).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class ElevationProbeWindowsTests
{
    [Fact]
    public void ProbeProcessId_OwnNonElevatedProcess_ReportsNotElevated()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The gate normally runs non-elevated; if someone runs it elevated
        // the not-elevated fixture simply is not available.
        Assert.SkipWhen(Environment.IsPrivilegedProcess,
            "gate host is running elevated; not-elevated fixture unavailable");

        ElevationProbe.ProbeProcessId((uint)Environment.ProcessId)
            .ShouldBe(ForegroundElevation.NotElevated);
    }

    [Fact]
    public void ProbeProcessId_ProtectedSystemProcess_ReportsElevated_ViaConservativeDenial()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipWhen(Environment.IsPrivilegedProcess,
            "an elevated runner could open winlogon for real; the denial path needs medium IL");

        // winlogon denies OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) to
        // medium IL (err 5 measured on the gate host); the probe must map
        // denial to Elevated so text is parked rather than silently dropped.
        var winlogon = Process.GetProcessesByName("winlogon").FirstOrDefault();
        Assert.SkipWhen(winlogon is null, "no winlogon process visible");

        ElevationProbe.ProbeProcessId((uint)winlogon!.Id)
            .ShouldBe(ForegroundElevation.Elevated);
    }

    [Fact]
    public void ProbeProcessId_PerCallCost_WellUnderInjectionBudget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = (uint)Environment.ProcessId;
        for (var i = 0; i < 10; i++) ElevationProbe.ProbeProcessId(pid); // warm-up

        const int iterations = 200;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) ElevationProbe.ProbeProcessId(pid);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // Measured ~0.003 ms/call on the gate host; the spec budget is
        // < 5 ms once per injection start (never per chunk).
        avgMs.ShouldBeLessThan(5.0);
    }
}
```

These run on the Windows gate (Task 7) AND on GitHub CI: `ci.yml`'s windows
job filters tests by NAME only (`FullyQualifiedName!~...`), so
`Platform=Windows`-trait tests are NOT excluded there — and GitHub-hosted
Windows runners execute **elevated** (administrators, UAC disabled). The two
`Assert.SkipWhen(Environment.IsPrivilegedProcess, ...)` guards above are
therefore LOAD-BEARING for CI green, not just gate-host hygiene (stage-2
ledger A9): without them both elevation sentinels flip red post-merge. Do
not remove or "simplify away" those guards. Verify the file at least
COMPILES for the windows TFM now:

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -p:EnableWindowsTargeting=true
```

Expected: build succeeds for BOTH TFMs (`net9.0` and
`net9.0-windows10.0.19041.0`), 0 errors.

- [ ] **Step 8: Run the full Linux suite and commit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. Then:

```bash
git add src/Winpepper.Platform/Injection/ElevationNative.cs \
        src/Winpepper.Platform/Injection/ElevationProbe.cs \
        tests/Winpepper.Platform.Tests/Injection/ElevationProbeTests.cs \
        tests/Winpepper.Platform.Tests/Injection/ElevationProbeWindowsTests.cs
git commit -m "feat(injection): add UIPI elevation probe (TokenElevation via PROCESS_QUERY_LIMITED_INFORMATION)

Window-unobservable maps to Unknown (fail open); any denial after the PID is
obtained maps to Elevated (conservative park -- probe evidence shows normal
apps are always queryable from medium IL while protected/elevated processes
deny OpenProcess). Windows-gate sentinels pin the chain, the denial mapping,
and the <5ms cost budget."
```

---

### Task 4: Wire the elevation pre-check into `TextInjector` (`BlockedElevated`)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs`
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (ctor seam + `TryInjectGuarded`)
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: `ElevatedTargetDecider.Decide(long, ForegroundElevation)` (Task 2),
  `ElevationProbe.Probe(long)` (Task 3).
- Produces (used by Task 6):
  - `InjectionRunOutcome.BlockedElevated` (new enum member, after `SendFailed`).
  - `TextInjector` ctor gains a FINAL optional parameter
    `Func<long, ForegroundElevation>? foregroundElevation = null`
    (appended last so existing positional/named callers keep compiling).
  - `TryInjectGuarded(string)` returns `BlockedElevated` (nothing typed, no
    preludes run) when the decider says Park.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`
(inside the existing test class):

```csharp
    [Fact]
    public void Guarded_ElevatedForeground_BlocksBeforeAnyKeystrokeOrWait()
    {
        // UIPI pre-check (paste-path-hardening): SendInput into an elevated
        // window is silently dropped while reporting success, so the run
        // must not start at all. The block must land BEFORE the modifier
        // and mouse release-wait preludes (no sleeps) and before any send.
        // isKeyDown reports everything held to prove no prelude ran.
        var sent = new List<string>();
        var sleeps = new List<int>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => true,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add,
            foregroundElevation: _ => ForegroundElevation.Elevated);

        injector.TryInjectGuarded("text for an admin window")
            .ShouldBe(InjectionRunOutcome.BlockedElevated);

        sent.ShouldBeEmpty();
        sleeps.ShouldBeEmpty(); // blocked before the release-wait preludes
    }

    [Fact]
    public void Guarded_ElevationUnknown_FailsOpen_AndSendsAll()
    {
        // Transient observation failure => today's behavior, unchanged.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { },
            foregroundElevation: _ => ForegroundElevation.Unknown);
        var text = new string('a', 16); // 2 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_ElevationNotElevated_SendsAll()
    {
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { },
            foregroundElevation: _ => ForegroundElevation.NotElevated);
        var text = new string('a', 16);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void Guarded_DefaultElevationProbe_OffWindows_FailsOpen()
    {
        // Construct WITHOUT the elevation seam: the production default
        // (ElevationProbe.Probe) must fail open off-Windows so every
        // existing Linux test and non-Windows path is unaffected.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });
        var text = new string('a', 8);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — no `foregroundElevation` ctor parameter (CS1739)
and no `InjectionRunOutcome.BlockedElevated` (CS0117).

- [ ] **Step 3: Add the enum member**

In `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs`, add after the
existing `SendFailed` member (keep existing members and their docs untouched):

```csharp
    /// <summary>
    /// The foreground window at send start belongs to an elevated
    /// (higher-integrity) process: Windows UIPI would silently drop every
    /// SendInput keystroke while reporting success, so NOTHING was typed --
    /// not even the modifier-neutralizing KEYUPs. The caller must park the
    /// FULL text as a pending paste and surface the elevated-target pill
    /// status. Not an error (no ErrorBus) -- the pill is the surface.
    /// </summary>
    BlockedElevated,
```

- [ ] **Step 4: Add the seam and the pre-check to `TextInjector`**

In `src/Winpepper.Platform/Injection/TextInjector.cs`:

(1) Add a field next to the other seam fields (after `_sleep`):

```csharp
    private readonly Func<long, ForegroundElevation> _foregroundElevation;
```

(2) Append the ctor parameter LAST and assign the default:

```csharp
    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null,
        Action<int>? sleep = null,
        Func<long, ForegroundElevation>? foregroundElevation = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
        _sleep = sleep ?? PacingWaiter.Wait;
        _foregroundElevation = foregroundElevation ?? ElevationProbe.Probe;
    }
```

(3) In `TryInjectGuarded`, insert immediately AFTER
`var hwndAtSendStart = _foregroundHwnd();` and BEFORE
`NeutralizeHeldModifiers();`:

```csharp
        // UIPI pre-check (paste-path-hardening): SendInput into an elevated
        // window is silently dropped while reporting success, so a run
        // against an elevated target would consume the text with nothing
        // delivered. Park instead -- BEFORE any synthesis (even the
        // modifier-neutralizing KEYUPs) and before the release-wait
        // preludes. Fail-open: an unobservable foreground or elevation
        // keeps today's behavior (ElevatedTargetDecider).
        if (ElevatedTargetDecider.Decide(hwndAtSendStart, _foregroundElevation(hwndAtSendStart))
            == ElevatedTargetDecision.Park)
        {
            _log.LogInformation(
                "Foreground window is elevated (UIPI would silently drop SendInput); not typing -- holding the full text as pending ({Chars} chars)",
                text.Length);
            return InjectionRunOutcome.BlockedElevated;
        }
```

(4) Extend the `TryInjectGuarded` XML doc: after the sentence ending
"…caller can hold the WHOLE original text as a pending paste.", insert:

```
    /// Before anything else -- even the preludes -- the foreground window's
    /// process elevation is probed once: an elevated (UIPI-protected) target
    /// returns <see cref="InjectionRunOutcome.BlockedElevated"/> with nothing
    /// typed, because SendInput to such a window is silently dropped while
    /// reporting success (MSDN); the caller parks the FULL text.
```

- [ ] **Step 5: Run the class to verify all tests pass (old and new)**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests
```

Expected: PASS, `Errors: 0, Failed: 0` (existing tests are unaffected: the
`NewInjector` helper and all existing constructions omit the new final
parameter, and the default probe fails open on Linux).

- [ ] **Step 6: Run the full Linux suite and commit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. Then:

```bash
git add src/Winpepper.Platform/Injection/InjectionRunOutcome.cs \
        src/Winpepper.Platform/Injection/TextInjector.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(injection): block guarded injection into elevated foreground windows

New BlockedElevated outcome, decided once at send start via the
foregroundElevation seam (default: ElevationProbe.Probe) BEFORE the
modifier/mouse preludes -- an elevated target receives no synthesis at all
and the caller parks the full text. Fail-open preserved for unobservable
foreground/elevation."
```

---

### Task 5: Elevated-target pill status copy in `SessionViewModel`

**Files:**
- Create: `src/Winpepper.Core/Pending/PendingPasteReason.cs`
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs:152-160` (the `EnterPendingPaste` region)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`

**Interfaces:**
- Consumes: existing `PendingPasteState`, `InjectionTarget`, `_ui.Post`,
  `SessionStage.PendingPaste` (all unchanged).
- Produces (used by Task 6):
  - `public enum PendingPasteReason { Interrupted, ElevatedTarget }`
    (namespace `Winpepper.Core.Pending`).
  - `public void SessionViewModel.EnterPendingPaste(string text, InjectionTarget target, PendingPasteReason reason = PendingPasteReason.Interrupted)`
    (existing 2-arg callers keep compiling via the default).
  - `public void SessionViewModel.ShowPendingPasteStatus(PendingPasteReason reason)`
    — updates only the pill copy while a slot is held; no-op when nothing pending.
  - Status copy strings: `"Click to paste"` (unchanged default) and
    `"Admin window - switch & click"` (elevated).

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`
(inside the existing test class; it already has the `NewVm()` and `T(...)`
helpers and `using Winpepper.Core.Pending;` for `InjectionTarget` — add that
using if not present):

```csharp
    [Fact]
    public void EnterPendingPaste_ElevatedReason_ShowsAdminCopy_AndStaysClickable()
    {
        // Elevated-target park (paste-path-hardening): same PendingPaste
        // stage (pill stays clickable, PillAnimationMap untouched), same
        // full-text slot semantics -- only the copy differs so the user
        // knows WHY nothing was typed and what to do.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("blocked text", T(1, "a"), PendingPasteReason.ElevatedTarget);

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("blocked text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Admin window - switch & click");
    }

    [Fact]
    public void EnterPendingPaste_DefaultReason_KeepsClickToPasteCopy()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("deferred text", T(1, "a"));

        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void ShowPendingPasteStatus_TogglesCopy_WhilePending()
    {
        // Pill-click retry path: clicking the pill while an admin window is
        // focused flips the copy to the admin message; a later kept-slot
        // outcome that is NOT elevated flips it back.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("retry me", T(1, "a"));

        vm.ShowPendingPasteStatus(PendingPasteReason.ElevatedTarget);
        vm.StatusText.ShouldBe("Admin window - switch & click");
        vm.Stage.ShouldBe(SessionStage.PendingPaste); // still clickable
        vm.HasPendingPaste.ShouldBeTrue();            // slot untouched

        vm.ShowPendingPasteStatus(PendingPasteReason.Interrupted);
        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void ShowPendingPasteStatus_NoOp_WhenNothingPending()
    {
        var (vm, _) = NewVm();
        vm.ShowPendingPasteStatus(PendingPasteReason.ElevatedTarget);

        vm.StatusText.ShouldNotBe("Admin window - switch & click");
        vm.HasPendingPaste.ShouldBeFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — `PendingPasteReason` does not exist (CS0246) /
no 3-arg `EnterPendingPaste` / no `ShowPendingPasteStatus`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Core/Pending/PendingPasteReason.cs`:

```csharp
namespace Winpepper.Core.Pending;

/// <summary>
/// Why text is sitting in the pending-paste slot -- selects the pill's
/// status copy. Slot semantics are identical for every reason: the FULL
/// transcription is held in memory (never persisted) and a pill click
/// re-attempts the paste into whatever field is focused then.
/// </summary>
public enum PendingPasteReason
{
    /// <summary>
    /// Deferred or interrupted paste (focus moved, halt gesture, SendInput
    /// refusal): the default "Click to paste" copy.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The target window was elevated -- UIPI would have silently dropped
    /// every keystroke (paste-path-hardening) -- so nothing was typed. The
    /// copy tells the user to focus a normal window before clicking.
    /// </summary>
    ElevatedTarget,
}
```

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`, replace the existing
`EnterPendingPaste` method (line ~152) with the following (add
`using Winpepper.Core.Pending;` at the top if `InjectionTarget` is currently
fully qualified — match the file's existing style):

```csharp
    private const string PendingPasteStatus = "Click to paste";
    private const string PendingPasteElevatedStatus = "Admin window - switch & click";

    private static string PendingStatusFor(PendingPasteReason reason)
        => reason == PendingPasteReason.ElevatedTarget ? PendingPasteElevatedStatus : PendingPasteStatus;

    /// <summary>
    /// Enter the pending-paste state: hold the final text in memory (never
    /// persisted) and show the pill's PENDING visual. Because Stage becomes
    /// PendingPaste (not Idle), the pill's Idle auto-hide does not fire.
    /// The reason selects the pill copy: an elevated-target park explains
    /// WHY nothing was typed (UIPI) and what to do next; everything else
    /// keeps the classic "Click to paste".
    /// </summary>
    public void EnterPendingPaste(string text, InjectionTarget target,
        PendingPasteReason reason = PendingPasteReason.Interrupted) => _ui.Post(() =>
    {
        _pending.SetPending(text, target);
        Stage = SessionStage.PendingPaste;
        StatusText = PendingStatusFor(reason);
    });

    /// <summary>
    /// Update the pill copy for a paste attempt that KEPT the slot (the
    /// pill-click retry path): an elevated block shows the admin-window
    /// copy; any other kept-slot outcome restores the default. No-op when
    /// nothing is pending. Stage stays PendingPaste so the pill remains
    /// clickable -- the slot itself is untouched.
    /// </summary>
    public void ShowPendingPasteStatus(PendingPasteReason reason) => _ui.Post(() =>
    {
        if (!_pending.HasPending) return;
        StatusText = PendingStatusFor(reason);
    });
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Core.Tests.ViewModels.SessionViewModelPendingTests
```

Expected: PASS, `Errors: 0, Failed: 0` (existing tests in the class still pass:
2-arg `EnterPendingPaste` calls get the `Interrupted` default and the
unchanged `"Click to paste"` copy).

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. Then:

```bash
git add src/Winpepper.Core/Pending/PendingPasteReason.cs \
        src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs
git commit -m "feat(core): elevated-target pending-paste pill copy

PendingPasteReason selects the pill status: ElevatedTarget explains why
nothing was typed (UIPI) and what to do; Stage stays PendingPaste so the
pill remains clickable and PillAnimationMap is untouched."
```

---

### Task 6: `PipelineHost` wiring — park + distinct status at all three entry points

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — `TryPastePending`
  (lines ~382–419), hold-arm injection block (lines ~695–750), toggle-arm
  injection block (lines ~1080–1133)

**Interfaces:**
- Consumes: `InjectionRunOutcome.BlockedElevated` (Task 4),
  `SessionViewModel.EnterPendingPaste(text, target, reason)` and
  `SessionViewModel.ShowPendingPasteStatus(reason)` (Task 5),
  `Winpepper.Core.Pending.PendingPasteReason`.
- Produces: complete production behavior — nothing downstream consumes new
  surface. NOTE: `Winpepper.App` has NO test project and does not build on
  Linux (WinUI); this task is verified by careful code-review-grade editing,
  a green Linux suite (proves nothing here but is mandatory before commit),
  and the full Windows gate in Task 7 which builds the App and runs all
  suites. Follow the file's existing fully-qualified-name style
  (`Winpepper.Platform.Injection.InjectionRunOutcome`,
  `Winpepper.Core.Pending.PendingPasteReason`).

- [ ] **Step 1: Update `TryPastePending` (pill-click retry)**

Replace the body of `public bool TryPastePending()` (currently lines
~391–419) so it reads exactly:

```csharp
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = Winpepper.Core.InjectionText.ForPaste(_vm.PendingPasteText);
        var outcome = string.IsNullOrWhiteSpace(text)
            ? Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed
            : _injector.TryInjectGuarded(text);
        var injected = outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
        if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed)
        {
            // Slot is kept below; the pill stays clickable for a retry.
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; pending slot kept for retry"),
                _currentSessionId);
        }
        if (injected)
            _log.LogInformation("Pending paste injected");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
            // The clicked-into window is elevated: UIPI would have silently
            // dropped every keystroke while reporting success (the exact
            // failure this check exists for -- previously the slot was
            // consumed and the text lost). Nothing was typed; the slot keeps
            // the FULL text and the pill copy tells the user to focus a
            // normal window first. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste blocked: foreground window is elevated; slot kept with full text");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
            // Focus moved mid-paste during the pill-click retry too: the slot
            // still holds the FULL original text, so the next click re-pastes
            // all of it. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste interrupted (focus, modifier, or mouse-button change); slot kept with full text for another click");
        else
            _log.LogWarning("Pending paste injection failed");

        // Keep the pill copy in sync with the LATEST attempt: an elevated
        // block shows the admin-window copy; any other kept-slot outcome
        // restores the default "Click to paste" (the previous attempt may
        // have set the admin copy).
        if (!injected)
            _vm.ShowPendingPasteStatus(
                outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated
                    ? Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget
                    : Winpepper.Core.Pending.PendingPasteReason.Interrupted);

        return _vm.NotifyPasteAttempted(injected);
    }
```

(Everything before the `if (injected)` line is byte-identical to today; the
changes are the `BlockedElevated` log branch and the `ShowPendingPasteStatus`
sync block.)

- [ ] **Step 2: Add the `BlockedElevated` branch to the hold arm**

In the hold-arm injection block (lines ~717–748), between the existing
`if (outcome == ...Interrupted) { ... }` branch and the
`else if (!injected) { ... }` branch, insert:

```csharp
                        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
                        {
                            // The target window is elevated: UIPI silently
                            // drops SendInput while reporting success, so
                            // nothing was typed. Park the WHOLE transcription
                            // and explain via the pill copy. Not an error:
                            // no ErrorBus report, no toast.
                            _vm.EnterPendingPaste(final, _targetAtStart,
                                Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget);
                            _log.LogInformation(
                                "Injection blocked: foreground window is elevated; held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
```

CRITICAL: this branch MUST come before `else if (!injected)` — otherwise
`BlockedElevated` falls into the SendFailed-style branch and gets
misclassified as an ErrorBus "SendInput refused" report.

Note `injected` stays `false` for `BlockedElevated`, so the downstream
`PostPasteGate.ShouldWatch(...)` gating (line ~752) correctly does NOT arm
post-paste learning for text that never landed — this also fixes the
predecessor plan's accepted residual AD-2 (false `Completed` consuming the
slot and arming learning).

- [ ] **Step 3: Add the same branch to the toggle arm**

In the toggle-arm injection block (lines ~1102–1131), the code is
structurally identical with `final2`/`outcome2`/`injected2` names. Between
its `if (outcome2 == ...Interrupted) { ... }` branch and its
`else if (!injected2) { ... }` branch, insert:

```csharp
                        else if (outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.BlockedElevated)
                        {
                            // The target window is elevated: UIPI silently
                            // drops SendInput while reporting success, so
                            // nothing was typed. Park the WHOLE transcription
                            // and explain via the pill copy. Not an error:
                            // no ErrorBus report, no toast.
                            _vm.EnterPendingPaste(final2, _targetAtStart,
                                Winpepper.Core.Pending.PendingPasteReason.ElevatedTarget);
                            _log.LogInformation(
                                "Injection blocked: foreground window is elevated; held full text as pending paste ({Chars} chars)",
                                final2.Length);
                        }
```

(Adjust the exact local variable names to what the block actually uses —
verify with `grep -n "outcome2\|final2" src/Winpepper.App/Hosting/PipelineHost.cs`
before editing; the hold arm uses `outcome`/`final`, the toggle arm uses the
`2`-suffixed twins.)

- [ ] **Step 4: Self-check the edit**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
grep -n "BlockedElevated" src/Winpepper.App/Hosting/PipelineHost.cs
```

Expected: 4 matches — one in `TryPastePending`'s log chain, one in its
`ShowPendingPasteStatus` sync, one in the hold arm, one in the toggle arm.
Also verify ordering (each `BlockedElevated` arm precedes the `!injected` /
`!injected2` arm in its chain).

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (the App project is not built on Linux; this
run guards the Platform/Core layers). Then:

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): park full text with admin-window pill copy when the injection target is elevated

All three entry points (hold arm, toggle arm, pill-click retry) map
BlockedElevated to a full-text park with the elevated-target status copy --
no ErrorBus, no slot consumption, no post-paste learning on text that never
landed. Closes the AD-2 residual where an elevated-from-start target yielded
a false Completed and silently lost the dictation."
```

---

### Task 7: Windows gate + manual smoke checklist

**Files:** none created/modified (unless the gate finds a defect — fix
forward with focused commits).

**Interfaces:**
- Consumes: everything above; runs the App build + all 12 project/TFM test
  runs on the Windows host.
- Produces: `GATE: GREEN` — the pre-push requirement (AGENTS.md).

- [ ] **Step 1: Run the full Windows gate from WSL**

```bash
cd /home/dan/code/winpepper/.worktrees/paste-path-hardening
./scripts/windows-gate.sh
```

Use a generous timeout (~60 min; the gate itself budgets 40 min build +
20 min per run). Expected: `GATE: GREEN` and exit 0. This proves, on the real
host: the App builds with the new `PipelineHost` branches; the retuned
pacing sentinels (`InterChunkPacingWindowsTests`) pass — the 5 ms probe shows
the high-res timer engages AND the 14 ms production pace really waits ≥
13.5 ms avg; and the elevation-probe sentinels (`ElevationProbeWindowsTests`)
pass — own process `NotElevated`, winlogon denial maps to `Elevated`, cost
< 5 ms/call.

Known transient failure: if the log shows UNC I/O errors containing
`retry should be performed`, other builds were running concurrently — re-run
the gate in a quiet window (nothing else building against the WSL share) or
from an isolated worktree. Do not chase it as a code failure. Known
environment caveat: `Hook_Installs_And_DisposesCleanly` hangs on a headless/
locked desktop — needs an interactive unlocked session (pre-existing,
unrelated).

- [ ] **Step 2: If the gate is RED for a real reason — fix, re-run, commit**

Apply the smallest fix, re-run `./scripts/linux-tests.sh` (must stay GREEN),
re-run `./scripts/windows-gate.sh`, and commit the fix with a focused message,
e.g. `fix(injection): <what the gate caught>`. If the gate reveals a
FUNDAMENTAL problem with either change (per the Global Constraints halt
condition — e.g. the elevation chain behaves differently under the gate host
than the probes showed in a way that cannot preserve fail-open), HALT and
report rather than working around it.

- [ ] **Step 3: Record the manual smoke checklist (perform if an interactive session is available; otherwise report as pending-user-smoke)**

These require a human at the real desktop; they are the acceptance walk for
the two incidents that motivated this work:

1. **Elevated pill-click (the original incident):** dictate text, let it park
   (e.g. switch windows mid-paste), focus an ELEVATED terminal, click the
   pill. Expect: nothing typed, log line
   `Pending paste blocked: foreground window is elevated; slot kept with full text`,
   pill shows `Admin window - switch & click`,
   and the pill remains clickable. Then focus a normal window (e.g. Notepad),
   click the pill: the FULL text pastes and the pill hides. The copy was
   pre-sized to the pill's measured text budget (~32 chars at FontSize 13 in
   the fixed 300-DIP pill; stage-2 ledger A8 falsified the original 57-char
   copy at ~57% elision) — confirm it renders fully, no ellipsis.
2. **Elevated hold/toggle arm:** focus an elevated terminal, dictate via the
   hotkey. Expect: no characters land, pill shows the admin copy, full text
   parks; click a normal window then the pill — full text lands.
3. **Bleed backlog (the second incident):** paste a ~450-char dictation into
   a slow-rendering app and click a different window mid-paste. Expect: the
   new window receives at most ~8 stray characters (one in-flight chunk),
   not dozens; the pill parks the full text. Perceived total duration in the
   slow app should be unchanged versus before (the app was already the
   bottleneck); in a fast app (Notepad) the 458-char paste now takes ~0.8 s
   instead of ~0.3 s — expected, accepted cost.
4. Reminder from the predecessor plan: smoke items exercising the MOUSE halt
   must use non-elevated targets (`GetAsyncKeyState` is blanked for elevated
   foregrounds — that limitation is unchanged; the elevated case is now
   handled by the pre-check instead).

No push is performed by this plan; the workflow's later stages own
integration. The gate green at this task satisfies the pre-push rule for
whoever pushes next.

---

## Self-Review (performed at plan-writing time)

1. **Spec coverage:** elevated detection before ALL THREE injection runs —
   Task 4 (single check inside `TryInjectGuarded`, used by all three sites) +
   Task 6 (per-site park/status). Full-text park + distinct pill status —
   Tasks 5–6. Detection approach + conservative denial mapping — Task 3.
   Load-bearing probes (a)/(b)/(c) — performed pre-plan, evidence recorded
   above AND pinned forever as Windows-gate sentinels
   (`ElevationProbeWindowsTests`). Fail-open preservation — Tasks 2–4
   (decider tests pin `Unknown`/hwnd-0 ⇒ inject). Render-rate pacing with
   named documented constant — Task 1 (`TargetFeedUnitsPerSecond`,
   `InterChunkPauseMs` derived). All old 5 ms / 1600 references updated —
   Task 1 covers every occurrence found by a repo sweep (TextInjector.cs:35–49,
   PacingWaiter.cs:6–16, TextInjectorGuardedTests.cs:132/150/153–155/207–208,
   InterChunkPacingWindowsTests.cs whole file). Sentinel retuned to prove the
   NEW floor — Task 1 Step 6. Halt predicate unchanged — no task touches
   `GuardedInjectionRun`/`MidPasteDecider`/guards. UX cost quantified —
   Global Constraints + Task 1 XML doc. Linux-green-per-commit and
   Windows-gate-before-push with the UNC-retry note — every task + Task 7.
   Halt condition — evaluated: probes passed, no fundamental problem; the
   condition is restated in Global Constraints for the execute stage.
2. **No silent deferrals:** every user-facing requirement lands as production
   behavior with a real observable outcome (Windows-gate sentinels + manual
   smoke walk for the two motivating incidents). The only test doubles are
   the repo-standard `Func<>` lambda seams, and each seamed behavior's
   production default is separately pinned on the Windows gate
   (`ElevationProbeWindowsTests` for the probe, `InterChunkPacingWindowsTests`
   for the pace). The App-layer wiring has no unit-test home (pre-existing
   repo reality: no App test project) — it is proven by the gate build plus
   the smoke checklist, same as the two predecessor plans. **No unresolved
   coverage gaps.**
3. **Placeholder scan:** no TBD/TODO/"similar to Task N"; every code step
   shows complete code; commands include expected outputs.
4. **Type consistency check:** `ForegroundElevation` (Task 2) is the param of
   the Task 4 seam `Func<long, ForegroundElevation>` and the return of
   `ElevationProbe.Probe`/`ProbeProcessId` (Task 3); `ElevatedTargetDecision`
   consumed only in Task 4; `InjectionRunOutcome.BlockedElevated` produced in
   Task 4, consumed in Task 6; `PendingPasteReason` produced in Task 5,
   consumed in Task 6; `EnterPendingPaste(string, InjectionTarget, PendingPasteReason)`
   and `ShowPendingPasteStatus(PendingPasteReason)` signatures match between
   Tasks 5 and 6; `TargetFeedUnitsPerSecond`/`InterChunkPauseMs` names match
   between Task 1 production code and both test files. Status copy string is
   byte-identical in Task 5 impl, Task 5 tests, and Task 7 smoke item.

---

## Post-ship evidence: render-rate measurement (2026-07-28, council fast-follow #2)

The `TargetFeedUnitsPerSecond = 600` design point was set from a user
eyeball estimate (~300 chars visibly unfolding in ~0.5 s in their daily
app). The council flagged this as unmeasured. Measured on the real host
(Winpepper 0.7.0.90 running, its keyboard hook active), by injecting 600
chars unpaced into sacrificial windows and polling WM_GETTEXTLENGTH at
5 ms until complete:

| Target | Measured consumption |
|---|---|
| WinForms TextBox (fast path) | ~633 chars/s |
| WinForms RichTextBox | ~1,163 chars/s |
| WinForms TextBox + 1 ms/keystroke handler | ~63 chars/s (synthetic; Start-Sleep quantum inflates 1 ms to ~15.6 ms — not representative of real apps) |
| Win11 Notepad | unmeasurable (RichEditD2DPT does not answer WM_GETTEXT-family reads; text landed but could not be polled) |

Conclusions:
1. **Constant kept at 600.** No measurable REAL target consumed slower
   than the 571 units/s nominal feed; the two real controls measured at
   633 and 1,163 chars/s, and the user's real-app estimate (~600) sits
   right at the design point. Backlog growth is zero-or-negative against
   everything measured.
2. Pathologically slow targets (the synthetic 63 chars/s case) exist in
   principle; for those the mid-paste guards (focus/modifier/mouse/hwnd-0
   halt) and the HwndZeroMeter field data are the mitigation, not pacing.
3. **Unconfirmed observation worth a future look:** a single unpaced
   600-char SendInput batch took ~0.5-1.0 s to RETURN with Winpepper's
   low-level keyboard hook running (queue-insertion is normally ~us).
   If the hook adds ~0.5 ms per injected event, Winpepper's own paced
   sends are effectively slower than nominal (extra safety margin against
   bleed, slight extra latency). Not acted on; measure before believing.

Probe scripts: /tmp/render-probe/{target,probe}.ps1 (WSL side, session-local).
