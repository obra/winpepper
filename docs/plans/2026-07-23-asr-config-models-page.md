# Consolidate Speech-Recognition Config onto the Models Page + Default to Universal-3.5 Pro — Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make the Models page the single home for ALL speech-recognition
configuration (local model + AssemblyAI cloud provider/key/model/test/privacy),
remove the duplicate ASR section from the Recording page, and default the
AssemblyAI model to the latest **Universal-3.5 Pro** (`universal-3-5-pro`)
while keeping `universal-2` and all legacy aliases working.

**Architecture:** Two independent changes.
(1) A **pure, Linux-testable** change to `Winpepper.Asr` +
`Winpepper.Core` that flips the canonical AssemblyAI model id and default.
(2) A **Windows-only WinUI** move of the provider/cloud config from
`RecordingPage` into the `ModelsPage` ASR card. The pure change lands first
so the UI move builds on top of the corrected model metadata.

**Tech Stack:** C# / .NET 9, WinUI 3 (WindowsAppSDK), xUnit v3 + Shouldly.
Managed class-libs and their test projects build/run on Linux (`net9.0`
TFM); the WinUI `Winpepper.App` project is Windows-only.

## Global Constraints

- **Do NOT touch the keyboard hook or `packaging/`.** No changes to
  hotkey capture, the low-level hook, or the MSI/packaging tree.
- **No settings-file migration code.** Existing stored `AssemblyAiModel`
  values (including `universal-3-pro`) are respected as-is; the owner's
  machine is updated at deploy time. Do not write any upgrade/rewrite path.
- **Target framework for all buildable/testable work: `net9.0` only.**
  Never a `net9.0-windows*` TFM on Linux.
- **`Winpepper.App` (WinUI) does not build or test on Linux** — it targets
  `net9.0-windows10.0.19041.0` with WinUI/WindowsAppSDK deps. **Never build
  the whole solution on Linux** (`dotnet build winpepper.sln` WILL try to
  build the App and fail). Scope every Linux build/test to the specific
  class-lib + test projects named in each task. New App code stays wrapped
  in `#if WINDOWS` as defense-in-depth. App-layer work (Tasks 3 & 4) is
  verified only via the Windows Smoke Test Checklist at the end.
- **.NET SDK on Linux:** the repo root provisions `./.dotnet` (gitignored).
  The worktree does not have its own `./.dotnet`; run the SDK from the main
  repo checkout: `/home/dan/code/winpepper/.dotnet/dotnet` (9.0.100). A
  `dotnet` already on `PATH` resolving 9.0.x also satisfies `global.json`.
  In the commands below, `dotnet` means "the .NET 9 SDK" — substitute the
  absolute `./.dotnet/dotnet` path if `dotnet` is not on `PATH`.
- **Model id / label copy is verbatim.** Canonical id `universal-3-5-pro`;
  accepted alias `universal-3-pro`; labels
  `Universal-3.5 Pro - latest, most accurate` and
  `Universal-2 - faster, lower cost` (ASCII hyphen, exactly as written).
- **Test runner:** build the test project, then run its built dll with the
  in-process runner, filtering out Windows-only tests:
  `dotnet exec <test>.dll -notrait "Platform=Windows"`. `dotnet test` also
  works but `dotnet exec` is the proven-portable path. VSTest is unreliable
  here — do not rely on it.

---

## Baseline (verify before starting)

The full non-Windows suite is currently green (~810 tests; Core 278,
Asr 86, Platform 209 on the Linux TFM). The two projects this plan's pure
change touches:

- `tests/Winpepper.Asr.Tests` (86 tests) — model metadata, client, transcribers.
- `tests/Winpepper.Core.Tests` (278 tests) — settings defaults, etc.

Optional sanity baseline (not a committed step):

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

---

## File Structure

**Change 1 — pure model metadata + settings default (Tasks 1–2):**

- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiModels.cs` — flip
  canonical id, add aliases, new default, new labels.
- Modify: `tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs` — assert the
  new metadata, aliases, default, and the crash-guard invariant.
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs:19` — default flip.
- Modify: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` — assert
  the new default.

**Change 2 — Windows-only UI move (Tasks 3–4):**

- Modify: `src/Winpepper.App/Views/ModelsPage.xaml` — add provider picker +
  AssemblyAI cloud panel inside the existing ASR card.
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs` — port the AssemblyAI
  wiring (provider, key, model combo + canonicalization, test, toggles,
  status) from RecordingPage.
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` — delete the entire
  "Speech recognition" section (lines 88–133).
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs` — delete the
  AssemblyAI wiring block (lines 90–223) and the now-unused
  `using Microsoft.Extensions.Logging;`.

**Regression gate + manual verification (Task 5):** full non-Windows suite
run, then the Windows Smoke Test Checklist.

**Intentional non-changes (do NOT modify), noted so review does not flag
them as gaps:**

- `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:70` already sends the
  plural `speech_models` array — **verify, do not change.**
- `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs:6` `Model` default
  stays `"universal-2"`: it is **always overridden** at construction by
  `AppShell.cs:256` (`Model = settings.AssemblyAiModel`), so it is a dead
  fallback never used in production. Leaving it avoids scope creep; the
  effective default comes from `AppSettings.AssemblyAiModel` (Task 2).
- The Asr-stage config-error deep-link already navigates to the `"models"`
  tag (`MainWindow.xaml.cs:103` → `ModelsPage`). After this move the setting
  lives on that page, so the deep-link now lands exactly where the fix is —
  **no navigation code change needed.**
- `CloudProvider.IsCloud` / `CloudProviderTests.cs` treat
  `AssemblyAI/universal-3-pro` as valid by prefix, independent of model
  metadata — unaffected by the alias flip (`universal-3-pro` remains
  `IsKnown`). No change.

---

## UNRESOLVED COVERAGE GAP (declared, not deferred)

None. Every spec requirement maps to a task below. Two requirements are
verifiable **only on Windows** (the UI move) and are covered by the Windows
Smoke Test Checklist rather than an automated Linux test — this is the
established repo pattern for `Winpepper.App`, not a silent deferral. The
one **residual** the spec itself calls out (we cannot live-test whether the
AssemblyAI API accepts `universal-3-5-pro` without an API key) is documented
in the "Residual / known limitation" note after Task 2; the existing
invalid-model config-error surfacing + local fallback already handle a
dictation-time 400, so no behavior is left unbuilt.

---

### Task 1: Flip canonical AssemblyAI model id, aliases, default, and labels

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiModels.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces (relied on by Task 3's UI wiring and by existing code):
  - `AssemblyAiModels.Known : IReadOnlyList<ModelChoice>` — now
    `[("universal-3-5-pro", "Universal-3.5 Pro - latest, most accurate"),
    ("universal-2", "Universal-2 - faster, lower cost")]`.
  - `AssemblyAiModels.DefaultId : string` → `"universal-3-5-pro"`.
  - `AssemblyAiModels.IsKnown(string) : bool` — true for both listed ids and
    aliases `universal-3-pro`, `best`, `nano` (case-insensitive).
  - `AssemblyAiModels.CanonicalId(string) : string` — maps
    `universal-3-pro`→`universal-3-5-pro`, `best`→`universal-3-5-pro`,
    `nano`→`universal-2`; listed ids and custom/empty ids unchanged.
  - `ModelChoice(string Id, string Label)` record struct — unchanged shape.

- [ ] **Step 1: Rewrite the tests to assert the new metadata (failing test)**

Replace the entire contents of
`tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs` with:

```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiModelsTests
{
    [Fact]
    public void Known_ListsLatestThenFast_InOrder()
    {
        AssemblyAiModels.Known.Select(m => m.Id)
            .ShouldBe(new[] { "universal-3-5-pro", "universal-2" });
        AssemblyAiModels.Known[0].Label.ShouldBe("Universal-3.5 Pro - latest, most accurate");
        AssemblyAiModels.Known[1].Label.ShouldBe("Universal-2 - faster, lower cost");
    }

    [Theory]
    [InlineData("universal-3-5-pro", true)]   // canonical/listed premium id
    [InlineData("universal-2", true)]         // listed fast id
    [InlineData("UNIVERSAL-3-PRO", true)]     // pricing-page spelling, case-insensitive
    [InlineData("best", true)]                // deprecated AssemblyAI alias
    [InlineData("NANO", true)]                // deprecated alias, case-insensitive
    [InlineData("universal-9000", false)]     // typo -> not known
    [InlineData("", false)]
    public void IsKnown_RecognizesGoodIds(string id, bool expected)
        => AssemblyAiModels.IsKnown(id).ShouldBe(expected);

    [Fact]
    public void DefaultId_IsUniversal35Pro()
        => AssemblyAiModels.DefaultId.ShouldBe("universal-3-5-pro");

    [Theory]
    [InlineData("universal-3-pro", "universal-3-5-pro")]   // pricing alias -> canonical
    [InlineData("UNIVERSAL-3-PRO", "universal-3-5-pro")]   // case-insensitive
    [InlineData("best", "universal-3-5-pro")]              // deprecated alias -> premium
    [InlineData("nano", "universal-2")]                    // deprecated alias -> fast
    [InlineData("universal-3-5-pro", "universal-3-5-pro")] // already-listed id -> unchanged
    [InlineData("universal-2", "universal-2")]             // already-listed id -> unchanged
    [InlineData("my-custom-model", "my-custom-model")]     // custom id -> unchanged
    [InlineData("", "")]                                   // empty -> unchanged
    public void CanonicalId_MapsAliasesToListedIds(string id, string expected)
        => AssemblyAiModels.CanonicalId(id).ShouldBe(expected);

    // Crash guard (spec): the settings-page model combo canonicalizes a stored
    // value and then selects the matching combo item. A stored value that is now
    // an alias (e.g. "universal-3-pro") MUST canonicalize to an id present in
    // Known so the combo selects a real listed item instead of mis-selecting or
    // dropping to the custom escape hatch. This is the pure coverage of the
    // combo-selection logic that previously crashed the settings page.
    [Theory]
    [InlineData("universal-3-pro")]
    [InlineData("best")]
    [InlineData("nano")]
    [InlineData("universal-3-5-pro")]
    [InlineData("universal-2")]
    public void CanonicalId_EveryKnownAliasOrId_ResolvesToAListedModelId(string id)
    {
        var canonical = AssemblyAiModels.CanonicalId(id);
        AssemblyAiModels.Known
            .Any(m => string.Equals(m.Id, canonical, StringComparison.OrdinalIgnoreCase))
            .ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Build the test project and run to verify it FAILS**

Run:
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: FAIL. `Known_ListsLatestThenFast_InOrder`, `DefaultId_IsUniversal35Pro`,
and several `IsKnown`/`CanonicalId` cases fail because the source still has
`universal-2` first, `DefaultId => "universal-2"`, and the alias mapped the
wrong direction (`universal-3-5-pro` → `universal-3-pro`).

- [ ] **Step 3: Rewrite `AssemblyAiModels.cs` with the flipped metadata**

Replace the entire contents of
`src/Winpepper.Asr/Transcription/AssemblyAiModels.cs` with:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Known-good AssemblyAI speech-model ids and their user-facing labels.</summary>
public static class AssemblyAiModels
{
    public readonly record struct ModelChoice(string Id, string Label);

    public static IReadOnlyList<ModelChoice> Known { get; } = new[]
    {
        new ModelChoice("universal-3-5-pro", "Universal-3.5 Pro - latest, most accurate"),
        new ModelChoice("universal-2", "Universal-2 - faster, lower cost"),
    };

    public static string DefaultId => "universal-3-5-pro";

    // Accepted aliases map to the listed (canonical) id they represent.
    // AssemblyAI's docs' model-selection page and the API's default array use
    // "universal-3-5-pro" (our canonical, listed id); the pricing page uses
    // "universal-3-pro" for the SAME model, so accept it as an alias. Also accept
    // AssemblyAI's deprecated-but-still-routing aliases "best" and "nano".
    // Canonicalizing every alias to a listed id lets the settings-page picker
    // always select a real combo item for a stored value (see crash-guard test),
    // and keeps neither official spelling flagged as a "custom" model.
    private static readonly IReadOnlyDictionary<string, string> KnownAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["universal-3-pro"] = "universal-3-5-pro", // pricing-page spelling -> canonical
            ["best"] = "universal-3-5-pro",            // deprecated alias -> premium tier
            ["nano"] = "universal-2",                  // deprecated alias -> fast tier
        };

    public static bool IsKnown(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (Known.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
               || KnownAliases.ContainsKey(id));

    /// <summary>
    /// Maps an accepted alias to the listed model id it represents so callers can
    /// resolve any accepted spelling to a single listed id. Returns the input
    /// unchanged when it is already a listed id or an unrecognized (custom) id.
    /// </summary>
    public static string CanonicalId(string id)
        => !string.IsNullOrWhiteSpace(id) && KnownAliases.TryGetValue(id, out var canonical)
            ? canonical
            : id;
}
```

- [ ] **Step 4: Rebuild and run to verify the tests PASS**

Run:
```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS. All `AssemblyAiModelsTests` pass and the rest of the Asr
suite (client, transcriber, cloud-provider, fallback tests) remains green —
`universal-3-pro` is still `IsKnown` (now via alias), so `CloudProviderTests`
and any test passing `universal-3-pro`/`universal-2` still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiModels.cs tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs
git commit -m "feat(asr): make universal-3-5-pro the canonical/default AssemblyAI model"
```

---

### Task 2: Flip the persisted `AssemblyAiModel` default to `universal-3-5-pro`

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs:19`
- Test: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`

**Interfaces:**
- Consumes: nothing (string literal only; kept in sync with
  `AssemblyAiModels.DefaultId` from Task 1 by value).
- Produces: `AppSettings.AssemblyAiModel` default is now `"universal-3-5-pro"`,
  read by `AppShell.cs:256` (`Model = settings.AssemblyAiModel`) and by the
  Models-page model picker (Task 3).

- [ ] **Step 1: Update the defaults test (failing test)**

In `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`, replace the
`Defaults_UseFastAssemblyAiModel` test (lines 16–21) with:

```csharp
    [Fact]
    public void Defaults_UseLatestAssemblyAiModel()
    {
        var s = new AppSettings();
        s.AssemblyAiModel.ShouldBe("universal-3-5-pro");
    }
```

- [ ] **Step 2: Build the test project and run to verify it FAILS**

Run:
```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: FAIL. `Defaults_UseLatestAssemblyAiModel` fails — the source still
defaults `AssemblyAiModel` to `"universal-2"`.

- [ ] **Step 3: Update the default in `AppSettings.cs`**

In `src/Winpepper.Core/Settings/AppSettings.cs`, change line 19 from:

```csharp
    public string AssemblyAiModel { get; init; } = "universal-2"; // speech_model id sent to AssemblyAI
```

to:

```csharp
    // Default speech_model id sent to AssemblyAI. Kept in sync with
    // AssemblyAiModels.DefaultId ("universal-3-5-pro" = Universal-3.5 Pro, latest).
    // No migration for stored values: an existing "universal-2" or "universal-3-pro"
    // is respected as-is (universal-3-pro canonicalizes to the same model).
    public string AssemblyAiModel { get; init; } = "universal-3-5-pro";
```

- [ ] **Step 4: Rebuild and run to verify the tests PASS**

Run:
```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS. `Defaults_UseLatestAssemblyAiModel` passes; the other
defaults tests (`Defaults_UseLocalProvider`,
`AssemblyAi_Retention_Deadline_Keyterms_Defaults`) still pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Settings/AppSettings.cs tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs
git commit -m "feat(core): default persisted AssemblyAiModel to universal-3-5-pro"
```

**Residual / known limitation (spec-acknowledged, no code owed):** We cannot
live-test that the AssemblyAI API accepts `universal-3-5-pro` without an API
key. If a dictation-time request returns a 400 for that id, the existing
invalid-model config-error surfacing (`AppShell.cs:447`, "AssemblyAI model
rejected …") plus the local-transcriber fallback already handle it — the user
sees an actionable error and dictation still works locally. No additional
behavior is required by this plan.

---

### Task 3: Move the provider/cloud ASR config into the Models page ASR card (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml`
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs`

**Interfaces:**
- Consumes (from Task 1): `Winpepper.Asr.Transcription.AssemblyAiModels.Known`,
  `.DefaultId`, `.CanonicalId(string)`.
- Consumes (existing, via `App.Shell!`): `SettingsStore` (`Load()`),
  `AssemblyAiKeyStore` (`HasKey`, `Save`, `Clear`), `SettingsWriter`
  (`QueueAndFlushAsync(Func<AppSettings,AppSettings>)`), `AssemblyAiClient`
  (`IAssemblyAiClient.ValidateKeyAsync`), `AssemblyAiOptions`, `LogFactory`.
- Produces: the Models page now owns provider selection, key management, model
  selection, key testing, retention + keyterms toggles, and privacy disclosure.

> **Build/verify note:** `Winpepper.App` does not build on Linux. This task
> has no Linux test cycle; it is verified by the Windows Smoke Test Checklist
> (Task 5). Keep the code-behind thin and mirror the existing RecordingPage
> patterns exactly. All new code stays inside the existing `#if WINDOWS`
> region of the code-behind.

- [ ] **Step 1: Add the provider picker + AssemblyAI panel to the ASR card XAML**

In `src/Winpepper.App/Views/ModelsPage.xaml`, replace the entire ASR card
`<Border>` block (currently lines 15–71, the `<!-- ASR card -->` border)
with the following. This keeps the existing header, installed-status row,
local `AsrCombo` model picker, and download-progress `ListView` (the local
model remains the always-visible fallback), and inserts the provider picker
at the top of the card plus a collapsible AssemblyAI cloud panel:

```xml
            <!-- ASR card -->
            <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                    BorderThickness="1" CornerRadius="8" Padding="16">
                <StackPanel Spacing="12">
                    <StackPanel Orientation="Horizontal" Spacing="12">
                        <FontIcon Glyph="&#xE720;" FontSize="18" Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" VerticalAlignment="Center" />
                        <StackPanel>
                            <TextBlock Text="Speech recognition" Style="{ThemeResource BodyStrongTextBlockStyle}" />
                            <TextBlock Text="ASR &#8212; turns your voice into raw text"
                                       Style="{ThemeResource CaptionTextBlockStyle}"
                                       Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                        </StackPanel>
                    </StackPanel>

                    <!-- Provider selection (top of the card): where transcription runs -->
                    <ComboBox x:Name="AsrProviderCombo" Header="Provider"
                              AutomationProperties.AutomationId="ModelsAsrProviderCombo"
                              HorizontalAlignment="Stretch">
                        <ComboBoxItem Content="Local processing (on this PC)" Tag="local" />
                        <ComboBoxItem Content="AssemblyAI (cloud)" Tag="assemblyai" />
                    </ComboBox>

                    <!-- AssemblyAI cloud config: key + model + test + privacy + retention.
                         Collapsed unless the AssemblyAI provider is selected. -->
                    <StackPanel x:Name="AssemblyAiPanel" Spacing="8" Margin="0,4,0,0">
                        <TextBlock x:Name="AsrPrivacyText"
                                   Text="Cloud transcription sends your recorded audio to AssemblyAI. Winpepper asks AssemblyAI to delete your audio and transcript after transcription (deletion happens on AssemblyAI's servers and may not be immediate). Turn deletion off below to keep them per AssemblyAI's retention policy."
                                   TextWrapping="Wrap"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                   Style="{ThemeResource CaptionTextBlockStyle}" />
                        <PasswordBox x:Name="AssemblyAiKeyBox" Header="API key"
                                     AutomationProperties.AutomationId="ModelsAssemblyAiKeyBox"
                                     HorizontalAlignment="Stretch"
                                     PlaceholderText="Paste your AssemblyAI API key" />
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <Button x:Name="SaveKeyButton" Content="Save key" AutomationProperties.AutomationId="ModelsAssemblyAiSaveKeyButton" />
                            <Button x:Name="ClearKeyButton" Content="Clear key" AutomationProperties.AutomationId="ModelsAssemblyAiClearKeyButton" />
                            <Button x:Name="TestKeyButton" Content="Test" AutomationProperties.AutomationId="ModelsAssemblyAiTestKeyButton" />
                        </StackPanel>
                        <ComboBox x:Name="AssemblyAiModelCombo" Header="Model"
                                  AutomationProperties.AutomationId="ModelsAssemblyAiModelCombo"
                                  HorizontalAlignment="Stretch" />
                        <TextBox x:Name="AssemblyAiModelBox" Header="Custom model id"
                                 HorizontalAlignment="Stretch"
                                 Visibility="Collapsed"
                                 PlaceholderText="Advanced: exact AssemblyAI speech_model id" />
                        <TextBlock x:Name="AssemblyAiModelWarning"
                                   Visibility="Collapsed"
                                   Text="Custom model ids are not validated and will fail at dictation time if wrong."
                                   TextWrapping="Wrap"
                                   Foreground="{ThemeResource SystemFillColorCautionBrush}"
                                   Style="{ThemeResource CaptionTextBlockStyle}" />
                        <ToggleSwitch x:Name="AssemblyAiDeleteToggle" Header="Delete audio from AssemblyAI after transcription" />
                        <ToggleSwitch x:Name="AssemblyAiKeytermsToggle" Header="Send preferred terms as keyterms" />
                        <TextBlock Text="Preferred terms may incur extra AssemblyAI cost on some plans. Off by default. Your corrections list is always applied via custom spelling at no extra cost."
                                   TextWrapping="Wrap"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                   Style="{ThemeResource CaptionTextBlockStyle}" />
                        <TextBlock x:Name="AsrStatusText"
                                   TextWrapping="Wrap"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                   Style="{ThemeResource CaptionTextBlockStyle}" />
                    </StackPanel>

                    <!-- Local model (always available as fallback; downloadable via the button below) -->
                    <TextBlock Text="Local model (fallback)" Style="{ThemeResource BodyStrongTextBlockStyle}" Margin="0,4,0,0" />
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <FontIcon x:Name="AsrInstalledIcon" Glyph="&#xE73E;" FontSize="14" Foreground="{ThemeResource SystemFillColorSuccessBrush}" VerticalAlignment="Center" />
                        <FontIcon x:Name="AsrNotInstalledIcon" Glyph="&#xE896;" FontSize="14" Foreground="{ThemeResource TextFillColorSecondaryBrush}" VerticalAlignment="Center" Visibility="Collapsed" />
                        <TextBlock x:Name="AsrInstalledText" AutomationProperties.AutomationId="ModelsAsrInstalledLabel" VerticalAlignment="Center" />
                    </StackPanel>
                    <ComboBox x:Name="AsrCombo"
                              AutomationProperties.AutomationId="ModelsAsrCombo"
                              Header="Active local model"
                              HorizontalAlignment="Stretch"
                              ItemsSource="{x:Bind ViewModel.AsrCard.Available, Mode=OneWay}"
                              DisplayMemberPath="DisplayName"
                              SelectionChanged="OnAsrChanged" />
                    <ListView ItemsSource="{x:Bind ViewModel.AsrCard.ProgressByFile, Mode=OneWay}" SelectionMode="None">
                        <ListView.ItemContainerStyle>
                            <Style TargetType="ListViewItem">
                                <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                                <Setter Property="Padding" Value="0" />
                                <Setter Property="MinHeight" Value="0" />
                            </Style>
                        </ListView.ItemContainerStyle>
                        <ListView.ItemTemplate>
                            <DataTemplate x:DataType="models:DownloadProgress">
                                <StackPanel Spacing="4" Padding="0,6">
                                    <Grid ColumnSpacing="8">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="*" />
                                            <ColumnDefinition Width="Auto" />
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Grid.Column="0" Text="{x:Bind FileRelativePath}"
                                                   Style="{ThemeResource CaptionTextBlockStyle}"
                                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                                                   TextTrimming="CharacterEllipsis" />
                                        <TextBlock Grid.Column="1" Text="{x:Bind ProgressDisplay}"
                                                   Style="{ThemeResource CaptionTextBlockStyle}"
                                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                                    </Grid>
                                    <ProgressBar Value="{x:Bind PercentComplete}" Minimum="0" Maximum="100" />
                                </StackPanel>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>
            </Border>
```

Note: the local-model control names (`AsrInstalledIcon`,
`AsrNotInstalledIcon`, `AsrInstalledText`, `AsrCombo`) are preserved exactly
so the existing code-behind (`UpdateInstalledLabels`, `OnAsrChanged`,
`x:Bind ViewModel.AsrCard.*`) keeps working unchanged.

- [ ] **Step 2: Port the AssemblyAI wiring into the Models page code-behind**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, add a call to a new
`WireSpeechProvider(...)` method inside `OnNavigatedTo`, immediately after
line 55 (`UpdateInstalledLabels();`) and before the `try { ... }` readiness
block. Change:

```csharp
        AsrCombo.SelectedItem = ViewModel.AsrCard.SelectedDescriptor;
        CleanupCombo.SelectedItem = ViewModel.CleanupCard.SelectedDescriptor;
        UpdateInstalledLabels();
        try
```

to:

```csharp
        AsrCombo.SelectedItem = ViewModel.AsrCard.SelectedDescriptor;
        CleanupCombo.SelectedItem = ViewModel.CleanupCard.SelectedDescriptor;
        UpdateInstalledLabels();
        WireSpeechProvider(s);
        try
```

Then add the following method to the `ModelsPage` class (place it after
`OnCleanupChanged`, before `OnDownloadMissing`). It is a faithful port of the
RecordingPage AssemblyAI wiring, using `App.Shell!` for services and the
already-loaded `AppSettings s`:

```csharp
    // All speech-recognition provider config lives here (owner decision: the
    // model section owns ASR config, including the API key). Ported verbatim
    // from the former RecordingPage "Speech recognition" section so behavior
    // (debounced settings writes, honest key testing, model canonicalization +
    // custom escape hatch) is unchanged.
    private void WireSpeechProvider(Winpepper.Core.Settings.AppSettings current)
    {
        var shell = App.Shell!;
        var keyStore = shell.AssemblyAiKeyStore;

        // Provider picker
        AsrProviderCombo.SelectedIndex =
            string.Equals(current.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AssemblyAiPanel.Visibility = AsrProviderCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AsrProviderCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AsrProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "local";
            AssemblyAiPanel.Visibility = tag == "assemblyai" ? Visibility.Visible : Visibility.Collapsed;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AsrProvider = tag });
        };

        // Model picker: known ids + an "Advanced/custom" escape hatch.
        const string CustomTag = "__custom__";
        AssemblyAiModelCombo.Items.Clear();
        foreach (var m in Winpepper.Asr.Transcription.AssemblyAiModels.Known)
            AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = "Advanced / custom\u2026", Tag = CustomTag });

        void SelectModelInCombo(string modelId)
        {
            // A model can be "known" via an accepted alias (e.g. "universal-3-pro")
            // that has no dedicated combo item; canonicalize first so any accepted
            // spelling resolves to the listed id, then look the item up safely. If no
            // listed item matches (truly custom id) we fall back to the custom item
            // rather than throwing.
            var canonical = Winpepper.Asr.Transcription.AssemblyAiModels.CanonicalId(modelId);
            var matchIndex = -1;
            for (var i = 0; i < AssemblyAiModelCombo.Items.Count; i++)
            {
                var tag = (string?)((ComboBoxItem)AssemblyAiModelCombo.Items[i]).Tag;
                if (tag != CustomTag && string.Equals(tag, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }
            }

            var hasItem = matchIndex >= 0;
            AssemblyAiModelCombo.SelectedIndex = hasItem ? matchIndex : AssemblyAiModelCombo.Items.Count - 1; // the custom item
            var isCustom = !hasItem;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelBox.Text = isCustom ? modelId : "";
        }
        SelectModelInCombo(current.AssemblyAiModel);

        AssemblyAiModelCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AssemblyAiModelCombo.SelectedItem as ComboBoxItem)?.Tag as string
                      ?? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId;
            var isCustom = tag == CustomTag;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            if (!isCustom)
                _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = tag });
        };
        AssemblyAiModelBox.LostFocus += (_, _) =>
        {
            var model = string.IsNullOrWhiteSpace(AssemblyAiModelBox.Text)
                ? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId
                : AssemblyAiModelBox.Text.Trim();
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = model });
        };

        // Retention + keyterms toggles.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = AssemblyAiDeleteToggle.IsOn });
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = AssemblyAiKeytermsToggle.IsOn });

        // Key status
        AsrStatusText.Text = keyStore.HasKey ? "A key is saved on this PC." : "No key saved.";

        SaveKeyButton.Click += (_, _) =>
        {
            var key = AssemblyAiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) { AsrStatusText.Text = "Enter a key first."; return; }
            keyStore.Save(key.Trim());
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key saved on this PC.";
        };

        ClearKeyButton.Click += (_, _) =>
        {
            keyStore.Clear();
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key cleared.";
        };

        TestKeyButton.Click += async (_, _) =>
        {
            var typed = AssemblyAiKeyBox.Password;
            var hasTyped = !string.IsNullOrWhiteSpace(typed);
            if (!hasTyped && !keyStore.HasKey) { AsrStatusText.Text = "Enter or save a key before testing."; return; }

            AsrStatusText.Text = hasTyped ? "Testing the key you typed\u2026" : "Testing the saved key\u2026";
            try
            {
                Winpepper.Asr.Transcription.IAssemblyAiClient clientToTest = shell.AssemblyAiClient;
                if (hasTyped)
                {
                    // Validate exactly what the user typed, not a previously saved key.
                    var typedKey = typed.Trim();
                    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    clientToTest = new Winpepper.Asr.Transcription.AssemblyAiClient(
                        http, () => typedKey, shell.AssemblyAiOptions,
                        shell.LogFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ok = await clientToTest.ValidateKeyAsync(cts.Token);
                if (ok && hasTyped)
                {
                    keyStore.Save(typed.Trim());          // typed key is valid -> save it
                    AssemblyAiKeyBox.Password = "";
                    AsrStatusText.Text = "Typed key is valid and was saved on this PC.";
                }
                else
                {
                    AsrStatusText.Text = ok
                        ? "Saved key is valid."
                        : (hasTyped ? "Typed key rejected (401). Check the key." : "Saved key rejected (401). Check the key.");
                }
            }
            catch (Exception ex)
            {
                AsrStatusText.Text = $"Test failed: {ex.Message}";
            }
        };
    }
```

Note: `ModelsPage.xaml.cs` already has `using Microsoft.Extensions.Logging;`
(line 2) and `using Microsoft.UI.Xaml;` / `using Microsoft.UI.Xaml.Controls;`
so `Visibility`, `ComboBoxItem`, `ToggleSwitch`, `CreateLogger`, etc. resolve
without new usings. `HttpClient`, `TimeSpan`, `CancellationTokenSource` come
from implicit global usings (as they do in RecordingPage today).

- [ ] **Step 3: Sanity-check that nothing outside the App project was touched**

Run:
```bash
git status --short
```
Expected: only `src/Winpepper.App/Views/ModelsPage.xaml` and
`src/Winpepper.App/Views/ModelsPage.xaml.cs` are modified. (No Linux build
step: `Winpepper.App` targets a Windows-only TFM and is verified via the
Windows Smoke Test Checklist in Task 5.)

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml src/Winpepper.App/Views/ModelsPage.xaml.cs
git commit -m "feat(app): move AssemblyAI provider/key/model config into the Models page ASR card"
```

---

### Task 4: Remove the Speech-recognition section from the Recording page (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml`
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs`

**Interfaces:**
- Consumes: nothing new. Removes the RecordingPage's ownership of ASR config;
  mic device, hotkeys, sounds, options, and test-dictation stay.
- Produces: RecordingPage has NO speech-recognition UI or wiring after this
  task. The named controls `AsrProviderCombo`, `AsrPrivacyText`,
  `AssemblyAiPanel`, `AssemblyAiKeyBox`, `SaveKeyButton`, `ClearKeyButton`,
  `TestKeyButton`, `AssemblyAiModelCombo`, `AssemblyAiModelBox`,
  `AssemblyAiModelWarning`, `AssemblyAiDeleteToggle`,
  `AssemblyAiKeytermsToggle`, `AsrStatusText` no longer exist on RecordingPage.

> **Build/verify note:** Windows-only; no Linux test cycle. The XAML deletion
> and the code-behind deletion MUST land in the same commit — the code-behind
> references the deleted named controls, so removing only one side would break
> the Windows build. Verified via the Windows Smoke Test Checklist (Task 5).

- [ ] **Step 1: Delete the Speech-recognition section from the XAML**

In `src/Winpepper.App/Views/RecordingPage.xaml`, delete the entire
`<!-- Speech recognition -->` block — lines 88–133 inclusive, i.e. the
comment through the closing `</StackPanel>` of the speech-recognition
`StackPanel`. After deletion, the file ends:

```xml
            <!-- Test -->
            <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                    BorderThickness="1" CornerRadius="8" Padding="16">
                <StackPanel Spacing="12">
                    <TextBlock Text="Test dictation" Style="{ThemeResource BodyStrongTextBlockStyle}" />
                    <TextBlock Text="Focus the box, press your hotkey, and say a sentence."
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    <Grid ColumnSpacing="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBox x:Name="TestBox" AutomationProperties.AutomationId="RecordingTestTextBox" PlaceholderText="Test dictation lands here..." />
                        <Button Grid.Column="1" Content="Focus" Click="OnFocusTestBox" AutomationProperties.AutomationId="RecordingFocusTestBoxButton" VerticalAlignment="Bottom" />
                    </Grid>
                </StackPanel>
            </Border>
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Delete the AssemblyAI wiring from the code-behind**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, delete the entire
"Speech recognition (AssemblyAI)" block — lines 90–223 inclusive, i.e. from
the `// Speech recognition (AssemblyAI)` comment through the closing `};` of
the `TestKeyButton.Click` handler. The method body around the edit must read
(the `AutostartToggle` wiring that precedes it stays; `RestartLevelMeter` at
the end stays):

```csharp
            else _shell.Autostart.Disable();
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = AutostartToggle.IsOn });
        };

        RestartLevelMeter(vm.MicDeviceId);
    }
```

- [ ] **Step 3: Remove the now-unused logging using directive**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, delete line 2:

```csharp
using Microsoft.Extensions.Logging;
```

Its only use was `shell.LogFactory.CreateLogger<...>()` inside the deleted
`TestKeyButton.Click` handler. (Leave `using Winpepper.Audio;` and the other
usings — they are still used by the mic/level-meter code.)

- [ ] **Step 4: Sanity-check the diff scope and confirm no ASR names remain**

Run:
```bash
git status --short
grep -n "AssemblyAi\|AsrProvider\|AsrStatus\|AsrPrivacy" src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
```
Expected: only the two RecordingPage files are modified; the `grep` returns
**no matches** (all ASR/AssemblyAI references are gone from RecordingPage).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "feat(app): remove the duplicate speech-recognition section from the Recording page"
```

---

### Task 5: Full non-Windows regression gate + Windows smoke checklist

**Files:** none modified (verification only).

**Interfaces:** none.

- [ ] **Step 1: Build and run the full non-Windows managed suite**

Build the buildable/testable projects and run each test dll with the
in-process runner, filtering Windows-only tests. Run all test projects that
build on Linux:

```bash
for p in Winpepper.Core.Tests Winpepper.Asr.Tests Winpepper.Platform.Tests \
         Winpepper.Audio.Tests Winpepper.Models.Tests Winpepper.History.Tests \
         Winpepper.Cleanup.Tests Winpepper.Corrections.Tests; do
  proj="tests/$p/$p.csproj"
  [ -f "$proj" ] || continue
  dotnet build "$proj" -f net9.0 -p:EnableWindowsTargeting=true || exit 1
  dotnet exec "tests/$p/bin/Debug/net9.0/$p.dll" -notrait "Platform=Windows" || exit 1
done
```

Expected: every project builds and its non-Windows tests PASS. The suite
total is ~810 green (Core 278 incl. the updated default test, Asr 86 incl.
the updated model-metadata tests, Platform 209, plus the remaining managed
projects). Zero failures. (If `winpepper.sln` enumerates fewer/more test
projects, run whichever ones exist — the two that matter for this change are
`Winpepper.Core.Tests` and `Winpepper.Asr.Tests`.)

- [ ] **Step 2: No commit** — this is a gate, not a change. If anything
  fails, return to the owning task, fix, re-run, and only then proceed.

**Windows Smoke Test Checklist (manual, run on a Windows host after merge —
the WinUI page moves compile and run only on Windows):**

1. **Models page is the one-stop ASR config.** Open Settings → Models. The
   "Speech recognition" card shows, in order: a **Provider** picker (Local
   processing / AssemblyAI (cloud)) at the top; an AssemblyAI panel (API key
   box, Save/Clear/Test, **Model** picker, retention + keyterms toggles,
   privacy text, status line); and the local-model fallback (installed
   status, "Active local model" picker, download progress). The "Download
   missing models" button still works.
2. **Model default.** With no prior stored value, the Model picker shows
   **"Universal-3.5 Pro - latest, most accurate"** selected, and
   "Universal-2 - faster, lower cost" plus "Advanced / custom…" are present.
3. **Recording page has NO ASR section.** Open Settings → Recording. It shows
   Hotkeys, Microphone, Options, Test dictation — and **no** "Speech
   recognition" section, provider picker, key box, or model picker.
4. **Stored-alias reopen does not crash (regression of the historical
   settings-page crash).** Set `AssemblyAiModel` to `universal-3-pro` in the
   settings file (or select the premium model, which persists the canonical
   id, then hand-edit to the alias), restart, open Models. The page loads
   without crashing and the Model picker selects the **Universal-3.5 Pro**
   item (not "Advanced / custom"). This is the live counterpart to the pure
   `CanonicalId_EveryKnownAliasOrId_ResolvesToAListedModelId` test.
5. **Provider switch + key test still work.** On Models, switch Provider to
   AssemblyAI (cloud) → the AssemblyAI panel appears; switch back to Local →
   it collapses. With a saved/typed key, click **Test** → status shows a
   valid/invalid/appropriate message (honest behavior; typed-and-valid key
   gets saved). Selecting a different Model persists it (debounced write).
6. **Deep-link lands correctly.** Trigger an Asr-stage config error (e.g. set
   an invalid custom model id and dictate) → the error's "open settings"
   deep-link navigates to the Models page, where the model setting now lives.

---

## Self-Review

**1. Spec coverage.**

- Change 1 — "move the ENTIRE provider/cloud configuration into the Models
  page's Speech recognition card; provider picker at top; local UI stays
  visible; RecordingPage has NO speech-recognition section" → **Task 3**
  (add to Models) + **Task 4** (remove from Recording). Local model UI kept
  visible as the "Local model (fallback)" sub-section. ✔
- "Move the code-behind wiring faithfully (debounced writes, honest Test,
  model canonicalization + custom option, config-error status)" → Task 3
  Step 2 ports the wiring verbatim (`QueueAndFlushAsync`, `ValidateKeyAsync`,
  `SelectModelInCombo` + `CanonicalId`, custom escape hatch). ✔
- "Asr-stage error deep-links navigate to the 'models' tag, which now lands
  where the setting lives" → File Structure "Intentional non-changes" note +
  smoke item 6. ✔
- Change 2(a) — flip canonical id to `universal-3-5-pro`, make
  `universal-3-pro` the alias → Task 1 Step 3 (`Known` lists
  `universal-3-5-pro`; `KnownAliases["universal-3-pro"]="universal-3-5-pro"`).
  ✔
- Change 2(b) — `DefaultId => "universal-3-5-pro"` → Task 1. ✔
- Change 2(c) — labels exactly "Universal-3.5 Pro - latest, most accurate"
  and "Universal-2 - faster, lower cost", both in picker plus Custom option →
  Task 1 (labels) + Task 3 XAML/wiring (both + "Advanced / custom…"). ✔
- Change 2(d) — accept `best`→`universal-3-5-pro`, `nano`→`universal-2` →
  Task 1 `KnownAliases`. ✔
- "Update AppSettings.AssemblyAiModel default + tests" → Task 2. ✔
- "NO settings-file migration code; existing stored values respected" →
  Global Constraints + Task 2 comment (no migration path written). ✔
- "verify AssemblyAiClient sends plural speech_models array, don't change" →
  File Structure "Intentional non-changes" (verified at
  `AssemblyAiClient.cs:70`). ✔
- "Do NOT touch keyboard hook or packaging/" → Global Constraints. ✔
- "canonicalization flip makes the settings-page crash pattern LIVE again;
  cover with a pure test" → Task 1 Step 1
  `CanonicalId_EveryKnownAliasOrId_ResolvesToAListedModelId` + smoke item 4.
  ✔
- "Run the FULL non-Windows suite" → Task 5 Step 1. ✔
- "End with a Windows smoke checklist" (all listed items) → Task 5 checklist
  items 1–6 map 1:1 to the spec's required smoke checks. ✔
- Residual: "if a dictation-time 400 reveals the API rejects
  universal-3-5-pro, existing surfacing + fallback handle it — note in plan"
  → Residual note after Task 2. ✔

**1b. No silent deferrals.** The only requirements not proven by a Linux
automated test are the WinUI page moves (Tasks 3–4). Their observable
production outcomes are proven by the Windows Smoke Test Checklist (items
1–6) run on a Windows host — the established repo pattern for `Winpepper.App`,
which cannot build on Linux. No stubs/mocks/fakes stand in for behavior: the
ported wiring calls the real `SettingsWriter`, real `AssemblyAiKeyStore`, and
real `AssemblyAiClient`. The crash-prevention invariant additionally has a
pure automated test (Task 1). No requirement is moved to "known limitations"
or "future work."

**2. Placeholder scan.** No TBD/TODO/"handle edge cases"/"similar to Task N"
placeholders; every code step shows complete code, every command shows
expected output.

**3. Type consistency.** `AssemblyAiModels.Known` / `.DefaultId` /
`.CanonicalId(string)` / `.IsKnown(string)` and the `ModelChoice(Id, Label)`
shape are used identically in Task 1 (definition + tests) and Task 3 (UI
wiring). Control names in Task 3's XAML (`AsrProviderCombo`, `AssemblyAiPanel`,
`AssemblyAiKeyBox`, `SaveKeyButton`, `ClearKeyButton`, `TestKeyButton`,
`AssemblyAiModelCombo`, `AssemblyAiModelBox`, `AssemblyAiModelWarning`,
`AssemblyAiDeleteToggle`, `AssemblyAiKeytermsToggle`, `AsrStatusText`) match
exactly the names referenced by Task 3's `WireSpeechProvider` code-behind, and
are exactly the set Task 4 removes from RecordingPage. `AppSettings` record
fields used in `s with { ... }` (`AsrProvider`, `AssemblyAiModel`,
`AssemblyAiDeleteAfterTranscribe`, `AssemblyAiKeytermsEnabled`) all exist in
`AppSettings.cs`. Consistent. ✔
