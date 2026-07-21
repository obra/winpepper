# AssemblyAI Cloud ASR Provider Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Add AssemblyAI as an optional premium cloud ASR provider alongside the existing local Parakeet ASR, with robust automatic fallback to local so the user always gets their dictation.

**Architecture:** Introduce a small pure-managed `ITranscriber` seam in `Winpepper.Asr`. `ParakeetTranscriber` adapts the existing `ParakeetSession`; `AssemblyAiTranscriber` uploads an in-memory 16 kHz PCM WAV via raw `HttpClient`, creates a batch transcript, and polls to completion. A `FallbackTranscriber` wraps the cloud provider and falls back to local on any failure, recording which provider actually produced the text. The API key is stored encrypted at rest via a Windows DPAPI seam (`IApiKeyProtector`) that is faked in tests. Selection is driven by two new `AppSettings` fields. All network/retry/WAV/fallback/key-store logic is pure managed code tested on Linux with fakes; the WinUI settings page, DPAPI concrete impl, and `PipelineHost`/`AppShell` wiring are Windows-only and verified via a smoke checklist.

**Tech Stack:** .NET 9, C#, `System.Net.Http.HttpClient`, `System.Text.Json`, `System.Security.Cryptography.ProtectedData` (Windows DPAPI), Serilog via `Microsoft.Extensions.Logging`, xUnit v3 + Shouldly (Linux in-process runner), WinUI 3 (settings UI, Windows-only).

## Global Constraints

- Target framework for all buildable/testable work: **`net9.0`** only (never a `net9.0-windows*` TFM on Linux). Copy verbatim.
- **`Winpepper.App` (WinUI) does not build or test on Linux** — because it targets a Windows-only TFM (`net9.0-windows10.0.19041.0`) with WinUI/WindowsAppSDK dependencies, **not** because of `#if WINDOWS` guards. **Never build the whole solution on Linux**: a solution-wide `dotnet build winpepper.sln` WILL attempt to build the App and fail. Always scope build/test to the specific class-lib + test projects (as every task below does). Note: the `SKIP_WINUI_LINUX` / `BuildProjectReferences=false` guard in `Directory.Build.props` is currently **inert** (its `'$(EnableWindowsTargeting)' != 'true'` clause can never be true because `EnableWindowsTargeting` is set `true` unconditionally at `Directory.Build.props:15`); project scoping — not that guard, and not `#if WINDOWS` — is what keeps Linux builds green. New App files are still wrapped in `#if WINDOWS` as defense-in-depth. App-layer work (settings UI, DPAPI concrete impl, `PipelineHost`/`AppShell` wiring) is verified only via the Windows Smoke Test Checklist (Task 14).
- **No network calls in tests.** The real AssemblyAI API needs a key the developer supplies at runtime; all provider tests inject a fake `HttpMessageHandler` or a fake `IAssemblyAiClient`.
- **Never log the API key.** Log only transcript id + timings + byte counts.
- **Do NOT touch** the keyboard hook or anything under `packaging/`.
- **Nullable reference violations are build errors** (`WarningsAsErrors=nullable`). Code must be null-clean.
- AssemblyAI HTTP contract (validated 2026-07-21 against official AssemblyAI docs + a live read-only probe; corrections folded in below — do not re-research further):
  - Auth header is `authorization: <API_KEY>` with **NO `Bearer` prefix**. (Confirmed: official docs + a live bogus-key probe returning 401.)
  - Upload: `POST https://api.assemblyai.com/v2/upload`, body = **RAW audio bytes** (`ByteArrayContent`, `Content-Type: application/octet-stream`) — never JSON/multipart — returns `{ "upload_url": ... }`.
  - Create: `POST /v2/transcript` json `{"audio_url":...,"speech_models":[<model>],"format_text":true,"punctuate":true,"disfluencies":false,"language_code":"en_us"}` returns `{ "id", "status":"queued" }`. **Use `speech_models` (plural array), NOT the singular `speech_model`** — the singular is documented as **deprecated / replaced by `speech_models`** and may become a no-op that silently ignores the user's model choice. (`language_code:"en_us"` is a valid AssemblyAI code; if a future API change rejects it, `create` fails and the app falls back to local — see Task 13 step 5.)
  - Poll: `GET /v2/transcript/{id}` every ~1s; lifecycle `queued -> processing -> completed | error`; `completed` gives `text`, `words[]`, `confidence`, `audio_duration`.
  - Model id is a **plain configurable string** placed inside the `speech_models` array; default `"universal-2"` (fast/cheap — a valid current enum value). The premium example is **`"universal-3-5-pro"`** (note the `-5-`; the current AssemblyAI `speech_models` enum is `universal-3-5-pro` / `universal-2` — there is **no** `universal-3-pro`). If the API rejects a model id, surface the API's error message — do not hardcode assumptions.
  - Error handling: `401` = bad key (no retry, tell user to check key); `429` = honor `Retry-After` header seconds; `500/502/503/504` = exponential backoff + jitter retry (up to 3 attempts total); `400/404` = no retry.
  - **Never re-POST the transcript-create call after an id is returned** — retry only the GET poll.
  - Cap total transcription wall-clock at **45s default**, then treat as failure.
- **Local model stays loaded even when AssemblyAI is selected** — fallback needs it. Keep the current `ParakeetSession` lifecycle unchanged (loaded once, owned by `PipelineHost`). Memory tradeoff: the ONNX model remains resident; this is intentional and required for instant fallback. **Concurrency invariant:** a single `ParakeetSession` is safe to reuse as the fallback target *because* dictations are serialized by `PipelineHost`'s single-consumer, awaited event pump (`PipelineHost.cs:162-164`) plus the `_engine.State` gates — `Transcribe` is never invoked on two threads at once. If any future path ever invokes a transcriber (cloud or local) **off** that serialized pump (e.g. a parallel re-run), ONNX reentrancy safety must be re-verified before doing so.
- **Streaming is out of scope** (spec-sanctioned) — batch flow only; noted as future work.
- **`custom_spelling` from corrections.json is an OPTIONAL stretch** the spec permits skipping — it is intentionally **not** included to keep the plan focused; noted as future work. (This is an explicit spec-granted scope decision, not a silent deferral of required behavior.)

### One-time environment setup (run once before any test step)

`dotnet` is not on PATH; the SDK is provisioned into gitignored `./.dotnet/`:

```bash
cd /home/dan/code/winpepper/.worktrees/assemblyai-asr-provider
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
```

Re-run the two `export` lines in every new shell. (A pre-existing .NET 9.0.x SDK already on `PATH` also satisfies `global.json`'s `9.0.100` pin via `rollForward: latestFeature`, so a fresh `./.dotnet` provision is not strictly required if `dotnet --version` already resolves 9.0.x inside the repo.) The build-then-`dotnet exec <dll> -notrait "Platform=Windows"` flow below is the proven-portable runner used throughout. Plain `dotnet test <project>` was observed working (15/15) during plan validation and is equivalent, but the `dotnet exec` path is retained as known-good.

---

## File Structure

**New — `src/Winpepper.Asr/Transcription/` (pure managed, Linux-buildable):**
- `ITranscriber.cs` — `ITranscriber` interface + `TranscriptionResult` record (the seam).
- `PcmWavEncoder.cs` — float samples -> in-memory 16 kHz mono int16 WAV `byte[]`.
- `FallbackTranscriber.cs` — wraps primary+local, falls back on any failure, records provider.
- `ParakeetTranscriber.cs` — adapts `ParakeetSession` to `ITranscriber`.
- `IApiKeyProtector.cs` — DPAPI seam (Protect/Unprotect bytes).
- `AssemblyAiKeyStore.cs` — `IAssemblyAiKeyStore` + file-backed impl using the protector.
- `AssemblyAiOptions.cs` — model, base URL, timeouts, retry count.
- `AssemblyAiException.cs` — typed error carrying status code + auth flag.
- `AssemblyAiClient.cs` — `IAssemblyAiClient` + raw-HTTP impl with retry/backoff/Retry-After.
- `AssemblyAiTranscriber.cs` — `ITranscriber` impl orchestrating encode -> upload -> create -> poll.

**New — `src/Winpepper.App/Asr/` (Windows-only, `#if WINDOWS`):**
- `DpapiApiKeyProtector.cs` — thin `IApiKeyProtector` over `ProtectedData` (CurrentUser).

**Modified:**
- `src/Winpepper.Core/Settings/AppSettings.cs` — add `AsrProvider`, `AssemblyAiModel`.
- `src/Winpepper.App/Hosting/AppPaths.cs` — add `AssemblyAiKeyFile`.
- `src/Winpepper.App/Hosting/AppShell.cs` — construct provider stack, wire into `PipelineHost` (Windows-only).
- `src/Winpepper.App/Hosting/PipelineHost.cs` — select transcriber per dictation, record actual provider, surface fallback notice (Windows-only).
- `src/Winpepper.App/Views/RecordingPage.xaml` + `.xaml.cs` — new "Speech recognition" section (Windows-only).

**Tests — `tests/Winpepper.Asr.Tests/`** (⚠️ this project **already exists** in `winpepper.sln` and already references `xunit.v3` + `Shouldly` + a `Winpepper.Asr` ProjectReference — **add these files to it; do NOT scaffold a new project or a new `.csproj`**)**:**
- `PcmWavEncoderTests.cs`, `FallbackTranscriberTests.cs`, `AssemblyAiKeyStoreTests.cs`,
  `AssemblyAiClientTests.cs`, `AssemblyAiTranscriberTests.cs`,
  and test doubles `FakeHttpMessageHandler.cs`, `FakeApiKeyProtector.cs`, `FakeTranscriber.cs`, `FakeAssemblyAiClient.cs`.

**Modified tests — `tests/Winpepper.Core.Tests/`:**
- `AppSettingsDefaultsTests.cs` (add cases for the two new fields; create if absent).

---

## Task 1: AppSettings provider fields + defaults test

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs` (add two fields after line 15, the `AsrModelName` line)
- Test: `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `AppSettings.AsrProvider` (`string`, default `"local"`), `AppSettings.AssemblyAiModel` (`string`, default `"universal-2"`). Later Windows wiring reads these to pick a transcriber.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs`:

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests;

public sealed class AppSettingsDefaultsTests
{
    [Fact]
    public void Defaults_UseLocalProvider()
    {
        var s = new AppSettings();
        s.AsrProvider.ShouldBe("local");
    }

    [Fact]
    public void Defaults_UseFastAssemblyAiModel()
    {
        var s = new AppSettings();
        s.AssemblyAiModel.ShouldBe("universal-2");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — compile error, `AppSettings` has no `AsrProvider` / `AssemblyAiModel`.

- [ ] **Step 3: Add the fields**

In `src/Winpepper.Core/Settings/AppSettings.cs`, immediately after the `AsrModelName` property (line 15), add:

```csharp
    // ASR provider selection
    public string AsrProvider { get; init; } = "local"; // "local" | "assemblyai"
    public string AssemblyAiModel { get; init; } = "universal-2"; // speech_model id sent to AssemblyAI
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, both facts green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Settings/AppSettings.cs tests/Winpepper.Core.Tests/AppSettingsDefaultsTests.cs
git commit -m "feat: add AsrProvider and AssemblyAiModel settings with local defaults"
```

---

## Task 2: In-memory PCM WAV encoder

**Files:**
- Create: `src/Winpepper.Asr/Transcription/PcmWavEncoder.cs`
- Test: `tests/Winpepper.Asr.Tests/PcmWavEncoderTests.cs` (create)

**Interfaces:**
- Consumes: nothing.
- Produces: `static byte[] Winpepper.Asr.Transcription.PcmWavEncoder.EncodeMono16k(ReadOnlySpan<float> samples)` — a complete RIFF/WAVE, 16 kHz, mono, 16-bit PCM blob. Used by `AssemblyAiTranscriber` (Task 6) as the upload body.

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/PcmWavEncoderTests.cs`:

```csharp
using System.Text;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class PcmWavEncoderTests
{
    [Fact]
    public void Encode_WritesRiffWaveHeader()
    {
        var wav = PcmWavEncoder.EncodeMono16k(new float[] { 0f, 0f, 0f, 0f });
        Encoding.ASCII.GetString(wav, 0, 4).ShouldBe("RIFF");
        Encoding.ASCII.GetString(wav, 8, 4).ShouldBe("WAVE");
        Encoding.ASCII.GetString(wav, 12, 4).ShouldBe("fmt ");
        Encoding.ASCII.GetString(wav, 36, 4).ShouldBe("data");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // channels
        BitConverter.ToInt32(wav, 24).ShouldBe(16000);          // sample rate
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);      // bits per sample
    }

    [Fact]
    public void Encode_DataChunkLengthAndSampleConversionAreCorrect()
    {
        var wav = PcmWavEncoder.EncodeMono16k(new float[] { 0f, 1f, -1f });
        // header is 44 bytes; 3 samples * 2 bytes = 6 data bytes
        BitConverter.ToInt32(wav, 40).ShouldBe(6);
        wav.Length.ShouldBe(44 + 6);
        BitConverter.ToInt16(wav, 44).ShouldBe((short)0);          // 0.0  -> 0
        BitConverter.ToInt16(wav, 46).ShouldBe(short.MaxValue);    // +1.0 -> 32767
        BitConverter.ToInt16(wav, 48).ShouldBe(short.MinValue);    // -1.0 -> -32768
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `PcmWavEncoder` does not exist.

- [ ] **Step 3: Write the encoder**

Create `src/Winpepper.Asr/Transcription/PcmWavEncoder.cs`:

```csharp
using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Encodes mono 16 kHz float samples ([-1,+1]) to an in-memory RIFF/WAVE
/// 16-bit PCM blob suitable for AssemblyAI's raw-bytes upload endpoint.
/// Mirrors the on-disk conversion in Winpepper.History.WavWriter.
/// </summary>
public static class PcmWavEncoder
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public static byte[] EncodeMono16k(ReadOnlySpan<float> samples)
    {
        var byteRate = SampleRate * Channels * (BitsPerSample / 8);
        var blockAlign = (short)(Channels * (BitsPerSample / 8));
        var dataBytes = samples.Length * (BitsPerSample / 8);

        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);            // PCM fmt chunk size
        w.Write((short)1);      // PCM
        w.Write(Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(BitsPerSample);

        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1.0f, 1.0f);
            short pcm;
            if (clamped >= 0f) pcm = (short)Math.Round(clamped * short.MaxValue);
            else pcm = (short)Math.Round(clamped * -(double)short.MinValue);
            w.Write(pcm);
        }

        w.Flush();
        return ms.ToArray();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/PcmWavEncoder.cs tests/Winpepper.Asr.Tests/PcmWavEncoderTests.cs
git commit -m "feat: add in-memory PCM WAV encoder for cloud ASR upload"
```

---

## Task 3: ITranscriber seam + FallbackTranscriber

**Files:**
- Create: `src/Winpepper.Asr/Transcription/ITranscriber.cs`
- Create: `src/Winpepper.Asr/Transcription/FallbackTranscriber.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeTranscriber.cs`
- Test: `tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `record TranscriptionResult(string Text, string ProviderModelName)`.
  - `interface ITranscriber { string ModelName { get; } Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct); }`.
  - `FallbackTranscriber(ITranscriber primary, ITranscriber local, ILogger<FallbackTranscriber> logger, Action<string>? onFallback = null)` — `ITranscriber` whose result's `ProviderModelName` reflects whichever provider actually produced the text. Consumed by Windows wiring (Task 9/10).

- [ ] **Step 1: Write the failing test**

Create the fake first — `tests/Winpepper.Asr.Tests/FakeTranscriber.cs`:

```csharp
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>A configurable ITranscriber test double.</summary>
public sealed class FakeTranscriber : ITranscriber
{
    private readonly Func<Task<TranscriptionResult>> _behavior;
    public int Calls { get; private set; }

    public FakeTranscriber(string modelName, Func<Task<TranscriptionResult>> behavior)
    {
        ModelName = modelName;
        _behavior = behavior;
    }

    public static FakeTranscriber Returning(string modelName, string text)
        => new(modelName, () => Task.FromResult(new TranscriptionResult(text, modelName)));

    public static FakeTranscriber Throwing(string modelName, Exception ex)
        => new(modelName, () => throw ex);

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        Calls++;
        return _behavior();
    }
}
```

Then `tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class FallbackTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0f, 0f };

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
    public async Task PrimaryFails_FallsBackToLocalAndRecordsLocalProvider()
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
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `ITranscriber` / `TranscriptionResult` / `FallbackTranscriber` do not exist.

- [ ] **Step 3: Write the seam and fallback**

Create `src/Winpepper.Asr/Transcription/ITranscriber.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Result of a single dictation transcription.</summary>
/// <param name="Text">The recognized text.</param>
/// <param name="ProviderModelName">
/// Identifier of the provider/model that actually produced the text,
/// e.g. "assemblyai/universal-2" or "parakeet-tdt-0.6b-v3". Stamped onto history.
/// </param>
public sealed record TranscriptionResult(string Text, string ProviderModelName);

/// <summary>Transcribes mono 16 kHz float samples to text.</summary>
public interface ITranscriber
{
    /// <summary>The model identifier this transcriber would report on success.</summary>
    string ModelName { get; }

    Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct);
}
```

Create `src/Winpepper.Asr/Transcription/FallbackTranscriber.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// Runs a primary (cloud) transcriber; on ANY non-cancellation failure it
/// transparently falls back to the local transcriber so the user always gets
/// their dictation. The returned result's ProviderModelName reflects whichever
/// provider actually produced the text.
/// </summary>
public sealed class FallbackTranscriber : ITranscriber
{
    private readonly ITranscriber _primary;
    private readonly ITranscriber _local;
    private readonly ILogger<FallbackTranscriber> _log;
    private readonly Action<string>? _onFallback;

    public FallbackTranscriber(
        ITranscriber primary,
        ITranscriber local,
        ILogger<FallbackTranscriber> logger,
        Action<string>? onFallback = null)
    {
        _primary = primary;
        _local = local;
        _log = logger;
        _onFallback = onFallback;
    }

    public string ModelName => _primary.ModelName;

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        try
        {
            return await _primary.TranscribeAsync(mono16k, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user aborted the dictation — do not run local as well
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cloud transcription failed; falling back to local ASR");
            _onFallback?.Invoke(ex.Message);
            return await _local.TranscribeAsync(mono16k, ct);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS (all three fallback tests + earlier tests).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/ITranscriber.cs src/Winpepper.Asr/Transcription/FallbackTranscriber.cs tests/Winpepper.Asr.Tests/FakeTranscriber.cs tests/Winpepper.Asr.Tests/FallbackTranscriberTests.cs
git commit -m "feat: add ITranscriber seam and fallback transcriber with provider recording"
```

---

## Task 4: Encrypted API key store + DPAPI seam

**Files:**
- Create: `src/Winpepper.Asr/Transcription/IApiKeyProtector.cs`
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiKeyStore.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeApiKeyProtector.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiKeyStoreTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `interface IApiKeyProtector { byte[] Protect(byte[] plaintext); byte[] Unprotect(byte[] ciphertext); }`.
  - `interface IAssemblyAiKeyStore { bool HasKey { get; } void Save(string apiKey); string? Load(); void Clear(); }`.
  - `class AssemblyAiKeyStore(string filePath, IApiKeyProtector protector) : IAssemblyAiKeyStore`. Consumed by `AssemblyAiClient`/`AssemblyAiTranscriber` (via a key provider) and Windows settings UI (Task 9/11).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/FakeApiKeyProtector.cs`:

```csharp
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Reversible byte transform standing in for Windows DPAPI in tests.</summary>
public sealed class FakeApiKeyProtector : IApiKeyProtector
{
    private const byte Mask = 0x5A;

    public byte[] Protect(byte[] plaintext) => Xor(plaintext);
    public byte[] Unprotect(byte[] ciphertext) => Xor(ciphertext);

    private static byte[] Xor(byte[] input)
    {
        var output = new byte[input.Length];
        for (var i = 0; i < input.Length; i++) output[i] = (byte)(input[i] ^ Mask);
        return output;
    }
}
```

Then `tests/Winpepper.Asr.Tests/AssemblyAiKeyStoreTests.cs`:

```csharp
using System.Text;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiKeyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aai-key-{Guid.NewGuid():N}.dat");

    [Fact]
    public void SaveThenLoad_RoundTripsKey()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.HasKey.ShouldBeFalse();

        store.Save("secret-key-123");

        store.HasKey.ShouldBeTrue();
        store.Load().ShouldBe("secret-key-123");
    }

    [Fact]
    public void SavedFile_IsNotPlaintext()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.Save("secret-key-123");

        var onDisk = File.ReadAllBytes(_path);
        Encoding.UTF8.GetString(onDisk).ShouldNotContain("secret-key-123");
    }

    [Fact]
    public void Clear_RemovesKey()
    {
        var store = new AssemblyAiKeyStore(_path, new FakeApiKeyProtector());
        store.Save("secret-key-123");

        store.Clear();

        store.HasKey.ShouldBeFalse();
        store.Load().ShouldBeNull();
    }

    [Fact]
    public void Load_UndecryptableBlob_ReturnsNullInsteadOfThrowing()
    {
        // Simulate a DPAPI blob that cannot be decrypted (different user/machine,
        // or corruption): Unprotect throws CryptographicException. Load() must
        // degrade to "no usable key" so the app falls back to local + re-prompts.
        File.WriteAllBytes(_path, new byte[] { 1, 2, 3, 4 });
        var store = new AssemblyAiKeyStore(_path, new ThrowingApiKeyProtector());

        store.Load().ShouldBeNull();
    }

    private sealed class ThrowingApiKeyProtector : IApiKeyProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext)
            => throw new System.Security.Cryptography.CryptographicException("cannot decrypt");
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `IApiKeyProtector` / `AssemblyAiKeyStore` do not exist.

- [ ] **Step 3: Write the seam and store**

Create `src/Winpepper.Asr/Transcription/IApiKeyProtector.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>
/// Encrypts/decrypts small secrets at rest. The Windows implementation wraps
/// DPAPI (ProtectedData, CurrentUser scope); tests inject a reversible fake.
/// </summary>
public interface IApiKeyProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
```

Create `src/Winpepper.Asr/Transcription/AssemblyAiKeyStore.cs`:

```csharp
using System.Text;

namespace Winpepper.Asr.Transcription;

/// <summary>Stores/loads the AssemblyAI API key, encrypted at rest.</summary>
public interface IAssemblyAiKeyStore
{
    bool HasKey { get; }
    void Save(string apiKey);
    string? Load();
    void Clear();
}

/// <summary>
/// File-backed key store. The key is protected via <see cref="IApiKeyProtector"/>
/// and written to a single file (e.g. %LOCALAPPDATA%\winpepper\assemblyai.key.dat).
/// settings.json never contains the key — presence is derived from file existence.
/// </summary>
public sealed class AssemblyAiKeyStore : IAssemblyAiKeyStore
{
    private readonly string _path;
    private readonly IApiKeyProtector _protector;

    public AssemblyAiKeyStore(string filePath, IApiKeyProtector protector)
    {
        _path = filePath;
        _protector = protector;
    }

    public bool HasKey => File.Exists(_path);

    public void Save(string apiKey)
    {
        var parent = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        var cipher = _protector.Protect(Encoding.UTF8.GetBytes(apiKey));
        File.WriteAllBytes(_path, cipher);
    }

    public string? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = _protector.Unprotect(File.ReadAllBytes(_path));
            return Encoding.UTF8.GetString(plain);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // DPAPI CurrentUser blobs are non-portable: a key file from a different
            // user/machine (or a corrupt file) cannot be decrypted. Treat as "no usable
            // key" so the app degrades to local fallback and can re-prompt, rather than
            // throwing on every dictation attempt.
            return null;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/IApiKeyProtector.cs src/Winpepper.Asr/Transcription/AssemblyAiKeyStore.cs tests/Winpepper.Asr.Tests/FakeApiKeyProtector.cs tests/Winpepper.Asr.Tests/AssemblyAiKeyStoreTests.cs
git commit -m "feat: add encrypted AssemblyAI key store with DPAPI protector seam"
```

---

## Task 5: AssemblyAI HTTP client (raw bytes, retry, Retry-After, key validation)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiException.cs`
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `class AssemblyAiOptions { string BaseUrl="https://api.assemblyai.com"; string Model="universal-2"; string LanguageCode="en_us"; TimeSpan TotalTimeout=45s; TimeSpan PollInterval=1s; int MaxTransientRetries=3; }` (all `init`).
  - `class AssemblyAiException : Exception { int? StatusCode; bool IsAuthError; }`.
  - `record AssemblyAiTranscript(string Status, string? Text, double? Confidence, double? AudioDuration, string? Error)`.
  - `interface IAssemblyAiClient { Task<string> UploadAsync(byte[] audio, CancellationToken ct); Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct); Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct); Task<bool> ValidateKeyAsync(CancellationToken ct); }`.
  - `class AssemblyAiClient(HttpClient http, Func<string?> apiKeyProvider, AssemblyAiOptions options, ILogger<AssemblyAiClient> logger, Func<TimeSpan, CancellationToken, Task>? delay = null) : IAssemblyAiClient`. Consumed by `AssemblyAiTranscriber` (Task 6) and the settings Test button (Task 11).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs`:

```csharp
using System.Net;

namespace Winpepper.Asr.Tests;

/// <summary>Queues scripted responses and records every request for assertions.</summary>
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken));
        if (_responses.Count == 0) throw new InvalidOperationException("No scripted response left.");
        return _responses.Dequeue()(request);
    }
}
```

Then `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiClientTests
{
    private static AssemblyAiClient Make(FakeHttpMessageHandler handler, List<TimeSpan> delays, string? key = "KEY")
    {
        var http = new HttpClient(handler);
        var opts = new AssemblyAiOptions { MaxTransientRetries = 3 };
        return new AssemblyAiClient(http, () => key, opts, NullLogger<AssemblyAiClient>.Instance,
            (ts, _) => { delays.Add(ts); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Upload_SendsRawBytesWithBareAuthHeader()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/xyz\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var body = new byte[] { 1, 2, 3, 4 };
        var url = await client.UploadAsync(body, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/xyz");
        var req = handler.Requests[0];
        req.Method.ShouldBe(HttpMethod.Post);
        req.RequestUri!.ToString().ShouldEndWith("/v2/upload");
        req.Headers.GetValues("authorization").ShouldContain("KEY"); // no "Bearer "
        req.Content!.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        handler.RequestBodies[0].ShouldBe(body); // raw bytes, not JSON/multipart
    }

    [Fact]
    public async Task Upload_401_ThrowsAuthErrorWithoutRetry()
    {
        var handler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => client.UploadAsync(new byte[] { 1 }, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
        ex.StatusCode.ShouldBe(401);
        handler.Requests.Count.ShouldBe(1);
        delays.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Upload_429_HonorsRetryAfterThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, "{}", mutate: r => r.Headers.TryAddWithoutValidation("Retry-After", "2"))
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1);
        delays[0].ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Upload_503_BacksOffThenSucceeds()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "{}")
            .Enqueue(HttpStatusCode.OK, "{\"upload_url\":\"https://cdn/aai/ok\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var url = await client.UploadAsync(new byte[] { 1 }, CancellationToken.None);

        url.ShouldBe("https://cdn/aai/ok");
        handler.Requests.Count.ShouldBe(2);
        delays.Count.ShouldBe(1);
        delays[0].ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task CreateTranscript_SendsSpeechModelPayload_ReturnsId()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"t-123\",\"status\":\"queued\"}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var id = await client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-2", CancellationToken.None);

        id.ShouldBe("t-123");
        var json = Encoding.UTF8.GetString(handler.RequestBodies[0]);
        json.ShouldContain("\"speech_models\":[\"universal-2\"]"); // plural array (singular is deprecated)
        json.ShouldContain("\"audio_url\":\"https://cdn/aai/ok\"");
        json.ShouldContain("\"format_text\":true");
        json.ShouldContain("\"punctuate\":true");
        json.ShouldContain("\"disfluencies\":false");
        json.ShouldContain("\"language_code\":\"en_us\"");
    }

    [Fact]
    public async Task GetTranscript_ParsesCompletedFields()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"status\":\"completed\",\"text\":\"hello world\",\"confidence\":0.97,\"audio_duration\":3.2}");
        var delays = new List<TimeSpan>();
        var client = Make(handler, delays);

        var tr = await client.GetTranscriptAsync("t-123", CancellationToken.None);

        tr.Status.ShouldBe("completed");
        tr.Text.ShouldBe("hello world");
        tr.Confidence.ShouldBe(0.97);
        tr.AudioDuration.ShouldBe(3.2);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Get);
        handler.Requests[0].RequestUri!.ToString().ShouldEndWith("/v2/transcript/t-123");
    }

    [Fact]
    public async Task ValidateKey_404MeansValid_401MeansBadKey()
    {
        var goodHandler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not found\"}");
        var badHandler = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Unauthorized, "{\"error\":\"bad key\"}");
        var delays = new List<TimeSpan>();

        (await Make(goodHandler, delays).ValidateKeyAsync(CancellationToken.None)).ShouldBeTrue();
        (await Make(badHandler, delays).ValidateKeyAsync(CancellationToken.None)).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — client/options/exception types do not exist.

- [ ] **Step 3: Write options, exception, and client**

Create `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

public sealed class AssemblyAiOptions
{
    public string BaseUrl { get; init; } = "https://api.assemblyai.com";
    public string Model { get; init; } = "universal-2";
    public string LanguageCode { get; init; } = "en_us";
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int MaxTransientRetries { get; init; } = 3;
}
```

Create `src/Winpepper.Asr/Transcription/AssemblyAiException.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Raised when an AssemblyAI request fails in a non-recoverable way.</summary>
public sealed class AssemblyAiException : Exception
{
    public int? StatusCode { get; }
    public bool IsAuthError { get; }

    public AssemblyAiException(string message, int? statusCode = null, bool isAuthError = false, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        IsAuthError = isAuthError;
    }
}
```

Create `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>A single AssemblyAI batch-transcript status snapshot.</summary>
public sealed record AssemblyAiTranscript(string Status, string? Text, double? Confidence, double? AudioDuration, string? Error);

public interface IAssemblyAiClient
{
    Task<string> UploadAsync(byte[] audio, CancellationToken ct);
    Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct);
    Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct);
    Task<bool> ValidateKeyAsync(CancellationToken ct);
}

/// <summary>
/// Raw-HttpClient AssemblyAI batch client. There is no maintained official C#
/// SDK, so every call is hand-built. Retry policy: transient 5xx/429/network
/// errors are retried with backoff (429 honors Retry-After); 401/400/404 are
/// terminal. The create-transcript POST is only retried before an id exists.
/// </summary>
public sealed class AssemblyAiClient : IAssemblyAiClient
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiClient> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Random _rng = new();

    public AssemblyAiClient(
        HttpClient http,
        Func<string?> apiKeyProvider,
        AssemblyAiOptions options,
        ILogger<AssemblyAiClient> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _apiKey = apiKeyProvider;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public async Task<string> UploadAsync(byte[] audio, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v2/upload");
            var content = new ByteArrayContent(audio);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            req.Content = content;
            return req;
        }, ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ReadString(json, "upload_url");
    }

    public async Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct)
    {
        var payload = new
        {
            audio_url = audioUrl,
            speech_models = new[] { model }, // plural array — singular `speech_model` is deprecated
            format_text = true,
            punctuate = true,
            disfluencies = false,
            language_code = _opts.LanguageCode,
        };
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

    public async Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
    {
        using var resp = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/{id}"), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new AssemblyAiTranscript(
            Status: root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "",
            Text: root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
            Confidence: root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
            AudioDuration: root.TryGetProperty("audio_duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
            Error: root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null);
    }

    public async Task<bool> ValidateKeyAsync(CancellationToken ct)
    {
        // GET a bogus id: 401 => bad key; anything else (typically 404) => key accepted.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_opts.BaseUrl}/v2/transcript/winpepper-key-check-000000000000");
        AddAuth(req);
        using var resp = await _http.SendAsync(req, ct);
        return (int)resp.StatusCode != 401;
    }

    private void AddAuth(HttpRequestMessage req)
    {
        var key = _apiKey();
        if (string.IsNullOrEmpty(key))
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);
        req.Headers.TryAddWithoutValidation("authorization", key); // NO "Bearer " prefix
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var req = requestFactory();
            AddAuth(req);

            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, ct);
            }
            catch (HttpRequestException) when (attempt <= _opts.MaxTransientRetries)
            {
                await _delay(Backoff(attempt), ct);
                continue;
            }

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

    private TimeSpan Backoff(int attempt)
        => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 250 + _rng.Next(0, 250)); // exponential + jitter

    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (resp.Headers.TryGetValues("Retry-After", out var values)
            && int.TryParse(values.FirstOrDefault(), out var seconds))
            return TimeSpan.FromSeconds(seconds);
        return null;
    }

    private static string ReadString(string json, string property)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()!;
        throw new AssemblyAiException($"AssemblyAI response missing '{property}'.");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS (all 7 client tests + earlier tests).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs src/Winpepper.Asr/Transcription/AssemblyAiException.cs src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/FakeHttpMessageHandler.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "feat: add raw-HttpClient AssemblyAI batch client with retry and key validation"
```

---

## Task 6: AssemblyAiTranscriber (encode -> upload -> create -> poll)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs`
- Create: `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs`

**Interfaces:**
- Consumes: `IAssemblyAiClient`, `IAssemblyAiKeyStore`, `AssemblyAiOptions`, `PcmWavEncoder`, `TranscriptionResult`/`ITranscriber` (Tasks 2–5).
- Produces: `class AssemblyAiTranscriber(IAssemblyAiClient client, IAssemblyAiKeyStore keyStore, AssemblyAiOptions options, ILogger<AssemblyAiTranscriber> logger, Func<TimeSpan, CancellationToken, Task>? delay = null) : ITranscriber` with `ModelName => $"assemblyai/{options.Model}"`. Consumed by Windows wiring (Task 9).

- [ ] **Step 1: Write the failing test**

Create `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`:

```csharp
using System.Text;
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Scripts upload/create/poll behavior and records the uploaded body.</summary>
public sealed class FakeAssemblyAiClient : IAssemblyAiClient
{
    private readonly Queue<AssemblyAiTranscript> _pollResults = new();
    public byte[]? UploadedBytes { get; private set; }
    public int PollCalls { get; private set; }

    public FakeAssemblyAiClient EnqueuePoll(AssemblyAiTranscript t) { _pollResults.Enqueue(t); return this; }

    public Task<string> UploadAsync(byte[] audio, CancellationToken ct)
    {
        UploadedBytes = audio;
        return Task.FromResult("https://cdn/aai/fake");
    }

    public Task<string> CreateTranscriptAsync(string audioUrl, string model, CancellationToken ct)
        => Task.FromResult("t-fake");

    public Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
    {
        PollCalls++;
        // If a specific result is queued use it; otherwise keep returning "processing".
        var next = _pollResults.Count > 0 ? _pollResults.Dequeue()
            : new AssemblyAiTranscript("processing", null, null, null, null);
        return Task.FromResult(next);
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct) => Task.FromResult(true);

    public string RiffMagic() => UploadedBytes is null ? "" : Encoding.ASCII.GetString(UploadedBytes, 0, 4);
}
```

Then `tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs`:

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

    private static AssemblyAiTranscriber Make(FakeAssemblyAiClient client, bool hasKey = true, TimeSpan? total = null, TimeSpan? poll = null)
    {
        var opts = new AssemblyAiOptions
        {
            Model = "universal-2",
            TotalTimeout = total ?? TimeSpan.FromSeconds(45),
            PollInterval = poll ?? TimeSpan.FromSeconds(1),
        };
        return new AssemblyAiTranscriber(client, new StubKeyStore { HasKey = hasKey }, opts,
            NullLogger<AssemblyAiTranscriber>.Instance, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task HappyPath_UploadsWavAndReturnsTextWithProviderModel()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("processing", null, null, null, null))
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hello from the cloud", 0.95, 4.0, null));
        var transcriber = Make(client);

        var result = await transcriber.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello from the cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        client.RiffMagic().ShouldBe("RIFF"); // uploaded a real WAV
    }

    [Fact]
    public async Task ErrorStatus_ThrowsWithApiMessage()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("error", null, null, null, "Transcoding failed"));
        var transcriber = Make(client);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("Transcoding failed");
    }

    [Fact]
    public async Task NeverCompletes_TimesOutAfterPollBudget()
    {
        var client = new FakeAssemblyAiClient(); // always "processing"
        var transcriber = Make(client, total: TimeSpan.FromSeconds(3), poll: TimeSpan.FromSeconds(1));

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("timed out");
        client.PollCalls.ShouldBe(3); // ceil(3s / 1s)
    }

    [Fact]
    public async Task NoKey_ThrowsAuthError()
    {
        var client = new FakeAssemblyAiClient();
        var transcriber = Make(client, hasKey: false);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: FAIL — `AssemblyAiTranscriber` does not exist.

- [ ] **Step 3: Write the transcriber**

Create `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs`:

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Transcription;

/// <summary>
/// AssemblyAI batch transcriber: encode float samples to WAV, upload raw bytes,
/// create a transcript, then poll to completion. Enforces a total wall-clock cap
/// (via a linked CTS) and a deterministic poll budget (ceil(TotalTimeout/PollInterval)).
/// </summary>
public sealed class AssemblyAiTranscriber : ITranscriber
{
    private readonly IAssemblyAiClient _client;
    private readonly IAssemblyAiKeyStore _keyStore;
    private readonly AssemblyAiOptions _opts;
    private readonly ILogger<AssemblyAiTranscriber> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AssemblyAiTranscriber(
        IAssemblyAiClient client,
        IAssemblyAiKeyStore keyStore,
        AssemblyAiOptions options,
        ILogger<AssemblyAiTranscriber> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _keyStore = keyStore;
        _opts = options;
        _log = logger;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public string ModelName => $"assemblyai/{_opts.Model}";

    public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
    {
        if (!_keyStore.HasKey)
            throw new AssemblyAiException("No AssemblyAI API key configured.", isAuthError: true);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opts.TotalTimeout);
        var token = cts.Token;
        var sw = Stopwatch.StartNew();

        try
        {
            var wav = PcmWavEncoder.EncodeMono16k(mono16k.Span);
            var uploadUrl = await _client.UploadAsync(wav, token);
            var id = await _client.CreateTranscriptAsync(uploadUrl, _opts.Model, token);
            _log.LogInformation("AssemblyAI transcript {Id} created ({Bytes} bytes uploaded)", id, wav.Length);

            var maxPolls = Math.Max(1, (int)Math.Ceiling(_opts.TotalTimeout / _opts.PollInterval));
            for (var i = 0; i < maxPolls; i++)
            {
                var tr = await _client.GetTranscriptAsync(id, token);
                if (tr.Status == "completed")
                {
                    _log.LogInformation("AssemblyAI transcript {Id} completed in {Ms}ms (confidence {Conf})",
                        id, sw.ElapsedMilliseconds, tr.Confidence);
                    return new TranscriptionResult(tr.Text ?? "", ModelName);
                }
                if (tr.Status == "error")
                    throw new AssemblyAiException($"AssemblyAI transcription failed: {tr.Error}");

                await _delay(_opts.PollInterval, token);
            }

            throw new AssemblyAiException($"AssemblyAI transcription timed out after {_opts.TotalTimeout.TotalSeconds:0}s.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The linked CTS fired the wall-clock cap, not the caller's token.
            throw new AssemblyAiException($"AssemblyAI transcription timed out after {_opts.TotalTimeout.TotalSeconds:0}s.");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS (all 4 transcriber tests + earlier tests).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiTranscriberTests.cs
git commit -m "feat: add AssemblyAiTranscriber orchestration with poll budget and timeout"
```

---

## Task 7: ParakeetTranscriber adapter (build-verified)

**Files:**
- Create: `src/Winpepper.Asr/Transcription/ParakeetTranscriber.cs`

**Interfaces:**
- Consumes: `ParakeetSession` (`src/Winpepper.Asr/ParakeetSession.cs`, method `ParakeetTranscript Transcribe(ReadOnlySpan<float> samples16k)`), `ITranscriber`/`TranscriptionResult` (Task 3).
- Produces: `class ParakeetTranscriber(ParakeetSession session, string modelName) : ITranscriber` with `ModelName => modelName`. Consumed by Windows wiring (Task 9) as the local transcriber and fallback target.

> This adapter wraps a concrete `ParakeetSession` that requires ONNX model files at runtime, so it is not unit-testable on Linux (covered by the Windows Smoke Checklist and indirectly by `FallbackTranscriberTests`, which use a fake local `ITranscriber`). Verification here is that `Winpepper.Asr` still compiles.

- [ ] **Step 1: Write the adapter**

Create `src/Winpepper.Asr/Transcription/ParakeetTranscriber.cs`:

```csharp
namespace Winpepper.Asr.Transcription;

/// <summary>Adapts the local ONNX Parakeet session to the ITranscriber seam.</summary>
public sealed class ParakeetTranscriber : ITranscriber
{
    private readonly ParakeetSession _session;

    public ParakeetTranscriber(ParakeetSession session, string modelName)
    {
        _session = session;
        ModelName = modelName;
    }

    public string ModelName { get; }

    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        => Task.Run(() =>
        {
            var transcript = _session.Transcribe(mono16k.Span);
            return new TranscriptionResult(transcript.Text, ModelName);
        }, ct);
}
```

- [ ] **Step 2: Verify the library compiles**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the ASR test suite to confirm no regression**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.Asr/Transcription/ParakeetTranscriber.cs
git commit -m "feat: add ParakeetTranscriber adapter over the local ONNX session"
```

---

## Task 8: Windows DPAPI protector + key-file path (Windows-only)

**Files:**
- Create: `src/Winpepper.App/Asr/DpapiApiKeyProtector.cs`
- Modify: `src/Winpepper.App/Hosting/AppPaths.cs` (add `AssemblyAiKeyFile` after line 11, the `CorrectionsJson` line)

**Interfaces:**
- Consumes: `IApiKeyProtector` (Task 4).
- Produces: `DpapiApiKeyProtector : IApiKeyProtector` (Windows, `#if WINDOWS`); `AppPaths.AssemblyAiKeyFile` (`string` = `%LOCALAPPDATA%\winpepper\assemblyai.key.dat`). Consumed by `AppShell` wiring (Task 9).

> Windows-only (`ProtectedData` is DPAPI). Not Linux-buildable/testable; verified via Smoke Checklist Task 14. The pure-managed round-trip is already proven with the fake protector in Task 4.
>
> **Security model (DPAPI CurrentUser):** the encrypted blob is intentionally **non-portable** — it can be decrypted only by the same Windows user on the same machine, and will not survive a profile reset / OS reinstall. This is correct for a locally-stored key. The `Unprotect` failure path (a blob from another user/machine, or corruption) must degrade gracefully to "no usable key" rather than throw on every dictation — handled in `AssemblyAiKeyStore.Load()` (Task 4, updated to catch `CryptographicException` and return `null`). The app ships **unpackaged** (`WindowsPackageType=None`), so there is no MSIX app-container/roaming caveat.

- [ ] **Step 1: Register the ProtectedData package (Central Package Management)**

This repo uses Central Package Management (`ManagePackageVersionsCentrally=true`) and `System.Security.Cryptography.ProtectedData` is **not currently referenced anywhere**, so it must be added in two places (a versionless `PackageReference` alone will fail to restore under CPM):

In `Directory.Packages.props`, add a stable 9.0.x `PackageVersion` (match the `net9.0-windows` TFM; do **not** use a preview):

```xml
    <PackageVersion Include="System.Security.Cryptography.ProtectedData" Version="9.0.0" />
```

In `src/Winpepper.App/Winpepper.App.csproj`, add the versionless reference (the API is package-delivered even on the `net9.0-windows` TFM):

```xml
    <PackageReference Include="System.Security.Cryptography.ProtectedData" />
```

- [ ] **Step 2: Add the key-file path**

In `src/Winpepper.App/Hosting/AppPaths.cs`, after the `CorrectionsJson` line (line 11) add:

```csharp
    public static string AssemblyAiKeyFile => Path.Combine(Root, "assemblyai.key.dat");
```

- [ ] **Step 3: Add the DPAPI protector**

Create `src/Winpepper.App/Asr/DpapiApiKeyProtector.cs`:

```csharp
#if WINDOWS
using System.Security.Cryptography;
using Winpepper.Asr.Transcription;

namespace Winpepper.App.Asr;

/// <summary>
/// Thin IApiKeyProtector over Windows DPAPI (CurrentUser scope). The ciphertext
/// is bound to the current Windows user account and cannot be decrypted by other
/// users or on other machines.
/// </summary>
public sealed class DpapiApiKeyProtector : IApiKeyProtector
{
    public byte[] Protect(byte[] plaintext)
        => ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext)
        => ProtectedData.Unprotect(ciphertext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
}
#endif
```

- [ ] **Step 4: Confirm the Linux solution build is unaffected**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded (App code is not built on Linux — its Windows-only TFM keeps it out of a project-scoped Linux build; this confirms no cross-project break). Full Windows build is verified in Task 14.

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Winpepper.App/Winpepper.App.csproj src/Winpepper.App/Asr/DpapiApiKeyProtector.cs src/Winpepper.App/Hosting/AppPaths.cs
git commit -m "feat: add Windows DPAPI key protector, ProtectedData package, and assemblyai key-file path"
```

---

## Task 9: Wire the provider stack in AppShell (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (construct the AssemblyAI stack near where `PipelineHost` is built; expose `IAssemblyAiKeyStore`, `AssemblyAiClient`, and a transcriber factory for the settings UI and pipeline)

**Interfaces:**
- Consumes: `AppSettings.AsrProvider`/`AssemblyAiModel` (Task 1), `AssemblyAiKeyStore`/`IAssemblyAiKeyStore` (Task 4), `AssemblyAiClient`/`AssemblyAiOptions` (Task 5), `AssemblyAiTranscriber` (Task 6), `ParakeetTranscriber` (Task 7), `FallbackTranscriber` (Task 3), `DpapiApiKeyProtector`/`AppPaths.AssemblyAiKeyFile` (Task 8), the existing `ParakeetSession` owned by `PipelineHost`, `ILoggerFactory`.
- Produces (new `AppShell` members, exact names the settings UI and pipeline rely on):
  - `public IAssemblyAiKeyStore AssemblyAiKeyStore { get; }`
  - `public AssemblyAiClient AssemblyAiClient { get; }`
  - `public AssemblyAiOptions AssemblyAiOptions { get; private set; }`
  - `public ITranscriber BuildTranscriber(ParakeetSession local, AppSettings settings, Action<string> onFallback)` — returns a bare `ParakeetTranscriber` when `settings.AsrProvider != "assemblyai"`, else a `FallbackTranscriber(assemblyAi, local, ...)`.

> Windows-only wiring (App project). Verified via Smoke Checklist Task 14. The building blocks are all unit-tested (Tasks 3–6).

- [ ] **Step 1: Add fields and construct the stack**

In `src/Winpepper.App/Hosting/AppShell.cs`, inside `BootstrapAsync` (near the existing `PipelineHost` construction), add — using `factory` (the existing `ILoggerFactory`) and `AppPaths`:

```csharp
        // --- AssemblyAI cloud ASR provider stack (optional; key may be absent) ---
        var aaiKeyStore = new Winpepper.Asr.Transcription.AssemblyAiKeyStore(
            AppPaths.AssemblyAiKeyFile, new Winpepper.App.Asr.DpapiApiKeyProtector());
        var aaiHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var aaiOptions = new Winpepper.Asr.Transcription.AssemblyAiOptions { Model = settings.AssemblyAiModel };
        var aaiClient = new Winpepper.Asr.Transcription.AssemblyAiClient(
            aaiHttp,
            () => aaiKeyStore.Load(),
            aaiOptions,
            factory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiClient>());

        AssemblyAiKeyStore = aaiKeyStore;
        AssemblyAiClient = aaiClient;
        AssemblyAiOptions = aaiOptions;
        _aaiLoggerFactory = factory;
```

Add these members to the `AppShell` class body (fields/properties):

```csharp
    public Winpepper.Asr.Transcription.IAssemblyAiKeyStore AssemblyAiKeyStore { get; private set; } = null!;
    public Winpepper.Asr.Transcription.AssemblyAiClient AssemblyAiClient { get; private set; } = null!;
    public Winpepper.Asr.Transcription.AssemblyAiOptions AssemblyAiOptions { get; private set; } = null!;
    private ILoggerFactory _aaiLoggerFactory = null!;

    /// <summary>
    /// Builds the transcriber for a dictation. When AssemblyAI is selected the
    /// cloud provider is wrapped in a FallbackTranscriber so any failure lands
    /// on the local Parakeet session. Otherwise the local transcriber is used.
    /// </summary>
    public Winpepper.Asr.Transcription.ITranscriber BuildTranscriber(
        Winpepper.Asr.ParakeetSession local,
        AppSettings settings,
        Action<string> onFallback)
    {
        var localTranscriber = new Winpepper.Asr.Transcription.ParakeetTranscriber(
            local, Winpepper.Models.ModelRegistry.DefaultAsrName);

        if (!string.Equals(settings.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase))
            return localTranscriber;

        var options = new Winpepper.Asr.Transcription.AssemblyAiOptions { Model = settings.AssemblyAiModel };
        var cloud = new Winpepper.Asr.Transcription.AssemblyAiTranscriber(
            AssemblyAiClient,
            AssemblyAiKeyStore,
            options,
            _aaiLoggerFactory.CreateLogger<Winpepper.Asr.Transcription.AssemblyAiTranscriber>());

        return new Winpepper.Asr.Transcription.FallbackTranscriber(
            cloud, localTranscriber,
            _aaiLoggerFactory.CreateLogger<Winpepper.Asr.Transcription.FallbackTranscriber>(),
            onFallback);
    }
```

> If `AppShell` does not already have a `using Microsoft.Extensions.Logging;` and `using System.Net.Http;`, add them at the top of the file.

- [ ] **Step 2: Confirm no cross-project break on Linux**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded. (App compilation is verified on Windows in Task 14.)

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Hosting/AppShell.cs
git commit -m "feat: wire AssemblyAI provider stack and transcriber factory in AppShell"
```

---

## Task 10: PipelineHost selection + provider recording + fallback notice (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (both dictation paths: Hold-up ~lines 197–332 and Toggle ~lines 337–489; the transcribe call sites at lines 206 and ~363; the archive calls that set `AsrModelName` at ~lines 310–328 and the toggle equivalent)

**Interfaces:**
- Consumes: `AppShell.BuildTranscriber(...)` (Task 9), `ITranscriber`/`TranscriptionResult` (Task 3), the existing `_asr` (`ParakeetSession`), `_settingsProvider`/current `AppSettings`, `_toasts` (`IToastService`), `_archiver`.
- Produces: history entries whose `AsrModelName` is the **actual** provider that produced the text; a non-intrusive toast on fallback.

> Windows-only. Verified via Smoke Checklist Task 14. The selection/fallback/provider-recording logic it depends on is unit-tested in `FallbackTranscriberTests` (Task 3).

- [ ] **Step 1: Replace the direct Parakeet call with the selected transcriber (Hold-up path)**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, in the Hold-up branch, replace the direct transcribe block (around line 206):

```csharp
var transcript = await Task.Run(() => _asr!.Transcribe(samples), ct);
```

with:

```csharp
var settingsNow = _settings.Load(); // existing SettingsStore reference on PipelineHost
var transcriber = _shell.BuildTranscriber(_asr!, settingsNow, notice =>
    _ = _toasts.ShowAsync(
        "Winpepper",
        "Cloud transcription unavailable — used local speech recognition instead.",
        Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
        TimeSpan.FromSeconds(6)));
var transcription = await transcriber.TranscribeAsync(samples, ct);
```

Then replace the two downstream references so the rest of the method uses the new result:

```csharp
string final = transcription.Text;
var producedModelName = transcription.ProviderModelName;
```

> `_shell` is the `AppShell` reference. If `PipelineHost` does not already hold one, add an `AppShell _shell` constructor parameter and field, and pass `this` from `AppShell.BootstrapAsync` where `PipelineHost` is constructed. If `_settings` (a `SettingsStore`) is not already a field, add it the same way — `AppShell` already has `SettingsStore`.

- [ ] **Step 2: Record the actual provider in history (Hold-up path)**

In the same branch, in the `_archiver.Archive(new HistoryArchiveInput { ... })` call (around lines 310–328), change:

```csharp
AsrModelName = _asrModelName,
```

to:

```csharp
AsrModelName = producedModelName,
```

- [ ] **Step 3: Apply the identical change to the Toggle path**

In the Toggle branch (around lines 337–489), make the same three edits: replace the direct `_asr!.Transcribe(samples)` call (around line 363) with the `BuildTranscriber` + `TranscribeAsync` block from Step 1 (reuse `settingsNow`/`transcriber`/`transcription` locals scoped to that branch), set `string final = transcription.Text;` and `var producedModelName = transcription.ProviderModelName;`, and set `AsrModelName = producedModelName` in that branch's `Archive` call.

- [ ] **Step 4: Confirm no cross-project break on Linux**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded. (App verified on Windows in Task 14.)

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat: select transcriber per dictation and record actual ASR provider"
```

---

## Task 11: Settings UI section — provider, key, model, Test, privacy note (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` (add a "Speech recognition" section)
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml.cs` (wire controls in `OnNavigatedTo`)

**Interfaces:**
- Consumes: `AppShell.AssemblyAiKeyStore` / `AssemblyAiClient` / `AssemblyAiOptions` (Task 9), `AppSettings.AsrProvider`/`AssemblyAiModel` (Task 1), the existing `ISettingsWriter.QueueAndFlushAsync` durable-write pattern, `App.Shell!`.
- Produces: user-facing controls to pick provider, enter/save/clear the key, set the model id, run a Test, and a one-line cloud-privacy disclosure.

> Windows-only WinUI. Verified via Smoke Checklist Task 14. The Test button calls `AssemblyAiClient.ValidateKeyAsync` (unit-tested in Task 5); key save/clear go through `AssemblyAiKeyStore` (unit-tested in Task 4).

- [ ] **Step 1: Add the XAML section**

In `src/Winpepper.App/Views/RecordingPage.xaml`, add inside the page's main vertical `StackPanel` (mirror the spacing/style of existing sections):

```xml
<StackPanel Spacing="8" Margin="0,16,0,0">
    <TextBlock Text="Speech recognition" Style="{StaticResource SubtitleTextBlockStyle}" />

    <ComboBox x:Name="AsrProviderCombo" Header="Provider" MinWidth="280">
        <ComboBoxItem Content="Local processing (on this PC)" Tag="local" />
        <ComboBoxItem Content="AssemblyAI premium (cloud)" Tag="assemblyai" />
    </ComboBox>

    <TextBlock
        Text="Cloud transcription sends your recorded audio to AssemblyAI for processing."
        TextWrapping="Wrap"
        Foreground="{ThemeResource TextFillColorSecondaryBrush}"
        Style="{StaticResource CaptionTextBlockStyle}" />

    <StackPanel x:Name="AssemblyAiPanel" Spacing="8" Margin="0,4,0,0">
        <PasswordBox x:Name="AssemblyAiKeyBox" Header="API key" MinWidth="280"
                     PlaceholderText="Paste your AssemblyAI API key" />
        <StackPanel Orientation="Horizontal" Spacing="8">
            <Button x:Name="SaveKeyButton" Content="Save key" />
            <Button x:Name="ClearKeyButton" Content="Clear key" />
            <Button x:Name="TestKeyButton" Content="Test" />
        </StackPanel>
        <TextBox x:Name="AssemblyAiModelBox" Header="Model id" MinWidth="280"
                 PlaceholderText="universal-2 (fast) or universal-3-5-pro (premium)" />
        <TextBlock x:Name="AsrStatusText"
                   TextWrapping="Wrap"
                   Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                   Style="{StaticResource CaptionTextBlockStyle}" />
    </StackPanel>
</StackPanel>
```

- [ ] **Step 2: Wire the controls in code-behind**

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, inside `OnNavigatedTo` (after the existing toggle wiring), add:

```csharp
        var shell = App.Shell!;
        var settingsStore = shell.SettingsStore;
        var keyStore = shell.AssemblyAiKeyStore;

        var current = settingsStore.Load();

        // Provider picker
        AsrProviderCombo.SelectedIndex = string.Equals(current.AsrProvider, "assemblyai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AssemblyAiPanel.Visibility = AsrProviderCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        AsrProviderCombo.SelectionChanged += (_, _) =>
        {
            var tag = (AsrProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "local";
            AssemblyAiPanel.Visibility = tag == "assemblyai" ? Visibility.Visible : Visibility.Collapsed;
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AsrProvider = tag });
        };

        // Model id
        AssemblyAiModelBox.Text = current.AssemblyAiModel;
        AssemblyAiModelBox.LostFocus += (_, _) =>
        {
            var model = string.IsNullOrWhiteSpace(AssemblyAiModelBox.Text) ? "universal-2" : AssemblyAiModelBox.Text.Trim();
            _ = shell.SettingsWriter.QueueAndFlushAsync(s => s with { AssemblyAiModel = model });
        };

        // Key status
        AsrStatusText.Text = keyStore.HasKey ? "A key is saved on this PC." : "No key saved.";

        SaveKeyButton.Click += (_, _) =>
        {
            var key = AssemblyAiKeyBox.Password;
            if (string.IsNullOrWhiteSpace(key)) { AsrStatusText.Text = "Enter a key first."; return; }
            keyStore.Save(key.Trim());
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key saved on this PC.";
        };

        ClearKeyButton.Click += (_, _) =>
        {
            keyStore.Clear();
            AssemblyAiKeyBox.Password = "";
            AsrStatusText.Text = "Key cleared.";
        };

        TestKeyButton.Click += async (_, _) =>
        {
            if (!keyStore.HasKey) { AsrStatusText.Text = "Save a key before testing."; return; }
            AsrStatusText.Text = "Testing key...";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var ok = await shell.AssemblyAiClient.ValidateKeyAsync(cts.Token);
                AsrStatusText.Text = ok ? "Key is valid." : "Key rejected (401). Check the key.";
            }
            catch (Exception ex)
            {
                AsrStatusText.Text = $"Test failed: {ex.Message}";
            }
        };
```

> The Test button uses the "bogus transcript id" approach documented in Task 5's `ValidateKeyAsync`: a cheap `GET /v2/transcript/<bogus-id>` returns 401 for a bad key and 404 for a good one — no audio is uploaded and no billable transcript is created. If `RecordingPage.xaml.cs` lacks `using System;` / `using System.Threading;` / `using Microsoft.UI.Xaml;`, add them.

- [ ] **Step 3: Confirm no cross-project break on Linux**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Asr/Winpepper.Asr.csproj -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: Build succeeded. (WinUI page compiles + renders only on Windows — verified in Task 14.)

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "feat: add AssemblyAI settings section with key entry, test, and privacy note"
```

---

## Task 12: Full non-Windows test suite gate

**Files:**
- None (verification task).

**Interfaces:**
- Consumes: all prior tasks.
- Produces: green evidence across every Linux-runnable test project.

- [ ] **Step 1: Build and run the whole non-Windows suite**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for proj in Winpepper.Core.Tests Winpepper.Asr.Tests Winpepper.Audio.Tests Winpepper.Cleanup.Tests \
            Winpepper.Corrections.Tests Winpepper.History.Tests Winpepper.Models.Tests \
            Winpepper.Platform.Tests Winpepper.IntegrationTests; do
  echo "=== $proj ==="
  dotnet build "tests/$proj/$proj.csproj" -f net9.0 -p:EnableWindowsTargeting=true || exit 1
  dotnet exec "tests/$proj/bin/Debug/net9.0/$proj.dll" -notrait "Platform=Windows" || exit 1
done
echo "ALL SUITES PASSED"
```
Expected: every project reports 0 failures; final line `ALL SUITES PASSED`. New Asr tests (WAV, client, transcriber, fallback, key store) and the Core settings-defaults tests are green; no pre-existing suite regressed.

- [ ] **Step 2: Commit (only if any incidental fixes were needed)**

```bash
git add -A
git commit -m "test: green non-Windows suite for AssemblyAI ASR provider" || echo "nothing to commit"
```

---

## Task 13: Windows Smoke Test Checklist (documentation)

**Files:**
- None in code — this section IS the deliverable. The manual checklist below is executed by a developer on Windows with a real AssemblyAI key. It is the spec-mandated verification for the Windows-only surfaces (DPAPI, WinUI settings page, `PipelineHost`/`AppShell` wiring) that cannot run in the Linux test harness.

**Windows smoke checklist (run on a Windows 11 machine, real key):**

1. **Settings visibility:** Launch Winpepper → Settings → Recording. Confirm the "Speech recognition" section shows the provider picker, the cloud-privacy disclosure line, and (when AssemblyAI is selected) the key box, Save/Clear/Test buttons, and model field.
2. **Key persistence (DPAPI):** Enter a real key, click **Save key**. Confirm status reads "Key saved on this PC." Confirm `%LOCALAPPDATA%\winpepper\assemblyai.key.dat` exists and its bytes are **not** the plaintext key. Confirm `settings.json` contains no key. Restart the app; status still reads a key is saved.
3. **Test button — good key:** Click **Test** with the valid key → status "Key is valid."
4. **Test button — bad key:** Save a wrong key, click **Test** → status "Key rejected (401). Check the key."
5. **Cloud dictation:** Select "AssemblyAI premium", set model `universal-2`. Dictate a 2–30s clip via the hotkey. Confirm the text is injected. Open History → the entry's `asrModelName` reads `assemblyai/universal-2`.
6. **Premium model:** Set model `universal-3-5-pro`, dictate again → text injected; history `asrModelName` reads `assemblyai/universal-3-5-pro`. If the API rejects the model id, confirm the fallback toast appears and local text is still delivered (see step 8) and the API's error is logged.
7. **Clear key:** Click **Clear key** → status "Key cleared." Confirm the `.key.dat` file is gone.
8. **Robust fallback (network cable / airplane mode):** With AssemblyAI selected and a valid key saved, disable networking, then dictate. Confirm: (a) the dictation still lands via **local Parakeet**, (b) the non-intrusive toast "Cloud transcription unavailable — used local speech recognition instead." appears, (c) History `asrModelName` reads `parakeet-tdt-0.6b-v3`.
9. **Fallback with no key:** Select AssemblyAI, clear the key, dictate → local result delivered + fallback toast; History shows the local model.
10. **Local unaffected:** Switch provider back to "Local processing", dictate → normal local behavior, History `asrModelName` reads `parakeet-tdt-0.6b-v3`.
11. **Full Windows build/test:** On the Windows machine run the app's normal build (`dotnet build winpepper.sln`) and the Windows test pass to confirm the WinUI project compiles and Windows-trait tests pass.

- [ ] **Step 1: Add this checklist to the plan doc (already included) and commit any doc touch-ups**

```bash
git add docs/plans/2026-07-21-assemblyai-asr-provider.md
git commit -m "docs: add Windows smoke checklist for AssemblyAI ASR provider" || echo "nothing to commit"
```

---

## Future work (spec-sanctioned, out of scope)

- **Streaming ASR** (real-time): explicitly out of scope per the research brief (the old SDK's streaming targets a dead endpoint). Batch flow only for v1.
- **`custom_spelling` / keyterms:** map `corrections.json` `Replacements`/`Preferred` (loaded at `PipelineHost.cs:233`, consumed by `CleanupRunner`/`PromptBuilder`) into AssemblyAI's `custom_spelling` at transcript-create time. The spec marks this an optional stretch that may be skipped; deferred to keep the plan focused.

---

## Self-Review

**1. Spec coverage (requirement → task):**
- Req 1 (provider abstraction `ITranscriber`, selection via new `AppSettings` fields, PipelineHost picks per dictation): Tasks 1 (settings), 3 (seam), 7 (Parakeet adapter), 9 (factory/selection), 10 (PipelineHost picks). ✅
- Req 2 (AssemblyAiTranscriber: float→WAV, upload/create/poll, single HttpClient, retry/backoff/Retry-After, fake-handler tests for happy/401/429/5xx/error-lifecycle/timeout/raw-bytes; log id+timings, never log key): Tasks 2 (WAV), 5 (client + all HTTP tests + raw-bytes assertion), 6 (orchestration + timeout/error/no-key tests + logs id/timings). ✅
- Req 3 (DPAPI-encrypted key at rest under `%LOCALAPPDATA%\winpepper`, interface seam + Linux fake, settings.json records no key/derive from file): Tasks 4 (store + protector seam + fake, not-plaintext test), 8 (DPAPI impl + key-file path), 11 (UI save/clear through seam). ✅
- Req 4 (robust fallback on ANY failure, non-intrusive notice, record actual provider in `asrModelName`, local model stays loaded): Tasks 3 (FallbackTranscriber + provider recording tests + cancellation guard), 10 (toast notice + `producedModelName` into history), Global Constraints (local session lifecycle unchanged). ✅
- Req 5 (settings UI: provider picker, PasswordBox key save/clear, model field with two known values, Test button via 401-vs-404, clear status, existing patterns): Task 11 (all controls, `QueueAndFlushAsync` durable writes) + Task 5 (`ValidateKeyAsync`). ✅
- Req 6 (one-line cloud privacy disclosure next to provider picker): Task 11 XAML disclosure line. ✅
- Verification (Linux xUnit v3 in-process runner, test transcriber/WAV/fallback/key-store/settings defaults; Windows-only surfaces in a smoke checklist; full non-Windows suite; no network in tests; don't touch hook/packaging): Tasks 1–7 (Linux tests), 12 (full suite), 13 (smoke checklist); Global Constraints forbid hook/packaging edits and network in tests. ✅

**1b. No silent deferrals of required behavior:** Every required user-facing behavior has a production task and an observable proof:
- Cloud transcription produces real text → Task 6 real orchestration; proved end-to-end by Smoke step 5 (real key, real dictation, history shows `assemblyai/<model>`).
- Fallback delivers local text on failure → Task 10 real wiring; unit-proved by `FallbackTranscriberTests` (Task 3) and end-to-end by Smoke step 8 (cut network → local text + toast + history `parakeet-tdt-0.6b-v3`).
- Encrypted key at rest → Task 8 real DPAPI; unit-proved (not-plaintext) with the fake in Task 4, and by Smoke step 2 (real `.key.dat` bytes ≠ plaintext).
- The test doubles (`FakeApiKeyProtector`, `FakeHttpMessageHandler`, `FakeAssemblyAiClient`, `FakeTranscriber`) live only in the test project; each has a named production counterpart replacing it (`DpapiApiKeyProtector` Task 8; real `HttpClient` Task 9; `AssemblyAiClient` Task 5; `AssemblyAiTranscriber`/`ParakeetTranscriber` Tasks 6–7) whose real outcome is proven by the Windows smoke checklist. No required behavior is parked in "future work" — only streaming and `custom_spelling`, both of which the spec explicitly authorizes deferring. No UNRESOLVED COVERAGE GAP remains.

**2. Placeholder scan:** No "TBD"/"handle edge cases"/"similar to Task N"/"add validation" placeholders; every code step shows complete code and every test step shows exact commands + expected output.

**3. Type consistency:** Names are consistent across tasks: `TranscriptionResult(Text, ProviderModelName)`, `ITranscriber.TranscribeAsync(ReadOnlyMemory<float>, CancellationToken)`, `ITranscriber.ModelName`, `IAssemblyAiClient` methods (`UploadAsync`/`CreateTranscriptAsync`/`GetTranscriptAsync`/`ValidateKeyAsync`), `AssemblyAiTranscript(Status, Text, Confidence, AudioDuration, Error)`, `IApiKeyProtector.Protect/Unprotect`, `IAssemblyAiKeyStore.HasKey/Save/Load/Clear`, `AssemblyAiOptions.{Model,TotalTimeout,PollInterval,MaxTransientRetries,LanguageCode,BaseUrl}`, `AssemblyAiException.{StatusCode,IsAuthError}`, `AppShell.BuildTranscriber(ParakeetSession, AppSettings, Action<string>)`. `ModelName` for cloud is `assemblyai/<model>` everywhere (Task 6 impl, Task 3/6 tests, Smoke steps 5–6). Local model string is `parakeet-tdt-0.6b-v3` / `ModelRegistry.DefaultAsrName` consistently (Tasks 9, 10, Smoke). No signature drift found.

**4. Load-bearing validation hardening (2026-07-21):** The plan was stress-tested against 17 load-bearing assumptions (finder → strategist → 5 parallel validators; ledger at `.the-usual-logs/assemblyai-asr-provider/load-bearing-ledger.md`). Five falsifications were fixed in place; the rest verified. No data-loss/irreversible risk found → no halt.
- **API contract (A9, falsified):** the create payload now sends **`speech_models: [<model>]`** (plural array), not the deprecated singular `speech_model` (Task 5 code + test updated; Global Constraints updated). The premium model string is **`universal-3-5-pro`** (there is no `universal-3-pro`) — fixed in Task 11 placeholder and Task 13 smoke step 6. Default `universal-2` confirmed valid. Verified against official AssemblyAI docs + a live read-only 401 probe.
- **DPAPI/CPM (A12, gap fixed):** Task 8 now adds `System.Security.Cryptography.ProtectedData` to `Directory.Packages.props` + `Winpepper.App.csproj` (required — repo uses Central Package Management; the package was absent). Security model documented (non-portable CurrentUser blob; app ships unpackaged so no MSIX caveat). `AssemblyAiKeyStore.Load()` (Task 4) now catches `CryptographicException` and returns `null` so an undecryptable blob degrades to local fallback + re-prompt (new test added).
- **Linux build model (A13, falsified):** corrected Global Constraints — the App is excluded from Linux builds by its Windows-only TFM (not `#if WINDOWS`, and not the now-inert `SKIP_WINUI_LINUX` guard); builds MUST be project-scoped (every task already is).
- **Existing test project (A15, falsified):** `tests/Winpepper.Asr.Tests` already exists in the solution — File Structure now says "add files to it," not "create it."
- **Verified positives worth noting:** the 45s cap is comfortably generous for 2–30s clips (A11: AssemblyAI RTF ≈ 0.008x); the fallback-on-timeout guarantee is sound (A16: Task 6's linked-CTS + `when (!ct.IsCancellationRequested)` correctly routes wall-clock timeouts to local fallback rather than misclassifying them as user cancellation); the local `ParakeetSession` is safe as the fallback target because dictations are serialized by PipelineHost's single-consumer pump (A14); `ParakeetTranscriber` already correctly unwraps `ParakeetTranscript.Text` and converts `ReadOnlyMemory<float>` → `.Span` inside `Task.Run` (A5/A6 — repo signatures confirmed, plan already correct).
- **Residual (acceptable):** the valid-key→404 branch of the key-validation trick (A10) and the exact `language_code:"en_us"` casing are not verifiable without a live key (tests forbid network); both are exercised end-to-end by the Windows Smoke Checklist (steps 3 & 5) and fail safe (create error → local fallback delivers text).
