#!/usr/bin/env bash
# Multi-model ASR corpus eval driver: build the bench with the Windows dotnet over
# the \\wsl.localhost UNC path, stage to %TEMP%, run the corpus scenario for ONE
# model (one model per process -- transcribe.cpp pins one runtime dir per process),
# and collect results into artifacts/asr-eval/<model-name>/ so serial runs of
# different models never overwrite each other.
#
# Host safety: only host writes are %TEMP% staging/results dirs and NuGet restore.
# Reads (never writes) the corpus dir and the model dir.
#
# Usage: ./scripts/run-asr-model-eval-windows.sh <corpus-dir-wsl> --model-dir <wsl-dir> --model-name <name> \
#          [--language <code>] [--batch-only] [--time-budget-minutes N] [--min-passes N] [--max-passes N] [--max-clips N]
#   e.g. ./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
#          --model-dir /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b \
#          --model-name nemotron-3.5-asr-streaming-0.6b --language en-US --max-clips 5 --min-passes 1 --max-passes 1
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORPUS_WSL="${1:?usage: run-asr-model-eval-windows.sh <corpus-dir> --model-dir <dir> --model-name <name> [options]}"
shift

MODEL_DIR_WSL="" MODEL_NAME="" LANGUAGE="" BATCH_ONLY=0
TIME_BUDGET="" MIN_PASSES="" MAX_PASSES="" MAX_CLIPS=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model-dir) MODEL_DIR_WSL="$2"; shift 2 ;;
    --model-name) MODEL_NAME="$2"; shift 2 ;;
    --language) LANGUAGE="$2"; shift 2 ;;
    --batch-only) BATCH_ONLY=1; shift ;;
    --time-budget-minutes) TIME_BUDGET="$2"; shift 2 ;;
    --min-passes) MIN_PASSES="$2"; shift 2 ;;
    --max-passes) MAX_PASSES="$2"; shift 2 ;;
    --max-clips) MAX_CLIPS="$2"; shift 2 ;;
    *) echo "run-asr-model-eval-windows: unknown option $1" >&2; exit 2 ;;
  esac
done
[[ -n "$MODEL_DIR_WSL" && -n "$MODEL_NAME" ]] || { echo "run-asr-model-eval-windows: --model-dir and --model-name are required" >&2; exit 2; }
[[ -f "$CORPUS_WSL/manifest.json" ]] || { echo "run-asr-model-eval-windows: no manifest.json in $CORPUS_WSL" >&2; exit 2; }
[[ -d "$MODEL_DIR_WSL" ]] || { echo "run-asr-model-eval-windows: model dir not found: $MODEL_DIR_WSL" >&2; exit 2; }

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-asr-model-eval-windows: powershell.exe not found at $PS" >&2; exit 2; }
UNC_ROOT="$(wslpath -w "$HERE")"
CORPUS_WIN="$(wslpath -w "$CORPUS_WSL")"
MODEL_DIR_WIN="$(wslpath -w "$MODEL_DIR_WSL")"
OUT="$HERE/artifacts/asr-eval/$MODEL_NAME"
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
bench_csproj="$UNC_ROOT"'\\scripts\\asr-latency-bench\\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [3/4] Stage bench output to %TEMP%\\\\winpepper-asr-eval ==="
bench_bin="$UNC_ROOT"'\\scripts\\asr-latency-bench\\bin\\Release\\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-asr-eval'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [4/4] Run the corpus eval for model $MODEL_NAME ==="
BENCH_FLAGS="--corpus '$CORPUS_WIN' --model-dir '$MODEL_DIR_WIN' --model-name '$MODEL_NAME'"
[[ -n "$LANGUAGE" ]]    && BENCH_FLAGS+=" --language '$LANGUAGE'"
[[ "$BATCH_ONLY" -eq 1 ]] && BENCH_FLAGS+=" --batch-only"
[[ -n "$TIME_BUDGET" ]] && BENCH_FLAGS+=" --time-budget-minutes $TIME_BUDGET"
[[ -n "$MIN_PASSES" ]]  && BENCH_FLAGS+=" --min-passes $MIN_PASSES"
[[ -n "$MAX_PASSES" ]]  && BENCH_FLAGS+=" --max-passes $MAX_PASSES"
[[ -n "$MAX_CLIPS" ]]   && BENCH_FLAGS+=" --max-clips $MAX_CLIPS"
corpus_status=0
ps_run 7200 "$OUT/corpus.log" "
  \$res = Join-Path \$env:TEMP 'winpepper-asr-eval-results-$MODEL_NAME'
  if (Test-Path \$res) { Remove-Item -Recurse -Force \$res }
  Set-Location (Join-Path \$env:TEMP 'winpepper-asr-eval')
  dotnet exec AsrLatencyBench.dll corpus $BENCH_FLAGS --out \$res" || corpus_status=$?

# Collect results (results.json contains transcript text -- artifacts/ is gitignored).
WIN_TEMP_WSL="$(wslpath "$(wslpath "$PS" -NoProfile -Command 'Write-Output $env:TEMP' | tr -d '\r')")"
RESULTS_WSL="$WIN_TEMP_WSL/winpepper-asr-eval-results-$MODEL_NAME"
if [[ ! -f "$RESULTS_WSL/results.json" ]]; then
  echo "run-asr-model-eval-windows: FAILED -- expected $RESULTS_WSL/results.json but no results were produced." >&2
  echo "run-asr-model-eval-windows: run was skipped or died before writing results; check $OUT/corpus.log" >&2
  if [[ "$corpus_status" -ne 0 ]]; then exit "$corpus_status"; fi
  exit 3
fi
cp -r "$RESULTS_WSL/." "$OUT/"
if [[ "$corpus_status" -ne 0 ]]; then
  echo "run-asr-model-eval-windows: eval reported failed clips (exit $corpus_status) -- results still collected in $OUT" >&2
  exit "$corpus_status"
fi
echo "run-asr-model-eval-windows: done -- results in $OUT (results.md, results.json), logs alongside"
