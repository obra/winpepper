# Punctuation-Run Guards + Always-Archive Pending Dictations + Pending Diagnostics — Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Neutralize a degenerate "lots of periods in a row" dictation at
its two root causes (ASR greedy-decode stuck-frame spray + punctuation-blind
cleanup guards), archive **every** completed dictation to history (owner
decision — including held-then-discarded pending pastes), and add
content-free diagnostics around the pending-paste flow.

**Architecture:** Four independent, additive changes across three seams.
Two are pure-managed and unit-tested on Linux (Cleanup punctuation collapse;
ASR same-token cap logic extracted to testable static helpers). Two edit the
Windows-only `Winpepper.App` pipeline glue (`PipelineHost`) — which is
**excluded from the Linux build** — so they are verified by code review plus
the Windows smoke checklist the spec mandates.

**Tech Stack:** .NET 9, C#, xUnit v3 (in-process runner via `dotnet exec`),
Shouldly assertions, WinUI 3 (App layer only, not built on Linux).

## Global Constraints

- **.NET SDK version:** `9.0.100`, provisioned into the gitignored
  `./.dotnet/` inside the worktree. Never commit `./.dotnet/` or build output.
- **Test runner:** VSTest (`dotnet test`) **crashes on this machine**. Run
  pure-managed tests via the xUnit v3 in-process runner:
  `dotnet exec <TestAssembly>.dll`. Exclude Windows-only tests with
  `-notrait "Platform=Windows"`. Target one test with
  `-method "<Namespace>.<Class>.<Method>"`.
- **Test TFM:** build/run the `net9.0` target only (never
  `net9.0-windows10.0.19041.0`). Always pass `-p:EnableWindowsTargeting=true`
  so multi-targeted project references restore on Linux.
- **Every test step re-exports the SDK env** (a fresh implementer shell does
  not inherit an earlier task's exports):
  `export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"`
- **Baseline:** the full non-Windows suite is green at **739 tests / 0
  failed** on `main` (`8dfe046`). Re-verify all line numbers before editing —
  a large upstream merge just landed; anchor on code text, not raw numbers.
- **Do NOT touch** `packaging/` or the keyboard hook internals
  (`src/Winpepper.Platform/Hotkeys/`).
- **Logs contain no dictation content** — counts and fixed outcome strings only.
- **The pending slot stays memory-only** (`PendingPasteState`) — never written
  to disk. CHANGE 1 archives at *dictation completion* via the existing
  `HistoryArchiver`; it does **not** persist the in-memory pending slot.
- **`Winpepper.App` (incl. `PipelineHost.cs`) does not compile on Linux**
  (`Directory.Build.props` sets `BuildProjectReferences=false` +
  `SKIP_WINUI_LINUX` for that project off-Windows). Edits to it are verified
  by review + Windows smoke, NOT by a Linux build. Keep such edits mechanical
  and show exact before/after.

---

## Coverage note (read before Self-Review)

Two of the four changes (CHANGE 1 always-archive, CHANGE 4 diagnostics) live
entirely in `PipelineHost.cs`, which is **structurally excluded from the Linux
build**. There is no Linux unit test that exercises `PipelineHost` today, and
adding one would require refactoring the WinUI-coupled pipeline — out of scope
per the spec ("do NOT restructure"; "do NOT touch packaging/ or the keyboard
hook internals"). The spec itself designates the **Windows Smoke Checklist**
(Task 6) as the verification for these two changes. This is a spec-designated
verification path, **not** a silent deferral: every CHANGE 1 / CHANGE 4
requirement maps to a concrete, named Windows smoke item in Task 6. The two
root-cause fixes that *can* be unit-tested on Linux (CHANGE 2, CHANGE 3) are
covered by real xUnit tests in Tasks 2 and 3.

---

## File Structure

| File | Change | Responsibility |
|------|--------|----------------|
| `src/Winpepper.Cleanup/CleanupRunner.cs` | Modify | Add `CollapsePunctuationRuns` (internal static) + call it from `ApplyDeterministicPostPass` so it runs on every path. |
| `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` | Modify | Unit tests for the collapse helper + one end-to-end fallback-path test. |
| `src/Winpepper.Asr/ParakeetSession.cs` | Modify | Add same-token run cap: two internal static seam helpers + wire them into `GreedyDecode`. |
| `src/Winpepper.Asr/Winpepper.Asr.csproj` | Modify | Add `InternalsVisibleTo Winpepper.Asr.Tests` (does not exist yet). |
| `tests/Winpepper.Asr.Tests/GreedyDecodeCapTests.cs` | Create | Unit tests for the cap seam helpers + a stuck-frame simulation. |
| `src/Winpepper.App/Hosting/PipelineHost.cs` | Modify | CHANGE 1: remove the two `heldPending` archive gates (archive unconditionally at completion). CHANGE 4: add 5 content-free log lines. |
| `docs/plans/2026-07-21-pending-paste.md` | Modify | Add a dated addendum recording the owner's always-archive decision (supersedes the no-archive-on-hold design note). |

---

## Task 0: Provision the .NET SDK in the worktree

**Files:**
- Create: `./.dotnet/` (gitignored — NOT committed)

**Interfaces:**
- Consumes: nothing.
- Produces: a working `dotnet` at `./.dotnet/dotnet`, reached via
  `DOTNET_ROOT`/`PATH`. Every later task's test steps re-export these.

- [ ] **Step 1: Reuse the main repo's provisioned SDK if present, else install**

The main checkout (`/home/dan/code/winpepper/.dotnet`) may already have a
provisioned SDK from the merge verification. The worktree runs from a
different directory, so `$PWD/.dotnet` must exist *here*. Prefer a symlink
(no network); fall back to a fresh install.

```bash
cd /home/dan/code/winpepper/.worktrees/punctuation-run-archive-fixes
if [ -x ../../.dotnet/dotnet ]; then
    ln -sfn ../../.dotnet .dotnet
else
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
    chmod +x /tmp/dotnet-install.sh
    /tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
fi
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

- [ ] **Step 2: Verify the SDK runs**

Run: `dotnet --version`
Expected: `9.0.100`

- [ ] **Step 3: Confirm `.dotnet` is gitignored (nothing to commit)**

Run: `git status --porcelain .dotnet`
Expected: **no output** (the repo's `.gitignore` contains `/.dotnet/`).

There is nothing to commit for this task.

---

## Task 1: Punctuation-run collapse helper in Cleanup (CHANGE 2)

Root-cause fix A. The plausibility guards in `CleanupRunner` are
punctuation-blind (echo markers, the `raw*2+64` length cap, and content-word
similarity all ignore punctuation), so a degenerate `0.5B`-model punctuation
spray passes every guard. `ApplyDeterministicPostPass` runs on **every** path
(LLM success at `CleanupRunner.cs:171`, and the `Finalize` fallback/bypass
path at `CleanupRunner.cs:260`), so collapsing runs there neutralizes the
spray everywhere — including raw-ASR passthrough.

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` (add `using`, add
  `CollapsePunctuationRuns`, call it from `ApplyDeterministicPostPass` ~line 247-250)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`

**Interfaces:**
- Consumes: `CleanupRunner.ApplyDeterministicPostPass(string, CorrectionsData)`
  (private), `CaseAwareReplacer.Apply(string, ...)`.
- Produces: `internal static string CleanupRunner.CollapsePunctuationRuns(string text)`
  — visible to `Winpepper.Cleanup.Tests` via the existing
  `InternalsVisibleTo Include="Winpepper.Cleanup.Tests"` in the csproj.

- [ ] **Step 1: Write the failing unit tests**

Append to `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`, inside the
`CleanupRunnerTests` class (before the closing brace):

```csharp
    [Theory]
    [InlineData(". . . .", ".")]        // space-separated run of 4 -> one
    [InlineData(". . .", ".")]          // space-separated run of 3 -> one
    [InlineData("! ! !", "!")]          // works for other marks
    [InlineData("? ? ? ?", "?")]
    [InlineData(".....", ".")]          // contiguous run of 5 -> one
    [InlineData("!!!!", "!")]           // contiguous run of 4 -> one
    public void CollapsePunctuationRuns_DegenerateRuns_CollapseToSingleMark(
        string input, string expected)
    {
        CleanupRunner.CollapsePunctuationRuns(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("...")]                     // genuine 3-dot ellipsis survives
    [InlineData("Wait... really?")]         // ellipsis mid-sentence survives
    [InlineData("e.g. one. two. three.")]   // ordinary sentence punctuation
    [InlineData(". .")]                       // two marks: below the 3+ threshold
    [InlineData("Hello world.")]             // nothing to collapse
    [InlineData("!!!")]                       // contiguous run of exactly 3 survives
    public void CollapsePunctuationRuns_OrdinaryText_Unchanged(string input)
    {
        CleanupRunner.CollapsePunctuationRuns(input).ShouldBe(input);
    }

    [Fact]
    public async Task Run_PunctuationSprayInRawTranscript_CollapsedOnFallbackPath()
    {
        // Model output is implausibly long garbage -> FallbackImplausible path,
        // which finalizes from the RAW transcript through
        // ApplyDeterministicPostPass. The raw transcript carries a degenerate
        // punctuation spray no plausibility guard catches; the deterministic
        // collapse must neutralize it even on the raw-passthrough path.
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = new string('x', 500) });
        var result = await runner.RunAsync(
            "please review the document . . . . .",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("please review the document .");
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: **build FAILS** — `'CleanupRunner' does not contain a definition for
'CollapsePunctuationRuns'`. (A compile failure is the RED state here since the
method does not exist yet.)

- [ ] **Step 3: Add the `using` for regex**

At the top of `src/Winpepper.Cleanup/CleanupRunner.cs`, add this line with the
other `using` directives (regex is NOT in the implicit-usings set):

```csharp
using System.Text.RegularExpressions;
```

- [ ] **Step 4: Implement `CollapsePunctuationRuns` and call it from the post-pass**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, replace the existing
`ApplyDeterministicPostPass` method (currently ~lines 247-250):

```csharp
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        return CaseAwareReplacer.Apply(text, corrections.Replacements);
    }
```

with:

```csharp
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        var corrected = CaseAwareReplacer.Apply(text, corrections.Replacements);
        return CollapsePunctuationRuns(corrected);
    }

    // Collapse degenerate punctuation runs produced by a mis-firing ASR or a
    // 0.5B cleanup model (e.g. a stuck decoder spraying ". . . . ." or
    // ".........."). Runs on EVERY cleanup path via ApplyDeterministicPostPass
    // (LLM success, fallback, and raw-ASR bypass) because the plausibility
    // guards are punctuation-blind and let a punctuation spray through.
    //
    // Rules (marks: '.', '!', '?'):
    //   - Contiguous run of 4+ identical marks ("....." / "!!!!") -> one mark.
    //     A genuine 3-dot ellipsis "..." is a run of exactly 3 and is preserved
    //     (the pattern requires 4+).
    //   - Space-separated run of 3+ identical marks (". . ." / "! ! !") -> one
    //     mark. Two marks (". .") are below threshold and left untouched.
    // Ordinary text ("Wait... really?", "e.g. one. two. three.") is unchanged:
    // it has neither a 4+ contiguous run nor a 3+ space-separated run of the
    // same mark.
    internal static string CollapsePunctuationRuns(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Contiguous 4+ ( \1{3,} = the captured mark plus 3 or more = 4+ total;
        // "..." is exactly 3 and does not match).
        var collapsed = Regex.Replace(text, @"([.!?])\1{3,}", "$1");

        // Space-separated 3+ ( (?: \1){2,} = 2 or more repeats of
        // "<space><same mark>" = 3+ marks total; ". ." has one repeat and does
        // not match).
        collapsed = Regex.Replace(collapsed, @"([.!?])(?: \1){2,}", "$1");

        return collapsed;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds; **all tests pass, 0 failed** (the three new tests
plus the pre-existing CleanupRunner tests).

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupRunner.cs \
        tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "fix(cleanup): collapse degenerate punctuation runs on every path"
```

---

## Task 2: Same-token run cap in the ASR greedy decode (CHANGE 3)

Root-cause fix B. `ParakeetSession.GreedyDecode` can emit up to
`MaxTokensPerStep = 10` tokens per encoder frame via argmax with no
repetition control (`ParakeetSession.cs` ~lines 176-210). A stuck frame
(duration head predicts 0 while the same non-blank token keeps winning argmax)
sprays that token — e.g. `..........`. Add a consecutive-same-token cap: if the
same non-blank token id is emitted `MaxSameTokenRun` (3) times on a
**non-advancing** frame, force the frame to advance. Legitimate repeated words
("no no no") are separate emissions across **different** frames — each advances
`t` via a positive duration (`bestDur > 0`), which resets the run counter — so
they are untouched.

The decode loop needs an ONNX model, so it is not unit-testable directly.
Per the spec, extract the cap decision and the run-counter update into two
pure `internal static` seam helpers and unit-test those in isolation.

**Files:**
- Modify: `src/Winpepper.Asr/Winpepper.Asr.csproj` (add `InternalsVisibleTo`)
- Modify: `src/Winpepper.Asr/ParakeetSession.cs` (add const + two helpers; wire
  into `GreedyDecode`)
- Create: `tests/Winpepper.Asr.Tests/GreedyDecodeCapTests.cs`

**Interfaces:**
- Consumes: existing `GreedyDecode` locals `blankId`, `emitted`, `bestToken`,
  `bestDur`, `t`; existing `private const int MaxTokensPerStep = 10`.
- Produces:
  - `private const int ParakeetSession.MaxSameTokenRun = 3`
  - `internal static (int RunTokenId, int SameTokenRun) ParakeetSession.AdvanceSameTokenRun(int bestToken, int runTokenId, int sameTokenRun)`
  - `internal static bool ParakeetSession.ShouldForceFrameAdvance(int bestToken, int blankId, int emitted, int maxTokensPerStep, int sameTokenRun, int maxSameTokenRun)`

- [ ] **Step 1: Add `InternalsVisibleTo` to the Asr project**

`src/Winpepper.Asr/Winpepper.Asr.csproj` has no `InternalsVisibleTo` today.
Add one so the test project can reach the internal seam helpers. Insert this
`ItemGroup` before the closing `</Project>`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Winpepper.Asr.Tests" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing unit tests**

Create `tests/Winpepper.Asr.Tests/GreedyDecodeCapTests.cs`:

```csharp
using Shouldly;
using Winpepper.Asr;
using Xunit;

namespace Winpepper.Asr.Tests;

public class GreedyDecodeCapTests
{
    [Fact]
    public void AdvanceSameTokenRun_SameToken_Increments()
    {
        ParakeetSession.AdvanceSameTokenRun(bestToken: 5, runTokenId: 5, sameTokenRun: 2)
            .ShouldBe((5, 3));
    }

    [Fact]
    public void AdvanceSameTokenRun_DifferentToken_ResetsToOne()
    {
        ParakeetSession.AdvanceSameTokenRun(bestToken: 7, runTokenId: 5, sameTokenRun: 3)
            .ShouldBe((7, 1));
    }

    [Fact]
    public void ShouldForceFrameAdvance_BlankToken_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 99, blankId: 99, emitted: 0, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_PerFrameEmitCapHit_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 10, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_SameTokenRunCapHit_True()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 3, maxTokensPerStep: 10,
            sameTokenRun: 3, maxSameTokenRun: 3).ShouldBeTrue();
    }

    [Fact]
    public void ShouldForceFrameAdvance_NormalDecode_False()
    {
        ParakeetSession.ShouldForceFrameAdvance(
            bestToken: 3, blankId: 99, emitted: 1, maxTokensPerStep: 10,
            sameTokenRun: 1, maxSameTokenRun: 3).ShouldBeFalse();
    }

    // A stuck frame (bestDur == 0) that keeps arg-maxing the SAME non-blank
    // token must emit at most MaxSameTokenRun copies before the loop forces the
    // frame to advance. Proves the cap in isolation with no ONNX model, using
    // the exact same two helpers the decode loop uses.
    [Fact]
    public void StuckFrameSprayingSameToken_CappedAtMaxSameTokenRun()
    {
        const int blankId = 99, stuckToken = 3, maxPerStep = 10, maxRun = 3;
        var runTokenId = blankId;
        var sameTokenRun = 0;
        var emitted = 0;
        var emissions = 0;
        var advanced = false;

        for (var step = 0; step < 20 && !advanced; step++)
        {
            // token emitted this decode step
            emitted++;
            emissions++;
            (runTokenId, sameTokenRun) =
                ParakeetSession.AdvanceSameTokenRun(stuckToken, runTokenId, sameTokenRun);

            // bestDur == 0 branch: does the loop force an advance now?
            if (ParakeetSession.ShouldForceFrameAdvance(
                    stuckToken, blankId, emitted, maxPerStep, sameTokenRun, maxRun))
            {
                advanced = true;
            }
        }

        advanced.ShouldBeTrue();
        emissions.ShouldBe(maxRun); // exactly 3 copies, then forced advance
    }

    // A legitimate repeated word ("no no no") emits the same token across
    // DIFFERENT frames, each advancing via a positive duration. The run counter
    // resets on every frame advance, so the cap never fires.
    [Fact]
    public void RepeatedWordAcrossFrames_NeverCapped()
    {
        const int blankId = 99, wordToken = 3, maxPerStep = 10, maxRun = 3;
        var runTokenId = blankId;
        var sameTokenRun = 0;
        var capped = false;

        // Three frames; each emits "wordToken" once, then advances (bestDur > 0).
        for (var frame = 0; frame < 3; frame++)
        {
            var emitted = 1;
            (runTokenId, sameTokenRun) =
                ParakeetSession.AdvanceSameTokenRun(wordToken, runTokenId, sameTokenRun);

            if (ParakeetSession.ShouldForceFrameAdvance(
                    wordToken, blankId, emitted, maxPerStep, sameTokenRun, maxRun))
            {
                capped = true;
            }

            // bestDur > 0 advance resets the per-frame run tracking.
            sameTokenRun = 0;
            runTokenId = blankId;
        }

        capped.ShouldBeFalse();
    }
}
```

- [ ] **Step 3: Run the new tests to verify they fail**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: **build FAILS** — `'ParakeetSession' does not contain a definition
for 'AdvanceSameTokenRun'` / `'ShouldForceFrameAdvance'`.

- [ ] **Step 4: Add the const and the two seam helpers**

In `src/Winpepper.Asr/ParakeetSession.cs`, next to the existing
`private const int MaxTokensPerStep = 10;` (~line 15), add:

```csharp
    // Max consecutive emissions of the SAME non-blank token id on a single,
    // non-advancing frame before the decode loop forces the frame to advance.
    // Guards against a stuck encoder frame spraying one token (e.g. a period).
    private const int MaxSameTokenRun = 3;
```

Then add these two pure helpers to the class (e.g. immediately before
`GreedyDecode`):

```csharp
    // Update the same-token run counter for the current frame. A different
    // token id resets the run to 1. Returns the new (runTokenId, sameTokenRun).
    internal static (int RunTokenId, int SameTokenRun) AdvanceSameTokenRun(
        int bestToken, int runTokenId, int sameTokenRun) =>
        bestToken == runTokenId ? (runTokenId, sameTokenRun + 1) : (bestToken, 1);

    // Whether the decode loop must force a frame advance (stop emitting on this
    // frame). True when the token is blank, when the per-frame emission cap is
    // hit, OR when the same non-blank token has repeated too many times on a
    // non-advancing frame (the degenerate-spray guard).
    internal static bool ShouldForceFrameAdvance(
        int bestToken, int blankId, int emitted, int maxTokensPerStep,
        int sameTokenRun, int maxSameTokenRun) =>
        bestToken == blankId
        || emitted >= maxTokensPerStep
        || sameTokenRun >= maxSameTokenRun;
```

- [ ] **Step 5: Wire the helpers into `GreedyDecode`**

In `GreedyDecode`, after the line `var emitted = 0;` (~line 148), add the
run-tracking locals:

```csharp
        var runTokenId = blankId;
        var sameTokenRun = 0;
```

Inside the `if (bestToken != blankId)` block, after `emitted++;` and the
decoder-state copy (i.e. after the `stateC[ci++] = v;` line, still inside the
block), add:

```csharp
                (runTokenId, sameTokenRun) = AdvanceSameTokenRun(bestToken, runTokenId, sameTokenRun);
```

Then replace the existing frame-advance tail (currently):

```csharp
            if (bestDur > 0)
            {
                t += bestDur;
                emitted = 0;
            }
            else if (bestToken == blankId || emitted >= MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
```

with (reset the run tracking on BOTH advance branches, and add the
same-token guard via the seam helper):

```csharp
            if (bestDur > 0)
            {
                t += bestDur;
                emitted = 0;
                sameTokenRun = 0;
                runTokenId = blankId;
            }
            else if (ShouldForceFrameAdvance(
                         bestToken, blankId, emitted, MaxTokensPerStep, sameTokenRun, MaxSameTokenRun))
            {
                t += 1;
                emitted = 0;
                sameTokenRun = 0;
                runTokenId = blankId;
            }
```

> **Design note (seam choice):** the run counter only accumulates while the
> frame is *not* advancing via duration. Any legitimate token advances `t`
> through `bestDur > 0`, which resets `sameTokenRun`/`runTokenId`, so genuine
> repeated words ("no no no") — emitted across separate advancing frames —
> never trip the cap. Only a stuck, non-advancing frame emitting the same id
> is capped, and even then it still emits up to 3 before forcing progress, so
> normal decoding is untouched. Combined with CHANGE 2's cleanup collapse, a
> spray is both bounded at the source and erased before injection.

- [ ] **Step 6: Run the tests to verify they pass**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds; **all tests pass, 0 failed** (7 new cap tests plus
any pre-existing non-Windows Asr tests).

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Asr/Winpepper.Asr.csproj \
        src/Winpepper.Asr/ParakeetSession.cs \
        tests/Winpepper.Asr.Tests/GreedyDecodeCapTests.cs
git commit -m "fix(asr): cap consecutive same-token emissions in greedy decode"
```

---

## Task 3: Always archive completed dictations (CHANGE 1)

Owner decision: "Treat the aborted (never pasted) ones like any other." Every
completed dictation is archived to history at **completion time** (wav + raw
transcript + cleaned text + timings), exactly like a normal injected
dictation — whether it was injected, held-then-pasted, or held-then-discarded.
Today `PipelineHost` gates the archive with `if (!heldPending)` at two sites
(HoldUp path and Toggle-stop path). Remove both gates so `Archive` runs
unconditionally. The in-memory pending slot is unchanged (still memory-only,
discarded on the next hotkey).

> **Reminder:** `PipelineHost.cs` is in `Winpepper.App`, which does NOT build
> on Linux. These edits are mechanical and verified by review + the Windows
> smoke checklist (Task 6). Do not attempt to build `Winpepper.App` on Linux.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs` (HoldUp site ~lines
  393-477; Toggle-stop site ~lines 581-665)
- Modify: `docs/plans/2026-07-21-pending-paste.md` (dated addendum)

**Interfaces:**
- Consumes: existing `_archiver.Archive(new Winpepper.History.HistoryArchiveInput { ... })`
  blocks and their in-scope locals (`samples`/`samples2`, `transcription`/
  `transcription2`, `final`/`final2`, timing stopwatches).
- Produces: no new API. Behavior change only (archive is now unconditional).

- [ ] **Step 1: HoldUp path — remove the `heldPending` local declaration**

In the `HotkeyEventKind.HoldUp` case, delete the line (~393):

```csharp
                var heldPending = false;
```

- [ ] **Step 2: HoldUp path — drop the `heldPending = true;` assignment and fix the comment**

In the `decision == InjectionDecision.HoldPending` branch (~398-406), replace:

```csharp
                    if (decision == InjectionDecision.HoldPending)
                    {
                        // Focus moved to a different known field: do NOT inject anywhere.
                        // Hold the text as an in-memory pending paste (never persisted).
                        // heldPending gates OUT the archive block below so the held text
                        // and its audio are never written to the on-disk history store.
                        _vm.EnterPendingPaste(final, _targetAtStart);
                        heldPending = true;
                    }
```

with (keep the hold behavior; remove the gate flag; the dictation is still
archived unconditionally below per the owner's always-archive decision):

```csharp
                    if (decision == InjectionDecision.HoldPending)
                    {
                        // Focus moved to a different known field: do NOT inject anywhere.
                        // Hold the text as an in-memory pending paste (memory-only slot).
                        // Owner decision (2026-07-22): the dictation is STILL archived at
                        // completion below, exactly like an injected one — the pending
                        // slot itself remains memory-only and is not what gets persisted.
                        _vm.EnterPendingPaste(final, _targetAtStart);
                    }
```

- [ ] **Step 3: HoldUp path — archive unconditionally**

Replace the gated archive block (~456-477):

```csharp
                if (!heldPending)
                {
                    _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                    {
                        Samples16k = samples,
                        RawTranscript = transcription.Text,
                        CleanedText = final,
                        AsrModelName = producedModelName,
                        CleanupModelName = cleanupUsedModel,
                        WindowContextUsed = windowContextUsed,
                        WindowTitleAtStart = "",
                        WindowTitleAtInject = "",
                        Timings = new Winpepper.History.HistoryTimings
                        {
                            RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                            TranscribeMs = (int)transcribeSw.ElapsedMilliseconds,
                            CleanupMs = (int)cleanupSw.ElapsedMilliseconds,
                            InjectMs = (int)injectSw.ElapsedMilliseconds,
                            TotalMs = totalMs,
                        },
                    });
                }
```

with the same `Archive(...)` call, no longer wrapped in the `if`:

```csharp
                _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                {
                    Samples16k = samples,
                    RawTranscript = transcription.Text,
                    CleanedText = final,
                    AsrModelName = producedModelName,
                    CleanupModelName = cleanupUsedModel,
                    WindowContextUsed = windowContextUsed,
                    WindowTitleAtStart = "",
                    WindowTitleAtInject = "",
                    Timings = new Winpepper.History.HistoryTimings
                    {
                        RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                        TranscribeMs = (int)transcribeSw.ElapsedMilliseconds,
                        CleanupMs = (int)cleanupSw.ElapsedMilliseconds,
                        InjectMs = (int)injectSw.ElapsedMilliseconds,
                        TotalMs = totalMs,
                    },
                });
```

- [ ] **Step 4: Toggle-stop path — remove the `heldPending2` local declaration**

In the `HotkeyEventKind.Toggle` case's `else if (_engine.State == SessionState.Recording)`
branch, delete the line (~581):

```csharp
                    var heldPending2 = false;
```

- [ ] **Step 5: Toggle-stop path — drop the `heldPending2 = true;` assignment and fix the comment**

Replace the `decision2 == InjectionDecision.HoldPending` branch (~586-594):

```csharp
                        if (decision2 == InjectionDecision.HoldPending)
                        {
                            // Focus moved to a different known field: do NOT inject anywhere.
                            // Hold the text as an in-memory pending paste (never persisted).
                            // heldPending2 gates OUT the archive block below so the held text
                            // and its audio are never written to the on-disk history store.
                            _vm.EnterPendingPaste(final2, _targetAtStart);
                            heldPending2 = true;
                        }
```

with:

```csharp
                        if (decision2 == InjectionDecision.HoldPending)
                        {
                            // Focus moved to a different known field: do NOT inject anywhere.
                            // Hold the text as an in-memory pending paste (memory-only slot).
                            // Owner decision (2026-07-22): the dictation is STILL archived at
                            // completion below, exactly like an injected one — the pending
                            // slot itself remains memory-only and is not what gets persisted.
                            _vm.EnterPendingPaste(final2, _targetAtStart);
                        }
```

- [ ] **Step 6: Toggle-stop path — archive unconditionally**

Replace the gated archive block (~644-665):

```csharp
                    if (!heldPending2)
                    {
                        _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                        {
                            Samples16k = samples2,
                            RawTranscript = transcription2.Text,
                            CleanedText = final2,
                            AsrModelName = producedModelName2,
                            CleanupModelName = cleanupUsedModel2,
                            WindowContextUsed = windowContextUsed2,
                            WindowTitleAtStart = "",
                            WindowTitleAtInject = "",
                            Timings = new Winpepper.History.HistoryTimings
                            {
                                RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                                TranscribeMs = (int)transcribeSw2.ElapsedMilliseconds,
                                CleanupMs = (int)cleanupSw2.ElapsedMilliseconds,
                                InjectMs = (int)injectSw2.ElapsedMilliseconds,
                                TotalMs = totalMs2,
                            },
                        });
                    }
```

with the un-wrapped call:

```csharp
                    _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                    {
                        Samples16k = samples2,
                        RawTranscript = transcription2.Text,
                        CleanedText = final2,
                        AsrModelName = producedModelName2,
                        CleanupModelName = cleanupUsedModel2,
                        WindowContextUsed = windowContextUsed2,
                        WindowTitleAtStart = "",
                        WindowTitleAtInject = "",
                        Timings = new Winpepper.History.HistoryTimings
                        {
                            RecordMs = (int)(_recordStopwatch?.ElapsedMilliseconds ?? 0),
                            TranscribeMs = (int)transcribeSw2.ElapsedMilliseconds,
                            CleanupMs = (int)cleanupSw2.ElapsedMilliseconds,
                            InjectMs = (int)injectSw2.ElapsedMilliseconds,
                            TotalMs = totalMs2,
                        },
                    });
```

- [ ] **Step 7: Confirm no other reader of `heldPending`/`heldPending2` remains**

Run: `grep -n "heldPending" src/Winpepper.App/Hosting/PipelineHost.cs`
Expected: **no output** (both locals fully removed). `injected`/`injected2`
are untouched — they still feed `PostPasteGate.ShouldWatch`.

- [ ] **Step 8: Confirm no Linux test asserts no-archive-on-hold (nothing to update)**

The spec says "update any tests that assert no-archive-on-hold." `PipelineHost`
is not Linux-tested, so verify there is no such assertion to change:

```bash
grep -rn "heldPending\|no.*archive\|NotArchived\|ShouldNotArchive\|archive.*hold\|hold.*archive" \
    tests --include='*.cs'
```
Expected: **no match** tying "hold" to "no archive." (The pending behavior is
covered by `SessionViewModelPendingTests` / `PendingPasteStateTests`, which
assert the memory-only slot lifecycle — NOT archiving — and stay valid.) If a
match *does* appear that asserts "held ⇒ not archived," flip that assertion to
expect archiving and update it in this commit; otherwise there is nothing to
change and Windows smoke (Task 6, item 2) is the covering verification.

- [ ] **Step 9: Add the dated owner-decision addendum to the pending-paste plan doc**

At the very top of `docs/plans/2026-07-21-pending-paste.md` (immediately after
the H1 title line), insert this addendum block:

```markdown
> ## Addendum — 2026-07-22: Owner decision, ALWAYS archive
>
> **Supersedes:** the "Skip history archiving on the HoldPending path" step
> and the "held-then-later-pasted text is NEVER archived" design note in this
> document.
>
> **Decision (owner, 2026-07-22):** treat aborted (never-pasted) dictations
> like any other. EVERY completed dictation is archived to history at
> completion time (wav + raw transcript + cleaned text + timings) regardless
> of whether it was injected, held-then-pasted, or held-then-discarded. The
> `if (!heldPending)` / `if (!heldPending2)` archive gates in `PipelineHost`
> were removed so `HistoryArchiver.Archive` runs unconditionally at dictation
> completion.
>
> **Unchanged:** the in-memory `PendingPasteState` slot is still memory-only
> and is discarded on the next hotkey. Archiving happens at *completion time*
> from the pipeline locals (as for a normal dictation) — the pending slot
> itself is never the thing persisted.
```

- [ ] **Step 10: Privacy-wording check (pill "Click to paste" state)**

Verify no user-facing string claims the held text is "not saved / private /
never stored" (which the always-archive decision would make inaccurate):

```bash
grep -rin "never saved\|not saved\|won't be saved\|isn't saved\|not stored\|private\|never persist\|not persist" \
    src/Winpepper.App src/Winpepper.Core \
    --include='*.xaml' --include='*.cs' --include='*.resw'
```
Expected findings are limited to: (a) code comments (now corrected in Steps 2
& 5), (b) `SessionViewModel.cs` xmldoc "memory only, never persisted"
describing the *slot* (still accurate — the slot is memory-only), and (c) the
ASR-cloud `AsrPrivacyText` in `RecordingPage.xaml` (unrelated to pending
paste). The pill's `StatusText = "Click to paste"` carries no privacy claim.
**No user-facing string change is required.** If this grep surfaces a *new*
user-facing string that promises the held text is not saved, edit it to remove
that promise in this commit and note it here.

- [ ] **Step 11: Run the full non-Windows suite (regression check)**

`PipelineHost` does not build on Linux, so this proves the buildable projects
are unaffected — it does NOT compile the App edits (Windows smoke does that).

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for p in tests/Winpepper.Cleanup.Tests tests/Winpepper.Asr.Tests \
         tests/Winpepper.Core.Tests tests/Winpepper.History.Tests \
         tests/Winpepper.Corrections.Tests tests/Winpepper.Audio.Tests \
         tests/Winpepper.Platform.Tests tests/Winpepper.Models.Tests \
         tests/Winpepper.IntegrationTests; do
  dotnet build "$p/$(basename "$p").csproj" -f net9.0 -p:EnableWindowsTargeting=true || exit 1
  dotnet exec "$p/bin/Debug/net9.0/$(basename "$p").dll" -notrait "Platform=Windows" || exit 1
done
```
Expected: every project builds; **0 failed** across the suite (baseline 739/0).

- [ ] **Step 12: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs \
        docs/plans/2026-07-21-pending-paste.md
git commit -m "feat(app): always archive completed dictations, including held pending"
```

---

## Task 4: Pending-paste diagnostics logging (CHANGE 4)

Root-cause fix C. Add content-free `ILogger` diagnostics (existing `_log`
pattern in `PipelineHost`) at the pending-paste decision points so a future
"period spray" report can be correlated without capturing any transcript text.

> **Reminder:** `PipelineHost.cs` does not build on Linux. Verified by review +
> Windows smoke (Task 6, items 3-5). Keep edits mechanical.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`

**Interfaces:**
- Consumes: existing `_log` (`ILogger`), `_vm.HasPendingPaste`, `final`/`final2`,
  the `injected` local in `TryPastePending`.
- Produces: no new API. Five new log statements.

- [ ] **Step 1: HoldUp path — log the hold decision**

In the `HotkeyEventKind.HoldUp` case's `HoldPending` branch, right after
`_vm.EnterPendingPaste(final, _targetAtStart);`, add:

```csharp
                        _log.LogInformation(
                            "Held as pending paste ({Chars} chars, {Words} words)",
                            final.Length,
                            final.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
```

- [ ] **Step 2: Toggle-stop path — log the hold decision**

In the `HotkeyEventKind.Toggle` stop branch's `HoldPending` branch, right after
`_vm.EnterPendingPaste(final2, _targetAtStart);`, add:

```csharp
                            _log.LogInformation(
                                "Held as pending paste ({Chars} chars, {Words} words)",
                                final2.Length,
                                final2.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
```

- [ ] **Step 3: `TryPastePending` — log the paste outcome**

In `TryPastePending`, replace the final `return` (~292):

```csharp
        return _vm.NotifyPasteAttempted(injected);
```

with an outcome log before returning:

```csharp
        if (injected)
            _log.LogInformation("Pending paste injected");
        else
            _log.LogWarning("Pending paste injection failed");

        return _vm.NotifyPasteAttempted(injected);
```

- [ ] **Step 4: HoldDown — log when a new hotkey discards a held pending slot**

A new dictation discards any held pending slot (the VM calls `_pending.Discard()`
when the engine enters `Recording`). Log that at the start of a new session. In
the `HotkeyEventKind.HoldDown` case, immediately after the guard
`if (_engine.State != SessionState.Idle) return;` and before
`_engine.Apply(SessionEvent.StartRequested);`, add:

```csharp
                if (_vm.HasPendingPaste)
                    _log.LogInformation("Pending paste discarded unpasted");
```

- [ ] **Step 5: Toggle-start — log when a new hotkey discards a held pending slot**

In the `HotkeyEventKind.Toggle` case's `if (_engine.State == SessionState.Idle)`
branch, as the first statement inside the `{` (before
`_engine.Apply(SessionEvent.StartRequested);`), add:

```csharp
                    if (_vm.HasPendingPaste)
                        _log.LogInformation("Pending paste discarded unpasted");
```

- [ ] **Step 6: Verify all five log sites are present and content-free**

```bash
grep -n "Held as pending paste\|Pending paste injected\|Pending paste injection failed\|Pending paste discarded unpasted" \
    src/Winpepper.App/Hosting/PipelineHost.cs
```
Expected: **five** matches — two "Held as pending paste", one "Pending paste
injected", one "Pending paste injection failed", two "Pending paste discarded
unpasted" (that is 6 lines total: the "Held" and "discarded" strings each
appear twice). Confirm no message interpolates `final`/`final2`/`text`
content — only `.Length` and word counts.

- [ ] **Step 7: Run the full non-Windows suite (regression check)**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for p in tests/Winpepper.Cleanup.Tests tests/Winpepper.Asr.Tests \
         tests/Winpepper.Core.Tests tests/Winpepper.History.Tests \
         tests/Winpepper.Corrections.Tests tests/Winpepper.Audio.Tests \
         tests/Winpepper.Platform.Tests tests/Winpepper.Models.Tests \
         tests/Winpepper.IntegrationTests; do
  dotnet build "$p/$(basename "$p").csproj" -f net9.0 -p:EnableWindowsTargeting=true || exit 1
  dotnet exec "$p/bin/Debug/net9.0/$(basename "$p").dll" -notrait "Platform=Windows" || exit 1
done
```
Expected: **0 failed** (baseline preserved; App is not built here).

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs
git commit -m "feat(app): add content-free diagnostics around pending paste"
```

---

## Task 5: Full non-Windows suite green (baseline gate)

**Files:** none (verification only).

**Interfaces:** Consumes all prior tasks. Produces the green-suite evidence.

- [ ] **Step 1: Build and run every non-Windows test project**

```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
fail=0
for p in tests/Winpepper.Cleanup.Tests tests/Winpepper.Asr.Tests \
         tests/Winpepper.Core.Tests tests/Winpepper.History.Tests \
         tests/Winpepper.Corrections.Tests tests/Winpepper.Audio.Tests \
         tests/Winpepper.Platform.Tests tests/Winpepper.Models.Tests \
         tests/Winpepper.IntegrationTests; do
  name=$(basename "$p")
  dotnet build "$p/$name.csproj" -f net9.0 -p:EnableWindowsTargeting=true || fail=1
  dotnet exec "$p/bin/Debug/net9.0/$name.dll" -notrait "Platform=Windows" || fail=1
done
echo "AGGREGATE_FAIL=$fail"
```
Expected: `AGGREGATE_FAIL=0`. Total passing count should be **≥ 739** (baseline
739 + the ~10 new tests from Tasks 1 and 2), **0 failed**.

- [ ] **Step 2: Record the evidence**

Note the per-project pass counts and the aggregate (expected `0 failed`) in the
task's review notes. No commit for this task.

---

## Task 6: Windows smoke checklist (manual, on Windows)

**Files:** none (manual verification, documented outcome).

This is the **spec-designated verification** for CHANGE 1 and CHANGE 4 (which
cannot be unit-tested on Linux) and a final confirmation for CHANGE 2/CHANGE 3
end-to-end. Run on a real Windows build. Record PASS/FAIL for each item.

- [ ] **Item 1 — Normal dictation is injected AND archived.**
  Dictate into a focused field without moving focus. Expect: cleaned text is
  typed into the field; a new history entry exists (wav + raw + cleaned +
  timings). (CHANGE 1 regression check — normal path still archives.)

- [ ] **Item 2 — Held pending dictation is archived immediately at completion.**
  Dictate, then switch focus to a different known field before injection so the
  pill enters PENDING ("Click to paste"). Expect: the pill shows PENDING AND a
  history entry for this dictation exists **immediately at completion** (before
  any pill click), with wav + raw + cleaned + timings. (CHANGE 1 core outcome.)

- [ ] **Item 3 — Pill-click paste is logged.**
  With a pending paste held (Item 2), click the pill into a focused field.
  Expect: text is injected; the log contains `Pending paste injected` (or
  `Pending paste injection failed` if SendInput was refused). (CHANGE 4.)

- [ ] **Item 4 — Discard-by-new-hotkey is logged.**
  With a pending paste held, start a NEW dictation (hold or toggle) instead of
  clicking the pill. Expect: the log contains `Pending paste discarded
  unpasted`; the old pending slot is gone; the new dictation proceeds and (per
  Item 1/2) is itself archived. (CHANGE 4 + CHANGE 1.)

- [ ] **Item 5 — Hold decision is logged, content-free.**
  Confirm the log shows `Held as pending paste (N chars, M words)` at the hold
  decision and that **no** log line contains any transcript text anywhere in
  the pending flow. (CHANGE 4.)

- [ ] **Item 6 — Punctuation spray is neutralized (observational).**
  During normal use, confirm no dictation lands as a run of periods/marks in
  the target field. The two guards (ASR same-token cap + cleanup collapse)
  bound and erase such sprays; a genuine "..." ellipsis still renders normally.
  (CHANGE 2 + CHANGE 3 end-to-end.)

---

## Self-Review

**1. Spec coverage:**

| Spec requirement | Covered by |
|------------------|-----------|
| CHANGE 1 — remove both `heldPending` archive gates; archive unconditionally at completion | Task 3 Steps 1-6 |
| CHANGE 1 — pending slot unchanged (memory-only, discarded next hotkey) | Untouched by design; asserted in Task 3 Step 8 note + Item 4 |
| CHANGE 1 — update tests asserting no-archive-on-hold | Task 3 Step 8 (grep confirms none exist on Linux; flip-if-found instruction) |
| CHANGE 1 — pending-paste plan doc addendum with owner decision + date | Task 3 Step 9 |
| CHANGE 1 — check pill "Click to paste" privacy wording | Task 3 Step 10 |
| CHANGE 2 — collapse space-separated 3+ runs | Task 1 Step 1 (tests) + Step 4 (2nd regex) |
| CHANGE 2 — collapse contiguous 4+ runs | Task 1 Step 4 (1st regex) |
| CHANGE 2 — genuine "..." survives; ordinary text unchanged | Task 1 Step 1 `_OrdinaryText_Unchanged` theory |
| CHANGE 2 — runs on every path incl. raw-ASR passthrough | Task 1 Step 4 (in `ApplyDeterministicPostPass`) + Step 1 fallback-path E2E test |
| CHANGE 3 — cap consecutive same-token emissions (N=3), force frame advance | Task 2 Steps 4-5 |
| CHANGE 3 — normal decoding untouched; "no no no" still works | Task 2 seam design + `RepeatedWordAcrossFrames_NeverCapped` test |
| CHANGE 3 — add a testable seam since decode loop isn't unit-testable | Task 2 Steps 1,2,4 (two internal static helpers + InternalsVisibleTo) |
| CHANGE 4 — log hold decision (chars, words) | Task 4 Steps 1-2 |
| CHANGE 4 — log paste outcome (injected/failed) | Task 4 Step 3 |
| CHANGE 4 — log discard-by-new-hotkey | Task 4 Steps 4-5 |
| CHANGE 4 — no content in logs | Task 4 Step 6 + Item 5 |
| Verification — full non-Windows suite (739/0) | Task 3 Step 11, Task 4 Step 7, Task 5 |
| Verification — Windows smoke checklist | Task 6 |
| Do not touch packaging/ or hotkey hook internals | Honored — no such files in any task |

**1b. No silent deferrals of required behavior:**
- CHANGE 2 and CHANGE 3 are proven by real production code paths with xUnit
  tests (no stubs/mocks stand in for the behavior; the `FakeLlamaCleanupBackend`
  in the CHANGE 2 E2E test is only the LLM backend — the punctuation collapse
  under test is real production code on the real fallback path). The ASR seam
  helpers are the exact functions the production decode loop calls (Task 2
  Step 5), not test-only doubles.
- CHANGE 1 and CHANGE 4 live in `PipelineHost.cs`, which is structurally
  excluded from the Linux build. Their observable production outcomes are named
  in Task 6 (Items 1-5) as the spec-designated Windows-smoke verification. This
  is not moved to "future work" — it is a required, listed verification step in
  this plan. Per the recipe's mid-run rule, no requirement is deferred; the
  covering task (Task 6) exists and is explicit.

**2. Placeholder scan:** No "TBD/TODO/handle edge cases/similar to Task N"
placeholders. Every code step shows full code; every test step shows full test
code and the exact run command with expected output.

**3. Type consistency:** Helper names/signatures are identical across
definition and use: `CollapsePunctuationRuns(string)` (Task 1 def + tests);
`AdvanceSameTokenRun(int,int,int)` returning `(int RunTokenId, int SameTokenRun)`
and `ShouldForceFrameAdvance(int,int,int,int,int,int)` returning `bool` (Task 2
def in Step 4, wired in Step 5, tested in Step 2 — all matching). Constant
`MaxSameTokenRun = 3` defined once (Task 2 Step 4) and referenced in Step 5.
`_log`, `_vm.HasPendingPaste`, `_archiver.Archive`, and the `final`/`final2`
locals all match the current `PipelineHost` source verified during
investigation.
