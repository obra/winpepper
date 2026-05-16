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
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 410_000_000,
                    },
                    new ModelFile
                    {
                        RelativePath = "decoder_joint-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 18_000_000,
                    },
                    new ModelFile
                    {
                        RelativePath = "vocab.txt",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 50_000,
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
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 398_000_000,
                    },
                },
            },
        };
    }

    public IReadOnlyList<ModelDescriptor> All => _all;

    public IEnumerable<ModelDescriptor> ByKind(ModelKind kind) => _all.Where(d => d.Kind == kind);

    public ModelDescriptor? Find(string name) => _all.FirstOrDefault(d => d.Name == name);
}
