# Nemotron-First ASR — Council-Review Fix Batch Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Apply the adversarial-council fix batch to `feat/nemotron-first-asr`: make the worker restart budget actually bound kill/respawn cycles, route both worker-retirement paths through the same generation-fence + dispose invariant, fix a locking-invariant violation and false certification text, correct user-facing copy, delete dead code, add worker-entry test coverage, and record honest verification evidence in-repo.

**Architecture:** Surgical fixes on reviewed code — no refactors beyond what the items require. The supervision fixes all live in `WorkerProcessEngine` (accounting moves: failure-crediting to every kill path, success-crediting off Load onto completed operation RPCs; natural-exit retirement routed through `KillLocked`). A new tiny `net9.0` console host gives the worker loop real-subprocess test coverage on both Linux and Windows. Everything else is targeted edits: four lock statements, comment/doc rewrites, two copy strings, one dead-interface deletion, one evidence document.

**Tech Stack:** C# / .NET 9, xUnit v3 (in-process runner via `dotnet exec`, never `dotnet test`), Shouldly, WinUI 3 (Windows-only paths), WSL→Windows `powershell.exe` interop for the gate and smoke.

## Global Constraints

- **Work in the existing worktree** `/home/dan/code/winpepper/.worktrees/nemotron-first-asr`, directly on branch `feat/nemotron-first-asr` (HEAD at plan time `dc73c52`, forked from `080e4f1`). Do NOT branch from main; the fixed code exists only on this branch. Leave the branch unmerged at the end.
- **Before EVERY commit:** full Linux suite green — `cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh`, expect final line `LINUX SUITE: GREEN`. This applies even to docs-only and Windows-only-code commits (repo `AGENTS.md:3-4`).
- **Before any push / at final HEAD:** `./scripts/windows-gate.sh` from WSL must print `GATE: GREEN` (~12–40 min; run backgrounded with a 20–40 min budget, never concurrently with `linux-tests.sh`).
- **Never mix Linux and Windows builds in the same `bin/`/`obj/`** — the helper scripts pre-clean automatically; do not hand-build with the Windows dotnet against Linux `obj` state (CS0006).
- **Test invocation:** build test projects with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, then `dotnet exec <built dll> -notrait "Platform=Windows"` on Linux. `DOTNET_ROOT=/home/dan/code/winpepper/.dotnet`.
- **Scope discipline — explicitly OUT of scope** (recorded backlog, do not do): PipelineHost backup-role machinery simplification; duplicated selection-slot class; the `"-batch"` string-suffix invariant; rerun-vs-dictation lock-convoy; MSI vc_redist chaining; any model-quality evaluation work.
- **Evidence honesty:** never claim unverified steps as done; record verbatim outputs; record skips and manual-remaining items explicitly.
- `README.md` is the only end-user markdown doc; files under `docs/plans/` are working/agent docs and are fine.

## Scope check

All ten fix items target one subsystem cluster (the worker-supervision code, its onboarding/UI surface, and this branch's bookkeeping) on a single branch, and several items interlock (fixes 1–3 share one method; fixes 5/8 share the evidence story). One plan.

## File structure

| Path | Role in this batch |
|---|---|
| `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs` | Modify — restart-budget accounting (Tasks 1–2), natural-exit retirement (Task 2) |
| `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs` | Modify — `BindKillOnClose` failure logging (Task 3), Job-Object comment rewrite (Task 4) |
| `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs` | Modify — new supervision regression tests (Tasks 1–2) |
| `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs` | Modify — test-double extensions (Task 2) |
| `tests/TranscribeWorkerHost/TranscribeWorkerHost.csproj`, `Program.cs` | Create — portable worker-entry host for real-subprocess tests (Task 3) |
| `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerHostProcessTests.cs` | Create — real-subprocess worker-entry tests (Task 3) |
| `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` | Modify — ProjectReference to the host (Task 3) |
| `docs/plans/2026-08-08-nemotron-first-asr.md` | Modify — A1 residual rewrite + residual label key (Task 5) |
| `src/Winpepper.App/Services/OnboardingModelProvisioner.cs` | Modify — percent-race locks (Task 6), VC++ error copy (Task 7) |
| `src/Winpepper.App/Views/ModelsPage.xaml` | Modify — stale "(optional)" header (Task 7) |
| `src/Winpepper.Core/ViewModels/IAsrProvisioningService.cs` | Delete — dead interface (Task 8) |
| `src/Winpepper.App/Services/ModelsServices.cs` | Modify — strip orphaned interface members (Task 8) |
| `docs/plans/2026-08-09-nemotron-first-asr-evidence.md` | Create — verification evidence (Task 10) |

Line numbers cited below are from HEAD `dc73c52`. If a file has drifted, locate the quoted code by content, not line number.

**Execution order:** Tasks run 1 → 10 in order. Tasks 1–2 are sequential (same method). Task 9 (gate) must precede Task 10 (evidence needs the gate output, and the gate's stage 1 builds `Winpepper.exe` on the host for the smoke).

---

### Task 1: Restart-budget accounting rewrite (council fix #1)

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs:62-86, 105-136, 141-168, 228-238`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs`

**Interfaces:**
- Consumes: `WorkerRestartPolicy.CanAttempt()/NoteFailure()/NoteSuccess()` (unchanged, `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerRestartPolicy.cs`); existing test helpers `Engine(factory, policy)` and `FastTimeouts` in `WorkerProcessEngineTests.cs:13-25`; `FakeTranscribeCppEngine.FeedGate` (`tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs:25`).
- Produces: the accounting contract Task 2 builds on — **every kill-path failure calls `NoteFailure()`** (RPC timeout, RPC IO failure, spawn failure, Load error frame — exactly once each, deduplicated in the `EnsureWorkerLocked` catch via `spawned && _proc is null`), and **`NoteSuccess()` fires only on a completed operation RPC** (non-Error `TranscribeBatch` response, non-Error `FinalizeStream` response) — never on Load.

**Background (why the obvious fix is a trap):** today `NoteFailure()` has exactly one call site (the Load catch, `:131`) and `NoteSuccess()` fires after every successful Load (`:126`). Operation-phase wedge kills (`:148-157`) never count. Merely adding `NoteFailure` to the kill path does NOT work: the counter oscillates 0↔1 because every respawn's Load immediately resets it. The fix must BOTH count kill-path failures AND move success-crediting off Load onto completed operation RPCs. Crediting `BeginStream`/`Feed` would re-open the oscillation for the feed-wedge pattern (every wedge cycle contains a successful BeginStream), so only the two "dictation actually produced text" ops credit: `TranscribeBatch` and `FinalizeStream`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs` (inside the existing `WorkerProcessEngineTests` class, matching its style — injected clock, `ManualResetEventSlim` wedge, `feedGate.Set()` last):

```csharp
    [Fact]
    public void OperationWedge_ThreeConsecutiveKills_ExhaustRestartBudget_ThenCooldownAllowsOneRetry()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 3, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        using var feedGate = new ManualResetEventSlim(false); // never set until the end: EVERY feed wedges
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine { FeedGate = feedGate });
        using var engine = Engine(factory, policy);

        for (var i = 1; i <= 3; i++)
        {
            using var stream = engine.BeginStream(13, null, out _); // respawn allowed while budget remains
            Should.Throw<TranscribeCppException>(() => stream.Feed(new float[2560], 2560)); // wedge -> 300 ms timeout -> kill
            factory.Last!.HasExited.ShouldBeTrue();
        }
        factory.Started.ShouldBe(3);

        // Wedge x3 exhausted the budget: the next call must NOT spawn a 4th worker.
        var blocked = Should.Throw<TranscribeCppException>(() => engine.BeginStream(13, null, out _));
        blocked.Message.ShouldContain("restart budget exhausted");
        factory.Started.ShouldBe(3);

        // Cooldown engages: exactly one attempt is allowed after 60 s.
        now = 60_000;
        Should.NotThrow(() => engine.BeginStream(13, null, out _));
        factory.Started.ShouldBe(4);

        feedGate.Set(); // release the parked worker threads
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" \
  -method "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProcessEngineTests.OperationWedge_ThreeConsecutiveKills_ExhaustRestartBudget_ThenCooldownAllowsOneRetry"
```

Expected: FAIL at the `blocked` assertion — `Should.Throw<TranscribeCppException>` reports no exception was thrown (pre-fix, the 4th `BeginStream` happily respawns because wedge kills never counted and each respawn's Load reset the counter).

- [ ] **Step 3: Implement the accounting rewrite**

Four edits in `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs`.

**(3a)** In `RpcLocked` (`:141-168`), charge the budget on both kill paths. The timeout branch becomes:

```csharp
            if (!read.Wait(timeout))
            {
                _restartPolicy.NoteFailure(); // operation-phase kills charge the budget (council fix #1)
                KillLocked($"{op} timed out after {(int)timeout.TotalMilliseconds} ms");
```

(the `read.ContinueWith(...)` observation and the `throw` stay exactly as they are), and the generic catch becomes:

```csharp
        catch (TranscribeCppException) { throw; }
        catch (Exception e)
        {
            var inner = (e as AggregateException)?.InnerException ?? e;
            _restartPolicy.NoteFailure(); // connection failures charge the budget too (council fix #1)
            KillLocked($"{op} failed: {inner.Message}");
            throw new TranscribeCppException($"speech worker connection failed during {op}: {inner.Message}");
        }
```

(A failed best-effort `Shutdown` during `Dispose()` also lands here and counts; harmless — a disposed engine never consults the budget again.)

**(3b)** In `EnsureWorkerLocked` (`:105-136`), remove the Load-path `NoteSuccess()` and deduplicate failure counting. The `try`/`catch` becomes:

```csharp
        var spawned = false;
        try
        {
            _proc = _factory.Start();
            spawned = true;
            _log?.Invoke("speech worker started");
            var payload = Build(w => { WorkerWire.WriteString(w, _runtimeDir); WorkerWire.WriteString(w, _ggufPath); });
            var (op, response) = RpcLocked(WorkerOp.Load, payload, _options.LoadTimeout);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out _);
            var loadedName = WorkerWire.ReadString(r);
            // Success is credited ONLY by a completed operation RPC (batch
            // returned text / stream finalized) — never by Load. Crediting
            // Load made the budget oscillate 0<->1 across every
            // kill->respawn->Load cycle, so it could never bound the
            // operation-phase kills it exists to bound (council fix #1).
            _log?.Invoke($"speech worker load ok ({loadedName})");
        }
        catch (Exception e)
        {
            // Failures inside RpcLocked (timeout / broken pipe) already
            // noted themselves and killed the worker, nulling _proc. The two
            // cases still uncounted here: _factory.Start() threw
            // (spawned == false), and Load answered with an Error frame
            // (worker alive, _proc != null).
            var alreadyCounted = spawned && _proc is null;
            if (!alreadyCounted) _restartPolicy.NoteFailure();
            KillLocked($"load failed: {e.Message}");
            throw e as TranscribeCppException
                  ?? new TranscribeCppException($"speech worker failed to start: {e.Message}");
        }
```

**(3c)** In `TranscribeBatch` (`:62-86`), credit success after a non-Error response. The tail of the locked block becomes:

```csharp
            var (op, response) = RpcLocked(WorkerOp.TranscribeBatch, payload, batchDeadline);
            using var r = Reader(response);
            if (op == WorkerOp.Error) throw ReadError(r, out gateWaitMs);
            gateWaitMs = r.ReadInt32();
            var text = WorkerWire.ReadString(r) ?? "";
            // A completed dictation is the only success credit: it proves the
            // kill->respawn cycle actually recovered (council fix #1).
            _restartPolicy.NoteSuccess();
            return text;
```

**(3d)** In `WorkerStream.Finalize` (`:228-238`), same crediting:

```csharp
        public (string Text, bool WasTruncated) Finalize()
        {
            lock (_owner._rpcGate)
            {
                ThrowIfLostLocked();
                var (op, response) = _owner.RpcLocked(WorkerOp.FinalizeStream, Array.Empty<byte>(), _owner._options.FinalizeTimeout);
                using var r = Reader(response);
                if (op == WorkerOp.Error) throw ReadError(r, out _);
                var text = WorkerWire.ReadString(r) ?? "";
                var truncated = r.ReadBoolean();
                _owner._restartPolicy.NoteSuccess(); // a finished stream dictation resets the budget
                return (text, truncated);
            }
        }
```

Do NOT credit `BeginStream`, `Feed`, or `DisposeStream` (see Background above). Do NOT touch `WorkerRestartPolicy.cs` — the policy class is correct; the call sites were wrong.

- [ ] **Step 4: Run the new test to verify it passes**

Same commands as Step 2. Expected: PASS.

- [ ] **Step 5: Run the whole Asr test project (the existing supervision tests must stay green)**

```bash
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows"
```

Expected: `Errors: 0, Failed: 0`. Existing tests to sanity-check by name in the output: `WedgedFeed_TimesOut_KillsWorker_AndNextCallRespawns` (one wedge < 3-strike budget, still respawns), `RestartBudgetExhausted_ThrowsWithoutSpawning` (Load-failure counting unchanged), `EndToEnd_WedgedStream_FallsBackToNemotronBatch_OnFreshWorker` (fallback batch completes → credits success).

- [ ] **Step 6: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
git add src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs
git commit -m "fix(asr): charge the restart budget on every kill path; credit success only on completed operations"
```

---

### Task 2: Between-RPC deaths charge the budget; natural-exit retirement goes through KillLocked (council fixes #2 + #3)

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs:105-116` (head of `EnsureWorkerLocked`)
- Modify: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs`

**Interfaces:**
- Consumes: Task 1's accounting contract (`NoteSuccess()` only on completed batch/finalize — test 2a relies on a completed batch resetting the count; `NoteFailure()` on every kill path).
- Produces: extended test double used by these tests and available to later work — `InProcessWorkerChannel.SimulateNaturalExit()` (`void`, flips `HasExited` true without tearing anything down), `InProcessWorkerChannel.KillCalls`/`DisposeCalls` (`int` counters), `InProcessWorkerChannelFactory.All` (`List<InProcessWorkerChannel>` of every channel ever started). Engine behavior: a dead-but-not-retired worker found at `EnsureWorkerLocked` is retired via `NoteFailure()` + `KillLocked("worker exited between requests")` BEFORE the budget check, so the generation fence and dispose invariant hold on both retirement paths.

**Background:** the natural-exit path (`:111` falls through when `HasExited` is true → `:119` overwrites `_proc`) respawns without a generation bump and without disposing the old process. Consequences: (a) a crash-looping worker never trips the budget → unbounded ~700 MB reload storms with no log trace; (b) the old `Process`/pipes/job handle leak; (c) a stale `WorkerStream` proxy still passes `ThrowIfLostLocked` (`:256` — generation matches, `_proc` is the NEW live process) and feeds into / disposes into a second dictation's one-stream worker.

- [ ] **Step 1: Extend the test double**

In `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs`:

Replace the current `Kill()`/`Dispose()` pair (`:46-58`):

```csharp
    public int KillCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    /// <summary>Simulates the worker process dying on its own (natural exit):
    /// HasExited flips true but nothing is torn down — like a real child that
    /// exited while the parent still holds the Process object, pipes, and
    /// (on Windows) the job handle.</summary>
    public void SimulateNaturalExit() => _exited = true;

    public void Kill()
    {
        KillCalls++;
        _exited = true;
        // WRITE ends FIRST — each unblocks the opposite side's blocked read
        // (EOF / IO fault). Disposing a read end while a read is in flight
        // would block THIS thread until its peer write end closes (V7).
        _toWorker.Dispose();   // client->worker write end: the worker's ReadFrame EOFs
        _workerOut.Dispose();  // worker->client write end: the client's deadline'd ReadFrame unblocks
        _fromWorker.Dispose();
        _workerIn.Dispose();
    }

    public void Dispose()
    {
        DisposeCalls++;
        Kill();
    }
```

(The pipe-teardown ORDER is load-bearing — do not reorder; stream `Dispose()` is idempotent, so `Kill()` after `SimulateNaturalExit()` or a second `Kill()` is safe.)

In `InProcessWorkerChannelFactory` (`:61-73`), track every channel:

```csharp
public sealed class InProcessWorkerChannelFactory : IWorkerProcessFactory
{
    private readonly Func<ITranscribeCppEngine> _engineFactory;
    public InProcessWorkerChannelFactory(Func<ITranscribeCppEngine> engineFactory) => _engineFactory = engineFactory;
    public int Started { get; private set; }
    public InProcessWorkerChannel? Last { get; private set; }
    public List<InProcessWorkerChannel> All { get; } = new();
    public IWorkerProcess Start()
    {
        Started++;
        Last = new InProcessWorkerChannel(_engineFactory);
        All.Add(Last);
        return Last;
    }
}
```

- [ ] **Step 2: Write the two failing tests**

Append to `WorkerProcessEngineTests.cs`:

```csharp
    [Fact]
    public void NaturalDeathBetweenRpcs_ChargesTheRestartBudget()
    {
        long now = 0;
        var policy = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory, policy);

        engine.TranscribeBatch(new float[16], null, out _); // healthy spawn; completed batch resets the budget
        factory.All[0].SimulateNaturalExit();               // dies between RPCs

        // The death is charged BEFORE the respawn attempt, so with a
        // 1-failure budget no second worker may spawn.
        var ex = Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[16], null, out _));
        ex.Message.ShouldContain("restart budget exhausted");
        factory.Started.ShouldBe(1);

        now = 60_000; // cooldown elapsed -> one respawn attempt allowed
        Should.NotThrow(() => engine.TranscribeBatch(new float[16], null, out _));
        factory.Started.ShouldBe(2);
    }

    [Fact]
    public void NaturalExitRespawn_InvalidatesStaleStreamProxy_AndDisposesTheOldProcess()
    {
        var factory = new InProcessWorkerChannelFactory(() => new FakeTranscribeCppEngine());
        using var engine = Engine(factory);

        var staleStream = engine.BeginStream(13, null, out _); // dictation 1 opens a stream
        factory.All[0].SimulateNaturalExit();                   // its worker dies on its own

        engine.TranscribeBatch(new float[16], null, out _);     // dictation 2 triggers the respawn
        factory.Started.ShouldBe(2);
        factory.All[0].DisposeCalls.ShouldBe(1); // old process retired through KillLocked, not leaked

        // The stale proxy must see "stream lost", NOT reach dictation 2's
        // fresh one-stream worker (pre-fix it RPCs the fresh worker and gets
        // its "no open stream (send BeginStream first)" error instead).
        var ex = Should.Throw<TranscribeCppException>(() => staleStream.Feed(new float[2560], 2560));
        ex.Message.ShouldContain("stream lost");
        Should.NotThrow(() => staleStream.Dispose()); // benign no-op on a lost stream
    }
```

- [ ] **Step 3: Run both to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" \
  -method "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProcessEngineTests.NaturalDeathBetweenRpcs_ChargesTheRestartBudget" \
  -method "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerProcessEngineTests.NaturalExitRespawn_InvalidatesStaleStreamProxy_AndDisposesTheOldProcess"
```

Expected: BOTH FAIL — the first at `Should.Throw` (pre-fix the death is uncounted and the respawn proceeds), the second at `DisposeCalls.ShouldBe(1)` (pre-fix the old channel is never disposed; expected 1, actual 0).

- [ ] **Step 4: Implement the retirement**

In `EnsureWorkerLocked` (`WorkerProcessEngine.cs`), insert retirement between the fast path and the budget check. The method head becomes:

```csharp
        if (_disposed) throw new ObjectDisposedException(nameof(WorkerProcessEngine));
        if (_proc is { HasExited: false }) return;
        if (_proc is not null)
        {
            // The worker died on its own since the last RPC. Retire it
            // through KillLocked so BOTH retirement paths share the same
            // invariant (the generation bump invalidates stale stream
            // proxies; Dispose frees the Process, pipes, and job handle),
            // and charge the budget: a worker crash-looping between RPCs
            // must exhaust the 3-strike/60 s budget instead of silently
            // reloading ~700 MB per call forever (council fixes #2, #3).
            _restartPolicy.NoteFailure();
            KillLocked("worker exited between requests");
        }
        if (!_restartPolicy.CanAttempt())
        {
            _log?.Invoke("speech worker restart blocked by budget; next attempt after cooldown");
            throw new TranscribeCppException("speech worker restart budget exhausted; retrying after cooldown");
        }
```

Retirement runs BEFORE the budget check on purpose: the fence/dispose must happen even when the budget then blocks the respawn. Every respawn is already logged — `KillLocked` logs `speech worker killed: worker exited between requests` and the spawn logs `speech worker started`.

- [ ] **Step 5: Run both new tests to verify they pass, then the whole project**

Same commands as Step 3, then:

```bash
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" -notrait "Platform=Windows"
```
Expected: PASS; `Errors: 0, Failed: 0`.

- [ ] **Step 6: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Asr/TranscribeCpp/Worker/WorkerProcessEngine.cs \
        tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/InProcessWorkerChannel.cs \
        tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerProcessEngineTests.cs
git commit -m "fix(asr): retire naturally-exited workers through KillLocked and charge the budget for between-RPC deaths"
```

---

### Task 3: BindKillOnClose failure logging + real-subprocess worker-entry tests (council fix #9)

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs:1-2, 45, 121-135`
- Create: `tests/TranscribeWorkerHost/TranscribeWorkerHost.csproj`
- Create: `tests/TranscribeWorkerHost/Program.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`
- Test: `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerHostProcessTests.cs` (new file)

**Interfaces:**
- Consumes: `TranscribeWorkerLoop.Run(Stream input, Stream output, Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log)` returning `int` (`src/Winpepper.Asr/TranscribeCpp/Worker/TranscribeWorkerLoop.cs:17`); `TranscribeCppEngine.Load(string runtimeDir, string ggufPath, Action<string> log)` (`src/Winpepper.Asr/TranscribeCpp/TranscribeCppEngine.cs` — its `contract.json` existence check runs BEFORE the Windows-only gate, so a bogus runtime dir yields a structured `Error` frame on any OS); production `ExeWorkerProcessFactory(Func<ProcessStartInfo> psi, Action<string>? onStderrLine = null)` and `WorkerProcessEngine`.
- Produces: `WindowsJob.BindKillOnClose(Process process, Action<string>? log = null)` (signature change, internal); `tests/TranscribeWorkerHost` console exe copied into the Asr test output as `TranscribeWorkerHost.dll` + apphost — later tasks/tests may spawn it as a real worker.

**Background:** `--transcribe-worker` (the worker-process entry) is executed by zero tests, and `BindKillOnClose` fails silently (`return 0` on all three failure branches, no log), so the only defense against orphaned ~700 MB workers on parent crash can be silently absent. The real `Winpepper.exe` entry cannot run on Linux (WinExe TFM, `SetErrorMode` P/Invoke, MTA `SetApartmentState` — all verified fatal off-Windows), but the worker loop itself is pure BCL until `Load` touches native code. So: a tiny portable console host runs the portable half of the verb, and a Linux-runnable test spawns it as a REAL child process through the PRODUCTION `ExeWorkerProcessFactory` + `WorkerProcessEngine` — real `Process.Start`, real stdio framing, real structured `Error` frame, real kill semantics. It runs under the Windows gate too.

- [ ] **Step 1: Add the failure log to `WindowsJob.BindKillOnClose`**

In `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs`, add to the `using` block at the top of the file:

```csharp
using System.ComponentModel;
```

Replace `BindKillOnClose` (`:121-135`):

```csharp
    internal static nint BindKillOnClose(Process process, Action<string>? log = null)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            log?.Invoke("worker job object create failed: " +
                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message} — " +
                "workers will not be reaped if this process crashes");
            return 0;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            log?.Invoke("worker job object bind failed: " +
                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message} — " +
                "workers will not be reaped if this process crashes");
            CloseHandle(job);
            return 0;
        }
        return job;
    }
```

And route the existing stderr sink into it at the sole call site (`:45`):

```csharp
        var jobHandle = OperatingSystem.IsWindows() ? WindowsJob.BindKillOnClose(process, onStderrLine) : 0;
```

(`onStderrLine` is the worker-log sink the production factory already wires to `ILogger.LogWarning` in `NemotronEngineHolder.cs:74-76`; a job-bind failure line belongs on the same channel. The onboarding probe at `AppShell.cs:145-147` passes no sink — observed, but widening that call is outside this surgical batch.)

- [ ] **Step 2: Write the failing worker-entry tests**

Create `tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerHostProcessTests.cs`:

```csharp
using System.Diagnostics;
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

/// <summary>Spawns the REAL worker loop as a REAL child process
/// (TranscribeWorkerHost — the portable half of `Winpepper.exe
/// --transcribe-worker`) through the PRODUCTION ExeWorkerProcessFactory +
/// WorkerProcessEngine. No native model needed: TranscribeCppEngine.Load's
/// contract.json existence check runs before any native/Windows-only gate,
/// so a bogus runtime dir yields a real structured Error frame on any OS.</summary>
public sealed class WorkerHostProcessTests
{
    private static ProcessStartInfo HostPsi()
    {
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "TranscribeWorkerHost.exe" : "TranscribeWorkerHost");
        if (File.Exists(apphost)) return new ProcessStartInfo(apphost);
        // Fallback: the suite always runs under `dotnet exec` (AGENTS.md),
        // so Environment.ProcessPath is the dotnet muxer on both OSes.
        return new ProcessStartInfo(Environment.ProcessPath!,
            $"exec \"{Path.Combine(dir, "TranscribeWorkerHost.dll")}\"");
    }

    [Fact]
    public void Load_WithBogusRuntimeDir_ReturnsStructuredError_OverARealProcess()
    {
        var factory = new ExeWorkerProcessFactory(HostPsi);
        using var engine = new WorkerProcessEngine(factory,
            "/definitely-missing-runtime", "/missing.gguf", "worker-host-test");

        var ex = Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[16], null, out _));
        ex.Message.ShouldContain("contract.json not found");
    }

    [Fact]
    public void Kill_TerminatesTheRealWorkerProcess()
    {
        var factory = new ExeWorkerProcessFactory(HostPsi);
        var proc = factory.Start();
        try
        {
            proc.HasExited.ShouldBeFalse();
            proc.Kill();
            SpinWait.SpinUntil(() => proc.HasExited, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        finally { proc.Dispose(); }
    }
}
```

- [ ] **Step 3: Run them to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" \
  -class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerHostProcessTests"
```

Expected: FAIL — `TranscribeWorkerHost.dll` does not exist yet, so the spawned child exits immediately and the Load RPC fails with a connection error whose message does NOT contain `contract.json not found` (and the kill test fails at `HasExited.ShouldBeFalse()`).

- [ ] **Step 4: Create the host project**

Create `tests/TranscribeWorkerHost/TranscribeWorkerHost.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/Winpepper.Asr/Winpepper.Asr.csproj" />
  </ItemGroup>
</Project>
```

First check whether `tests/Directory.Build.props` exists (`ls tests/Directory.Build.props`). If it exists and injects test-only packages/settings (xunit, `IsTestProject`), add `<IsTestProject>false</IsTestProject>` to the `PropertyGroup` above so the host builds as a plain console exe. (At plan time no such file was observed; skip if absent.)

Create `tests/TranscribeWorkerHost/Program.cs`:

```csharp
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

// The PORTABLE half of `Winpepper.exe --transcribe-worker`
// (src/Winpepper.App/Program.cs:27-51): same loop, same engine factory, same
// stderr log prefix. Deliberately omitted, Windows-only pieces: SetErrorMode
// (WER suppression) and the MTA thread hop (this Main is not [STAThread]).
// Exists so tests can spawn the REAL worker loop as a REAL child process on
// any OS — the loop itself is pure BCL until Load touches native code.
return TranscribeWorkerLoop.Run(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    (runtimeDir, ggufPath) => TranscribeCppEngine.Load(
        runtimeDir, ggufPath, msg => Console.Error.WriteLine($"[transcribe-worker] {msg}")),
    msg => Console.Error.WriteLine($"[transcribe-worker] {msg}"));
```

Reference it from the test project — in `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`, add alongside the existing `ProjectReference` items:

```xml
    <ProjectReference Include="../TranscribeWorkerHost/TranscribeWorkerHost.csproj" />
```

(Referencing an Exe project copies `TranscribeWorkerHost.dll`, its `runtimeconfig.json`/`deps.json`, and the apphost into the test output — this is what `HostPsi()` resolves. The host lives under `tests/` so both `windows-gate.sh` and `test-windows-from-wsl.sh` pre-clean its `bin`/`obj`, preserving the cross-OS build-hygiene rule.)

- [ ] **Step 5: Rebuild, verify the host lands in the test output, run the tests**

```bash
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
ls "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/" | grep TranscribeWorkerHost
dotnet exec "$R/tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll" \
  -class "Winpepper.Asr.Tests.TranscribeCpp.Worker.WorkerHostProcessTests"
```

Expected: `TranscribeWorkerHost.dll` (plus `runtimeconfig.json`; the apphost may or may not be copied — the test handles both); both tests PASS. If the run fails with a framework-resolution error from the child, the apphost could not find the runtime — verify `DOTNET_ROOT` is exported (the suite scripts do this) and prefer the muxer fallback by deleting the copied apphost; do not weaken the test.

- [ ] **Step 6: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs \
        tests/TranscribeWorkerHost/ \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/TranscribeCpp/Worker/WorkerHostProcessTests.cs
git commit -m "test(asr): real-subprocess worker-entry coverage via portable host; log BindKillOnClose failures"
```

---

### Task 4: Rewrite the false Job-Object certification comment (council fix #5, code half)

**Files:**
- Modify: `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs:6-13` (class doc comment only)

**Interfaces:**
- Consumes: nothing. Produces: nothing (comment-only change; Task 5 writes the matching plan-doc correction, Task 10 records both).

**Decision (make-it-true vs. rewrite):** the spec prefers making the claim true only if "genuinely small and testable". Keeping job handles open for killed-but-unexited workers (or one app-lifetime job) changes kill-time reaping semantics — today `Dispose()`'s `CloseHandle` kills any kernel-un-wedged survivor immediately; an app-lifetime job would defer that to app exit — and there is no Windows Job-Object test coverage at all to catch a regression, nor any way to exercise a kernel wedge from this environment. Not small-and-testable → rewrite the comment to tell the truth.

- [ ] **Step 1: Replace the class doc comment**

Replace lines 6–13 of `src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs` (the `/// <summary>` block on `ExeWorkerProcess`) with:

```csharp
/// <summary>Real child-process IWorkerProcess: redirected stdio, stderr lines
/// forwarded to a log callback, kill = whole process tree (ggml may spawn
/// nothing today, but the tree kill is free insurance). On Windows the child
/// is additionally bound to a Job Object with KILL_ON_JOB_CLOSE (a failed
/// bind is logged — see WindowsJob.BindKillOnClose — and forfeits the job
/// guarantees below). What the job actually guarantees: if the PARENT
/// CRASHES, the kernel closes the orphaned
/// job handle and the worker — even one wedged in native code that will never
/// see stdin EOF — is killed. In the supervised path the handle is closed at
/// kill time (KillLocked -> Dispose below), which kills any survivor THEN —
/// there is no job handle left at app exit, so a killed worker that is wedged
/// in a KERNEL-mode call and survives both the kill and the job-close is NOT
/// reaped at app exit; it can linger until the kernel operation completes or
/// the OS cleans it up. That leak is the accepted residual (see the plan's A1
/// residual note). No-op on Linux, so the Linux tests are unaffected.</summary>
```

Keep the `WindowsJob` class comment (`:67-70`) as is — "Failures are tolerated (returns 0)" is accurate, and Task 3 added the missing signal.

- [ ] **Step 2: Compile check**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
R=/home/dan/code/winpepper/.worktrees/nemotron-first-asr
dotnet build "$R/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded.

- [ ] **Step 3: Full Linux suite, then commit**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Asr/TranscribeCpp/Worker/ExeWorkerProcess.cs
git commit -m "docs(asr): correct the Job Object comment — killed workers are not reaped at app exit"
```

---

### Task 5: Correct the plan document's A1 residual text and residual labels (council fix #5, doc half)

**Files:**
- Modify: `docs/plans/2026-08-08-nemotron-first-asr.md:934` (clause (d) of the "Additional contracts" line) and end of file (after line 4643)

**Interfaces:**
- Consumes: Tasks 1–2 being merged (the corrected text asserts the budget now genuinely bounds kills and between-RPC deaths — do not run this task before them). Produces: the corrected certification record Task 10's evidence doc cross-references.

**Background:** clause (d) at `:934` currently ends: *"bounded by the restart budget (3 strikes → 60 s cooldown); the Task 6 job object reaps such zombies at app exit."* Both halves were false at review time: the budget could not bound kill-path failures (fixed by Tasks 1–2), and the job object does NOT reap at app exit (its handle is closed at kill time; see Task 4). Separately, residual labels must dereference correctly: the branch validation ledger (workflow logs, not committed) tracks the VC++-redist deployment residual as row **A15** (validator **V8**), while **V6/A16** is the falsified-and-fixed onboarding readiness assumption — an earlier recap mislabeled the redist residual, and the plan doc cites `V6/A16` in several places without a key, so readers cannot dereference either id.

- [ ] **Step 1: Rewrite clause (d) at line 934**

In `docs/plans/2026-08-08-nemotron-first-asr.md`, line 934, replace exactly this text:

```
(d) **A1 residual, accepted**: killing a worker wedged in kernel-mode I/O may delay its actual exit and leak one blocked threadpool thread per kill — bounded by the restart budget (3 strikes → 60 s cooldown); the Task 6 job object reaps such zombies at app exit.
```

with:

```
(d) **A1 residual, accepted (text corrected 2026-08-09)**: killing a worker wedged in kernel-mode I/O may delay its actual exit and leak one blocked threadpool thread per kill. The Task 6 Job Object does NOT reap such zombies at app exit — the supervised path closes the per-worker job handle at kill time (`KillLocked` → `ExeWorkerProcess.Dispose`), so KILL_ON_JOB_CLOSE fires then, and a kernel-wedged worker that survives it can linger until the kernel operation completes or the OS cleans up; the job's at-exit guarantee covers only parent CRASH (the kernel closes the orphaned handle). All job guarantees are conditional on the bind succeeding — bind failures are logged as of the 2026-08-09 fix batch. The respawn side is genuinely bounded as of the 2026-08-09 fix batch (`docs/plans/2026-08-09-nemotron-first-asr.md`): operation-phase kills and between-RPC worker deaths charge the 3-strike/60 s restart budget, and only a completed operation RPC (finished batch / finalized stream) resets it.
```

- [ ] **Step 2: Add the residual label key at the end of the file**

Append after the final line (4643, the deviations list):

```markdown

### Residual label key (added 2026-08-09)

The validation ledger for this branch (workflow logs, not committed to the
repo) tracks assumptions as `A<n>` rows and validators as `V<n>`. The two
labels that have been confused:

- **A15 (evidence: validator V8)** — the accepted VC++-redistributable
  DEPLOYMENT residual: the MSI does not chain `vc_redist.x64.exe`, so a
  machine without the Microsoft Visual C++ x64 Redistributable hard-fails
  local dictation (both transcribe.cpp and ONNX Runtime import the same
  CRT). Recorded follow-up: chain the redist in a future MSI (see the
  release-backlog note above).
- **V6/A16** — the falsified-and-FIXED onboarding readiness assumption:
  file verification alone was a lying readiness proxy; `SpeechModelReady`
  additionally requires the one-shot engine load probe. The probe SURFACES
  a missing redist; the A15 residual is that nothing INSTALLS it.

An earlier recap filed the VC++-redist residual under the wrong id; read any
`V6/A16` citation in this file as the probe fix, and the accepted redist
residual as A15/V8. The 2026-08-09 evidence doc restates this key in-repo.
```

- [ ] **Step 3: Verify the labels now dereference**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
grep -n "A15\|V6/A16\|reaps" docs/plans/2026-08-08-nemotron-first-asr.md
```
Expected: `A15` appears in the new key; the only remaining "reap" claims describe the corrected semantics; clause (d) matches Step 1.

- [ ] **Step 4: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` (docs-only change; the run satisfies the every-commit rule).

```bash
git add docs/plans/2026-08-08-nemotron-first-asr.md
git commit -m "docs: correct A1 accepted-residual text and add residual label key (A15 vs V6/A16)"
```

---

### Task 6: Honor the download-progress file's own locking invariant (council fix #4)

**Files:**
- Modify: `src/Winpepper.App/Services/OnboardingModelProvisioner.cs:79-84, 112, 116, 119`

**Interfaces:**
- Consumes: existing private `Percent(IReadOnlyList<ModelDescriptor>, IReadOnlyDictionary<string, long>)` (`:169-171`) and the `_gate` lock object (`:30`). Produces: local function `double SnapshotPercent()` inside `RunAsync` — used only within that method.

**Background:** the file states at `:90-93` that "the percent snapshot must be computed under the same lock — concurrent callbacks racing an unlocked read of `done` would be a data race", and the `Progress<>` callback honors it (`:95-99`), but four sites call `Percent(selection, done)` unlocked while straggler callbacks (explicitly acknowledged at `:104-105`) may still be writing `done`: lines 84, 112, 116, 119. Add the locking so the code matches its stated invariant. Do NOT hold `_gate` across `Publish` itself — `Publish` re-takes `_gate` (`:191`, reentrant so no deadlock, but it would then hold the lock across `_dispatcherQueue.TryEnqueue`, a lock-ordering hazard); compute the snapshot under the lock, release, then publish — exactly the shape the compliant callback already uses.

**Verification honesty:** this file is `#if WINDOWS` inside `Winpepper.App`, which has no test project — there is no Linux-runnable or Windows-gate unit test that can exercise it, and building one would mean extracting the tally state into a testable assembly (a refactor the scope rules forbid). Verification = review of the diff against the invariant + Windows gate compile (Task 9). The Linux suite run below proves nothing shared broke, per `AGENTS.md:4`.

- [ ] **Step 1: Add the locked snapshot helper**

In `RunAsync`, immediately after the `done` dictionary initialization (`:78-79`), insert:

```csharp
            // The file's own invariant (see the progress callback below):
            // EVERY read of `done` for a percent snapshot must happen under
            // _gate — straggler Progress<> callbacks keep writing `done`
            // after DownloadAsync returns.
            double SnapshotPercent() { lock (_gate) return Percent(selection, done); }
```

- [ ] **Step 2: Route the four unlocked sites through it**

Replace the first argument at each site (`Percent(selection, done)` → `SnapshotPercent()`):

Line 84:
```csharp
                Publish(SnapshotPercent(), $"Downloading {Friendly(descriptor)}…", null, false);
```
Line 112:
```csharp
                    Publish(SnapshotPercent(), "Verifying speech model…", null, false);
```
Line 116:
```csharp
                        Publish(SnapshotPercent(), "Speech model failed verification.", error, false);
```
Line 119:
```csharp
                    Publish(SnapshotPercent(), "Speech model ready — keep going while the rest downloads.", null, true);
```

The compliant callback at `:94-101` stays exactly as it is. Do not "fix" the milder paired-`State` reads at `:138`/`:155` — individually locked reads of an immutable record, explicitly out of this item's scope.

- [ ] **Step 3: Verify no unlocked `Percent(` call remains in the file**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
grep -n "Percent(selection, done)" src/Winpepper.App/Services/OnboardingModelProvisioner.cs
```
Expected: exactly two hits, both inside `lock (_gate)` blocks (the callback at ~:98 and the `SnapshotPercent` local function).

- [ ] **Step 4: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Services/OnboardingModelProvisioner.cs
git commit -m "fix(app): compute onboarding percent snapshots under the tally lock, per the file's own invariant"
```

---

### Task 7: User-facing copy — VC++ redist error leads with the fix; ModelsPage header loses "(optional)" (council fix #6)

**Files:**
- Modify: `src/Winpepper.App/Services/OnboardingModelProvisioner.cs:157-160`
- Modify: `src/Winpepper.App/Views/ModelsPage.xaml:208`

**Interfaces:**
- Consumes: `Friendly(d)` (`OnboardingModelProvisioner.cs:166-167`, renders "English speech model" / "Multilingual speech model" / "backup speech model"); the new-architecture role vocabulary from the onboarding picker (`OnboardingPage.xaml:101` — the Nemotron streaming model's role label is "Speech model"). Produces: nothing consumed by later tasks.

**Verification honesty:** both files are Windows-only (no `Winpepper.App` test project; no test asserts either string — verified at plan time). Verification = review + Windows gate compile (XAML compiles in gate stage 1). Linux suite run proves nothing shared broke.

- [ ] **Step 1: Rewrite the engine-load failure copy**

In `src/Winpepper.App/Services/OnboardingModelProvisioner.cs`, replace lines 157–160:

```csharp
        if (!probeOk)
            return $"The {Friendly(d)} downloaded and verified, but its speech engine failed to load. " +
                   "Open Settings > Models to repair it. A missing Microsoft Visual C++ x64 Redistributable " +
                   "is the most common cause.";
```

with:

```csharp
        if (!probeOk)
            return $"The {Friendly(d)} downloaded and verified, but its speech engine failed to load. " +
                   "Install the Microsoft Visual C++ x64 Redistributable " +
                   "(aka.ms/vs/17/release/vc_redist.x64.exe) — a missing redistributable is the most " +
                   "common cause — then retry. If it still fails, open Settings > Models to repair the model.";
```

Rationale: the model already passed size + SHA-256 + extraction (`:152-154`), so "repair the model" cannot be the leading remedy; the actionable fix (install the redist, with the same download link the engine layer names in `TranscribeCppEngine.cs:106-109`) must come first. The two "Retry the download." strings at `:151`/`:154` really are download-failure cases — leave them.

- [ ] **Step 2: Fix the stale ModelsPage header**

In `src/Winpepper.App/Views/ModelsPage.xaml`, line 208, replace:

```xml
                            <TextBlock Text="Live streaming model (optional)" Style="{ThemeResource BodyStrongTextBlockStyle}" />
```

with:

```xml
                            <TextBlock Text="Speech model" Style="{ThemeResource BodyStrongTextBlockStyle}" />
```

Rationale: under nemotron-first the Nemotron streaming model is the PRIMARY speech model (pipeline startup gates on it — `AppShell.cs:528-536`); "(optional)" is now wrong, and this is the exact card the Step 1 error copy sends users to. "Speech model" matches the onboarding picker's role label verbatim. The caption on line 209 (already updated by this branch) stays. Other stale-ish headers on the page (`:23-24`, `:89`, `:142-143`) are adjacent drift, NOT in this item's scope — leave them.

- [ ] **Step 3: Verify the stale string is gone**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
grep -rn "Live streaming model (optional)" src/
grep -n "vc_redist.x64.exe" src/Winpepper.App/Services/OnboardingModelProvisioner.cs
```
Expected: first grep — no hits in `src/`; second — one hit (the new copy).

- [ ] **Step 4: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Services/OnboardingModelProvisioner.cs src/Winpepper.App/Views/ModelsPage.xaml
git commit -m "fix(app): lead the engine-load failure copy with the VC++ redist fix; drop stale '(optional)' from the speech model card"
```

---

### Task 8: Delete dead `IAsrProvisioningService` (council fix #7)

**Files:**
- Delete: `src/Winpepper.Core/ViewModels/IAsrProvisioningService.cs`
- Modify: `src/Winpepper.App/Services/ModelsServices.cs`

**Interfaces:**
- Consumes: the reference inventory verified at plan time — 1 definition + 1 code reference (`ModelsServices.cs:8` base list) + docs-only mentions; zero references in tests, csproj, XAML; **no DI container exists in this repo** (all wiring is manual construction in `AppShell.cs`), so there is nothing to unregister. Produces: nothing (pure deletion).

**Background:** `AsrPipelineStartupGate` was refactored on this branch to take a `Func<CancellationToken, Task<bool>>` verify delegate; the interface (and its `AsrProvisioningStatus` enum + `AsrProvisioningState` record, same file) is now orphaned, along with the `ModelsServices` members that existed only to satisfy it. **KEEP `ModelsServices.VerifyReadyAsync(CancellationToken)` (`:63-71`)** — it has a live internal caller (`:151`, inside `VerifyPrimarySpeechReadyAsync`, used by `AppShell.cs:529`); it just stops being an interface member.

- [ ] **Step 1: Delete the interface file**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
git rm src/Winpepper.Core/ViewModels/IAsrProvisioningService.cs
```

- [ ] **Step 2: Strip the orphaned members from `ModelsServices.cs`**

Read the file first, then remove, as ONE atomic edit (line numbers from `dc73c52`):
- `:8` — remove `IAsrProvisioningService, ` from the class base list (keep `ModelsTabViewModel.IDownloader` and `IDisposable`).
- `:10` — delete field `private AsrProvisioningState _state = new(AsrProvisioningStatus.Missing);`
- `:20` — delete ctor line `_coordinator.StateChanged += OnCoordinatorStateChanged;`
- `:21` — delete ctor line `_state = MapState(_coordinator.State);`
- `:27` — delete `public AsrProvisioningState State => _state;`
- `:29` — delete `public event EventHandler<AsrProvisioningState>? StateChanged;`
- `:60-61` — delete `public Task EnsureReadyAsync(CancellationToken ct)` (no caller anywhere).
- `:154-158` — delete `private void OnCoordinatorStateChanged(...)`.
- `:160-172` — delete `private static AsrProvisioningState MapState(...)`.
- `:86` — the XML doc contains `<see cref="State"/>`; reword that phrase to plain text (e.g. "the coordinator's global state") so no CS1574 dangling-cref warning appears.
- KEEP `VerifyReadyAsync` (`:63-71`) exactly as is.

- [ ] **Step 3: Verify zero remaining references**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
git grep -n "IAsrProvisioningService\|AsrProvisioningState\|AsrProvisioningStatus" -- src/ tests/
```
Expected: no output. (Docs mentions in `docs/plans/*.md` are historical records — leave them.)

- [ ] **Step 4: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` — this also proves `Winpepper.Core` still compiles without the file (Core is referenced by the Linux-built test projects). `ModelsServices.cs` is Windows-only (`Winpepper.App`); its compile is proven by the Task 9 gate — if the gate later reports CS errors here, fix forward per Task 9 Step 3.

```bash
git add -A src/Winpepper.Core/ViewModels/ src/Winpepper.App/Services/ModelsServices.cs
git commit -m "refactor(core,app): delete dead IAsrProvisioningService and its orphaned ModelsServices members"
```

---

### Task 9: Final verification — full Linux suite + Windows gate at final code HEAD

**Files:** none (verification only; fix-forward commits allowed if the gate finds issues).

**Interfaces:**
- Consumes: all prior tasks committed. Produces: `/tmp/windows-gate-nemotron-fixbatch.log` containing the verbatim gate summary and `GATE: GREEN`, plus the SHA it ran against — Task 10's evidence doc quotes both.

- [ ] **Step 1: Full Linux suite at final code HEAD**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh 2>&1 | tail -20
git rev-parse --short HEAD
```
Expected: `linux-tests grand total: <N> tests` then `LINUX SUITE: GREEN`. Record N and the SHA — Task 10 needs both verbatim.

- [ ] **Step 2: Windows gate (from WSL, background, 20–40 min budget)**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
"$PS" -NoProfile -Command "Write-Output interop-ok"  # must print interop-ok; if it hangs/errors, the WSL vsock interop is in an outage window — wait and re-probe (see Step 3) before burning a ~20 min gate run
git rev-parse --short HEAD > /tmp/windows-gate-nemotron-fixbatch.sha
nohup ./scripts/windows-gate.sh > /tmp/windows-gate-nemotron-fixbatch.log 2>&1 &
# Poll until done (do NOT run linux-tests.sh concurrently):
tail -f /tmp/windows-gate-nemotron-fixbatch.log
```
Expected: exit 0, final line `GATE: GREEN` (non-zero `Skipped` counts are normal — model-presence skips; record them honestly). The gate script prints no SHA — that is why Step 2 stamps `/tmp/windows-gate-nemotron-fixbatch.sha` first; the two files together are the evidence inputs.

- [ ] **Step 3: On RED — first distinguish ENVIRONMENTAL from CODE red, then retry or fix forward**

Read `artifacts/windows-gate/*.log` for the failing stages.

**ENVIRONMENTAL RED (known failure mode, observed under load-bearing validation 2026-08-09):** failing stages have tiny (~66-byte) logs containing `UtilAcceptVsock ... accept4 failed 110` and the gate summary shows `<no summary line>` for them — a transient WSL→Windows vsock interop outage, not a code failure. (Two validation gate runs at `9c080c4` went RED exactly this way while every stage that actually executed was green — 863 tests, 0 failures; the prior branch GREEN at `4d5e63d` also needed vsock retries.) Do NOT change code: wait for the interop to answer again (outages were observed to self-heal within ~10 min — re-probe with the Step 2 `interop-ok` command), then re-run Step 2 in full (fresh SHA stamp + fresh log). If still environmentally blocked after 3 full attempts, STOP retrying and record the state honestly in Task 10's evidence doc — gate: BLOCKED-ENVIRONMENTAL with the verbatim log lines, never claimed as GREEN — and note the remaining remediation for the user: run `wsl.exe --shutdown` from Windows and retry (it kills every WSL session including this workflow's; never run it from here).

**CODE RED:** fix, run `./scripts/linux-tests.sh` (must be GREEN), commit as `fix(gate): <what>`, then re-run Step 2 in full (fresh SHA stamp + fresh log). Repeat until GREEN. Never paper over a RED.

- [ ] **Step 4: No commit**

The gate being GREEN satisfies the pre-push rule; recording happens in Task 10's evidence doc (with the SHA), per this batch's item 8 — not only in a commit message.

---

### Task 10: Windows smoke + in-repo verification evidence (council fixes #8 + #10)

**Files:**
- Create: `docs/plans/2026-08-09-nemotron-first-asr-evidence.md`
- Scratch (not committed): `/tmp/reap-check.ps1`

**Interfaces:**
- Consumes: Task 9's `/tmp/windows-gate-nemotron-fixbatch.log` + `.sha` and the Linux suite tail; `scripts/smoke-windows.ps1` (machine-checkable smoke: install payload, registry, `--tray` launch + process-alive, log freshness, `--selftest`; params `-RunSelftest`, `-SkipLaunch`; statuses PASS/FAIL/WARN/MANUAL); `scripts/windows-sandbox/Launch-WinpepperSandbox.ps1` (fresh-profile onboarding; requires an MSI artifact + Windows Sandbox feature; no microphone inside). Produces: the committed evidence file.

**Honesty rules for this task (non-negotiable):** every check below is recorded with its VERBATIM output and one of PASS / FAIL / MANUAL / SKIPPED(+why). Anything that cannot be executed from this environment is recorded as MANUAL with exactly what remains — never claimed. Never touch a `Winpepper.exe` this task did not launch (the user's instance may be running).

- [ ] **Step 1: Run the machine-checkable smoke**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
# Pre-existing instance check — if Winpepper.exe is already running, pass -SkipLaunch
# and record that launch/kill checks were skipped to protect the user's session:
"$PS" -NoProfile -Command "Get-Process Winpepper -ErrorAction SilentlyContinue | Select-Object Id, ProcessName"
"$PS" -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w scripts/smoke-windows.ps1)" -RunSelftest 2>&1 | tee /tmp/smoke-windows-fixbatch.log
```
Expected: exit 0 (no FAILs); WARN/MANUAL lines are recorded as-is. If Winpepper is not MSI-installed on this host, the script reports FAILs on install checks — record that verbatim and mark the affected sub-checks SKIPPED (not installed), do not fake them. Also record the INSTALLED binary's identity next to the selftest result (e.g. `"$PS" -NoProfile -Command "(Get-Item 'C:\Program Files\Winpepper\Winpepper.exe').VersionInfo, (Get-Item 'C:\Program Files\Winpepper\Winpepper.exe').LastWriteTime"` — adjust the path to the script's install dir): the smoke exercises the installed build, which may predate this branch, and the evidence must say which build was actually tested. (Safety, validated 2026-08-09: `--selftest` returns before the singleton registration and writes nothing beyond idempotent dir-creates, so running it beside a live user instance is side-effect-free; the script never kills processes and suppresses launch when an instance is already running.)

- [ ] **Step 2: Fresh-profile onboarding path**

```bash
ls artifacts/winpepper-*-x64.msi 2>/dev/null
PS=/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe
"$PS" -NoProfile -Command "Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM | Select-Object State" 2>/dev/null
```
If an MSI exists AND Windows Sandbox is enabled: run `"$PS" -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w scripts/windows-sandbox/Launch-WinpepperSandbox.ps1)"`, observe the in-sandbox install + `--selftest`, and record the result (onboarding UI walkthrough inside the sandbox is human-observed — record what was actually seen). Otherwise record: `Fresh-profile onboarding: MANUAL — no MSI artifact / Sandbox feature unavailable; what remains: install on a fresh profile, complete the model picker, verify 'ready to dictate'.` Do not build an MSI just for this (out of scope).

- [ ] **Step 3: Real dictation**

A real dictation requires a microphone and an interactive desktop — genuinely not automatable from WSL (the QEMU audio path is unprovisioned; Sandbox has no mic; both facts recorded on prior branches). If a human operator is available at the console: hold the hotkey, speak a sentence, verify text lands in the focused app; record their verbatim confirmation. Otherwise record: `Real dictation: MANUAL — requires mic + interactive desktop; not claimed.`

- [ ] **Step 4: Kill-the-app → worker-dies (Job Object reaping check)**

Only meaningful while a worker child is alive (the engine holder spawns it lazily on the first streaming dictation and keeps it loaded). Write `/tmp/reap-check.ps1`:

```powershell
$app = Get-CimInstance Win32_Process -Filter "Name='Winpepper.exe'" |
       Where-Object { $_.CommandLine -notmatch 'transcribe-worker' } | Select-Object -First 1
if (-not $app) { Write-Output 'REAP-CHECK: NO APP RUNNING'; exit 2 }
$worker = Get-CimInstance Win32_Process -Filter "Name='Winpepper.exe'" |
          Where-Object { $_.CommandLine -match 'transcribe-worker' -and $_.ParentProcessId -eq $app.ProcessId } |
          Select-Object -First 1
if (-not $worker) { Write-Output 'REAP-CHECK: NO WORKER ALIVE (do one dictation first)'; exit 2 }
Write-Output ("REAP-CHECK: app={0} worker={1}" -f $app.ProcessId, $worker.ProcessId)
Stop-Process -Id $app.ProcessId -Force   # simulate app crash/kill — job close must reap the worker
$dead = $false
foreach ($i in 1..20) {
  Start-Sleep -Milliseconds 500
  if (-not (Get-Process -Id $worker.ProcessId -ErrorAction SilentlyContinue)) { $dead = $true; break }
}
Write-Output ("REAP-CHECK: worker {0} dead within 10s = {1}" -f $worker.ProcessId, $dead)
```

Run it ONLY against an instance launched for this smoke (Step 1 launch or a fresh manual launch after the Step 3 dictation — a dictation must have happened for a worker to exist), never against a pre-existing user instance:

```bash
"$PS" -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w /tmp/reap-check.ps1)" 2>&1 | tee -a /tmp/smoke-windows-fixbatch.log
```
Expected when runnable: `REAP-CHECK: worker <pid> dead within 10s = True`. If it prints `NO WORKER ALIVE` (no dictation possible per Step 3), record: `Job-Object reap check: MANUAL — needs one real dictation first; script provided at /tmp/reap-check.ps1; not claimed.`

Note on mechanism: killing the app closes the worker's job handle two ways — orderly shutdown disposes the engine (`KillLocked`), and a forced kill orphans the handle for the kernel to close — either way the worker must exit; this check verifies the observable outcome (worker gone), which is exactly what the corrected Task 4 comment promises.

- [ ] **Step 5: Write the evidence file**

Create `docs/plans/2026-08-09-nemotron-first-asr-evidence.md` with this structure, filling every `<...>` from the actual recorded outputs (verbatim; never invented):

````markdown
# Nemotron-First ASR council fix batch — verification evidence

Branch: `feat/nemotron-first-asr` (forked from `080e4f1`). Fix batch plan:
`docs/plans/2026-08-09-nemotron-first-asr.md`. This doc records the
verification evidence the council review found missing, per its item 8.

## Linux suite at final code HEAD

Run date: <date>. HEAD: `<git rev-parse --short HEAD>`.

```
<verbatim last lines of ./scripts/linux-tests.sh: per-project summaries if desired,
the "linux-tests grand total: N tests" line, and "LINUX SUITE: GREEN">
```

## Windows pre-push gate result

Run date: <date>. Ran against commit `<contents of /tmp/windows-gate-nemotron-fixbatch.sha>`
(`git rev-parse --short HEAD`, stamped immediately before launching the gate —
the gate logs themselves contain no SHA).

Verbatim summary block from `/tmp/windows-gate-nemotron-fixbatch.log`:

```
<the "================ windows-gate summary ================" block through the final "GATE: ..." line — GREEN, or the honest BLOCKED-ENVIRONMENTAL/RED state per Task 9 Step 3; never invented>
```

Honesty note: Skipped totals <n> across the 12 runs (<which self-skips>).
<If HEAD moved after the gate run (this evidence commit): "Commits after the
gate SHA are docs-only — verified: `git diff --stat <gate-sha>..HEAD` touches
only docs/plans/*.">

## Review-claims record (corrected 2026-08-09)

A previous recap of this branch claimed a "second independent review pass"
without locatable artifacts, and an earlier draft of this correction was
going to withdraw the claim as artifact-free. Load-bearing validation of
this fix batch then LOCATED the artifacts: the claim is substantiated, not
withdrawn — the artifacts live in the workflow logs archive, not the repo,
which is why they were initially missed. The full review record over this
branch, with artifact locations (workflow logs under
`.the-usual-logs/nemotron-first-asr/`, archived under
`prior-run-archive-20260809/`):

1. Plan-stage load-bearing validation — assumption ledger + validator
   reports V1–V10 (archived).
2. Execute-stage whole-branch review + re-review — initial verdict "With
   fixes", re-review confirmed both fixes resolved (artifacts:
   `sdd/final-review-fix-report.md`, `review-c73b9f1..4d5e63d.diff`,
   recorded in the archived `execute-result.json`).
3. Independent cross-model fresh-eyes CODE review of `080e4f1..HEAD`
   (`fresheyes-delta.md`): iteration 1 FAILED on a bench-compile blocker
   (fixed in `dc73c52`), iteration 2 PASSED with 0 blocking issues; plus
   an independent fresh-eyes PLAN review (`fresheyes-plan.md`). This is
   most plausibly the "second independent review pass" the recap referred
   to (the recap's original text itself was not archived, so its exact
   referent cannot be asserted).
4. The 2026-08-09 adversarial council review that produced this fix batch
   (its verdict is carried in the fix-batch plan itself; no separate
   council report file was archived).

## Residual label key (restated in-repo)

- A15 (validator V8): accepted VC++-redistributable deployment residual —
  the MSI chains no redist; machines without it hard-fail local dictation.
- V6/A16: the falsified-and-fixed onboarding readiness assumption — file
  verification alone lied; SpeechModelReady now requires the engine load probe.

## Windows smoke (council item 10)

| Check | Status | Evidence |
|---|---|---|
| smoke-windows.ps1 -RunSelftest | <PASS/FAIL/SKIPPED> | <verbatim tail / reason> |
| Fresh-profile onboarding | <PASS/MANUAL/SKIPPED> | <what was actually exercised> |
| Real dictation | <PASS(human-verified)/MANUAL> | <verbatim confirmation / "not claimed"> |
| Kill app → worker dies (job reap) | <PASS/MANUAL> | <REAP-CHECK output / "not claimed"> |

<verbatim /tmp/smoke-windows-fixbatch.log content or its relevant sections>

## Remaining for the user

<explicit list of every MANUAL item above, or "None.">
````

Going forward (per the corrected Task 22 Step 4 practice): gate results are recorded WITH the commit SHA they ran against, in the evidence doc.

- [ ] **Step 6: Full Linux suite, then commit the evidence**

```bash
cd /home/dan/code/winpepper/.worktrees/nemotron-first-asr && ./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN`.

```bash
git add docs/plans/2026-08-09-nemotron-first-asr-evidence.md
git commit -m "docs: record fix-batch verification evidence (suite, gate with SHA, smoke, review-claims correction)"
```

This is the final commit. Leave `feat/nemotron-first-asr` unmerged; do not push unless the workflow says otherwise (the gate being GREEN satisfies the pre-push rule if it does).

---

## Recorded backlog (explicitly out of scope — do not do)

Carried from the council verdict for a future batch: PipelineHost backup-role machinery simplification; duplicated selection-slot class; `"-batch"` string-suffix invariant; rerun-vs-dictation lock-convoy; MSI vc_redist chaining (also noted at `2026-08-08-nemotron-first-asr.md:4559`); model-quality evaluation work; ModelsPage's other stale card headers (`:23-24`, `:89`, `:142-143`) and the files-only "Installed" predicate on the recovery card (`ModelsPage.xaml.cs:459-472`); a stderr/log sink for the `AppShell.cs:145-147` probe factory; RPC deadline tuning for slow hardware — the per-op deadlines (batch 30 s + 2 s/audio-s, feed 10 s, finalize 20 s, begin 15 s) are validated only on the dev host (~1.13x headroom), and timeout kills now charge the restart budget, so if field reports show budget-exhausted lockouts on honest-but-slow machines, widen the constants (one-line tunables; the 60 s cooldown meanwhile bounds the blast radius to periodic retry).
