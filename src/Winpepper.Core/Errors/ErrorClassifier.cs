namespace Winpepper.Core.Errors;

/// <summary>
/// Pure EVENT-vs-CONDITION classification for every <see cref="ErrorBus"/>
/// report. See <see cref="ErrorKind"/> for the taxonomy.
///
/// GOVERNING RULE: a stage is a CONDITION only when a RECOVERY SUCCESS signal
/// exists that can clear it. A condition with no clearing signal is a permanent
/// error surface - precisely the defect this taxonomy fixes.
///
/// Per stage:
///   Audio      - CONDITION only for <see cref="MicrophoneUnavailableException"/>
///                (the warm capture stream is down until a rebuild succeeds).
///                Every other Audio report is the per-dictation "no audio
///                detected" EVENT raised after a session ends.
///   Asr        - CONDITION: "no usable speech model" is an ongoing state,
///                cleared when a model actually loads. Every Asr report site
///                denotes exactly this state: the two sites that did not
///                (the per-attempt AssemblyAI config rejection, and the model
///                swap that keeps the old working session) are re-staged to
///                Models AT the report site - see the plan's Load-Bearing
///                Taxonomy Decision table.
///   Models     - EVENT: each report is one attempt that failed - a
///                user-initiated verify/download, a cloud (AssemblyAI) config
///                rejection after which the dictation succeeded via local
///                fallback, or a swap that kept the old working model. The
///                ongoing missing-model state is reported at the Asr stage.
///   Cleanup    - EVENT: quality degradation that already fell back.
///   Injection  - EVENT: that paste attempt failed (pending-paste covers it).
///   OcrUia     - EVENT: that context extraction degraded.
///   Learning   - EVENT: a background watcher hiccup.
///   History    - EVENT: that archive write hiccupped.
///   Settings   - EVENT: no recovery signal exists to clear it.
///   Hotkey     - EVENT: that chord-recording attempt failed.
///   Crash      - EVENT: a crash that already happened.
///   Unknown    - EVENT: not actionable by definition.
/// </summary>
public static class ErrorClassifier
{
    private static readonly string MicrophoneUnavailableTypeName =
        typeof(MicrophoneUnavailableException).FullName!;

    public static ErrorKind Classify(ErrorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Classify(record.Stage, record.ExceptionType);
    }

    public static ErrorKind Classify(ErrorStage stage, string exceptionType) => stage switch
    {
        ErrorStage.Audio when exceptionType == MicrophoneUnavailableTypeName => ErrorKind.Condition,
        ErrorStage.Asr => ErrorKind.Condition,
        _ => ErrorKind.Event,
    };
}
