# ASR Eval Framework — End-to-End Evidence (Task 12)

Real runs against the real corpus on the real Windows host. Numbers and clip ids only — no
transcript or reference text appears in this document (privacy rule).

## Branch taken

**KEY ABSENT.** `ASSEMBLYAI_API_KEY` was not set in the environment, so reference transcripts
were NOT generated (never fabricated). The eval below still proves streaming, post-stop latency,
and batch-parity diffs — without WER/CER scoring. What remains for the user is listed at the end.

## Run metadata

- corpus: `corpus-v1` (`C:\Users\dan\winpepper-evals\corpus-v1`, outside the repo)
- speech model: `nemotron-speech-streaming-en-0.6b-Q8_0`
- transcribe.cpp: `0.1.3`
- date: 2026-07-26, repeats: 3

## Commands run

```bash
# 1. Corpus present (from Task 3): 10 entries, 10 WAVs confirmed.
python3 -c "import json;m=json.load(open('/mnt/c/Users/dan/winpepper-evals/corpus-v1/manifest.json'));print(len(m['entries']),'entries')"

# 2. Exporter re-run (re-runnability + dedupe proof; grew the corpus 10 -> 15):
dotnet run --project scripts/asr-eval-corpus -c Release -- export \
  --history /mnt/c/Users/dan/AppData/Local/winpepper/history \
  --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --take 5

# 3. References: NOT run (ASSEMBLYAI_API_KEY absent -- see "Branch taken").

# 4. Eval on the Windows host (all 15 clips, 3 repeats):
./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
```

## Exporter output (re-runnability proven)

```
export: 5 new clips copied, 10 already in corpus, 0 missing WAVs
```

Post-run uniqueness check (`len(ids), len(set(ids))` over the manifest): `15 15` — all ids
unique; the 10 pre-existing clips were deduplicated by stable id, not re-copied. Because the
corpus grew to 15 clips, the eval below was run over all 15.

## Eval driver run

All four driver stages completed (pre-clean, UNC build on Windows dotnet, %TEMP% staging,
corpus run); driver exit code 0. `artifacts/asr-eval/` (gitignored) received `results.json`,
`results.md`, `build.log`, `stage.log`, `corpus.log`.

### Full `results.md` content

```markdown
# ASR corpus eval: corpus-v1

- speech model: `nemotron-speech-streaming-en-0.6b-Q8_0`
- transcribe.cpp: `0.1.3`
- date: 2026-07-26, repeats: 3

| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack | truncated | error |
|---|---|---|---|---|---|---|---|---|
| 5230fdab47874a5d80e4ee875cea9cf3 | 10.2 | no ref | - | - | 103 90 91 | False | False | - |
| 4104d520ac07445ea98fcde27652d30a | 15.3 | no ref | - | - | 125 131 133 | False | False | - |
| b91b5b5fb8964e8f95145c1d74c1ae26 | 2.7 | no ref | - | - | 139 109 107 | False | False | - |
| 63477584e8954dddb63128f3fffb4e07 | 3.0 | no ref | - | - | 131 125 116 | False | False | - |
| 1a085efb4a814f9a882b58c517386ffc | 14.2 | no ref | - | - | 113 125 112 | False | False | - |
| dba4517931a0473b85c42f969e712198 | 6.1 | no ref | - | - | 114 116 117 | False | False | - |
| 854522803ec347cc93eeae636f4f328c | 7.2 | no ref | - | - | 107 118 132 | False | False | - |
| 46b528dda8f3430087e11d4d0cb8ac4a | 12.9 | no ref | - | - | 130 144 133 | False | False | - |
| 7937057931be40dbadbba37c8584fe41 | 16.6 | no ref | - | - | 133 177 149 | False | False | - |
| a6e17b56300b42fa892c5719b68c9ddb | 10.6 | no ref | - | - | 132 112 135 | False | False | - |
| 6a670204b8dc47159c2707a506cb598f | 18.4 | no ref | - | - | 110 104 121 | False | False | - |
| 29fda6a35f9f4319a777a9250bf5a7d0 | 14.4 | no ref | - | - | 200 187 202 | False | False | - |
| 4d99e3a4d3474565bb886e97dc0564fa | 4.1 | no ref | - | - | 202 216 199 | False | False | - |
| d842ab76fdd54539af582201fc3f2fd6 | 6.8 | no ref | - | - | 252 242 255 | False | False | - |
| d49bd68d0c834ef7bae24905c7c63a42 | 3.1 | no ref | - | - | 202 189 198 | False | False | - |

**Summary:** 15 clips (0 scored). WER mean n/a / median n/a; CER mean n/a. Post-stop latency p50 131 ms, p90 202 ms, max 255 ms. Fallbacks: 0. Truncations: 0. Silent clips: 0/0 pass. Failed: 0.
```

### Honesty controls verified

- `corpus.log` contains one `# corpus[<id>]: fellBack=... truncated=... wer=... finishMs=...`
  line per clip: 15/15, ids exactly matching the manifest — the production streaming path ran
  every clip. Zero `FAILED` lines; `Failed: 0` in the summary; driver exit 0.
- Batch parity: all 15 clips reported
  `parity: IDENTICAL after case/punctuation/whitespace normalization` (clip word counts 5–35) —
  the streaming path matches the batch transcription of the same audio on every clip.
- Privacy check on `results.md`: every table row is a 32-hex clip id followed by numbers/flags
  only; no transcript text (verified mechanically, and guarded by unit test
  `ToMarkdown_HasPerClipRowsAndSummary_ButNoTranscriptText`).

## Step 5 spot-check (references)

**Pending.** No reference transcripts exist (key absent), so the mandatory human spot-check —
listening to a handful of clips against their `.reference.txt`, including confirming at least
one clip with an audible spoken filler retains "um"/"uh" — could not be performed. No
conclusions about model ranking are drawn in this document.

The number/currency/date rendering spot-check (references vs transcripts) is likewise pending
for the same reason.

## Remaining for the user

After `export ASSEMBLYAI_API_KEY=...`, run:

```bash
dotnet run --project scripts/asr-eval-corpus -c Release -- references --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1
./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
```

Then perform the Step 5 human spot-check (clip ids + pass/fail recorded here, never reference
text), including the filler ("um"/"uh") retention check and — if any clip contains
number/currency/date content — the digit-rendering comparison (ids + ok/mismatch counts only).
