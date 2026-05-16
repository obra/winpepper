namespace Winpepper.Core.Audio;

public interface ISoundEffectPlayer
{
    void PlayStart();
    void PlayStop();
    bool Enabled { get; set; }
}
