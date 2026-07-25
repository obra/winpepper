namespace Winpepper.Core.Errors;

/// <summary>
/// The two kinds of error the app surfaces (2026-07-24 stuck-pill incident).
///
///  * <see cref="Event"/>     - a fact about a PAST moment (injection failed,
///    cleanup fell back, this dictation captured no audio). It has no ongoing
///    validity, so it is only worth interrupting the user while a dictation is
///    in flight, and it self-clears shortly after.
///  * <see cref="Condition"/> - an ONGOING state (microphone unavailable, no
///    usable speech model). It is true over time, so it must stay surfaced
///    exactly as long as it is true and be cleared by a RECOVERY SUCCESS -
///    never by a timer.
/// </summary>
public enum ErrorKind
{
    Event,
    Condition,
}
