using System.ComponentModel;

namespace Winpepper.History.ViewModels;

public sealed class HistoryRowViewModel : INotifyPropertyChanged
{
    public HistoryRowViewModel(HistoryEntry entry) { Entry = entry; }

    public HistoryEntry Entry { get; }

    public string TimestampDisplay => Entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TranscriptPreviewDisplay => Entry.TranscriptPreview;
    public string DurationDisplay
    {
        get
        {
            var s = Entry.DurationMs / 1000.0;
            return $"{s:F1}s";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
