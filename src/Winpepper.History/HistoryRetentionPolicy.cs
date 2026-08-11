using Winpepper.Core.Settings;

namespace Winpepper.History;

/// <summary>Retention limits applied to the history index and its WAV files.</summary>
public sealed record HistoryRetentionPolicy
{
    public int MaxEntries { get; init; } = 100;
    public int? MaxAgeDays { get; init; } = 30;

    public TimeSpan? MaxAge => MaxAgeDays is int days
        ? TimeSpan.FromDays(Math.Clamp(days, 1, 36_500))
        : null;

    public static HistoryRetentionPolicy Default { get; } = new();

    public static HistoryRetentionPolicy FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new HistoryRetentionPolicy
        {
            MaxEntries = Math.Clamp(settings.HistoryMaxEntries, 1, 10_000),
            MaxAgeDays = settings.HistoryMaxAgeDays is int days
                ? Math.Clamp(days, 1, 36_500)
                : null,
        };
    }
}

public sealed record HistoryAudioCleanupResult
{
    public int DeletedCount { get; init; }
    public int FailedCount { get; init; }
    public bool IndexSaveFailed { get; init; }
    public bool EnumerationFailed { get; init; }
}

public sealed record HistoryPruneResult
{
    public int DroppedCount { get; init; }
    public int RetainedAfterFailedDelete { get; init; }
    public bool IndexSaveFailed { get; init; }
    public bool LoadFailed { get; init; }
}
