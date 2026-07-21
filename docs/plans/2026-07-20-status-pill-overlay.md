# Status Pill Overlay Improvements Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make Winpepper's dictation status pill reliably stay on top, show a
live voice meter while recording, animate a "thinking" pulse after hotkey
release, and stop the onboarding model download from crashing the wizard.

**Architecture:** All decision/state logic lives in pure-managed, unit-tested
classes in `Winpepper.Core` (a level-meter model, a stage→animation-mode
mapper, `SessionViewModel.InputLevel` plumbing, and an onboarding download
error path). The WinUI 3 layers (`StatusPillWindow`, native window styles,
onboarding page) stay thin: they bind to / call the tested Core logic and are
verified by a Windows-only manual smoke test. Native always-on-top is enforced
via Win32 `SetWindowPos(HWND_TOPMOST)` re-asserted on every show and on the
existing 100 ms tick.

**Tech Stack:** C# / .NET 9, WinUI 3 (Windows App SDK), xUnit v3 + Shouldly
for tests, NAudio (existing, untouched), Win32 interop via P/Invoke.

## Global Constraints

- **Do NOT touch** the keyboard hook (`HotkeyHook` / `Winpepper.Platform.Hotkeys`)
  or the `packaging/` directory. Nothing in this plan modifies them.
- **Test runner:** the VSTest host (`dotnet test`) **crashes on this machine**.
  Pure-managed tests MUST run via the xUnit v3 **in-process runner**:
  `dotnet exec <TestAssembly>.dll`. Never use `dotnet test`.
- **Runner output shape (verified on this box):** the in-process runner does NOT
  print a `Passed:` line. It prints a summary of the form:
  `=== TEST EXECUTION SUMMARY === <Assembly>  Total: N, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: ...`.
  The **canonical green predicate** for every test step below is:
  **exit code 0 AND `Errors: 0` AND `Failed: 0`**. `Errors` (discovery/collection
  failures) is reported separately from `Failed` (assertion failures) — both must
  be zero. Do NOT grep for `Passed:` (no such token exists); parse `Total:/Errors:/Failed:`.
- **.NET SDK:** `.NET 9` may need local provisioning into the worktree at
  `./.dotnet` (which is gitignored). Task 0 provisions it; every later test
  step re-exports `DOTNET_ROOT`/`PATH`.
- **Target framework for tests:** `net9.0` (test dlls build to
  `bin/Debug/net9.0/`).
- **Pure logic in Core, thin rendering in App:** peak extraction, smoothing,
  decay, throttling, stage→animation mapping, and the download error path are
  pure-managed and unit-tested. WinUI z-order / animation / rendering cannot
  run on Linux — keep those layers thin and verify them in the Windows smoke
  test (Task 9).
- **Windows-only code** stays inside `#if WINDOWS ... #endif` (matches existing
  `StatusPillWindow`, `ExtendedWindowStyle`, `PipelineHost`).
- **Commit** after every task (frequent, atomic commits).

---

## File Structure

**New pure-managed files (testable, in `Winpepper.Core`):**

- `src/Winpepper.Core/Audio/LevelMeterModel.cs` — converts raw mono float
  frames into a smoothed 0..1 loudness value (peak + asymmetric attack/decay
  smoothing + clamp). No timers, no UI. (Task 2)
- `src/Winpepper.Core/ViewModels/PillAnimationMode.cs` — enum
  `{ None, VoiceLevel, Thinking }`. (Task 3)
- `src/Winpepper.Core/ViewModels/PillAnimationMap.cs` — pure
  `SessionStage → PillAnimationMode` mapping. (Task 3)

**New test files (`Winpepper.Core.Tests`):**

- `tests/Winpepper.Core.Tests/Audio/LevelMeterModelTests.cs` (Task 2)
- `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs` (Task 3)
- `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelInputLevelTests.cs` (Task 4)
- `tests/Winpepper.Core.Tests/ViewModels/OnboardingDownloadErrorTests.cs` (Task 8)

**Modified files:**

- `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs` — add `WS_EX_TOPMOST`
  + `SetWindowPos` + `AssertTopmost` helper. (Task 1)
- `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` — assert topmost on show
  and on tick; drive Dot scale from `InputLevel`; run "thinking" pulse via the
  existing tick using `PillAnimationMap`. (Tasks 1, 5, 6)
- `src/Winpepper.App/Views/StatusPillWindow.xaml` — add a `ScaleTransform` on
  the Dot ellipse. (Task 5)
- `src/Winpepper.Core/ViewModels/SessionViewModel.cs` — add `InputLevel`
  property + `ReportAudioFrame` + reset-on-leave-Recording. (Task 4)
- `src/Winpepper.App/Hosting/PipelineHost.cs` — subscribe the live recorder's
  `FramesAvailable` to `SessionViewModel.ReportAudioFrame`. (Task 7)
- `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs` — wrap the download
  invocation in try/catch, expose `DownloadError`. (Task 8)
- `src/Winpepper.App/Views/OnboardingPage.xaml` — inline error text + Retry
  button. (Task 8)
- `src/Winpepper.App/Views/OnboardingPage.xaml.cs` — render `DownloadError`,
  wire Retry. (Task 8)

---

## Verified anchors (re-checked at commit 96b7ef9)

- `StatusPillWindow.xaml.cs`: ctor line 22; `MakeClickThroughTopmostTool` call
  line 31; `OverlappedPresenter.IsAlwaysOnTop=true` line 38; `_tickTimer`
  (100 ms) created lines 49-50; `OnVmChanged` lines 56-91; Error show path
  lines 72-73; non-idle show path lines 86-88.
- `StatusPillWindow.xaml`: `<Ellipse x:Name="Dot" .../>` line 9.
- `ExtendedWindowStyle.cs`: `MakeClickThroughTopmostTool` lines 22-30; sets
  `WS_EX_LAYERED|WS_EX_TRANSPARENT|WS_EX_TOOLWINDOW|WS_EX_NOACTIVATE`, does NOT
  set `WS_EX_TOPMOST`, does NOT call `SetWindowPos`.
- `WasapiRecorder.cs`: `event Action<ReadOnlyMemory<float>>? FramesAvailable`
  line 11; raised at line 96.
- `SessionViewModel.cs`: `Stage` setter lines 30-34; `_ui` (IUiThread) field;
  `Raise` helper line 121.
- `SessionStage.cs`: `{ Idle, Recording, Transcribing, CleaningUp, Injecting,
  Error }`.
- `PipelineHost.cs`: recorder constructed + started at lines 176-177 (HoldDown)
  and 335-336 (Toggle); `_vm` field is the `SessionViewModel`.
- `AppShell.cs`: `sessionVm` created line 61; `PipelineHost` created line 230;
  `StatusPillWindow` created line 273 (all wiring already exists — no change
  needed there).
- `OnboardingViewModel.cs`: ctor lines 28-33; `AdvanceAsync` lines 115-137;
  `DownloadModels` case lines 128-131 (`await _runDownloader()`); `CanSkip`
  line 113.
- `OnboardingPage.xaml.cs`: `OnAdvance` has a try/**finally with no catch**
  (~lines 104-118); `RenderStep`/`RefreshButtons` render per-step UI.
- `OnboardingPage.xaml`: `DownloadPanel` lines 81-101; buttons row lines 143-146.

> If any line number has drifted, locate the same symbol by name — the code
> shapes above are exact quotes.

---

## Task 0: Provision .NET 9 SDK and establish a green baseline

**Files:**
- No source changes. Environment + baseline only.

**Interfaces:**
- Consumes: nothing.
- Produces: a working `dotnet` at `./.dotnet`, and the command shape every
  later task's test step reuses:
  `dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll`.

- [ ] **Step 1: Provision the SDK into the worktree (idempotent)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/status-pill-overlay
if [ ! -x ".dotnet/dotnet" ]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  chmod +x /tmp/dotnet-install.sh
  /tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
fi
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a 9.0.x already present).

- [ ] **Step 2: Build the Core test project**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug
```
Expected: `Build succeeded`. (Windows-only projects like `Winpepper.App` are
NOT built on Linux — only Core and its tests.)

- [ ] **Step 3: Run the existing Core suite as a baseline (in-process runner)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, a line like `Passed: N, Failed: 0`. This is the green
baseline — all later suites must stay green.

- [ ] **Step 4: Commit (baseline marker — plan doc only, no code yet)**

```bash
git add docs/plans/2026-07-20-status-pill-overlay.md
git commit -m "docs: baseline note for status-pill-overlay plan execution"
```
(If the plan file is already committed, skip this commit — nothing else changed
in Task 0.)

---

## Task 1: Native strict always-on-top helper (`ExtendedWindowStyle`)

Add `WS_EX_TOPMOST` to the extended styles and a reusable `AssertTopmost`
helper that calls `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)`.
This is Windows-only P/Invoke; it cannot run on Linux and is verified in the
Task 9 smoke test. Keep click-through / no-activate semantics intact.

**Files:**
- Modify: `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ExtendedWindowStyle.MakeClickThroughTopmostTool(IntPtr hwnd, byte alpha)`
  (now also ORs `WS_EX_TOPMOST` and asserts topmost once) and a new
  `static void ExtendedWindowStyle.AssertTopmost(IntPtr hwnd)`.

- [ ] **Step 1: Add the topmost constants and `SetWindowPos` P/Invoke**

Replace the constant block and add the import. In
`src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`, change the constants
region (currently lines 8-13) and the P/Invoke region (currently lines 15-20)
to:

```csharp
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOPMOST     = 0x00000008;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_ALPHA         = 0x00000002;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
```

- [ ] **Step 2: OR in `WS_EX_TOPMOST` and assert topmost inside `MakeClickThroughTopmostTool`; add `AssertTopmost`**

Replace the `MakeClickThroughTopmostTool` method body (currently lines 22-30)
with:

```csharp
    public static void MakeClickThroughTopmostTool(IntPtr hwnd, byte alpha = 230)
    {
        // ORDER MATTERS: read existing styles, OR in TOPMOST + LAYERED +
        // TRANSPARENT + TOOLWINDOW + NOACTIVATE, commit with SetWindowLongPtr
        // BEFORE calling SetLayeredWindowAttributes. WS_EX_TOPMOST is the
        // *style* bit; SetWindowPos(HWND_TOPMOST) is what actually inserts us
        // into the topmost band. We do both, and never activate/steal focus.
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated  = existing | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        AssertTopmost(hwnd);
    }

    /// <summary>
    /// Re-inserts the window at the top of the z-order (HWND_TOPMOST) without
    /// moving, resizing, or activating it. Cheap; safe to call on every show
    /// and on a periodic tick. Other topmost windows created later can sit
    /// above us, so callers should re-assert whenever the pill becomes visible
    /// and while it stays visible.
    /// </summary>
    public static void AssertTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
```

- [ ] **Step 3: Verify the Core test suite still builds/passes (no Core change, sanity only)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, `Failed: 0`. (The App project is Windows-only and is not
compiled on Linux; correctness of this native change is confirmed in Task 9.
This step only proves nothing in Core regressed.)

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs
git commit -m "feat: add WS_EX_TOPMOST + AssertTopmost helper for strict pill z-order"
```

---

## Task 2: `LevelMeterModel` — pure voice-level computation (TDD)

A pure class that turns raw mono float frames into a smoothed 0..1 level with
fast attack and slow decay, so the pill's meter pulses naturally with speech.
Fully deterministic and unit-tested.

**Files:**
- Create: `src/Winpepper.Core/Audio/LevelMeterModel.cs`
- Test: `tests/Winpepper.Core.Tests/Audio/LevelMeterModelTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `namespace Winpepper.Core.Audio;`
  - `public sealed class LevelMeterModel`
    - ctor `LevelMeterModel(double attack = 0.5, double decay = 0.15)`
    - `double Level { get; }`
    - `static double Peak(ReadOnlySpan<float> frame)` → abs-peak clamped 0..1
    - `double Push(ReadOnlySpan<float> frame)` → new smoothed level 0..1
    - `void Reset()`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/Audio/LevelMeterModelTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Audio;
using Xunit;

namespace Winpepper.Core.Tests.Audio;

public class LevelMeterModelTests
{
    [Fact]
    public void Peak_ReturnsMaxAbsoluteSample()
    {
        var frame = new float[] { 0.1f, -0.9f, 0.3f };
        LevelMeterModel.Peak(frame).ShouldBe(0.9, 0.0001);
    }

    [Fact]
    public void Peak_ClampsAboveOneToOne()
    {
        var frame = new float[] { 2.0f, -3.0f };
        LevelMeterModel.Peak(frame).ShouldBe(1.0, 0.0001);
    }

    [Fact]
    public void Peak_EmptyFrameIsZero()
    {
        LevelMeterModel.Peak(System.Array.Empty<float>()).ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void Push_RisesTowardPeakUsingAttackCoefficient()
    {
        var m = new LevelMeterModel(attack: 0.5, decay: 0.15);
        // from 0, peak 1.0, attack 0.5 -> 0 + (1-0)*0.5 = 0.5
        m.Push(new float[] { 1.0f }).ShouldBe(0.5, 0.0001);
        // from 0.5, peak 1.0 -> 0.5 + (1-0.5)*0.5 = 0.75
        m.Push(new float[] { 1.0f }).ShouldBe(0.75, 0.0001);
    }

    [Fact]
    public void Push_FallsSlowlyUsingDecayCoefficient()
    {
        var m = new LevelMeterModel(attack: 1.0, decay: 0.15);
        m.Push(new float[] { 1.0f }).ShouldBe(1.0, 0.0001); // attack 1.0 -> jumps to peak
        // silent frame, peak 0, decay 0.15 -> 1.0 + (0-1)*0.15 = 0.85
        m.Push(new float[] { 0.0f }).ShouldBe(0.85, 0.0001);
    }

    [Fact]
    public void Push_StaysWithinZeroToOne()
    {
        var m = new LevelMeterModel();
        for (var i = 0; i < 50; i++)
        {
            var lvl = m.Push(new float[] { 5.0f, -5.0f });
            lvl.ShouldBeInRange(0.0, 1.0);
        }
    }

    [Fact]
    public void Reset_ReturnsLevelToZero()
    {
        var m = new LevelMeterModel(attack: 1.0);
        m.Push(new float[] { 1.0f });
        m.Level.ShouldBe(1.0, 0.0001);
        m.Reset();
        m.Level.ShouldBe(0.0, 0.0001);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug
```
Expected: build FAILS with `CS0246`/`The type or namespace name 'LevelMeterModel'
... could not be found`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Winpepper.Core/Audio/LevelMeterModel.cs`:

```csharp
namespace Winpepper.Core.Audio;

/// <summary>
/// Pure-managed voice-level meter: converts raw mono float frames into a
/// smoothed 0..1 loudness value suitable for driving the status pill's voice
/// meter. Fast attack (jumps up quickly on speech) and slower decay (falls
/// gently so the meter pulses naturally). No timers, no UI — fully testable.
/// </summary>
public sealed class LevelMeterModel
{
    private readonly double _attack;
    private readonly double _decay;
    private double _level;

    public LevelMeterModel(double attack = 0.5, double decay = 0.15)
    {
        _attack = Clamp01(attack);
        _decay = Clamp01(decay);
    }

    /// <summary>Current smoothed level, 0..1.</summary>
    public double Level => _level;

    /// <summary>Absolute-peak of a frame, clamped to 0..1.</summary>
    public static double Peak(ReadOnlySpan<float> frame)
    {
        double peak = 0;
        for (var i = 0; i < frame.Length; i++)
        {
            var v = Math.Abs((double)frame[i]);
            if (v > peak) peak = v;
        }
        return peak > 1.0 ? 1.0 : peak;
    }

    /// <summary>
    /// Push one frame; returns the new smoothed level (0..1). Rising peaks use
    /// the attack coefficient, falling peaks use the (slower) decay coefficient.
    /// </summary>
    public double Push(ReadOnlySpan<float> frame)
    {
        var target = Peak(frame);
        var coeff = target > _level ? _attack : _decay;
        _level += (target - _level) * coeff;
        if (_level < 0) _level = 0;
        if (_level > 1) _level = 1;
        return _level;
    }

    /// <summary>Snap the level back to zero (e.g. when recording stops).</summary>
    public void Reset() => _level = 0;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug \
  && dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0; the 7 new `LevelMeterModelTests` pass, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Audio/LevelMeterModel.cs tests/Winpepper.Core.Tests/Audio/LevelMeterModelTests.cs
git commit -m "feat: add LevelMeterModel for pill voice-level smoothing"
```

---

## Task 3: `PillAnimationMode` enum + `PillAnimationMap` (TDD)

Pure mapping deciding how the pill animates for each stage: Recording → live
voice meter; the post-release working stages (Transcribing/CleaningUp/Injecting)
→ "thinking" pulse; Idle/Error → no animation.

**Files:**
- Create: `src/Winpepper.Core/ViewModels/PillAnimationMode.cs`
- Create: `src/Winpepper.Core/ViewModels/PillAnimationMap.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Core.ViewModels.SessionStage` (existing enum).
- Produces:
  - `public enum PillAnimationMode { None, VoiceLevel, Thinking }`
  - `public static class PillAnimationMap` with
    `static PillAnimationMode ForStage(SessionStage stage)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class PillAnimationMapTests
{
    [Theory]
    [InlineData(SessionStage.Recording,    PillAnimationMode.VoiceLevel)]
    [InlineData(SessionStage.Transcribing, PillAnimationMode.Thinking)]
    [InlineData(SessionStage.CleaningUp,   PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Injecting,    PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Idle,         PillAnimationMode.None)]
    [InlineData(SessionStage.Error,        PillAnimationMode.None)]
    public void ForStage_MapsEachStage(SessionStage stage, PillAnimationMode expected)
    {
        PillAnimationMap.ForStage(stage).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug
```
Expected: build FAILS with `CS0246` for `PillAnimationMode` / `PillAnimationMap`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Winpepper.Core/ViewModels/PillAnimationMode.cs`:

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>How the status pill should animate for the current session stage.</summary>
public enum PillAnimationMode
{
    /// <summary>No animation (static). Idle when hidden; Error stays steady.</summary>
    None,
    /// <summary>Live voice meter driven by InputLevel while recording.</summary>
    VoiceLevel,
    /// <summary>Gentle indeterminate pulse while the app works after release.</summary>
    Thinking,
}
```

Create `src/Winpepper.Core/ViewModels/PillAnimationMap.cs`:

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure mapping from the session stage to how the status pill animates.
/// Recording → live voice meter; the post-release working stages
/// (Transcribing/CleaningUp/Injecting) → a gentle "thinking" pulse so the user
/// can tell the app is still working; Idle/Error → no animation (Error keeps
/// its steady colour).
/// </summary>
public static class PillAnimationMap
{
    public static PillAnimationMode ForStage(SessionStage stage) => stage switch
    {
        SessionStage.Recording    => PillAnimationMode.VoiceLevel,
        SessionStage.Transcribing => PillAnimationMode.Thinking,
        SessionStage.CleaningUp   => PillAnimationMode.Thinking,
        SessionStage.Injecting    => PillAnimationMode.Thinking,
        _                         => PillAnimationMode.None, // Idle, Error
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug \
  && dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0; the 6 `PillAnimationMapTests` cases pass, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/PillAnimationMode.cs \
        src/Winpepper.Core/ViewModels/PillAnimationMap.cs \
        tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs
git commit -m "feat: add PillAnimationMode + stage->mode mapper"
```

---

## Task 4: `SessionViewModel.InputLevel` plumbing (TDD)

Add an `InputLevel` (0..1) property fed by `ReportAudioFrame`, smoothed through
`LevelMeterModel`. Frames received while not recording are ignored, and leaving
the Recording stage resets the level to zero. All updates go through the
existing `IUiThread`.

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelInputLevelTests.cs`

**Interfaces:**
- Consumes: `LevelMeterModel` (Task 2); `SessionStage` (existing);
  `SessionEngine`, `SessionEvent`, `IUiThread`, `SynchronousUiThread` (existing).
- Produces on `SessionViewModel`:
  - `double InputLevel { get; }` (raises `PropertyChanged("InputLevel")`)
  - `void ReportAudioFrame(ReadOnlyMemory<float> frame)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelInputLevelTests.cs`:

```csharp
using System.ComponentModel;
using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelInputLevelTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    [Fact]
    public void InputLevel_StartsAtZero()
    {
        var (vm, _) = NewVm();
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void ReportAudioFrame_WhileRecording_RaisesInputLevel()
    {
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested); // -> Recording
        vm.Stage.ShouldBe(SessionStage.Recording);

        var raised = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionViewModel.InputLevel)) raised = true; };

        vm.ReportAudioFrame(new float[] { 0.8f });

        raised.ShouldBeTrue();
        vm.InputLevel.ShouldBeGreaterThan(0.0);
    }

    [Fact]
    public void ReportAudioFrame_WhenNotRecording_IsIgnored()
    {
        var (vm, _) = NewVm(); // stays Idle
        vm.ReportAudioFrame(new float[] { 0.9f });
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }

    [Fact]
    public void LeavingRecordingStage_ResetsInputLevelToZero()
    {
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested); // Recording
        vm.ReportAudioFrame(new float[] { 0.9f });
        vm.InputLevel.ShouldBeGreaterThan(0.0);

        engine.Apply(SessionEvent.StopRequested); // -> Transcribing
        vm.Stage.ShouldBe(SessionStage.Transcribing);
        vm.InputLevel.ShouldBe(0.0, 0.0001);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug
```
Expected: build FAILS — `SessionViewModel` has no `InputLevel` /
`ReportAudioFrame` (`CS1061`).

- [ ] **Step 3: Add the field, property, reporter, and reset**

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`:

(a) Add fields next to the existing private fields (after `_busSub` at line 19):

```csharp
    private readonly Winpepper.Core.Audio.LevelMeterModel _levelMeter = new();
    private double _inputLevel;
```

(b) Replace the existing `Stage` property (currently lines 30-34) with one that
resets the meter when leaving Recording:

```csharp
    public SessionStage Stage
    {
        get => _stage;
        private set
        {
            if (_stage == value) return;
            _stage = value;
            if (value != SessionStage.Recording)
            {
                _levelMeter.Reset();
                InputLevel = 0;
            }
            Raise(nameof(Stage));
            Raise(nameof(StatusText));
        }
    }
```

(c) Add the `InputLevel` property immediately after the `ElapsedMs` property
(after line 46):

```csharp
    /// <summary>
    /// Smoothed microphone level (0..1) while recording, for the pill's voice
    /// meter. Zero when not recording. Fed via <see cref="ReportAudioFrame"/>.
    /// </summary>
    public double InputLevel
    {
        get => _inputLevel;
        private set
        {
            if (Math.Abs(_inputLevel - value) < 0.0001) return;
            _inputLevel = value;
            Raise(nameof(InputLevel));
        }
    }
```

(d) Add the reporter method after `Tick()` (after line 91):

```csharp
    /// <summary>
    /// Feed a raw mono float frame from the live dictation recorder. Updates
    /// the smoothed <see cref="InputLevel"/> on the UI thread. Frames received
    /// while not recording are ignored so the meter reads zero between sessions.
    /// The live recorder already emits at ~20 Hz (50 ms buffers), which is
    /// within the target throttle — no extra rate limiting is needed here.
    /// </summary>
    public void ReportAudioFrame(ReadOnlyMemory<float> frame) => _ui.Post(() =>
    {
        if (_stage != SessionStage.Recording) return;
        InputLevel = _levelMeter.Push(frame.Span);
    });
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug \
  && dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0; the 4 new `SessionViewModelInputLevelTests` pass,
`Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelInputLevelTests.cs
git commit -m "feat: plumb InputLevel + ReportAudioFrame through SessionViewModel"
```

---

## Task 5: Pill XAML — add a `ScaleTransform` on the Dot

Give the Dot ellipse a named, center-origin `ScaleTransform` the code-behind
can drive from `InputLevel`. Windows-only rendering (verified in Task 9).

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml`

**Interfaces:**
- Consumes: nothing.
- Produces: a `ScaleTransform x:Name="DotScale"` on the `Dot` ellipse that
  Task 6 sets `ScaleX`/`ScaleY` on.

- [ ] **Step 1: Add the transform to the Dot ellipse**

Replace the `Dot` ellipse line (currently line 9) in
`src/Winpepper.App/Views/StatusPillWindow.xaml` with:

```xml
                <Ellipse x:Name="Dot" Width="10" Height="10" Fill="#FFEF4444"
                         RenderTransformOrigin="0.5,0.5">
                    <Ellipse.RenderTransform>
                        <ScaleTransform x:Name="DotScale" ScaleX="1.0" ScaleY="1.0" />
                    </Ellipse.RenderTransform>
                </Ellipse>
```

- [ ] **Step 2: Sanity — Core suite still green (no Core change)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, `Failed: 0`. (XAML compiles only on Windows; rendering
verified in Task 9.)

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml
git commit -m "feat: add ScaleTransform to status pill dot for voice meter"
```

---

## Task 6: Pill code-behind — strict topmost + voice meter + thinking pulse

Wire the three visible behaviours into `StatusPillWindow.xaml.cs`, all driven
by the already-tested Core logic:
1. Re-assert `HWND_TOPMOST` on every show and on each 100 ms tick while visible.
2. While `Stage==Recording`, scale the Dot from `InputLevel`.
3. During the post-release working stages, run a gentle opacity pulse off the
   existing tick; stop it (opacity 1.0) on Idle/Error.

This layer is thin: it calls `ExtendedWindowStyle.AssertTopmost`,
`PillAnimationMap.ForStage`, and reads `_vm.InputLevel`. Windows-only; verified
in Task 9.

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`

**Interfaces:**
- Consumes: `ExtendedWindowStyle.AssertTopmost` (Task 1);
  `PillAnimationMap.ForStage` / `PillAnimationMode` (Task 3);
  `SessionViewModel.InputLevel` + `PropertyChanged` (Task 4); the existing
  `_tickTimer` (100 ms) and `_hwnd`.
- Produces: no new public surface (view behaviour only).

- [ ] **Step 1: Track visibility + animation phase; subscribe to `InputLevel`**

In `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`, add fields after
`private IntPtr _hwnd;` (currently line 20):

```csharp
    private bool _visible;
    private double _pulsePhase;
    private PillAnimationMode _animMode = PillAnimationMode.None;
```

Add the Core namespace to the usings (the file already has
`using Winpepper.Core.ViewModels;` at line 10, which covers `PillAnimationMode`
and `PillAnimationMap` — no new using needed).

- [ ] **Step 2: Drive the meter + pulse + topmost re-assert from the tick**

Replace the `_tickTimer` creation (currently lines 49-50) with:

```csharp
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) =>
        {
            _vm.Tick();
            ElapsedText.Text = $"{_vm.ElapsedMs} ms";

            // Cheap: keep us pinned to the top even if another topmost window
            // was created after our last show. Only while visible.
            if (_visible) ExtendedWindowStyle.AssertTopmost(_hwnd);

            ApplyAnimationFrame();
        };
```

Add the `ApplyAnimationFrame` helper method just before `PositionBottomCenter`
(before line 93):

```csharp
    /// <summary>
    /// Per-tick (100 ms) visual update. VoiceLevel scales the dot from the
    /// smoothed input level; Thinking oscillates the dot opacity ~0.4..1.0 on a
    /// ~1 s loop; None leaves the dot static (scale 1, opacity 1).
    /// </summary>
    private void ApplyAnimationFrame()
    {
        switch (_animMode)
        {
            case PillAnimationMode.VoiceLevel:
                var scale = 1.0 + (_vm.InputLevel * 0.8); // 1.0 .. 1.8
                DotScale.ScaleX = scale;
                DotScale.ScaleY = scale;
                Dot.Opacity = 1.0;
                break;

            case PillAnimationMode.Thinking:
                // 100 ms tick, 10 ticks per ~1 s cycle.
                _pulsePhase += 2 * Math.PI / 10.0;
                var osc = (Math.Sin(_pulsePhase) + 1.0) / 2.0; // 0..1
                Dot.Opacity = 0.4 + (0.6 * osc);               // 0.4 .. 1.0
                DotScale.ScaleX = 1.0;
                DotScale.ScaleY = 1.0;
                break;

            default: // None
                Dot.Opacity = 1.0;
                DotScale.ScaleX = 1.0;
                DotScale.ScaleY = 1.0;
                break;
        }
    }
```

- [ ] **Step 3: Set the animation mode + assert topmost on show; reset on hide**

Rewrite `OnVmChanged` (currently lines 56-91) so every show path sets
`_animMode`, marks `_visible`, and re-asserts topmost; Idle stops everything;
Error stays steady:

```csharp
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))) return;

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        StatusText.Text = _vm.StatusText;
        _animMode = PillAnimationMap.ForStage(_vm.Stage);

        if (_vm.Stage == SessionStage.Idle)
        {
            _tickTimer.Stop();
            _visible = false;
            ResetPillVisual();
            _hideTimer.Stop(); _hideTimer.Start();
        }
        else if (_vm.Stage == SessionStage.Error)
        {
            _tickTimer.Stop();
            _visible = true;
            ResetPillVisual(); // steady dot; Error keeps its Goldenrod colour below
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            _hideTimer.Stop();
        }
        else
        {
            Dot.Fill = new SolidColorBrush(_vm.Stage switch
            {
                SessionStage.Recording   => Microsoft.UI.Colors.Red,
                SessionStage.Transcribing => Microsoft.UI.Colors.Orange,
                SessionStage.CleaningUp  => Microsoft.UI.Colors.Orange,
                SessionStage.Injecting   => Microsoft.UI.Colors.LimeGreen,
                _ => Microsoft.UI.Colors.Gray,
            });
            _pulsePhase = 0;
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _visible = true;
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            _tickTimer.Start();
            _hideTimer.Stop();
        }
    }

    private void ResetPillVisual()
    {
        Dot.Opacity = 1.0;
        DotScale.ScaleX = 1.0;
        DotScale.ScaleY = 1.0;
    }
```

- [ ] **Step 4: Clear the visible flag when the hide timer actually hides**

Replace the `_hideTimer.Tick` handler (currently line 47) with one that also
clears `_visible`:

```csharp
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _visible = false; appWindow.Hide(); };
```

- [ ] **Step 5: Sanity — Core suite still green (no Core change)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, `Failed: 0`. (App is Windows-only; visible behaviour
verified in Task 9.)

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "feat: strict topmost re-assert + voice meter + thinking pulse in status pill"
```

---

## Task 7: Plumb the live recorder's frames into `SessionViewModel`

Subscribe the live-dictation `WasapiRecorder.FramesAvailable` to
`_vm.ReportAudioFrame` at both recorder-creation sites in `PipelineHost`, and
detach on stop. Windows-only (verified in Task 9).

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes: `SessionViewModel.ReportAudioFrame` (Task 4);
  `WasapiRecorder.FramesAvailable` (existing);
  `_vm` (the `SessionViewModel` field, line 27).
- Produces: no new public surface.

- [ ] **Step 1: Add a small helper that wires a recorder to the VM meter**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, add a private field next to
`_recorder` (after line 29 `private IAudioRecorder? _recorder;`):

```csharp
    private Action<ReadOnlyMemory<float>>? _meterHandler;
```

Add a helper method just above `Dispose()` (before line 488):

```csharp
    private void AttachMeter(IAudioRecorder recorder)
    {
        _meterHandler = frame => _vm.ReportAudioFrame(frame);
        recorder.FramesAvailable += _meterHandler;
    }

    private void DetachMeter(IAudioRecorder recorder)
    {
        if (_meterHandler is not null)
        {
            recorder.FramesAvailable -= _meterHandler;
            _meterHandler = null;
        }
    }
```

> `IAudioRecorder` already declares `event Action<ReadOnlyMemory<float>>? FramesAvailable`
> (see `WasapiRecorder.cs:11`); if the interface does not surface the event,
> subscribe on the concrete `WasapiRecorder` instead — both creation sites use
> `new WasapiRecorder()`.

- [ ] **Step 2: Attach on both recorder starts**

In the `HoldDown` case, after `_recorder.Start();` (currently line 177) add:

```csharp
                AttachMeter(_recorder);
```

In the `Toggle` start branch, after `_recorder.Start();` (currently line 336)
add:

```csharp
                    AttachMeter(_recorder);
```

- [ ] **Step 3: Detach on both recorder stops (before dispose)**

In the `HoldUp` case, replace the stop/dispose pair (currently lines 193-194):

```csharp
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
```
with:
```csharp
                DetachMeter(_recorder!);
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
```

In the `Toggle` stop branch, replace the stop/dispose pair (currently
lines 352-353):

```csharp
                    var samples2 = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
```
with:
```csharp
                    DetachMeter(_recorder!);
                    var samples2 = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
```

Also, in the `Cancel` case (currently lines 325-328), detach before disposing:

```csharp
            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                if (_recorder is not null) DetachMeter(_recorder);
                _recorder?.Dispose(); _recorder = null;
                break;
```

- [ ] **Step 4: Sanity — Core suite still green (no Core change)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, `Failed: 0`. (PipelineHost is Windows-only; live meter
verified end-to-end in Task 9.)

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat: feed live recorder frames into SessionViewModel voice meter"
```

---

## Task 8: Onboarding download error handling (TDD at the VM seam)

Wrap `await _runDownloader()` in the VM's `AdvanceAsync` so a network failure is
caught, surfaced as a friendly `DownloadError`, logged via an optional callback,
and does NOT advance the step or throw — the wizard never crashes and Skip stays
usable. The Download button doubles as Retry (it re-runs the step and clears the
error). The catch stays at the seam where the download task is awaited (the
requirement). The page then renders `DownloadError` inline.

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml`
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/OnboardingDownloadErrorTests.cs`

**Interfaces:**
- Consumes: existing `ISettingsWriter`, `IHotkeyValidator`, `OnboardingStep`.
- Produces on `OnboardingViewModel`:
  - new ctor param `Action<Exception>? onDownloadError = null` (optional; keeps
    existing 3-arg call sites compiling)
  - `string? DownloadError { get; }`
  - `bool HasDownloadError { get; }`
  - behaviour: on download failure, `Step` stays `DownloadModels`,
    `DownloadError` set, no exception propagates; on success `DownloadError`
    stays null and `Step` advances to `TestDictation`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/OnboardingDownloadErrorTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class OnboardingDownloadErrorTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public void Queue(System.Func<AppSettings, AppSettings> mutator) { }
        public System.Threading.Tasks.Task FlushAsync() => System.Threading.Tasks.Task.CompletedTask;
    }

    private sealed class PermissiveValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => null;
        public bool Clash(string a, string b) => false;
    }

    private static OnboardingViewModel AtDownloadStep(System.Func<System.Threading.Tasks.Task> downloader,
                                                      System.Action<System.Exception>? onErr = null)
    {
        var vm = new OnboardingViewModel(new FakeWriter(), downloader, new PermissiveValidator(), onErr);
        // Jump straight to the download step: models unresolved, mic+hotkeys resolved.
        vm.InitializeFrom(new AppSettings { MicDeviceId = "mic-1" }, persistedMicPresent: true, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        return vm;
    }

    [Fact]
    public async System.Threading.Tasks.Task Advance_DownloadFailure_DoesNotThrow_StaysOnStep_ShowsError()
    {
        var called = false;
        var vm = AtDownloadStep(
            downloader: () => throw new System.Net.Http.HttpRequestException("network down"),
            onErr: _ => called = true);

        await vm.AdvanceAsync(); // must NOT throw

        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
        vm.HasDownloadError.ShouldBeTrue();
        vm.DownloadError.ShouldNotBeNullOrWhiteSpace();
        vm.CanSkip.ShouldBeTrue();   // Skip stays usable
        called.ShouldBeTrue();        // failure was logged
    }

    [Fact]
    public async System.Threading.Tasks.Task Advance_DownloadSuccess_AdvancesAndNoError()
    {
        var vm = AtDownloadStep(downloader: () => System.Threading.Tasks.Task.CompletedTask);

        await vm.AdvanceAsync();

        vm.Step.ShouldBe(OnboardingStep.TestDictation);
        vm.HasDownloadError.ShouldBeFalse();
        vm.DownloadError.ShouldBeNull();
    }

    [Fact]
    public async System.Threading.Tasks.Task Retry_AfterFailureThenSuccess_ClearsErrorAndAdvances()
    {
        var attempt = 0;
        var vm = AtDownloadStep(downloader: () =>
        {
            attempt++;
            if (attempt == 1) throw new System.Net.Http.HttpRequestException("first fails");
            return System.Threading.Tasks.Task.CompletedTask;
        });

        await vm.AdvanceAsync(); // fails
        vm.HasDownloadError.ShouldBeTrue();
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);

        await vm.AdvanceAsync(); // retry succeeds
        vm.HasDownloadError.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug
```
Expected: build FAILS — `OnboardingViewModel` has no 4-arg ctor / no
`DownloadError` / `HasDownloadError` (`CS1729` / `CS1061`).

- [ ] **Step 3: Add the error field, property, ctor param, and try/catch**

In `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`:

(a) Add fields after `private bool _testDictationDone;` (line 17):

```csharp
    private readonly Action<Exception>? _onDownloadError;
    private string? _downloadError;
```

(b) Replace the ctor (currently lines 28-33) with the 4-arg version (optional
4th param keeps the existing 3-arg call site in `OnboardingPage` compiling):

```csharp
    public OnboardingViewModel(ISettingsWriter writer, Func<Task> runDownloader, IHotkeyValidator validator,
                               Action<Exception>? onDownloadError = null)
    {
        _writer = writer;
        _runDownloader = runDownloader;
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _onDownloadError = onDownloadError;
    }
```

(c) Add the properties after `CanSkip` (after line 113):

```csharp
    /// <summary>
    /// Friendly, inline error message shown on the Download step when the model
    /// download fails. Null when there is no error. The Download button doubles
    /// as Retry: a fresh AdvanceAsync clears this before trying again.
    /// </summary>
    public string? DownloadError
    {
        get => _downloadError;
        private set
        {
            if (_downloadError == value) return;
            _downloadError = value;
            Raise();
            Raise(nameof(HasDownloadError));
        }
    }

    public bool HasDownloadError => _downloadError is not null;
```

(d) Replace the `DownloadModels` case (currently lines 128-131) with the guarded
version:

```csharp
            case OnboardingStep.DownloadModels:
                DownloadError = null;
                try
                {
                    await _runDownloader();
                }
                catch (Exception ex)
                {
                    // Never let a network/download failure crash the wizard.
                    // Stay on this step so Retry (the Download button) and Skip
                    // remain usable; surface a friendly inline message.
                    _onDownloadError?.Invoke(ex);
                    DownloadError = "Couldn't download the models. Check your connection and try again, or Skip to set them up later.";
                    return;
                }
                Step = OnboardingStep.TestDictation;
                break;
```

- [ ] **Step 4: Run the VM tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Debug \
  && dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0; the 3 new `OnboardingDownloadErrorTests` pass,
`Failed: 0`.

- [ ] **Step 5: Add the inline error UI to the Download panel (Windows XAML)**

In `src/Winpepper.App/Views/OnboardingPage.xaml`, inside `DownloadPanel`, add an
error `TextBlock` after the `DownloadProgress` bar (currently line 98) — inside
the same inner `<StackPanel Spacing="12">`:

```xml
                            <TextBlock x:Name="DownloadErrorText"
                                       AutomationProperties.AutomationId="OnboardingDownloadErrorText"
                                       Visibility="Collapsed"
                                       TextWrapping="Wrap"
                                       Foreground="{ThemeResource SystemFillColorCriticalBrush}"
                                       Style="{ThemeResource BodyTextBlockStyle}" />
```

(The existing `AdvanceButton` — which reads "Download" on this step — is the
Retry affordance; no new button is required. Skip is the existing `SkipButton`.)

- [ ] **Step 6: Render `DownloadError` from the page code-behind**

In `src/Winpepper.App/Views/OnboardingPage.xaml.cs`, in the `RenderStep` method
(after the `Show(...)` panel-visibility calls), add the error rendering:

```csharp
        DownloadErrorText.Text = _vm.DownloadError ?? string.Empty;
        DownloadErrorText.Visibility = _vm.HasDownloadError ? Visibility.Visible : Visibility.Collapsed;
```

(`RenderStep` is already invoked on every `_vm.PropertyChanged`, so the error
appears/clears automatically. `OnAdvance` re-enables `AdvanceButton` in its
`finally`, so the Download/Retry button stays clickable after a failure.)

- [ ] **Step 7: Sanity — Core suite still green**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll
```
Expected: exit code 0, `Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Core/ViewModels/OnboardingViewModel.cs \
        src/Winpepper.App/Views/OnboardingPage.xaml \
        src/Winpepper.App/Views/OnboardingPage.xaml.cs \
        tests/Winpepper.Core.Tests/ViewModels/OnboardingDownloadErrorTests.cs
git commit -m "feat: catch onboarding download failures with inline error + retry"
```

---

## Task 9: Full non-Windows suite + Windows smoke-test checklist

Prove the whole pure-managed suite is green, then record the Windows-only visual
behaviours that cannot run on Linux for the human/Windows smoke pass.

**Files:**
- No source changes. Verification only.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: a green full suite and a documented smoke-test list.

- [ ] **Step 1: Run the FULL non-Windows test suite**

Run every non-Windows test project via the in-process runner (skip Windows-only
projects — Audio/Platform/App are Windows-only and don't build on Linux). Build
and run each project that targets `net9.0` on Linux:

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for proj in Winpepper.Core.Tests Winpepper.Cleanup.Tests Winpepper.Corrections.Tests \
            Winpepper.History.Tests Winpepper.Asr.Tests Winpepper.Models.Tests; do
  dll="tests/$proj/bin/Debug/net9.0/$proj.dll"
  if dotnet build "tests/$proj/$proj.csproj" -c Debug >/dev/null 2>&1 && [ -f "$dll" ]; then
    echo "== $proj =="
    dotnet exec "$dll" || exit 1
  else
    echo "== $proj: skipped (Windows-only or did not build on Linux) =="
  fi
done
```
Expected: every project that builds prints `Failed: 0`; overall exit code 0. At
minimum `Winpepper.Core.Tests` (which holds all new tests from this plan) is
green.

- [ ] **Step 2: Record the Windows smoke-test checklist**

The following require a real Windows run (WinUI z-order, layered-window
rendering, live mic, animation) and CANNOT be validated on Linux. They verify
the thin WinUI layers over the Core logic tested above. Run manually on Windows:

1. **Strict always-on-top (Task 1/6):** start dictation; open another topmost
   tool or a **borderless/windowed-fullscreen** app (e.g. a video player, a
   maximized browser video). The pill remains visible above it, and stays on top
   for the whole session (topmost is re-asserted every 100 ms).
   **Scope / known limitation (verified against Win32 docs):** the documented
   `HWND_TOPMOST` contract only guarantees coverage over *non-topmost* and other
   *topmost* windows — it does **not** cover **exclusive-fullscreen (FSE)**
   surfaces (some games / exclusive-mode video), which bypass DWM composition and
   which *no* topmost approach can overlay. FSE coverage is an **explicit
   non-goal** of this task; do not treat the pill being hidden by a true FSE app
   as a failure of Task 1.
2. **Never steals focus (Task 1/6):** while the pill shows, keep typing in the
   foreground app — focus never moves to the pill; clicks pass through
   (click-through intact).
3. **Live voice meter (Task 5/6/7):** while `Recording`, the red dot grows/pulses
   with your voice (louder = bigger, scale up to ~1.8×) and settles when silent.
4. **Thinking pulse (Task 3/6):** release the hotkey; through Transcribing /
   CleaningUp / Injecting the dot gently pulses opacity (~0.4..1.0, ~1 s loop)
   so it never looks frozen.
5. **Error steadiness (Task 6):** force an error; the dot turns Goldenrod and
   holds steady (no pulse), and the pill does not auto-hide on Error.
6. **Auto-hide still works (Task 6):** when the session returns to Idle, the pill
   fades/hides after ~600 ms as before; visual state resets (opacity 1, scale 1).
7. **Onboarding download resilience (Task 8):** during first-run setup, simulate
   a network failure on the Download step (e.g. disconnect). The wizard shows the
   inline "Couldn't download the models…" message, does NOT crash, the
   Download button retries, and Skip still completes setup.

- [ ] **Step 3: Commit (checklist marker)**

If the checklist above is not already captured in this plan file at execution
time, ensure it is committed. Otherwise no commit is needed for this task.

```bash
git add docs/plans/2026-07-20-status-pill-overlay.md
git commit -m "docs: record Windows smoke-test checklist for status pill overlay" || echo "nothing to commit"
```

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Covered by | Verified by |
|---|---|---|
| Task 1: set `WS_EX_TOPMOST` + `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` | Task 1 (`ExtendedWindowStyle`) | Task 9 smoke #1 |
| Task 1: re-assert on EVERY show | Task 6 Step 3 (`AssertTopmost` in both show paths) | Task 9 smoke #1 |
| Task 1: re-assert periodically while visible via 100 ms `_tickTimer`, guarded to visible-only | Task 6 Step 2 (`if (_visible) AssertTopmost`) | Task 9 smoke #1 |
| Task 1: keep NOACTIVATE / click-through; never steal focus | Task 1 (styles preserved; `SWP_NOACTIVATE`) | Task 9 smoke #2 |
| Task 2: mic amplitude plumbed from live recorder (`PipelineHost`) through `SessionViewModel` to pill | Task 7 (attach `FramesAvailable`→`ReportAudioFrame`) + Task 4 (`InputLevel`) | Tasks 4 tests + Task 9 smoke #3 |
| Task 2: `double InputLevel` (0..1) with change notification | Task 4 | `SessionViewModelInputLevelTests` |
| Task 2: throttled ~20-30 Hz on UI side | Task 4 (documented: recorder emits ~20 Hz @50 ms; no extra throttle needed) | reasoning in code comment |
| Task 2: render voice meter via Dot scale while Recording | Task 5 (ScaleTransform) + Task 6 (VoiceLevel scale 1.0..1.8) | Task 9 smoke #3 |
| Task 2: peak/smoothing/decay/throttle in pure testable Core class | Task 2 (`LevelMeterModel`) | `LevelMeterModelTests` |
| Task 3: continuous gentle pulse during Transcribing/CleaningUp/(Injecting) | Task 6 (Thinking, opacity 0.4..1.0 ~1 s) via mapper | Task 9 smoke #4 |
| Task 3: stop on Idle/hide; Error steady Goldenrod | Task 6 (Idle → None+reset; Error → steady) | Task 9 smoke #5/#6 |
| Task 3: stage→mode logic in pure testable mapper (`enum PillAnimationMode`) | Task 3 (`PillAnimationMap`) | `PillAnimationMapTests` |
| Task 4: wrap download await; catch at the awaited seam; friendly inline error + Retry; never crash; Skip/Back usable | Task 8 (VM try/catch at `await _runDownloader()` + page UI) | `OnboardingDownloadErrorTests` + Task 9 smoke #7 |
| Verification: pure-managed tests via `dotnet exec`; .NET provisioning; run full non-Windows suite | Task 0 + Task 9 | Task 9 Step 1 |
| Verification: WinUI layers thin + explicit Windows smoke list | Tasks 1/5/6/7 thin; Task 9 Step 2 | Task 9 checklist |
| Constraint: don't touch keyboard hook / packaging | No task modifies them | n/a |

**1b. No silent deferrals of required behavior.** Every user-facing behavior has
a production task and a proof:
- Voice meter, thinking pulse, InputLevel plumbing, download error path — all
  implemented with real production code (no stubs/mocks in production paths).
  `LevelMeterModel`, `PillAnimationMap`, `SessionViewModel.InputLevel`, and the
  onboarding error path are proven by unit tests (Tasks 2, 3, 4, 8). The live
  frame plumbing (Task 7) uses the real `WasapiRecorder`.
- The **native z-order, WinUI rendering, and animation** (Tasks 1, 5, 6, 7's
  visible effect) are inherently platform-specific and physically cannot execute
  on Linux. They are NOT deferred or stubbed — they are real production code
  whose only possible verification is a real Windows run, enumerated explicitly
  in the Task 9 smoke checklist (#1–#6). This is the correct verification
  gradient for native window management, not a scope reduction. No requirement is
  moved to "future work".
- **"Back" usability (Task 4):** the current onboarding page exposes Skip +
  Advance only (no Back button in `OnboardingPage.xaml`). The fix keeps the user
  ON the Download step on failure (no state loss), so Skip remains usable and the
  Download button retries — satisfying "the wizard must never crash and Skip
  remains usable." No Back regression is introduced because no Back control is
  removed or altered.

**2. Placeholder scan.** No "TBD"/"handle edge cases"/"similar to Task N"
placeholders remain; every code step shows complete code and every test step
shows the full test and expected output.

**3. Type consistency.** Names are consistent across tasks:
`ExtendedWindowStyle.AssertTopmost(IntPtr)` (Task 1 → used in Task 6);
`LevelMeterModel.Push/Peak/Reset/Level` (Task 2 → used in Task 4);
`PillAnimationMode` / `PillAnimationMap.ForStage(SessionStage)` (Task 3 → used
in Task 6); `SessionViewModel.InputLevel` / `ReportAudioFrame(ReadOnlyMemory<float>)`
(Task 4 → used in Tasks 6, 7); `DotScale` (Task 5 → used in Task 6);
`OnboardingViewModel.DownloadError` / `HasDownloadError` + 4-arg ctor (Task 8 →
used by page + tests). No mismatches.
