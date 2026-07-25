# Streaming Transcription — Post-Stop Latency Evidence

Metric: **post-stop latency** — wall ms from "recording stopped" to "final
transcript available" (production `HistoryTimings.TranscribeMs` window).
Tool: `scripts/asr-latency-bench` (`dotnet run --project scripts/asr-latency-bench -c Release`).

Simulation assumptions (documented, identical for BEFORE and AFTER runs):
local realtime factor 0.30; cloud upload 400 ms; cloud batch processing 3.0 s
for a 10 s clip; AssemblyAI first-poll grace 750 ms + 1 s poll grid
(production `AssemblyAiOptions` values). `sim-*` rows exercise the real
production pipeline classes with only the ONNX/network edge replaced by these
delay models; `real-*` rows hit the real AssemblyAI API (run when
`ASSEMBLYAI_API_KEY` is set).

## BEFORE (batch architecture) — recorded 2026-07-25

| scenario | kind | audio | post-stop latency (ms) |
|---|---|---|---|
| sim-local-batch | simulated | 10 s | 3001 |
| sim-remote-batch | simulated | 10 s | 4196 |
| real-remote-batch | REAL network | 10 s | SKIPPED — no API key |

## AFTER (streaming architecture) — recorded 2026-07-25

| scenario | kind | audio | post-stop latency (ms) |
|---|---|---|---|
| sim-local-batch | simulated | 10 s | 3004 |
| sim-local-stream | simulated | 10 s | 912 |
| sim-remote-batch | simulated | 10 s | 4181 |
| sim-remote-stream | simulated | 10 s | 312 |
| real-remote-batch | REAL network | 10 s | SKIPPED — no API key |
| real-remote-stream | REAL network | 10 s | SKIPPED — no API key |

## Comparison (perceived transcription time, 10 s dictation)

| path | BEFORE (batch) | AFTER (streaming) | reduction |
|---|---|---|---|
| local | 3004 ms | 912 ms | 70% |
| remote | 4181 ms | 312 ms | 93% |

On Windows, production `HistoryTimings.TranscribeMs` (history archive) measures
this same post-stop window around the new FinishAsync call, so the improvement
is directly observable in real dictations after merge.
