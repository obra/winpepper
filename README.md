# Winpepper

> ## ⚠️ Read this first
>
> **Winpepper was written entirely by an AI agent. No human has ever
> tested it.**
>
> Every line of code, every test, every commit message, every doc — including this
> README — was produced by Claude Opus 4.7 across one ~16-hour autonomous session.
> The human in the loop approved scope (six plans, build an MSI) and arbitrated
> when the WinAppSDK toolchain bug initially blocked Plan 3. No human has installed
> the MSI, run the app interactively, or spoken a sentence into it.
>
> The app *builds*, *installs*, *uninstalls*, *boots*, and reports a healthy idle
> state in the log. Whether dictation actually works on real hardware, with a real
> microphone, in a real interactive desktop session — **nobody knows yet.** If you
> install this, you are the first human in the loop.
>
> Treat it accordingly. Don't pin production work to it. Send bug reports.

**Hold a hotkey. Speak. Release. Cleaned-up words appear in the focused Windows app.**

Winpepper is a Windows-native local dictation tool. Hold Right Ctrl + Right Shift,
speak, release — the audio is transcribed with [NVIDIA Parakeet TDT v3 (0.6B)][parakeet],
lightly polished by a small local LLM, and typed into whatever window has focus.
Everything runs on your machine. No cloud, no telemetry, no account.

Companion to [`pepper-x`](https://github.com/obra/pepper-x) — same problem, pure-native
rewrite for Windows.

[parakeet]: https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3

## Status: 0.6.2-alpha — agent-built, human-untested

The full surface is **code-complete**: all six plans (foundation, cleanup pipeline,
WinUI 3 shell, history + lab + models tab, post-paste learning + diagnostics + crash
safety, WiX MSI packaging) are merged. ~365 cross-platform unit tests pass on Linux.

The packaged app boots cleanly on a Windows 11 24H2 VM through the entire pipeline
(`SelftestProbe` exits 0; the tray host registers; `Hotkey hook installed on thread N`
lands in the log). The MSI installs and uninstalls cleanly via `msiexec /qn`.

What has **not** been verified by anyone — agent or human:

- a real microphone (the test VM has none)
- an interactive desktop session you can actually click into (the test VM was
  driven entirely over SSH)
- pressing Right Ctrl + Right Shift while speaking
- whether the XAML pages render correctly
- whether post-paste learning toasts appear and behave
- whether the Lab rerun panels work
- whether the model downloader UI works (the downloader *logic* has unit tests)

If you install the MSI, **you are the first human to do any of this.** Expect rough
edges. The Diagnostics tab's "Copy diagnostics bundle" button zips logs + system
info (never audio, never transcripts) into a file that's safe to send back.

## Install

Three ways to get Winpepper, in recommended order.

### Option 1: winget

> **Availability note:** this works once `obra.Winpepper` is accepted into
> [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) — the first
> submission needs moderator review. Until then, use Option 2 below.

```powershell
winget install obra.Winpepper
```

Don't have `winget`? It ships with modern Windows 11 as part of **App
Installer**. If the command isn't recognized, install or update "App Installer"
from the Microsoft Store, or grab the latest release from
[microsoft/winget-cli](https://github.com/microsoft/winget-cli/releases).

### Option 2: MSI from GitHub Releases

Download `winpepper-<version>-x64.msi` from the [Releases page](../../releases)
and run it.

**Verify the download (recommended):** each release also ships a
`winpepper-<version>-x64.msi.sha256` file containing the expected hash. Compare
(case-insensitive) against your download with either:

```powershell
certutil -hashfile winpepper-<version>-x64.msi SHA256
# or
Get-FileHash winpepper-<version>-x64.msi -Algorithm SHA256
```

The MSI is **unsigned** — a deliberate decision for now (the
`packaging/sign.ps1` scaffolding exists if that ever changes). Windows will
show a SmartScreen warning on first launch — click "More info" → "Run anyway".

### Option 3: Try it in Windows Sandbox first

Want to kick the tires without installing anything on your machine?
[`scripts/windows-sandbox/`](scripts/windows-sandbox/) launches a disposable
Windows Sandbox, auto-installs the MSI, runs the self-test, and evaporates when
you close the window. See
[`scripts/windows-sandbox/README.md`](scripts/windows-sandbox/README.md).

### Requirements

- Windows 11 22H2 or newer (build 22621+), x64
- ~700 MB free disk for the install
- Another ~1.2 GB for the ASR + cleanup models (see first-run step below)
- DirectX 12 GPU (recommended for ASR; the model will fall back to CPU otherwise)

### First run: download the models (~1.2 GB)

Winpepper cannot dictate until its two local models are downloaded — expect
this one-time step on first launch:

1. Launch Winpepper (Start Menu; it also starts hidden in the tray on logon).
2. The onboarding wizard offers a "Download models" step — or open the
   **Models** tab and click **Download Missing Models**.
3. ~1.2 GB downloads from HuggingFace (resumable, SHA-256 verified) into
   `%LOCALAPPDATA%\winpepper\models\`.

Until then, dictation reports "Speech model not installed. Open the Models tab
to download it."

### After install

Winpepper installs **per-user** — no administrator rights and **no UAC prompt**
for install, upgrade, or uninstall.

- Files land in `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; not `Program Files`)
- User data (settings, corrections, downloaded models, audio history) lives in
  `%LOCALAPPDATA%\winpepper\` — a separate folder that survives reinstalls and
  uninstalls
- Autostart is enabled: `HKCU\…\Run\Winpepper` runs the app hidden in the tray on
  logon

To uninstall: standard Add/Remove Programs entry (no elevation needed), or
`winget uninstall obra.Winpepper` if you installed via winget. User data is
preserved; delete `%LOCALAPPDATA%\winpepper\` yourself if you want a fully
clean slate.

> **Migrating from an older per-machine build?** Earlier releases installed to
> `C:\Program Files\Winpepper` (per-machine). Uninstall that one first — that one
> removal still needs elevation — before installing this per-user package, so
> upgrades track correctly afterward.

## Performance: what to expect

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

If the cleanup latency feels too slow on your setup, the **Cleanup** tab has an
**Enable cleanup LLM** toggle. Turning it off returns the raw Parakeet transcript
without LLM polish, which is near-instant. The transcript is generally already
quite clean for short utterances; the LLM adds value mainly for punctuation,
capitalization, and disfluency removal.

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

You need a Windows 11 host with .NET 9 SDK installed. Building from a WSL2
checkout (`\\wsl.localhost\...`) with the Windows `dotnet.exe` also works: the
projects detect the UNC path and automatically stage the mt.exe manifest merge
(`scripts/mt-unc-shim.ps1`) and the WiX link (`%TEMP%\winpepper-msi`, MSI
copied back to `artifacts/`) on a local drive. Those conditionals are inert on
normal `C:\` checkouts.

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

Winpepper was built entirely by Claude Opus 4.7 across one ~16-hour autonomous
session, following a spec → 6-plan → subagent-driven-development → MSI workflow.

The human in the loop:
- Picked the goal: a Windows-native rewrite of [`pepper-x`](https://github.com/obra/pepper-x).
- Answered ~10 multiple-choice scope questions early on (C# + WinUI 3, DirectML,
  WiX MSI, etc.).
- Said "ship it" / "go" / "do it" / "finish it successfully" at sensible decision
  points.
- Arbitrated once when the WinAppSDK + .NET 9 toolchain block stopped Plan 3
  mid-execution (picked "investigate yourself" over "stop and escalate").

Everything else — the design spec, the six implementation plans, every commit,
the test fixtures, the MSI packaging, the README you're reading — came out of
the model. The repo's git history is the literal output of the session.

No code in this repo has been read for review by a human before being committed.
No `Winpepper.exe` instance has been clicked by a human before being shipped as
the MSI release. The agent told the human it works; the human took the agent's
word for it and pushed the result public.

## License

Apache License 2.0. See [`LICENSE`](LICENSE).

Copyright 2026 Jesse Vincent.
