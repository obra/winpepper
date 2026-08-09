#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet (over the
# \\wsl.localhost UNC path), stage the build output to a Windows-local %TEMP%
# dir (ONNX/DirectML native library loads from UNC are unreliable), generate
# the reference TTS WAVs on the host, and run real Parakeet model batch
# transcription over the four phrase categories. Streaming evidence comes
# from run-nemotron-bench-windows.sh (real-nemotron-stream).
#
# Host safety: the only host writes are %TEMP% staging dirs and NuGet
# restore. The model dir is read, never written. Never touches a running
# Winpepper.exe or %LOCALAPPDATA%\winpepper.
#
# Usage: ./scripts/run-bench-windows.sh
# Output: artifacts/bench/<category>.log
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-bench-windows: powershell.exe not found at $PS" >&2; exit 2; }

UNC_ROOT="$(wslpath -w "$HERE")"
MODEL_DIR='C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3'
OUT="$HERE/artifacts/bench"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [2/4] Stage bench output to %TEMP%\\winpepper-bench ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-bench'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [3/4] Generate TTS WAVs on the host ==="
gen_script="$UNC_ROOT"'\scripts\generate-bench-wavs.ps1'
ps_run 300 "$OUT/tts.log" "& '$gen_script' -OutDir (Join-Path \$env:TEMP 'winpepper-bench-wavs')"

echo "=== [4/4] real-local batch, four phrase categories ==="
run_category() { # run_category <name> <wavfile> [extra bench args...]
  local name="$1" wav="$2"; shift 2
  echo "--- $name ---"
  ps_run 1800 "$OUT/$name.log" "
    Set-Location (Join-Path \$env:TEMP 'winpepper-bench')
    dotnet exec AsrLatencyBench.dll real-local --model-dir '$MODEL_DIR' --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\$wav') $*"
}

run_category normal        normal-10s.wav
run_category pause-mid     pause-mid.wav
run_category quiet         normal-10s.wav --gain 0.02
run_category lead-silence  normal-10s.wav --lead-silence-ms 1500

echo "=== Cloud (AssemblyAI) check ==="
key_probe="
  \$k = \$env:ASSEMBLYAI_API_KEY
  if (-not \$k) { \$k = [Environment]::GetEnvironmentVariable('ASSEMBLYAI_API_KEY','User') }
  if (-not \$k) { \$k = [Environment]::GetEnvironmentVariable('ASSEMBLYAI_API_KEY','Machine') }"
if ps_run 60 "$OUT/cloud-check.log" "$key_probe
  if (\$k) { exit 0 } else { exit 1 }"; then
  echo "--- cloud (real speech WAV) ---"
  ps_run 1800 "$OUT/cloud.log" "$key_probe
    \$env:ASSEMBLYAI_API_KEY = \$k
    Set-Location (Join-Path \$env:TEMP 'winpepper-bench')
    dotnet exec AsrLatencyBench.dll real-remote-batch real-remote-stream --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\normal-10s.wav')"
else
  echo "cloud: ASSEMBLYAI_API_KEY not set on the host in any scope -- cloud rows NOT RUN (record honestly in the evidence doc)"
fi

echo "run-bench-windows: done -- logs in $OUT"
