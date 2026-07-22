namespace Winpepper.Core.ViewModels;

/// <summary>Starts dictation only after authoritative ASR verification.</summary>
public sealed class AsrPipelineStartupGate
{
    private readonly IAsrProvisioningService _provisioner;
    private readonly Func<bool> _tryStartPipeline;
    private readonly Action? _onNotReady;

    public AsrPipelineStartupGate(
        IAsrProvisioningService provisioner,
        Func<bool> tryStartPipeline,
        Action? onNotReady = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
        _onNotReady = onNotReady;
    }

    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        if (!await _provisioner.VerifyReadyAsync(ct))
        {
            _onNotReady?.Invoke();
            return false;
        }
        return _tryStartPipeline();
    }
}
