namespace Winpepper.Core.Diagnostics;

/// <summary>Inputs to <see cref="DiagnosticsBundleBuilder.Build"/>.</summary>
public sealed record DiagnosticsBundle
{
    public required string LogsDir { get; init; }
    public required string HistoryRoot { get; init; }
    public required string SettingsPath { get; init; }
    public required DiagnosticsSysInfo SysInfo { get; init; }
}
