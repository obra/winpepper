namespace Winpepper.Asr.Transcription;

/// <summary>A loaded, owned local batch ASR model usable as an ITranscriber.
/// PipelineHost holds its optional Parakeet backup through this seam so it
/// can dispose it on swap/teardown without knowing the concrete model type.</summary>
public interface IDisposableTranscriber : ITranscriber, IDisposable { }
