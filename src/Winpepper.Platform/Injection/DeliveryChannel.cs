namespace Winpepper.Platform.Injection;

/// <summary>
/// Injection delivery channels (design doc 2026-08-06 §2.2). VkPacket is
/// deliberately 0 so a default(InjectionRunReport).Via reads as the
/// status-quo floor. Ladder order is NOT this declaration order — it comes
/// from settings via InjectionChannelNames.ParseLadder.
/// </summary>
public enum DeliveryChannel
{
    /// <summary>Rung 3: today's SendInput KEYEVENTF_UNICODE path (status-quo floor; gate always passes).</summary>
    VkPacket = 0,

    /// <summary>Rung 1: one EM_REPLACESEL per chunk via SendMessageTimeout (edit-class targets).</summary>
    EmReplaceSel = 1,

    /// <summary>Rung 2: one WM_CHAR per UTF-16 code unit via SendMessageTimeout (SMTO_ABORTIFHUNG).</summary>
    WmCharSmto = 2,
}
