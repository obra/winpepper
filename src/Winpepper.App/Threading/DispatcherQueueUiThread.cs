#if WINDOWS
using Microsoft.UI.Dispatching;
using Winpepper.Core.Threading;

namespace Winpepper.App.Threading;

public sealed class DispatcherQueueUiThread : IUiThread
{
    private readonly DispatcherQueue _queue;
    public DispatcherQueueUiThread(DispatcherQueue queue) { _queue = queue; }
    public bool HasThreadAccess => _queue.HasThreadAccess;
    public void Post(Action action) { _queue.TryEnqueue(() => action()); }
}
#endif
