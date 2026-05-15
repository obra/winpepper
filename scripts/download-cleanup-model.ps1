# Downloads the Qwen 2.5 0.5B Q4_K_M cleanup model to the standard winpepper location.
# Run via: ./scripts/winssh < scripts/download-cleanup-model.ps1

$dest = "$env:LOCALAPPDATA\winpepper\models\cleanup\qwen2.5-0.5b-instruct"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url  = "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"
$file = Join-Path $dest "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"

if (Test-Path $file) {
    Write-Host "Cleanup model already present: $file"
} else {
    Write-Host "Downloading cleanup model (~400 MB)..."
    Invoke-WebRequest -Uri $url -OutFile $file
}

Get-ChildItem $dest | Format-Table Name, Length
