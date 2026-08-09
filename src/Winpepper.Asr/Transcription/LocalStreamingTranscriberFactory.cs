using Microsoft.Extensions.Logging;
using Winpepper.Asr.TranscribeCpp;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Pure composition of the LOCAL transcriber ladder, extracted from
/// AppShell.BuildStreamingTranscriber so the selection rules are
/// Linux-testable (AppShell is #if WINDOWS and untestable).
///
/// Roles (2026-08 nemotron-first): the Nemotron model selected in settings is
/// PRIMARY (streaming when enabled+installed, batch otherwise). Parakeet is
/// an OPTIONAL BACKUP: it joins the batch ladder only when installed, and
/// only fires when the Nemotron engine has trouble (worker restarting, model
/// missing, native failure). With no Parakeet and no Nemotron the ladder
/// fails loudly — the pipeline gates on primary availability before this runs.
/// </summary>
public static class LocalStreamingTranscriberFactory
{
    public static ITranscriber BuildBatchLadder(
        Func<ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        ILoggerFactory loggerFactory)
    {
        var nemotronBatch = new NemotronBatchTranscriber(
            nemotronEngine, streamingModelName + "-batch", streamingLanguage,
            loggerFactory.CreateLogger<NemotronBatchTranscriber>());
        if (parakeetBatch is null) return nemotronBatch;
        return new FallbackTranscriber(
            nemotronBatch, parakeetBatch,
            loggerFactory.CreateLogger<FallbackTranscriber>());
    }

    public static IStreamingTranscriber Build(
        Func<ITranscribeCppEngine?> nemotronEngine,
        ITranscriber? parakeetBatch,
        string streamingModelName,
        string? streamingLanguage,
        bool streamingEnabled,
        ILoggerFactory loggerFactory)
    {
        var ladder = BuildBatchLadder(nemotronEngine, parakeetBatch, streamingModelName, streamingLanguage, loggerFactory);

        if (!streamingEnabled)
            return new BatchStreamingAdapter(ladder); // Nemotron serves StreamingEnabled=false

        var engine = nemotronEngine();
        if (engine is null)
            return new BatchStreamingAdapter(ladder); // model not installed yet

        return new NemotronStreamingTranscriber(
            () => engine, ladder, streamingModelName,
            loggerFactory.CreateLogger<NemotronStreamingTranscriber>(),
            language: streamingLanguage);
    }
}
