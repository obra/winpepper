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
    public void NewDictation_RetainsPending()
    {
        // DELIBERATE PIN REVISION (council 2026-07-28, all 6 lenses:
        // "preserve/append or fail loud -- never silently drop"; supersedes
        // Rule 5 of the 2026-07-21 pending-paste plan, owner-approved). The
        // old trapdoor: pressing the pedal again -- the most natural recovery
        // gesture -- destroyed the very text the park saved.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"));

        engine.Apply(SessionEvent.StartRequested); // Recording

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("saved text");
        vm.Stage.ShouldBe(SessionStage.Recording); // dictation UX unchanged
    }

    [Fact]
    public void ParkSurvivesDictation_EngineIdle_RestoresPendingPillAndCopy()
    {
        // After the retained park's dictation finishes (here: cancelled --
        // CancelRequested drives the engine straight back to Idle), the pill
        // must return to the PENDING presentation with the reason-correct
        // copy, not linger on the last in-flight stage and not auto-hide.
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("saved text", T(1, "a"), PendingPasteReason.ElevatedTarget);
        engine.Apply(SessionEvent.StartRequested);

        engine.Apply(SessionEvent.CancelRequested); // engine -> Idle

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("saved text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Admin window - switch & click");
    }

    [Fact]
    public void SecondPark_Appends_AndOneClickPastesEverything()
    {
        var (vm, engine) = NewVm();
        vm.EnterPendingPaste("first thought.", T(1, "a"));
        engine.Apply(SessionEvent.StartRequested);   // new dictation; park retained
        engine.Apply(SessionEvent.CancelRequested);  // back to Idle for clarity

        vm.EnterPendingPaste("second thought.", T(2, "b")); // this dictation parked too

        vm.PendingPasteText.ShouldBe("first thought. second thought.");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.NotifyPasteAttempted(injected: true).ShouldBeTrue(); // ONE click, everything
        vm.HasPendingPaste.ShouldBeFalse();
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Theory]
    [InlineData(PendingPasteReason.Interrupted)]
    [InlineData(PendingPasteReason.ElevatedTarget)]
    public void PendingCopy_FitsThePillBudget(PendingPasteReason reason)
    {
        // ~32 chars max at FontSize 13 in the fixed 300-DIP pill (ledger A8,
        // docs/plans/2026-07-27-paste-path-hardening.md:144-148): anything
        // longer silently ellipsizes. Guards every current and future copy.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("t", T(1, "a"), reason);

        vm.StatusText.Length.ShouldBeLessThanOrEqualTo(32);
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

    [Fact]
    public void EnterPendingPaste_ElevatedReason_ShowsAdminCopy_AndStaysClickable()
    {
        // Elevated-target park (paste-path-hardening): same PendingPaste
        // stage (pill stays clickable, PillAnimationMap untouched), same
        // full-text slot semantics -- only the copy differs so the user
        // knows WHY nothing was typed and what to do.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("blocked text", T(1, "a"), PendingPasteReason.ElevatedTarget);

        vm.HasPendingPaste.ShouldBeTrue();
        vm.PendingPasteText.ShouldBe("blocked text");
        vm.Stage.ShouldBe(SessionStage.PendingPaste);
        vm.StatusText.ShouldBe("Admin window - switch & click");
    }

    [Fact]
    public void EnterPendingPaste_DefaultReason_KeepsClickToPasteCopy()
    {
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("deferred text", T(1, "a"));

        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void ShowPendingPasteStatus_TogglesCopy_WhilePending()
    {
        // Pill-click retry path: clicking the pill while an admin window is
        // focused flips the copy to the admin message; a later kept-slot
        // outcome that is NOT elevated flips it back.
        var (vm, _) = NewVm();
        vm.EnterPendingPaste("retry me", T(1, "a"));

        vm.ShowPendingPasteStatus(PendingPasteReason.ElevatedTarget);
        vm.StatusText.ShouldBe("Admin window - switch & click");
        vm.Stage.ShouldBe(SessionStage.PendingPaste); // still clickable
        vm.HasPendingPaste.ShouldBeTrue();            // slot untouched

        vm.ShowPendingPasteStatus(PendingPasteReason.Interrupted);
        vm.StatusText.ShouldBe("Click to paste");
    }

    [Fact]
    public void ShowPendingPasteStatus_NoOp_WhenNothingPending()
    {
        var (vm, _) = NewVm();
        vm.ShowPendingPasteStatus(PendingPasteReason.ElevatedTarget);

        vm.StatusText.ShouldNotBe("Admin window - switch & click");
        vm.HasPendingPaste.ShouldBeFalse();
    }
}
