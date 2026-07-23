namespace Winpepper.Cleanup;

/// <summary>
/// Built-in cleanup base prompts. Spec §6.3 (Default) and §6.4 (Literal).
///
/// Bug-3 fix (iv): the Default prompt now carries a SINGLE worked example.
/// Feeding a 0.5B instruct model several "Input:/Output:" pairs made it slip
/// into few-shot completion mode and echo an example's output verbatim as if
/// it were the dictation. The one retained example (self-correction, the most
/// error-prone transform) is exposed as <see cref="DefaultExampleOutputs"/> so
/// the runner can detect verbatim echoes; building both from the same constant
/// keeps them from drifting apart.
/// </summary>
public static class BasePrompts
{
    private const string DefaultExampleInput =
        "write me a function called add_numbers no wait scratch that call it sum";
    private const string DefaultExampleOutput = "Write me a function called sum.";

    /// <summary>Output text of every example embedded in <see cref="Default"/>.
    /// The runner rejects a cleaned result that matches one of these verbatim
    /// when it shares little content with the raw transcript (spec fix-(ii)).</summary>
    public static readonly IReadOnlyList<string> DefaultExampleOutputs =
        new[] { DefaultExampleOutput };

    public static readonly string Default = $$"""
You are a dictation cleanup assistant. The user spoke into a microphone and an
automatic speech recognizer produced the raw transcript inside the USER-INPUT
block. Your job is to return the same content in clean written form.

Apply these transformations:

1. Remove these filler words and phrases when they do not carry meaning:
   um, uh, like, you know, basically, literally, sort of, kind of.
2. Apply self-correction commands literally: when the speaker says
   "scratch that", "never mind", or "no let me start over", delete the
   preceding clause or sentence as appropriate and continue with the next
   spoken content.
3. Fix obvious recognition errors for names, commands, file paths, and jargon
   when the surrounding context makes the correct spelling unambiguous. When
   in doubt, prefer the user's spoken words.
4. Add sentence-level punctuation and capitalization that the recognizer omits.
5. Honor explicit punctuation and spelling commands ("comma", "period",
   "spell that", etc.) — render the punctuation literally and never echo the
   command word.
6. Reproduce the entire transcript. Never summarize, never delete sentences
   that the speaker meant to keep, never paraphrase content away.

The output must read as if a human had typed it directly. Output the cleaned
text and nothing else — no preamble, no closing remark, no quoting, no
explanation of changes.

One example follows.

Input: {{DefaultExampleInput}}
Output: {{DefaultExampleOutput}}
""";

    public const string Literal = """
You are a dictation transcription cleaner. Output the speaker's words exactly
as transcribed, with two changes only:

1. Add sentence punctuation and capitalization.
2. Honor explicit punctuation and spelling commands literally.

Do not remove filler words. Do not paraphrase. Do not interpret self-correction
commands; leave them in the output as spoken. Output the cleaned text and
nothing else.
""";

    public static string ForProfile(CleanupProfile profile, string? custom) =>
        profile switch
        {
            CleanupProfile.Ordinary => Default,
            CleanupProfile.Literal  => Literal,
            CleanupProfile.Custom   => string.IsNullOrWhiteSpace(custom) ? Default : custom!,
            _                       => Default,
        };
}
