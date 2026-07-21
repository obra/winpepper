namespace Winpepper.Core.Learning;

/// <summary>
/// Single decision point for whether to start the post-paste learning watcher
/// after an injection (spec Task 5). Gates the (default-off) user setting
/// together with the pre-existing preconditions, so both PipelineHost trigger
/// sites share one tested predicate.
/// </summary>
public static class PostPasteGate
{
    public static bool ShouldWatch(
        bool learningEnabled, bool injected, bool hasWatcher, bool hasCapturer, bool hasText)
        => learningEnabled && injected && hasWatcher && hasCapturer && hasText;
}
