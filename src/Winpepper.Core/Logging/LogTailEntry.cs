namespace Winpepper.Core.Logging;

/// <summary>One rendered log line for the Diagnostics live tail.</summary>
public sealed record LogTailEntry(DateTime TimestampUtc, string Level, string Message)
{
    /// <summary>
    /// <see cref="TimestampUtc"/> converted to the user's local time, for display.
    /// Treats <see cref="DateTimeKind.Unspecified"/> as UTC so a missing Kind never
    /// silently displays a UTC time as if it were local.
    /// </summary>
    public DateTime TimestampLocal =>
        TimestampUtc.Kind == DateTimeKind.Local
            ? TimestampUtc
            : DateTime.SpecifyKind(TimestampUtc, DateTimeKind.Utc).ToLocalTime();
}
