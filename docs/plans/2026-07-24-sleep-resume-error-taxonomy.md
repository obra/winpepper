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
`CaptureRecoveryPolicy` (lock-guarded debounce + bounded one-shot retry +
frames-observed clearing) driven by a thin Windows-only `IMMNotificationClient`
shell, so a returning capture endpoint rebuilds the warm stream and the first
observed non-empty frame emits the recovery signal. `HotkeyHook` registers
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
   `MMDeviceEnumerator.GetDefaultAudioEndpoint` (`WasapiCaptureSource.cs:58-60`) —
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
pass while capture still fails. The proof of recovery is an **observed non-empty
frame from the live stream**, which cannot lie. (`IsRunning` right after a
rebuild CAN lie: NAudio 2.2.1 `WasapiCapture.StartRecording` returns after
`InitializeCaptureDevice()` and only *then* starts the WASAPI pump on the
capture thread, so a stream can report running and fault milliseconds later —
the incident's own `0x88890004` signature.)

To be explicit about what the doctrine bans, so it is not misread later: it bans
timers that **CLEAR** a condition and it bans validity **PROBES**. The bounded
one-shot retry in Tasks 5/6 is neither — it re-runs the recovery (`Rebuild()`)
and lets success clear the condition. That is literally the endorsed sentence
above, not a violation.

Long-lived conditions belong on the **persistent** surface (the tray icon), not
an always-on-top overlay squatting for hours. Hence: attention-grab on the pill
for ~10 s, then retire the pill and keep the tray in its error state.

## Load-Bearing Taxonomy Decision (read before Task 1)

**Governing rule: a stage is classified CONDITION only if this change wires a
recovery signal that can clear it.** A condition with no clearing signal is
exactly the defect being fixed (a permanent error surface), so classifying one
would reintroduce the bug on the tray instead of the pill.

Applying the rule against the actual report sites in the tree. Every path is
fully anchored — bare filenames proved an execution hazard (e.g.
`HotkeyRecorderBox.xaml.cs` lives under `src/Winpepper.App/Views/Controls/`,
NOT `Views/`):

| Report site (file:line) | Stage | Kind | Clearing signal |
|---|---|---|---|
| `src/Winpepper.App/Hosting/PipelineHost.cs:279` (capture-fault handler; Task 7 wraps in `MicrophoneUnavailableException`) | Audio | **CONDITION** | `IWarmAudioRecorder.CaptureRecovered` → `NotifyConditionRecovered(Audio)` (Tasks 6/7) |
| `src/Winpepper.App/Hosting/PipelineHost.cs:906` (`WarnIfSessionSilent`) | Audio | EVENT | none (self-clears) |
| `src/Winpepper.App/Hosting/PipelineHost.cs:216` (`CannotStart`) | Asr | **CONDITION** | model Load success in `TryEnsureAsrModel` (Task 7 Step 3) |
| `src/Winpepper.App/Hosting/PipelineHost.cs:243` (model load/swap failure) | Asr **when `_asr is null`**, else Models | CONDITION / EVENT | Load/Swap success — Task 7 Step 1b splits this site (see below) |
| `src/Winpepper.App/Hosting/PipelineHost.cs:455`, `:698` | Asr | **CONDITION** | Load success at next dictation or Models download→`TryStart` (only reachable while `_asr is null`) |
| `src/Winpepper.App/Hosting/AppShell.cs:380-384`, `:391` (startup gate) | Asr | **CONDITION** | download→`TryStart`→Load success |
| `src/Winpepper.App/Hosting/AppShell.cs:462-466` (`onConfigError`, AssemblyAI rejected) | **re-staged Asr → Models** (Task 7 Step 1a) | EVENT | **none exists** — the dictation already succeeded via local fallback; per the governing rule this MUST NOT be a condition |
| `src/Winpepper.App/Views/ModelsPage.xaml.cs:75`, `:102`, `:295` | Models | EVENT | none |
| `src/Winpepper.App/Hosting/PipelineHost.cs:522`, `:765` | Cleanup | EVENT | none |
| `src/Winpepper.App/Hosting/PipelineHost.cs:358`, `:555`, `:796` | Injection | EVENT | none |
| `src/Winpepper.App/Hosting/PipelineHost.cs:586`, `:827` | Learning | EVENT | none |
| `src/Winpepper.App/Hosting/PipelineHost.cs:313` | Unknown | EVENT | none |
| `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs:168` | Hotkey | EVENT | none |
| `src/Winpepper.Core/Crash/CrashHandler.cs:39` | Crash | EVENT | none |
| (none in tree today) | OcrUia, History, Settings | EVENT | none |

This satisfies the spec's taxonomy ("microphone unavailable, ASR/cleanup model
missing are Conditions") — the missing-speech-model condition is carried by the
**Asr** stage, which is where it is continuously reported and where a real
recovery signal exists. The **Models** stage carries only per-attempt failures.

**The Asr stage was NOT uniform in the tree, and two sites are re-staged at the
report site (Task 7 Steps 1a/1b) rather than special-cased in the classifier:**

- `AppShell.cs:462-466` (`onConfigError`, "AssemblyAI model rejected") is fired
  per dictation attempt from `FallbackTranscriber.cs:58-61`, **after which the
  dictation succeeds via local fallback**. Under a blanket `Asr ⇒ Condition` it
  would become a permanent tray condition whose only wired clearing seam (local
  model Load/Swap success) never fires for such a user
  (`AsrModelSwapState.Plan` → `KeepCurrent`) — the exact defect the governing
  rule forbids. It is re-staged to **Models** (EVENT).
- `PipelineHost.cs:242-244` keeps the old working model on a failed swap
  (`return _asr is not null;` at `:244`), so when a usable session survives the
  report is a per-attempt EVENT at **Models**, not the ongoing missing-model
  condition — which would otherwise never clear if the user reverts their
  selection.

Re-staging needs **zero classifier change** and is behavior-preserving
everywhere else: `ErrorDeepLink` maps both Asr and Models to "models" /
"Open Models tab" (`ErrorDeepLink.cs:12,17,29,34`) and `ErrorToastPolicy`
toasts both (`ErrorToastPolicy.cs:33-34`).

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
- **Windows compile checkpoints are INCREMENTAL, not end-gated.** CI
  (`.github/workflows/ci.yml:28-42`, job `windows-build`) already builds the
  full solution and runs the suite on `windows-latest`, and `scripts/winrun`
  does the same against a QEMU VM. Each task that touches Windows-only code
  (Tasks 4, 6, 7, 8) ends with a Windows compile checkpoint:
  `./scripts/winrun "dotnet build winpepper.sln -c Release"` — or, if the VM is
  unavailable, push the branch and require the CI `windows-build` job green
  (NOTE: pushing before a full Windows-host suite run is in tension with
  AGENTS.md's "full suite must pass on a Windows host before pushing" — get an
  owner ruling before using the CI path; the VM path avoids the tension).
  Task 9's Windows build is therefore a re-verification, not the first compile.
- **Do NOT touch `packaging/`.** Do NOT touch the WinUI XAML files.
  `StatusPillWindow.xaml.cs` needs **no change** — the fix is entirely in the
  view model that drives it.
- **The keyboard hook may be touched ONLY for the CHANGE 4 lifecycle work**
  (which, as amended, includes the observability-only heartbeat telemetry —
  one additive tick-write at `HookCallback` entry, Task 8 Step 4f). Every
  existing behavior (chord tracking, injected-event handling, swallow rules,
  capture suspend/resume for chord recording) stays byte-identical. One honest
  narrowing is recorded in Task 8's A15 note: `ResetTrackingState` clears
  `_captureKeysDown` and cancels `_spaceHold`, so a reinstall DURING raw
  capture / drain is behavior the tests do not pin.
- **The coordinator is unchanged except for ONE additive `FrameObserved` event
  on the existing lock-free callback path** (Task 6 Step 1a). Its
  lock/epoch-guard/dispose-scheduler discipline, the session-start
  default-device drift check (`RebuildIfDefaultChanged`), and the
  5000-iteration concurrency hammer
  (`WarmCaptureCoordinatorTests.ConcurrencyHammer_RebuildVsFrames_NeverThrows`)
  are untouched and must stay green. In particular, `OnSourceStopped`'s
  unconditional `CaptureFaulted?.Invoke(ex)` at
  `WarmCaptureCoordinator.cs:197` — raised even when its own in-lock retry at
  `:177` SUCCEEDED — must NOT be changed: it is load-bearing for observability
  and is pinned by the existing test
  `Fault_RaisesCaptureFaulted_AndAutoRebuildsWhenPastBackoff`
  (`WarmCaptureCoordinatorTests.cs:189`).
- **IMMNotificationClient callbacks arrive on COM/MTA threads.** Never rebuild
  capture (which takes a lock and may dispose a source that joins a capture
  thread) on the callback thread — always hand off first. NOTE: the hand-off
  (`ThreadPool.QueueUserWorkItem`) DE-serializes the callbacks, so several
  endpoint handlers can run concurrently — which is why every recovery decision
  lives behind `CaptureRecoveryPolicy`'s private lock (Task 5).
- **Fixed timings (owner-agreed, copy verbatim):** EVENT error pill hold =
  **6000 ms**; CONDITION pill attention-grab = **10000 ms**; device-event
  rebuild debounce = **500 ms** (unchanged). Plan-set retry constants (Task 5):
  one-shot retry delay = **2 s**, max **5** scheduled retries per endpoint
  event (budget refilled by each fresh event).
- **Exact log strings (copy verbatim):**
  - `"Microphone capture recovered (frames observed)"` — the recovery line the
    code actually emits, from the frames handler (clearing is frames-driven).
  - `"Microphone capture recovered on device change"` — the owner-agreed
    original wording, preserved here for the record; under frames-driven
    clearing NO code path emits it, so smoke steps must grep for the
    frames-observed line above instead.
  - `"System resumed; reinstalling keyboard hook"`
  - `"Keyboard hook reinstalled"` (now suffixed with the hook-thread id —
    grep for this prefix)
  - `"Session started (hold)"` / `"Session started (toggle)"` (each with the
    session GUID)
  - `"Microphone rebuild ({Trigger}) did not succeed; one-shot retry in {DelayMs} ms"`
    and `"Microphone rebuild ({Trigger}) did not succeed; retry budget spent, waiting for the next device event"`
    (both content-free)
- **Logs must stay content-free** — never log transcript text or user content.

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/Winpepper.Core/Errors/ErrorKind.cs` | The two-value taxonomy enum. |
| `src/Winpepper.Core/Errors/MicrophoneUnavailableException.cs` | Marker exception that makes an Audio *condition* distinguishable from an Audio *event*. |
| `src/Winpepper.Core/Errors/ErrorClassifier.cs` | Pure `ErrorRecord` → `ErrorKind` classification, documented per stage. |
| `src/Winpepper.Core/ViewModels/SessionStages.cs` | Pure "is a dictation in flight?" predicate — ONE definition, TWO overloads: the VM scopes EVENT errors by the ENGINE state (`SessionState`) and the tray mapper asks about the presentation stage (`SessionStage`) it consumes; both answer the same concept from different truthful inputs. |
| `src/Winpepper.Core/Threading/IDelayScheduler.cs` | Test seam for the two presentation timers. |
| `src/Winpepper.Core/Threading/SystemDelayScheduler.cs` | Production `Task.Delay` implementation. |
| `src/Winpepper.Audio/CaptureRecoveryPolicy.cs` | Pure, LOCK-GUARDED debounce + bounded one-shot retry + frames-observed clearing state machine for endpoint-driven mic recovery. |
| `src/Winpepper.Audio/AudioEndpointWatcher.cs` | Windows-only `IMMNotificationClient` shell; filters device-state signals to CAPTURE-flow endpoints, logs endpoint evidence, and marshals events off the COM thread. |
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
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` | Taxonomy-driven `OnBusReport` scoped by an engine-state mirror, transient EVENT display, per-stage condition map lifecycle, `NotifyConditionRecovered`, generation-token no-clobber guard. |
| `src/Winpepper.Core/Tray/TrayIconStateMapper.cs` | New optional `activeConditionMessage` arm (persistent surface for conditions). |
| `src/Winpepper.Audio/IWarmAudioRecorder.cs` | New `CaptureRecovered` event. |
| `src/Winpepper.Audio/WarmCaptureCoordinator.cs` | ONE additive `FrameObserved` event on the existing frame-ingest path (epoch-guarded, non-empty-gated); nothing else changes. |
| `src/Winpepper.Audio/WarmWasapiRecorder.cs` | Endpoint watcher + recovery policy wiring; bounded one-shot retry after a failed rebuild; recovery signal on first observed non-empty frame. |
| `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs` | One private thread-message constant + `GetLastInputInfo` P/Invoke for the hook heartbeat telemetry. |
| `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs` | Suspend/resume registration, hook-thread reinstall, tracking-state reset, heartbeat telemetry. |
| `src/Winpepper.App/Tray/TrayIconHost.cs` | Pass the active condition message to the mapper; listen for its change. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Wrap capture faults, wire `CaptureRecovered` → clear Audio condition, split the `:243` swap-failure report on `_asr is null`, clear Asr condition on model load, session-start log lines. |
| `src/Winpepper.App/Hosting/AppShell.cs` | One-token re-stage of the AssemblyAI `onConfigError` report (`ErrorStage.Asr` → `ErrorStage.Models`, `:463`). |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs` | Rewrite `ErrorReport_WithoutPending_StillFlipsToError` to run mid-dictation (Task 2 Step 4a) — it encoded exactly the idle behavior this plan removes. |

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

    [Fact]
    public void AssemblyAi_Config_Rejection_Is_An_Event()
    {
        // AppShell.BuildTranscriber onConfigError reports at Models: the cloud
        // attempt failed but the dictation succeeded via local fallback, and no
        // recovery signal exists that could clear it (governing rule).
        var rec = Record(ErrorStage.Models,
            new InvalidOperationException("AssemblyAI model rejected (foo). Check the model setting."));
        ErrorClassifier.Classify(rec).ShouldBe(ErrorKind.Event);
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
///                cleared when a model actually loads. Every Asr report site
///                denotes exactly this state: the two sites that did not
///                (the per-attempt AssemblyAI config rejection, and the model
///                swap that keeps the old working session) are re-staged to
///                Models AT the report site - see the plan's Load-Bearing
///                Taxonomy Decision table.
///   Models     - EVENT: each report is one attempt that failed - a
///                user-initiated verify/download, a cloud (AssemblyAI) config
///                rejection after which the dictation succeeded via local
///                fallback, or a swap that kept the old working model. The
///                ongoing missing-model state is reported at the Asr stage.
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
`ErrorClassifierTests` (16 cases) included; the pre-existing Core tests still
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
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs:12-32` (fields +
  ctor; `:10` is the class declaration and `:11` is `{` — do NOT touch them),
  `:34-49` (the whole `Stage` property; the edited setter body is `:37-48`),
  `:132-143` (`OnBusReport`), `:152-156` (`NotifyError`), `:176-206`
  (`OnEngineStateChanged` — engine-state mirror, edit 3e)
- Modify: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs:78-87`
  (Step 4a — the ONE pre-existing test that encodes the removed idle behavior)
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs`

**Interfaces:**
- Consumes: `ErrorClassifier.Classify(ErrorRecord)` / `ErrorKind` (Task 1).
- Produces:
  - `static class SessionStages` with `bool IsDictationInFlight(SessionStage)`
    (presentation form, for the tray mapper) and
    `bool IsDictationInFlight(SessionState)` (engine-truth form, for the VM's
    EVENT-error scoping)
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

        // RESYNC, not a hard reset: the engine is still Recording, so the pill
        // goes back to Recording - NOT to Idle/"Ready" (which would hide the
        // pill mid-dictation and kill the voice meter for the rest of the
        // session, since ReportAudioFrame only accepts frames while
        // _stage == Recording).
        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
    }

    [Fact]
    public void SelfClear_MidDictation_Restores_The_Live_Voice_Meter()
    {
        // DISCRIMINATING: this is the test that fails if the self-clear hard
        // resets to Idle. ReportAudioFrame (SessionViewModel.cs:170-174)
        // early-returns unless _stage == Recording, and the engine does NOT
        // re-raise Recording, so an Idle reset silences the meter permanently.
        var (vm, engine, bus, delays) = NewVm();
        StartDictation(engine);
        bus.Report(ErrorStage.Cleanup, new InvalidOperationException("boom"), Guid.NewGuid());
        vm.Stage.ShouldBe(SessionStage.Error);

        delays.FireAll();

        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.ReportAudioFrame(new float[] { 0.5f, -0.5f, 0.5f, -0.5f });
        vm.InputLevel.ShouldBeGreaterThan(0.0);
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

        delays.PendingCount.ShouldBe(2); // both the older and the newer clear are pending

        delays.FireAll(); // fires BOTH timers; only the newest may clear

        // The stale token's callback must not release the pill early; the
        // newest one then resyncs to the still-live engine state.
        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
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
        // The real call site is PipelineHost AFTER SessionEvent.Failed, i.e.
        // with the engine already back at Idle - so the resync lands on Idle
        // here, and the pre-existing NotifyError contract is unchanged.
        var (vm, _, _, delays) = NewVm();

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
using Winpepper.Core.Sessions;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Pure "is a dictation in flight?" predicates - one shared definition with two
/// truthful inputs. The VM scopes EVENT errors by the ENGINE state (the pill's
/// own stage becomes Error the moment an error shows, so it cannot answer the
/// question); the tray mapper asks about the presentation stage it consumes.
/// </summary>
public static class SessionStages
{
    /// <summary>Presentation-stage form, for the tray mapper: Idle, Error and
    /// PendingPaste are resting/waiting states, not a dictation.</summary>
    public static bool IsDictationInFlight(SessionStage stage) => stage is
        SessionStage.Recording or
        SessionStage.Transcribing or
        SessionStage.CleaningUp or
        SessionStage.Injecting;

    /// <summary>Engine-truth form, for the view model's EVENT-error scoping.
    /// The engine has no Error stage, so it is a faithful in-flight signal
    /// even while an error owns the pill.</summary>
    public static bool IsDictationInFlight(SessionState state) =>
        state is not SessionState.Idle;
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
    // UI-thread mirror of the ENGINE state. EVENT-error scoping must key on
    // this, not _stage: once an error takes the pill _stage reads Error, which
    // would wrongly report "not in flight" while the engine is still Recording.
    private SessionState _engineState = SessionState.Idle;

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
        // live dictation it never takes the pill. Keyed on the ENGINE state:
        // the presentation stage reads Error while an error is showing and
        // cannot answer "is the user mid-dictation?".
        if (!SessionStages.IsDictationInFlight(_engineState)) return;
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
            () => _ui.Post(() => ReleasePillIfUnchanged(token)));
    }

    /// <summary>
    /// Release the pill from an error presentation unless something newer owns
    /// it. It RESYNCS to the live engine state - it does NOT hard-reset to
    /// Idle. That distinction is load-bearing: an EVENT error only ever takes
    /// the pill while a dictation is IN FLIGHT, so when its hold expires the
    /// normal case is that we are STILL Recording/Transcribing/Injecting. The
    /// generation token only guards against a state change that happens AFTER
    /// the error took the pill; when the engine was already in flight and did
    /// not transition during the hold, nothing else restores the stage. A hard
    /// "Idle / Ready" there would:
    ///   * hide the pill mid-dictation (StatusPillWindow.xaml.cs:148-155 - the
    ///     Idle arm clears _visible and starts the auto-hide timer),
    ///   * make the tray read "Winpepper - Ready" while recording, and
    ///   * kill the voice meter for the rest of the session (ReportAudioFrame,
    ///     SessionViewModel.cs:170-174, early-returns unless
    ///     _stage == Recording, and the engine never re-raises Recording).
    /// NEVER clears a CONDITION - conditions live on the tray until a recovery
    /// success.
    /// </summary>
    private void ReleasePillIfUnchanged(int token)
    {
        if (token != _presentationGeneration) return; // newer state took the pill
        if (_pending.HasPending) return;              // click-to-paste wins
        if (_stage != SessionStage.Error) return;     // stage already moved on
        ResyncPillToEngineState();
    }

    /// <summary>
    /// Put the pill back in step with the ENGINE - the single source of truth
    /// for "what is this session actually doing right now?". Mirrors the
    /// OnEngineStateChanged switch (minus its stopwatch/pending side effects,
    /// which belong to real transitions); keep the two in step.
    /// </summary>
    private void ResyncPillToEngineState()
    {
        switch (_engineState)
        {
            case SessionState.Recording:
                Stage = SessionStage.Recording;
                StatusText = "Recording...";
                break;
            case SessionState.Transcribing:
                Stage = SessionStage.Transcribing;
                StatusText = "Transcribing...";
                break;
            case SessionState.Injecting:
                Stage = SessionStage.Injecting;
                StatusText = "Inserting...";
                break;
            default:
                Stage = SessionStage.Idle;
                StatusText = "Ready";
                break;
        }
    }
```

(3d) Replace `NotifyError` (`:152-156`) with:

```csharp
    /// <summary>
    /// A per-dictation pipeline failure reported directly by the host (not via
    /// the bus). Treated as an EVENT: shown now, self-cleared after
    /// <see cref="EventErrorHoldMs"/> - a pipeline error must not strand the
    /// pill either.
    ///
    /// INTENTIONALLY NOT idle-scoped: the host calls this after
    /// SessionEvent.Failed, when the engine is ALREADY Idle, and the
    /// pre-existing NotifyError_Sets_ErrorStage_With_Message depends on the
    /// error still showing. A future "consistency" edit adding the in-flight
    /// check here is a REGRESSION, not a cleanup.
    /// </summary>
    public void NotifyError(string message) => _ui.Post(() =>
    {
        if (_pending.HasPending) return;
        ShowTransientError($"Error: {message}");
    });
```

(3e) In `OnEngineStateChanged` (`:176-206`), make `_engineState = to;` the
**FIRST** statement inside the posted lambda, before the `switch (to)`:

```csharp
    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            // FIRST, before the switch: the UI-thread mirror of the engine
            // state that OnBusReport scopes EVENT errors by. Do NOT read
            // _engine.State directly from OnBusReport instead - that would be
            // a cross-thread read of a plain property AND would lose the
            // IUiThread.Post ordering this mirror inherits.
            _engineState = to;
            switch (to)
            {
```

The rest of the method (`switch` body and closing braces) stays byte-identical.

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: **`Failed: 1`** — exactly one pre-existing test fails:
`SessionViewModelPendingTests.ErrorReport_WithoutPending_StillFlipsToError`
(`tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs:78-87`).
It reports an Injection EVENT at idle and asserts `Stage == Error` — precisely
the behavior this task deliberately removes. Step 4a rewrites it. These are the
ONLY pre-existing tests affected, exhaustively verified by grepping the entire
test tree during validation; Tasks 3 and 4 break nothing further.
Meanwhile `SessionViewModelErrorBusTests.Vm_Sets_Stage_To_Error_On_Bus_Report`
(an Asr report at idle) still passes because Asr is a CONDITION, and
`Vm_Updates_LastError_When_ErrorBus_Reports` still passes because the record is
written before any branching.

- [ ] **Step 4a: Rewrite the one pre-existing test that encodes the removed behavior**

In `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs`,
replace `ErrorReport_WithoutPending_StillFlipsToError` (`:78-87`) with:

```csharp
    [Fact]
    public void ErrorReport_WithoutPending_MidDictation_StillFlipsToError()
    {
        // Contrast with ErrorReport_WhilePending_KeepsPendingClickable: it is
        // the pending slot, not the error, that keeps the pill clickable. With
        // no pending slot, an EVENT error DOES take the pill - but only inside
        // a live dictation (idle EVENT errors are recorded only; see
        // SessionViewModelErrorLifecycleTests).
        var (vm, engine) = NewVm();
        var bus = new ErrorBus();
        vm.AttachErrorBus(bus);
        engine.Apply(SessionEvent.StartRequested); // Recording: in flight

        bus.Report(ErrorStage.Injection, new InvalidOperationException("boom"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
    }
```

Its real invariant — the pending slot, not the error kind, decides whether the
pill stays clickable — is preserved by moving it inside a live dictation.
Re-run the Step 4 command. Expected: `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionStages.cs \
        src/Winpepper.Core/Threading/IDelayScheduler.cs \
        src/Winpepper.Core/Threading/SystemDelayScheduler.cs \
        src/Winpepper.Core/ViewModels/SessionViewModel.cs \
        tests/Winpepper.Core.Tests/Threading/ManualDelayScheduler.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorLifecycleTests.cs \
        tests/Winpepper.Core.Tests/ViewModels/SessionViewModelPendingTests.cs
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
  `IDelayScheduler`, `_presentationGeneration`, `ReleasePillIfUnchanged`,
  `ResyncPillToEngineState` (Task 2).
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
    public void RecoverySuccess_Does_Not_Wipe_A_Newer_Unrelated_Error_Off_The_Pill()
    {
        // DISCRIMINATING: reachable for real. The mic condition retires to the
        // tray; the user then starts dictating and an Injection EVENT error
        // takes the pill; capture frames resume and the host calls
        // NotifyConditionRecovered(Audio). Without the condition-ownership
        // stamp, the recovery sees _stage == Error and blows away an unrelated
        // error the user has not read yet (and its scheduled self-clear).
        var (vm, engine, bus, delays) = NewVm();
        ReportMicCondition(bus);
        delays.FireAll();                      // condition pill retires to the tray
        StartDictation(engine);
        bus.Report(ErrorStage.Injection, new InvalidOperationException("SendInput refused"), Guid.NewGuid());
        vm.StatusText.ShouldBe("Error (Injection): SendInput refused");

        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeFalse();                 // the condition IS cleared
        vm.Stage.ShouldBe(SessionStage.Error);                 // ...but the EVENT error keeps the pill
        vm.StatusText.ShouldBe("Error (Injection): SendInput refused");

        delays.FireAll();                      // the EVENT error's own hold expires

        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
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

    [Fact]
    public void Two_True_Conditions_Coexist_And_Clear_Independently()
    {
        // Reachable for real: an Asr swap-failure condition while running plus
        // an Audio capture fault after a sleep. A single condition slot would
        // let NotifyConditionRecovered(Audio) erase the still-true Asr
        // condition and the tray would read "Ready" while it is not.
        var (vm, _, bus, _) = NewVm();
        ReportMicCondition(bus);
        bus.Report(ErrorStage.Asr,
            new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
            Guid.Empty);
        vm.ActiveConditionMessage.ShouldContain("Element not found.");
        vm.ActiveConditionMessage.ShouldContain("Speech model not installed.");

        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeTrue();   // the Asr condition is still true
        vm.ActiveConditionMessage.ShouldContain("Speech model not installed.");
        vm.ActiveConditionMessage.ShouldNotContain("Element not found.");
        vm.ActiveConditionStage.ShouldBe(ErrorStage.Asr);
    }

    [Fact]
    public void Repeated_Reports_Of_The_Same_Condition_Do_Not_Regrab_The_Pill()
    {
        var (vm, _, bus, delays) = NewVm();
        ReportMicCondition(bus);
        delays.FireAll();                        // pill retired to the tray
        vm.Stage.ShouldBe(SessionStage.Idle);

        ReportMicCondition(bus, "Element not found (retry).");  // failed rebuild re-reports

        vm.Stage.ShouldBe(SessionStage.Idle);    // the pill has NOT come back
        delays.PendingCount.ShouldBe(0);
        vm.HasActiveCondition.ShouldBeTrue();
        vm.ActiveConditionMessage.ShouldContain("retry");       // tray text still refreshes
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
constant next to `EventErrorHoldMs`. More than one condition can be true at
once (e.g. an Asr swap-failure while running plus an Audio capture fault after
a sleep), so the model is a PER-STAGE MAP, not a single slot — a single slot
would let one stage's recovery silently erase a still-true condition:

```csharp
    // ONE entry per stage with a currently-true CONDITION. A map, not a single
    // slot: two conditions can be true at once and each clears independently
    // on ITS recovery.
    private readonly Dictionary<ErrorStage, string> _activeConditions = new();

    /// <summary>
    /// The _presentationGeneration stamp of the CONDITION that most recently
    /// grabbed the pill, or 0 if none has. NotifyConditionRecovered releases
    /// the pill ONLY when this still equals _presentationGeneration - i.e.
    /// only when a condition is what is actually on screen. Without it, a
    /// recovery would wipe an UNRELATED newer EVENT error off the pill (mic
    /// condition retires to the tray -> an Injection EVENT error takes the
    /// pill mid-dictation -> frames resume -> NotifyConditionRecovered sees
    /// _stage == Error and blows away the injection error the user has not
    /// seen yet, along with its own scheduled self-clear).
    /// </summary>
    private int _conditionPresentationGeneration;

    /// <summary>
    /// How long a CONDITION grabs the pill before retiring to the tray. The
    /// condition itself is NOT cleared by this timer - only the pill is.
    /// </summary>
    public const int ConditionPillHoldMs = 10000;
```

(3b) Add the public condition surface next to `LastErrorMessage`. The
tray/mapper contract (`string? activeConditionMessage`) is unchanged, so Task 4
needs no rework:

```csharp
    /// <summary>Stage of the most relevant active CONDITION (null when none).
    /// More than one condition can be true at once (e.g. mic unavailable AND a
    /// speech-model load failure); each clears independently on ITS recovery.</summary>
    public ErrorStage? ActiveConditionStage =>
        _activeConditions.Count == 0 ? null : _activeConditions.Keys.Last();

    /// <summary>User-facing text of ALL active conditions ("" when none).</summary>
    public string ActiveConditionMessage =>
        _activeConditions.Count == 0 ? "" : string.Join(" | ", _activeConditions.Values);

    /// <summary>True while any ongoing condition is unresolved (drives the tray).</summary>
    public bool HasActiveCondition => _activeConditions.Count > 0;
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
    /// Enter (or refresh) an ongoing CONDITION. A NEW condition grabs the pill
    /// for <see cref="ConditionPillHoldMs"/> as an attention grab, then the
    /// pill retires and the condition lives on the persistent surface (tray)
    /// until a RECOVERY SUCCESS clears it. Retiring the pill does NOT clear
    /// the condition - that is the whole point of the taxonomy.
    /// </summary>
    private void EnterCondition(ErrorStage stage, string message)
    {
        var isRefresh = _activeConditions.ContainsKey(stage);
        _activeConditions[stage] = message;
        Raise(nameof(ActiveConditionStage)); Raise(nameof(ActiveConditionMessage)); Raise(nameof(HasActiveCondition));

        // Re-reports of an ALREADY-SURFACED condition (each failed endpoint-driven
        // rebuild re-raises CaptureFaulted) update the tray text but do NOT
        // re-grab the pill: under device churn an enter-or-refresh grab would
        // keep the pill on screen indefinitely - the original defect, softened.
        if (isRefresh) return;

        // A held pending paste owns the pill; the condition is already on the
        // tray, which is where a long-lived condition belongs anyway.
        if (_pending.HasPending) return;
        Stage = SessionStage.Error;
        StatusText = $"Error ({stage}): {message}";
        var token = ++_presentationGeneration;
        // Stamp the pill as CONDITION-owned so a later recovery can tell
        // "my condition is on screen" from "something newer replaced it".
        _conditionPresentationGeneration = token;
        _delays.Schedule(TimeSpan.FromMilliseconds(ConditionPillHoldMs),
            () => _ui.Post(() => ReleasePillIfUnchanged(token)));
    }

    /// <summary>
    /// A recovery SUCCESS for <paramref name="stage"/> - the ONLY thing that
    /// clears a condition. Called by the host when the warm microphone stream
    /// is proven delivering frames again, or when a speech model actually
    /// loads. Recovery removes only ITS stage's entry: another still-true
    /// condition keeps the surface. Because the entry is removed, a genuine
    /// fault AFTER a recovery is a fresh condition and correctly grabs the
    /// pill again.
    /// </summary>
    public void NotifyConditionRecovered(ErrorStage stage) => _ui.Post(() =>
    {
        if (!_activeConditions.Remove(stage)) return;
        Raise(nameof(ActiveConditionStage)); Raise(nameof(ActiveConditionMessage)); Raise(nameof(HasActiveCondition));
        // Release the pill only when NO condition remains - a remaining
        // condition keeps the surface (pill and tray text).
        if (_activeConditions.Count > 0) return;
        if (_pending.HasPending) return;
        if (_stage != SessionStage.Error) return;
        // ...and only when a CONDITION is what is actually on the pill. If a
        // newer EVENT error took it (bumping _presentationGeneration past the
        // condition's stamp), that error owns the pill and has its own
        // self-clear scheduled; clearing it here would hide an unrelated error
        // the user has not seen yet.
        if (_conditionPresentationGeneration != _presentationGeneration) return;
        // RESYNC, never a hard reset - see ReleasePillIfUnchanged for why
        // "Idle / Ready" mid-dictation hides the pill, lies on the tray, and
        // kills the voice meter.
        ResyncPillToEngineState();
    });
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll
```

Expected: `Failed: 0`, including the 11 new condition tests (the original 8
plus the three discriminating tests: condition coexistence, no-pill-regrab
under re-reports, and recovery not wiping a newer unrelated error off the pill).

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
- Modify: `src/Winpepper.App/Tray/TrayIconHost.cs:83-102` (Windows-only; `:82`
  is a blank line and `UpdateFromSession` closes at `:102` — replacing `82-100`
  would orphan a comment fragment and a `}` and the file would no longer parse)
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
`UpdateFromSession` (`:83-102`; `:82` is blank — leave it and everything above
untouched) with:

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
Linux; it is verified by the Windows compile checkpoint below plus the Windows
smoke checklist (Task 9).

- [ ] **Step 4a: Windows compile checkpoint**

```bash
./scripts/winrun "dotnet build winpepper.sln -c Release"
```

(or, if the VM is unavailable, push the branch and require the CI
`windows-build` job green — note the tension with AGENTS.md's "full suite must
pass on a Windows host before pushing", which needs an owner ruling; the VM
path avoids it). Expected: `0 Error(s)`. Do not proceed to the next task until
green.

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
- Produces: `sealed class CaptureRecoveryPolicy` — every member takes a private
  `lock` (three concurrent writer families exist: NAudio capture threads,
  multiple concurrent thread-pool endpoint handlers, and the pipeline thread;
  `Volatile`/`Interlocked` are insufficient because the decisions are compound
  read-modify-writes and `DateTime?` is a multi-word field whose assignment is
  not atomic per ECMA-335 §I.12.6.6) — with
  - `CaptureRecoveryPolicy(TimeSpan? debounce = null, Func<DateTime>? clock = null, TimeSpan? retryDelay = null)`
  - `bool IsFailing { get; }`
  - `void NoteFault()`
  - `bool ShouldRebuild()` — leading-edge debounce; also refills the retry
    budget and bumps the retry ticket (a fresh endpoint event supersedes any
    pending scheduled retry)
  - `void NoteRebuildFailed()`
  - `bool TryScheduleRetry(out TimeSpan delay, out long ticket)` — bounded by
    `MaxScheduledRetries` per endpoint event
  - `bool TryClaimRetry(long ticket)` — single-use; false when superseded by a
    newer event or by recovery
  - `bool NoteFramesObserved()` — returns **true exactly once per failing
    episode**; the ONLY signal that clears the microphone condition
  - `public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);`
    (owner-agreed, unchanged)
  - `public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);`
  - `public const int MaxScheduledRetries = 5;`

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
        policy.NoteRebuildFailed();

        clock.Advance(TimeSpan.FromMilliseconds(501));

        policy.ShouldRebuild().ShouldBeTrue();
    }

    [Fact]
    public void Failed_Rebuild_Keeps_The_Failing_State()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();

        policy.NoteRebuildFailed();

        policy.IsFailing.ShouldBeTrue();
    }

    [Fact]
    public void Frames_From_A_Failing_Stream_Are_The_Recovery_And_Fire_Exactly_Once()
    {
        // "IsRunning right after a rebuild" can lie (NAudio starts the WASAPI
        // pump asynchronously; 0x88890004 arrives ms later). An observed
        // non-empty frame from the live source cannot. Only the FIRST frame of
        // a failing episode clears, so the recovery signal never spams.
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();

        policy.NoteFramesObserved().ShouldBeTrue();

        policy.IsFailing.ShouldBeFalse();
        policy.NoteFramesObserved().ShouldBeFalse();
        policy.NoteFramesObserved().ShouldBeFalse();
    }

    [Fact]
    public void Frames_While_Healthy_Are_Not_A_Recovery()
    {
        // The warm stream delivers frames continuously (~20 Hz); a healthy
        // stream must not spam "recovered".
        var policy = NewPolicy(new FakeClock());

        policy.NoteFramesObserved().ShouldBeFalse();
        policy.NoteFramesObserved().ShouldBeFalse();
    }

    [Fact]
    public void A_New_Fault_After_Recovery_Arms_The_Next_Recovery()
    {
        var clock = new FakeClock();
        var policy = NewPolicy(clock);
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteFramesObserved().ShouldBeTrue();

        policy.NoteFault();
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.ShouldRebuild().ShouldBeTrue();

        policy.NoteFramesObserved().ShouldBeTrue();
    }

    [Fact]
    public void Failed_Rebuild_Arms_A_One_Shot_Retry()
    {
        // A resume's notification burst can END before the endpoint is usable
        // (a default-device change is documented as exactly three back-to-back
        // calls, one per role). With no trailing action, recovery would stall
        // forever - the incident's exact symptom.
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildFailed();

        policy.TryScheduleRetry(out var delay, out var ticket).ShouldBeTrue();

        delay.ShouldBe(CaptureRecoveryPolicy.DefaultRetryDelay);
        policy.TryClaimRetry(ticket).ShouldBeTrue();
    }

    [Fact]
    public void A_Claimed_Retry_Is_Single_Use()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildFailed();
        policy.TryScheduleRetry(out _, out var ticket).ShouldBeTrue();

        policy.TryClaimRetry(ticket).ShouldBeTrue();

        policy.TryClaimRetry(ticket).ShouldBeFalse(); // a duplicate timer strands
    }

    [Fact]
    public void Recovery_Strands_A_Pending_Retry()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildFailed();
        policy.TryScheduleRetry(out _, out var ticket).ShouldBeTrue();

        policy.NoteFramesObserved().ShouldBeTrue(); // capture came back on its own

        policy.TryClaimRetry(ticket).ShouldBeFalse(); // a stale timer must not rebuild a healthy stream
    }

    [Fact]
    public void A_Fresh_Endpoint_Event_Supersedes_A_Pending_Retry_And_Refills_The_Budget()
    {
        var clock = new FakeClock();
        var policy = NewPolicy(clock);
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildFailed();
        policy.TryScheduleRetry(out _, out var staleTicket).ShouldBeTrue();

        clock.Advance(TimeSpan.FromMilliseconds(501));
        policy.ShouldRebuild().ShouldBeTrue();  // a fresh event drives its own rebuild...

        policy.TryClaimRetry(staleTicket).ShouldBeFalse(); // ...and strands the older timer
        policy.NoteRebuildFailed();
        // The budget was refilled by the fresh event: retries are bounded
        // per-event, not once per app lifetime.
        for (var i = 0; i < CaptureRecoveryPolicy.MaxScheduledRetries; i++)
            policy.TryScheduleRetry(out _, out _).ShouldBeTrue();
    }

    [Fact]
    public void The_Retry_Budget_Is_Bounded()
    {
        var policy = NewPolicy(new FakeClock());
        policy.NoteFault();
        policy.ShouldRebuild().ShouldBeTrue();
        policy.NoteRebuildFailed();

        for (var i = 0; i < CaptureRecoveryPolicy.MaxScheduledRetries; i++)
            policy.TryScheduleRetry(out _, out _).ShouldBeTrue();

        policy.TryScheduleRetry(out _, out _).ShouldBeFalse(); // spent: wait for the next device event
    }

    [Fact]
    public void No_Retry_Is_Scheduled_While_Healthy()
    {
        var policy = NewPolicy(new FakeClock());

        policy.TryScheduleRetry(out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Constants_Are_The_Owner_Agreed_Values()
    {
        CaptureRecoveryPolicy.DefaultDebounce.ShouldBe(TimeSpan.FromMilliseconds(500));
        CaptureRecoveryPolicy.DefaultRetryDelay.ShouldBe(TimeSpan.FromSeconds(2));
        CaptureRecoveryPolicy.MaxScheduledRetries.ShouldBe(5);
    }
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
/// Four decisions live here so they can be unit-tested on Linux while the COM
/// notification client stays a thin Windows shell:
///
///  * IS a retry warranted at all (<see cref="IsFailing"/>)? A healthy warm
///    stream is left alone; the session-start default-device drift check
///    already follows the default endpoint for the "changed the default, then
///    dictated" case.
///  * SHOULD this device event drive a rebuild now (<see cref="ShouldRebuild"/>)?
///    Endpoint notifications arrive in bursts on resume, so only the leading
///    edge of a burst acts.
///  * SHOULD a failed rebuild be retried (<see cref="TryScheduleRetry"/> /
///    <see cref="TryClaimRetry"/>)? A default-device change is documented as
///    exactly THREE back-to-back notifications (one per role) - trivially
///    inside one debounce window - and nothing is documented after the burst
///    settles, so leading-edge-only with no trailing action can stall forever
///    (the incident's exact symptom; OBS retries device-invalidated on a
///    timer, Chromium and cubeb bypass their debounce on device change). The
///    retry is BOUNDED (<see cref="MaxScheduledRetries"/> per endpoint event,
///    refilled by each fresh event) and is NOT a timer-clear or a validity
///    probe: it re-runs the recovery and lets success clear - the taxonomy's
///    own endorsed sentence.
///  * WAS this a recovery (<see cref="NoteFramesObserved"/>)? Clearing is
///    FRAMES-driven, NOT IsRunning-driven: NAudio 2.2.1's
///    WasapiCapture.StartRecording returns after InitializeCaptureDevice()
///    and starts IAudioClient on the capture thread LATER, so "IsRunning right
///    after a rebuild" proves only that Initialize succeeded - the stream can
///    fault milliseconds later (0x88890004, the incident's signature) or stay
///    "Capturing" delivering nothing. The first observed NON-EMPTY frame from
///    the live source cannot lie, and it is the ONLY signal that clears the
///    microphone CONDITION.
///
/// EVERY member takes the private lock: the writers are NAudio capture
/// threads, MULTIPLE CONCURRENT thread-pool endpoint handlers (the COM
/// callbacks are de-serialized by the marshaling hand-off), and the pipeline
/// thread. Volatile/Interlocked are insufficient - the decisions are compound
/// read-modify-writes and DateTime? is a multi-word field whose assignment is
/// not atomic (ECMA-335 §I.12.6.6). All operations are O(1); the per-frame
/// call is an uncontended lock acquisition.
/// </summary>
public sealed class CaptureRecoveryPolicy
{
    /// <summary>Endpoint notifications burst on resume; act on the leading
    /// edge only. (Owner-agreed, unchanged: 500 ms.)</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>Delay before the one-shot retry armed by a failed rebuild.</summary>
    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>Scheduled retries per endpoint event. Each fresh event refills
    /// the budget, so a persistent outage keeps converging while a dead
    /// endpoint cannot spin an unbounded timer chain.</summary>
    public const int MaxScheduledRetries = 5;

    private readonly object _gate = new();
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _retryDelay;
    private readonly Func<DateTime> _clock;
    private DateTime? _lastRebuildUtc;
    private bool _failing;
    private int _retryBudget;
    // Epoch for scheduled retries: ShouldRebuild, NoteFramesObserved and a
    // successful TryClaimRetry all bump it, so any timer holding an older
    // ticket strands (single-use, superseded-by-newer-event, and
    // stranded-by-recovery all fall out of this one counter).
    private long _retryTicket;

    public CaptureRecoveryPolicy(TimeSpan? debounce = null, Func<DateTime>? clock = null,
                                 TimeSpan? retryDelay = null)
    {
        _debounce = debounce ?? DefaultDebounce;
        _retryDelay = retryDelay ?? DefaultRetryDelay;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>True while capture is known to be down (fault, or a failed rebuild).</summary>
    public bool IsFailing { get { lock (_gate) return _failing; } }

    /// <summary>Capture faulted or failed to start: arm recovery.</summary>
    public void NoteFault() { lock (_gate) _failing = true; }

    /// <summary>
    /// True when this device event should drive a rebuild now. Records the
    /// attempt time (so a burst of notifications produces exactly one rebuild),
    /// refills the retry budget, and bumps the retry ticket (a fresh endpoint
    /// event supersedes any pending scheduled retry).
    /// </summary>
    public bool ShouldRebuild()
    {
        lock (_gate)
        {
            var now = _clock();
            if (_lastRebuildUtc is { } last && now - last < _debounce) return false;
            _lastRebuildUtc = now;
            _retryBudget = MaxScheduledRetries;
            _retryTicket++;
            return true;
        }
    }

    /// <summary>A (re)build attempt failed: capture is (still) down.</summary>
    public void NoteRebuildFailed() { lock (_gate) _failing = true; }

    /// <summary>
    /// After a failed rebuild: true when a one-shot retry should be scheduled,
    /// handing out the delay and the ticket the timer must later claim.
    /// Bounded by <see cref="MaxScheduledRetries"/> per endpoint event.
    /// </summary>
    public bool TryScheduleRetry(out TimeSpan delay, out long ticket)
    {
        lock (_gate)
        {
            delay = _retryDelay;
            ticket = _retryTicket;
            if (!_failing || _retryBudget <= 0) return false;
            _retryBudget--;
            return true;
        }
    }

    /// <summary>
    /// Called by the timer when it fires: true when the retry may run. Single
    /// use - a successful claim bumps the ticket, so a duplicate timer
    /// strands. False when superseded by a newer endpoint event or when
    /// recovery already happened (a stale timer must never rebuild a healthy
    /// stream). A claimed retry IS a rebuild attempt, so it restarts the
    /// debounce window too.
    /// </summary>
    public bool TryClaimRetry(long ticket)
    {
        lock (_gate)
        {
            if (!_failing) return false;
            if (ticket != _retryTicket) return false;
            _retryTicket++;
            _lastRebuildUtc = _clock();
            return true;
        }
    }

    /// <summary>
    /// A non-empty frame was observed from the LIVE source. Returns true
    /// exactly once per failing episode - THE recovery signal, and the only
    /// thing that clears the microphone condition. Also strands any pending
    /// scheduled retry (there is nothing left to retry). Cheap no-op (false)
    /// on every frame of a healthy stream.
    /// </summary>
    public bool NoteFramesObserved()
    {
        lock (_gate)
        {
            if (!_failing) return false;
            _failing = false;
            _retryTicket++;
            return true;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```

Expected: `Failed: 0`, including the 14 new `CaptureRecoveryPolicyTests` and
the existing 5000-iteration
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
- Modify: `src/Winpepper.Audio/WarmCaptureCoordinator.cs` (ONE additive event —
  Step 1a; nothing else in the file changes)
- Modify: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (whole file replaced)

**Interfaces:**
- Consumes: `CaptureRecoveryPolicy` (Task 5); `WarmCaptureCoordinator.Rebuild()`,
  `.EnsureStarted(bool)`, `.IsRunning`, `.ActiveDeviceId`, `.CaptureFaulted`
  (existing, unchanged).
- Produces:
  - `event Action? WarmCaptureCoordinator.FrameObserved` (Step 1a)
  - `sealed class AudioEndpointWatcher : IMMNotificationClient, IDisposable`
    with `AudioEndpointWatcher(Action onCaptureEndpointChanged, ILogger? log = null)`
  - `event Action? IWarmAudioRecorder.CaptureRecovered`

**Note:** the watcher and recorder are Windows-only (`#if WINDOWS` /
`net9.0-windows10.0.19041.0`), so there is no Linux test for them. All decision
logic they use is covered by Task 5; the COM plumbing is verified by this
task's Windows compile checkpoint plus the Windows smoke checklist (Task 9).
The coordinator edit (Step 1a) IS pure-managed and compiles on `net9.0`; the
Linux verification for this task is that `Winpepper.Audio` and its tests still
build and pass on `net9.0`, including the 5000-iteration concurrency hammer,
which exercises the edited frame-ingest path.

**Implementer note (explicit sign-off wanted):** an endpoint-driven `Rebuild()`
during an ACTIVE session clears the ring (`_buffer.Clear()`,
`WarmCaptureCoordinator.cs:88`) and would destroy live dictation audio. With
frames-driven clearing the false arming that made this likely is gone (a
self-healed stream clears `IsFailing` within ~50 ms), but the session-active
case is still reachable in principle — a guard (skip endpoint-driven rebuilds
while `_buffer.IsSessionActive`) or an explicit owner decision to accept it is
worth recording before this ships.

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

- [ ] **Step 1a: Add the FrameObserved seam to the coordinator (ONE additive event)**

Edit `src/Winpepper.Audio/WarmCaptureCoordinator.cs`. Add the event next to
`FramesAvailable`:

```csharp
/// <summary>Raised for every non-empty frame ingested from the LIVE source
/// (epoch-guarded). Proof that the WASAPI pump is delivering audio end-to-end.</summary>
public event Action? FrameObserved;
```

and raise it inside `OnSourceFrame` (`:150-159`), AFTER the existing
`ReferenceEquals` epoch guard and `_buffer.Ingest`, gated on non-empty — the
method body becomes:

```csharp
        _buffer.Ingest(frame.Span);
        if (!frame.IsEmpty) FrameObserved?.Invoke();
        if (_buffer.IsSessionActive) FramesAvailable?.Invoke(frame);
```

**Both gates are load-bearing:**

- **The NON-EMPTY gate:** NAudio's `ReadNextPacket` raises `DataAvailable` even
  with 0 bytes (`WasapiCapture.cs:311`), so a wedged-but-polling stream would
  otherwise "observe frames" and clear the microphone condition while
  delivering nothing.
- **The EPOCH guard** (already in place at `:156` — the event goes AFTER it): a
  late frame from a swapped-out source must never falsely clear. This is why
  tapping frames in the source factory instead would be strictly worse — the
  factory has no epoch knowledge.

Nothing else in the file changes. In particular do NOT touch
`OnSourceStopped`'s unconditional `CaptureFaulted?.Invoke(ex)` at `:197`
(raised even when its own in-lock retry at `:177` succeeded): it is
load-bearing for observability and pinned by
`Fault_RaisesCaptureFaulted_AndAutoRebuildsWhenPastBackoff`
(`WarmCaptureCoordinatorTests.cs:189`). The 5000-iteration
`ConcurrencyHammer_RebuildVsFrames_NeverThrows` must stay green.

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
/// CONTRACT: IMMNotificationClient callbacks arrive on COM/MTA threads and
/// must never block. Rebuilding capture takes a lock and can dispose a source
/// (which joins a capture thread), so the handler is ALWAYS marshalled onto the
/// thread pool and the callback thread returns immediately. This includes
/// resolving an endpoint's DataFlow (IMMDeviceEnumerator::GetDevice +
/// IMMEndpoint::GetDataFlow are blocking COM round-trips, and the field
/// enumerator's RCW has UI-thread affinity) - NO COM call is made on the
/// callback thread, and no MMDevice is disposed inside one. NOTE: the hand-off
/// DE-serializes the callbacks - several handlers can run concurrently, which
/// is why every recovery decision lives behind CaptureRecoveryPolicy's lock.
/// No decision logic lives here - see <see cref="CaptureRecoveryPolicy"/>.
///
/// LOGGING: every endpoint notification is logged so a Windows smoke run (and
/// the next field incident) yields EVIDENCE, not just pass/fail. Endpoint IDs
/// are opaque GUID strings - never user content - so this respects the
/// content-free logging constraint.
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
        // NAudio's wrapper passes the COM HRESULT through instead of throwing;
        // ignoring it would log "Subscribed..." for a registration that never
        // fires a single callback.
        var hr = _enumerator.RegisterEndpointNotificationCallback(this);
        if (hr != 0)
            _log?.LogWarning("RegisterEndpointNotificationCallback failed: 0x{Hr:X}", hr);
        else
            _log?.LogInformation("Subscribed to audio endpoint notifications");
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // A NULL default-device id is the diagnostic signature of the
        // mid-resume "no default capture endpoint yet" window (the incident).
        // The thread id shows callbacks arrive off the UI thread.
        _log?.LogInformation(
            "Default audio device changed: flow={Flow} role={Role} device={DeviceId} thread={ThreadId}",
            flow, role, defaultDeviceId ?? "<none>", Environment.CurrentManagedThreadId);
        if (flow == DataFlow.Capture) Signal();
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState)
    {
        _log?.LogInformation(
            "Audio device state changed: device={DeviceId} state={State} thread={ThreadId}",
            deviceId, newState, Environment.CurrentManagedThreadId);
        if (newState != DeviceState.Active) return;
        // This callback carries NO DataFlow, so it fires for RENDER endpoints
        // too (Bluetooth headphones, monitor speakers going Active). Signaling
        // on those would drive a spurious rebuild attempt on every unrelated
        // audio device connect/disconnect - so the flow must be resolved.
        //
        // BUT NOT HERE. Resolving it means IMMDeviceEnumerator::GetDevice +
        // IMMEndpoint::GetDataFlow, i.e. blocking COM round-trips, and
        // MS's IMMNotificationClient guidance is explicit that the client must
        // not block in a callback and must not release the last reference to
        // an MMDevice API object inside one. Worse, `_enumerator` is created
        // in the WarmWasapiRecorder constructor, which PipelineHost.TryStartCore
        // (PipelineHost.cs:262-267) runs on the app's STA/UI thread: calling it
        // from this MTA callback thread marshals back through the UI thread's
        // pump, blocking the endpoint notification thread on the very UI thread
        // the Task 9 smoke claims we are immune to. Hand off FIRST; resolve on
        // the thread pool with a thread-local enumerator.
        SignalIfCapture(deviceId);
    }

    public void OnDeviceAdded(string pwstrDeviceId)
        => _log?.LogDebug("Audio device added: device={DeviceId}", pwstrDeviceId);

    public void OnDeviceRemoved(string deviceId)
        => _log?.LogDebug("Audio device removed: device={DeviceId}", deviceId);

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

    /// <summary>
    /// Hand off FIRST, then resolve the endpoint's data flow on the thread
    /// pool - NEVER on the IMMNotificationClient callback thread. Uses a
    /// SHORT-LIVED, locally-created enumerator rather than the field: the
    /// field's RCW was created on the app's STA/UI thread, so calling it from
    /// here would marshal back through that thread's message pump. A local
    /// enumerator created on this MTA pool thread has no such affinity, and
    /// disposing the MMDevice here is outside any COM callback.
    /// </summary>
    private void SignalIfCapture(string deviceId)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDevice(deviceId);
                if (device.DataFlow != DataFlow.Capture) return;
            }
            catch (Exception ex)
            {
                // The device can vanish again mid-churn; content-free. Bail
                // rather than guess - OnDefaultDeviceChanged still covers the
                // incident's actual signature (a capture default reappearing).
                _log?.LogDebug(ex, "could not resolve data flow for a state-changed device");
                return;
            }
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
/// RECOVERY IS PROVEN BY FRAMES, not by IsRunning: NAudio starts the WASAPI
/// pump asynchronously, so "IsRunning right after a rebuild" only proves
/// AudioClient.Initialize succeeded (the stream can fault ms later - the
/// incident's own 0x88890004). Rebuilds are DRIVEN by endpoint events, by a
/// bounded one-shot retry after a failed rebuild, and by the session-start
/// force-start; the recovery SIGNAL fires exactly once per failing episode, on
/// the first observed non-empty frame from the live (epoch-guarded) source.
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
    // Set FIRST in Dispose (Volatile.Write) so a scheduled retry that fires
    // during teardown never touches a disposed coordinator.
    private int _disposed;

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
        // FRAMES-DRIVEN CLEARING: the one signal that cannot lie.
        _coordinator.FrameObserved += OnFrameObserved;
        _coordinator.CaptureFaulted += ex =>
        {
            // Capture is (or may be) down: arm recovery so the next endpoint
            // event actually retries. The coordinator raises this even when
            // its own in-lock retry SUCCEEDED (load-bearing for observability,
            // pinned by an existing test - do NOT change it); in that
            // self-healed case the very next frame clears the false arming
            // within ~50 ms, before any endpoint event could act on it.
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
        // NO recovery signal is raised here: IsRunning right after a start only
        // proves AudioClient.Initialize succeeded (NAudio starts the WASAPI
        // pump asynchronously), so clearing waits for the first non-empty
        // frame - which a genuinely restarted stream delivers within ~50 ms
        // via OnFrameObserved. The session-start seam still DRIVES recovery;
        // it just no longer CLAIMS it.
        _coordinator.EnsureStarted(force: true);
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    /// <summary>
    /// Runs on the coordinator's frame-ingest path for every non-empty frame
    /// of the LIVE (epoch-guarded) source. The FIRST such frame of a failing
    /// episode is the recovery: proof the WASAPI pump is delivering audio
    /// end-to-end, which neither IsRunning nor a validity probe can give.
    /// NoteFramesObserved is a cheap no-op (false) on every frame of a healthy
    /// stream. This is the ONLY place CaptureRecovered is raised.
    /// </summary>
    private void OnFrameObserved()
    {
        if (!_recovery.NoteFramesObserved()) return;
        _log?.LogInformation("Microphone capture recovered (frames observed)");
        CaptureRecovered?.Invoke();
    }

    /// <summary>
    /// A capture endpoint arrived or the default changed. Runs on a thread-pool
    /// thread (never the COM callback thread - see
    /// <see cref="AudioEndpointWatcher"/>); several such handlers can run
    /// CONCURRENTLY, which is why every decision is behind the policy's lock.
    /// Only acts when capture is known to be failing: a healthy warm stream
    /// keeps running, and the existing session-start drift check still follows
    /// the default device.
    /// </summary>
    private void OnCaptureEndpointChanged()
    {
        if (!_recovery.IsFailing) return;
        if (!_recovery.ShouldRebuild())
        {
            // Endpoint events burst on resume: leading edge only.
            _log?.LogDebug("Endpoint event suppressed by the rebuild debounce");
            return;
        }
        AttemptRebuild("device change");
    }

    /// <summary>
    /// One rebuild attempt plus the bounded one-shot retry. The retry is NOT a
    /// validity probe and does NOT clear anything: it re-runs the recovery
    /// (Rebuild) and lets success clear the condition via frames - the plan's
    /// endorsed "retry the recovery and let success clear it". Without it, a
    /// resume whose notification burst ends before the endpoint is usable (a
    /// default-device change is documented as exactly three back-to-back
    /// calls) would stall forever - the incident's exact symptom.
    /// </summary>
    private void AttemptRebuild(string trigger)
    {
        _coordinator.Rebuild();
        if (_coordinator.IsRunning)
        {
            // Success is NOT claimed here: the pump starts asynchronously and
            // can still fault ms from now (0x88890004 - the incident's own
            // signature). The first non-empty frame clears via OnFrameObserved.
            return;
        }

        // The no-endpoint failure leg (0x80070490) IS synchronous, so failure
        // detection here is sound. The exception already reached the ErrorBus
        // via CaptureFaulted; both log lines below are content-free.
        _recovery.NoteRebuildFailed();
        if (_recovery.TryScheduleRetry(out var delay, out var ticket))
        {
            _log?.LogWarning(
                "Microphone rebuild ({Trigger}) did not succeed; one-shot retry in {DelayMs} ms",
                trigger, (int)delay.TotalMilliseconds);
            _ = Task.Delay(delay).ContinueWith(
                _ =>
                {
                    // TryClaimRetry makes the timer single-use and strands it
                    // when a newer event or a recovery superseded it; the
                    // _disposed gate strands it across teardown.
                    if (Volatile.Read(ref _disposed) == 0 && _recovery.TryClaimRetry(ticket))
                        AttemptRebuild("scheduled retry");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _log?.LogWarning(
                "Microphone rebuild ({Trigger}) did not succeed; retry budget spent, waiting for the next device event",
                trigger);
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
        Volatile.Write(ref _disposed, 1);    // strand any scheduled retry FIRST
        _endpointWatcher?.Dispose();         // then stop endpoint callbacks
        _coordinator.Dispose();              // then tear capture down
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
the `net9.0` build; `CaptureRecoveryPolicy`, the edited coordinator, and the
5000-iteration hammer are compiled and green).

- [ ] **Step 4a: Windows compile checkpoint**

```bash
./scripts/winrun "dotnet build winpepper.sln -c Release"
```

(or, if the VM is unavailable, push the branch and require the CI
`windows-build` job green — note the AGENTS.md tension recorded in Global
Constraints; the VM path avoids it). Expected: `0 Error(s)`. This is the first
compile of `WarmWasapiRecorder` and `AudioEndpointWatcher` — do not proceed to
the next task until green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/AudioEndpointWatcher.cs \
        src/Winpepper.Audio/IWarmAudioRecorder.cs \
        src/Winpepper.Audio/WarmCaptureCoordinator.cs \
        src/Winpepper.Audio/WarmWasapiRecorder.cs
git commit -m "feat(audio): rebuild warm capture when a device returns, emit recovery"
```

---

## Task 7: Host Wiring + Session-Start Diagnosability

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (`:42-43` fields,
  `:232-235` model-load success, `:242-244` swap-failure split, `:262-284`
  recorder construction, `:378-382` HoldDown start, `:627-632` Toggle start,
  `:933-939` Dispose)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:462-466` (one-token re-stage
  of the AssemblyAI `onConfigError` report — Step 1a)

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

- [ ] **Step 1a: Re-stage the AssemblyAI config rejection to Models (one token)**

In `src/Winpepper.App/Hosting/AppShell.cs:463`, change `ErrorStage.Asr` to
`ErrorStage.Models` inside the `onConfigError` lambda (`:462-466`), and add the
explanatory comment — the call becomes:

```csharp
            onConfigError: msg => errorBus.Report(
                // Models, NOT Asr: this fires per dictation attempt and the
                // dictation then SUCCEEDS via local fallback, so it is a
                // per-attempt EVENT. At Asr it would classify as a CONDITION
                // whose only clearing seam (local model Load/Swap success)
                // never runs for a cloud user - a permanent tray error while
                // every dictation works. Behavior is otherwise identical:
                // ErrorDeepLink maps Asr and Models both to "models"/"Open
                // Models tab" and ErrorToastPolicy toasts both.
                Winpepper.Core.Errors.ErrorStage.Models,
                new InvalidOperationException(
                    $"AssemblyAI model rejected ({settings.AssemblyAiModel}). Check the model setting. {msg}"),
                Guid.Empty)); // config-level error, not tied to a capture session
```

This needs ZERO classifier change (the classifier's blanket `Asr ⇒ Condition`
stays honest because every remaining Asr site denotes the missing-model state).

- [ ] **Step 1b: Split the swap-failure report on whether a usable session survived**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, replace the `catch` block's
report (`:242-243`) — currently
`if (reportErrors) _errorBus.Report(...ErrorStage.Asr, ex, Guid.Empty);` — with:

```csharp
                        if (reportErrors && _asr is null)   // no usable session at all -> the ongoing condition
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
                        else if (reportErrors)              // kept the old working model -> per-attempt failure
                            _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Models, ex, Guid.Empty);
```

`return _asr is not null; // keep-old-on-failure` (`:244`) stays as-is. When a
swap fails but the old session survives, dictation still works — reporting the
ongoing missing-model CONDITION there would never clear if the user reverts
their selection (no Load/Swap ever runs again).

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
                    // The recorder raises CaptureRecovered only after observing
                    // a non-empty frame from the live source - the one signal
                    // that cannot lie (IsRunning after a rebuild can; a
                    // validity probe can). This is the ONLY thing that clears
                    // the microphone condition.
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
                        // condition is over.
                        _vm.NotifyConditionRecovered(Winpepper.Core.Errors.ErrorStage.Asr);
```

**Honest scope of this clear:** while the pipeline is NOT running, the Models
page's download-then-`TryStart` path reaches this seam (`TryStartCore` calls
`TryEnsureAsrModel`). But while the pipeline IS running, `TryStartCore`
short-circuits on `if (IsRunning) return true;` (`PipelineHost.cs:257`) and
never reaches the load — so a running-pipeline Asr condition clears only at
the NEXT dictation (whose start path loads the model). Do not claim more than
that in comments or docs.

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

- [ ] **Step 5a: Windows compile checkpoint**

```bash
./scripts/winrun "dotnet build winpepper.sln -c Release"
```

(or, if the VM is unavailable, push the branch and require the CI
`windows-build` job green — see the AGENTS.md tension recorded in Global
Constraints). Expected: `0 Error(s)`. This is the first compile of the
`PipelineHost` / `AppShell` edits — do not proceed to the next task until
green.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs \
        src/Winpepper.App/Hosting/AppShell.cs
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
chord / injected-event / capture rule stay byte-identical (sole exception: one
observability-only tick-write at `HookCallback` entry — Step 4f). The hook
thread and its `GetMessageW` loop are the ONLY place the hook handle is created
or destroyed - the reinstall runs there too, so the tracking dictionaries are
never touched from two threads.

**Trigger honesty (read before implementing).** The resume notification is
**necessary but NOT sufficient**. MS Learn names the LL-hook removal trigger as
ANY hook-callback timeout (≥1000 ms cap on Win10 1709+) — suspend/resume is
nowhere mentioned — and removals with no sleep involved are documented in the
field (CPU load, GC pauses — directly relevant to a C# app). Two cases this
task does NOT cover:

1. **Hook death with no resume** (callback timeout under load/GC) — no PBT
   notification ever fires.
2. **Ordering after resume**: `PBT_APMRESUMEAUTOMATIC` is delivered every time
   the system resumes, i.e. BEFORE the user types — so resume → reinstall (of a
   hook that was never removed) → first keypress lands while the process is
   paged out → the callback exceeds 1000 ms → the FRESHLY reinstalled hook is
   removed, with no further trigger. Ordering is not guaranteed.

We KEEP reinstall-on-resume: it heals the incident's most probable case cheaply,
and `RequestHookReinstall` is already safe from any thread. The heartbeat
telemetry (Step 4f) exists so an uncovered death leaves a TIMELINE in the log
("the hook went quiet at T") instead of the total silence the 2026-07-24
incident produced. It is deliberately NOT a health predicate: Win32 exposes no
"time of last KEYBOARD input" (`GetLastInputInfo` is system-wide and counts
mouse), so no verdict can be derived from it. A future automatic reinstall
trigger would still route through the SAME `RequestHookReinstall` path (no new
concurrency model needed), but it needs a genuinely keyboard-specific liveness
source first — out of scope here.

**Known gaps (recorded, not fixed here):** `ReinstallOnHookThread` has NO retry
if `SetWindowsHookExW` fails (one WRN line, then dead until the next resume),
and a `PostThreadMessageW` failure is logged but never retried — one lost post
= one unhealed resume.

**A15 implementer note:** `ResetTrackingState` clears `_captureKeysDown` (which
feeds `isRepeat` and the drain-exit condition in `TryProcessKey:116-178`) and
cancels `_spaceHold`, so the Global Constraint's "byte-identical chord-recording
capture behavior" is NARROWED: a reinstall arriving DURING raw capture / drain
is behavior no existing test pins. Flagged as a candidate discriminating test
(reinstall mid-capture, assert the recording flow survives or restarts
cleanly), not a blocker.

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
/// types mean "the machine just came back" and therefore warrant a keyboard
/// hook reinstall.
///
/// Windows silently removes a WH_KEYBOARD_LL hook whenever its callback times
/// out (>=1000 ms cap on Win10 1709+), and never tells the owner. A
/// suspend/resume is the most PROBABLE occasion (the process is cold/paged
/// out when the first key arrives) - and matches the 2026-07-24 incident:
/// hotkey presses after resume produced ZERO log lines, and an app restart
/// fixed it - but it is NOT the only one, so this trigger is necessary, not
/// sufficient (see the hook heartbeat telemetry).
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
/// MESSAGE-ONLY window does NOT receive the WM_POWERBROADCAST broadcast. (A
/// hidden TOP-LEVEL window DOES receive it - that is the documented fallback
/// if the Task 9 smoke ever falsifies callback delivery in this packaged
/// process.) The hook thread has a message loop but no window, so callback
/// mode is the mechanism that fits it.
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

    // No SetLastError: both APIs return their error code directly (a Win32
    // ULONG checked against ERROR_SUCCESS), so GetLastError is meaningless.
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerRegisterSuspendResumeNotification(
        uint flags,
        ref DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS recipient,
        out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
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

and, for the heartbeat telemetry (Step 4f), add next to the other user32
imports:

```csharp
    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        /// <summary>32-bit tick count of the last system-wide input event.</summary>
        public uint dwTime;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);
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
    // Tick when the resume callback posted the reinstall message: a post that
    // is never followed by execution is the wedged-hook-thread signature, so
    // the dequeue latency is logged (Step 4d).
    private long _reinstallRequestedTick;
    // Heartbeat TELEMETRY (Step 4f): tick of the last time the OS actually
    // called the hook. NOT a trigger - it turns uncovered hook deaths (any
    // >=1000 ms callback timeout, not only sleep/resume) into WRN evidence.
    private long _lastHookCallbackTick = Environment.TickCount64;
    private Timer? _heartbeatTimer;
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
        StartHeartbeat();
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
        Volatile.Write(ref _reinstallRequestedTick, Environment.TickCount64);
        if (_hookThread is null)
        {
            // Never started (unit tests, or before Start): there is no hook to
            // reinstall and no other thread touching tracking state.
            ReinstallOnHookThread();
            return;
        }
        if (!PostThreadMessageW(_hookThreadId, WM_WINPEPPER_REINSTALL_HOOK, IntPtr.Zero, IntPtr.Zero))
            // Known gap (recorded above): a lost post is never retried.
            _log.LogWarning("Failed to post hook reinstall to the hook thread: 0x{Err:X}",
                Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Runs on the hook thread. Resets per-chord tracking, then swaps the hook.
    /// Every branch logs: these lines are what turns the next field incident
    /// into evidence instead of guesswork.
    /// </summary>
    private void ReinstallOnHookThread()
    {
        _log.LogInformation("System resumed; reinstalling keyboard hook");
        // A post that executes late points at a busy-but-alive hook thread; a
        // post that NEVER executes (no line at all) is the wedged-thread
        // signature.
        _log.LogInformation("reinstall executed {Ms} ms after resume callback",
            Environment.TickCount64 - Volatile.Read(ref _reinstallRequestedTick));
        ResetTrackingState();
        if (_hookThread is null || _callback is null) return; // no live hook to reinstall

        if (_hookHandle != IntPtr.Zero)
        {
            // Do NOT discard the result: false means the OS had ALREADY removed
            // the hook (the case we are healing); true means the hook was still
            // installed and this resume-reinstall was precautionary.
            var unhooked = UnhookWindowsHookEx(_hookHandle);
            _log.LogInformation(
                "Stale hook unhook returned {Result} (false = OS had already removed the hook)",
                unhooked);
            _hookHandle = IntPtr.Zero;
        }
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hookHandle == IntPtr.Zero)
            // Known gap (recorded above): no retry here - dead until next resume.
            _log.LogWarning("Keyboard hook reinstall failed: 0x{Err:X}", Marshal.GetLastWin32Error());
        else
            _log.LogInformation("Keyboard hook reinstalled (thread {ThreadId})",
                Environment.CurrentManagedThreadId);
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
                _log.LogInformation("Registered for suspend/resume notifications (handle 0x{Handle:X})",
                    handle.ToInt64());
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
    /// The Debug line lets the smoke distinguish "registered but nothing
    /// delivered" from "resume classified wrong" (raw PBT_* type included).
    /// </summary>
    private uint OnPowerNotification(IntPtr context, uint type, IntPtr setting)
    {
        _log.LogDebug("Power notification: type=0x{Type:X}", type);
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
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _spaceHold.Dispose();
        lock (_captureGate) Volatile.Write(ref _rawCapture, null);
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
```

(4f) Hook heartbeat — TELEMETRY, not a trigger. Two edits:

First, at the very top of `HookCallback` (`:467`), before the existing
`if (nCode != 0) ...` line, add the ONE observability write (this is the sole
`HookCallback` change in the whole plan):

```csharp
        Volatile.Write(ref _lastHookCallbackTick, Environment.TickCount64);
```

Second, add the heartbeat method next to `RegisterPowerNotifications`:

```csharp
    /// <summary>
    /// TELEMETRY ONLY - an unconditional, non-judging evidence line, NOT a
    /// health verdict and NOT a reinstall trigger. Every 30 s it records two
    /// ages: how long since the OS last called our low-level keyboard hook,
    /// and how long since the system last saw ANY user input. Post-incident,
    /// this gives a timeline ("the hook went quiet at T, input continued
    /// past T") that today's logs cannot provide at all.
    ///
    /// WHY IT DOES NOT DECIDE: there is no Win32 API for "time of last
    /// KEYBOARD input". GetLastInputInfo is system-wide and is updated by
    /// MOUSE input too, so "input recent AND hook silent" is satisfied by
    /// ordinary mouse-only use (reading, scrolling) on a perfectly healthy
    /// hook. Emitting a WRN on that conjunction would fire routinely on
    /// healthy systems and drown the content-free log the incident response
    /// depends on. Two ages at DEBUG are honest; a warning would not be.
    ///
    /// Consequently this is NOT the detector for a silently-removed hook -
    /// the Task 9 R14-2 gate is a direct functional check (press the hotkey;
    /// does a dictation start?), and these lines are corroborating timeline
    /// evidence for it. Promoting this to a trigger requires a genuinely
    /// keyboard-specific liveness signal first (e.g. a WM_INPUT raw-input
    /// keyboard sink independent of the hook), which is out of scope here.
    /// </summary>
    private void StartHeartbeat()
    {
        if (!OperatingSystem.IsWindows()) return;
        _heartbeatTimer = new Timer(_ =>
        {
            try
            {
                var sinceCallbackMs = Environment.TickCount64 - Volatile.Read(ref _lastHookCallbackTick);
                long? sinceInputMs = null;
                var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
                if (GetLastInputInfo(ref info))
                {
                    // GetLastInputInfo reports 32-bit ticks; compare in 32-bit space.
                    var delta = unchecked(Environment.TickCount - (int)info.dwTime);
                    if (delta >= 0) sinceInputMs = delta;
                }
                // DEBUG, unconditional, no verdict: system-wide input includes
                // MOUSE, so these two ages diverging is NORMAL, not a fault.
                _log.LogDebug(
                    "Hook heartbeat: lastCallbackAgeMs={CallbackAge} lastAnyInputAgeMs={InputAge} (input age is system-wide and includes mouse)",
                    sinceCallbackMs, sinceInputMs?.ToString() ?? "unknown");
            }
            catch { /* telemetry must never take the app down */ }
        }, null, HeartbeatPeriod, HeartbeatPeriod);
    }

    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(30);
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
`HotkeyHookIntegrationTests`) still passes unchanged. (The heartbeat is
Windows-gated telemetry with no Linux test; its evidence lines are checked in
the Task 9 smoke.)

- [ ] **Step 5a: Windows compile checkpoint**

```bash
./scripts/winrun "dotnet build winpepper.sln -c Release"
```

(or, if the VM is unavailable, push the branch and require the CI
`windows-build` job green — see the AGENTS.md tension recorded in Global
Constraints). Expected: `0 Error(s)`. Do not proceed to Task 9 until green.

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
  # Redirect to a log and check the RUNNER's exit status, then tail. Piping
  # straight into `tail` would make the pipeline's status `tail`'s (always 0)
  # unless `set -o pipefail` is on, so `fail` could never be set and this
  # release gate would pass vacuously. Task 7 Step 5 uses this same shape.
  $DOTNET exec "tests/$proj/bin/Release/net9.0/$proj.dll" >"/tmp/$proj.log" 2>&1 \
    || { echo "TEST FAIL $proj"; fail=1; }
  tail -3 "/tmp/$proj.log"
done
echo "fail=$fail"
```

Expected: `fail=0`; total passing >= the 845 baseline plus the ~61 tests this
plan adds (16 classifier + 7 event-lifecycle + 11 condition-lifecycle + 4 tray +
14 recovery-policy + 9 hook/power; one pre-existing pending-paste test is
rewritten in place, not added); `Failed: 0` everywhere.

- [ ] **Step 2: Build the Windows solution on a Windows host**

```powershell
dotnet build winpepper.sln -c Release
```

Expected: `0 Error(s)`. This is a RE-verification: every Windows-only edit was
already compiled at its owning task's Windows compile checkpoint (Tasks 4, 6,
7, 8 — via `scripts/winrun` or the CI `windows-build` job), so any error here
means drift since those checkpoints — treat it as a blocker.

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

Idle-time condition (the incident) — **the deterministic substitute is
REQUIRED; the sleep path is OPPORTUNISTIC** (the natural mid-resume race is not
reproducible on demand: the Windows sandbox has no microphone, the QEMU VM
disables S3/S4, and CI runners cannot sleep or manipulate endpoints):

- [ ] REQUIRED: as admin, identify the default capture device
      (`Get-PnpDevice -Class AudioEndpoint` / Sound settings) and disable it:
      `Disable-PnpDevice -InstanceId <id> -Confirm:$false`. The pill appears
      with `Error (Audio): ...` and **retires on its own after ~10 s** — it
      does NOT stay on screen.
- [ ] REQUIRED: after the pill retires, the **tray icon is in its error state**
      and its tooltip carries the condition text.
- [ ] REQUIRED: wait 2+ minutes with the device still disabled → the tray
      condition is still there (no timer clears it) and the pill has NOT come
      back — **even if other audio devices (e.g. Bluetooth headphones, monitor
      speakers) connect/disconnect during the wait**.
- [ ] REQUIRED: `Enable-PnpDevice -InstanceId <id> -Confirm:$false` → the log
      contains `Microphone capture recovered (frames observed)`, the tray
      returns to `Winpepper - Ready`, and **no app restart was needed**.
- [ ] REQUIRED: dictate immediately after that → it works.
- [ ] OPPORTUNISTIC (separately, NOT gating): repeat the sequence via a real
      sleep/resume; if the no-endpoint window happens to occur (the endpoint
      log lines show a `<none>` default device), confirm the same transcript.
- [ ] Record WHERE recovery came from (the log shows it): a burst endpoint
      event, a **scheduled retry** (`one-shot retry in ... ms` line preceding
      the recovery), or session start. A scheduled-retry-driven recovery on
      real hardware is direct evidence the burst alone was insufficient (A4).

Hotkey survival:
- [ ] Sleep the machine, resume it → the log contains
      `System resumed; reinstalling keyboard hook`, the
      `reinstall executed ... ms after resume callback` latency line, the
      `Stale hook unhook returned ...` line, and `Keyboard hook reinstalled`
      (with the hook-thread id). Record whether the unhook returned `false`
      (OS had already removed the hook — the incident's mode) or `true`
      (precautionary reinstall).
- [ ] Press the hold hotkey after resume → a dictation starts (log shows
      `Session started (hold)`), proving the hook is live.
- [ ] Hold the hotkey down THROUGH a sleep/resume (press, sleep, resume,
      release) → no phantom dictation starts or hangs; the next fresh press
      works normally.
- [ ] A2 residuals (registration + delivery): the
      `Registered for suspend/resume notifications (handle 0x...)` line is
      present at startup; the Debug `Power notification: type=0x{Type:X}`
      lines show `0x4` (suspend) then `0x12` (resume automatic), and `0x7` on
      a user-initiated wake. Run 3+ sleep/resume cycles with no crash — a
      GC'd callback delegate crashes on the SECOND resume, not the first.
      Cover a Modern Standby machine as well as classic S3 if available.
- [ ] A3 residuals (endpoint delivery): after a resume, the endpoint log lines
      (`Default audio device changed: ...` / `Audio device state changed: ...`)
      appear with NO hotkey pressed; their thread ids are NOT the UI thread;
      events still arrive while the UI thread is busy (e.g. Settings open and
      repainting). Also `Restart-Service audiosrv` while idle → the mic still
      recovers.
- [ ] A1/A14 residuals: R1-1 — grep the 2026-07-24 incident log for
      `"Hotkey hook thread exiting"`; if present, the hook THREAD died and the
      posted-message reinstall design is inert for that incident (escalate to
      the owner). R14-2 — resume under memory pressure and type continuously
      for ~10 s, then verify hotkeys still work 5 minutes later (probes the
      "freshly reinstalled hook removed by the first paged-out keypress"
      ordering gap). THE GATE IS THE DIRECT FUNCTIONAL CHECK: press the
      dictation hotkey and confirm a dictation actually starts. The DEBUG
      `Hook heartbeat: lastCallbackAgeMs=... lastAnyInputAgeMs=...` lines are
      corroborating TIMELINE evidence only — they carry no verdict, because
      the input age is system-wide (mouse included) and cannot by itself
      distinguish a dead keyboard hook from ordinary mouse-only use.

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

**Evidence requirement:** attach the log transcript for each smoke section; a
section passes only if its expected lines appear IN ORDER. "It seemed to work"
without the transcript does not pass the gate.

**Honest classification of what this gate proves:** the natural mid-resume
race and the natural hook-death trigger are *verified by inspection + a
deterministic equivalent + logging, confirmed opportunistically in the field*.
The gate proves the handlers and recovery paths work; it does NOT prove the
exact production race was replayed (the sandbox has no microphone, the QEMU VM
cannot sleep, CI cannot manipulate endpoints). The decision-point log lines
added throughout this plan are what convert every future natural occurrence
into confirming or falsifying evidence.

**CI note:** the new Windows-TFM hook tests may need the same CI filter that
already excludes `HotkeyHookIntegrationTests` (`ci.yml:41`: hosted runners
"may not deliver low-level keyboard events"). If the `windows-build` job fails
only on such delivery-dependent tests, extend the existing
`--filter` expression rather than weakening the tests.

- [ ] **Step 5: No commit**

This task changes no files. If any check fails, fix it in the owning task and
re-run Steps 1-4.

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Covering task |
|---|---|
| CHANGE 1: pure classifier (`ErrorKind` Event/Condition), keyed on stage + the specific failure, explicit + documented per stage | Task 1 (`ErrorClassifier` with the full per-stage doc table; Audio disambiguated via `MicrophoneUnavailableException`; the two non-conforming Asr report sites are re-staged to Models at the site — Task 7 Steps 1a/1b — so the classifier stays honest with zero special-casing) |
| CHANGE 1: Audio capture-fault/device-missing is a Condition | Task 1 (classifier) + Task 7 Step 1 (the report site wraps the exception) |
| CHANGE 1: "Models-missing" / ASR-model-missing is a Condition | Task 1 — carried by the **Asr** stage, which is where the ongoing missing-model state is continuously reported (`PipelineHost.cs:216`, `:243` when `_asr is null`, `:455`, `:698`; `AppShell.cs:380-384`, `:391`) and where a real recovery signal exists (Task 7 Step 3). The Models stage carries only per-attempt failures, now including the re-staged AssemblyAI config rejection and the kept-old-model swap failure. See "Load-Bearing Taxonomy Decision". |
| CHANGE 1: Injection/Cleanup/OcrUia/Learning/History are Events | Task 1 (theory test covers all of them plus Settings/Hotkey/Crash/Unknown, plus the discriminating AssemblyAI-rejection test) |
| CHANGE 1: EVENT reports flip the pill only when a dictation is in flight; keep the `HasPendingPaste` guard; at idle record `LastErrorStage`/`LastErrorMessage` only | Task 2 (`OnBusReport` + `SessionStages.IsDictationInFlight(SessionState)` keyed on the `_engineState` mirror — the presentation stage reads Error while an error shows and cannot answer the question; pending guard preserved verbatim). The ONE pre-existing test that encoded the removed idle behavior is rewritten mid-dictation in Step 4a (exhaustively verified to be the only breaker). |
| CHANGE 1: unit-test all paths | Tasks 1-3 (`ErrorClassifierTests`, `SessionViewModelErrorLifecycleTests`) |
| CHANGE 2: condition shows on the pill immediately | Task 3 (`EnterCondition`) |
| CHANGE 2: pill retires after ~10 s with a generation token guarding against clobbering newer states | Task 3 (`ConditionPillHoldMs`, `ReleasePillIfUnchanged(token)` → `ResyncPillToEngineState()`) + Task 2 (token bumped in the `Stage` setter). Retiring RESYNCS to the engine state, so retiring mid-dictation restores Recording rather than hiding the pill. |
| CHANGE 2: persistent tray error state; tooltip carries the condition text | Task 4 (mapper arm + `TrayIconHost` wiring) |
| CHANGE 2: condition clears everywhere the moment recovery succeeds, never by a timer | Task 3 (`NotifyConditionRecovered`, plus `Condition_Is_Never_Cleared_By_A_Timer`) |
| CHANGE 2: EVENT errors shown mid-dictation self-clear after ~6 s with the same no-clobber guard | Task 2 (`ShowTransientError` + `EventErrorHoldMs`; two no-clobber tests) |
| CHANGE 3: register `RegisterEndpointNotificationCallback` for `OnDefaultDeviceChanged` + `OnDeviceStateChanged` | Task 6 (`AudioEndpointWatcher`; the returned HRESULT is now checked, device-state signals are filtered to CAPTURE-flow endpoints, and every notification is logged content-free) |
| CHANGE 3: debounce ~500 ms, then rebuild via `WarmCaptureCoordinator.Rebuild` | Task 5 (`CaptureRecoveryPolicy.ShouldRebuild`, `DefaultDebounce`) + Task 6 (`OnCaptureEndpointChanged` → `AttemptRebuild`), plus the bounded one-shot retry (2 s, max 5 per endpoint event) that keeps a burst-then-silence resume converging — doctrine-checked: it re-runs the recovery and lets success clear, which is the design's own endorsed sentence |
| CHANGE 3: marshal off the COM/MTA callback thread; never block it | Task 6 (`AudioEndpointWatcher.Signal` → thread pool; documented contract — and the resulting handler concurrency is why the policy is lock-guarded) |
| CHANGE 3: on success emit a recovery signal that clears the Audio condition + log the exact line | Tasks 5/6 (frames-driven: `WarmCaptureCoordinator.FrameObserved` → `NoteFramesObserved()` → `CaptureRecovered` + `"Microphone capture recovered (frames observed)"`; `IsRunning` alone is NOT trusted — NAudio starts the pump asynchronously) + Task 7 Step 2 (host clears the condition) |
| CHANGE 3: on failure the condition stays, content-free WRN, stay subscribed | Task 6 (`AttemptRebuild` failure branch: WRN + one-shot retry or budget-spent WRN; the watcher is never disposed on failure) |
| CHANGE 3: keep the `StartSession` force-start path as the second seam | Task 6 (`StartSession` retains `RebuildIfDefaultChanged()` + `EnsureStarted(force: true)` exactly; the seam still DRIVES recovery — the first non-empty frame of the restarted stream claims it) |
| CHANGE 3: pure decision logic unit-testable; COM subscription stays thin | Task 5 (14 tests) + Task 6 (watcher has zero decision logic) |
| CHANGE 3: do not regress the device-drift check or dispose discipline; keep the 5000-iteration hammer green | Task 6 (`RebuildIfDefaultChanged` unchanged; coordinator unchanged except the ONE additive `FrameObserved` event, with `OnSourceStopped`'s pinned unconditional raise explicitly protected) + Tasks 5/9 (hammer runs in every Audio test run) |
| CHANGE 4: `PowerRegisterSuspendResumeNotification` with `DEVICE_NOTIFY_CALLBACK`; no window reliance | Task 8 (`PowerNotificationNative`, with the corrected caveat: MESSAGE-ONLY windows miss `WM_POWERBROADCAST`, a hidden TOP-LEVEL window is the documented fallback) |
| CHANGE 4: on `PBT_APMRESUMESUSPEND`/`PBT_APMRESUMEAUTOMATIC`, marshal onto the hook thread and reinstall (unhook logging its result + `SetWindowsHookEx`) | Task 8 (`PowerResumeDecision`, `RequestHookReinstall` → `WM_WINPEPPER_REINSTALL_HOOK` → `ReinstallOnHookThread`). The trigger is explicitly documented as necessary-but-NOT-sufficient; the heartbeat telemetry (Step 4f) leaves a DEBUG timeline (last-callback age vs last-any-input age) so an uncovered hook death is reconstructable after the fact instead of leaving no trace. It carries no verdict — `GetLastInputInfo` is system-wide and counts mouse, so it cannot discriminate a dead hook on its own. |
| CHANGE 4: reset per-chord tracking so a half-tracked chord can't fire | Task 8 (`ResetTrackingState`, proven by two discriminating tests; the A15 raw-capture narrowing is recorded with a candidate test) |
| CHANGE 4: exact log lines | Task 8 (`"System resumed; reinstalling keyboard hook"`, `"Keyboard hook reinstalled"` + thread id, the unhook-result line, the dequeue-latency line, the Debug per-notification line, WRN with the Win32 error) |
| CHANGE 4: unregister on dispose | Task 8 Step 4e |
| CHANGE 4: every existing hook behavior byte-identical | Task 8 (`TryProcessKey` untouched; Step 5 requires all pre-existing hotkey tests green) |
| CHANGE 5: one content-free INFO line at each start seam with the session GUID | Task 7 Step 4 |
| Verification: pure tests on Linux via the xUnit v3 in-process runner; full non-Windows suite; INCREMENTAL Windows compile checkpoints (Tasks 4/6/7/8 via `scripts/winrun` or CI `windows-build`); Windows smoke checklist with a REQUIRED deterministic device-disable substitute and evidence transcripts | Every task's test step + the per-task checkpoints + Task 9 |
| Do NOT touch `packaging/`; hook touched only for the CHANGE 4 lifecycle | Global Constraints; no task edits `packaging/` or hook matching logic (sole `HookCallback` change is the one observability tick-write, Task 8 Step 4f) |

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
  `WarmCaptureCoordinator.Rebuild()`. Its production outcome — device
  disabled/re-enabled (deterministic, REQUIRED) or unplug/resume
  (opportunistic), then the device returns and dictation works again *without
  an app restart*, with the `"Microphone capture recovered (frames observed)"`
  log line — is proven by the Task 9 checklist with a log transcript attached.
- The hook reinstall is real `SetWindowsHookEx` on the real hook thread; its
  production outcome (hotkeys work after resume, with the reinstall log lines)
  is proven by the Task 9 checklist.
- **Limitations are explicit, not silent.** (a) The resume notification is a
  necessary-but-NOT-sufficient reinstall trigger (Task 8's "Trigger honesty"
  note); the two uncovered cases are named, and the heartbeat DEBUG telemetry
  leaves a reconstructable timeline for them rather than papering them over —
  it is explicitly NOT a detector (no keyboard-specific liveness API exists),
  so the Task 9 R14-2 gate is a direct functional hotkey check. (b) The natural mid-resume
  no-endpoint race is verified by inspection + the deterministic
  `Disable-PnpDevice` equivalent + decision-point logging, confirmed
  opportunistically in the field — Task 9 records this classification
  honestly. (c) The reinstall-during-raw-capture window (A15) and the
  session-active endpoint-rebuild ring-clear (Task 6 implementer note) are
  recorded gaps awaiting owner sign-off, with candidate tests named.
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
- `SessionStages.IsDictationInFlight` — TWO overloads defined in Task 2:
  the `SessionState` (engine-truth) form used by Task 2's `OnBusReport` via the
  `_engineState` UI-thread mirror (assigned FIRST in the `OnEngineStateChanged`
  posted lambda), and the `SessionStage` (presentation) form used by Task 4's
  mapper. `SessionState` lives in `Winpepper.Core.Sessions` —
  `SessionStages.cs` carries the `using`, and `SessionViewModel.cs` already
  imports it (`:5`).
- `IDelayScheduler.Schedule(TimeSpan, Action)` — defined in Task 2, implemented
  by `SystemDelayScheduler` (Task 2) and `ManualDelayScheduler` (Task 2 tests),
  used in Tasks 2 and 3.
- `SessionViewModel.EventErrorHoldMs` (Task 2) and `ConditionPillHoldMs`
  (Task 3) — both `public const int`, referenced by the tests in their own task.
- `ReleasePillIfUnchanged(int token)`, `ResyncPillToEngineState()` and
  `_presentationGeneration` — defined in Task 2, reused unchanged by Task 3's
  `EnterCondition` / `NotifyConditionRecovered`. Both release paths RESYNC to
  `_engineState` rather than hard-resetting to Idle; Task 3 additionally
  stamps `_conditionPresentationGeneration` so a recovery can only release a
  pill that a CONDITION actually owns.
- `NotifyConditionRecovered(ErrorStage)` — defined in Task 3, called in Task 7
  Steps 2 and 3 with `ErrorStage.Audio` and `ErrorStage.Asr`.
- The Task 3 condition model is a `Dictionary<ErrorStage, string>`; the
  computed `ActiveConditionStage` uses `Keys.Last()` (`System.Linq`, in the
  .NET 9 implicit usings) and insertion order is the "most recent condition"
  order — both properties raise via explicit `Raise(...)` calls in
  `EnterCondition`/`NotifyConditionRecovered`.
- `ActiveConditionMessage` (string, never null; `" | "`-joined when several
  conditions are true) — defined in Task 3, consumed by Task 4's mapper
  parameter `activeConditionMessage` and `TrayIconHost` — signature unchanged,
  so Task 4 needed no rework for the per-stage map.
- `TrayIconStateMapper.Map(SessionStage, string?, bool, string? = null)` — the
  fourth parameter is optional, so the three pre-existing call shapes in
  `TrayIconStateMapperTests` and the single production call site both compile.
- `CaptureRecoveryPolicy.{IsFailing, NoteFault, ShouldRebuild, NoteRebuildFailed,
  TryScheduleRetry(out TimeSpan, out long), TryClaimRetry(long),
  NoteFramesObserved, DefaultDebounce, DefaultRetryDelay, MaxScheduledRetries}`
  — defined in Task 5, used identically in Task 6 (`OnCaptureEndpointChanged`,
  `AttemptRebuild`, `OnFrameObserved`). The Task 5 test helper's positional
  two-argument construction (`new(debounce, clock.Read)`) matches the ctor's
  first two optional parameters; `retryDelay` is third and optional.
- `WarmCaptureCoordinator.FrameObserved` (`event Action?`) — added in Task 6
  Step 1a, subscribed by `WarmWasapiRecorder`'s `OnFrameObserved` in Task 6
  Step 3.
- `IWarmAudioRecorder.CaptureRecovered` (`event Action?`) — declared in Task 6,
  implemented by `WarmWasapiRecorder` in Task 6 (raised only from
  `OnFrameObserved`), subscribed/unsubscribed in Task 7 with a matching
  `Action?` field.
- `PowerResumeDecision.IsResume(uint)` plus the four `PBT_*` constants —
  defined in Task 8, used in Task 8's callback and tests.
- `KeyboardHookNative.WM_WINPEPPER_REINSTALL_HOOK` (`uint`) — added in Task 8,
  used by `PostThreadMessageW(uint, uint, IntPtr, IntPtr)` and compared against
  `MSG.Message` (`uint`), so no cast is needed.
- `HotkeyHook.RequestHookReinstall()` is `public` (used by tests and the power
  callback); `ResetTrackingState()` is `internal` and visible to
  `Winpepper.Platform.Tests` via the existing `InternalsVisibleTo`.
