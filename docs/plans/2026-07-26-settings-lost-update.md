# Settings Lost-Update Fix Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the settings lost-update bug that silently reverted `cleanupEnabled` and pasted raw uncleaned ASR output for 327 real dictations on 7/25–7/26 (`CleanupOptionsFactory.cs:17` maps the cleanup bypass solely from `CleanupEnabled`; 327 `Cleanup path="BypassDisabled"` raw pastes counted in the host logs). The out-of-band writes that got reverted were direct edits of `settings.json` (deduced: hand edits — every in-app write path is excluded by the forensics); the writer's stale construction-time snapshot reverting those out-of-band file writes is the CONFIRMED perpetuating mechanism (two observed revert signatures with no app restart between). The fix: make `DebouncedSettingsWriter` replay queued mutators over a fresh disk load at flush time — guarded so a degraded load or a failed save can never wipe settings — so ANY out-of-band write survives; close the latent same-class direct-`SettingsStore.Save` bypasses in ModelsPage/HistoryDetailPage (they write only `CleanupModelName` and did NOT cause this outage); and log every flush (changed field names) and every flush failure so a silent revert can never again leave zero evidence.

**Architecture:** `DebouncedSettingsWriter` (src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs) today seeds an in-memory `_pending` record from `store.Load()` ONCE at construction and `Flush()` writes that whole record — any settings change made outside the writer (in production: direct edits of settings.json) is silently reverted by the next unrelated flush (classic lost-update; the window-resize handler makes the clobber near-inevitable). The fix stores the queued MUTATOR FUNCTIONS instead of a materialized record; at flush time it re-loads fresh from disk via a new ADDITIVE `SettingsStore.TryLoadCurrent(out AppSettings)` — which returns false when the file exists but cannot be read, in which case the flush is SKIPPED and the mutators kept, so a degraded load can never become the base of a full-file rewrite — applies the queued mutators in order (a throwing mutator is dropped; the rest of the batch still applies), and saves (a failed save re-queues the batch for retry). Fields untouched by any queued mutator always round-trip from disk, so ANY out-of-band write survives. The two App-layer direct-save bypasses — latent same-class hazards, NOT this outage's trigger — are routed through the shared writer so there is ONE runtime write authority (the boot-time validity repair in AppShell, which runs before the writer exists, is the single documented exception). An optional `ILogger` on the writer emits one INF line per successful flush naming the fields that changed (names only — never values) plus WRN/ERR lines for every failure path (degraded-load skip, dropped mutator, failed save) — all log calls emitted OUTSIDE the writer's lock.

**Tech Stack:** C# 13 / .NET 9 (`net9.0`), xUnit v3 (in-process runner via `dotnet exec`, NEVER `dotnet test`), Shouldly, Microsoft.Extensions.Logging over Serilog. Core is Linux-testable; the five App-layer files are WinUI, Windows-gate-only.

## Global Constraints

- Work ONLY in the worktree `/home/dan/code/winpepper/.worktrees/settings-lost-update` (branch `fix/settings-lost-update`). Never touch the main checkout's working tree (it has other sessions' uncommitted work).
- TDD is MANDATORY and the user explicitly ordered: write the FAILING red test first (`Flush_PreservesChangesWrittenOutsideTheWriter`), observe it fail against current code, record the verbatim red output in the evidence doc BEFORE fixing.
- `./scripts/linux-tests.sh` must print `LINUX SUITE: GREEN` before EVERY commit (Linux SDK at `/home/dan/code/winpepper/.dotnet`; the script defaults `DOTNET_ROOT` there). Baseline ~1131 tests; other sessions may have moved it — measure at the fork point (Task 1 Step 0) and record it.
- Tests run via `dotnet exec <built test dll>` (xUnit v3), NEVER `dotnet test`.
- Windows pre-push gate: `./scripts/windows-gate.sh` from WSL, foreground, 45-minute timeout, must print `GATE: GREEN`. BEFORE running it, poll until no `dotnet.exe` with `winpepper` in its command line exists on the host (`powershell.exe Get-CimInstance Win32_Process`; two consecutive zero counts 45 s apart) — concurrent sessions cause UNC build races.
- Cross-OS hygiene: `rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj` before switching build sides (Linux ↔ Windows).
- The user's Winpepper app is RUNNING on the host with real data: never install, launch, kill, or write to `C:\Users\dan\AppData\Local\winpepper` (read-only inspection ok).
- Nothing is pushed. All work stays on the local branch.
- Preserve the writer's existing public API: `DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)` positional compatibility (new params appended last with defaults), `Queue` / `FlushAsync` / `QueueAndFlushAsync` semantics, debounce/coalescing behavior, dispose-flush. `ISettingsWriter` must not change.
- Existing tests must keep passing unmodified unless they pin the buggy behavior — one incidental pin exists and is reported: see "Reported existing-test adjustment" below.
- Content-free logging (repo rule): log field NAMES, counts, opaque ids — never values (settings carry user content, e.g. `CleanupCustomPrompt`).
- README.md is the only end-user markdown doc; this plan and its evidence file under `docs/plans/` are working/agent docs (matching the existing `*-evidence.md` convention in that directory).
- Commits are focused and atomic; conventional-commit style messages.

## Reported existing-test adjustment (spec: "report if any do")

`tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs::Queue_Coalesces_Bursts_Into_One_Write` queues 20 mutators from a `for` loop as `writer.Queue(s => s with { MicDeviceId = $"dev{i}" })`. In C#, a `for`-loop variable is a SINGLE variable shared by all closures. Today the mutator executes eagerly inside `Queue()`, so each call reads the current `i` (dev0..dev19). Under mutator-replay the mutators execute at FLUSH time, when `i == 20`, so every deferred mutator would produce `"dev20"` and the assert `latest.ShouldBe("dev19")` fails. The test's INTENT (bursts coalesce; the last queued write wins) is not the bug being fixed — the failure is an incidental pin of eager-capture timing via a C# closure quirk. Task 1 Step 5 makes the minimal, intent-preserving adjustment (capture a per-iteration local) with an explanatory comment, and the Task 1 commit message calls it out. No other existing test pins the buggy behavior (verified during planning: every other settings test uses single mutators capturing loop-free constants, and all of them construct stores over non-existent temp paths, so the stale-snapshot vector never mattered to them).

## Scope check

One subsystem (settings persistence) plus a mechanical App-layer call-site swap that depends on it — a single plan. End-to-end proof is the red regression test itself: it reproduces the production sequence's shape (an out-of-band settings.json write — in production a direct file edit flipping `cleanupEnabled` — then an unrelated resize write) against the real `SettingsStore` + real file I/O, no mocks.

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Core/Settings/SettingsStore.cs` | Modify (additive: one new method, `Load()` untouched) | `TryLoadCurrent(out AppSettings)` — degraded-load-aware read the writer's flush uses as its rewrite base |
| `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` | Rewrite (stays one file) | Debounced write authority: queue mutators, replay over fresh guarded load at flush, contain mutator/save failures, log changed field names + failure paths |
| `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs` | Extend (add 8 facts + 1 fake logger, adjust 1 closure) | All writer behavior: red regression, ordering, mutator-wins, dispose-preserves, coalescing, degraded-load skip, throwing-mutator containment, change + failure logging |
| `src/Winpepper.App/Views/ModelsPage.xaml.cs` | Edit lines 43–47 and 198–209 | Route `promoteCleanup` through the shared writer; capture toggle state into locals before queueing (Windows-only) |
| `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs` | Edit lines 70–74 | Route `promoteCleanupDefault` through the shared writer (Windows-only) |
| `src/Winpepper.App/Views/RecordingPage.xaml.cs` | Edit line 86 | Capture `AutostartToggle.IsOn` into a local before queueing (Windows-only) |
| `src/Winpepper.App/Hosting/AppShell.cs` | Edit lines ~83, ~95 | Document the boot-repair exception; wire the flush logger (Windows-only) |
| `src/Winpepper.App/Views/MainWindow.xaml.cs` | NO change (verify only) | Already uses the writer; it is the clobber trigger the red test simulates |
| `docs/plans/2026-07-26-settings-lost-update-evidence.md` | Create | Verbatim red output, baseline count, suite/gate results |

Unchanged by design: `ISettingsWriter.cs` (public contract), `AppSettings.cs`, `CleanupSettingsPersistenceTests.cs` (SettingsStore.cs gains one ADDITIVE method — `TryLoadCurrent` — while `Load()` behavior is unchanged for every existing caller, so `CleanupSettingsPersistenceTests` and all other store consumers are unaffected).

## Environment prelude (used by every task)

All commands run from the worktree root:

```bash
cd /home/dan/code/winpepper/.worktrees/settings-lost-update
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
```

Build + run just the Core test project (fast inner loop; the full suite script does the same across all 9 projects):

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -method "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.<TestName>"
```

(If `-method` is not recognized by the runner, run the dll with no filter and read the named test's result from the output — the suite is fast.)

Full Linux suite (before every commit; allow up to 30 minutes):

```bash
./scripts/linux-tests.sh    # must end with: LINUX SUITE: GREEN
```

---

### Task 1: Red regression test + mutator-replay flush

**Files:**
- Modify: `src/Winpepper.Core/Settings/SettingsStore.cs` (ADDITIVE: one new method `TryLoadCurrent`; `Load()` untouched)
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` (full rewrite of the class body)
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs` (add 1 fact, adjust 1 closure)
- Create: `docs/plans/2026-07-26-settings-lost-update-evidence.md`

**Interfaces:**
- Consumes: existing `SettingsStore.Save(AppSettings)`, `AppSettings` record (`CleanupModelName`, `CleanupEnabled`, `WindowWidth`, `MicDeviceId` properties), `ISettingsWriter` (unchanged).
- Produces: `SettingsStore.TryLoadCurrent(out AppSettings)` (Step 3a — additive; returns false when the file exists but cannot be read) and `DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)` whose private `Flush()` drains a `List<Func<AppSettings, AppSettings>> _pendingMutators`, re-loads from disk via `TryLoadCurrent` (skipping the flush and KEEPING the batch on a degraded load), applies mutators in order (a throwing mutator is dropped, the rest apply), saves (a failed save re-queues the batch at the front). Task 3 extends the ctor; Task 4 swaps App call sites onto the unchanged `QueueAndFlushAsync(Func<AppSettings, AppSettings>)`.

- [ ] **Step 0: Record the fork-point baseline**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0, output ends with `linux-tests grand total: N tests` then `LINUX SUITE: GREEN` (N was ~1131 recently; record the actual number). Create the evidence file:

```markdown
# Settings lost-update fix — evidence

Branch: fix/settings-lost-update, forked from 100d33c.

## Fork-point baseline (before any change)
- `./scripts/linux-tests.sh`: LINUX SUITE: GREEN, grand total: <N> tests (verbatim last 3 lines pasted below)

<paste the verbatim last 3 lines of the run here>

## Red test (Task 1) — verbatim failure against HEAD, BEFORE the fix
<filled in at Step 2>
```

Save as `docs/plans/2026-07-26-settings-lost-update-evidence.md`.

- [ ] **Step 1: Write the failing test**

Append inside the existing `DebouncedSettingsWriterTests` class in `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs` (it already has the `_path` temp-file fixture and `using Shouldly; using Winpepper.Core.Settings; using Xunit;`):

```csharp
    [Fact]
    public async Task Flush_PreservesChangesWrittenOutsideTheWriter()
    {
        // The shape of the production outage that lost 327 dictations
        // (7/25-7/26):
        // 1. app boots (writer constructed over the settings file),
        // 2. settings.json changes OUT-OF-BAND -- in production this was a
        //    direct edit of the file flipping cleanupEnabled (all in-app
        //    write paths were excluded by the forensics; the in-app
        //    ModelsPage/HistoryDetailPage direct saves are latent same-class
        //    bugs, closed in Task 4, but were NOT this outage's writer) --
        //    modeled here as a direct SettingsStore.Save of two fields,
        // 3. an UNRELATED write (MainWindow resize) flushes the writer,
        //    whose stale construction-time snapshot reverts step 2. That
        //    revert is the CONFIRMED perpetuating mechanism (two observed
        //    revert signatures with no app restart between).
        // The out-of-band changes must survive step 3: replay over a fresh
        // load survives ANY out-of-band write.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        using var writer = new DebouncedSettingsWriter(store); // HEAD snapshots disk here

        store.Save(store.Load() with
        {
            CleanupModelName = "promoted-model",
            CleanupEnabled = false
        }); // out-of-band write (same class as a hand edit of settings.json, or the latent ModelsPage:46 / HistoryDetailPage:73 bypasses)

        await writer.QueueAndFlushAsync(s => s with { WindowWidth = 999 }); // MainWindow resize

        var final = store.Load();
        final.CleanupModelName.ShouldBe("promoted-model"); // FAILS at HEAD
        final.CleanupEnabled.ShouldBeFalse();              // FAILS at HEAD
        final.WindowWidth.ShouldBe(999);                   // passes at HEAD
    }
```

- [ ] **Step 2: Run the test, verify it FAILS, record the verbatim red output**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -method "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.Flush_PreservesChangesWrittenOutsideTheWriter"
```

Expected: FAIL on the first assert — Shouldly output of the shape
`final.CleanupModelName should be "promoted-model" but was "qwen2.5-0.5b-instruct-q4_k_m"`
(the writer's stale boot snapshot clobbered the out-of-band write). Paste the VERBATIM failure block (test name, Shouldly message, `Failed: 1` summary line) into the `## Red test` section of `docs/plans/2026-07-26-settings-lost-update-evidence.md`. Do NOT proceed to Step 3 until the failure is observed and recorded. If it unexpectedly PASSES, STOP — the premise is wrong; report instead of fixing.

- [ ] **Step 3: Add the degraded-load guard to SettingsStore, then implement the mutator-replay writer**

3a. **ADDITIVE SettingsStore change.** In `src/Winpepper.Core/Settings/SettingsStore.cs`, add the following method after `Load()` (before `BackupCorruptFile`). It is the writer's flush-time read: it must answer "does this value legitimately reflect the CURRENT disk state?" — because under mutator-replay every flush is a full-file rewrite, and a degraded read must never become the base of one (run-proven during planning: corrupt JSON → `Load()` quarantines to `.bad-*` and returns defaults → a snapshot-based flush would persist all-defaults; transient IOException → `Load()` returns stale `_lastGood` or defaults → flush persists stale; `UnauthorizedAccessException` is uncaught by `Load()` → flush throws). `Load()` itself is UNTOUCHED — every existing caller keeps its exact behavior.

```csharp
    /// <summary>
    /// Like <see cref="Load"/>, but reports whether the out value
    /// legitimately reflects the CURRENT state of the file on disk. Returns
    /// true when the file parsed OK; also true when the file is missing
    /// (defaults ARE the current state) or corrupt (the content is
    /// quarantined to a .bad-* backup first, so defaults are then the
    /// current state). Returns false when the file EXISTS but could not be
    /// READ (persistent IOException, UnauthorizedAccessException) — the out
    /// value is then a last-known/default fallback that must NOT be used as
    /// the base of a full-file rewrite.
    /// </summary>
    public bool TryLoadCurrent(out AppSettings settings)
    {
        // Deliberately NO File.Exists pre-check (unlike Load): File.Exists
        // is false for a path occupied by a directory, which would disguise
        // an unreadable path as "missing → defaults". Read directly and
        // classify by exception: truly-absent paths throw
        // FileNotFoundException / DirectoryNotFoundException — both
        // IOException SUBCLASSES, so they must be caught BEFORE the general
        // IOException handlers below.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                _lastGood = loaded;
                settings = loaded;
                return true;
            }
            catch (FileNotFoundException)
            {
                settings = new AppSettings(); // no file: defaults ARE current
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                settings = new AppSettings(); // no parent dir yet (first run)
                return true;
            }
            catch (JsonException ex)
            {
                // Same quarantine as Load(): the corrupt content is preserved
                // in a .bad-* backup, so defaults are now the legitimate
                // current state and a rewrite cannot destroy evidence.
                BackupCorruptFile(ex);
                settings = new AppSettings();
                return true;
            }
            catch (IOException) when (attempt < 2)
            {
                // Same transient share/replace retry as Load().
                Thread.Sleep(15);
            }
            catch (IOException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed ({ex.Message}); skipping settings flush rather than rewriting from a stale base.");
                settings = _lastGood ?? new AppSettings();
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed ({ex.Message}); skipping settings flush rather than rewriting from a stale base.");
                settings = _lastGood ?? new AppSettings();
                return false;
            }
        }
    }
```

(Note for the implementer: a directory sitting at the settings path throws `UnauthorizedAccessException` from `File.ReadAllText` on both Windows and Linux under .NET 9 — verified on Linux during planning; if a runtime ever surfaces it as `IOException` instead, the catch set above still returns false. Task 2 pins this with a test.)

3b. Replace the entire contents of `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` with:

```csharp
namespace Winpepper.Core.Settings;

/// <summary>
/// Debounces settings writes. Queued mutations are stored as mutator
/// FUNCTIONS and applied over a FRESH disk load at flush time
/// (read-modify-write), so fields no queued mutator touches always
/// round-trip from disk. This is the fix for the 2026-07 lost-update bug:
/// the previous implementation snapshotted the whole AppSettings record at
/// construction and rewrote it wholesale on every flush, silently reverting
/// any write made outside this instance (e.g. a direct SettingsStore.Save).
/// Mutators therefore execute at flush time, not queue time — a queued
/// mutator is "newer intent" and wins over an out-of-band write to the
/// same field.
/// </summary>
public sealed class DebouncedSettingsWriter : ISettingsWriter, IDisposable
{
    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private readonly List<Func<AppSettings, AppSettings>> _pendingMutators = new();
    private CancellationTokenSource? _cts;
    private Task? _scheduled;

    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
    }

    public void Queue(Func<AppSettings, AppSettings> mutator)
    {
        lock (_lock)
        {
            _pendingMutators.Add(mutator);
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _scheduled = Task.Run(async () =>
            {
                try { await Task.Delay(_delay, token); }
                catch (OperationCanceledException) { return; }
                Flush();
            });
        }
    }

    public async Task FlushAsync()
    {
        Task? t;
        lock (_lock) { t = _scheduled; _cts?.Cancel(); }
        // Deliberately NO ConfigureAwait(false): resuming on the captured
        // context keeps the final Flush() — and therefore the mutators — on
        // the calling (UI) thread. Trade-off: FlushAsync().Wait() under a
        // sync context would deadlock; documented, and no production caller
        // blocks on FlushAsync (all await or discard with `_ =`).
        if (t is not null) { try { await t; } catch { } }
        Flush();
    }

    public async Task QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        Queue(mutator);
        await FlushAsync();
    }

    private void Flush()
    {
        // The whole read-modify-write runs under _lock: concurrent flushes
        // serialize (the old code called Save outside the lock, so two
        // flushes could write whole files out of order), and a Queue()
        // racing a flush lands either before the drain (applied now) or
        // after (applied on its own debounce tick) — never lost. Monitor
        // locks are reentrant, so a mutator cannot deadlock this. Measured
        // lock hold is fsync-dominated: median 13.3 ms, p95 22 ms.
        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;

            // Degraded-load guard: if the settings file exists but cannot be
            // READ right now (transient IOException, UnauthorizedAccess),
            // its fallback value must not become the base of a full-file
            // rewrite — skip this flush and KEEP the pending mutations; they
            // retry on the next flush (or the Dispose flush). Missing and
            // corrupt files DO load (defaults are then the legitimate
            // current state; corrupt content is quarantined to a .bad-*
            // backup by the store). Task 3 adds the WRN log line for this.
            if (!_store.TryLoadCurrent(out var settings)) return;

            var mutators = _pendingMutators.ToArray();
            _pendingMutators.Clear();

            foreach (var mutator in mutators)
            {
                // A throwing mutator is DROPPED; the rest of the batch still
                // applies — one bad lambda must not destroy sibling changes.
                // Task 3 adds the ERR log line for this.
                try { settings = mutator(settings); }
                catch { }
            }

            try
            {
                _store.Save(settings);
            }
            catch
            {
                // Save failed (e.g. a Windows sharing violation on the
                // atomic rename): re-insert the drained batch at the FRONT
                // so order is preserved and it retries on the next flush. A
                // Dispose-time flush that fails here gives up without
                // throwing (the app is exiting). Task 3 adds the WRN log
                // line for this.
                _pendingMutators.InsertRange(0, mutators);
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
```

Notes for the implementer:
- The constructor no longer calls `store.Load()` — the writer holds NO settings snapshot, ever.
- `_dirty` is gone; "dirty" is now `_pendingMutators.Count > 0`.
- `Queue`/`FlushAsync`/`QueueAndFlushAsync`/`Dispose` signatures and debounce scheduling are byte-identical to HEAD; only the storage/flush strategy changed.
- `Flush()` reads via `TryLoadCurrent` (Step 3a), never `Load()`: on a degraded load it returns with `_pendingMutators` INTACT — never clobber the file from a degraded base; the mutations retry on the next flush or at Dispose.
- `Flush()` cannot throw from a bad mutator or a failed `Save` — a throwing mutator is dropped (siblings still apply), a failed `Save` re-queues the drained batch at the front. Task 1 is deliberately logger-free; ALL flush logging (including these failure paths) arrives in Task 3.
- Do NOT add `ConfigureAwait(false)` to `FlushAsync` — it would move mutator execution off the calling UI thread. The known trade-off (`FlushAsync().Wait()` under a sync context deadlocks) is documented in the code; no production caller does it.

- [ ] **Step 4: Run the red test, verify it now PASSES**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -method "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.Flush_PreservesChangesWrittenOutsideTheWriter"
```

Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Run the whole Core test dll; apply the ONE reported closure adjustment**

```bash
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: exactly one failure — `Queue_Coalesces_Bursts_Into_One_Write` with `latest should be "dev19" but was "dev20"` (the shared `for`-loop closure; see "Reported existing-test adjustment" at the top of this plan). Fix it minimally, preserving the test's intent (bursts coalesce, last write wins), by editing lines 21–22 of `DebouncedSettingsWriterTests.cs` from:

```csharp
        for (var i = 0; i < 20; i++)
            writer.Queue(s => s with { MicDeviceId = $"dev{i}" });
```

to:

```csharp
        for (var i = 0; i < 20; i++)
        {
            // Mutators now execute at FLUSH time (mutator-replay fix), and a
            // for-loop variable is a single shared variable — without this
            // per-iteration copy every deferred mutator would read i == 20.
            var id = $"dev{i}";
            writer.Queue(s => s with { MicDeviceId = id });
        }
```

Re-run the dll: expected ALL PASS (`Failed: 0`; the Core.Tests count was 379 at the last recorded snapshot, now +1 for the red test — record the actual number). If ANY other test fails, stop and fix the implementation — no other test modification is authorized.

- [ ] **Step 6: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, grand total = baseline + 1.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Settings/SettingsStore.cs \
        src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs \
        docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "fix(settings): mutator-replay flush — out-of-band settings writes survive unrelated flushes

Red test Flush_PreservesChangesWrittenOutsideTheWriter observed FAILING
against HEAD before the fix (verbatim output in
docs/plans/2026-07-26-settings-lost-update-evidence.md): the writer's
construction-time snapshot clobbered any out-of-band settings.json
write on the next unrelated flush — the mechanism that silently
reverted cleanupEnabled for 327 dictations on 7/25–7/26 (the reverted
writes were direct file edits; the in-app direct-save bypasses are
latent same-class bugs, closed in a later commit).

Fix: DebouncedSettingsWriter stores queued mutator FUNCTIONS and, at
flush, re-loads fresh from disk via the new ADDITIVE
SettingsStore.TryLoadCurrent, applies them in order, then Saves.
Degraded-load guard: when the settings file exists but cannot be read,
the flush is skipped and the mutations kept for retry — a degraded
load can never poison a full-file rewrite. Exception containment: a
throwing mutator is dropped while the rest of the batch still applies;
a failed Save re-queues the batch for retry. Whole read-modify-write
is under the lock (also fixes the old Save-outside-lock flush reorder
race). Public API unchanged; Load() unchanged for existing callers.

Reported test adjustment: Queue_Coalesces_Bursts_Into_One_Write
captured the shared for-loop variable in its mutator closures — an
incidental pin of eager-capture timing, not of coalescing intent;
adjusted to a per-iteration copy."
```

---

### Task 2: Pin the mutator-replay semantics (ordering, mutator-wins, dispose-preserves, degraded-load skip, throwing-mutator containment)

**Files:**
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: Task 1's writer — `Queue(Func<AppSettings, AppSettings>)`, `FlushAsync()`, `QueueAndFlushAsync(Func<AppSettings, AppSettings>)`, `Dispose()`; `SettingsStore.Load()/Save` and (indirectly, through the writer's flush) `TryLoadCurrent`.
- Produces: nothing new — regression pins only. (Coalescing is already pinned by the existing `Queue_Coalesces_Bursts_Into_One_Write`, which Task 1 kept green.)

- [ ] **Step 1: Write the five semantics tests**

Append inside the same test class (note: the class's `Dispose` only deletes a FILE at `_path`; the two tests that create a DIRECTORY at `_path` clean it up themselves in a `finally`):

```csharp
    [Fact]
    public async Task Flush_Applies_Queued_Mutators_In_Order()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "a" });
        writer.Queue(s => s with { MicDeviceId = s.MicDeviceId + "b" });
        await writer.FlushAsync();
        new SettingsStore(_path).Load().MicDeviceId.ShouldBe("ab");
    }

    [Fact]
    public async Task Flush_QueuedMutator_Wins_Over_OutOfBand_Write_On_The_Same_Field()
    {
        // A queued mutator is newer intent than an out-of-band write to the
        // SAME field: replay-on-fresh-load applies it last, so it wins.
        // Accepted edge (verified during planning): a hand edit of
        // settings.json landing during the <=400 ms debounce window loses a
        // same-field conflict to the queued mutator — after Task 4 no in-app
        // runtime conflict pair exists at all, so this pins the writer's
        // deterministic replay contract, not a live product conflict.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        store.Save(store.Load() with { CleanupModelName = "out-of-band" });
        await writer.QueueAndFlushAsync(s => s with { CleanupModelName = "queued-wins" });
        new SettingsStore(_path).Load().CleanupModelName.ShouldBe("queued-wins");
    }

    [Fact]
    public void Dispose_Flush_Preserves_OutOfBand_Changes()
    {
        // App shutdown flushes via Dispose (AppShell.cs:580). At HEAD that
        // alone clobbered a direct save even with no intervening toggle.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { WindowWidth = 777 });
        store.Save(store.Load() with { CleanupEnabled = false }); // out-of-band, after queueing
        writer.Dispose(); // synchronous flush

        var final = new SettingsStore(_path).Load();
        final.WindowWidth.ShouldBe(777);
        final.CleanupEnabled.ShouldBeFalse();
    }

    [Fact]
    public async Task Flush_SkipsAndKeepsMutations_WhenSettingsFileIsUnreadable()
    {
        // A DIRECTORY at the settings path is a deterministic, cross-platform
        // "file exists but cannot be read": File.ReadAllText on it throws
        // UnauthorizedAccessException on both Windows and Linux under .NET 9
        // (and TryLoadCurrent also returns false for a persistent
        // IOException, should a runtime surface it that way). Note
        // File.Exists is FALSE for a directory — which is exactly why
        // TryLoadCurrent reads without Load()'s File.Exists pre-check.
        // The flush must SKIP (write nothing) and KEEP the queued mutation.
        var store = new SettingsStore(_path);
        Directory.CreateDirectory(_path);
        try
        {
            using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
            writer.Queue(s => s with { MicDeviceId = "kept-mutation" });
            await writer.FlushAsync(); // degraded load -> flush skipped

            Directory.Exists(_path).ShouldBeTrue(); // still the directory
            File.Exists(_path).ShouldBeFalse();     // nothing was written

            Directory.Delete(_path);                // path is healthy again
            store.Save(new AppSettings { WindowWidth = 555 });

            await writer.FlushAsync();              // the KEPT mutation now applies

            var final = new SettingsStore(_path).Load();
            final.MicDeviceId.ShouldBe("kept-mutation");
            final.WindowWidth.ShouldBe(555); // applied over the fresh valid file
        }
        finally
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    [Fact]
    public async Task Flush_AppliesRemainingMutators_WhenOneThrows()
    {
        // One bad lambda must not destroy sibling changes: the thrower is
        // DROPPED, the rest of the batch still applies, and the writer
        // stays usable. (Task 3 adds the ERR log line for the drop.)
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "x-applied" });          // m1: field X
        writer.Queue(s => throw new InvalidOperationException("boom"));   // m2: dropped
        writer.Queue(s => s with { WindowWidth = 424 });                  // m3: field Y — must still apply
        await writer.FlushAsync();

        var final = new SettingsStore(_path).Load();
        final.MicDeviceId.ShouldBe("x-applied");
        final.WindowWidth.ShouldBe(424);

        // Writer still usable after a throwing mutator.
        await writer.QueueAndFlushAsync(s => s with { WindowHeight = 200 });
        new SettingsStore(_path).Load().WindowHeight.ShouldBe(200);
    }
```

Save-failure re-queue (the third failure path in Task 1's `Flush`) gets NO dedicated fact: a portable deterministic test is impractical — making `SettingsStore.Save` fail while the Load succeeds requires a sharing-violation on `AtomicFile`'s `File.Move(tmp, path, overwrite: true)` rename, which run-testing during planning proved does NOT fail on Linux (rename(2) ignores advisory file locks), and any filesystem obstruction that DOES fail portably (directory at the path, permissions) also degrades the Load, which routes to the skip path instead. The re-queue path is covered by code review of Task 1 Step 3b plus the Windows gate compile in Task 4.

- [ ] **Step 2: Run the Core test dll**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: ALL PASS (Task 1's implementation already provides these semantics — the plan's existing convention; `Dispose_Flush_Preserves_OutOfBand_Changes` would have been red at HEAD, and the degraded-load and throwing-mutator facts pin the Task 1 Step 3 guards). If any of the five fails, the Task 1 implementation has a bug — fix `DebouncedSettingsWriter.cs`/`SettingsStore.cs`, not the tests, and re-run.

- [ ] **Step 3: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, grand total = baseline + 6.

- [ ] **Step 4: Commit**

```bash
git add tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
git commit -m "test(settings): pin mutator-replay semantics — ordering, queued-mutator-wins, dispose flush preserves out-of-band writes, degraded-load skip keeps mutations, throwing mutator dropped without destroying the batch"
```

---

### Task 3: Flush observability — log the names of changed fields and every flush failure path

**Files:**
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.Logging.ILogger` (already a `Winpepper.Core` package ref; flows to the test project transitively via the `Winpepper.Core` ProjectReference).
- Produces: ctor `DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null, ILogger? log = null)` — appended last with a default, so every existing positional `(store)` / `(store, TimeSpan)` call keeps compiling. ALL flush logging lands in this task: (a) INF changed-field names per successful flush; (b) WRN when a flush is skipped on a degraded load (kept-pending COUNT only); (c) ERR when a queued mutator throws (the exception — never settings values); (d) WRN when Save fails and the batch is re-queued (exception + count). Log payloads are captured under the lock; all log CALLS are emitted OUTSIDE the lock (measured during planning: a 250 ms logger sink inside the lock blocked a concurrent `Queue()` for 236 ms). Task 4 wires the App's logger through the ctor.

- [ ] **Step 1: Write the failing tests (fail to compile — the ctor has no logger yet)**

Append inside the test class — a hand-rolled fake logger (repo convention: no mocking framework) plus the two facts. Add `using Microsoft.Extensions.Logging;` to the top of `DebouncedSettingsWriterTests.cs`:

```csharp
    [Fact]
    public async Task Flush_Logs_The_Names_Of_Changed_Fields()
    {
        // The 327-dictation outage produced ZERO settings-write log
        // evidence. Every flush must name the fields that changed —
        // names only, never values (content-free logging rule).
        var store = new SettingsStore(_path);
        var log = new ListLogger();
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30), log);

        await writer.QueueAndFlushAsync(s => s with { MicDeviceId = "dev-a", CleanupEnabled = false });

        var line = log.Lines.ShouldHaveSingleItem();
        line.ShouldStartWith("Information:");
        line.ShouldContain("MicDeviceId");
        line.ShouldContain("CleanupEnabled");
        line.ShouldNotContain("dev-a"); // field NAMES only — never values
    }

    [Fact]
    public async Task Flush_Logs_Warning_When_DegradedLoad_SkipsFlush()
    {
        // A skipped flush must not be silent either: WRN with the
        // kept-pending COUNT only — never settings values. Same
        // directory-at-the-settings-path trick as Task 2's
        // Flush_SkipsAndKeepsMutations_WhenSettingsFileIsUnreadable.
        var store = new SettingsStore(_path);
        var log = new ListLogger();
        Directory.CreateDirectory(_path); // path exists but is unreadable as a file
        try
        {
            using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30), log);
            await writer.QueueAndFlushAsync(s => s with { MicDeviceId = "dev-x" });

            var line = log.Lines.ShouldHaveSingleItem();
            line.ShouldStartWith("Warning:");
            line.ShouldContain("keeping 1 pending mutation"); // COUNT only
            line.ShouldNotContain("dev-x");                   // never values
        }
        finally
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }

    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add($"{logLevel}:{formatter(state, exception)}");
        }
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILURE — `CS1729: 'DebouncedSettingsWriter' does not contain a constructor that takes 3 arguments`. (A compile failure of the new test IS the red state here.)

- [ ] **Step 3: Implement the logging**

In `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`:

3a. Add at the very top of the file (before `namespace`):

```csharp
using Microsoft.Extensions.Logging;
```

3b. Add fields and replace the constructor:

```csharp
    private static readonly System.Reflection.PropertyInfo[] SettingsProperties =
        typeof(AppSettings).GetProperties();

    private readonly ILogger? _log;
```

```csharp
    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null, ILogger? log = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
        _log = log;
    }
```

(Optional-logger pattern matches `AudioEndpointWatcher`; appended-last default keeps all existing positional ctor calls — including every `(store)` / `(store, TimeSpan)` call in the tests added by Tasks 1–2 — compiling unchanged.)

3c. Replace the `Flush()` body so it captures log payloads under the lock, EMITS every log call outside the lock, and logs all three failure paths; add the diff helper:

```csharp
    private void Flush()
    {
        // The whole read-modify-write runs under _lock: concurrent flushes
        // serialize (the old code called Save outside the lock, so two
        // flushes could write whole files out of order), and a Queue()
        // racing a flush lands either before the drain (applied now) or
        // after (applied on its own debounce tick) — never lost. Monitor
        // locks are reentrant, so a mutator cannot deadlock this. Measured
        // lock hold is fsync-dominated: median 13.3 ms, p95 22 ms.
        //
        // Log payloads are CAPTURED under the lock; the log CALLS are
        // emitted after it is released — a 250 ms logger sink inside the
        // lock blocked a concurrent Queue() for 236 ms in testing.
        var skippedKeptCount = 0;
        List<Exception>? mutatorErrors = null;
        Exception? saveError = null;
        var requeuedCount = 0;
        AppSettings? loggedBefore = null;
        AppSettings? loggedAfter = null;

        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;

            // Degraded-load guard: if the settings file exists but cannot be
            // READ right now, its fallback value must not become the base of
            // a full-file rewrite — skip this flush and KEEP the pending
            // mutations for retry on the next flush (or the Dispose flush).
            if (!_store.TryLoadCurrent(out var before))
            {
                skippedKeptCount = _pendingMutators.Count;
            }
            else
            {
                var mutators = _pendingMutators.ToArray();
                _pendingMutators.Clear();

                var after = before;
                foreach (var mutator in mutators)
                {
                    // A throwing mutator is DROPPED; the rest of the batch
                    // still applies.
                    try { after = mutator(after); }
                    catch (Exception ex) { (mutatorErrors ??= new()).Add(ex); }
                }

                try
                {
                    _store.Save(after);
                    loggedBefore = before;
                    loggedAfter = after;
                }
                catch (Exception ex)
                {
                    // Save failed (e.g. a Windows sharing violation on the
                    // atomic rename): re-insert the drained batch at the
                    // FRONT so order is preserved and it retries on the next
                    // flush. A Dispose-time flush that fails here gives up
                    // without throwing (the app is exiting).
                    saveError = ex;
                    requeuedCount = mutators.Length;
                    _pendingMutators.InsertRange(0, mutators);
                }
            }
        }

        // All emission OUTSIDE the lock. Counts and exceptions only — never
        // settings values (content-free logging rule).
        if (skippedKeptCount > 0)
            _log?.LogWarning(
                "Settings flush skipped (degraded settings load); keeping {PendingCount} pending mutation(s) for retry",
                skippedKeptCount);
        if (mutatorErrors is not null)
            foreach (var ex in mutatorErrors)
                _log?.LogError(ex,
                    "A queued settings mutator threw and was dropped; remaining mutations were still applied");
        if (saveError is not null)
            _log?.LogWarning(saveError,
                "Settings save failed; re-queued {RequeuedCount} mutation(s) for retry on the next flush",
                requeuedCount);
        if (loggedBefore is not null && loggedAfter is not null)
            LogChangedFields(loggedBefore, loggedAfter);
    }

    private void LogChangedFields(AppSettings before, AppSettings after)
    {
        if (_log is null) return;
        // Reflection + Equals diffing is valid for the CURRENT AppSettings:
        // all 27 properties are scalar (int / string / bool / int?), verified
        // during planning. Revisit if a collection-typed property is ever
        // added — Equals on a rebuilt-but-equal collection would mis-diff.
        var changed = SettingsProperties
            .Where(p => !Equals(p.GetValue(before), p.GetValue(after)))
            .Select(p => p.Name)
            .ToList();
        // Field NAMES only — never values: settings can carry user content
        // (e.g. CleanupCustomPrompt); the repo's content-free logging rule.
        _log.LogInformation(
            "Settings flushed: {ChangedCount} field(s) changed: {ChangedFields}",
            changed.Count,
            changed.Count == 0 ? "(none)" : string.Join(", ", changed));
    }
```

- [ ] **Step 4: Run to verify it passes**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: build succeeds; ALL PASS including `Flush_Logs_The_Names_Of_Changed_Fields` and `Flush_Logs_Warning_When_DegradedLoad_SkipsFlush` (`Failed: 0`).

- [ ] **Step 5: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, grand total = baseline + 8.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
git commit -m "feat(settings): log changed field NAMES on every flush, plus every flush failure path

One INF line per successful flush (field names only — content-free
logging rule). The 07/25 cleanupEnabled revert left zero log evidence
across 327 dictations; a silent settings change is now impossible.
Failure paths log too: WRN on a degraded-load skip (kept-pending count
only), ERR when a queued mutator throws (exception, never values), WRN
when Save fails and the batch is re-queued (exception + count). All
log calls are emitted OUTSIDE the writer lock so a slow sink cannot
block a concurrent Queue()."
```

---

### Task 4: Single write authority — route App direct-saves through the writer, capture UI state before queueing, wire the logger, run the Windows gate

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs:43-47` (promoteCleanup) and `:198-209` (toggle-state capture)
- Modify: `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs:70-74` (and its now-unused `settings` local, line ~56)
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs:86` (toggle-state capture)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:~83` (comment only) and `:~95` (logger wiring)
- Verify only (no change): `src/Winpepper.App/Views/MainWindow.xaml.cs`
- Modify: `docs/plans/2026-07-26-settings-lost-update-evidence.md` (append audit + gate results)

**Interfaces:**
- Consumes: `shell.SettingsWriter` (`AppShell.cs:23`, type `DebouncedSettingsWriter`), `QueueAndFlushAsync(Func<AppSettings, AppSettings>)` from Task 1, the `ILogger? log` ctor param from Task 3, `factory.CreateLogger("Winpepper.App.Settings")` (the category AppShell already uses for settings warnings at `AppShell.cs:72-73`).
- Produces: ONE runtime write authority. After this task the only `SettingsStore.Save` callers in `src/` are the writer's own `Flush()` and the documented boot-time repair in `AppShell.Create()`.

**IMPORTANT — these files do NOT compile on Linux.** `Winpepper.App.csproj` is single-TFM `net9.0-windows10.0.19041.0` (WinUI) and `scripts/linux-tests.sh` never builds it. Type-checking happens ONLY in the Windows gate (Step 7). Hand-verify each edit character-by-character against the code blocks below, and note this limitation explicitly in the evidence doc for the review gate.

- [ ] **Step 1: ModelsPage — route `promoteCleanup` through the writer; capture toggle state before queueing**

1a. In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, replace (currently lines 43–47):

```csharp
            promoteCleanup: name =>
            {
                var cur = settings.Load();
                settings.Save(cur with { CleanupModelName = name });
            },
```

with (mirroring the adjacent `promoteAsr` lambda at lines 37–42, which is the page's established writer pattern):

```csharp
            promoteCleanup: name =>
            {
                var shell = App.Shell!;
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            },
```

The `settings` local (line 29) is still used at line 30 (`var s = settings.Load();`) — keep it.

1b. Still in `src/Winpepper.App/Views/ModelsPage.xaml.cs`, three mutator lambdas read live WinUI control state (`.IsOn`) INSIDE the lambda. Under mutator-replay, mutators execute at FLUSH time — on a threadpool thread if a debounce tick races the caller's flush — and WinUI controls are thread-affine (cross-thread access throws). Capture the state into a local BEFORE queueing, restoring HEAD's read-at-queue-time semantics. Replace (currently lines 198–209):

```csharp
        // Retention + keyterms toggles.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = AssemblyAiDeleteToggle.IsOn });
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = AssemblyAiKeytermsToggle.IsOn });

        // Streaming toggle (provider-agnostic; read LIVE per dictation by PipelineHost).
        StreamingToggle.IsOn = current.StreamingEnabled;
        StreamingToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { StreamingEnabled = StreamingToggle.IsOn });
```

with:

```csharp
        // Retention + keyterms toggles. Mutators execute at FLUSH time under
        // the mutator-replay writer (possibly on a threadpool thread if a
        // debounce tick races), and WinUI controls are thread-affine — so
        // capture IsOn into a local NOW, on the UI thread, and let the
        // lambda close over the local.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
        {
            var isOn = AssemblyAiDeleteToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = isOn });
        };
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
        {
            var isOn = AssemblyAiKeytermsToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = isOn });
        };

        // Streaming toggle (provider-agnostic; read LIVE per dictation by PipelineHost).
        StreamingToggle.IsOn = current.StreamingEnabled;
        StreamingToggle.Toggled += (_, _) =>
        {
            var isOn = StreamingToggle.IsOn;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { StreamingEnabled = isOn });
        };
```

- [ ] **Step 2: Route `promoteCleanupDefault` in HistoryDetailPage through the writer**

In `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`, replace (currently lines 70–74):

```csharp
            promoteCleanupDefault: name =>
            {
                var s = settings.Load();
                settings.Save(s with { CleanupModelName = name });
            });
```

with:

```csharp
            promoteCleanupDefault: name =>
            {
                var shell = App.Shell!;
                _ = shell.SettingsWriter.QueueAndFlushAsync(s2 => s2 with { CleanupModelName = name }); // durability
            });
```

Then check whether the `settings` local is now unused:

```bash
grep -n "settings" src/Winpepper.App/Views/HistoryDetailPage.xaml.cs
```

If its ONLY remaining mention is the declaration `var settings = App.Shell!.SettingsStore;` (line ~56), delete that declaration line (the repo builds with analyzers on; an unused local invites an IDE0059 warning). If it has other uses, leave it.

- [ ] **Step 3: RecordingPage — capture the autostart toggle state before queueing**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, inside the `AutostartToggle.Toggled` handler, the mutator at line 86 reads `AutostartToggle.IsOn` inside the lambda — the fourth and last UI-state-reading mutator. Replace the tail of the handler (currently lines 83–87):

```csharp
                _shell.Autostart.Enable(exe, "--tray");
            }
            else _shell.Autostart.Disable();
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = AutostartToggle.IsOn });
        };
```

with:

```csharp
                _shell.Autostart.Enable(exe, "--tray");
            }
            else _shell.Autostart.Disable();
            // Capture on the UI thread: mutators execute at FLUSH time and
            // WinUI controls are thread-affine (see the ModelsPage toggles).
            var isOn = AutostartToggle.IsOn;
            _ = _shell.SettingsWriter.QueueAndFlushAsync(s => s with { AutostartEnabled = isOn });
        };
```

(Only line 86 changes plus the comment; the `if (AutostartToggle.IsOn)` read at line 73 already executes on the UI thread at handler time and needs no change.)

- [ ] **Step 4: Document the boot-repair exception and wire the flush logger in AppShell**

In `src/Winpepper.App/Hosting/AppShell.cs`:

4a. Immediately above `store.Save(settings);` (currently line 83, inside the unknown-ASR-model repair `if` block), add:

```csharp
            // Boot-time validity repair — the ONE sanctioned direct save:
            // it runs before the DebouncedSettingsWriter exists (below),
            // and the writer re-loads from disk at every flush, so this
            // can neither clobber nor be clobbered. ALL runtime settings
            // writes go through SettingsWriter (single write authority).
```

4b. Replace the writer construction (currently line 95):

```csharp
        var writer = new DebouncedSettingsWriter(store);
```

with:

```csharp
        var writer = new DebouncedSettingsWriter(store,
            log: factory.CreateLogger("Winpepper.App.Settings"));
```

(Named argument skips the `delay` parameter; `"Winpepper.App.Settings"` is the category AppShell already uses for settings warnings earlier in `Create` at AppShell.cs:72–73.)

- [ ] **Step 5: Write-authority audit**

```bash
grep -rn "\.Save(" --include="*.cs" src/
```

Expected output — exactly these settings-related hits and NOTHING else touching `SettingsStore`:
- `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`: the writer's own `_store.Save(after);` (the single runtime authority)
- `src/Winpepper.App/Hosting/AppShell.cs`: the boot repair `store.Save(settings);` (documented exception, pre-writer)
- `src/Winpepper.App/Views/ModelsPage.xaml.cs:218` and `:253`: `keyStore.Save(...)` — DPAPI API-key store, NOT settings.json (out of scope)
- `src/Winpepper.App/Hosting/AppShell.cs:155`: a comment, not a call

The ModelsPage:46 and HistoryDetailPage:73 hits must be GONE. Also verify MainWindow needs no change (it already uses the writer):

```bash
grep -n "SettingsWriter.QueueAndFlushAsync" src/Winpepper.App/Views/MainWindow.xaml.cs
```

Expected: one hit (~line 58, the resize handler). Also verify no mutator still reads live control state inside the lambda:

```bash
grep -rn "Toggle.IsOn })" --include="*.cs" src/
```

Expected: NO hits (Steps 1b and 3 replaced all four `IsOn`-reading mutators with captured locals). Paste all three grep outputs into a new `## Write-authority audit (Task 4)` section of the evidence doc, plus this note for the review gate: *"ModelsPage/HistoryDetailPage/RecordingPage/AppShell edits are Windows-only WinUI code — not compilable on Linux; hand-verified against the plan's code blocks and type-checked by windows-gate.sh below. One behavioral note: mutators now execute at flush time; the four lambdas that previously read live WinUI control state (ModelsPage AssemblyAi/Streaming toggles, RecordingPage Autostart toggle) now capture that state into a local BEFORE queueing, so no mutator reads UI state at all — flush-time execution is safe on any thread, including a racing debounce tick or a Dispose flush."*

- [ ] **Step 6: Linux suite + commit**

App-layer edits are invisible to Linux, but the repo rule is green-before-every-commit and it proves the shared code still stands:

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (grand total = baseline + 8). Then:

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml.cs \
        src/Winpepper.App/Views/HistoryDetailPage.xaml.cs \
        src/Winpepper.App/Views/RecordingPage.xaml.cs \
        src/Winpepper.App/Hosting/AppShell.cs \
        docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "fix(app): single settings write authority — route cleanup-model promotion through the shared writer; capture toggle state before queueing

ModelsPage.promoteCleanup and HistoryDetailPage.promoteCleanupDefault
bypassed DebouncedSettingsWriter with direct SettingsStore.Save —
latent same-class lost-update hazards (they write only
CleanupModelName; the 7/25–7/26 outage's out-of-band writes were
direct settings.json edits). Both now use
shell.SettingsWriter.QueueAndFlushAsync like every other toggle on
those pages. The four mutators that read live WinUI control state
(ModelsPage AssemblyAi/Streaming toggles, RecordingPage Autostart
toggle) now capture IsOn into a local before queueing — mutators
execute at flush time and must not touch thread-affine UI objects.
AppShell's boot-time validity repair is documented as the one
sanctioned pre-writer direct save, and the flush logger is wired
(Winpepper.App.Settings category)."
```

- [ ] **Step 7: Windows gate (pre-push proof; nothing is pushed)**

7a. Cross-OS hygiene — clean Linux build outputs first:

```bash
rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
```

7b. Poll until NO `dotnet.exe` with `winpepper` in its command line exists on the host (two consecutive zero counts 45 s apart — concurrent sessions cause UNC build races):

```bash
prev=-1
while :; do
  n=$(powershell.exe -NoProfile -Command \
    "@(Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*winpepper*' }).Count" \
    | tr -d '[:space:]')
  echo "winpepper dotnet.exe count: $n"
  if [ "$n" = "0" ] && [ "$prev" = "0" ]; then break; fi
  prev=$n
  sleep 45
done
```

7c. Run the gate in the FOREGROUND with a 45-minute timeout:

```bash
timeout 2700 ./scripts/windows-gate.sh
```

Expected: exit 0 and final line `GATE: GREEN`. If it fails on the App-layer edits (typo/type error in Steps 1–4), fix the file, re-run the Linux suite (after `rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj`), commit the fix (`fix(app): correct <file> from windows-gate failure`), and repeat Step 7 from 7a. Never install/launch/kill anything; never write to `C:\Users\dan\AppData\Local\winpepper`.

- [ ] **Step 8: Record gate evidence and commit**

Append to `docs/plans/2026-07-26-settings-lost-update-evidence.md` a `## Windows gate (Task 4)` section with the verbatim tail of the gate output (the `GATE: GREEN` line and the test-count summary lines above it). Then clean Windows build outputs, prove Linux green once more, and commit:

```bash
rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
./scripts/linux-tests.sh      # expected: LINUX SUITE: GREEN
git add docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "docs(evidence): windows-gate GREEN + write-authority audit for settings lost-update fix"
```

Done criteria for the whole plan: red test recorded failing first (evidence doc), writer fixed to mutator-replay-on-fresh-load with the degraded-load guard (`TryLoadCurrent`) and exception containment (dropped thrower, save re-queue), single write authority (audit in evidence doc, incl. the no-UI-state-in-mutators grep), settings-flush observability logging — success AND failure paths, emitted outside the lock — wired end-to-end, `LINUX SUITE: GREEN` (baseline + 8) and `GATE: GREEN` recorded, NOTHING pushed.

---

## Follow-ups (out of scope)

- **Cleanup backend is boot-frozen (verified during plan validation):** AppShell picks the FIRST `.gguf` under `models/cleanup` at startup (`AppShell.cs:182–192`) and never reads `CleanupModelName`; a cleanup-model promotion therefore does not change the executed model until restart — and possibly not even then, with multiple cleanup models installed. `PipelineHost._cleanupModelName` is history-attribution only. Consequence for this plan: Task 4's fire-and-forget promotion swap cannot regress model effectivity, because promotion never took live effect even at HEAD (verified). Recommend filing a follow-up issue for restart-independent (or at least deterministic, name-driven) cleanup-model selection; explicitly NOT in this plan's scope.

---

## Self-review record (kept for the plan-review stage)

1. **Spec coverage:** red-test-first with recorded verbatim evidence → Task 1 Steps 1–2; writer mutator-replay fix preserving API/debounce/dispose → Task 1 Step 3b; degraded-load guard (additive `SettingsStore.TryLoadCurrent`; skip + keep mutations, never rewrite from a degraded base) → Task 1 Step 3a/3b, pinned in Task 2 (`Flush_SkipsAndKeepsMutations_WhenSettingsFileIsUnreadable`); exception containment (throwing mutator dropped, siblings apply; failed Save re-queues the batch at the front) → Task 1 Step 3b, pinned in Task 2 (`Flush_AppliesRemainingMutators_WhenOneThrows`; save-failure re-queue covered by code review + Windows gate compile, portable test impractical — reasoning recorded in Task 2 Step 1); existing tests unmodified except one incidental pin, reported → header section + Task 1 Step 5; mutator-wins (with the accepted hand-edit-during-debounce edge documented in the test) / ordering / dispose-preserves / coalescing tests → Task 2 (+ existing coalescing test kept); change-logging + failure-path logging (WRN degraded skip with kept-pending count, ERR dropped mutator, WRN save re-queue), payloads captured in the lock but EMITTED outside it, with facts for the INF and WRN paths → Task 3; ModelsPage + HistoryDetailPage routed through writer, four UI-state-reading mutators converted to capture-before-queue (ModelsPage :201/:204/:209, RecordingPage :86), AppShell:83 audited and documented, full `.Save(` audit + no-`Toggle.IsOn`-in-mutators grep, MainWindow verified unchanged → Task 4 Steps 1–5; thread-safety kept/strengthened (whole RMW under the lock — median hold 13.3 ms measured; fixes the old Save-outside-lock reorder; no ConfigureAwait(false), documented) → Task 1 Step 3b; boot-frozen cleanup backend recorded as an explicit out-of-scope follow-up → Follow-ups section; Windows-only hand-verify note for the gate → Task 4 Step 5; Linux suite before every commit, windows-gate with process polling and hygiene, nothing pushed → Global Constraints + Task 4 Steps 6–8. No unresolved coverage gaps.
2. **No silent deferrals:** every requirement lands as production behavior in these tasks; the only test double is a fake `ILogger` capturing log lines (the real Serilog wiring is exercised by the app; the writer's contract is the `ILogger` interface itself). The single deliberately untested path — save-failure re-queue — is called out in Task 2 Step 1 with the portability reasoning, not silently skipped. No stubs, no TODOs.
3. **Type consistency:** `SettingsStore` gains `bool TryLoadCurrent(out AppSettings)` (Task 1 Step 3a), consumed by `Flush()` identically in Task 1 Step 3b and Task 3 Step 3c; `Load()` signature/behavior untouched for all existing callers (incl. `CleanupSettingsPersistenceTests`). Ctor evolves `(store)` → `(store, TimeSpan? delay = null)` [unchanged] → `+ ILogger? log = null` appended last (Task 3), matching Task 4's `new DebouncedSettingsWriter(store, log: ...)`. `QueueAndFlushAsync(Func<AppSettings, AppSettings>)` used identically in Tasks 1, 2, 4. Test names referenced in commands match their definitions (`Flush_PreservesChangesWrittenOutsideTheWriter`, the five Task 2 facts, `Flush_Logs_The_Names_Of_Changed_Fields`, `Flush_Logs_Warning_When_DegradedLoad_SkipsFlush` — 8 new facts total, matching the File Structure table and the baseline + 8 arithmetic: +1 Task 1, +5 Task 2, +2 Task 3).
