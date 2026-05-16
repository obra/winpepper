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
