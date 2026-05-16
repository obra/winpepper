# Winpepper Manual Test Plan

## Smoke checklist (per release)

- [ ] App launches without errors
- [ ] Hotkey records / stops on press / release
- [ ] Transcript appears in the focused window
- [ ] Cancel hotkey aborts a session cleanly
- [ ] Settings persist across restarts (Plan 3+)
- [ ] Models tab downloads and verifies (Plan 4+)
- [ ] MSI install / upgrade / uninstall (Plan 6+)

## VM bootstrap

If working on a fresh machine:

1. Set up the dockur Windows VM (see `~/.claude/skills/windows-vm/SKILL.md`).
2. From the repo root: `./scripts/winssh < scripts/provision-vm.ps1`.
3. Verify: `./scripts/winrun "dotnet --version"` returns `9.0.x`.
4. Download the Parakeet model: `./scripts/winssh < scripts/download-parakeet.ps1`.

## Plan 1 (retired): see Plan 3 smoke below.

The walking-skeleton CLI was always temporary scaffolding. As of Plan 3, `Winpepper.App` is the only entry point — see the "Plan 3 — WinUI 3 shell smoke" section below for the current smoke procedure.

Historical Plan 1 walking-skeleton smoke (kept for reference):

The dockur VM has neither a real microphone nor a real desktop session — `WasapiCapture` returns silence and `SetWindowsHookEx` may not deliver hotkey events. For an honest "hold and dictate" demo, run on a physical Windows 11 host with a working mic.

On the real Windows machine:

1. Clone the repo and check out `plan-1/foundation` (or main once merged).
2. Run `scripts/provision-vm.ps1` (it works on a real machine too — installs .NET 9, Git, VS Build Tools if missing).
3. Run `scripts/download-parakeet.ps1`.
4. Build the CLI: `dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj -c Release`.
5. Run it: `dotnet run --project src/Winpepper.Cli -c Release`.
6. Hold **Right Ctrl + Right Shift** for ~2 seconds while speaking, then release.
7. Expected log sequence in the console:
   - `State Idle -> Recording`
   - `Captured <N> samples (<seconds>s)`
   - `State Recording -> Transcribing`
   - `Transcript: '<your words>'`
   - `State Transcribing -> Injecting`
   - `State Injecting -> Idle`
8. The transcript text should appear in whatever window had focus when you released the hotkey.

Acceptance bar for Plan 1: all four state transitions log, transcript is non-empty for clear speech, text appears in the focused window.

## Audio-passthrough VM smoke (end-to-end)

We replaced dockur's bundled QEMU (no pulse) with raw QEMU + PulseAudio on the host so synthetic speech can be piped into the Windows guest's microphone.

**One-time host setup:**
```sh
./scripts/setup-audio-host.sh    # installs PA, piper, QEMU; loads null-sink; grants /dev/kvm
```

**Boot the VM** (with the existing `/home/jesse/windows-vm/storage` disk):
```sh
./scripts/launch-qemu.sh         # backgrounds itself; SSH on 2222, RDP on 3389
```

**Speak into the VM's mic:**
```sh
./scripts/say.sh "hello world testing one two three"
```

**Verify end-to-end** (WasapiCapture → resample → Parakeet) — start the audio-loopback test on the VM in one shell, run `say.sh` from another within the 6-second window:
```sh
./scripts/winrun "cd C:\winpepper\scripts\audio-loopback-test && dotnet run -c Release -- asr"
# (in another shell, after about 2-3 seconds)
./scripts/say.sh "hello world testing one two three"
```
Expected transcript: `Hello world testing 123` (the NeMo model itn-converts spoken digits).

## Headless VM smoke (without audio)

Build + non-audio tests still run cleanly:

- Build: `./scripts/winrun "dotnet build"`.
- Tests excluding Windows-only integration: `./scripts/winrun "dotnet test --filter 'Platform!=Windows'"`.
- Parakeet load + decode against a synthetic tone: `./scripts/winrun "dotnet test --filter 'FullyQualifiedName~ParakeetSessionIntegrationTests'"`. Proves the ONNX model loads and the TDT decode loop completes.
- Avoid `./scripts/winrun "dotnet test"` without a filter — `Hook_Installs_And_DisposesCleanly` hangs in headless environments.

## Plan 2 cleanup-pipeline smoke (Windows VM)

1. Sync: `./scripts/sync-to-vm.sh`
2. Make sure both models exist:
   ```bash
   ./scripts/winssh < scripts/download-parakeet.ps1
   ./scripts/winssh < scripts/download-cleanup-model.ps1
   ```
3. Build the CLI: `./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj -c Release"`
4. Pre-create a correction file so we can confirm the deterministic post-pass runs:
   ```bash
   ./scripts/winssh 'powershell -Command "$dst = \"$env:LOCALAPPDATA\\winpepper\\corrections.json\"; New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null; Set-Content -Path $dst -Value (@{schema=1; preferred=@(\"ChatGPT\"); replacements=@{\"chat gbt\"=\"ChatGPT\"}} | ConvertTo-Json -Depth 5)"'
   ```
5. Run the CLI in a foreground PowerShell session on the VM:
   ```powershell
   cd C:\winpepper
   dotnet run --project src/Winpepper.Cli -c Release
   ```
6. The console log should show:
   - "Loading cleanup model: ...Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"
   - "Cleanup model loaded."
   - "Cleanup LLM pre-warm complete."
   - "Winpepper ready. Hold the trigger to dictate."
7. From the host, hold `RightCtrl+RightShift` for ~2 seconds, then release.
8. The log should show:
   - "State Idle -> Recording"
   - "Captured NNNNN samples (X.XXs)"
   - "Raw transcript: '...'" (likely empty on a silent VM)
   - "Cleanup path=FallbackEmpty, NNms, text='...'"
   - "State Transcribing -> Injecting" then "-> Idle"
9. Acceptance bar for Plan 2 on the VM: no crash, model loaded, pre-warm completed, cleanup runner invoked, correction post-pass available. Real cleaned-text output requires real audio.

For a real demo, run on a physical Windows 11 host with a mic. Say
"so um like we tested chat gbt today" → release. Expected injected text:
`We tested ChatGPT today.` (filler removed by the LLM, then case-aware
correction post-pass maps any surviving "chat gbt" to "ChatGPT").


## Plan 3 — WinUI 3 shell smoke (audio-passthrough VM)

> **Known issue (2026-05-16):** the `Winpepper.App` build currently fails on the VM with `Microsoft.WindowsAppSDK 1.6/1.7 + .NET 9` due to a `RuntimeEnvironment.GetRuntimeInterfaceAsObject` PNSE in the XAML markup compiler chain. See the milestone commit at the tip of `plan-3/ui-shell` for the full diagnosis. Until the toolchain blocker is resolved (likely by installing VS Build Tools so .NET Framework MSBuild can host the markup compiler, or by waiting for a WinAppSDK update), this smoke procedure is **deferred**. All non-XAML projects build green on Linux and on the VM.

1. Sync: `./scripts/sync-to-vm.sh`
2. Build: `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"`
3. Confirm models are present: `./scripts/winrun "Test-Path C:\Users\user\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3\encoder.onnx"` should be `True`.
4. Launch the app on the VM in a foreground RDP session (`rdesktop localhost:3389` or VS remote-debug):
   ```powershell
   cd C:\winpepper
   dotnet run --project src/Winpepper.App -c Debug
   ```
5. The tray icon should appear; right-click → menu shows "Ready", Settings, Diagnostics (greyed), Pause, Quit, Winpepper v0.3.0.
6. On first launch, the main window opens to **Onboarding**. Click through:
   - Pick a mic — confirm the level meter twitches when you run `./scripts/say.sh "hello"` from the Linux host.
   - Record hotkeys — hold a chord while focused on `HoldBox`. Recording `Ctrl+C` should trigger the warning row.
   - Download models — click Skip. Step advances.
   - Test dictation — tick "That worked." Click Finish.
7. Window navigates to Recording tab. Toggle "Play start/stop sounds"; kill and relaunch — the toggle remembers.
8. Cleanup tab: pick Custom profile, edit the prompt, change the Max-tokens slider. Restart — values persist.
9. Corrections tab: add a preferred ("ChatGPT"), then a duplicate (see error). Add a replacement ("chat gbt" → "ChatGPT"). Reload — entries persist.
10. Hold dictation hotkey while focused on `TestBox`, run `./scripts/say.sh "hello world"`. Release. Expected:
    - Status pill appears bottom-center, red dot, "Recording..."
    - Pill transitions to "Transcribing...", "Inserting..."
    - `TestBox` contains text.
    - Pill auto-hides 600 ms after `SessionStage.Idle`.
11. Quit from the tray — process exits cleanly.

**Acceptance bar:** every step lands without exceptions in `%LOCALAPPDATA%\winpepper\logs\winpepper-<date>.log`. Tray icon and status pill appear. Onboarding finishes and `settings.json` shows `"onboardingCompleted": true`.

Tray autostart variant: after onboarding completes, restart the app with `dotnet run --project src/Winpepper.App -c Debug -- --tray` — the main window should NOT appear, only the tray icon. Click the tray icon to show the window.

## Plan 4 smoke (Windows VM)

> **Status (as of Plan 4 execution):** Cannot execute end-to-end on the VM until the
> WinUI markup compiler blocker (Plan 3 milestone commit 4bdb988) is resolved. The
> procedure below is the canonical Plan 4 smoke and runs once Winpepper.App builds.

1. `./scripts/sync-to-vm.sh`
2. `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Release"`
3. Launch the packaged app on the VM (per Plan 3's onboarding instructions).
4. Models tab:
   - The ASR and Cleanup cards each show a "Download Missing Models" button.
   - Click it. Watch the progress list populate. Confirm WAV / GGUF files land under
     `%LOCALAPPDATA%\winpepper\models\` and the "Installed" label flips to "yes".
   - Verify SHA-256 by deleting one file and re-running — it should re-download.
   - Verify resume by killing the app mid-download, restarting, and clicking again — the file should resume from byte X (check log for `Range: bytes=X-`).
5. Trigger a dictation session (hold + release).
6. History tab:
   - Newest entry appears at the top with the correct timestamp and preview.
   - Click it → detail page opens.
   - Click "Run" on the transcription rerun → diff renders (likely all-equal if the same model).
   - Pick a different model, rerun, observe diff.
   - Click "Run" on the cleanup rerun → cleaned text appears.
   - Click "Show cleanup transcript" → modal shows assembled prompt + raw output.
   - Click "Use as default cleanup" → return to Models tab, confirm the cleanup combo
     reflects the new selection. Settings file `%LOCALAPPDATA%\winpepper\settings.json`
     should also show the new `cleanupModelName` value.
7. Generate >50 sessions (or seed the index manually) and confirm only 50 remain
   and the oldest WAV files are deleted from disk.

## Plan 5 — Post-paste learning, Diagnostics, error bus, crash dumps

### Setup
1. Build + deploy as in earlier plans. Confirm `dotnet test` (Linux filter) is fully green.
2. Run `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"`. WinUI compiler PNSE is expected (carry-forward); proceed with the previously-built binary or, once the WinUI block is resolved, with the fresh build.
3. Launch the app on the VM.

### Post-paste learning
1. Open Notepad. Focus the document.
2. Press the hold-to-record hotkey, say "send chat gbt the link", release.
3. Observe injected text "send chat gbt the link" (or similar — depending on cleanup).
4. Within 30 s, edit `chat gbt` to `ChatGPT` in Notepad.
5. A non-modal toast should appear: "Learn correction: `chat gbt` -> `ChatGPT`? [Yes / Preferred / No]".
6. Click "Yes". Open `%LOCALAPPDATA%\winpepper\corrections.json` and confirm `"chat gbt": "ChatGPT"` under `replacements`.
7. Repeat with "Preferred" — confirm `"ChatGPT"` shows up under `preferred` and `replacements` is unchanged.
8. Repeat with "No" — confirm both lists are unchanged and a second identical edit in the same session does **not** re-prompt.
9. Wait 30 s with no edits. The watch window should silently close (no error).

### Toast button compatibility
- Microsoft Edge address bar — confirm Edge's autocomplete-style edits do NOT trigger the toast.
- Word — confirm Word's autocapitalize ("anthropic" → "Anthropic") does NOT trigger the toast.

### Diagnostics tab
1. Open the main window, click "Diagnostics" in the nav.
2. Confirm the tail shows the recent log lines (at least the boot lines).
3. Trigger a session; confirm new lines appear at the bottom.
4. Click "Open log folder" — Explorer opens at `%LOCALAPPDATA%\winpepper\logs\`.
5. Click "Copy diagnostics bundle" — pick a destination, watch the zip get created.
6. Unzip the bundle; confirm: `logs/winpepper-*.log`, `history-index.json`, `settings.json`, `sysinfo.json`.
7. Confirm there are **no** `*.wav` files in the zip.

### Error bus + tray
1. Rename the parakeet model directory under `%LOCALAPPDATA%\winpepper\models\` so ASR fails.
2. Trigger a session. Confirm the tray icon flips to the yellow Error glyph, the tooltip carries "Error (Asr): ...", and a toast appears with an "Open Models tab" button.
3. Click the toast button — main window opens and selects Models. Restore the model directory.

### Clipboard fallback
1. Open Windows Security → focus a search box. (Or any UAC-protected window.)
2. Trigger a session. Confirm a toast says "Couldn't type into the active window. The cleaned text is on your clipboard."
3. Paste with Ctrl+V — the cleaned text appears.

### Crash safety
1. Open Diagnostics tab.
2. Trigger an artificial crash using the developer hotkey (Ctrl+Shift+F12 — Task 23 wires this as a debug-build-only menu item; if not built into the current binary, throw from `PipelineHost` by editing in a temporary `throw new InvalidOperationException("synthetic crash")` and rebuilding).
3. Confirm `%LOCALAPPDATA%\winpepper\crashes\winpepper-YYYYMMDD-HHMMSS-PID.dmp` exists.
4. Confirm the sidecar `.txt` carries the exception type and stack.
5. Confirm the app stayed alive: tray still present, "Ready" status.
6. Re-trigger a dictation session and confirm the full pipeline still works.

## Plan 6 MSI smoke (Windows VM)

> **Status (as of Plan 6 execution, 2026-05-16):** blocked on `Winpepper.App` build.
> The carry-forward WinAppSDK 1.6 + .NET 9 XAML markup compiler error
> (`Microsoft.UI.Xaml.Markup.Compiler.Tasks.CompileXaml` cannot load
> `System.Security.Permissions, Version=6.0.0.0`) prevents publishing the App.
> The MSI packaging project (`packaging/Winpepper.Msi.wixproj`) and its harvest
> + WiX authoring are complete and green on Linux for everything that does not
> require an actual published App. When the WinUI block is resolved (per the
> Plan 3 note above), execute the procedure below end-to-end.

**Prerequisites on the VM:**

- `dotnet --version` returns `9.0.x` (provisioned by `scripts/provision-vm.ps1`).
- WiX v4 toolset available on PATH (already installed by `provision-vm.ps1` —
  verify with `./scripts/winrun "wix --version"`).
- Both models present under `%LOCALAPPDATA%\winpepper\models\` (see
  `download-parakeet.ps1` and `download-cleanup-model.ps1`).

**Smoke procedure:**

1. Sync the tree to the VM: `./scripts/sync-to-vm.sh`.
2. Publish the App self-contained, framework-dependent on Windows App SDK:
   ```bash
   ./scripts/winrun "cd C:\\winpepper; dotnet publish src/Winpepper.App/Winpepper.App.csproj -c Release -r win-x64 --self-contained true"
   ```
   Expected: succeeds; output lands under
   `src\Winpepper.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\`.
3. Fetch the WinAppSDK bootstrapper into `packaging\bootstrapper\` so the MSI
   can carry it as an authored file:
   ```bash
   ./scripts/winrun "cd C:\\winpepper; if (!(Test-Path packaging\\bootstrapper)) { New-Item -ItemType Directory packaging\\bootstrapper | Out-Null }; Copy-Item $env:USERPROFILE\\.nuget\\packages\\microsoft.windowsappsdk\\1.6.241114003\\runtimes\\win-x64\\native\\Microsoft.WindowsAppRuntime.Bootstrap.dll packaging\\bootstrapper\\ -Force"
   ```
4. Build the MSI on the VM:
   ```bash
   ./scripts/winrun "cd C:\\winpepper; dotnet build packaging\\Winpepper.Msi.wixproj -c Release"
   ```
   Expected: `artifacts\winpepper-<version>-x64.msi` exists; build is clean.
5. Install silently and capture the install log. (The MSI filename embeds the
   Nerdbank.GitVersioning-derived version — discover with `dir artifacts\*.msi`
   and substitute below.)
   ```bash
   ./scripts/winrun "cd C:\\winpepper; msiexec /i artifacts\\winpepper-<version>-x64.msi /qn /l*v artifacts\\install.log"
   ```
   Expected: exit code `0`; `install.log` ends with a successful-completion
   line and no `Return value 3`.
6. Run the self-test from the installed location:
   ```bash
   ./scripts/winrun "& 'C:\\Program Files\\Winpepper\\Winpepper.exe' --selftest"
   ```
   Expected: stdout contains `WINPEPPER_SELFTEST_OK`; exit code `0`.
7. Verify autostart is registered:
   ```bash
   ./scripts/winrun "Get-ItemProperty -Path HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run -Name Winpepper"
   ```
   Expected: a `Winpepper` value pointing at the installed exe.
8. Uninstall silently (same `<version>` as step 5):
   ```bash
   ./scripts/winrun "msiexec /x artifacts\\winpepper-<version>-x64.msi /qn /l*v artifacts\\uninstall.log"
   ```
   Expected: exit code `0`; `C:\Program Files\Winpepper` is gone; the
   `HKCU\...\Run\Winpepper` value is gone.
9. Confirm per-user data survives uninstall:
   ```bash
   ./scripts/winrun "Test-Path $env:LOCALAPPDATA\\winpepper"
   ```
   Expected: `True` (models, settings, history, corrections, logs all
   preserved on uninstall — only `%ProgramFiles%\Winpepper` is removed).

**Acceptance bar for Plan 6 on the VM:** steps 2–4 build green; step 5 installs
with exit code 0; step 6 prints `WINPEPPER_SELFTEST_OK`; step 7 finds the Run
key; step 8 uninstalls cleanly; step 9 leaves `%LOCALAPPDATA%\winpepper` intact.

## Plan 6 MSI upgrade smoke (Windows VM)

> **Status (as of Plan 6 execution, 2026-05-16):** blocked on `Winpepper.App`
> build (same WinAppSDK XAML markup compiler block as the install smoke above).
> When that unblocks, exercise the procedure below to confirm a major-upgrade
> install of a newer MSI over an older one preserves all per-user state and
> respects the user's autostart preference.

Goal: confirm settings, corrections, history, models, and the autostart Run key
all survive a major-upgrade install (MajorUpgrade is
`AllowDowngrades="no" Schedule="afterInstallInitialize"`; the autostart Run key
component carries `Condition="NOT WIX_UPGRADE_DETECTED AND NOT UPGRADINGPRODUCTCODE"`
so the upgrade installer must not overwrite a user-chosen Run value).

1. Build MSI **A** from the current `HEAD`. Note the version string in the
   filename (`artifacts\winpepper-<verA>-x64.msi`).
2. Install MSI A silently:
   ```bash
   ./scripts/winrun "cd C:\\winpepper; msiexec /i artifacts\\winpepper-<verA>-x64.msi /qn /l*v artifacts\\install-A.log"
   ```
3. Launch the app, complete onboarding (or skip), then exercise per-user state:
   - Toggle off "Capture window context" (or any other non-default setting).
   - Add a custom correction pair `("kubernetes", "Kubernetes")` on the
     Corrections tab.
   - Hold the hotkey for a short dictation so a history row + WAV are written.
   - Open Settings, disable autostart (the Run key should be removed), then
     re-enable it so a fresh user-owned Run value is written.
4. Quit the app.
5. Bump the minor version (edit `version.json`), commit, and build MSI **B**
   from that fresh HEAD. Note the new filename
   (`artifacts\winpepper-<verB>-x64.msi`).
6. Install MSI B silently — this exercises the MajorUpgrade path:
   ```bash
   ./scripts/winrun "cd C:\\winpepper; msiexec /i artifacts\\winpepper-<verB>-x64.msi /qn /l*v artifacts\\install-B.log"
   ```
   Expected: exit code `0`. `install-B.log` should reference
   `WIX_UPGRADE_DETECTED` and remove the older product before installing B.
7. Verify on the VM (every check must pass):
   - `%LOCALAPPDATA%\winpepper\settings.json` is intact (window-context toggle
     still off; no schema rewrite).
   - `%LOCALAPPDATA%\winpepper\corrections.json` still contains the
     `kubernetes` → `Kubernetes` pair you added under A.
   - `%LOCALAPPDATA%\winpepper\history\<date>\<uuid>.wav` and the matching row
     in `%LOCALAPPDATA%\winpepper\history\entries.json` survive untouched.
   - `%LOCALAPPDATA%\winpepper\models\` contents are intact (no re-download
     required — encoder.onnx, decoder/joint, the GGUF, manifests).
   - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` still
     holds the exact value you set in step 3 (B did **not** overwrite it; the
     `WIX_UPGRADE_DETECTED` condition excluded the Run-key component on the
     upgrade install).
   - Launch the upgraded app and confirm `--selftest` still prints
     `WINPEPPER_SELFTEST_OK` and a dictation round-trip still works.

**Acceptance bar:** all six verification checks pass and the install log shows
the MajorUpgrade ran (`WIX_UPGRADE_DETECTED` true, prior product removed).

## Verified working launch procedure (2026-05-16)

The dockur VM has an active console session (`quser` shows `user  console  Active`) but SSH sessions inherit a different (non-interactive) window station, which prevents WinUI 3 apps from creating their main window — they crash with `Microsoft.UI.Xaml.dll exception 0xc000027b`. Launch via the Task Scheduler with `LogonType Interactive` so the app runs inside the console session.

**Build (Windows VM):**
```sh
./scripts/sync-to-vm.sh
./scripts/winrun 'dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug -p:UseXamlCompilerExecutable=true'
```

`UseXamlCompilerExecutable=true` matters on `dotnet build`. After upgrading to WinAppSDK 1.8.260508005 the exe path actually works (it didn't on 1.6); the in-process task is still broken on .NET 9.

**One-time per WinAppSDK upgrade:** drop `System.Security.Permissions` 6.0.0 next to the markup compiler so the in-process task variant can load if anything reaches for it (Visual Studio still does):

```powershell
$nupkg = Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/System.Security.Permissions/6.0.0" -OutFile "$env:TEMP\ssp.zip" -UseBasicParsing
Expand-Archive "$env:TEMP\ssp.zip" -DestinationPath "$env:TEMP\ssp" -Force
Copy-Item "$env:TEMP\ssp\lib\net6.0\System.Security.Permissions.dll" `
          "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk.winui\1.8.260505002\tools\net6.0\System.Security.Permissions.dll" -Force
```

**Launch (Windows VM, in interactive console session):**
```powershell
$exe = "C:\winpepper\src\Winpepper.App\bin\Debug\net9.0-windows10.0.19041.0\win-x64\Winpepper.exe"
$action = New-ScheduledTaskAction -Execute $exe -Argument "--tray"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddSeconds(2)
$principal = New-ScheduledTaskPrincipal -UserId "user" -LogonType Interactive
Register-ScheduledTask -TaskName "WinpepperLaunch" -Action $action -Trigger $trigger -Principal $principal
```

After ~3 seconds:
```powershell
Get-Process -Name "Winpepper"       # expect 1 process
Get-Content "$env:LOCALAPPDATA\winpepper\logs\winpepper-*.log" -Tail 5
```
Latest log line should include `Hotkey hook installed on thread <N>` — pipeline is up.

**`--selftest` headless probe** (works from SSH; no UI needed):
```powershell
& "C:\winpepper\src\Winpepper.App\bin\Debug\net9.0-windows10.0.19041.0\win-x64\Winpepper.exe" --selftest
# Expected:
#   winpepper selftest
#   build: <version> (unsigned build)
#   signed: False
#   localappdata: C:\Users\user\AppData\Local\winpepper
#   WINPEPPER_SELFTEST_OK
```

**End-to-end dictation smoke** requires RDP into `localhost:3389` so you can see the tray icon, focus a text box, hold the hotkey, and let `./scripts/say.sh` from the Linux host pipe audio into the VM's virtual mic via PulseAudio passthrough (see [[winpepper-vm]] memory). The headless SSH path can build, launch, and verify pipeline init — but not exercise the full hold-record-release-inject round trip.

