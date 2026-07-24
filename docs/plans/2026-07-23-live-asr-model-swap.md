# Live ASR Model Swap Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make selecting a different LOCAL Parakeet ASR model (on the Models
page or the History-detail promote path) take effect immediately for the next
dictation — no app restart — while never disturbing an in-flight dictation.

**Architecture:** The transcription pipeline runs on a single serialized event
loop (`PipelineHost.RunAsync` → `HandleHotkey`), so there is never a concurrent
transcription. The UI promote callbacks already persist `AppSettings.AsrModelName`.
We add a **pure, unit-tested decision component** (`AsrModelSwapState` in
`Winpepper.Core`) that, given the currently-loaded model, the desired model from
settings, and whether the desired model's files are present, decides whether to
keep the current session, load the first session, or swap. The Windows-only
`PipelineHost` calls this decider **at the start of each dictation** (under its
existing `_startGate` lock, on the event-loop thread) — an inherently race-free
seam because the previous dictation has already completed. On a successful load
the old `ParakeetSession` is disposed; on load failure the previous working
session is kept and an error is reported (toast with "Open Models tab" deep
link). The model directory is derived from the selected descriptor via the
registry instead of the hardcoded `AppPaths.ParakeetModelDir`, and the live
loaded model name is threaded into the transcriber so new history entries record
the swapped model name.

**Tech Stack:** C# / .NET 9, WinUI 3 (App layer, Windows-only), ONNX Runtime
(Parakeet), xUnit v3 + Shouldly for tests.

## Global Constraints

- Main tip: `596dec0`. Do NOT touch the keyboard hook or `packaging/`.
- Pure decision logic (when to apply the swap, keep-old-on-failure, generation
  tracking) MUST live in `Winpepper.Core` and be unit-tested on Linux. The
  `Winpepper.App` project is Windows-only (`net9.0-windows10.0.19041.0`) and has
  NO xUnit test project — its wiring is verified by Windows build + the Windows
  smoke checklist (Task 8), not by Linux tests.
- Pure-managed test projects run on Linux via the xUnit v3 in-process runner:
  build `-c Release`, then `dotnet exec <built test dll>`. Do NOT use
  `dotnet test` (VSTest crashes on this host). The repo-local SDK lives at
  `/home/dan/code/winpepper/.dotnet` (`.dotnet` is gitignored).
- Test framework: xUnit v3 (`using Xunit;`), Shouldly assertions
  (`using Shouldly;`), `[Fact]` methods named `Method_Scenario_Expected`,
  namespace matches `<Assembly>.Tests`. Package versions are centralized
  (`Directory.Packages.props`) — never add inline `Version="..."`.
- Baseline to preserve green: full non-Windows suite (~810–822 tests; Core 278,
  Asr 94, Platform 209 Linux TFM). New tests add to these counts.
- Never leave the pipeline dead after a failed swap: on load failure, keep the
  previous working `ParakeetSession` if one exists and report via `ErrorBus`
  with `ErrorStage.Asr`.
- Never swap mid-transcription: the swap seam is the start of a dictation, on the
  event-loop thread, under `_startGate`.

---

## UNRESOLVED COVERAGE GAPS

None. Every spec requirement maps to a task below. The Windows-only wiring
(Tasks 6–7) cannot be covered by Linux xUnit tests because `Winpepper.App` is a
Windows-only project with no test project in the repo; those requirements are
covered by exact line-level changes plus the mandatory Windows smoke checklist
in Task 8. The core decision behavior each of those requirements depends on IS
unit-tested in pure code (Tasks 1–2, 5).

---

## Requirement → Coverage Map

| Spec requirement | Observable production outcome | Covered by |
|---|---|---|
| 1. Local model change effective next dictation, never mid-transcription | Next dictation after a promote uses the newly selected model; an in-flight dictation finishes on the old model | Pure decider Tasks 1–2; wiring Task 6 (call at dictation start under `_startGate`); Windows smoke Task 8 |
| 2. Derive model dir from selected descriptor, not hardcoded path | `PipelineHost` loads `ParakeetSession` from `ModelsRoot/<InstallDirRelative>` of the selected model; default resolves to the same path as before | `ModelRegistry.InstallDirFor` Task 3 (unit-tested); wiring Task 6–7 |
| 3. Respect verified provisioning; keep current model until download/verify completes; never dead after failed swap | Selecting a not-yet-installed model keeps the current session alive; after download the next dictation swaps; failed load keeps old + toast | Pure decider `Plan` returns `KeepCurrent` when desired absent (Task 2); keep-old-on-failure in `TryEnsureAsrModel` (Task 6); Windows smoke Task 8 |
| 4. Dispose old session after swap, safely | Old `ParakeetSession.Dispose()` called only after the new one is installed, on the event-loop thread under `_startGate` | Wiring Task 6; Windows smoke Task 8 |
| 5. History records the NEW model name after a swap | New history entries' `AsrModelName` equals the actually-loaded model, not a constructor snapshot or hardcoded default | Live-name threading Task 7 (`BuildTranscriber` uses live loaded name); Windows smoke Task 8 |
| 6. Cloud (AssemblyAI) path unaffected; no new UI controls | AssemblyAI still re-reads settings per dictation; Models page UI unchanged | No change to cloud path or XAML; verified by full-suite regression Task 5 + Windows smoke Task 8 |

---

## File Structure

**Created:**
- `src/Winpepper.Core/Asr/AsrSwapAction.cs` — enum of swap decisions (pure).
- `src/Winpepper.Core/Asr/AsrModelSwapState.cs` — pure decision state machine.
- `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs` — unit tests.
- `tests/Winpepper.Models.Tests/ModelRegistryInstallDirTests.cs` — unit tests.

**Modified:**
- `src/Winpepper.Models/ModelRegistry.cs` — add `InstallDirFor(...)`.
- `src/Winpepper.App/Hosting/PipelineHost.cs` — replace `_modelDir`/`_asrModelName`
  snapshots with a resolver + `AsrModelSwapState`; add `TryEnsureAsrModel()`;
  call it in `TryStartCore` and at both dictation transcribe seams; thread the
  live loaded name into the transcriber factory. (Windows-only)
- `src/Winpepper.App/Hosting/AppShell.cs` — pass a model-dir resolver instead of
  `AppPaths.ParakeetModelDir`; new transcriber-factory signature; `BuildTranscriber`
  uses the live loaded name for `ParakeetTranscriber`. (Windows-only)

---

## Task 1: `AsrSwapAction` enum + `AsrModelSwapState` skeleton (initial-load cases)

Pure `Winpepper.Core` component. This task establishes the type and the
first two decision branches (no session yet).

**Files:**
- Create: `src/Winpepper.Core/Asr/AsrSwapAction.cs`
- Create: `src/Winpepper.Core/Asr/AsrModelSwapState.cs`
- Test: `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `enum AsrSwapAction { KeepCurrent, Load, Swap, CannotStart }` in namespace
    `Winpepper.Core.Asr`.
  - `sealed class AsrModelSwapState` in namespace `Winpepper.Core.Asr` with:
    - `string? LoadedModelName { get; }` — null until first successful load.
    - `int Generation { get; }` — starts at 0, increments on each `CommitLoad`.
    - `AsrSwapAction Plan(string desiredModelName, bool desiredFilesPresent)` —
      pure, does NOT mutate state.
    - `void CommitLoad(string modelName)` — sets `LoadedModelName = modelName`
      and increments `Generation`. Called by the host ONLY after a session is
      successfully (re)loaded.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Asr;
using Xunit;

namespace Winpepper.Core.Tests.Asr;

public class AsrModelSwapStateTests
{
    [Fact]
    public void Plan_NoSessionLoaded_DesiredPresent_ReturnsLoad()
    {
        var state = new AsrModelSwapState();

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Load);
    }

    [Fact]
    public void Plan_NoSessionLoaded_DesiredMissing_ReturnsCannotStart()
    {
        var state = new AsrModelSwapState();

        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: false)
             .ShouldBe(AsrSwapAction.CannotStart);
    }

    [Fact]
    public void Plan_DoesNotMutateState()
    {
        var state = new AsrModelSwapState();

        state.Plan("parakeet-tdt-0.6b-v3", desiredFilesPresent: true);

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```
Expected: FAIL — build error "The type or namespace name 'Asr' does not exist
in the namespace 'Winpepper.Core'" (and `AsrModelSwapState` / `AsrSwapAction`
not found).

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Asr/AsrSwapAction.cs`:

```csharp
namespace Winpepper.Core.Asr;

/// <summary>
/// The decision produced by <see cref="AsrModelSwapState.Plan"/> about what the
/// pipeline host should do with its local ASR session before the next dictation.
/// </summary>
public enum AsrSwapAction
{
    /// <summary>Keep the currently loaded session; no reload needed.</summary>
    KeepCurrent,

    /// <summary>No session is loaded yet; load the desired model.</summary>
    Load,

    /// <summary>A different model is desired and present; swap to it.</summary>
    Swap,

    /// <summary>No session loaded and the desired model's files are absent.</summary>
    CannotStart,
}
```

Create `src/Winpepper.Core/Asr/AsrModelSwapState.cs`:

```csharp
namespace Winpepper.Core.Asr;

/// <summary>
/// Pure decision state for live ASR model swapping. Holds which local model is
/// currently loaded and decides, per dictation, whether to keep it, load the
/// first session, or swap to a newly selected model.
///
/// State only advances via <see cref="CommitLoad"/>, which the host calls after
/// a session is successfully (re)loaded. If a load fails, the host does not call
/// CommitLoad, so <see cref="LoadedModelName"/> keeps naming the previous
/// working model — this is the "keep-old-on-failure" guarantee expressed in
/// pure, testable code.
/// </summary>
public sealed class AsrModelSwapState
{
    /// <summary>The model whose session is currently loaded; null until first load.</summary>
    public string? LoadedModelName { get; private set; }

    /// <summary>Number of successful (re)loads so far; starts at 0.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Decide what to do for the next dictation given the desired model (from
    /// settings) and whether its files are present/verified on disk. Pure: does
    /// not mutate state.
    /// </summary>
    public AsrSwapAction Plan(string desiredModelName, bool desiredFilesPresent)
    {
        if (LoadedModelName is null)
            return desiredFilesPresent ? AsrSwapAction.Load : AsrSwapAction.CannotStart;

        // Later task fills in the loaded-session branches.
        return AsrSwapAction.KeepCurrent;
    }

    /// <summary>
    /// Record that a session for <paramref name="modelName"/> was successfully
    /// loaded. Advances state: sets the loaded name and increments the generation.
    /// </summary>
    public void CommitLoad(string modelName)
    {
        LoadedModelName = modelName;
        Generation++;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
     --filter-namespace Winpepper.Core.Tests.Asr
```
Expected: PASS — 3 tests passed in `Winpepper.Core.Tests.Asr`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.Core/Asr/AsrSwapAction.cs \
        src/Winpepper.Core/Asr/AsrModelSwapState.cs \
        tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs
git commit -m "feat(core): add AsrModelSwapState with initial-load decisions"
```

---

## Task 2: `AsrModelSwapState` loaded-session branches + generation tracking

Complete the pure decision machine: the branches when a session is already
loaded (keep, swap, keep-because-missing), and the `CommitLoad`/generation and
keep-old-on-failure behavior.

**Files:**
- Modify: `src/Winpepper.Core/Asr/AsrModelSwapState.cs`
- Test: `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs`

**Interfaces:**
- Consumes: `AsrModelSwapState`, `AsrSwapAction` (Task 1).
- Produces: fully-specified `Plan` behavior (all four branches) and
  `CommitLoad` semantics — relied on by `PipelineHost.TryEnsureAsrModel` (Task 6).
  Full `Plan` truth table:
  - `LoadedModelName == null` & present → `Load`
  - `LoadedModelName == null` & absent → `CannotStart`
  - loaded & `desired == LoadedModelName` → `KeepCurrent`
  - loaded & `desired != LoadedModelName` & present → `Swap`
  - loaded & `desired != LoadedModelName` & absent → `KeepCurrent`

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs` (inside the
class):

```csharp
    [Fact]
    public void CommitLoad_SetsLoadedNameAndIncrementsGeneration()
    {
        var state = new AsrModelSwapState();

        state.CommitLoad("parakeet-tdt-0.6b-v3");

        state.LoadedModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        state.Generation.ShouldBe(1);
    }

    [Fact]
    public void Plan_SameModelLoaded_ReturnsKeepCurrent()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-a", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.KeepCurrent);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredPresent_ReturnsSwap()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-b", desiredFilesPresent: true)
             .ShouldBe(AsrSwapAction.Swap);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredMissing_ReturnsKeepCurrent()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        // Desired model not downloaded yet: stay on the working model.
        state.Plan("model-b", desiredFilesPresent: false)
             .ShouldBe(AsrSwapAction.KeepCurrent);
    }

    [Fact]
    public void CommitLoad_AfterSwap_AdvancesLoadedNameAndGeneration()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        state.CommitLoad("model-b");

        state.LoadedModelName.ShouldBe("model-b");
        state.Generation.ShouldBe(2);
    }

    [Fact]
    public void FailedSwap_NoCommit_KeepsPreviousModelAndGeneration()
    {
        var state = new AsrModelSwapState();
        state.CommitLoad("model-a");

        // Host planned a Swap but the load threw, so it does NOT call CommitLoad.
        var action = state.Plan("model-b", desiredFilesPresent: true);
        action.ShouldBe(AsrSwapAction.Swap);
        // (no CommitLoad)

        state.LoadedModelName.ShouldBe("model-a");
        state.Generation.ShouldBe(1);
        // Next dictation still wants model-b and will retry the swap.
        state.Plan("model-b", desiredFilesPresent: true).ShouldBe(AsrSwapAction.Swap);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
     --filter-namespace Winpepper.Core.Tests.Asr
```
Expected: FAIL — `Plan_DifferentModelLoaded_DesiredPresent_ReturnsSwap` and
`Plan_DifferentModelLoaded_DesiredMissing_ReturnsKeepCurrent` fail because the
skeleton returns `KeepCurrent` for every loaded-session case (the Swap case is
asserted `Swap` but gets `KeepCurrent`). Other new tests pass.

- [ ] **Step 3: Write the implementation**

Replace the `Plan` method body in
`src/Winpepper.Core/Asr/AsrModelSwapState.cs` with the full truth table:

```csharp
    public AsrSwapAction Plan(string desiredModelName, bool desiredFilesPresent)
    {
        if (LoadedModelName is null)
            return desiredFilesPresent ? AsrSwapAction.Load : AsrSwapAction.CannotStart;

        if (string.Equals(desiredModelName, LoadedModelName, StringComparison.Ordinal))
            return AsrSwapAction.KeepCurrent;

        // A different model is selected. Swap only if its files are present;
        // otherwise keep the current working session until the download/verify
        // completes (a later dictation will re-evaluate and swap).
        return desiredFilesPresent ? AsrSwapAction.Swap : AsrSwapAction.KeepCurrent;
    }
```

(`CommitLoad`, `LoadedModelName`, `Generation` from Task 1 are unchanged.)

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
     --filter-namespace Winpepper.Core.Tests.Asr
```
Expected: PASS — 9 tests passed in `Winpepper.Core.Tests.Asr`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.Core/Asr/AsrModelSwapState.cs \
        tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs
git commit -m "feat(core): complete AsrModelSwapState swap/keep decision table"
```

---

## Task 3: `ModelRegistry.InstallDirFor` — name → install directory resolution

Pure `Winpepper.Models` helper so the host can derive the absolute model
directory from the selected model name, replacing the hardcoded
`AppPaths.ParakeetModelDir`. Verifies the default name resolves to the same
`.../models/parakeet-tdt-0.6b-v3` leaf as before.

**Files:**
- Modify: `src/Winpepper.Models/ModelRegistry.cs`
- Test: `tests/Winpepper.Models.Tests/ModelRegistryInstallDirTests.cs`

**Interfaces:**
- Consumes: existing `ModelRegistry.ResolveOrDefault(string?, ModelKind)`,
  `ModelDescriptor.InstallDirRelative`, `ModelKind` (all present).
- Produces:
  - `string ModelRegistry.InstallDirFor(string installRoot, string? requestedName, ModelKind kind)`
    → `Path.Combine(installRoot, ResolveOrDefault(requestedName, kind).InstallDirRelative)`.
    Unknown / null names fall back to the kind default (via `ResolveOrDefault`).
    Consumed by `AppShell` (Task 7) to build the resolver passed to `PipelineHost`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Models.Tests/ModelRegistryInstallDirTests.cs`:

```csharp
using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryInstallDirTests
{
    [Fact]
    public void InstallDirFor_DefaultAsr_MatchesLegacyParakeetLeaf()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, ModelRegistry.DefaultAsrName, ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_NullName_FallsBackToDefaultAsr()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, null, ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_UnknownName_FallsBackToDefaultAsr()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, "no-such-model", ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_CleanupDefault_UsesCleanupInstallDirRelative()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, ModelRegistry.DefaultCleanupName, ModelKind.Cleanup);

        dir.ShouldBe(Path.Combine(root, "cleanup", "qwen2.5-0.5b-instruct-q4_k_m"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release
```
Expected: FAIL — build error "'ModelRegistry' does not contain a definition for
'InstallDirFor'".

- [ ] **Step 3: Write minimal implementation**

In `src/Winpepper.Models/ModelRegistry.cs`, add this method to the
`ModelRegistry` class (immediately after `ResolveOrDefault`, before the closing
brace of the class):

```csharp
    /// <summary>
    /// Absolute install directory for the requested model, under
    /// <paramref name="installRoot"/>. Unknown or null names fall back to the
    /// kind default (see <see cref="ResolveOrDefault"/>).
    /// </summary>
    public string InstallDirFor(string installRoot, string? requestedName, ModelKind kind)
        => Path.Combine(installRoot, ResolveOrDefault(requestedName, kind).InstallDirRelative);
```

(If `System.IO` is not already available in the file, `Path` resolves via the
implicit global usings for the SDK; no explicit `using` line is needed because
the file already uses `Path.Combine` in the descriptor definitions.)

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll \
     --filter-class Winpepper.Models.Tests.ModelRegistryInstallDirTests
```
Expected: PASS — 4 tests passed.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.Models/ModelRegistry.cs \
        tests/Winpepper.Models.Tests/ModelRegistryInstallDirTests.cs
git commit -m "feat(models): add ModelRegistry.InstallDirFor for model dir resolution"
```

---

## Task 4: Pure-managed regression gate (Core + Models)

Verify the two pure additions haven't broken the existing Core/Models suites
before touching the Windows-only App layer.

**Files:**
- None created/modified — this is a verification task.

**Interfaces:**
- Consumes: everything from Tasks 1–3.
- Produces: a green Core + Models run (evidence for the plan review).

- [ ] **Step 1: Build and run Core tests**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```
Expected: PASS — all Core tests pass (278 baseline + 9 new = 287), 0 failed.

- [ ] **Step 2: Build and run Models tests**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll
```
Expected: PASS — all Models tests pass (baseline + 4 new), 0 failed.

- [ ] **Step 3: Commit (no-op marker)**

No files changed; record the gate in the branch history with an empty commit:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git commit --allow-empty -m "test: pure Core+Models suites green after swap-decider additions"
```

---

## Task 5: Wire the swap decider + resolver into `PipelineHost` (Windows-only)

Replace the frozen `_modelDir` and dead `_asrModelName` snapshots with a
model-dir resolver and an `AsrModelSwapState`, and add `TryEnsureAsrModel()`
which applies the pure decision (load/swap/keep, dispose-old, keep-old-on-failure).

> **Windows-only:** `Winpepper.App` targets `net9.0-windows10.0.19041.0` and does
> not build on Linux and has no xUnit test project. Verification is the Windows
> `dotnet build -c Release` of `Winpepper.App` (Task 8 Step 1) plus the smoke
> checklist (Task 8 Step 2+). The pure decision logic this wiring calls is
> already unit-tested (Tasks 1–2). Report exact line-level changes in the commit.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes:
  - `Winpepper.Core.Asr.AsrModelSwapState`, `AsrSwapAction` (Tasks 1–2).
  - `Winpepper.Asr.ParakeetSession.ModelFilesPresent(string modelDir) : bool`
    and `new ParakeetSession(string modelDir)` (existing).
  - A new ctor dependency `Func<string, string> resolveModelDir` (name → abs dir),
    supplied by `AppShell` in Task 7 as
    `name => registry.InstallDirFor(modelsRoot, name, ModelKind.Asr)`.
  - A new transcriber-factory signature (see Produces) supplied in Task 7.
- Produces:
  - `bool TryEnsureAsrModel()` — evaluates `_settingsProvider().AsrModelName`
    against the loaded model and applies the decision under `_startGate`;
    returns true iff `_asr` is non-null (ready to transcribe) afterward.
  - The loaded model name is exposed to the transcribe seams via
    `_asrSwap.LoadedModelName`.
  - New transcriber-factory delegate shape (consumed from Task 7):
    `Func<ParakeetSession, string /*loadedModelName*/, AppSettings, Action<string>, ITranscriber>`.

- [ ] **Step 1: Replace the frozen fields with a resolver + swap state**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

Replace the field declaration at line 30:
```csharp
    private readonly string _modelDir;
```
with:
```csharp
    private readonly Func<string, string> _resolveModelDir;
    private readonly Winpepper.Core.Asr.AsrModelSwapState _asrSwap = new();
```

Delete the dead ASR-name snapshot field at line 48:
```csharp
    private readonly string _asrModelName;
```
(Leave `_cleanupModelName` at line 49 untouched.)

Change the transcriber-factory field at line 57 from:
```csharp
    private readonly Func<Winpepper.Asr.ParakeetSession, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> _buildTranscriber;
```
to:
```csharp
    private readonly Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> _buildTranscriber;
```

- [ ] **Step 2: Update the constructor signature and assignments**

In the constructor parameter list (lines 64–86), replace the `string modelDir`
parameter (line 71) with:
```csharp
        Func<string, string> resolveModelDir,
```
and remove the `string asrModelName,` parameter (line 73).

Change the transcriber-factory parameter (line 78) from:
```csharp
        Func<Winpepper.Asr.ParakeetSession, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> transcriberFactory,
```
to:
```csharp
        Func<Winpepper.Asr.ParakeetSession, string, AppSettings, Action<string>, Winpepper.Asr.Transcription.ITranscriber> transcriberFactory,
```

In the constructor body, replace the assignment `_modelDir = modelDir;`
(line 101) with:
```csharp
        _resolveModelDir = resolveModelDir;
```
and delete the assignment `_asrModelName = asrModelName;` (line 103).
Leave `_buildTranscriber = transcriberFactory;` (line 112) as-is (the field type
now matches the new 4-arg delegate).

- [ ] **Step 3: Add `TryEnsureAsrModel()` and route `TryStartCore` through it**

Add this method to the class (place it directly above `TryStartCore` at line 161):

```csharp
    /// <summary>
    /// Ensure the local ASR session matches the currently-selected model. Runs
    /// under <see cref="_startGate"/> on the event-loop thread, so it can never
    /// race an in-flight transcription. Loads the first session, swaps to a newly
    /// selected model (disposing the old one), or keeps the current session when
    /// the selection is unchanged or the desired model is not yet installed.
    /// On load failure the previous working session is kept and the error is
    /// reported. Returns true iff a usable session is loaded afterward.
    /// </summary>
    private bool TryEnsureAsrModel()
    {
        lock (_startGate)
        {
            var desired = _settingsProvider().AsrModelName;
            var desiredDir = _resolveModelDir(desired);
            var present = ParakeetSession.ModelFilesPresent(desiredDir);
            var action = _asrSwap.Plan(desired, present);

            switch (action)
            {
                case Winpepper.Core.Asr.AsrSwapAction.KeepCurrent:
                    return _asr is not null;

                case Winpepper.Core.Asr.AsrSwapAction.CannotStart:
                    _log.LogWarning(
                        "ASR model files missing in {ModelDir}; pipeline disabled until models are downloaded",
                        desiredDir);
                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                        new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                        Guid.Empty);
                    return false;

                case Winpepper.Core.Asr.AsrSwapAction.Load:
                case Winpepper.Core.Asr.AsrSwapAction.Swap:
                    try
                    {
                        var previousModel = _asrSwap.LoadedModelName;
                        var fresh = new ParakeetSession(desiredDir);
                        var old = _asr;
                        _asr = fresh;
                        _asrSwap.CommitLoad(desired);
                        old?.Dispose();
                        _log.LogInformation(
                            "ASR model loaded (swap #{Generation}): {Previous} -> {Model}",
                            _asrSwap.Generation, previousModel ?? "(none)", desired);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex,
                            "Failed to load ASR model {Model} from {ModelDir}; keeping previous session",
                            desired, desiredDir);
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
                        return _asr is not null; // keep-old-on-failure
                    }

                default:
                    return _asr is not null;
            }
        }
    }
```

Then, inside `TryStartCore` (lines 161–223), replace the entire `_asr`-creation
block (lines 166–186, from `if (_asr is null)` through its closing `}`) with a
single guarded call:

```csharp
            if (!TryEnsureAsrModel())
                return false;
```

Leave the rest of `TryStartCore` (warm-recorder setup, `IsRunning = true;`,
`return true;`) and its `lock (_startGate)` unchanged. Because `TryStartCore`
still returns early on `if (IsRunning) return true;` (line 165), the
download-completion `Pipeline.TryStart()` path (ModelsPage line 251) continues to
no-op once running — swaps happen only at the dictation seam (Step 4), never from
the UI thread.

- [ ] **Step 4: Apply the swap at both dictation transcribe seams and pass the live name**

Hold-up path — at line 331 the code is:
```csharp
                var settingsNow = _settingsProvider();
                var transcriber = _buildTranscriber(_asr!, settingsNow, notice => ...);
```
Replace those two lines with (ensure the model is current first, then thread the
live loaded name into the factory's new second argument):
```csharp
                if (!TryEnsureAsrModel())
                {
                    _log.LogWarning("Local ASR unavailable for this dictation; skipping transcription");
                    return;
                }
                var settingsNow = _settingsProvider();
                var transcriber = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow, notice => ...);
```
> Preserve the existing lambda body passed as the `notice => ...` argument
> verbatim — only the delegate now takes the extra `_asrSwap.LoadedModelName!`
> argument in position 2. If the enclosing method is not `void` / cannot use a
> bare `return;` at this point, use the same early-exit control flow already used
> elsewhere in that handler for the "cannot transcribe" case (match the
> surrounding method's return type).

Toggle-stop path — at line 524 the code is:
```csharp
                    var settingsNow2 = _settingsProvider();
                    var transcriber2 = _buildTranscriber(_asr!, settingsNow2, notice => ...);
```
Replace with:
```csharp
                    if (!TryEnsureAsrModel())
                    {
                        _log.LogWarning("Local ASR unavailable for this dictation; skipping transcription");
                        return;
                    }
                    var settingsNow2 = _settingsProvider();
                    var transcriber2 = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow2, notice => ...);
```
> Same preservation note: keep the original `notice => ...` lambda body; only add
> the `_asrSwap.LoadedModelName!` argument. Match the handler's return type for
> the early exit.

(The history-archive lines 472 / 663 already use `producedModelName` /
`producedModelName2` = `transcription.ProviderModelName`; Task 7 makes that value
the live loaded name via `BuildTranscriber`. No change needed here.)

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): apply live ASR model swap at the dictation seam in PipelineHost"
```

> Windows build verification for this file happens in Task 8 Step 1 (the App
> project only compiles on Windows). Do not attempt to build `Winpepper.App` on
> Linux.

---

## Task 6: Update `AppShell` wiring — resolver + live-name transcriber factory (Windows-only)

Feed `PipelineHost` a model-dir resolver instead of the hardcoded
`AppPaths.ParakeetModelDir`, drop the dead `asrModelName` argument, and make
`BuildTranscriber` name the `ParakeetTranscriber` with the live loaded model so
history records the swapped model name.

> **Windows-only:** verified by Windows build + smoke (Task 8).

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs`

**Interfaces:**
- Consumes:
  - `PipelineHost` new ctor shape (Task 5): `Func<string,string> resolveModelDir`
    in place of `AppPaths.ParakeetModelDir`; no `asrModelName` arg; 4-arg
    transcriber factory.
  - `ModelRegistry.InstallDirFor(installRoot, name, ModelKind)` (Task 3).
  - `modelsServices.Registry` and `modelsServices.ModelsRoot` (existing on
    `ModelsServices`).
- Produces: a fully-wired Windows pipeline whose local ASR directory and history
  model name follow the selected model live.

- [ ] **Step 1: Pass a model-dir resolver and drop the dead name arg**

In `src/Winpepper.App/Hosting/AppShell.cs`, the `PipelineHost` construction is at
lines 267–277. Replace the two positional arguments `AppPaths.ParakeetModelDir`
(line 268) and `settings.AsrModelName` (line 269) so the call becomes:

```csharp
        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel,
                                         name => modelsServices.Registry.InstallDirFor(
                                             modelsServices.ModelsRoot, name, Winpepper.Models.ModelKind.Asr),
                                         historyServices.Archiver, cleanupModelName,
                                         clipboardFallback, toasts,
                                         () => store.Load(),
                                         (local, loadedModelName, s, onFallback) => AppShell.BuildTranscriber(
                                             local, loadedModelName, s, onFallback, aaiClient, aaiKeyStore, aaiOptions,
                                             correctionStore, errorBus, factory),
                                         cleanup, correctionStore, windowContext, cleanupOptions,
                                         postPaste: postPaste, focusedCapturer: focusedCapturer,
                                         postPasteLearningEnabled: settings.PostPasteLearningEnabled,
                                         prewarmMicEnabled: settings.PrewarmMicEnabled);
```

> Notes: the model-dir resolver replaces the old `AppPaths.ParakeetModelDir`
> positional; `settings.AsrModelName` is removed (the ctor no longer takes it);
> the lambda passed as `transcriberFactory` now takes `loadedModelName` in
> position 2 and forwards it to `BuildTranscriber`. `AppPaths.ParakeetModelDir`
> stays defined in `AppPaths.cs` (still the default model's directory) — only its
> use here is removed.

- [ ] **Step 2: Thread the live loaded name into `BuildTranscriber`**

Change the `BuildTranscriber` signature (line 410) to accept the loaded model
name as its second parameter:

```csharp
    public static Winpepper.Asr.Transcription.ITranscriber BuildTranscriber(
        Winpepper.Asr.ParakeetSession local,
        string loadedModelName,
        AppSettings settings,
        Action<string> onFallback,
        Winpepper.Asr.Transcription.IAssemblyAiClient client,
        Winpepper.Asr.Transcription.IAssemblyAiKeyStore keyStore,
        Winpepper.Asr.Transcription.AssemblyAiOptions options,
        Winpepper.Corrections.CorrectionStore? correctionStore,
        Winpepper.Core.Errors.ErrorBus errorBus,
        ILoggerFactory loggerFactory)
    {
```

Then change the local-transcriber construction (currently line 421–422):
```csharp
        var localTranscriber = new Winpepper.Asr.Transcription.ParakeetTranscriber(
            local, Winpepper.Models.ModelRegistry.DefaultAsrName);
```
to use the live loaded name so history records the actually-loaded model:
```csharp
        var localTranscriber = new Winpepper.Asr.Transcription.ParakeetTranscriber(
            local, loadedModelName);
```

(The rest of `BuildTranscriber` — the AssemblyAI branch and `FallbackTranscriber`
— is unchanged. The cloud path still re-reads settings per dictation and is
unaffected.)

- [ ] **Step 3: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.App/Hosting/AppShell.cs
git commit -m "feat(app): wire live model-dir resolver and loaded-name into transcriber"
```

> Windows build verification is Task 8 Step 1.

---

## Task 7: Full non-Windows regression suite

Run the entire pure-managed suite on Linux to prove the pure additions and the
(compile-guarded-out) App references didn't disturb the baseline. The App
project itself is excluded on Linux by its Windows-only TFM.

**Files:**
- None — verification task.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: green full non-Windows suite (~810–822 baseline + 13 new tests),
  evidence for the plan/execute review.

- [ ] **Step 1: Build and run every pure-managed test project**

Run each of the nine test projects with the in-process runner (per AGENTS.md;
`dotnet test` is banned here). For each project: build `-c Release`, then
`dotnet exec` the built dll. Example loop:

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
for proj in Winpepper.Core.Tests Winpepper.Asr.Tests Winpepper.Models.Tests \
            Winpepper.Corrections.Tests Winpepper.Cleanup.Tests Winpepper.Audio.Tests \
            Winpepper.History.Tests Winpepper.Platform.Tests Winpepper.IntegrationTests; do
  echo "==== $proj ===="
  $DOTNET build "tests/$proj/$proj.csproj" -c Release || { echo "BUILD FAIL $proj"; exit 1; }
  dll=$(ls "tests/$proj/bin/Release"/net9.0*/"$proj.dll" 2>/dev/null | head -1)
  $DOTNET exec "$dll" || { echo "TEST FAIL $proj"; exit 1; }
done
echo "ALL NON-WINDOWS SUITES GREEN"
```

Expected: every project prints a passing summary with `0` failed; final line
`ALL NON-WINDOWS SUITES GREEN`. Total ≈ 823–835 (baseline + 9 Core + 4 Models
new). `Winpepper.Platform.Tests` runs its `net9.0` (Linux) TFM only.

> If any Windows-only test class is skipped on the Linux TFM, that is expected —
> confirm 0 failures, not a specific total.

- [ ] **Step 2: Commit (gate marker)**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git commit --allow-empty -m "test: full non-Windows suite green with live ASR swap changes"
```

---

## Task 8: Windows build + smoke checklist (Windows-only verification)

The App-layer wiring (Tasks 5–6) only compiles and runs on Windows (WinUI,
NAudio, DPAPI, DirectML). This task is the authoritative verification for
requirements 1–5's end-to-end behavior. Execute on a Windows host per AGENTS.md.

**Files:**
- None — verification task. May add a short report under `docs/plans/` if the
  repo convention (see prior `*-regression-gate report` commits) calls for one.

**Interfaces:**
- Consumes: Tasks 5–6.
- Produces: a Windows build + manual smoke pass confirming the live swap behavior.

- [ ] **Step 1: Build the whole solution on Windows (Release)**

On the Windows host / VM (see `scripts/winrun`, `scripts/sync-to-vm.sh`):
```powershell
dotnet build Winpepper.sln -c Release
```
Expected: PASS — 0 errors. This is where `PipelineHost.cs` and `AppShell.cs`
compile against their new signatures.

- [ ] **Step 2: Build + run the full 9-project suite on Windows**

Per AGENTS.md, build each `tests/` project `-c Release` and run via
`dotnet exec <built test dll>` (the xUnit v3 in-process runner). Use
`scripts/smoke-windows.ps1` if it already automates this; otherwise loop the nine
projects as in Task 7 Step 1 but with the Windows TFM dlls
(`net9.0-windows10.0.19041.0` where applicable).
Expected: all 9 projects green, 0 failed, including Windows-only TFM tests.

- [ ] **Step 3: Manual smoke — switch model while idle**

1. Launch Winpepper with a downloaded default model; confirm dictation works and
   a history entry records the current model name.
2. On the Models page, select a *different, already-downloaded* local ASR model
   as Active model (this fires `promoteAsr` → persists `AsrModelName`).
3. Do a new dictation.
Expected: the next dictation transcribes using the newly selected model, and its
new history entry's ASR model name is the **new** model (requirements 1, 2, 5).

- [ ] **Step 4: Manual smoke — switch mid-dictation**

1. Start a hold-to-talk dictation and, while it is transcribing, switch the
   Active model on the Models page.
2. Let the current dictation finish, then do another dictation.
Expected: the in-flight dictation completes on the **old** model (its history
entry shows the old name); the **next** dictation uses the new model
(requirement 1, "never mid-transcription").

- [ ] **Step 5: Manual smoke — switch to a not-yet-downloaded model**

1. Select a local ASR model that is **not installed** as Active model.
2. Do a dictation *before* downloading it.
Expected: the pipeline stays alive on the **current** model; dictation still
works on the old model (requirement 3, keep-current-until-verified).
3. Download the model via the Models page download flow; let it complete.
4. Do another dictation.
Expected: this dictation swaps to the newly downloaded model (requirement 3).

- [ ] **Step 6: Manual smoke — failed load keeps old model + toast**

1. With a working model loaded, corrupt/remove one required file of a *different*
   selected model's directory (e.g. delete its `vocab.txt`) so `ModelFilesPresent`
   passes name resolution but `new ParakeetSession(dir)` throws, OR arrange a
   model whose files are present but not loadable.
2. Select that model and do a dictation.
Expected: an ASR error toast appears with the "Open Models tab" deep link; the
dictation still succeeds on the **previous working** model; subsequent dictations
keep working on the old model (requirement 3 "never dead after failed swap",
requirement 4 old session retained).

- [ ] **Step 7: Record the smoke result**

If the repo convention wants a written gate report (matching prior
`docs/sdd/*regression-gate*` / `docs/plans` commits), add a short
`docs/plans/2026-07-23-live-asr-model-swap-smoke.md` capturing build output and
the six smoke outcomes, then commit:
```bash
git add docs/plans/2026-07-23-live-asr-model-swap-smoke.md
git commit -m "docs: record Windows smoke results for live ASR model swap"
```
Otherwise commit an empty marker:
```bash
git commit --allow-empty -m "test: Windows build + smoke checklist passed for live ASR swap"
```

---

## Self-Review

**1. Spec coverage** — walked each requirement:
- Req 1 (immediate, never mid-transcription): pure decider (Tasks 1–2) + swap at
  the dictation seam under `_startGate` on the single event loop (Task 5 Step 4)
  + smoke Tasks 8.3/8.4. Covered.
- Req 2 (derive dir from descriptor, keep AppPaths for default): `InstallDirFor`
  (Task 3) + resolver wiring (Task 6 Step 1); `AppPaths.ParakeetModelDir` left
  defined, only its use removed. Covered.
- Req 3 (verified provisioning, keep-current-until-ready, never dead after
  failure): `Plan` returns `KeepCurrent` when desired absent (Task 2) +
  keep-old-on-failure in `TryEnsureAsrModel` (Task 5 Step 3) + smoke 8.5/8.6.
  Covered.
- Req 4 (dispose old safely): dispose after installing new, under `_startGate` on
  the event-loop thread (Task 5 Step 3) + smoke 8.6. Covered.
- Req 5 (history records new name): live `loadedModelName` threaded through
  `BuildTranscriber` → `ParakeetTranscriber.ModelName` → `ProviderModelName` →
  history `AsrModelName` (Task 6 Step 2, Task 5 Step 4) + smoke 8.3/8.4. Covered.
- Req 6 (cloud path + UI unchanged): no change to the AssemblyAI branch or XAML;
  full-suite regression Task 7 + smoke. Covered.

**1b. No silent deferrals** — the only non-Linux-tested behavior is the
Windows-only App wiring, which cannot have an xUnit project in this repo (App is
Windows-only, no `Winpepper.App.Tests`). It is NOT a stub/mock: the production
`PipelineHost` calls the real, unit-tested `AsrModelSwapState` and real
`ParakeetSession`. Its end-to-end outcome is proven by the mandatory Windows
build + six-point smoke checklist (Task 8), not deferred to "future work". No
requirement is parked in "known limitations". The UNRESOLVED COVERAGE GAPS
section is intentionally empty.

**2. Placeholder scan** — no "TBD/TODO/handle edge cases/similar to Task N".
Every code step shows complete code. The two transcribe-seam edits reference the
existing `notice => ...` lambda body by preservation instruction (the body is
long, unrelated, and must be kept verbatim) rather than reproducing it — this is
a "keep existing code" instruction, not a placeholder for new behavior.

**3. Type consistency** — verified names/signatures across tasks:
- `AsrSwapAction { KeepCurrent, Load, Swap, CannotStart }`, `AsrModelSwapState`,
  `.Plan(string, bool)`, `.CommitLoad(string)`, `.LoadedModelName`,
  `.Generation` — identical in Tasks 1, 2, 5.
- `ModelRegistry.InstallDirFor(string installRoot, string? requestedName,
  ModelKind kind)` — identical in Tasks 3 and 6.
- Transcriber factory delegate
  `Func<ParakeetSession, string, AppSettings, Action<string>, ITranscriber>` and
  `BuildTranscriber(local, loadedModelName, settings, onFallback, ...)` — matched
  between Task 5 (field/ctor/call) and Task 6 (call site + method signature).
- `ParakeetSession.ModelFilesPresent(string) : bool`, `new ParakeetSession(string)`,
  `Dispose()`, and `ParakeetTranscriber(ParakeetSession, string)` — used exactly
  as they exist in the codebase.
