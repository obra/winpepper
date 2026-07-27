# Streaming Dictation Drain Hardening Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix two confirmed bugs in the streaming dictation coordinator: (A) a NullReferenceException race where the pump dereferences the nullable `_session` field that a concurrent abandon nulls, and (B) a post-stop "10 s bounded" drain that is not actually bounded (FinishAsync inline-awaits a session dispose that blocks behind a wedged native P/Invoke) and that pointlessly waits the full deadline when zero pushes ever completed.

**Architecture:** All coordinator changes live in `StreamingDictationSession` (per-dictation glue between the audio frame channel and a streaming session). The pump captures its session in a local so field-nulling can never NRE it. The abandon paths (FinishAsync drain-timeout and DisposeAsync) never await a session dispose inline; instead a background chain disposes immediately (which aborts socket-style sessions and unwedges their pumps) and re-disposes after the pump exits (covering the pump-assigned-the-session-late race; for native sessions the dispose queues behind the session's native gate until the wedged P/Invoke returns — by design, since dispose cannot interrupt a P/Invoke). A volatile flag tracks whether any push ever completed; zero-push finishes use a short 1.5 s drain deadline because there is no streamed-latency win to preserve. A dispose guard is added to `FallbackStreamingTranscriber`'s session (the nemotron session already has one). Slow nemotron native calls (begin/feed/finalize) log a WRN with duration.

**Tech Stack:** C# / .NET 9 (`net9.0`), xUnit v3 (in-process runner via `dotnet exec`), Shouldly, `Microsoft.Extensions.Logging`. No new dependencies.

## Global Constraints

- Work inside the worktree: `/home/dan/code/winpepper/.worktrees/streaming-drain-hardening` (branch `fix/streaming-drain-hardening`). All paths below are relative to it.
- Root causes are CONFIRMED — do not re-litigate them; implement the fixes below with TDD.
- **Never use `dotnet test`.** Build test projects with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, then run via the xUnit v3 in-process runner: `dotnet exec <built test dll> -notrait "Platform=Windows"` (AGENTS.md).
- Environment for every build/test command:
  ```bash
  export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
  export PATH="$DOTNET_ROOT:$PATH"
  ```
- All tests green before every commit. On Linux the pre-commit bar is the pure-managed subset; this change is entirely in `Winpepper.Asr` (+ its tests), which is fully Linux-buildable/runnable.
- Before any **push**: `./scripts/windows-gate.sh` must pass (12 project/TFM runs, ~12 min — use a 20–30 min timeout; exit 0 + `GATE: GREEN`). Do not push without it. Do not run `./scripts/linux-tests.sh` concurrently with the gate.
- Do not mix Linux and Windows builds in the same `bin/`/`obj/` (helper scripts pre-clean).
- Commit messages follow existing style: `fix(asr): ...`, `feat(asr): ...`, `docs: ...`. One focused commit per task.
- Keep all existing tests green. Two existing tests in `StreamingDictationSessionTests.cs` are amended in Task 3 because the fix intentionally changes *when* the abandoned session's dispose completes (their assertions' intent is preserved; only synchronization changes).
- Repo style notes: `Nullable=enable` with `WarningsAsErrors=nullable`; tests always pass `TestContext.Current.CancellationToken`; Shouldly assertions in `StreamingDictationSessionTests.cs` / `FallbackStreamingTranscriberTests.cs`, bare xUnit `Assert` in `NemotronStreamingTranscriberTests.cs` (match each file's local style); test fakes are named after the behavior they model and carry dense comments explaining the production failure being modeled.

---

## File Structure

| File | Role in this plan |
|---|---|
| `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs` (148 lines) | The coordinator. Tasks 1, 3, 4 modify the pump, `FinishAsync`, `DisposeAsync`, `DisposeSessionAsync`, and add `ScheduleAbandonedSessionDispose` + `ZeroPushDrainDeadline` + `_anyPushCompleted`. |
| `src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs` (138 lines) | Task 2 adds a `_disposed` guard to its inner `Session` (parity with the nemotron session). |
| `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs` (206 lines) | Task 5 adds Stopwatch-based WRN logging around native begin/feed/finalize calls, with an injectable threshold seam. |
| `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` (241 lines) | Tasks 1, 3, 4 add fakes + tests; Task 3 amends two existing tests' synchronization. |
| `tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs` | Task 2 adds one test. |
| `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs` | Task 5 adds two tests. |
| `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` | Task 5 adds an optional `FeedDelay` knob to the existing shared fake. |
| `tests/Winpepper.Asr.Tests/CapturingLogger.cs` | **New** (Task 1): tiny shared `ILogger` that records warning+ messages; reused by Task 5. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | **Read-only audit** in Task 3 (no changes needed — verified below). |

Key existing signatures the tasks build on (all in namespace `Winpepper.Asr.Transcription`):

```csharp
public interface IStreamingTranscriptionSession : IAsyncDisposable
{
    ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);
    Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct);
}

public interface IStreamingTranscriber
{
    string ModelName { get; }
    Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct);
}

public static StreamingDictationSession Start(
    Func<CancellationToken, Task<IStreamingTranscriber?>> transcriberFactory,
    ILogger log,
    CancellationToken ct,
    TimeSpan? drainDeadline = null);   // default 10 s

public bool DrainTimedOut { get; private set; }
public Task PumpCompletion => _pump;
```

---

### Task 1: Fix A — pump pushes via a local session reference (kills the NRE race)

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs:38-59` (the pump task inside the constructor)
- Create: `tests/Winpepper.Asr.Tests/CapturingLogger.cs`
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`

**Interfaces:**
- Consumes: `IStreamingTranscriptionSession.PushAsync/DisposeAsync` (above). The nemotron session's `PushAsync` is a verified benign no-op after dispose (`NemotronStreamingTranscriber.cs:87`: `if (_disposed || _corrupt) return ValueTask.CompletedTask;` under `lock (_nativeGate)`).
- Produces: `public sealed class CapturingLogger : ILogger` with `public List<string> Warnings { get; }` (namespace `Winpepper.Asr.Tests`) — reused by Task 5. Pump behavior contract for later tasks: after an abandon, the pump keeps draining queued frames into the (disposed, no-op) session via its own local reference and completes without error.

**Context (confirmed root cause):** The pump does `_session = await transcriber.StartSessionAsync(ct);` then dereferences the **nullable field** `_session` on every loop iteration. The abandon path (`DisposeSessionAsync`, `StreamingDictationSession.cs:141-147`) nulls `_session` concurrently. `_frames.Writer.TryComplete()` does not stop the pump immediately — `ReadAllAsync` still yields already-queued frames — so the pump's next iteration NREs, which is caught and logged as WRN `"streaming dictation pump failed"` (noise that masks real pump errors; observed 3× in the incident log).

- [ ] **Step 1: Create the shared capturing logger**

Create `tests/Winpepper.Asr.Tests/CapturingLogger.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Tests;

/// <summary>Records formatted Warning+ messages so tests can assert on log
/// noise (e.g. that the pump does NOT warn on an ordinary abandon race).</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings
    {
        get { lock (_warnings) return _warnings.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        lock (_warnings) _warnings.Add(formatter(state, exception));
    }
}
```

(Lock because the pump task logs from a background thread while the test asserts from the test thread.)

- [ ] **Step 2: Write the failing test**

In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`, add after the existing `Dispose_AbandonsWithoutTranscribing_AndNeverThrows` test:

```csharp
    // Models the abandon race behind Bug A: dispose lands while the pump is
    // mid-push and MORE frames are already queued (completing the writer does
    // not stop ReadAllAsync from yielding them). DisposeAsync releases the
    // in-flight push SUCCESSFULLY — this is the ordinary silence-drop abandon,
    // not a failure. On the pre-fix code the pump's next iteration dereferences
    // the nulled `_session` field, NREs, and logs "streaming dictation pump
    // failed".
    private sealed class BlocksFirstPushTranscriber : IStreamingTranscriber
    {
        public string ModelName => "blocks-first-push";
        public BlockingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class BlockingSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _firstPushStarted = new();
            private readonly TaskCompletionSource _release = new();
            private int _pushes;

            public Task FirstPushStarted => _firstPushStarted.Task;
            public int PushCount => Volatile.Read(ref _pushes);

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _pushes) == 1)
                {
                    _firstPushStarted.TrySetResult();
                    await _release.Task; // held until DisposeAsync abandons the dictation
                }
                // Pushes after dispose are the benign no-op both production
                // sessions implement — just count them.
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run — this dictation is abandoned");

            public ValueTask DisposeAsync()
            {
                _release.TrySetResult(); // the in-flight push completes normally
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task AbandonWithQueuedFrames_PumpDrainsWithoutError()
    {
        var log = new CapturingLogger();
        var transcriber = new BlocksFirstPushTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            log, TestContext.Current.CancellationToken);
        session.OnFrame(new float[10]);
        session.OnFrame(new float[10]);
        session.OnFrame(new float[10]);
        await transcriber.Session.FirstPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await session.DisposeAsync(); // silence-drop abandon: frames 2 and 3 are still queued

        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // The pump drained the remaining frames through its OWN reference —
        // no NRE, no "streaming dictation pump failed" noise.
        transcriber.Session.PushCount.ShouldBe(3);
        log.Warnings.ShouldBeEmpty();
    }
```

- [ ] **Step 3: Run the test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Asr.Tests.StreamingDictationSessionTests
```

Expected: `AbandonWithQueuedFrames_PumpDrainsWithoutError` FAILS — `PushCount` is 1 (not 3) and/or `Warnings` contains `streaming dictation pump failed` (the NRE was caught and logged). All other tests in the class PASS.

- [ ] **Step 4: Implement the local capture**

In `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`, replace lines 49–51 (inside the pump `Task.Run`):

```csharp
                _session = await transcriber.StartSessionAsync(ct);
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    await _session.PushAsync(frame, ct);
```

with:

```csharp
                var session = await transcriber.StartSessionAsync(ct);
                _session = session;
                // Push via the LOCAL reference, never the nullable field: an
                // abandon (silence-drop / cancel / drain timeout) nulls
                // _session concurrently with this loop, and completing the
                // writer does not stop ReadAllAsync from yielding frames that
                // are already queued. Pushing into a disposed session is a
                // benign no-op by session contract.
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    await session.PushAsync(frame, ct);
```

- [ ] **Step 5: Verify the push-after-dispose posture of both production sessions (read-only)**

Confirm (do not change code in this step):
- `NemotronStreamingTranscriber.cs:82-113` — `PushAsync` takes `lock (_nativeGate)` and returns `ValueTask.CompletedTask` when `_disposed || _corrupt` (line 87). Push-after-dispose is a **safe no-op**. Nothing to add.
- `FallbackStreamingTranscriber.cs:81-97` — its `Session.PushAsync` has **no** dispose guard: after `DisposeAsync` it re-enters `_inner.PushAsync` on the disposed inner (AssemblyAI socket) session; a resulting throw is swallowed into `_failure`, which silently forces the local-batch path and fires the user-facing "cloud unavailable" toast. **Not benign** — Task 2 adds the guard. (This ordering is deliberate: Task 1's fix makes the pump *more* likely to push after dispose, so the guard lands immediately after in Task 2.)

- [ ] **Step 6: Run the whole test class to verify green**

Same commands as Step 3. Expected: ALL tests in `StreamingDictationSessionTests` PASS (the permanently-wedged test still takes ~5 s — that is pre-existing and addressed in Task 3).

- [ ] **Step 7: Run the full Asr test project**

```bash
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: summary line ends with `Errors: 0, Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs \
        tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs \
        tests/Winpepper.Asr.Tests/CapturingLogger.cs
git commit -m "fix(asr): pump pushes via a local session reference, not the nullable field"
```

---

### Task 2: Fix A follow-up — dispose guard in FallbackStreamingTranscriber's session

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs:68-137` (inner `Session`)
- Test: `tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: the coordinator pump behavior from Task 1 (queued frames may be pushed into an already-disposed session).
- Produces: `FallbackStreamingTranscriber.Session.PushAsync` is a benign no-op after `DisposeAsync` (parity with the nemotron session). No public API change.

**Context:** Without the guard, a post-dispose push lands on the disposed inner `AssemblyAiStreamingSession`, whose throw is swallowed into `_failure` — converting a working cloud dictation into a local-fallback one (plus toast) because of a lifecycle race, not a network failure.

- [ ] **Step 1: Write the failing test**

In `tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs`, add (using the file's existing `Wrap(...)` helper and shared fakes `FakeStreamingTranscriber` / `FakeTranscriber`):

```csharp
    [Fact]
    public async Task PushAfterDispose_IsANoOp_AndNeverReachesTheDisposedInner()
    {
        var primary = new FakeStreamingTranscriber("assemblyai/universal-streaming");
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.DisposeAsync(); // the pipeline abandons the dictation

        // The coordinator's pump legitimately drains queued frames after an
        // abandon (it pushes via its own local reference). That must be a
        // benign no-op — not a push into the DISPOSED inner socket session,
        // whose throw would poison _failure and silently convert a working
        // cloud dictation into a local-fallback one.
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);

        primary.LastSession!.Pushes.ShouldBe(0);
        primary.LastSession.Disposed.ShouldBeTrue();
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Asr.Tests.FallbackStreamingTranscriberTests
```

Expected: `PushAfterDispose_IsANoOp_AndNeverReachesTheDisposedInner` FAILS with `Pushes` should be `0` but was `1` (the fake inner happily counts the post-dispose push; the real inner would throw). All other tests PASS.

- [ ] **Step 3: Implement the guard**

In `src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs`, inside `private sealed class Session`:

Add a field after `private Exception? _failure;` (line 72):

```csharp
        private volatile bool _disposed;
```

Replace the first line of `PushAsync` (line 83):

```csharp
            if (_failure is not null || _inner is null) return;
```

with:

```csharp
            // Push-after-dispose is a benign no-op (parity with the nemotron
            // session): the coordinator's pump may legitimately drain queued
            // frames after the pipeline abandoned the dictation. Without this
            // guard the push lands on the DISPOSED inner socket session, whose
            // throw would poison _failure and silently force the local-batch
            // path (plus the user-facing "cloud unavailable" toast) on a
            // lifecycle race rather than a real network failure.
            if (_disposed || _failure is not null || _inner is null) return;
```

Replace `DisposeAsync` (lines 133–136):

```csharp
        public async ValueTask DisposeAsync()
        {
            if (_inner is not null) await _inner.DisposeAsync();
        }
```

with:

```csharp
        public async ValueTask DisposeAsync()
        {
            _disposed = true; // set BEFORE disposing the inner: pushes racing past this point must not reach it
            if (_inner is not null) await _inner.DisposeAsync();
        }
```

- [ ] **Step 4: Run the test class to verify green**

Same commands as Step 2. Expected: ALL tests in `FallbackStreamingTranscriberTests` PASS.

- [ ] **Step 5: Run the full Asr test project**

```bash
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/FallbackStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/FallbackStreamingTranscriberTests.cs
git commit -m "fix(asr): make FallbackStreamingTranscriber push-after-dispose a benign no-op"
```

---

### Task 3: Fix B1 + B3 — truly bounded abandon: background session dispose, corrected comments

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs` (class summary comment; `FinishAsync:93-124`; `DisposeAsync:131-139`; `DisposeSessionAsync:141-147`; new private method)
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs` (one new fake + one new test; amend two existing tests' synchronization)
- Audit (read-only, no changes): `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes: `PumpCompletion` / `DrainTimedOut` contracts (XML docs at `StreamingDictationSession.cs:74-86`); Task 1's pump (local-reference pushes).
- Produces: `private Task ScheduleAbandonedSessionDispose()` — used again verbatim by this task's two call sites; Task 4 does not touch it. `FinishAsync`'s drain-timeout path returns null within the drain deadline + scheduling epsilon (never blocked by session dispose). `DisposeAsync` worst case ≈ 6 s (5 s pump bound + 1 s grace), and ≈ 0 s when `DrainTimedOut` is already set. `DisposeSessionAsync` becomes `Interlocked.Exchange`-based (idempotent + race-safe).

**Design note (spec conformance):** The spec says "schedule the actual session.DisposeAsync() to run in the background once the pump task completes." The background chain implemented here disposes **immediately** in the background *and again* after the pump exits. The immediate attempt is required to preserve the existing socket-abort semantics (for cloud sessions, dispose is *what unwedges* a wedged send — the existing `WedgedPush_...` test models exactly that); for native sessions the immediate attempt simply queues behind the session's `_nativeGate` **in the background** and therefore completes only after the pump's wedged P/Invoke returns — which is precisely the spec's "dispose runs once the pump completes" behavior, enforced by the gate rather than by scheduling. The post-pump re-dispose covers the pump-assigned-the-session-late race with a real happens-before edge. Nothing caller-facing ever awaits any of it.

- [ ] **Step 1: Write the failing test (spec test 2 — wedged native push)**

In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`, add after the `PermanentlyWedgedTranscriber` class and its test:

```csharp
    // Models the NATIVE wedge from the 11:18:34 incident (Bug B): PushAsync
    // hangs inside what is really one synchronous P/Invoke, and DisposeAsync
    // BLOCKS behind the same per-session native gate until that call returns —
    // dispose cannot interrupt a P/Invoke; it can only queue behind the lock
    // (NemotronStreamingTranscriber.Session._nativeGate).
    private sealed class NativeGateWedgedTranscriber : IStreamingTranscriber
    {
        public string ModelName => "native-gate-wedged";
        public NativeGateWedgedSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class NativeGateWedgedSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            private readonly TaskCompletionSource _disposeDone = new();

            /// <summary>Completes when DisposeAsync actually finished (i.e. the
            /// wedged native call returned and the gate was released).</summary>
            public Task DisposeCompletion => _disposeDone.Task;

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
                => await _wedge.Task; // the wedged native feed

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public async ValueTask DisposeAsync()
            {
                await _wedge.Task; // lock(_nativeGate): dispose queues behind the in-flight call
                _disposeDone.TrySetResult();
            }

            public void Unwedge() => _wedge.TrySetResult();
        }
    }

    [Fact]
    public async Task WedgedNativePush_FinishReturnsPromptly_DisposeIsDeferredBehindThePump()
    {
        var transcriber = new NativeGateWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromMilliseconds(200));
        session.OnFrame(new float[800]); // the pump wedges on this push

        // Bounded by the drain deadline + a small epsilon, NOT by the blocked
        // dispose: on the pre-fix code this call NEVER returns (FinishAsync
        // inline-awaited a DisposeAsync that is itself stuck behind the wedged
        // native call), so the 3 s guard below trips.
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        session.PumpCompletion.IsCompleted.ShouldBeFalse();           // pump orphaned inside the native call
        transcriber.Session.DisposeCompletion.IsCompleted.ShouldBeFalse(); // dispose queued behind that call

        transcriber.Session.Unwedge(); // the native call finally returns
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // ...and only now can the (background) dispose complete.
        await transcriber.Session.DisposeCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Asr.Tests.StreamingDictationSessionTests
```

Expected: `WedgedNativePush_FinishReturnsPromptly_DisposeIsDeferredBehindThePump` FAILS with a `TimeoutException` from the 3 s `WaitAsync` guard (FinishAsync is blocked inside the session dispose). All other tests PASS.

- [ ] **Step 3: Implement the coordinator changes**

In `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`:

**3a.** Replace the last sentence of the class `<summary>` (lines 15–18, from "The pump drain is bounded..." through "...the caller's batch path takes over).") with:

```csharp
/// The pump drain is bounded by a drain deadline (default 10 s): a wedged push
/// HANGS rather than throws (half-dead socket send, or a stuck synchronous
/// native P/Invoke), so on timeout FinishAsync abandons the session and
/// returns null promptly — the caller's batch path takes over. The abandoned
/// session's dispose runs in the BACKGROUND: disposing a socket session aborts
/// the socket (which unwedges its pump), but disposing a native session cannot
/// interrupt an in-flight P/Invoke — it only prevents further use and frees
/// native state once the call returns — so no caller-facing path ever awaits it.
```

**3b.** Replace the whole `TimeoutException` handler in `FinishAsync` (lines 100–115) with:

```csharp
        catch (TimeoutException)
        {
            // A wedged push HANGS rather than throws (half-dead socket send,
            // or a stuck synchronous native P/Invoke), so no exception-based
            // fallback inside the session/wrapper can fire. Bound the whole
            // post-stop wait HERE: abandon the session and return null so the
            // caller's late path transcribes fullAudio (bounded, batch). The
            // session dispose runs in the BACKGROUND: awaiting it inline can
            // block for as long as the wedged native call takes (observed
            // ~16 s in the wild — dispose cannot interrupt a P/Invoke, it just
            // queues behind the session's native gate). Callers that see
            // DrainTimedOut coordinate shared-native disposal via
            // PumpCompletion, exactly as before.
            DrainTimedOut = true;
            _log.LogWarning(
                "streaming drain exceeded {DrainDeadline}; abandoning streaming session, batch path takes over",
                deadline);
            _ = ScheduleAbandonedSessionDispose();
            return null;
        }
```

and change the two lines at the top of `FinishAsync` (lines 95–98) from:

```csharp
        _frames.Writer.TryComplete();
        try
        {
            await _pump.WaitAsync(_drainDeadline, ct); // TimeoutException on a wedged drain
        }
```

to:

```csharp
        _frames.Writer.TryComplete();
        var deadline = _drainDeadline;
        try
        {
            await _pump.WaitAsync(deadline, ct); // TimeoutException on a wedged drain
        }
```

(The `deadline` local looks redundant here; Task 4 gives it a second value.)

**3c.** Add the new private method after `FinishAsync`:

```csharp
    /// <summary>Dispose the abandoned session OFF every caller-facing await
    /// path. The immediate attempt aborts a socket-style session — which is
    /// what unwedges a pump stuck in a socket send. A native session's dispose
    /// cannot interrupt an in-flight P/Invoke: it only queues behind the
    /// session's native gate until the call returns, which is exactly why no
    /// caller may await it. After the pump exits, dispose again: that is the
    /// only point with a happens-before edge on the pump's late `_session`
    /// assignment (the pump may still have been inside StartSessionAsync when
    /// we abandoned).</summary>
    private Task ScheduleAbandonedSessionDispose()
        => Task.Run(async () =>
        {
            await DisposeSessionAsync().ConfigureAwait(false);
            try { await _pump.ConfigureAwait(false); } catch { /* pump error already logged */ }
            await DisposeSessionAsync().ConfigureAwait(false);
        });
```

**3d.** Replace `DisposeAsync` (lines 126–139, including its XML doc — this also retires the misleading "aborting the session is what unblocks it" claim) with:

```csharp
    /// <summary>Abandon the dictation (silence-drop / cancel / drain timeout):
    /// stop the pump and dispose the session without transcribing. Never
    /// throws, and never blocks unboundedly: the session dispose runs in the
    /// background (for a socket session dispose aborts the socket and unwedges
    /// its pump; for a native session dispose cannot interrupt the in-flight
    /// P/Invoke and would otherwise block here behind the native gate), and
    /// the pump wait is bounded. Callers coordinate shared-native disposal via
    /// <see cref="PumpCompletion"/>.</summary>
    public async ValueTask DisposeAsync()
    {
        _frames.Writer.TryComplete();
        var abandonDispose = ScheduleAbandonedSessionDispose();
        // FinishAsync already proved the pump is wedged past the drain
        // deadline — waiting on it again here only delays the caller's late
        // batch path. Everything defers to the background dispose chain.
        if (DrainTimedOut) return;
        // Bounded: never let a pathologically hung pump (hanging factory, or
        // a wedged native call) block the serial hotkey loop; orphaning the
        // pump task is the lesser evil.
        try { await _pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* abandoned */ }
        // Let the common (healthy) case observe a disposed session
        // synchronously; a chain blocked behind a wedged native call finishes
        // in the background.
        try { await abandonDispose.WaitAsync(TimeSpan.FromSeconds(1)); } catch { /* finishes in background */ }
    }
```

**3e.** Replace `DisposeSessionAsync` (lines 141–147) with an `Interlocked`-based version (closes the read-null-swap race between the two abandon paths and the background chain):

```csharp
    private async ValueTask DisposeSessionAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        try { await session.DisposeAsync(); } catch { /* abandoned */ }
    }
```

**3f.** In the `DrainTimedOut` XML doc (lines 74–79) the contract text stays as-is (it is still accurate). Do **not** re-add the removed inline comment `// late path must NOT ensure (orphaned-pump risk)` — it contradicted `PipelineHost.cs:586-593`, which deliberately DOES run `TryEnsureAsrModel` on the late path because the orphaned pump was registered with the orphan guard first.

- [ ] **Step 4: Amend the two existing drain tests' synchronization**

The fix intentionally moves the abandoned session's dispose off the FinishAsync path, so `Disposed` is no longer guaranteed to be observable the instant `FinishAsync` returns. In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`:

**4a.** In `WedgedPush_DrainDeadlineExpires_ReturnsNullAndDisposesTheSession`, replace the assertion block:

```csharp
        result.ShouldBeNull(); // caller's late batch path takes over (bounded)
        transcriber.Session.Disposed.ShouldBeTrue();
        session.DrainTimedOut.ShouldBeTrue(); // keys the null-return contract
```

with:

```csharp
        result.ShouldBeNull(); // caller's late batch path takes over (bounded)
        session.DrainTimedOut.ShouldBeTrue(); // keys the null-return contract
        // The dispose now runs in the BACKGROUND (it must never block
        // FinishAsync); for this socket-style fake it aborts the wedged push,
        // which is what lets the pump exit — so pump completion implies the
        // dispose ran.
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        transcriber.Session.Disposed.ShouldBeTrue();
```

**4b.** In `PumpCompletion_RemainsIncomplete_AfterDrainTimeoutAbandon`, replace the stale comment above the `FinishAsync` call:

```csharp
        // Takes ~5 s: the internal DisposeAsync also bounds its pump wait before
        // abandoning (nothing can unwedge this fake until the test does).
```

with:

```csharp
        // Returns at the drain deadline: the abandoned session's dispose is
        // scheduled in the background instead of being awaited inline
        // (nothing can unwedge this fake until the test does).
```

The test's assertions are unchanged and must still pass.

- [ ] **Step 5: Run the test class to verify green**

Same commands as Step 2. Expected: ALL tests in `StreamingDictationSessionTests` PASS, and the class total runtime drops (the permanently-wedged test no longer eats the 5 s dispose bound).

- [ ] **Step 6: Audit PipelineHost's DrainTimedOut/PumpCompletion usage (read-only)**

Confirm the deferred dispose preserves the safety contract — no code changes expected. Verify in `src/Winpepper.App/Hosting/PipelineHost.cs`:

1. `NoteStreamingReleased` (`:1192-1197`) registers `PumpCompletion` with `_orphanGuard` when incomplete — it never *awaits* the pump, so a still-running background dispose chain changes nothing for it.
2. Every streaming release site follows `null field → await streaming.DisposeAsync() → NoteStreamingReleased(streaming)` (stop-arm finally `:567-571`/`:952-956`, silence-drop `:523-529`/`:908-914`, cancel `:808-814`, teardown `:1270-1276`) — ordering is preserved because our `DisposeAsync` still returns only after scheduling the chain, and `PumpCompletion` semantics are untouched (the pump completes exactly when it did before: the immediate background dispose preserves the socket-abort unwedge timing).
3. `DrainTimedOut` has **no** production reader — PipelineHost branches purely on `maybeTranscription is null` (`:574`/`:959`) — so the late path is reached exactly as before, just *sooner* (that is the fix). The late path's `TryEnsureAsrModel` (`:593`/`:978`) remains safe because `NoteStreamingReleased` at `:571`/`:956` registered the orphaned pump BEFORE it runs, and `TryEnsureAsrModel`'s Swap branch routes the old engine's dispose through `_orphanGuard.RunOrDefer` (`:243`).

If any of these three facts does not hold as described, STOP and re-assess before proceeding (do not silently adapt).

- [ ] **Step 7: Run the full Asr test project**

```bash
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0, Failed: 0`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs \
        tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
git commit -m "fix(asr): never block FinishAsync/DisposeAsync behind a wedged session dispose"
```

---

### Task 4: Fix B2 — short drain deadline when zero pushes completed by stop time

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs` (pump loop from Task 1; `FinishAsync` head from Task 3; two new members)
- Test: `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`

**Interfaces:**
- Consumes: Task 3's `FinishAsync` shape (`var deadline = _drainDeadline;` + `ScheduleAbandonedSessionDispose()`); Task 3's `NativeGateWedgedTranscriber` fake (reused by the first test below).
- Produces: `private volatile bool _anyPushCompleted;` set by the pump after each successful `PushAsync`; `private static readonly TimeSpan ZeroPushDrainDeadline = TimeSpan.FromSeconds(1.5);`. Behavior: effective drain deadline = `_anyPushCompleted ? _drainDeadline : min(_drainDeadline, ZeroPushDrainDeadline)`. The `min` keeps every existing 200 ms-deadline test exactly as it was.

- [ ] **Step 1: Write the two failing tests**

In `tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs`, add `using System.Diagnostics;` to the file's using directives, then add after the Task 3 test:

```csharp
    [Fact]
    public async Task ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline()
    {
        var transcriber = new NativeGateWedgedTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(30)); // the FULL deadline: must NOT be waited out
        session.OnFrame(new float[800]); // the pump wedges on the FIRST push — zero pushes ever complete

        // Zero completed pushes at stop time means there is no streamed-latency
        // win to preserve — the short deadline applies, not the 30 s one. The
        // 10 s guard sits between the two so the wrong branch fails loudly.
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();

        transcriber.Session.Unwedge(); // let the orphaned pump exit cleanly
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    // First push completes (streaming genuinely underway), the SECOND wedges —
    // the zero-push shortcut must NOT apply and the full deadline must hold.
    private sealed class WedgesOnSecondPushTranscriber : IStreamingTranscriber
    {
        public string ModelName => "wedges-on-second-push";
        public WedgingSession Session { get; } = new();
        public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
            => Task.FromResult<IStreamingTranscriptionSession>(Session);

        public sealed class WedgingSession : IStreamingTranscriptionSession
        {
            private readonly TaskCompletionSource _wedge = new();
            private readonly TaskCompletionSource _secondPushStarted = new();
            private int _pushes;

            /// <summary>The second push starting proves the first COMPLETED —
            /// and therefore that the coordinator observed a completed push.</summary>
            public Task SecondPushStarted => _secondPushStarted.Task;

            public async ValueTask PushAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _pushes) == 1) return; // first push succeeds
                _secondPushStarted.TrySetResult();
                await _wedge.Task;
            }

            public Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken ct)
                => throw new InvalidOperationException("FinishAsync must not run on a wedged session");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            public void Unwedge() => _wedge.TrySetResult();
        }
    }

    [Fact]
    public async Task StreamingUnderway_AtFinish_KeepsTheFullDrainDeadline()
    {
        var transcriber = new WedgesOnSecondPushTranscriber();
        var session = StreamingDictationSession.Start(
            _ => Task.FromResult<IStreamingTranscriber?>(transcriber),
            NullLogger.Instance, TestContext.Current.CancellationToken,
            drainDeadline: TimeSpan.FromSeconds(3)); // full deadline, above the 1.5 s short one
        session.OnFrame(new float[800]); // completes
        session.OnFrame(new float[800]); // wedges
        await transcriber.Session.SecondPushStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var finishSw = Stopwatch.StartNew();
        var result = await session
            .FinishAsync(new float[800], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        finishSw.Stop();

        result.ShouldBeNull();
        session.DrainTimedOut.ShouldBeTrue();
        // The FULL 3 s deadline applied — not the 1.5 s zero-push shortcut
        // (0.5 s margin absorbs timer slop).
        finishSw.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5));

        transcriber.Session.Unwedge();
        await session.PumpCompletion.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
```

- [ ] **Step 2: Run the tests to verify the first fails (and the second passes)**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Asr.Tests.StreamingDictationSessionTests
```

Expected: `ZeroCompletedPushes_AtFinish_UsesTheShortDrainDeadline` FAILS with a `TimeoutException` from the 10 s guard (the code waits the full 30 s). `StreamingUnderway_AtFinish_KeepsTheFullDrainDeadline` PASSES already (it pins the behavior the implementation must not break). All other tests PASS.

- [ ] **Step 3: Implement the short deadline**

In `src/Winpepper.Asr/Transcription/StreamingDictationSession.cs`:

**3a.** Add two members after the `_pumpError` field:

```csharp
    /// <summary>Drain bound applied when ZERO pushes completed by stop time.
    /// In that state streaming has no latency win to preserve: the engine
    /// would have to process ALL queued audio during the drain anyway, and the
    /// caller's late batch path on fullAudio produces an equivalent transcript
    /// (the session itself logs "Streamed latency win lost" when it falls
    /// back) — so waiting the full deadline buys the user nothing. Kept above
    /// 1 s so a session that is merely slow to start still gets a fair chance
    /// to drain.</summary>
    private static readonly TimeSpan ZeroPushDrainDeadline = TimeSpan.FromSeconds(1.5);

    private volatile bool _anyPushCompleted; // written by the pump, read by FinishAsync
```

**3b.** In the pump (Task 1's shape), replace:

```csharp
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                    await session.PushAsync(frame, ct);
```

with:

```csharp
                await foreach (var frame in _frames.Reader.ReadAllAsync(CancellationToken.None))
                {
                    await session.PushAsync(frame, ct);
                    _anyPushCompleted = true; // keys FinishAsync's drain-deadline choice
                }
```

**3c.** In `FinishAsync`, replace Task 3's line:

```csharp
        var deadline = _drainDeadline;
```

with:

```csharp
        var deadline = _anyPushCompleted
            ? _drainDeadline
            : TimeSpan.FromTicks(Math.Min(_drainDeadline.Ticks, ZeroPushDrainDeadline.Ticks));
```

(The `min` preserves explicitly-injected short deadlines — the existing 200 ms tests — and only ever *shortens* the wait.)

- [ ] **Step 4: Run the test class to verify green**

Same commands as Step 2. Expected: ALL tests in `StreamingDictationSessionTests` PASS (the class now includes two multi-second tests: ~1.5 s and ~3 s).

- [ ] **Step 5: Run the full Asr test project**

```bash
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0, Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/StreamingDictationSession.cs \
        tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
git commit -m "fix(asr): use a short drain deadline when zero pushes completed by stop"
```

---

### Task 5: Fix B4 — WRN with duration when a nemotron native streaming call runs long

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`
- Modify: `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs` (add `FeedDelay` knob)
- Test: `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`

**Interfaces:**
- Consumes: `CapturingLogger` from Task 1 (`tests/Winpepper.Asr.Tests/CapturingLogger.cs`); `ITranscribeCppStream { string? Feed(float[] samples, int count); (string Text, bool WasTruncated) Finalize(); }`.
- Produces: new optional constructor parameter on `NemotronStreamingTranscriber`: `TimeSpan? nativeCallWarnAfter = null` (default 3 s) — appended LAST so `AppShell.cs` and the bench need no changes. WRN template: `"nemotron native {Op} took {ElapsedMs} ms; a call this slow stalls the streaming pump until it returns"` with `Op` ∈ `"stream begin"`, `"stream feed"`, `"stream finalize"`.

**Context:** WHY the incident's native call took ~15 s is environmental and not determinable from managed code; per systematic-debugging the correct move is bounded handling (Tasks 3–4) plus observability so future wedges are diagnosable from the log alone. The instrumentation lives in `NemotronStreamingTranscriber.Session` — the layer that already brackets every native call and already has `_log?` (`TranscribeCppEngine`/`NativeStream` have no logger seam at all). Cost: one `Stopwatch` + one closure per 160 ms audio chunk — negligible next to the inference the call performs.

- [ ] **Step 1: Add the `FeedDelay` knob to the shared fake engine**

In `tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs`, add a public field next to `ThrowOnFeed`:

```csharp
    public TimeSpan FeedDelay; // simulates a slow native transcribe_stream_feed
```

and at the top of `FakeStream.Feed(float[] samples, int count)` (before the `ThrowOnFeed` check), add:

```csharp
            if (_e.FeedDelay > TimeSpan.Zero) Thread.Sleep(_e.FeedDelay);
```

(`Feed` is synchronous by contract — a real native call blocks the thread the same way.)

- [ ] **Step 2: Write the failing tests**

In `tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs`, add (this file uses bare xUnit `Assert` and has a `Samples(int)` helper and a `Make(engine, batch)` helper; keep its style):

```csharp
    [Fact]
    public async Task Native_call_slower_than_threshold_logs_a_duration_warning()
    {
        var engine = new FakeTranscribeCppEngine { FeedDelay = TimeSpan.FromMilliseconds(50) };
        var log = new CapturingLogger();
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en",
            log, nativeCallWarnAfter: TimeSpan.FromMilliseconds(1));
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken); // exactly one native feed

        Assert.Contains(log.Warnings,
            w => w.Contains("nemotron native stream feed took") && w.Contains("ms"));
    }

    [Fact]
    public async Task Fast_native_calls_log_no_duration_warning()
    {
        var engine = new FakeTranscribeCppEngine();
        var log = new CapturingLogger();
        var t = new NemotronStreamingTranscriber(
            () => engine, FakeTranscriber.Returning("batch", "batch text"), "nemotron-streaming-en", log);
        await using var s = await t.StartSessionAsync(TestContext.Current.CancellationToken);

        await s.PushAsync(Samples(2560), TestContext.Current.CancellationToken);
        var result = await s.FinishAsync(Samples(2560), TestContext.Current.CancellationToken);

        Assert.Equal("hello world final", result.Text);
        Assert.DoesNotContain(log.Warnings, w => w.Contains("nemotron native"));
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: the build FAILS — `NemotronStreamingTranscriber` has no `nativeCallWarnAfter` parameter yet (CS1739). That is this task's red state.

- [ ] **Step 4: Implement the instrumentation**

In `src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs`:

**4a.** Add `using System.Diagnostics;` to the using directives.

**4b.** Extend the outer class: add a field after `_language` (line 25):

```csharp
    private readonly TimeSpan _nativeCallWarnAfter;
```

Append an optional parameter to the constructor (after `string? language = null`):

```csharp
        TimeSpan? nativeCallWarnAfter = null)
```

and in the constructor body:

```csharp
        // 3 s: an order of magnitude above a healthy call (feeds are ~tens of
        // ms, finalize ~100-300 ms) yet well below the drain deadline, so a
        // wedge is visible in the log before it becomes a user-facing stall.
        _nativeCallWarnAfter = nativeCallWarnAfter ?? TimeSpan.FromSeconds(3);
```

Pass it through `StartSessionAsync` into the `Session` (line 45–47):

```csharp
    public Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        => Task.FromResult<IStreamingTranscriptionSession>(
            new Session(_engineProvider, _batchFallback, ModelName, _attContextRight, _language, _log,
                _nativeCallWarnAfter));
```

**4c.** Extend `Session`: add a field, extend the constructor accordingly:

```csharp
        private readonly TimeSpan _nativeCallWarnAfter;

        public Session(Func<ITranscribeCppEngine> engineProvider, ITranscriber batchFallback,
            string modelName, int attContextRight, string? language, ILogger? log,
            TimeSpan nativeCallWarnAfter)
        {
            _engineProvider = engineProvider;
            _batchFallback = batchFallback;
            _modelName = modelName;
            _attContextRight = attContextRight;
            _language = language;
            _log = log;
            _nativeCallWarnAfter = nativeCallWarnAfter;
        }
```

**4d.** Add the timing helper to `Session` (next to `EnsureStream`):

```csharp
        /// <summary>Native streaming calls are synchronous P/Invokes that
        /// cannot be cancelled or interrupted; when one wedges, the streaming
        /// pump stalls until it returns and the coordinator's drain deadline
        /// fires (observed: a call stuck >=15 s in the wild). Log the duration
        /// when a call completes after taking abnormally long, so future
        /// wedges are diagnosable from the log alone.</summary>
        private T TimedNativeCall<T>(string op, Func<T> call)
        {
            var nativeSw = Stopwatch.StartNew();
            try { return call(); }
            finally
            {
                nativeSw.Stop();
                if (nativeSw.Elapsed >= _nativeCallWarnAfter)
                    _log?.LogWarning(
                        "nemotron native {Op} took {ElapsedMs} ms; a call this slow stalls the streaming pump until it returns",
                        op, (int)nativeSw.ElapsedMilliseconds);
            }
        }
```

**4e.** Wrap the four native call sites (all already inside `lock (_nativeGate)`):

- `EnsureStream` (line 185–188):

```csharp
        private void EnsureStream()
        {
            _stream ??= TimedNativeCall("stream begin",
                () => _engineProvider().BeginStream(_attContextRight, _language));
        }
```

- `PushAsync` feed (line 101): replace `_stream!.Feed(_buffer, FeedChunkSamples);` with:

```csharp
                            TimedNativeCall("stream feed", () => _stream!.Feed(_buffer, FeedChunkSamples));
```

- `FinishAsync` tail flush (line 144): replace `_stream!.Feed(_buffer, _buffered);   // flush the tail` with:

```csharp
                            TimedNativeCall("stream feed", () => _stream!.Feed(_buffer, _buffered)); // flush the tail
```

- `FinishAsync` finalize (line 148): replace `var (text, truncated) = _stream!.Finalize();` with:

```csharp
                        var (text, truncated) = TimedNativeCall("stream finalize", () => _stream!.Finalize());
```

- [ ] **Step 5: Run the test class to verify green**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll \
  -notrait "Platform=Windows" -class Winpepper.Asr.Tests.Transcription.NemotronStreamingTranscriberTests
```

(If the `-class` filter reports 0 tests, the file's namespace differs — run the whole DLL instead and check the two new test names.) Expected: both new tests PASS, all existing Nemotron tests PASS.

- [ ] **Step 6: Run the full Asr test project**

```bash
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```

Expected: `Errors: 0, Failed: 0`.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs \
        tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs \
        tests/Winpepper.Asr.Tests/Transcription/FakeTranscribeCppEngine.cs
git commit -m "feat(asr): warn with duration when a nemotron native streaming call runs long"
```

---

### Task 6: Full-suite verification

**Files:**
- No source changes. Runs the whole Linux suite; records the pre-push gate requirement.

**Interfaces:**
- Consumes: everything above.
- Produces: a green `LINUX SUITE: GREEN` state on the branch; the branch is ready for the Windows gate.

- [ ] **Step 1: Run the full Linux test suite (pre-commit bar for the branch as a whole)**

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-drain-hardening
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (all 9 projects, `Errors: 0, Failed: 0` each). Allow ~5–10 minutes.

- [ ] **Step 2: Fix anything red, re-run until green**

If any project is red, fix within the scope of this plan's changes (most likely candidates: a timing-sensitive assertion in the amended drain tests, or a nullable warning-as-error). Re-run `./scripts/linux-tests.sh` until green. Commit any fix as `fix(asr): <what>`.

- [ ] **Step 3: Note the pre-push gate (do not push from this task)**

Before this branch is ever pushed, run the Windows gate from WSL with a generous timeout:

```bash
./scripts/windows-gate.sh   # ~12 min; use a 20-30 min timeout; require exit 0 + "GATE: GREEN"
```

Do not run it concurrently with `linux-tests.sh`. This plan's execute stage stops at a green Linux suite; the gate is the mandatory pre-push step for whoever integrates the branch.

---

## Self-Review (performed while writing this plan)

**1. Spec coverage:**
- Fix A local-capture → Task 1 (pump code + failing test 1: dispose/abandon with queued frames → pump completes, no pumpError warning, no NRE).
- Fix A "verify both implementations tolerate PushAsync-after-Dispose; add a guard only if one doesn't" → Task 1 Step 5 (nemotron verified safe, evidence cited) + Task 2 (Fallback session lacks the guard → guard added with test).
- Fix B1 (no inline dispose await on drain timeout; DrainTimedOut; prompt null; background dispose keyed to pump exit; PumpCompletion contract preserved; PipelineHost audit; DisposeAsync abandon path made consistent) → Task 3 (impl + failing test 2 + read-only audit with explicit stop-if-false facts).
- Fix B2 (zero-push short deadline, 1–2 s constant with rationale) → Task 4 (1.5 s constant + failing test 3 + a pin test that the full deadline still applies when streaming was underway).
- Fix B3 (correct the cloud-only "disposing aborts the socket" comments) → Task 3 Steps 3a/3b/3d (class summary, timeout handler, DisposeAsync doc) and 3f (retires the stale "late path must NOT ensure" comment).
- Fix B4 (WRN + duration for slow native begin/feed/finish, cheap Stopwatch) → Task 5.
- Spec tests 1/2/3 → Tasks 1/3/4 respectively; "keep existing tests green" → every task runs the full Asr project; two existing tests amended in Task 3 with intent preserved; Task 6 runs the whole Linux suite.
- Repo conventions (xUnit v3 runner, no `dotnet test`, Windows gate before push, no mixed bin/obj, commit style) → Global Constraints + per-task commands + Task 6.

**1b. No silent deferrals:** All fixes are production-code changes proven by tests against the real `StreamingDictationSession` / `FallbackStreamingTranscriber` / `NemotronStreamingTranscriber`. Test doubles stand in only for the *counterparty* (transcriber sessions / native engine), which is exactly the seam the production code already defines (`IStreamingTranscriber` factory injection, `Func<ITranscribeCppEngine>`); no task leaves a stub where production behavior was required. The one intentionally untestable item — WHY the native call wedged — is explicitly out of scope per the spec (environmental; addressed via bounded handling + observability).

**2. Placeholder scan:** No TBDs; every code step shows the code; every run step shows the command and expected outcome.

**3. Type consistency:** `ScheduleAbandonedSessionDispose()` (Task 3) is referenced by name in Tasks 3/4 only; `deadline` local introduced in Task 3 Step 3b is the exact line Task 4 Step 3c replaces; `_anyPushCompleted`/`ZeroPushDrainDeadline` appear only in Task 4; `CapturingLogger.Warnings` is `IReadOnlyList<string>` and both consumers (Tasks 1, 5) only enumerate/assert-contains; `nativeCallWarnAfter` is appended as the LAST optional parameter so `Make()`/`AppShell`/bench call sites compile unchanged; fakes' member names (`FirstPushStarted`, `SecondPushStarted`, `DisposeCompletion`, `Unwedge`, `PushCount`) are used consistently within their single task each.
