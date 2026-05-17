# Winpepper

**Hold a hotkey. Speak. Release. Cleaned-up words appear in the focused Windows app.**

Winpepper is a Windows-native local dictation tool. Hold Right Ctrl + Right Shift,
speak, release — the audio is transcribed with [NVIDIA Parakeet TDT v3 (0.6B)][parakeet],
lightly polished by a small local LLM, and typed into whatever window has focus.
Everything runs on your machine. No cloud, no telemetry, no account.

Companion to [`pepper-x`](https://github.com/obra/pepper-x) — same problem, pure-native
rewrite for Windows.

[parakeet]: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3

## Status: 0.6.0-alpha — works on my VM, needs your microphone

The full surface is **code-complete**: all six plans (foundation, cleanup pipeline,
WinUI 3 shell, history + lab + models tab, post-paste learning + diagnostics + crash
safety, WiX MSI packaging) are merged. ~365 cross-platform tests pass on Linux.

The packaged app boots cleanly on a Windows 11 24H2 VM through the entire pipeline
(`SelftestProbe` exits 0; the tray host registers; `Hotkey hook installed on thread N`
lands in the log). What it has **not** yet been end-to-end verified against:

- a real microphone (the test VM has none)
- an interactive desktop session you can actually click into (the test VM is headless
  over SSH)
- a real human pressing Right Ctrl + Right Shift while speaking

If you're testing the MSI, **you are the first human to round-trip speech through
this thing.** Expect rough edges. The Diagnostics tab's "Copy diagnostics bundle"
button zips logs + system info (no audio, no transcripts) into a file that's safe
to send back.

## Install (MSI)

Download `winpepper-<version>-x64.msi` from the [Releases page](../../releases) and
run it.

Requirements:
- Windows 11 22H2 or newer (build 22621+), x64
- ~700 MB free disk for the install
- Another ~1.2 GB for the ASR + cleanup models (downloaded on first run from the
  Models tab)
- DirectX 12 GPU (recommended for ASR; the model will fall back to CPU otherwise)

The MSI is currently **unsigned.** Windows will throw a SmartScreen warning on first
launch — click "More info" → "Run anyway". A signed build is on the roadmap; the
`packaging/sign.ps1` wrapper just needs a code-signing certificate wired in.

After install:
- Files land in `C:\Program Files\Winpepper\`
- User data (settings, corrections, downloaded models, audio history) lives in
  `%LOCALAPPDATA%\winpepper\` — survives reinstalls and uninstalls
- Autostart is enabled: `HKCU\…\Run\Winpepper` runs the app hidden in the tray on
  logon

To uninstall: standard Add/Remove Programs entry. User data is preserved; delete
`%LOCALAPPDATA%\winpepper\` yourself if you want a fully clean slate.

## Architecture

Single .NET 9 / WinUI 3 packaged process. Threads:

- **UI thread** — XAML.
- **Hook thread** — STA thread that owns the `WH_KEYBOARD_LL` hook and the
  `SendInput` injection (Windows requires hook callbacks to run on the thread that
  installed the hook).
- **Audio thread** — WASAPI capture, 20 ms PCM frames into a bounded channel.
- **ASR worker** — Parakeet TDT v3 streaming decode via ONNX Runtime DirectML, falls
  back to CPU.
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
| `src/Winpepper.Asr` | Parakeet TDT v3 ONNX session, mel-feature extraction, streaming chunker |
| `src/Winpepper.Audio` | WASAPI capture via NAudio |
| `src/Winpepper.Cleanup` | LlamaSharp Vulkan backend, prompt builder, `<think>` sanitizer, deterministic post-pass |
| `src/Winpepper.Corrections` | Correction store (preferred transcriptions + misheard replacements) |
| `src/Winpepper.History` | History archive, WAV writer, Lab rerun services, word-diff |
| `src/Winpepper.Models` | Model registry, downloader (HuggingFace, range-resume, SHA-256 verify) |
| `src/Winpepper.Platform` | P/Invoke: WH_KEYBOARD_LL, SendInput, UIA, OCR, MiniDumpWriteDump, autostart |
| `packaging/` | WiX v5 MSI source, capability probe, sign.ps1, install/uninstall smoke |
| `tests/` | xUnit unit + integration tests (~365 passing on Linux) |
| `docs/superpowers/specs/` | Approved product design |
| `docs/superpowers/plans/` | Six implementation plans (foundation, cleanup, ui-shell, history-models, learning-diagnostics, packaging) |
| `docs/manual-test.md` | Smoke procedures per plan, including the working VM launch recipe |
| `scripts/` | Dev/VM helpers (audio passthrough, build/test on a Win11 VM) |

## Building from source

You need a Windows 11 host with .NET 9 SDK installed.

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
dotnet build packaging/Winpepper.Msi.wixproj -c Release `
             -p:UseXamlCompilerExecutable=true
# → artifacts/winpepper-<version>-x64.msi
```

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

Tests run on Linux too (via .NET 9 SDK; `EnableWindowsTargeting=true` cross-compiles
the Windows TFM projects so they restore cleanly):

```sh
dotnet test --filter "Platform!=Windows"
```

The `Platform=Windows` traited tests (UIA, OCR, real-Parakeet model, real-LLamaSharp
model, hotkey hook) run on the Windows VM only.

## Documentation

- [`docs/superpowers/specs/2026-05-15-winpepper-design.md`](docs/superpowers/specs/2026-05-15-winpepper-design.md)
  — the approved product design. Read this for "what is this thing supposed to do."
- [`docs/superpowers/plans/`](docs/superpowers/plans/) — six implementation plans
  with task-by-task code and test fixtures. Read these for "why is this code shaped
  this way."
- [`docs/manual-test.md`](docs/manual-test.md) — smoke procedures, including the
  Plan 6 MSI install/uninstall verification and the Plan 3 WinUI shell launch
  recipe (the dockur VM trick).

## Known issues

- **Unsigned binaries.** SmartScreen warning on first launch. Plug `sign.ps1` into
  a code-signing cert to fix.
- **The 230 MB MSI** is fat because it bundles the .NET 9 runtime + WinAppSDK 1.8
  for self-contained execution. A framework-dependent build would be ~10 MB but
  requires the runtime to be pre-installed.
- **Tray ProgressBar dropped.** WinUI's `MenuFlyout` collection only accepts
  `MenuFlyoutItemBase` children, so the "model downloading…" progress bar that the
  spec puts in the tray menu lives in the status pill instead.
- **InstallWinAppSdk MSI custom action gated FALSE.** Self-contained publish ships
  the runtime in the install folder, so there's nothing to install separately. If
  you switch to framework-dependent, ship a real
  `WindowsAppRuntimeInstall-x64.exe` in `packaging/bootstrapper/` and flip the
  condition back.

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

## Origin

Winpepper was built entirely by Claude Opus 4.7 across one ~16-hour session,
following a spec → 6-plan → subagent-driven-development → MSI workflow. Every
commit in the history was authored by the model; humans approved scope and
arbitrated when the WinAppSDK toolchain bug initially blocked Plan 3.
The full session transcript is what produced the code, the tests, the docs, and
this README.

## License

Apache License 2.0. See [`LICENSE`](LICENSE).

Copyright 2026 Jesse Vincent.
