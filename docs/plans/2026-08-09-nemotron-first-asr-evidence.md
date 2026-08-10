# Nemotron-First ASR council fix batch — verification evidence

Branch: `feat/nemotron-first-asr` (forked from `080e4f1`). Fix batch plan:
`docs/plans/2026-08-09-nemotron-first-asr.md`. This doc records the
verification evidence the council review found missing, per its item 8.

## Linux suite at final code HEAD

Run date: 2026-08-09 15:52 PDT. HEAD: `5fa2ae9`.

```
linux-tests grand total: 1854 tests
LINUX SUITE: GREEN
```

## Windows pre-push gate result

Run date: 2026-08-09. Ran against commit `5fa2ae9`
(`git rev-parse --short HEAD`, stamped immediately before launching the gate —
the gate logs themselves contain no SHA).

**The gate is BLOCKED-ENVIRONMENTAL, not GREEN.** *(Superseded 2026-08-09
evening: a single all-GREEN run was later obtained at `476c2ac` — see
"Gate GREEN at 476c2ac" below.)* Three full attempts were made
at `5fa2ae9` (the SHA stamp was re-recorded before every run; HEAD did not move
between runs); all three went RED on a WSL→Windows interop (vsock) outage, not
on any code failure. Zero CS errors appeared in any build log across all 3 runs.

- RUN 1 (2026-08-09 15:52–16:10): GATE: RED. Winpepper.App build FAILED — log
  67 bytes, verbatim: `<3>WSL (2750102 - ) ERROR: UtilAcceptVsock:271: accept4 failed 110`.
  ALL 12 test project/TFM runs OK (2705 tests, 0 failures; Skipped: 45 Cleanup
  net9.0-windows, 1 Audio net9.0-windows, 2+2 Platform).
- RUN 2 (16:11–16:32): GATE: RED. Winpepper.App build OK. 7 of 12 test runs OK;
  5 runs (Audio net9.0, Cleanup net9.0, Corrections net9.0, History net9.0,
  IntegrationTests net9.0) FAILED with 67-byte logs, all verbatim
  `UtilAcceptVsock:271: accept4 failed 110`; 2261 tests passed across the OK
  runs, 0 failures.
- RUN 3 (16:37–16:44): GATE: RED. Total interop outage: the App build completed
  OK before the outage took hold; every test-project build stage then FAILED with
  the same 67-byte vsock log; all 12 runs exit 129
  `<no summary line>` (Set-Location PathNotFound — downstream of unbuilt DLLs);
  grand total 0 tests.

Union across runs 1+2: every gate stage succeeded at `5fa2ae9` (run 2's App
build OK proves the Windows-only compile of Tasks 6/7/8). A single all-GREEN
run remains outstanding.

Verbatim summary block from `/tmp/windows-gate-nemotron-fixbatch.log`
(run 3 of 3; full log also preserved at
`/tmp/windows-gate-nemotron-fixbatch-run3.log`):

```
================ windows-gate summary ================
Winpepper.App build: OK
Winpepper.Asr.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Asr.Tests.log
Winpepper.Audio.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Audio.Tests.log
Winpepper.Cleanup.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Cleanup.Tests.log
Winpepper.Core.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Core.Tests.log
Winpepper.Corrections.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Corrections.Tests.log
Winpepper.History.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.History.Tests.log
Winpepper.IntegrationTests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.IntegrationTests.log
Winpepper.Models.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Models.Tests.log
Winpepper.Platform.Tests build: FAILED (exit 1) -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/build-Winpepper.Platform.Tests.log
Winpepper.Asr.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Asr.Tests-net9.0.log
Winpepper.Audio.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Audio.Tests-net9.0.log
Winpepper.Audio.Tests (net9.0-windows10.0.19041.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Audio.Tests-net9.0-windows10.0.19041.0.log
Winpepper.Cleanup.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Cleanup.Tests-net9.0.log
Winpepper.Cleanup.Tests (net9.0-windows10.0.19041.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Cleanup.Tests-net9.0-windows10.0.19041.0.log
Winpepper.Core.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Core.Tests-net9.0.log
Winpepper.Corrections.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Corrections.Tests-net9.0.log
Winpepper.History.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.History.Tests-net9.0.log
Winpepper.IntegrationTests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.IntegrationTests-net9.0.log
Winpepper.Models.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Models.Tests-net9.0.log
Winpepper.Platform.Tests (net9.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Platform.Tests-net9.0.log
Winpepper.Platform.Tests (net9.0-windows10.0.19041.0): FAILED (exit 129) <no summary line> -- /home/dan/code/winpepper/.worktrees/nemotron-first-asr/artifacts/windows-gate/run-Winpepper.Platform.Tests-net9.0-windows10.0.19041.0.log
grand total tests: 0 (cross-check only; roughly ~1300+ across 12 runs -- record the actual number)
GATE: RED
```

Honesty note: Skipped totals 50 across the 12 runs in run 1, the only attempt
where all 12 test runs executed (45 Cleanup net9.0-windows, 1 Audio
net9.0-windows, 2+2 Platform — all self-skips). Remediation for the user: run
`wsl.exe --shutdown` from Windows, then re-run `./scripts/windows-gate.sh`
(this kills every WSL session, so it cannot be run from inside WSL).
Commits after the gate SHA are docs-only — verified: `git diff --stat
5fa2ae9..HEAD` touches only docs/plans/*.

### Gate GREEN at 476c2ac (2026-08-09 evening — supersedes BLOCKED status)

Four further full gate runs were made at `476c2ac` (SHA stamped via
`git rev-parse --short HEAD` before each run; HEAD did not move; worktree
clean) without restarting WSL:

- RUN 4 (~17:5x–18:1x): GATE: RED. Asr.Tests + Audio.Tests build stages hit the
  67-byte vsock log (`UtilAcceptVsock:271: accept4 failed 110`), their runs
  exit-129 downstream. All other stages OK. One genuine test failure:
  `ModelCardViewModelDispatchTests.ReportProgress_LateByteReportCannotOvertakeQueuedComplete`
  — `System.TimeoutException: The progress bridge did not drain through the
  manual dispatcher` (Models.Tests net9.0: 159 total, 1 failed). Flake
  determination below.
- RUN 5: GATE: RED. Only Audio.Tests build hit the vsock error (runs exit-129
  downstream); all 10 executed test runs OK — 2433 tests, 0 failures
  (Models.Tests 159/0: the RUN 4 failure did not reproduce).
- RUN 6: GATE: RED. Platform.Tests build + Corrections/History run stages hit
  the vsock error; all other stages OK — 1822 tests, 0 failures.
- RUN 7 (~18:5x–19:1x): **GATE: GREEN.** All 12 project/TFM runs OK —
  **2705 tests, 0 errors, 0 failures**, Skipped: 50 (45 Cleanup
  net9.0-windows, 1 Audio net9.0-windows, 2+2 Platform — all self-skips).
  Winpepper.App build OK.

Flake note (backlog): the RUN 4 `ReportProgress_LateByteReportCannotOvertakeQueuedComplete`
failure was re-run in isolation on the Windows host 3× at the same SHA
(`dotnet exec Winpepper.Models.Tests.dll -method ...`): 3/3 PASS (0.83–0.93 s),
and it passed inside full runs 5, 6, and 7. Classified as a load-sensitive
timing flake in the test's ManualDispatcher drain wait, not a product defect.
Tightening the test's drain deadline handling is a recorded backlog item.

Verbatim RUN 7 summary block:

```
================ windows-gate summary ================
Winpepper.App build: OK
Winpepper.Asr.Tests (net9.0): OK     Winpepper.Asr.Tests  Total: 374, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 11.157s
Winpepper.Audio.Tests (net9.0): OK     Winpepper.Audio.Tests  Total: 135, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.642s
Winpepper.Audio.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Audio.Tests  Total: 137, Errors: 0, Failed: 0, Skipped: 1, Not Run: 0, Time: 0.790s
Winpepper.Cleanup.Tests (net9.0): OK     Winpepper.Cleanup.Tests  Total: 222, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.300s
Winpepper.Cleanup.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Cleanup.Tests  Total: 296, Errors: 0, Failed: 0, Skipped: 45, Not Run: 0, Time: 19.785s
Winpepper.Core.Tests (net9.0): OK     Winpepper.Core.Tests  Total: 495, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 2.107s
Winpepper.Corrections.Tests (net9.0): OK     Winpepper.Corrections.Tests  Total: 31, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.860s
Winpepper.History.Tests (net9.0): OK     Winpepper.History.Tests  Total: 52, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.487s
Winpepper.IntegrationTests (net9.0): OK     Winpepper.IntegrationTests  Total: 4, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.522s
Winpepper.Models.Tests (net9.0): OK     Winpepper.Models.Tests  Total: 159, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.594s
Winpepper.Platform.Tests (net9.0): OK     Winpepper.Platform.Tests  Total: 398, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 1.608s
Winpepper.Platform.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Platform.Tests  Total: 402, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 7.209s
GATE: GREEN
```

The pre-push/merge precondition (one all-GREEN gate run at final HEAD) is
satisfied at `476c2ac`.

## Review-claims record (corrected 2026-08-09)

A previous recap of this branch claimed a "second independent review pass"
without locatable artifacts, and an earlier draft of this correction was
going to withdraw the claim as artifact-free. Load-bearing validation of
this fix batch then located substantiating artifacts for that review pass
(item 3 below) in the workflow logs archive — not the repo, which is why
they were initially missed — so the claim is substantiated, not withdrawn.
The execute-stage review's artifact files also survive, outside both the
repo and the logs archive (item 2 below records their verified location
and a durability caveat). The
full review record over this branch, with artifact locations (workflow
logs under
`.the-usual-logs/nemotron-first-asr/`, archived under
`prior-run-archive-20260809/`):

1. Plan-stage load-bearing validation — assumption ledger + validator
   reports V1–V10 (archived).
2. Execute-stage whole-branch review + re-review — initial verdict "With
   fixes", re-review confirmed both fixes resolved. Evidence: the archived
   `execute-result.json` records the review verdict, and its execution
   notes point to the SDD ledger ("progress.md in the worktree git dir").
   The review artifact FILES themselves were verified present on
   2026-08-09 in that same worktree git directory —
   `<main checkout>/.git/worktrees/nemotron-first-asr/sdd/` — including
   `final-review-fix-report.md` and `review-c73b9f1..4d5e63d.diff`
   (alongside the per-task briefs/reports, the other review diffs, and
   `progress.md`). Durability caveat: that directory sits outside the
   repo and its git history and is deleted when the worktree is removed,
   so the preserved evidence for this pass is this dated observation plus
   the archived execute record.
3. Independent cross-model fresh-eyes CODE review of `080e4f1..HEAD`
   (`fresheyes-delta.md`): iteration 1 FAILED on a bench-compile blocker
   (fixed in `dc73c52`), iteration 2 PASSED with 0 blocking issues; plus
   an independent fresh-eyes PLAN review (`fresheyes-plan.md`). This is
   most plausibly the "second independent review pass" the recap referred
   to (the recap's original text itself was not archived, so its exact
   referent cannot be asserted).
4. The 2026-08-09 adversarial council review that produced this fix batch
   (its verdict is carried in the fix-batch plan itself; no separate
   council report file was archived).

## Residual label key (restated in-repo)

- A15 (validator V8): accepted VC++-redistributable deployment residual —
  the MSI chains no redist; machines without it hard-fail local dictation.
- V6/A16: the falsified-and-fixed onboarding readiness assumption — file
  verification alone lied; SpeechModelReady now requires the engine load probe.

## Windows smoke (council item 10)

Run date: 2026-08-09 16:46–16:48 PDT. A pre-existing user `Winpepper.exe`
(PID 60588, started 8/7/2026 9:35:39 AM) was detected before the smoke, so
`-SkipLaunch` was passed and no process was launched, killed, or otherwise
touched by this run (`--selftest` is validated side-effect-free beside a live
instance). The smoke exercises the INSTALLED build, which predates this
branch — installed binary identity recorded verbatim beside the selftest
result below: FileVersion `0.7.0.262`, ProductVersion
`0.7.0.262-alpha+080e4f10f1` (built at the branch fork point `080e4f1`),
LastWriteTime `08/07/2026 09:26:48`.

| Check | Status | Evidence |
|---|---|---|
| smoke-windows.ps1 -RunSelftest -SkipLaunch | FAIL (2 launch-dependent checks; 11 PASS incl. Selftest) | `RESULT: FAIL (2 failed check(s))` — the 2 FAILs (`LogFreshness`: newest log 344.1 min old; `HotkeyHookLogged`: startup line not in today's rotated log) depend on a fresh app launch, which was deliberately not performed (`-SkipLaunch`, pre-existing user instance PID 60588); they reflect the user's 2-day-old session, not the code under test. All install/registry/selftest/state checks PASS, incl. `Selftest PASS — WINPEPPER_SELFTEST_OK token emitted` (against installed build `0.7.0.262-alpha+080e4f10f1`, which predates this branch). |
| Fresh-profile onboarding | MANUAL | No MSI artifact (`ls artifacts/winpepper-*-x64.msi` → no match, exit 2) / Sandbox feature unavailable (`Get-WindowsOptionalFeature`: verbatim `The requested operation requires elevation.`); what remains: install on a fresh profile, complete the model picker, verify 'ready to dictate'. Not built just for this (out of scope). |
| Real dictation | MANUAL | Requires mic + interactive desktop; not claimed. |
| Kill app → worker dies (job reap) | MANUAL | Needs one real dictation first; script provided at `/tmp/reap-check.ps1`; not claimed. Not run against the pre-existing user instance (never touch a Winpepper.exe this task did not launch). |

Verbatim `/tmp/smoke-windows-fixbatch.log` (smoke run + installed-binary
identity check appended):

```
[PASS  ] InstallPayload C:\Users\dan\AppData\Local\Programs\Winpepper\Winpepper.exe (FileVersion 0.7.0.262)
[PASS  ] InstallAssets C:\Users\dan\AppData\Local\Programs\Winpepper\Assets\AppIcon.ico
[PASS  ] ArpEntry DisplayVersion 0.7.0.262
[PASS  ] HkcuVersionStamp InstallVersion 0.7.0.262, InstallDir C:\Users\dan\AppData\Local\Programs\Winpepper\
[PASS  ] AutostartRunKey "C:\Users\dan\AppData\Local\Programs\Winpepper\Winpepper.exe" --tray
[PASS  ] Selftest WINPEPPER_SELFTEST_OK token emitted
[PASS  ] ProcessAlive PID 60588, started 8/7/2026 9:35:39 AM
[FAIL  ] LogFreshness newest log winpepper-20260809.log is 344.1 min old (limit 10)
[FAIL  ] HotkeyHookLogged "Hotkey hook installed" not found in last 2000 lines of winpepper-20260809.log
[PASS  ] SettingsJson C:\Users\dan\AppData\Local\winpepper\settings.json parses as JSON
[PASS  ] ModelsDir 28 files under C:\Users\dan\AppData\Local\winpepper\models
[PASS  ] HistoryDir 101 files under C:\Users\dan\AppData\Local\winpepper\history
[PASS  ] CorrectionsJson C:\Users\dan\AppData\Local\winpepper\corrections.json
[MANUAL] Dictation focus Notepad, hold the hotkey, speak a known phrase, verify the text appears
[MANUAL] RebootCycle reboot; verify Winpepper autostarts hidden to tray, reopens from tray, still dictates
[MANUAL] UpgradePersist install a newer MSI over this profile; re-run this script; verify settings/history survived

=== Winpepper Windows smoke summary ===

Check            Status Detail
-----            ------ ------
InstallPayload   PASS   C:\Users\dan\AppData\Local\Programs\Winpepper\Winpepper.exe (FileVersion 0.7.0.262)
InstallAssets    PASS   C:\Users\dan\AppData\Local\Programs\Winpepper\Assets\AppIcon.ico
ArpEntry         PASS   DisplayVersion 0.7.0.262
HkcuVersionStamp PASS   InstallVersion 0.7.0.262, InstallDir C:\Users\dan\AppData\Local\Programs\Winpepper\
AutostartRunKey  PASS   "C:\Users\dan\AppData\Local\Programs\Winpepper\Winpepper.exe" --tray
Selftest         PASS   WINPEPPER_SELFTEST_OK token emitted
ProcessAlive     PASS   PID 60588, started 8/7/2026 9:35:39 AM
LogFreshness     FAIL   newest log winpepper-20260809.log is 344.1 min old (limit 10)
HotkeyHookLogged FAIL   "Hotkey hook installed" not found in last 2000 lines of winpepper-20260809.log
SettingsJson     PASS   C:\Users\dan\AppData\Local\winpepper\settings.json parses as JSON
ModelsDir        PASS   28 files under C:\Users\dan\AppData\Local\winpepper\models
HistoryDir       PASS   101 files under C:\Users\dan\AppData\Local\winpepper\history
CorrectionsJson  PASS   C:\Users\dan\AppData\Local\winpepper\corrections.json
Dictation        MANUAL focus Notepad, hold the hotkey, speak a known phrase, verify the text appears
RebootCycle      MANUAL reboot; verify Winpepper autostarts hidden to tray, reopens from tray, still dictates
UpgradePersist   MANUAL install a newer MSI over this profile; re-run this script; verify settings/history survived

RESULT: FAIL (2 failed check(s))

FileVersion    : 0.7.0.262
ProductVersion : 0.7.0.262-alpha+080e4f10f1
FileName       : C:\Users\dan\AppData\Local\Programs\Winpepper\Winpepper.exe

LastWriteTime: 08/07/2026 09:26:48
```

## Remaining for the user

1. **Windows gate (blocking for push):** run `wsl.exe --shutdown` from
   Windows (kills every WSL session — cannot be run from inside WSL), then
   re-run `./scripts/windows-gate.sh` in the worktree until a single
   all-GREEN run at `5fa2ae9` (commits after that SHA are docs-only).
2. **Fresh-profile onboarding (MANUAL):** build/obtain an MSI, install on a
   fresh profile (e.g. Windows Sandbox via
   `scripts/windows-sandbox/Launch-WinpepperSandbox.ps1`, elevated), complete
   the model picker, verify "ready to dictate".
3. **Real dictation (MANUAL):** hold the hotkey, speak a sentence, verify the
   text lands in the focused app (smoke script's `Dictation` MANUAL row).
4. **Job-Object reap check (MANUAL):** after one real dictation (worker child
   alive), run `/tmp/reap-check.ps1` against an instance you launched; expect
   `REAP-CHECK: worker <pid> dead within 10s = True`.
5. **Smoke launch-dependent checks (from the 2 recorded FAILs):** after a
   fresh launch of the current install (or a new one), re-run
   `scripts/smoke-windows.ps1 -RunSelftest` and expect `LogFreshness` and
   `HotkeyHookLogged` to clear; once an MSI built from this branch is
   installed, the smoke will then exercise this branch's build rather than
   `080e4f1`.
6. **RebootCycle / UpgradePersist (MANUAL):** the smoke script's remaining
   MANUAL rows — reboot-autostart verification and upgrade-over-profile
   persistence.
