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
