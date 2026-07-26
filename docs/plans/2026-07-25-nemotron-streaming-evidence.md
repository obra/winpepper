# Nemotron Streaming: Real Streamed-vs-Batch Verification Evidence

Real streamed-path evidence for the Nemotron streaming transcriber
(`docs/plans/2026-07-25-nemotron-streaming.md`). Replaces the Parakeet-era
`docs/plans/2026-07-25-streaming-verification-evidence.md`, whose every
"stream" row was a blank-collapse batch fallback (`fellBackToBatch=True`) —
here, all four categories ran the real streamed path end-to-end with
`fellBackToBatch=False`.

Run date: 2026-07-26 (logs in `artifacts/nemotron-bench/`, untracked by
convention).

## Method

- Harness: `scripts/run-nemotron-bench-windows.sh` → `AsrLatencyBench`
  `real-nemotron-stream` scenario. The streamed path runs the REAL production
  stack (`StreamingDictationSession` + `NemotronStreamingTranscriber`) fed
  50 ms frames at real-time pace — the manual-equivalent, end-to-end check.
  Post-stop latency = time from last sample pushed to final transcript.
- Host: DANDESKTOP, AMD Ryzen 9 5950X 16-Core, Windows dotnet 9.0.316.
- Model: `nemotron-speech-streaming-en-0.6b-Q8_0.gguf` (729,650,176 bytes).
  SHA-256 of the spike-scratch copy verified this run as
  `90d8c89714cd31efc88be62a40c6b2bea57e0cc2063af1ffe2c28f1a228ca110` — matches
  the pin in `src/Winpepper.Models/ModelRegistry.cs`.
- Runtime: transcribe.cpp v0.1.3, CPU backend, `att_context_right=13`.
  SHA-256 of the spike-scratch tarball verified this run as
  `9f536cb0fb839bd305e6d92fb214fd417c7718a416a6c7646a9911fbd56fdad5` — matches
  the `ModelRegistry` pin.
- TDT comparison rows: installed
  `C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3`
  (read-only), batch path.
- Audio: host TTS via `scripts/generate-bench-wavs.ps1` (System.Speech,
  16 kHz mono 16-bit): `normal-10s.wav` (~10.6 s), `pause-mid.wav` (~8.5 s).
  Quiet category = normal phrase at `--gain 0.02`; lead-silence category =
  normal phrase with `--lead-silence-ms 1500` (12.1 s total).
- Honesty caveats: TTS-generated speech, not a live microphone; the
  spike-scratch binaries at `C:\Users\dan\AppData\Local\Temp\transcribe-spike`
  were reused read-only (hashes verified above — the SHIPPED acquisition path
  still downloads fresh with the same pinned hashes). Operational note: the
  first bench attempt failed at build with CS0006 (stale mixed WSL/Windows
  `obj/` artifacts); fixed by deleting `bin/`+`obj/` for the four involved
  projects and re-running — no source changes.

## Results (REAL Nemotron, streamed vs batch)

All numbers copied from the category logs in `artifacts/nemotron-bench/`.

| category | audio | nem-batch post-stop (ms) | nem-stream post-stop (ms) | parity diff (streamed vs nem-batch) | vs TDT-batch diff | fellBackToBatch |
|---|---|---|---|---|---|---|
| normal (~10 s dictation) | 10.6 s | 1074 | 166 | IDENTICAL (30 words) | 2 word-level diffs | False |
| pause-mid (2.0 s interior pause) | 8.5 s | 799 | 175 | IDENTICAL (17 words) | 2 word-level diffs | False |
| quiet (gain 0.02) | 10.6 s | 838 | 108 | IDENTICAL (30 words) | 5 word-level diffs | False |
| lead-silence (1500 ms) | 12.1 s | 835 | 142 | IDENTICAL (30 words) | IDENTICAL (30 words) | False |

### Verbatim result lines

**normal** (`artifacts/nemotron-bench/normal.log`):

```
# nem-batch[normal-10s.wav]: "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# nem-stream[normal-10s.wav]: fellBackToBatch=False "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# diff-parity[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)
# tdt-batch[normal-10s.wav]: "Please summarise the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# diff-vs-tdt[normal-10s.wav]: 2 word-level diffs (batch 30 words, stream 30 words): -summarise +summarize
```

**pause-mid** (`artifacts/nemotron-bench/pause-mid.log`):

```
# nem-batch[pause-mid.wav]: "Send the quarterly report to the finance team and schedule the follow-up meeting for Thursday afternoon."
# nem-stream[pause-mid.wav]: fellBackToBatch=False "Send the quarterly report to the finance team and schedule the follow-up meeting for Thursday afternoon."
# diff-parity[pause-mid.wav]: IDENTICAL after case/punctuation/whitespace normalization (17 words)
# tdt-batch[pause-mid.wav]: "Send a quarterly report to the finance team. And schedule the follow-up meeting for Thursday afternoon."
# diff-vs-tdt[pause-mid.wav]: 2 word-level diffs (batch 17 words, stream 17 words): -a +the
```

**quiet** (`artifacts/nemotron-bench/quiet.log`):

```
# nem-batch[normal-10s.wav]: "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# nem-stream[normal-10s.wav]: fellBackToBatch=False "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# diff-parity[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)
# tdt-batch[normal-10s.wav]: "Ple summarize the meeting notes from this morning and send them to the whole team. Then remind me to review the quarterly budget spreadshefor the planning session tomorrow afternoon."
# diff-vs-tdt[normal-10s.wav]: 5 word-level diffs (batch 29 words, stream 30 words): -ple +please -spreadshefor +spreadsheet +before
```

**lead-silence** (`artifacts/nemotron-bench/lead-silence.log`):

```
# nem-batch[normal-10s.wav]: "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# nem-stream[normal-10s.wav]: fellBackToBatch=False "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# diff-parity[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)
# tdt-batch[normal-10s.wav]: "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."
# diff-vs-tdt[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)
```

### Latency tables (verbatim)

```
| nem-batch normal-10s.wav | REAL nemotron | 10.6 s | 1074 |
| nem-stream normal-10s.wav | REAL nemotron | 10.6 s | 166 |
| nem-batch pause-mid.wav | REAL nemotron | 8.5 s | 799 |
| nem-stream pause-mid.wav | REAL nemotron | 8.5 s | 175 |
| nem-batch normal-10s.wav | REAL nemotron | 10.6 s | 838 |     (quiet, gain 0.02)
| nem-stream normal-10s.wav | REAL nemotron | 10.6 s | 108 |    (quiet, gain 0.02)
| nem-batch normal-10s.wav | REAL nemotron | 12.1 s | 835 |     (lead-silence, 1500 ms)
| nem-stream normal-10s.wav | REAL nemotron | 12.1 s | 142 |    (lead-silence, 1500 ms)
```

No `# nem-log:` warning lines appeared in any category log.

## Acceptance assessment

1. Streamed-nemotron == batch-nemotron word-level parity (after
   `TranscriptDiff` normalization) on all four categories: **MET**. All four
   `# diff-parity` lines read IDENTICAL (30 / 17 / 30 / 30 words).
2. Post-stop latency < 500 ms for the 10 s phrase: **MET**. The `normal`
   category `nem-stream` row is 166 ms (vs 1074 ms nem-batch over the same
   10.6 s audio — a 6.5x reduction). All other streamed rows are also under
   500 ms (175 / 108 / 142 ms).
3. Nemotron vs TDT-batch wording differences characterized honestly: **MET**.
   Actual diffs — normal: `-summarise +summarize` (spelling variant);
   pause-mid: `-a +the` (article choice; TDT also splits into two sentences,
   normalized away); quiet: `-ple +please -spreadshefor +spreadsheet +before`
   — at gain 0.02 the TDT batch transcript degrades ("Ple summarize",
   "spreadshefor") while Nemotron's batch AND streamed transcripts remain
   fully intact, so these diffs reflect TDT's low-signal degradation, not a
   Nemotron defect; lead-silence: IDENTICAL. These are legitimate model
   wording differences, not streaming artifacts.
4. No category fell back to batch: **MET**. All four `# nem-stream` lines
   carry `fellBackToBatch=False`.

## Windows pre-push gate result

Run date: 2026-07-26. Ran against commit `4fdfdf2`
(`4fdfdf28a33399220ba77464971cbf55a126b43d`); the only uncommitted files in
the tree were this task's docs (`THIRD-PARTY-NOTICES.md`, `README.md`, this
section), which do not affect compilation. GREEN on the first attempt — no
fixes were required; the App-project changes from Tasks 6/7 compiled cleanly
on their first Windows build. Verbatim summary block:

```
================ windows-gate summary ================
Winpepper.App build: OK
Winpepper.Asr.Tests (net9.0): OK     Winpepper.Asr.Tests  Total: 200, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 6.815s
Winpepper.Audio.Tests (net9.0): OK     Winpepper.Audio.Tests  Total: 62, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.387s
Winpepper.Audio.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Audio.Tests  Total: 64, Errors: 0, Failed: 0, Skipped: 1, Not Run: 0, Time: 0.420s
Winpepper.Cleanup.Tests (net9.0): OK     Winpepper.Cleanup.Tests  Total: 85, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.718s
Winpepper.Cleanup.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Cleanup.Tests  Total: 87, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 0.782s
Winpepper.Core.Tests (net9.0): OK     Winpepper.Core.Tests  Total: 379, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.330s
Winpepper.Corrections.Tests (net9.0): OK     Winpepper.Corrections.Tests  Total: 23, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.585s
Winpepper.History.Tests (net9.0): OK     Winpepper.History.Tests  Total: 45, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.980s
Winpepper.IntegrationTests (net9.0): OK     Winpepper.IntegrationTests  Total: 2, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.626s
Winpepper.Models.Tests (net9.0): OK     Winpepper.Models.Tests  Total: 87, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.622s
Winpepper.Platform.Tests (net9.0): OK     Winpepper.Platform.Tests  Total: 218, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 0.540s
Winpepper.Platform.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Platform.Tests  Total: 222, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 1.635s
GATE: GREEN
```

Actual test count across the 12 runs: 1474 (0 errors, 0 failures,
7 skipped — the pre-existing Windows-only skips).

## Cross-references & environment honesty

- Plan: `docs/plans/2026-07-25-nemotron-streaming.md`.
- Replaces `docs/plans/2026-07-25-streaming-verification-evidence.md`
  (Parakeet int8 chunked-decode defect; every stream row there was a batch
  fallback). This doc provides the streamed-path parity and latency evidence
  that document recorded as "NOT met"/"not citable".
- Spike source: `C:\Users\dan\AppData\Local\Temp\transcribe-spike` (GGUF,
  runtime tarball + extracted dir, `src/`); reused read-only, hashes verified
  against `ModelRegistry` pins (see Method).
- Audio is TTS-generated (System.Speech). Full end-to-end audio
  (mic → hotkey → paste) remains covered by `docs/manual-test.md`'s manual
  procedure, not by this bench.
