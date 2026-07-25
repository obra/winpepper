# mt-unc-shim.ps1 -- Winpepper WSL-build shim around mt.exe.
#
# Why: mt.exe from Microsoft.Windows.SDK.BuildTools (observed with
# 10.0.26100.4654) cannot open manifests on \\wsl.localhost UNC paths: it
# normalizes them to an invalid long path (\\?\\\wsl.localhost\...) and fails
# with "general error c1010070", exit code 31. The WindowsAppSDK
# self-contained targets invoke mt.exe to merge WindowsAppSDK.manifest with
# the app manifest (target CreateWinRTRegistration in
# Microsoft.WindowsAppSDK.SelfContained.targets).
#
# What: stages every existing UNC file argument into a stable local temp dir
# (%TEMP%\winpepper-mt, wiped on each run so it never accumulates), rewrites
# -out: to a local file, runs the real mt.exe, then copies the merged output
# back to the original destination. Exits with mt.exe's own exit code.
#
# Wiring: used only when Winpepper.App builds from a UNC project directory --
# see the ManifestTool override in src/Winpepper.App/Winpepper.App.csproj.
# Never invoked on normal C:\ checkouts (CI unaffected).
#
# Override: set WINPEPPER_MT to a full mt.exe path to skip auto-discovery.
$ErrorActionPreference = 'Stop'

$mt = $env:WINPEPPER_MT
if (-not $mt) {
    $packages = $env:NUGET_PACKAGES
    if (-not $packages) { $packages = Join-Path $env:USERPROFILE '.nuget\packages' }
    $candidates = Get-ChildItem -Path (Join-Path $packages 'microsoft.windows.sdk.buildtools\*\bin\*\x64\mt.exe') -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending
    if ($candidates) { $mt = $candidates[0].FullName }
}
if (-not $mt -or -not (Test-Path -LiteralPath $mt)) {
    [Console]::Error.WriteLine('mt-unc-shim: could not locate mt.exe. Set WINPEPPER_MT or restore Microsoft.Windows.SDK.BuildTools.')
    exit 31
}

$stage = Join-Path $env:TEMP 'winpepper-mt'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$outDest = $null
$localOut = Join-Path $stage 'merged.manifest'
$newArgs = @()
$n = 0
for ($i = 0; $i -lt $args.Count; $i++) {
    $a = [string]$args[$i]
    if ($a -like '-out:*') {
        $outDest = $a.Substring(5)
        $newArgs += "-out:$localOut"
    }
    elseif ($a -ieq '-out' -and ($i + 1) -lt $args.Count) {
        # powershell -File splits mt's -out:"<quoted path>" into '-out' + path;
        # rejoin it into the -out:<file> form mt.exe expects.
        $i++
        $outDest = [string]$args[$i]
        $newArgs += "-out:$localOut"
    }
    elseif ($a -like '\\*' -and (Test-Path -LiteralPath $a -PathType Leaf)) {
        $n++
        $local = Join-Path $stage ("in$n-" + [System.IO.Path]::GetFileName($a))
        Copy-Item -LiteralPath $a -Destination $local -Force
        $newArgs += $local
    }
    else {
        $newArgs += $a
    }
}

& $mt @newArgs
$code = $LASTEXITCODE
if ($code -eq 0 -and $outDest) {
    Copy-Item -LiteralPath $localOut -Destination $outDest -Force
}
exit $code
