namespace Winpepper.Core;

/// <summary>
/// Pure formatting applied to text at the moment it is pasted/typed into the
/// target field. Kept separate from the cleaned transcript itself: history
/// archives the clean text; only the injected string gets paste ergonomics.
/// </summary>
public static class InjectionText
{
    /// <summary>
    /// Paste ergonomics: a dictation that ends with sentence-final punctuation
    /// (period, question mark, exclamation mark) gets a trailing space so the
    /// user (or the next dictation) can continue typing without manually
    /// inserting one. Anything else is returned unchanged.
    /// </summary>
    public static string ForPaste(string text)
        => text.EndsWith('.') || text.EndsWith('?') || text.EndsWith('!')
            ? text + " "
            : text;
}
