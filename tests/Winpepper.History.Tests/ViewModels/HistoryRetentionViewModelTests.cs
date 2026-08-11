using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.History.ViewModels;
using Xunit;

namespace Winpepper.History.Tests.ViewModels;

public sealed class HistoryRetentionViewModelTests : IDisposable
{
    private readonly string _root;

    public HistoryRetentionViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmretention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MaxEntries_PublishesAndQueuesSynchronously_ThenPrunesAfterFlush()
    {
        var wavs = SeedEntries(5);
        var initial = new AppSettings();
        var slot = PublishedHistoryRetentionSlot.FromSettings(initial);
        var writer = new GateableWriter(initial);
        var vm = new HistoryRetentionViewModel(new HistoryStore(_root), writer, slot);
        var appliedCount = 0;
        var applied = WaitForEventsAsync(vm, 1, () => appliedCount++);

        vm.MaxEntries = 2;

        writer.QueuedSnapshots.ShouldHaveSingleItem().HistoryMaxEntries.ShouldBe(2);
        slot.Policy.MaxEntries.ShouldBe(2);
        new HistoryStore(_root).Load().Entries.Count.ShouldBe(5);
        appliedCount.ShouldBe(0);

        writer.Complete(0, persisted: true);
        await applied;

        new HistoryStore(_root).Load().Entries.Count.ShouldBe(2);
        wavs.Count(File.Exists).ShouldBe(2);
        vm.LastCommitPersisted.ShouldBeTrue();
        vm.LastApplyHadIndexFailure.ShouldBeFalse();
        appliedCount.ShouldBe(1);
    }

    [Fact]
    public async Task FailedFlush_StillPrunesUsingCommittedPolicy()
    {
        var wavs = SeedEntries(5);
        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);
        var applied = WaitForEventsAsync(vm, 1);

        vm.MaxEntries = 2;
        writer.Complete(0, persisted: false);
        await applied;

        vm.LastCommitPersisted.ShouldBeFalse();
        new HistoryStore(_root).Load().Entries.Count.ShouldBe(2);
        wavs.Count(File.Exists).ShouldBe(2);
    }

    [Fact]
    public async Task Setters_QueueEveryMutationImmediately_WhileFirstFlushIsGated()
    {
        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);
        var applied = WaitForEventsAsync(vm, 3);

        vm.StoreAudioEnabled = false;
        vm.MaxEntries = 42;
        vm.MaxAgeDays = 90;

        writer.QueuedSnapshots.Count.ShouldBe(3);
        writer.Current.HistoryStoreAudioEnabled.ShouldBeFalse();
        writer.Current.HistoryMaxEntries.ShouldBe(42);
        writer.Current.HistoryMaxAgeDays.ShouldBe(90);

        writer.CompleteAll(true, true, true);
        await applied;
    }

    [Fact]
    public async Task BurstAppliesInSetterOrder_AndLastLimitWins()
    {
        SeedEntries(5);
        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);
        var store = new HistoryStore(_root);
        var observedEntryCounts = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var applied = WaitForEventsAsync(vm, 3,
            () => observedEntryCounts.Enqueue(store.Load().Entries.Count));

        vm.MaxEntries = 4;
        vm.MaxEntries = 3;
        vm.MaxEntries = 2;

        writer.Complete(2, persisted: false);
        writer.Complete(1, persisted: true);
        writer.Complete(0, persisted: false);
        await applied;

        observedEntryCounts.ToArray().ShouldBe(new[] { 4, 3, 2 });
        writer.Current.HistoryMaxEntries.ShouldBe(2);
        store.Load().Entries.Count.ShouldBe(2);
        vm.LastCommitPersisted.ShouldBeFalse();
        vm.DiskUsageDisplay.ShouldContain("2 bytes");
    }

    [Fact]
    public async Task KeepForever_PersistsNullUntilDisabled_ThenPersistsNumericDays()
    {
        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var slot = PublishedHistoryRetentionSlot.FromSettings(initial);
        var vm = new HistoryRetentionViewModel(new HistoryStore(_root), writer, slot);
        var applied = WaitForEventsAsync(vm, 3);

        vm.KeepForever = true;
        writer.QueuedSnapshots[0].HistoryMaxAgeDays.ShouldBeNull();
        slot.Policy.MaxAgeDays.ShouldBeNull();

        vm.MaxAgeDays = 90;
        writer.QueuedSnapshots[1].HistoryMaxAgeDays.ShouldBeNull();
        slot.Policy.MaxAgeDays.ShouldBeNull();

        vm.KeepForever = false;
        writer.QueuedSnapshots[2].HistoryMaxAgeDays.ShouldBe(90);
        slot.Policy.MaxAgeDays.ShouldBe(90);

        writer.CompleteAll(true, true, true);
        await applied;
    }

    [Fact]
    public async Task ReopenFromUnlimited_ShowsFallbackDays_AndPersistsThemWhenDisabled()
    {
        var initial = new AppSettings { HistoryMaxAgeDays = null };
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);

        vm.KeepForever.ShouldBeTrue();
        vm.MaxAgeDays.ShouldBe(30);
        var applied = WaitForEventsAsync(vm, 1);

        vm.KeepForever = false;

        writer.QueuedSnapshots.ShouldHaveSingleItem().HistoryMaxAgeDays.ShouldBe(30);
        writer.Complete(0, persisted: true);
        await applied;
    }

    [Fact]
    public async Task NumericSetters_ClampBounds_AndIgnoreNaN()
    {
        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);

        vm.MaxEntries = double.NaN;
        writer.QueuedSnapshots.ShouldBeEmpty();

        var applied = WaitForEventsAsync(vm, 3);
        vm.MaxEntries = 0;
        vm.MaxEntries.ShouldBe(1);
        vm.MaxEntries = 99_999;
        vm.MaxEntries.ShouldBe(10_000);
        vm.MaxAgeDays = 0;
        vm.MaxAgeDays.ShouldBe(1);

        writer.Current.HistoryMaxEntries.ShouldBe(10_000);
        writer.Current.HistoryMaxAgeDays.ShouldBe(1);
        writer.CompleteAll(true, true, true);
        await applied;
    }

    [Fact]
    public void DiskUsageDisplay_IsPopulatedInConstructor_AndRefreshes()
    {
        File.WriteAllBytes(Path.Combine(_root, "one.wav"), new byte[123]);
        var vm = CreateViewModel(new AppSettings(), new GateableWriter(new AppSettings()));

        vm.DiskUsageDisplay.ShouldContain("123 bytes");

        File.WriteAllBytes(Path.Combine(_root, "two.wav"), new byte[7]);
        vm.Refresh();
        vm.DiskUsageDisplay.ShouldContain("130 bytes");
    }

    [Fact]
    public async Task DeleteAllAudioAsync_ReturnsResult_KeepsEntries_RefreshesAndRaisesEvent()
    {
        var wavs = SeedEntries(2);
        var initial = new AppSettings();
        var vm = CreateViewModel(initial, new GateableWriter(initial));
        var appliedCount = 0;
        vm.RetentionApplied += (_, _) => appliedCount++;

        var result = await vm.DeleteAllAudioAsync();

        result.DeletedCount.ShouldBe(2);
        result.FailedCount.ShouldBe(0);
        wavs.ShouldAllBe(path => !File.Exists(path));
        var entries = new HistoryStore(_root).Load().Entries;
        entries.Count.ShouldBe(2);
        entries.ShouldAllBe(entry => entry.WavRelativePath == "");
        vm.DiskUsageDisplay.ShouldContain("0 bytes");
        vm.LastApplyHadIndexFailure.ShouldBeFalse();
        appliedCount.ShouldBe(1);
    }

    [Fact]
    public async Task PruneIndexSaveFailure_IsSurfaced_AndStillRaisesEvent()
    {
        SeedEntries(1);
        Assert.SkipUnless(TryGetUnixMode(_root, out var originalMode),
            "Unix permission semantics are required for this test.");

        var initial = new AppSettings();
        var writer = new GateableWriter(initial);
        var vm = CreateViewModel(initial, writer);
        var appliedCount = 0;
        var applied = WaitForEventsAsync(vm, 1, () => appliedCount++);

        try
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.SkipUnless(!CanCreateFile(_root),
                "The current user can still write in a chmod 500 directory.");

            vm.StoreAudioEnabled = false;
            writer.Complete(0, persisted: true);
            await applied;

            vm.LastApplyHadIndexFailure.ShouldBeTrue();
            appliedCount.ShouldBe(1);
        }
        finally
        {
            File.SetUnixFileMode(_root, originalMode);
        }
    }

    [Fact]
    public async Task StoreAudioOff_TakesEffectBeforeFlush_ForNormalAndSilentDropArchives()
    {
        var initial = new AppSettings();
        var slot = PublishedHistoryRetentionSlot.FromSettings(initial);
        var store = new HistoryStore(_root, () => slot.Policy);
        var writer = new GateableWriter(initial);
        var vm = new HistoryRetentionViewModel(store, writer, slot);
        var archiver = new HistoryArchiver(store, storeAudio: () => slot.StoreAudio);
        var applied = WaitForEventsAsync(vm, 1);

        vm.StoreAudioEnabled = false;
        slot.StoreAudio.ShouldBeFalse();

        var normal = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16_000],
            RawTranscript = "kept as text",
        });
        var silent = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[16_000],
            IsSilentDrop = true,
        });

        normal.ShouldNotBeNull();
        normal!.WavRelativePath.ShouldBe("");
        silent.ShouldBeNull();
        Directory.EnumerateFiles(_root, "*.wav", SearchOption.AllDirectories).ShouldBeEmpty();
        store.Load().Entries.ShouldHaveSingleItem().RawTranscript.ShouldBe("kept as text");

        writer.Complete(0, persisted: true);
        await applied;
    }

    [Fact]
    public async Task ReopenedViewModel_UsesPublishedSlotWhilePersistenceIsPending()
    {
        var staleOnSettings = new AppSettings { HistoryStoreAudioEnabled = true };
        var slot = PublishedHistoryRetentionSlot.FromSettings(staleOnSettings);
        var writer = new GateableWriter(staleOnSettings);
        var store = new HistoryStore(_root);
        var vmA = new HistoryRetentionViewModel(store, writer, slot);

        vmA.StoreAudioEnabled = false;

        var vmB = new HistoryRetentionViewModel(store, writer, slot);
        vmB.StoreAudioEnabled.ShouldBeFalse();

        vmB.MaxEntries = 123;
        slot.StoreAudio.ShouldBeFalse();

        var appliedA = WaitForEventsAsync(vmA, 1);
        var appliedB = WaitForEventsAsync(vmB, 1);
        writer.CompleteAll(true, true);
        await Task.WhenAll(appliedA, appliedB);
    }

    [Fact]
    public async Task PolicyChangeFromSecondViewModel_DoesNotRepublishStaleAudioValue()
    {
        var initial = new AppSettings { HistoryStoreAudioEnabled = true };
        var slot = PublishedHistoryRetentionSlot.FromSettings(initial);
        var writer = new GateableWriter(initial);
        var store = new HistoryStore(_root);
        var vmA = new HistoryRetentionViewModel(store, writer, slot);
        var vmB = new HistoryRetentionViewModel(store, writer, slot);
        var appliedA = WaitForEventsAsync(vmA, 1);
        var appliedB = WaitForEventsAsync(vmB, 1);

        vmA.StoreAudioEnabled = false;
        vmB.MaxEntries = 123;

        try
        {
            var snapshot = slot.GetSnapshot();
            snapshot.StoreAudio.ShouldBeFalse();
            snapshot.Policy.MaxEntries.ShouldBe(123);
        }
        finally
        {
            writer.CompleteAll(true, true);
            await Task.WhenAll(appliedA, appliedB);
        }
    }

    private HistoryRetentionViewModel CreateViewModel(AppSettings initial, GateableWriter writer)
    {
        var slot = PublishedHistoryRetentionSlot.FromSettings(initial);
        return new HistoryRetentionViewModel(new HistoryStore(_root), writer, slot);
    }

    private List<string> SeedEntries(int count)
    {
        var store = new HistoryStore(_root);
        var wavs = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var relative = $"audio/{i}.wav";
            var absolute = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllBytes(absolute, new byte[] { (byte)i });
            wavs.Add(absolute);
            store.Append(new HistoryEntry
            {
                Id = $"entry-{i}",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-i),
                RawTranscript = $"entry {i}",
                WavRelativePath = relative,
            });
        }
        return wavs;
    }

    private static Task WaitForEventsAsync(
        HistoryRetentionViewModel vm,
        int expectedCount,
        Action? onEvent = null)
    {
        var count = 0;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            onEvent?.Invoke();
            if (Interlocked.Increment(ref count) != expectedCount) return;
            vm.RetentionApplied -= handler;
            completion.TrySetResult();
        };
        vm.RetentionApplied += handler;
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static bool TryGetUnixMode(string path, out UnixFileMode mode)
    {
        mode = default;
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            mode = File.GetUnixFileMode(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool CanCreateFile(string directory)
    {
        var path = Path.Combine(directory, $"write-probe-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, "probe");
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed class GateableWriter : ISettingsWriter
    {
        private readonly List<TaskCompletionSource<bool>> _completions = new();

        public GateableWriter(AppSettings initial) => Current = initial;

        public AppSettings Current { get; private set; }
        public List<AppSettings> QueuedSnapshots { get; } = new();

        public void Queue(Func<AppSettings, AppSettings> mutator)
        {
            Current = mutator(Current);
            QueuedSnapshots.Add(Current);
        }

        public Task FlushAsync() => Task.CompletedTask;

        public Task<bool> TryQueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
        {
            Queue(mutator);
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _completions.Add(completion);
            return completion.Task;
        }

        public void Complete(int index, bool persisted)
            => _completions[index].TrySetResult(persisted).ShouldBeTrue();

        public void CompleteAll(params bool[] outcomes)
        {
            outcomes.Length.ShouldBe(_completions.Count);
            for (var i = 0; i < outcomes.Length; i++) Complete(i, outcomes[i]);
        }
    }
}
