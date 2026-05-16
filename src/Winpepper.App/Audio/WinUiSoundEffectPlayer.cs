#if WINDOWS
using System.Media;
using Winpepper.Core.Audio;

namespace Winpepper.App.Audio;

public sealed class WinUiSoundEffectPlayer : ISoundEffectPlayer, IDisposable
{
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;

    public bool Enabled { get; set; } = true;

    public WinUiSoundEffectPlayer(string assetsDir)
    {
        _start = new SoundPlayer(Path.Combine(assetsDir, "start.wav"));
        _stop  = new SoundPlayer(Path.Combine(assetsDir, "stop.wav"));
        _start.Load(); _stop.Load();
    }

    public void PlayStart() { if (Enabled) try { _start.Play(); } catch { } }
    public void PlayStop()  { if (Enabled) try { _stop.Play(); }  catch { } }

    public void Dispose() { _start.Dispose(); _stop.Dispose(); }
}
#endif
