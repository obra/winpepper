using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class AppSettingsCleanupModelTests
{
    [Fact]
    public void Defaults_Include_CleanupModelName()
    {
        var s = new AppSettings();
        s.CleanupModelName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void With_RoundTrips()
    {
        var s = new AppSettings() with { CleanupModelName = "custom-model" };
        s.CleanupModelName.ShouldBe("custom-model");
    }
}
