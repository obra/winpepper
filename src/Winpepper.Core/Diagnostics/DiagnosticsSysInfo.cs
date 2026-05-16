using System.Runtime.InteropServices;

namespace Winpepper.Core.Diagnostics;

/// <summary>System-info block written into <c>sysinfo.json</c> inside the bundle.</summary>
public sealed record DiagnosticsSysInfo
{
    public required string AppVersion { get; init; }
    public required string OsDescription { get; init; }
    public required int ProcessorCount { get; init; }
    public required bool Is64BitOs { get; init; }
    public required DateTime CapturedAtUtc { get; init; }

    public static DiagnosticsSysInfo Capture(string appVersion) => new()
    {
        AppVersion = appVersion,
        OsDescription = RuntimeInformation.OSDescription,
        ProcessorCount = Environment.ProcessorCount,
        Is64BitOs = Environment.Is64BitOperatingSystem,
        CapturedAtUtc = DateTime.UtcNow,
    };
}
