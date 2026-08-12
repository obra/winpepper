# 2026-08-12 — Silence-gate end-to-end verification evidence (8whk)

## Scope

kata 8whk ("Suppress paste of junk words from silent recordings") acceptance:

1. Silent recordings paste nothing.
2. Real short utterances ("yes", "stop" scale) still paste — no false suppression.
3. Tests green per AGENTS.md gates.

Prior audit finding (2026-08-12 session): the gate already ships in main — three-tier
RMS/voiced-run classifier in `src/Winpepper.Audio/SilenceTrimmer.cs`; drop path at
`src/Winpepper.App/Hosting/PipelineHost.cs:746-777` (tap) and the toggle-path twin: skip
transcription AND injection, archive the ORIGINAL untrimmed audio, record a history
entry with `rawTranscript: ""` and `asrModelName: ""`, return the pill to Idle. Streaming
route never inserts partial text before this decision. This document records the
real-audio verification of both prongs.

## Method

A throwaway harness (not committed) referenced `src/Winpepper.Audio` directly and ran
the real `SilenceTrimmer.Trim` over real production WAVs from
`%LOCALAPPDATA%\winpepper\history` (2026-08-12 archive, the user's actual dictations
from this morning):

- **Prong 1 set** — all 9 history entries with `rawTranscript == ""` and
  `asrModelName == ""` (the serialized signature of a silent drop).
- **Prong 2 set** — the 10 shortest history entries with non-empty transcripts
  (2,530–3,551 ms total duration each).

WAVs decoded as 16 kHz mono int16 via a minimal RIFF walk in the harness.

## Prong 1 results — all 9 drops re-judged silent by today's gate

| WAV (prefix) | dur ms | IsSilent | voiced ms | clear ms | max frame RMS |
|---|---|---|---|---|---|
| 81fcba9f | 1441 | **True** | 0 | 0 | 0.00099 |
| 706311cc | 2590 | **True** | 0 | 0 | 0.00131 |
| 5b371e4b | 2050 | **True** | 0 | 0 | 0.00150 |
| 6574a917 | 3880 | **True** | 0 | 0 | 0.00651 |
| 4b56699c | 1556 | **True** | 0 | 0 | 0.00111 |
| 36bb4e47 | 1656 | **True** | 0 | 0 | 0.00100 |
| cab6281d | 2676 | **True** | 0 | 0 | 0.00000 |
| 5e23989f | 1981 | **True** | 0 | 0 | 0.00488 |
| 3170fd4b | 1051 | **True** | 0 | 0 | 0.00121 |

9/9: zero voiced milliseconds, max frame RMS ≤ 0.0065 — under the 0.010 dashed floor.
Every production drop the app made today stands up under re-judgment.

## Prong 2 results — shortest real utterances all kept

| WAV (prefix) | dur ms | IsSilent | voiced ms | kept ms | transcript |
|---|---|---|---|---|---|
| 21c9f18f | 2530 | **False** | 1240 | 2510 | "I'm getting this error." |
| 8b0d03c8 | 2540 | **False** | 1200 | 2040 | "Search exhaustively." |
| fd8c413f | 2570 | **False** | 740 | 2110 | "Do they both work?" |
| a5280d1a | 2940 | **False** | 760 | 2480 | "Hi Christine" |
| 56423ea7 | 2940 | **False** | 1280 | 2440 | "I've updated to the latest." |
| 20d020c9 | 2944 | **False** | 640 | 2944 | "New data point." |
| e3b6bb1b | 3090 | **False** | 1520 | 2630 | "Make sure the seller is reputable." |
| 28750aaf | 3170 | **False** | 1500 | 2710 | "sets up a standard allocation approach." |
| 7f670c6f | 3190 | **False** | 1700 | 2670 | "It should be from a reputable seller." |
| ca41e2b8 | 3551 | **False** | 1860 | 3091 | "How is it different from the auto repair help" |

10/10 kept, 74–100% of audio preserved. Note `8b0d03c8` ("Search exhaustively."):
maxRms 0.014, clearVoicedMs 0 — BELOW the 0.02 "clear" tier, saved by the dashed
tier exactly as designed.

## Separation margin

Max dropped RMS 0.0065 vs min kept real-speech RMS 0.0133: a ~2x gap with the 0.010
dashed floor sitting in the middle. Thresholds are validated by real data for this
mic/environment.

## Residual risk (filed as msbj)

Sustained broadband noise with max frame RMS ≥ 0.010 (loud fan, music, clatter) passes
all gate tiers and reaches ASR, which can still hallucinate short junk. No such case in
today's archive. Directions in the msbj body. Do not tighten thresholds without more
real recordings.

## Gate state

Linux suite on merged main (includes these code paths unchanged): 1,961 tests,
0 failures, GREEN. Unit coverage of the gate itself: 30+ tests in
`tests/Winpepper.Audio.Tests/SilenceTrimmerTests.cs` (floors pinned, dashed rescue,
cue-mask, escape hatch). Streaming drop/abandon paths covered in
`tests/Winpepper.Asr.Tests/Transcription/*` and prefetch race
(`SilenceDropThenDictate_*`) in `tests/Winpepper.Platform.Tests`.
