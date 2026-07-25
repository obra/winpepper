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
}
