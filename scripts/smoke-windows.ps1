<#
.SYNOPSIS
    Automated checks for the long-lived Windows dictation smoke test.

.DESCRIPTION
    Runs the machine-checkable half of docs/windows-smoke-test.md on a real
    Windows machine where Winpepper was installed from the MSI:

      * install payload under %LOCALAPPDATA%\Programs\Winpepper
      * Add/Remove Programs (ARP) registry entry
      * HKCU Software\Winpepper version stamp
      * HKCU autostart Run key
      * app launches (--tray) and the process stays alive
      * newest log file is fresh and contains "Hotkey hook installed"
      * %LOCALAPPDATA%\winpepper state (settings.json, logs/, models/,
        history/, corrections.json)
      * optional: Winpepper.exe --selftest emits WINPEPPER_SELFTEST_OK

    Steps that require a human (speaking into the microphone, verifying text
    lands in Notepad, reboot cycles, upgrade-over-profile) are listed as
    MANUAL in the summary; see docs/windows-smoke-test.md.

.EXAMPLE
    pwsh -File scripts\smoke-windows.ps1 -RunSelftest

.NOTES
    Exit code 0 = no FAILed checks; 1 = at least one FAIL.
#>
[CmdletBinding()]
param(
    # Where the MSI installed the app.
    [string]$InstallDir = (Join-Path (Join-Path $env:LOCALAPPDATA 'Programs') 'Winpepper'),

    # Per-user state root.
    [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'winpepper'),

    # Seconds to wait after launching before asserting the process is alive.
    [int]$LaunchWaitSeconds = 15,

    # The newest log file must have been written within this many minutes.
    [int]$LogFreshMinutes = 10,

    # Do not launch the app; only assert against current machine state.
    [switch]$SkipLaunch,

    # Also run "Winpepper.exe --selftest" and assert WINPEPPER_SELFTEST_OK.
    [switch]$RunSelftest
)

$ErrorActionPreference = 'Stop'

$script:Results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [ValidateSet('PASS', 'FAIL', 'WARN', 'MANUAL')] [string]$Status,
        [string]$Detail = ''
    )
    $script:Results.Add([pscustomobject]@{ Check = $Name; Status = $Status; Detail = $Detail })
    $color = switch ($Status) {
        'PASS'   { 'Green' }
        'FAIL'   { 'Red' }
        'WARN'   { 'Yellow' }
        'MANUAL' { 'Cyan' }
    }
    Write-Host ("[{0,-6}] {1} {2}" -f $Status, $Name, $Detail) -ForegroundColor $color
}

$exePath = Join-Path $InstallDir 'Winpepper.exe'

# ---------------------------------------------------------------- install ---
if (Test-Path -LiteralPath $exePath) {
    $ver = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
    Add-Result 'InstallPayload' 'PASS' "$exePath (FileVersion $ver)"
} else {
    Add-Result 'InstallPayload' 'FAIL' "missing $exePath"
}

$icoPath = Join-Path $InstallDir 'Assets\AppIcon.ico'
if (Test-Path -LiteralPath $icoPath) {
    Add-Result 'InstallAssets' 'PASS' $icoPath
} else {
    Add-Result 'InstallAssets' 'FAIL' "missing $icoPath"
}

# ---------------------------------------------------------- ARP / registry ---
$arpRoots = @(
    # Per-user MSI registers ARP under HKCU. HKLM roots kept as a fallback so
    # this script still detects a legacy per-machine install during migration.
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
$arp = $null
foreach ($root in $arpRoots) {
    if (-not (Test-Path $root)) { continue }
    $arp = Get-ChildItem $root |
        ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'Winpepper' } |
        Select-Object -First 1
    if ($arp) { break }
}
if ($arp) {
    Add-Result 'ArpEntry' 'PASS' ("DisplayVersion {0}" -f $arp.DisplayVersion)
} else {
    Add-Result 'ArpEntry' 'FAIL' 'no Add/Remove Programs entry named "Winpepper"'
}

$stampKey = 'HKCU:\SOFTWARE\Winpepper'
if (Test-Path $stampKey) {
    $stamp = Get-ItemProperty $stampKey
    Add-Result 'HkcuVersionStamp' 'PASS' ("InstallVersion {0}, InstallDir {1}" -f $stamp.InstallVersion, $stamp.InstallDir)
} else {
    Add-Result 'HkcuVersionStamp' 'FAIL' "missing $stampKey"
}

# ---------------------------------------------------------------- autostart ---
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runProps = Get-ItemProperty -Path $runKey -Name 'Winpepper' -ErrorAction SilentlyContinue
$runVal = if ($runProps) { $runProps.Winpepper } else { $null }
if ($runVal -and $runVal -match 'Winpepper\.exe' -and $runVal -match '--tray') {
    Add-Result 'AutostartRunKey' 'PASS' $runVal
} elseif ($runVal) {
    Add-Result 'AutostartRunKey' 'FAIL' ("unexpected value: {0}" -f $runVal)
} else {
    Add-Result 'AutostartRunKey' 'FAIL' "no 'Winpepper' value under $runKey (note: the MSI only writes it on fresh installs)"
}

# ----------------------------------------------------------------- selftest ---
if ($RunSelftest) {
    if (Test-Path -LiteralPath $exePath) {
        # Winpepper.exe is a GUI-subsystem binary; capturing its output makes
        # PowerShell wait for exit. Match the token (same contract as the
        # nightly CI job) rather than trusting $LASTEXITCODE for a WinExe.
        $selftestOut = & $exePath --selftest 2>&1 | Out-String
        if ($selftestOut -match 'WINPEPPER_SELFTEST_OK') {
            Add-Result 'Selftest' 'PASS' 'WINPEPPER_SELFTEST_OK token emitted'
        } else {
            Add-Result 'Selftest' 'FAIL' ("WINPEPPER_SELFTEST_OK missing; output: {0}" -f $selftestOut.Trim())
        }
    } else {
        Add-Result 'Selftest' 'FAIL' 'Winpepper.exe missing; cannot run --selftest'
    }
}

# ------------------------------------------------------------------- launch ---
$proc = Get-Process -Name 'Winpepper' -ErrorAction SilentlyContinue
if (-not $proc -and -not $SkipLaunch) {
    if (Test-Path -LiteralPath $exePath) {
        Write-Host "Launching $exePath --tray and waiting $LaunchWaitSeconds s..."
        Start-Process -FilePath $exePath -ArgumentList '--tray' | Out-Null
        Start-Sleep -Seconds $LaunchWaitSeconds
        $proc = Get-Process -Name 'Winpepper' -ErrorAction SilentlyContinue
    }
}
if ($proc) {
    Add-Result 'ProcessAlive' 'PASS' ("PID {0}, started {1}" -f ($proc | Select-Object -First 1).Id, ($proc | Select-Object -First 1).StartTime)
} elseif ($SkipLaunch) {
    Add-Result 'ProcessAlive' 'WARN' 'Winpepper not running (-SkipLaunch set, did not start it)'
} else {
    Add-Result 'ProcessAlive' 'FAIL' "Winpepper.exe is not running $LaunchWaitSeconds s after launch"
}

# --------------------------------------------------------------------- logs ---
$logsDir = Join-Path $DataDir 'logs'
$newestLog = $null
if (Test-Path -LiteralPath $logsDir) {
    $newestLog = Get-ChildItem -LiteralPath $logsDir -Filter 'winpepper-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if ($newestLog) {
    $ageMin = ((Get-Date) - $newestLog.LastWriteTime).TotalMinutes
    if ($ageMin -le $LogFreshMinutes) {
        Add-Result 'LogFreshness' 'PASS' ("{0} written {1:N1} min ago" -f $newestLog.Name, $ageMin)
    } else {
        Add-Result 'LogFreshness' 'FAIL' ("newest log {0} is {1:N1} min old (limit {2})" -f $newestLog.Name, $ageMin, $LogFreshMinutes)
    }
    # Tail enough of the log to catch the most recent startup sequence.
    $tail = Get-Content -LiteralPath $newestLog.FullName -Tail 2000 -ErrorAction SilentlyContinue
    if ($tail -match 'Hotkey hook installed') {
        Add-Result 'HotkeyHookLogged' 'PASS' '"Hotkey hook installed" found in newest log'
    } else {
        Add-Result 'HotkeyHookLogged' 'FAIL' ('"Hotkey hook installed" not found in last 2000 lines of {0}' -f $newestLog.Name)
    }
} else {
    Add-Result 'LogFreshness' 'FAIL' "no winpepper-*.log under $logsDir"
    Add-Result 'HotkeyHookLogged' 'FAIL' 'no log file to inspect'
}

# -------------------------------------------------------------- user state ---
$settingsPath = Join-Path $DataDir 'settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $null = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
        Add-Result 'SettingsJson' 'PASS' "$settingsPath parses as JSON"
    } catch {
        Add-Result 'SettingsJson' 'FAIL' ("{0} is not valid JSON: {1}" -f $settingsPath, $_.Exception.Message)
    }
} else {
    Add-Result 'SettingsJson' 'FAIL' "missing $settingsPath (run the app once / finish onboarding first)"
}

$modelsDir = Join-Path $DataDir 'models'
if (Test-Path -LiteralPath $modelsDir) {
    $modelCount = (Get-ChildItem -LiteralPath $modelsDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($modelCount -gt 0) {
        Add-Result 'ModelsDir' 'PASS' ("{0} files under {1}" -f $modelCount, $modelsDir)
    } else {
        Add-Result 'ModelsDir' 'WARN' "$modelsDir exists but is empty (models not downloaded yet)"
    }
} else {
    Add-Result 'ModelsDir' 'FAIL' "missing $modelsDir"
}

$historyDir = Join-Path $DataDir 'history'
if (Test-Path -LiteralPath $historyDir) {
    $histCount = (Get-ChildItem -LiteralPath $historyDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Add-Result 'HistoryDir' 'PASS' ("{0} files under {1}" -f $histCount, $historyDir)
} else {
    Add-Result 'HistoryDir' 'WARN' "missing $historyDir (created after the first dictation)"
}

$correctionsPath = Join-Path $DataDir 'corrections.json'
if (Test-Path -LiteralPath $correctionsPath) {
    Add-Result 'CorrectionsJson' 'PASS' $correctionsPath
} else {
    Add-Result 'CorrectionsJson' 'WARN' "missing $correctionsPath (created once a correction is added)"
}

# ------------------------------------------------------------- manual steps ---
Add-Result 'Dictation'      'MANUAL' 'focus Notepad, hold the hotkey, speak a known phrase, verify the text appears'
Add-Result 'RebootCycle'    'MANUAL' 'reboot; verify Winpepper autostarts hidden to tray, reopens from tray, still dictates'
Add-Result 'UpgradePersist' 'MANUAL' 'install a newer MSI over this profile; re-run this script; verify settings/history survived'

# ------------------------------------------------------------------ summary ---
Write-Host ''
Write-Host '=== Winpepper Windows smoke summary ==='
$script:Results | Format-Table -AutoSize | Out-String | Write-Host

$failCount = @($script:Results | Where-Object { $_.Status -eq 'FAIL' }).Count
if ($failCount -gt 0) {
    Write-Host "RESULT: FAIL ($failCount failed check(s))" -ForegroundColor Red
    exit 1
}
Write-Host 'RESULT: PASS (all automated checks passed; complete the MANUAL steps above)' -ForegroundColor Green
exit 0
