using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Models.Tests;

public sealed class DownloadBatchPlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"planner-{Guid.NewGuid():N}");
    public DownloadBatchPlannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Plan_OrdersSpeechFirst_AndDropsUnknownNames()
    {
        var r = new ModelRegistry();
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { ModelRegistry.DefaultCleanupName, ModelRegistry.StreamingAsrName, "nonsense" },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.DefaultCleanupName });
    }

    [Fact]
    public void Plan_SkipsFullyInstalledDescriptors()
    {
        var r = new ModelRegistry();
        var cleanup = r.Find(ModelRegistry.DefaultCleanupName)!;
        // Materialize the cleanup files at their exact sizes so IsFullyInstalled is true.
        foreach (var f in cleanup.Files)
        {
            var path = Path.Combine(_root, cleanup.InstallDirRelative, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            fs.SetLength(f.SizeBytes);
        }
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { ModelRegistry.StreamingAsrName, ModelRegistry.DefaultCleanupName },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(new[] { ModelRegistry.StreamingAsrName });
    }

    [Fact]
    public void Plan_SkipsManualInstallOnly()
    {
        var r = new ModelRegistry();
        var plan = DownloadBatchPlanner.Plan(r, _root,
            new[] { "sotto-cleanup-lfm25-350m-q8_0", ModelRegistry.StreamingAsrName },
            speechModelName: ModelRegistry.StreamingAsrName);
        plan.Select(d => d.Name).ShouldBe(new[] { ModelRegistry.StreamingAsrName });
    }

    [Theory]
    [InlineData(0, 0)]
    public void AggregatePercent_EmptyBatch_Is100(long _, long __)
        => DownloadBatchPlanner.AggregatePercent(Array.Empty<(long, long)>()).ShouldBe(100);

    [Fact]
    public void AggregatePercent_IsByteWeighted_AndClamped()
    {
        DownloadBatchPlanner.AggregatePercent(new[] { (100L, 100L), (300L, 0L) }).ShouldBe(25);
        DownloadBatchPlanner.AggregatePercent(new[] { (100L, 150L) }).ShouldBe(100); // overshoot clamps
    }
}
