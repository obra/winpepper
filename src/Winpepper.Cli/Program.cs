using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
#if WINDOWS
using Winpepper.Core.Settings;
using Winpepper.Platform.Hotkeys;
#endif

namespace Winpepper.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(localAppData, "winpepper", "logs");
        using var logFactory = WinpepperLogging.Create(logDir, debugConsole: true, minimumLevel: LogLevel.Information);
        var log = logFactory.CreateLogger("winpepper");

        log.LogInformation("Winpepper CLI starting.");

#if WINDOWS
        var settings = new SettingsStore(Path.Combine(localAppData, "winpepper", "settings.json")).Load();
        var modelDir = Path.Combine(localAppData, "winpepper", "models", "parakeet-tdt-0.6b-v3");
        if (!Directory.Exists(modelDir))
        {
            log.LogError("Parakeet model not found at {Dir}. Run scripts/download-parakeet.ps1 first.", modelDir);
            return 2;
        }

        using var pipeline = new Pipeline(
            logFactory.CreateLogger<Pipeline>(), logFactory, modelDir,
            HotkeyChord.Parse(settings.HoldHotkey),
            HotkeyChord.Parse(settings.ToggleHotkey),
            HotkeyChord.Parse("Esc"));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await pipeline.RunAsync(cts.Token);
        return 0;
#else
        log.LogError("Winpepper CLI requires Windows.");
        return 1;
#endif
    }
}
