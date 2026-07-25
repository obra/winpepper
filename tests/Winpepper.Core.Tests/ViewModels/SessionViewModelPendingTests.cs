using System;
using Shouldly;
using Winpepper.Core.Errors;
using Winpepper.Core.Pending;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelPendingTests
{
    private static (SessionViewModel vm, SessionEngine engine) NewVm()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        return (vm, engine);
    }

    private static InjectionTarget T(long hwnd, string id) =>
        new() { WindowHandle = hwnd, ElementId = id };

    [Fact]
    public void EnterPendingPaste_HoldsTextAndShowsPendingStage()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("deferred text", T(1, "a"));
        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("deferred text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void EngineIdle_WhilePending_KeepsPendingStage()
    {
        // Drive the engine to Injecting, enter pending, then complete injection.
        var (vm, engine) = NewVm();
        engine.Apply(SessionEvent.StartRequested);   // Recording
        engine.Apply(SessionEvent.StopRequested);    // Transcribing
        engine.Apply(SessionEvent.TranscriptReady);  // Injecting
        vm.EnterPendingPaste("hold me", T(1, "a"));
        engine.Apply(SessionEvent.InjectionCompleted); // -> Idle: must NOT hide

        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.HasPendingPaste.ShouldBeTrue();
    }

    [Fact]
    public void NewDictation_DiscardsPending()
    {
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("stale", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested); // Recording

        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Recording);
    }

    [Fact]
    public void ErrorReport_WhilePending_KeepsPendingClickable()
    {
        var (vm, _) = NewVm();
        var bus = new ErrorBus();
        vm.AttachErrorBus(bus);
        vm.EnterPendingPaste("retry me", T(1, "a"));

        bus.Report(ErrorStage.Injection, new InvalidOperationException("SendInput refused"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.PendingPaste);   // did NOT flip to Error
        vm.StatusText.ShouldBe("Click to paste");
        vm.HasPendingPaste.ShouldBeTrue();
        vm.LastErrorMessage.ShouldBe("SendInput refused"); // still recorded for diagnostics
    }

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

    [Fact]
    public void NotifyPasteAttempted_Success_ClearsPendingAndReturnsIdle()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("place me", T(1, "a"));

        var consumed = vm.NotifyPasteAttempted(injected: true);

        consumed.ShouldBeTrue();
        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Fact]
    public void NotifyPasteAttempted_Failure_KeepsPending()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("keep me", T(1, "a"));

        var consumed = vm.NotifyPasteAttempted(injected: false);

        consumed.ShouldBeFalse();
        vm.HasPendingPaste.ShouldBeTrue();
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
    }
}
