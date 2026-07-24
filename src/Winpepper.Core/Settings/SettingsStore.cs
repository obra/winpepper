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
    private AppSettings? _lastGood;

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

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                _lastGood = loaded;
                return loaded;
            }
            catch (JsonException ex)
            {
                // A torn/corrupt file (e.g. after an MSI upgrade force-kill) must
                // NOT silently wipe every setting. Preserve it for diagnosis, then
                // fall back to defaults. Keep it simple: no partial salvage.
                BackupCorruptFile(ex);
                return new AppSettings();
            }
            catch (IOException) when (attempt < 2)
            {
                // Transient share/replace race: an atomic Save (MoveFileEx
                // REPLACE_EXISTING) can collide with this open read handle on
                // Windows. Brief retry, then fall back — a Load must never
                // throw into a dictation (HandleHotkey calls it per dictation).
                Thread.Sleep(15);
            }
            catch (IOException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed transiently ({ex.Message}); using last known settings.");
                return _lastGood ?? new AppSettings();
            }
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
