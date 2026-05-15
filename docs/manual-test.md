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

## Plan 1 walking-skeleton smoke (real Windows machine)

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

