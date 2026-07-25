#requires -Version 5.1
<#
.SYNOPSIS
    Launches Winpepper in Windows Sandbox for ephemeral smoke-testing.
.DESCRIPTION
    Generates a .wsb file, optionally builds the MSI, and starts Windows Sandbox
    with the installer auto-mapped and executed.
#>
[CmdletBinding()]
param(
    [switch]$BuildMsi,
    [string]$MsiPath = $null,
    [switch]$KeepWsb
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$artifactsDir = Join-Path $repoRoot 'artifacts'
$sandboxDir = Join-Path $repoRoot 'scripts\windows-sandbox'

# Verify Windows Sandbox is available
if (-not (Get-Command 'WindowsSandboxClient.exe' -ErrorAction SilentlyContinue)) {
    if (-not (Get-WindowsOptionalFeature -Online -FeatureName 'Containers-DisposableClient' -ErrorAction SilentlyContinue | Where-Object { $_.State -eq 'Enabled' })) {
        throw 'Windows Sandbox does not appear to be enabled. Open ''Windows Features'' and check ''Windows Sandbox'', then reboot.'
    }
}

# Resolve MSI path
$resolvedMsi = $null
if ($MsiPath) {
    $resolvedMsi = Resolve-Path $MsiPath
} else {
    $candidates = Get-ChildItem -Path $artifactsDir -Filter 'winpepper-*-x64.msi' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if ($candidates) {
        $resolvedMsi = $candidates[0].FullName
    }
}

# Optionally build the MSI
if (-not $resolvedMsi -and $BuildMsi) {
    Write-Host 'MSI not found; building...' -ForegroundColor Cyan
    $wixProj = Join-Path $repoRoot 'packaging\Winpepper.Msi.wixproj'
    if (-not (Test-Path $wixProj)) {
        throw "Expected $wixProj does not exist."
    }
    $sdk = dotnet --version 2>$null
    if (-not $sdk) {
        throw '.NET SDK not found on PATH. Install .NET 9 SDK first.'
    }
    $wix = wix --version 2>$null
    if (-not $wix) {
        throw 'WiX v5 not found on PATH. Install WiX v5 (dotnet tool install --global wix) first.'
    }
    & dotnet build $wixProj -c Release -p:UseXamlCompilerExecutable=true | Write-Host
    if ($LASTEXITCODE -ne 0) {
        throw "MSI build failed (exit $LASTEXITCODE)."
    }
    $candidates = Get-ChildItem -Path $artifactsDir -Filter 'winpepper-*-x64.msi' | Sort-Object LastWriteTime -Descending
    if (-not $candidates) {
        throw 'MSI build reported success but no .msi was emitted to artifacts\.'
    }
    $resolvedMsi = $candidates[0].FullName
}

if (-not $resolvedMsi) {
    throw @"
No MSI found in $artifactsDir.
Either:
  1) Build it on Windows: dotnet build packaging\Winpepper.Msi.wixproj -c Release
  2) Pass -MsiPath <path>
  3) Pass -BuildMsi (requires .NET 9 SDK + WiX v5 on host)
"@
}

Write-Host "Using MSI: $resolvedMsi" -ForegroundColor Green

# Ensure the MSI is in artifacts so the mapped folder exposes it
$msiInArtifacts = Join-Path $artifactsDir (Split-Path $resolvedMsi -Leaf)
if ($resolvedMsi -ne $msiInArtifacts) {
    Copy-Item $resolvedMsi $msiInArtifacts -Force
}

# Generate .wsb in a temp location (repo root so relative paths stay short)
$wsbPath = Join-Path $repoRoot 'Winpepper-Sandbox.wsb'
$wsbXml = @"
<Configuration>
  <Networking>Default</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$artifactsDir</HostFolder>
      <SandboxFolder>C:\WinpepperInstaller</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$sandboxDir</HostFolder>
      <SandboxFolder>C:\SandboxScripts</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\SandboxScripts\install-in-sandbox.ps1</Command>
  </LogonCommand>
</Configuration>
"@
Set-Content -Path $wsbPath -Value $wsbXml -Encoding UTF8
Write-Host "Generated $wsbPath" -ForegroundColor DarkGray

# Launch
Write-Host 'Starting Windows Sandbox...' -ForegroundColor Cyan
Start-Process $wsbPath

# Cleanup .wsb after a short delay unless requested to keep
if (-not $KeepWsb) {
    Start-Sleep -Seconds 5
    Remove-Item $wsbPath -ErrorAction SilentlyContinue
    Write-Host "Removed temporary $wsbPath" -ForegroundColor DarkGray
}
