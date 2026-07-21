namespace Winpepper.Audio;

/// <summary>
/// Pure pre-roll bookkeeping for warm capture (Bug 2). A single audio callback
/// continuously <see cref="Ingest"/>s frames; the ring always holds the last
/// <c>ringCapacitySamples</c> samples. On <see cref="StartSession"/> the session
/// buffer is seeded from the ring (the pre-roll) and thereafter live frames are
/// appended too, so the returned buffer includes the ~500 ms spoken just before
/// the hotkey press. Thread-safe: the WASAPI callback and the hotkey thread call
/// in concurrently.
/// </summary>
public sealed class WarmCaptureBuffer
{
    private readonly int _ringCapacity;
    private readonly List<float> _ring;
    private readonly List<float> _session = new();
    private bool _active;
    private bool _sessionWasSilent;
    private readonly object _lock = new();

    public WarmCaptureBuffer(int ringCapacitySamples)
    {
        if (ringCapacitySamples < 0) ringCapacitySamples = 0;
        _ringCapacity = ringCapacitySamples;
        _ring = new List<float>(ringCapacitySamples + 1);
    }

    public bool IsSessionActive
    {
        get { lock (_lock) { return _active; } }
    }

    /// <summary>
    /// True when the most recently ended session captured essentially zero
    /// energy (Bug 2, warm-level health flag). Valid after <see cref="StopSession"/>.
    /// </summary>
    public bool SessionWasSilent
    {
        get { lock (_lock) { return _sessionWasSilent; } }
    }

    public void Ingest(ReadOnlySpan<float> frame)
    {
        lock (_lock)
        {
            foreach (var s in frame) _ring.Add(s);
            if (_ring.Count > _ringCapacity)
                _ring.RemoveRange(0, _ring.Count - _ringCapacity);

            if (_active)
                foreach (var s in frame) _session.Add(s);
        }
    }

    public void StartSession(int prerollSamples)
    {
        if (prerollSamples < 0) prerollSamples = 0;
        lock (_lock)
        {
            _session.Clear();
            var take = Math.Min(prerollSamples, _ring.Count);
            if (take > 0)
                _session.AddRange(_ring.GetRange(_ring.Count - take, take));
            _active = true;
            _sessionWasSilent = false;
        }
    }

    public float[] StopSession()
    {
        lock (_lock)
        {
            _active = false;
            var result = _session.ToArray();
            _sessionWasSilent = AudioEnergy.IsSessionSilent(result);
            _session.Clear();
            return result;
        }
    }

    /// <summary>
    /// Drop all buffered ring history (Bug 6). Called on a device rebuild so the
    /// next session's pre-roll cannot be seeded with audio captured on the old
    /// device. Leaves an in-flight session's already-collected audio intact.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _ring.Clear();
        }
    }
}
