# Winpepper Design — Native Windows 11 Local Dictation

**Status:** Approved for implementation
**Date:** 2026-05-15
**Companion to:** `pepper-x` (Linux/GNOME) — same problem, pure-native rewrite for Windows

## 1. Goal

Hold a key combo, speak, release — cleaned-up words appear in the focused Windows app. Everything runs locally. Match the feature surface of pepper-x plus the Ghost Pepper parity items, but built entirely on Windows-native tools.

Non-goals: cross-platform abstractions, Linux compatibility shims, cloud transcription, anything that needs a Microsoft Store identity.

## 2. Stack

| Concern                | Choice                                                              |
| ---------------------- | ------------------------------------------------------------------- |
| Language / runtime     | C# / .NET 9                                                          |
| UI                     | WinUI 3 (WinAppSDK), single packaged process                         |
| ASR                    | Parakeet TDT v3 (0.6B) via `Microsoft.ML.OnnxRuntime.DirectML`       |
| Cleanup LLM            | Qwen 2.5/3 (0.5B–2B GGUF, Q4_K_M) via `LlamaSharp` w/ Vulkan backend |
| Audio capture          | NAudio WASAPI                                                        |
| Global hotkey          | `SetWindowsHookEx(WH_KEYBOARD_LL)`                                   |
| Text injection         | `SendInput` with `KEYEVENTF_UNICODE`                                 |
| Window context         | UIA tree text → `Windows.Media.Ocr` fallback                         |
| Tray                   | `H.NotifyIcon` (Shell_NotifyIcon wrapper)                            |
| OCR                    | `Windows.Media.Ocr` (OS built-in, no Tesseract dep)                  |
| Logging                | `Microsoft.Extensions.Logging` + Serilog file sink                   |
| Packaging              | WiX v5 MSI                                                            |
| Minimum OS             | Windows 11 22H2, x64                                                 |

ARM64 is out of scope for v1.

## 3. Solution layout

```
src/
  Winpepper.App/              # WinUI 3 packaged app (App.xaml, MainWindow, tray host)
  Winpepper.Core/             # Orchestration: session state machine, pipeline glue, settings
  Winpepper.Asr/              # ONNX Runtime DirectML, Parakeet v3 streaming + batch
  Winpepper.Audio/            # NAudio WASAPI capture, level monitor, device enum
  Winpepper.Cleanup/          # LlamaSharp Vulkan, prompt assembly, output sanitize
  Winpepper.Corrections/      # CorrectionStore (preferred/replacements), learning
  Winpepper.History/          # Archived recordings + transcripts + experiments
  Winpepper.Models/           # Model registry, downloader (HuggingFace), checksums
  Winpepper.Platform/         # P/Invoke: WH_KEYBOARD_LL, SendInput, UIA, OCR, tray,
                              #          PrintWindow, foreground window, autostart key
  Winpepper.Ipc/              # In-process events (Channel<T>) and contracts
tests/
  Winpepper.Core.Tests/
  Winpepper.Asr.Tests/
  Winpepper.Cleanup.Tests/
  Winpepper.Corrections.Tests/
  Winpepper.History.Tests/
  Winpepper.Platform.Tests/
  Winpepper.IntegrationTests/
packaging/
  winpepper.wxs               # WiX MSI definition
  sign.ps1                    # Code signing wrapper (off by default)
docs/
  superpowers/specs/2026-05-15-winpepper-design.md
  manual-test.md
```

One executable: `winpepper.exe`. No services, no helper processes in v1. The app starts on user logon (autostart registry key, set by the MSI), runs hidden to tray, materializes its main window on tray click.

## 4. Process and threading model

The whole product runs in a single user-session process. WinUI 3 packaged identity. No Session 0 / service boundary (services can't `SendInput` into user sessions, which would break the entire product).

Threads and their owners:

- **UI thread** (DispatcherQueue) — anything XAML touches.
- **Hook thread** — a dedicated STA thread that owns the `WH_KEYBOARD_LL` hook and pumps a message loop. It is the only thread allowed to call `SendInput` (Windows requires hook callbacks to run on the thread that installed the hook). It posts hotkey events onto `Channel<HotkeyEvent>`.
- **Audio thread** — the WASAPI `IAudioCaptureClient` callback thread. Pushes 20 ms PCM frames into `Channel<AudioFrame>`.
- **ASR worker** — a `Task` that consumes audio frames, runs the streaming Parakeet session synchronously, emits partial-transcript events.
- **Cleanup worker** — a `Task` that consumes a "transcript finalized" event, runs LlamaSharp synchronously, emits cleaned text.
- **OCR/UIA prefetch worker** — a `Task` started at `HotkeyDown`, raced against transcription.

Inter-stage communication is exclusively through bounded `Channel<T>` (capacity 16) so backpressure surfaces rather than allocating unboundedly.

Orchestration is a small state machine in `Winpepper.Core.SessionEngine`:

```
Idle → Recording → Transcribing → CleaningUp → Injecting → Idle
                                                  │
                                                  └──→ Failed → Idle
```

A session-scoped `CancellationTokenSource` is observed by every stage. Cancel hotkey trips it.

Concurrency invariants:

- One active recording at a time. A second `HotkeyDown` while recording is ignored (logged).
- Final injection (`SendInput`) happens only on the hook thread.
- All file writes (settings, corrections, history) go through `Winpepper.Core.AtomicFile` (write temp → fsync → rename).

## 5. Hotkey, capture, ASR, cleanup, injection

### 5.1 Hotkey

`Winpepper.Platform.HotkeyHook` installs `WH_KEYBOARD_LL` on the hook thread.

Default bindings (user-configurable):

- **Hold to record**: Right Ctrl + Right Shift.
- **Toggle to record**: Ctrl + Shift + Space (tap).
- **Cancel while held**: Esc.

The hook distinguishes left/right modifiers, suppresses the chord from reaching the foreground app while the binding matches, and emits `HotkeyDown` / `HotkeyUp` events.

### 5.2 Audio capture

`Winpepper.Audio.Recorder` opens `WasapiCapture` against the user-selected input device in shared mode, then converts to 16 kHz mono float32 via `MediaFoundationResampler` if the device's native format differs.

On `HotkeyDown`:

1. Open the capture client and start recording.
2. Play start sound (`SoundPlayer` with bundled `start.wav`, ~150 ms).
3. Spin up the `StreamingTranscriber`.
4. Start OCR/UIA prefetch (if window context enabled).
5. Append raw samples to an in-memory buffer for archival, **and** push 560 ms windows (8960 samples — matches pepper-x) into the ASR channel.

### 5.3 Parakeet TDT v3 streaming

`Winpepper.Asr.ParakeetSession` wraps a `Microsoft.ML.OnnxRuntime` `InferenceSession` with the DirectML EP:

- Loads `encoder.onnx`, `decoder_joint.onnx`, and `tokenizer.model` from the model directory.
- Session options: `AppendExecutionProvider_DML(deviceId: 0)`, `GraphOptimizationLevel.ORT_ENABLE_ALL`, `EnableMemoryPattern = false` (DirectML requirement).
- `FeedChunk(ReadOnlySpan<float> samples)` runs the encoder on a 560 ms window, runs the TDT/RNN-T decode loop, returns the running transcript.
- `Flush()` zero-pads any leftover and emits the final transcript.
- Streaming state — decoder hidden states, last emitted token, blank counter — is held in fields and reset per session.

Model layout under `%LOCALAPPDATA%\winpepper\models\parakeet-tdt-0.6b-v3\`:

```
encoder.onnx
decoder_joint.onnx
tokenizer.model
config.json
```

The decode loop is a C# port of pepper-x's `parakeet-rs` crate. Implementation detail (decoder graph shapes, blank-token handling, the TDT skip mechanic) is deferred to the implementation plan; the reference is the existing Rust crate.

### 5.4 Finalize

On `HotkeyUp`:

1. Stop WASAPI capture; play stop sound.
2. `transcriber.Flush()`. Streaming has done most of the work; the tail latency is the final window.
3. Persist the WAV to `%LOCALAPPDATA%\winpepper\history\YYYY-MM-DD\<uuid>.wav` (16 kHz mono int16, 30-day rolling retention).
4. Hand transcript + window-context task handle + correction store to the cleanup worker.

### 5.5 Cleanup

`Winpepper.Cleanup.CleanupRunner`:

- `LLamaContext` constructed once at app start and pre-warmed with a tiny prompt so the KV cache is hot before the user's first dictation.
- Builds the prompt per §6.
- Greedy + `temperature = 0.1`, 15 s timeout via cancellation token, `max_new_tokens = min(2048, ceil(transcript_chars * 2.0))`.
- Strips `<think>…</think>` blocks and orphan opening `<think>` tags from the output.
- If sanitized output is empty or `"..."`, falls back to the deterministic correction-only path: apply correction-store replacements to the raw transcript and return.

Model layout under `%LOCALAPPDATA%\winpepper\models\cleanup\<name>\`.

### 5.6 Injection

The cleanup worker passes the final string back to the hook thread via channel. The hook thread calls `SendInput` with `KEYEVENTF_UNICODE` for each codepoint (surrogate pairs handled).

Chosen over clipboard paste because:

- No clipboard clobber.
- Works in any focused control that accepts keystrokes (terminals, editors, browsers, Office).
- Semantically closest to pepper-x's uinput virtual keyboard.

If injection fails (e.g., foreground window is a secure prompt that blocks synthetic input), the cleaned text is copied to the clipboard and a toast says so.

We log the foreground window title at both `HotkeyDown` and just before injection. The History detail surfaces it when they differ.

## 6. Window context and cleanup prompt

### 6.1 Window context prefetch

Starts at `HotkeyDown`, runs in parallel with capture/transcription, gated by the `WindowContextEnabled` setting (default off — Ghost Pepper parity). `Winpepper.Platform.WindowContext`:

**UIA path (preferred):**

1. `GetForegroundWindow()` → wrap in `AutomationElement.FromHandle`.
2. Walk via `TreeWalker.ContentViewWalker`.
3. For each element, extract text in order of preference: `TextPattern.DocumentRange.GetText(8000)`, `ValuePattern.Value`, `LegacyIAccessiblePattern.Value`, `Name`.
4. Deduplicate. Preserve reading order using each element's `BoundingRectangle` (top-to-bottom, left-to-right).
5. Truncate to 4000 chars.
6. If total recovered text is < 80 chars or null, treat as "UIA failed" and fall through.

**OCR fallback:**

1. `PrintWindow` the foreground window's client rect into a `SoftwareBitmap`.
2. `OcrEngine.TryCreateFromUserProfileLanguages()` → `RecognizeAsync(bitmap)`.
3. Sort `OcrLine`s top-to-bottom; within each line, sort words left-to-right.
4. Compute average confidence (logged). Truncate to 4000 chars.

**Lifecycle:** `Task<WindowContextResult>` with states `Idle → Running → Resolved`. Cancellable. Cleanup awaits up to 500 ms then proceeds without window context rather than blocking longer.

### 6.2 Prompt assembly

`Winpepper.Cleanup.PromptBuilder` produces the four-block structure (matches Ghost Pepper exactly):

```
<BASE-PROMPT>
{user custom prompt OR built-in default}
</BASE-PROMPT>

<CORRECTION-HINTS>
Preferred transcriptions:
- ChatGPT
- Anthropic
Misheard replacements:
- chat gbt -> ChatGPT
- ann thropic -> Anthropic
</CORRECTION-HINTS>

<OCR-RULES>
The WINDOW-OCR-CONTENT below is the text currently visible on the user's screen.
Use it only to disambiguate names, commands, file paths, and jargon.
Prefer the user's spoken words; never substitute OCR text wholesale.
</OCR-RULES>

<WINDOW-OCR-CONTENT>
{up to 4000 chars from UIA or OCR}
</WINDOW-OCR-CONTENT>

<USER-INPUT>
{raw streaming transcript from Parakeet}
</USER-INPUT>
```

Blocks join with `\n\n`.

`<CORRECTION-HINTS>` is omitted entirely when both correction lists are empty. `<OCR-RULES>` and `<WINDOW-OCR-CONTENT>` are omitted when window context is disabled or the prefetch returned empty.

### 6.3 Built-in default base prompt

Covers:

- Filler removal (`um`, `uh`, `like`, `you know`, `basically`, `literally`, `sort of`, `kind of`).
- Self-correction commands (`scratch that`, `never mind`, `no let me start over` → delete preceding content).
- Recognition-error fixes for names / commands / files / jargon when context is clear.
- Sentence-level punctuation.
- Honoring explicit punctuation and spelling commands.
- Reproducing the entire transcript — never summarize, never delete sentences.
- Output must read as professionally written by a human.
- Three input/output examples: filler removal, self-correction, mishearing fix.

### 6.4 Profiles

Ships with:

- **Ordinary Dictation** — the default base prompt above.
- **Literal Dictation** — filler removal off, minimal rewriting, punctuation only.
- **Custom** — user-editable in Settings → Cleanup.

### 6.5 Deterministic post-pass

After the LLM returns (or after the fallback path), `CorrectionStore.Replacements` is applied to the text as a final case-preserving substitution pass. This guarantees a high-confidence user correction always wins, regardless of model behavior.

## 7. UI

### 7.1 Tray

`H.NotifyIcon`. Icon states:

- **Ready** — monochrome mic glyph (theme-adaptive).
- **Recording** — red-tinted mic.
- **Loading / model warmup** — orange ellipsis.
- **Error** — yellow triangle; tooltip carries error summary.

Menu: dynamic status line ("Recording…", "Transcribing…", "Cleaning up…", "Ready") bound to the same observable as the status pill, Settings, Diagnostics, Pause dictation, Quit. Version string at the bottom. A progress bar appears under the status line while a model is downloading or warming.

### 7.2 Status pill

A frameless transparent `AppWindow` with `OverlappedPresenter.IsAlwaysOnTop = true`, no caption, click-through via `WS_EX_TRANSPARENT`. Anchored bottom-center of the screen containing the foreground window. Shows current stage and elapsed ms. Hides 600 ms after the pipeline returns to Idle.

### 7.3 Main window (`NavigationView`)

- **Recording.** Hold-to-record hotkey recorder, toggle-to-record hotkey recorder, mic picker with live level meter, sound-effect toggle, speaker-filter toggle (experimental — ports pepper-x's `speaker_filter.rs` approach), "Test dictation" button.
- **Cleanup.** Enable toggle, window-context toggle, prompt-profile picker, custom-prompt editor (`RichEditBox`, monospaced font), max-new-tokens slider, timeout slider.
- **Corrections.** Two editable lists — Preferred transcriptions and Misheard replacements (`wrong → right` pairs). Inline validation: no empty strings, no duplicates, no self-mappings, minimum length 2.
- **History.** Newest-first list of up to 50 archived recordings. Each row: timestamp, transcript preview, audio duration, copy button. Click → detail view.
- **History detail / Lab.**
  - Original transcript + original cleaned text side-by-side.
  - Rerun transcription panel: model picker → run → DiffPlex word-level diff vs original.
  - Rerun cleanup panel: model picker + prompt editor + window-context toggle → run → diff vs original.
  - "Show cleanup transcript" → modal with full assembled prompt + raw model output.
  - WAV playback via `MediaPlayerElement`.
  - Experiments are ephemeral. Entry data is never modified.
  - Selecting a model in a rerun panel can be promoted to "Use as default" with one click.
- **Models.** Two cards (ASR, Cleanup). Each shows current selection, available models, download status, "Download Missing Models" button. Downloads stream from HuggingFace via `Winpepper.Models.ModelDownloader` with SHA-256 verification and resumable HTTP range requests.
- **Diagnostics.** Live log tail (rolling last 2000 lines), "Open log folder", "Copy diagnostics bundle" (zips logs + system info + recent history metadata; **never** includes audio).

### 7.4 Onboarding

First-run flow on first window-show:

1. Pick mic (with level meter).
2. Record hotkeys (with conflict detection against common shortcuts).
3. Download models (progress bars; can skip and download later from Models tab).
4. "Try it" — guided test dictation into a sample text box.

### 7.5 Settings persistence

`%LOCALAPPDATA%\winpepper\settings.json`. Atomic write. Schema-versioned. Reactive — view models implement `INotifyPropertyChanged`; changes flow through `Winpepper.Core.SettingsStore`.

### 7.6 Sound effects

Bundled `start.wav` and `stop.wav` (short two-tone, ~150 ms each), played via `System.Media.SoundPlayer`. Gated by the sound-effects setting (default on). Cancellable.

### 7.7 Autostart

Stored at `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Winpepper` = `"%LOCALAPPDATA%\Programs\Winpepper\winpepper.exe" --tray`. `--tray` starts hidden. MSI sets this on first install only (via `[INSTALLFOLDER]`); toggling autostart in Settings writes/deletes the value directly thereafter.

## 8. Corrections and post-paste learning

### 8.1 Store

`Winpepper.Corrections.CorrectionStore` persists to `%LOCALAPPDATA%\winpepper\corrections.json`:

```json
{
  "schema": 1,
  "preferred": ["ChatGPT", "Anthropic"],
  "replacements": { "chat gbt": "ChatGPT", "ann thropic": "Anthropic" }
}
```

Atomic write. Used in two places:

- Feeds `<CORRECTION-HINTS>` in the cleanup prompt.
- Deterministic post-pass (§6.5).

### 8.2 Post-paste learning

`Winpepper.Core.PostPasteWatcher` mirrors pepper-x's learning flow with Windows-native plumbing:

1. After injection completes, record `(foregroundWindowHandle, focusedElementRuntimeId, injectedText, injectionEndTime)`.
2. Subscribe to UIA `TextEdit_TextChangedEvent` on the focused element (`Text_TextChangedEvent` fallback).
3. For up to 30 s, watch for the element's text to diverge from what we inserted at a single word position. Use a token-level Levenshtein on the diff window.
4. Apply pepper-x's learning constraints: minimum word length 3, edit distance ≤ 60 % of the word's length, no whitespace-only diffs, no diffs that look like punctuation drift, no diffs that match common autocomplete behaviors.
5. If accepted, show a non-modal toast bottom-of-screen: "Learn correction: `chat gbt` → `ChatGPT`? [Yes / Preferred / No]". 8 s timeout → No.
6. **Yes** writes a misheard replacement; **Preferred** writes the right side to preferred transcriptions; **No** suppresses the same pair for the session.
7. The store update is visible to the next cleanup pass immediately.

## 9. Errors, logging, observability

### 9.1 Error bus

`Winpepper.Core.ErrorBus`. All pipeline stages run inside a `try/catch` that funnels into the bus with a stage tag and the active session id.

Per-stage failure modes:

| Stage           | Failure                          | Behavior                                                                            |
| --------------- | -------------------------------- | ----------------------------------------------------------------------------------- |
| Audio           | `WasapiCapture` open fails       | Halt session, tray "Mic unavailable", deep-link to device picker.                   |
| ASR             | Model load fails                 | Tray Error state, deep-link to Models tab.                                          |
| ASR             | Decode fails mid-stream          | Keep recording, drop partial for that window, log, finalize with what we have.      |
| Cleanup         | Timeout / empty / "..." output   | Deterministic fallback. Session succeeds.                                           |
| OCR / UIA       | Any failure                      | Silent skip; cleanup runs without window context.                                   |
| Injection       | `SendInput` to non-accepting app | Clipboard fallback + toast.                                                         |

### 9.2 Cancellation

Every stage observes the session-scoped `CancellationToken`. Cancel hotkey (Esc-while-held) trips the CTS, all stages tear down, the WAV is deleted, and no stop sound plays.

### 9.3 Crash safety

`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException` are caught. We log the exception, write a minidump (`MiniDumpWriteDump`) under `%LOCALAPPDATA%\winpepper\crashes\`, attempt to reset the state machine to Idle, and keep the tray alive. The app exits only if reset fails.

### 9.4 Logging

`Microsoft.Extensions.Logging` + Serilog file sink at `%LOCALAPPDATA%\winpepper\logs\winpepper-YYYYMMDD.log`. Rolling daily, 14-day retention. Console sink only with `--debug-console`. Log level configurable (default Info; Debug toggleable from Diagnostics).

Structured fields: `session_id`, `stage`, `elapsed_ms`, `model_name`, `transcript_len`. Per-session timings (`record / transcribe / cleanup / insert / total`) are logged and surfaced in History detail.

### 9.5 Diagnostics bundle

"Copy diagnostics bundle" zips current and previous log file, GPU/CPU info, app version, settings (with secrets redacted — none in v1), and recent history metadata. **No audio** is ever included.

## 10. Testing strategy

### 10.1 Unit (`xUnit`)

- `Winpepper.Asr.Tests` — golden WAVs in `tests/fixtures/`; assert transcript matches reference within tolerance for known-good audio. Pure-C# tests for the streaming chunker boundaries and the TDT decode glue. Mock ORT for graph-shape tests.
- `Winpepper.Cleanup.Tests` — `PromptBuilder` snapshot tests covering every block combination (custom prompt × correction hints present/absent × OCR on/off). `<think>` sanitizer cases. Fallback paths. Timeout path with a fake `ILlamaContext`.
- `Winpepper.Corrections.Tests` — validation, learning constraints (Levenshtein bounds, min word length, whitespace/punctuation filters), JSON round-trip, atomic-write fault injection.
- `Winpepper.Platform.Tests` — pure-logic only: UIA tree-flattening order, OCR-line sort, `SendInput` codepoint translation (incl. surrogate pairs). Anything that needs live Win32 is in IntegrationTests.
- `Winpepper.Core.Tests` — state-machine transitions, error-bus routing, cancellation propagation, end-to-end pipeline with fake ASR / cleanup / injection adapters.
- `Winpepper.History.Tests` — persistence, pruning to 50, experiment ephemerality.

### 10.2 Integration (`Winpepper.IntegrationTests`)

Opt-in via `WINPEPPER_INTEGRATION=1`. Run on real Windows.

- Headless WASAPI capture against a loopback device.
- Real ORT-DirectML session against a small bundled test model so CI runs are fast.
- Real LlamaSharp against a ~70M-param test GGUF for smoke tests.
- `SendInput` round-trip via a hidden test window that captures `WM_CHAR`.
- UIA walk against a controlled test window.

### 10.3 Manual smoke

`docs/manual-test.md` lists what to exercise on each release: every tab, error toasts, cancel mid-session, autostart, MSI install / upgrade / uninstall, model download with simulated network drop.

### 10.4 CI

- Pre-merge: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`.
- Nightly on the Windows VM (`$WINPEPPER_VM_ROOT` (default `~/.local/share/winpepper/windows-vm/`), dockur container on `localhost:2222`): integration tests + MSI smoke install.

## 11. Packaging

WiX v5 (`Wix.Toolset.Sdk` MSBuild SDK). Single `packaging/winpepper.wxs`.

Components:

- App binaries → `%LOCALAPPDATA%\Programs\Winpepper\` (per-user; **no elevation / UAC required**). WiX `Package/@Scope="perUser"`; install tree under `LocalAppDataFolder\Programs\Winpepper` (the VS Code / Squirrel convention). Rationale: Winpepper is a single-user desktop app, and a per-machine install forced a UAC prompt on every dev-loop build-install — per-user removes that friction with no functional loss, since all user data already lives in `%LOCALAPPDATA%`.
- Empty model directory under `%LOCALAPPDATA%\winpepper\models\` created on first run, not by the MSI.
- Start menu shortcut.
- Programs and Features entry.
- Per-user autostart `Run` key (set on fresh install only; not overwritten on upgrade).

Upgrade rules: `MajorUpgrade.AllowDowngrades=no`, `Schedule=afterInstallInitialize`. A per-user `MajorUpgrade` only detects prior **per-user** installs of the same `UpgradeCode` — the intended end state. Migration: anyone with a pre-existing per-machine install (`C:\Program Files\Winpepper`) must uninstall it once (that removal needs elevation) before the per-user package will manage upgrades. Settings, corrections, history, and models survive upgrades because they live under `%LOCALAPPDATA%`.

Prereqs:

- Windows 11 22H2+ enforced via `LaunchCondition`.
- DirectX 12 capability: warn at install time if missing; app still runs CPU.
- WinAppSDK runtime: detected; if missing, the MSI invokes the WinAppSDK bootstrapper.

Code signing: `packaging/sign.ps1` accepts a thumbprint / PFX path for an EV cert. Off in dev / CI by default. About dialog shows "unsigned build" when the binary is unsigned.

Output: `winpepper-<version>-x64.msi`. Versioning via `Nerdbank.GitVersioning`: `MajorMinorPatch` from `version.json`, commit SHA stamped into `AssemblyInformationalVersion`.

## 12. Out of scope for v1

- ARM64 builds.
- Microsoft Store identity / Store distribution.
- Helper-process split (cleanup-helper, injection-helper). Single process for now.
- CUDA backend. DirectML is the only GPU path.
- Tesseract. We rely on `Windows.Media.Ocr` and UIA.
- Cross-language model packs beyond what Parakeet TDT v3 supports out of the box.
- Telemetry / phone-home of any kind.

## 13. Open implementation questions (resolve in plan)

- Parakeet TDT v3 ONNX export — exact shape of `encoder.onnx` outputs and `decoder_joint.onnx` inputs to confirm against the actual NVIDIA NeMo export.
- `LlamaSharp` Vulkan backend selector — confirm the right NuGet variant for Vulkan on DX12 GPUs and the runtime device-pick API.
- WinUI 3 `AppWindow` click-through configuration for the status pill (`SetExtendedWindowStyle` ordering).
- UIA `TextEdit_TextChangedEvent` availability across common target apps (Notepad, VS Code, Chrome, Word) — fallback path coverage.

These do not change the architecture; they're details the implementation plan needs to nail down.
