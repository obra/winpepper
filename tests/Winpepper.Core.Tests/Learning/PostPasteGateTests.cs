using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class PostPasteGateTests
{
    [Fact]
    public void ShouldWatch_False_When_Learning_Disabled()
    {
        // Everything else is ready, but the user opted out: never watch.
        PostPasteGate.ShouldWatch(learningEnabled: false, injected: true,
            hasWatcher: true, hasCapturer: true, hasText: true).ShouldBeFalse();
    }

    [Fact]
    public void ShouldWatch_True_When_Enabled_And_All_Preconditions_Met()
    {
        PostPasteGate.ShouldWatch(learningEnabled: true, injected: true,
            hasWatcher: true, hasCapturer: true, hasText: true).ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, true, true, true)]   // not injected
    [InlineData(true, false, true, true)]   // no watcher
    [InlineData(true, true, false, true)]   // no capturer
    [InlineData(true, true, true, false)]   // no text
    public void ShouldWatch_False_When_Any_Precondition_Missing(
        bool injected, bool hasWatcher, bool hasCapturer, bool hasText)
    {
        PostPasteGate.ShouldWatch(learningEnabled: true, injected: injected,
            hasWatcher: hasWatcher, hasCapturer: hasCapturer, hasText: hasText).ShouldBeFalse();
    }
}
