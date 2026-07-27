#!/usr/bin/env bash
# Build the cleanup-LLM latency bench with the Windows dotnet over the
# \\wsl.localhost UNC path, stage the build output to a Windows-local %TEMP%
# dir (native library loads -- LLamaSharp Vulkan -- from UNC are unreliable),
# export the raw dictation statements from the app's history (READ-ONLY),
# run the latency scenario over them plus the committed eval case texts, and
# collect results into artifacts/cleanup-bench/<timestamp>/.
#
# Host safety: the only host writes are the %TEMP% staging/results dirs and
# NuGet restore. %LOCALAPPDATA%\winpepper is READ-ONLY to us: the exporter
# only File.ReadAllText's history\index.json, and the models tree is only read
# by the model load. Never touches a running Winpepper.exe. Raw transcripts
# only ever land in %TEMP% staging and gitignored artifacts/ (results.md is
# numbers/ids only; results.json carries the text and stays out of git).
#
# Usage: ./scripts/run-cleanup-bench-windows.sh [options]
#   --model <registry-key>   cleanup model to bench (default: registry cleanup default)
#   --passes N               passes per statement (default: 3)
#   --statements <wsl-path>  use an existing statements JSONL; skips the export step
#   --exec-timeout-s N       timeout for the latency run (default: 7200 -- 100+
#                            statements x 3 passes x up to 15 s can exceed an hour)
#   (any other args are passed through to the latency scenario)
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

MODEL=""
PASSES=3
STATEMENTS_WSL=""
EXEC_TIMEOUT=7200
PASSTHRU=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --model) MODEL="${2:?--model requires a value}"; shift 2 ;;
    --passes) PASSES="${2:?--passes requires a value}"; shift 2 ;;
    --statements) STATEMENTS_WSL="${2:?--statements requires a value}"; shift 2 ;;
    --exec-timeout-s) EXEC_TIMEOUT="${2:?--exec-timeout-s requires a value}"; shift 2 ;;
    *) PASSTHRU+=("$1"); shift ;;
  esac
done

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-cleanup-bench-windows: powershell.exe not found at $PS" >&2; exit 2; }
UNC_ROOT="$(wslpath -w "$HERE")"
STAMP="$(date +%Y%m%d-%H%M%S)"
OUT="$HERE/artifacts/cleanup-bench/$STAMP"
mkdir -p "$OUT"

if [[ -n "$STATEMENTS_WSL" ]]; then
  [[ -f "$STATEMENTS_WSL" ]] || { echo "run-cleanup-bench-windows: statements file not found: $STATEMENTS_WSL" >&2; exit 2; }
  STATEMENTS_WIN="$(wslpath -w "$STATEMENTS_WSL")"
else
  STATEMENTS_WIN=""   # resolved to the %TEMP% staging path inside the PS steps
fi

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/5] Pre-clean cross-OS bin/obj (CS0006 guard) ==="
rm -rf "$HERE"/scripts/cleanup-latency-bench/bin "$HERE"/scripts/cleanup-latency-bench/obj \
       "$HERE"/src/*/bin "$HERE"/src/*/obj

echo "=== [2/5] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\cleanup-latency-bench\CleanupLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [3/5] Stage bench output to %TEMP%\\winpepper-cleanup-bench ==="
bench_bin="$UNC_ROOT"'\scripts\cleanup-latency-bench\bin\Release\net9.0-windows10.0.19041.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-cleanup-bench'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

if [[ -n "$STATEMENTS_WSL" ]]; then
  echo "=== [4/5] Export skipped (using provided statements: $STATEMENTS_WSL) ==="
else
  echo "=== [4/5] Export statements from history (READ-ONLY) ==="
  # Writes statements.jsonl (real transcripts) into the %TEMP% staging dir --
  # transcripts never enter the repo except via gitignored artifacts/.
  ps_run 300 "$OUT/export.log" "
    Set-Location (Join-Path \$env:TEMP 'winpepper-cleanup-bench')
    \$hist = Join-Path \$env:LOCALAPPDATA 'winpepper\\history'
    \$stmts = Join-Path \$env:TEMP 'winpepper-cleanup-bench\\statements.jsonl'
    dotnet exec CleanupLatencyBench.dll export-statements --history-dir \$hist --out \$stmts"
fi

echo "=== [5/5] Latency run (passes=$PASSES, exec timeout ${EXEC_TIMEOUT}s) ==="
model_arg=""
[[ -n "$MODEL" ]] && model_arg="--model '$MODEL'"
# The bench exits non-zero when any statement FAILED (per-statement error rows)
# but still writes results.json/results.md first -- so collect results even on
# failure, then propagate the exit code.
latency_status=0
ps_run "$EXEC_TIMEOUT" "$OUT/latency.log" "
  \$res = Join-Path \$env:TEMP 'winpepper-cleanup-bench-results'
  if (Test-Path \$res) { Remove-Item -Recurse -Force \$res }
  Set-Location (Join-Path \$env:TEMP 'winpepper-cleanup-bench')
  \$stmts = '$STATEMENTS_WIN'
  if (-not \$stmts) { \$stmts = Join-Path \$env:TEMP 'winpepper-cleanup-bench\\statements.jsonl' }
  dotnet exec CleanupLatencyBench.dll latency --statements \$stmts --include-eval-cases \
    --models-root (Join-Path \$env:LOCALAPPDATA 'winpepper\\models') \
    --passes $PASSES $model_arg ${PASSTHRU[*]:-} --out \$res" || latency_status=$?

# Collect results back (results.json contains transcript text -- artifacts/ is gitignored).
# shellcheck disable=SC2016  # $env:TEMP is expanded by PowerShell, not bash
WIN_TEMP_WSL="$(wslpath "$("$PS" -NoProfile -Command 'Write-Output $env:TEMP' | tr -d '\r')")"
RESULTS_WSL="$WIN_TEMP_WSL/winpepper-cleanup-bench-results"
# A run that died before the write (bad model path, load crash) leaves NO
# results.json -- that must be a loud failure, never a silent pass.
if [[ ! -f "$RESULTS_WSL/results.json" ]]; then
  echo "run-cleanup-bench-windows: FAILED -- expected $RESULTS_WSL/results.json but no results were produced." >&2
  echo "run-cleanup-bench-windows: the latency run died before writing results; check $OUT/latency.log (missing gguf? bad --model key? empty statements?)." >&2
  if [[ "$latency_status" -ne 0 ]]; then exit "$latency_status"; fi
  exit 3
fi
cp -r "$RESULTS_WSL/." "$OUT/"
if [[ "$latency_status" -ne 0 ]]; then
  echo "run-cleanup-bench-windows: latency run reported failed statements (exit $latency_status) -- results still collected in $OUT; see latency.log and results.md" >&2
  exit "$latency_status"
fi
echo "run-cleanup-bench-windows: done -- results in $OUT (results.md, results.json), logs alongside"
