using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Strips reasoning-style scratchpad markup from LLM output. Spec §5.5.
/// Handles both balanced <c>&lt;think&gt;...&lt;/think&gt;</c> blocks and
/// orphan opening <c>&lt;think&gt;</c> tags (where the model ran out of
/// tokens before closing).
/// </summary>
public static class ThinkSanitizer
{
    // Non-greedy, multi-line. The dotall flag lets `.` span newlines.
    private static readonly Regex BalancedThinkBlock = new(
        @"<think>.*?</think>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Orphan opening: <think> with no later </think>. We strip from <think> to end.
    private static readonly Regex OrphanOpening = new(
        @"<think>.*$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // 1) Strip all balanced blocks.
        var stripped = BalancedThinkBlock.Replace(raw, string.Empty);

        // 2) Any remaining <think> with no matching </think> = orphan; strip to EOF.
        stripped = OrphanOpening.Replace(stripped, string.Empty);

        return stripped.Trim();
    }
}
