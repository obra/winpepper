#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet over the \\wsl.localhost
# UNC path, stage it to a Windows-local %TEMP% dir (native library loads from
# UNC are unreliable), run the corpus eval scenario against an exported corpus
# of dictation clips + reference transcripts, and collect results into
# artifacts/asr-eval/.
#
# Host safety: only host writes are the %TEMP% staging/results dirs and NuGet
# restore. Reads (never writes) the corpus dir and the app-installed nemotron
# model and runtime under %LOCALAPPDATA%\winpepper\models (the canonical tree,
# read-only to us; NEM_MODEL/NEM_RUNTIME env overrides are the escape hatch).
# Never touches a running Winpepper.exe or any other %LOCALAPPDATA%\winpepper data.
#
# Usage: ./scripts/run-asr-eval-windows.sh <corpus-dir-wsl> [repeats]
#   e.g. ./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
# Env overrides (Windows paths): NEM_MODEL, NEM_RUNTIME
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORPUS_WSL="${1:?usage: run-asr-eval-windows.sh <corpus-dir> [repeats]}"
REPEATS="${2:-1}"
[[ -f "$CORPUS_WSL/manifest.json" ]] || { echo "run-asr-eval-windows: no manifest.json in $CORPUS_WSL" >&2; exit 2; }

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-asr-eval-windows: powershell.exe not found at $PS" >&2; exit 2; }
UNC_ROOT="$(wslpath -w "$HERE")"
CORPUS_WIN="$(wslpath -w "$CORPUS_WSL")"
NEM_MODEL="${NEM_MODEL:-C:\\Users\\dan\\AppData\\Local\\winpepper\\models\\nemotron-streaming-en\\nemotron-speech-streaming-en-0.6b-Q8_0.gguf}"
NEM_RUNTIME="${NEM_RUNTIME:-C:\\Users\\dan\\AppData\\Local\\winpepper\\models\\nemotron-streaming-en\\runtime\\transcribe-native-windows-x86_64-cpu-vulkan}"
OUT="$HERE/artifacts/asr-eval"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Pre-clean cross-OS bin/obj (CS0006 guard) ==="
rm -rf "$HERE"/scripts/asr-latency-bench/bin "$HERE"/scripts/asr-latency-bench/obj \
       "$HERE"/src/*/bin "$HERE"/src/*/obj

echo "=== [2/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [3/4] Stage bench output to %TEMP%\\winpepper-asr-eval ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-asr-eval'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [4/4] Run the corpus eval (repeats=$REPEATS) ==="
# The bench exits non-zero when any clip FAILED (per-clip error rows) but still
# writes results.json/results.md first -- so collect results even on failure,
# then propagate the exit code.
corpus_status=0
ps_run 7200 "$OUT/corpus.log" "
  \$res = Join-Path \$env:TEMP 'winpepper-asr-eval-results'
  if (Test-Path \$res) { Remove-Item -Recurse -Force \$res }
  Set-Location (Join-Path \$env:TEMP 'winpepper-asr-eval')
  dotnet exec AsrLatencyBench.dll corpus --corpus '$CORPUS_WIN' \
    --nemotron-model '$NEM_MODEL' --nemotron-runtime '$NEM_RUNTIME' \
    --repeats $REPEATS --out \$res" || corpus_status=$?

# Collect results back (results.json contains transcript text -- artifacts/ is gitignored).
WIN_TEMP_WSL="$(wslpath "$("$PS" -NoProfile -Command 'Write-Output $env:TEMP' | tr -d '\r')")"
cp -r "$WIN_TEMP_WSL/winpepper-asr-eval-results/." "$OUT/"
if [[ "$corpus_status" -ne 0 ]]; then
  echo "run-asr-eval-windows: corpus eval reported failed clips (exit $corpus_status) -- results still collected in $OUT; see corpus.log and results.md" >&2
  exit "$corpus_status"
fi
echo "run-asr-eval-windows: done -- results in $OUT (results.md, results.json), logs alongside"
