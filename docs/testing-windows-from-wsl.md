# Running the full Windows test suite from WSL2

The FULL suite (all 9 test projects, including the Windows-only TFMs that cover
WinUI helpers, NAudio/WASAPI, DPAPI, UIA/OCR, and the hotkey hook) can be run
**from inside a WSL2 session** by driving the Windows-host .NET SDK against
this checkout's UNC path (`\\wsl.localhost\<distro>\...`). A Windows host
session is NOT required. This procedure was verified end-to-end on
DANDESKTOP (WSL2 Ubuntu, Windows .NET SDK 9.0.316): 9/9 projects, 973 tests,
0 failures, ~12 minutes wall clock from a clean tree.

## Prerequisites

- WSL interop enabled (the default; `powershell.exe` runs from a WSL shell).
- Windows-side .NET 9 SDK at `C:\Program Files\dotnet\dotnet.exe`
  (`/mnt/c/Program Files/dotnet/dotnet.exe --version` → 9.0.3xx works with
  this repo's `global.json`).
- The repo checked out on the WSL (Linux) filesystem. Windows reaches it via
  `\\wsl.localhost\$WSL_DISTRO_NAME\...`; `wslpath -w <path>` prints that UNC
  path.

## One command

```sh
scripts/test-windows-from-wsl.sh
```

The script:

1. Verifies interop and the Windows SDK, then **wipes `bin/` and `obj/` under
   `src/` and `tests/`** (see "Why the clean step" below).
2. Builds each of the 9 projects in `tests/` with the **Windows**
   `dotnet.exe build <UNC path to csproj> -c Release`.
3. Runs each built test DLL via the xUnit v3 in-process runner:
   `dotnet.exe exec <UNC path to dll>` — never `dotnet test`. For the
   multi-targeted projects (Audio, Cleanup, Platform) it runs the
   `net9.0-windows10.0.19041.0` DLL; for the rest, `net9.0`.
4. Prints a per-project summary and exits 0 only if every project built and
   passed with 0 failures.

Expected duration: **~12 minutes** (clean rebuild over the 9P/UNC filesystem
dominates; the tests themselves take seconds). Run it with a generous timeout
(20–30 min) or in the background with polling — never a default 30 s timeout.

Verified reference run (2026-07-25):

| Project | TFM run | Tests | Result |
| ------- | ------- | ----- | ------ |
| Winpepper.Asr.Tests | net9.0 | 94 | pass |
| Winpepper.Audio.Tests | net9.0-windows | 61 (1 skip) | pass |
| Winpepper.Cleanup.Tests | net9.0-windows | 87 (2 skips) | pass |
| Winpepper.Core.Tests | net9.0 | 367 | pass |
| Winpepper.Corrections.Tests | net9.0 | 23 | pass |
| Winpepper.History.Tests | net9.0 | 45 | pass |
| Winpepper.IntegrationTests | net9.0 | 1 | pass |
| Winpepper.Models.Tests | net9.0 | 73 | pass |
| Winpepper.Platform.Tests | net9.0-windows | 222 (2 skips) | pass |

The skips are environment-gated, not failures: WASAPI record (explicit skip —
needs a real audio device smoke), TextInjector/HotkeyHook (explicit skip —
need an interactive/focused window), and the LlamaSharp integration pair
(`Assert.SkipUnless` — skipped when the Qwen GGUF is absent from
`%LOCALAPPDATA%\winpepper\models`; they run for real if the model is present).

## Manual invocation pattern

The same thing by hand, for a single project:

```sh
cd ~/code/winpepper
WIN_DOTNET='/mnt/c/Program Files/dotnet/dotnet.exe'
"$WIN_DOTNET" build "$(wslpath -w tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj)" -c Release
"$WIN_DOTNET" exec  "$(wslpath -w tests/Winpepper.Platform.Tests/bin/Release/net9.0-windows10.0.19041.0/Winpepper.Platform.Tests.dll)"
```

Notes:

- `dotnet.exe` and `powershell.exe` both accept a UNC working directory and
  UNC arguments; `cmd.exe` does not (it falls back to `C:\Windows`) — avoid it.
- Passing explicit UNC paths (via `wslpath -w`) is more robust than relying on
  the inherited working directory.
- NuGet restores happen on the Windows side into `C:\Users\<you>\.nuget`; the
  first-ever run may download packages and take longer.
- No XAML-compiler workaround is needed: the test projects do not build
  `Winpepper.App` (Platform.Tests compiles the two pill-layout source files
  directly). `-p:UseXamlCompilerExecutable=true` only matters for App/MSI
  builds (see README "Building from source").

## Building the app from WSL

The test script above never builds `Winpepper.App`. For a hand app build from
a WSL2 checkout — the same Release build the pre-push gate runs — use:

```sh
scripts/build-app-windows-from-wsl.sh [--attempts N]   # default N=5
```

The wrapper prints its run-log directory
(`artifacts/build-app-windows/run-<UTC-timestamp>-<pid>/`, one
`attempt<N>.log` per attempt) at start and end. Exit 0 = `BUILD OK`; exit 1 =
build failed (non-transient error, attempts exhausted, or timed out —
timeouts are never retried); exit 2 = usage or environment error. Like the
gate, it never installs the MSI, never launches or kills `Winpepper.exe`,
and never writes `%LOCALAPPDATA%\winpepper`.

Why the wrapper exists: building the App over the `\\wsl.localhost` share is
exposed to transient 9P filesystem races. Cross-process file-visibility lag
on the share is measured systematic — 98–100% of 800 probe writes took ≥5 ms
to become visible to a Windows-side reader (≤43 ms max) — and under
concurrent-build contention the share also throws outright transport errors:
reproduced live, the XAML compiler failed writing its output to the share
("An unexpected network error occurred") with follow-on WMC-family XAML
errors, then passed on the next identical run. The CS0006/WMC1006
ref-assembly codes this command defends against are *inferred* members of
the same 9P coherence/transport class — every historically recorded CS0006
trace also had the (since-fixed) cross-OS obj-mixing mechanism in play, so a
fresh isolated CS0006 reproduction has never been observed.

The three mitigations, byte-for-byte the gate's recipe
(`scripts/windows-gate.sh` stays the canary for this build):

1. **Always-on pre-clean** (`rm -rf src/*/bin src/*/obj`) removes leftover
   Linux-built intermediates — the deterministic cross-OS CS0006 covered in
   "Why the clean step (troubleshooting)" below.
2. **Single-node scheduling** (`-m:1 -p:UseSharedCompilation=false
   -p:UseXamlCompilerExecutable=true`): the whole project graph is scheduled
   on one MSBuild node, so targets run in strict dependency order and no two
   tool processes (per-project `csc.exe` children, `XamlCompiler.exe`, the mt
   shim) ever probe or write the share concurrently inside the measured lag
   windows. Compiles still run as child processes, and `-m:1` implies no
   timing guarantee — the residual handoff exposure is the retry layer's
   job. Cost check: serialized clean builds take 210–318 s vs 167–234 s
   parallel.
3. **Bounded retry** (default `--attempts 5`, the recorded worst-case
   transient chain) fires only on the observed transient signatures
   (`CS0006`, `WMC1006`, `unexpected network error`); any other failure
   stops immediately.

## Why the clean step (troubleshooting)

**CS0006 "Metadata file ...\obj\Release\net9.0\ref\X.dll could not be
found"**, or freshly built DLLs vanishing from `bin/`: this happens when
Windows builds run on top of Linux-built `obj/` state. MSBuild's
IncrementalClean reads the previous `*.FileListAbsolute.txt` (Linux-style
paths), decides those outputs are orphans, and deletes them — which on a UNC
cwd resolves to the very files the build just wrote. The fix is exactly what
the script does: wipe `bin/`+`obj/` under `src/` and `tests/` whenever the
previous build was made by the other OS. (`--no-clean` skips the wipe and is
safe only for back-to-back Windows-side runs.)

Consequence in the other direction: after a Windows-side run, the next
**Linux** build should also start from clean `bin/`/`obj/` (or at least expect
a full restore/rebuild) for the same reason.

Other issues encountered / to watch for:

- **Interop dead** (`dotnet.exe: cannot execute binary file`): WSL interop is
  disabled; check `/etc/wsl.conf` `[interop] enabled=true` and restart the
  distro. The script fails loudly on this.
- **Slow builds**: everything crosses the 9P `\\wsl.localhost` boundary;
  ~60–90 s per project build is normal. Don't kill it early.
