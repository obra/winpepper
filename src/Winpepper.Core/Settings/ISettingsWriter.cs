namespace Winpepper.Core.Settings;

public interface ISettingsWriter
{
    void Queue(Func<AppSettings, AppSettings> mutator);
    Task FlushAsync();

    /// <summary>
    /// Applies <paramref name="mutator"/> and flushes it to disk immediately,
    /// bypassing the debounce window. Use at durable checkpoints (onboarding
    /// step advance, a settings toggle/hotkey commit) so a subsequent
    /// force-kill (e.g. an MSI upgrade) cannot lose the change.
    /// </summary>
    Task QueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        Queue(mutator);
        return FlushAsync();
    }

    /// <summary>
    /// Applies and flushes <paramref name="mutator"/>, returning whether the
    /// change was durably persisted. Implementations without outcome tracking
    /// retain the historical assumption that a completed flush succeeded.
    /// </summary>
    async Task<bool> TryQueueAndFlushAsync(Func<AppSettings, AppSettings> mutator)
    {
        await QueueAndFlushAsync(mutator);
        return true;
    }
}
