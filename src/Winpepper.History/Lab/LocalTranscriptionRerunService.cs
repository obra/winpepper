#if WINDOWS
using System.Diagnostics;
using Winpepper.Asr;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.History.Lab;

/// <summary>Reruns a history WAV against a locally installed model: Nemotron
/// (batch, via a NAME-KEYED engine provider that must return an engine serving
/// exactly the requested model, or null) or Parakeet (fresh ONNX session per
/// call). Missing/unavailable models fail with an actionable message instead
/// of a raw FileNotFoundException or a silent wrong-model transcript.
/// Replaces ParakeetTranscriptionRerunService.</summary>
public sealed class LocalTranscriptionRerunService : ITranscriptionRerunService
{
    private readonly Func<string, ITranscribeCppEngine?> _nemotronEngineFor;
    private readonly Func<string, bool> _isStreamingModelName;

    public LocalTranscriptionRerunService(
        Func<string, ITranscribeCppEngine?> nemotronEngineFor,
        Func<string, bool> isStreamingModelName)
    {
        _nemotronEngineFor = nemotronEngineFor;
        _isStreamingModelName = isStreamingModelName;
    }

    public Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var samples = WavWriter.ReadMono16kInt16(wavPath);
            var route = RerunModelRouter.Decide(
                _isStreamingModelName(modelName),
                ParakeetSession.ModelFilesPresent(modelDirectory));
            var sw = Stopwatch.StartNew();
            string text;
            switch (route)
            {
                case RerunModelRouter.Route.NemotronBatch:
                    // Name-keyed: the provider returns null unless the shared
                    // engine serves EXACTLY modelName (see AppShell wiring) —
                    // never transcribe with a different model than we stamp.
                    var engine = _nemotronEngineFor(modelName)
                        ?? throw new InvalidOperationException(
                            $"Speech engine for '{modelName}' is unavailable. Select it as the speech model in Settings > Models (installing it if needed), then rerun.");
                    // A rerun issued DURING a live streaming dictation shares the
                    // worker: it disposes the live stream to serve this batch call
                    // (that dictation falls back to batch).
                    text = engine.TranscribeBatch(samples, StreamingModelLayout.For(modelName).Language, out _);
                    break;
                case RerunModelRouter.Route.ParakeetSession:
                    using (var session = new ParakeetSession(modelDirectory))
                        text = session.Transcribe(samples).Text;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Model '{modelName}' is not installed. Download it from Settings > Models.");
            }
            sw.Stop();
            return new TranscriptionRerunResult { ModelName = modelName, Text = text, Elapsed = sw.Elapsed };
        }, ct);
    }
}
#endif
