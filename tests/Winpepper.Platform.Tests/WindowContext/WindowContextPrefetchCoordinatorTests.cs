using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextPrefetchCoordinatorTests
{
    // 1a(d) named race case 1: rapid re-dictation (N+1 starts < 2 s after N).
    [Fact]
    public async Task RapidRedictation_PriorPrefetchCancelledAtNextStart_StampsNone_DistinctCts()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                // Dictation N's prefetch: never completes on its own; goes
                // cancelled when its per-dictation token fires.
                var tcs = new TaskCompletionSource<WindowContextResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            }
            return Task.FromResult(WindowContextResult.FromUia("next dictation context"));
        });

        // Dictation N: recording start, then stop -> prefetch launched, still running.
        coordinator.OnRecordingStart();
        var handleN = coordinator.Start(new IntPtr(1));
        handleN.Task.IsCompleted.ShouldBeFalse();

        // Dictation N+1's RECORDING START: per the 1a ruling, N's prefetch is
        // cancelled NOW (live speech wins over a stale context fetch).
        coordinator.OnRecordingStart();

        // Named observable 1: at N+1's recording start, N's prefetch is no
        // longer running (cancellation requested; task reaches completed).
        handleN.CancellationRequested.ShouldBeTrue();
        await Task.WhenAny(handleN.Task, Task.Delay(2000));
        handleN.Task.IsCompleted.ShouldBeTrue();
        handleN.Task.IsCompletedSuccessfully.ShouldBeFalse();

        // Named observable 2: N stamps ctx_src=none when cancelled — an
        // accepted, counted loss (consume-time semantics: the runner saw a
        // completed-but-cancelled task).
        WindowContextStamp.CtxSrc(consumedWindowContext: true, handleN.Task).ShouldBe("none");

        // Named observable 3: N and N+1 hold DISTINCT CancellationTokenSource
        // instances (observed via distinct tokens).
        var handleN1 = coordinator.Start(new IntPtr(2));
        handleN.Token.Equals(handleN1.Token).ShouldBeFalse();
        handleN1.CancellationRequested.ShouldBeFalse();
    }

    // 1a(d) named race case 2: silence-drop-then-dictate.
    [Fact]
    public async Task SilenceDropThenDictate_DroppedPrefetchCancelled_NothingObservableInNextContext()
    {
        var calls = 0;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                var tcs = new TaskCompletionSource<WindowContextResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                // If the dropped dictation's prefetch were EVER allowed to
                // finish, it would produce this marker text.
                ct.Register(() => tcs.TrySetCanceled(ct));
                _ = Task.Delay(5000, CancellationToken.None).ContinueWith(
                    _ => tcs.TrySetResult(WindowContextResult.FromUia("SECRET-FROM-DROPPED")),
                    TaskScheduler.Default);
                return tcs.Task;
            }
            return Task.FromResult(WindowContextResult.FromUia("fresh context"));
        });

        // Dictation D: start, stop -> prefetch launched, then D is dropped as silent.
        coordinator.OnRecordingStart();
        var dropped = coordinator.Start(new IntPtr(1));
        coordinator.CancelAndClear();

        // Named observable 1: the dropped dictation's prefetch was cancelled.
        dropped.CancellationRequested.ShouldBeTrue();
        coordinator.Current.ShouldBeNull();

        // Next dictation: nothing from the dropped prefetch is observable in
        // its context (named observable 2).
        coordinator.OnRecordingStart();
        var next = coordinator.Start(new IntPtr(2));
        var result = await next.Task;
        result.Text.ShouldBe("fresh context");
        result.Text.ShouldNotContain("SECRET-FROM-DROPPED");
    }

    [Fact]
    public void OnRecordingStart_CompletedPriorPrefetch_IsNotCancelled_JustCleared()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.FromUia("done")));
        coordinator.OnRecordingStart();
        var handle = coordinator.Start(new IntPtr(1));
        handle.Task.IsCompletedSuccessfully.ShouldBeTrue();

        coordinator.OnRecordingStart();
        handle.CancellationRequested.ShouldBeFalse(); // finished work is not disturbed
        coordinator.Current.ShouldBeNull();
    }

    [Fact]
    public void CancelAndClear_WithNoPrefetch_IsANoOp()
    {
        var coordinator = new WindowContextPrefetchCoordinator(
            (hwnd, ct) => Task.FromResult(WindowContextResult.Empty));
        coordinator.CancelAndClear();
        coordinator.Current.ShouldBeNull();
    }

    [Fact]
    public void Start_PassesTheHwndCapturedAtRecordingStart()
    {
        IntPtr seen = IntPtr.Zero;
        var coordinator = new WindowContextPrefetchCoordinator((hwnd, ct) =>
        {
            seen = hwnd;
            return Task.FromResult(WindowContextResult.Empty);
        });
        coordinator.Start(new IntPtr(0x1234));
        seen.ShouldBe(new IntPtr(0x1234)); // 1a(b): the start-captured target, not a re-read
    }
}
