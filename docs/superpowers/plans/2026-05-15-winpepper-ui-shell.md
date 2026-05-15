# Winpepper Plan 3 — WinUI 3 Shell, Tray, Status Pill, Settings, Onboarding

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `Winpepper.Cli` with `Winpepper.App` — a packaged WinUI 3 single-instance app that owns the pipeline, surfaces a tray icon, a click-through status pill, and a `NavigationView` main window with Recording / Cleanup / Corrections tabs and a first-run onboarding flow. Settings persistence becomes reactive (`INotifyPropertyChanged`, debounced writes). Autostart toggle and bundled start/stop sounds land here too.

**Architecture:** Single user-session WinUI 3 process. `AppShell` (a non-XAML host class) owns the long-lived pipeline that `Winpepper.Cli` previously ran, gated by `Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().IsCurrent` so a second launch just signals the running one. Tray (`H.NotifyIcon`), status pill (`AppWindow` with `WS_EX_TRANSPARENT` click-through), and main window (`NavigationView` with three pages) all bind to a small family of observable view models (`SessionViewModel`, `RecordingSettingsViewModel`, `CleanupSettingsViewModel`, `CorrectionsViewModel`, `OnboardingViewModel`). View models live in `Winpepper.App` but are pure C# (no XAML), and are unit-tested headlessly. The hook thread from Plan 1 remains the only thread that calls `SendInput`. WinUI 3 packaged build runs on the Windows VM (not from Linux); non-WinUI projects keep building on Linux.

**Tech Stack:** WinUI 3 (Microsoft.WindowsAppSDK 1.6.x), `H.NotifyIcon.WinUI`, `CommunityToolkit.Mvvm`, `System.Media.SoundPlayer`, `Microsoft.Win32.Registry`, `Microsoft.Windows.AppLifecycle.AppInstance`, plus the existing C#/.NET 9 / xUnit / Shouldly stack.

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md) — primarily §7.1–§7.7 and the open implementation question §13.3 (status pill click-through configuration). Out of scope (deferred to Plan 4/5/6): History, Lab, Models, Diagnostics tabs; post-paste learning; the actual model downloader; the WiX MSI.

**Prerequisites:**
- Plan 1 (`docs/superpowers/plans/2026-05-15-winpepper-foundation.md`) — committed on `plan-1/foundation`. Provides `Winpepper.Core`, `Winpepper.Audio`, `Winpepper.Asr`, `Winpepper.Platform` (hotkeys + injection), `Winpepper.Cli`, scripts/VM tooling.
- Plan 2 (cleanup, corrections, window context) — its outputs `Winpepper.Cleanup`, `Winpepper.Corrections`, and the UIA/OCR `WindowContextPrefetch` in `Winpepper.Platform` are referenced in this plan. Confirm `src/Winpepper.Cleanup/Winpepper.Cleanup.csproj` and `src/Winpepper.Corrections/Winpepper.Corrections.csproj` exist on disk before starting Task 11 (cleanup tab) and Task 12 (corrections tab). If Plan 2 has stubbed them, the bindings still compile against the public surface specified below.

**Type contract from Plan 2 that this plan binds to:**

The contract below mirrors what Plan 2 actually publishes (see `docs/superpowers/plans/2026-05-15-winpepper-cleanup.md` lines 1958 (`CleanupRunner.RunAsync`), 924 (`CleanupProfile`), 945 (`CleanupOptions`), 975 (`CleanupResult`), 679 (`CorrectionStore`), 2370 (`WindowContextResult`), 3439 (`WindowContextPrefetch.StartAsync`)). Plan 3 binds to these exact names and signatures:

- `Winpepper.Cleanup.CleanupRunner` —
  ```csharp
  Task<CleanupResult> RunAsync(
      string rawTranscript,
      CorrectionsData corrections,
      Task<string?>? windowContextTask,
      CleanupOptions options,
      CancellationToken ct);
  ```
  `windowContextTask` is a `Task<string?>?` — a task that resolves to the extracted window-context text string (or null). The runner internally bounds its own wait on it. The result is a `CleanupResult` record (`CleanedText`, `Path`, `RawModelOutput`, `AssembledPrompt`, `Elapsed`); callers consume `.CleanedText`.
- `Winpepper.Cleanup.CleanupProfile` — enum `Ordinary`, `Literal`, `Custom`. The default-prompt selector is `Winpepper.Cleanup.BasePrompts.ForProfile(CleanupProfile, string? custom)`.
- `Winpepper.Cleanup.CleanupOptions` — record with `Profile`, `CustomBasePrompt`, `Timeout` (TimeSpan, default 15s), `Temperature` (float, default 0.1), `WindowContextWait` (TimeSpan, default 500 ms), `WindowContextEnabled` (bool, default false), `MaxNewTokensCap` (int, default 2048).
- `Winpepper.Corrections.CorrectionsData` — record with `IReadOnlyList<string> Preferred` and `IReadOnlyDictionary<string, string> Replacements`. `CorrectionsData.Empty` is the default.
- `Winpepper.Corrections.CorrectionStore` — exposes `CorrectionsData Load()`, `void Save(CorrectionsData)`, `bool AddPreferred(string)`, `bool AddReplacement(string wrong, string right)`, `bool RemovePreferred(string)`, `bool RemoveReplacement(string wrong)`. Plan 3 reads via `Load()` and persists via `Save(...)`; the view models in Tasks 12 and 13 mirror that shape with a thin adapter.
- `Winpepper.Platform.WindowContext.WindowContextResult` — record (`Source`, `Text`, `CharCount`, `AverageConfidence`) with `Empty`, `FromUia(string)`, `FromOcr(string, double)` factories.
- `Winpepper.Platform.WindowContext.WindowContextPrefetch` — `Task<WindowContextResult> StartAsync(IntPtr foregroundHwnd, CancellationToken ct)`. The hwnd-aware factory `WindowContextPrefetch.CreateWindows(...)` is used in production; Plan 3 wires it up in Task 24.

Note that `CleanupRunner.RunAsync` takes a `Task<string?>?` (the text), not a `Task<WindowContextResult>` — Plan 2's CLI wiring at line 3749–3751 of `2026-05-15-winpepper-cleanup.md` extracts `.Text` from the `WindowContextResult` task with a `ContinueWith` before handing the resulting `Task<string?>` to the runner. Plan 3 replicates that adapter pattern in Task 24.

If a Plan 2 task is still in flight, leave the corresponding binding behind an `#if true` guard so Plan 3 work can land. Mark the guard with `// PLAN2-TYPE` so the search is easy.

**Repo root throughout:** `/home/jesse/git/winpepper/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced via `scripts/sync-to-vm.sh`).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Confirm failure. Implement minimal code. Confirm pass. Commit.

**Linux vs Windows build split.** Most code in this plan lives in cross-platform view models, so it builds on Linux. The WinUI 3 packaged app (`Winpepper.App`) and its XAML pages only build on Windows. Linux CI runs `dotnet build` against everything except `Winpepper.App` (we add a `Directory.Build.props` filter for this). The VM runs the full build, including the packaged app, via `./scripts/winrun "dotnet build winpepper.sln"`.

**WinUI 3 cross-compile note.** Even with `EnableWindowsTargeting=true`, the XAML compiler does not fully cross-compile from Linux. The plan never asks you to `dotnet build src/Winpepper.App` from Linux — every WinUI 3 build is `./scripts/winrun ...` against the VM.

**Test traits.**
- View-model tests are pure C# and tagged `[Trait("Layer","ViewModel")]`. They run on both Linux and Windows.
- WinUI 3 packaged tests (none in this plan; the shell is exercised manually) are tagged `[Trait("Platform","WindowsPackaged")]`.

**DispatcherQueue discipline.** View models never touch `DispatcherQueue` directly — they raise `PropertyChanged` and pages dispatch on receipt. The only exception is the tray menu, which has to update from background threads; it goes through the `IUiThread` abstraction introduced in Task 4.

**Single-instance.** `AppShell.Bootstrap` calls `AppInstance.FindOrRegisterForKey("Winpepper")`. A second launch signals the first via `RedirectActivationToAsync` and exits.

---

## Task 1: Provision WinAppSDK runtime on the VM

**Files:**
- Modify: `/home/jesse/git/winpepper/scripts/provision-vm.ps1` — append a WinAppSDK runtime install block.

The packaged `Winpepper.App` needs the WinApp Runtime installed on every machine that runs it. The MSI in Plan 6 will bootstrap it for end users; for development on the VM we install it once here.

- [ ] **Step 1: Append to `scripts/provision-vm.ps1`**

After the existing Git block and before the final summary lines, add:

```powershell
# Windows App SDK runtime (WinAppSDK 1.6) ---------------------------------
# Required by Winpepper.App (WinUI 3 packaged). The bootstrapper installs
# both the framework MSIX and the singleton service.
$winAppSdkInstalled = Get-AppxPackage -AllUsers -Name "Microsoft.WindowsAppRuntime.1.6" -ErrorAction SilentlyContinue
if (-not $winAppSdkInstalled) {
    Write-Host "Installing Windows App SDK 1.6 runtime..."
    $installerUrl = "https://aka.ms/windowsappsdk/1.6/latest/windowsappruntimeinstall-x64.exe"
    $installer = "$env:TEMP\windowsappruntimeinstall-x64.exe"
    Invoke-WebRequest -UseBasicParsing -Uri $installerUrl -OutFile $installer
    Start-Process -Wait -FilePath $installer -ArgumentList "--quiet"
    Remove-Item $installer -Force
} else {
    Write-Host "Windows App SDK runtime already installed: $($winAppSdkInstalled.Version)"
}
```

- [ ] **Step 2: Run on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winssh < scripts/provision-vm.ps1
```

Expected output ends with `Windows App SDK runtime already installed: 6000.xxxx.xxxx.x` (or the install + success line on first run).

- [ ] **Step 3: Verify**

```bash
./scripts/winssh "Get-AppxPackage -AllUsers Microsoft.WindowsAppRuntime.1.6 | Select-Object Name,Version"
```

Expected: a non-empty row.

- [ ] **Step 4: Commit**

```bash
git add scripts/provision-vm.ps1
git commit -m "scripts(vm): install Windows App SDK 1.6 runtime"
```

---

## Task 2: Package versions for WinUI 3 and MVVM

**Files:**
- Modify: `/home/jesse/git/winpepper/Directory.Packages.props` — add WinAppSDK, H.NotifyIcon, CommunityToolkit.Mvvm.

- [ ] **Step 1: Append package versions to `Directory.Packages.props`**

Open `/home/jesse/git/winpepper/Directory.Packages.props` and insert these `<PackageVersion>` entries inside the existing `<ItemGroup>` (keep the existing entries — do not remove anything):

```xml
    <PackageVersion Include="Microsoft.WindowsAppSDK" Version="1.6.241114003" />
    <PackageVersion Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.22621.3233" />
    <PackageVersion Include="H.NotifyIcon.WinUI" Version="2.1.4" />
    <PackageVersion Include="CommunityToolkit.Mvvm" Version="8.4.0" />
    <PackageVersion Include="Microsoft.Win32.Registry" Version="5.0.0" />
```

- [ ] **Step 2: Restore on Linux to confirm version graph resolves**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
```

Expected: `Restore succeeded`. No package downgrade warnings.

- [ ] **Step 3: Commit**

```bash
git add Directory.Packages.props
git commit -m "deps: add WinAppSDK, H.NotifyIcon, MVVM toolkit, registry"
```

---

## Task 3: Settings schema additions for Plan 3

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/AppSettings.cs`
- Modify: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`

We're adding the settings keys the new UI binds to: autostart, completion of onboarding, the speaker-filter toggle (placeholder — actual filter is later), and a `LastVersionSeen` for future migrations. Cleanup/correction settings live in Plan 2's own files.

- [ ] **Step 1: Write failing tests — append to `tests/Winpepper.Core.Tests/Settings/SettingsStoreTests.cs`**

Add inside the `SettingsStoreTests` class, after the existing tests:

```csharp
    [Fact]
    public void Defaults_Include_NewPlan3Fields()
    {
        var s = new SettingsStore(_path).Load();
        s.AutostartEnabled.ShouldBeFalse();
        s.OnboardingCompleted.ShouldBeFalse();
        s.SpeakerFilterEnabled.ShouldBeFalse();
        s.LastVersionSeen.ShouldBe("");
    }

    [Fact]
    public void Save_RoundTrips_NewFields()
    {
        var store = new SettingsStore(_path);
        var s = store.Load() with
        {
            AutostartEnabled = true,
            OnboardingCompleted = true,
            SpeakerFilterEnabled = true,
            LastVersionSeen = "0.3.0",
        };
        store.Save(s);
        var loaded = new SettingsStore(_path).Load();
        loaded.AutostartEnabled.ShouldBeTrue();
        loaded.OnboardingCompleted.ShouldBeTrue();
        loaded.SpeakerFilterEnabled.ShouldBeTrue();
        loaded.LastVersionSeen.ShouldBe("0.3.0");
    }
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: build fails — `AppSettings` has no `AutostartEnabled` etc.

- [ ] **Step 3: Extend `src/Winpepper.Core/Settings/AppSettings.cs`**

Append inside the `AppSettings` record, after `PlaySounds`:

```csharp
    // Plan 3 additions
    public bool AutostartEnabled { get; init; } = false;
    public bool OnboardingCompleted { get; init; } = false;
    public bool SpeakerFilterEnabled { get; init; } = false;
    public string LastVersionSeen { get; init; } = "";
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests"
```

Expected: all `SettingsStoreTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Settings tests/Winpepper.Core.Tests/Settings
git commit -m "feat(settings): add autostart, onboarding, speaker-filter, version fields"
```

---

## Task 4: IUiThread abstraction

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Threading/IUiThread.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Threading/SynchronousUiThread.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Threading/SynchronousUiThreadTests.cs`

`IUiThread` lets view models and the tray hand work to the UI without referencing `Microsoft.UI.Dispatching.DispatcherQueue`. WinUI provides a concrete `DispatcherQueueUiThread` in `Winpepper.App` (Task 9); unit tests use `SynchronousUiThread` which runs callbacks inline.

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Threading/SynchronousUiThreadTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Threading;
using Xunit;

namespace Winpepper.Core.Tests.Threading;

public class SynchronousUiThreadTests
{
    [Fact]
    public void Post_Runs_Callback_Inline()
    {
        var ui = new SynchronousUiThread();
        var ran = 0;
        ui.Post(() => ran++);
        ran.ShouldBe(1);
    }

    [Fact]
    public void HasThreadAccess_Is_True_For_Synchronous()
    {
        var ui = new SynchronousUiThread();
        ui.HasThreadAccess.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SynchronousUiThreadTests"
```

Expected: type `IUiThread` and `SynchronousUiThread` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Threading/IUiThread.cs`**

```csharp
namespace Winpepper.Core.Threading;

/// <summary>
/// Abstraction over the WinUI 3 DispatcherQueue so view models can post work
/// to the UI thread without referencing WinUI. The concrete implementation
/// lives in Winpepper.App; unit tests use SynchronousUiThread.
/// </summary>
public interface IUiThread
{
    bool HasThreadAccess { get; }
    void Post(Action action);
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Threading/SynchronousUiThread.cs`**

```csharp
namespace Winpepper.Core.Threading;

public sealed class SynchronousUiThread : IUiThread
{
    public bool HasThreadAccess => true;
    public void Post(Action action) => action();
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SynchronousUiThreadTests"
```

Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Threading tests/Winpepper.Core.Tests/Threading
git commit -m "feat(core): IUiThread abstraction with sync test impl"
```

---

## Task 5: Reactive SettingsViewModel with debounced writes

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/ISettingsWriter.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`

The view models in `Winpepper.App` fire `PropertyChanged` on every keystroke; we don't want to hit `settings.json` that often. `DebouncedSettingsWriter` coalesces writes over a 400 ms window.

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class DebouncedSettingsWriterTests : IDisposable
{
    private readonly string _path;
    public DebouncedSettingsWriterTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public async Task Queue_Coalesces_Bursts_Into_One_Write()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromMilliseconds(50));
        for (var i = 0; i < 20; i++)
            writer.Queue(s => s with { MicDeviceId = $"dev{i}" });
        await Task.Delay(200);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("dev19");
    }

    [Fact]
    public async Task FlushAsync_Forces_Immediate_Write()
    {
        var store = new SettingsStore(_path);
        using var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "forced" });
        await writer.FlushAsync();
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("forced");
    }

    [Fact]
    public async Task Dispose_Flushes_Pending_Writes()
    {
        var store = new SettingsStore(_path);
        var writer = new DebouncedSettingsWriter(store, TimeSpan.FromSeconds(30));
        writer.Queue(s => s with { MicDeviceId = "disposed" });
        writer.Dispose();
        await Task.Delay(50);
        var loaded = new SettingsStore(_path).Load();
        loaded.MicDeviceId.ShouldBe("disposed");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DebouncedSettingsWriter"
```

Expected: build fails — types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/Settings/ISettingsWriter.cs`**

```csharp
namespace Winpepper.Core.Settings;

public interface ISettingsWriter
{
    void Queue(Func<AppSettings, AppSettings> mutator);
    Task FlushAsync();
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs`**

```csharp
namespace Winpepper.Core.Settings;

public sealed class DebouncedSettingsWriter : ISettingsWriter, IDisposable
{
    private readonly SettingsStore _store;
    private readonly TimeSpan _delay;
    private readonly object _lock = new();
    private AppSettings _pending;
    private bool _dirty;
    private CancellationTokenSource? _cts;
    private Task? _scheduled;

    public DebouncedSettingsWriter(SettingsStore store, TimeSpan? delay = null)
    {
        _store = store;
        _delay = delay ?? TimeSpan.FromMilliseconds(400);
        _pending = store.Load();
    }

    public void Queue(Func<AppSettings, AppSettings> mutator)
    {
        lock (_lock)
        {
            _pending = mutator(_pending);
            _dirty = true;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _scheduled = Task.Run(async () =>
            {
                try { await Task.Delay(_delay, token); }
                catch (OperationCanceledException) { return; }
                Flush();
            });
        }
    }

    public async Task FlushAsync()
    {
        Task? t;
        lock (_lock) { t = _scheduled; _cts?.Cancel(); }
        if (t is not null) { try { await t; } catch { } }
        Flush();
    }

    private void Flush()
    {
        AppSettings? toWrite = null;
        lock (_lock)
        {
            if (_dirty) { toWrite = _pending; _dirty = false; }
        }
        if (toWrite is not null) _store.Save(toWrite);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        Flush();
    }
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DebouncedSettingsWriter"
```

Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings tests/Winpepper.Core.Tests/Settings
git commit -m "feat(settings): debounced writer coalesces UI bursts"
```

---

## Task 6: SessionViewModel — observable session state

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/SessionStage.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/SessionViewModelTests.cs`

`SessionViewModel` is the single source of truth that the status pill, the tray menu, and the main window all bind to. It does NOT contain the state machine — `SessionEngine` (Plan 1) still owns transitions. The view model subscribes to `SessionEngine.StateChanged` and projects a UI-friendly `Stage` enum, `ElapsedMs`, and `StatusText`.

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class SessionViewModelTests
{
    [Fact]
    public void Initial_Stage_Is_Idle()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.Stage.ShouldBe(SessionStage.Idle);
        vm.StatusText.ShouldBe("Ready");
    }

    [Fact]
    public void Engine_StartRequested_Updates_Stage_To_Recording()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        engine.Apply(SessionEvent.StartRequested);
        vm.Stage.ShouldBe(SessionStage.Recording);
        vm.StatusText.ShouldBe("Recording...");
    }

    [Fact]
    public void Stage_Change_Raises_PropertyChanged()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        engine.Apply(SessionEvent.StartRequested);
        changed.ShouldContain(nameof(SessionViewModel.Stage));
        changed.ShouldContain(nameof(SessionViewModel.StatusText));
    }

    [Fact]
    public void Stages_Cycle_Through_Pipeline()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        engine.Apply(SessionEvent.StartRequested);
        vm.Stage.ShouldBe(SessionStage.Recording);
        engine.Apply(SessionEvent.StopRequested);
        vm.Stage.ShouldBe(SessionStage.Transcribing);
        engine.Apply(SessionEvent.TranscriptReady);
        vm.Stage.ShouldBe(SessionStage.Injecting);
        engine.Apply(SessionEvent.InjectionCompleted);
        vm.Stage.ShouldBe(SessionStage.Idle);
    }

    [Fact]
    public void NotifyError_Sets_ErrorStage_With_Message()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.NotifyError("mic missing");
        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldBe("Error: mic missing");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SessionViewModelTests"
```

Expected: types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/ViewModels/SessionStage.cs`**

```csharp
namespace Winpepper.Core.ViewModels;

public enum SessionStage
{
    Idle,
    Recording,
    Transcribing,
    CleaningUp,
    Injecting,
    Error,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/ViewModels/SessionViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Diagnostics;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;

namespace Winpepper.Core.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private readonly IUiThread _ui;
    private readonly SessionEngine _engine;
    private readonly Stopwatch _stopwatch = new();
    private SessionStage _stage = SessionStage.Idle;
    private string _statusText = "Ready";
    private long _elapsedMs;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui)
    {
        _engine = engine;
        _ui = ui;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public SessionStage Stage
    {
        get => _stage;
        private set { if (_stage == value) return; _stage = value; Raise(nameof(Stage)); Raise(nameof(StatusText)); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; Raise(nameof(StatusText)); }
    }

    public long ElapsedMs
    {
        get => _elapsedMs;
        private set { if (_elapsedMs == value) return; _elapsedMs = value; Raise(nameof(ElapsedMs)); }
    }

    /// <summary>Called by pipeline glue when the cleanup worker starts.</summary>
    public void MarkCleaningUp() => _ui.Post(() =>
    {
        Stage = SessionStage.CleaningUp;
        StatusText = "Cleaning up...";
    });

    public void NotifyError(string message) => _ui.Post(() =>
    {
        Stage = SessionStage.Error;
        StatusText = $"Error: {message}";
    });

    public void Tick() => _ui.Post(() =>
    {
        if (_stopwatch.IsRunning) ElapsedMs = _stopwatch.ElapsedMilliseconds;
    });

    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            switch (to)
            {
                case SessionState.Recording:
                    _stopwatch.Restart();
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
                case SessionState.Transcribing:
                    Stage = SessionStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case SessionState.Injecting:
                    Stage = SessionStage.Injecting;
                    StatusText = "Inserting...";
                    break;
                case SessionState.Idle:
                    _stopwatch.Stop();
                    Stage = SessionStage.Idle;
                    StatusText = "Ready";
                    break;
            }
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _engine.StateChanged -= OnEngineStateChanged;
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SessionViewModelTests"
```

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): SessionViewModel binds engine state to UI"
```

---

## Task 7: HotkeyChord conflict detection

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/HotkeyConflicts.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Hotkeys/HotkeyConflictsTests.cs`

The onboarding hotkey-recorder needs to warn if the user picks a chord that conflicts with a well-known shortcut (Win+L, Ctrl+Alt+Del, Alt+Tab, Alt+F4, Ctrl+C/V/X/Z, Win+D, Win+E). Pure-logic; runs anywhere.

- [ ] **Step 1: Write failing test `tests/Winpepper.Platform.Tests/Hotkeys/HotkeyConflictsTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Hotkeys;
using Xunit;

namespace Winpepper.Platform.Tests.Hotkeys;

public class HotkeyConflictsTests
{
    [Theory]
    [InlineData("Ctrl+C")]
    [InlineData("Ctrl+V")]
    [InlineData("Ctrl+X")]
    [InlineData("Ctrl+Z")]
    [InlineData("Alt+F4")]
    [InlineData("Win+L")]
    [InlineData("Win+D")]
    [InlineData("Win+E")]
    public void Common_Shortcuts_Are_Flagged(string chord)
    {
        var c = HotkeyChord.Parse(chord);
        HotkeyConflicts.Describe(c).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("RightCtrl+RightShift")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData("RightAlt+F12")]
    public void Dictation_Defaults_Are_Not_Flagged(string chord)
    {
        var c = HotkeyChord.Parse(chord);
        HotkeyConflicts.Describe(c).ShouldBeNull();
    }

    [Fact]
    public void Same_Chord_For_Hold_And_Toggle_Is_A_Conflict()
    {
        var hold = HotkeyChord.Parse("RightCtrl+RightShift");
        var toggle = HotkeyChord.Parse("RightCtrl+RightShift");
        HotkeyConflicts.HoldAndToggleClash(hold, toggle).ShouldBeTrue();
    }

    [Fact]
    public void Different_Chords_Do_Not_Clash()
    {
        var hold = HotkeyChord.Parse("RightCtrl+RightShift");
        var toggle = HotkeyChord.Parse("Ctrl+Shift+Space");
        HotkeyConflicts.HoldAndToggleClash(hold, toggle).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~HotkeyConflictsTests"
```

Expected: type `HotkeyConflicts` not found.

- [ ] **Step 3: Implement `src/Winpepper.Platform/Hotkeys/HotkeyConflicts.cs`**

```csharp
namespace Winpepper.Platform.Hotkeys;

/// <summary>
/// Warns when the user picks a chord that collides with well-known Windows
/// shortcuts. Returns a human-readable description of the conflict, or null
/// when the chord is safe.
/// </summary>
public static class HotkeyConflicts
{
    private static readonly Dictionary<string, string> KnownConflicts = new()
    {
        ["Ctrl+C"]   = "Copy",
        ["Ctrl+V"]   = "Paste",
        ["Ctrl+X"]   = "Cut",
        ["Ctrl+Z"]   = "Undo",
        ["Ctrl+Y"]   = "Redo",
        ["Ctrl+A"]   = "Select All",
        ["Ctrl+S"]   = "Save",
        ["Ctrl+P"]   = "Print",
        ["Ctrl+F"]   = "Find",
        ["Alt+F4"]   = "Close window",
        ["Alt+Tab"]  = "Switch window",
        ["Win+L"]    = "Lock screen",
        ["Win+D"]    = "Show desktop",
        ["Win+E"]    = "File Explorer",
        ["Win+R"]    = "Run dialog",
        ["Win+Tab"]  = "Task view",
        ["Ctrl+Esc"] = "Start menu",
    };

    public static string? Describe(HotkeyChord chord)
    {
        var key = chord.ToString();
        return KnownConflicts.TryGetValue(key, out var name) ? $"Conflicts with {name}" : null;
    }

    public static bool HoldAndToggleClash(HotkeyChord hold, HotkeyChord toggle)
        => hold.ToString() == toggle.ToString();
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~HotkeyConflictsTests"
```

Expected: all conflict tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Platform/Hotkeys tests/Winpepper.Platform.Tests/Hotkeys
git commit -m "feat(platform): hotkey-conflict detector for onboarding"
```

---

## Task 8: AutostartRegistry helper

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Autostart/IAutostartRegistry.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Autostart/AutostartRegistry.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Autostart/InMemoryAutostartRegistry.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Platform/Winpepper.Platform.csproj` — add `Microsoft.Win32.Registry` reference.

Spec §7.7: toggling autostart writes / deletes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper`. The real Win32 implementation gets a Windows-only `#if WINDOWS` guard; tests use an in-memory fake.

- [ ] **Step 1: Modify `src/Winpepper.Platform/Winpepper.Platform.csproj`** — add the registry package reference inside the existing `<ItemGroup>` that holds package references:

```xml
    <PackageReference Include="Microsoft.Win32.Registry" />
```

- [ ] **Step 2: Write failing test `tests/Winpepper.Platform.Tests/Autostart/InMemoryAutostartRegistryTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Autostart;
using Xunit;

namespace Winpepper.Platform.Tests.Autostart;

public class InMemoryAutostartRegistryTests
{
    [Fact]
    public void Initial_State_Is_Disabled()
    {
        var r = new InMemoryAutostartRegistry();
        r.IsEnabled().ShouldBeFalse();
    }

    [Fact]
    public void Enable_Then_IsEnabled_True()
    {
        var r = new InMemoryAutostartRegistry();
        r.Enable(@"C:\Program Files\Winpepper\winpepper.exe", "--tray");
        r.IsEnabled().ShouldBeTrue();
        r.CurrentCommand().ShouldBe("\"C:\\Program Files\\Winpepper\\winpepper.exe\" --tray");
    }

    [Fact]
    public void Disable_Removes_Value()
    {
        var r = new InMemoryAutostartRegistry();
        r.Enable("a.exe", "");
        r.Disable();
        r.IsEnabled().ShouldBeFalse();
        r.CurrentCommand().ShouldBeNull();
    }
}
```

- [ ] **Step 3: Verify failure**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~InMemoryAutostartRegistryTests"
```

Expected: types missing.

- [ ] **Step 4: Implement `src/Winpepper.Platform/Autostart/IAutostartRegistry.cs`**

```csharp
namespace Winpepper.Platform.Autostart;

public interface IAutostartRegistry
{
    bool IsEnabled();
    string? CurrentCommand();
    void Enable(string exePath, string arguments);
    void Disable();
}
```

- [ ] **Step 5: Implement `src/Winpepper.Platform/Autostart/InMemoryAutostartRegistry.cs`**

```csharp
namespace Winpepper.Platform.Autostart;

public sealed class InMemoryAutostartRegistry : IAutostartRegistry
{
    private string? _value;

    public bool IsEnabled() => _value is not null;
    public string? CurrentCommand() => _value;

    public void Enable(string exePath, string arguments)
    {
        var args = string.IsNullOrEmpty(arguments) ? "" : $" {arguments}";
        _value = $"\"{exePath}\"{args}";
    }

    public void Disable() => _value = null;
}
```

- [ ] **Step 6: Implement `src/Winpepper.Platform/Autostart/AutostartRegistry.cs`**

```csharp
#if WINDOWS
using Microsoft.Win32;

namespace Winpepper.Platform.Autostart;

public sealed class AutostartRegistry : IAutostartRegistry
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Winpepper";

    public bool IsEnabled() => CurrentCommand() is not null;

    public string? CurrentCommand()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return k?.GetValue(ValueName) as string;
    }

    public void Enable(string exePath, string arguments)
    {
        var args = string.IsNullOrEmpty(arguments) ? "" : $" {arguments}";
        var value = $"\"{exePath}\"{args}";
        using var k = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Cannot open HKCU Run key for write.");
        k.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
#endif
```

- [ ] **Step 7: Verify pass on Linux**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~InMemoryAutostartRegistryTests"
```

Expected: 3 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Platform/Autostart tests/Winpepper.Platform.Tests/Autostart src/Winpepper.Platform/Winpepper.Platform.csproj
git commit -m "feat(platform): autostart registry helper (HKCU Run key)"
```

---

## Task 9: Winpepper.App project scaffold (packaged WinUI 3)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Winpepper.App.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Package.appxmanifest`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/app.manifest`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Assets/.gitkeep`
- Create: `/home/jesse/git/winpepper/scripts/make-placeholder-icon.ps1` — generates a 16x16 ICO on the VM.
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Assets/AppIcon.ico` (placeholder — generated by the script above; replaced by a real icon in Plan 6).
- Modify: `/home/jesse/git/winpepper/winpepper.sln` (add `Winpepper.App`)
- Modify: `/home/jesse/git/winpepper/Directory.Build.props` — exclude `Winpepper.App` from Linux build via condition.

The packaged-app csproj uses WinAppSDK self-contained mode so the VM doesn't need `Microsoft.VCLibs.x64.14.00` separately. The XAML compiler refuses to run on Linux, so `Directory.Build.props` skips this project there.

- [ ] **Step 1: Write `src/Winpepper.App/Winpepper.App.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <RootNamespace>Winpepper.App</RootNamespace>
    <AssemblyName>Winpepper</AssemblyName>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <Platforms>x64</Platforms>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>
    <WindowsPackageType>None</WindowsPackageType>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <EnableWindowsTargeting>true</EnableWindowsTargeting>
    <Nullable>enable</Nullable>
    <DefineConstants>$(DefineConstants);WINDOWS</DefineConstants>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" />
    <PackageReference Include="H.NotifyIcon.WinUI" />
    <PackageReference Include="CommunityToolkit.Mvvm" />
    <PackageReference Include="Microsoft.Extensions.Logging" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <ProjectReference Include="..\Winpepper.Audio\Winpepper.Audio.csproj" />
    <ProjectReference Include="..\Winpepper.Asr\Winpepper.Asr.csproj" />
    <ProjectReference Include="..\Winpepper.Platform\Winpepper.Platform.csproj" />
    <ProjectReference Include="..\Winpepper.Cleanup\Winpepper.Cleanup.csproj" />
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="Assets\start.wav" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Assets\stop.wav" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="Assets\AppIcon.ico" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write `src/Winpepper.App/app.manifest`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="0.3.0.0" name="Winpepper.App" />
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="asInvoker" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

- [ ] **Step 3: Write `src/Winpepper.App/Package.appxmanifest`** (used when `WindowsPackageType` becomes `MSIX` in Plan 6; harmless when None)

```xml
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">
  <Identity Name="Winpepper" Publisher="CN=Winpepper Dev" Version="0.3.0.0" />
  <Properties>
    <DisplayName>Winpepper</DisplayName>
    <PublisherDisplayName>Winpepper</PublisherDisplayName>
    <Logo>Assets\AppIcon.ico</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.22621.0" />
  </Dependencies>
  <Applications>
    <Application Id="App" Executable="$targetnametoken$.exe" EntryPoint="$targetentrypoint$">
      <uap:VisualElements DisplayName="Winpepper" Description="Local dictation"
                           Square150x150Logo="Assets\AppIcon.ico"
                           Square44x44Logo="Assets\AppIcon.ico"
                           BackgroundColor="transparent" />
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
```

- [ ] **Step 4: Create the placeholder-icon generator script**

The reviewer's concern with hand-rolled ICO byte streams is real — a BITMAPINFOHEADER size/stride mismatch produces an ICO that opens in Explorer but rejects loads from WinUI's `BitmapImage`. Generate the ICO on the VM via `System.Drawing.Common`, which emits a known-good 16x16 BITMAPINFOHEADER + correctly-sized AND/XOR masks.

Write `/home/jesse/git/winpepper/scripts/make-placeholder-icon.ps1`:

```powershell
# Generates a 16x16 placeholder ICO at the path passed as $args[0]. The icon is
# a solid steel-blue square; Plan 6 ships the real artwork.
param([string]$Out = "src\Winpepper.App\Assets\AppIcon.ico")

Add-Type -AssemblyName System.Drawing

$bmp = New-Object System.Drawing.Bitmap 16, 16
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(255, 70, 130, 180))  # SteelBlue
$g.Dispose()

# Convert to an .ico — Icon.FromHandle on a HBITMAP is the minimal path.
$hIcon = $bmp.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($hIcon)
    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $fs = [System.IO.File]::Create($Out)
    try { $icon.Save($fs) } finally { $fs.Dispose() }
    Write-Host "Wrote placeholder icon: $Out"
} finally {
    [Winpepper.Native.NativeMethods]::DestroyIcon($hIcon) 2>$null
    $bmp.Dispose()
}
```

- [ ] **Step 5: Run the script on the VM to materialize the ICO**

```bash
mkdir -p /home/jesse/git/winpepper/src/Winpepper.App/Assets
touch /home/jesse/git/winpepper/src/Winpepper.App/Assets/.gitkeep
./scripts/sync-to-vm.sh
./scripts/winrun "powershell -ExecutionPolicy Bypass -File scripts\make-placeholder-icon.ps1 -Out src\Winpepper.App\Assets\AppIcon.ico"
./scripts/sync-from-vm.sh src/Winpepper.App/Assets/AppIcon.ico
```

(If `sync-from-vm.sh` isn't present yet — Plan 1 may have only the push direction — `scp` the generated `AppIcon.ico` back manually, or run the script once on the Windows side and copy the file into the repo. The file is small and binary; commit it once and the script is only re-run if you want to regenerate.)

Expected: `src/Winpepper.App/Assets/AppIcon.ico` exists at ~4 KB, opens in any image viewer as a 16x16 solid steel-blue square.

- [ ] **Step 6: Modify `Directory.Build.props`** — wrap `Winpepper.App` builds so Linux skips them.

Append before the closing `</Project>`:

```xml
  <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Winpepper.App' AND '$(OS)' != 'Windows_NT' AND '$(EnableWindowsTargeting)' != 'true'">
    <BuildProjectReferences>false</BuildProjectReferences>
    <DefineConstants>$(DefineConstants);SKIP_WINUI_LINUX</DefineConstants>
  </PropertyGroup>
```

(WinUI 3 still won't compile on Linux even with `EnableWindowsTargeting`; the `dotnet build` invocations below explicitly exclude `Winpepper.App`.)

- [ ] **Step 7: Add to solution**

```bash
cd /home/jesse/git/winpepper
dotnet sln add src/Winpepper.App/Winpepper.App.csproj
```

- [ ] **Step 8: Verify Linux build excludes the WinUI project but rest of solution still builds**

```bash
export DOTNET_ROOT="$HOME/.dotnet"
dotnet build winpepper.sln /p:SkipWinUI=true \
  -p:BuildProjectReferences=true \
  --no-restore \
  -t:Build \
  -- /p:_Excluded=Winpepper.App 2>&1 | tee /tmp/lin-build.log || true
# Simpler: build everything except Winpepper.App explicitly:
for proj in src/Winpepper.Core src/Winpepper.Audio src/Winpepper.Asr src/Winpepper.Platform src/Winpepper.Cli; do
  dotnet build "$proj" --no-restore
done
```

Expected: every `dotnet build` call succeeds. (`Winpepper.App` itself only builds on Windows.)

- [ ] **Step 9: Verify the WinUI 3 project builds on the VM**

```bash
./scripts/winrun "dotnet restore winpepper.sln; dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build succeeds. Look for the `Winpepper.exe` artifact at `C:\winpepper\src\Winpepper.App\bin\x64\Debug\net9.0-windows10.0.19041.0\win-x64\Winpepper.exe`.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.App scripts/make-placeholder-icon.ps1 winpepper.sln Directory.Build.props
git commit -m "scaffold(app): Winpepper.App WinUI 3 packaged project"
```

---

## Task 10: Bundled start/stop sound effects

**Files:**
- Create: `/home/jesse/git/winpepper/scripts/gen-sounds.ps1` — generates `start.wav` and `stop.wav` on the VM.
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Assets/start.wav` (generated)
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Assets/stop.wav` (generated)
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Audio/ISoundEffectPlayer.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs`

Spec §7.6: short two-tone WAV (~150 ms) played via `System.Media.SoundPlayer`, gated by the `PlaySounds` setting. We generate WAVs deterministically once on the VM and commit them under `Assets/` so the build doesn't depend on PowerShell.

- [ ] **Step 1: Write `scripts/gen-sounds.ps1`**

```powershell
# Generates start.wav (440 Hz then 660 Hz, 75 ms each) and stop.wav (660 Hz then
# 440 Hz, 75 ms each) at 22050 Hz mono 16-bit PCM. Idempotent.
param(
  [string]$OutDir = "src\Winpepper.App\Assets"
)

function Write-Wav {
    param([string]$Path, [double[]]$Freqs)
    $sampleRate = 22050
    $perTone   = [int]($sampleRate * 0.075)
    $samples = New-Object System.Collections.Generic.List[Int16]
    foreach ($f in $Freqs) {
        for ($i = 0; $i -lt $perTone; $i++) {
            $env = if ($i -lt 200) { $i / 200.0 } elseif ($i -gt $perTone - 200) { ($perTone - $i) / 200.0 } else { 1.0 }
            $v = [Math]::Sin(2 * [Math]::PI * $f * $i / $sampleRate) * 0.4 * $env
            $samples.Add([int16][Math]::Round($v * 32767))
        }
    }
    $bytes = New-Object byte[] ($samples.Count * 2)
    [System.Buffer]::BlockCopy($samples.ToArray(), 0, $bytes, 0, $bytes.Length)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([byte[]][char[]]"RIFF")
    $w.Write([int32](36 + $bytes.Length))
    $w.Write([byte[]][char[]]"WAVE")
    $w.Write([byte[]][char[]]"fmt ")
    $w.Write([int32]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int32]$sampleRate); $w.Write([int32]($sampleRate * 2))
    $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([byte[]][char[]]"data"); $w.Write([int32]$bytes.Length)
    $w.Write($bytes); $w.Flush()
    [System.IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Wav "$OutDir\start.wav" @(440, 660)
Write-Wav "$OutDir\stop.wav"  @(660, 440)
Write-Host "Wrote $OutDir\start.wav and $OutDir\stop.wav"
```

- [ ] **Step 2: Generate the WAVs on the VM and pull them back**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "powershell -ExecutionPolicy Bypass -File scripts/gen-sounds.ps1"
# Pull the generated WAVs back into the Linux working copy:
sshpass -p 'password' scp -P 2222 -o StrictHostKeyChecking=no \
  user@localhost:C:/winpepper/src/Winpepper.App/Assets/start.wav \
  src/Winpepper.App/Assets/start.wav
sshpass -p 'password' scp -P 2222 -o StrictHostKeyChecking=no \
  user@localhost:C:/winpepper/src/Winpepper.App/Assets/stop.wav \
  src/Winpepper.App/Assets/stop.wav
```

Expected: both files exist locally and are non-zero size (~6.6 KB each).

- [ ] **Step 3: Write failing test `tests/Winpepper.Core.Tests/Audio/NoopSoundEffectPlayerTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Audio;
using Xunit;

namespace Winpepper.Core.Tests.Audio;

public class NoopSoundEffectPlayerTests
{
    [Fact]
    public void Calls_Are_Counted()
    {
        var p = new NoopSoundEffectPlayer();
        p.PlayStart();
        p.PlayStop();
        p.PlayStart();
        p.StartPlays.ShouldBe(2);
        p.StopPlays.ShouldBe(1);
    }
}
```

- [ ] **Step 4: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~NoopSoundEffectPlayerTests"
```

Expected: types missing.

- [ ] **Step 5: Implement `src/Winpepper.Core/Audio/ISoundEffectPlayer.cs`**

```csharp
namespace Winpepper.Core.Audio;

public interface ISoundEffectPlayer
{
    void PlayStart();
    void PlayStop();
    bool Enabled { get; set; }
}
```

- [ ] **Step 6: Implement `src/Winpepper.Core/Audio/NoopSoundEffectPlayer.cs`**

```csharp
namespace Winpepper.Core.Audio;

public sealed class NoopSoundEffectPlayer : ISoundEffectPlayer
{
    public int StartPlays { get; private set; }
    public int StopPlays { get; private set; }
    public bool Enabled { get; set; } = true;

    public void PlayStart() { if (Enabled) StartPlays++; }
    public void PlayStop()  { if (Enabled) StopPlays++; }
}
```

- [ ] **Step 7: Implement `src/Winpepper.App/Audio/WinUiSoundEffectPlayer.cs`**

```csharp
#if WINDOWS
using System.Media;
using Winpepper.Core.Audio;

namespace Winpepper.App.Audio;

public sealed class WinUiSoundEffectPlayer : ISoundEffectPlayer, IDisposable
{
    private readonly SoundPlayer _start;
    private readonly SoundPlayer _stop;

    public bool Enabled { get; set; } = true;

    public WinUiSoundEffectPlayer(string assetsDir)
    {
        _start = new SoundPlayer(Path.Combine(assetsDir, "start.wav"));
        _stop  = new SoundPlayer(Path.Combine(assetsDir, "stop.wav"));
        _start.Load(); _stop.Load();
    }

    public void PlayStart() { if (Enabled) try { _start.Play(); } catch { } }
    public void PlayStop()  { if (Enabled) try { _stop.Play(); }  catch { } }

    public void Dispose() { _start.Dispose(); _stop.Dispose(); }
}
#endif
```

- [ ] **Step 8: Verify Noop test pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~NoopSoundEffectPlayerTests"
```

Expected: 1 test passes.

- [ ] **Step 9: Commit**

```bash
git add scripts/gen-sounds.ps1 src/Winpepper.App/Assets src/Winpepper.Core/Audio src/Winpepper.App/Audio tests/Winpepper.Core.Tests/Audio
git commit -m "feat(app): bundled start/stop WAVs and sound-effect player"
```

---

## Task 11: SettingsViewModel for the Recording tab

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`

The Recording tab binds `HoldHotkey`, `ToggleHotkey`, `MicDeviceId`, `PlaySounds`, `SpeakerFilterEnabled`. The view model raises `INotifyPropertyChanged` and queues writes via `ISettingsWriter`. It also exposes a `string? HoldHotkeyConflict` derived field (used by the UI to show a warning row).

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class RecordingSettingsViewModelTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current { get; private set; } = new();
        public int WriteCount { get; private set; }
        public void Queue(Func<AppSettings, AppSettings> m) { Current = m(Current); WriteCount++; }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [Fact]
    public void Initial_Values_Come_From_AppSettings()
    {
        var s = new AppSettings { HoldHotkey = "Ctrl+Alt+D", MicDeviceId = "abc", PlaySounds = false };
        var vm = new RecordingSettingsViewModel(s, new FakeWriter());
        vm.HoldHotkey.ShouldBe("Ctrl+Alt+D");
        vm.MicDeviceId.ShouldBe("abc");
        vm.PlaySounds.ShouldBeFalse();
    }

    [Fact]
    public void Setting_HoldHotkey_Queues_Write()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);
        vm.HoldHotkey = "RightAlt+F12";
        w.WriteCount.ShouldBe(1);
        w.Current.HoldHotkey.ShouldBe("RightAlt+F12");
    }

    [Fact]
    public void Setting_HoldHotkey_To_Same_Value_Is_NoOp()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings { HoldHotkey = "Ctrl+Shift+Space" }, w);
        vm.HoldHotkey = "Ctrl+Shift+Space";
        w.WriteCount.ShouldBe(0);
    }

    [Fact]
    public void Conflicting_HoldHotkey_Sets_Conflict_Message()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter());
        vm.HoldHotkey = "Ctrl+C";
        vm.HoldHotkeyConflict.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Same_Chord_For_Hold_And_Toggle_Surfaces_Conflict()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter());
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkeyConflict.ShouldContain("Hold");
    }

    [Fact]
    public void Setting_PlaySounds_Raises_PropertyChanged()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter());
        var changes = new List<string>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? "");
        vm.PlaySounds = false;
        changes.ShouldContain(nameof(RecordingSettingsViewModel.PlaySounds));
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~RecordingSettingsViewModelTests"
```

Expected: type missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`**

The view model takes a `IHotkeyValidator` strategy instead of referencing `Winpepper.Platform` directly — that would create a circular project reference because `Winpepper.Platform` already references `Winpepper.Core`. The WinUI app (Task 18) injects a `PlatformHotkeyValidator` that delegates to `HotkeyChord.Parse` and `HotkeyConflicts.Describe`. Unit tests inject a fake.

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public interface IHotkeyValidator
{
    /// <summary>Returns null when valid; an error or conflict description otherwise.</summary>
    string? Validate(string chord);
    /// <summary>Returns true when the two chords would fire on the same key event.</summary>
    bool Clash(string a, string b);
}

public sealed class RecordingSettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsWriter _writer;
    private readonly IHotkeyValidator _validator;
    private string _holdHotkey;
    private string _toggleHotkey;
    private string _micDeviceId;
    private bool _playSounds;
    private bool _speakerFilterEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RecordingSettingsViewModel(AppSettings initial, ISettingsWriter writer, IHotkeyValidator? validator = null)
    {
        _writer = writer;
        _validator = validator ?? new NullHotkeyValidator();
        _holdHotkey = initial.HoldHotkey;
        _toggleHotkey = initial.ToggleHotkey;
        _micDeviceId = initial.MicDeviceId;
        _playSounds = initial.PlaySounds;
        _speakerFilterEnabled = initial.SpeakerFilterEnabled;
    }

    private sealed class NullHotkeyValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    public string HoldHotkey
    {
        get => _holdHotkey;
        set
        {
            if (_holdHotkey == value) return;
            _holdHotkey = value;
            _writer.Queue(s => s with { HoldHotkey = value });
            Raise(nameof(HoldHotkey));
            Raise(nameof(HoldHotkeyConflict));
            Raise(nameof(ToggleHotkeyConflict));
        }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set
        {
            if (_toggleHotkey == value) return;
            _toggleHotkey = value;
            _writer.Queue(s => s with { ToggleHotkey = value });
            Raise(nameof(ToggleHotkey));
            Raise(nameof(HoldHotkeyConflict));
            Raise(nameof(ToggleHotkeyConflict));
        }
    }

    public string MicDeviceId
    {
        get => _micDeviceId;
        set
        {
            if (_micDeviceId == value) return;
            _micDeviceId = value;
            _writer.Queue(s => s with { MicDeviceId = value });
            Raise(nameof(MicDeviceId));
        }
    }

    public bool PlaySounds
    {
        get => _playSounds;
        set
        {
            if (_playSounds == value) return;
            _playSounds = value;
            _writer.Queue(s => s with { PlaySounds = value });
            Raise(nameof(PlaySounds));
        }
    }

    public bool SpeakerFilterEnabled
    {
        get => _speakerFilterEnabled;
        set
        {
            if (_speakerFilterEnabled == value) return;
            _speakerFilterEnabled = value;
            _writer.Queue(s => s with { SpeakerFilterEnabled = value });
            Raise(nameof(SpeakerFilterEnabled));
        }
    }

    public string? HoldHotkeyConflict => DescribeChord(_holdHotkey, _toggleHotkey, isToggle: false);
    public string? ToggleHotkeyConflict => DescribeChord(_toggleHotkey, _holdHotkey, isToggle: true);

    private string? DescribeChord(string chord, string other, bool isToggle)
    {
        var sys = _validator.Validate(chord);
        if (sys is not null) return sys;
        if (_validator.Clash(chord, other))
            return isToggle ? "Same as Hold hotkey." : "Same as Toggle hotkey.";
        return null;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Update tests to inject a fake validator that triggers conflict detection**

Replace the `Conflicting_HoldHotkey_Sets_Conflict_Message` and `Same_Chord_For_Hold_And_Toggle_Surfaces_Conflict` test bodies with versions that use a fake validator:

```csharp
    private sealed class FakeValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => chord == "Ctrl+C" ? "Conflicts with Copy" : null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    [Fact]
    public void Conflicting_HoldHotkey_Sets_Conflict_Message()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter(), new FakeValidator());
        vm.HoldHotkey = "Ctrl+C";
        vm.HoldHotkeyConflict.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Same_Chord_For_Hold_And_Toggle_Surfaces_Conflict()
    {
        var vm = new RecordingSettingsViewModel(new AppSettings(), new FakeWriter(), new FakeValidator());
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkeyConflict.ShouldContain("Hold");
    }
```

- [ ] **Step 5: Implement `PlatformHotkeyValidator` in `Winpepper.Platform`**

Create `/home/jesse/git/winpepper/src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs`:

```csharp
using Winpepper.Core.ViewModels;

namespace Winpepper.Platform.Hotkeys;

public sealed class PlatformHotkeyValidator : IHotkeyValidator
{
    public string? Validate(string chord)
    {
        HotkeyChord parsed;
        try { parsed = HotkeyChord.Parse(chord); }
        catch (FormatException ex) { return ex.Message; }
        return HotkeyConflicts.Describe(parsed);
    }

    public bool Clash(string a, string b)
    {
        try { return HotkeyConflicts.HoldAndToggleClash(HotkeyChord.Parse(a), HotkeyChord.Parse(b)); }
        catch { return false; }
    }
}
```

(`Winpepper.Platform` already references `Winpepper.Core`, so it can implement the Core-defined interface. No circular reference.)

- [ ] **Step 6: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~RecordingSettingsViewModelTests"
dotnet build src/Winpepper.Platform
```

Expected: 6 view-model tests pass; platform builds clean.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/ViewModels src/Winpepper.Platform/Hotkeys/PlatformHotkeyValidator.cs tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): RecordingSettingsViewModel + IHotkeyValidator strategy"
```

---

## Task 12: CleanupSettingsViewModel for the Cleanup tab

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs`

The Cleanup tab binds Plan 2's `CleanupSettings`. The view model is a thin reactive shell around that record. Until Plan 2 lands, we mirror its expected shape locally so the file compiles — Plan 2 swaps the type alias for the real one.

- [ ] **Step 1: Define a `CleanupSettingsContract` in `Winpepper.Core` (used until Plan 2 publishes its real record)**

Create `src/Winpepper.Core/ViewModels/CleanupSettingsContract.cs`:

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>
/// Plan-3 settings record bound to the Cleanup tab. Profile values are the
/// string names of Plan 2's <c>Winpepper.Cleanup.CleanupProfile</c> enum
/// ("Ordinary", "Literal", "Custom"). Persistence into <c>CleanupOptions</c>
/// happens through the adapter in <see cref="CleanupSettingsViewModel"/>.
/// Marked PLAN2-TYPE for easy search.
/// </summary>
public sealed record CleanupSettingsContract(
    bool Enabled,
    bool WindowContextEnabled,
    string Profile,
    string CustomPrompt,
    int MaxNewTokens,
    int TimeoutMs)
{
    public static CleanupSettingsContract Defaults() =>
        new(Enabled: true, WindowContextEnabled: false,
            Profile: "Ordinary", CustomPrompt: "",
            MaxNewTokens: 512, TimeoutMs: 15000);
}
```

- [ ] **Step 2: Write failing test `tests/Winpepper.Core.Tests/ViewModels/CleanupSettingsViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class CleanupSettingsViewModelTests
{
    [Fact]
    public void Defaults_Map_From_Contract()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.Enabled.ShouldBeTrue();
        vm.WindowContextEnabled.ShouldBeFalse();
        vm.Profile.ShouldBe("Ordinary");
        vm.MaxNewTokens.ShouldBe(512);
        vm.TimeoutMs.ShouldBe(15000);
    }

    [Fact]
    public void Setting_MaxNewTokens_Clamps_To_Min_64_Max_4096()
    {
        CleanupSettingsContract? last = null;
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), s => last = s);
        vm.MaxNewTokens = 10;
        vm.MaxNewTokens.ShouldBe(64);
        vm.MaxNewTokens = 10_000;
        vm.MaxNewTokens.ShouldBe(4096);
        last!.MaxNewTokens.ShouldBe(4096);
    }

    [Fact]
    public void Setting_TimeoutMs_Clamps_To_Min_2000_Max_60000()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.TimeoutMs = 500;
        vm.TimeoutMs.ShouldBe(2000);
        vm.TimeoutMs = 999_999;
        vm.TimeoutMs.ShouldBe(60000);
    }

    [Fact]
    public void Setting_Profile_To_Custom_Allows_Editing_CustomPrompt()
    {
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => { });
        vm.Profile = "Custom";
        vm.CustomPromptEditable.ShouldBeTrue();
        vm.Profile = "Ordinary";
        vm.CustomPromptEditable.ShouldBeFalse();
    }

    [Fact]
    public void Property_Set_Invokes_Persist_Callback()
    {
        var calls = 0;
        var vm = new CleanupSettingsViewModel(CleanupSettingsContract.Defaults(), _ => calls++);
        vm.Enabled = false;
        calls.ShouldBe(1);
    }
}
```

- [ ] **Step 3: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CleanupSettingsViewModelTests"
```

Expected: types missing.

- [ ] **Step 4: Implement `src/Winpepper.Core/ViewModels/CleanupSettingsViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Core.ViewModels;

public sealed class CleanupSettingsViewModel : INotifyPropertyChanged
{
    private readonly Action<CleanupSettingsContract> _persist;
    private CleanupSettingsContract _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CleanupSettingsViewModel(CleanupSettingsContract initial, Action<CleanupSettingsContract> persist)
    {
        _state = initial;
        _persist = persist;
    }

    public bool Enabled
    {
        get => _state.Enabled;
        set => Apply(_state with { Enabled = value }, nameof(Enabled));
    }

    public bool WindowContextEnabled
    {
        get => _state.WindowContextEnabled;
        set => Apply(_state with { WindowContextEnabled = value }, nameof(WindowContextEnabled));
    }

    public string Profile
    {
        get => _state.Profile;
        set
        {
            Apply(_state with { Profile = value }, nameof(Profile));
            Raise(nameof(CustomPromptEditable));
        }
    }

    public string CustomPrompt
    {
        get => _state.CustomPrompt;
        set => Apply(_state with { CustomPrompt = value }, nameof(CustomPrompt));
    }

    public int MaxNewTokens
    {
        get => _state.MaxNewTokens;
        set
        {
            var clamped = Math.Clamp(value, 64, 4096);
            if (clamped == _state.MaxNewTokens) return;
            Apply(_state with { MaxNewTokens = clamped }, nameof(MaxNewTokens));
        }
    }

    public int TimeoutMs
    {
        get => _state.TimeoutMs;
        set
        {
            var clamped = Math.Clamp(value, 2000, 60000);
            if (clamped == _state.TimeoutMs) return;
            Apply(_state with { TimeoutMs = clamped }, nameof(TimeoutMs));
        }
    }

    public bool CustomPromptEditable => string.Equals(_state.Profile, "Custom", StringComparison.Ordinal);

    private void Apply(CleanupSettingsContract next, string property)
    {
        if (Equals(next, _state)) return;
        _state = next;
        _persist(next);
        Raise(property);
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CleanupSettingsViewModelTests"
```

Expected: 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): CleanupSettingsViewModel with sliders + clamping"
```

---

## Task 13: CorrectionsViewModel for the Corrections tab

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/CorrectionEntry.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs`

Two lists: `Preferred` (single string per row) and `Replacements` (wrong → right). Inline validation per row: no empty, no duplicates, no self-mappings, min length 2. The view model accepts an `Action<IReadOnlyList<string>, IReadOnlyDictionary<string,string>>` persistence callback rather than depending on Plan 2's `CorrectionStore` directly — wiring up `CorrectionStore` happens in Task 18 (AppShell).

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/ViewModels/CorrectionsViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class CorrectionsViewModelTests
{
    [Fact]
    public void AddPreferred_Adds_Valid_Entry()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT").ShouldBeNull();
        vm.Preferred.Count.ShouldBe(1);
        vm.Preferred[0].Text.ShouldBe("ChatGPT");
        vm.Preferred[0].Error.ShouldBeNull();
    }

    [Fact]
    public void AddPreferred_Rejects_Short_String()
    {
        var vm = NewVm();
        vm.AddPreferred("a").ShouldContain("at least 2");
        vm.Preferred.Count.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_Rejects_Empty()
    {
        var vm = NewVm();
        vm.AddPreferred("  ").ShouldContain("empty");
        vm.Preferred.Count.ShouldBe(0);
    }

    [Fact]
    public void AddPreferred_Rejects_Duplicate()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT");
        vm.AddPreferred("ChatGPT").ShouldContain("duplicate");
        vm.Preferred.Count.ShouldBe(1);
    }

    [Fact]
    public void AddReplacement_Adds_Valid_Pair()
    {
        var vm = NewVm();
        vm.AddReplacement("chat gbt", "ChatGPT").ShouldBeNull();
        vm.Replacements.Count.ShouldBe(1);
        vm.Replacements[0].Wrong.ShouldBe("chat gbt");
        vm.Replacements[0].Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void AddReplacement_Rejects_Self_Mapping()
    {
        var vm = NewVm();
        vm.AddReplacement("chatgpt", "ChatGPT").ShouldBeNull(); // case differs → allowed
        vm.AddReplacement("chatgpt", "chatgpt").ShouldContain("same");
    }

    [Fact]
    public void AddReplacement_Rejects_Short_Sides()
    {
        var vm = NewVm();
        vm.AddReplacement("a", "ChatGPT").ShouldContain("at least 2");
        vm.AddReplacement("ChatGPT", "b").ShouldContain("at least 2");
    }

    [Fact]
    public void Remove_Removes_Entry()
    {
        var vm = NewVm();
        vm.AddPreferred("ChatGPT");
        vm.AddPreferred("Anthropic");
        vm.RemovePreferred(vm.Preferred[0]);
        vm.Preferred.Count.ShouldBe(1);
        vm.Preferred[0].Text.ShouldBe("Anthropic");
    }

    [Fact]
    public void Adds_Trigger_Persist_Callback()
    {
        var saves = 0;
        var vm = new CorrectionsViewModel(
            new List<string>(), new Dictionary<string, string>(),
            (_, _) => saves++);
        vm.AddPreferred("ChatGPT");
        vm.AddReplacement("chat gbt", "ChatGPT");
        saves.ShouldBe(2);
    }

    private static CorrectionsViewModel NewVm()
        => new(new List<string>(), new Dictionary<string, string>(), (_, _) => { });
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CorrectionsViewModelTests"
```

Expected: types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/ViewModels/CorrectionEntry.cs`**

```csharp
using System.ComponentModel;

namespace Winpepper.Core.ViewModels;

public sealed class PreferredEntry : INotifyPropertyChanged
{
    private string _text;
    private string? _error;
    public PreferredEntry(string text) { _text = text; }
    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }
    public string? Error { get => _error; set { _error = value; Raise(nameof(Error)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class ReplacementEntry : INotifyPropertyChanged
{
    private string _wrong;
    private string _right;
    private string? _error;
    public ReplacementEntry(string wrong, string right) { _wrong = wrong; _right = right; }
    public string Wrong { get => _wrong; set { _wrong = value; Raise(nameof(Wrong)); } }
    public string Right { get => _right; set { _right = value; Raise(nameof(Right)); } }
    public string? Error { get => _error; set { _error = value; Raise(nameof(Error)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/ViewModels/CorrectionsViewModel.cs`**

```csharp
using System.Collections.ObjectModel;

namespace Winpepper.Core.ViewModels;

public sealed class CorrectionsViewModel
{
    private readonly Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> _persist;

    public ObservableCollection<PreferredEntry> Preferred { get; } = new();
    public ObservableCollection<ReplacementEntry> Replacements { get; } = new();

    public CorrectionsViewModel(
        IEnumerable<string> initialPreferred,
        IEnumerable<KeyValuePair<string, string>> initialReplacements,
        Action<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> persist)
    {
        _persist = persist;
        foreach (var p in initialPreferred) Preferred.Add(new PreferredEntry(p));
        foreach (var r in initialReplacements) Replacements.Add(new ReplacementEntry(r.Key, r.Value));
    }

    public string? AddPreferred(string text)
    {
        var err = ValidatePreferred(text, ignoreSelf: null);
        if (err is not null) return err;
        Preferred.Add(new PreferredEntry(text.Trim()));
        Persist();
        return null;
    }

    public string? AddReplacement(string wrong, string right)
    {
        var err = ValidateReplacement(wrong, right, ignoreSelf: null);
        if (err is not null) return err;
        Replacements.Add(new ReplacementEntry(wrong.Trim(), right.Trim()));
        Persist();
        return null;
    }

    public void RemovePreferred(PreferredEntry e) { Preferred.Remove(e); Persist(); }
    public void RemoveReplacement(ReplacementEntry e) { Replacements.Remove(e); Persist(); }

    public string? ValidatePreferred(string text, PreferredEntry? ignoreSelf)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Cannot be empty.";
        var trimmed = text.Trim();
        if (trimmed.Length < 2) return "Must be at least 2 characters.";
        foreach (var p in Preferred)
        {
            if (ReferenceEquals(p, ignoreSelf)) continue;
            if (string.Equals(p.Text, trimmed, StringComparison.Ordinal)) return "Is a duplicate.";
        }
        return null;
    }

    public string? ValidateReplacement(string wrong, string right, ReplacementEntry? ignoreSelf)
    {
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return "Both sides required.";
        var w = wrong.Trim();
        var r = right.Trim();
        if (w.Length < 2 || r.Length < 2) return "Both sides must be at least 2 characters.";
        if (string.Equals(w, r, StringComparison.Ordinal)) return "Left and right sides are the same.";
        foreach (var existing in Replacements)
        {
            if (ReferenceEquals(existing, ignoreSelf)) continue;
            if (string.Equals(existing.Wrong, w, StringComparison.Ordinal)) return "Is a duplicate.";
        }
        return null;
    }

    public void Persist()
    {
        var p = Preferred.Select(x => x.Text).ToList();
        var r = Replacements.ToDictionary(x => x.Wrong, x => x.Right, StringComparer.Ordinal);
        _persist(p, r);
    }
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CorrectionsViewModelTests"
```

Expected: 9 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): CorrectionsViewModel with inline validation"
```

---

## Task 14: OnboardingViewModel

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/OnboardingStep.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`

State machine for the four-step onboarding (§7.4): `PickMic → PickHotkeys → DownloadModels → TestDictation → Done`. Each step has a `CanAdvance` predicate. Plan 3 wires the UI; the actual model downloader is stubbed and returns `Task.CompletedTask` (real downloader is Plan 4).

Spec §7.4 requires hotkey-conflict detection during onboarding. To enforce this, `OnboardingViewModel` takes a **required** `IHotkeyValidator` constructor parameter — no `NullHotkeyValidator` default. Production constructs it with `PlatformHotkeyValidator` (Task 11); unit tests pass a fake. The default chords (`RightCtrl+RightShift`, `Ctrl+Shift+Space`) are validated like any other chord, so if the default itself conflicts with a system-reserved chord on the user's locale/system, onboarding refuses to advance until the user picks an alternative.

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/ViewModels/OnboardingViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

[Trait("Layer", "ViewModel")]
public class OnboardingViewModelTests
{
    private sealed class FakeWriter : ISettingsWriter
    {
        public AppSettings Current = new();
        public void Queue(Func<AppSettings, AppSettings> m) => Current = m(Current);
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class PermissiveValidator : IHotkeyValidator
    {
        public string? Validate(string chord) => null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    private sealed class FakeValidator : IHotkeyValidator
    {
        private readonly HashSet<string> _conflicting;
        public FakeValidator(params string[] conflicting) =>
            _conflicting = new HashSet<string>(conflicting, StringComparer.Ordinal);
        public string? Validate(string chord) =>
            _conflicting.Contains(chord) ? $"{chord} conflicts with a system shortcut" : null;
        public bool Clash(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
    }

    [Fact]
    public void Initial_Step_Is_PickMic()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.Step.ShouldBe(OnboardingStep.PickMic);
    }

    [Fact]
    public void Cannot_Advance_From_PickMic_Until_Mic_Selected()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.CanAdvance.ShouldBeFalse();
        vm.SelectedMicDeviceId = "{abc-123}";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Advance_From_PickMic_Goes_To_PickHotkeys()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "{abc-123}";
        await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);
    }

    [Fact]
    public async Task Cannot_Advance_From_PickHotkeys_If_Conflict()
    {
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask, new FakeValidator("Ctrl+C"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        vm.HoldHotkey = "Ctrl+C";
        vm.CanAdvance.ShouldBeFalse();
        vm.HoldHotkey = "RightCtrl+RightShift";
        vm.ToggleHotkey = "Ctrl+Shift+Space";
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task Cannot_Advance_When_Default_Toggle_Chord_Is_Flagged_By_Validator()
    {
        // Spec §7.4 requires conflict detection. The defaults must not auto-pass
        // when the validator flags them; the user must pick a non-conflicting chord.
        var vm = new OnboardingViewModel(new FakeWriter(), () => Task.CompletedTask,
            new FakeValidator("Ctrl+Shift+Space"));
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.PickHotkeys);

        // The defaults wired in the constructor include "Ctrl+Shift+Space" for toggle,
        // which the fake validator flags. CanAdvance MUST be false until the user replaces it.
        vm.ToggleHotkey.ShouldBe("Ctrl+Shift+Space");
        vm.ToggleHotkeyError.ShouldNotBeNull();
        vm.CanAdvance.ShouldBeFalse();

        vm.ToggleHotkey = "RightAlt+RightShift";
        vm.ToggleHotkeyError.ShouldBeNull();
        vm.CanAdvance.ShouldBeTrue();
    }

    [Fact]
    public async Task DownloadModels_Step_Awaits_Stub_And_Advances()
    {
        var downloaded = false;
        var vm = new OnboardingViewModel(new FakeWriter(),
            () => { downloaded = true; return Task.CompletedTask; },
            new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(); // PickHotkeys
        await vm.AdvanceAsync();                                // DownloadModels
        await vm.AdvanceAsync();                                // runs stub, → TestDictation
        downloaded.ShouldBeTrue();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Skip_From_DownloadModels_Advances_Without_Running_Stub()
    {
        var downloaded = false;
        var vm = new OnboardingViewModel(new FakeWriter(),
            () => { downloaded = true; return Task.CompletedTask; },
            new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync(); // PickHotkeys
        await vm.AdvanceAsync();                                // DownloadModels
        vm.Skip();
        downloaded.ShouldBeFalse();
        vm.Step.ShouldBe(OnboardingStep.TestDictation);
    }

    [Fact]
    public async Task Finish_Sets_OnboardingCompleted()
    {
        var w = new FakeWriter();
        var vm = new OnboardingViewModel(w, () => Task.CompletedTask, new PermissiveValidator());
        vm.SelectedMicDeviceId = "x"; await vm.AdvanceAsync();
        await vm.AdvanceAsync(); vm.Skip();
        vm.TestDictationDone = true;
        await vm.AdvanceAsync();
        vm.Step.ShouldBe(OnboardingStep.Done);
        w.Current.OnboardingCompleted.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~OnboardingViewModelTests"
```

Expected: types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/ViewModels/OnboardingStep.cs`**

```csharp
namespace Winpepper.Core.ViewModels;

public enum OnboardingStep
{
    PickMic,
    PickHotkeys,
    DownloadModels,
    TestDictation,
    Done,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.Core.Settings;

namespace Winpepper.Core.ViewModels;

public sealed class OnboardingViewModel : INotifyPropertyChanged
{
    private readonly ISettingsWriter _writer;
    private readonly Func<Task> _runDownloader;
    private readonly IHotkeyValidator _validator;

    private OnboardingStep _step = OnboardingStep.PickMic;
    private string _micId = "";
    private string _holdHotkey = "RightCtrl+RightShift";
    private string _toggleHotkey = "Ctrl+Shift+Space";
    private bool _testDictationDone;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Spec §7.4 requires real hotkey-conflict detection during onboarding.
    /// <paramref name="validator"/> is required — production wires a
    /// <c>PlatformHotkeyValidator</c>; unit tests pass a fake. There is no
    /// permissive default: a permissive default would mask conflicts on the
    /// onboarding step.
    /// </summary>
    public OnboardingViewModel(ISettingsWriter writer, Func<Task> runDownloader, IHotkeyValidator validator)
    {
        _writer = writer;
        _runDownloader = runDownloader;
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public OnboardingStep Step
    {
        get => _step;
        private set { if (_step == value) return; _step = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(CanSkip)); }
    }

    public string SelectedMicDeviceId
    {
        get => _micId;
        set { if (_micId == value) return; _micId = value; Raise(); Raise(nameof(CanAdvance)); }
    }

    public string HoldHotkey
    {
        get => _holdHotkey;
        set { if (_holdHotkey == value) return; _holdHotkey = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(HoldHotkeyError)); Raise(nameof(ToggleHotkeyError)); }
    }

    public string ToggleHotkey
    {
        get => _toggleHotkey;
        set { if (_toggleHotkey == value) return; _toggleHotkey = value; Raise(); Raise(nameof(CanAdvance)); Raise(nameof(HoldHotkeyError)); Raise(nameof(ToggleHotkeyError)); }
    }

    public bool TestDictationDone
    {
        get => _testDictationDone;
        set { if (_testDictationDone == value) return; _testDictationDone = value; Raise(); Raise(nameof(CanAdvance)); }
    }

    public string? HoldHotkeyError => Validate(_holdHotkey, _toggleHotkey, isToggle: false);
    public string? ToggleHotkeyError => Validate(_toggleHotkey, _holdHotkey, isToggle: true);

    public bool CanAdvance => _step switch
    {
        OnboardingStep.PickMic        => !string.IsNullOrEmpty(_micId),
        OnboardingStep.PickHotkeys    => HoldHotkeyError is null && ToggleHotkeyError is null,
        OnboardingStep.DownloadModels => true,
        OnboardingStep.TestDictation  => _testDictationDone,
        _ => false,
    };

    public bool CanSkip => _step == OnboardingStep.DownloadModels;

    public async Task AdvanceAsync()
    {
        if (!CanAdvance) return;
        switch (_step)
        {
            case OnboardingStep.PickMic:
                _writer.Queue(s => s with { MicDeviceId = _micId });
                Step = OnboardingStep.PickHotkeys;
                break;
            case OnboardingStep.PickHotkeys:
                _writer.Queue(s => s with { HoldHotkey = _holdHotkey, ToggleHotkey = _toggleHotkey });
                Step = OnboardingStep.DownloadModels;
                break;
            case OnboardingStep.DownloadModels:
                await _runDownloader();
                Step = OnboardingStep.TestDictation;
                break;
            case OnboardingStep.TestDictation:
                _writer.Queue(s => s with { OnboardingCompleted = true });
                await _writer.FlushAsync();
                Step = OnboardingStep.Done;
                break;
        }
    }

    public void Skip()
    {
        if (!CanSkip) return;
        Step = OnboardingStep.TestDictation;
    }

    private string? Validate(string chord, string other, bool isToggle)
    {
        var sys = _validator.Validate(chord);
        if (sys is not null) return sys;
        if (_validator.Clash(chord, other))
            return isToggle ? "Same as Hold." : "Same as Toggle.";
        return null;
    }

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~OnboardingViewModelTests"
```

Expected: 8 tests pass (including the new `Cannot_Advance_When_Default_Toggle_Chord_Is_Flagged_By_Validator` case).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/ViewModels tests/Winpepper.Core.Tests/ViewModels
git commit -m "feat(core): OnboardingViewModel state machine"
```

---

## Task 15: DispatcherQueueUiThread (WinUI bridge)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Threading/DispatcherQueueUiThread.cs`

Concrete `IUiThread` for WinUI 3. No unit test — the type is two lines and is exercised manually by the smoke procedure in Task 25.

- [ ] **Step 1: Implement `src/Winpepper.App/Threading/DispatcherQueueUiThread.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Dispatching;
using Winpepper.Core.Threading;

namespace Winpepper.App.Threading;

public sealed class DispatcherQueueUiThread : IUiThread
{
    private readonly DispatcherQueue _queue;
    public DispatcherQueueUiThread(DispatcherQueue queue) { _queue = queue; }
    public bool HasThreadAccess => _queue.HasThreadAccess;
    public void Post(Action action) { _queue.TryEnqueue(() => action()); }
}
#endif
```

- [ ] **Step 2: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Threading
git commit -m "feat(app): WinUI DispatcherQueue IUiThread implementation"
```

---

## Task 16: App.xaml and App.xaml.cs entry point

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/App.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/App.xaml.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Program.cs`

Entry point. `Program.Main` uses `Microsoft.UI.Xaml.Application.Start`. `App.OnLaunched` instantiates the `AppShell` (Task 18), boots the pipeline, and decides between showing onboarding vs. tray-only.

- [ ] **Step 1: Write `src/Winpepper.App/App.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Application
    x:Class="Winpepper.App.App"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 2: Write `src/Winpepper.App/App.xaml.cs`**

```csharp
using Microsoft.UI.Xaml;
using Winpepper.App.Hosting;

namespace Winpepper.App;

public partial class App : Application
{
    public static AppShell? Shell { get; private set; }

    public App() { InitializeComponent(); }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Shell = await AppShell.BootstrapAsync(this);
    }
}
```

- [ ] **Step 3: Write `src/Winpepper.App/Program.cs`**

```csharp
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Winpepper.App;

namespace Winpepper.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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

        ComWrappersSupport.InitializeComWrappers();
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

- [ ] **Step 4: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build fails — `AppShell` doesn't exist yet (Task 18 creates it). Capture the error to confirm it's the expected unresolved symbol; do not commit yet. Skip ahead to Task 18, then return here and run the build again.

- [ ] **Step 5: After Task 18 (AppShell) lands, re-run build and commit**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
git add src/Winpepper.App/App.xaml src/Winpepper.App/App.xaml.cs src/Winpepper.App/Program.cs
git commit -m "feat(app): App entry point + single-instance guard"
```

---

## Task 17: TrayIconHost

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayIconHost.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayMenu.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayMenu.xaml.cs`

§7.1. `H.NotifyIcon.WinUI` provides the tray icon. The tooltip and menu are data-bound to `SessionViewModel`; the icon image swaps based on `SessionStage`. The menu items are Settings, Diagnostics, Pause, Quit. Diagnostics navigates to the existing main window but the Diagnostics tab itself ships in Plan 5 — for Plan 3 the menu item is disabled with a tooltip "Available in Plan 5".

- [ ] **Step 1: Write `src/Winpepper.App/Tray/TrayMenu.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<MenuFlyout
    x:Class="Winpepper.App.Tray.TrayMenu"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <MenuFlyoutItem x:Name="StatusItem" Text="Ready" IsEnabled="False" />
    <ProgressBar x:Name="StatusProgress" Visibility="Collapsed" IsIndeterminate="True" Margin="12,4" Width="160" Height="3" />
    <MenuFlyoutSeparator />
    <MenuFlyoutItem x:Name="OpenSettings" Text="Settings..." />
    <MenuFlyoutItem x:Name="OpenDiagnostics" Text="Diagnostics (Plan 5)" IsEnabled="False" />
    <ToggleMenuFlyoutItem x:Name="PauseItem" Text="Pause dictation" />
    <MenuFlyoutSeparator />
    <MenuFlyoutItem x:Name="QuitItem" Text="Quit" />
    <MenuFlyoutSeparator />
    <MenuFlyoutItem x:Name="VersionItem" Text="Winpepper v0.0.0" IsEnabled="False" />
</MenuFlyout>
```

- [ ] **Step 2: Write `src/Winpepper.App/Tray/TrayMenu.xaml.cs`**

```csharp
using Microsoft.UI.Xaml.Controls;

namespace Winpepper.App.Tray;

public sealed partial class TrayMenu : MenuFlyout
{
    public TrayMenu() { InitializeComponent(); }

    public MenuFlyoutItem StatusItemControl => StatusItem;
    public ProgressBar StatusProgressBar => StatusProgress;
    public MenuFlyoutItem SettingsItem => OpenSettings;
    public ToggleMenuFlyoutItem PauseToggle => PauseItem;
    public MenuFlyoutItem QuitMenuItem => QuitItem;
    public MenuFlyoutItem VersionLabel => VersionItem;
}
```

- [ ] **Step 3: Write `src/Winpepper.App/Tray/TrayIconHost.cs`**

```csharp
#if WINDOWS
using System.ComponentModel;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Tray;

public sealed class TrayIconHost : IDisposable
{
    private readonly SessionViewModel _session;
    private readonly TaskbarIcon _icon;
    private readonly TrayMenu _menu;
    private readonly Action _openSettings;
    private readonly Action _quit;
    private bool _paused;

    public TrayIconHost(SessionViewModel session, string assetsDir, string versionString,
                        Action openSettings, Action quit)
    {
        _session = session;
        _openSettings = openSettings;
        _quit = quit;
        _menu = new TrayMenu();
        _icon = new TaskbarIcon
        {
            ToolTipText = "Winpepper - Ready",
            IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(assetsDir, "AppIcon.ico"))),
            ContextFlyout = _menu,
            NoLeftClickDelay = true,
        };
        _icon.LeftClickCommand = new SimpleCommand(openSettings);
        _menu.SettingsItem.Click += (_, _) => openSettings();
        _menu.PauseToggle.Click += (_, _) =>
        {
            // Pause is a UI-only label change. Don't go through NotifyError —
            // that channel sets Stage = Error and paints the pill yellow.
            _paused = _menu.PauseToggle.IsChecked;
            UpdateFromSession();
        };
        _menu.QuitMenuItem.Click += (_, _) => quit();
        _menu.VersionLabel.Text = $"Winpepper v{versionString}";
        _session.PropertyChanged += OnSessionChanged;
        UpdateFromSession();
    }

    public bool IsPaused => _paused;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))
            UpdateFromSession();
    }

    private void UpdateFromSession()
    {
        var label = _paused ? "Paused" : _session.StatusText;
        _menu.StatusItemControl.Text = label;
        _icon.ToolTipText = $"Winpepper - {label}";
        _menu.StatusProgressBar.Visibility =
            !_paused && _session.Stage is SessionStage.Recording or SessionStage.Transcribing or SessionStage.CleaningUp
                ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _icon.Dispose();
    }

    private sealed class SimpleCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;
        public SimpleCommand(Action action) { _action = action; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? _) => true;
        public void Execute(object? _) => _action();
    }
}
#endif
```

- [ ] **Step 4: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build succeeds (AppShell still references types defined in Task 18; if you've not yet done Task 18 the build will fail on `AppShell` only — that's fine, you'll commit Task 17 alongside Task 18).

- [ ] **Step 5: Commit (after Task 18)**

```bash
git add src/Winpepper.App/Tray
git commit -m "feat(app): TrayIconHost bound to SessionViewModel"
```

---

## Task 18: AppShell — single-instance host wiring the pipeline

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppPaths.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`

`AppShell` owns the long-lived objects: logger, settings store + writer, view models, tray, status pill, main window. It boots the pipeline (re-using `Winpepper.Cli.Pipeline` logic, copied here and refactored into `PipelineHost`).

- [ ] **Step 1: Write `src/Winpepper.App/Hosting/AppPaths.cs`**

```csharp
namespace Winpepper.App.Hosting;

public static class AppPaths
{
    public static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Root => Path.Combine(LocalAppData, "winpepper");
    public static string LogsDir => Path.Combine(Root, "logs");
    public static string ParakeetModelDir => Path.Combine(Root, "models", "parakeet-tdt-0.6b-v3");
    public static string SettingsJson => Path.Combine(Root, "settings.json");
    public static string CorrectionsJson => Path.Combine(Root, "corrections.json");
    public static string CleanupSettingsJson => Path.Combine(Root, "cleanup-settings.json");

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");
}
```

- [ ] **Step 2: Write `src/Winpepper.App/Hosting/PipelineHost.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Asr;
using Winpepper.Audio;
using Winpepper.Core.Audio;
using Winpepper.Core.Sessions;
using Winpepper.Core.ViewModels;
using Winpepper.Platform.Hotkeys;
using Winpepper.Platform.Injection;

namespace Winpepper.App.Hosting;

/// <summary>
/// Plan-1 pipeline lifted out of Winpepper.Cli.Pipeline and bound to the
/// session view model + sound-effect player. Cleanup wiring (Plan 2) is added
/// in Task 24 once Plan 2 lands; for Plan 3 we run the raw transcript through
/// injection just like Plan 1 did.
/// </summary>
public sealed class PipelineHost : IDisposable
{
    private readonly ILogger<PipelineHost> _log;
    private readonly HotkeyHook _hook;
    private readonly TextInjector _injector;
    private readonly ParakeetSession _asr;
    private readonly SessionEngine _engine;
    private readonly SessionViewModel _vm;
    private readonly ISoundEffectPlayer _sounds;
    private IAudioRecorder? _recorder;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    public PipelineHost(
        ILoggerFactory factory,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
    }

    public void Start()
    {
        _hook.Start();
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _hook.Events.ReadAllAsync(ct))
            {
                try { await HandleHotkey(evt, ct); }
                catch (Exception ex) { _log.LogError(ex, "pipeline error"); _engine.Apply(SessionEvent.Failed); _vm.NotifyError(ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task HandleHotkey(HotkeyEvent evt, CancellationToken ct)
    {
        switch (evt.Kind)
        {
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _sounds.PlayStart();
                _recorder = new WasapiRecorder();
                _recorder.Start();
                break;
            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
                _sounds.PlayStop();
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _engine.Apply(SessionEvent.TranscriptReady);
                if (!string.IsNullOrWhiteSpace(transcript.Text)) _injector.TryInject(transcript.Text);
                _engine.Apply(SessionEvent.InjectionCompleted);
                break;
            case HotkeyEventKind.Cancel:
                _engine.Apply(SessionEvent.CancelRequested);
                _recorder?.Dispose(); _recorder = null;
                break;
            case HotkeyEventKind.Toggle:
                if (_engine.State == SessionState.Idle)
                {
                    _engine.Apply(SessionEvent.StartRequested);
                    _sounds.PlayStart();
                    _recorder = new WasapiRecorder();
                    _recorder.Start();
                }
                else if (_engine.State == SessionState.Recording)
                {
                    _engine.Apply(SessionEvent.StopRequested);
                    var samples = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
                    _sounds.PlayStop();
                    var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                    _engine.Apply(SessionEvent.TranscriptReady);
                    if (!string.IsNullOrWhiteSpace(transcript.Text)) _injector.TryInject(transcript.Text);
                    _engine.Apply(SessionEvent.InjectionCompleted);
                }
                break;
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _hook.Dispose();
        _asr.Dispose();
        _recorder?.Dispose();
    }
}
#endif
```

- [ ] **Step 3: Write `src/Winpepper.App/Hosting/AppShell.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Winpepper.App.Audio;
using Winpepper.App.Threading;
using Winpepper.App.Tray;
using Winpepper.App.Views;
using Winpepper.Core.Logging;
using Winpepper.Core.Sessions;
using Winpepper.Core.Settings;
using Winpepper.Core.ViewModels;
using Winpepper.Platform.Autostart;
using Winpepper.Platform.Hotkeys;

namespace Winpepper.App.Hosting;

public sealed class AppShell : IDisposable
{
    public ILoggerFactory LogFactory { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; private set; }
    public DebouncedSettingsWriter SettingsWriter { get; }
    public SessionEngine Engine { get; }
    public SessionViewModel SessionVm { get; }
    public RecordingSettingsViewModel RecordingVm { get; }
    public CleanupSettingsViewModel CleanupVm { get; }
    public CorrectionsViewModel CorrectionsVm { get; }
    public IAutostartRegistry Autostart { get; }
    public PipelineHost Pipeline { get; }
    public TrayIconHost Tray { get; }
    public StatusPillWindow Pill { get; }
    public MainWindow Main { get; private set; }

    private readonly WinUiSoundEffectPlayer _sounds;

    public static async Task<AppShell> BootstrapAsync(Application _)
    {
        Directory.CreateDirectory(AppPaths.Root);
        var factory = WinpepperLogging.Create(AppPaths.LogsDir, debugConsole: false, minimumLevel: LogLevel.Information);
        var store = new SettingsStore(AppPaths.SettingsJson);
        var settings = store.Load();
        var writer = new DebouncedSettingsWriter(store);

        var uiThread = new DispatcherQueueUiThread(DispatcherQueue.GetForCurrentThread());
        var engine = new SessionEngine();
        var sessionVm = new SessionViewModel(engine, uiThread);
        var hotkeyValidator = new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator();
        var recordingVm = new RecordingSettingsViewModel(settings, writer, hotkeyValidator);
        var cleanupContract = CleanupSettingsContract.Defaults();
        var cleanupVm = new CleanupSettingsViewModel(cleanupContract, _ => { /* Plan 2 wires real persistence */ });

        // Plan 2 normally provides initial corrections; until then, empty.
        var correctionsVm = new CorrectionsViewModel(
            Array.Empty<string>(),
            new Dictionary<string, string>(),
            (_, _) => { /* Plan 2 wires CorrectionStore.Save() here */ });

        var autostart = new AutostartRegistry();
        var sounds = new WinUiSoundEffectPlayer(AppPaths.AssetsDir) { Enabled = settings.PlaySounds };

        var hold   = HotkeyChord.Parse(settings.HoldHotkey);
        var toggle = HotkeyChord.Parse(settings.ToggleHotkey);
        var cancel = HotkeyChord.Parse("Esc");
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir);

        var shell = new AppShell(factory, store, settings, writer, engine, sessionVm,
                                  recordingVm, cleanupVm, correctionsVm,
                                  autostart, pipeline, sounds);
        await shell.StartAsync();
        return shell;
    }

    private AppShell(ILoggerFactory factory, SettingsStore store, AppSettings settings,
                     DebouncedSettingsWriter writer, SessionEngine engine,
                     SessionViewModel sessionVm,
                     RecordingSettingsViewModel recVm, CleanupSettingsViewModel cleanupVm,
                     CorrectionsViewModel corrVm, IAutostartRegistry autostart,
                     PipelineHost pipeline, WinUiSoundEffectPlayer sounds)
    {
        LogFactory = factory; SettingsStore = store; Settings = settings;
        SettingsWriter = writer; Engine = engine; SessionVm = sessionVm; RecordingVm = recVm;
        CleanupVm = cleanupVm; CorrectionsVm = corrVm; Autostart = autostart;
        Pipeline = pipeline; _sounds = sounds;

        Pill = new StatusPillWindow(sessionVm);
        Tray = new TrayIconHost(sessionVm, AppPaths.AssetsDir, "0.3.0",
                                 openSettings: ShowMain, quit: Quit);
        Main = new MainWindow(this);
    }

    private async Task StartAsync()
    {
        Pipeline.Start();
        await Task.CompletedTask;
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
    }

    public void ShowMain() => ShowMain(navigateToOnboarding: false);

    public void ShowMain(bool navigateToOnboarding)
    {
        if (Main is null || Main.AppWindow is null) Main = new MainWindow(this);
        Main.Activate();
        if (navigateToOnboarding) Main.NavigateToOnboarding();
    }

    public void Quit()
    {
        Dispose();
        Application.Current.Exit();
    }

    public void Dispose()
    {
        Pipeline.Dispose();
        Tray.Dispose();
        Pill.Close();
        SettingsWriter.Dispose();
        _sounds.Dispose();
        WinpepperLogging.Flush();
    }
}
#endif
```

- [ ] **Step 4: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build fails — `MainWindow`, `StatusPillWindow` not yet defined. That's expected; they land in Tasks 19 and 20. Continue to those tasks, then run this build again.

- [ ] **Step 5: After Tasks 19 + 20 land, commit Tasks 16–18 together**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
git add src/Winpepper.App/Hosting
git commit -m "feat(app): AppShell wires logger, settings, pipeline, tray, windows"
```

---

## Task 19: StatusPillWindow — frameless click-through AppWindow

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/StatusPillWindow.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/StatusPillWindow.xaml.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`

Spec §7.2. Frameless, transparent, top-most, click-through, anchored bottom-center of the foreground window's screen, 600 ms hide-delay after Idle.

**Spec §13.3 — open question resolved here:** the `WS_EX_TRANSPARENT` extended style MUST be applied **after** the window's HWND is created (`AppWindow` materializes the HWND after `this.AppWindow` is first read), and **after** `WS_EX_LAYERED` is set. `H.NotifyIcon.WinUI`'s `WindowExtensions.GetWindowHandle` returns the HWND once the window is constructed; the order is:

1. Construct the WinUI `Window`.
2. Call `WindowNative.GetWindowHandle(this)` (one-time).
3. `SetWindowLongPtr(GWL_EXSTYLE, current | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)`.
4. `SetLayeredWindowAttributes(hwnd, 0, 230, LWA_ALPHA)` to set opacity (≈90%).
5. Set `AppWindow.IsShownInSwitchers = false` and the `OverlappedPresenter.IsAlwaysOnTop = true`, `IsMaximizable = false`, `IsMinimizable = false`, `IsResizable = false`, `SetBorderAndTitleBar(false, false)`.

If we apply `WS_EX_TRANSPARENT` before the HWND is realized, the call is a no-op; if we apply it before `WS_EX_LAYERED`, Windows ignores transparency. Hold this exact ordering in `OnActivated`.

- [ ] **Step 1: Write `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`**

```csharp
#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ExtendedWindowStyle
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_LAYERED     = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW  = 0x00000080;
    public const int WS_EX_NOACTIVATE  = 0x08000000;
    public const int LWA_ALPHA         = 0x00000002;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);

    public static void MakeClickThroughTopmostTool(IntPtr hwnd, byte alpha = 230)
    {
        // ORDER MATTERS: read existing styles, OR in LAYERED + TRANSPARENT + TOOLWINDOW + NOACTIVATE,
        // commit with SetWindowLongPtr BEFORE calling SetLayeredWindowAttributes.
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated  = existing | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }
}
#endif
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/StatusPillWindow.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="Winpepper.App.Views.StatusPillWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Background="Transparent" Padding="12,6">
        <Border Background="#CC202020" CornerRadius="14" Padding="14,6">
            <StackPanel Orientation="Horizontal" Spacing="10" VerticalAlignment="Center">
                <Ellipse x:Name="Dot" Width="10" Height="10" Fill="#FFEF4444" />
                <TextBlock x:Name="StatusText" Foreground="White" FontSize="13" Text="Recording..." />
                <TextBlock x:Name="ElapsedText" Foreground="#AAFFFFFF" FontSize="12" Text="0 ms" />
            </StackPanel>
        </Border>
    </Grid>
</Window>
```

- [ ] **Step 3: Write `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`**

```csharp
#if WINDOWS
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Winpepper.App.Views.Native;
using Winpepper.Core.ViewModels;
using WinRT.Interop;

namespace Winpepper.App.Views;

public sealed partial class StatusPillWindow : Window
{
    private readonly SessionViewModel _vm;
    private readonly DispatcherTimer _hideTimer;
    private readonly DispatcherTimer _tickTimer;
    private IntPtr _hwnd;

    public StatusPillWindow(SessionViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        // Step 1: realize HWND.
        _hwnd = WindowNative.GetWindowHandle(this);

        // Step 2: apply WS_EX_LAYERED then WS_EX_TRANSPARENT (see plan §13.3 note).
        ExtendedWindowStyle.MakeClickThroughTopmostTool(_hwnd, alpha: 230);

        // Step 3: AppWindow tweaks for frameless top-most.
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
            p.SetBorderAndTitleBar(false, false);
        }
        appWindow.Resize(new SizeInt32(260, 44));

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); appWindow.Hide(); };

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tickTimer.Tick += (_, _) => { _vm.Tick(); ElapsedText.Text = $"{_vm.ElapsedMs} ms"; };

        _vm.PropertyChanged += OnVmChanged;
        appWindow.Hide();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SessionViewModel.Stage) or nameof(SessionViewModel.StatusText))) return;

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        StatusText.Text = _vm.StatusText;

        if (_vm.Stage == SessionStage.Idle)
        {
            _tickTimer.Stop();
            _hideTimer.Stop(); _hideTimer.Start();
        }
        else if (_vm.Stage == SessionStage.Error)
        {
            _tickTimer.Stop();
            Dot.Fill = new SolidColorBrush(Microsoft.UI.Colors.Goldenrod);
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _hideTimer.Stop();
        }
        else
        {
            Dot.Fill = new SolidColorBrush(_vm.Stage switch
            {
                SessionStage.Recording   => Microsoft.UI.Colors.Red,
                SessionStage.Transcribing => Microsoft.UI.Colors.Orange,
                SessionStage.CleaningUp  => Microsoft.UI.Colors.Orange,
                SessionStage.Injecting   => Microsoft.UI.Colors.LimeGreen,
                _ => Microsoft.UI.Colors.Gray,
            });
            PositionBottomCenter(appWindow);
            appWindow.Show(activateWindow: false);
            _tickTimer.Start();
            _hideTimer.Stop();
        }
    }

    private void PositionBottomCenter(AppWindow appWindow)
    {
        var fgHwnd = Native.ForegroundWindow.GetForegroundWindow();
        var display = fgHwnd != IntPtr.Zero
            ? DisplayArea.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(fgHwnd), DisplayAreaFallback.Nearest)
            : DisplayArea.Primary;
        var work = display.WorkArea;
        var x = work.X + (work.Width  - appWindow.Size.Width)  / 2;
        var y = work.Y +  work.Height - appWindow.Size.Height - 48;
        appWindow.Move(new PointInt32(x, y));
    }
}
#endif
```

- [ ] **Step 4: Write `src/Winpepper.App/Views/Native/ForegroundWindow.cs`**

```csharp
#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.App.Views.Native;

internal static class ForegroundWindow
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
#endif
```

- [ ] **Step 5: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build still fails on `MainWindow` (Task 20). Continue.

- [ ] **Step 6: Commit (with Task 20)**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml src/Winpepper.App/Views/StatusPillWindow.xaml.cs src/Winpepper.App/Views/Native
git commit -m "feat(app): click-through status pill with §13.3 style ordering"
```

---

## Task 20: MainWindow shell (NavigationView)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/MainWindow.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/MainWindow.xaml.cs`

Main window holds the `NavigationView` and a `Frame` for page navigation. Plan 3 ships three pages plus the onboarding view: `RecordingPage`, `CleanupPage`, `CorrectionsPage`, `OnboardingPage`. History/Lab/Models/Diagnostics are nav items but are disabled with "Available in Plan 4/5" tooltips.

- [ ] **Step 1: Write `src/Winpepper.App/Views/MainWindow.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Window
    x:Class="Winpepper.App.Views.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid>
        <NavigationView x:Name="Nav" PaneDisplayMode="Left" IsSettingsVisible="False">
            <NavigationView.MenuItems>
                <NavigationViewItem Tag="recording"   Content="Recording" />
                <NavigationViewItem Tag="cleanup"     Content="Cleanup" />
                <NavigationViewItem Tag="corrections" Content="Corrections" />
                <NavigationViewItemSeparator />
                <NavigationViewItem Tag="history"     Content="History" IsEnabled="False" ToolTipService.ToolTip="Available in Plan 4" />
                <NavigationViewItem Tag="lab"         Content="Lab" IsEnabled="False" ToolTipService.ToolTip="Available in Plan 4" />
                <NavigationViewItem Tag="models"      Content="Models" IsEnabled="False" ToolTipService.ToolTip="Available in Plan 4" />
                <NavigationViewItem Tag="diagnostics" Content="Diagnostics" IsEnabled="False" ToolTipService.ToolTip="Available in Plan 5" />
            </NavigationView.MenuItems>
            <Frame x:Name="ContentFrame" />
        </NavigationView>
    </Grid>
</Window>
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/MainWindow.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Winpepper.App.Hosting;

namespace Winpepper.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;
    public MainWindow(AppShell shell)
    {
        _shell = shell;
        InitializeComponent();
        Title = "Winpepper";
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void NavigateToOnboarding()
    {
        ContentFrame.Navigate(typeof(OnboardingPage), _shell);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var pageType = (string?)item.Tag switch
        {
            "recording"   => typeof(RecordingPage),
            "cleanup"     => typeof(CleanupPage),
            "corrections" => typeof(CorrectionsPage),
            _ => null,
        };
        if (pageType is not null)
            ContentFrame.Navigate(pageType, _shell);
    }
}
#endif
```

- [ ] **Step 3: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build fails on missing page types (`RecordingPage`, `CleanupPage`, `CorrectionsPage`, `OnboardingPage`). Those land in Tasks 21–23 and 25.

- [ ] **Step 4: Commit (when pages exist)**

```bash
git add src/Winpepper.App/Views/MainWindow.xaml src/Winpepper.App/Views/MainWindow.xaml.cs
git commit -m "feat(app): MainWindow NavigationView shell"
```

---

## Task 21: RecordingPage

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/RecordingPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/RecordingPage.xaml.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs`

UI for §7.3 Recording: hold-to-record + toggle-to-record hotkey recorders, mic picker (populated from `Winpepper.Audio.DeviceEnumerator`), live-level meter (shows a `ProgressBar` driven by `WasapiRecorder.FramesAvailable`), sound-effect toggle, speaker-filter toggle, "Test dictation" button. The "Test dictation" button just focuses a TextBox so the next dictation lands in it.

- [ ] **Step 1: Write `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<UserControl
    x:Class="Winpepper.App.Views.Controls.HotkeyRecorderBox"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Spacing="4">
        <TextBlock x:Name="LabelBlock" Text="Hotkey" />
        <Border BorderBrush="{ThemeResource ControlElevationBorderBrush}" BorderThickness="1" CornerRadius="4" Padding="8,4">
            <Grid>
                <TextBlock x:Name="ChordText" Text="(press a chord)" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                <Button x:Name="RecordButton" Content="Record" HorizontalAlignment="Right" Click="OnRecordClick" />
            </Grid>
        </Border>
        <TextBlock x:Name="ErrorText" Foreground="OrangeRed" Visibility="Collapsed" />
    </StackPanel>
</UserControl>
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/Controls/HotkeyRecorderBox.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Winpepper.Platform.Hotkeys;

namespace Winpepper.App.Views.Controls;

public sealed partial class HotkeyRecorderBox : UserControl
{
    public event Action<string>? ChordRecorded;
    private bool _recording;

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(HotkeyRecorderBox), new PropertyMetadata("Hotkey",
            (d, e) => ((HotkeyRecorderBox)d).LabelBlock.Text = (string)e.NewValue));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public HotkeyRecorderBox()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        IsTabStop = true;
    }

    public void SetChord(string chord, string? error)
    {
        ChordText.Text = chord;
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRecordClick(object sender, RoutedEventArgs e)
    {
        _recording = true;
        ChordText.Text = "(press a chord)";
        Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows)
            return;

        var mods = "";
        var window = Microsoft.UI.Xaml.Window.Current; // null in WinUI 3 — query via input mgr.
        var inputState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(e.Key);

        // Read modifier states with InputKeyboardSource (WinUI 3 API).
        bool IsDown(VirtualKey vk) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(vk) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        if (IsDown(VirtualKey.LeftControl))  mods += "LeftCtrl+";
        if (IsDown(VirtualKey.RightControl)) mods += "RightCtrl+";
        if (IsDown(VirtualKey.LeftShift))    mods += "LeftShift+";
        if (IsDown(VirtualKey.RightShift))   mods += "RightShift+";
        if (IsDown(VirtualKey.LeftMenu))     mods += "LeftAlt+";
        if (IsDown(VirtualKey.RightMenu))    mods += "RightAlt+";
        if (IsDown(VirtualKey.LeftWindows))  mods += "LeftWin+";
        if (IsDown(VirtualKey.RightWindows)) mods += "RightWin+";

        var keyName = KeyToName(e.Key);
        if (keyName is null) return;

        var chord = mods + keyName;
        try
        {
            HotkeyChord.Parse(chord);
            SetChord(chord, null);
            ChordRecorded?.Invoke(chord);
            _recording = false;
            e.Handled = true;
        }
        catch
        {
            SetChord("(invalid)", "Could not parse that combination.");
        }
    }

    private static string? KeyToName(VirtualKey k) => k switch
    {
        VirtualKey.Space  => "Space",
        VirtualKey.Tab    => "Tab",
        VirtualKey.Enter  => "Enter",
        VirtualKey.Escape => "Esc",
        >= VirtualKey.A and <= VirtualKey.Z => k.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)k - (int)VirtualKey.Number0).ToString(),
        >= VirtualKey.F1 and <= VirtualKey.F12 => $"F{(int)k - (int)VirtualKey.F1 + 1}",
        _ => null,
    };
}
#endif
```

- [ ] **Step 3: Write `src/Winpepper.App/Views/RecordingPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Winpepper.App.Views.RecordingPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Winpepper.App.Views.Controls">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="16" MaxWidth="640">
            <TextBlock Text="Recording" Style="{ThemeResource TitleTextBlockStyle}" />
            <controls:HotkeyRecorderBox x:Name="HoldBox" Label="Hold to record" />
            <controls:HotkeyRecorderBox x:Name="ToggleBox" Label="Toggle to record" />
            <StackPanel Spacing="6">
                <TextBlock Text="Microphone" />
                <ComboBox x:Name="MicCombo" PlaceholderText="Select an input device" Width="400" />
                <ProgressBar x:Name="LevelMeter" Minimum="0" Maximum="1" Height="6" />
            </StackPanel>
            <ToggleSwitch x:Name="SoundsToggle" Header="Play start/stop sounds" />
            <ToggleSwitch x:Name="SpeakerFilterToggle" Header="Speaker filter (experimental)" />
            <ToggleSwitch x:Name="AutostartToggle" Header="Start with Windows" />
            <StackPanel Orientation="Horizontal" Spacing="8">
                <TextBox x:Name="TestBox" PlaceholderText="Test dictation lands here..." Width="380" />
                <Button Content="Focus" Click="OnFocusTestBox" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 4: Write `src/Winpepper.App/Views/RecordingPage.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Audio;

namespace Winpepper.App.Views;

public sealed partial class RecordingPage : Page
{
    private AppShell? _shell;
    private WasapiRecorder? _levelRecorder;

    public RecordingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (AppShell)e.Parameter;
        var vm = _shell.RecordingVm;

        HoldBox.SetChord(vm.HoldHotkey, vm.HoldHotkeyConflict);
        ToggleBox.SetChord(vm.ToggleHotkey, vm.ToggleHotkeyConflict);

        HoldBox.ChordRecorded   += chord => vm.HoldHotkey = chord;
        ToggleBox.ChordRecorded += chord => vm.ToggleHotkey = chord;
        vm.PropertyChanged += (_, _) =>
        {
            HoldBox.SetChord(vm.HoldHotkey, vm.HoldHotkeyConflict);
            ToggleBox.SetChord(vm.ToggleHotkey, vm.ToggleHotkeyConflict);
        };

        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);
        MicCombo.SelectedItem = devices.FirstOrDefault(d => d.Id == vm.MicDeviceId)
                                 ?? devices.FirstOrDefault(d => d.IsDefault);
        MicCombo.SelectionChanged += (_, _) =>
        {
            if (MicCombo.SelectedItem is CaptureDevice d) vm.MicDeviceId = d.Id;
            RestartLevelMeter(vm.MicDeviceId);
        };

        SoundsToggle.IsOn = vm.PlaySounds;
        SoundsToggle.Toggled += (_, _) => vm.PlaySounds = SoundsToggle.IsOn;
        SpeakerFilterToggle.IsOn = vm.SpeakerFilterEnabled;
        SpeakerFilterToggle.Toggled += (_, _) => vm.SpeakerFilterEnabled = SpeakerFilterToggle.IsOn;

        AutostartToggle.IsOn = _shell.Autostart.IsEnabled();
        AutostartToggle.Toggled += (_, _) =>
        {
            if (AutostartToggle.IsOn)
            {
                // Spec §7.7 mandates the literal value
                //   "C:\Program Files\Winpepper\winpepper.exe" --tray
                // because the MSI installs to Program Files. AppContext.BaseDirectory
                // is correct only when running from the install location; in dev / on
                // the VM you can override via the WINPEPPER_AUTOSTART_EXE env var.
                var exe = Environment.GetEnvironmentVariable("WINPEPPER_AUTOSTART_EXE");
                if (string.IsNullOrEmpty(exe))
                    exe = @"C:\Program Files\Winpepper\winpepper.exe";
                _shell.Autostart.Enable(exe, "--tray");
            }
            else _shell.Autostart.Disable();
            _shell.SettingsWriter.Queue(s => s with { AutostartEnabled = AutostartToggle.IsOn });
        };

        RestartLevelMeter(vm.MicDeviceId);
    }

    private void RestartLevelMeter(string deviceId)
    {
        _levelRecorder?.Dispose();
        _levelRecorder = new WasapiRecorder(string.IsNullOrEmpty(deviceId) ? null : deviceId);
        _levelRecorder.FramesAvailable += frames =>
        {
            float peak = 0;
            for (var i = 0; i < frames.Length; i++) { var v = Math.Abs(frames.Span[i]); if (v > peak) peak = v; }
            DispatcherQueue.TryEnqueue(() => LevelMeter.Value = Math.Min(1.0, peak));
        };
        try { _levelRecorder.Start(); } catch { /* device unavailable; meter stays at zero */ }
    }

    private void OnFocusTestBox(object sender, RoutedEventArgs e) => TestBox.Focus(FocusState.Programmatic);

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _levelRecorder?.Dispose();
    }
}
#endif
```

- [ ] **Step 5: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: still fails until `CleanupPage`/`CorrectionsPage`/`OnboardingPage` exist. Continue.

- [ ] **Step 6: Commit (after Task 25)**

```bash
git add src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs src/Winpepper.App/Views/Controls
git commit -m "feat(app): RecordingPage with hotkey recorder, mic picker, level meter"
```

---

## Task 22: CleanupPage

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/CleanupPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/CleanupPage.xaml.cs`

§7.3 Cleanup tab. Binds `CleanupSettingsViewModel`. RichEditBox uses monospaced font.

- [ ] **Step 1: Write `src/Winpepper.App/Views/CleanupPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Winpepper.App.Views.CleanupPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="16" MaxWidth="700">
            <TextBlock Text="Cleanup" Style="{ThemeResource TitleTextBlockStyle}" />
            <ToggleSwitch x:Name="EnabledSwitch" Header="Enable cleanup LLM" />
            <ToggleSwitch x:Name="WindowCtxSwitch" Header="Use window context (UIA + OCR)" />
            <StackPanel Spacing="4">
                <TextBlock Text="Prompt profile" />
                <ComboBox x:Name="ProfileCombo" SelectedValuePath="Tag" Width="280">
                    <ComboBoxItem Content="Ordinary Dictation" Tag="Ordinary" />
                    <ComboBoxItem Content="Literal Dictation"  Tag="Literal" />
                    <ComboBoxItem Content="Custom"              Tag="Custom" />
                </ComboBox>
            </StackPanel>
            <StackPanel Spacing="4">
                <TextBlock Text="Custom prompt" />
                <RichEditBox x:Name="CustomPromptBox" Height="220" FontFamily="Cascadia Mono,Consolas,monospace" />
            </StackPanel>
            <StackPanel Spacing="4">
                <TextBlock x:Name="MaxTokLabel" Text="Max new tokens: 512" />
                <Slider x:Name="MaxTokSlider" Minimum="64" Maximum="4096" StepFrequency="32" Width="380" />
            </StackPanel>
            <StackPanel Spacing="4">
                <TextBlock x:Name="TimeoutLabel" Text="Timeout: 15000 ms" />
                <Slider x:Name="TimeoutSlider" Minimum="2000" Maximum="60000" StepFrequency="500" Width="380" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/CleanupPage.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;

namespace Winpepper.App.Views;

public sealed partial class CleanupPage : Page
{
    public CleanupPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        var vm = shell.CleanupVm;

        EnabledSwitch.IsOn = vm.Enabled;
        EnabledSwitch.Toggled += (_, _) => vm.Enabled = EnabledSwitch.IsOn;

        WindowCtxSwitch.IsOn = vm.WindowContextEnabled;
        WindowCtxSwitch.Toggled += (_, _) => vm.WindowContextEnabled = WindowCtxSwitch.IsOn;

        ProfileCombo.SelectedValue = vm.Profile;
        ProfileCombo.SelectionChanged += (_, _) =>
        {
            if (ProfileCombo.SelectedValue is string s) vm.Profile = s;
            CustomPromptBox.IsReadOnly = !vm.CustomPromptEditable;
        };
        CustomPromptBox.IsReadOnly = !vm.CustomPromptEditable;
        CustomPromptBox.Document.SetText(TextSetOptions.None, vm.CustomPrompt);
        CustomPromptBox.TextChanged += (_, _) =>
        {
            CustomPromptBox.Document.GetText(TextGetOptions.None, out var text);
            vm.CustomPrompt = text;
        };

        MaxTokSlider.Value = vm.MaxNewTokens;
        MaxTokLabel.Text = $"Max new tokens: {vm.MaxNewTokens}";
        MaxTokSlider.ValueChanged += (_, _) =>
        {
            vm.MaxNewTokens = (int)MaxTokSlider.Value;
            MaxTokLabel.Text = $"Max new tokens: {vm.MaxNewTokens}";
        };

        TimeoutSlider.Value = vm.TimeoutMs;
        TimeoutLabel.Text = $"Timeout: {vm.TimeoutMs} ms";
        TimeoutSlider.ValueChanged += (_, _) =>
        {
            vm.TimeoutMs = (int)TimeoutSlider.Value;
            TimeoutLabel.Text = $"Timeout: {vm.TimeoutMs} ms";
        };
    }
}
#endif
```

- [ ] **Step 3: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: still fails on `CorrectionsPage`/`OnboardingPage`. Continue.

- [ ] **Step 4: Commit (after Task 25)**

```bash
git add src/Winpepper.App/Views/CleanupPage.xaml src/Winpepper.App/Views/CleanupPage.xaml.cs
git commit -m "feat(app): CleanupPage bound to CleanupSettingsViewModel"
```

---

## Task 23: CorrectionsPage

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/CorrectionsPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/CorrectionsPage.xaml.cs`

§7.3 Corrections. Two `ListView`s with inline TextBox + a Remove button each. New-row inputs at the bottom of each list. Validation errors render in red below the input.

- [ ] **Step 1: Write `src/Winpepper.App/Views/CorrectionsPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Winpepper.App.Views.CorrectionsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <ScrollViewer>
        <StackPanel Padding="24" Spacing="20" MaxWidth="720">
            <TextBlock Text="Corrections" Style="{ThemeResource TitleTextBlockStyle}" />

            <StackPanel Spacing="6">
                <TextBlock Text="Preferred transcriptions" Style="{ThemeResource SubtitleTextBlockStyle}" />
                <ListView x:Name="PreferredList">
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <Grid ColumnDefinitions="*,Auto" Padding="4">
                                <TextBlock Text="{Binding Text}" VerticalAlignment="Center" />
                                <Button Grid.Column="1" Content="Remove" Click="OnRemovePreferred" Tag="{Binding}" />
                            </Grid>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
                <Grid ColumnDefinitions="*,Auto" ColumnSpacing="8">
                    <TextBox x:Name="NewPreferredBox" PlaceholderText="Add a preferred transcription" />
                    <Button Grid.Column="1" Content="Add" Click="OnAddPreferred" />
                </Grid>
                <TextBlock x:Name="PreferredError" Foreground="OrangeRed" />
            </StackPanel>

            <StackPanel Spacing="6">
                <TextBlock Text="Misheard replacements" Style="{ThemeResource SubtitleTextBlockStyle}" />
                <ListView x:Name="ReplacementsList">
                    <ListView.ItemTemplate>
                        <DataTemplate>
                            <Grid ColumnDefinitions="*,*,Auto" Padding="4" ColumnSpacing="8">
                                <TextBlock Text="{Binding Wrong}" VerticalAlignment="Center" />
                                <TextBlock Grid.Column="1" Text="{Binding Right}" VerticalAlignment="Center" />
                                <Button Grid.Column="2" Content="Remove" Click="OnRemoveReplacement" Tag="{Binding}" />
                            </Grid>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
                <Grid ColumnDefinitions="*,*,Auto" ColumnSpacing="8">
                    <TextBox x:Name="NewWrongBox"  PlaceholderText="wrong (heard)" />
                    <TextBox x:Name="NewRightBox"  Grid.Column="1" PlaceholderText="right (correct)" />
                    <Button x:Name="AddReplacementButton" Grid.Column="2" Content="Add" Click="OnAddReplacement" />
                </Grid>
                <TextBlock x:Name="ReplacementsError" Foreground="OrangeRed" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/CorrectionsPage.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class CorrectionsPage : Page
{
    private CorrectionsViewModel? _vm;

    public CorrectionsPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _vm = ((AppShell)e.Parameter).CorrectionsVm;
        PreferredList.ItemsSource = _vm.Preferred;
        ReplacementsList.ItemsSource = _vm.Replacements;
    }

    private void OnAddPreferred(object sender, RoutedEventArgs e)
    {
        var text = NewPreferredBox.Text ?? "";
        var err = _vm!.AddPreferred(text);
        PreferredError.Text = err ?? "";
        if (err is null) NewPreferredBox.Text = "";
    }

    private void OnRemovePreferred(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PreferredEntry entry }) _vm!.RemovePreferred(entry);
    }

    private void OnAddReplacement(object sender, RoutedEventArgs e)
    {
        var w = NewWrongBox.Text ?? ""; var r = NewRightBox.Text ?? "";
        var err = _vm!.AddReplacement(w, r);
        ReplacementsError.Text = err ?? "";
        if (err is null) { NewWrongBox.Text = ""; NewRightBox.Text = ""; }
    }

    private void OnRemoveReplacement(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReplacementEntry entry }) _vm!.RemoveReplacement(entry);
    }
}
#endif
```

- [ ] **Step 3: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: still fails on `OnboardingPage` only. Continue.

- [ ] **Step 4: Commit (after Task 25)**

```bash
git add src/Winpepper.App/Views/CorrectionsPage.xaml src/Winpepper.App/Views/CorrectionsPage.xaml.cs
git commit -m "feat(app): CorrectionsPage with two ObservableCollection-backed lists"
```

---

## Task 24: Wire CleanupRunner + WindowContext into PipelineHost (Plan 2 hookup)

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`

Plan 2 publishes `Winpepper.Cleanup.CleanupRunner`, `Winpepper.Corrections.CorrectionStore`, and `Winpepper.Platform.WindowContext.WindowContextPrefetch`. This task replaces the raw-transcript-to-injection path with: transcript → cleanup runner (with optional window-context Task piped in) → injection.

The wiring mirrors what Plan 2 already did in `Winpepper.Cli.Pipeline` (Plan 2 lines 3725–3780). Specifically:
- `WindowContextPrefetch.StartAsync(hwnd, ct)` is kicked off on `HoldDown`, returning a `Task<WindowContextResult>`.
- On `HoldUp`, a `ContinueWith` extracts `.Text` from the result, producing a `Task<string?>` that we hand to `CleanupRunner.RunAsync` as the `windowContextTask` argument.
- The runner returns a `CleanupResult`; we consume `.CleanedText` for injection.

If Plan 2 isn't merged yet at the time this task runs, leave the additions guarded with `// PLAN2-TYPE` and ship the raw-transcript fallback. The smoke test in Task 26 covers both paths.

- [ ] **Step 1: Add fields and constructor parameter to `PipelineHost.cs`**

At the top of the class, alongside the existing fields, add:

```csharp
    private readonly Winpepper.Cleanup.CleanupRunner? _cleanup;        // PLAN2-TYPE
    private readonly Winpepper.Cleanup.CleanupOptions _cleanupOptions; // PLAN2-TYPE
    private readonly Winpepper.Corrections.CorrectionStore? _corrections; // PLAN2-TYPE
    private readonly Winpepper.Platform.WindowContext.WindowContextPrefetch? _windowContext; // PLAN2-TYPE
    private Task<Winpepper.Platform.WindowContext.WindowContextResult>? _ctxPrefetchTask;    // PLAN2-TYPE
```

Update the constructor to accept the three new optional dependencies plus the options bag, and assign them:

```csharp
    public PipelineHost(
        ILoggerFactory factory,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir,
        Winpepper.Cleanup.CleanupRunner? cleanup = null,                       // PLAN2-TYPE
        Winpepper.Corrections.CorrectionStore? corrections = null,             // PLAN2-TYPE
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null, // PLAN2-TYPE
        Winpepper.Cleanup.CleanupOptions? cleanupOptions = null)               // PLAN2-TYPE
    {
        _log = factory.CreateLogger<PipelineHost>();
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
        _cleanupOptions = cleanupOptions ?? new Winpepper.Cleanup.CleanupOptions();
    }
```

- [ ] **Step 2: Kick off the window-context prefetch on `HoldDown` (and `Toggle` start)**

Inside `HotkeyEventKind.HoldDown`, after the recorder starts, fire the prefetch keyed to the current foreground window. Mirror the same call inside the `Toggle` start branch.

```csharp
            case HotkeyEventKind.HoldDown:
                if (_engine.State != SessionState.Idle) return;
                _engine.Apply(SessionEvent.StartRequested);
                _sounds.PlayStart();
                _recorder = new WasapiRecorder();
                _recorder.Start();

                // PLAN2-TYPE — start window-context prefetch in parallel with audio capture.
                _ctxPrefetchTask = null;
                if (_windowContext is not null && _cleanupOptions.WindowContextEnabled)
                {
                    var hwnd = Winpepper.Platform.WindowContext.ForegroundWindow.Handle();
                    _ctxPrefetchTask = _windowContext.StartAsync(hwnd, ct);
                }
                break;
```

(Mirror the same prefetch kick-off inside the `Toggle` start branch when `_engine.State == SessionState.Idle`.)

- [ ] **Step 3: Replace the transcript→injection block in `HoldUp` (and `Toggle` stop)**

Replace this existing block:

```csharp
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _engine.Apply(SessionEvent.TranscriptReady);
                if (!string.IsNullOrWhiteSpace(transcript.Text)) _injector.TryInject(transcript.Text);
                _engine.Apply(SessionEvent.InjectionCompleted);
```

with:

```csharp
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                _engine.Apply(SessionEvent.TranscriptReady);

                string final = transcript.Text;
                if (!string.IsNullOrWhiteSpace(final) && _cleanup is not null)
                {
                    _vm.MarkCleaningUp();

                    // Plan 2's CleanupRunner.RunAsync expects a Task<string?>? for the
                    // window context. Adapt our Task<WindowContextResult> by projecting
                    // .Text out (or null on failure). This mirrors Plan 2 Cli/Pipeline.cs
                    // lines 3749-3751.
                    Task<string?>? ctxTextTask = null;
                    if (_ctxPrefetchTask is not null)
                    {
                        ctxTextTask = _ctxPrefetchTask.ContinueWith(
                            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                            ct,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    var corrections = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                    try
                    {
                        var result = await _cleanup.RunAsync(
                            rawTranscript: final,
                            corrections: corrections,
                            windowContextTask: ctxTextTask,
                            options: _cleanupOptions,
                            ct: ct);
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                    }
                }

                if (!string.IsNullOrWhiteSpace(final)) _injector.TryInject(final);
                _engine.Apply(SessionEvent.InjectionCompleted);
                _ctxPrefetchTask = null;
```

(Mirror the same block inside the `Toggle` stop branch when `_engine.State == SessionState.Recording`.)

- [ ] **Step 4: Update `AppShell.BootstrapAsync` to construct CorrectionStore, WindowContextPrefetch, and pass everything to PipelineHost**

Inside `AppShell.BootstrapAsync`, after `sounds` is created and before `var pipeline = new PipelineHost(...)`, replace the construction with:

```csharp
        // PLAN2-TYPE — Plan 2 owns these types; constructing them here so Plan 3's
        // pipeline can invoke real cleanup + window context. Each one is optional —
        // if the model or registry isn't present yet, we fall back to raw transcript.
        Winpepper.Cleanup.CleanupRunner? cleanup = null;
        Winpepper.Corrections.CorrectionStore? correctionStore = null;
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null;

        try
        {
            correctionStore = new Winpepper.Corrections.CorrectionStore(AppPaths.CorrectionsJson);
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "CorrectionStore unavailable; cleanup will run with empty corrections.");
        }

        try
        {
            // Plan 2's LlamaCleanupBackend (line 2141) is constructed with the path to
            // the .gguf file (not the directory). The cleanup model lives at
            // <Root>/models/cleanup/<name>.gguf. We pick the first .gguf in that dir.
            var cleanupModelDir = Path.Combine(AppPaths.Root, "models", "cleanup");
            var modelFile = Directory.Exists(cleanupModelDir)
                ? Directory.EnumerateFiles(cleanupModelDir, "*.gguf", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (modelFile is not null)
            {
                var backend = new Winpepper.Cleanup.LlamaCleanupBackend(modelFile,
                    factory.CreateLogger<Winpepper.Cleanup.LlamaCleanupBackend>());
                cleanup = new Winpepper.Cleanup.CleanupRunner(backend,
                    factory.CreateLogger<Winpepper.Cleanup.CleanupRunner>());
            }
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "Cleanup runner unavailable; falling back to raw transcripts.");
        }

        try
        {
            // CreateWindows is Plan 2's production factory (line 3480 of plan 2);
            // UiaTreeReader and OcrFallback both take a logger.
            windowContext = Winpepper.Platform.WindowContext.WindowContextPrefetch.CreateWindows(
                new Winpepper.Platform.WindowContext.UiaTreeReader(
                    factory.CreateLogger<Winpepper.Platform.WindowContext.UiaTreeReader>()),
                new Winpepper.Platform.WindowContext.OcrFallback(
                    factory.CreateLogger<Winpepper.Platform.WindowContext.OcrFallback>()),
                factory.CreateLogger<Winpepper.Platform.WindowContext.WindowContextPrefetch>());
        }
        catch (Exception ex)
        {
            factory.CreateLogger("Winpepper.App").LogWarning(ex,
                "WindowContextPrefetch unavailable; cleanup will run without window context.");
        }

        // Build CleanupOptions from current cleanup settings (Plan 3 keeps these in
        // the CleanupSettingsViewModel; here we read once at boot and re-read in
        // Plan 4's settings-reactive wiring).
        var cleanupOptions = new Winpepper.Cleanup.CleanupOptions
        {
            Profile = ParseProfile(cleanupContract.Profile),
            CustomBasePrompt = cleanupContract.CustomPrompt,
            Timeout = TimeSpan.FromMilliseconds(cleanupContract.TimeoutMs),
            WindowContextEnabled = cleanupContract.WindowContextEnabled,
            MaxNewTokensCap = cleanupContract.MaxNewTokens,
        };
```

Add this helper at file scope inside `AppShell`:

```csharp
    private static Winpepper.Cleanup.CleanupProfile ParseProfile(string s) => s switch
    {
        "Ordinary" => Winpepper.Cleanup.CleanupProfile.Ordinary,
        "Literal"  => Winpepper.Cleanup.CleanupProfile.Literal,
        "Custom"   => Winpepper.Cleanup.CleanupProfile.Custom,
        _          => Winpepper.Cleanup.CleanupProfile.Ordinary,
    };
```

Replace the `PipelineHost` construction with:

```csharp
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         cleanup, correctionStore, windowContext, cleanupOptions);
```

- [ ] **Step 5: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: builds when Plan 2 is merged. If Plan 2 named `LlamaCleanupBackend.Create`, `UiaTreeReader`, or `OcrFallback` differently, adjust the three factory calls accordingly — the rest of the wiring binds to the public signatures called out in the contract preamble. If `Winpepper.Cleanup`/`Winpepper.Corrections`/`WindowContextPrefetch` aren't available yet, comment the block out with `// PLAN2-TYPE wait`, set `cleanup = null` / `correctionStore = null` / `windowContext = null`, leave a TODO, and re-run — the raw-transcript fallback in PipelineHost still works.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting
git commit -m "feat(app): wire cleanup runner + window-context prefetch into PipelineHost"
```

---

## Task 25: OnboardingPage

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/OnboardingPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/OnboardingPage.xaml.cs`

§7.4. Four-step flow driven by `OnboardingViewModel`. Page uses a `Frame`-less stepper UI: header pills + a Grid showing the current step's content.

- [ ] **Step 1: Write `src/Winpepper.App/Views/OnboardingPage.xaml`**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Page
    x:Class="Winpepper.App.Views.OnboardingPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:controls="using:Winpepper.App.Views.Controls">
    <Grid Padding="24" RowDefinitions="Auto,*,Auto" RowSpacing="16">
        <StackPanel Orientation="Horizontal" Spacing="8">
            <Border x:Name="StepDot1" Background="#FF2D7DD2" Width="10" Height="10" CornerRadius="5" />
            <Border x:Name="StepDot2" Background="#FFAAAAAA" Width="10" Height="10" CornerRadius="5" />
            <Border x:Name="StepDot3" Background="#FFAAAAAA" Width="10" Height="10" CornerRadius="5" />
            <Border x:Name="StepDot4" Background="#FFAAAAAA" Width="10" Height="10" CornerRadius="5" />
        </StackPanel>

        <Grid Grid.Row="1">
            <StackPanel x:Name="PickMicPanel"      Visibility="Visible"  Spacing="12">
                <TextBlock Text="Pick your microphone" Style="{ThemeResource TitleTextBlockStyle}" />
                <ComboBox x:Name="MicCombo" Width="380" PlaceholderText="Select an input device" />
                <ProgressBar x:Name="LevelMeter" Minimum="0" Maximum="1" Height="6" Width="380" HorizontalAlignment="Left" />
            </StackPanel>
            <StackPanel x:Name="HotkeyPanel"       Visibility="Collapsed" Spacing="12">
                <TextBlock Text="Record your hotkeys" Style="{ThemeResource TitleTextBlockStyle}" />
                <controls:HotkeyRecorderBox x:Name="HoldBox" Label="Hold to record" />
                <controls:HotkeyRecorderBox x:Name="ToggleBox" Label="Toggle to record" />
            </StackPanel>
            <StackPanel x:Name="DownloadPanel"     Visibility="Collapsed" Spacing="12">
                <TextBlock Text="Download models" Style="{ThemeResource TitleTextBlockStyle}" />
                <TextBlock Text="This will download the ASR model (~600 MB) and cleanup model. You can skip and do this later from the Models tab." TextWrapping="Wrap" Width="500" />
                <ProgressBar x:Name="DownloadProgress" IsIndeterminate="True" Visibility="Collapsed" />
            </StackPanel>
            <StackPanel x:Name="TestPanel"         Visibility="Collapsed" Spacing="12">
                <TextBlock Text="Try it" Style="{ThemeResource TitleTextBlockStyle}" />
                <TextBlock Text="Hold your hotkey and say a sentence into the box below." TextWrapping="Wrap" Width="500" />
                <TextBox x:Name="TestBox" PlaceholderText="Dictate into me..." Width="500" Height="100" AcceptsReturn="True" />
                <CheckBox x:Name="TestDoneCheck" Content="That worked." />
            </StackPanel>
            <StackPanel x:Name="DonePanel"         Visibility="Collapsed" Spacing="12">
                <TextBlock Text="You are set." Style="{ThemeResource TitleTextBlockStyle}" />
                <TextBlock Text="Winpepper is now running in the tray. Click the icon or press your hotkey to start dictating." TextWrapping="Wrap" />
            </StackPanel>
        </Grid>

        <StackPanel Grid.Row="2" Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
            <Button x:Name="SkipButton" Content="Skip" Click="OnSkip" Visibility="Collapsed" />
            <Button x:Name="AdvanceButton" Content="Next" Click="OnAdvance" />
        </StackPanel>
    </Grid>
</Page>
```

- [ ] **Step 2: Write `src/Winpepper.App/Views/OnboardingPage.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Audio;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class OnboardingPage : Page
{
    private AppShell? _shell;
    private OnboardingViewModel? _vm;
    private WasapiRecorder? _meterRecorder;

    public OnboardingPage() { InitializeComponent(); }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _shell = (AppShell)e.Parameter;
        // The stub returns immediately for Plan 3. Plan 4 swaps in the real downloader.
        _vm = new OnboardingViewModel(_shell.SettingsWriter, () => Task.CompletedTask,
                                       new Winpepper.Platform.Hotkeys.PlatformHotkeyValidator());

        var devices = DeviceEnumerator.List();
        MicCombo.ItemsSource = devices;
        MicCombo.DisplayMemberPath = nameof(CaptureDevice.FriendlyName);
        MicCombo.SelectionChanged += (_, _) =>
        {
            if (MicCombo.SelectedItem is CaptureDevice d)
            {
                _vm.SelectedMicDeviceId = d.Id;
                RestartLevelMeter(d.Id);
            }
            RefreshButtons();
        };

        HoldBox.ChordRecorded   += chord => { _vm.HoldHotkey = chord; HoldBox.SetChord(chord, _vm.HoldHotkeyError);   RefreshButtons(); };
        ToggleBox.ChordRecorded += chord => { _vm.ToggleHotkey = chord; ToggleBox.SetChord(chord, _vm.ToggleHotkeyError); RefreshButtons(); };
        HoldBox.SetChord(_vm.HoldHotkey, _vm.HoldHotkeyError);
        ToggleBox.SetChord(_vm.ToggleHotkey, _vm.ToggleHotkeyError);

        TestDoneCheck.Checked   += (_, _) => { _vm.TestDictationDone = true; RefreshButtons(); };
        TestDoneCheck.Unchecked += (_, _) => { _vm.TestDictationDone = false; RefreshButtons(); };

        _vm.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RenderStep);
        RenderStep();
    }

    private async void OnAdvance(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        AdvanceButton.IsEnabled = false;
        if (_vm.Step == OnboardingStep.DownloadModels)
        {
            DownloadProgress.Visibility = Visibility.Visible;
        }
        try { await _vm.AdvanceAsync(); }
        finally { AdvanceButton.IsEnabled = true; DownloadProgress.Visibility = Visibility.Collapsed; }
        if (_vm.Step == OnboardingStep.Done)
        {
            // Onboarding complete; the user can stay on the page or switch tabs.
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) { _vm?.Skip(); }

    private void RenderStep()
    {
        if (_vm is null) return;
        void Show(UIElement el, OnboardingStep s) => el.Visibility = _vm.Step == s ? Visibility.Visible : Visibility.Collapsed;
        Show(PickMicPanel,   OnboardingStep.PickMic);
        Show(HotkeyPanel,    OnboardingStep.PickHotkeys);
        Show(DownloadPanel,  OnboardingStep.DownloadModels);
        Show(TestPanel,      OnboardingStep.TestDictation);
        Show(DonePanel,      OnboardingStep.Done);

        Border Dot(int i) => i switch { 1 => StepDot1, 2 => StepDot2, 3 => StepDot3, _ => StepDot4 };
        for (var i = 1; i <= 4; i++)
            Dot(i).Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                ((int)_vm.Step) >= (i - 1) ? Microsoft.UI.Colors.SteelBlue : Microsoft.UI.Colors.Gray);

        RefreshButtons();
    }

    private void RefreshButtons()
    {
        if (_vm is null) return;
        AdvanceButton.Content = _vm.Step switch
        {
            OnboardingStep.TestDictation => "Finish",
            OnboardingStep.DownloadModels => "Download",
            _ => "Next",
        };
        AdvanceButton.IsEnabled = _vm.CanAdvance;
        SkipButton.Visibility = _vm.CanSkip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestartLevelMeter(string deviceId)
    {
        _meterRecorder?.Dispose();
        _meterRecorder = new WasapiRecorder(string.IsNullOrEmpty(deviceId) ? null : deviceId);
        _meterRecorder.FramesAvailable += frames =>
        {
            float peak = 0;
            for (var i = 0; i < frames.Length; i++) { var v = Math.Abs(frames.Span[i]); if (v > peak) peak = v; }
            DispatcherQueue.TryEnqueue(() => LevelMeter.Value = Math.Min(1.0, peak));
        };
        try { _meterRecorder.Start(); } catch { /* mic unavailable in this VM */ }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) { _meterRecorder?.Dispose(); }
}
#endif
```

- [ ] **Step 3: Build on VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build succeeds for the first time end-to-end.

- [ ] **Step 4: Commit (Tasks 16–23, 25 together since they all chain on each other's symbols)**

```bash
git add src/Winpepper.App/App.xaml src/Winpepper.App/App.xaml.cs src/Winpepper.App/Program.cs \
        src/Winpepper.App/Hosting src/Winpepper.App/Tray src/Winpepper.App/Views src/Winpepper.App/Audio src/Winpepper.App/Threading
git commit -m "feat(app): WinUI 3 shell, tray, status pill, recording/cleanup/corrections/onboarding pages"
```

(If Tasks 16–23 were each committed individually earlier without the build succeeding, this final commit only picks up `OnboardingPage` plus any whitespace fixups.)

---

## Task 26: Retire Winpepper.Cli

**Files:**
- Delete: `/home/jesse/git/winpepper/src/Winpepper.Cli/Program.cs`
- Delete: `/home/jesse/git/winpepper/src/Winpepper.Cli/Pipeline.cs`
- Delete: `/home/jesse/git/winpepper/src/Winpepper.Cli/Winpepper.Cli.csproj`
- Modify: `/home/jesse/git/winpepper/winpepper.sln` — remove the `Winpepper.Cli` project entry.
- Modify: `/home/jesse/git/winpepper/docs/manual-test.md` — replace the Plan 1 walking-skeleton smoke section with a pointer to the new Plan 3 procedure (Task 27).

The walking-skeleton CLI was always temporary scaffolding. After Plan 3, `Winpepper.App` is the only entry point. Its `Program.cs` accepts `--tray` (start hidden — used by autostart) but no `--cli` flag is needed; the pipeline is identical to what the CLI ran.

- [ ] **Step 1: Remove the project from the solution**

```bash
cd /home/jesse/git/winpepper
dotnet sln remove src/Winpepper.Cli/Winpepper.Cli.csproj
```

- [ ] **Step 2: Delete the project directory**

```bash
rm -rf src/Winpepper.Cli
```

- [ ] **Step 3: Add a `--tray` argument to `Winpepper.App.Program`** — open `src/Winpepper.App/Program.cs` and prepend inside `Main` before `Application.Start`:

```csharp
        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("WINPEPPER_START_HIDDEN", startHidden ? "1" : "0");
```

Then in `AppShell.StartAsync` (replace the `if (!Settings.OnboardingCompleted)` block):

```csharp
        var startHidden = Environment.GetEnvironmentVariable("WINPEPPER_START_HIDDEN") == "1";
        if (!Settings.OnboardingCompleted) ShowMain(navigateToOnboarding: true);
        else if (!startHidden) ShowMain(navigateToOnboarding: false);
        // else: stay tray-only.
```

- [ ] **Step 4: Update `docs/manual-test.md`** — replace the `## Plan 1 walking-skeleton smoke (real Windows machine)` section title with `## Plan 1 (retired): see Plan 3 smoke below.` and add a one-line note that the walking-skeleton CLI is retired post-Plan-3.

- [ ] **Step 5: Build everything on Linux to confirm nothing else referenced the CLI**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
for proj in src/Winpepper.Core src/Winpepper.Audio src/Winpepper.Asr src/Winpepper.Platform; do
  dotnet build "$proj"
done
```

Expected: every project builds.

- [ ] **Step 6: Build the app on the VM**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git rm -r src/Winpepper.Cli
git add winpepper.sln src/Winpepper.App/Program.cs src/Winpepper.App/Hosting/AppShell.cs docs/manual-test.md
git commit -m "refactor: retire Winpepper.Cli; Winpepper.App is the entry point"
```

---

## Task 27: Manual smoke test on the VM

**Files:**
- Modify: `/home/jesse/git/winpepper/docs/manual-test.md` — add a Plan 3 smoke section.

The VM has no real desktop and no real mic, but the audio-passthrough setup from Plan 1 (Plan 1 Task 16 / `scripts/say.sh`) drives `WasapiCapture` with synthetic speech. For Plan 3 we verify the WinUI 3 packaged app starts, the tray icon appears, the main window navigates, onboarding can be completed, and a synthetic dictation lands in `TestBox`.

- [ ] **Step 1: Append the Plan 3 smoke section to `docs/manual-test.md`**

```markdown
## Plan 3 — WinUI 3 shell smoke (audio-passthrough VM)

1. Sync: `./scripts/sync-to-vm.sh`
2. Build: `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"`
3. Confirm models are present: `./scripts/winrun "Test-Path C:\Users\user\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3\encoder.onnx"` should be `True`.
4. Launch the app on the VM in a foreground RDP session (the VM ships with an autologon user session; connect via `rdesktop localhost:3389` or use the Visual Studio remote-debug pipeline):
   ```powershell
   cd C:\winpepper
   dotnet run --project src/Winpepper.App -c Debug
   ```
5. The tray icon should appear in the system tray; right-click → menu shows "Ready", Settings, Diagnostics (greyed), Pause, Quit, Winpepper v0.3.0.
6. On first launch, the main window opens to **Onboarding**. Click through steps:
   - Pick a mic from the dropdown — confirm the level meter twitches when you run `./scripts/say.sh "hello"` from the Linux host.
   - Record hotkeys: hold a chord while focused on the `HoldBox` (the visible chord text should update). If you record `Ctrl+C`, the warning row should appear.
   - Download models: click Skip. Step advances.
   - Test dictation: tick "That worked." Click Finish.
7. Window now navigates to Recording tab. Toggle "Play start/stop sounds" — confirm it persists (kill and relaunch — the toggle remembers its position).
8. Navigate to Cleanup tab: pick Custom profile, edit the prompt, change the Max-tokens slider. Restart — values persist.
9. Navigate to Corrections tab: add a preferred ("ChatGPT"), then add a duplicate — see error. Add a replacement ("chat gbt" → "ChatGPT"). Reload — entries persist.
10. Hold the dictation hotkey while focused on `TestBox`, run `./scripts/say.sh "hello world"`. Release. Expected:
    - Status pill appears bottom-center, red dot, "Recording..."
    - Pill transitions to "Transcribing...", "Inserting..."
    - `TestBox` contains text (likely "hello world" or similar).
    - Pill auto-hides 600 ms after `SessionStage.Idle`.
11. Quit from the tray — process exits cleanly.

**Acceptance bar:** every step lands without exceptions in the log (`%LOCALAPPDATA%\winpepper\logs\winpepper-<date>.log`). The tray icon and status pill appear. Onboarding finishes and `settings.json` shows `"onboardingCompleted": true`.
```

- [ ] **Step 2: Run the smoke procedure**

Execute steps 1–11 from the doc. If any step fails, capture the log and fix the underlying defect before continuing.

- [ ] **Step 3: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs: Plan 3 WinUI 3 shell smoke procedure"
```

---

## Self-review checklist (for the writer)

After completing all tasks, verify:

- [ ] **Spec coverage:**
  - §7.1 Tray → Tasks 17, 18.
  - §7.2 Status pill → Task 19 (§13.3 ordering documented inline).
  - §7.3 Main window (Recording, Cleanup, Corrections) → Tasks 20, 21, 22, 23.
  - §7.4 Onboarding → Tasks 14 (view model), 25 (page).
  - §7.5 Settings persistence (reactive + debounced) → Tasks 3, 5, 11, 12, 13.
  - §7.6 Sound effects → Task 10.
  - §7.7 Autostart → Tasks 8, 21. The Task 21 toggle writes the canonical install path `"C:\Program Files\Winpepper\winpepper.exe" --tray` (matching the Task 8 unit test on line ~872 and spec §7.7). A `WINPEPPER_AUTOSTART_EXE` env var overrides the path for dev / VM runs.
  - Retire `Winpepper.Cli` → Task 26.
  - WinAppSDK on VM → Task 1.
- [ ] **No placeholders.** Every step has concrete code, exact commands, expected output. Plan-2 type bindings are explicitly named and have a fallback path.
- [ ] **Type consistency:** view models (`SessionViewModel`, `RecordingSettingsViewModel`, `CleanupSettingsViewModel`, `CorrectionsViewModel`, `OnboardingViewModel`) keep the same property names across tasks (`HoldHotkey`, `ToggleHotkey`, `MicDeviceId`, `PlaySounds`, `SpeakerFilterEnabled`, `Enabled`, `Profile`, `MaxNewTokens`, `TimeoutMs`, `Preferred`, `Replacements`, `Step`, `CanAdvance`, etc.).
- [ ] **No XAML in unit tests:** every view-model test ships under `tests/Winpepper.Core.Tests` and tagged `[Trait("Layer","ViewModel")]`. UI shell is exercised via the Task 27 manual smoke.
- [ ] **Hook-thread invariant preserved:** `PipelineHost.HandleHotkey` is still the only path that calls `_injector.TryInject`, on the channel-reader thread driven by the hook.
- [ ] **Single-instance guard:** `Program.Main` redirects activation on duplicate launch (Task 16).

## What Plan 3 does NOT cover (intentionally — see follow-on plans)

- History tab + History detail (Lab) → Plan 4.
- Models tab + real downloader → Plan 4.
- Diagnostics tab + crash dumps → Plan 5.
- Post-paste learning → Plan 5.
- WiX MSI + signing → Plan 6.
- App icon artwork (the placeholder ICO ships through Plan 5).

## Handoff

When all tasks are committed and the Task 27 smoke passes: tell the user the WinUI 3 shell is alive on the VM, the CLI is retired, and onboarding/Settings/tray/status-pill all work. Then start Plan 4 (History + Lab + Models tabs).
