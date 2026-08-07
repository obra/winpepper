using System.Text;

namespace Winpepper.Platform.Injection;

/// <summary>The winning strategy for one run plus the gates record
/// ("<rung>:<reason>" comma-list; empty when the first rung
/// delivered) — design doc §2.4 provenance, no readback, no verdicts.</summary>
internal readonly record struct DeliverySelection(IDeliveryStrategy Strategy, string GatesSummary);

/// <summary>
/// The ladder walk (design doc §2.3): in configured order, the FIRST rung
/// whose gate passes delivers the WHOLE run. Gates run once, before any
/// text is sent. No scoring, no heuristics, no app lists, no mid-run
/// re-routing. Because the pinned IDeliveryStrategy gate returns only
/// bool, the gate-out reason vocabulary lives HERE: "focus-unstable" when
/// the capture was unstable/zero, else "no-em" (the only stable-focus
/// gate-out is rung 1's class/EM_GETSEL predicate).
/// </summary>
internal static class DeliveryLadder
{
    public const string ReasonNoEm = "no-em";
    public const string ReasonFocusUnstable = "focus-unstable";

    public static DeliverySelection Select(
        IReadOnlyList<DeliveryChannel> order,
        IReadOnlyList<IDeliveryStrategy> strategies,
        long foregroundHwnd,
        FocusedChildCapture capture)
    {
        var gates = new StringBuilder();
        foreach (var channel in order)
        {
            var strategy = Find(strategies, channel);
            if (strategy is null) continue; // channel configured but not registered
            if (strategy.CanDeliver(foregroundHwnd, capture.FocusedChildHwnd))
                return new DeliverySelection(strategy, gates.ToString());
            if (gates.Length > 0) gates.Append(',');
            gates.Append(InjectionChannelNames.Name(channel))
                 .Append(':')
                 .Append(capture.Stable ? ReasonNoEm : ReasonFocusUnstable);
        }

        // The configured ladder exhausted without a passing gate — only
        // possible when settings removed vkPacket (its gate is always
        // true). Degrade to the status-quo floor rather than silently
        // dropping the run (design doc §3: "rungs degrade to the VK_PACKET
        // floor = status quo").
        var floor = Find(strategies, DeliveryChannel.VkPacket)
            ?? throw new InvalidOperationException(
                "Delivery ladder exhausted and no VkPacket floor strategy is registered");
        return new DeliverySelection(floor, gates.ToString());
    }

    private static IDeliveryStrategy? Find(
        IReadOnlyList<IDeliveryStrategy> strategies, DeliveryChannel channel)
    {
        for (var i = 0; i < strategies.Count; i++)
        {
            if (strategies[i].Channel == channel) return strategies[i];
        }
        return null;
    }
}
