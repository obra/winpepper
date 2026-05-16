<#
.SYNOPSIS
    Sign Winpepper binaries and the MSI with an EV code-signing certificate.

.DESCRIPTION
    Off by default. Pass either -Thumbprint <sha1> to sign with an installed certificate,
    or -PfxPath <path> -PfxPassword <pw> to sign with a PFX file.

    When invoked with neither, prints WINPEPPER_SIGNING_DISABLED and exits 0
    (so release pipelines can call sign.ps1 unconditionally).

.PARAMETER Thumbprint
    SHA1 thumbprint of an EV code-signing certificate installed in the current
    user's certificate store.

.PARAMETER PfxPath
    Path to a PFX file containing the EV code-signing certificate.

.PARAMETER PfxPassword
    Password for the PFX file. Required if -PfxPath is given.

.PARAMETER InputFiles
    One or more paths to sign. Globs are expanded.

.EXAMPLE
    pwsh ./packaging/sign.ps1 -Thumbprint 0123ABCD... -InputFiles `
      artifacts/winpepper-0.6.0-x64.msi, src/Winpepper.App/bin/Release/.../Winpepper.exe
#>
[CmdletBinding()]
param(
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [Parameter(Mandatory=$false, ValueFromRemainingArguments=$true)]
    [string[]]$InputFiles
)

$ErrorActionPreference = "Stop"

if (-not $Thumbprint -and -not $PfxPath) {
    Write-Host "WINPEPPER_SIGNING_DISABLED"
    exit 0
}

if (-not $InputFiles -or $InputFiles.Count -eq 0) {
    Write-Error "No input files supplied."
    exit 2
}

# Locate signtool: prefer the Windows SDK install on the build agent.
$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Path
if (-not $signtool) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22000.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $signtool = $c; break }
    }
}
if (-not $signtool) {
    Write-Error "signtool.exe not found on PATH or in the Windows SDK install."
    exit 3
}

$timestampUrl = "http://timestamp.digicert.com"

$expanded = @()
foreach ($pattern in $InputFiles) {
    $matched = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue
    if ($matched) {
        $expanded += $matched.FullName
    } elseif (Test-Path $pattern) {
        $expanded += (Resolve-Path $pattern).Path
    } else {
        Write-Error "Input not found: $pattern"
        exit 4
    }
}

if ($Thumbprint) {
    & $signtool sign /sha1 $Thumbprint `
        /fd SHA256 `
        /tr $timestampUrl `
        /td SHA256 `
        /a $expanded
} else {
    if (-not $PfxPassword) {
        Write-Error "-PfxPassword is required when -PfxPath is supplied."
        exit 5
    }
    & $signtool sign /f $PfxPath /p $PfxPassword `
        /fd SHA256 `
        /tr $timestampUrl `
        /td SHA256 `
        $expanded
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "WINPEPPER_SIGNING_OK ($($expanded.Count) file(s))"
exit 0
