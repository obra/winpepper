using System.IO.Compression;
using System.Text.Json;

namespace Winpepper.Core.Diagnostics;

/// <summary>
/// Assembles the "Copy diagnostics bundle" zip. Spec §7.3 and §9.5. Never
/// includes <c>*.wav</c> files (filter is explicit, not by directory).
/// </summary>
public static class DiagnosticsBundleBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Build(DiagnosticsBundle inputs, string outputZipPath)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrEmpty(outputZipPath);

        var parent = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

        using var fs = File.Create(outputZipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        if (Directory.Exists(inputs.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(inputs.LogsDir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                AddFile(zip, file, $"logs/{name}");
            }
        }

        if (Directory.Exists(inputs.HistoryRoot))
        {
            var indexPath = Path.Combine(inputs.HistoryRoot, "index.json");
            if (File.Exists(indexPath))
                AddFile(zip, indexPath, "history-index.json");
        }

        if (File.Exists(inputs.SettingsPath))
            AddFile(zip, inputs.SettingsPath, "settings.json");

        var entry = zip.CreateEntry("sysinfo.json", CompressionLevel.Fastest);
        using (var es = entry.Open())
        using (var sw = new StreamWriter(es))
        {
            sw.Write(JsonSerializer.Serialize(inputs.SysInfo, JsonOptions));
        }
    }

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var es = entry.Open();
        using var fs = File.OpenRead(source);
        fs.CopyTo(es);
    }
}
