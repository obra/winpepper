namespace Winpepper.Core.Audio;

public interface ISoundEffectPlayer
{
    void PlayStart();
    void PlayStop();
    bool Enabled { get; set; }

    /// <summary>
    /// Measured duration of the start-cue asset in milliseconds, read from
    /// the actual file this player plays; 0 when unknown (no-op player,
    /// missing or unparseable asset). Used by the silence gate to mask the
    /// cue's mic-pickup window out of its decision — see StartCueGateMask.
    /// </summary>
    int StartCueMs { get; }
}
