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

    public CleanupRunner(ILlamaCleanupBackend backend, ILogger<CleanupRunner> log)
    {
        _backend = backend;
        _log = log;
    }

    public async Task<CleanupResult> RunAsync(
        string rawTranscript,
        CorrectionsData corrections,
        Task<string?>? windowContextTask,
        CleanupOptions options,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

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

        // 2) Build the assembled prompt.
        var basePrompt = BasePrompts.ForProfile(options.Profile, options.CustomBasePrompt);
        var assembled = PromptBuilder.Build(
            basePrompt: basePrompt,
            corrections: corrections,
            windowContext: windowContext,
            userInput: rawTranscript);

        // 3) Compute the max-new-tokens budget per spec §5.5.
        var maxTokens = Math.Min(options.MaxNewTokensCap, (int)Math.Ceiling(rawTranscript.Length * 2.0));
        if (maxTokens < 1) maxTokens = 1;

        // 4) Call the backend with a timeout token.
        string raw;
        CleanupPath chosenPath;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);
            raw = await _backend.GenerateAsync(assembled, maxTokens, options.Temperature, timeoutCts.Token)
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

        // 7) Apply deterministic correction post-pass.
        var withCorrections = CaseAwareReplacer.Apply(sanitized, corrections.Replacements);

        sw.Stop();
        return new CleanupResult(
            CleanedText: withCorrections,
            Path: chosenPath,
            RawModelOutput: raw,
            AssembledPrompt: assembled,
            Elapsed: sw.Elapsed);
    }

    private static CleanupResult Finalize(
        string rawTranscript,
        string rawModelOutput,
        CorrectionsData corrections,
        string assembledPrompt,
        CleanupPath path,
        Stopwatch sw)
    {
        var cleaned = CaseAwareReplacer.Apply(rawTranscript, corrections.Replacements);
        sw.Stop();
        return new CleanupResult(cleaned, path, rawModelOutput, assembledPrompt, sw.Elapsed);
    }
}
