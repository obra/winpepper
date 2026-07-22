namespace Winpepper.Core.Hosting;

public static class PublishedStartup
{
    public static Task RunAsync<T>(
        T instance,
        Action<T> publish,
        Func<T, Task> startAsync)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(startAsync);

        publish(instance);
        return startAsync(instance);
    }
}
