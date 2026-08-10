using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerRestartPolicyTests
{
    [Fact]
    public void FreshPolicy_AllowsAttempts()
    {
        var p = new WorkerRestartPolicy();
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void FailuresBelowBudget_StillAllowAttempts()
    {
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 3, nowMs: () => 0);
        p.NoteFailure();
        p.NoteFailure();
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void BudgetExhausted_BlocksUntilCooldownElapses()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 3, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        p.NoteFailure(); p.NoteFailure(); p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        now = 59_999;
        p.CanAttempt().ShouldBeFalse();
        now = 60_000;
        p.CanAttempt().ShouldBeTrue(); // one attempt per cooldown window
    }

    [Fact]
    public void FailureAfterCooldownAttempt_BlocksAgainForAnotherCooldown()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 1, cooldown: TimeSpan.FromSeconds(60), nowMs: () => now);
        p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        now = 60_000;
        p.CanAttempt().ShouldBeTrue();
        p.NoteFailure(); // the retry failed too
        now = 60_001;
        p.CanAttempt().ShouldBeFalse();
        now = 120_000;
        p.CanAttempt().ShouldBeTrue();
    }

    [Fact]
    public void Success_ResetsTheBudget()
    {
        long now = 0;
        var p = new WorkerRestartPolicy(maxConsecutiveFailures: 2, nowMs: () => now);
        p.NoteFailure(); p.NoteFailure();
        p.CanAttempt().ShouldBeFalse();
        p.NoteSuccess();
        p.CanAttempt().ShouldBeTrue();
        p.NoteFailure();
        p.CanAttempt().ShouldBeTrue(); // count restarted from zero
    }
}
