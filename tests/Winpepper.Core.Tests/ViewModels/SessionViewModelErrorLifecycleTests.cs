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
    public void A_Stale_SelfClear_Timer_Does_Not_Release_A_Newer_Error()
    {
        // DISCRIMINATING for the generation token: BOTH presentations here are
        // Stage == Error, so the `_stage != SessionStage.Error` guard cannot
        // tell the stale timer from the fresh one - only the token can. The
        // neighbouring tests use FireAll(), which fires both timers together
        // and can never leave a stale timer racing a newer error still inside
        // its own hold window.
        var (vm, engine, bus, delays) = NewVm();
        StartDictation(engine);

        bus.Report(ErrorStage.Cleanup, new InvalidOperationException("older"), Guid.NewGuid()); // timer A
        vm.StatusText.ShouldBe("Error (Cleanup): older");

        bus.Report(ErrorStage.Injection, new InvalidOperationException("newer"), Guid.NewGuid()); // timer B
        vm.StatusText.ShouldBe("Error (Injection): newer");
        delays.PendingCount.ShouldBe(2);

        delays.FireNext(); // fire ONLY the stale timer A; timer B stays pending

        // The newer error is still inside its own hold window: the stale timer
        // must not release the pill back to the engine state (Recording).
        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error (Injection): newer");
        delays.PendingCount.ShouldBe(1);
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

    [Fact]
    public void Recovery_Arriving_Before_Its_Condition_Does_Not_Strand_The_Condition()
    {
        var (vm, _, bus, delays) = NewVm();

        // The capture thread self-healed and its first frame consumed the
        // one-shot recovery signal BEFORE the fault report reached the VM
        // (Task 6 documents the window). Clearing a condition that is not
        // there must be a SILENT no-op that leaves no residue - no stage
        // change, no scheduled delay, nothing that a later condition inherits.
        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
        delays.PendingCount.ShouldBe(0);

        // ...and only THEN does the fault report land and enter the condition.
        ReportMicCondition(bus);
        vm.HasActiveCondition.ShouldBeTrue();
        vm.Stage.ShouldBe(SessionStage.Error);

        // Task 6's recorder re-asserts the recovery once the report has been
        // enqueued, precisely so this condition cannot outlive the fault.
        // Without a working repeat clear, the tray would carry "microphone
        // unavailable" forever on a healthy microphone.
        vm.NotifyConditionRecovered(ErrorStage.Audio);

        vm.HasActiveCondition.ShouldBeFalse();
        vm.ActiveConditionStage.ShouldBeNull();
        vm.ActiveConditionMessage.ShouldBe("");

        // Idempotent: a duplicate recovery (both the frame path and the
        // reconcile path can fire) is harmless.
        vm.NotifyConditionRecovered(ErrorStage.Audio);
        vm.HasActiveCondition.ShouldBeFalse();
    }
}
