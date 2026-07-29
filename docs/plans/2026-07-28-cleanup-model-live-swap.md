# Live Cleanup-Model Swap Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Changing the cleanup LLM model in Settings (or History Lab) takes effect on the next dictation without an app restart, with the ~1–1.7 s GGUF load AND the ~0.27–0.49 s first-generation warm-up pre-warmed in the background and the model hash-verified before it is ever swapped in.

**Architecture:** Mirror the existing ASR live-swap pattern (docs/plans/2026-07-23-live-asr-model-swap.md): a volatile in-memory selection slot for "effective immediately" transport, a pure `Plan/CommitLoad` swap decider, and a per-dictation seam inside PipelineHost's serialized run loop. Cleanup differs from ASR in three ways this plan addresses head-on: (1) the backend load PLUS a first-generation warm-up (`WarmAsync`, via the holder's `warmup` delegate) are pre-warmed asynchronously on promote so no dictation pays either, (2) readiness is descriptor-level SHA-256 verification (a new `ModelFilesVerifier` + `ModelsServices.VerifyCleanupModelReady`, deliberately NOT routed through `ModelProvisioningCoordinator` whose global state feeds the ASR startup gate), and (3) each model carries `PromptFormat` + `OmitPromptExample`, so a swap constructs a fresh `LlamaCleanupBackend` AND a fresh `CleanupRunner`. A new `CleanupBackendHolder` (in `Winpepper.Cleanup`, cross-platform, delegate-injected so it is fully Linux-testable) owns the live backend+runner pair, the pre-warm task, and all disposal.

**Tech Stack:** C# / .NET 9, LLamaSharp (Windows TFM only), xUnit v3 + Shouldly for tests, hand-written fakes (no mocking library).

## Global Constraints

- Branch: `feat/cleanup-model-live-swap` in worktree `/home/dan/code/winpepper/.worktrees/cleanup-model-live-swap`. All paths below are relative to the worktree root. Do NOT touch the keyboard hook or `packaging/`.
- **Design decision 1 (already made with the user — do not re-litigate): PRE-WARM ON PROMOTE.** When the user promotes a cleanup model, load the new backend asynchronously in the background so the ~1–1.7 s LlamaWeights load is NOT paid by the next dictation. The swap itself must still only take effect at the dictation boundary (the serialized seam), never mid-generation. **Validation finding (load-bearing ledger A5, falsified):** the ~1–1.7 s figure is LOAD-ONLY — the bake-off bench measured an ADDITIONAL 266–488 ms "Warm (once)" cost on the first generation (Vulkan shader pipeline + weight paging), roughly doubling first-dictation cleanup latency. Pre-warm therefore means load + `WarmAsync`: the holder's `warmup` delegate (Task 5) runs inside the background load task, and a pre-warm is "ready" only after both complete. Concurrent-load safety and dual-residency headroom were verified (ledger A3/A4: llama.cpp Vulkan device access is mutex-guarded at the pinned revision; OOM on the LOAD path surfaces as a catchable managed exception; ~6x memory headroom on the target machine).
- **Design decision 2 (already made with the user — do not re-litigate): HASH-VERIFIED READINESS.** Before swapping, verify the model files against registry sha256 (analog of `ModelsServices.VerifyAsrModelReady`) — file-exists alone is not sufficient. A model that fails verification must not be swapped in; keep the current backend and log the reason.
- **SERIALIZED-CALLER INVARIANT (the disposal safety mechanism — must be documented in code):** only PipelineHost's run loop calls `CleanupRunner.RunAsync`, and the loop awaits it inline (one hotkey event is fully processed before the next is dequeued — `PipelineHost.RunAsync`, `await foreach` + inline `await HandleHotkey`). Therefore at the per-dictation seam no generation is in flight on the old backend, and a pre-warmed backend discarded before ever being handed out has no callers at all. `LlamaCleanupBackend.Dispose()` is NOT gated against concurrent `GenerateAsync` — this invariant is why disposal at the seam is safe without an orphan guard. **Validation (ledger A1, verified):** the invariant holds on ALL `RunAsync` exit paths (normal, timeout, cancel, exception) — an `await` surfaces only after the awaited Task is terminal (its `finally`, incl. `_gate.Release()`, already ran), and LLamaSharp **0.27.0**'s `StatelessExecutor.InferAsync` is a plain async iterator whose native work runs via awaited `DecodeAsync` (no detached work possible). This proof is version-pinned: re-verify on any LLamaSharp upgrade. **Shutdown leg (ledger A2, FALSIFIED and corrected):** `PipelineHost.Dispose` only does a bounded best-effort join (`_runTask?.Wait(2 s)`, PipelineHost.cs:1304-1305, result previously discarded), so "dispose PipelineHost before the holder" alone does NOT guarantee quiescence. Task 8 therefore records the join outcome as `PipelineHost.RunLoopJoined`, and `AppShell.Dispose` disposes the holder ONLY when the join succeeded — on a timed-out join the holder is deliberately leaked (`Application.Current.Exit()` follows immediately; a leak is safe, a use-after-free is not).
- **Both dictation code paths must be modified.** PipelineHost has two near-duplicate cleanup blocks (HOLD path ~`:641-712` with archive `:810-832`; TOGGLE path ~`:1039-1110` with archive `:1216`), differing only by a `2` suffix on locals. Every seam change lands in BOTH. This is the single largest correctness hazard of the change.
- **Prompt-format correctness on swap:** `PromptFormat` is consumed only by `LlamaCleanupBackend` (constructor) and `OmitPromptExample` only by `CleanupRunner` (constructor); both are `readonly`. A swap therefore constructs a fresh backend AND a fresh runner from the NEW model's registry descriptor values.
- `Winpepper.Cleanup` references `Winpepper.Core` + `Winpepper.Corrections` but NOT `Winpepper.Models` (verified in `src/Winpepper.Cleanup/Winpepper.Cleanup.csproj`). The holder therefore takes a local `CleanupModelTarget` record; AppShell maps `Winpepper.Models.CleanupModelResolution` into it. Do not add a Cleanup→Models project reference.
- Pure decision logic lives in `Winpepper.Core` (Linux-tested from `tests/Winpepper.Core.Tests`, TFM net9.0). The holder lives in `Winpepper.Cleanup` (dual-TFM; its net9.0 leg is Linux-tested from `tests/Winpepper.Cleanup.Tests`). `Winpepper.App` is Windows-only (`net9.0-windows10.0.19041.0`) and has NO test project — its wiring is verified by the Windows gate + the smoke checklist (Task 11), never by Linux xUnit.
- Test framework: xUnit v3 (`using Xunit;`), Shouldly assertions (`using Shouldly;`), `[Fact]` methods named `Method_Scenario_Expected`, namespace mirrors the folder. Package versions are centralized in `Directory.Packages.props` — never add inline `Version="..."`. New pure-logic tests carry NO `[Trait("Platform", "Windows")]` so they run in the Linux gate.
- Test running: build each test project `-c Release`, then run via the xUnit v3 in-process runner: `dotnet exec <built test dll>`. **NEVER `dotnet test`** (the VSTest host is unreliable on this machine). The repo-local SDK lives at `/home/dan/code/winpepper/.dotnet`. Dual-TFM test projects need `-f net9.0 -p:EnableWindowsTargeting=true` on the Linux build and `-notrait "Platform=Windows"` on the run.
- **TDD red steps must assert on the runner summary (`Failed: N` with N>0, or a build error), NOT on exit code alone** — `-namespace <ns>` matching zero tests exits 0 with `Total: 0`.
- **All tests green before EVERY commit:** run `./scripts/linux-tests.sh` and require it to end with `LINUX SUITE: GREEN` before each `git commit`. Baseline at branch point: ~1387 Linux tests GREEN (from commit `dddcc14`'s verification block); new tests add to this count.
- **Full Windows suite before push:** `./scripts/windows-gate.sh` from WSL (expect ~10 min; success = `GATE: GREEN`). UNC MSB4025 "retry should be performed" build failures are a known transient flake — retry the gate. Never mix Linux- and Windows-side builds in the same `bin/`/`obj/` (the gate cleans them itself; do not hand-roll).
- Commit messages: Conventional Commits with scope (`feat(cleanup): ...`, `feat(core): ...`, `feat(app): ...`). Every commit ends with this exact trailer block (blank line before it, and a blank line between the two trailer lines):

  ```
  🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```
- README.md is the only end-user markdown doc; this plan under `docs/plans/` is a working/agent doc. Do not create other markdown docs.

## UNRESOLVED COVERAGE GAPS

None. Every spec requirement maps to a task below (see the coverage map).

## Requirement → Coverage Map

| Spec requirement | Observable production outcome | Covered by |
|---|---|---|
| Promote publishes in-memory + persists (both promote sites) | Changing the cleanup model in Settings or History Lab takes effect on the next dictation, no restart | Task 1 (slot), Task 9 (callbacks), Task 8 (seam), Task 11 smoke 4 |
| Pre-warm on promote — load not paid by the next dictation | The GGUF load + first-generation warm-up run on a background thread at promote time; the dictation path never loads synchronously | Task 4 (holder pre-warm), Task 5 (warm-up delegate), Task 9 (`RequestPrewarm` in callbacks), Task 11 smoke 5 |
| Swap only at the dictation boundary, never mid-generation | The live pair mutates only inside `EnsureCurrent()`, called once per dictation from the serialized run loop before `RunAsync` | Task 4 (tests 1–3), Task 8 (seam placement in BOTH paths), Task 6 (documented invariant) |
| Hash-verified readiness before swap; failed verification keeps current model + logs | A corrupt/missing model is never swapped in; dictations keep the working model | Task 3 (verifier), Task 5 (holder tests), Task 7 (`VerifyCleanupModelReady`), Task 11 smoke 6 |
| Fallback when desired model missing | Dictations continue with raw transcript (runner null) or the previous model; self-heals when the model appears | Task 5, Task 8, Task 11 smoke 6 |
| Disposal of replaced and never-used pre-warmed backends | No native-weights leak; no use-after-free | Task 4 (swap dispose), Task 5 (stale-pre-warm + holder Dispose), Task 6 (idempotent `Dispose`), Task 8 Step 5(c) (join-conditional shutdown dispose) |
| Prompt-format correctness on swap (`PromptFormat` + `OmitPromptExample` from the NEW descriptor) | A swapped-in granite/raw-io model cleans correctly (fresh backend AND fresh runner) | Task 4 (test 6), Task 8 (fresh construction via factories), Task 11 smoke 7 |
| History attribution reflects the actually-used model | History records stamp the swap state's loaded (resolved) name, not the boot-time raw settings string | Task 4 (test 5), Task 8 (both archive sites), Task 11 smoke 4 |
| Both near-duplicate dictation paths get the seam | Hold-dictation and toggle-dictation both swap | Task 8 |
| Tests: mirror ASR slot/decider coverage; pure logic Linux-runnable; real-LLamaSharp parts Windows-traited | `./scripts/linux-tests.sh` GREEN with new tests counted; Windows gate GREEN | Tasks 1–5, Task 10, Task 11 |

## File Structure

**Created:**
- `src/Winpepper.Core/Settings/CleanupModelSelectionSlot.cs` — volatile in-memory desired-model slot (mirror of `AsrModelSelectionSlot`).
- `src/Winpepper.Core/Cleanup/CleanupSwapAction.cs` — decision enum (mirror of `AsrSwapAction`).
- `src/Winpepper.Core/Cleanup/CleanupModelSwapState.cs` — pure `Plan`/`CommitLoad` decider (mirror of `AsrModelSwapState`).
- `src/Winpepper.Models/ModelFilesVerifier.cs` — stateless descriptor-level exists+size+SHA-256 check (kept out of `ModelProvisioningCoordinator` so cleanup verification never churns the ASR-facing global provisioning state).
- `src/Winpepper.Cleanup/CleanupModelTarget.cs` — plain record of a resolved cleanup model (path, resolved name, prompt format, omit flag); decouples the holder from `Winpepper.Models`.
- `src/Winpepper.Cleanup/CleanupBackendHolder.cs` — owns the live backend+runner pair, pre-warm task, swap-at-seam, disposal; also declares `CleanupBackendLease`.
- `tests/Winpepper.Core.Tests/Settings/CleanupModelSelectionSlotTests.cs`
- `tests/Winpepper.Core.Tests/Cleanup/CleanupModelSwapStateTests.cs`
- `tests/Winpepper.Models.Tests/ModelFilesVerifierTests.cs`
- `tests/Winpepper.Cleanup.Tests/Fakes/DisposableFakeBackend.cs`
- `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs`

**Modified:**
- `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` — idempotent `Dispose` + documented disposal contract (Windows TFM only).
- `src/Winpepper.App/Services/ModelsServices.cs` — add `VerifyCleanupModelReady` (name-keyed positive cache, coordinator-free).
- `src/Winpepper.App/Hosting/AppShell.cs` — seed the slot, construct the holder (replacing the boot-time backend/runner construction), pass the holder into PipelineHost, expose shell properties, dispose ordering.
- `src/Winpepper.App/Hosting/PipelineHost.cs` — replace `readonly CleanupRunner? _cleanup` + `readonly string _cleanupModelName` with the holder; per-dictation `EnsureCurrent()` at the cleanup seam in BOTH dictation paths; attribution from the lease.
- `src/Winpepper.App/Views/ModelsPage.xaml.cs` — `promoteCleanup` publishes + pre-warms.
- `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs` — `promoteCleanupDefault` publishes + pre-warms.

---

### Task 1: CleanupModelSelectionSlot (Core)

The in-memory transport for "effective immediately". Exact mirror of `src/Winpepper.Core/Settings/AsrModelSelectionSlot.cs` — the settings-file round-trip is durability only (a Windows atomic replace of settings.json can fail against a concurrently open read handle, silently dropping a promote).

**Files:**
- Create: `src/Winpepper.Core/Settings/CleanupModelSelectionSlot.cs`
- Test: `tests/Winpepper.Core.Tests/Settings/CleanupModelSelectionSlotTests.cs`

**Interfaces:**
- Consumes: nothing (leaf type).
- Produces: `Winpepper.Core.Settings.CleanupModelSelectionSlot` with `void Publish(string? modelName)` and `string? Read()`. Task 8 seeds it in AppShell and wires `() => cleanupSelection.Read()` into the holder; Task 9 calls `Publish(name)` from the promote callbacks.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Settings/CleanupModelSelectionSlotTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class CleanupModelSelectionSlotTests
{
    [Fact]
    public void Read_BeforeAnyPublish_ReturnsNull()
    {
        var slot = new CleanupModelSelectionSlot();

        slot.Read().ShouldBeNull();
    }

    [Fact]
    public void Read_AfterPublish_ReturnsPublishedName()
    {
        var slot = new CleanupModelSelectionSlot();

        slot.Publish("qwen2.5-0.5b-instruct-q4_k_m");

        slot.Read().ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void Publish_LatestWriteWins()
    {
        var slot = new CleanupModelSelectionSlot();

        slot.Publish("model-a");
        slot.Publish("model-b");

        slot.Read().ShouldBe("model-b");
    }

    [Fact]
    public void Publish_FromAnotherThread_IsVisibleToReader()
    {
        var slot = new CleanupModelSelectionSlot();

        var publisher = new Thread(() => slot.Publish("model-a"));
        publisher.Start();
        publisher.Join();

        slot.Read().ShouldBe("model-a");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/cleanup-model-live-swap
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: **build error** `CS0246: The type or namespace name 'CleanupModelSelectionSlot' could not be found` (a build error counts as red).

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Core/Settings/CleanupModelSelectionSlot.cs`:

```csharp
namespace Winpepper.Core.Settings;

/// <summary>
/// Thread-safe in-memory source of truth for the DESIRED cleanup model name.
/// UI promote callbacks <see cref="Publish"/> the newly selected RAW name (in
/// addition to persisting it to settings.json for durability across restarts);
/// the cleanup-backend holder <see cref="Read"/>s it, both when pre-warming on
/// promote and at the pipeline's per-dictation seam. This is the cross-thread
/// transport for "effective immediately" — the settings-file round-trip is
/// NOT: on Windows an atomic replace of settings.json can fail against a
/// concurrently open read handle, silently dropping the promote.
/// A volatile reference is sufficient: single word-sized publication,
/// last-write-wins, no compound state.
/// (Mirror of <see cref="AsrModelSelectionSlot"/>.)
/// </summary>
public sealed class CleanupModelSelectionSlot
{
    private volatile string? _desired;

    /// <summary>Publish the newly selected raw model name (UI thread).</summary>
    public void Publish(string? modelName) => _desired = modelName;

    /// <summary>Read the currently desired raw model name (holder / pipeline loop).</summary>
    public string? Read() => _desired;
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -namespace Winpepper.Core.Tests.Settings
```

Expected: summary line ends `Errors: 0, Failed: 0` and the total includes the 4 new facts (the namespace also runs the existing Settings tests — all green).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Core/Settings/CleanupModelSelectionSlot.cs tests/Winpepper.Core.Tests/Settings/CleanupModelSelectionSlotTests.cs
git commit -m "$(cat <<'EOF'
feat(core): CleanupModelSelectionSlot — in-memory desired cleanup model transport

Mirror of AsrModelSelectionSlot: volatile Publish/Read slot so a cleanup-model
promote is effective immediately, with settings.json kept for durability only.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: CleanupSwapAction + CleanupModelSwapState (Core)

The pure swap decider, mirror of `Winpepper.Core.Asr.AsrModelSwapState` / `AsrSwapAction`, with cleanup-specific doc: "ready" means a hash-verified, pre-warmed backend for the desired model is ready to adopt, and `CannotStart` is non-fatal (dictations fall back to the raw transcript).

**Files:**
- Create: `src/Winpepper.Core/Cleanup/CleanupSwapAction.cs`
- Create: `src/Winpepper.Core/Cleanup/CleanupModelSwapState.cs`
- Test: `tests/Winpepper.Core.Tests/Cleanup/CleanupModelSwapStateTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces: `Winpepper.Core.Cleanup.CleanupSwapAction { KeepCurrent, Load, Swap, CannotStart }`; `Winpepper.Core.Cleanup.CleanupModelSwapState` with `string? LoadedModelName { get; }`, `int Generation { get; }`, `CleanupSwapAction Plan(string desiredModelName, bool desiredReady)` (pure, non-mutating), `void CommitLoad(string modelName)`. Task 4's holder consumes all of these (`Winpepper.Cleanup` already references `Winpepper.Core`).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Cleanup/CleanupModelSwapStateTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Cleanup;
using Xunit;

namespace Winpepper.Core.Tests.Cleanup;

public class CleanupModelSwapStateTests
{
    [Fact]
    public void Plan_NothingLoaded_DesiredReady_ReturnsLoad()
    {
        var state = new CleanupModelSwapState();

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: true)
             .ShouldBe(CleanupSwapAction.Load);
    }

    [Fact]
    public void Plan_NothingLoaded_DesiredNotReady_ReturnsCannotStart()
    {
        var state = new CleanupModelSwapState();

        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: false)
             .ShouldBe(CleanupSwapAction.CannotStart);
    }

    [Fact]
    public void Plan_DoesNotMutateState()
    {
        var state = new CleanupModelSwapState();

        state.Plan("qwen2.5-0.5b-instruct-q4_k_m", desiredReady: true);

        state.LoadedModelName.ShouldBeNull();
        state.Generation.ShouldBe(0);
    }

    [Fact]
    public void CommitLoad_SetsLoadedNameAndIncrementsGeneration()
    {
        var state = new CleanupModelSwapState();

        state.CommitLoad("qwen2.5-0.5b-instruct-q4_k_m");

        state.LoadedModelName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        state.Generation.ShouldBe(1);
    }

    [Fact]
    public void Plan_SameModelLoaded_ReturnsKeepCurrent()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-a", desiredReady: true)
             .ShouldBe(CleanupSwapAction.KeepCurrent);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredReady_ReturnsSwap()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.Plan("model-b", desiredReady: true)
             .ShouldBe(CleanupSwapAction.Swap);
    }

    [Fact]
    public void Plan_DifferentModelLoaded_DesiredNotReady_ReturnsKeepCurrent()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        // Desired model not verified/pre-warmed yet: stay on the working model.
        state.Plan("model-b", desiredReady: false)
             .ShouldBe(CleanupSwapAction.KeepCurrent);
    }

    [Fact]
    public void CommitLoad_AfterSwap_AdvancesLoadedNameAndGeneration()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        state.CommitLoad("model-b");

        state.LoadedModelName.ShouldBe("model-b");
        state.Generation.ShouldBe(2);
    }

    [Fact]
    public void FailedSwap_NoCommit_KeepsPreviousModelAndGeneration()
    {
        var state = new CleanupModelSwapState();
        state.CommitLoad("model-a");

        // The holder planned a Swap but adoption failed, so it does NOT call CommitLoad.
        var action = state.Plan("model-b", desiredReady: true);
        action.ShouldBe(CleanupSwapAction.Swap);
        // (no CommitLoad)

        state.LoadedModelName.ShouldBe("model-a");
        state.Generation.ShouldBe(1);
        // The next dictation still wants model-b and will retry the swap.
        state.Plan("model-b", desiredReady: true).ShouldBe(CleanupSwapAction.Swap);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: **build error** `CS0246` on `CleanupModelSwapState` / `Winpepper.Core.Cleanup`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Core/Cleanup/CleanupSwapAction.cs`:

```csharp
namespace Winpepper.Core.Cleanup;

/// <summary>
/// The decision produced by <see cref="CleanupModelSwapState.Plan"/> about what
/// the cleanup-backend holder should do with its live backend+runner pair at
/// the next dictation's cleanup seam.
/// </summary>
public enum CleanupSwapAction
{
    /// <summary>Keep the currently loaded backend; no swap needed.</summary>
    KeepCurrent,

    /// <summary>Nothing is loaded yet; adopt the desired model's pre-warmed pair.</summary>
    Load,

    /// <summary>A different model is desired and its pre-warmed pair is ready; swap to it.</summary>
    Swap,

    /// <summary>
    /// Nothing loaded and the desired model is not ready. Unlike the ASR analog
    /// this is NON-FATAL: the dictation proceeds with the raw transcript (no
    /// cleanup) and a later dictation re-evaluates once a pre-warm completes.
    /// </summary>
    CannotStart,
}
```

Create `src/Winpepper.Core/Cleanup/CleanupModelSwapState.cs`:

```csharp
namespace Winpepper.Core.Cleanup;

/// <summary>
/// Pure decision state for live cleanup-model swapping (mirror of
/// <c>Winpepper.Core.Asr.AsrModelSwapState</c>). Holds which model's
/// backend+runner pair is currently live and decides, per dictation, whether
/// to keep it, adopt a first pair, or swap to a newly selected model.
///
/// Caller contract: model names passed to <see cref="Plan"/> and
/// <see cref="CommitLoad"/> are RESOLVED canonical descriptor names (the host
/// resolves the raw settings value via ModelRegistry.ResolveOrDefault first),
/// and the readiness flag means a hash-verified (per-file size + SHA-256),
/// fully pre-warmed backend for the desired model is ready to adopt — not
/// bare file existence, and never an in-flight load.
///
/// State only advances via <see cref="CommitLoad"/>, which the holder calls
/// after a pair is successfully adopted. If a load/pre-warm fails, the holder
/// does not call CommitLoad, so <see cref="LoadedModelName"/> keeps naming the
/// previous working model — the "keep-old-on-failure" guarantee in pure,
/// testable code. <see cref="LoadedModelName"/> is also the value history
/// records stamp: it names the model that actually ran.
/// </summary>
public sealed class CleanupModelSwapState
{
    /// <summary>The model whose pair is currently live; null until first load.</summary>
    public string? LoadedModelName { get; private set; }

    /// <summary>Number of successful (re)loads so far; starts at 0.</summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Decide what to do at the next dictation's cleanup seam given the desired
    /// model and whether a verified pre-warmed pair for it is ready. Pure: does
    /// not mutate state.
    /// </summary>
    public CleanupSwapAction Plan(string desiredModelName, bool desiredReady)
    {
        if (LoadedModelName is null)
            return desiredReady ? CleanupSwapAction.Load : CleanupSwapAction.CannotStart;

        if (string.Equals(desiredModelName, LoadedModelName, StringComparison.Ordinal))
            return CleanupSwapAction.KeepCurrent;

        // A different model is selected. Swap only if its pre-warmed pair is
        // ready; otherwise keep the current working pair until the background
        // load/verification completes (a later dictation will re-evaluate).
        return desiredReady ? CleanupSwapAction.Swap : CleanupSwapAction.KeepCurrent;
    }

    /// <summary>
    /// Record that a pair for <paramref name="modelName"/> was successfully
    /// adopted. Advances state: sets the loaded name and increments the generation.
    /// </summary>
    public void CommitLoad(string modelName)
    {
        LoadedModelName = modelName;
        Generation++;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -namespace Winpepper.Core.Tests.Cleanup
```

Expected: `Total: 9, Errors: 0, Failed: 0`.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Core/Cleanup/ tests/Winpepper.Core.Tests/Cleanup/
git commit -m "$(cat <<'EOF'
feat(core): CleanupModelSwapState — pure live-swap decider for the cleanup model

Mirror of AsrModelSwapState/AsrSwapAction: Plan(desired, ready) ->
KeepCurrent/Load/Swap/CannotStart plus CommitLoad generation tracking, with the
keep-old-on-failure guarantee expressed in pure, Linux-testable code. For
cleanup, "ready" means a hash-verified pre-warmed pair, and CannotStart is
non-fatal (raw-transcript fallback).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: ModelFilesVerifier (Models)

Descriptor-level readiness (exists + size + SHA-256) as a stateless helper. It deliberately does NOT go through `ModelProvisioningCoordinator.VerifyReadyAsync`: that method mutates the coordinator's single global `ModelProvisioningState`, which `ModelsServices.OnCoordinatorStateChanged` maps into the ASR provisioning state consumed by `AsrPipelineStartupGate`, onboarding, and the Models page — verifying a CLEANUP descriptor through it would churn the ASR-facing status. Leave the coordinator untouched (do not refactor it to call this helper — keeping the ASR path byte-identical is worth the ~10 duplicated lines).

**Files:**
- Create: `src/Winpepper.Models/ModelFilesVerifier.cs`
- Test: `tests/Winpepper.Models.Tests/ModelFilesVerifierTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Models.ModelDescriptor` (fields `InstallDirRelative`, `Files`), `Winpepper.Models.ModelFile` (fields `RelativePath`, `Sha256`, `SizeBytes`), `ChecksumVerifier.VerifyAsync(string path, string expectedHexSha256, CancellationToken ct) -> Task<bool>` (all existing).
- Produces: `Winpepper.Models.ModelFilesVerifier.VerifyAsync(ModelDescriptor descriptor, string installRoot, CancellationToken ct) -> Task<bool>`. Task 7 wraps it in `ModelsServices.VerifyCleanupModelReady`.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Models.Tests/ModelFilesVerifierTests.cs`:

```csharp
using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelFilesVerifierTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("winpepper-verifier-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private ModelDescriptor MakeInstalledModel(
        byte[] content, string? sha256Override = null, long? sizeOverride = null)
    {
        var dir = Path.Combine(_root, "cleanup", "model-x");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "model-x.gguf"), content);
        return new ModelDescriptor
        {
            Name = "model-x",
            Kind = ModelKind.Cleanup,
            DisplayName = "Model X",
            InstallDirRelative = Path.Combine("cleanup", "model-x"),
            Files = new[]
            {
                new ModelFile
                {
                    RelativePath = "model-x.gguf",
                    Url = "",
                    Sha256 = sha256Override ?? Sha256Hex(content),
                    SizeBytes = sizeOverride ?? content.Length,
                },
            },
        };
    }

    [Fact]
    public async Task VerifyAsync_AllFilesPresentWithMatchingSizeAndHash_ReturnsTrue()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3, 4, 5 });

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_FileMissing_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3 });
        File.Delete(Path.Combine(_root, "cleanup", "model-x", "model-x.gguf"));

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_SizeMismatch_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(new byte[] { 1, 2, 3 }, sizeOverride: 999);

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }

    [Fact]
    public async Task VerifyAsync_HashMismatch_ReturnsFalse()
    {
        var descriptor = MakeInstalledModel(
            new byte[] { 1, 2, 3 }, sha256Override: new string('0', 64));

        var ready = await ModelFilesVerifier.VerifyAsync(
            descriptor, _root, TestContext.Current.CancellationToken);

        ready.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release
```

Expected: **build error** `CS0103: The name 'ModelFilesVerifier' does not exist`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Models/ModelFilesVerifier.cs`:

```csharp
namespace Winpepper.Models;

/// <summary>
/// Stateless descriptor-level readiness check: every file in the descriptor
/// exists, matches its declared size, and matches its SHA-256. Extracted so
/// non-ASR callers (the cleanup live-swap pre-warm) can verify without going
/// through <see cref="ModelProvisioningCoordinator.VerifyReadyAsync"/>, whose
/// state notifications feed the single global provisioning status consumed by
/// the ASR startup gate, onboarding, and the Models page. Size is checked
/// before hashing so missing/partial files short-circuit cheaply.
/// </summary>
public static class ModelFilesVerifier
{
    public static async Task<bool> VerifyAsync(
        ModelDescriptor descriptor, string installRoot, CancellationToken ct)
    {
        foreach (var file in descriptor.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(installRoot, descriptor.InstallDirRelative, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes)
                return false;
            if (!await ChecksumVerifier.VerifyAsync(path, file.Sha256, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj -c Release
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -namespace Winpepper.Models.Tests
```

Expected: summary ends `Errors: 0, Failed: 0` (namespace includes the existing Models tests — all green, total grows by 4).

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Models/ModelFilesVerifier.cs tests/Winpepper.Models.Tests/ModelFilesVerifierTests.cs
git commit -m "$(cat <<'EOF'
feat(models): ModelFilesVerifier — coordinator-free descriptor hash verification

Stateless exists+size+SHA-256 readiness check so cleanup-model verification
never churns ModelProvisioningCoordinator's global state (which feeds the
ASR startup gate and Models page).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: CleanupBackendHolder — pre-warm and dictation-boundary swap (Cleanup)

The centerpiece. `CleanupBackendHolder` owns the live `ILlamaCleanupBackend` + `CleanupRunner` pair. `RequestPrewarm()` (UI thread / boot) starts a background load of the desired model; `EnsureCurrent()` (pipeline run loop only, once per dictation) adopts a COMPLETED pre-warm and swaps — it never loads synchronously, so a dictation never pays the multi-second GGUF load. All construction is delegate-injected (`backendFactory`, `runnerFactory`, `resolve`, `verifyReady`) so the class is cross-platform and fully Linux-testable; the Windows-only `LlamaCleanupBackend` construction stays in AppShell (Task 8).

This task builds the happy path: pre-warm → swap at seam → old backend disposed, plus prompt-format/omit-flag propagation and lease attribution. Failure and staleness paths are Task 5 — keep this task's implementation minimal exactly as written so Task 5's red steps are genuinely red.

**Files:**
- Create: `src/Winpepper.Cleanup/CleanupModelTarget.cs`
- Create: `src/Winpepper.Cleanup/CleanupBackendHolder.cs`
- Create: `tests/Winpepper.Cleanup.Tests/Fakes/DisposableFakeBackend.cs`
- Test: `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Core.Cleanup.CleanupModelSwapState` / `CleanupSwapAction` (Task 2); existing `Winpepper.Cleanup.ILlamaCleanupBackend` and `Winpepper.Cleanup.CleanupRunner(ILlamaCleanupBackend backend, ILogger<CleanupRunner> log, bool omitPromptExample = false)`.
- Produces (Tasks 5, 8, 9 rely on these exact signatures):
  - `Winpepper.Cleanup.CleanupModelTarget(string? GgufPath, string ResolvedName, bool FellBackToDefault, string PromptFormat, bool OmitPromptExample = false)` — sealed record.
  - `Winpepper.Cleanup.CleanupBackendLease(CleanupRunner? Runner, string? LoadedModelName)` — sealed record.
  - `Winpepper.Cleanup.CleanupBackendHolder : IDisposable` with constructor
    `CleanupBackendHolder(Func<string?> desiredModelName, Func<string?, CleanupModelTarget> resolve, Func<string, bool> verifyReady, Func<CleanupModelTarget, ILlamaCleanupBackend> backendFactory, Func<ILlamaCleanupBackend, bool, CleanupRunner> runnerFactory, ILogger<CleanupBackendHolder> log)`,
    members `void RequestPrewarm()`, `CleanupBackendLease EnsureCurrent()`, `string? LoadedModelName { get; }`, `void Dispose()`.

- [ ] **Step 1: Write the disposable fake backend**

Create `tests/Winpepper.Cleanup.Tests/Fakes/DisposableFakeBackend.cs`:

```csharp
namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>
/// Fake backend for CleanupBackendHolder tests: records disposal so tests can
/// prove replaced/unused backends are freed (the real LlamaCleanupBackend
/// holds native LLamaWeights).
/// </summary>
internal sealed class DisposableFakeBackend : ILlamaCleanupBackend, IDisposable
{
    private volatile bool _disposed;

    public bool Disposed => _disposed;

    public Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
        => Task.FromResult("cleaned");

    public void Dispose() => _disposed = true;
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup.Tests.Fakes;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CleanupBackendHolderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delegate-injected harness. The resolve map: any raw name resolves to
    /// itself (null -> "model-default"); "model-b" carries PromptFormat
    /// "granite" + OmitPromptExample true so format propagation is observable.
    /// The backend factory can be gated to simulate a slow GGUF load.
    /// </summary>
    private sealed class Harness
    {
        public volatile string? Desired;
        public volatile bool VerifyResult = true;
        public int VerifyCalls;
        public int BackendFactoryCalls;
        public Exception? ThrowOnNextBackendConstruction;
        public ManualResetEventSlim? FactoryGate; // when set, the factory blocks until it is released
        public readonly List<CleanupModelTarget> FactoryTargets = new();
        public readonly List<bool> RunnerOmitFlags = new();
        public readonly List<DisposableFakeBackend> Backends = new();
        public readonly CollectingLogger<CleanupBackendHolder> Log = new();
        public CleanupBackendHolder Holder { get; }

        public Harness()
        {
            Holder = new CleanupBackendHolder(
                desiredModelName: () => Desired,
                resolve: raw => new CleanupModelTarget(
                    GgufPath: $"/tmp/{raw ?? "model-default"}.gguf",
                    ResolvedName: raw ?? "model-default",
                    FellBackToDefault: false,
                    PromptFormat: raw == "model-b" ? "granite" : "chatml",
                    OmitPromptExample: raw == "model-b"),
                verifyReady: _ =>
                {
                    Interlocked.Increment(ref VerifyCalls);
                    return VerifyResult;
                },
                backendFactory: target =>
                {
                    FactoryGate?.Wait(Timeout);
                    Interlocked.Increment(ref BackendFactoryCalls);
                    var pendingThrow = Interlocked.Exchange(ref ThrowOnNextBackendConstruction, null);
                    if (pendingThrow is not null) throw pendingThrow;
                    var backend = new DisposableFakeBackend();
                    lock (FactoryTargets)
                    {
                        FactoryTargets.Add(target);
                        Backends.Add(backend);
                    }
                    return backend;
                },
                runnerFactory: (backend, omit) =>
                {
                    lock (RunnerOmitFlags) RunnerOmitFlags.Add(omit);
                    return new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance,
                        omitPromptExample: omit);
                },
                log: Log);
        }

        /// <summary>
        /// Simulate dictations: call the seam until the holder reports the
        /// model loaded, or time out. Each poll is one "dictation".
        /// </summary>
        public CleanupBackendLease DictateUntilLoaded(string resolvedName)
        {
            CleanupBackendLease lease = Holder.EnsureCurrent();
            SpinWait.SpinUntil(() =>
            {
                lease = Holder.EnsureCurrent();
                return string.Equals(lease.LoadedModelName, resolvedName, StringComparison.Ordinal);
            }, Timeout).ShouldBeTrue($"expected {resolvedName} to be adopted within {Timeout}");
            return lease;
        }
    }

    [Fact]
    public void RequestPrewarm_DoesNotSwap_UntilEnsureCurrent()
    {
        var h = new Harness { Desired = "model-a" };

        h.Holder.RequestPrewarm();

        // Only EnsureCurrent (the dictation seam) mutates the live pair.
        h.Holder.LoadedModelName.ShouldBeNull();

        var lease = h.DictateUntilLoaded("model-a");
        lease.Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void EnsureCurrent_WhilePrewarmInFlight_KeepsCurrentForThisDictation()
    {
        var h = new Harness { Desired = "model-a", FactoryGate = new ManualResetEventSlim(false) };
        h.Holder.RequestPrewarm();

        // The load is stuck in the factory: this dictation must proceed
        // without cleanup rather than wait for the load.
        var lease = h.Holder.EnsureCurrent();
        lease.Runner.ShouldBeNull();
        lease.LoadedModelName.ShouldBeNull();

        h.FactoryGate.Set();
        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
    }

    [Fact]
    public void EnsureCurrent_Swap_DisposesOldBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        var first = h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        var second = h.DictateUntilLoaded("model-b");

        second.Runner.ShouldNotBeNull();
        second.Runner.ShouldNotBeSameAs(first.Runner);
        h.Backends[0].Disposed.ShouldBeTrue();   // replaced live backend freed at the seam
        h.Backends[1].Disposed.ShouldBeFalse();  // the new live backend stays alive
    }

    [Fact]
    public void RequestPrewarm_SameModelTwice_LoadsOnce()
    {
        var h = new Harness { Desired = "model-a", FactoryGate = new ManualResetEventSlim(false) };

        h.Holder.RequestPrewarm();
        h.Holder.RequestPrewarm(); // second promote of the same model: no second load

        h.FactoryGate.Set();
        h.DictateUntilLoaded("model-a");
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void Lease_LoadedModelName_IsTheResolvedNameOfTheModelThatRuns()
    {
        var h = new Harness { Desired = "model-b" };
        h.Holder.RequestPrewarm();

        var lease = h.DictateUntilLoaded("model-b");

        // History attribution stamps this value: the actually-used model.
        lease.LoadedModelName.ShouldBe("model-b");
        h.Holder.LoadedModelName.ShouldBe("model-b");
    }

    [Fact]
    public void Swap_ConstructsFreshPairFromTheNewModelsDescriptorValues()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-b");

        // PromptFormat reaches the backend factory; OmitPromptExample reaches
        // the runner factory — both from the NEW model's descriptor.
        h.FactoryTargets[0].PromptFormat.ShouldBe("chatml");
        h.FactoryTargets[1].PromptFormat.ShouldBe("granite");
        h.RunnerOmitFlags[0].ShouldBeFalse();
        h.RunnerOmitFlags[1].ShouldBeTrue();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **build error** `CS0246` on `CleanupBackendHolder` / `CleanupModelTarget`.

- [ ] **Step 4: Write the implementation**

Create `src/Winpepper.Cleanup/CleanupModelTarget.cs`:

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Everything the cleanup-backend holder needs to know about a resolved
/// cleanup model. Field-for-field mirror of
/// <c>Winpepper.Models.CleanupModelResolution</c>, re-declared here because
/// Winpepper.Cleanup deliberately does not reference Winpepper.Models (the
/// established decoupling — History.Lab's rerun service passes plain values
/// too). AppShell maps between the two records.
/// </summary>
public sealed record CleanupModelTarget(
    string? GgufPath,
    string ResolvedName,
    bool FellBackToDefault,
    string PromptFormat,
    bool OmitPromptExample = false);
```

Create `src/Winpepper.Cleanup/CleanupBackendHolder.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Core.Cleanup;

namespace Winpepper.Cleanup;

/// <summary>
/// Per-dictation snapshot handed to the pipeline: the runner to use for THIS
/// dictation (null = no cleanup model available, fall back to the raw
/// transcript) and the resolved name of the model that runner actually wraps —
/// the value history records must stamp.
/// </summary>
public sealed record CleanupBackendLease(CleanupRunner? Runner, string? LoadedModelName);

/// <summary>
/// Owns the live cleanup backend+runner pair and the machinery for swapping
/// them without an app restart (mirror of the ASR live-swap seam,
/// docs/plans/2026-07-23-live-asr-model-swap.md).
///
/// Contract:
/// - <see cref="RequestPrewarm"/> (UI promote callbacks + boot) starts loading
///   the desired model on a background thread: resolve -> hash-verified
///   readiness (per-file size + SHA-256, injected) -> fresh backend + fresh
///   runner from the NEW model's descriptor values (PromptFormat feeds the
///   backend, OmitPromptExample feeds the runner). It NEVER touches the live
///   pair, so the ~1-1.7s GGUF load is not paid by any dictation.
/// - <see cref="EnsureCurrent"/> (pipeline run loop ONLY, once per dictation
///   at the cleanup seam) adopts a COMPLETED pre-warm and swaps; it never
///   loads synchronously. While a load is still in flight the current pair
///   (possibly none) is kept for this dictation and a later dictation swaps.
///
/// SERIALIZED-CALLER INVARIANT (why disposal is safe without an orphan
/// guard): only PipelineHost's run loop calls EnsureCurrent and
/// CleanupRunner.RunAsync, and the loop awaits RunAsync inline (one hotkey
/// event is fully processed before the next is dequeued). Therefore at the
/// EnsureCurrent seam no generation can be in flight on the old backend, and
/// a pre-warmed backend discarded before ever being handed out has no callers
/// at all. LlamaCleanupBackend.Dispose is NOT gated against concurrent
/// GenerateAsync — this invariant is the safety mechanism. AppShell.Dispose
/// must dispose PipelineHost BEFORE this holder for the same reason.
/// </summary>
public sealed class CleanupBackendHolder : IDisposable
{
    private readonly Func<string?> _desiredModelName;
    private readonly Func<string?, CleanupModelTarget> _resolve;
    private readonly Func<string, bool> _verifyReady;
    private readonly Func<CleanupModelTarget, ILlamaCleanupBackend> _backendFactory;
    private readonly Func<ILlamaCleanupBackend, bool, CleanupRunner> _runnerFactory;
    private readonly ILogger<CleanupBackendHolder> _log;

    private readonly object _gate = new();
    private readonly CleanupModelSwapState _swap = new();
    private ILlamaCleanupBackend? _backend;
    private CleanupRunner? _runner;
    private PendingPrewarm? _pending;

    public CleanupBackendHolder(
        Func<string?> desiredModelName,
        Func<string?, CleanupModelTarget> resolve,
        Func<string, bool> verifyReady,
        Func<CleanupModelTarget, ILlamaCleanupBackend> backendFactory,
        Func<ILlamaCleanupBackend, bool, CleanupRunner> runnerFactory,
        ILogger<CleanupBackendHolder> log)
    {
        _desiredModelName = desiredModelName;
        _resolve = resolve;
        _verifyReady = verifyReady;
        _backendFactory = backendFactory;
        _runnerFactory = runnerFactory;
        _log = log;
    }

    /// <summary>Resolved name of the currently live model; null until first adoption.</summary>
    public string? LoadedModelName
    {
        get { lock (_gate) return _swap.LoadedModelName; }
    }

    /// <summary>
    /// Start loading the desired model in the background (promote callbacks +
    /// boot). No-op when the desired model is already live or already loading.
    /// </summary>
    public void RequestPrewarm()
    {
        lock (_gate)
        {
            StartPrewarmLocked(_resolve(_desiredModelName()));
        }
    }

    /// <summary>
    /// The per-dictation seam. Adopts a completed pre-warm (swapping and
    /// disposing the replaced backend), never loads synchronously, and returns
    /// the pair to use for THIS dictation.
    /// </summary>
    public CleanupBackendLease EnsureCurrent()
    {
        lock (_gate)
        {
            var target = _resolve(_desiredModelName());
            var pending = _pending;
            var pendingReady = pending is not null
                && string.Equals(pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal)
                && pending.Load is { IsCompletedSuccessfully: true, Result: not null };

            switch (_swap.Plan(target.ResolvedName, pendingReady))
            {
                case CleanupSwapAction.Load:
                case CleanupSwapAction.Swap:
                    var previous = _swap.LoadedModelName;
                    var fresh = pending!.Load.Result!;
                    _pending = null;
                    var old = _backend;
                    _backend = fresh.Backend;
                    _runner = fresh.Runner;
                    _swap.CommitLoad(target.ResolvedName);
                    // Serialized-caller invariant (class doc): no generation is
                    // in flight on the old backend at this seam.
                    DisposeBackend(old);
                    _log.LogInformation(
                        "Cleanup model loaded (swap #{Generation}): {Previous} -> {Model}",
                        _swap.Generation, previous ?? "(none)", target.ResolvedName);
                    break;

                case CleanupSwapAction.KeepCurrent:
                case CleanupSwapAction.CannotStart:
                default:
                    break;
            }

            return new CleanupBackendLease(_runner, _swap.LoadedModelName);
        }
    }

    private void StartPrewarmLocked(CleanupModelTarget target)
    {
        if (string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
            return; // the desired model is already live

        if (_pending is not null
            && string.Equals(_pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal))
        {
            return; // already loading (or loaded and waiting for the seam)
        }

        var captured = target;
        _pending = new PendingPrewarm(captured, Task.Run(() => LoadCore(captured)));
    }

    private PrewarmResult? LoadCore(CleanupModelTarget target)
    {
        try
        {
            var backend = _backendFactory(target);
            try
            {
                var runner = _runnerFactory(backend, target.OmitPromptExample);
                return new PrewarmResult(backend, runner);
            }
            catch
            {
                DisposeBackend(backend);
                throw;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Cleanup model {ModelName} failed to load; keeping the current model.",
                target.ResolvedName);
            return null;
        }
    }

    private void DisposeBackend(ILlamaCleanupBackend? backend)
    {
        try
        {
            (backend as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cleanup backend dispose failed");
        }
    }

    /// <summary>
    /// Dispose the live backend. Caller contract: PipelineHost must already be
    /// disposed (run loop stopped) so no dictation holds a lease.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            DisposeBackend(_backend);
            _backend = null;
            _runner = null;
        }
    }

    private sealed record PendingPrewarm(CleanupModelTarget Target, Task<PrewarmResult?> Load);

    private sealed record PrewarmResult(ILlamaCleanupBackend Backend, CleanupRunner Runner);
}
```

Note: verification (`_verifyReady`), missing-GgufPath handling, `FellBackToDefault` logging, stale-pre-warm disposal, self-heal kicks, failed-load retry, and pending disposal in `Dispose()` are deliberately absent here — Task 5 adds them test-first. The `_verifyReady` field is assigned but unused for now; if `WarningsAsErrors` flags CS0414-style unused-field diagnostics on your build, suppress by referencing it in `LoadCore` only when Task 5 says so — in practice a `readonly` delegate field assigned from a constructor parameter does not trigger a warning.

- [ ] **Step 5: Run the tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```

Expected: summary ends `Errors: 0, Failed: 0`; total grows by 6 over the project's previous Linux-leg count.

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Cleanup/CleanupModelTarget.cs src/Winpepper.Cleanup/CleanupBackendHolder.cs tests/Winpepper.Cleanup.Tests/Fakes/DisposableFakeBackend.cs tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs
git commit -m "$(cat <<'EOF'
feat(cleanup): CleanupBackendHolder — background pre-warm + dictation-boundary swap

Owns the live backend+runner pair. RequestPrewarm loads the promoted model on
a background thread (the GGUF load is never paid by a dictation); EnsureCurrent
— the per-dictation seam on the serialized run loop — adopts a completed
pre-warm, disposes the replaced backend under the documented serialized-caller
invariant, and constructs a fresh backend AND runner from the new model's
PromptFormat/OmitPromptExample. Delegate-injected, Linux-tested.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: CleanupBackendHolder — verification, failure, staleness, and disposal paths

Adds, test-first: hash-verification gating (failed verification keeps the current model and never constructs a backend), missing-GgufPath handling, `FellBackToDefault` warning, disposal of a pre-warmed backend that is replaced/discarded before ever being used, self-heal (a dictation whose desired model differs from the loaded one kicks a background load if none is in flight), retry after a failed load, the pre-warm **warm-up delegate** (ledger A5: the ~1–1.7 s figure is load-only; the first generation pays an additional 266–488 ms, so pre-warm must also run a warm-up before it counts as ready), and pending disposal in `Dispose()` with a **bounded join** of an in-flight load (ledger A10: process exit must not kill a thread mid-native-GGUF-load).

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupBackendHolder.cs` (created in Task 4)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs` (append to the class from Task 4)

**Interfaces:**
- Consumes: everything from Task 4 (same harness; same public API — no signature changes).
- Produces: behavioral guarantees plus ONE backward-compatible API addition — the constructor gains an optional trailing parameter `Func<ILlamaCleanupBackend, CancellationToken, Task>? warmup = null` (Task 8 wires it to `LlamaCleanupBackend.WarmAsync`; Task 4's tests keep compiling unchanged). Task 8 relies on: `EnsureCurrent()` self-heals (so PipelineHost needs no extra pre-warm calls), and `Dispose()` frees both live and pending backends.

- [ ] **Step 1: Write the failing tests**

Append these facts inside `CleanupBackendHolderTests` (same file, after the Task 4 facts):

```csharp
    [Fact]
    public void EnsureCurrent_DesiredDiffersAndNoPrewarm_SelfHealsViaBackgroundLoad()
    {
        // No RequestPrewarm at all (e.g. the model was installed after boot):
        // the seam itself must kick a background load and a later dictation swaps.
        var h = new Harness { Desired = "model-a" };

        var first = h.Holder.EnsureCurrent();
        first.Runner.ShouldBeNull(); // this dictation proceeds raw; no synchronous load

        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
    }

    [Fact]
    public void FailedVerification_KeepsCurrentModel_AndNeverConstructsBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.VerifyResult = false;
        h.Desired = "model-b";
        h.Holder.RequestPrewarm();

        // Wait until at least one model-b verification attempt has run.
        var verifyCallsBefore = h.VerifyCalls;
        SpinWait.SpinUntil(() => h.VerifyCalls > verifyCallsBefore, Timeout).ShouldBeTrue();

        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");   // kept the working model
        lease.Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBe(1);            // model-b backend never constructed
        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout)
            .ShouldBeTrue("failed verification must be logged loudly");
    }

    [Fact]
    public void MissingGgufPath_KeepsCurrentModel_AndNeverConstructsBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "no-gguf"; // the harness resolve below maps this to GgufPath null
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout).ShouldBeTrue();
        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void StalePrewarm_RepromotingTheLoadedModel_DisposesTheUnusedPrewarmedBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        SpinWait.SpinUntil(() => { lock (h.FactoryTargets) return h.Backends.Count == 2; }, Timeout)
            .ShouldBeTrue("model-b pre-warm should construct a backend");

        // The user promotes model-a back before model-b was ever used: the
        // pre-warmed model-b backend must be disposed, never swapped in.
        h.Desired = "model-a";
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Backends[1].Disposed, Timeout)
            .ShouldBeTrue("unused pre-warmed backend must be disposed");
        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");
        h.Backends[0].Disposed.ShouldBeFalse(); // the live backend stays alive
    }

    [Fact]
    public void FailedLoad_IsRetriedByALaterDictation()
    {
        var h = new Harness { Desired = "model-a" };
        h.ThrowOnNextBackendConstruction = new InvalidOperationException("boom");
        h.Holder.RequestPrewarm();

        // First attempt fails (logged); polling the seam retries and succeeds.
        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBeGreaterThanOrEqualTo(2);
        h.Log.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void Dispose_DisposesLiveAndPendingBackends()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        SpinWait.SpinUntil(() => { lock (h.FactoryTargets) return h.Backends.Count == 2; }, Timeout)
            .ShouldBeTrue();

        h.Holder.Dispose();

        SpinWait.SpinUntil(() => h.Backends[0].Disposed && h.Backends[1].Disposed, Timeout)
            .ShouldBeTrue("both the live and the pending pre-warmed backend must be disposed");
    }

    [Fact]
    public void Prewarm_RunsWarmupOnTheNewBackend_BeforeItIsAdoptable()
    {
        DisposableFakeBackend? warmed = null;
        DisposableFakeBackend? made = null;
        var holder = new CleanupBackendHolder(
            desiredModelName: () => "model-a",
            resolve: raw => new CleanupModelTarget(
                GgufPath: "/tmp/model-a.gguf", ResolvedName: "model-a",
                FellBackToDefault: false, PromptFormat: "chatml"),
            verifyReady: _ => true,
            backendFactory: _ => made = new DisposableFakeBackend(),
            runnerFactory: (backend, omit) =>
                new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance, omit),
            log: new CollectingLogger<CleanupBackendHolder>(),
            warmup: (backend, _) =>
            {
                warmed = (DisposableFakeBackend)backend;
                return Task.CompletedTask;
            });

        holder.RequestPrewarm();
        SpinWait.SpinUntil(() => holder.EnsureCurrent().Runner is not null, Timeout)
            .ShouldBeTrue();

        // The warm-up ran inside the pre-warm load task, on the new backend,
        // before the seam could adopt it: a pre-warm is "ready" only after
        // load + warm-up complete (ledger A5).
        warmed.ShouldNotBeNull();
        warmed.ShouldBeSameAs(made);
    }

    [Fact]
    public void UnknownModelName_FallingBackToDefault_LogsWarning()
    {
        var h = new HarnessWithFallback { Desired = "bogus-name" };
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout)
            .ShouldBeTrue("silent registry fallback must be surfaced");
        h.DictateUntilLoaded("model-default");
    }
```

Also make two small harness edits in the same file:

1. In `Harness`'s `resolve` lambda, map the `"no-gguf"` name to a null path — replace the `GgufPath:` line with:

```csharp
                    GgufPath: raw == "no-gguf" ? null : $"/tmp/{raw ?? "model-default"}.gguf",
```

2. Add a second harness for the fallback fact, after the `Harness` class:

```csharp
    /// <summary>Harness whose resolve reports FellBackToDefault for unknown names.</summary>
    private sealed class HarnessWithFallback
    {
        public volatile string? Desired;
        public readonly CollectingLogger<CleanupBackendHolder> Log = new();
        public CleanupBackendHolder Holder { get; }

        public HarnessWithFallback()
        {
            Holder = new CleanupBackendHolder(
                desiredModelName: () => Desired,
                resolve: raw => new CleanupModelTarget(
                    GgufPath: "/tmp/model-default.gguf",
                    ResolvedName: "model-default",
                    FellBackToDefault: raw != "model-default",
                    PromptFormat: "chatml"),
                verifyReady: _ => true,
                backendFactory: _ => new DisposableFakeBackend(),
                runnerFactory: (backend, omit) =>
                    new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance, omit),
                log: Log);
        }

        public void DictateUntilLoaded(string resolvedName) =>
            SpinWait.SpinUntil(() =>
                string.Equals(Holder.EnsureCurrent().LoadedModelName, resolvedName, StringComparison.Ordinal),
                Timeout).ShouldBeTrue();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```

Expected: **build error** on the `warmup:` named argument (e.g. `CS1739: The best overload ... does not have a parameter named 'warmup'` — the ctor parameter does not exist yet; a build error counts as red). If you temporarily comment out only the new warm-up fact to see the behavioral reds, the summary shows `Failed: N` with N ≥ 5 (the self-heal, verification, missing-gguf, staleness, pending-dispose, and fallback-warning facts fail; the failed-load-retry fact may time out — timeouts count as failures) and Task 4's 6 facts stay green; restore the fact before Step 3.

- [ ] **Step 3: Implement the missing behaviors**

In `src/Winpepper.Cleanup/CleanupBackendHolder.cs`, make these five edits:

**(a)** In `EnsureCurrent`, replace the `KeepCurrent`/`CannotStart` branch body (`break;`) with self-heal + stale discard:

```csharp
                case CleanupSwapAction.KeepCurrent:
                case CleanupSwapAction.CannotStart:
                default:
                    if (!string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
                    {
                        // Desired differs from loaded and no completed pre-warm
                        // exists: kick (or retry) a background load so a later
                        // dictation can swap. No-op while one is in flight.
                        StartPrewarmLocked(target);
                    }
                    else
                    {
                        // Desired == loaded: any pending pre-warm is stale.
                        DiscardPendingLocked();
                    }
                    break;
```

**(b)** Replace `StartPrewarmLocked` in full (adds stale discard, failed-load retry, and the fallback warning):

```csharp
    private void StartPrewarmLocked(CleanupModelTarget target)
    {
        if (string.Equals(target.ResolvedName, _swap.LoadedModelName, StringComparison.Ordinal))
        {
            DiscardPendingLocked(); // desired model already live; drop any stale pre-warm
            return;
        }

        if (_pending is not null
            && string.Equals(_pending.Target.ResolvedName, target.ResolvedName, StringComparison.Ordinal))
        {
            if (!_pending.Load.IsCompleted)
                return; // load in flight
            if (_pending.Load is { IsCompletedSuccessfully: true, Result: not null })
                return; // warm and waiting for the seam
            _pending = null; // completed but failed -> retry below
        }

        DiscardPendingLocked(); // a pre-warm for a different model is stale

        if (target.FellBackToDefault)
        {
            _log.LogWarning(
                "Unknown cleanup model requested; using default {DefaultModel}",
                target.ResolvedName);
        }

        var captured = target;
        _pending = new PendingPrewarm(captured, Task.Run(() => LoadCore(captured)));
    }
```

**(c)** In `LoadCore`, insert the readiness gates at the top of the `try` block, BEFORE `var backend = _backendFactory(target);`:

```csharp
            if (target.GgufPath is null)
            {
                _log.LogWarning(
                    "Cleanup model {ModelName} declares no .gguf file; keeping the current model.",
                    target.ResolvedName);
                return null;
            }

            // Hash-verified readiness (per-file size + SHA-256) — a merely
            // present-but-stale/corrupt file must never be swapped in. Runs on
            // this background thread, never on the UI thread or the dictation
            // seam, so a cold multi-second hash is safe here.
            if (!_verifyReady(target.ResolvedName))
            {
                _log.LogWarning(
                    "Cleanup model {ModelName} failed verification (missing/size/SHA-256 mismatch); keeping the current model.",
                    target.ResolvedName);
                return null;
            }
```

**(d)** Add `DiscardPendingLocked` (next to `DisposeBackend`) and call it from `Dispose()` before `DisposeBackend(_backend);`:

```csharp
    private void DiscardPendingLocked()
    {
        var pending = _pending;
        if (pending is null) return;
        _pending = null;
        // The pre-warmed backend was never handed out, so no generation can be
        // in flight on it — dispose as soon as its load completes (it may
        // still be running right now).
        pending.Load.ContinueWith(
            t =>
            {
                if (t is { IsCompletedSuccessfully: true, Result: not null })
                    DisposeBackend(t.Result.Backend);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
```

and in `Dispose()`:

```csharp
    public void Dispose()
    {
        lock (_gate)
        {
            // Bounded join of an in-flight pre-warm BEFORE discarding: at app
            // exit, ExitProcess terminating a thread mid-native-GGUF-load risks
            // a loader/driver-lock deadlock during DLL_PROCESS_DETACH (ledger
            // A10). A pending load at quit is rare; worst case this waits one
            // load + warm-up. Wait() throwing (faulted/canceled) means the
            // task is terminal — exactly what we need — so it is swallowed.
            try { _pending?.Load.Wait(TimeSpan.FromSeconds(5)); } catch { }
            DiscardPendingLocked();
            DisposeBackend(_backend);
            _backend = null;
            _runner = null;
        }
    }
```

**(e)** Add the warm-up delegate. Append an optional trailing constructor parameter (after `log`) plus a matching `readonly` field, assigned in the constructor body:

```csharp
        Func<ILlamaCleanupBackend, CancellationToken, Task>? warmup = null
```

```csharp
    private readonly Func<ILlamaCleanupBackend, CancellationToken, Task>? _warmup; // = warmup
```

Then in `LoadCore`, inside the inner `try`, AFTER `var runner = _runnerFactory(backend, target.OmitPromptExample);` and BEFORE `return new PrewarmResult(backend, runner);`, insert:

```csharp
                // Pre-warm is load + WARM-UP: the ~1–1.7 s LoadFromFile figure
                // is load-only; the first generation pays an additional
                // ~0.27–0.49 s (Vulkan shader pipeline + weight paging — ledger
                // A5). Run it here, on the background load task, so a pre-warm
                // is "ready" only when the first real dictation will be fast.
                // A throw is treated as a failed load (backend disposed,
                // retried later); the production delegate
                // (LlamaCleanupBackend.WarmAsync) swallows its own exceptions
                // as non-fatal. Keeping the warm-up inside the load task
                // preserves the disposal discipline: a pending backend is only
                // ever disposed after its load+warm task completes (never with
                // a warm-up in flight — ledger A1b).
                _warmup?.Invoke(backend, CancellationToken.None).GetAwaiter().GetResult();
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
/home/dan/code/winpepper/.dotnet/dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```

Expected: summary ends `Errors: 0, Failed: 0`; all 14 holder facts green.

- [ ] **Step 5: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Cleanup/CleanupBackendHolder.cs tests/Winpepper.Cleanup.Tests/CleanupBackendHolderTests.cs
git commit -m "$(cat <<'EOF'
feat(cleanup): holder failure paths — hash gate, staleness disposal, self-heal, warm-up

Failed SHA-256 verification or a missing gguf keeps the current model (loudly
logged) and never constructs a backend; a pre-warmed backend replaced before
first use is disposed; the seam self-heals by kicking a background load when
desired != loaded with none in flight; failed loads retry; the optional warmup
delegate runs inside the load task so a pre-warm is ready only after load +
first-generation warm-up (ledger A5); Dispose bounded-joins an in-flight
pre-warm (ledger A10) and frees both live and pending backends.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 6: LlamaCleanupBackend disposal hardening + documented invariant (Windows TFM)

`LlamaCleanupBackend.Dispose()` (`src/Winpepper.Cleanup/LlamaCleanupBackend.cs:131-135`) currently disposes `_weights` + `_gate` unconditionally: no idempotence guard, no coordination with in-flight `GenerateAsync`. The holder is now its only production owner and disposes strictly under the serialized-caller invariant, so gating `Dispose` against `_gate` is unnecessary — but the contract must be written down, and double-dispose must be safe. This file is `#if WINDOWS`, so there is no Linux-runnable test; the Windows gate (Task 11) compiles and exercises it via the existing `[Trait("Platform","Windows")]` integration tests.

**Files:**
- Modify: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs:131-135` (plus one field)

**Interfaces:**
- Consumes: nothing new.
- Produces: idempotent `Dispose()` with a documented disposal contract that Task 8's seam and the holder rely on.

- [ ] **Step 1: Apply the change**

In `src/Winpepper.Cleanup/LlamaCleanupBackend.cs`, add a `_disposed` field next to the other private fields (after `private readonly string _promptFormat;`):

```csharp
    private bool _disposed;
```

Replace the existing `Dispose` method:

```csharp
    public void Dispose()
    {
        _weights.Dispose();
        _gate.Dispose();
    }
```

with:

```csharp
    /// <summary>
    /// Disposal contract: NOT gated against a concurrent
    /// <see cref="GenerateAsync"/> — the caller must guarantee quiescence.
    /// In production the only owner is <see cref="CleanupBackendHolder"/>,
    /// which disposes (a) a replaced live backend at the serialized
    /// per-dictation seam (PipelineHost's run loop awaits RunAsync inline, so
    /// no generation is in flight there) and (b) pre-warmed backends that were
    /// never handed out (no callers by construction). Idempotent: safe to call
    /// twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _weights.Dispose();
        _gate.Dispose();
    }
```

- [ ] **Step 2: Full Linux suite (proves the shared net9.0 leg still builds and passes), then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (this file is excluded from the net9.0 leg by `#if WINDOWS`; the Windows gate in Task 11 verifies the Windows TFM).

```bash
git add src/Winpepper.Cleanup/LlamaCleanupBackend.cs
git commit -m "$(cat <<'EOF'
feat(cleanup): idempotent LlamaCleanupBackend.Dispose + documented disposal contract

Dispose is not gated against in-flight GenerateAsync by design; safety comes
from the serialized-caller invariant now written into the doc comment (the
holder disposes only at the per-dictation seam or for never-used pre-warms).
Adds a _disposed guard so double-dispose is safe.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 7: ModelsServices.VerifyCleanupModelReady (Windows-only)

The cleanup analog of `VerifyAsrModelReady` (`src/Winpepper.App/Services/ModelsServices.cs:162-172`): synchronous, name-keyed positive cache (a full multi-hundred-MB SHA-256 on every pre-warm would be wasteful; a negative result is never cached so a completed download is picked up). Uses Task 3's `ModelFilesVerifier` instead of `_coordinator.VerifyReadyAsync` so it never churns the coordinator's global ASR-facing state. It is only ever called from the holder's background pre-warm thread (never the UI thread, never the dictation seam), so a cold multi-second hash is safe. `Winpepper.App` has no test project — verified by the Windows gate + smoke (Task 11).

**Files:**
- Modify: `src/Winpepper.App/Services/ModelsServices.cs` (add one field + one method, directly below `VerifyAsrModelReady`)

**Interfaces:**
- Consumes: `ModelFilesVerifier.VerifyAsync(descriptor, installRoot, ct)` (Task 3); existing `Registry.ResolveOrDefault(name, ModelKind.Cleanup)`, `ModelsRoot`.
- Produces: `public bool VerifyCleanupModelReady(string canonicalName)` — Task 8 wires it into the holder as the `verifyReady` delegate.

- [ ] **Step 1: Apply the change**

In `src/Winpepper.App/Services/ModelsServices.cs`, add directly after the `VerifyAsrModelReady` method:

```csharp
    private string? _verifiedCleanupModelName; // last canonical cleanup name that passed descriptor-level verification

    /// <summary>
    /// Cleanup analog of <see cref="VerifyAsrModelReady"/>: descriptor-level
    /// verified readiness (per-file size + SHA-256) for the CANONICAL cleanup
    /// model name. The positive result is cached per selection change; a
    /// negative result is never cached (missing files short-circuit cheaply,
    /// and the next attempt should pick up a completed download). Deliberately
    /// does NOT route through ModelProvisioningCoordinator.VerifyReadyAsync:
    /// that would churn the coordinator's single global state, which feeds the
    /// ASR startup gate, onboarding, and the Models page. Called only from the
    /// cleanup pre-warm background thread — never from the UI thread or the
    /// dictation seam — so a cold multi-second SHA-256 here is safe.
    /// </summary>
    public bool VerifyCleanupModelReady(string canonicalName)
    {
        if (string.Equals(_verifiedCleanupModelName, canonicalName, StringComparison.Ordinal))
            return true;

        var descriptor = Registry.ResolveOrDefault(canonicalName, ModelKind.Cleanup);
        var ready = ModelFilesVerifier.VerifyAsync(descriptor, ModelsRoot, CancellationToken.None)
                                      .GetAwaiter().GetResult();
        if (ready) _verifiedCleanupModelName = canonicalName;
        return ready;
    }
```

- [ ] **Step 2: Full Linux suite (nothing shared broke), then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Services/ModelsServices.cs
git commit -m "$(cat <<'EOF'
feat(app): ModelsServices.VerifyCleanupModelReady — cached hash-verified readiness

Cleanup analog of VerifyAsrModelReady built on ModelFilesVerifier so cleanup
verification never mutates the coordinator's ASR-facing global state. Positive
result cached per selection change; negatives never cached.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 8: Wire the holder through AppShell and PipelineHost (Windows-only, BOTH dictation paths)

Replace the boot-frozen `CleanupRunner` with the holder end-to-end. AppShell seeds the slot, builds the holder (with the four delegates), kicks a boot pre-warm, and passes the holder to PipelineHost. PipelineHost drops `_cleanup`/`_cleanupModelName` and calls `EnsureCurrent()` once per dictation at the cleanup seam — in BOTH the hold path and the toggle path — and stamps history from the lease. `Winpepper.App` has no test project; correctness is verified by the Windows gate build + smoke checklist (Task 11). Anchors below are line numbers at branch point `dddcc14` — re-locate by content if they have drifted.

Boot-behavior note (intended change): boot no longer blocks ~1–1.7 s on the GGUF load; `RequestPrewarm()` loads in the background. A dictation racing the very first load falls back to the raw transcript for that one dictation and self-heals — the same fallback the app already has when the model file is absent.

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (construction block ~`:166-221`, history name `:245-246`, PipelineHost call `:312-330`, `new AppShell(...)` args ~`:336-342`, ctor params ~`:363`, property assignments ~`:380`, property declarations ~`:52`, `Dispose` ~`:598-608`)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (fields `:52`, `:61`; ctor `:76-132`; HOLD cleanup block `:641-712` + archive `:810-832`; TOGGLE cleanup block `:1039-1110` + archive `:1216`)

**Interfaces:**
- Consumes: `CleanupModelSelectionSlot` (Task 1), `CleanupBackendHolder` / `CleanupModelTarget` / `CleanupBackendLease` (Tasks 4–5), `VerifyCleanupModelReady` (Task 7), existing `CleanupModelPathResolver.Resolve(registry, modelsRoot, requestedName) -> CleanupModelResolution`.
- Produces: `AppShell.CleanupModelSelection` (type `Winpepper.Core.Settings.CleanupModelSelectionSlot`) and `AppShell.CleanupBackend` (type `Winpepper.Cleanup.CleanupBackendHolder`) — Task 9's promote callbacks use exactly these two property names.

- [ ] **Step 1: AppShell — replace the boot-time backend construction with slot + holder**

In `src/Winpepper.App/Hosting/AppShell.cs`:

(a) Delete the `Winpepper.Cleanup.CleanupRunner? cleanup = null;` declaration (`:169`) and the whole cleanup `try { ... } catch` block (`:183-221`, the one that calls `CleanupModelPathResolver.Resolve` and constructs `LlamaCleanupBackend`/`CleanupRunner`). Keep the `correctionStore` try-block (`:173-181`) and the `windowContext` setup untouched.

(b) In place of the deleted block, insert:

```csharp
        // Live cleanup-model swap (mirror of the ASR slot + seam): the holder
        // owns backend+runner construction, hash-verified readiness, pre-warm,
        // and disposal; PipelineHost consumes it once per dictation at the
        // cleanup seam. Boot no longer blocks on the GGUF load —
        // RequestPrewarm loads in the background and a dictation that wins the
        // race falls back to the raw transcript once, then self-heals.
        var cleanupSelection = new Winpepper.Core.Settings.CleanupModelSelectionSlot();
        cleanupSelection.Publish(settings.CleanupModelName); // seed with the persisted boot value
        var cleanupHolder = new Winpepper.Cleanup.CleanupBackendHolder(
            desiredModelName: () => cleanupSelection.Read(),
            resolve: raw =>
            {
                // CleanupModelResolution -> CleanupModelTarget: field-for-field
                // copy (Winpepper.Cleanup does not reference Winpepper.Models).
                var r = Winpepper.Models.CleanupModelPathResolver.Resolve(
                    modelsServices.Registry, modelsServices.ModelsRoot, raw);
                return new Winpepper.Cleanup.CleanupModelTarget(
                    r.GgufPath, r.ResolvedName, r.FellBackToDefault,
                    r.PromptFormat, r.OmitPromptExample);
            },
            verifyReady: name => modelsServices.VerifyCleanupModelReady(name),
            backendFactory: target => new Winpepper.Cleanup.LlamaCleanupBackend(
                target.GgufPath!,
                factory.CreateLogger<Winpepper.Cleanup.LlamaCleanupBackend>(),
                promptFormat: target.PromptFormat),
            runnerFactory: (backend, omit) => new Winpepper.Cleanup.CleanupRunner(
                backend,
                factory.CreateLogger<Winpepper.Cleanup.CleanupRunner>(),
                omitPromptExample: omit),
            log: factory.CreateLogger<Winpepper.Cleanup.CleanupBackendHolder>(),
            // Pre-warm = load + first-generation warm-up (ledger A5): WarmAsync
            // pages in weights + Vulkan shader pipeline and swallows its own
            // failures as non-fatal. Cast is safe by construction: the
            // backendFactory above always constructs LlamaCleanupBackend.
            warmup: (backend, ct) =>
                ((Winpepper.Cleanup.LlamaCleanupBackend)backend).WarmAsync(ct));
        cleanupHolder.RequestPrewarm(); // replaces the old synchronous boot load
```

(The holder guarantees `GgufPath` is non-null before calling `backendFactory` — the `!` is safe by contract.)

(c) Delete the now-dead `var cleanupModelName = settings.CleanupModelName;` (`:246`).

- [ ] **Step 2: PipelineHost — fields and constructor**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

(a) Replace the two fields

```csharp
    private readonly Winpepper.Cleanup.CleanupRunner? _cleanup;        // PLAN2-TYPE
```
(`:52`, keep the comment lines `:53-55` about per-dictation options) and
```csharp
    private readonly string _cleanupModelName;
```
(`:61`) with a single field:

```csharp
    private readonly Winpepper.Cleanup.CleanupBackendHolder _cleanupHolder;
```

(b) In the constructor signature (`:76-99`): replace the parameter `string cleanupModelName,` (`:88`) with `Winpepper.Cleanup.CleanupBackendHolder cleanupHolder,` and DELETE the optional parameter `Winpepper.Cleanup.CleanupRunner? cleanup = null,` (`:93`).

(c) In the constructor body: replace `_cleanupModelName = cleanupModelName;` (`:120`) and `_cleanup = cleanup;` (`:121`) with:

```csharp
        _cleanupHolder = cleanupHolder;
```

- [ ] **Step 3: PipelineHost — HOLD-path cleanup seam (`:641-712` region)**

Immediately BEFORE the `var cleanupOptions = Winpepper.Cleanup.CleanupOptionsFactory.FromSettings(settingsNow);` line (`:651`), insert:

```csharp
                // Per-dictation cleanup-model seam (mirror of TryEnsureAsrModel):
                // adopt a completed pre-warm and swap HERE — never mid-generation.
                // The serialized run loop (await foreach + inline await
                // HandleHotkey) guarantees the previous dictation's RunAsync has
                // completed, so disposing the replaced backend is safe.
                var cleanupLease = _cleanupHolder.EnsureCurrent();
                var cleanupRunner = cleanupLease.Runner;
```

Then, in the same block, replace the three `_cleanup` reads with `cleanupRunner`:
- `:657` `... && _cleanup is not null` → `... && cleanupRunner is not null`
- `:661` `if (!string.IsNullOrWhiteSpace(final) && _cleanup is not null)` → `if (!string.IsNullOrWhiteSpace(final) && cleanupRunner is not null)`
- `:682` `var result = await _cleanup.RunAsync(` → `var result = await cleanupRunner.RunAsync(`

And replace the attribution arm (`:697`):

```csharp
                            _ => _cleanupModelName,
```
with:
```csharp
                            _ => cleanupLease.LoadedModelName ?? "",
```

(This also fixes the latent attribution bug: history now stamps the RESOLVED name of the model that actually ran, not the boot-time raw settings string. The archive site at `:820` (`CleanupModelName = cleanupUsedModel`) needs no change — `cleanupUsedModel` now carries the lease value. The early-archive sites that hardcode `""` at `:526`/`:924` are correct as-is: no cleanup ran.)

- [ ] **Step 4: PipelineHost — TOGGLE-path cleanup seam (`:1039-1110` region)**

Apply the identical change to the second (`2`-suffixed) path. Immediately BEFORE `var cleanupOptions2 = Winpepper.Cleanup.CleanupOptionsFactory.FromSettings(settingsNow2);` (`:1049`), insert:

```csharp
                    // Per-dictation cleanup-model seam — second (toggle) path;
                    // keep byte-parallel with the hold path above.
                    var cleanupLease2 = _cleanupHolder.EnsureCurrent();
                    var cleanupRunner2 = cleanupLease2.Runner;
```

Then replace:
- `:1055` `... && _cleanup is not null` → `... && cleanupRunner2 is not null`
- `:1059` `if (!string.IsNullOrWhiteSpace(final2) && _cleanup is not null)` → `if (!string.IsNullOrWhiteSpace(final2) && cleanupRunner2 is not null)`
- `:1080` `var result2 = await _cleanup.RunAsync(` → `var result2 = await cleanupRunner2.RunAsync(`
- `:1095` `_ => _cleanupModelName,` → `_ => cleanupLease2.LoadedModelName ?? "",`

After this step, `grep -n "_cleanup\b\|_cleanupModelName" src/Winpepper.App/Hosting/PipelineHost.cs` must return no matches (only `_cleanupHolder` remains).

- [ ] **Step 4b: PipelineHost — record the run-loop join outcome (ledger A2)**

The existing `Dispose()` join is bounded and best-effort — the `Wait` result is discarded, so a caller cannot know whether the loop actually quiesced. In `Dispose()` (~`:1304-1305`), replace:

```csharp
            try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
```

with:

```csharp
            // Record whether the run loop actually quiesced: AppShell disposes
            // the cleanup holder ONLY on a successful join (the wait is
            // bounded/best-effort, so "PipelineHost disposed first" alone does
            // not guarantee no generation is in flight — ledger A2). Wait()
            // throwing means _runTask is terminal (faulted/canceled), which IS
            // a completed join.
            try { RunLoopJoined = _runTask?.Wait(TimeSpan.FromSeconds(2)) ?? true; }
            catch { RunLoopJoined = true; }
```

and add, next to the other public members:

```csharp
    /// <summary>True once Dispose has joined the run loop (or it was never
    /// started / already terminal). When FALSE the loop was orphaned, possibly
    /// mid-dictation: the cleanup holder must NOT be disposed (leak instead —
    /// the process is exiting).</summary>
    public bool RunLoopJoined { get; private set; } = true;
```

- [ ] **Step 5: AppShell — PipelineHost call site, shell surface, dispose ordering**

(a) In the `new PipelineHost(...)` call (`:312-330`): replace the argument `cleanupModelName` (in `historyServices.Archiver, cleanupModelName,` at `:320`) with `cleanupHolder`, and in the line `cleanup, correctionStore, windowContext,` (`:327`) delete the leading `cleanup, ` so it reads `correctionStore, windowContext,` (the positional args still line up with `corrections`/`windowContext` after the deleted ctor param).

(b) Mirror the ASR slot's shell plumbing (pattern at `:52` / `:363` / `:380`) for BOTH new objects:
- Property declarations next to `AsrModelSelection` (`:52`):

```csharp
    public Winpepper.Core.Settings.CleanupModelSelectionSlot CleanupModelSelection { get; }
    public Winpepper.Cleanup.CleanupBackendHolder CleanupBackend { get; }
```

- Add `cleanupSelection, cleanupHolder,` to the `new AppShell(...)` argument list in `Create()` (~`:336-342`), immediately after the `asrSelection` argument.
- Add matching ctor parameters next to `Winpepper.Core.Settings.AsrModelSelectionSlot asrSelection` (~`:363`):

```csharp
        Winpepper.Core.Settings.CleanupModelSelectionSlot cleanupSelection,
        Winpepper.Cleanup.CleanupBackendHolder cleanupHolder,
```

- Add assignments next to `AsrModelSelection = asrSelection;` (~`:380`):

```csharp
        CleanupModelSelection = cleanupSelection;
        CleanupBackend = cleanupHolder;
```

(c) In `AppShell.Dispose()` (~`:598-608`), add — AFTER the line that disposes the Pipeline and before the remaining disposals:

```csharp
        // After Pipeline, and ONLY if its run loop actually joined: then no
        // dictation holds a cleanup lease and disposing the live backend
        // cannot race a generation (serialized-caller invariant). On a
        // timed-out join (loop orphaned, possibly mid-generation — ledger A2)
        // deliberately LEAK the holder: Application.Current.Exit() follows
        // immediately, and a leak is safe where a use-after-free is not.
        if (Pipeline.RunLoopJoined) CleanupBackend.Dispose();
```

- [ ] **Step 6: Full Linux suite (nothing shared broke), then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. (AppShell/PipelineHost are Windows-TFM-only; the Windows gate in Task 11 proves they compile and the smoke checklist proves behavior.)

```bash
git add src/Winpepper.App/Hosting/AppShell.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
feat(app): live cleanup-model swap — holder wired through AppShell + PipelineHost

Replaces the boot-frozen CleanupRunner/_cleanupModelName with
CleanupBackendHolder: AppShell seeds the CleanupModelSelectionSlot, builds the
holder (resolve/verify/backend/runner delegates) and kicks a boot pre-warm;
PipelineHost calls EnsureCurrent() once per dictation at the cleanup seam in
BOTH dictation paths and stamps history from the lease's loaded (resolved)
name — attribution now names the model that actually ran. PipelineHost now
records its bounded run-loop join outcome (RunLoopJoined) and AppShell.Dispose
frees the holder after the pipeline ONLY when the join succeeded — an orphaned
loop leaks the holder instead of racing it (serialized-caller invariant,
ledger A2). The holder pre-warm runs WarmAsync so a swap is ready only after
load + first-generation warm-up (ledger A5).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 9: Promote callbacks publish + pre-warm (Windows-only)

Both cleanup promote sites currently do durability only (settings.json write). Add the in-memory `Publish` (effective immediately) and the pre-warm kick, exactly mirroring the ASR promote at `ModelsPage.xaml.cs:37-42`.

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs:43-47`
- Modify: `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs:69-73`

**Interfaces:**
- Consumes: `AppShell.CleanupModelSelection` and `AppShell.CleanupBackend` (Task 8), reached through the `App.Shell!` ambient singleton (these pages have parameterless ctors; no constructor plumbing).
- Produces: user-facing promote behavior; nothing downstream.

- [ ] **Step 1: ModelsPage**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, replace:

```csharp
            promoteCleanup: name =>
            {
                var shell = App.Shell!;
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            },
```

with:

```csharp
            promoteCleanup: name =>
            {
                var shell = App.Shell!;
                shell.CleanupModelSelection.Publish(name); // effective immediately (next dictation)
                shell.CleanupBackend.RequestPrewarm();     // background load so the next dictation doesn't pay it
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            },
```

- [ ] **Step 2: HistoryDetailPage**

In `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`, replace:

```csharp
            promoteCleanupDefault: name =>
            {
                var shell = App.Shell!;
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            });
```

with:

```csharp
            promoteCleanupDefault: name =>
            {
                var shell = App.Shell!;
                shell.CleanupModelSelection.Publish(name); // effective immediately (next dictation)
                shell.CleanupBackend.RequestPrewarm();     // background load so the next dictation doesn't pay it
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            });
```

- [ ] **Step 3: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml.cs src/Winpepper.App/Views/HistoryDetailPage.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): cleanup promote publishes in-memory + pre-warms the new model

Both promote sites (Models tab, History Lab) now mirror the ASR promote:
Publish to the in-memory slot for immediate effect, RequestPrewarm so the GGUF
load happens in the background, settings write kept for durability only.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 10: Full non-Windows regression suite

- [ ] **Step 1: Run the full Linux suite and record the count**

```bash
cd /home/dan/code/winpepper/.worktrees/cleanup-model-live-swap
./scripts/linux-tests.sh
```

Expected: every project prints `Errors: 0` / `Failed: 0`; final lines show `linux-tests grand total: N tests` with N ≥ baseline (~1387) + 27 new facts (4 slot + 9 decider + 4 verifier + 14 holder — grand total ≈ 1418; record the actual number), then `LINUX SUITE: GREEN`.

- [ ] **Step 2: If red, fix forward before proceeding**

Any failure here is a regression introduced by Tasks 1–9. Diagnose, fix, re-run until GREEN, and amend the fix into a focused commit (`fix(...): ...` with the standard trailer). Do NOT proceed to Task 11 while red.

---

### Task 11: Windows gate + live-swap smoke checklist (Windows-only verification)

`Winpepper.App` has no test project — this task is the verification for Tasks 6–9. It runs from WSL; a Windows host session is NOT required.

- [ ] **Step 1: Precondition gate**

- `./scripts/windows-gate.sh` exists and is executable; the Windows host is reachable via `powershell.exe` interop.
- At least TWO cleanup models installed under `%LOCALAPPDATA%\winpepper\models\cleanup\` (e.g. the default `qwen2.5-0.5b-instruct-q4_k_m` plus `granite-4.0-1b-q4_k_m` — downloadable from the Models tab). Without a second model the Swap branch cannot be smoke-tested end to end.
- A working microphone path for dictation smoke.

- [ ] **Step 2: Run the Windows gate**

```bash
./scripts/windows-gate.sh
```

Expected: `GATE: GREEN` (all 12 project/TFM runs). Known flakes: UNC MSB4025 "retry should be performed" build failures are transient — retry the gate; `Hook_Installs_And_DisposesCleanly` needs an interactive desktop and surfaces as TIMEOUT in headless sessions; some Cleanup model-eval tests are baselined known-failing under the tracked-debt policy (`CleanupKnownFailingBaselineTests`) — neither counts as a new regression.

- [ ] **Step 3: Smoke — promote while idle swaps without restart**

Launch the app on Windows. Dictate once (baseline, current model). Open Models tab → promote the OTHER cleanup model. Wait ~5 s (pre-warm = load + warm-up), dictate again. Open History detail for the new entry.
Expected: the new dictation is cleaned; the history record's cleanup model name is the NEW model's canonical name; no restart happened. Logs contain `Cleanup model loaded (swap #...)`. The post-swap dictation's cleanup latency is comparable to steady state — the first-generation warm-up ran during pre-warm, not on the dictation (ledger A5).

- [ ] **Step 4: Smoke — promote then immediately dictate (pre-warm race)**

Promote back to the first model and dictate IMMEDIATELY (within ~1 s, before the load can finish).
Expected: the dictation completes normally using the PREVIOUS model (history attributes the previous model — attribution is truthful); the NEXT dictation uses the newly promoted model. No hang, no mid-dictation swap.

- [ ] **Step 4b: Smoke — promote DURING an active dictation (concurrent pre-warm)**

Start a long dictation (hold and keep talking ~15 s); while it is still capturing/generating, promote the other cleanup model from the Models tab so the background hash + GGUF load + warm-up overlaps live ASR/cleanup work on the same GPU.
Expected: the in-flight dictation completes normally — no audio stutter, hang, or corrupted cleanup output — and the NEXT dictation uses the new model. This is the empirical check for the structurally-verified concurrent-load safety and the accepted pre-warm-contention decision (ledger A3/A11). If this step shows degradation, the pre-identified fallback is idle-gating `RequestPrewarm` — file it as follow-up work; do not silently accept.

- [ ] **Step 5: Smoke — promote a not-ready model keeps the current one**

Rename one installed model's `.gguf` file aside (e.g. append `.bak` in `%LOCALAPPDATA%\winpepper\models\cleanup\<name>\`), promote that model, dictate twice.
Expected: dictations keep being cleaned by the CURRENT model; the log shows the verification warning (`failed verification` or `declares no .gguf`); nothing crashes. Then restore the file and dictate once — this dictation still uses the CURRENT model; it merely kicks the background retry at the seam (re-verify = full SHA-256 of the restored file, then load + warm-up). Wait ~5 s, dictate again: the swap self-heals — this second post-restore dictation uses the NEW model (same kick-then-adopt cadence as Step 3). Restore everything afterwards.

- [ ] **Step 6: Smoke — prompt-format correctness after swap**

With `granite-4.0-1b-q4_k_m` (PromptFormat "granite") promoted via live swap from the qwen default (PromptFormat "chatml"), dictate a sentence with a deliberate disfluency ("um, so, the meeting is uh tomorrow at three").
Expected: sane cleaned output (no template echo, no worked-example leakage) — proof the fresh backend uses the NEW model's prompt format.

- [ ] **Step 7: Smoke — boot pre-warm**

Restart the app; dictate ~10 s after boot.
Expected: dictation is cleaned (boot `RequestPrewarm` finished); log shows the boot-time `Cleanup model loaded (swap #1)` line.

- [ ] **Step 8: Record the results**

Append a short verification note (gate totals + smoke pass/fail per step) to the final commit message or a follow-up `docs(tests)` commit if any fix-forward commits were needed. Push only when the gate is GREEN and smoke passes (AGENTS.md pre-push rule).

---

## Self-Review

**1. Spec coverage.** Walked the spec top to bottom against the tasks:
- Pre-warm on promote (decision 1): Task 4 (`RequestPrewarm` + never-sync-load `EnsureCurrent`), Task 5 (warm-up delegate — pre-warm is load + first-generation warm-up, ledger A5), Task 9 (callbacks), boot pre-warm in Task 8. ✓
- Swap only at dictation boundary: `EnsureCurrent` is the sole mutation point (Task 4 tests 1–2), called from the serialized loop in both paths (Task 8 Steps 3–4). ✓
- Hash-verified readiness (decision 2), analog of `VerifyAsrModelReady`: Tasks 3, 5(c), 7; failed verification keeps current + logs (Task 5 test + smoke 6). ✓
- New types (slot, decider, holder): Tasks 1, 2, 4–5, mirroring `AsrModelSelectionSlot`/`AsrModelSwapState`. ✓
- Disposal safety without an orphan guard, documented invariant, never-used pre-warm disposal: Tasks 4 (swap dispose), 5 (stale/pending/Dispose with bounded pre-warm join), 6 (idempotent `Dispose` + contract doc), 8 Step 4b/5(c) (join-conditional shutdown dispose — ledger A2). ✓
- AppShell seed/construct/pass, PipelineHost both paths + attribution from loaded name, both promote sites: Tasks 8, 9 — with the explicit "grep for `_cleanup`/`_cleanupModelName` must be empty" check ensuring no third usage is missed. ✓
- Prompt-format correctness (fresh runner alongside fresh backend): holder constructs both per swap (Task 4 test 6), production factories in Task 8 Step 1(b), smoke 7. ✓
- Tests mirror ASR analog coverage, Linux-runnable pure logic, Windows-traited LLamaSharp parts: Tasks 1–5 are untraited net9.0-leg tests; no new real-LLamaSharp test is added (the existing `[Trait("Platform","Windows")]` integration tests plus the smoke checklist cover the Windows-only pieces). ✓
- Repo conventions (linux-tests.sh per commit, windows-gate.sh pre-push, no dotnet test, no bin/obj mixing, Amplifier trailer): Global Constraints + every commit step. ✓

**1b. No silent deferrals.** The holder tests use fakes (`DisposableFakeBackend`, delegate factories) — the production behavior they stand in for (real `LlamaCleanupBackend`/`CleanupRunner` construction and real hash verification) is wired in Task 8 Step 1(b) and Task 7, and proven by observable production outcomes in Task 11 smokes 3–7 (real swap, real race, real verification failure, real prompt-format, real boot pre-warm). No requirement is parked as "known limitation" or "future work"; the UNRESOLVED COVERAGE GAPS section is empty.

**2. Placeholder scan.** No TBD/TODO/"handle edge cases"/"similar to Task N" placeholders; every code step carries complete code; every run step has an exact command and expected output. Task 8 uses `file:line` anchors with content descriptions (the ASR plan's established convention for Windows-only tasks where TDD is impossible) plus verbatim replacement code.

**3. Type consistency.** Cross-checked signatures used across tasks: `CleanupModelSelectionSlot.Publish(string?)/Read()` (T1 = T8 seed = T9 callbacks); `CleanupModelSwapState.Plan(string, bool)/CommitLoad(string)/LoadedModelName/Generation` (T2 = T4 holder usage); `ModelFilesVerifier.VerifyAsync(ModelDescriptor, string, CancellationToken)` (T3 = T7 call); `CleanupModelTarget(string? GgufPath, string ResolvedName, bool FellBackToDefault, string PromptFormat, bool OmitPromptExample)` (T4 record = T8 mapping = T5 harness); holder ctor delegate order `desiredModelName, resolve, verifyReady, backendFactory, runnerFactory, log` with the optional trailing `warmup` added in T5 Step 3(e) (T4 ctor = T4/T5 test harnesses; T5 warm-up fact and T8 construction pass `warmup:` by name); `PipelineHost.RunLoopJoined` (T8 Step 4b = T8 Step 5(c) consumer); `CleanupBackendLease(CleanupRunner? Runner, string? LoadedModelName)` (T4 = T8 `cleanupLease.Runner` / `cleanupLease.LoadedModelName ?? ""`); `VerifyCleanupModelReady(string) -> bool` (T7 = T8 delegate); shell properties `CleanupModelSelection`/`CleanupBackend` (T8 = T9). `CleanupRunner(ILlamaCleanupBackend, ILogger<CleanupRunner>, bool omitPromptExample)` and `LlamaCleanupBackend(string, ILogger<LlamaCleanupBackend>, ..., string promptFormat)` match the existing production signatures. Consistent.

**4. Load-bearing validation amendments (2026-07-28).** The plan's assumptions were surfaced and validated (ledger: `.worktrees/.the-usual-logs/cleanup-model-live-swap/load-bearing-ledger.md`; 7 verified, 2 falsified, 2 accepted). Amendments applied: (a) ledger **A2 falsified** — `PipelineHost.Dispose`'s run-loop join is bounded/best-effort, so Task 8 gained Step 4b (`RunLoopJoined`) and Step 5(c) became join-conditional; (b) ledger **A5 falsified** — the ~1–1.7 s figure is load-only and the first generation pays an extra 266–488 ms, so the holder gained an optional `warmup` delegate (Task 5 Step 3(e) + new test, wired to `WarmAsync` in Task 8 Step 1(b)); (c) ledger **A10 accepted** — holder `Dispose` bounded-joins an in-flight pre-warm; (d) ledger **A11 accepted** — pre-warm-on-promote kept, with the new Task 11 Step 4b promote-mid-dictation smoke as the empirical check (fallback if it degrades: idle-gate `RequestPrewarm`). Verified with no plan change needed: serialized-caller invariant on all exit paths (A1, pinned to LLamaSharp 0.27.0), concurrent Vulkan load safety + dual-residency headroom (A3/A4), installed-file hashes match the registry incl. sotto (A6), verification-cache soundness (A7), promote callbacks are the only post-boot writers + raw seed is safe (A8), resolved-name attribution breaks no consumer (A9).
