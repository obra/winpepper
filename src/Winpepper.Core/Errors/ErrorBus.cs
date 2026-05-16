namespace Winpepper.Core.Errors;

/// <summary>
/// In-process pub/sub for pipeline errors. Spec §9.1. Subscribers receive
/// every report; the in-memory ring keeps the most recent N records so late
/// subscribers (e.g., the Diagnostics page opening for the first time) can
/// hydrate their UI.
/// </summary>
public sealed class ErrorBus
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly LinkedList<ErrorRecord> _recent = new();
    private readonly List<Action<ErrorRecord>> _subscribers = new();

    public ErrorBus(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Report(ErrorStage stage, Exception ex, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var record = new ErrorRecord
        {
            Stage = stage,
            Message = ex.Message,
            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
            StackTrace = ex.StackTrace ?? "",
            TimestampUtc = DateTime.UtcNow,
            SessionId = sessionId,
        };

        Action<ErrorRecord>[] snapshot;
        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > _capacity) _recent.RemoveLast();
            snapshot = _subscribers.ToArray();
        }

        foreach (var s in snapshot)
        {
            try { s(record); }
            catch { /* subscribers must not propagate */ }
        }
    }

    public IReadOnlyList<ErrorRecord> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    public ErrorRecord? MostRecent()
    {
        lock (_gate) return _recent.First?.Value;
    }

    public IDisposable Subscribe(Action<ErrorRecord> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<ErrorRecord> handler)
    {
        lock (_gate) _subscribers.Remove(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ErrorBus _bus;
        private readonly Action<ErrorRecord> _handler;
        public Subscription(ErrorBus bus, Action<ErrorRecord> handler) { _bus = bus; _handler = handler; }
        public void Dispose() => _bus.Unsubscribe(_handler);
    }
}
