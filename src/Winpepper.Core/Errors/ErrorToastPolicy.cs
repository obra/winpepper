namespace Winpepper.Core.Errors;

/// <summary>
/// Consumer-product toast policy: a toast interrupts the user, so it is only
/// shown when the USER can act on it. Everything else is still logged and
/// recorded on the ErrorBus ring for the Diagnostics page — just silently.
/// </summary>
public static class ErrorToastPolicy
{
    /// <summary>
    /// Whether an ErrorBus report at this stage warrants a toast.
    ///
    /// Toast (user action exists):
    ///   Asr/Models   — model missing/broken: user downloads or fixes config.
    ///   Settings     — bad configuration: user corrects it.
    ///   Hotkey       — binding problem: user re-records the hotkey.
    ///   Crash        — something is seriously wrong: user should know.
    ///
    /// Silent (self-healing or informational — no user action):
    ///   Audio        — capture faults auto-rebuild; the actionable case
    ///                  ("no audio captured in a dictation") has its own
    ///                  friendly toast at session end.
    ///   Injection    — the inject sites already show the friendlier
    ///                  "text is on your clipboard" toast; the bus report
    ///                  would be a duplicate.
    ///   Cleanup/OcrUia — quality degradations fall back automatically.
    ///   Learning     — background watcher; nothing to act on.
    ///   History      — archive hiccup; nothing to act on in the moment.
    ///   Unknown      — not actionable by definition; Diagnostics has it.
    /// </summary>
    public static bool ShouldToast(ErrorStage stage) => stage switch
    {
        ErrorStage.Asr => true,
        ErrorStage.Models => true,
        ErrorStage.Settings => true,
        ErrorStage.Hotkey => true,
        ErrorStage.Crash => true,
        _ => false,
    };
}
