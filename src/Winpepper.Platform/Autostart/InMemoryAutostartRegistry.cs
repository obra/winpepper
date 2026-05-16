namespace Winpepper.Platform.Autostart;

public sealed class InMemoryAutostartRegistry : IAutostartRegistry
{
    private string? _value;

    public bool IsEnabled() => _value is not null;
    public string? CurrentCommand() => _value;

    public void Enable(string exePath, string arguments)
    {
        var args = string.IsNullOrEmpty(arguments) ? "" : $" {arguments}";
        _value = $"\"{exePath}\"{args}";
    }

    public void Disable() => _value = null;
}
