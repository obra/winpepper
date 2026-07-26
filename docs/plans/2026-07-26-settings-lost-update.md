# Settings Lost-Update Fix Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the settings lost-update bug that silently reverted `cleanupEnabled` and pasted raw uncleaned ASR output for ~330 real dictations: make `DebouncedSettingsWriter` replay queued mutators over a fresh disk load at flush time, eliminate the second write authority (direct `SettingsStore.Save` bypasses in ModelsPage/HistoryDetailPage), and log the names of changed fields on every flush so a silent revert can never again leave zero evidence.

**Architecture:** `DebouncedSettingsWriter` (src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs) today seeds an in-memory `_pending` record from `store.Load()` ONCE at construction and `Flush()` writes that whole record — any settings change made outside the writer is silently reverted by the next unrelated flush (classic lost-update; the window-resize handler makes the clobber near-inevitable). The fix stores the queued MUTATOR FUNCTIONS instead of a materialized record; at flush time it re-`Load()`s fresh from disk, applies the queued mutators in order, and saves — fields untouched by any queued mutator always round-trip from disk. The two App-layer direct-save bypasses are routed through the shared writer so there is ONE runtime write authority (the boot-time validity repair in AppShell, which runs before the writer exists, is the single documented exception). An optional `ILogger` on the writer emits one INF line per flush naming the fields that changed (names only — never values).

**Tech Stack:** C# 13 / .NET 9 (`net9.0`), xUnit v3 (in-process runner via `dotnet exec`, NEVER `dotnet test`), Shouldly, Microsoft.Extensions.Logging over Serilog. Core is Linux-testable; the four App-layer files are WinUI, Windows-gate-only.

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

One subsystem (settings persistence) plus a mechanical App-layer call-site swap that depends on it — a single plan. End-to-end proof is the red regression test itself: it reproduces the exact production sequence (out-of-band cleanup-model promotion, then an unrelated resize write) against the real `SettingsStore` + real file I/O, no mocks.

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` | Rewrite (67 lines, stays one file) | Debounced write authority: queue mutators, replay over fresh load at flush, log changed field names |
| `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs` | Extend (add 5 facts + 1 fake logger, adjust 1 closure) | All writer behavior: red regression, ordering, mutator-wins, dispose-preserves, coalescing, change logging |
| `src/Winpepper.App/Views/ModelsPage.xaml.cs` | Edit lines 43–47 | Route `promoteCleanup` through the shared writer (Windows-only) |
| `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs` | Edit lines 70–74 | Route `promoteCleanupDefault` through the shared writer (Windows-only) |
| `src/Winpepper.App/Hosting/AppShell.cs` | Edit lines ~83, ~95 | Document the boot-repair exception; wire the flush logger (Windows-only) |
| `src/Winpepper.App/Views/MainWindow.xaml.cs` | NO change (verify only) | Already uses the writer; it is the clobber trigger the red test simulates |
| `docs/plans/2026-07-26-settings-lost-update-evidence.md` | Create | Verbatim red output, baseline count, suite/gate results |

Unchanged by design: `ISettingsWriter.cs` (public contract), `SettingsStore.cs`, `AppSettings.cs`, `CleanupSettingsPersistenceTests.cs`.

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
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` (full rewrite of the class body)
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs` (add 1 fact, adjust 1 closure)
- Create: `docs/plans/2026-07-26-settings-lost-update-evidence.md`

**Interfaces:**
- Consumes: existing `SettingsStore.Load()/Save(AppSettings)`, `AppSettings` record (`CleanupModelName`, `CleanupEnabled`, `WindowWidth`, `MicDeviceId` properties), `ISettingsWriter` (unchanged).
- Produces: `DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)` whose private `Flush()` drains a `List<Func<AppSettings, AppSettings>> _pendingMutators`, re-loads from disk, applies mutators in order, saves. Task 3 extends the ctor; Task 4 swaps App call sites onto the unchanged `QueueAndFlushAsync(Func<AppSettings, AppSettings>)`.

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
        // The exact production sequence that lost ~330 dictations:
        // 1. app boots (writer constructed over the settings file),
        // 2. a cleanup-model promotion writes DIRECTLY via SettingsStore
        //    (ModelsPage/HistoryDetailPage bypass), also cleanupEnabled
        //    flips out-of-band,
        // 3. an UNRELATED write (MainWindow resize) flushes the writer.
        // The out-of-band changes must survive step 3.
        var store = new SettingsStore(_path);
        store.Save(new AppSettings());
        using var writer = new DebouncedSettingsWriter(store); // HEAD snapshots disk here

        store.Save(store.Load() with
        {
            CleanupModelName = "promoted-model",
            CleanupEnabled = false
        }); // out-of-band write (exactly what ModelsPage:46 / HistoryDetailPage:73 do)

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

- [ ] **Step 3: Implement the mutator-replay writer**

Replace the entire contents of `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs` with:

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
        // locks are reentrant, so a mutator cannot deadlock this.
        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;
            var mutators = _pendingMutators.ToArray();
            _pendingMutators.Clear();

            var settings = _store.Load(); // FRESH read: out-of-band writes survive
            foreach (var mutator in mutators)
                settings = mutator(settings);
            _store.Save(settings);
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
git add src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs \
        docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "fix(settings): mutator-replay flush — out-of-band settings writes survive unrelated flushes

Red test Flush_PreservesChangesWrittenOutsideTheWriter observed FAILING
against HEAD before the fix (verbatim output in
docs/plans/2026-07-26-settings-lost-update-evidence.md): the writer's
construction-time snapshot clobbered a direct-save cleanup-model
promotion on the next unrelated flush — the mechanism that silently
reverted cleanupEnabled for ~330 dictations.

Fix: DebouncedSettingsWriter stores queued mutator FUNCTIONS and, at
flush, re-Loads fresh from disk, applies them in order, then Saves.
Whole read-modify-write is under the lock (also fixes the old
Save-outside-lock flush reorder race). Public API unchanged.

Reported test adjustment: Queue_Coalesces_Bursts_Into_One_Write
captured the shared for-loop variable in its mutator closures — an
incidental pin of eager-capture timing, not of coalescing intent;
adjusted to a per-iteration copy."
```

---

### Task 2: Pin the mutator-replay semantics (ordering, mutator-wins, dispose-preserves)

**Files:**
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: Task 1's writer — `Queue(Func<AppSettings, AppSettings>)`, `FlushAsync()`, `QueueAndFlushAsync(Func<AppSettings, AppSettings>)`, `Dispose()`; `SettingsStore.Load()/Save`.
- Produces: nothing new — regression pins only. (Coalescing is already pinned by the existing `Queue_Coalesces_Bursts_Into_One_Write`, which Task 1 kept green.)

- [ ] **Step 1: Write the three semantics tests**

Append inside the same test class:

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
```

- [ ] **Step 2: Run the Core test dll**

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: ALL PASS (Task 1's implementation already provides these semantics; `Dispose_Flush_Preserves_OutOfBand_Changes` would have been red at HEAD). If any of the three fails, the Task 1 implementation has a bug — fix `DebouncedSettingsWriter.cs`, not the tests, and re-run.

- [ ] **Step 3: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, grand total = baseline + 4.

- [ ] **Step 4: Commit**

```bash
git add tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
git commit -m "test(settings): pin mutator-replay semantics — ordering, queued-mutator-wins, dispose flush preserves out-of-band writes"
```

---

### Task 3: Flush observability — log the names of changed fields

**Files:**
- Modify: `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Test: `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.Logging.ILogger` (already a `Winpepper.Core` package ref; flows to the test project transitively via the `Winpepper.Core` ProjectReference).
- Produces: ctor `DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null, ILogger? log = null)` — appended last with a default, so every existing positional `(store)` / `(store, TimeSpan)` call keeps compiling. Task 4 wires the App's logger through it.

- [ ] **Step 1: Write the failing test (fails to compile — the ctor has no logger yet)**

Append inside the test class — a hand-rolled fake logger (repo convention: no mocking framework) plus the fact. Add `using Microsoft.Extensions.Logging;` to the top of `DebouncedSettingsWriterTests.cs`:

```csharp
    [Fact]
    public async Task Flush_Logs_The_Names_Of_Changed_Fields()
    {
        // The 330-dictation outage produced ZERO settings-write log
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

(Optional-logger pattern matches `AudioEndpointWatcher`; appended-last default keeps all existing positional ctor calls — including the four in the existing tests — compiling unchanged.)

3c. Replace the `Flush()` body so it diffs before/after and logs, and add the helper:

```csharp
    private void Flush()
    {
        // The whole read-modify-write runs under _lock: concurrent flushes
        // serialize (the old code called Save outside the lock, so two
        // flushes could write whole files out of order), and a Queue()
        // racing a flush lands either before the drain (applied now) or
        // after (applied on its own debounce tick) — never lost. Monitor
        // locks are reentrant, so a mutator cannot deadlock this.
        lock (_lock)
        {
            if (_pendingMutators.Count == 0) return;
            var mutators = _pendingMutators.ToArray();
            _pendingMutators.Clear();

            var before = _store.Load(); // FRESH read: out-of-band writes survive
            var after = before;
            foreach (var mutator in mutators)
                after = mutator(after);
            _store.Save(after);
            LogChangedFields(before, after);
        }
    }

    private void LogChangedFields(AppSettings before, AppSettings after)
    {
        if (_log is null) return;
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

Expected: build succeeds; ALL PASS including `Flush_Logs_The_Names_Of_Changed_Fields` (`Failed: 0`).

- [ ] **Step 5: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, grand total = baseline + 5.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs \
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs
git commit -m "feat(settings): log changed field NAMES on every settings flush

One INF line per flush (field names only — content-free logging rule).
The 07/25 cleanupEnabled revert left zero log evidence across ~330
dictations; a silent settings change is now impossible."
```

---

### Task 4: Single write authority — route App direct-saves through the writer, wire the logger, run the Windows gate

**Files:**
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml.cs:43-47`
- Modify: `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs:70-74` (and its now-unused `settings` local, line ~56)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:~83` (comment only) and `:~95` (logger wiring)
- Verify only (no change): `src/Winpepper.App/Views/MainWindow.xaml.cs`
- Modify: `docs/plans/2026-07-26-settings-lost-update-evidence.md` (append audit + gate results)

**Interfaces:**
- Consumes: `shell.SettingsWriter` (`AppShell.cs:23`, type `DebouncedSettingsWriter`), `QueueAndFlushAsync(Func<AppSettings, AppSettings>)` from Task 1, the `ILogger? log` ctor param from Task 3, `factory.CreateLogger("Winpepper.App.Settings")` (the category AppShell already uses for settings warnings at `AppShell.cs:72-73`).
- Produces: ONE runtime write authority. After this task the only `SettingsStore.Save` callers in `src/` are the writer's own `Flush()` and the documented boot-time repair in `AppShell.Create()`.

**IMPORTANT — these files do NOT compile on Linux.** `Winpepper.App.csproj` is single-TFM `net9.0-windows10.0.19041.0` (WinUI) and `scripts/linux-tests.sh` never builds it. Type-checking happens ONLY in the Windows gate (Step 6). Hand-verify each edit character-by-character against the code blocks below, and note this limitation explicitly in the evidence doc for the review gate.

- [ ] **Step 1: Route `promoteCleanup` in ModelsPage through the writer**

In `src/Winpepper.App/Views/ModelsPage.xaml.cs`, replace (currently lines 43–47):

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

- [ ] **Step 3: Document the boot-repair exception and wire the flush logger in AppShell**

In `src/Winpepper.App/Hosting/AppShell.cs`:

3a. Immediately above `store.Save(settings);` (currently line 83, inside the unknown-ASR-model repair `if` block), add:

```csharp
            // Boot-time validity repair — the ONE sanctioned direct save:
            // it runs before the DebouncedSettingsWriter exists (below),
            // and the writer re-loads from disk at every flush, so this
            // can neither clobber nor be clobbered. ALL runtime settings
            // writes go through SettingsWriter (single write authority).
```

3b. Replace the writer construction (currently line 95):

```csharp
        var writer = new DebouncedSettingsWriter(store);
```

with:

```csharp
        var writer = new DebouncedSettingsWriter(store,
            log: factory.CreateLogger("Winpepper.App.Settings"));
```

(Named argument skips the `delay` parameter; `"Winpepper.App.Settings"` is the category AppShell already uses for settings warnings two lines up.)

- [ ] **Step 4: Write-authority audit**

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

Expected: one hit (~line 58, the resize handler). Paste both grep outputs into a new `## Write-authority audit (Task 4)` section of the evidence doc, plus this note for the review gate: *"ModelsPage/HistoryDetailPage/AppShell edits are Windows-only WinUI code — not compilable on Linux; hand-verified against the plan's code blocks and type-checked by windows-gate.sh below. One behavioral note: mutators now execute at flush time; all App mutators are invoked via QueueAndFlushAsync, whose final Flush runs synchronously on the calling (UI) thread, so lambdas that read UI control state (e.g. AssemblyAiDeleteToggle.IsOn) still execute on the UI thread on the immediate-flush path."*

- [ ] **Step 5: Linux suite + commit**

App-layer edits are invisible to Linux, but the repo rule is green-before-every-commit and it proves the shared code still stands:

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (grand total = baseline + 5). Then:

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml.cs \
        src/Winpepper.App/Views/HistoryDetailPage.xaml.cs \
        src/Winpepper.App/Hosting/AppShell.cs \
        docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "fix(app): single settings write authority — route cleanup-model promotion through the shared writer

ModelsPage.promoteCleanup and HistoryDetailPage.promoteCleanupDefault
bypassed DebouncedSettingsWriter with direct SettingsStore.Save —
the second write authority behind the lost-update. Both now use
shell.SettingsWriter.QueueAndFlushAsync like every other toggle on
those pages. AppShell's boot-time validity repair is documented as
the one sanctioned pre-writer direct save, and the flush logger is
wired (Winpepper.App.Settings category)."
```

- [ ] **Step 6: Windows gate (pre-push proof; nothing is pushed)**

6a. Cross-OS hygiene — clean Linux build outputs first:

```bash
rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
```

6b. Poll until NO `dotnet.exe` with `winpepper` in its command line exists on the host (two consecutive zero counts 45 s apart — concurrent sessions cause UNC build races):

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

6c. Run the gate in the FOREGROUND with a 45-minute timeout:

```bash
timeout 2700 ./scripts/windows-gate.sh
```

Expected: exit 0 and final line `GATE: GREEN`. If it fails on the App-layer edits (typo/type error in Steps 1–3), fix the file, re-run the Linux suite (after `rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj`), commit the fix (`fix(app): correct <file> from windows-gate failure`), and repeat Step 6 from 6a. Never install/launch/kill anything; never write to `C:\Users\dan\AppData\Local\winpepper`.

- [ ] **Step 7: Record gate evidence and commit**

Append to `docs/plans/2026-07-26-settings-lost-update-evidence.md` a `## Windows gate (Task 4)` section with the verbatim tail of the gate output (the `GATE: GREEN` line and the test-count summary lines above it). Then clean Windows build outputs, prove Linux green once more, and commit:

```bash
rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj
./scripts/linux-tests.sh      # expected: LINUX SUITE: GREEN
git add docs/plans/2026-07-26-settings-lost-update-evidence.md
git commit -m "docs(evidence): windows-gate GREEN + write-authority audit for settings lost-update fix"
```

Done criteria for the whole plan: red test recorded failing first (evidence doc), writer fixed to mutator-replay-on-fresh-load, single write authority (audit in evidence doc), settings-flush observability logging wired end-to-end, `LINUX SUITE: GREEN` and `GATE: GREEN` recorded, NOTHING pushed.

---

## Self-review record (kept for the plan-review stage)

1. **Spec coverage:** red-test-first with recorded verbatim evidence → Task 1 Steps 1–2; writer mutator-replay fix preserving API/debounce/dispose → Task 1 Step 3; existing tests unmodified except one incidental pin, reported → header section + Task 1 Step 5; mutator-wins / ordering / dispose-preserves / coalescing tests → Task 2 (+ existing coalescing test kept); change-logging test + INF-per-flush, names only → Task 3; ModelsPage + HistoryDetailPage routed through writer, AppShell:83 audited and documented, full `.Save(` audit, MainWindow verified unchanged → Task 4 Steps 1–4; thread-safety kept/strengthened (whole RMW under the lock, fixes the old Save-outside-lock reorder) → Task 1 Step 3; Windows-only hand-verify note for the gate → Task 4 Step 4; Linux suite before every commit, windows-gate with process polling and hygiene, nothing pushed → Global Constraints + Task 4 Steps 5–7. No unresolved coverage gaps.
2. **No silent deferrals:** every requirement lands as production behavior in these tasks; the only test double is a fake `ILogger` capturing log lines (the real Serilog wiring is exercised by the app; the writer's contract is the `ILogger` interface itself). No stubs, no TODOs.
3. **Type consistency:** ctor evolves `(store)` → `(store, TimeSpan? delay = null)` [unchanged] → `+ ILogger? log = null` appended last (Task 3), matching Task 4's `new DebouncedSettingsWriter(store, log: ...)`. `QueueAndFlushAsync(Func<AppSettings, AppSettings>)` used identically in Tasks 1, 2, 4. Test names referenced in commands match their definitions.
