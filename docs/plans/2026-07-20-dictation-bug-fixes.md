# Dictation Bug Fixes Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Fix three root-caused dictation bugs — a frozen status pill, lost
start-of-speech audio, and a cleanup LLM that replaces short utterances with
its own few-shot example — without touching the keyboard hook or packaging.

**Architecture:** Push every testable decision into pure-managed classes
(`Winpepper.Core`, `Winpepper.Cleanup`, `Winpepper.Audio`) that run on Linux
via the xUnit v3 in-process runner. Keep the Windows-only layers
(`Winpepper.App` WinUI, WASAPI wiring, LLamaSharp backend — all `#if WINDOWS`)
thin wrappers over that tested logic, verified through the **Windows Smoke Test
Checklist** at the end. WinUI rendering and live WASAPI cannot be verified on
Linux; those changes are surgical and smoke-checked, not stubbed or deferred.

**Tech Stack:** C# / .NET 9 (`net9.0` target on Linux), xUnit v3, Shouldly
assertions, NAudio (WASAPI), LLamaSharp 0.27 (Qwen2.5-0.5B cleanup, Windows
only), WinUI 3 / WinAppSDK (status pill, Windows only).

## Global Constraints

Every task's requirements implicitly include this section. Values are copied
verbatim from the spec.

- **Out of scope — DO NOT touch:** the keyboard hook
  (`src/Winpepper.Platform/Hotkeys/*`) and packaging (`packaging/`).
- **Baseline tip:** investigation evidence was verified at commit `0517994`.
- **.NET SDK:** `dotnet` is **not** on PATH in this worktree. Task 0 provisions
  it into `./.dotnet/` (already gitignored via `/.dotnet/`; nothing is
  committed for the SDK). **Network is required** for Task 0 and the first
  build.
- **Test runner:** the VSTest host (`dotnet test`) **crashes on this machine**.
  Pure-managed tests MUST run via the xUnit v3 **in-process runner**:
  `dotnet exec <TestAssembly>.dll`. Exclude Windows-only tests with
  `-notrait "Platform=Windows"`. Target one test with
  `-method "<Namespace>.<Class>.<Method>"`, or one class with
  `-class "<Namespace>.<Class>"`.
- **Test TFM:** build/run the `net9.0` target only (never `net9.0-windows...`).
  Always pass `-p:EnableWindowsTargeting=true` so multi-targeted project
  references restore on Linux.
- **Every test step re-exports the SDK env** (a fresh implementer shell does
  not inherit Task 0's exports):
  ```bash
  export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
  ```
- **Do NOT commit** `./.dotnet/` or build output (`bin/`, `obj/` are gitignored).
- **WinUI / WASAPI / LLamaSharp are Linux-unbuildable and Linux-untestable.**
  Files under `src/Winpepper.App`, `WarmWasapiRecorder`, and
  `LlamaCleanupBackend` are wrapped in `#if WINDOWS`. Changes there are kept
  thin, call the tested pure-managed logic, and are verified in the **Windows
  Smoke Test Checklist** — they are NOT deferred or stubbed.
- **Audio pre-roll:** warm capture keeps a rolling buffer of the last **~500 ms**.
- **Settings default:** `PrewarmMicEnabled` defaults to **true**; when false,
  fall back to cold-start capture (the always-on mic is a privacy trade-off,
  documented on the toggle).
- **Cleanup thresholds (spec fix-3):** short-transcript bypass for raw
  transcripts under **4 words**; content-word retention floor of **0.40**
  applied when the raw has more than **6 words**; wholesale-replacement
  (retention 0) always rejected; known-example echo rejected when retention is
  below the floor.
- **Docs:** `README.md` is the only end-user markdown doc; this plan under
  `docs/plans/` is a working/agent doc and is fine.
- **Commits:** focused and atomic; `feat:`/`fix:`/`test:`/`refactor:`/`docs:`
  prefixes and the standard Amplifier co-author trailer (shown in each commit
  step).

---

## Scope Check

The spec spans three loosely-coupled subsystems: the WinUI status pill (Bug 1),
warm audio capture (Bug 2), and cleanup safety (Bug 3). Per the writing-plans
scope guidance this could be three plans; it is delivered as one because the
workflow requested a single dictation-bug-fix batch and each task below
produces its own independently-testable deliverable. There is **no** single
system-wide end-to-end test possible on Linux — the integration surface is
WinUI + live WASAPI + a real GGUF model. That whole-system verification is the
**Windows Smoke Test Checklist** at the end.

---

## File Structure

**Bug 3 — cleanup safety (pure-managed, Linux-tested):**
- `src/Winpepper.Cleanup/BasePrompts.cs` — reduce few-shot to one example;
  expose `DefaultExampleOutputs` (anti-drift echo denylist).
- `src/Winpepper.Cleanup/TranscriptSimilarity.cs` *(new)* — content-word
  tokenizer + retention ratio + word count.
- `src/Winpepper.Cleanup/CleanupResult.cs` — add `CleanupPath.BypassShort`.
- `src/Winpepper.Cleanup/PromptBuilder.cs` — split into `BuildSystem` / `BuildUser`.
- `src/Winpepper.Cleanup/ILlamaCleanupBackend.cs` — system + user prompt args.
- `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` *(Windows)* — ChatML system role.
- `src/Winpepper.Cleanup/CleanupRunner.cs` — short-transcript bypass + similarity
  floor + known-example echo guard.
- Tests: `tests/Winpepper.Cleanup.Tests/{BasePromptsTests,PromptBuilderTests,
  CleanupRunnerTests,TranscriptSimilarityTests,Fakes/FakeLlamaCleanupBackend,
  LlamaCleanupBackendIntegrationTests}.cs`.

**Bug 2 — warm capture (ring buffer pure-managed + thin WASAPI wiring):**
- `src/Winpepper.Audio/WarmCaptureBuffer.cs` *(new, pure)* — rolling pre-roll
  ring + active-session accumulation.
- `src/Winpepper.Audio/IWarmAudioRecorder.cs` *(new, pure interface)*.
- `src/Winpepper.Audio/WarmWasapiRecorder.cs` *(new, Windows)* — one persistent
  `WasapiCapture` feeding the buffer.
- `src/Winpepper.Core/Settings/AppSettings.cs` — `PrewarmMicEnabled` (default true).
- `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs` — toggle property.
- `src/Winpepper.App/Hosting/PipelineHost.cs` *(Windows)* — use the shared warm
  recorder; meter only during a session.
- `src/Winpepper.App/Hosting/AppShell.cs` *(Windows)* — pass the setting.
- `src/Winpepper.App/Views/RecordingPage.xaml{,.cs}` *(Windows)* — the toggle UI.
- Tests: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs` *(new)*;
  `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`.

**Bug 1 — status pill freeze (WinUI, Windows-only, smoke-verified):**
- `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` — **primary fix**: one-time
  `Activate()` → `Hide()` island init; explanatory comment. Click-through/layered
  setup left UNCHANGED.
- **Contingency only** (applied via the Windows smoke gate if the pill is still
  frozen): `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs` — drop
  `WS_EX_LAYERED` + `SetLayeredWindowAttributes`; `StatusPillWindow.xaml` —
  whole-window alpha via XAML `Opacity`. See Task 10 for why this is not the
  default (cross-process click-through relies on the layered style).

---

## Task 0: Provision the .NET 9 SDK

**Files:** none committed (SDK lands in gitignored `./.dotnet/`).

**Interfaces:**
- Produces: a working `dotnet` at `./.dotnet/dotnet`, reached via
  `DOTNET_ROOT`/`PATH`. Every later task's test steps re-export these.

- [ ] **Step 1: Provision the SDK**

Run from the worktree root:
```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
/tmp/dotnet-install.sh --version 9.0.100 --install-dir "$PWD/.dotnet"
export DOTNET_ROOT="$PWD/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
```

- [ ] **Step 2: Verify dotnet resolves**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet --version
```
Expected: prints `9.0.100` (or a `latestFeature` roll-forward like `9.0.1xx`).

- [ ] **Step 3: Confirm nothing to commit**

Run: `git status --short`
Expected: no `.dotnet/` entries (it is gitignored). No commit is made in this task.

---

## Task 1: Reduce cleanup few-shot to one example + expose echo denylist

Bug 3 root cause (a): `BasePrompts.Default` embeds three `Input:/Output:`
few-shot pairs; the 0.5B model pattern-completes them and emits an example's
output verbatim. Reduce to a single worked example and expose the remaining
example output(s) as a constant so the runner (Task 5) can detect verbatim
echoes without the two drifting apart (spec fix-(ii)/(iv)).

**Files:**
- Modify: `src/Winpepper.Cleanup/BasePrompts.cs`
- Test: `tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs:34-43`

**Interfaces:**
- Produces: `BasePrompts.Default` (now `static readonly string`, one example);
  `public static readonly IReadOnlyList<string> BasePrompts.DefaultExampleOutputs`
  — the exact output string(s) of the example(s) in `Default`. Task 5 and its
  tests consume `DefaultExampleOutputs`.

- [ ] **Step 1: Update the two affected BasePrompts tests to expect one example**

In `tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs`, replace the
`Default_HasThreeExamples` test (lines 34-43) with:

```csharp
    [Fact]
    public void Default_HasExactlyOneExample()
    {
        var p = BasePrompts.Default;
        // A single worked example keeps a 0.5B model from pattern-completing a
        // few-shot block (spec fix-(iv)). Examples are "Input:"/"Output:" lines.
        var inputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Input:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var outputs = System.Text.RegularExpressions.Regex.Matches(p, @"^Output:", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        inputs.ShouldBe(1);
        outputs.ShouldBe(1);
    }

    [Fact]
    public void DefaultExampleOutputs_MatchesTheEmbeddedExampleOutput()
    {
        // Anti-drift: the denylist the runner checks must be exactly the
        // output text shown in the prompt.
        BasePrompts.DefaultExampleOutputs.Count.ShouldBe(1);
        BasePrompts.Default.ShouldContain("Output: " + BasePrompts.DefaultExampleOutputs[0]);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `CS0117: 'BasePrompts' does not contain a
definition for 'DefaultExampleOutputs'`. That is the RED signal.

- [ ] **Step 3: Rewrite BasePrompts with one example + the denylist constant**

Replace the entire contents of `src/Winpepper.Cleanup/BasePrompts.cs` with:

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Built-in cleanup base prompts. Spec §6.3 (Default) and §6.4 (Literal).
///
/// Bug-3 fix (iv): the Default prompt now carries a SINGLE worked example.
/// Feeding a 0.5B instruct model several "Input:/Output:" pairs made it slip
/// into few-shot completion mode and echo an example's output verbatim as if
/// it were the dictation. The one retained example (self-correction, the most
/// error-prone transform) is exposed as <see cref="DefaultExampleOutputs"/> so
/// the runner can detect verbatim echoes; building both from the same constant
/// keeps them from drifting apart.
/// </summary>
public static class BasePrompts
{
    private const string DefaultExampleInput =
        "write me a function called add_numbers no wait scratch that call it sum";
    private const string DefaultExampleOutput = "Write me a function called sum.";

    /// <summary>Output text of every example embedded in <see cref="Default"/>.
    /// The runner rejects a cleaned result that matches one of these verbatim
    /// when it shares little content with the raw transcript (spec fix-(ii)).</summary>
    public static readonly IReadOnlyList<string> DefaultExampleOutputs =
        new[] { DefaultExampleOutput };

    public static readonly string Default = $$"""
You are a dictation cleanup assistant. The user spoke into a microphone and an
automatic speech recognizer produced the raw transcript inside the USER-INPUT
block. Your job is to return the same content in clean written form.

Apply these transformations:

1. Remove these filler words and phrases when they do not carry meaning:
   um, uh, like, you know, basically, literally, sort of, kind of.
2. Apply self-correction commands literally: when the speaker says
   "scratch that", "never mind", or "no let me start over", delete the
   preceding clause or sentence as appropriate and continue with the next
   spoken content.
3. Fix obvious recognition errors for names, commands, file paths, and jargon
   when the surrounding context makes the correct spelling unambiguous. When
   in doubt, prefer the user's spoken words.
4. Add sentence-level punctuation and capitalization that the recognizer omits.
5. Honor explicit punctuation and spelling commands ("comma", "period",
   "spell that", etc.) — render the punctuation literally and never echo the
   command word.
6. Reproduce the entire transcript. Never summarize, never delete sentences
   that the speaker meant to keep, never paraphrase content away.

The output must read as if a human had typed it directly. Output the cleaned
text and nothing else — no preamble, no closing remark, no quoting, no
explanation of changes.

One example follows.

Input: {{DefaultExampleInput}}
Output: {{DefaultExampleOutput}}
""";

    public const string Literal = """
You are a dictation transcription cleaner. Output the speaker's words exactly
as transcribed, with two changes only:

1. Add sentence punctuation and capitalization.
2. Honor explicit punctuation and spelling commands literally.

Do not remove filler words. Do not paraphrase. Do not interpret self-correction
commands; leave them in the output as spoken. Output the cleaned text and
nothing else.
""";

    public static string ForProfile(CleanupProfile profile, string? custom) =>
        profile switch
        {
            CleanupProfile.Ordinary => Default,
            CleanupProfile.Literal  => Literal,
            CleanupProfile.Custom   => string.IsNullOrWhiteSpace(custom) ? Default : custom!,
            _                       => Default,
        };
}
```

Note: the interpolated raw string uses `$$""" ... {{ }} """` so the literal
`{`/`}` are not needed here but the doubled delimiters keep it robust if braces
are added later.

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows" -class "Winpepper.Cleanup.Tests.BasePromptsTests"
```
Expected: **PASS** — `Failed: 0`. The unchanged assertions
(`Default_MentionsFillerWords`, `Default_MentionsSelfCorrectionCommands`,
`Default_RequiresFullTranscriptReproduction`) still hold — those strings live
in the numbered rules.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Cleanup/BasePrompts.cs tests/Winpepper.Cleanup.Tests/BasePromptsTests.cs
git commit -m "$(cat <<'EOF'
fix: reduce cleanup few-shot to one example, expose echo denylist

Bug-3(a): a 0.5B instruct model fed three Input/Output pairs pattern-completed
the block and emitted an example verbatim. Keep one worked example and expose
its output as BasePrompts.DefaultExampleOutputs so the runner can catch echoes.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 2: Add the content-similarity helper

The similarity floor (Task 5) needs a pure tokenizer that reduces a transcript
to content words (dropping fillers and self-correction phrases) and a retention
ratio. Build and test it in isolation first.

**Files:**
- Create: `src/Winpepper.Cleanup/TranscriptSimilarity.cs`
- Test: `tests/Winpepper.Cleanup.Tests/TranscriptSimilarityTests.cs`

**Interfaces:**
- Produces:
  - `TranscriptSimilarity.ContentWords(string) -> IReadOnlyList<string>`
    (lowercase, punctuation-split, filler/self-correction removed).
  - `TranscriptSimilarity.RetentionRatio(string raw, string cleaned) -> double`
    (fraction of raw's unique content words present in cleaned; `1.0` when raw
    has no content words).
  - `TranscriptSimilarity.WordCount(string) -> int` (whitespace tokens).
  Task 4 consumes `WordCount`; Task 5 consumes all three.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Cleanup.Tests/TranscriptSimilarityTests.cs`:

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class TranscriptSimilarityTests
{
    [Fact]
    public void ContentWords_DropsFillersAndSelfCorrectionPhrases()
    {
        var words = TranscriptSimilarity.ContentWords(
            "um so like I think we should basically just ship it tomorrow you know");
        // um, like, basically, "you know" removed; "so" kept (not a filler).
        words.ShouldBe(new[] { "so", "i", "think", "we", "should", "just", "ship", "it", "tomorrow" });
    }

    [Fact]
    public void ContentWords_RemovesSelfCorrectionPhrases()
    {
        var words = TranscriptSimilarity.ContentWords(
            "write me a function called add_numbers no wait scratch that call it sum");
        // "no wait" and "scratch that" removed; add_numbers splits on '_'.
        words.ShouldBe(new[] { "write", "me", "a", "function", "called", "add", "numbers", "call", "it", "sum" });
    }

    [Fact]
    public void RetentionRatio_HighWhenCleanedKeepsRawContent()
    {
        var r = TranscriptSimilarity.RetentionRatio(
            "um so like I think we should basically just ship it tomorrow you know",
            "I think we should just ship it tomorrow.");
        r.ShouldBeGreaterThan(0.8);
    }

    [Fact]
    public void RetentionRatio_ZeroOnWholesaleReplacement()
    {
        var r = TranscriptSimilarity.RetentionRatio(
            "who should be fixing this me or the person configuring runpod", "Me");
        r.ShouldBeLessThan(0.2);
    }

    [Fact]
    public void RetentionRatio_OneWhenRawHasNoContentWords()
    {
        // All-filler raw has no content words -> nothing to lose -> 1.0.
        TranscriptSimilarity.RetentionRatio("um uh like you know", "anything").ShouldBe(1.0);
    }

    [Fact]
    public void WordCount_CountsWhitespaceTokens()
    {
        TranscriptSimilarity.WordCount("  Right.  ").ShouldBe(1);
        TranscriptSimilarity.WordCount("output colon forty two").ShouldBe(4);
        TranscriptSimilarity.WordCount("").ShouldBe(0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `CS0103: The name 'TranscriptSimilarity' does not
exist`.

- [ ] **Step 3: Implement the helper**

Create `src/Winpepper.Cleanup/TranscriptSimilarity.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Winpepper.Cleanup;

/// <summary>
/// Pure content-word similarity used by <see cref="CleanupRunner"/> to detect
/// wholesale replacement / severe truncation by the cleanup LLM (Bug-3 fix-(i)).
/// Content words exclude the fillers and self-correction phrases a legitimate
/// cleanup is allowed to drop, so removing "um"/"scratch that" does not look
/// like content loss.
/// </summary>
public static class TranscriptSimilarity
{
    // Multi-word phrases removed before tokenizing. Ordered longest-first so a
    // shorter phrase never eats part of a longer one.
    private static readonly string[] Phrases =
    {
        "no let me start over", "let me start over",
        "scratch that", "never mind", "start over", "no wait",
        "you know", "sort of", "kind of",
    };

    private static readonly HashSet<string> FillerWords =
        new(StringComparer.Ordinal) { "um", "uh", "like", "basically", "literally" };

    /// <summary>Lowercase, strip filler/self-correction phrases, split on any
    /// non-alphanumeric run, and drop single-word fillers.</summary>
    public static IReadOnlyList<string> ContentWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        var lower = text.ToLowerInvariant();
        foreach (var p in Phrases)
            lower = lower.Replace(p, " ");

        var result = new List<string>();
        foreach (var tok in Regex.Split(lower, "[^a-z0-9]+"))
        {
            if (tok.Length == 0) continue;
            if (FillerWords.Contains(tok)) continue;
            result.Add(tok);
        }
        return result;
    }

    /// <summary>Fraction of the raw transcript's unique content words that
    /// survive into the cleaned text. 1.0 when the raw has no content words.</summary>
    public static double RetentionRatio(string raw, string cleaned)
    {
        var rawWords = new HashSet<string>(ContentWords(raw), StringComparer.Ordinal);
        if (rawWords.Count == 0) return 1.0;
        var cleanedWords = new HashSet<string>(ContentWords(cleaned), StringComparer.Ordinal);
        var kept = rawWords.Count(w => cleanedWords.Contains(w));
        return (double)kept / rawWords.Count;
    }

    /// <summary>Whitespace-delimited token count of the trimmed text.</summary>
    public static int WordCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows" -class "Winpepper.Cleanup.Tests.TranscriptSimilarityTests"
```
Expected: **PASS** — `Failed: 0, Passed: 6`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Cleanup/TranscriptSimilarity.cs tests/Winpepper.Cleanup.Tests/TranscriptSimilarityTests.cs
git commit -m "$(cat <<'EOF'
feat: add pure content-word similarity helper for cleanup

Content-word tokenizer + retention ratio + word count, excluding fillers and
self-correction phrases. Backing math for the Bug-3 similarity floor.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 3: Restructure the cleanup prompt into system + user roles

Bug 3 root cause (a) continued: `LlamaCleanupBackend` passes ONE templated user
blob (`ApplyTemplate=true`) containing instructions AND examples AND transcript,
with no system role — so the model treats the whole thing as one conversational
turn to continue. Split the assembled prompt into a **system** message
(instructions + hints + OCR) and a **user** message (just the transcript). This
is the clean modular seam the spec calls for (fix-(iv)) and it is exercised by
the fake backend on Linux.

**Files:**
- Modify: `src/Winpepper.Cleanup/PromptBuilder.cs` (replace `Build` with
  `BuildSystem` + `BuildUser`)
- Modify: `src/Winpepper.Cleanup/ILlamaCleanupBackend.cs`
- Modify: `src/Winpepper.Cleanup/LlamaCleanupBackend.cs` *(Windows)*
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs:58-64,71-79`
- Modify: `tests/Winpepper.Cleanup.Tests/Fakes/FakeLlamaCleanupBackend.cs`
- Modify: `tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs` (full rewrite)
- Modify: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (full rewrite —
  system-prompt assertions; all raws lengthened to ≥4 words so the Task-4 bypass
  and Task-5 floor do not perturb them)
- Modify: `tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs`
  *(Windows)*

**Interfaces:**
- Consumes: `BasePrompts.Default`, `TranscriptSimilarity` (unused here),
  `CorrectionsData`.
- Produces:
  - `PromptBuilder.BuildSystem(string basePrompt, CorrectionsData corrections,
    string? windowContext) -> string` (`<BASE-PROMPT>` + optional
    `<CORRECTION-HINTS>` + optional `<OCR-RULES>`/`<WINDOW-OCR-CONTENT>`).
  - `PromptBuilder.BuildUser(string userInput) -> string` (`<USER-INPUT>` block,
    trimmed).
  - `ILlamaCleanupBackend.GenerateAsync(string systemPrompt, string userPrompt,
    int maxNewTokens, float temperature, CancellationToken ct) -> Task<string>`.
  - `FakeLlamaCleanupBackend` exposes `LastSystemPrompt`, `LastUserPrompt`,
    `LastMaxNewTokens`, `CallCount`, `Output`, `Delay`, `Throw`.
  - `CleanupResult.AssembledPrompt` = `system + "\n\n" + user` (unchanged shape
    for history; still contains `<WINDOW-OCR-CONTENT>` when present).

- [ ] **Step 1: Rewrite PromptBuilderTests for the split (failing tests)**

Replace the entire contents of `tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs`
with:

```csharp
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class PromptBuilderTests
{
    private static CorrectionsData Data(IReadOnlyList<string>? preferred = null,
                                        IReadOnlyDictionary<string, string>? replacements = null) =>
        new()
        {
            Preferred = preferred ?? Array.Empty<string>(),
            Replacements = replacements ?? new Dictionary<string, string>(),
        };

    [Fact]
    public void BuildSystem_AllBlocksPresent()
    {
        var sys = PromptBuilder.BuildSystem(
            basePrompt: "BASE",
            corrections: Data(new[] { "ChatGPT" }, new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }),
            windowContext: "WINDOW");

        sys.ShouldContain("<BASE-PROMPT>\nBASE\n</BASE-PROMPT>");
        sys.ShouldContain("<CORRECTION-HINTS>");
        sys.ShouldContain("- ChatGPT");
        sys.ShouldContain("- chat gbt -> ChatGPT");
        sys.ShouldContain("<OCR-RULES>");
        sys.ShouldContain("<WINDOW-OCR-CONTENT>\nWINDOW\n</WINDOW-OCR-CONTENT>");
        // The system prompt must NOT carry the transcript.
        sys.ShouldNotContain("<USER-INPUT>");
    }

    [Fact]
    public void BuildSystem_NoCorrections_OmitsCorrectionHintsBlock()
    {
        PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, "WINDOW")
            .ShouldNotContain("<CORRECTION-HINTS>");
    }

    [Fact]
    public void BuildSystem_NoWindowContext_OmitsOcrBlocks()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, null);
        sys.ShouldNotContain("<OCR-RULES>");
        sys.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void BuildSystem_EmptyWindowContext_OmitsOcrBlocks()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, "   ");
        sys.ShouldNotContain("<OCR-RULES>");
        sys.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public void BuildSystem_PreferredOnly_StillRendersCorrectionHints()
    {
        var sys = PromptBuilder.BuildSystem("BASE", Data(new[] { "ChatGPT" }, replacements: null), null);
        sys.ShouldContain("Preferred transcriptions:");
        sys.ShouldContain("- ChatGPT");
        sys.ShouldNotContain("Misheard replacements:");
    }

    [Fact]
    public void BuildSystem_ReplacementsOnly_StillRendersCorrectionHints()
    {
        var sys = PromptBuilder.BuildSystem("BASE",
            Data(preferred: null, replacements: new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" }), null);
        sys.ShouldNotContain("Preferred transcriptions:");
        sys.ShouldContain("Misheard replacements:");
        sys.ShouldContain("- chat gbt -> ChatGPT");
    }

    [Fact]
    public void BuildSystem_TruncatesWindowContext_To4000Chars()
    {
        var sys = PromptBuilder.BuildSystem("BASE", CorrectionsData.Empty, new string('x', 40_000));
        var start = sys.IndexOf("<WINDOW-OCR-CONTENT>\n", StringComparison.Ordinal) + "<WINDOW-OCR-CONTENT>\n".Length;
        var end = sys.IndexOf("\n</WINDOW-OCR-CONTENT>", StringComparison.Ordinal);
        (end - start).ShouldBeLessThanOrEqualTo(4000);
    }

    [Fact]
    public void BuildUser_WrapsAndTrimsTranscript()
    {
        PromptBuilder.BuildUser("  hello world  ")
            .ShouldBe("<USER-INPUT>\nhello world\n</USER-INPUT>");
    }
}
```

- [ ] **Step 2: Run PromptBuilderTests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `CS0117: 'PromptBuilder' does not contain a
definition for 'BuildSystem'`.

- [ ] **Step 3: Split PromptBuilder into BuildSystem / BuildUser**

Replace the entire contents of `src/Winpepper.Cleanup/PromptBuilder.cs` with:

```csharp
using System.Text;
using Winpepper.Corrections;

namespace Winpepper.Cleanup;

/// <summary>
/// Assembles the cleanup prompt per spec §6.2, split into a system message
/// (instructions + correction hints + OCR context) and a user message (the raw
/// transcript). Bug-3 fix-(iv): the previous single-blob prompt gave the 0.5B
/// model no system role, so it pattern-completed the examples. Pure-string,
/// stateless. Omission rules:
/// - &lt;CORRECTION-HINTS&gt; omitted iff both preferred and replacements are empty.
/// - &lt;OCR-RULES&gt; and &lt;WINDOW-OCR-CONTENT&gt; omitted iff windowContext
///   is null, whitespace, or empty after truncation.
/// - The window-context body is truncated to 4000 chars (spec §6.1 / §6.2).
/// </summary>
public static class PromptBuilder
{
    public const int WindowContextMaxChars = 4000;

    /// <summary>Instructions + optional correction hints + optional OCR context.
    /// Does NOT include the transcript.</summary>
    public static string BuildSystem(
        string basePrompt,
        CorrectionsData corrections,
        string? windowContext)
    {
        var sb = new StringBuilder(capacity: 8192);

        sb.Append("<BASE-PROMPT>\n").Append(basePrompt).Append("\n</BASE-PROMPT>");

        var hasPreferred = corrections.Preferred.Count > 0;
        var hasReplacements = corrections.Replacements.Count > 0;
        if (hasPreferred || hasReplacements)
        {
            sb.Append("\n\n<CORRECTION-HINTS>");
            if (hasPreferred)
            {
                sb.Append("\nPreferred transcriptions:");
                foreach (var p in corrections.Preferred)
                    sb.Append("\n- ").Append(p);
            }
            if (hasReplacements)
            {
                sb.Append("\nMisheard replacements:");
                foreach (var kvp in corrections.Replacements)
                    sb.Append("\n- ").Append(kvp.Key).Append(" -> ").Append(kvp.Value);
            }
            sb.Append("\n</CORRECTION-HINTS>");
        }

        var truncated = TruncateWindowContext(windowContext);
        if (!string.IsNullOrEmpty(truncated))
        {
            sb.Append("\n\n<OCR-RULES>\n")
              .Append("The WINDOW-OCR-CONTENT below is the text currently visible on the user's screen.\n")
              .Append("Use it only to disambiguate names, commands, file paths, and jargon.\n")
              .Append("Prefer the user's spoken words; never substitute OCR text wholesale.")
              .Append("\n</OCR-RULES>");

            sb.Append("\n\n<WINDOW-OCR-CONTENT>\n").Append(truncated).Append("\n</WINDOW-OCR-CONTENT>");
        }

        return sb.ToString();
    }

    /// <summary>The raw transcript, trimmed, wrapped in a USER-INPUT block.</summary>
    public static string BuildUser(string userInput)
        => "<USER-INPUT>\n" + (userInput ?? string.Empty).Trim() + "\n</USER-INPUT>";

    private static string? TruncateWindowContext(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw!.Trim();
        if (trimmed.Length <= WindowContextMaxChars) return trimmed;
        return trimmed.Substring(0, WindowContextMaxChars);
    }
}
```

- [ ] **Step 4: Change the backend interface to take system + user prompts**

Replace the `GenerateAsync` signature in
`src/Winpepper.Cleanup/ILlamaCleanupBackend.cs` with:

```csharp
namespace Winpepper.Cleanup;

/// <summary>
/// Abstraction over the LlamaSharp context so <see cref="CleanupRunner"/> can
/// be unit-tested without loading a real model.
/// </summary>
public interface ILlamaCleanupBackend
{
    /// <summary>
    /// Run the model with a system message (instructions/hints/OCR) and a user
    /// message (the transcript), returning the raw output. The implementation
    /// is responsible for honoring <paramref name="ct"/>.
    /// </summary>
    Task<string> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxNewTokens,
        float temperature,
        CancellationToken ct);
}
```

- [ ] **Step 5: Update the fake backend to record both prompts**

Replace the entire contents of
`tests/Winpepper.Cleanup.Tests/Fakes/FakeLlamaCleanupBackend.cs` with:

```csharp
using Winpepper.Cleanup;

namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>
/// Configurable fake LLamaSharp backend for CleanupRunner unit tests.
/// </summary>
internal sealed class FakeLlamaCleanupBackend : ILlamaCleanupBackend
{
    public string Output { get; init; } = "";
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;
    public Exception? Throw { get; init; }
    public int CallCount { get; private set; }
    public string? LastSystemPrompt { get; private set; }
    public string? LastUserPrompt { get; private set; }
    public int? LastMaxNewTokens { get; private set; }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        int maxNewTokens, float temperature, CancellationToken ct)
    {
        CallCount++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;
        LastMaxNewTokens = maxNewTokens;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Throw is not null) throw Throw;
        return Output;
    }
}
```

- [ ] **Step 6: Wire the split prompts through CleanupRunner**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, replace the block at lines 58-80
(from `// 2) Build the assembled prompt.` through the `chosenPath =
CleanupPath.Llm;` line **and its closing `}` of the `try` block** at line 80)
with the following. The `catch` blocks that follow (lines 81+) are unchanged:

```csharp
        // 2) Build the system (instructions/hints/OCR) and user (transcript)
        //    messages separately. Bug-3 fix-(iv): a proper system role stops the
        //    0.5B model pattern-completing the examples.
        var basePrompt = BasePrompts.ForProfile(options.Profile, options.CustomBasePrompt);
        var systemPrompt = PromptBuilder.BuildSystem(basePrompt, corrections, windowContext);
        var userPrompt = PromptBuilder.BuildUser(rawTranscript);
        var assembled = systemPrompt + "\n\n" + userPrompt;

        // 3) Compute the max-new-tokens budget per spec §5.5.
        var maxTokens = Math.Min(options.MaxNewTokensCap, (int)Math.Ceiling(rawTranscript.Length * 2.0));
        if (maxTokens < 1) maxTokens = 1;

        // 4) Call the backend with a timeout token.
        string raw;
        CleanupPath chosenPath;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(options.Timeout);
            raw = await _backend.GenerateAsync(systemPrompt, userPrompt, maxTokens, options.Temperature, timeoutCts.Token)
                                .ConfigureAwait(false);
            chosenPath = CleanupPath.Llm;
        }
```

(The `catch` blocks below already pass `assembled` to `Finalize`; they are
unchanged.)

- [ ] **Step 7: Update the Windows LlamaSharp backend to use a ChatML system role**

Replace the `GenerateAsync` method body in
`src/Winpepper.Cleanup/LlamaCleanupBackend.cs` (lines 53-95) with:

```csharp
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt,
        int maxNewTokens, float temperature, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Bug-3 fix-(iv): hand the model a real ChatML system turn
            // (instructions/examples) separate from the user turn (transcript),
            // so it cleans the transcript instead of continuing the few-shot
            // block. We build ChatML ourselves (Qwen2.5's template) and disable
            // StatelessExecutor.ApplyTemplate, which only knows how to wrap a
            // single user message.
            var templated =
                "<|im_start|>system\n" + systemPrompt + "<|im_end|>\n" +
                "<|im_start|>user\n" + userPrompt + "<|im_end|>\n" +
                "<|im_start|>assistant\n";

            var executor = new StatelessExecutor(_weights, _params, _log)
            {
                ApplyTemplate = false,
            };
            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxNewTokens,
                AntiPrompts = new List<string> { "<|im_end|>", "</USER-INPUT>", "<USER-INPUT>", "<BASE-PROMPT>" },
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    TopP = 0.95f,
                    TopK = 40,
                },
            };

            var sb = new StringBuilder();
            await foreach (var token in executor.InferAsync(templated, inferenceParams, ct).ConfigureAwait(false))
            {
                sb.Append(token);
                if (sb.Length > maxNewTokens * 8) break; // hard char cap as belt-and-braces
            }
            return sb.ToString();
        }
        finally
        {
            _gate.Release();
        }
    }
```

Then update `WarmAsync` (line 44) to call the new two-arg signature:

```csharp
            await GenerateAsync("You are a helpful assistant.", "Hello.",
                maxNewTokens: 4, temperature: 0.1f, ct).ConfigureAwait(false);
```

- [ ] **Step 8: Update the Windows integration test to the new signature**

In `tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs`,
replace the `GenerateAsync` call (lines 25-29) with:

```csharp
        var result = await backend.GenerateAsync(
            systemPrompt: "You repeat the user's sentence back exactly.",
            userPrompt: "Hello, world.",
            maxNewTokens: 32,
            temperature: 0.1f,
            ct: CancellationToken.None);
```

- [ ] **Step 9: Rewrite CleanupRunnerTests for the split + safe (≥4-word) raws**

Replace the entire contents of
`tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` with:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Cleanup.Tests.Fakes;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CleanupRunnerTests
{
    private static CleanupRunner NewRunner(ILlamaCleanupBackend backend) =>
        new(backend, new NullLogger<CleanupRunner>());

    private static CleanupOptions DefaultOptions() => new()
    {
        Profile = CleanupProfile.Ordinary,
        Timeout = TimeSpan.FromSeconds(1),
        WindowContextEnabled = false,
        WindowContextWait = TimeSpan.FromMilliseconds(50),
    };

    // NOTE: every raw transcript here is >= 4 words so the short-transcript
    // bypass (Task 4) does not fire, and outputs share content with the raw so
    // the similarity floor (Task 5) does not fire — these tests isolate the
    // pre-existing LLM/fallback behavior.

    [Fact]
    public async Task Run_LlmEchoesPromptScaffold_FallsBackToRawTranscript()
    {
        var garbage = "<OUTPUT>\nI think we should just ship it tomorrow.\n</OUTPUT>Human: I think we should just ship it tomorrow.";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = garbage });
        var result = await runner.RunAsync("Hello, my name is Crispy. How do you do?",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("Hello, my name is Crispy. How do you do?");
    }

    [Fact]
    public async Task Run_LlmOutputImplausiblyLong_FallsBackToRawTranscript()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = new string('x', 500) });
        var result = await runner.RunAsync("short utterance here now please",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("short utterance here now please");
    }

    [Fact]
    public async Task Run_MarkerSpokenByUser_IsNotRejected()
    {
        // A user who actually dictated "Output:" must not trip the echo guard.
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Output: forty-two." });
        var result = await runner.RunAsync("output colon forty two",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Output: forty-two.");
    }

    [Fact]
    public void LooksLikePromptEcho_ChatTemplateMarkers_Detected()
    {
        CleanupRunner.LooksLikePromptEcho("<|im_start|>assistant hi", "anything").ShouldBeTrue();
        CleanupRunner.LooksLikePromptEcho("### Response: hi", "anything").ShouldBeTrue();
        CleanupRunner.LooksLikePromptEcho("Plain cleaned sentence.", "anything").ShouldBeFalse();
    }

    [Fact]
    public async Task Run_LlmReturnsCleanText_UsesLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Hello world." });
        var result = await runner.RunAsync("um hello there world",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsThinkBlock_StripsItBeforeUsingOutput()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "<think>reasoning</think>Hello world.",
        });
        var result = await runner.RunAsync("hello world okay then",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsEmpty_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "" });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("we tested chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT");
        result.Path.ShouldBe(CleanupPath.FallbackEmpty);
    }

    [Fact]
    public async Task Run_LlmReturnsEllipsis_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "..." });
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("hello world okay then");
        result.Path.ShouldBe(CleanupPath.FallbackEllipsis);
    }

    [Fact]
    public async Task Run_LlmExceedsTimeout_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Delay = TimeSpan.FromSeconds(5),
            Output = "unused",
        });
        var opts = DefaultOptions() with { Timeout = TimeSpan.FromMilliseconds(50) };
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, opts, CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackTimeout);
        result.CleanedText.ShouldBe("hello world okay then");
    }

    [Fact]
    public async Task Run_BackendThrows_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Throw = new InvalidOperationException("kaboom") });
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("hello world okay then");
    }

    [Fact]
    public async Task Run_AppliesCorrectionPostPass_OnLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "we tested chat gbt." });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("we tested chat gbt today", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_MaxNewTokens_FollowsSpecFormula()
    {
        // Spec §5.5: max_new_tokens = min(2048, ceil(transcript_chars * 2.0)).
        var backend = new FakeLlamaCleanupBackend { Output = "x" };
        var runner = NewRunner(backend);

        var raw124 = string.Join(" ", Enumerable.Repeat("word", 25)); // 25*4 + 24 spaces = 124 chars
        await runner.RunAsync(raw124, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe((int)System.Math.Ceiling(raw124.Length * 2.0)); // 248

        var rawLong = string.Join(" ", Enumerable.Repeat("word", 1250)); // > 2048 tokens by formula
        await runner.RunAsync(rawLong, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task Run_AwaitsWindowContext_UpTo50msThenProceeds()
    {
        var tcs = new TaskCompletionSource<string?>();
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(50),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, tcs.Task, opts, CancellationToken.None);
        sw.Stop();

        result.CleanedText.ShouldBe("cleaned");
        sw.ElapsedMilliseconds.ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Run_UsesWindowContext_WhenReadyInTime()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };
        await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastSystemPrompt.ShouldNotBeNull();
        backend.LastSystemPrompt!.ShouldContain("the foreground window says hello");
    }

    [Fact]
    public async Task Run_WindowContextDisabled_OmitsItEvenWhenTaskCompletes()
    {
        var ready = Task.FromResult<string?>("ignored");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { WindowContextEnabled = false };
        await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastSystemPrompt.ShouldNotBeNull();
        backend.LastSystemPrompt!.ShouldNotContain("ignored");
        backend.LastSystemPrompt!.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }
}
```

- [ ] **Step 10: Build and run the full Cleanup suite (Linux)**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: **PASS** — `Failed: 0`. All `PromptBuilderTests`,
`CleanupRunnerTests`, `BasePromptsTests`, `TranscriptSimilarityTests` green. The
Windows integration test is excluded by `-notrait "Platform=Windows"` (it is
compile-verified on Windows in the smoke pass).

- [ ] **Step 11: Commit**

```bash
git add src/Winpepper.Cleanup/PromptBuilder.cs src/Winpepper.Cleanup/ILlamaCleanupBackend.cs \
        src/Winpepper.Cleanup/LlamaCleanupBackend.cs src/Winpepper.Cleanup/CleanupRunner.cs \
        tests/Winpepper.Cleanup.Tests/Fakes/FakeLlamaCleanupBackend.cs \
        tests/Winpepper.Cleanup.Tests/PromptBuilderTests.cs \
        tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs \
        tests/Winpepper.Cleanup.Tests/LlamaCleanupBackendIntegrationTests.cs
git commit -m "$(cat <<'EOF'
refactor: split cleanup prompt into system + user roles

Bug-3(a): the cleanup prompt was one templated user blob with no system role,
so the 0.5B model pattern-completed the examples. Split into a system message
(instructions/hints/OCR) and a user message (transcript); backend builds ChatML
with a real system turn.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 4: Short-transcript bypass

Bug 3 root cause (c): nothing gates on length, so a 1-3 word utterance is fed to
the LLM, which is exactly where it hallucinates a whole sentence. Raw
transcripts under 4 words (after trimming) skip the LLM entirely and take the
deterministic correction-only path (spec fix-(iii)).

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupResult.cs` (add `CleanupPath.BypassShort`)
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs` (early bypass in `RunAsync`)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (append tests)

**Interfaces:**
- Consumes: `TranscriptSimilarity.WordCount`.
- Produces: `CleanupPath.BypassShort`; `RunAsync` returns it (with the raw
  transcript run through the deterministic correction post-pass) when
  `WordCount(rawTranscript) < 4`.

- [ ] **Step 1: Append the failing bypass tests**

Append these `[Fact]`s inside the `CleanupRunnerTests` class (before its closing
brace) in `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs`:

```csharp
    [Fact]
    public async Task Run_ShortTranscript_BypassesLlm_AndKeepsRaw()
    {
        // Bug-3(c): "Right." must never become the model's ship-it example.
        var backend = new FakeLlamaCleanupBackend { Output = "I think we should just ship it tomorrow." };
        var runner = NewRunner(backend);
        var result = await runner.RunAsync("Right.", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassShort);
        result.CleanedText.ShouldBe("Right.");
        backend.CallCount.ShouldBe(0); // LLM never called
    }

    [Fact]
    public async Task Run_ShortTranscript_StillAppliesCorrectionPostPass()
    {
        var backend = new FakeLlamaCleanupBackend { Output = "ignored" };
        var runner = NewRunner(backend);
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassShort);
        result.CleanedText.ShouldBe("ChatGPT");
        backend.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Run_FourWords_IsNotBypassed()
    {
        var backend = new FakeLlamaCleanupBackend { Output = "Clean up this sentence." };
        var runner = NewRunner(backend);
        var result = await runner.RunAsync("clean up this sentence", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        backend.CallCount.ShouldBe(1);
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `CS0117: 'CleanupPath' does not contain a
definition for 'BypassShort'`.

- [ ] **Step 3: Add the CleanupPath value**

In `src/Winpepper.Cleanup/CleanupResult.cs`, add the new enum member to
`CleanupPath` (after `FallbackImplausible`):

```csharp
    FallbackImplausible,  // The LLM output echoed prompt scaffolding or blew past plausible length.
    BypassShort,          // Raw transcript under 4 words; LLM skipped, deterministic path taken.
```

- [ ] **Step 4: Add the bypass at the top of RunAsync**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, immediately after
`var sw = Stopwatch.StartNew();` (line 31), insert:

```csharp

        // 0) Short-transcript bypass (spec fix-(iii)). A 0.5B model has nothing
        //    useful to do with a 1-3 word utterance and is where it most often
        //    hallucinates a whole sentence; skip it and take the deterministic
        //    correction-only path.
        if (TranscriptSimilarity.WordCount(rawTranscript) < 4)
        {
            _log.LogDebug("Transcript has fewer than 4 words; bypassing LLM cleanup");
            return Finalize(rawTranscript, "", corrections, assembledPrompt: "", CleanupPath.BypassShort, sw);
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows" -class "Winpepper.Cleanup.Tests.CleanupRunnerTests"
```
Expected: **PASS** — `Failed: 0`. The three new bypass tests pass and the
existing runner tests (all ≥4-word raws) are unaffected.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupResult.cs src/Winpepper.Cleanup/CleanupRunner.cs \
        tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "$(cat <<'EOF'
fix: bypass LLM cleanup for transcripts under 4 words

Bug-3(c): short utterances are where the 0.5B model hallucinates a whole
sentence. Under 4 words we skip the model and take the deterministic
correction-only path (new CleanupPath.BypassShort).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 5: Content-similarity floor + known-example echo guard

Bug 3 root cause (b): the plausibility guard checks only scaffold markers and
max length — no similarity floor, so wholesale replacement and severe truncation
pass. Add a content-word retention floor and a known-example echo check to the
existing step 6.5 (spec fix-(i)/(ii)). All rejections route to the existing
`CleanupPath.FallbackImplausible` (deterministic raw transcript).

**Files:**
- Modify: `src/Winpepper.Cleanup/CleanupRunner.cs:117-122` (extend step 6.5)
- Test: `tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs` (append regression
  tests)

**Interfaces:**
- Consumes: `TranscriptSimilarity.{RetentionRatio,ContentWords,WordCount}`,
  `BasePrompts.DefaultExampleOutputs`.
- Produces: no new public surface; adds a private
  `MatchesKnownExample(string) -> bool` and `Normalize(string) -> string` to
  `CleanupRunner`.

- [ ] **Step 1: Append the failing regression tests**

Append these `[Fact]`s inside `CleanupRunnerTests` (before its closing brace):

```csharp
    [Fact]
    public async Task Run_WholesaleTruncation_RejectedToFallback()
    {
        // Live case: long question wholesale-replaced by "Me".
        var raw = "Who should be fixing this? Me or the person configuring RunPod?";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Me" });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe(raw);
    }

    [Fact]
    public async Task Run_LegitimateFillerRemoval_IsAccepted()
    {
        // High overlap -> a real cleanup, even though the output equals a
        // former example. Similarity beats blacklisting.
        var raw = "um so like I think we should basically just ship it tomorrow you know";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "I think we should just ship it tomorrow." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("I think we should just ship it tomorrow.");
    }

    [Fact]
    public async Task Run_LegitimateSelfCorrection_IsAccepted()
    {
        // Output matches the retained example, but overlap with raw is high.
        var raw = "write me a function called add_numbers no wait scratch that call it sum";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Write me a function called sum." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Write me a function called sum.");
    }

    [Fact]
    public async Task Run_KnownExampleEcho_WithLowOverlap_IsRejected()
    {
        // Bare few-shot echo, no scaffold markers. 5 words (not >6, so the
        // truncation rule does not apply) with a single shared content word
        // ("sum") so retention is 0.2 (>0, not wholesale) — this must be caught
        // specifically by the known-example guard.
        var raw = "call sum here now please";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Write me a function called sum." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe(raw);
    }

    [Fact]
    public async Task Run_HeavyFillerInput_IsNotFalselyRejected()
    {
        var raw = "um uh like you know basically I really think this is good";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "I really think this is good." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("I really think this is good.");
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows" \
    -method "Winpepper.Cleanup.Tests.CleanupRunnerTests.Run_WholesaleTruncation_RejectedToFallback"
```
Expected: **FAIL** — the runner currently accepts `"Me"` (path `Llm`), so the
assertion `Path == FallbackImplausible` fails.

- [ ] **Step 3: Extend step 6.5 with the similarity floor + echo guard**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, immediately after the
implausibly-long check block (the `if (sanitized.Length > rawTranscript.Length *
2 + 64)` block ending at line 122), insert:

```csharp

        // 6.5b) Content-similarity floor (spec fix-(i)/(ii)). A legitimate
        // cleanup only drops fillers and adds punctuation, so it retains most of
        // the raw transcript's content words. Reject wholesale replacement
        // (near-zero overlap) and severe truncation, and reject any output that
        // matches a known few-shot example verbatim while sharing little with
        // what the user actually said.
        var retention = TranscriptSimilarity.RetentionRatio(rawTranscript, sanitized);
        var rawContentCount = TranscriptSimilarity.ContentWords(rawTranscript).Count;
        var rawWordCount = TranscriptSimilarity.WordCount(rawTranscript);

        if (rawContentCount >= 1 && retention <= 0.0)
        {
            _log.LogWarning("Cleanup output shares no content words with the transcript (wholesale replacement); falling back");
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (rawWordCount > 6 && retention < 0.40)
        {
            _log.LogWarning("Cleanup output retains only {Retention:P0} of a {Words}-word transcript (severe truncation); falling back",
                retention, rawWordCount);
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
        if (retention < 0.40 && MatchesKnownExample(sanitized))
        {
            _log.LogWarning("Cleanup output matches a known few-shot example with low transcript overlap; falling back");
            return Finalize(rawTranscript, raw, corrections, assembled, CleanupPath.FallbackImplausible, sw);
        }
```

- [ ] **Step 4: Add the known-example helpers**

In `src/Winpepper.Cleanup/CleanupRunner.cs`, add these private members (e.g.
just below `LooksLikePromptEcho`):

```csharp
    // Normalize to letters/digits only so punctuation/whitespace/case differences
    // between the model output and a stored example don't hide an echo.
    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool MatchesKnownExample(string cleaned)
    {
        var norm = Normalize(cleaned);
        if (norm.Length == 0) return false;
        foreach (var example in BasePrompts.DefaultExampleOutputs)
            if (Normalize(example) == norm) return true;
        return false;
    }
```

- [ ] **Step 5: Run the full Cleanup suite to verify pass + no regressions**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Cleanup.Tests/Winpepper.Cleanup.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Cleanup.Tests/bin/Debug/net9.0/Winpepper.Cleanup.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: **PASS** — `Failed: 0`. The five new regression tests pass; all prior
`CleanupRunnerTests` still pass. Note on why they survive the new floor: this
depends on Task 3 having already rewritten their raw transcripts to ≥4 words with
overlapping outputs (Task 3 runs before this task). Most rewritten raws are ≤6
words or keep ≥0.40 content-word retention, so the floor does not change their
outcome. A couple of tests (e.g. `Run_MaxNewTokens`, whose raw is long and cleaned
output is a single char) DO internally trip the floor and return
`FallbackImplausible` — they still pass only because they assert a pre-floor value
(`LastMaxNewTokens`), not `Path`/`CleanedText`. Do NOT add `Path`/`CleanedText`
assertions to those raws without re-checking the floor.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Cleanup/CleanupRunner.cs tests/Winpepper.Cleanup.Tests/CleanupRunnerTests.cs
git commit -m "$(cat <<'EOF'
fix: reject wholesale replacement and known-example echoes in cleanup

Bug-3(b): the plausibility guard only checked scaffold markers and length. Add
a content-word retention floor (reject near-zero overlap and >40% truncation on
>6-word transcripts) and a verbatim known-example echo check, both routing to
the deterministic FallbackImplausible path.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 6: Warm-capture pre-roll ring buffer (pure)

Bug 2 root cause: capture is cold-started on hotkey press, losing ~100-300 ms of
speech. The fix is a persistent warm capture feeding a rolling pre-buffer. Build
and test the pure bookkeeping first: a bounded ring that always holds the last N
samples, seeds a session buffer from that ring on start, appends live frames
while active, and returns the full buffer on stop.

**Files:**
- Create: `src/Winpepper.Audio/WarmCaptureBuffer.cs`
- Test: `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs`

**Interfaces:**
- Produces:
  - `new WarmCaptureBuffer(int ringCapacitySamples)`.
  - `void Ingest(ReadOnlySpan<float> frame)` — always feeds the ring (trimming
    to capacity); also appends to the session buffer while active.
  - `void StartSession(int prerollSamples)` — clears the session buffer, seeds
    it with up to `prerollSamples` most-recent ring samples, marks active.
  - `float[] StopSession()` — marks inactive, returns and clears the session
    buffer.
  - `bool IsSessionActive { get; }`.
  Task 8 (`WarmWasapiRecorder`) consumes all of these.

- [ ] **Step 1: Write the failing tests**

Create `tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs`:

```csharp
using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class WarmCaptureBufferTests
{
    private static float[] Ramp(int from, int count)
    {
        var a = new float[count];
        for (var i = 0; i < count; i++) a[i] = from + i;
        return a;
    }

    [Fact]
    public void Ingest_TrimsOldestBeyondCapacity_PrerollTakesMostRecent()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 15)); // 0..14; only last 10 (5..14) survive

        buf.StartSession(prerollSamples: 10);
        var session = buf.StopSession();

        session.ShouldBe(Ramp(5, 10)); // 5..14
    }

    [Fact]
    public void StartSession_SeedsPreroll_ThenAppendsLiveFrames()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2, 3 });

        buf.StartSession(prerollSamples: 100); // takes all available (3)
        buf.Ingest(new float[] { 4, 5 });
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void StartSession_PrerollLargerThanAvailable_TakesWhatExists()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2 });

        buf.StartSession(prerollSamples: 100);
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 1, 2 });
    }

    [Fact]
    public void Ingest_WhileInactive_DoesNotAppendToSession()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1 });          // inactive, ring only
        buf.StartSession(prerollSamples: 0);    // no preroll
        buf.Ingest(new float[] { 2 });          // active -> session
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 2 });
    }

    [Fact]
    public void SecondSession_ResetsSessionBuffer()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[] { 1, 2 });
        buf.StopSession().ShouldBe(new float[] { 1, 2 });

        buf.StartSession(0);
        buf.Ingest(new float[] { 9 });
        buf.StopSession().ShouldBe(new float[] { 9 });
    }

    [Fact]
    public void IsSessionActive_TracksStartAndStop()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.IsSessionActive.ShouldBeFalse();
        buf.StartSession(0);
        buf.IsSessionActive.ShouldBeTrue();
        buf.StopSession();
        buf.IsSessionActive.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `CS0246: The type or namespace name
'WarmCaptureBuffer' could not be found`.

- [ ] **Step 3: Implement WarmCaptureBuffer**

Create `src/Winpepper.Audio/WarmCaptureBuffer.cs` (no `#if WINDOWS` — pure
managed, Linux-tested):

```csharp
namespace Winpepper.Audio;

/// <summary>
/// Pure pre-roll bookkeeping for warm capture (Bug 2). A single audio callback
/// continuously <see cref="Ingest"/>s frames; the ring always holds the last
/// <c>ringCapacitySamples</c> samples. On <see cref="StartSession"/> the session
/// buffer is seeded from the ring (the pre-roll) and thereafter live frames are
/// appended too, so the returned buffer includes the ~500 ms spoken just before
/// the hotkey press. Thread-safe: the WASAPI callback and the hotkey thread call
/// in concurrently.
/// </summary>
public sealed class WarmCaptureBuffer
{
    private readonly int _ringCapacity;
    private readonly List<float> _ring;
    private readonly List<float> _session = new();
    private bool _active;
    private readonly object _lock = new();

    public WarmCaptureBuffer(int ringCapacitySamples)
    {
        if (ringCapacitySamples < 0) ringCapacitySamples = 0;
        _ringCapacity = ringCapacitySamples;
        _ring = new List<float>(ringCapacitySamples + 1);
    }

    public bool IsSessionActive
    {
        get { lock (_lock) { return _active; } }
    }

    public void Ingest(ReadOnlySpan<float> frame)
    {
        lock (_lock)
        {
            foreach (var s in frame) _ring.Add(s);
            if (_ring.Count > _ringCapacity)
                _ring.RemoveRange(0, _ring.Count - _ringCapacity);

            if (_active)
                foreach (var s in frame) _session.Add(s);
        }
    }

    public void StartSession(int prerollSamples)
    {
        if (prerollSamples < 0) prerollSamples = 0;
        lock (_lock)
        {
            _session.Clear();
            var take = Math.Min(prerollSamples, _ring.Count);
            if (take > 0)
                _session.AddRange(_ring.GetRange(_ring.Count - take, take));
            _active = true;
        }
    }

    public float[] StopSession()
    {
        lock (_lock)
        {
            _active = false;
            var result = _session.ToArray();
            _session.Clear();
            return result;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Audio.Tests/Winpepper.Audio.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: **PASS** — `Failed: 0`. The `WasapiRecorderIntegrationTests` are
excluded by the trait filter.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/WarmCaptureBuffer.cs tests/Winpepper.Audio.Tests/WarmCaptureBufferTests.cs
git commit -m "$(cat <<'EOF'
feat: add pure pre-roll ring buffer for warm audio capture

Bug-2: rolling buffer of the last N samples that seeds a session buffer on
start and appends live frames, so start-of-speech is not clipped. Pure managed
and unit-tested; the WASAPI wiring lands next.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 7: PrewarmMicEnabled setting + view-model toggle

The warm stream keeps the mic always on (the mic-in-use indicator stays lit).
Make it a setting so privacy-sensitive users can opt back into cold-start
(spec fix-2(a)). Default true.

**Files:**
- Modify: `src/Winpepper.Core/Settings/AppSettings.cs`
- Modify: `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`
- Test: `tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`

**Interfaces:**
- Produces:
  - `AppSettings.PrewarmMicEnabled { get; init; } = true`.
  - `RecordingSettingsViewModel.PrewarmMicEnabled { get; set; }` (commits durably
    like the other toggles). Task 9 (`AppShell`) reads `settings.PrewarmMicEnabled`.

- [ ] **Step 1: Write the failing view-model test**

Append this `[Fact]` to the `RecordingSettingsViewModelTests` class (before its
closing brace) in
`tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs`. It
mirrors the existing `PostPasteLearning_Defaults_Off_And_Commits_Durably` test
and reuses the file's existing `FakeWriter` (`ISettingsWriter` double exposing
`Current`, `WriteCount`, `FlushCount`):

```csharp
    [Fact]
    public void PrewarmMic_Defaults_On_And_Commits_Durably()
    {
        var w = new FakeWriter();
        var vm = new RecordingSettingsViewModel(new AppSettings(), w);
        vm.PrewarmMicEnabled.ShouldBeTrue(); // default on

        vm.PrewarmMicEnabled = false;

        w.Current.PrewarmMicEnabled.ShouldBeFalse();
        w.WriteCount.ShouldBe(1);
        w.FlushCount.ShouldBe(1);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -5
```
Expected: **build FAILS** — `'RecordingSettingsViewModel' does not contain a
definition for 'PrewarmMicEnabled'` (and/or `AppSettings` likewise).

- [ ] **Step 3: Add the setting to AppSettings**

In `src/Winpepper.Core/Settings/AppSettings.cs`, add after the
`PostPasteLearningEnabled` line (line 35):

```csharp
    // Warm-mic pre-roll: keep one capture stream running so the first ~500 ms
    // of speech is not clipped (Bug 2). On by default; turning it off restores
    // cold-start capture (the mic-in-use indicator then only lights while
    // dictating — a privacy trade-off).
    public bool PrewarmMicEnabled { get; init; } = true;
```

- [ ] **Step 4: Add the view-model property**

In `src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs`, add the
backing field after `_postPasteLearningEnabled` (line 24):

```csharp
    private bool _prewarmMicEnabled;
```

seed it in the constructor after line 37 (`_postPasteLearningEnabled = ...`):

```csharp
        _prewarmMicEnabled = initial.PrewarmMicEnabled;
```

and add the property after the `PostPasteLearningEnabled` property (line 126):

```csharp
    public bool PrewarmMicEnabled
    {
        get => _prewarmMicEnabled;
        set
        {
            if (_prewarmMicEnabled == value) return;
            _prewarmMicEnabled = value;
            CommitDurable(s => s with { PrewarmMicEnabled = value });
            Raise(nameof(PrewarmMicEnabled));
        }
    }
```

- [ ] **Step 5: Run the test to verify it passes**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Core.Tests/Winpepper.Core.Tests.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Debug/net9.0/Winpepper.Core.Tests.dll \
    -notrait "Platform=Windows" \
    -class "Winpepper.Core.Tests.ViewModels.RecordingSettingsViewModelTests"
```
Expected: **PASS** — `Failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Settings/AppSettings.cs \
        src/Winpepper.Core/ViewModels/RecordingSettingsViewModel.cs \
        tests/Winpepper.Core.Tests/ViewModels/RecordingSettingsViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat: add PrewarmMicEnabled setting (default on) for warm capture

Bug-2(a): the warm stream keeps the mic always on. Expose a durable setting so
users can opt back into cold-start; documents the privacy trade-off.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 8: WarmWasapiRecorder (Windows WASAPI wiring)

Wrap `WarmCaptureBuffer` in a persistent `WasapiCapture`. One capture runs for
the app lifetime (when pre-warm is on), continuously ingesting frames; a session
seeds the pre-roll and returns pre-roll + live audio on stop. When pre-warm is
off, the capture is started lazily on session start and torn down on stop
(cold-start behavior, no pre-roll). This file is `#if WINDOWS` — Linux-untestable
— so it is kept thin over the tested `WarmCaptureBuffer` and verified in the
Windows smoke checklist.

**Files:**
- Create: `src/Winpepper.Audio/IWarmAudioRecorder.cs` (pure interface, no `#if`)
- Create: `src/Winpepper.Audio/WarmWasapiRecorder.cs` (`#if WINDOWS`)

**Interfaces:**
- Consumes: `WarmCaptureBuffer`, NAudio `WasapiCapture`/`MMDeviceEnumerator`.
- Produces:
  - `interface IWarmAudioRecorder : IDisposable` with
    `event Action<ReadOnlyMemory<float>>? FramesAvailable`,
    `void StartSession(int includePrerollMs)`, `float[] StopSession()`.
  - `WarmWasapiRecorder(bool prewarm, string? deviceId = null)` implementing it.
  Task 9 (`PipelineHost`) consumes `IWarmAudioRecorder`.

- [ ] **Step 1: Create the interface (pure, builds on Linux)**

Create `src/Winpepper.Audio/IWarmAudioRecorder.cs`:

```csharp
namespace Winpepper.Audio;

/// <summary>
/// A capture that can start a dictation session with a pre-roll of audio that
/// was already flowing before the session began (Bug 2). Frames are raised only
/// while a session is active, so the voice meter is quiet at idle.
/// </summary>
public interface IWarmAudioRecorder : IDisposable
{
    /// <summary>Raised (mono 16 kHz frames) only while a session is active.</summary>
    event Action<ReadOnlyMemory<float>>? FramesAvailable;

    /// <summary>Begin a session, seeding up to <paramref name="includePrerollMs"/>
    /// milliseconds of already-captured audio.</summary>
    void StartSession(int includePrerollMs);

    /// <summary>End the session and return pre-roll + live audio (mono 16 kHz).</summary>
    float[] StopSession();
}
```

- [ ] **Step 2: Verify the interface compiles on Linux**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Audio/Winpepper.Audio.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true 2>&1 | tail -3
```
Expected: build succeeds (the interface uses only `System` types; the
`WarmWasapiRecorder` added next is `#if WINDOWS` and excluded on Linux).

- [ ] **Step 3: Create WarmWasapiRecorder (Windows-only)**

Create `src/Winpepper.Audio/WarmWasapiRecorder.cs`. The frame-conversion body
(float extraction, downmix, 16 kHz resample) mirrors the proven
`WasapiRecorder.OnData` conversion (kept independent so the working cold recorder
is not disturbed):

```csharp
#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Winpepper.Audio;

/// <summary>
/// Warm capture (Bug 2). When <c>prewarm</c> is true a single WasapiCapture runs
/// for the app lifetime, feeding a <see cref="WarmCaptureBuffer"/> so a session
/// includes the ~500 ms spoken just before the hotkey press. When false, capture
/// is started lazily on <see cref="StartSession"/> and stopped on
/// <see cref="StopSession"/> (cold-start, no pre-roll). On a device change or
/// capture fault the stream is disposed and lazily recreated on next use.
///
/// This file is #if WINDOWS and cannot be built or run on Linux; the sample
/// bookkeeping it delegates to (WarmCaptureBuffer) is unit-tested, and this thin
/// wiring is verified in the Windows smoke checklist.
/// </summary>
public sealed class WarmWasapiRecorder : IWarmAudioRecorder
{
    private const int SampleRate16k = 16000;
    // Ring holds ~1 s so a 500 ms pre-roll always has enough history.
    private const int RingCapacitySamples = SampleRate16k;

    public event Action<ReadOnlyMemory<float>>? FramesAvailable;

    private readonly bool _prewarm;
    private readonly string? _deviceId;
    private readonly WarmCaptureBuffer _buffer = new(RingCapacitySamples);
    private readonly object _captureLock = new();
    private WasapiCapture? _capture;
    private string? _activeDeviceId; // endpoint the live _capture was built on (Bug-2 default-change recheck)

    public WarmWasapiRecorder(bool prewarm, string? deviceId = null)
    {
        _prewarm = prewarm;
        _deviceId = deviceId;
        if (_prewarm) TryStartCapture();
    }

    public void StartSession(int includePrerollMs)
    {
        // Bug-2 default-device change: a persistent warm WasapiCapture does NOT
        // auto-follow a change of the Windows default input device -- WASAPI does
        // not signal a running capture, so without this check the pill would keep
        // recording the OLD mic. That is a regression vs the previous per-press
        // cold-start, which re-resolved the default endpoint on every press. When
        // we are following the default (no explicit _deviceId), re-resolve it here
        // and rebuild the stream if it drifted. (A fuller solution is an
        // IMMNotificationClient via RegisterEndpointNotificationCallback reacting to
        // OnDefaultDeviceChanged; this per-session recheck is the minimal fix that
        // restores parity and covers the "change default, then dictate" case.)
        if (string.IsNullOrEmpty(_deviceId)) RebuildIfDefaultChanged();
        // Cold mode (or a previously faulted warm stream): (re)start capture now.
        if (_capture is null) TryStartCapture();
        var prerollSamples = _prewarm ? Math.Max(0, includePrerollMs) * (SampleRate16k / 1000) : 0;
        _buffer.StartSession(prerollSamples);
    }

    private void RebuildIfDefaultChanged()
    {
        lock (_captureLock)
        {
            if (_capture is null) return; // nothing running; TryStartCapture will pick the current default
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var current = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                if (current.ID != _activeDeviceId)
                {
                    // Default moved. _captureLock is a reentrant Monitor, so calling
                    // StopCapture()/TryStartCapture() (which re-take it) is safe here.
                    StopCapture();     // drop the old-device stream
                    TryStartCapture(); // rebuild on the new default
                }
            }
            catch
            {
                // Enumeration failed (e.g. no capture device). Keep the current
                // stream; the fault path / next StartSession retries.
            }
        }
    }

    public float[] StopSession()
    {
        var samples = _buffer.StopSession();
        if (!_prewarm) StopCapture(); // cold mode tears the stream down between sessions
        return samples;
    }

    private void TryStartCapture()
    {
        lock (_captureLock)
        {
            if (_capture is not null) return;
            try
            {
                var enumerator = new MMDeviceEnumerator();
                var device = string.IsNullOrEmpty(_deviceId)
                    ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                    : enumerator.GetDevice(_deviceId);

                var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 50);
                capture.DataAvailable += OnData;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();
                _capture = capture;
                _activeDeviceId = device.ID; // remember the endpoint for the default-change recheck
            }
            catch
            {
                // Device unavailable (e.g. unplugged). Leave _capture null; the
                // next StartSession retries. Warm mode simply yields no pre-roll
                // until the device returns.
                _capture = null;
            }
        }
    }

    private void StopCapture()
    {
        lock (_captureLock)
        {
            if (_capture is null) return;
            try { _capture.StopRecording(); } catch { }
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.Dispose(); } catch { }
            _capture = null;
            _activeDeviceId = null;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // Fault (device change/removal): drop the stream so TryStartCapture
        // rebuilds it on next use.
        if (e.Exception is not null) StopCapture();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var capture = _capture;
        if (capture is null) return;
        var fmt = capture.WaveFormat;

        var sampleCount = e.BytesRecorded / (fmt.BitsPerSample / 8);
        var samples = new float[sampleCount];

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            for (var i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        }
        else
        {
            return;
        }

        float[] mono;
        if (fmt.Channels > 1)
        {
            mono = new float[sampleCount / fmt.Channels];
            for (var i = 0; i < mono.Length; i++)
            {
                float sum = 0;
                for (var c = 0; c < fmt.Channels; c++) sum += samples[i * fmt.Channels + c];
                mono[i] = sum / fmt.Channels;
            }
        }
        else
        {
            mono = samples;
        }

        if (fmt.SampleRate != SampleRate16k)
        {
            var sourceFormat = WaveFormat.CreateIeeeFloatWaveFormat(fmt.SampleRate, 1);
            var bytes = new byte[mono.Length * 4];
            Buffer.BlockCopy(mono, 0, bytes, 0, bytes.Length);
            var sourceProvider = new RawSourceWaveStream(new MemoryStream(bytes), sourceFormat);
            var resampler = new MediaFoundationResampler(sourceProvider,
                WaveFormat.CreateIeeeFloatWaveFormat(SampleRate16k, 1)) { ResamplerQuality = 60 };
            var resampled = new List<float>();
            var byteBuf = new byte[8192];
            int read;
            while ((read = resampler.Read(byteBuf, 0, byteBuf.Length)) > 0)
            {
                var floats = new float[read / 4];
                Buffer.BlockCopy(byteBuf, 0, floats, 0, read);
                resampled.AddRange(floats);
            }
            mono = resampled.ToArray();
        }

        _buffer.Ingest(mono);
        if (_buffer.IsSessionActive)
            FramesAvailable?.Invoke(mono);
    }

    public void Dispose() => StopCapture();
}
#endif
```

- [ ] **Step 4: Verify Linux build is unaffected**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build src/Winpepper.Audio/Winpepper.Audio.csproj \
    -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Audio.Tests/bin/Debug/net9.0/Winpepper.Audio.Tests.dll \
    -notrait "Platform=Windows"
```
Expected: build succeeds (`WarmWasapiRecorder` is excluded on Linux via
`#if WINDOWS`); `WarmCaptureBufferTests` still `Failed: 0`.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Audio/IWarmAudioRecorder.cs src/Winpepper.Audio/WarmWasapiRecorder.cs
git commit -m "$(cat <<'EOF'
feat: add WarmWasapiRecorder over the pre-roll ring buffer

Bug-2: one persistent WasapiCapture (when pre-warm is on) feeds
WarmCaptureBuffer so start-of-speech is captured; frames raise only during a
session. Cold-start fallback when pre-warm is off. Windows-only wiring;
verified in the smoke checklist.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 9: Wire the warm recorder into the pipeline (Windows)

Replace the per-press `new WasapiRecorder()` cold-start in `PipelineHost` with
the shared `IWarmAudioRecorder`, subscribe the voice meter once (frames only
flow during a session), pass the `PrewarmMicEnabled` setting through `AppShell`,
and add the Settings toggle. All three files are `#if WINDOWS` — verified in the
smoke checklist.

**Files:**
- Modify: `src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `src/Winpepper.App/Hosting/AppShell.cs:230-236`
- Modify: `src/Winpepper.App/Views/RecordingPage.xaml` and `RecordingPage.xaml.cs`

**Interfaces:**
- Consumes: `IWarmAudioRecorder`, `WarmWasapiRecorder`, `AppSettings.PrewarmMicEnabled`,
  `RecordingSettingsViewModel.PrewarmMicEnabled`.

- [ ] **Step 1: Add the prewarm field + constructor parameter to PipelineHost**

In `src/Winpepper.App/Hosting/PipelineHost.cs`:

Replace the recorder fields (lines 29-30):
```csharp
    private IAudioRecorder? _recorder;
    private Action<ReadOnlyMemory<float>>? _meterHandler;
```
with:
```csharp
    private IWarmAudioRecorder? _warmRecorder;
```

Add a field beside `_postPasteLearningEnabled` (line 51):
```csharp
    private readonly bool _prewarmMicEnabled;
```

Add a constructor parameter after `bool postPasteLearningEnabled = false)`
(line 72) — make it the last parameter:
```csharp
        bool postPasteLearningEnabled = false,
        bool prewarmMicEnabled = true)
```

Assign it after line 98 (`_postPasteLearningEnabled = postPasteLearningEnabled;`):
```csharp
        _prewarmMicEnabled = prewarmMicEnabled;
```

- [ ] **Step 2: Create the warm recorder and subscribe the meter once in TryStart**

In `TryStart()`, immediately before `_hook.Start();` (line 141), insert:
```csharp
        // Bug-2: one warm recorder for the app lifetime. Frames flow (and the
        // meter animates) only while a session is active, so subscribe once.
        if (_warmRecorder is null)
        {
            _warmRecorder = new Winpepper.Audio.WarmWasapiRecorder(prewarm: _prewarmMicEnabled);
            _warmRecorder.FramesAvailable += frame => _vm.ReportAudioFrame(frame);
        }
```

- [ ] **Step 3: Replace cold-start / stop / cancel with session calls**

In `HandleHotkey`:

`HoldDown` — replace lines 177-179:
```csharp
                _recorder = new WasapiRecorder();
                _recorder.Start();
                AttachMeter(_recorder);
```
with:
```csharp
                _warmRecorder!.StartSession(includePrerollMs: 500);
```

`HoldUp` — replace lines 195-197:
```csharp
                DetachMeter(_recorder!);
                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
```
with:
```csharp
                var samples = _warmRecorder!.StopSession();
```

`Cancel` — replace lines 330-331:
```csharp
                if (_recorder is not null) DetachMeter(_recorder);
                _recorder?.Dispose(); _recorder = null;
```
with:
```csharp
                _ = _warmRecorder?.StopSession();
```

`Toggle` start — replace lines 339-341:
```csharp
                    _recorder = new WasapiRecorder();
                    _recorder.Start();
                    AttachMeter(_recorder);
```
with:
```csharp
                    _warmRecorder!.StartSession(includePrerollMs: 500);
```

`Toggle` stop — replace lines 357-359:
```csharp
                    DetachMeter(_recorder!);
                    var samples2 = _recorder!.Stop();
                    _recorder.Dispose(); _recorder = null;
```
with:
```csharp
                    var samples2 = _warmRecorder!.StopSession();
```

- [ ] **Step 4: Remove the now-dead meter helpers and fix Dispose**

Delete the `AttachMeter` and `DetachMeter` methods (lines 494-507).

In `Dispose()` (line 515), replace:
```csharp
        _recorder?.Dispose();
```
with:
```csharp
        _warmRecorder?.Dispose();
```

- [ ] **Step 5: Pass the setting through AppShell**

In `src/Winpepper.App/Hosting/AppShell.cs`, in the `new PipelineHost(...)` call
(lines 230-236), change the final argument line:
```csharp
                                         postPasteLearningEnabled: settings.PostPasteLearningEnabled);
```
to:
```csharp
                                         postPasteLearningEnabled: settings.PostPasteLearningEnabled,
                                         prewarmMicEnabled: settings.PrewarmMicEnabled);
```

- [ ] **Step 6: Add the Settings toggle (Recording page)**

In `src/Winpepper.App/Views/RecordingPage.xaml`, add a `ToggleSwitch` next to the
existing `PostPasteLearningToggle` (after line 53):
```xml
                    <ToggleSwitch x:Name="PrewarmMicToggle" AutomationProperties.AutomationId="RecordingPrewarmMicToggle" Header="Keep the mic warm to capture the start of speech (mic indicator stays on)" />
```

In `src/Winpepper.App/Views/RecordingPage.xaml.cs`, after the
`PostPasteLearningToggle` wiring (lines 65-66):
```csharp
        PrewarmMicToggle.IsOn = vm.PrewarmMicEnabled;
        PrewarmMicToggle.Toggled += (_, _) => vm.PrewarmMicEnabled = PrewarmMicToggle.IsOn;
```

- [ ] **Step 7: Static verification (Linux — no App build possible)**

`src/Winpepper.App` is `#if WINDOWS` and does not build on Linux. Verify the
edits are internally consistent by static checks:
```bash
grep -n "_warmRecorder" src/Winpepper.App/Hosting/PipelineHost.cs
grep -n "new WasapiRecorder()" src/Winpepper.App/Hosting/PipelineHost.cs || echo "OK: no cold-start left"
grep -n "AttachMeter\|DetachMeter\|_meterHandler\|_recorder" src/Winpepper.App/Hosting/PipelineHost.cs || echo "OK: old meter/recorder members gone"
grep -n "prewarmMicEnabled: settings.PrewarmMicEnabled" src/Winpepper.App/Hosting/AppShell.cs
```
Expected: `_warmRecorder` referenced in start/stop/cancel/dispose; **no**
`new WasapiRecorder()`; **no** `AttachMeter`/`DetachMeter`/`_meterHandler`/
`_recorder`; the `AppShell` line present. Functional verification is the Windows
smoke checklist.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.App/Hosting/PipelineHost.cs src/Winpepper.App/Hosting/AppShell.cs \
        src/Winpepper.App/Views/RecordingPage.xaml src/Winpepper.App/Views/RecordingPage.xaml.cs
git commit -m "$(cat <<'EOF'
fix: capture start-of-speech via shared warm recorder

Bug-2: replace per-press cold-start WasapiRecorder with the lifetime
WarmWasapiRecorder (500 ms pre-roll); subscribe the voice meter once (frames
only during a session); thread PrewarmMicEnabled through AppShell and add the
Recording-page toggle. Windows-only; verified in the smoke checklist.

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

---

## Task 10: Unfreeze the status pill (Windows)

Bug 1 has two INDEPENDENT candidate causes, and the load-bearing-validation pass
found the original "drop the layered window" fix both un-attributable and
self-contradictory (see the root-cause note). This task therefore isolates the
better-evidenced fix and treats dropping the layered window as a *contingency*,
not the default:

- **(a) The content island is never initialized.** The window is only ever
  `appWindow.Show(activateWindow:false)`'d and never `Activate()`'d, so a WinUI 3
  window never composes a live DirectComposition tree and presents a frozen first
  frame. This is directly evidenced in the current code and is the PRIMARY fix.
- **(b) The legacy layered path *may* also interfere.** `MakeClickThroughTopmostTool`
  ORs in `WS_EX_LAYERED` + `SetLayeredWindowAttributes`. This *might* contribute to
  the freeze, but that specific symptom is uncorroborated — and the same layered
  style is what makes click-through reliable (below). So we do **not** drop it by
  default.

**Default fix (this task): initialize the island, keep everything else.** Add the
one-time `Activate()` → `Hide()`. Leave `WS_EX_LAYERED`/`SetLayeredWindowAttributes`
and the `alpha: 230` translucency exactly as they are. Click-through — which
currently WORKS — is untouched.

**Root-cause note (validated).** Click-through here overlays *other applications'*
windows (cross-process) on a composited (DWM) desktop. Independent, tested
implementations (GLFW, Wails, Electron, Godot, GTK-on-win32) converge that reliable
cross-process click-through needs `WS_EX_LAYERED | WS_EX_TRANSPARENT` **together** —
`WS_EX_TRANSPARENT`'s `HTTRANSPARENT` hit-test alone is *not* a blanket passthrough
(Raymond Chen, "WS_EX_TRANSPARENT is a lie"). Meanwhile the "layered redirection
freezes the DirectComposition content at frame 0" claim is plausible but has **no**
matching bug report; the better-evidenced freeze cause is the un-realized island
(a). The original plan bundled (a)+(b) into one change and kept "re-add
`WS_EX_LAYERED`" as its fallback — but if (b) is the freeze cause, that fallback
re-introduces the freeze, so neither the primary nor the fallback would yield a pill
that both animates AND is click-through. Isolating (a) removes that contradiction
and refuses to risk working click-through on an uncorroborated hypothesis. This
layer is Linux-untestable; the smoke checklist decides whether the contingency is
needed.

**Files (default fix):**
- Modify: `src/Winpepper.App/Views/StatusPillWindow.xaml.cs:66` (add `Activate()`
  before the final `Hide()`; the `MakeClickThroughTopmostTool(_hwnd, alpha: 230)`
  call at line 34 is left UNCHANGED).
- Contingency files (only if the smoke test still shows a frozen pill):
  `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`,
  `src/Winpepper.App/Views/StatusPillWindow.xaml` — see the Contingency block below.

- [ ] **Step 1: Initialize the content island once (the primary fix)**

In `src/Winpepper.App/Views/StatusPillWindow.xaml.cs`, leave the call at line 34
UNCHANGED (`ExtendedWindowStyle.MakeClickThroughTopmostTool(_hwnd, alpha: 230)` —
the layered/translucent click-through setup stays as-is). Replace ONLY the final
constructor line — the standalone `appWindow.Hide();` at the very end of the
constructor (currently line 66), **not** the `appWindow.Hide();` inside the
hide-timer lambda (~line 50) — with:
```csharp
        // Bug-1: initialize the content island exactly once. A WinUI 3 window
        // that is only ever Show(activateWindow:false)'d never composes a live
        // DirectComposition tree, so it presents a frozen first frame. Activate()
        // once realizes the island; Hide() immediately in the same pump keeps it
        // off-screen. WS_EX_NOACTIVATE (already set in MakeClickThroughTopmostTool)
        // is intended to prevent focus theft/flash; the smoke checklist verifies no
        // flash and no focus steal. Subsequent Show(activateWindow:false) calls then
        // present live content.
        this.Activate();
        appWindow.Hide();
```

- [ ] **Step 2: Static verification (Linux — no App build possible)**

Run:
```bash
grep -n "this.Activate();" src/Winpepper.App/Views/StatusPillWindow.xaml.cs
grep -n "MakeClickThroughTopmostTool(_hwnd, alpha: 230)" src/Winpepper.App/Views/StatusPillWindow.xaml.cs
grep -n "SetLayeredWindowAttributes" src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs
```
Expected: `this.Activate();` present; the `alpha: 230` click-through call still
present (layered path retained, unchanged); `SetLayeredWindowAttributes` still
called in setup (unchanged). Rendering / animation / click-through are decided by
the smoke checklist.

- [ ] **Step 3: Commit**

```bash
git add src/Winpepper.App/Views/StatusPillWindow.xaml.cs
git commit -m "$(cat <<'EOF'
fix: unfreeze status pill by initializing the WinUI content island

Bug-1: the pill window was only ever Show(activateWindow:false)'d and never
Activate()'d, so its WinUI 3 content island never composed a live
DirectComposition tree and presented a frozen first frame (no pulse, frozen
elapsed-ms). Activate() once then Hide() realizes the island; later Show() then
presents live content. The click-through/layered setup is intentionally left
untouched -- it works today, and reliable cross-process click-through relies on
WS_EX_LAYERED (dropping it is a documented contingency only).

🤖 Generated with [Amplifier](https://github.com/microsoft/amplifier)

Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
EOF
)"
```

### Contingency (apply ONLY if the smoke checklist shows the pill STILL frozen after Step 1, with the layered window retained)

If — and only if — the pill is still frozen after the island-init above, cause
(b) (the legacy layered-redirection path) is implicated. Only then, drop it and
move translucency into XAML. Do this as a SEPARATE commit so the two fixes stay
attributable.

- [ ] **C1: Remove the layered path from MakeClickThroughTopmostTool**

In `src/Winpepper.App/Views/Native/ExtendedWindowStyle.cs`, replace the
`MakeClickThroughTopmostTool` method (currently lines 31-43) with:

```csharp
    public static void MakeClickThroughTopmostTool(IntPtr hwnd)
    {
        // Bug-1 contingency: the legacy WS_EX_LAYERED + SetLayeredWindowAttributes
        // path puts the window on the layered-redirection composition path, which
        // can freeze a WinUI 3 / DirectComposition window's presented content.
        // Applied ONLY after the island-init fix proved insufficient in the smoke
        // test. Translucency now comes from XAML (StatusPillWindow.xaml root Opacity).
        //
        // Click-through WITHOUT the layered style is NOT guaranteed for a
        // cross-process overlay: WS_EX_TRANSPARENT returns HTTRANSPARENT from
        // hit-testing but, per tested implementations (GLFW/Wails/Electron/Godot),
        // that alone often does not pass clicks to a window in ANOTHER process on
        // modern DWM. The smoke checklist MUST re-verify click-through after this
        // change; if it regresses, use a non-layered passthrough (see C3) rather
        // than re-adding SetLayeredWindowAttributes, which would re-freeze the pill.
        var existing = (long)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        var updated  = existing | WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
        AssertTopmost(hwnd);
    }
```

(Leave the `WS_EX_LAYERED`, `LWA_ALPHA` constants and the
`SetLayeredWindowAttributes` P/Invoke declared but unused.)

- [ ] **C2: Apply whole-window translucency in XAML + drop the alpha arg**

In `src/Winpepper.App/Views/StatusPillWindow.xaml`, change:
```xml
    <Grid Background="Transparent" Padding="12,6">
```
to (0.9 = the prior `alpha: 230`/255):
```xml
    <Grid Background="Transparent" Padding="12,6" Opacity="0.9">
```
and in `src/Winpepper.App/Views/StatusPillWindow.xaml.cs` line 34, change
`MakeClickThroughTopmostTool(_hwnd, alpha: 230)` to
`MakeClickThroughTopmostTool(_hwnd)`.

- [ ] **C3: Re-validate click-through — do NOT re-add the layered alpha**

Dropping `WS_EX_LAYERED` is EXPECTED to risk cross-process click-through. Re-run
the smoke click-through check. If it regresses, do **not** restore
`SetLayeredWindowAttributes` (that re-introduces the freeze this contingency exists
to fix); instead adopt a non-layered passthrough (e.g. an
`InputNonClientPointerSource` region configuration, or a low-level mouse-hook
passthrough) and track it as a follow-up. The primary path (Step 1, layered
retained) avoids this entire trade-off, which is why it is the default.

---

## Task 11: Full non-Windows suite green

Confirm the whole Linux-runnable suite passes after all changes — the final gate
before the Windows smoke pass.

**Files:** none (verification only).

- [ ] **Step 1: Build + run the Cleanup and Core and Audio suites**

Run:
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
for proj in Winpepper.Cleanup.Tests Winpepper.Core.Tests Winpepper.Audio.Tests; do
  dotnet build "tests/$proj/$proj.csproj" -f net9.0 -p:EnableWindowsTargeting=true
  dotnet exec "tests/$proj/bin/Debug/net9.0/$proj.dll" -notrait "Platform=Windows"
done
```
Expected: each run reports `Failed: 0`. (Other test projects in the repo are
unchanged by this plan; if the workflow runs the whole suite, they remain green.)

- [ ] **Step 2: Confirm the tree is clean and committed**

Run: `git status --short`
Expected: no tracked modifications outstanding (only gitignored `.dotnet/`,
`bin/`, `obj/` may appear, which git ignores). No commit needed.

---

## Windows Smoke Test Checklist

These verify the WinUI, live-WASAPI, and real-GGUF layers that cannot run on
Linux. Run on a Windows 11 machine with a real microphone and an interactive
desktop session, using the local build/MSI. This is the required end-to-end
verification for Bugs 1 and the Windows halves of Bugs 2 and 3 — not optional.

**Bug 1 — status pill (`StatusPillWindow`, `ExtendedWindowStyle`):**
- [ ] Press-and-hold the hotkey and speak: the pill's elapsed-ms **counts up**
      (not frozen at `0 ms`).
- [ ] While speaking, the dot **scales with your voice** (louder = larger).
- [ ] During the transcribing/cleaning phase, the "thinking" **pulse is visible**
      (dot opacity oscillates).
- [ ] The pill is **click-through**: clicking where it overlaps another window
      activates the window beneath, not the pill. *(The default Task-10 fix keeps
      the layered window, so click-through should be UNCHANGED from before.)*
- [ ] **Decision gate for Task 10.** If the elapsed-ms/pulse checks above still
      show a **frozen** pill after the default fix (island-init with the layered
      window retained), apply the **Contingency** (Task 10 C1-C3: drop
      `WS_EX_LAYERED`, move translucency to XAML `Opacity`). Then RE-VERIFY both
      the animation checks AND click-through. If click-through then regresses, do
      **not** re-add `SetLayeredWindowAttributes` (it re-freezes the pill — that is
      the whole reason the layered path was dropped); use a non-layered passthrough
      (`InputNonClientPointerSource` region or a low-level mouse hook) and track it
      as a follow-up.
- [ ] The pill **never steals focus** (the caret/foreground window is unchanged
      when the pill shows) and shows **no flash** on first appearance.
- [ ] The pill stays **topmost** over other windows.

**Bug 2 — warm capture (`WarmWasapiRecorder`, `PipelineHost`):**
- [ ] Start-of-speech is **not clipped**: press the hotkey and immediately say a
      short word ("Test."); the injected text contains the whole word.
- [ ] The voice meter is **quiet at idle** (no dot movement when not dictating)
      and animates only during a session.
- [ ] Toggling **Settings → Recording → "Keep the mic warm…"** off restores
      cold-start (mic-in-use indicator only lights while dictating); dictation
      still works (start-of-speech may be slightly clipped, as before).
- [ ] **Changing the Windows default input device, then dictating again, records
      from the NEW device** (not the old one). The `StartSession` default-endpoint
      recheck rebuilds the warm stream when the default drifts. *(WASAPI does not
      signal a running capture on a default change, so without this the pill would
      silently keep recording the old mic — verify the correct device is actually
      captured, not merely that "something records".)*
- [ ] **Unplugging the active input device, then dictating**, still records (the
      `RecordingStopped`-with-exception fault path disposes and lazily recreates the
      stream). *(If a preference-only default change is ever found to slip through
      the per-session recheck, the fuller fix is an `IMMNotificationClient` via
      `RegisterEndpointNotificationCallback`.)*

**Bug 3 — cleanup safety (`CleanupRunner`, `LlamaCleanupBackend` with the real
Qwen2.5-0.5B model):**
- [ ] Dictate a single short word ("Right."): it is injected **as spoken**,
      never replaced by "I think we should just ship it tomorrow."
- [ ] Dictate a long question ("Who should be fixing this, me or the person
      configuring RunPod?"): it is **not** collapsed to "Me".
- [ ] Dictate a normal filler-heavy sentence ("um so like I think we should
      basically just ship it tomorrow you know"): cleanup still **improves** it
      (fillers removed, punctuation added) rather than falling back.
- [ ] Dictate a self-correction ("write me a function called add_numbers, no
      wait, scratch that, call it sum"): the correction is honored
      ("Write me a function called sum.").
```
