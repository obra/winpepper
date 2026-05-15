# Installs the toolchain Winpepper needs on a fresh Windows 11 VM.
# Run via: Get-Content scripts/provision-vm.ps1 | ./scripts/winssh

$ErrorActionPreference = "Stop"

function Add-ToMachinePath {
    param([string]$Path)
    $current = [Environment]::GetEnvironmentVariable("Path", "Machine")
    if ($current -notlike "*$Path*") {
        [Environment]::SetEnvironmentVariable("Path", "$current;$Path", "Machine")
        Write-Host "Added to machine PATH: $Path"
    }
}

# .NET 9 SDK ---------------------------------------------------------------
$dotnetVersion = try { & dotnet --version 2>$null } catch { $null }
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith("9.")) {
    Write-Host "Installing .NET 9 SDK..."
    $installer = "$env:TEMP\dotnet-sdk-9.exe"
    Invoke-WebRequest -UseBasicParsing -Uri "https://aka.ms/dotnet/9.0/dotnet-sdk-win-x64.exe" -OutFile $installer
    Start-Process -Wait -FilePath $installer -ArgumentList "/quiet", "/norestart"
    Add-ToMachinePath "C:\Program Files\dotnet"
} else {
    Write-Host ".NET SDK $dotnetVersion already installed"
}

# Visual Studio Build Tools (C++ + Windows SDK) ----------------------------
$vsTools = "C:\BuildTools"
if (-not (Test-Path "$vsTools\MSBuild\Current\Bin\MSBuild.exe")) {
    Write-Host "Installing VS Build Tools..."
    $bs = "$env:TEMP\vs_BuildTools.exe"
    Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vs_buildtools.exe" -OutFile $bs
    Start-Process -Wait -FilePath $bs -ArgumentList @(
        "--quiet", "--wait", "--norestart", "--nocache",
        "--installPath", $vsTools,
        "--add", "Microsoft.VisualStudio.Workload.VCTools",
        "--add", "Microsoft.VisualStudio.Component.Windows11SDK.22621",
        "--add", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
    )
} else {
    Write-Host "VS Build Tools already installed"
}

# Git for Windows (if not present) -----------------------------------------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Git for Windows..."
    $git = "$env:TEMP\git-installer.exe"
    Invoke-WebRequest -Uri "https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.1/Git-2.47.1-64-bit.exe" -OutFile $git
    Start-Process -Wait -FilePath $git -ArgumentList "/VERYSILENT /NORESTART /NOCANCEL /SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
}

$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$env:Path = "$machinePath;$userPath"

Write-Host ""
Write-Host "Provisioning done."
$dotnetExe = if (Get-Command dotnet -ErrorAction SilentlyContinue) { "dotnet" } else { "C:\Program Files\dotnet\dotnet.exe" }
Write-Host "  dotnet: $(& $dotnetExe --version)"
Write-Host "  git:    $(& git --version)"
