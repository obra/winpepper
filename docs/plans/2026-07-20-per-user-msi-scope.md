# Per-User MSI Scope Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Convert Winpepper's MSI from per-machine to per-user scope so that
installing, upgrading, and uninstalling it never triggers a UAC / elevation
prompt.

**Architecture:** Flip `Package/@Scope` from `perMachine` to `perUser`, move
the install tree from `ProgramFiles64Folder` to
`%LOCALAPPDATA%\Programs\Winpepper` (the VS Code / Squirrel per-user
convention), move the version-stamp registry component from HKLM to HKCU, and
sweep every tracked file that hard-codes the old `C:\Program Files\Winpepper`
install location or per-machine assumption (smoke script, app autostart path,
tests, docs, design spec). The MSI itself cannot be built or ICE-validated on
this Linux host, so correctness is proven by a **static XML validator**
(`packaging/validate-wxs.py`, runnable here with `python3`) plus targeted
`grep` gates; the real build + install verification happens later on the
Windows host.

**Tech Stack:** WiX Toolset v5 (`WixToolset.Sdk`), Windows Installer, .NET 9 /
WinUI 3 (app), PowerShell (smoke/provisioning), Python 3 + `xmllint` (the only
verification tooling available on this Linux host).

## Global Constraints

- WiX v5 authoring; the wxs schema namespace is `http://wixtoolset.org/schemas/v4/wxs` (unchanged by the v5 SDK).
- `Package/@Scope="perUser"`. Per WiX docs this leaves `ALLUSERS` null and sets install privileges to *limited* → no elevation. Do NOT also set `InstallPrivileges` or `ALLUSERS` by hand; `Scope` owns that.
- Install directory (verbatim): `%LOCALAPPDATA%\Programs\Winpepper` → WiX `StandardDirectory Id="LocalAppDataFolder"` ⭢ `Directory Id="ProgramsFolder" Name="Programs"` ⭢ `Directory Id="INSTALLFOLDER" Name="$(ProductName)"`.
- Per-user **data** directory is unchanged: `%LOCALAPPDATA%\winpepper` (lowercase, holds settings/logs/models/history). It is a DIFFERENT path from the new install dir `%LOCALAPPDATA%\Programs\Winpepper`. Never conflate them.
- The autostart `Run` key stays HKCU (already is). The MSI writes it via `"[INSTALLFOLDER]Winpepper.exe" --tray`, which auto-resolves to the new location — no literal path change needed inside the MSI Run-key component.
- `MajorUpgrade` is preserved exactly: `AllowDowngrades="no"`, `Schedule="afterInstallInitialize"`. Note in docs that a per-user `MajorUpgrade` only detects prior **per-user** installs of the same `UpgradeCode` — which is the intended end state.
- The version-stamp component keeps its existing GUID `6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed05`. Reuse is safe: this is the first per-user release, so no per-user install carrying that GUID exists in the field. (An HKLM→HKCU KeyPath change would normally warrant a new GUID for the *same* product line; here there is no prior per-user product line to collide with.)
- This host is Linux: **no** WiX toolchain, **no** Windows Installer, **no** `dotnet`, **no** `pwsh`. Available: `python3`, `xmllint`. Do NOT attempt to install .NET/WiX. Managed (.NET) tests and the MSI build run later on the Windows host.
- Keep the diff minimal and focused. Do NOT restructure unrelated wxs regions.
- Do NOT edit historical records under `docs/superpowers/plans/` — they are point-in-time build logs, not living docs. Only the design **spec** (`docs/superpowers/specs/2026-05-15-winpepper-design.md`) is updated (spec requirement #5).
- `.NET SDK` paths (`C:\Program Files\dotnet` in `provision-vm.ps1`) and `signtool` SDK paths (`Program Files (x86)\Windows Kits` in `packaging/sign.ps1`) are toolchain locations, NOT Winpepper's install scope — leave them unchanged.

---

## Spec Coverage Map

Every numbered requirement in the task maps to a task below. No requirement is
deferred to "known limitations" or "future work".

| Spec requirement | Covered by |
|---|---|
| #1 `Scope="perUser"` + install dir → `%LOCALAPPDATA%\Programs\Winpepper`; consistent per-user authoring | Task 1 |
| #1 verify against WiX v5 docs (LocalAppDataFolder is a valid per-user target; Scope sets privileges) | Task 1 (grounded: WiX docs + Windows Installer per-user LocalAppData guidance) |
| #2 `HKLMVersionStamp` → HKCU, rename component; autostart Run key stays HKCU | Task 1 |
| #3 audit launch condition, `MSI_WIN_BUILD` probe, ProgramMenuFolder shortcut, `InstallWinAppSdk` CA (gated), SummaryInfo/InstallPrivileges | Task 1 (audit table + validator asserts no HKLM *writes*, no per-machine dirs) |
| #4 sweep `scripts/smoke-windows.ps1` | Task 2 |
| #4 sweep app autostart path + tests | Task 3 |
| #4 sweep `docs` (README + smoke/manual/release docs) | Tasks 5, 6 |
| #4 audit `packaging/probes/*`, `packaging/sign.ps1`, `provision-vm.ps1` | Task 7 (audit-only, evidenced no-change) |
| #5 update design spec §7.7 + §11, record rationale + migration note | Task 4 |
| Verification: XML well-formed, refs resolve, no orphan Ids; run pure-managed tests | Task 1 validator + Task 7 final gate; managed tests noted for Windows host (no `dotnet` here) |

---

## File Structure

Files created or modified, and the one responsibility each carries:

- **Create** `packaging/validate-wxs.py` — static structural gate for the wxs (well-formedness, per-user scope, no HKLM writes, per-user directory root, ref/Id resolution). The only automated MSI-correctness proof available on Linux; also reusable in CI.
- **Modify** `packaging/winpepper.wxs` — scope, install directory tree, version-stamp hive/name, Feature `ComponentRef`.
- **Modify** `scripts/smoke-windows.ps1` — default `InstallDir`, ARP search roots, version-stamp key (HKLM→HKCU), header comments.
- **Modify** `src/Winpepper.App/Views/RecordingPage.xaml.cs` — the Settings autostart-toggle default exe path (per-machine literal → per-user path).
- **Modify** `tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs` — the illustrative install path used in the quoting assertion.
- **Modify** `docs/superpowers/specs/2026-05-15-winpepper-design.md` — §7.7 autostart path, §11 packaging scope + rationale + migration note.
- **Modify** `README.md` — "Install (MSI)" install location + no-elevation + migration note.
- **Modify** `docs/windows-smoke-test.md`, `docs/manual-test.md`, `docs/release.md` — install-path references.
- **Create** `docs/plans/2026-07-20-per-user-msi-scope.md` — this plan (working/agent doc).

---

## Task 1: Static wxs validator + convert winpepper.wxs to per-user scope

**Files:**
- Create: `packaging/validate-wxs.py`
- Modify: `packaging/winpepper.wxs` (lines 14, 53, 56-58, 98-113)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `packaging/validate-wxs.py`, run as `python3 packaging/validate-wxs.py` from the repo root; exit 0 = pass, exit 1 = fail (prints each failure). Later tasks (Task 7) re-run it as a gate.

**Audit performed in this task (spec requirement #3) — conclusions baked into the validator or left intentionally unchanged:**

| Element | Finding | Action |
|---|---|---|
| `Launch` condition (`MSI_WIN_BUILD >= 22621`) | pure gate, no elevation; `VersionNT64` already dropped | leave |
| `MSI_WIN_BUILD` `RegistrySearch` on HKLM `CurrentBuildNumber` | a **read**; reads from HKLM need no elevation | leave (validator only forbids HKLM *writes*) |
| `RunCapabilityProbe` CA | `Execute="immediate" Impersonate="yes" Return="ignore"`, writes `%TEMP%` only | leave |
| `InstallWinAppSdk` CA | `Execute="deferred" Impersonate="no"` but gated `Condition="0=1"` (never runs); deferred/non-impersonated does not itself force package elevation — package `Scope` governs that | leave gated |
| `ProgramMenuFolder` Start-menu shortcut | for a per-user package the Installer resolves `ProgramMenuFolder` to the user's Start Menu; component KeyPath is already HKCU | leave |
| `SummaryInformation` / `Package` | no `InstallPrivileges`/`ALLUSERS`/elevation attributes present; `Scope="perUser"` is the single control | leave |
| `MajorUpgrade` | preserved (`AllowDowngrades="no"`, `Schedule="afterInstallInitialize"`) | leave |

- [ ] **Step 1: Write the validator (the failing test)**

Create `packaging/validate-wxs.py` with exactly this content:

```python
#!/usr/bin/env python3
"""Static structural checks for packaging/winpepper.wxs.

The WiX toolchain and Windows Installer are unavailable on Linux, so the MSI
cannot be built or ICE-validated here. This script enforces the invariants we
CAN verify statically for the per-user scope conversion:

  1. The file is well-formed XML.
  2. The package installs per-user (Scope="perUser") -> no UAC elevation.
  3. No component WRITES to HKLM (per-machine hive). Reads (RegistrySearch)
     from HKLM are allowed and need no elevation.
  4. The install tree roots at LocalAppDataFolder (per-user convention) and
     never at ProgramFiles64Folder (per-machine).
  5. Every ComponentRef / ComponentGroupRef resolves to a component/group
     defined in this file, except build-generated groups (HarvestedFiles).
  6. Every Component @Directory attribute resolves to a Directory or
     StandardDirectory Id defined in this file (or a well-known folder).

Exit code 0 = all checks pass; 1 = at least one failure (details printed).
"""
from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

WXS_PATH = Path(__file__).resolve().parent / "winpepper.wxs"

# ComponentGroups generated by the WiX build (Heat HarvestDirectory in
# Winpepper.Msi.wixproj), not defined in winpepper.wxs.
GENERATED_COMPONENT_GROUPS = {"HarvestedFiles"}

# Directory ids resolved by the Windows Installer itself.
WELL_KNOWN_DIRECTORY_IDS = {
    "LocalAppDataFolder",
    "ProgramFiles64Folder",
    "ProgramFilesFolder",
    "ProgramMenuFolder",
    "AppDataFolder",
    "TARGETDIR",
}


def localname(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def main() -> int:
    failures: list[str] = []

    # Check 1: well-formed XML.
    try:
        tree = ET.parse(WXS_PATH)
    except ET.ParseError as exc:
        print(f"FAIL: {WXS_PATH.name} is not well-formed XML: {exc}")
        return 1
    root = tree.getroot()

    by_name: dict[str, list[ET.Element]] = {}
    for e in root.iter():
        by_name.setdefault(localname(e.tag), []).append(e)

    # Check 2: per-user scope.
    scopes = [p.get("Scope") for p in by_name.get("Package", [])]
    if "perUser" not in scopes:
        failures.append(f"Package/@Scope must be 'perUser'; found {scopes}")

    # Check 3: no HKLM writes (RegistryValue). RegistrySearch reads are allowed.
    for rv in by_name.get("RegistryValue", []):
        if rv.get("Root") == "HKLM":
            failures.append(
                "RegistryValue writes to HKLM (needs elevation): "
                f"Key={rv.get('Key')} Name={rv.get('Name')}"
            )

    # Check 4: directory roots.
    std_ids = {d.get("Id") for d in by_name.get("StandardDirectory", [])}
    if "LocalAppDataFolder" not in std_ids:
        failures.append(
            "StandardDirectory 'LocalAppDataFolder' is required for per-user install"
        )
    if "ProgramFiles64Folder" in std_ids:
        failures.append(
            "StandardDirectory 'ProgramFiles64Folder' is per-machine; remove it"
        )

    defined_dir_ids = set(std_ids)
    for d in by_name.get("Directory", []):
        if d.get("Id"):
            defined_dir_ids.add(d.get("Id"))

    # Check 5: ref resolution.
    comp_ids = {c.get("Id") for c in by_name.get("Component", []) if c.get("Id")}
    for ref in by_name.get("ComponentRef", []):
        rid = ref.get("Id")
        if rid not in comp_ids:
            failures.append(f"ComponentRef Id='{rid}' has no matching Component")

    group_ids = {
        g.get("Id") for g in by_name.get("ComponentGroup", []) if g.get("Id")
    }
    for ref in by_name.get("ComponentGroupRef", []):
        rid = ref.get("Id")
        if rid not in group_ids and rid not in GENERATED_COMPONENT_GROUPS:
            failures.append(
                f"ComponentGroupRef Id='{rid}' has no matching ComponentGroup"
            )

    # Check 6: Component @Directory resolves.
    resolvable = defined_dir_ids | WELL_KNOWN_DIRECTORY_IDS
    for c in by_name.get("Component", []):
        d = c.get("Directory")
        if d and d not in resolvable:
            failures.append(
                f"Component Id='{c.get('Id')}' Directory='{d}' does not resolve"
            )

    if failures:
        print(f"FAIL: {len(failures)} problem(s) in {WXS_PATH.name}:")
        for f in failures:
            print(f"  - {f}")
        return 1

    print(f"PASS: {WXS_PATH.name} passes all per-user static checks")
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

- [ ] **Step 2: Run the validator to verify it FAILS on the current (per-machine) wxs**

Run: `python3 packaging/validate-wxs.py; echo "exit=$?"`
Expected: exit=1, with failures listing at least:
- `Package/@Scope must be 'perUser'; found ['perMachine']`
- `RegistryValue writes to HKLM ... Name=InstallVersion`
- `RegistryValue writes to HKLM ... Name=InstallDir`
- `StandardDirectory 'LocalAppDataFolder' is required for per-user install`
- `StandardDirectory 'ProgramFiles64Folder' is per-machine; remove it`

(This is RED for the right reason: the wxs is still per-machine.)

- [ ] **Step 3: Flip the package scope**

In `packaging/winpepper.wxs`, replace (line 14):

```
           Scope="perMachine"
```

with:

```
           Scope="perUser"
```

- [ ] **Step 4: Point the Feature at the renamed version-stamp component**

In `packaging/winpepper.wxs`, replace (line 53):

```
      <ComponentRef Id="HKLMVersionStamp" />
```

with:

```
      <ComponentRef Id="HKCUVersionStamp" />
```

- [ ] **Step 5: Move the install directory to the per-user location**

In `packaging/winpepper.wxs`, replace the whole block (lines 56-58):

```
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="$(ProductName)" />
    </StandardDirectory>
```

with:

```
    <!-- Per-user install: %LOCALAPPDATA%\Programs\Winpepper (the VS Code /
         Squirrel per-user convention). Scope="perUser" installs without
         elevation; the Installer resolves LocalAppDataFolder to the invoking
         user's %LOCALAPPDATA%. Installing under a subdirectory of
         LocalAppDataFolder is a supported per-user pattern. -->
    <StandardDirectory Id="LocalAppDataFolder">
      <Directory Id="ProgramsFolder" Name="Programs">
        <Directory Id="INSTALLFOLDER" Name="$(ProductName)" />
      </Directory>
    </StandardDirectory>
```

- [ ] **Step 6: Move the version stamp from HKLM to HKCU and rename the component**

In `packaging/winpepper.wxs`, replace the whole block (lines 98-113):

```
    <!-- A marker so future MSIs can detect a prior install location. -->
    <Component Id="HKLMVersionStamp"
               Directory="INSTALLFOLDER"
               Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed05">
      <RegistryValue Root="HKLM"
                     Key="Software\Winpepper"
                     Name="InstallVersion"
                     Type="string"
                     Value="!(bind.FileVersion.WinpepperExe)"
                     KeyPath="yes" />
      <RegistryValue Root="HKLM"
                     Key="Software\Winpepper"
                     Name="InstallDir"
                     Type="string"
                     Value="[INSTALLFOLDER]" />
    </Component>
```

with:

```
    <!-- A marker so future MSIs can detect a prior install location. Per-user
         install => the stamp lives in HKCU (writing HKLM would require
         elevation). The autostart Run key below is already HKCU. GUID is
         intentionally unchanged: this is the first per-user release, so no
         per-user install carrying this component id exists in the field. -->
    <Component Id="HKCUVersionStamp"
               Directory="INSTALLFOLDER"
               Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed05">
      <RegistryValue Root="HKCU"
                     Key="Software\Winpepper"
                     Name="InstallVersion"
                     Type="string"
                     Value="!(bind.FileVersion.WinpepperExe)"
                     KeyPath="yes" />
      <RegistryValue Root="HKCU"
                     Key="Software\Winpepper"
                     Name="InstallDir"
                     Type="string"
                     Value="[INSTALLFOLDER]" />
    </Component>
```

- [ ] **Step 7: Run the validator to verify it PASSES**

Run: `python3 packaging/validate-wxs.py; echo "exit=$?"`
Expected: exit=0, output `PASS: winpepper.wxs passes all per-user static checks`

- [ ] **Step 8: Independently confirm XML well-formedness**

Run: `xmllint --noout packaging/winpepper.wxs; echo "exit=$?"`
Expected: exit=0, no output (well-formed).

- [ ] **Step 9: Confirm no orphaned old ids remain**

Run: `grep -n "HKLMVersionStamp\|ProgramFiles64Folder" packaging/winpepper.wxs; echo "exit=$?"`
Expected: exit=1 (grep found nothing — both old ids are gone).

- [ ] **Step 10: Commit**

```bash
git add packaging/validate-wxs.py packaging/winpepper.wxs
git commit -m "feat: convert MSI to per-user scope (no elevation)

- Package Scope perMachine -> perUser
- Install tree ProgramFiles64Folder -> LocalAppDataFolder\Programs\Winpepper
- Version stamp component HKLM -> HKCU (renamed HKCUVersionStamp)
- Add packaging/validate-wxs.py static gate (MSI cannot be built on Linux)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 2: Update smoke-windows.ps1 for the per-user install location

**Files:**
- Modify: `scripts/smoke-windows.ps1` (lines 9, 11, 32, 88-91, 107, 110-112)

**Interfaces:**
- Consumes: the per-user install dir `%LOCALAPPDATA%\Programs\Winpepper` and HKCU version stamp established in Task 1.
- Produces: an updated smoke script whose defaults match the per-user MSI. (No `pwsh` on this host — verification is static `grep`; the script runs on the Windows host later.)

- [ ] **Step 1: Repoint the default install directory (the change under test)**

In `scripts/smoke-windows.ps1`, replace (line 32):

```
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Winpepper'),
```

with:

```
    [string]$InstallDir = (Join-Path (Join-Path $env:LOCALAPPDATA 'Programs') 'Winpepper'),
```

- [ ] **Step 2: Verify the install-dir default changed**

Run: `grep -n "LOCALAPPDATA.*Programs.*Winpepper" scripts/smoke-windows.ps1; echo "exit=$?"`
Expected: exit=0, matches line 32 (the new default).

Run: `grep -n "env:ProgramFiles 'Winpepper'" scripts/smoke-windows.ps1; echo "exit=$?"`
Expected: exit=1 (the old per-machine default is gone).

- [ ] **Step 3: Move the ARP search to the per-user hive**

A per-user MSI writes its Add/Remove Programs entry under HKCU, not HKLM. In
`scripts/smoke-windows.ps1`, replace (lines 88-91):

```
$arpRoots = @(
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
```

with:

```
$arpRoots = @(
    # Per-user MSI registers ARP under HKCU. HKLM roots kept as a fallback so
    # this script still detects a legacy per-machine install during migration.
    'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
)
```

- [ ] **Step 4: Move the version-stamp check to HKCU**

In `scripts/smoke-windows.ps1`, replace (line 107):

```
$stampKey = 'HKLM:\SOFTWARE\Winpepper'
```

with:

```
$stampKey = 'HKCU:\SOFTWARE\Winpepper'
```

Then replace the pass/fail labels (lines 110 and 112). Replace:

```
    Add-Result 'HklmVersionStamp' 'PASS' ("InstallVersion {0}, InstallDir {1}" -f $stamp.InstallVersion, $stamp.InstallDir)
```

with:

```
    Add-Result 'HkcuVersionStamp' 'PASS' ("InstallVersion {0}, InstallDir {1}" -f $stamp.InstallVersion, $stamp.InstallDir)
```

and replace:

```
    Add-Result 'HklmVersionStamp' 'FAIL' "missing $stampKey"
```

with:

```
    Add-Result 'HkcuVersionStamp' 'FAIL' "missing $stampKey"
```

- [ ] **Step 5: Update the header comments (lines 9 and 11)**

In `scripts/smoke-windows.ps1`, replace (line 9):

```
      * install payload under Program Files
```

with:

```
      * install payload under %LOCALAPPDATA%\Programs\Winpepper
```

and replace (line 11):

```
      * HKLM Software\Winpepper version stamp
```

with:

```
      * HKCU Software\Winpepper version stamp
```

- [ ] **Step 6: Verify no per-machine assumptions remain in the script**

Run: `grep -n "ProgramFiles\|HKLM:\\\\SOFTWARE\\\\Winpepper\|HklmVersionStamp" scripts/smoke-windows.ps1; echo "exit=$?"`
Expected: exit=1 (none of: `ProgramFiles`, the HKLM `Winpepper` stamp key, or the old label remain).

- [ ] **Step 7: Commit**

```bash
git add scripts/smoke-windows.ps1
git commit -m "test: point Windows smoke script at per-user install location

InstallDir default -> %LOCALAPPDATA%\Programs\Winpepper; version stamp and
ARP lookup -> HKCU (with HKLM ARP fallback for legacy-install migration).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 3: Update the app autostart path and its test

**Files:**
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs` (lines 71-78)
- Modify: `tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs` (lines 20, 22)

**Interfaces:**
- Consumes: the per-user install dir from Task 1.
- Produces: the Settings autostart toggle now writes a Run-key value pointing at `%LOCALAPPDATA%\Programs\Winpepper\winpepper.exe` (matching where the MSI installs), instead of the dead `C:\Program Files\Winpepper` path.

**Why this file is in scope:** the MSI Run-key component uses `[INSTALLFOLDER]`
(auto-correct), but the in-app Settings toggle hard-codes the old per-machine
path. After the scope move, toggling autostart off then on in Settings would
write a path that does not exist. This is a real regression caused by the scope
change, so it must change too. (No `dotnet` on this host → static verification
only; the app compiles on the Windows host. The replacement uses only
`System.Environment`, already in scope, so no new `using` is required.)

- [ ] **Step 1: Replace the hard-coded per-machine autostart path**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, replace the block
(lines 71-78):

```
                // Spec §7.7 mandates the literal value
                //   "C:\Program Files\Winpepper\winpepper.exe" --tray
                // because the MSI installs to Program Files. AppContext.BaseDirectory
                // is correct only when running from the install location; in dev / on
                // the VM you can override via the WINPEPPER_AUTOSTART_EXE env var.
                var exe = Environment.GetEnvironmentVariable("WINPEPPER_AUTOSTART_EXE");
                if (string.IsNullOrEmpty(exe))
                    exe = @"C:\Program Files\Winpepper\winpepper.exe";
```

with:

```
                // Spec §7.7: the Run-key value points at the installed exe. The
                // MSI is a per-user install under %LOCALAPPDATA%\Programs\Winpepper,
                // so build that path from LocalApplicationData. In dev / on the VM
                // you can override via the WINPEPPER_AUTOSTART_EXE env var.
                var exe = Environment.GetEnvironmentVariable("WINPEPPER_AUTOSTART_EXE");
                if (string.IsNullOrEmpty(exe))
                    exe = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                          + @"\Programs\Winpepper\winpepper.exe";
```

- [ ] **Step 2: Verify the per-machine literal is gone from the app**

Run: `grep -n "Program Files" src/Winpepper.App/Views/RecordingPage.xaml.cs; echo "exit=$?"`
Expected: exit=1 (no `Program Files` reference left).

Run: `grep -n "SpecialFolder.LocalApplicationData" src/Winpepper.App/Views/RecordingPage.xaml.cs; echo "exit=$?"`
Expected: exit=0 (the new path source is present).

- [ ] **Step 3: Update the autostart quoting test's illustrative path**

`InMemoryAutostartRegistryTests` asserts the composed Run-key command string.
Its path is illustrative; update it so no per-machine assumption lingers in the
test suite. In `tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs`,
replace the two lines (20 and 22):

```
        r.Enable(@"C:\Program Files\Winpepper\winpepper.exe", "--tray");
        r.IsEnabled().ShouldBeTrue();
        r.CurrentCommand().ShouldBe("\"C:\\Program Files\\Winpepper\\winpepper.exe\" --tray");
```

with:

```
        r.Enable(@"C:\Users\me\AppData\Local\Programs\Winpepper\winpepper.exe", "--tray");
        r.IsEnabled().ShouldBeTrue();
        r.CurrentCommand().ShouldBe("\"C:\\Users\\me\\AppData\\Local\\Programs\\Winpepper\\winpepper.exe\" --tray");
```

- [ ] **Step 4: Verify the test no longer encodes a per-machine path**

Run: `grep -n "Program Files" tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs; echo "exit=$?"`
Expected: exit=1 (gone).

> Note: this test runs under `dotnet test --filter "Platform!=Windows"` on the
> Windows host / CI (no `dotnet` on this Linux host). The edit preserves the
> test's intent — it still asserts the exact quoting of `"<exe>" --tray` — so
> the RED/GREEN behavior is unchanged; only the literal path moved.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/RecordingPage.xaml.cs tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs
git commit -m "fix: write per-user autostart path from Settings toggle

The MSI Run key auto-resolves via [INSTALLFOLDER], but the in-app autostart
toggle hard-coded C:\Program Files\Winpepper. Build the path from
LocalApplicationData\Programs\Winpepper to match the per-user install.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 4: Update the design spec (§7.7 and §11)

**Files:**
- Modify: `docs/superpowers/specs/2026-05-15-winpepper-design.md` (lines 318, 422, 428)

**Interfaces:**
- Consumes: the per-user decision from Task 1.
- Produces: the authoritative design doc now describes a per-user install, records the rationale, and states the upgrade/migration implication (spec requirement #5). The wxs comment "Spec §11: AllowDowngrades=no" (unchanged) stays consistent with this spec.

- [ ] **Step 1: Update §7.7 autostart path**

In `docs/superpowers/specs/2026-05-15-winpepper-design.md`, replace (line 318):

```
Stored at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"C:\Program Files\Winpepper\winpepper.exe" --tray`. `--tray` starts hidden. MSI sets this on first install only; toggling autostart in Settings writes/deletes the value directly thereafter.
```

with:

```
Stored at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"%LOCALAPPDATA%\Programs\Winpepper\winpepper.exe" --tray`. `--tray` starts hidden. MSI sets this on first install only (via `[INSTALLFOLDER]`); toggling autostart in Settings writes/deletes the value directly thereafter.
```

- [ ] **Step 2: Update §11 install location + rationale**

In `docs/superpowers/specs/2026-05-15-winpepper-design.md`, replace (line 422):

```
- App binaries → `C:\Program Files\Winpepper\` (per-machine; requires elevation).
```

with:

```
- App binaries → `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; **no elevation / UAC required**). WiX `Package/@Scope="perUser"`; install tree under `LocalAppDataFolder\Programs\Winpepper` (the VS Code / Squirrel convention). Rationale: Winpepper is a single-user desktop app, and a per-machine install forced a UAC prompt on every dev-loop build-install — per-user removes that friction with no functional loss, since all user data already lives in `%LOCALAPPDATA%`.
```

- [ ] **Step 3: Update §11 upgrade rules with the per-user migration note**

In `docs/superpowers/specs/2026-05-15-winpepper-design.md`, replace (line 428):

```
Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.
```

with:

```
Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. A per-user `MajorUpgrade` only detects prior **per-user** installs of the same `UpgradeCode` — the intended end state. Migration: anyone with a pre-existing per-machine install (`C:\Program Files\Winpepper`) must uninstall it once (that removal needs elevation) before the per-user package will manage upgrades. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.
```

- [ ] **Step 4: Verify the spec no longer mandates per-machine**

Run: `grep -n "Program Files\\\\Winpepper\|per-machine" docs/superpowers/specs/2026-05-15-winpepper-design.md; echo "exit=$?"`
Expected: exit=1 (no per-machine install mandate remains in the spec).

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-05-15-winpepper-design.md
git commit -m "docs: update design spec for per-user MSI scope

§7.7 autostart path and §11 packaging: per-user install under
%LOCALAPPDATA%\Programs\Winpepper, rationale, and per-machine migration note.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 5: Update README install section

**Files:**
- Modify: `README.md` (lines 74-82)

**Interfaces:**
- Consumes: the per-user decision from Task 1.
- Produces: end-user install docs describing the per-user location, the absence of any elevation prompt, and the one-time migration step for existing per-machine installs.

- [ ] **Step 1: Rewrite the "After install" bullets + add no-elevation and migration notes**

In `README.md`, replace the block (lines 74-82):

```
After install:
- Files land in `C:\Program Files\Winpepper\`
- User data (settings, corrections, downloaded models, audio history) lives in
  `%LOCALAPPDATA%\winpepper\` — survives reinstalls and uninstalls
- Autostart is enabled: `HKCU\…\Run\Winpepper` runs the app hidden in the tray on
  logon

To uninstall: standard Add/Remove Programs entry. User data is preserved; delete
`%LOCALAPPDATA%\winpepper\` yourself if you want a fully clean slate.
```

with:

```
Winpepper installs **per-user** — no administrator rights and **no UAC prompt**
for install, upgrade, or uninstall.

After install:
- Files land in `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; not `Program Files`)
- User data (settings, corrections, downloaded models, audio history) lives in
  `%LOCALAPPDATA%\winpepper\` — a separate folder that survives reinstalls and
  uninstalls
- Autostart is enabled: `HKCU\…\Run\Winpepper` runs the app hidden in the tray on
  logon

To uninstall: standard Add/Remove Programs entry (no elevation needed). User data
is preserved; delete `%LOCALAPPDATA%\winpepper\` yourself if you want a fully
clean slate.

> **Migrating from an older per-machine build?** Earlier releases installed to
> `C:\Program Files\Winpepper` (per-machine). Uninstall that one first — that one
> removal still needs elevation — before installing this per-user package, so
> upgrades track correctly afterward.
```

- [ ] **Step 2: Verify the README no longer claims a Program Files install**

Run: `grep -n "Files land in" README.md; echo "exit=$?"`
Expected: exit=0, and the matched line reads `%LOCALAPPDATA%\Programs\Winpepper\`.

Run: `grep -n "Program Files\\\\Winpepper" README.md; echo "exit=$?"`
Expected: exit=0 — the ONLY remaining match is the migration note ("installed to `C:\Program Files\Winpepper`"). Confirm it is that line and not an active install instruction.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs: describe per-user install location in README

Install/upgrade/uninstall need no elevation; files under
%LOCALAPPDATA%\Programs\Winpepper; add per-machine migration note.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 6: Update the remaining Windows test/release docs

**Files:**
- Modify: `docs/windows-smoke-test.md` (lines 41-46, 88-92)
- Modify: `docs/manual-test.md` (lines 281, 293, 300)
- Modify: `docs/release.md` (line 44)

**Interfaces:**
- Consumes: the per-user decision from Task 1.
- Produces: the manual/smoke/release runbooks reference the per-user install path and HKCU version stamp, keeping them executable on the Windows host.

- [ ] **Step 1: windows-smoke-test.md — machine-state paragraph (lines 41-46)**

Replace:

```
Plus machine state written by the MSI: payload under
`C:\Program Files\Winpepper`, the ARP uninstall entry, the
`HKLM\Software\Winpepper` version stamp, and the per-user autostart value
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper`
```

with:

```
Plus per-user state written by the MSI: payload under
`%LOCALAPPDATA%\Programs\Winpepper`, the (per-user, HKCU) ARP uninstall entry, the
`HKCU\Software\Winpepper` version stamp, and the autostart value
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper`
```

- [ ] **Step 2: windows-smoke-test.md — checks list (lines 88-92)**

Replace:

```
- `InstallPayload` / `InstallAssets` — `Winpepper.exe` and `Assets\AppIcon.ico`
  under `C:\Program Files\Winpepper`.
- `ArpEntry` — Add/Remove Programs entry named `Winpepper`.
- `HklmVersionStamp` — `HKLM\Software\Winpepper` `InstallVersion`/`InstallDir`.
```

with:

```
- `InstallPayload` / `InstallAssets` — `Winpepper.exe` and `Assets\AppIcon.ico`
  under `%LOCALAPPDATA%\Programs\Winpepper`.
- `ArpEntry` — Add/Remove Programs entry named `Winpepper` (per-user, under HKCU).
- `HkcuVersionStamp` — `HKCU\Software\Winpepper` `InstallVersion`/`InstallDir`.
```

- [ ] **Step 3: manual-test.md — selftest path (line 281)**

Replace:

```
   ./scripts/winrun "& 'C:\\Program Files\\Winpepper\\Winpepper.exe' --selftest"
```

with:

```
   ./scripts/winrun "& \"$env:LOCALAPPDATA\\Programs\\Winpepper\\Winpepper.exe\" --selftest"
```

- [ ] **Step 4: manual-test.md — uninstall expectation (lines 293-294 and 300)**

Replace:

```
   Expected: exit code `0`; `C:\Program Files\Winpepper` is gone; the
   `HKCU\...\Run\Winpepper` value is gone.
```

with:

```
   Expected: exit code `0`; `%LOCALAPPDATA%\Programs\Winpepper` is gone; the
   `HKCU\...\Run\Winpepper` value is gone.
```

Then replace (line 300):

```
   preserved on uninstall — only `%ProgramFiles%\Winpepper` is removed).
```

with:

```
   preserved on uninstall — only `%LOCALAPPDATA%\Programs\Winpepper` is removed).
```

- [ ] **Step 5: release.md — signtool verify path (line 44)**

Replace:

```
signtool verify /pa /v "C:\Program Files\Winpepper\Winpepper.exe"
```

with:

```
signtool verify /pa /v "$env:LOCALAPPDATA\Programs\Winpepper\Winpepper.exe"
```

- [ ] **Step 6: Verify these three docs no longer point install references at Program Files**

Run:
```bash
grep -n "Program Files\\\\Winpepper\|ProgramFiles%\\\\Winpepper\|HklmVersionStamp\|HKLM\\\\Software\\\\Winpepper" docs/windows-smoke-test.md docs/manual-test.md docs/release.md; echo "exit=$?"
```
Expected: exit=1 (no per-machine install path, old label, or HKLM stamp reference remains in these three runbooks).

- [ ] **Step 7: Commit**

```bash
git add docs/windows-smoke-test.md docs/manual-test.md docs/release.md
git commit -m "docs: update Windows runbooks for per-user install path

Smoke/manual/release docs reference %LOCALAPPDATA%\Programs\Winpepper and the
HKCU version stamp instead of Program Files / HKLM.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 7: Whole-repo audit gate (no-change files + final sweep)

**Files:**
- No file modifications. This task is verification only: it audits the files the
  spec named but that need no change, and runs the system-wide gate proving the
  sweep is complete.

**Interfaces:**
- Consumes: all edits from Tasks 1-6.
- Produces: evidence that (a) `packaging/probes/*`, `packaging/sign.ps1`, and
  `scripts/provision-vm.ps1` legitimately need no change, and (b) no in-scope
  tracked file still hard-codes the per-machine install path.

**Audit conclusions (spec requirement #4, audit-only targets):**

| File | Reference found | Why no change |
|---|---|---|
| `packaging/probes/Program.cs` | reads HKLM `CurrentBuildNumber` + `WindowsAppRuntime\Installed\1.6`; writes `%TEMP%\winpepper-probe.txt` | HKLM **reads** need no elevation; no install-path or scope assumption |
| `packaging/sign.ps1` | `${env:ProgramFiles(x86)}\Windows Kits\...\signtool.exe` | Windows SDK toolchain path, not Winpepper's install scope |
| `scripts/provision-vm.ps1` | `C:\Program Files\dotnet`, `Get-AppxPackage -AllUsers ...WindowsAppRuntime` | .NET SDK install path + machine-wide runtime probe; unrelated to Winpepper's MSI scope |
| `docs/superpowers/plans/*.md` | multiple `C:\Program Files\Winpepper` | historical point-in-time build logs; not living docs (Global Constraints) |

- [ ] **Step 1: Confirm the audit-only files contain only out-of-scope references**

Run:
```bash
grep -n "Program Files\\\\Winpepper" packaging/probes/Program.cs packaging/sign.ps1 scripts/provision-vm.ps1; echo "exit=$?"
```
Expected: exit=1 (none of these files reference the Winpepper install path; their `Program Files` uses are dotnet/SDK paths, a different string).

- [ ] **Step 2: Re-run the wxs static validator (regression gate)**

Run: `python3 packaging/validate-wxs.py; echo "exit=$?"`
Expected: exit=0, `PASS: winpepper.wxs passes all per-user static checks`.

Run: `xmllint --noout packaging/winpepper.wxs; echo "exit=$?"`
Expected: exit=0.

- [ ] **Step 3: System-wide sweep — no in-scope file hard-codes the per-machine install path**

Run:
```bash
grep -rIn 'Program Files\\Winpepper\|ProgramFiles%\\Winpepper' \
  --include='*.wxs' --include='*.ps1' --include='*.cs' --include='*.md' . \
  | grep -v 'docs/superpowers/plans/' \
  | grep -v 'docs/plans/2026-07-20-per-user-msi-scope.md' \
  | grep -v 'README.md.*[Mm]igrat' ; echo "grep-exit=$?"
```
Expected: prints only the README **migration note** line (the single intentional
mention of the old path) and nothing else. Concretely: 0 lines from `.wxs`,
`.ps1`, `.cs`; 0 lines from `docs/windows-smoke-test.md`, `docs/manual-test.md`,
`docs/release.md`, `docs/superpowers/specs/`. If any of those appear, the sweep
is incomplete — fix the offending file before committing.

> The `grep -v` filters exclude the three legitimate homes for the string: the
> historical build logs under `docs/superpowers/plans/`, this plan document, and
> the README migration note. Everything else must be clean.

- [ ] **Step 4: Confirm no orphaned old identifiers survive anywhere**

Run:
```bash
grep -rIn 'HKLMVersionStamp\|HklmVersionStamp' \
  --include='*.wxs' --include='*.ps1' --include='*.md' . \
  | grep -v 'docs/superpowers/plans/' \
  | grep -v 'docs/plans/2026-07-20-per-user-msi-scope.md' ; echo "grep-exit=$?"
```
Expected: 0 lines (grep-exit=1) — the renamed component/label leaves no live
reference to the old `HKLM*VersionStamp` id outside historical logs and this plan.

- [ ] **Step 5: Commit (audit record — allow empty, since this task changes no files)**

```bash
git commit --allow-empty -m "chore: record per-user scope audit gate results

Audited probes/sign.ps1/provision-vm.ps1 (no change needed) and ran the
whole-repo sweep + wxs validator confirming no per-machine install-path
assumptions remain outside historical build logs.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Post-plan: verification that happens on the Windows host (out of this plan's reach)

This host cannot build the MSI or run the managed test suite. The caller will,
on the Windows host immediately after this plan:

1. `dotnet build packaging/Winpepper.Msi.wixproj -c Release -p:UseXamlCompilerExecutable=true` — must produce `artifacts/winpepper-<version>-x64.msi` with **no ICE errors** (the wxs is consistent per Task 1's static gate).
2. `msiexec /i artifacts\winpepper-<version>-x64.msi /qn` from a **non-elevated** prompt — must succeed with exit 0 and **no UAC prompt**, installing to `%LOCALAPPDATA%\Programs\Winpepper`.
3. `dotnet test --filter "Platform!=Windows"` — the autostart quoting test (Task 3) must stay green.
4. `pwsh -File scripts\smoke-windows.ps1 -RunSelftest` — all automated checks PASS against the per-user location and HKCU stamp.
5. `msiexec /x ... /qn` from a non-elevated prompt — uninstalls cleanly, leaving `%LOCALAPPDATA%\winpepper` intact.

These are the end-user acceptance outcomes; they are listed here so the Windows
operator knows exactly what "done" looks like. They are NOT deferred plan work —
they are the un-simulatable half of verification that is physically impossible on
Linux, and the static validator + grep gates in Tasks 1-7 are the strongest proof
obtainable here.

---

## Self-Review

**1. Spec coverage.** All five numbered requirements map to tasks (see Spec
Coverage Map). #1 scope+dir → Task 1; #2 HKLM→HKCU stamp → Task 1; #3 audit of
launch condition / probe / shortcut / gated CA / SummaryInfo → Task 1 audit
table + validator; #4 sweep of smoke script (Task 2), app+tests (Task 3), docs
(Tasks 5, 6), audit-only files (Task 7); #5 spec §7.7 + §11 + rationale +
migration note → Task 4. Static-verifiability constraint → validator (Task 1) +
grep gates (all tasks) + Task 7 system sweep.

**1b. No silent deferrals.** The only behavior that cannot be proven on this
Linux host is the actual MSI build + elevation-free install (no WiX, no
Windows Installer, no `dotnet`, no `pwsh` — confirmed by environment probe).
This is not a stub or a deferred requirement: it is physically un-runnable here,
and the task itself states that verification happens on the Windows host next.
The strongest obtainable local proof is provided (XML well-formedness via
`xmllint`, structural invariants via `validate-wxs.py`, exhaustive `grep`
gates). No requirement is parked in "known limitations." The one file that
cannot be compiled here (`RecordingPage.xaml.cs`, Task 3) uses only
already-imported `System.Environment` APIs and is verified statically; its
compile happens on the Windows host.

**2. Placeholder scan.** No `TBD`/`TODO`/"add error handling"/"similar to Task
N" placeholders. Every code and edit step shows exact before/after text and
exact commands with expected exit codes.

**3. Type/identifier consistency.** The renamed component id `HKCUVersionStamp`
is defined (Task 1 Step 6) and referenced (Task 1 Step 4) consistently; the old
`HKLMVersionStamp` is asserted gone (Task 1 Step 9, Task 7 Step 4). The smoke
label `HkcuVersionStamp` is used consistently in both PASS and FAIL branches
(Task 2 Step 4). The install path `%LOCALAPPDATA%\Programs\Winpepper` and the
WiX ids `LocalAppDataFolder` / `ProgramsFolder` / `INSTALLFOLDER` are identical
across the wxs (Task 1), validator well-known set, smoke script (Task 2), app
(Task 3), spec (Task 4), README (Task 5), and runbooks (Task 6). The data dir
`%LOCALAPPDATA%\winpepper` is never changed and never conflated with the install
dir.
