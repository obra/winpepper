using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
using Winpepper.Core.Threading;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Plug for the Diagnostics page. The page binds the WinUI <c>ListView</c> to
/// <see cref="DiagnosticsViewModel.Tail"/>, the level combo to
/// <see cref="DiagnosticsViewModel.MinimumLevel"/>, and invokes
/// <see cref="DiagnosticsViewModel.OpenLogFolder"/> /
/// <see cref="DiagnosticsViewModel.CopyDiagnosticsBundleAsync"/> from button
/// clicks. Spec §7.3.
/// </summary>
public interface IDiagnosticsHost
{
    void OpenLogFolder();
    /// <summary>Show a save dialog and write the bundle. Null = user cancelled.</summary>
    Task<string?> SaveBundleAsync();
}

public sealed class DiagnosticsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LogRingBuffer _buffer;
    private readonly IUiThread _ui;
    private readonly IDiagnosticsHost _host;
    private LogLevel _level = LogLevel.Information;
    private string _lastBundlePath = "";

    public ObservableCollection<LogTailEntry> Tail { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiagnosticsViewModel(LogRingBuffer buffer, IUiThread ui, IDiagnosticsHost host)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        foreach (var e in _buffer.Snapshot()) Tail.Add(e);
        _buffer.Appended += OnAppended;
    }

    public LogLevel MinimumLevel
    {
        get => _level;
        set { if (_level == value) return; _level = value; Raise(nameof(MinimumLevel)); }
    }

    public string LastBundlePath
    {
        get => _lastBundlePath;
        private set { if (_lastBundlePath == value) return; _lastBundlePath = value; Raise(nameof(LastBundlePath)); }
    }

    public void OpenLogFolder() => _host.OpenLogFolder();

    public async Task CopyDiagnosticsBundleAsync()
    {
        var path = await _host.SaveBundleAsync().ConfigureAwait(false);
        if (path is not null) _ui.Post(() => LastBundlePath = path);
    }

    private void OnAppended(LogTailEntry entry)
    {
        _ui.Post(() =>
        {
            while (Tail.Count >= _buffer.Capacity) Tail.RemoveAt(0);
            Tail.Add(entry);
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _buffer.Appended -= OnAppended;
}
