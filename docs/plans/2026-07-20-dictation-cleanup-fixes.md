# Dictation Cleanup Fixes Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Stop the dictation cleanup stage from injecting prompt-scaffold
garbage, and teach it to correct the ASR's mishearings of the app's own
name ("wheat pepper" -> "Winpepper").

**Architecture:** Two independent improvements to `src/Winpepper.Cleanup`.
Part 1 is a cherry-pick of an already-merged upstream fix (commit `4ab2ab6`,
PR #50) that swaps the LLM backend executor and adds a plausibility guard in
`CleanupRunner`. Part 2 adds a small, self-contained `AppNameCorrector` to
the deterministic post-pass that both the LLM-success and fallback paths
already run.

**Tech Stack:** C# / .NET 9 (`net9.0` managed TFM), xUnit v3, Shouldly
assertions. All new/changed logic under test here is pure managed code
exercised through fakes — no P/Invoke, no LlamaSharp runtime.

## Global Constraints

- **SDK:** .NET SDK satisfying `global.json` — `sdk 9.0.100`,
  `rollForward: latestFeature`. `dotnet` is **not** on PATH in this worktree;
  it must be provisioned locally (Task 0).
- **Test runner:** The VSTest host (`dotnet test`) **crashes on this
  machine**. Pure-managed tests MUST run via the xUnit v3 **in-process
  runner**: `dotnet exec <TestAssembly>.dll`. Exclude Windows-only tests with
  `-notrait "Platform=Windows"`.
- **Test TFM:** Build/run the `net9.0` target framework only (not
  `net9.0-windows...`). Pass `-p:EnableWindowsTargeting=true` so the
  multi-targeted project references restore cleanly on Linux.
- **Do NOT commit binaries:** The cherry-picked commit accidentally includes
  `LlamaCleanupBackend.dll` at the repo root — it MUST be excluded. Do NOT
  commit the provisioned `.dotnet/` SDK directory either.
- **Out of scope — do not touch:** the keyboard hook code and the packaging
  code (`packaging/`).
- **Docs:** `README.md` is the only end-user markdown doc; do not add other
  end-user docs. This plan under `docs/plans/` is a working/agent doc and is fine.
- **Windows LLM path is unverifiable here:** `LlamaCleanupBackend.cs` is
  wrapped in `#if WINDOWS` and only references LlamaSharp under the Windows
  TFM. Its actual inference cannot be exercised on Linux — flag it for the
  Windows smoke test that follows this task.
- **Commits:** keep them focused and atomic.

---

## File Structure

**Part 1 (cherry-picked — do not hand-edit these; the cherry-pick supplies them):**
- Modify: `src/Winpepper.Cleanup/CleanupResult.cs` — adds `FallbackImplausible`
  enum member.
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` — adds the plausibility
  guard (`LooksLikePromptEcho`, length guard) before accepting LLM output.
- Modify: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` — swaps the stateful
  `InstructExecutor`-over-shared-context for `StatelessExecutor` with
  `ApplyTemplate=true` and a `<|im_end|>` anti-prompt (Windows-only code).
- Modify: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` — regression
  tests reproducing the reported garbage.

**Part 2 (new, hand-written):**
- Create: `src/Winpepper.Cleanup/AppNameCorrector.cs` — new static class;
  fixed-list mishearing correction with context-aware capitalization.
- Create: `tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs` — unit
  tests for the corrector in isolation.
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` — route both post-pass
  call sites through a single `ApplyDeterministicPostPass` helper that runs
  `CaseAwareReplacer` then `AppNameCorrector`.
- Modify: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` — end-to-end
  tests proving the app-name correction reaches injected text on both the
  LLM-success and fallback paths.

**Infra:**
- Modify: `.gitignore` — ignore the locally provisioned `/.dotnet/` SDK dir.

### Design decision: why a dedicated `AppNameCorrector`, not the corrections store

The codebase has a user-editable corrections mechanism (`Winpepper.Corrections`,
`corrections.json` -> `CorrectionsData.Replacements`) applied by
`CaseAwareReplacer`. The app-name fix is **not** wired through it, for two
reasons:

1. `CaseAwareReplacer` emits the replacement **verbatim** (by design — see its
   XML doc: "users configure the canonical spelling, so we don't smear it back
   into the matched case"). The spec requires **context-aware capitalization**
   of the app name (`Testing wheat pepper.` -> `Testing Winpepper.` but a
   lowercase mid-sentence occurrence stays lowercase). Verbatim replacement
   cannot do this.
2. The app-name correction must be a **built-in guarantee**, not user-editable
   data that can be deleted or overwritten in `corrections.json`.

So Part 2 is a separate, minimal, data-driven static corrector applied in the
same deterministic post-pass, immediately after user corrections.

### Design decision: capitalization rule (reconciling the two required test vectors)

The spec fixes two concrete, simultaneously-required outcomes:
- `Testing wheat pepper. How's it going?` -> `Testing Winpepper. How's it going?`
  (matched token `wheat` is **lowercase**, output `Winpepper` is **capital**).
- a mid-sentence lowercase occurrence -> lowercase `winpepper`.

No rule based purely on the matched token's own case can satisfy both (the same
lowercase `wheat` would have to map to both `Winpepper` and `winpepper`). The
distinguishing signal is the **surrounding context**. The chosen deterministic
rule — capitalize the replacement when the match is sentence-initial **or**
the immediately-preceding word is capitalized; otherwise lowercase — satisfies
both vectors:

| Input | Preceding word | Result |
|-------|----------------|--------|
| `Testing wheat pepper.` | `Testing` (capital) | `Testing Winpepper.` |
| `i shipped win pepper today` | `shipped` (lowercase) | `i shipped winpepper today` |
| `wheat pepper soup recipe` | (start of string) | `Winpepper soup recipe` |

This is a deliberate, documented departure from the spec's looser phrasing
("preserve leading capitalization based on the matched text"), because the two
concrete required vectors are more authoritative than the prose and cannot both
be met by matched-token-case preservation.

---

## Task 0: Provision the .NET 9 SDK and establish the test harness

**Files:**
- Modify: `.gitignore` (append one line)

**Interfaces:**
- Consumes: nothing.
- Produces: a working `dotnet` at `./.dotnet/dotnet` (via `DOTNET_ROOT` +
  `PATH`), and the verified test-run pattern
  `dotnet exec <TestAssembly>.dll -notrait "Platform=Windows"` used by every
  later task. Every later task begins by re-exporting `DOTNET_ROOT`/`PATH`
  (a fresh implementer shell does not inherit them).

- [ ] **Step 1: Confirm the worktree branch and a clean tree**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git branch --show-current
git status --short
```
Expected: branch is `dictation-cleanup-fixes`; `git status --short` prints
nothing (clean tree).

- [ ] **Step 2: Provision the .NET 9 SDK into the worktree**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: `dotnet --version` prints `9.0.100` (or a `9.0.x` patch via
`rollForward: latestFeature`).

- [ ] **Step 3: Ignore the provisioned SDK directory**

Append `/.dotnet/` to `.gitignore` (the file currently ignores `bin/`; add the
SDK dir so it never shows up as untracked noise or gets staged):
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
printf '\n# Locally provisioned .NET SDK (see docs/plans/2026-07-20-dictation-cleanup-fixes.md)\n/.dotnet/\n' >> .gitignore
git status --short
```
Expected: `git status --short` shows only ` M .gitignore` — the `.dotnet/`
tree is NOT listed as untracked.

- [ ] **Step 4: Verify the harness against the existing (pre-change) Cleanup tests**

This proves the xUnit v3 in-process runner works before any code changes.
Run:
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds; the runner prints a summary line ending in
`Failed: 0` (a passing existing suite). This is the exact run pattern reused
by every later task.

- [ ] **Step 5: Commit the .gitignore change**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git add .gitignore
git commit -m "chore: ignore locally provisioned .dotnet SDK dir"
```

---

## Task 1: Part 1 — cherry-pick the upstream cleanup-runaway fix (commit 4ab2ab6)

This task cherry-picks an already-reviewed upstream commit. It is **not** a
from-scratch reimplementation. The cherry-pick supplies all four source/test
file changes atomically (including the regression tests). The only manual work
is excluding the stray binary and preserving authorship.

**Files:**
- Modify (via cherry-pick): `src/Winpepper.Cleanup/CleanupResult.cs`
- Modify (via cherry-pick): `src/Winpepper.Cleanup/CleanupRunner.cs`
- Modify (via cherry-pick): `src/Winpepper.Cleanup/LlamaCleanupBackend.cs`
- Modify (via cherry-pick): `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`
- Exclude (do NOT commit): `LlamaCleanupBackend.dll` (repo root)

**Interfaces:**
- Consumes: Task 0's provisioned SDK / test harness.
- Produces:
  - `CleanupPath.FallbackImplausible` — new enum member on
    `Winpepper.Cleanup.CleanupPath`.
  - `internal static bool CleanupRunner.LooksLikePromptEcho(string cleaned, string rawTranscript)`
    — visible to tests via the existing `InternalsVisibleTo("Winpepper.Cleanup.Tests")`.
  - The plausibility guard sits in `CleanupRunner.RunAsync` **after** the
    `"..."` check and **before** `CaseAwareReplacer.Apply` (this is the exact
    seam Task 3 extends).

- [ ] **Step 1: Verify the cherry-pick source commit exists and applies cleanly**

Run:
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git cat-file -t 4ab2ab6
git show -s --format='%an <%ae> :: %s' 4ab2ab6
```
Expected: type is `commit`; author is
`Dan Shapiro <3732858+danshapiro@users.noreply.github.com>`, subject
`Fix cleanup LLM injecting prompt-scaffold garbage instead of dictation`.

- [ ] **Step 2: Cherry-pick the commit (it commits with the original author)**

A plain cherry-pick (no `-n`) preserves upstream authorship automatically. The
tree currently applies cleanly against main, so no conflict resolution is
expected.
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git cherry-pick 4ab2ab6
```
Expected: cherry-pick succeeds without conflicts. If (unexpectedly) a conflict
appears, resolve it by keeping the upstream hunks shown in Step 4 while
preserving main's surrounding structure, then `git cherry-pick --continue`.

- [ ] **Step 3: Confirm the stray binary landed in the commit, then remove it**

The commit includes `LlamaCleanupBackend.dll` at the repo root (root `*.dll`
is not gitignored — only `bin/` is). Confirm, then drop it from the commit and
append the provenance note. `git commit --amend` preserves the original author.
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git show --stat HEAD | grep -i 'LlamaCleanupBackend.dll'   # confirm it's there
git rm LlamaCleanupBackend.dll
git commit --amend -m "$(git log -1 --format=%B)

cherry-picked from https://github.com/obra/winpepper/pull/50 (commit 4ab2ab6)"
```

- [ ] **Step 4: Verify the resulting tree matches the expected upstream changes**

Confirm authorship, that no `.dll` is in the commit, that the working tree has
no stray dll, and that the code hunks are present.
```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git show -s --format='author=%an <%ae>%nsubject=%s' HEAD
git show --stat HEAD | grep -i '\.dll' || echo "OK: no dll in commit"
test -e LlamaCleanupBackend.dll && echo "FAIL: dll still on disk" || echo "OK: no dll on disk"
git status --short
```
Expected: author is `Dan Shapiro <...>`; `OK: no dll in commit`;
`OK: no dll on disk`; clean `git status --short`.

The committed changes must match these upstream hunks exactly:

`src/Winpepper.Cleanup/CleanupResult.cs` — enum gains one member:
```csharp
    FallbackBackendError, // The backend threw.
    FallbackImplausible,  // The LLM output echoed prompt scaffolding or blew past plausible length.
}
```

`src/Winpepper.Cleanup/CleanupRunner.cs` — guard inserted after the `"..."`
check, before the correction post-pass:
```csharp
        if (LooksLikePromptEcho(sanitized, rawTranscript))
        {
            _log.LogWarning("Cleanup output contains prompt-scaffold markers absent from the transcript; falling back. Output preview: {Preview}",
                sanitized.Length > 120 ? sanitized[..120] : sanitized);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (sanitized.Length > rawTranscript.Length * 2 + 64)
        {
            _log.LogWarning("Cleanup output implausibly long ({OutLen} chars from {InLen}-char transcript); falling back",
                sanitized.Length, rawTranscript.Length);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
```
plus the `HardEchoMarkers` / `SoftEchoMarkers` arrays and the
`internal static bool LooksLikePromptEcho(string cleaned, string rawTranscript)`
method (hard markers rejected whenever present in output but absent from the
transcript; soft markers `Human:/Assistant:/Input:/Output:` rejected only when
the spoken word is absent from the transcript).

`src/Winpepper.Cleanup/LlamaCleanupBackend.cs` — the shared `LLamaContext`
field, its construction, and its disposal are removed; `GenerateAsync`
constructs `new StatelessExecutor(_weights, _params, _log) { ApplyTemplate = true }`
and adds `"<|im_end|>"` to `AntiPrompts`.

- [ ] **Step 5: Build and run the Cleanup tests (now including the cherry-picked regression tests)**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds (the backend change is Windows-only `#if WINDOWS`
code and is excluded from the `net9.0` build — that is expected; the LLM
runtime path is verified later on Windows). The runner summary ends in
`Failed: 0`, and the four new regression tests are present and passing:
`Run_LlmEchoesPromptScaffold_FallsBackToRawTranscript`,
`Run_LlmOutputImplausiblyLong_FallsBackToRawTranscript`,
`Run_MarkerSpokenByUser_IsNotRejected`,
`LooksLikePromptEcho_ChatTemplateMarkers_Detected`.

- [ ] **Step 6: No commit needed**

The cherry-pick (amended in Step 3) is already the commit for this task. Do not
create an additional commit. Verify history:
```bash
git log --oneline -2
```
Expected: top commit is the cherry-picked fix; second is
`chore: ignore locally provisioned .dotnet SDK dir`.

---

## Task 2: Part 2 — `AppNameCorrector` (new class, TDD in isolation)

**Files:**
- Create: `src/Winpepper.Cleanup/AppNameCorrector.cs`
- Test: `tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs`

**Interfaces:**
- Consumes: nothing (pure string function).
- Produces: `public static string Winpepper.Cleanup.AppNameCorrector.Apply(string text)`
  — used by Task 3.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs`:
```csharp
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class AppNameCorrectorTests
{
    [Fact]
    public void Apply_SentenceInitialContext_CapitalizesReplacement()
    {
        // Observed ASR case: "testing winpepper" heard as "Testing wheat pepper".
        // Preceding word "Testing" is capitalized -> "Winpepper".
        AppNameCorrector.Apply("Testing wheat pepper. How's it going?")
            .ShouldBe("Testing Winpepper. How's it going?");
    }

    [Fact]
    public void Apply_MidSentenceLowercaseContext_LowercasesReplacement()
    {
        // Preceding word "shipped" is lowercase -> "winpepper".
        AppNameCorrector.Apply("i shipped win pepper today")
            .ShouldBe("i shipped winpepper today");
    }

    [Theory]
    [InlineData("wheat pepper")]
    [InlineData("win pepper")]
    [InlineData("wind pepper")]
    [InlineData("when pepper")]
    public void Apply_AllKnownMishearings_AreCorrected(string mishearing)
    {
        // Start-of-string is sentence-initial -> capitalized.
        AppNameCorrector.Apply($"{mishearing} rocks").ShouldBe("Winpepper rocks");
    }

    [Fact]
    public void Apply_AfterSentencePunctuation_CapitalizesReplacement()
    {
        AppNameCorrector.Apply("Done. wheat pepper wins.").ShouldBe("Done. Winpepper wins.");
    }

    [Fact]
    public void Apply_CollateralCulinaryPhrase_IsAlsoCorrected()
    {
        // Documented tradeoff: a genuine "wheat pepper" phrase is corrected too.
        // Accepted collateral — the app name is dictated far more often, and the
        // rule stays conservative (fixed list, no general vocabulary system).
        AppNameCorrector.Apply("wheat pepper soup recipe").ShouldBe("Winpepper soup recipe");
    }

    [Fact]
    public void Apply_NoMishearing_ReturnsTextUnchanged()
    {
        AppNameCorrector.Apply("just some ordinary sentence").ShouldBe("just some ordinary sentence");
    }

    [Fact]
    public void Apply_DoesNotMatchAcrossWordBoundaries()
    {
        // "unwheat pepper" must not match ("wheat" is not a whole word here).
        AppNameCorrector.Apply("unwheat peppery").ShouldBe("unwheat peppery");
    }

    [Fact]
    public void Apply_EmptyString_ReturnsEmpty()
    {
        AppNameCorrector.Apply("").ShouldBe("");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
```
Expected: **build FAILS** with `error CS0103: The name 'AppNameCorrector' does
not exist in the current context` (the class does not exist yet). This is the
RED state.

- [ ] **Step 3: Write the minimal implementation**

Create `src/Winpepper.Cleanup/AppNameCorrector.cs`:
```csharp
using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Deterministic correction for the ASR (parakeet-tdt-0.6b-v3) mishearing the
/// app's own name. "winpepper" is transcribed as "wheat pepper" / "win pepper"
/// / "wind pepper" / "when pepper". Maps a small fixed set of mishearings back
/// to the app name, choosing capitalization from surrounding context:
/// capitalize ("Winpepper") when the match is sentence-initial or the
/// immediately preceding word is capitalized; otherwise lowercase
/// ("winpepper"). Part 2.
///
/// Tradeoff: this is an unconditional whole-word replacement, so a legitimate
/// culinary phrase like "wheat pepper soup recipe" is also rewritten to
/// "Winpepper soup recipe". Accepted collateral: the app name is dictated far
/// more often than wheat-pepper cookery, and the rule stays conservative — a
/// small fixed list, deliberately NOT a general vocabulary-hint system.
/// </summary>
public static class AppNameCorrector
{
    // Common mishearings of "winpepper". Data-driven and intentionally minimal.
    private static readonly string[] Mishearings =
    {
        "wheat pepper", "win pepper", "wind pepper", "when pepper",
    };

    // \b(?:wheat pepper|win pepper|wind pepper|when pepper)\b, case-insensitive.
    private static readonly Regex Pattern = new(
        @"\b(?:" + string.Join("|", Mishearings.Select(Regex.Escape)) + @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return Pattern.Replace(text, m =>
            ShouldCapitalize(text, m.Index) ? "Winpepper" : "winpepper");
    }

    private static bool ShouldCapitalize(string text, int matchStart)
    {
        // Walk back over whitespace immediately before the match.
        int i = matchStart - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;

        if (i < 0) return true;                                  // start of text
        if (text[i] is '.' or '!' or '?' or ':' or ';') return true; // sentence-initial

        // Mirror the capitalization of the preceding word's first letter.
        while (i >= 0 && !char.IsWhiteSpace(text[i])) i--;
        return char.IsUpper(text[i + 1]);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds; runner summary ends in `Failed: 0`; the
`AppNameCorrectorTests` cases all pass.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git add src/Winpepper.Cleanup/AppNameCorrector.cs \
        tests/Winpepper.Cleanup.Tests/AppNameCorrectorTests.cs
git commit -m "feat: correct ASR mishearings of the app name to Winpepper"
```

---

## Task 3: Part 2 — wire `AppNameCorrector` into the cleanup post-pass

Route both deterministic post-pass call sites in `CleanupRunner` (the
LLM-success path and the `Finalize` fallback path) through a single helper so
the app-name correction reaches injected text on **every** path.

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs`
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`

**Interfaces:**
- Consumes: `AppNameCorrector.Apply(string)` (Task 2);
  `CaseAwareReplacer.Apply(string, IReadOnlyDictionary<string,string>)`;
  `CorrectionsData` and its `Replacements`.
- Produces: `private static string CleanupRunner.ApplyDeterministicPostPass(string text, CorrectionsData corrections)`
  (internal seam; not consumed by later tasks).

- [ ] **Step 1: Write the failing tests**

Append these two tests to the `CleanupRunnerTests` class in
`tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (place them just before
the closing brace of the class). They reuse the existing `NewRunner`,
`DefaultOptions`, and `FakeLlamaCleanupBackend` helpers already in the file:
```csharp
    [Fact]
    public async Task Run_LlmPath_AppNameMishearingCorrected()
    {
        // LLM returns plausible text that still contains the ASR mishearing.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "Testing wheat pepper. How's it going?",
        });
        var result = await runner.RunAsync("testing wheat pepper how's it going",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Testing Winpepper. How's it going?");
    }

    [Fact]
    public async Task Run_FallbackPath_AppNameMishearingCorrected()
    {
        // Backend throws -> FallbackBackendError -> raw transcript is what gets
        // injected. The app-name correction must still be applied there.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Throw = new InvalidOperationException("boom"),
        });
        var result = await runner.RunAsync("Testing wheat pepper.",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("Testing Winpepper.");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds but the runner reports these two tests **FAIL** — the
mishearing is not yet corrected, so `CleanedText` is
`"Testing wheat pepper. How's it going?"` / `"Testing wheat pepper."` instead
of the `Winpepper` forms. Summary shows `Failed: 2`. This is the RED state.

- [ ] **Step 3: Write the minimal implementation**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, replace the LLM-success post-pass
line. Change:
```csharp
        // 7) Apply deterministic correction post-pass.
        var withCorrections = CaseAwareReplacer.Apply(sanitized, corrections.Replacements);
```
to:
```csharp
        // 7) Apply deterministic correction post-pass (user corrections, then
        //    the built-in app-name mishearing correction).
        var withCorrections = ApplyDeterministicPostPass(sanitized, corrections);
```

Then, in the `Finalize` method, change:
```csharp
        var cleaned = CaseAwareReplacer.Apply(rawTranscript, corrections.Replacements);
```
to:
```csharp
        var cleaned = ApplyDeterministicPostPass(rawTranscript, corrections);
```

Finally, add the shared helper to the `CleanupRunner` class (place it directly
above the `Finalize` method):
```csharp
    // Deterministic post-pass shared by the LLM-success and fallback paths:
    // user-configured corrections first, then the built-in app-name mishearing
    // correction. Applied on every path so injected text always benefits.
    private static string ApplyDeterministicPostPass(string text, CorrectionsData corrections)
    {
        var withCorrections = CaseAwareReplacer.Apply(text, corrections.Replacements);
        return AppNameCorrector.Apply(withCorrections);
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds; runner summary ends in `Failed: 0`. The two new
`CleanupRunnerTests` cases pass, and all cherry-picked regression tests and
`AppNameCorrectorTests` remain green.

- [ ] **Step 5: Commit**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git add src/Winpepper.Cleanup/CleanupRunner.cs \
        tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "feat: apply app-name correction in the cleanup post-pass on all paths"
```

---

## Task 4: Full non-Windows suite gate

Prove nothing else broke. The post-pass change touches `CleanupRunner`, which
is shared, so run the entire pure-managed test suite via the in-process runner.

**Files:** none (verification only).

**Interfaces:**
- Consumes: everything from Tasks 0–3.
- Produces: evidence that the full non-Windows suite is green.

- [ ] **Step 1: Build and run every managed test project (excluding Windows-only tests)**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
fail=0
for proj in $(find tests -name '*.Tests.csproj' | sort); do
  name=$(basename "$proj" .csproj)
  dir=$(dirname "$proj")
  echo "=== building $name ==="
  dotnet build "$proj" -f net9.0 -p:EnableWindowsTargeting=true -v minimal \
    || { echo "BUILD FAILED: $name"; fail=1; continue; }
  dll="$dir/bin/Debug/net9.0/$name.dll"
  echo "=== running $name ==="
  dotnet exec "$dll" -notrait "Platform=Windows" \
    || { echo "TESTS FAILED: $name"; fail=1; }
done
echo "AGGREGATE_EXIT=$fail"
```
Expected: every project builds and runs; each per-project summary ends in
`Failed: 0`; final line prints `AGGREGATE_EXIT=0`.

- [ ] **Step 2: Confirm the working tree is clean and history is correct**

```bash
cd /home/dan/code/winpepper/.worktrees/dictation-cleanup-fixes
git status --short
git log --oneline -5
test -e LlamaCleanupBackend.dll && echo "FAIL: stray dll" || echo "OK: no stray dll"
```
Expected: `git status --short` is empty (the `.dotnet/` SDK and `bin/` are
gitignored); `OK: no stray dll`; the log shows, newest first: the app-name
post-pass wiring, the `AppNameCorrector` feature, the cherry-picked cleanup
fix (authored by Dan Shapiro with the `cherry-picked from ...#50` note), and
the `.gitignore` chore.

- [ ] **Step 3: Record the Windows-only follow-up (no code change)**

The actual LlamaSharp inference path (`LlamaCleanupBackend.GenerateAsync` under
`#if WINDOWS`, using `StatelessExecutor` + `ApplyTemplate`) is Windows-only and
cannot be exercised on this Linux machine. It must be verified in the Windows
smoke test that follows this task: dictate `Testing winpepper. How's it going?`
and confirm the injected text is `Testing Winpepper. How's it going?` with no
`<OUTPUT>` / `Human:` / `<|im_*|>` scaffolding, and that a second consecutive
dictation still works (no `llama_decode InvalidInputBatch`). No action in this
task beyond noting it here.

---

## Self-Review

**1. Spec coverage**

| Spec requirement | Covered by |
|------------------|-----------|
| Part 1: cherry-pick commit `4ab2ab6` (not reimplement) | Task 1 Step 2 |
| (a) `LlamaCleanupBackend.cs` StatelessExecutor + ApplyTemplate + `<|im_end|>` | Task 1 (cherry-pick) + Step 4 verify |
| (b) `CleanupRunner.cs` plausibility guard (hard/soft markers, length guard, fallback) | Task 1 (cherry-pick) + Step 4 verify |
| (c) `CleanupResult.cs` `FallbackImplausible` enum | Task 1 (cherry-pick) + Step 4 verify |
| (d) regression tests reproducing the garbage | Task 1 (cherry-pick) + Step 5 run |
| Exclude the stray `LlamaCleanupBackend.dll` | Task 1 Step 3–4 |
| Resolve conflicts / keep main's structure | Task 1 Step 2 |
| Preserve upstream authorship + `cherry-picked from ...#50` note | Task 1 Step 2–3 |
| Part 2: app-name correction in the deterministic post-pass | Task 2 + Task 3 |
| Mishearings `wheat/win/wind/when pepper`, word-boundary, case-insensitive | Task 2 Step 3 (`Mishearings`, `\b`, `IgnoreCase`) |
| Case-preserving capitalization | Task 2 Step 3 (`ShouldCapitalize`) + documented design decision |
| Data-driven & minimal (no general vocabulary system) | Task 2 Step 3 (fixed array) |
| Test: `Testing wheat pepper. How's it going?` -> `Testing Winpepper. ...` | Task 2 Step 1 + Task 3 Step 1 (end-to-end) |
| Test: mid-sentence lowercase | Task 2 Step 1 (`Apply_MidSentenceLowercaseContext...`) |
| Test: collateral `wheat pepper soup recipe` still corrected + documented tradeoff | Task 2 Step 1 + class XML doc comment |
| Verification via xUnit v3 in-process runner (`dotnet exec`, not VSTest) | Global Constraints + every run step |
| .NET 9 SDK local provisioning into the worktree | Task 0 |
| Run full non-Windows suite incl. new/cherry-picked Cleanup tests | Task 1 Step 5 + Task 4 |
| Windows LLM path unverifiable here — note for Windows smoke test | Global Constraints + Task 4 Step 3 |
| Do not touch keyboard hook or packaging | Global Constraints (no task touches them) |

No gaps. The spec's "wire through an existing user-editable corrections
mechanism instead" alternative was evaluated and explicitly rejected with
reasons (see "Design decision" above) — the spec permits either, and this plan
chooses the dedicated corrector and says so.

**1b. No silent deferrals of required behavior**

Every requirement maps to a production code change proven by a test that runs
real production code through fakes at the transport boundary only:
- The plausibility guard runs as real `CleanupRunner` code; the four cherry-
  picked tests inject garbage through `FakeLlamaCleanupBackend` (the LLM
  transport) and assert the real guard falls back — no stubbed guard.
- `AppNameCorrector` is real production code, tested directly (Task 2) and
  end-to-end through the real `CleanupRunner` post-pass on both the LLM and
  fallback paths (Task 3).
- The only test double is `FakeLlamaCleanupBackend`, which stands in for the
  Windows-only LlamaSharp runtime — a genuine platform boundary, not a
  behavior being faked. The real inference path is a Windows-only concern
  explicitly flagged for the following Windows smoke test (Task 4 Step 3); it
  is **not** deferrable managed behavior, and there is no managed way to
  exercise it on this machine.

No requirement is moved to "known limitations" or "future work".

**2. Placeholder scan**

No `TBD`/`TODO`/"add error handling"/"similar to Task N" placeholders. Every
code step shows complete code; every run step shows the exact command and
expected output.

**3. Type consistency**

- `AppNameCorrector.Apply(string) : string` — defined Task 2 Step 3, consumed
  identically in Task 3 Step 3 and the Task 2 tests.
- `ApplyDeterministicPostPass(string, CorrectionsData) : string` — defined and
  both call sites updated in Task 3 Step 3.
- `CleanupPath.FallbackImplausible`, `CleanupPath.Llm`,
  `CleanupPath.FallbackBackendError` — used in tests exactly as the enum
  (Task 1 / existing) defines them.
- `FakeLlamaCleanupBackend { Output = ..., Throw = ... }` and
  `runner.RunAsync(rawTranscript, corrections, windowContextTask, options, ct)`
  match the existing fake and runner signatures in the repo.
- `CorrectionsData.Empty` and `.Replacements` used as defined in
  `Winpepper.Corrections`.

Consistent throughout.
