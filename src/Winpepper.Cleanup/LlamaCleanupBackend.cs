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
    private readonly LLamaContext _context;
    private readonly ModelParams _params;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    public LlamaCleanupBackend(string modelPath, ILogger<LlamaCleanupBackend> log,
                                int contextSize = 4096, int gpuLayerCount = 999)
    {
        _log = log;
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount, // Vulkan backend picks the first device.
        };
        _log.LogInformation("Loading cleanup model: {Path}", modelPath);
        _weights = LLamaWeights.LoadFromFile(_params);
        _context = _weights.CreateContext(_params);
        _log.LogInformation("Cleanup model loaded.");
    }

    /// <summary>Pre-warm the KV cache. Spec §5.5.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        const string warmupPrompt = "Hello.";
        try
        {
            _log.LogDebug("Pre-warming cleanup LLM context...");
            await GenerateAsync(warmupPrompt, maxNewTokens: 4, temperature: 0.1f, ct).ConfigureAwait(false);
            _log.LogDebug("Cleanup LLM pre-warm complete.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleanup LLM pre-warm failed (non-fatal).");
        }
    }

    public async Task<string> GenerateAsync(string prompt, int maxNewTokens, float temperature, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var executor = new InstructExecutor(_context);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxNewTokens,
                AntiPrompts = new List<string> { "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    TopP = 0.95f,
                    TopK = 40,
                },
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (sb.Length > maxNewTokens * 8) break; // hard char cap as belt-and-braces
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
        _context.Dispose();
        _weights.Dispose();
        _gate.Dispose();
    }
}
#endif
