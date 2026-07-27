namespace Winpepper.Cleanup;

/// <summary>
/// Everything the backend needs to run one generation for a given prompt
/// format: the fully assembled prompt text, the anti-prompt (stop) list, and
/// per-format generation overrides. Override fields are null/false for
/// formats that keep the production defaults (options temperature, top-p 0.95,
/// top-k 40); only <see cref="CleanupPromptFormatter.RawIo"/> sets them.
/// </summary>
public sealed record CleanupPromptPlan(
    string PromptText,
    IReadOnlyList<string> AntiPrompts,
    float? RepetitionPenalty,
    bool Greedy,
    int? MinNewTokensFloor);

/// <summary>
/// Pure per-model prompt assembly, keyed by <c>ModelDescriptor.PromptFormat</c>.
/// Lives OUTSIDE the #if WINDOWS gate so the exact prompt bytes, stop lists
/// and sampling overrides are unit-tested on the Linux gate; the Windows-only
/// <c>LlamaCleanupBackend</c> only executes the plan. Prompt shapes are
/// verified against each model's chat template (primary sources, 2026-07-27):
/// drifting a marker or a newline silently degrades cleanup quality, so keep
/// them byte-exact and covered by CleanupPromptFormatterTests.
/// </summary>
public static class CleanupPromptFormatter
{
    /// <summary>ChatML (Qwen2.5, LFM2.5-1.2B-Instruct). System + user turns;
    /// byte-identical to the prompt LlamaCleanupBackend used to hand-build.
    /// BOS comes from GGUF add_bos metadata, never from the prompt text.</summary>
    public const string ChatMl = "chatml";

    /// <summary>Granite-4.0 role markup. System + user turns; the assistant
    /// generation prompt has NO trailing newline. add_bos is false.</summary>
    public const string Granite = "granite";

    /// <summary>Raw completion (sotto-cleanup-lfm25-350m). The model was NOT
    /// trained with instructions: it receives ONLY the raw transcript inside
    /// an "### Input:/### Output:" frame -- no system prompt, no
    /// &lt;USER-INPUT&gt; wrapping, so correction hints and window context are
    /// inherently unsupported. Greedy decoding, repetition penalty 1.05, and
    /// a 900-token min-new-tokens floor per the model's usage notes.</summary>
    public const string RawIo = "raw-io";

    private const float RawIoRepetitionPenalty = 1.05f;
    private const int RawIoMinNewTokensFloor = 900;

    // Prompt-scaffold stops shared by the instruction formats: legitimately
    // cleaned dictation never contains these PromptBuilder markers, so hitting
    // one means the model slipped into transcript-completion mode.
    private static readonly string[] ScaffoldExtras =
        { "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>" };

    /// <summary>Throws for unknown format ids so a bad registry value fails at
    /// backend construction, not mid-dictation with silently wrong output.</summary>
    public static void Validate(string formatId)
    {
        if (formatId is not (ChatMl or Granite or RawIo))
        {
            throw new ArgumentException(
                $"Unknown cleanup prompt format '{formatId}'. Known formats: " +
                $"'{ChatMl}', '{Granite}', '{RawIo}' (see ModelDescriptor.PromptFormat).",
                nameof(formatId));
        }
    }

    /// <summary>
    /// Assemble the generation plan for one cleanup call.
    /// <paramref name="systemPrompt"/>/<paramref name="userPrompt"/> are the
    /// PromptBuilder outputs (instructions + &lt;USER-INPUT&gt;-wrapped
    /// transcript); <paramref name="rawTranscript"/> is the unwrapped
    /// transcript, used only by <see cref="RawIo"/>.
    /// </summary>
    public static CleanupPromptPlan Build(
        string formatId, string systemPrompt, string userPrompt, string rawTranscript)
    {
        Validate(formatId);
        return formatId switch
        {
            ChatMl => new CleanupPromptPlan(
                PromptText:
                    "<|im_start|>system\n" + systemPrompt + "<|im_end|>\n" +
                    "<|im_start|>user\n" + userPrompt + "<|im_end|>\n" +
                    "<|im_start|>assistant\n",
                AntiPrompts: Stops("<|im_end|>"),
                RepetitionPenalty: null,
                Greedy: false,
                MinNewTokensFloor: null),

            Granite => new CleanupPromptPlan(
                PromptText:
                    "<|start_of_role|>system<|end_of_role|>" + systemPrompt + "<|end_of_text|>\n" +
                    "<|start_of_role|>user<|end_of_role|>" + userPrompt + "<|end_of_text|>\n" +
                    "<|start_of_role|>assistant<|end_of_role|>",
                AntiPrompts: Stops("<|end_of_text|>"),
                RepetitionPenalty: null,
                Greedy: false,
                MinNewTokensFloor: null),

            // RawIo -- Validate() guarantees no other value reaches here.
            _ => new CleanupPromptPlan(
                PromptText:
                    "### Input:\n" + (rawTranscript ?? string.Empty).Trim() + "\n\n### Output:\n",
                // "###" is REQUIRED: the model sometimes starts a new
                // "### Input:" block instead of emitting EOS.
                AntiPrompts: new[] { "<|im_end|>", "###" },
                RepetitionPenalty: RawIoRepetitionPenalty,
                Greedy: true,
                MinNewTokensFloor: RawIoMinNewTokensFloor),
        };
    }

    /// <summary>Apply a plan's min-new-tokens floor to the runner-computed
    /// budget (spec 5.5's min(cap, chars*2)): floor wins only when larger.</summary>
    public static int ApplyMinNewTokensFloor(int computedMaxNewTokens, int? floor)
        => floor is { } f ? Math.Max(computedMaxNewTokens, f) : computedMaxNewTokens;

    private static string[] Stops(string endOfTurn)
    {
        var stops = new string[1 + ScaffoldExtras.Length];
        stops[0] = endOfTurn;
        ScaffoldExtras.CopyTo(stops, 1);
        return stops;
    }
}
