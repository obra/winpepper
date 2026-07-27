# ASR model comparison — proof-run evidence (2026-07-27)

System-level proof of the 3-model ASR eval tooling: one small run per model
(5 clips, 1 pass each — NOT full profiles), followed by the comparison
aggregator. All runs executed via `scripts/run-asr-model-eval-windows.sh`
(WSL → Windows-host dotnet over the UNC path). Run outputs (results.json,
results.md, build/stage/corpus logs) live only under gitignored
`artifacts/asr-eval/<model-name>/`. This doc contains numbers and clip ids
only — no transcript or reference text.

## Driver commands (exact)

All three exited 0.

```bash
# Run 1 — production model (streaming)
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/AppData/Local/winpepper/models/nemotron-streaming-en \
  --model-name nemotron-streaming-en \
  --max-clips 5 --min-passes 1 --max-passes 1

# Run 2 — nemotron-3.5 (streaming, language en-US)
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/winpepper-evals/models/nemotron-3.5-asr-streaming-0.6b \
  --model-name nemotron-3.5-asr-streaming-0.6b --language en-US \
  --max-clips 5 --min-passes 1 --max-passes 1

# Run 3 — Qwen3-ASR (batch-only)
./scripts/run-asr-model-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --model-dir /mnt/c/Users/dan/winpepper-evals/models/qwen3-asr-1.7b \
  --model-name qwen3-asr-1.7b --batch-only \
  --max-clips 5 --min-passes 1 --max-passes 1
```

Qwen staging status: complete — the Task 10 setup script had fully staged both
candidate models before these runs (gguf verified at exactly 2185030624 bytes);
no re-run of `setup-asr-eval-models.sh` was needed and the sanctioned graceful
skip did not apply.

## Per-model results (results.md content, text-free by contract)

### nemotron-streaming-en

```
# ASR corpus eval: corpus-v1

- speech model: `nemotron-streaming-en`
- transcribe.cpp: `0.1.3`
- date: 2026-07-27, repeats: 1
- mode: streaming
- passes: 1, converged: no
- CPU: 39.3 s total, peak memory: 2266 MB, mean RTF: 0.922

| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack (runs) | truncated | error |
|---|---|---|---|---|---|---|---|---|
| 5230fdab47874a5d80e4ee875cea9cf3 | 10.2 | 0.037 | 0.010 | - | 105 | 0/1 | False | - |
| 4104d520ac07445ea98fcde27652d30a | 15.3 | 0.000 | 0.000 | - | 122 | 0/1 | False | - |
| b91b5b5fb8964e8f95145c1d74c1ae26 | 2.7 | 0.000 | 0.000 | - | 103 | 0/1 | False | - |
| 63477584e8954dddb63128f3fffb4e07 | 3.0 | 0.000 | 0.000 | - | 164 | 0/1 | False | - |
| 1a085efb4a814f9a882b58c517386ffc | 14.2 | 0.000 | 0.000 | - | 128 | 0/1 | False | - |

**Summary:** 5 clips (5 scored). WER mean 0.007 / median 0.000; CER mean 0.002. Post-stop latency p50 122 ms, p90 164 ms, max 164 ms. Fallbacks: 0. Truncations: 0. Silent clips: 0/0 pass. Failed: 0.
```

### nemotron-3.5-asr-streaming-0.6b

```
# ASR corpus eval: corpus-v1

- speech model: `nemotron-3.5-asr-streaming-0.6b`
- transcribe.cpp: `0.1.3`
- date: 2026-07-27, repeats: 1
- mode: streaming (language en-US)
- passes: 1, converged: no
- CPU: 40.0 s total, peak memory: 2428 MB, mean RTF: 0.915

| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack (runs) | truncated | error |
|---|---|---|---|---|---|---|---|---|
| 5230fdab47874a5d80e4ee875cea9cf3 | 10.2 | 0.111 | 0.030 | - | 114 | 0/1 | False | - |
| 4104d520ac07445ea98fcde27652d30a | 15.3 | 0.000 | 0.000 | - | 113 | 0/1 | False | - |
| b91b5b5fb8964e8f95145c1d74c1ae26 | 2.7 | 0.000 | 0.000 | - | 111 | 0/1 | False | - |
| 63477584e8954dddb63128f3fffb4e07 | 3.0 | 0.000 | 0.000 | - | 135 | 0/1 | False | - |
| 1a085efb4a814f9a882b58c517386ffc | 14.2 | 0.000 | 0.000 | - | 136 | 0/1 | False | - |

**Summary:** 5 clips (5 scored). WER mean 0.022 / median 0.000; CER mean 0.006. Post-stop latency p50 114 ms, p90 136 ms, max 136 ms. Fallbacks: 0. Truncations: 0. Silent clips: 0/0 pass. Failed: 0.
```

### qwen3-asr-1.7b

```
# ASR corpus eval: corpus-v1

- speech model: `qwen3-asr-1.7b`
- transcribe.cpp: `0.1.3`
- date: 2026-07-27, repeats: 1
- mode: batch
- passes: 1, converged: no
- CPU: 122.9 s total, peak memory: 3529 MB, mean RTF: 0.398

| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack (runs) | truncated | error |
|---|---|---|---|---|---|---|---|---|
| 5230fdab47874a5d80e4ee875cea9cf3 | 10.2 | 0.000 | 0.000 | - |  | 0/0 | False | - |
| 4104d520ac07445ea98fcde27652d30a | 15.3 | 0.000 | 0.000 | - |  | 0/0 | False | - |
| b91b5b5fb8964e8f95145c1d74c1ae26 | 2.7 | 0.000 | 0.000 | - |  | 0/0 | False | - |
| 63477584e8954dddb63128f3fffb4e07 | 3.0 | 0.000 | 0.000 | - |  | 0/0 | False | - |
| 1a085efb4a814f9a882b58c517386ffc | 14.2 | 0.000 | 0.000 | - |  | 0/0 | False | - |

**Summary:** 5 clips (5 scored). WER mean 0.000 / median 0.000; CER mean 0.000. Post-stop latency p50 3967 ms, p90 5373 ms, max 5373 ms. Fallbacks: 0. Truncations: 0. Silent clips: 0/0 pass. Failed: 0.
```

## Sanity-check output (Step 4, verbatim)

```
nemotron-streaming-en mode= streaming language= None passes= 1 converged= False clips= 5 meanWer= 0.007407407407407407 p50ms= 122 cpuS= 39.312 peakMB= 2266.2 meanRtf= 0.9222 unstable= 0
nemotron-3.5-asr-streaming-0.6b mode= streaming language= en-US passes= 1 converged= False clips= 5 meanWer= 0.02222222222222222 p50ms= 114 cpuS= 39.968 peakMB= 2427.9 meanRtf= 0.9154 unstable= 0
qwen3-asr-1.7b mode= batch language= None passes= 1 converged= False clips= 5 meanWer= 0 p50ms= 3967 cpuS= 122.891 peakMB= 3529.4 meanRtf= 0.3983 unstable= 0
```

All sanity bars met: modes streaming/streaming/batch; language null/en-US/null;
passes=1 and converged=false and clips=5 for all; meanWer in [0,1) (production
model's 0.007 mean over these 5 clips is consistent with its corpus baseline);
latencyP50Ms > 0; cpuSecondsTotal > 0; peakMemoryMb > 500; meanRtf > 0;
unstableTranscriptCount = 0. `passes`/`convergenceTrace` array lengths: `1 1`.

## Comparison aggregator (Step 5)

`compare` run (Linux dotnet, after CS0006 pre-clean and Release rebuild) exited 0:

```
compare: wrote artifacts/asr-eval/comparison.json (3 models: nemotron-3.5-asr-streaming-0.6b, nemotron-streaming-en, qwen3-asr-1.7b)
```

comparison.json model rows (model, mode, passes, converged, meanWer, latencyP50Ms, cpuSecondsTotal, peakMemoryMb):

```
nemotron-3.5-asr-streaming-0.6b streaming 1 False 0.02222222222222222 114 39.968 2427.9
nemotron-streaming-en streaming 1 False 0.007407407407407407 122 39.312 2266.2
qwen3-asr-1.7b batch 1 False 0 3967 122.891 3529.4
```

Transcript-text spot-check: `grep -c reference artifacts/asr-eval/comparison.json`
→ `0` (no occurrences of any kind; comparison.json carries no transcript text).

## Scope note

Full profiles (55-minute time budget per model, `--min-passes 2`, convergence)
are the caller's follow-up; these small runs prove the tooling end to end —
model parameterization, language forwarding, batch-only load path, resource
capture, per-model output dirs, and the comparison aggregator.
