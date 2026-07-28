using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Orchestrates a cleanup attempt: optionally wait briefly for window context,
/// build the prompt, call the LLM with a timeout, sanitize the output, fall
/// back to a deterministic correction-only path on empty/"..."/timeout/error,
/// and always apply the case-aware substitution post-pass. Spec §5.5, §6.5.
/// </summary>
public sealed class CleanupRunner
{
    private readonly ILlamaCleanupBackend _backend;
    private readonly ILogger<CleanupRunner> _log;
    private readonly bool _omitPromptExample;

    /// <param name="omitPromptExample">From
    /// <c>ModelDescriptor.OmitPromptExample</c>: use the example-free default
    /// base prompt for models that echo the worked example instead of cleaning
    /// (see <see cref="BasePrompts.DefaultNoExample"/>).</param>
    public CleanupRunner(ILlamaCleanupBackend backend, ILogger<CleanupRunner> log,
        bool omitPromptExample = false)
    {
        _backend = backend;
        _log = log;
        _omitPromptExample = omitPromptExample;
    }

    /// <summary>
    /// Single home for the "will the cleanup LLM actually run?" policy:
    ///  * the user's Enabled toggle (read live per dictation),
    ///  * cloud transcripts (already server-side punctuated/formatted),
    ///  * the short-transcript bypass (a 0.5B model has nothing useful to do
    ///    with a 1-3 word utterance and most often hallucinates there).
    /// <see cref="RunAsync"/> uses it for its bypass behavior; PipelineHost uses
    /// it only to pick which engine event to fire (a dictation enters the
    /// CleaningUp state exactly when this returns true). Deterministic
    /// corrections run on every path regardless of this predicate.
    /// </summary>
    public static bool Preflight(string rawTranscript, CleanupOptions options, bool cloudTranscript) =>
        options.Enabled
        && !cloudTranscript
        && TranscriptSimilarity.WordCount(rawTranscript) >= 4;

    public async Task<CleanupResult> RunAsync(
        string rawTranscript,
        CorrectionsData corrections,
        Task<string?>? windowContextTask,
        CleanupOptions options,
        CancellationToken ct,
        bool skipLlm = false)
    {
        var sw = Stopwatch.StartNew();

        // 0) Bypass decision — the SAME predicate PipelineHost consults to pick
        //    the engine event, so pill state and runner behavior cannot diverge.
        //    Every bypass takes the deterministic correction-only path.
        //    Precedence (spec-owner ruling): the user's Enabled toggle outranks
        //    everything — when it is off, history/logs must say "disabled" even
        //    for cloud or short transcripts, so the user can SEE their switch
        //    working. Then cloud, then the short-transcript bypass.
        if (!Preflight(rawTranscript, options, cloudTranscript: skipLlm))
        {
            var path = !options.Enabled
                ? CleanupPath.BypassDisabled     // user turned the cleanup LLM off
                : skipLlm
                    ? CleanupPath.BypassProvider // cloud text arrives formatted; corrections only
                    : CleanupPath.BypassShort;   // 1-3 word utterance (spec fix-(iii))
            _log.LogDebug("Bypassing LLM cleanup ({Path})", path);
            return Finalize(rawTranscript, "", corrections, assembledPrompt: "", path, sw);
        }

        // 1) Resolve window context with a bounded wait.
        string? windowContext = null;
        if (options.WindowContextEnabled && windowContextTask is not null)
        {
            try
            {
                var completed = await Task.WhenAny(windowContextTask,
                                                   Task.Delay(options.WindowContextWait, ct))
                                          .ConfigureAwait(false);
                if (completed == windowContextTask)
                {
                    windowContext = await windowContextTask.ConfigureAwait(false);
                }
                else
                {
                    _log.LogDebug("Window-context prefetch exceeded {Budget}ms; proceeding without it",
                        options.WindowContextWait.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Window-context prefetch failed; proceeding without it");
            }
        }

        // 2) Build the system (instructions/hints/OCR) and user (transcript)
        //    messages separately. Bug-3 fix-(iv): a proper system role stops the
        //    0.5B model pattern-completing the examples.
        var basePrompt = BasePrompts.ForProfile(options.Profile, options.CustomBasePrompt, _omitPromptExample);
        var systemPrompt = PromptBuilder.BuildSystem(basePrompt, corrections, windowContext, out var wcTruncation);
        var userPrompt = PromptBuilder.BuildUser(rawTranscript);
        var assembled = systemPrompt + "\n\n" + userPrompt;
        if (wcTruncation.Truncated)
        {
            _log.LogWarning("Window context truncated from {OriginalChars} to {RetainedChars} chars to fit the prompt budget",
                wcTruncation.OriginalLength, wcTruncation.RetainedLength);
        }

        // 3) Compute the max-new-tokens budget per spec §5.5.
        var maxTokens = Math.Min(options.MaxNewTokensCap, (int)Math.Ceiling(rawTranscript.Length * 2.0));
        if (maxTokens < 1) maxTokens = 1;
        _log.LogDebug("Cleanup prompt budget: system {SystemChars} chars, user {UserChars} chars, maxNewTokens {MaxTokens}",
            systemPrompt.Length, userPrompt.Length, maxTokens);

        // 4) Call the backend with a timeout token.
        string raw;
        CleanupPath chosenPath;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);
            raw = await _backend.GenerateAsync(systemPrompt, userPrompt, rawTranscript, maxTokens, options.Temperature, timeoutCts.Token)
                                .ConfigureAwait(false);
            chosenPath = CleanupPath.Llm;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Cleanup LLM timed out after {Timeout}ms; falling back to correction-only path",
                options.Timeout.TotalMilliseconds);
            return Finalize(rawTranscript, "", corrections, assembled, CleanupPath.FallbackTimeout, sw);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cleanup backend threw; falling back to correction-only path");
            return Finalize(rawTranscript, "", corrections, assembled, CleanupPath.FallbackBackendError, sw);
        }

        // 5) Sanitize <think> blocks.
        var sanitized = ThinkSanitizer.Sanitize(raw);

        // 6) Empty or "..." → fallback.
        if (string.IsNullOrWhiteSpace(sanitized))
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackEmpty, sw);

        if (sanitized.Trim() == "...")
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackEllipsis, sw);

        // 6.5) Implausible output -> fallback. A small cleanup model fed an
        // out-of-distribution prompt can go into open-ended completion mode,
        // echoing the prompt scaffolding (few-shot "Input:/Output:" pairs,
        // chat-template markers) instead of cleaning the transcript. Cleanup
        // must never inject text that doesn't plausibly derive from what the
        // user said: reject scaffold markers the user didn't speak, and any
        // output that dramatically outgrew the raw transcript (cleanup only
        // removes fillers and adds punctuation; it never doubles the text).
        if (LooksLikePromptEcho(sanitized, rawTranscript))
        {
            _log.LogWarning("Cleanup output contains prompt-scaffold markers absent from the transcript; falling back. Output preview: {Preview}",
                sanitized.Length > 120 ? sanitized[..120] : sanitized);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (sanitized.Length > rawTranscript.Length * 2 + 64)
        {
            _log.LogWarning("Cleanup output implausibly long ({OutLen} chars from {InLen}-char transcript); falling back",
                sanitized.Length, rawTranscript.Length);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }

        // 6.5b) Content-similarity floor (spec fix-(i)/(ii)). A legitimate
        // cleanup only drops fillers and adds punctuation, so it retains most of
        // the raw transcript's content words. Reject wholesale replacement
        // (near-zero overlap) and severe truncation, and reject any output that
        // matches a known few-shot example verbatim while sharing little with
        // what the user actually said.
        var retention = TranscriptSimilarity.RetentionRatio(rawTranscript, sanitized);
        var rawContentCount = TranscriptSimilarity.ContentWords(rawTranscript).Count;
        var rawWordCount = TranscriptSimilarity.WordCount(rawTranscript);

        if (rawContentCount >= 1 && retention <= 0.0)
        {
            _log.LogWarning("Cleanup output shares no content words with the transcript (wholesale replacement); falling back");
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (rawWordCount > 6 && retention < 0.40)
        {
            _log.LogWarning("Cleanup output retains only {Retention:P0} of a {Words}-word transcript (severe truncation); falling back",
                retention, rawWordCount);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (retention < 0.40 && MatchesKnownExample(sanitized))
        {
            _log.LogWarning("Cleanup output matches a known few-shot example with low transcript overlap; falling back");
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }

        // 7) Apply deterministic correction post-pass (user corrections, then
        //    the built-in app-name mishearing correction).
        var withCorrections = ApplyDeterministicPostPass(sanitized, corrections);

        sw.Stop();
        return new CleanupResult(
            CleanedText: withCorrections,
            Path: chosenPath,
            RawModelOutput: raw,
            AssembledPrompt: assembled,
            Elapsed: sw.Elapsed);
    }

    // Hard markers: structural scaffolding (prompt-block tags, chat-template
    // tokens, Alpaca instruction markers) that never appears in legitimately
    // cleaned dictation. Counted whenever present in the output but not
    // literally present in the raw transcript.
    private static readonly string[] HardEchoMarkers =
    {
        "<BASE-PROMPT>", "</BASE-PROMPT>", "<USER-INPUT>", "</USER-INPUT>",
        "<CORRECTION-HINTS>", "<WINDOW-OCR-CONTENT>", "<OCR-RULES>",
        "<OUTPUT>", "</OUTPUT>",
        "<|im_start|>", "<|im_end|>",
        "### Instruction", "### Response",
    };

    // Soft markers: dialogue-turn labels the model emits when it slips into
    // transcript-completion mode. A user can legitimately dictate these
    // (e.g. "output colon forty two" -> "Output: forty-two."), so they only
    // count when the spoken transcript doesn't contain the base word at all.
    private static readonly (string Marker, string SpokenWord)[] SoftEchoMarkers =
    {
        ("Human:", "human"),
        ("Assistant:", "assistant"),
        ("Input:", "input"),
        ("Output:", "output"),
    };

    internal static bool LooksLikePromptEcho(string cleaned, string rawTranscript)
    {
        foreach (var marker in HardEchoMarkers)
        {
            if (cleaned.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                !rawTranscript.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        foreach (var (marker, spokenWord) in SoftEchoMarkers)
        {
            if (cleaned.Contains(marker, StringComparison.OrdinalIgnoreCase) &&
                !rawTranscript.Contains(spokenWord, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // Normalize to letters/digits only so punctuation/whitespace/case differences
    // between the model output and a stored example don't hide an echo.
    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool MatchesKnownExample(string cleaned)
    {
        var norm = Normalize(cleaned);
        if (norm.Length == 0) return false;
        foreach (var example in BasePrompts.DefaultExampleOutputs)
            if (Normalize(example) == norm) return true;
        return false;
    }

    // Deterministic post-pass shared by the LLM-success and fallback paths:
    // apply the user-configured corrections (corrections.json Replacements).
    // Applied on every path so injected text always benefits. There is no
    // built-in app-name correction: users add their own via the Corrections
    // page if they want it.
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        return CaseAwareReplacer.Apply(text, corrections.Replacements);
    }

    private static CleanupResult Finalize(
        string rawTranscript,
        string rawModelOutput,
        CorrectionsData corrections,
        string assembledPrompt,
        CleanupPath path,
        Stopwatch sw)
    {
        var cleaned = ApplyDeterministicPostPass(rawTranscript, corrections);
        sw.Stop();
        return new CleanupResult(cleaned, path, rawModelOutput, assembledPrompt, sw.Elapsed);
    }
}
