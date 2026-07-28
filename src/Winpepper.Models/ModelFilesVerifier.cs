namespace Winpepper.Models;

/// <summary>
/// Stateless descriptor-level readiness check: every file in the descriptor
/// exists, matches its declared size, and matches its SHA-256. Extracted so
/// non-ASR callers (the cleanup live-swap pre-warm) can verify without going
/// through <see cref="ModelProvisioningCoordinator.VerifyReadyAsync"/>, whose
/// state notifications feed the single global provisioning status consumed by
/// the ASR startup gate, onboarding, and the Models page. Size is checked
/// before hashing so missing/partial files short-circuit cheaply.
/// </summary>
public static class ModelFilesVerifier
{
    public static async Task<bool> VerifyAsync(
        ModelDescriptor descriptor, string installRoot, CancellationToken ct)
    {
        foreach (var file in descriptor.Files)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(installRoot, descriptor.InstallDirRelative, file.RelativePath);
            if (!File.Exists(path) || new FileInfo(path).Length != file.SizeBytes)
                return false;
            if (!await ChecksumVerifier.VerifyAsync(path, file.Sha256, ct).ConfigureAwait(false))
                return false;
        }

        return true;
    }
}
