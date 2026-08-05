# Silence Gate Recalibration Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Recalibrate the finished-recording silence gate in `SilenceTrimmer.Trim` to a three-tier speech verdict so quiet short utterances and long-hold/tail-speech dictations are no longer dropped, per the 2026-08-05 recalibration measurement.

**Architecture:** `SilenceTrimmer.Trim` is a pure static function over a finished 16 kHz mono float buffer (20 ms frames, per-frame RMS). Today it has a P90-silent early return (unconditional drop) and an AND-semantics duration-floor gate. The restructure computes cue-budget-deducted absolute tallies (clear @ 0.02, NEW quiet @ 0.010) on BOTH paths, lowers the voiced floor 600→350 ms, and replaces both drop sites with one three-tier OR verdict; any non-silent verdict falls through to the existing trim walk (which provably cannot eat rescued speech — see the threshold-cap proof in Task 3).

**Tech Stack:** C# / .NET 9 (`net9.0`), xUnit v3 in-process runner, Shouldly. Pure managed code in `src/Winpepper.Audio` — no Windows-only dependencies.

## Global Constraints

- Worktree root for ALL work: `/home/dan/code/winpepper/.worktrees/silence-gate-recalibration` — run every command from there.
- Tests green before EVERY commit: full Linux run is `./scripts/linux-tests.sh` (all 9 test projects; pass = exit 0 + final line `LINUX SUITE: GREEN`). NEVER use `dotnet test` (VSTest host is unreliable in this repo); build with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, run with `dotnet exec <built dll> -notrait "Platform=Windows"`.
- Full Windows suite before push/merge to main: `./scripts/windows-gate.sh` from WSL (~12+ min; use a 20–30 min timeout; pass = exit 0 + `GATE: GREEN`).
- Recalibrated verdict tiers (a recording is SPEECH when ANY fires): tier 1 `voiced >= 350 ms` (`MinVoicedDurationMs` 600 → **350**; normal path only), tier 2 `clear@0.02 >= 100 ms` (`ClearSpeechRmsFloor = 0.02`, `MinClearVoicedDurationMs = 100` — values UNCHANGED, now evaluated on BOTH paths), tier 3 `clear@0.010 >= 240 ms` (NEW `QuietSpeechRmsFloor = 0.010`, `MinQuietSpeechDurationMs = 240`; BOTH paths).
- All clear-tier tallies cue-budget-deducted exactly like the existing voiced/clear tallies: count ALL frames ≥ floor, track the in-window share for the first `maskFrames`, deduct up to `budgetFrames` of in-window frames.
- UNTOUCHED: `SilentSpeechLevel = 0.004`, the adaptive-threshold formula `min(max(3*P10, 0.002), 0.15*P90)`, the trim/keep-walk logic (`KeepMsPerEdge = 600` etc.), `TrimResult` field semantics (`VoicedMs` stays 0 on the P90-silent path), and the drop-log line format in `PipelineHost.cs:1870-1872`.
- Constants' doc comments must carry the condensed 2026-08-05 measurement provenance (exact text supplied in Tasks 1–3), replacing the superseded rationale claims.
- README.md is the only end-user markdown doc; this plan is a working/agent doc. Keep commits focused and atomic.
- Out of scope (pre-existing debt, not touched): stale `SilenceTrimmer.cs` line-number citations in `src/Winpepper.Asr/InteriorSilenceSkipper.cs` comments (already stale before this change).

---

## Current-State Orientation (for implementers with zero context)

`src/Winpepper.Audio/SilenceTrimmer.cs` (all line numbers pre-change):

- Constants: `FrameMs=20`/`FrameSamples=320` (L86–88); `KeepMsPerEdge=600` (L91); percentile/threshold constants (L95–99); `SilentSpeechLevel=0.004` (L102); `MinVoicedDurationMs=600` (L115, doc L104–114); `ClearSpeechRmsFloor=0.02` (L128, doc L117–127); `MinClearVoicedDurationMs=100` (L139, doc L130–138).
- `Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0, int prerollMs = 0)` (L157) has 4 exit paths: (A) sub-frame pass-through L161–176; (B) **P90-silent early return L241–268** — `IsSilent=true` unconditionally, `VoicedMs=0`, `ClearVoicedMs` budget-deducted absolute count, fires BEFORE `noiseFloor` is computed at L270; (C) duration-floor drop `voicedMs < 600 && clearVoicedMs < 100` L307–320; (D) trim/keep-walk L322–398.
- Mask/budget frame conversion (ceil) at L198–199; deduction arithmetic `(count - Math.Min(budgetFrames, inWindow)) * FrameMs` at L264 and L303–304.
- `TrimResult` (L4–68): `Trimmed`, `RemovedMs`, `RunsTrimmed`, `IsSilent`, `VoicedMs`, `ClearVoicedMs`, `MaxFrameRms` (post-cue-window max), `HeadSpeechAtMs`, computed `HeadClipped`. All `required init`; construction sites at L165, L257, L309, L388.
- Tests: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` — 28 `[Fact]`s, one file. DC-constant fixtures: `Dc(rms, ms)` produces frames whose RMS is exactly `rms`; `Join(...)` splices. Sole production caller: `PipelineHost.cs:1858` (drop-log at 1870–1872 — format must not change).

### Why the trim walk is safe for rescued P90-silent recordings (decision, per spec)

On the P90-silent path `speechLevel < SilentSpeechLevel = 0.004`, so the trim threshold `min(max(3*P10, 0.002), 0.15*speechLevel)` is capped at `0.15 * speechLevel < 0.0006` — far below both `QuietSpeechRmsFloor` (0.010) and `ClearSpeechRmsFloor` (0.02). Every frame that qualified a rescue tier is therefore ≥ 16x above the trim threshold and can never be classified as trim-silence; the keep-walk keeps all such frames plus 600 ms of adjacent silence per edge. **Decision: run the trim walk for any non-silent verdict; the UNTRIMMED fallback is unnecessary.** Task 3's `Trim_LongHoldTailSpeech_P90Silent_IsRescued_ByQuietTier_NoAudioLoss` test proves zero audio loss on the recalibration scenario (in the typical room-tone case the threshold sits below the room tone too, so nothing is trimmed at all: `RemovedMs = 0`). If that test cannot be made to pass without audio loss, STOP and escalate — do not silently switch to the fallback.

## File Structure

| File | Role in this change |
|---|---|
| `src/Winpepper.Audio/SilenceTrimmer.cs` (modify) | The gate: constants + doc comments + verdict restructure. Only production file touched. |
| `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (modify) | The only SilenceTrimmer test file: flip/adjust drop pins, add rescue + guard tests. |

No new files. `PipelineHost.cs`, `TrimResult` field set, and all trimming tests' expectations stay unchanged.

---

### Task 1: Lower the voiced-duration floor to 350 ms (tier 1)

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs:104-115` (constant + doc comment)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`

**Interfaces:**
- Consumes: existing `SilenceTrimmer.Trim(ReadOnlySpan<float>, int maskMs = 0, int cueBudgetMs = 0, int prerollMs = 0)` and test helpers `Dc(double rms, int ms)` / `Join(params float[][] parts)`.
- Produces: `private const int MinVoicedDurationMs = 350;` — Tasks 2 and 3 rely on this exact name and value in the verdict.

- [ ] **Step 1: Write the failing/adjusted tests**

In `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`:

(a) Replace `Trim_BriefQuietTransient_ShortRecording_IsSilent` (lines 204–224) entirely with (keep the same `var buf` line — the fixture is the 2026-07-28 measured transient):

```csharp
    [Fact]
    public void Trim_BriefQuietTransient_ShortRecording_IsKept_AcceptedTradeoff()
    {
        // KNOWN SACRIFICE of the 2026-08-05 recalibration: the confirmed
        // 2026-07-28 ~450 ms transient class (-36..-45 dBFS) in a SHORT
        // recording now passes via the 350 ms voiced floor (460 >= 350).
        // Accepted trade-off: cost is one wasted ASR call on an archived
        // recording vs 4 real dictations lost in 2 days under the old
        // 600 ms floor. The P90 gate still covers LONG recordings unless
        // the quiet tier fires (see the long-recording pins below).
        var buf = Join(Dc(0.001, 760), Dc(0.015, 460), Dc(0.001, 780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(460);
        result.ClearVoicedMs.ShouldBe(0);
    }
```

(b) Replace `Trim_ModerateVoiced_JustUnderFloor_IsSilent` (lines 226–236) entirely with (new fixture pins the NEW 350 boundary; level 0.008 sits below the Task-2 quiet floor 0.010 so this pin survives tier 3):

```csharp
    [Fact]
    public void Trim_ModerateVoiced_JustUnderFloor_IsSilent()
    {
        // Boundary pin at the 2026-08-05 floor: 340 ms of quiet voiced
        // audio (0.008 RMS -- below the 0.010 quiet tier and the 0.02
        // clear tier) is one frame under the 350 ms floor -> silent.
        // P90 = 0.008 (17/100 frames, idx 89), threshold =
        // min(max(3*0.001, 0.002), 0.15*0.008) = 0.0012.
        var buf = Join(Dc(0.001, 840), Dc(0.008, 340), Dc(0.001, 820));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(340);
    }
```

(c) Replace `Trim_ModerateVoiced_AtDurationFloor_IsKept` (lines 238–248) entirely with:

```csharp
    [Fact]
    public void Trim_ModerateVoiced_AtDurationFloor_IsKept()
    {
        // Boundary pin: 360 ms of quiet voiced audio (0.008 RMS) meets the
        // 350 ms floor (first whole frame >= 350) -> kept via tier 1 ALONE
        // (0.008 is below both the 0.010 quiet and 0.02 clear floors).
        // Protects soft-spoken dictation.
        var buf = Join(Dc(0.001, 820), Dc(0.008, 360), Dc(0.001, 820));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(360);
    }
```

(d) Replace `Trim_ClearSpeech_JustUnderClearTier_IsSilent` (lines 296–308) entirely with (old fixture had 460 ms voiced which the new floor would rescue; the new fixture keeps every tier just under its floor):

```csharp
    [Fact]
    public void Trim_ClearSpeech_JustUnderClearTier_IsSilent()
    {
        // The clear boundary's one-frame-under twin, recalibrated so no
        // tier fires: clear = 80 < 100; voiced = 180 + 80 = 260 < 350;
        // quiet-tier content (>= 0.010) = 80 < 240 (the 0.008 block sits
        // below the quiet floor). P90 = 0.008 (13/85 frames, idx 75),
        // threshold = min(max(3*0.001, 0.002), 0.15*0.008) = 0.0012.
        var buf = Join(Dc(0.001, 700), Dc(0.008, 180), Dc(0.05, 80), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }
```

(e) Add a new test directly after `Trim_ModerateVoiced_AtDurationFloor_IsKept`:

```csharp
    [Fact]
    public void Trim_QuietShortUtterance_MidVoiced_IsKept_By350Floor()
    {
        // 2026-08-05 recalibration scenario: the two quiet real "you have"
        // takes (voiced 360/500 ms, max frame RMS 0.0093-0.0275,
        // clear@0.02 = 0) were false-rejected by the old 600 ms floor.
        // Encoded as 500 ms @ 0.015 in a 2 s capture: P90 = 0.015
        // (25/100 frames, idx 89), threshold = min(max(3*0.001, 0.002),
        // 0.15*0.015) = 0.00225, voiced = 500 >= 350 -> kept.
        var buf = Join(Dc(0.001, 760), Dc(0.015, 500), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(500);
        result.ClearVoicedMs.ShouldBe(0);
    }
```

- [ ] **Step 2: Run tests to verify the expected failures**

```bash
cd /home/dan/code/winpepper/.worktrees/silence-gate-recalibration
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows" -class Winpepper.Audio.Tests.SilenceTrimmerTests
```

Expected: FAIL with exactly 3 failures — `Trim_BriefQuietTransient_ShortRecording_IsKept_AcceptedTradeoff`, `Trim_ModerateVoiced_AtDurationFloor_IsKept`, `Trim_QuietShortUtterance_MidVoiced_IsKept_By350Floor` (each asserts `IsSilent` false but the old 600 ms floor still drops them). The re-fixtured (b) and (d) pass both before and after.

- [ ] **Step 3: Change the constant and its doc comment**

In `src/Winpepper.Audio/SilenceTrimmer.cs`, replace lines 104–115 (the whole doc comment + `MinVoicedDurationMs` declaration) with:

```csharp
    /// <summary>
    /// Tier 1: minimum total duration of voiced (above-adaptive-threshold)
    /// audio a recording must contain to count as speech. Only meaningful
    /// on the normal path -- below SilentSpeechLevel the adaptive
    /// threshold has no speech level to anchor to and VoicedMs is 0.
    /// RECALIBRATED 2026-08-05: the gate was replicated bit-exactly over
    /// the retained 100-recording archive (computed voiced/clear/max-RMS
    /// match the logged drop lines exactly) plus 87 enriched drop lines
    /// from 14 days of logs; 8 drop-archived recordings were human-labeled.
    /// Two quiet real "you have" takes (voiced 360/500 ms, max frame RMS
    /// 0.0093-0.0275, clear@0.02 = 0) were false-rejected by the old
    /// 600 ms floor; 350 admits both. All 93 kept real dictations remain
    /// kept (the rule only loosens). KNOWN SACRIFICE: the 2026-07-28
    /// confirmed ~450 ms transient class (-36..-45 dBFS) is no longer
    /// archived -- voiced >= 350 admits such a transient in a SHORT
    /// recording (the P90 gate still covers long recordings unless the
    /// quiet tier fires). Accepted trade-off: one wasted ASR call on an
    /// archived recording vs 4 lost dictations in 2 days -- see
    /// Trim_BriefQuietTransient_ShortRecording_IsKept_AcceptedTradeoff.
    /// </summary>
    private const int MinVoicedDurationMs = 350;
```

- [ ] **Step 4: Run the class tests to verify they pass**

Same commands as Step 2. Expected: PASS (`Failed: 0`).

- [ ] **Step 5: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0, final line `LINUX SUITE: GREEN`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): lower the voiced-duration floor to 350 ms (2026-08-05 recalibration)"
```

---

### Task 2: Add the quiet-speech tier (240 ms @ 0.010) on the normal path

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (new constants after `MinClearVoicedDurationMs`; tally restructure in `Trim` around current lines 241–320; `ClearSpeechRmsFloor` doc comment)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`

**Interfaces:**
- Consumes: Task 1's `MinVoicedDurationMs = 350`; existing locals in `Trim`: `rms` (double[] per-frame RMS), `frameCount`, `maskFrames`, `budgetFrames`, `postWindowMax`, `sorted`, `speechLevel`, `headSpeechAtMs`.
- Produces: `private const double QuietSpeechRmsFloor = 0.010;` and `private const int MinQuietSpeechDurationMs = 240;`; locals `clearVoicedMs` and `quietVoicedMs` (both `int`, budget-deducted ms) computed BEFORE the `speechLevel < SilentSpeechLevel` branch — Task 3's verdict consumes all of these by these exact names.

- [ ] **Step 1: Write the failing test (flip the drop pin into a rescue pin)**

Replace `Trim_QuietShortUtterance_IsDropped_KnownResidual` (test file lines 310–327) entirely with (keep the same `var buf` line — it encodes the archived "Thank you." takes):

```csharp
    [Fact]
    public void Trim_QuietShortUtterance_IsRescued_ByQuietTier()
    {
        // Formerly Trim_QuietShortUtterance_IsDropped_KnownResidual -- the
        // 2026-08-05 recalibration flips the verdict. The two archived
        // "Thank you." dictations (voiced 240/260 ms, max frame RMS
        // 0.013-0.017) were REAL SPEECH false-rejects. Encoded as 260 ms
        // @ 0.015 in a 2 s capture: P90 = 0.015 passes, voiced = 260 < 350
        // (tier 1 misses), clear@0.02 = 0 (tier 2 misses), but 260 ms
        // >= 0.010 clears the 240 ms quiet tier -> KEPT.
        var buf = Join(Dc(0.001, 860), Dc(0.015, 260), Dc(0.001, 880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(260);
    }
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /home/dan/code/winpepper/.worktrees/silence-gate-recalibration
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows" -method "Winpepper.Audio.Tests.SilenceTrimmerTests.Trim_QuietShortUtterance_IsRescued_ByQuietTier"
```

Expected: FAIL — `result.IsSilent should be False but was True` (260 < 350 and clear 0 < 100; no quiet tier exists yet).

- [ ] **Step 3: Add the two constants**

In `src/Winpepper.Audio/SilenceTrimmer.cs`, insert directly after the `MinClearVoicedDurationMs` declaration (currently line 139):

```csharp
    /// <summary>
    /// Tier 3 level: frames at or above this RMS (~-40 dBFS) are plausibly
    /// QUIET speech. MEASURED (2026-08-05 recalibration: gate replicated
    /// bit-exactly over the retained 100-recording archive + 87 enriched
    /// drop lines from 14 days of logs; 8 drop-archived recordings
    /// human-labeled): the two real long-hold false-rejects carried
    /// 460 ms / 280 ms at or above 0.010 on the P90-silent path, while all
    /// 4 labeled non-speech drops have max frame RMS &lt;= 0.0010 with at
    /// most 80 ms of budget-deducted content at or above 0.010 (start-cue
    /// leakage past the 100 ms cue budget).
    /// </summary>
    private const double QuietSpeechRmsFloor = 0.010;

    /// <summary>
    /// Tier 3 duration: quiet-speech audio (>= QuietSpeechRmsFloor,
    /// cue-budget-deducted) needed to count as speech. 240 ms = 3x the
    /// measured 80 ms worst-case start-cue-leakage noise floor (see
    /// QuietSpeechRmsFloor), and sits at or under both measured real
    /// rescues (460/280 ms). Evaluated on BOTH paths, including
    /// P90-silent.
    /// </summary>
    private const int MinQuietSpeechDurationMs = 240;
```

- [ ] **Step 4: Restructure the tallies and extend the normal-path verdict**

In `Trim`, replace the current block from `if (speechLevel < SilentSpeechLevel)` (line 241) through the duration-floor `if` closing brace (line 320) — i.e. paths B and C — with the code below. The P90-silent branch STAYS an unconditional drop in this task (Task 3 changes that); its local clear loop is replaced by the shared tallies, which are byte-equivalent there (the old P90 branch already counted ALL frames ≥ 0.02, budget-deducted). On the normal path the clear tally moves from threshold-nested to absolute — equivalent whenever threshold ≤ 0.02, which holds for every fixture and matches ClearVoicedMs's documented "Absolute" semantics:

```csharp
        // Absolute-floor tallies (tier 2 clear and tier 3 quiet), computed
        // on BOTH paths and cue-budget-deducted exactly like the voiced
        // tally: count ALL frames at/above each floor, track the in-window
        // share, then deduct up to the budget of in-window frames.
        var clearFrames = 0;
        var clearFramesInWindow = 0;
        var quietFrames = 0;
        var quietFramesInWindow = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] >= ClearSpeechRmsFloor)
            {
                clearFrames++;
                if (f < maskFrames) clearFramesInWindow++;
            }
            if (rms[f] >= QuietSpeechRmsFloor)
            {
                quietFrames++;
                if (f < maskFrames) quietFramesInWindow++;
            }
        }
        var clearVoicedMs = (clearFrames - Math.Min(budgetFrames, clearFramesInWindow)) * FrameMs;
        var quietVoicedMs = (quietFrames - Math.Min(budgetFrames, quietFramesInWindow)) * FrameMs;

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is
            // derived from a speech level that does not exist), so
            // VoicedMs reports 0. The clear count is reported
            // budget-deducted so the cue cannot inflate the recalibration
            // fields (pre-mask logs showed clear = 60-160 ms of pure beep
            // on every silent drop).
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = postWindowMax,
                HeadSpeechAtMs = headSpeechAtMs,
            };
        }

        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive DECISION threshold -- same formula as always, over all
        // frames (identical to the trim threshold below).
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech,
        // silence cannot be confidently separated. Capping the threshold
        // at a fraction of speechLevel keeps genuine silence-vs-speech
        // separable and makes low-SNR recordings a no-op instead of eating
        // real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        // Tier 1 voiced tally (2026-07-28 transient-rejection fix,
        // recalibrated 2026-08-05). Tally ALL frames, tracking the
        // in-window share, then deduct up to the cue budget of the loudest
        // in-window frames. The tallies are frame COUNTS, so "loudest
        // first" reduces to capping the deduction at the in-window share.
        var voicedFrames = 0;
        var voicedFramesInWindow = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] < threshold) continue;
            voicedFrames++;
            if (f < maskFrames) voicedFramesInWindow++;
        }
        var voicedMs = (voicedFrames - Math.Min(budgetFrames, voicedFramesInWindow)) * FrameMs;
        var maxFrameRms = postWindowMax;

        // Three-tier speech verdict (2026-08-05 recalibration): the
        // recording is SPEECH when ANY tier fires.
        if (voicedMs < MinVoicedDurationMs
            && clearVoicedMs < MinClearVoicedDurationMs
            && quietVoicedMs < MinQuietSpeechDurationMs)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = voicedMs,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = maxFrameRms,
                HeadSpeechAtMs = headSpeechAtMs,
            };
        }
```

(Everything after — the `var trimThreshold = threshold;` line, the walk, and the final `return` — stays untouched.)

- [ ] **Step 5: Update the ClearSpeechRmsFloor doc comment**

Its "Known residual" sentence (currently lines 123–126, naming the old test) is superseded. Replace the doc comment (lines 117–127) above `ClearSpeechRmsFloor` with:

```csharp
    /// <summary>
    /// Tier 2 level: frames at or above this RMS (~-34 dBFS) are "clearly
    /// speech-loud". MEASURED (2026-07-28, 100-recording archive): every
    /// archived non-speech file has at most ONE 20 ms frame at or above
    /// 0.02, while loud short utterances reach it -- but 17% of real
    /// dictations never do, so this tier is a loud-short-utterance escape
    /// hatch, NOT a speech test. Since 2026-08-05 it is evaluated on BOTH
    /// paths (including P90-silent); quiet short utterances below it are
    /// handled by the quiet tier (QuietSpeechRmsFloor) -- see
    /// Trim_QuietShortUtterance_IsRescued_ByQuietTier.
    /// </summary>
```

- [ ] **Step 6: Run the class tests to verify they pass**

```bash
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows" -class Winpepper.Audio.Tests.SilenceTrimmerTests
```

Expected: PASS (`Failed: 0`) — the flipped test passes via tier 3; all drop pins that must survive (re-fixtured boundaries at 0.008, beep-budget tests at 40 ms residue, P90-path pins) stay green.

- [ ] **Step 7: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0, `LINUX SUITE: GREEN`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): add the 240 ms @ 0.010 quiet-speech tier to the silence gate"
```

---

### Task 3: Consult tiers 2 and 3 on the P90-silent path; trim rescued recordings

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (verdict unification in `Trim`; doc comments: `SilentSpeechLevel`, `MinClearVoicedDurationMs`, `TrimResult.VoicedMs`, `TrimResult.ClearVoicedMs`)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`

**Interfaces:**
- Consumes: Task 2's `clearVoicedMs` / `quietVoicedMs` locals and `QuietSpeechRmsFloor` / `MinQuietSpeechDurationMs` constants; existing trim walk (unchanged) starting at `var trimThreshold = threshold;`.
- Produces: the final `Trim` decision shape — one silent-return site, three-tier OR verdict on both paths, `VoicedMs = 0` whenever `speechLevel < SilentSpeechLevel`. No API/field changes; `PipelineHost` untouched.

- [ ] **Step 1: Write the failing/adjusted tests**

In `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`:

(a) Replace `Trim_SparseSpeechBurst_LongRecording_IsSilent_KnownResidual` (test lines 363–381) entirely with (keep the same `var buf` line; this is the spec's tier-2-on-P90-path test):

```csharp
    [Fact]
    public void Trim_SparseSpeechBurst_LongRecording_IsRescued_ByClearTier()
    {
        // Formerly ..._IsSilent_KnownResidual -- the 2026-08-05
        // recalibration flips it: the P90-silent path is no longer an
        // unconditional drop; tiers 2 and 3 are consulted there. A real
        // 300 ms burst @ 0.05 in a 10 s mostly-silent recording lands P90
        // on the room tone, but clear@0.02 = 300 >= 100 -> KEPT (tier 2).
        // VoicedMs stays 0 on this path (adaptive threshold undefined).
        // Trim safety: threshold = 0.15 * P90 = 0.00015 sits below the
        // 0.001 room tone, so nothing is trimmed -- no audio loss.
        var buf = Join(Dc(0.001, 4840), Dc(0.05, 300), Dc(0.001, 4860));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(300);
        result.RemovedMs.ShouldBe(0);
        result.Trimmed.Length.ShouldBe(buf.Length);
    }
```

(b) Replace `Trim_BriefQuietTransient_LongRecording_IsSilent` (test lines 345–361) entirely with (level drops 0.015 → 0.008 so the pin's purpose — P90 still drops long-recording transients — survives tier 3, which would rescue 460 ms at 0.015):

```csharp
    [Fact]
    public void Trim_BriefQuietTransient_LongRecording_IsSilent()
    {
        // A 460 ms sub-quiet transient (0.008 RMS, below the 0.010 quiet
        // floor) in a 10 s capture is only ~4.6% of frames: the P90 gate
        // holds and no rescue tier fires (clear = 0, quiet-tier content
        // = 0) -> still dropped. VoicedMs reports 0 on this path but the
        // absolute fields stay meaningful for recalibration.
        var buf = Join(Dc(0.001, 4760), Dc(0.008, 460), Dc(0.001, 4780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
        result.MaxFrameRms.ShouldBe(0.008, 0.0005);
    }
```

(c) Add the following new tests after it:

```csharp
    [Fact]
    public void Trim_LongHoldTailSpeech_P90Silent_IsRescued_ByQuietTier_NoAudioLoss()
    {
        // 2026-08-05 recalibration scenario: the archived 7.15 s long-hold
        // with tail speech (P90-silent path, 460 ms @ 0.010) was a REAL
        // SPEECH false-reject. Encoded as 460 ms @ 0.012 outside the cue
        // window in a 7.16 s capture (mask 1000 / budget 100 / preroll
        // 500): speech frames start at 3300 ms, far past the 1000 ms
        // window, so nothing is deducted; quiet = 460 >= 240 -> KEPT
        // (tier 3 on the P90-silent path). NO AUDIO LOSS: the trim
        // threshold caps at 0.15 * P90 = 0.00015, below the 0.001 room
        // tone, so the returned buffer is the input, sample-identical.
        var buf = Join(Dc(0.001, 3300), Dc(0.012, 460), Dc(0.001, 3400));

        var result = SilenceTrimmer.Trim(buf, 1000, 100, 500);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(0);
        result.RemovedMs.ShouldBe(0);
        result.Trimmed.Length.ShouldBe(buf.Length);
        result.Trimmed.SequenceEqual(buf).ShouldBeTrue();
    }

    [Fact]
    public void Trim_QuietTier_AtFloor_P90SilentPath_IsKept()
    {
        // Tier 3 boundary: exactly 240 ms @ 0.012 in a 10 s P90-silent
        // capture (12/500 frames; P90 = 0.001) meets the 240 ms floor ->
        // kept. The archived 6.0 s "when it posts" long-hold (280 ms
        // @ 0.010) sits just above this boundary.
        var buf = Join(Dc(0.001, 4880), Dc(0.012, 240), Dc(0.001, 4880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.VoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_QuietTier_JustUnderFloor_P90SilentPath_IsSilent()
    {
        // Tier 3 boundary's one-frame-under twin: 220 ms @ 0.012 in a
        // 10 s P90-silent capture -> no tier fires -> dropped.
        var buf = Join(Dc(0.001, 4900), Dc(0.012, 220), Dc(0.001, 4880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_QuietCueLeakage_InsideMask_StillDrops()
    {
        // Beep-leakage guard (2026-08-05 measurement: all 4 human-labeled
        // non-speech drops had <= 80 ms of budget-deducted quiet-floor
        // content -- start-cue leakage). A recording whose ONLY >= 0.010
        // content is ~80 ms inside the cue window (mask 1500 / budget 100)
        // deducts to 0 quiet ms and stays dropped. 4 s capture ->
        // P90 = 0.001, P90-silent path.
        var buf = Join(Dc(0.001, 600), Dc(0.012, 80), Dc(0.001, 3320));

        var result = SilenceTrimmer.Trim(buf, 1500, 100);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
        result.MaxFrameRms.ShouldBe(0.001, 0.0005);
    }

    [Fact]
    public void Trim_QuietContentInsideMask_IsBudgetDeducted_BeforeQuietTier()
    {
        // Proves the tier 3 tally is cue-budget-deducted: 300 ms @ 0.012
        // entirely inside the 1500 ms window. Unmasked it clears the tier
        // (300 >= 240 -> kept); with budget 100 (5 frames) it deducts to
        // 200 < 240 and DROPS. Without the deduction the masked call
        // would pass -- this pin is what keeps cue pickup from unlocking
        // the quiet tier.
        var buf = Join(Dc(0.001, 600), Dc(0.012, 300), Dc(0.001, 3100));

        SilenceTrimmer.Trim(buf).IsSilent.ShouldBeFalse(); // tier 3, unmasked

        var masked = SilenceTrimmer.Trim(buf, 1500, 100);
        masked.IsSilent.ShouldBeTrue();
        masked.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_TrueSilence_AtNoiseFloor_IsStillDropped()
    {
        // 2026-08-05 non-speech class: all 4 human-labeled non-speech
        // drops have max frame RMS <= 0.0010 -- essentially silence. A
        // 5 s capture at exactly 0.001 fires no tier -> dropped.
        var buf = Dc(0.001, 5000);

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
        result.MaxFrameRms.ShouldBe(0.001, 0.0005);
        result.Trimmed.Length.ShouldBe(0);
    }
```

- [ ] **Step 2: Run tests to verify the expected failures**

```bash
cd /home/dan/code/winpepper/.worktrees/silence-gate-recalibration
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows" -class Winpepper.Audio.Tests.SilenceTrimmerTests
```

Expected: FAIL with exactly 4 failures — `Trim_SparseSpeechBurst_LongRecording_IsRescued_ByClearTier`, `Trim_LongHoldTailSpeech_P90Silent_IsRescued_ByQuietTier_NoAudioLoss`, `Trim_QuietTier_AtFloor_P90SilentPath_IsKept`, `Trim_QuietContentInsideMask_IsBudgetDeducted_BeforeQuietTier` (its unmasked leg). The P90-silent early return still drops all of them unconditionally. (b) and the remaining new drop pins pass both before and after.

- [ ] **Step 3: Unify the verdict**

In `Trim`, replace the block from `if (speechLevel < SilentSpeechLevel)` through the three-tier silent-return's closing brace (i.e. everything Task 2 Step 4 inserted AFTER the shared tally block, up to but not including `var trimThreshold = threshold;`) with:

```csharp
        var noiseFloor = Percentile(sorted, NoiseFloorPercentile);

        // Adaptive DECISION threshold -- same formula as always, over all
        // frames (identical to the trim threshold below).
        var threshold = Math.Max(ThresholdNoiseMultiplier * noiseFloor, ThresholdAbsFloor);
        // Fail-safe: when the noise floor is high relative to speech,
        // silence cannot be confidently separated. Capping the threshold
        // at a fraction of speechLevel keeps genuine silence-vs-speech
        // separable and makes low-SNR recordings a no-op instead of eating
        // real audio.
        threshold = Math.Min(threshold, SpeechCapFactor * speechLevel);

        // Tier 1 (voiced duration) exists only when the P90 gate finds a
        // speech level: below SilentSpeechLevel the adaptive threshold is
        // derived from a speech level that does not exist, so VoicedMs
        // reports 0 (unchanged field semantics) and tier 1 cannot fire.
        // Tally ALL frames, tracking the in-window share, then deduct up
        // to the cue budget of the loudest in-window frames. The tallies
        // are frame COUNTS, so "loudest first" reduces to capping the
        // deduction at the in-window share.
        var voicedMs = 0;
        if (speechLevel >= SilentSpeechLevel)
        {
            var voicedFrames = 0;
            var voicedFramesInWindow = 0;
            for (var f = 0; f < frameCount; f++)
            {
                if (rms[f] < threshold) continue;
                voicedFrames++;
                if (f < maskFrames) voicedFramesInWindow++;
            }
            voicedMs = (voicedFrames - Math.Min(budgetFrames, voicedFramesInWindow)) * FrameMs;
        }
        var maxFrameRms = postWindowMax;

        // Three-tier speech verdict (2026-08-05 recalibration): the
        // recording is SPEECH when ANY tier fires. Tiers 2 and 3 are
        // consulted on BOTH paths -- the P90-silent path is no longer an
        // unconditional drop (it false-rejected two real long-hold
        // dictations: 460 ms and 280 ms @ >= 0.010).
        if (voicedMs < MinVoicedDurationMs
            && clearVoicedMs < MinClearVoicedDurationMs
            && quietVoicedMs < MinQuietSpeechDurationMs)
        {
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = voicedMs,
                ClearVoicedMs = clearVoicedMs,
                MaxFrameRms = maxFrameRms,
                HeadSpeechAtMs = headSpeechAtMs,
            };
        }
```

Then extend the comment directly above `var trimThreshold = threshold;` (keep its existing text about trim == decision threshold) by appending these lines to that comment block:

```csharp
        // On a rescued P90-silent recording the SpeechCapFactor cap binds:
        // threshold = 0.15 * speechLevel < 0.15 * SilentSpeechLevel =
        // 0.0006, far below QuietSpeechRmsFloor (0.010) -- no frame that
        // qualified a rescue tier can be classified as trim-silence, so
        // the walk cannot eat rescued speech. Pinned by
        // Trim_LongHoldTailSpeech_P90Silent_IsRescued_ByQuietTier_NoAudioLoss.
```

- [ ] **Step 4: Update the superseded doc comments**

(a) `SilentSpeechLevel` (current line 101 doc): replace

```csharp
    /// <summary>Below this 90th-percentile RMS the recording has no speech.</summary>
```

with

```csharp
    /// <summary>
    /// Below this 90th-percentile RMS the adaptive threshold has no speech
    /// level to anchor to: tier 1 (voiced duration) is unavailable and
    /// VoicedMs reports 0. Since 2026-08-05 this is NOT an unconditional
    /// drop -- the clear and quiet tiers still apply.
    /// </summary>
```

(b) `MinClearVoicedDurationMs` doc (currently lines 130–138): keep the measured-margin text, but replace its first sentence

```
    /// Clear-speech-loud audio needed to bypass the duration floor. 100 ms
```

with

```
    /// Tier 2 duration: clear-speech-loud audio needed to count as speech
    /// on EITHER path (2026-08-05: also consulted on the P90-silent
    /// path). 100 ms
```

(c) `TrimResult.VoicedMs` doc (currently lines 22–29): replace the sentence starting `/// 0 when the P90 gate fired` (lines 26–27) with:

```csharp
    /// 0 whenever the recording's P90 is below SilentSpeechLevel (the
    /// adaptive threshold is derived from a speech level that does not
    /// exist there) -- including recordings rescued by the clear/quiet
    /// tiers -- and for sub-frame buffers.
```

(d) `TrimResult.ClearVoicedMs` doc (currently lines 32–40): replace the parenthetical `(they were measured from one 100-recording archive, 2026-07-28, and are provisional)` with `(measured 2026-07-28, recalibrated 2026-08-05 against the archive plus 14 days of drop-line logs)`.

- [ ] **Step 5: Run the class tests to verify they pass**

Same commands as Step 2. Expected: PASS (`Failed: 0`) — rescues fire, drop pins hold, and every pre-existing trimming-behavior test is byte-identical (normal-path threshold and walk untouched).

- [ ] **Step 6: Run the full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: exit 0, `LINUX SUITE: GREEN`.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): consult clear/quiet tiers on the P90-silent path and trim rescued recordings"
```

---

### Task 4: Full-suite verification (Linux + Windows gate)

**Files:**
- No source changes. Verification only — required by AGENTS.md before this branch can land on main.

**Interfaces:**
- Consumes: the three commits from Tasks 1–3.
- Produces: a green `LINUX SUITE: GREEN` run and a green `GATE: GREEN` Windows run on the final tree.

- [ ] **Step 1: Confirm a clean tree and run the full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/silence-gate-recalibration
git status --short   # expected: empty (no uncommitted changes)
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
export PATH="$DOTNET_ROOT:$PATH"
./scripts/linux-tests.sh
```

Expected: exit 0, final line `LINUX SUITE: GREEN`, all 9 projects `Failed: 0`.

- [ ] **Step 2: Run the Windows gate (required before push/merge to main)**

The change is pure managed code (Winpepper.Audio), so Linux covers its logic — but the repo rule requires the full Windows suite before landing on main. Run from WSL with a 20–30 minute timeout:

```bash
cd /home/dan/code/winpepper/.worktrees/silence-gate-recalibration
./scripts/windows-gate.sh
```

Expected: exit 0 with `GATE: GREEN` (12 project/TFM runs, all `Failed: 0`). Note: the gate wipes cross-OS `bin/`/`obj/` — re-build on Linux afterwards if further Linux test runs are needed.

- [ ] **Step 3: Report**

No commit in this task. If either gate is RED, fix within the offending task's scope, re-run both gates, and amend/commit as appropriate — do not land on main with a red gate.

---

## Spec Coverage Map (self-review record)

| Spec requirement | Covered by |
|---|---|
| Tier 1: voiced ≥ 350 ms (600 → 350, normal path only) | Task 1 (constant + doc), Task 3 Step 3 (tier-1-unavailable-on-P90 guard) |
| Tier 2: clear@0.02 ≥ 100 ms on BOTH paths (values unchanged) | Task 2 (shared tally), Task 3 (P90-path verdict + `Trim_SparseSpeechBurst_LongRecording_IsRescued_ByClearTier`) |
| Tier 3: clear@0.010 ≥ 240 ms, new constants, BOTH paths | Task 2 (constants + normal path), Task 3 (P90 path) |
| Cue-budget deduction on all clear-tier tallies | Task 2 Step 4 (tally block), pinned by `Trim_QuietContentInsideMask_IsBudgetDeducted_BeforeQuietTier` and existing beep tests |
| VoicedMs stays 0 on P90-silent path; TrimResult semantics, drop-log format, trim constants untouched | Task 3 Step 3 (`voicedMs = 0` branch) + Global Constraints; no PipelineHost edits anywhere |
| Rescued P90-silent recordings trimmed like normal (or safe fallback) | Task 3: fall-through to walk + threshold-cap proof + `..._NoAudioLoss` test (decision: no fallback needed) |
| Condensed 2026-08-05 provenance in constants' doc comments, incl. known sacrifice | Task 1 Step 3, Task 2 Steps 3/5, Task 3 Step 4 |
| Flip quiet-short-utterance drop pin into rescue pin, renamed | Task 2 Step 1 |
| New: ~360–500 ms voiced below 0.02 passes via 350 floor | Task 1 Step 1(e) (`Trim_QuietShortUtterance_MidVoiced_IsKept_By350Floor`; boundary pair 340/360 in 1(b)/1(c)) |
| New: P90-silent + ≥ 240 ms @ ≥ 0.010 outside cue window passes, no speech-audio loss | Task 3 Step 1(c) (`..._NoAudioLoss`, plus 240/220 boundary pair) |
| New: P90-silent + ≥ 100 ms @ ≥ 0.02 passes | Task 3 Step 1(a) |
| New: beep-leakage guard (~80 ms @ ≥ 0.010 in cue window, mask 1500/budget 100, still dropped) | Task 3 Step 1(c) (`Trim_QuietCueLeakage_InsideMask_StillDrops`) |
| New: true silence (max RMS ≤ 0.001) still dropped; short transient under all tiers still dropped | Task 3 Step 1(c) (`Trim_TrueSilence_AtNoiseFloor_IsStillDropped`); Task 1 Step 1(b)/(d) (340 ms @ 0.008; 260 ms voiced / 80 ms clear / 80 ms quiet) |
| Keep/adjust existing tests; trimming tests green unchanged | Tasks 1–3 audit every drop pin (only L205/L227/L297/L311/L346/L364 move — all with exact replacements); trim walk and thresholds untouched |
| Linux suite green before every commit; Windows gate before landing | Every task's final steps + Task 4 |
