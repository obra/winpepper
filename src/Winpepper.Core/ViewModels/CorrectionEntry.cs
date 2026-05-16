using System.ComponentModel;

namespace Winpepper.Core.ViewModels;

public sealed class PreferredEntry : INotifyPropertyChanged
{
    private string _text;
    private string? _error;
    public PreferredEntry(string text) { _text = text; }
    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }
    public string? Error { get => _error; set { _error = value; Raise(nameof(Error)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ReplacementEntry : INotifyPropertyChanged
{
    private string _wrong;
    private string _right;
    private string? _error;
    public ReplacementEntry(string wrong, string right) { _wrong = wrong; _right = right; }
    public string Wrong { get => _wrong; set { _wrong = value; Raise(nameof(Wrong)); } }
    public string Right { get => _right; set { _right = value; Raise(nameof(Right)); } }
    public string? Error { get => _error; set { _error = value; Raise(nameof(Error)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
