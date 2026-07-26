using System;
using System.Collections.Generic;

namespace AsrLatencyBench;

/// <summary>
/// Replays a stored clip the way production capture delivers audio: one
/// ~500 ms preroll burst at session start (WarmWasapiRecorder drains the warm
/// ring as a single FramesAvailable event; PipelineHost StartSession uses
/// includePrerollMs: 500), then steady 50 ms frames. BCL-only so the same
/// file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalFraming
{
    public const int PrerollSamples = 8000; // 500 ms @ 16 kHz
    public const int FrameSamples = 800;    // 50 ms @ 16 kHz

    public static List<(int Offset, int Length)> Segments(int totalSamples)
    {
        var segments = new List<(int, int)>();
        if (totalSamples <= 0) return segments;
        var first = Math.Min(PrerollSamples, totalSamples);
        segments.Add((0, first));
        for (var offset = first; offset < totalSamples; offset += FrameSamples)
            segments.Add((offset, Math.Min(FrameSamples, totalSamples - offset)));
        return segments;
    }
}
