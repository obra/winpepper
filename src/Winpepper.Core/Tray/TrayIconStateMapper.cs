using Winpepper.Core.ViewModels;

namespace Winpepper.Core.Tray;

public sealed record TrayIconState(string IconName, string Tooltip);

public static class TrayIconStateMapper
{
    public static TrayIconState Map(SessionStage stage, string? lastErrorMessage, bool paused)
    {
        if (paused) return new TrayIconState("AppIcon.ico", "Winpepper - Paused");

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
