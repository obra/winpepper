# Cleanup Settings Honesty + CPU-Pegged Pill Indicator Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make the Cleanup settings UI honest about which settings the active
cleanup model can actually use (raw-io models discard the system prompt), gate
window-context prefetch on the same fact, and show a CPU-pegged indicator on
the status pill when system CPU is >= 75% at dictation start.

**Architecture:** Two independent features on one branch. Feature 1 adds a
single-source-of-truth capability helper (`PromptFormatCapabilities`) in
`Winpepper.Cleanup`, consumed by (a) a pure prefetch gate used by
`PipelineHost`'s two hotkey arms and (b) a `Func<bool>` delegate injected into
`CleanupSettingsViewModel` (Core has zero project references, so it can only
take delegates). Feature 2 adds a pure `CpuPeggedPolicy` in Core, near-start
sampling orchestrated inside `SessionViewModel.Tick()` (driven by the pill's
existing 100 ms tick — no new timers), a `cpu_pegged=` timing-line field, and
a Windows-only glyph on the status pill.

**Tech Stack:** C# / .NET 9, WinUI 3 (Windows-only App project), xUnit v3 +
Shouldly (Linux-run tests), hand-rolled INotifyPropertyChanged (no MVVM
toolkit, no DI container — everything is wired in `AppShell.Create()`).

## Global Constraints

- Base: `main` @ `96edc54`. Work only inside the worktree
  `/home/dan/code/winpepper/.worktrees/cleanup-honesty-cpu-pegged`
  (branch `feat/cleanup-honesty-cpu-pegged`).
- Linux suite green before EVERY commit: `./scripts/linux-tests.sh`
  (expect final line `LINUX SUITE: GREEN`). NEVER use `dotnet test`.
- Full Windows gate before done: `./scripts/windows-gate.sh` (expect
  `GATE: GREEN`; UNC MSB4025 + vsock interop failures are known transient
  flakes — retry). Never mix Linux- and Windows-side builds in the same
  `bin`/`obj`.
- Every commit carries: `Co-authored-by: Amplifier <amplifier@users.noreply.github.com>`
- Do NOT push to origin. Leave the branch local.
- CPU-pegged threshold is a named constant: `SystemCpuPeggedThresholdPercent = 75`.
- No new settings. Do NOT delete the window-context/OCR code paths. Do NOT
  change prefetch dispatch timing, pacing, or ASR/wedge paths.
- Disabled UI fields keep their stored values — nothing is erased.
- `Winpepper.Core` has ZERO project references — new VM/SessionViewModel
  dependencies must be delegates or Core-local types, never `ModelDescriptor`
  or `Winpepper.Cleanup` types.
- README.md is the only end-user markdown doc; docs/plans/ files are
  working/agent docs and are fine.
- On-device visual verification is the owner's step post-install; the FINAL
  commit message must carry the 2-line smoke checklist (Task 11).

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Cleanup/PromptFormatCapabilities.cs` | Create | Single source of truth: does a prompt format carry system-prompt content? |
| `src/Winpepper.Cleanup/WindowContextPrefetchGate.cs` | Create | Pure launch decision for window-context prefetch (settings + format) |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | Consume the gate at both stop arms (~:594-607, ~:1165-1178); stamp `cpu_pegged` in `EmitTimingSummary` (~:1687) |
| `src/Winpepper.App/Hosting/AppShell.cs` | Modify | Hoist slot + resolver above the VMs; wire new delegates into PipelineHost, CleanupSettingsViewModel, SessionViewModel |
| `src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs` | Modify | Expose `PromptSettingsSupported` + `RefreshModelCapabilities()` via injected `Func<bool>` |
| `src/Winpepper.App/Views/CleanupPage.xaml` + `.xaml.cs` | Modify | Gray out profile/custom-prompt/window-context controls + honesty notes |
| `src/Winpepper.App/Views/ModelsPage.xaml.cs` | Modify | Live refresh of the VM on cleanup-model promote (~:43) |
| `src/Winpepper.Core/Diagnostics/CpuPeggedPolicy.cs` | Create | Pure pegged decision: threshold const + `IsPegged(int?)` + tick constant |
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` | Modify | Near-start CPU sampling on the existing tick; `CpuPegged` state |
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` | Modify | `CpuPegged` field + `cpu_pegged=` token after `sys_cpu` |
| `src/Winpepper.App/Views/StatusPillWindow.xaml` + `.xaml.cs` | Modify | Pegged-meter glyph (5th column), driven by the existing 100 ms tick |
| `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` | Modify | Steering-investigation conclusion + max-new-tokens finding + gate results |
| `tests/Winpepper.Cleanup.Tests/PromptFormatCapabilitiesTests.cs` | Create | Format capability truth table |
| `tests/Winpepper.Cleanup.Tests/WindowContextPrefetchGateTests.cs` | Create | Gate truth table |
| `tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs` | Modify | Capability property/refresh tests |
| `tests/Winpepper.Core.Tests/Diagnostics/CpuPeggedPolicyTests.cs` | Create | Threshold boundary tests |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelCpuPeggedTests.cs` | Create | Tick-driven sampling orchestration tests |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` | Modify | Golden line + omission for `cpu_pegged` |

Verified anchors (re-verify against the tree; if code is not at a stated
location, search by symbol under `src/` only, never `.worktrees/`):

- Format constants: `CleanupPromptFormatter.ChatMl`/`Granite`/`RawIo` at
  `src/Winpepper.Cleanup/CleanupPromptFormatter.cs:31/35/43`; raw-io build arm
  at `:101-109` uses ONLY the transcript (`"### Input:\n" + rawTranscript ...`).
- `ModelDescriptor.PromptFormat` at `src/Winpepper.Models/ModelDescriptor.cs:33`
  (default `"chatml"`); the only raw-io model is `sotto-cleanup-lfm25-350m-q8_0`
  (`ModelRegistry.cs:~147`).
- Prefetch gate sites: `src/Winpepper.App/Hosting/PipelineHost.cs:594-607`
  (hold arm, locals `ctxPrefetch`/`settingsAtStop`) and `:1165-1178` (toggle
  arm, locals `ctxPrefetch2`/`settingsAtStop2`).
- VM construction: `AppShell.Create()` at `src/Winpepper.App/Hosting/AppShell.cs:157-159`;
  slot + holder at `:192-221`; `new PipelineHost(...)` at `:312-330`
  (20 positional then 4 named args); `sessionVm` at `:108-109`.
- CPU sampler: `Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes()`
  (`src/Winpepper.Platform/Diagnostics/ProcessResourceSampler.cs:35`, static,
  delta-based, returns `null` off-Windows). Pure math:
  `DictationTimingSummary.SystemCpuPercent(idleDelta, kernelDelta, userDelta)`
  at `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs:169`, returns `int?`.
- Max-new-tokens on raw-io (VERIFY resolved during planning): the setting is a
  cap that is FLOOR-overRIDDEN — `LlamaCleanupBackend.ApplyMinNewTokensFloor`
  is `Math.Max(maxNewTokens, plan.MinNewTokensFloor)` with raw-io floor 900
  (`CleanupPromptFormatter.cs:46`), so effective tokens =
  `max(900, min(clamp(setting,64,4096), ceil(chars*2)))`. At the default 512
  the setting is inert under raw-io; it only matters if raised above 900 with
  transcripts > 450 chars. Treatment: the MaxNewTokens slider stays ENABLED
  (it can still take effect above 900) and the finding is recorded in the
  evidence doc (Task 6). Do NOT touch the floor.

---

### Task 1: `PromptFormatCapabilities` (single source of truth)

**Files:**
- Create: `src/Winpepper.Cleanup/PromptFormatCapabilities.cs`
- Test: `tests/Winpepper.Cleanup.Tests/PromptFormatCapabilitiesTests.cs`

**Interfaces:**
- Consumes: `CleanupPromptFormatter.ChatMl` / `.Granite` / `.RawIo` public
  `const string`s (`src/Winpepper.Cleanup/CleanupPromptFormatter.cs:31/35/43`).
- Produces: `public static bool PromptFormatCapabilities.CarriesSystemPrompt(string? promptFormat)`
  — `false` only for `"raw-io"` (case-sensitive Ordinal, matching
  `CleanupPromptFormatter.Validate`'s case-sensitivity); `true` for chatml,
  granite, null, and unknown strings (conservative: only claim "ignores
  settings" when we know it does). Used by Tasks 2, 3, and 5.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Cleanup.Tests/PromptFormatCapabilitiesTests.cs`
(match the style of `CleanupPromptFormatterTests.cs` in the same directory:
xUnit v3 + Shouldly, file-scoped namespace):

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class PromptFormatCapabilitiesTests
{
    [Theory]
    [InlineData("chatml")]
    [InlineData("granite")]
    public void Chat_Formats_Carry_System_Prompt(string format)
        => PromptFormatCapabilities.CarriesSystemPrompt(format).ShouldBeTrue();

    [Fact]
    public void RawIo_Does_Not_Carry_System_Prompt()
        => PromptFormatCapabilities.CarriesSystemPrompt(CleanupPromptFormatter.RawIo)
            .ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-future-format")]
    [InlineData("RAW-IO")] // case-sensitive on purpose, matching Validate()
    public void Unknown_Or_Null_Formats_Are_Treated_As_Carrying(string? format)
        => PromptFormatCapabilities.CarriesSystemPrompt(format).ShouldBeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (from the worktree root; same build/run pattern `scripts/linux-tests.sh` uses):
```bash
cd /home/dan/code/winpepper/.worktrees/cleanup-honesty-cpu-pegged
dotnet build tests/Winpepper.Cleanup.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0103: The name 'PromptFormatCapabilities' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Cleanup/PromptFormatCapabilities.cs`:

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Single source of truth for what a cleanup model's prompt format can
/// actually use. 'chatml' and 'granite' are chat formats whose prompts carry
/// a SYSTEM section (cleanup profile, custom prompt, corrections vocabulary,
/// window context). 'raw-io' is a bare completion format:
/// CleanupPromptFormatter.Build's raw-io arm builds the prompt from ONLY the
/// transcript and structurally discards the system prompt — empirically
/// confirmed 2026-07-30/31 (the sotto model ignores every in-prompt steering
/// channel). Consumed by both the settings UI (via a delegate wired in
/// AppShell) and PipelineHost's window-context prefetch gate, so the UI and
/// the runtime can never disagree.
/// Unknown/null formats are treated as carrying — we only claim a setting is
/// ignored when we know the format discards it.
/// </summary>
public static class PromptFormatCapabilities
{
    public static bool CarriesSystemPrompt(string? promptFormat)
        => !string.Equals(promptFormat, CleanupPromptFormatter.RawIo, StringComparison.Ordinal);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Cleanup.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```
Expected: all tests pass, `Failed: 0`.

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Cleanup/PromptFormatCapabilities.cs tests/Winpepper.Cleanup.Tests/PromptFormatCapabilitiesTests.cs
git commit -m "feat(cleanup): add PromptFormatCapabilities — single source of truth for whether a prompt format carries system-prompt content

raw-io (sotto) builds prompts from only the transcript and structurally
discards the system prompt (profile, custom prompt, vocabulary, window
context). chatml/granite/unknown formats are treated as carrying.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 2: `WindowContextPrefetchGate` (pure launch decision)

**Files:**
- Create: `src/Winpepper.Cleanup/WindowContextPrefetchGate.cs`
- Test: `tests/Winpepper.Cleanup.Tests/WindowContextPrefetchGateTests.cs`

**Interfaces:**
- Consumes: `PromptFormatCapabilities.CarriesSystemPrompt(string?)` (Task 1).
- Produces: `public static bool WindowContextPrefetchGate.ShouldPrefetch(bool cleanupEnabled, bool windowContextEnabled, string? activePromptFormat)`
  — used by `PipelineHost` in Task 3.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Cleanup.Tests/WindowContextPrefetchGateTests.cs`:

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class WindowContextPrefetchGateTests
{
    [Fact]
    public void Prefetches_When_Enabled_And_Format_Carries_System_Prompt()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, "chatml").ShouldBeTrue();

    [Fact]
    public void Skips_When_Active_Model_Is_RawIo_Even_With_Settings_On()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, CleanupPromptFormatter.RawIo)
            .ShouldBeFalse();

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Skips_When_Either_Setting_Is_Off(bool cleanupEnabled, bool ctxEnabled)
        => WindowContextPrefetchGate.ShouldPrefetch(cleanupEnabled, ctxEnabled, "chatml")
            .ShouldBeFalse();

    [Fact]
    public void Unknown_Format_Behaves_As_Today_And_Prefetches()
        => WindowContextPrefetchGate.ShouldPrefetch(true, true, null).ShouldBeTrue();
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet build tests/Winpepper.Cleanup.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0103: The name 'WindowContextPrefetchGate' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Cleanup/WindowContextPrefetchGate.cs`:

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Pure launch decision for the window-context prefetch, extracted so
/// PipelineHost's two duplicated hotkey arms share one Linux-tested policy
/// (same pattern as WindowContextStamp / CleanupRunner.Preflight).
/// A raw-io cleanup model discards the system prompt, so gathering window
/// context for it is pure waste (a UIA walk plus waits that can never be
/// consumed) — no prefetch runs while a raw-io model is active. ctx_src is
/// then omitted from the timing line exactly as when the feature is off.
/// </summary>
public static class WindowContextPrefetchGate
{
    public static bool ShouldPrefetch(
        bool cleanupEnabled, bool windowContextEnabled, string? activePromptFormat)
        => cleanupEnabled
           && windowContextEnabled
           && PromptFormatCapabilities.CarriesSystemPrompt(activePromptFormat);
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Cleanup.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Release/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```
Expected: all pass, `Failed: 0`.

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.Cleanup/WindowContextPrefetchGate.cs tests/Winpepper.Cleanup.Tests/WindowContextPrefetchGateTests.cs
git commit -m "feat(cleanup): add WindowContextPrefetchGate — pure prefetch launch decision incl. prompt-format capability

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 3: Wire the runtime gate into PipelineHost (both arms) + AppShell

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (ctor ~:95-117, gate
  sites ~:594-607 and ~:1165-1178)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (~:157-159, ~:192-221, ~:312-330)

**Interfaces:**
- Consumes: `WindowContextPrefetchGate.ShouldPrefetch(bool, bool, string?)`
  (Task 2); `CleanupModelSelectionSlot.Read()`;
  `Winpepper.Models.CleanupModelPathResolver.Resolve(registry, modelsRoot, raw)`.
- Produces: in `AppShell.Create()`, hoisted locals available BEFORE the
  settings-VM construction block (Task 5 reuses both):
  `CleanupModelSelectionSlot cleanupSelection` and
  `Func<string?, Winpepper.Cleanup.CleanupModelTarget> resolveCleanupTarget`.
  New `PipelineHost` ctor param `Func<string?>? activeCleanupPromptFormat = null`.

Note: `Winpepper.App` code is not compiled by the Linux suite (App is
Windows-TFM-only, no test project). The decision logic is already Linux-tested
in Task 2; this task is wiring only, compile-verified by the Windows gate in
Task 11. The Linux suite must still be green before the commit.

- [ ] **Step 1: Hoist the slot and extract the resolver in `AppShell.Create()`**

Today `AppShell.cs:192-193` constructs and seeds the slot, and `:194-206`
passes an inline `resolve:` lambda to `CleanupBackendHolder`. Change:

1. MOVE the two slot lines (currently `:192-193`) to just above the cleanup
   VM construction block (currently `:157`, `var cleanupContract = ...`).
   The slot is a bare `new()` with no dependencies; `settings` is already in
   scope there:

```csharp
// Hoisted above the settings VMs so honesty delegates (cleanup VM, pipeline
// prefetch gate) can close over the same active-model source the cleanup
// call uses.
var cleanupSelection = new Winpepper.Core.Settings.CleanupModelSelectionSlot();
cleanupSelection.Publish(settings.CleanupModelName); // seed with the persisted boot value
```

2. Immediately after those two lines, extract the EXISTING resolve lambda
   body (currently inline at the holder construction, `:196-206`) into a
   named local — move the body VERBATIM, do not rewrite it:

```csharp
Func<string?, Winpepper.Cleanup.CleanupModelTarget> resolveCleanupTarget = raw =>
{
    // ... the existing lambda body moved verbatim from the
    // CleanupBackendHolder(resolve: ...) argument: it calls
    // Winpepper.Models.CleanupModelPathResolver.Resolve(modelsServices.Registry,
    // modelsServices.ModelsRoot, raw) and maps the resulting
    // CleanupModelResolution field-for-field to Winpepper.Cleanup.CleanupModelTarget
    // (GgufPath, ResolvedName, FellBackToDefault, PromptFormat, OmitPromptExample).
};
```

   (`modelsServices` is created at ~`:76`, well before `:157` — in scope.)

3. At the holder construction, replace the inline lambda with the named local:

```csharp
var cleanupHolder = new Winpepper.Cleanup.CleanupBackendHolder(
    desiredModelName: () => cleanupSelection.Read(),
    resolve: resolveCleanupTarget,
    // ... remaining args unchanged ...
```

- [ ] **Step 2: Add the delegate to PipelineHost**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

1. Add a field next to `_settingsProvider` (~`:87`):

```csharp
/// <summary>Prompt format of the ACTIVE cleanup model (slot -> resolver, the
/// same source the cleanup call uses), or null when unknown — null behaves
/// as today (prefetch allowed). See WindowContextPrefetchGate.</summary>
private readonly Func<string?>? _activeCleanupPromptFormat;
```

2. Append an optional ctor parameter at the END of the parameter list
   (~`:95-117`, after the existing optional params) and assign it:

```csharp
Func<string?>? activeCleanupPromptFormat = null
```
```csharp
_activeCleanupPromptFormat = activeCleanupPromptFormat;
```

3. Replace BOTH gate conditionals. Hold arm (~`:594-607`) — change:

```csharp
if (_ctxCoordinator is not null
    && settingsAtStop.CleanupEnabled
    && settingsAtStop.CleanupWindowContextEnabled)
```

to:

```csharp
// Gated on LIVE settings AND the ACTIVE model's prompt format: a raw-io
// model discards the system prompt, so no context gathering runs for it
// (no UIA walk, no waits; ctx_src omitted as when the feature is off).
if (_ctxCoordinator is not null
    && Winpepper.Cleanup.WindowContextPrefetchGate.ShouldPrefetch(
        settingsAtStop.CleanupEnabled,
        settingsAtStop.CleanupWindowContextEnabled,
        _activeCleanupPromptFormat?.Invoke()))
```

Toggle arm (~`:1165-1178`): the identical edit with `settingsAtStop2`.
Keep the existing "1a: launch the window-context prefetch AT STOP" comments.

- [ ] **Step 3: Wire the delegate at the PipelineHost construction site**

At `AppShell.cs` `new PipelineHost(...)` (~`:312-330`), add a named argument
alongside the existing named args (`postPaste:` etc.):

```csharp
activeCleanupPromptFormat: () => resolveCleanupTarget(cleanupSelection.Read()).PromptFormat,
```

This reads the slot (desired model, effective immediately on promote) through
the SAME resolver the holder uses — the format that the very next cleanup
call will run with, not just the persisted settings string.

- [ ] **Step 4: Verify the Linux suite is still green**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` (App project is not built here; full compile
proof comes from the Windows gate in Task 11).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs src/Winpepper.App/Hosting/AppShell.cs
git commit -m "feat(app): gate window-context prefetch on the active cleanup model's prompt format (both arms)

No context gathering (UIA walk, waits) runs while a raw-io model is
active; ctx_src is omitted exactly as when the feature is off. Active
model is read from the selection slot through the same resolver the
cleanup holder uses. Slot + resolver hoisted in AppShell.Create so
settings-VM delegates can share them.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 4: `CleanupSettingsViewModel` capability awareness

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs`

**Interfaces:**
- Consumes: nothing new from other tasks (Core cannot reference
  `Winpepper.Cleanup`; the capability arrives as a delegate).
- Produces (used by Task 5):
  - ctor gains optional third param `Func<bool>? promptSettingsSupported = null`
  - `public bool PromptSettingsSupported { get; }` — `true` when the active
    model's format carries system-prompt content; defaults `true` when no
    delegate was supplied.
  - `public void RefreshModelCapabilities()` — re-reads the delegate; raises
    `PropertyChanged(nameof(PromptSettingsSupported))` only on change.

The VM keeps all stored values (`Profile`, `CustomPrompt`,
`WindowContextEnabled`) untouched regardless of capability — only the new
read-only property changes. Existing ctor callers/tests compile unchanged
because the param is optional.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs`
(inside the existing class, matching its style):

```csharp
    [Fact]
    public void PromptSettingsSupported_Defaults_True_Without_Delegate()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.PromptSettingsSupported.ShouldBeTrue();
    }

    [Fact]
    public void PromptSettingsSupported_Reads_Delegate_At_Construction()
    {
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), _ => { }, () => false);
        vm.PromptSettingsSupported.ShouldBeFalse();
    }

    [Fact]
    public void RefreshModelCapabilities_Raises_Only_On_Change()
    {
        var supported = false;
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), _ => { }, () => supported);
        var raised = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CleanupSettingsViewModel.PromptSettingsSupported))
                raised++;
        };

        vm.RefreshModelCapabilities();          // false -> false: no raise
        raised.ShouldBe(0);

        supported = true;
        vm.RefreshModelCapabilities();          // false -> true: raise
        vm.PromptSettingsSupported.ShouldBeTrue();
        raised.ShouldBe(1);
    }

    [Fact]
    public void Capability_Change_Never_Touches_Stored_Values()
    {
        var supported = true;
        CleanupSettingsContract? last = null;
        var vm = new CleanupSettingsViewModel(
            CleanupSettingsContract.Defaults(), s => last = s, () => supported);
        vm.Profile = "Custom";
        vm.CustomPrompt = "keep me";
        vm.WindowContextEnabled = true;

        supported = false;
        vm.RefreshModelCapabilities();

        vm.Profile.ShouldBe("Custom");
        vm.CustomPrompt.ShouldBe("keep me");
        vm.WindowContextEnabled.ShouldBeTrue();
        last.ShouldNotBeNull();
        last!.CustomPrompt.ShouldBe("keep me");
    }
```

Note: if the existing tests read `vm.Profile`/`vm.CustomPrompt` differently,
mirror their exact accessor style.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (no third ctor param, no `PromptSettingsSupported`).

- [ ] **Step 3: Implement**

In `src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs`:

1. Extend the ctor (currently `:13`):

```csharp
public CleanupSettingsViewModel(
    CleanupSettingsContract initial,
    Action<CleanupSettingsContract> persist,
    Func<bool>? promptSettingsSupported = null)
{
    _state = initial;
    _persist = persist;
    _promptSettingsSupported = promptSettingsSupported;
    _promptSettingsSupportedValue = promptSettingsSupported?.Invoke() ?? true;
}
```
(Preserve whatever the existing ctor body does; only ADD the two new
assignments and the parameter.)

2. Add members (below the existing fields / above `Apply`):

```csharp
/// <summary>Pull delegate wired in AppShell: does the ACTIVE cleanup model's
/// prompt format carry system-prompt content (profile, custom prompt, window
/// context)? Core has no project references, so the capability arrives as a
/// delegate over PromptFormatCapabilities + the selection slot. Null (tests,
/// legacy callers) means "supported".</summary>
private readonly Func<bool>? _promptSettingsSupported;
private bool _promptSettingsSupportedValue;

/// <summary>False while the active cleanup model ignores in-prompt steering
/// (raw-io). The page grays out Profile/CustomPrompt/WindowContext and shows
/// the honesty note. Stored values are never touched.</summary>
public bool PromptSettingsSupported => _promptSettingsSupportedValue;

/// <summary>Re-read the capability delegate; called on page entry and from
/// the Models-page promote callback so the note updates live.</summary>
public void RefreshModelCapabilities()
{
    var next = _promptSettingsSupported?.Invoke() ?? true;
    if (next == _promptSettingsSupportedValue) return;
    _promptSettingsSupportedValue = next;
    Raise(nameof(PromptSettingsSupported));
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: all pass, `Failed: 0`.

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs
git commit -m "feat(core): CleanupSettingsViewModel exposes PromptSettingsSupported via injected capability delegate

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 5: Model-aware Cleanup page UI + live refresh on promote

**Files:**
- Modify: `src/Winpepper.App/Views/CleanupPage.xaml` (Behavior card ~:20-29,
  Prompt card ~:31-58)
- Modify: `src/Winpepper.App/Views/CleanupPage.xaml.cs`
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` (promoteCleanup ~:43)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (~:157-159)

**Interfaces:**
- Consumes: `CleanupSettingsViewModel.PromptSettingsSupported` /
  `.RefreshModelCapabilities()` (Task 4);
  `PromptFormatCapabilities.CarriesSystemPrompt` (Task 1); the hoisted
  `cleanupSelection` + `resolveCleanupTarget` locals in `AppShell.Create()`
  (Task 3); `AppShell.CleanupVm` (existing, `AppShell.cs:27`).
- Produces: no new public surface.

Windows-only (`Winpepper.App`); verified by compilation through the Windows
gate (Task 11) plus the owner's post-install smoke checklist. The Linux suite
must still be green before the commit.

- [ ] **Step 1: Pass the capability delegate to the VM in AppShell**

At `AppShell.cs` (~`:157-159`; the hoisted `cleanupSelection` /
`resolveCleanupTarget` from Task 3 now sit just above), change:

```csharp
var cleanupVm = new CleanupSettingsViewModel(cleanupContract,
    c => _ = writer.QueueAndFlushAsync(c.ApplyTo));
```

to:

```csharp
var cleanupVm = new CleanupSettingsViewModel(cleanupContract,
    c => _ = writer.QueueAndFlushAsync(c.ApplyTo),
    promptSettingsSupported: () =>
        Winpepper.Cleanup.PromptFormatCapabilities.CarriesSystemPrompt(
            resolveCleanupTarget(cleanupSelection.Read()).PromptFormat));
```

- [ ] **Step 2: Add the honesty notes to CleanupPage.xaml**

Both notes use the page's established advisory pattern
(`CaptionTextBlockStyle` + `TextFillColorSecondaryBrush` + wrap; see the
existing note at `:14-18`). Collapsed by default; code-behind toggles them.

In the **Behavior** card, immediately AFTER the `WindowCtxSwitch`
`ToggleSwitch` (~`:27`):

```xml
<TextBlock x:Name="WindowCtxHonestyNote"
           Text="The selected cleanup model does not read window context. This setting is kept and will apply if you switch to an instruction-style model."
           Style="{ThemeResource CaptionTextBlockStyle}"
           Foreground="{ThemeResource TextFillColorSecondaryBrush}"
           TextWrapping="Wrap"
           Visibility="Collapsed" />
```

In the **Prompt** card, immediately AFTER the card's section-header
`TextBlock` (~`:36`) and BEFORE `ProfileCombo`:

```xml
<TextBlock x:Name="ModelHonestyNote"
           Text="The selected cleanup model was trained to clean transcripts directly and does not read instructions, custom prompts, or window context. These settings are kept and will apply if you switch to an instruction-style model."
           Style="{ThemeResource CaptionTextBlockStyle}"
           Foreground="{ThemeResource TextFillColorSecondaryBrush}"
           TextWrapping="Wrap"
           Margin="0,0,0,8"
           Visibility="Collapsed" />
```

- [ ] **Step 3: Wire enable-state + notes in CleanupPage.xaml.cs**

The page is imperative seed-then-subscribe (no bindings). Add to the class
(inside the existing `#if WINDOWS` region):

```csharp
private CleanupSettingsViewModel? _vm;

private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
{
    if (e.PropertyName == nameof(CleanupSettingsViewModel.PromptSettingsSupported))
        ApplyModelCapabilities();
}

private void ApplyModelCapabilities()
{
    if (_vm is not { } vm) return;
    var supported = vm.PromptSettingsSupported;
    // Gray out (never hide, never clear) the channels a raw-io model ignores.
    ProfileCombo.IsEnabled = supported;
    CustomPromptBox.IsEnabled = supported;
    WindowCtxSwitch.IsEnabled = supported;
    ModelHonestyNote.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
    WindowCtxHonestyNote.Visibility = supported ? Visibility.Collapsed : Visibility.Visible;
}
```

At the END of `OnNavigatedTo` (after the existing control wiring), add:

```csharp
_vm = vm;
vm.RefreshModelCapabilities();   // selection may have changed while away
ApplyModelCapabilities();
// -=/+= pair keeps re-navigation from stacking handlers on the durable VM
// (the existing control-lambda re-subscription wart is page-local; this VM
// outlives the page, so be exact here).
vm.PropertyChanged -= OnVmPropertyChanged;
vm.PropertyChanged += OnVmPropertyChanged;
```

- [ ] **Step 4: Live refresh from the Models page promote callback**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, the `promoteCleanup` lambda
(~`:43`) currently does Publish → RequestPrewarm → durable write. Add the
refresh right after `Publish` (order matters — the delegate reads the slot):

```csharp
promoteCleanup: name =>
{
    var shell = App.Shell!;
    shell.CleanupModelSelection.Publish(name); // effective immediately (next dictation)
    shell.CleanupVm.RefreshModelCapabilities(); // honesty note tracks the ACTIVE selection live
    shell.CleanupBackend.RequestPrewarm();     // background load so the next dictation doesn't pay it
    _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
},
```
(Only the `RefreshModelCapabilities` line is new; keep the rest verbatim.)

- [ ] **Step 5: Verify the Linux suite is still green, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.App/Views/CleanupPage.xaml src/Winpepper.App/Views/CleanupPage.xaml.cs src/Winpepper.App/Views/ModelsPage.xaml.cs src/Winpepper.App/Hosting/AppShell.cs
git commit -m "feat(app): gray out profile/custom-prompt/window-context with an honesty note while a raw-io cleanup model is active

Controls disable (not hide) and keep stored values; the note updates
live on model promote and on tab entry. Capability comes from
PromptFormatCapabilities via the selection slot + resolver — the same
source the runtime prefetch gate uses.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 6: Evidence doc — steering conclusion + max-new-tokens finding

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append a
  new `##` section at the end, after `## Post-install validation expectations`;
  match the doc's style: em-dash headings, ~73-col wrapped bullets, backticked
  `file.cs:NN` pointers)

**Interfaces:**
- Consumes: the verdicts established in Tasks 1-5.
- Produces: documentation only.

- [ ] **Step 1: Append the section**

Append to `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`:

```markdown
## Raw-io steering investigation — UI honesty follow-up (2026-07-31)

- Empirically confirmed 2026-07-30/31: the sotto raw-io model ignores
  every in-prompt steering channel (context blocks, vocabulary hints,
  few-shot); its training data contains no context fields. Experiments
  at `~/models-work/dbg/steer2/`.
- Structural cause: `CleanupPromptFormatter.Build`'s raw-io arm
  (`CleanupPromptFormatter.cs:101-109`) builds the prompt from ONLY the
  transcript — the system prompt assembled by `PromptBuilder.BuildSystem`
  (profile, custom prompt, corrections vocabulary, window context) is
  discarded.
- The UI now reflects it: while a raw-io model is active, the Cleanup
  tab grays out profile / custom prompt / window context with a
  plain-language note (`PromptFormatCapabilities`, single source of
  truth), and PipelineHost's window-context prefetch is gated on the
  same capability (`WindowContextPrefetchGate`) — no UIA walk runs for
  a model that cannot consume it.
- Max-new-tokens under raw-io: the setting is a cap that the raw-io
  floor overrides — `LlamaCleanupBackend.ApplyMinNewTokensFloor` is
  `Math.Max(cap, 900)` (`RawIoMinNewTokensFloor`,
  `CleanupPromptFormatter.cs:46`), so effective tokens =
  `max(900, min(clamp(setting,64,4096), ceil(chars*2)))`. At the
  shipped default 512 the setting is inert under raw-io; it takes
  effect only above 900 with transcripts > ~450 chars. The slider
  therefore stays ENABLED. Still fully effective under raw-io:
  cleanup enabled, timeout, model selection.
```

- [ ] **Step 2: Run the Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN (docs-only, still binding)
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — raw-io steering conclusion, UI honesty, max-new-tokens floor interaction

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 7: `CpuPeggedPolicy` (pure decision)

**Files:**
- Create: `src/Winpepper.Core/Diagnostics/CpuPeggedPolicy.cs`
- Test: `tests/Winpepper.Core.Tests/Diagnostics/CpuPeggedPolicyTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces (used by Tasks 8-10):
  - `public const int CpuPeggedPolicy.SystemCpuPeggedThresholdPercent = 75`
  - `public const int CpuPeggedPolicy.SampleAfterTicks = 4` — evaluate on the
    4th 100 ms pill tick (~400 ms after recording start; indicator appears
    well within ~1 s of the pill showing)
  - `public static bool CpuPeggedPolicy.IsPegged(int? systemCpuPercent)`
    (int? matches `DictationTimingSummary.SystemCpuPercent`'s return type;
    null → false)

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Diagnostics/CpuPeggedPolicyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class CpuPeggedPolicyTests
{
    [Theory]
    [InlineData(75, true)]   // at the threshold counts as pegged
    [InlineData(76, true)]
    [InlineData(100, true)]
    [InlineData(74, false)]
    [InlineData(0, false)]
    public void IsPegged_Compares_Against_The_Named_Threshold(int pct, bool expected)
        => CpuPeggedPolicy.IsPegged(pct).ShouldBe(expected);

    [Fact]
    public void No_Reading_Is_Not_Pegged()
        => CpuPeggedPolicy.IsPegged(null).ShouldBeFalse();

    [Fact]
    public void Threshold_Is_75_Percent()
        => CpuPeggedPolicy.SystemCpuPeggedThresholdPercent.ShouldBe(75);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0103: The name 'CpuPeggedPolicy' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Diagnostics/CpuPeggedPolicy.cs`:

```csharp
namespace Winpepper.Core.Diagnostics;

/// <summary>
/// Pure decision for the status pill's "CPU pegged" indicator: is the
/// machine's overall CPU busy enough around dictation start that this
/// dictation may be slower? Sampling lives in SessionViewModel (riding the
/// pill's existing 100 ms tick); the sampler is
/// ProcessResourceSampler.SystemTimes and the percent math is
/// DictationTimingSummary.SystemCpuPercent. Linux-tested by design.
/// </summary>
public static class CpuPeggedPolicy
{
    /// <summary>Overall system CPU %, at or above which we show the meter.</summary>
    public const int SystemCpuPeggedThresholdPercent = 75;

    /// <summary>Evaluate on the Nth 100 ms pill tick after recording starts
    /// (~400 ms window — short enough that the indicator appears well within
    /// ~1 s of the pill showing, long enough for a stable GetSystemTimes delta).</summary>
    public const int SampleAfterTicks = 4;

    public static bool IsPegged(int? systemCpuPercent)
        => systemCpuPercent is { } pct && pct >= SystemCpuPeggedThresholdPercent;
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: all pass, `Failed: 0`.

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.Core/Diagnostics/CpuPeggedPolicy.cs tests/Winpepper.Core.Tests/Diagnostics/CpuPeggedPolicyTests.cs
git commit -m "feat(core): add CpuPeggedPolicy — 75% system-CPU threshold for the pill's pegged indicator

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 8: Near-start CPU sampling in `SessionViewModel` (tick-driven)

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (Recording arm
  ~:468-477, `Tick()` ~:438-441, fields region)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (sampler wiring, after
  `sessionVm` at ~:108-109)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelCpuPeggedTests.cs`

**Interfaces:**
- Consumes: `CpuPeggedPolicy.IsPegged` / `.SampleAfterTicks` (Task 7);
  `DictationTimingSummary.SystemCpuPercent(long, long, long)` (existing,
  `DictationTimingSummary.cs:169`);
  `ProcessResourceSampler.SystemTimes()` (existing, App-side wiring only —
  Core never references Platform).
- Produces (used by Tasks 9-10):
  - `public Func<(long Idle100ns, long Kernel100ns, long User100ns)?>? SystemTimesSampler { get; set; }`
    — settable delegate property (no ctor churn across the 13 existing
    construction sites); returns the raw cumulative GetSystemTimes values or
    null when unavailable.
  - `public bool? CpuPegged { get; }` — null until decided / when no reading;
    then fixed for the rest of the dictation (the pill's lifetime); reset when
    the next recording starts. Backed by a `volatile int` so PipelineHost's
    run loop can read it without tearing.

Design notes: the pill's existing 100 ms `DispatcherTimer` calls `_vm.Tick()`
while recording, so `Tick()` is the sanctioned no-new-timers hook. Both the
Recording arm (via `_ui.Post`) and `Tick()` (DispatcherTimer) run on the UI
thread — the fields need no locking; only the cross-thread READ from
PipelineHost needs the volatile backing. Do NOT reuse PipelineHost's
`_sysTimesAtStart` baseline (private, different lifetime, ordering vs the UI
hop not guaranteed) — the VM takes its own baseline through the SAME sampler
mechanism (no second sampling mechanism is added).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelCpuPeggedTests.cs`
(construction idiom copied from the existing SessionViewModel tests: real
`SessionEngine` + `SynchronousUiThread`, drive stages via `engine.Apply`):

```csharp
using Shouldly;
using Winpepper.Core.Diagnostics;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class SessionViewModelCpuPeggedTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    // GetSystemTimes semantics: kernel INCLUDES idle. busy = (kernel-idle)+user.
    // Baseline (0,0,0) -> sample (idle,kernel,user) gives pct = busy*100/total.
    private static (long, long, long) Sample(long idle, long kernel, long user)
        => (idle, kernel, user);

    [Fact]
    public void Pegged_When_Busy_At_Or_Above_Threshold_On_The_Sample_Tick()
    {
        var (vm, engine) = NewVm();
        var samples = new Queue<(long, long, long)?>(new (long, long, long)?[]
        {
            Sample(0, 0, 0),          // baseline at recording start
            Sample(25, 100, 0),       // busy = 75, total = 100 -> 75% (at threshold)
        });
        vm.SystemTimesSampler = () => samples.Dequeue();

        engine.Apply(SessionEvent.StartRequested);   // -> Recording, takes baseline
        vm.CpuPegged.ShouldBeNull();                 // no decision yet

        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks - 1; i++) vm.Tick();
        vm.CpuPegged.ShouldBeNull();                 // still inside the window

        vm.Tick();                                   // tick #4: sample + decide
        vm.CpuPegged.ShouldBe(true);
    }

    [Fact]
    public void Not_Pegged_Below_Threshold_And_Decision_Sticks_For_The_Dictation()
    {
        var (vm, engine) = NewVm();
        var calls = 0;
        vm.SystemTimesSampler = () =>
        {
            calls++;
            return calls == 1 ? Sample(0, 0, 0) : Sample(90, 100, 0); // busy=10 -> 10%
        };

        engine.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(false);

        var callsAtDecision = calls;
        vm.Tick();
        vm.Tick();
        calls.ShouldBe(callsAtDecision);             // decided once, never resampled
        vm.CpuPegged.ShouldBe(false);
    }

    [Fact]
    public void No_Sampler_Or_No_Reading_Leaves_CpuPegged_Null()
    {
        var (vmNoSampler, engine1) = NewVm();
        engine1.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks + 2; i++) vmNoSampler.Tick();
        vmNoSampler.CpuPegged.ShouldBeNull();

        var (vmNullReading, engine2) = NewVm();
        vmNullReading.SystemTimesSampler = () => null; // off-Windows / API failure
        engine2.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks + 2; i++) vmNullReading.Tick();
        vmNullReading.CpuPegged.ShouldBeNull();
    }

    [Fact]
    public void Next_Recording_Resets_The_Decision()
    {
        var (vm, engine) = NewVm();
        var q = new Queue<(long, long, long)?>(new (long, long, long)?[]
        {
            Sample(0, 0, 0), Sample(0, 100, 100),    // dictation 1: busy=200/total=200 -> 100%
            Sample(0, 200, 100), Sample(190, 400, 100), // dictation 2: idleΔ=190 kernelΔ=200 userΔ=0 -> busy=10 -> 5%
        });
        vm.SystemTimesSampler = () => q.Dequeue();

        engine.Apply(SessionEvent.StartRequested);
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(true);

        // Walk the engine back to Idle the way the pipeline does, then start again.
        engine.Apply(SessionEvent.StopRequested);
        engine.Apply(SessionEvent.PipelineCompleted);
        engine.Apply(SessionEvent.Dismissed);        // adjust to the engine's actual events if these names differ
        engine.State.ShouldBe(SessionState.Idle);

        engine.Apply(SessionEvent.StartRequested);   // dictation 2
        vm.CpuPegged.ShouldBeNull();                 // reset at recording start
        for (var i = 0; i < CpuPeggedPolicy.SampleAfterTicks; i++) vm.Tick();
        vm.CpuPegged.ShouldBe(false);
    }
}
```

NOTE for the implementer: (a) the exact `SessionEvent` names for walking back
to Idle must be taken from `SessionEngine` (look at how existing
`SessionViewModel*Tests` return to Idle and use those events verbatim);
(b) copy the `using` block (namespaces for `SessionEngine`, `SessionEvent`,
`SessionState`, `SynchronousUiThread`) from an existing
`SessionViewModel*Tests` file — the construction idiom
`new SessionViewModel(engine, new SynchronousUiThread())` is quoted verbatim
from them. Everything else stands as written.

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (no `SystemTimesSampler`, no `CpuPegged`).

- [ ] **Step 3: Implement in SessionViewModel**

1. Add fields + members (near the other fields, top of the class):

```csharp
// --- CPU-pegged near-start sampling (Feature: pill pegged indicator) ---
// Sampling rides the pill's existing 100 ms tick — no new threads/timers.
// All writes happen on the UI thread (Recording arm via _ui.Post, Tick via
// the pill's DispatcherTimer); the volatile int lets PipelineHost's run loop
// read the decision for the timing line without tearing.

/// <summary>Raw cumulative system times (GetSystemTimes semantics), wired in
/// AppShell to ProcessResourceSampler.SystemTimes(). Null delegate or null
/// reading => no decision (CpuPegged stays null, log field omitted).</summary>
public Func<(long Idle100ns, long Kernel100ns, long User100ns)?>? SystemTimesSampler { get; set; }

private (long Idle100ns, long Kernel100ns, long User100ns)? _cpuBaseline;
private int _cpuTicksSinceStart;
private volatile int _cpuPeggedState; // 0=pending 1=no-reading 2=not-pegged 3=pegged

/// <summary>Null until decided (or when no reading was possible); otherwise
/// fixed for the rest of the dictation and shown for the pill's lifetime.</summary>
public bool? CpuPegged => _cpuPeggedState switch { 2 => false, 3 => true, _ => null };
```

2. In `OnEngineStateChanged`'s `case SessionState.Recording:` arm
   (~`:468-477`), immediately after `_stopwatch.Restart();`, add:

```csharp
_cpuBaseline = SystemTimesSampler?.Invoke();
_cpuTicksSinceStart = 0;
_cpuPeggedState = 0;
```

3. In `Tick()` (~`:438-441`), after the existing `ElapsedMs` update, add:

```csharp
if (_cpuPeggedState == 0
    && Stage == SessionStage.Recording
    && ++_cpuTicksSinceStart >= Winpepper.Core.Diagnostics.CpuPeggedPolicy.SampleAfterTicks)
{
    _cpuPeggedState =
        _cpuBaseline is { } s0
        && SystemTimesSampler?.Invoke() is { } s1
        && Winpepper.Core.Diagnostics.DictationTimingSummary.SystemCpuPercent(
               s1.Idle100ns - s0.Idle100ns,
               s1.Kernel100ns - s0.Kernel100ns,
               s1.User100ns - s0.User100ns) is { } pct
            ? (Winpepper.Core.Diagnostics.CpuPeggedPolicy.IsPegged(pct) ? 3 : 2)
            : 1; // evaluated, no reading — never retried, field omitted from the log
}
```

(No `Raise` for `CpuPegged`: the pill reads it on its tick, exactly how
`ElapsedMs`/`InputLevel` are consumed today, and `OnVmChanged` filters to
`Stage`/`StatusText` anyway.)

4. Wire the sampler in `AppShell.Create()` immediately after `sessionVm` is
   constructed (~`:108-109`):

```csharp
// Pegged-indicator sampling reuses the ONE existing sampler mechanism
// (GetSystemTimes via ProcessResourceSampler); returns null off-Windows.
sessionVm.SystemTimesSampler = () =>
    Winpepper.Platform.Diagnostics.ProcessResourceSampler.SystemTimes() is { } s
        ? (s.Idle100ns, s.Kernel100ns, s.User100ns)
        : null;
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: all pass, `Failed: 0`.

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs src/Winpepper.App/Hosting/AppShell.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelCpuPeggedTests.cs
git commit -m "feat(core): near-start system-CPU pegged decision in SessionViewModel, riding the pill's 100 ms tick

Baseline at recording start, one delta sample on tick 4 (~400 ms),
decision fixed for the dictation and reset on the next recording.
Sampler is the existing GetSystemTimes mechanism injected as a delegate.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 9: `cpu_pegged=` on the dictation timing line

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`
  (property near `SysCpuPct` ~:90, `FormatLine()` ~:146)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`
  (`EmitTimingSummary` ~:1687-1701)
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: `SessionViewModel.CpuPegged` (Task 8; PipelineHost already holds
  the VM as `private readonly SessionViewModel _vm;`, `PipelineHost.cs:40`).
- Produces: `public bool? DictationTimingSummary.CpuPegged { get; set; }`;
  timing-line token ` cpu_pegged=true|false` immediately after ` sys_cpu=N`,
  omitted entirely when null — so log lines and what the user saw stay
  consistent (the pill shows the meter iff `cpu_pegged=true`).

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

1. In the golden-line test (~`:64-74`): add `CpuPegged = true` to the arrange
   block (next to where `SysCpuPct` is set) and insert ` cpu_pegged=true`
   into the expected string IMMEDIATELY AFTER ` sys_cpu=37` (before ` total=`).
   The test pins the exact whole string — position matters.

2. Add two new facts to the class (mirroring the `sys_cpu` omission test ~`:339`):

```csharp
    [Fact]
    public void CpuPegged_False_Is_Emitted_Explicitly()
    {
        var t = new DictationTimingSummary { CpuPegged = false };
        t.FormatLine().ShouldContain(" cpu_pegged=false");
    }

    [Fact]
    public void CpuPegged_Null_Omits_The_Field()
    {
        var t = new DictationTimingSummary();
        t.FormatLine().ShouldNotContain("cpu_pegged=");
    }
```

(If `DictationTimingSummary` requires ctor args or required members, copy the
construction idiom from the neighboring omission tests verbatim.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS (`CpuPegged` does not exist).

- [ ] **Step 3: Implement**

1. `DictationTimingSummary.cs` — add next to `SysCpuPct` (~`:90`):

```csharp
public bool? CpuPegged { get; set; }   // pegged decision near recording start (what the pill showed); null = no reading, field omitted
```

2. `FormatLine()` — immediately after `AppendOptNum(sb, "sys_cpu", SysCpuPct);`
   (~`:146`) and before `AppendCoreMs(sb, "total", TotalMs);`, add (the
   `prewarm_active` inline-boolean idiom, `:138-139`):

```csharp
if (CpuPegged is bool pegged)
    sb.Append(" cpu_pegged=").Append(pegged ? "true" : "false");
```

3. `PipelineHost.EmitTimingSummary` (~`:1693-1700`) — stamp next to the other
   emit-time values (after `timing.PrewarmActive = ...`); this single site
   covers both hotkey arms:

```csharp
timing.CpuPegged = _vm.CpuPegged; // same value that drove the pill's pegged meter
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: all pass, `Failed: 0` (including the updated golden line).

- [ ] **Step 5: Run the full Linux suite, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs src/Winpepper.App/Hosting/PipelineHost.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "feat(core): stamp cpu_pegged on the dictation timing line, mirroring the pill's pegged decision

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 10: Pegged-meter indicator on the status pill (Windows-only visual)

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml` (grid ~:12-51)
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` (tick ~:84-106)

**Interfaces:**
- Consumes: `SessionViewModel.CpuPegged` (Task 8).
- Produces: no new public surface.

Windows-only (`#if WINDOWS` file); compile-verified by the Windows gate
(Task 11); visually verified by the owner post-install (smoke checklist in
the final commit message, Task 11).

- [ ] **Step 1: Add the indicator to StatusPillWindow.xaml**

The grid has four columns (Dot / MeterPanel / StatusText / ElapsedText). Add
a fifth `Auto` column definition after the existing four, and the indicator
element after `ElapsedText`. Amber `#F59E0B` is the pill's established "hot"
color (the voice-meter gradient); `Segoe MDL2 Assets` `E9D9` ("Diagnostic")
is a compact gauge/speedometer glyph. Collapsed by default and zero-width
when collapsed, so the pill's fixed `ClientWidthDip = 300` is untouched
(StatusText is the star column and absorbs the width when visible).

```xml
<ColumnDefinition Width="Auto" />
```

```xml
<TextBlock x:Name="PeggedIndicator"
           Grid.Column="4"
           VerticalAlignment="Center"
           FontFamily="Segoe MDL2 Assets"
           FontSize="14"
           Text="&#xE9D9;"
           Foreground="#F59E0B"
           Visibility="Collapsed"
           ToolTipService.ToolTip="System CPU is busy — this dictation may be slower" />
```

- [ ] **Step 2: Drive it from the existing 100 ms tick**

In `StatusPillWindow.xaml.cs`, inside the `_tickTimer.Tick` lambda
(~`:84-106`), immediately after `_vm.Tick();`, add:

```csharp
// Pegged meter: decision is made in SessionViewModel on tick 4 (~400 ms
// after recording start) and stays fixed for the pill's lifetime; read on
// the tick like ElapsedMs/InputLevel (no new notification path, no
// per-tick allocations).
PeggedIndicator.Visibility =
    _vm.CpuPegged == true ? Visibility.Visible : Visibility.Collapsed;
```

Behavior this yields: indicator appears within ~500 ms of the pill showing
(inside the ~1 s budget); it persists through Transcribing/CleaningUp/
Injecting because `CpuPegged` only resets at the NEXT recording start; when a
new dictation starts, `CpuPegged` is null again so the first tick collapses it.

- [ ] **Step 3: Verify the Linux suite is still green, then commit**

```bash
./scripts/linux-tests.sh   # expect LINUX SUITE: GREEN
git add src/Winpepper.App/Views/StatusPillWindow.xaml src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "feat(app): show a pegged-meter glyph on the status pill when system CPU is >=75% at dictation start

Amber gauge glyph in a fifth Auto column, driven by the existing 100 ms
tick — no new timers, no per-tick allocations, hidden when the decision
is absent or below threshold.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 11: Full gates + evidence, final commit with smoke checklist

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append
  gate results to the section added in Task 6, matching the doc's existing
  "Run gates" reporting style)

**Interfaces:**
- Consumes: everything above.
- Produces: the branch's final commit, carrying the owner's 2-line smoke
  checklist in its message.

- [ ] **Step 1: Run the Linux suite**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`. If red, fix (with tests) before proceeding.

- [ ] **Step 2: Run the Windows gate**

```bash
./scripts/windows-gate.sh
```
Expected: exit 0 + `GATE: GREEN`. Known transient flakes (UNC MSB4025 parse
errors, vsock interop failures) — retry the gate on those. Record any honest
skips (e.g. Llama tests self-skip without the GGUF) rather than hiding them.
This can take up to ~60 minutes (40 min build / 20 min test timeouts).

- [ ] **Step 3: Append gate results to the evidence doc**

Append to the `## Raw-io steering investigation — UI honesty follow-up
(2026-07-31)` section of
`docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`:

```markdown
- Gates (2026-07-31, cleanup-honesty + cpu-pegged branch): Linux suite
  GREEN (all 9 projects, `scripts/linux-tests.sh`); Windows gate GREEN
  (`scripts/windows-gate.sh`, 12 project/TFM runs). Record retries or
  honest skips here if any occurred.
- NEW: `cpu_pegged=` on the dictation timing line (after `sys_cpu=`),
  mirroring the pill's pegged-meter decision (>=75% system CPU over the
  first ~400 ms of recording).
```

(Replace the gate sentence with the ACTUAL results observed — do not commit
claimed-green results that were not observed.)

- [ ] **Step 4: Final commit (carries the smoke checklist)**

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — gates green for cleanup honesty + cpu-pegged pill indicator

Post-install smoke checklist (owner, on device):
1. Load the machine >75% CPU, dictate — the amber gauge shows on the pill and the timing line logs cpu_pegged=true; idle machine — no gauge, cpu_pegged=false.
2. Select the Sotto (raw-io) cleanup model — Cleanup tab grays out profile/custom prompt/window context with the note (values preserved); switch to an instruction model — controls re-enable live.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

Do NOT push. The root session merges, gates, and installs.
