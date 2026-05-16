# Generates start.wav (440 Hz then 660 Hz, 75 ms each) and stop.wav (660 Hz then
# 440 Hz, 75 ms each) at 22050 Hz mono 16-bit PCM. Idempotent.
param(
  [string]$OutDir = "src\Winpepper.App\Assets"
)

function Write-Wav {
    param([string]$Path, [double[]]$Freqs)
    $sampleRate = 22050
    $perTone   = [int]($sampleRate * 0.075)
    $samples = New-Object System.Collections.Generic.List[Int16]
    foreach ($f in $Freqs) {
        for ($i = 0; $i -lt $perTone; $i++) {
            $env = if ($i -lt 200) { $i / 200.0 } elseif ($i -gt $perTone - 200) { ($perTone - $i) / 200.0 } else { 1.0 }
            $v = [Math]::Sin(2 * [Math]::PI * $f * $i / $sampleRate) * 0.4 * $env
            $samples.Add([int16][Math]::Round($v * 32767))
        }
    }
    $bytes = New-Object byte[] ($samples.Count * 2)
    [System.Buffer]::BlockCopy($samples.ToArray(), 0, $bytes, 0, $bytes.Length)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([byte[]][char[]]"RIFF")
    $w.Write([int32](36 + $bytes.Length))
    $w.Write([byte[]][char[]]"WAVE")
    $w.Write([byte[]][char[]]"fmt ")
    $w.Write([int32]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int32]$sampleRate); $w.Write([int32]($sampleRate * 2))
    $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([byte[]][char[]]"data"); $w.Write([int32]$bytes.Length)
    $w.Write($bytes); $w.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Wav "$OutDir\start.wav" @(440, 660)
Write-Wav "$OutDir\stop.wav"  @(660, 440)
Write-Host "Wrote $OutDir\start.wav and $OutDir\stop.wav"
