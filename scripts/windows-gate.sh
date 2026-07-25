#!/usr/bin/env bash
# Windows pre-push gate for winpepper, run from WSL.
#
# Runs on the Windows host via powershell.exe interop, over this checkout's
# \\wsl.localhost UNC path (requires the WSL2/UNC build support from commit
# 3b1903e: mt-unc-shim + RuntimeIdentifiers, merged from main):
#   [1/3] dotnet build src/Winpepper.App -c Release -p:UseXamlCompilerExecutable=true
#   [2/3] dotnet build all 9 test projects -c Release (dual-TFM projects build both)
#   [3/3] dotnet exec every project/TFM test DLL -- 12 runs, xUnit v3 in-process
#         (never `dotnet test`; the VSTest host is unreliable on some machines)
#
# Safety (the user's Winpepper may be RUNNING on the host):
#   - never installs the MSI
#   - never launches or kills Winpepper.exe
#   - never writes to %LOCALAPPDATA%\winpepper (tests read the models dir only)
#
# Known caveat: Hook_Installs_And_DisposesCleanly (Winpepper.Platform.Tests,
# windows TFM) hangs in headless sessions; it requires an interactive,
# unlocked desktop (verified interactive 2026-07-25). The per-run timeout
# below surfaces a hang as TIMEOUT instead of wedging the gate, and
# kill_orphans then removes this tree's orphaned dotnet.exe processes
# (validated: `timeout` kills only the interop proxy; Windows-side children
# survive holding file locks unless killed). Never kill Winpepper.exe.
#
# Accepted risk (validated 2026-07-25): the hook test installs a REAL
# WH_KEYBOARD_LL hook for ~200 ms whose test chord matches the app's toggle
# side-agnostically; a user keystroke in that window can be swallowed once.
# Kept anyway — it is the only real hook coverage; it changes no state.
#
# Cross-OS obj hygiene (validated 2026-07-25, first two gate runs): Linux
# builds share bin/obj with these UNC builds, and Windows builds against that
# stale state hit CS0006 (missing obj/**/ref/*.dll). --no-incremental does
# NOT fix it -- it maps to MSBuild's Rebuild, whose Clean transitively cleans
# referenced projects (CleanReferencedProjects), and across the gate's ten
# sequential entry builds a reference's ref assembly gets cleaned out from
# under consumers whose builds are then skipped as cached/up-to-date; the
# same project set failed identically on a multi-node run and a -m:1 run.
# Fix: delete the cross-OS src/**/{bin,obj} and tests/**/{bin,obj} up front
# (WSL side), then build plainly (no Rebuild): every project compiles exactly
# once from a clean slate and later stages reuse earlier outputs. Builds stay
# single-process (-m:1 -p:UseSharedCompilation=false) so every build-graph
# write and read happens in one process over the 9P share (conservatism, not
# a proven necessity). Never run linux-tests.sh concurrently with this gate;
# the pre-clean also means the next linux-tests.sh run rebuilds from scratch.
#
# Expected skips: Windows runs may report Skipped > 0 (Llama cleanup tests
# self-skip via Assert.SkipUnless when the qwen GGUF is absent on the host —
# it currently is). Skips keep the gate green; record them honestly in the
# evidence doc. Note the gate is CPU/RAM heavy — run it when the user isn't
# depending on a responsive host.
#
# Usage: ./scripts/windows-gate.sh
# Exit:  0 and "GATE: GREEN" iff the app builds and all 12 runs are green.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "windows-gate: powershell.exe not found at $PS" >&2; exit 2; }

UNC_ROOT="$(wslpath -w "$HERE")"
LOG_DIR="$HERE/artifacts/windows-gate"
mkdir -p "$LOG_DIR"

# Cross-OS hygiene pre-clean (see header): remove Linux-built intermediates so
# every Windows build below starts from a clean slate and compiles each
# project exactly once. rm -f tolerates non-matching globs.
echo "windows-gate: pre-clean src/**/{bin,obj} and tests/**/{bin,obj}"
rm -rf "$HERE"/src/*/bin "$HERE"/src/*/obj "$HERE"/tests/*/bin "$HERE"/tests/*/obj

BUILD_TIMEOUT=2400   # 40 min (first run restores NuGet over UNC)
TEST_TIMEOUT=1200    # 20 min per test run (hang guard)

run_ps() { # run_ps <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" > "$log" 2>&1
}

# `timeout` kills only the WSL-side interop proxy; Windows-side children
# survive and can hold file locks that wedge later stages (verified). After
# any TIMEOUT, kill orphaned dotnet.exe processes whose command line points
# at THIS checkout — never anything else, never Winpepper.exe.
kill_orphans() {
  "$PS" -NoProfile -Command \
    "Get-CimInstance Win32_Process -Filter \"Name='dotnet.exe'\" | Where-Object { \$_.CommandLine -like '*streaming-verification*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }" \
    >/dev/null 2>&1 || true
}

PROJECTS=(
  Winpepper.Asr.Tests
  Winpepper.Audio.Tests
  Winpepper.Cleanup.Tests
  Winpepper.Core.Tests
  Winpepper.Corrections.Tests
  Winpepper.History.Tests
  Winpepper.IntegrationTests
  Winpepper.Models.Tests
  Winpepper.Platform.Tests
)

# 9 projects, 3 dual-TFM (Audio, Cleanup, Platform) => 12 runs.
RUNS=(
  "Winpepper.Asr.Tests|net9.0"
  "Winpepper.Audio.Tests|net9.0"
  "Winpepper.Audio.Tests|net9.0-windows10.0.19041.0"
  "Winpepper.Cleanup.Tests|net9.0"
  "Winpepper.Cleanup.Tests|net9.0-windows10.0.19041.0"
  "Winpepper.Core.Tests|net9.0"
  "Winpepper.Corrections.Tests|net9.0"
  "Winpepper.History.Tests|net9.0"
  "Winpepper.IntegrationTests|net9.0"
  "Winpepper.Models.Tests|net9.0"
  "Winpepper.Platform.Tests|net9.0"
  "Winpepper.Platform.Tests|net9.0-windows10.0.19041.0"
)

fail=0
summary=()

host_dotnet="$("$PS" -NoProfile -Command 'dotnet --version' 2>/dev/null | tr -d '\r' || true)"
echo "windows-gate: host dotnet ${host_dotnet:-<unknown>}"
echo "windows-gate: UNC root $UNC_ROOT"

echo "=== [1/3] Build Winpepper.App (Release, XAML exe compiler) ==="
app="$UNC_ROOT"'\src\Winpepper.App\Winpepper.App.csproj'
if run_ps "$BUILD_TIMEOUT" "$LOG_DIR/app-build.log" \
     "dotnet build '$app' -c Release -m:1 -p:UseSharedCompilation=false -p:UseXamlCompilerExecutable=true"; then
  summary+=("Winpepper.App build: OK")
else
  rc=$?
  [[ $rc -eq 124 ]] && kill_orphans
  summary+=("Winpepper.App build: FAILED (exit $rc$([[ $rc -eq 124 ]] && echo ', TIMEOUT' || true)) -- $LOG_DIR/app-build.log")
  fail=1
fi

echo "=== [2/3] Build the 9 test projects (Release, all TFMs) ==="
for proj in "${PROJECTS[@]}"; do
  csproj="$UNC_ROOT"'\tests\'"$proj"'\'"$proj"'.csproj'
  if run_ps "$BUILD_TIMEOUT" "$LOG_DIR/build-$proj.log" "dotnet build '$csproj' -c Release -m:1 -p:UseSharedCompilation=false"; then
    echo "  built $proj"
  else
    rc=$?
    [[ $rc -eq 124 ]] && kill_orphans
    summary+=("$proj build: FAILED (exit $rc) -- $LOG_DIR/build-$proj.log")
    fail=1
  fi
done

echo "=== [3/3] Run the 12 project/TFM test DLLs ==="
grand_total=0
for entry in "${RUNS[@]}"; do
  proj="${entry%%|*}"; tfm="${entry##*|}"
  dll_dir="$UNC_ROOT"'\tests\'"$proj"'\bin\Release\'"$tfm"
  log="$LOG_DIR/run-$proj-$tfm.log"
  echo "  running $proj ($tfm) ..."
  rc=0
  run_ps "$TEST_TIMEOUT" "$log" \
    "Set-Location '$dll_dir'; dotnet exec '$dll_dir\\$proj.dll'" || rc=$?
  line="$(grep -E 'Total:.*Errors:.*Failed:' "$log" | tail -1 | tr -d '\r' || true)"
  total="$(grep -oE 'Total: *[0-9]+' <<<"$line" | grep -oE '[0-9]+' || echo 0)"
  grand_total=$((grand_total + total))
  if [[ $rc -eq 124 ]]; then
    kill_orphans
    summary+=("$proj ($tfm): TIMEOUT after ${TEST_TIMEOUT}s (likely hang; Hook_Installs_And_DisposesCleanly needs an interactive desktop; orphaned dotnet.exe for this tree killed) -- $log")
    fail=1
  elif [[ $rc -ne 0 ]] || ! grep -qE 'Errors: 0[^0-9]' <<<"$line" || ! grep -qE 'Failed: 0[^0-9]' <<<"$line"; then
    summary+=("$proj ($tfm): FAILED (exit $rc) ${line:-<no summary line>} -- $log")
    fail=1
  else
    summary+=("$proj ($tfm): OK  $line")
  fi
done

echo
echo "================ windows-gate summary ================"
printf '%s\n' "${summary[@]}"
echo "grand total tests: $grand_total (cross-check only; roughly ~1300+ across 12 runs -- record the actual number)"
if [[ $fail -ne 0 ]]; then
  echo "GATE: RED"
  exit 1
fi
echo "GATE: GREEN"
