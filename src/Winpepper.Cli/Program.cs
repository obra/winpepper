using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
#if WINDOWS
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Core.Settings;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.WindowContext;
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

        var cleanupModelPath = Path.Combine(localAppData, "winpepper", "models", "cleanup",
            "qwen2.5-0.5b-instruct", "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf");
        if (!File.Exists(cleanupModelPath))
        {
            log.LogError("Cleanup model not found at {Path}. Run scripts/download-cleanup-model.ps1 first.", cleanupModelPath);
            return 3;
        }

        var correctionsPath = Path.Combine(localAppData, "winpepper", "corrections.json");
        var corrections = new CorrectionStore(correctionsPath);

        using var cleanupBackend = new LlamaCleanupBackend(cleanupModelPath, logFactory.CreateLogger<LlamaCleanupBackend>());
        await cleanupBackend.WarmAsync(CancellationToken.None);
        var cleanupRunner = new CleanupRunner(cleanupBackend, logFactory.CreateLogger<CleanupRunner>());

        var uiaReader = new UiaTreeReader(logFactory.CreateLogger<UiaTreeReader>());
        var ocrFallback = new OcrFallback(logFactory.CreateLogger<OcrFallback>());
        var windowContextPrefetch = WindowContextPrefetch.CreateWindows(
            uiaReader, ocrFallback, logFactory.CreateLogger<WindowContextPrefetch>());

        using var pipeline = new Pipeline(
            logFactory.CreateLogger<Pipeline>(), logFactory, modelDir,
            HotkeyChord.Parse(settings.HoldHotkey),
            HotkeyChord.Parse(settings.ToggleHotkey),
            HotkeyChord.Parse("Esc"),
            cleanupRunner,
            corrections,
            windowContextPrefetch);

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
