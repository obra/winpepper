namespace Winpepper.History.Lab;

public interface ICleanupRerunService
{
    Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct);
}
