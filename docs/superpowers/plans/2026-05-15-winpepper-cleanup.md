# Winpepper Plan 2 — Cleanup, Window Context, Corrections

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Layer the cleanup pipeline on top of the Plan 1 walking skeleton. Add `Winpepper.Corrections.CorrectionStore` (preferred + replacements with atomic JSON persistence), `Winpepper.Platform.WindowContext` (UIA tree → `Windows.Media.Ocr` fallback prefetch), and `Winpepper.Cleanup.CleanupRunner` (LlamaSharp Vulkan, prompt assembly, `<think>` sanitizer, timeout fallback, deterministic correction post-pass). Wire the runner into `Winpepper.Cli` so manual VM dictation now produces cleaned-up text.

**Architecture:** Two new projects (`Winpepper.Cleanup`, `Winpepper.Corrections`) and a new `WindowContext` subsystem inside the existing `Winpepper.Platform`. Inter-stage communication continues through bounded `Channel<T>` (capacity 16). Persistence flows through `Winpepper.Core.AtomicFile`. Logging flows through `Winpepper.Core.Logging.WinpepperLogging`. The cleanup runner constructs an `LLamaContext` once at app start, pre-warms it with a tiny prompt, then runs synchronously per session inside a 15-second timeout. Window context prefetch begins at `HotkeyDown` and races against transcription; the runner waits up to 500 ms before proceeding without it. The deterministic post-pass applies `CorrectionStore.Replacements` (case-preserving) regardless of LLM outcome.

**Tech Stack:** C# / .NET 9, `LLamaSharp` 0.27.0 + `LLamaSharp.Backend.Vulkan` 0.27.0 (GGUF inference on DX12 GPUs via Vulkan), `UIAutomationClient` / `UIAutomationTypes` (UIA tree walk), `Windows.Media.Ocr` via CsWinRT `Microsoft.Windows.SDK.Net.Ref` (OCR fallback), `System.Drawing.Common` + `PrintWindow` P/Invoke (screen capture for OCR), xUnit v3 + Shouldly (tests).

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md). Covers spec §5.5 (Cleanup), §6.1–§6.5 (window context, prompt, profiles, deterministic post-pass), §8.1 (CorrectionStore), and the cleanup-relevant rows of §9.1 (error bus). Out of scope: spec §7 UI, §8.2 post-paste learning, §7.4 onboarding, §7.5 settings UI binding — covered by Plans 3 and 5.

**Prerequisites:** Plan 1 ([docs/superpowers/plans/2026-05-15-winpepper-foundation.md](2026-05-15-winpepper-foundation.md)) must be merged. The walking skeleton (`Winpepper.Cli` → Parakeet TDT v3 → SendInput) is committed on branch `plan-1/foundation` and is the integration target. Before starting Plan 2, verify on Linux:

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build
dotnet test --filter "Platform!=Windows"
```

and on the Windows VM:

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test"
```

Both should be green. The Parakeet model must already be present at `%LOCALAPPDATA%\winpepper\models\parakeet-tdt-0.6b-v3\` on the VM (downloaded in Plan 1, Task 12).

**Repo root throughout the plan:** `$REPO_ROOT/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced via `scripts/sync-to-vm.sh`).

**Open implementation questions from spec §13 addressed in this plan:**

- LlamaSharp Vulkan NuGet variant: confirmed as `LLamaSharp` + `LLamaSharp.Backend.Vulkan` (both 0.27.0). Verified in Task 1 by inspecting the package index. Vulkan device pick API is `ModelParams { GpuLayerCount = N }`; runtime selects the first Vulkan-capable adapter.
- UIA `TextEdit_TextChangedEvent` availability — deferred to Plan 5 (post-paste learning).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Run it and confirm it fails. Implement. Run it and confirm it passes. Commit.

**Commits.** One commit per task at minimum. Smaller commits within a task are fine. Always end a task with a green build and green tests on Linux *and* (where applicable) on the Windows VM.

**Building.** Cross-platform tasks build and test on Linux (`dotnet build`, `dotnet test`). Windows-only tasks run on the VM via the `winssh` and `winrun` helpers from Plan 1 Task 2.

**Multi-targeting.** New projects that touch UIA, OCR, `System.Drawing`, or `LlamaSharp` runtime native bits use `<TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>`. Windows-only code is guarded by `#if WINDOWS`. Linux cross-compilation of the Windows TFM requires `<EnableWindowsTargeting>true</EnableWindowsTargeting>` in `Directory.Build.props` (added in Task 1).

**Skipping Windows tests on Linux.** Tests that touch Win32 / UIA / OCR / native LlamaSharp are tagged with `[Trait("Platform", "Windows")]`. Linux runs `dotnet test --filter "Platform!=Windows"`. The VM runs the full suite. xunit v3 prefers `Assert.SkipUnless(cond, msg)` for runtime skip-when-missing-asset.

**Linux env reminder.** `dotnet` requires `export DOTNET_ROOT="$HOME/.dotnet"` before each shell session.

---

## File map (Plan 2 additions)

```
src/
  Winpepper.Cleanup/
    Winpepper.Cleanup.csproj
    PromptBuilder.cs                  # §6.2 prompt assembly
    BasePrompts.cs                    # §6.3 default + literal profile texts
    CleanupProfile.cs                 # §6.4 profile enum + selector
    CleanupOptions.cs                 # timeout/max-tokens/profile bag
    CleanupResult.cs                  # cleaned text + path taken + raw model output
    LlamaCleanupBackend.cs            # LLamaContext lifecycle, pre-warm, generate
    ILlamaCleanupBackend.cs           # injectable seam for tests (fakes)
    ThinkSanitizer.cs                 # <think>...</think> stripping
    CaseAwareReplacer.cs              # §6.5 case-preserving substitution
    CleanupRunner.cs                  # orchestrates: build prompt → run → sanitize → fallback → post-pass

  Winpepper.Corrections/
    Winpepper.Corrections.csproj
    CorrectionStore.cs                # §8.1 persistence (preferred + replacements)
    CorrectionsData.cs                # schema-versioned record
    CorrectionValidation.cs           # input validation rules

  Winpepper.Platform/                 # additions
    WindowContext/
      WindowContextResult.cs          # source + text + char count + avg confidence
      WindowContextSource.cs          # enum: Uia | Ocr | Empty
      WindowContextPrefetch.cs        # Task<WindowContextResult>, cancellable
      UiaNative.cs                    # UIAutomation* COM imports + helpers
      UiaTreeReader.cs                # tree walk + dedup + reading-order sort
      UiaTextExtraction.cs            # pattern preference order
      OcrFallback.cs                  # PrintWindow → SoftwareBitmap → OcrEngine
      PrintWindowNative.cs            # GetForegroundWindow, GetClientRect, PrintWindow
      ForegroundWindow.cs             # title accessor (used by error bus logging)

  Winpepper.Cli/                      # modified
    Pipeline.cs                       # wired to CleanupRunner + WindowContextPrefetch
    Program.cs                        # constructs CorrectionStore + CleanupRunner + WindowContextPrefetch

tests/
  Winpepper.Cleanup.Tests/
    Winpepper.Cleanup.Tests.csproj
    PromptBuilderTests.cs             # snapshot tests, every block combination
    BasePromptsTests.cs               # default prompt content checks (examples, filler words)
    ThinkSanitizerTests.cs            # tag stripping cases
    CaseAwareReplacerTests.cs         # casing preservation
    CleanupRunnerTests.cs             # timeout, empty, "..." fallback paths via fake backend
    LlamaCleanupBackendIntegrationTests.cs  # [Platform=Windows] real model smoke

  Winpepper.Corrections.Tests/
    Winpepper.Corrections.Tests.csproj
    CorrectionStoreTests.cs           # round-trip, atomic write, default on missing file, schema-mismatch fallback
    CorrectionValidationTests.cs

  Winpepper.Platform.Tests/           # additions
    WindowContext/
      UiaTreeReaderTests.cs           # pure-logic tree-flattening tests (Linux)
      OcrLineSortTests.cs             # pure-logic ordering tests (Linux)
      WindowContextPrefetchTests.cs   # cancellation + timeout-budget tests
      UiaIntegrationTests.cs          # [Platform=Windows] real walk against test window
      OcrIntegrationTests.cs          # [Platform=Windows] real PrintWindow → OCR
```

---

## Task 1: Project plumbing — add cleanup/corrections projects, packages, EnableWindowsTargeting

**Files:**
- Modify: `$REPO_ROOT/Directory.Build.props`
- Modify: `$REPO_ROOT/Directory.Packages.props`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/Winpepper.Cleanup.csproj`
- Create: `$REPO_ROOT/src/Winpepper.Corrections/Winpepper.Corrections.csproj`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/Placeholder.cs`
- Create: `$REPO_ROOT/src/Winpepper.Corrections/Placeholder.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/SanityTests.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj`
- Create: `$REPO_ROOT/tests/Winpepper.Corrections.Tests/SanityTests.cs`

- [ ] **Step 1: Modify `Directory.Build.props` to allow Windows TFM cross-compile from Linux**

Replace the existing `<PropertyGroup>` block (keep all existing keys, add the two new ones at the bottom):

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
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
  </PropertyGroup>
</Project>
```

Why: `EnableWindowsTargeting=true` lets `net9.0-windows10.0.19041.0` projects restore + build on Linux for cross-compile checks. `CA1416` (platform-call-site warnings) is suppressed globally because we gate Windows-only code with `#if WINDOWS` and the analyzer can't see those branches across TFM boundaries.

- [ ] **Step 2: Modify `Directory.Packages.props` to add Plan 2 packages**

Add these lines inside the existing `<ItemGroup>` (do not remove anything from Plan 1):

```xml
    <!-- Plan 2: cleanup LLM -->
    <PackageVersion Include="LLamaSharp" Version="0.27.0" />
    <PackageVersion Include="LLamaSharp.Backend.Vulkan" Version="0.27.0" />

    <!-- Plan 2: UIA + OCR + GDI (Windows TFM only) -->
    <PackageVersion Include="System.Drawing.Common" Version="9.0.0" />
    <PackageVersion Include="Microsoft.Windows.CsWinRT" Version="2.2.0" />
```

Note: UIA (`UIAutomationClient.dll`, `UIAutomationTypes.dll`) and `Windows.Media.Ocr` are reached through `<FrameworkReference>` on the Windows TFM rather than NuGet — no package needed for those. `System.Drawing.Common` is needed only on the Windows TFM and is referenced conditionally.

- [ ] **Step 3: Create the cleanup project `src/Winpepper.Cleanup/Winpepper.Cleanup.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Cleanup</RootNamespace>
    <AssemblyName>Winpepper.Cleanup</AssemblyName>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <PackageReference Include="LLamaSharp" />
    <PackageReference Include="LLamaSharp.Backend.Vulkan" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Create the corrections project `src/Winpepper.Corrections/Winpepper.Corrections.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Corrections</RootNamespace>
    <AssemblyName>Winpepper.Corrections</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

(Corrections is pure-managed, cross-platform, single TFM.)

- [ ] **Step 5: Stub `src/Winpepper.Cleanup/Placeholder.cs`** (so the project compiles)

```csharp
namespace Winpepper.Cleanup;

// Stub type so the assembly is non-empty until real types arrive in Task 4.
internal static class Placeholder
{
    public const string Marker = "Winpepper.Cleanup";
}
```

- [ ] **Step 6: Stub `src/Winpepper.Corrections/Placeholder.cs`**

```csharp
namespace Winpepper.Corrections;

internal static class Placeholder
{
    public const string Marker = "Winpepper.Corrections";
}
```

- [ ] **Step 7: Create the test project for cleanup `tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj`**

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
    <ProjectReference Include="..\..\src\Winpepper.Cleanup\Winpepper.Cleanup.csproj" />
    <ProjectReference Include="..\..\src\Winpepper.Corrections\Winpepper.Corrections.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 8: Create `tests/Winpepper.Cleanup.Tests/SanityTests.cs`** (compile-only smoke)

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class SanityTests
{
    [Fact]
    public void AssemblyLoads()
    {
        typeof(Winpepper.Cleanup.Placeholder).FullName.ShouldBe("Winpepper.Cleanup.Placeholder");
    }
}
```

(The `internal` Placeholder needs `InternalsVisibleTo` to be referenced from tests. Simpler: instead make the test reference the type indirectly — but for this sanity step we add `<InternalsVisibleTo Include="Winpepper.Cleanup.Tests" />` inside the cleanup csproj's `<ItemGroup>`):

Add this `<ItemGroup>` to `src/Winpepper.Cleanup/Winpepper.Cleanup.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Winpepper.Cleanup.Tests" />
  </ItemGroup>
```

And the matching block for `src/Winpepper.Corrections/Winpepper.Corrections.csproj`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Winpepper.Corrections.Tests" />
  </ItemGroup>
```

- [ ] **Step 9: Create the test project for corrections `tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj`**

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
    <ProjectReference Include="..\..\src\Winpepper.Corrections\Winpepper.Corrections.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 10: Create `tests/Winpepper.Corrections.Tests/SanityTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class SanityTests
{
    [Fact]
    public void AssemblyLoads()
    {
        typeof(Winpepper.Corrections.Placeholder).FullName.ShouldBe("Winpepper.Corrections.Placeholder");
    }
}
```

- [ ] **Step 11: Add the four new projects to the solution**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet sln add src/Winpepper.Cleanup/Winpepper.Cleanup.csproj
dotnet sln add src/Winpepper.Corrections/Winpepper.Corrections.csproj
dotnet sln add tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj
dotnet sln add tests/Winpepper.Corrections.Tests/Winpepper.Corrections.Tests.csproj
```

- [ ] **Step 12: Restore + build on Linux (cross-compiling the Windows TFM)**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build
```

Expected: build succeeds for both TFMs of `Winpepper.Cleanup` and the single TFM of `Winpepper.Corrections`. The two new SanityTests pass on Linux for `net9.0` and are restored (but won't run) for the Windows TFM.

- [ ] **Step 13: Run all Linux-runnable tests to confirm no regressions**

```bash
dotnet test --filter "Platform!=Windows"
```

Expected: all Plan 1 tests pass plus the two new sanity tests. No new failures.

- [ ] **Step 14: Sync + build on the VM to confirm Windows TFM still works**

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test"
```

Expected: green on the VM.

- [ ] **Step 15: Commit**

```bash
git add Directory.Build.props Directory.Packages.props \
    src/Winpepper.Cleanup src/Winpepper.Corrections \
    tests/Winpepper.Cleanup.Tests tests/Winpepper.Corrections.Tests \
    winpepper.sln
git commit -m "scaffold(plan2): cleanup + corrections projects + packages"
```

---

## Task 2: CorrectionsData record + validation rules

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Corrections/CorrectionsData.cs`
- Create: `$REPO_ROOT/src/Winpepper.Corrections/CorrectionValidation.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Corrections.Tests/CorrectionValidationTests.cs`

This task defines the in-memory schema and the input-validation rules from spec §7.3 (Corrections tab inline validation, repeated here so the data layer enforces them regardless of UI). Pure C#, runs everywhere.

- [ ] **Step 1: Write failing test `tests/Winpepper.Corrections.Tests/CorrectionValidationTests.cs`**

```csharp
using Shouldly;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionValidationTests
{
    [Theory]
    [InlineData("ChatGPT", true)]
    [InlineData("ab", true)]                  // min length 2
    [InlineData("a", false)]                  // too short
    [InlineData("", false)]                   // empty
    [InlineData("   ", false)]                // whitespace-only
    public void ValidatePreferred_AppliesLengthAndWhitespaceRules(string value, bool expected)
    {
        CorrectionValidation.IsValidPreferred(value).ShouldBe(expected);
    }

    [Theory]
    [InlineData("chat gbt", "ChatGPT", true)]
    [InlineData("ab", "cd", true)]
    [InlineData("a", "ChatGPT", false)]            // wrong side too short
    [InlineData("chat gbt", "a", false)]           // right side too short
    [InlineData("chat gbt", "chat gbt", false)]    // self-mapping
    [InlineData("ChatGPT", "chatgpt", true)]       // case differences are allowed
    [InlineData("  chat gbt  ", "ChatGPT", false)] // leading/trailing whitespace banned
    [InlineData("", "ChatGPT", false)]
    [InlineData("chat gbt", "", false)]
    public void ValidateReplacement_AppliesAllRules(string wrong, string right, bool expected)
    {
        CorrectionValidation.IsValidReplacement(wrong, right).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run it to confirm failure**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test --filter "FullyQualifiedName~CorrectionValidationTests"
```

Expected: build fails — `CorrectionValidation` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Corrections/CorrectionValidation.cs`**

```csharp
namespace Winpepper.Corrections;

/// <summary>
/// Input validation rules for the Preferred and Replacements lists.
/// Spec §7.3: "no empty strings, no duplicates, no self-mappings, minimum length 2".
/// (Duplicate checking is done at the list level by <see cref="CorrectionStore"/>.)
/// </summary>
public static class CorrectionValidation
{
    public const int MinLength = 2;

    public static bool IsValidPreferred(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length < MinLength) return false;
        if (value.Trim().Length != value.Length) return false; // no leading/trailing whitespace
        return true;
    }

    public static bool IsValidReplacement(string? wrong, string? right)
    {
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return false;
        if (wrong.Length < MinLength || right.Length < MinLength) return false;
        if (wrong.Trim().Length != wrong.Length) return false;
        if (right.Trim().Length != right.Length) return false;
        if (string.Equals(wrong, right, StringComparison.Ordinal)) return false; // self-mapping
        return true;
    }
}
```

- [ ] **Step 4: Implement `src/Winpepper.Corrections/CorrectionsData.cs`**

```csharp
using System.Text.Json.Serialization;

namespace Winpepper.Corrections;

/// <summary>
/// Persisted shape of <c>corrections.json</c>. Schema-versioned for forward compat.
/// Spec §8.1.
/// </summary>
public sealed record CorrectionsData
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("preferred")]
    public IReadOnlyList<string> Preferred { get; init; } = Array.Empty<string>();

    [JsonPropertyName("replacements")]
    public IReadOnlyDictionary<string, string> Replacements { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public static CorrectionsData Empty { get; } = new();
}
```

- [ ] **Step 5: Run the validation tests**

```bash
dotnet test --filter "FullyQualifiedName~CorrectionValidationTests"
```

Expected: 13 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Corrections/CorrectionsData.cs \
    src/Winpepper.Corrections/CorrectionValidation.cs \
    tests/Winpepper.Corrections.Tests/CorrectionValidationTests.cs
git commit -m "feat(corrections): CorrectionsData schema + input validation rules"
```

---

## Task 3: CorrectionStore — atomic JSON persistence

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Corrections/CorrectionStore.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Corrections.Tests/CorrectionStoreTests.cs`

- [ ] **Step 1: Write failing test `tests/Winpepper.Corrections.Tests/CorrectionStoreTests.cs`**

```csharp
using Shouldly;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Corrections.Tests;

public class CorrectionStoreTests : IDisposable
{
    private readonly string _path;

    public CorrectionStoreTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"corrections-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_path)}.tmp-*"))
            File.Delete(f);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmpty()
    {
        var store = new CorrectionStore(_path);
        var data = store.Load();
        data.Schema.ShouldBe(CorrectionsData.CurrentSchema);
        data.Preferred.ShouldBeEmpty();
        data.Replacements.ShouldBeEmpty();
    }

    [Fact]
    public void Save_Then_Load_RoundTrips()
    {
        var store = new CorrectionStore(_path);
        var data = new CorrectionsData
        {
            Preferred = new[] { "ChatGPT", "Anthropic" },
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
                ["ann thropic"] = "Anthropic",
            },
        };
        store.Save(data);

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT", "Anthropic" });
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT");
        loaded.Replacements["ann thropic"].ShouldBe("Anthropic");
    }

    [Fact]
    public void Load_BadJson_FallsBackToEmpty()
    {
        File.WriteAllText(_path, "{ not json");
        var data = new CorrectionStore(_path).Load();
        data.ShouldBe(CorrectionsData.Empty);
    }

    [Fact]
    public void Load_FutureSchema_FallsBackToEmpty()
    {
        File.WriteAllText(_path, """{ "schema": 999, "preferred": [], "replacements": {} }""");
        var data = new CorrectionStore(_path).Load();
        data.Schema.ShouldBe(CorrectionsData.CurrentSchema);
        data.Preferred.ShouldBeEmpty();
    }

    [Fact]
    public void Save_DoesNotLeave_TempFile()
    {
        var store = new CorrectionStore(_path);
        store.Save(CorrectionsData.Empty);
        Directory.GetFiles(Path.GetDirectoryName(_path)!, $"{Path.GetFileName(_path)}.tmp-*")
            .Length.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_AppendsUnique_AndPersists()
    {
        var store = new CorrectionStore(_path);
        store.AddPreferred("ChatGPT").ShouldBeTrue();
        store.AddPreferred("ChatGPT").ShouldBeFalse(); // duplicate (Ordinal compare)
        store.AddPreferred("Anthropic").ShouldBeTrue();

        var loaded = new CorrectionStore(_path).Load();
        loaded.Preferred.ShouldBe(new[] { "ChatGPT", "Anthropic" });
    }

    [Fact]
    public void AddPreferred_RejectsInvalid()
    {
        var store = new CorrectionStore(_path);
        store.AddPreferred("a").ShouldBeFalse(); // too short
        store.AddPreferred(" ").ShouldBeFalse();
        new CorrectionStore(_path).Load().Preferred.ShouldBeEmpty();
    }

    [Fact]
    public void AddReplacement_StoresAndPersists()
    {
        var store = new CorrectionStore(_path);
        store.AddReplacement("chat gbt", "ChatGPT").ShouldBeTrue();
        store.AddReplacement("chat gbt", "chat gbt").ShouldBeFalse(); // self-mapping rejected
        store.AddReplacement("chat gbt", "ChatGPT-NewMapping").ShouldBeTrue(); // overwrite is allowed

        var loaded = new CorrectionStore(_path).Load();
        loaded.Replacements["chat gbt"].ShouldBe("ChatGPT-NewMapping");
    }
}
```

- [ ] **Step 2: Run the test to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~CorrectionStoreTests"
```

Expected: build fails — `CorrectionStore` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Corrections/CorrectionStore.cs`**

```csharp
using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.Corrections;

/// <summary>
/// Persists <see cref="CorrectionsData"/> to disk atomically. Spec §8.1.
/// Path is typically <c>%LOCALAPPDATA%\winpepper\corrections.json</c> but is
/// injected so tests can use temp paths.
///
/// Concurrency: a single in-process instance per file is expected. The store
/// re-reads the file inside Add* methods so a stale handle never overwrites
/// a concurrent edit by another instance of the same process.
/// </summary>
public sealed class CorrectionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public CorrectionStore(string path) { _path = path; }

    public string Path => _path;

    public CorrectionsData Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    private CorrectionsData LoadLocked()
    {
        if (!File.Exists(_path)) return CorrectionsData.Empty;

        try
        {
            var json = File.ReadAllText(_path);
            var parsed = JsonSerializer.Deserialize<CorrectionsData>(json, JsonOptions);
            if (parsed is null) return CorrectionsData.Empty;
            if (parsed.Schema != CorrectionsData.CurrentSchema) return CorrectionsData.Empty;
            return parsed;
        }
        catch (JsonException)
        {
            return CorrectionsData.Empty;
        }
    }

    public void Save(CorrectionsData data)
    {
        lock (_gate)
        {
            SaveLocked(data);
        }
    }

    private void SaveLocked(CorrectionsData data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        AtomicFile.WriteAllText(_path, json);
    }

    /// <summary>
    /// Adds a preferred string if it passes validation and isn't already present
    /// (Ordinal comparison). Returns false otherwise.
    /// </summary>
    public bool AddPreferred(string value)
    {
        if (!CorrectionValidation.IsValidPreferred(value)) return false;
        lock (_gate)
        {
            var data = LoadLocked();
            if (data.Preferred.Contains(value, StringComparer.Ordinal)) return false;
            var next = data with
            {
                Preferred = data.Preferred.Concat(new[] { value }).ToArray(),
            };
            SaveLocked(next);
            return true;
        }
    }

    /// <summary>
    /// Adds or overwrites a "wrong → right" replacement. Returns false when
    /// validation fails.
    /// </summary>
    public bool AddReplacement(string wrong, string right)
    {
        if (!CorrectionValidation.IsValidReplacement(wrong, right)) return false;
        lock (_gate)
        {
            var data = LoadLocked();
            var dict = new Dictionary<string, string>(data.Replacements, StringComparer.Ordinal)
            {
                [wrong] = right,
            };
            SaveLocked(data with { Replacements = dict });
            return true;
        }
    }

    /// <summary>
    /// Removes a preferred entry if present. Returns true when something changed.
    /// </summary>
    public bool RemovePreferred(string value)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            var filtered = data.Preferred.Where(s => !string.Equals(s, value, StringComparison.Ordinal)).ToArray();
            if (filtered.Length == data.Preferred.Count) return false;
            SaveLocked(data with { Preferred = filtered });
            return true;
        }
    }

    /// <summary>
    /// Removes a replacement entry if present. Returns true when something changed.
    /// </summary>
    public bool RemoveReplacement(string wrong)
    {
        lock (_gate)
        {
            var data = LoadLocked();
            if (!data.Replacements.ContainsKey(wrong)) return false;
            var dict = new Dictionary<string, string>(data.Replacements, StringComparer.Ordinal);
            dict.Remove(wrong);
            SaveLocked(data with { Replacements = dict });
            return true;
        }
    }
}
```

- [ ] **Step 4: Run the tests**

```bash
dotnet test --filter "FullyQualifiedName~CorrectionStoreTests"
```

Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Corrections/CorrectionStore.cs \
    tests/Winpepper.Corrections.Tests/CorrectionStoreTests.cs
git commit -m "feat(corrections): CorrectionStore with atomic persistence + Add/Remove APIs"
```

---

## Task 4: Cleanup result + options + profile + base prompt text

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/CleanupProfile.cs`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/CleanupOptions.cs`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/CleanupResult.cs`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/BasePrompts.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs`

Pure-data step. The texts here are the §6.3 default prompt and §6.4 literal-profile prompt. The build will start failing on the cleanup project until this lands because Task 5's `PromptBuilder` references `CleanupProfile`.

- [ ] **Step 1: Write failing test `tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs`**

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class BasePromptsTests
{
    [Fact]
    public void Default_MentionsFillerWords()
    {
        var p = BasePrompts.Default;
        // Each of these filler words must appear in the default prompt per §6.3.
        foreach (var filler in new[] { "um", "uh", "like", "you know", "basically", "literally", "sort of", "kind of" })
            p.ShouldContain(filler, Case.Sensitive);
    }

    [Fact]
    public void Default_MentionsSelfCorrectionCommands()
    {
        var p = BasePrompts.Default;
        p.ShouldContain("scratch that");
        p.ShouldContain("never mind");
        p.ShouldContain("start over");
    }

    [Fact]
    public void Default_RequiresFullTranscriptReproduction()
    {
        var p = BasePrompts.Default;
        p.ShouldContain("never summarize", Case.Insensitive);
    }

    [Fact]
    public void Default_HasThreeExamples()
    {
        var p = BasePrompts.Default;
        // Examples are blocks starting with "Input:" / "Output:".
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(3);
        outputs.ShouldBe(3);
    }

    [Fact]
    public void Literal_DisablesFillerRemoval()
    {
        BasePrompts.Literal.ShouldContain("do not remove filler", Case.Insensitive);
        BasePrompts.Literal.ShouldContain("punctuation", Case.Insensitive);
    }

    [Fact]
    public void ForProfile_DefaultsRouteCorrectly()
    {
        BasePrompts.ForProfile(CleanupProfile.Ordinary, custom: null).ShouldBe(BasePrompts.Default);
        BasePrompts.ForProfile(CleanupProfile.Literal,  custom: null).ShouldBe(BasePrompts.Literal);
    }

    [Fact]
    public void ForProfile_Custom_UsesProvidedText()
    {
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: "MyPrompt").ShouldBe("MyPrompt");
    }

    [Fact]
    public void ForProfile_Custom_FallsBackToDefault_OnNullOrWhitespace()
    {
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: null).ShouldBe(BasePrompts.Default);
        BasePrompts.ForProfile(CleanupProfile.Custom, custom: "   ").ShouldBe(BasePrompts.Default);
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~BasePromptsTests"
```

Expected: build error — `CleanupProfile` / `BasePrompts` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Cleanup/CleanupProfile.cs`**

```csharp
namespace Winpepper.Cleanup;

/// <summary>Cleanup prompt profile, spec §6.4.</summary>
public enum CleanupProfile
{
    /// <summary>Default conversational dictation cleanup.</summary>
    Ordinary,

    /// <summary>Minimal rewriting: punctuation only, no filler removal.</summary>
    Literal,

    /// <summary>User-supplied base prompt.</summary>
    Custom,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Cleanup/CleanupOptions.cs`**

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Per-session options handed to <c>CleanupRunner.RunAsync</c>. Spec §5.5 + §6.4.
/// </summary>
public sealed record CleanupOptions
{
    public CleanupProfile Profile { get; init; } = CleanupProfile.Ordinary;

    /// <summary>Custom base prompt; only used when <see cref="Profile"/> is Custom.</summary>
    public string? CustomBasePrompt { get; init; }

    /// <summary>Max time the whole runner is allowed to spend, including pre-warm wait.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Greedy temperature; spec §5.5 says 0.1.</summary>
    public float Temperature { get; init; } = 0.1f;

    /// <summary>Window-context wait budget. Spec §6.1 sets 500 ms.</summary>
    public TimeSpan WindowContextWait { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Whether to attach window context at all. Default false (Ghost Pepper parity, §6.1).</summary>
    public bool WindowContextEnabled { get; init; }

    /// <summary>Hard cap on output tokens used when the formula in §5.5 would otherwise exceed it.</summary>
    public int MaxNewTokensCap { get; init; } = 2048;
}
```

- [ ] **Step 5: Implement `src/Winpepper.Cleanup/CleanupResult.cs`**

```csharp
namespace Winpepper.Cleanup;

/// <summary>Outcome of one <c>CleanupRunner</c> invocation.</summary>
public sealed record CleanupResult(
    string CleanedText,
    CleanupPath Path,
    string RawModelOutput,
    string AssembledPrompt,
    TimeSpan Elapsed);

/// <summary>Which branch the runner took. Surfaced in the History detail later.</summary>
public enum CleanupPath
{
    Llm,                 // The LLM returned usable text after sanitization.
    FallbackEmpty,       // The LLM returned empty/whitespace after sanitization.
    FallbackEllipsis,    // The LLM returned "..." (with or without whitespace).
    FallbackTimeout,     // The 15s timeout fired.
    FallbackBackendError, // The backend threw.
}
```

- [ ] **Step 6: Implement `src/Winpepper.Cleanup/BasePrompts.cs`**

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Built-in cleanup base prompts. Spec §6.3 (Default) and §6.4 (Literal).
/// </summary>
public static class BasePrompts
{
    public const string Default = """
You are a dictation cleanup assistant. The user spoke into a microphone and an
automatic speech recognizer produced the raw transcript inside the USER-INPUT
block. Your job is to return the same content in clean written form.

Apply these transformations:

1. Remove these filler words and phrases when they do not carry meaning:
   um, uh, like, you know, basically, literally, sort of, kind of.
2. Apply self-correction commands literally: when the speaker says
   "scratch that", "never mind", or "no let me start over", delete the
   preceding clause or sentence as appropriate and continue with the next
   spoken content.
3. Fix obvious recognition errors for names, commands, file paths, and jargon
   when the surrounding context makes the correct spelling unambiguous. When
   in doubt, prefer the user's spoken words.
4. Add sentence-level punctuation and capitalization that the recognizer omits.
5. Honor explicit punctuation and spelling commands ("comma", "period",
   "spell that", etc.) — render the punctuation literally and never echo the
   command word.
6. Reproduce the entire transcript. Never summarize, never delete sentences
   that the speaker meant to keep, never paraphrase content away.

The output must read as if a human had typed it directly. Output the cleaned
text and nothing else — no preamble, no closing remark, no quoting, no
explanation of changes.

Three examples follow.

Input: um so like I think we should basically just ship it tomorrow you know
Output: I think we should just ship it tomorrow.

Input: write me a function called add_numbers no wait scratch that call it sum
Output: Write me a function called sum.

Input: send the message to anne thropic about the chat gbt integration
Output: Send the message to Anthropic about the ChatGPT integration.
""";

    public const string Literal = """
You are a dictation transcription cleaner. Output the speaker's words exactly
as transcribed, with two changes only:

1. Add sentence punctuation and capitalization.
2. Honor explicit punctuation and spelling commands literally.

Do not remove filler words. Do not paraphrase. Do not interpret self-correction
commands; leave them in the output as spoken. Output the cleaned text and
nothing else.
""";

    public static string ForProfile(CleanupProfile profile, string? custom) =>
        profile switch
        {
            CleanupProfile.Ordinary => Default,
            CleanupProfile.Literal  => Literal,
            CleanupProfile.Custom   => string.IsNullOrWhiteSpace(custom) ? Default : custom!,
            _                       => Default,
        };
}
```

- [ ] **Step 7: Run the BasePromptsTests**

```bash
dotnet test --filter "FullyQualifiedName~BasePromptsTests"
```

Expected: 8 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupProfile.cs \
    src/Winpepper.Cleanup/CleanupOptions.cs \
    src/Winpepper.Cleanup/CleanupResult.cs \
    src/Winpepper.Cleanup/BasePrompts.cs \
    tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs
git commit -m "feat(cleanup): result/options/profile records + §6.3 default and §6.4 literal prompts"
```

---

## Task 5: PromptBuilder — four-block assembly with omission rules

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/PromptBuilder.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs`

Pure-string composition. Spec §6.2. Cross-platform, fully testable on Linux.

- [ ] **Step 1: Write failing test `tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs`**

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class PromptBuilderTests
{
    private static CorrectionsData Data(IReadOnlyList<string>? preferred = null,
                                        IReadOnlyDictionary<string, string>? replacements = null) =>
        new()
        {
            Preferred = preferred ?? Array.Empty<string>(),
            Replacements = replacements ?? new Dictionary<string, string>(),
        };

    [Fact]
    public void Build_AllBlocksPresent_JoinsWithBlankLines()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: "WINDOW",
            userInput: "raw transcript");

        prompt.ShouldContain("<BASE-PROMPT>\nBASE\n</BASE-PROMPT>");
        prompt.ShouldContain("<CORRECTION-HINTS>");
        prompt.ShouldContain("- ChatGPT");
        prompt.ShouldContain("- chat gbt -> ChatGPT");
        prompt.ShouldContain("<OCR-RULES>");
        prompt.ShouldContain("<WINDOW-OCR-CONTENT>\nWINDOW\n</WINDOW-OCR-CONTENT>");
        prompt.ShouldContain("<USER-INPUT>\nraw transcript\n</USER-INPUT>");
        prompt.ShouldContain("\n\n"); // blocks separated by blank lines
    }

    [Fact]
    public void Build_NoCorrections_OmitsCorrectionHintsBlock()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: "WINDOW",
            userInput: "x");

        prompt.ShouldNotContain("<CORRECTION-HINTS>");
    }

    [Fact]
    public void Build_NoWindowContext_OmitsOcrBlocks()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: null,
            userInput: "x");

        prompt.ShouldNotContain("<OCR-RULES>");
        prompt.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void Build_EmptyWindowContext_OmitsOcrBlocks()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: "   ",
            userInput: "x");

        prompt.ShouldNotContain("<OCR-RULES>");
        prompt.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void Build_PreferredOnly_StillRendersCorrectionHints()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, replacements: null),
            windowContext: null,
            userInput: "x");

        prompt.ShouldContain("Preferred transcriptions:");
        prompt.ShouldContain("- ChatGPT");
        prompt.ShouldNotContain("Misheard replacements:");
    }

    [Fact]
    public void Build_ReplacementsOnly_StillRendersCorrectionHints()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: Data(preferred: null, replacements: new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: null,
            userInput: "x");

        prompt.ShouldNotContain("Preferred transcriptions:");
        prompt.ShouldContain("Misheard replacements:");
        prompt.ShouldContain("- chat gbt -> ChatGPT");
    }

    [Fact]
    public void Build_TruncatesWindowContext_To4000Chars()
    {
        var long40k = new string('x', 40_000);
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: long40k,
            userInput: "x");

        // <WINDOW-OCR-CONTENT>\n{<=4000 chars}\n</WINDOW-OCR-CONTENT>
        var start = prompt.IndexOf("<WINDOW-OCR-CONTENT>\n", StringComparison.Ordinal) + "<WINDOW-OCR-CONTENT>\n".Length;
        var end = prompt.IndexOf("\n</WINDOW-OCR-CONTENT>", StringComparison.Ordinal);
        (end - start).ShouldBeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void Build_TrimsRawTranscript()
    {
        var prompt = PromptBuilder.Build(
            basePrompt: "BASE",
            corrections: CorrectionsData.Empty,
            windowContext: null,
            userInput: "  hello world  ");

        prompt.ShouldContain("<USER-INPUT>\nhello world\n</USER-INPUT>");
    }
}
```

- [ ] **Step 2: Run it to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~PromptBuilderTests"
```

Expected: build fails — `PromptBuilder` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Cleanup/PromptBuilder.cs`**

```csharp
using System.Text;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Assembles the four-block cleanup prompt per spec §6.2. Pure-string,
/// stateless. Omission rules:
/// - &lt;CORRECTION-HINTS&gt; omitted iff both preferred and replacements are empty.
/// - &lt;OCR-RULES&gt; and &lt;WINDOW-OCR-CONTENT&gt; omitted iff windowContext
///   is null, whitespace, or empty after truncation.
/// - The window-context body is truncated to 4000 chars (spec §6.1 / §6.2).
/// </summary>
public static class PromptBuilder
{
    public const int WindowContextMaxChars = 4000;

    public static string Build(
        string basePrompt,
        CorrectionsData corrections,
        string? windowContext,
        string userInput)
    {
        var sb = new StringBuilder(capacity: 8192);

        // <BASE-PROMPT>
        sb.Append("<BASE-PROMPT>\n").Append(basePrompt).Append("\n</BASE-PROMPT>");

        // <CORRECTION-HINTS> (omit when both lists empty)
        var hasPreferred = corrections.Preferred.Count > 0;
        var hasReplacements = corrections.Replacements.Count > 0;
        if (hasPreferred || hasReplacements)
        {
            sb.Append("\n\n<CORRECTION-HINTS>");
            if (hasPreferred)
            {
                sb.Append("\nPreferred transcriptions:");
                foreach (var p in corrections.Preferred)
                    sb.Append("\n- ").Append(p);
            }
            if (hasReplacements)
            {
                sb.Append("\nMisheard replacements:");
                foreach (var kvp in corrections.Replacements)
                    sb.Append("\n- ").Append(kvp.Key).Append(" -> ").Append(kvp.Value);
            }
            sb.Append("\n</CORRECTION-HINTS>");
        }

        // <OCR-RULES> + <WINDOW-OCR-CONTENT> (omit when window context is empty)
        var truncated = TruncateWindowContext(windowContext);
        if (!string.IsNullOrEmpty(truncated))
        {
            sb.Append("\n\n<OCR-RULES>\n")
              .Append("The WINDOW-OCR-CONTENT below is the text currently visible on the user's screen.\n")
              .Append("Use it only to disambiguate names, commands, file paths, and jargon.\n")
              .Append("Prefer the user's spoken words; never substitute OCR text wholesale.")
              .Append("\n</OCR-RULES>");

            sb.Append("\n\n<WINDOW-OCR-CONTENT>\n").Append(truncated).Append("\n</WINDOW-OCR-CONTENT>");
        }

        // <USER-INPUT>
        sb.Append("\n\n<USER-INPUT>\n").Append((userInput ?? string.Empty).Trim()).Append("\n</USER-INPUT>");

        return sb.ToString();
    }

    private static string? TruncateWindowContext(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw!.Trim();
        if (trimmed.Length <= WindowContextMaxChars) return trimmed;
        return trimmed.Substring(0, WindowContextMaxChars);
    }
}
```

- [ ] **Step 4: Verify all PromptBuilder tests pass**

```bash
dotnet test --filter "FullyQualifiedName~PromptBuilderTests"
```

Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Cleanup/PromptBuilder.cs \
    tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs
git commit -m "feat(cleanup): PromptBuilder with omission rules and 4000-char truncation"
```

---

## Task 6: ThinkSanitizer — strip `<think>` blocks and orphan opening tags

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/ThinkSanitizer.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/ThinkSanitizerTests.cs`

Spec §5.5: "Strips `<think>…</think>` blocks and orphan opening `<think>` tags from the output." Pure C#.

- [ ] **Step 1: Write failing test `tests/Winpepper.Cleanup.Tests/ThinkSanitizerTests.cs`**

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class ThinkSanitizerTests
{
    [Theory]
    [InlineData("hello",                                       "hello")]
    [InlineData("<think>thoughts</think>hello",                "hello")]
    [InlineData("before<think>thoughts</think>after",          "beforeafter")]
    [InlineData("<think>multi\nline\nstuff</think>output",     "output")]
    [InlineData("<think>a</think><think>b</think>tail",        "tail")]
    public void Sanitize_StripsBalancedThinkBlocks(string input, string expected)
    {
        ThinkSanitizer.Sanitize(input).ShouldBe(expected);
    }

    [Fact]
    public void Sanitize_OrphanOpeningTag_StripsFromTagToEnd()
    {
        // Model emitted <think> but ran out of tokens before closing it.
        // Per spec §5.5, drop the orphan and everything after.
        ThinkSanitizer.Sanitize("hello<think>started thinking and was cut off")
            .ShouldBe("hello");
    }

    [Fact]
    public void Sanitize_OnlyClosingTag_LeavesUnchanged()
    {
        // No opening tag — leave the (unusual) </think> alone rather than panic.
        ThinkSanitizer.Sanitize("hello</think>world")
            .ShouldBe("hello</think>world");
    }

    [Fact]
    public void Sanitize_TrimsResultingWhitespace()
    {
        ThinkSanitizer.Sanitize("  <think>x</think>  hello  ").ShouldBe("hello");
    }

    [Fact]
    public void Sanitize_PreservesInternalContent_AroundStrippedBlocks()
    {
        ThinkSanitizer.Sanitize("alpha <think>internal</think> beta")
            .ShouldBe("alpha  beta".Trim());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        ThinkSanitizer.Sanitize(input).ShouldBe("");
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~ThinkSanitizerTests"
```

Expected: build error — `ThinkSanitizer` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Cleanup/ThinkSanitizer.cs`**

```csharp
using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Strips reasoning-style scratchpad markup from LLM output. Spec §5.5.
/// Handles both balanced <c>&lt;think&gt;...&lt;/think&gt;</c> blocks and
/// orphan opening <c>&lt;think&gt;</c> tags (where the model ran out of
/// tokens before closing).
/// </summary>
public static class ThinkSanitizer
{
    // Non-greedy, multi-line. The dotall flag lets `.` span newlines.
    private static readonly Regex BalancedThinkBlock = new(
        @"<think>.*?</think>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    // Orphan opening: <think> with no later </think>. We strip from <think> to end.
    private static readonly Regex OrphanOpening = new(
        @"<think>.*$",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        // 1) Strip all balanced blocks.
        var stripped = BalancedThinkBlock.Replace(raw, string.Empty);

        // 2) Any remaining <think> with no matching </think> = orphan; strip to EOF.
        stripped = OrphanOpening.Replace(stripped, string.Empty);

        return stripped.Trim();
    }
}
```

- [ ] **Step 4: Verify tests pass**

```bash
dotnet test --filter "FullyQualifiedName~ThinkSanitizerTests"
```

Expected: all cases pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Cleanup/ThinkSanitizer.cs \
    tests/Winpepper.Cleanup.Tests/ThinkSanitizerTests.cs
git commit -m "feat(cleanup): <think> block sanitizer with orphan-tag handling"
```

---

## Task 7: CaseAwareReplacer — case-preserving deterministic substitution

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/CaseAwareReplacer.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/CaseAwareReplacerTests.cs`

Spec §6.5: "CorrectionStore.Replacements is applied to the text as a final case-preserving substitution pass." Pure C#.

The transformation: when matching is case-insensitive but the matched span uses a particular case shape, the replacement should mimic it. Concretely:

- `chat gbt` in lowercase → emit `ChatGPT` as-is (model preserves its own canonical case).
- `Chat Gbt` (Title Case) → still emit the canonical replacement (the right-hand side is the "preferred" spelling).

For Plan 2 we keep it simple: case-insensitive *match*, but emit the replacement string verbatim. This matches Ghost Pepper behavior and avoids surprising users who configured `ChatGPT` as the replacement.

- [ ] **Step 1: Write failing test `tests/Winpepper.Cleanup.Tests/CaseAwareReplacerTests.cs`**

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CaseAwareReplacerTests
{
    private static IReadOnlyDictionary<string, string> Dict(params (string K, string V)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void Apply_NoReplacements_ReturnsInputUnchanged()
    {
        CaseAwareReplacer.Apply("hello world", Dict()).ShouldBe("hello world");
    }

    [Fact]
    public void Apply_LowercaseMatch_EmitsCanonicalReplacement()
    {
        CaseAwareReplacer.Apply("we tested chat gbt today", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("we tested ChatGPT today");
    }

    [Fact]
    public void Apply_TitleCaseMatch_StillEmitsCanonicalReplacement()
    {
        CaseAwareReplacer.Apply("Chat Gbt is misnamed.", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("ChatGPT is misnamed.");
    }

    [Fact]
    public void Apply_OnlyMatchesWholeWords_NotSubstrings()
    {
        // "chat gbt" must not match "chatgbtstuff" or "prechat gbt".
        CaseAwareReplacer.Apply("chatgbt foo prechat gbt bar chat gbt baz",
                                 Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("chatgbt foo prechat gbt bar ChatGPT baz");
    }

    [Fact]
    public void Apply_MultipleMatches_AllReplaced()
    {
        CaseAwareReplacer.Apply("chat gbt and chat gbt again", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("ChatGPT and ChatGPT again");
    }

    [Fact]
    public void Apply_MultipleRules_AppliedInDeterministicOrder()
    {
        var input = "ann thropic plus chat gbt";
        var result = CaseAwareReplacer.Apply(input, Dict(
            ("chat gbt", "ChatGPT"),
            ("ann thropic", "Anthropic")));
        result.ShouldBe("Anthropic plus ChatGPT");
    }

    [Fact]
    public void Apply_OverlappingRules_LongerWins()
    {
        // "chat gbt model" beats "chat gbt".
        var result = CaseAwareReplacer.Apply("the chat gbt model is here", Dict(
            ("chat gbt", "ChatGPT"),
            ("chat gbt model", "GPT model")));
        result.ShouldBe("the GPT model is here");
    }

    [Fact]
    public void Apply_PunctuationAdjacentMatches_AreReplaced()
    {
        CaseAwareReplacer.Apply("(chat gbt), and chat gbt.", Dict(("chat gbt", "ChatGPT")))
            .ShouldBe("(ChatGPT), and ChatGPT.");
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~CaseAwareReplacerTests"
```

Expected: build error — `CaseAwareReplacer` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Cleanup/CaseAwareReplacer.cs`**

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Applies <see cref="Winpepper.Corrections.CorrectionsData.Replacements"/> as a
/// deterministic case-insensitive whole-word substitution pass. Spec §6.5.
/// The replacement string is emitted verbatim — users configure the canonical
/// spelling, so we don't smear it back into the matched case.
///
/// Overlap handling: when two rules overlap at a position, the longer key wins.
/// Within the same key, leftmost match wins.
/// </summary>
public static class CaseAwareReplacer
{
    public static string Apply(string text, IReadOnlyDictionary<string, string> replacements)
    {
        if (string.IsNullOrEmpty(text) || replacements.Count == 0) return text;

        // Sort keys by length descending so longer keys are attempted first
        // (Regex alternation alone doesn't guarantee longest-match).
        var keys = replacements.Keys
            .Where(k => !string.IsNullOrEmpty(k))
            .OrderByDescending(k => k.Length)
            .ToList();

        if (keys.Count == 0) return text;

        // Build a single regex: \b(?:k1|k2|k3)\b, case-insensitive.
        // \b ensures whole-word matching.
        var pattern = @"\b(?:" + string.Join("|", keys.Select(Regex.Escape)) + @")\b";
        var rx = new Regex(pattern, RegexOptions.IgnoreCase);

        var sb = new StringBuilder(text.Length + 64);
        var lastIndex = 0;
        foreach (Match m in rx.Matches(text))
        {
            sb.Append(text, lastIndex, m.Index - lastIndex);

            // Find the matching key (case-insensitive). Prefer the longest matched
            // key that fits at this index (regex already picked one but it doesn't
            // necessarily prefer longest — so re-scan).
            string? bestKey = null;
            foreach (var k in keys)
            {
                if (m.Index + k.Length > text.Length) continue;
                var slice = text.AsSpan(m.Index, k.Length);
                if (slice.Equals(k.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    bestKey = k; // keys are sorted longest-first
                    break;
                }
            }

            if (bestKey is null)
            {
                // Should not happen; defensive fallback.
                sb.Append(m.Value);
                lastIndex = m.Index + m.Length;
            }
            else
            {
                sb.Append(replacements[bestKey]);
                lastIndex = m.Index + bestKey.Length;
            }
        }

        if (lastIndex < text.Length)
            sb.Append(text, lastIndex, text.Length - lastIndex);

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Verify tests pass**

```bash
dotnet test --filter "FullyQualifiedName~CaseAwareReplacerTests"
```

Expected: 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Cleanup/CaseAwareReplacer.cs \
    tests/Winpepper.Cleanup.Tests/CaseAwareReplacerTests.cs
git commit -m "feat(cleanup): deterministic case-insensitive replacement pass (§6.5)"
```

---

## Task 8: ILlamaCleanupBackend seam + CleanupRunner with fake-backend tests

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/ILlamaCleanupBackend.cs`
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/CleanupRunner.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/Fakes/FakeLlamaCleanupBackend.cs`

This is the orchestration brain. Drive everything through the `ILlamaCleanupBackend` seam so timeout / empty / `"..."` / error fallback paths are unit-testable on Linux. The real backend lands in Task 9.

- [ ] **Step 1: Write `src/Winpepper.Cleanup/ILlamaCleanupBackend.cs`**

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Abstraction over the LlamaSharp context so <see cref="CleanupRunner"/> can
/// be unit-tested without loading a real model.
/// </summary>
public interface ILlamaCleanupBackend
{
    /// <summary>
    /// Run the model on the assembled prompt and return the raw output. The
    /// implementation is responsible for honoring <paramref name="ct"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string prompt,
        int maxNewTokens,
        float temperature,
        CancellationToken ct);
}
```

- [ ] **Step 2: Write the fake backend `tests/Winpepper.Cleanup.Tests/Fakes/FakeLlamaCleanupBackend.cs`**

```csharp
using Winpepper.Cleanup;

namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>
/// Configurable fake LLamaSharp backend for CleanupRunner unit tests.
/// </summary>
internal sealed class FakeLlamaCleanupBackend : ILlamaCleanupBackend
{
    public string Output { get; init; } = "";
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    public Exception? Throw { get; init; }
    public int CallCount { get; private set; }
    public string? LastPrompt { get; private set; }
    public int? LastMaxNewTokens { get; private set; }

    public async Task<string> GenerateAsync(string prompt, int maxNewTokens, float temperature, CancellationToken ct)
    {
        CallCount++;
        LastPrompt = prompt;
        LastMaxNewTokens = maxNewTokens;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Throw is not null) throw Throw;
        return Output;
    }
}
```

- [ ] **Step 3: Write failing test `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Cleanup.Tests.Fakes;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CleanupRunnerTests
{
    private static CleanupRunner NewRunner(ILlamaCleanupBackend backend) =>
        new(backend, new NullLogger<CleanupRunner>());

    private static CleanupOptions DefaultOptions() => new()
    {
        Profile = CleanupProfile.Ordinary,
        Timeout = TimeSpan.FromSeconds(1),
        WindowContextEnabled = false,
        WindowContextWait = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Run_LlmReturnsCleanText_UsesLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Hello world." });
        var result = await runner.RunAsync(
            rawTranscript: "um hello world",
            corrections: CorrectionsData.Empty,
            windowContextTask: null,
            options: DefaultOptions(),
            ct: CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsThinkBlock_StripsItBeforeUsingOutput()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "<think>reasoning</think>Hello world.",
        });
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsEmpty_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "" });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        };
        var result = await runner.RunAsync("we tested chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT");
        result.Path.ShouldBe(CleanupPath.FallbackEmpty);
    }

    [Fact]
    public async Task Run_LlmReturnsEllipsis_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "..." });
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("hello");
        result.Path.ShouldBe(CleanupPath.FallbackEllipsis);
    }

    [Fact]
    public async Task Run_LlmExceedsTimeout_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Delay = TimeSpan.FromSeconds(5),
            Output = "unused",
        });
        var opts = DefaultOptions() with { Timeout = TimeSpan.FromMilliseconds(50) };
        var result = await runner.RunAsync("hello world", CorrectionsData.Empty, null, opts, CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackTimeout);
        result.CleanedText.ShouldBe("hello world");
    }

    [Fact]
    public async Task Run_BackendThrows_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Throw = new InvalidOperationException("kaboom"),
        });
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("hello");
    }

    [Fact]
    public async Task Run_AppliesCorrectionPostPass_OnLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "we tested chat gbt." });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        };
        var result = await runner.RunAsync("raw", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_MaxNewTokens_FollowsSpecFormula()
    {
        // Spec §5.5: max_new_tokens = min(2048, ceil(transcript_chars * 2.0))
        // For a 100-char transcript that's min(2048, 200) = 200.
        var backend = new FakeLlamaCleanupBackend { Output = "x" };
        var runner = NewRunner(backend);
        await runner.RunAsync(new string('a', 100), CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(200);

        // For 5000-char transcript = min(2048, 10000) = 2048.
        await runner.RunAsync(new string('a', 5000), CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task Run_AwaitsWindowContext_UpTo500msThenProceeds()
    {
        // The window-context task hangs for 5s; the runner should give up at 50ms.
        var tcs = new TaskCompletionSource<string?>();
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(50),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync("raw", CorrectionsData.Empty, tcs.Task, opts, CancellationToken.None);
        sw.Stop();

        result.CleanedText.ShouldBe("cleaned");
        sw.ElapsedMilliseconds.ShouldBeLessThan(500); // bailed out at ~50ms
    }

    [Fact]
    public async Task Run_UsesWindowContext_WhenReadyInTime()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };
        await runner.RunAsync("raw", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastPrompt.ShouldContain("the foreground window says hello");
    }

    [Fact]
    public async Task Run_WindowContextDisabled_OmitsItEvenWhenTaskCompletes()
    {
        var ready = Task.FromResult<string?>("ignored");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { WindowContextEnabled = false };
        await runner.RunAsync("raw", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastPrompt.ShouldNotContain("ignored");
        backend.LastPrompt.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }
}
```

- [ ] **Step 4: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~CleanupRunnerTests"
```

Expected: build fails — `CleanupRunner` not defined.

- [ ] **Step 5: Implement `src/Winpepper.Cleanup/CleanupRunner.cs`**

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Orchestrates a cleanup attempt: optionally wait briefly for window context,
/// build the prompt, call the LLM with a timeout, sanitize the output, fall
/// back to a deterministic correction-only path on empty/"..."/timeout/error,
/// and always apply the case-aware substitution post-pass. Spec §5.5, §6.5.
/// </summary>
public sealed class CleanupRunner
{
    private readonly ILlamaCleanupBackend _backend;
    private readonly ILogger<CleanupRunner> _log;

    public CleanupRunner(ILlamaCleanupBackend backend, ILogger<CleanupRunner> log)
    {
        _backend = backend;
        _log = log;
    }

    public async Task<CleanupResult> RunAsync(
        string rawTranscript,
        CorrectionsData corrections,
        Task<string?>? windowContextTask,
        CleanupOptions options,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // 1) Resolve window context with a bounded wait.
        string? windowContext = null;
        if (options.WindowContextEnabled && windowContextTask is not null)
        {
            try
            {
                var completed = await Task.WhenAny(windowContextTask,
                                                   Task.Delay(options.WindowContextWait, ct))
                                          .ConfigureAwait(false);
                if (completed == windowContextTask)
                {
                    windowContext = await windowContextTask.ConfigureAwait(false);
                }
                else
                {
                    _log.LogDebug("Window-context prefetch exceeded {Budget}ms; proceeding without it",
                        options.WindowContextWait.TotalMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Window-context prefetch failed; proceeding without it");
            }
        }

        // 2) Build the assembled prompt.
        var basePrompt = BasePrompts.ForProfile(options.Profile, options.CustomBasePrompt);
        var assembled = PromptBuilder.Build(
            basePrompt: basePrompt,
            corrections: corrections,
            windowContext: windowContext,
            userInput: rawTranscript);

        // 3) Compute the max-new-tokens budget per spec §5.5.
        var maxTokens = Math.Min(options.MaxNewTokensCap, (int)Math.Ceiling(rawTranscript.Length * 2.0));
        if (maxTokens < 1) maxTokens = 1;

        // 4) Call the backend with a timeout token.
        string raw;
        CleanupPath chosenPath;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);
            raw = await _backend.GenerateAsync(assembled, maxTokens, options.Temperature, timeoutCts.Token)
                                .ConfigureAwait(false);
            chosenPath = CleanupPath.Llm;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Cleanup LLM timed out after {Timeout}ms; falling back to correction-only path",
                options.Timeout.TotalMilliseconds);
            return Finalize(rawTranscript, "", corrections, assembled, CleanupPath.FallbackTimeout, sw);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cleanup backend threw; falling back to correction-only path");
            return Finalize(rawTranscript, "", corrections, assembled, CleanupPath.FallbackBackendError, sw);
        }

        // 5) Sanitize <think> blocks.
        var sanitized = ThinkSanitizer.Sanitize(raw);

        // 6) Empty or "..." → fallback.
        if (string.IsNullOrWhiteSpace(sanitized))
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackEmpty, sw);

        if (sanitized.Trim() == "...")
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackEllipsis, sw);

        // 7) Apply deterministic correction post-pass.
        var withCorrections = CaseAwareReplacer.Apply(sanitized, corrections.Replacements);

        sw.Stop();
        return new CleanupResult(
            CleanedText: withCorrections,
            Path: chosenPath,
            RawModelOutput: raw,
            AssembledPrompt: assembled,
            Elapsed: sw.Elapsed);
    }

    private static CleanupResult Finalize(
        string rawTranscript,
        string rawModelOutput,
        CorrectionsData corrections,
        string assembledPrompt,
        CleanupPath path,
        Stopwatch sw)
    {
        var cleaned = CaseAwareReplacer.Apply(rawTranscript, corrections.Replacements);
        sw.Stop();
        return new CleanupResult(cleaned, path, rawModelOutput, assembledPrompt, sw.Elapsed);
    }
}
```

- [ ] **Step 6: Verify all CleanupRunner tests pass**

```bash
dotnet test --filter "FullyQualifiedName~CleanupRunnerTests"
```

Expected: 10 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Cleanup/ILlamaCleanupBackend.cs \
    src/Winpepper.Cleanup/CleanupRunner.cs \
    tests/Winpepper.Cleanup.Tests/Fakes \
    tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "feat(cleanup): CleanupRunner orchestration with timeout/empty/ellipsis fallbacks"
```

---

## Task 9: LlamaCleanupBackend — real LLamaSharp implementation

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Cleanup/LlamaCleanupBackend.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs`
- Create: `$REPO_ROOT/scripts/download-cleanup-model.ps1`

Windows-only. The backend wraps `LLamaSharp` 0.27.0 + the Vulkan native backend. We expose `WarmAsync` so callers can pre-warm the KV cache at app start (spec §5.5).

- [ ] **Step 1: Write `scripts/download-cleanup-model.ps1`**

```powershell
# Downloads the Qwen 2.5 0.5B Q4_K_M cleanup model to the standard winpepper location.
# Run via: ./scripts/winssh < scripts/download-cleanup-model.ps1

$dest = "$env:LOCALAPPDATA\winpepper\models\cleanup\qwen2.5-0.5b-instruct"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$url  = "https://huggingface.co/bartowski/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"
$file = Join-Path $dest "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"

if (Test-Path $file) {
    Write-Host "Cleanup model already present: $file"
} else {
    Write-Host "Downloading cleanup model (~400 MB)..."
    Invoke-WebRequest -Uri $url -OutFile $file
}

Get-ChildItem $dest | Format-Table Name, Length
```

- [ ] **Step 2: Implement `src/Winpepper.Cleanup/LlamaCleanupBackend.cs`**

```csharp
#if WINDOWS
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace Winpepper.Cleanup;

/// <summary>
/// Real <see cref="ILlamaCleanupBackend"/> built on LLamaSharp 0.27 with the
/// Vulkan backend NuGet. The <see cref="LLamaContext"/> is constructed once
/// (per process); <see cref="WarmAsync"/> primes the KV cache so the first
/// user dictation doesn't pay the cold-start cost.
/// </summary>
public sealed class LlamaCleanupBackend : ILlamaCleanupBackend, IDisposable
{
    private readonly ILogger<LlamaCleanupBackend> _log;
    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly ModelParams _params;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    public LlamaCleanupBackend(string modelPath, ILogger<LlamaCleanupBackend> log,
                                int contextSize = 4096, int gpuLayerCount = 999)
    {
        _log = log;
        _params = new ModelParams(modelPath)
        {
            ContextSize = (uint)contextSize,
            GpuLayerCount = gpuLayerCount, // Vulkan backend picks the first device.
        };
        _log.LogInformation("Loading cleanup model: {Path}", modelPath);
        _weights = LLamaWeights.LoadFromFile(_params);
        _context = _weights.CreateContext(_params);
        _log.LogInformation("Cleanup model loaded.");
    }

    /// <summary>Pre-warm the KV cache. Spec §5.5.</summary>
    public async Task WarmAsync(CancellationToken ct)
    {
        const string warmupPrompt = "Hello.";
        try
        {
            _log.LogDebug("Pre-warming cleanup LLM context...");
            await GenerateAsync(warmupPrompt, maxNewTokens: 4, temperature: 0.1f, ct).ConfigureAwait(false);
            _log.LogDebug("Cleanup LLM pre-warm complete.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleanup LLM pre-warm failed (non-fatal).");
        }
    }

    public async Task<string> GenerateAsync(string prompt, int maxNewTokens, float temperature, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var executor = new InstructExecutor(_context);
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxNewTokens,
                AntiPrompts = new List<string> { "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    TopP = 0.95f,
                    TopK = 40,
                },
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (sb.Length > maxNewTokens * 8) break; // hard char cap as belt-and-braces
            }
            return sb.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
        _gate.Dispose();
    }
}
#endif
```

- [ ] **Step 3: Write Windows-only integration test `tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

[Trait("Platform", "Windows")]
public class LlamaCleanupBackendIntegrationTests
{
    private static string ModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "winpepper", "models", "cleanup", "qwen2.5-0.5b-instruct",
        "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf");

    [Fact]
    public async Task Load_Generate_ReturnsSomething()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(File.Exists(ModelPath),
            $"Cleanup model not present at {ModelPath}; run scripts/download-cleanup-model.ps1");

        using var backend = new LlamaCleanupBackend(ModelPath, new NullLogger<LlamaCleanupBackend>());
        var result = await backend.GenerateAsync(
            prompt: "Repeat the following sentence: Hello, world.",
            maxNewTokens: 32,
            temperature: 0.1f,
            ct: CancellationToken.None);
        result.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Warm_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.SkipUnless(File.Exists(ModelPath),
            $"Cleanup model not present at {ModelPath}; run scripts/download-cleanup-model.ps1");

        using var backend = new LlamaCleanupBackend(ModelPath, new NullLogger<LlamaCleanupBackend>());
        await backend.WarmAsync(CancellationToken.None);
    }
}
#endif
```

- [ ] **Step 4: Cross-compile on Linux to verify the Windows TFM builds clean**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build src/Winpepper.Cleanup/Winpepper.Cleanup.csproj -f net9.0-windows10.0.19041.0
```

Expected: build succeeds (no native execution; just compile + restore checks). If LlamaSharp's API changed between 0.27.0 and whatever version you actually find on nuget.org, fix `InstructExecutor` / `InferenceParams` / `DefaultSamplingPipeline` to match the installed version — the rest of the runner doesn't depend on internal LlamaSharp details.

- [ ] **Step 5: Sync + download the model + run the integration tests on the VM**

```bash
cd $REPO_ROOT
./scripts/sync-to-vm.sh
./scripts/winssh < scripts/download-cleanup-model.ps1
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test --filter \"FullyQualifiedName~LlamaCleanupBackendIntegrationTests\""
```

Expected: both tests pass on the VM. The Vulkan backend may emit "device 0 picked" log lines.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/LlamaCleanupBackend.cs \
    tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs \
    scripts/download-cleanup-model.ps1
git commit -m "feat(cleanup): LLamaSharp Vulkan backend with pre-warm hook"
```

---

## Task 10: WindowContext result + source types + pure-logic test scaffold

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/WindowContextSource.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/WindowContextResult.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/WindowContextResultTests.cs`

These records are used by every other window-context type, so they land first. Pure C#, cross-platform.

- [ ] **Step 1: Write failing test `tests/Winpepper.Platform.Tests/WindowContext/WindowContextResultTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextResultTests
{
    [Fact]
    public void Empty_Has_EmptyTextAndZeroChars()
    {
        var r = WindowContextResult.Empty;
        r.Source.ShouldBe(WindowContextSource.Empty);
        r.Text.ShouldBeEmpty();
        r.CharCount.ShouldBe(0);
        r.AverageOcrConfidence.ShouldBeNull();
    }

    [Fact]
    public void FromUia_CountsChars_AndSetsSource()
    {
        var r = WindowContextResult.FromUia("hello");
        r.Source.ShouldBe(WindowContextSource.Uia);
        r.Text.ShouldBe("hello");
        r.CharCount.ShouldBe(5);
        r.AverageOcrConfidence.ShouldBeNull();
    }

    [Fact]
    public void FromOcr_TracksConfidence()
    {
        var r = WindowContextResult.FromOcr("hi there", averageConfidence: 0.84);
        r.Source.ShouldBe(WindowContextSource.Ocr);
        r.Text.ShouldBe("hi there");
        r.CharCount.ShouldBe(8);
        r.AverageOcrConfidence.ShouldBe(0.84);
    }
}
```

- [ ] **Step 2: Implement `src/Winpepper.Platform/WindowContext/WindowContextSource.cs`**

```csharp
namespace Winpepper.Platform.WindowContext;

public enum WindowContextSource
{
    Empty,
    Uia,
    Ocr,
}
```

- [ ] **Step 3: Implement `src/Winpepper.Platform/WindowContext/WindowContextResult.cs`**

```csharp
namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Output of a window-context prefetch. Always non-null; <see cref="Empty"/>
/// is used when nothing usable was recovered.
/// </summary>
public sealed record WindowContextResult(
    WindowContextSource Source,
    string Text,
    int CharCount,
    double? AverageOcrConfidence)
{
    public static WindowContextResult Empty { get; } =
        new(WindowContextSource.Empty, "", 0, null);

    public static WindowContextResult FromUia(string text) =>
        new(WindowContextSource.Uia, text, text.Length, null);

    public static WindowContextResult FromOcr(string text, double averageConfidence) =>
        new(WindowContextSource.Ocr, text, text.Length, averageConfidence);
}
```

- [ ] **Step 4: Run the tests**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test --filter "FullyQualifiedName~WindowContextResultTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Platform/WindowContext/WindowContextSource.cs \
    src/Winpepper.Platform/WindowContext/WindowContextResult.cs \
    tests/Winpepper.Platform.Tests/WindowContext
git commit -m "feat(platform): WindowContextResult / Source records"
```

---

## Task 11: UIA tree-walking — pure-logic ordering & dedup

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/UiaExtractedElement.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/UiaTreeOrdering.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/UiaTreeOrderingTests.cs`

Split the pure logic (reading-order sort, dedup, truncation) out from the COM-bound walk so it's testable on Linux. The COM walk lands in Task 12.

- [ ] **Step 1: Write failing test `tests/Winpepper.Platform.Tests/WindowContext/UiaTreeOrderingTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class UiaTreeOrderingTests
{
    private static UiaExtractedElement E(string text, int x, int y) =>
        new(text, BoundingLeft: x, BoundingTop: y);

    [Fact]
    public void Sort_ProducesTopToBottom_LeftToRight()
    {
        var items = new List<UiaExtractedElement>
        {
            E("c-right", 200, 100),
            E("a-top",   50,  10),
            E("c-left",  50,  100),
            E("b-mid",   50,  50),
        };
        var ordered = UiaTreeOrdering.Sort(items).Select(e => e.Text).ToList();
        ordered.ShouldBe(new[] { "a-top", "b-mid", "c-left", "c-right" });
    }

    [Fact]
    public void Dedup_RemovesExactDuplicateText_KeepsFirstOccurrence()
    {
        var items = new List<UiaExtractedElement>
        {
            E("hello", 0, 0),
            E("world", 10, 0),
            E("hello", 100, 0), // duplicate text — drop
        };
        var deduped = UiaTreeOrdering.Dedup(items).Select(e => e.Text).ToList();
        deduped.ShouldBe(new[] { "hello", "world" });
    }

    [Fact]
    public void Dedup_TreatsWhitespaceOnlyAsDroppable()
    {
        var items = new List<UiaExtractedElement>
        {
            E("",      0, 0),
            E("  \t",  10, 0),
            E("hello", 20, 0),
        };
        var deduped = UiaTreeOrdering.Dedup(items).Select(e => e.Text).ToList();
        deduped.ShouldBe(new[] { "hello" });
    }

    [Fact]
    public void Join_ConcatenatesWithNewlines_AndTruncatesTo4000()
    {
        var items = new List<UiaExtractedElement>
        {
            E(new string('a', 2000), 0, 0),
            E(new string('b', 2500), 0, 10),
        };
        var text = UiaTreeOrdering.Join(items, maxChars: 4000);
        text.Length.ShouldBe(4000);
        text[..2000].ShouldBe(new string('a', 2000));
    }

    [Fact]
    public void Compose_ShortText_ReturnsEmpty_Per80CharThreshold()
    {
        var items = new List<UiaExtractedElement> { E("hi there", 0, 0) };
        UiaTreeOrdering.Compose(items, maxChars: 4000, minViableChars: 80).ShouldBeNull();
    }

    [Fact]
    public void Compose_LongEnoughText_ReturnsIt()
    {
        var items = new List<UiaExtractedElement>
        {
            E(new string('x', 200), 0, 0),
        };
        var result = UiaTreeOrdering.Compose(items, maxChars: 4000, minViableChars: 80);
        result.ShouldNotBeNull();
        result!.Length.ShouldBe(200);
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~UiaTreeOrderingTests"
```

Expected: types not defined.

- [ ] **Step 3: Implement `src/Winpepper.Platform/WindowContext/UiaExtractedElement.cs`**

```csharp
namespace Winpepper.Platform.WindowContext;

/// <summary>
/// One piece of text recovered from a UIA tree element, with the element's
/// top-left position in screen coordinates for reading-order sorting.
/// </summary>
public sealed record UiaExtractedElement(
    string Text,
    int BoundingLeft,
    int BoundingTop);
```

- [ ] **Step 4: Implement `src/Winpepper.Platform/WindowContext/UiaTreeOrdering.cs`**

```csharp
using System.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Pure-logic helpers for the UIA window-context path. The COM-bound tree walk
/// lives in <c>UiaTreeReader</c>; everything testable lives here.
/// Spec §6.1: top-to-bottom, left-to-right reading order; dedup; 4000-char cap;
/// fall through to OCR when recovered text &lt; 80 chars.
/// </summary>
public static class UiaTreeOrdering
{
    public const int DefaultMaxChars = 4000;
    public const int DefaultMinViableChars = 80;

    public static IEnumerable<UiaExtractedElement> Sort(IEnumerable<UiaExtractedElement> items) =>
        items
            .OrderBy(e => e.BoundingTop)
            .ThenBy(e => e.BoundingLeft);

    public static IEnumerable<UiaExtractedElement> Dedup(IEnumerable<UiaExtractedElement> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in items)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            if (!seen.Add(e.Text)) continue;
            yield return e;
        }
    }

    public static string Join(IEnumerable<UiaExtractedElement> items, int maxChars = DefaultMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var e in items)
        {
            if (sb.Length > 0) sb.Append('\n');
            var remaining = maxChars - sb.Length;
            if (remaining <= 0) break;
            if (e.Text.Length <= remaining) sb.Append(e.Text);
            else { sb.Append(e.Text, 0, remaining); break; }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full pipeline: sort → dedup → join (truncated) → enforce min viable length.
    /// Returns null when the recovered text is shorter than <paramref name="minViableChars"/>
    /// — signalling the caller to fall through to OCR.
    /// </summary>
    public static string? Compose(
        IEnumerable<UiaExtractedElement> items,
        int maxChars = DefaultMaxChars,
        int minViableChars = DefaultMinViableChars)
    {
        var text = Join(Dedup(Sort(items)), maxChars);
        return text.Length < minViableChars ? null : text;
    }
}
```

- [ ] **Step 5: Verify the tests pass**

```bash
dotnet test --filter "FullyQualifiedName~UiaTreeOrderingTests"
```

Expected: 6 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Platform/WindowContext/UiaExtractedElement.cs \
    src/Winpepper.Platform/WindowContext/UiaTreeOrdering.cs \
    tests/Winpepper.Platform.Tests/WindowContext/UiaTreeOrderingTests.cs
git commit -m "feat(platform): UIA reading-order sort + dedup + truncation logic"
```

---

## Task 12: UIA native walk — `Winpepper.Platform.WindowContext.UiaTreeReader`

**Files:**
- Modify: `$REPO_ROOT/src/Winpepper.Platform/Winpepper.Platform.csproj`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/UiaNative.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/UiaTextExtraction.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/UiaTreeReader.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/ForegroundWindow.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/UiaIntegrationTests.cs`

Windows-only. UIA is reached through the in-box `UIAutomationClient` + `UIAutomationTypes` assemblies via `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />`.

- [ ] **Step 1: Modify `src/Winpepper.Platform/Winpepper.Platform.csproj` to multi-target and add the UIA framework reference**

Replace the existing csproj with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Platform</RootNamespace>
    <AssemblyName>Winpepper.Platform</AssemblyName>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="System.Threading.Channels" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <FrameworkReference Include="Microsoft.WindowsDesktop.App" />
    <PackageReference Include="System.Drawing.Common" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </ItemGroup>
</Project>
```

`Microsoft.WindowsDesktop.App` brings in `UIAutomationClient.dll`, `UIAutomationTypes.dll`, and `WindowsBase.dll` — exactly what UIA needs.

- [ ] **Step 2: Implement `src/Winpepper.Platform/WindowContext/UiaNative.cs`** (P/Invoke shims for foreground window only — UIA itself is reached through managed types)

```csharp
#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.WindowContext;

internal static partial class UiaNative
{
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int GetWindowTextW(IntPtr hWnd, [Out] char[] lpString, int nMaxCount);
}
#endif
```

- [ ] **Step 3: Implement `src/Winpepper.Platform/WindowContext/ForegroundWindow.cs`** (used by the error bus logging requirement in spec §5.6)

```csharp
#if WINDOWS
namespace Winpepper.Platform.WindowContext;

public static class ForegroundWindow
{
    public static IntPtr Handle() => UiaNative.GetForegroundWindow();

    public static string Title(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var buf = new char[512];
        var len = UiaNative.GetWindowTextW(hwnd, buf, buf.Length);
        return len > 0 ? new string(buf, 0, len) : "";
    }
}
#endif
```

- [ ] **Step 4: Implement `src/Winpepper.Platform/WindowContext/UiaTextExtraction.cs`** (pattern-preference reader for one `AutomationElement`)

```csharp
#if WINDOWS
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Extracts text from a single UIA element using the pattern-preference order
/// from spec §6.1:
///   1. TextPattern.DocumentRange.GetText(8000)
///   2. ValuePattern.Value
///   3. LegacyIAccessiblePattern.Value
///   4. Name
/// Returns null when nothing was extractable.
/// </summary>
internal static class UiaTextExtraction
{
    private const int TextPatternCap = 8000;

    public static string? Extract(AutomationElement element)
    {
        // 1) TextPattern
        try
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObj)
                && textObj is TextPattern tp)
            {
                var range = tp.DocumentRange;
                var text = range.GetText(TextPatternCap);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch { /* fall through */ }

        // 2) ValuePattern
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObj)
                && valueObj is ValuePattern vp)
            {
                var v = vp.Current.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }

        // 3) LegacyIAccessiblePattern
        try
        {
            if (element.TryGetCurrentPattern(LegacyIAccessiblePattern.Pattern, out var legacyObj)
                && legacyObj is LegacyIAccessiblePattern lp)
            {
                var v = lp.Current.Value;
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }

        // 4) Name
        try
        {
            var name = element.Current.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }

        return null;
    }
}
#endif
```

- [ ] **Step 5: Implement `src/Winpepper.Platform/WindowContext/UiaTreeReader.cs`** (walks the ContentView tree, fills a list of `UiaExtractedElement`)

```csharp
#if WINDOWS
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Walks the UIA ContentView subtree of the supplied window and returns
/// extracted text elements. Spec §6.1.
/// </summary>
public sealed class UiaTreeReader
{
    private readonly ILogger<UiaTreeReader> _log;
    private const int MaxElementsVisited = 2000; // hard guard against pathological trees

    public UiaTreeReader(ILogger<UiaTreeReader> log) { _log = log; }

    public List<UiaExtractedElement> ReadForeground(IntPtr foregroundHwnd, CancellationToken ct)
    {
        var results = new List<UiaExtractedElement>();
        if (foregroundHwnd == IntPtr.Zero) return results;

        AutomationElement root;
        try
        {
            root = AutomationElement.FromHandle(foregroundHwnd);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "AutomationElement.FromHandle failed; UIA path unavailable");
            return results;
        }

        var walker = TreeWalker.ContentViewWalker;
        var visited = 0;
        var stack = new Stack<AutomationElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (visited++ > MaxElementsVisited) break;

            var current = stack.Pop();

            try
            {
                var text = UiaTextExtraction.Extract(current);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var rect = current.Current.BoundingRectangle;
                    results.Add(new UiaExtractedElement(text!, (int)rect.Left, (int)rect.Top));
                }
            }
            catch (Exception ex) { _log.LogTrace(ex, "Element extract failed; skipping"); }

            // Push children in reverse so siblings come out in document order.
            try
            {
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(current);
                while (child != null)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            catch (Exception ex) { _log.LogTrace(ex, "Sibling walk failed"); }
        }

        return results;
    }
}
#endif
```

- [ ] **Step 6: Write Windows-only integration test `tests/Winpepper.Platform.Tests/WindowContext/UiaIntegrationTests.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

[Trait("Platform", "Windows")]
public class UiaIntegrationTests
{
    [Fact]
    public void ReadForeground_OnAnyForegroundWindow_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = ForegroundWindow.Handle();
        var reader = new UiaTreeReader(new NullLogger<UiaTreeReader>());
        var result = reader.ReadForeground(hwnd, CancellationToken.None);
        result.ShouldNotBeNull();
        // Result count is environment-dependent on a headless VM; we only verify no throw.
    }

    [Fact]
    public void ReadForeground_ZeroHandle_ReturnsEmptyList()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new UiaTreeReader(new NullLogger<UiaTreeReader>());
        var result = reader.ReadForeground(IntPtr.Zero, CancellationToken.None);
        result.ShouldBeEmpty();
    }
}
#endif
```

- [ ] **Step 7: Cross-compile on Linux and verify**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build src/Winpepper.Platform/Winpepper.Platform.csproj -f net9.0-windows10.0.19041.0
```

Expected: build succeeds. The Linux TFM build of the same project compiles (it just excludes the UIA-bound files via `#if WINDOWS`).

- [ ] **Step 8: Run all tests on the VM**

```bash
./scripts/winrun "dotnet test --filter \"FullyQualifiedName~UiaIntegrationTests\""
```

Expected: both UIA integration tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Winpepper.Platform/Winpepper.Platform.csproj \
    src/Winpepper.Platform/WindowContext/UiaNative.cs \
    src/Winpepper.Platform/WindowContext/ForegroundWindow.cs \
    src/Winpepper.Platform/WindowContext/UiaTextExtraction.cs \
    src/Winpepper.Platform/WindowContext/UiaTreeReader.cs \
    tests/Winpepper.Platform.Tests/WindowContext/UiaIntegrationTests.cs
git commit -m "feat(platform): UIA ContentView tree walk + pattern-preference text extraction"
```

---

## Task 13: OCR fallback — `PrintWindow` + `Windows.Media.Ocr`

**Files:**
- Modify: `$REPO_ROOT/tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/PrintWindowNative.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/OcrFallback.cs`
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/OcrLineSort.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/OcrLineSortTests.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/OcrIntegrationTests.cs`

Windows-only path. OCR is reached through the `Windows.Media.Ocr` projection that ships with the `net9.0-windows10.0.19041.0` TFM (no additional NuGet needed).

- [ ] **Step 1: Modify the platform test csproj to also multi-target so it can host the Windows OCR test**

Replace `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj` with:

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
    <ProjectReference Include="..\..\src\Winpepper.Platform\Winpepper.Platform.csproj" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing pure-logic test for OCR line ordering `tests/Winpepper.Platform.Tests/WindowContext/OcrLineSortTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class OcrLineSortTests
{
    [Fact]
    public void SortAndJoin_OrdersLinesTopToBottom_WordsLeftToRight()
    {
        var input = new List<OcrLineSort.Line>
        {
            new(Top: 50, Words: new()
            {
                new(Left: 100, Text: "right"),
                new(Left: 10,  Text: "left"),
            }),
            new(Top: 10, Words: new()
            {
                new(Left: 20, Text: "early"),
            }),
        };
        OcrLineSort.SortAndJoin(input).ShouldBe("early\nleft right");
    }

    [Fact]
    public void AverageConfidence_AveragesAcrossAllWords()
    {
        var input = new List<OcrLineSort.Line>
        {
            new(Top: 0, Words: new()
            {
                new(Left: 0, Text: "a", Confidence: 0.9),
                new(Left: 1, Text: "b", Confidence: 0.5),
            }),
            new(Top: 1, Words: new()
            {
                new(Left: 0, Text: "c", Confidence: 0.7),
            }),
        };
        OcrLineSort.AverageConfidence(input).ShouldBe((0.9 + 0.5 + 0.7) / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void AverageConfidence_NoWords_ReturnsZero()
    {
        OcrLineSort.AverageConfidence(new List<OcrLineSort.Line>()).ShouldBe(0.0);
    }

    [Fact]
    public void SortAndJoin_Truncates_At4000Chars()
    {
        var line = new OcrLineSort.Line(Top: 0, Words: new()
        {
            new(Left: 0, Text: new string('x', 5000), Confidence: 1.0),
        });
        OcrLineSort.SortAndJoin(new[] { line }, maxChars: 4000).Length.ShouldBe(4000);
    }
}
```

- [ ] **Step 3: Run to confirm failure**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test --filter "FullyQualifiedName~OcrLineSortTests"
```

Expected: build error — `OcrLineSort` not defined.

- [ ] **Step 4: Implement `src/Winpepper.Platform/WindowContext/OcrLineSort.cs`** (pure logic, cross-TFM)

```csharp
using System.Text;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Pure-logic helpers for the OCR window-context path. Spec §6.1.
/// </summary>
public static class OcrLineSort
{
    public const int DefaultMaxChars = 4000;

    public readonly record struct Word(int Left, string Text, double Confidence = 1.0);

    public sealed record Line(int Top, List<Word> Words);

    public static string SortAndJoin(IEnumerable<Line> lines, int maxChars = DefaultMaxChars)
    {
        var sb = new StringBuilder();
        foreach (var line in lines.OrderBy(l => l.Top))
        {
            if (sb.Length > 0)
            {
                if (sb.Length + 1 > maxChars) break;
                sb.Append('\n');
            }
            var sortedWords = line.Words.OrderBy(w => w.Left);
            var first = true;
            foreach (var w in sortedWords)
            {
                var prefix = first ? "" : " ";
                var addition = prefix + w.Text;
                if (sb.Length + addition.Length > maxChars)
                {
                    sb.Append(addition, 0, maxChars - sb.Length);
                    return sb.ToString();
                }
                sb.Append(addition);
                first = false;
            }
        }
        return sb.ToString();
    }

    public static double AverageConfidence(IEnumerable<Line> lines)
    {
        double sum = 0; int count = 0;
        foreach (var line in lines)
            foreach (var w in line.Words) { sum += w.Confidence; count++; }
        return count == 0 ? 0.0 : sum / count;
    }
}
```

- [ ] **Step 5: Verify line-sort tests pass**

```bash
dotnet test --filter "FullyQualifiedName~OcrLineSortTests"
```

Expected: 4 tests pass.

- [ ] **Step 6: Implement `src/Winpepper.Platform/WindowContext/PrintWindowNative.cs`**

```csharp
#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.WindowContext;

internal static partial class PrintWindowNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    public const uint PW_RENDERFULLCONTENT = 0x00000002;
}
#endif
```

- [ ] **Step 7: Implement `src/Winpepper.Platform/WindowContext/OcrFallback.cs`** (real OCR pipeline using `PrintWindow` + `Windows.Media.Ocr` + `SoftwareBitmap`)

```csharp
#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// OCR fallback for window-context prefetch. Spec §6.1. Captures the foreground
/// window's client area via <c>PrintWindow</c>, hands the bitmap to
/// <c>Windows.Media.Ocr.OcrEngine</c>, sorts the results in reading order,
/// truncates to 4000 chars.
/// </summary>
public sealed class OcrFallback
{
    private readonly ILogger<OcrFallback> _log;

    public OcrFallback(ILogger<OcrFallback> log) { _log = log; }

    public async Task<WindowContextResult> CaptureAsync(IntPtr foregroundHwnd, CancellationToken ct)
    {
        if (foregroundHwnd == IntPtr.Zero) return WindowContextResult.Empty;

        if (!PrintWindowNative.GetClientRect(foregroundHwnd, out var rect)) return WindowContextResult.Empty;
        var w = rect.Width;
        var h = rect.Height;
        if (w <= 0 || h <= 0) return WindowContextResult.Empty;

        SoftwareBitmap? swBitmap;
        try
        {
            swBitmap = CaptureWindowToSoftwareBitmap(foregroundHwnd, w, h);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PrintWindow capture failed");
            return WindowContextResult.Empty;
        }
        if (swBitmap is null) return WindowContextResult.Empty;

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            _log.LogDebug("OcrEngine.TryCreateFromUserProfileLanguages returned null; no OCR languages installed");
            return WindowContextResult.Empty;
        }

        OcrResult ocr;
        try
        {
            ocr = await engine.RecognizeAsync(swBitmap).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OcrEngine.RecognizeAsync threw");
            return WindowContextResult.Empty;
        }

        var lines = ocr.Lines.Select(l => new OcrLineSort.Line(
            Top: (int)(l.Words.Count > 0 ? l.Words[0].BoundingRect.Top : 0),
            Words: l.Words.Select(w => new OcrLineSort.Word(
                Left: (int)w.BoundingRect.Left,
                Text: w.Text,
                Confidence: 1.0)).ToList())).ToList();

        var text = OcrLineSort.SortAndJoin(lines);
        var confidence = OcrLineSort.AverageConfidence(lines);
        _log.LogDebug("OCR recovered {Chars} chars, avg confidence {Conf:F2}", text.Length, confidence);

        return text.Length == 0
            ? WindowContextResult.Empty
            : WindowContextResult.FromOcr(text, confidence);
    }

    private static SoftwareBitmap? CaptureWindowToSoftwareBitmap(IntPtr hwnd, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!PrintWindowNative.PrintWindow(hwnd, hdc, PrintWindowNative.PW_RENDERFULLCONTENT))
                    return null;
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var sw = SoftwareBitmap.CreateCopyFromBuffer(
                buffer.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                bmp.Width,
                bmp.Height,
                BitmapAlphaMode.Premultiplied);
            return sw;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
#endif
```

- [ ] **Step 8: Write Windows-only integration test `tests/Winpepper.Platform.Tests/WindowContext/OcrIntegrationTests.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

[Trait("Platform", "Windows")]
public class OcrIntegrationTests
{
    [Fact]
    public async Task Capture_ZeroHandle_ReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ocr = new OcrFallback(new NullLogger<OcrFallback>());
        var result = await ocr.CaptureAsync(IntPtr.Zero, CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Capture_AnyForeground_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ocr = new OcrFallback(new NullLogger<OcrFallback>());
        var hwnd = ForegroundWindow.Handle();
        var result = await ocr.CaptureAsync(hwnd, CancellationToken.None);
        result.ShouldNotBeNull();
        // Result text can be empty on a blank VM screen; we just verify no throw.
    }
}
#endif
```

- [ ] **Step 9: Cross-compile on Linux**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build src/Winpepper.Platform/Winpepper.Platform.csproj -f net9.0-windows10.0.19041.0
```

Expected: build succeeds.

- [ ] **Step 10: Run on VM**

```bash
./scripts/winrun "dotnet test --filter \"FullyQualifiedName~OcrIntegrationTests\""
```

Expected: both tests pass on the VM.

- [ ] **Step 11: Commit**

```bash
git add src/Winpepper.Platform/WindowContext/PrintWindowNative.cs \
    src/Winpepper.Platform/WindowContext/OcrLineSort.cs \
    src/Winpepper.Platform/WindowContext/OcrFallback.cs \
    tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj \
    tests/Winpepper.Platform.Tests/WindowContext/OcrLineSortTests.cs \
    tests/Winpepper.Platform.Tests/WindowContext/OcrIntegrationTests.cs
git commit -m "feat(platform): OCR fallback via PrintWindow + Windows.Media.Ocr"
```

---

## Task 14: WindowContextPrefetch — UIA-first, OCR-fallback, cancellable, error-bus silent

**Files:**
- Create: `$REPO_ROOT/src/Winpepper.Platform/WindowContext/WindowContextPrefetch.cs`
- Create: `$REPO_ROOT/tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchTests.cs`

This is the public entry point used by `Winpepper.Cli` (and later the WinUI shell). It's a thin coordinator: try UIA → if `<80` chars or failure → OCR → if both empty → `WindowContextResult.Empty`. Silent on errors per spec §9.1 row "OCR / UIA → Any failure → Silent skip".

The class accepts seams so logic is unit-testable on Linux without UIA/OCR.

- [ ] **Step 1: Write failing test `tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchTests.cs`**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextPrefetchTests
{
    private static WindowContextPrefetch NewPrefetch(
        Func<IntPtr, CancellationToken, Task<string?>>? uia = null,
        Func<IntPtr, CancellationToken, Task<WindowContextResult>>? ocr = null) =>
        new(
            readUia: uia ?? ((_, _) => Task.FromResult<string?>(null)),
            captureOcr: ocr ?? ((_, _) => Task.FromResult(WindowContextResult.Empty)),
            log: new NullLogger<WindowContextPrefetch>());

    [Fact]
    public async Task Start_UiaReturnsLongEnoughText_UsesUiaPath()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => Task.FromResult<string?>(new string('x', 200)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Uia);
        result.Text.Length.ShouldBe(200);
    }

    [Fact]
    public async Task Start_UiaReturnsShortText_FallsThroughToOcr()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => Task.FromResult<string?>("hi"),
            ocr: (_, _) => Task.FromResult(WindowContextResult.FromOcr("plenty of OCR text here", 0.9)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Ocr);
    }

    [Fact]
    public async Task Start_UiaThrows_FallsThroughToOcr_Silently()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => throw new InvalidOperationException("boom"),
            ocr: (_, _) => Task.FromResult(WindowContextResult.FromOcr("recovered", 0.7)));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.Source.ShouldBe(WindowContextSource.Ocr);
        result.Text.ShouldBe("recovered");
    }

    [Fact]
    public async Task Start_BothFail_ReturnsEmpty()
    {
        var prefetch = NewPrefetch();
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Start_BothThrow_ReturnsEmpty()
    {
        var prefetch = NewPrefetch(
            uia: (_, _) => throw new InvalidOperationException("u"),
            ocr: (_, _) => throw new InvalidOperationException("o"));
        var result = await prefetch.StartAsync(new IntPtr(0x1234), CancellationToken.None);
        result.ShouldBe(WindowContextResult.Empty);
    }

    [Fact]
    public async Task Start_Cancelled_BeforeUia_ReturnsEmpty()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var prefetch = NewPrefetch();
        var result = await prefetch.StartAsync(new IntPtr(0x1234), cts.Token);
        result.ShouldBe(WindowContextResult.Empty);
    }
}
```

- [ ] **Step 2: Run to confirm failure**

```bash
dotnet test --filter "FullyQualifiedName~WindowContextPrefetchTests"
```

Expected: build error — `WindowContextPrefetch` not defined.

- [ ] **Step 3: Implement `src/Winpepper.Platform/WindowContext/WindowContextPrefetch.cs`** (cross-TFM, seams for tests)

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Platform.WindowContext;

/// <summary>
/// Public window-context prefetch. UIA-first, OCR fallback. Failures are silent
/// (spec §9.1 — cleanup runs without window context rather than surfacing an
/// error to the user). The orchestrator (CleanupRunner) imposes its own
/// 500 ms wait budget; this class returns whenever the chosen path completes.
/// </summary>
public sealed class WindowContextPrefetch
{
    private readonly Func<IntPtr, CancellationToken, Task<string?>> _readUia;
    private readonly Func<IntPtr, CancellationToken, Task<WindowContextResult>> _captureOcr;
    private readonly ILogger<WindowContextPrefetch> _log;

    public WindowContextPrefetch(
        Func<IntPtr, CancellationToken, Task<string?>> readUia,
        Func<IntPtr, CancellationToken, Task<WindowContextResult>> captureOcr,
        ILogger<WindowContextPrefetch> log)
    {
        _readUia = readUia;
        _captureOcr = captureOcr;
        _log = log;
    }

    public async Task<WindowContextResult> StartAsync(IntPtr foregroundHwnd, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return WindowContextResult.Empty;
        if (foregroundHwnd == IntPtr.Zero) return WindowContextResult.Empty;

        // UIA path.
        string? uia;
        try
        {
            uia = await _readUia(foregroundHwnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "UIA prefetch threw; falling through to OCR");
            uia = null;
        }

        if (!string.IsNullOrEmpty(uia) && uia.Length >= UiaTreeOrdering.DefaultMinViableChars)
            return WindowContextResult.FromUia(uia);

        // OCR fallback.
        WindowContextResult ocr;
        try
        {
            ocr = await _captureOcr(foregroundHwnd, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "OCR prefetch threw; window context unavailable");
            ocr = WindowContextResult.Empty;
        }

        return ocr;
    }

    /// <summary>
    /// Convenience factory for the production Windows build. The Linux build
    /// callers (Cli on Linux is a no-op build target anyway) can construct
    /// directly with no-op seams.
    /// </summary>
#if WINDOWS
    public static WindowContextPrefetch CreateWindows(
        UiaTreeReader uiaReader,
        OcrFallback ocrFallback,
        ILogger<WindowContextPrefetch> log)
    {
        return new WindowContextPrefetch(
            readUia: (hwnd, ct) => Task.Run(() =>
            {
                var elements = uiaReader.ReadForeground(hwnd, ct);
                return UiaTreeOrdering.Compose(elements);
            }, ct),
            captureOcr: (hwnd, ct) => ocrFallback.CaptureAsync(hwnd, ct),
            log: log);
    }
#endif
}
```

- [ ] **Step 4: Verify the tests pass on Linux**

```bash
dotnet test --filter "FullyQualifiedName~WindowContextPrefetchTests"
```

Expected: 6 tests pass.

- [ ] **Step 5: Build + test on the VM**

```bash
./scripts/winrun "dotnet build"
./scripts/winrun "dotnet test"
```

Expected: no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Platform/WindowContext/WindowContextPrefetch.cs \
    tests/Winpepper.Platform.Tests/WindowContext/WindowContextPrefetchTests.cs
git commit -m "feat(platform): WindowContextPrefetch UIA-first OCR-fallback coordinator"
```

---

## Task 15: Wire CleanupRunner + WindowContextPrefetch into `Winpepper.Cli`

**Files:**
- Modify: `$REPO_ROOT/src/Winpepper.Cli/Winpepper.Cli.csproj`
- Modify: `$REPO_ROOT/src/Winpepper.Cli/Program.cs`
- Modify: `$REPO_ROOT/src/Winpepper.Cli/Pipeline.cs`

The CLI is the temporary entry point. After this task, the manual smoke procedure from Plan 1 Task 16 produces *cleaned* text in the focused window. The CLI is retired in Plan 3 when the WinUI shell lands.

- [ ] **Step 1: Modify `src/Winpepper.Cli/Winpepper.Cli.csproj`** to reference the new projects

Replace the existing csproj contents with:

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
    <ProjectReference Include="..\Winpepper.Cleanup\Winpepper.Cleanup.csproj" />
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net9.0-windows10.0.19041.0'">
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Replace `src/Winpepper.Cli/Program.cs`** with the version that constructs cleanup + corrections + window context

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
#if WINDOWS
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Core.Settings;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.WindowContext;
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

        var cleanupModelPath = Path.Combine(localAppData, "winpepper", "models", "cleanup",
            "qwen2.5-0.5b-instruct", "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf");
        if (!File.Exists(cleanupModelPath))
        {
            log.LogError("Cleanup model not found at {Path}. Run scripts/download-cleanup-model.ps1 first.", cleanupModelPath);
            return 3;
        }

        var correctionsPath = Path.Combine(localAppData, "winpepper", "corrections.json");
        var corrections = new CorrectionStore(correctionsPath);

        using var cleanupBackend = new LlamaCleanupBackend(cleanupModelPath, logFactory.CreateLogger<LlamaCleanupBackend>());
        await cleanupBackend.WarmAsync(CancellationToken.None);
        var cleanupRunner = new CleanupRunner(cleanupBackend, logFactory.CreateLogger<CleanupRunner>());

        var uiaReader = new UiaTreeReader(logFactory.CreateLogger<UiaTreeReader>());
        var ocrFallback = new OcrFallback(logFactory.CreateLogger<OcrFallback>());
        var windowContextPrefetch = WindowContextPrefetch.CreateWindows(
            uiaReader, ocrFallback, logFactory.CreateLogger<WindowContextPrefetch>());

        using var pipeline = new Pipeline(
            logFactory.CreateLogger<Pipeline>(), logFactory, modelDir,
            HotkeyChord.Parse(settings.HoldHotkey),
            HotkeyChord.Parse(settings.ToggleHotkey),
            HotkeyChord.Parse("Esc"),
            cleanupRunner,
            corrections,
            windowContextPrefetch);

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

- [ ] **Step 3: Replace `src/Winpepper.Cli/Pipeline.cs`** with the version that runs cleanup between transcription and injection, with window-context prefetch in parallel

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Winpepper.Core.Sessions;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;
using Winpepper.Platform.WindowContext;

namespace Winpepper.Cli;

public sealed class Pipeline : IDisposable
{
    private readonly ILogger<Pipeline> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly CleanupRunner _cleanup;
    private readonly CorrectionStore _corrections;
    private readonly WindowContextPrefetch _windowContext;
    private readonly SessionEngine _engine = new();

    private IAudioRecorder? _recorder;
    private CancellationTokenSource? _sessionCts;
    private Task<WindowContextResult>? _windowContextTask;

    public Pipeline(
        ILogger<Pipeline> log,
        ILoggerFactory factory,
        string modelDir,
        HotkeyChord hold,
        HotkeyChord toggle,
        HotkeyChord cancel,
        CleanupRunner cleanup,
        CorrectionStore corrections,
        WindowContextPrefetch windowContext)
    {
        _log = log;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
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

                _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                // Start audio capture.
                _recorder = new WasapiRecorder();
                _recorder.Start();

                // Start window-context prefetch in parallel (spec §6.1).
                var hwnd = ForegroundWindow.Handle();
                _log.LogDebug("Hotkey down. Foreground window: '{Title}' ({Hwnd:X})",
                    ForegroundWindow.Title(hwnd), hwnd.ToInt64());
                _windowContextTask = _windowContext.StartAsync(hwnd, _sessionCts.Token);
                break;

            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);

                var samples = _recorder!.Stop();
                _recorder.Dispose();
                _recorder = null;
                _log.LogInformation("Captured {Count} samples ({Sec:F2}s)", samples.Length, samples.Length / 16000.0);

                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _log.LogInformation("Raw transcript: '{Text}'", transcript.Text);

                // Run cleanup (with window context Task piped in).
                var contextTextTask = _windowContextTask is null
                    ? null
                    : _windowContextTask.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result.Text : null, ct);

                var cleanupOpts = new CleanupOptions
                {
                    Profile = CleanupProfile.Ordinary,
                    Timeout = TimeSpan.FromSeconds(15),
                    Temperature = 0.1f,
                    WindowContextEnabled = false, // toggle in Plan 3 settings UI
                    WindowContextWait = TimeSpan.FromMilliseconds(500),
                };

                var cleanupResult = await _cleanup.RunAsync(
                    rawTranscript: transcript.Text,
                    corrections: _corrections.Load(),
                    windowContextTask: contextTextTask,
                    options: cleanupOpts,
                    ct: ct);

                _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms, text='{Text}'",
                    cleanupResult.Path, (int)cleanupResult.Elapsed.TotalMilliseconds, cleanupResult.CleanedText);

                _engine.Apply(SessionEvent.TranscriptReady);

                if (!string.IsNullOrWhiteSpace(cleanupResult.CleanedText))
                {
                    var preInjectHwnd = ForegroundWindow.Handle();
                    var preInjectTitle = ForegroundWindow.Title(preInjectHwnd);
                    _log.LogDebug("Injecting into foreground window: '{Title}' ({Hwnd:X})",
                        preInjectTitle, preInjectHwnd.ToInt64());
                    _injector.TryInject(cleanupResult.CleanedText);
                }

                _engine.Apply(SessionEvent.InjectionCompleted);

                _sessionCts?.Dispose();
                _sessionCts = null;
                _windowContextTask = null;
                break;

            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _sessionCts?.Cancel();
                _recorder?.Dispose();
                _recorder = null;
                _windowContextTask = null;
                _sessionCts?.Dispose();
                _sessionCts = null;
                break;

            case HotkeyEventKind.Toggle:
                _log.LogInformation("Toggle hotkey is not implemented in Plan 2 (use hold).");
                break;
        }
    }

    public void Dispose()
    {
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
        _sessionCts?.Dispose();
    }
}
#endif
```

- [ ] **Step 4: Build + test the CLI on Linux (cross-compile both TFMs)**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj
```

Expected: build succeeds for both TFMs.

- [ ] **Step 5: Build on the VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj"
```

Expected: build succeeds. The CLI binary is at `C:\winpepper\src\Winpepper.Cli\bin\Debug\net9.0-windows10.0.19041.0\winpepper.exe`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cli
git commit -m "feat(cli): wire CleanupRunner + CorrectionStore + WindowContextPrefetch into pipeline"
```

---

## Task 16: End-to-end manual smoke test on the VM

**Files:**
- Modify: `$REPO_ROOT/docs/manual-test.md`

The headless VM mic returns silence, so transcription output will be empty. The cleanup path takes the empty-input branch (everything is empty); we mainly verify *no crash* and the cleanup runner is invoked. For a real demo, the operator runs on a physical Windows 11 machine.

- [ ] **Step 1: Append the Plan 2 smoke procedure to `docs/manual-test.md`**

Append this block after the Plan 1 smoke procedure:

```markdown
## Plan 2 cleanup-pipeline smoke (Windows VM)

1. Sync: `./scripts/sync-to-vm.sh`
2. Make sure both models exist:
   ```bash
   ./scripts/winssh < scripts/download-parakeet.ps1
   ./scripts/winssh < scripts/download-cleanup-model.ps1
   ```
3. Build the CLI: `./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj -c Release"`
4. Pre-create a correction file so we can confirm the deterministic post-pass runs:
   ```bash
   ./scripts/winssh 'powershell -Command "$dst = \"$env:LOCALAPPDATA\\winpepper\\corrections.json\"; New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null; Set-Content -Path $dst -Value (@{schema=1; preferred=@(\"ChatGPT\"); replacements=@{\"chat gbt\"=\"ChatGPT\"}} | ConvertTo-Json -Depth 5)"'
   ```
5. Run the CLI in a foreground PowerShell session on the VM:
   ```powershell
   cd C:\winpepper
   dotnet run --project src/Winpepper.Cli -c Release
   ```
6. The console log should show:
   - "Loading cleanup model: ...Qwen2.5-0.5B-Instruct-Q4_K_M.gguf"
   - "Cleanup model loaded."
   - "Cleanup LLM pre-warm complete."
   - "Winpepper ready. Hold the trigger to dictate."
7. From the host, hold `RightCtrl+RightShift` for ~2 seconds, then release.
8. The log should show:
   - "State Idle -> Recording"
   - "Captured NNNNN samples (X.XXs)"
   - "Raw transcript: '...'" (likely empty on a silent VM)
   - "Cleanup path=FallbackEmpty, NNms, text='...'"
   - "State Transcribing -> Injecting" then "-> Idle"
9. Acceptance bar for Plan 2 on the VM: no crash, model loaded, pre-warm completed, cleanup runner invoked, correction post-pass available. Real cleaned-text output requires real audio.

For a real demo, run on a physical Windows 11 host with a mic. Say
"so um like we tested chat gbt today" → release. Expected injected text:
`We tested ChatGPT today.` (filler removed by the LLM, then case-aware
correction post-pass maps any surviving "chat gbt" to "ChatGPT").
```

- [ ] **Step 2: Run the smoke procedure on the VM and paste the log into the task tracker**

```bash
cd $REPO_ROOT
./scripts/sync-to-vm.sh
./scripts/winssh < scripts/download-cleanup-model.ps1
./scripts/winrun "dotnet build src/Winpepper.Cli/Winpepper.Cli.csproj -c Release"
```

Then start the CLI in an interactive SSH session and hold the hotkey from the host. Capture at least 30 seconds of log output.

- [ ] **Step 3: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs: Plan 2 cleanup-pipeline smoke procedure"
```

---

## Task 17: Plan-2 self-review and cleanup task wrap-up

**Files:**
- None (verification only).

- [ ] **Step 1: Run the full test suite on Linux**

```bash
cd $REPO_ROOT
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test --filter "Platform!=Windows"
```

Expected: all of the following tests are green —
- `BasePromptsTests` (8)
- `CaseAwareReplacerTests` (8)
- `CleanupRunnerTests` (10)
- `CorrectionStoreTests` (8)
- `CorrectionValidationTests` (13)
- `OcrLineSortTests` (4)
- `PromptBuilderTests` (8)
- `SanityTests` (cleanup + corrections)
- `ThinkSanitizerTests` (8)
- `UiaTreeOrderingTests` (6)
- `WindowContextPrefetchTests` (6)
- `WindowContextResultTests` (3)
- All Plan 1 tests still green.

- [ ] **Step 2: Run the full test suite on the VM**

```bash
./scripts/winrun "dotnet test"
```

Expected: everything above plus the `[Trait("Platform","Windows")]` tests:
- `UiaIntegrationTests` (2)
- `OcrIntegrationTests` (2)
- `LlamaCleanupBackendIntegrationTests` (2, conditional on model presence)

- [ ] **Step 3: Self-review checklist**

Tick each off in this task's commit message:

- [ ] §5.5 covered by Task 9 (`LlamaCleanupBackend.WarmAsync`, timeout, max-tokens formula) + Task 8 (`<think>` sanitize, empty/`"..."` fallback).
- [ ] §6.1 covered by Tasks 10–14 (UIA tree walk, pattern preference, dedup + ordering, 4000-char truncation, <80-char fall-through, OCR fallback via `PrintWindow` + `Windows.Media.Ocr`, silent failure).
- [ ] §6.2 covered by Task 5 (`PromptBuilder` four-block assembly + omission rules).
- [ ] §6.3 covered by Task 4 (`BasePrompts.Default` with filler list, self-correction commands, recognition fixes, sentence punctuation, three examples).
- [ ] §6.4 covered by Task 4 (`CleanupProfile.Ordinary` / `.Literal` / `.Custom`, with `BasePrompts.ForProfile` selector).
- [ ] §6.5 covered by Task 7 (`CaseAwareReplacer`) + invoked in both the LLM-success and fallback branches of `CleanupRunner` (Task 8).
- [ ] §8.1 covered by Tasks 2–3 (`CorrectionsData` schema, atomic JSON, `CorrectionStore` Add/Remove APIs).
- [ ] §9.1 cleanup rows covered: timeout → `FallbackTimeout`; empty / "..." → `FallbackEmpty` / `FallbackEllipsis`; backend exception → `FallbackBackendError`; UIA / OCR exception → silent skip via `WindowContextPrefetch`.
- [ ] Wiring requirement satisfied: `Winpepper.Cli` invokes `CleanupRunner` between transcription and injection (Task 15).
- [ ] No "TBD" / "similar to Task N" / "implement edge cases" placeholders in this plan.
- [ ] Method signatures referenced across tasks match: `ILlamaCleanupBackend.GenerateAsync(string, int, float, CancellationToken)`, `CleanupRunner.RunAsync(string, CorrectionsData, Task<string?>?, CleanupOptions, CancellationToken)`, `WindowContextPrefetch.StartAsync(IntPtr, CancellationToken)`, `CorrectionStore.Load() / Save() / AddPreferred() / AddReplacement()` — all defined and consistent.

- [ ] **Step 4: Commit a marker that Plan 2 is done**

```bash
git commit --allow-empty -m "milestone: Plan 2 (cleanup, window context, corrections) complete

Self-review:
- §5.5: CleanupRunner timeout + max-tokens + <think> sanitize + ellipsis fallback (Tasks 8, 9)
- §6.1: UIA tree walk + OCR fallback + 500ms wait budget (Tasks 10–14)
- §6.2: PromptBuilder four-block assembly (Task 5)
- §6.3: Default base prompt with filler list + three examples (Task 4)
- §6.4: Ordinary/Literal/Custom profiles (Task 4)
- §6.5: Deterministic case-aware post-pass (Task 7, invoked in Task 8)
- §8.1: CorrectionStore with atomic JSON (Tasks 2, 3)
- §9.1 cleanup rows: timeout/empty/ellipsis/backend-error/UIA-or-OCR-silent
- CLI wired (Task 15)"
```

---

## What Plan 2 does NOT cover (intentionally — see follow-on plans)

- WinUI 3 main window, tray, status pill, settings UI — Plan 3.
- Post-paste learning (`PostPasteWatcher`, UIA `TextEdit_TextChangedEvent` listening, learn-correction toast) — Plan 5.
- History store, lab views, model downloader — Plan 4.
- WiX MSI, autostart, code signing — Plan 6.
- Onboarding flow (mic picker, hotkey recorder, model download progress, "try it" panel) — Plan 3.
- Settings UI binding for the cleanup profile + window-context toggle — Plan 3.

## Handoff

When all tasks are committed and the VM smoke procedure runs without crashing: tell the user the cleanup pipeline is alive on the VM, then start Plan 3 (WinUI 3 shell + tray + status pill + settings).
