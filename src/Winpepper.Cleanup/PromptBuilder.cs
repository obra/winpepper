using System.Text;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Assembles the cleanup prompt per spec §6.2, split into a system message
/// (instructions + correction hints + OCR context) and a user message (the raw
/// transcript). Bug-3 fix-(iv): the previous single-blob prompt gave the 0.5B
/// model no system role, so it pattern-completed the examples. Pure-string,
/// stateless. Omission rules:
/// - &lt;CORRECTION-HINTS&gt; omitted iff both preferred and replacements are empty.
/// - &lt;OCR-RULES&gt; and &lt;WINDOW-OCR-CONTENT&gt; omitted iff windowContext
///   is null, whitespace, or empty after truncation.
/// - The window-context body is truncated to 4000 chars (spec §6.1 / §6.2).
/// </summary>
public static class PromptBuilder
{
    public const int WindowContextMaxChars = 4000;

    /// <summary>Instructions + optional correction hints + optional OCR context.
    /// Does NOT include the transcript.</summary>
    public static string BuildSystem(
        string basePrompt,
        CorrectionsData corrections,
        string? windowContext)
        => BuildSystem(basePrompt, corrections, windowContext, out _);

    /// <summary>
    /// Same as <see cref="BuildSystem(string, CorrectionsData, string?)"/> but
    /// also reports whether (and by how much) the window context was truncated,
    /// so callers can log it instead of losing screen context silently.
    /// </summary>
    public static string BuildSystem(
        string basePrompt,
        CorrectionsData corrections,
        string? windowContext,
        out WindowContextTruncation truncation)
    {
        var sb = new StringBuilder(capacity: 8192);

        sb.Append("<BASE-PROMPT>\n").Append(basePrompt).Append("\n</BASE-PROMPT>");

        var hasPreferred = corrections.Preferred.Count > 0;
        var hasReplacements = corrections.Replacements.Count > 0;
        if (hasPreferred || hasReplacements)
        {
            sb.Append("\n\n<CORRECTION-HINTS>");
            if (hasPreferred)
            {
                sb.Append("\nPreferred transcriptions:");
                foreach (var p in corrections.Preferred)
                    sb.Append("\n- ").Append(p);
            }
            if (hasReplacements)
            {
                sb.Append("\nMisheard replacements:");
                foreach (var kvp in corrections.Replacements)
                    sb.Append("\n- ").Append(kvp.Key).Append(" -> ").Append(kvp.Value);
            }
            sb.Append("\n</CORRECTION-HINTS>");
        }

        var truncated = TruncateWindowContext(windowContext, out truncation);
        if (!string.IsNullOrEmpty(truncated))
        {
            sb.Append("\n\n<OCR-RULES>\n")
              .Append("The WINDOW-OCR-CONTENT below is the text currently visible on the user's screen.\n")
              .Append("Use it only to disambiguate names, commands, file paths, and jargon.\n")
              .Append("Prefer the user's spoken words; never substitute OCR text wholesale.")
              .Append("\n</OCR-RULES>");

            sb.Append("\n\n<WINDOW-OCR-CONTENT>\n").Append(truncated).Append("\n</WINDOW-OCR-CONTENT>");
        }

        return sb.ToString();
    }

    /// <summary>The raw transcript, trimmed, wrapped in a USER-INPUT block.</summary>
    public static string BuildUser(string userInput)
        => "<USER-INPUT>\n" + (userInput ?? string.Empty).Trim() + "\n</USER-INPUT>";

    private static string? TruncateWindowContext(string? raw, out WindowContextTruncation truncation)
    {
        truncation = default;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw!.Trim();
        if (trimmed.Length <= WindowContextMaxChars) return trimmed;
        truncation = new WindowContextTruncation(
            Truncated: true,
            OriginalLength: trimmed.Length,
            RetainedLength: WindowContextMaxChars);
        return trimmed.Substring(0, WindowContextMaxChars);
    }
}

/// <summary>
/// Whether (and by how much) the window-context body was cut to fit the
/// <see cref="PromptBuilder.WindowContextMaxChars"/> budget. Lengths are
/// measured after trimming. <c>default</c> means "not truncated".
/// </summary>
public readonly record struct WindowContextTruncation(
    bool Truncated,
    int OriginalLength,
    int RetainedLength);
