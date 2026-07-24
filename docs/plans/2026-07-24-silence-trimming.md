# Silence Trimming + Silent-Recording Drop Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Trim long silent stretches out of a finished dictation before ASR,
and drop recordings where nobody spoke — without changing what gets archived.

**Architecture:** A new pure, unit-tested `SilenceTrimmer` in the pure-managed
`Winpepper.Audio` project computes 20 ms RMS frames over the finished 16 kHz
mono buffer, derives an adaptive threshold from frame-RMS percentiles, and
compresses over-long below-threshold runs to a fixed cap. `PipelineHost` (the
Windows-only WinUI host) calls it after the existing dead-mic check: silent
recordings are dropped (no transcribe/inject/archive, session returns to idle
like an empty-final-text dictation); non-silent recordings are transcribed on
the *trimmed* buffer while the *original* buffer is still archived.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly (pure-managed tests run on
Linux via the in-process runner), WinUI 3 (Windows-only host).

## Global Constraints

- **Platform / TFM:** `Winpepper.Audio` and its test project multi-target
  `net9.0;net9.0-windows10.0.19041.0`. Pure logic + tests run on the `net9.0`
  TFM on Linux. `Winpepper.App/Hosting/PipelineHost.cs` is wrapped in
  `#if WINDOWS` and compiles/runs only on Windows.
- **Test runner (AGENTS.md mandate):** Do NOT use `dotnet test` (VSTest is
  unreliable here). Build `-c Release`, then run the built test dll with the
  xUnit v3 in-process runner: `dotnet exec <built test dll>`.
- **dotnet path:** The worktree has no local `.dotnet/`. Use the repo-root
  SDK at `/home/dan/code/winpepper/.dotnet/dotnet` for every build/exec.
- **All tests green before every commit** (AGENTS.md). A commit whose task
  touched pure-managed code must show its own test dll green AND must not
  regress the full non-Windows suite (baseline: 9 projects, 833 passing / 0
  failing at tip `5ed88ed`).
- **Do NOT modify** `AudioEnergy.SilenceRmsThreshold` or `AudioEnergy.Rms`'s
  contract (reuse `Rms`; the dead-mic detector is a different concern and its
  doc comment forbids VAD-ification). Do NOT touch the keyboard hook,
  `packaging/`, the AssemblyAI client, cleanup, or injection stages.
- **No new settings/UI.** All tunables are documented `const`s in
  `SilenceTrimmer` with the experiment rationale in comments (owner decision).
- **Experiment-fixed parameters — copy verbatim, do NOT re-derive:**
  - Frame: 20 ms RMS over the finished 16 kHz mono float buffer → 320
    samples/frame.
  - `noiseFloor` = 10th percentile of frame RMS; `speechLevel` = 90th
    percentile.
  - `threshold = min( max(3 * noiseFloor, 0.002), 0.15 * speechLevel )`.
  - If `speechLevel < 0.004` → the recording is SILENT (no speech).
  - Cap = 1200 ms; keep 600 ms adjacent to each speech edge; delete the
    middle. Only runs longer than their keep-budget are trimmed.
  - Rationale for 1200: at cap=1200, the experiment removed 59.0 s audio /
    saved 11.4 s ASR across 45 real dictations with only cosmetic transcript
    changes; cap=500/800 caused real word damage. 1200 is the safe point.

---

## File Structure

- **Create `src/Winpepper.Audio/SilenceTrimmer.cs`** — the pure trimmer and its
  `TrimResult` value type. One responsibility: turn a finished sample buffer
  into `(trimmed samples, removedMs, runsTrimmed, isSilent)`. Sits alongside
  `AudioEnergy.cs`; reuses `AudioEnergy.Rms`.
- **Create `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`** — synthetic
  fixtures proving every trimmer behavior (silence detection, interior/edge
  trimming, untouched boundary, noisy no-op, tail preservation, accounting).
- **Modify `src/Winpepper.App/Hosting/PipelineHost.cs`** — add one private
  helper `TrimForTranscription(...)` and wire it into both the hold path
  (`HoldUp`, `samples`) and the toggle path (`Toggle`/Recording, `samples2`),
  after `WarnIfSessionSilent` and before building the transcriber. Windows-only;
  verified by the Windows build + smoke checklist (Task 4).

### Decomposition rationale (monotone single-cap trimming)

The experiment fixed a single cap so that **output pause = min(input pause,
cap)** — monotone, no discontinuity. This plan models each below-threshold run
by how many **speech edges** it touches and keeps 600 ms adjacent to each:

| Run kind | Speech edges | Keep budget | Trimmed when run length > | Kept after trim |
|----------|-------------|-------------|---------------------------|-----------------|
| Interior (speech–silence–speech) | 2 | 1200 ms | 1200 ms | 600 ms + 600 ms |
| Leading / trailing | 1 | 600 ms | 600 ms | 600 ms adjacent to speech |

This is the reconciliation of the two spec statements — "runs longer than
cap=1200 ms compressed to 1200 ms, 600 ms per speech edge" (the interior case)
and "leading/trailing long silence trimmed with 600 ms kept adjacent to speech"
(the one-edge case) — and it keeps the "same number trimmed-vs-left, no
discontinuity" property per run kind. A run touching **zero** speech edges can
only occur when the whole recording is silence, which the `speechLevel < 0.004`
gate already classifies as `IsSilent` (handled before trimming); a defensive
guard keeps such a run whole rather than deleting everything.

---

## Task 1: SilenceTrimmer skeleton — frames, percentiles, silent-detection

**Files:**
- Create: `src/Winpepper.Audio/SilenceTrimmer.cs`
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Audio.AudioEnergy.Rms(ReadOnlySpan<float>)` (existing).
- Produces (relied on by Task 4 and later tests):
  - `readonly struct TrimResult` with `float[] Trimmed`, `int RemovedMs`,
    `int RunsTrimmed`, `bool IsSilent` (all `required` init properties).
  - `static TrimResult SilenceTrimmer.Trim(ReadOnlySpan<float> samples)`.
  - When `IsSilent` is true, `Trimmed` is empty and `RemovedMs`/`RunsTrimmed`
    are 0. When there is no speech signal at all but the buffer has fewer than
    one full frame, `IsSilent` is false and `Trimmed` is a copy of the input
    (empty captures are the caller's cancel/length concern, not the trimmer's).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class SilenceTrimmerTests
{
    // 16 kHz mono: 320 samples = 20 ms; 9600 = 600 ms; 19200 = 1200 ms.
    private const int Rate = 16000;
    private const int FrameSamples = 320;

    private static float[] Const(int samples, float amp)
    {
        var a = new float[samples];
        for (var i = 0; i < samples; i++) a[i] = amp;
        return a;
    }

    private static float[] Concat(params float[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var outBuf = new float[total];
        var w = 0;
        foreach (var p in parts) { p.CopyTo(outBuf, w); w += p.Length; }
        return outBuf;
    }

    [Fact]
    public void Trim_LiveMicNobodySpoke_IsSilent()
    {
        // Room tone at 0.002 (below the 0.004 speech gate) over 50 frames.
        var buf = Const(50 * FrameSamples, 0.002f);
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeTrue();
        r.Trimmed.Length.ShouldBe(0);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
    }

    [Fact]
    public void Trim_AllSpeechNoSilence_PassesThroughUnchanged()
    {
        var buf = Const(100 * FrameSamples, 0.3f); // 100 frames of speech
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_EmptyInput_IsNotSilentAndEmpty()
    {
        var r = SilenceTrimmer.Trim(ReadOnlySpan<float>.Empty);
        r.IsSilent.ShouldBeFalse();
        r.Trimmed.Length.ShouldBe(0);
        r.RemovedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_SubFrameBuffer_PassesThroughUnchanged()
    {
        var buf = Const(100, 0.3f); // < 1 frame
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.Trimmed.Length.ShouldBe(100);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release
```
Expected: FAIL to build — `SilenceTrimmer` / `TrimResult` do not exist
(`CS0103`/`CS0246`). That is the RED state for this task.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Winpepper.Audio/SilenceTrimmer.cs`:

```csharp
namespace Winpepper.Audio;

/// <summary>Outcome of <see cref="SilenceTrimmer.Trim"/>.</summary>
public readonly struct TrimResult
{
    /// <summary>Samples to send to ASR. Empty when <see cref="IsSilent"/> is true.</summary>
    public required float[] Trimmed { get; init; }

    /// <summary>Total milliseconds of silence removed (0 when nothing was trimmed).</summary>
    public required int RemovedMs { get; init; }

    /// <summary>Number of below-threshold runs that were compressed.</summary>
    public required int RunsTrimmed { get; init; }

    /// <summary>
    /// True when the recording contains no speech (live mic, nobody spoke).
    /// The caller DROPs such a dictation. Distinct from AudioEnergy's dead-mic
    /// detector: this is a voice-presence check over frame-RMS percentiles.
    /// </summary>
    public required bool IsSilent { get; init; }
}

/// <summary>
/// Pure silence trimmer for a FINISHED 16 kHz mono float session buffer.
///
/// Parameters are FIXED by an on-device experiment (45 real archived dictations
/// transcribed with the real parakeet model, original vs trimmed at caps of
/// 300/500/800/1200/2000 ms). At cap=1200 the experiment removed 59.0 s of
/// audio / saved 11.4 s of ASR time across 45 files with only 5 transcripts
/// changed (9 word-edits, ALL cosmetic — capitalization / comma-vs-period).
/// cap=500 caused real word damage ("ligh", "Great"->"Right"); cap=800 injected
/// a disfluency. 1200 ms is the chosen safe point. Do NOT re-derive these.
///
/// Reuses <see cref="AudioEnergy.Rms"/>. Does NOT touch
/// <see cref="AudioEnergy.SilenceRmsThreshold"/> (a different, dead-mic concern).
/// </summary>
public static class SilenceTrimmer
{
    private const int SampleRate = 16000;
    private const int FrameMs = 20;
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 320

    /// <summary>Milliseconds of silence kept adjacent to each speech edge.</summary>
    private const int KeepMsPerEdge = 600;
    private const int KeepFramesPerEdge = KeepMsPerEdge / FrameMs; // 30
    // Interior keep budget is 2 * KeepMsPerEdge = 1200 ms (the experiment cap).

    private const double NoiseFloorPercentile = 0.10;
    private const double SpeechLevelPercentile = 0.90;
    private const double ThresholdNoiseMultiplier = 3.0;
    private const double ThresholdAbsFloor = 0.002;
    private const double SpeechCapFactor = 0.15;

    /// <summary>Below this 90th-percentile RMS the recording has no speech.</summary>
    private const double SilentSpeechLevel = 0.004;

    public static TrimResult Trim(ReadOnlySpan<float> samples)
    {
        var n = samples.Length;
        var frameCount = n / FrameSamples;
        if (frameCount == 0)
        {
            // Fewer than one full frame: nothing to analyze. Not "silent" —
            // empty/cancel captures are guarded by the caller's length check.
            return new TrimResult
            {
                Trimmed = samples.ToArray(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = false,
            };
        }

        var rms = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
            rms[f] = AudioEnergy.Rms(samples.Slice(f * FrameSamples, FrameSamples));

        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var speechLevel = Percentile(sorted, SpeechLevelPercentile);

        if (speechLevel < SilentSpeechLevel)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
            };
        }

        // Increment A (Task 1): speech present — pass the whole buffer through.
        // Trimming is added in Task 2.
        return new TrimResult
        {
            Trimmed = samples.ToArray(),
            RemovedMs = 0,
            RunsTrimmed = 0,
            IsSilent = false,
        };
    }

    private static double Percentile(double[] sortedAsc, double p)
    {
        if (sortedAsc.Length == 0) return 0.0;
        var idx = (int)Math.Floor(p * (sortedAsc.Length - 1));
        if (idx < 0) idx = 0;
        if (idx >= sortedAsc.Length) idx = sortedAsc.Length - 1;
        return sortedAsc[idx];
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```
Expected: PASS — all `SilenceTrimmerTests` (4 new) and the existing
`AudioEnergyTests` green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): SilenceTrimmer skeleton with silent-recording detection"
```

---

## Task 2: Interior + leading/trailing trimming with tail preservation

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (rewrite the `Trim` body)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (add tests)

**Interfaces:**
- Consumes: `TrimResult`, `SilenceTrimmer.Trim` from Task 1 (unchanged signature).
- Produces: the trimming behavior later tasks/Task 4 rely on — over-long
  below-threshold runs compressed (interior → 600+600 ms; edge → 600 ms
  adjacent to speech), `RemovedMs`/`RunsTrimmed` populated, non-frame-aligned
  tail samples preserved verbatim at the end of `Trimmed`.

- [ ] **Step 1: Write the failing tests**

Append to `SilenceTrimmerTests.cs` (inside the class):

```csharp
    [Fact]
    public void Trim_Interior3sGap_BecomesExactly1200msSplit600_600()
    {
        // 500 ms speech | 3000 ms silence | 500 ms speech
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),   // 8000 samples speech
            Const(150 * FrameSamples, 0.0f),  // 48000 samples silence
            Const(25 * FrameSamples, 0.3f));  // 8000 samples speech

        var r = SilenceTrimmer.Trim(buf);

        r.IsSilent.ShouldBeFalse();
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1800); // 3000 - 1200 removed
        // 8000 speech + 19200 silence (1200 ms) + 8000 speech
        r.Trimmed.Length.ShouldBe(8000 + 19200 + 8000);
        r.Trimmed[7999].ShouldBe(0.3f);  // end of first speech block
        r.Trimmed[8000].ShouldBe(0.0f);  // 600 ms kept after speech
        r.Trimmed[27199].ShouldBe(0.0f); // 600 ms kept before speech
        r.Trimmed[27200].ShouldBe(0.3f); // second speech block resumes
    }

    [Fact]
    public void Trim_InteriorGapExactly1200ms_Untouched()
    {
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(60 * FrameSamples, 0.0f),   // exactly 1200 ms
            Const(25 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_InteriorGap1100ms_Untouched()
    {
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(55 * FrameSamples, 0.0f),   // 1100 ms
            Const(25 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }

    [Fact]
    public void Trim_LeadingLongSilence_Keeps600msAdjacentToSpeech()
    {
        // 2000 ms leading silence | 1000 ms speech
        var buf = Concat(
            Const(100 * FrameSamples, 0.0f),  // 32000 silence
            Const(50 * FrameSamples, 0.3f));  // 16000 speech
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1400);           // 2000 - 600 removed
        r.Trimmed.Length.ShouldBe(9600 + 16000);
        r.Trimmed[9599].ShouldBe(0.0f);       // last kept silence sample
        r.Trimmed[9600].ShouldBe(0.3f);       // speech starts right after 600 ms
    }

    [Fact]
    public void Trim_TrailingLongSilence_Keeps600msAdjacentToSpeech()
    {
        // 1000 ms speech | 2000 ms trailing silence
        var buf = Concat(
            Const(50 * FrameSamples, 0.3f),   // 16000 speech
            Const(100 * FrameSamples, 0.0f)); // 32000 silence
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(1);
        r.RemovedMs.ShouldBe(1400);
        r.Trimmed.Length.ShouldBe(16000 + 9600);
        r.Trimmed[15999].ShouldBe(0.3f);      // end of speech
        r.Trimmed[16000].ShouldBe(0.0f);      // 600 ms of trailing silence kept
    }

    [Fact]
    public void Trim_TailRemainderBeyondLastFrame_IsPreserved()
    {
        // Interior trim + a 7-sample non-frame-aligned tail marked 0.777.
        var buf = Concat(
            Const(25 * FrameSamples, 0.3f),
            Const(150 * FrameSamples, 0.0f),
            Const(25 * FrameSamples, 0.3f),
            Const(7, 0.777f));                // tail: 7 samples, no full frame
        var r = SilenceTrimmer.Trim(buf);
        r.RemovedMs.ShouldBe(1800);
        r.Trimmed.Length.ShouldBe(8000 + 19200 + 8000 + 7);
        for (var i = 0; i < 7; i++)
            r.Trimmed[^(i + 1)].ShouldBe(0.777f); // tail survives at the end
    }

    [Fact]
    public void Trim_TwoInteriorGaps_AccountsRemovedMsAndRuns()
    {
        // speech | 2000 ms gap | speech | 2000 ms gap | speech
        var buf = Concat(
            Const(20 * FrameSamples, 0.3f),
            Const(100 * FrameSamples, 0.0f),
            Const(20 * FrameSamples, 0.3f),
            Const(100 * FrameSamples, 0.0f),
            Const(20 * FrameSamples, 0.3f));
        var r = SilenceTrimmer.Trim(buf);
        r.RunsTrimmed.ShouldBe(2);
        r.RemovedMs.ShouldBe(1600); // (2000-1200) removed per interior gap, x2
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```
Expected: FAIL — the new trimming tests fail because Increment A passes the
buffer through unchanged (`RemovedMs` is 0, lengths unchanged). Task 1's tests
still pass.

- [ ] **Step 3: Write the implementation**

Replace the `Trim` method body in `src/Winpepper.Audio/SilenceTrimmer.cs` (from
the `var speechLevel = ...` line onward) so that, after the `IsSilent` gate, it
performs run-based trimming. Replace the Increment-A `return` block with the
following, and add the `AppendKeep` helper. The threshold here is **uncapped**
(the fail-safe cap is added in Task 3):

```csharp
        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive threshold. (Task 3 adds the 0.15*speechLevel fail-safe cap.)
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);

        var isSilence = new bool[frameCount];
        for (var f = 0; f < frameCount; f++)
            isSilence[f] = rms[f] < threshold;

        // Walk contiguous silence runs; build the ordered list of whole-frame
        // segments to KEEP. Interior runs keep 600 ms per speech edge; edge runs
        // keep 600 ms adjacent to their single speech edge; the middle is deleted.
        var kept = new List<(int start, int len)>();
        var removedFrames = 0;
        var runsTrimmed = 0;

        var i = 0;
        while (i < frameCount)
        {
            if (!isSilence[i])
            {
                AppendKeep(kept, i, 1);
                i++;
                continue;
            }

            var runStart = i;
            while (i < frameCount && isSilence[i]) i++;
            var runEnd = i; // exclusive
            var runLen = runEnd - runStart;

            var hasLeftSpeech = runStart > 0;
            var hasRightSpeech = runEnd < frameCount;
            var edges = (hasLeftSpeech ? 1 : 0) + (hasRightSpeech ? 1 : 0);
            var keepBudget = edges * KeepFramesPerEdge;

            if (edges > 0 && runLen > keepBudget)
            {
                if (hasLeftSpeech) AppendKeep(kept, runStart, KeepFramesPerEdge);
                if (hasRightSpeech) AppendKeep(kept, runEnd - KeepFramesPerEdge, KeepFramesPerEdge);
                removedFrames += runLen - keepBudget;
                runsTrimmed++;
            }
            else
            {
                // Short enough to keep whole, or an all-silence buffer with no
                // speech edge (defensive; the IsSilent gate normally catches it).
                AppendKeep(kept, runStart, runLen);
            }
        }

        var keptFrames = 0;
        foreach (var seg in kept) keptFrames += seg.len;
        var tail = n - frameCount * FrameSamples;
        var outBuf = new float[keptFrames * FrameSamples + tail];

        var w = 0;
        foreach (var (start, len) in kept)
        {
            samples.Slice(start * FrameSamples, len * FrameSamples).CopyTo(outBuf.AsSpan(w));
            w += len * FrameSamples;
        }
        if (tail > 0)
            samples.Slice(frameCount * FrameSamples, tail).CopyTo(outBuf.AsSpan(w));

        return new TrimResult
        {
            Trimmed = outBuf,
            RemovedMs = removedFrames * FrameMs,
            RunsTrimmed = runsTrimmed,
            IsSilent = false,
        };
    }

    private static void AppendKeep(List<(int start, int len)> segs, int start, int len)
    {
        if (len <= 0) return;
        if (segs.Count > 0)
        {
            var last = segs[^1];
            if (last.start + last.len == start)
            {
                segs[^1] = (last.start, last.len + len);
                return;
            }
        }
        segs.Add((start, len));
    }
```

(The Increment-A `return new TrimResult { ... IsSilent = false }` block that
followed the `IsSilent` gate is removed — the code above replaces it. Keep the
`Percentile` helper and the constants unchanged.)

- [ ] **Step 4: Run tests to verify they pass**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```
Expected: PASS — all Task 1 + Task 2 `SilenceTrimmerTests` and existing
`AudioEnergyTests` green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): trim over-long silence runs with tail preservation"
```

---

## Task 3: Fail-safe threshold cap (noisy-floor no-op)

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (one added line in threshold)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (add one test)

**Interfaces:**
- Consumes/Produces: same `Trim` signature. Adds the experiment-specified cap
  `threshold = min(threshold, 0.15 * speechLevel)` so that when silence cannot
  be confidently separated from speech (high noise floor relative to speech),
  the trimmer becomes a no-op rather than eating real audio.

- [ ] **Step 1: Write the failing test**

Append to `SilenceTrimmerTests.cs`:

```csharp
    [Fact]
    public void Trim_NoisyFloorRelativeToSpeech_IsNoOp()
    {
        // Quiet speech at 0.02, "silence" at 0.01 (high floor vs speech).
        // noiseFloor≈0.01, speechLevel≈0.02 -> 3*floor=0.03 but capped at
        // 0.15*0.02=0.003; silence frames (0.01) stay ABOVE 0.003 -> not
        // classified as silence -> nothing trimmed.
        var buf = Concat(
            Const(25 * FrameSamples, 0.02f),
            Const(150 * FrameSamples, 0.01f),
            Const(25 * FrameSamples, 0.02f));
        var r = SilenceTrimmer.Trim(buf);
        r.IsSilent.ShouldBeFalse();
        r.RemovedMs.ShouldBe(0);
        r.RunsTrimmed.ShouldBe(0);
        r.Trimmed.Length.ShouldBe(buf.Length);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll \
     -method "*Trim_NoisyFloorRelativeToSpeech_IsNoOp*"
```
Expected: FAIL — without the cap, `threshold = max(3*0.01, 0.002) = 0.03`, so
the 0.01 silence frames fall below it, the interior run is trimmed, and
`RemovedMs` is 1800 instead of 0.

- [ ] **Step 3: Write the implementation**

In `src/Winpepper.Audio/SilenceTrimmer.cs`, add the cap immediately after the
threshold line:

```csharp
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech, silence
        // cannot be confidently separated. Capping the threshold at a fraction
        // of speechLevel keeps genuine silence-vs-speech separable and makes
        // low-SNR recordings a no-op instead of eating real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);
```

- [ ] **Step 4: Run the full test dll to verify it passes**

Run:
```bash
/home/dan/code/winpepper/.dotnet/dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release \
  && /home/dan/code/winpepper/.dotnet/dotnet exec \
     tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll
```
Expected: PASS — the noisy-floor test passes AND every earlier trimming test
still passes (the cap does not change the healthy-speech fixtures, where
`0.15*speechLevel` stays above the uncapped threshold).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): fail-safe threshold cap so noisy recordings are a no-op"
```

---

## Task 4: Wire trimming + silent-drop into PipelineHost (Windows-only)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`
  - Add helper `TrimForTranscription(...)` near `WarnIfSessionSilent`
    (around line 800).
  - Hold path (`HotkeyEventKind.HoldUp`): insert the drop/trim block after
    `_sounds.PlayStop();` (currently line 401) and before
    `var transcribeSw = ...` (currently line 403); change the transcribe call
    (currently line 432) to use the trimmed buffer.
  - Toggle path (`HotkeyEventKind.Toggle`, Recording branch): insert the same
    block after `_sounds.PlayStop();` (currently line 615) and before
    `var transcribeSw2 = ...` (currently line 617); change the transcribe call
    (currently line 640) to use the trimmed buffer.

**Interfaces:**
- Consumes: `Winpepper.Audio.SilenceTrimmer.Trim` and `TrimResult` (Tasks 1-3);
  `SessionEvent.TranscriptReady`, `SessionEvent.InjectionCompleted`,
  `SessionEngine.Apply` (existing — the empty-final-text completion seam:
  `Transcribing --TranscriptReady--> Injecting --InjectionCompleted--> Idle`).
- Produces: no public surface change. Behavioral contract:
  - Silent recording → DROP: content-free info log `"dropped silent recording,
    N ms"`; no transcribe/cleanup/injection/archive; session driven to Idle via
    `TranscriptReady` + `InjectionCompleted`; no toast.
  - Non-silent → transcribe the TRIMMED buffer; log `"trimmed silence: N ms
    across R runs"` only when `RemovedMs > 0`.
  - Archive still receives the ORIGINAL `samples`/`samples2`; `RecordMs` and the
    archived `DurationMs` stay based on the original recording.

> **Why no Linux unit test here:** `PipelineHost.cs` is wrapped in
> `#if WINDOWS` and depends on the WinUI host — it cannot compile or run on
> Linux. The drop-vs-transcribe DECISION is exactly `TrimResult.IsSilent`,
> which is fully proven by the pure `SilenceTrimmerTests` (silent → drop;
> non-silent → transcribe trimmed). This task's own end-to-end proof is the
> Windows smoke checklist in Step 5. This is a platform-inherent verification
> boundary, not a stubbed or deferred behavior: the wiring is real production
> code shipped in the Windows build.

- [ ] **Step 1: Read the two call sites and confirm current line anchors**

Run (read-only — orient before editing; anchors are text, not line numbers):
```bash
grep -n "WarnIfSessionSilent(samples\|WarnIfSessionSilent(samples2\|TranscribeAsync(samples\|Samples16k = samples\|_sounds.PlayStop" \
  src/Winpepper.App/Hosting/PipelineHost.cs
```
Expected: two `WarnIfSessionSilent(...)` sites, two `TranscribeAsync(...)`
sites, two `Samples16k = ...` archive sites, two `_sounds.PlayStop()` sites.

- [ ] **Step 2: Add the helper**

In `src/Winpepper.App/Hosting/PipelineHost.cs`, add this method directly above
`private void WarnIfSessionSilent(float[] samples, Guid sessionId)`:

```csharp
    /// <summary>
    /// Silence-trims the finished recording for TRANSCRIPTION ONLY. Returns the
    /// trimmed samples to send to ASR, or <c>null</c> when the recording has no
    /// speech (live mic, nobody spoke) and the caller should DROP the dictation.
    /// Logs a content-free info line for either outcome. The ORIGINAL buffer is
    /// still archived by the caller — only the transcription input is trimmed.
    /// Runs AFTER WarnIfSessionSilent, so a dead-mic session has already toasted
    /// (actionable); the quiet drop below adds no toast (consumer policy: a
    /// live-mic-nobody-spoke drop is not actionable).
    /// </summary>
    private float[]? TrimForTranscription(float[] samples, Guid sessionId)
    {
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples);
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            _log.LogInformation("dropped silent recording, {Ms} ms", ms);
            return null;
        }

        if (result.RemovedMs > 0)
            _log.LogInformation(
                "trimmed silence: {Ms} ms across {Runs} runs",
                result.RemovedMs, result.RunsTrimmed);

        return result.Trimmed;
    }
```

- [ ] **Step 3: Wire the hold path (`HoldUp`)**

In the `case HotkeyEventKind.HoldUp:` block, immediately after
`_sounds.PlayStop();` and before `var transcribeSw = System.Diagnostics.Stopwatch.StartNew();`,
insert:

```csharp
                var trimmed = TrimForTranscription(samples, _currentSessionId);
                if (trimmed is null)
                {
                    // Live-mic silence: complete exactly like an empty-final-text
                    // dictation (Transcribing -> Injecting -> Idle) so the pill
                    // returns to idle and does not hang. No transcription,
                    // cleanup, injection, or archive; no toast.
                    _engine.Apply(SessionEvent.TranscriptReady);
                    _engine.Apply(SessionEvent.InjectionCompleted);
                    _ctxPrefetchTask = null;
                    _recordStopwatch = null;
                    break;
                }
```

Then change the transcribe call in the same block from:

```csharp
                var transcription = await transcriber.TranscribeAsync(samples, ct);
```
to:
```csharp
                var transcription = await transcriber.TranscribeAsync(trimmed, ct);
```

Leave the archive call unchanged — it must keep `Samples16k = samples` (the
original buffer).

- [ ] **Step 4: Wire the toggle path (`Toggle` / Recording branch)**

In the `else if (_engine.State == SessionState.Recording)` block of
`case HotkeyEventKind.Toggle:`, immediately after `_sounds.PlayStop();` and
before `var transcribeSw2 = System.Diagnostics.Stopwatch.StartNew();`, insert:

```csharp
                    var trimmed2 = TrimForTranscription(samples2, _currentSessionId);
                    if (trimmed2 is null)
                    {
                        // Live-mic silence: complete like an empty-final-text
                        // dictation (Transcribing -> Injecting -> Idle). No
                        // transcription, cleanup, injection, or archive; no toast.
                        _engine.Apply(SessionEvent.TranscriptReady);
                        _engine.Apply(SessionEvent.InjectionCompleted);
                        _ctxPrefetchTask = null;
                        _recordStopwatch = null;
                        break;
                    }
```

Then change the transcribe call in the same block from:

```csharp
                    var transcription2 = await transcriber2.TranscribeAsync(samples2, ct);
```
to:
```csharp
                    var transcription2 = await transcriber2.TranscribeAsync(trimmed2, ct);
```

Leave the archive call unchanged — it must keep `Samples16k = samples2`.

- [ ] **Step 5: Verify — full non-Windows suite + Windows smoke checklist**

Because `PipelineHost` is Windows-only, verification is (a) the full Linux
suite stays green, and (b) a manual Windows smoke pass.

Run the full non-Windows suite (build + in-process exec each pure-managed test
project; baseline 833/0). Do them one project at a time so a failure is
attributable:
```bash
DOTNET=/home/dan/code/winpepper/.dotnet/dotnet
for P in Winpepper.Core.Tests Winpepper.Audio.Tests Winpepper.History.Tests \
         Winpepper.Models.Tests Winpepper.Cleanup.Tests Winpepper.Corrections.Tests \
         Winpepper.Asr.Tests Winpepper.Platform.Tests Winpepper.IntegrationTests; do
  echo "=== $P ===" && \
  $DOTNET build tests/$P/$P.csproj -c Release && \
  $DOTNET exec tests/$P/bin/Release/net9.0/$P.dll || { echo "FAILED: $P"; break; }
done
```
Expected: every project reports 0 failures; total unchanged from baseline
except `Winpepper.Audio.Tests`, which now includes the new `SilenceTrimmerTests`
(all passing). Any project whose `net9.0` build is skipped because it is
Windows-only is expected — do not treat a Windows-only project as a failure;
confirm the pure-managed subset is green.

**Windows smoke checklist** (run on a Windows host per AGENTS.md; these prove
the wiring the Linux suite cannot reach):
- [ ] Dictate a normal, pause-heavy sentence via the hold hotkey → text is
      pasted correctly; the log shows a `trimmed silence: N ms across R runs`
      line (N > 0) for the pauses.
- [ ] Dictate a short phrase with no long pauses → text pasted; no
      `trimmed silence` line (RemovedMs == 0, so no log spam).
- [ ] Hold the hotkey silently for ~5 s with a LIVE mic (nobody speaks) →
      nothing is pasted; NO toast; the log shows `dropped silent recording,
      N ms`; the status pill returns to idle (does not hang in transcribing).
- [ ] Confirm the same drop behavior via the toggle hotkey (start, stay silent,
      stop) → dropped, pill idle, no toast.
- [ ] Verify the dead-mic path is unchanged: with the mic muted / privacy off,
      the existing "No audio detected" toast still fires (WarnIfSessionSilent),
      and the session still returns to idle.
- [ ] Open the newest history entry's WAV for a pause-heavy dictation → it
      contains the FULL original audio (pauses intact), not the trimmed audio;
      the entry's duration reflects the original recording length.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): drop silent recordings and transcribe trimmed audio in PipelineHost"
```

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Covering task |
|------------------|---------------|
| Pure, unit-tested `SilenceTrimmer` in `Winpepper.Audio` reusing `AudioEnergy.Rms`; constants documented with rationale; `Trim(...)` → `(trimmed, removedMs, runsTrimmed)` + `IsSilent` | Tasks 1-3 |
| 20 ms RMS frames; adaptive threshold `min(max(3*noiseFloor,0.002), 0.15*speechLevel)`; `speechLevel<0.004` ⇒ silent; fail-safe no-op when unseparable | Task 1 (frames/percentiles/silent gate), Task 2 (threshold), Task 3 (cap) |
| Trim runs > cap; interior 600+600, edge 600 adjacent to speech; runs ≤ budget untouched; tail preserved | Task 2 |
| Do NOT modify `AudioEnergy.SilenceRmsThreshold` | Honored — Task 1 only *reuses* `Rms`; no `AudioEnergy` edit in any task |
| PipelineHost wiring on BOTH hold + toggle, after `WarnIfSessionSilent`, before building transcriber | Task 4 (Steps 3, 4) |
| Silent ⇒ drop: content-free log, skip transcribe/cleanup/injection/archive, complete like empty-final-text, no toast | Task 4 (drop block reuses `TranscriptReady`+`InjectionCompleted` seam) |
| Non-silent ⇒ transcribe TRIMMED; log `trimmed silence: N ms across R runs` only when removedMs>0 | Task 4 helper + transcribe-call change |
| Archive ORIGINAL untrimmed samples | Task 4 leaves `Samples16k = samples`/`samples2` unchanged (verified in Steps 3-4) |
| durationMs / timings stay based on original recording | Task 4 — archive uses original buffer; `RecordMs` from `_recordStopwatch`; not conflated |
| No new settings/UI; constants with rationale; no changes to AssemblyAI/cleanup/injection | Tasks 1-4 (constants only; no touched stages) |
| Verification: pure tests on Linux via xUnit v3 in-process runner; full non-Windows suite; Windows smoke checklist | Tasks 1-3 test dll runs; Task 4 Step 5 |
| Trimmer fixtures: pure silence→IsSilent; 3 s gap→1200 split 600/600; 1100 ms untouched; leading/trailing kept 600 adjacent; noisy no-op; tail preservation; removedMs accounting | Task 1 (silence), Task 2 (gaps/edges/tail/accounting), Task 3 (noisy) |

**1b. No silent deferrals of required behavior.** Every user-facing requirement
has a proven production outcome, no stubs/mocks/fakes:
- Trimming and silent detection are proven by real assertions over synthetic
  16 kHz buffers in `SilenceTrimmerTests` (Tasks 1-3) — real algorithm, real
  outputs, no doubles.
- The pipeline drop-vs-transcribe decision is `TrimResult.IsSilent`, proven by
  the same real tests. The PipelineHost wiring is real Windows production code;
  its end-to-end outcome (paste on normal dictation, drop+idle+no-toast on
  silence, original-audio archive) is proven by the Windows smoke checklist
  (Task 4 Step 5). No requirement is moved to "known limitations" or "future
  work"; the Windows-only test boundary is platform-inherent, not a deferral.
- **No UNRESOLVED COVERAGE GAP.**

**2. Placeholder scan.** No "TBD/TODO/handle edge cases/similar to Task N"
placeholders: every step shows complete code or exact commands with expected
output.

**3. Type consistency.** `TrimResult` members (`Trimmed`, `RemovedMs`,
`RunsTrimmed`, `IsSilent`) and `SilenceTrimmer.Trim(ReadOnlySpan<float>)` are
named identically across Tasks 1-4. `TrimForTranscription(float[], Guid)`
returns `float[]?` and is consumed as `trimmed`/`trimmed2` consistently. Session
events (`TranscriptReady`, `InjectionCompleted`) and `SessionEngine.Apply` match
the existing `Winpepper.Core.Sessions` API verified in the source.
