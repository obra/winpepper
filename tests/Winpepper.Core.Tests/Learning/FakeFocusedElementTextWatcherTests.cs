using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class FakeFocusedElementTextWatcherTests
{
    [Fact]
    public async Task Emits_Changes_To_Subscriber_Until_Disposed()
    {
        var fake = new FakeFocusedElementTextWatcher();
        var received = new List<string>();
        using var sub = fake.Subscribe("element-id-1", c => { received.Add(c.NewText); return Task.CompletedTask; });

        await fake.EmitAsync("element-id-1", "step 1");
        await fake.EmitAsync("element-id-1", "step 2");

        received.ShouldBe(new[] { "step 1", "step 2" });
    }

    [Fact]
    public async Task Subscriptions_Are_Scoped_To_Element_Id()
    {
        var fake = new FakeFocusedElementTextWatcher();
        var received = new List<string>();
        using var sub = fake.Subscribe("target", c => { received.Add(c.NewText); return Task.CompletedTask; });

        await fake.EmitAsync("not-target", "noise");
        await fake.EmitAsync("target", "real");

        received.ShouldBe(new[] { "real" });
    }
}
