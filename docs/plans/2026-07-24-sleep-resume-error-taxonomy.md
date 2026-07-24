# Sleep/Resume Recovery + Error Taxonomy Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Stop the status pill from squatting on screen after an idle-time
error, make the microphone and speech-model failures behave as *conditions*
that clear only on recovery success, recover the warm mic automatically when a
capture endpoint returns, and reinstall the keyboard hook after the machine
resumes from sleep.

**Architecture:** A pure classifier in `Winpepper.Core.Errors` splits every
`ErrorBus` report into an **EVENT** (a fact about a past moment) or a
**CONDITION** (an ongoing state). `SessionViewModel` becomes the single owner of
error presentation: EVENT errors only take the pill while a dictation is in
flight and self-clear after ~6 s; CONDITION errors grab the pill for ~10 s, then
retire it to the tray and stay on the tray until a *recovery success* clears
them — never a timer. `Winpepper.Audio` gains a pure, unit-tested
`CaptureRecoveryPolicy` (debounce + failing/recovered state) driven by a thin
Windows-only `IMMNotificationClient` shell, so a returning capture endpoint
rebuilds the warm stream and emits the recovery signal. `HotkeyHook` registers
for suspend/resume notifications via `powrprof.dll` and reinstalls
`WH_KEYBOARD_LL` on its own hook thread after resume. `PipelineHost` logs one
content-free line at each dictation start.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly (pure-managed tests run on
Linux via the in-process runner), NAudio / NAudio.Wasapi 2.2.1, WinUI 3
(Windows-only host).

---

## Incident Context (root cause is CONFIRMED — do not re-litigate)

2026-07-24 05:48 local, installed build 0.6.2.222. During a system sleep/resume:

1. The warm WASAPI stream faulted `0x88890004` (device invalidated).
2. `WarmCaptureCoordinator.Rebuild()` immediately retried and failed
   `0x80070490` "Element not found" at
   `MMDeviceEnumerator.GetDefaultAudioEndpoint` (`WasapiCaptureSource.cs:58`) —
   mid-resume there is no default capture endpoint yet.
3. The fault was logged and reported to the `ErrorBus` correctly (no toast —
   `ErrorToastPolicy` keeps Audio silent).

Two defects then compounded:

- **(a) Stuck pill.** `SessionViewModel.OnBusReport`
  (`src/Winpepper.Core/ViewModels/SessionViewModel.cs:132-143`) mirrors *every*
  bus report into `Stage = SessionStage.Error` + `StatusText`, even while IDLE.
  `StatusPillWindow`'s Error arm
  (`src/Winpepper.App/Views/StatusPillWindow.xaml.cs:156-167`) calls
  `_hideTimer.Stop()`. So the pill sat on screen for 3.5 hours reading
  `Error (Audio): Element not found. (...`.
- **(b) Dead hotkeys.** Later hotkey presses produced **zero** log lines:
  Windows silently removed the low-level keyboard hook across suspend/resume
  (documented behavior when the hook thread stalls) and `HotkeyHook` has no
  reinstall mechanism. Nothing retried the mic rebuild either, because the only
  remaining retry seam is `WarmWasapiRecorder.StartSession`'s
  `EnsureStarted(force: true)` — which needs a hotkey that never arrived.

Restarting the app fixed everything, confirming both mechanisms.

## Owner-Agreed Design — The Error Taxonomy

Errors are one of two kinds:

- **EVENT** — a fact about a past moment: injection failed, cleanup fell back,
  this dictation captured no audio. It has **no ongoing validity**. Show it
  briefly, only while the user is actually in a dictation.
- **CONDITION** — an ongoing state: the microphone is unavailable, there is no
  usable speech model. It is **true over time**. It must stay surfaced *exactly
  as long as it is true*, be cleared by **recovery success**, and **never** by a
  timer.

Polling for validity was explicitly considered and **REJECTED**: every condition
we surface has a push notification available, and "retry the recovery and let
success clear it" is strictly more honest than a validity probe — a probe can
pass while capture still fails, but a *successful rebuild cannot lie*.

Long-lived conditions belong on the **persistent** surface (the tray icon), not
an always-on-top overlay squatting for hours. Hence: attention-grab on the pill
for ~10 s, then retire the pill and keep the tray in its error state.

## Load-Bearing Taxonomy Decision (read before Task 1)

**Governing rule: a stage is classified CONDITION only if this change wires a
recovery signal that can clear it.** A condition with no clearing signal is
exactly the defect being fixed (a permanent error surface), so classifying one
would reintroduce the bug on the tray instead of the pill.

Applying the rule against the actual report sites in the tree:

| Report site | Stage | Kind | Why |
|---|---|---|---|
| `PipelineHost.cs:279` capture-fault handler | Audio | **CONDITION** | The stream is down until something rebuilds it. Cleared by rebuild success (Task 6/7). |
| `PipelineHost.cs:906` `WarnIfSessionSilent` | Audio | EVENT | A fact about the dictation that just ended ("No audio detected"). No ongoing validity. |
| `PipelineHost.cs:216` `CannotStart` ("Speech model not installed") | Asr | **CONDITION** | No usable speech model is an ongoing state. Cleared by a successful model load (Task 7). |
| `PipelineHost.cs:243` model load failure | Asr | **CONDITION** | Same state, same clearing signal. |
| `PipelineHost.cs:455/698` "Speech model unavailable; dictation aborted" | Asr | **CONDITION** | Same state, same clearing signal. |
| `AppShell.cs:380/391` startup gate | Asr | **CONDITION** | Same state, same clearing signal. |
| `ModelsPage.xaml.cs:75/102/295` | Models | EVENT | Each is the failure of one *user-initiated attempt* (verify readiness / download). The ongoing "no usable speech model" state is separately and continuously reported at the **Asr** stage, which is the condition we surface and clear. |
| `PipelineHost.cs:522/765` | Cleanup | EVENT | Quality degradation that already fell back. |
| `PipelineHost.cs:358/555/796` | Injection | EVENT | The paste attempt failed; a pending-paste slot already handles it. |
| `PipelineHost.cs:586/827` | Learning | EVENT | Background watcher hiccup. |
| `PipelineHost.cs:313` | Unknown | EVENT | Per-dictation pipeline failure. |
| (none in tree today) | OcrUia, History | EVENT | Per-attempt degradations, consistent with the spec. |
| `HotkeyRecorderBox.xaml.cs:168` | Hotkey | EVENT | The recording attempt failed; the user retries. |
| `CrashHandler.cs:39` | Crash | EVENT | A crash that already happened. |
| (none in tree today) | Settings | EVENT | No recovery signal exists to clear it. |

This satisfies the spec's taxonomy ("microphone unavailable, ASR/cleanup model
missing are Conditions") — the missing-speech-model condition is carried by the
**Asr** stage, which is where it is continuously reported and where a real
recovery signal exists. The **Models** stage carries only per-attempt failures.

**Audio is the one stage where the stage alone is not enough** (a capture fault
and "no audio in that dictation" are both `ErrorStage.Audio`). The classifier
therefore keys on `ErrorStage` **plus** `ErrorRecord.ExceptionType`, and the
capture-fault site wraps its exception in a new
`Winpepper.Core.Errors.MicrophoneUnavailableException` (message preserved
verbatim, so Diagnostics text is unchanged).

---

## Global Constraints

- **Test runner (AGENTS.md mandate):** Do **NOT** use `dotnet test` (the VSTest
  host is unreliable here). Build `-c Release`, then run the built test dll with
  the xUnit v3 in-process runner: `dotnet exec <built test dll>`.
- **dotnet path:** the worktree has no local `.dotnet/`. Use the repo-root SDK
  at `/home/dan/code/winpepper/.dotnet/dotnet` for every build/exec.
- **Worktree:** all work happens in
  `/home/dan/code/winpepper/.worktrees/sleep-resume-error-taxonomy` on branch
  `feature/sleep-resume-error-taxonomy` (base `4a03618`).
- **All tests green before EVERY commit** (AGENTS.md). Each task's own test dll
  must be green AND the full non-Windows suite must not regress. Baseline at
  `4a03618`: 9 test projects, ~845 passing / 0 failing on Linux.
- **Windows-only code cannot be compiled on Linux.** `Winpepper.App` is skipped
  on Linux (`SKIP_WINUI_LINUX` in `Directory.Build.props`), and
  `WarmWasapiRecorder` / `WasapiCaptureSource` / `AudioEndpointWatcher` are
  inside `#if WINDOWS`. Correctness for those files rests on careful editing
  plus the Windows smoke checklist (Task 9). Keep them THIN; all decision logic
  goes in pure classes that Linux tests cover.
- **Do NOT touch `packaging/`.** Do NOT touch the WinUI XAML files.
  `StatusPillWindow.xaml.cs` needs **no change** — the fix is entirely in the
  view model that drives it.
- **The keyboard hook may be touched ONLY for the CHANGE 4 lifecycle work.**
  Every existing behavior (chord tracking, injected-event handling, swallow
  rules, capture suspend/resume for chord recording) stays byte-identical.
- **Do not regress the existing audio discipline:** the coordinator's
  lock/epoch-guard/dispose-scheduler rules, the session-start default-device
  drift check (`RebuildIfDefaultChanged`), and the 5000-iteration concurrency
  hammer (`WarmCaptureCoordinatorTests.ConcurrencyHammer_RebuildVsFrames_NeverThrows`)
  must all stay green and unchanged.
- **IMMNotificationClient callbacks arrive on COM/MTA threads.** Never rebuild
  capture (which takes a lock and may dispose a source that joins a capture
  thread) on the callback thread — always hand off first.
- **Fixed timings (owner-agreed, copy verbatim):** EVENT error pill hold =
  **6000 ms**; CONDITION pill attention-grab = **10000 ms**; device-event
  rebuild debounce = **500 ms**.
- **Exact log strings (copy verbatim):**
  - `"Microphone capture recovered on device change"`
  - `"System resumed; reinstalling keyboard hook"`
  - `"Keyboard hook reinstalled"`
  - `"Session started (hold)"` / `"Session started (toggle)"` (each with the
    session GUID)
- **Logs must stay content-free** — never log transcript text or user content.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Winpepper.Core/Errors/ErrorKind.cs` | The two-value taxonomy enum. |
| `src/Winpepper.Core/Errors/MicrophoneUnavailableException.cs` | Marker exception that makes an Audio *condition* distinguishable from an Audio *event*. |
| `src/Winpepper.Core/Errors/ErrorClassifier.cs` | Pure `ErrorRecord` → `ErrorKind` classification, documented per stage. |
| `src/Winpepper.Core/ViewModels/SessionStages.cs` | Pure "is a dictation in flight?" predicate shared by the VM and the tray mapper. |
| `src/Winpepper.Core/Threading/IDelayScheduler.cs` | Test seam for the two presentation timers. |
| `src/Winpepper.Core/Threading/SystemDelayScheduler.cs` | Production `Task.Delay` implementation. |
| `src/Winpepper.Audio/CaptureRecoveryPolicy.cs` | Pure debounce + failing/recovered state machine for endpoint-driven mic recovery. |
| `src/Winpepper.Audio/AudioEndpointWatcher.cs` | Windows-only `IMMNotificationClient` shell; marshals endpoint events off the COM thread. |
| `src/Winpepper.Platform/Hotkeys/PowerResumeDecision.cs` | Pure "is this PBT_* a resume?" decision. |
| `src/Winpepper.Platform/Hotkeys/PowerNotificationNative.cs` | `powrprof.dll` P/Invokes for callback-mode suspend/resume notifications. |
| `tests/Winpepper.Core.Tests/Errors/ErrorClassifierTests.cs` | Per-stage taxonomy tests. |
| `tests/Winpepper.Core.Tests/Threading/ManualDelayScheduler.cs` | Deterministic scheduler fake. |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs` | Idle scoping, self-clear, condition lifecycle, recovery clearing. |
| `tests/Winpepper.Audio.Tests/CaptureRecoveryPolicyTests.cs` | Debounce / retry / recovered-vs-still-failed. |
| `tests/Winpepper.Platform.Tests/Hotkeys/PowerResumeDecisionTests.cs` | Resume-type decision. |
| `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookReinstallTests.cs` | Tracking-state reset on reinstall. |

**Modified:**

| File | Change |
|---|---|
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` | Taxonomy-driven `OnBusReport`, transient EVENT display, condition lifecycle, `NotifyConditionRecovered`, generation-token no-clobber guard. |
| `src/Winpepper.Core/Tray/TrayIconStateMapper.cs` | New optional `activeConditionMessage` arm (persistent surface for conditions). |
| `src/Winpepper.Audio/IWarmAudioRecorder.cs` | New `CaptureRecovered` event. |
| `src/Winpepper.Audio/WarmWasapiRecorder.cs` | Endpoint watcher + recovery policy wiring; recovery signal on rebuild/session-start success. |
| `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs` | One private thread-message constant. |
| `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` | Suspend/resume registration, hook-thread reinstall, tracking-state reset. |
| `src/Winpepper.App/Tray/TrayIconHost.cs` | Pass the active condition message to the mapper; listen for its change. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Wrap capture faults, wire `CaptureRecovered` → clear Audio condition, clear Asr condition on model load, session-start log lines. |

---

## Task 1: Error Taxonomy (pure Core)

**Files:**
- Create: `src/Winpepper.Core/Errors/ErrorKind.cs`
- Create: `src/Winpepper.Core/Errors/MicrophoneUnavailableException.cs`
- Create: `src/Winpepper.Core/Errors/ErrorClassifier.cs`
- Test: `tests/Winpepper.Core.Tests/Errors/ErrorClassifierTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Core.Errors.ErrorStage`, `ErrorRecord` (existing;
  `ErrorRecord.ExceptionType` is `ex.GetType().FullName`, set by
  `ErrorBus.Report`).
- Produces:
  - `enum ErrorKind { Event, Condition }`
  - `sealed class MicrophoneUnavailableException : Exception` with
    `MicrophoneUnavailableException(Exception inner)`
  - `static class ErrorClassifier` with
    `ErrorKind Classify(ErrorRecord record)` and
    `ErrorKind Classify(ErrorStage stage, string exceptionType)`

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Errors/ErrorClassifierTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorClassifierTests
{
    private static ErrorRecord Record(ErrorStage stage, Exception ex) => new()
    {
        Stage = stage,
        Message = ex.Message,
        ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
        StackTrace = "",
        TimestampUtc = DateTime.UtcNow,
        SessionId = Guid.Empty,
    };

    [Fact]
    public void CaptureFault_Is_A_Condition()
    {
        var rec = Record(ErrorStage.Audio,
            new MicrophoneUnavailableException(new InvalidOperationException("Element not found.")));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Condition);
    }

    [Fact]
    public void MicrophoneUnavailableException_Preserves_Inner_Message()
    {
        var inner = new InvalidOperationException("Element not found.");
        var wrapped = new MicrophoneUnavailableException(inner);

        wrapped.Message.ShouldBe("Element not found.");
        wrapped.InnerException.ShouldBeSameAs(inner);
    }

    [Fact]
    public void SilentDictation_Audio_Report_Is_An_Event()
    {
        // WarnIfSessionSilent reports a plain InvalidOperationException at the
        // Audio stage: a fact about the dictation that just ended.
        var rec = Record(ErrorStage.Audio,
            new InvalidOperationException("No audio detected - check your microphone / privacy settings."));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
    }

    [Fact]
    public void MissingSpeechModel_Is_A_Condition()
    {
        var rec = Record(ErrorStage.Asr,
            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Condition);
    }

    [Theory]
    [InlineData(ErrorStage.Injection)]
    [InlineData(ErrorStage.Cleanup)]
    [InlineData(ErrorStage.OcrUia)]
    [InlineData(ErrorStage.Learning)]
    [InlineData(ErrorStage.History)]
    [InlineData(ErrorStage.Models)]
    [InlineData(ErrorStage.Settings)]
    [InlineData(ErrorStage.Hotkey)]
    [InlineData(ErrorStage.Crash)]
    [InlineData(ErrorStage.Unknown)]
    public void Per_Attempt_Failures_Are_Events(ErrorStage stage)
    {
        var rec = Record(stage, new InvalidOperationException("boom"));

        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
    }

    [Fact]
    public void Unknown_ExceptionType_At_Audio_Defaults_To_Event()
    {
        // Fail safe: only the explicit condition marker keeps the surface.
        ErrorClassifier.Classify(ErrorStage.Audio, "Some.Other.Exception")
            .ShouldBe(ErrorKind.Event);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: BUILD FAILS with `CS0246: The type or namespace name 'ErrorKind'
could not be found` and `... 'MicrophoneUnavailableException' ...` and
`... 'ErrorClassifier' ...`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/Errors/ErrorKind.cs`:

```csharp
namespace Winpepper.Core.Errors;

/// <summary>
/// The two kinds of error the app surfaces (2026-07-24 stuck-pill incident).
///
///  * <see cref="Event"/>     - a fact about a PAST moment (injection failed,
///    cleanup fell back, this dictation captured no audio). It has no ongoing
///    validity, so it is only worth interrupting the user while a dictation is
///    in flight, and it self-clears shortly after.
///  * <see cref="Condition"/> - an ONGOING state (microphone unavailable, no
///    usable speech model). It is true over time, so it must stay surfaced
///    exactly as long as it is true and be cleared by a RECOVERY SUCCESS -
///    never by a timer.
/// </summary>
public enum ErrorKind
{
    Event,
    Condition,
}
```

Create `src/Winpepper.Core/Errors/MicrophoneUnavailableException.cs`:

```csharp
namespace Winpepper.Core.Errors;

/// <summary>
/// Marks an <see cref="ErrorStage.Audio"/> report as the ONGOING "capture is
/// down" CONDITION rather than a per-dictation Audio EVENT (the "no audio
/// detected" report from a finished session). Both arrive at the same stage, so
/// the stage alone cannot distinguish them; the capture-fault site wraps its
/// exception in this type and <see cref="ErrorClassifier"/> keys on it.
///
/// The inner exception's message is preserved verbatim so the Diagnostics page
/// and tray tooltip read exactly as before.
/// </summary>
public sealed class MicrophoneUnavailableException : Exception
{
    public MicrophoneUnavailableException(Exception inner)
        : base(inner?.Message ?? "Microphone unavailable.", inner)
    {
    }
}
```

Create `src/Winpepper.Core/Errors/ErrorClassifier.cs`:

```csharp
namespace Winpepper.Core.Errors;

/// <summary>
/// Pure EVENT-vs-CONDITION classification for every <see cref="ErrorBus"/>
/// report. See <see cref="ErrorKind"/> for the taxonomy.
///
/// GOVERNING RULE: a stage is a CONDITION only when a RECOVERY SUCCESS signal
/// exists that can clear it. A condition with no clearing signal is a permanent
/// error surface - precisely the defect this taxonomy fixes.
///
/// Per stage:
///   Audio      - CONDITION only for <see cref="MicrophoneUnavailableException"/>
///                (the warm capture stream is down until a rebuild succeeds).
///                Every other Audio report is the per-dictation "no audio
///                detected" EVENT raised after a session ends.
///   Asr        - CONDITION: "no usable speech model" is an ongoing state,
///                cleared when a model actually loads.
///   Models     - EVENT: each report is one user-initiated attempt (verify
///                readiness / download) that failed. The ongoing missing-model
///                state is reported continuously at the Asr stage.
///   Cleanup    - EVENT: quality degradation that already fell back.
///   Injection  - EVENT: that paste attempt failed (pending-paste covers it).
///   OcrUia     - EVENT: that context extraction degraded.
///   Learning   - EVENT: a background watcher hiccup.
///   History    - EVENT: that archive write hiccupped.
///   Settings   - EVENT: no recovery signal exists to clear it.
///   Hotkey     - EVENT: that chord-recording attempt failed.
///   Crash      - EVENT: a crash that already happened.
///   Unknown    - EVENT: not actionable by definition.
/// </summary>
public static class ErrorClassifier
{
    private static readonly string MicrophoneUnavailableTypeName =
        typeof(MicrophoneUnavailableException).FullName!;

    public static ErrorKind Classify(ErrorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Classify(record.Stage, record.ExceptionType);
    }

    public static ErrorKind Classify(ErrorStage stage, string exceptionType) => stage switch
    {
        ErrorStage.Audio when exceptionType == MicrophoneUnavailableTypeName => ErrorKind.Condition,
        ErrorStage.Asr => ErrorKind.Condition,
        _ => ErrorKind.Event,
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: `0 errors`, and the run reports `Failed: 0` with the new
`ErrorClassifierTests` (15 cases) included; the pre-existing Core tests still
pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Errors/ErrorKind.cs \
        src/Winpepper.Core/Errors/MicrophoneUnavailableException.cs \
        src/Winpepper.Core/Errors/ErrorClassifier.cs \
        tests/Winpepper.Core.Tests/Errors/ErrorClassifierTests.cs
git commit -m "feat(core): classify error bus reports as EVENT or CONDITION"
```

---

## Task 2: Idle Scoping + Self-Clearing EVENT Errors

**Files:**
- Create: `src/Winpepper.Core/ViewModels/SessionStages.cs`
- Create: `src/Winpepper.Core/Threading/IDelayScheduler.cs`
- Create: `src/Winpepper.Core/Threading/SystemDelayScheduler.cs`
- Create: `tests/Winpepper.Core.Tests/Threading/ManualDelayScheduler.cs`
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs:10-32` (fields +
  ctor), `:34-49` (Stage setter), `:132-143` (`OnBusReport`), `:152-156`
  (`NotifyError`)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`

**Interfaces:**
- Consumes: `ErrorClassifier.Classify(ErrorRecord)` / `ErrorKind` (Task 1).
- Produces:
  - `static class SessionStages` with `bool IsDictationInFlight(SessionStage)`
  - `interface IDelayScheduler` with `void Schedule(TimeSpan delay, Action action)`
  - `sealed class SystemDelayScheduler : IDelayScheduler`
  - `SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null)`
  - `public const int SessionViewModel.EventErrorHoldMs = 6000;`
  - `sealed class ManualDelayScheduler : IDelayScheduler` (test fake) with
    `IReadOnlyList<TimeSpan> PendingDelays`, `int PendingCount`, `void FireAll()`

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/Threading/ManualDelayScheduler.cs`:

```csharp
using System.Linq;
using Winpepper.Core.Threading;

namespace Winpepper.Core.Tests.Threading;

/// <summary>
/// Deterministic <see cref="IDelayScheduler"/> for tests: nothing runs until
/// <see cref="FireAll"/> is called, so timer-driven behavior is exercised
/// without sleeping. Actions scheduled DURING a fire are queued for the next
/// call, so a self-rescheduling bug shows up as a growing pending queue instead
/// of an infinite loop.
/// </summary>
public sealed class ManualDelayScheduler : IDelayScheduler
{
    private readonly List<(TimeSpan Delay, Action Action)> _pending = new();

    public IReadOnlyList<TimeSpan> PendingDelays => _pending.Select(p => p.Delay).ToList();
    public int PendingCount => _pending.Count;

    public void Schedule(TimeSpan delay, Action action) => _pending.Add((delay, action));

    public void FireAll()
    {
        var due = _pending.ToArray();
        _pending.Clear();
        foreach (var (_, action) in due) action();
    }
}
```

Create `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Winpepper.Core.Tests.Threading;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelErrorLifecycleTests
{
    private static (SessionViewModel Vm, SessionEngine Engine, ErrorBus Bus, ManualDelayScheduler Delays) NewVm()
    {
        var engine = new SessionEngine();
        var bus = new ErrorBus();
        var delays = new ManualDelayScheduler();
        var vm = new SessionViewModel(engine, new SynchronousUiThread(), delays);
        vm.AttachErrorBus(bus);
        return (vm, engine, bus, delays);
    }

    /// <summary>Puts the VM into a live dictation the way the pipeline does.</summary>
    private static void StartDictation(SessionEngine engine)
        => engine.Apply(SessionEvent.StartRequested);

    [Fact]
    public void EventError_While_Idle_Records_But_Does_Not_Take_The_Pill()
    {
        // THE INCIDENT: an Audio report at idle used to pin the pill to Error,
        // and the pill's Error arm stops its auto-hide timer -> stuck for hours.
        var (vm, _, bus, delays) = NewVm();

        bus.Report(ErrorStage.Injection, new InvalidOperationException("SendInput refused"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
        vm.LastErrorStage.ShouldBe(ErrorStage.Injection);
        vm.LastErrorMessage.ShouldBe("SendInput refused");
        delays.PendingCount.ShouldBe(0);
    }

    [Fact]
    public void EventError_Mid_Dictation_Shows_Then_Self_Clears_After_Six_Seconds()
    {
        var (vm, engine, bus, delays) = NewVm();
        StartDictation(engine);
        vm.Stage.ShouldBe(SessionStage.Recording);

        bus.Report(ErrorStage.Cleanup, new InvalidOperationException("cleanup fell back"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error (Cleanup): cleanup fell back");
        delays.PendingDelays.ShouldContain(TimeSpan.FromMilliseconds(SessionViewModel.EventErrorHoldMs));

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void SelfClear_Is_A_NoOp_When_A_Newer_State_Took_The_Pill()
    {
        var (vm, engine, bus, delays) = NewVm();
        StartDictation(engine);
        bus.Report(ErrorStage.Cleanup, new InvalidOperationException("older"), Guid.NewGuid());
        vm.Stage.ShouldBe(SessionStage.Error);

        // A newer error replaces it before the first timer fires.
        bus.Report(ErrorStage.Injection, new InvalidOperationException("newer"), Guid.NewGuid());
        vm.StatusText.ShouldBe("Error (Injection): newer");

        delays.FireAll(); // fires BOTH timers; only the newest may clear

        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void SelfClear_Does_Not_Clobber_A_Dictation_That_Started_Meanwhile()
    {
        var (vm, engine, bus, delays) = NewVm();
        StartDictation(engine);
        bus.Report(ErrorStage.Cleanup, new InvalidOperationException("boom"), Guid.NewGuid());
        vm.Stage.ShouldBe(SessionStage.Error);

        engine.Apply(SessionEvent.StopRequested); // Recording -> Transcribing
        vm.Stage.ShouldBe(SessionStage.Transcribing);

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Transcribing);
        vm.StatusText.ShouldBe("Transcribing...");
    }

    [Fact]
    public void NotifyError_Also_Self_Clears()
    {
        var (vm, engine, _, delays) = NewVm();
        StartDictation(engine);

        vm.NotifyError("pipeline blew up");

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error: pipeline blew up");

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void ConditionError_Still_Surfaces_While_Idle()
    {
        var (vm, _, bus, _) = NewVm();

        bus.Report(ErrorStage.Audio,
            new MicrophoneUnavailableException(new InvalidOperationException("Element not found.")),
            Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error (Audio): Element not found.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: BUILD FAILS with `CS0246: ... 'IDelayScheduler' ...` and
`CS1729: 'SessionViewModel' does not contain a constructor that takes 3
arguments` and `CS0117: 'SessionViewModel' does not contain a definition for
'EventErrorHoldMs'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Core/ViewModels/SessionStages.cs`:

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure predicates over <see cref="SessionStage"/>. Shared by the view model
/// (should an EVENT error take the pill?) and the tray mapper (should an
/// ongoing CONDITION outrank the live stage?), so both answer the question the
/// same way.
/// </summary>
public static class SessionStages
{
    /// <summary>
    /// True while the user is actually mid-dictation. Idle/Error/PendingPaste
    /// are NOT in flight: Idle and Error are resting states, and PendingPaste
    /// is a waiting-for-the-user state guarded separately.
    /// </summary>
    public static bool IsDictationInFlight(SessionStage stage) => stage is
        SessionStage.Recording or
        SessionStage.Transcribing or
        SessionStage.CleaningUp or
        SessionStage.Injecting;
}
```

Create `src/Winpepper.Core/Threading/IDelayScheduler.cs`:

```csharp
namespace Winpepper.Core.Threading;

/// <summary>
/// Test seam for "run this later". The status-pill lifetime rules are pure
/// policy that must be unit-testable on Linux without sleeping, so the view
/// model schedules through this instead of owning a timer.
/// </summary>
public interface IDelayScheduler
{
    /// <summary>
    /// Invoke <paramref name="action"/> after <paramref name="delay"/>.
    /// Implementations must never throw and must never propagate an exception
    /// from <paramref name="action"/>.
    /// </summary>
    void Schedule(TimeSpan delay, Action action);
}
```

Create `src/Winpepper.Core/Threading/SystemDelayScheduler.cs`:

```csharp
namespace Winpepper.Core.Threading;

/// <summary>
/// Production <see cref="IDelayScheduler"/>: a plain <see cref="Task.Delay"/>
/// continuation on the thread pool. The callback is expected to marshal itself
/// onto the UI thread (the view model posts through <see cref="IUiThread"/>).
/// </summary>
public sealed class SystemDelayScheduler : IDelayScheduler
{
    public void Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Task.Delay(delay).ContinueWith(
            _ =>
            {
                // A presentation timer must never take the app down.
                try { action(); } catch { /* best-effort */ }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
```

Edit `src/Winpepper.Core/ViewModels/SessionViewModel.cs`.

(3a) Replace the field block and constructor (`:12-32`) with:

```csharp
    private readonly IUiThread _ui;
    private readonly IDelayScheduler _delays;
    private readonly SessionEngine _engine;
    private readonly Stopwatch _stopwatch = new();
    private SessionStage _stage = SessionStage.Idle;
    private string _statusText = "Ready";
    private long _elapsedMs;
    private ErrorStage? _lastErrorStage;
    private string _lastErrorMessage = "";
    private IDisposable? _busSub;
    private readonly Winpepper.Core.Audio.LevelMeterModel _levelMeter = new();
    private double _inputLevel;
    private readonly PendingPasteState _pending = new();
    // Bumped by EVERY change of what the pill is showing. A scheduled clear
    // carries the token it was issued with and no-ops when the token is stale,
    // so a timer can never clobber a newer state (an in-flight dictation, a
    // newer error, a pending paste).
    private int _presentationGeneration;

    /// <summary>How long an EVENT error holds the pill before it self-clears.</summary>
    public const int EventErrorHoldMs = 6000;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null)
    {
        _engine = engine;
        _ui = ui;
        _delays = delays ?? new SystemDelayScheduler();
        _engine.StateChanged += OnEngineStateChanged;
    }
```

(3b) In the `Stage` setter, bump the generation on every real change — replace
`:37-48` with:

```csharp
        private set
        {
            if (_stage == value) return;
            _stage = value;
            _presentationGeneration++;
            if (value != SessionStage.Recording)
            {
                _levelMeter.Reset();
                InputLevel = 0;
            }
            Raise(nameof(Stage));
            Raise(nameof(StatusText));
        }
```

(3c) Replace `OnBusReport` (`:132-143`) with:

```csharp
    /// <summary>
    /// ERROR TAXONOMY (see <see cref="ErrorClassifier"/>):
    ///
    ///  * EVENT     - a fact about a past moment. No ongoing validity, so it
    ///    only takes the pill while a dictation is in flight and self-clears
    ///    after <see cref="EventErrorHoldMs"/>. At idle it is RECORDED only
    ///    (LastErrorStage/LastErrorMessage feed Diagnostics and the tray text).
    ///  * CONDITION - an ongoing state, so it surfaces even at idle.
    ///
    /// This is the fix for the 2026-07-24 incident: a mid-resume mic fault was
    /// mirrored into Stage=Error while IDLE, and StatusPillWindow's Error arm
    /// stops the auto-hide timer, so the pill squatted on screen for 3.5 hours.
    /// </summary>
    private void OnBusReport(ErrorRecord rec) => _ui.Post(() =>
    {
        LastErrorStage = rec.Stage;
        LastErrorMessage = rec.Message;

        if (ErrorClassifier.Classify(rec) == ErrorKind.Condition)
        {
            // While a pending paste is held, the clickable PENDING pill wins.
            if (_pending.HasPending) return;
            Stage = SessionStage.Error;
            StatusText = $"Error ({rec.Stage}): {rec.Message}";
            return;
        }

        // While a pending paste is held (e.g. a failed pill-click retry), keep
        // the pill in its clickable PENDING state instead of flipping to Error
        // so the user can click again. The error is still recorded above and is
        // surfaced to the user via the toast raised by the caller.
        if (_pending.HasPending) return;
        // Idle scoping: an EVENT error has no ongoing validity, so outside a
        // live dictation it never takes the pill.
        if (!SessionStages.IsDictationInFlight(_stage)) return;
        ShowTransientError($"Error ({rec.Stage}): {rec.Message}");
    });

    /// <summary>
    /// Show an EVENT error on the pill and schedule its return to Idle. The
    /// generation token makes the scheduled clear a no-op if anything newer
    /// took the pill in the meantime.
    /// </summary>
    private void ShowTransientError(string text)
    {
        Stage = SessionStage.Error;
        StatusText = text;
        var token = ++_presentationGeneration;
        _delays.Schedule(
            TimeSpan.FromMilliseconds(EventErrorHoldMs),
            () => _ui.Post(() => ReturnToIdleIfUnchanged(token)));
    }

    /// <summary>
    /// Return the pill to Idle unless something newer owns it. NEVER clears a
    /// CONDITION - conditions live on the tray until a recovery success.
    /// </summary>
    private void ReturnToIdleIfUnchanged(int token)
    {
        if (token != _presentationGeneration) return; // newer state took the pill
        if (_pending.HasPending) return;              // click-to-paste wins
        if (_stage != SessionStage.Error) return;     // stage already moved on
        Stage = SessionStage.Idle;
        StatusText = "Ready";
    }
```

(3d) Replace `NotifyError` (`:152-156`) with:

```csharp
    /// <summary>
    /// A per-dictation pipeline failure reported directly by the host (not via
    /// the bus). Treated as an EVENT: shown now, self-cleared after
    /// <see cref="EventErrorHoldMs"/> - a pipeline error must not strand the
    /// pill either.
    /// </summary>
    public void NotifyError(string message) => _ui.Post(() =>
    {
        if (_pending.HasPending) return;
        ShowTransientError($"Error: {message}");
    });
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: `Failed: 0`. The pre-existing
`SessionViewModelErrorBusTests.Vm_Sets_Stage_To_Error_On_Bus_Report` (an Asr
report at idle) still passes because Asr is a CONDITION, and
`Vm_Updates_LastError_When_ErrorBus_Reports` still passes because the record is
written before any branching.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionStages.cs \
        src/Winpepper.Core/Threading/IDelayScheduler.cs \
        src/Winpepper.Core/Threading/SystemDelayScheduler.cs \
        src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/Threading/ManualDelayScheduler.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs
git commit -m "fix(core): scope EVENT errors to live dictations and self-clear the pill"
```

---

## Task 3: Condition Lifecycle (pill retire + recovery clear)

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (condition state +
  `EnterCondition` + `NotifyConditionRecovered`; replaces the interim CONDITION
  block added in Task 2)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`
  (append)

**Interfaces:**
- Consumes: `ErrorKind`, `ErrorClassifier` (Task 1); `SessionStages`,
  `IDelayScheduler`, `_presentationGeneration`, `ReturnToIdleIfUnchanged`
  (Task 2).
- Produces:
  - `public const int SessionViewModel.ConditionPillHoldMs = 10000;`
  - `public ErrorStage? SessionViewModel.ActiveConditionStage { get; }`
  - `public string SessionViewModel.ActiveConditionMessage { get; }`
  - `public bool SessionViewModel.HasActiveCondition { get; }`
  - `public void SessionViewModel.NotifyConditionRecovered(ErrorStage stage)`

- [ ] **Step 1: Write the failing test**

Append to
`tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`
(inside the existing class, before the closing brace):

```csharp
    private static void ReportMicCondition(ErrorBus bus, string message = "Element not found.")
        => bus.Report(ErrorStage.Audio,
            new MicrophoneUnavailableException(new InvalidOperationException(message)),
            Guid.NewGuid());

    [Fact]
    public void Condition_Grabs_The_Pill_Then_Retires_To_The_Tray()
    {
        var (vm, _, bus, delays) = NewVm();

        ReportMicCondition(bus);

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.HasActiveCondition.ShouldBeTrue();
        vm.ActiveConditionStage.ShouldBe(ErrorStage.Audio);
        vm.ActiveConditionMessage.ShouldBe("Element not found.");
        delays.PendingDelays.ShouldContain(TimeSpan.FromMilliseconds(SessionViewModel.ConditionPillHoldMs));

        delays.FireAll(); // ~10 s later

        // Pill retires...
        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
        // ...but the CONDITION is still true, so it stays on the persistent surface.
        vm.HasActiveCondition.ShouldBeTrue();
        vm.ActiveConditionMessage.ShouldBe("Element not found.");
    }

    [Fact]
    public void Condition_Is_Never_Cleared_By_A_Timer()
    {
        var (vm, _, bus, delays) = NewVm();
        ReportMicCondition(bus);

        delays.FireAll();
        delays.FireAll();
        delays.FireAll();

        vm.HasActiveCondition.ShouldBeTrue();
    }

    [Fact]
    public void RecoverySuccess_Clears_The_Condition_Everywhere()
    {
        var (vm, _, bus, _) = NewVm();
        ReportMicCondition(bus);
        vm.Stage.ShouldBe(SessionStage.Error);

        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeFalse();
        vm.ActiveConditionStage.ShouldBeNull();
        vm.ActiveConditionMessage.ShouldBe("");
        vm.Stage.ShouldBe(SessionStage.Idle);   // pill dropped immediately too
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void RecoverySuccess_After_The_Pill_Retired_Still_Clears_The_Tray()
    {
        var (vm, _, bus, delays) = NewVm();
        ReportMicCondition(bus);
        delays.FireAll();
        vm.Stage.ShouldBe(SessionStage.Idle);

        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Fact]
    public void RecoverySuccess_For_A_Different_Stage_Leaves_The_Condition_Alone()
    {
        var (vm, _, bus, _) = NewVm();
        ReportMicCondition(bus);

        vm.NotifyConditionRecovered(ErrorStage.Asr);

        vm.HasActiveCondition.ShouldBeTrue();
        vm.ActiveConditionStage.ShouldBe(ErrorStage.Audio);
    }

    [Fact]
    public void Condition_Retire_Does_Not_Clobber_A_Dictation_That_Started_Meanwhile()
    {
        var (vm, engine, bus, delays) = NewVm();
        ReportMicCondition(bus);
        vm.Stage.ShouldBe(SessionStage.Error);

        StartDictation(engine);
        vm.Stage.ShouldBe(SessionStage.Recording);

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
        vm.HasActiveCondition.ShouldBeTrue();
    }

    [Fact]
    public void MissingSpeechModel_Is_Surfaced_As_A_Condition_While_Idle()
    {
        var (vm, _, bus, delays) = NewVm();

        bus.Report(ErrorStage.Asr,
            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
            Guid.Empty);

        vm.HasActiveCondition.ShouldBeTrue();
        vm.ActiveConditionStage.ShouldBe(ErrorStage.Asr);
        vm.Stage.ShouldBe(SessionStage.Error);

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.HasActiveCondition.ShouldBeTrue();
    }

    [Fact]
    public void Condition_Raises_PropertyChanged_So_The_Tray_Can_Follow()
    {
        var (vm, _, bus, _) = NewVm();
        var seen = new List<string>();
        vm.PropertyChanged += (_, e) => seen.Add(e.PropertyName ?? "");

        ReportMicCondition(bus);

        seen.ShouldContain(nameof(SessionViewModel.ActiveConditionMessage));
        seen.ShouldContain(nameof(SessionViewModel.ActiveConditionStage));
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: BUILD FAILS with `CS0117: 'SessionViewModel' does not contain a
definition for 'ConditionPillHoldMs'` and `CS1061: ... does not contain a
definition for 'HasActiveCondition' / 'ActiveConditionStage' /
'ActiveConditionMessage' / 'NotifyConditionRecovered'`.

- [ ] **Step 3: Write minimal implementation**

Edit `src/Winpepper.Core/ViewModels/SessionViewModel.cs`.

(3a) Add the condition fields next to `_presentationGeneration` and the second
constant next to `EventErrorHoldMs`:

```csharp
    private ErrorStage? _conditionStage;
    private string _conditionMessage = "";

    /// <summary>
    /// How long a CONDITION grabs the pill before retiring to the tray. The
    /// condition itself is NOT cleared by this timer - only the pill is.
    /// </summary>
    public const int ConditionPillHoldMs = 10000;
```

(3b) Add the public condition surface next to `LastErrorMessage`:

```csharp
    /// <summary>
    /// The stage of the currently-active CONDITION (an ongoing state such as
    /// "microphone unavailable"), or null when none is active. Cleared ONLY by
    /// <see cref="NotifyConditionRecovered"/> - never by a timer.
    /// </summary>
    public ErrorStage? ActiveConditionStage
    {
        get => _conditionStage;
        private set { if (_conditionStage == value) return; _conditionStage = value; Raise(nameof(ActiveConditionStage)); Raise(nameof(HasActiveCondition)); }
    }

    /// <summary>User-facing text of the active condition ("" when none).</summary>
    public string ActiveConditionMessage
    {
        get => _conditionMessage;
        private set { if (_conditionMessage == value) return; _conditionMessage = value; Raise(nameof(ActiveConditionMessage)); }
    }

    /// <summary>True while an ongoing condition is unresolved (drives the tray).</summary>
    public bool HasActiveCondition => _conditionStage is not null;
```

(3c) Replace the interim CONDITION block inside `OnBusReport` (added in Task 2)
with a call to the new method:

```csharp
        if (ErrorClassifier.Classify(rec) == ErrorKind.Condition)
        {
            EnterCondition(rec.Stage, rec.Message);
            return;
        }
```

(3d) Add the condition lifecycle methods next to `ShowTransientError`:

```csharp
    /// <summary>
    /// Enter (or refresh) an ongoing CONDITION. It grabs the pill for
    /// <see cref="ConditionPillHoldMs"/> as an attention grab, then the pill
    /// retires and the condition lives on the persistent surface (tray) until a
    /// RECOVERY SUCCESS clears it. Retiring the pill does NOT clear the
    /// condition - that is the whole point of the taxonomy.
    /// </summary>
    private void EnterCondition(ErrorStage stage, string message)
    {
        ActiveConditionStage = stage;
        ActiveConditionMessage = message;

        // A held pending paste owns the pill; the condition is already on the
        // tray, which is where a long-lived condition belongs anyway.
        if (_pending.HasPending) return;

        Stage = SessionStage.Error;
        StatusText = $"Error ({stage}): {message}";
        var token = ++_presentationGeneration;
        _delays.Schedule(
            TimeSpan.FromMilliseconds(ConditionPillHoldMs),
            () => _ui.Post(() => ReturnToIdleIfUnchanged(token)));
    }

    /// <summary>
    /// A recovery SUCCESS for <paramref name="stage"/> - the ONLY thing that
    /// clears a condition. Called by the host when the warm microphone stream
    /// is rebuilt successfully, or when a speech model actually loads. A
    /// successful rebuild cannot lie; a validity probe can, which is why this
    /// is success-driven rather than polled.
    /// </summary>
    public void NotifyConditionRecovered(ErrorStage stage) => _ui.Post(() =>
    {
        if (_conditionStage != stage) return;
        ActiveConditionStage = null;
        ActiveConditionMessage = "";
        // If the condition is still ON the pill, drop it back to Idle now
        // instead of waiting out the attention-grab window.
        if (_pending.HasPending) return;
        if (_stage != SessionStage.Error) return;
        Stage = SessionStage.Idle;
        StatusText = "Ready";
    });
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: `Failed: 0`, including the 8 new condition tests.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs
git commit -m "feat(core): retire condition errors to a persistent surface, clear on recovery"
```

---

## Task 4: Tray Icon Carries the Condition

**Files:**
- Modify: `src/Winpepper.Core/Tray/TrayIconStateMapper.cs:9-22`
- Modify: `src/Winpepper.App/Tray/TrayIconHost.cs:82-100` (Windows-only)
- Test: `tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs` (append)

**Interfaces:**
- Consumes: `SessionStages.IsDictationInFlight` (Task 2);
  `SessionViewModel.ActiveConditionMessage` (Task 3).
- Produces:
  `TrayIconState TrayIconStateMapper.Map(SessionStage stage, string? lastErrorMessage, bool paused, string? activeConditionMessage = null)`
  (the new parameter is optional, so all existing call sites and tests compile
  unchanged).

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs` (inside
the class):

```csharp
    [Fact]
    public void ActiveCondition_Owns_The_Tray_While_Idle()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, lastErrorMessage: null, paused: false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldContain("Element not found.");
    }

    [Fact]
    public void ActiveCondition_Yields_To_A_Live_Dictation()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, false,
            activeConditionMessage: "Element not found.");

        r.IconName.ShouldBe("AppIcon-Recording.ico");
        r.Tooltip.ShouldBe("Winpepper - Recording...");
    }

    [Fact]
    public void Paused_Still_Overrides_An_Active_Condition()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, null, paused: true,
            activeConditionMessage: "Element not found.");

        r.Tooltip.ShouldBe("Winpepper - Paused");
        r.IconName.ShouldBe("AppIcon.ico");
    }

    [Fact]
    public void No_Condition_Leaves_Idle_Reporting_Ready()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, "an old event error", false,
            activeConditionMessage: null);

        r.IconName.ShouldBe("AppIcon.ico");
        r.Tooltip.ShouldBe("Winpepper - Ready");
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release
```

Expected: BUILD FAILS with `CS1739: The best overload for 'Map' does not have a
parameter named 'activeConditionMessage'`.

- [ ] **Step 3: Write minimal implementation**

Replace `src/Winpepper.Core/Tray/TrayIconStateMapper.cs` with:

```csharp
using Winpepper.Core.ViewModels;

namespace Winpepper.Core.Tray;

public sealed record TrayIconState(string IconName, string Tooltip);

public static class TrayIconStateMapper
{
    /// <summary>
    /// Maps the session surface onto the tray icon. The tray is the PERSISTENT
    /// surface: an ongoing CONDITION (microphone unavailable, no usable speech
    /// model) lives here for exactly as long as it is true, which is why the
    /// status pill is allowed to retire after its attention-grab window.
    /// A live dictation still outranks it - while recording/transcribing the
    /// stage is the more useful signal, and Paused outranks everything.
    /// </summary>
    public static TrayIconState Map(SessionStage stage, string? lastErrorMessage, bool paused,
                                    string? activeConditionMessage = null)
    {
        if (paused) return new TrayIconState("AppIcon.ico", "Winpepper - Paused");

        if (!string.IsNullOrWhiteSpace(activeConditionMessage)
            && !SessionStages.IsDictationInFlight(stage))
            return new TrayIconState("AppIcon-Error.ico", $"Winpepper - {activeConditionMessage}");

        return stage switch
        {
            SessionStage.Recording    => new("AppIcon-Recording.ico", "Winpepper - Recording..."),
            SessionStage.Transcribing => new("AppIcon-Loading.ico",   "Winpepper - Transcribing..."),
            SessionStage.CleaningUp   => new("AppIcon-Loading.ico",   "Winpepper - Cleaning up..."),
            SessionStage.Injecting    => new("AppIcon-Loading.ico",   "Winpepper - Inserting..."),
            SessionStage.Error        => new("AppIcon-Error.ico",     $"Winpepper - Error: {lastErrorMessage ?? "see Diagnostics"}"),
            _                         => new("AppIcon.ico",           "Winpepper - Ready"),
        };
    }
}
```

Edit `src/Winpepper.App/Tray/TrayIconHost.cs` — replace `OnSessionChanged` and
`UpdateFromSession` (`:82-100`) with:

```csharp
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.Stage)
                           or nameof(SessionViewModel.StatusText)
                           or nameof(SessionViewModel.LastErrorMessage)
                           or nameof(SessionViewModel.ActiveConditionMessage))
            UpdateFromSession();
    }

    private void UpdateFromSession()
    {
        // The tray is the persistent surface for an ongoing CONDITION: the pill
        // retires after its attention-grab window, the tray keeps it until a
        // recovery success clears it.
        var state = Winpepper.Core.Tray.TrayIconStateMapper.Map(
            _session.Stage, _session.LastErrorMessage, _paused, _session.ActiveConditionMessage);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", state.IconName);
        if (File.Exists(iconPath))
            _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
        _menu.StatusItemControl.Text = _paused ? "Paused" : _session.StatusText;
        _icon.ToolTipText = state.Tooltip;
        // Tray progress indicator dropped - MenuFlyout doesn't accept ProgressBar
        // children. Live progress is shown by the status pill instead.
    }
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: `Failed: 0`. The four pre-existing `TrayIconStateMapperTests` still
pass unchanged (the new parameter is optional and defaults to null).

`TrayIconHost.cs` is Windows-only (`#if WINDOWS`) and cannot be compiled on
Linux; it is verified by the Windows smoke checklist (Task 9).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Tray/TrayIconStateMapper.cs \
        src/Winpepper.App/Tray/TrayIconHost.cs \
        tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs
git commit -m "feat(tray): surface ongoing conditions on the persistent tray icon"
```

---

## Task 5: Capture Recovery Policy (pure Audio)

**Files:**
- Create: `src/Winpepper.Audio/CaptureRecoveryPolicy.cs`
- Test: `tests/Winpepper.Audio.Tests/CaptureRecoveryPolicyTests.cs`

**Interfaces:**
- Consumes: nothing (pure).
- Produces: `sealed class CaptureRecoveryPolicy` with
  - `CaptureRecoveryPolicy(TimeSpan? debounce = null, Func<DateTime>? clock = null)`
  - `bool IsFailing { get; }`
  - `void NoteFault()`
  - `bool ShouldRebuild()`
  - `bool NoteRebuildResult(bool succeeded)` — returns **true** exactly when
    this result is a *recovery* (first success after a failing state)
  - `public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);`

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Audio.Tests/CaptureRecoveryPolicyTests.cs`:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class CaptureRecoveryPolicyTests
{
    private sealed class FakeClock
    {
        public DateTime Now = new(2026, 7, 24, 5, 48, 0, DateTimeKind.Utc);
        public DateTime Read() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static CaptureRecoveryPolicy NewPolicy(FakeClock clock, TimeSpan? debounce = null)
        => new(debounce ?? CaptureRecoveryPolicy.DefaultDebounce, clock.Read);

    [Fact]
    public void Starts_Healthy()
    {
        var policy = NewPolicy(new FakeClock());

        policy.IsFailing.ShouldBeFalse();
    }

    [Fact]
    public void Fault_Marks_Failing()
    {
        var policy = NewPolicy(new FakeClock());

        policy.NoteFault();

        policy.IsFailing.ShouldBeTrue();
    }

    [Fact]
    public void Device_Event_Burst_Is_Debounced()
    {
        // On resume, WASAPI fires OnDefaultDeviceChanged/OnDeviceStateChanged in
        // bursts. Only the leading edge should drive a rebuild.
        var clock = new FakeClock();
        var policy = NewPolicy(clock);
        policy.NoteFault();

        policy.ShouldRebuild().ShouldBeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(100));
        policy.ShouldRebuild().ShouldBeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        policy.ShouldRebuild().ShouldBeFalse();
    }

    [Fact]
    public void A_Later_Device_Event_Retries_After_The_Debounce_Window()
    {
        var clock = new FakeClock();
        var policy = NewPolicy(clock);
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildResult(succeeded: false);

        clock.Advance(TimeSpan.FromMilliseconds(501));

        policy.ShouldRebuild().ShouldBeTrue();
    }

    [Fact]
    public void First_Success_After_A_Fault_Is_A_Recovery()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();

        policy.NoteRebuildResult(succeeded: true).ShouldBeTrue();
        policy.IsFailing.ShouldBeFalse();
    }

    [Fact]
    public void Success_While_Healthy_Is_Not_A_Recovery()
    {
        // The session-start force-start path calls this on every dictation; a
        // healthy stream must not spam "recovered".
        var policy = NewPolicy(new FakeClock());

        policy.NoteRebuildResult(succeeded: true).ShouldBeFalse();
        policy.NoteRebuildResult(succeeded: true).ShouldBeFalse();
    }

    [Fact]
    public void Failed_Rebuild_Keeps_The_Failing_State()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();

        policy.NoteRebuildResult(succeeded: false).ShouldBeFalse();

        policy.IsFailing.ShouldBeTrue();
    }

    [Fact]
    public void A_New_Fault_After_Recovery_Arms_The_Next_Recovery()
    {
        var clock = new FakeClock();
        var policy = NewPolicy(clock);
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildResult(true).ShouldBeTrue();

        policy.NoteFault();
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.ShouldRebuild().ShouldBeTrue();

        policy.NoteRebuildResult(true).ShouldBeTrue();
    }

    [Fact]
    public void Default_Debounce_Is_500ms()
        => CaptureRecoveryPolicy.DefaultDebounce.ShouldBe(TimeSpan.FromMilliseconds(500));
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0
```

Expected: BUILD FAILS with `CS0246: The type or namespace name
'CaptureRecoveryPolicy' could not be found`.

- [ ] **Step 3: Write minimal implementation**

Create `src/Winpepper.Audio/CaptureRecoveryPolicy.cs`:

```csharp
namespace Winpepper.Audio;

/// <summary>
/// Pure decision logic for endpoint-event-driven microphone recovery
/// (2026-07-24 sleep/resume incident): mid-resume the warm stream faults and
/// the immediate rebuild fails because no default capture endpoint exists yet.
/// Something must retry when a device comes back - and nothing did, because the
/// only remaining seam was the next hotkey press.
///
/// Three decisions live here so they can be unit-tested on Linux while the COM
/// notification client stays a thin Windows shell:
///
///  * IS a retry warranted at all (<see cref="IsFailing"/>)? A healthy warm
///    stream is left alone; the session-start default-device drift check
///    already follows the default endpoint for the "changed the default, then
///    dictated" case.
///  * SHOULD this device event drive a rebuild now (<see cref="ShouldRebuild"/>)?
///    Endpoint notifications arrive in bursts on resume, so only the leading
///    edge of a burst acts; a later event outside the window retries.
///  * WAS this a recovery (<see cref="NoteRebuildResult"/>)? Only the first
///    success after a failing state is a recovery, and a recovery is the ONLY
///    thing that clears the microphone CONDITION. A successful rebuild cannot
///    lie, which is why this is success-driven rather than probe-driven.
/// </summary>
public sealed class CaptureRecoveryPolicy
{
    /// <summary>Endpoint notifications burst on resume; act on the leading edge only.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _debounce;
    private readonly Func<DateTime> _clock;
    private DateTime? _lastRebuildUtc;
    private bool _failing;

    public CaptureRecoveryPolicy(TimeSpan? debounce = null, Func<DateTime>? clock = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True while capture is known to be down (fault, or a failed rebuild).</summary>
    public bool IsFailing => _failing;

    /// <summary>Capture faulted or failed to start: arm recovery.</summary>
    public void NoteFault() => _failing = true;

    /// <summary>
    /// True when this device event should drive a rebuild now. Records the
    /// attempt time, so a burst of notifications produces exactly one rebuild.
    /// </summary>
    public bool ShouldRebuild()
    {
        var now = _clock();
        if (_lastRebuildUtc is { } last && now - last < _debounce) return false;
        _lastRebuildUtc = now;
        return true;
    }

    /// <summary>
    /// Record the outcome of a (re)build attempt. Returns true exactly when
    /// this is a RECOVERY - the first success after a failing state - which is
    /// the signal that clears the microphone condition.
    /// </summary>
    public bool NoteRebuildResult(bool succeeded)
    {
        if (!succeeded)
        {
            _failing = true;
            return false;
        }
        var recovered = _failing;
        _failing = false;
        return recovered;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```

Expected: `Failed: 0`, including the 9 new `CaptureRecoveryPolicyTests` and the
existing 5000-iteration
`ConcurrencyHammer_RebuildVsFrames_NeverThrows`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/CaptureRecoveryPolicy.cs \
        tests/Winpepper.Audio.Tests/CaptureRecoveryPolicyTests.cs
git commit -m "feat(audio): pure recovery policy for endpoint-driven mic rebuilds"
```

---

## Task 6: Endpoint Watcher + Warm Recorder Recovery (Windows-only shell)

**Files:**
- Create: `src/Winpepper.Audio/AudioEndpointWatcher.cs`
- Modify: `src/Winpepper.Audio/IWarmAudioRecorder.cs`
- Modify: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (whole file replaced)

**Interfaces:**
- Consumes: `CaptureRecoveryPolicy` (Task 5); `WarmCaptureCoordinator.Rebuild()`,
  `.EnsureStarted(bool)`, `.IsRunning`, `.ActiveDeviceId`, `.CaptureFaulted`
  (existing, unchanged).
- Produces:
  - `sealed class AudioEndpointWatcher : IMMNotificationClient, IDisposable`
    with `AudioEndpointWatcher(Action onCaptureEndpointChanged, ILogger? log = null)`
  - `event Action? IWarmAudioRecorder.CaptureRecovered`

**Note:** every file in this task is Windows-only (`#if WINDOWS` /
`net9.0-windows10.0.19041.0`), so there is no Linux test for it. All decision
logic it uses is already covered by Task 5; the COM plumbing is verified by the
Windows smoke checklist (Task 9). The Linux verification for this task is that
`Winpepper.Audio` and its tests still build and pass on `net9.0`.

- [ ] **Step 1: Add the recovery signal to the recorder contract**

Replace `src/Winpepper.Audio/IWarmAudioRecorder.cs` with:

```csharp
namespace Winpepper.Audio;

/// <summary>
/// A capture that can start a dictation session with a pre-roll of audio that
/// was already flowing before the session began (Bug 2). Frames are raised only
/// while a session is active, so the voice meter is quiet at idle.
/// </summary>
public interface IWarmAudioRecorder : IDisposable
{
    /// <summary>Raised (mono 16 kHz frames) only while a session is active.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when the capture stream faults or fails to (re)start, so
    /// the host can log it and surface a user-facing signal (Bug 3).</summary>
    event Action<Exception>? CaptureFaulted;

    /// <summary>
    /// Raised when capture is proven healthy again after a fault - i.e. a
    /// rebuild actually succeeded. This is the RECOVERY SUCCESS that clears the
    /// microphone CONDITION; nothing else may clear it, and never a timer.
    /// </summary>
    event Action? CaptureRecovered;

    /// <summary>Begin a session, seeding up to <paramref name="includePrerollMs"/>
    /// milliseconds of already-captured audio.</summary>
    void StartSession(int includePrerollMs);

    /// <summary>End the session and return pre-roll + live audio (mono 16 kHz).</summary>
    float[] StopSession();
}
```

- [ ] **Step 2: Write the endpoint watcher**

Create `src/Winpepper.Audio/AudioEndpointWatcher.cs`:

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Winpepper.Audio;

/// <summary>
/// Thin Windows-only shell over WASAPI endpoint notifications
/// (MMDeviceEnumerator.RegisterEndpointNotificationCallback). It exists for one
/// reason: after a sleep/resume there is a window where no default capture
/// endpoint exists, the warm stream's immediate rebuild fails
/// (0x80070490 "Element not found"), and nothing retries until the user presses
/// a hotkey - which, if the keyboard hook also died, never happens.
///
/// CONTRACT: IMMNotificationClient callbacks arrive on COM/MTA threads that
/// WASAPI serializes. Rebuilding capture takes a lock and can dispose a source
/// (which joins a capture thread), so the handler is ALWAYS marshalled onto the
/// thread pool and the callback thread returns immediately. No decision logic
/// lives here - see <see cref="CaptureRecoveryPolicy"/>.
/// </summary>
public sealed class AudioEndpointWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action _onCaptureEndpointChanged;
    private readonly ILogger? _log;
    private int _disposed;

    public AudioEndpointWatcher(Action onCaptureEndpointChanged, ILogger? log = null)
    {
        _onCaptureEndpointChanged = onCaptureEndpointChanged
            ?? throw new ArgumentNullException(nameof(onCaptureEndpointChanged));
        _log = log;
        _enumerator.RegisterEndpointNotificationCallback(this);
        _log?.LogInformation("Subscribed to audio endpoint notifications");
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture) Signal();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        // A capture endpoint coming back Active is the arrival we care about;
        // the policy decides whether a rebuild is warranted.
        if (newState == DeviceState.Active) Signal();
    }

    public void OnDeviceAdded(string pwstrDeviceId) { }

    public void OnDeviceRemoved(string deviceId) { }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private void Signal()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { _onCaptureEndpointChanged(); }
            catch (Exception ex) { _log?.LogWarning(ex, "audio endpoint change handler failed"); }
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _enumerator.UnregisterEndpointNotificationCallback(this); }
        catch (Exception ex) { _log?.LogDebug(ex, "unregister endpoint notification failed"); }
        try { _enumerator.Dispose(); }
        catch (Exception ex) { _log?.LogDebug(ex, "endpoint enumerator dispose failed"); }
    }
}
#endif
```

- [ ] **Step 3: Wire the recorder**

Replace `src/Winpepper.Audio/WarmWasapiRecorder.cs` with:

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single capture runs for
/// the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll).
///
/// All lifecycle/concurrency/ring/fault logic lives in the pure-managed,
/// Linux-tested <see cref="WarmCaptureCoordinator"/> behind the
/// <see cref="ICaptureSource"/> seam, and all recovery DECISIONS live in the
/// pure <see cref="CaptureRecoveryPolicy"/>. This class is the thin Windows
/// shell that supplies the NAudio <see cref="WasapiCaptureSource"/> factory,
/// re-resolves the default input device on session start, and subscribes to
/// endpoint notifications so a device that comes back after a sleep/resume
/// rebuilds capture WITHOUT waiting for the next hotkey press (2026-07-24
/// incident).
///
/// There are exactly two recovery seams, and both are recovery SUCCESS signals:
/// an endpoint-driven rebuild that works, and an explicit session-start
/// force-start that works.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    private const int RingCapacitySamples = SampleRate16k; // ~1 s of history

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly WarmCaptureCoordinator _coordinator;
    private readonly CaptureRecoveryPolicy _recovery = new();
    private readonly AudioEndpointWatcher? _endpointWatcher;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception>? CaptureFaulted;
    public event Action? CaptureRecovered;

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null, ILogger? log = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        _log = log;
        _coordinator = new WarmCaptureCoordinator(
            _buffer,
            sourceFactory: () => new WasapiCaptureSource(_deviceId, _log));
        _coordinator.FramesAvailable += f => FramesAvailable?.Invoke(f);
        _coordinator.CaptureFaulted += ex =>
        {
            // Capture is down until something rebuilds it: arm recovery so the
            // next endpoint event actually retries.
            _recovery.NoteFault();
            CaptureFaulted?.Invoke(ex);
        };

        try
        {
            _endpointWatcher = new AudioEndpointWatcher(OnCaptureEndpointChanged, _log);
        }
        catch (Exception ex)
        {
            // Non-fatal: we simply fall back to the session-start recovery seam.
            _log?.LogWarning(ex, "audio endpoint notifications unavailable; recovery falls back to session start");
        }

        if (_prewarm)
        {
            _coordinator.EnsureStarted();
            // A prewarm start that never came up (e.g. no default endpoint at
            // boot) is a failing state the endpoint watcher should retry.
            if (!_coordinator.IsRunning) _recovery.NoteFault();
        }
    }

    public void StartSession(int includePrerollMs)
    {
        // Follow the default input device: if it drifted since the warm stream
        // was built, rebuild on the new endpoint (clears the ring too).
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode, or a previously faulted warm stream: (re)start now. force
        // bypasses the fault backoff because the user explicitly asked to record.
        _coordinator.EnsureStarted(force: true);
        // Second recovery seam: an explicit start that brings the stream back is
        // just as much a recovery as an endpoint-driven rebuild. On a healthy
        // stream this returns false, so it never spams the signal.
        if (_coordinator.IsRunning && _recovery.NoteRebuildResult(succeeded: true))
        {
            _log?.LogInformation("Microphone capture recovered on session start");
            CaptureRecovered?.Invoke();
        }
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    /// <summary>
    /// A capture endpoint arrived or the default changed. Runs on a thread-pool
    /// thread (never the COM callback thread - see
    /// <see cref="AudioEndpointWatcher"/>). Only acts when capture is known to
    /// be failing: a healthy warm stream keeps running, and the existing
    /// session-start drift check still follows the default device.
    /// </summary>
    private void OnCaptureEndpointChanged()
    {
        if (!_recovery.IsFailing) return;
        if (!_recovery.ShouldRebuild()) return; // endpoint events burst on resume

        _coordinator.Rebuild();
        var running = _coordinator.IsRunning;
        if (_recovery.NoteRebuildResult(running))
        {
            _log?.LogInformation("Microphone capture recovered on device change");
            CaptureRecovered?.Invoke();
        }
        else if (!running)
        {
            // Content-free: the exception itself already reached the ErrorBus via
            // CaptureFaulted. Stay subscribed - the next endpoint event retries.
            _log?.LogWarning("Microphone rebuild after a device change did not succeed; waiting for the next device event");
        }
    }

    private void RebuildIfDefaultChanged()
    {
        if (!_coordinator.IsRunning) return; // nothing live; EnsureStarted picks the current default
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            if (current.ID != _coordinator.ActiveDeviceId)
                _coordinator.Rebuild();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "default-device recheck failed; keeping current warm stream");
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) _coordinator.StopCapture(); // cold mode tears down between sessions
        return samples;
    }

    public void Dispose()
    {
        _endpointWatcher?.Dispose(); // stop endpoint callbacks BEFORE tearing capture down
        _coordinator.Dispose();
    }
}
#endif
```

- [ ] **Step 4: Verify the pure-managed build and suite are unaffected**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build src/Winpepper.Audio/Winpepper.Audio.csproj -c Release -f net9.0 \
  && /home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```

Expected: `0 errors` and `Failed: 0` (the `#if WINDOWS` files are excluded from
the `net9.0` build; `CaptureRecoveryPolicy` and the coordinator hammer are
compiled and green).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/AudioEndpointWatcher.cs \
        src/Winpepper.Audio/IWarmAudioRecorder.cs \
        src/Winpepper.Audio/WarmWasapiRecorder.cs
git commit -m "feat(audio): rebuild warm capture when a device returns, emit recovery"
```

---

## Task 7: Host Wiring + Session-Start Diagnosability

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (`:42-43` fields,
  `:232-235` model-load success, `:262-284` recorder construction, `:378-382`
  HoldDown start, `:627-632` Toggle start, `:933-939` Dispose)

**Interfaces:**
- Consumes: `MicrophoneUnavailableException` (Task 1);
  `SessionViewModel.NotifyConditionRecovered(ErrorStage)` (Task 3);
  `IWarmAudioRecorder.CaptureRecovered` (Task 6).
- Produces: nothing consumed by later tasks.

**Note:** `Winpepper.App` is Windows-only and is not built on Linux
(`SKIP_WINUI_LINUX`). This task is verified by the Windows smoke checklist
(Task 9); on Linux the requirement is only that the full non-Windows suite
stays green.

- [ ] **Step 1: Report capture faults as the microphone CONDITION**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, replace the
`_captureFaultHandler` assignment (inside `if (_warmRecorder is null)`, `:269-280`)
with:

```csharp
                _captureFaultHandler = ex =>
                {
                    // Capture faults are logged and recorded for Diagnostics but
                    // show NO toast: recovery is automatic (endpoint-driven
                    // rebuild + session-start rebuild), so there is nothing for
                    // the user to act on. The actionable failure - a dictation
                    // that captured no audio - has its own toast at session end
                    // (WarnIfSessionSilent). Consumer toast policy: see
                    // ErrorToastPolicy (Audio stage is silent on the bus too).
                    //
                    // Wrapped in MicrophoneUnavailableException so the taxonomy
                    // can tell this ONGOING condition apart from the
                    // per-dictation "no audio detected" EVENT, which arrives at
                    // the same stage. The inner message is preserved verbatim.
                    _log.LogError(ex, "microphone capture faulted");
                    _errorBus.Report(
                        Winpepper.Core.Errors.ErrorStage.Audio,
                        new Winpepper.Core.Errors.MicrophoneUnavailableException(ex),
                        _currentSessionId);
                };
```

- [ ] **Step 2: Wire the recovery signal**

(2a) Add the handler field next to `_captureFaultHandler` (`:42-43`):

```csharp
    private Action<Exception>? _captureFaultHandler;
    private Action? _captureRecoveredHandler;
    private Action<ReadOnlyMemory<float>>? _frameHandler;
```

(2b) Immediately after `recorder.CaptureFaulted += _captureFaultHandler;`
(`:282`), add:

```csharp
                _captureRecoveredHandler = () =>
                    // A successful rebuild is the ONLY thing that clears the
                    // microphone condition (a validity probe could pass while
                    // capture still fails; a successful rebuild cannot lie).
                    _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Audio);
                recorder.CaptureRecovered += _captureRecoveredHandler;
```

(2c) In `Dispose()`, unhook it alongside the others (`:936-937`):

```csharp
                if (_frameHandler is not null) _warmRecorder.FramesAvailable -= _frameHandler;
                if (_captureFaultHandler is not null) _warmRecorder.CaptureFaulted -= _captureFaultHandler;
                if (_captureRecoveredHandler is not null) _warmRecorder.CaptureRecovered -= _captureRecoveredHandler;
```

- [ ] **Step 3: Clear the speech-model condition when a model actually loads**

In `TryEnsureAsrModel`, immediately after the successful-load log line
(`:232-234`) and before `return true;`, add:

```csharp
                        // Recovery success for the Asr CONDITION ("no usable
                        // speech model"): a model that loads is proof the
                        // condition is over. This also covers the
                        // download-then-TryStart path from the Models page.
                        _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Asr);
```

- [ ] **Step 4: Add the session-start log lines**

In `HandleHotkey`, in the `HoldDown` case, immediately after
`_currentSessionId = Guid.NewGuid();` (`:380`, indented 16 spaces), add:

```csharp
                _log.LogInformation("Session started (hold) {SessionId}", _currentSessionId);
```

In the `Toggle` case's start branch, immediately after
`_currentSessionId = Guid.NewGuid();` (`:630`, indented 20 spaces), add:

```csharp
                    _log.LogInformation("Session started (toggle) {SessionId}", _currentSessionId);
```

Both are content-free (a GUID only). Completion paths already log; the incident
forced guesswork precisely because *starts* logged nothing.

- [ ] **Step 5: Verify the non-Windows suite is still green**

```bash
cd /home/dan/code/winpepper/.worktrees/sleep-resume-error-taxonomy
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
fail=0
for proj in Winpepper.Asr.Tests Winpepper.Audio.Tests Winpepper.Cleanup.Tests \
            Winpepper.Core.Tests Winpepper.Corrections.Tests Winpepper.History.Tests \
            Winpepper.IntegrationTests Winpepper.Models.Tests Winpepper.Platform.Tests; do
  $DOTNET build "tests/$proj/$proj.csproj" -c Release -f net9.0 >/dev/null 2>&1 \
    || { echo "BUILD FAIL $proj"; fail=1; continue; }
  $DOTNET exec "tests/$proj/bin/Release/net9.0/$proj.dll" >/tmp/$proj.log 2>&1 \
    || { echo "TEST FAIL $proj"; fail=1; }
  tail -3 /tmp/$proj.log
done
echo "fail=$fail"
```

Expected: `fail=0`, every project reporting `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): wire mic condition/recovery and log dictation starts"
```

---

## Task 8: Keyboard Hook Survives Suspend/Resume

**Files:**
- Create: `src/Winpepper.Platform/Hotkeys/PowerResumeDecision.cs`
- Create: `src/Winpepper.Platform/Hotkeys/PowerNotificationNative.cs`
- Modify: `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs` (one constant)
- Modify: `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` (`:39-42` fields,
  `:432-465` Start/HookThread, `:587-595` Dispose, plus new methods)
- Test: `tests/Winpepper.Platform.Tests/Hotkeys/PowerResumeDecisionTests.cs`
- Test: `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookReinstallTests.cs`

**Interfaces:**
- Consumes: existing `KeyboardHookNative` P/Invokes and `HotkeyChord.Parse`.
- Produces:
  - `static class PowerResumeDecision` with `bool IsResume(uint notificationType)`
    and the `PBT_*` constants
  - `internal static partial class PowerNotificationNative`
  - `public void HotkeyHook.RequestHookReinstall()`
  - `internal void HotkeyHook.ResetTrackingState()`

**DELICATE.** Only lifecycle is touched. `TryProcessKey` and every swallow /
chord / injected-event / capture rule stay byte-identical. The hook thread and
its `GetMessageW` loop are the ONLY place the hook handle is created or
destroyed - the reinstall runs there too, so the tracking dictionaries are never
touched from two threads.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Hotkeys/PowerResumeDecisionTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class PowerResumeDecisionTests
{
    [Fact]
    public void ResumeSuspend_Is_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMRESUMESUSPEND).ShouldBeTrue();

    [Fact]
    public void ResumeAutomatic_Is_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMRESUMEAUTOMATIC).ShouldBeTrue();

    [Fact]
    public void Suspend_Is_Not_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_APMSUSPEND).ShouldBeFalse();

    [Fact]
    public void PowerSettingChange_Is_Not_A_Resume()
        => PowerResumeDecision.IsResume(PowerResumeDecision.PBT_POWERSETTINGCHANGE).ShouldBeFalse();

    [Fact]
    public void Constants_Match_The_Win32_Values()
    {
        PowerResumeDecision.PBT_APMSUSPEND.ShouldBe(0x0004u);
        PowerResumeDecision.PBT_APMRESUMESUSPEND.ShouldBe(0x0007u);
        PowerResumeDecision.PBT_APMRESUMEAUTOMATIC.ShouldBe(0x0012u);
    }
}
```

Create `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookReinstallTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

/// <summary>
/// A resume must leave NO half-tracked chord behind. Windows silently removes
/// the low-level hook across suspend/resume, so the transitions that would have
/// closed a chord (the key-ups) are simply never delivered; without a reset the
/// hook would think a chord is still held.
/// </summary>
public class HotkeyHookReinstallTests
{
    private const int F24 = 0x87;

    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                      string toggle = "Ctrl+Shift+Space",
                                      string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>(),
               keyPhysicallyDown: _ => true);

    [Fact]
    public void Reinstall_Drops_A_HalfTracked_Hold_So_No_Phantom_HoldUp()
    {
        var hook = NewHook(hold: "F24");
        hook.TryProcessKey(F24, true, out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        hook.RequestHookReinstall(); // system resumed

        hook.TryProcessKey(F24, false, out var up).ShouldBeFalse(); // no longer swallowed
        up.ShouldBeNull();                                          // no phantom HoldUp
    }

    [Fact]
    public void Reinstall_Clears_Modifier_State_So_A_HalfHeld_Chord_Cannot_Complete()
    {
        var hook = NewHook(); // hold = RightCtrl+RightShift
        hook.TryProcessKey(VK_RCONTROL, true, out var first).ShouldBeFalse();
        first.ShouldBeNull(); // chord incomplete so far

        hook.RequestHookReinstall();

        // Without the reset, RightCtrl would still be "held" and this would
        // complete the chord and fire HoldDown.
        hook.TryProcessKey(VK_RSHIFT, true, out var afterResume).ShouldBeFalse();
        afterResume.ShouldBeNull();
    }

    [Fact]
    public void Hook_Still_Works_After_Reinstall()
    {
        var hook = NewHook(hold: "F24", toggle: "F23");

        hook.RequestHookReinstall();

        hook.TryProcessKey(F24, true, out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);
        hook.TryProcessKey(F24, false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        hook.TryProcessKey(0x86, true, out var toggle).ShouldBeTrue();
        toggle!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void Reinstall_On_A_Never_Started_Hook_Is_Safe()
    {
        var hook = NewHook();

        Should.NotThrow(() => hook.RequestHookReinstall());
        Should.NotThrow(() => hook.Dispose());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0
```

Expected: BUILD FAILS with `CS0103: The name 'PowerResumeDecision' does not
exist` and `CS1061: 'HotkeyHook' does not contain a definition for
'RequestHookReinstall'`.

- [ ] **Step 3: Write the pure decision + P/Invokes**

Create `src/Winpepper.Platform/Hotkeys/PowerResumeDecision.cs`:

```csharp
namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Pure decision for the suspend/resume callback: which PBT_* notification
/// types mean "the machine just came back" and therefore require a keyboard
/// hook reinstall.
///
/// Windows silently removes a WH_KEYBOARD_LL hook across suspend/resume when
/// the hook thread stalls, and never tells the owner. That is exactly what the
/// 2026-07-24 incident looked like from the logs: hotkey presses after resume
/// produced ZERO log lines, and an app restart fixed it.
/// </summary>
public static class PowerResumeDecision
{
    public const uint PBT_APMSUSPEND         = 0x0004;
    public const uint PBT_APMRESUMESUSPEND   = 0x0007;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>True when this PBT_* notification means the system resumed.</summary>
    public static bool IsResume(uint notificationType)
        => notificationType is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC;
}
```

Create `src/Winpepper.Platform/Hotkeys/PowerNotificationNative.cs`:

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// powrprof.dll suspend/resume notifications in CALLBACK mode.
///
/// DEVICE_NOTIFY_CALLBACK works WITHOUT a window - which matters here because a
/// message-only window does NOT receive the WM_POWERBROADCAST broadcast, so the
/// usual "create a hidden window" trick would silently never fire. The hook
/// thread has a message loop but no window, so callback mode is the only
/// mechanism that works.
/// </summary>
internal static partial class PowerNotificationNative
{
    public const uint DEVICE_NOTIFY_CALLBACK = 0x00000002;
    public const uint ERROR_SUCCESS = 0;

    /// <summary>
    /// ULONG DeviceNotifyCallbackRoutine(PVOID Context, ULONG Type, PVOID Setting).
    /// Must return ERROR_SUCCESS and must not block: it runs on a system thread.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint DeviceNotifyCallbackRoutine(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        /// <summary>Function pointer to a <see cref="DeviceNotifyCallbackRoutine"/>.</summary>
        public IntPtr Callback;
        public IntPtr Context;
    }

    [LibraryImport("powrprof.dll", SetLastError = true)]
    public static partial uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient,
        out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    public static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);
}
```

In `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs`, add next to
`WM_QUIT`:

```csharp
    /// <summary>
    /// Private thread message asking the hook thread to reinstall the low-level
    /// hook (posted from the suspend/resume callback thread). WM_USER+1: the
    /// hook thread owns no window class, so any WM_USER-range value is free.
    /// </summary>
    public const uint WM_WINPEPPER_REINSTALL_HOOK = 0x0401;
```

- [ ] **Step 4: Add the reinstall path to HotkeyHook**

(4a) Add fields next to `_hookHandle` (`:41-42`):

```csharp
    private IntPtr _hookHandle;
    private IntPtr _powerRegistration;
    // Held for the lifetime of the registration: the OS keeps a raw function
    // pointer to it, so letting the delegate be collected would crash on resume.
    private PowerNotificationNative.DeviceNotifyCallbackRoutine? _powerCallback;
    private LowLevelKeyboardProc? _callback;
```

(4b) In `Start()`, register after the hook is confirmed installed — replace
`:432-440` with:

```csharp
    public void Start()
    {
        if (_hookThread != null) throw new InvalidOperationException("HotkeyHook already started.");
        _hookThread = new Thread(HookThread) { IsBackground = true, Name = "WinpepperHotkeyHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Hotkey hook failed to install within 5s.");
        RegisterPowerNotifications();
    }
```

(4c) Handle the reinstall message in the hook thread's loop — replace `:456-460`
with:

```csharp
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            // Thread messages carry no window, so handle ours here rather than
            // dispatching. Running the reinstall on THIS thread is the point:
            // it is the only thread that touches the hook handle and the
            // per-chord tracking dictionaries, so no locking is introduced.
            if (msg.Message == WM_WINPEPPER_REINSTALL_HOOK)
            {
                ReinstallOnHookThread();
                continue;
            }
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }
```

(4d) Add the new lifecycle methods immediately after `HookCallback` (`:488`):

```csharp
    /// <summary>
    /// Ask the hook thread to reinstall WH_KEYBOARD_LL. Safe to call from any
    /// thread: the unhook/hook and the tracking-state reset run ON the hook
    /// thread via its message loop, so they never race the hook callback.
    /// </summary>
    public void RequestHookReinstall()
    {
        if (_hookThread is null)
        {
            // Never started (unit tests, or before Start): there is no hook to
            // reinstall and no other thread touching tracking state.
            ReinstallOnHookThread();
            return;
        }
        if (!PostThreadMessageW(_hookThreadId, WM_WINPEPPER_REINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero))
            _log.LogWarning("Failed to post hook reinstall to the hook thread: 0x{Err:X}",
                Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Runs on the hook thread. Resets per-chord tracking, then swaps the hook.
    /// </summary>
    private void ReinstallOnHookThread()
    {
        _log.LogInformation("System resumed; reinstalling keyboard hook");
        ResetTrackingState();
        if (_hookThread is null || _callback is null) return; // no live hook to reinstall

        if (_hookHandle != IntPtr.Zero)
        {
            // Expected to fail when Windows already removed the hook - which is
            // precisely the case we are healing. Ignore the result.
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hookHandle == IntPtr.Zero)
            _log.LogWarning("Keyboard hook reinstall failed: 0x{Err:X}", Marshal.GetLastWin32Error());
        else
            _log.LogInformation("Keyboard hook reinstalled");
    }

    /// <summary>
    /// Drops every per-chord tracking entry so a chord that was half-tracked
    /// across a suspend cannot fire (or stay swallowed) after resume: the
    /// key-ups that would have closed it were never delivered. Deliberately
    /// does NOT touch the raw-capture lease or the suspend-for-capture flag - a
    /// settings chord recording in progress stays in progress.
    /// </summary>
    internal void ResetTrackingState()
    {
        _swallowedKeys.Clear();
        _passedThroughKeys.Clear();
        _captureKeysDown.Clear();
        _observedCancelKeys.Clear();
        _modifiers = Modifier.None;
        _holding = false;
        _spaceHold.Cancel();
    }

    private void RegisterPowerNotifications()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            _powerCallback = OnPowerNotification;
            var parameters = new PowerNotificationNative.DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
            {
                Callback = Marshal.GetFunctionPointerForDelegate(_powerCallback),
                Context = IntPtr.Zero,
            };
            var rc = PowerNotificationNative.PowerRegisterSuspendResumeNotification(
                PowerNotificationNative.DEVICE_NOTIFY_CALLBACK, ref parameters, out var handle);
            if (rc == PowerNotificationNative.ERROR_SUCCESS)
            {
                _powerRegistration = handle;
                _log.LogInformation("Registered for suspend/resume notifications");
            }
            else
            {
                _powerCallback = null;
                _log.LogWarning("PowerRegisterSuspendResumeNotification failed: 0x{Err:X}", rc);
            }
        }
        catch (Exception ex)
        {
            _powerCallback = null;
            _log.LogWarning(ex, "suspend/resume notifications unavailable; hotkeys will not self-heal after resume");
        }
    }

    /// <summary>
    /// Runs on a system callback thread: decide, post, return. Never block here.
    /// </summary>
    private uint OnPowerNotification(IntPtr context, uint type, IntPtr setting)
    {
        if (PowerResumeDecision.IsResume(type)) RequestHookReinstall();
        return PowerNotificationNative.ERROR_SUCCESS;
    }

    private void UnregisterPowerNotifications()
    {
        var handle = _powerRegistration;
        _powerRegistration = IntPtr.Zero;
        if (handle != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            try { _ = PowerNotificationNative.PowerUnregisterSuspendResumeNotification(handle); }
            catch (Exception ex) { _log.LogDebug(ex, "PowerUnregisterSuspendResumeNotification failed"); }
        }
        // Only after the OS can no longer call it.
        _powerCallback = null;
    }
```

(4e) Unregister in `Dispose()` — replace `:587-595` with:

```csharp
    public void Dispose()
    {
        UnregisterPowerNotifications(); // stop resume callbacks before teardown
        _spaceHold.Dispose();
        lock (_captureGate) Volatile.Write(ref _rawCapture, null);
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll
```

Expected: `Failed: 0` — the 9 new tests pass AND every pre-existing hotkey test
(`HotkeyHookLogicTests`, `SwallowSelfHealTests`, `ModifierPassthroughTests`,
`RawCaptureTests`, `CaptureDrainSelfHealTests`, `LongPressSpaceHookTests`,
`HotkeyHookIntegrationTests`) still passes unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/PowerResumeDecision.cs \
        src/Winpepper.Platform/Hotkeys/PowerNotificationNative.cs \
        src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs \
        src/Winpepper.Platform/Hotkeys/HotkeyHook.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/PowerResumeDecisionTests.cs \
        tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookReinstallTests.cs
git commit -m "fix(platform): reinstall the keyboard hook after system resume"
```

---

## Task 9: Full-Suite Verification + Windows Smoke

**Files:** none modified. This is the release gate.

- [ ] **Step 1: Run the FULL non-Windows suite**

```bash
cd /home/dan/code/winpepper/.worktrees/sleep-resume-error-taxonomy
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
fail=0
for proj in Winpepper.Asr.Tests Winpepper.Audio.Tests Winpepper.Cleanup.Tests \
            Winpepper.Core.Tests Winpepper.Corrections.Tests Winpepper.History.Tests \
            Winpepper.IntegrationTests Winpepper.Models.Tests Winpepper.Platform.Tests; do
  $DOTNET build "tests/$proj/$proj.csproj" -c Release -f net9.0 >/dev/null 2>&1 \
    || { echo "BUILD FAIL $proj"; fail=1; continue; }
  echo "== $proj"
  $DOTNET exec "tests/$proj/bin/Release/net9.0/$proj.dll" | tail -3 \
    || { echo "TEST FAIL $proj"; fail=1; }
done
echo "fail=$fail"
```

Expected: `fail=0`; total passing >= the 845 baseline plus the ~51 tests this
plan adds (15 classifier + 6 event-lifecycle + 8 condition-lifecycle + 4 tray +
9 recovery-policy + 9 hook/power); `Failed: 0` everywhere.

- [ ] **Step 2: Build the Windows solution on a Windows host**

```powershell
dotnet build winpepper.sln -c Release
```

Expected: `0 Error(s)`. This is the first compile of `Winpepper.App`,
`WarmWasapiRecorder`, `AudioEndpointWatcher`, and `WasapiCaptureSource` in this
change — treat any error here as a blocker.

- [ ] **Step 3: Run the FULL suite on Windows (including Windows-only TFMs)**

```powershell
foreach ($p in "Winpepper.Asr.Tests","Winpepper.Audio.Tests","Winpepper.Cleanup.Tests",
               "Winpepper.Core.Tests","Winpepper.Corrections.Tests","Winpepper.History.Tests",
               "Winpepper.IntegrationTests","Winpepper.Models.Tests","Winpepper.Platform.Tests") {
  dotnet build "tests/$p/$p.csproj" -c Release
  Get-ChildItem "tests/$p/bin/Release" -Recurse -Filter "$p.dll" | ForEach-Object {
    Write-Host "== $($_.FullName)"; dotnet exec $_.FullName
  }
}
```

Expected: `Failed: 0` for every project and every TFM.

- [ ] **Step 4: Windows smoke checklist (manual, on the installed build)**

Normal path:
- [ ] Dictate with the hold hotkey → text is injected; the log contains
      `Session started (hold)` with a session GUID.
- [ ] Dictate with the toggle hotkey → text is injected; the log contains
      `Session started (toggle)` with a session GUID.

Idle-time condition (the incident):
- [ ] With the app IDLE, unplug/disable the default capture device (or
      sleep/resume the machine). The pill appears with
      `Error (Audio): ...` and **retires on its own after ~10 s** — it does NOT
      stay on screen.
- [ ] After the pill retires, the **tray icon is in its error state** and its
      tooltip carries the condition text.
- [ ] Wait several minutes with the device still gone → the tray condition is
      still there (no timer clears it) and the pill has NOT come back.
- [ ] Plug the device back in / let the resume settle → the log contains
      `Microphone capture recovered on device change`, the tray returns to
      `Winpepper - Ready`, and **no app restart was needed**.
- [ ] Dictate immediately after that → it works.

Hotkey survival:
- [ ] Sleep the machine, resume it → the log contains
      `System resumed; reinstalling keyboard hook` followed by
      `Keyboard hook reinstalled`.
- [ ] Press the hold hotkey after resume → a dictation starts (log shows
      `Session started (hold)`), proving the hook is live.
- [ ] Hold the hotkey down THROUGH a sleep/resume (press, sleep, resume,
      release) → no phantom dictation starts or hangs; the next fresh press
      works normally.

Mid-dictation event error:
- [ ] Force an injection failure mid-dictation (e.g. dictate into a window that
      refuses SendInput) → the pill shows `Error (Injection): ...` and returns
      to the idle/hidden state on its own after ~6 s.
- [ ] Confirm the pending-paste behavior is unchanged: when a paste is held, the
      pill stays in its blue click-to-paste state and clicking it still pastes.

Regressions to confirm untouched:
- [ ] Change the default input device while idle, then dictate → the new device
      is used (existing session-start drift check).
- [ ] Record a new hotkey chord in Settings → capture still works and the
      recorded chord fires afterwards.
- [ ] Quit from the tray menu → the app exits cleanly (no hang in
      `HotkeyHook.Dispose` / power unregistration).

- [ ] **Step 5: No commit**

This task changes no files. If any check fails, fix it in the owning task and
re-run Steps 1-4.

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Covering task |
|---|---|
| CHANGE 1: pure classifier (`ErrorKind` Event/Condition), keyed on stage + the specific failure, explicit + documented per stage | Task 1 (`ErrorClassifier` with the full per-stage doc table; Audio disambiguated via `MicrophoneUnavailableException`) |
| CHANGE 1: Audio capture-fault/device-missing is a Condition | Task 1 (classifier) + Task 7 Step 1 (the report site wraps the exception) |
| CHANGE 1: "Models-missing" / ASR-model-missing is a Condition | Task 1 — carried by the **Asr** stage, which is where the ongoing missing-model state is continuously reported (`PipelineHost:216/243/455/698`, `AppShell:380/391`) and where a real recovery signal exists (Task 7 Step 3). The Models stage carries only per-attempt download/verify failures. See "Load-Bearing Taxonomy Decision". |
| CHANGE 1: Injection/Cleanup/OcrUia/Learning/History are Events | Task 1 (theory test covers all of them plus Settings/Hotkey/Crash/Unknown) |
| CHANGE 1: EVENT reports flip the pill only when a dictation is in flight; keep the `HasPendingPaste` guard; at idle record `LastErrorStage`/`LastErrorMessage` only | Task 2 (`OnBusReport` + `SessionStages.IsDictationInFlight`; pending guard preserved verbatim) |
| CHANGE 1: unit-test all paths | Tasks 1-3 (`ErrorClassifierTests`, `SessionViewModelErrorLifecycleTests`) |
| CHANGE 2: condition shows on the pill immediately | Task 3 (`EnterCondition`) |
| CHANGE 2: pill retires after ~10 s with a generation token guarding against clobbering newer states | Task 3 (`ConditionPillHoldMs`, `ReturnToIdleIfUnchanged(token)`) + Task 2 (token bumped in the `Stage` setter) |
| CHANGE 2: persistent tray error state; tooltip carries the condition text | Task 4 (mapper arm + `TrayIconHost` wiring) |
| CHANGE 2: condition clears everywhere the moment recovery succeeds, never by a timer | Task 3 (`NotifyConditionRecovered`, plus `Condition_Is_Never_Cleared_By_A_Timer`) |
| CHANGE 2: EVENT errors shown mid-dictation self-clear after ~6 s with the same no-clobber guard | Task 2 (`ShowTransientError` + `EventErrorHoldMs`; two no-clobber tests) |
| CHANGE 3: register `RegisterEndpointNotificationCallback` for `OnDefaultDeviceChanged` + `OnDeviceStateChanged` | Task 6 (`AudioEndpointWatcher`) |
| CHANGE 3: debounce ~500 ms, then rebuild via `WarmCaptureCoordinator.Rebuild` | Task 5 (`CaptureRecoveryPolicy.ShouldRebuild`, `DefaultDebounce`) + Task 6 (`OnCaptureEndpointChanged`) |
| CHANGE 3: marshal off the COM/MTA callback thread; never block it | Task 6 (`AudioEndpointWatcher.Signal` → thread pool; documented contract) |
| CHANGE 3: on success emit a recovery signal that clears the Audio condition + log the exact line | Task 6 (`CaptureRecovered` + `"Microphone capture recovered on device change"`) + Task 7 Step 2 (host clears the condition) |
| CHANGE 3: on failure the condition stays, content-free WRN, stay subscribed | Task 6 (`OnCaptureEndpointChanged` else-branch; the watcher is never disposed on failure) |
| CHANGE 3: keep the `StartSession` force-start path as the second seam | Task 6 (`StartSession` retains `EnsureStarted(force: true)` and reports recovery there too) |
| CHANGE 3: pure decision logic unit-testable; COM subscription stays thin | Task 5 (9 tests) + Task 6 (watcher has zero decision logic) |
| CHANGE 3: do not regress the device-drift check or dispose discipline; keep the 5000-iteration hammer green | Task 6 (`RebuildIfDefaultChanged` unchanged; coordinator untouched) + Tasks 5/9 (hammer runs in every Audio test run) |
| CHANGE 4: `PowerRegisterSuspendResumeNotification` with `DEVICE_NOTIFY_CALLBACK`; no window reliance | Task 8 (`PowerNotificationNative`, with the message-only-window caveat documented) |
| CHANGE 4: on `PBT_APMRESUMESUSPEND`/`PBT_APMRESUMEAUTOMATIC`, marshal onto the hook thread and reinstall (unhook ignoring failure + `SetWindowsHookEx`) | Task 8 (`PowerResumeDecision`, `RequestHookReinstall` → `WM_WINPEPPER_REINSTALL_HOOK` → `ReinstallOnHookThread`) |
| CHANGE 4: reset per-chord tracking so a half-tracked chord can't fire | Task 8 (`ResetTrackingState`, proven by two discriminating tests) |
| CHANGE 4: exact log lines | Task 8 (`"System resumed; reinstalling keyboard hook"`, `"Keyboard hook reinstalled"`, WRN with the Win32 error) |
| CHANGE 4: unregister on dispose | Task 8 Step 4e |
| CHANGE 4: every existing hook behavior byte-identical | Task 8 (`TryProcessKey` untouched; Step 5 requires all pre-existing hotkey tests green) |
| CHANGE 5: one content-free INFO line at each start seam with the session GUID | Task 7 Step 4 |
| Verification: pure tests on Linux via the xUnit v3 in-process runner; full non-Windows suite; Windows smoke checklist | Every task's test step + Task 9 |
| Do NOT touch `packaging/`; hook touched only for the CHANGE 4 lifecycle | Global Constraints; no task edits `packaging/` or hook matching logic |

**1b. No silent deferrals of required behavior.** Every user-facing requirement
has a real production outcome, and no task substitutes a stub, mock, fake
provider, synthetic URL, TODO, or "seam" for behavior the spec requires:

- The taxonomy, idle scoping, self-clear, condition retire, and recovery
  clearing are all real production code in `SessionViewModel` /
  `ErrorClassifier` / `TrayIconStateMapper`, proven by real assertions against
  the real `ErrorBus`. The only test double is `ManualDelayScheduler`, which
  substitutes for *wall-clock time*, not for behavior: the production
  `SystemDelayScheduler` is wired by default (the VM's `delays` parameter is
  optional and `AppShell` passes nothing), and the observable production
  outcome — pill appears then hides while the tray keeps the condition — is
  proven by the Task 9 smoke checklist.
- Mic recovery is real: `AudioEndpointWatcher` subscribes to real WASAPI
  notifications and `WarmWasapiRecorder` calls the real
  `WarmCaptureCoordinator.Rebuild()`. Its production outcome — unplug/resume,
  then the device returns and dictation works again *without an app restart*,
  with the `recovered` log line — is proven by the Task 9 checklist.
- The hook reinstall is real `SetWindowsHookEx` on the real hook thread; its
  production outcome (hotkeys work after resume, with both log lines) is proven
  by the Task 9 checklist.
- **No UNRESOLVED COVERAGE GAP.** The Models-stage classification is a
  documented taxonomy *decision* with the requirement satisfied at the Asr stage
  (see the Load-Bearing Taxonomy Decision table), not a deferral: the
  missing-speech-model condition IS surfaced, IS persistent on the tray, and IS
  cleared by a real recovery success.
- The Windows-only test boundary (`Winpepper.App`, `WarmWasapiRecorder`,
  `AudioEndpointWatcher`, `TrayIconHost`) is platform-inherent, not a deferral:
  every decision those files make is pushed into a pure class that Linux tests
  cover, and the residue is covered by the Task 9 Windows build + smoke.

**2. Placeholder scan.** No "TBD", "TODO", "implement later", "add appropriate
error handling", "write tests for the above", or "similar to Task N" appears in
any step. Every code step shows complete code; every run step shows the exact
command and the expected output.

**3. Type consistency.** Cross-checked names and signatures:
- `ErrorKind.Event` / `ErrorKind.Condition`, `ErrorClassifier.Classify(ErrorRecord)`
  and `Classify(ErrorStage, string)` — defined in Task 1, used in Tasks 2 and 3.
- `MicrophoneUnavailableException(Exception inner)` — defined in Task 1, used in
  Tasks 1/2/3 tests and Task 7 Step 1.
- `SessionStages.IsDictationInFlight(SessionStage)` — defined in Task 2, used in
  Task 2 (`OnBusReport`) and Task 4 (mapper).
- `IDelayScheduler.Schedule(TimeSpan, Action)` — defined in Task 2, implemented
  by `SystemDelayScheduler` (Task 2) and `ManualDelayScheduler` (Task 2 tests),
  used in Tasks 2 and 3.
- `SessionViewModel.EventErrorHoldMs` (Task 2) and `ConditionPillHoldMs`
  (Task 3) — both `public const int`, referenced by the tests in their own task.
- `ReturnToIdleIfUnchanged(int token)` and `_presentationGeneration` — defined
  in Task 2, reused unchanged by Task 3's `EnterCondition`.
- `NotifyConditionRecovered(ErrorStage)` — defined in Task 3, called in Task 7
  Steps 2 and 3 with `ErrorStage.Audio` and `ErrorStage.Asr`.
- `ActiveConditionMessage` (string, never null) — defined in Task 3, consumed by
  Task 4's mapper parameter `activeConditionMessage` and `TrayIconHost`.
- `TrayIconStateMapper.Map(SessionStage, string?, bool, string? = null)` — the
  fourth parameter is optional, so the three pre-existing call shapes in
  `TrayIconStateMapperTests` and the single production call site both compile.
- `CaptureRecoveryPolicy.{IsFailing, NoteFault, ShouldRebuild, NoteRebuildResult, DefaultDebounce}`
  — defined in Task 5, used identically in Task 6.
- `IWarmAudioRecorder.CaptureRecovered` (`event Action?`) — declared in Task 6,
  implemented by `WarmWasapiRecorder` in Task 6, subscribed/unsubscribed in
  Task 7 with a matching `Action?` field.
- `PowerResumeDecision.IsResume(uint)` plus the four `PBT_*` constants —
  defined in Task 8, used in Task 8's callback and tests.
- `KeyboardHookNative.WM_WINPEPPER_REINSTALL_HOOK` (`uint`) — added in Task 8,
  used by `PostThreadMessageW(uint, uint, IntPtr, IntPtr)` and compared against
  `MSG.Message` (`uint`), so no cast is needed.
- `HotkeyHook.RequestHookReinstall()` is `public` (used by tests and the power
  callback); `ResetTrackingState()` is `internal` and visible to
  `Winpepper.Platform.Tests` via the existing `InternalsVisibleTo`.
