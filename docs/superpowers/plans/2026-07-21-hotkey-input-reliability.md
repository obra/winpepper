# Hotkey Input Reliability Implementation Plan

> **For the implementer:** Follow test-driven development. Work only in the assigned worktree, commit the finished track, and do not push.

**Goal:** Reliably capture modifier/dedicated keys and add tap-Space/hold-Space recording without disrupting normal typing.

**Architecture:** Centralize key vocabulary, expose an exclusive raw-capture lease from the low-level hook, and implement dual-role Space as a testable state machine used by the hook.

---

### Task 1: Centralize the supported key vocabulary

**Files:** `src/Winpepper.Platform/Hotkeys/HotkeyChord.cs`, recorder/UI hotkey files, platform/core tests.

1. Add failing parser/formatter/validator tests for F13-F24, Application aliases, modifier-only Alt+Win, and Copilot's canonical sequence.
2. Introduce a shared virtual-key catalog used by parser, formatter, validation, and recorder display.
3. Permit bare triggers only for F1-F24, Application, and Copilot. Keep ordinary bare keys rejected.
4. Use original/raw virtual-key data in the UI path where available.
5. Run affected tests and commit the vocabulary slice.

### Task 2: Fix trigger release semantics

**Files:** low-level hook/state-machine files and their tests.

1. Add a failing test showing a non-modifier hold key emits HoldDown on press and HoldUp on its own release.
2. Fix release matching without regressing modifier-only holds, toggle, cancellation, or swallow/pass-through behavior.
3. Add repeat-key regression coverage and commit.

### Task 3: Add focus-independent raw capture

**Files:** low-level hook capture API, `src/Winpepper.App/Controls/HotkeyRecorderBox.xaml*` or equivalent, view/page wiring, tests.

1. Add failing tests for an exclusive capture lease, raw down/up delivery while triggers are suspended, and cleanup.
2. Implement the lease and route capture events before normal trigger handling.
3. Make the recorder complete Alt+Win and dedicated-key chords even if WinUI loses focus. Focus loss alone must not cancel an active raw capture.
4. Ensure navigation/disposal cancels the lease and restores normal processing.
5. Run affected tests and commit the capture slice.

### Task 4: Implement dual-role long-press Space

**Files:** testable Space state machine, hook integration, settings/UI binding, tests.

1. Add failing deterministic tests for: short tap replay; 300 ms hold start; release stop; repeat suppression; injected-event pass-through; cancellation/reconfiguration; and no replay after a long press.
2. Implement the state machine with an injectable clock/timer and a private `SendInput` marker for replay.
3. Integrate it only when Hold is configured as the explicit long-press Space policy. Do not allow Space as a normal bare trigger.
4. Surface `Space` in the recorder/settings as the dual-role option with concise explanatory text.
5. Run all affected tests, then a Windows Release build. Commit final track changes and report commits plus exact commands/results.
