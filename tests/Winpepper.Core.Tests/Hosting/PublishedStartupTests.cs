using Shouldly;
using Winpepper.Core.Hosting;
using Xunit;

namespace Winpepper.Core.Tests.Hosting;

public sealed class PublishedStartupTests
{
    [Fact]
    public async Task RunAsync_PublishesInstanceBeforeStartingInteractiveWork()
    {
        var shell = new object();
        object? published = null;
        var publishedAtStartup = false;
        var releaseStartup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var startup = PublishedStartup.RunAsync(
            shell,
            instance => published = instance,
            async instance =>
            {
                publishedAtStartup = ReferenceEquals(published, instance);
                await releaseStartup.Task;
            });

        published.ShouldBeSameAs(shell);
        publishedAtStartup.ShouldBeTrue();
        startup.IsCompleted.ShouldBeFalse();

        releaseStartup.TrySetResult();
        await startup;
    }
}
