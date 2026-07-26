#requires -Version 5.1
<#
.SYNOPSIS
    Runs inside Windows Sandbox to install and smoke-test Winpepper.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$host.ui.RawUI.WindowTitle = 'Winpepper Sandbox Install & Smoke Test'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Winpepper'
$logDir = 'C:\WinpepperSandbox'
$msiDir = 'C:\WinpepperInstaller'

New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# Find MSI
$msi = Get-ChildItem -Path $msiDir -Filter 'winpepper-*-x64.msi' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msi) {
    throw "No winpepper-*-x64.msi found in $msiDir. Was the artifacts folder mapped correctly?"
}

Write-Host "Found MSI: $($msi.Name)" -ForegroundColor Green

# Install silently with verbose logging
$installLog = Join-Path $logDir 'install.log'
Write-Host "Installing silently (log: $installLog)..." -ForegroundColor Cyan
$proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList "/i `"$($msi.FullName)`" /qn /l*v `"$installLog`"" -Wait -PassThru
if ($proc.ExitCode -ne 0) {
    Write-Host "msiexec returned exit code $($proc.ExitCode)" -ForegroundColor Red
    if (Test-Path $installLog) {
        Get-Content $installLog -Tail 30 | Write-Host -ForegroundColor DarkGray
    }
    throw 'Installation failed. See log above.'
}
Write-Host 'Installation completed successfully.' -ForegroundColor Green

# Verify files landed
$exe = Join-Path $installDir 'Winpepper.exe'
if (-not (Test-Path $exe)) {
    throw "Expected $exe not found after install."
}
Write-Host "Verified: $exe exists." -ForegroundColor Green

# Verify autostart registry key
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = Get-ItemProperty -Path $runKey -Name 'Winpepper' -ErrorAction SilentlyContinue
if ($runValue) {
    Write-Host "Autostart registry value found: $($runValue.Winpepper)" -ForegroundColor Green
} else {
    Write-Host 'WARNING: Autostart registry value not found.' -ForegroundColor Yellow
}

# Run selftest
Write-Host "`nRunning Winpepper --selftest..." -ForegroundColor Cyan
# Winpepper.exe is a GUI-subsystem binary (OutputType=WinExe), and PowerShell
# launches those detached: `$selftest = & $exe --selftest 2>&1` returns
# immediately with NO captured output and no $LASTEXITCODE, so the token check
# always reported a false failure. Start-Process with explicit stdout/stderr
# redirection both waits for exit and captures the output.
$selftestOutLog = Join-Path $logDir 'selftest.out.log'
$selftestErrLog = Join-Path $logDir 'selftest.err.log'
$selftestProc = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow `
    -RedirectStandardOutput $selftestOutLog -RedirectStandardError $selftestErrLog
$selftest = @(Get-Content $selftestOutLog -ErrorAction SilentlyContinue) + @(Get-Content $selftestErrLog -ErrorAction SilentlyContinue)
$selftest | ForEach-Object { Write-Host "  $_" }
if ($selftestProc.ExitCode -ne 0) {
    throw "Self-test failed with exit code $($selftestProc.ExitCode)."
}
if ($selftest -match 'WINPEPPER_SELFTEST_OK') {
    Write-Host 'Self-test PASSED.' -ForegroundColor Green
} else {
    Write-Host 'Self-test output did not contain WINPEPPER_SELFTEST_OK; review output above.' -ForegroundColor Yellow
}

# Tail latest log for hook confirmation
$localAppData = $env:LOCALAPPDATA
$winpepperLogDir = Join-Path $localAppData 'winpepper\logs'
Start-Sleep -Seconds 2
$logFile = Get-ChildItem -Path $winpepperLogDir -Filter 'winpepper-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($logFile) {
    Write-Host "`nLatest log tail ($($logFile.Name)):" -ForegroundColor Cyan
    Get-Content $logFile.FullName -Tail 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
} else {
    Write-Host "No log file found yet under $winpepperLogDir." -ForegroundColor Yellow
}

# Print summary & instructions
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host " Winpepper Sandbox Smoke Test Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host @"

Installed to: $installDir
Logs inside sandbox: $winpepperLogDir
Install log: $installLog

Manual tests you can try inside this sandbox:
  1. Launch the app:
     & '$exe' --tray
  2. Look for the tray icon (bottom-right). Right-click it.
  3. Open the main window, step through Onboarding.
  4. Check Settings, Models, Corrections tabs.

NOTE: This sandbox has no real microphone, so you cannot test
hold-to-record audio dictation. You can still verify UI, logs,
and the self-test passed above.

When finished, just close this Sandbox window — everything
(install, logs, models, settings) evaporates.

To uninstall before closing (optional):
   msiexec /x `"$($msi.FullName)`" /qn

Waiting... (press Ctrl+C to exit this window; close Sandbox to destroy everything)
"@

# Keep window open so user can read results
while ($true) {
    Start-Sleep -Seconds 60
}
