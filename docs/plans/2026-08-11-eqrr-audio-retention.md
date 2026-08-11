# User-Configurable Audio History Retention Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** Users can control whether Winpepper saves dictation audio at all, how many dictations are kept, and for how long (including "keep forever") — with a privacy-worded section on the History page — while defaults (on / 100 entries / 30 days) preserve today's behavior exactly.

**Architecture:** Three new persisted `AppSettings` fields feed a pure-managed `HistoryRetentionPolicy` consumed by `HistoryStore` (constructor-injected provider seam, live per prune) and `HistoryArchiver` (audio on/off seam). A new public `HistoryStore.Prune()` reapplies policy on settings save. A pure-managed `HistoryRetentionViewModel` owns the settings writes; the History page hosts the controls in a new card. Two silent-drop archive sites in `PipelineHost` skip archiving when audio storage is off.

**Tech Stack:** C# / .NET 9, WinUI 3 XAML (App-side, Windows-only), xUnit v3 in-process runner, Shouldly.

## Global Constraints

- Build test projects with `-c Release` and run via `dotnet exec <built test dll>` — **never `dotnet test`** (VSTest host unreliable). Full Linux suite: `./scripts/linux-tests.sh` from the worktree.
- SDK: .NET 9 provisioned at `/home/dan/code/winpepper/.dotnet` (gitignored). Every test step re-exports:
  `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"`.
- Focused xUnit v3 filters: `-class "<FullyQualified.ClassName>"` or `-method "<Namespace.Class.Method>"`.
- **`Winpepper.App` does not compile on Linux** (verified this run: XAML compiler `WMC0621: Cannot resolve GenXbf.dll`). App-side edits (`HistoryServices.cs`, `PipelineHost.cs`, `HistoryPage.xaml[.cs]`) are verified by `./scripts/windows-gate.sh` (~12–25 min; exit 0 / `GATE: GREEN`) — run it once after Task 4 lands and again whenever code changes afterward. Keep App edits mechanical and show exact before/after in briefs.
- Defaults must preserve current behavior: store-audio **on**, **100** entries, **30** days (`HistoryStore.cs:18` is now 100).
- Never push, merge to main, create PRs, install MSIs, launch/kill Winpepper.exe, or write to `%LOCALAPPDATA%\winpepper`. Never commit `.kata.toml`, `.opencode/`, `.dotnet/`, model/corpus/artifact files. Commits on this branch are authorized; stop short of merge/push.
- Diagnostics bundles must keep excluding `*.wav` regardless of these settings (`DiagnosticsBundleBuilder.cs:31` — untouched by this plan).
- Eval tooling reads history WAVs and must keep working: `Winpepper.History/Lab/*` rerun services and `scripts/asr-eval-corpus` (its exporter already skips missing WAVs, `Program.cs:91-96`).
- Keep changes minimal; other agents work other katas in parallel worktrees.
- Full suite green before every commit: on Linux `./scripts/linux-tests.sh`; Windows gate once after Task 4 (it is the App compile+test authority).

## Requirements

- **R1 — Store-audio on/off:** a persisted setting gates whether dictation audio is saved. Off ⇒ text-only history entries (transcripts/timings kept, no WAV). Silent-drop sessions archive nothing when off (no audio = nothing recoverable) and the UI text says so. Design decision documented in this plan (§ Design decisions).
- **R2 — Configurable limits:** max entries and max age configurable; max age supports "keep forever". `HistoryStore`/`HistoryArchiver` honor them.
- **R3 — Prune-on-save:** tightened limits take effect immediately on save, not only on next append.
- **R4 — Settings UI:** History-page section with clear privacy wording, plus disk-usage display and a "Delete all saved audio now" button (issue's optional item, included).
- **R5 — Invariants:** diagnostics `*.wav` exclusion preserved; Lab rerun services and corpus exporter unbroken; a WAV-less entry cannot crash the detail-page transcription rerun.
- **R6 — Defaults:** on / 100 / 30 days — existing users see no change; all pre-existing tests keep passing unmodified.
- **R7 — Evidence:** unit tests for off, custom caps, unlimited age, prune-on-save; Linux suite green; Windows gate GREEN (XAML touched).

## Design decisions (documented per the issue's scope item 1)

- **D1 — "Store audio = off" keeps text-only history.** Rationale: the voice recording is the privacy-sensitive artifact; transcripts are text the user already pasted elsewhere and remain useful (History browsing/copy, Lab cleanup rerun, corpus references). "Disable history entirely" rejected: it guts History/Lab value for a control framed around **voice recordings**. Cost, stated in the UI: silent-drop audio recovery is impossible with audio off; Lab **transcription** rerun and replay need audio.
- **D1a — silent-drop sessions archive nothing when audio is off.** Their entries carry no transcript text; without a WAV they would be noise rows. Gated at the two silent-drop PipelineHost call sites (settings are already read live there).
- **D2 — unlimited age is `int? HistoryMaxAgeDays = null`.** Nullable int round-trips cleanly through the existing JSON settings store; no sentinel ambiguity. Default `30`.
- **D3 — policy is read live through a provider seam** (`Func<HistoryRetentionPolicy>`), so a settings save applies to the very next `Append` without rebuilding the singleton store; `Prune()` applies it to existing entries on save (R3).
- **D4 — "Delete all saved audio now" deletes WAVs only**; entries and their transcripts stay, `WavRelativePath` cleared. Coherent with D1's split.

- **D5 — destructive ops use a strict load and a truthful delete protocol** (finder F2–F4). `Prune()` and `DeleteAllAudio()` read the index through a strict loader that bails (returns 0, writes nothing) when the index is corrupt or unreadable — a lenient "empty on error" read must never become the base of a destructive rewrite. Deletes are tracked per WAV: an entry is dropped / its `WavRelativePath` cleared only when its WAV is gone-or-absent; a failed delete keeps the entry (retryable on the next pass) and counts report what was ACTUALLY deleted. `DeleteAllAudio()` sweeps `*.wav` recursively under the history root, so orphan WAVs (archiver append-failure, past failed deletes) are covered instead of surviving invisibly.

- **D6 — delete-path containment** (finder F5). Every WAV-delete path is normalized full-path and rejected unless it stays under the history root (no absolute paths, no `..` escapes) — guard lives in the store's shared delete helper so Append/Delete/Prune/DeleteAllAudio all inherit it.

- **D7 — supported cap bound is 10,000** (finder F6). The index is one JSON file loaded/sorted wholesale; 10k entries ≈ 10 MB worst case — acceptable for an opt-in eval corpus; the original draft's 100,000 was unvalidated. Policy and UI clamp to [1, 10000].

- **D8 — commit ordering is flush → prune → refresh** (finder F1). The retention VM awaits the durable settings flush and only then prunes and refreshes the disk display, so prune-on-save can never act on pre-commit settings. Live per-dictation reads stay disk-truth (the established `() => store.Load()` pattern).

- **D9 — the archiver samples its audio gate once per call** (finder F7). One `Archive` reads the gate Func a single time and uses that value for both the file write and the entry's path, so a mid-archive flip can't orphan a WAV or dangle a reference. The PipelineHost silent-drop guard uses the same session's earlier settings snapshot; a flip inside that seconds-scale window can only lose one silent-drop recovery entry or produce one text-only empty entry — never a privacy leak. Accepted and documented.

---

### Task 1: Retention settings fields + policy + store seams (pure-managed core)

**Requirements served:** R1 (fields), R2, R3, R6 (partly), R7 (Linux tests)

**Behavior:**
- `AppSettings` gains `HistoryStoreAudioEnabled` (bool, default `true`), `HistoryMaxEntries` (int, default `100`), `HistoryMaxAgeDays` (int?, default `30`; `null` = keep forever).
- New `HistoryRetentionPolicy` maps settings → store policy with clamping (entries to [1, 10000]; age to ≥ 1 day when non-null).
- `HistoryStore` prunes via an injected policy provider (defaults reproduce today's constants exactly), factors the two-tier prune into a shared helper used by both `Append` and new `public int Prune()`, and gains `DeleteAllAudio()` and `ComputeAudioDiskUsageBytes()` (recursive `*.wav` byte sum; 0 when root missing).
- `Append` with `MaxAgeDays == null` skips the age tier entirely (count cap still applies).
- Destructive ops follow D5/D6:
  - `Prune()` loads via a strict loader (`false` on corrupt/unreadable index → return 0, write nothing; missing index file = legitimately empty → proceed). It drops an entry only when its WAV (if any) is gone after its delete attempt; an entry whose WAV delete failed stays (retried on the next pass). Returns the number of dropped entries. `Append` keeps today's lenient load behavior (out-of-scope finding F2a records the pre-existing overwrite hazard).
  - `DeleteAllAudio()` sweeps `*.wav` recursively under the root (tracked per file) — the privacy intent is file deletion, which needs no index — and then, ONLY when the strict loader reads the index cleanly, rewrites `WavRelativePath=""` on entries whose WAV is gone-or-absent and saves once. Corrupt/unreadable index → WAVs still swept, refs untouched (a clean retry on the next pass), and the returned count still reflects files actually deleted. Safe re-run: a second call mops up leftovers from a failed first call.
  - `TryDeleteWav` rejects any relative path whose normalized full path leaves the root (absolute or `..`) — the guard is inside this shared helper so every caller inherits it.
  - Save ordering mirrors existing `Append` (deletes first, atomic index save second); a save failure after deletes leaves the index pointing at possibly-deleted WAVs — the same exposure today's `Append` has, accepted as precedent-consistent (not a new risk).

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (add 3 fields after `PrewarmMicEnabled`-era block, with comments noting pre-2026-08 settings files keep defaults)
- Create: `src/Winpepper.History/HistoryRetentionPolicy.cs`
- Modify: `src/Winpepper.History/HistoryStore.cs` (keep `MaxEntries`/`MaxAge` as fallback defaults so existing tests/docs keep meaning)
- Test: `tests/Winpepper.History.Tests/HistoryRetentionPolicyTests.cs` (new), `tests/Winpepper.History.Tests/HistoryStoreTests.cs` (extend), `tests/Winpepper.Core.Tests/Settings/HistoryRetentionSettingsPersistenceTests.cs` (new)

**Interfaces:**
- Consumes: `Winpepper.Core.Settings.AppSettings`, existing `HistoryStore` ctor `(string root)` / internal `(string root, Func<DateTime> utcNow)`.
- Produces:
  - `AppSettings.HistoryStoreAudioEnabled : bool = true`
  - `AppSettings.HistoryMaxEntries : int = 100`
  - `AppSettings.HistoryMaxAgeDays : int? = 30`
  - `public sealed record HistoryRetentionPolicy { int MaxEntries /*=100*/; int? MaxAgeDays /*=30*/; TimeSpan? MaxAge { get; } /*null when MaxAgeDays null*/; static HistoryRetentionPolicy Default { get; } static HistoryRetentionPolicy FromSettings(AppSettings) }` in `Winpepper.History`
  - `public HistoryStore(string root, Func<HistoryRetentionPolicy> policyProvider)`
  - `public int HistoryStore.Prune()` (dropped-entry count; 0 without writing on strict-load failure)
  - `public int HistoryStore.DeleteAllAudio()` (WAV-file count actually deleted; 0 without writing on strict-load failure)
  - `public long HistoryStore.ComputeAudioDiskUsageBytes()`

**Test cases:**
- Existing 52 History tests pass unmodified (R6 regression proof).
- Policy `MaxEntries = 3`: append 5 with real WAVs → 3 newest kept, 2 oldest WAVs deleted.
- Policy `MaxAgeDays = 7`: 8-day-old entry pruned on append, 5-day kept.
- Policy `MaxAgeDays = null`: 400-day-old entry survives appends; count cap at `MaxEntries` still enforced.
- `Prune()`: seed 5 entries (index written, no append afterwards) then `Prune()` with MaxEntries=2 → 2 remain, 3 WAVs gone, returns 3. Also age variant.
- Prune with `null` age keeps by age but drops by count.
- `Prune()` on a corrupt `index.json` → returns 0, file bytes unchanged.
- `DeleteAllAudio()`: 3 entries with WAVs → all WAVs gone, 3 entries kept with empty `WavRelativePath`, returns 3; second call returns 0 (idempotent). Include an orphan `orphan.wav` (no entry) → also deleted and counted.
- `DeleteAllAudio()` on corrupt index → WAVs still deleted and counted (sweep needs no index), index file untouched byte-for-byte.
- Traversal: entry with `WavRelativePath = "../evil.wav"` and a fabricated target outside the root → the outside file survives `Append`-prune/`DeleteAllAudio` passes (guard refuses the escape).
- `ComputeAudioDiskUsageBytes()`: two fabricated WAVs (e.g. 10 + 20 bytes) + non-WAV file → returns exact WAV byte sum; empty/missing root → 0.
- `FromSettings`: defaults → (100, 30); custom (5, 7) round-trips; `HistoryMaxAgeDays = null` → `MaxAge == null`; clamping `HistoryMaxEntries = 0` → 1 and `= 50000` → 10000, and `HistoryMaxAgeDays = 0` → 1 day.
- Persistence chain-pin (mirrors `StreamingSettingPersistenceTests`): `QueueAndFlushAsync(s => s with { HistoryStoreAudioEnabled = false, HistoryMaxEntries = 7, HistoryMaxAgeDays = null })` through a real `DebouncedSettingsWriter` + `SettingsStore` on a temp path → reload shows all three; neighbors untouched.
- Pre-2026-08 settings file (JSON lacking the 3 fields) → `SettingsStore.Load()` yields the defaults (true/100/30).

- [ ] **Step 1: Write the failing behavioral tests**

  `HistoryRetentionPolicyTests`: mapping/clamping cases above. New tests in `HistoryStoreTests`: custom caps, unlimited age, Prune, DeleteAllAudio, disk usage (fabricate WAVs with `File.WriteAllText`, store built with pinned `utcNow` seam where age matters). `HistoryRetentionSettingsPersistenceTests`: real writer round-trip + missing-fields defaults.

- [ ] **Step 2: Run the tests and verify the intended failures**

  Run (from worktree, after exports):
  ```
  dotnet build tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj -c Release
  dotnet exec tests/Winpepper.History.Tests/bin/Release/net9.0/Winpepper.History.Tests.dll
  dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
  dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -class "Winpepper.Core.Tests.Settings.HistoryRetentionSettingsPersistenceTests"
  ```
  Expected: FAIL — new types/properties/methods do not exist (compile error naming `HistoryRetentionPolicy` / `HistoryMaxEntries` / `Prune` / etc. is the intended missing-behavior failure; do not progress on syntax accidents).

- [ ] **Step 3: Add the minimal production implementation**

  `AppSettings`: add the three init-properties with defaults + comments. `HistoryRetentionPolicy`: record with `MaxAge` derived property and clamping `FromSettings`. `HistoryStore`: chained ctors ending in a private `(root, policyProvider, utcNow)`; `_policyProvider` field; `Append` prunes through extracted private `ApplyPolicy(IReadOnlyList<HistoryEntry>) → (keep, dropped)` honoring null age; `Prune()` / `DeleteAllAudio()` / `ComputeAudioDiskUsageBytes()` lock `_gate` like existing members. Update the stale `HistoryArchiver`-adjacent doc comment ("Pruning to 50") to policy wording while touching that area only if the archiver file is edited in Task 2 (leave for Task 2).

- [ ] **Step 4: Run the focused tests**

  Same four commands as Step 2. Expected: PASS (all new + existing History = 52+new; Core suite focused class PASS).

- [ ] **Step 5: Refactor while green**

  Only naming/duplication inside the touched methods. No new abstractions.

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`
  Expected: `LINUX SUITE: GREEN`, grand total ≥ 1854 + new test count.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.Core/Settings/AppSettings.cs src/Winpepper.History/HistoryRetentionPolicy.cs src/Winpepper.History/HistoryStore.cs tests/Winpepper.History.Tests tests/Winpepper.Core.Tests/Settings/HistoryRetentionSettingsPersistenceTests.cs
  git commit -m "feat(history): configurable retention policy in HistoryStore + AppSettings fields"
  ```

---

### Task 2: Archiver audio gate + app wiring (HistoryServices + silent-drop sites)

**Requirements served:** R1 (+ D1a), R5 (archiver side), R6, R7

**Behavior:**
- `HistoryArchiver` gains `Func<bool>? storeAudio` ctor seam (default on). Each `Archive` call samples the gate **exactly once** into a local (D9) and uses that value for both the file write and the entry's `WavRelativePath`. When off: no WAV written, entry persisted with `WavRelativePath = ""`, `DurationMs` still derived from samples.
- `HistoryServices` wires live settings: store policy provider + archiver audio gate read `Func<AppSettings>`; ctor gains that parameter.
- The two silent-drop sites in `PipelineHost.cs` skip `Archive` entirely when `HistoryStoreAudioEnabled` is false in that session's already-read settings snapshot (`settingsAtStop` / `settingsAtStop2`); the two success sites are unchanged (text-only entries still archived when off). Residual flip-skew accepted per D9.
- Defaults ⇒ byte-identical behavior to today (archiver tests from Task 1 baseline keep passing).

**Files:**
- Modify: `src/Winpepper.History/HistoryArchiver.cs` (also fix its stale "Pruning to 50" doc comment → policy wording)
- Modify: `src/Winpepper.App/Services/HistoryServices.cs` (ctor signature + wiring) — Windows-compile only
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (2 `if` guards) — Windows-compile only
- Test: `tests/Winpepper.History.Tests/HistoryArchiverTests.cs` (extend)

**Interfaces:**
- Consumes: `HistoryRetentionPolicy.FromSettings` (Task 1), `AppSettings.HistoryStoreAudioEnabled` (Task 1), `() => store.Load()` pattern already passed to `PipelineHost` (`AppShell.cs`).
- Produces:
  - `HistoryArchiver(HistoryStore store, Func<DateTime>? nowUtc = null, Func<bool>? storeAudio = null)`
  - `HistoryServices(string historyRoot, ITranscriptionRerunService transcriptionRerun, Func<Winpepper.Core.Settings.AppSettings> settingsProvider)`

**Test cases:**
- Gate off: no WAV file appears in root, entry appended with empty `WavRelativePath`, `DurationMs` correct for 16000 samples (1000 ms).
- Gate flips between calls (Func re-read per call): first archive writes WAV, flip returns false, second archive WAV-less → proves live read.
- Single sampling (D9): counting `storeAudio` fake invoked exactly **once** per `Archive` call.
- Gate default: existing archiver tests pass (constructor back-compat — old two-arg call sites unchanged).

- [ ] **Step 1: Write the failing behavioral tests**

  New `HistoryArchiverTests`: `Archive_StoreAudioOff_WritesNoWav_PersistsTextOnlyEntry`, `Archive_StoreAudioGate_ReadLive_PerCall`, and `Archive_StoreAudioGate_SampledOncePerCall` (counting fake).

- [ ] **Step 2: Run the tests and verify the intended failures**

  ```
  dotnet build tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj -c Release
  dotnet exec tests/Winpepper.History.Tests/bin/Release/net9.0/Winpepper.History.Tests.dll -class "Winpepper.History.Tests.HistoryArchiverTests"
  ```
  Expected: FAIL — no `storeAudio` ctor parameter exists (compile error = intended failure).

- [ ] **Step 3: Add the minimal production implementation**

  HistoryArchiver: add optional third parameter, `_storeAudio = storeAudio ?? (() => true)`; in `Archive`, sample ONCE (`var keepAudio = _storeAudio();`), wrap the `WavWriter.WriteMono16kInt16` call in `if (keepAudio)`, and set `WavRelativePath = keepAudio ? relative : ""`. Fix the class doc comment's stale "Pruning to 50" line. HistoryServices: new ctor param; `Store = new HistoryStore(historyRoot, () => HistoryRetentionPolicy.FromSettings(settingsProvider())); Archiver = new HistoryArchiver(Store, storeAudio: () => settingsProvider().HistoryStoreAudioEnabled);`. PipelineHost: wrap the two silent-drop `_archiver.Archive(...)` calls in `if (settingsAtStop.HistoryStoreAudioEnabled)` / `if (settingsAtStop2.HistoryStoreAudioEnabled)` (locals already read at those points). Exact before/after snippets in the task brief.

- [ ] **Step 4: Run the focused tests**

  Same commands as Step 2. Expected: PASS (all archiver tests incl. pre-existing).

- [ ] **Step 5: Refactor while green**

  None needed beyond the comment fix (change is deliberately minimal).

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`
  Expected: `LINUX SUITE: GREEN`. Note in the implementer report that `HistoryServices.cs`/`PipelineHost.cs` do not compile on Linux (App excluded); their compile verification is deferred to the Windows gate in Task 4 — App edits here are three-mechanical-line scale, review-gated.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.History/HistoryArchiver.cs src/Winpepper.App/Services/HistoryServices.cs src/Winpepper.App/Hosting/PipelineHost.cs tests/Winpepper.History.Tests/HistoryArchiverTests.cs
  git commit -m "feat(history): store-audio on/off gate in HistoryArchiver; wire live settings; skip silent-drop archive when off"
  ```

---

### Task 3: Retention view-model + WAV-less detail-page rerun guard

**Requirements served:** R3 (prune-on-save trigger), R4 (VM half), R5 (no crash), R6

**Behavior:**
- New pure-managed `HistoryRetentionViewModel` (in `Winpepper.History/ViewModels`): binds `StoreAudioEnabled` (bool), `MaxEntries` (double for NumberBox, clamped ≥ 1), `MaxAgeDays` (double, clamped ≥ 1), `KeepForever` (bool; true ⇒ persists `HistoryMaxAgeDays = null`), exposes `DiskUsageDisplay` (e.g. `Audio on disk: 12.4 MB across 34 recording(s)`), and follows D8 ordering on every change: the setter kicks off a commit chain that **awaits** `ISettingsWriter.QueueAndFlushAsync` and only then calls `HistoryStore.Prune()` and refreshes the disk display — prune-on-save can never act on pre-commit settings.
- `DeleteAllAudioAsync()` calls `HistoryStore.DeleteAllAudio()`, refreshes usage, raises change notifications; returns the deleted count for the page to echo.
- `HistoryDetailViewModel` transcription Runner: short-circuits on empty `WavRelativePath` with an inline message, and catches `FileNotFoundException` with an inline message (same inline-surface precedent as the existing `InvalidOperationException` catch) — WAV-less entries can no longer crash the page's async-void handler.

**Files:**
- Create: `src/Winpepper.History/ViewModels/HistoryRetentionViewModel.cs`
- Modify: `src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs` (Runner only)
- Test: `tests/Winpepper.History.Tests/ViewModels/HistoryRetentionViewModelTests.cs` (new), extend existing detail-VM tests if present else add `tests/Winpepper.History.Tests/ViewModels/HistoryDetailViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1's `HistoryStore.Prune/DeleteAllAudio/ComputeAudioDiskUsageBytes`, `AppSettings` fields, `Winpepper.Core.Settings.ISettingsWriter`, fakes `FakeTranscriptionRerunService` (already throws `FileNotFoundException` for missing WAV) and `FakeCleanupRerunService`.
- Produces:
  - `public sealed class HistoryRetentionViewModel : INotifyPropertyChanged`
    - ctor `(AppSettings initial, HistoryStore store, ISettingsWriter writer)`
    - `bool StoreAudioEnabled`, `double MaxEntries`, `double MaxAgeDays`, `bool KeepForever` (setters commit via an ordered flush→prune→refresh chain per D8)
    - `string DiskUsageDisplay` (read-only, refreshed on commit/Refresh)
    - `void Refresh()` (reloads usage; cheap)
    - `Task<int> DeleteAllAudioAsync()`
  - `HistoryDetailViewModel` unchanged publicly (behavioral fix inside the Runner).

**Test cases:**
- Each setter writes exactly the right `AppSettings` mutation through the fake writer AND prunes only AFTER the flush completes: the fake writer's `QueueAndFlushAsync` is gated on a `TaskCompletionSource` the test completes explicitly — before completion the store is unpruned and the writer holds the mutation; after completion the prune has run (seed real temp store with 5 entries + WAVs, `MaxEntries = 2` → writer's `Current.HistoryMaxEntries == 2`, store holds 2, 3 WAVs gone).
- `KeepForever = true` persists `HistoryMaxAgeDays = null`; toggling back persists the numeric days.
- Clamps: `MaxEntries = 0`/NaN guarded (0 → commit as 1; NaN ignored), `MaxAgeDays = 0` → 1.
- `DiskUsageDisplay` reflects fabricated WAV bytes (assert contains the byte count; exact string format asserted loosely via contains, to not overfit).
- `DeleteAllAudioAsync` returns count, clears WAVs, keeps entries, refreshes usage.
- Detail VM with `WavRelativePath = ""` entry: `await vm.TranscriptionPanel.RunAsync(default)` → `RerunText` is the no-audio inline message; fake service's throw path (non-empty path, missing file → `FileNotFoundException` from `FakeTranscriptionRerunService`) → inline message, no exception escapes.

- [ ] **Step 1: Write the failing behavioral tests**

  New `HistoryRetentionViewModelTests` (cases above) and detail-VM WAV-less cases (use the Lab fakes; temp roots per test, dispose pattern from `HistoryStoreTests`).

- [ ] **Step 2: Run the tests and verify the intended failures**

  ```
  dotnet build tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj -c Release
  dotnet exec tests/Winpepper.History.Tests/bin/Release/net9.0/Winpepper.History.Tests.dll
  ```
  Expected: FAIL — `HistoryRetentionViewModel` does not exist; detail-VM missing-WAV test fails by the `FileNotFoundException` escaping `RunAsync` (that IS the crash being fixed).

- [ ] **Step 3: Add the minimal production implementation**

  New VM file (setter pattern mirrors `RecordingSettingsViewModel`, but the commit path is an ordered async chain per D8: setter stores the field, then `_ = CommitAndApplyAsync(mutator)` whose body awaits `_writer.QueueAndFlushAsync(mutator)` and only then runs `_store.Prune()` and refreshes the usage display — the prune can never act on pre-commit settings; NaN/clamp guards in setters). Detail VM: prepend the empty-path short-circuit and add `catch (FileNotFoundException)` in the transcription Runner.

- [ ] **Step 4: Run the focused tests**

  Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: Refactor while green**

  Keep `HistoryRetentionViewModel` symmetric with `RecordingSettingsViewModel` naming; no further changes.

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`
  Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.History/ViewModels/HistoryRetentionViewModel.cs src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs tests/Winpepper.History.Tests/ViewModels
  git commit -m "feat(history): retention settings view-model with prune-on-save; WAV-less detail rerun guard"
  ```

---

### Task 4: History page "Recordings" settings card (WinUI XAML)

**Requirements served:** R4 (UI), R1/D1a (UI callout), R5 (disk+delete affordances' safety: confirm dialog), R7 (Windows gate)

**Behavior:**
- History page gains a card above the list: store-audio ToggleSwitch (with the silent-drop recovery-loss callout), max-entries NumberBox, age NumberBox + "Keep forever" CheckBox, disk-usage TextBlock, and a confirm-dialoged "Delete all saved audio now" Button. Controls commit through `HistoryRetentionViewModel` (Task 3); after each commit the page refreshes the history list (a prune may have dropped rows).
- Pure UI wiring task: no new logic beyond event plumbing. No useful Red step exists for XAML wiring (no UI test harness in this repo; precedent: prior page work verified by XAML compile + review) — recorded as such per usual-test-driven-development's documentation/mechanical exception. Verification = Windows gate + reviewer inspection.

**Files:**
- Modify: `src/Winpepper.App/Views/HistoryPage.xaml` (add `Grid.Row`, insert card)
- Modify: `src/Winpepper.App/Views/HistoryPage.xaml.cs` (build VM, wire events, confirm dialog, refresh list)

**Interfaces:**
- Consumes: `HistoryRetentionViewModel` (Task 3), `App.Shell.HistoryServices.Store`, `App.Shell.SettingsStore.Load()` (freshest snapshot for VM init, precedent: VM needs `AppSettings initial`), `App.Shell.SettingsWriter`, existing card style from `RecordingPage.xaml` (`CardBackgroundFillColorDefaultBrush` borders, `ToggleSwitch`, caption TextBlocks), `ContentDialog` with `XamlRoot`.

**Test cases:**
- Windows gate compiles the new XAML and runs all test projects (authoritative check).
- x:Name'd controls: `StoreAudioToggle`, `MaxEntriesBox`, `MaxAgeBox`, `KeepForeverCheck`, `DiskUsageText`, `DeleteAllAudioButton` — code-behind references them only inside `#if WINDOWS` page partial (whole file is `#if WINDOWS` already).
- Reviewer check: every control event handler commits via the VM only (no direct store/settings mutation in the page).

- [ ] **Step 1: Write the failing behavioral test**

  None — mechanical XAML wiring with no repo UI-test harness; record the reason. Substitute check: XAML must compile on Windows (GenXbf runs there), i.e. the gate build is the executable check.

- [ ] **Step 2: (n/a — see Step 1)**

- [ ] **Step 3: Add the minimal production implementation**

  XAML: insert a `RowDefinition` (`Auto`) before the list row; add one `Border` card containing: `ToggleSwitch x:Name="StoreAudioToggle" Header="Save audio recordings of your dictations"`, a caption TextBlock stating "When off, Winpepper keeps transcripts and timings in history but saves no audio. Dictations dismissed as silent are not archived — without audio there is nothing to recover — and Lab replay/re-transcribe needs audio.", a NumberBox `x:Name="MaxEntriesBox"` (`Minimum=1`, `Maximum=10000`) with header "Keep at most this many dictations", a NumberBox `x:Name="MaxAgeBox"` (`Minimum=1`, `Maximum=36500`) with header "Delete dictations older than (days)", a CheckBox `x:Name="KeepForeverCheck" Content="Keep forever — never delete by age (for building an eval corpus)"`, a TextBlock `x:Name="DiskUsageText"`, and a Button `x:Name="DeleteAllAudioButton" Content="Delete all saved audio now"`. Code-behind: in `OnNavigatedTo` build `new HistoryRetentionViewModel(App.Shell.SettingsStore.Load(), services.Store, App.Shell.SettingsWriter)`, initialize control state from it, wire `Toggled`/`ValueChanged` (NaN-guarded)/`Checked`/`Unchecked`/`Click` to the VM, then `ViewModel.Refresh()` the list VM and re-read `DiskUsageText`. Delete button shows a `ContentDialog` ("Delete all saved audio? Recordings are deleted; transcripts are kept. This cannot be undone.", Primary="Delete", Close="Cancel") before calling `await vm.DeleteAllAudioAsync()`, and reports the returned count afterwards (e.g. "N recordings deleted." — with a "M could not be deleted (file in use); try again" suffix when entries remain).

- [ ] **Step 4: Run the Windows-side compile+test verification**

  Run (from worktree root, WSL): `./scripts/windows-gate.sh` with a 30-minute timeout.
  Expected: exit 0, final line `GATE: GREEN` (App + all 9 test projects build, 12 runs pass on the Windows host).

- [ ] **Step 5: Refactor while green**

  Only if the gate flags XAML issues; keep to the minimal fix.

- [ ] **Step 6: Run broader verification**

  Already done by Step 4 (gate is the broadest suite). Additionally re-run `./scripts/linux-tests.sh` to confirm the commit's Linux state.
  Expected: both GREEN.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.App/Views/HistoryPage.xaml src/Winpepper.App/Views/HistoryPage.xaml.cs
  git commit -m "feat(app): recordings privacy card on History page (store-audio toggle, limits, disk usage, delete-all)"
  ```

---

## Self-review record

- **Spec coverage:** R1→T2(+T4 UI), R2→T1, R3→T1(`Prune`)+T3(vm chain), R4→T3+T4, R5→T2(archiver gate)+T3(rerun guard), R6→T1 defaults + unmodified existing tests checked every task, R7→per-task Linux runs + T4 gate. Every task names its Requirements; none serves zero.
- **No silent deferrals:** no mocks standing in for production outcomes; the only deferred-verification surface is `Winpepper.App` compile/runtime (structurally Linux-unbuildable) — covered by the Windows gate in T4 and called out in T2's Step 6.
- **Interface consistency:** T1 produces `HistoryRetentionPolicy`/`Prune`/`DeleteAllAudio`/`ComputeAudioDiskUsageBytes` consumed by T2/T3; T3's VM ctor is consumed by T4 exactly as written; AppSettings property names spelled identically everywhere (`HistoryStoreAudioEnabled`, `HistoryMaxEntries`, `HistoryMaxAgeDays`).
- **Executable tests:** each Red step references members that do not exist yet (compile failure = intended failure) or, for T3's detail-VM case, the currently-crashing behavior; each Green command names the exact test assembly/class.
- **Placeholder scan:** no TBD/TODO/"handle edge cases"; UI wording is literal in T4; commit messages literal.
- **Operational completeness:** disk-usage refresh after delete-all; confirm dialog before destructive delete; NaN guards for NumberBox; doc-comment staleness ("Pruning to 50") fixed in T2 where the file is already touched.
- **Task size:** T1/T3 pure-managed (Linux-verifiable), T2 small mixed, T4 pure UI; each reviewable independently.
- **Load-bearing amendments (Stage 2):** finder round 1 surfaced 7 confirmed gaps (commit ordering F1, strict load F2, truthful deletes F3, orphan sweep F4, path containment F5, cap bound F6, single sampling F7) — encoded as decisions D5–D9 and Task 1–4 amendments; pre-existing hazards recorded as out-of-scope findings (Append's lenient corrupt-index overwrite; `SettingsStore._lastGood` staleness; hand-edited duplicate-path cross-delete). Changed tasks re-checked against all eight self-review items above: coverage unchanged, interfaces extended consistently (`Prune`/`DeleteAllAudio` return semantics now explicit), every new behavior has a named failing-first test.
