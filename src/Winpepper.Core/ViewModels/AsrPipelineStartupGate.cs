namespace Winpepper.Core.ViewModels;

/// <summary>Starts dictation only after authoritative ASR verification.</summary>
public sealed class AsrPipelineStartupGate
{
    private readonly IAsrProvisioningService _provisioner;
    private readonly Func<bool> _tryStartPipeline;

    public AsrPipelineStartupGate(
        IAsrProvisioningService provisioner,
        Func<bool> tryStartPipeline)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _tryStartPipeline = tryStartPipeline ?? throw new ArgumentNullException(nameof(tryStartPipeline));
    }

    public async Task<bool> TryStartAsync(CancellationToken ct)
    {
        if (!await _provisioner.VerifyReadyAsync(ct).ConfigureAwait(false))
            return false;
        return _tryStartPipeline();
    }
}
