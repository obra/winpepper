namespace Winpepper.Core;

public static class AboutText
{
    public const string Title = "Winpepper";

    public static string Body() =>
        $"Winpepper local dictation for Windows.\n" +
        $"Version {BuildSignature.Describe()}\n" +
        $"Companion to pepper-x (Linux/GNOME).\n" +
        $"Local-only. No cloud, no telemetry.";
}
