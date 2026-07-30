using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.Transcription;

public class StreamingRouteGuardTests
{
    [Fact]
    public void NoAbandon_StreamingIsAllowed()
    {
        var guard = new StreamingRouteGuard();
        Assert.True(guard.TryClaimStreaming(out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void AbandonedPumpStillWedged_RoutesToBatch_WithReason()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);

        Assert.False(guard.TryClaimStreaming(out var reason));
        Assert.NotNull(reason);
        Assert.Contains("drain timeout", reason);
    }

    [Fact]
    public void AbandonedPumpCompleted_StreamingResumes_AndStaysResumed()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);
        wedged.SetResult(); // the wedged native call finally returned

        Assert.True(guard.TryClaimStreaming(out _));
        Assert.True(guard.TryClaimStreaming(out _)); // cleared permanently
    }

    [Fact]
    public void AbandonedPumpFaulted_CountsAsCompleted_StreamingResumes()
    {
        var guard = new StreamingRouteGuard();
        var wedged = new TaskCompletionSource();
        guard.NoteAbandoned(wedged.Task);
        wedged.SetException(new InvalidOperationException("pump error"));

        Assert.True(guard.TryClaimStreaming(out _)); // the call RETURNED; gate is releasable
    }

    [Fact]
    public void SecondAbandon_LatestWedgeWins()
    {
        var guard = new StreamingRouteGuard();
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        guard.NoteAbandoned(first.Task);
        guard.NoteAbandoned(second.Task);
        first.SetResult();

        Assert.False(guard.TryClaimStreaming(out _)); // still blocked on the latest wedge
        second.SetResult();
        Assert.True(guard.TryClaimStreaming(out _));
    }

    // E1 coverage-gap fix: cancel/silence-drop/teardown call DisposeAsync
    // WITHOUT FinishAsync, so DrainTimedOut stays false while a wedged
    // gate-holding pump is orphaned — the abandon must key off
    // PumpCompletion-incomplete, not DrainTimedOut alone.
    [Fact]
    public void DisposeOutcome_PumpIncompleteWithoutDrainTimeout_RoutesToBatch()
    {
        var guard = new StreamingRouteGuard();
        var orphaned = new TaskCompletionSource();
        guard.NoteDisposeOutcome(drainTimedOut: false, orphaned.Task); // the cancel-path orphan

        Assert.False(guard.TryClaimStreaming(out var reason));
        Assert.NotNull(reason);
        orphaned.SetResult(); // the wedged call finally returned
        Assert.True(guard.TryClaimStreaming(out _));
    }

    [Fact]
    public void DisposeOutcome_PumpCompleteWithoutDrainTimeout_DoesNotBlock()
    {
        var guard = new StreamingRouteGuard();
        guard.NoteDisposeOutcome(drainTimedOut: false, Task.CompletedTask); // healthy dispose

        Assert.True(guard.TryClaimStreaming(out _));
    }
}
