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

    /// <param name="samplingSeed">Optional fixed sampling seed. Null (production)
    /// keeps LLamaSharp's default random seed; the prompt eval suite pins it for
    /// determinism on top of the temp-0.1 sampling.</param>
    public LlamaCleanupBackend(string modelPath, ILogger<LlamaCleanupBackend> log,
                                int contextSize = 4096, int gpuLayerCount = 999,
                                uint? samplingSeed = null)
    {
        _log = log;
        _samplingSeed = samplingSeed;
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount, // Vulkan backend picks the first device.
        };
        _log.LogInformation("Loading cleanup model: {Path}", modelPath);
        _weights = LLamaWeights.LoadFromFile(_params);
        _log.LogInformation("Cleanup model loaded.");
    }

    /// <summary>Pre-warm: pages in weights and shader pipeline (no persistent KV cache with StatelessExecutor). Spec §5.5.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            _log.LogDebug("Pre-warming cleanup LLM context...");
            await GenerateAsync("You are a helpful assistant.", "Hello.",
                maxNewTokens: 4, temperature: 0.1f, ct).ConfigureAwait(false);
            _log.LogDebug("Cleanup LLM pre-warm complete.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleanup LLM pre-warm failed (non-fatal).");
        }
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        int maxNewTokens, float temperature, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Bug-3 fix-(iv): hand the model a real ChatML system turn
            // (instructions/examples) separate from the user turn (transcript),
            // so it cleans the transcript instead of continuing the few-shot
            // block. We build ChatML ourselves (Qwen2.5's template) and disable
            // StatelessExecutor.ApplyTemplate, which only knows how to wrap a
            // single user message.
            var templated =
                "<|im_start|>system\n" + systemPrompt + "<|im_end|>\n" +
                "<|im_start|>user\n" + userPrompt + "<|im_end|>\n" +
                "<|im_start|>assistant\n";

            var executor = new StatelessExecutor(_weights, _params, _log)
            {
                ApplyTemplate = false,
            };
            var pipeline = new DefaultSamplingPipeline
            {
                Temperature = temperature,
                TopP = 0.95f,
                TopK = 40,
            };
            if (_samplingSeed is { } seed) pipeline.Seed = seed;
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxNewTokens,
                AntiPrompts = new List<string> { "<|im_end|>", "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>" },
                SamplingPipeline = pipeline,
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(templated, inferenceParams, ct).ConfigureAwait(false))
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

    public void Dispose()
    {
        _weights.Dispose();
        _gate.Dispose();
    }
}
#endif
