using System.Text;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiKeyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aai-key-{Guid.NewGuid():N}.dat");

    [Fact]
    public void SaveThenLoad_RoundTripsKey()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.HasKey.ShouldBeFalse();

        store.Save("secret-key-123");

        store.HasKey.ShouldBeTrue();
        store.Load().ShouldBe("secret-key-123");
    }

    [Fact]
    public void SavedFile_IsNotPlaintext()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.Save("secret-key-123");

        var onDisk = File.ReadAllBytes(_path);
        Encoding.UTF8.GetString(onDisk).ShouldNotContain("secret-key-123");
    }

    [Fact]
    public void Clear_RemovesKey()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.Save("secret-key-123");

        store.Clear();

        store.HasKey.ShouldBeFalse();
        store.Load().ShouldBeNull();
    }

    [Fact]
    public void Load_UndecryptableBlob_ReturnsNullInsteadOfThrowing()
    {
        // Simulate a DPAPI blob that cannot be decrypted (different user/machine,
        // or corruption): Unprotect throws CryptographicException. Load() must
        // degrade to "no usable key" so the app falls back to local + re-prompts.
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3, 4 });
        var store = new AssemblyAiKeyStore(_path, new ThrowingApiKeyProtector());

        store.Load().ShouldBeNull();
    }

    private sealed class ThrowingApiKeyProtector : IApiKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext)
            => throw new System.Security.Cryptography.CryptographicException("cannot decrypt");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
