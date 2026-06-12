# Long-lived Windows dictation smoke test

This is the release smoke test that prioritizes a **real Windows environment
and real user behavior** over clean-VM or headless checks. Linux-side and
disposable-VM testing already cover build, unit, and install mechanics; the
highest-value remaining risk is Windows-only behavior that only shows up
after normal use over time: installer upgrades, tray lifecycle, foreground
focus, global hotkeys, microphone capture, text injection, autostart,
reboot/upgrade persistence, and user-profile state accumulation.

The companion script `scripts/smoke-windows.ps1` automates every check that
a machine can assert. The dictation itself (a human speaking into a real
microphone) is deliberately manual and is marked MANUAL in the script's
summary.

## Test environment

- A **dedicated Windows 11 machine** (or a real interactive desktop session
  reserved for this purpose) — not the developer's active workstation, and
  not a VM that gets wiped between runs.
- A working microphone (built-in or USB).
- **Keep the same Windows user profile across runs.** The point of this test
  is state accumulation: `%LOCALAPPDATA%\winpepper` must survive and grow
  across days, reboots, and upgrades. Never reset the profile between runs.
- PowerShell 5.1+ (built in) or PowerShell 7 to run the smoke script.

## State that must accumulate and survive

All under `%LOCALAPPDATA%\winpepper`:

| Artifact | Created by |
|---|---|
| `settings.json` | first run / onboarding |
| `cleanup-settings.json` | cleanup settings page |
| `corrections.json` | adding a correction |
| `history\` (entries + WAV artifacts) | each dictation |
| `logs\winpepper-*.log` | every run (rolling) |
| `models\` | model download |
| `crashes\` | only on crash (should stay empty) |

Plus machine state written by the MSI: payload under
`C:\Program Files\Winpepper`, the ARP uninstall entry, the
`HKLM\Software\Winpepper` version stamp, and the per-user autostart value
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper`
(`"...\Winpepper.exe" --tray`, written on fresh installs only so upgrades
never resurrect a toggled-off autostart).

## Run cadence

Use **few runs, but make them stateful** — each run builds on the last:

1. **Run 0 — fresh install (once).** Install the release MSI on the clean
   profile. Complete onboarding: pick the mic, record hotkeys, download
   models, dictate into the try-it box. Then run
   `scripts\smoke-windows.ps1 -RunSelftest`.
2. **Runs 1..n — daily/startup dictation.** On an already-provisioned
   machine: log in (or reboot first — see below), let Winpepper autostart,
   dictate a known phrase into Notepad, run the script. History and logs
   should grow monotonically.
3. **Reboot cycles (at least twice across the test window).** Reboot, log
   in, and verify Winpepper autostarted **hidden to the tray** (no main
   window), can be reopened from the tray icon, and still dictates. Then run
   the script.
4. **Upgrade over the existing profile (once per release).** Install the new
   MSI **without uninstalling**. Verify: ARP shows the new version, settings
   and hotkeys are unchanged, corrections and history are intact, autostart
   reflects the user's pre-upgrade choice, and dictation still works. Run the
   script again.
5. **Uninstall / reinstall preserving user data (once).** Uninstall via ARP,
   confirm `%LOCALAPPDATA%\winpepper` is left behind, reinstall, and confirm
   the app picks the old profile up (no onboarding, history present).

## The automated half: `scripts/smoke-windows.ps1`

```powershell
# default: assert install state, launch the app, check logs and profile state
pwsh -File scripts\smoke-windows.ps1

# also run the headless selftest probe
pwsh -File scripts\smoke-windows.ps1 -RunSelftest

# assert-only (don't launch; e.g. right after a reboot to prove autostart worked)
pwsh -File scripts\smoke-windows.ps1 -SkipLaunch
```

Checks performed (PASS/FAIL/WARN, exit code 1 on any FAIL):

- `InstallPayload` / `InstallAssets` — `Winpepper.exe` and `Assets\AppIcon.ico`
  under `C:\Program Files\Winpepper`.
- `ArpEntry` — Add/Remove Programs entry named `Winpepper`.
- `HklmVersionStamp` — `HKLM\Software\Winpepper` `InstallVersion`/`InstallDir`.
- `AutostartRunKey` — HKCU Run value pointing at `Winpepper.exe --tray`.
- `Selftest` (with `-RunSelftest`) — `Winpepper.exe --selftest` emits
  `WINPEPPER_SELFTEST_OK` (same contract as the nightly CI job).
- `ProcessAlive` — launches `Winpepper.exe --tray` if not running and asserts
  the process is still alive after the wait.
- `LogFreshness` / `HotkeyHookLogged` — newest
  `%LOCALAPPDATA%\winpepper\logs\winpepper-*.log` was written recently and
  contains `Hotkey hook installed`.
- `SettingsJson` — `settings.json` exists and parses as JSON.
- `ModelsDir` / `HistoryDir` / `CorrectionsJson` — state accumulation
  (missing history/corrections are WARN on a fresh profile, since they only
  appear after first dictation / first correction).

After a reboot-autostart check, prefer `-SkipLaunch` so the script proves the
*autostarted* instance is alive rather than starting one itself.

## Manual steps (cannot be automated here)

These are listed as MANUAL in the script summary and must be done by a human
at the machine:

1. **Dictation:** focus Notepad (or any normal text field), hold the
   configured hotkey, speak a known phrase (e.g. "hello world testing one
   two three"), release, and verify the transcribed text appears in the
   focused window.
2. **Tray behavior:** after a reboot, confirm Winpepper started hidden to the
   tray, the tray icon is present, and the main window opens from it.
3. **Upgrade persistence:** after installing a newer MSI over the live
   profile, spot-check settings, hotkeys, corrections, and history in the UI.

## Acceptance bar

A known spoken phrase reaches the microphone input; Winpepper records,
transcribes, and inserts it into a real focused Windows app; and the expected
profile state is still correct — and has grown — after restart and upgrade.
Concretely, a release passes when:

- `smoke-windows.ps1` reports `RESULT: PASS` on the fresh install, after at
  least two reboot cycles, and after the upgrade-over-profile run, and
- every MANUAL step above succeeded on each of those runs.
