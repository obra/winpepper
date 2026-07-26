# Streaming Transcription: Real-Model Verification Evidence

Replaces the simulated latency/quality evidence from
`docs/plans/2026-07-25-streaming-transcription.md` (whose committed bench rows
were simulated by construction: paced fakes returning the literal string
"simulated transcript" over tone-sweep audio). Closes the two accepted risks
from that work's assumption ledger:

- **A1** — "Chunked Parakeet encoding (1 s left context, running-stats norm,
  carried TDT state) yields acceptable transcript quality" (accepted with a
  mandatory Windows post-merge batch-vs-streamed transcript diff — this doc).
- **A16** — "Simulated local + optional real remote numbers satisfy the
  'prove it with before/after' requirement" (accepted; superseded by the real
  numbers below).

## Method

- Harness: `scripts/run-bench-windows.sh` → `scripts/asr-latency-bench`
  `real-local` scenario (production `ParakeetSession` batch vs
  `ParakeetStreamingSession` fed 50 ms frames at real-time pace; post-stop
  latency = time from last sample pushed to final transcript).
- Host: DANDESKTOP, Windows dotnet 9.0.316, UsingDirectML=True.
- Model: `C:\Users\dan\AppData\Local\winpepper\models\parakeet-tdt-0.6b-v3`
  (read-only).
- Audio: host TTS via `scripts/generate-bench-wavs.ps1` (System.Speech,
  16 kHz mono 16-bit). Quiet category = normal phrase at `--gain 0.02`
  (maxFrameRms 0.0050, inside the quiet-talker guard window
  0.002–0.0133); leading-silence category = normal phrase with
  `--lead-silence-ms 1500`.
- Streaming honesty check: every streamed row below ran with
  `fellBackToBatch=True` (the bench flags batch fallback; the fallback here
  is the LOUD, designed one — see next bullet — so the "stream" rows are
  batch-fallback outputs, not streamed-path outputs).
- Streaming defect context: a 2026-07-25 validation probe falsified the
  original streaming-parity assumption against the real model (only the
  first encoded chunk emitted tokens; later encodes decoded to blanks).
  Root cause is model-level/research-grade: carried TDT predictor state
  poisons subsequent decodes, mid-window decode starts collapse to blanks,
  and the int8 model emits zero tokens even for ideal batch-preprocessed
  3 s mid-utterance windows. Task 7b applied the loud-fallback +
  default-off safety valve (a blank-collapse guard in
  `ParakeetStreamingSession` detects zero non-blank emissions from any
  post-first encode, logs warnings, and `FinishAsync` returns the batch
  fallback over the full audio; `StreamingEnabled` now defaults OFF in
  `AppSettings` with a defect citation); the results below reflect that
  state.

## Results (REAL local Parakeet)

Every "stream post-stop" number below is a fallen-back run: the blank-collapse
guard tripped and `FinishAsync` batch-transcribed the full buffer. These
numbers are therefore batch latencies measured through the streaming session's
fallback path — NOT streamed-path latencies — and must not be read as the
before/after latency proof.

| category | audio | batch post-stop (ms) | stream post-stop (ms) | transcript diff |
|---|---|---|---|---|
| normal (~10 s dictation) | 10.6 s | 3366 | 2849 (fallback) | IDENTICAL after normalization (30 words) |
| pause-mid (2.0 s interior pause) | 8.5 s | 2920 | 2636 (fallback) | IDENTICAL after normalization (17 words) |
| quiet (gain 0.02) | 10.6 s | 3097 | 3803 (fallback) | IDENTICAL after normalization (29 words) |
| lead-silence (1500 ms) | 12.1 s | 3604 | 3379 (fallback) | IDENTICAL after normalization (30 words) |

The IDENTICAL diffs are expected by construction in the safety-valve state:
the "stream" transcript IS a batch transcription of the same full audio, so
the diff verifies the fallback path's correctness, not streamed-path parity.

### Transcripts (verbatim)

**normal** — batch:
> Please summarise the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.

**normal** — stream:
> Please summarise the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.

**pause-mid** — batch:
> Send a quarterly report to the finance team. And schedule the follow-up meeting for Thursday afternoon.

**pause-mid** — stream:
> Send a quarterly report to the finance team. And schedule the follow-up meeting for Thursday afternoon.

**quiet** — batch:
> Ple summarize the meeting notes from this morning and send them to the whole team. Then remind me to review the quarterly budget spreadshefor the planning session tomorrow afternoon.

**quiet** — stream:
> Ple summarize the meeting notes from this morning and send them to the whole team. Then remind me to review the quarterly budget spreadshefor the planning session tomorrow afternoon.

**lead-silence** — batch:
> Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.

**lead-silence** — stream:
> Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.

### Word-level diffs

```
# diff[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)
# diff[pause-mid.wav]: IDENTICAL after case/punctuation/whitespace normalization (17 words)
# diff[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (29 words)   (quiet, gain 0.02)
# diff[normal-10s.wav]: IDENTICAL after case/punctuation/whitespace normalization (30 words)   (lead-silence, 1500 ms)
```

All four diffs are identical because every stream row fell back to batch (see
above) — batch-vs-batch over the same audio. One honest quality note from the
BATCH path itself: the quiet category (gain 0.02) batch transcript is degraded
relative to the same phrase at gain 1 ("Ple summarize" for "Please summarise",
"spreadshefor" for "spreadsheet before") — a real low-signal quality
divergence in the batch model, identical across both rows and unrelated to
the streaming defect.

The streaming honesty flags, verbatim (category log in parentheses):

```
# stream[normal-10s.wav]: fellBackToBatch=True "Please summarise the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."   (normal.log)
# stream[pause-mid.wav]: fellBackToBatch=True "Send a quarterly report to the finance team. And schedule the follow-up meeting for Thursday afternoon."   (pause-mid.log)
# stream[normal-10s.wav]: fellBackToBatch=True "Ple summarize the meeting notes from this morning and send them to the whole team. Then remind me to review the quarterly budget spreadshefor the planning session tomorrow afternoon."   (quiet.log)
# stream[normal-10s.wav]: fellBackToBatch=True "Please summarize the meeting notes from this morning and send them to the whole team, then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon."   (lead-silence.log)
```

In every category the fallback was the BLANK-COLLAPSE guard (the designed
Task 7b outcome), not the quiet-talker guard — each of the four category
logs carries the same pair of guard warnings (verbatim, `normal-10s.wav` in
normal/quiet/lead-silence, `pause-mid.wav` in pause-mid):

```
# log[normal-10s.wav]: Warning: streaming encode decoded to zero tokens (known int8 chunked-decode defect); will batch-transcribe the full buffer at stop
# log[normal-10s.wav]: Warning: streaming decode collapsed to blanks (known int8 chunked-decode defect); batch-transcribing the full buffer instead of returning a truncated transcript
```

For quiet specifically: `maxFrameRms=0.0050` at `--gain 0.02` is inside the
usable window (0.002–0.0133), so the audio was NOT the problem — the gain
needed no adjustment and the scripted 0.02 was used as-is.

### InteriorSilenceSkipper telemetry (pause-mid)

The complete `# log[pause-mid.wav]` lines from the run:

```
# log[pause-mid.wav]: Warning: streaming encode decoded to zero tokens (known int8 chunked-decode defect); will batch-transcribe the full buffer at stop
# log[pause-mid.wav]: Warning: streaming decode collapsed to blanks (known int8 chunked-decode defect); batch-transcribing the full buffer instead of returning a truncated transcript
```

No skip-stat line was emitted, and none CAN be emitted in the safety-valve
state: the "streaming interior silence skipped: {Ms} ms across {Runs} runs"
log (`src/Winpepper.Asr/Transcription/ParakeetStreamingSession.cs`, in
`FinishAsync`) executes only on the streamed-success path, which the
blank-collapse guard preempts by design. The skipper itself did run (every
pushed frame passes through it, and its `Flush()` precedes the guard check),
but its skip telemetry is unverifiable from this run's logs. Recorded
honestly rather than pasted from expectation; this evidence gap follows
directly from the defect, and re-verification belongs with the future real
fix.

## Acceptance assessment

- Transcript parity: **NOT met** (streamed-path parity). No category produced
  a streamed-path transcript at all — every post-first encode decoded to
  blanks and the blank-collapse guard forced the batch fallback. The
  IDENTICAL diffs above verify only that the fallback returns the batch
  transcript, which is its designed behavior.
- Latency: **not citable**. A fallen-back stream's "latency" is a batch run's
  latency (e.g. normal: 2849 ms "stream" vs 3366 ms batch — both are batch
  transcriptions of the same 10.6 s buffer). Streamed post-stop latency
  cannot be presented as the before/after proof in this state, so the
  "dramatically lower" bar is **not met** and is not assessable until the
  underlying defect is fixed.
- Task 7b ended on the safety-valve path (defect not fixed): the streaming
  truncation defect is root-caused as model-level/research-grade (carried
  TDT predictor state poisons decodes; mid-window decode starts collapse to
  blanks; the int8 model emits zero tokens even for ideal batch-preprocessed
  3 s mid-utterance windows). Mitigation: the blank-collapse guard makes the
  failure LOUD (warnings above) and `FinishAsync` returns the batch fallback
  over the full audio, so users get a complete transcript, never a silently
  truncated one; and `StreamingEnabled` defaults OFF in `AppSettings` with a
  defect citation. Streamed latency is NOT presented as the before/after
  proof.

## Cloud (AssemblyAI)

`ASSEMBLYAI_API_KEY` was set on the host, so the cloud rows ran for real
(from `artifacts/bench/cloud.log`, real speech WAV `normal-10s.wav`):

```
  (transcript: "Please summarize the meeting notes from this morning and send them to the whole team. Then remind me to review the quarterly budget spreadsheet before the planning session tomorrow afternoon.")
  (drain deadline: 10 s)

| scenario | kind | audio | post-stop latency (ms) |
|---|---|---|---|
| real-remote-batch | REAL network | 10.6 s | 1336 |
| real-remote-stream | REAL network | 10.6 s | 421 |
```

The remote streaming path (AssemblyAI websocket) shows the expected shape:
streamed post-stop 421 ms vs batch 1336 ms over the same 10.6 s audio. This
is the cloud provider's streaming, not the local Parakeet path, and does not
substitute for the local streamed-path evidence above.

## Windows pre-push gate result

<filled by the final gate run — see "Gate summary" below>

## Cross-references & environment honesty

- `scripts/windows-sandbox/README.md` (untracked, main checkout) says no
  audio testing is possible in Windows Sandbox ("No real microphone ... you
  cannot test the full hold-to-dictate audio pipeline"). That remains true
  for Sandbox, but model-level audio-FILE testing works today via
  `./scripts/run-bench-windows.sh` (this doc's harness). The file is
  untracked in the main checkout, so this note lives here instead of editing
  it.
- Full end-to-end audio (mic → hotkey → paste) is covered by
  `docs/manual-test.md`'s QEMU audio-passthrough procedure, which is
  currently NOT provisioned on this machine: no VM image, no PulseAudio
  server under WSLg, no piper, no sshpass. Stated honestly rather than
  implying it works.
