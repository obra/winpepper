#if WINDOWS
using System.Security.Cryptography;
using Winpepper.Asr.Transcription;

namespace Winpepper.App.Asr;

/// <summary>
/// Thin IApiKeyProtector over Windows DPAPI (CurrentUser scope). The ciphertext
/// is bound to the current Windows user account and cannot be decrypted by other
/// users or on other machines.
/// </summary>
public sealed class DpapiApiKeyProtector : IApiKeyProtector
{
    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext)
        => ProtectedData.Unprotect(ciphertext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
}
#endif
