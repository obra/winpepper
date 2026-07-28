# Cleanup model bake-off — preparation notes

Status: PREP COMPLETE — bake-off NOT started. This doc records the baseline
evidence, the fixes that unblock multi-model work, and the researched options,
so the bake-off can be designed from data instead of anecdotes.

## 1. What landed in this prep phase

- `df7443e` fix(cleanup): cleanup model selection now resolves via
  `CleanupModelPathResolver` (registry key + `CleanupModelName` setting) instead
  of first-`.gguf` glob; silent registry fallback and missing files are logged.
  Silent prompt-budget failures made observable (window-context truncation,
  prompt char lengths + maxTokens, generation char-cap trip).
- `30a1ad8` feat(cleanup): cleanup latency bench
  (`scripts/cleanup-latency-bench/`, driver `scripts/run-cleanup-bench-windows.sh`),
  mirroring the ASR bench conventions (linked-file rule, %TEMP% staging,
  read-only host access, results.md = numbers/ids only).
- Known deferred item (pre-existing, still open): the cleanup backend is
  boot-frozen — changing `CleanupModelName` needs an app restart to take
  effect. A swappable holder (cf. `NemotronEngineHolder`) is a bake-off-adjacent
  follow-up, not required for benching.

## 2. Baseline: Qwen 2.5 0.5B Instruct Q4_K_M (2026-07-27)

Run: 118 statements (100 real dictations exported read-only from history +
18 committed eval cases), 3 passes, seed 42, timeout 15000 ms, production
`CleanupRunner` path. Full detail: `artifacts/cleanup-bench/20260727-115614/`
(gitignored; results.md is quotable, results.json holds transcript text).

| Metric | Value |
|---|---|
| Llm-path calls | 282 |
| p50 / p95 / mean call | **334 ms / 572 ms / 368.2 ms** |
| Model load (once) | 1037 ms |
| Warm (once) | 266 ms |
| Paths | Llm=282, BypassShort=48, FallbackImplausible=24 |
| Errors | 0 |

Findings:

- **The 6.8 s anecdote in DEVELOPMENT.md is not representative of steady-state.**
  Warm per-call latency is ~1/20th of that. The old number likely captured
  cold-start (load + Vulkan shader compile + first call) or worse hardware
  conditions. Latency headroom for a bigger model is therefore much larger
  than previously believed: even a ~5x-cost 4B-class model would land near
  ~1.7 s p50 on this machine (to be measured, not assumed — and low-end
  hardware tiers still need their own numbers).
- **Quality guard pressure is measurable on real data:** 8 of 102 LLM-eligible
  statements (24 of 354 runs) were rejected as implausible on every pass —
  ~8% consistent fallback rate on real dictations, on top of the 4 known-failing
  eval baselines. This is a per-model comparison metric for the bake-off.
- Latency scales with statement length as expected (guard-long-multisentence,
  255 chars: ~450 ms; typical 30–90 char dictation: ~280–340 ms).
- Two first-pass outliers (1.8 s, 2.4 s) suggest residual first-touch effects;
  passes 2–3 are tight. Report medians per statement in comparisons.

Bench usage (from WSL):

```
./scripts/run-cleanup-bench-windows.sh                        # default model, 3 passes
./scripts/run-cleanup-bench-windows.sh --model <registry-key> --passes 5
./scripts/run-cleanup-bench-windows.sh --statements <file>    # skip history export
```

## 3. Candidate models (researched 2026-07-27)

Constraint envelope: CPU-only up to 4 GB VRAM; Q4 GGUF ≤ ~2.5 GB; llama.cpp
(LLamaSharp) Vulkan; non-thinking preferred (DRES: reasoning modes over-delete).

| Candidate | Q4 GGUF | IFEval* | Notes |
|---|---|---|---|
| Qwen2.5-0.5B (baseline) | 491 MB | 27.9 | two generations behind |
| **LFM2.5-1.2B-Instruct** | 731 MB | 86.2 (IFBench 47.3) | best IF-per-byte; KV cache @4096 = 48 MiB (same as baseline); official GGUF; LFM Open License (<$10M revenue cap — business decision) |
| **Granite-4.0-1b** | 1024 MB | ~79.6 | Apache 2.0, official IBM GGUF, pure transformer (llama.cpp-safe) |
| Granite-4.0-h-1b | 901 MB | ~80.1 | hybrid SSM — verify llama.cpp/LLamaSharp support first |
| Qwen3-4B-Instruct-2507 | 2497 MB | 83.4 | top of envelope; ~5x latency; 4 GB VRAM marginal at ctx 4096 |
| Gemma-3-4B-it | 2490 MB | 90.2 | best 4B at long ctx; Gemma ToU license |

*Vendor-reported, harness-sensitive — directional only. The bake-off's own 18
eval cases + implausible-rate on real dictations are the real yardstick.

Avoid: Qwen3.5 small (IF regression vs Qwen3), Llama 3.2 (obsolete),
Gemma 3n/4 E2B (GGUF 3.0–3.3 GB), EXAONE (non-commercial license).
Watch: Microsoft **Aion-1.0-Instruct** (Edge on-device writing-assistance model,
open weights promised July 2026 — not on HF as of 2026-07-27; re-check).

## 4. Fine-tuning path (researched 2026-07-27)

A directly relevant shipped precedent exists: **SottoASR** (Apr 2026) fine-tuned
LFM2.5-350M for exactly this task.

- **Dataset: `juanquivilla/sotto-transcript-cleanup` (MIT, 124K pairs)** —
  categories map ~1:1 to our prompt rules: self_correction 14%,
  preserve_wording 13%, filler_removal 11%, crutch_words 8%, false_start 8%,
  dictation_commands 8%, misheard_words 7%, adversarial 5%. The only public
  dataset covering dictation commands. Claim: fine-tuned 350M beats a prompted
  2B on this task at 8x speed.
- **Licensing hard lines:** Switchboard/Fisher (incl. DRES training data) are
  LDC research-only — unusable for shipped weights. PodcastFillers metadata
  prohibits commercial deployment. IWSLT/TED is NC. Permissive: sotto (MIT),
  Disfl-QA (CC BY 4.0), LARD (CC BY 4.0). Synthetic generation: check the
  generator LLM's ToS before generating at scale.
- **Key lessons from the precedent (adopt, don't re-learn):**
  1. Plain completion format (`### Input:` / `### Output:`), NO chat template —
     removes the model's affordance to answer content (our #1 failure mode).
  2. LoRA was not enough for preservation behavior; full fine-tune at
     350M–600M is cheap — budget for it.
  3. Build a **substantive-deletion metric** before training (strip fillers
     from input, count real-word survival) — filler metrics alone lie.
  4. Gap-generate what sotto lacks: explicit "scratch that / never mind"
     meta-commands and an imperative-resistance class ("tell me a joke" →
     cleaned imperative, not an answer).
  5. Train bf16, merge, `convert_hf_to_gguf.py` → `llama-quantize`; smoke-test
     base-model GGUF conversion on day one for the chosen family.
- **Base model candidates for a fine-tune:** Granite-4.0-350m / Qwen3-0.6B
  (Apache 2.0, cleanest) benchmarked against LFM2.5-350M (proven for the task,
  revenue-capped license).

## 5. Bake-off design sketch (NOT started)

1. Fix restart-frozen backend or accept restart-per-model during testing.
2. Registry gains candidate `ModelKind.Cleanup` entries (eval slots support 4
   models per run; hashes via `scripts/verify-model-hashes.ps1`).
3. Per model: (a) 18-case prompt eval (chatbot traps / self-corrections /
   fillers / guards), (b) latency bench on the same 118-statement corpus,
   (c) implausible-fallback rate on real dictations, (d) memory footprint.
4. ~~Chat-template caution~~ DONE (293724f): per-model prompt formats
   (`chatml` / `granite` / `raw-io`) via `CleanupPromptFormatter`, declared on
   `ModelDescriptor.PromptFormat` and threaded through every construction site.
5. Compare off-the-shelf winners vs a sotto-seeded fine-tune of a 350M–1.2B
   base; decide on quality-per-latency, license, and footprint.

## 6. Candidate integration + first-contact results (2026-07-27)

Landed: per-model prompt formats + registry entries (`293724f`), rejection
diagnosis fixes + bench raw-output/`--verbose`/`--gpu-layers` (`619e05c`).
Registry now holds 4 cleanup models (eval slots exactly full); qwen stays
default. Test models staged at `C:\Users\dan\winpepper-models-test` (bench
`--models-root`, eval `WINPEPPER_MODELS_ROOT`).

**Sotto GGUF conversion** (no public GGUF exists): converted from
`juanquivilla/sotto-cleanup-lfm25-350m` (MIT) via llama.cpp
`convert_hf_to_gguf.py --outtype q8_0`. Two tokenizer_config.json edits were
required (transformers-v5 artifacts): `tokenizer_class` →
`PreTrainedTokenizerFast`, and drop `extra_special_tokens: []`. Output:
379,215,808 bytes, sha256 `67113c65…5d962d` (in registry, manual-install-only).

### Results — 118-statement bench + 18-case eval, same host as §2 baseline

| Model | p50 / p95 / mean (Llm ms) | Implausible | Eval (18 cases) |
|---|---|---|---|
| qwen2.5-0.5b (baseline) | 334 / 572 / 368 | 24 of 354 | 14/18 — fails 3 chatbot traps (ANSWERS content) + 1 self-corr applied BACKWARD |
| **sotto-350m (raw-io)** | **289 / 475 / 307** | **0** | **14/18 — ALL 8 chatbot traps pass**; fails 3 self-corr (meta-command not applied, text kept) + keeps "sort of" |
| lfm2.5-1.2b (chatml) | 304 / 438 / 327 (48 accepted) | 252 → fix landed, re-run pending | pending |
| granite-4.0-1b | — | 300 of 300 | model-side: Vulkan garbage |

Key judgments:

- **Sotto's failures are non-destructive under-edits** (keeps the correction
  words as literal text); qwen's failures are catastrophic (answers the
  dictation, deletes the wrong clause). Same 14/18 count, very different risk.
  Sotto is also ~10% faster, 23% smaller (379 vs 491 MB), and had ZERO
  implausible-guard rejections on 100 real dictations.
- **LFM2.5 84% rejection was OUR bug**: it echoes the one-shot example in
  `BasePrompts.Default` verbatim (reproduced with the vendor template on CPU —
  not a template or Vulkan issue). Fixed via `ModelDescriptor.OmitPromptExample`
  → `BasePrompts.DefaultNoExample`. Verification re-run pending.
- **Granite-4.0-1b Q4_K_M is broken on the Vulkan backend**: degenerate token
  salad ("$118$150$once…") on GPU, clean output for the same GGUF+prompt on
  CPU llama.cpp — matches IBM's published numerical-range warning. Options:
  Q8_0 variant (~1.7 GB), `--gpu-layers 0`, or drop from the Vulkan bake-off.
- **Parallel eval fixture crash fixed**: the 4 slot classes now share one
  `DisableParallelization` collection (concurrent Vulkan model loads crashed
  the process natively).

### Pending (blocked 2026-07-27 by dead WSL→Windows interop; resume after
`wsl --shutdown` / fresh terminal)

```
./scripts/windows-gate.sh   # gate for 619e05c before any push
./scripts/run-cleanup-bench-windows.sh --model lfm2.5-1.2b-instruct-q4_k_m --models-root /mnt/c/Users/dan/winpepper-models-test
./scripts/run-cleanup-bench-windows.sh --model granite-4.0-1b-q4_k_m --models-root /mnt/c/Users/dan/winpepper-models-test --gpu-layers 0 --passes 1
# eval per model (serialized slots), e.g.:
#   WINPEPPER_MODELS_ROOT=C:\Users\dan\winpepper-models-test dotnet exec Winpepper.Cleanup.Tests.dll -class Winpepper.Cleanup.Tests.CleanupPromptEvalModelSlot1
```

### Recommendation as of today

Sotto-350m is already the best candidate measured: strictly better failure
profile than the current model at lower latency and footprint. Its remaining
gap (explicit "scratch that / never mind" meta-commands) is exactly the
gap-fill fine-tune identified in §4 — a targeted fine-tuning run on top of the
MIT sotto dataset with added meta-command + imperative-resistance pairs is the
highest-leverage next step. Cleanup of `/home/dan/models-work` (~6 GB of
conversion artifacts) can happen once the bake-off no longer needs re-conversion.
