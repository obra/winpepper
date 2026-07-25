using System.Linq;
using Winpepper.Core.Threading;

namespace Winpepper.Core.Tests.Threading;

/// <summary>
/// Deterministic <see cref="IDelayScheduler"/> for tests: nothing runs until
/// <see cref="FireAll"/> is called, so timer-driven behavior is exercised
/// without sleeping. Actions scheduled DURING a fire are queued for the next
/// call, so a self-rescheduling bug shows up as a growing pending queue instead
/// of an infinite loop.
/// </summary>
public sealed class ManualDelayScheduler : IDelayScheduler
{
    private readonly List<(TimeSpan Delay, Action Action)> _pending = new();

    public IReadOnlyList<TimeSpan> PendingDelays => _pending.Select(p => p.Delay).ToList();
    public int PendingCount => _pending.Count;

    public void Schedule(TimeSpan delay, Action action) => _pending.Add((delay, action));

    public void FireAll()
    {
        var due = _pending.ToArray();
        _pending.Clear();
        foreach (var (_, action) in due) action();
    }
}
