# Onboarding and Hotkey Reliability Design

**Date:** 2026-07-21

## Goal

Make first-run dictation impossible to test without a ready speech model, make interrupted model downloads recover automatically, and make hotkey recording reliable for modifier-only and dedicated keys. Add a dual-role Space option: a tap still types Space, while a press held for 300 ms records until release.

## Model provisioning

Model readiness is a verified state, not merely the existence of a directory or the return of a download method. A shared provisioning coordinator owns the ASR model lifecycle for onboarding and the Models page. Its observable states are `Missing`, `Downloading`, `Verifying`, `Retrying`, `Ready`, and `Failed`; state includes progress and a user-facing error when applicable.

The coordinator verifies every required ASR file against its descriptor. It downloads missing or invalid files with these rules:

- Preserve `.partial` files across transient failures and explicit retries.
- Validate resumed responses. A nonzero range request must receive `206 Partial Content` with a compatible `Content-Range`; a server that ignores ranges restarts safely from byte zero rather than appending a full body.
- Treat an interval with no received bytes as a stalled attempt. Use a 30-second idle timeout, independent of total download duration.
- Retry transient transport/stall failures three times with short bounded backoff (1 second, then 2 seconds), always resuming from the validated partial length.
- Verify size and SHA-256 before atomically promoting a partial file.
- Leave resumable partial data on transport failure or cancellation; discard it only when it is structurally impossible or fails final verification.

## Onboarding gate

The onboarding page uses the production provisioning coordinator; the no-op downloader is removed. Pressing Download waits for the required ASR model to reach `Ready`. Failure keeps the user on Download Models with progress/error and a Retry action. Cleanup models remain optional.

The Test Dictation step is reachable only while the ASR model is verified and the dictation pipeline can start. Skipping the model download must not lead to Test Dictation; if a skip remains available, it finishes onboarding in a limited state with a clear Models-page call to action. The preferred initial UI is to omit model skipping because usable dictation is the product's core first-run outcome.

Readiness is rechecked immediately before a test begins so files removed or corrupted after the download cannot produce a model-less recording prompt.

## Hotkey capture and key vocabulary

Hotkey recording uses the existing low-level keyboard hook through an explicit capture lease. While capture is active, the hook forwards raw key transitions to the recorder even if WinUI loses focus (for example when Alt+Win activates shell behavior). Ending capture restores normal trigger processing. Capture cleanup is idempotent and occurs on completion, cancellation, navigation, and disposal.

A shared key-name catalog is used by parsing, formatting, validation, and the recorder. It supports:

- modifier-only chords, including `Alt+Win`;
- `F1` through `F24`;
- the context-menu key, canonicalized as `Application` with `Menu`, `Apps`, and `ContextMenu` accepted as aliases;
- a best-effort `Copilot` alias represented by the common Windows hardware sequence `LeftShift+LeftWin+F23`.

The recorder uses raw/original virtual keys and ignores auto-repeat. Bare global triggers remain deliberately limited to dedicated keys: `F1`-`F24`, `Application`, and `Copilot`. Ordinary letters, digits, navigation keys, and Space are still rejected as normal bare bindings because swallowing them globally would break typing.

## Long-press Space

Long-press Space is a separate dual-role hold policy, not a normal bare hotkey. When enabled for the hold action:

1. The first physical Space-down is buffered and swallowed.
2. Releasing before 300 ms replays exactly one synthetic Space down/up pair to the foreground application.
3. Reaching 300 ms starts hold-to-record without replaying Space.
4. The physical Space-up after a long press stops recording and remains swallowed.

Synthetic replay events carry a private injection marker so the hook passes them through and never re-enters the state machine. Typematic repeats are swallowed while the original press is pending or recording. Cancellation, reconfiguration, suspension, and disposal clear pending timers and either replay a still-short tap or end an active recording deterministically.

The hook must also emit `HoldUp` when any non-modifier hold trigger itself is released; matching the released key as though it were still down is incorrect.

## Verification

Automated tests cover resume validation, idle timeout/retry, readiness gating, parser/formatter round trips, capture across focus loss, non-modifier hold release, and the short/long Space state machine including injected replay and repeat suppression. Windows Release build and the repository's CI-equivalent test suite must pass before launch. The final build is launched from the Windows filesystem and its process path is verified.
