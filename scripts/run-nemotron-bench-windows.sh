#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet, stage it to a
# Windows-local %TEMP% dir (native library loads from UNC are unreliable),
# generate the reference TTS WAVs, and run real-nemotron-stream over the four
# phrase categories using the spike's already-downloaded model + runtime at
# %LOCALAPPDATA%\Temp\transcribe-spike (read-only reuse; the SHIPPED
# acquisition path still downloads fresh with pinned hashes).
#
# Host safety: only host writes are %TEMP% staging dirs and NuGet restore.
# Reads (never writes) the spike scratch and the installed TDT model dir.
# Never touches a running Winpepper.exe or %LOCALAPPDATA%\winpepper.
#
# Usage: ./scripts/run-nemotron-bench-windows.sh
# Output: artifacts/nemotron-bench/<category>.log
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-nemotron-bench-windows: powershell.exe not found at $PS" >&2; exit 2; }

UNC_ROOT="$(wslpath -w "$HERE")"
TDT_MODEL_DIR='C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3'
NEM_MODEL='C:\Users\dan\AppData\Local\Temp\transcribe-spike\nemotron-speech-streaming-en-0.6b-Q8_0.gguf'
NEM_RUNTIME='C:\Users\dan\AppData\Local\Temp\transcribe-spike\transcribe-native-windows-x86_64-cpu-vulkan'
OUT="$HERE/artifacts/nemotron-bench"
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

echo "=== [2/4] Stage bench output to %TEMP%\\winpepper-nemotron-bench ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-nemotron-bench'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [3/4] Generate TTS WAVs on the host ==="
gen_script="$UNC_ROOT"'\scripts\generate-bench-wavs.ps1'
ps_run 300 "$OUT/tts.log" "& '$gen_script' -OutDir (Join-Path \$env:TEMP 'winpepper-bench-wavs')"

echo "=== [4/4] real-nemotron-stream, four phrase categories ==="
run_category() { # run_category <name> <wav> [extra bench args...]
  local name="$1" wav="$2"; shift 2
  echo "--- $name ---"
  ps_run 1800 "$OUT/$name.log" "
    Set-Location (Join-Path \$env:TEMP 'winpepper-nemotron-bench')
    dotnet exec AsrLatencyBench.dll real-nemotron-stream \
      --nemotron-model '$NEM_MODEL' --nemotron-runtime '$NEM_RUNTIME' \
      --model-dir '$TDT_MODEL_DIR' \
      --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\$wav') $*"
}
run_category normal        normal-10s.wav
run_category pause-mid     pause-mid.wav
run_category quiet         normal-10s.wav --gain 0.02
run_category lead-silence  normal-10s.wav --lead-silence-ms 1500

echo "run-nemotron-bench-windows: done -- logs in $OUT"
