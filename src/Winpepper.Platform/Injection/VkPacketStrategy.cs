namespace Winpepper.Platform.Injection;

/// <summary>
/// Rung 3, the status-quo floor (design doc §2.2): EXACTLY today's
/// SendInput KEYEVENTF_UNICODE chunk send. This class only WRAPS the
/// existing send delegate (TextInjector wires its _sendChunk — default
/// SendChunkViaSendInput — in), so behavior stays byte-identical and the
/// sendChunk ctor seam keeps working for every existing test. Gate: always
/// true. SendInput is focus-routed by the OS, so targetHwnd is unused.
/// </summary>
internal sealed class VkPacketStrategy : IDeliveryStrategy
{
    private readonly Func<string, bool> _sendChunk;

    public VkPacketStrategy(Func<string, bool> sendChunk) => _sendChunk = sendChunk;

    public DeliveryChannel Channel => DeliveryChannel.VkPacket;

    public bool CanDeliver(long foregroundHwnd, long focusedChildHwnd) => true;

    public bool TrySendChunk(long targetHwnd, string chunk) => _sendChunk(chunk);
}
