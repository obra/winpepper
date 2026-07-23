namespace Winpepper.Asr.Transcription;

/// <summary>
/// Encrypts/decrypts small secrets at rest. The Windows implementation wraps
/// DPAPI (ProtectedData, CurrentUser scope); tests inject a reversible fake.
/// </summary>
public interface IApiKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
