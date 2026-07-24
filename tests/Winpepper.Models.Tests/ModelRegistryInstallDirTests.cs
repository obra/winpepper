using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryInstallDirTests
{
    [Fact]
    public void InstallDirFor_DefaultAsr_MatchesLegacyParakeetLeaf()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, ModelRegistry.DefaultAsrName, ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_NullName_FallsBackToDefaultAsr()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, null, ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_UnknownName_FallsBackToDefaultAsr()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, "no-such-model", ModelKind.Asr);

        dir.ShouldBe(Path.Combine(root, "parakeet-tdt-0.6b-v3"));
    }

    [Fact]
    public void InstallDirFor_CleanupDefault_UsesCleanupInstallDirRelative()
    {
        var registry = new ModelRegistry();
        var root = Path.Combine("C:", "root", "models");

        var dir = registry.InstallDirFor(root, ModelRegistry.DefaultCleanupName, ModelKind.Cleanup);

        dir.ShouldBe(Path.Combine(root, "cleanup", "qwen2.5-0.5b-instruct-q4_k_m"));
    }
}
