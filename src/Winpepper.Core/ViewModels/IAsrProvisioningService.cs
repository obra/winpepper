namespace Winpepper.Core.ViewModels;

public enum AsrProvisioningStatus
{
    Missing,
    Downloading,
    Verifying,
    Retrying,
    Ready,
    Failed,
}

public sealed record AsrProvisioningState(
    AsrProvisioningStatus Status,
    double ProgressPercent = 0,
    string? ErrorMessage = null);

/// <summary>A platform-neutral view of production ASR provisioning.</summary>
public interface IAsrProvisioningService
{
    AsrProvisioningState State { get; }
    event EventHandler<AsrProvisioningState>? StateChanged;
    Task EnsureReadyAsync(CancellationToken ct);
    Task<bool> VerifyReadyAsync(CancellationToken ct);
}
