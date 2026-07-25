# Install & Distribution Improvements Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Make Winpepper easy to install from GitHub: fix the red nightly, add a tag-triggered release workflow that publishes a smoke-tested MSI + SHA256 checksum, automate winget submission, commit the Windows Sandbox trial scripts, and rewrite the install/release docs around the new flow.

**Architecture:** Pure CI/scripts/docs work — no .NET code changes. The new `release.yml` mirrors the nightly's proven publish → MSI → install → `--selftest` → uninstall steps as a release gate, then renames the MSI to a tag-derived name, hashes it, and publishes via `softprops/action-gh-release`. Winget submission runs as a second job in the same workflow (a separate `release:`-triggered workflow would never fire, because releases created with `GITHUB_TOKEN` do not trigger other workflows).

**Tech Stack:** GitHub Actions (windows-latest), WiX v5 via `WixToolset.Sdk` (restored by `dotnet restore` — no explicit WiX install step exists or is needed), Nerdbank.GitVersioning, `softprops/action-gh-release@v2`, `vedantmgoyal9/winget-releaser@v2`, PowerShell.

## Global Constraints

- **The app STAYS UNSIGNED — by decision.** Do NOT wire code signing into CI, do NOT add signing secrets, do NOT modify `packaging/sign.ps1` or the wixproj `SignArtifacts` target (they stay as inert scaffolding for possible future use).
- **Worktree:** all work happens in `/home/dan/code/winpepper/.worktrees/install-distribution` (branch `feat/install-distribution`).
- **AGENTS.md test rule:** before EVERY commit, run the Linux test suite with 0 failures: build each project in `tests/` with `-c Release`, then run via the xUnit v3 in-process runner (`dotnet exec <built test dll>`). Do NOT use `dotnet test`. The exact command block is repeated in every task's commit step. A locally provisioned SDK may exist at `/home/dan/code/winpepper/.dotnet/dotnet`; fall back to `dotnet` on PATH.
- **Keep the existing nightly workflow's structure intact** aside from the staleness fixes (no `shell:` additions, no artifact-name refactors, no new assertions).
- **Per-user install facts** (source: `packaging/winpepper.wxs`): `Scope="perUser"`, install dir `%LOCALAPPDATA%\Programs\Winpepper`, autostart `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"...Winpepper.exe" --tray`, no UAC. `msiexec /i <msi> /qn` needs NO `ALLUSERS`/`MSIINSTALLPERUSER` flags — do not add them.
- **MSI build facts:** `dotnet build packaging\Winpepper.Msi.wixproj -c Release -r win-x64` (`-r win-x64` is mandatory — NETSDK1047 otherwise); output lands in gitignored `artifacts/`; built filename is `winpepper-<major.minor.patch.githeight>-x64.msi` (4-part NBGV version, NOT the tag) — always glob `artifacts/winpepper-*-x64.msi`, never construct the name. `fetch-depth: 0` is mandatory for NBGV.
- **Winget identity:** `PackageIdentifier` = `obra.Winpepper`; repo = `github.com/obra/winpepper`; License Apache-2.0; stable `UpgradeCode` = `6C0B2A36-9D4F-44CF-9A3E-A3A4F0C1ED01`; `ProductCode` is `*` (regenerated per build — never hardcode it anywhere).
- **Release asset naming convention (defined by Task 2, used by Tasks 3/5/6):** the MSI is renamed at release time to `winpepper-<version>-x64.msi` where `<version>` = tag without the leading `v` (e.g. `winpepper-0.6.2-alpha-x64.msi`), plus `winpepper-<version>-x64.msi.sha256` containing `<lowercase-hex-sha256> *<filename>`.
- **Version header truth:** `version.json` carries `0.6.2-alpha`; README currently says `0.6.0-alpha` (stale).
- **README.md is the only end-user markdown doc** to be added/rewritten besides the existing `docs/release.md` and the committed `scripts/windows-sandbox/README.md`. Do not create new doc files.
- **Commit style:** Conventional Commits with scope (`fix(ci):`, `feat(ci):`, `docs:`, `chore(scripts):`), focused and atomic.

## Verification limits (state these in the final report)

GitHub Actions workflows cannot be executed locally, and `actionlint` is NOT installed on this machine. Local verification is limited to: YAML well-formedness (PyYAML parse), grep assertions, and careful review. The following can only be verified by real runs after push: the nightly going green on Windows, `release.yml` end-to-end on a `v*` tag, the winget-releaser submission, and the sandbox scripts on a real Windows host.

---

## File Structure

| File | Action | Responsibility |
|---|---|---|
| `.github/workflows/nightly.yml` | Modify (2 lines) | Nightly MSI smoke — fix stale per-machine path assertions |
| `.github/workflows/release.yml` | Create | Tag-triggered release: build → smoke gate → checksum → GitHub Release; second job: winget submission |
| `scripts/windows-sandbox/Launch-WinpepperSandbox.ps1` | Create (copy from main checkout + 1 fix) | Host-side sandbox launcher |
| `scripts/windows-sandbox/install-in-sandbox.ps1` | Create (copy + 1 fix) | In-sandbox install + smoke test |
| `scripts/windows-sandbox/README.md` | Create (copy + 2 fixes) | Sandbox flow docs |
| `.gitignore` | Modify (1 line) | Ignore generated `*.wsb` files |
| `README.md` | Modify | Rewritten install section (winget → MSI+checksum → sandbox), fixed status header, first-run model download |
| `docs/release.md` | Rewrite | Automated release flow + winget one-time setup; manual flow marked obsolete |

Task order matters only where noted: Task 3 edits the file Task 2 creates; Task 5 links the directory Task 4 commits.

---

### Task 1: Fix nightly.yml stale per-machine path assertions

The nightly has been red for ≥3 runs because commit `74ac5bc` converted the MSI to per-user scope (`%LOCALAPPDATA%\Programs\Winpepper`) but `.github/workflows/nightly.yml` still asserts `C:\Program Files\Winpepper\Winpepper.exe` in two places (lines 96 and 124). A repo-wide review found these are the ONLY two stale occurrences in `.github/`; everything else in the workflow is already per-user-correct and must NOT be changed:

- `msiexec` invocations (lines 87, 118) correctly pass no `ALLUSERS`/`MSIINSTALLPERUSER` — `Scope="perUser"` in the wxs bakes the scope in; adding `ALLUSERS=1` would force per-machine and break `/qn`.
- The HKCU Run-key checks (lines 107, 128) were always HKCU and remain correct.
- `ci.yml` never installs the MSI and has no path assertions — no changes there.

**Files:**
- Modify: `.github/workflows/nightly.yml:96` and `.github/workflows/nightly.yml:124`

**Interfaces:**
- Consumes: nothing.
- Produces: the corrected per-user assertion idiom `Join-Path $env:LOCALAPPDATA 'Programs\Winpepper\Winpepper.exe'`, which Task 2 reuses verbatim in `release.yml`.

- [ ] **Step 1: Run the failing check (proves current staleness)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -n 'Program Files' .github/workflows/nightly.yml
```
Expected: exactly 2 matches, lines 96 and 124 (`$exe = "C:\Program Files\Winpepper\Winpepper.exe"` and `if (Test-Path 'C:\Program Files\Winpepper\Winpepper.exe') {`).

- [ ] **Step 2: Fix line 96 (Selftest installed binary step)**

Replace (exact current text, preserving the 10-space indentation):
```powershell
          $exe = "C:\Program Files\Winpepper\Winpepper.exe"
```
with:
```powershell
          $exe = Join-Path $env:LOCALAPPDATA 'Programs\Winpepper\Winpepper.exe'
```

- [ ] **Step 3: Fix line 124 (Uninstall MSI step)**

Replace (exact current text):
```powershell
          if (Test-Path 'C:\Program Files\Winpepper\Winpepper.exe') {
```
with:
```powershell
          if (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\Winpepper\Winpepper.exe')) {
```

- [ ] **Step 4: Verify the fix**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -c 'Program Files' .github/workflows/nightly.yml
grep -c "Join-Path \$env:LOCALAPPDATA 'Programs" .github/workflows/nightly.yml
python3 -c "import yaml; yaml.safe_load(open('.github/workflows/nightly.yml')); print('YAML OK')"
```
Expected: `0`, then `2`, then `YAML OK`. (If `python3` lacks PyYAML: `pip3 install --user pyyaml`, or `uv run --with pyyaml python3 -c "..."`.)

- [ ] **Step 5: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: every project's xUnit summary reports 0 failures; final line `ALL_TESTS_GREEN`.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add .github/workflows/nightly.yml
git commit -m "fix(ci): assert the per-user install path in the nightly MSI smoke

The per-user MSI conversion (74ac5bc) moved the install dir from
C:\Program Files\Winpepper to %LOCALAPPDATA%\Programs\Winpepper, but the
nightly selftest and uninstall assertions were never updated, so the
nightly has been red since. No other post-conversion staleness exists in
.github/ (msiexec flags and HKCU Run-key checks were already correct)."
```

---

### Task 2: Add the tag-triggered release workflow (`release.yml`)

Runs on `v*` tag pushes. Mirrors the nightly's proven steps as a release gate (publish → MSI → silent install → `--selftest` → autostart check → silent uninstall), then renames the MSI to the tag-derived asset name, generates a `.sha256`, and publishes a GitHub Release (prerelease when the tag contains a `-` suffix). No signing anywhere.

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: corrected per-user assertions from Task 1; nightly's step bodies (duplicated deliberately — the spec prefers duplication over refactoring both into a reusable workflow).
- Produces: job id `release-msi` (Task 3's winget job does `needs: release-msi`); release assets `winpepper-<version>-x64.msi` + `winpepper-<version>-x64.msi.sha256` where `<version>` = tag minus leading `v` (Tasks 5/6 document these); `.sha256` content format `<lowercase-hex-sha256> *<filename>`; prerelease rule `contains(github.ref_name, '-')`.

- [ ] **Step 1: Run the failing check**

Run: `test -f /home/dan/code/winpepper/.worktrees/install-distribution/.github/workflows/release.yml && echo EXISTS || echo MISSING`
Expected: `MISSING`

- [ ] **Step 2: Write `.github/workflows/release.yml`**

Create the file with exactly this content (Task 3 appends a second job later):

```yaml
name: Release MSI

on:
  push:
    tags: ['v*']

permissions:
  contents: write   # create the GitHub Release and upload assets

jobs:
  release-msi:
    runs-on: windows-latest
    timeout-minutes: 60
    steps:
      - name: Checkout
        uses: actions/checkout@v4
        with:
          fetch-depth: 0   # Nerdbank.GitVersioning needs history

      - name: Setup .NET 9
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: Restore
        run: dotnet restore

      - name: Publish Winpepper.App
        run: |
          dotnet publish src/Winpepper.App/Winpepper.App.csproj `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -p:WindowsPackageType=None

      - name: Locate or stub WinAppSDK bootstrapper
        id: bootstrap
        run: |
          # Mirrors nightly.yml: the MSI's <Binary> bootstrapper reference is
          # consumed by an InstallWinAppSdk custom action gated FALSE, so a
          # stub satisfies the wxs bind without affecting behavior. Prefer the
          # real exe when the NuGet cache has one.
          $pkg = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages" -Recurse -Filter 'WindowsAppRuntimeInstall-x64.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
          New-Item -ItemType Directory -Path "packaging\bootstrapper" -Force | Out-Null
          if ($pkg) {
            Copy-Item $pkg.FullName -Destination "packaging\bootstrapper\WindowsAppRuntimeInstall-x64.exe" -Force
            Write-Host "Using real WindowsAppRuntimeInstall-x64.exe from $($pkg.FullName)"
          } else {
            'stub' | Out-File -Encoding ASCII 'packaging\bootstrapper\WindowsAppRuntimeInstall-x64.exe'
            Write-Host "::warning::Real WindowsAppRuntimeInstall-x64.exe not found in NuGet cache; placed text stub. This is fine while the InstallWinAppSdk MSI custom action remains gated FALSE."
          }

      - name: Build MSI
        # -r win-x64 is required: without it, the wixproj's transitive
        # dependency on Winpepper.History (net9.0-windows10.0.19041.0)
        # fails with NETSDK1047.
        run: dotnet build packaging\Winpepper.Msi.wixproj -c Release -r win-x64

      - name: Locate MSI and derive release asset name
        id: version
        run: |
          # The built filename carries NBGV's 4-part version
          # (winpepper-0.6.2.240-x64.msi) — glob it, then rename to the
          # tag-derived name (winpepper-0.6.2-alpha-x64.msi) so release asset
          # URLs are predictable for winget and for humans.
          $msi = Get-ChildItem -Path "artifacts" -Filter "winpepper-*-x64.msi" | Select-Object -First 1
          if (-not $msi) { Write-Error "No MSI produced."; exit 1 }
          Write-Host "Built MSI: $($msi.Name)"
          $tagVersion = "$env:GITHUB_REF_NAME".TrimStart('v')
          $assetName = "winpepper-$tagVersion-x64.msi"
          $assetPath = Join-Path $msi.DirectoryName $assetName
          if ($msi.Name -ne $assetName) { Move-Item $msi.FullName $assetPath -Force }
          "msi=$assetPath" >> $env:GITHUB_OUTPUT
          "name=$assetName" >> $env:GITHUB_OUTPUT

      - name: Install MSI (silent)
        run: |
          # Start-Process -Wait is a PowerShell cmdlet, not a native command,
          # so it does NOT update $LASTEXITCODE. Capture the process with
          # -PassThru and read its ExitCode property explicitly.
          $msi = "${{ steps.version.outputs.msi }}"
          $proc = Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /l*v artifacts\install.log" -Wait -PassThru
          if ($proc.ExitCode -ne 0) {
            Get-Content artifacts\install.log -Tail 200
            Write-Error "msiexec install failed (exit code $($proc.ExitCode))"
            exit 1
          }

      - name: Selftest installed binary
        run: |
          $exe = Join-Path $env:LOCALAPPDATA 'Programs\Winpepper\Winpepper.exe'
          if (-not (Test-Path $exe)) { Write-Error "Winpepper.exe not installed"; exit 1 }
          $output = & $exe --selftest
          $output | Write-Host
          if ($output -notmatch "WINPEPPER_SELFTEST_OK") {
            Write-Error "Selftest token missing"
            exit 1
          }

      - name: Verify autostart Run key
        run: |
          $val = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Winpepper -ErrorAction SilentlyContinue
          if (-not $val -or $val.Winpepper -notmatch '--tray') {
            Write-Error "Autostart Run key not set as expected. Got: $($val.Winpepper)"
            exit 1
          }
          Write-Host "Autostart OK: $($val.Winpepper)"

      - name: Uninstall MSI (silent)
        run: |
          # Start-Process -Wait doesn't update $LASTEXITCODE; use -PassThru.
          $msi = "${{ steps.version.outputs.msi }}"
          $proc = Start-Process msiexec.exe -ArgumentList "/x `"$msi`" /qn /l*v artifacts\uninstall.log" -Wait -PassThru
          if ($proc.ExitCode -ne 0) {
            Get-Content artifacts\uninstall.log -Tail 200
            Write-Error "msiexec uninstall failed (exit code $($proc.ExitCode))"
            exit 1
          }
          if (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\Winpepper\Winpepper.exe')) {
            Write-Error "INSTALLFOLDER not cleaned up by uninstall"
            exit 1
          }
          $val = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Winpepper -ErrorAction SilentlyContinue
          if ($val) {
            Write-Error "Run key not removed by uninstall: $($val.Winpepper)"
            exit 1
          }

      - name: Generate SHA256 checksum
        id: checksum
        run: |
          # Hash the exact bytes being uploaded (after the rename) — winget's
          # InstallerSha256 must match the published asset.
          $msi = "${{ steps.version.outputs.msi }}"
          $name = "${{ steps.version.outputs.name }}"
          $hash = (Get-FileHash -Algorithm SHA256 $msi).Hash.ToLowerInvariant()
          $checksumPath = "$msi.sha256"
          # "<hash> *<filename>" is sha256sum-compatible and easy to eyeball.
          "$hash *$name" | Out-File -Encoding ascii -NoNewline $checksumPath
          Write-Host "SHA256: $hash"
          "path=$checksumPath" >> $env:GITHUB_OUTPUT

      - name: Publish GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          files: |
            ${{ steps.version.outputs.msi }}
            ${{ steps.checksum.outputs.path }}
          prerelease: ${{ contains(github.ref_name, '-') }}
          generate_release_notes: true
          fail_on_unmatched_files: true

      - name: Upload install/uninstall logs on failure
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: release-msi-logs
          path: |
            artifacts/install.log
            artifacts/uninstall.log
          if-no-files-found: ignore
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
python3 -c "
import yaml
d = yaml.safe_load(open('.github/workflows/release.yml'))
trigger = d.get('on', d.get(True))   # PyYAML parses a bare 'on:' key as boolean True
assert trigger['push']['tags'] == ['v*'], trigger
assert d['permissions']['contents'] == 'write'
assert list(d['jobs']) == ['release-msi'], list(d['jobs'])
print('YAML OK')"
grep -c 'Program Files' .github/workflows/release.yml || true
grep -ci 'signtool\|sign.ps1\|SigningThumbprint\|SigningPfx' .github/workflows/release.yml || true
```
Expected: `YAML OK`, then `0` for `Program Files`, then `0` for signing references (no signing steps).

- [ ] **Step 4: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: 0 failures everywhere; `ALL_TESTS_GREEN`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add .github/workflows/release.yml
git commit -m "feat(ci): add tag-triggered release workflow (smoke-gated MSI + sha256)

On v* tags: publish -> build the real MSI -> silent install ->
--selftest -> autostart check -> silent uninstall (the nightly's proven
smoke steps, with per-user paths), then rename the MSI to the
tag-derived winpepper-<version>-x64.msi, emit a .sha256, and publish
both to a GitHub Release (prerelease when the tag has a -suffix).
Releases are unsigned by decision - no signing steps or secrets."
```

---

### Task 3: Add the winget submission job to `release.yml`

A separate workflow triggered on `release: types: [published]` would NEVER fire: `release.yml` creates the release using the default `GITHUB_TOKEN`, and events created with `GITHUB_TOKEN` do not trigger other workflows (GitHub anti-recursion rule). So winget submission runs as a second job in `release.yml`, after the release is published, using winget-releaser's `release-tag` input. It is gated on a repository variable `WINGET_AUTOSUBMIT` so the job is skipped (not red) until the one-time external setup (Task 6 documents it: `WINGET_TOKEN` PAT, winget-pkgs fork, first manual PR) is complete — `vars` is usable in job-level `if`, `secrets` is not.

**Files:**
- Modify: `.github/workflows/release.yml` (append a job; created in Task 2)

**Interfaces:**
- Consumes: job id `release-msi` and the `winpepper-<version>-x64.msi` asset naming from Task 2.
- Produces: winget identifier `obra.Winpepper`, secret name `WINGET_TOKEN`, repo variable name `WINGET_AUTOSUBMIT` — Task 6 documents all three; Task 5's README uses the identifier.

- [ ] **Step 1: Run the failing check**

Run: `grep -c 'winget-releaser' /home/dan/code/winpepper/.worktrees/install-distribution/.github/workflows/release.yml`
Expected: `0`

- [ ] **Step 2: Append the winget job**

Append to the end of `.github/workflows/release.yml` (top-level under `jobs:`, i.e. indented 2 spaces, after the `release-msi` job):

```yaml
  # Submits the just-published release to microsoft/winget-pkgs.
  #
  # Deliberately a job here rather than a `release:`-triggered workflow:
  # the release above is created with the default GITHUB_TOKEN, and events
  # created by GITHUB_TOKEN do not trigger other workflows.
  #
  # Skipped until the repo variable WINGET_AUTOSUBMIT is set to 'true'
  # (one-time setup: WINGET_TOKEN secret + winget-pkgs fork + first manual
  # submission — see docs/release.md). winget-releaser can only UPDATE a
  # package that already exists in winget-pkgs.
  winget:
    needs: release-msi
    if: vars.WINGET_AUTOSUBMIT == 'true'
    runs-on: windows-latest
    steps:
      - name: Submit obra.Winpepper to microsoft/winget-pkgs
        uses: vedantmgoyal9/winget-releaser@v2
        with:
          identifier: obra.Winpepper
          release-tag: ${{ github.ref_name }}
          installers-regex: '\.msi$'
          token: ${{ secrets.WINGET_TOKEN }}
```

- [ ] **Step 3: Verify**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
python3 -c "
import yaml
d = yaml.safe_load(open('.github/workflows/release.yml'))
jobs = d['jobs']
assert list(jobs) == ['release-msi', 'winget'], list(jobs)
assert jobs['winget']['needs'] == 'release-msi'
assert jobs['winget']['if'] == \"vars.WINGET_AUTOSUBMIT == 'true'\"
step = jobs['winget']['steps'][0]
assert step['with']['identifier'] == 'obra.Winpepper'
print('YAML OK')"
```
Expected: `YAML OK`

- [ ] **Step 4: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: 0 failures everywhere; `ALL_TESTS_GREEN`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add .github/workflows/release.yml
git commit -m "feat(ci): submit releases to winget-pkgs via winget-releaser

Second job in release.yml (a release-event workflow would never fire for
GITHUB_TOKEN-created releases). Gated on the WINGET_AUTOSUBMIT repo
variable so it stays skipped until the one-time winget setup (PAT, fork,
first manual submission) documented in docs/release.md is done."
```

---

### Task 4: Commit the Windows Sandbox scripts (fixed for per-user install)

`scripts/windows-sandbox/` exists ONLY in the main checkout at `/home/dan/code/winpepper/scripts/windows-sandbox/` (untracked); the worktree does not have it. Copy the three files in, then fix the per-user staleness and the internal contradictions before committing. Stage the directory explicitly — never `git add -A` (the main checkout has unrelated untracked files).

**Files:**
- Create: `scripts/windows-sandbox/Launch-WinpepperSandbox.ps1` (copy + networking fix)
- Create: `scripts/windows-sandbox/install-in-sandbox.ps1` (copy + install-dir fix)
- Create: `scripts/windows-sandbox/README.md` (copy + 2 doc fixes)
- Modify: `.gitignore` (append `*.wsb`)

**Interfaces:**
- Consumes: nothing.
- Produces: tracked `scripts/windows-sandbox/` directory + its `README.md` — Task 5's README links both.

- [ ] **Step 1: Copy the untracked scripts into the worktree**

Run:
```bash
cp -r /home/dan/code/winpepper/scripts/windows-sandbox /home/dan/code/winpepper/.worktrees/install-distribution/scripts/
cd /home/dan/code/winpepper/.worktrees/install-distribution
ls scripts/windows-sandbox/
```
Expected: `Launch-WinpepperSandbox.ps1  README.md  install-in-sandbox.ps1`

- [ ] **Step 2: Run the failing check (proves the stale path)**

Run: `grep -rn "Program Files" scripts/windows-sandbox/`
Expected: 2 matches — `install-in-sandbox.ps1:12` (`$installDir = 'C:\Program Files\Winpepper'`) and `README.md:7` ("without touching your host machine's `Program Files` or registry").

- [ ] **Step 3: Fix `install-in-sandbox.ps1` line 12**

Replace:
```powershell
$installDir = 'C:\Program Files\Winpepper'
```
with:
```powershell
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Winpepper'
```
This single fix cascades correctly: lines 40–43 (`$exe = Join-Path $installDir 'Winpepper.exe'` + the hard `throw` that made the smoke test fail on every successful per-user install), line 86 (`Installed to: $installDir` summary), and line 92 (`& '$exe' --tray` instruction) all derive from `$installDir`/`$exe` variables. No other edits needed in this file — the msiexec flags (lines 29, 105), HKCU autostart check (47–53), and `%LOCALAPPDATA%\winpepper\logs` tail (69–72) are already per-user-correct.

- [ ] **Step 4: Fix `scripts/windows-sandbox/README.md`**

Edit 1 — line 7, replace:
```markdown
Winpepper is currently **agent-built, human-untested**. Running it in Windows Sandbox lets you try the MSI, verify the self-test, and exercise the UI without touching your host machine's `Program Files` or registry.
```
with:
```markdown
Winpepper is currently **agent-built, human-untested**. Running it in Windows Sandbox lets you try the MSI, verify the self-test, and exercise the UI without touching your host machine's profile — the per-user install (`%LOCALAPPDATA%`, `HKCU`) happens inside the disposable sandbox instead.
```

Edit 2 — line 46, replace:
```markdown
3. Verifies `%ProgramFiles%\Winpepper\Winpepper.exe` exists
```
with:
```markdown
3. Verifies `%LOCALAPPDATA%\Programs\Winpepper\Winpepper.exe` exists
```

- [ ] **Step 5: Fix `Launch-WinpepperSandbox.ps1` networking**

The generated `.wsb` sets `<Networking>Disable</Networking>` (line 87), which makes the ~1.2 GB first-run model download impossible — contradicting this flow's own README (lines 12, 62–63 budget disk for models downloaded inside the sandbox). Replace:
```xml
  <Networking>Disable</Networking>
```
with:
```xml
  <Networking>Default</Networking>
```
(`Default` enables networking; it is inside the PowerShell here-string starting `$wsbXml = @"`.)

- [ ] **Step 6: Add `*.wsb` to `.gitignore`**

`Launch-WinpepperSandbox.ps1` writes `Winpepper-Sandbox.wsb` to the repo root (deleted after 5 s unless `-KeepWsb`, but crashes/`-KeepWsb` leave it behind). Append to `.gitignore` (after the existing line 18 `/.dotnet/`):
```
# Generated Windows Sandbox config (scripts/windows-sandbox/)
*.wsb
```

- [ ] **Step 7: Verify**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -rc "Program Files" scripts/windows-sandbox/ | grep -v ':0' || echo NO_STALE_PATHS
grep -n "Networking>Default" scripts/windows-sandbox/Launch-WinpepperSandbox.ps1
grep -n '\*\.wsb' .gitignore
git check-ignore scripts/windows-sandbox/README.md && echo IGNORED || echo NOT_IGNORED
```
Expected: `NO_STALE_PATHS`; one `Networking>Default` match; one `*.wsb` match in `.gitignore`; `NOT_IGNORED`.

- [ ] **Step 8: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: 0 failures everywhere; `ALL_TESTS_GREEN`.

- [ ] **Step 9: Commit (stage explicitly)**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add scripts/windows-sandbox/ .gitignore
git commit -m "chore(scripts): commit the Windows Sandbox trial flow, fixed for per-user install

Previously untracked. Fixes vs the untracked originals: install dir
corrected from C:\Program Files\Winpepper to %LOCALAPPDATA%\Programs\
Winpepper (the stale path made the in-sandbox smoke test hard-fail on
every successful install), sandbox networking enabled so the ~1.2 GB
first-run model download is testable, README path/wording updated, and
generated *.wsb files gitignored."
```

---

### Task 5: Rewrite the README install section

Priority order: (a) winget one-liner (with how-to-get-winget and "once accepted" gating language), (b) MSI from Releases with SHA256 verification + SmartScreen guidance, (c) Windows Sandbox trial flow. Also fix the stale status header and make the first-run ~1.2 GB model download an explicit step.

**Files:**
- Modify: `README.md:33` (status header) and `README.md:58-92` (the entire `## Install (MSI)` section, up to but not including `## Performance: what to expect` at line 94)

**Interfaces:**
- Consumes: asset names `winpepper-<version>-x64.msi` / `.msi.sha256` (Task 2), identifier `obra.Winpepper` (Task 3), `scripts/windows-sandbox/` + its README (Task 4).
- Produces: nothing downstream.

- [ ] **Step 1: Run the failing check**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -c 'winget' README.md
grep -n '## Status: 0.6.0-alpha' README.md
```
Expected: `0` winget matches; status match on line 33.

- [ ] **Step 2: Fix the status header (line 33)**

Replace:
```markdown
## Status: 0.6.0-alpha — agent-built, human-untested
```
with (matches `version.json`):
```markdown
## Status: 0.6.2-alpha — agent-built, human-untested
```

- [ ] **Step 3: Replace the install section**

Replace everything from line 58 (`## Install (MSI)`) through line 92 (the end of the migration blockquote, `> upgrades track correctly afterward.`) — keeping `## Performance: what to expect` and everything after untouched — with exactly:

````markdown
## Install

Three ways to get Winpepper, in recommended order.

### Option 1: winget

> **Availability note:** this works once `obra.Winpepper` is accepted into
> [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) — the first
> submission needs moderator review. Until then, use Option 2 below.

```powershell
winget install obra.Winpepper
```

Don't have `winget`? It ships with modern Windows 11 as part of **App
Installer**. If the command isn't recognized, install or update "App Installer"
from the Microsoft Store, or grab the latest release from
[microsoft/winget-cli](https://github.com/microsoft/winget-cli/releases).

### Option 2: MSI from GitHub Releases

Download `winpepper-<version>-x64.msi` from the [Releases page](../../releases)
and run it.

**Verify the download (recommended):** each release also ships a
`winpepper-<version>-x64.msi.sha256` file containing the expected hash. Compare
(case-insensitive) against your download with either:

```powershell
certutil -hashfile winpepper-<version>-x64.msi SHA256
# or
Get-FileHash winpepper-<version>-x64.msi -Algorithm SHA256
```

The MSI is **unsigned** — a deliberate decision for now (the
`packaging/sign.ps1` scaffolding exists if that ever changes). Windows will
show a SmartScreen warning on first launch — click "More info" → "Run anyway".

### Option 3: Try it in Windows Sandbox first

Want to kick the tires without installing anything on your machine?
[`scripts/windows-sandbox/`](scripts/windows-sandbox/) launches a disposable
Windows Sandbox, auto-installs the MSI, runs the self-test, and evaporates when
you close the window. See
[`scripts/windows-sandbox/README.md`](scripts/windows-sandbox/README.md).

### Requirements

- Windows 11 22H2 or newer (build 22621+), x64
- ~700 MB free disk for the install
- Another ~1.2 GB for the ASR + cleanup models (see first-run step below)
- DirectX 12 GPU (recommended for ASR; the model will fall back to CPU otherwise)

### First run: download the models (~1.2 GB)

Winpepper cannot dictate until its two local models are downloaded — expect
this one-time step on first launch:

1. Launch Winpepper (Start Menu; it also starts hidden in the tray on logon).
2. The onboarding wizard offers a "Download models" step — or open the
   **Models** tab and click **Download Missing Models**.
3. ~1.2 GB downloads from HuggingFace (resumable, SHA-256 verified) into
   `%LOCALAPPDATA%\winpepper\models\`.

Until then, dictation reports "Speech model not installed. Open the Models tab
to download it."

### After install

Winpepper installs **per-user** — no administrator rights and **no UAC prompt**
for install, upgrade, or uninstall.

- Files land in `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; not `Program Files`)
- User data (settings, corrections, downloaded models, audio history) lives in
  `%LOCALAPPDATA%\winpepper\` — a separate folder that survives reinstalls and
  uninstalls
- Autostart is enabled: `HKCU\…\Run\Winpepper` runs the app hidden in the tray on
  logon

To uninstall: standard Add/Remove Programs entry (no elevation needed), or
`winget uninstall obra.Winpepper` if you installed via winget. User data is
preserved; delete `%LOCALAPPDATA%\winpepper\` yourself if you want a fully
clean slate.

> **Migrating from an older per-machine build?** Earlier releases installed to
> `C:\Program Files\Winpepper` (per-machine). Uninstall that one first — that one
> removal still needs elevation — before installing this per-user package, so
> upgrades track correctly afterward.
````

- [ ] **Step 4: Verify**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -n 'winget install obra.Winpepper' README.md
grep -n 'certutil -hashfile' README.md
grep -n 'scripts/windows-sandbox/README.md' README.md
grep -n '## Status: 0.6.2-alpha' README.md
grep -n 'Run anyway' README.md
grep -n '## Performance: what to expect' README.md
```
Expected: one match each (Performance heading proves the following section survived intact).

- [ ] **Step 5: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: 0 failures everywhere; `ALL_TESTS_GREEN`.

- [ ] **Step 6: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add README.md
git commit -m "docs: rewrite the README install section around winget, checksums, and Sandbox

winget one-liner first (with how to get winget and once-accepted
gating), MSI + SHA256 verification + SmartScreen guidance second,
Windows Sandbox trial flow third. Fixes the stale 0.6.0-alpha status
header (version.json is 0.6.2-alpha) and adds the first-run ~1.2 GB
model download as an explicit install step."
```

---

### Task 6: Rewrite `docs/release.md` for the automated flow

Replace the manual download-sign-upload instructions with the new automated flow, add the precise winget one-time setup, and mark the old flow obsolete. The doc currently claims version `0.6.0-alpha` and filename `winpepper-0.6.0-x64.msi` — both wrong even before this work.

**Files:**
- Rewrite: `docs/release.md` (full replacement; currently 48 lines)

**Interfaces:**
- Consumes: release flow, asset naming, prerelease rule (Task 2); `WINGET_TOKEN` / `WINGET_AUTOSUBMIT` / `obra.Winpepper` (Task 3).
- Produces: nothing downstream.

- [ ] **Step 1: Run the failing check**

Run: `grep -n 'sign it locally' /home/dan/code/winpepper/.worktrees/install-distribution/docs/release.md`
Expected: 1 match (line 38 area) — the manual signed flow still documented.

- [ ] **Step 2: Replace `docs/release.md` entirely with:**

````markdown
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
  scrutiny.
- Known caveat: winget-releaser auto-updates URLs, hashes, and versions on each
  release, but it does **not** recompute `AppsAndFeaturesEntries.DisplayVersion`
  — spot-check it on each auto-opened PR.
- Prerelease-tagged releases are submitted too; if you want alphas kept off
  winget, set `WINGET_AUTOSUBMIT` to `false` before tagging and restore it after.

## Obsolete: the old manual flow

Before `release.yml`, releasing meant dispatching the nightly workflow,
downloading the MSI artifact, signing it locally with `sign.ps1`, and running
`gh release upload` by hand. **That flow is retired.** CI signing was never
wired up and — by decision — will not be: releases ship unsigned.
````

- [ ] **Step 3: Verify**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
grep -n 'WINGET_TOKEN' docs/release.md
grep -n 'WINGET_AUTOSUBMIT' docs/release.md
grep -n 'unsigned — by decision' docs/release.md
grep -n 'wingetcreate new' docs/release.md
grep -cn 'sign it locally' docs/release.md || echo MANUAL_FLOW_GONE
```
Expected: matches for the first four; `MANUAL_FLOW_GONE` for the last.

- [ ] **Step 4: Run the Linux test suite (AGENTS.md gate)**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet; [ -x "$DOTNET" ] || DOTNET=dotnet
set -e
for proj in tests/*/; do
  name=$(basename "$proj")
  "$DOTNET" build "tests/$name/$name.csproj" -c Release
  # Some test projects multi-target net9.0 + net9.0-windows...; on Linux run
  # the pure-managed net9.0 build (the windows-TFM dll needs WindowsDesktop
  # and fails on Linux). Do NOT glob net9.0* — the windows dir sorts first.
  dll="tests/$name/bin/Release/net9.0/$name.dll"
  "$DOTNET" exec "$dll"
done
echo ALL_TESTS_GREEN
```
Expected: 0 failures everywhere; `ALL_TESTS_GREEN`.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/install-distribution
git add docs/release.md
git commit -m "docs: document the automated tag-to-release-to-winget flow

nbgv bump -> push v* tag -> release.yml builds, smoke-gates, and
publishes the MSI + .sha256 -> winget job submits to winget-pkgs.
Documents the one-time winget setup (WINGET_TOKEN classic PAT,
winget-pkgs fork, first manual wingetcreate submission with
AppsAndFeaturesEntries/UpgradeCode, WINGET_AUTOSUBMIT variable) and
retires the manual download-sign-upload flow; unsigned by decision."
```

---

## Self-Review (performed at plan-writing time)

1. **Spec coverage:** Deliverable 1 → Task 1 (both stale lines fixed; review of remaining workflow documented: nothing else stale, msiexec/HKCU checks deliberately untouched, ci.yml clean). Deliverable 2 → Task 2 (tag trigger, mirrored smoke gate, SHA256, softprops release, prerelease detection, no signing). Deliverable 3 → Task 3 (winget-releaser automation, `obra.Winpepper`, gated) + Task 6 (first-submission manual PR, PAT/fork one-time setup, moderator review time, unsigned-MSI acceptance). Deliverable 4 → Task 4 (scripts committed, path fixed, plus networking/`.wsb` hygiene) with the README link in Task 5. Deliverable 5 → Task 5 (winget first with winget-install instructions and acceptance gating, MSI + SmartScreen + CertUtil/Get-FileHash checksum second, Sandbox third, status header fixed, first-run 1.2 GB model step added). Deliverable 6 → Task 6. No gaps; no UNRESOLVED COVERAGE GAP entries needed.
2. **No silent deferrals:** the only outcomes not provable from this machine are real Windows CI runs and the winget submission — inherent to the constraint "GitHub Actions workflows cannot be executed locally", which the spec explicitly acknowledges and asks to be reported, not tasked around. Everything implementable in-repo is implemented; the external one-time winget steps are documented precisely (spec's stated intent). No stubs or mocks anywhere.
3. **Placeholder scan:** no TBD/TODO; every step carries exact code, exact commands, expected output.
4. **Type/name consistency:** asset name `winpepper-<version>-x64.msi` + `.msi.sha256` consistent across Tasks 2/3/5/6; job id `release-msi` matches the winget job's `needs:`; `WINGET_TOKEN`/`WINGET_AUTOSUBMIT`/`obra.Winpepper` identical in Tasks 3/5/6; per-user path idiom identical in Tasks 1/2/4.
