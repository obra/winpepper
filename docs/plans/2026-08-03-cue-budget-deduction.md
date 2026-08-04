# Silence-Gate Cue-Budget Deduction Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix the active start-cue gate-mask regression (40% of dictations
dropped) by replacing the mask window's frame EXCLUSION with cue-budget
DEDUCTION in `SilenceTrimmer`, so prompt short replies pass while beep-only
recordings still drop.

**Architecture:** `StartCueGateMask` gains a second derivation,
`ComputeCueBudgetMs` (the cue's own deductible worth, derived from the
runtime-measured cue duration). `SilenceTrimmer.Trim` gains a third
defaulted parameter `cueBudgetMs`; the decision statistics revert to
all-frames (removing the post-mask stats exclusion), the voiced/clear
tallies count all frames, and up to the budget of in-window frames is
deducted from each tally. Trimming stays bit-identical. `PipelineHost`
plumbs the budget alongside the existing mask.

**Tech Stack:** C# / .NET 9 (`src/Winpepper.Audio`, `src/Winpepper.App`),
xUnit v3 + Shouldly (`tests/Winpepper.Audio.Tests`), Python 3 + numpy for
the offline archive-replay validation (logs-dir harness, not checked in).

## Global Constraints

- Base: `main` @ `5f80e96`; work only in this worktree
  (`/home/dan/code/winpepper/.worktrees/cue-budget-deduction`).
- Gate constants UNCHANGED: `SilentSpeechLevel 0.004`,
  `MinVoicedDurationMs 600`, `ClearSpeechRmsFloor 0.02`,
  `MinClearVoicedDurationMs 100`, P10/P90 percentiles, threshold formula
  `min(max(3*P10, 0.002), 0.15*P90)`.
- NO changes to: pre-roll (500 ms request / actual-seed plumbing), cue
  playback, the runtime cue measurement (`WavDuration`,
  `ISoundEffectPlayer.StartCueMs`), streaming
  (`ParakeetStreamingSession`/`InteriorSilenceSkipper`), or trimming
  semantics — `Trimmed`/`RemovedMs`/`RunsTrimmed` must be bit-identical
  for every input (existing characterization tests pin this).
- The cue duration is NEVER hardcoded: the budget must be derived from the
  measured `cueMs` argument plus a named constant. Banned literals in
  mask/budget math: `900`, `1000`, `150` as a cue length, `100` as a bare
  budget (write `cueMs - CueBudgetMarginMs`).
- `./scripts/linux-tests.sh` green before EVERY commit. NEVER `dotnet
  test` (xUnit v3 runs via `dotnet exec <built dll>`).
- Never mix Linux- and Windows-side builds in the same `bin/`/`obj/`.
- Full Windows gate before done: `./scripts/windows-gate.sh` (UNC MSB4025
  + vsock interop flakes are transients — retry).
- Do NOT push. Leave the branch local; the root session merges, gates,
  and installs.
- Every commit ends with the exact two-line Amplifier trailer (blank line
  between body and trailer):

  ```
  🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

  Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
  ```

- Out of scope: mid-sentence head-loss (speech before the pre-roll window;
  hold-retrigger splits), gate-constant recalibration, `README.md`.
- Privacy/size: NEVER commit archive WAVs or `index.json` slices into the
  repo. The frozen fixture corpora live under
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/fixtures/`
  and are referenced from the evidence doc by absolute path.

---

## Background: the defect, measured

Root-caused 2026-08-03 (investigation artifacts `/tmp/gate-inv3/`; frozen
copies + the deduction sweep in
`/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/`,
see `reports/dedu-sweep.md`).

- The warm cue mask = actualPreroll(500) + startLatency(200) + cueMs(150)
  + decay(150) = 1000 ms (`src/Winpepper.Audio/StartCueGateMask.cs:72-76`).
  Buffer t=0 is 500 ms BEFORE the hotkey, so the mask blinds the gate to
  the first ~500 ms of post-hotkey time.
- `SilenceTrimmer` EXCLUDES masked frames from the decision statistics
  (`SilenceTrimmer.cs:190-192`) and from the voiced/clear tallies
  (`SilenceTrimmer.cs:241-247`), so a short utterance spoken promptly
  after the press cannot reach the 600 ms voiced / 100 ms clear floors
  (`SilenceTrimmer.cs:249`) and the WHOLE dictation drops as silent.
- Measured: pre-install drop rate 3.7–8%; post-install 4/10 (40%). The
  four post-install drops all contain spectrally-verified real speech
  (harmonic stacks at F0 ~92–95 Hz with falling contours, matching
  transcribed positive controls — `reports/V1-audio-content.md`): WAV ids
  `173b20b3`, `525f0643`, `003777a1`, `4bf32da1`. Speech runs sit at
  810–1110 ms (in or straddling the mask window, past the cue) for the
  first three; `4bf32da1`'s run is at 1730–1890 ms — past the window; it
  dropped because the post-mask STATS starved (P90-silent), not because
  its tally was excluded. Production logs confirm all four dropped under
  `cue mask 1000 ms` (`reports/V2-warm-mask-counterfactual.md`).
- Offline replica reproduces all four deterministically:
  pre-mask-era verdict `pass`, mask-era verdict `DROP:voiced-floor` ×3 and
  `DROP:P90-silent` ×1 (`4bf32da1`).

### Frozen fixtures (already created — do not re-copy from the live mount)

The live archive is a rolling 100-entry window; the regression clips get
evicted as the owner keeps dictating. Both corpora were frozen on
2026-08-03 under
`/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/fixtures/`:

- `live-snapshot/` — 100 clips (88 with non-empty `rawTranscript` = real
  dictations), including the four regression ids and `cade05cf`
  (2026-08-03T23:48, empty transcript, cue/noise-only escape).
- `frozen-0of91/` — the 100-clip corpus behind the previous plan's 0/91
  claim, including the beep-only escape `67518b61` and 6 true-silent
  pre-era drops.

Each corpus is `index.json` + `<date>/<id>.wav`, replayable by the
harness in Task 4. Use ONLY these fixtures for validation, never the live
mount `/mnt/c/Users/dan/AppData/Local/winpepper/history`.

### Measured design decisions (deviations from the task text, data-forced)

The task text authorizes deviation where the code/data contradicts it,
and its validation criteria are non-negotiable. Two deviations, both
forced by the archive sweep (`reports/dedu-sweep.md`, budget sweep 0–400 ms
in 20 ms steps over both corpora):

1. **Budget is `cueMs - 50`, NOT `cueMs + decay` (~300 ms).** At 300 ms
   the deduction eats the user's own prompt speech and 3 of the 4
   MUST-pass regression WAVs still drop (at 300: `173b20b3` clear 0 ms,
   `525f0643` clear 80 ms, `003777a1` clear 0 ms — all voiced-floor
   drops; only `4bf32da1` passes). The full acceptance window that
   satisfies every criterion (4/4 regression pass; zero flips of real
   dictations on both corpora; zero drop→pass; beep escape `67518b61`
   drops; cue-only escape `cade05cf` drops) is **100–120 ms**. 100 ms is
   chosen: it maximizes the regression-side margin (binding clip
   `003777a1` has clear 120 ms vs the 100 ms floor; first to die at
   budget 140) while sitting at the minimum that closes `cade05cf`
   (which survives at ≤80). Derivation: `budget = max(0, cueMs -
   CueBudgetMarginMs)` with `CueBudgetMarginMs = 50` — scales with the
   measured cue, never hardcoded. Physical reading: the cue's clear-tier
   (≥0.02) mic pickup is 120–140 ms of its 150 ms emission; deducting
   50 ms less than the emission leaves a ≤40 ms beep residue that the
   100 ms clear floor absorbs, while every ms of under-deduction is a ms
   of the user's prompt speech preserved.
2. **The post-mask stats exclusion is REMOVED, not kept.** The task said
   keeping it is fine; the data falsifies that: `4bf32da1` has post-mask
   P90 = 0.001233 < 0.004, so under post-mask statistics it hits the
   P90-silent early exit and drops at EVERY budget — the deduction is
   unreachable. `173b20b3` leaves 3 post-mask frames for the percentiles
   (statistically starved, knife-edge at P90 0.004741). Decision
   statistics revert to all-frames; the exclusion's anti-cue duty
   (keeping the beep from unlocking the gate and inflating recalibration
   fields) moves to the budget deduction, and `MaxFrameRms` stays a
   post-window max so the cue still cannot inflate that recalibration
   field. Side benefit: decision threshold == trim threshold again (one
   percentile pass), making trim bit-identity structural.

Key per-clip numbers at mask 1000 / budget 100 (all-frames stats;
`voiced/clear` are the deducted tallies; floor is `voiced<600 &&
clear<100`):

| id | frames | thr | voiced_all/in-window | clear_all/in-window | voiced | clear | verdict |
|---|---|---|---|---|---|---|---|
| 173b20b3 | 53 | 0.002000 | 420/360 | 280/280 | 320 | 180 | pass |
| 525f0643 | 71 | 0.002000 | 600/260 | 260/180 | 500 | 160 | pass |
| 003777a1 | 69 | 0.002000 | 500/420 | 220/220 | 400 | 120 | pass |
| 4bf32da1 | 253 | 0.000638 | 720/240 | 260/140 | 620 | 160 | pass |
| cade05cf | 147 | 0.000786 | 680/380 | 120/120 | 580 | 20 | DROP:voiced-floor |
| 67518b61 | 97 | 0.000867 | 360/360 | 120/120 | 260 | 20 | DROP:voiced-floor |

### Plan validation (2026-08-03 load-bearing pass — verified facts)

An assumption-validation pass ran before execution; ledger + full evidence:
`/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/load-bearing-ledger.md`
(+ `reports/V1..V4`). Facts the implementer may rely on:

- All four regression WAVs are spectrally-verified real speech; both escape
  clips are cue/noise-only (no voiced frames); both escapes passed under the
  PRE-mask-era gate (V1, V2).
- Production logs (`/mnt/c/.../winpepper/logs/winpepper-20260803.log`) show
  `cue mask 1000 ms` for all four field drops, and the replica reproduces
  the logged voiced/clear/max-RMS exactly. All 200 fixture clips are warm
  (durationMs − recordMs ∈ [492, 513] ms), so the fixed mask-1000 replay is
  faithful (V2).
- Verdicts are robust at the knife edges: 0/200 clips flip under ±0.5-LSB
  dither or ±0.5% amplitude perturbation in either era config; the budget
  acceptance window is exactly 100–120 ms as stated above (V3).
- `./scripts/windows-gate.sh` ran GREEN from THIS worktree (attempt 1,
  ~9 min, 2402 tests, `PipelineHost.cs` compile diagnostics observed), and
  `./scripts/linux-tests.sh` ran GREEN immediately after it (1658 tests) —
  Task 5's exact sequence is pre-proven on this machine (V4).
- Accepted residual risks (do NOT re-litigate during implementation; they are
  recorded decisions): cross-config cue pickup, single-user corpus
  generalization, budget linearity beyond the 150 ms asset, cold-session
  escapes. Details in the ledger.

---

## File Structure

No new repo files. All changes modify existing files:

- `src/Winpepper.Audio/StartCueGateMask.cs` — add `CueBudgetMarginMs`
  const + `ComputeCueBudgetMs`; update class/`ComputeMaskMs` docs from
  "exclude" to "deduction window" wording. (Owns: mask/budget policy.)
- `src/Winpepper.Audio/SilenceTrimmer.cs` — replace the decision-path
  exclusion (lines ~162–278) with all-frames stats + budget deduction;
  add `cueBudgetMs` parameter; delete the dual trim-threshold
  re-derivation; update `TrimResult` field docs. (Owns: gate mechanism.)
- `src/Winpepper.App/Hosting/PipelineHost.cs` — `TrimForTranscription`
  (lines ~1729–1762): compute + pass the budget, extend the drop log.
  (Windows-only project — compiles at the Windows gate, not on Linux.)
- `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs` — +5 facts for
  `ComputeCueBudgetMs`.
- `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` — rewrite the mask
  block (lines 383–575): 5 tests rewritten for deduction semantics, 4 new
  facts, 3 mask tests and all 23 pre-mask tests untouched.
- `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` — append one
  `##` section: regression summary + fix validation numbers + gates.

Out-of-repo artifacts (referenced, not committed):
`/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/`
(`fixtures/`, `dedu.py`, `reports/dedu-sweep.md`).

Note: `scripts/asr-latency-bench/Program.cs:464` calls
`SilenceTrimmer.Trim(wavAudio)` single-arg; both new parameters are
defaulted so it compiles unchanged (bench WAVs have no cue — mask 0 /
budget 0 is correct there).

---

### Task 1: `StartCueGateMask.ComputeCueBudgetMs`

**Files:**
- Modify: `src/Winpepper.Audio/StartCueGateMask.cs`
- Test: `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs`

**Interfaces:**
- Consumes: nothing new (pure arithmetic; same inputs as `ComputeMaskMs`).
- Produces (Task 3 relies on these exact signatures):
  - `public const int CueBudgetMarginMs = 50;`
  - `public static int ComputeCueBudgetMs(int cueMs, bool soundsEnabled)`
    → `0` when `!soundsEnabled || cueMs <= 0`, else
    `Math.Max(cueMs - CueBudgetMarginMs, 0)`.
  - `ComputeMaskMs` signature and values UNCHANGED (all 8 existing facts
    stay green).

- [ ] **Step 1: Write the failing tests**

Append inside `StartCueGateMaskTests` in
`tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs` (file header/class
already exist; match the existing `[Fact]`-only, arithmetic-in-comment
house style):

```csharp
    [Fact]
    public void ComputeCueBudgetMs_MeasuredCue_DeductsCueWorthMinusMargin()
    {
        // Budget = measured cue - CueBudgetMarginMs = 150 - 50 = 100 ms.
        // Archive sweep 2026-08-03 (two frozen corpora, budget 0..400 ms in
        // 20 ms steps): the window satisfying ALL criteria (4/4 regression
        // WAVs pass, 0 real-dictation flips, 0 drop->pass, both beep/cue
        // escapes drop) is 100..120 ms; 100 maximizes the regression-side
        // margin (binding clip 003777a1: clear 120 vs the 100 ms floor).
        StartCueGateMask.ComputeCueBudgetMs(150, soundsEnabled: true).ShouldBe(100);
        StartCueGateMask.ComputeCueBudgetMs(150, true)
            .ShouldBe(150 - StartCueGateMask.CueBudgetMarginMs);
    }

    [Fact]
    public void ComputeCueBudgetMs_LongerCueAsset_ScalesWithMeasuredDuration()
    {
        // The asset may change or become user-configurable (owner
        // requirement): the budget must track the MEASURED duration, never
        // a constant. 300 ms asset => 300 - 50 = 250.
        StartCueGateMask.ComputeCueBudgetMs(300, soundsEnabled: true).ShouldBe(250);
    }

    [Fact]
    public void ComputeCueBudgetMs_SoundsDisabled_ReturnsZero()
    {
        // No cue was emitted => nothing to deduct (mirrors ComputeMaskMs).
        StartCueGateMask.ComputeCueBudgetMs(150, soundsEnabled: false).ShouldBe(0);
    }

    [Fact]
    public void ComputeCueBudgetMs_UnmeasuredCue_ReturnsZero()
    {
        // FAIL OPEN like the mask: unmeasured (0) or nonsense (negative)
        // cue duration => no deduction, gate behaves as before the mask.
        StartCueGateMask.ComputeCueBudgetMs(0, soundsEnabled: true).ShouldBe(0);
        StartCueGateMask.ComputeCueBudgetMs(-5, soundsEnabled: true).ShouldBe(0);
    }

    [Fact]
    public void ComputeCueBudgetMs_TinyCue_ClampsToZero()
    {
        // A cue shorter than the margin deducts nothing: max(40 - 50, 0).
        // Safe: a <=40 ms beep can never reach the 100 ms clear floor.
        StartCueGateMask.ComputeCueBudgetMs(40, soundsEnabled: true).ShouldBe(0);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS0117: 'StartCueGateMask' does not contain a
definition for 'ComputeCueBudgetMs'` (compile failure is the RED here).

- [ ] **Step 3: Implement**

In `src/Winpepper.Audio/StartCueGateMask.cs`, append inside the class
(after `ComputeMaskMs`, before the closing brace at line 77):

```csharp
    /// <summary>
    /// Deliberate under-deduction margin for <see cref="ComputeCueBudgetMs"/>:
    /// the budget deducts this many ms LESS than the measured cue emission.
    /// The cue's clear-tier (>= 0.02 RMS) mic pickup is 120-140 ms of the
    /// 150 ms emission (2026-08-03 archive measurement), so a 50 ms
    /// under-deduction leaves a &lt;= 40 ms beep residue in a beep-only
    /// tally -- safely under the 100 ms clear floor -- while every ms NOT
    /// deducted is a ms of the user's prompt speech preserved. Archive
    /// sweep (two frozen 100-clip corpora, budgets 0-400 ms): the window
    /// satisfying all regression/escape criteria is 100-120 ms; cueMs - 50
    /// = 100 sits at maximum regression margin. NOTE: the 50 ms margin is
    /// validated ONLY at the 150 ms shipped asset; if the cue asset
    /// materially changes, re-run the archive sweep before trusting the
    /// derived budget. Evidence:
    /// docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
    /// (cue-budget deduction section).
    /// </summary>
    public const int CueBudgetMarginMs = 50;

    /// <summary>
    /// The cue's own deductible worth: how many ms of in-window voiced and
    /// clear tally SilenceTrimmer may subtract as "that was probably the
    /// cue, not the user". Derived from the runtime-MEASURED cue duration
    /// (never hardcoded -- the asset may change), 0 when the cue is
    /// disabled or unmeasured (FAIL OPEN: nothing was played, nothing is
    /// deducted, the gate behaves as before the mask existed). NOTE the
    /// asymmetry with <see cref="ComputeMaskMs"/>: the mask WINDOW is
    /// sized generously (over-masking a window that merely marks frames as
    /// deduction-eligible is safe), but the BUDGET is sized tightly --
    /// over-deducting eats the user's own prompt speech, which is exactly
    /// the 2026-08-03 regression this replaces.
    /// </summary>
    public static int ComputeCueBudgetMs(int cueMs, bool soundsEnabled)
    {
        if (!soundsEnabled || cueMs <= 0) return 0;
        return Math.Max(cueMs - CueBudgetMarginMs, 0);
    }
```

Then update the class-level `<summary>` (lines 3–29): replace its first
sentence

```
/// Computes the head-of-buffer window that <see cref="SilenceTrimmer"/>
/// excludes from its silence-gate DECISION because the app's own start cue
/// contaminates it
```

with

```
/// Computes (a) the head-of-buffer window in which the app's own start cue
/// can contaminate the mic capture and (b) the cue-budget the silence gate
/// deducts from that window's voiced/clear tallies (cue-budget DEDUCTION,
/// 2026-08-03, replacing the window EXCLUSION that regressed prompt short
/// replies)
```

and in the `ComputeMaskMs` doc (line 64) replace
`The mask duration SilenceTrimmer should exclude from its decision.` with
`The window within which SilenceTrimmer's tallies are cue-budget-deductible
(it no longer excludes these frames).`

Leave `ComputeMaskMs`'s body, `WarmPrerollMs`, `CueStartLatencyMarginMs`,
`CueDecayMarginMs` values untouched.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll \
  -class "Winpepper.Audio.Tests.StartCueGateMaskTests"
```

Expected: `Total: 13, Errors: 0, Failed: 0, Skipped: 0` (8 existing + 5 new).

- [ ] **Step 5: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN` (every project `Errors: 0`, `Failed: 0`).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Audio/StartCueGateMask.cs tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs
git commit -m "$(cat <<'EOF'
feat(audio): derive the silence-gate cue budget from the measured cue duration

ComputeCueBudgetMs(cueMs, soundsEnabled) = max(cueMs - CueBudgetMarginMs(50), 0),
0 when disabled/unmeasured (fail open). This is the deductible "cue's own
worth" for the upcoming SilenceTrimmer cue-budget deduction (regression
fix for the 2026-08-02 mask-window exclusion). Sized by a 0-400 ms budget
sweep over two frozen 100-clip archive corpora: acceptance window is
100-120 ms; cueMs - 50 = 100 maximizes the regression-side margin. Scales
with the measured asset, never hardcoded.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 2: `SilenceTrimmer` — exclusion → cue-budget deduction

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` (signature :138,
  decision path :162–278, `TrimResult` docs :29/:39/:47)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (mask block
  :383–575)

**Interfaces:**
- Consumes: nothing from Task 1 (tests pass literal budgets; production
  wiring is Task 3).
- Produces (Task 3 relies on this exact signature):
  `public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0)`.
  `TrimResult` shape unchanged (same 7 required-init fields); `VoicedMs`
  and `ClearVoicedMs` are now budget-deducted counts; `MaxFrameRms`
  remains the post-window max.

Semantics to implement (the contract):

- `maskFrames` = ceil(`maskMs`/20) clamped to `frameCount` (unchanged);
  `budgetFrames` = ceil(`cueBudgetMs`/20), not clamped (deduction is
  already capped per-tally).
- Decision statistics (P90 speech level, P90-silent gate, P10, threshold)
  over ALL frames. The `decisionFrameCount == 0` early return is DELETED
  (a fully-masked buffer is no longer silent by definition; `frameCount
  >= 1` is guaranteed by the existing sub-frame early return, so the
  percentile empty-array guard is structurally unnecessary).
- Voiced/clear tallies over ALL frames, tracking the in-window share
  (`f < maskFrames`); then
  `voicedMs = (voicedFrames - min(budgetFrames, voicedFramesInWindow)) * 20`
  and likewise for clear. Counts are frame counts, so "deduct the loudest
  in-window frames" reduces to capping the deduction at the in-window
  share.
- P90-silent path reports `ClearVoicedMs` budget-deducted the same way
  (all-frames ≥0.02 count minus `min(budgetFrames, in-window ≥0.02
  count)`); `VoicedMs` stays 0.
- `MaxFrameRms` = max over frames `[maskFrames, frameCount)` on every
  path (0 when the window covers everything) — recalibration field, cue
  must not inflate it.
- Trim threshold = the (all-frames) decision threshold; the `maskFrames >
  0` re-derivation block is DELETED. Walker/output untouched. This keeps
  `Trimmed`/`RemovedMs`/`RunsTrimmed` bit-identical to both the current
  code and the pre-mask code for every input.
- `maskMs <= 0` ⇒ in-window shares are 0 ⇒ deduction 0 regardless of
  budget ⇒ byte-identical to pre-mask behavior.

- [ ] **Step 1: Rewrite the mask-test block with the new expectations**

In `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs`, the mask block
(lines 383–575) currently holds 9 tests. Keep these three UNTOUCHED:
`Trim_MaskZero_IsIdenticalToUnmasked`, `Trim_NegativeMask_TreatedAsZero`,
`Trim_MaskDoesNotChangeTrimOffsets_InteriorGap`. Also leave
`Trim_VoicedSpeechAfterMask_StillPasses_TrimmingUnchanged` untouched (its
speech and its counts are entirely post-window; the uniform-noise fixture
gives identical percentiles under all-frames stats, so its exact counter
asserts stay valid — if it unexpectedly fails after Step 4, STOP and
re-check the implementation rather than editing the test).

Replace the other five ENTIRE test methods (match by method name, delete
the old body including its `[Fact]`, insert these) and append the four
new facts. Every replacement is justified in the Step 8 commit message.

Replacement 1 — was `Trim_CueBeepAloneInsideMask_NoLongerPassesEscapeHatch`
(:427). Justification: pinned exclusion semantics (post-mask counts +
post-mask stats); its 240 ms beep also exceeded the real cue's worth —
under deduction the fixture must model the MEASURED cue pickup
(120–140 ms clear) for the zero-out property to hold:

```csharp
    [Fact]
    public void Trim_CueBeepAloneInsideMask_StillDrops_ByBudgetDeduction()
    {
        // THE 2026-08-02 escape class, re-pinned under DEDUCTION semantics.
        // Beep-only recording: the only energy is the cue's mic pickup,
        // modelled at its measured size (140 ms @ 0.05 starting 600 ms in;
        // archive: onset 592-644 ms, clear pickup 120-140 ms of the 150 ms
        // emission). 1200 ms buffer = 60 frames; beep = frames 30-36 (7).
        // Unmasked: P90 idx floor(0.9*59)=53 lands in the 7 loud frames ->
        // P90 0.05 passes the 0.004 gate; thr = min(max(3*0.001, 0.002),
        // 0.15*0.05) = 0.003; voiced 140 < 600 but clear 140 >= 100 -> the
        // escape hatch would PASS a silent recording (pinned below).
        // Masked+budget: window ceil(1000/20)=50 frames, budget
        // ceil(100/20)=5 frames; all 7 beep frames are in-window, so each
        // tally deducts min(5,7)=5 frames: voiced = clear = (7-5)*20 = 40.
        // 40 < 600 && 40 < 100 -> DROPS via the voiced floor.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        SilenceTrimmer.Trim(buf, 0).IsSilent.ShouldBeFalse(); // the escape, pinned

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(40);        // beep residue after deduction
        masked.ClearVoicedMs.ShouldBe(40);   // < 100 ms clear floor
        masked.MaxFrameRms.ShouldBe(0.001, 0.0005); // post-window max, not 0.05
        masked.Trimmed.Length.ShouldBe(0);
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
    }
```

Replacement 2 — was `Trim_SpeechStartingInsideMask_PassesOnPostMaskRemainder`
(:474). Justification: pinned the exclusion's post-mask-only counts
(`VoicedMs == 1100` = 1400 − 300 in-mask); deduction keeps the surplus:

```csharp
    [Fact]
    public void Trim_SpeechStartingInsideMask_KeepsSurplusAfterBudgetDeduction()
    {
        // Speech spans 700-2100 ms -- it STARTS inside the 1000 ms window.
        // 3000 ms = 150 frames: 35 tone | 70 speech (frames 35-104) | 45
        // tone. All-frames stats: P90 idx floor(0.9*149)=134 -> 0.05;
        // thr = min(max(3*0.001, 0.002), 0.0075) = 0.003. Tallies count all
        // 70 speech frames (voiced_all = clear_all = 1400 ms); in-window
        // share = frames 35-49 = 15; deduction = min(budget 5, 15) = 5
        // frames -> voiced = clear = (70-5)*20 = 1300 ms (was 1100 under
        // exclusion: the 300 in-window ms minus the cue's 100 ms worth are
        // the user's own speech, now kept).
        var buf = Join(Dc(0.001, 700), Dc(0.05, 1400), Dc(0.001, 900));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(1300);
        masked.ClearVoicedMs.ShouldBe(1300);
        // Trimming identical to unmasked: leading 35-frame run removes 5
        // (100 ms), trailing 45-frame run removes 15 (300 ms).
        masked.RemovedMs.ShouldBe(400);
        masked.RunsTrimmed.ShouldBe(2);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }
```

Replacement 3 — was `Trim_UtteranceEntirelyInsideMask_IsSilent_KnownResidual`
(:541). Justification: the accepted residual this test pinned is exactly
the regression's failure mode; deduction FIXES it (the residual shrinks
to in-window utterances with ≤ budget+100 ms of clear speech):

```csharp
    [Fact]
    public void Trim_UtteranceEntirelyInsideMask_NowPassesOnBudgetSurplus()
    {
        // Regression-class recording (2026-08-04, 4/10 owner dictations):
        // a real utterance spoken promptly after the hotkey, ENTIRELY
        // inside the cue window. 1000 ms buffer = 50 frames, speech 500 ms
        // @ 0.05 = frames 15-39 (25 loud). Window covers all 50 frames.
        // All-frames stats: P90 idx floor(0.9*49)=44 -> 0.05; thr = 0.003.
        // voiced_all = clear_all = 500 ms, all in-window; deduction =
        // min(5, 25) = 5 frames -> voiced = clear = (25-5)*20 = 400 ms.
        // clear 400 >= 100 -> PASSES (under exclusion this was the
        // "fully-masked => silent by definition" hard drop).
        var buf = Join(Dc(0.001, 300), Dc(0.05, 500), Dc(0.001, 200));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(400);
        masked.ClearVoicedMs.ShouldBe(400);
        // Window covers every frame -> the post-window observability max
        // is empty by definition and reports 0.
        masked.MaxFrameRms.ShouldBe(0.0);
        // Leading 15-frame and trailing 10-frame silence runs are each
        // <= the 30-frame edge keep -> nothing trimmed.
        masked.RemovedMs.ShouldBe(0);
        masked.Trimmed.Length.ShouldBe(buf.Length);
    }
```

Replacement 4 — was `Trim_RecordingShorterThanMask_IsSilent_DoesNotThrow`
(:520). Justification: pinned the deleted `decisionFrameCount == 0` hard
drop; the guard property (no crash, quiet fully-covered buffer drops) is
re-pinned via the P90-silent path:

```csharp
    [Fact]
    public void Trim_RecordingShorterThanMask_QuietBuffer_IsSilent_DoesNotThrow()
    {
        // 400 ms of near-silence, window 1000 ms: maskFrames clamps to all
        // 20 frames. No special-case branch remains for this -- all-frames
        // P90 = 0.001 < 0.004 -> P90-silent. Nothing is >= 0.02, so the
        // deducted clear count is 0; post-window max over an empty range
        // reports 0.
        var buf = Dc(0.001, 400);

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        masked.IsSilent.ShouldBeTrue();
        masked.VoicedMs.ShouldBe(0);
        masked.ClearVoicedMs.ShouldBe(0);
        masked.MaxFrameRms.ShouldBe(0.0);
        masked.Trimmed.Length.ShouldBe(0);
    }
```

Replacement 5 — was `Trim_MaskRoundsUpToWholeFrames` (:563).
Justification: pinned ceil rounding via exclusion's count effect; ceil
now governs deduction ELIGIBILITY:

```csharp
    [Fact]
    public void Trim_MaskRoundsUpToWholeFrames_ForDeductionEligibility()
    {
        // One clear frame at 880-900 ms (frame 44) in an otherwise quiet
        // 1200 ms buffer (60 frames). All-frames P90 idx floor(0.9*59)=53
        // -> 0.001 < 0.004: P90-silent path; the reported clear count is
        // budget-deducted. mask 890 -> ceil(890/20) = 45 frames: frame 44
        // is IN the window, so min(budget 5, in-window 1) = 1 frame is
        // deducted -> clear 0. mask 880 -> 44 frames: frame 44 is OUTSIDE,
        // nothing is deduction-eligible -> clear 20. Unmasked reports the
        // raw 20 ms.
        var buf = Join(Dc(0.001, 880), Dc(0.05, 20), Dc(0.001, 300));

        SilenceTrimmer.Trim(buf, 0).ClearVoicedMs.ShouldBe(20);
        SilenceTrimmer.Trim(buf, 880, 100).ClearVoicedMs.ShouldBe(20);
        SilenceTrimmer.Trim(buf, 890, 100).ClearVoicedMs.ShouldBe(0);
        SilenceTrimmer.Trim(buf, 890, 100).IsSilent.ShouldBeTrue();
    }
```

New fact 1 — the budget also rounds up to whole frames:

```csharp
    [Fact]
    public void Trim_CueBudgetRoundsUpToWholeFrames()
    {
        // Beep-only fixture from the escape test: 7 in-window clear frames.
        // budget 90 -> ceil(90/20) = 5 frames deducted, same as budget 100:
        // (7-5)*20 = 40. budget 80 -> exactly 4 frames: (7-4)*20 = 60.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        SilenceTrimmer.Trim(buf, 1000, 90).VoicedMs.ShouldBe(40);
        SilenceTrimmer.Trim(buf, 1000, 80).VoicedMs.ShouldBe(60);
    }
```

New fact 2 — a budget without a window is inert:

```csharp
    [Fact]
    public void Trim_BudgetWithoutMask_IsInert()
    {
        // maskMs 0 => no frame is deduction-eligible, so any budget deducts
        // nothing: identical to the plain unmasked call on every field.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        var plain = SilenceTrimmer.Trim(buf, 0);
        var budgeted = SilenceTrimmer.Trim(buf, 0, 100);

        budgeted.IsSilent.ShouldBe(plain.IsSilent);
        budgeted.VoicedMs.ShouldBe(plain.VoicedMs);
        budgeted.ClearVoicedMs.ShouldBe(plain.ClearVoicedMs);
        budgeted.MaxFrameRms.ShouldBe(plain.MaxFrameRms);
        budgeted.RemovedMs.ShouldBe(plain.RemovedMs);
        budgeted.RunsTrimmed.ShouldBe(plain.RunsTrimmed);
        budgeted.Trimmed.SequenceEqual(plain.Trimmed).ShouldBeTrue();
    }
```

New fact 3 — a window with zero budget counts everything, deducts nothing
(pins that the EXCLUSION is gone and the beep protection lives entirely
in the budget; production always derives both from the same cue state,
see Task 3):

```csharp
    [Fact]
    public void Trim_MaskWithZeroBudget_CountsAllFramesWithoutDeduction()
    {
        // Same beep-only fixture: with the window present but budget 0 the
        // tallies include the beep undeducted (voiced = clear = 140) and
        // the clear escape hatch passes -- the 2026-08-02 exclusion is
        // GONE by design; ComputeCueBudgetMs is what closes the escape.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 460));

        var r = SilenceTrimmer.Trim(buf, 1000, 0);
        r.IsSilent.ShouldBeFalse();
        r.VoicedMs.ShouldBe(140);
        r.ClearVoicedMs.ShouldBe(140);
    }
```

New fact 4 — the headline regression shape (prompt short reply straddling
the window edge; synthetic twin of archive clip `003777a1`):

```csharp
    [Fact]
    public void Trim_PromptShortReply_SpeechInsideWindowPastCue_Passes()
    {
        // THE 2026-08-03 regression (4/10 owner dictations dropped): cue
        // pickup at 600-740 ms, the user's short reply at 820-1080 ms --
        // inside the 1000 ms window but past the cue. 1400 ms = 70 frames:
        // cue frames 30-36 (7), speech frames 41-53 (13). Under EXCLUSION
        // the decision saw only frames 50-69: clear 80 < 100, voiced 80 <
        // 600 -> the whole dictation dropped. Under DEDUCTION: all-frames
        // P90 idx floor(0.9*69)=62 -> 0.05; thr = 0.003; voiced_all =
        // clear_all = 20 frames = 400 ms; in-window share = 7+9 = 16
        // frames; deduct min(5,16)=5 -> voiced = clear = 300 >= 100 ->
        // PASSES with the user's surplus intact.
        var buf = Join(Dc(0.001, 600), Dc(0.05, 140), Dc(0.001, 80),
                       Dc(0.05, 260), Dc(0.001, 320));

        var masked = SilenceTrimmer.Trim(buf, 1000, 100);
        var unmasked = SilenceTrimmer.Trim(buf, 0);

        masked.IsSilent.ShouldBeFalse();
        masked.VoicedMs.ShouldBe(300);
        masked.ClearVoicedMs.ShouldBe(300);
        masked.MaxFrameRms.ShouldBe(0.05, 0.001); // post-window speech frames
        // Leading 30-frame edge run == the 30-frame keep budget, interior
        // 4-frame gap and trailing 16-frame run under their budgets ->
        // nothing trimmed; output identical to unmasked.
        masked.RemovedMs.ShouldBe(0);
        masked.RunsTrimmed.ShouldBe(0);
        masked.Trimmed.SequenceEqual(unmasked.Trimmed).ShouldBeTrue();
    }
```

- [ ] **Step 2: Run to verify the new tests fail**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS1501: No overload for method 'Trim' takes 3
arguments` (the RED — the 3-argument calls don't exist yet).

- [ ] **Step 3: Change the `Trim` signature and doc comment**

In `src/Winpepper.Audio/SilenceTrimmer.cs` change line 138:

```csharp
    public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0)
```

Replace the XML doc comment immediately above `Trim` with:

```csharp
    /// <summary>
    /// Trim interior/edge silence and decide whether the recording is
    /// silent. <paramref name="maskMs"/> is the head-of-buffer window in
    /// which the app's own start cue can appear
    /// (StartCueGateMask.ComputeMaskMs); <paramref name="cueBudgetMs"/> is
    /// the cue's own deductible worth
    /// (StartCueGateMask.ComputeCueBudgetMs). Frames in the window COUNT
    /// toward every decision statistic and tally, and up to the budget of
    /// in-window frames is then DEDUCTED from the voiced and clear tallies
    /// (cue-budget deduction, 2026-08-03 -- replaces the window EXCLUSION
    /// that dropped prompt short replies). maskMs &lt;= 0 or
    /// cueBudgetMs &lt;= 0 deducts nothing; both default to 0 = pre-mask
    /// behavior, byte-identical. Trimming offsets and the output buffer
    /// are unaffected by mask and budget by construction.
    /// </summary>
```

Update the `TrimResult` field docs: on `VoicedMs` (:29) and
`ClearVoicedMs` (:39) replace the "post-mask count" wording with
`Cue-budget-deducted count (in-window frames count, then up to the cue
budget of them is subtracted).`; on `MaxFrameRms` (:47) make it `Max
frame RMS over the frames AFTER the cue window (recalibration field; the
cue must not inflate it; 0 when the window covers every frame).`

- [ ] **Step 4: Replace the decision path (exclusion → deduction)**

Two surgical replacements in `src/Winpepper.Audio/SilenceTrimmer.cs`,
with the floor check between them kept untouched.

**(4a)** Delete lines 162–247 — from the comment line `// Start-cue
mask: frames [0, maskFrames) are excluded ...` (line 162) through the
closing `}` of the voiced/clear tally loop (line 247). That removes the
old mask computation, the fully-masked (`decisionFrameCount == 0`) early
return, the post-mask statistics, and the exclusion-based tally. In
their place insert:

```csharp
        // Start-cue budget DEDUCTION (2026-08-03, replacing the 2026-08-02
        // window EXCLUSION). The exclusion blinded the gate to the first
        // ~500 ms of post-hotkey time (buffer t=0 sits the seeded pre-roll
        // BEFORE the hotkey), so a prompt short reply could not reach the
        // 600/100 ms floors and the WHOLE dictation dropped: 4/10 owner
        // dictations on 2026-08-04 (archive WAVs 173b20b3, 525f0643,
        // 003777a1, 4bf32da1 -- all real speech at 820-1180 ms). Now the
        // window's frames COUNT normally and the gate deducts up to
        // cueBudgetMs (the cue's own worth, derived from the measured cue
        // duration -- StartCueGateMask.ComputeCueBudgetMs) of in-window
        // frames from each tally. A beep-only recording's in-window tally
        // IS the cue (measured 120-140 ms clear pickup), so it deducts to
        // below the floors and still drops; prompt real speech keeps its
        // surplus and passes. Ceil on both conversions: a partially
        // covered frame is fully eligible, a partial budget frame deducts
        // whole.
        var maskFrames = maskMs <= 0 ? 0 : Math.Min((maskMs + FrameMs - 1) / FrameMs, frameCount);
        var budgetFrames = cueBudgetMs <= 0 ? 0 : (cueBudgetMs + FrameMs - 1) / FrameMs;

        // DECISION statistics run over ALL frames again -- the 2026-08-02
        // post-mask stats exclusion is deliberately REMOVED, not kept.
        // Measured why (2026-08-03 archive, see the cue-budget section of
        // docs/plans/2026-07-29-cleanup-asr-contention-evidence.md): with
        // a prompt short reply the post-mask remainder is statistically
        // starved (clip 173b20b3, 1070 ms: 3 frames left for the
        // percentiles), and the P90-silent gate misfires on real speech
        // (clip 4bf32da1: post-mask P90 0.0012 < 0.004 despite 620 ms of
        // deducted voiced audio -- unfixable by any budget while the
        // exclusion stands). The exclusion's anti-cue duty moves to the
        // budget deduction below; MaxFrameRms stays post-window so the cue
        // still cannot inflate the recalibration fields. Side benefit: the
        // decision threshold IS the trim threshold again (one percentile
        // pass over all frames), so trimming is bit-identical by
        // construction.
        var sorted = (double[])rms.Clone();
        Array.Sort(sorted);
        var speechLevel = Percentile(sorted, SpeechLevelPercentile);

        // Post-window max: drop-log recalibration field; the cue window
        // must not inflate it (0 when the window covers every frame).
        var postWindowMax = 0.0;
        for (var f = maskFrames; f < frameCount; f++)
            if (rms[f] > postWindowMax) postWindowMax = rms[f];

        if (speechLevel < SilentSpeechLevel)
        {
            // P90-silent: the adaptive threshold is undefined (it is
            // derived from a speech level that does not exist), so
            // VoicedMs reports 0. The clear count is reported
            // budget-deducted so the cue cannot inflate the recalibration
            // fields (pre-mask logs showed clear = 60-160 ms of pure beep
            // on every silent drop).
            var clearAll = 0;
            var clearInWindow = 0;
            for (var f = 0; f < frameCount; f++)
            {
                if (rms[f] < ClearSpeechRmsFloor) continue;
                clearAll++;
                if (f < maskFrames) clearInWindow++;
            }
            return new TrimResult
            {
                Trimmed = Array.Empty<float>(),
                RemovedMs = 0,
                RunsTrimmed = 0,
                IsSilent = true,
                VoicedMs = 0,
                ClearVoicedMs = (clearAll - Math.Min(budgetFrames, clearInWindow)) * FrameMs,
                MaxFrameRms = postWindowMax,
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

        // Minimum-voiced-duration gate (2026-07-28 transient-rejection
        // fix; AND semantics -- this gate can only make the verdict MORE
        // silent). Tally ALL frames, tracking the in-window share, then
        // deduct up to the cue budget of the loudest in-window frames from
        // each tally. The tallies are frame COUNTS, so "loudest first"
        // reduces to capping the deduction at the in-window share.
        var voicedFrames = 0;
        var clearFrames = 0;
        var voicedFramesInWindow = 0;
        var clearFramesInWindow = 0;
        for (var f = 0; f < frameCount; f++)
        {
            if (rms[f] < threshold) continue;
            voicedFrames++;
            if (f < maskFrames) voicedFramesInWindow++;
            if (rms[f] >= ClearSpeechRmsFloor)
            {
                clearFrames++;
                if (f < maskFrames) clearFramesInWindow++;
            }
        }
        var voicedMs = (voicedFrames - Math.Min(budgetFrames, voicedFramesInWindow)) * FrameMs;
        var clearVoicedMs = (clearFrames - Math.Min(budgetFrames, clearFramesInWindow)) * FrameMs;
        var maxFrameRms = postWindowMax;
```

The floor check that follows (old :249–261,
`if (voicedMs < MinVoicedDurationMs && clearVoicedMs < MinClearVoicedDurationMs)`
and its returning block) stays EXACTLY as-is — it consumes
`voicedMs`/`clearVoicedMs`/`maxFrameRms` defined above.

**(4b)** Replace the trim-threshold re-derivation block (old lines
263–278: from the comment `// TRIM threshold: ALL frames ...` through
the `}` closing `if (maskFrames > 0)`, including the old
`var trimThreshold = threshold;` declaration inside it) with just this,
so the untouched walker (old :280 onward,
`var isSilence = new bool[frameCount];` ...) keeps compiling:

```csharp
        // TRIM threshold == the decision threshold: both are derived over
        // ALL frames now, so the mask/budget can never move trimming
        // offsets or change the output buffer (the walker's sole input is
        // isSilence[]).
        var trimThreshold = threshold;
```

Nothing after `var isSilence = new bool[frameCount];` changes. Verify no
reference to `decisionFrameCount` or `decisionSorted` remains (both are
deleted with the old block).

- [ ] **Step 5: Build and run the SilenceTrimmer class tests**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll \
  -class "Winpepper.Audio.Tests.SilenceTrimmerTests"
```

Expected: `Total: 36, Errors: 0, Failed: 0, Skipped: 0` (23 pre-mask + 4
kept mask tests + 5 replacements + 4 new facts).

- [ ] **Step 6: Whole Audio test project**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows"
```

Expected: `Total: 111, Errors: 0, Failed: 0` (was 102; +5 Task 1, +4 here).

- [ ] **Step 7: Full Linux suite**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "$(cat <<'EOF'
fix(audio): silence gate counts the cue window and deducts the cue budget, replacing exclusion

The 2026-08-02 window EXCLUSION blinded the gate to the first ~500 ms of
post-hotkey time and dropped 4/10 real dictations on 2026-08-04. Now
in-window frames count toward the stats and tallies, and up to
cueBudgetMs of in-window frames is deducted from the voiced/clear
tallies: a beep-only recording deducts to below the floors and still
drops; prompt real speech keeps its surplus and passes. Decision stats
revert to all-frames (post-mask stats starve on short clips and
P90-silence real speech: archive 4bf32da1 post-mask P90 0.0012);
MaxFrameRms stays post-window for recalibration honesty. Trim threshold
== decision threshold again: trimming bit-identical by construction.

Test adjustments, each pinned to the semantics change:
- CueBeepAloneInsideMask: fixture resized to the cue's measured worth
  (140 ms clear pickup); drops via deducted voiced-floor, not exclusion.
- SpeechStartingInsideMask: counts keep the surplus (1300, was 1100).
- UtteranceEntirelyInsideMask: the KnownResidual is FIXED -- now passes.
- RecordingShorterThanMask: hard fully-masked drop branch deleted; quiet
  buffer re-pinned via P90-silent.
- MaskRoundsUpToWholeFrames: ceil now governs deduction eligibility.
New pins: budget ceil rounding, budget-without-window inert,
window-with-zero-budget counts undeducted, prompt-short-reply regression
shape passes. maskMs=0 identity and trim-offset invariance tests
unchanged and green.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 3: `PipelineHost` plumbs the cue budget

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs:1729-1762`
  (`TrimForTranscription`)

**Interfaces:**
- Consumes: `StartCueGateMask.ComputeCueBudgetMs(int, bool)` (Task 1);
  `SilenceTrimmer.Trim(ReadOnlySpan<float>, int, int)` (Task 2);
  existing `_sounds.StartCueMs`, `_sounds.Enabled`,
  `_lastSessionPrerollMs`.
- Produces: nothing new for later tasks (this is the production wiring;
  Task 4 validates the semantics offline, Task 5 compiles this file at
  the Windows gate).

`Winpepper.App` is Windows-only (WinUI): it does NOT build on Linux. The
Linux suite still gates the commit (proves the shared `Winpepper.Audio`
contract is intact); the compile proof for this file is Task 5's Windows
gate. There is no unit test for `TrimForTranscription` (pre-existing
condition; the behavior change is pinned by the Task 2 unit tests and the
Task 4 archive replay).

- [ ] **Step 1: Edit `TrimForTranscription`**

Replace lines 1739–1740:

```csharp
        var cueMaskMs = StartCueGateMask.ComputeMaskMs(_lastSessionPrerollMs, _sounds.StartCueMs, _sounds.Enabled);
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples, cueMaskMs);
```

with:

```csharp
        var cueMaskMs = StartCueGateMask.ComputeMaskMs(_lastSessionPrerollMs, _sounds.StartCueMs, _sounds.Enabled);
        var cueBudgetMs = StartCueGateMask.ComputeCueBudgetMs(_sounds.StartCueMs, _sounds.Enabled);
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples, cueMaskMs, cueBudgetMs);
```

Replace the drop log statement (lines 1750–1752):

```csharp
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000}, cue mask {CueMaskMs} ms)",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms, cueMaskMs);
```

with:

```csharp
            _log.LogInformation(
                "dropped silent recording, {Ms} ms (voiced {VoicedMs} ms, clear {ClearVoicedMs} ms, max frame rms {MaxFrameRms:0.0000}, cue mask {CueMaskMs} ms, cue budget {CueBudgetMs} ms)",
                ms, result.VoicedMs, result.ClearVoicedMs, result.MaxFrameRms, cueMaskMs, cueBudgetMs);
```

In the comment block above the mask computation (lines 1731–1738),
replace the sentence `Mask the app's own start cue out of the gate
DECISION.` with `Give the gate the cue window AND the cue's deductible
budget (cue-budget deduction, 2026-08-03: in-window frames count, up to
the budget of them is deducted -- the old window EXCLUSION dropped prompt
short replies).` — the rest of that comment (player-honesty and
preroll-awareness rationale) stays. In the method's XML `<summary>`
(lines 1726–1727) replace `the drop line's voiced/clear/max-RMS are
post-mask counts` with `the drop line's voiced/clear are cue-budget-
deducted counts and max-RMS is the post-window max`.

- [ ] **Step 2: Sanity-check the only other `Trim` caller**

```bash
grep -rn "SilenceTrimmer.Trim" --include="*.cs" src scripts tests | grep -v Audio.Tests
```

Expected output: exactly two production/bench call sites —
`src/Winpepper.App/Hosting/PipelineHost.cs` (3-arg, just edited) and
`scripts/asr-latency-bench/Program.cs:464` (1-arg; defaulted parameters,
no edit needed).

- [ ] **Step 3: Full Linux suite (commit gate; App itself builds on Windows only)**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "$(cat <<'EOF'
feat(app): plumb the cue budget into the silence gate and drop log

TrimForTranscription now passes ComputeCueBudgetMs alongside the mask
window; the drop line reports the budget next to the mask so post-install
drops remain diagnosable. Windows-only file: Linux suite green as the
commit gate, compile proof lands with the full Windows gate.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 4: Offline archive validation + evidence doc

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`
  (append one `##` section at the end)
- Reads (out of repo, absolute paths):
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/fixtures/{live-snapshot,frozen-0of91}/`

**Interfaces:**
- Consumes: the frozen fixtures (see Background) and the final semantics
  from Task 2 (the harness below is a line-for-line port of them).
- Produces: recorded validation numbers; Task 5 appends the gates bullet
  to the same evidence section.

The four criteria (non-negotiable, from the task): (1) the four captured
regression WAVs PASS; (2) every pre-mask genuinely-silent drop still
DROPS; (3) beep-only recordings still DROP; (4) ZERO flips on the real
gate-passing dictations in the archive.

- [ ] **Step 1: Run the replica harness against both frozen corpora**

Run exactly this (an inline-heredoc replica of the SHIPPED-AFTER-THIS-FIX
gate: all-frames stats, window+budget deduction, plus the trim walker to
re-prove trim invariance; mask 1000 = shipped warm window 500+200+150+150,
budget 100 = ComputeCueBudgetMs(150, true)):

```bash
python3 - <<'EOF'
import json, os, wave
import numpy as np

BASE = '/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/fixtures'
FRAME = 320               # 20 ms @ 16 kHz
MASK_MS = 1000            # warm window: 500 preroll + 200 latency + 150 cue + 150 decay
BUD_MS = 100              # ComputeCueBudgetMs(150, true) = 150 - 50
REG = ['173b20b3', '525f0643', '003777a1', '4bf32da1']

def load(p):
    w = wave.open(p, 'rb')
    assert w.getnchannels() == 1 and w.getframerate() == 16000
    sw = w.getsampwidth(); raw = w.readframes(w.getnframes()); w.close()
    if sw == 2:
        return np.frombuffer(raw, dtype=np.int16).astype(np.float64) / 32768.0
    return np.frombuffer(raw, dtype=np.float32).astype(np.float64)

def rmsf(x):
    n = len(x) // FRAME
    if n == 0: return np.zeros(0)
    return np.sqrt((x[:n * FRAME].reshape(n, FRAME) ** 2).mean(axis=1))

def pct(s, p):
    if len(s) == 0: return 0.0
    return s[max(0, min(int(np.floor(p * (len(s) - 1))), len(s) - 1))]

def walker(rms, thr):
    isS = rms < thr; n = len(rms); kept = []; i = 0; KEEP = 30
    def app(s, l):
        if l <= 0: return
        if kept and kept[-1][0] + kept[-1][1] == s:
            kept[-1] = (kept[-1][0], kept[-1][1] + l)
        else:
            kept.append((s, l))
    while i < n:
        if not isS[i]:
            app(i, 1); i += 1; continue
        rs = i
        while i < n and isS[i]: i += 1
        rl = i - rs; L = rs > 0; R = i < n
        budget = ((1 if L else 0) + (1 if R else 0)) * KEEP
        if budget > 0 and rl > budget:
            if L: app(rs, KEEP)
            if R: app(i - KEEP, KEEP)
        else:
            app(rs, rl)
    return kept

def gate(rms, mask_ms, bud_ms):
    n = len(rms)
    if n == 0: return 'pass', None
    maskF = 0 if mask_ms <= 0 else min((mask_ms + 19) // 20, n)
    budF = 0 if bud_ms <= 0 else (bud_ms + 19) // 20
    s = np.sort(rms)
    if pct(s, 0.90) < 0.004: return 'DROP:P90-silent', None
    thr = min(max(3.0 * pct(s, 0.10), 0.002), 0.15 * pct(s, 0.90))
    v_all = int((rms >= thr).sum()); v_in = int((rms[:maskF] >= thr).sum())
    c_all = int(((rms >= thr) & (rms >= 0.02)).sum())
    c_in = int(((rms[:maskF] >= thr) & (rms[:maskF] >= 0.02)).sum())
    voiced = (v_all - min(budF, v_in)) * 20
    clear = (c_all - min(budF, c_in)) * 20
    if voiced < 600 and clear < 100: return 'DROP:voiced-floor', None
    return 'pass', walker(rms, thr)

fail = 0
for corpus in ['live-snapshot', 'frozen-0of91']:
    root = os.path.join(BASE, corpus)
    idx = json.load(open(os.path.join(root, 'index.json'), encoding='utf-8'))['entries']
    A = []; B = []; reg = {}; esc = []; trimdiff = 0; npass = 0
    for e in idx:
        p = os.path.join(root, e['wavRelativePath'].replace('\\', '/'))
        r = rmsf(load(p))
        pre, prek = gate(r, 0, 0)
        ded, dedk = gate(r, MASK_MS, BUD_MS)
        real = e['rawTranscript'] != ''
        if pre == 'pass' and ded != 'pass' and real: A.append(e['id'][:8])
        if pre != 'pass' and ded == 'pass': B.append(e['id'][:8])
        if pre == 'pass' and ded != 'pass' and not real: esc.append(e['id'][:8])
        if e['id'][:8] in REG: reg[e['id'][:8]] = ded
        if pre == 'pass' and ded == 'pass':
            npass += 1
            if prek != dedk: trimdiff += 1
    print(f'{corpus}: A(real pass->drop)={len(A)} {A}  B(drop->pass)={len(B)} {B}')
    print(f'{corpus}: empty-transcript pass->drop (cue-only escapes, EXPECTED) = {esc}')
    print(f'{corpus}: trim walker diffs across {npass} dual-passers = {trimdiff}')
    if A or B or trimdiff: fail = 1
    if corpus == 'live-snapshot':
        print('regression ids:', {k: reg.get(k, 'MISSING') for k in REG})
        if any(reg.get(k) != 'pass' for k in REG): fail = 1
print('VALIDATION:', 'GREEN' if fail == 0 else 'RED')
EOF
```

Expected output (dual-passer counts are informational and may differ by
±1; every other line must match, and the last line MUST be GREEN):

```
live-snapshot: A(real pass->drop)=0 []  B(drop->pass)=0 []
live-snapshot: empty-transcript pass->drop (cue-only escapes, EXPECTED) = ['cade05cf']
live-snapshot: trim walker diffs across 92 dual-passers = 0
regression ids: {'173b20b3': 'pass', '525f0643': 'pass', '003777a1': 'pass', '4bf32da1': 'pass'}
frozen-0of91: A(real pass->drop)=0 []  B(drop->pass)=0 []
frozen-0of91: empty-transcript pass->drop (cue-only escapes, EXPECTED) = ['67518b61']
frozen-0of91: trim walker diffs across 93 dual-passers = 0
VALIDATION: GREEN
```

This covers all four criteria: (1) the `regression ids` line — 4/4 pass;
(2) `B(drop->pass)=0` on both corpora — every genuinely-silent pre-mask
drop still drops; (3) the escape lines — `cade05cf` (live cue/noise-only)
and `67518b61` (the frozen beep-only escape of the 2026-08-02 20:18
class) still drop; (4) `A(real pass->drop)=0` on both corpora — zero
flips on real gate-passing dictations. `trim walker diffs = 0` re-proves
bit-identical trimming on real audio. If ANY line deviates, STOP — do not
edit the harness constants to make it pass; the implementation (or this
plan) is wrong and the run output is the evidence.

- [ ] **Step 2: Append the evidence section**

Append to `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`
(after the last section; house style: `##` heading with em-dash + ISO
date, flat `-` bullets wrapped ~72 cols, `file.cs:NN` pointers, SHOUTED
verdicts, exact ratios; substitute the two `<...>` placeholders with the
actual counts printed by Step 1 — placeholders must NOT be committed):

```markdown
## Start-cue gate mask — cue-budget deduction regression fix (2026-08-03)

- REGRESSION (introduced by the 2026-08-02 mask merge, `5f80e96`): the
  warm mask (1000 ms) starts at buffer t=0, which is 500 ms of pre-roll
  BEFORE the hotkey, so the window EXCLUSION blinded the gate to the
  first ~500 ms of post-hotkey time. Prompt short replies could not
  reach the 600 ms voiced / 100 ms clear floors and dropped whole.
  Owner-measured: 4/10 dictations dropped in the first 30 minutes on the
  build (pre-install baseline 3.7-8%). The four drops all contain
  spectrally-verified real speech (F0 ~92-95 Hz, matches transcribed
  controls): runs at 810-1110 ms for `173b20b3`/`525f0643`/`003777a1`,
  1730-1890 ms for `4bf32da1`. Production logs confirm all four dropped
  under `cue mask 1000 ms`. Replica-CONFIRMED offline: pre-era pass,
  mask-era drop (`voiced-floor` x3, `P90-silent` x1) for all four. The
  2026-08-02 plan's accepted residual only covered utterances ENTIRELY
  inside the mask; the field class is broader -- prompt short replies in
  or straddling the window (x3) plus a long quiet clip P90-silenced by
  post-mask stats starvation (x1) -- and the 0/91 corpus contained no
  dictations of either shape (its A6 caveat fired).
- FIX: cue-budget DEDUCTION replaces window exclusion
  (`SilenceTrimmer.cs`, `StartCueGateMask.ComputeCueBudgetMs`). In-window
  frames count normally; up to budget = measured cueMs -
  `CueBudgetMarginMs`(50) = 100 ms of the loudest in-window frames is
  deducted from the voiced and clear tallies. Beep-only recordings (in-
  window tally == the cue's own 120-140 ms clear pickup) deduct to below
  the floors and still drop; prompt speech keeps its surplus and passes.
- Budget sizing FALSIFIED the task's suggested cueMs + decay (300 ms):
  at 300 the deduction eats the user's own speech and 3/4 regression
  WAVs still drop (173b20b3 clear 0, 525f0643 clear 80, 003777a1 clear
  0). Sweep 0-400 ms in 20 ms steps over both frozen corpora: acceptance
  window 100-120 ms; 100 chosen (binding passer 003777a1 clear 120 vs
  the 100 floor; binding escape cade05cf voiced 580 vs the 600 floor —
  it survives at budgets <= 80).
- Stats exclusion REMOVED, not kept: post-mask percentiles starve on
  short clips (173b20b3: 3 decision frames) and P90-silence real speech
  (4bf32da1: post-mask P90 0.0012 < 0.004 — unfixable by any budget
  while the exclusion stood). Decision stats revert to all frames;
  decision threshold == trim threshold again, so trimming is
  bit-identical by construction. MaxFrameRms stays post-window so the
  cue cannot inflate the recalibration fields.
- Validation (offline replica over the frozen corpora at
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/fixtures/`,
  mask 1000 / budget 100): 4/4 regression WAVs PASS; 0 drop->pass flips
  on either corpus (all genuinely-silent drops UNCHANGED); cue-only
  escapes still drop (live `cade05cf`, frozen beep escape `67518b61` of
  the 2026-08-02 20:18 class; both spectrally verified noise-only, both
  passed under the PRE-mask-era gate); 0 pass->drop flips among real
  transcribed dictations on BOTH corpora (0/88 live, 0/93 frozen); trim
  walker diffs 0/<live dual-passer count> and 0/<frozen dual-passer
  count> — trimming invariant HOLDS.
- Unit pins: budget derivation from the MEASURED cue (150 -> 100, 300 ->
  250, disabled/unmeasured -> 0); beep-only zero-out; prompt-short-reply
  pass; fully-in-window utterance now passes (the old KnownResidual is
  FIXED); mask=0 byte-identity and trim-offset invariance unchanged.
- Residual risks ACCEPTED (2026-08-03 load-bearing pass; ledger +
  decision records in the evidence dir): cross-config cue pickup (louder
  speakers/reverb could push clear pickup >= 200 ms and reopen the
  beep-only escape; zero-pickup headphone-shaped sessions exist in-corpus
  with no near-floor passers), single-user corpus generalization (binding
  margins are 1-2 frames, on the two empty-transcript boundary clips
  only), budget linearity beyond the 150 ms asset (re-run the sweep on
  asset change), cold-session escapes (no cold clip exists; the mask
  construction keeps cue pickup in-window). Watch post-install drop log
  lines (which now include the cue budget).
- Evidence dir:
  `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/`
  (`reports/dedu-sweep.md` budget sweep, `reports/source-code.md`,
  `dedu.py`, frozen `fixtures/`).
```

- [ ] **Step 3: Full Linux suite (docs-only commit still gets the gate)**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

- [ ] **Step 4: Commit**

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "$(cat <<'EOF'
docs(plans): evidence — cue-budget deduction regression fix + archive validation

Records the 2026-08-03 regression (4/10 prompt short replies dropped by
the mask-window exclusion), the deduction fix, the budget sweep that
falsified cueMs+decay and sized the budget at cueMs-50, and the frozen-
corpus validation: 4/4 regression WAVs pass, 0 silent-drop flips, both
cue-only escapes still drop, 0/88 + 0/91 real-dictation flips, trim
invariant holds.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

### Task 5: Full Windows gate + gates bullet

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`
  (append the gates bullet to the Task 4 section)

**Interfaces:**
- Consumes: everything committed in Tasks 1–4.
- Produces: the done-signal for the branch (root session merges/installs;
  do NOT push).

- [ ] **Step 1: Run the Windows gate**

```bash
cd /home/dan/code/winpepper/.worktrees/cue-budget-deduction
./scripts/windows-gate.sh
```

Use a 20–30 minute timeout. Expected: exit 0 and `GATE: GREEN` (builds
`Winpepper.App` — the compile proof for Task 3 — plus all 9 test
projects, 12 project/TFM runs). UNC `MSB4025` and vsock interop failures
are KNOWN TRANSIENTS: retry the script (up to 3 times) before treating a
failure as real. A real test failure is a STOP: fix, re-run the Linux
suite, amend the relevant task's commit or add a fix commit, and re-gate.

- [ ] **Step 2: Append the gates bullet**

Append as the final bullet of the section added in Task 4 (fill the
exact counts from the actual runs — `<...>` placeholders must NOT be
committed):

```markdown
- Gates: `scripts/linux-tests.sh` GREEN (<grand total> tests, 9/9
  projects; Winpepper.Audio.Tests 111 with the 9 new deduction/budget
  facts); `scripts/windows-gate.sh` GATE: GREEN (App build OK — compiles
  the PipelineHost budget plumbing — <n>/12 test project/TFM runs OK,
  <total> tests<, transient retries disclosed if any>). Branch left
  local per workflow; root session merges, gates, installs.
```

- [ ] **Step 3: Full Linux suite + commit**

```bash
./scripts/linux-tests.sh
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "$(cat <<'EOF'
docs(plans): gates — linux suite + windows gate green for cue-budget deduction

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

Expected: `LINUX SUITE: GREEN`, then a clean commit. Do NOT push.
