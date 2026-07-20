# Task 4: Update the Design Spec — Completion Report

## Status
**DONE** — All edits applied, verification passed, committed.

---

## Changes Applied

### Step 1: §7.7 Autostart Path
**Line 318** — Updated autostart registry path from per-machine to per-user location.

**Before:**
```
Stored at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"C:\Program Files\Winpepper\winpepper.exe" --tray`. `--tray` starts hidden. MSI sets this on first install only; toggling autostart in Settings writes/deletes the value directly thereafter.
```

**After:**
```
Stored at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"%LOCALAPPDATA%\Programs\Winpepper\winpepper.exe" --tray`. `--tray` starts hidden. MSI sets this on first install only (via `[INSTALLFOLDER]`); toggling autostart in Settings writes/deletes the value directly thereafter.
```

**Rationale:** Changes the hardcoded path reference to use `%LOCALAPPDATA%\Programs\Winpepper\` (the per-user install location) and adds clarification that the MSI sets this via WiX's `[INSTALLFOLDER]` token, ensuring the path is correct regardless of locale or user-specific paths.

---

### Step 2: §11 Install Location + Rationale
**Line 422** — Updated install location from per-machine to per-user with full rationale.

**Before:**
```
- App binaries → `C:\Program Files\Winpepper\` (per-machine; requires elevation).
```

**After:**
```
- App binaries → `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; **no elevation / UAC required**). WiX `Package/@Scope="perUser"`; install tree under `LocalAppDataFolder\Programs\Winpepper` (the VS Code / Squirrel convention). Rationale: Winpepper is a single-user desktop app, and a per-machine install forced a UAC prompt on every dev-loop build-install — per-user removes that friction with no functional loss, since all user data already lives in `%LOCALAPPDATA%`.
```

**Rationale:** Documents the per-user scope, explains the WiX configuration (`Package/@Scope="perUser"` + install tree convention), and provides the explicit rationale: no UAC friction for dev builds, no functional loss, and alignment with existing user-data layout.

---

### Step 3: §11 Upgrade Rules + Migration Note
**Line 428** — Added migration guidance for users with pre-existing per-machine installs.

**Before:**
```
Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.
```

**After:**
```
Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. A per-user `MajorUpgrade` only detects prior **per-user** installs of the same `UpgradeCode` — the intended end state. Migration: anyone with a pre-existing per-machine install (`C:\Program Files\Winpepper`) must uninstall it once (that removal needs elevation) before the per-user package will manage upgrades. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.
```

**Rationale:** Clarifies the per-user upgrade scope semantics and provides explicit migration guidance for users who had the old per-machine install. The migration path is clear: one-time manual uninstall of the old per-machine version, then the per-user package takes over upgrade management.

---

## Verification: Step 4 Grep Output

```bash
grep -n "Program Files\\Winpepper\|per-machine" docs/superpowers/specs/2026-05-15-winpepper-design.md
```

**Output:**
```
422:- App binaries → `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; **no elevation / UAC required**). WiX `Package/@Scope="perUser"`; install tree under `LocalAppDataFolder\Programs\Winpepper` (the VS Code / Squirrel convention). Rationale: Winpepper is a single-user desktop app, and a per-machine install forced a UAC prompt on every dev-loop build-install — per-user removes that friction with no functional loss, since all user data already lives in `%LOCALAPPDATA%`.
428:Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. A per-user `MajorUpgrade` only detects prior **per-user** installs of the same `UpgradeCode` — the intended end state. Migration: anyone with a pre-existing per-machine install (`C:\Program Files\Winpepper`) must uninstall it once (that removal needs elevation) before the per-user package will manage upgrades. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.
exit=0
```

### Line-by-Line Judgment

- **Line 422:** Contains `%LOCALAPPDATA%\Programs\Winpepper\` (correct per-user path) and the word "per-user" (correct designation). ✓ INTENTIONAL: This line now correctly states the per-user scope.

- **Line 428:** Contains the phrase "pre-existing per-machine install (`C:\Program Files\Winpepper`)" — this is the **deliberate, one-time migration note added in Step 3**. ✓ INTENTIONAL: This line documents the migration path for users with the old per-machine install.

**Verification Result:** ✅ **PASS** — The ONLY remaining matches for "Program Files\Winpepper" and "per-machine" in the spec are:
1. Line 422: The new per-user reference in the descriptive text (correct).
2. Line 428: The intentional migration note describing the one-time upgrade path for legacy per-machine installs.

The spec no longer mandates or describes per-machine as an active install mode. All references to per-machine are either:
- Historical (the migration note describing what users need to uninstall once)
- Or corrected to the new per-user path (`%LOCALAPPDATA%\Programs\Winpepper\`)

---

## Commit Details

**Commit SHA:** `b0f5b92c85efdc1bd903819ca9a5cd6708a8a9c8`  
**Branch:** `per-user-msi-scope`  
**Files Modified:** `docs/superpowers/specs/2026-05-15-winpepper-design.md` (+3 insertions, -3 deletions)

**Commit Message:**
```
docs: update design spec for per-user MSI scope

§7.7 autostart path and §11 packaging: per-user install under
%LOCALAPPDATA%\Programs\Winpepper, rationale, and per-machine migration note.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
```

---

## Self-Review Findings

### Completeness Checklist

- [x] **§7.7 autostart path updated** to `%LOCALAPPDATA%\Programs\Winpepper\winpepper.exe`
- [x] **§11 install location updated** to `%LOCALAPPDATA%\Programs\Winpepper\` with rationale
- [x] **§11 upgrade/migration note added** describing per-machine→per-user migration requirement
- [x] **All edits match the brief exactly** (before/after text blocks)
- [x] **Verification grep command passed** — only intentional mentions remain
- [x] **Single commit** with exact message from brief

### Integrity Checks

- [x] No unrelated sections restructured or reworded
- [x] No edits to `docs/superpowers/plans/` (historical build logs)
- [x] `README.md` remains untouched (end-user doc)
- [x] diff is minimal and focused (3 lines changed)
- [x] Spec is still valid Markdown and renders correctly

### Verification Integrity

- [x] Grep returned exit code 0 (matches found)
- [x] Only two lines matched (422 and 428)
- [x] Line 422: Contains the new per-user text (correct)
- [x] Line 428: Contains the intentional migration note (correct)
- [x] No OTHER lines contain "Program Files\Winpepper" or "per-machine"
- [x] Sweep is complete — spec no longer mandates per-machine

---

## Concerns

**None.** All edits applied cleanly, verification passed, commit created successfully. The spec now accurately reflects the per-user MSI scope decision and provides clear migration guidance for existing per-machine users.

---

## Files Changed

- `docs/superpowers/specs/2026-05-15-winpepper-design.md`
  - Line 318: §7.7 autostart path (per-machine → per-user)
  - Line 422: §11 install location + rationale (per-machine → per-user + explanation)
  - Line 428: §11 upgrade rules + migration note (added per-user scope semantics + one-time migration path)

---

## Summary

Task 4 complete. The design spec `docs/superpowers/specs/2026-05-15-winpepper-design.md` now authoritatively documents the per-user MSI scope:

1. **§7.7** reflects the per-user autostart registry path using `%LOCALAPPDATA%\Programs\Winpepper\`.
2. **§11** documents the per-user install location, explains the rationale (no UAC prompt, dev-loop friction removed), and references the WiX config.
3. **§11 upgrade section** adds semantics of per-user `MajorUpgrade` and a clear one-time migration path for legacy per-machine installs.

The verification grep confirms the sweep is complete: the ONLY remaining references to "Program Files\Winpepper" or "per-machine" are the intentional migration note (describing the legacy per-machine install that users must manually uninstall once). All active descriptions now reference the per-user scope.

Commit `b0f5b92` ready to integrate into the per-user-msi-scope branch.
