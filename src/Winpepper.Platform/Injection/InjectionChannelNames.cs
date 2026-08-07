namespace Winpepper.Platform.Injection;

/// <summary>
/// Single source of truth for the channel-name vocabulary shared by
/// settings ("injectionChannels"), the inject_via= / inject_gates=
/// telemetry, and log lines. Canonical spellings are camelCase per the
/// design doc §2.3; parsing is case-insensitive. Unknown names are
/// reported to the caller (which logs a warning) and skipped; an empty or
/// fully-invalid list falls back to the hardcoded default ladder.
/// </summary>
public static class InjectionChannelNames
{
    public const string EmReplaceSelName = "emReplaceSel";
    public const string WmCharSmtoName = "wmCharSmto";
    public const string VkPacketName = "vkPacket";

    /// <summary>Hardcoded default ladder order (design doc §5 decision 1).</summary>
    public static readonly IReadOnlyList<DeliveryChannel> DefaultLadder = new[]
    {
        DeliveryChannel.EmReplaceSel,
        DeliveryChannel.WmCharSmto,
        DeliveryChannel.VkPacket,
    };

    public static string Name(DeliveryChannel channel) => channel switch
    {
        DeliveryChannel.EmReplaceSel => EmReplaceSelName,
        DeliveryChannel.WmCharSmto => WmCharSmtoName,
        _ => VkPacketName,
    };

    public static bool TryParse(string? name, out DeliveryChannel channel)
    {
        if (string.Equals(name, EmReplaceSelName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.EmReplaceSel;
            return true;
        }
        if (string.Equals(name, WmCharSmtoName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.WmCharSmto;
            return true;
        }
        if (string.Equals(name, VkPacketName, StringComparison.OrdinalIgnoreCase))
        {
            channel = DeliveryChannel.VkPacket;
            return true;
        }
        channel = default;
        return false;
    }

    /// <summary>
    /// Parse a settings-supplied channel-name list into a ladder order.
    /// Unknown names invoke <paramref name="onUnknown"/> and are skipped;
    /// duplicates keep the first occurrence (gates run once per rung per
    /// run); null/empty/fully-invalid input yields <see cref="DefaultLadder"/>.
    /// </summary>
    public static IReadOnlyList<DeliveryChannel> ParseLadder(
        IReadOnlyList<string>? names, Action<string>? onUnknown = null)
    {
        if (names is null || names.Count == 0) return DefaultLadder;
        var result = new List<DeliveryChannel>(names.Count);
        foreach (var name in names)
        {
            if (TryParse(name, out var channel))
            {
                if (!result.Contains(channel)) result.Add(channel);
            }
            else
            {
                onUnknown?.Invoke(name ?? "<null>");
            }
        }
        return result.Count == 0 ? DefaultLadder : result;
    }
}
