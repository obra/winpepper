# Streaming-Transcription Branch Verification Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Close out verification of the streaming-transcription work: merge `main`, build a scripted Windows pre-push gate that runs from WSL, replace the simulated latency/quality evidence with REAL evidence from the actual Parakeet model on the Windows host, and update the stale docs — all committed on `feat/streaming-verification`, nothing pushed.

**Architecture:** Two new WSL-side bash scripts drive the Windows host over `powershell.exe` interop and the `\\wsl.localhost` UNC path (build support for that arrives by merging `main` @ `3b1903e`). The existing `scripts/asr-latency-bench` console app (net9.0, already drives the production pipeline classes) gains a `real-local` mode that loads real WAV files and runs `ParakeetSession` (batch) vs `ParakeetStreamingSession` (streamed at real-time pace) against the real model, reporting transcripts, post-stop latency, and a word-level diff. Reference speech WAVs are generated on the host with built-in TTS (`System.Speech.Synthesis`). Measured results are committed as a markdown evidence report.

**Tech Stack:** .NET 9 (Linux SDK 9.0.100 at `/home/dan/code/winpepper/.dotnet`, Windows SDK 9.0.316 via `powershell.exe` interop), xUnit v3 in-process runner (`dotnet exec`), bash, PowerShell 5.1, ONNX Runtime DirectML (Windows-only at runtime), System.Speech TTS.

## Global Constraints

- **Worktree / branch:** all work happens in `/home/dan/code/winpepper/.worktrees/streaming-verification` on branch `feat/streaming-verification` (created from `feat/streaming-transcription` head `43f6363`). **Do NOT push anything** — merge/push is left to the user.
- **Main checkout is off-limits for edits:** `/home/dan/code/winpepper` (branch `main`) has uncommitted user work-in-progress in `packaging/Winpepper.Msi.wixproj` and `packaging/winpepper.wxs` plus untracked `scripts/windows-sandbox/`. Never modify, stage, stash, or commit anything in that checkout. Read-only `git` commands there are fine.
- **Windows host safety (the user's Winpepper app may be RUNNING):** never install the MSI, never launch or kill `Winpepper.exe`, never write to `C:\Users\dan\AppData\Local\winpepper` (read-only access to the models dir is allowed). Do not install anything on the Windows host beyond NuGet-restored build artifacts and `%TEMP%` files.
- **Test method (AGENTS.md, verbatim rule):** build each project in `tests/` with `-c Release`, then execute via the xUnit v3 in-process runner (`dotnet exec <built test dll>`). **Never `dotnet test`** — the VSTest host is unreliable on some dev machines.
- **Green-before-commit:** every commit must have a green Linux suite (`./scripts/linux-tests.sh` after Task 1 creates it; 0 failures, 0 errors). Branch Linux baseline: **1044 tests, 0 failures** (measured 2026-07-25 at `537be6b` via the exact Task 1 method; an earlier session's "1050" figure was wrong). Windows expectation after merge: **12 project/TFM runs, roughly ~1300+ tests** — record the actual number; 0 failures/0 errors is the gate, the count is a cross-check. Windows runs may legitimately report `Skipped: N > 0`: the Llama cleanup integration tests self-skip via `Assert.SkipUnless` when the qwen GGUF model is absent on the host (it currently is). Skips keep the gate green but must be recorded honestly in the evidence doc.
- **Cross-OS build hygiene (validated 2026-07-25):** Windows-over-UNC and Linux builds share each project's `bin/`/`obj/`. Incremental Windows builds after a Linux build fail with transient `CS0006` (missing `obj/**/ref/*.dll`), one project per retry — so `windows-gate.sh` builds with `--no-incremental`. NEVER run `./scripts/linux-tests.sh` and `./scripts/windows-gate.sh` (or any other cross-OS build of this tree) concurrently.
- **Linux SDK:** `export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"` (SDK 9.0.100; the worktree has no `.dotnet` of its own; `global.json` pins `9.0.100` + `latestFeature`).
- **Windows interop facts (verified):** `powershell.exe` at `/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`; Windows `dotnet` is on PATH for powershell (9.0.316); UNC path of a WSL file = `$(wslpath -w <path>)` → `\\wsl.localhost\Ubuntu\...`; building the WinUI app from UNC requires `-p:UseXamlCompilerExecutable=true` and the mt.exe UNC shim from commit `3b1903e`.
- **QEMU audio VM is NOT provisioned** (no VM image, no PulseAudio under WSLg, no piper, no sshpass). Do not attempt to use or provision it. `scripts/winrun`/`winssh`/`sync-to-vm.sh` target that VM — do not use them.
- **Model:** the real Parakeet model lives ONLY on the Windows host at `C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3` (flat dir: `encoder-model.int8.onnx`, `decoder_joint-model.int8.onnx`, `vocab.txt`). Never copy or modify it. ONNX Runtime DirectML has no Linux native runtime — real-model runs happen on Windows only.
- **Package versions** may only be declared in `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`); `WarningsAsErrors=nullable` is on repo-wide — new C# code must be null-clean. (No new packages are planned.)
- **Docs rule:** `README.md` is the only end-user markdown doc; everything under `docs/plans/` is a working/agent doc. Commit messages follow conventional commits (`feat:`, `fix:`, `docs:`, `build:` ...). Keep commits focused and atomic.
- `artifacts/` and `/.dotnet/` are gitignored — scripts write logs under `artifacts/`.

---

## File Structure

| File | Task | Responsibility |
|---|---|---|
| `scripts/linux-tests.sh` (create) | 1 | Single entrypoint for the pure-managed Linux suite (build 9 test projects, `dotnet exec` each net9.0 DLL, fail on any red) |
| *(merge from `main`)* `scripts/mt-unc-shim.{ps1,cmd}`, csproj/wixproj changes, `README.md` | 2 | WSL2/UNC build support + MSI upgrade fix, brought in by merging `main` |
| `scripts/windows-gate.sh` (create) | 3 | The Windows pre-push gate: app build + 9 test-project builds + 12 `dotnet exec` runs on the host via powershell.exe, loud summary |
| `src/`, `tests/` fixes (modify, as needed) | 4 | Any Windows-only compile/test fixes surfaced by the first-ever Windows compile of this branch |
| `scripts/asr-latency-bench/BenchAudio.cs` (create) | 5 | Pure helpers: WAV read (16 kHz mono int16), gain, leading-silence prepend, RMS stats |
| `scripts/asr-latency-bench/TranscriptDiff.cs` (create) | 5 | Transcript normalization + word-level LCS diff summary |
| `tests/Winpepper.Asr.Tests/BenchHelpersTests.cs` (create) + `Winpepper.Asr.Tests.csproj` (modify) | 5 | Unit tests for the two helper files (compiled in via `<Compile Include>`) |
| `scripts/asr-latency-bench/Program.cs` (modify) | 6 | `--wav/--model-dir/--gain/--lead-silence-ms` options, `real-local` scenario (batch vs streamed real Parakeet), per-row audio duration, WAV-fed remote scenarios |
| `scripts/generate-bench-wavs.ps1` (create) | 7 | Host-side TTS generation of the reference speech WAVs (System.Speech, 16 kHz mono 16-bit) |
| `scripts/run-bench-windows.sh` (create) | 7 | Build bench with Windows dotnet, stage to `%TEMP%`, generate WAVs, run the 4 phrase categories (+ cloud if key present) |
| `src/Winpepper.Asr/**` + `tests/Winpepper.Asr.Tests/**` (modify) | 7b | Root-cause & fix the real-model streaming truncation (only the first encoded chunk emits tokens — falsified by a 2026-07-25 validation probe), or land the loud-fallback + default-off safety valve |
| `docs/plans/2026-07-25-streaming-verification-evidence.md` (create) | 8, 9 | The committed evidence report: real transcripts, latencies, diffs, gate summary, honest environment notes |
| `docs/manual-test.md` (modify) | 10 | Supersede the stale Plan 3/4/5/6 "blocked/deferred" annotations |
| `AGENTS.md` (modify) | 10 | Document the gate script + bench procedure as THE way to satisfy the Windows pre-push rule from WSL |

Scope check: this is one deliverable chain on one branch (merge → gate → evidence → docs); each task below is independently testable, so a single plan is appropriate.

---

### Task 1: Linux test-runner script + baseline

**Files:**
- Create: `scripts/linux-tests.sh`

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `./scripts/linux-tests.sh` — exits 0 iff all 9 net9.0 test runs are green (`Errors: 0` and `Failed: 0` and runner exit 0); prints a per-project tail and a grand total. Every later task's commit step calls this.

- [ ] **Step 1: Write the script**

Create `scripts/linux-tests.sh` with exactly this content:

```bash
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
```

- [ ] **Step 2: Make it executable and syntax-check it**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/streaming-verification
chmod +x scripts/linux-tests.sh
bash -n scripts/linux-tests.sh && echo SYNTAX-OK
```
Expected: `SYNTAX-OK`.

- [ ] **Step 3: Run it — this is the pre-merge baseline**

Run (allow ~10–20 minutes; the first build restores NuGet):
```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` and `linux-tests grand total: 1044 tests` (measured 2026-07-25 at `537be6b` via exactly this method — 0 failed / 0 errors / 0 skipped; per-project: Asr 161, Audio 62, Cleanup 85, Core 379, Corrections 23, History 45, Integration 1, Models 73, Platform 215). If the grand total differs from 1044, do NOT paper over it: re-check the per-project tails for skips/failures and record the actual number — 0 failures/errors is the hard gate, the count is the cross-check. Note: the summary lines carry leading ANSI color codes, which is why the script's greps are deliberately unanchored — keep them that way. **Record the exact grand total; Task 2 must reproduce it.**

- [ ] **Step 4: Commit**

```bash
git add scripts/linux-tests.sh
git commit -m "build: add scripts/linux-tests.sh, the scripted Linux test suite runner"
```

---

### Task 2: Merge `main` into the branch

**Files:**
- Modify: (merge commit) — brings in `main` commits `a41a1bd` (MSI same-version upgrades) and `3b1903e` (WSL2/UNC build support: `scripts/mt-unc-shim.{ps1,cmd}`, `Winpepper.App.csproj` ManifestTool override, `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>` in 8 library csprojs, `Winpepper.Msi.wixproj` UNC staging, `README.md` note).

**Interfaces:**
- Consumes: `./scripts/linux-tests.sh` (Task 1).
- Produces: a branch containing `3b1903e`, which Task 3's gate script requires (the app cannot build from a UNC path without the mt.exe shim). After this task, `scripts/mt-unc-shim.cmd` and `scripts/mt-unc-shim.ps1` exist in the worktree.

- [ ] **Step 1: Start the merge without committing**

A dry run (`git merge-tree --write-tree main feat/streaming-verification`) was already verified conflict-free: the changed-file sets of the two sides do not overlap at all (main touched only `README.md`, `packaging/`, `scripts/mt-unc-shim.*`, and csproj `<PropertyGroup>` headers; the branch touched only `.cs`/`.xaml`, `tests/`, `docs/plans/`, and `scripts/asr-latency-bench/`).

```bash
cd /home/dan/code/winpepper/.worktrees/streaming-verification
git merge --no-commit --no-ff main
git status --short
```
Expected: no `UU`/`AA` conflict entries; staged changes only. If a conflict does appear (unexpected), resolve by keeping BOTH sides' intent (main's changes are build-plumbing-only and orthogonal to the branch's ASR code), then `git add` the resolved files.

- [ ] **Step 2: Verify the shim files arrived**

```bash
ls scripts/mt-unc-shim.ps1 scripts/mt-unc-shim.cmd
grep -n "UseXamlCompilerExecutable" README.md | head -3
```
Expected: both shim files listed; README mentions the flag.

- [ ] **Step 3: Run the full Linux suite on the merged tree**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` with the SAME grand total as Task 1's baseline (main adds zero test files, so the count must be identical). Any regression → fix before committing (but none is expected: zero file overlap).

- [ ] **Step 4: Commit the merge**

```bash
git commit --no-edit
git log --oneline -3
```
Expected: top commit is `Merge branch 'main' into feat/streaming-verification` (default message), with `3b1903e` now an ancestor: `git merge-base --is-ancestor 3b1903e HEAD && echo OK` prints `OK`.

---

### Task 3: The Windows pre-push gate script

**Files:**
- Create: `scripts/windows-gate.sh`

**Interfaces:**
- Consumes: merged `main` (Task 2 — mt-unc-shim + RuntimeIdentifiers must be on the branch); `powershell.exe` interop; Windows `dotnet` 9.0.316 on the host PATH.
- Produces: `./scripts/windows-gate.sh` — exits 0 and prints `GATE: GREEN` iff (a) `Winpepper.App` builds Release with `-p:UseXamlCompilerExecutable=true`, (b) all 9 test projects build Release, and (c) all 12 project/TFM `dotnet exec` runs are green. Logs under `artifacts/windows-gate/`. Tasks 4 and 9 run it; Task 10 documents it in AGENTS.md.

- [ ] **Step 1: Write the script**

Create `scripts/windows-gate.sh` with exactly this content:

```bash
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
# Cross-OS obj hygiene (validated 2026-07-25): Linux builds share bin/obj
# with these UNC builds; incremental Windows builds after a Linux build hit
# transient CS0006 (missing obj/**/ref/*.dll), so every build below uses
# --no-incremental. Never run linux-tests.sh concurrently with this gate.
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
     "dotnet build '$app' -c Release --no-incremental -p:UseXamlCompilerExecutable=true"; then
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
  if run_ps "$BUILD_TIMEOUT" "$LOG_DIR/build-$proj.log" "dotnet build '$csproj' -c Release --no-incremental"; then
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
```

- [ ] **Step 2: Syntax-check and smoke the failure path**

```bash
chmod +x scripts/windows-gate.sh
bash -n scripts/windows-gate.sh && echo SYNTAX-OK
```
Expected: `SYNTAX-OK`. Also sanity-check the interop preflight works at all:
```bash
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command 'dotnet --version'
```
Expected: `9.0.316` (or a later 9.0.x).

- [ ] **Step 3: Run the Linux suite (green-before-commit rule)**

```bash
./scripts/linux-tests.sh
```
Expected: `LINUX SUITE: GREEN` (script-only change; count unchanged).

- [ ] **Step 4: Commit**

```bash
git add scripts/windows-gate.sh
git commit -m "build: add scripts/windows-gate.sh, the WSL-driven Windows pre-push gate"
```

---

### Task 4: First gate run — compile this branch's Windows code and get the gate green

This is the first-ever Windows compile of the branch's Windows-only changes (PipelineHost `OrphanedPumpGuard` wiring, `StreamingEnabled` toggle gating, ModelsPage toggle UI). Expect possible compile errors or Windows-TFM test failures; fix them here.

**Files:**
- Modify: `src/**` and/or `tests/**` — only as needed to fix real Windows failures. Likely candidates if anything breaks: `src/Winpepper.App/Hosting/PipelineHost.cs`, `src/Winpepper.App/Views/ModelsPage.xaml`, `src/Winpepper.App/Views/ModelsPage.xaml.cs`, `src/Winpepper.Core/Settings/AppSettings.cs`.

**Interfaces:**
- Consumes: `./scripts/windows-gate.sh` (Task 3), `./scripts/linux-tests.sh` (Task 1).
- Produces: a branch state where `./scripts/windows-gate.sh` prints `GATE: GREEN` — the precondition for Tasks 8–9's evidence runs.

- [ ] **Step 1: Run the gate**

Run with a generous outer timeout (the whole run can take 30–90 minutes; first run restores NuGet over UNC):
```bash
./scripts/windows-gate.sh 2>&1 | tee artifacts/windows-gate/first-run.txt
```
Expected outcomes: either `GATE: GREEN` (skip to Step 4) or `GATE: RED` with a summary naming the failing stage(s).

- [ ] **Step 2: If RED — diagnose from the logs**

For each red line in the summary, open the referenced log under `artifacts/windows-gate/`. Triage guide:
- **App compile errors** (`app-build.log`): XAML codegen errors usually mean an `x:Name`/handler mismatch between `ModelsPage.xaml` and `ModelsPage.xaml.cs`; C# errors in `PipelineHost.cs` are plain fixes. `System.Security.Permissions` load failure has a documented one-time workaround in `README.md` (lines ~186–207) — apply it via powershell.exe if hit.
- **Windows-TFM test failures**: read the xUnit failure output in the run log; fix the product code or the test's Windows assumptions — never weaken an assertion just to pass.
- **TIMEOUT on `Winpepper.Platform.Tests (net9.0-windows...)`**: almost certainly `Hook_Installs_And_DisposesCleanly` wedged. The host desktop is interactive so this is unexpected — check the host isn't locked; re-run once; if it reproduces, report it honestly (do NOT silently exclude the test; surface the situation for the user in the evidence doc and stop this task as blocked).

- [ ] **Step 3: Fix → Linux green → commit → re-run gate (repeat until GREEN)**

For each fix cycle:
```bash
# make the fix, then:
./scripts/linux-tests.sh          # must print LINUX SUITE: GREEN
git add <changed files>
git commit -m "fix(app): <what the Windows compile/test run surfaced>"
./scripts/windows-gate.sh
```
Keep each fix commit focused (one failure class per commit).

- [ ] **Step 4: Preserve the green run's summary**

When the gate prints `GATE: GREEN`, keep the console capture:
```bash
cp artifacts/windows-gate/first-run.txt artifacts/windows-gate/last-green.txt 2>/dev/null || true
./scripts/windows-gate.sh 2>&1 | tee artifacts/windows-gate/last-green.txt   # only if the green run wasn't already captured
```
(No commit needed if no source changed in this task; `artifacts/` is gitignored. Task 9 records the final gate summary in the evidence doc.)

---

### Task 5: Bench pure helpers — `BenchAudio` + `TranscriptDiff` (TDD)

**Files:**
- Create: `scripts/asr-latency-bench/BenchAudio.cs`
- Create: `scripts/asr-latency-bench/TranscriptDiff.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add `<Compile Include>` for the two files)
- Test: `tests/Winpepper.Asr.Tests/BenchHelpersTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Asr.Transcription.PcmWavEncoder.EncodeMono16k(ReadOnlySpan<float>) -> byte[]` (already in `Winpepper.Asr`, used by tests to fabricate WAVs).
- Produces (namespace `AsrLatencyBench`, used by Task 6's Program.cs):
  - `static float[] BenchAudio.ReadMono16k(string path)` — throws `InvalidDataException` unless mono/16000 Hz/16-bit PCM.
  - `static float[] BenchAudio.ApplyGain(float[] samples, double gain)` — scales, clamps to [-1, 1].
  - `static float[] BenchAudio.PrependSilence(float[] samples, int ms, int sampleRate = 16000)`.
  - `static float[] BenchAudio.Prepare(float[] samples, double gain, int leadSilenceMs)`.
  - `static (double Rms, double Peak, double MaxFrameRms) BenchAudio.Stats(float[] samples, int frameSamples = 320)`.
  - `sealed record DiffSummary(bool TrivialOnly, int BatchWordCount, int StreamWordCount, IReadOnlyList<string> WordDiffs)` with `string Describe()`.
  - `static string TranscriptDiff.Normalize(string text)`; `static DiffSummary TranscriptDiff.Summarize(string batchText, string streamText)`.

The rationale for `<Compile Include>`: the bench is an Exe not referenced by any test project, and adding a 10th test project would change the "9 projects / 12 runs" gate contract. `Winpepper.Platform.Tests` already uses the `<Compile Include>` pattern for src files, so this follows repo precedent. Both helper files must depend only on the BCL (no `Winpepper.*` references) so they compile identically in the bench and in `Winpepper.Asr.Tests`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/BenchHelpersTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using AsrLatencyBench;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class BenchHelpersTests
{
    private static string WriteTempWav(float[] samples)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, PcmWavEncoder.EncodeMono16k(samples).ToArray());
        return path;
    }

    [Fact]
    public void ReadMono16k_RoundTrips_PcmWavEncoderOutput()
    {
        var samples = Enumerable.Range(0, 1600)
            .Select(i => (float)Math.Sin(2 * Math.PI * 440 * i / 16000.0) * 0.5f)
            .ToArray();
        var path = WriteTempWav(samples);
        try
        {
            var read = BenchAudio.ReadMono16k(path);
            Assert.Equal(samples.Length, read.Length);
            for (var i = 0; i < samples.Length; i++)
                Assert.True(Math.Abs(samples[i] - read[i]) < 0.001f, $"sample {i}: {samples[i]} vs {read[i]}");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadMono16k_Rejects_WrongSampleRate()
    {
        var bytes = PcmWavEncoder.EncodeMono16k(new float[1600]).ToArray();
        BitConverter.GetBytes(8000).CopyTo(bytes, 24); // canonical fmt-chunk sample-rate offset
        var path = Path.Combine(Path.GetTempPath(), $"bench-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, bytes);
        try
        {
            Assert.Throws<InvalidDataException>(() => BenchAudio.ReadMono16k(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ApplyGain_Scales_And_Clamps()
    {
        var result = BenchAudio.ApplyGain(new[] { 0.5f, -0.5f, 0.9f }, 2.0);
        Assert.Equal(1.0f, result[0]);      // 1.0 after clamp
        Assert.Equal(-1.0f, result[1]);     // -1.0 after clamp
        Assert.Equal(1.0f, result[2]);      // clamped
        var quiet = BenchAudio.ApplyGain(new[] { 0.5f }, 0.1);
        Assert.True(Math.Abs(quiet[0] - 0.05f) < 1e-6f);
    }

    [Fact]
    public void PrependSilence_Adds_LeadingZeros()
    {
        var result = BenchAudio.PrependSilence(new[] { 0.3f, 0.4f }, 100);
        Assert.Equal(1600 + 2, result.Length); // 100 ms @ 16 kHz = 1600 samples
        Assert.All(result.Take(1600), s => Assert.Equal(0f, s));
        Assert.Equal(0.3f, result[1600]);
        Assert.Equal(0.4f, result[1601]);
    }

    [Fact]
    public void Stats_Reports_Rms_Peak_And_MaxFrameRms()
    {
        // 320 loud samples followed by 320 silent samples.
        var samples = Enumerable.Repeat(0.5f, 320).Concat(Enumerable.Repeat(0f, 320)).ToArray();
        var (rms, peak, maxFrameRms) = BenchAudio.Stats(samples);
        Assert.Equal(0.5, peak, 3);
        Assert.Equal(0.5, maxFrameRms, 3);                    // the loud frame
        Assert.Equal(0.5 / Math.Sqrt(2), rms, 3);             // half the energy overall
    }

    [Fact]
    public void Normalize_Strips_Case_Punctuation_And_Whitespace()
    {
        Assert.Equal("hello world", TranscriptDiff.Normalize("  Hello,   World! "));
        Assert.Equal("don't stop", TranscriptDiff.Normalize("Don't stop."));
    }

    [Fact]
    public void Summarize_TrivialOnly_When_Only_Punctuation_Differs()
    {
        var diff = TranscriptDiff.Summarize("Send the report, please.", "send the report please");
        Assert.True(diff.TrivialOnly);
        Assert.Empty(diff.WordDiffs);
        Assert.Equal(4, diff.BatchWordCount);
    }

    [Fact]
    public void Summarize_Lists_WordLevel_Diffs()
    {
        var diff = TranscriptDiff.Summarize("send the report", "send that report");
        Assert.False(diff.TrivialOnly);
        Assert.Contains("-the", diff.WordDiffs);
        Assert.Contains("+that", diff.WordDiffs);
        Assert.Equal(3, diff.BatchWordCount);
        Assert.Equal(3, diff.StreamWordCount);
    }
}
```

Add to `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`, inside the `<Project>` element as a new ItemGroup:

```xml
  <ItemGroup>
    <Compile Include="..\..\scripts\asr-latency-bench\BenchAudio.cs" Link="Bench\BenchAudio.cs" />
    <Compile Include="..\..\scripts\asr-latency-bench\TranscriptDiff.cs" Link="Bench\TranscriptDiff.cs" />
  </ItemGroup>
```

Note: `PcmWavEncoder.EncodeMono16k` returns `byte[]` — if the compiler says so directly, drop the `.ToArray()` calls above (they're defensive in case the signature is span-based).

- [ ] **Step 2: Run the tests to verify they fail**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
cd /home/dan/code/winpepper/.worktrees/streaming-verification
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAILS with `CS0246`-style errors (`BenchAudio`/`TranscriptDiff` don't exist yet — the Compile Include points at missing files, so expect a "file not found" MSB error; that counts as the failing state).

- [ ] **Step 3: Implement `BenchAudio`**

Create `scripts/asr-latency-bench/BenchAudio.cs`:

```csharp
using System;
using System.IO;

namespace AsrLatencyBench;

/// <summary>
/// Pure audio helpers for the bench: WAV loading (16 kHz mono int16 only),
/// deterministic gain / leading-silence transforms, and RMS stats used to
/// pick a --gain that keeps the quiet-talker guard active. BCL-only so the
/// same file compiles into Winpepper.Asr.Tests via Compile Include.
/// </summary>
public static class BenchAudio
{
    public static float[] ReadMono16k(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        if (new string(br.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"{path}: not a RIFF file");
        br.ReadInt32(); // riff chunk size
        if (new string(br.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"{path}: not a WAVE file");

        short channels = 0, bits = 0;
        var rate = 0;
        byte[]? data = null;
        while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
        {
            var id = new string(br.ReadChars(4));
            var size = br.ReadInt32();
            if (id == "fmt ")
            {
                br.ReadInt16(); // format tag
                channels = br.ReadInt16();
                rate = br.ReadInt32();
                br.ReadInt32(); // byte rate
                br.ReadInt16(); // block align
                bits = br.ReadInt16();
                br.BaseStream.Seek(size - 16, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        if (data is null)
            throw new InvalidDataException($"{path}: no data chunk");
        if (channels != 1 || rate != 16000 || bits != 16)
            throw new InvalidDataException(
                $"{path}: need mono/16000Hz/16-bit PCM, got {channels}ch/{rate}Hz/{bits}-bit");

        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return samples;
    }

    public static float[] ApplyGain(float[] samples, double gain)
    {
        if (Math.Abs(gain - 1.0) < 1e-9) return samples;
        var result = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
            result[i] = (float)Math.Clamp(samples[i] * gain, -1.0, 1.0);
        return result;
    }

    public static float[] PrependSilence(float[] samples, int ms, int sampleRate = 16000)
    {
        if (ms <= 0) return samples;
        var pad = ms * sampleRate / 1000;
        var result = new float[pad + samples.Length];
        Array.Copy(samples, 0, result, pad, samples.Length);
        return result;
    }

    public static float[] Prepare(float[] samples, double gain, int leadSilenceMs)
        => PrependSilence(ApplyGain(samples, gain), leadSilenceMs);

    /// <summary>MaxFrameRms uses 20 ms (320-sample) frames, matching
    /// InteriorSilenceSkipper's analysis frames — the quiet-talker guard is
    /// active while max frame RMS &lt; 0.002 / 0.15 ≈ 0.0133.</summary>
    public static (double Rms, double Peak, double MaxFrameRms) Stats(float[] samples, int frameSamples = 320)
    {
        double sum = 0, peak = 0, maxFrameRms = 0;
        for (var start = 0; start < samples.Length; start += frameSamples)
        {
            var end = Math.Min(start + frameSamples, samples.Length);
            double frameSum = 0;
            for (var i = start; i < end; i++)
            {
                var s = samples[i];
                frameSum += s * s;
                peak = Math.Max(peak, Math.Abs(s));
            }
            sum += frameSum;
            maxFrameRms = Math.Max(maxFrameRms, Math.Sqrt(frameSum / Math.Max(1, end - start)));
        }
        return (Math.Sqrt(sum / Math.Max(1, samples.Length)), peak, maxFrameRms);
    }
}
```

- [ ] **Step 4: Implement `TranscriptDiff`**

Create `scripts/asr-latency-bench/TranscriptDiff.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AsrLatencyBench;

public sealed record DiffSummary(
    bool TrivialOnly,
    int BatchWordCount,
    int StreamWordCount,
    IReadOnlyList<string> WordDiffs)
{
    public string Describe() => TrivialOnly
        ? $"IDENTICAL after case/punctuation/whitespace normalization ({BatchWordCount} words)"
        : $"{WordDiffs.Count} word-level diffs (batch {BatchWordCount} words, stream {StreamWordCount} words): {string.Join(" ", WordDiffs)}";
}

/// <summary>
/// Word-level transcript comparison. "Trivial" = equal after lowercasing,
/// stripping punctuation (apostrophes kept), and collapsing whitespace —
/// the acceptance bar for streamed-vs-batch transcripts. Anything else is
/// reported honestly as -word (batch only) / +word (stream only) via LCS.
/// BCL-only so the same file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class TranscriptDiff
{
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? c : ' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static DiffSummary Summarize(string batchText, string streamText)
    {
        var b = Normalize(batchText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var s = Normalize(streamText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (b.SequenceEqual(s))
            return new DiffSummary(true, b.Length, s.Length, Array.Empty<string>());

        // LCS-based word diff (transcripts are short; O(n*m) is fine).
        var lcs = new int[b.Length + 1, s.Length + 1];
        for (var i = b.Length - 1; i >= 0; i--)
            for (var j = s.Length - 1; j >= 0; j--)
                lcs[i, j] = b[i] == s[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var diffs = new List<string>();
        int x = 0, y = 0;
        while (x < b.Length && y < s.Length)
        {
            if (b[x] == s[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) diffs.Add("-" + b[x++]);
            else diffs.Add("+" + s[y++]);
        }
        while (x < b.Length) diffs.Add("-" + b[x++]);
        while (y < s.Length) diffs.Add("+" + s[y++]);
        return new DiffSummary(false, b.Length, s.Length, diffs);
    }
}
```

- [ ] **Step 5: Run the new tests and verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.BenchHelpersTests"
```
Expected: all 8 tests pass (`Failed: 0`, `Errors: 0`). Also verify the bench itself still builds with the new files:
```bash
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release
```
Expected: build succeeds (files are compiled but unused until Task 6).

- [ ] **Step 6: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/BenchAudio.cs scripts/asr-latency-bench/TranscriptDiff.cs \
        tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj tests/Winpepper.Asr.Tests/BenchHelpersTests.cs
git commit -m "feat(bench): add WAV/gain/silence helpers and word-level transcript diff, with tests"
```
Expected: `LINUX SUITE: GREEN` (grand total = baseline + 8).

---

### Task 6: Bench `real-local` mode — real Parakeet batch vs streaming over WAVs

**Files:**
- Modify: `scripts/asr-latency-bench/Program.cs`

**Interfaces:**
- Consumes: `BenchAudio` / `TranscriptDiff` (Task 5); from `Winpepper.Asr`: `ParakeetSession(string modelDir)` (implements `IParakeetBackend`; `bool UsingDirectML`; `static bool ModelFilesPresent(string)`); `ParakeetTranscriber(ParakeetSession session, string modelName)` with `Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float>, CancellationToken)`; `ParakeetStreamingSession(IParakeetBackend backend, string modelName, PreprocessorConfig config, Func<ReadOnlyMemory<float>, CancellationToken, Task<TranscriptionResult>> batchFallback, int chunkMelFrames = 200, int leftContextMelFrames = 100, ILogger? log = null)` with `ValueTask PushAsync(ReadOnlyMemory<float>, CancellationToken)` and `Task<TranscriptionResult> FinishAsync(ReadOnlyMemory<float> fullAudio, CancellationToken)`; `PreprocessorConfig.ParakeetTdtV3`.
- Produces: the bench CLI contract used by Task 7's runner and Task 8's evidence:
  `dotnet exec AsrLatencyBench.dll [scenario ...] [--wav <path>]... [--model-dir <dir>] [--gain <g>] [--lead-silence-ms <ms>]`
  with new scenario `real-local` (runs batch AND streaming per `--wav`, prints per-run `# ...` diagnostic lines — transcripts, `UsingDirectML`, `fellBackToBatch`, input stats, captured session log lines including `InteriorSilenceSkipper` skip stats — plus rows `real-local-batch <file>` / `real-local-stream <file>` in the final table). The final markdown table gains a real per-row audio duration. When `--wav` is provided, the FIRST wav (after `--gain`/`--lead-silence-ms`) also becomes the audio for the existing sim/remote scenarios, so `real-remote-*` can run on real speech.

- [ ] **Step 1: Add option parsing and audio selection**

In `scripts/asr-latency-bench/Program.cs`, add near the top (with the other `using` directives):

```csharp
using AsrLatencyBench;
```

Replace the existing argument handling at the top (currently: `var requested = args.Length > 0 ? args : new[] { ... };` around line 19) with:

```csharp
var wavPaths = new List<string>();
string? modelDir = null;
var gain = 1.0;
var leadSilenceMs = 0;
var scenarioArgs = new List<string>();
for (var argIdx = 0; argIdx < args.Length; argIdx++)
{
    switch (args[argIdx])
    {
        case "--wav": wavPaths.Add(args[++argIdx]); break;
        case "--model-dir": modelDir = args[++argIdx]; break;
        case "--gain": gain = double.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--lead-silence-ms": leadSilenceMs = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
        default: scenarioArgs.Add(args[argIdx]); break;
    }
}
var requested = scenarioArgs.Count > 0 ? scenarioArgs.ToArray() : new[]
{
    "sim-local-batch", "sim-local-stream",
    "sim-remote-batch", "sim-remote-stream",
    "real-remote-batch", "real-remote-stream",
};
```

Then hoist a SHARED audio buffer. **Important (verified against the current source):** `Program.cs` does NOT have a single shared audio-buffer creation point — it calls `SynthesizeAudio(...)` separately inside each scenario (≥5 per-scenario call sites, around lines 33/44/63/80/93). Add ONE shared buffer right after the option parsing above:

```csharp
var audio = wavPaths.Count > 0
    ? BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPaths[0]), gain, leadSilenceMs)
    : SynthesizeAudio(AudioSeconds);
var audioSeconds = audio.Length / 16000.0;
```

then replace EVERY per-scenario `SynthesizeAudio(...)` call site with the shared `audio` variable (preserving any per-scenario handling around it). This is what makes the promised CLI contract true — "when `--wav` is provided, the FIRST wav also becomes the audio for the existing sim/remote scenarios" — so `real-remote-*` runs on real speech. Done naively (editing only one call site), the cloud rows would silently keep using tone-sweep audio and the evidence claim would be false.

- [ ] **Step 2: Add real per-row audio duration to the results table**

Change the rows accumulator (currently `var rows = new List<(string Scenario, string Kind, long PostStopMs)>();` around line 25) to:

```csharp
var rows = new List<(string Scenario, string Kind, double AudioSeconds, long PostStopMs)>();
```

Update EVERY existing `rows.Add((...))` call site (all six existing scenarios) to pass `audioSeconds` as the third element, e.g. `rows.Add((scenario, "simulated", audioSeconds, sw.ElapsedMilliseconds));`. Update the table printer (around lines 128–132) to:

```csharp
Console.WriteLine("| scenario | kind | audio | post-stop latency (ms) |");
Console.WriteLine("|---|---|---|---|");
foreach (var r in rows)
    Console.WriteLine($"| {r.Scenario} | {r.Kind} | {r.AudioSeconds:F1} s | {r.PostStopMs} |");
```

- [ ] **Step 3: Add the `real-local` scenario**

Add a new case to the scenario `switch` (before the `default:` unknown-scenario arm):

```csharp
case "real-local":
{
    if (modelDir is null || wavPaths.Count == 0)
    {
        Console.WriteLine("real-local: SKIPPED (requires --model-dir and at least one --wav)");
        break;
    }
    if (!ParakeetSession.ModelFilesPresent(modelDir))
    {
        Console.WriteLine($"real-local: SKIPPED (model files not found in {modelDir})");
        break;
    }
    using var session = new ParakeetSession(modelDir);
    Console.WriteLine($"# real-local: UsingDirectML={session.UsingDirectML}");
    var realBatch = new ParakeetTranscriber(session, "parakeet-tdt-0.6b-v3");
    foreach (var wavPath in wavPaths)
    {
        var name = Path.GetFileName(wavPath);
        var wavAudio = BenchAudio.Prepare(BenchAudio.ReadMono16k(wavPath), gain, leadSilenceMs);
        var seconds = wavAudio.Length / 16000.0;
        var (rms, peak, maxFrameRms) = BenchAudio.Stats(wavAudio);
        Console.WriteLine(
            $"# {name}: {seconds:F1}s gain={gain} leadSilenceMs={leadSilenceMs} " +
            $"rms={rms:F4} peak={peak:F4} maxFrameRms={maxFrameRms:F4}");

        // Batch: whole buffer through ParakeetSession; post-stop latency is the
        // full transcription time (nothing was processed before "stop").
        var swBatch = Stopwatch.StartNew();
        var batchResult = await realBatch.TranscribeAsync(wavAudio, CancellationToken.None);
        swBatch.Stop();
        rows.Add(($"real-local-batch {name}", "REAL local", seconds, swBatch.ElapsedMilliseconds));
        Console.WriteLine($"# batch[{name}]: \"{batchResult.Text}\"");

        // Streaming: ParakeetStreamingSession fed 50 ms frames at real-time
        // pace; post-stop latency is FinishAsync only. The batchFallback flag
        // proves the run genuinely streamed (FinishAsync silently falls back
        // on any streaming failure, which would fake a plausible number).
        var fellBack = false;
        var sessionLog = new CollectingLogger();
        await using var streaming = new ParakeetStreamingSession(
            session, "parakeet-tdt-0.6b-v3", PreprocessorConfig.ParakeetTdtV3,
            (mem, ct) => { fellBack = true; return realBatch.TranscribeAsync(mem, ct); },
            log: sessionLog);
        const int frame = 800; // 50 ms at 16 kHz
        for (var i = 0; i < wavAudio.Length; i += frame)
        {
            await streaming.PushAsync(
                wavAudio.AsMemory(i, Math.Min(frame, wavAudio.Length - i)), CancellationToken.None);
            await Task.Delay(50);
        }
        var swStream = Stopwatch.StartNew();
        var streamResult = await streaming.FinishAsync(wavAudio, CancellationToken.None);
        swStream.Stop();
        rows.Add(($"real-local-stream {name}", "REAL local", seconds, swStream.ElapsedMilliseconds));
        Console.WriteLine($"# stream[{name}]: fellBackToBatch={fellBack} \"{streamResult.Text}\"");
        foreach (var logLine in sessionLog.Lines)
            Console.WriteLine($"# log[{name}]: {logLine}");

        var diff = TranscriptDiff.Summarize(batchResult.Text, streamResult.Text);
        Console.WriteLine($"# diff[{name}]: {diff.Describe()}");
    }
    break;
}
```

Required `using` directives (add any not already present in Program.cs): `System.Diagnostics`, `Winpepper.Asr`, `Winpepper.Asr.Transcription`, `Microsoft.Extensions.Logging`.

- [ ] **Step 4: Add `CollectingLogger`**

Add alongside the other in-file helper fakes (`PacedTranscriber` etc., end of Program.cs):

```csharp
/// <summary>Captures ParakeetStreamingSession log lines so the bench can print
/// InteriorSilenceSkipper skip stats and fallback warnings inline.</summary>
sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger
{
    public List<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Lines.Add($"{logLevel}: {formatter(state, exception)}{(exception is null ? "" : " :: " + exception.Message)}");
}
```

- [ ] **Step 5: Build and smoke on Linux (no model on Linux — verify the skip paths and that sims still work)**

```bash
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release
dotnet exec scripts/asr-latency-bench/bin/Release/net9.0/AsrLatencyBench.dll real-local
# Create a real throwaway WAV first: the Step 1 shared-audio hoist eagerly reads
# wavPaths[0] BEFORE any scenario runs, so a nonexistent --wav path would crash
# with FileNotFoundException before the model-dir skip path is ever reached.
python3 - <<'EOF'
import wave
w = wave.open('/tmp/winpepper-smoke.wav', 'wb')
w.setnchannels(1); w.setsampwidth(2); w.setframerate(16000)
w.writeframes(b'\x00\x00' * 16000)  # 1 s of mono 16 kHz PCM silence
w.close()
EOF
dotnet exec scripts/asr-latency-bench/bin/Release/net9.0/AsrLatencyBench.dll real-local --model-dir /nonexistent --wav /tmp/winpepper-smoke.wav
dotnet exec scripts/asr-latency-bench/bin/Release/net9.0/AsrLatencyBench.dll sim-local-batch
```
Expected, in order: `real-local: SKIPPED (requires --model-dir and at least one --wav)`; `real-local: SKIPPED (model files not found in /nonexistent)`; the sim scenario prints its table with `| sim-local-batch | simulated | 10.0 s | ~3000 |` (audio column now shows a computed duration).

- [ ] **Step 6: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/Program.cs
git commit -m "feat(bench): real-local scenario -- real Parakeet batch vs streaming over WAV input"
```
Expected: `LINUX SUITE: GREEN`.

---

### Task 7: Host-side WAV generation + bench runner scripts

**Files:**
- Create: `scripts/generate-bench-wavs.ps1`
- Create: `scripts/run-bench-windows.sh`

**Interfaces:**
- Consumes: the bench CLI from Task 6; `powershell.exe` interop; System.Speech on the host.
- Produces: `./scripts/run-bench-windows.sh` — builds the bench with the Windows dotnet, stages it to `%TEMP%\winpepper-bench`, generates `normal-10s.wav` + `pause-mid.wav` into `%TEMP%\winpepper-bench-wavs` via `scripts/generate-bench-wavs.ps1 -OutDir <dir>`, runs the four phrase categories (normal / pause-mid / quiet via `--gain 0.02` / leading-silence via `--lead-silence-ms 1500`), checks `ASSEMBLYAI_API_KEY` (Process/User/Machine scopes) and runs the cloud scenarios on real speech if present, and leaves per-category logs in `artifacts/bench/`. Task 8 runs it and harvests the logs.

- [ ] **Step 1: Write the TTS generation script**

Create `scripts/generate-bench-wavs.ps1`:

```powershell
<#
.SYNOPSIS
Generate the reference speech WAVs (16 kHz mono 16-bit PCM) for the ASR
latency bench using built-in Windows TTS (System.Speech.Synthesis).

.DESCRIPTION
Writes two files into -OutDir:
  normal-10s.wav  -- a ~10 s continuous dictation phrase
  pause-mid.wav   -- a phrase with a 2.0 s mid-utterance pause (> 1.2 s;
                     exercises InteriorSilenceSkipper edge-keeping)
The quiet-talker and leading-silence phrase categories reuse normal-10s.wav
via the bench's --gain / --lead-silence-ms flags (deterministic transforms;
the bench prints RMS stats so the gain can be chosen honestly).

.EXAMPLE
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\generate-bench-wavs.ps1 -OutDir $env:TEMP\winpepper-bench-wavs
#>
param([Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(
    16000,
    [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
    [System.Speech.AudioFormat.AudioChannel]::Mono)

function New-Wav([string]$Name, [System.Speech.Synthesis.PromptBuilder]$Prompt) {
    $path = Join-Path $OutDir $Name
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $synth.Rate = 0
        $synth.SetOutputToWaveFile($path, $fmt)
        $synth.Speak($Prompt)
    }
    finally { $synth.Dispose() }
    $seconds = [math]::Round(((Get-Item $path).Length - 44) / 32000.0, 1)  # 16 kHz * 2 B/sample
    Write-Host "wrote $Name (~$seconds s)"
}

$normal = New-Object System.Speech.Synthesis.PromptBuilder
$normal.AppendText('Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.')
New-Wav 'normal-10s.wav' $normal

$pause = New-Object System.Speech.Synthesis.PromptBuilder
$pause.AppendText('Send the quarterly report to the finance team')
$pause.AppendBreak([TimeSpan]::FromSeconds(2.0))
$pause.AppendText('and schedule the follow up meeting for Thursday afternoon.')
New-Wav 'pause-mid.wav' $pause
```

- [ ] **Step 2: Write the Windows bench runner**

Create `scripts/run-bench-windows.sh`:

```bash
#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet (over the
# \\wsl.localhost UNC path), stage the build output to a Windows-local %TEMP%
# dir (ONNX/DirectML native library loads from UNC are unreliable), generate
# the reference TTS WAVs on the host, and run the real Parakeet model
# batch-vs-streaming over the four phrase categories.
#
# Host safety: the only host writes are %TEMP% staging dirs and NuGet
# restore. The model dir is read, never written. Never touches a running
# Winpepper.exe or %LOCALAPPDATA%\winpepper.
#
# Usage: ./scripts/run-bench-windows.sh
# Output: artifacts/bench/<category>.log
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-bench-windows: powershell.exe not found at $PS" >&2; exit 2; }

UNC_ROOT="$(wslpath -w "$HERE")"
MODEL_DIR='C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3'
OUT="$HERE/artifacts/bench"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [2/4] Stage bench output to %TEMP%\\winpepper-bench ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-bench'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [3/4] Generate TTS WAVs on the host ==="
gen_script="$UNC_ROOT"'\scripts\generate-bench-wavs.ps1'
ps_run 300 "$OUT/tts.log" "& '$gen_script' -OutDir (Join-Path \$env:TEMP 'winpepper-bench-wavs')"

echo "=== [4/4] real-local batch-vs-streaming, four phrase categories ==="
run_category() { # run_category <name> <wavfile> [extra bench args...]
  local name="$1" wav="$2"; shift 2
  echo "--- $name ---"
  ps_run 1800 "$OUT/$name.log" "
    Set-Location (Join-Path \$env:TEMP 'winpepper-bench')
    dotnet exec AsrLatencyBench.dll real-local --model-dir '$MODEL_DIR' --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\$wav') $*"
}

run_category normal        normal-10s.wav
run_category pause-mid     pause-mid.wav
run_category quiet         normal-10s.wav --gain 0.02
run_category lead-silence  normal-10s.wav --lead-silence-ms 1500

echo "=== Cloud (AssemblyAI) check ==="
key_probe="
  \$k = \$env:ASSEMBLYAI_API_KEY
  if (-not \$k) { \$k = [Environment]::GetEnvironmentVariable('ASSEMBLYAI_API_KEY','User') }
  if (-not \$k) { \$k = [Environment]::GetEnvironmentVariable('ASSEMBLYAI_API_KEY','Machine') }"
if ps_run 60 "$OUT/cloud-check.log" "$key_probe
  if (\$k) { exit 0 } else { exit 1 }"; then
  echo "--- cloud (real speech WAV) ---"
  ps_run 1800 "$OUT/cloud.log" "$key_probe
    \$env:ASSEMBLYAI_API_KEY = \$k
    Set-Location (Join-Path \$env:TEMP 'winpepper-bench')
    dotnet exec AsrLatencyBench.dll real-remote-batch real-remote-stream --wav (Join-Path \$env:TEMP 'winpepper-bench-wavs\\normal-10s.wav')"
else
  echo "cloud: ASSEMBLYAI_API_KEY not set on the host in any scope -- cloud rows NOT RUN (record honestly in the evidence doc)"
fi

echo "run-bench-windows: done -- logs in $OUT"
```

- [ ] **Step 3: Syntax-check both scripts**

```bash
chmod +x scripts/run-bench-windows.sh
bash -n scripts/run-bench-windows.sh && echo BASH-OK
/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -Command \
  "\$t=[System.Management.Automation.PSParser]::Tokenize((Get-Content -Raw '$(wslpath -w "$PWD")\scripts\generate-bench-wavs.ps1'),[ref]\$null); 'PS-OK'"
```
Expected: `BASH-OK` and `PS-OK` (the second command parses the ps1 without executing it; run it from the worktree root so `$PWD` resolves).

- [ ] **Step 4: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/generate-bench-wavs.ps1 scripts/run-bench-windows.sh
git commit -m "build: add host-side TTS WAV generation and Windows bench runner scripts"
```
Expected: `LINUX SUITE: GREEN`.

---

### Task 7b: Root-cause and fix the real-model streaming truncation defect

**Why this task exists (validation finding, 2026-07-25):** a pre-plan validation probe ran the production classes against the REAL int8 Parakeet model on the host and **falsified** the assumption that chunked streaming produces sensible transcripts. The graphs accept the chunked inputs (no ONNX Runtime errors, `fellBackToBatch=False`), but only the FIRST encoded chunk emits tokens — every subsequent encode decodes to 100% blanks, silently truncating ~75% of each utterance (e.g. normal-10s streamed to just "Please summarize the meeting notes from this."). The failure is deterministic, reproduced on BOTH DirectML and CPU EPs, with `leftContextMelFrames=0`, and with 600-frame chunks — so it is not the context-discard arithmetic, not chunk size, and not a DirectML quirk. Because no exception is thrown, `_corrupt` never sets and the batch fallback never triggers: **production dictation with `StreamingEnabled=true` truncates real dictations longer than ~2 s.** Full evidence and instrumentation data: the probe under `artifacts/v5-probe/` (gitignored — `Program.cs`, `run-temp-staged.log`, `run-diag.log`) and the validation report `../../../.the-usual-logs/streaming-verification/reports/V5.md`.

Root-cause candidates (from the probe's instrumentation; start here):
1. **Running-stats normalization mismatch** — chunk 1 is normalized by exactly its own stats (batch-like), later chunks by mixed running stats (`RunningMelNormalizer.cs:41–53`); the int8-quantized joint may be highly sensitive to that distribution shift.
2. **Carried decoder LSTM state across the encoder context break** — the carried `TdtDecoderState` (LastToken + LSTM h/c, per `TdtGreedyDecoder.cs:38–96`) combined with a fresh chunk's encoder output argmaxes blank on every frame with large duration skips.
3. Int8 quantization sensitivity amplifying either of the above.

**Files:**
- Modify: `src/Winpepper.Asr/**` (streaming path — likely `ParakeetStreamingSession.cs`, `RunningMelNormalizer.cs`, and/or decoder-state handling) + matching tests in `tests/Winpepper.Asr.Tests/`
- Possibly modify: `src/Winpepper.Core/Settings/AppSettings.cs` (safety-valve path only — `StreamingEnabled` default)

**Interfaces:**
- Consumes: the Task 6 bench `real-local` scenario (the reproduction harness) + Task 7's WAV generation; the probe artifacts above.
- Produces: a streaming path whose real-model transcripts reach word parity with batch (the precondition for Task 8's acceptance), OR the documented safety-valve state below.

- [ ] **Step 1: Reproduce with the bench**

Run `./scripts/run-bench-windows.sh` (or invoke the bench's `real-local` scenario directly on the normal-10s WAV) and confirm the truncation: stream transcript cut to ~first chunk, `fellBackToBatch=False`. This proves the harness sees the defect before you change anything.

- [ ] **Step 2: Test the hypotheses cheaply, in order**

Suggested experiments (each is a small, revertable change run against the bench):
- Normalize each chunk window with batch-equivalent stats (or re-normalize the full accumulated audio per encode) to test hypothesis 1.
- Reset (or re-prime) the decoder state at chunk boundaries to test hypothesis 2 — note this may harm cross-chunk continuity; measure with the word diff.
Record per-experiment `# stream[...]` transcripts + non-blank emission counts. Keep experiments out of commits; only the chosen fix lands.

- [ ] **Step 3: Implement the fix with tests**

Land the minimal fix in `src/Winpepper.Asr` with unit tests that encode the failure shape (the fake backend can now be taught to reproduce "later chunks decode to blanks" at whatever seam the root cause reveals). `WarningsAsErrors=nullable` applies. Acceptance: bench `real-local` on normal-10s reaches `TrivialOnly` word parity (or clearly characterized near-parity) with `fellBackToBatch=False`.

- [ ] **Step 4: Safety valve — only if the defect is not tractably fixable in this task**

If root-causing shows the fix is research-grade (e.g. the chunked int8 approach fundamentally can't work), do NOT leave the silent truncation in place. Instead: (a) make the failure LOUD — detect pathological streams (e.g. zero non-blank emissions from any post-first encode while speech frames were fed) and set the corrupt/fallback path so `FinishAsync` returns the batch fallback result; (b) check `StreamingEnabled`'s default in `AppSettings` and, if it defaults on, flip the default to OFF with a comment citing this defect; (c) record the defect honestly in Task 8's evidence doc — the acceptance assessment must then say parity NOT met and latency numbers not citable.

- [ ] **Step 5: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add <changed files>
git commit -m "fix(asr): <root cause> made real-model streaming truncate after the first chunk"
```
Expected: `LINUX SUITE: GREEN`. (Safety-valve path: commit message `fix(asr): fall back loudly when streaming decodes to blanks; default streaming off`.)

---

### Task 8: Run the bench on the real host and write the evidence report

This closes accepted risks A1 and A16 from the streaming-transcription work with REAL model evidence.

**Files:**
- Create: `docs/plans/2026-07-25-streaming-verification-evidence.md`

**Interfaces:**
- Consumes: `./scripts/run-bench-windows.sh` (Task 7); Task 7b's streaming fix (or its documented safety-valve state); logs land in `artifacts/bench/*.log`.
- Produces: the committed evidence report. Task 9 appends the final gate summary to it; Task 10's AGENTS.md entry links to it.

- [ ] **Step 1: Run the bench end-to-end**

Run with a generous outer timeout (~30–45 min: model load is a 652 MB encoder, each streaming run takes at least real-time):
```bash
./scripts/run-bench-windows.sh
```
Expected: four `real-local` category logs in `artifacts/bench/`, each containing `# real-local: UsingDirectML=...`, `# batch[...]`, `# stream[...] fellBackToBatch=False`, `# diff[...]`, and a final markdown table with `real-local-batch` / `real-local-stream` rows. Plus either a `cloud.log` or the honest `cloud: ... NOT RUN` line.

- [ ] **Step 2: Validate the runs are genuinely streaming and honestly quiet**

Check each category log:
- `fellBackToBatch=False` in every `# stream[...]` line. If `True` for **quiet**: the gain is outside the usable band. Measured facts (validated 2026-07-25 on these exact TTS clips): the target window (printed as `maxFrameRms`) is `0.002 < maxFrameRms < 0.0133` — above the leading gate, below the quiet-talker guard threshold (`0.002 / 0.15`); the feasible gain band is roughly **0.01–0.03** (`--gain 0.02` verified end-to-end: guard active, zero drops, streaming engaged). Do NOT raise the gain above ~0.04 — `0.05` already trips the guard boundary (effective maxFrameRms 0.01357 > 0.01333) and `0.08` is far outside the window. Adjust within 0.01–0.03 by editing the `run_category quiet ...` invocation temporarily or invoking the bench directly via powershell. Record the gain actually used. If `True` for **any other category**, read the `# log[...]` lines for the failure and fix the underlying issue before writing evidence — do not report fallen-back numbers as streaming numbers. (Exception: if Task 7b ended on the safety-valve path, `fellBackToBatch=True` is the DESIGNED outcome — record it as such, per the acceptance section.)
- The **pause-mid** log's `# log[...]` lines should show `InteriorSilenceSkipper` skip stats (skipped ms > 0, runs skipped ≥ 1) — that proves the edge-keeping path ran.
- The **lead-silence** run should still produce the full transcript (the leading gate drops silence frames entirely).

- [ ] **Step 3: Write the evidence report from the captured logs**

Create `docs/plans/2026-07-25-streaming-verification-evidence.md` with this structure, filling every `<...>` from the actual logs — never from memory or estimation:

````markdown
# Streaming Transcription: Real-Model Verification Evidence

Replaces the simulated latency/quality evidence from
`docs/plans/2026-07-25-streaming-transcription.md` (whose committed bench rows
were simulated by construction: paced fakes returning the literal string
"simulated transcript" over tone-sweep audio). Closes the two accepted risks
from that work's assumption ledger:

- **A1** — "Chunked Parakeet encoding (1 s left context, running-stats norm,
  carried TDT state) yields acceptable transcript quality" (accepted with a
  mandatory Windows post-merge batch-vs-streamed transcript diff — this doc).
- **A16** — "Simulated local + optional real remote numbers satisfy the
  'prove it with before/after' requirement" (accepted; superseded by the real
  numbers below).

## Method

- Harness: `scripts/run-bench-windows.sh` → `scripts/asr-latency-bench`
  `real-local` scenario (production `ParakeetSession` batch vs
  `ParakeetStreamingSession` fed 50 ms frames at real-time pace; post-stop
  latency = time from last sample pushed to final transcript).
- Host: DANDESKTOP, Windows dotnet <version>, UsingDirectML=<True/False>.
- Model: `C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3`
  (read-only).
- Audio: host TTS via `scripts/generate-bench-wavs.ps1` (System.Speech,
  16 kHz mono 16-bit). Quiet category = normal phrase at `--gain <used>`
  (maxFrameRms <value>, inside the quiet-talker guard window
  0.002–0.0133); leading-silence category = normal phrase with
  `--lead-silence-ms 1500`.
- Streaming honesty check: every streamed row below ran with
  `fellBackToBatch=False` (the bench flags silent batch fallback).
- Streaming defect context: a 2026-07-25 validation probe falsified the
  original streaming-parity assumption against the real model (only the
  first encoded chunk emitted tokens; later encodes decoded to blanks).
  Task 7b <fixed it via <root cause + fix> / applied the loud-fallback +
  default-off safety valve>; the results below reflect that state.

## Results (REAL local Parakeet)

| category | audio | batch post-stop (ms) | stream post-stop (ms) | transcript diff |
|---|---|---|---|---|
| normal (~10 s dictation) | <s> s | <ms> | <ms> | <IDENTICAL after normalization / N word diffs> |
| pause-mid (2.0 s interior pause) | <s> s | <ms> | <ms> | <...> |
| quiet (gain <g>) | <s> s | <ms> | <ms> | <...> |
| lead-silence (1500 ms) | <s> s | <ms> | <ms> | <...> |

### Transcripts (verbatim)

**normal** — batch:
> <batch transcript>

**normal** — stream:
> <stream transcript>

<repeat for pause-mid, quiet, lead-silence>

### Word-level diffs

<paste each `# diff[...]` line; for any non-trivial diff, characterize it
honestly: which words differ, and whether it is a real quality divergence>

### InteriorSilenceSkipper telemetry (pause-mid)

<paste the `# log[pause-mid.wav]: ...` skip-stat lines — proves edge-keeping ran>

## Acceptance assessment

- Transcript parity: <met/not met per category — the bar is streamed ==
  batch after whitespace/punctuation normalization; any word-level diff is
  listed above and characterized honestly>.
- Latency: streamed post-stop <ms> vs batch <ms> for the ~10 s phrase —
  <X>% reduction. Bar: "dramatically lower" (streamed well under half of
  batch). <met/not met>. NOTE: latency may only be cited as met if
  transcript parity holds for that category — a truncated stream finishes
  early, which makes its latency number meaningless.
- If Task 7b ended on the safety-valve path (defect not fixed): state
  explicitly that parity is NOT met, document the defect and the mitigation
  (loud batch fallback; `StreamingEnabled` default off), and do not present
  streamed latency as the before/after proof.

## Cloud (AssemblyAI)

<EITHER: real-remote rows from artifacts/bench/cloud.log (real speech WAV)
OR: "NOT RUN — ASSEMBLYAI_API_KEY is not set in any scope on the host; the
app's stored key is DPAPI-encrypted for the app and not usable from the
bench. Local-model evidence above is the hard requirement and is complete.">

## Windows pre-push gate result

<filled by the final gate run — see "Gate summary" below>

## Cross-references & environment honesty

- `scripts/windows-sandbox/README.md` (untracked, main checkout) says no
  audio testing is possible in Windows Sandbox ("No real microphone ... you
  cannot test the full hold-to-dictate audio pipeline"). That remains true
  for Sandbox, but model-level audio-FILE testing works today via
  `./scripts/run-bench-windows.sh` (this doc's harness). The file is
  untracked in the main checkout, so this note lives here instead of editing
  it.
- Full end-to-end audio (mic → hotkey → paste) is covered by
  `docs/manual-test.md`'s QEMU audio-passthrough procedure, which is
  currently NOT provisioned on this machine: no VM image, no PulseAudio
  server under WSLg, no piper, no sshpass. Stated honestly rather than
  implying it works.
````

- [ ] **Step 4: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add docs/plans/2026-07-25-streaming-verification-evidence.md
git commit -m "docs: real Parakeet batch-vs-streaming evidence (closes A1/A16 simulated-evidence risk)"
```
Expected: `LINUX SUITE: GREEN`. The "Windows pre-push gate result" section still holds a placeholder line at this point — Task 9 fills it; that is the ONLY permitted unfilled section in this commit.

---

### Task 9: Final full gate run, recorded in the evidence doc

**Files:**
- Modify: `docs/plans/2026-07-25-streaming-verification-evidence.md` (fill the "Windows pre-push gate result" section)

**Interfaces:**
- Consumes: `./scripts/windows-gate.sh` (Task 3, proven green in Task 4); the evidence doc (Task 8).
- Produces: the final SUCCESS state — a committed evidence doc containing the green gate summary for the branch's final code state.

- [ ] **Step 1: Run the gate end-to-end on the final code state**

```bash
./scripts/windows-gate.sh 2>&1 | tee artifacts/windows-gate/final-run.txt
```
Expected: `GATE: GREEN`, 12 OK run lines, grand total roughly ~1300+ (cross-check only — record the actual number; `Skipped > 0` from the Llama self-skips is expected and must be noted honestly). If RED (a bench/test change since Task 4 broke something), run this fix cycle until GREEN:
```bash
# make the fix, then:
./scripts/linux-tests.sh          # must print LINUX SUITE: GREEN
git add <changed files>
git commit -m "fix: <what the final gate run surfaced>"
./scripts/windows-gate.sh 2>&1 | tee artifacts/windows-gate/final-run.txt
```

- [ ] **Step 2: Record the summary in the evidence doc**

Replace the placeholder under `## Windows pre-push gate result` with the verbatim `================ windows-gate summary ================` block from `artifacts/windows-gate/final-run.txt` (the per-run OK lines, the grand total, and `GATE: GREEN`), inside a fenced code block, plus the date and the commit hash it ran against (`git rev-parse --short HEAD`).

- [ ] **Step 3: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add docs/plans/2026-07-25-streaming-verification-evidence.md
git commit -m "docs: record green windows-gate run in the verification evidence"
```
Expected: `LINUX SUITE: GREEN`.

---

### Task 10: Docs cleanup — supersede stale annotations, document the gate

**Files:**
- Modify: `docs/manual-test.md` (5 stale annotation sites)
- Modify: `AGENTS.md`

**Interfaces:**
- Consumes: proven-green `scripts/windows-gate.sh` and `scripts/run-bench-windows.sh`; evidence doc path.
- Produces: docs that point at the scripted gate as THE pre-push procedure.

- [ ] **Step 1: Supersede the five stale `docs/manual-test.md` annotations**

The Plan 3/4/5/6 "blocked/deferred" notes cite the WinAppSDK XAML in-process compiler blocker, which is resolved by WinAppSDK 1.8.260508005 + `-p:UseXamlCompilerExecutable=true` (commit `3b1903e` made it work from WSL2/UNC too; the file's own "Verified working launch procedure (2026-05-16)" section documents the resolution but the annotations were never updated). Add a superseding note at each site — do not delete the historical notes:

1. **Plan 3 (after the line-125 blockquote ending "...build green on Linux and on the VM.")** insert:
```markdown
> **Superseded (2026-07-25):** resolved — WinAppSDK 1.8.260508005 + `-p:UseXamlCompilerExecutable=true` builds the App (see "Verified working launch procedure" below), including from a WSL2 UNC checkout (commit 3b1903e). Run `./scripts/windows-gate.sh` from WSL for the scripted build + full-suite gate.
```

2. **Plan 4 (after line 159, "...runs once Winpepper.App builds.")** insert:
```markdown
> **Superseded (2026-07-25):** Winpepper.App builds now (WinAppSDK 1.8 + `-p:UseXamlCompilerExecutable=true`, commit 3b1903e); the blocker above no longer applies.
```

3. **Plan 5 setup step (after line 188, the numbered item saying "WinUI compiler PNSE is expected (carry-forward)...")** insert as an indented note under that list item:
```markdown
   > **Superseded (2026-07-25):** the WinUI compiler PNSE no longer occurs with WinAppSDK 1.8 + `-p:UseXamlCompilerExecutable=true` (commit 3b1903e); expect a clean build.
```

4. **Plan 6 MSI install smoke (after the lines-235–242 blockquote ending "...execute the procedure below end-to-end.")** insert:
```markdown
> **Superseded (2026-07-25):** the Winpepper.App build blocker is resolved (WinAppSDK 1.8 + `-p:UseXamlCompilerExecutable=true`, commit 3b1903e); the procedure below is executable.
```

5. **Plan 6 MSI upgrade smoke (after the lines-308–312 blockquote ending "...respects the user's autostart preference.")** insert:
```markdown
> **Superseded (2026-07-25):** the Winpepper.App build blocker is resolved (WinAppSDK 1.8 + `-p:UseXamlCompilerExecutable=true`, commit 3b1903e); the procedure below is executable.
```

(Line numbers are pre-edit anchors; locate each site by its quoted text since earlier insertions shift later lines.)

- [ ] **Step 2: Add the gate + bench procedure to `AGENTS.md`**

`AGENTS.md` is currently a single bullet list (7 lines). Append these bullets at the end:

```markdown
  - From WSL, THE way to satisfy the Windows pre-push rule is `./scripts/windows-gate.sh`:
    it builds `Winpepper.App` (Release, `-p:UseXamlCompilerExecutable=true`) and builds + runs
    all 9 test projects (12 project/TFM runs) on the Windows host via `powershell.exe` interop
    over the `\\wsl.localhost` UNC path. Exit 0 with `GATE: GREEN` = pass. It never installs
    the MSI, never launches or kills `Winpepper.exe`, and never writes to
    `%LOCALAPPDATA%\winpepper`.
- **ASR model-level audio evidence:** `./scripts/run-bench-windows.sh` builds the latency bench
  with the Windows dotnet, generates reference TTS WAVs on the host, and runs the real Parakeet
  model batch-vs-streaming over them (transcripts, post-stop latency, word-level diff). Recorded
  results: `docs/plans/2026-07-25-streaming-verification-evidence.md`.
```

(The first bullet is indented two spaces to sit under the existing "Before pushing" bullet as its how-to; match the file's existing indentation style.)

- [ ] **Step 3: Verify the edits render sanely**

```bash
grep -n "Superseded (2026-07-25)" docs/manual-test.md
grep -n "windows-gate.sh" AGENTS.md docs/manual-test.md
```
Expected: 5 `Superseded` hits in manual-test.md; `windows-gate.sh` appears in both files.

- [ ] **Step 4: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add docs/manual-test.md AGENTS.md
git commit -m "docs: supersede stale XAML-blocker annotations; document the WSL Windows gate"
```
Expected: `LINUX SUITE: GREEN`. Final check that everything is committed and nothing is pushed:
```bash
git status --short          # expect empty (artifacts/ is gitignored)
git log --oneline main..HEAD | head -30
```

---

## Self-Review Notes (performed at plan-writing time)

- **Spec coverage:** Deliverable 1 (merge) → Tasks 1–2; deliverable 2 (gate script incl. app build, 9 builds, 12 `dotnet exec` runs, loud failure, hang caveat, host-safety constraints) → Task 3; deliverable 3 (real quality+latency evidence: bench extension over production classes, 4 phrase categories incl. >1.2 s pause / quiet / leading silence, TTS generation, %TEMP% staging, honest cloud handling, committed markdown report) → Tasks 5–8; deliverable 4 (full gate green end-to-end, Windows fixes with Linux green per fix, gate summary in evidence doc) → Tasks 4 and 9; deliverable 5 (manual-test.md supersessions, AGENTS.md gate procedure, windows-sandbox cross-reference placed in the evidence doc because the file is untracked and lives in the main checkout, honest QEMU non-provisioned statement) → Task 10 + Task 8's cross-reference section. No requirement was deferred to "future work".
- **No silent deferrals:** the only test doubles are the bench's pre-existing sim fakes (untouched); the new `real-local` path uses the production `ParakeetSession`/`ParakeetStreamingSession` directly, with an explicit `fellBackToBatch` honesty flag so a silent batch fallback cannot masquerade as streaming evidence (Task 8 Step 2 gates on it).
- **Type consistency:** `BenchAudio.ReadMono16k/ApplyGain/PrependSilence/Prepare/Stats` and `TranscriptDiff.Normalize/Summarize` + `DiffSummary.Describe()` are used identically in Tasks 5, 6; the bench CLI flags (`--wav/--model-dir/--gain/--lead-silence-ms`, scenario `real-local`) match between Tasks 6, 7, 8; script names (`linux-tests.sh`, `windows-gate.sh`, `generate-bench-wavs.ps1`, `run-bench-windows.sh`) are consistent throughout.
- **Known judgment calls (documented, not hidden):** quiet/lead-silence categories are deterministic transforms of the normal TTS phrase (explicitly allowed by the spec: "scale amplitude down in the bench or TTS volume"); helper unit tests are compiled into `Winpepper.Asr.Tests` via `<Compile Include>` (repo precedent) to preserve the 9-project/12-run gate contract; line numbers cited for `Program.cs`/`manual-test.md` are pre-edit anchors and each edit instruction also carries a text anchor.

## Load-Bearing Validation Update (2026-07-25)

Nineteen load-bearing assumptions were validated against the real environment before execution (ledger + full evidence: `.worktrees/.the-usual-logs/streaming-verification/load-bearing-ledger.md` and `reports/V1.md`–`V6.md` there; validator artifacts in the worktree's gitignored `artifacts/`). Plan changes applied:

- **FALSIFIED — real-model streaming truncates (was: streaming parity assumed reachable):** only the first encoded chunk emits tokens against the real int8 graphs; silent (`fellBackToBatch=False`), EP-independent. Production `StreamingEnabled=true` truncates dictations >~2 s. → New **Task 7b** (root-cause & fix, with a loud-fallback + default-off safety valve); Task 8 method/acceptance reworked (latency citable only with parity).
- **FALSIFIED — cross-OS incremental builds:** Windows-over-UNC builds after a Linux build hit transient `CS0006`. → gate builds use `--no-incremental`; new global constraint forbids concurrent cross-OS builds.
- **FALSIFIED — timeout kills the Windows side:** interop children survive a WSL `timeout`, holding locks. → gate gained `kill_orphans` (kills only this tree's dotnet.exe orphans) after any TIMEOUT.
- **FALSIFIED — single shared audio buffer in bench Program.cs:** audio is synthesized per scenario (≥5 call sites). → Task 6 Step 1 rewritten to hoist a shared buffer and update every call site.
- **Corrected numbers:** Linux baseline is **1044** (not 1050), measured via the exact Task 1 method; Windows totals are a cross-check ("~1300+, record actual"); quiet-category gain is **0.02** (0.05 trips the guard boundary; 0.08 guidance was the wrong direction).
- **Verified (now facts, not assumptions):** worktree-UNC builds (incl. dual-TFM test projects, no shim needed), UNC in-place `dotnet exec` incl. onnxruntime native load, %TEMP% staging self-containment, model shared-read while Winpepper.exe runs, TTS WAV format/quality (batch Parakeet transcribes it near-perfectly), 2 s AppendBreak → real InteriorSilenceSkipper skips, session reuse pattern, desktop currently interactive.
- **Accepted residual risks (documented in the gate script):** the hook test's ~200 ms real WH_KEYBOARD_LL window (kept — only real hook coverage; worst case one swallowed keystroke, no state change); Llama tests self-skip (qwen GGUF absent — record `Skipped > 0` honestly); desktop interactivity is a runtime precondition surfaced by the per-run timeout.
