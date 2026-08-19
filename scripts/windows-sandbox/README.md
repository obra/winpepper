# Install Winpepper in Windows Sandbox

This directory provides scripts to quickly install and smoke-test Winpepper inside an ephemeral [Windows Sandbox](https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-overview) — a clean, throwaway Windows environment that leaves no trace on your host after you close it.

## Why Sandbox?

Winpepper is currently **agent-built, human-untested**. Running it in Windows Sandbox lets you try the MSI, verify the self-test, and exercise the UI without touching your host machine's profile — the per-user install (`%LOCALAPPDATA%`, `HKCU`) happens inside the disposable sandbox instead.

## Prerequisites

- Windows 10/11 Pro or Enterprise with the Windows Sandbox feature enabled
- ~2.5 GB free disk space inside the sandbox (700 MB install + ~1.2 GB models + overhead)
- The Winpepper MSI already built, **or** the .NET 9 SDK + WiX v5 on the host so the launcher can build it for you

## Quick Start

From a PowerShell prompt in the repo root:

```powershell
scripts\windows-sandbox\Launch-WinpepperSandbox.ps1
```

This will:

1. Discover or build `artifacts\winpepper-<version>-x64.msi`
2. Generate a temporary `.wsb` file pointing at your repo
3. Launch Windows Sandbox
4. Auto-install the MSI inside the sandbox
5. Run `Winpepper.exe --selftest` and display the result
6. Leave a PowerShell window open with the log tail so you can see `Hotkey hook installed`

When you're done, just **close the Sandbox window** — everything evaporates.

## What happens inside

The generated sandbox configuration:

- Maps `artifacts\` (read-only) → `C:\WinpepperInstaller`
- Maps `scripts\windows-sandbox\` (read-only) → `C:\SandboxScripts`
- Runs `install-in-sandbox.ps1` at logon

That script:

1. Finds the MSI in `C:\WinpepperInstaller`
2. Installs silently with logging to `C:\WinpepperSandbox\install.log`
3. Verifies `%LOCALAPPDATA%\Programs\Winpepper\Winpepper.exe` exists
4. Runs `--selftest` and checks for `WINPEPPER_SELFTEST_OK`
5. Tails the latest log so you can confirm `Hotkey hook installed on thread`
6. Keeps the window open with instructions for manual testing

## Manual testing inside the sandbox

After the auto-install finishes, the PowerShell window stays open.

- **Launch the app:** The script prints the exact path; you can paste it into the sandbox Start menu or Run dialog.
- **Check autostart:** The MSI writes `HKCU\…\Run\Winpepper`; the script validates it.
- **Uninstall test:** The script also prints the `msiexec /x` command if you want to verify clean removal before closing the Sandbox.

## Limitations

- **No real microphone** inside Sandbox (by design), so you cannot test the full hold-to-dictate audio pipeline. You can only verify the app launches, hooks install, and `--selftest` passes.
- **GPU passthrough** depends on your host's Sandbox / Hyper-V vGPU configuration. DirectX 12 probing is attempted; if it fails, the app falls back to CPU for ASR/cleanup (slower but functional).
- **No persistence.** Models, settings, and history downloaded inside the sandbox are lost when you close the window. This is intentional for a clean-room test.

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `"Windows Sandbox is not enabled"` | Open *Windows Features* → check *Windows Sandbox* → reboot. |
| `"MSI not found"` | Build it first: `dotnet build packaging\Winpepper.Msi.wixproj -c Release -r win-x64` (on Windows host with WiX v5; `-r win-x64` is required, otherwise the build fails with NETSDK1047). |
| `Install exit code 1603` | Read `C:\WinpepperSandbox\install.log` inside the sandbox; usually a previous product GUID conflict from an earlier Sandbox session that wasn't fully reset. Close and reopen Sandbox. |
| SmartScreen blocks the unsigned MSI inside Sandbox | Inside the sandbox: right-click the MSI → Properties → Unblock, or run the install script again (it already handles silent install, which bypasses the GUI SmartScreen prompt). |

## Files

| File | Purpose |
|------|---------|
| `Launch-WinpepperSandbox.ps1` | Host-side launcher. Generates `.wsb`, optionally builds MSI, starts Sandbox. |
| `install-in-sandbox.ps1` | Runs *inside* the sandbox. Installs MSI, self-tests, tails logs. |
| `README.md` | This file. |
