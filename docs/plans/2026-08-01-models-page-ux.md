# Models Page UX Overhaul Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Rework the Models page so the streaming model is a registry-driven dropdown like the other cards, the bottom button downloads only selected-and-missing models (and disables when nothing is missing), manual-install-only models get an inline explanation instead of a silent no-op, and the cleanup card grays out with a note when cleanup is disabled.

**Architecture:** All decision logic (which selected models are missing/downloadable, button enable state, manual-install detection, cleanup gate) lives in a new pure static policy class `SelectedModelsPolicy` in `Winpepper.Models` (Linux-tested, following the `CpuPeggedPolicy`/`PromptFormatCapabilities` idiom), plus one extraction-aware installed check `ModelDescriptor.IsFullyInstalledAndExtracted` (Task 1b) so a broken-but-present streaming install counts as missing. `ModelsTabViewModel` gains one new orchestration method `DownloadSelectedAsync` that downloads an explicit descriptor list. The Windows-only `ModelsPage` code-behind stays thin: it gathers inputs from the sources it already uses (verified-ASR flag, card presence checks, `CleanupVm.Enabled`), asks the policy, and applies results to controls imperatively — matching the page's existing imperative style and the CleanupPage `ApplyModelCapabilities` gray-out precedent.

**Tech Stack:** C# / .NET 9, WinUI 3 (`Winpepper.App`, Windows-only, `#if WINDOWS`), xUnit v3 + Shouldly (`tests/Winpepper.Models.Tests`, Linux-run).

## Global Constraints

- Worktree root (all commands run here): `/home/dan/code/winpepper/.worktrees/models-page-ux`, branch `feat/models-page-ux`. All file paths below are relative to this root.
- NEVER read or edit anything under a nested `.worktrees/` directory (stale copies mislead).
- Linux suite green before EVERY commit: `./scripts/linux-tests.sh` (NEVER `dotnet test`). Ends `LINUX SUITE: GREEN` on pass.
- Raw `dotnet` commands (the per-task `dotnet build`/`dotnet exec` steps) require the SDK on PATH first — run once per shell (verified: fresh shells have no `dotnet`): `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"`. The scripts set this up themselves; only raw commands need it.
- Full Windows gate before done and after each task that touches `Winpepper.App`: `./scripts/windows-gate.sh` (use a 20–30 min timeout; UNC `MSB4025` and vsock interop failures are known transient flakes — retry the gate, do not "fix" them). Ends `GATE: GREEN` on pass.
- Never mix Linux- and Windows-side builds in the same `bin/`/`obj/` (clean when switching sides).
- Every commit message ends with the trailer line: `Co-authored-by: Amplifier <amplifier@users.noreply.github.com>`
- Do NOT push to origin — leave the branch local.
- Do NOT change: model selection semantics (promote callbacks publishing to selection slots + durable settings writes), the live-swap/pre-warm machinery, the AssemblyAI key controls, the `StreamingToggle` behavior, or download mechanics (`ModelDownloader`, `ModelsServices.DownloadAsync`, `ModelProvisioningCoordinator`).
- No new settings in `AppSettings`. Streaming model selection is NOT persisted (the registry pins it via `ModelRegistry.StreamingAsrName`; the card's promote callback stays a deliberate no-op).
- AutomationIds are a test-facing contract: `ModelsDownloadButton` must keep its ID. New named controls get new AutomationIds documented in `docs/automation-ids.md`.
- Exact user-facing copy (verbatim, do not restyle):
  - Bottom button label: `Download selected models`
  - Bottom button tooltip: `Downloads the models chosen above that aren't installed yet.`
  - Cleanup-off note: `Cleanup is turned off — enable it in the Cleanup tab to choose a model.`
  - Streaming failed label: `Install failed — use the download button to retry`
- `ModelsPage.xaml.cs` is `#if WINDOWS`-gated: it cannot compile or run on Linux. Windows-gate compiles it; on-device visual verification is the owner's post-install step (spec-approved), recorded as a smoke checklist in the final commit message (Task 6).
- Line numbers cited below are anchors as of `main @ 7345ca1` — re-locate by symbol if drifted. STOP only if the claimed code cannot be found anywhere under `src/` or contradicts in substance.

---

## Verified current-state facts (from code survey @ 7345ca1)

The implementer of each task can rely on these without re-deriving them:

- `ModelKind` (`src/Winpepper.Models/ModelKind.cs`) has exactly `Asr`, `Cleanup`, `StreamingAsr`.
- `ModelRegistry` (`src/Winpepper.Models/ModelRegistry.cs`): `ByKind(ModelKind)`, `Find(string) : ModelDescriptor?`, `All`, constants `DefaultAsrName`, `DefaultCleanupName`, `StreamingAsrName = "nemotron-streaming-en"`. `ResolveOrDefault` THROWS for `ModelKind.StreamingAsr` — never call it for streaming.
- `ModelDescriptor` has `Name`, `DisplayName`, `Kind`, `ManualInstallOnly` (only `sotto-cleanup-lfm25-350m-q8_0` sets it; its file has an empty URL), and `IsFullyInstalled(string installRoot)` (presence + non-empty check).
- `ModelDownloader.DownloadAsync` throws `InvalidOperationException` if a `ManualInstallOnly` descriptor reaches it — download paths must filter these out.
- `ModelsTabViewModel` (`src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`): nested `IDownloader` interface; cards `AsrCard`/`CleanupCard`/`StreamingCard` (each `ModelCardViewModel` built from `registry.ByKind(...)`); `_downloadGate` = `SharedOperationGateFor(downloader)`; private `DownloadOneAsync(ModelDescriptor, CancellationToken)` routes progress to the card matching `d.Kind` (already handles `StreamingAsr`). `StreamingCard`'s promote callback is `_ => { }` (deliberate no-op) and its seed name is `ModelRegistry.StreamingAsrName`.
- `ModelCardViewModel`: `Available` (ObservableCollection, bound as combo ItemsSource with `DisplayMemberPath="DisplayName"`), `SelectedName`, `SelectedDescriptor`, `IsSelectedInstalled`, `CommitSelection()`, `RaiseIsSelectedInstalledChanged()`.
- `ModelsPage.xaml.cs` (`src/Winpepper.App/Views/ModelsPage.xaml.cs`, ~404 lines, all `#if WINDOWS`): `OnNavigatedTo` builds a fresh `ModelsTabViewModel` per navigation (page is NOT cached) and seeds `AsrCombo.SelectedItem`/`CleanupCombo.SelectedItem` imperatively; `UpdateInstalledLabels()` (~:361-389) computes all installed icons/labels imperatively and is called from every state-changing site (navigation seed, both selection handlers, both download handlers, the auto-installer status lambda); `_asrSelectedVerified` is the page's hash-verified ASR installed flag (set off-thread via `ModelsServices.VerifyAsrModelReady`); `OnDownloadMissing` (~:287-329) is the bottom button handler; `OnInstallStreamingModel` (~:331-359) is the streaming install handler; `OnNavigatedFrom` (~:391-402) unsubscribes the auto-installer handler and cancels `_lifetimeCts`.
- There is NO settings change event anywhere. The only live channel for `CleanupEnabled` is `App.Shell.CleanupVm.PropertyChanged` filtered on `nameof(CleanupSettingsViewModel.Enabled)` (`CleanupSettingsViewModel` is a shell singleton in `Winpepper.Core.ViewModels`, exposes `Enabled : bool`). This is the "cheap live-update path" the spec prefers.
- AssemblyAI implies NO downloadable models (`AssemblyAiModels` is remote vendor IDs, outside the registry). The local ASR model remains a required fallback regardless of provider, and it is always "chosen" via `AsrCombo` — so the selected set never needs an AssemblyAI special case.
- Gray-out precedent (`src/Winpepper.App/Views/CleanupPage.xaml.cs`, `ApplyModelCapabilities`, ~:22-32): set `IsEnabled` on interactive controls only (never `Opacity`, never hide, never clear values); notes are `TextBlock` with `CaptionTextBlockStyle` + `TextFillColorSecondaryBrush` + `TextWrapping="Wrap"` + `Visibility="Collapsed"` default; live refresh via `PropertyChanged` subscription with `-=`/`+=` re-subscribe in `OnNavigatedTo`.
- Test idiom (`tests/Winpepper.Models.Tests`): xUnit v3 + Shouldly, file-scoped namespace mirroring the folder, `public class <Type>Tests`, methods named `Pascal_Snake_Case` sentences, `[Theory]`+`[InlineData]` for truth tables, cancellation via `TestContext.Current.CancellationToken`. Pure policy tests carry no traits.
- Known bug being fixed in passing (required for change 1's "live after download" rule): `OnDownloadMissing` discards the post-download verify result, so `_asrSelectedVerified` stays stale and the ASR label can read "Not downloaded" after a successful download.

### Validation findings (load-bearing check, 2026-08-01 — evidence in `.worktrees/.the-usual-logs/models-page-ux/`)

- Both gates verified green at base FROM THIS WORKTREE: `./scripts/linux-tests.sh` (1613 tests, `LINUX SUITE: GREEN`, ~50 s) and `./scripts/windows-gate.sh` (`GATE: GREEN`, ~9.5 min, Winpepper.App XAML compile OK, no flakes). The gate script self-cleans `bin/`/`obj/` before building.
- `ModelDescriptor.IsFullyInstalled` is presence-only — it does NOT check the extracted `runtime/` tree. A broken-but-present streaming install is reachable: `ModelDownloader` moves the verified archive into place (~:184) BEFORE `EnsureExtracted` (~:185), which can throw (locked native DLLs, `TarGzExtractor.cs:47-54`, whose error text says to restart the app; or IO failure mid-extract); the `runtime/` tree can also be deleted post-install. That state reads "Installed" under the weak check while streaming is actually broken. This is why the plan uses the extraction-aware check (Task 1b) for streaming.
- The downloader IS the heal path: its verify-short-circuit (`ModelDownloader.cs:76-89`) calls `EnsureExtracted` even when present files hash-verify, so routing a broken-but-present streaming install through `DownloadSelectedAsync` repairs the extraction WITHOUT re-downloading ~720 MB.
- `TarGzExtractor` is a public static class: `IsExtracted(archivePath, destinationDir, archiveSha256)` (cheap: marker read + `Directory.Exists`, no hashing; marker = `<archivePath>.extracted` containing the archive SHA-256) and `EnsureExtracted(archivePath, destinationDir, archiveSha256)`.
- `StreamingAutoInstaller` is GATED on `StreamingEnabled` (`SkippedStreamingDisabled`, `StreamingAutoInstaller.cs:97-101`) and on `OnboardingCompleted` (`AppShell.cs:482-487`), and latches `Installed` in-process — next-launch auto-heal is conditional, never guaranteed. The toggle-INDEPENDENT precedent is the manual install button (`DownloadStreamingAsync` has no toggle check); the new bottom button inherits that role.
- `StreamingEnabled` has no live change-notification channel (the toggle only queues a settings write, `ModelsPage.xaml.cs:220-225`; consumers re-read settings). Do not attempt a live streaming gate.
- `StreamingAutoInstaller.IsInstalledAndExtracted` is PRIVATE (instance) — pages/VMs cannot call it; Task 1b adds the public equivalent on `ModelDescriptor`.
- WinUI 3 does NOT show tooltips on disabled controls and has no `ShowOnDisabled` equivalent (WinUI 3 `ToolTipService` exposes only Placement/PlacementTarget/ToolTip; microsoft/microsoft-ui-xaml#1149). Accepted: the tooltip aids the enabled state; the disabled state is explained by the per-card installed labels and the manual-install note.
- No end-user documentation exists for manually installing the sotto model (README and all non-plan docs: zero hits) — the manual-install note copy is therefore self-contained (no "see the docs" pointer).
- ASR verify transients are existing behavior, preserved (not regressed) by this plan: the page renders before the off-thread verify completes (stale-false window → label "Not downloaded"/button enabled transiently), and `OnAsrChanged` re-verifies without resetting the flag (stale-true window → button may transiently gray during a re-hash of a corrupt install). Both self-correct when the verify lands and `UpdateInstalledLabels` re-runs; no tri-state policy input is warranted.
- `ModelFile` properties: `RelativePath`, `Url`, `Sha256`, `SizeBytes`, `ExtractToRelative` (`string?`; only the streaming runtime archive sets it, to `"runtime"`). `ModelDescriptor` is a sealed record with `required` init properties and `Files : IReadOnlyList<ModelFile>`.

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Models/SelectedModelsPolicy.cs` | Create (Task 1) | Pure decisions: selected-set construction (incl. cleanup gate), missing/downloadable names, manual-only names, button enable, cleanup-card enable/note |
| `tests/Winpepper.Models.Tests/SelectedModelsPolicyTests.cs` | Create (Task 1) | Policy unit tests (Linux) |
| `src/Winpepper.Models/ModelDescriptor.cs` | Modify (Task 1b) | Add `IsFullyInstalledAndExtracted` — extraction-aware installed check for streaming |
| `tests/Winpepper.Models.Tests/ModelDescriptorTests.cs` | Modify (Task 1b) | Tests for the extraction-aware check (Linux) |
| `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs` | Modify (Tasks 2, 6) | Add `DownloadSelectedAsync`; later remove superseded `DownloadMissingAsync`/`DownloadStreamingAsync` |
| `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelDownloadSelectedTests.cs` | Create (Task 2) | New VM method tests (Linux) |
| `src/Winpepper.App/Views/ModelsPage.xaml` | Modify (Tasks 3, 4, 5) | Streaming combo replaces install button; bottom button rename + name + tooltip; manual-install note; cleanup-off note |
| `src/Winpepper.App/Views/ModelsPage.xaml.cs` | Modify (Tasks 3, 4, 5) | `OnStreamingChanged`; `OnDownloadSelected` + `CurrentSelection()` + `UpdateDownloadButtonState()`; cleanup gate seed/subscription + `ApplyCleanupGate()` |
| `docs/automation-ids.md` | Modify (Tasks 3, 4, 5) | Register new AutomationIds |
| `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs` | Modify (Task 6) | Migrate streaming-download coverage to `DownloadSelectedAsync` |
| `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs` | Modify (Task 6) | Migrate the shared-gate concurrency test's `DownloadStreamingAsync` call (:218) to `DownloadSelectedAsync` |
| `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs` | Modify (Task 6) | Delete tests of deleted `DownloadMissingAsync` selection semantics; migrate the three plumbing-contract tests (gate serialization, sync-context, progress burst) to `DownloadSelectedAsync` |

Task order keeps every intermediate commit compiling on both sides: pure additions first (Tasks 1, 1b, 2), then page rework that switches callers (Tasks 3–5), then removal of the superseded VM methods once nothing calls them (Task 6).

---

### Task 1: `SelectedModelsPolicy` — pure decision logic

**Files:**
- Create: `src/Winpepper.Models/SelectedModelsPolicy.cs`
- Test: `tests/Winpepper.Models.Tests/SelectedModelsPolicyTests.cs`

**Interfaces:**
- Consumes: nothing (pure; no dependency on `ModelDescriptor` — the page maps descriptors to inputs).
- Produces (later tasks rely on these exact signatures):
  - `SelectedModelsPolicy.SelectedModel` — `readonly record struct SelectedModel(string Name, bool IsInstalled, bool IsManualInstallOnly)`
  - `static IReadOnlyList<SelectedModel> BuildSelection(SelectedModel? asr, SelectedModel? streaming, SelectedModel? cleanup, bool cleanupEnabled)`
  - `static IReadOnlyList<string> DownloadableMissingNames(IReadOnlyList<SelectedModel> selection)`
  - `static IReadOnlyList<string> ManualOnlyMissingNames(IReadOnlyList<SelectedModel> selection)`
  - `static bool DownloadButtonEnabled(IReadOnlyList<SelectedModel> selection)`
  - `static bool CleanupCardEnabled(bool cleanupEnabled)`
  - `static bool CleanupOffNoteVisible(bool cleanupEnabled)`

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Models.Tests/SelectedModelsPolicyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class SelectedModelsPolicyTests
{
    private static SelectedModelsPolicy.SelectedModel Model(
        string name, bool installed, bool manual = false) => new(name, installed, manual);

    [Fact]
    public void BuildSelection_Includes_Asr_Streaming_And_Cleanup_When_Cleanup_Enabled()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: true);

        selection.Count.ShouldBe(3);
        selection[0].Name.ShouldBe("asr-a");
        selection[1].Name.ShouldBe("stream-a");
        selection[2].Name.ShouldBe("clean-a");
    }

    [Fact]
    public void BuildSelection_Excludes_Cleanup_When_Cleanup_Disabled()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: false);

        selection.ShouldAllBe(m => m.Name != "clean-a");
        selection.Count.ShouldBe(2);
    }

    [Fact]
    public void BuildSelection_Skips_Null_Slots()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: null, streaming: null, cleanup: null, cleanupEnabled: true);

        selection.ShouldBeEmpty();
    }

    [Fact]
    public void DownloadableMissingNames_Returns_Only_Missing_Downloadable_Models()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: Model("stream-a", installed: false),
            cleanup: Model("clean-a", installed: false),
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "stream-a", "clean-a" });
    }

    [Fact]
    public void DownloadableMissingNames_Excludes_Manual_Install_Only_Models()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: false),
            streaming: null,
            cleanup: Model("sotto", installed: false, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "asr-a" });
    }

    [Fact]
    public void DownloadableMissingNames_Deduplicates_Repeated_Names()
    {
        // Two dropdowns pointing at the same registry entry must not download it twice.
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("same", installed: false),
            streaming: Model("same", installed: false),
            cleanup: null,
            cleanupEnabled: true);

        SelectedModelsPolicy.DownloadableMissingNames(selection)
            .ShouldBe(new[] { "same" });
    }

    [Fact]
    public void ManualOnlyMissingNames_Returns_Manual_Models_That_Are_Missing()
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: null,
            cleanup: Model("sotto", installed: false, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.ManualOnlyMissingNames(selection)
            .ShouldBe(new[] { "sotto" });
    }

    [Fact]
    public void ManualOnlyMissingNames_Excludes_Installed_Manual_Models()
    {
        // An installed manual model needs no note and no download.
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed: true),
            streaming: null,
            cleanup: Model("sotto", installed: true, manual: true),
            cleanupEnabled: true);

        SelectedModelsPolicy.ManualOnlyMissingNames(selection).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false, false, true)]  // missing + downloadable => enabled
    [InlineData(true, false, false)]  // installed => nothing to do
    [InlineData(false, true, false)]  // missing but manual-only => button cannot help
    [InlineData(true, true, false)]   // installed manual model => nothing to do
    public void DownloadButtonEnabled_Truth_Table(bool installed, bool manual, bool expected)
    {
        var selection = SelectedModelsPolicy.BuildSelection(
            asr: Model("asr-a", installed, manual),
            streaming: null, cleanup: null, cleanupEnabled: true);

        SelectedModelsPolicy.DownloadButtonEnabled(selection).ShouldBe(expected);
    }

    [Fact]
    public void DownloadButtonEnabled_Is_False_For_Empty_Selection() =>
        SelectedModelsPolicy.DownloadButtonEnabled([]).ShouldBeFalse();

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void Cleanup_Gate_Mirrors_The_Setting(bool cleanupEnabled, bool cardEnabled, bool noteVisible)
    {
        SelectedModelsPolicy.CleanupCardEnabled(cleanupEnabled).ShouldBe(cardEnabled);
        SelectedModelsPolicy.CleanupOffNoteVisible(cleanupEnabled).ShouldBe(noteVisible);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0103: The name 'SelectedModelsPolicy' does not exist` (compile failure is the RED state for a new type).

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Models/SelectedModelsPolicy.cs`:

```csharp
namespace Winpepper.Models;

/// <summary>
/// Pure decision logic for the Models page: which selected models are
/// missing and downloadable, whether the bottom download button should be
/// enabled, which selected models can only be installed manually, and the
/// cleanup-gate state. Pure decision — Linux-tested by design. The page
/// supplies installed-state inputs from the same sources it already
/// renders (the hash-verified flag for ASR, presence checks for cleanup
/// and streaming), so this class never touches the file system.
/// </summary>
public static class SelectedModelsPolicy
{
    /// <summary>One dropdown's current choice, reduced to what decisions need.</summary>
    public readonly record struct SelectedModel(string Name, bool IsInstalled, bool IsManualInstallOnly);

    /// <summary>
    /// The set of models the page's dropdowns currently choose. The cleanup
    /// choice only counts while cleanup is enabled (change 4's gate): a
    /// disabled feature's model is not "selected" for download purposes.
    /// Null slots (no selection in that combo) are skipped.
    /// </summary>
    public static IReadOnlyList<SelectedModel> BuildSelection(
        SelectedModel? asr, SelectedModel? streaming, SelectedModel? cleanup, bool cleanupEnabled)
    {
        var selection = new List<SelectedModel>(3);
        if (asr is { } a) selection.Add(a);
        if (streaming is { } s) selection.Add(s);
        if (cleanupEnabled && cleanup is { } c) selection.Add(c);
        return selection;
    }

    /// <summary>Selected, not installed, and fetchable by the downloader — the bottom button's work list.</summary>
    public static IReadOnlyList<string> DownloadableMissingNames(IReadOnlyList<SelectedModel> selection) =>
        selection.Where(m => !m.IsInstalled && !m.IsManualInstallOnly)
                 .Select(m => m.Name)
                 .Distinct(StringComparer.Ordinal)
                 .ToList();

    /// <summary>Selected, not installed, but manual-install only — the button must not attempt these; the UI explains instead.</summary>
    public static IReadOnlyList<string> ManualOnlyMissingNames(IReadOnlyList<SelectedModel> selection) =>
        selection.Where(m => !m.IsInstalled && m.IsManualInstallOnly)
                 .Select(m => m.Name)
                 .Distinct(StringComparer.Ordinal)
                 .ToList();

    /// <summary>A button whose only effect is already satisfied must be disabled, not hidden.</summary>
    public static bool DownloadButtonEnabled(IReadOnlyList<SelectedModel> selection) =>
        DownloadableMissingNames(selection).Count > 0;

    /// <summary>Gray out (never hide, never clear): the combo disables, values are preserved.</summary>
    public static bool CleanupCardEnabled(bool cleanupEnabled) => cleanupEnabled;

    /// <summary>The note shows exactly when the card is gated off.</summary>
    public static bool CleanupOffNoteVisible(bool cleanupEnabled) => !cleanupEnabled;
}
```

Note: the project uses implicit usings — `System.Linq`/`System.Collections.Generic` are already in scope. If the build reports missing usings, add `using System.Linq;` at the top.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "Winpepper.Models.Tests.SelectedModelsPolicyTests"
```
Expected: all `SelectedModelsPolicyTests` PASS, 0 failures.

- [ ] **Step 5: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` (use a 10+ min timeout).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Models/SelectedModelsPolicy.cs tests/Winpepper.Models.Tests/SelectedModelsPolicyTests.cs
git commit -m "feat(models): SelectedModelsPolicy — pure decisions for the Models page download button and cleanup gate

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 1b: `ModelDescriptor.IsFullyInstalledAndExtracted` — extraction-aware installed check

**Why (from the load-bearing validation):** `IsFullyInstalled` is presence-only. A streaming install can be broken-but-present (archive moved into place before `EnsureExtracted`, which can throw; or the extracted `runtime/` tree deleted later) — that state must count as NOT installed so the bottom button includes streaming and the downloader's verify-short-circuit + `EnsureExtracted` heal path repairs it (without re-downloading: present files hash-verify and only re-extract). The strong check that exists today (`StreamingAutoInstaller.IsInstalledAndExtracted`) is private; this task adds the public equivalent where it belongs, on the descriptor. Tasks 3–4 use it for the streaming card and selection snapshot.

**Files:**
- Modify: `src/Winpepper.Models/ModelDescriptor.cs` (add one method after `IsFullyInstalled`)
- Test: `tests/Winpepper.Models.Tests/ModelDescriptorTests.cs` (append tests; reuse its existing `TempDir` helper)

**Interfaces:**
- Consumes: `TarGzExtractor.IsExtracted(string archivePath, string destinationDir, string archiveSha256)` (public static; marker read + `Directory.Exists`, no hashing), existing `IsFullyInstalled`.
- Produces: `public bool IsFullyInstalledAndExtracted(string installRoot)` — Tasks 3–4 call exactly this. For descriptors with no `ExtractToRelative` files it equals `IsFullyInstalled`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Models.Tests/ModelDescriptorTests.cs` (add `using System.Formats.Tar;` and `using System.IO.Compression;` if not present; mirror the archive-building idiom from `TarGzExtractorTests.MakeArchive`):

```csharp
    [Fact]
    public void IsFullyInstalledAndExtracted_Equals_IsFullyInstalled_When_No_Archive_Files()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "plain", Kind = ModelKind.Asr, DisplayName = "Plain",
            InstallDirRelative = "plain",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 5 },
            },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "plain"));
        File.WriteAllText(Path.Combine(temp.Path, "plain", "a.bin"), "hello");

        d.IsFullyInstalled(temp.Path).ShouldBeTrue();
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeTrue();
    }

    [Fact]
    public void IsFullyInstalledAndExtracted_False_When_Archive_Present_But_Not_Extracted()
    {
        using var temp = new TempDir();
        var d = MakeArchiveDescriptor(temp, out _);

        // Broken-but-present: files exist and are non-empty, but nothing was
        // ever extracted. The weak check says installed; the strong one must not.
        d.IsFullyInstalled(temp.Path).ShouldBeTrue();
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeFalse();
    }

    [Fact]
    public void IsFullyInstalledAndExtracted_True_After_Extraction_And_False_After_Tree_Deleted()
    {
        using var temp = new TempDir();
        var d = MakeArchiveDescriptor(temp, out var archivePath);
        var runtimeDir = Path.Combine(temp.Path, "streamy", "runtime");

        TarGzExtractor.EnsureExtracted(archivePath, runtimeDir, "cafebabe");
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeTrue();

        Directory.Delete(runtimeDir, recursive: true);
        d.IsFullyInstalledAndExtracted(temp.Path).ShouldBeFalse();
    }

    /// <summary>Descriptor with one plain file and one archive file (ExtractToRelative
    /// = "runtime"), both present on disk; the archive is a real (tiny) tar.gz so
    /// EnsureExtracted can extract it. Sha256 "cafebabe" is arbitrary — IsExtracted
    /// compares it to the marker file EnsureExtracted writes, not to a real hash.</summary>
    private static ModelDescriptor MakeArchiveDescriptor(TempDir temp, out string archivePath)
    {
        var dir = Path.Combine(temp.Path, "streamy");
        var src = Path.Combine(temp.Path, "src", "toplevel");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(dir, "model.gguf"), "weights");
        File.WriteAllText(Path.Combine(src, "transcribe.dll"), "fake dll bytes");
        archivePath = Path.Combine(dir, "runtime.tar.gz");
        using (var fs = File.Create(archivePath))
        using (var gz = new GZipStream(fs, CompressionMode.Compress))
        {
            TarFile.CreateFromDirectory(Path.Combine(temp.Path, "src"), gz, includeBaseDirectory: false);
        }

        return new ModelDescriptor
        {
            Name = "streamy", Kind = ModelKind.StreamingAsr, DisplayName = "Streamy",
            InstallDirRelative = "streamy",
            Files = new[]
            {
                new ModelFile { RelativePath = "model.gguf", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 7 },
                new ModelFile { RelativePath = "runtime.tar.gz", Url = "https://x", Sha256 = "cafebabe", SizeBytes = 1, ExtractToRelative = "runtime" },
            },
        };
    }
```

(If `ModelFile`'s object-initializer shape differs — e.g. `ExtractToRelative` has another name — STOP and re-check `src/Winpepper.Models/ModelDescriptor.cs`/the `ModelFile` type; the registry's streaming archive entry at `ModelRegistry.cs:~181-188` shows the real property names.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS1061: 'ModelDescriptor' does not contain a definition for 'IsFullyInstalledAndExtracted'`.

- [ ] **Step 3: Write the implementation**

In `src/Winpepper.Models/ModelDescriptor.cs`, add immediately after `IsFullyInstalled`:

```csharp
    /// <summary>
    /// <see cref="IsFullyInstalled"/> plus extraction state: every file with
    /// an ExtractToRelative must also have its extracted tree present
    /// (extraction marker + directory, via TarGzExtractor.IsExtracted).
    /// Presence-only checks cannot distinguish ready files from a corrupt
    /// install whose archive landed but whose extraction failed or whose
    /// extracted tree was deleted — those must read as NOT installed so the
    /// downloader's verify-short-circuit + EnsureExtracted heal path runs.
    /// Cheap: marker read + Directory.Exists, no hashing.
    /// </summary>
    public bool IsFullyInstalledAndExtracted(string installRoot)
    {
        if (!IsFullyInstalled(installRoot)) return false;
        foreach (var f in Files)
        {
            if (f.ExtractToRelative is null) continue;
            var dir = Path.Combine(installRoot, InstallDirRelative);
            if (!TarGzExtractor.IsExtracted(
                    Path.Combine(dir, f.RelativePath),
                    Path.Combine(dir, f.ExtractToRelative),
                    f.Sha256))
            {
                return false;
            }
        }
        return true;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "Winpepper.Models.Tests.ModelDescriptorTests"
```
Expected: all `ModelDescriptorTests` PASS, 0 failures.

- [ ] **Step 5: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Models/ModelDescriptor.cs tests/Winpepper.Models.Tests/ModelDescriptorTests.cs
git commit -m "feat(models): ModelDescriptor.IsFullyInstalledAndExtracted — extraction-aware installed check

Presence-only IsFullyInstalled cannot see a broken extraction (archive
landed, runtime/ tree missing or deleted). The Models page's streaming
installed-state and download work-list need the strong check so
broken-but-present installs count as missing and get healed by the
downloader's verify-short-circuit + EnsureExtracted path.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 2: `ModelsTabViewModel.DownloadSelectedAsync`

**Files:**
- Modify: `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs` (add one method after `DownloadStreamingAsync`, ~line 116; do NOT remove the existing methods yet — the page still calls them until Tasks 3–4)
- Test: `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelDownloadSelectedTests.cs`

**Interfaces:**
- Consumes: existing private `DownloadOneAsync(ModelDescriptor, CancellationToken)` (routes progress to the card matching `d.Kind` — already handles `StreamingAsr`), existing `_downloadGate`, `ModelDescriptor.ManualInstallOnly`.
- Produces: `public async Task DownloadSelectedAsync(IReadOnlyList<ModelDescriptor> models, CancellationToken ct)` — Task 4's page handler and Task 6's migrated tests call exactly this.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelDownloadSelectedTests.cs`:

```csharp
using Shouldly;
using Winpepper.Models;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests.ViewModels;

public class ModelsTabViewModelDownloadSelectedTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("winpepper-dl-selected-").FullName;

    private sealed class RecordingDownloader : ModelsTabViewModel.IDownloader
    {
        public List<string> Downloaded { get; } = [];

        public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                  IProgress<DownloadProgress> progress, CancellationToken ct)
        {
            Downloaded.Add(descriptor.Name);
            return Task.CompletedTask;
        }
    }

    private ModelsTabViewModel CreateVm(ModelsTabViewModel.IDownloader downloader) =>
        new(new ModelRegistry(), _root, downloader,
            currentAsrName: ModelRegistry.DefaultAsrName,
            currentCleanupName: ModelRegistry.DefaultCleanupName,
            promoteAsr: _ => { }, promoteCleanup: _ => { });

    [Fact]
    public async Task Downloads_Exactly_The_Given_Descriptors_In_Order()
    {
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var registry = new ModelRegistry();
        var selected = new[]
        {
            registry.Find(ModelRegistry.DefaultAsrName)!,
            registry.Find(ModelRegistry.StreamingAsrName)!,
        };

        await vm.DownloadSelectedAsync(selected, TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBe(
            new[] { ModelRegistry.DefaultAsrName, ModelRegistry.StreamingAsrName });
    }

    [Fact]
    public async Task Does_Not_Download_Unlisted_Registry_Models()
    {
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var registry = new ModelRegistry();

        await vm.DownloadSelectedAsync(
            new[] { registry.Find(ModelRegistry.DefaultCleanupName)! },
            TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBe(new[] { ModelRegistry.DefaultCleanupName });
    }

    [Fact]
    public async Task Skips_Manual_Install_Only_Descriptors()
    {
        // Belt-and-braces: the policy filters these upstream, and the raw
        // downloader would throw InvalidOperationException if one got through.
        var downloader = new RecordingDownloader();
        var vm = CreateVm(downloader);
        var sotto = new ModelRegistry().Find("sotto-cleanup-lfm25-350m-q8_0")!;
        sotto.ManualInstallOnly.ShouldBeTrue(); // pin the registry assumption

        await vm.DownloadSelectedAsync(new[] { sotto }, TestContext.Current.CancellationToken);

        downloader.Downloaded.ShouldBeEmpty();
    }

    [Fact]
    public async Task Raises_IsSelectedInstalled_Changed_On_All_Three_Cards()
    {
        var vm = CreateVm(new RecordingDownloader());
        var changed = new List<string>();
        vm.AsrCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("asr"); };
        vm.CleanupCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("cleanup"); };
        vm.StreamingCard.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ModelCardViewModel.IsSelectedInstalled)) changed.Add("streaming"); };

        await vm.DownloadSelectedAsync([], TestContext.Current.CancellationToken);

        changed.ShouldContain("asr");
        changed.ShouldContain("cleanup");
        changed.ShouldContain("streaming");
    }
}
```

(If `ModelDescriptor.ManualInstallOnly` is `init`-only and `sotto` resolution differs, the pin assertion in `Skips_Manual_Install_Only_Descriptors` will catch it loudly — that is its job.)

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS1061: 'ModelsTabViewModel' does not contain a definition for 'DownloadSelectedAsync'`.

- [ ] **Step 3: Write the implementation**

In `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`, add immediately AFTER the `DownloadStreamingAsync` method (~line 116):

```csharp
    /// <summary>
    /// Downloads exactly the given descriptors — the page computes the
    /// "selected and missing" set via SelectedModelsPolicy, so this method
    /// never reaches for unselected registry models. Manual-install-only
    /// descriptors are skipped defensively: the policy filters them
    /// upstream, and the raw downloader throws if one reaches it.
    /// </summary>
    public async Task DownloadSelectedAsync(IReadOnlyList<ModelDescriptor> models, CancellationToken ct)
    {
        await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var d in models)
            {
                if (d.ManualInstallOnly) continue;
                await DownloadOneAsync(d, ct).ConfigureAwait(false);
            }

            AsrCard.RaiseIsSelectedInstalledChanged();
            CleanupCard.RaiseIsSelectedInstalledChanged();
            StreamingCard.RaiseIsSelectedInstalledChanged();
        }
        finally { _downloadGate.Release(); }
    }
```

CAUTION (pinned by existing tests): do NOT add an `IsFullyInstalled` pre-filter inside this method. `ModelsTabViewModelStreamingTests` (:51, :81) pin the always-route-through-the-downloader semantics — the downloader's verify-short-circuit + `EnsureExtracted` is the heal path for broken-but-present installs, and a presence pre-filter would remove exactly that. Callers decide what is "missing" (via `SelectedModelsPolicy` with the strong installed check); this method downloads what it is given.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll -class "Winpepper.Models.Tests.ViewModels.ModelsTabViewModelDownloadSelectedTests"
```
Expected: all 4 tests PASS.

- [ ] **Step 5: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelDownloadSelectedTests.cs
git commit -m "feat(models): ModelsTabViewModel.DownloadSelectedAsync downloads an explicit descriptor set

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 3: Streaming card becomes a registry-driven dropdown (change 2)

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` (streaming card, ~:194-252: add `StreamingCombo`, delete `StreamingModelInstallButton`)
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` (add `OnStreamingChanged`; delete `OnInstallStreamingModel` (~:331-359) and `_streamingDownloadInProgress`; rework the streaming section of `UpdateInstalledLabels` (~:361-389); seed `StreamingCombo` in `OnNavigatedTo`)
- Modify: `docs/automation-ids.md` (Models page section: add `ModelsStreamingCombo`)

**Interfaces:**
- Consumes: `ViewModel.StreamingCard` (`ModelCardViewModel`: `Available`, `SelectedName`, `SelectedDescriptor`, `CommitSelection()` — promote is a deliberate no-op for streaming); `ModelDescriptor.IsFullyInstalledAndExtracted(string)` (Task 1b — extraction-aware; NOT the presence-only `IsFullyInstalled`, see Validation findings); `App.Shell.StreamingAutoInstaller.Status`.
- Produces: `StreamingCombo` (`x:Name`, AutomationId `ModelsStreamingCombo`) and handler `private void OnStreamingChanged(object sender, SelectionChangedEventArgs e)`. Task 4 relies on `ViewModel.StreamingCard.SelectedDescriptor` reflecting the combo choice.
- Deliberately unchanged: the card's descriptive paragraph, `StreamingToggle`, the installed/not-installed icon row, the progress `ListView`, `DownloadStreamingAsync` (now uncalled from the page; removed in Task 6), `StreamingAutoInstaller`.
- Spec cross-reference: change 1's "disable the streaming install button when installed" is satisfied here by change 2's stronger requirement — the button is deleted outright; installing a missing streaming model becomes the bottom button's job (Task 4), which disables when satisfied.
- NOT added: any "(none)" combo entry — `StreamingToggle` governs whether streaming is used; the dropdown only selects which model. NOT added: persistence of the streaming choice (no new settings; registry pins the name, promote stays no-op).

- [ ] **Step 1: XAML — replace the install button with a combo**

In `src/Winpepper.App/Views/ModelsPage.xaml`, inside the streaming card (locate the comment `<!-- Streaming model card -->`):

(a) Immediately AFTER the closing `</StackPanel>` of the horizontal icon row that contains `StreamingInstalledIcon` / `StreamingNotInstalledIcon` / `StreamingInstalledText`, and BEFORE the `<ListView ItemsSource="{x:Bind ViewModel.StreamingCard.ProgressByFile, Mode=OneWay}" ...>` element, insert (this mirrors the other cards' icons-row → combo → progress-list order):

```xml
                    <ComboBox x:Name="StreamingCombo"
                              AutomationProperties.AutomationId="ModelsStreamingCombo"
                              Header="Active model"
                              HorizontalAlignment="Stretch"
                              ItemsSource="{x:Bind ViewModel.StreamingCard.Available, Mode=OneWay}"
                              DisplayMemberPath="DisplayName"
                              SelectionChanged="OnStreamingChanged" />
```

(b) DELETE the entire `StreamingModelInstallButton` block (the last child of the card's StackPanel):

```xml
                    <Button x:Name="StreamingModelInstallButton"
                            Click="OnInstallStreamingModel"
                            AutomationProperties.AutomationId="StreamingModelInstallButton"
                            HorizontalAlignment="Left">
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <FontIcon Glyph="&#xE896;" FontSize="16" />
                            <TextBlock Text="Install streaming model" />
                        </StackPanel>
                    </Button>
```

- [ ] **Step 2: Code-behind — selection handler, seeding, label rework, handler deletion**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`:

(a) Add next to `OnCleanupChanged` (~:117-125):

```csharp
    private void OnStreamingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StreamingCombo.SelectedItem is ModelDescriptor d)
        {
            // Streaming has no selection slot and no setting: the registry
            // pins the active streaming model, so the card's promote callback
            // is a deliberate no-op. The combo exists so future registry
            // entries appear automatically and feed the download button.
            ViewModel.StreamingCard.SelectedName = d.Name;
            ViewModel.StreamingCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }
```

(b) In `OnNavigatedTo`, directly after the line that seeds `CleanupCombo.SelectedItem` (search for `CleanupCombo.SelectedItem =`), add:

```csharp
        StreamingCombo.SelectedItem = ViewModel.StreamingCard.SelectedDescriptor;
```

(c) DELETE the whole `OnInstallStreamingModel` method (~:331-359) and the `private bool _streamingDownloadInProgress;` field declaration.

(d) In `UpdateInstalledLabels` (~:361-389), replace the streaming block. OLD (keep the multi-line comment about the shared operation gate that sits inside it — it still applies to bottom-button downloads):

```csharp
        var models = App.Shell!.ModelsServices;
        var streamingInstalled = models.Registry.Find(ModelRegistry.StreamingAsrName)!
            .IsFullyInstalled(models.ModelsRoot);
```
NEW (selection-aware — the label follows the dropdown):

```csharp
        var models = App.Shell!.ModelsServices;
        var streamingInstalled = ViewModel.StreamingCard.SelectedDescriptor
            ?.IsFullyInstalledAndExtracted(models.ModelsRoot) ?? false;
```

The strong check matters here (validated): a streaming install whose archive landed but whose `runtime/` extraction failed or was deleted reads "Installed" under presence-only `IsFullyInstalled` — masking a `Failed` auto-install AND excluding streaming from the bottom button, stranding the user with no repair path (the old install button had no pre-filter precisely to heal this; this page deletes that button). With `IsFullyInstalledAndExtracted`, that state reads "not installed": the failed label shows when applicable, the bottom button includes streaming, and the downloader heals via verify-short-circuit + `EnsureExtracted` (no ~720 MB re-download — present files hash-verify and only re-extract).

And replace the busy/label lines. OLD:

```csharp
        var streamingBusy = _streamingDownloadInProgress
            || autoStatus == StreamingAutoInstallStatus.Installing;
        StreamingInstalledText.Text = streamingInstalled ? "Installed"
            : streamingBusy ? "Installing…"
            : autoStatus == StreamingAutoInstallStatus.Failed ? "Install failed — use Install to retry"
            : "Not downloaded";
```
NEW:

```csharp
        var streamingBusy = autoStatus == StreamingAutoInstallStatus.Installing;
        StreamingInstalledText.Text = streamingInstalled ? "Installed"
            : streamingBusy ? "Installing…"
            : autoStatus == StreamingAutoInstallStatus.Failed ? "Install failed — use the download button to retry"
            : "Not downloaded";
```
(Task 4 extends `streamingBusy` to cover bottom-button runs that include streaming.)

- [ ] **Step 3: Register the AutomationId**

In `docs/automation-ids.md`, in the Models page section (~lines 69-77, listing `ModelsAsrCombo`, `ModelsCleanupCombo`, `ModelsDownloadButton`, ...), add a row/line following the file's existing format:

```
ModelsStreamingCombo — Models page, streaming model ComboBox (streaming card)
```
Match the surrounding entries' exact formatting (table row vs list item — copy the neighbors' style).

- [ ] **Step 4: Linux suite (unchanged projects must stay green)**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 5: Windows gate (only compile check for `Winpepper.App`)**

```bash
./scripts/windows-gate.sh
```
Expected: `GATE: GREEN` (20–30 min timeout; retry on UNC MSB4025 / vsock interop flakes). If the XAML compiler reports an unknown member `OnInstallStreamingModel`, a stale reference remains in XAML — re-check Step 1(b).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml src/Winpepper.App/Views/ModelsPage.xaml.cs docs/automation-ids.md
git commit -m "feat(app): streaming model card becomes a registry-driven dropdown; dedicated install button removed

The streaming card now matches the ASR/cleanup pattern: ComboBox over
ModelKind.StreamingAsr registry entries with the same installed-state
row. Installing a missing streaming model moves to the bottom download
button (next commit). StreamingToggle and descriptive text unchanged;
streaming selection is deliberately not persisted (registry pins it).

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 4: Bottom button — "Download selected models" semantics + enable state + manual-install note (changes 1 & 3)

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` (bottom button ~:254-262: add `x:Name`, new label + tooltip; add `ManualInstallNote` TextBlock after it)
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` (replace `OnDownloadMissing` with `OnDownloadSelected`; add `CurrentSelection()`, `UpdateDownloadButtonState()`, fields `_cleanupEnabled`, `_downloadRunIncludesStreaming`; extend `UpdateInstalledLabels`)
- Modify: `docs/automation-ids.md` (add `ModelsManualInstallNote`)

**Interfaces:**
- Consumes: `SelectedModelsPolicy` (Task 1 signatures), `ViewModel.DownloadSelectedAsync(IReadOnlyList<ModelDescriptor>, CancellationToken)` (Task 2), `StreamingCombo`/`ViewModel.StreamingCard.SelectedDescriptor` (Task 3), existing `_asrSelectedVerified`, `ViewModel.CleanupCard.IsSelectedInstalled`, `ModelsServices` (`Registry`, `ModelsRoot`, `VerifyAsrModelReady`), `shell.AsrModelSelection.Read()`, `shell.Pipeline.TryStart()`.
- Produces: `DownloadSelectedButton` (`x:Name`; AutomationId stays `ModelsDownloadButton`), `ManualInstallNote` (`x:Name`, AutomationId `ModelsManualInstallNote`), `private IReadOnlyList<SelectedModelsPolicy.SelectedModel> CurrentSelection()`, `private void UpdateDownloadButtonState()`, `private async void OnDownloadSelected(object sender, RoutedEventArgs e)`, `private bool _cleanupEnabled = true;` (Task 5 wires it to the real gate), `private bool _downloadRunIncludesStreaming;`.
- Enable-state liveness comes free: `UpdateDownloadButtonState()` is called at the end of `UpdateInstalledLabels()`, which already runs on navigation seed, every selection change (ASR/cleanup/streaming), ASR verify completion, auto-installer status change, and download completion.

- [ ] **Step 1: XAML — button rename + manual-install note**

In `src/Winpepper.App/Views/ModelsPage.xaml`, replace the bottom button block. OLD:

```xml
            <Button Click="OnDownloadMissing"
                    AutomationProperties.AutomationId="ModelsDownloadButton"
                    Style="{StaticResource AccentButtonStyle}"
                    HorizontalAlignment="Left">
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <FontIcon Glyph="&#xE896;" FontSize="16" />
                    <TextBlock Text="Download missing models" />
                </StackPanel>
            </Button>
```
NEW (same position; note follows the button):

```xml
            <Button x:Name="DownloadSelectedButton"
                    Click="OnDownloadSelected"
                    AutomationProperties.AutomationId="ModelsDownloadButton"
                    ToolTipService.ToolTip="Downloads the models chosen above that aren't installed yet."
                    Style="{StaticResource AccentButtonStyle}"
                    HorizontalAlignment="Left">
                <StackPanel Orientation="Horizontal" Spacing="8">
                    <FontIcon Glyph="&#xE896;" FontSize="16" />
                    <TextBlock Text="Download selected models" />
                </StackPanel>
            </Button>
            <TextBlock x:Name="ManualInstallNote"
                       AutomationProperties.AutomationId="ModelsManualInstallNote"
                       Style="{ThemeResource CaptionTextBlockStyle}"
                       Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                       TextWrapping="Wrap"
                       Visibility="Collapsed" />
```

Known platform limitation (validated; accepted — do not "fix"): WinUI 3 does not show tooltips on disabled controls and has no `ShowOnDisabled` equivalent (WinUI 3 `ToolTipService` exposes only Placement/PlacementTarget/ToolTip; microsoft/microsoft-ui-xaml#1149). The tooltip therefore aids the enabled state only; the disabled state is explained by the per-card installed labels and `ManualInstallNote`. Keep the tooltip exactly as spec'd — do not add wrapper-element workarounds or extra copy.

- [ ] **Step 2: Code-behind — fields, selection snapshot, button state**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`:

(a) Next to the existing `private bool _downloadInProgress;` field, add:

```csharp
    // Seeded from the cleanup gate in OnNavigatedTo (Task 5 wires it to
    // App.Shell.CleanupVm.Enabled); until then cleanup counts as enabled,
    // which matches today's behavior.
    private bool _cleanupEnabled = true;

    // True while a bottom-button run that includes the streaming model is
    // in flight, so the streaming state line can honestly say "Installing…".
    private bool _downloadRunIncludesStreaming;
```

(b) Add these two methods near `UpdateInstalledLabels`:

```csharp
    /// <summary>
    /// Snapshot of what the page's dropdowns currently choose, using the
    /// SAME installed-state sources the page already renders: the
    /// hash-verified flag for ASR, presence checks for cleanup/streaming.
    /// </summary>
    private IReadOnlyList<SelectedModelsPolicy.SelectedModel> CurrentSelection()
    {
        var models = App.Shell!.ModelsServices;

        SelectedModelsPolicy.SelectedModel? asr = ViewModel.AsrCard.SelectedDescriptor is { } a
            ? new(a.Name, _asrSelectedVerified, a.ManualInstallOnly) : null;
        SelectedModelsPolicy.SelectedModel? streaming = ViewModel.StreamingCard.SelectedDescriptor is { } s
            ? new(s.Name, s.IsFullyInstalledAndExtracted(models.ModelsRoot), s.ManualInstallOnly) : null;
        SelectedModelsPolicy.SelectedModel? cleanup = ViewModel.CleanupCard.SelectedDescriptor is { } c
            ? new(c.Name, ViewModel.CleanupCard.IsSelectedInstalled, c.ManualInstallOnly) : null;

        return SelectedModelsPolicy.BuildSelection(asr, streaming, cleanup, _cleanupEnabled);
    }

    private void UpdateDownloadButtonState()
    {
        var selection = CurrentSelection();

        // Disabled (grayed, not hidden) whenever its only effect is already
        // satisfied — and always while a run is in flight.
        DownloadSelectedButton.IsEnabled =
            !_downloadInProgress && SelectedModelsPolicy.DownloadButtonEnabled(selection);

        var manualNames = SelectedModelsPolicy.ManualOnlyMissingNames(selection);
        if (manualNames.Count > 0)
        {
            var registry = App.Shell!.ModelsServices.Registry;
            var displays = manualNames.Select(n => registry.Find(n)?.DisplayName ?? n);
            ManualInstallNote.Text =
                $"{string.Join(", ", displays)} must be installed manually — the download button can't fetch it.";
            ManualInstallNote.Visibility = Visibility.Visible;
        }
        else
        {
            ManualInstallNote.Visibility = Visibility.Collapsed;
        }
    }
```

(c) At the very END of `UpdateInstalledLabels()` (after the streaming icon visibility lines), add:

```csharp
        UpdateDownloadButtonState();
```

(d) In the streaming block of `UpdateInstalledLabels`, extend the busy flag. OLD (from Task 3):

```csharp
        var streamingBusy = autoStatus == StreamingAutoInstallStatus.Installing;
```
NEW:

```csharp
        var streamingBusy = (_downloadInProgress && _downloadRunIncludesStreaming)
            || autoStatus == StreamingAutoInstallStatus.Installing;
```

- [ ] **Step 3: Code-behind — replace the download handler**

Replace the entire `OnDownloadMissing` method (~:287-329) with:

```csharp
    private async void OnDownloadSelected(object sender, RoutedEventArgs e)
    {
        if (_downloadInProgress) return;

        var selection = CurrentSelection();
        var names = SelectedModelsPolicy.DownloadableMissingNames(selection);
        if (names.Count == 0) return; // button should already be disabled; belt-and-braces

        var shell = App.Shell!;
        var registry = shell.ModelsServices.Registry;
        var descriptors = names.Select(n => registry.Find(n)!).ToList();

        _downloadInProgress = true;
        _downloadRunIncludesStreaming =
            descriptors.Any(d => d.Kind == ModelKind.StreamingAsr);
        UpdateInstalledLabels(); // disables the button + shows "Installing…" where honest

        try
        {
            await ViewModel.DownloadSelectedAsync(descriptors, _lifetimeCts?.Token ?? CancellationToken.None);

            // Refresh the verified ASR flag off-thread so the label and the
            // button's enable state reflect the download that just finished
            // (previously the verify result was discarded and the label went
            // stale). This also primes ModelsServices' verified-readiness
            // cache so the synchronous check inside TryStart() below is a
            // cache hit, not a dispatcher-blocking re-hash.
            var canonicalAsr = registry
                .ResolveOrDefault(shell.AsrModelSelection.Read(), ModelKind.Asr).Name;
            _asrSelectedVerified = await Task.Run(() => shell.ModelsServices.VerifyAsrModelReady(canonicalAsr));

            // If the pipeline was left disabled at boot because models were
            // missing (issue #6), bring it up now that the download finished.
            shell.Pipeline.TryStart();
        }
        catch (OperationCanceledException)
        {
            // Navigation away cancels _lifetimeCts; cancellation must not
            // surface as an application crash.
        }
        catch (Exception ex)
        {
            shell.LogFactory.CreateLogger<ModelsPage>()
                .LogError(ex, "Model download failed");
            shell.ErrorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
        }
        finally
        {
            _downloadInProgress = false;
            _downloadRunIncludesStreaming = false;
            // Recompute rather than blindly re-enable: if everything the
            // dropdowns choose is now installed, the button must gray out.
            UpdateInstalledLabels();
        }
    }
```

Notes for the implementer:
- `CurrentSelection`/`UpdateDownloadButtonState`/`OnDownloadSelected` use LINQ (`Select`, `Any`, `ToList`). If the Windows gate reports missing names, add `using System.Linq;` at the top of `ModelsPage.xaml.cs` (inside the `#if WINDOWS` region, with the other usings).
- Streaming's installed input is `IsFullyInstalledAndExtracted` (Task 1b), NOT `IsFullyInstalled` — a broken-but-present install (archive landed, `runtime/` missing) must count as missing so this button heals it via the downloader's verify-short-circuit + `EnsureExtracted` (present files hash-verify; no ~720 MB re-download).
- The gate asymmetry is deliberate (validated + recorded): the cleanup model is excluded while `CleanupEnabled` is off, but streaming is included regardless of `StreamingToggle`. Precedent: today's manual "Install streaming model" button works regardless of the toggle, and this button inherits that role; also `StreamingEnabled` has no live change-notification channel (only a queued settings write), so a live streaming gate is not implementable without new machinery the constraints forbid. Do NOT add a `streamingEnabled` parameter to `BuildSelection`.
- Known transients (validated as existing behavior, preserved — do not "fix" with a tri-state): on a cold cache the ASR verify completes off-thread after first render, so the button can be transiently enabled with everything installed (today's page has the same window with an always-enabled button); and `OnAsrChanged` re-verifies without resetting the flag, so the button can transiently gray during a re-hash of a corrupt install. Both self-correct when the verify lands and `UpdateInstalledLabels` re-runs.
- Behavior change vs old code is intentional and specified: the selected ASR model is no longer downloaded unconditionally — it is included only when `_asrSelectedVerified` is false. `_asrSelectedVerified` is hash-verified, so a corrupt-but-present install still reads as missing and gets repaired through the coordinator path.
- Progress UI needs no change: `DownloadOneAsync` already routes per-file rows to the card matching each descriptor's kind, so rows appear under exactly the cards whose models are downloading (now including streaming).

- [ ] **Step 4: Register the AutomationId**

In `docs/automation-ids.md`, Models page section, add (matching neighbors' format):

```
ModelsManualInstallNote — Models page, inline note shown when a selected model is manual-install only
```

- [ ] **Step 5: Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 6: Windows gate**

```bash
./scripts/windows-gate.sh
```
Expected: `GATE: GREEN` (retry on known flakes).

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml src/Winpepper.App/Views/ModelsPage.xaml.cs docs/automation-ids.md
git commit -m "feat(app): download button fetches only selected-and-missing models; disabled when satisfied

'Download missing models' becomes 'Download selected models': it downloads
exactly the models chosen in the page's dropdowns (ASR, streaming, cleanup
while cleanup is enabled) that are not yet installed, via
SelectedModelsPolicy + DownloadSelectedAsync. The button disables live when
nothing it would fetch is missing. Manual-install-only selections (sotto)
are never attempted and get an inline explanatory note. Also fixes the
stale ASR 'Not downloaded' label after a bottom-button download.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 5: Cleanup card gated on CleanupEnabled (change 4)

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` (cleanup card ~:135-191: add `CleanupDisabledNote` after `CleanupCombo`)
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` (seed + subscribe in `OnNavigatedTo`, unsubscribe in `OnNavigatedFrom`, add `ApplyCleanupGate()`)
- Modify: `docs/automation-ids.md` (add `ModelsCleanupDisabledNote`)

**Interfaces:**
- Consumes: `App.Shell.CleanupVm` (`Winpepper.Core.ViewModels.CleanupSettingsViewModel`, shell singleton: `Enabled : bool`, `PropertyChanged`) — the only live channel for `CleanupEnabled` (there is no settings-level event; verified). `SelectedModelsPolicy.CleanupCardEnabled` / `CleanupOffNoteVisible` (Task 1). `_cleanupEnabled` field and `UpdateDownloadButtonState()` (Task 4).
- Produces: `CleanupDisabledNote` (`x:Name`, AutomationId `ModelsCleanupDisabledNote`), `private void ApplyCleanupGate()`, `private PropertyChangedEventHandler? _cleanupVmChanged;`.
- Precedent to match: `CleanupPage.xaml.cs` `ApplyModelCapabilities` — set `IsEnabled` on the interactive control only (gray-out is the platform's disabled rendering; never `Opacity`, never hide, never clear the selection), note toggles `Visibility`, subscription re-wired with `-=`/`+=` in `OnNavigatedTo`.
- Liveness: the page is rebuilt on every navigation (not cached), so re-seeding in `OnNavigatedTo` covers the "toggle in Cleanup tab, return to Models" flow; the `PropertyChanged` subscription additionally covers any flip while the page is open. Both paths are cheap.

- [ ] **Step 1: XAML — the gated note**

In `src/Winpepper.App/Views/ModelsPage.xaml`, inside the cleanup card, immediately AFTER the `CleanupCombo` element's closing `/>` and BEFORE the cleanup progress `<ListView ...>`, insert:

```xml
                    <TextBlock x:Name="CleanupDisabledNote"
                               AutomationProperties.AutomationId="ModelsCleanupDisabledNote"
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                               TextWrapping="Wrap"
                               Visibility="Collapsed"
                               Text="Cleanup is turned off — enable it in the Cleanup tab to choose a model." />
```

- [ ] **Step 2: Code-behind — gate application + live subscription**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`:

(a) Next to the existing `_autoInstallStatusChanged` field declaration, add:

```csharp
    private System.ComponentModel.PropertyChangedEventHandler? _cleanupVmChanged;
```

(b) Add this method near `UpdateDownloadButtonState`:

```csharp
    /// <summary>
    /// Gray out (never hide, never clear) the cleanup model chooser while
    /// cleanup is off — mirrors CleanupPage.ApplyModelCapabilities. The
    /// selection is preserved; only the combo disables and the note shows.
    /// </summary>
    private void ApplyCleanupGate()
    {
        CleanupCombo.IsEnabled = SelectedModelsPolicy.CleanupCardEnabled(_cleanupEnabled);
        CleanupDisabledNote.Visibility =
            SelectedModelsPolicy.CleanupOffNoteVisible(_cleanupEnabled)
                ? Visibility.Visible : Visibility.Collapsed;
    }
```

(c) In `OnNavigatedTo`, after the combo seeding lines (`AsrCombo.SelectedItem = ...`, `CleanupCombo.SelectedItem = ...`, `StreamingCombo.SelectedItem = ...`) and before the final `UpdateInstalledLabels()` call, add:

```csharp
        // Cleanup gate: seed from the shell's live cleanup view-model (the
        // page is rebuilt per navigation, so this re-seed alone covers the
        // "toggled in the Cleanup tab, came back" flow), then subscribe for
        // flips that happen while this page is open. There is no
        // settings-level change event; CleanupVm is the one live channel.
        var cleanupVm = App.Shell!.CleanupVm;
        _cleanupEnabled = cleanupVm.Enabled;
        ApplyCleanupGate();
        if (_cleanupVmChanged is not null) cleanupVm.PropertyChanged -= _cleanupVmChanged;
        _cleanupVmChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(Winpepper.Core.ViewModels.CleanupSettingsViewModel.Enabled))
            {
                _cleanupEnabled = App.Shell!.CleanupVm.Enabled;
                ApplyCleanupGate();
                UpdateDownloadButtonState(); // gate changes what counts as "selected"
            }
        };
        cleanupVm.PropertyChanged += _cleanupVmChanged;
```

(d) In `OnNavigatedFrom` (~:391-402), next to the existing `_autoInstallStatusChanged` unsubscribe, add:

```csharp
        if (_cleanupVmChanged is not null)
        {
            App.Shell!.CleanupVm.PropertyChanged -= _cleanupVmChanged;
            _cleanupVmChanged = null;
        }
```

- [ ] **Step 3: Register the AutomationId**

In `docs/automation-ids.md`, Models page section, add (matching neighbors' format):

```
ModelsCleanupDisabledNote — Models page, note shown when cleanup is disabled and the cleanup model chooser is gated off
```

- [ ] **Step 4: Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 5: Windows gate**

```bash
./scripts/windows-gate.sh
```
Expected: `GATE: GREEN` (retry on known flakes).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml src/Winpepper.App/Views/ModelsPage.xaml.cs docs/automation-ids.md
git commit -m "feat(app): gate the Models page cleanup card on CleanupEnabled with a live note

While cleanup is off, the cleanup model combo disables (grayed, value
preserved) with the note 'Cleanup is turned off — enable it in the
Cleanup tab to choose a model.', and the cleanup selection stops counting
toward the download button. Seeded per navigation and updated live via
App.Shell.CleanupVm.PropertyChanged, matching the CleanupPage
ApplyModelCapabilities precedent.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

---

### Task 6: Remove superseded VM entry points, final gates, evidence + smoke checklist

**Files:**
- Modify: `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs` (delete `DownloadMissingAsync` ~:64-91 and `DownloadStreamingAsync` ~:93-116 — no production caller remains after Tasks 3–4; verified production callers were only the page's two old handlers. Test callers span THREE files: `ModelsTabViewModelStreamingTests.cs` :46/:76/:112/:123, `ViewModels/ModelsTabViewModelTests.cs` :75/:92/:117/:139/:176/:215/:217, and `StreamingAutoInstallerTests.cs` :218 — all migrated or deleted below. `ModelCardViewModelDispatchTests.cs:13` mentions `DownloadMissingAsync` in a doc comment only — no call, no compile impact; leave it.)
- Modify: `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs` (migrate calls)
- Modify: `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs` (migrate the one call in `AutoInstall_and_models_card_download_never_run_concurrently` :218)
- Modify: `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs` (delete tests of deleted semantics; MIGRATE the three plumbing-contract tests)
- Modify: `docs/plans/2026-08-01-models-page-ux.md` (append evidence section)

**Interfaces:**
- Consumes: `DownloadSelectedAsync(IReadOnlyList<ModelDescriptor>, CancellationToken)` (Task 2) — the sole surviving download entry point on the VM (besides the auto-installer's own path, which is untouched).
- Produces: nothing new. `MissingModelsResolver` stays (still used by `StreamingAutoInstaller`/other callers — do NOT delete it; verify with `grep -rn "MissingModelsResolver" src/` before touching anything beyond the two methods).

- [ ] **Step 1: Delete the two methods**

In `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`, delete the entire `DownloadMissingAsync` and `DownloadStreamingAsync` methods (locate by name; both are `public async Task ...(CancellationToken ct)`). Do not touch `DownloadOneAsync`, `SharedOperationGateFor`, or the cards.

- [ ] **Step 2: Surface every broken caller**

```bash
cd /home/dan/code/winpepper/.worktrees/models-page-ux
grep -rn "DownloadMissingAsync\|DownloadStreamingAsync" src/ tests/
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: ZERO hits under `src/` (Step 1 deleted the definitions; the page's two old handlers were removed by Tasks 3–4 — if ANY `src/` hit remains, STOP: a production caller was missed; re-check Tasks 3–4 landed). Under `tests/`, expect hits in exactly THREE test files plus one comment-only mention:
- `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs` (:46, :76, :112, :123) — migrate in Step 3(a)
- `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs` (:218) — migrate in Step 3(b)
- `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs` (:75, :92, :117, :139, :176, :215, :217) — delete/migrate per Step 3(c)
- `tests/Winpepper.Models.Tests/ModelCardViewModelDispatchTests.cs:13` — doc-comment prose only, no call; leave as-is (it names the method to explain a historical crash; it compiles fine)

Build FAILS (CS1061) only in those three test files.

- [ ] **Step 3: Migrate the tests**

Apply these rules exactly:

(a) In `tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs` — these tests pin streaming download/extraction behavior worth keeping. Replace every call of the form:

```csharp
await vm.DownloadStreamingAsync(<token>);
```
with:

```csharp
await vm.DownloadSelectedAsync(
    new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! }, <token>);
```
keeping each test's arrangement and assertions unchanged (`DownloadSelectedAsync` routes the descriptor through the same `DownloadOneAsync` path the old method used, so download/progress/gate assertions still hold). If a test asserts that `RaiseIsSelectedInstalledChanged` fired ONLY on the streaming card, relax it to assert the streaming card fired (the new method raises on all three cards — covered by `Raises_IsSelectedInstalled_Changed_On_All_Three_Cards` in Task 2).

(b) In `tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs`, test `AutoInstall_and_models_card_download_never_run_concurrently` (:218) — this pins the shared per-downloader operation gate between the auto-installer and the Models page VM; it MUST be kept. It captures the task un-awaited (`var card = ...`), so apply the same descriptor mapping WITHOUT adding `await`. Replace:

```csharp
var card = vm.DownloadStreamingAsync(TestContext.Current.CancellationToken);
```
with:

```csharp
var card = vm.DownloadSelectedAsync(
    new[] { new ModelRegistry().Find(ModelRegistry.StreamingAsrName)! },
    TestContext.Current.CancellationToken);
```
Everything else in the test stays unchanged: `DownloadSelectedAsync` takes the same `_downloadGate` at entry, so `fake.SawOverlap == false` and `fake.EnteredCount == 2` still hold.

(c) In `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs` — split the `DownloadMissingAsync` callers into two groups:

DELETE exactly these three tests, which exercise the OLD selection semantics (ASR downloaded unconditionally; cleanup filtered through `MissingModelsResolver`; streaming excluded) — behavior that no longer exists anywhere; the new semantics are covered by `SelectedModelsPolicyTests` (what to download) and `ModelsTabViewModelDownloadSelectedTests` (that exactly the given set downloads):
- `DownloadMissingAsync_ManualInstallOnlySelection_IsSkippedGracefully` (:66)
- `DownloadMissingAsync_OnlyEnqueuesMissingSelected` (:83)
- `DownloadMissingAsync_AlwaysRoutesSelectedAsrThroughAuthoritativeProvisioning` (:99)

MIGRATE these three tests to `DownloadSelectedAsync` — they pin still-live plumbing contracts (cross-VM `_downloadGate` serialization; no ambient sync-context capture with a bounded dispatcher queue; single-flight progress-bridge burst behavior) that the Task 2 suite does NOT cover, and that `DownloadSelectedAsync` inherits via the same gate/`DownloadOneAsync` path. Do NOT delete them:
- `DownloadMissingAsync_DoesNotCaptureAmbientUiContextForEitherDescriptor` (:123)
- `DownloadMissingAsync_ShowsIntermediateBurstProgressWithoutGrowingUiQueue` (:160)
- `DownloadMissingAsync_SerializesViewModelsSharingDownloader` (:203)

Migration rules for those three (apply exactly):
1. Each VM in these tests is constructed with `currentAsrName: "parakeet-tdt-0.6b-v3"` and `currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m"` against an empty `_root`, so the old method downloaded exactly those two descriptors per call. Preserve that by passing the same two descriptors explicitly. At the top of each test add:
```csharp
var registry = new ModelRegistry();
var selected = new[]
{
    registry.Find("parakeet-tdt-0.6b-v3")!,
    registry.Find("qwen2.5-0.5b-instruct-q4_k_m")!,
};
```
(reuse `registry` for the VM constructor's first argument) and replace every `vm.DownloadMissingAsync(<token>)` / `firstVm.DownloadMissingAsync(...)` / `secondVm.DownloadMissingAsync(...)` call with `...DownloadSelectedAsync(selected, <token>)`, preserving whether the returned task was awaited or captured.
2. `DownloadSelectedAsync` raises `RaiseIsSelectedInstalledChanged` on all THREE cards (Task 2), where the old method raised on two. In the two dispatcher tests, relax the constant queue bound from 2 to 3 — change `dispatcher.MaxPendingCount.ShouldBeLessThanOrEqualTo(2)` to `ShouldBeLessThanOrEqualTo(3)` (both occurrences, :154 and :199) and update the adjacent comment to say "one installed-state notification per card, so the whole tab's constant upper bound is three". Leave the mid-burst `MaxPendingCount.ShouldBe(1)` asserts unchanged (single-flight bridge is unaffected).
3. In `..._SerializesViewModelsSharingDownloader`, keep `downloader.DownloadCount.ShouldBe(4, ...)` (2 VMs x 2 selected descriptors — the count is unchanged because the selected set matches what the old method computed) but update its reason string to "each request downloads its two selected descriptors, but downloader calls must never overlap".
4. Rename the three migrated tests' `DownloadMissingAsync_` prefix to `DownloadSelectedAsync_` so the names match the method they now exercise.

Keep any test in the file that does not call `DownloadMissingAsync`.

- [ ] **Step 4: Run the migrated tests, then the full Linux suite**

```bash
dotnet build tests/Winpepper.Models.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Models.Tests/bin/Release/net9.0/Winpepper.Models.Tests.dll
./scripts/linux-tests.sh
```
Expected: 0 failures; `LINUX SUITE: GREEN`.

- [ ] **Step 5: Commit the removal**

```bash
git add src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs tests/Winpepper.Models.Tests/ModelsTabViewModelStreamingTests.cs tests/Winpepper.Models.Tests/StreamingAutoInstallerTests.cs tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs
git commit -m "refactor(models): remove DownloadMissingAsync/DownloadStreamingAsync, superseded by DownloadSelectedAsync

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

- [ ] **Step 6: Full Windows gate**

```bash
./scripts/windows-gate.sh
```
Expected: `GATE: GREEN` (20–30 min timeout; retry on UNC MSB4025 / vsock interop flakes).

- [ ] **Step 7: Evidence + smoke-checklist commit**

Append to `docs/plans/2026-08-01-models-page-ux.md` an `## Evidence` section recording: date, `LINUX SUITE: GREEN` and `GATE: GREEN` confirmations (with retry notes if flakes occurred), and the commit SHAs of Tasks 1–6. Then commit with the owner's on-device smoke checklist in the message body (this is the FINAL commit — the checklist must be in ITS message):

```bash
git add docs/plans/2026-08-01-models-page-ux.md
git commit -m "docs(plans): evidence — gates green for Models page UX overhaul

On-device smoke checklist (owner's post-install verification):
1. Streaming model installed: streaming card shows a dropdown with
   'Nemotron Speech Streaming (0.6B, Q8_0 GGUF, English)' selected and
   'Installed'; the old 'Install streaming model' button is gone.
2. Select an uninstalled model in any dropdown: bottom button reads
   'Download selected models' and enables.
3. Everything selected is installed: bottom button is disabled
   (grayed, still visible).
4. Select the Sotto cleanup model while it is not installed: inline
   manual-install note appears; the download button does not attempt it.
5. Cleanup tab -> turn cleanup off -> back to Models: cleanup combo is
   grayed with the note 'Cleanup is turned off — enable it in the
   Cleanup tab to choose a model.'; selection preserved. Turn cleanup
   on -> combo re-enables, note disappears.
6. Download a missing model via the button: per-file progress rows
   appear under the matching card; on completion the label flips to
   'Installed' and the button grays out if nothing else is missing.

Co-authored-by: Amplifier <amplifier@users.noreply.github.com>"
```

Do NOT push. The root session merges, gates, and installs.
