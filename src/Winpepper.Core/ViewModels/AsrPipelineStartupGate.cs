namespace Winpepper.Core.ViewModels;

/// <summary>Starts dictation only after authoritative verification of the
/// PRIMARY speech model. The verify policy is injected: since nemotron-first,
/// "primary ready" means the selected streaming model is installed+extracted,
/// OR the optional Parakeet backup passes size+SHA-256 (ModelsServices
/// composes this; see VerifyPrimarySpeechReadyAsync). The invariant stands:
/// a merely loadable stale model must not enter PipelineHost.</summary>
public sealed class AsrPipelineStartupGate
{
    private readonly Func<CancellationToken, Task<bool>> _verifyPrimaryReady;
    private readonly Func<bool> _tryStartPipeline;
    private readonly Action? _onNotReady;

    public AsrPipelineStartupGate(
        Func<CancellationToken, Task<bool>> verifyPrimaryReady,
        Func<bool> tryStartPipeline,
        Action? onNotReady = null)
    {
        _verifyPrimaryReady = verifyPrimaryReady ?? throw new ArgumentNullException(nameof(verifyPrimaryReady));
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
        _onNotReady = onNotReady;
    }

    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        if (!await _verifyPrimaryReady(ct))
        {
            _onNotReady?.Invoke();
            return false;
        }
        return _tryStartPipeline();
    }
}
