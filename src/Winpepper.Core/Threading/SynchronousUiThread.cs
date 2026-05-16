namespace Winpepper.Core.Threading;

public sealed class SynchronousUiThread : IUiThread
{
    public bool HasThreadAccess => true;
    public void Post(Action action) => action();
}
