namespace Winpepper.Models;

/// <summary>
/// Hard-coded catalog of models Winpepper knows how to download. To add or
/// update a model, edit this class and rerun scripts/verify-model-hashes.ps1
/// to refresh the SHA-256 fields.
/// </summary>
public sealed class ModelRegistry
{
    public const string DefaultAsrName = "parakeet-tdt-0.6b-v3";
    public const string DefaultCleanupName = "qwen2.5-0.5b-instruct-q4_k_m";

    private readonly List<ModelDescriptor> _all;

    public ModelRegistry()
    {
        _all = new List<ModelDescriptor>
        {
            new ModelDescriptor
            {
                Name = DefaultAsrName,
                Kind = ModelKind.Asr,
                DisplayName = "Parakeet TDT v3 (0.6B, int8 ONNX)",
                InstallDirRelative = "parakeet-tdt-0.6b-v3",
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "encoder-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx",
                        Sha256 = "6139d2fa7e1b086097b277c7149725edbab89cc7c7ae64b23c741be4055aff09",
                        SizeBytes = 652_183_999,
                    },
                    new ModelFile
                    {
                        RelativePath = "decoder_joint-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx",
                        Sha256 = "eea7483ee3d1a30375daedc8ed83e3960c91b098812127a0d99d1c8977667a70",
                        SizeBytes = 18_202_004,
                    },
                    new ModelFile
                    {
                        RelativePath = "vocab.txt",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt",
                        Sha256 = "d58544679ea4bc6ac563d1f545eb7d474bd6cfa467f0a6e2c1dc1c7d37e3c35d",
                        SizeBytes = 93_939,
                    },
                },
            },
            new ModelDescriptor
            {
                Name = DefaultCleanupName,
                Kind = ModelKind.Cleanup,
                DisplayName = "Qwen 2.5 0.5B Instruct (Q4_K_M GGUF)",
                InstallDirRelative = Path.Combine("cleanup", "qwen2.5-0.5b-instruct-q4_k_m"),
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "qwen2.5-0.5b-instruct-q4_k_m.gguf",
                        Url = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf",
                        Sha256 = "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db",
                        SizeBytes = 491_400_032,
                    },
                },
            },
        };
    }

    public IReadOnlyList<ModelDescriptor> All => _all;

    public IEnumerable<ModelDescriptor> ByKind(ModelKind kind) => _all.Where(d => d.Kind == kind);

    public ModelDescriptor? Find(string name) => _all.FirstOrDefault(d => d.Name == name);
}
