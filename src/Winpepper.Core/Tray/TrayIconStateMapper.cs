using Winpepper.Core.ViewModels;

namespace Winpepper.Core.Tray;

public sealed record TrayIconState(string IconName, string Tooltip);

public static class TrayIconStateMapper
{
    /// <summary>
    /// Maps the session surface onto the tray icon. The tray is the PERSISTENT
    /// surface: an ongoing CONDITION (microphone unavailable, no usable speech
    /// model) lives here for exactly as long as it is true, which is why the
    /// status pill is allowed to retire after its attention-grab window.
    /// A live dictation still outranks it - while recording/transcribing the
    /// stage is the more useful signal, and Paused outranks everything.
    /// </summary>
    public static TrayIconState Map(SessionStage stage, string? lastErrorMessage, bool paused,
                                    string? activeConditionMessage = null)
    {
        if (paused) return new TrayIconState("AppIcon.ico", "Winpepper - Paused");

        if (!string.IsNullOrWhiteSpace(activeConditionMessage)
            && !SessionStages.IsDictationInFlight(stage))
            return new TrayIconState("AppIcon-Error.ico", $"Winpepper - {activeConditionMessage}");

        return stage switch
        {
            SessionStage.Recording    => new("AppIcon-Recording.ico", "Winpepper - Recording..."),
            SessionStage.Transcribing => new("AppIcon-Loading.ico",   "Winpepper - Transcribing..."),
            SessionStage.CleaningUp   => new("AppIcon-Loading.ico",   "Winpepper - Cleaning up..."),
            SessionStage.Injecting    => new("AppIcon-Loading.ico",   "Winpepper - Inserting..."),
            SessionStage.Error        => new("AppIcon-Error.ico",     $"Winpepper - Error: {lastErrorMessage ?? "see Diagnostics"}"),
            _                         => new("AppIcon.ico",           "Winpepper - Ready"),
        };
    }
}
