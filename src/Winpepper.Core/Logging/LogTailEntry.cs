namespace Winpepper.Core.Logging;

/// <summary>One rendered log line for the Diagnostics live tail.</summary>
public sealed record LogTailEntry(DateTime TimestampUtc, string Level, string Message);
