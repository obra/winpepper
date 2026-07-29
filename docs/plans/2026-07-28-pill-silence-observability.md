# Pill Keep-Alive, Silence Gate, and Responsiveness Observability Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix two diagnosed bugs (the pending-paste pill losing topmost z-order across window switches; the silence gate letting a brief transient unlock a whole silent recording) and add responsiveness observability so the logs alone can answer "where did the time go?" after the fact.

**Architecture:** All pure decision logic lands in Linux-testable `net9.0` code (`Winpepper.Audio.SilenceTrimmer` gate, `Winpepper.Core.ViewModels.PillTimerPolicy`, `Winpepper.Core.Diagnostics.DictationTimingSummary`, `Winpepper.Platform.Injection` run report). The Windows-only WinUI/`PipelineHost` layers get thin wiring that consumes those helpers, verified by compile under the Windows gate plus an on-device smoke checklist recorded in commit messages. The repo has zero `PipelineHost` tests by established pattern; `StatusPillWindow` is untestable WinUI code-behind — this split (policy in Core, wiring in App) is the house pattern.

**Tech Stack:** .NET 9 (`net9.0` pure projects; `net9.0-windows10.0.19041.0` WinUI), xUnit v3 + Shouldly, Serilog behind `Microsoft.Extensions.Logging`, `./scripts/linux-tests.sh` (Linux suite) and `./scripts/windows-gate.sh` (full Windows suite from WSL).

## Global Constraints

- **DO NOT change injection pacing constants or pacing behavior.** `TargetFeedUnitsPerSecond` stays `600`; `InterChunkPauseMs` stays `14`; the `DeadlinePacer` path is observed through, never altered (pauses stay net of send time).
- **DO NOT touch the hotkey-hook injected-event fast path** (`src/Winpepper.Platform/Hotkeys/HotkeyHook.cs:105–135`). No instrumentation inside it — its entire value is being the shortest route out of the hook callback.
- **Preserve pending-paste retain+append semantics** (council hardening 7557efc/892baaa — the "Pending paste retained across new dictation" log line stays). A visual cue for retained parks is DEFERRED — no UI work for it.
- **No new settings/UI knobs** — all new thresholds are documented `const`s (owner decision recorded in `docs/plans/2026-07-24-silence-trimming.md`).
- All new log lines are `LogInformation` or higher (`minimumLevel` is hard-coded to `Information`; `LogDebug` never ships). Everything a user should see must be in the message template itself (the in-app Diagnostics tail renders `{Message:lj}` only).
- **Content-free logging:** never log user text — names/counts/durations only.
- All tests green before every commit: run `./scripts/linux-tests.sh` (NEVER `dotnet test`; the script builds each test project `-c Release` then `dotnet exec`s the built dll). Full Windows suite before finishing: `./scripts/windows-gate.sh` from WSL (~10 min; transient UNC MSB4025 "retry should be performed" failures and vsock interop flakes are known — retry the gate). Never mix Linux- and Windows-side builds in the same `bin`/`obj`; never run `linux-tests.sh` concurrently with the gate.
- Commit messages: Conventional-Commits subject with scope, prose body, and the exact Amplifier trailer (blank line before each trailer line):

  ```
  🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```
- **Do NOT push to origin.** Leave the branch (`feat/pill-silence-observability`) local; the root session merges to main.
- README.md untouched; this plan is the only new doc. Historical plans under `docs/plans/` are records of decisions as-made — do not retro-edit them.
- Work happens inside the worktree `/home/dan/code/winpepper/.worktrees/pill-silence-observability`. All file paths below are relative to that worktree root. Line numbers refer to the tree at base `f6d043b` — verify anchors against the current file before editing (earlier tasks shift later anchors).

## Known Verification Gaps and Accepted Residuals (record, do not "fix")

These are spec-acknowledged limits. Each is pinned by a test or recorded in a commit message — none is silent scope reduction.

1. **Pill fix (Task 4) on-device efficacy cannot be proven by automated gates** — the Windows gate only compiles WinUI code; no test project references `Winpepper.App` and `StatusPillWindow` cannot be instantiated off a live XAML app. The testable invariant ("timer runs whenever pill visible") is extracted to `PillTimerPolicy` and pinned on Linux (Task 3). The on-device smoke checklist is recorded in Task 4's commit message. Residual failure modes to record there: an occluder that itself re-asserts topmost at ≤100 ms cadence, and higher-band (ZBID/UIAccess) occluders.
2. **Silence gate measured residual (a):** 2 of 93 archived real dictations (quiet short "Thank you." utterances, max frame RMS 0.013–0.017) are now dropped — their level band overlaps the transient class; no energy-only constant can rescue them. Non-destructive (audio is always archived; recoverable within the last 50 dictations). Pinned by `Trim_QuietShortUtterance_IsDropped_KnownResidual`.
3. **Silence gate measured residual (b):** a sustained quiet transient ≥ 600 ms (e.g. a 900 ms door rumble) still passes — an energy detector cannot distinguish it from quiet speech. Pinned by `Trim_SustainedQuietTransient_IsKept_KnownResidual`.
4. **Pre-existing P90 sparse-speech residual:** a real brief speech burst in a long mostly-silent recording is dropped by the P90 gate (the false-positive direction the 2026-07-24 plan accepted and promised — but never wrote — a characterization test for). Pinned by `Trim_SparseSpeechBurst_LongRecording_IsSilent_KnownResidual`.
5. **Five of the eight stage budgets are PROVISIONAL** (no log evidence exists for them yet — closing that gap is this feature's point). Cleanup and the two ASR budgets are measured from production-log distributions (cleanup window 2026-07-17→28; re-verified against the raw logs 2026-07-28: Llm n=455, p50=505 ms, p90=816 ms, one overrun). The cleanup live-swap merged at base `f6d043b` AFTER the measurement window — recheck the cleanup distribution from the first week of `dictation timing` lines. The budget consts carry a recalibration note.
6. **UI latency markers are log proxies:** "pill visible" is measured at the `SessionViewModel.Stage` setter (UI thread), not at pixels-on-screen; "pill hidden" adds the fixed 600 ms `_hideTimer` delay. Documented in the log-line comments.

## File Structure

| File | Task | Responsibility |
|---|---|---|
| `src/Winpepper.Audio/SilenceTrimmer.cs` (modify) | 1 | Minimum-voiced-duration gate + `VoicedMs`/`ClearVoicedMs`/`MaxFrameRms` on `TrimResult` |
| `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (modify) | 1 | 11 new gate/characterization tests |
| `src/Winpepper.Asr/InteriorSilenceSkipper.cs` (modify) | 1 | Refresh 3 stale `SilenceTrimmer.cs:NN` cross-reference comments |
| `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` (modify) | 2 | DELIBERATE DIVERGENCE doc on `LeadingSilenceRmsFloor` |
| `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (modify) | 2 | 2 boundary tests pinning the 0.002 latch |
| `src/Winpepper.Core/ViewModels/PillTimerPolicy.cs` (create) | 3 | Pure keep-alive-vs-animation table per pill stage |
| `tests/Winpepper.Core.Tests/ViewModels/PillTimerPolicyTests.cs` (create) | 3 | Stage table + 2 cross-mapper invariants |
| `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` (modify) | 4 | Keep tick alive in PendingPaste/Error; follow foreground monitor |
| `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` (modify) | 5 | `Execute` returns `GuardedRunResult` (outcome + chunks sent) |
| `src/Winpepper.Platform/Injection/InjectionRunReport.cs` (create) | 5 | Detailed injection run report record |
| `src/Winpepper.Platform/Injection/TextInjector.cs` (modify) | 5 | `TryInjectGuardedDetailed`; `TryInjectGuarded` becomes a wrapper |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` (modify) | 5 | 3 report tests |
| `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs` (modify) | 5 | Mechanical `.Outcome` migration at ~9 assertion sites |
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` (create) | 6 | Per-dictation timing accumulator, formatter, budget classifier |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` (create) | 6 | 9 format/budget tests |
| `src/Winpepper.Core/ViewModels/SessionViewModel.cs` (modify) | 7 | Optional `ILogger`; `pill stage {From} -> {To}` on stage change |
| `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelLoggingTests.cs` (create) | 7 | Stage-transition logging tests |
| `src/Winpepper.App/Hosting/AppShell.cs` (modify) | 7 | Pass a logger into `SessionViewModel` |
| `src/Winpepper.App/Hosting/PipelineHost.cs` (modify) | 8 | Timing summary in both hotkey arms, budget WRNs, hotkey lag, cancel log, enriched drop/pending logs |

Task dependency graph: `1 → 8`, `3 → 4`, `5 → 8`, `6 → 8`; Tasks 2 and 7 are independent; Task 9 last.

---

### Task 1: SilenceTrimmer minimum-voiced-duration gate

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs`
- Modify: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`
- Modify: `src/Winpepper.Asr/InteriorSilenceSkipper.cs` (comment-only)

**Interfaces:**
- Consumes: existing `SilenceTrimmer.Trim(ReadOnlySpan<float>) -> TrimResult` and its per-frame classification (`threshold = min(max(3*noiseFloor, 0.002), 0.15*speechLevel)`; a frame is voiced iff `rms[f] >= threshold`).
- Produces: `TrimResult` gains three `required` init props — `int VoicedMs`, `int ClearVoicedMs`, `double MaxFrameRms`. Task 8 reads all three in the drop log line. `Trim`'s signature and all existing behavior for kept recordings are unchanged. The only production constructor of `TrimResult` is `SilenceTrimmer` itself (3 sites in this file); the bench harness `scripts/asr-latency-bench/Program.cs:464` only *reads* results and keeps compiling.

**Background (verified root cause):** `IsSilent` today is `P90(20 ms-frame RMS) < 0.004` — purely proportional, so any transient occupying > 10 % of frames (cough, mic bump, keyboard clatter) unlocks the entire recording. Confirmed near-miss (validated against the archived WAV and `winpepper-20260728.log`): an 8.95 s silent recording with a ~450 ms transient at −36..−45 dBFS, dropped only because the transient occupied ~6.3 % of frames (28/447 ≥ 0.004) — under the > 10 % the proportional gate needs; the same transient in a capture under ~4.5 s flips the verdict. The fix adds an absolute gate as an ADDITIONAL condition (AND semantics; do not re-derive the P90 parameters), counted off the existing per-frame classification: require ≥ 600 ms of voiced frames, with a clear-speech escape hatch (≥ 100 ms of frames at ≥ 0.02 RMS). Constants were MEASURED against all 100 archived recordings in a prior validation cycle. Note on "evaluated on the KEPT (post-trim) audio": under the trim walk every voiced frame is always kept (only `isSilence` frames are ever dropped), so counting voiced frames off the input classification IS the count on the kept audio — the implementation comment states this equivalence.

- [ ] **Step 1: Write the failing tests**

Append these 11 tests to `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (inside the existing `SilenceTrimmerTests` class; reuse the existing `Const(int samples, float amp)` and `Concat(params float[][] parts)` helpers — `Const` fills constant DC so `Rms == |amp|` exactly). Add these two duration-based wrappers next to the existing helpers first (all segment durations are multiples of 20 ms at 16 kHz):

```csharp
    // Duration-based wrappers over Const/Concat: Dc(0.015, 460) is exactly
    // 460 ms (23 frames) of audio whose every 20 ms frame has RMS 0.015.
    private static float[] Dc(double rms, int ms) => Const(Rate * ms / 1000, (float)rms);

    private static float[] Join(params float[][] parts) => Concat(parts);
```

```csharp
    [Fact]
    public void Trim_BriefQuietTransient_ShortRecording_IsSilent()
    {
        // THE bug (2026-07-28 near-miss class): a ~460 ms transient at 0.015
        // RMS (-36.5 dBFS -- cough/mic-bump loudness, below clear speech) in
        // a 2 s otherwise-silent capture. 23 of 100 frames exceed 0.004, so
        // the proportional P90 gate alone says "speech" and the whole silent
        // recording would be transcribed. The absolute voiced-duration gate
        // must drop it: 460 ms voiced < 600 ms, and no frame reaches the
        // 0.02 clear-speech tier.
        var buf = Join(Dc(0.001, 760), Dc(0.015, 460), Dc(0.001, 780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.Trimmed.Length.ShouldBe(0);
        result.RemovedMs.ShouldBe(0);
        result.RunsTrimmed.ShouldBe(0);
        result.VoicedMs.ShouldBe(460);
        result.ClearVoicedMs.ShouldBe(0);
    }

    [Fact]
    public void Trim_ModerateVoiced_JustUnderFloor_IsSilent()
    {
        // Boundary pin: 580 ms of quiet voiced audio (0.01 RMS, below the
        // 0.02 clear tier) is under the 600 ms floor -> silent.
        var buf = Join(Dc(0.001, 720), Dc(0.01, 580), Dc(0.001, 700));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_ModerateVoiced_AtDurationFloor_IsKept()
    {
        // Boundary pin: exactly 600 ms of quiet voiced audio (0.01 RMS)
        // meets the floor -> kept. Protects soft-spoken dictation.
        var buf = Join(Dc(0.001, 700), Dc(0.01, 600), Dc(0.001, 700));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_ShortLoudUtterance_IsKept()
    {
        // The must-not-eat-loud-speech guard: a 300 ms one-word utterance
        // ("yes") at clear dictation loudness (0.05 RMS) passes via the
        // clear-speech tier (300 ms >= 100 ms at >= 0.02) even though it is
        // under the 600 ms voiced floor. Passes both before and after the fix.
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_VoicedMs_IsReportedOnKeptAudio()
    {
        // Observability field: 300 ms of 0.05 speech classifies as exactly
        // 15 voiced frames (adaptive threshold = max(3*0.001, 0.002) = 0.003,
        // capped at 0.15*0.05 = 0.0075 -> 0.003; room tone 0.001 is silent).
        var buf = Join(Dc(0.001, 840), Dc(0.05, 300), Dc(0.001, 860));

        var result = SilenceTrimmer.Trim(buf);

        result.VoicedMs.ShouldBe(300);
        result.ClearVoicedMs.ShouldBe(300);
        result.MaxFrameRms.ShouldBe(0.05, 0.0005);
    }

    [Fact]
    public void Trim_ClearSpeech_AtClearTierFloor_IsKept()
    {
        // Boundary pin for the clear tier: exactly 100 ms at 0.05 RMS in a
        // short 700 ms capture. P90 = 0.05 (5/35 loud frames, idx 30 lands on
        // the loud block), threshold = 0.003, voiced = 100 < 600,
        // clear = 100 >= 100 -> kept ONLY via the clear tier. The archived
        // "Great." utterance passes at exactly this boundary -- do not raise
        // the tier without new archive data.
        var buf = Join(Dc(0.001, 300), Dc(0.05, 100), Dc(0.001, 300));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
        result.ClearVoicedMs.ShouldBe(100);
    }

    [Fact]
    public void Trim_ClearSpeech_JustUnderClearTier_IsSilent()
    {
        // The boundary's one-frame-under twin: 80 ms at 0.05 inside
        // quiet-voiced padding. P90 = 0.01, threshold = 0.0015 (the
        // 0.15*speechLevel cap binds), voiced = 460 < 600,
        // clear = 80 < 100 -> silent.
        var buf = Join(Dc(0.001, 700), Dc(0.01, 380), Dc(0.05, 80), Dc(0.001, 740));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
    }

    [Fact]
    public void Trim_QuietShortUtterance_IsDropped_KnownResidual()
    {
        // Characterization of the MEASURED residual: the two archived
        // "Thank you." dictations (voiced 240/260 ms, max frame RMS
        // 0.013-0.017) sit inside the transient level band and are now
        // dropped. Encoded here as 260 ms @ 0.015 in a 2 s capture:
        // P90 = 0.015 passes (13/100 frames), threshold = 0.00225,
        // voiced = 260 < 600, clear = 0 -> silent via the NEW gate.
        // Non-destructive (archived). Any future change to this verdict must
        // be a visible decision backed by new archive measurements.
        var buf = Join(Dc(0.001, 860), Dc(0.015, 260), Dc(0.001, 880));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(260);
    }

    [Fact]
    public void Trim_SustainedQuietTransient_IsKept_KnownResidual()
    {
        // Characterization of the ACCEPTED residual: a sustained quiet
        // transient >= 600 ms (e.g. a 900 ms door rumble at 0.015 RMS)
        // passes the voiced-duration floor -- an energy detector cannot
        // distinguish it from quiet speech. 45 of 150 frames at 0.015 lift
        // P90 to 0.015 (passes) and voiced = 900 >= 600 -> kept. Mitigation:
        // downstream ASR/cleanup handles garbage; the recording is archived.
        var buf = Join(Dc(0.001, 1000), Dc(0.015, 900), Dc(0.001, 1100));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeFalse();
    }

    [Fact]
    public void Trim_BriefQuietTransient_LongRecording_IsSilent()
    {
        // Characterization: the same 460 ms transient in a 10 s capture is
        // only ~4.6% of frames, so the P90 gate already drops it today. The
        // new gate must not change that. On this P90-silent path VoicedMs
        // reports 0 (the adaptive threshold is undefined without a speech
        // level) but the absolute fields stay meaningful for recalibration.
        var buf = Join(Dc(0.001, 4760), Dc(0.015, 460), Dc(0.001, 4780));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.VoicedMs.ShouldBe(0);
        result.ClearVoicedMs.ShouldBe(0);
        result.MaxFrameRms.ShouldBe(0.015, 0.0005);
    }

    [Fact]
    public void Trim_SparseSpeechBurst_LongRecording_IsSilent_KnownResidual()
    {
        // Characterization of the PRE-EXISTING accepted residual (the
        // 2026-07-24 silence-trimming plan asked for this pin and it was
        // never written): a real 300 ms burst in a 10 s mostly-silent
        // recording lands P90 on the room tone, so the P90 gate fires FIRST
        // and the recording is dropped -- recoverable from the archive.
        // ClearVoicedMs = 300 >= 100 here, pinning the AND semantics: the
        // clear tier is an escape hatch WITHIN the new gate, it never
        // overrides a P90-silent verdict. Any future change to this verdict
        // must be a visible decision.
        var buf = Join(Dc(0.001, 4840), Dc(0.05, 300), Dc(0.001, 4860));

        var result = SilenceTrimmer.Trim(buf);

        result.IsSilent.ShouldBeTrue();
        result.ClearVoicedMs.ShouldBe(300);
    }
```

- [ ] **Step 2: Run the suite to verify the new tests fail**

Run from the worktree root: `./scripts/linux-tests.sh`

Expected: the build of `Winpepper.Audio.Tests` FAILS to compile (CS0117: `TrimResult` has no `VoicedMs`/`ClearVoicedMs`/`MaxFrameRms`). That compile failure is the red state. (If you want to see behavioral red instead: temporarily comment out only the new-field assertions and re-run — exactly these fail as not-silent: `Trim_BriefQuietTransient_ShortRecording_IsSilent`, `Trim_ModerateVoiced_JustUnderFloor_IsSilent`, `Trim_ClearSpeech_JustUnderClearTier_IsSilent`, `Trim_QuietShortUtterance_IsDropped_KnownResidual`. The other new tests pass before and after — they are guards/characterizations. Restore the assertions before Step 3.)

- [ ] **Step 3: Implement the gate**

In `src/Winpepper.Audio/SilenceTrimmer.cs`:

(a) Add three fields to `TrimResult` (after the existing `IsSilent` property):

```csharp
    /// <summary>
    /// Milliseconds of voiced (above-adaptive-threshold) audio detected.
    /// 0 when the P90 gate fired (the adaptive threshold is derived from a
    /// speech level that does not exist there) and for sub-frame buffers.
    /// Observability only -- lets the drop log say WHY.
    /// </summary>
    public required int VoicedMs { get; init; }

    /// <summary>
    /// Milliseconds of frames at or above ClearSpeechRmsFloor (0.02).
    /// Absolute, so it is reported on BOTH silent paths (0 only for
    /// sub-frame buffers). Together with MaxFrameRms this makes the gate
    /// constants recalibratable from production logs (they were measured
    /// from one 100-recording archive, 2026-07-28, and are provisional).
    /// </summary>
    public required int ClearVoicedMs { get; init; }

    /// <summary>
    /// Loudest 20 ms frame RMS observed (0 for sub-frame buffers).
    /// Observability only -- a dropped short utterance is diagnosable from
    /// the log by how close it came to the 0.02 clear tier.
    /// </summary>
    public required double MaxFrameRms { get; init; }
```

(b) Add three constants after `SilentSpeechLevel`:

```csharp
    /// <summary>
    /// Minimum total duration of voiced (above-adaptive-threshold) audio a
    /// recording must contain to count as speech. The P90 gate above is
    /// PROPORTIONAL (needs >10% of frames loud), so a brief non-speech
    /// transient (cough, mic bump, keyboard clatter) in a SHORT recording
    /// can unlock the whole buffer -- confirmed near-miss 2026-07-28
    /// (~450 ms transient at -36..-45 dBFS in an 8.95 s silent recording).
    /// This is an absolute backstop. 600 ms exceeds the confirmed transient
    /// class; real speech shorter than this passes via the clear-speech
    /// tier below. Drops remain non-destructive (original audio archived).
    /// </summary>
    private const int MinVoicedDurationMs = 600;

    /// <summary>
    /// Frames at or above this RMS (~-34 dBFS) are "clearly speech-loud".
    /// MEASURED (2026-07-28, 100-recording archive): every archived
    /// non-speech file has at most ONE 20 ms frame at or above 0.02, while
    /// loud short utterances reach it -- but 17% of real dictations never
    /// do, so this tier is a loud-short-utterance escape hatch, NOT a
    /// speech test. Known residual: quiet short utterances (max frame RMS
    /// 0.013-0.017, e.g. the two archived "Thank you."s) sit inside the
    /// transient level band and are dropped -- see
    /// Trim_QuietShortUtterance_IsDropped_KnownResidual.
    /// </summary>
    private const double ClearSpeechRmsFloor = 0.02;

    /// <summary>
    /// Clear-speech-loud audio needed to bypass the duration floor. 100 ms
    /// = 5 frames: the measured worst non-speech file shows 1 frame at or
    /// above 0.02 (5x margin), while the archived loud short utterance
    /// "Great." has EXACTLY 100 ms of clear audio and 9/93 real dictations
    /// sit in the [100, 200) ms clear band -- do not raise this without new
    /// archive measurements (provisional constants; the drop log's
    /// voiced/clear/max-RMS fields exist for recalibration).
    /// </summary>
    private const int MinClearVoicedDurationMs = 100;
```

(c) Update the sub-frame early return (`frameCount == 0`) to add `VoicedMs = 0, ClearVoicedMs = 0, MaxFrameRms = 0,` to its `TrimResult` initializer.

(d) Replace the P90 early return (`if (speechLevel < SilentSpeechLevel) { ... }`) with:

```csharp
        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is derived
            // from a speech level that does not exist), so VoicedMs reports
            // 0. Clear/max fields are absolute and still meaningful -- they
            // keep long-recording transient near-misses diagnosable from the
            // drop log (the gate constants below are recalibrated from
            // these fields).
            var clearMsAtP90 = 0;
            for (var f = 0; f < frameCount; f++)
                if (rms[f] >= ClearSpeechRmsFloor) clearMsAtP90 += FrameMs;
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = clearMsAtP90,
                MaxFrameRms = sorted[^1],
            };
        }
```

(e) Insert the new gate immediately AFTER the `isSilence[]` classification loop (`isSilence[f] = rms[f] < threshold;`) and BEFORE the trim walk:

```csharp
        // Minimum-voiced-duration gate (2026-07-28 transient-rejection fix;
        // AND semantics -- the owner-fixed P90 parameters above are not
        // re-derived, and this gate can only make the verdict MORE silent).
        // Voiced frames are never trimmed (only isSilence frames are), so
        // "voiced in the kept post-trim audio" == "voiced in the input" and
        // we can count directly off isSilence[]/rms[] without re-analyzing
        // the output buffer.
        var voicedMs = 0;
        var clearVoicedMs = 0;
        var maxFrameRms = 0.0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] > maxFrameRms) maxFrameRms = rms[f];
            if (isSilence[f]) continue;
            voicedMs += FrameMs;
            if (rms[f] >= ClearSpeechRmsFloor) clearVoicedMs += FrameMs;
        }

        if (voicedMs < MinVoicedDurationMs && clearVoicedMs < MinClearVoicedDurationMs)
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
            };
        }
```

(f) Add `VoicedMs = voicedMs, ClearVoicedMs = clearVoicedMs, MaxFrameRms = maxFrameRms,` to the final (kept) `TrimResult` initializer at the end of `Trim`.

- [ ] **Step 4: Refresh the stale cross-references in InteriorSilenceSkipper**

`src/Winpepper.Asr/InteriorSilenceSkipper.cs` cites `SilenceTrimmer.cs` by line range in three comments (at approximately `:154` — "`SilenceTrimmer.cs:96-101`" — plus two more at `:184` and `:186`). Step 3 shifts those lines. Open the file, find the three `SilenceTrimmer.cs:` citations, and update each cited range to the new line numbers of the same code (the adaptive-threshold block, and the two trim-walk blocks they point at). Comment-only change.

- [ ] **Step 5: Run the suite to verify everything passes**

Run: `./scripts/linux-tests.sh`

Expected: exit 0, all projects green — the 12 pre-existing `SilenceTrimmerTests` pass unchanged, plus the 11 new tests.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs src/Winpepper.Asr/InteriorSilenceSkipper.cs
git commit -m "$(cat <<'EOF'
feat(audio): minimum-voiced-duration silence gate (transient rejection)

The P90 presence gate is proportional, so a brief non-speech transient
(cough, mic bump) occupying >10% of frames unlocked whole silent
recordings in short captures. Add an absolute backstop: require 600 ms
of voiced audio, with a 100 ms clear-speech (>=0.02 RMS) escape hatch.
Constants measured against all 100 archived recordings: every archived
non-speech file shows <=1 frame >=0.02 (5x margin at 100 ms) while the
archived loud short utterance passes at exactly 100 ms. Measured
residuals, pinned by tests: 2/93 archived real dictations (quiet short
utterances, max frame RMS 0.013-0.017) are now dropped -- their level
band overlaps the transient class; and a sustained quiet transient
>=600 ms still passes -- energy detectors cannot tell it from quiet
speech. TrimResult gains VoicedMs, ClearVoicedMs, and MaxFrameRms so
the drop log says why and the provisional constants can be
recalibrated from production logs. Adds the sparse-speech
characterization pin the 2026-07-24 plan asked for.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: Streaming silence gate — document the deliberate divergence + pin the latch boundary

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` (comment-only)
- Modify: `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs`

**Interfaces:**
- Consumes: `ParakeetStreamingSession.PushAsync(ReadOnlyMemory<float>, CancellationToken)`, the `private const double LeadingSilenceRmsFloor = 0.002;` latch (`ParakeetStreamingSession.cs:69`), and the existing test helpers in `ParakeetStreamingSessionTests` — `private const int Hop = 160;`, `NewSession(FakeParakeetBackend backend, ..., int chunk = 50, int context = 20)`, and `FakeParakeetBackend.EncodeMelFrameCounts` (a `List<int>` of encode calls).
- Produces: nothing new — no behavior change. This task exists because the spec requires the batch/streaming divergence to be documented as deliberate (the `InteriorSilenceSkipper` precedent), NOT unified: `LeadingSilenceRmsFloor` is a start-of-speech FEED gate with no drop authority, not an `IsSilent` analogue.

- [ ] **Step 1: Write the two failing-to-exist boundary tests**

Add to `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (the existing test `LeadingSilence_IsGated_NotFedToTheEncoder` pins zeros-vs-speech; these pin the 0.002 constant at its boundary):

```csharp
    [Fact]
    public async Task LeadingSilence_JustBelowFloor_StaysGated()
    {
        // Pins the 0.002 LeadingSilenceRmsFloor constant at its boundary
        // (previously tested only with exact zeros vs speech-level audio).
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        var below = new float[Hop * 60];
        Array.Fill(below, 0.0019f); // DC: whole-buffer RMS = 0.0019 < 0.002
        await session.PushAsync(below, TestContext.Current.CancellationToken);
        await session.PushAsync(below, TestContext.Current.CancellationToken);

        backend.EncodeMelFrameCounts.Count.ShouldBe(0);
    }

    [Fact]
    public async Task LeadingSilence_JustAboveFloor_Unlatches()
    {
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        var above = new float[Hop * 60];
        Array.Fill(above, 0.0021f); // DC: whole-buffer RMS = 0.0021 >= 0.002
        await session.PushAsync(above, TestContext.Current.CancellationToken);
        await session.PushAsync(above, TestContext.Current.CancellationToken);

        // Unlatched: mel frames reach the extractor -> at least one chunk
        // encode. Post-latch 0.0021-level frames classify as SPEECH in the
        // InteriorSilenceSkipper (its per-frame floor is the same 0.002, and
        // the >=floor branch bypasses quiet-recording suppression entirely),
        // so audio flows to the encoder. Assert the latch opened, not the
        // chunking arithmetic.
        backend.EncodeMelFrameCounts.Count.ShouldBeGreaterThanOrEqualTo(1);
    }
```

Note: match the existing file's `using` set and any session-construction details of `NewSession` (its optional `fallback` parameter can be left defaulted). Validation (2026-07-28) traced the private `Rms` (whole-buffer sqrt(Σs²/N), so a DC fill's RMS equals its amplitude to within float noise) and the skipper's `Classify` — frames at ≥ 0.002 take the speech branch, never quiet-suppression — so both tests are expected to pass as written. Safety valve kept: if `LeadingSilence_JustAboveFloor_Unlatches` nonetheless fails with 0 encodes, raise the fill value until it clears the skipper's per-frame floor with margin (e.g. `0.003f`) while keeping `JustBelowFloor` at `0.0019f`; the latch boundary being pinned is the LEADING gate, and the below-floor test carries that pin.

- [ ] **Step 2: Run the suite**

Run: `./scripts/linux-tests.sh`

Expected: exit 0 — these tests should PASS immediately (they pin existing behavior; this is characterization, not TDD-red). If either fails, the latch behaves differently than diagnosed — STOP and investigate before documenting.

- [ ] **Step 3: Add the divergence documentation**

In `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs`, append this paragraph to the END of the existing XML doc comment on `LeadingSilenceRmsFloor` (keep the existing text; this extends it):

```csharp
    /// DELIBERATE DIVERGENCE from the batch SilenceTrimmer (2026-07-28,
    /// pill-silence-observability): this latch is a START-OF-SPEECH feed
    /// gate, not a speech-presence verdict. It has NO drop authority -- the
    /// dictation drop decision is made exclusively by the batch
    /// SilenceTrimmer.Trim IsSilent verdict (which now also enforces a
    /// minimum-voiced-duration), and on a silent verdict the streaming
    /// session is disposed unused. Consequences accepted on purpose:
    /// (a) granularity differs (whole pushed buffer here vs 20 ms frames in
    /// batch), so a short transient inside a large buffer may or may not
    /// unlatch depending on buffer sizing; (b) a transient can permanently
    /// unlatch the stream -- that costs only encoder work, never words,
    /// because the batch verdict still governs. A minimum-voiced-duration
    /// has no natural counterpart in a one-shot start latch; do not unify.
    /// Mirrors the documentation precedent in InteriorSilenceSkipper.
```

- [ ] **Step 4: Run the suite again**

Run: `./scripts/linux-tests.sh` — Expected: exit 0, all green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs
git commit -m "$(cat <<'EOF'
test(asr): pin LeadingSilenceRmsFloor boundary; document batch/streaming divergence

The streaming 0.002 latch is a start-of-speech FEED gate with no drop
authority -- not an IsSilent analogue -- so the new batch
minimum-voiced-duration gate is deliberately NOT mirrored there. Two
boundary tests pin the 0.002 constant (previously covered only by
zeros-vs-speech), and the constant's doc now records the divergence
following the InteriorSilenceSkipper precedent.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: PillTimerPolicy — pure keep-alive-vs-animation table

**Files:**
- Create: `src/Winpepper.Core/ViewModels/PillTimerPolicy.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/PillTimerPolicyTests.cs`

**Interfaces:**
- Consumes: `Winpepper.Core.ViewModels.SessionStage` (7 values: `Idle, Recording, Transcribing, CleaningUp, Injecting, PendingPaste, Error`) and `PillAnimationMap.ForStage(SessionStage) -> PillAnimationMode` (unchanged; `PendingPaste`/`Idle`/`Error` map to `None`).
- Produces: `public static PillTimerPlan PillTimerPolicy.ForStage(SessionStage stage)` where `public readonly record struct PillTimerPlan(bool KeepAliveRunning, bool AnimationRunning)`. Task 4 consumes both members.

**Why:** The diagnosed bug is that the pill's 100 ms tick timer is BOTH the animation driver AND the only periodic `AssertTopmost` caller. `PendingPaste`/`Error` stop the timer to suppress the pulse and thereby lose the z-order keep-alive while the pill stays visible indefinitely. This helper pins the invariant "the housekeeping tick runs whenever the pill is on screen" on Linux, and pins that "no thinking pulse while pending" is guaranteed by `PillAnimationMap` independently.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/PillTimerPolicyTests.cs`:

```csharp
using System;
using Shouldly;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class PillTimerPolicyTests
{
    [Theory]
    [InlineData(SessionStage.Idle, false, false)]
    [InlineData(SessionStage.Recording, true, true)]
    [InlineData(SessionStage.Transcribing, true, true)]
    [InlineData(SessionStage.CleaningUp, true, true)]
    [InlineData(SessionStage.Injecting, true, true)]
    // THE fix: PendingPaste persists indefinitely across window switches --
    // the z-order keep-alive must run; the thinking pulse must not.
    [InlineData(SessionStage.PendingPaste, true, false)]
    // Same latent defect, narrower window (6-10 s error holds).
    [InlineData(SessionStage.Error, true, false)]
    public void ForStage_MapsEveryStage(SessionStage stage, bool keepAlive, bool animation)
    {
        var plan = PillTimerPolicy.ForStage(stage);
        plan.KeepAliveRunning.ShouldBe(keepAlive);
        plan.AnimationRunning.ShouldBe(animation);
    }

    [Fact]
    public void AnimationRunning_AgreesWithPillAnimationMap_ForEveryStage()
    {
        // The "no pulse while pending" guarantee must be a pinned agreement
        // between the two mappers, not an emergent accident of two files.
        foreach (var stage in Enum.GetValues<SessionStage>())
        {
            PillTimerPolicy.ForStage(stage).AnimationRunning
                .ShouldBe(PillAnimationMap.ForStage(stage) != PillAnimationMode.None,
                    $"stage {stage}");
        }
    }

    [Fact]
    public void KeepAlive_RunsForEveryOnScreenStage()
    {
        // The pill is on screen for every stage except Idle; the periodic
        // AssertTopmost keep-alive must cover all of them. A newly added
        // stage forces a deliberate decision here.
        foreach (var stage in Enum.GetValues<SessionStage>())
        {
            PillTimerPolicy.ForStage(stage).KeepAliveRunning
                .ShouldBe(stage != SessionStage.Idle, $"stage {stage}");
        }
    }
}
```

- [ ] **Step 2: Run the suite to verify it fails**

Run: `./scripts/linux-tests.sh`

Expected: `Winpepper.Core.Tests` FAILS to compile (CS0246: `PillTimerPolicy` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Core/ViewModels/PillTimerPolicy.cs`:

```csharp
namespace Winpepper.Core.ViewModels;

/// <summary>
/// Which of the status pill's two periodic jobs run in each stage.
/// KeepAlive = the periodic z-order re-assertion (AssertTopmost) and
/// monitor-follow reposition; it must run whenever the pill is on screen,
/// INCLUDING PendingPaste (which persists indefinitely across window
/// switches -- the 2026-07-28 buried-pill fix) and Error. Animation = the
/// 100 ms pulse/meter rendering; it runs only for stages whose
/// PillAnimationMap mode is not None, so PendingPaste shows no thinking
/// pulse (pinned against PillAnimationMap by PillTimerPolicyTests).
/// </summary>
public readonly record struct PillTimerPlan(bool KeepAliveRunning, bool AnimationRunning);

public static class PillTimerPolicy
{
    public static PillTimerPlan ForStage(SessionStage stage) => stage switch
    {
        SessionStage.Idle => new(KeepAliveRunning: false, AnimationRunning: false),
        SessionStage.Recording => new(true, true),
        SessionStage.Transcribing => new(true, true),
        SessionStage.CleaningUp => new(true, true),
        SessionStage.Injecting => new(true, true),
        SessionStage.PendingPaste => new(true, false),
        SessionStage.Error => new(true, false),
        // Unknown/new stage: safe default -- stay on top, no animation. The
        // invariant tests force a deliberate mapping when a stage is added.
        _ => new(true, false),
    };
}
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `./scripts/linux-tests.sh` — Expected: exit 0, all green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/PillTimerPolicy.cs tests/Winpepper.Core.Tests/ViewModels/PillTimerPolicyTests.cs
git commit -m "$(cat <<'EOF'
feat(core): PillTimerPolicy -- keep-alive vs animation per pill stage

Pure, Linux-tested table for the status pill's two periodic jobs:
z-order keep-alive runs for every on-screen stage (incl. PendingPaste
and Error -- the buried-pill fix), animation only where PillAnimationMap
is not None. Invariant tests pin both mappers together.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: StatusPillWindow — keep-alive tick in PendingPaste/Error + follow the foreground monitor

**Files:**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` (whole file is `#if WINDOWS` WinUI code-behind — compile-verified by the Windows gate; no unit test is possible: no test project references `Winpepper.App`)

**Interfaces:**
- Consumes: `PillTimerPolicy.ForStage(stage).KeepAliveRunning / .AnimationRunning` (Task 3); existing `ExtendedWindowStyle.AssertTopmost(IntPtr)`, `Native.ForegroundWindow.GetForegroundWindow()`, WinRT `DisplayArea.GetFromWindowId(..., DisplayAreaFallback.Nearest)`, and `PositionBottomCenter(AppWindow)`.
- Produces: no new API. Behavior: the 100 ms tick keeps running for every visible stage; the pill re-anchors to the foreground window's monitor at ~1 s cadence only when the work area actually changed.

**Current defect sites (verify anchors in the current file first):** `OnVmChanged` (lines 136–203) has four arms; `PendingPaste` (147–159) and `Error` (169–180) call `_tickTimer.Stop()` while setting `_visible = true`; the tick handler (lines 83–93) is the ONLY periodic `AssertTopmost` caller; `PositionBottomCenter` (325–345) runs once per stage entry. `ApplyAnimationFrame()`'s `default: // None` arm already makes a running tick harmless for `None` stages (idempotent constant writes — no pulse).

- [ ] **Step 1: Add the new fields**

Next to the existing `private bool _visible;` field, add:

```csharp
    private int _keepAliveTick;
    private Windows.Graphics.RectInt32 _lastPositionedWorkArea;
```

- [ ] **Step 2: Rewrite the tick handler**

Replace the `_tickTimer.Tick` lambda body (currently: `_vm.Tick();` / elapsed text / `if (_visible) ExtendedWindowStyle.AssertTopmost(_hwnd);` / `ApplyAnimationFrame();`) with:

```csharp
        _tickTimer.Tick += (_, _) =>
        {
            _vm.Tick();
            ElapsedText.Text = $"{_vm.ElapsedMs / 1000} s";

            // Cheap: keep us pinned to the top even if another topmost window
            // was created after our last show. Only while visible. This tick
            // now also runs during PendingPaste/Error (PillTimerPolicy), so
            // the pill survives other topmost windows appearing while it
            // waits -- the 2026-07-28 buried-pill fix.
            if (_visible)
            {
                ExtendedWindowStyle.AssertTopmost(_hwnd);
                MaybeFollowForegroundMonitor();
            }

            // Pulse/meter rendering only where the policy says so; for
            // PendingPaste/Error the None-mode frame writes constants anyway,
            // but gating here makes "no pulse while pending" explicit.
            if (_previewActive || PillTimerPolicy.ForStage(_vm.Stage).AnimationRunning)
                ApplyAnimationFrame();
        };
```

(`PillTimerPolicy` lives in `Winpepper.Core.ViewModels`, already imported by this file for `SessionStage`/`PillAnimationMap` — confirm the `using` and add it if the file uses fully-qualified names instead.)

- [ ] **Step 3: Drive the timer from the policy in OnVmChanged**

In `OnVmChanged`, immediately after `_animMode = PillAnimationMap.ForStage(_vm.Stage);` insert:

```csharp
        // Timer policy: the keep-alive tick runs whenever the pill is on
        // screen (incl. PendingPaste and Error); the pulse itself is gated
        // per-tick by PillTimerPolicy.AnimationRunning.
        if (PillTimerPolicy.ForStage(_vm.Stage).KeepAliveRunning) _tickTimer.Start();
        else _tickTimer.Stop();
```

Then DELETE the four per-branch timer calls this replaces: `_tickTimer.Stop();` in the `PendingPaste` arm (with its `// no thinking pulse while waiting` comment), `_tickTimer.Stop();` in the `Idle` arm, `_tickTimer.Stop();` in the `Error` arm, and `_tickTimer.Start();` in the final else arm. Everything else in each arm (colors, `_visible`, `ResetPillVisual`, `PositionBottomCenter`, `Show`, `AssertTopmost`, `SetClickThrough`, `_hideTimer` handling) stays exactly as-is — this is deliberately the Error branch getting the same treatment as PendingPaste.

Validated behavior note (2026-07-28, `SessionViewModel.Tick()` fully inspected): the tick body is `if (_stopwatch.IsRunning) ElapsedMs = _stopwatch.ElapsedMilliseconds;` and the stopwatch only starts on engine→Recording and stops on engine→Idle. So during PendingPaste the tick is a pure no-op (stopwatch stopped — no counter artifacts, indefinitely safe), and during an IN-FLIGHT error hold (engine still Recording) `ElapsedText` will now visibly count up through the 6–10 s hold — a truthful, accepted behavior delta vs today's frozen text; do not add a second timer or guard for it.

- [ ] **Step 4: Add the monitor-follow**

Add this method next to `PositionBottomCenter`:

```csharp
    /// <summary>
    /// Re-anchors the pill when the FOREGROUND window moved to a different
    /// monitor while the pill is on screen (PendingPaste persists across
    /// window switches, so it must follow the user's active display). Runs
    /// on the 100 ms keep-alive tick but throttled to ~1 s, and calls
    /// PositionBottomCenter (2x Move + resize -- not cheap) ONLY when the
    /// target work area actually changed, so the clickable pill is never
    /// repositioned under a user's pointer on the same monitor.
    /// </summary>
    private void MaybeFollowForegroundMonitor()
    {
        if (++_keepAliveTick % 10 != 0) return; // 100 ms tick -> ~1 s cadence
        var fgHwnd = Native.ForegroundWindow.GetForegroundWindow();
        if (fgHwnd == IntPtr.Zero) return;
        var work = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(fgHwnd), DisplayAreaFallback.Nearest).WorkArea;
        if (work.X == _lastPositionedWorkArea.X && work.Y == _lastPositionedWorkArea.Y
            && work.Width == _lastPositionedWorkArea.Width && work.Height == _lastPositionedWorkArea.Height)
        {
            return;
        }
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        PositionBottomCenter(appWindow);
    }
```

And in `PositionBottomCenter`, after `var work = display.WorkArea;` add one line so the guard has a baseline:

```csharp
        _lastPositionedWorkArea = work;
```

- [ ] **Step 5: Verify Linux suite still green, then commit**

Run: `./scripts/linux-tests.sh` — Expected: exit 0 (this task touches only `#if WINDOWS` code; the Linux suite proves nothing regressed elsewhere). Windows compile verification happens in Task 9's gate run — if you want early confidence, you may run `./scripts/windows-gate.sh` now (~10 min, retry known flakes), but it is mandatory only in Task 9.

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "$(cat <<'EOF'
feat(app): pill z-order keep-alive survives PendingPaste/Error; follow monitor changes

PendingPaste (and Error) stopped the 100 ms tick timer -- the ONLY
periodic AssertTopmost caller -- while leaving the pill visible, so
other topmost windows buried the click-to-paste pill indefinitely. Keep
the tick running per PillTimerPolicy; the thinking pulse stays off
(PillAnimationMap None + explicit gate). Re-anchor via
PositionBottomCenter only when the foreground window's work area
actually changed, at ~1 s cadence.

Known verification gap (accepted): on-device efficacy of the z-order
keep-alive and mixed-DPI monitor-follow cannot be proven by automated
gates (the Windows gate only compiles WinUI code). Residual failure
modes: an occluder that itself re-asserts topmost at <=100 ms cadence,
and higher-band (ZBID/UIAccess) occluders.

On-device smoke (post-merge): enter PendingPaste, raise another topmost
window (e.g. Task Manager always-on-top) -> pill returns to front within
~100 ms; move focus to another monitor -> pill re-anchors within ~1 s.
If the pill ever seems missing, check ALL monitors before concluding
z-order burial -- wrong-monitor placement is a distinct rival cause
(addressed by the monitor-follow, discriminated by this step).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: Injection run report — chunks sent/total + nominal pacing, observed THROUGH DeadlinePacer

**Files:**
- Create: `src/Winpepper.Platform/Injection/InjectionRunReport.cs`
- Modify: `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` (add 3 tests)
- Modify: `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs` (mechanical migration)

**Interfaces:**
- Consumes: `InjectionRunOutcome` enum (`Completed, Interrupted, SendFailed, BlockedElevated, NoForeground`); `InjectionChunker.Split(text, ChunkCodeUnits)`; `DeadlinePacer.PauseForNextChunk(int periodMs)`; `TextInjector.PeriodMsForChunk(string)`; `InterChunkPauseMs` (= 14 — DO NOT change).
- Produces (Task 8 consumes):
  - `public readonly record struct InjectionRunReport(InjectionRunOutcome Outcome, int ChunksTotal, int ChunksSent, int PacingWaitMs);`
  - `public InjectionRunReport TextInjector.TryInjectGuardedDetailed(string text)`
  - `public InjectionRunOutcome TextInjector.TryInjectGuarded(string text)` — unchanged signature/behavior (now `=> TryInjectGuardedDetailed(text).Outcome`), so the 3 existing production call sites keep compiling.
  - `GuardedInjectionRun.Execute(...)` now returns `public readonly record struct GuardedRunResult(InjectionRunOutcome Outcome, int ChunksSent)` (same parameters).

**PACING GUARDRAIL:** this task adds counters only. `DeadlinePacer` itself, `TargetFeedUnitsPerSecond`, `InterChunkPauseMs`, `PeriodMsForChunk`, and every pause actually taken are byte-identical in behavior. `PacingWaitMs` is the NOMINAL total of pause periods requested (sum of `PeriodMsForChunk` over invoked pauses); wall time stays the caller's stopwatch.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` (match the file's existing `using`s and construction style — it already builds `TextInjector` with fake seams; all constructor seams except `log` are optional named parameters):

```csharp
    [Fact]
    public void GuardedDetailed_CompletedRun_ReportsChunksAndPacing()
    {
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 42,
            sendChunk: _ => true,
            sleep: _ => { });
        var text = new string('a', 96); // 12 chunks of 8

        var report = injector.TryInjectGuardedDetailed(text);

        report.Outcome.ShouldBe(InjectionRunOutcome.Completed);
        report.ChunksTotal.ShouldBe(12);
        report.ChunksSent.ShouldBe(12);
        // 11 inter-chunk pauses of 8 code units each at the 14 ms deadline
        // period (nominal -- the DeadlinePacer nets out send time at run time).
        report.PacingWaitMs.ShouldBe(11 * TextInjector.InterChunkPauseMs);
    }

    [Fact]
    public void GuardedDetailed_InterruptedMidRun_ReportsPartialChunks()
    {
        var foreground = 42L;
        var sent = new List<string>();
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => foreground,
            sendChunk: c => { sent.Add(c); if (sent.Count == 3) foreground = 99; return true; },
            sleep: _ => { });
        var text = new string('a', 96); // 12 chunks of 8

        var report = injector.TryInjectGuardedDetailed(text);

        report.Outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        report.ChunksTotal.ShouldBe(12);
        report.ChunksSent.ShouldBe(3);
        // Paused before chunks 2, 3, and 4; the guard halted before send #4.
        report.PacingWaitMs.ShouldBe(3 * TextInjector.InterChunkPauseMs);
    }

    [Fact]
    public void GuardedDetailed_NoForeground_ReportsZeroChunks()
    {
        var injector = new TextInjector(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,
            foregroundHwnd: () => 0,
            sendChunk: _ => true,
            sleep: _ => { });

        var report = injector.TryInjectGuardedDetailed(new string('a', 16));

        report.Outcome.ShouldBe(InjectionRunOutcome.NoForeground);
        report.ChunksTotal.ShouldBe(0);
        report.ChunksSent.ShouldBe(0);
        report.PacingWaitMs.ShouldBe(0);
    }
```

If the file lacks them, add `using Microsoft.Extensions.Logging.Abstractions;` (for `NullLogger<T>`) and `using System.Collections.Generic;`. If `InterChunkPauseMs` is `internal` and not visible to the test project, check for an existing `InternalsVisibleTo` (the existing guarded tests already exercise internals-adjacent behavior); if the tests genuinely cannot see it, use the literal `14` with a comment `// TextInjector.InterChunkPauseMs`.

- [ ] **Step 2: Run to verify failure**

Run: `./scripts/linux-tests.sh`

Expected: `Winpepper.Platform.Tests` FAILS to compile (CS1061: no `TryInjectGuardedDetailed`).

- [ ] **Step 3: Implement**

(a) Create `src/Winpepper.Platform/Injection/InjectionRunReport.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Detailed outcome of one guarded injection run, for the per-dictation
/// timing summary. PacingWaitMs is the NOMINAL total of the inter-chunk
/// pause periods requested (sum of PeriodMsForChunk over invoked pauses);
/// the DeadlinePacer nets out send time at run time, and wall time is
/// measured by the caller's stopwatch. ChunksSent &lt; ChunksTotal on an
/// Interrupted/SendFailed run. ChunksTotal is 0 when the run never
/// reached chunking (NoForeground / BlockedElevated / mouse-held park).
/// </summary>
public readonly record struct InjectionRunReport(
    InjectionRunOutcome Outcome,
    int ChunksTotal,
    int ChunksSent,
    int PacingWaitMs);
```

(b) In `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`: add above the class

```csharp
/// <summary>Outcome of one guarded run plus how many chunks actually landed.</summary>
public readonly record struct GuardedRunResult(InjectionRunOutcome Outcome, int ChunksSent);
```

change `Execute`'s return type from `InjectionRunOutcome` to `GuardedRunResult` (parameters unchanged), declare `var sent = 0;` before the chunk loop, increment `sent++;` immediately after each successful `sendChunk(...)` call, and wrap every `return` value: `Completed` → `new GuardedRunResult(InjectionRunOutcome.Completed, sent)`, `Interrupted` → `new GuardedRunResult(InjectionRunOutcome.Interrupted, sent)`, `SendFailed` → `new GuardedRunResult(InjectionRunOutcome.SendFailed, sent)` (the refused chunk does not count as sent).

(c) In `src/Winpepper.Platform/Injection/TextInjector.cs`: rename the existing `public InjectionRunOutcome TryInjectGuarded(string text)` method to `public InjectionRunReport TryInjectGuardedDetailed(string text)` and adjust its returns:
- every early `return InjectionRunOutcome.X;` before chunking (the `NoForeground` park, the `BlockedElevated` park, the mouse-still-held `Interrupted` park) becomes `return new InjectionRunReport(InjectionRunOutcome.X, 0, 0, 0);`
- the chunk-run tail becomes:

```csharp
        var chunks = InjectionChunker.Split(text, ChunkCodeUnits);
        var pacer = new DeadlinePacer(InterChunkPauseMs, _sleep, _monotonicMs);
        var pausedChunks = 0;
        var nominalPacingMs = 0;
        var run = GuardedInjectionRun.Execute(
            chunks,
            hwndAtSendStart,
            _foregroundHwnd,
            _sendChunk,
            physicalInputDown: () => ModifierGuard.AnyDown(_isKeyDown)
                                     || MouseButtonGuard.AnyDown(_isKeyDown),
            pauseBetweenChunks: () =>
            {
                var periodMs = PeriodMsForChunk(chunks[pausedChunks++]);
                nominalPacingMs += periodMs;
                // DeadlinePacer path unchanged -- observe only, never alter.
                pacer.PauseForNextChunk(periodMs);
            },
            onZeroForeground: /* keep the existing lambda exactly as-is */);
        if (run.Outcome == InjectionRunOutcome.Interrupted)
            _log.LogInformation("Injection interrupted: foreground window, physical modifier, or mouse button state changed mid-paste");
        return new InjectionRunReport(run.Outcome, chunks.Count, run.ChunksSent, nominalPacingMs);
```

This is the EXISTING run-assembly block with three additions (`nominalPacingMs` accumulator, the `run.Outcome`/`run.ChunksSent` unwrap, and the report return) — keep the existing `onZeroForeground` lambda and any surrounding `HwndZeroMeter` logic byte-identical. Then add the compatibility wrapper where the old method was:

```csharp
    public InjectionRunOutcome TryInjectGuarded(string text) => TryInjectGuardedDetailed(text).Outcome;
```

(`TryInject(string)` already delegates to `TryInjectGuarded` — leave it alone.)

(d) In `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`: `Execute` now returns `GuardedRunResult`, so the ~9 assertions of the shape `outcome.ShouldBe(InjectionRunOutcome.X)` no longer compile. Update every one mechanically to `outcome.Outcome.ShouldBe(InjectionRunOutcome.X)` (identical change at all sites; do not alter what each test proves).

- [ ] **Step 4: Run to verify green**

Run: `./scripts/linux-tests.sh` — Expected: exit 0, all green (including the pre-existing `TextInjectorGuardedTests` and migrated `GuardedInjectionRunTests`).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Platform/Injection/ tests/Winpepper.Platform.Tests/Injection/
git commit -m "$(cat <<'EOF'
feat(platform): injection run report -- chunks sent/total + nominal pacing

TryInjectGuardedDetailed returns (outcome, chunksTotal, chunksSent,
nominalPacingWaitMs) for the per-dictation timing summary;
TryInjectGuarded keeps its exact signature as a wrapper.
GuardedInjectionRun.Execute now reports how many chunks landed. The
DeadlinePacer pacing path is observed through, not altered: identical
periods, identical pauses, counters only.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 6: DictationTimingSummary — per-dictation line + stage budgets (pure, Linux-tested)

**Files:**
- Create: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`

**Interfaces:**
- Consumes: nothing (pure; `System.Text` only).
- Produces (Task 8 consumes):
  - `public sealed class DictationTimingSummary` in `Winpepper.Core.Diagnostics` with `required Guid SessionId`, `required string Kind` init props; settable `string Outcome` (default `"completed"`; values `completed|pending|silent|failed|empty`); `int?` props `RecordMs, DrainMs, TrimMs, TrimRemovedMs, AsrMs, CorrectionsMs, CleanupMs, InjectMs, InjectChars, InjectChunksSent, InjectChunksTotal, InjectPacingMs, TotalMs`; `string?` props `AsrMode` (values `streaming|batch|cloud`), `AsrModel, CleanupPath, CleanupModel`.
  - `public string FormatLine()` — one deterministic key=value line; null CORE stages (`rec drain trim asr corrections cleanup inject total`) render `skip`; optional fields are omitted entirely when null; string values containing a space are double-quoted.
  - `public IReadOnlyList<StageOverrun> Overruns()` with `public readonly record struct StageOverrun(string Stage, int ActualMs, int BudgetMs)`.
  - Public budget consts: `DrainBudgetMs=500, TrimBudgetMs=200, AsrStreamingBudgetMs=2000, AsrBatchBudgetMs=8000, CorrectionsBudgetMs=150, CleanupBudgetMs=2000, InjectBudgetMs=1500, TotalBudgetMs=5000`. `Overruns()` checks `AsrMs` against the streaming budget ONLY when `AsrMode == "streaming"`; `batch`, `cloud`, and unknown/null modes all use the (generous) batch budget — a misclassified mode must fail toward silence, not WRN spam. Recording has no budget.

**Budget rationale (grounded in production-log distributions from the prior validation cycle, RE-VERIFIED against the raw logs 2026-07-28):** recording has no budget (user-controlled duration). Cleanup 2000 ms — MEASURED as a true anomaly signal over 2026-07-17→28: Llm-path n=453, p50=505 ms, p90=808 ms, exactly one overrun (4256 ms, plausibly a cold post-swap load — precisely what a WRN should catch); note the cleanup live-swap merged at base `f6d043b` AFTER this window, so re-check the distribution from week-one `dictation timing` lines. ASR is per-mode because one budget was measured to be routine noise: streaming-era gaps sit at p50 ≈ 140–180 ms / p90 ≤ 464 ms on the measured days (day-to-day variance is real — 07-26 p90 hit 1640 ms, still under budget; 2000 ms catches genuine anomalies), while healthy batch-era dictations ran p50 3.2–3.6 s / p90 6.0–7.0 s and cloud (AssemblyAI, n=30) p50 2247 ms — a flat 2000 ms budget would have flagged 84–86 % of batch dictations. `AsrBatchBudgetMs=8000` sits above the measured batch p90, so batch WRNs mean genuinely slow, and the differing budget value printed in the WRN also reveals the mode at a glance. `cloud` is a distinct MODE (truthful logging) but shares the batch budget — its 2247 ms p50 must never hit the 2000 ms streaming budget (see Task 8 Step 5 for why the mode comes from the produced model name, not from which code arm returned). The remaining five budgets are PROVISIONAL (no log evidence exists for them yet — that is exactly the gap this feature closes): Drain 500 ms — `StopSession` is a buffer copy plus streaming tee teardown, normally near-instant. Trim 200 ms — pure math over ≤ 60 s of 16 kHz floats. Corrections 150 ms — a local file load. Inject 1500 ms — a 458-char paste is ~0.8 s of nominal pacing at the current 14 ms/8-unit deadline pace (57 pauses × 14 ms ≈ 798 ms, and the DeadlinePacer nets out send time so wall ≈ nominal); 1500 ms leaves headroom, while a full release-wait prelude timeout (1500 ms) overruns — deserving a warning. Total 5000 ms — beyond that the dictation "felt slow" by definition. Recalibration note: re-derive the provisional five from the first weeks of `dictation timing` lines; they are consts precisely so a retune is a one-line diff.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

```csharp
using System;
using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class DictationTimingSummaryTests
{
    private static DictationTimingSummary Full() => new()
    {
        SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Kind = "hold",
        Outcome = "completed",
        RecordMs = 3512,
        DrainMs = 42,
        TrimMs = 8,
        TrimRemovedMs = 1200,
        AsrMs = 812,
        AsrMode = "streaming",
        AsrModel = "nemotron-streaming-en",
        CorrectionsMs = 2,
        CleanupMs = 640,
        CleanupPath = "Llm",
        CleanupModel = "qwen2.5-1.5b",
        InjectMs = 850,
        InjectChars = 458,
        InjectChunksSent = 58,
        InjectChunksTotal = 58,
        InjectPacingMs = 798,
        TotalMs = 2354,
    };

    [Fact]
    public void FormatLine_FullDictation_IsOneParseableKeyValueLine()
    {
        var line = Full().FormatLine();

        line.ShouldBe(
            "session=11111111-2222-3333-4444-555555555555 kind=hold outcome=completed"
            + " rec=3512ms drain=42ms trim=8ms trim_removed=1200ms"
            + " asr=812ms asr_mode=streaming asr_model=nemotron-streaming-en"
            + " corrections=2ms cleanup=640ms cleanup_path=Llm cleanup_model=qwen2.5-1.5b"
            + " inject=850ms inject_chars=458 inject_chunks=58/58 inject_pace=798ms"
            + " total=2354ms");
        line.ShouldNotContain("\n");
    }

    [Fact]
    public void FormatLine_SilentDrop_MarksSkippedStages()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "toggle",
            Outcome = "silent",
            RecordMs = 8950,
            DrainMs = 30,
            TrimMs = 12,
            TotalMs = 60,
        };

        var line = s.FormatLine();

        line.ShouldContain("kind=toggle");
        line.ShouldContain("outcome=silent");
        line.ShouldContain("asr=skip");
        line.ShouldContain("corrections=skip");
        line.ShouldContain("cleanup=skip");
        line.ShouldContain("inject=skip");
        // Optional extras are omitted entirely when unknown, not "skip"-ed.
        line.ShouldNotContain("trim_removed");
        line.ShouldNotContain("asr_model");
        line.ShouldNotContain("inject_chars");
        line.ShouldNotContain("inject_chunks");
    }

    [Fact]
    public void FormatLine_QuotesStringValuesContainingSpaces()
    {
        var s = Full();
        s.CleanupModel = "none (cloud, corrections-only)";

        s.FormatLine().ShouldContain("cleanup_model=\"none (cloud, corrections-only)\"");
    }

    [Fact]
    public void Overruns_AtBudget_IsEmpty()
    {
        var s = Full();
        s.DrainMs = DictationTimingSummary.DrainBudgetMs;      // 500, not over
        s.TrimMs = DictationTimingSummary.TrimBudgetMs;        // 200
        s.AsrMs = DictationTimingSummary.AsrStreamingBudgetMs; // 2000 (Full() is streaming)
        s.CorrectionsMs = DictationTimingSummary.CorrectionsBudgetMs;
        s.CleanupMs = DictationTimingSummary.CleanupBudgetMs;
        s.InjectMs = DictationTimingSummary.InjectBudgetMs;
        s.TotalMs = DictationTimingSummary.TotalBudgetMs;

        s.Overruns().ShouldBeEmpty();
    }

    [Fact]
    public void Overruns_OverBudget_NamesStageActualAndBudget()
    {
        var s = Full();
        s.AsrMs = 2001;
        s.TotalMs = 5001;

        var overruns = s.Overruns();

        overruns.ShouldBe(new[]
        {
            new StageOverrun("asr", 2001, 2000),
            new StageOverrun("total", 5001, 5000),
        });
    }

    [Fact]
    public void Overruns_BatchAsr_UsesBatchBudget()
    {
        // Budgets are per-mode: healthy batch ASR measured p50 3.2-3.6 s /
        // p90 6.0-7.0 s -- a flat 2000 ms budget would WRN on 84-86% of
        // batch dictations.
        var s = Full();
        s.AsrMode = "batch";
        s.AsrMs = 3500; // routine healthy batch -- must NOT warn

        s.Overruns().ShouldBeEmpty();

        s.AsrMs = DictationTimingSummary.AsrBatchBudgetMs + 1;

        s.Overruns().ShouldBe(new[]
        {
            new StageOverrun("asr", DictationTimingSummary.AsrBatchBudgetMs + 1,
                DictationTimingSummary.AsrBatchBudgetMs),
        });
    }

    [Fact]
    public void Overruns_CloudAsr_UsesBatchBudget()
    {
        // "cloud" is a distinct mode for truthful logging but shares the
        // batch budget: measured cloud (AssemblyAI) p50 is ~2247 ms, which
        // must NOT trip the 2000 ms streaming budget. Only an explicit
        // "streaming" mode gets the tight budget -- unknown modes fail
        // toward silence.
        var s = Full();
        s.AsrMode = "cloud";
        s.AsrMs = 2247; // routine healthy cloud -- must NOT warn

        s.Overruns().ShouldBeEmpty();

        s.AsrMs = DictationTimingSummary.AsrBatchBudgetMs + 1;

        s.Overruns().ShouldBe(new[]
        {
            new StageOverrun("asr", DictationTimingSummary.AsrBatchBudgetMs + 1,
                DictationTimingSummary.AsrBatchBudgetMs),
        });
    }

    [Fact]
    public void Overruns_SkippedStages_ProduceNoWarnings()
    {
        var s = new DictationTimingSummary
        {
            SessionId = Guid.NewGuid(),
            Kind = "hold",
            Outcome = "silent",
        };

        s.Overruns().ShouldBeEmpty();
    }

    [Fact]
    public void Overruns_RecordingHasNoBudget()
    {
        var s = Full();
        s.RecordMs = 600_000; // a 10-minute recording is the user's business

        s.Overruns().ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `./scripts/linux-tests.sh` — Expected: `Winpepper.Core.Tests` FAILS to compile (CS0246: `DictationTimingSummary` not found).

- [ ] **Step 3: Implement**

Create `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs`:

```csharp
using System.Text;

namespace Winpepper.Core.Diagnostics;

/// <summary>One stage exceeding its budget; rendered as a [WRN] log line.</summary>
public readonly record struct StageOverrun(string Stage, int ActualMs, int BudgetMs);

/// <summary>
/// Per-dictation timing accumulator + formatter. PipelineHost creates one
/// per dictation, stamps stage durations along the existing flow (Stopwatch
/// reads only -- no threads, no timers, no hot-path allocations), and emits
/// FormatLine() as ONE structured [INF] line at the end, so "where did the
/// 3 s go?" is answerable from the log alone, after the fact. Core stages
/// left null render as "skip" (the summary appears even when stages are
/// skipped); optional detail fields left null are omitted. Overruns()
/// classifies stages against fixed budgets for grep-able [WRN] lines.
/// Pure and Linux-testable by design (DictationTimingSummaryTests).
/// </summary>
public sealed class DictationTimingSummary
{
    // Stage budgets (ms). Recording has none: its duration is the user's.
    // cleanup and the two asr budgets are log-derived (production
    // distributions, re-verified against the raw logs 2026-07-28); the rest
    // are PROVISIONAL -- re-derive from the first weeks of `dictation
    // timing` lines. The cleanup live-swap merged AFTER the cleanup
    // measurement window, so recheck that distribution in week one too.
    public const int DrainBudgetMs = 500;         // provisional: buffer copy + tee teardown
    public const int TrimBudgetMs = 200;          // provisional: pure math over <=60 s of floats
    public const int AsrStreamingBudgetMs = 2000; // measured: streaming p90 <= 464 ms on measured days
                                                  // (day variance is real: 07-26 p90 = 1640 ms, still under)
    public const int AsrBatchBudgetMs = 8000;     // measured: healthy batch p50 3.2-3.6 s, p90 6-7 s;
                                                  // cloud (n=30, p50 2247 ms) shares this budget
    public const int CorrectionsBudgetMs = 150;   // provisional: local file load
    public const int CleanupBudgetMs = 2000;      // measured: Llm path n=453 p50=505 p90=808, 1 overrun
                                                  // (window 2026-07-17..28)
    public const int InjectBudgetMs = 1500;       // provisional: ~0.8 s nominal send for 458 chars at
                                                  // the 14 ms/8-unit deadline pace; a full 1500 ms
                                                  // release-wait prelude overruns -- deserving a WRN
    public const int TotalBudgetMs = 5000;        // provisional: beyond this it "felt slow" by definition

    public required Guid SessionId { get; init; }
    public required string Kind { get; init; }          // "hold" | "toggle"
    public string Outcome { get; set; } = "completed";  // completed|pending|silent|failed|empty

    public int? RecordMs { get; set; }
    public int? DrainMs { get; set; }
    public int? TrimMs { get; set; }
    public int? TrimRemovedMs { get; set; }
    public int? AsrMs { get; set; }
    public string? AsrMode { get; set; }                // "streaming" | "batch" | "cloud"
    public string? AsrModel { get; set; }
    public int? CorrectionsMs { get; set; }
    public int? CleanupMs { get; set; }
    public string? CleanupPath { get; set; }            // CleanupPath enum name or "exception"
    public string? CleanupModel { get; set; }
    public int? InjectMs { get; set; }
    public int? InjectChars { get; set; }
    public int? InjectChunksSent { get; set; }
    public int? InjectChunksTotal { get; set; }
    public int? InjectPacingMs { get; set; }
    public int? TotalMs { get; set; }                   // hotkey-release -> emit, wall clock

    public string FormatLine()
    {
        var sb = new StringBuilder(256);
        sb.Append("session=").Append(SessionId);
        sb.Append(" kind=").Append(Kind);
        sb.Append(" outcome=").Append(Outcome);
        AppendCoreMs(sb, "rec", RecordMs);
        AppendCoreMs(sb, "drain", DrainMs);
        AppendCoreMs(sb, "trim", TrimMs);
        AppendOptMs(sb, "trim_removed", TrimRemovedMs);
        AppendCoreMs(sb, "asr", AsrMs);
        AppendOptStr(sb, "asr_mode", AsrMode);
        AppendOptStr(sb, "asr_model", AsrModel);
        AppendCoreMs(sb, "corrections", CorrectionsMs);
        AppendCoreMs(sb, "cleanup", CleanupMs);
        AppendOptStr(sb, "cleanup_path", CleanupPath);
        AppendOptStr(sb, "cleanup_model", CleanupModel);
        AppendCoreMs(sb, "inject", InjectMs);
        AppendOptNum(sb, "inject_chars", InjectChars);
        if (InjectChunksSent is not null || InjectChunksTotal is not null)
            sb.Append(" inject_chunks=").Append(InjectChunksSent ?? 0).Append('/').Append(InjectChunksTotal ?? 0);
        AppendOptMs(sb, "inject_pace", InjectPacingMs);
        AppendCoreMs(sb, "total", TotalMs);
        return sb.ToString();
    }

    public IReadOnlyList<StageOverrun> Overruns()
    {
        var list = new List<StageOverrun>(2);
        Check(list, "drain", DrainMs, DrainBudgetMs);
        Check(list, "trim", TrimMs, TrimBudgetMs);
        // Per-mode asr budget (the WRN's budget figure also reveals the
        // mode). ONLY an explicit "streaming" gets the tight budget; batch,
        // cloud (measured p50 2247 ms), and unknown/null modes all use the
        // generous batch budget so a misclassification fails toward silence.
        Check(list, "asr", AsrMs, AsrMode == "streaming" ? AsrStreamingBudgetMs : AsrBatchBudgetMs);
        Check(list, "corrections", CorrectionsMs, CorrectionsBudgetMs);
        Check(list, "cleanup", CleanupMs, CleanupBudgetMs);
        Check(list, "inject", InjectMs, InjectBudgetMs);
        Check(list, "total", TotalMs, TotalBudgetMs);
        return list;
    }

    private static void Check(List<StageOverrun> list, string stage, int? actual, int budget)
    {
        if (actual is int a && a > budget) list.Add(new StageOverrun(stage, a, budget));
    }

    private static void AppendCoreMs(StringBuilder sb, string key, int? value)
    {
        sb.Append(' ').Append(key).Append('=');
        if (value is int v) sb.Append(v).Append("ms");
        else sb.Append("skip");
    }

    private static void AppendOptMs(StringBuilder sb, string key, int? value)
    {
        if (value is int v) sb.Append(' ').Append(key).Append('=').Append(v).Append("ms");
    }

    private static void AppendOptNum(StringBuilder sb, string key, int? value)
    {
        if (value is int v) sb.Append(' ').Append(key).Append('=').Append(v);
    }

    private static void AppendOptStr(StringBuilder sb, string key, string? value)
    {
        if (value is null) return;
        sb.Append(' ').Append(key).Append('=');
        if (value.Contains(' ')) sb.Append('"').Append(value).Append('"');
        else sb.Append(value);
    }
}
```

(If the project does not have implicit usings for `System`/`System.Collections.Generic`, add `using System;` and `using System.Collections.Generic;` — match the neighboring `Diagnostics` files.)

- [ ] **Step 4: Run to verify green**

Run: `./scripts/linux-tests.sh` — Expected: exit 0, all green.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "$(cat <<'EOF'
feat(core): DictationTimingSummary -- per-dictation stage line + budget overruns

Pure, Linux-tested accumulator/formatter: one parseable key=value line
per dictation (skipped stages marked, string values with spaces quoted)
plus Overruns() classification against fixed stage budgets for [WRN]
lines. Cleanup and per-mode ASR budgets are measured from production
logs (re-verified against the raw 2026-07 logs); asr_mode is three-way
(streaming|batch|cloud) with only explicit "streaming" getting the
tight 2000 ms budget; the other five budgets are labeled provisional
with a recalibration note. PipelineHost wiring lands separately.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 7: Pill stage-transition logging (UI latency markers)

**Files:**
- Modify: `src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelLoggingTests.cs` (create)
- Modify: `src/Winpepper.App/Hosting/AppShell.cs` (one line, `#if WINDOWS`)

**Interfaces:**
- Consumes: existing ctor `public SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null)` (`SessionViewModel.cs:63`); `Microsoft.Extensions.Logging` (already referenced by `Winpepper.Core.csproj`, so `Winpepper.Core.Tests` sees it transitively — no csproj change).
- Produces: ctor gains a trailing optional 4th parameter `Microsoft.Extensions.Logging.ILogger? log = null` (existing 2- and 3-arg call sites keep compiling). Every ACTUAL `Stage` value change emits `pill stage {From} -> {To}` at INF on the UI thread — the closest available log proxy for pixels-on-screen. Combined with the session-start line (which gains hotkey lag in Task 8), this makes hotkey-press → pill-visible measurable from the log; the `-> Idle` transition plus the fixed 600 ms `StatusPillWindow._hideTimer` delay makes paste-complete → pill-hidden measurable.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelLoggingTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelLoggingTests
{
    private sealed class ListLogger : ILogger
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add($"[{logLevel}] {formatter(state, exception)}");
    }

    [Fact]
    public void StageTransitions_AreLoggedAtInformation()
    {
        var engine = new SessionEngine();
        var log = new ListLogger();
        var vm = new SessionViewModel(engine, new SynchronousUiThread(), log: log); // named: skips the IDelayScheduler? delays 3rd param

        engine.Apply(SessionEvent.StartRequested);   // Idle -> Recording
        engine.Apply(SessionEvent.StopRequested);    // Recording -> Transcribing

        log.Lines.ShouldContain(l => l.Contains("pill stage") && l.Contains("Recording"));
        log.Lines.ShouldContain(l => l.Contains("pill stage") && l.Contains("Transcribing"));
        log.Lines.Where(l => l.Contains("pill stage")).ShouldAllBe(l => l.StartsWith("[Information]"));
    }

    [Fact]
    public void NullLogger_IsFine_AndExistingCtorShapeStillWorks()
    {
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());

        engine.Apply(SessionEvent.StartRequested);

        vm.Stage.ShouldBe(SessionStage.Recording);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `./scripts/linux-tests.sh` — Expected: `Winpepper.Core.Tests` FAILS to compile (CS1739: no `log` parameter on the `SessionViewModel` constructor).

- [ ] **Step 3: Implement**

In `src/Winpepper.Core/ViewModels/SessionViewModel.cs`:

(a) Add `using Microsoft.Extensions.Logging;` to the file's usings and a field next to `_engine`:

```csharp
    private readonly ILogger? _log;
```

(b) Extend the constructor (keep parameter order — new parameter LAST):

```csharp
    public SessionViewModel(SessionEngine engine, IUiThread ui, IDelayScheduler? delays = null, ILogger? log = null)
    {
        _engine = engine;
        _ui = ui;
        _delays = delays ?? new SystemDelayScheduler();
        _log = log;
        _engine.StateChanged += OnEngineStateChanged;
    }
```

(c) Replace the `Stage` setter body (currently: equal-check early return, `_stage = value`, `_presentationGeneration++`, level-meter reset, two `Raise` calls) with:

```csharp
        private set
        {
            if (_stage == value) return;
            var previous = _stage;
            _stage = value;
            _presentationGeneration++;
            if (value != SessionStage.Recording)
            {
                _levelMeter.Reset();
                InputLevel = 0;
            }
            // Observability (UI latency markers): the closest log proxy for
            // pill visible/hidden. Runs on the UI thread; actual hide adds
            // the fixed 600 ms StatusPillWindow._hideTimer delay downstream.
            // INF because minimumLevel is hard-coded to Information.
            _log?.LogInformation("pill stage {From} -> {To}", previous, value);
            Raise(nameof(Stage));
            Raise(nameof(StatusText));
        }
```

(d) In `src/Winpepper.App/Hosting/AppShell.cs`, in `Create()` (the `var sessionVm = new SessionViewModel(engine, uiThread);` line at ~:108; the logger factory local `factory` is created earlier in the same method):

```csharp
        var sessionVm = new SessionViewModel(engine, uiThread,
            log: factory.CreateLogger<SessionViewModel>());
```

- [ ] **Step 4: Run to verify green**

Run: `./scripts/linux-tests.sh` — Expected: exit 0, all green (all pre-existing `SessionViewModel*` tests pass unchanged — the new parameter is optional).

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelLoggingTests.cs src/Winpepper.App/Hosting/AppShell.cs
git commit -m "$(cat <<'EOF'
feat(core): pill stage-transition logging via optional SessionViewModel logger

Every actual Stage change now emits 'pill stage {From} -> {To}' at INF
on the UI thread -- the closest log proxy for pill visible/hidden
(hide adds the fixed 600 ms hide-timer delay). Wired from AppShell with
a real logger; ctor parameter is optional so all existing call sites
and tests are untouched. Together with the hotkey-lag session-start
line this makes hotkey-press -> pill-visible and paste-complete ->
pill-hidden measurable from the logs alone.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 8: PipelineHost wiring — one `dictation timing` line per dictation + budget WRNs

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (whole file is `#if WINDOWS`; the repo has zero PipelineHost tests by established pattern — verification is compile via the Windows gate + the Linux-tested helpers from Tasks 1/5/6 carrying all logic)

**Interfaces:**
- Consumes: `Winpepper.Core.Diagnostics.DictationTimingSummary` / `StageOverrun` (Task 6); `TextInjector.TryInjectGuardedDetailed` / `InjectionRunReport` (Task 5); `TrimResult.VoicedMs/ClearVoicedMs/MaxFrameRms` (Task 1); existing stopwatches `_recordStopwatch`, `transcribeSw`, `cleanupSw`, `injectSw` (+ `2`-suffixed toggle twins); `HotkeyEvent.Timestamp` (stamped in the hook callback); `cleanupUsedModel` (already the truthful actually-used model — the LEASE's snapshot, not the holder's current name); `CleanupResult.Path`; `_currentSessionId`.
- Produces: log lines only —
  - `dictation timing {Summary}` INF, once per dictation, in BOTH hotkey arms, for ALL terminals: completed, pending (all park reasons incl. NoForeground), silent-drop, ASR-failed, and empty-final-text.
  - `slow dictation stage {Stage}: {ActualMs} ms (budget {BudgetMs} ms), session {SessionId}` WRN per overrun.
  - `Session started (hold|toggle) {SessionId} (hotkey observed {LagMs} ms before handling)` — hotkey-observation lag.
  - `Session cancelled {SessionId}` INF (previously invisible).
  - Enriched drop line: `dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000})`.
  - Enriched pill-click success line: `Pending paste injected ({Chars} chars, {ChunksSent}/{ChunksTotal} chunks, nominal pacing {PacingMs} ms)`.

**Zero-cost discipline:** Stopwatch reads along the existing flow only — no new threads, no timers, no hot-path allocations beyond the one summary object/line per dictation. Do NOT touch `HotkeyHook.cs` (the injected-event fast path at :105–135 stays byte-identical). Existing one-off timing logs (trimmed silence, injection interrupted, retained parks, cleanup path line, model-load lines) all STAY — the summary complements them.

**CRITICAL structural fact:** `HandleHotkey` (`PipelineHost.cs:449`) writes the dictation flow TWICE, byte-parallel — the `HoldUp` arm (≈512–867) and the `Toggle`-stop arm (≈933–1282, every local suffixed `2`). Steps 2–9 below show the HOLD arm; Step 10 duplicates every change into the toggle arm with `2`-suffixed names (`timing2`, `releaseAt2`, `drainSw2`, `trimSw2`, `correctionsSw2`, `injReport2`). On the toggle side, `evt.Timestamp` of the stop-toggle event is the stop-press instant — the correct `releaseAt2`. Line anchors below are from base `f6d043b` — earlier tasks do not touch this file, but VERIFY each anchor against the current file as you edit.

- [ ] **Step 1: Add the two private helpers**

Next to `TrimForTranscription` (≈:1304), add:

```csharp
    private static int TotalSince(DateTimeOffset releaseAt)
        => (int)(DateTimeOffset.UtcNow - releaseAt).TotalMilliseconds;

    /// <summary>
    /// Emits the one-line per-dictation timing summary (INF, grep:
    /// "dictation timing") and a [WRN] per stage-budget overrun (grep:
    /// "slow dictation stage"). Complements -- never replaces -- the
    /// existing one-off timing logs (trimmed silence, injection
    /// interrupted, retained parks, ...).
    /// </summary>
    private void EmitTimingSummary(Winpepper.Core.Diagnostics.DictationTimingSummary timing)
    {
        _log.LogInformation("dictation timing {Summary}", timing.FormatLine());
        foreach (var o in timing.Overruns())
        {
            _log.LogWarning(
                "slow dictation stage {Stage}: {ActualMs} ms (budget {BudgetMs} ms), session {SessionId}",
                o.Stage, o.ActualMs, o.BudgetMs, timing.SessionId);
        }
    }
```

- [ ] **Step 2: Hotkey-observation lag on the session-start lines**

Replace the two session-start logs (`:461` hold, `:888` toggle):

```csharp
                _log.LogInformation("Session started (hold) {SessionId} (hotkey observed {LagMs} ms before handling)",
                    _currentSessionId, (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds);
```

(toggle: same with `(toggle)`). `evt.Timestamp` is stamped `DateTimeOffset.UtcNow` inside the hook callback, so this lag covers hook-thread → channel → serialized-loop latency, including waiting behind a previous dictation.

- [ ] **Step 3: Construct the summary at stop-arm entry + drain stopwatch**

In the `HoldUp` arm, after `_engine.Apply(SessionEvent.StopRequested);` / `_recordStopwatch?.Stop();` (`:514–515`) and around `var samples = _warmRecorder!.StopSession();` (`:517`):

```csharp
                var releaseAt = evt.Timestamp;
                var timing = new Winpepper.Core.Diagnostics.DictationTimingSummary
                {
                    SessionId = _currentSessionId,
                    Kind = "hold",
                };
                timing.RecordMs = (int?)_recordStopwatch?.ElapsedMilliseconds;

                var drainSw = System.Diagnostics.Stopwatch.StartNew();
                var samples = _warmRecorder!.StopSession();
                drainSw.Stop();
                timing.DrainMs = (int)drainSw.ElapsedMilliseconds;
```

(`WarnIfSessionSilent` / `_sounds.PlayStop()` lines stay between as-is.)

- [ ] **Step 4: Trim stopwatch + enriched drop log + silent-drop emission**

(a) Change `TrimForTranscription`'s signature and body (`:1304–1320`) to surface `RemovedMs` and log the Task 1 fields:

```csharp
    private float[]? TrimForTranscription(float[] samples, Guid sessionId, out int removedMs)
    {
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples);
        removedMs = result.RemovedMs;
        if (result.IsSilent)
        {
            var ms = (int)((long)samples.Length * 1000 / 16000);
            // voiced/clear/max-RMS make the provisional gate constants
            // recalibratable from logs and a dropped short utterance
            // diagnosable after the fact. Content-free: numbers only.
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000})",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms);
            return null;
        }

        if (result.RemovedMs > 0)
            _log.LogInformation(
                "trimmed silence: {Ms} ms across {Runs} runs",
                result.RemovedMs, result.RunsTrimmed);

        return result.Trimmed;
    }
```

(b) Update the hold-arm call site (`:521`) and stamp trim timings:

```csharp
                var trimSw = System.Diagnostics.Stopwatch.StartNew();
                var trimmed = TrimForTranscription(samples, _currentSessionId, out var trimRemovedMs);
                trimSw.Stop();
                timing.TrimMs = (int)trimSw.ElapsedMilliseconds;
                if (trimRemovedMs > 0) timing.TrimRemovedMs = trimRemovedMs;
```

(c) In the silent-drop branch (`if (trimmed is null) { ... }`, `:522–562`), immediately BEFORE the closing `break;`:

```csharp
                    timing.Outcome = "silent";
                    timing.TotalMs = TotalSince(releaseAt);
                    EmitTimingSummary(timing);
```

- [ ] **Step 5: ASR stamps + the ASR-failed terminal**

(a) Do NOT derive the ASR mode from which code arm returned. `maybeTranscription is not null` was validated (2026-07-28) to be a wrong signal: with `StreamingEnabled=true` (the default), the local `BatchStreamingAdapter`, the cloud websocket, the cloud REST zero-push batch, and the cloud→local fallback ALL return non-null through the streaming arm — under an arm-based derivation, routine cloud (p50 ≈ 2247 ms) and local batch (p50 3.2–3.6 s) dictations would spam the 2000 ms streaming-budget WRN, poisoning exactly the signal this feature creates. Instead, the mode comes from `producedModelName` in (c) below — every internal fallback rewrites `ProviderModelName` to the engine that ACTUALLY produced the text, so the name is outcome-truthful (verified across all five paths).

(b) Inside the ASR-unavailable early-exit (`:625–644`), immediately BEFORE the `return;` at `:643` (after the existing `_log.LogWarning("Local ASR unavailable ...")`):

```csharp
                        timing.Outcome = "failed";
                        timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;
                        timing.AsrMode = "batch";
                        timing.TotalMs = TotalSince(releaseAt);
                        EmitTimingSummary(timing);
```

(This bare-`return` terminal was previously completely invisible in the logs — no archive, no line.)

(c) After `transcribeSw.Stop();` (`:650`), where `transcription` and `producedModelName` are read:

```csharp
                timing.AsrMs = (int)transcribeSw.ElapsedMilliseconds;
                // Mode from the model that ACTUALLY produced the result (see
                // Step 5a): nemotron const => true local streaming; the
                // assemblyai/ prefix => cloud (shares the batch budget); else
                // local batch (incl. every fallback path).
                timing.AsrMode =
                    string.Equals(producedModelName, Winpepper.Asr.TranscribeCpp.NemotronStreamingModel.Name, StringComparison.OrdinalIgnoreCase) ? "streaming"
                    : CloudProvider.IsCloud(producedModelName) ? "cloud"
                    : "batch";
                timing.AsrModel = producedModelName;
```

(`NemotronStreamingModel.Name` is the existing `public const string` (`"nemotron-streaming-en"`, `NemotronStreamingModel.cs:8`); `CloudProvider.IsCloud` is the same call PipelineHost already makes at `:670`/`:1087` for `skipLlm` — reuse its exact form/qualification from those sites.)

- [ ] **Step 6: Corrections + cleanup stamps**

(a) Wrap the corrections load (`var correctionsData = _corrections?.Load() ?? CorrectionsData.Empty;`, `:695`):

```csharp
                    var correctionsSw = System.Diagnostics.Stopwatch.StartNew();
                    var correctionsData = _corrections?.Load() ?? CorrectionsData.Empty;
                    correctionsSw.Stop();
                    timing.CorrectionsMs = (int)correctionsSw.ElapsedMilliseconds;
```

(Corrections APPLICATION happens inside `CleanupRunner.RunAsync`; this stage measures the PipelineHost-side load, which is what can stall the flow here — noted so the field is honest.)

(b) After the success-path `cleanupSw.Stop();` (`:707`, right before the existing `"Cleanup path={Path}, {ElapsedMs}ms"` log):

```csharp
                    timing.CleanupMs = (int)cleanupSw.ElapsedMilliseconds;
                    timing.CleanupPath = result.Path.ToString();
```

(c) After the `cleanupUsedModel` switch assignment (`:711–716`):

```csharp
                    timing.CleanupModel = string.IsNullOrWhiteSpace(cleanupUsedModel) ? null : cleanupUsedModel;
```

(`cleanupUsedModel` is computed from the LEASE (`cleanupLease.LoadedModelName`) — the model this dictation ACTUALLY ran on, immune to a concurrent live-swap. Reuse it verbatim; do not read `_cleanupHolder.LoadedModelName` at log time.)

(d) In the cleanup `catch` block, after its `cleanupSw.Stop();` (`:722`):

```csharp
                    timing.CleanupMs = (int)cleanupSw.ElapsedMilliseconds;
                    timing.CleanupPath = "exception";
```

When cleanup never runs (empty final text / no runner), all three stay null → `cleanup=skip` in the line.

- [ ] **Step 7: Injection stamps (through the detailed report)**

(a) Replace the injector call (`var outcome = _injector.TryInjectGuarded(toType);` at `:754`) with:

```csharp
                        var injReport = _injector.TryInjectGuardedDetailed(toType);
                        var outcome = injReport.Outcome;
                        if (injReport.ChunksTotal > 0)
                        {
                            timing.InjectChunksSent = injReport.ChunksSent;
                            timing.InjectChunksTotal = injReport.ChunksTotal;
                            timing.InjectPacingMs = injReport.PacingWaitMs;
                        }
```

Every downstream branch keyed off `outcome` (HoldPending, Interrupted, BlockedElevated, NoForeground, SendFailed, `injected = outcome == ...Completed`) stays byte-identical.

(b) After `injectSw.Stop();` (`:814`):

```csharp
                timing.InjectMs = (int)injectSw.ElapsedMilliseconds;
                if (!string.IsNullOrWhiteSpace(final)) timing.InjectChars = final.Length;
                // Outcome derivation: "pending" covers every park reason
                // (HoldPending, Interrupted, BlockedElevated, NoForeground,
                // SendFailed -- all end in EnterPendingPaste). "empty" is the
                // honest bucket for the empty-final-text dictation where the
                // whole injection block was skipped: no injection ran and no
                // pending paste exists, so neither "completed" nor "pending"
                // would be true.
                timing.Outcome = injected
                    ? "completed"
                    : (string.IsNullOrWhiteSpace(final) ? "empty" : "pending");
```

Note the wall-clock caveat, worth a one-line comment at the stopwatch: `injectSw` includes `CaptureTarget()` and up to 2 × 1500 ms release-wait preludes inside `TryInjectGuardedDetailed` — `inject_pace` (nominal) vs `inject` (wall) separates pacing from prelude stalls.

- [ ] **Step 8: Emit at the normal terminal**

Immediately after `_engine.Apply(SessionEvent.InjectionCompleted);` (`:839`) and before the `totalMs`/archive block:

```csharp
                timing.TotalMs = TotalSince(releaseAt);
                EmitTimingSummary(timing);
```

(The `HistoryTimings` archive block stays exactly as-is — log and History intentionally share the same stopwatch values. Placement matters: emission sits BEFORE the `_archiver.Archive` call so an Archive throw — which escapes to the RunAsync catch — can never skip the timing line; verified 2026-07-28 that these three emission points plus the RunAsync catch are the arms' only exits.)

- [ ] **Step 9: Session cancelled line**

In the `Cancel` arm (`:868–878`), after `_engine.Apply(SessionEvent.CancelRequested);`:

```csharp
                _log.LogInformation("Session cancelled {SessionId}", _currentSessionId);
```

(A cancel gets a line, not a timing summary — there is no dictation outcome to summarize.)

- [ ] **Step 10: Duplicate Steps 3–8 into the Toggle-stop arm**

The toggle-stop block (`:933–1282`) is byte-parallel. Apply every change from Steps 3–8 with: `Kind = "toggle"`, and locals `releaseAt2`, `timing2`, `drainSw2`, `trimSw2`, `trimRemovedMs2`, `correctionsSw2`, `injReport2` (the AsrMode derivation uses `producedModelName2`). Anchor mapping (hold → toggle): 514→935, 517→938, 521→942, drop-branch break 561→981, 604→1024, 643→1063, 650→1070, 695→1112, 707→1124, 711→1128, 722→1139, 754→1171, 814→1229, 839→1254. The second `TrimForTranscription` call site (`:942`) needs the same `out var trimRemovedMs2` update.

- [ ] **Step 11: Enrich the pill-click retry success log**

In `TryPastePending()` (`:394–447`), replace lines `:398–400` (the outcome computation) and the success log (`:410–411`):

```csharp
        Winpepper.Platform.Injection.InjectionRunReport report = default;
        Winpepper.Platform.Injection.InjectionRunOutcome outcome;
        if (string.IsNullOrWhiteSpace(text))
        {
            outcome = Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed; // never ran
        }
        else
        {
            report = _injector.TryInjectGuardedDetailed(text);
            outcome = report.Outcome;
        }
```

and:

```csharp
        if (injected)
            _log.LogInformation(
                "Pending paste injected ({Chars} chars, {ChunksSent}/{ChunksTotal} chunks, nominal pacing {PacingMs} ms)",
                text.Length, report.ChunksSent, report.ChunksTotal, report.PacingWaitMs);
```

All other branches of the method (BlockedElevated / Interrupted / NoForeground / SendFailed handling, `ShowPendingPasteStatus`, `NotifyPasteAttempted`) stay byte-identical — they only read `outcome`.

- [ ] **Step 12: Verify + commit**

Run: `./scripts/linux-tests.sh` — Expected: exit 0 (file is `#if WINDOWS`; Linux proves no cross-project regressions). Compile verification of this task happens in Task 9's Windows gate — run it now if you want early feedback.

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
feat(app): per-dictation timing summary + stage-budget warnings

One 'dictation timing' INF line per dictation (both hotkey arms; also
emitted on silent-drop, pending, empty, and the previously-invisible
ASR-failed path) with per-stage ms: rec/drain/trim/asr(mode,model)/
corrections/cleanup(path,actually-used model)/inject(chars,chunks,
nominal pacing)/total, where total is a true wall-clock span seeded
from the hook-observed hotkey timestamp. Stage-budget overruns log
grep-able [WRN] 'slow dictation stage' lines. Cancel (also previously
invisible) gains a 'Session cancelled' INF line. Session-start lines
now carry hotkey-observation lag. Silent-drop log reports
voiced/clear/max-RMS; pill-click pending-paste success reports chunks
and nominal pacing. Stopwatch reads along the existing flow only -- no
new threads, no timers; the DeadlinePacer injection path and the
hotkey-hook fast path are untouched.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 9: Full-suite verification (Linux + Windows gate), no push

**Files:** none (verification only; fix-forward anything the gate finds, amending into focused commits)

- [ ] **Step 1: Full Linux suite**

Run from the worktree root: `./scripts/linux-tests.sh`

Expected: exit 0, `0 failures` across all 9 test projects.

- [ ] **Step 2: Full Windows gate**

Run from WSL, from the worktree root: `./scripts/windows-gate.sh` (~10 min; do NOT run `linux-tests.sh` concurrently).

Expected: exit 0 and `GATE: GREEN`. Known transient failures — retry the gate, do not "fix" them: UNC MSB4025 "retry should be performed" build flakes, vsock interop flakes, and `Hook_Installs_And_DisposesCleanly` hanging on a headless host (needs an interactive desktop). Llama cleanup tests self-skip when the qwen GGUF is absent — `Skipped > 0` keeps the gate green and is expected.

Baseline notes (validated 2026-07-28): the Linux suite was GREEN at base (1476 tests, 0 failed, 0 skipped — any future Linux `Skipped > 0` is itself a change to investigate) and the gate's hardcoded `powershell.exe` interop path executes; but host dotnet SDK health, `\\wsl.localhost` UNC build viability, and interactive-desktop availability were NOT pre-verified — if the first gate run fails in one of those categories, treat it as host-environmental, not as caused by this plan's changes. Also: the gate pre-cleans all `bin`/`obj`, so the next Linux run rebuilds from scratch (slow but expected).

If the gate finds REAL compile errors (most likely in the Task 4 or Task 8 Windows-only wiring), fix them and commit each fix scoped to the task it repairs, e.g. `fix(app): <what the gate caught>` with the standard Amplifier trailer, then re-run the gate until GREEN.

- [ ] **Step 3: Confirm branch state — and stop**

```bash
git log --oneline main..HEAD
git status --short
```

Expected: the 8 task commits (plus any gate-fix commits), clean tree. Do NOT push and do NOT merge — the branch `feat/pill-silence-observability` stays local; the root session merges to main.

---

## How to read the new logs (reference)

- One `dictation timing session=... kind=hold|toggle outcome=completed|pending|silent|failed|empty rec=...ms drain=...ms trim=...ms [trim_removed=...ms] asr=...ms [asr_mode=... asr_model=...] corrections=...ms cleanup=...ms [cleanup_path=... cleanup_model=...] inject=...ms [inject_chars=... inject_chunks=S/T inject_pace=...ms] total=...ms` line per dictation; skipped core stages read `skip`.
- `grep "slow dictation stage"` → budget overruns with stage, actual, budget, session id.
- Hotkey-press → pill-visible ≈ (`Session started ... hotkey observed N ms before handling` timestamp − N ms) → `pill stage Idle -> Recording` timestamp.
- Paste-complete → pill-hidden ≈ `pill stage Injecting -> Idle` (or `-> PendingPaste`) timestamp + the fixed 600 ms hide delay.
- Silence-gate recalibration: `grep "dropped silent recording"` → voiced/clear/max-RMS distributions vs the 600 ms / 0.02 / 100 ms constants.

## Self-Review (performed while writing; recorded for the reviewer)

1. **Spec coverage:** Fix 1 → Tasks 3+4 (timer policy + wiring, Error branch included, monitor-follow at ~1 s with change-guard); Fix 2 → Tasks 1+2 (AND-semantics gate, measured constants, both residual pins, the missing brief-transient test, recalibratable drop log fields via Task 8, streaming divergence documented not unified); Feature 3 → Tasks 5+6+7+8 (summary line incl. silent/pending/failed/empty outcomes, per-mode budgets with measured-vs-provisional rationale re-derived for the current 14 ms pacing, UI latency markers, hotkey lag, existing one-off logs preserved, zero-cost discipline, all pure logic Linux-tested).
2. **No silent deferrals:** the two spec-acknowledged verification gaps (on-device pill efficacy; residual gate classes) are pinned by tests or recorded in commit messages per the spec's explicit "accept + record" instruction — not moved to future work. The deferred visual cue for retained parks is the spec's own explicit deferral. No stubs, mocks, or fake providers stand in for any production behavior: every new log line and gate runs in production code paths.
3. **Placeholder scan:** every code step carries complete code; the only "adapt to the file" notes are pinned to verified existing helpers (`NewSession`, `Const`/`Concat`, usings) with concrete fallback instructions.
4. **Type consistency check:** `TrimResult.VoicedMs/ClearVoicedMs/MaxFrameRms` (Task 1) = fields read in Task 8's drop log; `PillTimerPlan.KeepAliveRunning/AnimationRunning` (Task 3) = members used in Task 4; `InjectionRunReport(Outcome, ChunksTotal, ChunksSent, PacingWaitMs)` and `TryInjectGuardedDetailed` (Task 5) = exactly what Task 8 Steps 7/11 consume; `DictationTimingSummary` property names in Task 8 match Task 6's class, and the `AsrMode` values Task 8 stamps (`streaming|batch|cloud` from `producedModelName`) match Task 6's mode set and budget rule (streaming budget only for explicit `"streaming"`); `SessionViewModel(..., ILogger? log = null)` (Task 7) matches the AppShell call. Verified consistent.
5. **Load-bearing validation pass (2026-07-28, post-writing):** 10 assumptions surfaced and dispatched to evidence: 9 verified (archive measurements, the near-miss WAV, Tick() safety, terminal coverage, baseline green, `TrimResult` construction sites, streaming latch boundary, budget distributions re-computed from raw production logs, and the model-name mode signal), 1 falsified (`maybeTranscription is not null` as the ASR-mode signal — fixed: mode now derives from `producedModelName`; Task 6 gained the `cloud` mode + `Overruns_CloudAsr_UsesBatchBudget`), 1 accepted residual (on-device pill-fix efficacy — tick keep-alive chosen over event-driven/higher-band alternatives; smoke checklist gained a wrong-monitor discrimination step). Ledger: `.worktrees/.the-usual-logs/pill-silence-observability/load-bearing-ledger.md`.

