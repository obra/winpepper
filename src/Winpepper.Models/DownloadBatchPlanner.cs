namespace Winpepper.Models;

/// <summary>Pure planning math for the onboarding download batch —
/// Linux-tested; the App-side OnboardingModelProvisioner drives it.</summary>
public static class DownloadBatchPlanner
{
    public static IReadOnlyList<ModelDescriptor> Plan(
        ModelRegistry registry, string installRoot,
        IReadOnlyList<string> names, string speechModelName)
    {
        var unique = names.Distinct(StringComparer.Ordinal);
        return unique
            .Select(registry.Find)
            .Where(d => d is not null).Select(d => d!)
            .Where(d => !d.ManualInstallOnly)
            .Where(d => !d.IsFullyInstalledAndExtracted(installRoot))
            .OrderBy(d => d.Name == speechModelName ? 0 : 1)
            .ToList();
    }

    public static double AggregatePercent(IReadOnlyList<(long TotalBytes, long DoneBytes)> perDescriptor)
    {
        var total = perDescriptor.Sum(p => p.TotalBytes);
        if (total <= 0) return 100;
        var done = perDescriptor.Sum(p => Math.Clamp(p.DoneBytes, 0, p.TotalBytes));
        return Math.Clamp(100.0 * done / total, 0, 100);
    }
}
