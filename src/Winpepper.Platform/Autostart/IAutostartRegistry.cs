namespace Winpepper.Platform.Autostart;

public interface IAutostartRegistry
{
    bool IsEnabled();
    string? CurrentCommand();
    void Enable(string exePath, string arguments);
    void Disable();
}
