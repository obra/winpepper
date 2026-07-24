# Computes SHA-256 hashes for every file declared in the ModelRegistry by hitting
# the HuggingFace direct-download URLs. Prints C# snippets that can be pasted into
# ModelRegistry.cs.
#
# Usage on the VM:  ./scripts/winssh < scripts/verify-model-hashes.ps1
# Usage on Linux:   pwsh scripts/verify-model-hashes.ps1  (requires pwsh installed)
$ErrorActionPreference = "Stop"

$files = @(
    @{ Name = "encoder-model.int8.onnx";       Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx" },
    @{ Name = "decoder_joint-model.int8.onnx"; Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx" },
    @{ Name = "vocab.txt";                     Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt" },
    @{ Name = "encoder-model.int8.onnx";       Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/encoder-model.int8.onnx" },
    @{ Name = "decoder_joint-model.int8.onnx"; Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/decoder_joint-model.int8.onnx" },
    @{ Name = "vocab.txt";                     Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main/vocab.txt" },
    @{ Name = "qwen2.5-0.5b-instruct-q4_k_m.gguf"; Url = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf" }
)

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "winpepper-verify-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    foreach ($f in $files) {
        $dest = Join-Path $tempDir $f.Name
        Write-Host "Downloading $($f.Name)..."
        Invoke-WebRequest -Uri $f.Url -OutFile $dest
        $hash = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item $dest).Length
        Write-Host "  Sha256 = `"$hash`""
        Write-Host "  SizeBytes = $size"
        Write-Host ""
    }
}
finally {
    Remove-Item -Recurse -Force $tempDir
}
