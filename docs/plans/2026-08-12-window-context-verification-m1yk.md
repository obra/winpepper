# 2026-08-12 — Window-context → cleanup-LLM verification (m1yk)

## Question

Does window context actually reach the cleanup LLM on the streaming path?
(Current state: user's settings have cleanupEnabled=false,
cleanupWindowContextEnabled=false; cleanup model configured = sotto raw-io.)

## Code map (merged main; installed build 0.7.0.338)

Capture and delivery chain, both hotkey arms:

1. **Launch at listen-start** — `PipelineHost.LaunchPrefetchAtListenStart`,
   `src/Winpepper.App/Hosting/PipelineHost.cs:485-497`. Fires once per
   dictation regardless of hold/toggle mode. Policy:
   `WindowContextListenStartPolicy.ShouldStart` (Cleanup), which requires an
   hwnd at start and delegates to `WindowContextPrefetchGate.ShouldPrefetch`.
2. **Hand-off at stop** — `_ctxSequencer.RecordingStopped()` at
   PipelineHost.cs:732 (hold) and :1350 (toggle). Same object feeds both arms.
3. **Delivery to cleanup** — hold arm PipelineHost.cs:946-961, toggle arm
   :1559-1578: `ctxTextTask` is passed as `windowContextTask` to
   `CleanupRunner.RunAsync`. Identical in both arms by reading.
4. **Bounded wait** — `CleanupRunner.RunAsync`,
   `src/Winpepper.Cleanup/CleanupRunner.cs:80-110`: waits at most
   `options.WindowContextWait` (default 500 ms) and only when
   `WindowContextEnabled` and a task was supplied.
5. **Prompt assembly** — context text enters the SYSTEM prompt at
   CleanupRunner.cs:116 (`PromptBuilder.BuildSystem`, marker
   `<WINDOW-OCR-CONTENT>`, PromptBuilder.cs:68-73). The backend then formats
   per prompt-format (`CleanupPromptFormatter`): chatml/granite keep the
   system prompt; **raw-io sends only the transcript and discards the system
   prompt** (formatter contract + PromptFormatCapabilities.cs docs).
6. **Streaming vs batch**: the streaming/batch choice is made at
   transcription (PipelineHost.cs:899-903); everything below that line — the
   whole context chain above — is shared. There is no separate streaming
   window-context path to bypass. The m1yk-comment timing worry (streaming
   finish ~166 ms vs 500 ms wait) is moot on current main: the prefetch
   launches at LISTEN-START since tbc0, so the stop-time wait is ≈0.

## Model-level demonstration (real runner, real models, GPU, 2026-08-12)

Harness (throwaway; not committed): real `LlamaCleanupBackend` +
`CleanupRunner.RunAsync`, seed 42, `WindowContextEnabled=true`, each probe run
twice (context vs null).

**qwen2.5-0.5b-instruct (chatml — carries system prompt):**

| raw transcript | WITH context | WITHOUT |
|---|---|---|
| open the winpepper settings page and toggle cleanup | Open the **winpepper** settings page and toggle cleanup. | Open the **WinPepper** settings page and toggle cleanup. |
| the nemotron bench run was green end to end | The nemotron bench **run was green** end to end. | The nemotron bench **ran** green end to end. |
| tell mykhail the build is green when he wakes up | (FallbackEmpty — raw kept: "mykhail" un-anglicized) | Tell **Mikhail** the build is green... |
| i pushed the fix to the gzcc branch already | I pushed the fix to the gzcc branch. | I pushed the fix to the gzcc branch already. |
| control: this sentence contains no unusual names or words | identical | identical |

Context measurably changes output (product-token spelling preserved with
context vs. mangled without); the control case is byte-identical, so
context does not interfere where it is irrelevant. marker+ true / marker-
false on all cases.

**sotto-cleanup-lfm25-350m (raw-io — structurally discards system prompt):**
all 5 cases byte-identical WITH vs WITHOUT (including the diagnostic
"WinPepper" mangling occurring in BOTH). The model provably never receives
the context — by design of the raw-io format.

## Findings

1. **The chain is complete and reaches output for carrying formats** (chatml:
   qwen; granite: future models). Acceptance's "injection point" documented
   above; model-level influence demonstrated.
2. **With the user's configured sotto model the context can never matter** —
   the raw-io formatter discards it. The gate is designed to skip the prefetch
   in this case, but y301 (unassigned `_activeCleanupPromptFormat` → null)
   defeats the skip (null is treated as carrying), so the prefetch runs and
   its result is thrown away. Wasteful, not corrupting. Comment added to y301.
3. **New bug filed: 5xp6** — `windowContextUsed` (PipelineHost.cs:978-979 /
   1592-1593) checks the diagnostic `AssembledPrompt`, which contains the
   marker whenever context was CONSUMED, even for raw-io where the model
   never receives it. History would claim windowContextUsed=true falsely for
   sotto dictations.

## Remaining for a full close

A live in-app trace: enable cleanup + window context, set cleanup model to
qwen (chatml), dictate one sentence in a window containing an unusual name,
confirm history shows windowContextUsed=true and the timing line's
ctx_src/ctx_wait. Requires a human at the keyboard + mic, so it is left as
the one open checkbox on m1yk.
