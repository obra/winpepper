# Mid-Paste Focus-Change Fallback Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** If the foreground window changes WHILE transcribed text is actively being typed into the target (mid-paste), immediately halt the remaining keystrokes and fall back to the existing click-to-paste (pending paste) behavior — and the pill click must then paste the WHOLE transcription, never just the un-sent remainder.

**Architecture:** Today `TextInjector.TryInject` hands the entire transcript to Windows in ONE `SendInput` call, so there is no in-process "mid-paste" moment to interrupt. We convert the send into a chunked loop: pure, Linux-testable primitives (`InjectionChunker`, `MidPasteDecider`, `GuardedInjectionRun`) drive the loop and check the foreground window handle (HWND) between chunks; `TextInjector` grows a `TryInjectGuarded` method that wires those primitives to real `SendInput`/`GetForegroundWindow` via constructor-injectable seams (same pattern as its existing `isKeyDown` seam). `PipelineHost` (all three paste call sites: hold arm, toggle arm, pill-click retry) maps an `Interrupted` outcome to the existing pending-paste flow with the FULL original text.

**Tech Stack:** C# / .NET 9, xUnit v3 + Shouldly (no mocking library — hand-built fakes/lambdas), Win32 `SendInput`/`GetForegroundWindow` P/Invoke, WinUI 3 (untouched — the existing pill PENDING state is reused as-is).

## Global Constraints

- Worktree: `/home/dan/code/winpepper/.worktrees/midpaste-focus-fallback`, branch `feat/midpaste-focus-fallback`. All paths below are relative to this worktree root.
- **NEVER use `dotnet test`** (AGENTS.md: VSTest host unreliable). Build `-c Release`, then run the xUnit v3 in-process runner via `dotnet exec <test dll>`.
- Linux gate before EVERY commit: `./scripts/linux-tests.sh` must print `LINUX SUITE: GREEN` and exit 0.
- Windows gate before push (Task 7): `./scripts/windows-gate.sh` must print `GATE: GREEN` and exit 0 (~20–40 min; run from WSL).
- Never mix Linux- and Windows-side builds in the same `bin/`/`obj/` (windows-gate.sh cleans automatically).
- For targeted (single-class) test runs, bootstrap the SDK exactly as `scripts/linux-tests.sh` does:
  ```bash
  export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
  export PATH="$DOTNET_ROOT:$PATH"
  ```
  If `dotnet` is still not found, open `scripts/linux-tests.sh` and copy its exact SDK bootstrap lines.
- Commit style: Conventional Commits, lowercase `type(scope):`, `--` for em-dash, body with numbered change list and a test-evidence line (e.g. "Linux test suite: N tests, 0 failures").
- Code style: file-scoped namespaces, Allman braces, 4-space indent, LF, `WarningsAsErrors=nullable`.
- Consumer policy (must be preserved): **no toast, no clipboard clobbering** — the pill IS the surface. A mid-paste interrupt is NOT an error (it is a user action): do not report it to the `ErrorBus`.
- Fail-open policy (must be preserved): when focus identity cannot be captured (probe returns 0 / capture fails / running on non-Windows), injection proceeds exactly as today. We never regress into holding because we merely failed to observe.
- `src/Winpepper.Platform` multi-targets `net9.0;net9.0-windows10.0.19041.0`; `WINDOWS` is defined only for the windows TFM. New pure code must compile and run on BOTH TFMs (no `#if WINDOWS`, no unguarded Win32 calls at runtime on Linux).
- Test naming: `Pascal_Snake_Case` describing behavior (e.g. `DifferentTarget_HoldsPending`). Class-level `[Trait("Platform", "Windows")]` marks Windows-only test classes — none of the new tests need it (all new tests are pure).
- README.md is the only end-user markdown doc; this plan under `docs/plans/` is a working/agent doc.

---

## File Structure

All new pure logic goes in `src/Winpepper.Platform/Injection/` (precedent: `ModifierGuard` — pure, Linux-unit-tested code living in the Platform injection folder, no `#if WINDOWS`). This avoids adding any new project references: `TextInjector` (same assembly) consumes the primitives directly, and `PipelineHost` (App, already references Platform) consumes the outcome enum.

| File | Action | Responsibility |
|---|---|---|
| `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs` | Create | Tri-state outcome of a guarded injection run (`Completed`, `Interrupted`, `SendFailed`) |
| `src/Winpepper.Platform/Injection/MidPasteDecider.cs` | Create | Pure HWND-vs-HWND continue/halt decision with fail-open semantics (+ `MidPasteDecision` enum) |
| `src/Winpepper.Platform/Injection/InjectionChunker.cs` | Create | Pure UTF-16 chunk splitter that never splits a surrogate pair |
| `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs` | Create | Pure loop driver: per-chunk focus check → send → outcome |
| `src/Winpepper.Platform/Injection/SendInputNative.cs` | Modify | Add `GetForegroundWindow` P/Invoke (file already has unguarded `user32.dll` imports) |
| `src/Winpepper.Platform/Injection/TextInjector.cs` | Modify | Extract modifier prelude + per-chunk send; add `foregroundHwnd`/`sendChunk` seams; add `TryInjectGuarded`; `TryInject` becomes a bool adapter |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | Map `Interrupted` → `EnterPendingPaste(full text)` at the hold arm (~line 686), toggle arm (~line 1055), and `TryPastePending` (~line 391) |
| `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs` | Create (Test) | Decider behavior incl. fail-open |
| `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs` | Create (Test) | Chunking incl. surrogate-pair safety |
| `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs` | Create (Test) | Loop driver: complete / interrupt / send-fail / prefix-only sends |
| `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs` | Create (Test) | `TryInjectGuarded` end-to-end through fakes (no real SendInput) |

Nothing in `Winpepper.Core` changes: the existing `SessionViewModel.EnterPendingPaste(string, InjectionTarget)`, `NotifyPasteAttempted(bool)`, `PendingPasteState`, `SessionStage.PendingPaste`, and the pill's PENDING visual/click handling are reused untouched (they already hold arbitrary text and keep the slot on a failed attempt — which is exactly the full-text retry semantics we need).

**Why HWND-only mid-paste checks (not the full `InjectionTarget` UIA identity):** the pre-paste decision uses UIA `FocusedElement.GetRuntimeId()`, a slow COM round-trip — far too heavy to run between every 32-code-unit chunk. `GetForegroundWindow()` is a cheap kernel call and matches the spec ("if the WINDOW focus changes"). Element-level focus moves within the same window do not halt the paste.

**Known trade-off (accepted):** a single `SendInput` batch is atomic relative to other injected input; a chunked send is not, so another process's synthetic input could theoretically interleave between chunks. This is inherent to making the paste interruptible; a chunk size of 32 code units keeps the windows tiny.

---

### Task 1: `MidPasteDecider` + `InjectionRunOutcome` (pure decision primitives)

**Files:**
- Create: `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs`
- Create: `src/Winpepper.Platform/Injection/MidPasteDecider.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs`

**Interfaces:**
- Consumes: nothing (leaf primitives).
- Produces:
  - `enum Winpepper.Platform.Injection.InjectionRunOutcome { Completed, Interrupted, SendFailed }` — used by Tasks 3, 5, 6.
  - `enum Winpepper.Platform.Injection.MidPasteDecision { Continue, Halt }` and `static MidPasteDecision MidPasteDecider.Decide(long hwndAtSendStart, long hwndNow)` — used by Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class MidPasteDeciderTests
{
    [Fact]
    public void SameHwnd_Continues()
    {
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 42)
            .ShouldBe(MidPasteDecision.Continue);
    }

    [Fact]
    public void DifferentHwnd_Halts()
    {
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Halt);
    }

    [Fact]
    public void UnknownBaseline_Continues_FailOpen()
    {
        // Could not capture the foreground window when the send started
        // (probe failed / non-Windows). Preserve today's behavior: keep typing.
        MidPasteDecider.Decide(hwndAtSendStart: 0, hwndNow: 99)
            .ShouldBe(MidPasteDecision.Continue);
    }

    [Fact]
    public void UnknownCurrent_Continues_FailOpen()
    {
        // Probe failed mid-run (or Windows reports no foreground window,
        // e.g. lock screen). We never halt on a failed observation.
        MidPasteDecider.Decide(hwndAtSendStart: 42, hwndNow: 0)
            .ShouldBe(MidPasteDecision.Continue);
    }
}
```

Match the existing test files' `using` style in `tests/Winpepper.Platform.Tests/Injection/` (they may rely on implicit usings for `Xunit`; if the project uses global usings, drop the redundant `using Xunit;`).

- [ ] **Step 2: Run tests to verify they fail to compile**

```bash
cd /home/dan/code/winpepper/.worktrees/midpaste-focus-fallback
export DOTNET_ROOT="${DOTNET_ROOT:-/home/dan/code/winpepper/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS0246: The type or namespace name 'MidPasteDecider' could not be found` (a compile failure IS the red step for a new type).

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/InjectionRunOutcome.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Outcome of a guarded (chunked, focus-checked) injection run.
/// Pure managed; no Win32 dependency.
/// </summary>
public enum InjectionRunOutcome
{
    /// <summary>Every chunk was sent; equivalent to the old TryInject == true.</summary>
    Completed,

    /// <summary>
    /// The foreground window changed mid-paste; remaining chunks were NOT sent.
    /// The caller must fall back to a pending paste holding the WHOLE original
    /// text (never just the remainder).
    /// </summary>
    Interrupted,

    /// <summary>SendInput refused a chunk; equivalent to the old TryInject == false.</summary>
    SendFailed,
}
```

Create `src/Winpepper.Platform/Injection/MidPasteDecider.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>Per-chunk continue/halt outcome while a paste is in flight.</summary>
public enum MidPasteDecision
{
    /// <summary>Same window still foreground, or identity unknown: keep typing.</summary>
    Continue,

    /// <summary>Foreground positively moved to a DIFFERENT window: stop typing.</summary>
    Halt,
}

/// <summary>
/// Pure mid-paste decision: is the window we started typing into still the
/// foreground window? Halt is chosen ONLY when we positively know the
/// foreground changed (both handles known and different). If either handle is
/// unknown (0) we default to Continue — same fail-open bias as
/// <c>PendingPasteDecider</c>: we never regress into holding when we simply
/// failed to observe. Compares raw HWNDs (not UIA element identity) because
/// this runs between every send chunk and must stay cheap.
/// </summary>
public static class MidPasteDecider
{
    public static MidPasteDecision Decide(long hwndAtSendStart, long hwndNow)
    {
        if (hwndAtSendStart == 0 || hwndNow == 0) return MidPasteDecision.Continue;
        return hwndNow == hwndAtSendStart
            ? MidPasteDecision.Continue
            : MidPasteDecision.Halt;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.MidPasteDeciderTests"
```

Expected: 4 tests, `Failed: 0`, `Errors: 0`.

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

```bash
git add src/Winpepper.Platform/Injection/InjectionRunOutcome.cs src/Winpepper.Platform/Injection/MidPasteDecider.cs tests/Winpepper.Platform.Tests/Injection/MidPasteDeciderTests.cs
git commit -m "feat(inject): pure mid-paste focus decider and injection run outcome

1. MidPasteDecision/MidPasteDecider -- HWND-vs-HWND continue/halt with the
   same fail-open bias as PendingPasteDecider (unknown handle => Continue).
2. InjectionRunOutcome -- tri-state outcome (Completed/Interrupted/SendFailed)
   replacing the binary bool for guarded injection runs.

Linux test suite: GREEN (see ./scripts/linux-tests.sh)."
```

---

### Task 2: `InjectionChunker` (pure UTF-16 chunk splitter)

**Files:**
- Create: `src/Winpepper.Platform/Injection/InjectionChunker.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `static IReadOnlyList<string> InjectionChunker.Split(string text, int chunkSize)` in namespace `Winpepper.Platform.Injection` — used by Tasks 3 (tests build chunk lists with it) and 5 (`TextInjector`). `chunkSize` is a count of UTF-16 code units; a chunk may exceed it by exactly 1 when needed to avoid splitting a surrogate pair. Throws `ArgumentOutOfRangeException` for `chunkSize <= 0`. Empty/null-ish input yields an empty list.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class InjectionChunkerTests
{
    [Fact]
    public void Empty_Text_Yields_No_Chunks()
    {
        InjectionChunker.Split(string.Empty, 32).ShouldBeEmpty();
    }

    [Fact]
    public void Short_Text_Is_One_Chunk()
    {
        var chunks = InjectionChunker.Split("hello", 32);
        chunks.ShouldBe(new[] { "hello" });
    }

    [Fact]
    public void Long_Text_Splits_At_ChunkSize()
    {
        var text = new string('a', 70);
        var chunks = InjectionChunker.Split(text, 32);
        chunks.Count.ShouldBe(3);
        chunks[0].Length.ShouldBe(32);
        chunks[1].Length.ShouldBe(32);
        chunks[2].Length.ShouldBe(6);
    }

    [Fact]
    public void Chunks_Reassemble_To_Original()
    {
        var text = "The quick brown fox jumps over the lazy dog. \U0001F600 twice \U0001F600!";
        string.Concat(InjectionChunker.Split(text, 7)).ShouldBe(text);
    }

    [Fact]
    public void Surrogate_Pair_Never_Split_Across_Boundary()
    {
        // 3 BMP chars then an emoji (surrogate pair) straddling a chunkSize=4
        // boundary: the pair's high surrogate lands at index 3, so a naive
        // split at 4 would cut the pair in half.
        var text = "abc\U0001F600def";
        var chunks = InjectionChunker.Split(text, 4);
        foreach (var chunk in chunks)
        {
            char.IsHighSurrogate(chunk[^1]).ShouldBeFalse(
                $"chunk '{chunk}' ends with an unpaired high surrogate");
            char.IsLowSurrogate(chunk[0]).ShouldBeFalse(
                $"chunk '{chunk}' starts with an unpaired low surrogate");
        }
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void NonPositive_ChunkSize_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => InjectionChunker.Split("x", 0));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS0246 ... 'InjectionChunker'`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/InjectionChunker.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Splits injection text into chunks of at most <c>chunkSize</c> UTF-16 code
/// units for the guarded (interruptible) send loop, extending a chunk by one
/// code unit when needed so a surrogate pair is never split across a chunk
/// boundary (an interrupt between the halves would leave a mangled character
/// in the old window). Pure managed; no Win32 dependency.
/// </summary>
public static class InjectionChunker
{
    public static IReadOnlyList<string> Split(string text, int chunkSize)
    {
        if (chunkSize <= 0) throw new ArgumentOutOfRangeException(nameof(chunkSize));
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var chunks = new List<string>((text.Length / chunkSize) + 1);
        var i = 0;
        while (i < text.Length)
        {
            var len = Math.Min(chunkSize, text.Length - i);
            // Never end a chunk on the high half of a surrogate pair.
            if (char.IsHighSurrogate(text[i + len - 1]) && i + len < text.Length)
                len++;
            chunks.Add(text.Substring(i, len));
            i += len;
        }
        return chunks;
    }
}
```

If the build complains about missing `using System;` / `using System.Collections.Generic;`, the project does not enable implicit usings for this file's TFM — add the two `using` lines above the namespace.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.InjectionChunkerTests"
```

Expected: 6 tests, `Failed: 0`, `Errors: 0`.

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/InjectionChunker.cs tests/Winpepper.Platform.Tests/Injection/InjectionChunkerTests.cs
git commit -m "feat(inject): surrogate-safe UTF-16 chunk splitter for interruptible sends

Pure InjectionChunker.Split(text, chunkSize) -- never splits a surrogate
pair across a chunk boundary; chunks reassemble to the original text.

Linux test suite: GREEN (see ./scripts/linux-tests.sh)."
```

---

### Task 3: `GuardedInjectionRun` (pure interruptible send-loop driver)

**Files:**
- Create: `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`

**Interfaces:**
- Consumes: `MidPasteDecider.Decide(long, long)`, `MidPasteDecision`, `InjectionRunOutcome` (Task 1).
- Produces:
  ```csharp
  static InjectionRunOutcome GuardedInjectionRun.Execute(
      IReadOnlyList<string> chunks,
      long hwndAtSendStart,
      Func<long> currentForegroundHwnd,
      Func<string, bool> sendChunk)
  ```
  in namespace `Winpepper.Platform.Injection` — used by Task 5 (`TextInjector.TryInjectGuarded`). Semantics: the focus check runs BEFORE every chunk (including the first — the modifier-release wait can burn up to 1500 ms before the first keystroke, so focus may already have moved). `Halt` → `Interrupted` with remaining chunks unsent; `sendChunk` returning false → `SendFailed`; all chunks sent → `Completed`; empty chunk list → `Completed`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs`:

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class GuardedInjectionRunTests
{
    [Fact]
    public void StableFocus_SendsAllChunks_Completed()
    {
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Completed);
        sent.ShouldBe(new[] { "aa", "bb", "cc" });
    }

    [Fact]
    public void FocusChange_BeforeFirstChunk_Interrupts_WithNothingSent()
    {
        // Focus can move during the pre-send modifier-release wait (up to
        // 1500 ms) -- the guard must catch that before the FIRST chunk.
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 99,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBeEmpty();
    }

    [Fact]
    public void FocusChange_MidRun_Interrupts_AfterPrefixOnly()
    {
        var sent = new List<string>();
        var probes = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            // First probe (before chunk 1) sees the original window; every
            // later probe sees a different one.
            currentForegroundHwnd: () => ++probes == 1 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        sent.ShouldBe(new[] { "aa" });
    }

    [Fact]
    public void SendFailure_ReturnsSendFailed_AndStops()
    {
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb", "cc" },
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => 42,
            sendChunk: c => { sent.Add(c); return sent.Count < 2; });

        outcome.ShouldBe(InjectionRunOutcome.SendFailed);
        sent.ShouldBe(new[] { "aa", "bb" });
    }

    [Fact]
    public void EmptyChunks_Completed_WithoutProbing()
    {
        var outcome = GuardedInjectionRun.Execute(
            chunks: Array.Empty<string>(),
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => throw new InvalidOperationException("must not probe"),
            sendChunk: _ => throw new InvalidOperationException("must not send"));

        outcome.ShouldBe(InjectionRunOutcome.Completed);
    }

    [Fact]
    public void FailOpen_UnknownBaseline_SendsEverything()
    {
        // hwndAtSendStart == 0 => guard disabled; behaves exactly like the
        // old unguarded send even though the probe reports a different hwnd.
        var sent = new List<string>();
        var outcome = GuardedInjectionRun.Execute(
            chunks: new[] { "aa", "bb" },
            hwndAtSendStart: 0,
            currentForegroundHwnd: () => 99,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Completed);
        sent.Count.ShouldBe(2);
    }

    [Fact]
    public void Interrupted_Run_Sent_Text_Is_A_Strict_Prefix_Never_The_Whole()
    {
        // The user story: on interrupt the target got only a leading prefix.
        // The CALLER is then required to hold the WHOLE original text as the
        // pending paste (PipelineHost passes `final`, not the remainder) --
        // this test pins the "strict prefix" half of that contract.
        var text = new string('x', 100);
        var chunks = InjectionChunker.Split(text, 32);
        var sent = new List<string>();
        var probes = 0;
        var outcome = GuardedInjectionRun.Execute(
            chunks,
            hwndAtSendStart: 42,
            currentForegroundHwnd: () => ++probes <= 2 ? 42L : 99L,
            sendChunk: c => { sent.Add(c); return true; });

        outcome.ShouldBe(InjectionRunOutcome.Interrupted);
        var sentText = string.Concat(sent);
        sentText.Length.ShouldBeLessThan(text.Length);
        text.StartsWith(sentText, StringComparison.Ordinal).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS0246 ... 'GuardedInjectionRun'`.

- [ ] **Step 3: Write the implementation**

Create `src/Winpepper.Platform/Injection/GuardedInjectionRun.cs`:

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Pure driver for an interruptible, chunked injection send. Before EVERY
/// chunk (including the first -- the modifier-release wait can delay the
/// first keystroke by up to 1500 ms) it asks <see cref="MidPasteDecider"/>
/// whether the window we started typing into is still foreground; on Halt it
/// stops immediately and reports <see cref="InjectionRunOutcome.Interrupted"/>
/// so the caller can hold the WHOLE original text as a pending paste.
/// All Win32 access is behind the two delegates, so this loop is fully
/// unit-testable on Linux.
/// </summary>
public static class GuardedInjectionRun
{
    public static InjectionRunOutcome Execute(
        IReadOnlyList<string> chunks,
        long hwndAtSendStart,
        Func<long> currentForegroundHwnd,
        Func<string, bool> sendChunk)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(currentForegroundHwnd);
        ArgumentNullException.ThrowIfNull(sendChunk);

        foreach (var chunk in chunks)
        {
            if (MidPasteDecider.Decide(hwndAtSendStart, currentForegroundHwnd())
                == MidPasteDecision.Halt)
            {
                return InjectionRunOutcome.Interrupted;
            }
            if (!sendChunk(chunk))
                return InjectionRunOutcome.SendFailed;
        }
        return InjectionRunOutcome.Completed;
    }
}
```

As in Task 2: if the build complains about missing `System` / `System.Collections.Generic` types (`Func`, `IReadOnlyList`, `ArgumentNullException`), add the corresponding `using` lines above the namespace.

Note: when `hwndAtSendStart == 0` the decider always returns `Continue`, so the probe result is irrelevant — but the probe IS still invoked per chunk. That is fine (it is a cheap call in production); the `EmptyChunks` test only forbids probing when there is nothing to send.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows" -class "Winpepper.Platform.Tests.Injection.GuardedInjectionRunTests"
```

Expected: 7 tests, `Failed: 0`, `Errors: 0`.

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/GuardedInjectionRun.cs tests/Winpepper.Platform.Tests/Injection/GuardedInjectionRunTests.cs
git commit -m "feat(inject): pure interruptible send-loop driver (GuardedInjectionRun)

Per-chunk foreground check (before EVERY chunk, incl. the first) -> Halt =>
Interrupted with remaining chunks unsent; sendChunk false => SendFailed;
all sent => Completed. Fully Linux-unit-tested via delegates.

Linux test suite: GREEN (see ./scripts/linux-tests.sh)."
```

---

### Task 4: `TextInjector` seams + refactor (no behavior change)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/SendInputNative.cs` (add `GetForegroundWindow` import)
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs` (extract prelude/send, add ctor seams)

**Interfaces:**
- Consumes: existing `ModifierGuard`, `SendInputNative`, existing helpers `ToCodeUnits` / `BuildKeyDownUpInputs`.
- Produces (for Task 5):
  - `TextInjector` constructor: `TextInjector(ILogger<TextInjector> log, Func<int, bool>? isKeyDown = null, Func<long>? foregroundHwnd = null, Func<string, bool>? sendChunk = null)` — the two new optional params are test seams following the existing `isKeyDown` seam pattern; production defaults are `DefaultForegroundProbe` (Win32 `GetForegroundWindow`, 0 on non-Windows/failure) and `SendChunkViaSendInput` (the existing build-inputs + `SendInput` tail, per chunk).
  - Private `void NeutralizeHeldModifiers()` — the existing modifier-release prelude, extracted verbatim.
  - `public static partial IntPtr SendInputNative.GetForegroundWindow()` (internal class, same assembly).
- `public bool TryInject(string text)` keeps its exact current signature and behavior in this task.

This is a pure refactor + seam-introduction task: existing tests must stay green and no new observable behavior is added. (The new seams get their failing-test cycle in Task 5, where `TryInjectGuarded` uses them.)

- [ ] **Step 1: Add the P/Invoke**

In `src/Winpepper.Platform/Injection/SendInputNative.cs`, inside `internal static partial class SendInputNative`, directly below the existing `SendInput` import (currently ~lines 39–40):

```csharp
    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr GetForegroundWindow();
```

(The file already contains unguarded `user32.dll` `LibraryImport`s and compiles for both TFMs; nothing calls this on Linux at runtime.)

- [ ] **Step 2: Refactor `TextInjector`**

In `src/Winpepper.Platform/Injection/TextInjector.cs`:

1. Add fields and extend the constructor (currently `TextInjector(ILogger<TextInjector> log, Func<int, bool>? isKeyDown = null)` at ~line 15):

```csharp
    /// <summary>UTF-16 code units per guarded send chunk (Task: mid-paste focus fallback).</summary>
    internal const int ChunkCodeUnits = 32;

    private readonly Func<long> _foregroundHwnd;
    private readonly Func<string, bool> _sendChunk;

    public TextInjector(
        ILogger<TextInjector> log,
        Func<int, bool>? isKeyDown = null,
        Func<long>? foregroundHwnd = null,
        Func<string, bool>? sendChunk = null)
    {
        _log = log;
        _isKeyDown = isKeyDown ?? DefaultKeyProbe;
        _foregroundHwnd = foregroundHwnd ?? DefaultForegroundProbe;
        _sendChunk = sendChunk ?? SendChunkViaSendInput;
    }

    /// <summary>Foreground HWND as Int64; 0 when unknown (non-Windows, or the call fails).</summary>
    private static long DefaultForegroundProbe()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return SendInputNative.GetForegroundWindow().ToInt64(); }
        catch { return 0; }
    }
```

2. Extract the modifier-release prelude (the entire `if (!ModifierGuard.WaitForRelease(...)) { ... }` block currently at the top of `TryInject`, ~lines 28–43) into a private method, verbatim:

```csharp
    private void NeutralizeHeldModifiers()
    {
        // A physically-held modifier (e.g. Ctrl still down from the dictation
        // chord, or held while clicking the pending-paste pill) is applied by
        // the target app to every injected character -- turning the text into
        // control characters / accelerator shortcuts. Wait briefly for release;
        // if the user keeps holding, synthesize releases (KEYUP only -- never
        // re-press, so their eventual physical release is a harmless no-op).
        if (!ModifierGuard.WaitForRelease(() => ModifierGuard.AnyDown(_isKeyDown),
                ModifierWaitTimeoutMs, ModifierWaitPollMs, Thread.Sleep))
        {
            var held = ModifierGuard.HeldModifiers(_isKeyDown);
            _log.LogInformation(
                "Modifiers still held {Timeout}ms after injection was requested; neutralizing {Count} key(s) before typing",
                ModifierWaitTimeoutMs, held.Count);
            var releases = ModifierGuard.BuildKeyUpInputs(held);
            var released = SendInputNative.SendInput(
                (uint)releases.Length, releases, Marshal.SizeOf<SendInputNative.INPUT>());
            if (released != (uint)releases.Length)
                _log.LogWarning("Modifier neutralization partial send: requested {Req}, sent {Sent}",
                    releases.Length, released);
        }
    }
```

3. Extract the send tail (the `BuildKeyDownUpInputs` + `SendInput` + partial-send check currently at ~lines 45–52) into a per-chunk method, verbatim logic:

```csharp
    private bool SendChunkViaSendInput(string chunk)
    {
        var inputs = BuildKeyDownUpInputs(ToCodeUnits(chunk));
        var sent = SendInputNative.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<SendInputNative.INPUT>());
        if (sent != (uint)inputs.Length)
        {
            _log.LogWarning("SendInput partial send: requested {Req}, sent {Sent}, err 0x{Err:X}",
                inputs.Length, sent, Marshal.GetLastWin32Error());
            return false;
        }
        return true;
    }
```

4. Rewrite `TryInject` to use the extracted pieces WITHOUT changing behavior (still one single send of the whole text — chunking arrives in Task 5):

```csharp
    public bool TryInject(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        NeutralizeHeldModifiers();
        return _sendChunk(text);
    }
```

- [ ] **Step 3: Verify existing tests still pass (refactor gate)**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: `Failed: 0`, `Errors: 0` (all pre-existing injection tests unchanged and green).

- [ ] **Step 4: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/SendInputNative.cs src/Winpepper.Platform/Injection/TextInjector.cs
git commit -m "refactor(inject): extract modifier prelude + per-chunk send; add fg-hwnd and send seams

1. SendInputNative gains GetForegroundWindow (LibraryImport, unguarded like
   its siblings; never invoked on Linux at runtime).
2. TextInjector: NeutralizeHeldModifiers() and SendChunkViaSendInput()
   extracted verbatim; ctor gains optional foregroundHwnd/sendChunk seams
   (same pattern as the existing isKeyDown seam). No behavior change.

Linux test suite: GREEN (see ./scripts/linux-tests.sh)."
```

---

### Task 5: `TextInjector.TryInjectGuarded` (interruptible paste)

**Files:**
- Modify: `src/Winpepper.Platform/Injection/TextInjector.cs`
- Test: `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`

**Interfaces:**
- Consumes: `InjectionChunker.Split` (Task 2), `GuardedInjectionRun.Execute` (Task 3), `InjectionRunOutcome` (Task 1), seams from Task 4.
- Produces (for Task 6):
  - `public InjectionRunOutcome TryInjectGuarded(string text)` — the ONLY paste entry point `PipelineHost` will use after Task 6. Baselines the foreground HWND at METHOD ENTRY (before the up-to-1500 ms modifier wait, so a focus change during the wait is caught before the first chunk), then runs the guarded chunk loop. Empty text → `Completed`.
  - `public bool TryInject(string text)` becomes a thin adapter: `TryInjectGuarded(text) == InjectionRunOutcome.Completed` (kept so any existing caller/test keeps compiling; on Windows its observable typing behavior is unchanged when focus is stable).

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs`.
Look at the existing test files in `tests/Winpepper.Platform.Tests/Injection/` for how they obtain an `ILogger<TextInjector>` (expected: `NullLogger<TextInjector>.Instance` from `Microsoft.Extensions.Logging.Abstractions`); use the same mechanism. The code below assumes `NullLogger`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class TextInjectorGuardedTests
{
    private static TextInjector NewInjector(
        Func<long> foregroundHwnd,
        Func<string, bool> sendChunk)
        => new(
            NullLogger<TextInjector>.Instance,
            isKeyDown: _ => false,          // no held modifiers => no sleep
            foregroundHwnd: foregroundHwnd,
            sendChunk: sendChunk);

    [Fact]
    public void Guarded_StableFocus_SendsWholeText_InChunks()
    {
        var sent = new List<string>();
        var injector = NewInjector(() => 42, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
        sent.Count.ShouldBe(3); // ChunkCodeUnits = 32 => 32 + 32 + 16
    }

    [Fact]
    public void Guarded_FocusChange_MidSend_Interrupts_AndStopsSending()
    {
        var sent = new List<string>();
        var probes = 0;
        // Probe call 1 = entry baseline (42). Call 2 = check before chunk 1
        // (42 -> sends). Call 3 = check before chunk 2 (99 -> halts).
        var injector = NewInjector(
            () => ++probes <= 2 ? 42L : 99L,
            c => { sent.Add(c); return true; });
        var text = new string('a', 96); // 3 chunks of 32

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Interrupted);

        sent.Count.ShouldBe(1);
    }

    [Fact]
    public void Guarded_FocusChange_DuringModifierWait_SendsNothing()
    {
        // Baseline is taken at method ENTRY; if focus moves before the first
        // chunk check (e.g. during the modifier-release wait), nothing sends.
        var sent = new List<string>();
        var probes = 0;
        var injector = NewInjector(
            () => ++probes == 1 ? 42L : 99L,
            c => { sent.Add(c); return true; });

        injector.TryInjectGuarded("hello world").ShouldBe(InjectionRunOutcome.Interrupted);

        sent.ShouldBeEmpty();
    }

    [Fact]
    public void Guarded_SendRefused_ReturnsSendFailed()
    {
        var injector = NewInjector(() => 42, _ => false);

        injector.TryInjectGuarded("hello").ShouldBe(InjectionRunOutcome.SendFailed);
    }

    [Fact]
    public void Guarded_EmptyText_Completes_WithoutSending()
    {
        var injector = NewInjector(
            () => throw new InvalidOperationException("must not probe"),
            _ => throw new InvalidOperationException("must not send"));

        injector.TryInjectGuarded(string.Empty).ShouldBe(InjectionRunOutcome.Completed);
    }

    [Fact]
    public void Guarded_UnknownBaseline_FailOpen_SendsEverything()
    {
        // Probe returns 0 (non-Windows / GetForegroundWindow failed): the
        // guard is disabled and the paste behaves exactly like today.
        var sent = new List<string>();
        var injector = NewInjector(() => 0, c => { sent.Add(c); return true; });
        var text = new string('a', 80);

        injector.TryInjectGuarded(text).ShouldBe(InjectionRunOutcome.Completed);

        string.Concat(sent).ShouldBe(text);
    }

    [Fact]
    public void TryInject_Adapter_True_OnCompleted_False_OnInterrupted()
    {
        var stable = NewInjector(() => 42, _ => true);
        stable.TryInject("hi").ShouldBeTrue();

        var probes = 0;
        var moving = NewInjector(() => ++probes == 1 ? 42L : 99L, _ => true);
        moving.TryInject("hi").ShouldBeFalse();
    }
}
```

If `Microsoft.Extensions.Logging.Abstractions` is not already referenced by `tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj`, add `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` to its `<ItemGroup>` (version comes from central package management in `Directory.Packages.props`; add it there too if absent — it will already be present, since `Winpepper.Platform` itself uses `ILogger<T>`).

- [ ] **Step 2: Run tests to verify they fail to compile**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: BUILD FAILS with `CS1061: 'TextInjector' does not contain a definition for 'TryInjectGuarded'`.

- [ ] **Step 3: Write the implementation**

In `src/Winpepper.Platform/Injection/TextInjector.cs`, replace the Task-4 version of `TryInject` and add `TryInjectGuarded`:

```csharp
    /// <summary>
    /// Interruptible paste: types the text in chunks of
    /// <see cref="ChunkCodeUnits"/> UTF-16 code units, checking before every
    /// chunk that the window that was foreground when this method was entered
    /// is STILL foreground. If focus moves mid-paste the remaining chunks are
    /// not sent and <see cref="InjectionRunOutcome.Interrupted"/> is returned
    /// so the caller can hold the WHOLE original text as a pending paste.
    /// The baseline is captured at method entry -- BEFORE the modifier-release
    /// wait (up to 1500 ms) -- so a focus change during that wait is caught
    /// before the first keystroke. Fail-open: if the foreground window cannot
    /// be determined (probe returns 0), the guard is disabled and the paste
    /// proceeds exactly as it did before this feature.
    /// </summary>
    public InjectionRunOutcome TryInjectGuarded(string text)
    {
        if (string.IsNullOrEmpty(text)) return InjectionRunOutcome.Completed;

        var hwndAtSendStart = _foregroundHwnd();
        NeutralizeHeldModifiers();
        var chunks = InjectionChunker.Split(text, ChunkCodeUnits);
        var outcome = GuardedInjectionRun.Execute(chunks, hwndAtSendStart, _foregroundHwnd, _sendChunk);
        if (outcome == InjectionRunOutcome.Interrupted)
            _log.LogInformation("Injection interrupted: foreground window changed mid-paste");
        return outcome;
    }

    public bool TryInject(string text)
        => TryInjectGuarded(text) == InjectionRunOutcome.Completed;
```

- [ ] **Step 4: Run tests to verify they pass (new class + all pre-existing injection tests)**

```bash
dotnet build tests/Winpepper.Platform.Tests/Winpepper.Platform.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Platform.Tests/bin/Release/net9.0/Winpepper.Platform.Tests.dll -notrait "Platform=Windows"
```

Expected: `Failed: 0`, `Errors: 0` (7 new tests plus all pre-existing ones).

- [ ] **Step 5: Run the full Linux suite and commit**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`.

```bash
git add src/Winpepper.Platform/Injection/TextInjector.cs tests/Winpepper.Platform.Tests/Injection/TextInjectorGuardedTests.cs
git commit -m "feat(inject): interruptible TryInjectGuarded -- halt typing when foreground changes mid-paste

1. TryInjectGuarded chunks the text (32 code units, surrogate-safe) and
   checks the foreground HWND before every chunk; Interrupted means the
   remainder was never sent.
2. Baseline captured at method entry, before the modifier wait, so a focus
   change during the wait sends nothing.
3. TryInject becomes a bool adapter over the guarded run (fail-open keeps
   stable-focus behavior identical).

Linux test suite: GREEN (see ./scripts/linux-tests.sh)."
```

---

### Task 6: `PipelineHost` wiring — fall back to click-to-paste with the FULL text

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (three sites: hold arm ~lines 686–725, toggle arm ~lines 1055–1092, `TryPastePending` ~lines 382–410)

**Interfaces:**
- Consumes: `TextInjector.TryInjectGuarded(string) -> InjectionRunOutcome` (Task 5); existing `SessionViewModel.EnterPendingPaste(string text, InjectionTarget target)` and `bool NotifyPasteAttempted(bool injected)`; existing field `_targetAtStart`; existing `Winpepper.Core.InjectionText.ForPaste(string)`.
- Produces: end-user behavior — mid-paste focus change halts typing, the pill shows "Click to paste", and a click pastes the WHOLE transcription. No new public API.

Notes that MUST hold in this task:
- On interrupt, pass **`final` / `final2` (the full transcription)** to `EnterPendingPaste`, exactly as the existing hold branch does — NEVER a remainder substring. That is the entire "whole transcription" requirement.
- An interrupt is a user action, not an error: do NOT call `_errorBus.Report` for `Interrupted` (the existing `ErrorReport_WhilePending_KeepsPendingClickable` semantics remain reserved for real failures).
- `injected` stays `false` on interrupt, so the existing `PostPasteGate.ShouldWatch(..., injected, ...)` correctly skips the learning watcher, `_engine.Apply(SessionEvent.InjectionCompleted)` still runs (VM stage stays `PendingPaste` — already pinned by the existing Core test `EngineIdle_WhilePending_KeepsPendingStage`), and history archiving stays unconditional.
- In `TryPastePending`, on interrupt the pending slot is simply KEPT (`NotifyPasteAttempted(false)`) — the slot still holds the full original text, so the next click re-pastes ALL of it (pinned by the existing Core test `NotifyPasteAttempted_Failure_KeepsPending`).

`PipelineHost` is `#if WINDOWS` and no test project references `Winpepper.App` (repo convention: this glue layer is verified by the Windows gate build + the manual Windows smoke checklist, exactly like the original pending-paste feature). So this task has no new automated test; its compile is proven by the Windows gate in Task 7 and its behavior by the smoke checklist there.

- [ ] **Step 1: Update the hold arm (~line 686)**

Find (exact current code inside `HandleHotkey`'s hold-up arm):

```csharp
                    else
                    {
                        var toType = Winpepper.Core.InjectionText.ForPaste(final);
                        injected = _injector.TryInject(toType);
                        if (!injected)
                        {
```

Replace the `else` block's opening with:

```csharp
                    else
                    {
                        var toType = Winpepper.Core.InjectionText.ForPaste(final);
                        var outcome = _injector.TryInjectGuarded(toType);
                        injected = outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
                        if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
                        {
                            // Focus moved to another window while the keystrokes
                            // were still going out: stop typing and hold the WHOLE
                            // transcription as a pending paste (never just the
                            // remainder -- a torn partial paste in the old window
                            // means the user re-pastes ALL of it where they want it).
                            // Not an error: no ErrorBus report, no toast, no
                            // clipboard clobbering -- the pill is the surface.
                            _vm.EnterPendingPaste(final, _targetAtStart);
                            _log.LogInformation(
                                "Injection interrupted by focus change; held full text as pending paste ({Chars} chars)",
                                final.Length);
                        }
                        else if (!injected)
                        {
```

Leave the body of the existing `if (!injected)` branch (the `_errorBus.Report` + `EnterPendingPaste` + log for "SendInput refused") completely unchanged — it just becomes `else if (!injected)`.

- [ ] **Step 2: Update the toggle arm (~line 1055)**

Apply the identical edit to the toggle arm's copy (locals are suffixed `2`). Find:

```csharp
                    else
                    {
                        var toType2 = Winpepper.Core.InjectionText.ForPaste(final2);
                        injected2 = _injector.TryInject(toType2);
                        if (!injected2)
                        {
```

Replace with:

```csharp
                    else
                    {
                        var toType2 = Winpepper.Core.InjectionText.ForPaste(final2);
                        var outcome2 = _injector.TryInjectGuarded(toType2);
                        injected2 = outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
                        if (outcome2 == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
                        {
                            // Focus moved to another window while the keystrokes
                            // were still going out: stop typing and hold the WHOLE
                            // transcription as a pending paste (never just the
                            // remainder -- a torn partial paste in the old window
                            // means the user re-pastes ALL of it where they want it).
                            // Not an error: no ErrorBus report, no toast, no
                            // clipboard clobbering -- the pill is the surface.
                            _vm.EnterPendingPaste(final2, _targetAtStart);
                            _log.LogInformation(
                                "Injection interrupted by focus change; held full text as pending paste ({Chars} chars)",
                                final2.Length);
                        }
                        else if (!injected2)
                        {
```

Again leave the existing failure branch body untouched (it becomes `else if (!injected2)`). If the local names in the toggle arm differ slightly from these (verify in the file — they are `final2`/`injected2`/`toType2` per the current source), match whatever the file actually uses.

- [ ] **Step 3: Update `TryPastePending` (~line 391)**

Replace the current method body:

```csharp
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = Winpepper.Core.InjectionText.ForPaste(_vm.PendingPasteText);
        var injected = !string.IsNullOrWhiteSpace(text) && _injector.TryInject(text);
        if (!injected)
        {
            // Slot is kept below; the pill stays clickable for a retry.
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; pending slot kept for retry"),
                _currentSessionId);
        }
        if (injected)
            _log.LogInformation("Pending paste injected");
        else
            _log.LogWarning("Pending paste injection failed");

        return _vm.NotifyPasteAttempted(injected);
    }
```

with:

```csharp
    public bool TryPastePending()
    {
        if (!_vm.HasPendingPaste) return false;
        var text = Winpepper.Core.InjectionText.ForPaste(_vm.PendingPasteText);
        var outcome = string.IsNullOrWhiteSpace(text)
            ? Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed
            : _injector.TryInjectGuarded(text);
        var injected = outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Completed;
        if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.SendFailed)
        {
            // Slot is kept below; the pill stays clickable for a retry.
            _errorBus.Report(
                Winpepper.Core.Errors.ErrorStage.Injection,
                new InvalidOperationException("SendInput refused; pending slot kept for retry"),
                _currentSessionId);
        }
        if (injected)
            _log.LogInformation("Pending paste injected");
        else if (outcome == Winpepper.Platform.Injection.InjectionRunOutcome.Interrupted)
            // Focus moved mid-paste during the pill-click retry too: the slot
            // still holds the FULL original text, so the next click re-pastes
            // all of it. Not an error -- no ErrorBus report.
            _log.LogInformation(
                "Pending paste interrupted by focus change; slot kept with full text for another click");
        else
            _log.LogWarning("Pending paste injection failed");

        return _vm.NotifyPasteAttempted(injected);
    }
```

Also update the method's XML doc summary sentence "on failure the pending slot is kept" to: `on failure OR a mid-paste focus change the pending slot is kept (full text) so the user simply clicks again`.

- [ ] **Step 4: Run the full Linux suite (regression only — App does not compile on Linux)**

```bash
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`. (This cannot compile `PipelineHost.cs`; the Windows gate in Task 7 is the compile + suite proof for this file. If Task 7's gate reports a compile error in `PipelineHost.cs`, fix it and commit the fix as `fix(app): ...`.)

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): halt paste on mid-paste focus change; hold FULL text as click-to-paste

1. Hold and toggle inject arms use TryInjectGuarded; Interrupted =>
   EnterPendingPaste(final, ...) with the WHOLE transcription (never the
   remainder), no ErrorBus report (user action, not an error).
2. TryPastePending: an interrupted pill-click retry keeps the slot (full
   text) via NotifyPasteAttempted(false) so the next click re-pastes all
   of it; SendFailed keeps today's error-report path.
3. injected stays false on interrupt, so the post-paste learning watcher
   is skipped and archiving/state transitions are unchanged.

Linux test suite: GREEN (PipelineHost itself is WINDOWS-only; compile and
suite proof via ./scripts/windows-gate.sh before push)."
```

---

### Task 7: Full verification — Linux suite, Windows gate, smoke checklist

**Files:**
- No source changes expected (fixes only if the gate fails).

**Interfaces:**
- Consumes: everything above.
- Produces: push-ready branch (`GATE: GREEN`) + the manual smoke checklist below (the repo's standard verification for Win32 glue, same as the original pending-paste feature).

- [ ] **Step 1: Run the full Linux suite**

```bash
cd /home/dan/code/winpepper/.worktrees/midpaste-focus-fallback
./scripts/linux-tests.sh
```

Expected: `LINUX SUITE: GREEN`, exit 0.

- [ ] **Step 2: Run the Windows gate (from WSL; ~20–40 min)**

```bash
./scripts/windows-gate.sh
```

Expected: `GATE: GREEN`, exit 0. This builds `Winpepper.App` (proving the Task 6 `PipelineHost` edits compile) and runs all test projects on all TFMs with no trait filter. Known pre-existing quirk: `Hook_Installs_And_DisposesCleanly` can hang headless — if the gate fails on something demonstrably unrelated to this feature, re-run once before investigating.

- [ ] **Step 3: If the gate found compile/test errors, fix and re-gate**

Fix any errors in the files this plan touched, commit as `fix(inject): ...` or `fix(app): ...` (Linux suite green before each commit), and re-run `./scripts/windows-gate.sh` until `GATE: GREEN`.

- [ ] **Step 4: Windows smoke test checklist (manual, on a real Windows session)**

These are the production proofs of the user story. They cannot be automated in this repo (no test project references `Winpepper.App`; real `SendInput`/foreground behavior needs a live desktop). Record results in the task notes/PR description.

1. Dictate a LONG utterance (several sentences) into Notepad; do not touch anything. Expect: full text lands in Notepad, pill returns to idle (guard is invisible when focus is stable).
2. Dictate a long utterance into Notepad and, the instant text starts appearing, Alt+Tab to another window. Expect: typing stops almost immediately (within ~one chunk), pill shows "Click to paste".
3. Focus a fresh empty editor window and click the pill. Expect: the ENTIRE transcription appears — including the part that had already been typed into Notepad before the halt (the whole text, not the remainder).
4. Repeat (2), then click the pill and Alt+Tab away again while the click-paste is typing. Expect: typing halts again, pill stays "Click to paste"; clicking once more into a stable window pastes the ENTIRE transcription.
5. Regression — pre-existing hold behavior: dictate, and switch windows BEFORE the transcription completes (not mid-paste). Expect: nothing is typed anywhere; pill shows "Click to paste"; click pastes the full text (unchanged behavior).
6. Regression — modifier guard: hold Ctrl while a dictation finishes; release after ~1 s. Expect: text types normally after release (no control-character garbage).
7. Regression — next dictation discards a pending slot: leave a pending paste unpasted, start a new dictation. Expect: old pending text is discarded; new flow proceeds normally.

- [ ] **Step 5: Confirm branch is push-ready**

```bash
git log --oneline main..HEAD
git status --short
```

Expected: the commits from Tasks 1–6 (plus any gate fixes), clean working tree, `LINUX SUITE: GREEN` and `GATE: GREEN` both achieved after the final commit.

---

## Self-Review (performed at plan-writing time)

**1. Spec coverage:**
- "If focus changes WHILE text is actively being pasted, immediately halt" → Tasks 1–5 create the interruptible send (per-chunk HWND checks); Task 6 wires it into all three production paste sites. The only physical way to halt a `SendInput`-based paste is to chunk it — covered.
- "Fall back to the same click-to-paste behavior" → Task 6 reuses the exact existing pending-paste flow (`EnterPendingPaste`, pill PENDING state, `TryPastePending`); no parallel mechanism invented.
- "Click-to-paste must paste the WHOLE transcription, not just the remainder" → Task 6 passes `final`/`final2` (full text) to `EnterPendingPaste`; the pill-retry interrupt path keeps the untouched full-text slot (`NotifyPasteAttempted(false)`). Pinned at the pure level by `Interrupted_Run_Sent_Text_Is_A_Strict_Prefix_Never_The_Whole` (Task 3) plus existing Core tests (`EnterPendingPaste_HoldsTextAndShowsPendingStage`, `NotifyPasteAttempted_Failure_KeepsPending`), and at the production level by smoke items 3–4.
- "All tests green before committing (Linux subset); full Windows suite via ./scripts/windows-gate.sh before pushing" → every task's Step 5 runs `linux-tests.sh`; Task 7 runs the gate.
- No unresolved coverage gaps.

**1b. No silent deferrals:** the fakes in Tasks 3/5 (`foregroundHwnd`, `sendChunk` lambdas) are test doubles for Win32 calls whose production implementations are delivered in the same plan (Task 4 `DefaultForegroundProbe`/`SendChunkViaSendInput`, real `GetForegroundWindow`/`SendInput`). The production outcome is proven by Task 7's Windows gate (compile + full suite) and smoke items 1–4 — the repo's established verification method for `#if WINDOWS` glue (identical to how the original pending-paste feature was verified). Nothing is moved to "future work".

**2. Placeholder scan:** every code step contains complete code; every run step has an exact command and expected output. The two adaptive instructions (logger acquisition in Task 5 Step 1, toggle-arm local names in Task 6 Step 2) each state the concrete expected value AND where to verify it — no TBDs.

**3. Type consistency:** `InjectionRunOutcome { Completed, Interrupted, SendFailed }`, `MidPasteDecision { Continue, Halt }`, `MidPasteDecider.Decide(long, long)`, `InjectionChunker.Split(string, int)`, `GuardedInjectionRun.Execute(IReadOnlyList<string>, long, Func<long>, Func<string,bool>)`, `TextInjector.TryInjectGuarded(string) -> InjectionRunOutcome`, `TryInject(string) -> bool`, `ChunkCodeUnits = 32` — used with these exact names/signatures in every task that references them (verified line-by-line). Existing consumed APIs (`EnterPendingPaste(string, InjectionTarget)`, `NotifyPasteAttempted(bool)`, `InjectionText.ForPaste(string)`, `_targetAtStart`) match the current source verbatim.
