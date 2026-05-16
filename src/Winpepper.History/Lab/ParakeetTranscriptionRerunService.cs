#if WINDOWS
using System.Diagnostics;
using Winpepper.Asr;

namespace Winpepper.History.Lab;

public sealed class ParakeetTranscriptionRerunService : ITranscriptionRerunService
{
    public Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var samples = WavWriter.ReadMono16kInt16(wavPath);
            using var session = new ParakeetSession(modelDirectory);
            var sw = Stopwatch.StartNew();
            var transcript = session.Transcribe(samples);
            sw.Stop();
            return new TranscriptionRerunResult
            {
                ModelName = modelName,
                Text = transcript.Text,
                Elapsed = sw.Elapsed,
            };
        }, ct);
    }
}
#endif
