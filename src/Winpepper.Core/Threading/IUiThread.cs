namespace Winpepper.Core.Threading;

/// <summary>
/// Abstraction over the WinUI 3 DispatcherQueue so view models can post work
/// to the UI thread without referencing WinUI. The concrete implementation
/// lives in Winpepper.App; unit tests use SynchronousUiThread.
/// </summary>
public interface IUiThread
{
    bool HasThreadAccess { get; }
    void Post(Action action);
}
