using System.Text;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Assembles the four-block cleanup prompt per spec §6.2. Pure-string,
/// stateless. Omission rules:
/// - &lt;CORRECTION-HINTS&gt; omitted iff both preferred and replacements are empty.
/// - &lt;OCR-RULES&gt; and &lt;WINDOW-OCR-CONTENT&gt; omitted iff windowContext
///   is null, whitespace, or empty after truncation.
/// - The window-context body is truncated to 4000 chars (spec §6.1 / §6.2).
/// </summary>
public static class PromptBuilder
{
    public const int WindowContextMaxChars = 4000;

    public static string Build(
        string basePrompt,
        CorrectionsData corrections,
        string? windowContext,
        string userInput)
    {
        var sb = new StringBuilder(capacity: 8192);

        // <BASE-PROMPT>
        sb.Append("<BASE-PROMPT>\n").Append(basePrompt).Append("\n</BASE-PROMPT>");

        // <CORRECTION-HINTS> (omit when both lists empty)
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

        // <OCR-RULES> + <WINDOW-OCR-CONTENT> (omit when window context is empty)
        var truncated = TruncateWindowContext(windowContext);
        if (!string.IsNullOrEmpty(truncated))
        {
            sb.Append("\n\n<OCR-RULES>\n")
              .Append("The WINDOW-OCR-CONTENT below is the text currently visible on the user's screen.\n")
              .Append("Use it only to disambiguate names, commands, file paths, and jargon.\n")
              .Append("Prefer the user's spoken words; never substitute OCR text wholesale.")
              .Append("\n</OCR-RULES>");

            sb.Append("\n\n<WINDOW-OCR-CONTENT>\n").Append(truncated).Append("\n</WINDOW-OCR-CONTENT>");
        }

        // <USER-INPUT>
        sb.Append("\n\n<USER-INPUT>\n").Append((userInput ?? string.Empty).Trim()).Append("\n</USER-INPUT>");

        return sb.ToString();
    }

    private static string? TruncateWindowContext(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw!.Trim();
        if (trimmed.Length <= WindowContextMaxChars) return trimmed;
        return trimmed.Substring(0, WindowContextMaxChars);
    }
}
