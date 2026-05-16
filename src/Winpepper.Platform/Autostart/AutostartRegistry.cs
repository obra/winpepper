#if WINDOWS
using Microsoft.Win32;

namespace Winpepper.Platform.Autostart;

public sealed class AutostartRegistry : IAutostartRegistry
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Winpepper";

    public bool IsEnabled() => CurrentCommand() is not null;

    public string? CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return k?.GetValue(ValueName) as string;
    }

    public void Enable(string exePath, string arguments)
    {
        var args = string.IsNullOrEmpty(arguments) ? "" : $" {arguments}";
        var value = $"\"{exePath}\"{args}";
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key for write.");
        k.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
#endif
