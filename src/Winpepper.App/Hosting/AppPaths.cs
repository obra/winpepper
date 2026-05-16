namespace Winpepper.App.Hosting;

public static class AppPaths
{
    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Root => Path.Combine(LocalAppData, "winpepper");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ParakeetModelDir => Path.Combine(Root, "models", "parakeet-tdt-0.6b-v3");
    public static string SettingsJson => Path.Combine(Root, "settings.json");
    public static string CorrectionsJson => Path.Combine(Root, "corrections.json");
    public static string CleanupSettingsJson => Path.Combine(Root, "cleanup-settings.json");

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");
    public static string HistoryRoot => Path.Combine(Root, "history");
    public static string CrashesDir => Path.Combine(Root, "crashes");
}
