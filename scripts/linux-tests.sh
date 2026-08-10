#!/usr/bin/env bash
# Run the pure-managed (net9.0) test suite on Linux: build each of the 9 test
# projects -c Release, then run each via the xUnit v3 in-process runner
# (`dotnet exec <dll>`). Never `dotnet test` (VSTest host is unreliable).
# Green = every run exits 0 with "Errors: 0" and "Failed: 0".
# Usage: ./scripts/linux-tests.sh
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

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

for proj in "${PROJECTS[@]}"; do
  dotnet build "$HERE/tests/$proj/$proj.csproj" -c Release -f net9.0 -p:EnableWindowsTargeting=true
done

# Compile gate for the ASR latency bench: its Program.cs is in no test project
# (top-level statements), so without this build the bench can silently break.
dotnet build "$HERE/scripts/asr-latency-bench/AsrLatencyBench.csproj" -c Release --nologo -v q

fail=0
grand_total=0
for proj in "${PROJECTS[@]}"; do
  echo "=== $proj (net9.0) ==="
  rc=0
  out="$(dotnet exec "$HERE/tests/$proj/bin/Release/net9.0/$proj.dll" -notrait "Platform=Windows")" || rc=$?
  echo "$out" | tail -n 3
  line="$(grep -E 'Total:.*Errors:.*Failed:' <<<"$out" | tail -1 || true)"
  total="$(grep -oE 'Total: *[0-9]+' <<<"$line" | grep -oE '[0-9]+' || echo 0)"
  grand_total=$((grand_total + total))
  if [[ $rc -ne 0 ]] || ! grep -qE 'Errors: 0[^0-9]' <<<"$line" || ! grep -qE 'Failed: 0[^0-9]' <<<"$line"; then
    echo "RED: $proj (exit $rc) ${line:-<no summary line>}"
    fail=1
  fi
done

echo "linux-tests grand total: $grand_total tests"
if [[ $fail -ne 0 ]]; then echo "LINUX SUITE: RED"; exit 1; fi
echo "LINUX SUITE: GREEN"
