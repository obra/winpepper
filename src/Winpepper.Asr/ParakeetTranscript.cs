namespace Winpepper.Asr;

public sealed record ParakeetTranscript(
    string Text,
    IReadOnlyList<int> TokenIds,
    IReadOnlyList<int> FrameIndices,
    IReadOnlyList<int> Durations);
