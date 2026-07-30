# Plan: Stop the cleanup model from slowing down live transcription

Repo: /home/dan/code/winpepper (main @ 72dc112).
Status: APPROVED FOR EXECUTION (2026-07-29). Rev 5 — three council rounds plus a
targeted spot-check (PASS) of round 3's fix list. Execute per the numbered sequence in
"How this gets executed"; the owner gate at step 3 is a real stop.

## Rules for whoever executes this plan (binding)

- All file paths are relative to the repo root. Work ONLY under the repo root `src/`,
  `tests/`, and `scripts/`. NEVER read or edit anything under `.worktrees/` — stale
  copies of the same files live there and will mislead you.
- Line numbers and file paths in this document are pointers to evidence, not claims.
  If code is not at the stated location, search by filename or symbol under `src/`
  only, verify the claimed substance, record the corrected location in the evidence
  file, and continue. STOP only when the claimed code or behavior cannot be found
  anywhere under `src/`, or contradicts the claim in substance.
- This plan file is read-only during execution. The run-1 executor creates
  `docs/plans/2026-07-29-cleanup-asr-contention-evidence.md` as its first action; all
  findings, measurements, and evidence go there.
- Any validation that requires real spoken dictations is performed by the OWNER (Dan)
  on the Windows machine. The executor may pre-compute tallies from the logs, but only
  the owner signs criteria off as met.

## The problem (measured) — and both halves of the goal

WinPepper runs two AI models: the speech recognizer (nemotron, via transcribe.cpp, on
the CPU) which transcribes while the user speaks, and the cleanup model (sotto-350m, via
LLamaSharp) which tidies the transcript afterward. They never run at the same time — the
pipeline is strictly one-step-after-another. Yet the measurements show that merely having
the cleanup model loaded and enabled makes the speech recognizer's native processing
calls slower WHILE THE USER IS SPEAKING:

- Cleanup off: slowest native call 236 ms; zero calls over 250 ms.
- Cleanup on: slowest native call up to 1151 ms; 1–8 calls over 250 ms per dictation;
  occasionally the user waits an extra ~1 second at the end (`asr_wait` on the timing line).

The goal has two halves, and both are pass criteria: (1) remove the slowdown, AND
(2) preserve cleanup context quality — the cleanup model's "window context" input must
keep working as well as it does today. A fix that wins speed by silently starving the
cleanup model of context is a failure.

Baselines are recorded in `%LOCALAPPDATA%\winpepper\logs\winpepper-20260729.log`.

Separate issue, OUT OF SCOPE, must not be muddied: rare 5–16 second transcription stalls
that happen even with cleanup completely disabled. That investigation continues on its
own; this plan must keep its measuring instruments intact.

## Where this plan came from

Three planning agents worked the problem independently, each optimizing for a different
goal: (A) smallest safe change, (B) protect transcription speed at any cost, (C) long-term
robustness and measurability. This plan takes A's discovery and minimal fix as the core,
C's measurement ideas for Phase 0, and B's protections as a later phase that only happens
if the numbers demand it. B's biggest idea (moving cleanup into a separate process) is
recorded as a last resort, not built. A seven-lens council reviewed the plan twice
(2026-07-29); this revision incorporates both rounds' required corrections.

## Claimed code facts (re-check each one before relying on it; the mismatch rule above applies)

- `src/Winpepper.Cleanup/LlamaCleanupBackend.cs:88` creates a fresh StatelessExecutor for
  every cleanup generation; its ModelParams (:43-47) set only ContextSize and
  GpuLayerCount=999. Threads and BatchThreads are left at "autodetect", which means the
  cleanup model may use every CPU core when it runs. LLamaSharp 0.27 offers Threads and
  BatchThreads settings; it offers NO thread-priority or core-affinity settings.
  LLamaSharp arrives as a NuGet package reference — there is NO copy of its source code
  in this repo.
- transcribe.cpp v0.1.3 exposes NO thread setting at all
  (`src/Winpepper.Asr/TranscribeCpp/TranscribeCppNative.cs:31-37`).
- Verified by five council reviewers at main @ 72dc112 (still re-check): when cleanup and
  its "window context" feature are both enabled, `src/Winpepper.App/Hosting/PipelineHost.cs`
  starts a context prefetch AT THE MOMENT RECORDING STARTS (hold arm ~:526-534, toggle
  arm ~:1027-1035). That prefetch walks the focused window's UI tree (Windows UI
  Automation) and, when that fails or yields too little, captures the window image and
  runs text recognition on it (OCR) — see
  `src/Winpepper.Platform/WindowContext/WindowContextPrefetch.cs:32-56`. This is the only
  cleanup-related work that runs while the user is speaking. It is bursty, costs hundreds
  of milliseconds, and appears exactly when cleanup is enabled — it fits the measured
  symptom precisely. Note for 0b: the prefetch's result type (`WindowContextResult`)
  already carries a Source value (Uia/Ocr/Empty), but it is never logged today, and
  CleanupRunner receives only the finished text — today, both the source AND whether
  cleanup actually consumed the context in time are invisible at that boundary. 0b must
  surface consumption: CleanupRunner's RESULT (not its input signature, which stays
  narrow) may carry a consumed-context indicator so PipelineHost can stamp truthfully.
- UNRESOLVED disagreement between planners (settled by step 0c): Planner B says
  StatelessExecutor's constructor creates and immediately disposes its native context,
  and each generation's working context is disposed via `using` — so nothing native
  lingers between generations. Planner C says a native context DOES outlive each
  generation until the garbage collector finalizes it. Both, either, or neither may be
  wrong. Do not assume; follow 0c's evidence procedure.
- The background "prewarm" (loading + warming a newly selected cleanup model: 1–1.7 s of
  load plus a warm-up generation, on all cores, at normal priority, via Task.Run) can
  overlap the NEXT recording if the user starts dictating again within ~2 seconds
  (`src/Winpepper.Cleanup/CleanupBackendHolder.cs`, LoadCore). The timing line's
  `prewarm_active=` field already flags when this happened.

## Outlier rule (used by 0a, Phase 1 validation, and the Phase 2 gate — one rule, one home)

A dictation is an EXCLUDED OUTLIER when ALL THREE hold: asr_wait > 2000 ms AND
backlog_ms > 2000 AND native_max > 2000 ms. (Contention has never produced a native call
over 1151 ms; the separate cleanup-off stalls are 5–16 s monsters. The third condition
prevents a genuine 2–5 s contention regression from excluding itself.) Excluded lines are
never silently dropped: they are COUNTED, listed in the evidence file, and their
over250_at offsets adjudicated against the logged prefetch, prewarm, and
garbage-collection windows. Lines with prewarm_active=true are not outliers; they are
filtered from Phase 1 acceptance and tallied separately for 1c's re-open trigger.

## Phase 0 — Find the real cause before changing behavior

0a. **Free experiment (owner action, zero code; happens AFTER run 1's instrumentation
    lands — see "How this gets executed").** In Settings: turn OFF the cleanup
    "window context" option, keep cleanup itself ON. Dictate normally until 20
    non-excluded, prewarm_active=false timing lines exist. Record the outcome in the
    evidence file, then turn the option back ON. Branches (counted once, over the FIRST
    20 qualifying lines in log order):
    - CONFIRMED (at most 2 lines show any native call over 250 ms): the prefetch is the
      dominant cause → proceed to 1a.
    - NO CHANGE (10 or more lines show a native call over 250 ms): STOP. The prefetch
      theory is refuted; do not do 1a. A human reviews before any further work.
    - PARTIAL (3–9 lines show a native call over 250 ms): more than one cause exists →
      proceed to 1a; the Validation section's criteria remain the single success
      definition, and the evidence file must note that Phase 2 is the expected follow-up.
0b. **Better measurement on the timing line.** Three additions, all formatted in the pure
    DictationTimingSummary helper (tested on Linux); zero behavior change:
    - `ctx_src=(uia|ocr|none)`: which context the cleanup model ACTUALLY CONSUMED —
      consume-time semantics, not produce-time. Stamp `none` whenever the prefetch task
      was not complete at the moment CleanupRunner stopped waiting for it, regardless of
      what the task later produced; otherwise stamp the task's WindowContextResult.Source.
      The stamp lives in PipelineHost, where the prefetch task is held; to know that
      cleanup "stopped waiting", surface a consumed-context indicator on CleanupRunner's
      RESULT (its input signature stays narrow). Baseline note: baseline collection
      (prefetch still launched at recording start) is unchanged by these semantics —
      there, production and consumption coincide because the prefetch finishes long
      before cleanup needs it.
    - `over250_at=[...]`: offsets (milliseconds from recording start) of each native
      call that exceeded 250 ms. Values unclamped (offsets after the stop request are
      themselves evidence); list capped at 16 entries plus an overflow count. Used to
      match slow events against the logged time windows of prefetch, prewarm, and
      garbage collection — matching timestamps to events is the primary attribution
      method.
    - `proc_cpu_ms` (secondary hedge only — timestamps above are the primary method):
      the whole process's CPU time between recording start and the stop request (sample
      `Process.TotalProcessorTime` at recording start and at the StopRequested handling —
      hold arm ~:538, toggle arm ~:1039). Do NOT sample at the summary-emit point: that
      happens after cleanup inference and would make the field a useless constant.
      Interpret relative to the same dictation's recording length and to cleanup-off
      baselines; the recognizer's own threads are included.
0c. **Settle the B-versus-C disagreement with evidence only.** There is no LLamaSharp
    source in this repo. Either (i) fetch the official LLamaSharp v0.27.0 tag from
    GitHub and read StatelessExecutor.cs, or (ii) decompile the NuGet-resolved assembly
    (for example with ilspycmd). The finding MUST quote the file and line numbers into
    the evidence file. If neither is possible in the execution environment, write
    "could not verify" and treat 1d as NOT triggered — never settle this from memory.

## Phase 1 — Ship the confirmed, low-risk changes

1a. **Move the window-context prefetch from recording-start to recording-stop** — only
    if 0a landed on CONFIRMED or PARTIAL. Relocate the launch in BOTH arms (hold
    ~:526-534 → immediately after the stop request in HoldUp ~:538; toggle arm
    likewise). Four required design details:
    (a) Give the prefetch its own per-dictation CancellationTokenSource and CANCEL it
        when a dictation is dropped as silent and on teardown. Today `_ctxPrefetchTask`
        is nulled (~:591/~:1091) without cancelling; after the move, every
        silence-dropped dictation would otherwise leave a full OCR burst running.
        RULING (rapid re-dictation): a new dictation's recording start CANCELS any
        prior dictation's still-running prefetch; the prior dictation takes the
        no-context path and stamps `ctx_src=none` — an accepted, counted loss (live
        speech wins over a stale context fetch).
    (b) Pass the injection target captured at recording start (`_targetAtStart`, ~:520)
        to the relocated prefetch instead of re-reading the focused window at stop —
        otherwise a focus change during recording captures the WRONG window's content
        and the cleanup model rewrites the user's words with someone else's context.
    (c) Add a test that exercises the real path where the per-dictation
        CancellationTokenSource is created, and asserts the timing line's CONSUMED
        stamp reads `ctx_src=uia` (0b's consume-time semantics — not merely that the
        prefetch produced non-empty text) for a normal dictation — guards the trap
        where a cancelled token makes the prefetch quietly return empty, latency looks
        great, and context quality silently dies.
    (d) Two named test cases with named observables: rapid re-dictation (a new dictation
        starting under 2 seconds after the last — assert that at N+1's recording start,
        dictation N's prefetch is no longer running (cancelled per 1a(a)'s ruling if it
        had not finished), that N stamps `ctx_src=none` when cancelled, and that N and
        N+1 hold DISTINCT CancellationTokenSource instances), and
        silence-drop-then-dictate (assert the dropped dictation's prefetch was cancelled
        and nothing from it is observable in the next dictation's context).
    Latency budget: the prefetch now overlaps mic-stop, trimming, and transcription
    finish, plus CleanupRunner's existing context wait (locate the wait and its budget
    constant in `src/Winpepper.Cleanup/CleanupRunner.cs`; ~500 ms). The UI-tree path
    (the common case) fits comfortably; the OCR-fallback minority may sometimes miss the
    budget and take the existing no-context path (same file, the branch where context
    arrives too late and cleanup proceeds without it).
1b. **Cap the cleanup model's CPU threads — DECIDED: ships with 1a in run 2.**
    (Council split 5–2 on whether this rides along or waits; the owner delegated the
    call and the majority position is adopted, with its binding conditions.) Set
    ModelParams.Threads = BatchThreads = max(1, ProcessorCount/2) in LlamaCleanupBackend,
    expressed as a NAMED CONSTANT with a comment stating why. With 1c cut (below), this
    cap is the only remaining bound on the burst that happens when the user re-dictates
    within ~2 s of a model prewarm starting. Binding conditions: (i) 1b is judged ONLY
    on the cleanup bench (`scripts/run-cleanup-bench-windows.sh`) — median latency
    ≤ 1000 ms and unchanged evaluation outcomes — never on the recognizer's numbers;
    (ii) the prewarm_active sampling filter is defined once, in the Outlier rule above;
    (iii) the parked 1c has a written re-open trigger (see 1c).
1c. **CUT (council unanimous).** The earlier idea — "don't start a prewarm while
    recording" — was a check-then-act race, added a way for the app to hang at quit,
    and blinded its own validation. PARKED, with this re-open trigger: reconsider only
    if, after 1a and 1b, dictations that start under 2 seconds after a prewarm began
    show native_over250 > 0 on more than 2 of the first 10 such dictations in log order.
1d. **Dispose the cleanup model's native context explicitly after each generation** —
    only if 0c's quoted evidence proves Planner C right (a native context really does
    linger). This is the single home for that fix; 0c only gathers the evidence.

## Phase 2 — Stronger protection (only if the numbers demand it)

Gate: run Phase 2 only if, AFTER Phase 1, more than 2 of the FIRST 30 filtered
(prewarm_active=false), non-excluded timing lines in log order still show
native_over250 > 0 — evaluated once, on that fixed set. The Outlier rule above applies;
excluded lines are counted, reported, and adjudicated in the evidence file.

2a. **Give the audio-feed loop its own thread at high priority.** Convert the
    StreamingDictationSession pump from Task.Run to a dedicated Thread at
    ThreadPriority.Highest (no multimedia-scheduler registration unless measurement
    demands it). This also shields transcription against the unknown environmental
    5–16 s stalls. Stamp `pump_qos=` on the timing line so runs are attributable.
    Guard against priority inversion: the pump path must share no locks with cleanup
    paths (WasPrewarmActiveSince is already lock-free; add a test pinning that the pump
    never takes CleanupBackendHolder's internal lock).
2b. **Regression alarm.** Log a warning whenever the slowest native call exceeds 250 ms
    while a cleanup model is loaded (constant beside AsrWaitBudgetMs; the classification
    lives in the pure helper, tested on Linux).

## Set aside on purpose (recorded, not built)

- **Running cleanup in a separate worker process** (low OS priority, low memory
  priority, restricted cores, inter-process messaging): the only change that makes
  transcription structurally immune, but out of proportion to the measured harm.
  Escalation trigger: more than 2 of the first 30 filtered, non-excluded timing lines
  in log order still show native_over250 > 0 AFTER 2a.
- **A general CPU-budget policy framework**: not needed yet with only one adjustable
  engine; 1b's cap is a named constant, not a framework.
- **Multimedia-scheduler ("Pro Audio") registration**: only if 2a alone is not enough.
- **1c prewarm deferral**: parked with its written trigger (see 1c).

## Validation (after run 2; owner signs off; executor may pre-compute tallies)

The owner (Dan) dictates on the Windows machine with cleanup and window context enabled
until at least 30 filtered (prewarm_active=false), non-excluded timing lines exist; the
numbers are read from `%LOCALAPPDATA%\winpepper\logs\winpepper-YYYYMMDD.log` and every
criterion is evaluated once, on the FIRST 30 qualifying lines in log order. Pass
criteria (exact numbers):

- native_over250 = 0 on at least 28 of the 30 filtered lines. For the (at most 2)
  tolerated lines, list every exceedance with its over250_at offsets in the evidence
  file (reporting, not a pass criterion).
- native_max ≤ 250 ms on those same at-least-28 lines.
- asr_wait: 95th percentile under 500 ms.
- total=: 95th percentile at or under 5000 ms (the existing TotalBudgetMs — beyond
  this the app "felt slow" by definition).
- Context quality (both halves of the goal): compare the 30 post-fix lines' ctx_src
  distribution against the pre-fix ctx_src baseline (the first 20 qualifying lines
  collected in owner step 2). Pass if EACH of the uia share and the ocr share drops by
  fewer than 10 percentage points versus the baseline. At sign-off, report the raw
  per-value line counts (uia / ocr / none), baseline and post-fix, in the evidence file.
- Cleanup speed/quality (1b's gate): re-run `scripts/run-cleanup-bench-windows.sh`;
  median ≤ 1000 ms, evaluation outcomes unchanged. Report the before/after `cleanup=`
  medians from real dictations in the evidence file (reporting, not a pass criterion).
- Repo gates per AGENTS.md: Linux suite green on every commit; full Windows gate
  before any push.

## How this gets executed (numbered; the owner gate is a real stop)

1. RUN 1 (the-usual): 0b (ctx_src, over250_at, proc_cpu_ms) + 0c. Instrumentation only;
   no behavior change. Merge and install.
2. OWNER STEP (normal daily use, near-zero burden): (i) accumulate at least 20 filtered,
   non-excluded timing lines with window context ON — the FIRST 20 such lines in log
   order are the ctx_src baseline; (ii) then perform 0a (window context OFF until 20
   qualifying lines, record branch, turn it back ON).
3. GATE — the owner reviews 0a's branch and the baseline, and explicitly approves
   proceeding. The pipeline STOPS here and does not continue on its own.
4. RUN 2 (the-usual): 1a + 1b (+1d only if 0c triggered it). Merge, install, validate
   per the Validation section.
5. PHASE 2 only if its gate fires, as a separate follow-up run.

This plan file is committed at a pinned commit before run 1 and is read-only to the
executor; all findings go to the evidence file.

## What this plan deliberately does not solve

The 5–16 s stalls that occur with cleanup disabled (separate investigation — helped,
not hindered, by 0b's over250_at timestamps); memory/cache pressure effects if any
remain after Phase 1 (escalation path recorded above); protection against hypothetical
future engines (rejected as not needed yet).
