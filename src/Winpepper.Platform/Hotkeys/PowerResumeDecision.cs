namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Pure decision for the suspend/resume callback: which PBT_* notification
/// types mean "the machine just came back" and therefore warrant a keyboard
/// hook reinstall.
///
/// Windows silently removes a WH_KEYBOARD_LL hook whenever its callback times
/// out (>=1000 ms cap on Win10 1709+), and never tells the owner. A
/// suspend/resume is the most PROBABLE occasion (the process is cold/paged
/// out when the first key arrives) - and matches the 2026-07-24 incident:
/// hotkey presses after resume produced ZERO log lines, and an app restart
/// fixed it - but it is NOT the only one, so this trigger is necessary, not
/// sufficient (see the hook heartbeat telemetry).
/// </summary>
public static class PowerResumeDecision
{
    public const uint PBT_APMSUSPEND         = 0x0004;
    public const uint PBT_APMRESUMESUSPEND   = 0x0007;
    public const uint PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const uint PBT_POWERSETTINGCHANGE = 0x8013;

    /// <summary>True when this PBT_* notification means the system resumed.</summary>
    public static bool IsResume(uint notificationType)
        => notificationType is PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC;
}
