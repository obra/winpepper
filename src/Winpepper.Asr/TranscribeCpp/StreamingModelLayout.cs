namespace Winpepper.Asr.TranscribeCpp;

/// <summary>Per-streaming-model on-disk layout. Must stay in lockstep with the
/// ModelRegistry descriptors (enforced by NemotronLayoutContractTests). The
/// native runtime tarball is shared between both models but extracts into each
/// model's own directory (per-model-dir), so each model is self-contained and
/// independently installable/repairable.</summary>
public sealed record StreamingModelLayout(string Name, string GgufFileName, string? Language)
{
    public const string TarballTopLevelDir = "transcribe-native-windows-x86_64-cpu-vulkan";
    public static readonly StreamingModelLayout English =
        new("nemotron-streaming-en", "nemotron-speech-streaming-en-0.6b-Q8_0.gguf", Language: null);
    public static readonly StreamingModelLayout Multilingual =
        new("nemotron-streaming-multi", "nemotron-3.5-asr-streaming-0.6b-Q8_0.gguf", Language: null); // null = autodetect (the model's auto prompt); the literal "auto" is rejected by the v0.1.3 language gate
    public static StreamingModelLayout For(string? name)
        => name == Multilingual.Name ? Multilingual : English;   // unknown/null -> English (safe default)
    public string ModelFileRelative => Path.Combine(Name, GgufFileName);
    public string RuntimeDirRelative => Path.Combine(Name, "runtime", TarballTopLevelDir);
    public string GgufPath(string modelsRoot) => Path.Combine(modelsRoot, ModelFileRelative);
    public string RuntimeDir(string modelsRoot) => Path.Combine(modelsRoot, RuntimeDirRelative);
    public bool IsInstalled(string modelsRoot)
        => File.Exists(GgufPath(modelsRoot))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "transcribe.dll"))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "contract.json"));
}
