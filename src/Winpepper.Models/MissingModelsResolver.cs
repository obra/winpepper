namespace Winpepper.Models;

/// <summary>
/// Picks the descriptors that still need downloading given a list of currently
/// selected model names. The Models tab uses this for the "Download Missing
/// Models" button — it should only fetch what the user has chosen, not the
/// entire registry.
/// </summary>
public sealed class MissingModelsResolver
{
    public IReadOnlyList<ModelDescriptor> FindMissing(
        IEnumerable<ModelDescriptor> registry, string installRoot, IEnumerable<string> selectedNames)
    {
        var scope = new HashSet<string>(selectedNames, StringComparer.Ordinal);
        return registry
            .Where(d => scope.Contains(d.Name))
            .Where(d => !d.IsFullyInstalled(installRoot))
            .ToList();
    }
}
