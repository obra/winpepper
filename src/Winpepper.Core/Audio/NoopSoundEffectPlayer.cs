namespace Winpepper.Core.Audio;

public sealed class NoopSoundEffectPlayer : ISoundEffectPlayer
{
    public int StartPlays { get; private set; }
    public int StopPlays { get; private set; }
    public bool Enabled { get; set; } = true;

    public void PlayStart() { if (Enabled) StartPlays++; }
    public void PlayStop()  { if (Enabled) StopPlays++; }
}
