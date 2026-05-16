namespace Winpepper.Platform.Injection;

/// <summary>
/// Cross-platform clipboard seam. Production Windows impl lives in
/// <c>Winpepper.App.Hosting.WindowsClipboard</c>.
/// </summary>
public interface IClipboard
{
    /// <summary>Returns true on success.</summary>
    bool SetText(string text);
}

/// <summary>
/// Spec §5.6 fallback: when <see cref="TextInjector.TryInject"/> fails, write
/// the text to the clipboard. A toast announcing it is fired separately by
/// <c>PipelineHost</c> (so this class stays test-friendly).
/// </summary>
public sealed class ClipboardFallback
{
    private readonly IClipboard _clip;

    public ClipboardFallback(IClipboard clip)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
    }

    public bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        try { return _clip.SetText(text); }
        catch { return false; }
    }
}
