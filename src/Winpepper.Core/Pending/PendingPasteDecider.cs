namespace Winpepper.Core.Pending;

/// <summary>Outcome of the inject-vs-hold decision.</summary>
public enum InjectionDecision
{
    /// <summary>Inject now, exactly as today (same target, or identity unknown).</summary>
    InjectNow,
    /// <summary>Focus moved to a different KNOWN target: hold the text as a pending paste.</summary>
    HoldPending,
}

/// <summary>
/// Pure decision: at injection time, is the same field still focused?
/// HoldPending is chosen ONLY when we positively know the target changed (both
/// snapshots valid and different). If either snapshot is unknown/invalid we
/// default to InjectNow so the common path keeps today's zero-behavior-change
/// semantics (we never regress into holding when we simply failed to capture).
/// </summary>
public static class PendingPasteDecider
{
    public static InjectionDecision Decide(InjectionTarget atStart, InjectionTarget atInject)
    {
        ArgumentNullException.ThrowIfNull(atStart);
        ArgumentNullException.ThrowIfNull(atInject);
        if (!atStart.IsValid || !atInject.IsValid) return InjectionDecision.InjectNow;
        return atStart.Matches(atInject)
            ? InjectionDecision.InjectNow
            : InjectionDecision.HoldPending;
    }
}
