using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Reversible byte transform standing in for Windows DPAPI in tests.</summary>
public sealed class FakeApiKeyProtector : IApiKeyProtector
{
    private const byte Mask = 0x5A;

    public byte[] Protect(byte[] plaintext) => Xor(plaintext);
    public byte[] Unprotect(byte[] ciphertext) => Xor(ciphertext);

    private static byte[] Xor(byte[] input)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) output[i] = (byte)(input[i] ^ Mask);
        return output;
    }
}
