using Shouldly;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public sealed class RerunModelRouterTests
{
    [Theory]
    [InlineData(true,  true,  RerunModelRouter.Route.NemotronBatch)]
    [InlineData(true,  false, RerunModelRouter.Route.NemotronBatch)]
    [InlineData(false, true,  RerunModelRouter.Route.ParakeetSession)]
    [InlineData(false, false, RerunModelRouter.Route.NotInstalled)]
    public void Decide_RoutesByKindThenPresence(bool streaming, bool filesPresent, RerunModelRouter.Route expected)
        => RerunModelRouter.Decide(streaming, filesPresent).ShouldBe(expected);

    [Theory]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-en",    true)]
    [InlineData("nemotron-streaming-en", "nemotron-streaming-multi", false)]
    [InlineData(null,                    "nemotron-streaming-multi", false)]
    public void EngineServes_RequiresExactModelMatch(string? engineModelName, string requested, bool expected)
        => RerunModelRouter.EngineServes(engineModelName, requested).ShouldBe(expected);
}
