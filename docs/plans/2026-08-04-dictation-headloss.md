# Dictation Head-Loss Elimination Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Stop users losing the first words of an utterance by (1) doubling the warm microphone pre-roll to 1000 ms with a 2 s capture ring, (2) compensating the pre-roll request for hotkey-observation lag, and (3) adding self-diagnosing fields to the dictation timing log line.

**Architecture:** All decision math (pre-roll request composition, head-speech detection, log formatting) lives in pure, Linux-testable classes (`Winpepper.Audio`, `Winpepper.Core`) following the `StartCueGateMask` idiom; the `#if WINDOWS` `PipelineHost` is reduced to plumbing. The silence-gate cue mask already scales automatically with the *actually seeded* pre-roll (`PipelineHost.cs:1741` passes `_lastSessionPrerollMs` into `StartCueGateMask.ComputeMaskMs`), so nothing in the gate arithmetic needs restructuring — but the larger mask window is **outside prior archive validation**, so Task 1 re-validates it against the frozen corpora before any constant changes.

**Tech Stack:** C# / .NET 9 (xUnit v3 + Shouldly for tests), Python 3 + numpy for the offline gate replication (pre-existing `dedu.py` replica).

**Base:** `main @ c71a40b`, branch `fix/dictation-headloss`, worktree `/home/dan/code/winpepper/.worktrees/dictation-headloss`. All paths below are relative to that worktree unless absolute.

## Global Constraints

- Linux suite green before **every** commit: `./scripts/linux-tests.sh` (NEVER `dotnet test`). Expect all 9 test projects green and `LINUX SUITE: GREEN` (currently 1667 tests at base).
- Full Windows gate before done: `./scripts/windows-gate.sh` → `GATE: GREEN` (12/12 project/TFM runs). UNC MSB4025 + vsock interop flakes are known transients — retry; the `ModelCardViewModelDispatchTests` LateByteReport test is a known flaky — retry once before treating as real.
- Never mix Linux- and Windows-side builds in the same bin/obj (use the scripts; the per-project commands in this plan replicate exactly what `linux-tests.sh` does).
- Every commit carries Amplifier co-author attribution (footer shown in each commit step, copied verbatim from repo precedent).
- Do NOT push; leave the branch local — the root session merges, gates, and installs.
- OUT OF SCOPE: M3 retrigger merge/debounce (parked deliberately: a continuation-window merge taxes every dictation to rescue ~0.5%). Trim margins. Gate constants (other than `WarmPrerollMs`). Streaming feed architecture. Cue playback and its runtime measurement. The `models-page-ux` branch — do not touch it.
- Zero-cost discipline for the log fields: no new threads/timers; reuse values the pipeline already has. The `Session started` line keeps its existing fields.
- README.md is the only end-user markdown doc; this plan and the evidence ledger under `docs/plans/` are working/agent docs.
- Mismatch rule: every `path:line` in this plan was re-verified 2026-08-04 at `c71a40b`. If code is not at a stated location, search by symbol under `src/` only (never `.worktrees/`) and STOP only if the claimed code cannot be found or contradicts the claim in substance.
- Artifact note (verified 2026-08-04): the investigation dirs `/tmp/headloss-inv/` and `/tmp/gate-inv3/` have evaporated (`/tmp` is ephemeral; the same happened to `/tmp/gate-inv/` before them). Their findings are inlined in this plan, and the gate-replication procedure uses the **preserved in-repo 2026-08-03 replica** at `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/` (`dedu.py` + two frozen 100-WAV corpora) — the direct ancestor of `trim.py`, verified runnable this session (`corpus=frozen-0of91 pad=500ms mask=1000 budget=100 clips=100 flips=0`).

## Background (from the completed root-cause investigation)

- **M1 pre-roll limit:** the warm WASAPI capture runs continuously with a ring holding exactly 1 s (`WarmWasapiRecorder.cs:35`); each session seeds a 500 ms pre-roll slice (`StartCueGateMask.WarmPrerollMs`, requested from `PipelineHost.cs:575`/`:1165` via `WarmCaptureBuffer.cs:62-75`). Speech begun >500 ms before the hotkey is never recorded (confirmed instance `8ec9e52c`).
- **M2 hotkey→capture latency:** hotkey handling is serialized behind the previous dictation's stop path (`PipelineHost.RunAsync:400-422`, `await HandleHotkey` at `:411`); observed lag p99 = 30 ms but max 1145 ms, >100 ms in 6/755 sessions. The pre-roll counts back from the DELAYED `StartSession`, so lag eats pre-keydown coverage 1:1 (confirmed instance `2b2e4384`: 617 ms lag + retrigger → 240 ms unrecorded hole).
- **M3 hold-key release blips** split dictations — OUT OF SCOPE this run (parked, recorded above).
- **Verified interactions (2026-08-04, do not re-derive):** the mask arithmetic scales automatically (`PipelineHost.cs:1741` feeds the ACTUAL seed); the full untrimmed buffer (pre-roll head included) is archived on all four archive paths (`PipelineHost.cs:673/1075/1262/1659` set `Samples16k = samples`; `HistoryArchiver.cs:45` → `WavWriter.WriteMono16kInt16`); `rec=` measures post-hotkey wall time only (`_recordStopwatch` starts AFTER `StartSession` returns at `:576/:1166`); AssemblyAI streaming chunks oversized frames to ≤ 16000 samples per send (`AssemblyAiStreamingTranscriber.cs:176-182`, pinned by `AssemblyAiStreamingTests.cs:59` `Push_SplitsAnOversizedBufferIntoAtMost1000MsMessages` at 40 000 samples — larger than this plan's 32 000-sample worst case).
- **One genuine interaction found during planning (Task 8):** `ParakeetStreamingSession`'s leading-silence latch tests the **whole pushed buffer's** RMS (`ParakeetStreamingSession.cs:121`, `Rms:253-259`). The speech duration needed to unlatch scales linearly with buffer length, so a 4× longer pre-roll makes quiet onsets 4× harder to detect; a miss discards the entire pre-roll INCLUDING its onset — a new head-loss path for the local streamed transcript that would partially undo Change 1. Task 8 converts the latch to per-20 ms-frame granularity and feeds from the onset frame.

## File Structure

| File | Responsibility in this plan |
|---|---|
| `src/Winpepper.Audio/StartCueGateMask.cs` | Modify: `WarmPrerollMs` 500 → 1000; refresh stale evidence prose |
| `src/Winpepper.Audio/WarmWasapiRecorder.cs` | Modify: ring capacity 1 s → 2 s; refresh "~500 ms" prose |
| `src/Winpepper.Audio/WarmCaptureBuffer.cs` | Modify: doc prose only |
| `src/Winpepper.Audio/PrerollRequest.cs` | **Create:** pure lag-compensation request math (the `StartCueGateMask` idiom) |
| `src/Winpepper.Audio/SilenceTrimmer.cs` | Modify: `Trim` gains `prerollMs` param; head-speech scan; `TrimResult.HeadSpeechAtMs`/`HeadClipped` |
| `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` | Modify: 5 new nullable fields + `FormatLine` emission |
| `src/Winpepper.Core/Settings/AppSettings.cs` | Modify: comment prose only (`:85`) |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify: lag-compensated request at both arms; worst-case startup log; host fields; stamping funnel; comment prose |
| `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` | Modify: per-frame leading-silence latch + onset-frame feed; stale comment |
| `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs` | Modify: re-pin constants (500→1000, 1000→1500) |
| `tests/Winpepper.Audio.Tests/PrerollRequestTests.cs` | **Create:** pins the request math |
| `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` | Modify: 6 new head-speech facts |
| `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` | Modify: golden line + omission/bool facts |
| `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` | Modify: 2 new latch facts |
| `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` | Append-only ledger: new 2026-08-04 section (replication numbers, schema additions, residual risks, gates) |
| `/home/dan/code/winpepper/.worktrees/.the-usual-logs/dictation-headloss/preroll-pad-check.py` | **Create (outside repo, absolute path per house precedent):** padded-archive gate replication script |

Key signatures that must stay intact (consumed by the plan, never changed):

- `IWarmAudioRecorder.StartSession(int includePrerollMs)` → `int` actual seeded ms (`IWarmAudioRecorder.cs:33`); recorder clamps `Math.Max(0, …)`, buffer clamps `Math.Min(prerollSamples, _ring.Count)` (`WarmCaptureBuffer.cs:68`).
- `StartCueGateMask.ComputeMaskMs(int actualPrerollMs, int cueMs, bool soundsEnabled)` = `max(preroll,0) + 200 + cue + 150` (`StartCueGateMask.cs:75-79`).
- `StartCueGateMask.ComputeCueBudgetMs(int cueMs, bool soundsEnabled)` = `max(cue − 50, 0)` — untouched.

---

### Task 1: Padded-Archive Gate Replication (validation gate — runs BEFORE any code change)

**Files:**
- Create: `/home/dan/code/winpepper/.worktrees/.the-usual-logs/dictation-headloss/preroll-pad-check.py` (absolute path; this artifact dir is the house home for replication scripts, per the cue-budget-deduction precedent)
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (append a new dated section — the file is an append-only ledger; NEVER edit earlier sections)

**Interfaces:**
- Consumes: `/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/dedu.py` (offline gate replica: `load(path)`, `frame_rms(x)`, `trim_dedu(rms, mask_ms, budget_ms)` returning a dict with `silent: bool` and `why: str`), plus its two frozen corpora under `fixtures/{frozen-0of91,live-snapshot}/index.json` (ground truth: `entry["rawTranscript"] != ""` = real dictation).
- Produces: measured flip counts (both directions, both corpora) at the NEW mask window that Task 2's doc-comment updates and Task 9's evidence section cite. **HALT criterion** for the whole plan.

**Why:** `StartCueGateMask.cs:23-27` records that the current gate was archive-validated at a **1000 ms** warm window (0/91 flips). With a 1000 ms pre-roll the warm window becomes 1000 + 200 + 150 (cue) + 150 = **1500 ms — outside that validation**. The cue-budget deduction is pre-roll-independent (in-window frames still count; only the cue's worth is deducted), so the expectation is 0 flips, but this must be *measured*, not assumed. The honest simulation of the change is: baseline = today's gate (`mask=1000`) on the original buffer vs. new = the gate at `mask=1500` on the buffer with 500 ms of silence prefixed (the extra pre-roll the change adds).

- [ ] **Step 1: Write the replication script**

Write exactly this to `/home/dan/code/winpepper/.worktrees/.the-usual-logs/dictation-headloss/preroll-pad-check.py`:

```python
#!/usr/bin/env python3
"""Padded-archive gate replication for the 500 -> 1000 ms warm pre-roll change.

Baseline: shipped gate, mask=1000 (500 preroll + 200 + 150 cue + 150), budget=100,
on the original frozen buffers. New: gate at mask=1500 (1000 preroll + same margins)
on the same buffers with 500 ms of silence prefixed (the extra pre-roll the change
adds). Ground truth per clip: rawTranscript != "" = real dictation.

Uses the preserved 2026-08-03 replica (dedu.py) — the ancestor of the evaporated
/tmp/gate-inv3/trim.py — faithful to SilenceTrimmer by construction (20 ms frames,
index-floor percentiles, deduction semantics).
"""
import json
import os
import sys

import numpy as np

BASE = "/home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction"
sys.path.insert(0, BASE)
import dedu  # noqa: E402

PAD_MS = 500       # extra pre-roll the change adds (500 -> 1000)
MASK_BASE = 1000   # shipped warm mask: 500 + 200 + 150 + 150
MASK_NEW = 1500    # new warm mask:    1000 + 200 + 150 + 150
BUDGET = 100       # unchanged: cue 150 - margin 50

pad = np.zeros(PAD_MS * 16, dtype=np.float64)  # 16 samples/ms @ 16 kHz

halt = False
for corpus in ("frozen-0of91", "live-snapshot"):
    root = os.path.join(BASE, "fixtures", corpus)
    with open(os.path.join(root, "index.json"), encoding="utf-8") as fh:
        idx = json.load(fh)["entries"]
    n = 0
    regressions = []   # real dictation pass -> drop  (HALT if any)
    admissions = []    # silent clip drop -> pass     (HALT if any)
    improvements = []  # real dictation drop -> pass  (record, proceed)
    tightened = []     # silent clip pass -> drop     (record, proceed)
    for e in idx:
        p = os.path.join(root, e["wavRelativePath"].replace("\\", "/"))
        if not os.path.exists(p):
            continue
        x = dedu.load(p)
        base = dedu.trim_dedu(dedu.frame_rms(x), MASK_BASE, BUDGET)
        new = dedu.trim_dedu(dedu.frame_rms(np.concatenate([pad, x])), MASK_NEW, BUDGET)
        n += 1
        if base["silent"] == new["silent"]:
            continue
        real = e["rawTranscript"] != ""
        rec = (e["id"][:8],
               "pass" if not base["silent"] else "DROP:" + base["why"],
               "pass" if not new["silent"] else "DROP:" + new["why"])
        if real and new["silent"]:
            regressions.append(rec)
        elif not real and not new["silent"]:
            admissions.append(rec)
        elif real:
            improvements.append(rec)
        else:
            tightened.append(rec)
    print(f"corpus={corpus} clips={n} pad={PAD_MS}ms mask {MASK_BASE}->{MASK_NEW} budget={BUDGET}")
    print(f"  real pass->drop (REGRESSION, halt): {len(regressions)} {regressions}")
    print(f"  silent drop->pass (ADMISSION, halt): {len(admissions)} {admissions}")
    print(f"  real drop->pass (improvement): {len(improvements)} {improvements}")
    print(f"  silent pass->drop (tightened): {len(tightened)} {tightened}")
    halt = halt or regressions or admissions
sys.exit(1 if halt else 0)
```

- [ ] **Step 2: Run it**

Run: `python3 /home/dan/code/winpepper/.worktrees/.the-usual-logs/dictation-headloss/preroll-pad-check.py`

Expected: exit code 0 and, for BOTH corpora, `real pass->drop (REGRESSION, halt): 0 []` and `silent drop->pass (ADMISSION, halt): 0 []`, each over ~100 clips. (A smoke run at `mask=1000` both sides showed 0 flips on frozen-0of91; the 1500 window is what is genuinely being validated here.)

**If the script exits 1 (any regression or admission): STOP the plan.** Record the offending clip IDs and directions in the evidence section (Step 3's format), do not proceed to Task 2, and report the halt — the pre-roll change is not safe at the current gate constants and needs a decision, not a workaround.

- [ ] **Step 3: Append the evidence section**

Append to the END of `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (after the 2026-08-03 section; touch nothing above), filling every `<N>`/`<list>` slot from Step 2's actual printed output:

```markdown
## Dictation head-loss — pre-roll extension + lag compensation + timing diagnostics (2026-08-04)

Plan: docs/plans/2026-08-04-dictation-headloss.md. Base for the fix branch: main @ c71a40b.
Investigation artifacts: /tmp/headloss-inv/ and /tmp/gate-inv3/ evaporated before execution
(ephemeral /tmp, same fate as /tmp/gate-inv/); replication uses the preserved 2026-08-03
replica at /home/dan/code/winpepper/.worktrees/.the-usual-logs/cue-budget-deduction/
(dedu.py, two frozen 100-WAV corpora), script preserved at
/home/dan/code/winpepper/.worktrees/.the-usual-logs/dictation-headloss/preroll-pad-check.py.

Mechanisms (from the completed investigation, inlined in the plan): M1 — 500 ms pre-roll
request vs speech begun earlier (confirmed instance 8ec9e52c); M2 — hotkey lag p99 30 ms /
max 1145 ms, >100 ms in 6/755 sessions, eats pre-keydown coverage 1:1 (confirmed instance
2b2e4384: 617 ms lag + retrigger -> 240 ms hole); M3 — release blips, PARKED this run
(continuation-window merge taxes every dictation to rescue ~0.5%).

### Padded-archive gate replication (pre-change validation gate)

Baseline mask 1000 / budget 100 on original buffers vs mask 1500 / budget 100 with 500 ms
digital silence prefixed (the honest simulation of a fully-seeded 1000 ms pre-roll):

- frozen-0of91 (<N> clips): real pass->drop <N>/<N>, silent drop->pass <N>/<N>,
  real drop->pass <N>, silent pass->drop <N> <list clip IDs for any nonzero>
- live-snapshot (<N> clips): real pass->drop <N>/<N>, silent drop->pass <N>/<N>,
  real drop->pass <N>, silent pass->drop <N> <list clip IDs for any nonzero>

Acceptance (plan Task 1): zero real pass->drop and zero silent drop->pass on both corpora — <PASS/FAIL>.

- Residual risks ACCEPTED (this subsection): the pad is digital zeros, not room tone — real
  extra pre-roll carries room tone that lowers P10 (and thr), which zeros cannot model;
  frozen-0of91 is a single-user corpus (standing residual recorded at :400-409 of this file).
```

(The rest of the 2026-08-04 section — schema additions, verified interactions, gates — is appended by Tasks 9's steps; this ledger grows within the run exactly as the 07-30 run's sections did.)

- [ ] **Step 4: Linux suite (pre-commit rule)**

Run: `cd /home/dan/code/winpepper/.worktrees/dictation-headloss && ./scripts/linux-tests.sh`
Expected: `LINUX SUITE: GREEN` (no code changed; this pins the base is green before the first commit).

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-headloss
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence — padded-archive gate replication for the 1000 ms pre-roll window

Baseline mask 1000 vs mask 1500 over both frozen corpora with 500 ms silence
prefixed; validates the enlarged silence-gate window before the constant
changes land. Script preserved in .the-usual-logs/dictation-headloss/.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 2: Pre-Roll Constant 500 → 1000 ms, Capture Ring 1 s → 2 s

**Files:**
- Modify: `src/Winpepper.Audio/StartCueGateMask.cs:41` (constant), `:23-27` + `:12` region (stale evidence prose)
- Modify: `src/Winpepper.Audio/WarmWasapiRecorder.cs:35` (ring), `:10` and `:145` (prose)
- Modify: `src/Winpepper.Audio/WarmCaptureBuffer.cs:8` (prose)
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs:165` (prose), `src/Winpepper.Core/Settings/AppSettings.cs:85` (prose)
- Test: `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs:19-24`, `:82`

**Interfaces:**
- Consumes: Task 1's measured flip counts (cited in the refreshed doc comments).
- Produces: `StartCueGateMask.WarmPrerollMs == 1000` (`public const int`); `WarmWasapiRecorder.RingCapacitySamples == SampleRate16k * 2` (32 000 samples ≈ 128 KB backing store; `private const`, consumed only inside the recorder). The single-source property is preserved: `WarmPrerollMs` keeps its name (it still names exactly what it feeds — the mask arithmetic AND the capture request) and stays THE only home of the number.

- [ ] **Step 1: Re-pin the constants in the tests (failing first)**

In `tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs`, the fact at `:19-24` currently asserts:

```csharp
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(1000);
        StartCueGateMask.ComputeMaskMs(500, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.WarmPrerollMs
            + StartCueGateMask.CueStartLatencyMarginMs
            + 150
            + StartCueGateMask.CueDecayMarginMs);
```

Replace those two asserts with (and update the fact's evidence comment to cite the 2026-08-04 padded replication and its numbers from Task 1):

```csharp
        // With the shipped 150 ms cue and a fully-seeded warm pre-roll:
        // 1000 + 200 + 150 + 150 = 1500 ms. Validated 2026-08-04 by padded-
        // archive replication (both frozen corpora, +500 ms silence prefixed,
        // mask 1000->1500): 0 real pass->drop, 0 silent drop->pass — see
        // docs/plans/2026-07-29-cleanup-asr-contention-evidence.md (2026-08-04
        // section). The 1000/150 here are TEST inputs, not production
        // constants — production feeds the recorder's actually-seeded pre-roll
        // and the runtime-measured WAV.
        StartCueGateMask.ComputeMaskMs(1000, 150, soundsEnabled: true).ShouldBe(1500);
        StartCueGateMask.ComputeMaskMs(1000, 150, soundsEnabled: true).ShouldBe(
            StartCueGateMask.WarmPrerollMs
            + StartCueGateMask.CueStartLatencyMarginMs
            + 150
            + StartCueGateMask.CueDecayMarginMs);
```

And at `:82` change `StartCueGateMask.WarmPrerollMs.ShouldBe(500);` to `StartCueGateMask.WarmPrerollMs.ShouldBe(1000);` (update its comment likewise: the request is 1000 ms, raised 2026-08-04 from 500 — speech begun >500 ms before the hotkey was never recorded, confirmed instance 8ec9e52c). The facts at `:35` (`ComputeMaskMs(0, 150, …) == 500`) and `:44` (`ComputeMaskMs(300, 150, …) == 800`) are pre-roll-independent — leave them.

- [ ] **Step 2: Run to verify the pins fail**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-headloss
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows"
```
Expected: FAIL — `ComputeMaskMs_WarmFullPreroll_AddsPrerollAndBothMargins` (1500 expected, 1000 actual) and `WarmPrerollMs_IsThePipelinesPrerollRequest` (1000 expected, 500 actual).

- [ ] **Step 3: Change the two constants**

In `src/Winpepper.Audio/StartCueGateMask.cs:41`:

```csharp
    public const int WarmPrerollMs = 1000;
```

and extend its `<summary>` (keep the existing single-source sentences) with: `Raised 2026-08-04 from 500: speech begun &gt;500 ms before the hotkey was never recorded (head-loss investigation, confirmed instance 8ec9e52c). PipelineHost may request MORE than this per session (hotkey-lag compensation, see PrerollRequest); this constant remains the base and the mask still scales from the ACTUAL seed.`

In `src/Winpepper.Audio/WarmWasapiRecorder.cs:35`:

```csharp
    private const int RingCapacitySamples = SampleRate16k * 2; // ~2 s of history: the 1000 ms
    // request plus up to 1000 ms of hotkey-lag compensation (PrerollRequest) must never race
    // the ring edge. Cost: ~128 KB float backing store; the per-callback RemoveRange shift
    // (~800 floats per 50 ms WASAPI callback) doubles — accepted, measured region is O(n) List.
```

- [ ] **Step 4: Refresh the stale evidence prose (same files, load-bearing claims)**

In `src/Winpepper.Audio/StartCueGateMask.cs` class doc: `:27` says "with the current 150 ms asset the warm window is 1000 ms" → change to `1500 ms` and append to that sizing-evidence paragraph: `Re-validated 2026-08-04 for the 1000 ms pre-roll by padded-archive replication (+500 ms silence prefixed, mask 1000->1500, both frozen corpora): 0 real pass->drop, 0 silent drop->pass — evidence doc, 2026-08-04 section.` Leave the historical "flips 4/91 at 1000 ms" cold-mode sentence at `:23-24` intact (it describes a 2026-08-02 measurement of a different design, still true as history).

Prose-only updates of "~500 ms" to "~1 s" (or "the seeded pre-roll") where they describe the pre-roll: `WarmWasapiRecorder.cs:10` and `:145`, `WarmCaptureBuffer.cs:8`, `SilenceTrimmer.cs:165`, `AppSettings.cs:85`. (PipelineHost's `:534/:537/:1130` comments are updated in Task 4 together with the code they describe; `ParakeetStreamingSession.cs:67` in Task 8.)

- [ ] **Step 5: Run to verify the pins pass**

Same commands as Step 2. Expected: PASS (whole project green).

- [ ] **Step 6: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-headloss
git add src/Winpepper.Audio/StartCueGateMask.cs src/Winpepper.Audio/WarmWasapiRecorder.cs \
        src/Winpepper.Audio/WarmCaptureBuffer.cs src/Winpepper.Audio/SilenceTrimmer.cs \
        src/Winpepper.Core/Settings/AppSettings.cs tests/Winpepper.Audio.Tests/StartCueGateMaskTests.cs
git commit -m "feat(audio): raise warm pre-roll request to 1000 ms; grow capture ring to 2 s

Speech begun >500 ms before the hotkey was never recorded (M1, confirmed
instance 8ec9e52c). The ring doubles so the request never races the ring
edge and lag compensation has headroom. Gate window validated at 1500 ms by
padded-archive replication (evidence doc, 2026-08-04 section).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 3: `PrerollRequest` — Lag-Compensated Request Math (pure helper)

**Files:**
- Create: `src/Winpepper.Audio/PrerollRequest.cs`
- Test: `tests/Winpepper.Audio.Tests/PrerollRequestTests.cs` (create)

**Interfaces:**
- Consumes: `StartCueGateMask.WarmPrerollMs` (== 1000 after Task 2).
- Produces: `Winpepper.Audio.PrerollRequest` — `public const int LagCompensationCapMs = 1000`, `public const int MaxRequestMs` (= 2000), `public static int ComputeRequestMs(int observedLagMs)`. Task 4 calls `ComputeRequestMs` at both hotkey arms and `MaxRequestMs` in the startup log.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Audio.Tests/PrerollRequestTests.cs`:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class PrerollRequestTests
{
    [Fact]
    public void ComputeRequestMs_ZeroLag_RequestsBasePreroll()
    {
        // No lag: the request is exactly the single-source base constant.
        PrerollRequest.ComputeRequestMs(0).ShouldBe(StartCueGateMask.WarmPrerollMs);
        PrerollRequest.ComputeRequestMs(0).ShouldBe(1000);
    }

    [Fact]
    public void ComputeRequestMs_TypicalLag_AddsLagOneToOne()
    {
        // M2: lag eats pre-keydown coverage 1:1 (2b2e4384: 617 ms lag ->
        // 240 ms unrecorded hole), so every observed ms is requested back.
        PrerollRequest.ComputeRequestMs(30).ShouldBe(1030);
        PrerollRequest.ComputeRequestMs(617).ShouldBe(1617);
    }

    [Fact]
    public void ComputeRequestMs_HugeLag_ClampsToWhatTheRingCanServe()
    {
        // Observed max lag 1145 ms (755-session survey); the 2 s ring can
        // serve at most base 1000 + 1000, so the lag contribution clamps.
        PrerollRequest.ComputeRequestMs(1145).ShouldBe(PrerollRequest.MaxRequestMs);
        PrerollRequest.ComputeRequestMs(5000).ShouldBe(2000);
    }

    [Fact]
    public void ComputeRequestMs_NegativeLag_ContributesNothing()
    {
        // Clock skew can make hook->handler deltas negative; never shrink
        // the base request because of it.
        PrerollRequest.ComputeRequestMs(-50).ShouldBe(1000);
    }

    [Fact]
    public void MaxRequestMs_IsBasePlusCap()
    {
        // Keep in lockstep with WarmWasapiRecorder.RingCapacitySamples (2 s):
        // MaxRequestMs must never exceed what a full ring can seed.
        PrerollRequest.MaxRequestMs.ShouldBe(
            StartCueGateMask.WarmPrerollMs + PrerollRequest.LagCompensationCapMs);
        PrerollRequest.MaxRequestMs.ShouldBe(2000);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-headloss
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAIL — `PrerollRequest` does not exist.

- [ ] **Step 3: Implement the helper**

Create `src/Winpepper.Audio/PrerollRequest.cs`:

```csharp
namespace Winpepper.Audio;

/// <summary>
/// Composes the warm pre-roll REQUEST for a dictation session: the base
/// <see cref="StartCueGateMask.WarmPrerollMs"/> plus compensation for the
/// hotkey-observation lag (hook timestamp -> pipeline handler; hotkey events
/// are handled serially behind the previous dictation's stop path). The
/// pre-roll counts back from the DELAYED StartSession, so every ms of lag
/// eats pre-keydown coverage 1:1 (2026-08-03/04 head-loss investigation, M2:
/// lag p99 30 ms, max 1145 ms; confirmed instance 2b2e4384 — 617 ms lag +
/// retrigger = 240 ms unrecorded hole). The lag contribution is clamped to
/// <see cref="LagCompensationCapMs"/> so the request never exceeds what the
/// capture ring can serve; the recorder still reports the ACTUAL seeded
/// pre-roll, so the silence-gate mask (StartCueGateMask.ComputeMaskMs) keeps
/// scaling with reality, not with this request.
/// </summary>
public static class PrerollRequest
{
    /// <summary>
    /// Maximum ms of observed hotkey lag the request may add on top of
    /// WarmPrerollMs. Equals ring capacity (2 s, WarmWasapiRecorder.
    /// RingCapacitySamples) minus the 1000 ms base — keep the two in
    /// lockstep when either changes.
    /// </summary>
    public const int LagCompensationCapMs = 1000;

    /// <summary>
    /// Worst-case request (fully clamped lag). Feeds the startup worst-case
    /// mask observability line, which must remain a ceiling now that
    /// per-session requests vary with lag.
    /// </summary>
    public const int MaxRequestMs = StartCueGateMask.WarmPrerollMs + LagCompensationCapMs;

    /// <summary>
    /// The includePrerollMs to pass to IWarmAudioRecorder.StartSession.
    /// Negative lag (clock skew across the hook/handler timestamps)
    /// contributes 0 — never shrink the base request.
    /// </summary>
    public static int ComputeRequestMs(int observedLagMs)
        => StartCueGateMask.WarmPrerollMs + Math.Clamp(observedLagMs, 0, LagCompensationCapMs);
}
```

- [ ] **Step 4: Run to verify they pass**

Same build command, then:
```bash
dotnet exec tests/Winpepper.Audio.Tests/bin/Release/net9.0/Winpepper.Audio.Tests.dll -notrait "Platform=Windows"
```
Expected: PASS, project green.

- [ ] **Step 5: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Audio/PrerollRequest.cs tests/Winpepper.Audio.Tests/PrerollRequestTests.cs
git commit -m "feat(audio): PrerollRequest — lag-compensated pre-roll request math

Pure decision math per the StartCueGateMask idiom: base 1000 ms + observed
hotkey lag, lag contribution clamped to the 2 s ring's headroom (1000 ms).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 4: Wire Lag Compensation at Both Hotkey Arms (PipelineHost)

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — hold arm `:529-530` (log) and `:575` (request); toggle arm `:1125-1126` (log) and `:1165` (request); startup worst-case log `:176-187`; stale comments `:533-538` and `:1129-1130`.

`PipelineHost.cs` is `#if WINDOWS` — it cannot be compile-checked on Linux. The Linux suite still gates the commit (pure projects); Windows compilation is verified by Task 9's `./scripts/windows-gate.sh`. Follow the code below exactly; it uses only symbols proven above.

**Interfaces:**
- Consumes: `PrerollRequest.ComputeRequestMs(int)`, `PrerollRequest.MaxRequestMs` (Task 3); `HotkeyEvent.Timestamp` (`DateTimeOffset`, stamped in the hook callback — `HotkeyHook.cs:320` HoldDown, `:307` Toggle).
- Produces: hoisted locals `hotkeyLagMs` (hold arm) / `hotkeyLagMs2` (toggle arm) holding the keydown→handler lag in ms — Task 7 reads these into a host field. The `Session started` log template is UNCHANGED (same fields, same text); only the inline expression is hoisted.

- [ ] **Step 1: Hoist the lag and compensate the request — hold arm**

At `:529-530` the hold arm currently logs the lag inline:

```csharp
                _log.LogInformation("Session started (hold) {SessionId} (hotkey observed {LagMs} ms before handling)",
                    _currentSessionId, (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds);
```

Replace with:

```csharp
                // Hoisted so the SAME lag value drives the log line, the
                // lag-compensated pre-roll request below, and the timing
                // line's arm_latency= (M2: the pre-roll counts back from this
                // DELAYED handling moment, so lag eats pre-keydown coverage
                // 1:1 — request it back, clamped to the ring's headroom).
                var hotkeyLagMs = (int)(DateTimeOffset.UtcNow - evt.Timestamp).TotalMilliseconds;
                _log.LogInformation("Session started (hold) {SessionId} (hotkey observed {LagMs} ms before handling)",
                    _currentSessionId, hotkeyLagMs);
```

At `:575` change the request:

```csharp
                _lastSessionPrerollMs = _warmRecorder!.StartSession(
                    includePrerollMs: Winpepper.Audio.PrerollRequest.ComputeRequestMs(hotkeyLagMs));
```

(Match the file's existing qualification style: `TrimForTranscription` at `:1739-1742` uses unqualified `StartCueGateMask` — if `Winpepper.Audio` is already imported/resolvable there, write `PrerollRequest.ComputeRequestMs(hotkeyLagMs)` unqualified for consistency.)

Update the stale comment at `:533-538`: it says "raises the StartCueGateMask.WarmPrerollMs (500 ms) pre-roll request" and "permanently loses the first ~500 ms" — reword to "raises the lag-compensated pre-roll request (PrerollRequest.ComputeRequestMs: WarmPrerollMs 1000 ms + observed hotkey lag, clamped)" and "permanently loses the first ~1-2 s".

- [ ] **Step 2: Same at the toggle arm**

At `:1125-1126` (log) and `:1165` (request) apply the identical transformation with the local named `hotkeyLagMs2` (the toggle arm's locals carry the `2` suffix by house convention — see `settingsForStream2`, `routeBlockReason2`). Update the twin comment at `:1129-1130` ("(500 ms) pre-roll request is not dropped") the same way.

- [ ] **Step 3: Keep the startup worst-case log an honest ceiling**

At `:176-187` the ctor logs the worst-case mask from `StartCueGateMask.WarmPrerollMs`. Per-session requests can now exceed that (lag compensation), so the "worst case" would silently become a floor. Change the call and args (template text edits shown inline):

```csharp
        var startCueMs = sounds.StartCueMs;
        if (startCueMs > 0)
            _log.LogInformation(
                "start cue measured {CueMs} ms; worst-case warm silence-gate cue mask {WorstCaseMaskMs} ms (max preroll request {PrerollMs} incl. hotkey-lag compensation + start latency {LatencyMs} + cue + decay {DecayMs}; per-dictation mask uses the actually-seeded preroll; sounds enabled {Enabled})",
                startCueMs,
                StartCueGateMask.ComputeMaskMs(Winpepper.Audio.PrerollRequest.MaxRequestMs, startCueMs, sounds.Enabled),
                Winpepper.Audio.PrerollRequest.MaxRequestMs,
                StartCueGateMask.CueStartLatencyMarginMs,
                StartCueGateMask.CueDecayMarginMs,
                sounds.Enabled);
```

(The `else` warning branch at `:186-187` is untouched. `_lastSessionPrerollMs`'s field doc at `:43-54` mentions "the WarmPrerollMs request" — extend it to "the PrerollRequest.ComputeRequestMs request (WarmPrerollMs + clamped hotkey lag)". The mask consumption at `:1741` needs NO change: it already uses the ACTUAL seed, which now honestly includes any lag-compensated extra the ring could serve.)

- [ ] **Step 4: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN` (PipelineHost is Windows-only; the suite proves the pure projects are unaffected — Windows compile lands in Task 9's gate).

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): request lag-compensated pre-roll at both hotkey arms

Hoists the already-measured hotkey lag into a local at each arm and requests
WarmPrerollMs + lag (clamped to the ring headroom) so serialized/delayed
handling no longer eats pre-keydown coverage (M2). Startup worst-case mask
line now uses MaxRequestMs so it stays a ceiling.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 5: Head-Speech Diagnostics in `SilenceTrimmer`

**Files:**
- Modify: `src/Winpepper.Audio/SilenceTrimmer.cs` — `Trim` signature (`:139`), `TrimResult` struct (`:4-51`), scan insertion after the rms/maskFrames computation (`:141-180` region), all `TrimResult` construction sites (`:147`, `:223`, `:274`, `:352` — the exact set is every `new TrimResult` in the file; the compiler enforces completeness via `required`)
- Test: `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (append 6 facts)

**Interfaces:**
- Consumes: existing internals — `rms[]` per-20 ms-frame array, `maskFrames = ceil(maskMs/20)` (`:179`), `ClearSpeechRmsFloor = 0.02` (`:111`), `FrameMs = 20` (`:71`).
- Produces: `public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0, int prerollMs = 0)` — existing 2- and 3-arg callers compile unchanged. `TrimResult.HeadSpeechAtMs` (`required int?`): ms offset from buffer t=0 of the first frame ≥ the clear-speech floor OUTSIDE the cue-pickup window, null when none. `TrimResult.HeadClipped` (`bool?`, computed): `HeadSpeechAtMs < 40` when set, null otherwise. Task 7 passes `_lastSessionPrerollMs` as `prerollMs` and stamps both onto the timing line.

**Semantics being implemented (the design decision, resolved here):** buffer t=0 sits `prerollMs` BEFORE the hotkey; the app's own start cue can only be picked up AFTER the hotkey (measured onset `prerollMs+92..144` ms, pickup ending by `prerollMs+~361` ms — `StartCueGateMask` class doc). So the *cue-pickup window* is the band `[prerollMs, maskMs)` — the post-hotkey part of the mask — and the pre-roll head `[0, prerollMs)` plus everything at/after `maskMs` is scannable. This keeps `head_clipped` reachable (frames 0–1 are pre-hotkey audio the cue cannot contaminate) while never mistaking the cue for user speech, and uses only existing constants via `maskMs`. When `maskMs == 0` (cue disabled/unmeasured) nothing was played — scan everything. Known consequence, accepted: in cold mode (`prerollMs == 0`) with sounds on, the exclusion covers `[0, maskMs)`, so `head_speech_at ≥ maskMs` and `head_clipped` never fires — cold mode has no pre-hotkey audio, so head-clip detection is meaningless there anyway. The fields are pure diagnostics: they must not influence the gate verdict, trimming offsets, or the transcribed audio.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (uses the file's existing `Dc(rms, ms)` / `Join(...)` vocabulary; every fact carries its arithmetic in a comment, per house convention):

```csharp
    [Fact]
    public void Trim_SpeechAtBufferStart_ReportsHeadSpeechAtZero_AndClipped()
    {
        // Head-loss signature (M1): speech already in progress when the
        // pre-roll ring was seeded. preroll=1000, mask=1500 (1000+200+150+150).
        // Loud frames 0-1 (40 ms @ 0.05) sit in the pre-roll head [0,1000) —
        // OUTSIDE the cue-pickup band [1000,1500) — so they are scannable.
        // Gate: 125 frames, P90 idx floor(0.9*124)=111 -> 0.001 < 0.004 ->
        // P90-silent DROP; head fields must be populated even on the drop path.
        var buf = Join(Dc(0.05, 40), Dc(0.001, 2460));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.IsSilent.ShouldBeTrue();
        r.HeadSpeechAtMs.ShouldBe(0);
        r.HeadClipped.ShouldBe(true);
    }

    [Fact]
    public void Trim_SpeechOnlyInsideCuePickupWindow_OmitsHeadFields()
    {
        // The only clear-tier energy is where the cue lands: frames 50-56
        // (1000-1140 ms), inside the excluded band [1000,1500) at
        // preroll=1000/mask=1500. head_speech_at must NOT report the app's
        // own beep as user speech.
        var buf = Join(Dc(0.001, 1000), Dc(0.05, 140), Dc(0.001, 1360));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBeNull();
        r.HeadClipped.ShouldBeNull();
    }

    [Fact]
    public void Trim_SpeechAfterMask_ReportsPostMaskOffset_NotClipped()
    {
        // Speech starts exactly at the mask edge: frames 75-109 (1500-2200 ms,
        // 700 ms @ 0.05). Exclusion [50,75) skipped; first scannable clear
        // frame is 75 -> 1500 ms. Gate passes (voiced 700 >= 600).
        var buf = Join(Dc(0.001, 1500), Dc(0.05, 700), Dc(0.001, 300));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.IsSilent.ShouldBeFalse();
        r.HeadSpeechAtMs.ShouldBe(1500);
        r.HeadClipped.ShouldBe(false);
    }

    [Fact]
    public void Trim_HeadSpeechAt20Ms_IsClipped()
    {
        // Onset in frame 1 (20 ms): still within the first two frames ->
        // clipped. 20 quiet + 800 loud + 780 quiet; preroll=1000/mask=1500;
        // deduction: 40 loud frames in-window minus budget 5 -> 700 ms
        // voiced/clear, passes.
        var buf = Join(Dc(0.001, 20), Dc(0.05, 800), Dc(0.001, 780));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBe(20);
        r.HeadClipped.ShouldBe(true);
    }

    [Fact]
    public void Trim_HeadSpeechAt40Ms_IsNotClipped()
    {
        // Onset in frame 2 (40 ms): first two frames are genuinely quiet, so
        // the utterance onset was captured — not clipped. Pins the < 40 ms
        // boundary (frames 0-1 only).
        var buf = Join(Dc(0.001, 40), Dc(0.05, 800), Dc(0.001, 760));

        var r = SilenceTrimmer.Trim(buf, 1500, 100, 1000);

        r.HeadSpeechAtMs.ShouldBe(40);
        r.HeadClipped.ShouldBe(false);
    }

    [Fact]
    public void Trim_NoMask_ScansFromBufferStart()
    {
        // Cue disabled (maskMs=0): nothing was played, nothing to exclude —
        // the scan covers the whole buffer from t=0.
        var buf = Join(Dc(0.05, 700), Dc(0.001, 500));

        var r = SilenceTrimmer.Trim(buf, 0, 0, 0);

        r.HeadSpeechAtMs.ShouldBe(0);
        r.HeadClipped.ShouldBe(true);
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAIL — `Trim` has no 4th parameter and `TrimResult` has no `HeadSpeechAtMs`.

- [ ] **Step 3: Implement**

(a) `TrimResult` (`SilenceTrimmer.cs:4-51`) — add after `MaxFrameRms`:

```csharp
    /// <summary>
    /// ms offset (from buffer t=0) of the first 20 ms frame at/above the
    /// clear-speech floor (0.02) OUTSIDE the cue-pickup window (the band
    /// [prerollMs, maskMs) where the app's own start cue can masquerade as
    /// speech); null when no such frame exists or the input is sub-frame.
    /// Head-loss diagnostic (2026-08-04) — no influence on the gate verdict
    /// or trimming.
    /// </summary>
    public required int? HeadSpeechAtMs { get; init; }

    /// <summary>
    /// True when head speech lands within the first two frames (offset
    /// &lt; 40 ms) — the signature of speech predating the recording window
    /// (the utterance onset was cut off). Null when HeadSpeechAtMs is null.
    /// </summary>
    public bool? HeadClipped => HeadSpeechAtMs is int h ? h < 40 : null;
```

(b) `Trim` signature (`:139`):

```csharp
    public static TrimResult Trim(ReadOnlySpan<float> samples, int maskMs = 0, int cueBudgetMs = 0, int prerollMs = 0)
```

and extend its `<summary>` with one sentence: `prerollMs is the pre-roll the recorder actually seeded (buffer t=0 sits that far before the hotkey); it locates the cue-pickup band [prerollMs, maskMs) that the head-speech diagnostic must not scan.`

(c) The scan — insert immediately after the existing `maskFrames`/`budgetFrames` computation (`:179-180`):

```csharp
        // Head-speech diagnostic (2026-08-04 head-loss work): first clearly-
        // speech-loud frame OUTSIDE the cue-pickup band. The cue can only be
        // picked up AFTER the hotkey — measured onset preroll+92..144 ms
        // (StartCueGateMask doc) — so the pre-roll head [0, prerollFrames)
        // is scannable and only [prerollFrames, maskFrames) is excluded.
        // maskMs == 0 means no cue was played: scan everything. Diagnostic
        // only — no effect on the verdict, tallies, or trimming below.
        var prerollFrames = maskFrames == 0 ? 0 : Math.Min(Math.Max(prerollMs, 0) / FrameMs, maskFrames);
        int? headSpeechAtMs = null;
        for (var f = 0; f < frameCount; f++)
        {
            if (f >= prerollFrames && f < maskFrames) continue;
            if (rms[f] >= ClearSpeechRmsFloor) { headSpeechAtMs = f * FrameMs; break; }
        }
```

(Note the guard: when `maskFrames == 0` the exclusion band `[0, 0)` is empty by construction, so the `continue` never fires.)

(d) Construction sites: the sub-frame early return (`:147` region) gets `HeadSpeechAtMs = null,`; every other `new TrimResult` in the file gets `HeadSpeechAtMs = headSpeechAtMs,`. The compiler flags any missed site (required member).

- [ ] **Step 4: Run to verify they pass**

Build + exec `Winpepper.Audio.Tests` as in Task 3 Step 4. Expected: PASS — the 6 new facts plus all 34 pre-existing trimmer facts (the added parameter defaults to 0 and the scan touches no gate state, so nothing else may move).

- [ ] **Step 5: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Audio/SilenceTrimmer.cs tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs
git commit -m "feat(audio): head-speech diagnostics in SilenceTrimmer

Trim reports the first clear-speech frame outside the cue-pickup band
(head_speech_at) and whether it lands in the first two frames (head_clipped
— speech predating the recording window). Pure diagnostics: verdict,
tallies, and trimming untouched.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 6: Timing-Line Fields in `DictationTimingSummary`

**Files:**
- Modify: `src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs` — property block (`:47-92` region), `FormatLine()` (`:94-152`)
- Test: `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs` — `Full()` fixture (`:10-58`), golden string (`:61-77`), new facts

**Interfaces:**
- Consumes: nothing new (pure formatting; existing `AppendOptMs` helper and the hand-rolled bool idiom used by `prewarm_active=`/`cpu_pegged=`).
- Produces: nullable properties `PrerollMs`, `ArmLatencyMs`, `RetriggerGapMs`, `HeadSpeechAtMs` (all `int?`), `HeadClipped` (`bool?`) — Task 7 stamps them in `EmitTimingSummary`. Emission order (immediately after `rec=`): `preroll=`, `arm_latency=`, `retrigger_gap=`, `head_speech_at=` (each `<n>ms`, omitted when null), `head_clipped=true|false` (omitted when null). The `<3000` filter for `retrigger_gap` is applied where the field is ASSIGNED (host side, Task 7) — `FormatLine` stays dumb and renders whatever is set, per the class's existing convention.

- [ ] **Step 1: Extend the tests (failing first)**

In `tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs`:

(a) Add to the `Full()` fixture (`:10-58`), next to the other recording-side fields:

```csharp
            PrerollMs = 1000,
            ArmLatencyMs = 17,
            RetriggerGapMs = 812,
            HeadSpeechAtMs = 120,
            HeadClipped = false,
```

(b) In the golden string of `FormatLine_FullDictation_IsOneParseableKeyValueLine` (`:65-75`), insert after `rec=3512ms `:

```
preroll=1000ms arm_latency=17ms retrigger_gap=812ms head_speech_at=120ms head_clipped=false 
```

(so the line reads `… rec=3512ms preroll=1000ms arm_latency=17ms retrigger_gap=812ms head_speech_at=120ms head_clipped=false mic_stop=42ms …`).

(c) Append three facts:

```csharp
    [Fact]
    public void FormatLine_HeadLossFields_AreOmittedWhenNull()
    {
        // The five 2026-08-04 head-loss diagnostics are all optional: a
        // session where they were never stamped renders none of them.
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "hold",
        };

        var line = s.FormatLine();

        line.ShouldNotContain("preroll=");
        line.ShouldNotContain("arm_latency=");
        line.ShouldNotContain("retrigger_gap=");
        line.ShouldNotContain("head_speech_at=");
        line.ShouldNotContain("head_clipped=");
    }

    [Fact]
    public void HeadClipped_True_Is_Emitted_Explicitly()
    {
        // Follows the cpu_pegged bool idiom: an explicit true/false when set.
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "hold",
            HeadSpeechAtMs = 0,
            HeadClipped = true,
        };

        s.FormatLine().ShouldContain(" head_speech_at=0ms head_clipped=true");
    }

    [Fact]
    public void RetriggerGap_RendersWhateverIsAssigned()
    {
        // The < 3000 ms emit gate lives at the ASSIGNMENT site (PipelineHost),
        // not here — FormatLine renders any set value, per class convention.
        var s = new DictationTimingSummary
        {
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Kind = "toggle",
            RetriggerGapMs = 2999,
        };

        s.FormatLine().ShouldContain(" retrigger_gap=2999ms");
    }
```

- [ ] **Step 2: Run to verify they fail**

```bash
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: BUILD FAIL — the five properties do not exist.

- [ ] **Step 3: Implement**

(a) Properties — add to `DictationTimingSummary` beside the other nullable diagnostics:

```csharp
    /// <summary>Warm pre-roll ms the recorder ACTUALLY seeded into this session
    /// (StartSession's return; 0 in cold mode). Head-loss diagnostics, 2026-08-04.</summary>
    public int? PrerollMs { get; set; }

    /// <summary>Hotkey-keydown (hook timestamp) to StartSession-handling lag in ms
    /// — the same value logged on the 'Session started' line. Uncompensated, this
    /// eats pre-keydown coverage 1:1 (M2); see PrerollRequest.</summary>
    public int? ArmLatencyMs { get; set; }

    /// <summary>ms between the previous session's stop hotkey and this session's
    /// start hotkey; assigned only when 0 &lt;= gap &lt; 3000 (the retrigger
    /// signature) — the filter lives at the assignment site.</summary>
    public int? RetriggerGapMs { get; set; }

    /// <summary>ms offset (buffer t=0) of the first clear-speech frame outside the
    /// cue-pickup window (SilenceTrimmer TrimResult.HeadSpeechAtMs); null when none
    /// or when trim never ran.</summary>
    public int? HeadSpeechAtMs { get; set; }

    /// <summary>True when head speech lands in the first two 20 ms frames — speech
    /// predating the recording window. Null when HeadSpeechAtMs is null.</summary>
    public bool? HeadClipped { get; set; }
```

(b) `FormatLine()` — immediately after `AppendCoreMs(sb, "rec", RecordMs);` (`:97`):

```csharp
        AppendOptMs(sb, "preroll", PrerollMs);
        AppendOptMs(sb, "arm_latency", ArmLatencyMs);
        AppendOptMs(sb, "retrigger_gap", RetriggerGapMs);
        AppendOptMs(sb, "head_speech_at", HeadSpeechAtMs);
        if (HeadClipped is bool clipped)
            sb.Append(" head_clipped=").Append(clipped ? "true" : "false");
```

(The hand-rolled bool follows the `prewarm_active=`/`cpu_pegged=` idiom exactly — do NOT introduce an `AppendOptBool` helper refactor in this task.)

- [ ] **Step 4: Run to verify they pass**

Build + `dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll -notrait "Platform=Windows"`. Expected: PASS — 3 new facts plus all pre-existing (golden line updated in Step 1 keeps `FormatLine_FullDictation_IsOneParseableKeyValueLine` green).

- [ ] **Step 5: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Core/Diagnostics/DictationTimingSummary.cs tests/Winpepper.Core.Tests/Diagnostics/DictationTimingSummaryTests.cs
git commit -m "feat(core): timing line gains preroll/arm_latency/retrigger_gap/head_speech_at/head_clipped

Self-diagnosing head-loss fields (all optional, emitted right after rec=);
formatting only — stamping lands in PipelineHost separately.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 7: Stamp the Diagnostics in `PipelineHost`

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` — host-field block (`:43-109` region), both start arms (`:566-580`, `:1156-1172` regions), stop-initiating hotkey sites (HoldUp `releaseAt` binding at `:610`; toggle-stop `releaseAt2` at `:1200`; Cancel case `:1097-1115`), `TrimForTranscription` (`:1739-1742`), `EmitTimingSummary` (`:1777-1799`).

Windows-only file — same verification posture as Task 4 (Linux suite per commit; Windows compile in Task 9's gate).

**Interfaces:**
- Consumes: `hotkeyLagMs`/`hotkeyLagMs2` locals (Task 4); `SilenceTrimmer.Trim(samples, maskMs, cueBudgetMs, prerollMs)` + `TrimResult.HeadSpeechAtMs`/`HeadClipped` (Task 5); `DictationTimingSummary.{PrerollMs,ArmLatencyMs,RetriggerGapMs,HeadSpeechAtMs,HeadClipped}` (Task 6); `evt.Timestamp` (`DateTimeOffset`, hook-stamped); existing fields `_lastSessionPrerollMs`, `_dictStartTicks`.
- Produces: every emitted `dictation timing` line (all 6 `EmitTimingSummary` call sites — `:709, :804, :1067, :1298, :1393, :1651` — funnel through the one method being edited) carries `preroll=` and `arm_latency=` always, `retrigger_gap=` when a previous stop happened <3 s before this start, and `head_speech_at=`/`head_clipped=` when this session's trim found head speech.

- [ ] **Step 1: Add the host fields**

Next to `_lastSessionPrerollMs` (`:54`) add (the run loop is serial — one dictation fully processed before the next — so plain host fields suffice, same argument as the existing baseline fields at `:80-88`):

```csharp
    /// <summary>Hotkey-keydown→handling lag (ms) of the CURRENT session — the
    /// hoisted 'Session started' value; feeds arm_latency= on the timing line.
    /// Like _lastSessionPrerollMs: BOTH arms must assign it.</summary>
    private int _lastArmLatencyMs;

    /// <summary>Hook timestamp of the most recent stop-initiating hotkey
    /// (HoldUp / toggle-stop / Cancel); null until the first stop. Source for
    /// retrigger_gap= — hook timestamps at both ends, so the gap measures USER
    /// behavior, not pipeline latency.</summary>
    private DateTimeOffset? _lastStopHotkeyUtc;

    /// <summary>ms between the previous stop hotkey and this session's start
    /// hotkey when 0 &lt;= gap &lt; 3000 (the retrigger signature), else null.
    /// The filter lives HERE, not in FormatLine. BOTH arms must assign it.</summary>
    private int? _retriggerGapMs;

    /// <summary>head_speech_at/head_clipped from this session's
    /// TrimForTranscription; null when trim did not run or found no clear
    /// frame outside the cue window. Reset at BOTH arms (a failed/silent
    /// session must not leak the previous session's values).</summary>
    private int? _lastHeadSpeechAtMs;
    private bool? _lastHeadClipped;
```

- [ ] **Step 2: Assign at both start arms**

Hold arm — directly after the hoisted `hotkeyLagMs` from Task 4 (before `PlayStart()`), add:

```csharp
                _lastArmLatencyMs = hotkeyLagMs;
                _retriggerGapMs = null;
                if (_lastStopHotkeyUtc is DateTimeOffset prevStop)
                {
                    // Hook-time to hook-time: immune to handler serialization.
                    // Negative (wall-clock skew) or >= 3 s gaps are not
                    // retriggers — omit the field entirely.
                    var gapMs = (int)(evt.Timestamp - prevStop).TotalMilliseconds;
                    if (gapMs is >= 0 and < 3000) _retriggerGapMs = gapMs;
                }
```

and directly after the `_lastSessionPrerollMs = _warmRecorder!.StartSession(…)` line:

```csharp
                _lastHeadSpeechAtMs = null;
                _lastHeadClipped = null;
```

Toggle arm — identical block using `hotkeyLagMs2` (and a `prevStop2` pattern-variable name to avoid any scope collision), inserted at the equivalent points around `:1165`.

- [ ] **Step 3: Record stop-hotkey timestamps**

At each stop-initiating arm add `_lastStopHotkeyUtc = evt.Timestamp;`:
- HoldUp arm: adjacent to the existing `releaseAt` binding from `evt.Timestamp` (`:610`).
- Toggle-stop arm: adjacent to `releaseAt2` (`:1200`).
- Cancel case (`:1097-1115`): at the top of the case body. A cancel followed by a quick re-press is exactly the blip pattern the field exists to expose, so cancels count as stops — this is the deliberate answer to the open design question, record nothing else.

- [ ] **Step 4: Feed the trimmer and stash its head fields**

In `TrimForTranscription` (`:1739-1742`) change the `Trim` call and stash:

```csharp
        var result = Winpepper.Audio.SilenceTrimmer.Trim(samples, cueMaskMs, cueBudgetMs, _lastSessionPrerollMs);
        _lastHeadSpeechAtMs = result.HeadSpeechAtMs;
        _lastHeadClipped = result.HeadClipped;
```

(Stash BEFORE the `result.IsSilent` early return so silent drops still carry head diagnostics on their timing line.)

- [ ] **Step 5: Stamp in the funnel**

In `EmitTimingSummary` (`:1777-1799`), beside the existing `GcGen*`/`PrewarmActive`/`CpuPegged` stamps:

```csharp
        // Head-loss diagnostics (2026-08-04): zero-cost reads of values the
        // pipeline already computed this session.
        timing.PrerollMs = _lastSessionPrerollMs;
        timing.ArmLatencyMs = _lastArmLatencyMs;
        timing.RetriggerGapMs = _retriggerGapMs;
        timing.HeadSpeechAtMs = _lastHeadSpeechAtMs;
        timing.HeadClipped = _lastHeadClipped;
```

- [ ] **Step 6: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): stamp head-loss diagnostics onto the dictation timing line

preroll= and arm_latency= always; retrigger_gap= when a stop hotkey preceded
this start by <3 s (hook-time to hook-time, cancels count); head_speech_at=/
head_clipped= from the trimmer. No new threads/timers — all values the
pipeline already had.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 8: Per-Frame Leading-Silence Latch (Parakeet streaming) — prevents Change 1 from creating a new head-loss path

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs` — latch in `PushAsync` (`:113-121`), latch doc comment (`:59-87`, incl. the stale "500 ms pre-roll" at `:67`), new private helper next to `Rms` (`:253-259`)
- Test: `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (append 2 facts; the existing three latch facts at `:94/:109/:125` must stay green)

**Why this is in scope:** the latch gates the whole pushed buffer on its **whole-buffer** RMS (`if (Rms(mono16k.Span) < LeadingSilenceRmsFloor) return;` at `:121`, before `_skipper.Push` at `:131`). Required onset duration to unlatch scales linearly with buffer length — 4× harder at a 2000 ms pre-roll than at today's 500 ms (a 0.01-amplitude onset needs 80 ms instead of 20 ms). On a miss the ENTIRE pre-roll — including the onset words this whole plan exists to capture — is silently discarded from the streamed transcript, and `FinishAsync` has no guard for a head-truncated stream. Fix: test per-20 ms frame (mirroring the batch trimmer's granularity, which the doc comment already names as the deliberate divergence) and feed from the first voiced frame. This also stops ~2 s of pre-roll silence flooding the running mel normalizer and closes the new blank-collapse surface. The latch keeps NO drop authority — unchanged contract.

**Interfaces:**
- Consumes: existing `LeadingSilenceRmsFloor = 0.002` (`:87`), existing `private static double Rms(ReadOnlySpan<float>)` (`:253-259`), `_speechSeen` latch flag.
- Produces: unchanged public API (`PushAsync(ReadOnlyMemory<float>, CancellationToken)`); new private `static int FirstVoicedFrameOffset(ReadOnlySpan<float>)` returning the sample offset of the first 20 ms frame with RMS ≥ floor, or −1.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs` (reuse the file's existing `FakeParakeetBackend`, `NewSession(backend, chunk:, context:)`, `Hop` vocabulary — see the latch facts at `:94-140` for the exact idiom):

```csharp
    [Fact]
    public async Task LeadingSilence_QuietOnsetDilutedInLongPreroll_Unlatches_AndFeedsFromOnset()
    {
        // 2026-08-04 head-loss guard: with a 2000 ms mostly-silent pre-roll,
        // whole-buffer RMS dilutes a quiet onset below the 0.002 floor —
        // 1960 ms zeros + 40 ms DC 0.005 gives RMS 0.005*sqrt(40/2000) =
        // 0.0007 < 0.002 (the old latch discards the WHOLE buffer, onset
        // included), while the onset's own 20 ms frames sit at 0.005 >= 0.002.
        // Per-frame latch must unlatch AND feed from the onset frame: fed
        // audio = 640 (onset) + 9600 (speech push) = 10240 samples ->
        // floor((10240-256)/160)+1 = 63 mel frames -> exactly ONE 50-frame
        // encode (chunk: 50). Feeding the whole first buffer instead would
        // give 41600 samples -> 259 mel frames -> 5 encodes.
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        var preroll = new float[16000 * 2];                    // 2000 ms of zeros...
        Array.Fill(preroll, 0.005f, 16000 * 2 - 640, 640);     // ...ending in a 40 ms quiet onset
        await session.PushAsync(preroll, TestContext.Current.CancellationToken);

        var speech = new float[9600];                          // 600 ms of clear speech
        Array.Fill(speech, 0.02f);
        await session.PushAsync(speech, TestContext.Current.CancellationToken);

        backend.EncodeMelFrameCounts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task LeadingSilence_AllFramesBelowFloor_StaysGated_EvenWhenLong()
    {
        // Uniform 0.0019 DC: every 20 ms frame RMS = 0.0019 < 0.002, so the
        // per-frame latch stays gated exactly like the whole-buffer one did
        // (pins that granularity did not loosen the floor).
        var backend = new FakeParakeetBackend();
        var session = NewSession(backend, chunk: 50, context: 20);

        var below = new float[16000 * 2];
        Array.Fill(below, 0.0019f);
        await session.PushAsync(below, TestContext.Current.CancellationToken);
        await session.PushAsync(below, TestContext.Current.CancellationToken);

        backend.EncodeMelFrameCounts.Count.ShouldBe(0);
    }
```

- [ ] **Step 2: Run to verify the first fact fails**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows"
```
Expected: `LeadingSilence_QuietOnsetDilutedInLongPreroll_Unlatches_AndFeedsFromOnset` FAILS (0 encodes — the diluted buffer is discarded whole); `…StaysGated_EvenWhenLong` passes (both implementations gate it).

- [ ] **Step 3: Implement the per-frame latch**

In `ParakeetStreamingSession.PushAsync` (`:113-121`) replace the latch block:

```csharp
        if (!_speechSeen)
        {
            // Leading-silence gate, per-20 ms-frame since 2026-08-04 (head-loss
            // work): a whole-buffer RMS diluted quiet onsets linearly with
            // buffer length — a 2 s lag-compensated pre-roll made a miss
            // routine, and a miss discarded the onset words themselves. Now
            // the first frame at/above the floor unlatches, and audio is fed
            // FROM that frame — pre-onset silence is skipped instead of
            // polluting the running normalizer. Still a START-OF-SPEECH feed
            // gate with NO drop authority (the batch SilenceTrimmer verdict
            // governs drops), same as before.
            var onset = FirstVoicedFrameOffset(mono16k.Span);
            if (onset < 0) return ValueTask.CompletedTask;
            _speechSeen = true;
            mono16k = mono16k.Slice(onset);
        }
```

Add next to `Rms` (`:253-259`):

```csharp
    /// <summary>
    /// Sample offset of the first 20 ms frame (320 samples @ 16 kHz; trailing
    /// partial frame included so a just-arrived onset is never discarded)
    /// whose RMS is at/above LeadingSilenceRmsFloor; -1 when the whole buffer
    /// is below the floor.
    /// </summary>
    private static int FirstVoicedFrameOffset(ReadOnlySpan<float> samples)
    {
        const int frameSamples = 320;
        for (var offset = 0; offset < samples.Length; offset += frameSamples)
        {
            var length = Math.Min(frameSamples, samples.Length - offset);
            if (Rms(samples.Slice(offset, length)) >= LeadingSilenceRmsFloor) return offset;
        }
        return -1;
    }
```

Update the latch doc comment (`:59-87`): replace the stale "the 500 ms pre-roll is mostly silence" (`:67`) with "the seeded pre-roll (up to ~2 s with lag compensation) is mostly silence", and rewrite divergence point "(a)" — granularity now MATCHES the batch trimmer's 20 ms frames; keep points about no-drop-authority, transient permanent-unlatch (now per-frame: a single ≥0.002 frame unlatches — still only costs encoder work, never words), and the no-mask divergence.

- [ ] **Step 4: Run to verify all pass**

Same commands as Step 2. Expected: PASS — both new facts AND the three pre-existing latch facts (`LeadingSilence_IsGated_NotFedToTheEncoder`: all-zero frames stay below floor; `…JustBelowFloor_StaysGated`: uniform 0.0019 frames all below; `…JustAboveFloor_Unlatches`: uniform 0.0021 → frame 0 unlatches at offset 0, whole buffer fed — byte-identical behavior to before).

- [ ] **Step 5: Full Linux suite, then commit**

Run: `./scripts/linux-tests.sh` → `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs tests/Winpepper.Asr.Tests/ParakeetStreamingSessionTests.cs
git commit -m "fix(asr): per-frame leading-silence latch — long pre-roll no longer dilutes onset detection

Whole-buffer RMS made unlatching 4x harder at a 2 s pre-roll and a miss
discarded the onset words the head-loss work exists to capture. The latch
now unlatches on the first 20 ms frame at the floor and feeds from it;
drop authority unchanged (none).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

---

### Task 9: Evidence Completion + Full Gates

**Files:**
- Modify: `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` (extend the 2026-08-04 section created in Task 1 — append below it, edit nothing above)

**Interfaces:**
- Consumes: everything landed in Tasks 1–8; gate outputs.
- Produces: the run's complete evidence record and green gates. Branch left local.

- [ ] **Step 1: Append the schema + verification record to the evidence section**

Append to the 2026-08-04 section of `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md`:

```markdown
### Timing-line schema additions (2026-08-04)

Emitted immediately after `rec=`, all optional, stamped in EmitTimingSummary
from values the pipeline already computed (zero-cost discipline — no new
threads/timers). The 'Session started' line keeps its existing fields.

- `preroll=<n>ms` — pre-roll the recorder ACTUALLY seeded (StartSession's
  return; 0 in cold mode). Base request is now 1000 ms + observed hotkey lag
  clamped to 1000 ms (PrerollRequest), served by a 2 s ring.
- `arm_latency=<n>ms` — hotkey-keydown (hook timestamp) -> handling lag; the
  same value as the Session-started line's LagMs. Hook-callback delays
  (Windows LowLevelHooksTimeout) are invisible to it, as before.
- `retrigger_gap=<n>ms` — start hotkey minus previous stop hotkey (HoldUp /
  toggle-stop / Cancel all count as stops), emitted only when 0 <= gap < 3000.
- `head_speech_at=<n>ms` — first 20 ms frame at/above the 0.02 clear-speech
  floor OUTSIDE the cue-pickup band [seeded-preroll, mask); omitted when none.
- `head_clipped=true|false` — head_speech_at within the first two frames
  (< 40 ms): speech predating the recording window. Omitted when
  head_speech_at is.

### Interactions verified rather than assumed (2026-08-04)

- Mask auto-scaling: PipelineHost.cs:1741 feeds the ACTUAL seed into
  ComputeMaskMs — lag compensation needed no mask change. The startup
  worst-case line now uses PrerollRequest.MaxRequestMs so it stays a ceiling.
- Archiving: the FULL untrimmed buffer (pre-roll head included) is archived
  on all four paths (PipelineHost.cs:673/1075/1262/1659 -> HistoryArchiver.cs:45);
  the longer head simply rides along.
- rec= accounting: _recordStopwatch starts AFTER StartSession returns
  (PipelineHost.cs:576/1166) — pre-roll excluded, field meaning unchanged.
- AssemblyAI streaming: oversized pre-roll frames are chunked to <= 16000
  samples/send (AssemblyAiStreamingTranscriber.cs:176-182), pinned by
  Push_SplitsAnOversizedBufferIntoAtMost1000MsMessages at 40 000 samples
  (> the 32 000 worst case).
- Parakeet streaming: whole-buffer leading-silence latch WOULD have diluted
  quiet onsets 4x at a 2 s pre-roll and discarded them silently — converted
  to per-frame with feed-from-onset (Task 8), pinned by two new facts.

- Residual risks ACCEPTED: digital-zero pad in the replication is not room
  tone; single-user frozen corpora (standing residual at :400-409); up to
  ~2 s of pre-roll now arrives at AssemblyAI as an initial burst against its
  ~1.25x-realtime ingest throttle (previously ~0.5 s; size-legal, rate
  unpinned by tests); the 2 s ring doubles the per-callback RemoveRange
  shift on the WASAPI thread (~800 floats moved in a 32000-float list per
  50 ms — O(n) List, accepted); arm_latency measures hook->handler only,
  physical-keypress->hook latency remains invisible; M3 release-blip
  merge/debounce PARKED (retrigger_gap= now measures its true frequency for
  a future decision).
```

- [ ] **Step 2: Run the full gates**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-headloss
./scripts/linux-tests.sh
./scripts/windows-gate.sh
```
Expected: `LINUX SUITE: GREEN` (base 1667 + this plan's ~16 new facts; record the exact total) and `GATE: GREEN` (12/12 project/TFM runs). Known transients: UNC MSB4025 + vsock interop flakes — retry the gate; `ModelCardViewModelDispatchTests` LateByteReport — retry once before treating as real. If the gate reports real compile/test failures in the Windows-only edits (Tasks 4/7), fix them and land the fix as its own focused commit (`fix(app): …` with the same footer), then re-run both gates.

- [ ] **Step 3: Record the gates and commit**

Append as the final bullet of the 2026-08-04 evidence section (exact counts from Step 2's output):

```markdown
- Gates: scripts/linux-tests.sh GREEN (<N> tests, 9/9 projects); scripts/windows-gate.sh
  GATE: GREEN (12/12 test project/TFM runs, <N> tests, <transient retries: none|list>).
  Branch fix/dictation-headloss left local per workflow; root session merges, gates, installs.
```

```bash
git add docs/plans/2026-07-29-cleanup-asr-contention-evidence.md
git commit -m "docs(plans): evidence + gates — dictation head-loss run complete

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>"
```

Do NOT push. Done.

---

## Coverage Map (spec → tasks)

| Spec requirement | Task(s) |
|---|---|
| Change 1: pre-roll 500 → 1000 ms, ring → 2 s, single-source constant preserved | 2 |
| Change 1 required validation: padded frozen-archive gate replication, numbers recorded in the evidence doc | 1 (+ numbers cited in 2, gates in 9) |
| Change 1 verify: archiving includes longer head; rec= unaffected | Verified during planning (file:line in Background); recorded in 9 |
| Change 2: lag-compensated request at BOTH arms, clamped to ring; actual seed still drives mask; pure helper in the StartCueGateMask idiom | 3, 4 |
| Change 3: `preroll=`, `arm_latency=`, `retrigger_gap=` (<3000 only), `head_speech_at=` (clear floor outside cue window), `head_clipped=` (≤ first 2 frames); zero-cost; schema documented in evidence file; Session-started line unchanged | 5, 6, 7, 9 |
| Repo conventions: linux suite per commit, windows gate before done, no push, Amplifier attribution | every task; 9 |
| Out of scope: M3, trim margins, gate constants, streaming feed, cue playback, models-page-ux | honored throughout (M3 explicitly parked; retrigger_gap only measures it) |
| Change-1 side-effect found in planning: Parakeet latch dilution (new head-loss path) | 8 |
