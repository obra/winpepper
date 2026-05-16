using Shouldly;
using Winpepper.History.ViewModels;
using Xunit;

namespace Winpepper.History.Tests.ViewModels;

public class HistoryListViewModelTests : IDisposable
{
    private readonly string _root;
    public HistoryListViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmlist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Refresh_LoadsEntries_NewestFirst()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "old", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5), RawTranscript = "old" });
        store.Append(new HistoryEntry { Id = "new", CreatedAtUtc = DateTime.UtcNow, RawTranscript = "new" });

        var vm = new HistoryListViewModel(store);
        vm.Refresh();

        vm.Rows.Count.ShouldBe(2);
        vm.Rows[0].Entry.Id.ShouldBe("new");
        vm.Rows[1].Entry.Id.ShouldBe("old");
    }

    [Fact]
    public void Refresh_FiresPropertyChanged_ForRows()
    {
        var store = new HistoryStore(_root);
        var vm = new HistoryListViewModel(store);
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryListViewModel.Rows)) fired = true; };
        vm.Refresh();
        fired.ShouldBeTrue();
    }

    [Fact]
    public void DeleteSelected_RemovesEntryAndRefreshes()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "a", RawTranscript = "x" });
        var vm = new HistoryListViewModel(store);
        vm.Refresh();

        vm.DeleteSelected(vm.Rows[0]);
        vm.Rows.ShouldBeEmpty();
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Row_FormatsTimestamp_AndDuration()
    {
        var entry = new HistoryEntry
        {
            CreatedAtUtc = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc),
            DurationMs = 2500,
            RawTranscript = "hi",
        };
        var row = new HistoryRowViewModel(entry);
        row.DurationDisplay.ShouldBe("2.5s");
        row.TimestampDisplay.ShouldContain("2026");
    }
}
