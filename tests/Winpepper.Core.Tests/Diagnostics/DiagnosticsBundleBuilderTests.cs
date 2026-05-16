using System.IO.Compression;
using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class DiagnosticsBundleBuilderTests : IDisposable
{
    private readonly string _root;
    public DiagnosticsBundleBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wp-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Build_Zips_Logs_Settings_History_Index_And_SysInfo_But_Skips_Wav()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "winpepper-20260516.log"), "log content");
        File.WriteAllText(Path.Combine(logs, "winpepper-20260515.log"), "older log");

        var historyRoot = Path.Combine(_root, "history"); Directory.CreateDirectory(historyRoot);
        File.WriteAllText(Path.Combine(historyRoot, "index.json"), "[]");
        File.WriteAllBytes(Path.Combine(historyRoot, "session-1.wav"), new byte[] { 0, 1, 2 });

        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{"schema":1}""");

        var output = Path.Combine(_root, "bundle.zip");
        var inputs = new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = historyRoot,
            SettingsPath = settings,
            SysInfo = new DiagnosticsSysInfo
            {
                AppVersion = "0.5.0",
                OsDescription = "Windows 11 Pro 23H2",
                ProcessorCount = 16,
                Is64BitOs = true,
                CapturedAtUtc = DateTime.UtcNow,
            },
        };

        DiagnosticsBundleBuilder.Build(inputs, output);

        File.Exists(output).ShouldBeTrue();
        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        names.ShouldContain("logs/winpepper-20260516.log");
        names.ShouldContain("logs/winpepper-20260515.log");
        names.ShouldContain("history-index.json");
        names.ShouldContain("settings.json");
        names.ShouldContain("sysinfo.json");
        names.ShouldNotContain(n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Tolerates_Missing_History_Index()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{}""");

        var output = Path.Combine(_root, "bundle.zip");
        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = Path.Combine(_root, "does-not-exist"),
            SettingsPath = settings,
            SysInfo = DiagnosticsSysInfo.Capture("0.0.0"),
        }, output);

        File.Exists(output).ShouldBeTrue();
        using var zip = ZipFile.OpenRead(output);
        zip.Entries.ShouldNotContain(e => e.FullName == "history-index.json");
    }

    [Fact]
    public void Build_Tolerates_Missing_Settings_File()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");

        var output = Path.Combine(_root, "bundle.zip");
        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = _root,
            SettingsPath = Path.Combine(_root, "no-settings.json"),
            SysInfo = DiagnosticsSysInfo.Capture("0.0.0"),
        }, output);

        File.Exists(output).ShouldBeTrue();
    }
}
