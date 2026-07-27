# Mid-Paste Bleed Hardening Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Shrink the text that can bleed into a newly focused window during a
mid-paste window switch from ~32 to ~8 characters, and halt injection on the
earliest observable click signal (physical mouse-button-down) — without
slowing the paste feed rate, breaking the pending-paste pill's click-to-paste,
or violating fail-open.

**Architecture:** Two surgical changes inside the existing guarded-injection
design (shipped in `docs/plans/2026-07-26-midpaste-focus-fallback.md`, merged
7bd039d). (1) Retune the pacing constants in `TextInjector` from 32 code
units/20 ms to 8 code units/5 ms — an identical 1600 units/s feed rate, but
the per-chunk guard runs 4× more often, so the accepted ≤1-chunk in-flight
residual (prior ledger AD-1) shrinks from ≤32 to ≤8 code units. (2) Add a
pure `MouseButtonGuard` (VK_LBUTTON/VK_RBUTTON/VK_MBUTTON via the existing
injectable `isKeyDown` seam), consumed in two places: a new *release-wait
prelude* in `TryInjectGuarded` (never start typing while a button is
physically down — this is what stops the pill's own `PointerPressed` click
from self-cancelling the paste) and the per-chunk halt predicate (renamed
`modifierHeld` → `physicalInputDown`). No `Winpepper.Core` changes; all new
logic is Linux-unit-testable through the existing `isKeyDown`/`sleep` seams.

**Tech Stack:** C# / .NET 9 (`Winpepper.Platform` multi-targets
`net9.0;net9.0-windows10.0.19041.0`), Win32 `SendInput`/`GetAsyncKeyState`
behind delegate seams, xUnit v3 + Shouldly (no mocking library).

## Global Constraints

- **Feed rate floor:** effective paste feed rate must never drop below the
  1600 code units/s design point (`ChunkCodeUnits * 1000 / InterChunkPauseMs
  >= 1600` — pinned by a test in Task 1).
- **Full-text pending semantics:** interruption always parks the ENTIRE
  transcription, never a remainder. `Winpepper.Core` (`PendingPasteState`,
  `SessionViewModel`, `SessionStage`) is NOT modified by this plan.
- **Fail-open:** never hold a paste because we merely failed to observe.
  `GetAsyncKeyState` has no error channel — a probe that cannot observe
  reports "up", which must read as "no halt".
- **No mouse synthesis, ever:** mouse buttons are only OBSERVED. Never
  synthesize a mouse button-up (it would fabricate a click). This is why
  mouse VKs must NOT be added to `ModifierGuard.ModifierVks` — that array
  also drives the KEYUP-synthesizing neutralization prelude and its 1500 ms
  `WaitForRelease`.
- **Interrupted is not an error:** no `ErrorBus` report, no toast, no
  clipboard clobbering — the pill IS the surface (unchanged consumer policy).
- **Dual TFM:** new code in `Winpepper.Platform` must compile and run on both
  `net9.0` and `net9.0-windows10.0.19041.0`. No `#if WINDOWS` in pure logic.
- **Gates (AGENTS.md):** `./scripts/linux-tests.sh` must print
  `LINUX SUITE: GREEN` and exit 0 before EVERY commit. The full Windows gate
  `./scripts/windows-gate.sh` (from WSL, ~20–40 min, prints `GATE: GREEN`)
  before any push. NEVER use `dotnet test` — build `-c Release` then
  `dotnet exec <test dll>`.
- **Docs:** README.md is the only end-user markdown doc. The historical plan
  `docs/plans/2026-07-26-midpaste-focus-fallback.md` is a shipped record —
  do NOT edit it; this plan supersedes its AD-1 bleed bound (32 → 8) and
  extends its A6 rationale to mouse buttons.

## HALT CONDITIONS (from the user — blockers, not workaround material)

If any of the following is observed during execution, **STOP the run and
report the problem**. Do not plan around it, do not weaken an assertion to
get past it, do not move it to "known limitations":

1. **Pacing reliability:** evidence that 8-char chunks with 5 ms pacing
   materially changes injection reliability or atomicity — in particular, if
   the Windows pacing sentinel test (Task 1, Step 7) fails, meaning
   `Thread.Sleep(5)` quantizes to the legacy ~15.6 ms timer resolution and
   the real feed rate drops below the 1600 units/s floor.
2. **Pill-click safety:** evidence that the per-chunk mouse-button check
   cannot distinguish the pill-retry click case safely (e.g. the release-wait
   prelude still self-cancels or livelocks the pill paste).
3. **No bleed reduction:** evidence the design cannot deliver the intended
   bleed reduction.

## File Structure

| File | Change | Responsibility |
|---|---|---|
| `src/Winpepper.Platform/Injection/TextInjector.cs` | Modify | Constants 32/20 → 8/5; doc updates; mouse release-wait prelude; composed per-chunk predicate; `_sleep` seam fix; log messages |
| `src/Winpepper.Platform/Injection/MouseButtonGuard.cs` | **Create** | Pure static mouse-button probe (`MouseButtonVks`, `AnyDown`) — deliberately separate from `ModifierGuard` |
| `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` | Modify | Rename `modifierHeld` → `physicalInputDown`; doc covers mouse buttons |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` | Modify | Retuned assertions; seam-fix test; mouse prelude/halt/fail-open tests |
| `tests/Winpepper.Platform.Tests/Injection/MouseButtonGuardTests.cs` | **Create** | Unit tests for the new guard |
| `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs` | Modify | Surrogate-safety case at the production chunk size 8 |
| `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs` | Modify | Named-argument update for the rename |
| `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs` | **Create** | Windows-gate sentinel: `Thread.Sleep(5)` really is ~5 ms (HALT condition 1 detector) |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | Comment/log accuracy only ("focus or modifier" → also mouse button). `#if WINDOWS`; compile proven by the Windows gate |

**Not touched (verified by repo-wide sweep):** `InjectionChunker.cs` (chunk
size is a parameter; the only production call site passes
`TextInjector.ChunkCodeUnits`), `MidPasteDecider.cs`, `InjectionRunOutcome.cs`,
`SendInputNative.cs`, `ClipboardFallback.cs`, everything in `Winpepper.Core`.
Other literal `32`/`20`/`1600`/`0.6`/`chunkSize` hits in the repo are
unrelated (RIFF `chunkSize` in `WavWriter.cs`, audio bits-per-sample,
`[FieldOffset(32)]`, XAML sizes, audio-eval thresholds) — do not touch them.

## Design rationale (load-bearing facts an implementer must know)

- **Why 8/5 preserves throughput:** 8 units per 5 ms = 32 units per 20 ms =
  1600 code units/s. The documented "1000-unit paste ≈ 0.6 s" claim stays
  true verbatim. What changes: 125 `SendInput` calls per 1000 units instead
  of 32 (each call now carries 16 INPUT structs — 8 code units × down+up —
  instead of 64), and the guard checks run 4× more often. Per-chunk probe
  cost (`GetForegroundWindow` ≈ 11 ns, `GetAsyncKeyState` comparable) totals
  a few microseconds per paste — negligible.
- **Why the pill click would self-cancel without a prelude:** the pill's
  click handler is wired to `PointerPressed` (the button-DOWN edge,
  `StatusPillWindow.xaml:13`) and `PipelineHost.TryPastePending` runs
  synchronously inside that handler on the WinUI UI thread. A naive
  VK_LBUTTON check in the per-chunk predicate (which runs before chunk 0)
  would see the user's own click still physically down and return
  `Interrupted` with zero chunks sent — deterministically, every click,
  because the blocked UI thread can't even pump the pointer-up message. The
  slot is kept on failure, so the user clicks again → same result → the
  click-to-paste feature becomes a hard no-op.
- **Why a release-WAIT works anyway:** `GetAsyncKeyState` reads asynchronous
  *physical* device state, not the (blocked) message queue — the button
  release is observable from inside the handler even though WM_LBUTTONUP is
  never pumped during the wait.
- **Why timeout ⇒ abort (not proceed):** the modifier prelude can neutralize
  a stuck key by synthesizing KEYUPs; there is no safe mouse analogue. A
  button still held past the timeout (e.g. the user is dragging) aborts with
  `Interrupted`: the pending slot keeps the FULL text and nothing is sprayed
  into whatever sits under the still-held pointer. This is a positive
  observation of "button down", so fail-open is not violated.
- **Why the HWND baseline stays FIRST:** `hwndAtSendStart` is captured at
  method entry, before both preludes, so a focus change during either wait
  is caught by the pre-chunk-0 HWND check (pinned by
  `Guarded_FocusChange_DuringModifierWait_SendsNothing`). The new mouse wait
  must go AFTER the baseline and AFTER `NeutralizeHeldModifiers()`.
- **Prelude budget:** worst case modifier wait (1500 ms) + mouse wait
  (1500 ms) = 3 s before the first keystroke, both only while the user
  physically holds something and both ending within ~15 ms of release. The
  prior ledger's A3 caveat ("re-weigh if pacing grows beyond ~1–2 s")
  concerned the *send* duration, which is unchanged; the prelude extension is
  user-controlled and bounded. Typical pill click adds ~30–60 ms.
- **Superseded decision:** prior ledger AD-1 accepted a ≤32-code-unit
  cosmetic bleed (the in-flight chunk backlog the RIT routes into the new
  window). This plan hardens that bound to ≤8 and adds an earlier halt signal
  for click-to-switch (which today has NO early signal — the modifier check
  only covers Alt+Tab). Ledger A5 ("clicking the pill never changes the
  foreground window", via `WS_EX_NOACTIVATE`) protected the HWND half of the
  guard from pill self-cancel; the mouse prelude is the analogous protection
  for the new mouse half.

## Test commands

Quick loop (Platform tests only, from the worktree root
`/home/dan/code/winpepper/.worktrees/midpaste-bleed-hardening`):

```bash
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests"
```

(Substitute `-class` per task; drop it to run the whole assembly.)

Pre-commit gate (MANDATORY before every commit):

```bash
./scripts/linux-tests.sh
```

Expected: exit 0 and final line `LINUX SUITE: GREEN`.

---

### Task 1: Retune guarded chunking to 8 code units / 5 ms

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs:12-23`
- Modify: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs:25-30,42,95,105-107`
- Modify: `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs` (append one test)
- Create: `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs`

**Interfaces:**
- Consumes: `TextInjector.ChunkCodeUnits` / `TextInjector.InterChunkPauseMs`
  (`internal const int`, visible to tests via `InternalsVisibleTo`),
  `InjectionChunker.Split(string text, int chunkSize)`.
- Produces: `TextInjector.ChunkCodeUnits == 8`, `TextInjector.InterChunkPauseMs == 5`.
  Later tasks rely on these exact values in sleep-sequence assertions
  (inter-chunk pauses of `5`).

- [ ] **Step 1: Update the chunk-size-sensitive tests to the new design point (failing first)**

In `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`:

Change line 30 (in `Guarded_StableFocus_SendsWholeText_InChunks`; the test's
`text` is `new string('a', 80)`) from:

```csharp
        sent.Count.ShouldBe(3); // ChunkCodeUnits = 32 => 32 + 32 + 16
```

to:

```csharp
        sent.Count.ShouldBe(10); // ChunkCodeUnits = 8 => ten chunks of 8
```

Change the stale comment on line 42 (in
`Guarded_FocusChange_MidSend_Interrupts_AndStopsSending`) from:

```csharp
        var text = new string('a', 96); // 3 chunks of 32
```

to:

```csharp
        var text = new string('a', 96); // 12 chunks of 8
```

Change the identical stale comment in `Guarded_ModifierPressed_MidSend_Interrupts`
(line 95) the same way:

```csharp
        var text = new string('a', 96); // 12 chunks of 8
```

In `Guarded_Paces_Between_Chunks_Only` change the comment and assertion
(lines 105 and 107) from:

```csharp
        var text = new string('a', 96); // 3 chunks => exactly 2 inter-chunk pauses
```
```csharp
        sleeps.ShouldBe(new[] { 20, 20 }); // TextInjector.InterChunkPauseMs
```

to:

```csharp
        var text = new string('a', 96); // 12 chunks => exactly 11 inter-chunk pauses
```
```csharp
        sleeps.ShouldBe(new[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }); // 11 x TextInjector.InterChunkPauseMs
```

Append this new test to the same class (before the closing brace):

```csharp
    [Fact]
    public void DesignPoint_FeedRateFloor_And_BleedBound()
    {
        // Spec constraint: the effective feed rate must never drop below the
        // original 1600 code units/s design point, and the worst-case bleed
        // into a newly focused window (<= 1 in-flight chunk, prior ledger
        // AD-1, hardened by this task) must not regress past 8 code units.
        (TextInjector.ChunkCodeUnits * 1000 / TextInjector.InterChunkPauseMs)
            .ShouldBeGreaterThanOrEqualTo(1600);
        TextInjector.ChunkCodeUnits.ShouldBeLessThanOrEqualTo(8);
    }
```

- [ ] **Step 2: Run the class to verify the new expectations fail against the current 32/20 constants**

```bash
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests"
```

Expected: FAIL — `Guarded_StableFocus_SendsWholeText_InChunks` (`sent.Count`
should be 10 but was 3), `Guarded_Paces_Between_Chunks_Only` (sleeps were
`[20, 20]`), and `DesignPoint_FeedRateFloor_And_BleedBound`
(`ChunkCodeUnits` should be <= 8 but was 32).

- [ ] **Step 3: Change the constants and their docs in TextInjector.cs**

In `src/Winpepper.Platform/Injection/TextInjector.cs`, replace lines 12–23:

```csharp
    /// <summary>UTF-16 code units per guarded send chunk (Task: mid-paste focus fallback).</summary>
    internal const int ChunkCodeUnits = 32;

    /// <summary>
    /// Pause between guarded send chunks. Load-bearing (validation ledger, A1):
    /// SendInput is queue-insertion (~µs per call), so an UNPACED loop finishes
    /// in single-digit milliseconds and the mid-paste guard could never observe
    /// a human focus change. 20 ms/chunk ≈ 1600 code units/s -- far faster than
    /// any typist, slow enough that a long paste spans the human reaction
    /// window (a 1000-unit paste ≈ 0.6 s).
    /// </summary>
    internal const int InterChunkPauseMs = 20;
```

with:

```csharp
    /// <summary>
    /// UTF-16 code units per guarded send chunk. Also the worst-case bleed
    /// bound: at most ~one in-flight chunk can land in a newly focused window
    /// when the user switches mid-paste (mid-paste focus fallback, AD-1 --
    /// hardened from 32 to 8 by the bleed-hardening task).
    /// </summary>
    internal const int ChunkCodeUnits = 8;

    /// <summary>
    /// Pause between guarded send chunks. Load-bearing (validation ledger, A1):
    /// SendInput is queue-insertion (~µs per call), so an UNPACED loop finishes
    /// in single-digit milliseconds and the mid-paste guard could never observe
    /// a human focus change. 5 ms per 8-unit chunk ≈ 1600 code units/s -- the
    /// same feed rate as the original 32/20 ms tuning (a 1000-unit paste
    /// ≈ 0.6 s), but the guard now runs 4x more often, shrinking the
    /// worst-case bleed into a newly focused window from ~32 to ~8 units.
    /// </summary>
    internal const int InterChunkPauseMs = 5;
```

- [ ] **Step 4: Run the class again to verify it passes**

Same command as Step 2. Expected: PASS, 0 failed.

- [ ] **Step 5: Pin surrogate safety at the production chunk size**

Append to `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs`
(inside the existing test class; the file already has
`using Winpepper.Platform.Injection;` and Shouldly):

```csharp
    [Fact]
    public void ProductionChunkSize_NeverSplitsSurrogatePair()
    {
        // 7 BMP chars put the emoji's high surrogate exactly at the chunk-8
        // boundary; the chunk must extend by one unit, never tear the pair.
        var text = new string('a', 7) + "\U0001F600" + new string('b', 4);
        var chunks = InjectionChunker.Split(text, TextInjector.ChunkCodeUnits);
        chunks[0].ShouldBe(new string('a', 7) + "\U0001F600"); // 9 units, pair intact
        string.Concat(chunks).ShouldBe(text);
    }
```

Run:

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.InjectionChunkerTests"
```

Expected: PASS (chunker behavior is unchanged; this locks the 8-unit case).

- [ ] **Step 6: Create the Windows pacing sentinel (HALT condition 1 detector)**

Create `tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs`:

```csharp
using System.Diagnostics;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

/// <summary>
/// Windows-host sentinel for the 5 ms inter-chunk pause. The guarded send
/// paces with Thread.Sleep(InterChunkPauseMs); on .NET 8+ / Windows 10 1803+
/// the runtime uses a high-resolution waitable timer, so Sleep(5) should wake
/// in single-digit milliseconds regardless of the legacy ~15.6 ms timer
/// quantum. If this test FAILS on the Windows gate, the real feed rate has
/// dropped below the 1600 code units/s design floor (a 1000-unit paste would
/// take ~2 s and stall the UI thread that long on a pill click). That is a
/// fundamental problem with the 8/5 retune: STOP and report -- do not widen
/// this threshold or swap in a spin-wait without explicit approval.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class InterChunkPacingWindowsTests
{
    [Fact]
    public void Sleep5ms_AverageStaysNearRequest_HighResolutionTimer()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Warm-up (JIT + timer state).
        for (var i = 0; i < 5; i++) Thread.Sleep(TextInjector.InterChunkPauseMs);

        const int iterations = 40;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) Thread.Sleep(TextInjector.InterChunkPauseMs);
        sw.Stop();

        var avgMs = sw.Elapsed.TotalMilliseconds / iterations;
        // 5 ms requested; generous jitter allowance, but strictly below the
        // ~15.6 ms legacy quantum that would break the feed-rate floor.
        avgMs.ShouldBeLessThan(10.0);
    }
}
```

- [ ] **Step 7: Verify the sentinel is excluded on Linux and the whole assembly is green**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: PASS, 0 failed (the sentinel is trait-filtered out on Linux; it
runs on the Windows gate in Task 5).

- [ ] **Step 8: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

```bash
git add src/Winpepper.Platform/Injection/TextInjector.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs \
        tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs \
        tests/Winpepper.Platform.Tests/Injection/InterChunkPacingWindowsTests.cs
git commit -m "feat(injection): retune guarded paste to 8-unit chunks at 5 ms

Same 1600 code units/s feed rate as the 32/20 tuning, but the per-chunk
halt guard now runs 4x more often -- worst-case mid-paste bleed into a
newly focused window drops from ~32 to ~8 code units (hardens AD-1).
Adds a design-point invariant test (feed-rate floor + bleed bound), a
surrogate-safety case at the production chunk size, and a Windows-gate
sentinel proving Thread.Sleep(5) wakes near-request on the host.

Linux suite green."
```

---

### Task 2: `MouseButtonGuard` — pure physical mouse-button probe

**Files:**
- Create: `src/Winpepper.Platform/Injection/MouseButtonGuard.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/MouseButtonGuardTests.cs`

**Interfaces:**
- Consumes: nothing (pure; probe is a `Func<int, bool>` parameter, same seam
  shape as `ModifierGuard.AnyDown`).
- Produces (Task 4 relies on these exact names):
  `public static class MouseButtonGuard` in namespace
  `Winpepper.Platform.Injection` with
  `public static readonly int[] MouseButtonVks` (= `{ 0x01, 0x02, 0x04 }`)
  and `public static bool AnyDown(Func<int, bool> isKeyDown)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/MouseButtonGuardTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public sealed class MouseButtonGuardTests
{
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkCancel = 0x03;  // NOT a mouse button
    private const int VkControl = 0x11; // modifier, not a mouse button

    [Fact]
    public void AnyDown_FalseWhenNothingHeld()
        => MouseButtonGuard.AnyDown(_ => false).ShouldBeFalse();

    [Theory]
    [InlineData(VkLButton)]
    [InlineData(VkRButton)]
    [InlineData(VkMButton)]
    public void AnyDown_TrueWhenAButtonIsHeld(int heldVk)
        => MouseButtonGuard.AnyDown(vk => vk == heldVk).ShouldBeTrue();

    [Fact]
    public void AnyDown_IgnoresNonMouseVks()
        => MouseButtonGuard.AnyDown(vk => vk is VkCancel or VkControl).ShouldBeFalse();

    [Fact]
    public void MouseVks_StayDisjointFromModifierVks()
        // ModifierVks drives WaitForRelease (1500 ms block) AND the KEYUP
        // neutralization prelude -- a mouse VK in that set would synthesize a
        // meaningless keyboard KEYUP for a mouse button. Keep the sets apart.
        => MouseButtonGuard.MouseButtonVks.Intersect(ModifierGuard.ModifierVks).ShouldBeEmpty();
}
```

- [ ] **Step 2: Run to verify it fails to compile (type does not exist)**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — `CS0103: The name 'MouseButtonGuard' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/MouseButtonGuard.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Detects physically-held mouse buttons for the guarded-injection halt and
/// prelude logic. A click-to-switch focus change starts with a button going
/// DOWN before the foreground flips, so button-down is the earliest
/// observable leading edge of a click halt gesture -- the mouse analogue of
/// the modifier check (Alt is down before Alt+Tab flips the foreground).
///
/// Deliberately SEPARATE from <see cref="ModifierGuard.ModifierVks"/>: that
/// set also drives the neutralization prelude, which synthesizes keyboard
/// KEYUPs on timeout. There is no safe mouse analogue (synthesizing a mouse
/// button-up would fabricate a click), so mouse buttons are only ever
/// OBSERVED, never synthesized.
///
/// VK_LBUTTON/VK_RBUTTON are LOGICAL buttons (Windows applies the user's
/// swap-buttons setting); this checks the union, so the swap is irrelevant.
/// Pure managed; the probe is injectable and fail-open: a probe that cannot
/// observe reports "up", so a failed observation never halts a paste.
/// </summary>
public static class MouseButtonGuard
{
    /// <summary>VK_LBUTTON, VK_RBUTTON, VK_MBUTTON.</summary>
    public static readonly int[] MouseButtonVks = { 0x01, 0x02, 0x04 };

    /// <summary>Whether any mouse button is reported down by the probe.</summary>
    public static bool AnyDown(Func<int, bool> isKeyDown)
    {
        foreach (var vk in MouseButtonVks)
            if (isKeyDown(vk)) return true;
        return false;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.MouseButtonGuardTests"
```

Expected: PASS, 6 tests (1 + 3 theory cases + 2), 0 failed.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

```bash
git add src/Winpepper.Platform/Injection/MouseButtonGuard.cs \
        tests/Winpepper.Platform.Tests/Injection/MouseButtonGuardTests.cs
git commit -m "feat(injection): add MouseButtonGuard -- pure physical mouse-button probe

VK_LBUTTON/VK_RBUTTON/VK_MBUTTON via the existing injectable isKeyDown
seam. Kept deliberately disjoint from ModifierGuard.ModifierVks (which
also drives the KEYUP-synthesizing neutralization prelude -- there is no
safe mouse analogue). Observed only, never synthesized; fail-open.

Linux suite green."
```

---

### Task 3: Route the modifier-release wait through the `_sleep` seam

The modifier prelude at `TextInjector.cs:109-110` passes `Thread.Sleep`
literally instead of the injected `_sleep`. Today this is invisible (every
test passes `isKeyDown: _ => false`, so the wait returns immediately), but
Task 4's mouse-wait tests drive `isKeyDown` through held→released
transitions using recorded virtual sleeps — the modifier prelude must honor
the same seam or such tests would wall-clock-sleep and, on timeout, hit real
`SendInput` P/Invoke on Linux.

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs:109-110`
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: `ModifierGuard.WaitForRelease(Func<bool> anyDown, int timeoutMs,
  int pollMs, Action<int> sleep)`; `TextInjector._sleep` (ctor param `sleep`).
- Produces: the guarantee "every sleep in `TryInjectGuarded` goes through the
  injected `sleep` seam" — Task 4's tests assert exact sleep sequences that
  interleave prelude polls (15 ms) with inter-chunk pauses (5 ms).

- [ ] **Step 1: Write the failing test**

Append to `TextInjectorGuardedTests.cs`:

```csharp
    [Fact]
    public void Guarded_ModifierWait_UsesInjectedSleep_NeverWallClock()
    {
        // The modifier-release prelude must poll through the injected sleep
        // seam (_sleep), not Thread.Sleep -- virtual-time tests depend on it.
        var sleeps = new List<int>();
        var held = true;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x11 && held, // Ctrl held...
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: ms =>
            {
                sleeps.Add(ms);
                if (sleeps.Count >= 2) held = false; // ...released after 2 polls
            });

        // "hi" = 1 chunk => no inter-chunk pauses; the only sleeps are the
        // two 15 ms modifier-wait polls, recorded through the seam.
        injector.TryInjectGuarded("hi").ShouldBe(InjectionRunOutcome.Completed);

        sleeps.ShouldBe(new[] { 15, 15 });
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests"
```

Expected: FAIL. With the bug, the injected `sleep` is never called, so `held`
never flips; the prelude wall-clock-sleeps the full 1500 ms and then
`NeutralizeHeldModifiers` attempts a real `SendInput` P/Invoke, which throws
on Linux (`DllNotFoundException`/`PlatformNotSupportedException`) — a loud,
honest red either way.

- [ ] **Step 3: Fix the seam**

In `src/Winpepper.Platform/Injection/TextInjector.cs`, inside
`NeutralizeHeldModifiers()`, change:

```csharp
        if (!ModifierGuard.WaitForRelease(() => ModifierGuard.AnyDown(_isKeyDown),
                ModifierWaitTimeoutMs, ModifierWaitPollMs, Thread.Sleep))
```

to:

```csharp
        if (!ModifierGuard.WaitForRelease(() => ModifierGuard.AnyDown(_isKeyDown),
                ModifierWaitTimeoutMs, ModifierWaitPollMs, _sleep))
```

- [ ] **Step 4: Run to verify it passes**

Same command as Step 2. Expected: PASS, 0 failed.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

```bash
git add src/Winpepper.Platform/Injection/TextInjector.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "fix(injection): route modifier-release wait through the injected sleep seam

NeutralizeHeldModifiers passed Thread.Sleep literally, bypassing the
_sleep ctor seam. Invisible today (tests never hold a modifier), but
virtual-time tests for the upcoming mouse-release prelude require every
sleep in TryInjectGuarded to be injectable.

Linux suite green."
```

---

### Task 4: Mouse-button halt — release-wait prelude + per-chunk predicate

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (new constants,
  prelude, composed predicate, XML doc, log message)
- Modify: `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`
  (rename `modifierHeld` → `physicalInputDown`; doc)
- Modify: `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs:133`
  (named argument follows the rename)
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`
  (five new tests)

**Interfaces:**
- Consumes: `MouseButtonGuard.AnyDown(Func<int, bool>)` (Task 2),
  `ModifierGuard.WaitForRelease(Func<bool>, int, int, Action<int>)` (existing,
  pure — reused for the mouse wait), `_sleep`-seam guarantee (Task 3),
  `TextInjector.InterChunkPauseMs == 5` (Task 1).
- Produces: `GuardedInjectionRun.Execute(IReadOnlyList<string> chunks,
  long hwndAtSendStart, Func<long> currentForegroundHwnd,
  Func<string, bool> sendChunk, Func<bool>? physicalInputDown = null,
  Action? pauseBetweenChunks = null)` — note the renamed optional parameter.
  Behavioral contract for Task 5's messaging: `TryInjectGuarded` returns
  `Interrupted` when a mouse button goes down mid-run OR stays held past the
  1500 ms prelude timeout, and never starts typing while a button is down.

- [ ] **Step 1: Write the failing tests**

Append to `TextInjectorGuardedTests.cs`:

```csharp
    [Fact]
    public void Guarded_PillClick_ButtonStillDownAtStart_WaitsForRelease_ThenSendsAll()
    {
        // THE pill-click regression pin: TryPastePending runs inside the
        // pill's PointerPressed handler (button-DOWN edge) on the UI thread,
        // so VK_LBUTTON is still physically down when injection starts. The
        // guard must WAIT for the release (GetAsyncKeyState reads physical
        // state -- observable even though the blocked UI thread never pumps
        // the pointer-up message), then paste ALL the text. It must never
        // self-cancel on the click that requested the paste.
        var sleeps = new List<int>();
        var buttonDown = true;
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && buttonDown,
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: ms => { sleeps.Add(ms); if (sleeps.Count >= 3) buttonDown = false; });
        var text = new string('a', 16); // 2 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
        // Three 15 ms release-wait polls, then the single 5 ms inter-chunk pause.
        sleeps.ShouldBe(new[] { 15, 15, 15, 5 });
    }

    [Fact]
    public void Guarded_ButtonHeldPastTimeout_Interrupts_SendsNothing()
    {
        // No safe neutralization exists for a mouse button (a synthesized
        // button-up would fabricate a click), so a button held past the
        // bounded wait ABORTS the run: the caller keeps the FULL text
        // pending; nothing is sprayed under the still-held pointer.
        var sleeps = new List<int>();
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01, // left button held forever
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: sleeps.Add);

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
        sleeps.Sum().ShouldBe(1500); // waited the full bounded budget, no longer
    }

    [Fact]
    public void Guarded_MouseButtonPressed_MidSend_Interrupts_AfterPrefixOnly()
    {
        // Click-to-switch: the button-down is the leading edge of the focus
        // change, observable BEFORE the foreground flips (Alt+Tab's modifier
        // analogue). Halt on it; the caller parks the full text.
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && sent.Count >= 1, // click after chunk 1
            foregroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => { });
        var text = new string('a', 24); // 3 chunks of 8

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Guarded_FocusChange_DuringMouseWait_SendsNothing()
    {
        // The HWND baseline is captured at method ENTRY, before both
        // preludes: if the user's click lands in another window while we
        // wait for the button release, the pre-chunk-0 check catches it and
        // nothing is typed into the new window.
        var sent = new List<string>();
        var probes = 0;
        var buttonDown = true;
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: vk => vk == 0x01 && buttonDown,
            foregroundHwnd: () => ++probes == 1 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; },
            sleep: _ => buttonDown = false); // released after the first poll

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_MouseProbeUnavailable_FailOpen_SendsEverything()
    {
        // A probe that cannot observe reports "up" (GetAsyncKeyState has no
        // error channel; non-Windows returns false). We never hold a paste
        // because we merely failed to observe.
        var sent = new List<string>();
        var injector = NewInjector(() => 42, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }
```

- [ ] **Step 2: Run to verify the meaningful failures**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests"
```

Expected: FAIL —
`Guarded_PillClick_ButtonStillDownAtStart_WaitsForRelease_ThenSendsAll`
(sleeps were `[5]`, no release-wait exists yet),
`Guarded_ButtonHeldPastTimeout_Interrupts_SendsNothing` (outcome was
`Completed`, text was sent),
`Guarded_MouseButtonPressed_MidSend_Interrupts_AfterPrefixOnly` (outcome was
`Completed`). The two others pass already (they pin invariants the change
must not break).

- [ ] **Step 3: Rename the run parameter and extend its doc**

In `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`:

Replace the class XML summary (lines 6–20) with:

```csharp
/// <summary>
/// Pure driver for an interruptible, chunked, PACED injection send. The pause
/// runs BETWEEN chunks (never before the first): without pacing the whole
/// loop completes in single-digit milliseconds (SendInput is queue-insertion,
/// ~µs per call) and no human focus change could ever be observed mid-run.
/// Before EVERY chunk (including the first -- the pre-send release waits can
/// delay the first keystroke) it checks, in order: has a physical modifier or
/// mouse button gone down (the leading edge of a halt gesture -- Alt is down
/// before Alt+Tab changes the foreground; a mouse button is down before a
/// click flips it), then asks <see cref="MidPasteDecider"/> whether the
/// window we started typing into is still foreground. On either halt it stops
/// immediately and reports <see cref="InjectionRunOutcome.Interrupted"/> so
/// the caller can hold the WHOLE original text as a pending paste. All Win32
/// access is behind the delegates, so this loop is fully unit-testable on
/// Linux.
/// </summary>
```

Rename the parameter (line 28) from:

```csharp
        Func<bool>? modifierHeld = null,
```

to:

```csharp
        Func<bool>? physicalInputDown = null,
```

and its use (line 41) from:

```csharp
            if (modifierHeld?.Invoke() == true)
```

to:

```csharp
            if (physicalInputDown?.Invoke() == true)
```

In `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`
line 133 (inside `ModifierDown_MidRun_Interrupts_AfterPrefixOnly`), change
the named argument:

```csharp
            modifierHeld: () => sent.Count >= 1); // "Alt goes down" after chunk 1
```

to:

```csharp
            physicalInputDown: () => sent.Count >= 1); // "Alt goes down" after chunk 1
```

- [ ] **Step 4: Add the mouse prelude and composed predicate to TextInjector**

In `src/Winpepper.Platform/Injection/TextInjector.cs`:

**4a.** After the existing `ModifierWaitTimeoutMs`/`ModifierWaitPollMs`
constants (lines 8–10), add:

```csharp
    /// <summary>
    /// How long to wait for a physically-held mouse button to be released
    /// before the guarded send starts. The pending-paste pill fires on
    /// PointerPressed (the button-DOWN edge) and TryPastePending runs
    /// synchronously inside that handler, so at entry the initiating button
    /// is still down -- without this wait the mouse half of the halt
    /// predicate would self-cancel every pill click (deterministically, not
    /// as a race). GetAsyncKeyState reads physical device state, so the
    /// release is observable even though the blocked UI thread never pumps
    /// the pointer-up message. Unlike modifiers there is no safe
    /// neutralization on timeout (a synthesized button-up would fabricate a
    /// click), so a button still held past this budget aborts the run and
    /// the text stays pending.
    /// </summary>
    private const int MouseWaitTimeoutMs = 1500;
    private const int MouseWaitPollMs = 15;
```

**4b.** In `TryInjectGuarded`, after `NeutralizeHeldModifiers();` and before
`var chunks = ...`, insert:

```csharp
        // Mouse prelude: never START typing while a button is physically
        // down (the pill click that requested this paste is the common
        // case). Timeout => abort, keep the text pending -- never spray.
        if (!ModifierGuard.WaitForRelease(() => MouseButtonGuard.AnyDown(_isKeyDown),
                MouseWaitTimeoutMs, MouseWaitPollMs, _sleep))
        {
            _log.LogInformation(
                "Mouse button still held {Timeout}ms after injection was requested; not typing -- text stays pending",
                MouseWaitTimeoutMs);
            return InjectionRunOutcome.Interrupted;
        }
```

**4c.** Change the `GuardedInjectionRun.Execute` call's predicate argument
from:

```csharp
            modifierHeld: () => ModifierGuard.AnyDown(_isKeyDown),
```

to:

```csharp
            physicalInputDown: () => ModifierGuard.AnyDown(_isKeyDown)
                                     || MouseButtonGuard.AnyDown(_isKeyDown),
```

**4d.** Update the interruption log (line 94) from:

```csharp
            _log.LogInformation("Injection interrupted: foreground window or physical modifier state changed mid-paste");
```

to:

```csharp
            _log.LogInformation("Injection interrupted: foreground window, physical modifier, or mouse button state changed mid-paste");
```

**4e.** Replace the `TryInjectGuarded` XML summary (lines 57–78) with:

```csharp
    /// <summary>
    /// Interruptible paste: types the text in chunks of
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, pausing
    /// <see cref="InterChunkPauseMs"/> between chunks (pacing is what makes
    /// the guard able to observe a human halt gesture at all -- an unpaced
    /// loop is queue-insertion-fast and finishes in milliseconds) and
    /// checking before every chunk that (a) no physical modifier has gone
    /// down (the leading edge of Alt+Tab -- injected Unicode is delivered
    /// with the current physical modifier state applied), (b) no physical
    /// mouse button has gone down (the leading edge of a click-to-switch --
    /// the button is down BEFORE the foreground flips), and (c) the window
    /// that was foreground when this method was entered is STILL foreground.
    /// If any check trips, the remaining chunks are not sent and
    /// <see cref="InjectionRunOutcome.Interrupted"/> is returned so the
    /// caller can hold the WHOLE original text as a pending paste.
    /// The baseline is captured at method entry -- BEFORE the modifier
    /// release-wait (up to 1500 ms) and the mouse release-wait (up to
    /// 1500 ms) -- so a focus change during either wait is caught before the
    /// first keystroke. The modifier check cannot re-trip on its prelude's
    /// timeout: NeutralizeHeldModifiers synthesizes KEYUPs, so after it
    /// returns the observable modifier state is up. The mouse check cannot
    /// self-trip on the pill click that requested the paste: the mouse
    /// prelude waits for the initiating button's release before the run
    /// starts, and a button still held past the timeout ABORTS the run
    /// (Interrupted; the pending slot keeps the full text) because there is
    /// no safe mouse neutralization -- a synthesized button-up would
    /// fabricate a click. Fail-open: if the foreground window cannot be
    /// determined (probe returns 0) the HWND guard is disabled, and a
    /// key/button probe that cannot observe reports "up" and never halts.
    /// </summary>
```

- [ ] **Step 5: Run the injection test classes to verify everything passes**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.TextInjectorGuardedTests"
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.GuardedInjectionRunTests"
```

Expected: PASS, 0 failed in both classes (including all pre-existing tests —
notably `Guarded_FocusChange_DuringModifierWait_SendsNothing` and
`ModifierDown_MidRun_Interrupts_AfterPrefixOnly`).

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

```bash
git add src/Winpepper.Platform/Injection/TextInjector.cs \
        src/Winpepper.Platform/Injection/GuardedInjectionRun.cs \
        tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs \
        tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs
git commit -m "feat(injection): halt mid-paste on physical mouse-button-down

Click-to-switch previously had no early halt signal (the modifier check
only covers Alt+Tab; the HWND check fires after the flip). The per-chunk
predicate (renamed physicalInputDown) now also observes
VK_LBUTTON/VK_RBUTTON/VK_MBUTTON. A release-wait prelude (bounded
1500 ms, via the injected sleep seam) keeps the pill's own
PointerPressed click from self-cancelling the paste; a button still held
past the budget aborts with Interrupted -- the pending slot keeps the
full text, nothing is sprayed. Mouse buttons are observed only, never
synthesized. Fail-open preserved.

Linux suite green."
```

---

### Task 5: App-layer message accuracy + full verification

`PipelineHost.cs` is `#if WINDOWS` with no test-project coverage (repo
convention: this glue layer is verified by the Windows gate build + the
manual smoke checklist). This task updates its now-inaccurate halt-cause
comments/log strings and runs the full gates.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (strings/comments only —
  in `TryPastePending` and in the two `EnterPendingPaste`-on-interrupt arms)

**Interfaces:**
- Consumes: `TextInjector.TryInjectGuarded` outcome semantics from Task 4
  (unchanged enum; new halt cause). No signature changes anywhere in this
  task — text edits only.
- Produces: nothing consumed by other tasks (terminal task).

- [ ] **Step 1: Update the interrupted-paste wording in PipelineHost.cs**

Three string edits and two comment edits (no logic changes):

In `TryPastePending` (~line 56), change:

```csharp
            _log.LogInformation(
                "Pending paste interrupted (focus or modifier change); slot kept with full text for another click");
```

to:

```csharp
            _log.LogInformation(
                "Pending paste interrupted (focus, modifier, or mouse-button change); slot kept with full text for another click");
```

In the hold arm (`HotkeyEventKind.HoldUp`, ~lines 697–708 of the current
file), change the comment:

```csharp
                            // Focus moved to another window (or a halt-gesture
                            // modifier went down) while the keystrokes
```

to:

```csharp
                            // Focus moved to another window (or a halt-gesture
                            // modifier or mouse button went down) while the keystrokes
```

and the log:

```csharp
                                "Injection interrupted (focus or modifier change); held full text as pending paste ({Chars} chars)",
```

to:

```csharp
                                "Injection interrupted (focus, modifier, or mouse-button change); held full text as pending paste ({Chars} chars)",
```

Make the identical comment + log edits in the toggle arm
(`HotkeyEventKind.Toggle` — the byte-identical block with `final2`/`outcome2`
locals). Use grep to find every remaining occurrence and confirm none are
missed:

```bash
grep -rn "focus or modifier" src/
```

Expected after the edits: no matches.

- [ ] **Step 2: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0 (PipelineHost is not compiled on
Linux; this proves no collateral damage).

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "docs(app): reflect mouse-button halt cause in PipelineHost logs and comments

String/comment accuracy only -- the guarded injection now also halts on
physical mouse-button-down. No logic changes. Compile proven by the
Windows gate.

Linux suite green (App layer not built on Linux)."
```

- [ ] **Step 3: Run the full Windows gate (MANDATORY before any push)**

```bash
./scripts/windows-gate.sh
```

Expected: exit 0 and `GATE: GREEN` (~20–40 min; builds `Winpepper.App` —
proving the PipelineHost and injection changes compile on
`net9.0-windows` — and runs all test DLLs including the Windows-trait
tests).

**This run executes `InterChunkPacingWindowsTests.Sleep5ms_AverageStaysNearRequest_HighResolutionTimer`
for the first time.** If it FAILS: this is HALT CONDITION 1 — the 5 ms pause
quantizes to the legacy timer resolution and the feed-rate floor is broken.
STOP and report per the halt conditions at the top of this plan. Do not
widen the threshold, do not swap in a spin-wait, do not proceed.

- [ ] **Step 4: Manual Windows smoke checklist (behavioral proof for the `#if WINDOWS` glue)**

Perform on the Windows host with the freshly built app:

1. Dictate a long sentence (>200 chars) into Notepad; let it complete —
   full text lands, pacing feels instant (~0.6 s per 1000 chars, same as
   before the change).
2. Dictate a long sentence, then click into a DIFFERENT window mid-paste —
   typing stops with visibly fewer stray characters in the new window than
   the old ~dozen-plus (bound is now ~8), pill shows "Click to paste",
   clicking it pastes the FULL text.
3. Dictate, Alt+Tab mid-paste — still halts early (modifier path unchanged),
   full text parked.
4. Click the pending pill normally — the full text pastes into the focused
   field (the release-wait must not break the ordinary click).
5. Click the pending pill and HOLD the button ~1 s before releasing — the
   paste still lands in full once released (release-wait path).
6. Click the pending pill and keep the button held >2 s — nothing is typed,
   the pill stays in "Click to paste"; release and click again — full text
   pastes (timeout-abort path keeps the slot).

If item 4 or 5 fails (pill click self-cancels or loops): HALT CONDITION 2 —
stop and report. If item 2 shows no bleed reduction: HALT CONDITION 3.

---

## Self-Review

Checked the plan against the spec from a fresh read:

1. **Spec coverage:** (a) 32/20 → 8/5 retune with identical 1600 units/s —
   Task 1 (constants, docs, feed-rate-floor test, timing-claim recheck: the
   ~0.6 s/1000-units claim stays true and stays in the doc; the "verify no
   assumption depends on chunk size 32" sweep is embedded in the File
   Structure "Not touched" list, and the two hard-breaking tests + two stale
   comments are all addressed). Per-chunk INPUT arithmetic (8 chars = 16
   INPUT structs per `SendInput`) verified against `BuildKeyDownUpInputs` —
   nothing else assumes 64-struct batches. (b) Mouse-button halt — Tasks 2+4
   (predicate via the same `isKeyDown` seam, Linux-testable); pill-click
   self-cancel subtlety — Task 4's release-wait prelude with bounded timeout
   and abort-not-spray policy, pinned by three dedicated tests; hold/toggle
   paths halt on mouse-button-down through the same composed predicate
   (single `TryInjectGuarded` entry point for all three paste sites).
   Constraints: feed rate (invariant test + Windows sentinel), full-text
   pending (no Core changes; existing `Interrupted` handling reused),
   fail-open (explicit test), AGENTS.md gates (every task ends with the
   Linux script; Task 5 runs the Windows gate). User's stop condition:
   HALT CONDITIONS section + concrete detectors (sentinel test, smoke items).
2. **No silent deferrals:** every requirement has a production code path and
   a proving outcome. The only test doubles are the established injectable
   delegate seams (`isKeyDown`/`foregroundHwnd`/`sendChunk`/`sleep`), whose
   production defaults (`GetAsyncKeyState`, `GetForegroundWindow`,
   `SendInput`, `Thread.Sleep`) are exercised end-to-end by the Windows gate
   build + the Task 5 smoke checklist — same verification gradient the
   shipped predecessor feature used. No stubs awaiting replacement.
3. **Placeholder scan:** no TBDs, no "add error handling", every code step
   shows the code, every command shows expected output.
4. **Type consistency:** `MouseButtonGuard.AnyDown(Func<int,bool>)` (Task 2)
   matches Task 4's usage; `physicalInputDown` rename is applied at the
   definition, the only production call site, and the only named-argument
   test site; sleep-sequence assertions use Task 1's `5` and the existing
   `15` poll/`1500` timeout constants consistently; `Interrupted` semantics
   in Task 5's wording match Task 4's contract.
