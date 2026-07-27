namespace AsrLatencyBench;

/// <summary>Resolves a per-model eval directory that mirrors the production
/// model layout: exactly one *.gguf at the root, plus a runtime dir holding
/// transcribe.dll — either runtime/transcribe.dll (flat) or
/// runtime/<tarball-top-dir>/transcribe.dll (production mirror).</summary>
public static class ModelDirLayout
{
    public sealed record Resolved(string GgufPath, string RuntimeDir);

    public static Resolved Resolve(string modelDir)
    {
        if (!Directory.Exists(modelDir))
            throw new InvalidOperationException($"model dir not found: {modelDir}");
        var ggufs = Directory.GetFiles(modelDir, "*.gguf");
        if (ggufs.Length != 1)
            throw new InvalidOperationException(
                $"model dir must contain exactly one .gguf at its root, found {ggufs.Length}: {modelDir}");
        var runtimeRoot = Path.Combine(modelDir, "runtime");
        string? runtimeDir = null;
        if (File.Exists(Path.Combine(runtimeRoot, "transcribe.dll")))
            runtimeDir = runtimeRoot;
        else if (Directory.Exists(runtimeRoot))
            runtimeDir = Directory.GetDirectories(runtimeRoot)
                .OrderBy(d => d, StringComparer.Ordinal)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "transcribe.dll")));
        if (runtimeDir is null)
            throw new InvalidOperationException(
                $"no transcribe.dll under {runtimeRoot} (expected runtime/transcribe.dll or runtime/<dir>/transcribe.dll)");
        return new Resolved(ggufs[0], runtimeDir);
    }
}
