using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;
    private readonly Action<string>? _onError;

    public SettingsStore(string path, Action<string>? onError = null)
    {
        _path = path;
        _onError = onError;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            // A torn/corrupt file (e.g. after an MSI upgrade force-kill) must
            // NOT silently wipe every setting. Preserve it for diagnosis, then
            // fall back to defaults. Keep it simple: no partial salvage.
            BackupCorruptFile(ex);
            return new AppSettings();
        }
    }

    private void BackupCorruptFile(Exception ex)
    {
        var backup = $"{_path}.bad-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            File.Move(_path, backup);
        }
        catch (Exception moveEx)
        {
            _onError?.Invoke(
                $"settings.json was corrupt and could not be backed up: {moveEx.Message}");
            return;
        }

        _onError?.Invoke(
            $"settings.json was corrupt ({ex.Message}); backed up to " +
            $"{Path.GetFileName(backup)} and reset to defaults.");
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }
}
