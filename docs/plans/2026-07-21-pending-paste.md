# Pending Paste Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Never inject dictated text into the wrong field. If keyboard focus
moved away from the original target while the pipeline was working, hold the
final text as an in-memory "pending paste" instead of injecting it anywhere;
the status pill enters a distinct clickable state and clicking it pastes into
whatever field is focused at click time.

**Architecture:** All decision and state logic is pure-managed C# in
`Winpepper.Core` (a new `Winpepper.Core.Pending` namespace + additions to
`SessionViewModel`/`SessionStage`/`PillAnimationMap`), unit-tested on Linux via
the xUnit v3 in-process runner. The Windows-only glue in `Winpepper.App`
(`#if WINDOWS`) — the focused-target capture, the `WS_EX_TRANSPARENT` toggle,
the pill click handler, and the `PipelineHost` wiring — stays thin, calls the
tested pure logic, and is verified only in the Windows Smoke Test Checklist.

**Tech Stack:** C# / .NET 9 (multi-targeted `net9.0` + `net9.0-windows...`; we
build/test the `net9.0` target on Linux), xUnit v3, Shouldly assertions.

## Global Constraints

- **SDK:** .NET SDK satisfying `global.json` — `sdk 9.0.100`,
  `rollForward: latestFeature`. `dotnet` is **not** on PATH in this worktree;
  Task 0 provisions it locally into `./.dotnet/` (already gitignored — the
  `.gitignore` contains `/.dotnet/`, so nothing is committed for the SDK).
- **Network required** for Task 0's provisioning and the first `dotnet build`
  (cold NuGet cache; restore evaluates the `net9.0-windows10.0.19041.0` TFM of
  multi-targeted projects even when building `-f net9.0`). All packages are on
  nuget.org / dot.net.
- **Test runner:** the VSTest host (`dotnet test`) **crashes on this machine**.
  Pure-managed tests MUST run via the xUnit v3 **in-process runner**:
  `dotnet exec <TestAssembly>.dll`. Exclude Windows-only tests with
  `-notrait "Platform=Windows"`. Target a single test with
  `-method "<Namespace>.<Class>.<Method>"`.
- **Test TFM:** build/run the `net9.0` target only (never `net9.0-windows...`).
  Always pass `-p:EnableWindowsTargeting=true` so multi-targeted project
  references restore on Linux.
- **Every test step re-exports the SDK env** (a fresh implementer shell does
  not inherit Task 0's exports):
  ```bash
  export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
  ```
- **Do NOT commit** the provisioned `./.dotnet/` directory or any build output
  (`bin/`, `obj/` are already gitignored).
- **Out of scope — do NOT touch:** the keyboard hook
  (`src/Winpepper.Platform/Hotkeys/*`) and packaging (`packaging/`). Consume
  existing hotkey events only.
- **The pending slot is memory-only.** It MUST never be written to disk. The
  existing history archiving feature (`Winpepper.History.HistoryArchiver`) is
  separate and unchanged — this plan does not add the pending text to any
  archived `HistoryEntry`, settings file, or log payload.
- **WinUI code is Linux-unbuildable and Linux-untestable.** Every file under
  `src/Winpepper.App` is wrapped in `#if WINDOWS` and is NOT compiled on Linux
  (`Directory.Build.props` skips `Winpepper.App`'s project references on
  non-Windows unless forced). Changes there are kept thin, call the tested
  pure-managed logic, and are verified in the **Windows Smoke Test Checklist**
  at the end — they are NOT deferred or stubbed.
- **Docs:** `README.md` is the only end-user markdown doc; this plan under
  `docs/plans/` is a working/agent doc and is fine. Do not add other end-user
  docs.
- **Commits:** focused and atomic; use `feat:`/`fix:`/`test:`/`refactor:`
  prefixes and the standard Amplifier co-author trailer (shown in each commit
  step).

---

## Scope Check

This is a single cohesive feature (one user story: "don't paste into the wrong
field; let me place a moved-away paste myself"). It is delivered as one plan.
The pure decision/state logic (Tasks 1–5) is fully unit-tested on Linux; the
platform-bound WinUI/Win32 integration (Tasks 6–9) cannot execute on Linux and
is verified as a whole in the **Windows Smoke Test Checklist**. There is no
Linux end-to-end test possible because the integration surface is WinUI (the
pill window, the UIA focus capture, and `SendInput`).

---

## File Structure

**Task 0 — provision the .NET SDK**
- None committed (SDK lands in gitignored `./.dotnet/`).

**Task 1 — injection-target identity (pure)**
- Create: `src/Winpepper.Core/Pending/InjectionTarget.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/InjectionTargetTests.cs`

**Task 2 — inject-vs-hold decision (pure)**
- Create: `src/Winpepper.Core/Pending/PendingPasteDecider.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/PendingPasteDeciderTests.cs`

**Task 3 — pending-paste state machine (pure)**
- Create: `src/Winpepper.Core/Pending/PendingPasteState.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs`

**Task 4 — PendingPaste session stage + animation mapping (pure)**
- Modify: `src/Winpepper.Core/ViewModels/SessionStage.cs`
- Modify: `src/Winpepper.Core/ViewModels/PillAnimationMap.cs`
- Test:   `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs`

**Task 5 — SessionViewModel pending integration (pure)**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Test:   `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`

**Task 6 — runtime click-through toggle (Windows glue)**
- Modify: `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`

**Task 7 — pill PENDING visual + click handler (Windows glue)**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml`
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`

**Task 8 — pipeline capture + decision + pending wiring (Windows glue)**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Task 9 — AppShell wiring: pill click → paste-pending (Windows glue)**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs`

---

### Task 0: Provision the .NET SDK locally

**Files:** none committed (SDK lands in gitignored `./.dotnet/`).

**Interfaces:**
- Produces: a working `dotnet` at `./.dotnet/dotnet`, reached via
  `DOTNET_ROOT`/`PATH`. Every later task's test steps begin by re-exporting
  these two variables (they are not inherited across fresh implementer shells).

- [ ] **Step 1: Provision the SDK**

Run from the worktree root:

```bash
if [ ! -x "./.dotnet/dotnet" ]; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
fi
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a `latestFeature` roll-forward such as
`9.0.1xx`). If `dot.net` is unreachable, the network is down — stop and report;
do not proceed.

- [ ] **Step 2: Confirm the pure-managed test project already builds**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: `Build succeeded`. This warms the NuGet cache and proves the toolchain
before any feature code is written. No commit (nothing tracked changed).

---

### Task 1: Injection-target identity (pure)

A pure, platform-agnostic value type identifying the field we intend to inject
into. The Windows layer will build it from a UIA focused-element snapshot
(foreground window handle + UIA RuntimeId string); tests build it directly.

**Files:**
- Create: `src/Winpepper.Core/Pending/InjectionTarget.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/InjectionTargetTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `namespace Winpepper.Core.Pending;`
  - `sealed record InjectionTarget { long WindowHandle; string ElementId; }`
    (both `required` init-only), `bool IsValid`,
    `bool Matches(InjectionTarget other)`, `static InjectionTarget Empty`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Pending/InjectionTargetTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class InjectionTargetTests
{
    private static InjectionTarget Make(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void Empty_IsNotValid()
    {
        InjectionTarget.Empty.IsValid.ShouldBeFalse();
        InjectionTarget.Empty.WindowHandle.ShouldBe(0L);
        InjectionTarget.Empty.ElementId.ShouldBe("");
    }

    [Fact]
    public void IsValid_TrueWhenElementIdPresent()
    {
        Make(42, "7.3.11").IsValid.ShouldBeTrue();
        Make(42, "").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Matches_TrueForSameWindowAndElement()
    {
        Make(42, "7.3.11").Matches(Make(42, "7.3.11")).ShouldBeTrue();
    }

    [Fact]
    public void Matches_FalseWhenElementDiffers()
    {
        Make(42, "7.3.11").Matches(Make(42, "9.9.9")).ShouldBeFalse();
    }

    [Fact]
    public void Matches_FalseWhenWindowDiffers()
    {
        Make(42, "7.3.11").Matches(Make(99, "7.3.11")).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: build FAILS with `CS0246: The type or namespace name 'InjectionTarget'
could not be found` (and the `Winpepper.Core.Pending` namespace missing).

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Pending/InjectionTarget.cs`:

```csharp
namespace Winpepper.Core.Pending;

/// <summary>
/// Pure, platform-agnostic identity of the field we intend to inject dictated
/// text into. Captured when dictation STARTS and re-captured at injection time
/// so the pipeline can tell whether focus is still on the same target. The
/// Windows layer builds this from a UIA focused-element snapshot (foreground
/// window handle + UIA RuntimeId joined with '.'); unit tests build it directly.
/// </summary>
public sealed record InjectionTarget
{
    /// <summary>Foreground window handle as a 64-bit value (IntPtr.ToInt64()). 0 when unknown.</summary>
    public required long WindowHandle { get; init; }

    /// <summary>Opaque focused-element identity (UIA RuntimeId joined with '.'). Empty when unknown.</summary>
    public required string ElementId { get; init; }

    /// <summary>True when we captured a usable element identity.</summary>
    public bool IsValid => !string.IsNullOrEmpty(ElementId);

    /// <summary>True when both targets refer to the same window AND the same focused element.</summary>
    public bool Matches(InjectionTarget other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return WindowHandle == other.WindowHandle
            && string.Equals(ElementId, other.ElementId, StringComparison.Ordinal);
    }

    public static InjectionTarget Empty { get; } = new() { WindowHandle = 0, ElementId = string.Empty };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" -method "Winpepper.Core.Tests.Pending.InjectionTargetTests.*"
```
Expected: `Failed: 0`. All five `InjectionTargetTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Pending/InjectionTarget.cs \
        tests/Winpepper.Core.Tests/Pending/InjectionTargetTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add InjectionTarget identity for pending-paste

Pure value type identifying the focused field (foreground window handle +
UIA element id) so the pipeline can compare the target at dictation start
against the target at injection time.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Inject-vs-hold decision (pure)

The pure rule that decides, given the target at start and the target at inject
time, whether to inject now (zero behavior change) or hold as pending.

**Files:**
- Create: `src/Winpepper.Core/Pending/PendingPasteDecider.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/PendingPasteDeciderTests.cs`

**Interfaces:**
- Consumes: `InjectionTarget` (Task 1).
- Produces:
  - `enum InjectionDecision { InjectNow, HoldPending }`
  - `static class PendingPasteDecider` with
    `static InjectionDecision Decide(InjectionTarget atStart, InjectionTarget atInject)`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Pending/PendingPasteDeciderTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class PendingPasteDeciderTests
{
    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void SameTarget_InjectsNow()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(1, "a.b"))
            .ShouldBe(InjectionDecision.InjectNow);
    }

    [Fact]
    public void DifferentTarget_HoldsPending()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(2, "c.d"))
            .ShouldBe(InjectionDecision.HoldPending);
    }

    [Fact]
    public void SameWindowDifferentElement_HoldsPending()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), T(1, "z.z"))
            .ShouldBe(InjectionDecision.HoldPending);
    }

    [Fact]
    public void UnknownStartTarget_InjectsNow()
    {
        // Could not capture identity at start -> preserve today's behavior.
        PendingPasteDecider.Decide(InjectionTarget.Empty, T(2, "c.d"))
            .ShouldBe(InjectionDecision.InjectNow);
    }

    [Fact]
    public void UnknownInjectTarget_InjectsNow()
    {
        PendingPasteDecider.Decide(T(1, "a.b"), InjectionTarget.Empty)
            .ShouldBe(InjectionDecision.InjectNow);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: build FAILS with `CS0103`/`CS0246` for `PendingPasteDecider` and
`InjectionDecision`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Pending/PendingPasteDecider.cs`:

```csharp
namespace Winpepper.Core.Pending;

/// <summary>Outcome of the inject-vs-hold decision.</summary>
public enum InjectionDecision
{
    /// <summary>Inject now, exactly as today (same target, or identity unknown).</summary>
    InjectNow,
    /// <summary>Focus moved to a different KNOWN target: hold the text as a pending paste.</summary>
    HoldPending,
}

/// <summary>
/// Pure decision: at injection time, is the same field still focused?
/// HoldPending is chosen ONLY when we positively know the target changed (both
/// snapshots valid and different). If either snapshot is unknown/invalid we
/// default to InjectNow so the common path keeps today's zero-behavior-change
/// semantics (we never regress into holding when we simply failed to capture).
/// </summary>
public static class PendingPasteDecider
{
    public static InjectionDecision Decide(InjectionTarget atStart, InjectionTarget atInject)
    {
        ArgumentNullException.ThrowIfNull(atStart);
        ArgumentNullException.ThrowIfNull(atInject);
        if (!atStart.IsValid || !atInject.IsValid) return InjectionDecision.InjectNow;
        return atStart.Matches(atInject)
            ? InjectionDecision.InjectNow
            : InjectionDecision.HoldPending;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" -method "Winpepper.Core.Tests.Pending.PendingPasteDeciderTests.*"
```
Expected: `Failed: 0`. All five decision tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Pending/PendingPasteDecider.cs \
        tests/Winpepper.Core.Tests/Pending/PendingPasteDeciderTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add PendingPasteDecider inject-vs-hold rule

Holds text as pending only when both start/inject targets are known and
differ; otherwise injects now to preserve today's zero-behavior-change path.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: Pending-paste state machine (pure)

The in-memory-only slot holding the deferred text. Never persisted. Lifecycle:
`None -> Pending(text,target) -> consumed (successful paste) | discarded (next
hotkey / cancel / app exit)`.

**Files:**
- Create: `src/Winpepper.Core/Pending/PendingPasteState.cs`
- Test:   `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs`

**Interfaces:**
- Consumes: `InjectionTarget` (Task 1).
- Produces: `sealed class PendingPasteState` with
  - `bool HasPending { get; }`, `string PendingText { get; }`,
    `InjectionTarget Target { get; }`
  - `void SetPending(string text, InjectionTarget target)`
  - `void Discard()`
  - `bool OnPasteAttempted(bool injected)` (returns true when consumed).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Pending;
using Xunit;

namespace Winpepper.Core.Tests.Pending;

public class PendingPasteStateTests
{
    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void Fresh_HasNoPending()
    {
        var s = new PendingPasteState();
        s.HasPending.ShouldBeFalse();
        s.PendingText.ShouldBe("");
        s.Target.ShouldBe(InjectionTarget.Empty);
    }

    [Fact]
    public void SetPending_HoldsTextAndTarget()
    {
        var s = new PendingPasteState();
        s.SetPending("hello world", T(5, "1.2"));
        s.HasPending.ShouldBeTrue();
        s.PendingText.ShouldBe("hello world");
        s.Target.ShouldBe(T(5, "1.2"));
    }

    [Fact]
    public void SetPending_ReplacesExisting()
    {
        var s = new PendingPasteState();
        s.SetPending("first", T(1, "a"));
        s.SetPending("second", T(2, "b"));
        s.PendingText.ShouldBe("second");
        s.Target.ShouldBe(T(2, "b"));
    }

    [Fact]
    public void Discard_ClearsSlot()
    {
        var s = new PendingPasteState();
        s.SetPending("gone", T(1, "a"));
        s.Discard();
        s.HasPending.ShouldBeFalse();
        s.PendingText.ShouldBe("");
    }

    [Fact]
    public void Discard_IsIdempotent()
    {
        var s = new PendingPasteState();
        Should.NotThrow(() => s.Discard());
        s.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void OnPasteAttempted_Success_ConsumesSlot()
    {
        var s = new PendingPasteState();
        s.SetPending("place me", T(1, "a"));
        var consumed = s.OnPasteAttempted(injected: true);
        consumed.ShouldBeTrue();
        s.HasPending.ShouldBeFalse();
    }

    [Fact]
    public void OnPasteAttempted_Failure_KeepsSlotForRetry()
    {
        var s = new PendingPasteState();
        s.SetPending("keep me", T(1, "a"));
        var consumed = s.OnPasteAttempted(injected: false);
        consumed.ShouldBeFalse();
        s.HasPending.ShouldBeTrue();
        s.PendingText.ShouldBe("keep me");
    }

    [Fact]
    public void OnPasteAttempted_NoPending_ReturnsFalse()
    {
        var s = new PendingPasteState();
        s.OnPasteAttempted(injected: true).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: build FAILS with `CS0246: PendingPasteState could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Pending/PendingPasteState.cs`:

```csharp
namespace Winpepper.Core.Pending;

/// <summary>
/// In-memory ONLY pending-paste slot. Holds the final dictated text when focus
/// moved away from the original target before injection. This slot is NEVER
/// persisted to disk — history archiving is a separate, unchanged feature.
/// Lifecycle: None -> Pending(text,target) -> consumed (successful paste) |
/// discarded (next hotkey / cancel / app exit — app exit is memory-only, so
/// trivially discarded).
/// </summary>
public sealed class PendingPasteState
{
    public bool HasPending { get; private set; }
    public string PendingText { get; private set; } = string.Empty;
    public InjectionTarget Target { get; private set; } = InjectionTarget.Empty;

    /// <summary>Hold text as pending, replacing any existing pending slot.</summary>
    public void SetPending(string text, InjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        PendingText = text ?? string.Empty;
        Target = target;
        HasPending = true;
    }

    /// <summary>Clear the slot (next hotkey, cancel, or app exit). Idempotent.</summary>
    public void Discard()
    {
        HasPending = false;
        PendingText = string.Empty;
        Target = InjectionTarget.Empty;
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

- [ ] **Step 4: Run tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" -method "Winpepper.Core.Tests.Pending.PendingPasteStateTests.*"
```
Expected: `Failed: 0`. All eight state-machine tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Pending/PendingPasteState.cs \
        tests/Winpepper.Core.Tests/Pending/PendingPasteStateTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add in-memory PendingPasteState machine

Holds deferred dictation text in memory only (never persisted). Create/replace/
discard/consume with keep-on-failed-paste semantics for pill-click retry.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: PendingPaste session stage + animation mapping (pure)

Add the new `PendingPaste` stage and map it to a steady (non-animated) pill so
there is no "thinking" pulse while waiting for the user to click.

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionStage.cs`
- Modify: `src/Winpepper.Core/ViewModels/PillAnimationMap.cs`
- Test:   `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs`

**Interfaces:**
- Consumes: `SessionStage`, `PillAnimationMode`, `PillAnimationMap` (existing).
- Produces: `SessionStage.PendingPaste` enum member;
  `PillAnimationMap.ForStage(SessionStage.PendingPaste) == PillAnimationMode.None`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class PillAnimationMapTests
{
    [Theory]
    [InlineData(SessionStage.Recording, PillAnimationMode.VoiceLevel)]
    [InlineData(SessionStage.Transcribing, PillAnimationMode.Thinking)]
    [InlineData(SessionStage.CleaningUp, PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Injecting, PillAnimationMode.Thinking)]
    [InlineData(SessionStage.Idle, PillAnimationMode.None)]
    [InlineData(SessionStage.Error, PillAnimationMode.None)]
    [InlineData(SessionStage.PendingPaste, PillAnimationMode.None)]
    public void ForStage_MapsEveryStage(SessionStage stage, PillAnimationMode expected)
        => PillAnimationMap.ForStage(stage).ShouldBe(expected);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: build FAILS with `CS0117: 'SessionStage' does not contain a definition
for 'PendingPaste'`.

- [ ] **Step 3: Add the enum member**

Edit `src/Winpepper.Core/ViewModels/SessionStage.cs` to read in full:

```csharp
namespace Winpepper.Core.ViewModels;

public enum SessionStage
{
    Idle,
    Recording,
    Transcribing,
    CleaningUp,
    Injecting,
    PendingPaste,
    Error,
}
```

- [ ] **Step 4: Add the explicit mapping arm**

In `src/Winpepper.Core/ViewModels/PillAnimationMap.cs`, add an explicit
`PendingPaste` arm before the `_` default so the intent is tested and clear.
Replace the `ForStage` body:

```csharp
    public static PillAnimationMode ForStage(SessionStage stage) => stage switch
    {
        SessionStage.Recording    => PillAnimationMode.VoiceLevel,
        SessionStage.Transcribing => PillAnimationMode.Thinking,
        SessionStage.CleaningUp   => PillAnimationMode.Thinking,
        SessionStage.Injecting    => PillAnimationMode.Thinking,
        SessionStage.PendingPaste => PillAnimationMode.None, // steady; no pulse while waiting for click
        _                         => PillAnimationMode.None, // Idle, Error
    };
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" -method "Winpepper.Core.Tests.ViewModels.PillAnimationMapTests.*"
```
Expected: `Failed: 0`. All seven mapping rows pass, including `PendingPaste`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionStage.cs \
        src/Winpepper.Core/ViewModels/PillAnimationMap.cs \
        tests/Winpepper.Core.Tests/ViewModels/PillAnimationMapTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add PendingPaste session stage (steady, no pulse)

New SessionStage.PendingPaste maps to PillAnimationMode.None so the pill stays
steady while waiting for the user to click to paste.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: SessionViewModel pending integration (pure)

Wire the pure `PendingPasteState` into `SessionViewModel` so that: entering
pending shows the PENDING pill and holds the text; the Idle auto-hide does not
fire (Stage is `PendingPaste`, not `Idle`); a new dictation (engine → Recording)
discards the pending slot; an ErrorBus report while pending is held keeps the
pill clickable instead of flipping to Error; and a successful/failed pill paste
consumes/keeps the slot.

`SessionViewModel` currently drives `Stage` from two sources: the `SessionEngine`
state (`OnEngineStateChanged`) and the `ErrorBus` (`OnBusReport`, which forces
`Stage = Error`). Both must respect a held pending slot.

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Test:   `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`

**Interfaces:**
- Consumes: `PendingPasteState`, `InjectionTarget` (Tasks 1,3);
  `SessionEngine`/`SessionEvent`/`SessionState` (existing);
  `ErrorBus`/`ErrorStage` (existing); `SynchronousUiThread` (existing, in
  `Winpepper.Core.Threading`, used by tests).
- Produces on `SessionViewModel`:
  - `bool HasPendingPaste { get; }`
  - `string PendingPasteText { get; }`
  - `void EnterPendingPaste(string text, InjectionTarget target)`
  - `bool NotifyPasteAttempted(bool injected)` (UI-thread; returns true when
    consumed, returning the VM to Idle).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`:

```csharp
using System;
using Shouldly;
using Winpepper.Core.Errors;
using Winpepper.Core.Pending;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelPendingTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void EnterPendingPaste_HoldsTextAndShowsPendingStage()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("deferred text", T(1, "a"));
        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("deferred text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void EngineIdle_WhilePending_KeepsPendingStage()
    {
        // Drive the engine to Injecting, enter pending, then complete injection.
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested);   // Recording
        engine.Apply(SessionEvent.StopRequested);    // Transcribing
        engine.Apply(SessionEvent.TranscriptReady);  // Injecting
        vm.EnterPendingPaste("hold me", T(1, "a"));
        engine.Apply(SessionEvent.InjectionCompleted); // -> Idle: must NOT hide

        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.HasPendingPaste.ShouldBeTrue();
    }

    [Fact]
    public void NewDictation_DiscardsPending()
    {
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("stale", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested); // Recording

        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Recording);
    }

    [Fact]
    public void ErrorReport_WhilePending_KeepsPendingClickable()
    {
        var (vm, _) = NewVm();
        var bus = new ErrorBus();
        vm.AttachErrorBus(bus);
        vm.EnterPendingPaste("retry me", T(1, "a"));

        bus.Report(ErrorStage.Injection, new InvalidOperationException("SendInput refused"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.PendingPaste);   // did NOT flip to Error
        vm.StatusText.ShouldBe("Click to paste");
        vm.HasPendingPaste.ShouldBeTrue();
        vm.LastErrorMessage.ShouldBe("SendInput refused"); // still recorded for diagnostics
    }

    [Fact]
    public void ErrorReport_WithoutPending_StillFlipsToError()
    {
        var (vm, _) = NewVm();
        var bus = new ErrorBus();
        vm.AttachErrorBus(bus);

        bus.Report(ErrorStage.Injection, new InvalidOperationException("boom"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
    }

    [Fact]
    public void NotifyPasteAttempted_Success_ClearsPendingAndReturnsIdle()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("place me", T(1, "a"));

        var consumed = vm.NotifyPasteAttempted(injected: true);

        consumed.ShouldBeTrue();
        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Fact]
    public void NotifyPasteAttempted_Failure_KeepsPending()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("keep me", T(1, "a"));

        var consumed = vm.NotifyPasteAttempted(injected: false);

        consumed.ShouldBeFalse();
        vm.HasPendingPaste.ShouldBeTrue();
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
```
Expected: build FAILS with `CS1061`/`CS0246` — `SessionViewModel` has no
`EnterPendingPaste`, `HasPendingPaste`, `PendingPasteText`, or
`NotifyPasteAttempted`.

- [ ] **Step 3: Add the pending field and using**

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`, add the using at the top
(after the existing `using Winpepper.Core.Threading;`):

```csharp
using Winpepper.Core.Pending;
```

Then add a field alongside the other private fields (right after
`private double _inputLevel;`):

```csharp
    private readonly PendingPasteState _pending = new();
```

- [ ] **Step 4: Add the public pending API**

In the same file, add these members right after the `LastErrorMessage` property
(before `public void AttachErrorBus(...)`):

```csharp
    /// <summary>True while a pending paste is held in memory awaiting a pill click.</summary>
    public bool HasPendingPaste => _pending.HasPending;

    /// <summary>The deferred text held in the pending slot (memory only, never persisted).</summary>
    public string PendingPasteText => _pending.PendingText;

    /// <summary>
    /// Enter the pending-paste state: hold the final text in memory (never
    /// persisted) and show the pill's PENDING visual. Because Stage becomes
    /// PendingPaste (not Idle), the pill's Idle auto-hide does not fire.
    /// </summary>
    public void EnterPendingPaste(string text, InjectionTarget target) => _ui.Post(() =>
    {
        _pending.SetPending(text, target);
        Stage = SessionStage.PendingPaste;
        StatusText = "Click to paste";
    });

    /// <summary>
    /// Report the outcome of a pill-click paste attempt (called on the UI
    /// thread by the pill click handler). On success the slot is consumed and
    /// the VM returns to Idle; on failure the slot is kept so the user can
    /// click again. Returns true when the slot was consumed.
    /// </summary>
    public bool NotifyPasteAttempted(bool injected)
    {
        var consumed = _pending.OnPasteAttempted(injected);
        if (consumed)
        {
            Stage = SessionStage.Idle;
            StatusText = "Ready";
        }
        return consumed;
    }
```

- [ ] **Step 5: Guard the ErrorBus handler**

Replace the existing `OnBusReport` method body so a held pending slot keeps the
pill clickable instead of flipping to Error:

```csharp
    private void OnBusReport(ErrorRecord rec) => _ui.Post(() =>
    {
        LastErrorStage = rec.Stage;
        LastErrorMessage = rec.Message;
        // While a pending paste is held (e.g. a failed pill-click retry), keep
        // the pill in its clickable PENDING state instead of flipping to Error
        // so the user can click again. The error is still recorded above and is
        // surfaced to the user via the toast raised by the caller.
        if (_pending.HasPending) return;
        Stage = SessionStage.Error;
        StatusText = $"Error ({rec.Stage}): {rec.Message}";
    });
```

- [ ] **Step 6: Guard the engine state handler**

Replace the `Recording` and `Idle` arms inside `OnEngineStateChanged` so a new
dictation discards the pending slot and a completed injection does not hide the
pending pill. The full method becomes:

```csharp
    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            switch (to)
            {
                case SessionState.Recording:
                    _pending.Discard(); // Rule 5: a new dictation discards any pending paste.
                    _stopwatch.Restart();
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
                case SessionState.Transcribing:
                    Stage = SessionStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case SessionState.Injecting:
                    Stage = SessionStage.Injecting;
                    StatusText = "Inserting...";
                    break;
                case SessionState.Idle:
                    _stopwatch.Stop();
                    // If a pending paste is held, keep the PENDING pill visible
                    // instead of returning to Idle (which would auto-hide it).
                    if (_pending.HasPending) break;
                    Stage = SessionStage.Idle;
                    StatusText = "Ready";
                    break;
            }
        });
    }
```

- [ ] **Step 7: Run tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true -v minimal
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" -method "Winpepper.Core.Tests.ViewModels.SessionViewModelPendingTests.*"
```
Expected: `Failed: 0`. All seven VM integration tests pass.

- [ ] **Step 8: Run the FULL Core suite (no regressions)**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: `Failed: 0`. The existing `SessionViewModel`-adjacent tests (audio
level, error handling) and everything else in `Winpepper.Core.Tests` still pass.

- [ ] **Step 9: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs
git commit -m "$(cat <<'EOF'
feat(core): integrate pending-paste state into SessionViewModel

EnterPendingPaste holds text in memory and shows the PENDING pill; the Idle
auto-hide is suppressed while pending; a new dictation discards the slot; an
error while pending keeps the pill clickable; NotifyPasteAttempted consumes on
success and keeps on failure.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 6: Runtime click-through toggle (Windows glue)

The pill is click-through by design (`WS_EX_TRANSPARENT`). For the PENDING
state it must become clickable, then return to click-through afterward. Add a
runtime toggle that clears/sets only `WS_EX_TRANSPARENT` and **keeps
`WS_EX_NOACTIVATE` at all times** so clicking the pill never moves focus (the
target field must retain focus so injection goes to the right place).

> **Platform note:** This file is `#if WINDOWS` and is NOT compiled or run on
> Linux. It is verified in the Windows Smoke Test Checklist (items 3 and 5).
> The implementer verifies on Linux only that the pure-managed suite still
> builds/passes (unchanged) and by code inspection.

**Files:**
- Modify: `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`

**Interfaces:**
- Consumes: existing `GetWindowLongPtr64`/`SetWindowLongPtr64`, `GWL_EXSTYLE`,
  `WS_EX_TRANSPARENT`, `AssertTopmost`.
- Produces: `static void ExtendedWindowStyle.SetClickThrough(IntPtr hwnd, bool clickThrough)`.

- [ ] **Step 1: Add the toggle method**

In `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`, add this method
inside the class, immediately after `MakeClickThroughTopmostTool`:

```csharp
    /// <summary>
    /// Toggle ONLY the WS_EX_TRANSPARENT bit at runtime. When clickThrough is
    /// false the pill receives mouse input (needed for the PENDING "click to
    /// paste" state); when true, clicks pass through as normal. WS_EX_NOACTIVATE
    /// is left untouched in BOTH states so clicking the pill never activates it
    /// or steals focus from the target field. Re-asserts topmost afterward
    /// because changing the ex-style can drop us out of the topmost band.
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated = clickThrough
            ? existing | WS_EX_TRANSPARENT
            : existing & ~(long)WS_EX_TRANSPARENT;
        if (updated == existing) return;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        AssertTopmost(hwnd);
    }
```

- [ ] **Step 2: Confirm the pure-managed suite is unaffected**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: `Failed: 0` (this Windows-only file is not part of the Core suite; the
run confirms no accidental cross-project breakage). Also confirm the new method
exists:
```bash
grep -n "public static void SetClickThrough" src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs
```
Expected: one match.

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs
git commit -m "$(cat <<'EOF'
feat(app): add runtime WS_EX_TRANSPARENT toggle for the pill

SetClickThrough clears/sets only WS_EX_TRANSPARENT so the pill can become
clickable in the PENDING state and return to click-through afterward, while
keeping WS_EX_NOACTIVATE so clicks never steal focus from the target field.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 7: Pill PENDING visual + click handler (Windows glue)

Give the pill a distinct PENDING look (steady blue dot, no pulse, stays visible,
no auto-hide) and make it clickable in that state. Clicking invokes a supplied
handler (wired in Task 9) that performs the paste, then reports the outcome.

> **Platform note:** `#if WINDOWS`; not Linux-buildable. Verified in the Windows
> Smoke Test Checklist. Uses the `PillAnimationMap`/`SessionStage.PendingPaste`
> mapping already unit-tested in Task 4 and the VM state already tested in Task 5.

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml`
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`

**Interfaces:**
- Consumes: `SessionViewModel.Stage` (now includes `PendingPaste`),
  `ExtendedWindowStyle.SetClickThrough` (Task 6).
- Produces: `public Func<bool>? PastePendingHandler { get; set; }` on
  `StatusPillWindow` (set by `AppShell` in Task 9). Returns `true` when the
  paste succeeded.

- [ ] **Step 1: Name the root grid and add a pointer handler in XAML**

Edit `src/Winpepper.App/Views/StatusPillWindow.xaml`. Give the root `Grid` a
name and a pointer-pressed handler so the PENDING state can receive clicks. The
`<Grid ...>` line becomes:

```xml
    <Grid x:Name="RootGrid" Background="Transparent" Padding="12,6"
          PointerPressed="OnRootPointerPressed">
```

(Leave the rest of the XAML unchanged.)

- [ ] **Step 2: Add the handler field and pointer callback in code-behind**

In `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`, add a using for pointer
event args near the other `Microsoft.UI.Xaml` usings:

```csharp
using Microsoft.UI.Xaml.Input;
```

Add a public handler property next to the other fields (after
`private PillAnimationMode _animMode = PillAnimationMode.None;`):

```csharp
    /// <summary>
    /// Invoked when the user clicks the pill while it is in the PENDING state.
    /// Wired by AppShell to perform the paste at click time. Returns true when
    /// the paste succeeded (slot consumed), false when it failed (slot kept).
    /// </summary>
    public Func<bool>? PastePendingHandler { get; set; }
```

Add the pointer callback as a new method (place it after `OnVmChanged`):

```csharp
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Only actionable in the PENDING state; other states are click-through.
        if (_vm.Stage != SessionStage.PendingPaste) return;
        e.Handled = true;
        // The handler injects into whatever field is focused NOW (the user's
        // explicit choice) and reports the outcome to the VM. On success the VM
        // returns to Idle, which hides the pill via OnVmChanged's Idle arm.
        PastePendingHandler?.Invoke();
    }
```

- [ ] **Step 3: Add the PENDING branch to `OnVmChanged`**

In `OnVmChanged`, add a dedicated `PendingPaste` branch BEFORE the existing
`if (_vm.Stage == SessionStage.Idle)` check. It must: stop the hide timer, stay
visible, show a steady blue dot, make the window clickable, and NOT start the
"thinking" tick loop. Insert this block right after the
`_animMode = PillAnimationMap.ForStage(_vm.Stage);` line:

```csharp
        if (_vm.Stage == SessionStage.PendingPaste)
        {
            _tickTimer.Stop();               // no thinking pulse while waiting
            _visible = true;
            ResetPillVisual();               // steady dot, full opacity, scale 1
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            ExtendedWindowStyle.AssertTopmost(_hwnd);
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: false); // make pill clickable
            _hideTimer.Stop();               // never auto-hide while pending
            return;
        }
```

- [ ] **Step 4: Restore click-through when leaving the PENDING state**

The Idle, Error, and working branches must restore click-through (the pill is
click-through in all normal states). Add
`ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);` as the FIRST
line inside each of the three existing branches:

In the `if (_vm.Stage == SessionStage.Idle)` branch, before `_tickTimer.Stop();`:
```csharp
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
```
In the `else if (_vm.Stage == SessionStage.Error)` branch, before `_tickTimer.Stop();`:
```csharp
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
```
In the final `else` (working stages) branch, before the `Dot.Fill = ...` switch:
```csharp
            ExtendedWindowStyle.SetClickThrough(_hwnd, clickThrough: true);
```

- [ ] **Step 5: Confirm the pure-managed suite is unaffected + inspect edits**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
grep -n "PendingPaste\|PastePendingHandler\|SetClickThrough\|OnRootPointerPressed" \
    src/Winpepper.App/Views/StatusPillWindow.xaml.cs
```
Expected: `Failed: 0`; grep shows the PENDING branch, handler property, pointer
callback, and the four `SetClickThrough` calls (one false in PENDING, three true
in the normal branches). This file's runtime behavior is verified in the Windows
Smoke Test Checklist (items 2–5).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml \
        src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): pill PENDING visual + click-to-paste handler

PENDING state shows a steady blue dot, stays visible (no auto-hide, no pulse),
and becomes clickable via SetClickThrough(false). A PointerPressed handler on
the root grid invokes PastePendingHandler; normal states restore click-through.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 8: Pipeline capture + decision + pending wiring (Windows glue)

Capture the injection target when dictation STARTS, re-capture at injection
time, and use `PendingPasteDecider` to either inject as today or hold as
pending. Also add the `TryPastePending()` method that the pill click invokes.
`PipelineHost` has **two** near-identical hotkey branches (HoldDown/HoldUp and
Toggle start/stop) — apply the capture at BOTH start sites and the decision at
BOTH injection sites.

> **Platform note:** `#if WINDOWS`; not Linux-buildable. The decision/state
> logic it calls is unit-tested (Tasks 2,3,5); this task is the thin wiring,
> verified in the Windows Smoke Test Checklist (items 1–4).

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes: `_focusedCapturer` (`FocusedElementCapturer?`, existing field),
  `FocusedElementSnapshot` (existing: `ForegroundHwnd`, `ElementId`, `IsValid`),
  `InjectionTarget`/`PendingPasteDecider`/`InjectionDecision` (Tasks 1,2),
  `_vm.EnterPendingPaste`/`_vm.HasPendingPaste`/`_vm.PendingPasteText`/
  `_vm.NotifyPasteAttempted` (Task 5), `_injector.TryInject` (existing),
  `_errorBus.Report` / `_clipboardFallback.Copy` / `_toasts.ShowAsync` (existing).
- Produces on `PipelineHost`:
  - `private InjectionTarget _targetAtStart = InjectionTarget.Empty;`
  - `private InjectionTarget CaptureTarget()`
  - `public bool TryPastePending()`.

- [ ] **Step 1: Add usings and the start-target field**

At the top of `src/Winpepper.App/Hosting/PipelineHost.cs`, add:

```csharp
using Winpepper.Core.Pending;
```

Add a field next to the other private fields (near `_focusedCapturer`):

```csharp
    private InjectionTarget _targetAtStart = InjectionTarget.Empty;
```

- [ ] **Step 2: Add the capture + paste helpers**

Add these two methods to the class (place them near the injection logic):

```csharp
    /// <summary>
    /// Capture the current focused-field identity as a pure InjectionTarget.
    /// Maps the Windows-only FocusedElementSnapshot into the platform-agnostic
    /// identity the pure decider compares. Returns Empty when capture is
    /// unavailable (no capturer) or fails (invalid snapshot) — the decider then
    /// defaults to InjectNow, preserving today's behavior.
    /// </summary>
    private InjectionTarget CaptureTarget()
    {
        if (_focusedCapturer is null) return InjectionTarget.Empty;
        var snap = _focusedCapturer.Capture();
        if (!snap.IsValid) return InjectionTarget.Empty;
        return new InjectionTarget
        {
            WindowHandle = snap.ForegroundHwnd.ToInt64(),
            ElementId = snap.ElementId,
        };
    }

    /// <summary>
    /// Paste the held pending text into whatever field is focused NOW (the
    /// user's explicit choice via the pill click). Uses the normal injection
    /// path. On success the VM consumes the slot and hides the pill; on failure
    /// the error is surfaced via the ErrorBus/clipboard/toast pattern and the
    /// pending slot is kept (NotifyPasteAttempted(false)) so the user can retry.
    /// Returns true when the paste succeeded. Runs on the UI thread.
    /// </summary>
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = _vm.PendingPasteText;
        var injected = !string.IsNullOrWhiteSpace(text) && _injector.TryInject(text);
        if (!injected)
        {
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; clipboard fallback engaged"),
                _currentSessionId);
            _clipboardFallback.Copy(text);
            _ = _toasts.ShowAsync(
                "Winpepper",
                "Couldn't type into the active window. The text is on your clipboard.",
                Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                TimeSpan.FromSeconds(6));
        }
        return _vm.NotifyPasteAttempted(injected);
    }
```

- [ ] **Step 3: Capture the target at BOTH dictation-start sites**

In the HoldDown branch (recording start, around the `StartRequested` /
recording-begin logic near `PipelineHost.cs:211-260`) add, right after recording
actually starts:

```csharp
        _targetAtStart = CaptureTarget();
```

Do the **same** in the Toggle-start branch (around `PipelineHost.cs:379-430`) at
the equivalent recording-start point. Both branches must set `_targetAtStart`
each time a new dictation begins.

- [ ] **Step 4: Apply the decision at BOTH injection sites**

In the HoldUp branch, replace the current injection block
(`PipelineHost.cs:302-320`, the `if (!string.IsNullOrWhiteSpace(final)) { injected = _injector.TryInject(final); ... }`)
with a decision-gated version:

```csharp
        var injectSw = System.Diagnostics.Stopwatch.StartNew();
        var injected = false;
        if (!string.IsNullOrWhiteSpace(final))
        {
            var targetAtInject = CaptureTarget();
            var decision = PendingPasteDecider.Decide(_targetAtStart, targetAtInject);
            if (decision == InjectionDecision.HoldPending)
            {
                // Focus moved to a different known field: do NOT inject anywhere.
                // Hold the text as an in-memory pending paste (never persisted).
                _vm.EnterPendingPaste(final, _targetAtStart);
            }
            else
            {
                injected = _injector.TryInject(final);
                if (!injected)
                {
                    _errorBus.Report(
                        Winpepper.Core.Errors.ErrorStage.Injection,
                        new InvalidOperationException("SendInput refused; clipboard fallback engaged"),
                        _currentSessionId);
                    _clipboardFallback.Copy(final);
                    _ = _toasts.ShowAsync(
                        "Winpepper",
                        "Couldn't type into the active window. The cleaned text is on your clipboard.",
                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                        TimeSpan.FromSeconds(6));
                }
            }
        }
        injectSw.Stop();
```

Apply the **identical** transformation to the Toggle-stop branch injection block
(`PipelineHost.cs:~491-514`). Both sites must gate on `PendingPasteDecider.Decide`.

> Note: leave the existing `_focusedCapturer.Capture()` post-paste-learning
> gate and the `_engine.Apply(SessionEvent.InjectionCompleted)` call unchanged
> in both branches. When we hold pending, `injected` stays false so the
> post-paste-learning gate does not fire (it already requires `injected == true`
> via `PostPasteGate.ShouldWatch`), and the engine still returns to Idle — the
> VM's Idle guard (Task 5) keeps the PENDING pill visible.

- [ ] **Step 5: Confirm the pure-managed suite is unaffected + inspect edits**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
grep -n "CaptureTarget()\|EnterPendingPaste\|TryPastePending\|PendingPasteDecider" \
    src/Winpepper.App/Hosting/PipelineHost.cs
```
Expected: `Failed: 0`; grep shows `_targetAtStart = CaptureTarget();` at **two**
start sites, `PendingPasteDecider.Decide` at **two** inject sites, one
`EnterPendingPaste` per inject site, and one `TryPastePending` definition. This
wiring is verified end-to-end in the Windows Smoke Test Checklist (items 1–4).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
feat(app): capture target + decide inject-vs-hold in the pipeline

Captures the focused-field identity at dictation start (both hotkey branches),
re-captures at injection time, and uses PendingPasteDecider to inject as today
or hold as pending. Adds TryPastePending() for the pill click, keeping the
pending slot on failure and consuming it on success.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 9: AppShell wiring — pill click → paste-pending (Windows glue)

Connect the pill's `PastePendingHandler` to `PipelineHost.TryPastePending` so a
click actually pastes. This is the final seam that makes the feature reachable
by the user.

> **Platform note:** `#if WINDOWS`; not Linux-buildable. Verified in the Windows
> Smoke Test Checklist (items 2–4).

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs`

**Interfaces:**
- Consumes: the `StatusPillWindow` instance and the `PipelineHost` instance that
  `AppShell` already constructs/wires; `StatusPillWindow.PastePendingHandler`
  (Task 7); `PipelineHost.TryPastePending` (Task 8).
- Produces: no new public API; a one-line wiring assignment.

- [ ] **Step 1: Locate the pill + host construction**

In `src/Winpepper.App/Hosting/AppShell.cs`, find where the `StatusPillWindow`
and `PipelineHost` are created (both are already constructed here — the pill is
built with the `SessionViewModel`, and `PipelineHost` receives the same VM).
Confirm the local variable names, e.g. `pill` (`StatusPillWindow`) and
`pipelineHost` (`PipelineHost`):

```bash
grep -n "new StatusPillWindow\|new PipelineHost\|StatusPillWindow \|PipelineHost " \
    src/Winpepper.App/Hosting/AppShell.cs
```
Expected: shows the two construction sites and their variable names. Use those
exact names in Step 2 (substitute if they differ from `pill`/`pipelineHost`).

- [ ] **Step 2: Wire the click handler**

After BOTH the pill and the pipeline host exist (i.e. after the later of the two
construction sites), add:

```csharp
        // Clicking the pill in its PENDING state pastes the held text into the
        // field focused at click time, via the normal injection path.
        pill.PastePendingHandler = pipelineHost.TryPastePending;
```

(If the pill is built as a field like `_pill` and the host as `_pipelineHost`,
use those names instead.)

- [ ] **Step 3: Confirm the pure-managed suite is unaffected + inspect edit**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
grep -n "PastePendingHandler = " src/Winpepper.App/Hosting/AppShell.cs
```
Expected: `Failed: 0`; grep shows exactly one wiring assignment. End-to-end
behavior is verified in the Windows Smoke Test Checklist.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Hosting/AppShell.cs
git commit -m "$(cat <<'EOF'
feat(app): wire pill click to PipelineHost.TryPastePending

The PENDING pill's click handler now pastes the held text into the currently
focused field via the pipeline's normal injection path.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Windows Smoke Test Checklist

These behaviors live in `#if WINDOWS` code that cannot build or run on Linux.
They are IMPLEMENTED in this plan (Tasks 6–9), not stubbed or deferred. Each
maps to a spec requirement whose pure-managed decision/state logic is already
covered by the Linux tests above (Tasks 1–5). Verify on the Windows VM after the
plan lands:

1. **Same-target inject is unchanged (Req 2, zero behavior change):** Focus a
   text field, hold the hotkey, speak, release WITHOUT moving focus. The cleaned
   text is typed into that field exactly as before. No pill "Click to paste"
   state appears.
2. **Focus moved → no stray paste, pill shows Click to paste (Req 1–3):** Focus
   field A, start dictation, speak, and BEFORE releasing (or before the pipeline
   finishes) click into a different field B (or trigger a popup that steals
   focus). On completion: text is NOT injected into B (or anywhere); the pill
   stays visible with a steady blue dot and "Click to paste"; no thinking pulse;
   the Idle auto-hide does not fire.
3. **Clicking the pill pastes into the now-focused field (Req 4):** With the
   pill in PENDING state, click into field C, then click the pill. The held text
   is typed into C. Focus remains on C when clicking the pill (WS_EX_NOACTIVATE
   kept). The pending slot clears and the pill hides.
4. **Next hotkey discards pending (Req 5):** Reach the PENDING state, then press
   the dictation hotkey again (start a new dictation). The old pending text is
   discarded (never pasted); the pill returns to its normal Recording lifecycle.
5. **Pill is click-through in normal states (Req 4 gotcha):** During Recording/
   Transcribing/CleaningUp/Injecting/Idle/Error, clicks pass THROUGH the pill to
   the window beneath it (only the PENDING state is clickable). After a pending
   paste completes or is discarded, the pill is click-through again.
6. **Injection failure on pill-click keeps pending (Req 6):** Force an injection
   failure at click time (e.g. focus a field that rejects SendInput). A toast/
   clipboard fallback surfaces the error, the pill STAYS in "Click to paste",
   and clicking again into a normal field succeeds.
7. **Cancel leaves no pending (Req 6):** Start dictation, move focus away, then
   press Esc (cancel) before completion. No pending paste is created; the pill
   hides normally.
8. **Pending never persisted (Req 2):** With a pending paste held, inspect
   `%LOCALAPPDATA%\winpepper\` — no settings/history/log file contains the
   pending text. History archiving continues to work for actually-injected
   dictations and is unchanged.
9. **Subsequent dictation animations intact (Req 6):** After discarding a
   pending paste via a new hotkey, the voice meter (Recording) and thinking
   pulse (Transcribing/CleaningUp/Injecting) animate normally, and the strict-
   topmost re-assert still pins the pill.

---

## Self-Review

**1. Spec coverage.**

- **Req 1 — capture target identity at dictation START, pragmatic identity
  (foreground HWND + focused element), pure seam + Windows-thin impl:**
  `InjectionTarget` (T1, pure, tested) is the identity; `CaptureTarget()` maps
  the existing `FocusedElementSnapshot` (foreground HWND + UIA element id) into
  it at both start sites (T8); the comparison lives in the pure `Matches`/
  `PendingPasteDecider` (T1/T2, tested). ✓
- **Req 2 — compare at inject; same→inject as today; different→hold in memory
  ONLY, never disk; history archiving separate/unchanged:** `PendingPasteDecider`
  (T2, tested) decides; T8 injects unchanged on `InjectNow` and calls
  `EnterPendingPaste` on `HoldPending`; `PendingPasteState` is memory-only (T3,
  tested; Global Constraints + Smoke item 8 assert no persistence); no task
  touches `HistoryArchiver`. ✓
- **Req 3 — pill PENDING state: stays visible (no Idle auto-hide), steady blue
  dot + "Click to paste", no thinking pulse:** `SessionStage.PendingPaste` +
  `PillAnimationMap → None` (T4, tested); VM suppresses Idle hide + sets "Click
  to paste" (T5, tested); pill PENDING branch shows steady DodgerBlue, stops the
  tick, never auto-hides (T7; Smoke item 2). ✓
- **Req 4 — clicking pill pastes into now-focused field via normal injection;
  slot clears; pill hides; WS_EX_TRANSPARENT cleared while pending and restored
  after; WS_EX_NOACTIVATE kept; click handler on root element:**
  `SetClickThrough` toggles only `WS_EX_TRANSPARENT`, keeps `WS_EX_NOACTIVATE`
  (T6); PENDING makes clickable / normal states restore click-through (T7);
  `OnRootPointerPressed` on `RootGrid` → `PastePendingHandler` →
  `TryPastePending` (normal `_injector.TryInject`) → VM consumes + Idle hides
  (T7/T8/T9; Smoke items 3,5). ✓
- **Req 5 — pending discarded on next hotkey (new dictation); discard on app
  exit (memory only):** VM `OnEngineStateChanged` Recording arm calls
  `_pending.Discard()` (T5, tested `NewDictation_DiscardsPending`); app-exit is
  trivially memory-only (T3 doc). Smoke item 4. ✓
- **Req 6 — edge cases:** (a) dictation completes while pending already exists →
  the new dictation cleared pending at hotkey-down (T5 Recording-discard), so the
  new flow replaces everything; (b) injection failure on pill-click → ErrorBus/
  clipboard/toast AND keep pending (`OnPasteAttempted(false)` keeps — T3 tested;
  `NotifyPasteAttempted(false)` keeps stage PENDING — T5 tested; `OnBusReport`
  guard keeps clickable — T5 tested; T8 `TryPastePending`); (c) cancel (Esc) →
  engine → Idle without ever entering Injecting, so `EnterPendingPaste` is never
  called → no pending (Smoke item 7); (d) pending must not interfere with
  strict-topmost re-assert or subsequent animations → PENDING branch calls
  `AssertTopmost`, normal branches restore click-through and animations
  (Smoke items 5,9). ✓
- **Verification — pure-managed xUnit v3 in-process on Linux; unit-test the
  state machine (create/replace/discard/consume), same-target comparison with
  fake identities, discard-on-next-hotkey, injection-failure-keeps-pending; run
  full non-Windows suite; Windows bits thin + smoke checklist:** T0 provisions;
  every Core test uses `dotnet exec ... -notrait "Platform=Windows"`; T3 covers
  create/replace/discard/consume + keep-on-failure; T1/T2 cover comparison with
  hand-built identities; T5 covers discard-on-next-hotkey and
  failure-keeps-pending at the VM level; T5 Step 8 runs the full suite; Tasks
  6–9 are thin and routed to the Windows Smoke Test Checklist. ✓
- **Out of scope — hook internals + packaging untouched:** no task edits
  `src/Winpepper.Platform/Hotkeys/*` or `packaging/`; T8 consumes existing
  hotkey-driven pipeline events only. ✓

**1b. No silent deferrals of required behavior.** No stubs, mocks, fake
providers, synthetic URLs, or TODOs stand in for required behavior. Every
requirement has (a) pure decision/state logic executed and asserted on Linux —
`InjectionTarget`/`PendingPasteDecider`/`PendingPasteState`/`PillAnimationMap`/
`SessionViewModel` pending behavior — and (b) a named production outcome in the
Windows Smoke Test Checklist for the genuinely platform-bound WinUI/Win32 glue
(UIA capture, `SendInput`, `WS_EX_TRANSPARENT` toggle, pill window, pointer
click). The glue is IMPLEMENTED in Tasks 6–9 (real `SetClickThrough`, real
pointer handler, real `TryInject` path, real `AppShell` wiring), not seams
standing in for behavior. No requirement is moved to "known limitations" or
"future work." **There is NO UNRESOLVED COVERAGE GAP.**

**2. Placeholder scan.** No "TBD/TODO/handle edge cases/similar to Task N".
Every code step shows complete, copy-pasteable code; every run step gives an
exact command and expected result. The repeated SDK-export/build/exec
boilerplate is intentionally restated per step because a fresh implementer may
execute tasks out of order.

**3. Type consistency.** Signatures are used identically across tasks:
`InjectionTarget { long WindowHandle; string ElementId; }` with
`bool IsValid`, `bool Matches(InjectionTarget)`, `static Empty` (T1; consumed
T2/T3/T5/T8); `PendingPasteDecider.Decide(InjectionTarget, InjectionTarget)`
returning `InjectionDecision { InjectNow, HoldPending }` (T2; used T8);
`PendingPasteState.SetPending(string, InjectionTarget)` / `Discard()` /
`bool OnPasteAttempted(bool)` (T3; used T5); `SessionStage.PendingPaste` (T4;
used T5/T7); `SessionViewModel.EnterPendingPaste(string, InjectionTarget)`,
`HasPendingPaste`, `PendingPasteText`, `bool NotifyPasteAttempted(bool)` (T5;
used T7/T8); `ExtendedWindowStyle.SetClickThrough(IntPtr, bool)` (T6; used T7);
`StatusPillWindow.PastePendingHandler` (`Func<bool>?`, T7; set T9);
`PipelineHost.TryPastePending()` returning `bool` and `CaptureTarget()`
returning `InjectionTarget` (T8; wired T9). The `FocusedElementSnapshot` fields
consumed in T8 (`ForegroundHwnd`, `ElementId`, `IsValid`) match the existing
record. No renamed-mismatch or dangling references.
