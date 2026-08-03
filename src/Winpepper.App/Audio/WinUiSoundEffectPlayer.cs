#if WINDOWS
using System.Media;
using Winpepper.Core.Audio;

namespace Winpepper.App.Audio;

public sealed class WinUiSoundEffectPlayer : ISoundEffectPlayer, IDisposable
{
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Measured once at construction from the SAME file handed to
    /// SoundPlayer, so the mask math can never disagree with what actually
    /// plays. 0 when the header is unreadable (WavDuration fails open).
    /// </summary>
    public int StartCueMs { get; }

    public WinUiSoundEffectPlayer(string assetsDir)
    {
        var startPath = Path.Combine(assetsDir, "start.wav");
        _start = new SoundPlayer(startPath);
        _stop  = new SoundPlayer(Path.Combine(assetsDir, "stop.wav"));
        _start.Load(); _stop.Load();
        StartCueMs = Winpepper.Audio.WavDuration.TryMeasureMs(startPath, out var cueMs) ? cueMs : 0;
    }

    public void PlayStart() { if (Enabled) try { _start.Play(); } catch { } }
    public void PlayStop()  { if (Enabled) try { _stop.Play(); }  catch { } }

    public void Dispose() { _start.Dispose(); _stop.Dispose(); }
}
#endif
