using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>Stores/loads the AssemblyAI API key, encrypted at rest.</summary>
public interface IAssemblyAiKeyStore
{
    bool HasKey { get; }
    void Save(string apiKey);
    string? Load();
    void Clear();
}

/// <summary>
/// File-backed key store. The key is protected via <see cref="IApiKeyProtector"/>
/// and written to a single file (e.g. %LOCALAPPDATA%\winpepper\assemblyai.key.dat).
/// settings.json never contains the key — presence is derived from file existence.
/// </summary>
public sealed class AssemblyAiKeyStore : IAssemblyAiKeyStore
{
    private readonly string _path;
    private readonly IApiKeyProtector _protector;

    public AssemblyAiKeyStore(string filePath, IApiKeyProtector protector)
    {
        _path = filePath;
        _protector = protector;
    }

    public bool HasKey => File.Exists(_path);

    public void Save(string apiKey)
    {
        var parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var cipher = _protector.Protect(Encoding.UTF8.GetBytes(apiKey));
        File.WriteAllBytes(_path, cipher);
    }

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(_path));
            return Encoding.UTF8.GetString(plain);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // DPAPI CurrentUser blobs are non-portable: a key file from a different
            // user/machine (or a corrupt file) cannot be decrypted. Treat as "no usable
            // key" so the app degrades to local fallback and can re-prompt, rather than
            // throwing on every dictation attempt.
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
