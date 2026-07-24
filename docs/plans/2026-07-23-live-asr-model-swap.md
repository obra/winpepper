# Live ASR Model Swap Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make selecting a different LOCAL Parakeet ASR model (on the Models
page or the History-detail promote path) take effect immediately for the next
dictation — no app restart — while never swapping mid-transcription: a dictation
whose recording is already complete transcribes on the model loaded at
transcribe time (so a promote made mid-recording applies to that clip).

**Architecture:** The transcription pipeline runs on a single serialized event
loop (`PipelineHost.RunAsync` → `await foreach` + inline `await HandleHotkey`),
so there is never a concurrent transcription. The UI promote callbacks already
persist `AppSettings.AsrModelName` — but the settings-file round-trip is NOT
the cross-thread transport for "effective immediately": on Windows, an atomic
`Save` (`File.Move(tmp, path, overwrite: true)` = `MoveFileEx(REPLACE_EXISTING)`)
can FAIL against the event loop's concurrently open `Load` handle, silently
dropping the promote, and the boot-snapshot `DebouncedSettingsWriter` can
revert a direct save (lost update). So the desired model name travels through a
**thread-safe in-memory slot** (`AsrModelSelectionSlot` in `Winpepper.Core`):
promotes publish the raw selected name to the slot (and still persist it for
durability across restarts, routed through the single `SettingsWriter`
authority so a stale debounced flush cannot revert it), and the dictation seam
reads the slot — never `store.Load()` — for the model name. We add a **pure,
unit-tested decision component** (`AsrModelSwapState` in `Winpepper.Core`)
that, given the currently-loaded model, the desired model, and whether the
desired model is **verified-ready**, decides whether to keep the current
session, load the first session, or swap. The seam **resolves first**: the raw
slot value is mapped
to a canonical descriptor name via `ModelRegistry.ResolveOrDefault` before it
ever reaches the decider (unknown/null/`""` fall back to the default descriptor,
so a bad settings value can never record a model that didn't run or cause
spurious swaps of identical files). Readiness is **descriptor-level verified
provisioning** — per-file size + SHA-256 via
`ModelProvisioningCoordinator.VerifyReadyAsync(descriptor)`, cached per
selection change — never a bare `File.Exists`: the app's existing startup
invariant ("a merely loadable stale model must not enter PipelineHost",
`AppShell.cs:355-357`) is preserved at the swap seam. The Windows-only
`PipelineHost` calls this decider **at the transcribe seam of each dictation** —
race-free not by thread-affinity or a lock spanning transcription, but because
the run loop is serialized and `TryStartCore`'s `if (IsRunning) return true;`
early-return keeps UI-triggered starts from re-entering; `_startGate` is taken
only around session mutation (create/replace/dispose). Because the seam sits
after `StopSession()`, a promote made mid-recording applies to that clip's
transcription; the invariant is *never swap mid-transcription*. The seam is
**provider-aware**: when `AsrProvider == "assemblyai"`, a failed local swap
never skips or aborts the cloud dictation — the old local session is kept for
`FallbackTranscriber` and the local error surface is softened. On a successful
load the old `ParakeetSession` is disposed under `_startGate`; on load failure
the previous working session is kept and an error is reported (toast with "Open
Models tab" deep link). The model directory is derived from the selected
descriptor via the registry instead of the hardcoded
`AppPaths.ParakeetModelDir`, the live loaded model name is threaded into the
transcriber so new history entries record the swapped model name, and
`ModelsServices`/`ModelsPage` gain live-model verification so the Models page
"Installed" state follows the selected model instead of the boot snapshot. A
second local ASR descriptor is added to the catalog so the Swap branch is
actually reachable in production.

**Tech Stack:** C# / .NET 9, WinUI 3 (App layer, Windows-only), ONNX Runtime
(Parakeet), xUnit v3 + Shouldly for tests.

## Global Constraints

- Main tip: `596dec0`. Do NOT touch the keyboard hook or `packaging/`.
- Pure decision logic (when to apply the swap, keep-old-on-failure, generation
  tracking) MUST live in `Winpepper.Core` and be unit-tested on Linux. The
  `Winpepper.App` project is Windows-only (`net9.0-windows10.0.19041.0`) and has
  NO xUnit test project — its wiring is verified by Windows build + the Windows
  smoke checklist (Task 10), not by Linux tests.
- Pure-managed test projects run on Linux via the xUnit v3 in-process runner:
  build `-c Release`, then `dotnet exec <built test dll>`. Do NOT use
  `dotnet test` (VSTest crashes on this host). The repo-local SDK lives at
  `/home/dan/code/winpepper/.dotnet` (`.dotnet` is gitignored).
- Test framework: xUnit v3 (`using Xunit;`), Shouldly assertions
  (`using Shouldly;`), `[Fact]` methods named `Method_Scenario_Expected`,
  namespace matches `<Assembly>.Tests`. Package versions are centralized
  (`Directory.Packages.props`) — never add inline `Version="..."`.
- TDD red steps must assert on the runner summary (`Failed: N` with N>0, or a
  build error), NOT on exit code alone — `-namespace <ns>` matching zero tests
  exits 0 with `Total: 0`.
- Baseline to preserve green: full non-Windows suite (~810–822 tests; Core 278,
  Models 67, Asr 94, Platform 209 Linux TFM — Core 278 and Models 67 confirmed
  green on this host). New tests add to these counts.
- The Swap branch is only reachable in production once the registry exposes >=2
  local ASR models (Task 4 adds the second descriptor); without it a live swap
  cannot be smoke-tested end to end.
- Never leave the pipeline dead after a failed swap: on load failure, keep the
  previous working `ParakeetSession` if one exists and report via `ErrorBus`
  with `ErrorStage.Asr`. A transcribe-seam early-exit must NEVER bare-`return`
  out of the handler: it bypasses the `RunAsync` catch that applies
  `SessionEvent.Failed` and leaves the session engine stuck non-Idle, killing
  all later dictations — the exit must drive the state machine to its terminal
  state first (Task 7 Step 4).
- Dispose `ParakeetSession` safely: ALL disposal — the seam's swap AND
  `PipelineHost.Dispose()` — happens under `_startGate`, and
  `ParakeetSession.Dispose` is idempotent (`_disposed` guard, Task 7 Step 5).
- Never swap mid-transcription: the swap seam runs at the transcribe seam of a
  dictation on the serialized run loop (`await foreach` + inline
  `await HandleHotkey`) plus the `if (IsRunning) return true;` early-return —
  race-freedom comes from loop serialization, NOT thread-affinity and NOT
  `_startGate` spanning transcription (`_startGate` guards only session
  mutation). Because the seam is after `StopSession()`, a promote made
  mid-recording applies to that clip's transcription.

---

## UNRESOLVED COVERAGE GAPS

None. Every spec requirement maps to a task below. The Windows-only wiring
(Tasks 7–8) cannot be covered by Linux xUnit tests because `Winpepper.App` is a
Windows-only project with no test project in the repo; those requirements are
covered by exact line-level changes plus the mandatory Windows smoke checklist
in Task 10. The core decision behavior each of those requirements depends on IS
unit-tested in pure code (Tasks 1–5).

---

## Requirement → Coverage Map

| Spec requirement | Observable production outcome | Covered by |
|---|---|---|
| 1. Local model change effective next dictation, never mid-transcription | Next dictation after a promote uses the newly selected model — carried by the in-memory `AsrModelSelectionSlot` (promotes publish, the seam reads), NOT the settings-file round-trip, whose Windows replace-vs-open-handle race can silently drop a promote and whose debounced writer can revert it; a swap never happens mid-transcription. The seam is after `StopSession()`, so a dictation whose recording is already complete transcribes on the model loaded at transcribe time — a promote made mid-RECORDING applies to that clip | Slot Task 5 (unit-tested); pure decider Tasks 1–2; wiring Tasks 7–8 (seam reads slot; promotes publish + single-writer persist); Windows smoke Task 10 (incl. mid-recording-promote and promote-transport steps) |
| 2. Derive model dir from selected descriptor, not hardcoded path | `PipelineHost` loads `ParakeetSession` from `ModelsRoot/<InstallDirRelative>` of the selected model; default resolves to the same path as before | `ModelRegistry.InstallDirFor` Task 3 (unit-tested); wiring Tasks 7–8 |
| 3. Respect verified provisioning; keep current model until download/verify completes; never dead after failed swap | `desiredFilesPresent` is fed by DESCRIPTOR-LEVEL verified readiness (per-file size + SHA-256 via `ModelProvisioningCoordinator.VerifyReadyAsync`, cached per selection change), never bare `File.Exists`; selecting a not-yet-verified model keeps the current session alive; after download+verify the next dictation swaps; failed load keeps old + toast; a seam early-exit drives the engine back to Idle | Pure decider `Plan` returns `KeepCurrent` when desired not verified-ready (Task 2); verified-readiness feed + keep-old-on-failure + terminal-state early-exit in `TryEnsureAsrModel` (Task 7); `ModelsServices.VerifyAsrModelReady` (Task 8); Windows smoke Task 10 |
| 4. Dispose old session after swap, safely | Old `ParakeetSession.Dispose()` called only after the new one is installed, under `_startGate` — at the swap seam AND in `PipelineHost.Dispose()`; `ParakeetSession.Dispose` is idempotent (no double-dispose) | Wiring Task 7 (Steps 4–5); Windows smoke Task 10 |
| 5. History records the NEW model name after a swap | New history entries' `AsrModelName` equals the actually-loaded CANONICAL model name (resolved via `ResolveOrDefault` before `Plan`/`CommitLoad`), not a constructor snapshot, hardcoded default, or raw settings string | Resolve-first contract Tasks 1–2 + Task 7; live-name threading Task 8 (`BuildTranscriber` uses live loaded name); Windows smoke Task 10 |
| 6. Cloud (AssemblyAI) path unaffected; no new UI controls | AssemblyAI still re-reads settings per dictation; the seam is provider-aware — a failed local swap never skips/aborts a cloud dictation (old local session kept for `FallbackTranscriber`, local error surface softened); Models page gains no new controls | Provider-aware seam Task 7 Step 4; no change to the AssemblyAI request/fallback code or XAML; full-suite regression Task 9 + Windows smoke Task 10 |

---

## File Structure

**Created:**
- `src/Winpepper.Core/Asr/AsrSwapAction.cs` — enum of swap decisions (pure).
- `src/Winpepper.Core/Asr/AsrModelSwapState.cs` — pure decision state machine.
- `src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs` — thread-safe in-memory
  desired-ASR-model transport (promotes publish, the seam reads; settings.json
  remains the durability mechanism only).
- `tests/Winpepper.Core.Tests/Asr/AsrModelSwapStateTests.cs` — unit tests.
- `tests/Winpepper.Core.Tests/Settings/AsrModelSelectionSlotTests.cs` — unit tests.
- `tests/Winpepper.Models.Tests/ModelRegistryInstallDirTests.cs` — unit tests.
- `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs` — unit tests
  (>=2 local ASR descriptors; local names never match the cloud prefix).
- `scripts/download-parakeet-v2.ps1` — provisions the second local ASR model on
  the Windows VM so the Task 10 smoke can install >=2 models.

**Modified:**
- `src/Winpepper.Models/ModelRegistry.cs` — add `InstallDirFor(...)`; add a
  second `ModelKind.Asr` descriptor (`parakeet-tdt-0.6b-v2`) so the Swap branch
  is reachable in production.
- `src/Winpepper.Core/Settings/SettingsStore.cs` — harden `Load` against a
  transient `IOException` (retry + last-known-good fallback) so the settings
  read/replace race can never fail a dictation.
- `scripts/verify-model-hashes.ps1` — add the second model's file URLs so it
  prints the `Sha256`/`SizeBytes` literals for the new descriptor.
- `src/Winpepper.App/Hosting/PipelineHost.cs` — replace `_modelDir`/`_asrModelName`
  snapshots with a resolver + `AsrModelSwapState`; add `TryEnsureAsrModel()`
  fed by the resolved canonical name and descriptor-level verified readiness;
  call it in `TryStartCore` and (provider-aware) at both dictation transcribe
  seams with a terminal-state early-exit; thread the live loaded name into the
  transcriber factory; take `_startGate` around `_asr?.Dispose()` in
  `Dispose()`. (Windows-only)
- `src/Winpepper.Asr/ParakeetSession.cs` — make `Dispose()` idempotent
  (`_disposed` guard) so swap-dispose and host-dispose can never double-dispose.
- `src/Winpepper.App/Hosting/AppShell.cs` — pass a model-dir resolver, a
  raw-name→canonical-name resolver, and a verified-readiness check instead of
  `AppPaths.ParakeetModelDir`; new transcriber-factory signature;
  `BuildTranscriber` uses the live loaded name for `ParakeetTranscriber`.
  (Windows-only)
- `src/Winpepper.App/Services/ModelsServices.cs` — expose
  `VerifyAsrModelReady(name)`: per-descriptor live verification (resolved
  per-name, NOT the boot-frozen `AsrDescriptor`), cached per selection change.
  (Windows-only)
- `src/Winpepper.App/Views/ModelsPage.xaml.cs` — verify-on-navigate and the
  ASR "Installed" label follow the LIVE selected model instead of the boot
  descriptor, and refresh after a promote. (Windows-only)

> Note: `AppPaths.cs` may need a per-model dir helper if any remaining caller
> needs a model directory outside the registry resolver; `AppPaths.ParakeetModelDir`
> itself stays defined (still the default model's directory).

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
  - **Caller INPUT CONTRACT (documented here, enforced at the seam in Task 7):**
    `desiredModelName` and `modelName` are RESOLVED canonical descriptor names —
    the caller resolves the raw selected name (read from the in-memory
    `AsrModelSelectionSlot`, Task 5) via
    `ModelRegistry.ResolveOrDefault(raw, ModelKind.Asr).Name` BEFORE calling
    `Plan(...)`/`CommitLoad(...)`, never passing the raw value through.
    `ResolveOrDefault` maps unknown/null/`""` to the default descriptor, so a bad
    settings value resolves to the default name and yields `KeepCurrent` (no
    spurious Swap) once the default is loaded — and `CommitLoad` can never record
    a model that never ran. `desiredFilesPresent` is descriptor-level VERIFIED
    readiness (per-file size + SHA-256), not bare file existence.

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
/// Caller contract: model names passed to <see cref="Plan"/> and
/// <see cref="CommitLoad"/> are RESOLVED canonical descriptor names (the host
/// resolves the raw settings value via ModelRegistry.ResolveOrDefault first),
/// and the readiness flag is descriptor-level verified provisioning, not bare
/// file existence.
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
     -namespace Winpepper.Core.Tests.Asr
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
  `CommitLoad` semantics — relied on by `PipelineHost.TryEnsureAsrModel` (Task 7).
  Full `Plan` truth table ("present" = descriptor-level verified-ready):
  - `LoadedModelName == null` & present → `Load`
  - `LoadedModelName == null` & absent → `CannotStart`
  - loaded & `desired == LoadedModelName` → `KeepCurrent`
  - loaded & `desired != LoadedModelName` & present → `Swap`
  - loaded & `desired != LoadedModelName` & absent → `KeepCurrent`
- **Caller INPUT CONTRACT (same as Task 1, enforced in Task 7):** the names fed
  to `Plan`/`CommitLoad` are RESOLVED canonical descriptor names. Because
  `ModelRegistry.ResolveOrDefault` maps unknown/null/`""` to the default
  descriptor, a hand-edited settings.json or the History-detail promote that
  persists `""` resolves to the DEFAULT name — which equals `LoadedModelName`
  once the default is loaded, so the truth table's `KeepCurrent` row applies and
  no spurious Swap of identical files can occur. (No decider test is needed for
  this: it is exactly the `Plan_SameModelLoaded_ReturnsKeepCurrent` case with the
  resolved name; the resolution itself is already covered by
  `ModelRegistryTests.ResolveOrDefault_UnknownAsrName_UsesCatalogDefault`.)
- Related pre-existing bug (surfaced, scoped OUT unless trivial): the
  History-detail promote buttons are enabled with no selection and persist `""`
  unvalidated (`HistoryDetailPage.xaml.cs:64-68`). Recommend a guard at that
  promote boundary; the resolve-first contract makes the swap seam safe against
  it either way.

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
     -namespace Winpepper.Core.Tests.Asr
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
     -namespace Winpepper.Core.Tests.Asr
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
    Consumed by `AppShell` (Task 8) to build the resolver passed to `PipelineHost`.

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
     -class Winpepper.Models.Tests.ModelRegistryInstallDirTests
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

## Task 4: Add a second local ASR descriptor to the catalog (enables Swap)

The registry currently has exactly ONE local ASR descriptor
(`parakeet-tdt-0.6b-v3`), so the decider's Swap branch is unreachable in
production and the Windows smoke steps for switching models (Task 10 Steps
4–8) cannot run. Add a second `ModelKind.Asr` descriptor plus a provisioning
script for the Windows VM.

**Compatibility contract (surfaced assumption S1):** the new descriptor MUST be
a parakeet-rs TDT export compatible with `ParakeetSession` — shared
`ParakeetTdtV3` mel frontend, encoder I/O `audio_signal`/`length`, decoder
`[2,1,640]` LSTM state, TDT duration head, blank-last `vocab.txt` — not merely
a filename match. Candidate: `parakeet-tdt-0.6b-v2` from the same
publisher/converter as v3 (`istupakov/parakeet-tdt-0.6b-v2-onnx`). The layout
MUST be VERIFIED against that contract (inspect the repo's ONNX I/O metadata /
load it once on the Windows host) BEFORE the hashes are added; if v2 does not
satisfy the contract, pick another export from the same converter that does.

**Files:**
- Modify: `src/Winpepper.Models/ModelRegistry.cs`
- Modify: `scripts/verify-model-hashes.ps1`
- Create: `scripts/download-parakeet-v2.ps1`
- Test: `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs`

**Interfaces:**
- Consumes: existing `ModelDescriptor`, `ModelFile`, `ModelKind` (all present).
- Produces:
  - `ModelRegistry.SecondAsrName` (`"parakeet-tdt-0.6b-v2"`) and a second
    `ModelKind.Asr` descriptor with real `InstallDirRelative` and per-file
    `Sha256` + `SizeBytes` — makes `AsrSwapAction.Swap` reachable from the
    Models page / History-detail promote (consumed by Tasks 7–8 at runtime and
    by the Task 10 smoke).
  - `scripts/download-parakeet-v2.ps1` — installs the second model on the VM.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs`:

```csharp
using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryCatalogTests
{
    [Fact]
    public void ByKind_Asr_ExposesAtLeastTwoLocalDescriptors()
    {
        var registry = new ModelRegistry();

        var asr = registry.ByKind(ModelKind.Asr).ToList();

        // The live-swap Swap branch is only reachable with >=2 local ASR models.
        asr.Count.ShouldBeGreaterThanOrEqualTo(2);
        asr.Select(d => d.Name).Distinct().Count().ShouldBe(asr.Count);
        asr.ShouldContain(d => d.Name == ModelRegistry.DefaultAsrName);
        asr.ShouldContain(d => d.Name == ModelRegistry.SecondAsrName);
    }

    [Fact]
    public void ByKind_Asr_LocalNamesNeverMatchCloudProviderPrefix()
    {
        var registry = new ModelRegistry();

        foreach (var d in registry.ByKind(ModelKind.Asr))
        {
            // CloudProvider.IsCloud (CloudProvider.cs:12-14) is an "assemblyai/"
            // prefix check; a local catalog name must never satisfy it, or the
            // history pipeline would treat a local dictation as cloud.
            d.Name.ShouldNotStartWith("assemblyai/");
        }
    }
}
```

(The cloud check replicates `CloudProvider.IsCloud`'s prefix contract rather
than referencing `Winpepper.Asr` — `Winpepper.Models.Tests` has no dependency
on the Asr project.)

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release
```
Expected: FAIL — build error "'ModelRegistry' does not contain a definition for
'SecondAsrName'". (Assert on the build error / `Failed: N>0` summary, never on
exit code alone.)

- [ ] **Step 3: Verify the model layout and compute the hashes**

First verify the S1 compatibility contract for
`istupakov/parakeet-tdt-0.6b-v2-onnx` (same converter as v3): the repo must
ship `encoder-model.int8.onnx` (inputs `audio_signal`/`length`),
`decoder_joint-model.int8.onnx` (LSTM state `[2,1,640]`, TDT duration head),
and a blank-last `vocab.txt`. Then extend `scripts/verify-model-hashes.ps1` by
adding the three v2 entries to its `$files` array (same shape as the existing
v3 entries, base URL
`https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main`) and
run it — it downloads each file and PRINTS the `Sha256 = "..."` and
`SizeBytes = ...` literals to paste into the registry:
```bash
pwsh scripts/verify-model-hashes.ps1
```

- [ ] **Step 4: Write the implementation**

In `src/Winpepper.Models/ModelRegistry.cs`, add next to `DefaultAsrName`:

```csharp
    public const string SecondAsrName = "parakeet-tdt-0.6b-v2";
```

and add a second ASR descriptor to the `_all` list (directly after the
`DefaultAsrName` descriptor), filling each `Sha256`/`SizeBytes` with the exact
literals printed by Step 3 — the task is NOT complete while any of those
fields still holds a from-the-script comment instead of a literal:

```csharp
            new ModelDescriptor
            {
                Name = SecondAsrName,
                Kind = ModelKind.Asr,
                DisplayName = "Parakeet TDT v2 (0.6B, int8 ONNX, English)",
                InstallDirRelative = "parakeet-tdt-0.6b-v2",
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "encoder-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/encoder-model.int8.onnx",
                        Sha256 = /* literal printed by Step 3 */,
                        SizeBytes = /* literal printed by Step 3 */,
                    },
                    new ModelFile
                    {
                        RelativePath = "decoder_joint-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/decoder_joint-model.int8.onnx",
                        Sha256 = /* literal printed by Step 3 */,
                        SizeBytes = /* literal printed by Step 3 */,
                    },
                    new ModelFile
                    {
                        RelativePath = "vocab.txt",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/vocab.txt",
                        Sha256 = /* literal printed by Step 3 */,
                        SizeBytes = /* literal printed by Step 3 */,
                    },
                },
            },
```

Create `scripts/download-parakeet-v2.ps1` (mirrors `download-parakeet.ps1` so
the Task 10 smoke can install a second model on the VM):

```powershell
# Run: ./scripts/winssh < scripts/download-parakeet-v2.ps1
$dest = "$env:LOCALAPPDATA\winpepper\models\parakeet-tdt-0.6b-v2"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$base = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main"
$files = @("encoder-model.int8.onnx", "decoder_joint-model.int8.onnx", "vocab.txt")
foreach ($f in $files) {
    $out = Join-Path $dest $f
    if (Test-Path $out) { Write-Host "skip $f (exists)"; continue }
    Write-Host "Downloading $f..."
    Invoke-WebRequest -Uri "$base/$f" -OutFile $out
}
Write-Host "Models in $dest"
Get-ChildItem $dest | Format-Table Name, Length
```

- [ ] **Step 5: Run test to verify it passes**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll \
     -class Winpepper.Models.Tests.ModelRegistryCatalogTests
```
Expected: PASS — 2 tests passed. Also rerun the full Models suite (no filter)
to confirm the existing `ModelRegistryTests` still pass with the enlarged
catalog (`ResolveOrDefault` default behavior is name-keyed, not count-keyed).

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.Models/ModelRegistry.cs \
        scripts/verify-model-hashes.ps1 \
        scripts/download-parakeet-v2.ps1 \
        tests/Winpepper.Models.Tests/ModelRegistryCatalogTests.cs
git commit -m "feat(models): add second local ASR descriptor so live swap is reachable"
```

---

## Task 5: In-memory desired-ASR-model slot + `SettingsStore.Load` hardening

The settings-file round-trip is NOT a safe cross-thread transport for "effective
immediately": promotes call `SettingsStore.Save`, whose `AtomicFile.WriteAllText`
ends in `File.Move(tmp, path, overwrite: true)` = `MoveFileEx(REPLACE_EXISTING)`
— on Windows, replacing settings.json while the event-loop's `store.Load()` has
it open FAILS the Move, the promote's `Save` throws in the (uncaught) UI
callback, and the new `AsrModelName` is silently not persisted. Additionally
(S4) `DebouncedSettingsWriter` seeds `_pending` from a boot `store.Load()` and
never re-reads disk, so a later debounced flush from another settings control
can REVERT a direct-`Save` promote (two-writer lost update). And
`SettingsStore.Load` catches only `JsonException`, so a transient read
`IOException` from that same race would propagate into `HandleHotkey` and fail
the whole dictation.

This task adds the pure pieces of the fix: (a) a single thread-safe in-memory
slot that carries the desired ASR model name from the UI promotes to the
dictation seam (the file stays the DURABILITY mechanism, not the transport);
(b) a hardened `SettingsStore.Load` that can never throw a transient
`IOException` into a dictation. The App-side wiring (publish on promote, read
at the seam, single-writer persistence) is Tasks 7–8.

The slot holds the RAW selected name; the seam still resolves it via
`ResolveOrDefault(...).Name` before `Plan`/`CommitLoad` (the Task 1/2
resolve-first contract is unchanged).

**Files:**
- Create: `src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs`
- Modify: `src/Winpepper.Core/Settings/SettingsStore.cs`
- Test: `tests/Winpepper.Core.Tests/Settings/AsrModelSelectionSlotTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `sealed class AsrModelSelectionSlot` in `Winpepper.Core.Settings` with
    `void Publish(string? modelName)` and `string? Read()` — a `volatile`
    write/read slot safe to publish from the UI thread and read from the
    pipeline loop without locks. Consumed by `PipelineHost` (Task 7, seam read)
    and the promote callbacks via `AppShell` (Task 8).
  - `SettingsStore.Load` that retries a transient `IOException` briefly and
    then falls back to the last successfully loaded snapshot (or defaults)
    instead of throwing — `Load` is still called at the seam for the rest of
    the settings (provider, options), just no longer for the model name.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Settings/AsrModelSelectionSlotTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class AsrModelSelectionSlotTests
{
    [Fact]
    public void Read_BeforeAnyPublish_ReturnsNull()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Read().ShouldBeNull();
    }

    [Fact]
    public void Read_AfterPublish_ReturnsPublishedName()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Publish("parakeet-tdt-0.6b-v2");

        slot.Read().ShouldBe("parakeet-tdt-0.6b-v2");
    }

    [Fact]
    public void Publish_LatestWriteWins()
    {
        var slot = new AsrModelSelectionSlot();

        slot.Publish("model-a");
        slot.Publish("model-b");

        slot.Read().ShouldBe("model-b");
    }

    [Fact]
    public void Publish_FromAnotherThread_IsVisibleToReader()
    {
        var slot = new AsrModelSelectionSlot();

        var publisher = new Thread(() => slot.Publish("model-a"));
        publisher.Start();
        publisher.Join();

        slot.Read().ShouldBe("model-a");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```
Expected: FAIL — build error "The type or namespace name 'AsrModelSelectionSlot'
could not be found". (Assert on the build error / `Failed: N>0` summary, never
on exit code alone.)

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs`:

```csharp
namespace Winpepper.Core.Settings;

/// <summary>
/// Thread-safe in-memory source of truth for the DESIRED local ASR model name.
/// UI promote callbacks <see cref="Publish"/> the newly selected RAW name (in
/// addition to persisting it to settings.json for durability across restarts);
/// the pipeline's dictation seam <see cref="Read"/>s it. This is the
/// cross-thread transport for "effective immediately" — the settings-file
/// round-trip is NOT: on Windows an atomic replace of settings.json can fail
/// against a concurrently open read handle, silently dropping the promote.
/// A volatile reference is sufficient: single word-sized publication,
/// last-write-wins, no compound state.
/// </summary>
public sealed class AsrModelSelectionSlot
{
    private volatile string? _desired;

    /// <summary>Publish the newly selected raw model name (UI thread).</summary>
    public void Publish(string? modelName) => _desired = modelName;

    /// <summary>Read the currently desired raw model name (pipeline loop).</summary>
    public string? Read() => _desired;
}
```

- [ ] **Step 4: Harden `SettingsStore.Load` against transient IO failures**

In `src/Winpepper.Core/Settings/SettingsStore.cs`, add a last-known-good field
next to the existing fields:

```csharp
    private AppSettings? _lastGood;
```

and replace the `Load` method (lines 23–43) with:

```csharp
    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                _lastGood = loaded;
                return loaded;
            }
            catch (JsonException ex)
            {
                // A torn/corrupt file (e.g. after an MSI upgrade force-kill) must
                // NOT silently wipe every setting. Preserve it for diagnosis, then
                // fall back to defaults. Keep it simple: no partial salvage.
                BackupCorruptFile(ex);
                return new AppSettings();
            }
            catch (IOException) when (attempt < 2)
            {
                // Transient share/replace race: an atomic Save (MoveFileEx
                // REPLACE_EXISTING) can collide with this open read handle on
                // Windows. Brief retry, then fall back — a Load must never
                // throw into a dictation (HandleHotkey calls it per dictation).
                Thread.Sleep(15);
            }
            catch (IOException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed transiently ({ex.Message}); using last known settings.");
                return _lastGood ?? new AppSettings();
            }
        }
    }
```

(`BackupCorruptFile` and `Save` are unchanged. The transient-`IOException`
branch is not deterministically reproducible on Linux — no mandatory file
locking — so it has no Linux unit test; it is exercised by the existing
Settings suite staying green here plus the concurrent-promote smoke in Task 10
Step 10. This is stated here explicitly, not silently deferred.)

- [ ] **Step 5: Run tests to verify they pass**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
     -namespace Winpepper.Core.Tests.Settings
```
Expected: PASS — the pre-existing Settings tests (16) plus the 4 new slot tests
all pass, 0 failed.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs \
        src/Winpepper.Core/Settings/SettingsStore.cs \
        tests/Winpepper.Core.Tests/Settings/AsrModelSelectionSlotTests.cs
git commit -m "feat(core): in-memory desired-ASR-model slot + resilient settings Load"
```

---

## Task 6: Pure-managed regression gate (Core + Models)

Verify the pure additions (decider, `InstallDirFor`, second catalog descriptor,
desired-model slot, hardened `SettingsStore.Load`)
haven't broken the existing Core/Models suites before touching the Windows-only
App layer.

**Files:**
- None created/modified — this is a verification task.

**Interfaces:**
- Consumes: everything from Tasks 1–5.
- Produces: a green Core + Models run (evidence for the plan review).

- [ ] **Step 1: Build and run Core tests**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```
Expected: PASS — all Core tests pass (278 baseline + 13 new: 9 decider + 4
slot = 291), 0 failed.

- [ ] **Step 2: Build and run Models tests**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll
```
Expected: PASS — all Models tests pass (67 baseline + 4 InstallDir + 2 catalog
= 73), 0 failed.

- [ ] **Step 3: Commit (no-op marker)**

No files changed; record the gate in the branch history with an empty commit:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git commit --allow-empty -m "test: pure Core+Models suites green after swap-decider additions"
```

---

## Task 7: Wire the swap decider + resolver into `PipelineHost` (Windows-only)

Replace the frozen `_modelDir` and dead `_asrModelName` snapshots with a
model-dir resolver, a raw→canonical name resolver, a verified-readiness check,
and an `AsrModelSwapState`; add `TryEnsureAsrModel()` which applies the pure
decision (load/swap/keep, dispose-old-under-`_startGate`, keep-old-on-failure);
make the transcribe seams provider-aware with a terminal-state early-exit; and
make disposal double-dispose-safe.

> **Windows-only:** `Winpepper.App` targets `net9.0-windows10.0.19041.0` and does
> not build on Linux and has no xUnit test project. Verification is the Windows
> `dotnet build -c Release` of `Winpepper.App` (Task 10 Step 2) plus the smoke
> checklist (Task 10 Step 4+). The pure decision logic this wiring calls is
> already unit-tested (Tasks 1–2). Report exact line-level changes in the commit.
> (`ParakeetSession.cs` in Step 5 DOES build on Linux — verify it there.)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `src/Winpepper.Asr/ParakeetSession.cs` (idempotent `Dispose`, Step 5)

**Interfaces:**
- Consumes:
  - `Winpepper.Core.Asr.AsrModelSwapState`, `AsrSwapAction` (Tasks 1–2).
  - `new ParakeetSession(string modelDir)` (existing).
  - A new ctor dependency `Func<string, string> resolveModelDir`
    (canonical name → abs dir), supplied by `AppShell` in Task 8 as
    `name => registry.InstallDirFor(modelsRoot, name, ModelKind.Asr)`.
  - A new ctor dependency `Func<string?> desiredAsrModelName`
    (reads the raw desired model name from the in-memory
    `AsrModelSelectionSlot`, Task 5), supplied by `AppShell` in Task 8 as
    `() => asrSelection.Read()`. The seam reads the SLOT, not
    `_settingsProvider().AsrModelName` — the settings-file round-trip is not a
    safe cross-thread transport (F1: a Windows atomic replace can fail against
    the seam's open read handle, silently dropping a promote).
  - A new ctor dependency `Func<string?, string> resolveAsrModelName`
    (raw slot value → canonical descriptor name), supplied by `AppShell` in
    Task 8 as `raw => registry.ResolveOrDefault(raw, ModelKind.Asr).Name` —
    the seam RESOLVES FIRST so the decider never sees a raw name
    (Task 1/2 caller contract; the slot holds the raw name, resolution happens
    here).
  - A new ctor dependency `Func<string, bool> isAsrModelReady`
    (canonical name → descriptor-level verified readiness), supplied by
    `AppShell` in Task 8 as `name => modelsServices.VerifyAsrModelReady(name)`.
    This is per-file size + SHA-256 via
    `ModelProvisioningCoordinator.VerifyReadyAsync(descriptor)` (per-descriptor;
    it queues behind an in-flight download), CACHED per selection change —
    NEVER a bare `ParakeetSession.ModelFilesPresent`/`File.Exists`, which would
    silently downgrade the verified-provisioning invariant
    (`AppShell.cs:355-357`).
  - A new transcriber-factory signature (see Produces) supplied in Task 8.
- Produces:
  - `bool TryEnsureAsrModel(bool reportErrors = true)` — reads the raw desired
    name from the in-memory slot, resolves it to the canonical name, evaluates
    it against the loaded model with verified readiness,
    and applies the decision under `_startGate`; returns true iff `_asr` is
    non-null (ready to transcribe) afterward. `reportErrors: false` softens the
    error surface for the cloud path (log only, no toast).
  - The loaded model name is exposed to the transcribe seams via
    `_asrSwap.LoadedModelName`.
  - New transcriber-factory delegate shape (consumed from Task 8):
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
    private readonly Func<string?> _desiredAsrModel;
    private readonly Func<string?, string> _resolveAsrModelName;
    private readonly Func<string, bool> _isAsrModelReady;
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
        Func<string?> desiredAsrModelName,
        Func<string?, string> resolveAsrModelName,
        Func<string, bool> isAsrModelReady,
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
        _desiredAsrModel = desiredAsrModelName;
        _resolveAsrModelName = resolveAsrModelName;
        _isAsrModelReady = isAsrModelReady;
```
and delete the assignment `_asrModelName = asrModelName;` (line 103).
Leave `_buildTranscriber = transcriberFactory;` (line 112) as-is (the field type
now matches the new 4-arg delegate).

- [ ] **Step 3: Add `TryEnsureAsrModel()` and route `TryStartCore` through it**

Add this method to the class (place it directly above `TryStartCore` at line 161):

```csharp
    /// <summary>
    /// Ensure the local ASR session matches the currently-selected model.
    /// Called only from the serialized run loop (`await foreach` + inline
    /// `await HandleHotkey`), so it can never race another dictation; it takes
    /// <see cref="_startGate"/> around all session mutation, including disposal
    /// of the old session. Resolves the canonical descriptor name FIRST, feeds
    /// the decider descriptor-level VERIFIED readiness (size + SHA-256, cached
    /// per selection change), loads the first session, swaps to a newly
    /// selected model (disposing the old one), or keeps the current session
    /// when the selection is unchanged or the desired model is not yet
    /// verified-ready. On load failure the previous working session is kept
    /// and, when <paramref name="reportErrors"/> is true, the error is
    /// reported; the cloud path passes false to soften the local error surface.
    /// Returns true iff a usable session is loaded afterward.
    /// </summary>
    private bool TryEnsureAsrModel(bool reportErrors = true)
    {
        lock (_startGate)
        {
            // Read the desired name from the in-memory slot — NOT from
            // _settingsProvider(): the settings-file round-trip is not a safe
            // cross-thread transport (a Windows atomic replace can fail against
            // this loop's open read handle, silently dropping a promote).
            // Then resolve FIRST: unknown/null/"" values fall back to the
            // default descriptor via ModelRegistry.ResolveOrDefault, so the
            // decider only ever sees canonical catalog names. Planning or
            // committing the raw name would record a model that never ran and
            // cause spurious swaps between two unknown names.
            var desired = _resolveAsrModelName(_desiredAsrModel());
            var desiredDir = _resolveModelDir(desired);
            // Descriptor-level verified readiness (per-file size + SHA-256 via
            // ModelProvisioningCoordinator.VerifyReadyAsync, cached per
            // selection change by ModelsServices) — NOT a bare File.Exists.
            // "A merely loadable stale model must not enter PipelineHost."
            var ready = _isAsrModelReady(desired);
            var action = _asrSwap.Plan(desired, ready);

            switch (action)
            {
                case Winpepper.Core.Asr.AsrSwapAction.KeepCurrent:
                    return _asr is not null;

                case Winpepper.Core.Asr.AsrSwapAction.CannotStart:
                    _log.LogWarning(
                        "ASR model {Model} not verified-ready in {ModelDir}; pipeline disabled until models are downloaded",
                        desired, desiredDir);
                    if (reportErrors)
                    {
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                            Guid.Empty);
                    }
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
                        old?.Dispose(); // under _startGate; idempotent (Step 5)
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
                        if (reportErrors)
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

- [ ] **Step 4: Apply the swap at both dictation transcribe seams — provider-aware, with a terminal-state early-exit — and pass the live name**

Both seams sit AFTER `StopSession()` and after
`_engine.Apply(SessionEvent.StopRequested)`. Two hard requirements here:

1. **Never leave the pipeline dead (S2):** a bare `return;` at this point
   bypasses the `RunAsync` catch that applies `SessionEvent.Failed`, and the
   start paths require `State == Idle` — so a single skipped dictation would
   leave the session engine stuck non-Idle and kill ALL later dictations. The
   early-exit must drive the state machine to its terminal state
   (`_engine.Apply(SessionEvent.Failed)` — if this handler drives failures
   through a different terminal event or helper, match that instead; the
   requirement is that the engine returns to a state from which the next
   dictation can start) and the failure must be user-visible (the `ErrorBus`
   report from `TryEnsureAsrModel` provides the toast on the local path).
2. **Cloud path unaffected (req 6):** the seam must be provider-aware — when
   `AsrProvider == "assemblyai"` a failed LOCAL swap must not skip or abort the
   dictation (the cloud path can still transcribe); keep the old local session
   for `FallbackTranscriber` and soften the local-swap error surface
   (`reportErrors: false`). The AssemblyAI request/fallback code itself stays
   unchanged.

Hold-up path — at line 331 the code is:
```csharp
                var settingsNow = _settingsProvider();
                var transcriber = _buildTranscriber(_asr!, settingsNow, notice => ...);
```
Replace those two lines with (ensure the model is current first, then thread the
live loaded name into the factory's new second argument):
```csharp
                var settingsNow = _settingsProvider();
                var cloudSelected = string.Equals(
                    settingsNow.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                // Provider-aware (req 6): a failed LOCAL swap never skips or
                // aborts a CLOUD dictation; soften its error surface.
                var localReady = TryEnsureAsrModel(reportErrors: !cloudSelected);
                if ((!localReady && !cloudSelected) || _asr is null)
                {
                    // Terminal-state early-exit (S2): never bare-return — drive
                    // the engine back so the next dictation can start.
                    _engine.Apply(SessionEvent.Failed);
                    if (cloudSelected && _asr is null)
                    {
                        // Cloud selected but no local session exists at all (the
                        // fallback wrapper needs one): surface this rare case.
                        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                            new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                            Guid.Empty);
                    }
                    _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                    return;
                }
                var transcriber = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow, notice => ...);
```
> Preserve the existing lambda body passed as the `notice => ...` argument
> verbatim — only the delegate now takes the extra `_asrSwap.LoadedModelName!`
> argument in position 2. If the enclosing method is not `void` / cannot use a
> bare `return;` at this point, use the same early-exit control flow already used
> elsewhere in that handler for the "cannot transcribe" case (match the
> surrounding method's return type) — but ALWAYS after applying the terminal
> session event. On the local path the user-visible toast comes from
> `TryEnsureAsrModel`'s own `ErrorBus` report — do not double-report here.

Toggle-stop path — at line 524 the code is:
```csharp
                    var settingsNow2 = _settingsProvider();
                    var transcriber2 = _buildTranscriber(_asr!, settingsNow2, notice => ...);
```
Replace with the same structure (note the `2`-suffixed locals):
```csharp
                    var settingsNow2 = _settingsProvider();
                    var cloudSelected2 = string.Equals(
                        settingsNow2.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase);
                    var localReady2 = TryEnsureAsrModel(reportErrors: !cloudSelected2);
                    if ((!localReady2 && !cloudSelected2) || _asr is null)
                    {
                        _engine.Apply(SessionEvent.Failed);
                        if (cloudSelected2 && _asr is null)
                        {
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                                new InvalidOperationException("Speech model unavailable; dictation aborted. Open the Models tab."),
                                Guid.Empty);
                        }
                        _log.LogWarning("Local ASR unavailable for this dictation; session failed back to Idle");
                        return;
                    }
                    var transcriber2 = _buildTranscriber(_asr!, _asrSwap.LoadedModelName!, settingsNow2, notice => ...);
```
> Same preservation note: keep the original `notice => ...` lambda body; only add
> the `_asrSwap.LoadedModelName!` argument. Match the handler's return type and
> its terminal-event pattern for the early exit.

(The history-archive lines 472 / 663 already use `producedModelName` /
`producedModelName2` = `transcription.ProviderModelName`; Task 8 makes that value
the live loaded name via `BuildTranscriber`. No change needed here.)

- [ ] **Step 5: Make disposal safe — `Dispose()` under `_startGate`, idempotent `ParakeetSession.Dispose`**

`PipelineHost.Dispose()` (lines 708–728) currently disposes `_asr` (line 720)
WITHOUT `_startGate` after only a bounded 2s `_runTask.Wait` — it can race the
seam's swap and double-dispose. In `Dispose()`, replace the bare
`_asr?.Dispose();` with:
```csharp
        lock (_startGate)
        {
            _asr?.Dispose();
            _asr = null;
        }
```
(Requires the `_asr` field to be non-`readonly`, which Step 3's swap already
requires.)

In `src/Winpepper.Asr/ParakeetSession.cs`, make `Dispose()` idempotent so the
seam's `old?.Dispose()` and the host's `Dispose()` can never double-dispose the
underlying ONNX sessions. Replace:
```csharp
    public void Dispose()
    {
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
```
with:
```csharp
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
```
Verify the Asr project still builds on Linux:
```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
/home/dan/code/winpepper/.dotnet/dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -c Release
```
Expected: PASS — 0 errors (the Asr suite reruns in Task 9).

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.App/Hosting/PipelineHost.cs \
        src/Winpepper.Asr/ParakeetSession.cs
git commit -m "feat(app): apply live ASR model swap at the dictation seam in PipelineHost"
```

> Windows build verification for `PipelineHost.cs` happens in Task 10 Step 2 (the
> App project only compiles on Windows). Do not attempt to build `Winpepper.App`
> on Linux.

---

## Task 8: Update `AppShell` wiring + live-model verification in `ModelsServices`/`ModelsPage` (Windows-only)

Feed `PipelineHost` a model-dir resolver, an in-memory desired-model slot read,
a raw→canonical name resolver, and a verified-readiness check instead of the
hardcoded `AppPaths.ParakeetModelDir`; drop the dead `asrModelName` argument;
make `BuildTranscriber` name the `ParakeetTranscriber` with the live loaded
model so history records the swapped model name; make the promote callbacks
publish to the slot and persist through the single `SettingsWriter` authority
(F1/S4); and fix the boot-snapshot staleness: `ModelsServices.AsrDescriptor`
is frozen at construction, so `ModelsPage`'s verify-on-navigate and "Installed"
label would describe a model that is neither selected nor running after a live
swap — expose a per-descriptor live verify on `ModelsServices` and make the
page follow the live selected model.

> **Windows-only:** verified by Windows build + smoke (Task 10).

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs`
- Modify: `src/Winpepper.App/Services/ModelsServices.cs`
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs`
- Modify: `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`

**Interfaces:**
- Consumes:
  - `PipelineHost` new ctor shape (Task 7): `Func<string,string> resolveModelDir`
    in place of `AppPaths.ParakeetModelDir`; new `Func<string?>
    desiredAsrModelName`, `Func<string?,string> resolveAsrModelName`, and
    `Func<string,bool> isAsrModelReady`; no `asrModelName` arg; 4-arg
    transcriber factory.
  - `Winpepper.Core.Settings.AsrModelSelectionSlot` (Task 5).
  - `ModelRegistry.InstallDirFor(installRoot, name, ModelKind)` (Task 3) and
    `ModelRegistry.ResolveOrDefault(name, ModelKind)` (existing).
  - `modelsServices.Registry` and `modelsServices.ModelsRoot` (existing on
    `ModelsServices`); `ModelProvisioningCoordinator.VerifyReadyAsync(descriptor, ct)`
    (existing, private `_coordinator` on `ModelsServices`).
  - The existing `DebouncedSettingsWriter` exposed on the shell
    (`shell.SettingsWriter`, already used by e.g. the provider radios via
    `QueueAndFlushAsync`).
- Produces:
  - a fully-wired Windows pipeline whose local ASR directory, verified
    readiness, and history model name follow the selected model live.
  - `ModelsServices.VerifyAsrModelReady(string canonicalName) : bool` — the
    per-descriptor verified-readiness check consumed by `PipelineHost` (Task 7)
    and by `ModelsPage`.
  - `AppShell.AsrModelSelection` (`AsrModelSelectionSlot`) — the shared slot the
    promote callbacks publish to and the pipeline reads from.

- [ ] **Step 1: Expose per-descriptor live verification on `ModelsServices`**

`ModelsServices.AsrDescriptor` is boot-fixed (`ModelsServices.cs:16,26`), so
verification MUST resolve the descriptor per-name. Note also that
`ModelProvisioningCoordinator.State` is a single global — it must NOT be used
as a per-model signal; only the per-descriptor `VerifyReadyAsync` return is
authoritative. Add to `src/Winpepper.App/Services/ModelsServices.cs`:

```csharp
    private string? _verifiedAsrModelName; // last canonical name that passed descriptor-level verification

    /// <summary>
    /// Descriptor-level verified readiness (per-file size + SHA-256 via
    /// ModelProvisioningCoordinator.VerifyReadyAsync, which queues behind any
    /// in-flight download) for the CANONICAL model name — resolved per-name
    /// because <see cref="AsrDescriptor"/> is frozen at boot. The positive
    /// result is CACHED per selection change: a full ~1.1 GB SHA-256 on every
    /// dictation start is too slow, so we re-verify only when the requested
    /// name differs from the last verified one. A negative result is never
    /// cached (missing files short-circuit cheaply, and the next dictation
    /// should pick up a completed download). Only the per-descriptor
    /// VerifyReadyAsync return is authoritative — the coordinator's global
    /// <see cref="State"/> is not a per-model signal.
    /// </summary>
    public bool VerifyAsrModelReady(string canonicalName)
    {
        if (string.Equals(_verifiedAsrModelName, canonicalName, StringComparison.Ordinal))
            return true;

        var descriptor = Registry.ResolveOrDefault(canonicalName, ModelKind.Asr);
        var ready = _coordinator.VerifyReadyAsync(descriptor, CancellationToken.None)
                                .GetAwaiter().GetResult();
        if (ready) _verifiedAsrModelName = canonicalName;
        return ready;
    }
```

> This method is called from `PipelineHost`'s serialized run loop (threadpool
> continuations), never the UI thread, so the synchronous wait is safe there.
> The name-keyed cache is the "invalidate when `AsrModelName` changes" rule: a
> promote changes the requested name, which misses the cache and forces a fresh
> descriptor-level verification before the swap.

- [ ] **Step 2: Pass the resolvers + readiness check and drop the dead name arg**

In `src/Winpepper.App/Hosting/AppShell.cs`, first create and expose the shared
slot (before the `PipelineHost` construction, after the settings/model-name
reconcile at lines 62–67 so the seed is the reconciled boot value):

```csharp
        var asrSelection = new Winpepper.Core.Settings.AsrModelSelectionSlot();
        asrSelection.Publish(settings.AsrModelName); // seed with the persisted boot value
        AsrModelSelection = asrSelection;
```

and add the public property next to the shell's other exposed services:

```csharp
    /// <summary>
    /// Thread-safe in-memory desired-ASR-model transport: promote callbacks
    /// publish the newly selected raw name here (persistence to settings.json
    /// is durability only), and PipelineHost's dictation seam reads it.
    /// </summary>
    public Winpepper.Core.Settings.AsrModelSelectionSlot AsrModelSelection { get; private set; } = null!;
```

Then, the `PipelineHost` construction is at
lines 267–277. Replace the two positional arguments `AppPaths.ParakeetModelDir`
(line 268) and `settings.AsrModelName` (line 269) so the call becomes:

```csharp
        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel,
                                         name => modelsServices.Registry.InstallDirFor(
                                             modelsServices.ModelsRoot, name, Winpepper.Models.ModelKind.Asr),
                                         () => asrSelection.Read(),
                                         raw => modelsServices.Registry.ResolveOrDefault(
                                             raw, Winpepper.Models.ModelKind.Asr).Name,
                                         name => modelsServices.VerifyAsrModelReady(name),
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
> positional; the next three lambdas are the slot read, the raw→canonical name
> resolver, and the per-descriptor verified-readiness check (Task 7's
> `desiredAsrModelName` / `resolveAsrModelName` / `isAsrModelReady`);
> `settings.AsrModelName` is removed (the ctor no longer
> takes it); the lambda passed as `transcriberFactory` now takes
> `loadedModelName` in position 2 and forwards it to `BuildTranscriber`.
> `AppPaths.ParakeetModelDir` stays defined in `AppPaths.cs` (still the default
> model's directory) — only its use here is removed. (If any remaining caller
> needs a per-model directory outside the registry resolver, add a per-model
> helper to `AppPaths.cs` rather than re-hardcoding.)

- [ ] **Step 3: Thread the live loaded name into `BuildTranscriber`**

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
— is unchanged. The cloud path still re-reads settings per dictation; the
seam-side protection for cloud dictations is Task 7 Step 4.)

- [ ] **Step 4: Make `ModelsPage` follow the LIVE selected model (fix boot-snapshot staleness)**

After a live swap, `ModelsPage.OnNavigatedTo` currently verifies the BOOT
descriptor (`await models.VerifyReadyAsync(_lifetimeCts.Token)` at
`ModelsPage.xaml.cs:59`, which targets the frozen `AsrDescriptor`) and
`UpdateInstalledLabels` (`ModelsPage.xaml.cs:272-278`) reads the coordinator's
single global `State` — so the "Installed" label and any `ErrorBus` reports
(`ModelsPage.xaml.cs:66-71`) would describe a model that is neither selected
nor running. We take the REFRESH option (the label actively contradicting the
UI is not acceptable):

1. In `OnNavigatedTo`, replace the boot-descriptor verification with a
   live-selected-model verification. Replace:
   ```csharp
            await models.VerifyReadyAsync(_lifetimeCts.Token);
            UpdateInstalledLabels();
   ```
   with:
   ```csharp
            var selectedAsr = App.Shell!.ModelsServices.Registry.ResolveOrDefault(
                App.Shell!.SettingsStore.Load().AsrModelName, ModelKind.Asr).Name;
            _asrSelectedVerified = await Task.Run(
                () => App.Shell!.ModelsServices.VerifyAsrModelReady(selectedAsr));
            UpdateInstalledLabels();
   ```
   (`_asrSelectedVerified` is a new `private bool` field on the page. If the
   shell exposes the settings store under a different member name, use that —
   the requirement is: resolve the CURRENTLY persisted `AsrModelName`, verify
   THAT descriptor. Keep the surrounding `try/catch` — the
   `OperationCanceledException` and error-report handlers at
   `ModelsPage.xaml.cs:62-72` stay as they are.)
2. In `UpdateInstalledLabels` (`ModelsPage.xaml.cs:272-278`), base the ASR
   "Installed" state on the live result instead of the global coordinator
   state. Replace:
   ```csharp
        var asrInstalled = App.Shell!.ModelsServices.State.Status ==
            Winpepper.Core.ViewModels.AsrProvisioningStatus.Ready;
   ```
   with:
   ```csharp
        var asrInstalled = _asrSelectedVerified;
   ```
3. Refresh on promote: after the ASR promote/selection commit path that
   persists `AsrModelName` (the `OnAsrChanged` handler at
   `ModelsPage.xaml.cs:75-83` calling `CommitSelection()`), re-run the same
   live verification for the newly selected name (as in item 1) and then
   `UpdateInstalledLabels()`, so the label reflects the newly promoted model
   without renavigating.

- [ ] **Step 5: Publish promotes to the slot + single-writer persistence (F1/S4)**

Both promote callbacks currently persist via a direct `SettingsStore.Save`
(`ModelsPage.xaml.cs:37`, `HistoryDetailPage.xaml.cs:67`) — the exact write
path that (a) can throw uncaught in the UI callback when the atomic replace
collides with the seam's open read handle, and (b) can be REVERTED by a later
flush of the boot-snapshot `DebouncedSettingsWriter` (S4 two-writer lost
update: the writer's `_pending` was seeded from a boot `store.Load()` and
never re-reads disk, so a direct `Save` is invisible to it). Fix both by
making each promote (1) publish to the in-memory slot — the actual
"effective immediately" transport — and (2) persist through the SAME
`shell.SettingsWriter` authority every other settings control already uses,
so the promote lands in the writer's `_pending` and no stale debounced
snapshot can revert it.

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, replace the ASR promote
callback body (lines 35–38):
```csharp
            {
                var cur = settings.Load();
                settings.Save(cur with { AsrModelName = name });
            },
```
with:
```csharp
            {
                var shell = App.Shell!;
                shell.AsrModelSelection.Publish(name); // effective immediately
                _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AsrModelName = name }); // durability
            },
```

In `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`, replace the promote
persistence (lines 66–67):
```csharp
                var s = settings.Load();
                settings.Save(s with { AsrModelName = name });
```
with:
```csharp
                var shell = App.Shell!;
                shell.AsrModelSelection.Publish(name); // effective immediately
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { AsrModelName = name }); // durability
```
> Match the actual surrounding callback shape at those lines — the requirement
> is: publish the raw selected name to `App.Shell!.AsrModelSelection` FIRST,
> then queue persistence through `shell.SettingsWriter` (never a direct
> `SettingsStore.Save`). The CLEANUP promote callbacks may stay as they are —
> they are outside this feature's scope — but if touched, route them through
> the writer too for the same S4 reason. Persistence remains durability-only:
> even if the flush transiently fails, the slot has already made the promote
> effective for the next dictation, and `SettingsStore.Load`'s hardening
> (Task 5 Step 4) keeps any concurrent read from failing a dictation.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git add src/Winpepper.App/Hosting/AppShell.cs \
        src/Winpepper.App/Services/ModelsServices.cs \
        src/Winpepper.App/Views/ModelsPage.xaml.cs \
        src/Winpepper.App/Views/HistoryDetailPage.xaml.cs
git commit -m "feat(app): wire live model resolvers, slot transport, verified readiness, and live Models-page state"
```

> Windows build verification is Task 10 Step 2.

---

## Task 9: Full non-Windows regression suite

Run the entire pure-managed suite on Linux to prove the pure additions and the
(compile-guarded-out) App references didn't disturb the baseline. The App
project itself is excluded on Linux by its Windows-only TFM.

**Files:**
- None — verification task.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: green full non-Windows suite (~810–822 baseline + 19 new tests),
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
`ALL NON-WINDOWS SUITES GREEN`. Total ≈ 829–841 (baseline + 13 Core + 6 Models
new: 4 InstallDir + 2 catalog). `Winpepper.Platform.Tests` runs its `net9.0`
(Linux) TFM only.

> If any Windows-only test class is skipped on the Linux TFM, that is expected —
> confirm 0 failures, not a specific total.

- [ ] **Step 2: Commit (gate marker)**

```bash
cd /home/dan/code/winpepper/.worktrees/live-asr-model-swap
git commit --allow-empty -m "test: full non-Windows suite green with live ASR swap changes"
```

---

## Task 10: Windows build + smoke checklist (Windows-only verification)

The App-layer wiring (Tasks 7–8) only compiles and runs on Windows (WinUI,
NAudio, DPAPI, DirectML). This task is the authoritative verification for
requirements 1–5's end-to-end behavior. Execute on a Windows host per AGENTS.md.

> **Execution-provider scope:** the QEMU VM has no DX12 adapter (virtio VGA),
> so this task verifies the **CPU execution-provider path only** — an
> explicitly blessed configuration (`ParakeetSession.cs:38-40`). DirectML is
> NOT end-to-end verified here; the DML-specific residual risks (device
> poisoning after `DEVICE_REMOVED`, DML alloc behavior) remain open and are
> recorded in Step 11.

**Files:**
- None — verification task. May add a short report under `docs/plans/` if the
  repo convention (see prior `*-regression-gate report` commits) calls for one.

**Interfaces:**
- Consumes: Tasks 7–8 (wiring), Task 4 (second installed model).
- Produces: a Windows build + manual smoke pass confirming the live swap behavior.

- [ ] **Step 1: Precondition gate — verify the Windows VM is actually usable**

The VM was DOWN at plan-validation time; availability must not be left
implicit. Before anything else, verify ALL of:

1. The VM is reachable on port 2222 (`scripts/winssh` connects; if not, boot it
   via `scripts/launch-qemu.sh` and re-check).
2. `dotnet --version` on the VM reports 9.x.
3. **>=2 local ASR models are installed** under
   `%LOCALAPPDATA%\winpepper\models` (run `scripts/winssh <
   scripts/download-parakeet.ps1` and `scripts/winssh <
   scripts/download-parakeet-v2.ps1` from Task 4 if missing).
4. The mic path is live: `scripts/say.sh` plays audio into the VM's mic
   passthrough (see `scripts/setup-audio-host.sh` / AGENTS.md).

Expected: all four checks pass. If the host proves unavailable, STOP and
resolve the environment before proceeding — do not silently downgrade this
task to "Linux-only verification".

- [ ] **Step 2: Build the whole solution on Windows (Release)**

On the Windows host / VM (see `scripts/winrun`, `scripts/sync-to-vm.sh`):
```powershell
dotnet build Winpepper.sln -c Release
```
Expected: PASS — 0 errors. This is where `PipelineHost.cs`, `AppShell.cs`,
`ModelsServices.cs`, and `ModelsPage.xaml.cs` compile against their new
signatures.

- [ ] **Step 3: Build + run the full 9-project suite on Windows**

Per AGENTS.md, build each `tests/` project `-c Release` and run via
`dotnet exec <built test dll>` (the xUnit v3 in-process runner). Use
`scripts/smoke-windows.ps1` if it already automates this; otherwise loop the nine
projects as in Task 9 Step 1 but with the Windows TFM dlls
(`net9.0-windows10.0.19041.0` where applicable).
Expected: all 9 projects green, 0 failed, including Windows-only TFM tests.

- [ ] **Step 4: Manual smoke — switch model while idle**

1. Launch Winpepper with a downloaded default model; confirm dictation works and
   a history entry records the current model name.
2. On the Models page, select a *different, already-downloaded* local ASR model
   as Active model (this fires `promoteAsr` → persists `AsrModelName`).
3. Do a new dictation.
Expected: the next dictation transcribes using the newly selected model, and its
new history entry's ASR model name is the **new** model (requirements 1, 2, 5).

- [ ] **Step 5: Manual smoke — switch mid-TRANSCRIPTION**

1. Start a hold-to-talk dictation and, while it is **transcribing** (after the
   stop beep, before the paste), switch the Active model on the Models page.
2. Let the current dictation finish, then do another dictation.
Expected: the dictation already transcribing completes on the **old** model
(its history entry shows the old name — the seam for it already ran); the
**next** dictation uses the new model (requirement 1, "never
mid-transcription").

- [ ] **Step 6: Manual smoke — promote mid-RECORDING**

1. Start a hold-to-talk dictation and, while still **recording** (before
   releasing the hotkey), switch the Active model on the Models page.
2. Release the hotkey and let the dictation finish.
Expected (documented behavior, per the reworded req-1 invariant): the swap seam
runs AFTER `StopSession()`, so THIS clip transcribes on the **new** model —
its history entry shows the new name, and the new model's load latency is paid
between the stop beep and the transcript. Never a mid-transcription swap; the
pipeline stays alive throughout.

- [ ] **Step 7: Manual smoke — switch to a not-yet-downloaded model**

1. Select a local ASR model that is **not installed** as Active model.
2. Do a dictation *before* downloading it.
Expected: the pipeline stays alive on the **current** model; dictation still
works on the old model (requirement 3, keep-current-until-verified — the
descriptor-level `VerifyReadyAsync` check returns false, so the decider keeps
the current session).
3. Download the model via the Models page download flow; let it complete.
4. Do another dictation.
Expected: this dictation swaps to the newly downloaded model (requirement 3 —
the readiness check queues behind/observes the completed verified download).

- [ ] **Step 8: Manual smoke — failed load keeps old model + toast**

1. With a working model loaded, corrupt one required file of a *different*
   selected model's directory in a way that preserves its size (e.g. flip bytes
   in `vocab.txt` without truncating) so the readiness check would need a full
   hash run to notice, OR temporarily stub the readiness delegate to return
   true for it — the goal is to reach `new ParakeetSession(dir)` and have it
   throw. (Note: deleting a file no longer works as the trigger here — the
   descriptor-level verification catches missing/short files BEFORE the load
   and yields the Step 7 keep-current behavior instead.)
2. Select that model and do a dictation.
Expected: an ASR error toast appears with the "Open Models tab" deep link; the
dictation still succeeds on the **previous working** model; subsequent dictations
keep working on the old model AND new dictations can still start (the engine
returned to Idle — requirement 3 "never dead after failed swap", requirement 4
old session retained).

- [ ] **Step 9: Manual smoke — Models page "Installed" state after a live swap**

1. After a successful live swap (Step 4), navigate away from and back to the
   Models page.
Expected: the verify-on-navigate and the ASR "Installed" label describe the
**live selected** model, not the boot model (Task 8 Step 4); no stale
`ErrorBus` report for a model that is neither selected nor running.
2. Promote a different installed model while staying on the page.
Expected: the "Installed" state refreshes for the newly promoted model without
renavigating.

- [ ] **Step 10: Manual smoke — promote transport: effective immediately, no lost update, no dictation kill (F1/S4)**

1. Promote a different installed local model and IMMEDIATELY do a dictation
   (do not wait for any debounce).
Expected: that next dictation already uses the newly promoted model (the
in-memory slot carried it — requirement 1 "effective immediately").
2. After the promote, change some OTHER debounced setting (e.g. toggle a
   settings control that goes through `SettingsWriter`), wait a few seconds,
   then restart the app.
Expected: `AsrModelName` in settings.json still names the promoted model — the
single-writer routing means no stale debounced snapshot reverted the promote
(S4 lost-update check).
3. Issue a promote CONCURRENTLY with an active dictation (promote on the
   Models page while a dictation is recording/transcribing).
Expected: no exception surfaces from the promote's persistence, and the
in-flight dictation completes normally — a transient settings read/replace
collision neither throws into the UI callback nor fails the dictation
(`SettingsStore.Load` hardening); the promote takes effect per the req-1
invariant (next transcribe seam).

- [ ] **Step 11: Dual-session load + latency probe (D1/D2 residuals)**

On the Windows host, run a small probe (a scratch console project or
`dotnet-script` against the built `Winpepper.Asr.dll`) that constructs **two**
`ParakeetSession` instances against the installed models:

1. Construct session A (model v3), record `GC.GetTotalMemory` + process working
   set; construct session B (model v2) while A is alive — this is the swap's
   transient install-new-before-dispose-old coexistence window. Record peak
   working set.
2. Measure `new ParakeetSession(dir)` wall-clock latency for each model (cold
   file cache if practical) — this is the latency a dictation pays at the seam
   on first-load/model-change (D2: lazy sync load was chosen; revisit with
   async preload only if this is unacceptable).
Expected: both sessions coexist without OOM on the VM's memory budget, and the
load latency is recorded in the smoke report.

**Residual risks (explicitly accepted, not silently deferred):** a rare native
abort (SEH/AV/`std::terminate`) during load is NOT a catchable managed
exception and would violate keep-old-on-failure by crashing the process
(worst case: crash → restart, no data loss); on DirectML hardware (not
testable in this VM) a caught `DEVICE_REMOVED` during the new load can poison
the shared D3D12 device so the KEPT old session's `Run()` later fails.
Recommended follow-up (S3, out of scope here): a subsequent-`Run` failure
ladder that reports and recovers rather than assuming the kept session stays
healthy.

- [ ] **Step 12: Record the smoke result**

If the repo convention wants a written gate report (matching prior
`docs/sdd/*regression-gate*` / `docs/plans` commits), add a short
`docs/plans/2026-07-23-live-asr-model-swap-smoke.md` capturing build output,
the smoke outcomes (Steps 4–10), and the Step 11 memory/latency numbers, then
commit:
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

**1. Spec coverage** — walked each requirement against the revised tasks:
- Req 1 (immediate, never mid-transcription): "effective immediately" now rests
  on the in-memory `AsrModelSelectionSlot` (Task 5, unit-tested) — promotes
  publish the raw name to the slot and the seam reads it — NOT on the
  settings-file round-trip, whose Windows replace-vs-open-handle race (F1) can
  silently drop a promote and whose boot-snapshot `DebouncedSettingsWriter` (S4)
  can revert one; promote persistence is routed through the single
  `SettingsWriter` authority (Task 8 Step 5) so no stale debounced flush can
  undo it, and `SettingsStore.Load` is hardened so the race cannot fail a
  dictation (Task 5 Step 4). Pure decider (Tasks 1–2) + swap at the transcribe
  seam of each dictation on the serialized run loop (Task 7 Step 4) + smoke
  Task 10 Steps 4–6 and Step 10 (promote transport: effective immediately, no
  lost update, concurrent promote doesn't kill a dictation). The invariant is
  stated accurately: never swap mid-transcription; a dictation whose recording
  is already complete transcribes on the model loaded at transcribe time, so a
  mid-RECORDING promote applies to that clip (explicitly smoke-tested in
  Task 10 Step 6). Covered.
- Req 2 (derive dir from descriptor, keep AppPaths for default): `InstallDirFor`
  (Task 3) + resolver wiring (Task 8 Step 2); `AppPaths.ParakeetModelDir` left
  defined, only its use removed. Covered.
- Req 3 (VERIFIED provisioning, keep-current-until-ready, never dead after
  failure): the decider's readiness input is descriptor-level verification —
  `ModelProvisioningCoordinator.VerifyReadyAsync(descriptor)` per resolved name,
  cached per selection change (`ModelsServices.VerifyAsrModelReady`, Task 8
  Step 1; consumed in Task 7 Step 3) — never bare `File.Exists`, preserving the
  `AppShell.cs:355-357` invariant. `Plan` returns `KeepCurrent` when desired not
  verified-ready (Task 2); keep-old-on-failure in `TryEnsureAsrModel` (Task 7
  Step 3); the seam early-exit drives the engine back via
  `SessionEvent.Failed` instead of a bare `return`, so one failure can never
  leave all later dictations dead (Task 7 Step 4); smoke Task 10 Steps 7–8.
  Covered.
- Req 4 (dispose old safely): dispose only after installing the new session,
  under `_startGate` — at the swap seam AND in `PipelineHost.Dispose()`; plus an
  idempotent `ParakeetSession.Dispose` (`_disposed` guard) so seam-dispose and
  host-dispose can never double-dispose (Task 7 Steps 3–5) + smoke Task 10
  Step 8. Covered.
- Req 5 (history records new name): the seam resolves the canonical descriptor
  name FIRST (`ResolveOrDefault`) and only that resolved name reaches
  `Plan`/`CommitLoad` (Tasks 1–2 caller contract, Task 7 Step 3), so history can
  never record a model that didn't run and unknown/`""` names cannot cause
  spurious swaps; live `loadedModelName` threaded through `BuildTranscriber` →
  `ParakeetTranscriber.ModelName` → `ProviderModelName` → history `AsrModelName`
  (Task 8 Step 3, Task 7 Step 4) + smoke Task 10 Steps 4–6. Covered.
- Req 6 (cloud path + UI unchanged): the seam is provider-aware — when
  `AsrProvider == "assemblyai"` a failed local swap never skips/aborts the
  dictation, the old local session is kept for `FallbackTranscriber`, and the
  local error surface is softened (`reportErrors: false`) (Task 7 Step 4); the
  AssemblyAI request/fallback code and XAML are untouched; full-suite regression
  Task 9 + smoke Task 10. Covered.
- Enablers with coverage: the Swap branch is production-reachable only via the
  second local ASR descriptor (Task 4, unit-tested: >=2 ASR descriptors, local
  names never match the cloud prefix); the Models page boot-snapshot staleness
  is fixed rather than accepted (Task 8 Step 4, smoke Task 10 Step 9); the
  settings-file transport hazard (F1) and the debounced-writer lost update (S4)
  are fixed rather than accepted — in-memory slot as transport (Task 5),
  promotes publish + persist through the single writer authority (Task 8
  Step 5), hardened `Load` (Task 5 Step 4), smoke Task 10 Step 10 — the
  boundary honestly spans `ModelRegistry`, `PipelineHost`, `AppShell`,
  `ParakeetSession.Dispose`, `SettingsStore`, `AsrModelSelectionSlot`,
  `ModelsServices`, `ModelsPage`, and `HistoryDetailPage`, all listed in
  File Structure.

**1b. No silent deferrals** — the Windows-only App wiring cannot have an xUnit
project in this repo (App is Windows-only, no `Winpepper.App.Tests`); it is NOT
a stub/mock: the production `PipelineHost` calls the real, unit-tested
`AsrModelSwapState` and real `ParakeetSession`, proven by the mandatory Windows
build + smoke checklist (Task 10). The previously-implicit residuals are now
EXPLICIT Windows-verification items, not silent: VM availability is a hard
precondition gate (Task 10 Step 1 — the VM was down at validation time);
DirectML is explicitly OUT of scope for the VM (CPU execution provider only,
blessed at `ParakeetSession.cs:38-40` — stated in the Task 10 intro and Step 11);
dual-session memory coexistence and seam load latency are measured by a
dedicated probe, and the rare native-abort / DML `DEVICE_REMOVED` residuals are
recorded with the recommended subsequent-Run failure ladder (S3) as a scoped-out
follow-up (Task 10 Step 11). The transient-`IOException` branch of the hardened
`SettingsStore.Load` has no Linux unit test (no mandatory file locking to
reproduce the race) — stated explicitly in Task 5 Step 4 and exercised by the
concurrent-promote smoke (Task 10 Step 10), not silently skipped. The known
pre-existing History-detail promote-`""`
bug (S5) is surfaced in Task 2 with a recommended guard, scoped as a related
bug, and rendered harmless to this feature by the resolve-first contract (and
the promote path now routes through the slot + single writer, Task 8 Step 5).
The UNRESOLVED COVERAGE GAPS section is intentionally empty.

**2. Placeholder scan** — no "TBD/TODO/handle edge cases/similar to Task N".
Every code step shows complete code, with two deliberate, bounded exceptions:
(a) the transcribe-seam edits reference the existing `notice => ...` lambda body
by preservation instruction (the body is long, unrelated, and must be kept
verbatim) — a "keep existing code" instruction, not a placeholder; (b) Task 4's
descriptor `Sha256`/`SizeBytes` fields are filled from the literals PRINTED by
`scripts/verify-model-hashes.ps1` in the same task (Step 3 → Step 4), with an
explicit rule that the task is not complete while any from-the-script comment
remains — a generated-value procedure, not a deferred design decision.

**3. Type consistency** — verified names/signatures across the renumbered tasks:
- `AsrSwapAction { KeepCurrent, Load, Swap, CannotStart }`, `AsrModelSwapState`,
  `.Plan(string, bool)`, `.CommitLoad(string)`, `.LoadedModelName`,
  `.Generation` — identical in Tasks 1, 2, 6; the caller contract (RESOLVED
  canonical names in, verified readiness for the bool) is stated identically in
  Tasks 1, 2, and 6.
- `ModelRegistry.InstallDirFor(string installRoot, string? requestedName,
  ModelKind kind)` — identical in Tasks 3 and 7; `ModelRegistry.SecondAsrName`
  — identical in Task 4's implementation and test;
  `ResolveOrDefault(string?, ModelKind)` — used identically in Tasks 6 (contract),
  7 (AppShell lambda + `ModelsServices.VerifyAsrModelReady`).
- `AsrModelSelectionSlot` with `Publish(string?)` / `Read() : string?` —
  identical in Task 5 (implementation + tests), Task 7 (consumed via
  `Func<string?> desiredAsrModelName`), and Task 8 (constructed, seeded with the
  reconciled boot `settings.AsrModelName`, exposed as
  `AppShell.AsrModelSelection`, published to by both promote callbacks). The
  slot holds the RAW name; only the seam's `_resolveAsrModelName` produces the
  canonical name fed to `Plan`/`CommitLoad` — resolve-first is preserved.
- `PipelineHost` ctor deps `Func<string,string> resolveModelDir`,
  `Func<string?> desiredAsrModelName`, `Func<string?,string> resolveAsrModelName`,
  `Func<string,bool> isAsrModelReady`
  — declared in Task 7 (fields/ctor/assignments) and supplied positionally in
  Task 8 Step 2's construction; `TryEnsureAsrModel(bool reportErrors = true)` —
  defined in Task 7 Step 3, called with `reportErrors: !cloudSelected` in Task 7
  Step 4 and bare (`reportErrors` defaulting true) in `TryStartCore`.
- `ModelsServices.VerifyAsrModelReady(string) : bool` — defined in Task 8 Step 1,
  consumed by the Task 8 Step 2 lambda and by `ModelsPage` (Task 8 Step 4).
- `SettingsStore.Load() : AppSettings` — signature unchanged by the Task 5
  hardening (retry + last-known-good is internal), so the seam's
  `() => store.Load()` settings provider and every other caller compile
  untouched; promotes no longer call `SettingsStore.Save` directly (Task 8
  Step 5 routes them through `ISettingsWriter.QueueAndFlushAsync`).
- Transcriber factory delegate
  `Func<ParakeetSession, string, AppSettings, Action<string>, ITranscriber>` and
  `BuildTranscriber(local, loadedModelName, settings, onFallback, ...)` — matched
  between Task 7 (field/ctor/call) and Task 8 (call site + method signature).
- `new ParakeetSession(string)`, idempotent `Dispose()`, and
  `ParakeetTranscriber(ParakeetSession, string)` — used exactly as they exist
  in the codebase (plus the Task 7 Step 5 `_disposed` guard).
  `ParakeetSession.ModelFilesPresent` is intentionally NO LONGER an input to the
  decider anywhere in this plan (replaced by descriptor-level verified
  readiness); no task references it as the readiness feed.
- Cross-task references audited after renumbering: Tasks 1–3 unchanged; Task 4
  = catalog; Task 5 = desired-model slot + `SettingsStore.Load` hardening;
  Task 6 = pure gate (consumes 1–5); Task 7 = PipelineHost (supplied
  by Task 8; verified in Task 10 Steps 2/4+); Task 8 = AppShell/ModelsServices/
  ModelsPage/HistoryDetailPage; Task 9 = full regression (~829–841 total);
  Task 10 = Windows gate + smoke (Steps 1–12).
