#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Cleanup;

namespace Winpepper.History.Lab;

/// <summary>
/// Production rerun service. Constructs a transient
/// <see cref="LlamaCleanupBackend"/> against the user-selected GGUF, wraps it
/// in a <see cref="CleanupRunner"/>, and delegates to
/// <see cref="CleanupRunner.RunAsync"/>. Nothing is persisted back to the
/// history entry — this is an experiment, not an edit.
/// </summary>
public sealed class LlamaCleanupRerunService : ICleanupRerunService
{
    private readonly ILoggerFactory _loggerFactory;

    public LlamaCleanupRerunService(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public async Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct)
    {
        var hasCustom = !string.IsNullOrWhiteSpace(input.CustomBasePrompt);
        var options = new CleanupOptions
        {
            Profile = hasCustom ? CleanupProfile.Custom : CleanupProfile.Ordinary,
            CustomBasePrompt = hasCustom ? input.CustomBasePrompt : null,
            WindowContextEnabled = input.IncludeWindowContext,
        };

        Task<string?>? windowContextTask = null;
        if (input.IncludeWindowContext && !string.IsNullOrEmpty(input.WindowContextText))
            windowContextTask = Task.FromResult<string?>(input.WindowContextText);

        using var backend = new LlamaCleanupBackend(
            input.ModelPath,
            _loggerFactory.CreateLogger<LlamaCleanupBackend>(),
            promptFormat: input.PromptFormat);

        var runner = new CleanupRunner(
            backend,
            _loggerFactory.CreateLogger<CleanupRunner>());

        var result = await runner.RunAsync(
            rawTranscript: input.RawTranscript,
            corrections: input.Corrections,
            windowContextTask: windowContextTask,
            options: options,
            ct: ct).ConfigureAwait(false);

        return new CleanupRerunResult
        {
            ModelName = input.ModelName,
            AssembledPrompt = result.AssembledPrompt,
            RawOutput = result.RawModelOutput,
            CleanedText = result.CleanedText,
            Elapsed = result.Elapsed,
        };
    }
}
#endif
