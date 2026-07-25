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
    private static ModelCardViewModel MakeCard(Action<Action>? dispatch,
                                               TimeSpan? progressInterval = null,
                                               Func<TimeSpan, Task>? progressDelay = null) =>
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
            dispatch: dispatch,
            progressInterval: progressInterval,
            progressDelay: progressDelay);

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
    public async Task ReportProgress_BurstKeepsPendingUiWorkBounded()
    {
        var dispatcher = new ManualDispatcher();
        var card = MakeCard(dispatcher.Post, TimeSpan.Zero);

        for (var i = 0; i < 10_000; i++)
        {
            card.ReportProgress(new DownloadProgress
            {
                DescriptorName = "m",
                FileRelativePath = "large.bin",
                BytesDownloaded = i,
                TotalBytes = 10_000,
                Phase = DownloadPhase.Downloading,
            });
        }

        dispatcher.PendingCount.ShouldBe(1,
            "a producer burst must be coalesced instead of growing the UI dispatcher queue");

        var drained = card.DrainProgressAsync();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        card.ProgressByFile.Single().BytesDownloaded.ShouldBe(9_999L);
        dispatcher.MaxPendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReportProgress_PreservesResumeLatestAndTerminalPhases()
    {
        var dispatcher = new ManualDispatcher();
        var card = MakeCard(dispatcher.Post, TimeSpan.Zero);
        var observed = new List<DownloadProgress>();
        card.ProgressByFile.CollectionChanged += (_, e) =>
        {
            if (e.NewItems?[0] is DownloadProgress progress) observed.Add(progress);
        };

        card.ReportProgress(Progress("large.bin", 3_000, 10_000, DownloadPhase.Downloading));
        for (var bytes = 3_001; bytes < 10_000; bytes++)
            card.ReportProgress(Progress("large.bin", bytes, 10_000, DownloadPhase.Downloading));
        card.ReportProgress(Progress("large.bin", 10_000, 10_000, DownloadPhase.Verifying));
        card.ReportProgress(Progress("large.bin", 10_000, 10_000, DownloadPhase.Complete));

        var drained = card.DrainProgressAsync();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        observed.Select(p => p.Phase).ShouldBe(new[]
        {
            DownloadPhase.Downloading,
            DownloadPhase.Downloading,
            DownloadPhase.Verifying,
            DownloadPhase.Complete,
        });
        observed[0].BytesDownloaded.ShouldBe(3_000L);
        observed[1].BytesDownloaded.ShouldBe(9_999L);
        observed[^1].PercentComplete.ShouldBe(100.0);
        dispatcher.MaxPendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReportProgress_CoalescesEachFileWithoutDroppingAnotherFilesTerminalState()
    {
        var dispatcher = new ManualDispatcher();
        var card = MakeCard(dispatcher.Post, TimeSpan.Zero);

        card.ReportProgress(Progress("a.bin", 0, 100, DownloadPhase.Downloading));
        card.ReportProgress(Progress("a.bin", 100, 100, DownloadPhase.Complete));
        card.ReportProgress(Progress("b.bin", 40, 200, DownloadPhase.Downloading));
        card.ReportProgress(Progress("b.bin", 200, 200, DownloadPhase.Complete));

        var drained = card.DrainProgressAsync();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        card.ProgressByFile.Count.ShouldBe(2);
        card.ProgressByFile.ShouldAllBe(progress => progress.Phase == DownloadPhase.Complete);
        card.ProgressByFile.Single(p => p.FileRelativePath == "a.bin").BytesDownloaded.ShouldBe(100L);
        card.ProgressByFile.Single(p => p.FileRelativePath == "b.bin").BytesDownloaded.ShouldBe(200L);
        dispatcher.MaxPendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReportProgress_LingersForCadenceBeforeDispatchingAnotherByteUpdate()
    {
        var dispatcher = new ManualDispatcher();
        var delay = new ManualDelay();
        var card = MakeCard(dispatcher.Post, TimeSpan.FromMilliseconds(100), delay.WaitAsync);

        card.ReportProgress(Progress("large.bin", 10, 100, DownloadPhase.Downloading));
        dispatcher.RunNext().ShouldBeTrue();
        await delay.WaitUntilPendingAsync();

        card.ReportProgress(Progress("large.bin", 20, 100, DownloadPhase.Downloading));
        dispatcher.PendingCount.ShouldBe(0,
            "ordinary byte progress must wait for the cadence boundary");

        delay.ReleaseNext();
        await dispatcher.RunUntilAsync(() => dispatcher.PendingCount == 1);
        dispatcher.RunNext().ShouldBeTrue();

        var drained = card.DrainProgressAsync();
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        card.ProgressByFile.Single().BytesDownloaded.ShouldBe(20L);
        dispatcher.MaxPendingCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReportProgress_LateByteReportCannotOvertakeQueuedComplete()
    {
        var dispatcher = new ManualDispatcher();
        var card = MakeCard(dispatcher.Post, TimeSpan.Zero);

        card.ReportProgress(Progress("large.bin", 0, 100, DownloadPhase.Downloading));
        card.ReportProgress(Progress("large.bin", 100, 100, DownloadPhase.Complete));
        var drained = card.DrainProgressAsync();

        dispatcher.RunNext().ShouldBeTrue();
        await dispatcher.RunUntilAsync(() => dispatcher.PendingCount == 1);

        // Complete has been taken by the bridge and is waiting in the UI
        // queue. A stale/concurrent producer report must not create a new run
        // that regresses the visible terminal state.
        card.ReportProgress(Progress("large.bin", 50, 100, DownloadPhase.Downloading));

        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        card.ProgressByFile.Single().Phase.ShouldBe(DownloadPhase.Complete);
        card.ProgressByFile.Single().BytesDownloaded.ShouldBe(100L);
    }

    [Fact]
    public async Task ReportProgress_LateByteReportCannotRegressAppliedCompleteDuringLinger()
    {
        var dispatcher = new ManualDispatcher();
        var delay = new ManualDelay();
        var card = MakeCard(dispatcher.Post, TimeSpan.FromMilliseconds(100), delay.WaitAsync);

        card.ReportProgress(Progress("large.bin", 0, 100, DownloadPhase.Downloading));
        card.ReportProgress(Progress("large.bin", 100, 100, DownloadPhase.Complete));
        var drained = card.DrainProgressAsync();

        dispatcher.RunNext().ShouldBeTrue();
        await delay.WaitUntilPendingAsync();
        delay.ReleaseNext();
        await dispatcher.RunUntilAsync(() => dispatcher.PendingCount == 1);

        dispatcher.RunNext().ShouldBeTrue();
        card.ProgressByFile.Single().Phase.ShouldBe(DownloadPhase.Complete);
        await delay.WaitUntilPendingAsync();

        card.ReportProgress(Progress("large.bin", 50, 100, DownloadPhase.Downloading));
        delay.ReleaseNext();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        card.ProgressByFile.Single().Phase.ShouldBe(DownloadPhase.Complete);
        card.ProgressByFile.Single().BytesDownloaded.ShouldBe(100L);
    }

    [Fact]
    public async Task ReportProgress_RejectedDispatcherFaultStaysStickyAcrossSustainedReports()
    {
        var failure = new InvalidOperationException("dispatcher closed");
        var dispatchAttempts = 0;
        var card = MakeCard(_ =>
        {
            Interlocked.Increment(ref dispatchAttempts);
            throw failure;
        });

        card.ReportProgress(Progress("large.bin"));
        for (var bytes = 2; bytes <= 10_000; bytes++)
            card.ReportProgress(Progress(
                "large.bin", bytes, 10_000, DownloadPhase.Downloading));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => card.DrainProgressAsync());
        ReferenceEquals(ex, failure).ShouldBeTrue(
            "Drain must preserve the first dispatcher failure for the run");
        dispatchAttempts.ShouldBe(1,
            "later chunks must not create more faulted pump cycles");
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
    public async Task ReportProgress_ConcurrentBurstMutatesOnlyInsideBoundedDispatcherWork()
    {
        var dispatcher = new ManualDispatcher();
        var card = MakeCard(dispatcher.Post, TimeSpan.Zero);
        var mutationOutsideDispatcher = false;
        card.ProgressByFile.CollectionChanged += (_, _) =>
            mutationOutsideDispatcher |= !dispatcher.IsExecuting;

        System.Threading.Tasks.Parallel.For(0, 10_000, i =>
            card.ReportProgress(Progress($"f{i % 7}.bin", i, 10_000, DownloadPhase.Downloading)));

        var drained = card.DrainProgressAsync();
        await dispatcher.RunUntilAsync(() => drained.IsCompleted);
        await drained;

        mutationOutsideDispatcher.ShouldBeFalse();
        dispatcher.MaxPendingCount.ShouldBe(1);
        card.ProgressByFile.Count.ShouldBe(7);
    }

    private static DownloadProgress Progress(string file, long bytes, long total, DownloadPhase phase) => new()
    {
        DescriptorName = "m",
        FileRelativePath = file,
        BytesDownloaded = bytes,
        TotalBytes = total,
        Phase = phase,
    };

    private sealed class ManualDispatcher
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _queued = new();
        private int _executing;

        public int MaxPendingCount { get; private set; }
        public bool IsExecuting => Volatile.Read(ref _executing) != 0;
        public int PendingCount
        {
            get { lock (_gate) return _queued.Count; }
        }

        public void Post(Action action)
        {
            lock (_gate)
            {
                _queued.Enqueue(action);
                MaxPendingCount = Math.Max(MaxPendingCount, _queued.Count);
            }
        }

        public async Task RunUntilAsync(Func<bool> done)
        {
            // Wall-clock bound, not attempt bound: 100k Task.Yield iterations
            // burn in ~100 ms, which a busy host (e.g. right after the Windows
            // gate's builds) can exceed before the bridge's thread-pool
            // continuation is ever scheduled (observed 2026-07-25).
            var deadline = Environment.TickCount64 + 30_000;
            while (Environment.TickCount64 < deadline)
            {
                if (done()) return;

                if (!RunNext())
                {
                    await Task.Yield();
                }
            }

            throw new TimeoutException("The progress bridge did not drain through the manual dispatcher.");
        }

        public bool RunNext()
        {
            Action? action = null;
            lock (_gate)
            {
                if (_queued.Count > 0) action = _queued.Dequeue();
            }
            if (action is null) return false;

            Interlocked.Increment(ref _executing);
            try { action(); }
            finally { Interlocked.Decrement(ref _executing); }
            return true;
        }
    }

    private sealed class ManualDelay
    {
        private readonly object _gate = new();
        private readonly Queue<TaskCompletionSource<bool>> _pending = new();

        public Task WaitAsync(TimeSpan duration)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending.Enqueue(completion);
            return completion.Task;
        }

        public async Task WaitUntilPendingAsync()
        {
            // Wall-clock bound for the same reason as
            // ManualDispatcher.RunUntilAsync: attempt-bounded yielding expires
            // in ~100 ms on a busy host before the bridge gets scheduled.
            var deadline = Environment.TickCount64 + 30_000;
            while (Environment.TickCount64 < deadline)
            {
                lock (_gate)
                {
                    if (_pending.Count > 0) return;
                }
                await Task.Yield();
            }
            throw new TimeoutException("The progress bridge did not request its cadence delay.");
        }

        public void ReleaseNext()
        {
            TaskCompletionSource<bool> completion;
            lock (_gate) completion = _pending.Dequeue();
            completion.TrySetResult(true);
        }
    }
}
