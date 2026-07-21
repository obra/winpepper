# Harden Warm-Microphone Capture Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the six critical and three important defects the 6-lens council
found in Winpepper's always-on warm-microphone capture — a per-callback
resampler leak, undetectable silent-capture states, silent failure paths, a
dispose race on device rebuild, a capture-object leak on init failure, a stale
pre-roll ring across rebuilds, fault/device-change recovery, `Dispose` hygiene,
and the missing always-on-mic disclosure.

**Architecture:** All concurrency-critical and signal-bearing logic is lifted
out of the NAudio-specific (`#if WINDOWS`) code into pure-managed,
Linux-testable classes in `Winpepper.Audio`: an `AudioEnergy` silence detector,
a `WarmCaptureCoordinator` that owns the epoch/lock lifecycle discipline, ring
invalidation, and fault recovery behind an injected `ICaptureSource` seam, and a
`Clear()`-able `WarmCaptureBuffer`. The NAudio layer becomes a thin
`WasapiCaptureSource` adapter (hoisted resampler, dispose-on-throw, safe
`OnData`/`Dispose`) that the coordinator drives. WinUI wiring in
`Winpepper.App` (`#if WINDOWS`) stays thin and is verified in the Windows Smoke
Test Checklist.

**Tech Stack:** C# / .NET 9 (multi-targeted `net9.0` + `net9.0-windows10.0.19041.0`;
we build/test the `net9.0` target on Linux), NAudio (Windows-only capture),
`Microsoft.Extensions.Logging.Abstractions`, xUnit v3, Shouldly assertions.

## Global Constraints

- **SDK:** .NET SDK satisfying `global.json` — `sdk 9.0.100`,
  `rollForward: latestFeature`. `dotnet` is **not** on PATH in this worktree;
  Task 0 provisions it locally into `./.dotnet/` (already gitignored — the
  `.gitignore` contains `/.dotnet/`).
- **Network required** for Task 0's provisioning and the first `dotnet build`
  (cold NuGet cache; restore evaluates the `net9.0-windows10.0.19041.0` TFM of
  multi-targeted projects even when building `-f net9.0`). All packages are on
  nuget.org / dot.net.
- **Test runner:** the VSTest host (`dotnet test`) **crashes on this machine**.
  Pure-managed tests MUST run via the xUnit v3 **in-process runner**:
  `dotnet exec <TestAssembly>.dll`. Exclude Windows-only tests with
  `-notrait "Platform=Windows"`. Target a single test with
  `-method "<Namespace>.<Class>.<Method>"`.
- **Test TFM:** build/run the `net9.0` target only (never `net9.0-windows...`).
  Always pass `-p:EnableWindowsTargeting=true` so multi-targeted project
  references restore on Linux.
- **Every test step re-exports the SDK env** (a fresh implementer shell does
  not inherit Task 0's exports):
  ```bash
  export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
  ```
- **Do NOT commit** the provisioned `./.dotnet/` directory or any build output
  (`bin/`, `obj/` are already gitignored).
- **Out of scope — do NOT touch:** the keyboard hook
  (`src/Winpepper.Platform/Hotkeys/*`) and packaging (`packaging/`).
- **NAudio code stays thin and Windows-only.** Every capture file that touches
  NAudio is wrapped in `#if WINDOWS`. The concurrency, ring, energy, and fault
  logic lives in pure-managed classes and is unit-tested on Linux; the NAudio
  adapters and WinUI wiring are verified in the **Windows Smoke Test Checklist**
  at the end — they are NOT deferred or stubbed.
- **Silence threshold:** RMS `< 1e-4f` over the full session's mono-16k samples
  counts as "essentially zero energy" (verbatim design value used across all
  tasks).
- **Fault backoff:** `2` seconds between automatic rebuild attempts on repeated
  capture faults (verbatim design value).
- **Docs:** `README.md` is the only end-user markdown doc; this plan under
  `docs/plans/` is a working/agent doc and is fine. Do not add other end-user
  docs.
- **Commits:** focused and atomic; use `feat:`/`fix:`/`test:`/`refactor:`/`docs:`
  prefixes and the standard Amplifier co-author trailer (shown in each commit
  step).

---

## Scope Check

This is a single subsystem (warm-microphone capture) with one cohesive set of
defects, delivered as one plan. It is decomposed into pure-managed library tasks
(Tasks 1–6, fully Linux-tested) and thin Windows adapter/wiring tasks
(Tasks 7–11, verified in the smoke checklist). There is **no** single
system-wide end-to-end test possible on Linux because the integration surface is
NAudio + WinUI (real WASAPI device, pipeline host); whole-system verification is
the **Windows Smoke Test Checklist** at the end, including the three
real-hardware proof scenarios.

---

## Council Finding → Task Coverage Map

| # | Council finding | Covered by |
|---|-----------------|-----------|
| 1 | CRITICAL resampler leak (per-callback `MediaFoundationResampler`), both files | Task 7 (WarmWasapiRecorder path via `WasapiCaptureSource`), Task 9 (`WasapiRecorder`) |
| 2 | CRITICAL silent "capturing silence" undetectable | Task 1 (`AudioEnergy` detector), Task 6 (optional warm-level health flag), Task 10 (session-end ErrorBus/toast wiring) |
| 3 | CRITICAL silent capture-failure paths | Task 5 (coordinator `CaptureFaulted`), Task 7/9 (adapter logging), Task 10 (ErrorBus routing) |
| 4 | CRITICAL dispose race on rebuild | Task 3 + Task 4 (coordinator epoch/lock + concurrency hammer), Task 7/8 (adapter safe OnData/Dispose, wiring) |
| 5 | CRITICAL capture-object leak when `StartRecording()` throws | Task 3 (coordinator dispose-on-throw of seam), Task 7 (adapter dispose-on-throw of COM object) |
| 6 | IMPORTANT pre-roll ring never invalidated across rebuild | Task 2 (`WarmCaptureBuffer.Clear`), Task 3 (coordinator clears ring on rebuild) |
| 7 | IMPORTANT device-change only at session start | Task 5 (rebuild-on-fault + backoff + faulted-start rebuild), Task 8 (session-start recheck via coordinator; TODO for full `IMMNotificationClient`) |
| 8 | IMPORTANT `WasapiRecorder.Dispose` + `TryStart` reentrancy + meter unhook | Task 9 (`WasapiRecorder.Dispose`), Task 10 (`TryStart` guard + `FramesAvailable`/`CaptureFaulted` unhook) |
| 9 | UX always-on-mic disclosure | Task 11 (onboarding sentence + settings caption/tooltip) |

There are **no** UNRESOLVED COVERAGE GAPS. Optional item ("also track sustained
zero-energy at the warm stream level") is implemented as a real, tested feature
in Task 6, not deferred.

**Residual concurrency surface (finding #4, Tasks 7/9).** The coordinator's
epoch/ring/fault race IS reproduced and proven on Linux by Task 4's hammer. But
one guarantee lives ONLY in the `#if WINDOWS` NAudio adapters and cannot be
exercised by the Linux hammer: mutual exclusion between `OnData` (the NAudio
capture thread) and `Dispose`/teardown (which JOINS that thread). Because
`WasapiCapture.Dispose()` joins the capture thread, the teardown must not run
under the same lock `OnData` holds — doing so is a lock↔join inversion that hangs
on rebuild. Tasks 7 and 9 therefore run their teardown OUTSIDE the callback lock,
and smoke **S5** is a repeated start/rebuild/unplug stress loop with hang
detection (not a single unplug) to actually exercise this window.

**Fault-detection scope (finding #7, Task 5).** `ICaptureSource.Stopped(non-null)`
→ rebuild covers HARD device faults (unplug / disable / reconfigure), which NAudio
surfaces as a non-null `RecordingStopped` exception (`AUDCLNT_E_DEVICE_INVALIDATED`).
It does **not** cover "soft" faults — OS mic-privacy toggle and some Bluetooth
hiccups — which can keep the stream alive delivering silent/zero frames with no
stop event. Those are caught instead by the Bug 2 session-end silence detector
(Task 10 `WarnIfSessionSilent`) and the Task 6 `SessionWasSilent` flag, which fire
on essentially-zero energy regardless of whether a fault event was raised. A full
no-data/heartbeat watchdog that would let soft faults ALSO trigger auto-rebuild is
a clean follow-up, not required for this plan.

---

## File Structure

**Pure-managed library (`Winpepper.Audio`, `net9.0`, Linux-tested)**
- Create: `src/Winpepper.Audio/AudioEnergy.cs` — RMS + session-silence predicate.
- Create: `src/Winpepper.Audio/ICaptureSource.cs` — capture seam abstraction.
- Create: `src/Winpepper.Audio/WarmCaptureCoordinator.cs` — epoch/lock lifecycle,
  ring invalidation, fault recovery, frame routing.
- Modify: `src/Winpepper.Audio/WarmCaptureBuffer.cs` — add `Clear()` and an
  optional sustained-silence health flag.
- Modify: `src/Winpepper.Audio/IWarmAudioRecorder.cs` — add `CaptureFaulted`.

**Tests (`Winpepper.Audio.Tests`, `net9.0`, Linux-tested)**
- Create: `tests/Winpepper.Audio.Tests/AudioEnergyTests.cs`
- Create: `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs` — test seam.
- Create: `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`
- Modify: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs`

**NAudio adapters (`Winpepper.Audio`, `#if WINDOWS`, smoke-verified)**
- Create: `src/Winpepper.Audio/WasapiCaptureSource.cs` — thin NAudio adapter.
- Rewrite: `src/Winpepper.Audio/WarmWasapiRecorder.cs` — delegates to coordinator.
- Modify: `src/Winpepper.Audio/WasapiRecorder.cs` — resampler hoist, dispose fix.

**WinUI wiring (`Winpepper.App`, `#if WINDOWS`, smoke-verified)**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — session-end silence
  signal, capture-fault routing, `TryStart` reentrancy guard, `Dispose` unhook.
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml` — mic disclosure sentence.
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` — resource-cost caption.

`AppShell.cs` and `ModelsPage.xaml.cs` are **not** modified: both call
`Pipeline.TryStart()` (AppShell.cs:321, ModelsPage.xaml.cs:90); the idempotency
guard lands inside `TryStart` (Task 10), so no call-site change is needed.

---

## Interfaces Defined By This Plan (single source of truth)

Later tasks rely on these exact signatures. They are defined once here so a
fresh implementer reading tasks out of order sees the contract:

```csharp
// AudioEnergy (Task 1)
public static double AudioEnergy.Rms(ReadOnlySpan<float> samples);
public const double AudioEnergy.SilenceRmsThreshold = 1e-4;
public static bool AudioEnergy.IsSessionSilent(
    ReadOnlySpan<float> samples, double rmsThreshold = 1e-4);

// WarmCaptureBuffer additions (Task 2, Task 6)
public void WarmCaptureBuffer.Clear();
public bool WarmCaptureBuffer.SessionWasSilent { get; }   // valid after StopSession

// ICaptureSource seam (Task 3)
public interface ICaptureSource : IDisposable
{
    string DeviceId { get; }
    event Action<ReadOnlyMemory<float>>? FramesAvailable; // mono 16 kHz
    event Action<Exception?>? Stopped;                     // non-null => fault
    void Start();
}

// WarmCaptureCoordinator (Tasks 3–5)
public sealed class WarmCaptureCoordinator : IDisposable
{
    public WarmCaptureCoordinator(
        WarmCaptureBuffer buffer,
        Func<ICaptureSource> sourceFactory,
        Func<DateTime>? clock = null,
        TimeSpan? faultBackoff = null);
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception>? CaptureFaulted;
    public bool IsRunning { get; }
    public string? ActiveDeviceId { get; }
    public void EnsureStarted(bool force = false);
    public void Rebuild();
    public void StopCapture();
    public void Dispose();
}

// IWarmAudioRecorder addition (Task 5)
event Action<Exception>? IWarmAudioRecorder.CaptureFaulted;
```

---

## Task 0: Provision the .NET SDK and establish a green baseline

**Files:**
- None committed (SDK lands in gitignored `./.dotnet/`).

**Interfaces:**
- Consumes: nothing.
- Produces: a working `dotnet` and a passing baseline run of the existing
  `Winpepper.Audio.Tests` — the harness every later task reuses.

- [ ] **Step 1: Provision the SDK locally**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/harden-warm-mic-capture
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a `latestFeature` roll-forward such as `9.0.1xx`).

- [ ] **Step 2: Build the audio test project for the Linux TFM**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: `Build succeeded.` (warnings OK). The `#if WINDOWS` capture files are
excluded from the `net9.0` compile, so NAudio's Windows-only types never break
the Linux build.

- [ ] **Step 3: Run the existing audio suite via the in-process runner**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows"
```
Expected: all existing `WarmCaptureBufferTests` pass, `0 errors`. This confirms
the runner works before any code changes. No commit (nothing changed).

---

## Task 1: `AudioEnergy` — pure session-silence detector (finding #2)

**Files:**
- Create: `src/Winpepper.Audio/AudioEnergy.cs`
- Create: `tests/Winpepper.Audio.Tests/AudioEnergyTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `AudioEnergy.Rms`, `AudioEnergy.SilenceRmsThreshold`,
  `AudioEnergy.IsSessionSilent` (consumed by Task 6 and Task 10).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Audio.Tests/AudioEnergyTests.cs`:
```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class AudioEnergyTests
{
    [Fact]
    public void Rms_OfAllZeros_IsZero()
    {
        AudioEnergy.Rms(new float[512]).ShouldBe(0.0, 1e-9);
    }

    [Fact]
    public void Rms_OfConstantAmplitude_EqualsThatAmplitude()
    {
        var frame = new float[1000];
        for (var i = 0; i < frame.Length; i++) frame[i] = 0.5f;
        AudioEnergy.Rms(frame).ShouldBe(0.5, 1e-6);
    }

    [Fact]
    public void IsSessionSilent_TrueForZeroFilledSession()
    {
        AudioEnergy.IsSessionSilent(new float[16000]).ShouldBeTrue();
    }

    [Fact]
    public void IsSessionSilent_TrueForNearZeroBelowThreshold()
    {
        var frame = new float[16000];
        for (var i = 0; i < frame.Length; i++) frame[i] = 5e-5f; // rms < 1e-4
        AudioEnergy.IsSessionSilent(frame).ShouldBeTrue();
    }

    [Fact]
    public void IsSessionSilent_FalseForRealSpeechLevel()
    {
        var frame = new float[16000];
        for (var i = 0; i < frame.Length; i++) frame[i] = (i % 2 == 0) ? 0.2f : -0.2f;
        AudioEnergy.IsSessionSilent(frame).ShouldBeFalse();
    }

    [Fact]
    public void IsSessionSilent_FalseForEmptySession()
    {
        // Nothing captured is handled by the caller's length guard, not here.
        AudioEnergy.IsSessionSilent(ReadOnlySpan<float>.Empty).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `error CS0103: The name 'AudioEnergy' does not exist`.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Winpepper.Audio/AudioEnergy.cs`:
```csharp
namespace Winpepper.Audio;

/// <summary>
/// Pure-managed audio-energy helpers (Bug 2 — undetectable silent capture).
/// OS mic-mute, the Windows privacy toggle, or a Bluetooth profile hiccup can
/// hand us zero-filled buffers that are indistinguishable from healthy audio.
/// A cheap RMS check over a whole session lets the host tell the user "no audio
/// detected" instead of silently transcribing nothing.
/// </summary>
public static class AudioEnergy
{
    /// <summary>
    /// Sessions whose RMS is below this are "essentially zero energy" (~-80 dBFS).
    /// This is a ZERO-ENERGY / dead-device detector, NOT a voice-activity detector:
    /// a live mic's noise floor (~-40..-65 dBFS) stays above this even during long
    /// pauses, so only muted / privacy-off / zero-filled capture falls below it. Do
    /// not "improve" this into a VAD or raise it toward speech levels.
    /// </summary>
    public const double SilenceRmsThreshold = 1e-4;

    /// <summary>Root-mean-square amplitude of a mono float frame (0 for empty).</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0.0;
        double sumSq = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            double v = samples[i];
            sumSq += v * v;
        }
        return Math.Sqrt(sumSq / samples.Length);
    }

    /// <summary>
    /// True when a non-empty session captured essentially zero energy. Empty
    /// input returns false — "nothing captured" is a distinct condition the
    /// caller guards with a length check before deciding to warn.
    /// </summary>
    public static bool IsSessionSilent(ReadOnlySpan<float> samples, double rmsThreshold = SilenceRmsThreshold)
        => samples.Length > 0 && Rms(samples) < rmsThreshold;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" -class "Winpepper.Audio.Tests.AudioEnergyTests"
```
Expected: PASS — 6 tests, `0 errors`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/AudioEnergy.cs tests/Winpepper.Audio.Tests/AudioEnergyTests.cs
git commit -m "feat: add AudioEnergy session-silence detector (Bug 2)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 2: `WarmCaptureBuffer.Clear()` — invalidate stale pre-roll on rebuild (finding #6)

**Files:**
- Modify: `src/Winpepper.Audio/WarmCaptureBuffer.cs`
- Modify: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs`

**Interfaces:**
- Consumes: existing `WarmCaptureBuffer(int)`, `Ingest`, `StartSession`,
  `StopSession`, `IsSessionActive`.
- Produces: `WarmCaptureBuffer.Clear()` (consumed by Task 3's `Rebuild`).

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs` (inside the
class, before the closing brace):
```csharp
    [Fact]
    public void Clear_DropsRing_SoNextSessionHasNoStalePreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2, 3 }); // stale-device audio

        buf.Clear();                          // device rebuilt -> ring invalid

        buf.StartSession(prerollSamples: 100);
        buf.Ingest(new float[] { 4, 5 });     // only new-device audio
        buf.StopSession().ShouldBe(new float[] { 4, 5 });
    }

    [Fact]
    public void Clear_WhileSessionActive_DropsRingButKeepsSessionUsable()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(prerollSamples: 0);
        buf.Ingest(new float[] { 7 });
        buf.Clear();                          // must not throw or wedge the session
        buf.Ingest(new float[] { 8 });
        buf.StopSession().ShouldBe(new float[] { 7, 8 });
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `error CS1061: 'WarmCaptureBuffer' does not contain a definition for 'Clear'`.

- [ ] **Step 3: Write the minimal implementation**

In `src/Winpepper.Audio/WarmCaptureBuffer.cs`, add this method inside the class
(e.g. after `StopSession`):
```csharp
    /// <summary>
    /// Drop all buffered ring history (Bug 6). Called on a device rebuild so the
    /// next session's pre-roll cannot be seeded with audio captured on the old
    /// device. Leaves an in-flight session's already-collected audio intact.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _ring.Clear();
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" -class "Winpepper.Audio.Tests.WarmCaptureBufferTests"
```
Expected: PASS — all `WarmCaptureBufferTests` (existing 6 + new 2), `0 errors`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/WarmCaptureBuffer.cs tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs
git commit -m "feat: add WarmCaptureBuffer.Clear to invalidate pre-roll on rebuild (Bug 6)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 3: `ICaptureSource` seam + `WarmCaptureCoordinator` core (findings #4, #5, #6)

**Files:**
- Create: `src/Winpepper.Audio/ICaptureSource.cs`
- Create: `src/Winpepper.Audio/WarmCaptureCoordinator.cs`
- Create: `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs`
- Create: `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`

**Interfaces:**
- Consumes: `WarmCaptureBuffer` (incl. `Clear` from Task 2).
- Produces: `ICaptureSource`, `WarmCaptureCoordinator` (start/stop/rebuild/route),
  and `FakeCaptureSource` (test seam). The fault-recovery members
  (`CaptureFaulted`, backoff, `EnsureStarted(force)`) are extended in Task 5; the
  concurrency hammer is Task 4.

- [ ] **Step 1: Write the seam and the fake first (compile targets for the test)**

Create `src/Winpepper.Audio/ICaptureSource.cs`:
```csharp
namespace Winpepper.Audio;

/// <summary>
/// Test seam over a live audio capture (Bug 4/5). The pure-managed
/// <see cref="WarmCaptureCoordinator"/> drives lifecycle through this interface
/// so the epoch/lock discipline and fault recovery can be unit-tested on Linux
/// with a fake, while the real NAudio implementation
/// (<c>WasapiCaptureSource</c>) stays thin and Windows-only.
///
/// Implementations must already deliver mono 16 kHz float frames — all
/// decode/downmix/resample happens inside the implementation, never here.
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>Endpoint id the live source was built on (for default-device drift checks).</summary>
    string DeviceId { get; }

    /// <summary>Raised on the capture thread with mono 16 kHz frames.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when capture stops. A non-null argument signals a fault.</summary>
    event Action<Exception?>? Stopped;

    /// <summary>Begin capturing. May throw if the device is unavailable.</summary>
    void Start();
}
```

Create `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs`:
```csharp
using Winpepper.Audio;

namespace Winpepper.Audio.Tests;

/// <summary>
/// Deterministic in-memory capture seam for coordinator tests. Lets a test fire
/// synthetic frames and faults. <see cref="RaiseFrame"/> deliberately does NOT
/// throw when disposed — it models a *late* capture-thread callback arriving
/// after teardown, exactly the race the coordinator's epoch guard must absorb.
/// The guard is what's under test: if the coordinator ever touched a disposed
/// source's members it would be observable (see the sabotage step in the
/// concurrency hammer), but under the correct guard no frame is ever routed into
/// a disposed instance.
/// </summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    private volatile bool _disposed;

    public FakeCaptureSource(string deviceId = "fake-device") { DeviceId = deviceId; }

    public string DeviceId { get; }
    public bool Disposed => _disposed;
    public bool Started { get; private set; }
    public bool ThrowOnStart { get; set; }

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        if (ThrowOnStart) throw new InvalidOperationException("fake start failure");
        Started = true;
    }

    /// <summary>Simulate a capture-thread frame callback (may arrive after Dispose).</summary>
    public void RaiseFrame(float[] frame) => FramesAvailable?.Invoke(frame);

    /// <summary>Simulate the source stopping (fault when <paramref name="ex"/> is non-null).</summary>
    public void RaiseStopped(Exception? ex) => Stopped?.Invoke(ex);

    public void Dispose() => _disposed = true;
}
```

- [ ] **Step 2: Write the failing core tests**

Create `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`:
```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class WarmCaptureCoordinatorTests
{
    private static WarmCaptureCoordinator NewCoordinator(
        Func<ICaptureSource> factory, out WarmCaptureBuffer buffer)
    {
        buffer = new WarmCaptureBuffer(ringCapacitySamples: 16000);
        return new WarmCaptureCoordinator(buffer, factory);
    }

    [Fact]
    public void EnsureStarted_StartsExactlyOneSource()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out _);

        c.EnsureStarted();
        c.EnsureStarted(); // idempotent

        made.Count.ShouldBe(1);
        made[0].Started.ShouldBeTrue();
        c.IsRunning.ShouldBeTrue();
        c.ActiveDeviceId.ShouldBe("fake-device");
    }

    [Fact]
    public void Frames_RouteToBuffer_AndReRaiseOnlyDuringSession()
    {
        FakeCaptureSource? src = null;
        var c = NewCoordinator(() => src = new FakeCaptureSource(), out var buffer);
        var reRaised = new List<float>();
        c.FramesAvailable += f => reRaised.AddRange(f.ToArray());

        c.EnsureStarted();
        src!.RaiseFrame(new float[] { 1, 2 });   // idle: ring only, no re-raise
        buffer.StartSession(prerollSamples: 0);
        src!.RaiseFrame(new float[] { 3, 4 });   // active: re-raised
        var session = buffer.StopSession();

        reRaised.ShouldBe(new float[] { 3, 4 });
        session.ShouldBe(new float[] { 3, 4 });
    }

    [Fact]
    public void Rebuild_DisposesOldSource_ClearsRing_AndStartsNew()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out var buffer);

        c.EnsureStarted();
        made[0].RaiseFrame(new float[] { 9, 9, 9 }); // stale-device audio into the ring
        c.Rebuild();

        made.Count.ShouldBe(2);
        made[0].Disposed.ShouldBeTrue();   // old disposed
        made[1].Started.ShouldBeTrue();    // new started
        c.ActiveDeviceId.ShouldBe("fake-device");

        // Ring was cleared on rebuild: a session started now sees no stale audio.
        buffer.StartSession(prerollSamples: 16000);
        buffer.StopSession().ShouldBeEmpty();
    }

    [Fact]
    public void StartLocked_DisposesPartialSource_WhenStartThrows()
    {
        FakeCaptureSource? src = null;
        var c = NewCoordinator(() => src = new FakeCaptureSource { ThrowOnStart = true }, out _);

        c.EnsureStarted();          // must swallow the throw, not leak the source

        c.IsRunning.ShouldBeFalse();
        src!.Disposed.ShouldBeTrue(); // partial source disposed (Bug 5)
    }

    [Fact]
    public void StaleSourceFrame_AfterRebuild_IsIgnored_NoDisposedAccess()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out var buffer);
        c.EnsureStarted();
        var old = made[0];
        c.Rebuild();

        // A late frame from the disposed old source must be dropped by the epoch
        // guard without ever routing into the buffer.
        buffer.StartSession(0);
        old.RaiseFrame(new float[] { 5 });
        buffer.StopSession().ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `error CS0246: The type or namespace name 'WarmCaptureCoordinator' could not be found`.

- [ ] **Step 4: Write the minimal implementation**

Create `src/Winpepper.Audio/WarmCaptureCoordinator.cs` (this version includes the
Task 5 fault-recovery members so the class is written once; Task 5 adds only the
tests that exercise them):
```csharp
using System.Threading;

namespace Winpepper.Audio;

/// <summary>
/// Pure-managed lifecycle owner for warm capture (Bugs 4/5/6/7). Holds the live
/// <see cref="ICaptureSource"/> behind a lock, routes frames into the
/// <see cref="WarmCaptureBuffer"/>, and makes teardown mutually safe with the
/// lock-free capture callback:
///
///  * Frames carry their originating source; <see cref="OnSourceFrame"/> reads
///    the current source once and drops the frame if it no longer matches
///    (epoch guard), so a late callback from a disposed source is ignored.
///  * Teardown swaps the reference to null BEFORE disposing, so the callback
///    can never observe a half-disposed source.
///  * A partially-constructed source is disposed if <see cref="ICaptureSource.Start"/>
///    throws (Bug 5).
///  * The ring is cleared on every rebuild (Bug 6).
///  * A fault triggers a logged rebuild attempt, rate-limited by a backoff so a
///    storming device does not spin (Bug 7).
/// </summary>
public sealed class WarmCaptureCoordinator : IDisposable
{
    private readonly WarmCaptureBuffer _buffer;
    private readonly Func<ICaptureSource> _sourceFactory;
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _faultBackoff;
    private readonly object _lock = new();

    private ICaptureSource? _current;   // read lock-free in OnSourceFrame via Volatile
    private string? _activeDeviceId;
    private DateTime? _lastFaultUtc;
    private bool _disposed;

    public WarmCaptureCoordinator(
        WarmCaptureBuffer buffer,
        Func<ICaptureSource> sourceFactory,
        Func<DateTime>? clock = null,
        TimeSpan? faultBackoff = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
        _clock = clock ?? (() => DateTime.UtcNow);
        _faultBackoff = faultBackoff ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Re-raised (mono 16 kHz) only while a session is active.</summary>
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Raised when capture faults or fails to (re)start (Bug 3).</summary>
    public event Action<Exception>? CaptureFaulted;

    public bool IsRunning => Volatile.Read(ref _current) is not null;
    public string? ActiveDeviceId { get { lock (_lock) return _activeDeviceId; } }

    /// <summary>
    /// Start capture if not already running. <paramref name="force"/> bypasses
    /// the fault backoff — used when the user explicitly starts a session on a
    /// previously faulted stream (Bug 7).
    /// </summary>
    public void EnsureStarted(bool force = false)
    {
        Exception? fault = null;
        lock (_lock)
        {
            if (_disposed || _current is not null) return;
            if (!force && InBackoffLocked()) return;
            fault = StartLocked();
        }
        if (fault is not null) CaptureFaulted?.Invoke(fault);
    }

    /// <summary>Tear down the current source and build a fresh one on the current default (Bug 6/7).</summary>
    public void Rebuild()
    {
        Exception? fault = null;
        lock (_lock)
        {
            if (_disposed) return;
            SwapOutAndDisposeLocked();
            _buffer.Clear();            // Bug 6: no stale-device pre-roll
            fault = StartLocked();
        }
        if (fault is not null) CaptureFaulted?.Invoke(fault);
    }

    /// <summary>Stop and dispose the current source (used by cold-mode teardown).</summary>
    public void StopCapture()
    {
        lock (_lock) SwapOutAndDisposeLocked();
    }

    // --- internals -----------------------------------------------------------

    private bool InBackoffLocked()
        => _lastFaultUtc is { } last && (_clock() - last) <= _faultBackoff;

    /// <summary>Build+subscribe+start under the lock. Returns a fault to raise after unlocking.</summary>
    private Exception? StartLocked()
    {
        ICaptureSource? src = null;
        try
        {
            src = _sourceFactory();
            src.FramesAvailable += f => OnSourceFrame(src, f);
            src.Stopped += ex => OnSourceStopped(src, ex);
            src.Start();
            Volatile.Write(ref _current, src);
            _activeDeviceId = src.DeviceId;
            return null;
        }
        catch (Exception ex)
        {
            try { src?.Dispose(); } catch { /* best-effort teardown of partial source (Bug 5) */ }
            _lastFaultUtc = _clock();
            return ex;
        }
    }

    /// <summary>Swap the reference to null BEFORE disposing so callbacks bail early.</summary>
    private void SwapOutAndDisposeLocked()
    {
        var old = _current;
        Volatile.Write(ref _current, null);
        _activeDeviceId = null;
        if (old is not null) { try { old.Dispose(); } catch { /* best-effort */ } }
    }

    private void OnSourceFrame(ICaptureSource source, ReadOnlyMemory<float> frame)
    {
        // Epoch guard: read the live reference once. If this callback belongs to
        // a source that has since been swapped out, drop it — we never touch the
        // (possibly disposed) source object here, only the frame payload.
        if (!ReferenceEquals(source, Volatile.Read(ref _current))) return;
        _buffer.Ingest(frame.Span);
        if (_buffer.IsSessionActive) FramesAvailable?.Invoke(frame);
    }

    private void OnSourceStopped(ICaptureSource source, Exception? ex)
    {
        if (ex is null) return; // clean stop, nothing to recover
        bool retry;
        Exception? startFault = null;
        lock (_lock)
        {
            if (!ReferenceEquals(source, _current)) return; // already replaced
            SwapOutAndDisposeLocked();
            var now = _clock();
            retry = _lastFaultUtc is not { } last || (now - last) > _faultBackoff;
            _lastFaultUtc = now;
            if (retry && !_disposed) startFault = StartLocked();
        }
        CaptureFaulted?.Invoke(ex);
        if (startFault is not null) CaptureFaulted?.Invoke(startFault);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            SwapOutAndDisposeLocked();
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" -class "Winpepper.Audio.Tests.WarmCaptureCoordinatorTests"
```
Expected: PASS — 5 tests, `0 errors`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Audio/ICaptureSource.cs src/Winpepper.Audio/WarmCaptureCoordinator.cs \
        tests/Winpepper.Audio.Tests/FakeCaptureSource.cs \
        tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs
git commit -m "feat: add ICaptureSource seam and WarmCaptureCoordinator (Bugs 4/5/6)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 4: Concurrency hammer test — rebuild vs frame callback (finding #4)

**Files:**
- Modify: `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`

**Interfaces:**
- Consumes: `WarmCaptureCoordinator`, `FakeCaptureSource` (Task 3).
- Produces: proof (thousands of iterations) that no `ObjectDisposedException`
  or null-deref escapes when teardown races the capture callback.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs` (inside
the class):
```csharp
    [Fact]
    public void ConcurrencyHammer_RebuildVsFrames_NeverThrows()
    {
        // A rolling registry of sources so the frame thread can fire callbacks
        // from whichever source is (or just was) live, exactly the race the
        // council could not settle statically.
        var live = new System.Collections.Concurrent.ConcurrentBag<FakeCaptureSource>();
        FakeCaptureSource Make() { var s = new FakeCaptureSource(); live.Add(s); return s; }

        var buffer = new WarmCaptureBuffer(ringCapacitySamples: 4000);
        using var c = new WarmCaptureCoordinator(buffer, Make);
        c.EnsureStarted();
        buffer.StartSession(0);

        Exception? escaped = null;
        var stop = false;
        var frame = new float[] { 0.1f, -0.1f, 0.2f, -0.2f };

        var frameThread = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    // Fire frames from every source ever made — including ones
                    // that were just disposed by a concurrent Rebuild. The fake's
                    // RaiseFrame never throws on its own; the ONLY way an
                    // exception escapes here is if the coordinator touches a
                    // disposed source (which the epoch guard must prevent).
                    foreach (var s in live.ToArray())
                        s.RaiseFrame(frame);
                }
            }
            catch (Exception ex) { escaped = ex; }
        });

        var rebuildThread = new Thread(() =>
        {
            try { for (var i = 0; i < 5000; i++) c.Rebuild(); }
            catch (Exception ex) { escaped = ex; }
            finally { Volatile.Write(ref stop, true); }
        });

        frameThread.Start();
        rebuildThread.Start();
        rebuildThread.Join();
        frameThread.Join();

        escaped.ShouldBeNull();
    }
```

- [ ] **Step 2: Run the test to verify it passes (guard is already in place)**

Because the epoch/reference-swap discipline was implemented in Task 3, this test
should pass immediately. Run it and confirm it exercises the race:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" \
  -method "Winpepper.Audio.Tests.WarmCaptureCoordinatorTests.ConcurrencyHammer_RebuildVsFrames_NeverThrows"
```
Expected: PASS.

- [ ] **Step 3: Prove the test has teeth (temporary sabotage)**

Temporarily break the guard to confirm the hammer catches the race, then revert.
In `src/Winpepper.Audio/WarmCaptureCoordinator.cs`, change `OnSourceFrame`'s
guard line from:
```csharp
        if (!ReferenceEquals(source, Volatile.Read(ref _current))) return;
```
to (sabotage — routes to a member of the possibly-disposed source):
```csharp
        _ = source.DeviceId; // SABOTAGE: touch the source unconditionally
        if (!ReferenceEquals(source, Volatile.Read(ref _current))) return;
```
`FakeCaptureSource.DeviceId` is a plain property so it will not throw; to make
the sabotage observable, also temporarily change `FakeCaptureSource.DeviceId`'s
getter to `_disposed ? throw new ObjectDisposedException(nameof(FakeCaptureSource)) : "fake-device"`.
Rebuild and run the hammer:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" \
  -method "Winpepper.Audio.Tests.WarmCaptureCoordinatorTests.ConcurrencyHammer_RebuildVsFrames_NeverThrows"
```
Expected: FAIL (an `ObjectDisposedException` escapes). Now revert BOTH temporary
edits (the sabotage line in `WarmCaptureCoordinator.cs` and the `DeviceId`
getter in `FakeCaptureSource.cs`) and re-run — expected PASS. Do not commit the
sabotage.

- [ ] **Step 4: Commit the hammer test**

```bash
git add tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs
git commit -m "test: add rebuild-vs-frame concurrency hammer for WarmCaptureCoordinator (Bug 4)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 5: Fault recovery, backoff, and faulted-stream restart (findings #3, #7)

**Files:**
- Modify: `src/Winpepper.Audio/IWarmAudioRecorder.cs`
- Modify: `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`

**Interfaces:**
- Consumes: `WarmCaptureCoordinator` (fault-recovery members already implemented
  in Task 3's file).
- Produces: `IWarmAudioRecorder.CaptureFaulted` (consumed by Task 8 forwarding
  and Task 10 routing); tests proving fault → `CaptureFaulted` + rebuild + backoff
  + forced restart.

> **Fault-detection scope (see Coverage Map note).** This rebuild-on-fault path
> is driven ONLY by a non-null `ICaptureSource.Stopped(ex)`, which NAudio raises
> for HARD device faults (unplug/disable/reconfigure → `AUDCLNT_E_DEVICE_INVALIDATED`).
> "Soft" faults — OS mic-privacy toggle, some Bluetooth hiccups — can keep the
> stream alive delivering silent frames with NO stop event, so they do NOT
> auto-rebuild here; they are surfaced instead by the Bug 2 session-end silence
> detector (Task 10) and the Task 6 `SessionWasSilent` flag. Do not "fix" the
> tests to expect auto-recovery from a silent-but-unfaulted stream — that is a
> deliberate, documented boundary, not a gap.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs` (inside
the class):
```csharp
    [Fact]
    public void Fault_RaisesCaptureFaulted_AndAutoRebuildsWhenPastBackoff()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var made = new List<FakeCaptureSource>();
        var buffer = new WarmCaptureBuffer(1000);
        var c = new WarmCaptureCoordinator(
            buffer,
            () => { var s = new FakeCaptureSource(); made.Add(s); return s; },
            clock: () => now,
            faultBackoff: TimeSpan.FromSeconds(2));

        var faults = new List<Exception>();
        c.CaptureFaulted += faults.Add;
        c.EnsureStarted();

        made[0].RaiseStopped(new InvalidOperationException("device removed"));

        faults.Count.ShouldBe(1);
        made.Count.ShouldBe(2);          // auto-rebuilt (first fault, no prior)
        made[0].Disposed.ShouldBeTrue();
        c.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public void StormingFaults_WithinBackoff_DoNotAutoRebuild()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var made = new List<FakeCaptureSource>();
        var buffer = new WarmCaptureBuffer(1000);
        var c = new WarmCaptureCoordinator(
            buffer,
            () => { var s = new FakeCaptureSource(); made.Add(s); return s; },
            clock: () => now,
            faultBackoff: TimeSpan.FromSeconds(2));
        c.EnsureStarted();                       // made[0]

        made[0].RaiseStopped(new Exception("f1")); // past-backoff (no prior) -> rebuild made[1]
        made[1].RaiseStopped(new Exception("f2")); // same clock -> within backoff -> no rebuild

        made.Count.ShouldBe(2);
        c.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public void EnsureStarted_Force_RestartsAFaultedStreamIgnoringBackoff()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var made = new List<FakeCaptureSource>();
        var buffer = new WarmCaptureBuffer(1000);
        var c = new WarmCaptureCoordinator(
            buffer,
            () => { var s = new FakeCaptureSource(); made.Add(s); return s; },
            clock: () => now,
            faultBackoff: TimeSpan.FromSeconds(2));
        c.EnsureStarted();
        made[0].RaiseStopped(new Exception("f1")); // rebuild made[1]
        made[1].RaiseStopped(new Exception("f2")); // within backoff -> stays down

        c.IsRunning.ShouldBeFalse();
        c.EnsureStarted(force: true);              // user starts a session on a faulted stream (Bug 7)

        c.IsRunning.ShouldBeTrue();
        made.Count.ShouldBe(3);
    }
```

- [ ] **Step 2: Add the interface member**

In `src/Winpepper.Audio/IWarmAudioRecorder.cs`, add inside the interface (after
the `FramesAvailable` event):
```csharp
    /// <summary>Raised when the capture stream faults or fails to (re)start, so
    /// the host can log it and surface a user-facing signal (Bug 3).</summary>
    event Action<Exception>? CaptureFaulted;
```

- [ ] **Step 3: Run the tests to verify they pass**

The coordinator's fault logic was implemented in Task 3, so these tests pass once
they compile. Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" -class "Winpepper.Audio.Tests.WarmCaptureCoordinatorTests"
```
Expected: PASS — all coordinator tests (Task 3 + Task 4 + Task 5), `0 errors`.

Note: adding `CaptureFaulted` to `IWarmAudioRecorder` will make the Linux build
of `Winpepper.Audio` succeed (the interface is not `#if WINDOWS`); the only
implementer, `WarmWasapiRecorder`, is `#if WINDOWS` and is updated in Task 8, so
the `net9.0` compile is unaffected.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.Audio/IWarmAudioRecorder.cs tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs
git commit -m "feat: fault recovery + backoff for warm capture; add CaptureFaulted (Bugs 3/7)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 6: Optional warm-level sustained-silence health flag (finding #2, optional)

**Files:**
- Modify: `src/Winpepper.Audio/WarmCaptureBuffer.cs`
- Modify: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs`

**Interfaces:**
- Consumes: `AudioEnergy` (Task 1), `WarmCaptureBuffer`.
- Produces: `WarmCaptureBuffer.SessionWasSilent` — a per-session health flag,
  computed at `StopSession`, available to the host as a cheap secondary signal.

This implements the council's optional "also track sustained zero-energy at the
warm stream level" as a real, tested feature: the buffer already accumulates the
whole session, so it can flag a wholly-silent session for free at `StopSession`.
The host (Task 10) primarily uses `AudioEnergy.IsSessionSilent` over the returned
samples; this flag is the equivalent signal computed at the buffer boundary and
is unit-tested here.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs` (inside the
class):
```csharp
    [Fact]
    public void SessionWasSilent_TrueWhenAllSamplesEssentiallyZero()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[64]); // zero-filled
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeTrue();
    }

    [Fact]
    public void SessionWasSilent_FalseWhenSpeechPresent()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[] { 0.3f, -0.3f, 0.3f, -0.3f });
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeFalse();
    }

    [Fact]
    public void SessionWasSilent_FalseWhenNothingCaptured()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeFalse(); // empty != silent capture
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `error CS1061: 'WarmCaptureBuffer' does not contain a definition for 'SessionWasSilent'`.

- [ ] **Step 3: Write the minimal implementation**

In `src/Winpepper.Audio/WarmCaptureBuffer.cs`:

Add a backing field next to the other fields (after `private bool _active;`):
```csharp
    private bool _sessionWasSilent;
```

Add the public property (e.g. after `IsSessionActive`):
```csharp
    /// <summary>
    /// True when the most recently ended session captured essentially zero
    /// energy (Bug 2, warm-level health flag). Valid after <see cref="StopSession"/>.
    /// </summary>
    public bool SessionWasSilent
    {
        get { lock (_lock) { return _sessionWasSilent; } }
    }
```

Change `StopSession` to compute the flag before clearing:
```csharp
    public float[] StopSession()
    {
        lock (_lock)
        {
            _active = false;
            var result = _session.ToArray();
            _sessionWasSilent = AudioEnergy.IsSessionSilent(result);
            _session.Clear();
            return result;
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows" -class "Winpepper.Audio.Tests.WarmCaptureBufferTests"
```
Expected: PASS — all `WarmCaptureBufferTests`, `0 errors`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/WarmCaptureBuffer.cs tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs
git commit -m "feat: warm-level SessionWasSilent health flag (Bug 2, optional)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 7 (Windows): `WasapiCaptureSource` NAudio adapter — resampler hoist, dispose-on-throw, logging (findings #1, #3, #5)

**Files:**
- Create: `src/Winpepper.Audio/WasapiCaptureSource.cs` (`#if WINDOWS`)

**Interfaces:**
- Consumes: `ICaptureSource` (Task 3), NAudio (`WasapiCapture`,
  `MediaFoundationResampler`, `BufferedWaveProvider`), `ILogger`.
- Produces: `WasapiCaptureSource(string? deviceId, ILogger? log)` — the concrete
  seam the coordinator builds (consumed by Task 8).

**Verification:** Linux-unbuildable (`#if WINDOWS`). Verified in the Windows
Smoke Test Checklist (items S1, S2, S6). The coordinator's epoch/ring/fault
discipline is proven on Linux by Tasks 3–5, but this adapter carries ONE
concurrency guarantee the Linux hammer cannot reach: the mutual exclusion between
`OnData` (NAudio capture thread) and `Dispose`/teardown (which joins that thread).
That teardown-vs-callback safety is a residual Windows-only surface — it is why
`Dispose` deliberately runs the join OUTSIDE the callback lock (see Step 1) and
why smoke **S5** is a repeated stress loop with hang detection, not a single
unplug. This step ships production NAudio code — no stub.

- [ ] **Step 1: Write the adapter**

Create `src/Winpepper.Audio/WasapiCaptureSource.cs`:
```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

/// <summary>
/// Thin NAudio-backed <see cref="ICaptureSource"/> (Bugs 1/3/5). Owns exactly one
/// <see cref="WasapiCapture"/> and — critically — exactly ONE
/// <see cref="MediaFoundationResampler"/> per capture format, fed by a reusable
/// <see cref="BufferedWaveProvider"/>. The old code built a resampler in every
/// ~50 ms callback and never disposed it (~72k leaked COM objects/hour); here it
/// is created once when the format is known and disposed on teardown.
///
/// Decode/downmix/resample all happen here so the coordinator only ever sees
/// mono 16 kHz frames. <see cref="OnData"/> and <see cref="Dispose"/> are made
/// mutually safe with a lock + disposed flag, mirroring the epoch discipline the
/// coordinator unit-tests on Linux. NOTE: <see cref="Dispose"/> performs its
/// bookkeeping (set disposed, unhook, null the capture) under the lock but runs
/// the actual teardown (StopRecording/Dispose, which JOINS NAudio's capture
/// thread) OUTSIDE the lock. OnData holds the same lock, so joining while holding
/// it would deadlock. This teardown-vs-callback mutual exclusion is the one piece
/// of concurrency the Linux hammer (Task 4) does NOT cover — it lives only here
/// and is exercised in the Windows smoke stress loop (S5).
/// </summary>
public sealed class WasapiCaptureSource : ICaptureSource
{
    private const int SampleRate16k = 16000;

    private readonly string? _requestedDeviceId;
    private readonly ILogger? _log;
    private readonly object _lock = new();

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _resamplerInput;
    private MediaFoundationResampler? _resampler;
    private bool _disposed;

    public WasapiCaptureSource(string? deviceId, ILogger? log = null)
    {
        _requestedDeviceId = deviceId;
        _log = log;
    }

    public string DeviceId { get; private set; } = "";

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception?>? Stopped;

    public void Start()
    {
        WasapiCapture? capture = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(_requestedDeviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(_requestedDeviceId);

            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnRecordingStopped;
            capture.StartRecording();

            _capture = capture;
            DeviceId = device.ID;
        }
        catch (Exception ex)
        {
            // Bug 5: dispose the partially-constructed COM AudioClient before
            // rethrowing so a flaky-hardware retry loop does not leak a live mic.
            _log?.LogWarning(ex, "WASAPI capture failed to start for device {DeviceId}", _requestedDeviceId ?? "(default)");
            if (capture is not null)
            {
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnRecordingStopped;
                try { capture.Dispose(); } catch (Exception dex) { _log?.LogDebug(dex, "dispose of partial WASAPI capture failed"); }
            }
            throw;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _log?.LogWarning(e.Exception, "WASAPI capture stopped with fault");
        Stopped?.Invoke(e.Exception);
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var capture = _capture;
            if (capture is null) return;
            var fmt = capture.WaveFormat;

            try
            {
                var mono = DecodeToMono(e, fmt);
                if (mono is null)
                {
                    // Bug 3: an unsupported format used to be dropped silently.
                    _log?.LogWarning("Dropping capture frame: unsupported format {Encoding} {Bits}-bit",
                        fmt.Encoding, fmt.BitsPerSample);
                    return;
                }

                var frame = fmt.SampleRate == SampleRate16k ? mono : Resample(mono, fmt.SampleRate);
                FramesAvailable?.Invoke(frame);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "WASAPI frame processing failed");
            }
        }
    }

    private static float[]? DecodeToMono(WaveInEventArgs e, WaveFormat fmt)
    {
        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return null;
        }

        if (fmt.Channels <= 1) return samples;

        var mono = new float[sampleCount / fmt.Channels];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
            mono[i] = sum / fmt.Channels;
        }
        return mono;
    }

    private float[] Resample(float[] mono, int sourceSampleRate)
    {
        // Bug 1: build the resampler ONCE (per source rate), fed by a reusable
        // BufferedWaveProvider, instead of allocating one per callback.
        if (_resampler is null || _resamplerInput is null ||
            _resamplerInput.WaveFormat.SampleRate != sourceSampleRate)
        {
            DisposeResampler();
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, 1);
            _resamplerInput = new BufferedWaveProvider(sourceFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            _resampler = new MediaFoundationResampler(
                _resamplerInput, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate16k, 1))
            { ResamplerQuality = 60 };
        }

        var inBytes = new byte[mono.Length * 4];
        Buffer.BlockCopy(mono, 0, inBytes, 0, inBytes.Length);
        _resamplerInput.AddSamples(inBytes, 0, inBytes.Length);

        var resampled = new List<float>();
        var byteBuf = new byte[8192];
        int read;
        while ((read = _resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
        {
            var floats = new float[read / 4];
            Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
            resampled.AddRange(floats);
        }
        return resampled.ToArray();
    }

    private void DisposeResampler()
    {
        try { _resampler?.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "resampler dispose failed"); }
        _resampler = null;
        _resamplerInput = null;
    }

    public void Dispose()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            capture = _capture;
            _capture = null;
            if (capture is not null)
            {
                // Unhook INSIDE the lock so no new callback is dispatched. Any
                // in-flight OnData either already holds the lock (we wait for it) or
                // will see _disposed and return at the top.
                capture.DataAvailable -= OnData;
                capture.RecordingStopped -= OnRecordingStopped;
            }
        }
        // Bug 4 (deadlock): tear down OUTSIDE _lock. capture.Dispose() calls
        // WasapiCapture's captureThread.Join(); that capture thread may be parked at
        // OnData's `lock (_lock)`. Joining while holding _lock is a lock->Join vs
        // OnData-waiting-on-lock inversion => intermittent hang on rebuild/teardown.
        // Once _disposed is set and handlers are unhooked above, no OnData touches
        // _capture or the resampler, so this teardown is race-free without the lock.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
    }
}
#endif
```

- [ ] **Step 2: Verify it does not break the Linux build**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: `Build succeeded.` The new file is entirely `#if WINDOWS`, so it is
excluded from the `net9.0` compile and cannot break Linux tests.

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.Audio/WasapiCaptureSource.cs
git commit -m "feat: WasapiCaptureSource adapter with hoisted resampler + dispose-on-throw (Bugs 1/3/5)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 8 (Windows): Rewrite `WarmWasapiRecorder` to delegate to the coordinator (findings #4, #6, #7)

**Files:**
- Rewrite: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (`#if WINDOWS`)

**Interfaces:**
- Consumes: `WarmCaptureCoordinator` (Tasks 3–5), `WasapiCaptureSource` (Task 7),
  `WarmCaptureBuffer` (Task 2), `IWarmAudioRecorder` incl. `CaptureFaulted`
  (Task 5), `ILogger`.
- Produces: `WarmWasapiRecorder(bool prewarm, string? deviceId = null, ILogger? log = null)`
  — constructed by `PipelineHost` (Task 10).

**Verification:** Linux-unbuildable (`#if WINDOWS`). Verified in the Windows
Smoke Test Checklist (S1–S7). All of its concurrency, ring, and fault logic now
lives in the Linux-tested coordinator.

- [ ] **Step 1: Replace the file contents**

Overwrite `src/Winpepper.Audio/WarmWasapiRecorder.cs`:
```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single capture runs for
/// the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll).
///
/// All lifecycle/concurrency/ring/fault logic lives in the pure-managed,
/// Linux-tested <see cref="WarmCaptureCoordinator"/> behind the
/// <see cref="ICaptureSource"/> seam. This class is the thin Windows shell that
/// supplies the NAudio <see cref="WasapiCaptureSource"/> factory and re-resolves
/// the default input device on session start (WASAPI does not signal a running
/// capture when the default endpoint changes).
///
/// TODO(consolidation): a full IMMNotificationClient via
/// MMDeviceEnumerator.RegisterEndpointNotificationCallback would let us react to
/// OnDefaultDeviceChanged mid-session instead of only at session start. The
/// coordinator already rebuilds on fault with backoff and clears the ring on
/// rebuild, which covers the removal/hiccup cases; the per-session recheck below
/// covers "change default, then dictate". Full notification-client integration
/// is deferred as a clean follow-up because it is Windows-only and cannot be
/// unit-tested on this harness.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    private const int RingCapacitySamples = SampleRate16k; // ~1 s of history

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly WarmCaptureCoordinator _coordinator;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;
    public event Action<Exception>? CaptureFaulted;

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null, ILogger? log = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        _log = log;
        _coordinator = new WarmCaptureCoordinator(
            _buffer,
            sourceFactory: () => new WasapiCaptureSource(_deviceId, _log));
        _coordinator.FramesAvailable += f => FramesAvailable?.Invoke(f);
        _coordinator.CaptureFaulted += ex => CaptureFaulted?.Invoke(ex);
        if (_prewarm) _coordinator.EnsureStarted();
    }

    public void StartSession(int includePrerollMs)
    {
        // Follow the default input device: if it drifted since the warm stream
        // was built, rebuild on the new endpoint (clears the ring too).
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode, or a previously faulted warm stream: (re)start now. force
        // bypasses the fault backoff because the user explicitly asked to record.
        _coordinator.EnsureStarted(force: true);
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    private void RebuildIfDefaultChanged()
    {
        if (!_coordinator.IsRunning) return; // nothing live; EnsureStarted picks the current default
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            if (current.ID != _coordinator.ActiveDeviceId)
                _coordinator.Rebuild();
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "default-device recheck failed; keeping current warm stream");
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) _coordinator.StopCapture(); // cold mode tears down between sessions
        return samples;
    }

    public void Dispose() => _coordinator.Dispose();
}
#endif
```

- [ ] **Step 2: Verify the Linux build still succeeds**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows"
```
Expected: `Build succeeded.` and all pure-managed audio tests pass. The rewritten
file is `#if WINDOWS` so it is excluded from the `net9.0` compile.

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.Audio/WarmWasapiRecorder.cs
git commit -m "refactor: delegate WarmWasapiRecorder lifecycle to WarmCaptureCoordinator (Bugs 4/6/7)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 9 (Windows): Fix `WasapiRecorder` — resampler hoist, dispose-on-throw, safe Dispose, logging (findings #1, #8, #3)

**Files:**
- Modify: `src/Winpepper.Audio/WasapiRecorder.cs` (`#if WINDOWS`)

**Interfaces:**
- Consumes: NAudio, `IAudioRecorder` (unchanged), `ILogger`.
- Produces: `WasapiRecorder(string? deviceId = null, ILogger? log = null)` — the
  existing per-page level-meter/cold-start recorder, now leak-free. The optional
  `ILogger` parameter is additive; existing call sites (level meters) keep working.

**Verification:** Linux-unbuildable (`#if WINDOWS`). Verified in the Windows
Smoke Test Checklist (S8).

- [ ] **Step 1: Replace the file contents**

Overwrite `src/Winpepper.Audio/WasapiRecorder.cs`:
```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

public sealed class WasapiRecorder : IAudioRecorder
{
    private const int SampleRate16k = 16000;

    public AudioFormat Format => WinpepperAudioFormat.Mono16k;
    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly string? _deviceId;
    private readonly ILogger? _log;
    private readonly object _lock = new();
    private WasapiCapture? _capture;
    private List<float> _buffer = new();
    private BufferedWaveProvider? _resamplerInput;
    private MediaFoundationResampler? _resampler;
    private bool _disposed;

    public WasapiRecorder(string? deviceId = null, ILogger? log = null)
    {
        _deviceId = deviceId;
        _log = log;
    }

    public void Start()
    {
        WasapiCapture? capture = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = string.IsNullOrEmpty(_deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                : enumerator.GetDevice(_deviceId);

            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
            capture.DataAvailable += OnData;
            _buffer = new List<float>(SampleRate16k * 30);
            capture.StartRecording();
            _capture = capture;
        }
        catch (Exception ex)
        {
            // Dispose the partially-constructed COM object before rethrowing.
            _log?.LogWarning(ex, "WasapiRecorder failed to start for device {DeviceId}", _deviceId ?? "(default)");
            if (capture is not null)
            {
                capture.DataAvailable -= OnData;
                try { capture.Dispose(); } catch (Exception dex) { _log?.LogDebug(dex, "dispose of partial capture failed"); }
            }
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var capture = _capture;
            if (capture is null) return;
            var fmt = capture.WaveFormat;

            try
            {
                var mono = DecodeToMono(e, fmt);
                if (mono is null)
                {
                    _log?.LogWarning("Dropping meter frame: unsupported format {Encoding} {Bits}-bit",
                        fmt.Encoding, fmt.BitsPerSample);
                    return;
                }

                var frame = fmt.SampleRate == SampleRate16k ? mono : Resample(mono, fmt.SampleRate);
                _buffer.AddRange(frame);
                FramesAvailable?.Invoke(frame);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "WasapiRecorder frame processing failed");
            }
        }
    }

    private static float[]? DecodeToMono(WaveInEventArgs e, WaveFormat fmt)
    {
        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return null;
        }

        if (fmt.Channels <= 1) return samples;

        var mono = new float[sampleCount / fmt.Channels];
        for (var i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
            mono[i] = sum / fmt.Channels;
        }
        return mono;
    }

    private float[] Resample(float[] mono, int sourceSampleRate)
    {
        // Bug 1 (duplicate): build the resampler ONCE, not per callback.
        if (_resampler is null || _resamplerInput is null ||
            _resamplerInput.WaveFormat.SampleRate != sourceSampleRate)
        {
            DisposeResampler();
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, 1);
            _resamplerInput = new BufferedWaveProvider(sourceFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2),
            };
            _resampler = new MediaFoundationResampler(
                _resamplerInput, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate16k, 1))
            { ResamplerQuality = 60 };
        }

        var inBytes = new byte[mono.Length * 4];
        Buffer.BlockCopy(mono, 0, inBytes, 0, inBytes.Length);
        _resamplerInput.AddSamples(inBytes, 0, inBytes.Length);

        var resampled = new List<float>();
        var byteBuf = new byte[8192];
        int read;
        while ((read = _resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
        {
            var floats = new float[read / 4];
            Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
            resampled.AddRange(floats);
        }
        return resampled.ToArray();
    }

    private void DisposeResampler()
    {
        try { _resampler?.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "resampler dispose failed"); }
        _resampler = null;
        _resamplerInput = null;
    }

    public float[] Stop()
    {
        WasapiCapture? capture;
        float[] result;
        lock (_lock)
        {
            capture = _capture;
            _capture = null;
            if (capture is not null) capture.DataAvailable -= OnData;
            result = _buffer.ToArray();
        }
        // Bug 8 (deadlock): run teardown OUTSIDE _lock. capture.Dispose() joins
        // NAudio's capture thread, which may be parked at OnData's `lock (_lock)`;
        // joining while holding _lock would deadlock. Nulling _capture + unhooking
        // above means any later OnData returns before touching the resampler.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
        return result;
    }

    public void Dispose()
    {
        WasapiCapture? capture;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            capture = _capture;
            _capture = null;
            // Bug 8: unhook DataAvailable inside the lock so no new callback runs.
            if (capture is not null) capture.DataAvailable -= OnData;
        }
        // Bug 8 (deadlock): StopRecording()/Dispose() run OUTSIDE _lock. Dispose()
        // joins NAudio's capture thread, which may be parked at OnData's
        // `lock (_lock)`; joining while holding _lock would deadlock. _disposed +
        // nulled _capture guarantee any later OnData returns before the resampler.
        if (capture is not null)
        {
            try { capture.StopRecording(); } catch (Exception ex) { _log?.LogDebug(ex, "StopRecording failed"); }
            try { capture.Dispose(); } catch (Exception ex) { _log?.LogDebug(ex, "capture dispose failed"); }
        }
        DisposeResampler();
    }
}
#endif
```

- [ ] **Step 2: Verify the Linux build still succeeds**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: `Build succeeded.` (file is `#if WINDOWS`, excluded from Linux compile).

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.Audio/WasapiRecorder.cs
git commit -m "fix: WasapiRecorder resampler leak + safe Dispose/unhook (Bugs 1/3/8)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 10 (Windows): `PipelineHost` — session-end silence signal, fault routing, `TryStart` guard, `Dispose` unhook (findings #2, #3, #8)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (`#if WINDOWS`)

**Interfaces:**
- Consumes: `AudioEnergy.IsSessionSilent` (Task 1), `WarmWasapiRecorder`
  (Task 8) incl. `CaptureFaulted` (Task 5), existing `ErrorBus`, `IToastService`,
  `ILogger`.
- Produces: no new public surface; wires the user-facing signals.

**Verification:** Linux-unbuildable (`#if WINDOWS`). The silence *decision*
(`AudioEnergy.IsSessionSilent`) is already Linux-tested (Task 1); this task wires
it. Verified end-to-end in the Windows Smoke Test Checklist (S3, S4, S6, S7, S9).

- [ ] **Step 1: Add a reentrancy guard field**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, add a field beside the other
private fields (e.g. after `private Task? _runTask;`):
```csharp
    private readonly object _startGate = new();
    private Action<Exception>? _captureFaultHandler;
    private Action<ReadOnlyMemory<float>>? _frameHandler;
```

- [ ] **Step 2: Make `TryStart` idempotent and wire capture-fault routing**

Replace the body of `TryStart()` (currently `PipelineHost.cs:126-163`) with a
lock-guarded version. The two real call sites (AppShell.cs:321,
ModelsPage.xaml.cs:90) can now race or double-fire without constructing two live
mic streams:
```csharp
    public bool TryStart()
    {
        lock (_startGate)
        {
            if (IsRunning) return true;
            if (_asr is null)
            {
                if (!ParakeetSession.ModelFilesPresent(_modelDir))
                {
                    _log.LogWarning("ASR model files missing in {ModelDir}; pipeline disabled until models are downloaded", _modelDir);
                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr,
                        new FileNotFoundException("Speech model not installed. Open the Models tab to download it."),
                        Guid.Empty);
                    return false;
                }
                try
                {
                    _asr = new ParakeetSession(_modelDir);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed to load ASR model from {ModelDir}; pipeline disabled", _modelDir);
                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Asr, ex, Guid.Empty);
                    return false;
                }
            }
            // Bug 2: one warm recorder for the app lifetime. Frames flow (and the
            // meter animates) only while a session is active, so subscribe once.
            if (_warmRecorder is null)
            {
                var recorder = new Winpepper.Audio.WarmWasapiRecorder(
                    prewarm: _prewarmMicEnabled,
                    deviceId: null,
                    log: _log);
                _frameHandler = frame => _vm.ReportAudioFrame(frame);
                _captureFaultHandler = ex =>
                {
                    // Bug 3: capture faults are no longer silent — log and tell the user.
                    _log.LogError(ex, "microphone capture faulted");
                    _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Audio, ex, _currentSessionId);
                    _ = _toasts.ShowAsync(
                        "Winpepper",
                        "Microphone capture stopped unexpectedly — attempting to recover. Check your microphone if this repeats.",
                        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
                        TimeSpan.FromSeconds(6));
                };
                recorder.FramesAvailable += _frameHandler;
                recorder.CaptureFaulted += _captureFaultHandler;
                _warmRecorder = recorder;
            }
            _hook.Start();
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_runCts.Token));
            IsRunning = true;
            _log.LogInformation("Pipeline started (model dir {ModelDir})", _modelDir);
            return true;
        }
    }
```

- [ ] **Step 3: Add a session-end silence helper**

Add this private method to `PipelineHost` (e.g. just above `Dispose`):
```csharp
    /// <summary>
    /// Bug 2: if a whole session captured essentially zero energy (OS mic mute,
    /// privacy toggle, Bluetooth hiccup), the transcript will be empty for a
    /// reason the user cannot see. Surface it via the ErrorBus + a toast. Never
    /// called mid-session — only after StopSession — so genuine mid-session
    /// silence is not misreported.
    /// </summary>
    private void WarnIfSessionSilent(float[] samples, Guid sessionId)
    {
        if (samples.Length == 0) return; // nothing captured is a distinct (cancel) case
        if (!Winpepper.Audio.AudioEnergy.IsSessionSilent(samples)) return;

        _log.LogWarning("Session {SessionId} captured near-zero energy (RMS below the zero-energy threshold \u2014 mic likely muted / privacy-off / disconnected)", sessionId);
        _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Audio,
            new InvalidOperationException("No audio detected — check your microphone / privacy settings."),
            sessionId);
        _ = _toasts.ShowAsync(
            "Winpepper",
            "No audio detected — check your microphone / privacy settings.",
            Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
            TimeSpan.FromSeconds(6));
    }
```

- [ ] **Step 4: Call the helper from both stop paths**

In the `HoldUp` branch, immediately after
`var samples = _warmRecorder!.StopSession();` (currently `PipelineHost.cs:209`),
add:
```csharp
                WarnIfSessionSilent(samples, _currentSessionId);
```
In the `Toggle`-stop branch, immediately after
`var samples2 = _warmRecorder!.StopSession();` (currently `PipelineHost.cs:374`),
add:
```csharp
                    WarnIfSessionSilent(samples2, _currentSessionId);
```

- [ ] **Step 5: Unhook handlers in `Dispose`**

Replace the body of `Dispose()` (currently `PipelineHost.cs:517-524`) with:
```csharp
    public void Dispose()
    {
        _runCts?.Cancel();
        try { _runTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _hook.Dispose();
        _asr?.Dispose();
        if (_warmRecorder is not null)
        {
            // Bug 8 (hygiene): unhook the meter + fault handlers before teardown.
            if (_frameHandler is not null) _warmRecorder.FramesAvailable -= _frameHandler;
            if (_captureFaultHandler is not null) _warmRecorder.CaptureFaulted -= _captureFaultHandler;
            _warmRecorder.Dispose();
        }
    }
```

- [ ] **Step 6: Verify the change is self-consistent (no Linux build — Windows-only file)**

`PipelineHost.cs` is `#if WINDOWS` and references WinUI/NAudio types, so it is
not built on this Linux harness. Verify by inspection against this checklist and
confirm the pure-managed suite is still green (nothing this task touched is
Linux-compiled, but re-run to be safe):
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows"
```
Expected: all pure-managed audio tests pass. Inspection checklist:
`IWarmAudioRecorder.CaptureFaulted` exists (Task 5); `AudioEnergy.IsSessionSilent`
exists (Task 1); `WarmWasapiRecorder` ctor takes `(bool, string?, ILogger?)`
(Task 8); `_vm.ReportAudioFrame`, `_toasts.ShowAsync`, `_errorBus.Report`,
`ErrorStage.Audio` are all referenced exactly as in the existing file.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat: session-end silence signal + capture-fault routing + TryStart guard (Bugs 2/3/8)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Task 11 (Windows/UX): Always-on-mic disclosure — onboarding sentence + settings caption (finding #9)

**Files:**
- Modify: `src/Winpepper.App/Views/OnboardingPage.xaml` (`#if WINDOWS` view)
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` (`#if WINDOWS` view)

**Interfaces:**
- Consumes: existing XAML panels (`PickMicPanel` at OnboardingPage.xaml:35;
  `PrewarmMicToggle` at RecordingPage.xaml:54).
- Produces: static disclosure copy — no code contract.

**Verification:** XAML text; verified visually in the Windows Smoke Test
Checklist (S10). This is real user-facing copy, not a stub.

- [ ] **Step 1: Add the onboarding disclosure sentence**

In `src/Winpepper.App/Views/OnboardingPage.xaml`, inside `PickMicPanel` (which
starts at line 35, `<StackPanel x:Name="PickMicPanel" ...>`), add a caption
`TextBlock` immediately after the `MicCombo` `ComboBox` (line 52). Use the exact
verbatim copy from the spec:
```xml
                <TextBlock TextWrapping="Wrap"
                           Style="{ThemeResource CaptionTextBlockStyle}"
                           Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                           Text="While Winpepper runs, the microphone stays warm so your first words are never clipped — the mic-in-use indicator will stay on. You can turn this off in Settings." />
```

- [ ] **Step 2: Add the resource-cost caption to the Settings toggle**

In `src/Winpepper.App/Views/RecordingPage.xaml`, immediately after the
`PrewarmMicToggle` `ToggleSwitch` (line 54), add a caption `TextBlock` mentioning
the resource cost, and set a `ToolTipService.ToolTip` on the toggle. Add the
caption:
```xml
                    <TextBlock TextWrapping="Wrap"
                               Text="Keeps one microphone stream open the whole time Winpepper runs (the mic-in-use indicator stays on and it uses a little CPU and battery). Turn it off to only use the mic while dictating."
                               Style="{ThemeResource CaptionTextBlockStyle}"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
```
And add the tooltip to the existing toggle by inserting this attribute on the
`PrewarmMicToggle` element (keep all its current attributes):
```xml
                                  ToolTipService.ToolTip="Uses a little extra CPU and battery to keep the mic ready; the mic-in-use indicator stays on."
```

- [ ] **Step 3: Verify markup is well-formed by inspection**

These `#if WINDOWS` views are not built on the Linux harness. Verify by
inspection: the two new `TextBlock`s are inside their parent `StackPanel`s, the
em-dash `—` matches the spec's copy, and the `ToolTipService.ToolTip` attribute
sits on the existing `PrewarmMicToggle` element without duplicating attributes.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/OnboardingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml
git commit -m "docs: disclose always-on warm mic in onboarding + settings (Bug 9)

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

## Final Full-Suite Verification (Linux)

- [ ] **Run every pure-managed audio test**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
  -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
  -notrait "Platform=Windows"
```
Expected: PASS — `AudioEnergyTests`, `WarmCaptureBufferTests`,
`WarmCaptureCoordinatorTests` (incl. the concurrency hammer), `0 errors`.

- [ ] **Run the broader non-Windows suite (regression guard)**

The other test projects are independent of the audio changes, but run them to
confirm nothing else regressed. For each pure-managed suite, build the `net9.0`
target and exec its dll excluding Windows traits:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for proj in Winpepper.Core.Tests Winpepper.Cleanup.Tests Winpepper.Corrections.Tests \
            Winpepper.History.Tests Winpepper.Asr.Tests Winpepper.Platform.Tests \
            Winpepper.Models.Tests Winpepper.IntegrationTests Winpepper.Audio.Tests; do
  dotnet build "tests/$proj/$proj.csproj" -f net9.0 -p:EnableWindowsTargeting=true \
    || { echo "BUILD FAILED: $proj"; break; }
  dotnet exec "tests/$proj/bin/Debug/net9.0/$proj.dll" -notrait "Platform=Windows" \
    || { echo "TESTS FAILED: $proj"; break; }
done
```
Expected: every project builds and its non-Windows tests pass. (Some projects
have only Windows-trait tests and will report 0 executed — that is fine.)

---

## Windows Smoke Test Checklist (run on a real Windows 11 machine)

These verify the `#if WINDOWS` NAudio adapters and WinUI wiring that cannot be
built or run on the Linux harness. Build the packaged app per the repo's normal
Windows build, then:

**Capture correctness & leak (findings #1, #5)**
- **S1 — Warm start:** launch with `PrewarmMicEnabled = true` (default). Confirm
  the Windows mic-in-use indicator turns on at launch and dictation captures the
  first ~500 ms (say a word the instant you press the hotkey; it appears).
- **S2 — Resampler leak gone:** with a non-16 kHz mic (e.g. 44.1/48 kHz), let the
  app sit warm+idle 10 minutes, then dictate repeatedly for 5 minutes. Watch the
  process in Task Manager / a COM-object or handle counter: memory/handles must
  stay flat (previously ~72k leaked resampler COM objects/hour). Confirm one
  resampler is built per format, not per callback.
- **S6 — Init-failure no leak:** start the app with the mic disabled in Windows
  Sound settings, then enable it and dictate. Repeat the disable/enable/dictate
  cycle ~10×. Confirm no handle/memory growth (Bug 5 — partial capture disposed
  on `Start()` throw) and that a fault toast appears while the device is absent.

**Silent-capture detection (finding #2)**
- **S3 — OS mute mid-session:** mute the mic via the OS/hardware, dictate a full
  session, release the hotkey. Confirm the toast "No audio detected — check your
  microphone / privacy settings." appears and an `Audio`-stage record lands on
  the Diagnostics page. **Also log/record the actual session RMS** in this muted
  state to confirm it lands below the −80 dBFS (`1e-4`) threshold on real hardware.
- **S4 — Privacy toggle:** turn off "Let apps access your microphone" in Windows
  Privacy settings, dictate a session. Confirm the same "No audio detected" toast
  and ErrorBus record. Re-enable and confirm normal capture resumes. **Threshold
  calibration:** also capture a normal live-but-idle (quiet room, mic on) session
  and log its RMS; confirm the idle-live floor stays ABOVE `1e-4` while muted/
  privacy-off stays below it — i.e. the −80 dBFS guard band holds on this device.
  If a very-low-input-gain + very-quiet-room combo lands a live session below the
  threshold, retune the constant (it is the single `AudioEnergy.SilenceRmsThreshold`).

**Fault & device recovery (findings #3, #4, #7)**
- **S5 — Rebuild race / teardown-vs-callback deadlock (unplug/swap mid-session,
  STRESS LOOP):** the adapter's `OnData`-vs-`Dispose` mutual exclusion (findings
  #4/#8) is the one concurrency guarantee the Linux hammer cannot reach, and its
  failure mode is an *intermittent* hang, not a deterministic fault — so a single
  unplug is NOT sufficient evidence. Run a repeated stress loop: with a USB mic,
  script/drive **≥50 cycles** of start-dictation → force a rebuild (physically
  unplug/replug or switch default) → dictate again, as fast as practical, with a
  watchdog/hang detector on the capture teardown (e.g. assert each
  `StopSession`/rebuild returns within a few seconds; log a stack dump if it does
  not). Confirm across all cycles: no hang/deadlock on rebuild; no crash / no
  `ObjectDisposedException` in the log; a capture-fault toast appears on unplug;
  and a subsequent dictation on the built-in mic works (ring cleared, no stale
  pre-roll audio bleeds in). **[real-hardware proof scenario]**
- **S7 — Change default device between sessions:** dictate on mic A, switch the
  Windows default input to mic B in Sound settings, dictate again. Confirm the
  second session records from mic B (per-session default recheck + rebuild) with
  no stale-device pre-roll.

**Dispose hygiene & reentrancy (finding #8)**
- **S8 — Level meter pages:** open the Recording settings "Test dictation" and the
  mic-picker level meter repeatedly (open/close 10×). Confirm no handle growth
  (Bug 8 — `WasapiRecorder.Dispose` now stops recording and unhooks
  `DataAvailable`).
- **S9 — Double TryStart:** trigger both `TryStart` call sites — boot with models
  present (AppShell.cs:321) and finish a model download on the Models tab
  (ModelsPage.xaml.cs:90). Confirm only one warm mic stream exists (one
  mic-in-use indicator, no duplicated meter frames).

**Disclosure copy (finding #9)**
- **S10 — Onboarding + settings copy:** run first-launch onboarding; confirm the
  mic step shows the "…the microphone stays warm so your first words are never
  clipped…" sentence. Open Settings → Recording; confirm the warm-mic toggle
  shows the resource-cost caption and tooltip.

**Real-hardware proof runs (explicitly out of automated scope — human must run)**
- **S11 — Unplug/swap mid-session** (covered operationally by S5): verify across
  at least one wired USB mic and one Bluetooth mic.
- **S12 — Sleep → resume → dictate:** put the machine to sleep mid-idle (warm),
  resume, and dictate. Confirm capture recovers (fault-driven rebuild or
  session-start restart) and no leak/crash.
- **S13 — RDP reconnect → dictate:** disconnect and reconnect an RDP session (audio
  endpoints churn), then dictate. Confirm capture recovers and the ring holds no
  stale audio.

**Consolidation note (deliberately deferred):** the four capture code paths
(`WarmWasapiRecorder`, `WasapiCaptureSource`, `WasapiRecorder`, and the per-page
meter usage) now share the `ICaptureSource` seam and the decode/downmix/resample
logic is duplicated only between `WasapiCaptureSource` and `WasapiRecorder`.
Fully consolidating them is a larger refactor that the council said to do only if
these fixes made it natural. They did not make it fully natural (the meter path
wants synchronous `Stop()` returning a buffer; the warm path wants the
coordinator), so a `TODO(consolidation)` marker is left in
`WarmWasapiRecorder.cs` (Task 8) and this remains a clean follow-up rather than
in-scope churn.
