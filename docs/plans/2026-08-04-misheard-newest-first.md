# Misheard Replacements Newest-First Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** In Settings → Corrections, place the add-new-entry fields at the TOP of the "Misheard replacements" card, and render the existing entries newest-to-oldest (most recently added first) — without changing the `corrections.json` format or its on-disk ordering.

**Architecture:** Humble-object pattern (repo-wide rule): all ordering logic lives in `CorrectionsViewModel` (`Winpepper.Core`, Linux-testable). The `ObservableCollection<ReplacementEntry>` becomes the newest-first *display* order (the WinUI `ListView` renders it verbatim — there is no sort/view layer), while `Persist()` reverses it back so the persisted dictionary stays canonical oldest-first/append-at-end. The disk file is therefore byte-order-identical to today, so every downstream consumer (`CaseAwareReplacer` tie-breaks, `PromptBuilder` `<CORRECTION-HINTS>` line order, AssemblyAI `custom_spelling` array order, the post-paste learning writer's append-at-end) is untouched. The XAML change is a pure element reorder inside the card.

**Tech Stack:** C# / .NET 9, WinUI 3 (Windows App SDK), xUnit v3 + Shouldly (run via `dotnet exec`, never `dotnet test`).

## Global Constraints

- Work inside the worktree: `/home/dan/code/winpepper/.worktrees/misheard-newest-first` (branch `feat/misheard-newest-first`). `cd` there before every command.
- `dotnet` is NOT on PATH. In every shell first run: `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"`.
- Never use `dotnet test` (VSTest host unreliable here). Build test projects with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, then run via the xUnit v3 in-process runner: `dotnet exec <built dll>`.
- Before EVERY commit: `./scripts/linux-tests.sh` must exit 0 and print `LINUX SUITE: GREEN` (allow a 1200s timeout).
- Before push: `./scripts/windows-gate.sh` (run from WSL) must exit 0 and print `GATE: GREEN` (~40 min; allow a 3600s timeout). Never run it concurrently with `linux-tests.sh`; never mix Linux and Windows builds in the same `bin/`/`obj/` (the scripts clean automatically).
- Do NOT change the `CorrectionsViewModel` constructor signature — `CorrectionsViewModel(IEnumerable<string> initialPreferred, IEnumerable<KeyValuePair<string, string>> initialReplacements, Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> persist)` — 10+ existing tests and `CorrectionsWiring` depend on it, and the `Action` callback is the only legal Core↔Corrections seam.
- Never add a `Winpepper.Core` → `Winpepper.Corrections` project reference (Core has zero project references and must stay that way).
- `CorrectionsData.CurrentSchema` stays `1`. No new persisted fields, no timestamps, no schema bump — `CorrectionStore.LoadLocked` (`src/Winpepper.Corrections/CorrectionStore.cs:46`) silently discards the whole file on schema mismatch, so a bump is a user-data-loss bug.
- `corrections.json` stays oldest-first / append-at-end. The persisted dictionary order after this change must be identical to today's for the same sequence of user actions.
- Automation IDs must remain exactly: `CorrectionsReplacementsList`, `CorrectionsNewWrongTextBox`, `CorrectionsNewRightTextBox`, `CorrectionsAddReplacementButton`, `CorrectionsReplacementsErrorLabel` (`docs/automation-ids.md:79-91` is a contract; a pure reorder needs no doc update).
- Scope: ONLY the "Misheard replacements" card. The structurally identical "Preferred transcriptions" card (`CorrectionsPage.xaml:16-45`) keeps its current layout and ordering — the spec names only the misheard list; the asymmetry is deliberate.
- Nullable warnings are build errors repo-wide (`WarningsAsErrors=nullable`).
- Commit style: conventional commits; body includes an honest `Verified:` line naming which gates actually ran; end with the Amplifier trailer:

  ```
  🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```

---

## File Structure

No new production files. All ordering behavior goes where it is Linux-testable; the WinUI edit is layout-only.

| File | Change | Responsibility |
|---|---|---|
| `src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs` | Modify 3 lines (`:19`, `:35`, `:74`) | Owns the ordering contract: collection = newest-first display order; `Persist()` maps back to canonical oldest-first disk order |
| `tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs` | Add 4 tests | Pins the VM ordering contract (seed order, add order, remove order, persist order) |
| `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` | Add 1 test | Pins the disk round-trip: display newest-first ⇄ file oldest-first, through the production factory and a real temp file |
| `src/Winpepper.App/Views/CorrectionsPage.xaml` | Reorder elements in the misheard card (`:47-78`) | Add-entry row + error label move above the `ListView` |
| `docs/manual-test.md` | Extend line 145 | Windows manual smoke line covers the new layout/order |
| `docs/plans/2026-08-04-misheard-newest-first.md` | This plan (+ gate evidence appended in Task 4) | Working/agent doc |

**Not touched:** `CorrectionsPage.xaml.cs` (binding `ReplacementsList.ItemsSource = _vm.Replacements` already renders collection order verbatim), `CorrectionStore.cs`, `CorrectionsWiring.cs`, `CorrectionsData.cs`, `CorrectionEntry.cs`, everything under `Winpepper.Cleanup`/`Winpepper.Asr`.

**Why display-order-in-VM + reverse-on-persist (and not the alternatives):**
- *Reversing the stored order too* would rewrite every user's `corrections.json` key order on their next edit, change the cleanup LLM prompt text, change the AssemblyAI `custom_spelling` array order, flip the `CaseAwareReplacer` stable-sort tie-break for equal-length case-insensitive-duplicate keys, and put post-paste-learned entries (which the learning writer appends at the file END behind the VM's back, `src/Winpepper.Corrections/CorrectionStore.cs:93-106`) at the *oldest* display position. All avoided by keeping disk order canonical.
- *A display-only projection in the UI layer* (CollectionViewSource/converter/mirror collection) has zero precedent in this codebase and would live in Linux-untestable `#if WINDOWS` code. The repo's established idiom is "sort in the model/VM layer, hand the UI an already-ordered collection" (`src/Winpepper.History/HistoryStore.cs:51`).

**Known, unchanged limitation:** the Corrections VM is long-lived and does not re-seed on page navigation (`CorrectionsPage.xaml.cs` `OnNavigatedTo` reuses `AppShell.CorrectionsVm`), so an entry learned via the post-paste toast appears in the list only after app relaunch — exactly as today. It then appears at the TOP (it is last in the file = newest). This plan does not alter that pre-existing behavior.

---

### Task 1: Newest-first ordering contract in `CorrectionsViewModel`

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs:19,35,74`
- Test: `tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs`

**Interfaces:**
- Consumes: existing `CorrectionsViewModel` public surface — ctor `(IEnumerable<string>, IEnumerable<KeyValuePair<string,string>>, Action<IReadOnlyList<string>, IReadOnlyDictionary<string,string>>)`, `ObservableCollection<ReplacementEntry> Replacements`, `string? AddReplacement(string wrong, string right)`, `void RemoveReplacement(ReplacementEntry e)`, `void Persist()`. `ReplacementEntry` has `string Wrong`, `string Right`, `string? Error`. None of these signatures change.
- Produces (later tasks rely on this): `vm.Replacements` is **newest-first display order** — index 0 is the most recently added entry; the ctor seeds it by reversing the incoming (oldest-first, disk-order) sequence. `Persist()` invokes the callback with the replacements dictionary in **oldest-first** order (reversed back), so the persisted file order for any action sequence is identical to the pre-change behavior. `Preferred` ordering is untouched.

- [ ] **Step 1: Write the four failing/pinning tests**

Append inside the `CorrectionsViewModelTests` class in `tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs` (file-scoped namespace `Winpepper.Core.Tests.ViewModels`, existing usings `Shouldly` / `Winpepper.Core.ViewModels` / `Xunit` plus implicit usings suffice; keep the existing `private static CorrectionsViewModel NewVm()` helper at the bottom of the class):

```csharp
    [Fact]
    public void AddReplacement_Inserts_NewestFirst()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });
    }

    [Fact]
    public void Ctor_Seeds_Replacements_NewestFirst()
    {
        // Disk order is oldest-first (new entries are appended at the END of
        // corrections.json by both Persist() and the post-paste learning
        // writer), so the LAST seeded pair is the newest and must render first.
        var vm = new CorrectionsViewModel(
            new List<string>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
            (_, _) => { });

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });
    }

    [Fact]
    public void RemoveReplacement_Preserves_NewestFirst_Order_Of_Survivors()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();
        vm.AddReplacement("open ai", "OpenAI").ShouldBeNull();

        vm.RemoveReplacement(vm.Replacements[1]); // the middle entry ("ann thropic")

        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "chat gbt" });
    }

    [Fact]
    public void Persist_Writes_Replacements_OldestFirst()
    {
        // The DISPLAY order is newest-first, but corrections.json stays
        // canonical oldest-first/append-at-end so the file byte-order, the
        // cleanup prompt hint order, the AssemblyAI custom_spelling order,
        // and the post-paste learning writer's append semantics are all
        // unchanged. This test pins that: it passes today and must STILL
        // pass after the newest-first change (it fails a naive
        // Insert(0)-only implementation that forgets to reverse in Persist).
        IReadOnlyDictionary<string, string>? captured = null;
        var vm = new CorrectionsViewModel(
            new List<string>(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
            (_, r) => captured = r);

        vm.AddReplacement("ann thropic", "Anthropic").ShouldBeNull();

        captured.ShouldNotBeNull();
        captured!.Keys.ShouldBe(new[] { "chat gbt", "ann thropic" });
    }
```

- [ ] **Step 2: Run the test class to verify the red/green split**

```bash
cd /home/dan/code/winpepper/.worktrees/misheard-newest-first
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -class "Winpepper.Core.Tests.ViewModels.CorrectionsViewModelTests"
```

Expected: build succeeds (the tests use only existing API); exactly **3 FAIL** with Shouldly sequence mismatches (`AddReplacement_Inserts_NewestFirst`, `Ctor_Seeds_Replacements_NewestFirst`, `RemoveReplacement_Preserves_NewestFirst_Order_Of_Survivors` — each expecting newest-first but observing oldest-first) and **`Persist_Writes_Replacements_OldestFirst` PASSES** (it pins today's disk contract). All pre-existing tests in the class also pass. If anything else fails, stop and investigate before touching production code.

- [ ] **Step 3: Implement the three-line ordering change**

In `src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs`, make exactly these three edits (line numbers as of `c71a40b`; locate by content if drifted):

Edit 1 — ctor seed loop (line 19). Change:

```csharp
        foreach (var r in initialReplacements) Replacements.Add(new ReplacementEntry(r.Key, r.Value));
```

to:

```csharp
        // Newest-first display: initialReplacements arrives oldest-first
        // (corrections.json document order), so inserting each at the front
        // leaves the most recently added entry at index 0.
        foreach (var r in initialReplacements) Replacements.Insert(0, new ReplacementEntry(r.Key, r.Value));
```

Edit 2 — `AddReplacement` (line 35). Change:

```csharp
        Replacements.Add(new ReplacementEntry(wrong.Trim(), right.Trim()));
```

to:

```csharp
        Replacements.Insert(0, new ReplacementEntry(wrong.Trim(), right.Trim()));
```

Edit 3 — `Persist()` (line 74). Change:

```csharp
        var r = Replacements.ToDictionary(x => x.Wrong, x => x.Right, StringComparer.Ordinal);
```

to:

```csharp
        // The collection is newest-first for display; reverse back to the
        // canonical oldest-first/append-at-end order so corrections.json,
        // its downstream consumers (prompt hints, custom_spelling), and the
        // post-paste learning writer's append semantics are unchanged.
        var r = Replacements.Reverse().ToDictionary(x => x.Wrong, x => x.Right, StringComparer.Ordinal);
```

(`Reverse()` is `System.Linq.Enumerable.Reverse` — unambiguous on `ObservableCollection<T>`, which has no instance `Reverse`. Do NOT touch the `Preferred` seed loop, `AddPreferred`, or the `Preferred` half of `Persist()`.)

- [ ] **Step 4: Run the test class to verify all green**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -class "Winpepper.Core.Tests.ViewModels.CorrectionsViewModelTests"
```

Expected: PASS — 0 failed, 0 errors, all tests in the class green (the pre-existing `AddReplacement_Adds_Valid_Pair` asserts `Replacements[0]` after a single add, which still holds under newest-first).

- [ ] **Step 5: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

(Allow a 1200s timeout.) Expected: exit 0 + `LINUX SUITE: GREEN`. This also proves the downstream consumers' suites (`Winpepper.Cleanup.Tests`, `Winpepper.Asr.Tests`, `Winpepper.Corrections.Tests`) are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(core): render misheard replacements newest-first in the corrections VM

The Replacements collection is now the newest-first display order (seed
reversed from disk order, adds insert at index 0), while Persist() reverses
back so corrections.json stays canonical oldest-first/append-at-end and no
downstream consumer (prompt hints, custom_spelling, learning writer) changes.

- Winpepper.Core: seed/insert/persist ordering in CorrectionsViewModel
- Winpepper.Core.Tests: 4 ordering-contract tests (add, seed, remove, persist)

Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Disk round-trip contract through the production factory

**Files:**
- Test: `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs`

**Interfaces:**
- Consumes: Task 1's contract (`vm.Replacements` newest-first; persisted dictionary oldest-first) via `CorrectionsWiring.CreateViewModel(CorrectionStore store, Action<Exception>? onError = null)` (unchanged), `CorrectionStore(string path)` with `CorrectionsData Load()` / `void Save(CorrectionsData data)`, and `CorrectionsData { IReadOnlyList<string> Preferred, IReadOnlyDictionary<string, string> Replacements }`.
- Produces: an executable pin on the one assumption Task 1 cannot prove in-memory — that a `Dictionary<string,string>` built oldest-first survives the System.Text.Json write→read round-trip in document order, so "newest-first on relaunch" actually holds against a real file.

- [ ] **Step 1: Write the round-trip test**

Append inside the `CorrectionsWiringTests` class in `tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs` (it already has the `IDisposable` temp-file fixture with `_path`, and existing tests already use `Select`, `CorrectionStore`, `CorrectionsData`; match the file's existing namespace):

```csharp
    [Fact]
    public void Replacements_Display_NewestFirst_While_Disk_Stays_OldestFirst()
    {
        // Seed the store the way an existing user's corrections.json looks:
        // oldest-first, append-at-end.
        new CorrectionStore(_path).Save(new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
        });

        var vm = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));

        // Display order: newest (last on disk) first.
        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "ann thropic", "chat gbt" });

        // A UI add lands at the TOP of the display...
        vm.AddReplacement("open ai", "OpenAI").ShouldBeNull();
        vm.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "ann thropic", "chat gbt" });

        // ...but is APPENDED at the END of the persisted file, keeping the
        // disk contract identical to today's and to the post-paste learning
        // writer. Proven with a FRESH store over the same path, exactly like
        // the dictation pipeline reads it.
        var loaded = new CorrectionStore(_path).Load();
        loaded.Replacements.Keys.ShouldBe(new[] { "chat gbt", "ann thropic", "open ai" });

        // And a fresh VM seeded from that file renders newest-first again —
        // the "relaunch shows newest-first" guarantee.
        var reseeded = CorrectionsWiring.CreateViewModel(new CorrectionStore(_path));
        reseeded.Replacements.Select(r => r.Wrong)
            .ShouldBe(new[] { "open ai", "ann thropic", "chat gbt" });
    }
```

- [ ] **Step 2: Run the test**

```bash
cd /home/dan/code/winpepper/.worktrees/misheard-newest-first
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet && export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Corrections.Tests/bin/Release/net9.0/Winpepper.Corrections.Tests.dll -method "*.CorrectionsWiringTests.Replacements_Display_NewestFirst_While_Disk_Stays_OldestFirst"
```

(If the wildcard `-method` pattern is not accepted by the runner, use `-class` with the class's fully-qualified name from the top of the file.)

Expected: **PASS on first run.** This is deliberately a verification/pinning test, not a red-first test: Task 1 already implemented the behavior at the VM seam; what this test adds is empirical proof of the JSON⇄Dictionary ordering assumption against a real file. **If it FAILS, halt** — do not adjust assertions to match observed order. A failure means the round-trip does not preserve document order, "newest-first after relaunch" is broken, and the fix (an explicitly ordered persisted structure) is a schema change that is out of scope per Global Constraints; surface it as a blocker instead.

- [ ] **Step 3: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0 + `LINUX SUITE: GREEN`.

- [ ] **Step 4: Commit**

```bash
git add tests/Winpepper.Corrections.Tests/CorrectionsWiringTests.cs
git commit -m "$(cat <<'EOF'
test(corrections): pin newest-first display vs oldest-first disk round-trip

Proves against a real temp file that the VM renders newest-first, persists
oldest-first/append-at-end, and re-seeds newest-first after reload — the
JSON<->Dictionary document-order assumption is now executable, not assumed.

- Winpepper.Corrections.Tests: round-trip ordering test via CorrectionsWiring

Verified: linux-tests.sh GREEN; windows-gate.sh to run before push.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: Move the add-entry row above the list (XAML) + manual smoke doc

**Files:**
- Modify: `src/Winpepper.App/Views/CorrectionsPage.xaml:47-78`
- Modify: `docs/manual-test.md:145`

**Interfaces:**
- Consumes: `vm.Replacements` newest-first (Task 1) — `ReplacementsList.ItemsSource = _vm.Replacements` in `CorrectionsPage.xaml.cs:20` renders the collection verbatim, so no code-behind change is needed for the ordering half.
- Produces: the final on-screen layout — add-entry fields on top, error label directly beneath them, entry list underneath, newest first. No element is renamed, added, or removed; all 5 automation IDs and both `Click` handler references (`OnAddReplacement`, `OnRemoveReplacement`) are preserved.

**Note:** this file is inside `#if WINDOWS`-guarded WinUI territory — the XAML compiler only runs on Windows, so Linux verification is (a) a careful diff re-read and (b) the mandatory green Linux suite proving nothing shared broke. The Windows gate in Task 4 compile-verifies it; the Windows Smoke Test Checklist at the end of this plan covers the rendered result. This is the repo's standard treatment for XAML-only edits — it is not a deferral.

- [ ] **Step 1: Reorder the card's children**

In `src/Winpepper.App/Views/CorrectionsPage.xaml`, find the card that begins with the comment `<!-- Misheard replacements -->` (lines 47-78 as of `c71a40b`). Its `StackPanel` currently contains, in order: heading TextBlock, caption TextBlock, `ListView`, add-entry `Grid`, error `TextBlock`. Replace the card so the `StackPanel` children are reordered to: heading, caption, **add-entry Grid**, **error TextBlock**, **ListView** — every element byte-identical, only the order changes:

```xml
            <!-- Misheard replacements -->
            <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
                    BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                    BorderThickness="1" CornerRadius="8" Padding="16">
                <StackPanel Spacing="12">
                    <TextBlock Text="Misheard replacements" Style="{ThemeResource BodyStrongTextBlockStyle}" />
                    <TextBlock Text="When the left text is heard, it is replaced with the right text."
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    <Grid ColumnDefinitions="*,*,Auto" ColumnSpacing="8">
                        <TextBox x:Name="NewWrongBox" AutomationProperties.AutomationId="CorrectionsNewWrongTextBox"  PlaceholderText="wrong (heard)" />
                        <TextBox x:Name="NewRightBox" AutomationProperties.AutomationId="CorrectionsNewRightTextBox"  Grid.Column="1" PlaceholderText="right (correct)" />
                        <Button x:Name="AddReplacementButton" AutomationProperties.AutomationId="CorrectionsAddReplacementButton" Grid.Column="2" Content="Add" Click="OnAddReplacement" Style="{StaticResource AccentButtonStyle}" />
                    </Grid>
                    <TextBlock x:Name="ReplacementsError"
                               AutomationProperties.AutomationId="CorrectionsReplacementsErrorLabel"
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource SystemFillColorCriticalBrush}"
                               TextWrapping="Wrap" />
                    <ListView x:Name="ReplacementsList" AutomationProperties.AutomationId="CorrectionsReplacementsList" SelectionMode="None">
                        <ListView.ItemTemplate>
                            <DataTemplate>
                                <Grid ColumnDefinitions="*,*,Auto" Padding="0,4" ColumnSpacing="8">
                                    <TextBlock Text="{Binding Wrong}" VerticalAlignment="Center" />
                                    <TextBlock Grid.Column="1" Text="{Binding Right}" VerticalAlignment="Center" />
                                    <Button Grid.Column="2" Content="Remove" Click="OnRemoveReplacement" Tag="{Binding}" />
                                </Grid>
                            </DataTemplate>
                        </ListView.ItemTemplate>
                    </ListView>
                </StackPanel>
            </Border>
```

(The error label follows the add row because it reports validation of those inputs; keeping them adjacent is the usable pairing. Do NOT touch the "Preferred transcriptions" card above this one.)

- [ ] **Step 2: Verify the diff is a pure reorder**

```bash
cd /home/dan/code/winpepper/.worktrees/misheard-newest-first
git diff src/Winpepper.App/Views/CorrectionsPage.xaml
grep -c 'AutomationProperties.AutomationId="Corrections' src/Winpepper.App/Views/CorrectionsPage.xaml
```

Expected: the diff shows only moved lines within the misheard card (no attribute changed, no element added/removed; `x:Name`s `NewWrongBox`/`NewRightBox`/`AddReplacementButton`/`ReplacementsError`/`ReplacementsList` all still present exactly once); the grep count is unchanged from before the edit (run it against `git show HEAD:src/Winpepper.App/Views/CorrectionsPage.xaml | grep -c ...` to compare — the file has both cards' IDs, and the count must match).

- [ ] **Step 3: Extend the manual smoke line**

In `docs/manual-test.md` (line 145 as of `c71a40b` — locate the line containing `Corrections tab:`), replace the sentence

```
Corrections tab: add a preferred ("ChatGPT"), then a duplicate (see error). Add a replacement ("chat gbt" → "ChatGPT"). Reload — entries persist.
```

with

```
Corrections tab: add a preferred ("ChatGPT"), then a duplicate (see error). In Misheard replacements the add fields sit ABOVE the list: add a replacement ("chat gbt" → "ChatGPT") — it appears at the TOP of the list; add another ("ann thropic" → "Anthropic") — it appears above "chat gbt". Reload — entries persist, still newest-first.
```

(Preserve any list marker/prefix the line has; change only this sentence.)

- [ ] **Step 4: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0 + `LINUX SUITE: GREEN` (mandatory before every commit, including XAML-only edits).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/CorrectionsPage.xaml docs/manual-test.md
git commit -m "$(cat <<'EOF'
feat(app): move misheard add-entry row above the replacements list

Pure element reorder inside the Misheard replacements card: add fields and
their validation label now sit above the ListView, so new entries appear
directly under where they were typed (list itself is newest-first via the VM).
Automation IDs, x:Names, and handlers unchanged.

- Winpepper.App: CorrectionsPage.xaml card child order
- docs: manual-test corrections smoke line covers layout + newest-first

Verified: linux-tests.sh GREEN; XAML compile-verified by windows-gate.sh before push.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: Windows gate + evidence

**Files:**
- Modify: `docs/plans/2026-08-04-misheard-newest-first.md` (append gate evidence)

**Interfaces:**
- Consumes: all previous tasks' commits on `feat/misheard-newest-first`.
- Produces: the pre-push proof that the WinUI half (the Task 3 XAML) compiles and the full 12-project/TFM suite is green on Windows, recorded in this plan.

- [ ] **Step 1: Run the Windows gate**

```bash
cd /home/dan/code/winpepper/.worktrees/misheard-newest-first
./scripts/windows-gate.sh
```

(Runs from WSL against the Windows host; allow a 3600s timeout; never run concurrently with `linux-tests.sh`.) Expected: exit 0 + `GATE: GREEN`. This is what compile-verifies `CorrectionsPage.xaml` — a XAML typo from Task 3 fails HERE, so on failure read the log under `artifacts/windows-gate/`, fix, re-run the Linux suite, amend/commit the fix, and re-run the gate.

- [ ] **Step 2: Record the evidence in this plan and commit**

Append to the END of `docs/plans/2026-08-04-misheard-newest-first.md`:

```markdown

---

## Gate evidence

- `./scripts/linux-tests.sh` — LINUX SUITE: GREEN (run before each of the 3 implementation commits)
- `./scripts/windows-gate.sh` — GATE: GREEN on <date from `date +%F`> (compile-verifies CorrectionsPage.xaml + full 12 project/TFM runs)
```

(Fill in the real date; if a gate did not pass, write what actually happened instead — evidence must be honest.) Then:

```bash
git add docs/plans/2026-08-04-misheard-newest-first.md
git commit -m "$(cat <<'EOF'
docs(plans): gates — linux suite + windows gate green for misheard newest-first

Verified: linux-tests.sh GREEN; windows-gate.sh GREEN.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Windows Smoke Test Checklist (manual, on the Windows host)

Per the repo's humble-object rule, the rendered WinUI layout is verified by named manual items (performed by the user at next Windows launch), backed by the automated proxies above (VM ordering tests render-order-equivalent because the ListView binds the collection verbatim; windows-gate compile).

1. Open Settings → Corrections. In the **Misheard replacements** card, the "wrong (heard)" / "right (correct)" boxes and the **Add** button sit ABOVE the entries list. The **Preferred transcriptions** card is unchanged.
2. Add `chat gbt` → `ChatGPT`, then `ann thropic` → `Anthropic`. Each new entry appears at the TOP of the list; final order top-to-bottom: `ann thropic`, `chat gbt`.
3. Enter a replacement with an empty right side and press Add: the validation error text appears directly UNDER the add row (above the list); the boxes keep their text.
4. Remove `ann thropic` via its Remove button: it disappears; `chat gbt` remains.
5. Re-add `ann thropic` → `Anthropic`, then relaunch the app: the list still shows `ann thropic` above `chat gbt`, and `%LOCALAPPDATA%\winpepper\corrections.json` has keys in oldest-first order (`chat gbt` before `ann thropic`).
6. (Learning path, unchanged behavior) An entry learned via the post-paste toast appears at the TOP of the list after the next app relaunch.

---

## Self-review — spec coverage map

| Spec requirement | Covering task(s) | Production proof |
|---|---|---|
| Add-entry field appears at the top of the misheard replacements section | Task 3 (XAML reorder) | Windows gate compile (Task 4) + Smoke items 1, 3 |
| Existing entries listed newest-to-oldest (most recently added first) | Task 1 (VM ordering) + Task 2 (round-trip incl. relaunch re-seed) | Linux tests against production types; ListView renders the collection verbatim (binding unchanged); Smoke items 2, 5 |
| Order survives restart / persistence intact | Task 1 (`Persist` reversal) + Task 2 (fresh-store reload + re-seed) | Round-trip test on a real file; Smoke item 5 |
| No collateral behavior change (prompt hints, custom_spelling, replacement engine, learning writer, file format) | Global Constraints + Task 1 `Persist_Writes_Replacements_OldestFirst` + Task 2 disk-order assertion | Full Linux suite green each task; disk order byte-identical |

No stubs, mocks standing in for production behavior, or deferred requirements: all tests use the real `CorrectionsViewModel`, `CorrectionsWiring`, and `CorrectionStore` against real temp files; the only non-automated surface (WinUI rendering) follows the repo's standard named-smoke-item treatment and is compile-verified by the Windows gate. Type consistency checked: `ReplacementEntry.Wrong/.Right`, ctor signature, `CorrectionsData.Replacements` usage match across all tasks. No unresolved coverage gaps.
