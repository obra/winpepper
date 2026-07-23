using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CloudProviderTests
{
    [Theory]
    [InlineData("assemblyai/universal-2", true)]
    [InlineData("AssemblyAI/universal-3-pro", true)]
    [InlineData("parakeet-tdt-0.6b-v3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCloud_DetectsAssemblyAiPrefix(string? name, bool expected)
        => CloudProvider.IsCloud(name!).ShouldBe(expected);
}
