#!/usr/bin/env bash
# Self-test for scripts/build-app-windows-from-wsl.sh — pure bash, no Windows
# interop. Drives the wrapper through its documented self-test seams:
# WINPEPPER_APP_BUILD_CMD (fake build commands) plus a disposable
# WINPEPPER_APP_ROOT_OVERRIDE (mktemp dir with a placeholder
# src/Winpepper.App/Winpepper.App.csproj), and the timeout /
# orphan-list / orphan-kill seams for the timeout case.
#
# Prints one line per case; exits 0 only when every case passes:
#   SELFTEST: PASS
#
# Cases (per docs/plans/2026-08-11-gzcc-unc-build-race.md, Task 1):
#   1 transient-then-success at the --attempts boundary: CS0006 on calls 1–4,
#     OK on call 5 with --attempts 5 → exit 0, "BUILD OK on attempt 5",
#     exactly four "transient signature" lines.
#   2 exhaustion: WMC1006 on every call → exit 1 after exactly 5 attempts in
#     one unique run dir, naming that dir.
#   3 non-transient: CS1234 → exit 1 after exactly 1 attempt, no retry line,
#     naming attempt1.log.
#   4 transport signature: "An unexpected network error occurred" x2 then OK
#     → exit 0 on attempt 3.
#   5 clean first try → exit 0 on attempt 1, no retry lines.
#   6 timeout + kill scoping: sleep-based fake under
#     WINPEPPER_APP_BUILD_TIMEOUT_S=2 → exit 124 path; the orphan filter
#     normalizes '\' to '/' before comparing and must kill only the row whose
#     CommandLine carries this tree's full tag followed by a separator —
#     rejecting a different checkout under the parent's .worktrees, nested
#     <tag>/.worktrees/ rows in both homogeneous slash spellings, BOTH
#     mixed-separator spellings, and a prefix-named sibling <tag>2/...).
#   7 pre-clean always runs against the effective root: seeded
#     src/Seed/{bin,obj} sentinels must be gone before the fake build runs.
#   8 usage validation: "--attempts 0", "--attempts abc", a trailing positional,
#     and a set-but-empty WINPEPPER_APP_ROOT_OVERRIDE each exit 2 without
#     running a build (the empty-override guard keeps a failed mktemp upstream
#     from ever pointing the pre-clean at the real checkout).
#   9 logging integrity: a PATH-shadowed failing tee with a 0-exit build must
#     exit 1 with a logging-integrity message and never print BUILD OK.
#  10 cleanup path is timeout-capped: build times out (124) and the orphan LIST
#     command hangs (`sleep 120`, capped at 60 s by the wrapper) — the wrapper
#     must still reach its exit 1 TIMEOUT result rather than hang forever.
#  11 timeout-override validation: option-like/nonnumeric/zero
#     WINPEPPER_APP_BUILD_TIMEOUT_S values (--help, --version, -k, abc, 0)
#     must exit 2 with usage and never run or certify a build (a leading '-'
#     lands in GNU timeout's option position and would exit 0 without
#     building); a valid override still builds.
#  12 hanging orphan KILL is capped: build times out (124), the orphan list
#     returns one matching row promptly, and the kill seam hangs
#     (`sleep 120`, capped at 30 s by the wrapper) — the kill path must be
#     reached (kill line printed for the fake PID) and the wrapper must still
#     reach exit 1 TIMEOUT in bounded time.
# Cases 1, 2 and 5 also assert run-dir uniqueness across two invocations.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WRAPPER="$HERE/build-app-windows-from-wsl.sh"

failures=0
roots=()
ok()  { printf 'case %s: PASS — %s\n' "$1" "$2"; }
bad() { printf 'case %s: FAIL — %s\n' "$1" "$2" >&2; failures=$((failures + 1)); }

make_root() {
  # Fail hard and loudly: an empty result must never flow into
  # WINPEPPER_APP_ROOT_OVERRIDE (the wrapper also defends itself, case-hardened).
  local r
  if ! r="$(mktemp -d /tmp/winpepper-app-build-selftest.XXXXXX)"; then
    echo "SELFTEST SETUP FAILURE: mktemp -d failed" >&2
    exit 1
  fi
  if [[ -z "$r" || "$r" != /tmp/* ]]; then
    echo "SELFTEST SETUP FAILURE: suspicious root '$r'" >&2
    exit 1
  fi
  mkdir -p "$r/src/Winpepper.App" || { echo "SELFTEST SETUP FAILURE: mkdir $r" >&2; exit 1; }
  printf '<Project Sdk="Microsoft.NET.Sdk" />\n' >"$r/src/Winpepper.App/Winpepper.App.csproj" || { echo "SELFTEST SETUP FAILURE: seed csproj" >&2; exit 1; }
  printf '%s\n' "$r"
}

run_dirs_count() { find "$1/artifacts/build-app-windows" -mindepth 1 -maxdepth 1 -type d -name 'run-*' 2>/dev/null | wc -l || true; }
n_attempt_logs() { find "$1" -maxdepth 1 -type f -name 'attempt*.log' 2>/dev/null | wc -l || true; }
run_dir_from() { grep -m1 -oE '/[^ ]*/artifacts/build-app-windows/run-[^ )]+' "$1" 2>/dev/null | head -n1 || true; }
transient_lines() { grep -c 'transient signature' "$1" 2>/dev/null || true; }

# --- Case 1: transient x4 then success at the --attempts 5 boundary ----------
r="$(make_root)"; roots+=("$r")
state="$r/calls"
fake="n=\$(cat \"$state\" 2>/dev/null || echo 0); n=\$((n + 1)); printf '%s\n' \"\$n\" >\"$state\"; if [ \"\$n\" -le 4 ]; then echo 'fake error CS0006: metadata file not found'; exit 1; fi; exit 0"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
rc2=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out2" 2>&1 || rc2=$?
d1="$(run_dir_from "$r/out1")"; d2="$(run_dir_from "$r/out2")"
t1="$(transient_lines "$r/out1")"; nd="$(run_dirs_count "$r")"
if [[ $rc1 -eq 0 && $rc2 -eq 0 ]] \
  && grep -q 'BUILD OK on attempt 5' "$r/out1" \
  && [[ "$t1" -eq 4 && -n "$d1" && -n "$d2" && "$d1" != "$d2" && "$nd" -eq 2 ]]; then
  ok 1 "CS0006 on attempts 1–4 then BUILD OK on attempt 5 (--attempts 5 boundary); 4 transient lines; unique run dirs"
else
  bad 1 "rc1=$rc1 rc2=$rc2 transient=$t1(want 4) rundirs=$nd(want 2) d1=${d1:-none} d2=${d2:-none} | $(grep -m1 -E 'BUILD OK|gave up|non-transient|No such file' "$r/out1" || true)"
fi

# --- Case 2: exhaustion — all 5 attempts transient ---------------------------
r="$(make_root)"; roots+=("$r")
fake="echo 'fake error WMC1006: XAML compiler failed'; exit 1"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" >"$r/out1" 2>&1 || rc1=$?
rc2=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" >"$r/out2" 2>&1 || rc2=$?
d1="$(run_dir_from "$r/out1")"; d2="$(run_dir_from "$r/out2")"
logs1="$(n_attempt_logs "${d1:-/nonexistent}")"; nd="$(run_dirs_count "$r")"
if [[ $rc1 -eq 1 && $rc2 -eq 1 && "$logs1" -eq 5 && "$nd" -eq 2 && -n "$d1" && "$d1" != "$d2" ]] \
  && grep -qF "$d1" "$r/out1"; then
  ok 2 "WMC1006 on every attempt → exit 1 after exactly 5 attempts, run dir named, run dirs unique across invocations"
else
  bad 2 "rc1=$rc1 rc2=$rc2 attemptlogs=$logs1(want 5) rundirs=$nd(want 2) d1=${d1:-none} d2=${d2:-none} | $(tail -n1 "$r/out1" 2>/dev/null || true)"
fi

# --- Case 3: non-transient failure — no retry --------------------------------
r="$(make_root)"; roots+=("$r")
fake="echo 'error CS1234: this failure is permanent'; exit 1"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
d1="$(run_dir_from "$r/out1")"
logs1="$(n_attempt_logs "${d1:-/nonexistent}")"
t1="$(transient_lines "$r/out1")"
if [[ $rc1 -eq 1 && "$logs1" -eq 1 && "$t1" -eq 0 && -n "$d1" ]] \
  && [[ -f "$d1/attempt1.log" ]] \
  && grep -q 'non-transient' "$r/out1" && grep -q 'attempt1.log' "$r/out1"; then
  ok 3 "permanent CS1234 → exit 1 after exactly 1 attempt (attempt1.log only), no retry line, attempt log named"
else
  bad 3 "rc1=$rc1(want 1) attemptlogs=$logs1(want 1) transient=$t1(want 0) d1=${d1:-none} | $(tail -n1 "$r/out1" 2>/dev/null || true)"
fi

# --- Case 4: transport signature is retryable --------------------------------
r="$(make_root)"; roots+=("$r")
state="$r/calls"
fake="n=\$(cat \"$state\" 2>/dev/null || echo 0); n=\$((n + 1)); printf '%s\n' \"\$n\" >\"$state\"; if [ \"\$n\" -le 2 ]; then echo 'XamlCompiler: Failed to write output file: An unexpected network error occurred'; exit 1; fi; exit 0"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
t1="$(transient_lines "$r/out1")"
if [[ $rc1 -eq 0 && "$t1" -eq 2 ]] && grep -q 'BUILD OK on attempt 3' "$r/out1"; then
  ok 4 "'An unexpected network error occurred' x2 then success → BUILD OK on attempt 3 (transport signature matched, not permanent)"
else
  bad 4 "rc1=$rc1(want 0) transient=$t1(want 2) | $(grep -m1 -E 'BUILD OK|gave up|non-transient|No such file' "$r/out1" || true)"
fi

# --- Case 5: clean first try --------------------------------------------------
r="$(make_root)"; roots+=("$r")
fake="exit 0"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
rc2=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out2" 2>&1 || rc2=$?
d1="$(run_dir_from "$r/out1")"; d2="$(run_dir_from "$r/out2")"
t1="$(transient_lines "$r/out1")"; nd="$(run_dirs_count "$r")"
if [[ $rc1 -eq 0 && $rc2 -eq 0 && "$t1" -eq 0 && "$nd" -eq 2 && -n "$d1" && "$d1" != "$d2" ]] \
  && grep -q 'BUILD OK on attempt 1' "$r/out1"; then
  ok 5 "immediate success → exit 0 on attempt 1, no retry lines, unique run dirs"
else
  bad 5 "rc1=$rc1 rc2=$rc2 transient=$t1(want 0) rundirs=$nd(want 2) | $(grep -m1 -E 'BUILD OK|No such file' "$r/out1" || true)"
fi

# --- Case 6: TIMEOUT path + orphan-kill scoping ------------------------------
r="$(make_root)"; roots+=("$r")
tag="$(wslpath -w "$r" 2>/dev/null || printf '%s' "$r")"
tag="${tag%$'\r'}"
parent="${tag%\\*}"
printf '%s\t%s\n%s\t%s\n%s\t%s\n%s\t%s\n%s\t%s\n%s\t%s\n%s\t%s\n' \
  1111 "dotnet build ${tag}\\src\\Winpepper.App\\Winpepper.App.csproj -c Release" \
  2222 "dotnet build ${parent}\\.worktrees\\other-agent\\src\\Other\\Other.csproj" \
  3333 "dotnet build ${tag}\\.worktrees\\nested-agent\\src\\Nested\\Nested.csproj" \
  4444 "dotnet build ${tag}2\\src\\PrefixSibling\\PrefixSibling.csproj" \
  5555 "dotnet build ${tag}/.worktrees/forward-nested/src/Nested/Nested.csproj" \
  6666 "dotnet build ${tag}/.worktrees\\mixed-a\\src\\Mixed\\Mixed.csproj" \
  7777 "dotnet build ${tag}\\.worktrees/mixed-b/src/Mixed/Mixed.csproj" \
  >"$r/procs.tsv"
rc1=0
WINPEPPER_APP_BUILD_CMD="sleep 30" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  WINPEPPER_APP_BUILD_TIMEOUT_S=2 \
  WINPEPPER_APP_ORPHAN_LIST_CMD="cat \"$r/procs.tsv\"" \
  WINPEPPER_APP_ORPHAN_KILL_CMD="printf '%s\n' \"\$1\" >> \"$r/killed\"" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
d1="$(run_dir_from "$r/out1")"
logs1="$(n_attempt_logs "${d1:-/nonexistent}")"
t1="$(transient_lines "$r/out1")"
killed="$(cat "$r/killed" 2>/dev/null || true)"
if [[ $rc1 -eq 1 && "$logs1" -eq 1 && "$t1" -eq 0 && "$killed" == "1111" ]] \
  && grep -q 'TIMEOUT' "$r/out1" \
  && grep -q 'killing orphaned dotnet.exe PID 1111' "$r/out1"; then
  ok 6 "2s timeout on a sleeping build → TIMEOUT stop (exit 1, 1 attempt, no retry); separator-normalized full-path filter killed only this tree's PID 1111 (parent .worktrees, nested .worktrees in both slash spellings, both mixed-separator spellings, and prefix-sibling rows all rejected)"
else
  bad 6 "rc1=$rc1(want 1) attemptlogs=$logs1(want 1) transient=$t1(want 0) killed='${killed:-<none>}'(want exactly 1111) | $(grep -m1 -E 'TIMEOUT|No such file' "$r/out1" || true)"
fi

# --- Case 7: pre-clean always runs against the effective root ----------------
r="$(make_root)"; roots+=("$r")
mkdir -p "$r/src/Seed/bin" "$r/src/Seed/obj"
touch "$r/src/Seed/bin/sentinel" "$r/src/Seed/obj/sentinel"
fake="if [ -e \"$r/src/Seed/bin/sentinel\" ] || [ -e \"$r/src/Seed/obj/sentinel\" ]; then echo 'PRE-CLEAN MISS: sentinel still present'; exit 7; fi; exit 0"
rc1=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
if [[ $rc1 -eq 0 && ! -e "$r/src/Seed/bin" && ! -e "$r/src/Seed/obj" ]] \
  && grep -q 'pre-clean' "$r/out1"; then
  ok 7 "seeded src/Seed/{bin,obj} sentinels were gone at build-call time and stay gone — pre-clean ran against the effective root"
else
  bad 7 "rc1=$rc1(want 0) bin-exists=$([[ -e $r/src/Seed/bin ]] && echo yes || echo no) obj-exists=$([[ -e $r/src/Seed/obj ]] && echo yes || echo no) | $(grep -m1 -E 'PRE-CLEAN MISS|BUILD OK|No such file' "$r/out1" || true)"
fi

# --- Case 8: usage validation -------------------------------------------------
r="$(make_root)"; roots+=("$r")
marker="$r/build-ran"
fake="touch \"$marker\"; exit 0"
rc_a=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 0 >"$r/out_a" 2>&1 || rc_a=$?
rc_b=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts abc >"$r/out_b" 2>&1 || rc_b=$?
rc_c=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 3 extra >"$r/out_c" 2>&1 || rc_c=$?
rc_d=0
WINPEPPER_APP_BUILD_CMD="$fake" WINPEPPER_APP_ROOT_OVERRIDE="" \
  bash "$WRAPPER" >"$r/out_d" 2>&1 || rc_d=$?
usage='Usage: scripts/build-app-windows-from-wsl.sh \[--attempts N\]'
if [[ $rc_a -eq 2 && $rc_b -eq 2 && $rc_c -eq 2 && $rc_d -eq 2 && ! -e "$marker" ]] \
  && grep -q "$usage" "$r/out_a" && grep -q "$usage" "$r/out_b" && grep -q "$usage" "$r/out_c" \
  && grep -q 'WINPEPPER_APP_ROOT_OVERRIDE is empty or not a directory' "$r/out_d"; then
  ok 8 "'--attempts 0', '--attempts abc', trailing positional, and a set-but-empty root override each print usage/error and exit 2; no build ran"
else
  bad 8 "rc_a=$rc_a rc_b=$rc_b rc_c=$rc_c rc_d=$rc_d(want 2 2 2 2) marker=$([[ -e $marker ]] && echo present || echo absent)"
fi

# --- Case 9: logging integrity — a failing tee must never certify a run -------
r="$(make_root)"; roots+=("$r")
mkdir -p "$r/fakebin"
printf '#!/usr/bin/env bash\ncat > /dev/null\nexit 1\n' >"$r/fakebin/tee"
chmod +x "$r/fakebin/tee"
rc1=0
PATH="$r/fakebin:$PATH" WINPEPPER_APP_BUILD_CMD="exit 0" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
if [[ $rc1 -eq 1 ]] && ! grep -q 'BUILD OK' "$r/out1" \
  && grep -q 'tee .* failed' "$r/out1"; then
  ok 9 "PATH-shadowed failing tee with a 0-exit build → exit 1, no BUILD OK, logging-integrity message (a build can never certify past a lost attempt log)"
else
  bad 9 "rc1=$rc1(want 1) | $(grep -m1 -E 'BUILD OK|tee|logging' "$r/out1" || true)"
fi

# --- Case 10: cleanup path is timeout-capped (no hang on stalled interop) -----
r="$(make_root)"; roots+=("$r")
start=$SECONDS
rc1=0
WINPEPPER_APP_BUILD_CMD="sleep 30" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  WINPEPPER_APP_BUILD_TIMEOUT_S=2 \
  WINPEPPER_APP_ORPHAN_LIST_CMD="sleep 120" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
elapsed=$((SECONDS - start))
if [[ $rc1 -eq 1 && $elapsed -lt 100 ]] \
  && grep -q 'TIMEOUT' "$r/out1"; then
  ok 10 "build 124 + orphan list hanging (sleep 120 capped at 60 s) → wrapper reached exit 1 TIMEOUT in ${elapsed}s (<100 s) instead of hanging"
else
  bad 10 "rc1=$rc1(want 1) elapsed=${elapsed}s(want <100) | $(grep -m1 -E 'TIMEOUT|No such file' "$r/out1" || true)"
fi

# --- Case 11: timeout-override validation (option injection can never certify) -
r="$(make_root)"; roots+=("$r")
bad11=""
for t in --help --version -k abc 0; do
  rc1=0
  WINPEPPER_APP_BUILD_CMD="echo NEVER-RAN; exit 0" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
    WINPEPPER_APP_BUILD_TIMEOUT_S="$t" \
    bash "$WRAPPER" --attempts 1 >"$r/out_$t" 2>&1 || rc1=$?
  if [[ $rc1 -ne 2 ]] || grep -q 'NEVER-RAN\|BUILD OK' "$r/out_$t"; then
    bad11="$bad11 $t(rc=$rc1)"
  fi
done
rc1=0
WINPEPPER_APP_BUILD_CMD="echo RAN-FINE; exit 0" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  WINPEPPER_APP_BUILD_TIMEOUT_S=30 \
  bash "$WRAPPER" --attempts 1 >"$r/out_ok" 2>&1 || rc1=$?
if [[ -z "$bad11" && $rc1 -eq 0 ]] && grep -q 'BUILD OK on attempt 1' "$r/out_ok"; then
  ok 11 "--help/--version/-k/abc/0 timeout overrides each exit 2 without running or certifying a build; valid override 30 builds green"
else
  bad 11 "bad:$bad11 valid-override rc=$rc1(want 0) | $(grep -m1 -E 'BUILD OK|NEVER-RAN' "$r/out_ok" || true)"
fi

# --- Case 12: hanging orphan KILL is capped at 30 s (kill path reached) --------
r="$(make_root)"; roots+=("$r")
tag="$(wslpath -w "$r" 2>/dev/null || printf '%s' "$r")"
tag="${tag%$'\r'}"
printf '%s\t%s\n' 4321 "dotnet exec ${tag}\\src\\Fake.dll" >"$r/procs12.tsv"
start=$SECONDS
rc1=0
WINPEPPER_APP_BUILD_CMD="sleep 30" WINPEPPER_APP_ROOT_OVERRIDE="$r" \
  WINPEPPER_APP_BUILD_TIMEOUT_S=2 \
  WINPEPPER_APP_ORPHAN_LIST_CMD="cat $r/procs12.tsv" \
  WINPEPPER_APP_ORPHAN_KILL_CMD="sleep 120" \
  bash "$WRAPPER" --attempts 5 >"$r/out1" 2>&1 || rc1=$?
elapsed=$((SECONDS - start))
if [[ $rc1 -eq 1 && $elapsed -lt 60 ]] \
  && grep -q 'killing orphaned dotnet.exe PID 4321' "$r/out1" \
  && grep -q 'TIMEOUT' "$r/out1"; then
  ok 12 "build 124 + matching orphan row + hanging kill (sleep 120 capped at 30 s) → kill path reached (PID 4321 line) and exit 1 TIMEOUT in ${elapsed}s (<60 s)"
else
  bad 12 "rc1=$rc1(want 1) elapsed=${elapsed}s(want <60) | $(grep -m1 -E 'TIMEOUT|killing|No such file' "$r/out1" || true)"
fi

# --- Summary ------------------------------------------------------------------
if [[ $failures -eq 0 ]]; then
  rm -rf "${roots[@]}"
  echo "SELFTEST: PASS"
  exit 0
fi
echo "SELFTEST: FAIL — $failures of 12 cases failed (disposable roots kept: ${roots[*]})" >&2
exit 1
