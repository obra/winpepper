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

## Rationale (candidate evaluation, from Phase-1 evidence)

Phase-1 report (absolute): `/home/dan/code/winpepper/.worktrees/.the-usual-logs/gzcc-unc-build-race/reports/phase1-systematic-debugging.md`
(scratch evidence under `/tmp/gzcc-repro/logs`). Summary:

- 9P cross-process file-visibility lag measured systematic: 98–100% of 800 writes ≥5 ms
  (≤43 ms observed); under concurrent-build contention the share also throws outright transport
  write errors — reproduced live as a WMC-family XAML compiler failure
  (`XamlCompiler: Failed to write output file: An unexpected network error occurred`), which
  passed on the next identical run.
- **Adopt** `-m:1 -p:UseSharedCompilation=false` — mechanism, stated accurately: the flags do
  NOT make the build one OS process (Roslyn always compiles out-of-proc — the shared
  VBCSCompiler server, or a per-project csc.exe child when `UseSharedCompilation=false`).
  `-m:1` makes MSBuild schedule the whole graph on ONE node, so targets run strictly in order:
  every tool process (CSC child, XamlCompiler.exe, mt shim) has exited and its 9P writes have
  settled for seconds-to-minutes before any dependent tool starts — versus parallel defaults,
  where independent nodes' compiler processes write and probe the share concurrently, inside
  the measured 5–43 ms lag windows. `UseSharedCompilation=false` additionally retires the
  long-lived Roslyn server, removing cross-invocation server state from the retry story.
  Evidence: parallel twins under matched 2×-concurrent-app-build contention: 1 failure in 6
  builds (the PX1b iter 2 WMC-family repro); serialized twins under the same contention:
  app-SS1/app-SS2 arms (see Verification); serialized solo: 6/6 OK in 210–318 s vs 167–234 s
  parallel. The flag is a risk reducer, not a guarantee — hence the retry layer.
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
- `Usage: scripts/build-app-windows-from-wsl.sh [--attempts N]` (default N=3).
- Env checks (fail exit 2 with a clear message): running under WSL (`WSL_DISTRO_NAME` set),
  `powershell.exe` executable at `/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`,
  `wslpath` resolvable.
- Prints and performs a WSL-side `rm -rf src/*/bin src/*/obj` pre-clean (cross-OS build state is
  not shareable — the deterministic CS0006 guard; see docs/testing-windows-from-wsl.md).
- Creates a unique per-invocation log directory
  `artifacts/build-app-windows/run-<UTC-YYYYMMDDTHHMMSSZ>-<pid>/` (printed at start and end) and
  tees every attempt's output to `attempt<N>.log` inside it, so evidence from consecutive runs
  never overwrites earlier runs and stale higher-numbered attempts cannot be misread.
- Per attempt: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "dotnet build
  '<UNC>\src\Winpepper.App\Winpepper.App.csproj' -c Release -m:1 -p:UseSharedCompilation=false
  -p:UseXamlCompilerExecutable=true; exit \$LASTEXITCODE"` under `timeout --foreground 2400`.
- Success → print `BUILD OK on attempt N (run log: <dir>)` + exit 0.
- Failure classification: exit 124 → TIMEOUT: run the checkout-name-scoped orphan kill (exact
  `kill_orphans` pattern from scripts/windows-gate.sh:82-91, tag = basename of repo root
  computed the same way as the gate's `HERE`), then stop with exit 1 (timeouts are not retried:
  a wedged build needs human eyes). Non-timeout failures: retry only if the attempt log matches
  `CS0006|WMC1006|unexpected network error`; print the matched signature; any other failure
  exits 1 immediately naming the attempt log.
- After N failed attempts → exit 1 naming the run directory.
- Self-test seam (documented in the script header): when `WINPEPPER_APP_BUILD_CMD` is set, it
  replaces the powershell build invocation entirely, the pre-clean and the WSL/powershell prereq
  checks are skipped, and `WINPEPPER_APP_BUILD_TIMEOUT_S` overrides the default 2400 s timeout.
  The seam exists so `scripts/build-app-windows-from-wsl.selftest.sh` can drive the retry /
  exhaustion / classification logic with an injected fake build command, and it changes nothing
  when unset.

Self-test (`scripts/build-app-windows-from-wsl.selftest.sh`, pure bash, no Windows interop;
returns exit 0 only when every case passes, printing `SELFTEST: PASS` / per-case lines):
1. *transient-then-success:* fake command writes a canned log containing `error CS0006` and
   exits 1 on calls 1–2, exits 0 on call 3 (attempt counter in a mktemp state file); run the
   wrapper with the seam and `--attempts 3` → wrapper exits 0, prints
   `BUILD OK on attempt 3`, and two `transient signature` lines appear.
2. *exhaustion:* fake always fails with a `WMC1006` log → wrapper exits 1 after exactly 3
   attempts (three attempt logs exist in a unique run dir).
3. *non-transient:* fake fails with a permanent `error CS1234` → wrapper exits 1 after exactly
   1 attempt (only `attempt1.log` exists), with no retry line.
4. *transport signature:* fake fails twice with `An unexpected network error occurred` then
   succeeds → wrapper exits 0 on attempt 3 (signature matched, not treated as permanent).
5. *clean first try:* fake succeeds immediately → exit 0 on attempt 1, no retry lines.
Each case also asserts on the wrapper's printed run-dir uniqueness (two runs → two distinct
run dirs).

**Files:**
- Create: `scripts/build-app-windows-from-wsl.sh`
- Test: `scripts/build-app-windows-from-wsl.selftest.sh`

**Interfaces:**
- Consumes: repo layout `src/Winpepper.App/Winpepper.App.csproj`; interop pattern and the
  `kill_orphans` body from `scripts/windows-gate.sh`; `wslpath`, `powershell.exe`.
- Produces: exit codes 0/1/2; `artifacts/build-app-windows/run-*/attempt<N>.log` under the
  gitignored `artifacts/`; stdout progress lines; env-seam contract
  (`WINPEPPER_APP_BUILD_CMD`, `WINPEPPER_APP_BUILD_TIMEOUT_S`).

**Test cases:**
- `bash -n` on both scripts → syntax clean.
- `shellcheck` on both scripts if shellcheck is installed → no findings; if not installed,
  record `Not run (shellcheck unavailable)` and rely on review.
- Self-test → `SELFTEST: PASS` (covers every classification branch deterministically).
- One real clean run `--attempts 1` → exit 0 `BUILD OK on attempt 1` (happy path; transient
  branches are covered deterministically by the self-test, statistically by Task 3's battery).

- [ ] **Step 1: Write the failing behavioral test**

  Create `scripts/build-app-windows-from-wsl.selftest.sh` per the five cases above (the fake
  build command is a small inline bash function exported via the seam env var; canned log
  snippets are written to the attempt-log path the wrapper hands the command — simplest: the
  fake builder prints to stdout/stderr and the wrapper captures it exactly as it captures the
  real build, so no path handoff is needed).

- [ ] **Step 2: Run the test and verify the intended failure**

  Run: `bash scripts/build-app-windows-from-wsl.selftest.sh`

  Expected: FAIL because `scripts/build-app-windows-from-wsl.sh` does not exist yet
  (shell reports No such file or directory / case 1 cannot run) — the missing behavior is the
  wrapper itself, not a setup accident.

- [ ] **Step 3: Add the minimal production implementation**

  Create `scripts/build-app-windows-from-wsl.sh` with exactly the Behavior above. Structure:
  header comment (purpose; the three mitigations with accurate single-node wording from
  Rationale; safety invariants; self-test seam contract); `set -euo pipefail`;
  `HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"`; env checks (skipped when the seam
  is set); `ATTEMPTS` default 3 parsed with a plain `if [[ ... ]]; then` (never `[[ ]] && { }`
  under `set -e`); unique run dir; pre-clean (skipped under seam); attempt loop with
  `run_attempt` (timeout-wrapped command string) + the classification rules above; kill_orphans
  verbatim from the gate. `chmod +x` both scripts.

- [ ] **Step 4: Run the focused test**

  Run: `bash -n scripts/build-app-windows-from-wsl.sh scripts/build-app-windows-from-wsl.selftest.sh && bash scripts/build-app-windows-from-wsl.selftest.sh`

  Expected: syntax clean; `SELFTEST: PASS` (all five cases green).

- [ ] **Step 5: Refactor while green**

  Keep the script self-contained (no shared lib with the gate — deliberate: the gate is proven
  and frozen for this kata; duplication of 15 well-commented lines is cheaper than touching the
  gate). Confirm no stray arg-parsing, no unused vars; rerun the self-test after any refactor.

- [ ] **Step 6: Run broader verification**

  Run: `scripts/build-app-windows-from-wsl.sh --attempts 1` (one real serialized clean build
  over UNC), then `./scripts/linux-tests.sh`.

  Expected: `BUILD OK on attempt 1` (exit 0); `LINUX SUITE: GREEN` (repo rule: green run before
  the commit).

- [ ] **Step 7: Commit the task**

  ```bash
  git add scripts/build-app-windows-from-wsl.sh scripts/build-app-windows-from-wsl.selftest.sh
  git commit -m "feat(build): add build-app-windows-from-wsl.sh — pre-clean + serialized UNC app build with bounded transient retry (kata gzcc)"
  ```

### Task 2: Documentation teaching the command and the why

**Requirements served:** R1, R2

**Behavior:**
- `docs/testing-windows-from-wsl.md` gains a `## Building the app from WSL` section after the
  "One command" section: the wrapper command; one paragraph why (9P cross-process visibility lag
  measured 5–43 ms + transport write errors under contention → CS0006/WMC1006; pre-clean kills
  the deterministic cross-OS CS0006; `-m:1` keeps the graph single-NODE so every tool's writes
  settle before the next tool starts — compiles themselves still run as child processes; bounded
  retry covers the XamlCompiler/mt-shim edges); pointer that the gate uses the same recipe.
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
- Consumes: Phase-1 numbers (lag 5–43 ms; repro PX1b iter 2; app-S 6/6; wrapper evidence Task 3).
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
  Numbers quoted in docs must match Phase-1 evidence (5–43 ms probe range; repro under
  contention = PX1b iter 2; retry default 3; serialized-build wall-clock 210–318 s).
  Use "single-node/serialized scheduling" wording — never "single process".

- [ ] **Step 4: Run the focused test**

  Run: `grep -n 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md docs/DEVELOPMENT.md`

  Expected: PASS — matches in all three files.

- [ ] **Step 5: Refactor while green**

  Verify the section sits after "One command" and before "Why the clean step"; no duplicated
  guidance (the troubleshooting section already covers cross-OS CS0006 — reference it, don't
  repeat it).

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh` (repo pre-commit rule applies to every commit)

  Expected: `LINUX SUITE: GREEN`. (Runs together with Task 1's run if Task 1 hasn't committed
  yet; each commit gets its own preceding green run.)

- [ ] **Step 7: Commit the task**

  ```bash
  git add docs/testing-windows-from-wsl.md AGENTS.md
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

  Run: `./scripts/linux-tests.sh` at final HEAD + confirm `git status` clean.

  Expected: `LINUX SUITE: GREEN`; clean tree.

  Additionally: one full `./scripts/windows-gate.sh` run at final HEAD as the whole-suite Windows
  confirmation (20–30 min timeout; the host is expected to be loaded by other agents — record
  wall-clock honestly). If the gate cannot complete for environmental reasons (interop outages),
  record `GATE: BLOCKED-ENVIRONMENTAL` with verbatim log lines — never claim GREEN without the
  summary.

- [ ] **Step 7: Commit the task**

  No commit (no tracked changes). Record evidence paths in the run ledger.

## Verification (complete, final HEAD)

1. `bash -n` on both new scripts → clean; selftest → `SELFTEST: PASS`.
2. 5 consecutive wrapper runs → 5/5 exit 0 (five distinct `run-*` log dirs; retries itemized).
3. Phase-1 arms: app-S 6/6 serialized solo; matched 2×-concurrent contention — parallel twins
   5/6 OK + 1 reproduced transient vs serialized twins (app-SS1/app-SS2 results as measured —
   recorded in the kata comment and recap).
4. `./scripts/linux-tests.sh` at final HEAD → `LINUX SUITE: GREEN` (count recorded).
5. Fresh-eyes plan and delta reviews (usual stages 3 and 5) → verdicts recorded in run-state.
