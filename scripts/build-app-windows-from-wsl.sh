#!/usr/bin/env bash
# Build Winpepper.App (Release, XAML exe compiler) on the Windows host from
# this WSL checkout — the single documented command for WSL-side app builds,
# hardened against the transient UNC ref-assembly races (kata gzcc).
#
#   Usage: scripts/build-app-windows-from-wsl.sh [--attempts N]   (default N=5)
#
# The build runs via powershell.exe interop over this checkout's
# \\wsl.localhost UNC path and layers three mitigations:
#
#   1. Always-on pre-clean: a WSL-side `rm -rf src/**/{bin,obj}` against the
#      effective root before every run. Cross-OS bin/obj state is not
#      shareable — stale Linux-built intermediates make Windows CS0006
#      ref-assembly failures deterministic (docs/testing-windows-from-wsl.md).
#      It is never silently skipped: the "clean build" evidence depends on it.
#   2. Single-node scheduling with the gate's exact flags
#      (-m:1 -p:UseSharedCompilation=false -p:UseXamlCompilerExecutable=true).
#      -m:1 schedules the whole project graph on one MSBuild node, so targets
#      run in strict dependency order and no two tool processes (per-project
#      csc.exe children, XamlCompiler.exe, the mt shim) probe or write the 9P
#      share concurrently inside the measured 5–43 ms cross-process
#      visibility-lag windows. Compiles still run out-of-process and -m:1
#      implies no timing guarantee — it is a risk reducer, which is why the
#      retry layer exists. UseSharedCompilation=false retires the long-lived
#      Roslyn server, removing cross-invocation server state from the retry
#      story. The flags are byte-identical to scripts/windows-gate.sh, so the
#      gate stays the canary for this recipe.
#   3. Bounded retry (default 5 attempts — the kata's recorded worst-case
#      transient chain) that fires ONLY on the observed transient signatures
#      (CS0006 | WMC1006 | unexpected network error). Any other failure stops
#      immediately, and timeouts are never retried (a wedged build needs
#      human eyes).
#
# Safety invariants (same as scripts/windows-gate.sh): never installs the
# MSI, never launches or kills Winpepper.exe, never writes
# %LOCALAPPDATA%\winpepper. After a timeout, `timeout` has killed only the
# WSL-side interop proxy — wedged Windows-side dotnet.exe children keep
# holding file locks — so the orphan cleanup lists dotnet.exe processes and
# kills only those whose FULL command line contains this checkout's full UNC
# path tag AND does not contain "<tag>\.worktrees\". Full path, never a
# basename: the bare name "winpepper" is a substring of every nested
# worktree path (...\winpepper\.worktrees\<other-agent>\...), so a basename
# filter could kill other agents' builds. Listing stays on the powershell
# side; the filter is bash-side and self-testable.
#
# Self-test seams (each changes nothing when unset):
#   WINPEPPER_APP_BUILD_CMD        command string run via bash -c instead of
#                                  the powershell build invocation; also skips
#                                  the WSL / powershell.exe / wslpath checks.
#   WINPEPPER_APP_ROOT_OVERRIDE    effective root for the pre-clean, the run
#                                  log dir, and the orphan-kill tag (only
#                                  meaningful with the build-cmd seam).
#   WINPEPPER_APP_BUILD_TIMEOUT_S  per-attempt timeout override (default 2400).
#   WINPEPPER_APP_ORPHAN_LIST_CMD  replaces the powershell process listing;
#                                  must print "<pid>\t<command line>" rows.
#   WINPEPPER_APP_ORPHAN_KILL_CMD  replaces the powershell per-PID kill; run
#                                  as bash -c "<cmd>" kill <pid> (pid = $1).
#
# Logs: every invocation gets a unique
#   <root>/artifacts/build-app-windows/run-<UTC-YYYYMMDDTHHMMSSZ>-<pid>/
# directory (printed at start and end) with attempt<N>.log per attempt, so
# evidence from consecutive runs never overwrites earlier runs. artifacts/ is
# gitignored.
#
# Exit: 0 = BUILD OK; 1 = build failed (non-transient, exhausted, or
# timeout); 2 = usage or environment error.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ROOT="${WINPEPPER_APP_ROOT_OVERRIDE:-$HERE}"

usage() { echo "Usage: scripts/build-app-windows-from-wsl.sh [--attempts N]" >&2; }

# --attempts parsing: a positive integer, and no trailing arguments. Plain
# if/then (never `[[ ]] && { }`) so a failed test routes to usage instead of
# tripping `set -e`.
ATTEMPTS=5
if [[ $# -eq 0 ]]; then
  :
elif [[ $# -eq 2 && $1 == "--attempts" && $2 =~ ^[0-9]+$ ]] && (( 10#$2 >= 1 )); then
  ATTEMPTS=$((10#$2))
else
  usage
  exit 2
fi

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
TIMEOUT_S="${WINPEPPER_APP_BUILD_TIMEOUT_S:-2400}"

if [[ -n "${WINPEPPER_APP_BUILD_CMD:-}" ]]; then
  # Self-test seam: replaces the build and skips the interop prereq checks.
  BUILD_CMD="$WINPEPPER_APP_BUILD_CMD"
else
  if [[ -z "${WSL_DISTRO_NAME:-}" ]]; then
    echo "build-app-windows-from-wsl: not running under WSL (WSL_DISTRO_NAME is unset)" >&2
    exit 2
  fi
  if [[ ! -x "$PS" ]]; then
    echo "build-app-windows-from-wsl: powershell.exe not found or not executable at $PS" >&2
    exit 2
  fi
  if ! command -v wslpath >/dev/null 2>&1; then
    echo "build-app-windows-from-wsl: wslpath is not resolvable on PATH" >&2
    exit 2
  fi
  UNC_ROOT="$(wslpath -w "$ROOT")"
  APP_PROJ="$UNC_ROOT"'\src\Winpepper.App\Winpepper.App.csproj'
  # %q-quote the real invocation into a re-evaluatable command string so both
  # the seam and the real path run through the same timeout wrapper.
  BUILD_CMD="$(printf '%q ' "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "dotnet build '$APP_PROJ' -c Release -m:1 -p:UseSharedCompilation=false -p:UseXamlCompilerExecutable=true; exit \$LASTEXITCODE")"
fi

RUN_DIR="$ROOT/artifacts/build-app-windows/run-$(date -u +%Y%m%dT%H%M%SZ)-$$"
mkdir -p "$RUN_DIR"
echo "build-app-windows-from-wsl: effective root: $ROOT"
echo "build-app-windows-from-wsl: run log dir: $RUN_DIR"

# Cross-OS hygiene pre-clean (mitigation 1): always runs, never skipped.
echo "build-app-windows-from-wsl: pre-clean: rm -rf $ROOT/src/*/bin $ROOT/src/*/obj"
rm -rf "$ROOT"/src/*/bin "$ROOT"/src/*/obj

# Checkout-scoped orphan cleanup for the timeout path. Listing is separated
# from filtering so the filter lives in bash and is self-testable.
kill_orphans() {
  local tag
  tag="$(wslpath -w "$ROOT" 2>/dev/null || true)"
  tag="${tag%$'\r'}"
  [[ -n "$tag" ]] || tag="$ROOT"   # Linux spelling as fallback
  local needle="$tag\\.worktrees\\"
  local rows
  if [[ -n "${WINPEPPER_APP_ORPHAN_LIST_CMD:-}" ]]; then
    rows="$(bash -c "$WINPEPPER_APP_ORPHAN_LIST_CMD" || true)"
  else
    # shellcheck disable=SC2016 # the $() and backtick-t are PowerShell-side, not bash
    rows="$("$PS" -NoProfile -Command 'Get-CimInstance Win32_Process -Filter "Name='"'"'dotnet.exe'"'"'" | ForEach-Object { "$($_.ProcessId)`t$($_.CommandLine)" }' 2>/dev/null || true)"
  fi
  local pid cmd
  while IFS=$'\t' read -r pid cmd; do
    pid="${pid%$'\r'}"
    cmd="${cmd%$'\r'}"
    [[ -n "$pid" && -n "$cmd" ]] || continue
    # Keep only rows for THIS checkout: the full effective-root tag ...
    [[ "$cmd" == *"$tag"* ]] || continue
    # ... and never a worktree nested under it (<tag>\.worktrees\...).
    [[ "$cmd" != *"$needle"* ]] || continue
    echo "build-app-windows-from-wsl: killing orphaned dotnet.exe PID $pid"
    if [[ -n "${WINPEPPER_APP_ORPHAN_KILL_CMD:-}" ]]; then
      bash -c "$WINPEPPER_APP_ORPHAN_KILL_CMD" kill "$pid" || true
    else
      "$PS" -NoProfile -Command "Stop-Process -Id $pid -Force" >/dev/null 2>&1 || true
    fi
  done <<< "$rows"
}

attempt=1
while true; do
  log="$RUN_DIR/attempt$attempt.log"
  echo "build-app-windows-from-wsl: attempt $attempt of $ATTEMPTS (log: $log)"
  rc=0
  timeout --foreground "$TIMEOUT_S" bash -c "$BUILD_CMD" >"$log" 2>&1 || rc=$?
  if [[ $rc -eq 0 ]]; then
    echo "BUILD OK on attempt $attempt (run log: $RUN_DIR)"
    exit 0
  fi
  if [[ $rc -eq 124 ]]; then
    echo "build-app-windows-from-wsl: attempt $attempt TIMEOUT after ${TIMEOUT_S}s — timeouts are not retried; running checkout-scoped orphan cleanup"
    kill_orphans
    echo "build-app-windows-from-wsl: aborting after TIMEOUT (run log: $RUN_DIR)" >&2
    exit 1
  fi
  sig="$(grep -m1 -oE 'CS0006|WMC1006|unexpected network error' "$log" | head -n1 || true)"
  if [[ -z "$sig" ]]; then
    echo "build-app-windows-from-wsl: attempt $attempt failed with a non-transient error (attempt log: $log)" >&2
    echo "build-app-windows-from-wsl: run log dir: $RUN_DIR" >&2
    exit 1
  fi
  if (( attempt >= ATTEMPTS )); then
    echo "build-app-windows-from-wsl: attempt $attempt failed with transient signature '$sig'; attempts exhausted"
    echo "build-app-windows-from-wsl: gave up after $ATTEMPTS attempts (run log: $RUN_DIR)" >&2
    exit 1
  fi
  echo "build-app-windows-from-wsl: attempt $attempt failed with transient signature '$sig'; retrying"
  attempt=$((attempt + 1))
done
