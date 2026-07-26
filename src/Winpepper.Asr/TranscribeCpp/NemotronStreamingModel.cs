namespace Winpepper.Asr.TranscribeCpp;

/// <summary>On-disk layout of the installed nemotron streaming model. Must
/// stay in lockstep with the ModelRegistry descriptor (enforced by
/// NemotronLayoutContractTests).</summary>
public static class NemotronStreamingModel
{
    public const string Name = "nemotron-streaming-en";
    public const string GgufFileName = "nemotron-speech-streaming-en-0.6b-Q8_0.gguf";
    /// <summary>The tarball extracts with ONE top-level directory.</summary>
    public const string TarballTopLevelDir = "transcribe-native-windows-x86_64-cpu-vulkan";

    public static string ModelFileRelative => Path.Combine(Name, GgufFileName);
    public static string RuntimeDirRelative => Path.Combine(Name, "runtime", TarballTopLevelDir);

    public static string GgufPath(string modelsRoot) => Path.Combine(modelsRoot, ModelFileRelative);
    public static string RuntimeDir(string modelsRoot) => Path.Combine(modelsRoot, RuntimeDirRelative);

    public static bool IsInstalled(string modelsRoot)
        => File.Exists(GgufPath(modelsRoot))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "transcribe.dll"))
        && File.Exists(Path.Combine(RuntimeDir(modelsRoot), "contract.json"));
}
