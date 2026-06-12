using System.Collections.Specialized;
using Shouldly;
using Winpepper.Models;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests;

/// <summary>
/// Regression tests for the Models-tab download crash: ReportProgress used to
/// mutate the XAML-bound ObservableCollection directly on whatever thread the
/// downloader's IProgress callback arrived on (a ThreadPool thread once
/// DownloadMissingAsync resumed off-context), crashing the app with
/// COMException 0x8001010E / "Cannot change ObservableCollection during a
/// CollectionChanged event". All UI-bound mutations must flow through the
/// injected dispatcher.
/// </summary>
public class ModelCardViewModelDispatchTests
{
    private static ModelCardViewModel MakeCard(Action<Action>? dispatch) =>
        new(ModelKind.Asr,
            new[]
            {
                new ModelDescriptor
                {
                    Name = "m", Kind = ModelKind.Asr, DisplayName = "m",
                    InstallDirRelative = "m", Files = Array.Empty<ModelFile>(),
                },
            },
            installRoot: Path.GetTempPath(),
            selectedName: "m",
            promote: _ => { },
            dispatch: dispatch);

    private static DownloadProgress Progress(string file) => new()
    {
        DescriptorName = "m",
        FileRelativePath = file,
        BytesDownloaded = 1,
        TotalBytes = 2,
        Phase = DownloadPhase.Downloading,
    };

    [Fact]
    public void ReportProgress_RoutesMutationThroughDispatcher()
    {
        var queued = new List<Action>();
        var card = MakeCard(a => queued.Add(a));

        card.ReportProgress(Progress("a.bin"));

        // Mutation must not have happened yet - only queued on the dispatcher.
        card.ProgressByFile.ShouldBeEmpty();
        queued.Count.ShouldBe(1);

        queued[0]();
        card.ProgressByFile.Count.ShouldBe(1);
    }

    [Fact]
    public void ReportProgress_WithoutDispatcher_RunsInline()
    {
        var card = MakeCard(dispatch: null);
        card.ReportProgress(Progress("a.bin"));
        card.ProgressByFile.Count.ShouldBe(1);
    }

    [Fact]
    public void RaiseIsSelectedInstalledChanged_RoutesThroughDispatcher()
    {
        var queued = new List<Action>();
        var card = MakeCard(a => queued.Add(a));
        var raised = new List<string?>();
        card.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        card.RaiseIsSelectedInstalledChanged();
        raised.ShouldBeEmpty();

        queued.Single()();
        raised.ShouldContain(nameof(ModelCardViewModel.IsSelectedInstalled));
    }

    [Fact]
    public void ReportProgress_SerializedThroughDispatcher_NeverReenters()
    {
        // Simulate the production wiring: a dispatcher that executes inline
        // but tracks reentrancy, with progress reports arriving from many
        // threads concurrently. The dispatcher contract (UI thread) serializes
        // execution; here we assert mutations only ever happen inside the
        // dispatcher so the host's serialization actually covers them.
        var inDispatch = 0;
        var maxConcurrent = 0;
        var gate = new object();
        var card = MakeCard(a =>
        {
            lock (gate)
            {
                inDispatch++;
                maxConcurrent = Math.Max(maxConcurrent, inDispatch);
                a();
                inDispatch--;
            }
        });

        System.Threading.Tasks.Parallel.For(0, 200, i =>
            card.ReportProgress(Progress($"f{i % 7}.bin")));

        maxConcurrent.ShouldBe(1);
        card.ProgressByFile.Count.ShouldBe(7);
    }
}
