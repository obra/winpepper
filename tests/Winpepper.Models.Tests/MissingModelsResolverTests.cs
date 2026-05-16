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
    public void FindMissing_Only_Considers_NamesInScope()
    {
        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b"), Desc("c", "c") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a" });
        missing.Select(m => m.Name).ShouldBe(new[] { "a" });
    }
}
