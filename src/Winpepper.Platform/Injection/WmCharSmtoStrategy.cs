using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 2 (design doc §2.2, validated E7+E9e): one WM_CHAR per UTF-16 code
/// unit via SendMessageTimeout (SMTO_ABORTIFHUNG, 150 ms). Synchronous
/// delivery survives the cold-Notepad class and is phantom-Ctrl-immune (no
/// translation step). Gate: focused child observable + stable (encoded as
/// focusedChildHwnd != 0). Cost honestly stated in the doc: ~0.8 s per 134
/// units on targets that reach this rung.
/// </summary>
internal sealed class WmCharSmtoStrategy : IDeliveryStrategy
{
    private readonly ILogger _log;
    private readonly Func<long, ushort, bool> _sendChar;

    public WmCharSmtoStrategy(ILogger log, Func<long, ushort, bool>? sendChar = null)
    {
        _log = log;
        _sendChar = sendChar ?? MessageDelivery.SendCharSmto;
    }

    public DeliveryChannel Channel => DeliveryChannel.WmCharSmto;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd)
        => focusedChildHwnd != 0;

    public bool TrySendChunk(long targetHwnd, string chunk)
    {
        for (var i = 0; i < chunk.Length; i++)
        {
            if (!_sendChar(targetHwnd, chunk[i]))
            {
                _log.LogWarning(
                    "WM_CHAR send refused or timed out at unit {Index}/{Count} (hwnd 0x{Hwnd:X}); stopping the run",
                    i, chunk.Length, targetHwnd);
                return false;
            }
        }
        return true;
    }
}
