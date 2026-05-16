#if WINDOWS
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Winpepper.Core.Diagnostics;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Hosting;

public sealed class DiagnosticsHost : IDiagnosticsHost
{
    private readonly Func<Window?> _mainWindow;
    private readonly string _logsDir;
    private readonly string _historyRoot;
    private readonly string _settingsPath;
    private readonly string _appVersion;

    public DiagnosticsHost(
        Func<Window?> mainWindow, string logsDir, string historyRoot,
        string settingsPath, string appVersion)
    {
        _mainWindow = mainWindow;
        _logsDir = logsDir;
        _historyRoot = historyRoot;
        _settingsPath = settingsPath;
        _appVersion = appVersion;
    }

    public void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo { FileName = _logsDir, UseShellExecute = true }); }
        catch { }
    }

    public async Task<string?> SaveBundleAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"winpepper-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("Zip archive", new[] { ".zip" });
        var win = _mainWindow();
        if (win is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(win);
            InitializeWithWindow.Initialize(picker, hwnd);
        }
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = _logsDir,
            HistoryRoot = _historyRoot,
            SettingsPath = _settingsPath,
            SysInfo = DiagnosticsSysInfo.Capture(_appVersion),
        }, file.Path);
        return file.Path;
    }
}
#endif
