# ASR Eval Framework Implementation Plan

> **For agentic workers:** This plan is executed task-by-task by the
> workflow's execute stage: a fresh implementer per task, with a spec +
> quality review after each task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** Build a speech-to-text evaluation framework: export real dictation recordings out of the app's rolling history into a durable corpus, generate AssemblyAI reference transcripts for them, and extend the existing ASR latency bench with a corpus mode that measures each candidate speech model's accuracy (WER/CER) and post-stop latency through the exact production streaming path.

**Architecture:** Three parts. (A) A new console tool `scripts/asr-eval-corpus` with an `export` command that copies clips + metadata from `%LOCALAPPDATA%\winpepper\history` into a corpus folder outside the repo, keyed by the app's stable history entry ids. (B) A `references` command in the same tool that reuses the existing `AssemblyAiTranscriber` (with a new `Disfluencies` option) to write one hand-editable `*.reference.txt` per clip. (C) A new `corpus` scenario in `scripts/asr-latency-bench` that streams each clip through the real production classes (`TranscribeCppEngine` + `NemotronStreamingTranscriber` + `StreamingDictationSession`) with three fidelity fixes (500 ms preroll burst, silence-trimmed `FinishAsync` audio, stopwatch-scheduled pacing) and scores against the references, emitting `results.json` + `results.md`. Pure logic (manifest, export planning, reference planning, WER/CER, framing, result aggregation) lives in BCL-only files that are `Compile Include`'d into `tests/Winpepper.Asr.Tests` — the established pattern for bench code (`BenchAudio.cs`, `TranscriptDiff.cs`).

**Tech Stack:** C# / .NET 9 (local SDK at `/home/dan/code/winpepper/.dotnet`), xUnit v3 + Shouldly, System.Text.Json, existing `Winpepper.Asr` library, bash + PowerShell WSL→Windows interop driver.

## Global Constraints

Copied from the spec and `AGENTS.md` — every task's requirements implicitly include this section.

- **Privacy (hard rule):** the corpus and reference transcripts are real voice recordings — they must NEVER land in the repo or in git. Corpus lives at `/mnt/c/Users/dan/winpepper-evals/corpus-v1` (Windows: `C:\Users\dan\winpepper-evals\corpus-v1`), outside the repo. Eval `results.json` (contains transcript text) goes only to gitignored `artifacts/`. `results.md` and the committed evidence doc contain NUMBERS and clip ids only — no transcript/reference text. Unit tests use synthetic text only.
- **History folder is read-only:** never modify or delete anything under `/mnt/c/Users/dan/AppData/Local/winpepper/history`. The exporter only reads `index.json` and copies WAVs out.
- **Tests green before EVERY commit.** On Linux run `./scripts/linux-tests.sh` (exit 0, all `Failed: 0`). Per-project quick loop: build with `-c Release -f net9.0 -p:EnableWindowsTargeting=true`, then `dotnet exec <built test dll> -notrait "Platform=Windows"`. NEVER use `dotnet test` (VSTest host is unreliable in this repo).
- **SDK env** (needed once per shell; the SDK lives in the main checkout, not the worktree):

  ```bash
  cd /home/dan/code/winpepper/.worktrees/asr-eval-framework
  export DOTNET_ROOT=/home/dan/code/winpepper/.dotnet
  export PATH="$DOTNET_ROOT:$PATH"
  ```

  All commands in this plan run from the worktree root above.
- **Full Windows gate before any push:** `./scripts/windows-gate.sh` must end `GATE: GREEN`.
- **Never mix Linux- and Windows-side builds in the same `bin`/`obj`** — clean when switching sides (CS0006 corruption otherwise). The new driver script pre-cleans, like `windows-gate.sh` does.
- **`scripts/asr-latency-bench` is deliberately NOT in `winpepper.sln` — keep it that way.** The new `scripts/asr-eval-corpus` project is likewise kept OUT of the solution.
- **Linked-file rule:** any file under `scripts/` that is `Compile Include`'d into `tests/Winpepper.Asr.Tests` must stay BCL-only (System.* only, no package/project-specific types), because that test project does not reference the tools' dependencies.
- **AssemblyAI specifics:** model id is exactly `"universal-3-5-pro"` (`AssemblyAiModels.DefaultId`); references are requested with `disfluencies: true` (keep "um"/"uh" — local models transcribe fillers verbatim) but `format_text`/`punctuate` stay `true`; `DeleteAfterTranscribe = true`; API key ONLY from the `ASSEMBLYAI_API_KEY` environment variable; if unset, say so clearly and exit non-zero — never fabricate references. Beware: `new AssemblyAiOptions()` defaults `Model` to `"universal-2"` — always set it explicitly.
- **Unrelated uncommitted change:** the main checkout has an uncommitted `HistoryStore` MaxEntries 50→100 change (plus test). All work happens in this worktree (which is clean); do not touch `src/Winpepper.History/HistoryStore.cs` or its tests, and never include that change in this task's commits.
- **Windows-only engine:** `TranscribeCppEngine` hard-throws off Windows (`TranscribeCppEngine.cs:73`). The corpus eval runs on the Windows host via the driver script; on Linux we only compile the bench.
- **Naming:** use exactly two terms consistently — "corpus" (the exported clip collection) and "reference transcript" (the AssemblyAI-generated text we treat as correct). No new jargon.
- **Docs:** README.md untouched. The only markdown added is this plan and the evidence doc, both under `docs/plans/` (working/agent docs — allowed).
- **Commits:** conventional style used in this repo (`feat(...)`, `fix(...)`, `docs(...)`), focused and atomic.

---

## Design decisions (locked in)

1. **Corpus layout** (all outside the repo):

   ```
   corpus-v1/
     manifest.json                      # schema 1, camelCase, hand-editable curation flags
     clips/<id>.wav                     # flat copy; <id> = app's stable 32-hex history id
     clips/<id>.reference.txt           # reference transcript, next to the clip, plain text
   ```

2. **Manifest is a local DTO, not a `ProjectReference` to `Winpepper.History`.** The tool only READS the history folder; a ~20-line camelCase DTO keeps every shared file BCL-only so it can be `Compile Include`'d into `tests/Winpepper.Asr.Tests` (the repo's existing pattern for bench files). The real `index.json` parse is proven end-to-end in Task 3.
3. **Disfluencies via a source change (Route A):** add `AssemblyAiOptions.Disfluencies { get; init; } = false` and send `_opts.Disfluencies` in `AssemblyAiClient.CreateTranscriptAsync`. Default `false` keeps the app's behavior identical; the eval sets `true`. This literally reuses the existing client (spec requirement) instead of reimplementing the HTTP layer.
4. **No number-word normalization.** The spec says "consider number-word normalization only if simple" — it is not simple to do correctly (dates, currency, ordinals), and the same normalization is applied to reference and every candidate alike, so relative ranking (the stated purpose) is unaffected. WER/CER reuse `TranscriptDiff.Normalize` (lowercase, strip punctuation keeping apostrophes, collapse whitespace) unchanged.
5. **Batch fallback in the corpus mode runs on a SECOND `TranscribeCppEngine` instance** (same model + runtime, its own compute gate), wrapped in a `ProbeTranscriber` (the bench's integrity-control pattern), so `fellBack` is recorded per clip and a fallback run still produces text. It must NOT be the primary engine — code inspection falsified that: `FinishAsync` never disposes the native stream; the sole dispose site is `Session.DisposeAsync()` (`NemotronStreamingTranscriber.cs:174`), which `StreamingDictationSession` calls only AFTER `FinishAsync` returns (`StreamingDictationSession.cs:120-121`), and the stream holds the engine-wide `SemaphoreSlim(1,1)` compute gate for its whole lifetime (acquired `TranscribeCppEngine.cs:177`, released only in `NativeStream.Dispose`, `:336`). A fallback awaited inside `FinishAsync` that calls `TranscribeBatch` on the SAME engine therefore stalls 5 s at the gate wait (`:235`) and throws `TranscribeCppException` — every fallback clip would fail. (Production never hits this: its batch fallback is a different engine.) The batch-parity `TranscribeBatch` call stays on the primary `corpusEngine` — it runs before any streaming session exists on that engine, so it is gate-safe. Cost of the second instance: ~700 MB extra model RAM, bench-only, accepted — do not "simplify" back to one engine. Truncation is detected by scanning `NemotronStreamingTranscriber`'s log lines for `was_truncated` (its fallback reason string) — the only externally observable truncation signal.
6. **Silent clips replay production exactly:** streamed frames stay untrimmed; the buffer passed to `FinishAsync` is `SilenceTrimmer.Trim(...)`'s output; when `Trim` says `IsSilent`, production drops the dictation without transcribing (`PipelineHost` `TrimForTranscription` → null), so the eval records an empty transcript and no latency sample for that clip.
7. **`results.md` contains no transcript text** (safe to quote in the committed evidence doc); `results.json` carries full transcripts, references, and diffs and stays in gitignored `artifacts/`.

## File structure

```
scripts/asr-eval-corpus/                          # NEW console tool (Parts A + B) — NOT in winpepper.sln
  AsrEvalCorpus.csproj
  Program.cs                                      # export + references commands (not linked into tests)
  CorpusManifest.cs                               # ClipTimings, CorpusEntry, CorpusJson, CorpusManifest   [BCL-only, test-linked]
  HistoryIndex.cs                                 # HistoryIndexEntry, HistoryIndexFile, HistoryIndex      [BCL-only, test-linked]
  CorpusExport.cs                                 # ExportItem, ExportPlan, CorpusExport.BuildPlan         [BCL-only, test-linked]
  ReferencePlanner.cs                             # ReferenceAction, ReferencePlanner                      [BCL-only, test-linked]

scripts/asr-latency-bench/                        # EXISTING bench (Part C)
  Program.cs                                      # + --corpus/--out/--repeats flags, "corpus" case,
                                                  #   EngineBatchTranscriber, ListLogger helper classes
  AsrLatencyBench.csproj                          # + ProjectReference Winpepper.Audio, + links to CorpusManifest.cs, ReferencePlanner.cs
  EvalMetrics.cs                                  # NEW: ErrorRate, EvalMetrics (WER/CER/SilentPass)       [BCL-only, test-linked]
  EvalFraming.cs                                  # NEW: preroll-burst + 50 ms frame segmentation          [BCL-only, test-linked]
  EvalResults.cs                                  # NEW: ClipResult, EvalRunInfo, EvalSummary, EvalReport, EvalResults [BCL-only, test-linked]

scripts/run-asr-eval-windows.sh                   # NEW driver: build on Windows dotnet over UNC, stage to %TEMP%, run, collect artifacts/

src/Winpepper.Asr/Transcription/
  AssemblyAiOptions.cs                            # + Disfluencies option
  AssemblyAiClient.cs                             # line 73: ["disfluencies"] = _opts.Disfluencies

tests/Winpepper.Asr.Tests/
  Winpepper.Asr.Tests.csproj                      # + Compile Include links for the 7 BCL-only files above
  AssemblyAiClientTests.cs                        # + disfluencies:true test
  CorpusManifestTests.cs                          # NEW
  HistoryIndexTests.cs                            # NEW
  CorpusExportTests.cs                            # NEW
  ReferencePlannerTests.cs                        # NEW
  EvalMetricsTests.cs                             # NEW
  EvalFramingTests.cs                             # NEW
  EvalResultsTests.cs                             # NEW

docs/plans/2026-07-26-asr-eval-framework-evidence.md   # NEW (Task 12): real run numbers, no transcript text
```

Scope check: the three parts form one workflow sharing the manifest types and one eval pipeline; a single plan with per-task test cycles covers it without artificial splits.

---

### Task 1: Corpus tool skeleton + manifest and history-index models

**Files:**
- Create: `scripts/asr-eval-corpus/AsrEvalCorpus.csproj`
- Create: `scripts/asr-eval-corpus/CorpusManifest.cs`
- Create: `scripts/asr-eval-corpus/HistoryIndex.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (add Compile Include links)
- Test: `tests/Winpepper.Asr.Tests/CorpusManifestTests.cs`
- Test: `tests/Winpepper.Asr.Tests/HistoryIndexTests.cs`

**Interfaces:**
- Consumes: nothing (foundation task).
- Produces (used by Tasks 2, 3, 5, 6, 10):
  - `AsrEvalCorpus.ClipTimings(int RecordMs, int TranscribeMs, int CleanupMs, int InjectMs, int TotalMs)` — record
  - `AsrEvalCorpus.CorpusEntry(string Id, DateTime CreatedAtUtc, int DurationMs, string WavPath, string RawTranscript, string CleanedText, string AsrModelName, string CleanupModelName, ClipTimings Timings)` — record with `bool ExpectedSilent { get; init; }` and `bool Exclude { get; init; }`
  - `AsrEvalCorpus.CorpusManifest` — `int Schema` (=1), `List<CorpusEntry> Entries`, `static CorpusManifest Load(string path)`, `static CorpusManifest LoadOrEmpty(string path)`, `void Save(string path)`
  - `AsrEvalCorpus.CorpusJson.Options` — shared `JsonSerializerOptions` (camelCase, case-insensitive, indented)
  - `AsrEvalCorpus.HistoryIndexEntry(string Id, DateTime CreatedAtUtc, string RawTranscript, string CleanedText, string WavRelativePath, int DurationMs, string AsrModelName, string CleanupModelName, ClipTimings Timings)` — record
  - `AsrEvalCorpus.HistoryIndexFile(int Schema, List<HistoryIndexEntry> Entries)` — record
  - `AsrEvalCorpus.HistoryIndex.Load(string indexJsonPath)` → `HistoryIndexFile`

- [ ] **Step 1: Create the tool csproj (kept OUT of winpepper.sln)**

Create `scripts/asr-eval-corpus/AsrEvalCorpus.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>AsrEvalCorpus</RootNamespace>
    <AssemblyName>AsrEvalCorpus</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Asr\Winpepper.Asr.csproj" />
    <Compile Include="..\asr-latency-bench\BenchAudio.cs" Link="Bench\BenchAudio.cs" />
  </ItemGroup>
</Project>
```

(`BenchAudio.ReadMono16k` is the repo's strict WAV reader — reused, not rewritten. Do NOT add this project to `winpepper.sln`.)

Also create a placeholder `scripts/asr-eval-corpus/Program.cs` so the project builds (the real commands come in Tasks 3 and 6):

```csharp
Console.Error.WriteLine("AsrEvalCorpus: commands are added in later tasks (export, references).");
return 1;
```

- [ ] **Step 2: Write the failing tests + csproj links**

Add to `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`, in the same `<ItemGroup>` that already links `BenchAudio.cs` / `TranscriptDiff.cs` (lines 22-23):

```xml
    <Compile Include="..\..\scripts\asr-eval-corpus\CorpusManifest.cs" Link="Corpus\CorpusManifest.cs" />
    <Compile Include="..\..\scripts\asr-eval-corpus\HistoryIndex.cs" Link="Corpus\HistoryIndex.cs" />
```

Create `tests/Winpepper.Asr.Tests/CorpusManifestTests.cs`:

```csharp
using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorpusManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"corpus-manifest-{Guid.NewGuid():N}");

    public CorpusManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static CorpusEntry Entry(string id, bool expectedSilent = false, bool exclude = false) =>
        new(id, new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc), 2300, $"clips/{id}.wav",
            "raw text", "Cleaned text.", "nemotron-streaming-en", "none",
            new ClipTimings(2300, 450, 0, 12, 2800))
        { ExpectedSilent = expectedSilent, Exclude = exclude };

    [Fact]
    public void SaveThenLoad_RoundTrips_EntriesAndFlags()
    {
        var path = Path.Combine(_dir, "manifest.json");
        var manifest = new CorpusManifest();
        manifest.Entries.Add(Entry("aaa", expectedSilent: true));
        manifest.Entries.Add(Entry("bbb", exclude: true));

        manifest.Save(path);
        var loaded = CorpusManifest.Load(path);

        loaded.Schema.ShouldBe(1);
        loaded.Entries.Count.ShouldBe(2);
        loaded.Entries[0].Id.ShouldBe("aaa");
        loaded.Entries[0].ExpectedSilent.ShouldBeTrue();
        loaded.Entries[0].Exclude.ShouldBeFalse();
        loaded.Entries[1].Exclude.ShouldBeTrue();
        loaded.Entries[1].Timings.TranscribeMs.ShouldBe(450);
    }

    [Fact]
    public void Load_HandEditedCamelCaseJson_ReadsCurationFlags()
    {
        var path = Path.Combine(_dir, "manifest.json");
        File.WriteAllText(path, """
        {
          "schema": 1,
          "entries": [
            {
              "id": "abc123",
              "createdAtUtc": "2026-07-26T10:15:30Z",
              "durationMs": 1500,
              "wavPath": "clips/abc123.wav",
              "rawTranscript": "um hello",
              "cleanedText": "Hello.",
              "asrModelName": "nemotron-streaming-en",
              "cleanupModelName": "none",
              "timings": { "recordMs": 1500, "transcribeMs": 300, "cleanupMs": 0, "injectMs": 10, "totalMs": 1900 },
              "expectedSilent": true,
              "exclude": false
            }
          ]
        }
        """);

        var loaded = CorpusManifest.Load(path);

        loaded.Entries.Single().ExpectedSilent.ShouldBeTrue();
        loaded.Entries.Single().WavPath.ShouldBe("clips/abc123.wav");
        loaded.Entries.Single().Timings.TotalMs.ShouldBe(1900);
    }

    [Fact]
    public void LoadOrEmpty_MissingFile_ReturnsEmptyManifest()
    {
        var loaded = CorpusManifest.LoadOrEmpty(Path.Combine(_dir, "does-not-exist.json"));

        loaded.Schema.ShouldBe(1);
        loaded.Entries.ShouldBeEmpty();
    }
}
```

Create `tests/Winpepper.Asr.Tests/HistoryIndexTests.cs` (sample mirrors the app's real `index.json` shape — camelCase, `schema`/`entries`, extra fields like `windowContextUsed` and `transcriptPreview` that must be ignored):

```csharp
using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class HistoryIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"history-index-{Guid.NewGuid():N}");

    public HistoryIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Load_RealWorldShape_ParsesEntriesAndIgnoresExtraFields()
    {
        var path = Path.Combine(_dir, "index.json");
        File.WriteAllText(path, """
        {
          "schema": 1,
          "entries": [
            {
              "id": "3f2a1b4c5d6e7f8091a2b3c4d5e6f708",
              "createdAtUtc": "2026-07-26T10:15:30.1234567Z",
              "rawTranscript": "hello world",
              "cleanedText": "Hello world.",
              "wavRelativePath": "2026-07-26/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.wav",
              "durationMs": 2300,
              "asrModelName": "nemotron-streaming-en",
              "cleanupModelName": "none",
              "windowContextUsed": false,
              "windowTitleAtStart": "editor",
              "windowTitleAtInject": "editor",
              "timings": { "recordMs": 2300, "transcribeMs": 450, "cleanupMs": 0, "injectMs": 12, "totalMs": 2800 },
              "transcriptPreview": "Hello world."
            }
          ]
        }
        """);

        var index = HistoryIndex.Load(path);

        index.Schema.ShouldBe(1);
        var entry = index.Entries.Single();
        entry.Id.ShouldBe("3f2a1b4c5d6e7f8091a2b3c4d5e6f708");
        entry.WavRelativePath.ShouldBe("2026-07-26/3f2a1b4c5d6e7f8091a2b3c4d5e6f708.wav");
        entry.RawTranscript.ShouldBe("hello world");
        entry.CleanedText.ShouldBe("Hello world.");
        entry.DurationMs.ShouldBe(2300);
        entry.Timings.TranscribeMs.ShouldBe(450);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail (missing source files → build error)**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001: Source file '...scripts/asr-eval-corpus/CorpusManifest.cs' could not be found` (and the same for `HistoryIndex.cs`).

- [ ] **Step 4: Implement the models**

Create `scripts/asr-eval-corpus/CorpusManifest.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AsrEvalCorpus;

/// <summary>Per-entry pipeline timings copied from the app's history index.</summary>
public sealed record ClipTimings(int RecordMs, int TranscribeMs, int CleanupMs, int InjectMs, int TotalMs);

/// <summary>
/// One corpus clip. ExpectedSilent and Exclude are curation flags meant to be
/// edited by hand in manifest.json. BCL-only so the same file compiles into
/// Winpepper.Asr.Tests and AsrLatencyBench.
/// </summary>
public sealed record CorpusEntry(
    string Id,
    DateTime CreatedAtUtc,
    int DurationMs,
    string WavPath,
    string RawTranscript,
    string CleanedText,
    string AsrModelName,
    string CleanupModelName,
    ClipTimings Timings)
{
    /// <summary>Reference transcript is empty by definition (the user recorded silence).</summary>
    public bool ExpectedSilent { get; init; }

    /// <summary>Skip this clip entirely (e.g. sensitive content).</summary>
    public bool Exclude { get; init; }
}

public static class CorpusJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}

public sealed class CorpusManifest
{
    public int Schema { get; init; } = 1;
    public List<CorpusEntry> Entries { get; init; } = new();

    public static CorpusManifest Load(string path)
        => JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path), CorpusJson.Options)
           ?? new CorpusManifest();

    public static CorpusManifest LoadOrEmpty(string path)
        => File.Exists(path) ? Load(path) : new CorpusManifest();

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, CorpusJson.Options));
        File.Move(tmp, path, overwrite: true);
    }
}
```

Create `scripts/asr-eval-corpus/HistoryIndex.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AsrEvalCorpus;

/// <summary>
/// Read-only model of the app's %LOCALAPPDATA%\winpepper\history\index.json
/// (schema 1, camelCase; written by src/Winpepper.History/HistoryStore.cs).
/// Extra fields (windowContextUsed, transcriptPreview, ...) are ignored on read.
/// Deliberately a local DTO instead of a ProjectReference to Winpepper.History:
/// this tool only ever READS the history folder, and the file must stay
/// BCL-only so it compiles into Winpepper.Asr.Tests.
/// </summary>
public sealed record HistoryIndexEntry(
    string Id,
    DateTime CreatedAtUtc,
    string RawTranscript,
    string CleanedText,
    string WavRelativePath,
    int DurationMs,
    string AsrModelName,
    string CleanupModelName,
    ClipTimings Timings);

public sealed record HistoryIndexFile(int Schema, List<HistoryIndexEntry> Entries);

public static class HistoryIndex
{
    public static HistoryIndexFile Load(string indexJsonPath)
        => JsonSerializer.Deserialize<HistoryIndexFile>(File.ReadAllText(indexJsonPath), CorpusJson.Options)
           ?? new HistoryIndexFile(1, new List<HistoryIndexEntry>());
}
```

- [ ] **Step 5: Run the new tests to verify they pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.CorpusManifestTests
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.HistoryIndexTests
```

Expected: PASS (4 tests, `Failed: 0`). Also verify the tool project itself builds:

```bash
dotnet build scripts/asr-eval-corpus/AsrEvalCorpus.csproj -c Release
```

Expected: `Build succeeded.`

- [ ] **Step 6: Full Linux suite, then commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-eval-corpus/ tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/CorpusManifestTests.cs tests/Winpepper.Asr.Tests/HistoryIndexTests.cs
git commit -m "feat(eval): corpus manifest and history index models for the ASR eval corpus tool"
```

Expected: linux-tests exits 0 with all `Failed: 0`; commit succeeds.

---

### Task 2: Export planning logic

**Files:**
- Create: `scripts/asr-eval-corpus/CorpusExport.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (one more link)
- Test: `tests/Winpepper.Asr.Tests/CorpusExportTests.cs`

**Interfaces:**
- Consumes: `HistoryIndexEntry`, `CorpusManifest`, `CorpusEntry`, `ClipTimings` (Task 1).
- Produces (used by Task 3):
  - `AsrEvalCorpus.ExportItem(HistoryIndexEntry Source, CorpusEntry Entry)` — record
  - `AsrEvalCorpus.ExportPlan(IReadOnlyList<ExportItem> ToAdd, int SkippedExisting)` — record
  - `AsrEvalCorpus.CorpusExport.BuildPlan(IReadOnlyList<HistoryIndexEntry> history, CorpusManifest existing, int? take = null)` → `ExportPlan`

- [ ] **Step 1: Write the failing tests + csproj link**

Add to the same `<ItemGroup>` in `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj`:

```xml
    <Compile Include="..\..\scripts\asr-eval-corpus\CorpusExport.cs" Link="Corpus\CorpusExport.cs" />
```

Create `tests/Winpepper.Asr.Tests/CorpusExportTests.cs`:

```csharp
using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class CorpusExportTests
{
    private static HistoryIndexEntry History(string id, int minuteOffset = 0) =>
        new(id, new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc).AddMinutes(minuteOffset),
            "raw", "clean", $"2026-07-26/{id}.wav", 2000, "nemotron-streaming-en", "none",
            new ClipTimings(2000, 400, 0, 10, 2500));

    private static CorpusManifest ManifestWith(params string[] ids)
    {
        var m = new CorpusManifest();
        foreach (var id in ids)
            m.Entries.Add(new CorpusEntry(id, DateTime.UtcNow, 1, $"clips/{id}.wav",
                "r", "c", "m", "n", new ClipTimings(1, 1, 1, 1, 1)));
        return m;
    }

    [Fact]
    public void BuildPlan_EmptyManifest_MapsEveryHistoryEntryToCorpusEntry()
    {
        var plan = CorpusExport.BuildPlan(new[] { History("aaa") }, new CorpusManifest());

        plan.SkippedExisting.ShouldBe(0);
        var item = plan.ToAdd.Single();
        item.Source.Id.ShouldBe("aaa");
        item.Entry.Id.ShouldBe("aaa");
        item.Entry.WavPath.ShouldBe("clips/aaa.wav");
        item.Entry.RawTranscript.ShouldBe("raw");
        item.Entry.CleanedText.ShouldBe("clean");
        item.Entry.AsrModelName.ShouldBe("nemotron-streaming-en");
        item.Entry.Timings.TranscribeMs.ShouldBe(400);
        item.Entry.ExpectedSilent.ShouldBeFalse();
        item.Entry.Exclude.ShouldBeFalse();
    }

    [Fact]
    public void BuildPlan_ExistingIds_AreSkippedNotDuplicated()
    {
        var plan = CorpusExport.BuildPlan(
            new[] { History("aaa"), History("bbb") }, ManifestWith("aaa"));

        plan.SkippedExisting.ShouldBe(1);
        plan.ToAdd.Single().Entry.Id.ShouldBe("bbb");
    }

    [Fact]
    public void BuildPlan_Take_LimitsToTheMostRecentNewClips()
    {
        var plan = CorpusExport.BuildPlan(
            new[] { History("old", 0), History("mid", 1), History("new", 2) },
            new CorpusManifest(), take: 2);

        plan.ToAdd.Count.ShouldBe(2);
        plan.ToAdd[0].Entry.Id.ShouldBe("new");
        plan.ToAdd[1].Entry.Id.ShouldBe("mid");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001: Source file '...CorpusExport.cs' could not be found`.

- [ ] **Step 3: Implement**

Create `scripts/asr-eval-corpus/CorpusExport.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace AsrEvalCorpus;

public sealed record ExportItem(HistoryIndexEntry Source, CorpusEntry Entry);

public sealed record ExportPlan(IReadOnlyList<ExportItem> ToAdd, int SkippedExisting);

public static class CorpusExport
{
    /// <summary>
    /// Plans an export: history entries whose id is not yet in the corpus
    /// manifest, newest first, optionally limited to the most recent
    /// <paramref name="take"/> new clips. Ids are the app's stable history
    /// entry ids, so re-running never duplicates a clip.
    /// </summary>
    public static ExportPlan BuildPlan(
        IReadOnlyList<HistoryIndexEntry> history, CorpusManifest existing, int? take = null)
    {
        var known = new HashSet<string>(existing.Entries.Select(e => e.Id), StringComparer.Ordinal);
        var fresh = history.Where(h => !known.Contains(h.Id))
            .OrderByDescending(h => h.CreatedAtUtc)
            .ToList();
        var skipped = history.Count - fresh.Count;
        if (take is int n)
            fresh = fresh.Take(n).ToList();
        var toAdd = fresh.Select(h => new ExportItem(h, new CorpusEntry(
            h.Id, h.CreatedAtUtc, h.DurationMs, $"clips/{h.Id}.wav",
            h.RawTranscript, h.CleanedText, h.AsrModelName, h.CleanupModelName, h.Timings))).ToList();
        return new ExportPlan(toAdd, skipped);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.CorpusExportTests
```

Expected: PASS (3 tests).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-eval-corpus/CorpusExport.cs tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/CorpusExportTests.cs
git commit -m "feat(eval): export planning -- dedupe by stable history id, newest-first take"
```

---

### Task 3: Exporter command + proof against the real history folder

**Files:**
- Modify: `scripts/asr-eval-corpus/Program.cs` (replace the placeholder)

**Interfaces:**
- Consumes: `HistoryIndex.Load`, `CorpusManifest.LoadOrEmpty/Save`, `CorpusExport.BuildPlan` (Tasks 1-2).
- Produces: the `export` CLI command. Task 6 extends this same file with `references`; the arg-parsing loop and `PrintUsage` written here are shared.

- [ ] **Step 1: Implement the export command**

Replace `scripts/asr-eval-corpus/Program.cs` entirely with:

```csharp
using System.Globalization;
using AsrEvalCorpus;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
string? historyDir = null;
string? corpusDir = null;
int? take = null;
var force = false;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--history": historyDir = args[++i]; break;
        case "--corpus": corpusDir = args[++i]; break;
        case "--take": take = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--force": force = true; break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            PrintUsage();
            return 1;
    }
}
_ = force; // used by the references command (Task 6)

if (corpusDir is null)
{
    Console.Error.WriteLine("--corpus <dir> is required");
    PrintUsage();
    return 1;
}

switch (command)
{
    case "export":
        if (historyDir is null)
        {
            Console.Error.WriteLine("export requires --history <dir>");
            return 1;
        }
        return RunExport(historyDir, corpusDir, take);
    default:
        Console.Error.WriteLine($"unknown command: {command}");
        PrintUsage();
        return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
        usage:
          AsrEvalCorpus export --history <app-history-dir> --corpus <corpus-dir> [--take N]
          AsrEvalCorpus references --corpus <corpus-dir> [--force]

        export      copies new dictation clips out of the app's rolling history into a
                    durable corpus folder (read-only on the history side; re-runnable,
                    never duplicates a clip).
        references  generates a reference transcript per clip via AssemblyAI
                    (needs ASSEMBLYAI_API_KEY; skips clips that already have one).
        """);
}

static int RunExport(string historyDir, string corpusDir, int? take)
{
    var indexPath = Path.Combine(historyDir, "index.json");
    if (!File.Exists(indexPath))
    {
        Console.Error.WriteLine($"export: no history index at {indexPath}");
        return 1;
    }
    var history = HistoryIndex.Load(indexPath);
    var manifestPath = Path.Combine(corpusDir, "manifest.json");
    var manifest = CorpusManifest.LoadOrEmpty(manifestPath);
    var plan = CorpusExport.BuildPlan(history.Entries, manifest, take);

    Directory.CreateDirectory(Path.Combine(corpusDir, "clips"));
    var copied = 0;
    var missing = 0;
    foreach (var item in plan.ToAdd)
    {
        var src = Path.Combine(historyDir, item.Source.WavRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src))
        {
            missing++;
            Console.Error.WriteLine($"export[{item.Entry.Id}]: WAV missing in history (already pruned?), skipped");
            continue;
        }
        File.Copy(src, Path.Combine(corpusDir, item.Entry.WavPath), overwrite: true);
        manifest.Entries.Add(item.Entry);
        copied++;
    }
    manifest.Save(manifestPath);
    Console.WriteLine($"export: {copied} new clips copied, {plan.SkippedExisting} already in corpus, {missing} missing WAVs");
    return 0;
}
```

(Note: the history folder is touched only by `File.Exists` and `File.Copy` **source** reads — never written.)

- [ ] **Step 2: Build**

```bash
dotnet build scripts/asr-eval-corpus/AsrEvalCorpus.csproj -c Release
```

Expected: `Build succeeded.` 0 warnings.

- [ ] **Step 3: Real run — build a small corpus from the actual history (E2E proof for Part A)**

```bash
dotnet run --project scripts/asr-eval-corpus -c Release -- export \
  --history /mnt/c/Users/dan/AppData/Local/winpepper/history \
  --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --take 5
ls /mnt/c/Users/dan/winpepper-evals/corpus-v1/clips/ | head
head -40 /mnt/c/Users/dan/winpepper-evals/corpus-v1/manifest.json
```

Expected: `export: 5 new clips copied, 0 already in corpus, 0 missing WAVs`; 5 `<32-hex-id>.wav` files; manifest shows camelCase entries with `expectedSilent: false`, `exclude: false`.

- [ ] **Step 4: Re-run to prove no duplication**

```bash
dotnet run --project scripts/asr-eval-corpus -c Release -- export \
  --history /mnt/c/Users/dan/AppData/Local/winpepper/history \
  --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1 \
  --take 5
python3 -c "import json;m=json.load(open('/mnt/c/Users/dan/winpepper-evals/corpus-v1/manifest.json'));ids=[e['id'] for e in m['entries']];print(len(ids),len(set(ids)))"
```

Expected: `export: 5 new clips copied, 5 already in corpus, 0 missing WAVs` (5 *different* clips — the next-most-recent ones) and the python check prints `10 10` (all ids unique). Re-runnability + dedupe proven.

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-eval-corpus/Program.cs
git commit -m "feat(eval): corpus exporter -- copy dictation clips out of the rolling history"
```

---

### Task 4: `Disfluencies` option on the AssemblyAI client

**Files:**
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs` (add property after `LanguageCode`, line ~8)
- Modify: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs:73`
- Test: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs` (add one test)

**Interfaces:**
- Consumes: existing `AssemblyAiOptions`, `AssemblyAiClient`.
- Produces (used by Task 6): `AssemblyAiOptions.Disfluencies { get; init; } = false` — when `true`, the transcript request payload contains `"disfluencies":true`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs` (same class, after `CreateTranscript_SendsSpeechModelPayload_NoVocab_NoWordBoost` which asserts the default `"disfluencies":false` at line ~119 — that existing assertion must keep passing):

```csharp
    [Fact]
    public async Task CreateTranscript_DisfluenciesOptionTrue_SendsDisfluenciesTrue()
    {
        var handler = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"id\":\"t-9\",\"status\":\"queued\"}");
        var http = new HttpClient(handler);
        var opts = new AssemblyAiOptions { Disfluencies = true };
        var client = new AssemblyAiClient(http, () => "KEY", opts, NullLogger<AssemblyAiClient>.Instance,
            (ts, _) => Task.CompletedTask);

        await client.CreateTranscriptAsync("https://cdn/aai/ok", "universal-3-5-pro",
            AssemblyAiRequestExtras.Empty, CancellationToken.None);

        var json = Encoding.UTF8.GetString(handler.RequestBodies[0]);
        json.ShouldContain("\"disfluencies\":true");
        json.ShouldContain("\"format_text\":true");   // formatting stays on
        json.ShouldContain("\"punctuate\":true");
    }
```

- [ ] **Step 2: Run to verify it fails**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS0117: 'AssemblyAiOptions' does not contain a definition for 'Disfluencies'`.

- [ ] **Step 3: Implement**

In `src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs`, after the `LanguageCode` property, add:

```csharp
    // Include filler words ("um", "uh") in the transcript verbatim. Off by
    // default: dictation output should be clean. Eval reference generation
    // turns this on so local models are not penalized for transcribing fillers.
    public bool Disfluencies { get; init; } = false;
```

In `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs` line 73, change:

```csharp
            ["disfluencies"] = false,
```

to:

```csharp
            ["disfluencies"] = _opts.Disfluencies,
```

- [ ] **Step 4: Run tests to verify pass (new test AND the existing default-false assertion)**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.AssemblyAiClientTests
```

Expected: PASS, `Failed: 0` (including `CreateTranscript_SendsSpeechModelPayload_NoVocab_NoWordBoost` still asserting `"disfluencies":false` with default options).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add src/Winpepper.Asr/Transcription/AssemblyAiOptions.cs \
        src/Winpepper.Asr/Transcription/AssemblyAiClient.cs \
        tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "feat(asr): configurable disfluencies option on the AssemblyAI batch client (default unchanged)"
```

---

### Task 5: Reference planning logic

**Files:**
- Create: `scripts/asr-eval-corpus/ReferencePlanner.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (one more link)
- Test: `tests/Winpepper.Asr.Tests/ReferencePlannerTests.cs`

**Interfaces:**
- Consumes: `CorpusEntry` (Task 1).
- Produces (used by Tasks 6 and 10):
  - `AsrEvalCorpus.ReferenceAction` — enum `{ Skip, WriteEmpty, Transcribe }`
  - `AsrEvalCorpus.ReferencePlanner.ReferencePath(string corpusDir, CorpusEntry entry)` → `string` (`<corpusDir>/clips/<id>.reference.txt`)
  - `AsrEvalCorpus.ReferencePlanner.Decide(CorpusEntry entry, bool referenceExists, bool force)` → `ReferenceAction`

- [ ] **Step 1: Write the failing tests + csproj link**

Add to the test csproj `<ItemGroup>`:

```xml
    <Compile Include="..\..\scripts\asr-eval-corpus\ReferencePlanner.cs" Link="Corpus\ReferencePlanner.cs" />
```

Create `tests/Winpepper.Asr.Tests/ReferencePlannerTests.cs`:

```csharp
using AsrEvalCorpus;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class ReferencePlannerTests
{
    private static CorpusEntry Entry(bool expectedSilent = false, bool exclude = false) =>
        new("abc123", new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc), 1000, "clips/abc123.wav",
            "r", "c", "m", "n", new ClipTimings(1, 1, 1, 1, 1))
        { ExpectedSilent = expectedSilent, Exclude = exclude };

    [Fact]
    public void Decide_ExcludedClip_IsAlwaysSkipped()
    {
        ReferencePlanner.Decide(Entry(exclude: true), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(exclude: true), referenceExists: false, force: true)
            .ShouldBe(ReferenceAction.Skip);
    }

    [Fact]
    public void Decide_ExpectedSilent_WritesEmptyUnlessAlreadyPresent()
    {
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.WriteEmpty);
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: true, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(expectedSilent: true), referenceExists: true, force: true)
            .ShouldBe(ReferenceAction.WriteEmpty);
    }

    [Fact]
    public void Decide_NormalClip_TranscribesWhenMissingOrForced()
    {
        ReferencePlanner.Decide(Entry(), referenceExists: false, force: false)
            .ShouldBe(ReferenceAction.Transcribe);
        ReferencePlanner.Decide(Entry(), referenceExists: true, force: false)
            .ShouldBe(ReferenceAction.Skip);
        ReferencePlanner.Decide(Entry(), referenceExists: true, force: true)
            .ShouldBe(ReferenceAction.Transcribe);
    }

    [Fact]
    public void ReferencePath_SitsNextToTheClip()
    {
        var path = ReferencePlanner.ReferencePath("/corpus", Entry());

        path.Replace('\\', '/').ShouldBe("/corpus/clips/abc123.reference.txt");
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001` for `ReferencePlanner.cs`.

- [ ] **Step 3: Implement**

Create `scripts/asr-eval-corpus/ReferencePlanner.cs`:

```csharp
using System.IO;

namespace AsrEvalCorpus;

public enum ReferenceAction
{
    Skip,
    WriteEmpty,
    Transcribe,
}

public static class ReferencePlanner
{
    /// <summary>The reference transcript sits next to the clip: clips/&lt;id&gt;.reference.txt.</summary>
    public static string ReferencePath(string corpusDir, CorpusEntry entry)
        => Path.Combine(corpusDir, Path.ChangeExtension(entry.WavPath, ".reference.txt"));

    public static ReferenceAction Decide(CorpusEntry entry, bool referenceExists, bool force)
    {
        if (entry.Exclude) return ReferenceAction.Skip;
        if (referenceExists && !force) return ReferenceAction.Skip;
        return entry.ExpectedSilent ? ReferenceAction.WriteEmpty : ReferenceAction.Transcribe;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.ReferencePlannerTests
```

Expected: PASS (4 tests).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-eval-corpus/ReferencePlanner.cs tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/ReferencePlannerTests.cs
git commit -m "feat(eval): reference planning -- skip/write-empty/transcribe decisions with --force"
```

---

### Task 6: Reference transcript generator command

**Files:**
- Modify: `scripts/asr-eval-corpus/Program.cs` (add `references` command)

**Interfaces:**
- Consumes: `ReferencePlanner` (Task 5), `AssemblyAiOptions.Disfluencies` (Task 4), `AssemblyAiTranscriber`/`AssemblyAiClient`/`AssemblyAiModels`/`IAssemblyAiKeyStore` from `Winpepper.Asr.Transcription` — including the transcriber's injectable `scheduleDetached` constructor hook (`Action<Func<Task>>`, `AssemblyAiTranscriber.cs:30`; default `a => _ = Task.Run(a)` at `:38`, i.e. fire-and-forget), `BenchAudio.ReadMono16k` (linked file, namespace `AsrLatencyBench`).
- Produces: the `references` CLI command; `clips/<id>.reference.txt` files. Failure contract: a failed clip is recorded and the command CONTINUES to the next clip, prints a final `N written, M skipped, K failed` summary, and exits non-zero if any failed; re-running retries only missing/failed references (existing ones are skipped). Remote transcript deletes are drained deterministically before exit.

- [ ] **Step 1: Implement the references command**

In `scripts/asr-eval-corpus/Program.cs`:

(a) Extend the usings at the top:

```csharp
using System.Globalization;
using AsrEvalCorpus;
using AsrLatencyBench;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Asr.Transcription;
```

(b) Delete the `_ = force;` line, and add a `references` case to the command switch (before `default:`):

```csharp
    case "references":
        return await RunReferences(corpusDir, force);
```

(c) Add below `RunExport`:

```csharp
static async Task<int> RunReferences(string corpusDir, bool force)
{
    var key = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY");
    if (string.IsNullOrWhiteSpace(key))
    {
        Console.Error.WriteLine(
            "references: ASSEMBLYAI_API_KEY is not set. Set it and re-run -- reference transcripts are never fabricated.");
        return 1;
    }
    var manifestPath = Path.Combine(corpusDir, "manifest.json");
    if (!File.Exists(manifestPath))
    {
        Console.Error.WriteLine($"references: no manifest at {manifestPath} (run export first)");
        return 1;
    }
    var manifest = CorpusManifest.Load(manifestPath);

    var opts = new AssemblyAiOptions
    {
        Model = AssemblyAiModels.DefaultId,        // "universal-3-5-pro" -- the options default is universal-2!
        Disfluencies = true,                       // KEEP "um"/"uh": local models transcribe fillers verbatim
        CloudDeadline = TimeSpan.FromSeconds(120), // dictation clips, but allow slow batch turnaround
        PollInterval = TimeSpan.FromSeconds(2),
        DeleteAfterTranscribe = true,              // do not leave transcripts on AssemblyAI's servers
    };
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    var client = new AssemblyAiClient(http, () => key, opts, NullLogger<AssemblyAiClient>.Instance);
    // DeleteAfterTranscribe deletes are DETACHED in the transcriber: its default
    // scheduleDetached is `a => _ = Task.Run(a)` (AssemblyAiTranscriber.cs:38), which
    // would race process exit in this short-lived CLI. Capture the delete task via the
    // injectable scheduleDetached hook (ctor param, AssemblyAiTranscriber.cs:30) and
    // await it per clip below, so every remote transcript delete deterministically
    // completes before exit. The transcriber gets a console warning logger so a failed
    // delete (logged inside ScheduleDelete, AssemblyAiTranscriber.cs:94) is visible.
    Task? pendingDelete = null;
    var transcriber = new AssemblyAiTranscriber(
        client, new EnvKeyStore(), opts, new ConsoleWarnLogger<AssemblyAiTranscriber>(),
        scheduleDetached: work => pendingDelete = work());

    var written = 0;
    var skipped = 0;
    var failed = 0;
    foreach (var entry in manifest.Entries)
    {
        var refPath = ReferencePlanner.ReferencePath(corpusDir, entry);
        switch (ReferencePlanner.Decide(entry, File.Exists(refPath), force))
        {
            case ReferenceAction.Skip:
                skipped++;
                break;
            case ReferenceAction.WriteEmpty:
                File.WriteAllText(refPath, "");
                written++;
                Console.WriteLine($"references[{entry.Id}]: expected-silent -> empty reference");
                break;
            case ReferenceAction.Transcribe:
                try
                {
                    var audio = BenchAudio.ReadMono16k(Path.Combine(corpusDir, entry.WavPath));
                    var result = await transcriber.TranscribeAsync(audio, CancellationToken.None);
                    File.WriteAllText(refPath, result.Text.TrimEnd() + Environment.NewLine);
                    written++;
                    Console.WriteLine($"references[{entry.Id}]: ok ({result.Text.Length} chars)");
                    if (pendingDelete is not null)
                    {
                        // Drain the remote-transcript delete NOW, per clip: ScheduleDelete's
                        // own try/catch means this never throws; a failed delete surfaces as
                        // a [Warning] line from ConsoleWarnLogger, not an exception.
                        await pendingDelete;
                        pendingDelete = null;
                        Console.WriteLine($"references[{entry.Id}]: remote transcript delete drained");
                    }
                }
                catch (Exception ex)
                {
                    // Covers transport failures AND transcripts that complete with
                    // status "error" -- AssemblyAiTranscriber.cs:73-74 throws
                    // AssemblyAiException for those, e.g. "Audio duration is too
                    // short." (documented minimum 160 ms; the 0.51 s corpus floor
                    // clears it, but handle it anyway). The loop CONTINUES to the
                    // next clip; no reference file is written for this one.
                    failed++;
                    Console.Error.WriteLine($"references[{entry.Id}]: FAILED {ex.Message}");
                }
                break;
        }
    }
    Console.WriteLine($"references: {written} written, {skipped} skipped, {failed} failed");
    // Non-zero exit when any clip failed. Re-running retries ONLY missing/failed
    // references: Decide skips clips whose reference file already exists, and a
    // failed clip never wrote one -- idempotent by construction.
    return failed == 0 ? 0 : 1;
}
```

(d) Add at the very bottom of the file (types must follow top-level statements):

```csharp
/// <summary>Presence gate only; the real key goes to AssemblyAiClient via the () => key closure.</summary>
sealed class EnvKeyStore : Winpepper.Asr.Transcription.IAssemblyAiKeyStore
{
    public bool HasKey => true;
    public void Save(string apiKey) { }
    public string? Load() => null;
    public void Clear() { }
}

/// <summary>Warning+ console logger so the transcriber's non-fatal warnings
/// (notably a failed remote transcript delete) are visible in this CLI instead
/// of being swallowed by NullLogger. Never logs the API key (the transcriber
/// already guarantees that).</summary>
sealed class ConsoleWarnLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
            Console.Error.WriteLine($"[{logLevel}] {formatter(state, exception)}");
    }
}
```

- [ ] **Step 2: Build**

```bash
dotnet build scripts/asr-eval-corpus/AsrEvalCorpus.csproj -c Release
```

Expected: `Build succeeded.`

- [ ] **Step 3: Prove the no-key path (real E2E of the guard)**

```bash
env -u ASSEMBLYAI_API_KEY dotnet run --project scripts/asr-eval-corpus -c Release -- references \
  --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1; echo "exit=$?"
```

Expected: the exact error line `references: ASSEMBLYAI_API_KEY is not set. Set it and re-run -- reference transcripts are never fabricated.` and `exit=1`. No reference files created.

- [ ] **Step 4: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-eval-corpus/Program.cs
git commit -m "feat(eval): AssemblyAI reference transcript generator (universal-3-5-pro, disfluencies kept, idempotent)"
```

(Real reference generation against the API happens in Task 12, where the key availability branch is handled.)

---

### Task 7: WER / CER metrics and silent-clip scoring

**Files:**
- Create: `scripts/asr-latency-bench/EvalMetrics.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (one more link)
- Test: `tests/Winpepper.Asr.Tests/EvalMetricsTests.cs`

**Interfaces:**
- Consumes: `AsrLatencyBench.TranscriptDiff.Normalize` (existing, unchanged — lowercase, strip punctuation keeping apostrophes, collapse whitespace).
- Produces (used by Tasks 9-10):
  - `AsrLatencyBench.ErrorRate(int Substitutions, int Insertions, int Deletions, int ReferenceLength)` — record with `int Edits` and `double Rate` (empty reference: 0.0 if hypothesis empty too, else 1.0)
  - `AsrLatencyBench.EvalMetrics.Wer(string referenceText, string hypothesisText)` → `ErrorRate`
  - `AsrLatencyBench.EvalMetrics.Cer(string referenceText, string hypothesisText)` → `ErrorRate`
  - `AsrLatencyBench.EvalMetrics.SilentPass(string hypothesisText)` → `bool`

- [ ] **Step 1: Write the failing tests + csproj link**

Add to the test csproj `<ItemGroup>`:

```xml
    <Compile Include="..\..\scripts\asr-latency-bench\EvalMetrics.cs" Link="Bench\EvalMetrics.cs" />
```

Create `tests/Winpepper.Asr.Tests/EvalMetricsTests.cs`:

```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalMetricsTests
{
    [Fact]
    public void Wer_IdenticalAfterNormalization_IsZero()
    {
        var r = EvalMetrics.Wer("Hello, world!", "hello world");

        r.Rate.ShouldBe(0.0);
        r.ReferenceLength.ShouldBe(2);
    }

    [Fact]
    public void Wer_OneSubstitution_IsOneOverReferenceLength()
    {
        var r = EvalMetrics.Wer("the cat sat", "the dog sat");

        r.Substitutions.ShouldBe(1);
        r.Insertions.ShouldBe(0);
        r.Deletions.ShouldBe(0);
        r.Rate.ShouldBe(1.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_PureInsertion_IsCountedAsInsertion()
    {
        // ref: a b   hyp: a x b   -> insert "x" (1 edit over 2 ref words)
        var r = EvalMetrics.Wer("a b", "a x b");

        r.Insertions.ShouldBe(1);
        r.Substitutions.ShouldBe(0);
        r.Deletions.ShouldBe(0);
        r.Rate.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_MixedEdits_TotalEditCountIsMinimal()
    {
        // ref: a b c   hyp: a x b   -> two optimal alignments exist (insert+delete
        // or two substitutions); either way the minimal edit count is 2.
        var r = EvalMetrics.Wer("a b c", "a x b");

        r.Edits.ShouldBe(2);
        r.Rate.ShouldBe(2.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_DroppedFillerWord_CountsAsDeletion()
    {
        // References keep disfluencies; a model that drops "um" pays for it.
        var r = EvalMetrics.Wer("um hello", "hello");

        r.Deletions.ShouldBe(1);
        r.Rate.ShouldBe(0.5, tolerance: 1e-9);
    }

    [Fact]
    public void Wer_EmptyReference_NonEmptyHypothesis_IsOne()
    {
        EvalMetrics.Wer("", "hello there").Rate.ShouldBe(1.0);
    }

    [Fact]
    public void Wer_EmptyReferenceAndHypothesis_IsZero()
    {
        EvalMetrics.Wer("", "...").Rate.ShouldBe(0.0); // "..." normalizes to empty
    }

    [Fact]
    public void Cer_SingleCharacterError_IsOneOverReferenceChars()
    {
        var r = EvalMetrics.Cer("abc", "abd");

        r.Rate.ShouldBe(1.0 / 3.0, tolerance: 1e-9);
    }

    [Fact]
    public void SilentPass_PunctuationOrEmpty_IsTrue_WordsAreFalse()
    {
        EvalMetrics.SilentPass("").ShouldBeTrue();
        EvalMetrics.SilentPass(" . , ! ").ShouldBeTrue();
        EvalMetrics.SilentPass("hm").ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001` for `EvalMetrics.cs`.

- [ ] **Step 3: Implement**

Create `scripts/asr-latency-bench/EvalMetrics.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AsrLatencyBench;

public sealed record ErrorRate(int Substitutions, int Insertions, int Deletions, int ReferenceLength)
{
    public int Edits => Substitutions + Insertions + Deletions;

    /// <summary>Edits over reference length. Empty reference: 0.0 when the
    /// hypothesis is also empty, else 1.0.</summary>
    public double Rate => ReferenceLength == 0
        ? (Edits == 0 ? 0.0 : 1.0)
        : (double)Edits / ReferenceLength;
}

/// <summary>
/// Word and character error rates against a reference transcript, computed on
/// TranscriptDiff.Normalize output (lowercase, punctuation stripped with
/// apostrophes kept, whitespace collapsed). Deliberately no number-word
/// normalization: digits stay digits for the reference and every candidate
/// model alike, so relative ranking is unaffected. BCL-only so the same file
/// compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalMetrics
{
    public static ErrorRate Wer(string referenceText, string hypothesisText)
        => Align(Tokens(referenceText), Tokens(hypothesisText));

    public static ErrorRate Cer(string referenceText, string hypothesisText)
        => Align(Chars(referenceText), Chars(hypothesisText));

    /// <summary>Expected-silent clips pass when the model produced no words.</summary>
    public static bool SilentPass(string hypothesisText)
        => TranscriptDiff.Normalize(hypothesisText).Length == 0;

    private static string[] Tokens(string text)
        => TranscriptDiff.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static char[] Chars(string text)
        => TranscriptDiff.Normalize(text).Replace(" ", "").ToCharArray();

    private static ErrorRate Align<T>(IReadOnlyList<T> reference, IReadOnlyList<T> hypothesis)
        where T : IEquatable<T>
    {
        var n = reference.Count;
        var m = hypothesis.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var sub = d[i - 1, j - 1] + (reference[i - 1].Equals(hypothesis[j - 1]) ? 0 : 1);
                d[i, j] = Math.Min(sub, Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1));
            }
        }

        int subs = 0, ins = 0, dels = 0, x = n, y = m;
        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && reference[x - 1].Equals(hypothesis[y - 1]) && d[x, y] == d[x - 1, y - 1])
            {
                x--; y--;
            }
            else if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + 1)
            {
                subs++; x--; y--;
            }
            else if (x > 0 && d[x, y] == d[x - 1, y] + 1)
            {
                dels++; x--;
            }
            else
            {
                ins++; y--;
            }
        }
        return new ErrorRate(subs, ins, dels, n);
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.EvalMetricsTests
```

Expected: PASS (9 tests).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalMetrics.cs tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/EvalMetricsTests.cs
git commit -m "feat(bench): word and character error rate metrics plus silent-clip scoring"
```

---

### Task 8: Production-fidelity frame segmentation

**Files:**
- Create: `scripts/asr-latency-bench/EvalFraming.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (one more link)
- Test: `tests/Winpepper.Asr.Tests/EvalFramingTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (used by Task 10):
  - `AsrLatencyBench.EvalFraming.PrerollSamples` — `const int` 8000 (500 ms @ 16 kHz)
  - `AsrLatencyBench.EvalFraming.FrameSamples` — `const int` 800 (50 ms @ 16 kHz)
  - `AsrLatencyBench.EvalFraming.Segments(int totalSamples)` → `List<(int Offset, int Length)>` — one preroll burst first, then steady 50 ms frames

- [ ] **Step 1: Write the failing tests + csproj link**

Add to the test csproj `<ItemGroup>`:

```xml
    <Compile Include="..\..\scripts\asr-latency-bench\EvalFraming.cs" Link="Bench\EvalFraming.cs" />
```

Create `tests/Winpepper.Asr.Tests/EvalFramingTests.cs`:

```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalFramingTests
{
    [Fact]
    public void Segments_ClipShorterThanPreroll_IsOneBurst()
    {
        EvalFraming.Segments(5000).ShouldBe(new[] { (0, 5000) });
    }

    [Fact]
    public void Segments_ExactPrerollLength_IsOneBurst()
    {
        EvalFraming.Segments(8000).ShouldBe(new[] { (0, 8000) });
    }

    [Fact]
    public void Segments_LongClip_PrerollBurstThenSteady50msFramesWithRemainder()
    {
        var segs = EvalFraming.Segments(10000);

        segs.ShouldBe(new[] { (0, 8000), (8000, 800), (8800, 800), (9600, 400) });
    }

    [Fact]
    public void Segments_CoverEverySampleExactlyOnce()
    {
        var segs = EvalFraming.Segments(48123);

        segs[0].ShouldBe((0, 8000));
        var covered = 0;
        foreach (var (offset, length) in segs)
        {
            offset.ShouldBe(covered);
            covered += length;
        }
        covered.ShouldBe(48123);
    }

    [Fact]
    public void Segments_ZeroSamples_IsEmpty()
    {
        EvalFraming.Segments(0).ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001` for `EvalFraming.cs`.

- [ ] **Step 3: Implement**

Create `scripts/asr-latency-bench/EvalFraming.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace AsrLatencyBench;

/// <summary>
/// Replays a stored clip the way production capture delivers audio: one
/// ~500 ms preroll burst at session start (WarmWasapiRecorder drains the warm
/// ring as a single FramesAvailable event; PipelineHost StartSession uses
/// includePrerollMs: 500), then steady 50 ms frames. BCL-only so the same
/// file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalFraming
{
    public const int PrerollSamples = 8000; // 500 ms @ 16 kHz
    public const int FrameSamples = 800;    // 50 ms @ 16 kHz

    public static List<(int Offset, int Length)> Segments(int totalSamples)
    {
        var segments = new List<(int, int)>();
        if (totalSamples <= 0) return segments;
        var first = Math.Min(PrerollSamples, totalSamples);
        segments.Add((0, first));
        for (var offset = first; offset < totalSamples; offset += FrameSamples)
            segments.Add((offset, Math.Min(FrameSamples, totalSamples - offset)));
        return segments;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.EvalFramingTests
```

Expected: PASS (5 tests).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalFraming.cs tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/EvalFramingTests.cs
git commit -m "feat(bench): preroll-burst plus 50 ms frame segmentation matching production capture"
```

---

### Task 9: Eval results model, aggregation, and writers

**Files:**
- Create: `scripts/asr-latency-bench/EvalResults.cs`
- Modify: `tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj` (one more link)
- Test: `tests/Winpepper.Asr.Tests/EvalResultsTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces (used by Task 10):
  - `AsrLatencyBench.ClipResult(string Id, double AudioSeconds, bool ExpectedSilent, bool HasReference, string Reference, string StreamText, string BatchText, double? Wer, double? Cer, bool? SilentPass, IReadOnlyList<long> FinishMsRuns, bool FellBack, bool Truncated, bool TrimmedSilent, string BatchParityDiff, string? Error = null)` — record; `Error` is `null` for normal rows; a failed clip carries a short exception type/message (results.json only — results.md shows just an ERROR marker) with empty texts and null metrics
  - `AsrLatencyBench.EvalRunInfo(string Corpus, string SpeechModel, string TranscribeCppVersion, string DateUtc, int Repeats)` — record
  - `AsrLatencyBench.EvalSummary(int ClipCount, int ScoredCount, double? MeanWer, double? MedianWer, double? MeanCer, long LatencyP50Ms, long LatencyP90Ms, long LatencyMaxMs, int FallbackCount, int TruncatedCount, int SilentClipCount, int SilentPassCount, int FailedCount)` — record
  - `AsrLatencyBench.EvalResults.Summarize(IReadOnlyList<ClipResult>)` → `EvalSummary`
  - `AsrLatencyBench.EvalResults.ToJson(EvalRunInfo, IReadOnlyList<ClipResult>, EvalSummary)` → `string`
  - `AsrLatencyBench.EvalResults.ToMarkdown(EvalRunInfo, IReadOnlyList<ClipResult>, EvalSummary)` → `string` (numbers + ids only, NO transcript text)

- [ ] **Step 1: Write the failing tests + csproj link**

Add to the test csproj `<ItemGroup>`:

```xml
    <Compile Include="..\..\scripts\asr-latency-bench\EvalResults.cs" Link="Bench\EvalResults.cs" />
```

Create `tests/Winpepper.Asr.Tests/EvalResultsTests.cs`:

```csharp
using AsrLatencyBench;
using Shouldly;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class EvalResultsTests
{
    private static ClipResult Clip(
        string id, double? wer = null, double? cer = null, bool? silentPass = null,
        bool expectedSilent = false, long[]? runsMs = null, bool fellBack = false, bool truncated = false) =>
        new(id, 3.0, expectedSilent, HasReference: wer is not null,
            Reference: "secret reference words", StreamText: "secret stream words", BatchText: "secret batch words",
            wer, cer, silentPass, runsMs ?? new long[] { 100 }, fellBack, truncated,
            TrimmedSilent: false, BatchParityDiff: "IDENTICAL");

    private static readonly EvalRunInfo Info = new("corpus-v1", "model-x", "0.1.3", "2026-07-26", 1);

    [Fact]
    public void Summarize_ComputesMeansMediansPercentilesAndCounts()
    {
        var clips = new[]
        {
            Clip("a", wer: 0.10, cer: 0.05, runsMs: new long[] { 100, 200 }),
            Clip("b", wer: 0.30, cer: 0.15, runsMs: new long[] { 300, 400 }, fellBack: true, truncated: true),
            Clip("c", silentPass: true, expectedSilent: true, runsMs: new long[] { 0 }),
        };

        var s = EvalResults.Summarize(clips);

        s.ClipCount.ShouldBe(3);
        s.ScoredCount.ShouldBe(2);
        s.MeanWer!.Value.ShouldBe(0.20, tolerance: 1e-9);
        s.MedianWer!.Value.ShouldBe(0.10, tolerance: 1e-9);
        s.MeanCer!.Value.ShouldBe(0.10, tolerance: 1e-9);
        s.LatencyP50Ms.ShouldBe(200);    // 0 ms silent-skip runs are excluded
        s.LatencyMaxMs.ShouldBe(400);
        s.FallbackCount.ShouldBe(1);
        s.TruncatedCount.ShouldBe(1);
        s.SilentClipCount.ShouldBe(1);
        s.SilentPassCount.ShouldBe(1);
    }

    [Fact]
    public void Summarize_NoScoredClips_YieldsNullRates()
    {
        var s = EvalResults.Summarize(new[] { Clip("a") });

        s.MeanWer.ShouldBeNull();
        s.MedianWer.ShouldBeNull();
        s.MeanCer.ShouldBeNull();
    }

    [Fact]
    public void ToMarkdown_HasPerClipRowsAndSummary_ButNoTranscriptText()
    {
        var clips = new[] { Clip("clip1", wer: 0.25, cer: 0.10) };

        var md = EvalResults.ToMarkdown(Info, clips, EvalResults.Summarize(clips));

        md.ShouldContain("corpus-v1");
        md.ShouldContain("model-x");
        md.ShouldContain("0.1.3");
        md.ShouldContain("| clip1 |");
        md.ShouldContain("0.250");
        md.ShouldContain("**Summary:**");
        md.ShouldNotContain("secret");   // transcripts/references never leak into markdown
    }

    [Fact]
    public void ToJson_CarriesFullTranscriptsAndRoundTripFields()
    {
        var clips = new[] { Clip("clip1", wer: 0.25) };

        var json = EvalResults.ToJson(Info, clips, EvalResults.Summarize(clips));

        json.ShouldContain("\"corpus\": \"corpus-v1\"");
        json.ShouldContain("\"secret reference words\"");
        json.ShouldContain("\"wer\": 0.25");
        json.ShouldContain("\"finishMsRuns\"");
    }

    [Fact]
    public void FailedClip_CountedInSummary_MarkedInMarkdownWithoutErrorText()
    {
        // Error rows (per-clip failures in the corpus run) have empty texts and
        // null metrics; results.md shows only an ERROR marker + counts.
        var clips = new[]
        {
            Clip("ok", wer: 0.10, cer: 0.05),
            new ClipResult("bad", 0.0, ExpectedSilent: false, HasReference: false,
                Reference: "", StreamText: "", BatchText: "", Wer: null, Cer: null, SilentPass: null,
                FinishMsRuns: Array.Empty<long>(), FellBack: false, Truncated: false,
                TrimmedSilent: false, BatchParityDiff: "", Error: "TranscribeCppException: secret failure details"),
        };

        var s = EvalResults.Summarize(clips);

        s.ClipCount.ShouldBe(2);
        s.ScoredCount.ShouldBe(1);
        s.FailedCount.ShouldBe(1);

        var md = EvalResults.ToMarkdown(Info, clips, s);
        md.ShouldContain("| bad |");
        md.ShouldContain("ERROR");
        md.ShouldContain("Failed: 1");
        md.ShouldNotContain("secret failure details"); // exception text stays out of results.md
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected: **FAIL** with `CS2001` for `EvalResults.cs`.

- [ ] **Step 3: Implement**

Create `scripts/asr-latency-bench/EvalResults.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AsrLatencyBench;

public sealed record ClipResult(
    string Id,
    double AudioSeconds,
    bool ExpectedSilent,
    bool HasReference,
    string Reference,
    string StreamText,
    string BatchText,
    double? Wer,
    double? Cer,
    bool? SilentPass,
    IReadOnlyList<long> FinishMsRuns,
    bool FellBack,
    bool Truncated,
    bool TrimmedSilent,
    string BatchParityDiff,
    string? Error = null); // non-null = per-clip failure row (empty texts, null metrics); text goes to results.json only

public sealed record EvalRunInfo(
    string Corpus, string SpeechModel, string TranscribeCppVersion, string DateUtc, int Repeats);

public sealed record EvalSummary(
    int ClipCount,
    int ScoredCount,
    double? MeanWer,
    double? MedianWer,
    double? MeanCer,
    long LatencyP50Ms,
    long LatencyP90Ms,
    long LatencyMaxMs,
    int FallbackCount,
    int TruncatedCount,
    int SilentClipCount,
    int SilentPassCount,
    int FailedCount);

public sealed record EvalReport(EvalRunInfo Info, EvalSummary Summary, IReadOnlyList<ClipResult> Clips);

/// <summary>
/// Corpus eval aggregation and rendering. results.md deliberately contains NO
/// transcript or reference text (safe to quote in committed docs); results.json
/// carries the full text and diffs and must stay out of git (artifacts/ only).
/// BCL-only so the same file compiles into Winpepper.Asr.Tests.
/// </summary>
public static class EvalResults
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static double Percentile(IReadOnlyList<double> sortedAscending, double q)
    {
        if (sortedAscending.Count == 0) return 0;
        var idx = (int)Math.Ceiling(q * sortedAscending.Count) - 1;
        return sortedAscending[Math.Clamp(idx, 0, sortedAscending.Count - 1)];
    }

    public static EvalSummary Summarize(IReadOnlyList<ClipResult> clips)
    {
        var wers = clips.Where(c => c.Wer is not null).Select(c => c.Wer!.Value).OrderBy(v => v).ToArray();
        var cers = clips.Where(c => c.Cer is not null).Select(c => c.Cer!.Value).ToArray();
        // 0 ms runs are silent-trimmed clips that never reached FinishAsync; exclude them.
        var latencies = clips.SelectMany(c => c.FinishMsRuns).Where(ms => ms > 0)
            .Select(ms => (double)ms).OrderBy(v => v).ToArray();
        var silent = clips.Where(c => c.ExpectedSilent).ToArray();
        return new EvalSummary(
            ClipCount: clips.Count,
            ScoredCount: wers.Length,
            MeanWer: wers.Length == 0 ? null : wers.Average(),
            MedianWer: wers.Length == 0 ? null : Percentile(wers, 0.5),
            MeanCer: cers.Length == 0 ? null : cers.Average(),
            LatencyP50Ms: (long)Percentile(latencies, 0.5),
            LatencyP90Ms: (long)Percentile(latencies, 0.9),
            LatencyMaxMs: latencies.Length == 0 ? 0 : (long)latencies[^1],
            FallbackCount: clips.Count(c => c.FellBack),
            TruncatedCount: clips.Count(c => c.Truncated),
            SilentClipCount: silent.Length,
            SilentPassCount: silent.Count(c => c.SilentPass == true),
            FailedCount: clips.Count(c => c.Error is not null));
    }

    public static string ToJson(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary)
        => JsonSerializer.Serialize(new EvalReport(info, summary, clips), JsonOpts);

    public static string ToMarkdown(EvalRunInfo info, IReadOnlyList<ClipResult> clips, EvalSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# ASR corpus eval: {info.Corpus}");
        sb.AppendLine();
        sb.AppendLine($"- speech model: `{info.SpeechModel}`");
        sb.AppendLine($"- transcribe.cpp: `{info.TranscribeCppVersion}`");
        sb.AppendLine($"- date: {info.DateUtc}, repeats: {info.Repeats}");
        sb.AppendLine();
        sb.AppendLine("| clip | audio (s) | WER | CER | silent | post-stop ms (runs) | fellBack | truncated | error |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var c in clips)
        {
            if (c.Error is not null)
            {
                // Ids and a marker only -- the exception text stays in results.json.
                sb.AppendLine($"| {c.Id} | - | - | - | - | - | - | - | ERROR |");
                continue;
            }
            var werCell = c.Wer is not null ? c.Wer.Value.ToString("F3") : (c.ExpectedSilent ? "-" : "no ref");
            var cerCell = c.Cer is not null ? c.Cer.Value.ToString("F3") : "-";
            var silentCell = c.SilentPass is null ? "-" : (c.SilentPass.Value ? "PASS" : "FAIL");
            sb.AppendLine($"| {c.Id} | {c.AudioSeconds:F1} | {werCell} | {cerCell} | {silentCell} | " +
                          $"{string.Join(" ", c.FinishMsRuns)} | {c.FellBack} | {c.Truncated} | - |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Summary:** {summary.ClipCount} clips ({summary.ScoredCount} scored). " +
            $"WER mean {Fmt(summary.MeanWer)} / median {Fmt(summary.MedianWer)}; CER mean {Fmt(summary.MeanCer)}. " +
            $"Post-stop latency p50 {summary.LatencyP50Ms} ms, p90 {summary.LatencyP90Ms} ms, max {summary.LatencyMaxMs} ms. " +
            $"Fallbacks: {summary.FallbackCount}. Truncations: {summary.TruncatedCount}. " +
            $"Silent clips: {summary.SilentPassCount}/{summary.SilentClipCount} pass. " +
            $"Failed: {summary.FailedCount}.");
        return sb.ToString();

        static string Fmt(double? v) => v is null ? "n/a" : v.Value.ToString("F3");
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait "Platform=Windows" -class Winpepper.Asr.Tests.EvalResultsTests
```

Expected: PASS (5 tests).

- [ ] **Step 5: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/asr-latency-bench/EvalResults.cs tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj \
        tests/Winpepper.Asr.Tests/EvalResultsTests.cs
git commit -m "feat(bench): eval results model with aggregate summary, JSON and privacy-safe markdown writers"
```

---

### Task 10: Bench `corpus` scenario with production-fidelity streaming

**Files:**
- Modify: `scripts/asr-latency-bench/AsrLatencyBench.csproj`
- Modify: `scripts/asr-latency-bench/Program.cs` (arg flags ~line 28-40; new `case` before `default:` in the scenario switch ~line 287; two helper classes near `ProbeTranscriber` ~line 357)

**Interfaces:**
- Consumes: `EvalFraming` (Task 8), `EvalMetrics` (Task 7), `EvalResults`/`ClipResult`/`EvalRunInfo` (Task 9), `AsrEvalCorpus.CorpusManifest`/`ReferencePlanner` (Tasks 1, 5), `Winpepper.Audio.SilenceTrimmer.Trim(ReadOnlySpan<float>)` → `TrimResult { float[] Trimmed; bool IsSilent; ... }`, existing bench types (`ProbeTranscriber`, `BenchAudio`, `TranscriptDiff`), production classes (`TranscribeCppEngine.Load(...)`, `NemotronStreamingTranscriber(Func<ITranscribeCppEngine>, ITranscriber, string, ILogger?, int)`, `StreamingDictationSession.Start(...)/OnFrame/FinishAsync`), and `Winpepper.Asr.TranscribeCpp.TranscribeCppContract.RequiredVersion` (the pinned transcribe.cpp version constant — the pin documented at `TranscribeCppEngine.cs:7` lives at `TranscribeCppContract.cs:21`).
- Produces: the `corpus` scenario: `AsrLatencyBench.dll corpus --corpus <dir> --nemotron-model <gguf> --nemotron-runtime <dir> [--repeats N] [--out <dir>]`, writing `results.json` + `results.md` (default `--out` is gitignored `artifacts/asr-eval-results`). Failure contract: a failed clip becomes an error row, both files are ALWAYS written after the loop, and the process exits non-zero (via `Environment.ExitCode`) when any clip failed.

- [ ] **Step 1: Wire the csproj**

In `scripts/asr-latency-bench/AsrLatencyBench.csproj`, extend the `<ItemGroup>`:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Asr\Winpepper.Asr.csproj" />
    <ProjectReference Include="..\..\src\Winpepper.Audio\Winpepper.Audio.csproj" />
    <Compile Include="..\asr-eval-corpus\CorpusManifest.cs" Link="Corpus\CorpusManifest.cs" />
    <Compile Include="..\asr-eval-corpus\ReferencePlanner.cs" Link="Corpus\ReferencePlanner.cs" />
  </ItemGroup>
```

(`Winpepper.Audio` is dual-TFM `net9.0;net9.0-windows...`; this plain `net9.0` consumer picks the `net9.0` flavor, and `SilenceTrimmer` is fully managed.)

- [ ] **Step 2: Add the CLI flags**

In `scripts/asr-latency-bench/Program.cs`, next to the existing flag variables (`var gain = 1.0;` etc., ~line 22-27), add:

```csharp
string? corpusDir = null;
var outDir = "artifacts/asr-eval-results"; // default INSIDE gitignored artifacts/: results.json contains transcript text, and a bare "asr-eval-results/" is NOT gitignored
var repeats = 1;
```

In the arg-parsing `switch` (after `case "--lead-silence-ms"`), add:

```csharp
        case "--corpus": corpusDir = args[++argIdx]; break;
        case "--out": outDir = args[++argIdx]; break;
        case "--repeats": repeats = int.Parse(args[++argIdx], System.Globalization.CultureInfo.InvariantCulture); break;
```

(The default scenario list is unchanged — `corpus` must be requested explicitly, like `real-nemotron-stream`.)

- [ ] **Step 3: Add the `corpus` case**

In the scenario `switch`, before `default:`, add (model the structure on the existing `real-nemotron-stream` case at lines 205-286):

```csharp
        case "corpus":
        {
            if (corpusDir is null || nemotronModel is null || nemotronRuntime is null)
            {
                Console.WriteLine("corpus: SKIPPED (requires --corpus, --nemotron-model and --nemotron-runtime)");
                break;
            }
            var manifestPath = Path.Combine(corpusDir, "manifest.json");
            if (!File.Exists(manifestPath) || !File.Exists(nemotronModel)
                || !File.Exists(Path.Combine(nemotronRuntime, "transcribe.dll")))
            {
                Console.WriteLine("corpus: SKIPPED (manifest, model or runtime not found)");
                break;
            }
            var manifest = AsrEvalCorpus.CorpusManifest.Load(manifestPath);
            using var corpusEngine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-log: {msg}"));
            // SECOND engine instance (same model + runtime, its OWN compute gate),
            // used ONLY as the streaming sessions' batch fallback via
            // EngineBatchTranscriber. Do NOT "simplify" this back to one engine:
            // during FinishAsync the primary engine's native stream still HOLDS the
            // engine-wide SemaphoreSlim(1,1) compute gate (acquired
            // TranscribeCppEngine.cs:177, released only in NativeStream.Dispose at
            // :336, whose sole caller is Session.DisposeAsync() at
            // NemotronStreamingTranscriber.cs:174, which StreamingDictationSession
            // invokes only AFTER FinishAsync returns, StreamingDictationSession.cs:120-121).
            // A same-engine fallback awaited inside FinishAsync would stall 5 s at the
            // gate wait (TranscribeCppEngine.cs:235) and throw TranscribeCppException
            // on EVERY fallback clip. Cost: ~700 MB extra model RAM, bench-only, accepted.
            using var fallbackEngine = Winpepper.Asr.TranscribeCpp.TranscribeCppEngine.Load(
                nemotronRuntime, nemotronModel, msg => Console.WriteLine($"# nem-fallback-log: {msg}"));
            Console.WriteLine($"# corpus: engines loaded (primary + fallback), {manifest.Entries.Count} manifest entries, repeats={repeats}");

            var clipResults = new List<ClipResult>();
            foreach (var entry in manifest.Entries.Where(e => !e.Exclude))
            {
                // One bad clip must not destroy the whole eval: any per-clip failure
                // (including a null coordinator result) becomes an error row, and
                // results.json/results.md are still written after the loop.
                try
                {
                    var wavAudio = BenchAudio.ReadMono16k(Path.Combine(corpusDir, entry.WavPath));
                    var refPath = AsrEvalCorpus.ReferencePlanner.ReferencePath(corpusDir, entry);
                    var hasReference = File.Exists(refPath);
                    var referenceText = hasReference ? File.ReadAllText(refPath).Trim() : "";

                    // (a) batch parity reference: the PRIMARY engine, offline over the
                    //     full clip. Gate-safe: it runs before any streaming session
                    //     exists on this engine, so its compute gate is free.
                    var batchText = corpusEngine.TranscribeBatch(wavAudio);

                    // (b) production passes silence-trimmed audio to FinishAsync (PipelineHost.cs:554);
                    //     streamed frames stay untrimmed -- same asymmetry as production.
                    var trimResult = Winpepper.Audio.SilenceTrimmer.Trim(wavAudio);

                    var streamText = "";
                    var fellBack = false;
                    var truncated = false;
                    var finishRuns = new List<long>();
                    for (var run = 0; run < repeats; run++)
                    {
                        var runFellBack = false;
                        // Fallback runs on the SECOND engine -- see the fallbackEngine comment above.
                        var probe = new ProbeTranscriber(() => runFellBack = true, new EngineBatchTranscriber(fallbackEngine));
                        var nemLog = new ListLogger();
                        var streaming = new NemotronStreamingTranscriber(
                            () => corpusEngine, probe, "nemotron-streaming-en", nemLog);
                        await using var session = StreamingDictationSession.Start(
                            _ => Task.FromResult<IStreamingTranscriber?>(streaming),
                            NullLogger.Instance, CancellationToken.None, TimeSpan.FromSeconds(10));

                        // Production sends one ~500 ms preroll burst at session start, then
                        // steady 50 ms frames (WarmWasapiRecorder.cs:144-147, PipelineHost.cs:455).
                        // Stopwatch-scheduled pacing: steady frame s is due at s*50 ms, so
                        // cumulative timing stays true to real time (no Task.Delay drift).
                        var segments = EvalFraming.Segments(wavAudio.Length);
                        var pacer = Stopwatch.StartNew();
                        for (var s = 0; s < segments.Count; s++)
                        {
                            if (s > 0)
                            {
                                var waitMs = s * 50L - pacer.ElapsedMilliseconds;
                                if (waitMs > 0) await Task.Delay((int)waitMs);
                            }
                            var (segOffset, segLength) = segments[s];
                            session.OnFrame(wavAudio.AsMemory(segOffset, segLength));
                        }

                        long finishMs;
                        string runText;
                        if (trimResult.IsSilent)
                        {
                            // Production drops silent dictations before transcription
                            // (PipelineHost TrimForTranscription returns null): no text, no latency sample.
                            runText = "";
                            finishMs = 0;
                        }
                        else
                        {
                            var swFinish = Stopwatch.StartNew();
                            var finishResult = await session.FinishAsync(trimResult.Trimmed, CancellationToken.None);
                            swFinish.Stop();
                            if (finishResult is null)
                                throw new InvalidOperationException(
                                    $"corpus[{entry.Id}]: no transcript from coordinator"); // caught below -> error row, run continues
                            runText = finishResult.Text;
                            finishMs = swFinish.ElapsedMilliseconds;
                        }
                        finishRuns.Add(finishMs);
                        if (run == 0)
                        {
                            // Accuracy and flags from the first run; later runs only add latency samples.
                            streamText = runText;
                            fellBack = runFellBack;
                            truncated = nemLog.Lines.Any(l => l.Contains("was_truncated", StringComparison.OrdinalIgnoreCase));
                        }
                    }

                    double? wer = null;
                    double? cer = null;
                    bool? silentPass = null;
                    if (entry.ExpectedSilent)
                    {
                        silentPass = EvalMetrics.SilentPass(streamText);
                    }
                    else if (hasReference)
                    {
                        wer = EvalMetrics.Wer(referenceText, streamText).Rate;
                        cer = EvalMetrics.Cer(referenceText, streamText).Rate;
                    }
                    var parityDiff = TranscriptDiff.Summarize(batchText, streamText).Describe();
                    clipResults.Add(new ClipResult(
                        entry.Id, wavAudio.Length / 16000.0, entry.ExpectedSilent, hasReference,
                        referenceText, streamText, batchText, wer, cer, silentPass,
                        finishRuns, fellBack, truncated, trimResult.IsSilent, parityDiff));
                    Console.WriteLine($"# corpus[{entry.Id}]: fellBack={fellBack} truncated={truncated} " +
                        $"wer={(wer is null ? "n/a" : wer.Value.ToString("F3"))} finishMs={finishRuns[0]} parity: {parityDiff}");
                }
                catch (Exception ex)
                {
                    // Error row: empty texts/metrics; the exception text goes only to
                    // results.json (gitignored artifacts/) and the run log -- results.md
                    // shows an ERROR marker and counts, never the message.
                    clipResults.Add(new ClipResult(
                        entry.Id, 0.0, entry.ExpectedSilent, HasReference: false,
                        Reference: "", StreamText: "", BatchText: "", Wer: null, Cer: null, SilentPass: null,
                        FinishMsRuns: Array.Empty<long>(), FellBack: false, Truncated: false,
                        TrimmedSilent: false, BatchParityDiff: "",
                        Error: $"{ex.GetType().Name}: {ex.Message}"));
                    Console.Error.WriteLine($"# corpus[{entry.Id}]: FAILED {ex.GetType().Name}: {ex.Message}");
                }
            }

            var runInfo = new EvalRunInfo(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(corpusDir)),
                Path.GetFileNameWithoutExtension(nemotronModel),
                Winpepper.Asr.TranscribeCpp.TranscribeCppContract.RequiredVersion,
                DateTime.UtcNow.ToString("yyyy-MM-dd"),
                repeats);
            var evalSummary = EvalResults.Summarize(clipResults);
            // ALWAYS write both files -- even when clips failed -- then report failures.
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "results.json"), EvalResults.ToJson(runInfo, clipResults, evalSummary));
            var resultsMd = EvalResults.ToMarkdown(runInfo, clipResults, evalSummary);
            File.WriteAllText(Path.Combine(outDir, "results.md"), resultsMd);
            Console.WriteLine();
            Console.WriteLine(resultsMd);
            if (evalSummary.FailedCount > 0)
            {
                // Results are already on disk; the non-zero exit only flags the failures.
                Console.Error.WriteLine($"# corpus: {evalSummary.FailedCount} clip(s) FAILED -- results written to {outDir}, exiting non-zero");
                Environment.ExitCode = 1;
            }
            break;
        }
```

If the constant `Winpepper.Asr.TranscribeCpp.TranscribeCppContract.RequiredVersion` does not resolve, check `src/Winpepper.Asr/TranscribeCpp/TranscribeCppContract.cs:21` for the exact type/namespace and use that — it is the `RequiredVersion = "0.1.3"` pin referenced by the `TranscribeCppEngine.cs:7` comment. Do not hardcode the version string in the bench.

- [ ] **Step 4: Add the helper classes**

Near `ProbeTranscriber` (~line 357), add:

```csharp
/// <summary>Batch fallback for the corpus eval: a SECOND nemotron engine instance
/// (same model + runtime, its OWN compute gate), offline. Must NOT be the primary
/// engine: during FinishAsync the primary's native stream still holds its compute
/// gate (acquired TranscribeCppEngine.cs:177, released only in NativeStream.Dispose
/// at :336 via Session.DisposeAsync, NemotronStreamingTranscriber.cs:174, which runs
/// only AFTER FinishAsync returns, StreamingDictationSession.cs:120-121) -- a
/// same-engine fallback would stall 5 s and throw at the gate wait (:235).
/// Wrapped in ProbeTranscriber so a fallback is recorded per clip and still yields text.</summary>
sealed class EngineBatchTranscriber : ITranscriber
{
    private readonly Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine _engine;
    public EngineBatchTranscriber(Winpepper.Asr.TranscribeCpp.ITranscribeCppEngine engine) => _engine = engine;
    public string ModelName => "nemotron-batch";
    public Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> audio, CancellationToken ct)
        => Task.FromResult(new TranscriptionResult(_engine.TranscribeBatch(audio.ToArray()), ModelName));
}

/// <summary>Collects NemotronStreamingTranscriber log lines so the corpus eval can
/// detect the "stream reports was_truncated" fallback reason.</summary>
sealed class ListLogger : ILogger
{
    public List<string> Lines { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Lines.Add(formatter(state, exception));
}
```

(If `using Microsoft.Extensions.Logging;` is not already among Program.cs's usings — the existing `CollectingLogger : ILogger` suggests it is — add it.)

- [ ] **Step 5: Verify it compiles on Linux and nothing regressed**

```bash
dotnet build scripts/asr-latency-bench/AsrLatencyBench.csproj -c Release
./scripts/linux-tests.sh
```

Expected: `Build succeeded.` and all tests `Failed: 0`. (The scenario itself only runs on Windows — Task 12.)

- [ ] **Step 6: Commit**

```bash
git add scripts/asr-latency-bench/AsrLatencyBench.csproj scripts/asr-latency-bench/Program.cs
git commit -m "feat(bench): corpus eval mode -- production-fidelity streaming (preroll burst, trimmed FinishAsync, stopwatch pacing) scored against reference transcripts"
```

---

### Task 11: Windows driver script

**Files:**
- Create: `scripts/run-asr-eval-windows.sh` (executable)

**Interfaces:**
- Consumes: the `corpus` scenario (Task 10); pattern copied from `scripts/run-nemotron-bench-windows.sh`.
- Produces: `./scripts/run-asr-eval-windows.sh <corpus-dir-wsl> [repeats]` → `artifacts/asr-eval/{build.log,stage.log,corpus.log,results.json,results.md}`.

- [ ] **Step 1: Write the script**

Create `scripts/run-asr-eval-windows.sh`:

```bash
#!/usr/bin/env bash
# Build the ASR latency bench with the Windows dotnet over the \\wsl.localhost
# UNC path, stage it to a Windows-local %TEMP% dir (native library loads from
# UNC are unreliable), run the corpus eval scenario against an exported corpus
# of dictation clips + reference transcripts, and collect results into
# artifacts/asr-eval/.
#
# Host safety: only host writes are the %TEMP% staging/results dirs and NuGet
# restore. Reads (never writes) the corpus dir and the app-installed nemotron
# model and runtime under %LOCALAPPDATA%\winpepper\models (the canonical tree,
# read-only to us; NEM_MODEL/NEM_RUNTIME env overrides are the escape hatch).
# Never touches a running Winpepper.exe or any other %LOCALAPPDATA%\winpepper data.
#
# Usage: ./scripts/run-asr-eval-windows.sh <corpus-dir-wsl> [repeats]
#   e.g. ./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
# Env overrides (Windows paths): NEM_MODEL, NEM_RUNTIME
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CORPUS_WSL="${1:?usage: run-asr-eval-windows.sh <corpus-dir> [repeats]}"
REPEATS="${2:-1}"
[[ -f "$CORPUS_WSL/manifest.json" ]] || { echo "run-asr-eval-windows: no manifest.json in $CORPUS_WSL" >&2; exit 2; }

PS="/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
[[ -x "$PS" ]] || { echo "run-asr-eval-windows: powershell.exe not found at $PS" >&2; exit 2; }
UNC_ROOT="$(wslpath -w "$HERE")"
CORPUS_WIN="$(wslpath -w "$CORPUS_WSL")"
NEM_MODEL="${NEM_MODEL:-C:\\Users\\dan\\AppData\\Local\\winpepper\\models\\nemotron-streaming-en\\nemotron-speech-streaming-en-0.6b-Q8_0.gguf}"
NEM_RUNTIME="${NEM_RUNTIME:-C:\\Users\\dan\\AppData\\Local\\winpepper\\models\\nemotron-streaming-en\\runtime\\transcribe-native-windows-x86_64-cpu-vulkan}"
OUT="$HERE/artifacts/asr-eval"
mkdir -p "$OUT"

ps_run() { # ps_run <timeout_s> <logfile> <ps-command>
  local t="$1" log="$2" cmd="$3"
  timeout --foreground "$t" "$PS" -NoProfile -ExecutionPolicy Bypass \
    -Command "$cmd; exit \$LASTEXITCODE" 2>&1 | tee "$log"
  return "${PIPESTATUS[0]}"
}

echo "=== [1/4] Pre-clean cross-OS bin/obj (CS0006 guard) ==="
rm -rf "$HERE"/scripts/asr-latency-bench/bin "$HERE"/scripts/asr-latency-bench/obj \
       "$HERE"/src/*/bin "$HERE"/src/*/obj

echo "=== [2/4] Build bench (Windows dotnet, Release) ==="
bench_csproj="$UNC_ROOT"'\scripts\asr-latency-bench\AsrLatencyBench.csproj'
ps_run 1800 "$OUT/build.log" "dotnet build '$bench_csproj' -c Release"

echo "=== [3/4] Stage bench output to %TEMP%\\winpepper-asr-eval ==="
bench_bin="$UNC_ROOT"'\scripts\asr-latency-bench\bin\Release\net9.0'
ps_run 300 "$OUT/stage.log" "
  \$dst = Join-Path \$env:TEMP 'winpepper-asr-eval'
  if (Test-Path \$dst) { Remove-Item -Recurse -Force \$dst }
  Copy-Item -Recurse '$bench_bin' \$dst"

echo "=== [4/4] Run the corpus eval (repeats=$REPEATS) ==="
# The bench exits non-zero when any clip FAILED (per-clip error rows) but still
# writes results.json/results.md first -- so collect results even on failure,
# then propagate the exit code.
corpus_status=0
ps_run 7200 "$OUT/corpus.log" "
  \$res = Join-Path \$env:TEMP 'winpepper-asr-eval-results'
  if (Test-Path \$res) { Remove-Item -Recurse -Force \$res }
  Set-Location (Join-Path \$env:TEMP 'winpepper-asr-eval')
  dotnet exec AsrLatencyBench.dll corpus --corpus '$CORPUS_WIN' \
    --nemotron-model '$NEM_MODEL' --nemotron-runtime '$NEM_RUNTIME' \
    --repeats $REPEATS --out \$res" || corpus_status=$?

# Collect results back (results.json contains transcript text -- artifacts/ is gitignored).
WIN_TEMP_WSL="$(wslpath "$("$PS" -NoProfile -Command 'Write-Output $env:TEMP' | tr -d '\r')")"
cp -r "$WIN_TEMP_WSL/winpepper-asr-eval-results/." "$OUT/"
if [[ "$corpus_status" -ne 0 ]]; then
  echo "run-asr-eval-windows: corpus eval reported failed clips (exit $corpus_status) -- results still collected in $OUT; see corpus.log and results.md" >&2
  exit "$corpus_status"
fi
echo "run-asr-eval-windows: done -- results in $OUT (results.md, results.json), logs alongside"
```

- [ ] **Step 2: Syntax-check and make executable**

```bash
bash -n scripts/run-asr-eval-windows.sh && echo "syntax ok"
chmod +x scripts/run-asr-eval-windows.sh
```

Expected: `syntax ok`.

- [ ] **Step 3: Full suite + commit**

```bash
./scripts/linux-tests.sh
git add scripts/run-asr-eval-windows.sh
git commit -m "feat(bench): Windows driver for the corpus eval (UNC build, %TEMP% staging, artifacts collection)"
```

---

### Task 12: End-to-end proof, evidence doc, Windows gate

**Files:**
- Create: `docs/plans/2026-07-26-asr-eval-framework-evidence.md`

**Interfaces:**
- Consumes: everything above. No new types.
- Produces: real eval numbers from real clips; the committed evidence doc (numbers and clip ids only — NO transcript or reference text).

- [ ] **Step 1: Confirm the corpus from Task 3 is present (re-export if needed)**

```bash
python3 -c "import json;m=json.load(open('/mnt/c/Users/dan/winpepper-evals/corpus-v1/manifest.json'));print(len(m['entries']),'entries')"
ls /mnt/c/Users/dan/winpepper-evals/corpus-v1/clips/*.wav | wc -l
```

Expected: ≥5 entries with matching WAV count. If missing, re-run the Task 3 Step 3 export command.

- [ ] **Step 2: Branch on ASSEMBLYAI_API_KEY**

```bash
if [ -n "${ASSEMBLYAI_API_KEY:-}" ]; then echo "KEY PRESENT"; else echo "KEY ABSENT"; fi
```

**If KEY PRESENT — generate real reference transcripts:**

```bash
dotnet run --project scripts/asr-eval-corpus -c Release -- references \
  --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1
ls /mnt/c/Users/dan/winpepper-evals/corpus-v1/clips/*.reference.txt | wc -l
```

Expected: `references: N written, 0 skipped, 0 failed` (N = clip count) and one `.reference.txt` per non-excluded clip. Then re-run the same command once more — expected: `references: 0 written, N skipped, 0 failed` (idempotence proven). Spot-check ONE file is plain readable text (`wc -c` on it; do not paste its content anywhere).

**If KEY ABSENT:** do NOT fabricate anything. Proceed to Step 3 (the eval still proves streaming + latency + batch-parity diffs without WER); Step 5's reference spot-check is then also pending, and in Step 6 record precisely: references are pending, and the user must run, after `export ASSEMBLYAI_API_KEY=...`:

```bash
dotnet run --project scripts/asr-eval-corpus -c Release -- references --corpus /mnt/c/Users/dan/winpepper-evals/corpus-v1
./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
```

- [ ] **Step 3: Run the eval on the Windows host**

```bash
./scripts/run-asr-eval-windows.sh /mnt/c/Users/dan/winpepper-evals/corpus-v1 3
cat artifacts/asr-eval/results.md
python3 -c "import json;r=json.load(open('artifacts/asr-eval/results.json'));print(r['info'],r['summary'])"
```

Expected: the four driver stages complete; `results.md` shows one row per clip with real post-stop latencies (3 runs each), `fellBack`/`truncated` flags, batch-parity noted per clip on the run log, and — if references exist — real WER/CER numbers plus the aggregate summary line (including `Failed: 0`; a non-zero failed count means per-clip error rows — investigate before trusting the numbers). `results.json` carries full transcripts. The driver defaults to the app-installed model/runtime tree at `C:\Users\dan\AppData\Local\winpepper\models\nemotron-streaming-en` (canonical, verified present with `transcribe.dll` contract 0.1.3; read-only to us). If they have moved, the scenario prints `corpus: SKIPPED (...)` — in that case set the `NEM_MODEL`/`NEM_RUNTIME` env overrides to the actual locations and re-run; do not fake a results file.

- [ ] **Step 4: Sanity-check honesty controls in the output**

Verify in `artifacts/asr-eval/corpus.log`: per-clip `# corpus[<id>]: fellBack=... truncated=... wer=... finishMs=...` lines exist for every non-excluded clip (proof the production path streamed each clip; a failed clip instead shows `# corpus[<id>]: FAILED ...` and an ERROR row — the driver exits non-zero when any clip failed), and `results.md` contains no transcript text.

- [ ] **Step 5: Mandatory human spot-check of the references (before trusting any numbers)**

Ask the user (Dan) to open a handful of `clips/<id>.reference.txt` files next to their WAVs and confirm, by listening, that each reference matches what was actually said. This MUST include verifying that at least one clip with an audible spoken filler retains "um"/"uh" in its reference — if fillers are systematically absent despite being audible, treat disfluencies as a vendor no-op for this model and revisit the scoring policy (references would then be missing words every local candidate transcribes verbatim). Record only the outcome in the evidence doc: clip ids checked + pass/fail — never the reference text itself. Do not proceed to conclusions about model ranking until this check passes.

- [ ] **Step 6: Write the evidence doc**

Create `docs/plans/2026-07-26-asr-eval-framework-evidence.md` containing, honestly and exactly:
- The commands run (exporter, references or the honest "key absent" statement with the two pending commands from Step 2, driver).
- The exporter output lines (counts) proving re-runnability.
- The full `results.md` content (it is privacy-safe by construction: ids and numbers only).
- The run metadata line: corpus folder name, speech model name, transcribe.cpp version, date, repeats.
- The Step 5 spot-check outcome: clip ids checked + pass/fail (including the filler check), no reference text.
- If number/currency/date content appears in any clip, spot-check its rendering between references and transcripts (references use formatted digits; local nemotron output was verified digit-native) and record the outcome as ids + ok/mismatch counts only.
- A one-line statement of which of the two branches (key present/absent) this run took and what, if anything, remains for the user.

No transcript or reference text may appear in this file.

- [ ] **Step 7: Full Linux suite (bin/obj were cleaned by the driver — rebuild), commit, Windows gate**

```bash
./scripts/linux-tests.sh
git add docs/plans/2026-07-26-asr-eval-framework-evidence.md
git commit -m "docs(evidence): real corpus eval results -- exporter, references branch, Windows streaming run"
./scripts/windows-gate.sh
```

Expected: linux tests all `Failed: 0`; commit succeeds; the gate ends `GATE: GREEN` (required before any push).

---

## Self-review record

**1. Spec coverage:**
- Part A exporter: source/dest/read-only (Task 3), re-runnable dedupe by stable id (Tasks 2-3, proven Step 4), manifest metadata incl. raw+cleaned transcripts, models, timings (Tasks 1-2), hand-editable curation flags `expectedSilent`/`exclude` defaulting to neither (Task 1). ✔
- Part B: reuses `AssemblyAiTranscriber` as a library (Task 6), model `universal-3-5-pro` (Task 6), env-var key with clear failure and non-zero exit, proven live (Task 6 Step 3), `disfluencies: true` with `format_text` kept (Tasks 4, 6), delete-after-transcribe on (Task 6), expected-silent → empty reference, exclude → skipped, idempotent with `--force` (Tasks 5-6), plain-text hand-editable reference next to the clip (Tasks 5-6). ✔
- Part C: extends the existing bench, not a new project (Task 10); `--corpus` mode skipping excluded clips; three fidelity fixes — preroll burst (Task 8 + Task 10 replay loop), silence-trimmed `FinishAsync` via `Winpepper.Audio.SilenceTrimmer` (Task 10), stopwatch-scheduled pacing (Task 10); WER+CER on extended-normalization (reuses `TranscriptDiff.Normalize`; number-word normalization explicitly decided against — spec allows: "only if simple", and shared normalization preserves relative ranking); post-stop latency same stopwatch window; fallback probe; truncation recorded; expected-silent pass/fail; JSON per-clip + aggregate summary (mean/median WER, latency percentiles, fallback count, silent pass rate) + markdown table; corpus name/model/transcribe.cpp version/date recorded; `--repeats N` with accuracy from first run; Windows driver following the existing pattern (build over UNC, stage to %TEMP%, run, collect to artifacts/) (Tasks 7-11). ✔
- Proving it works: pure-logic unit tests per repo conventions (Tasks 1, 2, 4, 5, 7, 8, 9); real exporter run (Task 3); key-present/key-absent branches handled honestly with exact stop-and-report behavior (Task 12); Windows run showing real numbers, with the no-reference degraded mode still producing latency + batch-parity diffs (Tasks 10, 12). ✔
- Constraints: nothing private in git (corpus outside repo; results.json in gitignored artifacts/; results.md and evidence doc are numbers-only by construction and by test `ToMarkdown_HasPerClipRowsAndSummary_ButNoTranscriptText`); tests green before every commit; gate before push; bench stays out of the sln (and the new tool too); the unrelated uncommitted HistoryStore change is in the main checkout and untouched; consistent "corpus"/"reference transcript" naming. ✔

**1b. No silent deferrals:** every requirement has a production-path task; the only conditional is AssemblyAI reference generation when `ASSEMBLYAI_API_KEY` is absent, and the spec itself mandates that exact stop-and-report branch (Task 12 Step 2 encodes it). The `corpus` scenario runs real clips through the real engine on the real Windows host — no mocks stand in for any user-facing outcome. No unresolved coverage gaps.

**2. Placeholder scan:** no TBD/TODO/"handle edge cases"/"similar to Task N" anywhere; every code step contains complete code; every run step has a command and expected output. The single lookup contingency (the `TranscribeCppContract.RequiredVersion` namespace, Task 10 Step 3) names the exact file and line to confirm against.

**3. Type consistency:** `ClipTimings`, `CorpusEntry`, `CorpusManifest`, `CorpusJson`, `HistoryIndexEntry/File`, `ExportItem/ExportPlan/BuildPlan`, `ReferenceAction/ReferencePlanner.{Decide,ReferencePath}`, `ErrorRate/EvalMetrics.{Wer,Cer,SilentPass}`, `EvalFraming.{Segments,PrerollSamples,FrameSamples}`, `ClipResult` (incl. trailing `string? Error = null` — error rows from Task 10's per-clip catch), `EvalRunInfo`, `EvalSummary` (incl. trailing `int FailedCount`), `EvalReport/EvalResults.{Summarize,ToJson,ToMarkdown,Percentile}`, `EngineBatchTranscriber` (constructed with the SECOND engine instance, never the primary — see design decision 5), `ListLogger`, `EnvKeyStore/ConsoleWarnLogger` (Task 6) — cross-checked; signatures used in Tasks 3, 6, 10 match their defining tasks (1, 2, 5, 7, 8, 9) exactly, and `Error`/`FailedCount` sit last with defaults/named use so every positional construction in Tasks 9-10 still compiles.
