using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 1 (design doc §2.2, validated E6+E9a): one EM_REPLACESEL per chunk
/// via SendMessageTimeout (150 ms). Gate: focused-child class contains
/// "edit" (case-insensitive) AND a side-effect-free SMTO EM_GETSEL probe
/// answers AND the capture was stable (encoded as focusedChildHwnd != 0).
/// Fastest rung (~2x VK_PACKET) and immune to the cold-Notepad async-drop
/// class because delivery is synchronous.
/// </summary>
internal sealed class EmReplaceSelStrategy : IDeliveryStrategy
{
    private readonly ILogger _log;
    private readonly Func<long, string?> _className;
    private readonly Func<long, bool> _emGetSelProbe;
    private readonly Func<long, string, bool> _sendReplaceSel;

    public EmReplaceSelStrategy(
        ILogger log,
        Func<long, string?>? className = null,
        Func<long, bool>? emGetSelProbe = null,
        Func<long, string, bool>? sendReplaceSel = null)
    {
        _log = log;
        _className = className ?? MessageDelivery.ClassName;
        _emGetSelProbe = emGetSelProbe ?? MessageDelivery.EmGetSelProbe;
        _sendReplaceSel = sendReplaceSel ?? MessageDelivery.SendReplaceSel;
    }

    public DeliveryChannel Channel => DeliveryChannel.EmReplaceSel;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
    {
        if (focusedChildHwnd == 0) return false; // unstable or no focused child
        var cls = _className(focusedChildHwnd);
        if (cls is null || !cls.Contains("edit", StringComparison.OrdinalIgnoreCase))
            return false;
        return _emGetSelProbe(focusedChildHwnd);
    }

    public bool TrySendChunk(long targetHwnd, string chunk)
    {
        if (_sendReplaceSel(targetHwnd, chunk)) return true;
        _log.LogWarning(
            "EM_REPLACESEL send refused or timed out (hwnd 0x{Hwnd:X}); stopping the run",
            targetHwnd);
        return false;
    }
}
