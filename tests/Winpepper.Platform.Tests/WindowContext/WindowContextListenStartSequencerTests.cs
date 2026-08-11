using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextListenStartSequencerTests
{
    // Test 1: launch happens at RecordingStarted (the start arm); stop just
    // hands the book over and clears. A second stop yields null. The spy's
    // invocation count CANNOT grow at stop — there is no stop-time launch
    // path in the sequencer.
    [Fact]
    public void RecordingStarted_WithStartTrue_LaunchesNow_AndRecordingStoppedHandsItOverOnce()
    {
        var spyLog = new List<string>();
        var calls = 0;
        WindowContextPrefetchHandle? firstReturned = null;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            spyLog.Add("start-called");
            return Task.FromResult(WindowContextResult.FromUia("ctx-" + calls));
        });
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        var handle = sequencer.RecordingStarted(startPrefetch: true, new IntPtr(7));
        firstReturned = handle;
        handle.ShouldNotBeNull();
        handle.ShouldBeSameAs(firstReturned);
        calls.ShouldBe(1);                       // spy ran exactly once at start
        spyLog.ShouldContain("start-called");
        spyLog.Count.ShouldBe(1);                // no extra invocations

        var stopped = sequencer.RecordingStopped();
        stopped.ShouldBeSameAs(handle);          // hands THAT handle back, once
        calls.ShouldBe(1);                       // stop does NOT launch

        var secondStop = sequencer.RecordingStopped();
        secondStop.ShouldBeNull();               // book cleared on first stop
        calls.ShouldBe(1);                       // STILL 1 — never re-launches
    }

    // Test 2: false → no launch at all; stop yields null. This is the wiring-
    // level "no OCR when cleanup is disabled" proof: PipelineHost maps
    // ShouldStart(cleanupEnabled: false, ...) → false, and false → no launch
    // lives HERE (the proof that cleanup-false → false lives in Task 1).
    [Fact]
    public void RecordingStarted_WithStartFalse_LaunchesNothing_AndStopGetsNull()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            return Task.FromResult(WindowContextResult.FromUia("ctx"));
        });
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        var handle = sequencer.RecordingStarted(startPrefetch: false, new IntPtr(1));
        handle.ShouldBeNull();
        calls.ShouldBe(0);                       // start-false → no coordinator launch

        sequencer.RecordingStopped().ShouldBeNull();
        calls.ShouldBe(0);
    }

    // Test 3: the 1a ruling is preserved under listen-start timing — driven
    // via the REAL coordinator underneath the sequencer. A COMPLETED first
    // handle is NOT cancelled by the next OnRecordingStart; a never-completing
    // first handle IS cancelled (live speech wins over a stale fetch).
    [Fact]
    public async Task StoppedHandle_CompletedSurvivesNextOnRecordingStart_StillRunningIsCancelled()
    {
        // Phase A: completed first handle survives the next OnRecordingStart.
        var completedCoordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.FromUia("done")));
        var completedSeq = new WindowContextListenStartSequencer(completedCoordinator);
        completedCoordinator.OnRecordingStart();
        var completedHandle = completedSeq.RecordingStarted(true, new IntPtr(1));
        completedSeq.RecordingStopped();   // consume the finished handle
        completedHandle!.Task.IsCompletedSuccessfully.ShouldBeTrue();
        completedCoordinator.OnRecordingStart();
        completedHandle.CancellationRequested.ShouldBeFalse(); // finished work is left alone

        // Phase B: a never-completing first handle IS cancelled by the next
        // OnRecordingStart (the 1a ruling).
        var firstTcs = new TaskCompletionSource<WindowContextResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var blockingCoordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                ct.Register(() => firstTcs.TrySetCanceled(ct));
                return firstTcs.Task;
            }
            return Task.FromResult(WindowContextResult.FromUia("next"));
        });
        var blockingSeq = new WindowContextListenStartSequencer(blockingCoordinator);
        blockingCoordinator.OnRecordingStart();
        var neverCompleting = blockingSeq.RecordingStarted(true, new IntPtr(2));
        neverCompleting!.Task.IsCompleted.ShouldBeFalse();

        blockingCoordinator.OnRecordingStart();    // live speech wins
        neverCompleting.CancellationRequested.ShouldBeTrue();
        await Task.WhenAny(neverCompleting.Task, Task.Delay(2000));
        neverCompleting.Task.IsCompletedSuccessfully.ShouldBeFalse();
    }

    // Test 4: rapid re-dictation shape — two starts, RecordingStopped returns
    // the SECOND handle. The book is overwritten at the second start.
    [Fact]
    public void RecordingStarted_Again_OverwritesTheBook()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            return Task.FromResult(WindowContextResult.FromUia("ctx-" + calls));
        });
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        coordinator.OnRecordingStart();
        var first = sequencer.RecordingStarted(true, new IntPtr(1));
        first.ShouldNotBeNull();
        coordinator.OnRecordingStart();
        var second = sequencer.RecordingStarted(true, new IntPtr(2));
        second.ShouldNotBeNull();
        second.ShouldNotBeSameAs(first);          // second launch overwrote the book

        var stopped = sequencer.RecordingStopped();
        stopped.ShouldBeSameAs(second);           // the SECOND handle is consumed
    }

    // Test 5: Clear() drops the book without consuming. After Clear, stop
    // returns null. (Book hygiene on cancel / silence-drop / teardown.)
    [Fact]
    public void Clear_DropsTheBook()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.FromUia("ctx")));
        var sequencer = new WindowContextListenStartSequencer(coordinator);

        sequencer.RecordingStarted(true, new IntPtr(1));
        sequencer.Clear();
        sequencer.RecordingStopped().ShouldBeNull();
    }
}