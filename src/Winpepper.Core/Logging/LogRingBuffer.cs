namespace Winpepper.Core.Logging;

/// <summary>
/// Bounded FIFO ring of rendered log lines. Spec §7.3: live tail of the last
/// 2000 lines. Thread-safe (the Serilog sink may write from any thread).
/// </summary>
public sealed class LogRingBuffer
{
    private readonly int _capacity;
    private readonly Queue<LogTailEntry> _q;
    private readonly object _gate = new();

    public event Action<LogTailEntry>? Appended;

    public LogRingBuffer(int capacity = 2000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _q = new Queue<LogTailEntry>(capacity);
    }

    public int Capacity => _capacity;

    public void Append(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_q.Count >= _capacity) _q.Dequeue();
            _q.Enqueue(entry);
        }
        Appended?.Invoke(entry);
    }

    public IReadOnlyList<LogTailEntry> Snapshot()
    {
        lock (_gate) return _q.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _q.Clear();
    }
}
