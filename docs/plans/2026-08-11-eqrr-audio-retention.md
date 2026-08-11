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
- **D1a — silent-drop sessions archive nothing when audio is off, and the archiver owns that decision.** `HistoryArchiveInput` gains `IsSilentDrop` (default `false`); the two PipelineHost silent-drop sites set it (one-line addition to each existing initializer — no `if` wraps, no settings reads in the App layer). `Archive` rule: when the sampled store-audio gate is off AND `IsSilentDrop`, skip the write+append entirely (returns `null`; all four call sites ignore the return). This keeps the decision in the pure-managed, Linux-tested archiver instead of an untestable App-side guard. Successful dictations whose transcription merely came back empty are still archived (owner's always-archive decision stands).
- **D2 — unlimited age is `int? HistoryMaxAgeDays = null`.** Nullable int round-trips cleanly through the existing JSON settings store; no sentinel ambiguity. Default `30`.
- **D3 — policy is read live through a provider seam** (`Func<HistoryRetentionPolicy>`), so a settings save applies to the very next `Append` without rebuilding the singleton store; `Prune()` applies it to existing entries on save (R3).
- **D4 — "Delete all saved audio now" deletes WAVs only**; entries and their transcripts stay, `WavRelativePath` cleared. Coherent with D1's split.

- **D5 — destructive ops use a strict load or no load at all, a truthful delete protocol, and never throw for index/enumeration IO failures** (finder F2–F4, reviewer r1#5/r1#7/r2#2/r2#4).
  - Strict loader: corrupt `index.json` (JsonException) or unreadable (IOException/UnauthorizedAccessException) → `false`; missing file → true+empty. Catching UnauthorizedAccess too mirrors `SettingsStore.TryLoadCurrent`.
  - `Prune()` is index-driven ⇒ it requires a clean read: strict-load `false` → returns `{ DroppedCount=0, IndexSaveFailed=false }` having written nothing. It drops an entry only when its WAV (if any) is gone after its delete attempt; an entry whose WAV delete failed stays (retried on the next pass). A failed index save is CAUGHT and reported as `{ DroppedCount=<intended>, IndexSaveFailed=true }` — never thrown. (`Append` keeps its existing lenient load and throw-on-save-failure behavior unchanged; the pre-existing hazards are recorded as out-of-scope findings.)
  - `DeleteAllAudio()`'s payload action is sweeping `*.wav` recursively under the history root and needs NO index: it sweeps always (even with a corrupt index — the privacy intent is file deletion), tracks per-file success/failure, and THEN, only when the strict loader reads the index cleanly, clears `WavRelativePath` on entries whose file is gone-or-absent and saves once. A failed index save (or a deferred-iteration enumeration failure, e.g. an inaccessible subtree) is CAUGHT and reported — never thrown into the UI chain: `DeleteAllAudio` returns `HistoryAudioCleanupResult { DeletedCount, FailedCount, IndexSaveFailed, EnumerationFailed }` (`EnumerationFailed=true` means the sweep provably incomplete — distinct from an empty folder); `Prune` returns `HistoryPruneResult { DroppedCount, IndexSaveFailed }`. Re-runnable: a second call mops up what a failed first call kept.

- **D6 — delete-path containment, physical as well as lexical** (finder F5, reviewer #6). Every WAV the store deletes/sweeps must be a real file physically under the history root: lexical normalization + root-prefix check in the shared delete helper (rejects absolute or `..` paths), recursive enumeration with `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }` so junctions/symlinks are neither traversed nor matched, and the per-file delete helper refuses reparse-point files. Inheritance: sweep, `TryDeleteWav`, `Prune`, `DeleteAllAudio`.

- **D7 — supported cap bound is 10,000** (finder F6). The index is one JSON file loaded/sorted wholesale; 10k entries ≈ 10 MB worst case — acceptable for an opt-in eval corpus; the original draft's 100,000 was unvalidated. Policy and UI clamp max entries to [1, 10000]; max age days clamp to [1, 36500] (reviewer #1: unclamped `int`, e.g. hand-edited `int.MaxValue`, overflows `TimeSpan.FromDays` and would break every subsequent `Append`).

- **D8 — commit ordering with an explicit outcome: publish → flush → prune-with-committed-policy → refresh** (finder F1, reviewer r1#3, r2#1). The runtime gate is NOT the settings file: a persisted settings write is fallible and eventually-consistent, but the privacy toggle's effective state must be synchronous. So the VM, BEFORE starting any disk work, synchronously publishes the new retention values to an in-memory slot (D12) that both the archiver's audio gate and the store's policy provider read. Then the chain awaits the writer — and an awaited `QueueAndFlushAsync` is NOT proof of persistence (its flush returns normally when the current settings file is unreadable, requeuing the mutation; a failed Save likewise requeues). Therefore: (a) `ISettingsWriter` gains `Task<bool> TryQueueAndFlushAsync(mutator)` reporting whether the change actually landed on disk (default interface member wraps the old method and assumes success; `DebouncedSettingsWriter` overrides with real persisted/failed tracking); (b) prune-on-save applies the policy the user JUST committed by value (built from the VM's bound values), not a disk re-read — the user's destructive intent is honored exactly once, immediately, regardless of flush outcome, and any later successful flush merely re-persists the same limits; (c) the VM records `LastCommitPersisted` so failed flushes are observable and testable. Disk persistence remains the restart mechanism: the slot is seeded once at boot from settings.

- **D9 — the archiver samples its audio gate once per call** (finder F7). One `Archive` reads the gate Func a single time and uses that value for the skip decision, the file write, and the entry's path, so a mid-archive flip can't orphan a WAV or dangle a reference. A user flipping the toggle mid-finalize can change which value this session's archive samples; worst outcomes are one text-only empty entry or one skipped silent-drop entry — never a privacy leak. Accepted and documented.

- **D10 — archive write+append is atomic against store sweeps** (reviewer #5). `HistoryArchiver` runs "write WAV + append entry" inside a new `HistoryStore.WithExclusiveLock(Action)` (which takes the store's existing gate; `Append` re-enters it fine — Monitor is reentrant), so `Prune`/`DeleteAllAudio` can no longer interleave between a WAV write and its entry.

- **D11 — bulk store work runs off the UI thread** (reviewer r1#12). The VM's commit chain and delete-all wrap `Prune`/`DeleteAllAudio`/usage computation in `await Task.Run(...)`; continuation resumes on the captured UI context (WinUI page) or thread pool (tests), raising notifications afterward.

- **D12 — runtime retention gate is a synchronously published in-memory slot** (reviewer r2#1). New pure-managed `PublishedHistoryRetentionSlot` (Winpepper.History) holds an immutable snapshot `{ bool StoreAudio; HistoryRetentionPolicy Policy }` swapped under a lock (publication is atomic vs. dictation reads). `HistoryServices` seeds it once from `settingsProvider()` at construction and wires the archiver gate + store policy provider to the slot — never to disk. The VM publishes before starting its D8 chain, so turning audio off takes effect for the very next archive even while (or if) persistence is still pending or has failed. Precedent: `AsrModelSelectionSlot.Publish` (in-memory publish + separate durability write).

- **D13 — mutations are queued immediately; the apply chains are serialized** (reviewer r2#5, r3#1). The setter STOPS being the queueing boundary: it publishes the slot, then STARTS `TryQueueAndFlushAsync(mutator)` synchronously without awaiting — the `Queue(mutator)` inside runs synchronously at call time, so `DebouncedSettingsWriter`'s pending list already holds the change even if the app exits while an earlier chain is still pruning (Dispose flushes everything queued). Ordering/serialization applies only to the APPLY chain: each setter's chain (await its own flush task → prune → refresh → notify) passes through one `SemaphoreSlim(1,1)`, so `RetentionApplied` and the applied-state flags complete in setter order and a quick burst can't leave stale notifications on top.

---

### Task 1: Retention settings fields + policy + store seams (pure-managed core)

**Requirements served:** R1 (fields), R2, R3, R6 (partly), R7 (Linux tests)

**Behavior:**
- `AppSettings` gains `HistoryStoreAudioEnabled` (bool, default `true`), `HistoryMaxEntries` (int, default `100`), `HistoryMaxAgeDays` (int?, default `30`; `null` = keep forever).
- `HistoryRetentionPolicy` maps settings → store policy with clamping: entries to [1, 10000] (D7); age days to [1, 36500] — applied in BOTH `FromSettings` and the `MaxAge` derived getter (defense in depth: the getter is what `Append` actually consumes, so no hand-edited `int` can overflow `TimeSpan.FromDays` mid-archive).
- `HistoryStore` prunes via an injected policy provider (defaults reproduce today's constants exactly), factors the two-tier prune into a shared helper used by both `Append` and new `public int Prune()`, and gains `DeleteAllAudio()`, `ComputeAudioDiskUsageBytes()`, and `WithExclusiveLock(Action body)` (runs `body` under the store's existing `_gate`; consumed by the archiver in Task 2 per D10).
- `Append` with `MaxAgeDays == null` skips the age tier entirely (count cap still applies).
- Destructive ops follow D5/D6:
  - `Prune()` strict-loads (corrupt/unreadable index → return 0, write nothing; missing index file = legitimately empty → proceed). It drops an entry only when its WAV (if any) is gone after its delete attempt; an entry whose WAV delete failed stays (retried on the next pass). Returns the number of dropped entries. `Append` keeps today's lenient load behavior (out-of-scope finding F2a records the pre-existing overwrite hazard).
  - `DeleteAllAudio()` sweeps `*.wav` recursively under the root — always, even with a corrupt index — enumerating with `EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }` and refusing reparse-point files; tracks per-file success; then, only when the strict loader reads cleanly, clears `WavRelativePath=""` on entries whose WAV is gone-or-absent and saves once. Returns a structured `HistoryAudioCleanupResult { DeletedCount, FailedCount }`. Re-runnable: a second call mops up what a failed first call kept.
  - The strict loader returns `false` on `JsonException`, `IOException`, and `UnauthorizedAccessException` (mirroring `SettingsStore.TryLoadCurrent`'s classification); a missing index file is NOT a failure. The shared delete helper normalizes to a full path and rejects anything outside the root (absolute or `..`) and any reparse-point file — sweep, `Prune`, `Delete`, `DeleteAllAudio`, and `Append`-prune all inherit this.
  - Save ordering mirrors existing `Append` (deletes first, atomic index save second) — but unlike `Append`, the two NEW destructive ops CATCH that save failure and report it (`IndexSaveFailed=true`) instead of throwing, so UI chains never fault after partial work.

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (add 3 fields after `PrewarmMicEnabled`-era block, with comments noting pre-2026-08 settings files keep defaults)
- Create: `src/Winpepper.History/HistoryRetentionPolicy.cs` (also carries `HistoryAudioCleanupResult` + `HistoryPruneResult`, or same-file siblings — implementer's choice; the names and shapes are the interface)
- Modify: `src/Winpepper.History/HistoryStore.cs` (keep `MaxEntries`/`MaxAge` as fallback defaults so existing tests/docs keep meaning)
- Test: `tests/Winpepper.History.Tests/HistoryRetentionPolicyTests.cs` (new), `tests/Winpepper.History.Tests/HistoryStoreTests.cs` (extend), `tests/Winpepper.Core.Tests/Settings/HistoryRetentionSettingsPersistenceTests.cs` (new)

**Interfaces:**
- Consumes: `Winpepper.Core.Settings.AppSettings`, existing `HistoryStore` ctor `(string root)` (the internal `(string root, Func<DateTime> utcNow)` seam stays internal-only; tests do NOT use it — no `InternalsVisibleTo` exists; age tests use `DateTime.UtcNow.AddDays(...)` like the existing tests do).
- Produces:
  - `AppSettings.HistoryStoreAudioEnabled : bool = true`
  - `AppSettings.HistoryMaxEntries : int = 100`
  - `AppSettings.HistoryMaxAgeDays : int? = 30`
  - `public sealed record HistoryRetentionPolicy { int MaxEntries /*=100*/; int? MaxAgeDays /*=30*/; TimeSpan? MaxAge { get; } /*null when MaxAgeDays null; clamps to [1,36500] days*/; static HistoryRetentionPolicy Default { get; } static HistoryRetentionPolicy FromSettings(AppSettings) }` in `Winpepper.History`
  - `public sealed record HistoryAudioCleanupResult { int DeletedCount; int FailedCount; bool IndexSaveFailed; bool EnumerationFailed }` in `Winpepper.History`
  - `public sealed record HistoryPruneResult { int DroppedCount; bool IndexSaveFailed }` in `Winpepper.History`
  - `public HistoryStore(string root, Func<HistoryRetentionPolicy> policyProvider)`
  - `public HistoryPruneResult HistoryStore.Prune()` (never throws for index IO: strict-load failure ⇒ all-zeros + no write; save failure ⇒ `IndexSaveFailed=true`)
  - `public HistoryAudioCleanupResult HistoryStore.DeleteAllAudio()` (never throws for index/enumeration IO; per-file delete failures counted)
  - `public long HistoryStore.ComputeAudioDiskUsageBytes()`
  - `public void HistoryStore.WithExclusiveLock(Action body)`

**Test cases:**
- Existing 52 History tests pass unmodified (R6 regression proof).
- Policy `MaxEntries = 3`: append 5 with real WAVs → 3 newest kept, 2 oldest WAVs deleted.
- Policy `MaxAgeDays = 7`: 8-day-old entry pruned on append, 5-day kept.
- Policy `MaxAgeDays = null`: 400-day-old entry survives appends; count cap at `MaxEntries` still enforced.
- `Prune()`: seed 5 entries (index written, no append afterwards) then `Prune()` with MaxEntries=2 → 2 remain, 3 WAVs gone, `DroppedCount=3, IndexSaveFailed=false`. Also age variant.
- Prune with `null` age keeps by age but drops by count.
- `Prune()` on a corrupt `index.json` → all-zeros result, file bytes unchanged.
- `Prune()` on an UNREADABLE `index.json` (chmod 000; guard with `Assert.SkipUnless` when the chmod didn't bite — cross-platform precedent: the Cleanup tests self-skip this way) → all-zeros result, file untouched.
- `Prune()` with a blocked index save (valid over-cap index, entries WAV-less, history-root directory chmod 555 so `AtomicFile` cannot write) → reports `DroppedCount=<intended>, IndexSaveFailed=true`, index content unchanged, no exception escapes.
- `DeleteAllAudio()`: 3 entries with WAVs → all WAVs gone, 3 entries kept with empty `WavRelativePath`, result `DeletedCount=3, FailedCount=0, IndexSaveFailed=false`; second call returns zeros (idempotent). Include an orphan `orphan.wav` (no entry) → also deleted and counted.
- `DeleteAllAudio()` on a corrupt index → WAVs still swept and counted, index file untouched byte-for-byte (`IndexSaveFailed=false` — no save was attempted).
- `DeleteAllAudio()` with a resisting WAV (file read-only + its day directory read-only; SkipUnless-guarded) → counted in `FailedCount`, its entry retains `WavRelativePath`, the other WAVs deleted; after restoring permissions a second call deletes it and clears the ref (truthful retry).
- `DeleteAllAudio()` with blocked index save → WAVs deleted, `IndexSaveFailed=true`, refs NOT cleared, no exception escapes.
- `DeleteAllAudio()` with an inaccessible subtree (a day directory chmod 000 containing WAVs; SkipUnless-guarded) → no exception escapes, `EnumerationFailed=true`, and the result is distinguishable from a clean sweep of an empty folder.
- Junction/symlink escape: an in-root symlink (test-created) to an external directory containing `outside.wav` → sweep does NOT delete `outside.wav`; the symlink entry itself is not treated as a WAV. (Linux-runnable symlink creation; gate repeats it on Windows.)
- Traversal: entry with `WavRelativePath = "../evil.wav"` and a fabricated target outside the root → the outside file survives `Append`-prune and `Prune()` passes (guard refuses the escape) and the entry is retained (its WAV never became gone).
- `ComputeAudioDiskUsageBytes()`: two fabricated WAVs (e.g. 10 + 20 bytes) + non-WAV file → returns exact WAV byte sum; empty/missing root → 0; reparse-pointed-away content not counted.
- `FromSettings`: defaults → (100, 30); custom (5, 7) round-trips; `HistoryMaxAgeDays = null` → `MaxAge == null`; clamping `HistoryMaxEntries = 0` → 1 and `= 50000` → 10000; `HistoryMaxAgeDays = 0` → 1 day and `= int.MaxValue` → 36500 days (no overflow: `MaxAge` getter also clamps when the record is built directly).
- Persistence chain-pin (mirrors `StreamingSettingPersistenceTests`): `QueueAndFlushAsync(s => s with { HistoryStoreAudioEnabled = false, HistoryMaxEntries = 7, HistoryMaxAgeDays = null })` through a real `DebouncedSettingsWriter` + `SettingsStore` on a temp path → reload shows all three; neighbors untouched.
- Pre-2026-08 settings file (JSON lacking the 3 fields) → `SettingsStore.Load()` yields the defaults (true/100/30).

- [ ] **Step 1: Write the failing behavioral tests**

  `HistoryRetentionPolicyTests`: mapping/clamping cases above. New tests in `HistoryStoreTests`: custom caps, unlimited age, Prune, DeleteAllAudio, disk usage, traversal, symlink — fabricate WAVs with `File.WriteAllText`; age-sensitive entries use `CreatedAtUtc = DateTime.UtcNow.AddDays(-N)` exactly like the existing 31-day prune test (the internal `utcNow` ctor is not accessible from this test assembly — no friend assembly — and no test should need it). `HistoryRetentionSettingsPersistenceTests`: real writer round-trip + missing-fields defaults.

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

  `AppSettings`: add the three init-properties with defaults + comments. `HistoryRetentionPolicy`: record with `MaxAge` derived property and clamping `FromSettings` (+ clamp again inside the `MaxAge` getter). `HistoryStore`: chained ctors ending in a private `(root, policyProvider, utcNow)`; `_policyProvider` field; `Append` prunes through extracted private `ApplyPolicy` honoring null age; strict loader; containment-checked + reparse-refusing delete helper; `Prune()`/`DeleteAllAudio()`/`ComputeAudioDiskUsageBytes()`/`WithExclusiveLock()` lock `_gate` like existing members, implement the D5/D6 failure protocols, catch index-save failures into the result structs, and never rethrow them.

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
- `HistoryArchiveInput` gains `bool IsSilentDrop` (default `false`). `HistoryArchiver` gains `Func<bool>? storeAudio` ctor seam (default on). Each `Archive` call samples the gate **exactly once** into a local (D9); when off AND `IsSilentDrop`, return `null` without writing/appending (D1a); when off for a normal entry: no WAV written, entry persisted with `WavRelativePath = ""`, `DurationMs` still derived from samples. `Archive`'s return type becomes `HistoryEntry?` (all four production call sites ignore it; tests consume it).
- When a WAV IS written, the write+append run inside `_store.WithExclusiveLock(...)` (D10) so a `Prune`/`DeleteAllAudio` can never interleave between the file appearing and its entry landing.
- New `PublishedHistoryRetentionSlot` (pure-managed, Winpepper.History): lock-guarded immutable snapshot `{ bool StoreAudio; HistoryRetentionPolicy Policy }`, `Get()`/`Publish(...)`, and `static FromSettings(AppSettings)` seeding. The runtime gate reads THIS (D12), not the settings file.
- `HistoryServices` creates and exposes the slot: seeded once from its `settingsProvider()` argument at construction; `Store = new HistoryStore(historyRoot, () => Slot.Policy)` and `Archiver = new HistoryArchiver(Store, storeAudio: () => Slot.StoreAudio)` — never a disk re-read per dictation. Its only production caller — `AppShell.cs:329` — passes `() => store.Load()` (same lambda shape `PipelineHost` already receives; used for the one-time seed).
- The two PipelineHost silent-drop initializers gain `IsSilentDrop = true;` — the ONLY App-side pipeline edit (no `if` guards, no settings reads); the two success sites are byte-untouched.
- `Archive`'s return type becoming `HistoryEntry?` (D1a) forces a syntactic null-forgiveness in the three pre-existing archiver tests (`entry!.RawTranscript…`) because `Directory.Build.props` promotes nullable warnings to errors; this is the authorized, recorded exception to R6's "existing tests unmodified" claim — the assertions' semantics are unchanged (Step 1 names the exact edits).
- Defaults ⇒ byte-identical behavior to today (archiver tests from Task 1 baseline keep passing).

**Files:**
- Create: `src/Winpepper.History/PublishedHistoryRetentionSlot.cs`
- Modify: `src/Winpepper.History/HistoryArchiver.cs` (also fix its stale "Pruning to 50" doc comment → policy wording)
- Modify: `src/Winpepper.App/Services/HistoryServices.cs` (slot creation + exposure; ctor signature) — Windows-compile only
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (pass `() => store.Load()` at the HistoryServices construction, ~line 329) — Windows-compile only
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (add `IsSilentDrop = true;` to the two silent-drop `HistoryArchiveInput` initializers at ~:745 and ~:1371) — Windows-compile only
- Test: `tests/Winpepper.History.Tests/HistoryArchiverTests.cs` (extend; 3 existing tests get `entry!`, no assertion changes)

**Interfaces:**
- Consumes: `HistoryRetentionPolicy.FromSettings` + `HistoryStore.WithExclusiveLock` (Task 1), `AppSettings.HistoryStoreAudioEnabled` (Task 1), `() => store.Load()` pattern already used for `PipelineHost` in `AppShell.cs`.
- Produces:
  - `public sealed class PublishedHistoryRetentionSlot { bool StoreAudio { get; } HistoryRetentionPolicy Policy { get; } void Publish(bool storeAudio, HistoryRetentionPolicy policy); static PublishedHistoryRetentionSlot FromSettings(AppSettings) }` (lock-guarded; getters return the current snapshot atomically — model as an immutable snapshot record swapped under lock)
  - `HistoryArchiver(HistoryStore store, Func<DateTime>? nowUtc = null, Func<bool>? storeAudio = null)`
  - `HistoryArchiveInput.IsSilentDrop : bool = false`
  - `HistoryEntry? HistoryArchiver.Archive(HistoryArchiveInput input)` (null ⇒ skipped: audio off + silent drop)
  - `HistoryServices(string historyRoot, ITranscriptionRerunService transcriptionRerun, Func<Winpepper.Core.Settings.AppSettings> settingsProvider)` + `HistoryServices.RetentionSlot` property

**Test cases:**
- Gate off: no WAV file appears in root, entry appended with empty `WavRelativePath`, `DurationMs` correct for 16000 samples (1000 ms).
- Gate off + `IsSilentDrop = true` → `Archive` returns `null`, no WAV, and the store index stays empty (D1a, Linux-verified decision owner).
- Gate on + `IsSilentDrop = true` → archived normally with WAV (today's silent-drop recovery preserved).
- Gate flips between calls (Func re-read per call): first archive writes WAV, flip returns false, second archive WAV-less → proves live read.
- Single sampling (D9): counting `storeAudio` fake invoked exactly **once** per `Archive` call.
- Concurrency gate-observation test (D10, deterministic): thread T1 enters `store.WithExclusiveLock(() => { gateHeld.Set(); release.WaitOne(…); })`; thread T2 runs one gate-on `Archive` as a task. After `gateHeld` + a 250 ms settle, the archive task MUST NOT be complete (it can only be waiting on the gate — a non-locked implementation completes in ms and fails the assertion); then `release.Set()` and the archive completes with file+entry consistent. No probabilistic loops; the sleep only waits for a would-complete-unblocked implementation, never gates correctness in the blocking direction.
- Gate default: existing archiver tests pass (constructor back-compat — old two-arg call sites unchanged).

- [ ] **Step 1: Write the failing behavioral tests**

  New `HistoryArchiverTests`: `Archive_StoreAudioOff_WritesNoWav_PersistsTextOnlyEntry`, `Archive_StoreAudioOff_SilentDrop_SkipsArchiveEntirely`, `Archive_StoreAudioOn_SilentDrop_ArchivesWithWav`, `Archive_StoreAudioGate_ReadLive_PerCall`, `Archive_StoreAudioGate_SampledOncePerCall` (counting fake), and `Archive_BlockedByExclusiveLock_CompletesAfterRelease` (deterministic gate-observation; see Test cases). Authorized pre-existing-test adjustment (R6 exception, semantics unchanged): the three existing archiver tests change `entry.<prop>` to `entry!.<prop>` for the nullable return.

- [ ] **Step 2: Run the tests and verify the intended failures**

  ```
  dotnet build tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj -c Release
  dotnet exec tests/Winpepper.History.Tests/bin/Release/net9.0/Winpepper.History.Tests.dll -class "Winpepper.History.Tests.HistoryArchiverTests"
  ```
  Expected: FAIL — no `storeAudio`/`IsSilentDrop`/`WithExclusiveLock`-consuming archiver exists yet (compile error = intended failure).

- [ ] **Step 3: Add the minimal production implementation**

  HistoryArchiver: add `IsSilentDrop` to the input record; add optional third ctor parameter (`_storeAudio = storeAudio ?? (() => true)`); in `Archive`, sample ONCE (`var keepAudio = _storeAudio();`); `if (!keepAudio && input.IsSilentDrop) return null;`; when `keepAudio`, wrap `WavWriter.WriteMono16kInt16(absolute, ...)` + `_store.Append(entry)` in `_store.WithExclusiveLock(() => { ... })`; when off, append with `WavRelativePath = ""` (no file write, no exclusive section needed beyond Append's own lock); `DurationMs` from sample count either way; XML doc comment updated ("50" → policy wording, IsSilentDrop rule documented). New `PublishedHistoryRetentionSlot`: an immutable snapshot record `{ StoreAudio, Policy }` swapped under a private lock; `FromSettings(s)` builds it via `(s.HistoryStoreAudioEnabled, HistoryRetentionPolicy.FromSettings(s))`. HistoryServices: new ctor param; `RetentionSlot = PublishedHistoryRetentionSlot.FromSettings(settingsProvider()); Store = new HistoryStore(historyRoot, () => RetentionSlot.Policy); Archiver = new HistoryArchiver(Store, storeAudio: () => RetentionSlot.StoreAudio);`. AppShell (`~:329`): pass `() => store.Load()` as the third HistoryServices argument. PipelineHost: in the two silent-drop initializers (`~:745`, `~:1371` — anchors: `if (trimmed is null)` / `if (trimmed2 is null)` blocks whose comments say "STILL archive the ORIGINAL buffer"), add `IsSilentDrop = true,` to the `HistoryArchiveInput` initializer. Exact before/after snippets in the task brief.

- [ ] **Step 4: Run the focused tests**

  Same commands as Step 2. Expected: PASS (all archiver tests incl. pre-existing).

- [ ] **Step 5: Refactor while green**

  None needed beyond the comment fix (change is deliberately minimal).

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`
  Expected: `LINUX SUITE: GREEN`. Note in the implementer report that `HistoryServices.cs`/`AppShell.cs`/`PipelineHost.cs` do not compile on Linux (App excluded); their compile verification is deferred to the Windows gate in Task 4 — App edits here are mechanical one-liners over verified anchors, review-gated.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.History/PublishedHistoryRetentionSlot.cs src/Winpepper.History/HistoryArchiver.cs src/Winpepper.App/Services/HistoryServices.cs src/Winpepper.App/Hosting/AppShell.cs src/Winpepper.App/Hosting/PipelineHost.cs tests/Winpepper.History.Tests/HistoryArchiverTests.cs
  git commit -m "feat(history): store-audio on/off gate in HistoryArchiver; silent-drop skip; exclusive write+append; wire live settings"
  ```

---

### Task 3: Retention view-model + WAV-less detail-page rerun guard

**Requirements served:** R3 (prune-on-save trigger), R4 (VM half), R5 (no crash), R6

**Behavior:**
- `ISettingsWriter` gains `Task<bool> TryQueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)` — true when the change is durably on disk when the task completes. Default interface member wraps `QueueAndFlushAsync` and assumes success (test fakes unaffected); `DebouncedSettingsWriter` overrides it with real tracking: false when the flush was skipped for a degraded load (mutations kept pending) or the save failed (requeued). Existing `QueueAndFlushAsync` behavior is untouched; all existing writer tests keep passing.
- New pure-managed `HistoryRetentionViewModel` (in `Winpepper.History/ViewModels`): binds `StoreAudioEnabled` (bool), `MaxEntries` (double for NumberBox, clamped to [1,10000]), `MaxAgeDays` (double, clamped to [1,36500]; setting it while `KeepForever` is true STILL persists `null` — the invariant that protects D2), `KeepForever` (bool; true ⇒ persists `HistoryMaxAgeDays = null`), exposes `DiskUsageDisplay`, `LastCommitPersisted` (bool), and `LastApplyHadIndexFailure` (bool — from the structured prune/delete results, so `RetentionApplied` never silently implies a failed index save). When the persisted age is already `null` at construction, `KeepForever` starts true and `MaxAgeDays` still carries a concrete numeric value — the last numeric if known, else the 30-day default — so the disabled box displays something sensible and unchecking later persists that value. The ctor populates `DiskUsageDisplay` immediately. Each setter: (1) synchronously `slot.Publish(storeAudio, policy)` so the runtime gate flips NOW (D12); (2) synchronously STARTS `var flush = _writer.TryQueueAndFlushAsync(mutator);` — the writer queues the mutation at call time (D13: an app exit mid-chain loses nothing already queued) — then (3) `_ = CommitAndApplyAsync(flush, committedPolicy)` — serialized through one `SemaphoreSlim(1,1)` — `LastCommitPersisted = await flush;` → `var pr = await Task.Run(() => _store.Prune(committedPolicy));` → `LastApplyHadIndexFailure = pr.IndexSaveFailed;` → recompute usage bytes off-thread but assign + notify on the continuation context (D11's promise) → raise `RetentionApplied`.
- `event EventHandler? RetentionApplied` fires after prune+refresh complete; the page subscribes and refreshes its list there (never synchronously after assignment).
- `Task<HistoryAudioCleanupResult> DeleteAllAudioAsync()` wraps `_store.DeleteAllAudio()` in `Task.Run`, refreshes usage, raises `RetentionApplied`; returns the structured result for the page's message.
- `HistoryStore.Prune` gains an optional explicit-policy parameter: `Prune(HistoryRetentionPolicy? policyOverride = null)` uses the override when given, else the provider (Task 1's store file picks this up; test it here against VM flows).
- `HistoryDetailViewModel` transcription Runner: short-circuits on empty `WavRelativePath` with an inline message, and catches `FileNotFoundException` with an inline message (same inline-surface precedent as the existing `InvalidOperationException` catch) — WAV-less entries can no longer crash the page's async-void handler.

**Files:**
- Modify: `src/Winpepper.Core/Settings/ISettingsWriter.cs` (new default member)
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` (override + Flush outcome tracking)
- Modify: `src/Winpepper.History/HistoryStore.cs` (Prune policyOverride parameter — one-line overload body reusing Task 1's helper)
- Create: `src/Winpepper.History/ViewModels/HistoryRetentionViewModel.cs`
- Modify: `src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs` (Runner only)
- Test: `tests/Winpepper.History.Tests/ViewModels/HistoryRetentionViewModelTests.cs` (new), extend `tests/Winpepper.History.Tests/ViewModels/HistoryDetailViewModelTests.cs`, extend `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: Task 1's `HistoryStore.Prune/DeleteAllAudio/ComputeAudioDiskUsageBytes` + `HistoryRetentionPolicy` + `HistoryAudioCleanupResult` + `HistoryPruneResult`, Task 2's `PublishedHistoryRetentionSlot`, `AppSettings` fields, `Winpepper.Core.Settings.ISettingsWriter`, fakes `FakeTranscriptionRerunService` (already throws `FileNotFoundException` for missing WAV) and `FakeCleanupRerunService`.
- Produces:
  - `Task<bool> ISettingsWriter.TryQueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)` (default member)
  - `public HistoryPruneResult HistoryStore.Prune(HistoryRetentionPolicy? policyOverride = null)`
  - `public sealed class HistoryRetentionViewModel : INotifyPropertyChanged`
    - ctor `(AppSettings initial, HistoryStore store, ISettingsWriter writer, PublishedHistoryRetentionSlot slot)`
    - `bool StoreAudioEnabled`, `double MaxEntries`, `double MaxAgeDays`, `bool KeepForever` (setters publish to the slot synchronously, then kick off the ordered, serialized D8/D11/D13 commit chain)
    - `string DiskUsageDisplay` (populated in ctor + refreshed after each apply), `bool LastCommitPersisted`, `bool LastApplyHadIndexFailure` (read-only, notified)
    - `event EventHandler? RetentionApplied`
    - `void Refresh()` (reloads usage; cheap)
    - `Task<HistoryAudioCleanupResult> DeleteAllAudioAsync()`
  - `HistoryDetailViewModel` unchanged publicly (behavioral fix inside the Runner).

**Test cases:**
- Writer outcome (Core): `DebouncedSettingsWriter.TryQueueAndFlushAsync` → true on a normal persisted write; false (and mutation requeued) on a FAILED SAVE — a valid readable settings file inside a read-only containing directory (chmod 555; SkipUnless-guarded): `TryLoadCurrent` reads fine, the atomic temp-write fails, the save-error branch runs; and false on a DEGRADED READ — settings path that exists-but-unreadable during load (SkipUnless-guarded). (The plain directory-at-settings-path variant exercises only the degraded-read branch and is not a save-failure test — reviewer r3#4.)
- Queued-immediately guarantee (D13, reviewer r3#1): set three VM properties in a burst while the gated writer never completes; assert the writer's queued-mutation list already contains all three mutations synchronously after the setters return (an exit would lose nothing queued).
- Each VM setter writes the right `AppSettings` mutation through a gated fake writer whose `TryQueueAndFlushAsync` completes only when the test says so: before completion — store unpruned, `RetentionApplied` not fired (but the slot already carries the new values — assert `slot.StoreAudio`/policy update is synchronous); after completion — prune ran with the committed policy (seed 5 entries + WAVs, `MaxEntries = 2` → 2 remain, 3 WAVs gone), usage refreshed, `RetentionApplied` fired exactly once.
- Failed-flush path: fake writer returns false → `LastCommitPersisted == false` AND the prune STILL ran with the committed policy (the user's destructive intent is honored once; D8).
- Index-save-failure surfacing (reviewer r3#2): prune with a blocked index save → `LastApplyHadIndexFailure == true` (and `RetentionApplied` still fired — the page refresh is wanted — but the failure state is observable and shown in the page's info text).
- End-to-end privacy gate (reviewer r2#1): VM + `PublishedHistoryRetentionSlot` + a `HistoryArchiver` wired `storeAudio: () => slot.StoreAudio` + real temp store; set `vm.StoreAudioEnabled = false` while the fake writer's flush is still gated INCOMPLETE → an immediate `archiver.Archive(...)` writes no WAV and persists a text-only entry (the runtime gate flipped synchronously, persistence notwithstanding). Mirror: silent-drop input right after the flip → skipped entirely.
- Burst ordering (D13): three rapid setter changes → `RetentionApplied` fires three times in setter order and the final store/settings/usage state reflects the LAST setter (no stale-completion overwrite).
- `KeepForever = true` persists `HistoryMaxAgeDays = null`; setting `MaxAgeDays` while `KeepForever` stays true STILL persists null (guard); toggling `KeepForever` back off persists the current numeric days.
- Reopen-from-unlimited (reviewer r3#7): VM constructed from settings with `HistoryMaxAgeDays = null` → `KeepForever == true`, `MaxAgeDays == 30` (numeric fallback); unchecking persists 30.
- Clamps: `MaxEntries = 0`/NaN guarded (0 → commit as 1; NaN ignored), `MaxEntries = 99999` → 10000, `MaxAgeDays = 0` → 1.
- `DiskUsageDisplay` reflects fabricated WAV bytes (assert contains the byte count; loose match to avoid overfitting).
- `DeleteAllAudioAsync` returns the structured result (deleted/failed), clears WAVs, keeps entries, refreshes usage, fires `RetentionApplied`.
- Detail VM with `WavRelativePath = ""` entry: `await vm.TranscriptionPanel.RunAsync(default)` → `RerunText` is the no-audio inline message; fake service's throw path (non-empty path, missing file → `FileNotFoundException` from `FakeTranscriptionRerunService`) → inline message, no exception escapes.

- [ ] **Step 1: Write the failing behavioral tests**

  New `HistoryRetentionViewModelTests` (cases above: gated-flush ordering, failed-flush path, keep-forever, clamps, usage, delete-all, event), detail-VM WAV-less cases (extend `HistoryDetailViewModelTests`; temp roots per test, dispose pattern from `HistoryStoreTests`), and the `DebouncedSettingsWriterTests` outcome additions.

- [ ] **Step 2: Run the tests and verify the intended failures**

  ```
  dotnet build tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj -c Release
  dotnet exec tests/Winpepper.History.Tests/bin/Release/net9.0/Winpepper.History.Tests.dll
  dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
  dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -class "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests"
  ```
  Expected: FAIL — `HistoryRetentionViewModel`/`TryQueueAndFlushAsync`/Prune-override do not exist (compile errors = intended failures); the detail-VM missing-WAV test fails by the `FileNotFoundException` escaping `RunAsync` (that IS the crash being fixed).

- [ ] **Step 3: Add the minimal production implementation**

  ISettingsWriter: default member `async Task<bool> TryQueueAndFlushAsync(m) { await QueueAndFlushAsync(m); return true; }`. DebouncedSettingsWriter: factor the persisted/not-persisted outcome out of `Flush()` (persisted := nothing pending-or-Save-succeeded; not-persisted := degraded-load-kept or Save-failed-requeued) and implement the override end-to-end (Queue → cancel/await scheduled → flush → outcome). HistoryStore: `Prune(HistoryRetentionPolicy? policyOverride = null)`. New VM: ctor populates `DiskUsageDisplay` once and resolves `MaxAgeDays = initial.HistoryMaxAgeDays ?? 30` with `KeepForever = initial.HistoryMaxAgeDays is null`; setters guard NaN/clamps (and keep `null` persisted while `KeepForever`), synchronously `slot.Publish(storeAudio, policy)`, synchronously START `var flush = _writer.TryQueueAndFlushAsync(mutator)` (queues NOW), then `_ = CommitAndApplyAsync(flush, policy)` whose body is serialized via a private `SemaphoreSlim(1,1)`: `LastCommitPersisted = await flush; var pr = await Task.Run(() => _store.Prune(policy)); LastApplyHadIndexFailure = pr.IndexSaveFailed;` then recompute the usage STRING inside `Task.Run` but assign `DiskUsageDisplay` + raise `PropertyChanged` and `RetentionApplied` on the continuation context; all inside try/finally releasing the semaphore; store-side failures never escape (D5) and any unexpected chain exception is contained into `LastCommitPersisted = false` so the fire-and-forget chain cannot fault unobserved. `DeleteAllAudioAsync` wraps the sweep in `Task.Run`, folds `IndexSaveFailed`/`EnumerationFailed` into `LastApplyHadIndexFailure` and the usage display, refreshes, raises the event, returns the structured result. Detail VM: prepend the empty-path short-circuit and add `catch (FileNotFoundException)` in the transcription Runner.

- [ ] **Step 4: Run the focused tests**

  Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: Refactor while green**

  Keep `HistoryRetentionViewModel` symmetric with `RecordingSettingsViewModel` naming; no further changes.

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`
  Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 7: Commit the task**

  ```bash
  git add src/Winpepper.Core/Settings/ISettingsWriter.cs src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs src/Winpepper.History/HistoryStore.cs src/Winpepper.History/ViewModels/HistoryRetentionViewModel.cs src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs tests/Winpepper.History.Tests/ViewModels tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
  git commit -m "feat(history): retention settings view-model with outcome-tracked prune-on-save; WAV-less detail rerun guard"
  ```

---

### Task 4: History page "Recordings" settings card (WinUI XAML)

**Requirements served:** R4 (UI), R1/D1a (UI callout), R5 (disk+delete affordances' safety: confirm dialog), R7 (Windows gate)

**Behavior:**
- History page gains a card above the list: store-audio ToggleSwitch (with the silent-drop recovery-loss callout), max-entries NumberBox, age NumberBox + "Keep forever" CheckBox (age box disabled while checked; the VM independently keeps persisting `null` in that state), disk-usage TextBlock, and a confirm-dialoged "Delete all saved audio now" Button. Controls commit through `HistoryRetentionViewModel` (Task 3); the page refreshes the history list and usage text from the VM's `RetentionApplied` event (never synchronously after a property assignment) and populates the usage text on navigation from the VM's ctor-filled value.
- The page root becomes a `ScrollViewer` (reviewer #8): title, subtitle, card, and list all remain reachable at the supported 480×400 minimum window (`WindowSizePolicy` min dims). The `ListView` keeps internal scrolling via a `MaxHeight` (e.g. 480) instead of the `*` grid row (a `*` row is unbounded inside a ScrollViewer).
- Pure UI wiring task: no new logic beyond event plumbing. No useful Red step exists for XAML wiring (no UI test harness in this repo; precedent: prior page work verified by XAML compile + review) — recorded as such per usual-test-driven-development's documentation/mechanical exception. Verification = Windows gate compile + reviewer inspection. NOTE: runtime pixel/layout verification at 480×400 is not executable in this environment (launching Winpepper.exe is forbidden); recorded as the task's verification limit.

**Files:**
- Modify: `src/Winpepper.App/Views/HistoryPage.xaml` (ScrollViewer root, add card)
- Modify: `src/Winpepper.App/Views/HistoryPage.xaml.cs` (build VM, wire events, confirm dialog, subscribe `RetentionApplied`, refresh list)

**Interfaces:**
- Consumes: `HistoryRetentionViewModel` incl. `RetentionApplied` + `LastCommitPersisted` + slot-fed ctor (Task 3), `App.Shell.HistoryServices.Store`, `App.Shell.HistoryServices.RetentionSlot`, `App.Shell.SettingsStore.Load()` (freshest snapshot for VM init), `App.Shell.SettingsWriter`, existing card style from `RecordingPage.xaml` (`CardBackgroundFillColorDefaultBrush` borders, `ToggleSwitch`, caption TextBlocks), `ContentDialog` with `XamlRoot`.

**Test cases:**
- Windows gate compiles the new XAML and runs all test projects (authoritative check).
- x:Name'd controls: `StoreAudioToggle`, `MaxEntriesBox`, `MaxAgeBox`, `KeepForeverCheck`, `DiskUsageText`, `DeleteAllAudioButton` — code-behind references them only inside `#if WINDOWS` page partial (whole file is `#if WINDOWS` already).
- Reviewer check: every control event handler commits via the VM only (no direct store/settings mutation in the page); list refresh happens in the `RetentionApplied` handler; `MaxAgeBox.IsEnabled` tracks `!KeepForever`; `DiskUsageText` is set on navigation (not only after a change).

- [ ] **Step 1: Write the failing behavioral test**

  None — mechanical XAML wiring with no repo UI-test harness; record the reason. Substitute check: XAML must compile on Windows (GenXbf runs there), i.e. the gate build is the executable check.

- [ ] **Step 2: (n/a — see Step 1)**

- [ ] **Step 3: Add the minimal production implementation**

  XAML: wrap the existing `Grid` content in a `ScrollViewer`; keep title/subtitle rows; insert a new `Auto` grid row before the list row; set the ListView's row to `Auto` and give the ListView `MaxHeight="480"`. Add one `Border` card containing: `ToggleSwitch x:Name="StoreAudioToggle" Header="Save audio recordings of your dictations"`, a caption TextBlock stating "When off, Winpepper keeps transcripts and timings in history but saves no audio. Dictations dismissed as silent are not archived — without audio there is nothing to recover — and Lab replay/re-transcribe needs audio.", a NumberBox `x:Name="MaxEntriesBox"` (`Minimum=1`, `Maximum=10000`) with header "Keep at most this many dictations", a NumberBox `x:Name="MaxAgeBox"` (`Minimum=1`, `Maximum=36500`) with header "Delete dictations older than (days)", a CheckBox `x:Name="KeepForeverCheck" Content="Keep forever — never delete by age (for building an eval corpus)"`, a TextBlock `x:Name="DiskUsageText"`, and a Button `x:Name="DeleteAllAudioButton" Content="Delete all saved audio now"`. Code-behind: in `OnNavigatedTo` build `new HistoryRetentionViewModel(App.Shell.SettingsStore.Load(), services.Store, App.Shell.SettingsWriter, services.RetentionSlot)`, initialize control state from it INCLUDING `DiskUsageText.Text = vm.DiskUsageDisplay` (populated by the VM ctor), wire `Toggled`/`ValueChanged` (NaN-guarded)/`Checked`/`Unchecked` to the VM setters, keep `MaxAgeBox.IsEnabled = !vm.KeepForever` synced from both the checkbox handler and the VM, subscribe `vm.RetentionApplied += (_, _) => { ViewModel.Refresh(); DiskUsageText.Text = vm.DiskUsageDisplay; };` (and surface `vm.LastCommitPersisted == false` as "Setting could not be saved right now; it will be retried." plus `vm.LastApplyHadIndexFailure` as "The history index could not be updated; retry to finish applying the limit." in a small inline info text). Delete button shows a `ContentDialog` ("Delete all saved audio? Recordings are deleted; transcripts are kept. This cannot be undone.", Primary="Delete", Close="Cancel") before `var r = await vm.DeleteAllAudioAsync();`, then shows the truthful result from the structured counts ("N recordings deleted." +, when `r.FailedCount > 0`, " M could not be deleted (file in use) — press again to retry." +, when `r.EnumerationFailed`, " Part of the history folder could not be scanned; the result above is incomplete." +, when `r.IndexSaveFailed`, " The history index could not be updated; your entry list may still show audio paths until the next cleanup.").

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
- **Fresh-Eyes round-1 amendments (Stage 3):** all 12 In-scope findings fixed — age clamp hard ceiling 36500 in `FromSettings` AND the `MaxAge` getter (+int.MaxValue test); `AppShell.cs` added to T2 files/commit; D8 rewritten around an explicit flush-outcome contract (`TryQueueAndFlushAsync`) + prune-by-committed-policy (false-outcome path tested); `RetentionApplied` event drives page refresh (no synchronous-after-assignment refresh); D10 exclusive-lock boundary for write+append (+barrier concurrency test); D6 extended to physical safety (reparse-point-skipping enumeration + reparse-refusing delete, symlink-escape test); single consistent `DeleteAllAudio` contract (always sweep, conditional ref-rewrite, structured result used by the page's message); silent-drop skip moved into the archiver via `IsSilentDrop` (Linux-testable owner, `if`-guards deleted from the App edits); internal `utcNow` seam dropped from test instructions (no friend assembly; relative-date pattern kept); D11 offloads bulk store work via `Task.Run`; History page root becomes a ScrollViewer with a `MaxHeight`-bounded ListView for the 480×400 minimum (runtime pixel verification recorded as unexecutable here — no app launch allowed). Interface consistency re-verified (T1 produces `WithExclusiveLock`/`Prune(override)`/`HistoryAudioCleanupResult`; T2/T3/T4 consume exactly those).
- **Fresh-Eyes round-2 amendments (Stage 3):** all 7 In-scope findings fixed — D12 synchronously-published in-memory `PublishedHistoryRetentionSlot` becomes the runtime gate (archiver/store read it, never disk; seeded once at boot; VM publishes before persisting) with a VM→slot→archiver end-to-end test that flips the toggle while the flush is still gated and asserts no WAV is written; D5 extended so both destructive ops CATCH and REPORT index-save/enumeration failures (`HistoryPruneResult{DroppedCount,IndexSaveFailed}`, `HistoryAudioCleanupResult{DeletedCount,FailedCount,IndexSaveFailed}`) — unreadable-index, save-blocked, resisting-WAV, and truthful-retry tests added (SkipUnless-guarded permission injections); the `IsSilentDrop` path's `HistoryEntry?` return now explicitly authorizes the `entry!` adjustment in the 3 existing archiver tests (recorded R6 exception, semantics unchanged); D13 serializes commit chains through `SemaphoreSlim(1,1)` (+burst-ordering test) and every fire-and-forget chain is exception-contained into `LastCommitPersisted=false` so no UI-chain fault escapes; age box disabled while Keep-forever is checked and the VM keeps persisting `null` in that state (+guard test); usage text populated at VM construction and on page navigation. Interface consistency re-verified (slot produced by T2, consumed by T2 wiring/T3 VM/T4 page; `HistoryServices.RetentionSlot` exposed for the page; Prune signature is `HistoryPruneResult Prune(HistoryRetentionPolicy? policyOverride = null)` everywhere).
- **Fresh-Eyes round-3 amendments (Stage 3, final plan round):** all 8 In-scope findings fixed — D13 now queues each mutation with the writer SYNCHRONOUSLY in the setter (start `TryQueueAndFlushAsync` immediately, don't await) so an app exit mid-chain can't lose a not-yet-queued change; the serialized semaphore only orders the apply chains. `LastApplyHadIndexFailure` surfaces the structured prune/delete `IndexSaveFailed` (and `EnumerationFailed`) instead of letting `RetentionApplied` imply success; page text covers all failure flags. The D10 regression test is deterministic gate-observation (hold `WithExclusiveLock`, assert a concurrent archive cannot complete until release) instead of a probabilistic barrier loop. The writer failed-save test uses a valid readable settings file in a read-only containing directory (reaches the genuine save-failure requeue branch; the directory-as-path variant hits only the degraded-read branch and stays as that case). `HistoryAudioCleanupResult` gains `EnumerationFailed` (inaccessible-subtree test). Usage-string computation moves off-thread with assignment+notification on the continuation context (D11's actual promise). VM-from-persisted-null defines `MaxAgeDays = 30`/`KeepForever = true` and unchecking persists 30 (+reopen test). One Out-of-scope Major (diagnostics bundle contains transcripts despite README promise) recorded verbatim in the shared findings file; non-blocking, no extra round. Interface consistency re-verified end-to-end.
