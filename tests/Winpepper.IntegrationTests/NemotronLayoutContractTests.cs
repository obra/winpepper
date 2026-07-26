using Winpepper.Asr.TranscribeCpp;
using Winpepper.Models;
using Xunit;

namespace Winpepper.IntegrationTests;

public class NemotronLayoutContractTests
{
    // The registry (download side) and the locator (load side) must agree on
    // the on-disk layout, or install would succeed and the engine still miss it.
    [Fact]
    public void Registry_descriptor_and_locator_agree_on_paths()
    {
        var d = new ModelRegistry().Find(ModelRegistry.StreamingAsrName)!;
        Assert.Equal(NemotronStreamingModel.Name, d.Name);
        var gguf = d.Files.Single(f => f.RelativePath.EndsWith(".gguf"));
        Assert.Equal(
            Path.Combine(d.InstallDirRelative, gguf.RelativePath),
            NemotronStreamingModel.ModelFileRelative);
        var archive = d.Files.Single(f => f.ExtractToRelative is not null);
        // Locator's runtime dir = model dir + ExtractToRelative + the tarball's top-level dir
        Assert.StartsWith(
            Path.Combine(d.InstallDirRelative, archive.ExtractToRelative!),
            NemotronStreamingModel.RuntimeDirRelative);
    }
}
