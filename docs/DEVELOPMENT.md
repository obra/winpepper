# Winpepper developer guide

Technical reference for people building, testing, or releasing Winpepper. If you
just want to install and use it, see the [README](../README.md).

## Architecture

Single .NET 9 / WinUI 3 packaged process. Threads:

- **UI thread** — XAML.
- **Hook thread** — STA thread that owns the `WH_KEYBOARD_LL` hook and the
  `SendInput` injection (Windows requires hook callbacks to run on the thread that
  installed the hook).
- **Audio thread** — WASAPI capture, 20 ms PCM frames into a bounded channel.
- **ASR worker** — Parakeet TDT v3 streaming decode via ONNX Runtime DirectML, falls
  back to CPU. An optional cloud provider (AssemblyAI, off by default, requires your
  own API key) can be selected instead, with automatic fallback to the local model.
- **Cleanup worker** — Qwen 2.5 0.5B Instruct (Q4_K_M GGUF) via LlamaSharp with the
  Vulkan backend.
- **Window-context worker** — optional UIA tree walk → `Windows.Media.Ocr` fallback,
  raced against transcription so cleanup has the surrounding screen text as
  disambiguation context.

State machine: `Idle → Recording → Transcribing → CleaningUp → Injecting → Idle`,
with a cancel hotkey (Esc) that trips a session-scoped `CancellationToken` and
unwinds every stage.

## Repository layout

| Path | What |
| ---- | ---- |
| `src/Winpepper.App` | WinUI 3 packaged app — tray, status pill, nav shell, all view-models bound |
| `src/Winpepper.Core` | Session state machine, settings, error bus, logging, view-models, post-paste learning analyzer |
| `src/Winpepper.Asr` | Parakeet TDT v3 ONNX session, mel-feature extraction, streaming chunker, optional AssemblyAI transcriber |
| `src/Winpepper.Audio` | WASAPI capture via NAudio |
| `src/Winpepper.Cleanup` | LlamaSharp Vulkan backend, prompt builder, `<think>` sanitizer, deterministic post-pass |
| `src/Winpepper.Corrections` | Correction store (preferred transcriptions + misheard replacements) |
| `src/Winpepper.History` | History archive, WAV writer, Lab rerun services, word-diff |
| `src/Winpepper.Models` | Model registry, downloader (HuggingFace, range-resume, SHA-256 verify) |
| `src/Winpepper.Platform` | P/Invoke: WH_KEYBOARD_LL, SendInput, UIA, OCR, MiniDumpWriteDump, autostart |
| `packaging/` | WiX v5 MSI source, capability probe, sign.ps1, install/uninstall smoke |
| `tests/` | xUnit unit + integration tests |
| `docs/superpowers/specs/` | Approved product design |
| `docs/superpowers/plans/` | Implementation plans |
| `docs/plans/` | Follow-up feature and bug-fix plans |
| `docs/manual-test.md` | Smoke procedures per plan, including the working VM launch recipe |
| `scripts/` | Dev/VM helpers (audio passthrough, build/test on a Win11 VM, Windows Sandbox trial) |

## Building from source

You need a Windows 11 host with .NET 9 SDK installed. Building from a WSL2
checkout (`\\wsl.localhost\...`) with the Windows `dotnet.exe` also works: the
projects detect the UNC path and automatically stage the mt.exe manifest merge
(`scripts/mt-unc-shim.ps1`) and the WiX link (`%TEMP%\winpepper-msi`, MSI
copied back to `artifacts/`) on a local drive. Those conditionals are inert on
normal `C:\` checkouts. From a WSL shell, build the App via
`scripts/build-app-windows-from-wsl.sh` (see
[`docs/testing-windows-from-wsl.md`](testing-windows-from-wsl.md) "Building
the app from WSL"): it runs the same documented `dotnet build` command but
hardened with a pre-clean, single-node scheduling, and a bounded retry. The
hardening targets transient UNC build failures seen from WSL checkouts: a 9P
transport write fault inside the XAML compiler under concurrent-build
contention was reproduced on this host, while the exact CS0006/WMC1006 codes
are inferred (plausible-but-unproven) members of that same transient-I/O
class, never isolated-reproduced — the raw command block below remains the
reference for native-Windows readers.

```powershell
# Restore + build (the App project needs UseXamlCompilerExecutable=true on
# `dotnet build` because the in-process markup-compiler task hits a
# PlatformNotSupportedException on .NET 9 + WinAppSDK 1.6/1.7/1.8).
dotnet build src/Winpepper.App/Winpepper.App.csproj -c Release `
             -p:UseXamlCompilerExecutable=true

# Self-contained publish (bundles .NET 9 + WinAppSDK runtime).
dotnet publish src/Winpepper.App/Winpepper.App.csproj -c Release -r win-x64 `
               --self-contained true -p:UseXamlCompilerExecutable=true

# Build the MSI.
dotnet build packaging/Winpepper.Msi.wixproj -c Release -r win-x64 `
             -p:UseXamlCompilerExecutable=true
# → artifacts/winpepper-<version>-x64.msi
```

`-r win-x64` is mandatory on the MSI build (NETSDK1047 otherwise).

If you switch WinAppSDK versions and the App build fails with `Could not load file
or assembly 'System.Security.Permissions, Version=6.0.0.0'`, drop that DLL next to
the markup compiler task:

```powershell
# One-time per WinAppSDK upgrade.
Invoke-WebRequest -Uri "https://www.nuget.org/api/v2/package/System.Security.Permissions/6.0.0" `
                  -OutFile "$env:TEMP\ssp.zip" -UseBasicParsing
Expand-Archive "$env:TEMP\ssp.zip" -DestinationPath "$env:TEMP\ssp" -Force
Copy-Item "$env:TEMP\ssp\lib\net6.0\System.Security.Permissions.dll" `
          "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk.winui\<version>\tools\net6.0\System.Security.Permissions.dll"
```

## Testing

The cross-platform subset runs on Linux (via .NET 9 SDK; `EnableWindowsTargeting=true`
cross-compiles the Windows TFM projects so they restore cleanly):

```sh
dotnet test --filter "Platform!=Windows"
```

The `Platform=Windows` traited tests (UIA, OCR, real-Parakeet model, real-LLamaSharp
model, hotkey hook) need Windows.

- **Full suite from WSL2:** [`docs/testing-windows-from-wsl.md`](testing-windows-from-wsl.md)
  drives the Windows-host .NET SDK against a WSL checkout via UNC paths —
  `scripts/test-windows-from-wsl.sh` builds and runs all 9 test projects. Expect
  roughly 12 minutes; run it with a generous timeout.
- **Release smoke test on real hardware:** [`docs/windows-smoke-test.md`](windows-smoke-test.md)
  plus `scripts/smoke-windows.ps1` — install state, autostart, tray lifecycle,
  upgrade-over-profile, and the manual dictation checks.
- **Disposable trial:** [`scripts/windows-sandbox/`](../scripts/windows-sandbox/)
  installs the MSI in Windows Sandbox and runs the self-test.

## Releasing

See [`docs/release.md`](release.md). Releases are tag-triggered: pushing a `v*`
tag builds the MSI, gates on a silent install + `--selftest` + uninstall run,
publishes the MSI and its SHA256 to a GitHub Release, and (once enabled) opens a
winget-pkgs update PR.

Winpepper ships **unsigned, by decision**. `packaging/sign.ps1` and the wixproj
`SignArtifacts` target are retained as inert scaffolding in case that changes.

## Documentation

- [`docs/superpowers/specs/2026-05-15-winpepper-design.md`](superpowers/specs/2026-05-15-winpepper-design.md)
  — the approved product design. Read this for "what is this thing supposed to do."
- [`docs/superpowers/plans/`](superpowers/plans/) — implementation plans with
  task-by-task code and test fixtures. Read these for "why is this code shaped
  this way."
- [`docs/plans/`](plans/) — later feature and fix plans (hotkey hardening, model
  provisioning, install/distribution, and more).
- [`docs/manual-test.md`](manual-test.md) — smoke procedures, including the MSI
  install/uninstall verification and the WinUI shell launch recipe (the dockur VM
  trick).
- [`docs/automation-ids.md`](automation-ids.md) — UI automation IDs.

## Performance notes

End-to-end latency from hotkey-release to text-in-the-focused-window is dominated
by the **cleanup LLM** step, not by ASR. Parakeet runs streaming during your
recording (only a ~560 ms final-window flush happens on release), so wait time
scales with how heavy the cleanup model is for your GPU.

A first real-hardware data point (single observation, single utterance):

| Component | Hardware | Observed |
|-----------|----------|----------|
| Intel Iris Xe (integrated) | Qwen 2.5 0.5B Q4_K_M via LlamaSharp Vulkan | `Cleanup path="Llm", 6823ms` |

That's ~6.8 s of cleanup on integrated graphics for one short dictation. The
cleanup token budget is `ceil(transcript_chars * 2.0)` capped at 2048, so longer
utterances cost proportionally more time. A discrete NVIDIA/AMD GPU will be much
faster — the design targets sub-second cleanup there, but no reproducible
hardware-tier benchmark has been published yet.

Turning off **Enable cleanup LLM** on the Cleanup tab returns the raw Parakeet
transcript, which is near-instant. The transcript is generally already quite clean
for short utterances; the LLM adds value mainly for punctuation, capitalization,
and disfluency removal.

## Known issues

- **Unsigned binaries.** SmartScreen warning on first launch. Plug `sign.ps1` into
  a code-signing cert to fix.
- **The MSI is large** (a couple hundred MB) because it bundles the .NET 9 runtime
  and WinAppSDK for self-contained execution. A framework-dependent build would be
  ~10 MB but requires the runtime to be pre-installed.
- **Tray ProgressBar dropped.** WinUI's `MenuFlyout` collection only accepts
  `MenuFlyoutItemBase` children, so the "model downloading…" progress bar that the
  spec puts in the tray menu lives in the status pill instead.
- **InstallWinAppSdk MSI custom action gated FALSE.** Self-contained publish ships
  the runtime in the install folder, so there's nothing to install separately. If
  you switch to framework-dependent, ship a real
  `WindowsAppRuntimeInstall-x64.exe` in `packaging/bootstrapper/` and flip the
  condition back.

## Installed layout

- Program files: `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; not `Program Files`)
- User data: `%LOCALAPPDATA%\winpepper\` — settings, corrections, downloaded models,
  history, logs, crash dumps. Survives reinstall and uninstall.
- Autostart: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` =
  `"...\Winpepper.exe" --tray`, written on fresh installs only so upgrades never
  resurrect a toggled-off autostart.
- Version stamp: `HKCU\Software\Winpepper` (`InstallVersion` / `InstallDir`).

**Migrating from an older per-machine build?** Earlier releases installed to
`C:\Program Files\Winpepper` (per-machine). Uninstall that one first — that one
removal still needs elevation — before installing the per-user package, so
upgrades track correctly afterward.

## Dev VM notes

`scripts/winrun`, `scripts/winssh`, and `scripts/sync-to-vm.sh` assume a
[dockur/windows](https://github.com/dockur/windows)-style Windows 11 VM at
`localhost:2222` with the default user `user` and password `password`. The
hardcoded `password` literal in those scripts is dockur's documented default — not
a secret. If you've changed credentials or run a different VM, edit the scripts.

The headless dockur VM can build, install, uninstall, and run `--selftest`, but
**not** exercise the full hold-record-release-inject UI loop (no real mic, no
interactive desktop session from SSH). For that, RDP into `localhost:3389`,
focus a text box, hold the hotkey, and use the audio-passthrough setup (raw QEMU
+ PulseAudio null-sink + `scripts/say.sh`) documented in `docs/manual-test.md`.
