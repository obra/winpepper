# Releasing Winpepper

Winpepper versions are derived by `Nerdbank.GitVersioning` from `version.json`.
The `version.json` carries `0.6.0-alpha` during the alpha phase; bump to `0.6.0`
when shipping the first stable build.

## Bumping the version

```bash
nbgv prepare-release minor       # or: nbgv set-version 0.7.0-alpha
git push origin main release/v0.6.0
```

## Building a signed MSI locally

1. Install the EV code-signing certificate into the current user's certificate
   store (or have the PFX path ready).
2. Build:

```powershell
$env:WINPEPPER_SIGNED = "1"
$env:WinpepperSigningThumbprint = "<sha1>"
dotnet build packaging\Winpepper.Msi.wixproj -c Release
```

The MSI in `artifacts\` is signed; the embedded `Winpepper.exe` is also signed
(see Task 10 caveat about exe-then-MSI sign ordering).

## Building a signed MSI in CI

The nightly workflow does NOT sign. To produce a release build, dispatch the
nightly workflow with secrets — *not yet wired in Plan 6; left for a follow-up
release-engineering plan*. The workflow today produces unsigned MSIs.

## Attaching the MSI to a GitHub release

After a tagged release commit (`v0.6.0`), download the MSI artifact from the
nightly workflow run for that commit, sign it locally with `sign.ps1`, and
upload it to the GitHub release via `gh release upload v0.6.0 artifacts/winpepper-0.6.0-x64.msi`.

## Verifying the signature

```powershell
signtool verify /pa /v "C:\Program Files\Winpepper\Winpepper.exe"
signtool verify /pa /v "winpepper-0.6.0-x64.msi"
```

Both should report `Successfully verified`.
