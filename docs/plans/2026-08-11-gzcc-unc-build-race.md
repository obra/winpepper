# UNC App-Build Race Mitigation (kata gzcc) Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** Give humans (and agents outside the pre-push gate) one documented command that builds
`Winpepper.App` Release from the WSL2 checkout without falling over on the transient
CS0006/WMC1006 UNC ref-assembly races.

**Architecture:** One new wrapper script, `scripts/build-app-windows-from-wsl.sh`, that encodes
the gate-proven recipe end to end: WSL-side pre-clean of `src/**/{bin,obj}`, an MSBuild
single-node (strictly serialized) Windows build (`-m:1 -p:UseSharedCompilation=false
-p:UseXamlCompilerExecutable=true`) over the `\\wsl.localhost` UNC path, and a bounded retry that
fires only on the three observed transient signatures. A self-test script drives the wrapper with
an injected fake build command to prove the retry/exhaustion/non-transient classification logic
deterministically. Documentation teaches the command and the why. No product code, csproj, gate,
or CI changes.

**Tech Stack:** bash + powershell.exe interop (same pattern as `scripts/windows-gate.sh`),
Windows .NET SDK 9.0.3xx on the host, WSL2.

## Global Constraints

- Work only in the run worktree (`/home/dan/code/winpepper/.worktrees/gzcc-unc-build-race`,
  branch `the-usual/gzcc-unc-build-race`); never modify the main checkout.
- Repo rules (AGENTS.md): before every commit the Linux suite is green (build each `tests/`
  project `-c Release`, run via `dotnet exec <built test dll>` — never `dotnet test`;
  `./scripts/linux-tests.sh` does exactly this and shows `LINUX SUITE: GREEN`).
- Windows builds only via the documented WSL→Windows interop (powershell.exe / dotnet.exe over
  the UNC path), 20–30 min timeouts where relevant.
- Safety invariants identical to `scripts/windows-gate.sh`: never install the MSI, never launch
  or kill `Winpepper.exe`, never write `%LOCALAPPDATA%\winpepper`; orphan-kill filters must be
  scoped to this checkout's directory name only.
- Keep changes minimal and focused: new script + doc touch-ups only. No changes to
  `scripts/windows-gate.sh` behavior, csproj/sln files, or CI.
- Never commit `.kata.toml`, `.opencode/`, model/corpus/artifact payloads (`artifacts/` is
  gitignored build output — the script writes logs there but they are never committed).

## Requirements

- **R1 — Outcome:** A single documented command builds `Winpepper.App` (Release, XAML exe
  compiler) from a WSL2 checkout without user-visible CS0006/WMC1006 transient build failures:
  `scripts/build-app-windows-from-wsl.sh` exits 0 across consecutive clean builds on the loaded
  Windows host.
- **R2 — Constraint:** The kata's three candidate fixes are evaluated in-repo and the evaluation
  is recorded (in this plan's Rationale and the kata comment); the chosen recipe keeps the build
  byte-for-byte the gate's recipe (same flags) so the gate remains the canary for it.
- **R3 — Evidence:** ≥5 consecutive wrapper runs from clean state, every run exit-0, with
  per-attempt logs under `artifacts/build-app-windows/`; Linux suite green before the commit;
  fresh-eyes reviews of plan and delta.
- **R4 — Reporting:** post `kata comment gzcc` with root cause, fix implemented, worktree/branch,
  verification evidence (consecutive clean-build counts), fresh-eyes verdicts, and what remains;
  never close the issue.

## Rationale (candidate evaluation, from Phase-1 evidence)

Phase-1 report (absolute): `/home/dan/code/winpepper/.worktrees/.the-usual-logs/gzcc-unc-build-race/reports/phase1-systematic-debugging.md`
(scratch evidence under `/tmp/gzcc-repro/logs`, incl. retained probe-v2 raw rows). Summary:

- What is hard evidence: under concurrent-build contention the 9P share throws outright
  transport faults — reproduced live as `XamlCompiler: Failed to write output file: An
  unexpected network error occurred` with WMC-family follow-on errors, passing on the next
  identical run. What is NOT true, despite v1 appearing to show it: systematic cross-process
  *visibility lag* — probe v1 conflated per-op UNC latency with invisibility windows (found in
  delta review); the corrected probe v2 (first-attempt miss field + local-disk control, raw rows
  retained) shows 0/300 first-attempt misses on 9P, idle AND under twin-build contention, zero
  open retries — only per-op latency (p99 53–64 ms contended vs 0 local). The kata's exact
  CS0006/WMC1006 codes are inferred members of this transient-I/O class, never fresh-reproduced;
  every recorded historical CS0006 also had the confounded, since-fixed cross-OS obj-mixing
  mechanism in play.
- **Adopt** `-m:1 -p:UseSharedCompilation=false` as the contention reducer and determinism aid:
  one MSBuild node, strictly ordered project targets, per-project csc.exe children and
  XamlCompiler.exe never overlapping each other on the share. It is not a process-coherence or
  visibility-window fix (none proved necessary) and imposes no timing guarantee: its value is
  minimal concurrent 9P traffic (the variable the reproduced fault tracks with) and a graph
  order that makes bounded retry converge. `UseSharedCompilation=false` retires the long-lived
  Roslyn server, removing cross-invocation server state from the retry story.
  Evidence: parallel twins under matched 2×-concurrent-app-build contention: 1 fault in 6
  builds; serialized twins same contention: app-SS1 3/3, app-SS2 2/2-builds OK, zero transient
  signatures; serialized solo: 6/6 OK in 210–318 s vs 167–234 s parallel. Small N — the flag is
  a risk reducer, not a guarantee; the retry layer exists accordingly.
- **Adopt** the documented retry wrapper as the delivery vehicle: XamlCompiler.exe, per-project
  csc.exe children, and the mt-unc-shim Exec cross 9P process boundaries no matter what; a
  bounded retry on transient signatures (`CS0006|WMC1006|unexpected network error`) matches the
  manual recovery users already perform ("retry the identical command").
- **Reject** `-nr:false` (parallel node-reuse disable) alone: fresh processes show the same lag
  (probe), so it neither removes the race nor reduces process fan-out; no observed failure had a
  stale-node-cache signature.
- **Reject** "pre-build the library graph first": its protective effect equals `-m:1` with extra
  choreography and unchanged library-internal edges; strictly worse.

---

### Task 1: `scripts/build-app-windows-from-wsl.sh` (+ deterministic self-test)

**Requirements served:** R1, R2

**Behavior:**
- `Usage: scripts/build-app-windows-from-wsl.sh [--attempts N]` (default N=5 — the kata records
  transient chains needing up to 5 attempts; the default must cover the recorded worst case).
  Argument validation: if `--attempts` is present its value must be a positive integer and no
  trailing arguments may remain; any violation prints usage and exits 2.
- Env checks (fail exit 2 with a clear message): running under WSL (`WSL_DISTRO_NAME` set),
  `powershell.exe` executable at `/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`,
  `wslpath` resolvable.
- Prints and performs a WSL-side `rm -rf <root>/src/*/bin <root>/src/*/obj` pre-clean (cross-OS
  build state is not shareable — the deterministic CS0006 guard; see
  docs/testing-windows-from-wsl.md). The pre-clean ALWAYS runs against the effective root
  `<root>` (normally the repo checkout; see `WINPEPPER_APP_ROOT_OVERRIDE`) — it is never
  silently skipped, because R3's "clean build" evidence depends on it.
- Creates a unique per-invocation log directory
  `artifacts/build-app-windows/run-<UTC-YYYYMMDDTHHMMSSZ>-<pid>/` (printed at start and end) and
  tees every attempt's output to `attempt<N>.log` inside it, so evidence from consecutive runs
  never overwrites earlier runs and stale higher-numbered attempts cannot be misread.
- Per attempt: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "dotnet build
  '<UNC>\src\Winpepper.App\Winpepper.App.csproj' -c Release -m:1 -p:UseSharedCompilation=false
  -p:UseXamlCompilerExecutable=true; exit \$LASTEXITCODE"` under `timeout --foreground 2400`.
- Success → print `BUILD OK on attempt N (run log: <dir>)` + exit 0.
- Failure classification: exit 124 → TIMEOUT: run the checkout-path-scoped orphan cleanup
  (below), then stop with exit 1 (timeouts are not retried: a wedged build needs human eyes).
  Non-timeout failures: retry only if the attempt log matches
  `CS0006|WMC1006|unexpected network error`; print the matched signature; any other failure
  exits 1 immediately naming the attempt log.
- After N failed attempts → exit 1 naming the run directory.
- **Timeout orphan cleanup, correctly scoped (deviation from the gate's basename pattern):**
  listing and filtering are separated so the filter lives in bash and is self-testable —
  1. list: `dotnet.exe` processes as `PID<TAB>CommandLine` rows (default:
     powershell `Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |%{
     "$($_.ProcessId)`t$($_.CommandLine)"}`; overridable via `WINPEPPER_APP_ORPHAN_LIST_CMD`),
  2. filter in bash, on command lines NORMALIZED to `/` separators (mixed `\`/`/` spellings can
     coexist in real Windows command lines — task-1 re-review): keep a row only when the
     normalized line contains the FULL effective-root tag (the `wslpath -w` UNC spelling
     of `<root>`, or Linux spelling as fallback) IMMEDIATELY FOLLOWED by a separator AND does
     not contain `<tag>/.worktrees/` — full path + separator boundary, never a basename: from
     the main checkout the bare basename `winpepper` is a substring of every nested worktree
     path (`...\winpepper\.worktrees\<other-agent>\...`), an unbounded full-path match would
     also accept prefix-named siblings (`...\gzcc` vs `...\gzcc2`), and either of those can
     kill other agents' builds under the expected concurrent-agent workload (round-3 plan
     review + task-1 review rounds);
  3. kill only the kept PIDs (default: powershell `Stop-Process -Id <pid> -Force` per PID;
     overridable via `WINPEPPER_APP_ORPHAN_KILL_CMD`), printing each PID.
- Self-test seams (documented in the script header; each changes nothing when unset):
  `WINPEPPER_APP_BUILD_CMD` — replaces the powershell build invocation and skips the
  WSL/powershell prereq checks; `WINPEPPER_APP_ROOT_OVERRIDE` — replaces the effective root for
  the pre-clean and kill tag (only meaningful with the build-cmd seam; lets the selftest use a
  disposable tree); `WINPEPPER_APP_BUILD_TIMEOUT_S` — overrides the 2400 s timeout;
  `WINPEPPER_APP_ORPHAN_LIST_CMD` / `WINPEPPER_APP_ORPHAN_KILL_CMD` — replace the powershell
  list/kill invocations. The seams exist so the self-test can drive every branch
  deterministically with injected fake commands.

Self-test (`scripts/build-app-windows-from-wsl.selftest.sh`, pure bash, no Windows interop;
returns exit 0 only when every case passes, printing `SELFTEST: PASS` / per-case lines).
All runs use the build-cmd seam plus a disposable `WINPEPPER_APP_ROOT_OVERRIDE` (mktemp dir
with a fake `src/Winpepper.App/Winpepper.App.csproj` placeholder):
1. *transient-then-success-at-the-boundary:* fake command prints a canned log containing
   `error CS0006` and exits 1 on calls 1–4, exits 0 on call 5 (attempt counter in a mktemp
   state file); run the wrapper with `--attempts 5` → wrapper exits 0, prints
   `BUILD OK on attempt 5`, and four `transient signature` lines appear.
2. *exhaustion:* fake always fails with a `WMC1006` log → wrapper exits 1 after exactly 5
   attempts (five attempt logs exist in a unique run dir).
3. *non-transient:* fake fails with a permanent `error CS1234` → wrapper exits 1 after exactly
   1 attempt (only `attempt1.log` exists), with no retry line.
4. *transport signature:* fake fails twice with `An unexpected network error occurred` then
   succeeds → wrapper exits 0 on attempt 3 (signature matched, not treated as permanent).
5. *clean first try:* fake succeeds immediately → exit 0 on attempt 1, no retry lines.
6. *timeout path + kill scoping:* fake build is `sleep`-based with
   `WINPEPPER_APP_BUILD_TIMEOUT_S=2` (→ exit 124); `WINPEPPER_APP_ORPHAN_LIST_CMD` fakes seven
   rows — (a) a CommandLine containing the disposable root's tag + separator, (b) one containing
   a DIFFERENT checkout under `<main>\.worktrees\other-agent\...`, (c) one containing a
   `<tag>\.worktrees\` nested path, (d) one containing a prefix-named sibling `<tag>2\...`,
   (e) one containing a FORWARD-SLASH nested `<tag>/.worktrees/...`, (f,g) both MIXED-separator
   nested spellings; `WINPEPPER_APP_ORPHAN_KILL_CMD` records the PIDs it receives.
   → wrapper exits 1 after exactly 1 attempt (no retry), prints a TIMEOUT line, and the kill
   record contains ONLY PID (a) (the separator-normalized, boundary-aware full-path filter
   provably selects own-checkout command lines and rejects all six other-checkout flavors).
7. *pre-clean runs against the effective root:* seed `<disposable>/src/Seed/bin/` and
   `.../obj/` sentinel files; the fake build command asserts both are already gone at call
   time (failing the run if present); after the wrapper exits 0, the sentinels are absent from
   disk — the "clean build" claim of Task 3 rests on this, never on assumption.
8. *usage validation:* `--attempts 0`, `--attempts abc`, a trailing positional argument, and a
   SET-BUT-EMPTY `WINPEPPER_APP_ROOT_OVERRIDE` each exit 2 with usage/error and never run a
   build (the empty-override guard keeps a failed `mktemp` upstream from pointing the pre-clean
   at the real checkout; the selftest's own setup also aborts hard when a disposable root cannot
   be created and validates it lives under /tmp).
9. *logging integrity (added in delta round 1):* a PATH-shadowed fake `tee` that exits nonzero
   while the fake build exits 0 → the wrapper must exit 1 with a logging-integrity message and
   never print BUILD OK (a logging failure must not certify a run whose evidence log is
   missing/truncated; the production branch treats a tee failure as a non-retried immediate
   stop).
10. *cleanup timeout caps (added in delta round 2):* build times out (124) while the orphan-list
    command hangs (`sleep 120`; the wrapper caps the list at 60 s and each kill at 30 s, seam
    path included) → the wrapper must still reach its exit-1 TIMEOUT result in bounded time
    rather than hang on the same stalled interop that wedged the build.
Cases 1, 2 and 5 also assert run-dir uniqueness (any two runs → two distinct `run-*` dirs).

**Files:**
- Create: `scripts/build-app-windows-from-wsl.sh`
- Test: `scripts/build-app-windows-from-wsl.selftest.sh`

**Interfaces:**
- Consumes: repo layout `src/Winpepper.App/Winpepper.App.csproj`; the interop pattern from
  `scripts/windows-gate.sh` (run_ps/timeout structure — NOT its basename kill filter);
  `wslpath`, `powershell.exe`.
- Produces: exit codes 0/1/2; `artifacts/build-app-windows/run-*/attempt<N>.log` under the
  gitignored `artifacts/`; stdout progress lines; env-seam contract
  (`WINPEPPER_APP_BUILD_CMD`, `WINPEPPER_APP_ROOT_OVERRIDE`,
  `WINPEPPER_APP_BUILD_TIMEOUT_S`, `WINPEPPER_APP_ORPHAN_LIST_CMD`,
  `WINPEPPER_APP_ORPHAN_KILL_CMD`).

**Test cases:**
- `bash -n` on both scripts → syntax clean.
- `shellcheck` on both scripts if shellcheck is installed → no findings; if not installed,
  record `Not run (shellcheck unavailable)` and rely on review.
- Self-test → `SELFTEST: PASS` (covers every classification branch deterministically:
  retryable CS0006/WMC1006/transport, non-transient, exhaustion at N=5, timeout+orphan-kill).
- One real clean run `--attempts 1` → exit 0 `BUILD OK on attempt 1` (happy path; transient
  branches are covered deterministically by the self-test, statistically by Task 3's battery).

- [ ] **Step 1: Write the failing behavioral test**

  Create `scripts/build-app-windows-from-wsl.selftest.sh` per the ten cases above (the fake
  build command is a small inline bash snippet passed via the seam env var; the wrapper captures
  its stdout/stderr to the attempt log exactly as it captures the real build, so no path handoff
  is needed).

- [ ] **Step 2: Run the test and verify the intended failure**

  Run: `bash scripts/build-app-windows-from-wsl.selftest.sh`

  Expected: FAIL because `scripts/build-app-windows-from-wsl.sh` does not exist yet
  (shell reports No such file or directory / case 1 cannot run) — the missing behavior is the
  wrapper itself, not a setup accident.

- [ ] **Step 3: Add the minimal production implementation**

  Create `scripts/build-app-windows-from-wsl.sh` with exactly the Behavior above. Structure:
  header comment (purpose; the three mitigations with accurate single-node wording from
  Rationale; safety invariants; self-test seam contract for all five seams); `set -euo
  pipefail`; `HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"`; `usage()` defined before
  any call site; the root-override guard (a SET-but-empty or non-directory
  `WINPEPPER_APP_ROOT_OVERRIDE` prints the error + usage and exits 2 — never silently falls
  back to the real checkout) evaluated BEFORE `ROOT="${WINPEPPER_APP_ROOT_OVERRIDE:-$HERE}"` is
  allowed to take effect; env checks (skipped when the build-cmd seam is set); `--attempts`
  parsing with 1–3-digit positive-integer validation in a plain `if ...; then`
  (never `[[ ]] && { }` under `set -e`); unique run dir
  `$ROOT/artifacts/build-app-windows/run-<UTC>-<pid>/` (uniform for real and seam runs — in real
  use `$ROOT` is the checkout so logs land in the gitignored `artifacts/`; in the selftest it is
  the mktemp disposable root); pre-clean against `$ROOT` (always, never skipped); attempt loop
  with `run_attempt` (timeout-wrapped command string, output teed to the attempt log with the
  build's exit status preserved from the pipeline head) + the classification rules above;
  timeout branch = list (seam-able) → bash filter on separator-normalized command lines
  (full-path tag + separator boundary + `.worktrees/` exclusion) → kill only kept PIDs
  (seam-able). `chmod +x` both scripts.

- [ ] **Step 4: Run the focused test**

  Run: `bash -n scripts/build-app-windows-from-wsl.sh scripts/build-app-windows-from-wsl.selftest.sh && bash scripts/build-app-windows-from-wsl.selftest.sh`

  Expected: syntax clean; `SELFTEST: PASS` (all ten cases green).

- [ ] **Step 5: Refactor while green**

  Keep the script self-contained (no shared lib with the gate — deliberate: the gate is proven
  and frozen for this kata; duplication of 15 well-commented lines is cheaper than touching the
  gate). Confirm no stray arg-parsing, no unused vars; rerun the self-test after any refactor.

- [ ] **Step 6: Run broader verification**

  Order matters — never let Windows-built `obj` state leak into a Linux build (AGENTS.md
  cross-OS rule). Run the Linux suite FIRST (its own clean conditions), then the real wrapper
  run (the wrapper pre-cleans `src/**/{bin,obj}` itself, so stale Linux `src` state from the
  suite is removed by design):

  Run: `find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && ./scripts/linux-tests.sh && scripts/build-app-windows-from-wsl.sh --attempts 1`

  Expected: `LINUX SUITE: GREEN` (repo rule: green run before the commit), then
  `BUILD OK on attempt 1` (exit 0), run log directory printed. Note: after this step the tree's
  `src/**` state is Windows-built — any later Linux run must wipe first (AGENTS.md).

- [ ] **Step 7: Commit the task**

  ```bash
  git add scripts/build-app-windows-from-wsl.sh scripts/build-app-windows-from-wsl.selftest.sh
  git commit -m "feat(build): add build-app-windows-from-wsl.sh — pre-clean + serialized UNC app build with bounded transient retry (kata gzcc)"
  ```

### Task 2: Documentation teaching the command and the why

**Requirements served:** R1, R2

**Behavior:**
- `docs/testing-windows-from-wsl.md` gains a `## Building the app from WSL` section after the
  "One command" section: the wrapper command; one honest paragraph why — reproduced live under
  concurrent-build contention: the XAML compiler failed writing to the share ("An unexpected
  network error occurred") with follow-on WMC-family XAML errors; and the corrected probe v2
  result — cross-process file reads on 9P showed zero first-attempt misses (600 pairs, half
  under contention), only stretched per-op latency (p99 ≈ 53–64 ms contended vs ~0 ms locally),
  i.e. docs must NOT claim a visibility-lag window; the kata's exact CS0006/WMC1006 ref-assembly
  codes are *inferred* members of the same transient-I/O class (every historically recorded
  CS0006 trace also had the confounded, since-fixed cross-OS obj-mixing mechanism in play) — the
  docs must say exactly that and not claim fresh CS0006 reproduction; pre-clean kills the
  deterministic cross-OS CS0006; `-m:1` keeps the graph single-node so no two tool processes
  race each other on the share (compiles still run as child processes; no timing guarantee is
  implied) and the bounded retry covers the residual; pointer that the gate uses the same recipe.
- `docs/DEVELOPMENT.md`: the "Building from source" WSL paragraph (lines ~51-56) gains one
  sentence routing WSL2-checkout app builds through `scripts/build-app-windows-from-wsl.sh`
  (from a WSL shell), noting it hardens the documented `dotnet build` command against the
  transient 9P ref-assembly races; the raw command block stays for native-Windows readers.
- `AGENTS.md` gains exactly one sentence inside the existing Windows-gate bullet: hand app builds
  from WSL go through the new script. No other restructuring.
- `README.md`: only if it currently documents a hand `dotnet build` of the app from WSL — check
  first (`grep -n 'dotnet build' README.md`); if it does not, do not touch it.

**Files:**
- Modify: `docs/testing-windows-from-wsl.md` (one new section)
- Modify: `docs/DEVELOPMENT.md` (one sentence in "Building from source")
- Modify: `AGENTS.md` (one sentence)
- Modify (only if it documents WSL hand app builds): `README.md`

**Interfaces:**
- Consumes: Phase-1 numbers (probe v2: 0/300 first-attempt misses on 9P incl. contention,
  p99 ≈ 53–64 ms contended latency; repro PX1b iter 2 transport fault; app-S 6/6;
  wrapper evidence Task 3).
- Produces: no code interfaces.

**Test cases:**
- Docs-only: no behavioral test (recorded per usual-test-driven-development "pure documentation"
  clause). Validation: the new commands' text matches the script's real interface `--attempts`;
  markdown renders (no broken fences); `AGENTS.md` diff is one line.

- [ ] **Step 1: Write the failing behavioral test**

  Red evidence: `grep -n 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md`
  finds nothing (the command is undocumented). Record output.

- [ ] **Step 2: Run the test and verify the intended failure**

  Run: `grep -rn 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md docs/DEVELOPMENT.md`

  Expected: FAIL — no matches (exit 1).

- [ ] **Step 3: Add the minimal production implementation**

  Write the doc section, the DEVELOPMENT.md sentence, and the AGENTS.md sentence per Behavior.
  Numbers quoted in docs must match Phase-1 evidence (probe v2: first-attempt 0/300 misses on
  9P incl. contention, p99 ≈ 53–64 ms contended latency — no visibility claim; repro under
  contention = PX1b iter 2 transport fault; retry default 5; serialized-build wall-clock
  210–318 s vs parallel 167–234 s).
  Use "single-node/serialized scheduling" wording — never "single process".

- [ ] **Step 4: Run the focused test**

  Run: `grep -n 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md docs/DEVELOPMENT.md`

  Expected: PASS — matches in all three files.

- [ ] **Step 5: Refactor while green**

  Verify the section sits after "One command" and before "Why the clean step"; no duplicated
  guidance (the troubleshooting section already covers cross-OS CS0006 — reference it, don't
  repeat it).

- [ ] **Step 6: Run broader verification**

  Task 1's Windows run left Windows-built `src/**` state — wipe before any Linux build
  (AGENTS.md cross-OS rule; linux-tests.sh does not pre-clean).

  Run: `find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && ./scripts/linux-tests.sh`

  Expected: `LINUX SUITE: GREEN` (repo pre-commit rule applies to every commit).

- [ ] **Step 7: Commit the task**

  ```bash
  git add docs/testing-windows-from-wsl.md docs/DEVELOPMENT.md AGENTS.md
  git commit -m "docs(build): teach build-app-windows-from-wsl.sh for WSL app builds (kata gzcc)"
  ```

### Task 3: Evidence — consecutive clean-wrapper runs and final verification

**Requirements served:** R1, R3

**Behavior:**
- Run `scripts/build-app-windows-from-wsl.sh` 5 consecutive times from the worktree on the
  loaded Windows host (each run pre-cleans, so each is a true clean build). Record exit codes,
  attempt counts, and each run's unique `run-*` log directory (five distinct dirs, logs
  retained).
- Combine with Phase-1 arms into the final evidence table: app-P 4/4 solo parallel; matched
  2×-concurrent contention — parallel twins 5/6 (1 reproduced transient) vs serialized twins
  app-SS1 3/3 + app-SS2 2/2 builds OK (its third attempt never ran a build: the documented
  `UtilAcceptVsock accept4 failed 110` WSL interop outage — environmental, excluded from build
  rates); zero transient signatures in any serialized attempt; minisln 10/10; app-S 6/6
  serialized solo; wrapper 5/5 (with any retries itemized and their matched signatures quoted).
- Final `./scripts/linux-tests.sh` green run against the exact final HEAD; record the SHA and
  totals.

**Files:**
- No repo files. Logs land in `artifacts/build-app-windows/run-*` (gitignored) and the run's
  `<logs-dir>/reports/`.

**Interfaces:**
- Consumes: Task 1 script (committed), Phase-1 CSVs at `/tmp/gzcc-repro/logs/*.csv`.
- Produces: the verification table for the kata comment and recap.

**Test cases:**
- 5 wrapper invocations → each exits 0; if any invocation needed an attempt ≥2, the itemized
  transient signature from that attempt's log is quoted in the report.
- Linux suite → `LINUX SUITE: GREEN` with the recorded test count.

- [ ] **Step 1: Write the failing behavioral test**

  Red statement: without Task 1 the command cannot run; already proven in Task 1 Step 2. Nothing
  new to fail.

- [ ] **Step 2: Run the test and verify the intended failure**

  N/A — covered by Task 1 Step 2 (recorded as such).

- [ ] **Step 3: Add the minimal production implementation**

  No implementation — this task is the verification battery.

- [ ] **Step 4: Run the focused test**

  Run: 5 × `scripts/build-app-windows-from-wsl.sh`

  Expected: every run `BUILD OK ...` exit 0 (retries, if any, itemized with signatures).

- [ ] **Step 5: Refactor while green**

  N/A (no code).

- [ ] **Step 6: Run broader verification**

  Cross-OS order (AGENTS.md rule): wipe → Linux suite → gate (the gate pre-cleans `src` and
  `tests` itself).

  Run: `find src tests -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && ./scripts/linux-tests.sh && ./scripts/windows-gate.sh`

  Expected: `LINUX SUITE: GREEN` at final HEAD (count recorded); then one full gate run
  (`GATE: GREEN`), 20–30 min timeout budget — the host is expected to be loaded by other
  agents, record wall-clock honestly. If the gate cannot complete for environmental reasons
  (e.g. the documented `UtilAcceptVsock accept4 failed 110` interop outages), record
  `GATE: BLOCKED-ENVIRONMENTAL` with verbatim log lines — never claim GREEN without the summary.
  Finally confirm `git status` clean.

- [ ] **Step 7: Commit the task**

  No commit (no tracked changes). Record evidence paths in the run ledger.

### Task 4: Report back to kata gzcc (no close)

**Requirements served:** R4

**Behavior:**
- Post one `kata comment gzcc --body ...` from the main checkout (`cd /home/dan/code/winpepper`)
  containing: root cause (≤3 sentences + reproduction status), the fix (script/flags/docs),
  worktree + branch + final SHAs, verification evidence (consecutive clean-build counts across
  the wrapper battery and Phase-1 arms), fresh-eyes plan/delta verdicts and rounds, and what
  remains. The issue stays OPEN.
- Write the run recap artifacts under `<logs-dir>/reports/` (final evidence table, test-status
  ledger) before the comment so the comment can cite them.

**Files:**
- No repo files.

**Interfaces:**
- Consumes: run-state.md, plan/delta review logs, Task 3 evidence table.
- Produces: the kata comment (visible via `kata show gzcc`).

**Test cases:**
- `kata show gzcc` afterwards shows the new comment and the issue still open.

- [ ] **Step 1: Write the failing behavioral test** — N/A (reporting task; no code). The "red"
  statement: `kata show gzcc` currently has no comment from this run.

- [ ] **Step 2: Run the test and verify the intended failure** — Run `kata show gzcc`; expect
  zero comments from this run.

- [ ] **Step 3: Add the minimal production implementation** — compose and post the comment
  (content per Behavior; cite run-state SHAs and the evidence table).

- [ ] **Step 4: Run the focused test** — `kata show gzcc` → comment present, issue open.

- [ ] **Step 5: Refactor while green** — N/A.

- [ ] **Step 6: Run broader verification** — N/A (after the final delta fresh-eyes round;
  nothing to build).

- [ ] **Step 7: Commit the task** — No commit.

## Verification (complete, final HEAD)

1. `bash -n` on both new scripts → clean; selftest → `SELFTEST: PASS`.
2. 5 consecutive wrapper runs → 5/5 exit 0 (five distinct `run-*` log dirs; retries itemized).
3. Phase-1 arms: app-S 6/6 serialized solo; matched 2×-concurrent contention — parallel twins
   5/6 OK + 1 reproduced transient vs serialized twins (app-SS1/app-SS2 results as measured —
   recorded in the kata comment and recap).
4. `./scripts/linux-tests.sh` at final HEAD → `LINUX SUITE: GREEN` (count recorded).
5. Fresh-eyes plan and delta reviews (usual stages 3 and 5) → verdicts recorded in run-state.
