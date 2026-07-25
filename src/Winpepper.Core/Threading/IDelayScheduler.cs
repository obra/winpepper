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
