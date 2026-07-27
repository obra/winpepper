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

    /// <summary>
    /// Like <see cref="Load"/>, but reports whether the out value
    /// legitimately reflects the CURRENT state of the file on disk. Returns
    /// true when the file parsed OK; also true when the file is missing
    /// (defaults ARE the current state) or corrupt (the content is
    /// quarantined to a .bad-* backup first, so defaults are then the
    /// current state). Returns false when the file EXISTS but could not be
    /// READ (persistent IOException, UnauthorizedAccessException) — the out
    /// value is then a last-known/default fallback that must NOT be used as
    /// the base of a full-file rewrite.
    /// </summary>
    public bool TryLoadCurrent(out AppSettings settings)
    {
        // Deliberately NO File.Exists pre-check (unlike Load): File.Exists
        // is false for a path occupied by a directory, which would disguise
        // an unreadable path as "missing → defaults". Read directly and
        // classify by exception: truly-absent paths throw
        // FileNotFoundException / DirectoryNotFoundException — both
        // IOException SUBCLASSES, so they must be caught BEFORE the general
        // IOException handlers below.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path, System.Text.Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                _lastGood = loaded;
                settings = loaded;
                return true;
            }
            catch (FileNotFoundException)
            {
                settings = new AppSettings(); // no file: defaults ARE current
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                settings = new AppSettings(); // no parent dir yet (first run)
                return true;
            }
            catch (JsonException ex)
            {
                // Same quarantine as Load(): the corrupt content is preserved
                // in a .bad-* backup, so defaults are now the legitimate
                // current state and a rewrite cannot destroy evidence.
                BackupCorruptFile(ex);
                settings = new AppSettings();
                return true;
            }
            catch (IOException) when (attempt < 2)
            {
                // Same transient share/replace retry as Load().
                Thread.Sleep(15);
            }
            catch (IOException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed ({ex.Message}); skipping settings flush rather than rewriting from a stale base.");
                settings = _lastGood ?? new AppSettings();
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                _onError?.Invoke(
                    $"settings.json read failed ({ex.Message}); skipping settings flush rather than rewriting from a stale base.");
                settings = _lastGood ?? new AppSettings();
                return false;
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
