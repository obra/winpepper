# UNC App-Build Race Mitigation (kata gzcc) Implementation Plan

> **For agentic workers:** Execute this plan task by task with a fresh
> implementer and a specification-plus-quality review after every task. Track
> progress with the checkbox steps below.

**Goal:** Give humans (and agents outside the pre-push gate) one documented command that builds
`Winpepper.App` Release from the WSL2 checkout without falling over on the transient
CS0006/WMC1006 UNC ref-assembly races.

**Architecture:** One new wrapper script, `scripts/build-app-windows-from-wsl.sh`, that encodes
the gate-proven recipe end to end: WSL-side pre-clean of `src/**/{bin,obj}`, a serialized
single-node Windows build (`-m:1 -p:UseSharedCompilation=false
-p:UseXamlCompilerExecutable=true`) over the `\\wsl.localhost` UNC path, and a bounded retry that
fires only on the three observed transient signatures. Documentation teaches the command and the
why. No product code, csproj, gate, or CI changes.

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

Phase-1 report: `.worktrees/.the-usual-logs/gzcc-unc-build-race/reports/phase1-systematic-debugging.md`
(scratch evidence under `/tmp/gzcc-repro/logs`). Summary:

- 9P cross-process file-visibility lag measured systematic: 98–100% of 800 writes ≥5 ms
  (≤43 ms observed); under concurrent-build contention the share also throws outright transport
  write errors — both reproduced live, one producing the kata's WMC-family failure
  (`XamlCompiler: Failed to write output file: An unexpected network error occurred`).
- **Adopt** `-m:1 -p:UseSharedCompilation=false`: removes nearly all cross-process 9P edges from
  the managed graph; gate-proven since 2026-07-25; app-S arm (identical flags, 6 consecutive
  clean builds) = 6/6 OK, ~220–280 s per build vs ~170–230 s parallel (measured on a loaded host).
- **Adopt** the documented retry wrapper as the delivery vehicle: XamlCompiler.exe and the
  mt-unc-shim Exec remain separate processes over 9P no matter what; a bounded retry on transient
  signatures (`CS0006|WMC1006|unexpected network error`) matches the manual recovery users
  already perform ("retry the identical command").
- **Reject** `-nr:false` (parallel node-reuse disable) alone: fresh processes show the same lag
  (probe), so it neither removes the race nor reduces process fan-out; no observed failure had a
  stale-node-cache signature.
- **Reject** "pre-build the library graph first": its protective effect equals `-m:1` with extra
  choreography and unchanged library-internal edges; strictly worse.

---

### Task 1: `scripts/build-app-windows-from-wsl.sh`

**Requirements served:** R1, R2

**Behavior:**
- `Usage: scripts/build-app-windows-from-wsl.sh [--attempts N]` (default N=3).
- Env checks (fail exit 2 with a clear message): running under WSL (`WSL_DISTRO_NAME` set),
  `powershell.exe` executable at `/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`,
  `wslpath` and `wslpath -w` resolvable.
- Prints and performs a WSL-side `rm -rf src/*/bin src/*/obj` pre-clean (cross-OS build state is
  not shareable — the deterministic CS0006 guard; see docs/testing-windows-from-wsl.md).
- Per attempt: `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "dotnet build
  '<UNC>\src\Winpepper.App\Winpepper.App.csproj' -c Release -m:1 -p:UseSharedCompilation=false
  -p:UseXamlCompilerExecutable=true; exit \$LASTEXITCODE"` under `timeout --foreground 2400`,
  output teed to `artifacts/build-app-windows/attempt<N>.log` (`mkdir -p` first).
- Success → print `BUILD OK on attempt N` + exit 0.
- Failure classification: exit 124 → TIMEOUT: run the checkout-name-scoped orphan kill (exact
  `kill_orphans` pattern from scripts/windows-gate.sh:82-91, tag = basename of repo root
  computed the same way as the gate's `HERE`), then stop with exit 1 (timeouts are not retried:
  a wedged build needs human eyes). Non-timeout failures: retry only if the attempt log matches
  `CS0006|WMC1006|unexpected network error`; any other failure exits 1 immediately naming the log.
- After N failed attempts → exit 1 naming the log directory.

**Files:**
- Create: `scripts/build-app-windows-from-wsl.sh`

**Interfaces:**
- Consumes: repo layout `src/Winpepper.App/Winpepper.App.csproj`; interop pattern and the
  `kill_orphans` body from `scripts/windows-gate.sh`; `wslpath`, `powershell.exe`.
- Produces: exit codes 0/1/2; `artifacts/build-app-windows/attempt<N>.log` (gitignored path);
  stdout progress lines.

**Test cases:**
- `bash -n scripts/build-app-windows-from-wsl.sh` → syntax clean.
- `shellcheck scripts/build-app-windows-from-wsl.sh` if shellcheck is installed → no findings;
  if not installed, record `Not run (shellcheck unavailable)` and rely on review.
- Real clean runs (T2 in Verification) → every run exit 0; any retried attempt must have a
  transient signature matched in its attempt log.

- [ ] **Step 1: Write the failing behavioral test**

  This is a script, not a unit-test-resident behavior (repo has no shell-test harness; consistent
  with `scripts/windows-gate.sh`, which has none either). The failing behavioral statement: the
  script does not exist, so "one documented command" fails. Red evidence: before implementation,
  `scripts/build-app-windows-from-wsl.sh --attempts 1` from the worktree fails with
  `No such file or directory`. Record that output in the task report.

- [ ] **Step 2: Run the test and verify the intended failure**

  Run: `scripts/build-app-windows-from-wsl.sh --attempts 1`

  Expected: FAIL because the script does not exist yet (shell reports No such file or directory).

- [ ] **Step 3: Add the minimal production implementation**

  Create `scripts/build-app-windows-from-wsl.sh` with exactly the Behavior above. Reference
  implementation sketch (keep the structure; fix any quoting issues against the real shell):
  header comment stating purpose + the three mitigations + safety invariants; `set -euo
  pipefail`; `HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"`; primitives copied in
  structure from `scripts/windows-gate.sh` (`run_ps` + `kill_orphans`); `ATTEMPTS` default 3
  parsed with a plain `if [[ ... ]]; then` (never `[[ ]] && { }` under `set -e`); the pre-clean;
  the attempt loop with the classification rules above. `chmod +x`.

- [ ] **Step 4: Run the focused test**

  Run: `bash -n scripts/build-app-windows-from-wsl.sh && scripts/build-app-windows-from-wsl.sh --attempts 1`

  Expected: syntax clean; one serialized clean build of Winpepper.App over UNC exits
  `BUILD OK on attempt 1` (exit 0). (If a transient hits, exit still 0 via retry on later
  attempts of the same run — any retry must cite the matched signature from the attempt log.)

- [ ] **Step 5: Refactor while green**

  Keep the script self-contained (no shared lib with the gate — deliberate: the gate is proven
  and frozen for this kata; duplication of 15 well-commented lines is cheaper than touching the
  gate). Confirm no stray arg-parsing, no unused vars.

- [ ] **Step 6: Run broader verification**

  Run: `./scripts/linux-tests.sh`

  Expected: `LINUX SUITE: GREEN` (docs+scripts-only change; must not affect tests, but the repo
  rule requires the green run before commit).

- [ ] **Step 7: Commit the task**

  ```bash
  git add scripts/build-app-windows-from-wsl.sh
  git commit -m "feat(build): add build-app-windows-from-wsl.sh — pre-clean + serialized UNC app build with bounded transient retry (kata gzcc)"
  ```

### Task 2: Documentation teaching the command and the why

**Requirements served:** R1, R2

**Behavior:**
- `docs/testing-windows-from-wsl.md` gains a `## Building the app from WSL` section after the
  "One command" section: the wrapper command; one paragraph why (9P cross-process visibility lag
  measured 5–43 ms + transport write errors under contention → CS0006/WMC1006; pre-clean kills
  the deterministic cross-OS CS0006; flags keep the graph single-process; bounded retry covers
  the XamlCompiler/mt-shim edges); pointer that the gate uses the same recipe.
- `AGENTS.md` gains exactly one sentence inside the existing Windows-gate bullet: hand app builds
  from WSL go through the new script. No other restructuring.
- `README.md`: only if it currently documents a hand `dotnet build` of the app from WSL — check
  first; if it does not, do not touch it.

**Files:**
- Modify: `docs/testing-windows-from-wsl.md` (one new section)
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

  Run: `grep -rn 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md`

  Expected: FAIL — no matches (exit 1).

- [ ] **Step 3: Add the minimal production implementation**

  Write the doc section and the one AGENTS.md sentence per Behavior. Numbers quoted in docs must
  match Phase-1 evidence (5–43 ms probe range; repro under contention; retry default 3).

- [ ] **Step 4: Run the focused test**

  Run: `grep -n 'build-app-windows-from-wsl' docs/testing-windows-from-wsl.md AGENTS.md`

  Expected: PASS — matches in both files.

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
  loaded Windows host (each run pre-cleans, so each is a true clean build). Record exit codes
  and attempt counts; keep attempt logs.
- Combine with Phase-1 arms into the final evidence table: app-P 4/4, stress 1/6 failure
  (reproduced), minisln 10/10, app-S 6/6, wrapper 5/5 (with any retries itemized).
- Final `./scripts/linux-tests.sh` green run against the exact final HEAD; record the SHA and
  totals.

**Files:**
- No repo files. Logs land in `artifacts/build-app-windows/` (gitignored) and the run's
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

1. `bash -n scripts/build-app-windows-from-wsl.sh` → clean.
2. 5 consecutive wrapper runs → 5/5 exit 0 (attempt logs itemized).
3. Phase-1 app-S arm: 6/6 serialized clean builds OK (flags recipe standalone evidence).
4. `./scripts/linux-tests.sh` at final HEAD → `LINUX SUITE: GREEN` (count recorded).
5. Fresh-eyes plan and delta reviews (usual stages 3 and 5) → verdicts recorded in run-state.
