#!/usr/bin/env bash
# Run the FULL Windows test suite (all 9 test projects, Windows TFMs included)
# from inside WSL2 by driving the Windows-host .NET SDK against this checkout's
# UNC path (\\wsl.localhost\<distro>\...). See docs/testing-windows-from-wsl.md.
#
# Usage: scripts/test-windows-from-wsl.sh [--no-clean]
#
#   --no-clean   Skip wiping src/ and tests/ bin+obj first. Only safe if the
#                previous build in this tree was ALSO a Windows-side build.
#                Default is to clean: mixing Linux- and Windows-side builds in
#                the same bin/obj corrupts incremental state (MSBuild
#                IncrementalClean deletes fresh outputs -> CS0006).
#
# Exit code: 0 = every project built and every test run passed; 1 otherwise.
set -u

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="/mnt/c/Program Files/dotnet/dotnet.exe"
WIN_TFM="net9.0-windows10.0.19041.0"
CLEAN=1
[ "${1:-}" = "--no-clean" ] && CLEAN=0

fail() { echo "FATAL: $*" >&2; exit 1; }

# ---------------------------------------------------------------- prereqs ---
[ -n "${WSL_DISTRO_NAME:-}" ] || fail "not running inside WSL (WSL_DISTRO_NAME unset)"
[ -x "$DOTNET_EXE" ] || fail "Windows .NET SDK not found at $DOTNET_EXE (install .NET 9 SDK on the Windows host)"
"$DOTNET_EXE" --version >/dev/null 2>&1 || fail "WSL interop cannot launch dotnet.exe (is interop enabled in /etc/wsl.conf?)"
command -v wslpath >/dev/null || fail "wslpath not available"

cd "$REPO_ROOT" || fail "cannot cd to $REPO_ROOT"

# ------------------------------------------------------------------ clean ---
if [ "$CLEAN" = 1 ]; then
    echo "== Cleaning bin/ and obj/ under src/ and tests/ (cross-OS build state is not shareable)"
    find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
fi

# ---------------------------------------------------------- build + run -----
projects=(tests/*/)
[ "${#projects[@]}" -eq 9 ] || echo "WARNING: expected 9 test projects, found ${#projects[@]}" >&2

declare -a results
overall=0
start_all=$(date +%s)

for proj in "${projects[@]}"; do
    name=$(basename "$proj")
    csproj="$proj$name.csproj"
    [ -f "$csproj" ] || fail "missing $csproj"

    echo
    echo "== [$name] build -c Release (Windows SDK, UNC path)"
    if ! "$DOTNET_EXE" build "$(wslpath -w "$csproj")" -c Release; then
        results+=("$name: BUILD FAILED")
        overall=1
        continue
    fi

    # Prefer the Windows TFM output when the project multi-targets.
    dll="$proj/bin/Release/$WIN_TFM/$name.dll"
    [ -f "$dll" ] || dll="$proj/bin/Release/net9.0/$name.dll"
    [ -f "$dll" ] || { results+=("$name: BUILT BUT NO TEST DLL FOUND"); overall=1; continue; }

    echo "== [$name] dotnet.exe exec $(basename "$(dirname "$dll")")/$name.dll"
    out=$("$DOTNET_EXE" exec "$(wslpath -w "$dll")" 2>&1)
    rc=$?
    echo "$out"
    summary=$(echo "$out" | grep -Eo 'Total:[^|]*(\|[^|]*)*' | tail -1)
    if [ $rc -eq 0 ]; then
        results+=("$name [$(basename "$(dirname "$dll")")]: PASS  ${summary:-}")
    else
        results+=("$name [$(basename "$(dirname "$dll")")]: FAIL (exit $rc)  ${summary:-}")
        overall=1
    fi
done

# ---------------------------------------------------------------- summary ---
echo
echo "=== Windows-from-WSL full suite summary ($(( $(date +%s) - start_all ))s total) ==="
printf '%s\n' "${results[@]}"
if [ $overall -ne 0 ]; then
    echo "RESULT: FAIL"
    exit 1
fi
echo "RESULT: PASS (all ${#projects[@]} projects green on Windows)"
exit 0
