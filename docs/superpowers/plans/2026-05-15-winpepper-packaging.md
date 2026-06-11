# Plan 6 — WiX MSI Packaging, Autostart, Code Signing, CI Nightly

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a per-machine `winpepper-<version>-x64.msi` installer that drops the app binaries into `C:\Program Files\Winpepper\`, registers a Start menu shortcut and Programs and Features entry, writes the per-user `HKCU\…\Run\Winpepper` autostart value on fresh install only, enforces Windows 11 22H2+ via `LaunchCondition`, warns at install if DirectX 12 is missing, and invokes the WinAppSDK bootstrapper when the runtime is absent. Version stamping flows through `Nerdbank.GitVersioning` from `version.json`. A `packaging/sign.ps1` wrapper performs optional EV code signing (off by default in dev/CI), and the About dialog surfaces `unsigned build` when the binary is unsigned. A nightly GitHub Actions workflow on the Windows runner builds the MSI, performs `msiexec /qn` install + smoke + uninstall, and uploads the artifact.

**Architecture:** A new `packaging/` MSBuild project (`packaging/Winpepper.Msi.wixproj`) uses the WiX v5 SDK (`WixToolset.Sdk` package) to compile `packaging/winpepper.wxs` into the MSI. The wixproj depends on `Winpepper.App` so that `dotnet publish src/Winpepper.App` materialises the unpackaged `Winpepper.exe` plus its WinAppSDK self-contained runtime under `src/Winpepper.App/bin/Release/net9.0-windows10.0.19041.0/win-x64/publish/`; the wxs file harvests that directory via a `HarvestDirectory` item. Versioning is wired through `Nerdbank.GitVersioning` (`version.json` → `MajorMinorPatch` MSBuild property → `<Package Version="…">` and `AssemblyInformationalVersion`). The autostart `Run` value is written by a WiX `RegistryValue` component scheduled under a `Condition` of `NOT Installed AND NOT UPGRADINGPRODUCTCODE` so upgrades never overwrite a user's toggled-off state. DirectX 12 capability is checked with a custom `D3D12CheckFeatureSupport` P/Invoke binary inside the MSI (a tiny `Winpepper.D3D12Probe` console exe shipped only inside the MSI's `binary` table). WinAppSDK bootstrapper invocation is performed by a deferred custom action that runs `WindowsAppRuntimeInstall.exe /quiet` from the MSI's binary table when the bootstrapper's API reports the runtime missing. `sign.ps1` invokes `signtool.exe sign /sha1 <thumbprint>` or `/f <pfx> /p <password>`; both `Winpepper.exe` and the final MSI are signed when a thumbprint or PFX path is provided. Without those env vars, signing is skipped and a `WINPEPPER_UNSIGNED_BUILD=1` MSBuild constant is baked into `Winpepper.App` so the About dialog displays `unsigned build`. The nightly CI workflow targets `windows-latest`, publishes `Winpepper.App`, builds the MSI, installs it under `msiexec /qn`, runs `winpepper.exe --selftest` (a single-shot Idle-state probe added in Task 4), uninstalls, and uploads the MSI as an artifact.

**Tech Stack:** WiX Toolset v5 (`WixToolset.Sdk` 5.0.2), `Microsoft.WindowsAppSDK` 1.6.241114003 (bootstrapper shim), `Nerdbank.GitVersioning` 3.6.146, `signtool.exe` (from Windows SDK, already present on `windows-latest`), `msiexec.exe` (built-in), GitHub Actions (`windows-latest`).

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md) — §11 (Packaging), §7.7 (Autostart), §10.4 (CI nightly).

**Prerequisites:**

- **Plan 1** (`plan-1/foundation`) — solution scaffolding, `Winpepper.Core` (AtomicFile, logging), `Winpepper.Cli`, the VM scripts in `scripts/` (`winrun`, `winssh`, `sync-to-vm.sh`).
- **Plan 2** — `Winpepper.Cleanup.CleanupRunner`, `Winpepper.Corrections.CorrectionStore`, `Winpepper.Platform.WindowContext`.
- **Plan 3** — `Winpepper.App` (WinUI 3 packaged process), `Winpepper.App.Program.Main` accepts `--tray`, `Winpepper.Platform.Autostart.AutostartRegistry` writes HKCU `Software\Microsoft\Windows\CurrentVersion\Run\Winpepper`. The MSI writes the same value verbatim so Settings toggles operate on the same registry entry.
- **Plan 4** — `Winpepper.History`, `Winpepper.Models`. The Lab uses MSI-installed binaries during nightly install smoke.
- **Plan 5** — Diagnostics tab + crash dump infrastructure. The MSI ships the same binaries; the Diagnostics tab reads `AssemblyInformationalVersion` populated by `Nerdbank.GitVersioning`.

**Known carry-forward block: `Winpepper.App` does not build on the VM.** Plans 3, 4, and 5 all milestone-committed without a working `Winpepper.App` build because of a WinAppSDK 1.6/1.7 + .NET 9 XAML markup compiler `PlatformNotSupportedException` (`RuntimeEnvironment.GetRuntimeInterfaceAsObject`). Plan 6 cannot produce a runnable `winpepper-<version>-x64.msi` until that block is resolved upstream. **This plan is still completable end-to-end** — the wxs source, wixproj, custom-action binaries, `sign.ps1`, and CI workflow are all written, lint-validated (`wix build --bindFiles -outputType Package` or equivalent), and committed. The single task that depends on `Winpepper.App` actually building (Task 11 — full MSI build + install smoke on the VM) is explicitly flagged. The nightly CI workflow (Task 13) is written in a form that runs through `dotnet publish src/Winpepper.App` and will surface the WinUI failure as a CI failure on its first nightly run; that failure is the trigger for fixing the WinUI block, not a Plan 6 defect. Plan 6's deliverables — the WiX source, signing wrapper, autostart MSI logic, and CI workflow — are otherwise independent of XAML markup compilation.

**Repo root throughout the plan:** `$REPO_ROOT/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced via `scripts/sync-to-vm.sh`).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Run it and confirm it fails. Implement. Run it and confirm it passes. Commit. For wxs source — the "test" is `wix build` or `dotnet build packaging/Winpepper.Msi.wixproj` producing a valid `.msi` (or failing with a precise WIX error code when the wxs is malformed). Author the failing wxs first, watch it fail, then fix.

**Commits.** One commit per task at minimum. Smaller commits within a task are fine. Always end a task with a green build on Linux for cross-platform parts (`dotnet build packaging/Winpepper.Msi.wixproj` works from Linux because the WiX SDK ships a managed compiler), and a green VM smoke for Windows-only parts (`./scripts/winrun "dotnet build packaging\Winpepper.Msi.wixproj"`).

**Building MSIs from Linux.** The WiX v5 SDK is fully managed .NET and runs on Linux. `dotnet build packaging/Winpepper.Msi.wixproj` produces a `.msi` on Linux. Installing the MSI requires Windows, but compiling it does not. The CI nightly job runs on Windows because it needs `msiexec /i` for the install smoke.

**SSH conventions** (Windows VM): use the existing `./scripts/winssh` and `./scripts/winrun` wrappers from Plan 1.

**File path for the MSI output:** `artifacts/winpepper-<version>-x64.msi`. The `artifacts/` directory is gitignored from Plan 1.

---

## Task 1: Add `Nerdbank.GitVersioning` and `version.json`

**Files:**
- Create: `$REPO_ROOT/version.json`
- Modify: `$REPO_ROOT/Directory.Packages.props` — add `Nerdbank.GitVersioning` package version.
- Modify: `$REPO_ROOT/Directory.Build.props` — reference the package and enable `PublicRelease` defaulting to false.
- Create: `$REPO_ROOT/tests/Winpepper.Core.Tests/VersionStampTests.cs`

- [ ] **Step 1: Write `version.json`**

```json
{
  "$schema": "https://raw.githubusercontent.com/dotnet/Nerdbank.GitVersioning/main/src/NerdBank.GitVersioning/version.schema.json",
  "version": "0.6.0-alpha",
  "publicReleaseRefSpec": [
    "^refs/heads/main$",
    "^refs/tags/v\\d+\\.\\d+\\.\\d+$"
  ],
  "cloudBuild": {
    "buildNumber": { "enabled": true },
    "setVersionVariables": true
  },
  "release": {
    "branchName": "release/v{version}",
    "versionIncrement": "minor",
    "firstUnstableTag": "alpha"
  }
}
```

- [ ] **Step 2: Add `Nerdbank.GitVersioning` to `Directory.Packages.props`**

Append inside the existing `<ItemGroup>`:

```xml
    <!-- Plan 6: versioning + packaging -->
    <PackageVersion Include="Nerdbank.GitVersioning" Version="3.6.146" />
    <PackageVersion Include="WixToolset.Sdk" Version="5.0.2" />
    <PackageVersion Include="WixToolset.UI.wixext" Version="5.0.2" />
    <PackageVersion Include="WixToolset.Util.wixext" Version="5.0.2" />
```

- [ ] **Step 3: Reference `Nerdbank.GitVersioning` from `Directory.Build.props`**

Append a new `<ItemGroup>` block under the existing `<PropertyGroup>` blocks:

```xml
  <ItemGroup Condition="'$(MSBuildProjectExtension)' == '.csproj' Or '$(MSBuildProjectExtension)' == '.wixproj'">
    <PackageReference Include="Nerdbank.GitVersioning" PrivateAssets="all" />
  </ItemGroup>
```

- [ ] **Step 4: Write the failing test `tests/Winpepper.Core.Tests/VersionStampTests.cs`**

```csharp
using System.Reflection;
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class VersionStampTests
{
    [Fact]
    public void AssemblyInformationalVersion_IsNotEmpty()
    {
        var asm = typeof(HelloWinpepper).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        info.ShouldNotBeNull();
        info!.InformationalVersion.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AssemblyVersion_MatchesMajorMinorPatchFromVersionJson()
    {
        // version.json declares 0.6.0-alpha; Nerdbank.GitVersioning sets AssemblyVersion
        // to 0.6.0.{git-height}. We only assert the major/minor/build prefix.
        var v = typeof(HelloWinpepper).Assembly.GetName().Version!;
        v.Major.ShouldBe(0);
        v.Minor.ShouldBe(6);
        v.Build.ShouldBe(0);
    }
}
```

- [ ] **Step 5: Run the test and confirm it fails**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~VersionStampTests"
```

Expected: `VersionStampTests.AssemblyVersion_MatchesMajorMinorPatchFromVersionJson` fails because the current `Winpepper.Core.dll` has the default `0.0.0.0` version (no `Nerdbank.GitVersioning` reference yet wired through the build).

- [ ] **Step 6: Restore and rebuild to pick up `Nerdbank.GitVersioning`**

```bash
dotnet restore
dotnet build src/Winpepper.Core/Winpepper.Core.csproj -c Release
```

- [ ] **Step 7: Run the test and confirm it passes**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~VersionStampTests"
```

Expected: both `VersionStampTests` pass. The version is `0.6.0.<git-height>` and the informational version includes the short commit SHA (e.g., `0.6.0-alpha.5+g1a2b3c4`).

- [ ] **Step 8: Commit**

```bash
git add version.json Directory.Packages.props Directory.Build.props tests/Winpepper.Core.Tests/VersionStampTests.cs
git commit -m "build: add Nerdbank.GitVersioning with version.json (0.6.0-alpha)"
```

---

## Task 2: Bake `unsigned build` marker into `Winpepper.App`

**Files:**
- Modify: `$REPO_ROOT/Directory.Build.props` — read `WINPEPPER_SIGNED` env var, set `DefineConstants` accordingly.
- Create: `$REPO_ROOT/src/Winpepper.Core/BuildSignature.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Core.Tests/BuildSignatureTests.cs`

The About dialog (added in Plan 3) calls into `BuildSignature.Describe()` to render either `0.6.0-alpha.5+g1a2b3c4` or `0.6.0-alpha.5+g1a2b3c4 (unsigned build)`. The unsigned suffix appears whenever the build host did not export `WINPEPPER_SIGNED=1`.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/BuildSignatureTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class BuildSignatureTests
{
    [Fact]
    public void Describe_IncludesAssemblyInformationalVersion()
    {
        var s = BuildSignature.Describe();
        s.ShouldContain("0.6.0");
    }

    [Fact]
    public void Describe_FlagsUnsignedBuildWhenSignedConstantAbsent()
    {
        // Default dev build is unsigned; constant WINPEPPER_SIGNED is not defined.
        var s = BuildSignature.Describe();
        s.ShouldContain("(unsigned build)");
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~BuildSignatureTests"
```

Expected: type `BuildSignature` not found — compile error.

- [ ] **Step 3: Write `src/Winpepper.Core/BuildSignature.cs`**

```csharp
using System.Reflection;

namespace Winpepper.Core;

public static class BuildSignature
{
    public static string Describe()
    {
        var asm = typeof(BuildSignature).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = info?.InformationalVersion ?? "0.0.0";
#if WINPEPPER_SIGNED
        return version;
#else
        return $"{version} (unsigned build)";
#endif
    }

    public static bool IsSigned =>
#if WINPEPPER_SIGNED
        true;
#else
        false;
#endif
}
```

- [ ] **Step 4: Wire the `WINPEPPER_SIGNED` env var into `Directory.Build.props`**

Append a `<PropertyGroup>` to `Directory.Build.props` immediately after the existing primary `<PropertyGroup>`:

```xml
  <PropertyGroup>
    <WinpepperSigned Condition="'$(WINPEPPER_SIGNED)' == '1'">true</WinpepperSigned>
    <DefineConstants Condition="'$(WinpepperSigned)' == 'true'">$(DefineConstants);WINPEPPER_SIGNED</DefineConstants>
  </PropertyGroup>
```

- [ ] **Step 5: Run the test and confirm both cases pass**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~BuildSignatureTests"
```

Expected: both tests pass.

- [ ] **Step 6: Verify the "signed" branch by exporting the env var**

```bash
WINPEPPER_SIGNED=1 dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
  --filter "FullyQualifiedName~BuildSignatureTests.Describe_IncludesAssemblyInformationalVersion"
```

Expected: pass. The unsigned-flag test obviously fails under `WINPEPPER_SIGNED=1` — that's the inverse path of the same code and is asserted in Task 9 once `sign.ps1` exists.

- [ ] **Step 7: Commit**

```bash
git add Directory.Build.props src/Winpepper.Core/BuildSignature.cs tests/Winpepper.Core.Tests/BuildSignatureTests.cs
git commit -m "feat(core): BuildSignature.Describe surfaces (unsigned build) marker"
```

---

## Task 3: Bind the About dialog to `BuildSignature.Describe`

**Files:**
- Modify: `$REPO_ROOT/src/Winpepper.App/Views/MainWindow.xaml.cs` (or wherever Plan 3 placed the About dialog) — replace the literal version string with `BuildSignature.Describe()`.
- Create: `$REPO_ROOT/tests/Winpepper.Core.Tests/AboutTextTests.cs` — pure-string assertion against a small `AboutText` helper (avoids needing a WinUI test host).

Because `Winpepper.App` does not currently build on the VM (see plan header), the About-page edit lands in a small pure helper in `Winpepper.Core` and a single-line call site in the XAML code-behind. The helper is testable on Linux; the call site change compiles whenever the WinUI block is resolved.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/AboutTextTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class AboutTextTests
{
    [Fact]
    public void Title_StartsWithProductName()
    {
        AboutText.Title.ShouldStartWith("Winpepper");
    }

    [Fact]
    public void Body_ContainsVersionAndUnsignedMarker()
    {
        var body = AboutText.Body();
        body.ShouldContain("Version");
        body.ShouldContain("0.6.0");
        body.ShouldContain("(unsigned build)");
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~AboutTextTests"
```

Expected: compile failure — `AboutText` not found.

- [ ] **Step 3: Write `src/Winpepper.Core/AboutText.cs`**

```csharp
namespace Winpepper.Core;

public static class AboutText
{
    public const string Title = "Winpepper";

    public static string Body() =>
        $"Winpepper local dictation for Windows 11.\n" +
        $"Version {BuildSignature.Describe()}\n" +
        $"Companion to pepper-x (Linux/GNOME).\n" +
        $"Local-only. No cloud, no telemetry.";
}
```

- [ ] **Step 4: Run and confirm pass**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~AboutTextTests"
```

Expected: both pass.

- [ ] **Step 5: Wire `AboutText` into the WinUI About dialog**

Locate the existing About dialog handler in `src/Winpepper.App/Views/MainWindow.xaml.cs` (or wherever Plan 3 put it; if no About dialog exists yet, add one as a `MenuFlyoutItem` under the existing settings flyout). Replace the body literal:

```csharp
// In the About dialog click handler (file: src/Winpepper.App/Views/MainWindow.xaml.cs):
private async void OnAboutClick(object sender, RoutedEventArgs e)
{
    var dialog = new ContentDialog
    {
        Title = Winpepper.Core.AboutText.Title,
        Content = Winpepper.Core.AboutText.Body(),
        CloseButtonText = "Close",
        XamlRoot = this.Content.XamlRoot
    };
    await dialog.ShowAsync();
}
```

- [ ] **Step 6: Best-effort build verify (will likely fail with the WinUI block — that's OK)**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Release"
```

Expected: either build succeeds (the WinUI block has been resolved), or it fails with the same `RuntimeEnvironment.GetRuntimeInterfaceAsObject` PlatformNotSupportedException Plan 3 documented. Either is acceptable for this plan — the About dialog wiring is plumbing that activates whenever the WinUI build comes back.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/AboutText.cs tests/Winpepper.Core.Tests/AboutTextTests.cs src/Winpepper.App/Views/MainWindow.xaml.cs
git commit -m "feat(app): About dialog reads BuildSignature.Describe"
```

---

## Task 4: Add `--selftest` to `Winpepper.App.Program`

The CI nightly install smoke needs a way to invoke `winpepper.exe` after MSI install, verify the process starts, reaches the Idle state, and exits cleanly, without putting up any UI or installing models. The lowest-risk way is a `--selftest` argument that constructs the `SessionEngine` (added in Plan 1), confirms it boots to `Idle`, prints `WINPEPPER_SELFTEST_OK`, and exits with code 0.

**Files:**
- Modify: `$REPO_ROOT/src/Winpepper.App/Program.cs` — add `--selftest` branch ahead of the WinUI bootstrap.
- Create: `$REPO_ROOT/src/Winpepper.App/Selftest.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Core.Tests/SelftestProbeTests.cs` (the pure logic lives in `Winpepper.Core.SelftestProbe`; the `Winpepper.App.Selftest` class is a thin shim that calls it).
- Create: `$REPO_ROOT/src/Winpepper.Core/SelftestProbe.cs`

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/SelftestProbeTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core;
using Xunit;

namespace Winpepper.Core.Tests;

public class SelftestProbeTests
{
    [Fact]
    public void Run_ReturnsZero_AndEmitsExpectedToken()
    {
        var sb = new System.Text.StringBuilder();
        var code = SelftestProbe.Run(line => sb.AppendLine(line));
        code.ShouldBe(0);
        sb.ToString().ShouldContain("WINPEPPER_SELFTEST_OK");
        sb.ToString().ShouldContain(BuildSignature.Describe());
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~SelftestProbeTests"
```

Expected: compile failure — `SelftestProbe` not found.

- [ ] **Step 3: Write `src/Winpepper.Core/SelftestProbe.cs`**

```csharp
namespace Winpepper.Core;

public static class SelftestProbe
{
    /// <summary>
    /// Returns 0 if the core data files Winpepper expects on first launch can be reached,
    /// the version string is non-empty, and the (no-op) state machine smoke succeeds.
    /// Writes a single-line WINPEPPER_SELFTEST_OK token plus diagnostic lines to <paramref name="emit"/>.
    /// </summary>
    public static int Run(Action<string> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);

        emit($"winpepper selftest");
        emit($"build: {BuildSignature.Describe()}");
        emit($"signed: {BuildSignature.IsSigned}");

        // Verify %LOCALAPPDATA% is reachable; create the winpepper subtree if absent.
        // The MSI does NOT pre-create the models dir — first run does.
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            emit("FAIL: LocalApplicationData is empty");
            return 2;
        }
        var winpepperRoot = Path.Combine(localAppData, "winpepper");
        Directory.CreateDirectory(winpepperRoot);
        Directory.CreateDirectory(Path.Combine(winpepperRoot, "models"));
        emit($"localappdata: {winpepperRoot}");

        emit("WINPEPPER_SELFTEST_OK");
        return 0;
    }
}
```

- [ ] **Step 4: Run and confirm pass**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~SelftestProbeTests"
```

Expected: pass.

- [ ] **Step 5: Add the `--selftest` branch to `src/Winpepper.App/Program.cs`**

Replace the existing `Main` body in `$REPO_ROOT/src/Winpepper.App/Program.cs` with the version below. The selftest branch runs *before* any WinUI/WinRT init so it does not depend on the WinUI block being resolved.

```csharp
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Winpepper.App;
using Winpepper.Core;

namespace Winpepper.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return SelftestProbe.Run(Console.WriteLine);
        }

        // Autostart hand-off: --tray means start hidden to the tray.
        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("WINPEPPER_START_HIDDEN", startHidden ? "1" : "0");

        // Single-instance handshake. If a sibling is already running, redirect
        // activation and exit.
        var key = "Winpepper-singleton";
        var instance = AppInstance.FindOrRegisterForKey(key);
        if (!instance.IsCurrent)
        {
            var current = AppInstance.GetCurrent();
            instance.RedirectActivationToAsync(current.GetActivatedEventArgs()).AsTask().Wait();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start((p) =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App();
        });
        return 0;
    }
}
```

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/SelftestProbe.cs tests/Winpepper.Core.Tests/SelftestProbeTests.cs src/Winpepper.App/Program.cs
git commit -m "feat(app): --selftest probe for MSI install smoke"
```

---

## Task 5: Create the `packaging/` directory and the wixproj skeleton

**Files:**
- Create: `$REPO_ROOT/packaging/Winpepper.Msi.wixproj`
- Create: `$REPO_ROOT/packaging/winpepper.wxs` (empty stub for now; populated in Tasks 6 and 7)
- Modify: `$REPO_ROOT/winpepper.sln` — add the wixproj.
- Create: `$REPO_ROOT/packaging/.gitignore` — `*.wixobj`, `*.wixpdb`.

- [ ] **Step 1: Create `packaging/.gitignore`**

```gitignore
*.wixobj
*.wixpdb
*.wixmsp
```

- [ ] **Step 2: Write the wixproj stub `packaging/Winpepper.Msi.wixproj`**

```xml
<Project Sdk="WixToolset.Sdk">
  <PropertyGroup>
    <OutputName>winpepper-$(Version)-x64</OutputName>
    <OutputType>Package</OutputType>
    <InstallerPlatform>x64</InstallerPlatform>
    <SuppressIces>ICE60</SuppressIces>
    <DefineConstants>AppPublishDir=$(MSBuildThisFileDirectory)..\src\Winpepper.App\bin\$(Configuration)\net9.0-windows10.0.19041.0\win-x64\publish</DefineConstants>
    <OutputPath>$(MSBuildThisFileDirectory)..\artifacts\</OutputPath>
    <IntermediateOutputPath>$(MSBuildThisFileDirectory)obj\$(Configuration)\</IntermediateOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WixToolset.UI.wixext" />
    <PackageReference Include="WixToolset.Util.wixext" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\src\Winpepper.App\Winpepper.App.csproj">
      <DoNotHarvest>true</DoNotHarvest>
      <Private>false</Private>
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
    </ProjectReference>
  </ItemGroup>
</Project>
```

ICE60 (font-installation check on non-installed-font files) is suppressed because we ship no fonts and the rule sometimes false-positives on harvested directories. All other ICEs remain on.

- [ ] **Step 3: Write an empty stub `packaging/winpepper.wxs`**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <!-- Real content lands in Task 6. Empty stub so the wixproj can lint. -->
  <Package Name="Winpepper" Manufacturer="Winpepper" Version="0.0.0.1" UpgradeCode="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed01">
    <SummaryInformation Description="Winpepper installer stub" />
    <MediaTemplate EmbedCab="yes" />
    <Feature Id="Main" Title="Winpepper" Level="1">
      <Component Id="StubComponent" Directory="INSTALLFOLDER" Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed02">
        <RegistryValue Root="HKLM" Key="Software\Winpepper" Name="Installed" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </Feature>
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="Winpepper" />
    </StandardDirectory>
  </Package>
</Wix>
```

- [ ] **Step 4: Add the wixproj to the solution**

```bash
cd $REPO_ROOT
dotnet sln add packaging/Winpepper.Msi.wixproj
```

- [ ] **Step 5: Restore and lint-build the wixproj on Linux**

```bash
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore packaging/Winpepper.Msi.wixproj
dotnet build packaging/Winpepper.Msi.wixproj -c Release
```

Expected: a stub `artifacts/winpepper-0.6.0.<height>-x64.msi` is produced. If the WinUI block prevents `Winpepper.App` from compiling, the wixproj should still build because `<ReferenceOutputAssembly>false</ReferenceOutputAssembly>` decouples the package build from the app build. The harvested directory will be empty at this stage — that is intentional. The `.msi` exists but installs only the stub registry value.

- [ ] **Step 6: Commit**

```bash
git add packaging/Winpepper.Msi.wixproj packaging/winpepper.wxs packaging/.gitignore winpepper.sln
git commit -m "build(packaging): wixproj + stub winpepper.wxs skeleton"
```

---

## Task 6: Author the real `winpepper.wxs` — directories, files, shortcut, ARP entry

**Files:**
- Modify: `$REPO_ROOT/packaging/winpepper.wxs` — replace the stub with the real wxs.

The MSI installs to `C:\Program Files\Winpepper\` (per-machine, x64). It harvests the entire publish directory of `Winpepper.App` (the app exe plus the WinAppSDK self-contained runtime). It creates a Start menu shortcut `Winpepper.lnk` pointing at `[INSTALLFOLDER]Winpepper.exe`. It registers an entry in Programs and Features through the standard `ARP*` properties.

`MajorUpgrade` is configured with `AllowDowngrades="no"` and `Schedule="afterInstallInitialize"` per spec §11.

- [ ] **Step 1: Overwrite `packaging/winpepper.wxs`**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui"
     xmlns:util="http://wixtoolset.org/schemas/v4/wxs/util">

  <?define ProductName = "Winpepper" ?>
  <?define Manufacturer = "Winpepper" ?>
  <?define UpgradeCodeGuid = "6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed01" ?>

  <Package Name="$(ProductName)"
           Manufacturer="$(Manufacturer)"
           Version="!(bind.FileVersion.WinpepperExe)"
           UpgradeCode="$(UpgradeCodeGuid)"
           Scope="perMachine"
           Compressed="yes"
           InstallerVersion="500">

    <SummaryInformation Description="Winpepper local dictation for Windows 11"
                        Manufacturer="$(Manufacturer)" />

    <MediaTemplate EmbedCab="yes" />

    <!-- Spec §11: AllowDowngrades=no, Schedule=afterInstallInitialize. -->
    <MajorUpgrade AllowDowngrades="no"
                  Schedule="afterInstallInitialize"
                  DowngradeErrorMessage="A newer version of [ProductName] is already installed." />

    <!-- ARP entry. -->
    <Property Id="ARPPRODUCTICON" Value="WinpepperIcon" />
    <Property Id="ARPHELPLINK" Value="https://github.com/jesse-michael-han/winpepper" />
    <Property Id="ARPNOREPAIR" Value="1" />
    <Icon Id="WinpepperIcon" SourceFile="$(AppPublishDir)\Assets\AppIcon.ico" />

    <!-- Spec §11: Windows 11 22H2+ via LaunchCondition. Build 22621 = 22H2.
         VersionNT64 >= 1000 enforces Windows 10/11 x64; the build-number gate (MSI_WIN_BUILD,
         set by the capability-probe CA in Task 8 from HKLM\…\CurrentBuildNumber) is the real
         22H2+ check. MSI string comparison on >= is lexicographic, so this works correctly
         only as long as CurrentBuildNumber is always a same-width number string — which it
         is on Windows 10/11 (5 digits). WindowsBuild is NOT a stock MSI/WiX property and
         must not be used here. -->
    <Launch Condition='Installed OR (VersionNT64 &gt;= 1000 AND MSI_WIN_BUILD &gt;= "22621")'
            Message="[ProductName] requires Windows 11 22H2 (build 22621) or newer." />

    <Feature Id="MainFeature" Title="$(ProductName)" Level="1" ConfigurableDirectory="INSTALLFOLDER">
      <ComponentGroupRef Id="HarvestedFiles" />
      <ComponentRef Id="StartMenuShortcut" />
      <ComponentRef Id="AutostartRunKey" />
      <ComponentRef Id="HKLMVersionStamp" />
    </Feature>

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="$(ProductName)" />
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="WinpepperStartMenu" Name="$(ProductName)" />
    </StandardDirectory>

    <!-- Start menu shortcut. -->
    <Component Id="StartMenuShortcut"
               Directory="WinpepperStartMenu"
               Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed03">
      <Shortcut Id="WinpepperShortcut"
                Name="Winpepper"
                Description="Local dictation for Windows 11"
                Target="[#WinpepperExe]"
                WorkingDirectory="INSTALLFOLDER"
                Icon="WinpepperIcon" />
      <RemoveFolder Id="RemoveWinpepperStartMenu" On="uninstall" />
      <RegistryValue Root="HKCU"
                     Key="Software\Winpepper\InstalledShortcuts"
                     Name="StartMenu"
                     Type="integer"
                     Value="1"
                     KeyPath="yes" />
    </Component>

    <!-- Autostart Run key — fresh install only (spec §7.7).
         WiX v5 requires the conditional form as a Component @Condition attribute;
         the legacy <Condition> child element was removed in v5. -->
    <Component Id="AutostartRunKey"
               Directory="INSTALLFOLDER"
               Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed04"
               Condition="NOT WIX_UPGRADE_DETECTED AND NOT UPGRADINGPRODUCTCODE">
      <RegistryValue Root="HKCU"
                     Key="Software\Microsoft\Windows\CurrentVersion\Run"
                     Name="Winpepper"
                     Type="string"
                     Value="&quot;[INSTALLFOLDER]Winpepper.exe&quot; --tray"
                     KeyPath="yes" />
    </Component>

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

    <!-- Harvest publish output. The HarvestDirectory item is added in Task 7;
         the ComponentGroup it generates is referenced above by id "HarvestedFiles". -->

    <!-- DirectX 12 warning (Task 8) and WinAppSDK bootstrapper (Task 8) are
         injected by separate Task 8 edits below. Keep this region stable. -->

    <ui:WixUI Id="WixUI_InstallDir" />
    <Property Id="WIXUI_INSTALLDIR" Value="INSTALLFOLDER" />

  </Package>
</Wix>
```

- [ ] **Step 2: Run `dotnet build` and confirm it fails on the missing `HarvestedFiles` ComponentGroup**

```bash
dotnet build packaging/Winpepper.Msi.wixproj -c Release
```

Expected: WIX error along the lines of `Unresolved reference to symbol 'WixComponentGroup:HarvestedFiles'`. That's intentional — Task 7 fills it in.

- [ ] **Step 3: Commit (the failing-by-design wxs is the TDD "red")**

```bash
git add packaging/winpepper.wxs
git commit -m "build(packaging): real wxs — dirs/shortcut/ARP/upgrade/autostart"
```

---

## Task 7: Wire `HarvestDirectory` to harvest the `Winpepper.App` publish output

**Files:**
- Modify: `$REPO_ROOT/packaging/Winpepper.Msi.wixproj` — add `HarvestDirectory` item.
- Modify: `$REPO_ROOT/src/Winpepper.App/Winpepper.App.csproj` — set `<PublishDir>` so the wxs `AppPublishDir` constant matches what `dotnet publish` produces.

The WiX 5 SDK exposes a `HarvestDirectory` MSBuild item that crawls a folder, generates a `ComponentGroup` (named via `ComponentGroupName`) referencing every file, and links it during build. The publish directory must exist at build time — the wixproj depends on the App project's publish target.

- [ ] **Step 1: Modify `packaging/Winpepper.Msi.wixproj` to add `HarvestDirectory` and the App publish step**

Replace the wixproj contents with:

```xml
<Project Sdk="WixToolset.Sdk">
  <PropertyGroup>
    <OutputName>winpepper-$(Version)-x64</OutputName>
    <OutputType>Package</OutputType>
    <InstallerPlatform>x64</InstallerPlatform>
    <SuppressIces>ICE60</SuppressIces>
    <DefineConstants>AppPublishDir=$(MSBuildThisFileDirectory)..\src\Winpepper.App\bin\$(Configuration)\net9.0-windows10.0.19041.0\win-x64\publish</DefineConstants>
    <OutputPath>$(MSBuildThisFileDirectory)..\artifacts\</OutputPath>
    <IntermediateOutputPath>$(MSBuildThisFileDirectory)obj\$(Configuration)\</IntermediateOutputPath>
    <AppPublishDir>$(MSBuildThisFileDirectory)..\src\Winpepper.App\bin\$(Configuration)\net9.0-windows10.0.19041.0\win-x64\publish</AppPublishDir>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="WixToolset.UI.wixext" />
    <PackageReference Include="WixToolset.Util.wixext" />
  </ItemGroup>
  <ItemGroup>
    <HarvestDirectory Include="$(AppPublishDir)">
      <ComponentGroupName>HarvestedFiles</ComponentGroupName>
      <DirectoryRefId>INSTALLFOLDER</DirectoryRefId>
      <SuppressRootDirectory>true</SuppressRootDirectory>
      <PreprocessorVariable>var.AppPublishDir</PreprocessorVariable>
    </HarvestDirectory>
  </ItemGroup>
  <ItemGroup>
    <BindPath Include="$(AppPublishDir)" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\src\Winpepper.App\Winpepper.App.csproj">
      <DoNotHarvest>true</DoNotHarvest>
      <Private>false</Private>
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
      <Targets>Publish</Targets>
      <Properties>Configuration=$(Configuration);RuntimeIdentifier=win-x64;SelfContained=true;PublishDir=$(AppPublishDir)\</Properties>
    </ProjectReference>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Add a bind-friendly id to the App exe so the wxs `[#WinpepperExe]` and `bind.FileVersion.WinpepperExe` resolve**

The HarvestDirectory item generates synthesised file ids by hashing the path. Override the id for `Winpepper.exe` by adding to `packaging/winpepper.wxs` an explicit harvested-file override block immediately before `<ui:WixUI …>`:

```xml
    <!-- Override harvested file id for the main exe so wxs can bind to it. -->
    <Fragment>
      <DirectoryRef Id="INSTALLFOLDER">
        <Component Id="WinpepperExeAlias" Guid="6c0b2a36-9d4f-44cf-9a3e-a3a4f0c1ed06" Bitness="always64">
          <File Id="WinpepperExe"
                Source="$(var.AppPublishDir)\Winpepper.exe"
                KeyPath="yes" />
        </Component>
      </DirectoryRef>
      <ComponentGroup Id="WinpepperExeAliasGroup">
        <ComponentRef Id="WinpepperExeAlias" />
      </ComponentGroup>
    </Fragment>
```

…and reference the alias group inside the existing `<Feature Id="MainFeature">`:

```xml
      <ComponentGroupRef Id="WinpepperExeAliasGroup" />
```

…and add `ExcludeFile` to the `HarvestDirectory` item so the harvested `ComponentGroup` does not double-install the exe:

In `packaging/Winpepper.Msi.wixproj`, replace the `HarvestDirectory` block with:

```xml
    <HarvestDirectory Include="$(AppPublishDir)">
      <ComponentGroupName>HarvestedFiles</ComponentGroupName>
      <DirectoryRefId>INSTALLFOLDER</DirectoryRefId>
      <SuppressRootDirectory>true</SuppressRootDirectory>
      <PreprocessorVariable>var.AppPublishDir</PreprocessorVariable>
      <ExcludeFiles>$(AppPublishDir)\Winpepper.exe</ExcludeFiles>
    </HarvestDirectory>
```

- [ ] **Step 3: Build the wixproj on Linux**

```bash
dotnet build packaging/Winpepper.Msi.wixproj -c Release
```

Expected outcomes (both acceptable):

- If `Winpepper.App` builds clean (WinUI block resolved): publish runs, the publish dir contains `Winpepper.exe` plus the WinAppSDK self-contained runtime, harvest generates `HarvestedFiles`, the link resolves, an `artifacts/winpepper-<version>-x64.msi` is produced. Approximate size: 80-160 MB depending on the WinAppSDK payload.
- If `Winpepper.App` build fails (current state per plan header): the `Publish` target fails, the wixproj exits with the same error. **That is the carry-forward block, not a wxs defect.** The wxs itself was linted successfully by the previous wixproj-only `dotnet build` attempt — the structural correctness of `winpepper.wxs` is verified by inspecting the build log: it should reach the `Light` (wix link) phase before failing on the missing publish output.

Run an "isolated wxs validation" that copies the publish dir's expected layout into a temp dir, so the link step actually runs to completion on Linux without needing `Winpepper.App` to build:

```bash
# Synthetic publish dir for wxs lint:
rm -rf /tmp/winpepper-publish-stub
mkdir -p /tmp/winpepper-publish-stub/Assets
echo "stub" > /tmp/winpepper-publish-stub/Winpepper.exe
echo "stub" > /tmp/winpepper-publish-stub/Microsoft.WindowsAppRuntime.dll
cp src/Winpepper.App/Assets/AppIcon.ico /tmp/winpepper-publish-stub/Assets/AppIcon.ico

dotnet build packaging/Winpepper.Msi.wixproj -c Release \
  -p:AppPublishDir=/tmp/winpepper-publish-stub \
  -p:_SkipUpstreamProjectReferences=true \
  -p:BuildProjectReferences=false
```

Expected: an `artifacts/winpepper-<version>-x64.msi` is produced from the stub publish dir. This validates that the wxs and wixproj are structurally correct, independent of whether `Winpepper.App` itself builds.

- [ ] **Step 4: Commit**

```bash
git add packaging/Winpepper.Msi.wixproj packaging/winpepper.wxs
git commit -m "build(packaging): harvest publish dir, alias exe for shortcut+bind"
```

---

## Task 8: DirectX 12 capability warning and WinAppSDK bootstrapper invocation

**Files:**
- Create: `$REPO_ROOT/packaging/probes/Winpepper.D3D12Probe.csproj`
- Create: `$REPO_ROOT/packaging/probes/Program.cs`
- Modify: `$REPO_ROOT/packaging/winpepper.wxs` — add a `Property` set by the probe via custom action, and `Launch` conditions.
- Modify: `$REPO_ROOT/packaging/Winpepper.Msi.wixproj` — build the probe alongside the MSI and include its binary.

Two prereqs from spec §11 are not pure-MSI predicates:

1. **DirectX 12.** Detected via `D3D12CreateDevice`. The MSI does NOT block install if DX12 is missing; it shows a warning and continues. The app still runs CPU.
2. **WinAppSDK runtime.** Detected via the WinAppSDK bootstrapper. If missing, the MSI invokes `WindowsAppRuntimeInstall-x64.exe /quiet` from its binary table.

Both checks live in a small standalone exe `Winpepper.D3D12Probe.exe` shipped in the MSI's binary table and invoked via a `CustomAction` with `Execute="immediate"` and `Impersonate="yes"` (the probe is a read-only capability query — no elevation needed).

- [ ] **Step 1: Write the probe csproj `packaging/probes/Winpepper.D3D12Probe.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishTrimmed>false</PublishTrimmed>
    <AssemblyName>Winpepper.D3D12Probe</AssemblyName>
    <RootNamespace>Winpepper.D3D12Probe</RootNamespace>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: Write the probe entry point `packaging/probes/Program.cs`**

The probe writes `%TEMP%\winpepper-probe.txt` with three `KEY=VALUE` lines:

- `WINPEPPER_DX12_PRESENT` — `1` if `D3D12CreateDevice` succeeds at FL 12.0, else `0`.
- `WINPEPPER_WINAPPSDK_PRESENT` — `1` if `HKLM\SOFTWARE\Microsoft\WindowsAppRuntime\Installed\1.6` exists, else `0`.
- `MSI_WIN_BUILD` — the value of `HKLM\Software\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber` (REG_SZ on every Windows since Vista; on 10/11 it's a 5-digit ASCII number like `22621`).

The `ReadProbeOutput` VBScript CA later in this task parses those three keys into MSI session properties so the LaunchCondition in `winpepper.wxs` (Task 6) can compare `MSI_WIN_BUILD >= "22621"` for the Windows 11 22H2+ gate.

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.D3D12Probe;

internal static class Program
{
    private static int Main(string[] args)
    {
        var dx12 = HasDirectX12() ? "1" : "0";
        var sdk = HasWinAppSdk() ? "1" : "0";
        var build = ReadWindowsBuildNumber() ?? "0";

        // CA mode: write properties into the MSI session by emitting key=value to
        // %TEMP%\winpepper-probe.txt — which the wxs custom action below reads via
        // a follow-up "ReadProbeOutput" CA. This avoids needing a managed CA host.
        var temp = Environment.GetEnvironmentVariable("TEMP")
                   ?? Path.GetTempPath();
        var path = Path.Combine(temp, "winpepper-probe.txt");
        File.WriteAllText(
            path,
            $"WINPEPPER_DX12_PRESENT={dx12}\r\n" +
            $"WINPEPPER_WINAPPSDK_PRESENT={sdk}\r\n" +
            $"MSI_WIN_BUILD={build}\r\n");
        return 0;
    }

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        int MinimumFeatureLevel,
        ref Guid riid,
        IntPtr ppDevice);

    private const int D3D_FEATURE_LEVEL_12_0 = 0xC000;

    private static bool HasDirectX12()
    {
        try
        {
            var iid = new Guid("189819F1-1DB6-4B57-BE54-1821339B85F7"); // ID3D12Device
            var hr = D3D12CreateDevice(IntPtr.Zero, D3D_FEATURE_LEVEL_12_0, ref iid, IntPtr.Zero);
            // S_FALSE (1) means "would succeed but no actual device was created" — that's what we want.
            return hr == 0 || hr == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasWinAppSdk()
    {
        // Probe via the public install location set by the WinAppSDK MSI.
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\WindowsAppRuntime\Installed\1.6");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads HKLM\Software\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber.
    /// Returns the raw REG_SZ string (e.g. "22621") so the MSI LaunchCondition can
    /// do a string >= compare against "22621" (same width => lexicographic order
    /// matches numeric order).
    /// </summary>
    private static string? ReadWindowsBuildNumber()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(
                Microsoft.Win32.RegistryHive.LocalMachine,
                Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion");
            var v = key?.GetValue("CurrentBuildNumber") as string;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Wire the probe into the wixproj**

Append to `packaging/Winpepper.Msi.wixproj` after the existing `<ItemGroup>` blocks:

```xml
  <ItemGroup>
    <ProjectReference Include="probes\Winpepper.D3D12Probe.csproj">
      <Private>false</Private>
      <ReferenceOutputAssembly>false</ReferenceOutputAssembly>
      <Targets>Publish</Targets>
      <Properties>Configuration=$(Configuration);RuntimeIdentifier=win-x64;SelfContained=true;PublishSingleFile=true;PublishDir=$(MSBuildThisFileDirectory)probes\bin\$(Configuration)\publish\</Properties>
    </ProjectReference>
  </ItemGroup>
  <PropertyGroup>
    <D3D12ProbeExe>$(MSBuildThisFileDirectory)probes\bin\$(Configuration)\publish\Winpepper.D3D12Probe.exe</D3D12ProbeExe>
    <WinAppSdkBootstrapper Condition="'$(WinAppSdkBootstrapper)' == ''">$(MSBuildThisFileDirectory)bootstrapper\WindowsAppRuntimeInstall-x64.exe</WinAppSdkBootstrapper>
  </PropertyGroup>
```

- [ ] **Step 4: Reference the probe binary inside `packaging/winpepper.wxs`**

Inside `<Package>`, just before `<ui:WixUI …>`, add:

```xml
    <!-- Capability probe binary. -->
    <Binary Id="D3D12ProbeBinary" SourceFile="$(var.D3D12ProbeExe)" />
    <Binary Id="WinAppSdkBootstrapperBinary" SourceFile="$(var.WinAppSdkBootstrapper)" />

    <!-- Properties the probe sets via the ReadProbeOutput CA below.
         Secure="yes" makes them transferable from the immediate to the
         deferred context if a future CA ever needs them there. -->
    <Property Id="WINPEPPER_DX12_PRESENT" Secure="yes" />
    <Property Id="WINPEPPER_WINAPPSDK_PRESENT" Secure="yes" />
    <Property Id="MSI_WIN_BUILD" Secure="yes" />

    <!-- Run probe before UI. -->
    <CustomAction Id="RunCapabilityProbe"
                  BinaryRef="D3D12ProbeBinary"
                  ExeCommand=""
                  Execute="immediate"
                  Impersonate="yes"
                  Return="ignore" />

    <!-- Install the WinAppSDK runtime if absent. -->
    <CustomAction Id="InstallWinAppSdk"
                  BinaryRef="WinAppSdkBootstrapperBinary"
                  ExeCommand="--quiet"
                  Execute="deferred"
                  Impersonate="no"
                  Return="check" />

    <!-- Show a one-time warning if DX12 is missing. -->
    <CustomAction Id="WarnNoDx12"
                  Script="vbscript"
                  Execute="immediate"
                  Return="ignore">
      <![CDATA[
        MsgBox "DirectX 12 is not available on this system. Winpepper will run on CPU; voice input will be slower. The app will still install.", 48, "Winpepper"
      ]]>
    </CustomAction>

    <!-- Read the probe's KEY=VALUE output file (written by RunCapabilityProbe)
         and copy the three keys (WINPEPPER_DX12_PRESENT, WINPEPPER_WINAPPSDK_PRESENT,
         MSI_WIN_BUILD) into MSI session properties. Generic KEY=VALUE parser, so
         any future probe key flows through without a wxs edit. -->
    <CustomAction Id="ReadProbeOutput"
                  Script="vbscript"
                  Execute="immediate"
                  Return="ignore">
      <![CDATA[
        Dim fso, f, line, kv
        Set fso = CreateObject("Scripting.FileSystemObject")
        Dim path
        path = Session.Property("TempFolder") & "winpepper-probe.txt"
        If Not fso.FileExists(path) Then
          path = Environ("TEMP") & "\winpepper-probe.txt"
        End If
        If fso.FileExists(path) Then
          Set f = fso.OpenTextFile(path, 1, False)
          Do Until f.AtEndOfStream
            line = Trim(f.ReadLine)
            kv = Split(line, "=", 2)
            If UBound(kv) = 1 Then Session.Property(kv(0)) = kv(1)
          Loop
          f.Close
        End If
      ]]>
    </CustomAction>

    <!-- The probe + ReadProbeOutput pair must run in BOTH sequences so the
         LaunchCondition's MSI_WIN_BUILD gate works under interactive (UI) AND
         silent (/qn) installs. The UI sequence's LaunchConditions evaluates
         before the execute sequence runs; without an InstallUISequence schedule,
         an interactive install would silently fail-open on the build gate. -->
    <InstallUISequence>
      <Custom Action="RunCapabilityProbe" Before="LaunchConditions" />
      <Custom Action="ReadProbeOutput"    After="RunCapabilityProbe" Before="LaunchConditions" />
    </InstallUISequence>
    <InstallExecuteSequence>
      <Custom Action="RunCapabilityProbe" Before="LaunchConditions" />
      <Custom Action="ReadProbeOutput"    After="RunCapabilityProbe" Before="LaunchConditions" />
      <Custom Action="WarnNoDx12"
              After="LaunchConditions"
              Condition="WINPEPPER_DX12_PRESENT = &quot;0&quot; AND UILevel &gt;= 4 AND NOT REMOVE" />
      <Custom Action="InstallWinAppSdk"
              Before="InstallFinalize"
              Condition="WINPEPPER_WINAPPSDK_PRESENT = &quot;0&quot; AND NOT REMOVE" />
    </InstallExecuteSequence>
```

- [ ] **Step 5: Stage a placeholder `WindowsAppRuntimeInstall-x64.exe`**

The real bootstrapper ships with the Microsoft.WindowsAppSDK NuGet at `runtimes/win-x64/native/`. The CI workflow downloads it on demand (Task 13); for local Linux builds, drop a 1-byte placeholder so wix link succeeds. The MSI built on Linux will not install correctly because of the placeholder, but the wxs link step proves out.

```bash
mkdir -p $REPO_ROOT/packaging/bootstrapper
printf "stub" > $REPO_ROOT/packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe
echo "WindowsAppRuntimeInstall-x64.exe" > $REPO_ROOT/packaging/bootstrapper/.gitignore
```

Also add `packaging/bootstrapper/` to the repo with only the `.gitignore`:

```bash
git add packaging/bootstrapper/.gitignore
```

- [ ] **Step 6: Run the probe directly on the VM and assert its output**

This test (which runs on the VM, not Linux — the probe is a `win-x64` self-contained exe) calls `Winpepper.D3D12Probe.exe` and verifies that `%TEMP%\winpepper-probe.txt` contains all three of the expected `KEY=VALUE` lines: `WINPEPPER_DX12_PRESENT`, `WINPEPPER_WINAPPSDK_PRESENT`, and `MSI_WIN_BUILD`. The MSI_WIN_BUILD value must be a non-empty 4+ digit number string (Win 10/11 builds are always 5 digits — Windows 11 22H2 is `22621`). This is the critical assertion that catches the regression the reviewer identified: a missing `CurrentBuildNumber` read would silently leave the property empty and the LaunchCondition would silently fail-open.

First sync the probe to the VM, then publish it, then run it:

```bash
./scripts/sync-to-vm.sh
./scripts/winrun "dotnet publish packaging\\probes\\Winpepper.D3D12Probe.csproj -c Release -r win-x64 --self-contained true"
./scripts/winrun "Remove-Item -ErrorAction SilentlyContinue \$env:TEMP\\winpepper-probe.txt; & 'C:\\winpepper\\packaging\\probes\\bin\\Release\\net9.0-windows10.0.19041.0\\win-x64\\publish\\Winpepper.D3D12Probe.exe'; Get-Content \$env:TEMP\\winpepper-probe.txt"
```

Expected output (file contents):

```
WINPEPPER_DX12_PRESENT=0
WINPEPPER_WINAPPSDK_PRESENT=0
MSI_WIN_BUILD=22621
```

(`WINPEPPER_DX12_PRESENT` is typically `0` in the QEMU VM with no DX12 driver; on a real Windows 11 22H2 host it's `1`. `WINPEPPER_WINAPPSDK_PRESENT` is `0` until the bootstrapper has run. `MSI_WIN_BUILD` is the only value the LaunchCondition gates on.)

Assert all three keys are present, and that `MSI_WIN_BUILD` parses to >= 22621 on the dev VM (the VM image is Windows 11 22H2 per `winpepper-vm.md`):

```bash
./scripts/winrun "\$f = Get-Content \$env:TEMP\\winpepper-probe.txt; if (\$f -notmatch 'WINPEPPER_DX12_PRESENT=') { throw 'DX12 key missing' }; if (\$f -notmatch 'WINPEPPER_WINAPPSDK_PRESENT=') { throw 'WinAppSDK key missing' }; \$buildLine = (\$f | Where-Object { \$_ -match '^MSI_WIN_BUILD=' }); if (-not \$buildLine) { throw 'MSI_WIN_BUILD key missing' }; \$build = [int](\$buildLine -replace 'MSI_WIN_BUILD=',''); if (\$build -lt 22621) { throw \"MSI_WIN_BUILD too low: \$build\" }; Write-Host \"OK: dx12 + winappsdk + build=\$build\""
```

Expected: `OK: dx12 + winappsdk + build=22621` (or whatever the VM's current build is).

When the VM is unavailable (Plan 6 may run before the VM is provisioned), skip this step and rely on Task 11 / Task 13 to surface a missing-key regression as an MSI install-time LaunchCondition failure. The Step 6 assertion is the cheapest place to catch the bug; the next-cheapest is the nightly CI install smoke (Task 13).

- [ ] **Step 7: Lint-build with the stub publish dir to verify wxs structure**

```bash
dotnet build packaging/Winpepper.Msi.wixproj -c Release \
  -p:AppPublishDir=/tmp/winpepper-publish-stub \
  -p:_SkipUpstreamProjectReferences=true \
  -p:BuildProjectReferences=false \
  -p:WinAppSdkBootstrapper=$REPO_ROOT/packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe
```

Expected: the wixproj builds the probe project (which IS Linux-Windows-cross-compilable via `EnableWindowsTargeting`), then links the wxs. Output: `artifacts/winpepper-<version>-x64.msi`. Inspect with `7z l artifacts/winpepper-*-x64.msi | head -40` to confirm the probe exe and bootstrapper placeholder are embedded.

- [ ] **Step 8: Commit**

```bash
git add packaging/probes packaging/winpepper.wxs packaging/Winpepper.Msi.wixproj packaging/bootstrapper/.gitignore
git commit -m "build(packaging): DX12 + WinAppSDK + Win11-build probe + CAs"
```

---

## Task 9: Write `packaging/sign.ps1`

**Files:**
- Create: `$REPO_ROOT/packaging/sign.ps1`
- Create: `$REPO_ROOT/tests/Winpepper.Core.Tests/SignScriptTests.cs` — pure-text test that asserts the script's behavior contract on a small structural level (the real signing exercise lives in the manual-test doc; no signtool runs in CI).

`sign.ps1` accepts either `-Thumbprint <sha1>` or `-PfxPath <path> -PfxPassword <password>` and signs every input file with `signtool sign`. With no args, it prints `WINPEPPER_SIGNING_DISABLED` and returns 0. Plan 6 keeps signing off in dev/CI by default — the script only does work when invoked explicitly by the release pipeline.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/SignScriptTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Core.Tests;

public class SignScriptTests
{
    private static string ScriptPath()
    {
        var here = AppContext.BaseDirectory;
        // Walk up to repo root from bin/.
        var dir = new DirectoryInfo(here);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "winpepper.sln")))
            dir = dir.Parent;
        dir.ShouldNotBeNull();
        return Path.Combine(dir!.FullName, "packaging", "sign.ps1");
    }

    [Fact]
    public void ScriptExists()
    {
        File.Exists(ScriptPath()).ShouldBeTrue();
    }

    [Fact]
    public void Script_HasThumbprintAndPfxParameters()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("param(");
        txt.ShouldContain("$Thumbprint");
        txt.ShouldContain("$PfxPath");
        txt.ShouldContain("$PfxPassword");
        txt.ShouldContain("$InputFiles");
    }

    [Fact]
    public void Script_DisabledMessage()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("WINPEPPER_SIGNING_DISABLED");
    }

    [Fact]
    public void Script_InvokesSigntool()
    {
        var txt = File.ReadAllText(ScriptPath());
        txt.ShouldContain("signtool");
        txt.ShouldContain("/sha1");
        txt.ShouldContain("/f");
        // EV certs require the SHA256 file digest and an RFC 3161 timestamp server.
        txt.ShouldContain("/fd SHA256");
        txt.ShouldContain("/tr ");
        txt.ShouldContain("/td SHA256");
    }
}
```

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~SignScriptTests"
```

Expected: all four asserts fail (script missing).

- [ ] **Step 3: Write `packaging/sign.ps1`**

```powershell
<#
.SYNOPSIS
    Sign Winpepper binaries and the MSI with an EV code-signing certificate.

.DESCRIPTION
    Off by default. Pass either -Thumbprint <sha1> to sign with an installed certificate,
    or -PfxPath <path> -PfxPassword <pw> to sign with a PFX file.

    When invoked with neither, prints WINPEPPER_SIGNING_DISABLED and exits 0
    (so release pipelines can call sign.ps1 unconditionally).

.PARAMETER Thumbprint
    SHA1 thumbprint of an EV code-signing certificate installed in the current
    user's certificate store.

.PARAMETER PfxPath
    Path to a PFX file containing the EV code-signing certificate.

.PARAMETER PfxPassword
    Password for the PFX file. Required if -PfxPath is given.

.PARAMETER InputFiles
    One or more paths to sign. Globs are expanded.

.EXAMPLE
    pwsh ./packaging/sign.ps1 -Thumbprint 0123ABCD... -InputFiles `
      artifacts/winpepper-0.6.0-x64.msi, src/Winpepper.App/bin/Release/.../Winpepper.exe
#>
[CmdletBinding()]
param(
    [string]$Thumbprint,
    [string]$PfxPath,
    [string]$PfxPassword,
    [Parameter(Mandatory=$false, ValueFromRemainingArguments=$true)]
    [string[]]$InputFiles
)

$ErrorActionPreference = "Stop"

if (-not $Thumbprint -and -not $PfxPath) {
    Write-Host "WINPEPPER_SIGNING_DISABLED"
    exit 0
}

if (-not $InputFiles -or $InputFiles.Count -eq 0) {
    Write-Error "No input files supplied."
    exit 2
}

# Locate signtool: prefer the Windows SDK install on the build agent.
$signtool = (Get-Command signtool.exe -ErrorAction SilentlyContinue)?.Path
if (-not $signtool) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22000.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe"
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $signtool = $c; break }
    }
}
if (-not $signtool) {
    Write-Error "signtool.exe not found on PATH or in the Windows SDK install."
    exit 3
}

$timestampUrl = "http://timestamp.digicert.com"

$expanded = @()
foreach ($pattern in $InputFiles) {
    $matched = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue
    if ($matched) {
        $expanded += $matched.FullName
    } elseif (Test-Path $pattern) {
        $expanded += (Resolve-Path $pattern).Path
    } else {
        Write-Error "Input not found: $pattern"
        exit 4
    }
}

if ($Thumbprint) {
    & $signtool sign /sha1 $Thumbprint `
        /fd SHA256 `
        /tr $timestampUrl `
        /td SHA256 `
        /a $expanded
} else {
    if (-not $PfxPassword) {
        Write-Error "-PfxPassword is required when -PfxPath is supplied."
        exit 5
    }
    & $signtool sign /f $PfxPath /p $PfxPassword `
        /fd SHA256 `
        /tr $timestampUrl `
        /td SHA256 `
        $expanded
}

if ($LASTEXITCODE -ne 0) {
    Write-Error "signtool failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "WINPEPPER_SIGNING_OK ($($expanded.Count) file(s))"
exit 0
```

- [ ] **Step 4: Run the tests and confirm pass**

```bash
dotnet test tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj --filter "FullyQualifiedName~SignScriptTests"
```

Expected: all four pass.

- [ ] **Step 5: Smoke the disabled path on Linux (no signtool present)**

```bash
pwsh $REPO_ROOT/packaging/sign.ps1
```

Expected output: `WINPEPPER_SIGNING_DISABLED`. Exit code 0.

If `pwsh` is not installed on the dev box:

```bash
sudo apt-get install -y powershell || \
  curl -sL https://aka.ms/install-powershell.sh | sudo bash
```

- [ ] **Step 6: Commit**

```bash
git add packaging/sign.ps1 tests/Winpepper.Core.Tests/SignScriptTests.cs
git commit -m "feat(packaging): sign.ps1 wrapper (off by default)"
```

---

## Task 10: Hook `sign.ps1` into the wixproj as an opt-in post-build step

**Files:**
- Modify: `$REPO_ROOT/packaging/Winpepper.Msi.wixproj` — add a `Target Name="SignArtifacts"` that runs after `Build` only when `WinpepperSigningThumbprint` or `WinpepperSigningPfx` is set.

The wixproj must NOT fail when signing is disabled. The signing target is no-op in that case.

- [ ] **Step 1: Append the signing target to `packaging/Winpepper.Msi.wixproj`**

Append just before the closing `</Project>`:

```xml
  <Target Name="SignArtifacts" AfterTargets="Build"
          Condition="'$(OS)' == 'Windows_NT' AND ('$(WinpepperSigningThumbprint)' != '' OR '$(WinpepperSigningPfx)' != '')">
    <PropertyGroup>
      <SignScript>$(MSBuildThisFileDirectory)sign.ps1</SignScript>
      <SignedExe>$(AppPublishDir)\Winpepper.exe</SignedExe>
      <SignedMsi>$(OutputPath)$(OutputName).msi</SignedMsi>
    </PropertyGroup>
    <Exec Condition="'$(WinpepperSigningThumbprint)' != ''"
          Command="pwsh -NoProfile -ExecutionPolicy Bypass -File &quot;$(SignScript)&quot; -Thumbprint $(WinpepperSigningThumbprint) -InputFiles &quot;$(SignedExe)&quot; &quot;$(SignedMsi)&quot;" />
    <Exec Condition="'$(WinpepperSigningThumbprint)' == '' AND '$(WinpepperSigningPfx)' != ''"
          Command="pwsh -NoProfile -ExecutionPolicy Bypass -File &quot;$(SignScript)&quot; -PfxPath &quot;$(WinpepperSigningPfx)&quot; -PfxPassword $(WinpepperSigningPfxPassword) -InputFiles &quot;$(SignedExe)&quot; &quot;$(SignedMsi)&quot;" />
  </Target>
```

Note: the exe is signed BEFORE it goes into the MSI in a perfect world — to do that, signing would need to run after `Publish` of the App project but before WiX harvests it. For Plan 6's first cut, we sign the staged exe in the publish dir and then re-sign the MSI; the re-signed exe inside the MSI is the same one that was on disk when WiX cabbed it. This works because we sign during `AfterTargets="Build"` only when the wixproj is in a configuration where signing matters (release builds invoked with the env var set), and signing happens on every release rebuild.

A cleaner alternative (sign during `Publish` of the App project itself) is deferred — for a single-binary product the current ordering is acceptable, and the post-build sign of the MSI is the externally-visible attestation.

- [ ] **Step 2: Verify the no-op path still builds**

```bash
dotnet build packaging/Winpepper.Msi.wixproj -c Release \
  -p:AppPublishDir=/tmp/winpepper-publish-stub \
  -p:_SkipUpstreamProjectReferences=true \
  -p:BuildProjectReferences=false \
  -p:WinAppSdkBootstrapper=$REPO_ROOT/packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe
```

Expected: build succeeds, `SignArtifacts` is skipped (Condition false on Linux), no `signtool` invocation.

- [ ] **Step 3: Commit**

```bash
git add packaging/Winpepper.Msi.wixproj
git commit -m "build(packaging): opt-in sign.ps1 post-build step"
```

---

## Task 11: VM smoke — full MSI build + install + selftest + uninstall

**This task DEPENDS on `Winpepper.App` building on the VM.** Per the plan header, that build is currently broken (WinAppSDK 1.6/1.7 + .NET 9 XAML markup compiler PNSE). When it is broken, **skip Steps 3–7** of this task; record the skip in `docs/manual-test.md` and proceed to Task 12. When it is fixed, return and execute all steps. The wxs source, signing wrapper, and CI workflow in surrounding tasks remain valid and committable independently.

**Files:**
- Modify: `$REPO_ROOT/docs/manual-test.md` — append the Plan 6 smoke procedure.

- [ ] **Step 1: Check whether `Winpepper.App` builds on the VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Release"
```

If the build fails with `RuntimeEnvironment.GetRuntimeInterfaceAsObject` PNSE, **skip to Step 8** and document the skip. Otherwise continue.

- [ ] **Step 2: Sync to the VM and publish the App project**

```bash
./scripts/sync-to-vm.sh
./scripts/winrun "dotnet publish src/Winpepper.App/Winpepper.App.csproj -c Release -r win-x64 --self-contained true"
```

Expected: a populated `src\Winpepper.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\` containing `Winpepper.exe`, the WinAppSDK runtime DLLs, the Assets folder, and Microsoft.WindowsAppRuntime.Bootstrap.dll.

- [ ] **Step 3: Fetch the WinAppSDK bootstrapper into `packaging/bootstrapper/`**

The WinAppSDK NuGet ships the bootstrapper at `runtimes/win-x64/native/`. On the VM:

```bash
./scripts/winrun "powershell -Command \"\$pkg = Get-ChildItem -Path \$env:USERPROFILE\\.nuget\\packages\\microsoft.windowsappsdk -Recurse -Filter 'WindowsAppRuntimeInstall-x64.exe' | Select-Object -First 1; Copy-Item \$pkg.FullName -Destination C:\\winpepper\\packaging\\bootstrapper\\WindowsAppRuntimeInstall-x64.exe -Force\""
```

Expected: the bootstrapper (~3 MB) is copied next to the placeholder. It is gitignored — never committed.

- [ ] **Step 4: Build the MSI on the VM**

```bash
./scripts/winrun "dotnet build packaging\\Winpepper.Msi.wixproj -c Release"
```

Expected: `artifacts\winpepper-<version>-x64.msi` is produced. Approximate size: 80-160 MB.

- [ ] **Step 5: Install via `msiexec /qn`**

The MSI filename includes the git-height build number (e.g. `winpepper-0.6.0.5-x64.msi`), so resolve the path via a glob to match Task 13's CI pattern:

```bash
./scripts/winrun "\$msi = (Get-Item artifacts\\winpepper-*-x64.msi | Select-Object -First 1).FullName; msiexec /i \$msi /qn /l*v artifacts\\install.log"
```

Expected exit code: 0. Verify install log shows `Action ended …: INSTALL. Return value 1.` near the end.

- [ ] **Step 6: Run the selftest from the installed location**

```bash
./scripts/winrun "& 'C:\\Program Files\\Winpepper\\Winpepper.exe' --selftest"
```

Expected: stdout contains `WINPEPPER_SELFTEST_OK`, exit code 0.

- [ ] **Step 7: Verify the autostart Run key**

```bash
./scripts/winrun "Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name Winpepper"
```

Expected: value is `"C:\Program Files\Winpepper\winpepper.exe" --tray`.

- [ ] **Step 8: Uninstall**

```bash
./scripts/winrun "\$msi = (Get-Item artifacts\\winpepper-*-x64.msi | Select-Object -First 1).FullName; msiexec /x \$msi /qn /l*v artifacts\\uninstall.log"
./scripts/winrun "Test-Path 'C:\\Program Files\\Winpepper\\Winpepper.exe'"
```

Expected: exit code 0; file existence check returns `False`. The Run key should be gone:

```bash
./scripts/winrun "Get-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name Winpepper -ErrorAction SilentlyContinue"
```

Expected: empty output.

- [ ] **Step 9: Verify `%LOCALAPPDATA%\winpepper` was NOT deleted**

```bash
./scripts/winrun "Test-Path \$env:LOCALAPPDATA\\winpepper"
```

Expected: `True`. Settings, history, corrections, and downloaded models survive uninstall per spec §11 — they live in `%LOCALAPPDATA%`, not in `C:\Program Files\Winpepper\`.

- [ ] **Step 10: Append the procedure to `docs/manual-test.md`**

```markdown
## Plan 6 MSI smoke (Windows VM)

Status: blocked on Winpepper.App build. When unblocked, execute steps 2–9 above.

1. `./scripts/winrun "dotnet publish src/Winpepper.App/Winpepper.App.csproj -c Release -r win-x64 --self-contained true"` — succeeds.
2. Copy WinAppSDK bootstrapper into `packaging/bootstrapper/` (step 3 above).
3. `./scripts/winrun "dotnet build packaging\\Winpepper.Msi.wixproj -c Release"` produces `artifacts\winpepper-<version>-x64.msi`.
4. `msiexec /i ... /qn` returns 0; install log healthy.
5. `Winpepper.exe --selftest` emits `WINPEPPER_SELFTEST_OK`.
6. Autostart `Run` key is set.
7. `msiexec /x ... /qn` returns 0; INSTALLFOLDER is gone; Run key is gone.
8. `%LOCALAPPDATA%\winpepper` survives.
```

- [ ] **Step 11: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs(manual-test): Plan 6 MSI install/uninstall smoke procedure"
```

---

## Task 12: Document upgrade-survival expectations in `docs/manual-test.md`

**Files:**
- Modify: `$REPO_ROOT/docs/manual-test.md`

Spec §11 specifies: "Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`." The MSI's `MajorUpgrade` is set to `AllowDowngrades="no"` and `Schedule="afterInstallInitialize"`. The autostart Run key is created only on fresh install (the `AutostartRunKey` component carries the WiX v5 attribute form `Condition="NOT WIX_UPGRADE_DETECTED AND NOT UPGRADINGPRODUCTCODE"` from Task 6).

- [ ] **Step 1: Append the upgrade procedure to `docs/manual-test.md`**

```markdown
## Plan 6 MSI upgrade smoke (Windows VM)

Status: blocked on Winpepper.App build. When unblocked, exercise the procedure below.

Goal: confirm settings, corrections, history, models, and the autostart Run key
   survive a major-upgrade install of a newer MSI over an older one.

1. Install MSI A: `msiexec /i artifacts\winpepper-A.msi /qn`
2. Open the app, edit a setting (e.g., toggle window-context off), add a custom
   correction `("kubernetes", "Kubernetes")`, record a dictation, and disable
   autostart from Settings (Run key is removed).
3. Re-enable autostart from Settings — the Run key is created.
4. Bump version.json minor version, build MSI B from a freshly-committed HEAD.
5. Install MSI B: `msiexec /i artifacts\winpepper-B.msi /qn` (uses MajorUpgrade).
6. Verify on the VM:
   - `%LOCALAPPDATA%\winpepper\settings.json` is intact (window-context still off).
   - `%LOCALAPPDATA%\winpepper\corrections.json` still contains the kubernetes pair.
   - `%LOCALAPPDATA%\winpepper\history\<date>\<uuid>.wav` and the corresponding
     `entries.json` row are intact.
   - `%LOCALAPPDATA%\winpepper\models\` contents are intact (no re-download needed).
   - The Run key value is the same string the user set in Settings (B did NOT
     overwrite it; the component condition excluded WIX_UPGRADE_DETECTED).

Acceptance: all six checks pass.
```

- [ ] **Step 2: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs(manual-test): Plan 6 MSI upgrade survival procedure"
```

---

## Task 13: Nightly CI workflow

**Files:**
- Create: `$REPO_ROOT/.github/workflows/nightly.yml`

The nightly workflow runs on `windows-latest`, publishes `Winpepper.App`, builds the MSI, runs `msiexec /qn` install + selftest + uninstall, and uploads the MSI as an artifact. Failure is expected on the first run until the WinUI block is resolved — that's by design.

- [ ] **Step 1: Write `.github/workflows/nightly.yml`**

```yaml
name: Nightly MSI

on:
  schedule:
    - cron: "0 8 * * *"   # 08:00 UTC daily
  workflow_dispatch: {}

permissions:
  contents: read
  actions: read

jobs:
  msi-windows:
    runs-on: windows-latest
    timeout-minutes: 45
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

      - name: Locate WinAppSDK bootstrapper
        id: bootstrap
        run: |
          $pkg = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk" -Recurse -Filter 'WindowsAppRuntimeInstall-x64.exe' | Select-Object -First 1
          if (-not $pkg) {
            Write-Error "WindowsAppRuntimeInstall-x64.exe not found under the WindowsAppSDK NuGet package."
            exit 1
          }
          New-Item -ItemType Directory -Path "packaging\bootstrapper" -Force | Out-Null
          Copy-Item $pkg.FullName -Destination "packaging\bootstrapper\WindowsAppRuntimeInstall-x64.exe" -Force
          "bootstrapper=$($pkg.FullName)" >> $env:GITHUB_OUTPUT

      - name: Build MSI
        run: dotnet build packaging\Winpepper.Msi.wixproj -c Release

      - name: Compute version
        id: version
        run: |
          $msi = Get-ChildItem -Path "artifacts" -Filter "winpepper-*-x64.msi" | Select-Object -First 1
          if (-not $msi) { Write-Error "No MSI produced."; exit 1 }
          "msi=$($msi.FullName)" >> $env:GITHUB_OUTPUT
          "name=$($msi.Name)" >> $env:GITHUB_OUTPUT

      - name: Install MSI (silent)
        run: |
          $msi = "${{ steps.version.outputs.msi }}"
          Start-Process msiexec.exe -ArgumentList "/i `"$msi`" /qn /l*v artifacts\install.log" -Wait
          if ($LASTEXITCODE -ne 0) {
            Get-Content artifacts\install.log -Tail 200
            Write-Error "msiexec install failed"
            exit 1
          }

      - name: Selftest installed binary
        run: |
          $exe = "C:\Program Files\Winpepper\Winpepper.exe"
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
          $msi = "${{ steps.version.outputs.msi }}"
          Start-Process msiexec.exe -ArgumentList "/x `"$msi`" /qn /l*v artifacts\uninstall.log" -Wait
          if ($LASTEXITCODE -ne 0) {
            Get-Content artifacts\uninstall.log -Tail 200
            Write-Error "msiexec uninstall failed"
            exit 1
          }
          if (Test-Path 'C:\Program Files\Winpepper\Winpepper.exe') {
            Write-Error "INSTALLFOLDER not cleaned up by uninstall"
            exit 1
          }
          $val = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Winpepper -ErrorAction SilentlyContinue
          if ($val) {
            Write-Error "Run key not removed by uninstall: $($val.Winpepper)"
            exit 1
          }

      - name: Upload MSI artifact
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: ${{ steps.version.outputs.name }}
          path: artifacts/winpepper-*-x64.msi
          if-no-files-found: error

      - name: Upload install/uninstall logs on failure
        if: failure()
        uses: actions/upload-artifact@v4
        with:
          name: msi-logs
          path: |
            artifacts/install.log
            artifacts/uninstall.log
          if-no-files-found: ignore
```

- [ ] **Step 2: Lint the workflow YAML**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('$REPO_ROOT/.github/workflows/nightly.yml'))" && echo OK
```

Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/nightly.yml
git commit -m "ci: nightly MSI build + install/uninstall smoke (windows-latest)"
```

---

## Task 14: Extend pre-merge CI to lint the wixproj

**Files:**
- Modify: `$REPO_ROOT/.github/workflows/ci.yml` — add a wixproj-lint step on `linux-build`.

The wixproj is buildable on Linux (the WiX 5 SDK is fully managed). Adding a lint step catches wxs regressions on every PR even when the App publish output is absent.

- [ ] **Step 1: Modify `.github/workflows/ci.yml` — append after `dotnet test` on `linux-build`**

Replace the `linux-build` job's steps with:

```yaml
  linux-build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - run: dotnet restore
      - run: dotnet build --configuration Release --no-restore
      - run: dotnet test --configuration Release --no-build --filter "Platform!=Windows"

      - name: Stage publish stub for wxs lint
        run: |
          mkdir -p /tmp/winpepper-publish-stub/Assets
          printf 'stub' > /tmp/winpepper-publish-stub/Winpepper.exe
          printf 'stub' > /tmp/winpepper-publish-stub/Microsoft.WindowsAppRuntime.dll
          cp src/Winpepper.App/Assets/AppIcon.ico /tmp/winpepper-publish-stub/Assets/AppIcon.ico
          mkdir -p packaging/bootstrapper
          printf 'stub' > packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe

      - name: Lint wxs (build MSI from stub publish dir)
        run: |
          dotnet build packaging/Winpepper.Msi.wixproj -c Release \
            -p:AppPublishDir=/tmp/winpepper-publish-stub \
            -p:_SkipUpstreamProjectReferences=true \
            -p:BuildProjectReferences=false \
            -p:WinAppSdkBootstrapper=$PWD/packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe

      - name: Verify MSI produced
        run: |
          test -f artifacts/winpepper-*-x64.msi || (echo "No MSI produced from stub"; exit 1)
          ls -la artifacts/
```

- [ ] **Step 2: Lint the YAML**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('$REPO_ROOT/.github/workflows/ci.yml'))" && echo OK
```

Expected: `OK`.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: lint packaging/winpepper.wxs on every PR (Linux build)"
```

---

## Task 15: Wire `version.json` cadence into the release flow

**Files:**
- Create: `$REPO_ROOT/docs/release.md`

A short doc that captures: how to bump the version, how to cut a release branch, how to invoke `sign.ps1`, and how the MSI gets attached to the GitHub release. No code; pure procedure that an engineer running a future release follows.

- [ ] **Step 1: Write `docs/release.md`**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add docs/release.md
git commit -m "docs: release procedure (versioning, signing, GH release attach)"
```

---

## Task 16: Solution-wide green-build check

**Files:**
- None (verification only).

- [ ] **Step 1: Build the full solution on Linux**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build -c Release
```

Expected: every project compiles. `Winpepper.App` is skipped on Linux via the existing `Directory.Build.props` guard. The wixproj produces an empty MSI if no publish stub is in place, or fails fast — both are acceptable; the CI workflow takes care of the stub during the real lint.

- [ ] **Step 2: Run all Linux-runnable tests**

```bash
dotnet test -c Release --filter "Platform!=Windows"
```

Expected: every test passes, including the new `VersionStampTests`, `BuildSignatureTests`, `AboutTextTests`, `SelftestProbeTests`, and `SignScriptTests`.

- [ ] **Step 3: Verify the wxs lints with the stub publish dir**

```bash
rm -rf /tmp/winpepper-publish-stub
mkdir -p /tmp/winpepper-publish-stub/Assets
printf "stub" > /tmp/winpepper-publish-stub/Winpepper.exe
printf "stub" > /tmp/winpepper-publish-stub/Microsoft.WindowsAppRuntime.dll
cp src/Winpepper.App/Assets/AppIcon.ico /tmp/winpepper-publish-stub/Assets/AppIcon.ico
[ -f packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe ] || \
  printf "stub" > packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe

dotnet build packaging/Winpepper.Msi.wixproj -c Release \
  -p:AppPublishDir=/tmp/winpepper-publish-stub \
  -p:_SkipUpstreamProjectReferences=true \
  -p:BuildProjectReferences=false \
  -p:WinAppSdkBootstrapper=$PWD/packaging/bootstrapper/WindowsAppRuntimeInstall-x64.exe

ls -la artifacts/
```

Expected: a `winpepper-<version>-x64.msi` exists in `artifacts/`. Size around 4-8 MB for the stub harvest (the real ~100 MB MSI requires the actual publish output, which depends on the WinUI block being resolved).

- [ ] **Step 4: Run `dotnet format --verify-no-changes`**

```bash
dotnet format --verify-no-changes
```

Expected: clean exit. If formatting issues surface, run `dotnet format` and amend the appropriate commit.

- [ ] **Step 5: No commit needed — verification only**

Plan 6 is complete. If `Winpepper.App` is still blocked at this point, follow the carry-forward block resolution path (out of scope for Plan 6) and then return to Task 11 to exercise the live MSI install path on the VM.

---

## Self-review checklist (for the writer)

After completing all tasks, verify:

- [ ] **Spec coverage.** Every §11 packaging requirement maps to a task: per-machine `C:\Program Files\Winpepper\` install (Task 6), empty model dir created on first run not by MSI (Task 4 `SelftestProbe.Run` creates it), Start menu shortcut (Task 6), Programs and Features entry (Task 6), per-user autostart Run key on fresh install only (Task 6), `MajorUpgrade.AllowDowngrades=no Schedule=afterInstallInitialize` (Task 6), Windows 11 22H2+ `LaunchCondition` (Task 6), DirectX 12 warn-not-block (Task 8), WinAppSDK bootstrapper invocation (Task 8), `Nerdbank.GitVersioning` + commit SHA in `AssemblyInformationalVersion` (Task 1), `sign.ps1` with thumbprint or PFX (Task 9), `unsigned build` in About (Task 2 + Task 3), output `winpepper-<version>-x64.msi` (Task 5+), CI nightly install/uninstall on Windows runner (Task 13), settings/corrections/history/models survive upgrade (Task 12 procedure + Task 6 component condition). §7.7 autostart values match `AutostartRegistry.RunKey` + `ValueName` from Plan 3 (Task 6). §10.4 CI nightly on the Windows VM/runner (Task 13).
- [ ] **Placeholder scan.** No "TBD", no "implement later", no "add appropriate handling". Every test step has actual test code; every implementation step has the actual implementation.
- [ ] **Type consistency.** `BuildSignature.Describe` / `BuildSignature.IsSigned` (Task 2) is the same shape `AboutText.Body` (Task 3) and `SelftestProbe.Run` (Task 4) consume. `AutostartRunKey` component (Task 6) uses the same registry path (`Software\Microsoft\Windows\CurrentVersion\Run`) and value name (`Winpepper`) as `AutostartRegistry.RunKey` + `ValueName` from Plan 3. The MSI's autostart value string (`"[INSTALLFOLDER]Winpepper.exe" --tray`) matches Plan 3's `AutostartRegistry.Enable(exePath, "--tray")` formatting. `winpepper.exe` (lowercase in spec §7.7) is `Winpepper.exe` (the actual assembly name from `Winpepper.App.csproj`) — Windows paths are case-insensitive so this round-trips, and the `[INSTALLFOLDER]Winpepper.exe` bind from the MSI works either way.
- [ ] **File paths.** Every `git add` references the paths created or modified in the same task's headers. The wxs `$(AppPublishDir)` (Task 7) matches the publish output path of `Winpepper.App.csproj` (verified in Task 11 Step 2).
- [ ] **Carry-forward block honored.** Task 11 is explicitly conditional on `Winpepper.App` building; Task 13's CI nightly workflow is committed in a form that will fail loudly when the block is present, so the failure surfaces and triggers a fix.
- [ ] **WiX v5 syntax.** All conditional Components use the `Condition="…"` attribute form (Task 6 `AutostartRunKey`), not the legacy `<Condition>` child element. All `<Launch>` elements use the `Condition="…"` attribute form (Task 6 22H2+ gate). The 22H2+ gate references `MSI_WIN_BUILD`, which is populated by the capability-probe CA (Task 8) reading `HKLM\Software\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber`; the probe + `ReadProbeOutput` pair is scheduled `Before="LaunchConditions"` in BOTH `InstallUISequence` and `InstallExecuteSequence` so the gate works under interactive AND `/qn` installs. The non-existent `WindowsBuild` MSI property is NOT used anywhere.

## What Plan 6 does NOT cover

- Fixing the WinUI 3 / .NET 9 XAML markup compiler block (separate effort).
- Microsoft Store identity / Store distribution (out of scope per spec §12).
- Signed nightly releases (left for a follow-up release-engineering plan; the
  workflow here produces unsigned MSIs).
- ARM64 MSI (spec §2 — ARM64 explicitly out of scope for v1).
- Auto-update from inside the app (spec §12 implicit — no telemetry, no
  phone-home; future plan if needed).

## Handoff

When every task is committed and the lint-build passes: tell the user the
packaging plumbing is complete and committed, and that producing a real
installable MSI is blocked on the carry-forward WinUI build issue. The nightly
CI workflow will fail loudly on its first run; that failure is the action item
that closes the carry-forward.
