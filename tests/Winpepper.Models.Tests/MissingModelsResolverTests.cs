using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class MissingModelsResolverTests : IDisposable
{
    private readonly string _root;
    public MissingModelsResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static ModelDescriptor Desc(string name, string installDirRelative) => new()
    {
        Name = name,
        Kind = ModelKind.Asr,
        DisplayName = name,
        InstallDirRelative = installDirRelative,
        Files = new[]
        {
            new ModelFile { RelativePath = "f.bin", Url = "https://x", Sha256 = "h", SizeBytes = 5 },
        },
    };

    [Fact]
    public void FindMissing_Returns_All_When_NothingDownloaded()
    {
        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a", "b" });
        missing.Select(m => m.Name).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void FindMissing_Excludes_Installed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        File.WriteAllText(Path.Combine(_root, "a", "f.bin"), "hello");

        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a", "b" });
        missing.Single().Name.ShouldBe("b");
    }

    [Fact]
    public void FindMissing_Never_Returns_ManualInstallOnly_Models_EvenWhenSelectedAndMissing()
    {
        var resolver = new MissingModelsResolver();
        var manual = Desc("manual", "manual") with { ManualInstallOnly = true };
        var registry = new[] { Desc("a", "a"), manual };

        var missing = resolver.FindMissing(registry, _root, new[] { "a", "manual" });

        missing.Select(m => m.Name).ShouldBe(new[] { "a" });
    }

    [Fact]
    public void FindMissing_SottoManualInstall_IsSkipped_WithTheRealRegistry()
    {
        var registry = new ModelRegistry();
        var resolver = new MissingModelsResolver();

        var missing = resolver.FindMissing(
            registry.All, _root, new[] { "sotto-cleanup-lfm25-350m-q8_0" });

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void FindMissing_Only_Considers_NamesInScope()
    {
        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b"), Desc("c", "c") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a" });
        missing.Select(m => m.Name).ShouldBe(new[] { "a" });
    }

    [Fact]
    public void FindMissing_OnboardingScope_ReturnsOnlyUninstalled_Then_Empty()
    {
        var registry = new ModelRegistry();
        var names = new[] { ModelRegistry.DefaultAsrName, ModelRegistry.DefaultCleanupName };
        var resolver = new MissingModelsResolver();

        // Nothing installed yet: both selected models are missing.
        var before = resolver.FindMissing(registry.All, _root, names);
        before.Select(d => d.Name).OrderBy(n => n)
              .ShouldBe(new[] { ModelRegistry.DefaultAsrName, ModelRegistry.DefaultCleanupName }.OrderBy(n => n));

        // Install every file of both descriptors (non-empty content).
        foreach (var d in registry.All.Where(d => names.Contains(d.Name)))
        {
            foreach (var f in d.Files)
            {
                var p = Path.Combine(_root, d.InstallDirRelative, f.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "x");
            }
        }

        // Now nothing is missing -> the onboarding step auto-resolves.
        resolver.FindMissing(registry.All, _root, names).ShouldBeEmpty();
    }
}
