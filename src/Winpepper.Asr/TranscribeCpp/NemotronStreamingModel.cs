namespace Winpepper.Asr.TranscribeCpp;

/// <summary>Back-compat shim over <see cref="StreamingModelLayout.English"/>.
/// Prefer StreamingModelLayout for anything model-selection aware.</summary>
public static class NemotronStreamingModel
{
    public static string Name => StreamingModelLayout.English.Name;
    public static string GgufFileName => StreamingModelLayout.English.GgufFileName;
    public static string TarballTopLevelDir => StreamingModelLayout.TarballTopLevelDir;
    public static string ModelFileRelative => StreamingModelLayout.English.ModelFileRelative;
    public static string RuntimeDirRelative => StreamingModelLayout.English.RuntimeDirRelative;
    public static string GgufPath(string modelsRoot) => StreamingModelLayout.English.GgufPath(modelsRoot);
    public static string RuntimeDir(string modelsRoot) => StreamingModelLayout.English.RuntimeDir(modelsRoot);
    public static bool IsInstalled(string modelsRoot) => StreamingModelLayout.English.IsInstalled(modelsRoot);
}
