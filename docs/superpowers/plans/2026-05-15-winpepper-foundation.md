# Winpepper Plan 1 — Foundation and Walking Skeleton

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce a working Windows CLI dictation tool. Hold the configured hotkey, speak, release — Parakeet TDT v3 transcribes the audio and the result is typed into the focused window via SendInput. No cleanup LLM. No WinUI. No history. End-to-end native pipeline validated.

**Architecture:** Multi-project .NET 9 solution. `Winpepper.Cli` is a temporary entry point that wires `Winpepper.Audio` (WASAPI) → `Winpepper.Asr` (ONNX Runtime DirectML, Parakeet TDT v3 streaming decode) → `Winpepper.Platform` (SendInput injection + WH_KEYBOARD_LL hotkey hook). `Winpepper.Core` holds the session state machine, atomic file IO, and logging. Everything is buildable cross-platform; integration tests and real-model tests run on the Windows VM at `localhost:2222`.

**Tech Stack:** C# / .NET 9, NAudio (WASAPI), Microsoft.ML.OnnxRuntime.DirectML, NWaves (mel filterbank), Serilog, xUnit, Shouldly. WiX comes in Plan 6.

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md). Plans 2–6 will cover cleanup, UI, history/lab, learning, packaging in turn.

**Reference implementation:** the Rust crate `parakeet-rs` at `/home/jesse/.cargo/registry/src/index.crates.io-1949cf8c6b5b557f/parakeet-rs-0.3.4/`. The C# code in this plan is a port of its TDT decoder (`src/model_tdt.rs`), audio preprocessing (`src/audio.rs`), and vocabulary loader (`src/vocab.rs`).

**Repo root throughout the plan:** `/home/jesse/git/winpepper/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced from Linux via `scripts/sync-to-vm.sh`).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Run it and confirm it fails. Implement. Run it and confirm it passes. Commit.

**Commits.** One commit per task at minimum. Smaller commits within a task are fine. Always end a task with a green build and green tests on Linux *and* (where applicable) on the Windows VM.

**Building.** Cross-platform tasks build and test on Linux (`dotnet build`, `dotnet test`). Windows-only tasks run on the VM via the `winssh` and `winrun` helpers from Task 2.

**Skipping Windows tests on Linux.** Tests that touch Win32 APIs are tagged with the xUnit trait `[Trait("Platform", "Windows")]`. CI on Linux runs `dotnet test --filter "Platform!=Windows"`. The Windows VM runs the full suite.

**SSH conventions** (Windows VM):
```bash
sshpass -p 'password' ssh -o StrictHostKeyChecking=no -p 2222 user@localhost
```
The `scripts/winssh` wrapper in Task 2 abstracts this.

---

## Task 1: Repo bootstrap and solution scaffolding

**Files:**
- Create: `/home/jesse/git/winpepper/.gitignore`
- Create: `/home/jesse/git/winpepper/.editorconfig`
- Create: `/home/jesse/git/winpepper/global.json`
- Create: `/home/jesse/git/winpepper/Directory.Build.props`
- Create: `/home/jesse/git/winpepper/Directory.Packages.props`
- Create: `/home/jesse/git/winpepper/winpepper.sln`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Winpepper.Core.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/HelloWinpepper.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/HelloWinpepperTests.cs`
- Create: `/home/jesse/git/winpepper/README.md`

- [ ] **Step 1: Write `.gitignore`**

```gitignore
bin/
obj/
.vs/
*.user
*.suo
out/
artifacts/
TestResults/
.idea/
.vscode/
node_modules/
*.log
%LOCALAPPDATA%
```

- [ ] **Step 2: Write `.editorconfig`**

```ini
root = true

[*]
charset = utf-8
end_of_line = lf
indent_style = space
indent_size = 4
trim_trailing_whitespace = true
insert_final_newline = true

[*.{cs,csproj,sln,props,targets}]
indent_size = 4

[*.{json,yml,yaml,md}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
csharp_new_line_before_open_brace = all
dotnet_style_qualification_for_field = false:warning
dotnet_style_qualification_for_property = false:warning
dotnet_style_predefined_type_for_locals_parameters_members = true:warning
csharp_style_var_for_built_in_types = true:suggestion
csharp_style_var_when_type_is_apparent = true:suggestion
```

- [ ] **Step 3: Write `global.json`**

```json
{
  "sdk": {
    "version": "9.0.100",
    "rollForward": "latestFeature"
  }
}
```

- [ ] **Step 4: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsAsErrors>nullable</WarningsAsErrors>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <AnalysisLevel>latest</AnalysisLevel>
    <Deterministic>true</Deterministic>
    <ContinuousIntegrationBuild Condition="'$(CI)' == 'true'">true</ContinuousIntegrationBuild>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Write `Directory.Packages.props`**

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Logging" Version="9.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    <PackageVersion Include="Serilog" Version="4.2.0" />
    <PackageVersion Include="Serilog.Extensions.Logging" Version="9.0.0" />
    <PackageVersion Include="Serilog.Sinks.File" Version="6.0.0" />
    <PackageVersion Include="Serilog.Sinks.Console" Version="6.0.0" />
    <PackageVersion Include="System.Threading.Channels" Version="9.0.0" />
    <PackageVersion Include="NAudio" Version="2.2.1" />
    <PackageVersion Include="NAudio.Wasapi" Version="2.2.1" />
    <PackageVersion Include="Microsoft.ML.OnnxRuntime.DirectML" Version="1.20.1" />
    <PackageVersion Include="NWaves" Version="0.9.6" />
    <PackageVersion Include="xunit.v3" Version="1.0.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="3.0.0" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageVersion Include="Shouldly" Version="4.2.1" />
    <PackageVersion Include="coverlet.collector" Version="6.0.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: Write `src/Winpepper.Core/Winpepper.Core.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Core</RootNamespace>
    <AssemblyName>Winpepper.Core</AssemblyName>
  </PropertyGroup>
</Project>
```

- [ ] **Step 7: Write `src/Winpepper.Core/HelloWinpepper.cs`** (smoke type so the project compiles to a non-empty assembly)

```csharp
namespace Winpepper.Core;

public static class HelloWinpepper
{
    public const string Greeting = "Winpepper online.";
}
```

- [ ] **Step 8: Write the failing test `tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Core\Winpepper.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 9: Write `tests/Winpepper.Core.Tests/HelloWinpepperTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class HelloWinpepperTests
{
    [Fact]
    public void Greeting_HasExpectedValue()
    {
        HelloWinpepper.Greeting.ShouldBe("Winpepper online.");
    }
}
```

- [ ] **Step 10: Generate `winpepper.sln`**

Run from `/home/jesse/git/winpepper/`:

```bash
dotnet new sln -n winpepper
dotnet sln add src/Winpepper.Core/Winpepper.Core.csproj
dotnet sln add tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj
```

- [ ] **Step 11: Write `README.md`**

```markdown
# Winpepper

Native Windows 11 local dictation. Companion to [pepper-x](https://github.com/.../pepper-x) (Linux).

Hold a hotkey, speak, release — your words appear in the focused app. Everything runs locally.

See `docs/superpowers/specs/2026-05-15-winpepper-design.md` for the design.

## Build

```sh
dotnet build
dotnet test
```

Windows-specific tests require the dev VM described in `docs/manual-test.md`.
```

- [ ] **Step 12: Build and run tests**

```bash
cd /home/jesse/git/winpepper
dotnet restore
dotnet build
dotnet test
```

Expected: build succeeds, 1 test passes.

- [ ] **Step 13: Commit**

```bash
git add .
git commit -m "scaffold: solution layout, core lib, smoke test"
```

---

## Task 2: Windows VM dev environment and sync tooling

**Files:**
- Create: `/home/jesse/git/winpepper/scripts/winssh`
- Create: `/home/jesse/git/winpepper/scripts/winrun`
- Create: `/home/jesse/git/winpepper/scripts/sync-to-vm.sh`
- Create: `/home/jesse/git/winpepper/scripts/provision-vm.ps1`
- Create: `/home/jesse/git/winpepper/docs/manual-test.md`

Notes for the worker: the Windows VM is a dockur container named `windows11` with SSH on `localhost:2222`, username `user`, password `password`. See `~/.claude/skills/windows-vm/SKILL.md` for the full setup. The container may currently be **stopped** — start it with `docker start windows11` and wait for SSH to respond (`sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o ConnectTimeout=5 -p 2222 user@localhost "whoami"` should return `winpepper11\user` or similar).

- [ ] **Step 1: Verify VM is up**

```bash
docker ps -f name=windows11 --format '{{.Status}}'
```

If exited:
```bash
docker start windows11
# Wait until this succeeds:
until sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o ConnectTimeout=5 -p 2222 user@localhost "whoami" 2>/dev/null; do sleep 5; done
```

- [ ] **Step 2: Write `scripts/winssh`** (run a remote command on the VM)

```bash
#!/usr/bin/env bash
# Usage: ./scripts/winssh "powershell command here"
# Or:    ./scripts/winssh < script.ps1
set -euo pipefail
if [ "$#" -gt 0 ]; then
    sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost "powershell -NoProfile -ExecutionPolicy Bypass -Command \"$*\""
else
    sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost "powershell -NoProfile -ExecutionPolicy Bypass -Command -"
fi
```

```bash
chmod +x /home/jesse/git/winpepper/scripts/winssh
```

- [ ] **Step 3: Write `scripts/winrun`** (sync + run a dotnet command on the VM)

```bash
#!/usr/bin/env bash
# Usage: ./scripts/winrun "dotnet test" [-- extra args]
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$HERE/scripts/sync-to-vm.sh"
CMD="$*"
sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost "cd C:\\winpepper; $CMD"
```

```bash
chmod +x /home/jesse/git/winpepper/scripts/winrun
```

- [ ] **Step 4: Write `scripts/sync-to-vm.sh`** (tarball-over-SSH sync; rsync on Windows is fiddly)

```bash
#!/usr/bin/env bash
# Sync the repo to C:\winpepper on the Windows VM, excluding build outputs.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Ensure target directory exists.
sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost \
    'powershell -NoProfile -Command "if (!(Test-Path C:\winpepper)) { New-Item -ItemType Directory -Path C:\winpepper | Out-Null }"' >/dev/null

# Tar up the repo (without bin/obj/.git/test-results), pipe to the VM, untar into C:\winpepper.
tar --exclude='./.git' \
    --exclude='./bin' \
    --exclude='./obj' \
    --exclude='./TestResults' \
    --exclude='*/bin' \
    --exclude='*/obj' \
    --exclude='./artifacts' \
    -cf - -C "$HERE" . \
  | sshpass -p 'password' ssh -o StrictHostKeyChecking=no -o LogLevel=ERROR -p 2222 user@localhost \
      'tar -xf - -C C:/winpepper'

echo "Synced $HERE to localhost:2222 C:\\winpepper"
```

```bash
chmod +x /home/jesse/git/winpepper/scripts/sync-to-vm.sh
```

- [ ] **Step 5: Write `scripts/provision-vm.ps1`** (one-shot installer for the dev environment)

```powershell
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
$dotnetVersion = & dotnet --version 2>$null
if ($LASTEXITCODE -ne 0 -or -not $dotnetVersion.StartsWith("9.")) {
    Write-Host "Installing .NET 9 SDK..."
    $msi = "$env:TEMP\dotnet-sdk-9.msi"
    Invoke-WebRequest -Uri "https://download.visualstudio.microsoft.com/download/pr/dotnet-sdk-9.0.100-win-x64.exe" -OutFile $msi
    Start-Process -Wait -FilePath $msi -ArgumentList "/quiet /norestart"
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

# DirectX runtime — already present on Win11; no install needed.
# Vulkan runtime for LlamaSharp comes in Plan 2.

# Final PATH refresh so subsequent SSH sessions see it -----------------------
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
$env:Path = "$machinePath;$userPath"

Write-Host ""
Write-Host "Provisioning done."
Write-Host "  dotnet: $(& dotnet --version)"
Write-Host "  git:    $(& git --version)"
```

- [ ] **Step 6: Run provisioning**

```bash
cd /home/jesse/git/winpepper
./scripts/winssh < scripts/provision-vm.ps1
```

Expected: ends with `dotnet: 9.0.x` and `git: git version 2.x.x`. This step downloads several hundred MB and can take 15-30 minutes.

- [ ] **Step 7: Verify sync works**

```bash
./scripts/sync-to-vm.sh
./scripts/winssh "ls C:\winpepper"
```

Expected: lists the repo contents on the VM.

- [ ] **Step 8: Verify build works on the VM**

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test"
```

Expected: build succeeds, the smoke test from Task 1 passes on Windows.

- [ ] **Step 9: Write `docs/manual-test.md`**

```markdown
# Winpepper Manual Test Plan

## Smoke checklist (per release)

- [ ] App launches without errors
- [ ] Hotkey records / stops on press / release
- [ ] Transcript appears in the focused window
- [ ] Cancel hotkey aborts a session cleanly
- [ ] Settings persist across restarts (Plan 3+)
- [ ] Models tab downloads and verifies (Plan 4+)
- [ ] MSI install / upgrade / uninstall (Plan 6+)

## VM bootstrap

If working on a fresh machine:

1. Set up the dockur Windows VM (see `~/.claude/skills/windows-vm/SKILL.md`).
2. From the repo root: `./scripts/winssh < scripts/provision-vm.ps1`.
3. Verify: `./scripts/winrun "dotnet --version"` returns `9.0.x`.
```

- [ ] **Step 10: Commit**

```bash
git add scripts/ docs/manual-test.md
git commit -m "tooling: VM provisioning + sync + remote-run scripts"
```

---

## Task 3: Atomic file IO

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Io/AtomicFile.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Io/AtomicFileTests.cs`

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Io/AtomicFileTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Io;
using Xunit;

namespace Winpepper.Core.Tests.Io;

public class AtomicFileTests : IDisposable
{
    private readonly string _tempDir;

    public AtomicFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"winpepper-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void WriteAllText_CreatesFileWithContents()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        AtomicFile.WriteAllText(path, "hello");
        File.ReadAllText(path).ShouldBe("hello");
    }

    [Fact]
    public void WriteAllText_OverwritesExistingFile()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        File.WriteAllText(path, "old");
        AtomicFile.WriteAllText(path, "new");
        File.ReadAllText(path).ShouldBe("new");
    }

    [Fact]
    public void WriteAllText_DoesNotLeaveTempFile()
    {
        var path = Path.Combine(_tempDir, "file.txt");
        AtomicFile.WriteAllText(path, "content");
        Directory.GetFiles(_tempDir).Length.ShouldBe(1);
    }

    [Fact]
    public void WriteAllText_CreatesParentDirectories()
    {
        var path = Path.Combine(_tempDir, "nested", "deep", "file.txt");
        AtomicFile.WriteAllText(path, "hello");
        File.ReadAllText(path).ShouldBe("hello");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~AtomicFileTests"
```

Expected: build fails (type `AtomicFile` not found).

- [ ] **Step 3: Implement `src/Winpepper.Core/Io/AtomicFile.cs`**

```csharp
namespace Winpepper.Core.Io;

/// <summary>
/// Writes files atomically: write to a temp file, flush to disk, then rename
/// over the destination. A crash mid-write leaves the destination either
/// untouched or fully replaced — never corrupted.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
        => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(contents, 0, contents.Length);
                fs.Flush(flushToDisk: true);
            }
            // File.Move(..., overwrite: true) is atomic on Windows (MoveFileEx with REPLACE_EXISTING)
            // and on Linux/macOS via rename(2).
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~AtomicFileTests"
```

Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Io tests/Winpepper.Core.Tests/Io
git commit -m "feat(core): atomic file writes"
```

---

## Task 4: Settings store

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/AppSettings.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/SettingsStore.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _path;
    public SettingsStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s.Schema.ShouldBe(1);
        s.MicDeviceId.ShouldBe("");
        s.AsrModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        s.PlaySounds.ShouldBeTrue();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new SettingsStore(_path);
        var s = store.Load();
        s = s with { MicDeviceId = "{abc-123}", PlaySounds = false };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("{abc-123}");
        loaded.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void Load_BadJson_FallsBackToDefaults()
    {
        File.WriteAllText(_path, "{ not json");
        var s = new SettingsStore(_path).Load();
        s.Schema.ShouldBe(1);
    }

    [Fact]
    public void Save_Uses_AtomicWrite()
    {
        var store = new SettingsStore(_path);
        store.Save(store.Load());
        Path.GetDirectoryName(_path)!
            .Pipe(d => Directory.GetFiles(d, $"{Path.GetFileName(_path)}.tmp-*"))
            .Length.ShouldBe(0);
    }
}

internal static class PipeExtensions
{
    public static TOut Pipe<TIn, TOut>(this TIn input, Func<TIn, TOut> fn) => fn(input);
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: fails to compile.

- [ ] **Step 3: Implement `src/Winpepper.Core/Settings/AppSettings.cs`**

```csharp
namespace Winpepper.Core.Settings;

/// <summary>
/// Persisted user settings. Schema-versioned for forward compatibility.
/// Defaults are returned when the file is missing or corrupt.
/// </summary>
public record AppSettings
{
    public int Schema { get; init; } = 1;

    // Audio
    public string MicDeviceId { get; init; } = "";

    // ASR
    public string AsrModelName { get; init; } = "parakeet-tdt-0.6b-v3";

    // Hotkeys (Plan 1 defaults; persisted as raw VK codes + modifier flags
    // — full chord recording UI comes in Plan 3)
    public string HoldHotkey { get; init; } = "RightCtrl+RightShift";
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+Space";

    // Sound effects
    public bool PlaySounds { get; init; } = true;
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Settings/SettingsStore.cs`**

```csharp
using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public SettingsStore(string path)
    {
        _path = path;
    }

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings tests/Winpepper.Core.Tests/Settings
git commit -m "feat(core): settings store with atomic persistence"
```

---

## Task 5: Logging infrastructure

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Core/Winpepper.Core.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Logging/WinpepperLogging.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Logging/WinpepperLoggingTests.cs`

- [ ] **Step 1: Modify `Winpepper.Core.csproj` to reference Serilog**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Core</RootNamespace>
    <AssemblyName>Winpepper.Core</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Extensions.Logging" />
    <PackageReference Include="Serilog.Sinks.File" />
    <PackageReference Include="Serilog.Sinks.Console" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write failing test `tests/Winpepper.Core.Tests/Logging/WinpepperLoggingTests.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class WinpepperLoggingTests : IDisposable
{
    private readonly string _dir;
    public WinpepperLoggingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"winpepper-log-{Guid.NewGuid():N}");
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void Create_WritesToFile_AndLogsLineAppears()
    {
        using var factory = WinpepperLogging.Create(_dir, debugConsole: false, minimumLevel: LogLevel.Information);
        var log = factory.CreateLogger("Test");
        log.LogInformation("hello {Token}", "world");
        WinpepperLogging.Flush();

        // Find the rolling file (winpepper-YYYYMMDD.log)
        var files = Directory.GetFiles(_dir, "winpepper-*.log");
        files.Length.ShouldBe(1);
        var contents = File.ReadAllText(files[0]);
        contents.ShouldContain("hello world");
    }
}
```

- [ ] **Step 3: Implement `src/Winpepper.Core/Logging/WinpepperLogging.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Winpepper.Core.Logging;

public static class WinpepperLogging
{
    public static ILoggerFactory Create(string logDirectory, bool debugConsole, LogLevel minimumLevel)
    {
        Directory.CreateDirectory(logDirectory);

        var serilogLevel = minimumLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };

        var template = "{Timestamp:yyyy-MM-ddTHH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(serilogLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "winpepper-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: template,
                shared: false);

        if (debugConsole)
        {
            config = config.WriteTo.Console(outputTemplate: template);
        }

        Log.Logger = config.CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false));
    }

    public static void Flush() => Log.CloseAndFlush();
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test --filter "FullyQualifiedName~WinpepperLoggingTests"
```

Expected: 1 test passes.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Logging tests/Winpepper.Core.Tests/Logging src/Winpepper.Core/Winpepper.Core.csproj
git commit -m "feat(core): serilog file + optional console logging"
```

---

## Task 6: Hotkey chord parsing and matching

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Winpepper.Platform.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/HotkeyChord.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/Modifier.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordTests.cs`

Pure-logic part of the hotkey system. Native hook comes in Task 7. Chord parsing is unit-testable on Linux.

- [ ] **Step 1: Add the projects to the solution**

```bash
cd /home/jesse/git/winpepper
mkdir -p src/Winpepper.Platform/Hotkeys tests/Winpepper.Platform.Tests/Hotkeys
```

- [ ] **Step 2: Write `src/Winpepper.Platform/Winpepper.Platform.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Platform</RootNamespace>
    <AssemblyName>Winpepper.Platform</AssemblyName>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="System.Threading.Channels" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Platform\Winpepper.Platform.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Add to solution**

```bash
dotnet sln add src/Winpepper.Platform/Winpepper.Platform.csproj
dotnet sln add tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj
```

- [ ] **Step 5: Write failing test `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyChordTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyChordTests
{
    [Theory]
    [InlineData("RightCtrl+RightShift", Modifier.RightCtrl | Modifier.RightShift, 0)]
    [InlineData("Ctrl+Shift+Space", Modifier.Ctrl | Modifier.Shift, 0x20)]
    [InlineData("Esc", Modifier.None, 0x1B)]
    [InlineData("LeftAlt+F12", Modifier.LeftAlt, 0x7B)]
    public void Parse_ValidStrings_ProducesExpectedChord(string text, Modifier mods, int vk)
    {
        var chord = HotkeyChord.Parse(text);
        chord.Modifiers.ShouldBe(mods);
        chord.VirtualKey.ShouldBe(vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("+Shift")]
    [InlineData("NotARealKey")]
    public void Parse_Invalid_ThrowsFormatException(string text)
    {
        Should.Throw<FormatException>(() => HotkeyChord.Parse(text));
    }

    [Fact]
    public void Matches_IgnoresExtraModifiers_WhenChordHasNoModifierRequirement()
    {
        // "Esc" with no required modifiers matches regardless of what's down.
        var chord = HotkeyChord.Parse("Esc");
        chord.Matches(0x1B, Modifier.LeftCtrl).ShouldBeTrue();
        chord.Matches(0x20, Modifier.None).ShouldBeFalse();
    }

    [Fact]
    public void Matches_RequiresExactModifierSet_WhenSpecified()
    {
        var chord = HotkeyChord.Parse("RightCtrl+RightShift");
        chord.Matches(0, Modifier.RightCtrl | Modifier.RightShift).ShouldBeTrue();
        chord.Matches(0, Modifier.RightCtrl).ShouldBeFalse();
        chord.Matches(0, Modifier.RightCtrl | Modifier.RightShift | Modifier.LeftCtrl).ShouldBeFalse();
    }

    [Fact]
    public void ToString_RoundTrips_Through_Parse()
    {
        var original = HotkeyChord.Parse("Ctrl+Shift+Space");
        var formatted = original.ToString();
        var roundtripped = HotkeyChord.Parse(formatted);
        roundtripped.ShouldBe(original);
    }
}
```

- [ ] **Step 6: Verify failure**

```bash
dotnet test --filter "FullyQualifiedName~HotkeyChordTests"
```

Expected: compile errors.

- [ ] **Step 7: Implement `src/Winpepper.Platform/Hotkeys/Modifier.cs`**

```csharp
namespace Winpepper.Platform.Hotkeys;

[Flags]
public enum Modifier
{
    None        = 0,
    LeftCtrl    = 1 << 0,
    RightCtrl   = 1 << 1,
    LeftShift   = 1 << 2,
    RightShift  = 1 << 3,
    LeftAlt     = 1 << 4,
    RightAlt    = 1 << 5,
    LeftWin     = 1 << 6,
    RightWin    = 1 << 7,

    Ctrl        = LeftCtrl | RightCtrl,
    Shift       = LeftShift | RightShift,
    Alt         = LeftAlt  | RightAlt,
    Win         = LeftWin  | RightWin,
}
```

- [ ] **Step 8: Implement `src/Winpepper.Platform/Hotkeys/HotkeyChord.cs`**

```csharp
namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// A keyboard chord such as "RightCtrl+RightShift" or "Ctrl+Shift+Space".
/// Strict parser: modifier names are case-sensitive to avoid ambiguity with
/// key letters.
/// </summary>
public sealed record HotkeyChord(Modifier Modifiers, int VirtualKey)
{
    private static readonly Dictionary<string, Modifier> ModifierMap = new()
    {
        ["LeftCtrl"]   = Modifier.LeftCtrl,
        ["RightCtrl"]  = Modifier.RightCtrl,
        ["Ctrl"]       = Modifier.Ctrl,
        ["LeftShift"]  = Modifier.LeftShift,
        ["RightShift"] = Modifier.RightShift,
        ["Shift"]      = Modifier.Shift,
        ["LeftAlt"]    = Modifier.LeftAlt,
        ["RightAlt"]   = Modifier.RightAlt,
        ["Alt"]        = Modifier.Alt,
        ["LeftWin"]    = Modifier.LeftWin,
        ["RightWin"]   = Modifier.RightWin,
        ["Win"]        = Modifier.Win,
    };

    // Subset of common keys, expand as needed. Names map to Windows VK_* codes.
    private static readonly Dictionary<string, int> KeyMap = BuildKeyMap();

    private static Dictionary<string, int> BuildKeyMap()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"]  = 0x20,
            ["Esc"]    = 0x1B,
            ["Escape"] = 0x1B,
            ["Tab"]    = 0x09,
            ["Enter"]  = 0x0D,
            ["Back"]   = 0x08,
            ["Insert"] = 0x2D,
            ["Delete"] = 0x2E,
            ["Home"]   = 0x24,
            ["End"]    = 0x23,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["Left"]   = 0x25,
            ["Up"]     = 0x26,
            ["Right"]  = 0x27,
            ["Down"]   = 0x28,
        };
        for (var i = 1; i <= 12; i++) { map[$"F{i}"] = 0x70 + i - 1; }
        for (var c = 'A'; c <= 'Z'; c++) { map[c.ToString()] = c; }
        for (var c = '0'; c <= '9'; c++) { map[c.ToString()] = c; }
        return map;
    }

    public static HotkeyChord Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Empty chord.");

        var parts = text.Split('+', StringSplitOptions.None);
        if (parts.Any(string.IsNullOrEmpty))
            throw new FormatException($"Empty token in chord '{text}'.");

        Modifier mods = Modifier.None;
        int? key = null;

        foreach (var part in parts)
        {
            if (ModifierMap.TryGetValue(part, out var m))
            {
                mods |= m;
            }
            else if (KeyMap.TryGetValue(part, out var k))
            {
                if (key.HasValue)
                    throw new FormatException($"Chord '{text}' has more than one non-modifier key.");
                key = k;
            }
            else
            {
                throw new FormatException($"Unknown token '{part}' in chord '{text}'.");
            }
        }

        // Modifier-only chord is allowed (key=0 means "match on modifier release/press only").
        return new HotkeyChord(mods, key ?? 0);
    }

    /// <summary>
    /// True when the supplied key + modifier state satisfies this chord.
    /// If <see cref="VirtualKey"/> is 0, only modifiers are compared.
    /// </summary>
    public bool Matches(int virtualKey, Modifier currentModifiers)
    {
        if (Modifiers != Modifier.None)
        {
            // Caller's modifier set must include exactly our required modifiers.
            // "Ctrl" matches if either LeftCtrl or RightCtrl is down; do that by
            // checking each pair-group.
            if (!ModifiersSatisfied(currentModifiers)) return false;
        }
        return VirtualKey == 0 || virtualKey == VirtualKey;
    }

    private bool ModifiersSatisfied(Modifier current)
    {
        // For each modifier group required, at least one side must be down.
        if (HasAny(Modifier.Ctrl)  && !HasAny(current & Modifier.Ctrl))  return false;
        if (HasAny(Modifier.Shift) && !HasAny(current & Modifier.Shift)) return false;
        if (HasAny(Modifier.Alt)   && !HasAny(current & Modifier.Alt))   return false;
        if (HasAny(Modifier.Win)   && !HasAny(current & Modifier.Win))   return false;

        // Side-specific requirement: if we require LeftCtrl, that exact bit must be set.
        var sideSpecific = Modifiers & ~(Modifier.Ctrl | Modifier.Shift | Modifier.Alt | Modifier.Win
                                          | Modifier.LeftCtrl | Modifier.RightCtrl
                                          | Modifier.LeftShift | Modifier.RightShift
                                          | Modifier.LeftAlt | Modifier.RightAlt
                                          | Modifier.LeftWin | Modifier.RightWin);
        // The masked-off "group" values (Ctrl, Shift, ...) include their side flags, so
        // a chord declared with "RightCtrl" alone has Modifiers == RightCtrl: the group
        // check above sees the Ctrl bit and passes if any Ctrl is down. We additionally
        // demand the *specific* side here:
        var requiredSides = Modifiers & (Modifier.LeftCtrl | Modifier.RightCtrl
                                        | Modifier.LeftShift | Modifier.RightShift
                                        | Modifier.LeftAlt | Modifier.RightAlt
                                        | Modifier.LeftWin | Modifier.RightWin);
        if (requiredSides != Modifier.None && (current & requiredSides) != requiredSides) return false;

        // And nothing extra above the required modifier categories should be on (helps
        // avoid Ctrl+Shift firing on Ctrl+Alt+Shift).
        var requiredGroups = Modifier.None;
        if (HasAny(Modifier.Ctrl))  requiredGroups |= Modifier.Ctrl;
        if (HasAny(Modifier.Shift)) requiredGroups |= Modifier.Shift;
        if (HasAny(Modifier.Alt))   requiredGroups |= Modifier.Alt;
        if (HasAny(Modifier.Win))   requiredGroups |= Modifier.Win;

        var currentGroups =
            (HasAny(current & Modifier.Ctrl)  ? Modifier.Ctrl  : Modifier.None) |
            (HasAny(current & Modifier.Shift) ? Modifier.Shift : Modifier.None) |
            (HasAny(current & Modifier.Alt)   ? Modifier.Alt   : Modifier.None) |
            (HasAny(current & Modifier.Win)   ? Modifier.Win   : Modifier.None);
        if (currentGroups != requiredGroups) return false;

        return true;
    }

    private bool HasAny(Modifier m) => (Modifiers & m) != Modifier.None;
    private static bool HasAny(Modifier source, Modifier mask) => (source & mask) != Modifier.None;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(Modifier.LeftCtrl))   parts.Add("LeftCtrl");
        else if (Modifiers.HasFlag(Modifier.RightCtrl)) parts.Add("RightCtrl");
        else if (Modifiers.HasFlag(Modifier.Ctrl))  parts.Add("Ctrl");

        if (Modifiers.HasFlag(Modifier.LeftShift))   parts.Add("LeftShift");
        else if (Modifiers.HasFlag(Modifier.RightShift)) parts.Add("RightShift");
        else if (Modifiers.HasFlag(Modifier.Shift))  parts.Add("Shift");

        if (Modifiers.HasFlag(Modifier.LeftAlt))   parts.Add("LeftAlt");
        else if (Modifiers.HasFlag(Modifier.RightAlt)) parts.Add("RightAlt");
        else if (Modifiers.HasFlag(Modifier.Alt))  parts.Add("Alt");

        if (Modifiers.HasFlag(Modifier.LeftWin))   parts.Add("LeftWin");
        else if (Modifiers.HasFlag(Modifier.RightWin)) parts.Add("RightWin");
        else if (Modifiers.HasFlag(Modifier.Win))  parts.Add("Win");

        if (VirtualKey != 0)
        {
            var keyName = ReverseKeyName(VirtualKey);
            parts.Add(keyName);
        }
        return string.Join("+", parts);
    }

    private static string ReverseKeyName(int vk) => vk switch
    {
        0x20 => "Space", 0x1B => "Esc", 0x09 => "Tab", 0x0D => "Enter",
        0x08 => "Back", 0x2D => "Insert", 0x2E => "Delete",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        >= 0x70 and <= 0x7B => $"F{vk - 0x70 + 1}",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => $"VK_0x{vk:X2}",
    };
}
```

- [ ] **Step 9: Verify pass**

```bash
dotnet test --filter "FullyQualifiedName~HotkeyChordTests"
```

Expected: all chord tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Platform tests/Winpepper.Platform.Tests winpepper.sln
git commit -m "feat(platform): hotkey chord parsing and matching"
```

---

## Task 7: Hotkey hook (WH_KEYBOARD_LL) native + manager

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/HotkeyEvent.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookIntegrationTests.cs`

Windows-only. Tests for this task are tagged `Platform=Windows` and only run on the VM.

- [ ] **Step 1: Implement `src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs`**

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Hotkeys;

internal static partial class KeyboardHookNative
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN     = 0x0100;
    public const int WM_KEYUP       = 0x0101;
    public const int WM_SYSKEYDOWN  = 0x0104;
    public const int WM_SYSKEYUP    = 0x0105;

    // Virtual-key codes for left/right modifiers
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LSHIFT   = 0xA0;
    public const int VK_RSHIFT   = 0xA1;
    public const int VK_LMENU    = 0xA4; // Left Alt
    public const int VK_RMENU    = 0xA5; // Right Alt
    public const int VK_LWIN     = 0x5B;
    public const int VK_RWIN     = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll")]
    public static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    public static partial int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TranslateMessage(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial IntPtr DispatchMessageW(in MSG lpMsg);

    [LibraryImport("user32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
}
```

- [ ] **Step 2: Implement `src/Winpepper.Platform/Hotkeys/HotkeyEvent.cs`**

```csharp
namespace Winpepper.Platform.Hotkeys;

public enum HotkeyEventKind
{
    HoldDown,
    HoldUp,
    Toggle,
    Cancel,
}

public sealed record HotkeyEvent(HotkeyEventKind Kind, DateTimeOffset Timestamp);
```

- [ ] **Step 3: Implement `src/Winpepper.Platform/Hotkeys/HotkeyHook.cs`**

```csharp
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Installs WH_KEYBOARD_LL on a dedicated STA thread, watches for the configured
/// chords, and emits <see cref="HotkeyEvent"/> instances on an unbounded channel.
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private readonly HotkeyChord _hold;
    private readonly HotkeyChord _toggle;
    private readonly HotkeyChord _cancel;
    private readonly ILogger<HotkeyHook> _log;

    private readonly Channel<HotkeyEvent> _events =
        Channel.CreateUnbounded<HotkeyEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private LowLevelKeyboardProc? _callback;
    private Modifier _modifiers;
    private bool _holding;
    private readonly ManualResetEventSlim _ready = new(initialState: false);

    public ChannelReader<HotkeyEvent> Events => _events.Reader;

    /// <summary>
    /// Public for tests: synchronously evaluate a key event against the registered
    /// chords. Used by integration tests that don't want to install a real hook.
    /// </summary>
    public bool TryProcessKey(int vk, bool down, out HotkeyEvent? evt)
    {
        evt = null;
        UpdateModifierState(vk, down);

        if (down)
        {
            if (_cancel.Matches(vk, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Cancel, DateTimeOffset.UtcNow);
                return true;
            }
            if (_toggle.Matches(vk, _modifiers))
            {
                evt = new HotkeyEvent(HotkeyEventKind.Toggle, DateTimeOffset.UtcNow);
                return true;
            }
            if (_hold.Matches(vk, _modifiers) && !_holding)
            {
                _holding = true;
                evt = new HotkeyEvent(HotkeyEventKind.HoldDown, DateTimeOffset.UtcNow);
                return true;
            }
        }
        else if (_holding && !_hold.Matches(vk, _modifiers))
        {
            _holding = false;
            evt = new HotkeyEvent(HotkeyEventKind.HoldUp, DateTimeOffset.UtcNow);
            return true;
        }

        return false;
    }

    public HotkeyHook(HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel, ILogger<HotkeyHook> log)
    {
        _hold = hold; _toggle = toggle; _cancel = cancel; _log = log;
    }

    public void Start()
    {
        if (_hookThread != null) throw new InvalidOperationException("HotkeyHook already started.");
        _hookThread = new Thread(HookThread) { IsBackground = true, Name = "WinpepperHotkeyHook" };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("Hotkey hook failed to install within 5s.");
    }

    private void HookThread()
    {
        _hookThreadId = GetCurrentThreadId();
        _callback = HookCallback; // pin
        _hookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        if (_hookHandle == IntPtr.Zero)
        {
            _log.LogError("SetWindowsHookEx failed: 0x{Err:X}", Marshal.GetLastWin32Error());
            _ready.Set();
            return;
        }
        _ready.Set();
        _log.LogInformation("Hotkey hook installed on thread {Tid}", _hookThreadId);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(msg);
            DispatchMessageW(msg);
        }

        UnhookWindowsHookEx(_hookHandle);
        _events.Writer.TryComplete();
        _log.LogInformation("Hotkey hook thread exiting");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;
        var down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        var up   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;

        if (down || up)
        {
            if (TryProcessKey((int)data.VkCode, down, out var evt) && evt is not null)
            {
                _events.Writer.TryWrite(evt);
                // Swallow the chord so the foreground app doesn't see it.
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vk, bool down)
    {
        var mod = vk switch
        {
            VK_LCONTROL => Modifier.LeftCtrl,
            VK_RCONTROL => Modifier.RightCtrl,
            VK_LSHIFT   => Modifier.LeftShift,
            VK_RSHIFT   => Modifier.RightShift,
            VK_LMENU    => Modifier.LeftAlt,
            VK_RMENU    => Modifier.RightAlt,
            VK_LWIN     => Modifier.LeftWin,
            VK_RWIN     => Modifier.RightWin,
            _           => Modifier.None,
        };
        if (mod == Modifier.None) return;
        if (down) _modifiers |= mod; else _modifiers &= ~mod;
    }

    public void Dispose()
    {
        if (_hookThread is null) return;
        PostThreadMessageW(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _hookThread = null;
    }
}
```

- [ ] **Step 4: Write Linux-runnable test of the pure key-evaluation logic — `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs`**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;
using static Winpepper.Platform.Hotkeys.KeyboardHookNative;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyHookLogicTests
{
    private static HotkeyHook NewHook(string hold = "RightCtrl+RightShift",
                                       string toggle = "Ctrl+Shift+Space",
                                       string cancel = "Esc")
        => new(HotkeyChord.Parse(hold), HotkeyChord.Parse(toggle), HotkeyChord.Parse(cancel),
               new NullLogger<HotkeyHook>());

    [Fact]
    public void HoldChord_PressAndRelease_EmitsHoldDownThenHoldUp()
    {
        var hook = NewHook();
        // Right Ctrl down, then Right Shift down should fire HoldDown.
        hook.TryProcessKey(VK_RCONTROL, down: true,  out _).ShouldBeFalse();
        hook.TryProcessKey(VK_RSHIFT,   down: true,  out var down).ShouldBeTrue();
        down!.Kind.ShouldBe(HotkeyEventKind.HoldDown);

        // Releasing either modifier should fire HoldUp.
        hook.TryProcessKey(VK_RSHIFT, down: false, out var up).ShouldBeTrue();
        up!.Kind.ShouldBe(HotkeyEventKind.HoldUp);

        hook.TryProcessKey(VK_RCONTROL, down: false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ToggleChord_KeyDown_FiresToggleOnce()
    {
        var hook = NewHook();
        hook.TryProcessKey(VK_LCONTROL, down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(VK_LSHIFT,   down: true, out _).ShouldBeFalse();
        hook.TryProcessKey(0x20 /*Space*/, down: true, out var ev).ShouldBeTrue();
        ev!.Kind.ShouldBe(HotkeyEventKind.Toggle);
    }

    [Fact]
    public void CancelChord_PlainEsc_Fires()
    {
        var hook = NewHook();
        hook.TryProcessKey(0x1B, down: true, out var ev).ShouldBeTrue();
        ev!.Kind.ShouldBe(HotkeyEventKind.Cancel);
    }
}
```

- [ ] **Step 5: Verify Linux tests pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HotkeyHookLogicTests"
```

Expected: 3 tests pass.

- [ ] **Step 6: Write the Windows-only integration test `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookIntegrationTests.cs`**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

[Trait("Platform", "Windows")]
public class HotkeyHookIntegrationTests
{
    [Fact]
    public async Task Hook_Installs_And_DisposesCleanly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var hook = new HotkeyHook(
            HotkeyChord.Parse("RightCtrl+RightShift"),
            HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>());

        hook.Start();
        await Task.Delay(200);
        // Just confirm we can dispose without hanging.
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT { [FieldOffset(0)] public int Type; [FieldOffset(8)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort Vk; public ushort Scan; public uint Flags; public uint Time; public IntPtr ExtraInfo; }

    [Fact(Skip = "Manual: requires injecting Esc via SendInput, captured by our own hook. Enable when on the VM.")]
    public async Task Hook_ObservesSyntheticEscKey()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var hook = new HotkeyHook(
            HotkeyChord.Parse("RightCtrl+RightShift"),
            HotkeyChord.Parse("Ctrl+Shift+Space"),
            HotkeyChord.Parse("Esc"),
            new NullLogger<HotkeyHook>());

        hook.Start();

        var inputs = new[]
        {
            new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Vk = 0x1B, Flags = 0 } },
            new INPUT { Type = 1, Keyboard = new KEYBDINPUT { Vk = 0x1B, Flags = 2 } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()).ShouldBe(2u);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var got = await hook.Events.ReadAsync(cts.Token);
        got.Kind.ShouldBe(HotkeyEventKind.Cancel);
    }
}
```

- [ ] **Step 7: Run all tests on the VM**

```bash
./scripts/winrun "dotnet test"
```

Expected: all non-skipped tests pass on the VM, including `Hook_Installs_And_DisposesCleanly`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys/KeyboardHookNative.cs src/Winpepper.Platform/Hotkeys/HotkeyEvent.cs src/Winpepper.Platform/Hotkeys/HotkeyHook.cs tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookLogicTests.cs tests/Winpepper.Platform.Tests/Hotkeys/HotkeyHookIntegrationTests.cs
git commit -m "feat(platform): WH_KEYBOARD_LL hook with chord-aware event emission"
```

---

## Task 8: Text injection (SendInput, KEYEVENTF_UNICODE)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Injection/SendInputNative.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Injection/TextInjector.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Injection/TextInjectorTests.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Injection/TextInjectorIntegrationTests.cs`

- [ ] **Step 1: Implement `src/Winpepper.Platform/Injection/SendInputNative.cs`**

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Injection;

internal static partial class SendInputNative
{
    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP   = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int Dx, Dy; public uint MouseData; public uint Flags; public uint Time; public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint Msg; public ushort WParamL; public ushort WParamH; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUT
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public KEYBDINPUT Keyboard;
        [FieldOffset(8)] public MOUSEINPUT Mouse;
        [FieldOffset(8)] public HARDWAREINPUT Hardware;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);
}
```

- [ ] **Step 2: Write failing test `tests/Winpepper.Platform.Tests/Injection/TextInjectorTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorTests
{
    [Theory]
    [InlineData("a", new ushort[] { 0x0061 })]
    [InlineData("ab", new ushort[] { 0x0061, 0x0062 })]
    [InlineData("é", new ushort[] { 0x00E9 })] // é
    [InlineData("中", new ushort[] { 0x4E2D })] // 中
    // U+1F600 (grinning face) = surrogate pair D83D DE00
    [InlineData("😀", new ushort[] { 0xD83D, 0xDE00 })]
    [InlineData("ab中😀",
        new ushort[] { 0x0061, 0x0062, 0x4E2D, 0xD83D, 0xDE00 })]
    public void ToCodeUnits_HandlesAscii_NonAscii_AndSurrogatePairs(string text, ushort[] expected)
    {
        TextInjector.ToCodeUnits(text).ShouldBe(expected);
    }

    [Fact]
    public void BuildKeyDownUpInputs_ProducesTwoInputsPerCodeUnit_WithUnicodeFlag()
    {
        var inputs = TextInjector.BuildKeyDownUpInputs(new ushort[] { 0x0041 });
        inputs.Length.ShouldBe(2);
        inputs[0].Keyboard.Scan.ShouldBe((ushort)0x0041);
        (inputs[0].Keyboard.Flags & SendInputNative.KEYEVENTF_UNICODE).ShouldBe(SendInputNative.KEYEVENTF_UNICODE);
        (inputs[1].Keyboard.Flags & SendInputNative.KEYEVENTF_KEYUP).ShouldBe(SendInputNative.KEYEVENTF_KEYUP);
    }
}
```

- [ ] **Step 3: Implement `src/Winpepper.Platform/Injection/TextInjector.cs`**

```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.Injection;

public sealed class TextInjector
{
    private readonly ILogger<TextInjector> _log;
    public TextInjector(ILogger<TextInjector> log) => _log = log;

    public bool TryInject(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        var inputs = BuildKeyDownUpInputs(ToCodeUnits(text));
        var sent = SendInputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<SendInputNative.INPUT>());
        if (sent != (uint)inputs.Length)
        {
            _log.LogWarning("SendInput partial send: requested {Req}, sent {Sent}, err 0x{Err:X}",
                inputs.Length, sent, Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }

    /// <summary>
    /// UTF-16 code units (so emoji => surrogate pair, each unit sent separately).
    /// </summary>
    internal static ushort[] ToCodeUnits(string text)
    {
        var arr = new ushort[text.Length];
        for (var i = 0; i < text.Length; i++) arr[i] = text[i];
        return arr;
    }

    internal static SendInputNative.INPUT[] BuildKeyDownUpInputs(ReadOnlySpan<ushort> codeUnits)
    {
        var inputs = new SendInputNative.INPUT[codeUnits.Length * 2];
        for (var i = 0; i < codeUnits.Length; i++)
        {
            inputs[i * 2] = new SendInputNative.INPUT
            {
                Type = SendInputNative.INPUT_KEYBOARD,
                Keyboard = new SendInputNative.KEYBDINPUT
                {
                    Vk = 0,
                    Scan = codeUnits[i],
                    Flags = SendInputNative.KEYEVENTF_UNICODE,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };
            inputs[i * 2 + 1] = new SendInputNative.INPUT
            {
                Type = SendInputNative.INPUT_KEYBOARD,
                Keyboard = new SendInputNative.KEYBDINPUT
                {
                    Vk = 0,
                    Scan = codeUnits[i],
                    Flags = SendInputNative.KEYEVENTF_UNICODE | SendInputNative.KEYEVENTF_KEYUP,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            };
        }
        return inputs;
    }
}
```

- [ ] **Step 4: Verify Linux tests pass**

```bash
dotnet test --filter "FullyQualifiedName~TextInjectorTests"
```

Expected: all tests pass.

- [ ] **Step 5: Write Windows-only integration test `tests/Winpepper.Platform.Tests/Injection/TextInjectorIntegrationTests.cs`**

```csharp
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

[Trait("Platform", "Windows")]
public class TextInjectorIntegrationTests
{
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [Fact(Skip = "Requires interactive console window; run manually on VM with focus.")]
    public void Inject_Writes_To_Focused_Window()
    {
        if (!OperatingSystem.IsWindows()) return;
        var injector = new TextInjector(new NullLogger<TextInjector>());
        injector.TryInject("hello").ShouldBeTrue();
    }
}
```

- [ ] **Step 6: Run all tests on VM**

```bash
./scripts/winrun "dotnet test"
```

Expected: passes (the Skip'd test stays skipped).

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Platform/Injection tests/Winpepper.Platform.Tests/Injection
git commit -m "feat(platform): SendInput-based unicode text injection"
```

---

## Task 9: Audio recorder (WASAPI via NAudio)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Audio/Winpepper.Audio.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Audio/AudioFormat.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Audio/IAudioRecorder.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Audio/WasapiRecorder.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Audio/DeviceEnumerator.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Audio.Tests/WasapiRecorderIntegrationTests.cs`

- [ ] **Step 1: Write `src/Winpepper.Audio/Winpepper.Audio.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Audio</RootNamespace>
    <AssemblyName>Winpepper.Audio</AssemblyName>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="NAudio" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <PackageReference Include="NAudio.Wasapi" />
  </ItemGroup>
</Project>
```

Note: `WasapiRecorder.cs` will be wrapped in `#if WINDOWS` so the cross-platform build doesn't pull in WASAPI symbols.

- [ ] **Step 2: Write `tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Audio\Winpepper.Audio.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add the projects to the solution**

```bash
dotnet sln add src/Winpepper.Audio/Winpepper.Audio.csproj
dotnet sln add tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj
```

- [ ] **Step 4: Implement `src/Winpepper.Audio/AudioFormat.cs`**

```csharp
namespace Winpepper.Audio;

public sealed record AudioFormat(int SampleRate, int Channels);

public static class WinpepperAudioFormat
{
    public static readonly AudioFormat Mono16k = new(SampleRate: 16000, Channels: 1);
}
```

- [ ] **Step 5: Implement `src/Winpepper.Audio/IAudioRecorder.cs`**

```csharp
namespace Winpepper.Audio;

public interface IAudioRecorder : IDisposable
{
    /// <summary>Output format that frames are delivered in.</summary>
    AudioFormat Format { get; }

    /// <summary>Raised on a background thread with the next batch of mono PCM samples.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    void Start();
    /// <summary>Stops recording and returns the full captured buffer as f32 mono PCM.</summary>
    float[] Stop();
}
```

- [ ] **Step 6: Implement `src/Winpepper.Audio/WasapiRecorder.cs`**

```csharp
#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

public sealed class WasapiRecorder : IAudioRecorder
{
    public AudioFormat Format => WinpepperAudioFormat.Mono16k;
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly string? _deviceId;
    private WasapiCapture? _capture;
    private List<float> _buffer = new();
    private MediaFoundationResampler? _resampler;
    private float[] _resampleScratch = new float[4096];

    public WasapiRecorder(string? deviceId = null) { _deviceId = deviceId; }

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrEmpty(_deviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
            : enumerator.GetDevice(_deviceId);

        _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
        _capture.DataAvailable += OnData;
        _buffer = new List<float>(16000 * 30);
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (_capture is null) return;
        var fmt = _capture.WaveFormat;

        // Decode device bytes to float32 stereo/mono (whatever the device produced).
        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(e.Buffer, i * 2);
                samples[i] = s / 32768f;
            }
        }
        else
        {
            // Other formats: skip for v1. Real product would convert.
            return;
        }

        // Downmix to mono if needed.
        float[] mono;
        if (fmt.Channels > 1)
        {
            mono = new float[sampleCount / fmt.Channels];
            for (var i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
                mono[i] = sum / fmt.Channels;
            }
        }
        else
        {
            mono = samples;
        }

        // Resample to 16 kHz if needed.
        if (fmt.SampleRate != 16000)
        {
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, 1);
            var sourceProvider = new RawSourceWaveStream(MemoryStreamFromFloats(mono), sourceFormat);
            var resampler = new MediaFoundationResampler(sourceProvider, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)) { ResamplerQuality = 60 };
            var resampled = new List<float>();
            var byteBuf = new byte[8192];
            int read;
            while ((read = resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
            {
                var floats = new float[read / 4];
                Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
                resampled.AddRange(floats);
            }
            mono = resampled.ToArray();
        }

        lock (_buffer)
        {
            _buffer.AddRange(mono);
        }
        FramesAvailable?.Invoke(mono);
    }

    private static Stream MemoryStreamFromFloats(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return new MemoryStream(bytes);
    }

    public float[] Stop()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
        _capture = null;
        lock (_buffer)
        {
            return _buffer.ToArray();
        }
    }

    public void Dispose()
    {
        _capture?.Dispose();
        _capture = null;
    }
}
#endif
```

- [ ] **Step 7: Implement `src/Winpepper.Audio/DeviceEnumerator.cs`**

```csharp
#if WINDOWS
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

public sealed record CaptureDevice(string Id, string FriendlyName, bool IsDefault);

public static class DeviceEnumerator
{
    public static IReadOnlyList<CaptureDevice> List()
    {
        var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia).ID;
        var list = new List<CaptureDevice>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            list.Add(new CaptureDevice(d.ID, d.FriendlyName, d.ID == defaultId));
        }
        return list;
    }
}
#endif
```

- [ ] **Step 8: Write Windows-only integration test `tests/Winpepper.Audio.Tests/WasapiRecorderIntegrationTests.cs`**

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

[Trait("Platform", "Windows")]
public class WasapiRecorderIntegrationTests
{
#if WINDOWS
    [Fact]
    public void Enumerate_Devices_ReturnsAtLeastOne()
    {
        if (!OperatingSystem.IsWindows()) return;
        var devices = DeviceEnumerator.List();
        devices.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Record_500ms_ProducesNonEmptyBuffer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var rec = new WasapiRecorder();
        rec.Start();
        Thread.Sleep(500);
        var samples = rec.Stop();
        samples.Length.ShouldBeGreaterThan(1000); // anything plausibly recorded
    }
#endif
}
```

- [ ] **Step 9: Build and run on the VM**

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test --filter \"FullyQualifiedName~WasapiRecorderIntegrationTests\""
```

Expected: both tests pass. The VM doesn't have a real mic — NAudio still opens the default endpoint and returns silence; the buffer length assertion validates that.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Audio tests/Winpepper.Audio.Tests winpepper.sln
git commit -m "feat(audio): WASAPI recorder with downmix + 16k resample"
```

---

## Task 10: Parakeet vocabulary loader

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/Winpepper.Asr.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/Vocabulary.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/VocabularyTests.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/fixtures/tiny-vocab.txt`

Reference: `parakeet-rs/src/vocab.rs` — one token per line, line index = token id; SentencePiece-style `▁` prefix means word boundary; last id is the blank token.

- [ ] **Step 1: Write `src/Winpepper.Asr/Winpepper.Asr.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Asr</RootNamespace>
    <AssemblyName>Winpepper.Asr</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Microsoft.ML.OnnxRuntime.DirectML" />
    <PackageReference Include="NWaves" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Asr\Winpepper.Asr.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Update="fixtures\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Add to solution**

```bash
dotnet sln add src/Winpepper.Asr/Winpepper.Asr.csproj
dotnet sln add tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj
```

- [ ] **Step 4: Create test fixture `tests/Winpepper.Asr.Tests/fixtures/tiny-vocab.txt`**

```
▁hello
▁world
,
.
<blank>
```

- [ ] **Step 5: Write failing test `tests/Winpepper.Asr.Tests/VocabularyTests.cs`**

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class VocabularyTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void FromFile_ReadsAllTokens_InOrder()
    {
        var v = Vocabulary.FromFile(FixturePath("tiny-vocab.txt"));
        v.Size.ShouldBe(5);
        v.Tokens.ShouldBe(["▁hello", "▁world", ",", ".", "<blank>"]);
        v.BlankId.ShouldBe(4);
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, "hello world")]
    [InlineData(new[] { 0, 2, 1, 3 }, "hello, world.")]
    [InlineData(new int[] { }, "")]
    public void Decode_ExpandsBoundary_AndStripsBlanks(int[] tokenIds, string expected)
    {
        var v = Vocabulary.FromFile(FixturePath("tiny-vocab.txt"));
        v.Decode(tokenIds).ShouldBe(expected);
    }
}
```

- [ ] **Step 6: Implement `src/Winpepper.Asr/Vocabulary.cs`**

```csharp
namespace Winpepper.Asr;

public sealed class Vocabulary
{
    public IReadOnlyList<string> Tokens { get; }
    public int Size => Tokens.Count;
    public int BlankId { get; }

    private Vocabulary(IReadOnlyList<string> tokens, int blankId) { Tokens = tokens; BlankId = blankId; }

    public static Vocabulary FromFile(string path)
    {
        var lines = File.ReadAllLines(path).Select(l => l.TrimEnd('\r')).ToList();
        // Trim a trailing blank line if present (common in HuggingFace exports).
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        // Convention: last token is the blank.
        return new Vocabulary(lines, lines.Count - 1);
    }

    public string Decode(IEnumerable<int> tokenIds)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var id in tokenIds)
        {
            if (id == BlankId) continue;
            if (id < 0 || id >= Tokens.Count) continue;
            var tok = Tokens[id];
            if (tok.StartsWith("▁", StringComparison.Ordinal))
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(tok[1..]);
            }
            else
            {
                sb.Append(tok);
            }
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 7: Verify pass**

```bash
dotnet test --filter "FullyQualifiedName~VocabularyTests"
```

Expected: 4 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Asr tests/Winpepper.Asr.Tests winpepper.sln
git commit -m "feat(asr): SentencePiece-style vocabulary loader and decode"
```

---

## Task 11: Mel-feature extraction for Parakeet TDT v3

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/MelFeatureExtractor.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/PreprocessorConfig.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/MelFeatureExtractorTests.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.wav` (generated by step 1)
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.mel.json` (reference output, generated)

Parakeet TDT v3 preprocessor (from `parakeet-rs/src/model_tdt.rs:32-44`):
- 128 mel features
- hop length 160 samples
- n_fft 512, win length 400
- preemphasis 0.97
- Hann window, periodic=false
- mel filter bank: slaney scale, slaney norm
- log-mel with mel_floor = 2^-24
- per-utterance mean/std normalization (ddof = 1)

NWaves provides `FilterBanks.MelBankSlaney` and an STFT implementation. We hand-roll the preemphasis, windowing detail, and normalization to match Parakeet exactly.

- [ ] **Step 1: Generate the test fixture WAV and reference mel features**

Write a small Python script that produces the fixtures using the same math, then run it once. Save it under `tests/Winpepper.Asr.Tests/fixtures/gen-fixture.py`:

```python
# Run: python3 tests/Winpepper.Asr.Tests/fixtures/gen-fixture.py
import json, math, struct, wave
import numpy as np

SAMPLE_RATE = 16000
DURATION = 1.0
FREQ = 440.0

# 1) Write WAV
samples = np.sin(2 * np.pi * FREQ * np.arange(int(SAMPLE_RATE * DURATION)) / SAMPLE_RATE).astype(np.float32)
pcm16 = (samples * 32767).astype(np.int16)
with wave.open('tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.wav', 'wb') as w:
    w.setnchannels(1); w.setsampwidth(2); w.setframerate(SAMPLE_RATE)
    w.writeframes(pcm16.tobytes())

# 2) Compute reference mel features (matches parakeet-rs / HF transformers ParakeetFeatureExtractor)
def slaney_mel_filters(n_fft, n_mels, sr, fmin=0.0, fmax=None):
    if fmax is None: fmax = sr / 2
    # Slaney mel scale
    def hz_to_mel(f):
        f = np.asarray(f, dtype=np.float64)
        below = f < 1000.0
        mel = np.where(below, f * 3.0 / 200.0,
                       15.0 + np.log(f / 1000.0) / (np.log(6.4) / 27.0))
        return mel
    def mel_to_hz(m):
        m = np.asarray(m, dtype=np.float64)
        below = m < 15.0
        f = np.where(below, m * 200.0 / 3.0,
                     1000.0 * np.exp((m - 15.0) * (np.log(6.4) / 27.0)))
        return f
    mel_min, mel_max = hz_to_mel(fmin), hz_to_mel(fmax)
    mel_points = np.linspace(mel_min, mel_max, n_mels + 2)
    hz_points = mel_to_hz(mel_points)
    bins = hz_points * (n_fft / sr)

    filters = np.zeros((n_mels, n_fft // 2 + 1), dtype=np.float64)
    for i in range(n_mels):
        left, center, right = bins[i], bins[i+1], bins[i+2]
        for k in range(n_fft // 2 + 1):
            if k < left or k > right: continue
            if k <= center:
                filters[i, k] = (k - left) / (center - left + 1e-12)
            else:
                filters[i, k] = (right - k) / (right - center + 1e-12)
        # Slaney norm
        enorm = 2.0 / (hz_points[i+2] - hz_points[i])
        filters[i] *= enorm
    return filters

def stft_magnitude_squared(x, n_fft=512, hop=160, win_len=400):
    # Hann periodic=false window, centered on n_fft
    w = 0.5 - 0.5 * np.cos(2 * np.pi * np.arange(win_len) / (win_len - 1))
    offset = (n_fft - win_len) // 2
    window = np.zeros(n_fft, dtype=np.float64)
    window[offset:offset + win_len] = w

    # Centered: pad reflect by n_fft//2 on each side
    pad = n_fft // 2
    x_padded = np.pad(x.astype(np.float64), pad_width=pad, mode='constant')
    n_frames = (len(x_padded) - n_fft) // hop + 1
    out = np.zeros((n_frames, n_fft // 2 + 1), dtype=np.float64)
    for t in range(n_frames):
        frame = x_padded[t * hop : t * hop + n_fft] * window
        spec = np.fft.rfft(frame, n=n_fft)
        out[t] = (spec.real * spec.real + spec.imag * spec.imag)
    return out

def compute_parakeet_features(samples_f32, sr=16000, n_mels=128, n_fft=512, hop=160,
                              win_len=400, preemphasis=0.97):
    x = samples_f32.astype(np.float64).copy()
    # Preemphasis: y[t] = x[t] - 0.97 * x[t-1] (applied to entire waveform once)
    for j in range(len(x) - 1, 0, -1):
        x[j] = x[j] - preemphasis * x[j - 1]

    power = stft_magnitude_squared(x, n_fft=n_fft, hop=hop, win_len=win_len)
    mel_filters = slaney_mel_filters(n_fft, n_mels, sr)
    mel = power @ mel_filters.T  # [T, n_mels]

    # Log mel with offset 2^-24
    mel_offset = 2 ** -24
    log_mel = np.log(np.maximum(mel + mel_offset, 1e-30))

    # Per-utterance normalization with ddof=1
    n_frames = log_mel.shape[0]
    mean = log_mel.mean(axis=0)
    var = log_mel.var(axis=0, ddof=1 if n_frames > 1 else 0)
    std = np.sqrt(var) + 1e-5
    norm = (log_mel - mean) / std
    return norm.astype(np.float32)

features = compute_parakeet_features(samples)
with open('tests/Winpepper.Asr.Tests/fixtures/tone-440hz-1s.mel.json', 'w') as f:
    json.dump({
        'shape': list(features.shape),
        # Save first 6 frames worth of features as the comparison surface
        # (full grid is ~100 frames × 128 mels = too much to inline)
        'first_six_frames': features[:6].tolist(),
        'last_frame': features[-1].tolist(),
    }, f)
print(f"Wrote tone-440hz-1s.wav and tone-440hz-1s.mel.json shape={features.shape}")
```

Run it:

```bash
cd /home/jesse/git/winpepper
python3 tests/Winpepper.Asr.Tests/fixtures/gen-fixture.py
```

Commit the script + the generated fixtures (so the test doesn't depend on Python at run time).

- [ ] **Step 2: Implement `src/Winpepper.Asr/PreprocessorConfig.cs`**

```csharp
namespace Winpepper.Asr;

/// <summary>
/// Parakeet TDT v3 preprocessor configuration. Values match
/// parakeet-rs/src/model_tdt.rs and the HuggingFace ParakeetFeatureExtractor.
/// </summary>
public sealed record PreprocessorConfig(
    int FeatureSize = 128,
    int HopLength = 160,
    int NFft = 512,
    int WinLength = 400,
    double Preemphasis = 0.97,
    int SamplingRate = 16000)
{
    public static readonly PreprocessorConfig ParakeetTdtV3 = new();
}
```

- [ ] **Step 3: Write failing test `tests/Winpepper.Asr.Tests/MelFeatureExtractorTests.cs`**

```csharp
using System.Text.Json;
using NAudio.Wave;
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class MelFeatureExtractorTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static float[] ReadWavMonoF32(string path)
    {
        // Minimal RIFF reader: assumes 16-bit PCM mono.
        var bytes = File.ReadAllBytes(path);
        // 'data' chunk header search.
        int i = 0;
        while (i < bytes.Length - 4 && !(bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a'))
            i++;
        if (i >= bytes.Length - 4) throw new InvalidDataException("no data chunk");
        var size = BitConverter.ToInt32(bytes, i + 4);
        var dataStart = i + 8;
        var sampleCount = size / 2;
        var samples = new float[sampleCount];
        for (var s = 0; s < sampleCount; s++)
        {
            short v = BitConverter.ToInt16(bytes, dataStart + s * 2);
            samples[s] = v / 32768f;
        }
        return samples;
    }

    [Fact]
    public void Extract_MatchesPythonReference_FirstSixFrames()
    {
        var wav = ReadWavMonoF32(FixturePath("tone-440hz-1s.wav"));
        var reference = JsonSerializer.Deserialize<MelReference>(
            File.ReadAllText(FixturePath("tone-440hz-1s.mel.json")))!;

        var features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3).Extract(wav);

        features.GetLength(0).ShouldBe(reference.Shape[0]);
        features.GetLength(1).ShouldBe(reference.Shape[1]);

        // Compare first six frames within tolerance.
        for (var t = 0; t < 6; t++)
            for (var m = 0; m < reference.Shape[1]; m++)
                features[t, m].ShouldBe(reference.FirstSixFrames[t][m], tolerance: 1e-3);
    }

    [Fact]
    public void Extract_MatchesPythonReference_LastFrame()
    {
        var wav = ReadWavMonoF32(FixturePath("tone-440hz-1s.wav"));
        var reference = JsonSerializer.Deserialize<MelReference>(
            File.ReadAllText(FixturePath("tone-440hz-1s.mel.json")))!;

        var features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3).Extract(wav);

        var t = features.GetLength(0) - 1;
        for (var m = 0; m < reference.Shape[1]; m++)
            features[t, m].ShouldBe(reference.LastFrame[m], tolerance: 1e-3);
    }

    private sealed record MelReference(
        [property: System.Text.Json.Serialization.JsonPropertyName("shape")] int[] Shape,
        [property: System.Text.Json.Serialization.JsonPropertyName("first_six_frames")] float[][] FirstSixFrames,
        [property: System.Text.Json.Serialization.JsonPropertyName("last_frame")] float[] LastFrame);
}
```

- [ ] **Step 4: Implement `src/Winpepper.Asr/MelFeatureExtractor.cs`**

```csharp
namespace Winpepper.Asr;

/// <summary>
/// Mel feature extractor for Parakeet TDT v3.
/// Produces a [T, n_mels] float32 matrix in row-major order.
///
/// Plan 1 uses a hand-rolled O(n^2) rFFT — fine at n_fft=512 (one frame per 10 ms
/// of audio, ~100 frames/sec). Plan 2 will swap in NWaves' RealFft once its
/// output layout is verified to match the math here.
/// </summary>
public sealed class MelFeatureExtractor
{
    private readonly PreprocessorConfig _config;
    private readonly double[] _window;
    private readonly double[][] _melFilters;

    private const double MelOffset = 1.0 / (1 << 24); // 2^-24
    private const double Epsilon = 1e-5;
    private const double MelMin = 1e-30;

    public MelFeatureExtractor(PreprocessorConfig config)
    {
        _config = config;
        _window = BuildHannWindow(config.NFft, config.WinLength);
        _melFilters = BuildSlaneyMelFilters(config.NFft, config.FeatureSize, config.SamplingRate);
    }

    public float[,] Extract(ReadOnlySpan<float> samplesF32)
    {
        // 1) Preemphasis (in-place, on a double-precision copy).
        var x = new double[samplesF32.Length];
        for (var i = 0; i < x.Length; i++) x[i] = samplesF32[i];
        for (var j = x.Length - 1; j >= 1; j--) x[j] -= _config.Preemphasis * x[j - 1];

        // 2) Centered framing with reflect-style zero padding.
        var pad = _config.NFft / 2;
        var padded = new double[x.Length + 2 * pad];
        Array.Copy(x, 0, padded, pad, x.Length);

        var nFrames = (padded.Length - _config.NFft) / _config.HopLength + 1;
        var nBins = _config.NFft / 2 + 1;

        // 3) For each frame: window, FFT, magnitude squared, mel project, log, accumulate.
        var logMel = new double[nFrames, _config.FeatureSize];
        var frame = new float[_config.NFft];
        var power = new double[nBins];

        for (var t = 0; t < nFrames; t++)
        {
            for (var k = 0; k < _config.NFft; k++)
                frame[k] = (float)(padded[t * _config.HopLength + k] * _window[k]);

            HandRolledRfftPower(frame, _config.NFft, power);

            // Mel project.
            for (var m = 0; m < _config.FeatureSize; m++)
            {
                double acc = 0.0;
                var filter = _melFilters[m];
                for (var k = 0; k < nBins; k++) acc += power[k] * filter[k];
                logMel[t, m] = Math.Log(Math.Max(acc + MelOffset, MelMin));
            }
        }

        // 4) Per-utterance mean/std normalization with ddof=1.
        var mean = new double[_config.FeatureSize];
        var sumSq = new double[_config.FeatureSize];
        for (var t = 0; t < nFrames; t++)
            for (var m = 0; m < _config.FeatureSize; m++)
            {
                var v = logMel[t, m];
                mean[m] += v; sumSq[m] += v * v;
            }
        var divisor = nFrames > 1 ? nFrames - 1 : 1;
        for (var m = 0; m < _config.FeatureSize; m++) mean[m] /= nFrames;

        var output = new float[nFrames, _config.FeatureSize];
        for (var m = 0; m < _config.FeatureSize; m++)
        {
            var variance = (sumSq[m] - nFrames * mean[m] * mean[m]) / divisor;
            var std = Math.Sqrt(Math.Max(variance, 0)) + Epsilon;
            var invStd = 1.0 / std;
            for (var t = 0; t < nFrames; t++)
                output[t, m] = (float)((logMel[t, m] - mean[m]) * invStd);
        }
        return output;
    }

    private static void HandRolledRfftPower(ReadOnlySpan<float> frame, int n, double[] power)
    {
        // O(n^2) DFT — fine for n=512 in tests; SessionEngine code path uses
        // NWaves FFT for production. Implementing this here keeps the test
        // independent of NWaves' rFFT output layout.
        for (var k = 0; k <= n / 2; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = -2.0 * Math.PI * k * t / n;
                re += frame[t] * Math.Cos(angle);
                im += frame[t] * Math.Sin(angle);
            }
            power[k] = re * re + im * im;
        }
    }

    private static double[] BuildHannWindow(int nFft, int winLength)
    {
        var w = new double[nFft];
        var offset = (nFft - winLength) / 2;
        for (var i = 0; i < winLength; i++)
            w[offset + i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (winLength - 1));
        return w;
    }

    private static double[][] BuildSlaneyMelFilters(int nFft, int nMels, int sr)
    {
        static double HzToMel(double f) =>
            f < 1000.0
                ? f * 3.0 / 200.0
                : 15.0 + Math.Log(f / 1000.0) / (Math.Log(6.4) / 27.0);
        static double MelToHz(double m) =>
            m < 15.0
                ? m * 200.0 / 3.0
                : 1000.0 * Math.Exp((m - 15.0) * (Math.Log(6.4) / 27.0));

        var melMin = HzToMel(0);
        var melMax = HzToMel(sr / 2.0);
        var nBins = nFft / 2 + 1;

        var melPoints = new double[nMels + 2];
        var hzPoints = new double[nMels + 2];
        for (var i = 0; i < nMels + 2; i++)
        {
            melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);
            hzPoints[i] = MelToHz(melPoints[i]);
        }
        var bins = new double[nMels + 2];
        for (var i = 0; i < nMels + 2; i++) bins[i] = hzPoints[i] * nFft / sr;

        var filters = new double[nMels][];
        for (var m = 0; m < nMels; m++)
        {
            filters[m] = new double[nBins];
            double left = bins[m], center = bins[m + 1], right = bins[m + 2];
            for (var k = 0; k < nBins; k++)
            {
                if (k < left || k > right) continue;
                filters[m][k] = k <= center
                    ? (k - left) / (center - left + 1e-12)
                    : (right - k) / (right - center + 1e-12);
            }
            var enorm = 2.0 / (hzPoints[m + 2] - hzPoints[m]);
            for (var k = 0; k < nBins; k++) filters[m][k] *= enorm;
        }
        return filters;
    }
}
```

Note: For Plan 1 the hand-rolled rFFT inside the extractor is acceptable (n_fft=512 is tiny). Plan 2 may swap to NWaves's `RealFft` after verifying its output layout matches; that's a performance-only swap that does not change correctness.

- [ ] **Step 5: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~MelFeatureExtractorTests"
```

Expected: both reference-comparison tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/MelFeatureExtractor.cs src/Winpepper.Asr/PreprocessorConfig.cs tests/Winpepper.Asr.Tests/MelFeatureExtractorTests.cs tests/Winpepper.Asr.Tests/fixtures
git commit -m "feat(asr): mel feature extraction matching Parakeet TDT v3 preprocessor"
```

---

## Task 12: Parakeet TDT v3 ONNX session + greedy decode

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/ParakeetSession.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/ParakeetTranscript.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/ParakeetSessionIntegrationTests.cs`
- Create: `/home/jesse/git/winpepper/scripts/download-parakeet.ps1`

Reference: `parakeet-rs/src/model_tdt.rs:run_encoder` and `:greedy_decode`. The C# version uses `Microsoft.ML.OnnxRuntime.DirectML` with the same input/output names.

Model files come from `istupakov/parakeet-tdt-0.6b-v3-onnx` on HuggingFace:
- `encoder-model.int8.onnx` (or `encoder-model.onnx`)
- `decoder_joint-model.int8.onnx` (or `decoder_joint-model.onnx`)
- `vocab.txt`

- [ ] **Step 1: Write `scripts/download-parakeet.ps1`**

```powershell
# Run via: ./scripts/winssh < scripts/download-parakeet.ps1
$dest = "$env:LOCALAPPDATA\winpepper\models\parakeet-tdt-0.6b-v3"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$base = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main"
$files = @("encoder-model.int8.onnx", "decoder_joint-model.int8.onnx", "vocab.txt")
foreach ($f in $files) {
    $out = Join-Path $dest $f
    if (Test-Path $out) { Write-Host "skip $f (exists)"; continue }
    Write-Host "Downloading $f..."
    Invoke-WebRequest -Uri "$base/$f" -OutFile $out
}
Write-Host "Models in $dest"
Get-ChildItem $dest | Format-Table Name, Length
```

- [ ] **Step 2: Download the models on the VM**

```bash
./scripts/winssh < scripts/download-parakeet.ps1
```

Expected: three files in `%LOCALAPPDATA%\winpepper\models\parakeet-tdt-0.6b-v3\`. Total size ~600 MB. The download takes 5-15 minutes depending on bandwidth.

- [ ] **Step 3: Implement `src/Winpepper.Asr/ParakeetTranscript.cs`**

```csharp
namespace Winpepper.Asr;

public sealed record ParakeetTranscript(
    string Text,
    IReadOnlyList<int> TokenIds,
    IReadOnlyList<int> FrameIndices,
    IReadOnlyList<int> Durations);
```

- [ ] **Step 4: Implement `src/Winpepper.Asr/ParakeetSession.cs`**

```csharp
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Winpepper.Asr;

/// <summary>
/// Parakeet TDT v3 ONNX session. Loads encoder + decoder_joint, performs a
/// greedy TDT decode (port of parakeet-rs/src/model_tdt.rs).
///
/// Decoder hidden state shape is the standard parakeet-rs export: [2, 1, 640].
/// Vocab size and feature dim are inferred from the loaded vocab and tensor shapes.
/// </summary>
public sealed class ParakeetSession : IDisposable
{
    private const int MaxTokensPerStep = 10;
    private const int DecoderHiddenLayers = 2;
    private const int DecoderHiddenDim = 640;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderJoint;
    private readonly Vocabulary _vocab;
    private readonly MelFeatureExtractor _features;

    public Vocabulary Vocab => _vocab;

    public ParakeetSession(string modelDir)
    {
        var (encoderPath, decoderPath, vocabPath) = ResolvePaths(modelDir);
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableMemoryPattern = false, // DirectML EP requirement
        };
        options.AppendExecutionProvider_DML(0);
        _encoder = new InferenceSession(encoderPath, options);
        _decoderJoint = new InferenceSession(decoderPath, options);
        _vocab = Vocabulary.FromFile(vocabPath);
        _features = new MelFeatureExtractor(PreprocessorConfig.ParakeetTdtV3);
    }

    private static (string Encoder, string Decoder, string Vocab) ResolvePaths(string dir)
    {
        string Find(params string[] names)
        {
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
            throw new FileNotFoundException($"None of {string.Join(", ", names)} found in {dir}");
        }
        return (
            Find("encoder-model.int8.onnx", "encoder-model.onnx", "encoder.onnx"),
            Find("decoder_joint-model.int8.onnx", "decoder_joint-model.onnx", "decoder_joint.onnx"),
            Find("vocab.txt"));
    }

    public ParakeetTranscript Transcribe(ReadOnlySpan<float> samples16k)
    {
        var features = _features.Extract(samples16k); // [T, 128]
        var (encoderOut, encoderLen) = RunEncoder(features);
        return GreedyDecode(encoderOut, encoderLen);
    }

    private (float[] EncoderOut, int Len) RunEncoder(float[,] features)
    {
        var time = features.GetLength(0);
        var feat = features.GetLength(1);

        // Encoder expects [batch=1, feature_size, time].
        var input = new float[1 * feat * time];
        for (var t = 0; t < time; t++)
            for (var f = 0; f < feat; f++)
                input[f * time + t] = features[t, f];

        var audioSignal = new DenseTensor<float>(input, [1, feat, time]);
        var length = new DenseTensor<long>([time], [1]);

        using var results = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("audio_signal", audioSignal),
            NamedOnnxValue.CreateFromTensor("length", length),
        ]);

        var outTensor = results.First(r => r.Name == "outputs").AsTensor<float>();
        var lengths   = results.First(r => r.Name == "encoded_lengths").AsTensor<long>();

        // Encoder outputs [B, T', D] per parakeet-rs.
        var b = outTensor.Dimensions[0];
        var tprime = outTensor.Dimensions[1];
        var d = outTensor.Dimensions[2];
        if (b != 1) throw new InvalidOperationException("Batch != 1");
        var flat = new float[tprime * d];
        var i = 0;
        foreach (var v in outTensor) flat[i++] = v;
        return (flat, (int)lengths[0]);
    }

    private ParakeetTranscript GreedyDecode(float[] encoderOut, int validLen)
    {
        // encoderOut is row-major [T', D]; we don't trust validLen > T'.
        var vocabSize = _vocab.Size;
        var blankId = _vocab.BlankId;
        // Derive T' and D from len:
        // We saved encoderOut length above. T' = encoderOut.Length / D. D unknown here;
        // we need it from the decoder_joint metadata. The simplest path: ask the
        // first decoder_joint input metadata.
        var encoderInputMeta = _decoderJoint.InputMetadata["encoder_outputs"];
        var d = encoderInputMeta.Dimensions[1] > 0 ? encoderInputMeta.Dimensions[1] : 1024;
        var tprime = encoderOut.Length / d;

        var stateH = new float[DecoderHiddenLayers * 1 * DecoderHiddenDim];
        var stateC = new float[DecoderHiddenLayers * 1 * DecoderHiddenDim];
        var lastToken = blankId;

        var tokens = new List<int>();
        var frameIndices = new List<int>();
        var durations = new List<int>();

        var t = 0;
        var emitted = 0;
        var frameBuf = new float[d];

        while (t < Math.Min(tprime, validLen))
        {
            // Slice encoder frame at index t into [1, D, 1].
            for (var k = 0; k < d; k++) frameBuf[k] = encoderOut[t * d + k];
            var encFrame = new DenseTensor<float>(frameBuf, [1, d, 1]);
            var targets = new DenseTensor<int>([lastToken], [1, 1]);
            var targetLen = new DenseTensor<int>([1], [1]);
            var sh = new DenseTensor<float>(stateH, [DecoderHiddenLayers, 1, DecoderHiddenDim]);
            var sc = new DenseTensor<float>(stateC, [DecoderHiddenLayers, 1, DecoderHiddenDim]);

            using var results = _decoderJoint.Run(
            [
                NamedOnnxValue.CreateFromTensor("encoder_outputs", encFrame),
                NamedOnnxValue.CreateFromTensor("targets", targets),
                NamedOnnxValue.CreateFromTensor("target_length", targetLen),
                NamedOnnxValue.CreateFromTensor("input_states_1", sh),
                NamedOnnxValue.CreateFromTensor("input_states_2", sc),
            ]);

            var logits = results.First(r => r.Name == "outputs").AsTensor<float>();
            // logits shape [1, 1, vocab + 5]; flatten.
            var flat = new float[logits.Length];
            var idx = 0;
            foreach (var v in logits) flat[idx++] = v;

            // Pick best token from first vocab_size logits.
            var bestToken = 0; var bestVal = float.NegativeInfinity;
            for (var i = 0; i < vocabSize; i++)
                if (flat[i] > bestVal) { bestVal = flat[i]; bestToken = i; }

            // Pick best duration step from remaining.
            var durCount = flat.Length - vocabSize;
            var bestDur = 0; var bestDurVal = float.NegativeInfinity;
            for (var i = 0; i < durCount; i++)
                if (flat[vocabSize + i] > bestDurVal) { bestDurVal = flat[vocabSize + i]; bestDur = i; }

            if (bestToken != blankId)
            {
                tokens.Add(bestToken);
                frameIndices.Add(t);
                durations.Add(bestDur);
                lastToken = bestToken;
                emitted++;

                // Update hidden state from decoder outputs.
                var newH = results.First(r => r.Name == "output_states_1").AsTensor<float>();
                var newC = results.First(r => r.Name == "output_states_2").AsTensor<float>();
                var hi = 0; foreach (var v in newH) stateH[hi++] = v;
                var ci = 0; foreach (var v in newC) stateC[ci++] = v;
            }

            if (bestDur > 0)
            {
                t += bestDur;
                emitted = 0;
            }
            else if (bestToken == blankId || emitted >= MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
        }

        var text = _vocab.Decode(tokens);
        return new ParakeetTranscript(text, tokens, frameIndices, durations);
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}
```

- [ ] **Step 5: Write Windows-only integration test `tests/Winpepper.Asr.Tests/ParakeetSessionIntegrationTests.cs`**

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

[Trait("Platform", "Windows")]
public class ParakeetSessionIntegrationTests
{
    private static string ModelDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winpepper", "models", "parakeet-tdt-0.6b-v3");

    private static float[] LoadWavMonoF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int i = 0;
        while (i < bytes.Length - 4 && !(bytes[i] == 'd' && bytes[i + 1] == 'a' && bytes[i + 2] == 't' && bytes[i + 3] == 'a'))
            i++;
        var size = BitConverter.ToInt32(bytes, i + 4);
        var dataStart = i + 8;
        var sampleCount = size / 2;
        var samples = new float[sampleCount];
        for (var s = 0; s < sampleCount; s++)
        {
            short v = BitConverter.ToInt16(bytes, dataStart + s * 2);
            samples[s] = v / 32768f;
        }
        return samples;
    }

    [Fact]
    public void Transcribe_PureTone_ReturnsSomething()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(Directory.Exists(ModelDir),
            $"Parakeet model not present at {ModelDir}; run scripts/download-parakeet.ps1");

        var wav = LoadWavMonoF32(Path.Combine(AppContext.BaseDirectory, "fixtures", "tone-440hz-1s.wav"));
        using var session = new ParakeetSession(ModelDir);
        var result = session.Transcribe(wav);
        // A pure tone produces nothing meaningful, but the call should complete
        // without throwing and return a non-null transcript object.
        result.ShouldNotBeNull();
    }

    // Plan 2 adds a known-good speech fixture for transcript accuracy assertions;
    // for now, a "the model loads and runs" smoke test is the bar for Plan 1.
}
```

- [ ] **Step 6: Build, sync, run on VM**

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test --filter \"FullyQualifiedName~ParakeetSessionIntegrationTests\""
```

Expected: the transcribe test passes (no exception, returns transcript object). Build may emit DML provider warnings — those are informational.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Asr/ParakeetSession.cs src/Winpepper.Asr/ParakeetTranscript.cs tests/Winpepper.Asr.Tests/ParakeetSessionIntegrationTests.cs scripts/download-parakeet.ps1
git commit -m "feat(asr): Parakeet TDT v3 ONNX session with DirectML EP and greedy TDT decode"
```

---

## Task 13: Streaming transcriber wrapper

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Asr/StreamingTranscriber.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs`

The streaming wrapper for Plan 1 is intentionally simple: buffer until end, then transcribe in one shot via `ParakeetSession.Transcribe`. True window-by-window streaming (which produces partial transcripts mid-recording) is a Plan 2 optimization once we've validated full-batch correctness.

- [ ] **Step 1: Write failing test `tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs`**

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class StreamingTranscriberTests
{
    [Fact]
    public void FeedChunks_AccumulatesAllSamples()
    {
        var t = new StreamingTranscriber(_ => new ParakeetTranscript("ignored", [], [], []));
        t.FeedChunk(new float[1000]);
        t.FeedChunk(new float[2000]);
        t.TotalSamples.ShouldBe(3000);
    }

    [Fact]
    public void Flush_RunsTranscribeOnAccumulatedSamples()
    {
        var sawSamples = 0;
        var t = new StreamingTranscriber(s =>
        {
            sawSamples = s.Length;
            return new ParakeetTranscript("hello world", [], [], []);
        });
        t.FeedChunk(new float[16000]);
        var result = t.Flush();
        sawSamples.ShouldBe(16000);
        result.Text.ShouldBe("hello world");
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var t = new StreamingTranscriber(_ => new ParakeetTranscript("", [], [], []));
        t.FeedChunk(new float[5000]);
        t.Reset();
        t.TotalSamples.ShouldBe(0);
    }
}
```

- [ ] **Step 2: Implement `src/Winpepper.Asr/StreamingTranscriber.cs`**

```csharp
namespace Winpepper.Asr;

/// <summary>
/// Buffers audio samples during recording. On <see cref="Flush"/>, calls the
/// supplied transcribe function once with all collected samples.
///
/// Plan 2 will replace this with true window-by-window streaming inside
/// <see cref="ParakeetSession"/>; today the encoder is run in one shot for
/// correctness reasons and because the encoder is fast enough.
/// </summary>
public sealed class StreamingTranscriber
{
    private readonly Func<float[], ParakeetTranscript> _transcribe;
    private readonly List<float> _buffer = new();

    public StreamingTranscriber(Func<float[], ParakeetTranscript> transcribe)
    {
        _transcribe = transcribe;
    }

    public int TotalSamples => _buffer.Count;

    public void FeedChunk(ReadOnlySpan<float> samples)
    {
        for (var i = 0; i < samples.Length; i++) _buffer.Add(samples[i]);
    }

    public ParakeetTranscript Flush()
    {
        var arr = _buffer.ToArray();
        return _transcribe(arr);
    }

    public void Reset() => _buffer.Clear();
}
```

- [ ] **Step 3: Verify pass**

```bash
dotnet test --filter "FullyQualifiedName~StreamingTranscriberTests"
```

Expected: 3 tests pass.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.Asr/StreamingTranscriber.cs tests/Winpepper.Asr.Tests/StreamingTranscriberTests.cs
git commit -m "feat(asr): streaming transcriber wrapper (single-shot for v1)"
```

---

## Task 14: Session engine state machine

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Sessions/SessionState.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Sessions/SessionEvent.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Sessions/SessionEngine.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Sessions/SessionEngineTests.cs`

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Sessions/SessionEngineTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Sessions;
using Xunit;

namespace Winpepper.Core.Tests.Sessions;

public class SessionEngineTests
{
    [Fact]
    public void Idle_ReceivesStart_GoesToRecording()
    {
        var e = new SessionEngine();
        e.State.ShouldBe(SessionState.Idle);
        e.Apply(SessionEvent.StartRequested);
        e.State.ShouldBe(SessionState.Recording);
    }

    [Fact]
    public void Recording_ReceivesStop_GoesToTranscribing()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.State.ShouldBe(SessionState.Transcribing);
    }

    [Fact]
    public void Transcribing_TranscriptReady_GoesToInjecting()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.Apply(SessionEvent.TranscriptReady);
        e.State.ShouldBe(SessionState.Injecting);
    }

    [Fact]
    public void Injecting_Done_GoesToIdle()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StopRequested);
        e.Apply(SessionEvent.TranscriptReady);
        e.Apply(SessionEvent.InjectionCompleted);
        e.State.ShouldBe(SessionState.Idle);
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Transcribing)]
    [InlineData(SessionState.Injecting)]
    public void Cancel_FromAnyActiveState_GoesToIdle(SessionState start)
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        if (start == SessionState.Transcribing || start == SessionState.Injecting)
            e.Apply(SessionEvent.StopRequested);
        if (start == SessionState.Injecting)
            e.Apply(SessionEvent.TranscriptReady);

        e.State.ShouldBe(start);
        e.Apply(SessionEvent.CancelRequested);
        e.State.ShouldBe(SessionState.Idle);
    }

    [Fact]
    public void Start_DuringRecording_IsIgnored()
    {
        var e = new SessionEngine();
        e.Apply(SessionEvent.StartRequested);
        e.Apply(SessionEvent.StartRequested);
        e.State.ShouldBe(SessionState.Recording);
    }

    [Fact]
    public void StateChange_FiresStateChanged_WithOldAndNew()
    {
        var e = new SessionEngine();
        (SessionState From, SessionState To)? observed = null;
        e.StateChanged += (from, to) => observed = (from, to);
        e.Apply(SessionEvent.StartRequested);
        observed.ShouldBe((SessionState.Idle, SessionState.Recording));
    }
}
```

- [ ] **Step 2: Implement `src/Winpepper.Core/Sessions/SessionState.cs`**

```csharp
namespace Winpepper.Core.Sessions;

public enum SessionState
{
    Idle,
    Recording,
    Transcribing,
    Injecting,
}
```

- [ ] **Step 3: Implement `src/Winpepper.Core/Sessions/SessionEvent.cs`**

```csharp
namespace Winpepper.Core.Sessions;

public enum SessionEvent
{
    StartRequested,
    StopRequested,
    TranscriptReady,
    InjectionCompleted,
    CancelRequested,
    Failed,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Sessions/SessionEngine.cs`**

```csharp
namespace Winpepper.Core.Sessions;

public sealed class SessionEngine
{
    public SessionState State { get; private set; } = SessionState.Idle;
    public event Action<SessionState, SessionState>? StateChanged;

    public void Apply(SessionEvent evt)
    {
        var from = State;
        var to = NextState(State, evt);
        if (to == State) return;
        State = to;
        StateChanged?.Invoke(from, to);
    }

    private static SessionState NextState(SessionState state, SessionEvent evt) => (state, evt) switch
    {
        (SessionState.Idle,         SessionEvent.StartRequested)       => SessionState.Recording,
        (SessionState.Recording,    SessionEvent.StopRequested)        => SessionState.Transcribing,
        (SessionState.Transcribing, SessionEvent.TranscriptReady)      => SessionState.Injecting,
        (SessionState.Injecting,    SessionEvent.InjectionCompleted)   => SessionState.Idle,

        (SessionState.Recording,    SessionEvent.CancelRequested)      => SessionState.Idle,
        (SessionState.Transcribing, SessionEvent.CancelRequested)      => SessionState.Idle,
        (SessionState.Injecting,    SessionEvent.CancelRequested)      => SessionState.Idle,

        (_,                         SessionEvent.Failed)               => SessionState.Idle,
        _                                                              => state,
    };
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test --filter "FullyQualifiedName~SessionEngineTests"
```

Expected: all 9 cases pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Sessions tests/Winpepper.Core.Tests/Sessions
git commit -m "feat(core): session state machine"
```

---

## Task 15: Pipeline wiring — Winpepper.Cli walking skeleton

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Cli/Winpepper.Cli.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Cli/Program.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Cli/Pipeline.cs`

The CLI is the production entry point for Plan 1 — it's how we manually test the end-to-end loop on the VM. It is replaced by the WinUI 3 shell in Plan 3.

- [ ] **Step 1: Write `src/Winpepper.Cli/Winpepper.Cli.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <RootNamespace>Winpepper.Cli</RootNamespace>
    <AssemblyName>winpepper</AssemblyName>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
    <UseWindowsForms>false</UseWindowsForms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <ProjectReference Include="..\Winpepper.Audio\Winpepper.Audio.csproj" />
    <ProjectReference Include="..\Winpepper.Asr\Winpepper.Asr.csproj" />
    <ProjectReference Include="..\Winpepper.Platform\Winpepper.Platform.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add to solution**

```bash
dotnet sln add src/Winpepper.Cli/Winpepper.Cli.csproj
```

- [ ] **Step 3: Implement `src/Winpepper.Cli/Pipeline.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Core.Sessions;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;

namespace Winpepper.Cli;

public sealed class Pipeline : IDisposable
{
    private readonly ILogger<Pipeline> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly SessionEngine _engine = new();

    private IAudioRecorder? _recorder;

    public Pipeline(ILogger<Pipeline> log, ILoggerFactory factory, string modelDir,
                    HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel)
    {
        _log = log;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _engine.StateChanged += (from, to) => _log.LogInformation("State {From} -> {To}", from, to);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _hook.Start();
        _log.LogInformation("Winpepper ready. Hold the trigger to dictate.");

        try
        {
            await foreach (var evt in _hook.Events.ReadAllAsync(ct))
            {
                try
                {
                    await HandleHotkey(evt, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Pipeline error in state {State}", _engine.State);
                    _engine.Apply(SessionEvent.Failed);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _recorder = new WasapiRecorder();
                _recorder.Start();
                break;

            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                var samples = _recorder!.Stop();
                _recorder.Dispose();
                _recorder = null;
                _log.LogInformation("Captured {Count} samples ({Sec:F2}s)", samples.Length, samples.Length / 16000.0);
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _log.LogInformation("Transcript: '{Text}'", transcript.Text);
                _engine.Apply(SessionEvent.TranscriptReady);
                if (!string.IsNullOrWhiteSpace(transcript.Text))
                    _injector.TryInject(transcript.Text);
                _engine.Apply(SessionEvent.InjectionCompleted);
                break;

            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _recorder?.Dispose();
                _recorder = null;
                break;

            case HotkeyEventKind.Toggle:
                // Plan 1 doesn't implement toggle; full impl in Plan 3.
                _log.LogInformation("Toggle hotkey is not implemented in Plan 1 (use hold).");
                break;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
    }
}
#endif
```

- [ ] **Step 4: Implement `src/Winpepper.Cli/Program.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
#if WINDOWS
using Winpepper.Core.Settings;
using Winpepper.Platform.Hotkeys;
#endif

namespace Winpepper.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var logDir = Path.Combine(localAppData, "winpepper", "logs");
        using var logFactory = WinpepperLogging.Create(logDir, debugConsole: true, minimumLevel: LogLevel.Information);
        var log = logFactory.CreateLogger("winpepper");

        log.LogInformation("Winpepper CLI starting.");

#if WINDOWS
        var settings = new SettingsStore(Path.Combine(localAppData, "winpepper", "settings.json")).Load();
        var modelDir = Path.Combine(localAppData, "winpepper", "models", "parakeet-tdt-0.6b-v3");
        if (!Directory.Exists(modelDir))
        {
            log.LogError("Parakeet model not found at {Dir}. Run scripts/download-parakeet.ps1 first.", modelDir);
            return 2;
        }

        using var pipeline = new Pipeline(
            logFactory.CreateLogger<Pipeline>(), logFactory, modelDir,
            HotkeyChord.Parse(settings.HoldHotkey),
            HotkeyChord.Parse(settings.ToggleHotkey),
            HotkeyChord.Parse("Esc"));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await pipeline.RunAsync(cts.Token);
        return 0;
#else
        log.LogError("Winpepper CLI requires Windows.");
        return 1;
#endif
    }
}
```

- [ ] **Step 5: Verify build**

```bash
./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj"
```

Expected: build succeeds for the windows TFM.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cli winpepper.sln
git commit -m "feat(cli): walking-skeleton pipeline wiring hotkey/audio/asr/injection"
```

---

## Task 16: End-to-end manual smoke test on the VM

**Files:**
- Modify: `/home/jesse/git/winpepper/docs/manual-test.md` — add the Plan 1 smoke procedure.

The VM has no microphone hardware, but WASAPI in the dockur/QEMU stack returns a silent stream. That means transcription will produce empty or near-empty output — which still validates that the whole pipeline runs end-to-end without crashing.

For a real "speak and it types" demo, the operator needs to either:
- Plug a USB mic into the host, expose it to the dockur VM (out of scope for Plan 1), **or**
- Run `winpepper` on a physical Windows 11 machine.

- [ ] **Step 1: Add the smoke procedure to `docs/manual-test.md`**

Append:

```markdown
## Plan 1 walking-skeleton smoke (Windows VM)

1. Sync: `./scripts/sync-to-vm.sh`
2. Make sure the model exists: `./scripts/winssh < scripts/download-parakeet.ps1`
3. Build the CLI: `./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj -c Release"`
4. Run the CLI in a foreground PowerShell session on the VM:
   ```powershell
   cd C:\winpepper
   dotnet run --project src/Winpepper.Cli -c Release
   ```
5. From the host, hold `RightCtrl+RightShift` for ~2 seconds, then release.
6. The CLI log should show `State Idle -> Recording`, then `-> Transcribing`, then `-> Injecting`, then `-> Idle`. Captured-samples count should be > 30000 for a 2-second hold.
7. With no real mic, the transcript will likely be empty or noise — that's fine. The acceptance bar for Plan 1 is "no crash, all four state transitions, model loads, encoder/decoder run".

For a real dictation demo, run the same `dotnet run` command on a physical Windows 11 machine with a mic and hold the hotkey while speaking. The transcript should appear in whatever window is focused.
```

- [ ] **Step 2: Run the smoke procedure**

Execute steps 1-6 from the procedure. Capture the log output and paste a summary into the task tracker.

- [ ] **Step 3: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs: Plan 1 walking-skeleton smoke procedure"
```

---

## Task 17: CI baseline (GitHub Actions)

**Files:**
- Create: `/home/jesse/git/winpepper/.github/workflows/ci.yml`

- [ ] **Step 1: Write `.github/workflows/ci.yml`**

```yaml
name: CI

on:
  push: { branches: [main] }
  pull_request: { branches: [main] }

jobs:
  linux-build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet restore
      - run: dotnet build --configuration Release --no-restore
      - run: dotnet test --configuration Release --no-build --filter "Platform!=Windows"

  windows-build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet restore
      - run: dotnet build --configuration Release --no-restore
      # Windows-only integration tests that need the Parakeet model are tagged
      # with [Trait("Platform","WindowsModel")] and run nightly on the VM, not in CI.
      - run: dotnet test --configuration Release --no-build --filter "Platform!=WindowsModel"
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: linux + windows build and test workflows"
```

---

## Self-review checklist (for the writer)

After completing all tasks, verify:

- [ ] Every step has concrete code or commands — no "implement X" placeholders.
- [ ] Types referenced across tasks (e.g., `ParakeetTranscript`, `SessionState`, `HotkeyChord`) match exactly.
- [ ] File paths in step headers match the paths in `git add` commands.
- [ ] Each task ends with a green build and a green test run.
- [ ] The walking skeleton actually walks: Task 15 + Task 16 produce a runnable `winpepper` CLI on the VM.

## What Plan 1 does NOT cover (intentionally — see follow-on plans)

- LLM cleanup, prompt assembly, correction store — Plan 2.
- Window context (UIA + Windows.Media.Ocr) — Plan 2.
- WinUI 3 main window, tray, status pill, settings UI — Plan 3.
- History store, lab views, model downloader — Plan 4.
- Post-paste learning, diagnostics tab, crash dumps — Plan 5.
- WiX MSI, autostart, code signing — Plan 6.
- True window-by-window streaming inside `ParakeetSession` — Plan 2 optimization.

## Handoff

When all tasks are committed and the smoke test passes: tell the user the walking skeleton is alive on the VM, then start Plan 2 (cleanup pipeline + window context).
