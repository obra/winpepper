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

## AFTER (streaming architecture)

_To be recorded by the final task of this plan._
