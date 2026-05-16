using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.History.ViewModels;

public sealed class HistoryListViewModel : INotifyPropertyChanged
{
    private readonly HistoryStore _store;
    private ObservableCollection<HistoryRowViewModel> _rows = new();

    public HistoryListViewModel(HistoryStore store) { _store = store; }

    public ObservableCollection<HistoryRowViewModel> Rows
    {
        get => _rows;
        private set { _rows = value; OnPropertyChanged(); }
    }

    public void Refresh()
    {
        var loaded = _store.Load();
        Rows = new ObservableCollection<HistoryRowViewModel>(
            loaded.Entries.Select(e => new HistoryRowViewModel(e)));
    }

    public void DeleteSelected(HistoryRowViewModel row)
    {
        _store.Delete(row.Entry.Id);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
