# Run: ./scripts/winssh < scripts/download-parakeet-v2.ps1
$dest = "$env:LOCALAPPDATA\winpepper\models\parakeet-tdt-0.6b-v2"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$base = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v2-onnx/resolve/main"
$files = @("encoder-model.int8.onnx", "decoder_joint-model.int8.onnx", "vocab.txt")
foreach ($f in $files) {
    $out = Join-Path $dest $f
    if (Test-Path $out) { Write-Host "skip $f (exists)"; continue }
    Write-Host "Downloading $f..."
    Invoke-WebRequest -Uri "$base/$f" -OutFile $out
}
Write-Host "Models in $dest"
Get-ChildItem $dest | Format-Table Name, Length
