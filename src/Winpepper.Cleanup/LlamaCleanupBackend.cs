#if WINDOWS
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Winpepper.Cleanup;

/// <summary>
/// Real <see cref="ILlamaCleanupBackend"/> built on LLamaSharp 0.27 with the
/// Vulkan backend NuGet. The <see cref="LLamaContext"/> is constructed once
/// (per process); <see cref="WarmAsync"/> primes the KV cache so the first
/// user dictation doesn't pay the cold-start cost.
/// </summary>
public sealed class LlamaCleanupBackend : ILlamaCleanupBackend, IDisposable
{
    private readonly ILogger<LlamaCleanupBackend> _log;
    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly uint? _samplingSeed;
    private readonly string _promptFormat;
    private readonly StatelessExecutor _executor;
    private bool _disposed;

    /// <summary>1b thread cap, tightened per the 2026-07-30 owner order:
    /// LLamaSharp 0.27's DEFAULT Threads is already ProcessorCount/2 (=16 on
    /// the owner's box), so the approved plan's max(1, ProcessorCount/2)
    /// would have been a no-op. The model is fully GPU-offloaded
    /// (GpuLayerCount=999) — CPU threads mainly drive graph orchestration —
    /// so cap LOW to bound the CPU burst that competes with live streaming
    /// ASR. Judged ONLY on scripts/run-cleanup-bench-windows.sh: median
    /// latency <= 1000 ms and unchanged eval outcomes.</summary>
    private static readonly int CleanupInferenceThreads =
        Math.Min(4, Math.Max(1, Environment.ProcessorCount / 4));

    /// <param name="samplingSeed">Optional fixed sampling seed. Null (production)
    /// keeps LLamaSharp's default random seed; the prompt eval suite pins it for
    /// determinism on top of the temp-0.1 sampling.</param>
    /// <param name="promptFormat">Prompt format id from
    /// <c>ModelDescriptor.PromptFormat</c> (see <see cref="CleanupPromptFormatter"/>).
    /// Defaults to chatml, the format of the registry-default qwen model.</param>
    public LlamaCleanupBackend(string modelPath, ILogger<LlamaCleanupBackend> log,
                                int contextSize = 4096, int gpuLayerCount = 999,
                                uint? samplingSeed = null,
                                string promptFormat = CleanupPromptFormatter.ChatMl)
    {
        _log = log;
        _samplingSeed = samplingSeed;
        // Fail at construction, not mid-dictation: an unknown format id would
        // otherwise produce silently wrong prompts.
        CleanupPromptFormatter.Validate(promptFormat);
        _promptFormat = promptFormat;
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount, // Vulkan backend picks the first device.
            Threads = CleanupInferenceThreads,
            BatchThreads = CleanupInferenceThreads,
        };
        _log.LogInformation("Loading cleanup model: {Path}", modelPath);
        _weights = LLamaWeights.LoadFromFile(_params);
        _log.LogInformation("Cleanup model loaded.");
        // C3: ONE executor per backend, not one per generation. Safe ONLY
        // because _gate (SemaphoreSlim(1,1)) serializes GenerateAsync — the
        // executor's _batch/Context are not concurrency-safe (v0.27.0 source:
        // batch.Clear() fully wipes stale state at the top of every
        // prompt-decode chunk, SafeLLamaContextHandle.cs:721 /
        // LLamaBatch.cs:259-272) — and ApplyTemplate=false is constant.
        // LLamaSharp 0.27's StatelessExecutor ctor creates and immediately
        // disposes a throwaway context (verified against the official
        // v0.27.0 tag — see docs/plans/2026-07-29-cleanup-asr-contention-evidence.md,
        // section "0c — RESOLVED"), so per-call construction doubled
        // per-generation Vulkan context churn for nothing.
        _executor = new StatelessExecutor(_weights, _params, _log)
        {
            ApplyTemplate = false,
        };
    }

    /// <summary>Pre-warm: pages in weights and shader pipeline (no persistent KV cache with StatelessExecutor). Spec §5.5.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            _log.LogDebug("Pre-warming cleanup LLM context...");
            await GenerateAsync("You are a helpful assistant.", "Hello.", "Hello.",
                maxNewTokens: 4, temperature: 0.1f, ct).ConfigureAwait(false);
            _log.LogDebug("Cleanup LLM pre-warm complete.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleanup LLM pre-warm failed (non-fatal).");
        }
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        string rawTranscript, int maxNewTokens, float temperature, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Bug-3 fix-(iv): hand the model a real system turn
            // (instructions/examples) separate from the user turn (transcript),
            // so it cleans the transcript instead of continuing the few-shot
            // block. We build the template ourselves (per-model shape from
            // CleanupPromptFormatter, unit-tested on the Linux gate) and
            // disable StatelessExecutor.ApplyTemplate, which only knows how to
            // wrap a single user message. Raw-completion formats ignore the
            // system/user prompts and frame the raw transcript directly.
            var plan = CleanupPromptFormatter.Build(
                _promptFormat, systemPrompt, userPrompt, rawTranscript);
            maxNewTokens = CleanupPromptFormatter.ApplyMinNewTokensFloor(
                maxNewTokens, plan.MinNewTokensFloor);

            // Greedy (raw-io): temperature 0 makes llama.cpp's temp sampler
            // keep only the max-logit candidate, i.e. greedy decoding, while
            // still supporting the repetition penalty (GreedySamplingPipeline
            // has no penalty support). TopP/TopK are no-ops under greedy: the
            // argmax token always survives both filters.
            // C1: BaseSamplingPipeline owns a native llama.cpp sampler chain
            // via a finalizer-backed SafeHandle (ownsHandle: true). Undisposed,
            // reclamation is delayed, finalizer-dependent, and non-deterministic
            // — and native memory exerts no managed-heap pressure, so undisposed
            // chains can pile up between collections. 'using' makes the free
            // deterministic per generation.
            using var pipeline = new DefaultSamplingPipeline
            {
                Temperature = plan.Greedy ? 0f : temperature,
                TopP = 0.95f,
                TopK = 40,
                // Default 1f = no penalty, matching the pre-formatter behavior.
                RepeatPenalty = plan.RepetitionPenalty ?? 1f,
            };
            if (_samplingSeed is { } seed) pipeline.Seed = seed;
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxNewTokens,
                AntiPrompts = plan.AntiPrompts.ToList(),
                SamplingPipeline = pipeline,
            };

            var sb = new StringBuilder();
            await foreach (var token in _executor.InferAsync(plan.PromptText, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (sb.Length > maxNewTokens * 8) // hard char cap as belt-and-braces
                {
                    _log.LogWarning("Cleanup generation hit the hard char cap ({Chars} chars > {MaxTokens} maxNewTokens * 8); output truncated mid-stream",
                        sb.Length, maxNewTokens);
                    break;
                }
            }
            return sb.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Disposal contract: NOT gated against a concurrent
    /// <see cref="GenerateAsync"/> — the caller must guarantee quiescence.
    /// In production the only owner is <see cref="CleanupBackendHolder"/>,
    /// which disposes (a) a replaced live backend at the serialized
    /// per-dictation seam (PipelineHost's run loop awaits RunAsync inline, so
    /// no generation is in flight there) and (b) pre-warmed backends that were
    /// never handed out (no callers by construction). Idempotent: safe to call
    /// twice.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _weights.Dispose();
        _gate.Dispose();
    }
}
#endif
