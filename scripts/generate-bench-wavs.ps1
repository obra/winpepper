<#
.SYNOPSIS
Generate the reference speech WAVs (16 kHz mono 16-bit PCM) for the ASR
latency bench using built-in Windows TTS (System.Speech.Synthesis).

.DESCRIPTION
Writes two files into -OutDir:
  normal-10s.wav  -- a ~10 s continuous dictation phrase
  pause-mid.wav   -- a phrase with a 2.0 s mid-utterance pause (> 1.2 s;
                     exercises InteriorSilenceSkipper edge-keeping)
The quiet-talker and leading-silence phrase categories reuse normal-10s.wav
via the bench's --gain / --lead-silence-ms flags (deterministic transforms;
the bench prints RMS stats so the gain can be chosen honestly).

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\generate-bench-wavs.ps1 -OutDir $env:TEMP\winpepper-bench-wavs
#>
param([Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

function New-Wav([string]$Name, [System.Speech.Synthesis.PromptBuilder]$Prompt) {
    $path = Join-Path $OutDir $Name
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.Rate = 0
        $synth.SetOutputToWaveFile($path, $fmt)
        $synth.Speak($Prompt)
    }
    finally { $synth.Dispose() }
    $seconds = [math]::Round(((Get-Item $path).Length - 44) / 32000.0, 1)  # 16 kHz * 2 B/sample
    Write-Host "wrote $Name (~$seconds s)"
}

$normal = New-Object System.Speech.Synthesis.PromptBuilder
$normal.AppendText('Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.')
New-Wav 'normal-10s.wav' $normal

$pause = New-Object System.Speech.Synthesis.PromptBuilder
$pause.AppendText('Send the quarterly report to the finance team')
$pause.AppendBreak([TimeSpan]::FromSeconds(2.0))
$pause.AppendText('and schedule the follow up meeting for Thursday afternoon.')
New-Wav 'pause-mid.wav' $pause
