namespace Winpepper.Core.Errors;

/// <summary>
/// Maps an <see cref="ErrorStage"/> to a navigation tag and human label used
/// by the tray error toast's "Open X" deep-link button. Spec §9.1.
/// </summary>
public static class ErrorDeepLink
{
    public static string NavigationTagFor(ErrorStage stage) => stage switch
    {
        ErrorStage.Audio     => "recording",
        ErrorStage.Asr       => "models",
        ErrorStage.Cleanup   => "cleanup",
        ErrorStage.OcrUia    => "cleanup",
        ErrorStage.Injection => "diagnostics",
        ErrorStage.Learning  => "corrections",
        ErrorStage.Models    => "models",
        ErrorStage.History   => "history",
        ErrorStage.Settings  => "recording",
        ErrorStage.Hotkey    => "recording",
        ErrorStage.Crash     => "diagnostics",
        ErrorStage.Unknown   => "diagnostics",
        _ => "diagnostics",
    };

    public static string ActionLabelFor(ErrorStage stage) => stage switch
    {
        ErrorStage.Audio     => "Open Recording settings",
        ErrorStage.Asr       => "Open Models tab",
        ErrorStage.Cleanup   => "Open Cleanup settings",
        ErrorStage.OcrUia    => "Open Cleanup settings",
        ErrorStage.Injection => "Open Diagnostics",
        ErrorStage.Learning  => "Open Corrections",
        ErrorStage.Models    => "Open Models tab",
        ErrorStage.History   => "Open History",
        ErrorStage.Settings  => "Open Recording settings",
        ErrorStage.Hotkey    => "Open Recording settings",
        ErrorStage.Crash     => "Open Diagnostics",
        ErrorStage.Unknown   => "Open Diagnostics",
        _ => "Open Diagnostics",
    };
}
