namespace Winpepper.Asr;

/// <summary>
/// Parakeet TDT v3 preprocessor configuration. Values match
/// parakeet-rs/src/model_tdt.rs and the HuggingFace ParakeetFeatureExtractor.
/// </summary>
public sealed record PreprocessorConfig(
    int FeatureSize = 128,
    int HopLength = 160,
    int NFft = 512,
    int WinLength = 400,
    double Preemphasis = 0.97,
    int SamplingRate = 16000)
{
    public static readonly PreprocessorConfig ParakeetTdtV3 = new();
}
