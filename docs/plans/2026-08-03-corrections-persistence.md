# Corrections Persistence Fix Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make Corrections UI edits persist to `corrections.json` and make the UI seed from that file at boot, so user corrections survive app restart and are applied to dictations.

**Architecture:** `AppShell.Create` currently constructs `CorrectionsViewModel` with empty seed data and a no-op persist callback, *before* the `CorrectionStore` even exists — so UI edits die with the process and the dictation pipeline (which reads the disk store) never sees them. The fix hoists the store construction above the VM, and extracts the seed/wire logic into a new Linux-testable static factory `CorrectionsWiring` in `Winpepper.Corrections` (the only shared project that can see both `CorrectionStore` and `Winpepper.Core.ViewModels.CorrectionsViewModel` — Corrections → Core is the existing one-way dependency; Core has zero project references and must stay that way). AppShell calls the factory and supplies an `onError` callback that logs a warning and reports to the existing `ErrorBus`.

**Tech Stack:** C# / .NET 9, xUnit v3 (in-process runner via `dotnet exec`), Shouldly assertions.

## Global Constraints

- Worktree root for ALL commands and paths in this plan: `/home/dan/code/winpepper/.worktrees/corrections-persistence` — run every command from this directory.
- Testing (AGENTS.md, mandatory): build test projects with `-c Release`, run via the xUnit v3 in-process runner (`dotnet exec <built test dll>`). **NEVER `dotnet test`** (VSTest host is unreliable).
- Before EVERY commit: `./scripts/linux-tests.sh` must exit 0 and print `LINUX SUITE: GREEN` (use a 1200s timeout; it builds and runs all 9 test projects).
- The Windows gate (`./scripts/windows-gate.sh`) is required before push and is handled by the root session AFTER this workflow. **Do NOT push from this workflow.**
- Every shell that builds/runs tests needs: `export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"; export PATH="$DOTNET_ROOT:$PATH"`.
- Do NOT modify `src/Winpepper.Corrections/CorrectionStore.cs` (atomic-write semantics), `src/Winpepper.Core/Io/AtomicFile.cs`, or `CaseAwareReplacer`.
- Do NOT change the `CorrectionsViewModel` constructor signature (10 existing tests in `tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs` depend on it, and the `Action` callback is the only legal Core↔Corrections seam).
- NEVER add a `Winpepper.Core` → `Winpepper.Corrections` project reference (layering is deliberately one-way: Corrections → Core).
- Keep the change minimal: wiring + factory + tests. No opportunistic refactors.
- Nullable warnings are build errors repo-wide (`<WarningsAsErrors>nullable</WarningsAsErrors>`).
- Commit style: conventional commits (`fix(scope): lowercase imperative summary`), body = problem paragraph + `- Component: change` bullets + verification line, then the Amplifier trailer exactly:

  ```
  Generated with Amplifier

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```

  Because the Windows gate runs after this workflow, the verification line in this plan's commits is `Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.` (do not claim `GATE: GREEN` — it has not run yet).

---

## Scope Check

Single subsystem (corrections wiring between one ViewModel and one store, plus one call site). One plan.

**Highest-level user story this proves:** "a correction I add in the UI survives app restart and reaches dictation." The dictation pipeline reads `corrections.json` via its own `CorrectionStore` over the same path (`PipelineHost` already consumes `correctionStore` — read path is already wired and tested elsewhere; commit `e619dc3` made corrections apply on every dictation path). Therefore the highest Linux-testable proof is: *a VM edit made through the production factory lands in the file a fresh `CorrectionStore(path).Load()` can read* — that is exactly regression test (a). The WinUI click-handler → VM path is Windows-only (`#if WINDOWS`, no test project references `Winpepper.App`) and is compile-verified by the Windows gate before push.

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Corrections/CorrectionsWiring.cs` | Create | Static factory: seed `CorrectionsViewModel` from `CorrectionStore.Load()`, wire persist callback to `CorrectionStore.Save()`, contain load/persist failures behind an optional `onError` callback. |
| `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` | Create | Regression tests (a)–(d) from the spec, plus seed-failure fallback. Flat layout (this test project has no subfolders). |
| `tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj` | Modify | Add explicit `Winpepper.Core` ProjectReference (house style: list every directly-used project; the tests use `Winpepper.Core.ViewModels` types directly). |
| `src/Winpepper.App/Hosting/AppShell.cs:187-210` | Modify | Hoist `correctionStore` construction above the VM; replace empty-seeded/no-op VM construction with a `CorrectionsWiring.CreateViewModel` call; report persist failures via logger + `ErrorBus`. |

Known interaction, deliberately unchanged (do not "fix" it — out of scope): the post-paste learning path (`CorrectionStoreWriter`, wired at `AppShell.cs:287-292`) does read-modify-write `Add*` on the same file, while the VM's persist is a whole-document `Save` (last-writer-wins). A background-learned entry added after the VM seeded will be overwritten by the next explicit UI edit. This trade-off is documented in the factory's XML doc comment (Task 1).

---

### Task 1: `CorrectionsWiring` factory — seed from disk, persist to disk

**Files:**
- Create: `src/Winpepper.Corrections/CorrectionsWiring.cs`
- Create: `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs`
- Modify: `tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj`
- Test: `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs`

**Interfaces:**
- Consumes (all pre-existing, unchanged):
  - `Winpepper.Corrections.CorrectionStore` — `CorrectionStore(string path)`, `CorrectionsData Load()`, `void Save(CorrectionsData data)`.
  - `Winpepper.Corrections.CorrectionsData` — record with `IReadOnlyList<string> Preferred`, `IReadOnlyDictionary<string, string> Replacements`, `static CorrectionsData Empty` (`Schema` defaults to `CurrentSchema` — never set it explicitly).
  - `Winpepper.Core.ViewModels.CorrectionsViewModel` — ctor `(IEnumerable<string> initialPreferred, IEnumerable<KeyValuePair<string, string>> initialReplacements, Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> persist)`; members `ObservableCollection<PreferredEntry> Preferred`, `ObservableCollection<ReplacementEntry> Replacements`, `string? AddPreferred(string)`, `string? AddReplacement(string, string)`, `void RemovePreferred(PreferredEntry)`, `void RemoveReplacement(ReplacementEntry)`. `PreferredEntry` has `string Text`; `ReplacementEntry` has `string Wrong`, `string Right`.
- Produces: `Winpepper.Corrections.CorrectionsWiring` (public static class) with `public static CorrectionsViewModel CreateViewModel(CorrectionStore store)`. Task 2 extends this to `CreateViewModel(CorrectionStore store, Action<Exception>? onError = null)` — additive optional parameter, so Task 1 call sites/tests stay valid.

- [ ] **Step 1: Add the `Winpepper.Core` ProjectReference to the test project**

In `tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj`, find the existing line:

```xml
    <ProjectReference Include="..\..\src\Winpepper.Corrections\Winpepper.Corrections.csproj" />
```

and add this line directly above it (same `<ItemGroup>`):

```xml
    <ProjectReference Include="..\..\src\Winpepper.Core\Winpepper.Core.csproj" />
```

(Core already flows in transitively; the explicit reference is house style because the new tests use `Winpepper.Core.ViewModels` types directly. Change nothing else in the file.)

- [ ] **Step 2: Write the failing tests**

Create `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` with exactly:

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionsWiringTests : IDisposable
{
    private readonly string _path;

    public CorrectionsWiringTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"corrections-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_path)}.tmp-*"))
            File.Delete(f);
    }

    [Fact]
    public void Vm_Add_RoundTrips_To_Disk()
    {
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.AddPreferred("ChatGPT").ShouldBeNull();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();

        // Persistence is proven by a FRESH store over the same path, exactly
        // like the dictation pipeline reads it — not by in-memory state.
        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT" });
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Seeds_From_Existing_Store()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });

        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.Preferred.Select(p => p.Text).ShouldBe(new[] { "ChatGPT", "Anthropic" });
        vm.Replacements.Count.ShouldBe(1);
        vm.Replacements[0].Wrong.ShouldBe("chat gbt");
        vm.Replacements[0].Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void Vm_Remove_Persists_The_Removal()
    {
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        });
        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        vm.RemovePreferred(vm.Preferred.Single(p => p.Text == "ChatGPT"));
        vm.RemoveReplacement(vm.Replacements[0]);

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "Anthropic" });
        loaded.Replacements.ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj \
  -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **BUILD FAILS** with `error CS0103: The name 'CorrectionsWiring' does not exist in the current context` (compile failure is the RED state — the tests reference a type that does not exist yet).

- [ ] **Step 4: Write the minimal implementation**

Create `src/Winpepper.Corrections/CorrectionsWiring.cs` with exactly:

```csharp
using Winpepper.Core.ViewModels;

namespace Winpepper.Corrections;

/// <summary>
/// Builds the <see cref="CorrectionsViewModel"/> for the Corrections settings
/// page: seeds it from <see cref="CorrectionStore.Load"/> and wires its
/// persist callback to <see cref="CorrectionStore.Save"/>.
///
/// Lives in Winpepper.Corrections because it is the only shared project that
/// can see both the store and the VM (Corrections -> Core; Core has no
/// project references; Winpepper.App is WinUI-bound and untestable on Linux).
/// The store path stays injected by the caller — AppPaths is App-layer.
///
/// Known interaction (deliberate, unchanged): the VM's persist is a
/// whole-document last-writer-wins Save, while the post-paste learning path
/// (<see cref="CorrectionStoreWriter"/>) does read-modify-write Add*. A
/// background-learned entry added after the VM was seeded is overwritten by
/// the next explicit UI edit.
/// </summary>
public static class CorrectionsWiring
{
    public static CorrectionsViewModel CreateViewModel(CorrectionStore store)
    {
        var initial = store.Load();
        return new CorrectionsViewModel(
            initial.Preferred,
            initial.Replacements,
            (preferred, replacements) => store.Save(new CorrectionsData
            {
                Preferred = preferred,
                Replacements = replacements,
            }));
    }
}
```

(No error handling yet — Task 2 adds it test-first. Types line up exactly: `IReadOnlyList<string>` ↔ `Preferred`, `IReadOnlyDictionary<string, string>` ↔ `Replacements`; an `IReadOnlyDictionary<K,V>` is an `IEnumerable<KeyValuePair<K,V>>`, so no adapters.)

- [ ] **Step 5: Run to verify it passes**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj \
  -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Corrections.Tests/bin/Release/net9.0/Winpepper.Corrections.Tests.dll \
  -notrait "Platform=Windows"
```

Expected: build succeeds; runner summary line ends with `Errors: 0, Failed: 0` and includes the 3 new tests (total for this project rises by 3 over its previous count).

- [ ] **Step 6: Full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence && ./scripts/linux-tests.sh
```

(Use a 1200-second timeout.) Expected: exits 0, prints `LINUX SUITE: GREEN`.

- [ ] **Step 7: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
git add src/Winpepper.Corrections/CorrectionsWiring.cs \
        tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs \
        tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj
git commit -m "$(cat <<'EOF'
fix(corrections): add CorrectionsWiring factory that seeds the VM from disk and persists edits

The Corrections UI never persisted: AppShell built CorrectionsViewModel
with empty seed data and a no-op persist callback, so UI edits lived
only in process memory and the dictation pipeline (which reads the disk
store) never saw them.

- CorrectionsWiring (new, Winpepper.Corrections): static factory that
  seeds CorrectionsViewModel from CorrectionStore.Load() and wires its
  persist callback to CorrectionStore.Save(). Lives in Corrections (not
  Core: would invert layering; not App: WinUI-bound, untestable on
  Linux). Store path stays caller-injected.
- Tests: add round-trips to a fresh store over the same path; VM seeds
  Preferred/Replacements from pre-existing data; removals persist.
- Winpepper.Corrections.Tests.csproj: explicit Winpepper.Core reference
  (house style; tests use Core ViewModel types directly).

Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.

Generated with Amplifier

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Failure containment in `CorrectionsWiring`

**Files:**
- Modify: `src/Winpepper.Corrections/CorrectionsWiring.cs`
- Modify: `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` (append tests)
- Test: `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs`

**Interfaces:**
- Consumes: Task 1's `CorrectionsWiring.CreateViewModel(CorrectionStore)`.
- Produces (final signature, consumed by Task 3): `public static CorrectionsViewModel CreateViewModel(CorrectionStore store, Action<Exception>? onError = null)` in `Winpepper.Corrections.CorrectionsWiring`. `onError` fires on a failed boot `Load()` (VM falls back to empty seed) and on every failed `Save()` (in-memory edit kept, exception contained). Additive optional parameter — Task 1 tests remain valid unchanged.

**Why:** `CorrectionStore.Save()` deliberately rethrows I/O failures (`AtomicFile.WriteAllText` deletes its temp then `throw;`), and `CorrectionsViewModel.Persist()` has NO try/catch — an escape reaches the WinUI click handler and crashes the app. `Load()` swallows only `JsonException`; a locked/permission-denied file throws out of it and would crash `AppShell.Create` at boot. Both must be contained *inside the factory's lambdas* without touching `CorrectionStore` or the VM.

- [ ] **Step 1: Write the failing tests**

Append inside the `CorrectionsWiringTests` class in `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` (immediately before the closing brace of the class):

```csharp
    [Fact]
    public void Persist_Failure_Does_Not_Throw_Out_Of_Add_Or_Remove()
    {
        // The store path's PARENT is a regular file, so AtomicFile's
        // Directory.CreateDirectory throws IOException on every Save —
        // deterministic on both Linux and Windows.
        var blocker = Path.Combine(Path.GetTempPath(), $"corrections-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var store = new CorrectionStore(Path.Combine(blocker, "corrections.json"));
            Exception? seen = null;
            var vm = CorrectionsWiring.CreateViewModel(store, onError: ex => seen = ex);

            Should.NotThrow(() => vm.AddPreferred("ChatGPT"));
            seen.ShouldNotBeNull();
            vm.Preferred.Count.ShouldBe(1); // in-memory edit is kept

            seen = null;
            Should.NotThrow(() => vm.RemovePreferred(vm.Preferred[0]));
            seen.ShouldNotBeNull();
            vm.Preferred.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Persist_Failure_Without_OnError_Is_Still_Contained()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"corrections-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "");
        try
        {
            var store = new CorrectionStore(Path.Combine(blocker, "corrections.json"));
            var vm = CorrectionsWiring.CreateViewModel(store);

            Should.NotThrow(() => vm.AddPreferred("ChatGPT"));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Seed_Load_Failure_Falls_Back_To_Empty_And_Reports()
    {
        File.WriteAllText(_path, """{"schema":1,"preferred":["ChatGPT"],"replacements":{}}""");
        // Hold the file with FileShare.None: CorrectionStore.Load()'s
        // File.ReadAllText then throws IOException (native sharing on
        // Windows; flock-based FileShare emulation between FileStreams on
        // Linux). Load() only swallows JsonException, so this escapes it.
        using var locker = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        Exception? seen = null;
        CorrectionsViewModel vm = null!;
        Should.NotThrow(() =>
            vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path), onError: ex => seen = ex));

        vm.Preferred.ShouldBeEmpty();
        vm.Replacements.ShouldBeEmpty();
        seen.ShouldNotBeNull();
    }
```

- [ ] **Step 2: Run to verify the new tests fail**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj \
  -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **BUILD FAILS** with `error CS1739: The best overload for 'CreateViewModel' does not have a parameter named 'onError'` (the RED state — the parameter does not exist yet).

- [ ] **Step 3: Implement failure containment**

Replace the entire `CreateViewModel` method in `src/Winpepper.Corrections/CorrectionsWiring.cs` (keep the class XML doc comment and everything else) with:

```csharp
    public static CorrectionsViewModel CreateViewModel(
        CorrectionStore store,
        Action<Exception>? onError = null)
    {
        CorrectionsData initial;
        try
        {
            initial = store.Load();
        }
        catch (Exception ex)
        {
            // Load() swallows only JsonException; I/O errors (locked file,
            // permissions) escape it and must not crash app boot. Degrade to
            // an empty seed — the UI stays usable in-memory.
            initial = CorrectionsData.Empty;
            onError?.Invoke(ex);
        }

        return new CorrectionsViewModel(
            initial.Preferred,
            initial.Replacements,
            (preferred, replacements) =>
            {
                try
                {
                    store.Save(new CorrectionsData
                    {
                        Preferred = preferred,
                        Replacements = replacements,
                    });
                }
                catch (Exception ex)
                {
                    // Save() deliberately rethrows I/O failures (AtomicFile),
                    // and CorrectionsViewModel.Persist() has no containment —
                    // an escape would reach the WinUI click handler. Contain
                    // here: the in-memory edit is kept; disk stays stale
                    // until the next successful persist.
                    onError?.Invoke(ex);
                }
            });
    }
```

- [ ] **Step 4: Run to verify all tests pass**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj \
  -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Corrections.Tests/bin/Release/net9.0/Winpepper.Corrections.Tests.dll \
  -notrait "Platform=Windows"
```

Expected: build succeeds; summary ends with `Errors: 0, Failed: 0` (3 more tests than after Task 1 — all 6 `CorrectionsWiringTests` green).

- [ ] **Step 5: Full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence && ./scripts/linux-tests.sh
```

(1200s timeout.) Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
git add src/Winpepper.Corrections/CorrectionsWiring.cs \
        tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs
git commit -m "$(cat <<'EOF'
fix(corrections): contain load/persist failures in CorrectionsWiring behind onError

CorrectionStore.Save() deliberately rethrows I/O failures and
CorrectionsViewModel.Persist() has no try/catch, so a failed save would
escape into the WinUI click handler and crash the app. Load() swallows
only JsonException, so a locked corrections.json would crash boot.

- CorrectionsWiring: optional Action<Exception>? onError parameter;
  boot Load() failure degrades to an empty seed, Save() failure keeps
  the in-memory edit and reports instead of throwing. Store semantics
  untouched.
- Tests: persist failure does not throw out of add/remove (with and
  without onError) and keeps in-memory state; seed-load failure falls
  back to empty and reports.

Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.

Generated with Amplifier

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: Wire AppShell to the factory

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:187-210`

**Interfaces:**
- Consumes:
  - `Winpepper.Corrections.CorrectionsWiring.CreateViewModel(CorrectionStore store, Action<Exception>? onError = null)` (Task 2).
  - Existing `AppShell.Create()` locals already in scope at line 187: `factory` (`ILoggerFactory`, created ~line 71) and `errorBus` (`Winpepper.Core.Errors.ErrorBus`, created at line 116).
  - `AppPaths.CorrectionsJson` (App-layer path — stays in AppShell, never in the factory).
  - `Winpepper.Core.Errors.ErrorStage.Learning` — the stage whose deep-link opens the Corrections page (`ErrorDeepLink`: `Learning => "corrections"`); it is toast-silent by `ErrorToastPolicy` design (lands on the Diagnostics ring + log — this matches the established pattern for background persistence issues).
  - `ErrorBus.Report(ErrorStage stage, Exception ex, Guid sessionId)` — `Guid.Empty` = "no session"; `Report` cannot throw (subscriber exceptions are swallowed internally), so it is safe inside the persist error path.
- Produces: nothing new for later tasks (final task). `AppShell.CorrectionsVm` behavior changes: seeded from disk, persists to disk.

**Caution:** `AppShell.cs` is wrapped in `#if WINDOWS` and `Winpepper.App` is not compiled by the Linux suite, so this edit gets NO compile check until the Windows gate runs (after this workflow, before push). Copy the code below exactly; then re-read the edited region verifying every identifier against the Interfaces block above. House style in this file: fully-qualified names for cross-assembly types (`Winpepper.Corrections.*`, `Winpepper.Core.Errors.*`) — do NOT add new `using` directives.

- [ ] **Step 1: Replace the wiring block**

In `src/Winpepper.App/Hosting/AppShell.cs`, find this exact block (lines 187–210):

```csharp
        // Plan 2 normally provides initial corrections; until then, empty.
        var correctionsVm = new CorrectionsViewModel(
            Array.Empty<string>(),
            new Dictionary<string, string>(),
            (_, _) => { /* Plan 2 wires CorrectionStore.Save() here */ });

        var autostart = new AutostartRegistry();
        var sounds = new WinUiSoundEffectPlayer(AppPaths.AssetsDir) { Enabled = settings.PlaySounds };

        // PLAN2-TYPE — Plan 2 owns these types; constructing them here so Plan 3's
        // pipeline can invoke real cleanup + window context. Each one is optional —
        // if the model or registry isn't present yet, we fall back to raw transcript.
        Winpepper.Corrections.CorrectionStore? correctionStore = null;
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null;

        try
        {
            correctionStore = new Winpepper.Corrections.CorrectionStore(AppPaths.CorrectionsJson);
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "CorrectionStore unavailable; cleanup will run with empty corrections.");
        }
```

and replace it with exactly:

```csharp
        // Corrections: the store must exist before the VM so the VM can seed
        // from disk and persist back through it (the dictation pipeline reads
        // the same file). Store construction stays optional: if it fails, the
        // UI still works in-memory for this session and cleanup runs with
        // empty corrections.
        Winpepper.Corrections.CorrectionStore? correctionStore = null;
        try
        {
            correctionStore = new Winpepper.Corrections.CorrectionStore(AppPaths.CorrectionsJson);
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "CorrectionStore unavailable; cleanup will run with empty corrections.");
        }

        var correctionsVm = correctionStore is not null
            ? Winpepper.Corrections.CorrectionsWiring.CreateViewModel(
                correctionStore,
                onError: ex =>
                {
                    factory.CreateLogger("Winpepper.App").LogWarning(ex,
                        "Corrections persistence failed; edits are kept in memory for this session.");
                    errorBus.Report(Winpepper.Core.Errors.ErrorStage.Learning, ex, Guid.Empty);
                })
            : new CorrectionsViewModel(
                Array.Empty<string>(),
                new Dictionary<string, string>(),
                (_, _) => { /* no store: in-memory only for this session */ });

        var autostart = new AutostartRegistry();
        var sounds = new WinUiSoundEffectPlayer(AppPaths.AssetsDir) { Enabled = settings.PlaySounds };

        // PLAN2-TYPE — Plan 2 owns these types; constructing them here so Plan 3's
        // pipeline can invoke real cleanup + window context. Each one is optional —
        // if the model or registry isn't present yet, we fall back to raw transcript.
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null;
```

Nothing else in the file changes. Note the later consumers of `correctionStore` (`CorrectionStoreWriter` wiring at ~line 287 and the `PipelineHost` args at ~line 341) still see the same nullable local — only its declaration moved earlier.

- [ ] **Step 2: Manual verification checklist (no Linux compile exists for this file)**

Re-read the edited region and confirm each item:
- `correctionStore` is declared exactly once in `Create()` (the old declaration at the former line 199 is gone; the `windowContext` declaration remains, now alone under the PLAN2-TYPE comment).
- The factory call reads `Winpepper.Corrections.CorrectionsWiring.CreateViewModel(` — fully qualified, matching the Task 2 signature `(CorrectionStore, Action<Exception>?)`.
- `errorBus` (lowercase local, created at line ~116) is used — not the `ErrorBus` property, which is not assigned yet inside `Create()`.
- `ErrorStage.Learning` is spelled `Winpepper.Core.Errors.ErrorStage.Learning`; `Report`'s third argument is `Guid.Empty`.
- The fallback branch still compiles against the unchanged `CorrectionsViewModel` ctor: `Array.Empty<string>()`, `new Dictionary<string, string>()`, `(_, _) => { ... }`.
- `git -C /home/dan/code/winpepper/.worktrees/corrections-persistence diff --stat` shows exactly one modified file: `src/Winpepper.App/Hosting/AppShell.cs`.

- [ ] **Step 3: Full Linux suite (required before every commit, even for Windows-only edits)**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence && ./scripts/linux-tests.sh
```

(1200s timeout.) Expected: `LINUX SUITE: GREEN` (proves nothing shared broke; `Winpepper.App` itself is compile-verified by the Windows gate before push).

- [ ] **Step 4: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/corrections-persistence
git add src/Winpepper.App/Hosting/AppShell.cs
git commit -m "$(cat <<'EOF'
fix(app): wire CorrectionsViewModel to CorrectionStore so UI corrections persist and load

AppShell built the Corrections VM with empty seed data and a no-op
persist callback, before the CorrectionStore even existed: corrections
added in the UI lived only in process memory, persisted data never
displayed, and the dictation pipeline read an always-empty store — all
configured corrections were lost on app restart.

- AppShell.Create: hoist the CorrectionStore construction above the VM
  and build the VM via CorrectionsWiring.CreateViewModel (seeds from
  Load(), persists via Save()). Persist/load failures log a warning and
  report to ErrorBus as ErrorStage.Learning (deep-links to the
  Corrections page; toast-silent per ErrorToastPolicy). Null-store
  fallback keeps the previous in-memory-only behavior.

Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.

Generated with Amplifier

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Spec-requirement → task map (self-review record)

| Spec requirement | Covered by |
|---|---|
| VM constructed after store, seeded from `Load()` (Preferred + Replacements) | Task 3 Step 1 (hoist + factory call); seeding proven Linux-side by Task 1 test `Vm_Seeds_From_Existing_Store` |
| Persist callback calls `Save(...)` with current preferred/replacements | Task 1 Step 4; proven by `Vm_Add_RoundTrips_To_Disk` |
| Persist failure: no crash, log warning, surface via ErrorBus (established pattern) | Task 2 (containment + tests), Task 3 (LogWarning + `ErrorBus.Report(ErrorStage.Learning, ...)` — the repo's established log+Report idiom) |
| Testable, Linux-runnable factory in a non-WinUI project, correct dependency direction | Task 1 (`Winpepper.Corrections`, the only shared project seeing both types; Corrections → Core verified) |
| Regression test (a): VM add round-trips to fresh `CorrectionStore(path).Load()` | Task 1 `Vm_Add_RoundTrips_To_Disk` |
| Regression test (b): VM seeds from existing store | Task 1 `Vm_Seeds_From_Existing_Store` |
| Regression test (c): removal persists | Task 1 `Vm_Remove_Persists_The_Removal` |
| Regression test (d): persist failure does not throw out of add/remove | Task 2 `Persist_Failure_Does_Not_Throw_Out_Of_Add_Or_Remove`, `Persist_Failure_Without_OnError_Is_Still_Contained` |
| AGENTS.md testing rules; green Linux suite before each commit; no push | Every task's build/exec/`linux-tests.sh` steps; no push step exists in this plan |
| No changes to `CorrectionStore` atomics / `CaseAwareReplacer`; minimal change | File Structure (neither file touched); three focused commits |

No stubs, mocks, or fake providers stand in for required behavior anywhere: tests exercise the production factory against the real `CorrectionStore` on real temp files, and the only no-op persist remaining is the pre-existing degraded null-store fallback (store construction failure), which preserves today's behavior for that edge and is outside the spec's required path. No unresolved coverage gaps.
