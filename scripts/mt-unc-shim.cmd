@echo off
rem Winpepper WSL-build shim: mt.exe cannot read \\wsl.localhost UNC manifests.
rem Forwards all arguments to mt-unc-shim.ps1 which stages them locally, runs
rem the real mt.exe, and copies the output back. See mt-unc-shim.ps1 for the
rem full story. Wired via the ManifestTool override in
rem src\Winpepper.App\Winpepper.App.csproj (UNC project dirs only).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0mt-unc-shim.ps1" %*
exit /b %ERRORLEVEL%
