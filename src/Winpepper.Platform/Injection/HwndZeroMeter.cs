using System.Threading;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Process-lifetime occurrence counter for GetForegroundWindow() == 0
/// observations on the injection path, kept separately for the at-start
/// pre-check and the mid-stream per-chunk guard. Exists so the park-on-0
/// polarity (council majority 5-1, probe-gated 2026-07-28: 0-readings occur
/// only in 0.3-3.7 ms bursts during focus transitions, never at rest) can be
/// re-evaluated with field data -- every increment is paired with a log line
/// carrying the running count. Thread-safe: hotkey-arm injections and
/// UI-thread pill-click retries can race.
/// </summary>
public sealed class HwndZeroMeter
{
    private long _atStart;
    private long _midStream;

    /// <summary>Observations of hwnd == 0 at injection start.</summary>
    public long AtStartCount => Interlocked.Read(ref _atStart);

    /// <summary>Observations of hwnd == 0 by the per-chunk mid-paste guard.</summary>
    public long MidStreamCount => Interlocked.Read(ref _midStream);

    /// <summary>Record an at-start observation; returns the new running count.</summary>
    public long RecordAtStart() => Interlocked.Increment(ref _atStart);

    /// <summary>Record a mid-stream observation; returns the new running count.</summary>
    public long RecordMidStream() => Interlocked.Increment(ref _midStream);
}
