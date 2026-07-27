namespace Winpepper.Models;

/// <summary>
/// Outcome of resolving <c>AppSettings.CleanupModelName</c> to an on-disk
/// GGUF path. <see cref="GgufPath"/> is computed, not verified: callers decide
/// what to do when the file is absent. <see cref="FellBackToDefault"/> is true
/// when a non-empty requested name did not match the resolved descriptor
/// (unknown name, or a name of the wrong kind), so callers can surface the
/// silent fallback baked into <see cref="ModelRegistry.ResolveOrDefault"/>.
/// </summary>
public sealed record CleanupModelResolution(
    string? GgufPath,
    string ResolvedName,
    bool FellBackToDefault);

/// <summary>
/// Pure resolver from a requested cleanup-model name to the absolute path of
/// its .gguf file under the models root
/// (<c>&lt;modelsRoot&gt;/&lt;InstallDirRelative&gt;/&lt;file&gt;.gguf</c>,
/// e.g. <c>&lt;root&gt;/cleanup/&lt;key&gt;/&lt;key&gt;.gguf</c>).
/// No filesystem access.
/// </summary>
public static class CleanupModelPathResolver
{
    public static CleanupModelResolution Resolve(
        ModelRegistry registry, string modelsRoot, string? requestedName)
    {
        var descriptor = registry.ResolveOrDefault(requestedName, ModelKind.Cleanup);
        var fellBack = !string.IsNullOrEmpty(requestedName)
                       && !string.Equals(requestedName, descriptor.Name, StringComparison.Ordinal);
        var gguf = descriptor.Files.FirstOrDefault(
            f => f.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
        var path = gguf is null
            ? null
            : Path.Combine(modelsRoot, descriptor.InstallDirRelative, gguf.RelativePath);
        return new CleanupModelResolution(path, descriptor.Name, fellBack);
    }
}
