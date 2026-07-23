# Settings & Setup UX Batch Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Ship four settings/setup UX improvements for Winpepper — remove the
redundant hardcoded app-name corrector, make settings/onboarding survive MSI
upgrades, make setup skip already-resolved steps and download only missing
models, give the main window a sensible default size that persists, and add a
default-off "offer to learn corrections" toggle.

**Architecture:** All behavior-bearing logic is pushed into pure-managed
libraries (`Winpepper.Core`, `Winpepper.Cleanup`, `Winpepper.Models`) so it
runs and is tested on Linux. Thin WinUI wiring in `Winpepper.App`
(`#if WINDOWS`) calls that logic and is verified only in the Windows smoke
test. New durability primitives (corrupt-file backup, a `QueueAndFlushAsync`
write) and new pure decision helpers (`WindowSizePolicy`, `PostPasteGate`,
onboarding first-unresolved-step) carry the spec requirements under test.

**Tech Stack:** C# / .NET 9 (multi-targeted `net9.0` + `net9.0-windows...`;
we build/test the `net9.0` target on Linux), xUnit v3, Shouldly assertions.

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
  (`src/Winpepper.Platform/Hotkeys/*`) and packaging (`packaging/`).
- **WinUI code is Linux-unbuildable and Linux-untestable.** Every file under
  `src/Winpepper.App` is wrapped in `#if WINDOWS`. Changes there are kept thin,
  call the tested pure-managed logic, and are verified in the **Windows Smoke
  Test Checklist** at the end of this plan — they are NOT deferred or stubbed.
- **Docs:** `README.md` is the only end-user markdown doc; this plan under
  `docs/plans/` is a working/agent doc and is fine. Do not add other end-user
  docs.
- **Commits:** focused and atomic; use `feat:`/`fix:`/`test:`/`refactor:`
  prefixes and the standard Amplifier co-author trailer (shown in each commit
  step).

---

## Scope Check

This batch spans five loosely-coupled subsystems (cleanup post-pass, settings
durability, onboarding flow, main-window sizing, post-paste learning). Per the
writing-plans scope guidance this could be five plans; it is delivered here as
one plan because the workflow requested a single batch, and each task below
produces its own working, independently-testable deliverable with its own test
coverage. There is **no** single system-wide end-to-end test possible on Linux
because the integration surface is WinUI (window, pages, pipeline host); that
whole-system verification is the **Windows Smoke Test Checklist** at the end.

---

## File Structure

**Task 1 — remove app-name corrector**
- Delete: `src/Winpepper.Cleanup/AppNameCorrector.cs`
- Delete: `tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs`
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` (post-pass helper + comment)
- Modify: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (remove 2 tests
  that assert the removed behavior)

**Task 2 — corrupt-settings backup**
- Modify: `src/Winpepper.Core/Settings/SettingsStore.cs`
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify (Windows): `src/Winpepper.App/Hosting/AppShell.cs` (pass a log sink)

**Task 3 — durable-write primitive**
- Modify: `src/Winpepper.Core/Settings/ISettingsWriter.cs`
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Modify: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Task 4 — onboarding checkpoints + skip completion**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs` (OnSkip)

**Task 5 — settings-page flush-on-commit**
- Modify: `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/RecordingPage.xaml.cs` (Autostart
  toggle writes durably)

**Task 6 — onboarding hydration + first-unresolved-step**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs`
  (compute resolution flags, call `InitializeFrom`)

**Task 7 — real onboarding downloader + missing-only scope**
- Modify: `tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs`
  (swap the `() => Task.CompletedTask` stub for the real downloader)

**Task 8 — main window default size + persistence**
- Create: `src/Winpepper.Core/WindowSizePolicy.cs`
- Create: `tests/Winpepper.Core.Tests/WindowSizePolicyTests.cs`
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (nullable size fields)
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/MainWindow.xaml.cs`

**Task 9 — post-paste learning toggle (default off)**
- Create: `src/Winpepper.Core/Learning/PostPasteGate.cs`
- Create: `tests/Winpepper.Core.Tests/Learning/PostPasteGateTests.cs`
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (new bool field)
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify: `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs` (new
  property)
- Modify: `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Hosting/PipelineHost.cs` (thread the
  setting, gate both trigger sites)
- Modify (Windows): `src/Winpepper.App/Hosting/AppShell.cs` (pass the setting)
- Modify (Windows): `src/Winpepper.App/Views/RecordingPage.xaml` +
  `RecordingPage.xaml.cs` (new ToggleSwitch)

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
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

- [ ] **Step 2: Verify dotnet resolves**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a `latestFeature` roll-forward like `9.0.1xx`).

- [ ] **Step 3: Confirm nothing to commit**

Run: `git status --short`
Expected: no `.dotnet/` entries (it is gitignored). No commit is made in this
task.

---

### Task 1: Remove the hardcoded app-name corrector

**Files:**
- Delete: `src/Winpepper.Cleanup/AppNameCorrector.cs`
- Delete: `tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs`
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs:124-126,186-190`
- Modify: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs:230-259`

**Interfaces:**
- Consumes: `CaseAwareReplacer.Apply(string text, IReadOnlyDictionary<string, string> replacements)`
  (existing; already the first half of the post-pass) and
  `CorrectionsData.Replacements` (existing; typed `IReadOnlyDictionary<string, string>`).
- Produces: `CleanupRunner.ApplyDeterministicPostPass` now runs ONLY the
  user-configured `CaseAwareReplacer` pass; the `AppNameCorrector` type no
  longer exists anywhere in the tree.

**Why:** the hardcoded `AppNameCorrector` duplicates the user-facing
corrections mechanism (`corrections.json` Replacements + the Corrections page).
Removing it returns that decision to the user.

- [ ] **Step 1: Delete the corrector class and its unit tests**

Run:
```bash
git rm src/Winpepper.Cleanup/AppNameCorrector.cs \
       tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs
```

- [ ] **Step 2: Reduce the post-pass to the user-corrections replacer**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, replace this block (currently
lines 183-190):

```csharp
    // Deterministic post-pass shared by the LLM-success and fallback paths:
    // user-configured corrections first, then the built-in app-name mishearing
    // correction. Applied on every path so injected text always benefits.
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        var withCorrections = CaseAwareReplacer.Apply(text, corrections.Replacements);
        return AppNameCorrector.Apply(withCorrections);
    }
```

with:

```csharp
    // Deterministic post-pass shared by the LLM-success and fallback paths:
    // apply the user-configured corrections (corrections.json Replacements).
    // Applied on every path so injected text always benefits. There is no
    // built-in app-name correction: users add their own via the Corrections
    // page if they want it.
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        return CaseAwareReplacer.Apply(text, corrections.Replacements);
    }
```

Leave the unrelated `"<CORRECTION-HINTS>"` entry in `HardEchoMarkers`
(line 144) untouched.

- [ ] **Step 3: Remove the two CleanupRunner tests that assert the removed behavior**

In `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`, delete these two
methods in full (currently lines 230-259 — `Run_LlmPath_AppNameMishearingCorrected`
and `Run_FallbackPath_AppNameMishearingCorrected`), leaving the closing `}` of
the class:

```csharp
    [Fact]
    public async Task Run_LlmPath_AppNameMishearingCorrected()
    {
        // LLM returns plausible text that still contains the ASR mishearing.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "Testing wheat pepper. How's it going?",
        });
        var result = await runner.RunAsync("testing wheat pepper how's it going",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Testing Winpepper. How's it going?");
    }

    [Fact]
    public async Task Run_FallbackPath_AppNameMishearingCorrected()
    {
        // Backend throws -> FallbackBackendError -> raw transcript is what gets
        // injected. The app-name correction must still be applied there.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Throw = new InvalidOperationException("boom"),
        });
        var result = await runner.RunAsync("Testing wheat pepper.",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("Testing Winpepper.");
    }
```

- [ ] **Step 4: Build the Cleanup tests and verify they compile and pass without the corrector**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds with **no** reference to `AppNameCorrector`
(a leftover reference would fail as `CS0103: The name 'AppNameCorrector' does
not exist`). Test run: `Failed: 0`. The remaining `CleanupRunnerTests`
(echo-guard, plausibility, corrections round-trip) still pass; the two deleted
tests no longer run.

- [ ] **Step 5: Confirm no dangling references remain**

Run:
```bash
grep -rn "AppNameCorrector" src tests || echo "OK: no references"
```
Expected: `OK: no references`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "$(cat <<'EOF'
refactor: remove hardcoded app-name corrector from cleanup post-pass

The AppNameCorrector duplicated the user-facing corrections mechanism
(corrections.json Replacements + the Corrections page). Removed the class,
its unit tests, and the two CleanupRunner tests that asserted the built-in
correction. The deterministic post-pass now applies only the user's own
CaseAwareReplacer corrections.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Back up a corrupt settings file instead of silently wiping it

**Files:**
- Modify: `src/Winpepper.Core/Settings/SettingsStore.cs`
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify (Windows): `src/Winpepper.App/Hosting/AppShell.cs:54`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `SettingsStore(string path, Action<string>? onError = null)` — a new
    optional log-sink parameter (back-compatible; existing `new
    SettingsStore(path)` callers still compile).
  - On `JsonException`, `Load()` renames the corrupt file to
    `<path>.bad-<timestamp>` (timestamp `yyyyMMddHHmmssfff`, no `:` so it is
    Windows-safe), invokes `onError` with a message, and returns
    `new AppSettings()`.

**Why (spec 2(i)):** an MSI upgrade can leave `settings.json` torn/partial.
Silently returning defaults wipes `OnboardingCompleted` and every other
setting. Preserving the bad file lets us diagnose and never destroys evidence.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`, add this test
inside the `SettingsStoreTests` class (after `Load_BadJson_FallsBackToDefaults`,
before the class-closing `}`):

```csharp
    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(_path, "{ this is not valid json", System.Text.Encoding.UTF8);
        string? logged = null;
        var store = new SettingsStore(_path, msg => logged = msg);

        var s = store.Load();

        // Defaults returned (nothing is silently kept from the corrupt file).
        s.Schema.ShouldBe(1);
        s.OnboardingCompleted.ShouldBeFalse();

        // The corrupt file was moved aside to a .bad-* backup, not deleted.
        File.Exists(_path).ShouldBeFalse();
        var dir = Path.GetDirectoryName(_path)!;
        Directory.GetFiles(dir, $"{Path.GetFileName(_path)}.bad-*").Length.ShouldBe(1);

        // The caller was told.
        logged.ShouldNotBeNull();
    }
```

Also update `Dispose()` at the top of the file so backups are cleaned up.
Replace:

```csharp
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
```

with:

```csharp
    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        var dir = Path.GetDirectoryName(_path)!;
        foreach (var f in Directory.GetFiles(dir, $"{Path.GetFileName(_path)}.bad-*"))
            File.Delete(f);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.Settings.SettingsStoreTests.Load_CorruptFile_BacksUpAndReturnsDefaults"
```
Expected: **build FAILS** — `SettingsStore` has no 2-argument constructor
(`CS1729: 'SettingsStore' does not contain a constructor that takes 2
arguments`). That is the RED signal for this step.

- [ ] **Step 3: Implement the backup-on-corruption behavior**

Replace the entire contents of `src/Winpepper.Core/Settings/SettingsStore.cs`
with:

```csharp
using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly Action<string>? _onError;

    public SettingsStore(string path, Action<string>? onError = null)
    {
        _path = path;
        _onError = onError;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // A torn/corrupt file (e.g. after an MSI upgrade force-kill) must
            // NOT silently wipe every setting. Preserve it for diagnosis, then
            // fall back to defaults. Keep it simple: no partial salvage.
            BackupCorruptFile(ex);
            return new AppSettings();
        }
    }

    private void BackupCorruptFile(Exception ex)
    {
        try
        {
            var backup = $"{_path}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(_path, backup);
            _onError?.Invoke(
                $"settings.json was corrupt ({ex.Message}); backed up to " +
                $"{Path.GetFileName(backup)} and reset to defaults.");
        }
        catch (Exception moveEx)
        {
            _onError?.Invoke(
                $"settings.json was corrupt and could not be backed up: {moveEx.Message}");
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.Settings.SettingsStoreTests.Load_CorruptFile_BacksUpAndReturnsDefaults"
```
Expected: **PASS** — `Failed: 0, Passed: 1`.

- [ ] **Step 5: Run the whole SettingsStore test class to confirm no regressions**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.Settings.SettingsStoreTests"
```
Expected: **PASS** — `Failed: 0`. The pre-existing
`Load_BadJson_FallsBackToDefaults` still passes (it now also moves the file
aside; `Dispose` cleans up the backup).

- [ ] **Step 6: Wire the log sink in the app (Windows-only)**

In `src/Winpepper.App/Hosting/AppShell.cs`, replace line 54:

```csharp
        var store = new SettingsStore(AppPaths.SettingsJson);
```

with:

```csharp
        var store = new SettingsStore(AppPaths.SettingsJson,
            onError: msg => factory.CreateLogger("Winpepper.App.Settings").LogWarning("{SettingsWarning}", msg));
```

(This file is `#if WINDOWS`; it is not built on Linux and is verified in the
Windows smoke test.)

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Settings/SettingsStore.cs \
        tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs \
        src/Winpepper.App/Hosting/AppShell.cs
git commit -m "$(cat <<'EOF'
fix: back up corrupt settings.json instead of silently resetting

A torn/corrupt settings file (e.g. after an MSI upgrade force-kill) used to be
silently replaced by defaults, wiping OnboardingCompleted and every setting.
SettingsStore.Load now renames the bad file to settings.json.bad-<timestamp>,
logs a warning via an optional sink, and returns defaults.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: Add a `QueueAndFlushAsync` durable-write primitive

**Files:**
- Modify: `src/Winpepper.Core/Settings/ISettingsWriter.cs`
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Modify: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: existing `ISettingsWriter.Queue` and `ISettingsWriter.FlushAsync`.
- Produces (relied on by Tasks 4, 5, 8):
  - `Task ISettingsWriter.QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)`
    — a **default interface method** (`Queue(mutator); return FlushAsync();`),
    so existing fake implementers keep compiling without change.
  - A matching **public concrete** `Task DebouncedSettingsWriter.QueueAndFlushAsync(...)`
    so callers holding the concrete type (WinUI wiring) can call it too.

**Why (spec 2(ii)):** durable checkpoints need "apply this change AND get it on
disk now", without waiting out the 400 ms debounce that a force-kill can beat.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`, add
this test inside the class (after `FlushAsync_Forces_Immediate_Write`):

```csharp
    [Fact]
    public async Task QueueAndFlushAsync_Writes_Immediately_Without_Debounce()
    {
        var store = new SettingsStore(_path);
        // 30 s debounce: only an immediate flush can make this land in time.
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));

        await writer.QueueAndFlushAsync(s => s with { MicDeviceId = "flushed-now" });

        new SettingsStore(_path).Load().MicDeviceId.ShouldBe("flushed-now");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.QueueAndFlushAsync_Writes_Immediately_Without_Debounce"
```
Expected: **build FAILS** — `DebouncedSettingsWriter`/`ISettingsWriter` has no
`QueueAndFlushAsync` (`CS1061`). That is the RED signal.

- [ ] **Step 3: Add the default interface method**

Replace the entire contents of `src/Winpepper.Core/Settings/ISettingsWriter.cs`
with:

```csharp
namespace Winpepper.Core.Settings;

public interface ISettingsWriter
{
    void Queue(Func<AppSettings, AppSettings> mutator);
    Task FlushAsync();

    /// <summary>
    /// Applies <paramref name="mutator"/> and flushes it to disk immediately,
    /// bypassing the debounce window. Use at durable checkpoints (onboarding
    /// step advance, a settings toggle/hotkey commit) so a subsequent
    /// force-kill (e.g. an MSI upgrade) cannot lose the change.
    /// </summary>
    Task QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        Queue(mutator);
        return FlushAsync();
    }
}
```

- [ ] **Step 4: Add the concrete override on the debounced writer**

In `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`, add this method
immediately after the existing `FlushAsync` method (after its closing `}`,
before `private void Flush()`):

```csharp
    public async Task QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        Queue(mutator);
        await FlushAsync();
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests"
```
Expected: **PASS** — `Failed: 0` (the new test plus the three existing ones).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings/ISettingsWriter.cs \
        src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
git commit -m "$(cat <<'EOF'
feat: add QueueAndFlushAsync durable-write to the settings writer

Adds an immediate apply-and-flush that bypasses the 400ms debounce, for use at
durable checkpoints where a later force-kill (MSI upgrade) could otherwise lose
a pending write. Default interface method keeps existing writers/fakes working;
DebouncedSettingsWriter provides a concrete override.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: Onboarding writes durably at each step; Skip completes setup

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs:79-108`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs:88`

**Interfaces:**
- Consumes: `ISettingsWriter.QueueAndFlushAsync` (Task 3).
- Produces (relied on by Task 6, which extends the same VM):
  - `AdvanceAsync` flushes at every checkpoint (PickMic, PickHotkeys,
    TestDictation).
  - `Skip()` is replaced by `Task SkipAsync()`, which sets
    `OnboardingCompleted = true`, flushes, then advances to `TestDictation`.

**Why (spec 2(ii) onboarding + 2(iii)):** debounced writes pending at a
force-kill are lost, so each onboarding advance must flush. And a user who
Skips must not be re-onboarded forever — Skip means "setup done".

- [ ] **Step 1: Update the existing tests to the new async Skip + flush contract**

In `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`:

(a) Replace the `FakeWriter` nested class (lines 11-16) so it records flushes:

```csharp
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current = new();
        public int Flushes;
        public void Queue(Func<AppSettings, AppSettings> m) => Current = m(Current);
        public Task FlushAsync() { Flushes++; return Task.CompletedTask; }
    }
```

(b) Replace `Skip_From_DownloadModels_Advances_Without_Running_Stub` in full:

```csharp
    [Fact]
    public async Task Skip_From_DownloadModels_Advances_Without_Running_Stub()
    {
        var downloaded = false;
        var vm = new OnboardingViewModel(new FakeWriter(),
            () => { downloaded = true; return Task.CompletedTask; },
            new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync();
        await vm.SkipAsync();
        downloaded.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }
```

(c) Replace `Finish_Sets_OnboardingCompleted` in full:

```csharp
    [Fact]
    public async Task Finish_Sets_OnboardingCompleted()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync(); await vm.SkipAsync();
        vm.TestDictationDone = true;
        await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.Done);
        w.Current.OnboardingCompleted.ShouldBeTrue();
    }
```

- [ ] **Step 2: Add the two new failing tests**

Append these two tests inside the class (before the class-closing `}`):

```csharp
    [Fact]
    public async Task SkipAsync_Sets_OnboardingCompleted_And_Flushes()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();  // -> PickHotkeys
        await vm.AdvanceAsync();                                 // -> DownloadModels
        var flushesBefore = w.Flushes;

        await vm.SkipAsync();

        w.Current.OnboardingCompleted.ShouldBeTrue();
        w.Flushes.ShouldBeGreaterThan(flushesBefore);           // Skip flushed
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Advance_From_PickMic_Flushes_The_Checkpoint()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "{mic-1}";

        await vm.AdvanceAsync();

        w.Current.MicDeviceId.ShouldBe("{mic-1}");
        w.Flushes.ShouldBeGreaterThan(0);                       // checkpoint flushed
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.ViewModels.OnboardingViewModelTests"
```
Expected: **build FAILS** — `OnboardingViewModel` has no `SkipAsync`
(`CS1061` on `vm.SkipAsync()`). That is the RED signal.

- [ ] **Step 4: Flush at each advance and rewrite Skip as SkipAsync**

In `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`, replace the
`AdvanceAsync` method and the `Skip` method (lines 79-108) with:

```csharp
    public async Task AdvanceAsync()
    {
        if (!CanAdvance) return;
        switch (_step)
        {
            case OnboardingStep.PickMic:
                await _writer.QueueAndFlushAsync(s => s with { MicDeviceId = _micId });
                Step = OnboardingStep.PickHotkeys;
                break;
            case OnboardingStep.PickHotkeys:
                await _writer.QueueAndFlushAsync(s => s with { HoldHotkey = _holdHotkey, ToggleHotkey = _toggleHotkey });
                Step = OnboardingStep.DownloadModels;
                break;
            case OnboardingStep.DownloadModels:
                await _runDownloader();
                Step = OnboardingStep.TestDictation;
                break;
            case OnboardingStep.TestDictation:
                await _writer.QueueAndFlushAsync(s => s with { OnboardingCompleted = true });
                Step = OnboardingStep.Done;
                break;
        }
    }

    /// <summary>
    /// Skipping the (optional) model download still completes setup: the user
    /// chose to skip, so persist OnboardingCompleted durably and move on to the
    /// test-dictation step. This prevents onboarding from reappearing forever
    /// (spec 2(iii)).
    /// </summary>
    public async Task SkipAsync()
    {
        if (!CanSkip) return;
        await _writer.QueueAndFlushAsync(s => s with { OnboardingCompleted = true });
        Step = OnboardingStep.TestDictation;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.ViewModels.OnboardingViewModelTests"
```
Expected: **PASS** — `Failed: 0` (all onboarding VM tests, including the two
new ones and the three updated ones).

- [ ] **Step 6: Update the WinUI Skip handler (Windows-only)**

In `src/Winpepper.App/Views/OnboardingPage.xaml.cs`, replace line 88:

```csharp
    private void OnSkip(object sender, RoutedEventArgs e) { _vm?.Skip(); }
```

with:

```csharp
    private async void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) await _vm.SkipAsync();
    }
```

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/ViewModels/OnboardingViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs \
        src/Winpepper.App/Views/OnboardingPage.xaml.cs
git commit -m "$(cat <<'EOF'
fix: flush onboarding progress durably and complete setup on Skip

Each onboarding advance now flushes via QueueAndFlushAsync so a force-kill
(MSI upgrade) cannot lose progress. Skip() becomes SkipAsync(): it sets
OnboardingCompleted=true and flushes, so a user who skips the model download is
never re-onboarded.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: Settings-page edits flush durably on commit

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs:44-106`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/RecordingPage.xaml.cs:82`

**Interfaces:**
- Consumes: `ISettingsWriter.QueueAndFlushAsync` (Task 3).
- Produces: every `RecordingSettingsViewModel` setter that mutates settings now
  fires an immediate durable flush (fire-and-forget, per spec 2(ii): "a
  FlushAsync call after Queue ... is acceptable"). The one-write-per-change
  contract is preserved (`Queue` still runs exactly once per real change).

**Why (spec 2(ii) settings pages):** a toggle/hotkey change followed quickly by
an upgrade force-kill must survive. Flushing on commit closes the lost-write
window for settings-page edits, mirroring the onboarding checkpoints.

- [ ] **Step 1: Add the failing test**

In `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`,
extend the nested `FakeWriter` (lines 11-17) to count flushes:

```csharp
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current { get; private set; } = new();
        public int WriteCount { get; private set; }
        public int FlushCount { get; private set; }
        public void Queue(Func<AppSettings, AppSettings> m) { Current = m(Current); WriteCount++; }
        public Task FlushAsync() { FlushCount++; return Task.CompletedTask; }
    }
```

Then add this test inside the class (before the class-closing `}`):

```csharp
    [Fact]
    public void Setting_SpeakerFilter_Queues_And_Flushes_Durably()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);

        vm.SpeakerFilterEnabled = true;

        w.Current.SpeakerFilterEnabled.ShouldBeTrue();
        w.WriteCount.ShouldBe(1);   // exactly one write per real change
        w.FlushCount.ShouldBe(1);   // and it was flushed durably
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.ViewModels.RecordingSettingsViewModelTests.Setting_SpeakerFilter_Queues_And_Flushes_Durably"
```
Expected: **FAIL** — `w.FlushCount.ShouldBe(1)` but was `0` (the setter today
calls only `Queue`, never flushes).

- [ ] **Step 3: Flush durably in the settings setters**

In `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`, add this
private helper immediately after the constructor's `NullHotkeyValidator` nested
class (after its closing `}`, before the `HoldHotkey` property):

```csharp
    // Commit a settings change durably: apply it and flush past the debounce so
    // a subsequent force-kill (MSI upgrade) can't lose it. Fire-and-forget is
    // acceptable here (spec 2(ii)); the writer swallows write errors.
    private void CommitDurable(Func<AppSettings, AppSettings> mutator)
        => _ = _writer.QueueAndFlushAsync(mutator);
```

Then replace each `_writer.Queue(...)` call in the setters with
`CommitDurable(...)`. Specifically:

- In `HoldHotkey` (line 51): `_writer.Queue(s => s with { HoldHotkey = value });`
  → `CommitDurable(s => s with { HoldHotkey = value });`
- In `ToggleHotkey` (line 65): `_writer.Queue(s => s with { ToggleHotkey = value });`
  → `CommitDurable(s => s with { ToggleHotkey = value });`
- In `MicDeviceId` (line 79): `_writer.Queue(s => s with { MicDeviceId = value });`
  → `CommitDurable(s => s with { MicDeviceId = value });`
- In `PlaySounds` (line 91): `_writer.Queue(s => s with { PlaySounds = value });`
  → `CommitDurable(s => s with { PlaySounds = value });`
- In `SpeakerFilterEnabled` (line 103): `_writer.Queue(s => s with { SpeakerFilterEnabled = value });`
  → `CommitDurable(s => s with { SpeakerFilterEnabled = value });`

- [ ] **Step 4: Run the test to verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.ViewModels.RecordingSettingsViewModelTests"
```
Expected: **PASS** — `Failed: 0`. The pre-existing `Setting_HoldHotkey_Queues_Write`
(asserts `WriteCount == 1`) and `Setting_HoldHotkey_To_Same_Value_Is_NoOp`
(asserts `WriteCount == 0`) still pass because `Queue` still runs exactly once
per real change.

- [ ] **Step 5: Make the Autostart toggle write durably too (Windows-only)**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, replace line 82:

```csharp
            _shell.SettingsWriter.Queue(s => s with { AutostartEnabled = AutostartToggle.IsOn });
```

with:

```csharp
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = AutostartToggle.IsOn });
```

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs \
        src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "$(cat <<'EOF'
fix: flush settings-page edits durably on commit

Recording settings setters (hotkeys, mic, sounds, speaker filter) and the
Autostart toggle now apply-and-flush via QueueAndFlushAsync so a force-kill
(MSI upgrade) can't lose a just-made change. One write per real change is
preserved.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 6: Onboarding hydrates from settings and starts at the first unresolved step

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs:19-26`

**Interfaces:**
- Consumes: `AppSettings` (MicDeviceId, HoldHotkey, ToggleHotkey);
  `IHotkeyValidator` (already injected) for hotkey resolution.
- Produces (relied on by Task 7, which passes `modelsResolved` in):
  - `void OnboardingViewModel.InitializeFrom(AppSettings settings, bool persistedMicPresent, bool modelsResolved)`
    — prefills mic + hotkeys from settings and sets `Step` to the first
    unresolved step among PickMic → PickHotkeys → DownloadModels, else
    TestDictation.
  - Resolution rules: PickMic resolved when `MicDeviceId` is non-empty AND
    `persistedMicPresent`; PickHotkeys resolved when both hotkey errors are
    null; DownloadModels resolved when `modelsResolved`.

**Why (spec 3(i),(ii)):** returning users shouldn't redo already-resolved
setup. All steps stay visible (the user can navigate Back); only the *starting*
step changes.

- [ ] **Step 1: Write the failing tests**

In `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`, add
these tests inside the class (before the class-closing `}`):

```csharp
    [Fact]
    public void InitializeFrom_NoMic_StartsAtPickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(new AppSettings { MicDeviceId = "" },
            persistedMicPresent: false, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicSetButMissingDevice_StartsAtPickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(new AppSettings { MicDeviceId = "{gone}" },
            persistedMicPresent: false, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void InitializeFrom_MicPresent_HotkeysValid_ModelsMissing_StartsAtDownloadModels()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: false);
        vm.Step.ShouldBe(OnboardingStep.DownloadModels);
    }

    [Fact]
    public void InitializeFrom_AllResolved_StartsAtTestDictation()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public void InitializeFrom_InvalidHotkey_StartsAtPickHotkeys()
    {
        // Validator flags the persisted toggle chord as conflicting.
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask,
            new FakeValidator("Ctrl+Shift+Space"));
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "RightCtrl+RightShift", ToggleHotkey = "Ctrl+Shift+Space" },
            persistedMicPresent: true, modelsResolved: true);
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public void InitializeFrom_Prefills_Mic_And_Hotkeys()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.InitializeFrom(
            new AppSettings { MicDeviceId = "{mic}", HoldHotkey = "LeftAlt+F9", ToggleHotkey = "LeftCtrl+LeftShift" },
            persistedMicPresent: true, modelsResolved: true);
        vm.SelectedMicDeviceId.ShouldBe("{mic}");
        vm.HoldHotkey.ShouldBe("LeftAlt+F9");
        vm.ToggleHotkey.ShouldBe("LeftCtrl+LeftShift");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.ViewModels.OnboardingViewModelTests"
```
Expected: **build FAILS** — `OnboardingViewModel` has no `InitializeFrom`
(`CS1061`). That is the RED signal.

- [ ] **Step 3: Add hydration + first-unresolved-step logic**

In `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`, add these two
methods immediately after the constructor (after its closing `}`, before the
`Step` property):

```csharp
    /// <summary>
    /// Prefill the VM from persisted settings and start at the first unresolved
    /// step. All steps remain reachable via Back; this only moves the starting
    /// position (spec 3(i),(ii)). <paramref name="persistedMicPresent"/> is
    /// supplied by the page (device enumeration lives there);
    /// <paramref name="modelsResolved"/> is true when no selected model is
    /// missing (computed via MissingModelsResolver).
    /// </summary>
    public void InitializeFrom(AppSettings settings, bool persistedMicPresent, bool modelsResolved)
    {
        _micId = settings.MicDeviceId;
        _holdHotkey = settings.HoldHotkey;
        _toggleHotkey = settings.ToggleHotkey;

        Raise(nameof(SelectedMicDeviceId));
        Raise(nameof(HoldHotkey));
        Raise(nameof(ToggleHotkey));
        Raise(nameof(HoldHotkeyError));
        Raise(nameof(ToggleHotkeyError));

        Step = FirstUnresolvedStep(persistedMicPresent, modelsResolved);
    }

    private OnboardingStep FirstUnresolvedStep(bool persistedMicPresent, bool modelsResolved)
    {
        var micResolved = !string.IsNullOrEmpty(_micId) && persistedMicPresent;
        if (!micResolved) return OnboardingStep.PickMic;

        var hotkeysResolved = HoldHotkeyError is null && ToggleHotkeyError is null;
        if (!hotkeysResolved) return OnboardingStep.PickHotkeys;

        if (!modelsResolved) return OnboardingStep.DownloadModels;

        return OnboardingStep.TestDictation;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.ViewModels.OnboardingViewModelTests"
```
Expected: **PASS** — `Failed: 0` (the six new tests plus all earlier ones,
including `Initial_Step_Is_PickMic` which still holds when `InitializeFrom` is
not called).

- [ ] **Step 5: Hydrate the VM from the page (Windows-only)**

In `src/Winpepper.App/Views/OnboardingPage.xaml.cs`, in `OnNavigatedTo`,
replace lines 27-29:

```csharp
        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);
```

with:

```csharp
        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);

        // Hydrate from persisted settings and start at the first unresolved
        // step (spec 3). persistedMicPresent: the saved device still exists in
        // the current enumeration. modelsResolved: no selected model missing.
        var settings = shell.Settings;
        var persistedMicPresent = !string.IsNullOrEmpty(settings.MicDeviceId)
                                   && devices.Any(d => d.Id == settings.MicDeviceId);
        var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
            shell.ModelsServices.Registry.All,
            shell.ModelsServices.ModelsRoot,
            new[] { settings.AsrModelName, settings.CleanupModelName });
        var modelsResolved = missing.Count == 0;
        _vm.InitializeFrom(settings, persistedMicPresent, modelsResolved);

        // Reflect the hydrated device selection in the combo.
        MicCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == settings.MicDeviceId);
```

(The `_vm` is constructed just above at line 24-25; `InitializeFrom` runs after
the device list is known.)

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels/OnboardingViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs \
        src/Winpepper.App/Views/OnboardingPage.xaml.cs
git commit -m "$(cat <<'EOF'
feat: onboarding hydrates from settings and skips resolved steps

Adds OnboardingViewModel.InitializeFrom(settings, persistedMicPresent,
modelsResolved): prefills mic + hotkeys and starts at the first unresolved step
(PickMic -> PickHotkeys -> DownloadModels -> TestDictation). All steps remain
navigable; only the starting position changes. The page supplies device
presence and missing-models resolution.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 7: Onboarding downloads only missing models (real downloader)

**Files:**
- Modify: `tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/OnboardingPage.xaml.cs:24`

**Interfaces:**
- Consumes: `MissingModelsResolver.FindMissing(registry, installRoot, selectedNames)`
  and `ModelDescriptor.IsFullyInstalled(installRoot)` (a method taking the install
  root, not a property; both existing, spec 3(iii));
  `ModelsServices.DownloadAsync(descriptor, installRoot, progress, ct)`,
  `ModelsServices.Registry.All`, `ModelsServices.ModelsRoot` (existing);
  `ModelRegistry.DefaultAsrName`, `ModelRegistry.DefaultCleanupName`.
- Produces: the onboarding DownloadModels step invokes the **real** downloader
  used by the Models tab, fetching ONLY missing models and resolving
  immediately when nothing is missing (replacing the `() => Task.CompletedTask`
  stub).

**Why (spec 3(iii),(iv)):** the onboarding downloader was a stub. It must use
the same resolver + downloader as the Models tab so existing files are never
re-downloaded.

**Verification note (spec 1b):** the actual network download is Windows- and
network-bound (`ModelsServices` is `#if WINDOWS`, wraps `HttpClientRangeClient`
+ `ModelDownloader`), so its end-to-end outcome ("only missing models are
fetched; nothing re-downloads") is proven in the **Windows Smoke Test
Checklist**, NOT deferred. The Linux-testable guarantee this task adds is that
the *selection* logic the downloader depends on — `FindMissing` over the
onboarding scope (the ASR + cleanup names) — returns exactly the missing
descriptors and empty when both are installed.

- [ ] **Step 1: Write the failing test (onboarding-scope selection)**

In `tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs`, add this test
inside the class (before the class-closing `}`):

```csharp
    [Fact]
    public void FindMissing_OnboardingScope_ReturnsOnlyUninstalled_Then_Empty()
    {
        var registry = new ModelRegistry();
        var names = new[] { ModelRegistry.DefaultAsrName, ModelRegistry.DefaultCleanupName };
        var resolver = new MissingModelsResolver();

        // Nothing installed yet: both selected models are missing.
        var before = resolver.FindMissing(registry.All, _root, names);
        before.Select(d => d.Name).OrderBy(n => n)
              .ShouldBe(new[] { ModelRegistry.DefaultAsrName, ModelRegistry.DefaultCleanupName }.OrderBy(n => n));

        // Install every file of both descriptors (non-empty content).
        foreach (var d in registry.All.Where(d => names.Contains(d.Name)))
        {
            foreach (var f in d.Files)
            {
                var p = Path.Combine(_root, d.InstallDirRelative, f.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
        }

        // Now nothing is missing -> the onboarding step auto-resolves.
        resolver.FindMissing(registry.All, _root, names).ShouldBeEmpty();
    }
```

- [ ] **Step 2: Run the test to verify it passes (logic already exists)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Models.Tests/bin/Debug/net9.0/Winpepper.Models.Tests.dll \
    -method "Winpepper.Models.Tests.MissingModelsResolverTests.FindMissing_OnboardingScope_ReturnsOnlyUninstalled_Then_Empty"
```
Expected: **PASS** — `Failed: 0, Passed: 1`. This test pins the selection
contract the real downloader (Step 3) relies on. (It passes immediately because
`MissingModelsResolver`/`ModelDescriptor.IsFullyInstalled` already implement the
logic; the test guards it against regression as the downloader is wired in.)

- [ ] **Step 3: Wire the real downloader into onboarding (Windows-only)**

In `src/Winpepper.App/Views/OnboardingPage.xaml.cs`, replace lines 23-25:

```csharp
        // The stub returns immediately for Plan 3. Plan 4 swaps in the real downloader.
        _vm = new OnboardingViewModel(shell.SettingsWriter, () => Task.CompletedTask,
                                       new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator());
```

with:

```csharp
        // Real downloader: fetch ONLY the selected models that are missing,
        // using the same resolver + downloader as the Models tab (spec 3(iv)).
        // If nothing is missing this returns immediately and the step
        // auto-resolves.
        async Task RunOnboardingDownloadAsync()
        {
            var s = shell.Settings;
            var names = new[] { s.AsrModelName, s.CleanupModelName };
            var missing = new Winpepper.Models.MissingModelsResolver().FindMissing(
                shell.ModelsServices.Registry.All, shell.ModelsServices.ModelsRoot, names);
            foreach (var descriptor in missing)
            {
                var progress = new Progress<Winpepper.Models.DownloadProgress>();
                await shell.ModelsServices.DownloadAsync(
                    descriptor, shell.ModelsServices.ModelsRoot, progress, CancellationToken.None);
            }
        }

        _vm = new OnboardingViewModel(shell.SettingsWriter, RunOnboardingDownloadAsync,
                                       new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator());
```

- [ ] **Step 4: Run the full Models test suite to confirm no regressions**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Models.Tests/bin/Debug/net9.0/Winpepper.Models.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: **PASS** — `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs \
        src/Winpepper.App/Views/OnboardingPage.xaml.cs
git commit -m "$(cat <<'EOF'
feat: onboarding downloads only missing models via the real downloader

Replaces the onboarding DownloadModels stub with the same resolver + downloader
the Models tab uses: FindMissing over the selected ASR + cleanup models, then
ModelsServices.DownloadAsync for each missing one. Existing files are never
re-downloaded; nothing missing auto-resolves the step. Adds a Models.Tests
guard pinning the onboarding-scope selection contract.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 8: Main window default size + persistence

**Files:**
- Create: `src/Winpepper.Core/WindowSizePolicy.cs`
- Create: `tests/Winpepper.Core.Tests/WindowSizePolicyTests.cs`
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs:32`
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify (Windows): `src/Winpepper.App/Views/MainWindow.xaml.cs:11-26`

**Interfaces:**
- Consumes: `ISettingsWriter.QueueAndFlushAsync` (Task 3).
- Produces:
  - `static (int Width, int Height) WindowSizePolicy.ComputeDefault(int platformWidth, int platformHeight, int minWidth = 480, int minHeight = 400)`
    — returns `(max(platformWidth/3, minWidth), max(platformHeight/2, minHeight))`.
  - `AppSettings.WindowWidth` and `AppSettings.WindowHeight` — new
    `int?` fields (default `null` = no persisted size).

**Why (spec Task 4):** default the window to ~1/3 width × 1/2 height of the
platform default, clamped to a usable minimum, and remember a user resize.

- [ ] **Step 1: Write the failing policy tests**

Create `tests/Winpepper.Core.Tests/WindowSizePolicyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class WindowSizePolicyTests
{
    [Fact]
    public void ComputeDefault_Halves_Height_And_Thirds_Width()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(platformWidth: 1500, platformHeight: 1000);
        w.ShouldBe(500);   // 1500 / 3
        h.ShouldBe(500);   // 1000 / 2
    }

    [Fact]
    public void ComputeDefault_Clamps_To_Minimum_On_Small_Screens()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(platformWidth: 900, platformHeight: 600);
        w.ShouldBe(480);   // 900 / 3 = 300 -> clamped up to 480
        h.ShouldBe(400);   // 600 / 2 = 300 -> clamped up to 400
    }

    [Fact]
    public void ComputeDefault_Respects_Custom_Minimums()
    {
        var (w, h) = WindowSizePolicy.ComputeDefault(1200, 800, minWidth: 700, minHeight: 700);
        w.ShouldBe(700);   // 1200 / 3 = 400 -> clamped up to 700
        h.ShouldBe(700);   // 800 / 2 = 400 -> clamped up to 700
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.WindowSizePolicyTests"
```
Expected: **build FAILS** — `WindowSizePolicy` does not exist
(`CS0246: The type or namespace name 'WindowSizePolicy' could not be found`).
That is the RED signal.

- [ ] **Step 3: Implement the policy**

Create `src/Winpepper.Core/WindowSizePolicy.cs`:

```csharp
namespace Winpepper.Core;

/// <summary>
/// Pure sizing policy for the main window (spec Task 4): default to about a
/// third of the platform default width and half its height, clamped to a
/// usable minimum so the nav UI stays usable on small screens.
/// </summary>
public static class WindowSizePolicy
{
    public static (int Width, int Height) ComputeDefault(
        int platformWidth, int platformHeight, int minWidth = 480, int minHeight = 400)
    {
        var w = Math.Max(platformWidth / 3, minWidth);
        var h = Math.Max(platformHeight / 2, minHeight);
        return (w, h);
    }
}
```

- [ ] **Step 4: Run the policy tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.WindowSizePolicyTests"
```
Expected: **PASS** — `Failed: 0, Passed: 3`.

- [ ] **Step 5: Write the failing settings round-trip test**

In `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`, add this test
inside the class (before the class-closing `}`, before the
`PipeExtensions` helper):

```csharp
    [Fact]
    public void Save_RoundTrips_WindowSize()
    {
        var store = new SettingsStore(_path);
        var s = store.Load() with { WindowWidth = 640, WindowHeight = 520 };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.WindowWidth.ShouldBe(640);
        loaded.WindowHeight.ShouldBe(520);
    }

    [Fact]
    public void Defaults_Have_No_Persisted_WindowSize()
    {
        var s = new SettingsStore(_path).Load();
        s.WindowWidth.ShouldBeNull();
        s.WindowHeight.ShouldBeNull();
    }
```

- [ ] **Step 6: Run the round-trip test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.Settings.SettingsStoreTests.Save_RoundTrips_WindowSize"
```
Expected: **build FAILS** — `AppSettings` has no `WindowWidth`/`WindowHeight`
(`CS0117`). That is the RED signal.

- [ ] **Step 7: Add the nullable size fields**

In `src/Winpepper.Core/Settings/AppSettings.cs`, add these two fields
immediately after line 32 (`public string LastVersionSeen { get; init; } = "";`),
before the closing `}` of the record:

```csharp

    // Main-window size in physical pixels; null until the user resizes or the
    // first-run default is applied (spec Task 4). No position persistence.
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
```

- [ ] **Step 8: Run the settings tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.Settings.SettingsStoreTests"
```
Expected: **PASS** — `Failed: 0` (the two new tests plus all existing ones).

- [ ] **Step 9: Apply/restore/persist the window size (Windows-only)**

In `src/Winpepper.App/Views/MainWindow.xaml.cs`, add `using Windows.Graphics;`
to the using block (after line 4, `using Winpepper.App.Hosting;`), then replace
the constructor body region — lines 23-25:

```csharp
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
        AppWindow.Closing += OnAppWindowClosing;
```

with:

```csharp
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];

        // Size (spec Task 4): restore a remembered size, else default to a
        // third of the platform width and half its height (clamped). Only
        // applied when no persisted size exists.
        var appWindow = AppWindow;
        var persisted = _shell.Settings;
        if (persisted.WindowWidth is int savedW && persisted.WindowHeight is int savedH)
        {
            appWindow.Resize(new SizeInt32(savedW, savedH));
        }
        else
        {
            var (w, h) = Winpepper.Core.WindowSizePolicy.ComputeDefault(
                appWindow.Size.Width, appWindow.Size.Height);
            appWindow.Resize(new SizeInt32(w, h));
        }

        // Remember user resizes durably (physical px). No position persistence.
        appWindow.Changed += (sender, args) =>
        {
            if (args.DidSizeChange)
            {
                var size = sender.Size;
                _ = _shell.SettingsWriter.QueueAndFlushAsync(
                    st => st with { WindowWidth = size.Width, WindowHeight = size.Height });
            }
        };

        AppWindow.Closing += OnAppWindowClosing;
```

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Core/WindowSizePolicy.cs \
        tests/Winpepper.Core.Tests/WindowSizePolicyTests.cs \
        src/Winpepper.Core/Settings/AppSettings.cs \
        tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs \
        src/Winpepper.App/Views/MainWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat: default main window size to 1/3 width x 1/2 height, and persist resizes

Adds WindowSizePolicy.ComputeDefault (clamped to 480x400 min) and nullable
WindowWidth/WindowHeight in AppSettings. MainWindow restores a remembered size
or applies the clamped default from the platform default, and persists user
resizes durably via QueueAndFlushAsync. No position or multi-monitor logic.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 9: "Offer to learn corrections" toggle, default OFF

**Files:**
- Create: `src/Winpepper.Core/Learning/PostPasteGate.cs`
- Create: `tests/Winpepper.Core.Tests/Learning/PostPasteGateTests.cs`
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs`
- Modify: `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`
- Modify: `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`
- Modify: `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`
- Modify (Windows): `src/Winpepper.App/Hosting/PipelineHost.cs:269,425` + ctor
- Modify (Windows): `src/Winpepper.App/Hosting/AppShell.cs:229-234`
- Modify (Windows): `src/Winpepper.App/Views/RecordingPage.xaml` +
  `RecordingPage.xaml.cs`

**Interfaces:**
- Consumes: `ISettingsWriter.QueueAndFlushAsync` (Task 3).
- Produces:
  - `AppSettings.PostPasteLearningEnabled` — new `bool` field, default `false`.
  - `static bool PostPasteGate.ShouldWatch(bool learningEnabled, bool injected, bool hasWatcher, bool hasCapturer, bool hasText)`
    — the single gate predicate both PipelineHost trigger sites use
    (de-dupes the two identical conditions and is Linux-testable).
  - `RecordingSettingsViewModel.PostPasteLearningEnabled` — new bool property
    that commits durably.

**Why (spec Task 5):** the post-paste learning prompt currently runs
unconditionally. Make it opt-in, default off, controlled by a Recording-page
toggle. Gating BOTH PipelineHost trigger sites through one pure predicate keeps
the change testable on Linux even though PipelineHost itself is `#if WINDOWS`.

- [ ] **Step 1: Write the failing gate tests**

Create `tests/Winpepper.Core.Tests/Learning/PostPasteGateTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class PostPasteGateTests
{
    [Fact]
    public void ShouldWatch_False_When_Learning_Disabled()
    {
        // Everything else is ready, but the user opted out: never watch.
        PostPasteGate.ShouldWatch(learningEnabled: false, injected: true,
            hasWatcher: true, hasCapturer: true, hasText: true).ShouldBeFalse();
    }

    [Fact]
    public void ShouldWatch_True_When_Enabled_And_All_Preconditions_Met()
    {
        PostPasteGate.ShouldWatch(learningEnabled: true, injected: true,
            hasWatcher: true, hasCapturer: true, hasText: true).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, true, true, true)]   // not injected
    [InlineData(true, false, true, true)]   // no watcher
    [InlineData(true, true, false, true)]   // no capturer
    [InlineData(true, true, true, false)]   // no text
    public void ShouldWatch_False_When_Any_Precondition_Missing(
        bool injected, bool hasWatcher, bool hasCapturer, bool hasText)
    {
        PostPasteGate.ShouldWatch(learningEnabled: true, injected: injected,
            hasWatcher: hasWatcher, hasCapturer: hasCapturer, hasText: hasText).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the gate tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.Learning.PostPasteGateTests"
```
Expected: **build FAILS** — `PostPasteGate` does not exist (`CS0246`). RED.

- [ ] **Step 3: Implement the gate**

Create `src/Winpepper.Core/Learning/PostPasteGate.cs`:

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Single decision point for whether to start the post-paste learning watcher
/// after an injection (spec Task 5). Gates the (default-off) user setting
/// together with the pre-existing preconditions, so both PipelineHost trigger
/// sites share one tested predicate.
/// </summary>
public static class PostPasteGate
{
    public static bool ShouldWatch(
        bool learningEnabled, bool injected, bool hasWatcher, bool hasCapturer, bool hasText)
        => learningEnabled && injected && hasWatcher && hasCapturer && hasText;
}
```

- [ ] **Step 4: Run the gate tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -class "Winpepper.Core.Tests.Learning.PostPasteGateTests"
```
Expected: **PASS** — `Failed: 0, Passed: 6`.

- [ ] **Step 5: Write failing tests for the setting + VM property**

In `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`, add inside the
class (before the class-closing `}`):

```csharp
    [Fact]
    public void PostPasteLearning_Defaults_Off_And_RoundTrips()
    {
        new SettingsStore(_path).Load().PostPasteLearningEnabled.ShouldBeFalse();

        var store = new SettingsStore(_path);
        store.Save(store.Load() with { PostPasteLearningEnabled = true });
        new SettingsStore(_path).Load().PostPasteLearningEnabled.ShouldBeTrue();
    }
```

In `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`,
add inside the class (before the class-closing `}`):

```csharp
    [Fact]
    public void PostPasteLearning_Defaults_Off_And_Commits_Durably()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);
        vm.PostPasteLearningEnabled.ShouldBeFalse();

        vm.PostPasteLearningEnabled = true;

        w.Current.PostPasteLearningEnabled.ShouldBeTrue();
        w.WriteCount.ShouldBe(1);
        w.FlushCount.ShouldBe(1);
    }
```

(This relies on the `FlushCount`-aware `FakeWriter` introduced in Task 5.)

- [ ] **Step 6: Run these tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: **build FAILS** — `AppSettings.PostPasteLearningEnabled` and
`RecordingSettingsViewModel.PostPasteLearningEnabled` do not exist (`CS0117`,
`CS1061`). RED.

- [ ] **Step 7: Add the setting field**

In `src/Winpepper.Core/Settings/AppSettings.cs`, add immediately after
`public bool SpeakerFilterEnabled { get; init; } = false;` (line 31):

```csharp

    // Post-paste "offer to learn corrections" prompt. Off by default: this is
    // opt-in behavior (spec Task 5).
    public bool PostPasteLearningEnabled { get; init; } = false;
```

- [ ] **Step 8: Add the VM property**

In `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`:

(a) add a backing field after line 23 (`private bool _speakerFilterEnabled;`):

```csharp
    private bool _postPasteLearningEnabled;
```

(b) initialize it in the constructor after line 35
(`_speakerFilterEnabled = initial.SpeakerFilterEnabled;`):

```csharp
        _postPasteLearningEnabled = initial.PostPasteLearningEnabled;
```

(c) add the property immediately after the `SpeakerFilterEnabled` property
(after its closing `}` at line 106):

```csharp

    public bool PostPasteLearningEnabled
    {
        get => _postPasteLearningEnabled;
        set
        {
            if (_postPasteLearningEnabled == value) return;
            _postPasteLearningEnabled = value;
            CommitDurable(s => s with { PostPasteLearningEnabled = value });
            Raise(nameof(PostPasteLearningEnabled));
        }
    }
```

(This uses the `CommitDurable` helper added in Task 5.)

- [ ] **Step 9: Run the Core tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: **PASS** — `Failed: 0` (full Core.Tests suite: gate, settings,
recording VM, onboarding VM, window policy, all green).

- [ ] **Step 10: Gate both PipelineHost trigger sites (Windows-only)**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

(a) add a field after line 49
(`private readonly Winpepper.Platform.Learning.FocusedElementCapturer? _focusedCapturer;`):

```csharp
    private readonly bool _postPasteLearningEnabled;
```

(b) add a constructor parameter — change the last two parameters (lines 68-69)
from:

```csharp
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null)
```

to:

```csharp
        Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
        Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null,
        bool postPasteLearningEnabled = false)
```

(c) assign it in the constructor body after line 94 (`_focusedCapturer = focusedCapturer;`):

```csharp
        _postPasteLearningEnabled = postPasteLearningEnabled;
```

(d) replace the FIRST trigger condition at line 269:

```csharp
                if (injected && _postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final))
```

with:

```csharp
                if (Winpepper.Core.Learning.PostPasteGate.ShouldWatch(
                        _postPasteLearningEnabled, injected,
                        _postPaste is not null, _focusedCapturer is not null,
                        !string.IsNullOrWhiteSpace(final)))
```

(e) replace the SECOND trigger condition at line 425:

```csharp
                    if (injected2 && _postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final2))
```

with:

```csharp
                    if (Winpepper.Core.Learning.PostPasteGate.ShouldWatch(
                            _postPasteLearningEnabled, injected2,
                            _postPaste is not null, _focusedCapturer is not null,
                            !string.IsNullOrWhiteSpace(final2)))
```

(The bodies still dereference `_postPaste` and `_focusedCapturer`; because the
gate guarantees both are non-null when true, use `_postPaste!` / `_focusedCapturer!`
if the compiler warns — the existing bodies already index them directly.)

- [ ] **Step 11: Pass the setting from AppShell (Windows-only)**

In `src/Winpepper.App/Hosting/AppShell.cs`, change the `PipelineHost`
construction (lines 229-234) from:

```csharp
        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         historyServices.Archiver, settings.AsrModelName, cleanupModelName,
                                         clipboardFallback, toasts,
                                         cleanup, correctionStore, windowContext, cleanupOptions,
                                         postPaste: postPaste, focusedCapturer: focusedCapturer);
```

to:

```csharp
        var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         historyServices.Archiver, settings.AsrModelName, cleanupModelName,
                                         clipboardFallback, toasts,
                                         cleanup, correctionStore, windowContext, cleanupOptions,
                                         postPaste: postPaste, focusedCapturer: focusedCapturer,
                                         postPasteLearningEnabled: settings.PostPasteLearningEnabled);
```

- [ ] **Step 12: Add the Recording-page toggle (Windows-only)**

In `src/Winpepper.App/Views/RecordingPage.xaml`, inside the "Options"
`StackPanel` (after the `SpeakerFilterToggle` at line 52), add:

```xml
                    <ToggleSwitch x:Name="PostPasteLearningToggle" AutomationProperties.AutomationId="RecordingPostPasteLearningToggle" Header="Offer to learn corrections after typing" />
                    <TextBlock Text="After Winpepper types, watch for an edit you make and offer to remember it as a correction. Off by default."
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
```

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, after the
`SpeakerFilterToggle` wiring (line 64), add:

```csharp
        PostPasteLearningToggle.IsOn = vm.PostPasteLearningEnabled;
        PostPasteLearningToggle.Toggled += (_, _) => vm.PostPasteLearningEnabled = PostPasteLearningToggle.IsOn;
```

- [ ] **Step 13: Full non-Windows suite green across every touched project**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for proj in Winpepper.Core.Tests Winpepper.Cleanup.Tests Winpepper.Models.Tests; do
  dotnet build "tests/$proj/$proj.csproj" -f net9.0 -p:EnableWindowsTargeting=true -v minimal
  dotnet exec "tests/$proj/bin/Debug/net9.0/$proj.dll" -notrait "Platform=Windows"
done
```
Expected: each project reports `Failed: 0`.

- [ ] **Step 14: Commit**

```bash
git add src/Winpepper.Core/Learning/PostPasteGate.cs \
        tests/Winpepper.Core.Tests/Learning/PostPasteGateTests.cs \
        src/Winpepper.Core/Settings/AppSettings.cs \
        tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs \
        src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs \
        src/Winpepper.App/Hosting/PipelineHost.cs \
        src/Winpepper.App/Hosting/AppShell.cs \
        src/Winpepper.App/Views/RecordingPage.xaml \
        src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "$(cat <<'EOF'
feat: add default-off "offer to learn corrections" toggle

Post-paste learning was always on. Adds AppSettings.PostPasteLearningEnabled
(default false), a pure PostPasteGate.ShouldWatch predicate gating both
PipelineHost trigger sites, a RecordingSettingsViewModel property, and a
Recording-page ToggleSwitch. Setting threaded in via AppShell. Default off is
the requirement.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Windows Smoke Test Checklist

These behaviors live in `#if WINDOWS` code that cannot build or run on Linux.
They are implemented in this plan (not stubbed, not deferred) and MUST be
verified on the Windows VM after the plan lands. Each maps to a spec
requirement whose pure-managed logic is already covered by the Linux tests
above.

1. **Corrupt-settings backup (Task 2):** Put invalid JSON in
   `%LOCALAPPDATA%\winpepper\settings.json`, launch. App starts with defaults;
   a `settings.json.bad-<timestamp>` file exists; a warning is logged.
2. **Upgrade survives (Tasks 2-5):** Complete onboarding, toggle a setting,
   then run an MSI MajorUpgrade. After upgrade the app does NOT re-onboard and
   the toggled setting is retained.
3. **Skip completes setup (Task 4):** On a fresh profile, reach DownloadModels
   and click Skip. Relaunch — onboarding does NOT reappear.
4. **Setup skips resolved steps (Tasks 6-7):** With a valid persisted mic +
   hotkeys + installed models, launching onboarding starts at Test dictation;
   with models missing it starts at Download and downloads ONLY the missing
   model(s); with a removed mic it starts at PickMic. All steps remain
   navigable via Back.
5. **Window size (Task 8):** First launch opens at ~1/3 width × 1/2 height of
   the platform default (min 480×400). Resize, relaunch — the new size is
   restored.
6. **Learning toggle default off (Task 9):** Fresh profile: after dictation +
   inject, NO "Learn correction" prompt appears. Turn on
   "Offer to learn corrections after typing" on the Recording page; repeat —
   the prompt now appears. Relaunch — the toggle state persists.

---

## Self-Review

**1. Spec coverage.**

- **Task 1 — remove app-name corrector:** class + its tests deleted (T1 S1);
  `ApplyDeterministicPostPass` reduced to `CaseAwareReplacer.Apply` and comment
  updated (T1 S2); the two end-to-end tests asserting the removed behavior in
  `CleanupRunnerTests` deleted (T1 S3 — discovered during investigation; not in
  the original spec bullet but required or the build stays red); `"<CORRECTION-HINTS>"`
  left intact (T1 S2); no dangling refs (T1 S5). ✓
- **Task 2(i) — corrupt-file backup + log, no silent reset:** T2 (SettingsStore
  backup, log sink, defaults; tested). ✓
- **Task 2(ii) — reduce lost-write window (writer method + onboarding + settings
  pages):** `QueueAndFlushAsync` (T3); onboarding advances flush (T4); recording
  settings setters + Autostart toggle flush (T5). ✓
- **Task 2(iii) — Skip sets OnboardingCompleted + flush:** T4 `SkipAsync`
  (tested). ✓
- **Task 3(i) — hydrate VM from settings:** T6 `InitializeFrom` prefill
  (tested). ✓
- **Task 3(ii) — auto-advance to first unresolved step:** T6
  `FirstUnresolvedStep` with all three resolution rules (tested for mic-empty,
  mic-missing, hotkey-invalid, models-missing, all-resolved). ✓
- **Task 3(iii) — models check via IsFullyInstalled/FindMissing against the
  models dir, never re-download:** T6 page wiring computes `modelsResolved` via
  `MissingModelsResolver.FindMissing`; T7 downloader fetches only `FindMissing`
  results (Linux test pins the selection; Windows smoke proves the download). ✓
- **Task 3(iv) — wire the REAL downloader, only missing, auto-resolve when
  none:** T7 replaces the stub with `ModelsServices.DownloadAsync` over the
  missing set (Windows smoke item 4). ✓
- **Task 4 — default window size 1/3 W × 1/2 H, clamped, only when no persisted
  size; persist resizes; no position/multi-monitor:** T8 `WindowSizePolicy`
  (tested), nullable size fields (tested round-trip), MainWindow restore/default/persist
  wiring (Windows smoke item 5). ✓
- **Task 5 — post-paste toggle default off, gate BOTH trigger sites, Recording
  toggle following SpeakerFilter precedent:** T9 `PostPasteLearningEnabled`
  (tested), `PostPasteGate` gating both sites (tested), VM property (tested),
  XAML toggle + label (Windows smoke item 6). ✓
- **Verification — pure-managed tests via `dotnet exec`, `-notrait
  "Platform=Windows"`, SDK provisioning:** T0 provisions; every test step uses
  the in-process runner; WinUI-only bits routed to the Windows Smoke Test
  Checklist. ✓
- **Out of scope — hook + packaging untouched:** no task edits
  `src/Winpepper.Platform/Hotkeys` or `packaging/`. ✓

**1b. No silent deferrals of required behavior.** No stubs/mocks/fake providers
stand in for required production behavior. The one pre-existing stub
(`() => Task.CompletedTask` onboarding downloader) is REPLACED with production
behavior in Task 7 (real `ModelsServices.DownloadAsync`), not left as a seam.
The behaviors that cannot execute on Linux are genuinely platform-bound WinUI
integration (window resize/persist, the actual model download over HTTP, the
XAML toggles, PipelineHost gating); each has (a) its pure decision logic tested
on Linux — `WindowSizePolicy`, `MissingModelsResolver`/onboarding-scope,
`PostPasteGate`, `InitializeFrom`, `SettingsStore` backup, `QueueAndFlushAsync`
— and (b) a named production outcome in the Windows Smoke Test Checklist. No
requirement is moved to "known limitations" or "future work." There is **no
UNRESOLVED COVERAGE GAP**.

**2. Placeholder scan.** No "TBD/TODO/handle edge cases/similar to Task N".
Every code step shows complete, copy-pasteable code and each run step gives an
exact command and expected summary. Repeated boilerplate (SDK export, build,
exec) is intentionally restated per step because tasks are executed by fresh
implementers who may read out of order.

**3. Type consistency.** Signatures are used identically across tasks:
`QueueAndFlushAsync(Func<AppSettings, AppSettings>)` (defined T3, consumed T4/T5/T8/T9);
`SkipAsync()` (T4, replacing the old `Skip()`; the two callers updated — test
file T4 S1, WinUI OnSkip T4 S6);
`InitializeFrom(AppSettings, bool persistedMicPresent, bool modelsResolved)`
(T6, called from the page T6 S5);
`WindowSizePolicy.ComputeDefault(int, int, int, int)` returning
`(int Width, int Height)` (T8);
`PostPasteGate.ShouldWatch(bool, bool, bool, bool, bool)` (T9, both PipelineHost
sites);
`AppSettings.WindowWidth/WindowHeight` (`int?`, T8) and
`AppSettings.PostPasteLearningEnabled` (`bool`, T9);
`RecordingSettingsViewModel.CommitDurable(Func<AppSettings, AppSettings>)`
(defined T5, reused by the new property in T9);
`MissingModelsResolver.FindMissing(registry.All, ModelsRoot, names)` and
`ModelsServices.DownloadAsync(descriptor, ModelsRoot, progress, ct)` (T6/T7,
matching the real signatures read from source). The `FakeWriter` in
`RecordingSettingsViewModelTests` gains `FlushCount` in T5 and is reused by
T9's property test; the `FakeWriter` in `OnboardingViewModelTests` gains
`Flushes` in T4. No dangling or renamed-mismatch references.
