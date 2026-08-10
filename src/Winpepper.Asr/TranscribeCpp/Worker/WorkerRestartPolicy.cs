namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Kill→respawn→retry budget for the worker engine. Replaces the old
/// NemotronEngineHolder latch-forever: a broken runtime still fails loudly
/// per dictation, but a transient wedge (or a fixed install) recovers without
/// an app restart. After N consecutive failures, one attempt is allowed per
/// cooldown window (default 60 s) until a success resets the count.
/// Not thread-safe on its own — WorkerProcessEngine calls it under its RPC lock.
/// </summary>
public sealed class WorkerRestartPolicy
{
    private readonly int _maxConsecutiveFailures;
    private readonly long _cooldownMs;
    private readonly Func<long> _nowMs;
    private int _consecutiveFailures;
    private long _lastFailureMs;

    public WorkerRestartPolicy(int maxConsecutiveFailures = 3, TimeSpan? cooldown = null, Func<long>? nowMs = null)
    {
        _maxConsecutiveFailures = maxConsecutiveFailures;
        _cooldownMs = (long)(cooldown ?? TimeSpan.FromSeconds(60)).TotalMilliseconds;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public bool CanAttempt()
        => _consecutiveFailures < _maxConsecutiveFailures
           || _nowMs() - _lastFailureMs >= _cooldownMs;

    public void NoteFailure()
    {
        _consecutiveFailures++;
        _lastFailureMs = _nowMs();
    }

    public void NoteSuccess() => _consecutiveFailures = 0;
}
