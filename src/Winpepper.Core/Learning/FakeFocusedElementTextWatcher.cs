namespace Winpepper.Core.Learning;

/// <summary>
/// Test double for <see cref="IFocusedElementTextWatcher"/>. Drives changes by
/// calling <see cref="EmitAsync"/>. Used by <c>PostPasteWatcherTests</c>.
/// </summary>
public sealed class FakeFocusedElementTextWatcher : IFocusedElementTextWatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Func<FocusedElementTextChange, Task>>> _subs = new();

    public IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange)
    {
        ArgumentException.ThrowIfNullOrEmpty(elementId);
        ArgumentNullException.ThrowIfNull(onChange);
        lock (_gate)
        {
            if (!_subs.TryGetValue(elementId, out var list))
                _subs[elementId] = list = new List<Func<FocusedElementTextChange, Task>>();
            list.Add(onChange);
        }
        return new Sub(this, elementId, onChange);
    }

    public async Task EmitAsync(string elementId, string newText)
    {
        Func<FocusedElementTextChange, Task>[] snapshot;
        lock (_gate)
        {
            if (!_subs.TryGetValue(elementId, out var list)) return;
            snapshot = list.ToArray();
        }
        var change = new FocusedElementTextChange(elementId, newText, DateTime.UtcNow);
        foreach (var h in snapshot) await h(change);
    }

    private sealed class Sub : IDisposable
    {
        private readonly FakeFocusedElementTextWatcher _owner;
        private readonly string _id;
        private readonly Func<FocusedElementTextChange, Task> _h;
        public Sub(FakeFocusedElementTextWatcher owner, string id, Func<FocusedElementTextChange, Task> h)
        { _owner = owner; _id = id; _h = h; }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._subs.TryGetValue(_id, out var list)) list.Remove(_h);
            }
        }
    }
}
