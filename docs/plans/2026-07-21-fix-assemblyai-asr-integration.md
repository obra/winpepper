# Fix AssemblyAI Cloud-ASR Integration Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Remediate the six critical + five important defects a 6-lens council found in Winpepper's AssemblyAI cloud-ASR integration (robust polling, real timeout retry, sane budgets, transcript deletion, honest key testing, model validation, corrections vocabulary, cloud-cleanup skip), and separately fix a warm-mic capture-thread self-join deadlock.

**Architecture:** Push all decision logic *down* into the pure-managed, Linux-testable libraries (`Winpepper.Asr`, `Winpepper.Cleanup`, `Winpepper.Core`, `Winpepper.Audio`) where it can be driven by fake `HttpMessageHandler`s / fake seams and verified with the xUnit v3 in-process runner. Keep the WinUI `#if WINDOWS` wiring (`AppShell`, `RecordingPage`, `PipelineHost`) *thin* — it only assembles already-tested pieces and is verified by a Windows smoke checklist. Every HTTP call goes through one retry helper with one clearly-owned budget; a short cloud deadline (owned by `FallbackTranscriber`) races cloud vs. immediate local fallback.

**Tech Stack:** C# / .NET 9, xUnit v3 (in-process runner via `dotnet exec`), Shouldly, `System.Text.Json` + `System.Net.Http` (in-box), WinUI 3 (Windows-only, smoke-tested).

## Global Constraints

Every task's requirements implicitly include this section. Values are copied verbatim from the spec.

- **Do NOT touch** the keyboard hook (`Winpepper.Platform.Hotkeys` / hook code) or packaging (`packaging/`). Only `src/Winpepper.Asr/`, `src/Winpepper.Cleanup/`, `src/Winpepper.Core/Settings/`, `src/Winpepper.App/` (thin wiring), `src/Winpepper.Audio/` (separate task), and their test projects.
- **One budget, clearly owned.** Total cloud deadline default **10s** (setting `AssemblyAiCloudDeadlineSeconds`, clamp **5–30**), enforced via a `CancellationTokenSource` in `FallbackTranscriber`. Per-HTTP-request timeout **~8s** via a linked CTS inside `AssemblyAiClient` (NOT the global `HttpClient.Timeout`). Remove the uncoordinated 30s/45s double-knob.
- **First-poll grace:** wait **~750 ms** before the FIRST transcript poll, then poll at **~1 s**.
- **Retention/privacy:** issue `DELETE /v2/transcript/{id}` after a successful transcription. Setting `AssemblyAiDeleteAfterTranscribe` default **TRUE**.
- **Corrections vocabulary:** map `corrections.json` Replacements → AssemblyAI `custom_spelling` (`{from:[...], to: "..."}`) **unconditionally** (safe on all tiers). Preferred terms → `keyterms_prompt` **only behind a setting default OFF** (`AssemblyAiKeytermsEnabled`, may cost extra). **NEVER send `word_boost`** (silently downgrades universal-3 models).
- **Skip redundant local LLM cleanup for cloud results** (already server-side punctuated/formatted); run only the deterministic correction-only post-pass.
- **Known-good model ids:** `universal-2` (fast), `universal-3-pro` (premium). Freeform model ids are "Advanced/custom" and warn.
- **Never log the API key.** Log the transcript id on every request.
- **Retry-After** clamped to `[0, 30s]`.
- **Verification:** pure-managed tests on Linux via the xUnit v3 **in-process runner** (`dotnet exec <TestAssembly>.dll`). `dotnet test` (VSTest host) **crashes on this machine** — never use it. The .NET 9 SDK is provisioned into `./.dotnet/` (gitignored via `/.dotnet/`). WinUI changes stay thin and are covered by the Windows smoke checklist (§ end of plan).
- **Only `README.md`** is an end-user markdown doc; this plan under `docs/plans/` is a working/agent doc and is fine. Keep commits focused and atomic. The warm-mic self-join fix (Task 20) is a **separate commit** in an unrelated area.

---

## File Structure

**Created (pure-managed, `Winpepper.Asr`):**
- `src/Winpepper.Asr/Transcription/AssemblyAiRequests.cs` — `AssemblyAiCustomSpelling`, `AssemblyAiRequestExtras` request-shape records.
- `src/Winpepper.Asr/Transcription/AssemblyAiModels.cs` — known model ids, friendly labels, `IsKnown`.
- `src/Winpepper.Asr/Transcription/CloudProvider.cs` — `IsCloud(providerModelName)` prefix helper.
- `src/Winpepper.Asr/Transcription/AssemblyAiErrors.cs` — `IsInvalidModel(AssemblyAiException)`.
- `src/Winpepper.Asr/Transcription/CorrectionSpellingMapper.cs` — `CorrectionsData` → `AssemblyAiRequestExtras`.

**Modified (pure-managed):**
- `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs` — new budget/retention/keyterms knobs + clamp.
- `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs` — Retry-After clamp, `Random.Shared`, timeout-retry + per-request timeout, robust status parse, `custom_spelling`/`keyterms` payload, `DeleteTranscriptAsync`, `ValidateKeyAsync` via retry, id logging.
- `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs` — robust poll loop, first-poll grace, budget-by-`ct`, delete-after-success, extras wiring, id logging.
- `src/Winpepper.Asr/Transcription/FallbackTranscriber.cs` — cloud deadline CTS, invalid-model config-error callback.
- `src/Winpepper.Asr/Winpepper.Asr.csproj` — add `ProjectReference` to `Winpepper.Corrections`.
- `src/Winpepper.Cleanup/CleanupRunner.cs`, `src/Winpepper.Cleanup/CleanupResult.cs` — `skipLlm` bypass + `CleanupPath.BypassProvider`.
- `src/Winpepper.Core/Settings/AppSettings.cs` — 3 new settings.

**Modified (Windows-only, thin wiring — smoke-tested):**
- `src/Winpepper.App/Hosting/AppShell.cs` — remove `HttpClient.Timeout=30s`; wire extras/deadline/delete/config-error; pass provider to cleanup skip.
- `src/Winpepper.App/Hosting/PipelineHost.cs` — cloud-cleanup skip via `skipLlm`.
- `src/Winpepper.App/Views/RecordingPage.xaml` + `.xaml.cs` — model ComboBox, honest Test-key, privacy disclosure, provider label, keyterms toggle, config-error surface.

**Modified (pure-managed, separate task):**
- `src/Winpepper.Audio/WarmCaptureCoordinator.cs` — capture-thread self-join dispose scheduler seam.
- `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs` — thread-id seam for the self-join test.

**Test files touched:** `tests/Winpepper.Asr.Tests/*`, `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`, `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`, `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`.

---

## Task 0: Provision the .NET 9 SDK

**Files:** none committed (SDK lands in gitignored `./.dotnet/`).

**Interfaces:**
- Produces: a working `dotnet` at `./.dotnet/dotnet`, reached via `DOTNET_ROOT`/`PATH`. Every later task's test steps begin by re-exporting these.

- [ ] **Step 1: Provision the SDK**

Run from the worktree root (`/home/dan/code/winpepper/.worktrees/fix-assemblyai-asr-integration`):
```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

- [ ] **Step 2: Verify dotnet resolves**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a `latestFeature` roll-forward like `9.0.1xx`).

- [ ] **Step 3: Confirm the Asr suite builds and is green at baseline**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll
```
Expected: build succeeds; test run ends `Failed: 0`. (No commit — SDK is gitignored.)

---

## Task 1: New AppSettings fields (delete / deadline / keyterms)

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs:19` (insert after `AssemblyAiModel`)
- Test: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`

**Interfaces:**
- Produces: `AppSettings.AssemblyAiDeleteAfterTranscribe` (bool, default `true`), `AppSettings.AssemblyAiCloudDeadlineSeconds` (int, default `10`), `AppSettings.AssemblyAiKeytermsEnabled` (bool, default `false`). Consumed by Task 17/18/19 wiring.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` (inside the existing `AppSettingsDefaultsTests` class):
```csharp
    [Fact]
    public void AssemblyAi_Retention_Deadline_Keyterms_Defaults()
    {
        var s = new AppSettings();
        s.AssemblyAiDeleteAfterTranscribe.ShouldBeTrue();     // privacy default: delete
        s.AssemblyAiCloudDeadlineSeconds.ShouldBe(10);        // single owned budget
        s.AssemblyAiKeytermsEnabled.ShouldBeFalse();          // opt-in, may cost extra
    }
```
(If the file lacks `using Shouldly;` / `using Winpepper.Core.Settings;` / `using Xunit;`, add them at the top.)

- [ ] **Step 2: Run the test and verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.AppSettingsDefaultsTests.AssemblyAi_Retention_Deadline_Keyterms_Defaults"
```
Expected: FAIL — build error `CS0117: 'AppSettings' does not contain a definition for 'AssemblyAiDeleteAfterTranscribe'`.

- [ ] **Step 3: Add the settings fields**

In `src/Winpepper.Core/Settings/AppSettings.cs`, immediately after line 19 (`public string AssemblyAiModel ...`), insert:
```csharp

    // AssemblyAI retention: delete the remote transcript after we have the text
    // so dictated audio/text does not persist on AssemblyAI servers. On by default.
    public bool AssemblyAiDeleteAfterTranscribe { get; init; } = true;

    // Single owned cloud budget (seconds). FallbackTranscriber cancels the cloud
    // attempt after this and falls back to local immediately. Clamped to [5,30].
    public int AssemblyAiCloudDeadlineSeconds { get; init; } = 10;

    // Send Preferred terms as AssemblyAI keyterms_prompt. Off by default: this is
    // a paid add-on on some tiers. Replacements always map to custom_spelling
    // (safe on all tiers) regardless of this flag.
    public bool AssemblyAiKeytermsEnabled { get; init; } = false;
```

- [ ] **Step 4: Run the test and verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -method "Winpepper.Core.Tests.AppSettingsDefaultsTests.AssemblyAi_Retention_Deadline_Keyterms_Defaults"
```
Expected: PASS. `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Settings/AppSettings.cs tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs
git commit -m "feat(settings): add AssemblyAI delete/deadline/keyterms settings"
```

---

## Task 2: AssemblyAiOptions budget/retention/keyterms knobs + deadline clamp

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiOptionsTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `AssemblyAiOptions` with new members `CloudDeadline` (`TimeSpan`, default 10s), `PerRequestTimeout` (`TimeSpan`, default 8s), `FirstPollDelay` (`TimeSpan`, default 750ms), `DeleteAfterTranscribe` (`bool`, default true), `KeytermsEnabled` (`bool`, default false), and `static TimeSpan ClampDeadline(int seconds)` clamping to `[5,30]`. **Keep** existing `BaseUrl`, `Model`, `LanguageCode`, `PollInterval`, `MaxTransientRetries`, and (for now) `TotalTimeout` — `TotalTimeout` is removed in Task 14 once the transcriber no longer reads it.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/AssemblyAiOptionsTests.cs`:
```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiOptionsTests
{
    [Fact]
    public void Defaults_MatchSingleOwnedBudget()
    {
        var o = new AssemblyAiOptions();
        o.CloudDeadline.ShouldBe(TimeSpan.FromSeconds(10));
        o.PerRequestTimeout.ShouldBe(TimeSpan.FromSeconds(8));
        o.FirstPollDelay.ShouldBe(TimeSpan.FromMilliseconds(750));
        o.PollInterval.ShouldBe(TimeSpan.FromSeconds(1));
        o.DeleteAfterTranscribe.ShouldBeTrue();
        o.KeytermsEnabled.ShouldBeFalse();
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, 5)]     // below floor -> 5
    [InlineData(3, 5)]     // below floor -> 5
    [InlineData(45, 30)]   // above ceiling -> 30
    [InlineData(-7, 5)]    // negative -> 5
    public void ClampDeadline_KeepsWithin5To30(int input, int expected)
        => AssemblyAiOptions.ClampDeadline(input).ShouldBe(TimeSpan.FromSeconds(expected));
}
```

- [ ] **Step 2: Run the test and verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — `CS0117: 'AssemblyAiOptions' does not contain a definition for 'CloudDeadline'`.

- [ ] **Step 3: Add the option members and clamp**

Replace the entire body of `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs` with:
```csharp
namespace Winpepper.Asr.Transcription;

public sealed class AssemblyAiOptions
{
    public string BaseUrl { get; init; } = "https://api.assemblyai.com";
    public string Model { get; init; } = "universal-2";
    public string LanguageCode { get; init; } = "en_us";

    // Single owned cloud budget. FallbackTranscriber cancels the cloud attempt
    // after CloudDeadline; the client caps each HTTP request at PerRequestTimeout
    // via a linked CTS (NOT the global HttpClient.Timeout).
    public TimeSpan CloudDeadline { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PerRequestTimeout { get; init; } = TimeSpan.FromSeconds(8);

    // Clips take at least ~750 ms to enter processing; wait before the first poll,
    // then poll at PollInterval.
    public TimeSpan FirstPollDelay { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxTransientRetries { get; init; } = 3;

    // Retention: delete the remote transcript after success.
    public bool DeleteAfterTranscribe { get; init; } = true;

    // Send Preferred terms as keyterms_prompt (paid add-on on some tiers). Off by default.
    public bool KeytermsEnabled { get; init; } = false;

    // TODO(remove in Task 14): legacy wall-clock cap; kept only so the current
    // transcriber compiles until it switches to the ct-owned budget.
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Clamp a user-supplied cloud-deadline seconds value to [5, 30].</summary>
    public static TimeSpan ClampDeadline(int seconds)
        => TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 30));
}
```

- [ ] **Step 4: Run the test and verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -class "Winpepper.Asr.Tests.AssemblyAiOptionsTests"
```
Expected: PASS. Also run the full suite `dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll` → `Failed: 0` (existing tests still compile — `TotalTimeout` retained).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs tests/Winpepper.Asr.Tests/AssemblyAiOptionsTests.cs
git commit -m "feat(asr): add AssemblyAI budget/retention/keyterms options + deadline clamp"
```

---

## Task 3: Clamp Retry-After to [0, 30s]

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:183-189` (`RetryAfter`)
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: `AssemblyAiOptions` (Task 2).
- Produces: `AssemblyAiClient` no longer throws `ArgumentOutOfRangeException` from `Task.Delay` on garbage `Retry-After`; the honored delay is always within `[0s, 30s]`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs` (inside the class):
```csharp
    [Theory]
    [InlineData("-5", 0)]        // negative -> clamped to 0
    [InlineData("99999", 30)]    // huge -> clamped to 30
    [InlineData("banana", null)] // non-numeric -> ignored, falls back to backoff (>0)
    public async Task Upload_429_ClampsGarbageRetryAfter(string headerValue, int? expectedSeconds)
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}", mutate: r => r.Headers.TryAddWithoutValidation("Retry-After", headerValue))
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        delays.Count.ShouldBe(1);
        delays[0].ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        delays[0].ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        if (expectedSeconds is int s)
            delays[0].ShouldBe(TimeSpan.FromSeconds(s));
        else
            delays[0].ShouldBeGreaterThan(TimeSpan.Zero); // non-numeric -> backoff jitter
    }
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -method "Winpepper.Asr.Tests.AssemblyAiClientTests.Upload_429_ClampsGarbageRetryAfter"
```
Expected: FAIL — the `-5` case throws `ArgumentOutOfRangeException` (negative `TimeSpan` to `Task.Delay`); the `99999` case yields 99999s, not 30s.

- [ ] **Step 3: Clamp in `RetryAfter`**

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, replace the `RetryAfter` method (lines 183-190) with:
```csharp
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        TimeSpan? raw = null;
        if (resp.Headers.RetryAfter?.Delta is { } delta) raw = delta;
        else if (resp.Headers.TryGetValues("Retry-After", out var values)
                 && int.TryParse(values.FirstOrDefault(), out var seconds))
            raw = TimeSpan.FromSeconds(seconds);

        if (raw is null) return null;
        // Clamp defensively: a negative value would throw from Task.Delay, and a
        // huge value would freeze dictation past any sane budget.
        var clamped = raw.Value;
        if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;
        if (clamped > MaxRetryAfter) clamped = MaxRetryAfter;
        return clamped;
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS (including the pre-existing `Upload_429_HonorsRetryAfterThenSucceeds`).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "fix(asr): clamp AssemblyAI Retry-After to [0,30s]"
```

---

## Task 4: Use Random.Shared instead of the unlocked shared Random field

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:32` (field) and `:181` (`Backoff`)
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `AssemblyAiClient.Backoff` jitter uses `Random.Shared` (thread-safe); the `_rng` instance field is removed.

- [ ] **Step 1: Write the failing test (guards backoff bounds after refactor)**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:
```csharp
    [Fact]
    public async Task Upload_503_RetriesUpToMax_ThenThrows_WithMonotonicBackoff()
    {
        // 4 consecutive 503s with MaxTransientRetries=3 -> 3 delays, then throw.
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => client.UploadAsync(new byte[] { 1 }, CancellationToken.None));
        ex.StatusCode.ShouldBe(503);
        delays.Count.ShouldBe(3);                      // exactly MaxTransientRetries delays
        foreach (var d in delays) d.ShouldBeGreaterThan(TimeSpan.Zero);
    }
```

- [ ] **Step 2: Run the test and verify it fails or passes-for-wrong-reason**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -method "Winpepper.Asr.Tests.AssemblyAiClientTests.Upload_503_RetriesUpToMax_ThenThrows_WithMonotonicBackoff"
```
Expected: This test PASSES already (it documents current retry-exhaustion behavior and guards it through the refactor). If it does not, stop and reconcile before editing `Backoff`.

- [ ] **Step 3: Switch to Random.Shared**

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`:
1. Delete the field at line 32: `    private readonly Random _rng = new();`
2. Replace the `Backoff` method (around line 180-181) with:
```csharp
    private static TimeSpan Backoff(int attempt)
        // exponential + jitter; Random.Shared is thread-safe (no shared unlocked field)
        => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 250 + Random.Shared.Next(0, 250));
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "fix(asr): use Random.Shared for backoff jitter + guard retry exhaustion"
```

---

## Task 5: Retry on request timeout (not caller cancel) + per-request timeout

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:122-178` (`SendWithRetryAsync`)
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`, `tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs`

**Interfaces:**
- Consumes: `AssemblyAiOptions.PerRequestTimeout` (Task 2).
- Produces: `SendWithRetryAsync` (a) caps each HTTP send with a per-request linked CTS (`PerRequestTimeout`), (b) treats `TaskCanceledException`/`OperationCanceledException` NOT caused by the caller's token as a retryable transient, (c) re-throws immediately when the caller's `ct` is cancelled.

- [ ] **Step 1: Extend the fake handler to script throwing behaviors**

In `tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs`, add an overload that enqueues a *thrown* exception, and honor request cancellation. Replace the class body with:
```csharp
using System.Net;

namespace Winpepper.Asr.Tests;

/// <summary>Queues scripted responses/throws and records every request for assertions.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<byte[]> RequestBodies { get; } = new();

    public FakeHttpMessageHandler Enqueue(HttpStatusCode status, string body, string contentType = "application/json",
        Action<HttpResponseMessage>? mutate = null)
    {
        _responses.Enqueue(_ =>
        {
            var resp = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
            };
            mutate?.Invoke(resp);
            return resp;
        });
        return this;
    }

    /// <summary>Enqueue a send that throws (e.g. a per-request timeout as TaskCanceledException).</summary>
    public FakeHttpMessageHandler EnqueueThrow(Exception ex)
    {
        _responses.Enqueue(_ => throw ex);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken));
        // Honor the (possibly linked) per-request token so a triggered timeout surfaces as cancellation.
        cancellationToken.ThrowIfCancellationRequested();
        if (_responses.Count == 0) throw new InvalidOperationException("No scripted response left.");
        return _responses.Dequeue()(request);
    }
}
```

- [ ] **Step 2: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:
```csharp
    [Fact]
    public async Task Upload_RequestTimeout_IsRetriedThenSucceeds()
    {
        // First send throws TaskCanceledException NOT tied to the caller token
        // (models a per-request timeout); second send succeeds.
        var handler = new FakeHttpMessageHandler()
            .EnqueueThrow(new TaskCanceledException("per-request timeout"))
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1); // one backoff before the retry
    }

    [Fact]
    public async Task Upload_CallerCancellation_IsNotRetried()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new FakeHttpMessageHandler(); // no scripted responses needed
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        await Should.ThrowAsync<OperationCanceledException>(
            () => client.UploadAsync(new byte[] { 1 }, cts.Token));
        delays.Count.ShouldBe(0);       // never retried
        handler.Requests.Count.ShouldBeLessThanOrEqualTo(1);
    }
```

- [ ] **Step 3: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -method "Winpepper.Asr.Tests.AssemblyAiClientTests.Upload_RequestTimeout_IsRetriedThenSucceeds"
```
Expected: FAIL — `Upload_RequestTimeout_IsRetriedThenSucceeds` throws `TaskCanceledException` (current code only catches `HttpRequestException`).

- [ ] **Step 4: Rewrite `SendWithRetryAsync` with per-request timeout + timeout-retry**

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, replace the whole `SendWithRetryAsync` method (lines 122-178) with:
```csharp
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var req = requestFactory();
            AddAuth(req);

            // Cap each HTTP round-trip independently of the global HttpClient.Timeout.
            using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            reqCts.CancelAfter(_opts.PerRequestTimeout);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, reqCts.Token);
            }
            catch (Exception ex) when (
                (ex is HttpRequestException ||
                 // A per-request timeout surfaces as (Task)OperationCanceledException whose
                 // cause is reqCts, NOT the caller's ct. Treat that as a retryable transient.
                 (ex is OperationCanceledException && !ct.IsCancellationRequested))
                && attempt <= _opts.MaxTransientRetries)
            {
                await _delay(Backoff(attempt), ct);
                continue;
            }
            // Caller aborted (ct cancelled): let the OperationCanceledException propagate.

            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return resp;

            if (code == 401)
            {
                resp.Dispose();
                throw new AssemblyAiException("AssemblyAI rejected the API key (401). Check your key.", 401, isAuthError: true);
            }
            if (code is 400 or 404)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                resp.Dispose();
                throw new AssemblyAiException($"AssemblyAI request failed ({code}): {body}", code);
            }
            if (code == 429)
            {
                var wait = RetryAfter(resp) ?? Backoff(attempt);
                resp.Dispose();
                if (attempt > _opts.MaxTransientRetries)
                    throw new AssemblyAiException("AssemblyAI rate limit (429) exceeded retries.", 429);
                await _delay(wait, ct);
                continue;
            }
            if (code is 500 or 502 or 503 or 504)
            {
                resp.Dispose();
                if (attempt > _opts.MaxTransientRetries)
                    throw new AssemblyAiException($"AssemblyAI server error ({code}) exceeded retries.", code);
                await _delay(Backoff(attempt), ct);
                continue;
            }

            var other = await resp.Content.ReadAsStringAsync(ct);
            resp.Dispose();
            throw new AssemblyAiException($"AssemblyAI unexpected status ({code}): {other}", code);
        }
    }
```

- [ ] **Step 5: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS, `Failed: 0` (all prior client tests plus the two new ones).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs
git commit -m "fix(asr): retry request timeouts + per-request timeout, never retry caller cancel"
```

---

## Task 6: Error-status coverage + robust status parsing on GET

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:89-103` (`GetTranscriptAsync`)
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `GetTranscriptAsync` reads `status` tolerantly across JSON kinds (string/number/other) so a non-string status can never silently drop a completed transcript; error statuses on create/get throw `AssemblyAiException` with the code.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:
```csharp
    [Fact]
    public async Task GetTranscript_NonStringStatus_IsCoercedNotDropped()
    {
        // Defensive: even if status arrives as a JSON number, we must surface it
        // (as "123") rather than silently returning empty and burning the budget.
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"status\":123,\"text\":\"hi\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var tr = await client.GetTranscriptAsync("t-1", CancellationToken.None);
        tr.Status.ShouldBe("123");
        tr.Text.ShouldBe("hi");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task GetTranscript_ClientError_Throws(int code)
    {
        var handler = new FakeHttpMessageHandler().Enqueue((HttpStatusCode)code, "{\"error\":\"nope\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => client.GetTranscriptAsync("t-1", CancellationToken.None));
        ex.StatusCode.ShouldBe(code);
    }
```
> The `CreateTranscriptAsync` 401/error-status test lives in Task 7 (it needs Task 7's `AssemblyAiRequestExtras` signature). Keep Task 6 focused on the GET path so this task compiles and reaches green on its own.

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -method "Winpepper.Asr.Tests.AssemblyAiClientTests.GetTranscript_NonStringStatus_IsCoercedNotDropped"
```
Expected: FAIL — current `GetTranscriptAsync` returns `Status=""` for a numeric status.

- [ ] **Step 3: Make status parsing tolerant**

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, replace `GetTranscriptAsync` (lines 89-103) with:
```csharp
    public async Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/{id}"), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AssemblyAiTranscript(
            Status: ReadStatus(root),
            Text: root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            Confidence: root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
            AudioDuration: root.TryGetProperty("audio_duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
            Error: root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }

    /// <summary>
    /// Read `status` tolerant of JSON kind. AssemblyAI sends a string, but a
    /// non-string value must never silently drop a completed transcript: coerce
    /// numbers/bools to their raw text and let the transcriber's poll loop treat
    /// an unrecognized status explicitly.
    /// </summary>
    private static string ReadStatus(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var s)) return "";
        return s.ValueKind switch
        {
            JsonValueKind.String => s.GetString() ?? "",
            JsonValueKind.Null => "",
            JsonValueKind.Number => s.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => s.GetRawText(),
            _ => s.GetRawText(),
        };
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS — the two new GET tests and all pre-existing client tests. `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "fix(asr): tolerant status parsing + error-status coverage on transcript GET"
```

---

## Task 7: custom_spelling + keyterms payload (never word_boost)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiRequests.cs`
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:11-17` (interface), `:63-87` (`CreateTranscriptAsync`)
- Modify: `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs` (interface member signature), `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `public sealed record AssemblyAiCustomSpelling(IReadOnlyList<string> From, string To);`
  - `public sealed record AssemblyAiRequestExtras(IReadOnlyList<AssemblyAiCustomSpelling> CustomSpelling, IReadOnlyList<string> Keyterms) { public static AssemblyAiRequestExtras Empty { get; } = new(Array.Empty<AssemblyAiCustomSpelling>(), Array.Empty<string>()); }`
  - `IAssemblyAiClient.CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct)` — payload includes `custom_spelling` (always, when non-empty) and `keyterms_prompt` (only when `extras.Keyterms` non-empty). **Never** emits `word_boost`.

- [ ] **Step 1: Create the request-shape records**

Create `src/Winpepper.Asr/Transcription/AssemblyAiRequests.cs`:
```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>One AssemblyAI custom_spelling rule: map misheard forms to the correct text.</summary>
public sealed record AssemblyAiCustomSpelling(IReadOnlyList<string> From, string To);

/// <summary>
/// Optional per-request vocabulary. CustomSpelling is safe on all tiers and is
/// always sent when non-empty. Keyterms maps to keyterms_prompt and is only sent
/// when the user opts in (paid add-on on some tiers). word_boost is intentionally
/// absent: it silently downgrades universal-3 models.
/// </summary>
public sealed record AssemblyAiRequestExtras(
    IReadOnlyList<AssemblyAiCustomSpelling> CustomSpelling,
    IReadOnlyList<string> Keyterms)
{
    public static AssemblyAiRequestExtras Empty { get; } =
        new(Array.Empty<AssemblyAiCustomSpelling>(), Array.Empty<string>());
}
```

- [ ] **Step 2: Write the failing tests**

Replace the existing `CreateTranscript_SendsSpeechModelPayload_ReturnsId` test in `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs` with:
```csharp
    [Fact]
    public async Task CreateTranscript_SendsSpeechModelPayload_NoVocab_NoWordBoost()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"t-123\",\"status\":\"queued\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var id = await client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-2",
            AssemblyAiRequestExtras.Empty, CancellationToken.None);

        id.ShouldBe("t-123");
        var json = Encoding.UTF8.GetString(handler.RequestBodies[0]);
        json.ShouldContain("\"speech_models\":[\"universal-2\"]");
        json.ShouldContain("\"audio_url\":\"https://cdn/aai/ok\"");
        json.ShouldContain("\"format_text\":true");
        json.ShouldContain("\"punctuate\":true");
        json.ShouldContain("\"disfluencies\":false");
        json.ShouldContain("\"language_code\":\"en_us\"");
        json.ShouldNotContain("word_boost");        // never
        json.ShouldNotContain("custom_spelling");   // empty extras -> omitted
        json.ShouldNotContain("keyterms_prompt");   // empty extras -> omitted
    }

    [Fact]
    public async Task CreateTranscript_MapsCustomSpelling_AndKeyterms()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"t-9\",\"status\":\"queued\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var extras = new AssemblyAiRequestExtras(
            new[] { new AssemblyAiCustomSpelling(new[] { "kubernetes", "kubernettes" }, "Kubernetes") },
            new[] { "Winpepper" });

        await client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-3-pro", extras, CancellationToken.None);

        var json = Encoding.UTF8.GetString(handler.RequestBodies[0]);
        json.ShouldContain("\"custom_spelling\":[{\"from\":[\"kubernetes\",\"kubernettes\"],\"to\":\"Kubernetes\"}]");
        json.ShouldContain("\"keyterms_prompt\":[\"Winpepper\"]");
        json.ShouldNotContain("word_boost");
    }

    [Fact]
    public async Task CreateTranscript_401_ThrowsAuthError()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(
            () => client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-2", AssemblyAiRequestExtras.Empty, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
        ex.StatusCode.ShouldBe(401);
    }
```

- [ ] **Step 3: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — build error: `CreateTranscriptAsync` has no 4-arg overload; `FakeAssemblyAiClient` doesn't match the new interface.

- [ ] **Step 4: Update interface, client payload, and the fake**

4a. In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, change the interface member (line 14):
```csharp
    Task<string> CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct);
```
4b. Replace `CreateTranscriptAsync` (lines 63-87) with:
```csharp
    public async Task<string> CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct)
    {
        // Build the payload as a mutable dictionary so custom_spelling / keyterms_prompt
        // are only present when non-empty. NEVER add word_boost (downgrades universal-3).
        var payload = new Dictionary<string, object?>
        {
            ["audio_url"] = audioUrl,
            ["speech_models"] = new[] { model }, // plural array — singular `speech_model` is deprecated
            ["format_text"] = true,
            ["punctuate"] = true,
            ["disfluencies"] = false,
            ["language_code"] = _opts.LanguageCode,
        };
        if (extras.CustomSpelling.Count > 0)
            payload["custom_spelling"] = extras.CustomSpelling
                .Select(cs => new { from = cs.From, to = cs.To })
                .ToArray();
        if (extras.Keyterms.Count > 0)
            payload["keyterms_prompt"] = extras.Keyterms.ToArray();

        var body = JsonSerializer.Serialize(payload);

        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/transcript")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return req;
        }, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ReadString(json, "id");
    }
```
4c. In `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`, update the `CreateTranscriptAsync` signature and record the extras:
```csharp
    public AssemblyAiRequestExtras? LastExtras { get; private set; }

    public Task<string> CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct)
    {
        LastExtras = extras;
        return Task.FromResult("t-fake");
    }
```
(Replace the existing 3-arg `CreateTranscriptAsync` method; add the `LastExtras` property near `PollCalls`.)

4d. Keep the production transcriber compiling: `AssemblyAiTranscriber.cs:49` still calls the old 3-arg `CreateTranscriptAsync`. Update that one call site to pass empty extras (the real extras provider is wired in Task 14). In `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs`, change:
```csharp
            var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, token);
```
to:
```csharp
            var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, AssemblyAiRequestExtras.Empty, token);
```

> The serialized `custom_spelling` key order (`from` then `to`) is guaranteed by the anonymous type member order. If a future serializer setting reorders keys, relax the two `ShouldContain` asserts to check `custom_spelling`, `"to":"Kubernetes"`, and `"from":["kubernetes"` separately.

- [ ] **Step 5: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll
```
Expected: PASS, `Failed: 0` (whole suite — including the new `CreateTranscript_401_ThrowsAuthError` and the custom_spelling/keyterms payload tests).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiRequests.cs src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "feat(asr): send custom_spelling always + keyterms opt-in, never word_boost"
```

---

## Task 8: DeleteTranscriptAsync (retention)

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:11-17` (interface) + add method
- Modify: `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`, `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `IAssemblyAiClient.DeleteTranscriptAsync(string id, CancellationToken ct)` — issues `DELETE {BaseUrl}/v2/transcript/{id}` through the retry helper with auth.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:
```csharp
    [Fact]
    public async Task DeleteTranscript_IssuesDeleteWithAuth()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "{\"status\":\"deleted\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        await client.DeleteTranscriptAsync("t-42", CancellationToken.None);

        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Delete);
        req.RequestUri!.ToString().ShouldEndWith("/v2/transcript/t-42");
        req.Headers.GetValues("authorization").ShouldContain("KEY");
    }
```

- [ ] **Step 2: Run the test and verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — build error: `IAssemblyAiClient` has no `DeleteTranscriptAsync`; `FakeAssemblyAiClient` no longer implements the interface.

- [ ] **Step 3: Add the interface member, client impl, and fake impl**

3a. In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, add to the interface (after `ValidateKeyAsync`):
```csharp
    Task DeleteTranscriptAsync(string id, CancellationToken ct);
```
3b. Add the implementation to `AssemblyAiClient` (e.g. after `GetTranscriptAsync`):
```csharp
    public async Task DeleteTranscriptAsync(string id, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{_opts.BaseUrl}/v2/transcript/{id}"), ct);
        // Body is irrelevant; a 2xx means the remote transcript is gone. Log id, never the key.
        _log.LogInformation("AssemblyAI transcript {Id} deleted", id);
    }
```
3c. In `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`, add:
```csharp
    public List<string> Deleted { get; } = new();
    public Task DeleteTranscriptAsync(string id, CancellationToken ct)
    {
        Deleted.Add(id);
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS, `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "feat(asr): add DeleteTranscriptAsync for post-transcription retention cleanup"
```

---

## Task 9: Route ValidateKeyAsync through the retry helper

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:105-112` (`ValidateKeyAsync`)
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: `SendWithRetryAsync` (Task 5).
- Produces: `ValidateKeyAsync` retries transient failures (5xx / request timeout) before deciding; `401 → false`, any other accepted response (incl. `404`) → `true`.

- [ ] **Step 1: Write the failing test**

Append to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:
```csharp
    [Fact]
    public async Task ValidateKey_RetriesTransient_ThenReturnsTrueOn404()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")            // transient, retried
            .Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not found\"}"); // 404 => valid key
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        (await client.ValidateKeyAsync(CancellationToken.None)).ShouldBeTrue();
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1);
    }
```
(The existing `ValidateKey_404MeansValid_401MeansBadKey` test must continue to pass.)

- [ ] **Step 2: Run the test and verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -method "Winpepper.Asr.Tests.AssemblyAiClientTests.ValidateKey_RetriesTransient_ThenReturnsTrueOn404"
```
Expected: FAIL — current `ValidateKeyAsync` calls `_http.SendAsync` directly (no retry): only 1 request, 0 delays.

- [ ] **Step 3: Reroute through the retry helper**

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`, replace `ValidateKeyAsync` (lines 105-112) with:
```csharp
    public async Task<bool> ValidateKeyAsync(CancellationToken ct)
    {
        // GET a bogus id through the retry helper: 401 => bad key; anything the
        // helper accepts (or a terminal 404) => key is accepted. Transient 5xx /
        // request-timeouts are retried before we decide.
        try
        {
            using var resp = await SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/winpepper-key-check-000000000000"), ct);
            return true; // 2xx accepted
        }
        catch (AssemblyAiException ex)
        {
            // 401 => invalid; 404 (bogus id) and other non-auth terminals => key was accepted.
            return ex.StatusCode != 401;
        }
    }
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
Expected: PASS — both the new retry test and `ValidateKey_404MeansValid_401MeansBadKey`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "fix(asr): route ValidateKeyAsync through retry helper for transient resilience"
```

---

## Task 10: CorrectionSpellingMapper (corrections → request extras)

**Files:**
- Modify: `src/Winpepper.Asr/Winpepper.Asr.csproj` (add `ProjectReference`)
- Create: `src/Winpepper.Asr/Transcription/CorrectionSpellingMapper.cs`
- Test: `tests/Winpepper.Asr.Tests/CorrectionSpellingMapperTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Corrections.CorrectionsData` (Replacements `wrong→right`, Preferred `string[]`); `AssemblyAiRequestExtras`/`AssemblyAiCustomSpelling` (Task 7).
- Produces: `static AssemblyAiRequestExtras CorrectionSpellingMapper.ToExtras(CorrectionsData data, bool includeKeyterms)` — each `Replacements[wrong]=right` → `AssemblyAiCustomSpelling(From:[wrong], To:right)`; `Preferred` → `Keyterms` only when `includeKeyterms`.

- [ ] **Step 1: Add the project reference**

In `src/Winpepper.Asr/Winpepper.Asr.csproj`, inside the existing `<ItemGroup>` that contains the `Winpepper.Core` ProjectReference, add:
```xml
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/CorrectionSpellingMapperTests.cs`:
```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorrectionSpellingMapperTests
{
    [Fact]
    public void Replacements_MapToCustomSpelling_KeytermsOffByDefault()
    {
        var data = CorrectionsData.Empty with
        {
            Replacements = new Dictionary<string, string> { ["kubernettes"] = "Kubernetes", ["winpeper"] = "Winpepper" },
            Preferred = new[] { "Amplifier" },
        };

        var extras = CorrectionSpellingMapper.ToExtras(data, includeKeyterms: false);

        extras.CustomSpelling.Count.ShouldBe(2);
        extras.CustomSpelling.ShouldContain(cs => cs.To == "Kubernetes" && cs.From.Count == 1 && cs.From[0] == "kubernettes");
        extras.CustomSpelling.ShouldContain(cs => cs.To == "Winpepper" && cs.From[0] == "winpeper");
        extras.Keyterms.ShouldBeEmpty(); // opt-in
    }

    [Fact]
    public void Keyterms_IncludedWhenEnabled()
    {
        var data = CorrectionsData.Empty with { Preferred = new[] { "Amplifier", "Winpepper" } };
        var extras = CorrectionSpellingMapper.ToExtras(data, includeKeyterms: true);
        extras.Keyterms.ShouldBe(new[] { "Amplifier", "Winpepper" });
    }

    [Fact]
    public void Empty_YieldsEmptyExtras()
    {
        var extras = CorrectionSpellingMapper.ToExtras(CorrectionsData.Empty, includeKeyterms: true);
        extras.CustomSpelling.ShouldBeEmpty();
        extras.Keyterms.ShouldBeEmpty();
    }
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — `CorrectionSpellingMapper` does not exist.

- [ ] **Step 4: Implement the mapper**

Create `src/Winpepper.Asr/Transcription/CorrectionSpellingMapper.cs`:
```csharp
using Winpepper.Corrections;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Maps the user's corrections vocabulary into AssemblyAI request extras.
/// Replacements always become custom_spelling (safe on all tiers). Preferred
/// terms become keyterms_prompt only when the caller opts in (paid on some tiers).
/// </summary>
public static class CorrectionSpellingMapper
{
    public static AssemblyAiRequestExtras ToExtras(CorrectionsData data, bool includeKeyterms)
    {
        var spelling = data.Replacements
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .Select(kv => new AssemblyAiCustomSpelling(new[] { kv.Key }, kv.Value))
            .ToArray();

        var keyterms = includeKeyterms
            ? data.Preferred.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray()
            : Array.Empty<string>();

        if (spelling.Length == 0 && keyterms.Length == 0) return AssemblyAiRequestExtras.Empty;
        return new AssemblyAiRequestExtras(spelling, keyterms);
    }
}
```

- [ ] **Step 5: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.CorrectionSpellingMapperTests"
```
Expected: PASS. Also run the full suite → `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Winpepper.Asr.csproj src/Winpepper.Asr/Transcription/CorrectionSpellingMapper.cs tests/Winpepper.Asr.Tests/CorrectionSpellingMapperTests.cs
git commit -m "feat(asr): map corrections vocabulary to AssemblyAI custom_spelling/keyterms"
```

---

## Task 11: Known model ids + friendly labels + validation

**Files:**
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiModels.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `AssemblyAiModels.Known` — ordered list of `(string Id, string Label)`: `("universal-2","universal-2 (fast)")`, `("universal-3-pro","universal-3-pro (premium)")`.
  - `AssemblyAiModels.DefaultId => "universal-2"`.
  - `bool AssemblyAiModels.IsKnown(string id)` (ordinal, case-insensitive) — recognizes the displayed ids PLUS the alias `universal-3-5-pro` (see model-id note below).
  Consumed by Task 18 (model ComboBox).

> **Model-id note (verified against AssemblyAI docs 2026-07):** `universal-2` is confirmed valid. The premium id is **ambiguous across official sources** — the API-reference `speech_models` enum spells it `universal-3-5-pro`, while the pricing page and Python SDK use `universal-3-pro`. This could not be resolved without a live authed call. We keep `universal-3-pro` as the displayed premium id but ALSO treat `universal-3-5-pro` as *known* so neither spelling is mis-flagged as "custom". A wrong premium id degrades gracefully: the request 400s → `AssemblyAiErrors.IsInvalidModel` surfaces a config error → `FallbackTranscriber` falls back to local, so the user still gets text. The request payload already sends the id in the **plural `speech_models` array** (Task 7), matching the current (non-deprecated) API field.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs`:
```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiModelsTests
{
    [Fact]
    public void Known_ListsFastAndPremium_InOrder()
    {
        AssemblyAiModels.Known.Select(m => m.Id).ShouldBe(new[] { "universal-2", "universal-3-pro" });
        AssemblyAiModels.Known[0].Label.ShouldContain("fast");
        AssemblyAiModels.Known[1].Label.ShouldContain("premium");
    }

    [Theory]
    [InlineData("universal-2", true)]
    [InlineData("UNIVERSAL-3-PRO", true)]     // case-insensitive
    [InlineData("universal-3-5-pro", true)]   // API-reference spelling accepted as alias
    [InlineData("universal-9000", false)]     // typo -> not known
    [InlineData("", false)]
    public void IsKnown_RecognizesGoodIds(string id, bool expected)
        => AssemblyAiModels.IsKnown(id).ShouldBe(expected);

    [Fact]
    public void DefaultId_IsUniversal2()
        => AssemblyAiModels.DefaultId.ShouldBe("universal-2");
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — `AssemblyAiModels` does not exist.

- [ ] **Step 3: Implement the model catalog**

Create `src/Winpepper.Asr/Transcription/AssemblyAiModels.cs`:
```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Known-good AssemblyAI speech-model ids and their user-facing labels.</summary>
public static class AssemblyAiModels
{
    public readonly record struct ModelChoice(string Id, string Label);

    public static IReadOnlyList<ModelChoice> Known { get; } = new[]
    {
        new ModelChoice("universal-2", "universal-2 (fast)"),
        new ModelChoice("universal-3-pro", "universal-3-pro (premium)"),
    };

    public static string DefaultId => "universal-2";

    // Accepted alias: the AssemblyAI API-reference enum spells the premium model
    // "universal-3-5-pro" while pricing/Python-SDK use "universal-3-pro". Recognize
    // both so neither official spelling is wrongly flagged as a "custom" model.
    private static readonly string[] KnownAliases = { "universal-3-5-pro" };

    public static bool IsKnown(string id)
        => !string.IsNullOrWhiteSpace(id)
           && (Known.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
               || KnownAliases.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase)));
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.AssemblyAiModelsTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiModels.cs tests/Winpepper.Asr.Tests/AssemblyAiModelsTests.cs
git commit -m "feat(asr): known-good AssemblyAI model ids + labels + validation"
```

---

## Task 12: CloudProvider.IsCloud helper

**Files:**
- Create: `src/Winpepper.Asr/Transcription/CloudProvider.cs`
- Test: `tests/Winpepper.Asr.Tests/CloudProviderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `bool CloudProvider.IsCloud(string providerModelName)` → `providerModelName.StartsWith("assemblyai/", OrdinalIgnoreCase)`. Consumed by Task 17 (cloud-cleanup skip) and available to Task 15.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/CloudProviderTests.cs`:
```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CloudProviderTests
{
    [Theory]
    [InlineData("assemblyai/universal-2", true)]
    [InlineData("AssemblyAI/universal-3-pro", true)]
    [InlineData("parakeet-tdt-0.6b-v3", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCloud_DetectsAssemblyAiPrefix(string? name, bool expected)
        => CloudProvider.IsCloud(name!).ShouldBe(expected);
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — `CloudProvider` does not exist.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/CloudProvider.cs`:
```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>
/// Classifies a TranscriptionResult.ProviderModelName. Cloud results (AssemblyAI)
/// are already server-side punctuated/formatted, so downstream cleanup can skip
/// the local LLM pass and run only the deterministic correction pass.
/// </summary>
public static class CloudProvider
{
    public const string AssemblyAiPrefix = "assemblyai/";

    public static bool IsCloud(string providerModelName)
        => !string.IsNullOrEmpty(providerModelName)
           && providerModelName.StartsWith(AssemblyAiPrefix, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.CloudProviderTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/CloudProvider.cs tests/Winpepper.Asr.Tests/CloudProviderTests.cs
git commit -m "feat(asr): CloudProvider.IsCloud to detect AssemblyAI-produced transcripts"
```

---

## Task 13: AssemblyAiErrors.IsInvalidModel detection

**Files:**
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiErrors.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiErrorsTests.cs`

**Interfaces:**
- Consumes: `AssemblyAiException` (has `StatusCode`, `Message` carrying the 400 body).
- Produces: `bool AssemblyAiErrors.IsInvalidModel(AssemblyAiException ex)` → true when `StatusCode == 400` and the message/body indicates an invalid/unsupported model. Consumed by Task 15 (FallbackTranscriber config-error callback).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Asr.Tests/AssemblyAiErrorsTests.cs`:
```csharp
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiErrorsTests
{
    [Theory]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"invalid speech_model 'universal-9000'\"}", true)]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"unsupported model\"}", true)]
    [InlineData("AssemblyAI request failed (400): {\"error\":\"bad audio_url\"}", false)]
    public void IsInvalidModel_On400_MatchesModelWording(string message, bool expected)
        => AssemblyAiErrors.IsInvalidModel(new AssemblyAiException(message, 400)).ShouldBe(expected);

    [Fact]
    public void IsInvalidModel_False_WhenNot400()
        => AssemblyAiErrors.IsInvalidModel(new AssemblyAiException("server error", 500)).ShouldBeFalse();
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — `AssemblyAiErrors` does not exist.

- [ ] **Step 3: Implement**

Create `src/Winpepper.Asr/Transcription/AssemblyAiErrors.cs`:
```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Classifies terminal AssemblyAI errors so config problems surface persistently.</summary>
public static class AssemblyAiErrors
{
    // 400 bodies for a bad model id mention the model or speech_model field.
    private static readonly string[] ModelHints = { "speech_model", "model", "unsupported model", "invalid model" };

    public static bool IsInvalidModel(AssemblyAiException ex)
    {
        if (ex.StatusCode != 400) return false;
        var m = ex.Message ?? "";
        return ModelHints.Any(h => m.Contains(h, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -class "Winpepper.Asr.Tests.AssemblyAiErrorsTests"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiErrors.cs tests/Winpepper.Asr.Tests/AssemblyAiErrorsTests.cs
git commit -m "feat(asr): detect invalid-model 400 errors for persistent config surfacing"
```

---

## Task 14: AssemblyAiTranscriber — robust poll loop, first-poll grace, ct budget, delete, extras, id logging

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs` (full rewrite)
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs` (remove `TotalTimeout`)
- Modify: `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs` (record delete already added in Task 8; add status scripting flexibility), `tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs` (rewrite)

**Interfaces:**
- Consumes: `IAssemblyAiClient` (Tasks 5–9), `AssemblyAiOptions` (Task 2), `AssemblyAiRequestExtras` (Task 7).
- Produces: `AssemblyAiTranscriber` constructor:
  ```csharp
  public AssemblyAiTranscriber(
      IAssemblyAiClient client, IAssemblyAiKeyStore keyStore, AssemblyAiOptions options,
      ILogger<AssemblyAiTranscriber> logger,
      Func<TimeSpan, CancellationToken, Task>? delay = null,
      Func<AssemblyAiRequestExtras>? extrasProvider = null,
      Action<Func<Task>>? scheduleDetached = null)
  ```
  Behavior: waits `FirstPollDelay` before the first poll, polls at `PollInterval` up to `ceil(CloudDeadline / PollInterval)` polls, honors `ct` (deadline owned by `FallbackTranscriber`), treats unrecognized statuses explicitly (log + continue), and — on `completed` with `DeleteAfterTranscribe` — schedules a fire-and-forget `DeleteTranscriptAsync` via `scheduleDetached`. Logs the transcript id; never the key.

- [ ] **Step 1: Rewrite the transcriber tests (RED)**

Replace the entire body of `tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs` with:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0.5f, -0.5f, 0f };

    private sealed class StubKeyStore : IAssemblyAiKeyStore
    {
        public bool HasKey { get; init; } = true;
        public void Save(string apiKey) { }
        public string? Load() => HasKey ? "KEY" : null;
        public void Clear() { }
    }

    private static AssemblyAiTranscriber Make(
        FakeAssemblyAiClient client, bool hasKey = true,
        int deadlineSec = 10, TimeSpan? poll = null,
        List<TimeSpan>? delays = null,
        Func<AssemblyAiRequestExtras>? extras = null,
        bool delete = true)
    {
        var opts = new AssemblyAiOptions
        {
            Model = "universal-2",
            CloudDeadline = TimeSpan.FromSeconds(deadlineSec),
            PollInterval = poll ?? TimeSpan.FromSeconds(1),
            FirstPollDelay = TimeSpan.FromMilliseconds(750),
            DeleteAfterTranscribe = delete,
        };
        return new AssemblyAiTranscriber(
            client, new StubKeyStore { HasKey = hasKey }, opts,
            NullLogger<AssemblyAiTranscriber>.Instance,
            delay: (ts, _) => { delays?.Add(ts); return Task.CompletedTask; },
            extrasProvider: extras,
            scheduleDetached: a => a().GetAwaiter().GetResult()); // run inline for determinism
    }

    [Fact]
    public async Task HappyPath_FirstPollGrace_ReturnsText_AndDeletes()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("processing", null, null, null, null))
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hello from the cloud", 0.95, 4.0, null));
        var delays = new List<TimeSpan>();
        var t = Make(client, delays: delays);

        var result = await t.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello from the cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        client.RiffMagic().ShouldBe("RIFF");
        delays[0].ShouldBe(TimeSpan.FromMilliseconds(750)); // first-poll grace precedes poll #1
        client.Deleted.ShouldContain("t-fake");             // retention cleanup issued
    }

    [Fact]
    public async Task DeleteDisabled_DoesNotDelete()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hi", 0.9, 1.0, null));
        var t = Make(client, delete: false);
        await t.TranscribeAsync(Audio, CancellationToken.None);
        client.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnrecognizedStatus_DoesNotDropCompletion()
    {
        // A weird status must NOT abort or silently drop the eventual completion.
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("123", null, null, null, null))   // coerced non-string
            .EnqueuePoll(new AssemblyAiTranscript("completed", "recovered", 0.9, 2.0, null));
        var t = Make(client);
        var result = await t.TranscribeAsync(Audio, CancellationToken.None);
        result.Text.ShouldBe("recovered");
    }

    [Fact]
    public async Task ErrorStatus_Throws()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("error", null, null, null, "Transcoding failed"));
        var t = Make(client);
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("Transcoding failed");
    }

    [Fact]
    public async Task NeverCompletes_TimesOutAfterPollBudget()
    {
        var client = new FakeAssemblyAiClient(); // always "processing"
        var t = Make(client, deadlineSec: 3, poll: TimeSpan.FromSeconds(1));
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("timed out");
        client.PollCalls.ShouldBe(3); // ceil(3s / 1s)
    }

    [Fact]
    public async Task NoKey_ThrowsAuthError()
    {
        var client = new FakeAssemblyAiClient();
        var t = Make(client, hasKey: false);
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
    }

    [Fact]
    public async Task PassesExtrasToCreate()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("completed", "x", 0.9, 1.0, null));
        var extras = new AssemblyAiRequestExtras(
            new[] { new AssemblyAiCustomSpelling(new[] { "winpeper" }, "Winpepper") },
            Array.Empty<string>());
        var t = Make(client, extras: () => extras);
        await t.TranscribeAsync(Audio, CancellationToken.None);
        client.LastExtras.ShouldBe(extras);
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — build errors: the transcriber constructor has no `extrasProvider`/`scheduleDetached` params; `AssemblyAiOptions.CloudDeadline` used in `Make` but transcriber still references `TotalTimeout`.

- [ ] **Step 3: Rewrite the transcriber**

Replace the entire body of `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs` with:
```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI batch transcriber: encode float samples to WAV, upload, create a
/// transcript, then poll to completion on the caller's token (the cloud deadline
/// is owned by FallbackTranscriber). Waits a first-poll grace before poll #1,
/// treats unrecognized statuses explicitly, and (best-effort) deletes the remote
/// transcript after success. Never logs the API key.
/// </summary>
public sealed class AssemblyAiTranscriber : ITranscriber
{
    private readonly IAssemblyAiClient _client;
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiTranscriber> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<AssemblyAiRequestExtras> _extrasProvider;
    private readonly Action<Func<Task>> _scheduleDetached;

    public AssemblyAiTranscriber(
        IAssemblyAiClient client,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiTranscriber> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<AssemblyAiRequestExtras>? extrasProvider = null,
        Action<Func<Task>>? scheduleDetached = null)
    {
        _client = client;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
        _extrasProvider = extrasProvider ?? (() => AssemblyAiRequestExtras.Empty);
        _scheduleDetached = scheduleDetached ?? (a => _ = Task.Run(a));
    }

    public string ModelName => $"assemblyai/{_opts.Model}";

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);

        var sw = Stopwatch.StartNew();
        var wav = PcmWavEncoder.EncodeMono16k(mono16k.Span);
        var uploadUrl = await _client.UploadAsync(wav, ct);

        var extras = _extrasProvider();
        var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, extras, ct);
        _log.LogInformation("AssemblyAI transcript {Id} created ({Bytes} bytes uploaded)", id, wav.Length);

        var maxPolls = Math.Max(1, (int)Math.Ceiling(_opts.CloudDeadline / _opts.PollInterval));

        // First-poll grace: a freshly created clip needs ~750 ms to enter processing.
        await _delay(_opts.FirstPollDelay, ct);

        for (var i = 0; i < maxPolls; i++)
        {
            var tr = await _client.GetTranscriptAsync(id, ct);
            var status = (tr.Status ?? "").Trim();

            if (status.Equals("completed", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("AssemblyAI transcript {Id} completed in {Ms}ms (confidence {Conf})",
                    id, sw.ElapsedMilliseconds, tr.Confidence);
                if (_opts.DeleteAfterTranscribe) ScheduleDelete(id);
                return new TranscriptionResult(tr.Text ?? "", ModelName);
            }
            if (status.Equals("error", StringComparison.OrdinalIgnoreCase))
                throw new AssemblyAiException($"AssemblyAI transcription failed: {tr.Error}");

            if (!status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                && !status.Equals("processing", StringComparison.OrdinalIgnoreCase))
            {
                // Unknown status: never silently drop a possible completion — log and keep polling.
                _log.LogWarning("AssemblyAI transcript {Id} returned unrecognized status '{Status}'; continuing to poll",
                    id, tr.Status);
            }

            if (i < maxPolls - 1) await _delay(_opts.PollInterval, ct);
        }

        throw new AssemblyAiException($"AssemblyAI transcription timed out after {maxPolls} polls.");
    }

    private void ScheduleDelete(string id)
        => _scheduleDetached(async () =>
        {
            try { await _client.DeleteTranscriptAsync(id, CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "AssemblyAI transcript {Id} delete failed (non-fatal)", id); }
        });
}
```

- [ ] **Step 4: Remove the now-unused TotalTimeout option**

In `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`, delete the `TotalTimeout` property and its `TODO(remove in Task 14)` comment block (the three-line region added in Task 2). Confirm no remaining references:
```bash
grep -rn "TotalTimeout" src tests || echo "OK: no TotalTimeout references"
```
Expected: `OK: no TotalTimeout references`.

- [ ] **Step 5: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll
```
Expected: PASS, `Failed: 0` (whole Asr suite).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs
git commit -m "fix(asr): robust poll loop, first-poll grace, ct budget, retention delete, extras"
```

---

## Task 15: FallbackTranscriber — cloud deadline + invalid-model config-error callback

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/FallbackTranscriber.cs` (rewrite)
- Test: `tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs`

**Interfaces:**
- Consumes: `ITranscriber` (primary/local), `AssemblyAiErrors.IsInvalidModel` (Task 13).
- Produces: `FallbackTranscriber` constructor:
  ```csharp
  public FallbackTranscriber(
      ITranscriber primary, ITranscriber local, ILogger<FallbackTranscriber> logger,
      Action<string>? onFallback = null,
      TimeSpan? cloudDeadline = null,
      Action<string>? onConfigError = null,
      Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
  ```
  Behavior: runs `primary` under a linked CTS cancelled after `cloudDeadline` (default 10s). On the caller's own cancellation → rethrow (no local). On any other failure OR deadline → invoke `onConfigError` if the failure is an invalid-model 400, invoke `onFallback`, and run `local` on the caller's `ct`.

- [ ] **Step 1: Rewrite the tests (RED for new behavior; keep existing)**

Replace the body of `tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs` with:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class FallbackTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0f, 0f };

    private sealed class BlockingTranscriber : ITranscriber
    {
        public string ModelName => "assemblyai/universal-2";
        public int Calls { get; private set; }
        public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, ct); // block until the deadline cancels us
            return new TranscriptionResult("never", ModelName);
        }
    }

    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryResultAndProvider()
    {
        var primary = FakeTranscriber.Returning("assemblyai/universal-2", "hello cloud");
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToLocal()
    {
        var primary = FakeTranscriber.Throwing("assemblyai/universal-2", new InvalidOperationException("network down"));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        string? notice = null;
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance, msg => notice = msg);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        result.ProviderModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        local.Calls.ShouldBe(1);
        notice.ShouldNotBeNull();
    }

    [Fact]
    public async Task UserCancellation_DoesNotFallBack()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var primary = FakeTranscriber.Throwing("assemblyai/universal-2", new OperationCanceledException(cts.Token));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => fb.TranscribeAsync(Audio, cts.Token));
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CloudDeadline_Elapses_FallsBackToLocalImmediately()
    {
        var primary = new BlockingTranscriber();
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        // scheduleDeadline cancels the cloud CTS immediately (deterministic, no real wait).
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance,
            cloudDeadline: TimeSpan.FromSeconds(10),
            scheduleDeadline: (cts, _) => cts.Cancel());

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        primary.Calls.ShouldBe(1);
        local.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidModel400_RaisesConfigError_AndFallsBack()
    {
        var primary = FakeTranscriber.Throwing("assemblyai/universal-9000",
            new AssemblyAiException("AssemblyAI request failed (400): {\"error\":\"invalid speech_model\"}", 400));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        string? configError = null;
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance,
            onConfigError: msg => configError = msg);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        configError.ShouldNotBeNull();
        configError!.ShouldContain("speech_model");
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
Expected: FAIL — build errors: `FallbackTranscriber` has no `cloudDeadline`/`onConfigError`/`scheduleDeadline` params.

- [ ] **Step 3: Rewrite FallbackTranscriber**

Replace the body of `src/Winpepper.Asr/Transcription/FallbackTranscriber.cs` with:
```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Runs a primary (cloud) transcriber under a single owned cloud deadline; on ANY
/// non-user-cancellation failure (including deadline) it falls back to local so the
/// user always gets their dictation. Invalid-model 400s additionally raise a config
/// error so the misconfiguration surfaces persistently instead of degrading silently.
/// </summary>
public sealed class FallbackTranscriber : ITranscriber
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackTranscriber> _log;
    private readonly Action<string>? _onFallback;
    private readonly TimeSpan _cloudDeadline;
    private readonly Action<string>? _onConfigError;
    private readonly Action<CancellationTokenSource, TimeSpan> _scheduleDeadline;

    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber local,
        ILogger<FallbackTranscriber> logger,
        Action<string>? onFallback = null,
        TimeSpan? cloudDeadline = null,
        Action<string>? onConfigError = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
    {
        _primary = primary;
        _local = local;
        _log = logger;
        _onFallback = onFallback;
        _cloudDeadline = cloudDeadline ?? TimeSpan.FromSeconds(10);
        _onConfigError = onConfigError;
        _scheduleDeadline = scheduleDeadline ?? ((cts, d) => cts.CancelAfter(d));
    }

    public string ModelName => _primary.ModelName;

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        using var cloudCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scheduleDeadline(cloudCts, _cloudDeadline);

        try
        {
            return await _primary.TranscribeAsync(mono16k, cloudCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the USER aborted the dictation — do not run local as well
        }
        catch (Exception ex)
        {
            // Either the cloud deadline fired (cloudCts cancelled, ct not) or the
            // cloud attempt failed. Either way, fall back so the user still gets text.
            if (ex is AssemblyAiException aai && AssemblyAiErrors.IsInvalidModel(aai))
            {
                _log.LogWarning("AssemblyAI model appears invalid; surfacing config error and falling back");
                _onConfigError?.Invoke(aai.Message);
            }
            else
            {
                _log.LogWarning(ex, "Cloud transcription failed or timed out; falling back to local ASR");
            }
            _onFallback?.Invoke(ex.Message);
            return await _local.TranscribeAsync(mono16k, ct);
        }
    }
}
```

- [ ] **Step 4: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll
```
Expected: PASS, `Failed: 0` (whole Asr suite, including all prior tasks).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/FallbackTranscriber.cs tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs
git commit -m "fix(asr): FallbackTranscriber owns cloud deadline + surfaces invalid-model config errors"
```

---

## Task 16: CleanupRunner — skip LLM for cloud results (deterministic-only)

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupResult.cs` (add `BypassProvider` to `CleanupPath`)
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs:24-41` (`RunAsync` add optional `skipLlm`)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`

**Interfaces:**
- Consumes: existing `CleanupRunner`, `CorrectionsData`, `CleanupOptions`.
- Produces: `RunAsync(string rawTranscript, CorrectionsData corrections, Task<string?>? windowContextTask, CleanupOptions options, CancellationToken ct, bool skipLlm = false)`. When `skipLlm` is true, short-circuits to the deterministic correction-only post-pass (no backend/LLM call), returning `CleanupResult.Path == CleanupPath.BypassProvider`.

- [ ] **Step 1: Add the enum value (compile prerequisite for the test)**

In `src/Winpepper.Cleanup/CleanupResult.cs`, add `BypassProvider` to the `CleanupPath` enum (append after `BypassShort`):
```csharp
    BypassShort,
    BypassProvider, // cloud provider already formatted server-side; corrections only
```

- [ ] **Step 2: Write the failing test**

Append to `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (reuse the file's existing fake backend + helpers; if it has a `Make(...)`/fake-backend helper, follow that pattern — this test uses a backend that MUST NOT be invoked). Add:
```csharp
    [Fact]
    public async Task SkipLlm_RunsDeterministicOnly_NoBackendCall()
    {
        // A backend that throws if called — proves the LLM path is skipped.
        var backend = new ThrowingCleanupBackend();
        var runner = new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance);
        var corrections = CorrectionsData.Empty with
        {
            Replacements = new Dictionary<string, string> { ["kubernettes"] = "Kubernetes" },
        };

        var result = await runner.RunAsync(
            rawTranscript: "deploy to kubernettes now",
            corrections: corrections,
            windowContextTask: null,
            options: new CleanupOptions(),
            ct: CancellationToken.None,
            skipLlm: true);

        result.Path.ShouldBe(CleanupPath.BypassProvider);
        result.CleanedText.ShouldBe("deploy to Kubernetes now"); // correction applied deterministically
    }
```
Add this fake near the top of the test file (or in the existing `Fakes` folder if the file uses one — check `tests/Winpepper.Cleanup.Tests/Fakes/`). If no shared fake exists, add inline:
```csharp
    private sealed class ThrowingCleanupBackend : Winpepper.Cleanup.ILlamaCleanupBackend
    {
        public Task<string> GenerateAsync(string prompt, float temperature, int maxNewTokens, CancellationToken ct)
            => throw new Xunit.Sdk.XunitException("LLM backend must not be called when skipLlm=true");
    }
```
> Verify `ILlamaCleanupBackend`'s exact member signature in `src/Winpepper.Cleanup/` before writing the fake; match it exactly (method name/params may differ — adapt the throwing stub to the real interface). If a `Fakes/FakeCleanupBackend` already exists with a "record calls" flag, prefer configuring that to assert zero calls instead of throwing.

- [ ] **Step 3: Run the test and verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -method "Winpepper.Cleanup.Tests.CleanupRunnerTests.SkipLlm_RunsDeterministicOnly_NoBackendCall"
```
Expected: FAIL — `RunAsync` has no `skipLlm` parameter.

- [ ] **Step 4: Add the skipLlm short-circuit**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, change the `RunAsync` signature (line ~24) to add the optional parameter:
```csharp
    public async Task<CleanupResult> RunAsync(
        string rawTranscript,
        CorrectionsData corrections,
        Task<string?>? windowContextTask,
        CleanupOptions options,
        CancellationToken ct,
        bool skipLlm = false)
    {
```
Then, as the FIRST statement inside the method (before the existing word-count `BypassShort` short-circuit at ~line 37), insert:
```csharp
        if (skipLlm)
        {
            // Cloud text is already server-side punctuated/formatted; run only the
            // deterministic correction post-pass (no LLM). Mirror the BypassShort call.
            var swBypass = System.Diagnostics.Stopwatch.StartNew();
            return Finalize(rawTranscript, "", corrections, assembledPrompt: "", CleanupPath.BypassProvider, swBypass);
        }
```
> **VERIFIED signature (`CleanupRunner.cs:244`):** `private static CleanupResult Finalize(string rawTranscript, string rawModelOutput, CorrectionsData corrections, string assembledPrompt, CleanupPath path, Stopwatch sw)` — 6 positional args, `CleanupPath` is the **5th**, and a `Stopwatch` is required. The exemplar `BypassShort` call at `CleanupRunner.cs:40` is `return Finalize(rawTranscript, "", corrections, assembledPrompt: "", CleanupPath.BypassShort, sw);` — the snippet above mirrors it exactly (substituting `BypassProvider`). If a method-level `sw` is already in scope at the insertion point, reuse it instead of `swBypass`. `Finalize` runs `ApplyDeterministicPostPass` (→ `CaseAwareReplacer.Apply(rawTranscript, corrections.Replacements)`), so corrections are applied and no LLM runs.

- [ ] **Step 5: Run the test and verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, `Failed: 0` (existing CleanupRunner tests still pass — the new param is optional).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupResult.cs src/Winpepper.Cleanup/CleanupRunner.cs tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "feat(cleanup): skipLlm bypass runs deterministic correction pass for cloud results"
```

---

## Task 17: Wire the ASR stack in AppShell + PipelineHost (Windows, thin)

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:236-242` (HttpClient + options + client construction), `:357-382` (`BuildTranscriber`), `:244-254` (pipeline construction)
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs:254`, `:420` (cloud-cleanup skip)

**Interfaces:**
- Consumes: everything from Tasks 1–16.
- Produces: at runtime — no global 30s `HttpClient.Timeout`; cloud attempts bounded by `AssemblyAiCloudDeadlineSeconds` (clamped); `custom_spelling`/`keyterms` sent from `CorrectionStore` + settings; transcripts deleted per setting; invalid-model config errors routed to `ErrorBus`; cloud transcripts skip LLM cleanup.

> **This task is `#if WINDOWS`-only and cannot be unit-tested on Linux.** Its correctness is proven by the Windows smoke checklist (§ end of plan). Keep the wiring thin — all logic already lives in tested library code. Build the App project only to confirm it compiles is NOT possible on Linux (WinUI). Verify via `grep` assertions below and the smoke checklist.

- [ ] **Step 1: Remove the 30s HttpClient timeout and build the options from settings**

In `src/Winpepper.App/Hosting/AppShell.cs`, replace lines 236-242 (the `aaiHttp`/`aaiOptions`/`aaiClient` block) with:
```csharp
        // No global HttpClient.Timeout: per-request timeouts are enforced inside
        // AssemblyAiClient via a linked CTS, and the total cloud budget is owned by
        // FallbackTranscriber. A single large safety cap guards against a truly wedged socket.
        var aaiHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var aaiOptions = new Winpepper.Asr.Transcription.AssemblyAiOptions
        {
            Model = settings.AssemblyAiModel,
            CloudDeadline = Winpepper.Asr.Transcription.AssemblyAiOptions.ClampDeadline(settings.AssemblyAiCloudDeadlineSeconds),
            DeleteAfterTranscribe = settings.AssemblyAiDeleteAfterTranscribe,
            KeytermsEnabled = settings.AssemblyAiKeytermsEnabled,
        };
        var aaiClient = new Winpepper.Asr.Transcription.AssemblyAiClient(
            aaiHttp,
            () => aaiKeyStore.Load(),
            aaiOptions,
            factory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());
```

- [ ] **Step 2: Pass CorrectionStore + a config-error sink into BuildTranscriber**

In `src/Winpepper.App/Hosting/AppShell.cs`, the pipeline is constructed at lines 244-254 with a `transcriberFactory` delegate `(local, s, onFallback) => AppShell.BuildTranscriber(local, s, onFallback, aaiClient, aaiKeyStore, factory)`. Extend `BuildTranscriber` to also receive `correctionStore`, the freshly-read options, and a `Action<string> onConfigError`, then thread them through. Replace the delegate (lines 249-250) with:
```csharp
                                         (local, s, onFallback) => AppShell.BuildTranscriber(
                                             local, s, onFallback, aaiClient, aaiKeyStore, aaiOptions,
                                             correctionStore, errorBus, factory),
```

- [ ] **Step 3: Update BuildTranscriber to build extras + deadline + config-error routing**

In `src/Winpepper.App/Hosting/AppShell.cs`, replace `BuildTranscriber` (lines 357-382) with:
```csharp
    public static Winpepper.Asr.Transcription.ITranscriber BuildTranscriber(
        Winpepper.Asr.ParakeetSession local,
        AppSettings settings,
        Action<string> onFallback,
        Winpepper.Asr.Transcription.IAssemblyAiClient client,
        Winpepper.Asr.Transcription.IAssemblyAiKeyStore keyStore,
        Winpepper.Asr.Transcription.AssemblyAiOptions options,
        Winpepper.Corrections.CorrectionStore? correctionStore,
        Winpepper.Core.Errors.ErrorBus errorBus,
        ILoggerFactory loggerFactory)
    {
        var localTranscriber = new Winpepper.Asr.Transcription.ParakeetTranscriber(
            local, Winpepper.Models.ModelRegistry.DefaultAsrName);

        if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
            return localTranscriber;

        // Snapshot corrections into request extras at build time; keyterms only when opted in.
        Winpepper.Asr.Transcription.AssemblyAiRequestExtras Extras()
        {
            var data = correctionStore?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;
            return Winpepper.Asr.Transcription.CorrectionSpellingMapper.ToExtras(data, options.KeytermsEnabled);
        }

        var cloud = new Winpepper.Asr.Transcription.AssemblyAiTranscriber(
            client, keyStore, options,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiTranscriber>(),
            extrasProvider: Extras);

        return new Winpepper.Asr.Transcription.FallbackTranscriber(
            cloud, localTranscriber,
            loggerFactory.CreateLogger<Winpepper.Asr.Transcription.FallbackTranscriber>(),
            onFallback: onFallback,
            cloudDeadline: options.CloudDeadline,
            onConfigError: msg => errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Asr,
                new InvalidOperationException(
                    $"AssemblyAI model rejected ({settings.AssemblyAiModel}). Check the model setting. {msg}"),
                Guid.Empty)); // config-level error, not tied to a capture session
    }
```
> **VERIFIED API (`src/Winpepper.Core/Errors/`):** `ErrorBus.Report(ErrorStage stage, Exception ex, Guid sessionId)` — it takes an **`Exception` and a `Guid`**, NOT a string. There is **no `ErrorStage.Transcription` member**; the members are `Audio, Asr, Cleanup, OcrUia, Injection, Learning, History, Models, Settings, Hotkey, Crash, Unknown` — use **`ErrorStage.Asr`** for transcription/cloud-model errors. The exemplar call is `PipelineHost.cs:295`: `_errorBus.Report(ErrorStage.Cleanup, ex, _currentSessionId)`. Since `BuildTranscriber` runs at composition time with no session in scope, pass `Guid.Empty` (a config-level error). If you prefer per-session attribution, thread the active session id from `PipelineHost` into the `onConfigError` callback instead — but `Guid.Empty` is acceptable for a configuration fault. **This task is `#if WINDOWS`-only and is NOT covered by any Linux test — get this call exactly right, because the compiler won't catch it until the Windows build.**

- [ ] **Step 4: Skip LLM cleanup for cloud transcripts in PipelineHost**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, the HoldUp cleanup gate is at line 254 (`if (!string.IsNullOrWhiteSpace(final) && _cleanup is not null)`), with `producedModelName` available from line 249. Pass `skipLlm` into the `_cleanup.RunAsync(...)` call inside that block (around lines 279-285). Add the argument:
```csharp
        var result = await _cleanup.RunAsync(
            rawTranscript: final,
            corrections: correctionsData,
            windowContextTask: ctxTextTask,
            options: _cleanupOptions,
            ct: ct,
            skipLlm: Winpepper.Asr.Transcription.CloudProvider.IsCloud(producedModelName));
```
Immediately after the `RunAsync` call in that block, record the shorter path in the history model name — replace the `cleanupUsedModel = _cleanupModelName;` assignment (line ~287) with:
```csharp
        cleanupUsedModel = result.Path == Winpepper.Cleanup.CleanupPath.BypassProvider
            ? "none (cloud, corrections-only)"
            : _cleanupModelName;
```
Apply the identical two edits to the Toggle-path block: the `RunAsync` call at ~lines 445-451 (use `producedModelName2`) and the `cleanupUsedModel2 = _cleanupModelName;` assignment at ~line 453.
> Confirm the exact local variable names in each block (`producedModelName`/`producedModelName2`, `cleanupUsedModel`/`cleanupUsedModel2`, `correctionsData`, `ctxTextTask`) by reading the two blocks first; the two paths are copy-pasted with `2` suffixes.

- [ ] **Step 5: Verify the wiring statically (no Linux WinUI build available)**

Run:
```bash
grep -n "Timeout = TimeSpan.FromSeconds(30)" src/Winpepper.App/Hosting/AppShell.cs && echo "FAIL: 30s timeout still present" || echo "OK: 30s timeout removed"
grep -n "ClampDeadline" src/Winpepper.App/Hosting/AppShell.cs
grep -n "CorrectionSpellingMapper.ToExtras" src/Winpepper.App/Hosting/AppShell.cs
grep -n "CloudProvider.IsCloud" src/Winpepper.App/Hosting/PipelineHost.cs
grep -c "skipLlm:" src/Winpepper.App/Hosting/PipelineHost.cs   # expect 2 (both paths)
```
Expected: "OK: 30s timeout removed"; `ClampDeadline`, `ToExtras`, `CloudProvider.IsCloud` each found; `skipLlm:` count is 2.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting/AppShell.cs src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): wire AssemblyAI budget/extras/delete/config-error + skip cloud LLM cleanup"
```

---

## Task 18: RecordingPage UI — honest test-key, model combo, disclosure, label, keyterms, config-error (Windows, thin)

**Files:**
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` (model ComboBox, keyterms toggle, provider label, privacy text)
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs` (test-key logic, model combo binding, config-error status)

**Interfaces:**
- Consumes: `AssemblyAiModels` (Task 11), `AppSettings.AssemblyAiKeytermsEnabled` (Task 1), `AssemblyAiClient.ValidateKeyAsync`.
- Produces: settings UI where Test validates the *typed* key if present (else saved), model is chosen from known ids (+ Advanced/custom with a warning), privacy text discloses deletion, provider label reworded, keyterms is an opt-in toggle with a cost caption, and a persistent config-error status area exists.

> **`#if WINDOWS`-only; verified by the Windows smoke checklist.** Keep code-behind thin.

- [ ] **Step 1: Update the XAML — label, disclosure, model combo, keyterms toggle, config-error text**

In `src/Winpepper.App/Views/RecordingPage.xaml`, within the "Speech recognition" `StackPanel` (lines 86-116), apply:

1a. Reword the provider item (line 92):
```xml
                    <ComboBoxItem Content="AssemblyAI (cloud)" Tag="assemblyai" />
```
1b. Replace the disclosure `TextBlock` (lines 95-99) with deletion-aware copy:
```xml
                <TextBlock
                    x:Name="AsrPrivacyText"
                    Text="Cloud transcription sends your recorded audio to AssemblyAI. Winpepper asks AssemblyAI to delete your audio and transcript after transcription (deletion happens on AssemblyAI's servers and may not be immediate). Turn deletion off below to keep them per AssemblyAI's retention policy."
                    TextWrapping="Wrap"
                    Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                    Style="{StaticResource CaptionTextBlockStyle}" />
```
1c. Replace the freeform `AssemblyAiModelBox` (lines 109-110) with a ComboBox + custom box + warning:
```xml
                    <ComboBox x:Name="AssemblyAiModelCombo" Header="Model" MinWidth="280" />
                    <TextBox x:Name="AssemblyAiModelBox" Header="Custom model id" MinWidth="280"
                             Visibility="Collapsed"
                             PlaceholderText="Advanced: exact AssemblyAI speech_model id" />
                    <TextBlock x:Name="AssemblyAiModelWarning"
                               Visibility="Collapsed"
                               Text="Custom model ids are not validated and will fail at dictation time if wrong."
                               TextWrapping="Wrap"
                               Foreground="{ThemeResource SystemFillColorCautionBrush}"
                               Style="{StaticResource CaptionTextBlockStyle}" />
```
1d. Add a keyterms opt-in toggle + a delete toggle, immediately before the `AsrStatusText` block (line 111):
```xml
                    <ToggleSwitch x:Name="AssemblyAiDeleteToggle" Header="Delete audio from AssemblyAI after transcription" />
                    <ToggleSwitch x:Name="AssemblyAiKeytermsToggle" Header="Send preferred terms as keyterms" />
                    <TextBlock Text="Preferred terms may incur extra AssemblyAI cost on some plans. Off by default. Your corrections list is always applied via custom spelling at no extra cost."
                               TextWrapping="Wrap"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                               Style="{StaticResource CaptionTextBlockStyle}" />
```

- [ ] **Step 2: Update the code-behind — model combo, honest test-key, keyterms/delete toggles, config-error status**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, replace the model-id block (lines 105-111) and the Test button block (lines 132-146) as follows.

2a. Replace lines 105-111 (model binding) with:
```csharp
        // Model picker: known ids + an "Advanced/custom" escape hatch.
        const string CustomTag = "__custom__";
        AssemblyAiModelCombo.Items.Clear();
        foreach (var m in Winpepper.Asr.Transcription.AssemblyAiModels.Known)
            AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Id });
        AssemblyAiModelCombo.Items.Add(new ComboBoxItem { Content = "Advanced / custom…", Tag = CustomTag });

        void SelectModelInCombo(string modelId)
        {
            var known = Winpepper.Asr.Transcription.AssemblyAiModels.IsKnown(modelId);
            AssemblyAiModelCombo.SelectedIndex = known
                ? Enumerable.Range(0, AssemblyAiModelCombo.Items.Count)
                    .First(i => (string?)((ComboBoxItem)AssemblyAiModelCombo.Items[i]).Tag == modelId
                             || string.Equals((string?)((ComboBoxItem)AssemblyAiModelCombo.Items[i]).Tag, modelId, StringComparison.OrdinalIgnoreCase))
                : AssemblyAiModelCombo.Items.Count - 1; // the custom item
            var isCustom = !known;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelBox.Text = isCustom ? modelId : "";
        }
        SelectModelInCombo(current.AssemblyAiModel);

        AssemblyAiModelCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AssemblyAiModelCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId;
            var isCustom = tag == CustomTag;
            AssemblyAiModelBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            AssemblyAiModelWarning.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
            if (!isCustom)
                _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = tag });
        };
        AssemblyAiModelBox.LostFocus += (_, _) =>
        {
            var model = string.IsNullOrWhiteSpace(AssemblyAiModelBox.Text)
                ? Winpepper.Asr.Transcription.AssemblyAiModels.DefaultId
                : AssemblyAiModelBox.Text.Trim();
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = model });
        };

        // Retention + keyterms toggles.
        AssemblyAiDeleteToggle.IsOn = current.AssemblyAiDeleteAfterTranscribe;
        AssemblyAiDeleteToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiDeleteAfterTranscribe = AssemblyAiDeleteToggle.IsOn });
        AssemblyAiKeytermsToggle.IsOn = current.AssemblyAiKeytermsEnabled;
        AssemblyAiKeytermsToggle.Toggled += (_, _) =>
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiKeytermsEnabled = AssemblyAiKeytermsToggle.IsOn });
```

2b. Replace the Test button block (lines 132-146) with an honest typed-or-saved test:
```csharp
        TestKeyButton.Click += async (_, _) =>
        {
            var typed = AssemblyAiKeyBox.Password;
            var hasTyped = !string.IsNullOrWhiteSpace(typed);
            if (!hasTyped && !keyStore.HasKey) { AsrStatusText.Text = "Enter or save a key before testing."; return; }

            AsrStatusText.Text = hasTyped ? "Testing the key you typed…" : "Testing the saved key…";
            try
            {
                Winpepper.Asr.Transcription.IAssemblyAiClient clientToTest = shell.AssemblyAiClient;
                if (hasTyped)
                {
                    // Validate exactly what the user typed, not a previously saved key.
                    var typedKey = typed.Trim();
                    var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                    clientToTest = new Winpepper.Asr.Transcription.AssemblyAiClient(
                        http, () => typedKey, shell.AssemblyAiOptions,
                        shell.LogFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ok = await clientToTest.ValidateKeyAsync(cts.Token);
                if (ok && hasTyped)
                {
                    keyStore.Save(typed.Trim());          // typed key is valid -> save it
                    AssemblyAiKeyBox.Password = "";
                    AsrStatusText.Text = "Typed key is valid and was saved on this PC.";
                }
                else
                {
                    AsrStatusText.Text = ok
                        ? "Saved key is valid."
                        : (hasTyped ? "Typed key rejected (401). Check the key." : "Saved key rejected (401). Check the key.");
                }
            }
            catch (Exception ex)
            {
                AsrStatusText.Text = $"Test failed: {ex.Message}";
            }
        };
```

- [ ] **Step 3: Verify the wiring statically**

Run:
```bash
grep -n "AssemblyAiModels" src/Winpepper.App/Views/RecordingPage.xaml.cs
grep -n "hasTyped" src/Winpepper.App/Views/RecordingPage.xaml.cs
grep -n "AssemblyAI (cloud)" src/Winpepper.App/Views/RecordingPage.xaml
grep -n "deleted from AssemblyAI after transcription" src/Winpepper.App/Views/RecordingPage.xaml
grep -n "AssemblyAiKeytermsToggle" src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
```
Expected: each pattern is found (proves the six UI fixes are present in source). Functional proof is the Windows smoke checklist.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "feat(app): honest test-key, model combo+warning, deletion disclosure, keyterms toggle, config-error status"
```

---

## Task 19 (SEPARATE — own commit): Warm-mic capture-thread self-join dispose scheduler

> **Unrelated area** (escalated residual from the just-merged audio hardening). Keep this as its own task and its own commit. Does not touch AssemblyAI code.

**Files:**
- Modify: `src/Winpepper.Audio/WarmCaptureCoordinator.cs` (add a capture-thread-id seam + off-thread dispose decision in the locked teardown paths)
- Modify: `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs` (expose a way to mark "callbacks run on thread X")
- Test: `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`

**Background:** `WarmWasapiRecorder`'s teardown can deadlock: if the capture stream faults, the source's `Stopped`/`RecordingStopped` callback runs *on the capture thread*, which can drive `Dispose()`/`StopCapture()`/`Rebuild()` → `SwapOutAndDisposeLocked()` → `ICaptureSource.Dispose()` → `captureThread.Join()` on **itself** (self-join) while holding `_lock`. `OnSourceStopped` already disposes *outside* the lock for the fault path (`WarmCaptureCoordinator.cs:139-176`), but `Dispose()`/`StopCapture()`/`Rebuild()` still dispose *inside* the lock. Fix: record the capture-callback thread id, and when a teardown is initiated from that same thread, schedule the source `Dispose()` on the thread pool instead of joining inline.

**Interfaces:**
- Consumes: existing `WarmCaptureCoordinator`, `ICaptureSource`.
- Produces: `WarmCaptureCoordinator` constructor gains two optional seams:
  ```csharp
  Func<int>? currentThreadId = null,          // default: () => Environment.CurrentManagedThreadId
  Action<Action>? disposeScheduler = null      // default: a => ThreadPool.QueueUserWorkItem(_ => a())
  ```
  Behavior: the coordinator records the managed thread id observed in its `FramesAvailable`/`Stopped` handlers (the capture thread). When a dispose is requested and `currentThreadId()` equals that recorded id, the source is disposed via `disposeScheduler` (off-thread) instead of inline — avoiding self-join. Otherwise dispose stays inline.

- [ ] **Step 1: Add a thread-marker to the fake capture source**

In `tests/Winpepper.Audio.Tests/FakeCaptureSource.cs`, add a settable "callback thread id" and a flag recording whether `Dispose` ran on that same thread. Add these members to the class:
```csharp
    public int? CallbackThreadId { get; set; }       // simulate the capture-callback thread
    public int? DisposedOnThreadId { get; private set; }
```
and change `Dispose()` to record the disposing thread:
```csharp
    public void Dispose()
    {
        DisposedOnThreadId = Environment.CurrentManagedThreadId;
        _disposed = true;
    }
```
Also add a helper to raise a frame *as if* from the capture thread (so the coordinator records that thread id). If `RaiseFrame` exists, add:
```csharp
    public void RaiseFrameFromCaptureThread(float[] frame) => FramesAvailable?.Invoke(frame);
```
(Functionally identical to `RaiseFrame`; named for intent in the test.)

- [ ] **Step 2: Write the failing tests (decision is pure/deterministic via the seam)**

Append to `tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs`. These drive the decision through the injected `currentThreadId` seam so no real thread is required:
```csharp
    [Fact]
    public void Dispose_FromCaptureThread_SchedulesOffThread_NoInlineSelfJoin()
    {
        var source = new FakeCaptureSource();
        var scheduled = new List<Action>();
        // currentThreadId returns the SAME id the coordinator recorded from callbacks (7).
        var coord = new WarmCaptureCoordinator(
            new WarmCaptureBuffer(16000),
            sourceFactory: () => source,
            currentThreadId: () => 7,
            disposeScheduler: a => scheduled.Add(a));
        coord.EnsureStarted(force: true);

        // Simulate a capture-thread callback so the coordinator records thread id 7.
        source.RaiseFrame(new float[] { 0f });

        coord.Dispose();

        // Because we are "on" the capture thread (id 7), dispose must be deferred, not inline.
        scheduled.Count.ShouldBe(1);
        source.DisposedOnThreadId.ShouldBeNull(); // not yet disposed inline
        scheduled[0]();                            // run the scheduled dispose
        source.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_FromOtherThread_DisposesInline()
    {
        var source = new FakeCaptureSource();
        var scheduled = new List<Action>();
        var recordedCallbackId = 7;
        var currentId = 99; // a DIFFERENT thread from the capture callbacks
        var coord = new WarmCaptureCoordinator(
            new WarmCaptureBuffer(16000),
            sourceFactory: () => source,
            currentThreadId: () => currentId,
            disposeScheduler: a => scheduled.Add(a));
        coord.EnsureStarted(force: true);

        // Record callback thread id 7 by overriding currentId during the callback.
        currentId = recordedCallbackId;
        source.RaiseFrame(new float[] { 0f });
        currentId = 99; // now we're on a normal (non-capture) thread

        coord.Dispose();

        scheduled.Count.ShouldBe(0);      // no deferral needed
        source.Disposed.ShouldBeTrue();    // disposed inline
    }
```
> If the existing `WarmCaptureCoordinator` constructor already has a `clock`/`faultBackoff` parameter list, add the two new optional params at the END so existing call sites (production `WarmWasapiRecorder` and other tests) keep compiling. Read `WarmCaptureCoordinator.cs:35-59` (the constructor) first and append.

- [ ] **Step 3: Run the tests and verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
    -method "Winpepper.Audio.Tests.WarmCaptureCoordinatorTests.Dispose_FromCaptureThread_SchedulesOffThread_NoInlineSelfJoin"
```
Expected: FAIL — the constructor has no `currentThreadId`/`disposeScheduler` params.

- [ ] **Step 4: Add the seams and the off-thread dispose decision**

In `src/Winpepper.Audio/WarmCaptureCoordinator.cs`:

4a. Add fields near the other readonly fields (around lines 24-33):
```csharp
    private readonly Func<int> _currentThreadId;
    private readonly Action<Action> _disposeScheduler;
    private int? _captureThreadId; // managed thread id observed from source callbacks
```
4b. Extend the constructor (read the existing signature at ~line 35 first) by appending two optional params and initializing:
```csharp
        Func<int>? currentThreadId = null,
        Action<Action>? disposeScheduler = null)
    {
        // ... existing assignments ...
        _currentThreadId = currentThreadId ?? (() => Environment.CurrentManagedThreadId);
        _disposeScheduler = disposeScheduler ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
    }
```
4c. Record the capture thread id in the frame + stopped handlers. Wherever the coordinator receives frames from the source (the `FramesAvailable` subscription set in `StartLocked`, ~line 106) and in `OnSourceStopped` (~line 139), add as the first line of each handler:
```csharp
        _captureThreadId = _currentThreadId();
```
4d. Replace the disposal in `SwapOutAndDisposeLocked` (~lines 121-127) so it defers when called from the capture thread. Change the line that calls `old.Dispose();` to:
```csharp
        DisposeSourceSafely(old);
```
and add the helper:
```csharp
    /// <summary>
    /// Dispose a capture source without self-joining. If we are running on the
    /// same thread the source raises its callbacks from (its capture thread),
    /// ICaptureSource.Dispose()'s internal Thread.Join() would deadlock — so
    /// schedule the dispose off-thread instead of joining inline.
    /// </summary>
    private void DisposeSourceSafely(ICaptureSource source)
    {
        if (_captureThreadId is int id && id == _currentThreadId())
            _disposeScheduler(source.Dispose);
        else
            source.Dispose();
    }
```
> `SwapOutAndDisposeLocked` runs under `_lock`. `DisposeSourceSafely` must NOT re-enter `_lock`; it only calls `source.Dispose()` (inline or scheduled). The scheduled dispose runs later on a pool thread and does not touch coordinator state, so this is safe. The existing outside-the-lock dispose in `OnSourceStopped` (~line 173) may also route through `DisposeSourceSafely` for consistency, but is not required for these tests.

- [ ] **Step 5: Run the tests and verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, `Failed: 0` — including the pre-existing coordinator tests (new ctor params are optional, so `ConcurrencyHammer_*` etc. still compile and pass).

- [ ] **Step 6: Commit (separate commit)**

```bash
git add src/Winpepper.Audio/WarmCaptureCoordinator.cs tests/Winpepper.Audio.Tests/FakeCaptureSource.cs tests/Winpepper.Audio.Tests/WarmCaptureCoordinatorTests.cs
git commit -m "fix(audio): schedule capture-source dispose off-thread to avoid self-join deadlock"
```

---

## Final: Full non-Windows suite gate

- [ ] **Run every pure-managed suite and confirm all green**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"

dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll

dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll -notrait "Platform=Windows"

dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"

dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows"
```
Expected: every run ends `Failed: 0`.

---

## Windows Smoke Checklist (manual, on the Windows VM)

The `#if WINDOWS` wiring (Tasks 17–18) and end-to-end cloud behavior cannot run on Linux. On the Windows build, verify:

- **S-ASR-1 — Test key (typed vs saved):** In Recording settings, with a *saved valid* key, click **Test** with the PasswordBox empty → "Saved key is valid." Paste a *different, valid* key and click **Test** → "Typed key is valid and was saved on this PC." (the typed key is what gets validated & saved). Paste a *bad* key → "Typed key rejected (401)…".
- **S-ASR-2 — Model combo:** The Model dropdown lists `universal-2 (fast)` and `universal-3-pro (premium)` plus `Advanced / custom…`. Selecting a known id persists it; selecting Advanced reveals the custom textbox + the "not validated" caution.
- **S-ASR-3 — Cloud dictation + deletion logged:** With AssemblyAI selected and deletion ON, dictate a sentence; text is injected; the log shows `AssemblyAI transcript <id> created`, `completed`, and `deleted`. With deletion OFF, no `deleted` line appears.
- **S-ASR-4 — Deadline fallback timing:** Simulate a slow/blocked cloud (e.g. bad network); dictation falls back to local within ~the configured `AssemblyAiCloudDeadlineSeconds` (default 10s), NOT 30–45s. The mic-to-text round trip does not hang for the old 45s worst case.
- **S-ASR-5 — Invalid model surfaced:** Set a bogus custom model id, dictate → local text still appears AND a persistent config-error status shows on the settings page + a toast fires (via ErrorBus), pointing at the model setting. It does NOT silently fall back forever with no signal.
- **S-ASR-6 — Cloud skips LLM cleanup:** With AssemblyAI producing the transcript, the history entry's cleanup/model field records the corrections-only path (`none (cloud, corrections-only)`), and a `corrections.json` Replacement is applied (custom_spelling) while the local LLM cleanup step is skipped (faster).
- **S-ASR-7 — Privacy disclosure:** The Speech-recognition card states audio is sent to AssemblyAI and that Winpepper *requests deletion* after transcription (deletion is performed on AssemblyAI's servers and may not be immediate; retained if deletion is disabled). Copy must NOT over-promise instant/guaranteed erasure — DELETE initiates removal subject to AssemblyAI's retention/lag. Onboarding/settings copy matches.
- **S5 (warm-mic, from `2026-07-21-harden-warm-mic-capture.md`):** Re-run the repeated start/rebuild/unplug stress loop with the hang watchdog, including the capture-thread self-join sub-case (fault-driven `RecordingStopped` on the capture thread during teardown). No deadlock; the watchdog does not trip.

---

## Notes / Design Decisions

- **Short-deadline over racing:** The council considered running local + cloud in parallel and racing them. This plan chooses the simpler short-deadline approach (one owned budget in `FallbackTranscriber`, immediate local fallback on deadline). Racing doubles compute for every utterance and complicates cancellation; the short deadline delivers the same "no long dead air" guarantee with far less machinery. If real-world data later shows the deadline is frequently hit on good networks, revisit racing as a follow-up.
- **Logic pushed into pure-managed libraries:** Every decision (retry, budget, status handling, model validation, error classification, corrections mapping, cloud detection, cleanup skip, dispose scheduling) lives in Linux-testable code with fakes. The WinUI layer only assembles tested pieces — which is why Tasks 17–18 have no Linux tests and lean on the smoke checklist.
- **No UNRESOLVED COVERAGE GAPS.** Every spec item maps to a covering task (see the spec-coverage map the executor can reconstruct from task titles): fix 1→T6+T14, fix 2→T5, fix 3→T3, fix 4→T1+T8+T14+T17+T18, fix 5→T18, fix 6→T11(models)+T13(detect)+T15(surface)+T18(combo/label), fix 7→T2+T5+T14+T15+T17, fix 8→T7+T10+T14+T17+T18, fix 9→T12+T16+T17, fix 10→T2+T14, fix 11→T4+T5(exhaustion)+T6(error statuses)+T9(validate)+T14(id logging), separate warm-mic→T19.
