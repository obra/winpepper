namespace Winpepper.Core.Threading;

/// <summary>
/// Production <see cref="IDelayScheduler"/>: a plain <see cref="Task.Delay"/>
/// continuation on the thread pool. The callback is expected to marshal itself
/// onto the UI thread (the view model posts through <see cref="IUiThread"/>).
/// </summary>
public sealed class SystemDelayScheduler : IDelayScheduler
{
    public void Schedule(TimeSpan delay, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Task.Delay(delay).ContinueWith(
            _ =>
            {
                // A presentation timer must never take the app down.
                try { action(); } catch { /* best-effort */ }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
