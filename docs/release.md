# Releasing Winpepper

Winpepper versions are derived by `Nerdbank.GitVersioning` from `version.json`.
Releases are **automated**: pushing a `v*` tag builds, smoke-tests, and
publishes the MSI plus a SHA256 checksum to a GitHub Release, then submits the
release to winget-pkgs.

**Winpepper ships unsigned — by decision.** No code signing runs in CI and no
signing secrets exist. `packaging/sign.ps1` and the wixproj `SignArtifacts`
target are retained, untouched and unwired, in case that decision ever changes.

## Cutting a release

1. Bump the version on `main`:

```bash
nbgv prepare-release minor       # or: nbgv set-version 0.7.0-alpha
git push origin main release/v0.6.2   # push whatever branches nbgv created/updated
```

2. Tag the commit to release and push the tag:

```bash
git tag v0.6.2-alpha
git push origin v0.6.2-alpha
```

The tag must match `v<major>.<minor>.<patch>` plus an optional single
alphanumeric prerelease token (`v0.6.2-alpha` and `v0.7.0` work;
`v0.6.2-alpha.1` does NOT — the `publicReleaseRefSpec` regex in `version.json`
disallows dotted suffixes).

3. The tag push triggers `.github/workflows/release.yml` (`release-msi` job):
   - publishes the app and builds the real MSI (WiX v5, self-contained win-x64)
   - **release gate:** silently installs the MSI on the runner, runs
     `Winpepper.exe --selftest`, verifies the `HKCU` autostart Run key, and
     silently uninstalls — nothing is published unless all of that passes
   - renames the MSI from its build name (`winpepper-<a.b.c.height>-x64.msi`,
     the 4-part NBGV version) to the tag-derived
     `winpepper-<version>-x64.msi` (e.g. `winpepper-0.6.2-alpha-x64.msi`)
   - writes `winpepper-<version>-x64.msi.sha256` (lowercase hex, hashed after
     the rename so it matches the published bytes)
   - creates the GitHub Release for the tag with both files attached, marked
     **prerelease** when the tag contains a `-` suffix (`-alpha`, `-beta`, …)

4. The `winget` job then runs
   [winget-releaser](https://github.com/vedantmgoyal9/winget-releaser), opening
   a version-update PR against
   [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) for
   `obra.Winpepper`. It is skipped unless the repo variable
   `WINGET_AUTOSUBMIT` is `true` (see one-time setup below).

## winget: one-time setup (manual, external to this repo)

winget-releaser can only **update a package that already exists** in
winget-pkgs, so the first submission is manual and needs moderator review
(typically days, sometimes a couple of weeks — plan for that; the winget
install path in the README only works after acceptance).

1. **PAT:** create a *classic* GitHub Personal Access Token with the
   `public_repo` scope on the account that will own the winget-pkgs fork. Add
   it to this repo as the Actions secret `WINGET_TOKEN`
   (Settings → Secrets and variables → Actions → Secrets).
2. **Fork:** fork `microsoft/winget-pkgs` under that same account —
   winget-releaser pushes manifest branches there and opens PRs from it.
3. **First submission** with
   [wingetcreate](https://github.com/microsoft/winget-create), pointing at a
   published release asset:

```powershell
wingetcreate new https://github.com/obra/winpepper/releases/download/<tag>/winpepper-<version>-x64.msi
```

   Fill the prompts with: `PackageIdentifier` `obra.Winpepper`,
   `PackageVersion` = the tag without the leading `v` (e.g. `0.6.2-alpha`),
   `Publisher` `Winpepper`, `PackageName` `Winpepper`, `License` `Apache-2.0`,
   `ShortDescription` `Local dictation for Windows 11`. Before submitting, edit
   the generated installer manifest to add:

```yaml
Scope: user
InstallerType: wix
AppsAndFeaturesEntries:
  - UpgradeCode: '{6C0B2A36-9D4F-44CF-9A3E-A3A4F0C1ED01}'
    DisplayVersion: <the 4-part build version, e.g. 0.6.2.240>
```

   The `UpgradeCode` is stable across builds; the ARP `DisplayVersion` is the
   4-part NBGV version (visible in the release workflow log as the MSI's
   original build name) and is what lets winget correlate the installed app
   with the package version. **Never** hardcode `ProductCode` — the wxs
   regenerates it every build. Submit with `wingetcreate submit`.
4. **Enable automation:** once the first PR is merged, set the repo variable
   `WINGET_AUTOSUBMIT` to `true`
   (Settings → Secrets and variables → Actions → Variables).

Notes:
- winget accepts **unsigned** MSIs — installers are validated by the SHA256 in
  the manifest — though unsigned packages can attract extra moderator/Defender
  scrutiny (e.g. automated dynamic-scan stalls on the first PR that may need a
  fix-up push or a polite moderator ping; typical new-package merge time is
  about a week).
- Known caveat: winget-releaser auto-updates URLs, hashes, and versions on each
  release, but it does **not** recompute `AppsAndFeaturesEntries.DisplayVersion`
  — spot-check it on each auto-opened PR. And because Winpepper's ARP
  `DisplayVersion` (4-part build version) diverges from `PackageVersion`
  (tag-derived), winget-pkgs authoring rules make `AppsAndFeaturesEntries`
  **mandatory in every future manifest version** — never drop it, or installed
  copies stop correlating and upgrades loop.
- Prerelease-tagged releases are submitted too; if you want alphas kept off
  winget, set `WINGET_AUTOSUBMIT` to `false` before tagging and restore it after.

## Obsolete: the old manual flow

Before `release.yml`, releasing meant dispatching the nightly workflow,
downloading the MSI artifact, signing it locally with `sign.ps1`, and running
`gh release upload` by hand. **That flow is retired.** CI signing was never
wired up and — by decision — will not be: releases ship unsigned.
