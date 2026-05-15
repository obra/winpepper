# Winpepper Plan 4 — History, Lab, Models Tab, Downloader

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent history of dictation sessions (up to 50 entries, newest-first), a Lab/detail view that lets users rerun transcription and cleanup against the original WAV with arbitrary models/prompts (non-destructively), a Models tab that lists and downloads ASR + cleanup models from HuggingFace with resumable range-request downloads and SHA-256 verification, and the WAV-archival hook the Plan 1 pipeline was missing.

**Architecture:** Two new pure-.NET-9 libraries — `Winpepper.History` (atomic JSON index + 50-entry prune + WAV cleanup) and `Winpepper.Models` (declarative model registry + `HttpClient` downloader with `Range:` resume and SHA-256 verify). Both are cross-platform; their tests run on Linux CI. The WinUI 3 pages that consume them live in `Winpepper.App` (Windows-only, smoke-tested on the VM). DiffPlex powers the word-level diffs in the Lab. Lab reruns construct a transient `ParakeetSession` / `CleanupRunner` against the WAV bytes — nothing is written back to the history entry.

**Tech Stack:** C# / .NET 9, `System.Text.Json`, `System.Net.Http` (Range header, streamed response), `System.Security.Cryptography.SHA256`, DiffPlex 1.7.x, WinUI 3 (`Microsoft.WindowsAppSDK`), `MediaPlayerElement`, xUnit, Shouldly. No new native dependencies.

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md) — §5.4, §7.3 (History, History detail / Lab, Models), §9 model downloader, §10.1 `Winpepper.History.Tests`.

**Prerequisites:**

- **Plan 1** (`plan-1/foundation`) — solution scaffolding, `Winpepper.Core` (AtomicFile, SettingsStore, logging), `Winpepper.Asr.ParakeetSession`, `Winpepper.Audio.WasapiRecorder`, VM scripts.
- **Plan 2** — `Winpepper.Cleanup.CleanupRunner`, `Winpepper.Cleanup.PromptBuilder`, `Winpepper.Corrections.CorrectionStore`, `Winpepper.Platform.WindowContext`. The Lab cleanup-rerun panel constructs a transient `CleanupRunner` and calls its public run method.
- **Plan 3** — `Winpepper.App` (WinUI 3, packaged), `NavigationView` shell with placeholder pages, settings view-model base class, tray. Plan 4 fills in the History, History detail / Lab, and Models pages and adds the model-selection bindings to existing Cleanup/Recording settings view-models.

**Repo root throughout the plan:** `/home/jesse/git/winpepper/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced from Linux via `scripts/sync-to-vm.sh`).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Run it and confirm it fails. Implement. Run it and confirm it passes. Commit.

**Commits.** One commit per task at minimum. Smaller commits within a task are fine. Always end a task with a green build and green tests on Linux *and* (where applicable) on the Windows VM.

**Building.** Cross-platform tasks (everything in `Winpepper.History`, `Winpepper.Models`, and their tests) build and test on Linux (`dotnet build`, `dotnet test`). Windows-only tasks (anything in `Winpepper.App`) run on the VM via the `winrun` / `winssh` helpers.

**Linux build env:** `export DOTNET_ROOT="$HOME/.dotnet"` if the SDK isn't on PATH.

**Skipping Windows tests on Linux.** Tests that touch Win32 / WinUI APIs are tagged `[Trait("Platform", "Windows")]` (Plan 1 convention). Linux CI runs `dotnet test --filter "Platform!=Windows"`.

**SHA-256 hashes for HuggingFace assets.** The hashes hard-coded in `ModelRegistry` (Task 10) MUST be re-verified at plan-execution time. The implementing engineer runs the verification script in Task 10 Step 4 and updates the constants before merging. The plan ships with placeholder hashes flagged `TODO(verify-at-exec)` (the only TODOs allowed in this plan).

---

## Task 1: Scaffold `Winpepper.History` project

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Winpepper.History.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/PlaceholderType.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/PlaceholderTests.cs`
- Modify: `/home/jesse/git/winpepper/winpepper.sln`

- [ ] **Step 1: Create directories**

```bash
cd /home/jesse/git/winpepper
mkdir -p src/Winpepper.History tests/Winpepper.History.Tests
```

- [ ] **Step 2: Write `src/Winpepper.History/Winpepper.History.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.History</RootNamespace>
    <AssemblyName>Winpepper.History</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `src/Winpepper.History/PlaceholderType.cs`** (so the project compiles to a non-empty assembly before later tasks fill it in)

```csharp
namespace Winpepper.History;

internal static class PlaceholderType
{
    public const string Marker = "Winpepper.History scaffold";
}
```

- [ ] **Step 4: Write `tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.History\Winpepper.History.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write `tests/Winpepper.History.Tests/PlaceholderTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Scaffold_Compiles()
    {
        true.ShouldBeTrue();
    }
}
```

- [ ] **Step 6: Add both projects to the solution**

```bash
cd /home/jesse/git/winpepper
dotnet sln add src/Winpepper.History/Winpepper.History.csproj
dotnet sln add tests/Winpepper.History.Tests/Winpepper.History.Tests.csproj
```

- [ ] **Step 7: Build and test**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build
dotnet test --filter "FullyQualifiedName~Winpepper.History.Tests"
```

Expected: build succeeds, 1 placeholder test passes.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.History tests/Winpepper.History.Tests winpepper.sln
git commit -m "scaffold(history): create Winpepper.History project + test project"
```

---

## Task 2: `HistoryEntry` record

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/HistoryEntry.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/HistoryEntryTests.cs`

A `HistoryEntry` captures everything we want to show in the History list and the Lab detail view. It is the unit the index file stores.

- [ ] **Step 1: Write the failing test `tests/Winpepper.History.Tests/HistoryEntryTests.cs`**

```csharp
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryEntryTests
{
    [Fact]
    public void Defaults_AreSafe()
    {
        var e = new HistoryEntry();
        e.Id.ShouldNotBeNullOrEmpty();
        e.RawTranscript.ShouldBe("");
        e.CleanedText.ShouldBe("");
        e.WavRelativePath.ShouldBe("");
        e.DurationMs.ShouldBe(0);
        e.AsrModelName.ShouldBe("");
        e.CleanupModelName.ShouldBe("");
        e.WindowContextUsed.ShouldBeFalse();
        e.WindowTitleAtStart.ShouldBe("");
        e.WindowTitleAtInject.ShouldBe("");
    }

    [Fact]
    public void RoundTrips_Through_Json()
    {
        var original = new HistoryEntry
        {
            Id = "deadbeef",
            CreatedAtUtc = new DateTime(2026, 5, 15, 10, 30, 0, DateTimeKind.Utc),
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            WavRelativePath = "2026-05-15/deadbeef.wav",
            DurationMs = 1234,
            AsrModelName = "parakeet-tdt-0.6b-v3",
            CleanupModelName = "qwen2.5-0.5b-instruct-q4_k_m",
            WindowContextUsed = true,
            WindowTitleAtStart = "Notepad",
            WindowTitleAtInject = "Notepad",
            Timings = new HistoryTimings { RecordMs = 1200, TranscribeMs = 350, CleanupMs = 410, InjectMs = 12, TotalMs = 1990 },
        };

        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<HistoryEntry>(json)!;

        loaded.ShouldBe(original);
    }

    [Fact]
    public void TranscriptPreview_TrimsAndTruncatesTo80Chars()
    {
        var e = new HistoryEntry { RawTranscript = "   " + new string('x', 200) + "   " };
        var preview = e.TranscriptPreview;
        preview.Length.ShouldBeLessThanOrEqualTo(80);
        preview.ShouldStartWith("xxxx");
        preview.ShouldNotContain("   ");
    }

    [Fact]
    public void TranscriptPreview_ShortText_ReturnedUnchanged()
    {
        var e = new HistoryEntry { RawTranscript = "hi" };
        e.TranscriptPreview.ShouldBe("hi");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryEntryTests"
```

Expected: compile errors — `HistoryEntry` / `HistoryTimings` not found.

- [ ] **Step 3: Implement `src/Winpepper.History/HistoryEntry.cs`**

```csharp
namespace Winpepper.History;

/// <summary>
/// Per-stage timings (ms) captured by the session pipeline. Surfaced in the Lab.
/// </summary>
public sealed record HistoryTimings
{
    public int RecordMs { get; init; }
    public int TranscribeMs { get; init; }
    public int CleanupMs { get; init; }
    public int InjectMs { get; init; }
    public int TotalMs { get; init; }
}

/// <summary>
/// One archived dictation session. The WAV file lives at
/// <c>%LOCALAPPDATA%\winpepper\history\{WavRelativePath}</c>.
///
/// Records are immutable. The Lab rerun panels never write back into the
/// entry — promotions go through the settings store and the corrections store.
/// </summary>
public sealed record HistoryEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public string RawTranscript { get; init; } = "";
    public string CleanedText { get; init; } = "";
    public string WavRelativePath { get; init; } = "";
    public int DurationMs { get; init; }
    public string AsrModelName { get; init; } = "";
    public string CleanupModelName { get; init; } = "";
    public bool WindowContextUsed { get; init; }
    public string WindowTitleAtStart { get; init; } = "";
    public string WindowTitleAtInject { get; init; } = "";
    public HistoryTimings Timings { get; init; } = new();

    /// <summary>80-char preview of the raw transcript, with leading/trailing whitespace trimmed.</summary>
    public string TranscriptPreview
    {
        get
        {
            var t = RawTranscript.Trim();
            return t.Length <= 80 ? t : t[..80];
        }
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryEntryTests"
```

Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.History/HistoryEntry.cs tests/Winpepper.History.Tests/HistoryEntryTests.cs
git commit -m "feat(history): HistoryEntry + HistoryTimings records"
```

---

## Task 3: `HistoryIndex` — schema-versioned envelope

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/HistoryIndex.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/HistoryIndexTests.cs`

The on-disk index is a JSON object `{ "schema": 1, "entries": [...] }`. Future schema migrations live here.

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/HistoryIndexTests.cs`**

```csharp
using Shouldly;
using System.Text.Json;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryIndexTests
{
    [Fact]
    public void Empty_HasSchemaVersion_AndNoEntries()
    {
        var idx = new HistoryIndex();
        idx.Schema.ShouldBe(1);
        idx.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void RoundTrips_With_TwoEntries()
    {
        var original = new HistoryIndex
        {
            Entries = new List<HistoryEntry>
            {
                new() { Id = "a", RawTranscript = "alpha" },
                new() { Id = "b", RawTranscript = "beta" },
            }
        };
        var json = JsonSerializer.Serialize(original);
        var loaded = JsonSerializer.Deserialize<HistoryIndex>(json)!;
        loaded.Schema.ShouldBe(1);
        loaded.Entries.Count.ShouldBe(2);
        loaded.Entries[0].RawTranscript.ShouldBe("alpha");
        loaded.Entries[1].RawTranscript.ShouldBe("beta");
    }

    [Fact]
    public void OlderSchema_LoadStillReturnsEntries()
    {
        // A future migration would convert. For now we accept and pass through.
        var json = """{"schema":1,"entries":[{"id":"x","rawTranscript":"hi"}]}""";
        var loaded = JsonSerializer.Deserialize<HistoryIndex>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        loaded.Entries.Count.ShouldBe(1);
        loaded.Entries[0].Id.ShouldBe("x");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryIndexTests"
```

Expected: `HistoryIndex` not found.

- [ ] **Step 3: Implement `src/Winpepper.History/HistoryIndex.cs`**

```csharp
namespace Winpepper.History;

/// <summary>
/// On-disk envelope for the history file. Schema versioned for forward compat.
/// </summary>
public sealed record HistoryIndex
{
    public int Schema { get; init; } = 1;
    public List<HistoryEntry> Entries { get; init; } = new();
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryIndexTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.History/HistoryIndex.cs tests/Winpepper.History.Tests/HistoryIndexTests.cs
git commit -m "feat(history): HistoryIndex schema-versioned envelope"
```

---

## Task 4: `HistoryStore` — load/save/append with pruning

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/HistoryStore.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/HistoryStoreTests.cs`

The store owns:
- The index file path (`%LOCALAPPDATA%\winpepper\history\index.json`).
- Atomic save via `Winpepper.Core.Io.AtomicFile`.
- Newest-first ordering (sorted on load by `CreatedAtUtc` descending).
- Two-tier pruning on every `Append`: first drop entries whose `CreatedAtUtc` is older than `MaxAge` (default 30 days per spec §5.4 line 150), then cap the survivors at `MaxEntries` (50). Both tiers also delete the dropped WAVs.
- Crash safety: a corrupt JSON file yields an empty index (logged by callers).

`HistoryStore` deliberately does NOT know how WAV files are produced — it takes an absolute root directory in its constructor and treats `entry.WavRelativePath` as a relative path under that root.

The age cap is 30 days because the spec's §5.4 line 150 says "16 kHz mono int16, 30-day rolling retention". The 50-entry cap is tighter and exists for disk-budget protection: 50 average dictations at ~5s each at 16 kHz mono int16 is roughly 8 MB, well under the spec's "few hundred MB" envelope. In practice, the entry cap usually fires before the age cap — but both are needed because a power user can hit 50 entries in a day and a light user can leave a single WAV around for months.

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/HistoryStoreTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _root;

    public HistoryStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"winpepper-history-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyIndex()
    {
        var store = new HistoryStore(_root);
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Append_Then_Load_ReturnsEntry()
    {
        var store = new HistoryStore(_root);
        var entry = new HistoryEntry { Id = "a", RawTranscript = "alpha" };
        store.Append(entry);
        store.Load().Entries.Single().Id.ShouldBe("a");
    }

    [Fact]
    public void Append_NewestFirst()
    {
        var store = new HistoryStore(_root);
        var older = new HistoryEntry { Id = "older", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5) };
        var newer = new HistoryEntry { Id = "newer", CreatedAtUtc = DateTime.UtcNow };
        store.Append(older);
        store.Append(newer);
        var entries = store.Load().Entries;
        entries[0].Id.ShouldBe("newer");
        entries[1].Id.ShouldBe("older");
    }

    [Fact]
    public void Append_PrunesTo50_AndDeletesPrunedWavFiles()
    {
        var store = new HistoryStore(_root);
        // Pre-create 60 entries with real WAV files on disk.
        for (var i = 0; i < 60; i++)
        {
            var rel = $"2026-05-15/entry-{i:00}.wav";
            var abs = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, $"wav-{i}");
            store.Append(new HistoryEntry
            {
                Id = $"e{i:00}",
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(i), // newer entries have larger i
                WavRelativePath = rel,
            });
        }

        var entries = store.Load().Entries;
        entries.Count.ShouldBe(50);
        // Newest 50 should be i=10..59
        entries.First().Id.ShouldBe("e59");
        entries.Last().Id.ShouldBe("e10");

        // WAV files for the pruned (oldest) entries should be gone.
        for (var i = 0; i < 10; i++)
            File.Exists(Path.Combine(_root, $"2026-05-15/entry-{i:00}.wav")).ShouldBeFalse();
        // WAV files for the kept entries should still exist.
        for (var i = 10; i < 60; i++)
            File.Exists(Path.Combine(_root, $"2026-05-15/entry-{i:00}.wav")).ShouldBeTrue();
    }

    [Fact]
    public void Append_PrunesEntriesOlderThanMaxAge_AndDeletesTheirWavs()
    {
        // Spec §5.4 line 150: WAVs follow a 30-day rolling retention.
        var store = new HistoryStore(_root);
        var oldRel = "2026-04-01/old.wav";
        var oldAbs = Path.Combine(_root, oldRel);
        Directory.CreateDirectory(Path.GetDirectoryName(oldAbs)!);
        File.WriteAllText(oldAbs, "stale-wav");

        // 31 days old — past the 30-day retention.
        store.Append(new HistoryEntry
        {
            Id = "old",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-31),
            WavRelativePath = oldRel,
        });
        // Touching the store again triggers the age-based prune.
        store.Append(new HistoryEntry
        {
            Id = "fresh",
            CreatedAtUtc = DateTime.UtcNow,
            WavRelativePath = "",
        });

        var entries = store.Load().Entries;
        entries.Select(e => e.Id).ShouldNotContain("old");
        entries.Select(e => e.Id).ShouldContain("fresh");
        File.Exists(oldAbs).ShouldBeFalse();
    }

    [Fact]
    public void Append_KeepsEntriesAtTheMaxAgeBoundary()
    {
        // Exactly 29 days old — must survive.
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry
        {
            Id = "boundary",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-29),
            WavRelativePath = "",
        });
        store.Load().Entries.Single().Id.ShouldBe("boundary");
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyIndex()
    {
        var indexPath = Path.Combine(_root, "index.json");
        File.WriteAllText(indexPath, "{ not valid json");
        var store = new HistoryStore(_root);
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Delete_RemovesEntryAndWav()
    {
        var store = new HistoryStore(_root);
        var rel = "2026-05-15/keep.wav";
        var abs = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "wav");

        var entry = new HistoryEntry { Id = "k", WavRelativePath = rel };
        store.Append(entry);
        store.Delete("k");

        store.Load().Entries.ShouldBeEmpty();
        File.Exists(abs).ShouldBeFalse();
    }

    [Fact]
    public void Delete_UnknownId_NoOp()
    {
        var store = new HistoryStore(_root);
        Should.NotThrow(() => store.Delete("never-existed"));
    }

    [Fact]
    public void Append_DoesNotLeaveTempFile()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "a" });
        Directory.GetFiles(_root, "index.json.tmp-*").ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryStoreTests"
```

Expected: `HistoryStore` not found.

- [ ] **Step 3: Implement `src/Winpepper.History/HistoryStore.cs`**

```csharp
using System.Text.Json;
using Winpepper.Core.Io;

namespace Winpepper.History;

/// <summary>
/// Persistent newest-first archive of dictation sessions. Backed by a single
/// JSON index file plus a tree of WAV files on disk. Pruned to 50 entries
/// on every <see cref="Append"/>; pruned entries' WAVs are deleted.
///
/// Thread-safety: callers are expected to serialize access (one pipeline
/// session at a time per spec §4). The store uses a process-internal lock
/// to defend against accidental concurrent <see cref="Append"/> from the UI
/// thread (e.g., delete-while-finalize).
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 50;

    /// <summary>Spec §5.4: WAVs follow a 30-day rolling retention.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _root;
    private readonly string _indexPath;
    private readonly object _gate = new();
    private readonly Func<DateTime> _utcNow;

    public HistoryStore(string root) : this(root, () => DateTime.UtcNow) { }

    // Test seam: tests can pin "now" if they need deterministic boundary checks.
    internal HistoryStore(string root, Func<DateTime> utcNow)
    {
        _root = root;
        _indexPath = Path.Combine(root, "index.json");
        _utcNow = utcNow;
        Directory.CreateDirectory(_root);
    }

    /// <summary>Absolute path to the history root (= WAV directory).</summary>
    public string Root => _root;

    /// <summary>Resolve a relative WAV path against the history root.</summary>
    public string ResolveWavPath(string relative) => Path.Combine(_root, relative);

    /// <summary>Load all entries, sorted newest-first.</summary>
    public HistoryIndex Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    private HistoryIndex LoadUnlocked()
    {
        if (!File.Exists(_indexPath)) return new HistoryIndex();

        try
        {
            var json = File.ReadAllText(_indexPath);
            var loaded = JsonSerializer.Deserialize<HistoryIndex>(json, JsonOptions) ?? new HistoryIndex();
            var sorted = loaded.Entries.OrderByDescending(e => e.CreatedAtUtc).ToList();
            return loaded with { Entries = sorted };
        }
        catch (JsonException)
        {
            return new HistoryIndex();
        }
        catch (IOException)
        {
            return new HistoryIndex();
        }
    }

    /// <summary>Insert <paramref name="entry"/>, prune entries older than <see cref="MaxAge"/>, then cap at <see cref="MaxEntries"/>.</summary>
    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            var idx = LoadUnlocked();
            var combined = idx.Entries.Concat(new[] { entry })
                              .OrderByDescending(e => e.CreatedAtUtc)
                              .ToList();

            // Tier 1: age-based prune (spec §5.4 — 30-day rolling retention).
            // We compute the cutoff once so a multi-entry prune is consistent.
            var cutoff = _utcNow() - MaxAge;
            var fresh = combined.Where(e => e.CreatedAtUtc >= cutoff).ToList();
            var stale = combined.Where(e => e.CreatedAtUtc < cutoff).ToList();

            // Tier 2: count cap (50 entries) over the fresh survivors.
            var keep = fresh.Take(MaxEntries).ToList();
            var dropForCount = fresh.Skip(MaxEntries).ToList();

            foreach (var d in stale)
                TryDeleteWav(d.WavRelativePath);
            foreach (var d in dropForCount)
                TryDeleteWav(d.WavRelativePath);

            Save(new HistoryIndex { Entries = keep });
        }
    }

    /// <summary>Remove the entry with the given id (no-op if absent) and delete its WAV.</summary>
    public void Delete(string id)
    {
        lock (_gate)
        {
            var idx = LoadUnlocked();
            var match = idx.Entries.FirstOrDefault(e => e.Id == id);
            if (match is null) return;

            TryDeleteWav(match.WavRelativePath);
            var remaining = idx.Entries.Where(e => e.Id != id).ToList();
            Save(new HistoryIndex { Entries = remaining });
        }
    }

    private void Save(HistoryIndex index)
    {
        var json = JsonSerializer.Serialize(index, JsonOptions);
        AtomicFile.WriteAllText(_indexPath, json);
    }

    private void TryDeleteWav(string relative)
    {
        if (string.IsNullOrEmpty(relative)) return;
        try
        {
            var abs = Path.Combine(_root, relative);
            if (File.Exists(abs)) File.Delete(abs);
        }
        catch
        {
            // Best-effort: a locked WAV (rare on Windows) is logged elsewhere.
        }
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryStoreTests"
```

Expected: 10 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.History/HistoryStore.cs tests/Winpepper.History.Tests/HistoryStoreTests.cs
git commit -m "feat(history): HistoryStore with newest-first pruning to 50"
```

---

## Task 5: `WavWriter` — 16 kHz mono int16 PCM

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/WavWriter.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/WavWriterTests.cs`

The pipeline produces `float[]` samples at 16 kHz mono (see `WasapiRecorder.Stop()` in Plan 1). To match the spec — "16 kHz mono int16" — we convert and write a minimal RIFF/WAVE header. Pure-managed (no NAudio dep on the History project), to keep `Winpepper.History` Linux-buildable.

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/WavWriterTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class WavWriterTests : IDisposable
{
    private readonly string _dir;
    public WavWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"wavwriter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public void WriteMono16kInt16_Writes_Valid_RIFF_Header()
    {
        var path = Path.Combine(_dir, "tone.wav");
        var samples = new float[16000]; // 1 second of silence
        WavWriter.WriteMono16kInt16(path, samples);

        var bytes = File.ReadAllBytes(path);
        // 44-byte RIFF/WAVE header + 2 bytes per sample
        bytes.Length.ShouldBe(44 + 16000 * 2);

        // RIFF
        System.Text.Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
        // WAVE
        System.Text.Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WAVE");
        // fmt 
        System.Text.Encoding.ASCII.GetString(bytes, 12, 4).ShouldBe("fmt ");
        // PCM format code = 1
        BitConverter.ToInt16(bytes, 20).ShouldBe((short)1);
        // 1 channel
        BitConverter.ToInt16(bytes, 22).ShouldBe((short)1);
        // 16000 Hz
        BitConverter.ToInt32(bytes, 24).ShouldBe(16000);
        // 16 bits per sample
        BitConverter.ToInt16(bytes, 34).ShouldBe((short)16);
        // data chunk header
        System.Text.Encoding.ASCII.GetString(bytes, 36, 4).ShouldBe("data");
    }

    [Fact]
    public void WriteMono16kInt16_Clamps_OutOfRange_Floats()
    {
        var path = Path.Combine(_dir, "clip.wav");
        var samples = new[] { 2.0f, -2.0f, 0.0f, 0.5f };
        WavWriter.WriteMono16kInt16(path, samples);
        var bytes = File.ReadAllBytes(path);
        // First sample clamped to +1.0 -> int16 32767
        BitConverter.ToInt16(bytes, 44).ShouldBe(short.MaxValue);
        // Second clamped to -1.0 -> int16 -32768
        BitConverter.ToInt16(bytes, 46).ShouldBe(short.MinValue);
        // 0.0 -> 0
        BitConverter.ToInt16(bytes, 48).ShouldBe((short)0);
        // 0.5 -> ~16384
        BitConverter.ToInt16(bytes, 50).ShouldBe((short)16384);
    }

    [Fact]
    public void WriteMono16kInt16_CreatesParentDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deep", "f.wav");
        var samples = new float[4];
        WavWriter.WriteMono16kInt16(path, samples);
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void ReadMono16kInt16_RoundTrips()
    {
        var path = Path.Combine(_dir, "rt.wav");
        var samples = new[] { 0.0f, 0.25f, -0.5f, 1.0f };
        WavWriter.WriteMono16kInt16(path, samples);
        var loaded = WavWriter.ReadMono16kInt16(path);
        loaded.Length.ShouldBe(4);
        // int16 quantization tolerance
        loaded[0].ShouldBe(0f, 1e-3);
        loaded[1].ShouldBe(0.25f, 1e-3);
        loaded[2].ShouldBe(-0.5f, 1e-3);
        loaded[3].ShouldBe(1.0f, 1e-3);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~WavWriterTests"
```

Expected: `WavWriter` not found.

- [ ] **Step 3: Implement `src/Winpepper.History/WavWriter.cs`**

```csharp
namespace Winpepper.History;

/// <summary>
/// Minimal pure-managed RIFF/WAVE reader and writer for 16 kHz mono int16 PCM.
/// We do this in-project (instead of pulling NAudio into Winpepper.History) so
/// Winpepper.History stays cross-platform and Linux-buildable.
/// </summary>
public static class WavWriter
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public static void WriteMono16kInt16(string path, ReadOnlySpan<float> samples)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var byteRate = SampleRate * Channels * (BitsPerSample / 8);
        var blockAlign = (short)(Channels * (BitsPerSample / 8));
        var dataBytes = samples.Length * (BitsPerSample / 8);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(fs);

        // RIFF header
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); // subchunk size for PCM
        w.Write((short)1); // PCM
        w.Write(Channels);
        w.Write(SampleRate);
        w.Write(byteRate);
        w.Write(blockAlign);
        w.Write(BitsPerSample);

        // data chunk
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1.0f, 1.0f);
            // -1.0 maps to short.MinValue (-32768) and +1.0 maps to short.MaxValue (+32767).
            short pcm;
            if (clamped >= 0f) pcm = (short)Math.Round(clamped * short.MaxValue);
            else               pcm = (short)Math.Round(clamped * -(double)short.MinValue);
            w.Write(pcm);
        }
    }

    /// <summary>Read a 16 kHz mono int16 WAV back to float samples in [-1, +1].</summary>
    public static float[] ReadMono16kInt16(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var r = new BinaryReader(fs);

        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        r.ReadInt32(); // file size minus 8
        if (System.Text.Encoding.ASCII.GetString(r.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        short bitsPerSample = 0;
        short channels = 0;
        int sampleRate = 0;
        byte[]? data = null;

        while (fs.Position < fs.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(r.ReadBytes(4));
            var chunkSize = r.ReadInt32();
            switch (chunkId)
            {
                case "fmt ":
                    var fmtStart = fs.Position;
                    r.ReadInt16(); // pcm format code
                    channels = r.ReadInt16();
                    sampleRate = r.ReadInt32();
                    r.ReadInt32(); // byte rate
                    r.ReadInt16(); // block align
                    bitsPerSample = r.ReadInt16();
                    fs.Position = fmtStart + chunkSize;
                    break;
                case "data":
                    data = r.ReadBytes(chunkSize);
                    break;
                default:
                    fs.Position += chunkSize;
                    break;
            }
        }

        if (channels != 1 || sampleRate != SampleRate || bitsPerSample != 16 || data is null)
            throw new InvalidDataException($"Unexpected WAV format channels={channels} rate={sampleRate} bps={bitsPerSample}");

        var sampleCount = data.Length / 2;
        var result = new float[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var pcm = BitConverter.ToInt16(data, i * 2);
            result[i] = pcm < 0 ? pcm / (float)-(double)short.MinValue : pcm / (float)short.MaxValue;
        }
        return result;
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~WavWriterTests"
```

Expected: 4 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.History/WavWriter.cs tests/Winpepper.History.Tests/WavWriterTests.cs
git commit -m "feat(history): pure-managed WAV writer/reader (16 kHz mono int16)"
```

---

## Task 6: `HistoryArchiver` — finalize-time recorder

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/HistoryArchiver.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/HistoryArchiverTests.cs`

`HistoryArchiver` is the single API the pipeline calls at session-finalize time. It:

1. Picks a uuid for the entry.
2. Writes the WAV under `YYYY-MM-DD/<uuid>.wav` (relative to the history root).
3. Builds a `HistoryEntry` from the supplied raw transcript / cleaned text / timings / window context.
4. Appends to the store (which prunes to 50).
5. Returns the created entry.

Plan 5 will extend this to write window-context-used breakdowns. Plan 4 only needs the surface described in §5.4 of the spec.

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/HistoryArchiverTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.History.Tests;

public class HistoryArchiverTests : IDisposable
{
    private readonly string _root;
    public HistoryArchiverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"archiver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Archive_WritesWavAndAppendsIndex()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store, () => new DateTime(2026, 5, 15, 10, 0, 0, DateTimeKind.Utc));

        var samples = new float[16000]; // 1s silence
        var input = new HistoryArchiveInput
        {
            Samples16k = samples,
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            AsrModelName = "parakeet-tdt-0.6b-v3",
            CleanupModelName = "qwen2.5-0.5b-instruct-q4_k_m",
            WindowContextUsed = true,
            WindowTitleAtStart = "Notepad",
            WindowTitleAtInject = "Notepad",
            Timings = new HistoryTimings { RecordMs = 1000, TranscribeMs = 200, CleanupMs = 300, InjectMs = 5, TotalMs = 1505 },
        };

        var entry = archiver.Archive(input);

        entry.RawTranscript.ShouldBe("hello world");
        entry.CleanedText.ShouldBe("Hello, world.");
        entry.WavRelativePath.ShouldBe($"2026-05-15/{entry.Id}.wav");
        entry.DurationMs.ShouldBe(1000); // 16000 samples / 16 kHz = 1 second

        // WAV exists on disk
        File.Exists(Path.Combine(_root, entry.WavRelativePath)).ShouldBeTrue();

        // Persisted in the index
        store.Load().Entries.Single().Id.ShouldBe(entry.Id);
    }

    [Fact]
    public void Archive_DurationMs_FromSampleCount()
    {
        var store = new HistoryStore(_root);
        var archiver = new HistoryArchiver(store);
        var entry = archiver.Archive(new HistoryArchiveInput
        {
            Samples16k = new float[8000], // 0.5s
            RawTranscript = "",
            CleanedText = "",
        });
        entry.DurationMs.ShouldBe(500);
    }

    [Fact]
    public void Archive_PartitionsByDay_InUtc()
    {
        var store = new HistoryStore(_root);
        var d1 = new DateTime(2026, 5, 14, 23, 59, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 15, 0, 1, 0, DateTimeKind.Utc);
        var queue = new Queue<DateTime>(new[] { d1, d2 });
        var archiver = new HistoryArchiver(store, () => queue.Dequeue());

        var e1 = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });
        var e2 = archiver.Archive(new HistoryArchiveInput { Samples16k = new float[16] });

        e1.WavRelativePath.ShouldStartWith("2026-05-14/");
        e2.WavRelativePath.ShouldStartWith("2026-05-15/");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryArchiverTests"
```

Expected: `HistoryArchiver` not found.

- [ ] **Step 3: Implement `src/Winpepper.History/HistoryArchiver.cs`**

```csharp
namespace Winpepper.History;

/// <summary>
/// Bundle of session-finalize information handed to <see cref="HistoryArchiver.Archive"/>.
/// </summary>
public sealed class HistoryArchiveInput
{
    public required float[] Samples16k { get; init; }
    public string RawTranscript { get; init; } = "";
    public string CleanedText { get; init; } = "";
    public string AsrModelName { get; init; } = "";
    public string CleanupModelName { get; init; } = "";
    public bool WindowContextUsed { get; init; }
    public string WindowTitleAtStart { get; init; } = "";
    public string WindowTitleAtInject { get; init; } = "";
    public HistoryTimings Timings { get; init; } = new();
}

/// <summary>
/// Session-finalize sink. Writes the WAV under <c>history-root/YYYY-MM-DD/uuid.wav</c>
/// (UTC date), builds a <see cref="HistoryEntry"/>, and appends it to the store.
/// Pruning to 50 happens inside <see cref="HistoryStore.Append"/>.
/// </summary>
public sealed class HistoryArchiver
{
    private const int SampleRate = 16000;

    private readonly HistoryStore _store;
    private readonly Func<DateTime> _nowUtc;

    public HistoryArchiver(HistoryStore store, Func<DateTime>? nowUtc = null)
    {
        _store = store;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    public HistoryEntry Archive(HistoryArchiveInput input)
    {
        var now = _nowUtc();
        var id = Guid.NewGuid().ToString("N");
        var day = now.ToString("yyyy-MM-dd");
        var relative = $"{day}/{id}.wav";
        var absolute = Path.Combine(_store.Root, relative);

        WavWriter.WriteMono16kInt16(absolute, input.Samples16k);

        var entry = new HistoryEntry
        {
            Id = id,
            CreatedAtUtc = now,
            RawTranscript = input.RawTranscript,
            CleanedText = input.CleanedText,
            WavRelativePath = relative,
            DurationMs = (int)((long)input.Samples16k.Length * 1000 / SampleRate),
            AsrModelName = input.AsrModelName,
            CleanupModelName = input.CleanupModelName,
            WindowContextUsed = input.WindowContextUsed,
            WindowTitleAtStart = input.WindowTitleAtStart,
            WindowTitleAtInject = input.WindowTitleAtInject,
            Timings = input.Timings,
        };

        _store.Append(entry);
        return entry;
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryArchiverTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.History/HistoryArchiver.cs tests/Winpepper.History.Tests/HistoryArchiverTests.cs
git commit -m "feat(history): HistoryArchiver — WAV + index write at finalize"
```

---

## Task 7: Amend `PipelineHost` (Plan 3's orchestrator) to archive sessions

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Winpepper.App.csproj`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppPaths.cs`

**Cross-plan coordination note:** Plan 1's `Winpepper.Cli.Pipeline` recorded the WAV in memory and discarded it after transcription. Plan 3 Task 26 retires `Winpepper.Cli` entirely (its `Program.cs`/`Pipeline.cs` are deleted; `Winpepper.App` becomes the only entry point, with a `--tray` flag for autostart). Plans 1→2→3→4 run serially, so by the time Plan 4 Task 7 lands the CLI is gone — pipeline orchestration lives in `Winpepper.App.Hosting.PipelineHost` (Plan 3 Task 18). We therefore wire `HistoryArchiver` into `PipelineHost` at the same finalize point Plan 3 Task 24 chose for the cleanup runner (after `InjectionCompleted` is applied; mirrored in both the `HoldUp` and `Toggle` stop branches).

- [ ] **Step 1: Add the `Winpepper.History` project reference to `Winpepper.App`**

Plan 3's `Winpepper.App.csproj` already references the cleanup/corrections projects (Plan 3 Task 24). Add the history reference to its Windows-only ItemGroup:

```xml
<ProjectReference Include="..\Winpepper.History\Winpepper.History.csproj" />
```

- [ ] **Step 2: Add a history-root path helper to `AppPaths`**

Append to `src/Winpepper.App/Hosting/AppPaths.cs` (Plan 3 Task 18 created this file):

```csharp
    public static string HistoryRoot => Path.Combine(Root, "history");
```

- [ ] **Step 3: Add archiver fields + constructor parameter to `PipelineHost`**

In `src/Winpepper.App/Hosting/PipelineHost.cs` (Plan 3 Task 18 defined the type; Plan 3 Task 24 added the cleanup/window-context fields), add two new fields next to the existing `_cleanup`/`_corrections`/`_windowContext` block:

```csharp
    private readonly Winpepper.History.HistoryArchiver _archiver;
    private readonly string _asrModelName;
    private readonly string _cleanupModelName;
    private System.Diagnostics.Stopwatch? _recordStopwatch;
```

Update the constructor signature to take the archiver + model-name pair. Plan 3 Task 24's constructor already accepts `cleanup`, `corrections`, `windowContext`, and `cleanupOptions`; add the two new required parameters at the end so callers can still pass `null` for the optional Plan 2 dependencies:

```csharp
    public PipelineHost(
        ILoggerFactory factory,
        SessionEngine engine,
        SessionViewModel vm,
        ISoundEffectPlayer sounds,
        HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
        string modelDir,
        Winpepper.History.HistoryArchiver archiver,
        string asrModelName,
        string cleanupModelName,
        Winpepper.Cleanup.CleanupRunner? cleanup = null,
        Winpepper.Corrections.CorrectionStore? corrections = null,
        Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null,
        Winpepper.Cleanup.CleanupOptions? cleanupOptions = null)
    {
        _log = factory.CreateLogger<PipelineHost>();
        _engine = engine;
        _vm = vm;
        _sounds = sounds;
        _hook = new HotkeyHook(hold, toggle, cancel, factory.CreateLogger<HotkeyHook>());
        _injector = new TextInjector(factory.CreateLogger<TextInjector>());
        _asr = new ParakeetSession(modelDir);
        _archiver = archiver;
        _asrModelName = asrModelName;
        _cleanupModelName = cleanupModelName;
        _cleanup = cleanup;
        _corrections = corrections;
        _windowContext = windowContext;
        _cleanupOptions = cleanupOptions ?? new Winpepper.Cleanup.CleanupOptions();
    }
```

- [ ] **Step 4: Start the record-stopwatch on `HoldDown`/`Toggle` start**

Inside the existing `HoldDown` branch, after `_recorder.Start();`, add:

```csharp
                _recordStopwatch = System.Diagnostics.Stopwatch.StartNew();
```

Mirror the same line in the `Toggle` start branch (when `_engine.State == SessionState.Idle`).

- [ ] **Step 5: Call `HistoryArchiver.Archive` at the finalize point**

Plan 3 Task 24 replaced the transcript→injection block with a transcript→cleanup→injection block. After the existing `_engine.Apply(SessionEvent.InjectionCompleted);` line (in both the `HoldUp` and `Toggle` stop branches), add the archive call. Time the transcribe + cleanup + inject phases by wrapping each in a local `Stopwatch` — same pattern as Plan 1 Task 14 used. The full replacement for the `HoldUp` body:

```csharp
            case HotkeyEventKind.HoldUp:
                if (_engine.State != SessionState.Recording) return;
                _engine.Apply(SessionEvent.StopRequested);
                _recordStopwatch?.Stop();

                var samples = _recorder!.Stop();
                _recorder.Dispose(); _recorder = null;
                _sounds.PlayStop();

                var transcribeSw = System.Diagnostics.Stopwatch.StartNew();
                var transcript = await Task.Run(() => _asr.Transcribe(samples), ct);
                transcribeSw.Stop();
                _engine.Apply(SessionEvent.TranscriptReady);

                string final = transcript.Text;
                var cleanupSw = new System.Diagnostics.Stopwatch();
                var cleanupUsedModel = "";
                var windowContextUsed = false;

                if (!string.IsNullOrWhiteSpace(final) && _cleanup is not null)
                {
                    _vm.MarkCleaningUp();

                    Task<string?>? ctxTextTask = null;
                    if (_ctxPrefetchTask is not null)
                    {
                        ctxTextTask = _ctxPrefetchTask.ContinueWith(
                            t => t.IsCompletedSuccessfully ? t.Result.Text : null,
                            ct,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }

                    var correctionsData = _corrections?.Load() ?? Winpepper.Corrections.CorrectionsData.Empty;

                    cleanupSw.Start();
                    try
                    {
                        var result = await _cleanup.RunAsync(
                            rawTranscript: final,
                            corrections: correctionsData,
                            windowContextTask: ctxTextTask,
                            options: _cleanupOptions,
                            ct: ct);
                        cleanupSw.Stop();
                        _log.LogInformation("Cleanup path={Path}, {ElapsedMs}ms",
                            result.Path, (int)result.Elapsed.TotalMilliseconds);
                        final = result.CleanedText;
                        cleanupUsedModel = _cleanupModelName;
                        // Window context was used if the prefetch ran AND the
                        // assembled prompt embedded a non-empty window block.
                        windowContextUsed = ctxTextTask is not null
                                            && result.AssembledPrompt.Contains("<WINDOW-OCR-CONTENT>");
                    }
                    catch (Exception ex)
                    {
                        cleanupSw.Stop();
                        _log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
                    }
                }

                var injectSw = System.Diagnostics.Stopwatch.StartNew();
                if (!string.IsNullOrWhiteSpace(final)) _injector.TryInject(final);
                injectSw.Stop();
                _engine.Apply(SessionEvent.InjectionCompleted);

                // Archive after injection. The archiver writes the WAV + appends
                // the index entry; pruning (50-entry cap + 30-day retention) runs
                // inside HistoryStore.Append.
                var totalMs = (int)((_recordStopwatch?.ElapsedMilliseconds ?? 0)
                                     + transcribeSw.ElapsedMilliseconds
                                     + cleanupSw.ElapsedMilliseconds
                                     + injectSw.ElapsedMilliseconds);
                _archiver.Archive(new Winpepper.History.HistoryArchiveInput
                {
                    Samples16k = samples,
                    RawTranscript = transcript.Text,
                    CleanedText = final,
                    AsrModelName = _asrModelName,
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

                _ctxPrefetchTask = null;
                _recordStopwatch = null;
                break;
```

Mirror the same finalize-and-archive block inside the `Toggle` stop branch (when `_engine.State == SessionState.Recording`). The only differences are the entry conditions; the body — transcribe / optional cleanup / inject / archive — is identical.

- [ ] **Step 6: Update `AppShell.BootstrapAsync` to construct the archiver and pass it to `PipelineHost`**

In `src/Winpepper.App/Hosting/AppShell.cs`, in the `BootstrapAsync` block where the pipeline is constructed (Plan 3 Task 24 amended this), add an archiver right before the `new PipelineHost(...)` call:

```csharp
        var historyStore = new Winpepper.History.HistoryStore(AppPaths.HistoryRoot);
        var archiver = new Winpepper.History.HistoryArchiver(historyStore);
        var cleanupModelName = settings.CleanupModelName;
```

Then replace the `new PipelineHost(...)` call with one that hands in the archiver + model names. Plan 3 Task 24 wrote:

```csharp
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         cleanup, correctionStore, windowContext, cleanupOptions);
```

Replace with:

```csharp
        var pipeline = new PipelineHost(factory, engine, sessionVm, sounds,
                                         hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                         archiver, settings.AsrModelName, cleanupModelName,
                                         cleanup, correctionStore, windowContext, cleanupOptions);
```

The `historyStore` instance is also reused by `HistoryServices` (Task 22 wires it into DI for `HistoryPage`/`HistoryDetailPage`); pass the same `historyStore` reference instead of constructing a second one. See Task 22 for the DI registration.

- [ ] **Step 7: Build on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: build succeeds.

- [ ] **Step 8: Smoke-run a session and verify the index appears**

```bash
./scripts/winrun "dotnet run --project src/Winpepper.App -c Release -- --tray" &
sleep 5
# Trigger a hold/release via the host-side audio passthrough (scripts/say.sh) +
# uinput hotkey injection. After the session completes, kill the process and
# check the index:
./scripts/winssh "Get-Content $env:LOCALAPPDATA\winpepper\history\index.json | Out-String"
./scripts/winssh "Get-ChildItem -Recurse $env:LOCALAPPDATA\winpepper\history\*.wav"
```

Expected: `index.json` exists with one entry; one `.wav` file under `history\YYYY-MM-DD\`.

- [ ] **Step 9: Commit**

```bash
git add src/Winpepper.App/Hosting src/Winpepper.App/Winpepper.App.csproj
git commit -m "feat(app): archive sessions via HistoryArchiver at finalize in PipelineHost"
```

---

## Task 8: Scaffold `Winpepper.Models` project

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/Winpepper.Models.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/PlaceholderType.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/PlaceholderTests.cs`
- Modify: `/home/jesse/git/winpepper/winpepper.sln`

- [ ] **Step 1: Create directories**

```bash
cd /home/jesse/git/winpepper
mkdir -p src/Winpepper.Models tests/Winpepper.Models.Tests
```

- [ ] **Step 2: Write `src/Winpepper.Models/Winpepper.Models.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Models</RootNamespace>
    <AssemblyName>Winpepper.Models</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write `src/Winpepper.Models/PlaceholderType.cs`**

```csharp
namespace Winpepper.Models;

internal static class PlaceholderType
{
    public const string Marker = "Winpepper.Models scaffold";
}
```

- [ ] **Step 4: Write `tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="coverlet.collector" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Winpepper.Models\Winpepper.Models.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write `tests/Winpepper.Models.Tests/PlaceholderTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Scaffold_Compiles()
    {
        true.ShouldBeTrue();
    }
}
```

- [ ] **Step 6: Add to solution**

```bash
cd /home/jesse/git/winpepper
dotnet sln add src/Winpepper.Models/Winpepper.Models.csproj
dotnet sln add tests/Winpepper.Models.Tests/Winpepper.Models.Tests.csproj
```

- [ ] **Step 7: Build and test**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build
dotnet test --filter "FullyQualifiedName~Winpepper.Models.Tests"
```

Expected: 1 placeholder test passes.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Models tests/Winpepper.Models.Tests winpepper.sln
git commit -m "scaffold(models): create Winpepper.Models project + test project"
```

---

## Task 9: `ModelKind` enum and `ModelFile` / `ModelDescriptor` records

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ModelKind.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ModelFile.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ModelDescriptor.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/ModelDescriptorTests.cs`

`ModelDescriptor` is the declarative record describing a model that lives in the registry. Each descriptor has 1..N `ModelFile`s (e.g., Parakeet ships encoder + decoder_joint + vocab; Qwen ships a single GGUF).

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/ModelDescriptorTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDescriptorTests
{
    [Fact]
    public void IsFullyInstalled_True_When_AllFilesExistAndAreNonZero()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 5 },
                new ModelFile { RelativePath = "b.bin", Url = "https://x", Sha256 = "deadbeef", SizeBytes = 4 },
            },
        };
        var installRoot = temp.Path;
        Directory.CreateDirectory(Path.Combine(installRoot, "test"));
        File.WriteAllText(Path.Combine(installRoot, "test", "a.bin"), "hello");
        File.WriteAllText(Path.Combine(installRoot, "test", "b.bin"), "abcd");
        d.IsFullyInstalled(installRoot).ShouldBeTrue();
    }

    [Fact]
    public void IsFullyInstalled_False_When_AnyFileMissing()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[]
            {
                new ModelFile { RelativePath = "a.bin", Url = "u", Sha256 = "h", SizeBytes = 5 },
                new ModelFile { RelativePath = "b.bin", Url = "u", Sha256 = "h", SizeBytes = 5 },
            },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "test"));
        File.WriteAllText(Path.Combine(temp.Path, "test", "a.bin"), "hello");
        d.IsFullyInstalled(temp.Path).ShouldBeFalse();
    }

    [Fact]
    public void IsFullyInstalled_False_When_FileEmpty()
    {
        using var temp = new TempDir();
        var d = new ModelDescriptor
        {
            Name = "test",
            Kind = ModelKind.Asr,
            DisplayName = "Test",
            InstallDirRelative = "test",
            Files = new[] { new ModelFile { RelativePath = "a.bin", Url = "u", Sha256 = "h", SizeBytes = 5 } },
        };
        Directory.CreateDirectory(Path.Combine(temp.Path, "test"));
        File.WriteAllText(Path.Combine(temp.Path, "test", "a.bin"), "");
        d.IsFullyInstalled(temp.Path).ShouldBeFalse();
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"models-test-{Guid.NewGuid():N}");
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelDescriptorTests"
```

Expected: types not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/ModelKind.cs`**

```csharp
namespace Winpepper.Models;

public enum ModelKind
{
    Asr,
    Cleanup,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Models/ModelFile.cs`**

```csharp
namespace Winpepper.Models;

/// <summary>
/// One downloadable file inside a model bundle.
/// </summary>
public sealed record ModelFile
{
    /// <summary>Path relative to the model's install directory, e.g. <c>encoder-model.int8.onnx</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Direct download URL (HuggingFace <c>/resolve/main/...</c>).</summary>
    public required string Url { get; init; }

    /// <summary>Lowercase hex SHA-256 of the fully downloaded file.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Expected total size in bytes (used for progress + sanity).</summary>
    public required long SizeBytes { get; init; }
}
```

- [ ] **Step 5: Implement `src/Winpepper.Models/ModelDescriptor.cs`**

```csharp
namespace Winpepper.Models;

/// <summary>
/// A model the user can pick. The registry (<see cref="ModelRegistry"/>) holds
/// the canonical list. The downloader (<see cref="ModelDownloader"/>) iterates
/// <see cref="Files"/> to fetch missing pieces.
/// </summary>
public sealed record ModelDescriptor
{
    /// <summary>Stable id used in <c>AppSettings.AsrModelName</c> / <c>CleanupModelName</c>.</summary>
    public required string Name { get; init; }

    public required ModelKind Kind { get; init; }

    /// <summary>Human-readable label for the Models tab.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Path under <c>%LOCALAPPDATA%\winpepper\models\</c> where files land.
    /// For Parakeet, this is <c>parakeet-tdt-0.6b-v3</c>.
    /// For cleanup models, this is <c>cleanup\&lt;name&gt;</c>.
    /// </summary>
    public required string InstallDirRelative { get; init; }

    public required IReadOnlyList<ModelFile> Files { get; init; }

    /// <summary>Sum of file sizes in bytes.</summary>
    public long TotalSizeBytes => Files.Sum(f => f.SizeBytes);

    /// <summary>True when every file in the descriptor exists, is non-empty, on disk.</summary>
    public bool IsFullyInstalled(string installRoot)
    {
        foreach (var f in Files)
        {
            var p = Path.Combine(installRoot, InstallDirRelative, f.RelativePath);
            if (!File.Exists(p)) return false;
            if (new FileInfo(p).Length == 0) return false;
        }
        return true;
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelDescriptorTests"
```

Expected: 3 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Models/ModelKind.cs src/Winpepper.Models/ModelFile.cs src/Winpepper.Models/ModelDescriptor.cs tests/Winpepper.Models.Tests/ModelDescriptorTests.cs
git commit -m "feat(models): ModelKind, ModelFile, ModelDescriptor"
```

---

## Task 10: `ModelRegistry` — hard-coded catalog

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ModelRegistry.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/ModelRegistryTests.cs`
- Create: `/home/jesse/git/winpepper/scripts/verify-model-hashes.ps1`

The registry hard-codes one ASR model (the Parakeet bundle Plan 1 already downloads) and one cleanup model (a Qwen 0.5B GGUF). SHA-256 hashes are placeholders flagged `TODO(verify-at-exec)` and MUST be replaced before merge by the verification script in Step 4 below.

**Committed URLs:**
- ASR (Parakeet TDT v3 ONNX, int8):
  - `https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx`
  - `https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx`
  - `https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt`
- Cleanup (Qwen 2.5 0.5B Instruct GGUF Q4_K_M):
  - `https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf`

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/ModelRegistryTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelRegistryTests
{
    [Fact]
    public void All_HasAtLeastOne_AsrAnd_Cleanup_Descriptor()
    {
        var registry = new ModelRegistry();
        registry.All.OfType<ModelDescriptor>().Any(d => d.Kind == ModelKind.Asr).ShouldBeTrue();
        registry.All.OfType<ModelDescriptor>().Any(d => d.Kind == ModelKind.Cleanup).ShouldBeTrue();
    }

    [Fact]
    public void Find_KnownName_ReturnsDescriptor()
    {
        var registry = new ModelRegistry();
        var d = registry.Find("parakeet-tdt-0.6b-v3");
        d.ShouldNotBeNull();
        d!.Kind.ShouldBe(ModelKind.Asr);
    }

    [Fact]
    public void Find_UnknownName_ReturnsNull()
    {
        new ModelRegistry().Find("not-a-model").ShouldBeNull();
    }

    [Fact]
    public void DefaultAsrName_And_DefaultCleanupName_ResolveInRegistry()
    {
        var r = new ModelRegistry();
        r.Find(ModelRegistry.DefaultAsrName).ShouldNotBeNull();
        r.Find(ModelRegistry.DefaultCleanupName).ShouldNotBeNull();
    }

    [Fact]
    public void Every_File_Has_NonEmptyUrl_And_PositiveSize_And_64charSha()
    {
        var r = new ModelRegistry();
        foreach (var d in r.All)
        {
            foreach (var f in d.Files)
            {
                f.Url.ShouldStartWith("https://huggingface.co/");
                f.SizeBytes.ShouldBeGreaterThan(0);
                f.Sha256.Length.ShouldBe(64);
            }
        }
    }

    [Fact]
    public void ByKind_Filters_Correctly()
    {
        var r = new ModelRegistry();
        r.ByKind(ModelKind.Asr).ShouldAllBe(d => d.Kind == ModelKind.Asr);
        r.ByKind(ModelKind.Cleanup).ShouldAllBe(d => d.Kind == ModelKind.Cleanup);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelRegistryTests"
```

Expected: `ModelRegistry` not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/ModelRegistry.cs`**

```csharp
namespace Winpepper.Models;

/// <summary>
/// Hard-coded catalog of models Winpepper knows how to download. To add or
/// update a model, edit this class and rerun scripts/verify-model-hashes.ps1
/// to refresh the SHA-256 fields.
/// </summary>
public sealed class ModelRegistry
{
    public const string DefaultAsrName = "parakeet-tdt-0.6b-v3";
    public const string DefaultCleanupName = "qwen2.5-0.5b-instruct-q4_k_m";

    private readonly List<ModelDescriptor> _all;

    public ModelRegistry()
    {
        _all = new List<ModelDescriptor>
        {
            new ModelDescriptor
            {
                Name = DefaultAsrName,
                Kind = ModelKind.Asr,
                DisplayName = "Parakeet TDT v3 (0.6B, int8 ONNX)",
                InstallDirRelative = "parakeet-tdt-0.6b-v3",
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "encoder-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 410_000_000,
                    },
                    new ModelFile
                    {
                        RelativePath = "decoder_joint-model.int8.onnx",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 18_000_000,
                    },
                    new ModelFile
                    {
                        RelativePath = "vocab.txt",
                        Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 50_000,
                    },
                },
            },
            new ModelDescriptor
            {
                Name = DefaultCleanupName,
                Kind = ModelKind.Cleanup,
                DisplayName = "Qwen 2.5 0.5B Instruct (Q4_K_M GGUF)",
                InstallDirRelative = Path.Combine("cleanup", "qwen2.5-0.5b-instruct-q4_k_m"),
                Files = new[]
                {
                    new ModelFile
                    {
                        RelativePath = "qwen2.5-0.5b-instruct-q4_k_m.gguf",
                        Url = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf",
                        // TODO(verify-at-exec): replace with SHA-256 from scripts/verify-model-hashes.ps1
                        Sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                        SizeBytes = 398_000_000,
                    },
                },
            },
        };
    }

    public IReadOnlyList<ModelDescriptor> All => _all;

    public IEnumerable<ModelDescriptor> ByKind(ModelKind kind) => _all.Where(d => d.Kind == kind);

    public ModelDescriptor? Find(string name) => _all.FirstOrDefault(d => d.Name == name);
}
```

- [ ] **Step 4: Write `scripts/verify-model-hashes.ps1`** (re-run before merging the plan)

```powershell
# Computes SHA-256 hashes for every file declared in the ModelRegistry by hitting
# the HuggingFace direct-download URLs. Prints C# snippets that can be pasted into
# ModelRegistry.cs.
#
# Usage on the VM:  ./scripts/winssh < scripts/verify-model-hashes.ps1
# Usage on Linux:   pwsh scripts/verify-model-hashes.ps1  (requires pwsh installed)
$ErrorActionPreference = "Stop"

$files = @(
    @{ Name = "encoder-model.int8.onnx";       Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/encoder-model.int8.onnx" },
    @{ Name = "decoder_joint-model.int8.onnx"; Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/decoder_joint-model.int8.onnx" },
    @{ Name = "vocab.txt";                     Url = "https://huggingface.co/istupakov/parakeet-tdt-0.6b-v3-onnx/resolve/main/vocab.txt" },
    @{ Name = "qwen2.5-0.5b-instruct-q4_k_m.gguf"; Url = "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf" }
)

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "winpepper-verify-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempDir | Out-Null

try {
    foreach ($f in $files) {
        $dest = Join-Path $tempDir $f.Name
        Write-Host "Downloading $($f.Name)..."
        Invoke-WebRequest -Uri $f.Url -OutFile $dest
        $hash = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        $size = (Get-Item $dest).Length
        Write-Host "  Sha256 = `"$hash`""
        Write-Host "  SizeBytes = $size"
        Write-Host ""
    }
}
finally {
    Remove-Item -Recurse -Force $tempDir
}
```

```bash
chmod +x /home/jesse/git/winpepper/scripts/verify-model-hashes.ps1
```

- [ ] **Step 5: Run the verifier and update placeholders**

```bash
cd /home/jesse/git/winpepper
./scripts/winssh < scripts/verify-model-hashes.ps1
```

For each printed `Sha256 = "..."` and `SizeBytes = ...`, edit `src/Winpepper.Models/ModelRegistry.cs` and replace the corresponding `TODO(verify-at-exec)` placeholder and the size constant. Once done, no `TODO(verify-at-exec)` strings should remain in `ModelRegistry.cs`.

- [ ] **Step 6: Verify pass (on Linux)**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet test --filter "FullyQualifiedName~ModelRegistryTests"
```

Expected: 6 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Models/ModelRegistry.cs tests/Winpepper.Models.Tests/ModelRegistryTests.cs scripts/verify-model-hashes.ps1
git commit -m "feat(models): ModelRegistry with Parakeet + Qwen 0.5B descriptors"
```

---

## Task 11: `DownloadProgress` event type

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/DownloadProgress.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/DownloadProgressTests.cs`

A `DownloadProgress` carries enough information for the Models tab to render a progress bar. Reported via `IProgress<DownloadProgress>` so the UI thread can marshal it onto the dispatcher.

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/DownloadProgressTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class DownloadProgressTests
{
    [Fact]
    public void PercentComplete_IsBytesOverTotal_Times100()
    {
        var p = new DownloadProgress
        {
            DescriptorName = "x",
            FileRelativePath = "a.bin",
            BytesDownloaded = 250,
            TotalBytes = 1000,
            Phase = DownloadPhase.Downloading,
        };
        p.PercentComplete.ShouldBe(25.0, 0.001);
    }

    [Fact]
    public void PercentComplete_ZeroTotal_ReturnsZero()
    {
        var p = new DownloadProgress
        {
            DescriptorName = "x",
            FileRelativePath = "a.bin",
            BytesDownloaded = 0,
            TotalBytes = 0,
            Phase = DownloadPhase.Downloading,
        };
        p.PercentComplete.ShouldBe(0.0);
    }

    [Fact]
    public void Phases_AreOrdered()
    {
        ((int)DownloadPhase.Pending).ShouldBeLessThan((int)DownloadPhase.Downloading);
        ((int)DownloadPhase.Downloading).ShouldBeLessThan((int)DownloadPhase.Verifying);
        ((int)DownloadPhase.Verifying).ShouldBeLessThan((int)DownloadPhase.Complete);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~DownloadProgressTests"
```

Expected: type not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/DownloadProgress.cs`**

```csharp
namespace Winpepper.Models;

public enum DownloadPhase
{
    Pending = 0,
    Downloading = 1,
    Verifying = 2,
    Complete = 3,
    Failed = 4,
}

/// <summary>
/// Reported via <see cref="IProgress{T}"/> as a download advances. UI binds
/// the latest <see cref="DownloadProgress"/> per (descriptor, file) pair.
/// </summary>
public sealed record DownloadProgress
{
    public required string DescriptorName { get; init; }
    public required string FileRelativePath { get; init; }
    public required long BytesDownloaded { get; init; }
    public required long TotalBytes { get; init; }
    public required DownloadPhase Phase { get; init; }
    public string? ErrorMessage { get; init; }

    public double PercentComplete => TotalBytes <= 0 ? 0.0 : 100.0 * BytesDownloaded / TotalBytes;
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~DownloadProgressTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Models/DownloadProgress.cs tests/Winpepper.Models.Tests/DownloadProgressTests.cs
git commit -m "feat(models): DownloadProgress event record + DownloadPhase enum"
```

---

## Task 12: `ChecksumVerifier` — streaming SHA-256

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ChecksumVerifier.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/ChecksumVerifierTests.cs`

Reads a file in 1 MiB chunks and computes lowercase-hex SHA-256. Streaming because GGUF cleanup models are ~400 MB.

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/ChecksumVerifierTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ChecksumVerifierTests : IDisposable
{
    private readonly string _dir;
    public ChecksumVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"checksum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task ComputeSha256Async_EmptyFile_ReturnsKnownEmptyHash()
    {
        var path = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(path, Array.Empty<byte>());
        var hash = await ChecksumVerifier.ComputeSha256Async(path, CancellationToken.None);
        hash.ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public async Task ComputeSha256Async_KnownContent()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var hash = await ChecksumVerifier.ComputeSha256Async(path, CancellationToken.None);
        hash.ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public async Task VerifyAsync_True_When_HashMatches()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CancellationToken.None);
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_CaseInsensitive_OnExpected()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", CancellationToken.None);
        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task VerifyAsync_False_When_HashMismatches()
    {
        var path = Path.Combine(_dir, "abc.bin");
        File.WriteAllText(path, "abc");
        var ok = await ChecksumVerifier.VerifyAsync(path,
            "0000000000000000000000000000000000000000000000000000000000000000", CancellationToken.None);
        ok.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ChecksumVerifierTests"
```

Expected: `ChecksumVerifier` not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/ChecksumVerifier.cs`**

```csharp
using System.Security.Cryptography;

namespace Winpepper.Models;

/// <summary>
/// Streaming SHA-256 helper for large model files. 1 MiB read buffer to keep
/// peak memory bounded.
/// </summary>
public static class ChecksumVerifier
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                            BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    public static async Task<bool> VerifyAsync(string path, string expectedHexSha256, CancellationToken ct)
    {
        var actual = await ComputeSha256Async(path, ct).ConfigureAwait(false);
        return string.Equals(actual, expectedHexSha256, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ChecksumVerifierTests"
```

Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Models/ChecksumVerifier.cs tests/Winpepper.Models.Tests/ChecksumVerifierTests.cs
git commit -m "feat(models): streaming SHA-256 checksum verifier"
```

---

## Task 13: `ModelDownloader` — `HttpClient` with range-resume

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/IHttpRangeClient.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/HttpClientRangeClient.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ModelDownloader.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/ModelDownloaderTests.cs`

The downloader does NOT use raw `HttpClient` in its tests — testing real HTTP would couple us to HuggingFace's availability. We introduce a tiny `IHttpRangeClient` abstraction so tests can supply a fake that streams bytes, throws mid-way to simulate disconnects, and asserts the resume `Range:` header.

Production code uses `HttpClientRangeClient` which wraps `HttpClient` with a single `GET` per file and an `If-Range`-aware `Range: bytes=<start>-` header when resuming a partial.

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/ModelDownloaderTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class ModelDownloaderTests : IDisposable
{
    private readonly string _root;
    public ModelDownloaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static ModelDescriptor TwoFileDescriptor(string aSha, string bSha) => new()
    {
        Name = "test",
        Kind = ModelKind.Asr,
        DisplayName = "Test",
        InstallDirRelative = "test",
        Files = new[]
        {
            new ModelFile { RelativePath = "a.bin", Url = "https://x/a", Sha256 = aSha, SizeBytes = 5 },
            new ModelFile { RelativePath = "b.bin", Url = "https://x/b", Sha256 = bSha, SizeBytes = 4 },
        },
    };

    [Fact]
    public async Task DownloadAsync_HappyPath_WritesAllFiles_AndReports100Percent()
    {
        // sha256("hello") = 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824
        // sha256("abcd")  = 88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var reports = new List<DownloadProgress>();
        var progress = new Progress<DownloadProgress>(p => reports.Add(p));
        var dl = new ModelDownloader(fake);

        await dl.DownloadAsync(d, _root, progress, CancellationToken.None);

        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
        File.ReadAllText(Path.Combine(_root, "test", "b.bin")).ShouldBe("abcd");

        // We should see at least one Complete phase per file.
        var completes = reports.Where(p => p.Phase == DownloadPhase.Complete).ToList();
        completes.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task DownloadAsync_ResumesFromPartial()
    {
        // sha256("hello") = 2cf2...
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        // Pre-create a .partial with the first 3 bytes already written.
        var partialDir = Path.Combine(_root, "test");
        Directory.CreateDirectory(partialDir);
        File.WriteAllBytes(Path.Combine(partialDir, "a.bin.partial"),
            System.Text.Encoding.ASCII.GetBytes("hel"));

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        await dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);

        // The range request for a.bin should have started at byte 3.
        fake.RequestsFor("https://x/a").Single().RangeStart.ShouldBe(3L);
        File.ReadAllText(Path.Combine(_root, "test", "a.bin")).ShouldBe("hello");
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadAsync_HashMismatch_ThrowsAndDeletesFile()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", System.Text.Encoding.ASCII.GetBytes("hello"));
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));

        var d = TwoFileDescriptor(
            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        await Should.ThrowAsync<ModelDownloadException>(() =>
            dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), CancellationToken.None));

        File.Exists(Path.Combine(_root, "test", "a.bin")).ShouldBeFalse();
        // partial should also be gone on hash failure so the next retry restarts from scratch.
        File.Exists(Path.Combine(_root, "test", "a.bin.partial")).ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadAsync_AlreadyVerified_SkipsDownload()
    {
        var fake = new FakeRangeClient();
        // Don't set any bodies — should never be called.

        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "test", "a.bin"), "hello");
        File.WriteAllText(Path.Combine(_root, "test", "b.bin"), "abcd");

        var d = TwoFileDescriptor(
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        await dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), CancellationToken.None);

        fake.AllRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DownloadAsync_CancellationPropagates()
    {
        var fake = new FakeRangeClient();
        fake.SetBody("https://x/a", new byte[1024 * 1024]); // 1 MiB to give the cancel time to fire
        fake.SetBody("https://x/b", System.Text.Encoding.ASCII.GetBytes("abcd"));
        fake.DelayPerChunkMs = 50;

        var d = TwoFileDescriptor(
            "0000000000000000000000000000000000000000000000000000000000000000",
            "88d4266fd4e6338d13b845fcf289579d209c897823b9217da3e161936f031589");

        var dl = new ModelDownloader(fake);
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            dl.DownloadAsync(d, _root, new Progress<DownloadProgress>(_ => { }), cts.Token));
    }
}

/// <summary>
/// Test double for <see cref="IHttpRangeClient"/>. Records every request and
/// yields the configured payload starting at the requested offset.
/// </summary>
internal sealed class FakeRangeClient : IHttpRangeClient
{
    public sealed record RecordedRequest(string Url, long RangeStart);

    private readonly Dictionary<string, byte[]> _bodies = new();
    private readonly List<RecordedRequest> _requests = new();

    public int DelayPerChunkMs { get; set; }

    public void SetBody(string url, byte[] body) => _bodies[url] = body;

    public IEnumerable<RecordedRequest> AllRequests => _requests;
    public IEnumerable<RecordedRequest> RequestsFor(string url) => _requests.Where(r => r.Url == url);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(
        string url, long startByte,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        _requests.Add(new RecordedRequest(url, startByte));
        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");

        const int chunkSize = 64 * 1024;
        var i = (int)startByte;
        while (i < body.Length)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(chunkSize, body.Length - i);
            yield return body.AsMemory(i, take);
            i += take;
            if (DelayPerChunkMs > 0)
                await Task.Delay(DelayPerChunkMs, ct).ConfigureAwait(false);
        }
    }

    public Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        if (!_bodies.TryGetValue(url, out var body))
            throw new InvalidOperationException($"FakeRangeClient: no body for {url}");
        return Task.FromResult((long)body.Length);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelDownloaderTests"
```

Expected: types not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/IHttpRangeClient.cs`**

```csharp
namespace Winpepper.Models;

/// <summary>
/// Minimal HTTP range-request surface used by <see cref="ModelDownloader"/>.
/// The production implementation wraps <see cref="System.Net.Http.HttpClient"/>.
/// Tests substitute a fake to avoid network IO.
/// </summary>
public interface IHttpRangeClient
{
    /// <summary>
    /// Stream bytes from <paramref name="url"/> starting at <paramref name="startByte"/>.
    /// Implementations must issue <c>Range: bytes=startByte-</c> when startByte &gt; 0.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(string url, long startByte, CancellationToken ct);

    /// <summary>Returns the full content length via a HEAD or 0-range GET.</summary>
    Task<long> GetContentLengthAsync(string url, CancellationToken ct);
}
```

- [ ] **Step 4: Implement `src/Winpepper.Models/HttpClientRangeClient.cs`**

```csharp
using System.Net.Http.Headers;

namespace Winpepper.Models;

/// <summary>
/// Production <see cref="IHttpRangeClient"/> backed by <see cref="HttpClient"/>.
/// Owns the <see cref="HttpClient"/> instance (do not dispose externally).
/// </summary>
public sealed class HttpClientRangeClient : IHttpRangeClient, IDisposable
{
    private const int CopyBufferSize = 64 * 1024;

    private readonly HttpClient _http;

    public HttpClientRangeClient() : this(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }) { }

    public HttpClientRangeClient(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Winpepper/1.0");
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> GetRangeAsync(
        string url, long startByte,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (startByte > 0)
        {
            req.Headers.Range = new RangeHeaderValue(startByte, null);
        }
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buf = new byte[CopyBufferSize];
        int read;
        while ((read = await stream.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            yield return new ReadOnlyMemory<byte>(buf, 0, read);
        }
    }

    public async Task<long> GetContentLengthAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return resp.Content.Headers.ContentLength ?? -1;
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 5: Implement `src/Winpepper.Models/ModelDownloader.cs`**

```csharp
namespace Winpepper.Models;

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message) : base(message) { }
    public ModelDownloadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Downloads every file in a <see cref="ModelDescriptor"/> to its install
/// directory. Resumes from <c>.partial</c> temp files; verifies SHA-256
/// before renaming into place; reports progress per chunk and per phase.
/// </summary>
public sealed class ModelDownloader
{
    private readonly IHttpRangeClient _http;

    public ModelDownloader(IHttpRangeClient http)
    {
        _http = http;
    }

    public async Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                                    IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var modelDir = Path.Combine(installRoot, descriptor.InstallDirRelative);
        Directory.CreateDirectory(modelDir);

        foreach (var file in descriptor.Files)
        {
            await DownloadOneAsync(descriptor, modelDir, file, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadOneAsync(ModelDescriptor descriptor, string modelDir, ModelFile file,
                                        IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var finalPath = Path.Combine(modelDir, file.RelativePath);
        var partialPath = finalPath + ".partial";

        // 1) If the final file already exists and verifies, skip.
        if (File.Exists(finalPath))
        {
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = new FileInfo(finalPath).Length,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Verifying,
            });
            if (await ChecksumVerifier.VerifyAsync(finalPath, file.Sha256, ct).ConfigureAwait(false))
            {
                progress.Report(new DownloadProgress
                {
                    DescriptorName = descriptor.Name,
                    FileRelativePath = file.RelativePath,
                    BytesDownloaded = new FileInfo(finalPath).Length,
                    TotalBytes = file.SizeBytes,
                    Phase = DownloadPhase.Complete,
                });
                return;
            }
            // Stale/corrupt — start over.
            File.Delete(finalPath);
        }

        // 2) Determine resume offset.
        long startByte = 0;
        if (File.Exists(partialPath))
        {
            startByte = new FileInfo(partialPath).Length;
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        }

        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = startByte,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Downloading,
        });

        // 3) Stream bytes.
        var totalBytes = startByte;
        try
        {
            await using (var fs = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None,
                                                 bufferSize: 64 * 1024, useAsync: true))
            {
                await foreach (var chunk in _http.GetRangeAsync(file.Url, startByte, ct).ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    await fs.WriteAsync(chunk, ct).ConfigureAwait(false);
                    totalBytes += chunk.Length;
                    progress.Report(new DownloadProgress
                    {
                        DescriptorName = descriptor.Name,
                        FileRelativePath = file.RelativePath,
                        BytesDownloaded = totalBytes,
                        TotalBytes = file.SizeBytes,
                        Phase = DownloadPhase.Downloading,
                    });
                }
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Leave .partial in place so the next attempt resumes.
            throw;
        }
        catch (Exception ex)
        {
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = totalBytes,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Failed,
                ErrorMessage = ex.Message,
            });
            throw new ModelDownloadException($"Download of {file.Url} failed: {ex.Message}", ex);
        }

        // 4) Verify checksum on the partial.
        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = totalBytes,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Verifying,
        });

        var ok = await ChecksumVerifier.VerifyAsync(partialPath, file.Sha256, ct).ConfigureAwait(false);
        if (!ok)
        {
            // Drop the corrupt download so the next attempt restarts from byte 0.
            TryDelete(partialPath);
            TryDelete(finalPath);
            progress.Report(new DownloadProgress
            {
                DescriptorName = descriptor.Name,
                FileRelativePath = file.RelativePath,
                BytesDownloaded = totalBytes,
                TotalBytes = file.SizeBytes,
                Phase = DownloadPhase.Failed,
                ErrorMessage = "SHA-256 mismatch",
            });
            throw new ModelDownloadException($"SHA-256 mismatch on {file.RelativePath}");
        }

        // 5) Promote .partial to final.
        if (File.Exists(finalPath)) File.Delete(finalPath);
        File.Move(partialPath, finalPath);

        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = file.RelativePath,
            BytesDownloaded = totalBytes,
            TotalBytes = file.SizeBytes,
            Phase = DownloadPhase.Complete,
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelDownloaderTests"
```

Expected: 5 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Models/IHttpRangeClient.cs src/Winpepper.Models/HttpClientRangeClient.cs src/Winpepper.Models/ModelDownloader.cs tests/Winpepper.Models.Tests/ModelDownloaderTests.cs
git commit -m "feat(models): ModelDownloader with range-resume + sha256 verify"
```

---

## Task 14: `MissingModelsResolver` — find descriptors needing download

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/MissingModelsResolver.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs`

The Models tab's "Download Missing Models" button calls this to decide which descriptors to enqueue.

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs`**

```csharp
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public class MissingModelsResolverTests : IDisposable
{
    private readonly string _root;
    public MissingModelsResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static ModelDescriptor Desc(string name, string installDirRelative) => new()
    {
        Name = name,
        Kind = ModelKind.Asr,
        DisplayName = name,
        InstallDirRelative = installDirRelative,
        Files = new[]
        {
            new ModelFile { RelativePath = "f.bin", Url = "https://x", Sha256 = "h", SizeBytes = 5 },
        },
    };

    [Fact]
    public void FindMissing_Returns_All_When_NothingDownloaded()
    {
        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a", "b" });
        missing.Select(m => m.Name).ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public void FindMissing_Excludes_Installed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        File.WriteAllText(Path.Combine(_root, "a", "f.bin"), "hello");

        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a", "b" });
        missing.Single().Name.ShouldBe("b");
    }

    [Fact]
    public void FindMissing_Only_Considers_NamesInScope()
    {
        var resolver = new MissingModelsResolver();
        var registry = new[] { Desc("a", "a"), Desc("b", "b"), Desc("c", "c") };
        var missing = resolver.FindMissing(registry, _root, new[] { "a" });
        missing.Select(m => m.Name).ShouldBe(new[] { "a" });
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~MissingModelsResolverTests"
```

Expected: type not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/MissingModelsResolver.cs`**

```csharp
namespace Winpepper.Models;

/// <summary>
/// Picks the descriptors that still need downloading given a list of currently
/// selected model names. The Models tab uses this for the "Download Missing
/// Models" button — it should only fetch what the user has chosen, not the
/// entire registry.
/// </summary>
public sealed class MissingModelsResolver
{
    public IReadOnlyList<ModelDescriptor> FindMissing(
        IEnumerable<ModelDescriptor> registry, string installRoot, IEnumerable<string> selectedNames)
    {
        var scope = new HashSet<string>(selectedNames, StringComparer.Ordinal);
        return registry
            .Where(d => scope.Contains(d.Name))
            .Where(d => !d.IsFullyInstalled(installRoot))
            .ToList();
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~MissingModelsResolverTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Models/MissingModelsResolver.cs tests/Winpepper.Models.Tests/MissingModelsResolverTests.cs
git commit -m "feat(models): MissingModelsResolver to scope downloads"
```

---

## Task 15: Add DiffPlex package and `WordDiff` service

**Files:**
- Modify: `/home/jesse/git/winpepper/Directory.Packages.props`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Diff/WordDiff.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Diff/WordDiffSegment.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/Diff/WordDiffTests.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.History/Winpepper.History.csproj`

DiffPlex's `Differ` gives us inline diff blocks; we wrap it in a stable `WordDiff` API that returns a sequence of `WordDiffSegment` records (Equal / Insert / Delete) suitable for the Lab's side-by-side rendering.

- [ ] **Step 1: Add DiffPlex to `Directory.Packages.props`**

Append inside the `<ItemGroup>`:

```xml
<PackageVersion Include="DiffPlex" Version="1.7.2" />
```

- [ ] **Step 2: Reference DiffPlex in `Winpepper.History.csproj`**

Update `src/Winpepper.History/Winpepper.History.csproj` to:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.History</RootNamespace>
    <AssemblyName>Winpepper.History</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="DiffPlex" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write failing test `tests/Winpepper.History.Tests/Diff/WordDiffTests.cs`**

```csharp
using Shouldly;
using Winpepper.History.Diff;
using Xunit;

namespace Winpepper.History.Tests.Diff;

public class WordDiffTests
{
    [Fact]
    public void Identical_Strings_AllEqual()
    {
        var segments = WordDiff.Compute("hello world", "hello world");
        segments.ShouldAllBe(s => s.Kind == WordDiffKind.Equal);
        string.Concat(segments.Select(s => s.Text)).ShouldBe("hello world");
    }

    [Fact]
    public void Single_Word_Substitution()
    {
        var segments = WordDiff.Compute("hello world", "hello earth");
        // Expect Equal "hello", Delete "world", Insert "earth"
        segments.Count(s => s.Kind == WordDiffKind.Equal).ShouldBeGreaterThanOrEqualTo(1);
        segments.Any(s => s.Kind == WordDiffKind.Delete && s.Text.Contains("world")).ShouldBeTrue();
        segments.Any(s => s.Kind == WordDiffKind.Insert && s.Text.Contains("earth")).ShouldBeTrue();
    }

    [Fact]
    public void Empty_Original_All_Inserts()
    {
        var segments = WordDiff.Compute("", "anything goes");
        segments.ShouldAllBe(s => s.Kind == WordDiffKind.Insert || s.Kind == WordDiffKind.Equal && string.IsNullOrEmpty(s.Text));
        string.Concat(segments.Where(s => s.Kind == WordDiffKind.Insert).Select(s => s.Text))
              .ShouldContain("anything");
    }

    [Fact]
    public void Empty_Rerun_All_Deletes()
    {
        var segments = WordDiff.Compute("anything goes", "");
        string.Concat(segments.Where(s => s.Kind == WordDiffKind.Delete).Select(s => s.Text))
              .ShouldContain("anything");
    }

    [Fact]
    public void Stable_Reconstruction_OfOriginal_And_Rerun()
    {
        var original = "the quick brown fox jumps";
        var rerun    = "the slow brown fox leaps over";
        var segments = WordDiff.Compute(original, rerun);

        // Reconstruct: Equal + Delete = original; Equal + Insert = rerun.
        var reconstructedOriginal = string.Concat(segments
            .Where(s => s.Kind == WordDiffKind.Equal || s.Kind == WordDiffKind.Delete)
            .Select(s => s.Text));
        var reconstructedRerun = string.Concat(segments
            .Where(s => s.Kind == WordDiffKind.Equal || s.Kind == WordDiffKind.Insert)
            .Select(s => s.Text));

        reconstructedOriginal.Trim().ShouldBe(original);
        reconstructedRerun.Trim().ShouldBe(rerun);
    }
}
```

- [ ] **Step 4: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~WordDiffTests"
```

Expected: `WordDiff` / `WordDiffSegment` not found.

- [ ] **Step 5: Implement `src/Winpepper.History/Diff/WordDiffSegment.cs`**

```csharp
namespace Winpepper.History.Diff;

public enum WordDiffKind
{
    Equal,
    Insert,
    Delete,
}

/// <summary>
/// One run of words in the diff. <see cref="Text"/> always includes the
/// trailing whitespace originally separating it from the next token so that
/// concatenating Equal+Delete reconstructs the original input and
/// Equal+Insert reconstructs the rerun.
/// </summary>
public sealed record WordDiffSegment(WordDiffKind Kind, string Text);
```

- [ ] **Step 6: Implement `src/Winpepper.History/Diff/WordDiff.cs`**

```csharp
using DiffPlex;
using DiffPlex.Chunkers;

namespace Winpepper.History.Diff;

/// <summary>
/// Word-level diff over two transcripts. Wraps DiffPlex's <see cref="Differ"/>
/// with a stable output shape and contiguous segment merging so the Lab UI
/// can render runs of green / red / unchanged text without single-word
/// thrashing.
/// </summary>
public static class WordDiff
{
    private static readonly WordChunker Chunker = WordChunker.Instance;

    public static IReadOnlyList<WordDiffSegment> Compute(string original, string rerun)
    {
        var differ = new Differ();
        var diff = differ.CreateDiffs(original, rerun, ignoreWhiteSpace: false, ignoreCase: false, Chunker);

        var oldTokens = diff.PiecesOld;
        var newTokens = diff.PiecesNew;
        var blocks = diff.DiffBlocks;

        var result = new List<WordDiffSegment>();
        var oldIdx = 0;
        var newIdx = 0;

        foreach (var block in blocks)
        {
            // Equal block before this diff block.
            if (block.DeleteStartA > oldIdx)
            {
                Append(result, WordDiffKind.Equal,
                    string.Concat(oldTokens.AsSpan(oldIdx, block.DeleteStartA - oldIdx).ToArray()));
            }

            if (block.DeleteCountA > 0)
            {
                Append(result, WordDiffKind.Delete,
                    string.Concat(oldTokens.AsSpan(block.DeleteStartA, block.DeleteCountA).ToArray()));
            }
            if (block.InsertCountB > 0)
            {
                Append(result, WordDiffKind.Insert,
                    string.Concat(newTokens.AsSpan(block.InsertStartB, block.InsertCountB).ToArray()));
            }

            oldIdx = block.DeleteStartA + block.DeleteCountA;
            newIdx = block.InsertStartB + block.InsertCountB;
        }

        // Trailing equal tail.
        if (oldIdx < oldTokens.Length)
        {
            Append(result, WordDiffKind.Equal,
                string.Concat(oldTokens.AsSpan(oldIdx, oldTokens.Length - oldIdx).ToArray()));
        }

        return result;
    }

    private static void Append(List<WordDiffSegment> list, WordDiffKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (list.Count > 0 && list[^1].Kind == kind)
        {
            list[^1] = list[^1] with { Text = list[^1].Text + text };
            return;
        }
        list.Add(new WordDiffSegment(kind, text));
    }
}
```

- [ ] **Step 7: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet restore && dotnet test --filter "FullyQualifiedName~WordDiffTests"
```

Expected: 5 tests pass.

- [ ] **Step 8: Commit**

```bash
git add Directory.Packages.props src/Winpepper.History/Winpepper.History.csproj src/Winpepper.History/Diff tests/Winpepper.History.Tests/Diff
git commit -m "feat(history): DiffPlex word-level diff service"
```

---

## Task 16: Extend `AppSettings` for cleanup-model selection

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Core/Settings/AppSettings.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Settings/AppSettingsCleanupModelTests.cs`

Plan 1's `AppSettings` already has `AsrModelName`. The Lab + Models tab need a corresponding `CleanupModelName` field and a way to flip the default from a Lab rerun ("Use as default"). The current-prompt selection used by Plan 2's `PromptBuilder` is also persisted here so the Lab can show it.

**Cross-plan coordination note:** Plan 2 also touches `AppSettings` to add cleanup-related settings (enable toggle, profile, custom prompt, timeout, etc.). Plan 4 must be merged AFTER Plan 2 or coordinate the field set. The field additions here (`CleanupModelName`) are additive and idempotent. The view-model in Task 20 binds to both Plan 2's and Plan 4's fields.

- [ ] **Step 1: Write failing test `tests/Winpepper.Core.Tests/Settings/AppSettingsCleanupModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Settings;
using Xunit;

namespace Winpepper.Core.Tests.Settings;

public class AppSettingsCleanupModelTests
{
    [Fact]
    public void Defaults_Include_CleanupModelName()
    {
        var s = new AppSettings();
        s.CleanupModelName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void With_RoundTrips()
    {
        var s = new AppSettings() with { CleanupModelName = "custom-model" };
        s.CleanupModelName.ShouldBe("custom-model");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~AppSettingsCleanupModelTests"
```

Expected: `CleanupModelName` not found.

- [ ] **Step 3: Add the field to `src/Winpepper.Core/Settings/AppSettings.cs`**

Insert this property below `AsrModelName`:

```csharp
    // Cleanup model selection. Bound to Winpepper.Models.ModelRegistry.DefaultCleanupName.
    public string CleanupModelName { get; init; } = "qwen2.5-0.5b-instruct-q4_k_m";
```

The full updated record (showing the location):

```csharp
public record AppSettings
{
    public int Schema { get; init; } = 1;

    // Audio
    public string MicDeviceId { get; init; } = "";

    // ASR
    public string AsrModelName { get; init; } = "parakeet-tdt-0.6b-v3";

    // Cleanup model selection. Bound to Winpepper.Models.ModelRegistry.DefaultCleanupName.
    public string CleanupModelName { get; init; } = "qwen2.5-0.5b-instruct-q4_k_m";

    // Hotkeys
    public string HoldHotkey { get; init; } = "RightCtrl+RightShift";
    public string ToggleHotkey { get; init; } = "Ctrl+Shift+Space";

    // Sound effects
    public bool PlaySounds { get; init; } = true;
}
```

- [ ] **Step 4: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~AppSettings"
```

Expected: all settings tests (Plan 1's `SettingsStoreTests` + new `AppSettingsCleanupModelTests`) pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Settings/AppSettings.cs tests/Winpepper.Core.Tests/Settings/AppSettingsCleanupModelTests.cs
git commit -m "feat(settings): add CleanupModelName field"
```

---

## Task 17: Lab transcription-rerun service (transient `ParakeetSession`)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/ITranscriptionRerunService.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/TranscriptionRerunResult.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/ParakeetTranscriptionRerunService.cs` (Windows-only)
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/FakeTranscriptionRerunService.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/Lab/FakeTranscriptionRerunServiceTests.cs`

The Lab needs to construct a fresh `ParakeetSession` against a user-selected model directory, transcribe the entry's WAV bytes, and return the resulting text plus the model name. Nothing is persisted. The interface lets the WinUI page bind a fake during design-time tooling and lets cross-platform tests cover the happy path.

The real implementation lives behind a `#if WINDOWS` guard because `ParakeetSession` and `Microsoft.ML.OnnxRuntime.DirectML` are Windows-only.

- [ ] **Step 1: Make `Winpepper.History` cross-target so the Windows-only file compiles only on Windows**

Update `src/Winpepper.History/Winpepper.History.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.History</RootNamespace>
    <AssemblyName>Winpepper.History</AssemblyName>
    <TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="DiffPlex" />
  </ItemGroup>
  <ItemGroup Condition="$(TargetFramework.Contains('windows'))">
    <ProjectReference Include="..\Winpepper.Asr\Winpepper.Asr.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Implement `src/Winpepper.History/Lab/TranscriptionRerunResult.cs`**

```csharp
namespace Winpepper.History.Lab;

public sealed record TranscriptionRerunResult
{
    public required string ModelName { get; init; }
    public required string Text { get; init; }
    public required TimeSpan Elapsed { get; init; }
}
```

- [ ] **Step 3: Implement `src/Winpepper.History/Lab/ITranscriptionRerunService.cs`**

```csharp
namespace Winpepper.History.Lab;

/// <summary>
/// Runs Parakeet (or a fake) over an existing WAV file and returns the
/// transcript. Stateless from the caller's perspective — every call constructs
/// a fresh session against the supplied model directory.
/// </summary>
public interface ITranscriptionRerunService
{
    Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct);
}
```

- [ ] **Step 4: Implement `src/Winpepper.History/Lab/FakeTranscriptionRerunService.cs`** (cross-platform; used by tests and design-time tooling)

```csharp
namespace Winpepper.History.Lab;

/// <summary>
/// Returns canned transcripts so cross-platform tests can exercise the
/// Lab view-model without loading ORT.
/// </summary>
public sealed class FakeTranscriptionRerunService : ITranscriptionRerunService
{
    private readonly Func<string, string, string> _produce;
    public FakeTranscriptionRerunService(Func<string, string, string>? produce = null)
        => _produce = produce ?? ((_, modelName) => $"[fake {modelName}]");

    public Task<TranscriptionRerunResult> RerunAsync(string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(wavPath)) throw new FileNotFoundException(wavPath);
        return Task.FromResult(new TranscriptionRerunResult
        {
            ModelName = modelName,
            Text = _produce(wavPath, modelName),
            Elapsed = TimeSpan.Zero,
        });
    }
}
```

- [ ] **Step 5: Implement `src/Winpepper.History/Lab/ParakeetTranscriptionRerunService.cs`**

```csharp
#if WINDOWS
using System.Diagnostics;
using Winpepper.Asr;

namespace Winpepper.History.Lab;

public sealed class ParakeetTranscriptionRerunService : ITranscriptionRerunService
{
    public Task<TranscriptionRerunResult> RerunAsync(
        string wavPath, string modelName, string modelDirectory, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var samples = WavWriter.ReadMono16kInt16(wavPath);
            using var session = new ParakeetSession(modelDirectory);
            var sw = Stopwatch.StartNew();
            var transcript = session.Transcribe(samples);
            sw.Stop();
            return new TranscriptionRerunResult
            {
                ModelName = modelName,
                Text = transcript.Text,
                Elapsed = sw.Elapsed,
            };
        }, ct);
    }
}
#endif
```

- [ ] **Step 6: Write failing test `tests/Winpepper.History.Tests/Lab/FakeTranscriptionRerunServiceTests.cs`**

```csharp
using Shouldly;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public class FakeTranscriptionRerunServiceTests : IDisposable
{
    private readonly string _dir;
    public FakeTranscriptionRerunServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"rerun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }

    [Fact]
    public async Task RerunAsync_ReturnsCannedText()
    {
        var wav = Path.Combine(_dir, "t.wav");
        WavWriter.WriteMono16kInt16(wav, new float[16]);

        var svc = new FakeTranscriptionRerunService((path, m) => $"canned for {m}");
        var result = await svc.RerunAsync(wav, "parakeet-test", _dir, CancellationToken.None);

        result.Text.ShouldBe("canned for parakeet-test");
        result.ModelName.ShouldBe("parakeet-test");
    }

    [Fact]
    public async Task RerunAsync_MissingWav_Throws()
    {
        var svc = new FakeTranscriptionRerunService();
        await Should.ThrowAsync<FileNotFoundException>(() =>
            svc.RerunAsync(Path.Combine(_dir, "missing.wav"), "m", _dir, CancellationToken.None));
    }
}
```

- [ ] **Step 7: Build and test**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build
dotnet test --filter "FullyQualifiedName~FakeTranscriptionRerunServiceTests"
```

Expected: 2 tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.History/Lab tests/Winpepper.History.Tests/Lab src/Winpepper.History/Winpepper.History.csproj
git commit -m "feat(history): Lab transcription-rerun service + fake"
```

---

## Task 18: Lab cleanup-rerun service (transient `CleanupRunner`)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/ICleanupRerunService.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/CleanupRerunInput.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/CleanupRerunResult.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/LlamaCleanupRerunService.cs` (Windows-only)
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/Lab/FakeCleanupRerunService.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/Lab/FakeCleanupRerunServiceTests.cs`

The cleanup-rerun service mirrors the transcription-rerun service: caller supplies the raw transcript, the user's chosen GGUF model path, optional custom-prompt override, and a window-context toggle; the service returns the cleaned text PLUS the fully assembled prompt and raw model output (so the "Show cleanup transcript" modal can display them).

**Cross-plan dependency:** Plan 2 publishes these exact public types — verified against Plan 2 lines 945–966 (`CleanupOptions`), 975–990 (`CleanupResult`), 1253–1316 (static `PromptBuilder.Build`), 1947–2061 (`CleanupRunner` ctor + `RunAsync`), and 2133–2210 (`LlamaCleanupBackend` ctor + `Dispose`):

- `Winpepper.Cleanup.LlamaCleanupBackend(string modelPath, ILogger<LlamaCleanupBackend> log, int contextSize = 4096, int gpuLayerCount = 999)` — IDisposable.
- `Winpepper.Cleanup.CleanupRunner(ILlamaCleanupBackend backend, ILogger<CleanupRunner> log)` — NOT IDisposable.
- `CleanupRunner.RunAsync(string rawTranscript, CorrectionsData corrections, Task<string?>? windowContextTask, CleanupOptions options, CancellationToken ct)` returns `Task<CleanupResult>`.
- `CleanupResult(string CleanedText, CleanupPath Path, string RawModelOutput, string AssembledPrompt, TimeSpan Elapsed)`.
- `CleanupOptions { CleanupProfile Profile; string? CustomBasePrompt; TimeSpan Timeout; float Temperature; TimeSpan WindowContextWait; bool WindowContextEnabled; int MaxNewTokensCap; }`.
- `static PromptBuilder.Build(string basePrompt, CorrectionsData corrections, string? windowContext, string userInput)` — Plan 2's only PromptBuilder surface. There is NO `PromptBuilderInput` and the rerun service does NOT call `PromptBuilder` directly; it delegates to `CleanupRunner`, which internally calls `PromptBuilder.Build` and surfaces the result via `CleanupResult.AssembledPrompt`.

- [ ] **Step 1: Add the Plan 2 references to `Winpepper.History.csproj`**

`Winpepper.Cleanup` is Windows-only (transitively pulls `LlamaSharp` + the Vulkan native backend; Plan 2 Task 1 sets `<TargetFrameworks>net9.0;net9.0-windows10.0.19041.0</TargetFrameworks>` and the real backend lives behind `#if WINDOWS`). `Winpepper.Corrections` is cross-platform pure-managed (Plan 2 Task 1 line 217: "Corrections is pure-managed, cross-platform, single TFM"), so it lands in the unconditional ItemGroup. `CleanupRerunInput.cs` is compiled on both TFMs and references `Winpepper.Corrections.CorrectionsData`, so the Corrections project reference MUST be unconditional or the cross-platform build breaks.

```xml
<ItemGroup>
    <ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="DiffPlex" />
</ItemGroup>
<ItemGroup Condition="$(TargetFramework.Contains('windows'))">
    <ProjectReference Include="..\Winpepper.Asr\Winpepper.Asr.csproj" />
    <ProjectReference Include="..\Winpepper.Cleanup\Winpepper.Cleanup.csproj" />
</ItemGroup>
```

- [ ] **Step 2: Implement `src/Winpepper.History/Lab/CleanupRerunInput.cs`**

The rerun input is intentionally narrow: the Lab does not persist corrections, so `CorrectionsData` is built fresh by the caller (see Task 20's `HistoryDetailViewModel`) and passed through. The Lab UI only exposes a custom-prompt textbox and a window-context toggle.

```csharp
using Winpepper.Corrections;

namespace Winpepper.History.Lab;

public sealed class CleanupRerunInput
{
    public required string RawTranscript { get; init; }
    public required string ModelName { get; init; }

    /// <summary>
    /// Absolute path to the GGUF file the user picked. The production rerun
    /// service hands this straight to <c>LlamaCleanupBackend</c>'s constructor.
    /// </summary>
    public required string ModelPath { get; init; }

    /// <summary>
    /// Override the base prompt for this run. Empty string means "use the
    /// built-in default" — the rerun service maps this to
    /// <c>CleanupProfile.Ordinary</c>; a non-empty string maps to
    /// <c>CleanupProfile.Custom</c> with this text as the base prompt.
    /// </summary>
    public string CustomBasePrompt { get; init; } = "";

    /// <summary>Whether the assembled prompt should include the window-context block.</summary>
    public bool IncludeWindowContext { get; init; }

    /// <summary>
    /// Pre-fetched window context text. The Lab does not refetch on its own;
    /// v1 leaves this empty unless the caller wires a refetch.
    /// </summary>
    public string WindowContextText { get; init; } = "";

    /// <summary>
    /// Corrections data (preferred-transcription hints + misheard-replacement
    /// map) to pass through to <c>CleanupRunner</c>. Empty for an experiment
    /// that ignores user corrections; otherwise the caller loads the live data.
    /// </summary>
    public CorrectionsData Corrections { get; init; } = CorrectionsData.Empty;
}
```

- [ ] **Step 3: Implement `src/Winpepper.History/Lab/CleanupRerunResult.cs`**

```csharp
namespace Winpepper.History.Lab;

public sealed record CleanupRerunResult
{
    public required string ModelName { get; init; }

    /// <summary>The fully assembled prompt fed to the model. Surfaced in the "Show cleanup transcript" modal.</summary>
    public required string AssembledPrompt { get; init; }

    /// <summary>Raw model output before sanitization (think-tag stripping etc.). Surfaced in the modal.</summary>
    public required string RawOutput { get; init; }

    /// <summary>Final cleaned text shown in the Lab.</summary>
    public required string CleanedText { get; init; }

    public required TimeSpan Elapsed { get; init; }
}
```

- [ ] **Step 4: Implement `src/Winpepper.History/Lab/ICleanupRerunService.cs`**

```csharp
namespace Winpepper.History.Lab;

public interface ICleanupRerunService
{
    Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct);
}
```

- [ ] **Step 5: Implement `src/Winpepper.History/Lab/FakeCleanupRerunService.cs`**

```csharp
namespace Winpepper.History.Lab;

public sealed class FakeCleanupRerunService : ICleanupRerunService
{
    private readonly Func<CleanupRerunInput, (string Prompt, string Raw, string Clean)> _produce;
    public FakeCleanupRerunService(Func<CleanupRerunInput, (string, string, string)>? produce = null)
        => _produce = produce ?? (i => ($"PROMPT[{i.ModelName}]:{i.RawTranscript}", $"RAW[{i.ModelName}]", $"CLEAN[{i.ModelName}] {i.RawTranscript}"));

    public Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (p, r, c) = _produce(input);
        return Task.FromResult(new CleanupRerunResult
        {
            ModelName = input.ModelName,
            AssembledPrompt = p,
            RawOutput = r,
            CleanedText = c,
            Elapsed = TimeSpan.Zero,
        });
    }
}
```

- [ ] **Step 6: Implement `src/Winpepper.History/Lab/LlamaCleanupRerunService.cs`**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Cleanup;

namespace Winpepper.History.Lab;

/// <summary>
/// Production rerun service. Constructs a transient
/// <see cref="LlamaCleanupBackend"/> against the user-selected GGUF, wraps it
/// in a <see cref="CleanupRunner"/>, and delegates to
/// <see cref="CleanupRunner.RunAsync"/>. Nothing is persisted back to the
/// history entry — this is an experiment, not an edit.
/// </summary>
public sealed class LlamaCleanupRerunService : ICleanupRerunService
{
    private readonly ILoggerFactory _loggerFactory;

    public LlamaCleanupRerunService(ILoggerFactory? loggerFactory = null)
        => _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public async Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct)
    {
        // Map the Lab's narrow inputs to Plan 2's CleanupOptions. A non-empty
        // CustomBasePrompt means "use Custom profile with this text"; empty
        // means "fall back to the built-in Ordinary prompt".
        var hasCustom = !string.IsNullOrWhiteSpace(input.CustomBasePrompt);
        var options = new CleanupOptions
        {
            Profile = hasCustom ? CleanupProfile.Custom : CleanupProfile.Ordinary,
            CustomBasePrompt = hasCustom ? input.CustomBasePrompt : null,
            WindowContextEnabled = input.IncludeWindowContext,
        };

        // Pre-resolved window context: the Lab does not refetch live, so feed
        // the caller-supplied text via an already-completed Task<string?> when
        // the toggle is on; otherwise null (and the runner skips that block
        // regardless because WindowContextEnabled controls inclusion).
        Task<string?>? windowContextTask = null;
        if (input.IncludeWindowContext && !string.IsNullOrEmpty(input.WindowContextText))
            windowContextTask = Task.FromResult<string?>(input.WindowContextText);

        // The backend is transient: one rerun loads the GGUF, runs once, and
        // disposes. This keeps Lab experiments off the production runner.
        using var backend = new LlamaCleanupBackend(
            input.ModelPath,
            _loggerFactory.CreateLogger<LlamaCleanupBackend>());

        var runner = new CleanupRunner(
            backend,
            _loggerFactory.CreateLogger<CleanupRunner>());

        var result = await runner.RunAsync(
            rawTranscript: input.RawTranscript,
            corrections: input.Corrections,
            windowContextTask: windowContextTask,
            options: options,
            ct: ct).ConfigureAwait(false);

        return new CleanupRerunResult
        {
            ModelName = input.ModelName,
            AssembledPrompt = result.AssembledPrompt,
            RawOutput = result.RawModelOutput,
            CleanedText = result.CleanedText,
            Elapsed = result.Elapsed,
        };
    }
}
#endif
```

Note: the rerun service does NOT wrap `CleanupRunner` in a `using` block — Plan 2's `CleanupRunner` is not `IDisposable`. The `LlamaCleanupBackend` IS `IDisposable` and IS wrapped, so the GGUF is unloaded as soon as the rerun finishes.

- [ ] **Step 7: Write failing test `tests/Winpepper.History.Tests/Lab/FakeCleanupRerunServiceTests.cs`**

```csharp
using Shouldly;
using Winpepper.Corrections;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public class FakeCleanupRerunServiceTests
{
    [Fact]
    public async Task RerunAsync_ReturnsAssembledPromptAndCleanedText()
    {
        var svc = new FakeCleanupRerunService();
        var input = new CleanupRerunInput
        {
            RawTranscript = "hello world",
            ModelName = "qwen-test",
            ModelPath = "/tmp/qwen-test.gguf",
        };
        var result = await svc.RerunAsync(input, CancellationToken.None);
        result.ModelName.ShouldBe("qwen-test");
        result.AssembledPrompt.ShouldContain("hello world");
        result.CleanedText.ShouldContain("hello world");
    }

    [Fact]
    public async Task RerunAsync_HonorsCustomProduce()
    {
        var svc = new FakeCleanupRerunService(_ => ("P", "R", "C"));
        var result = await svc.RerunAsync(new CleanupRerunInput
        {
            RawTranscript = "x", ModelName = "m", ModelPath = "/tmp/m.gguf",
        }, CancellationToken.None);
        result.AssembledPrompt.ShouldBe("P");
        result.RawOutput.ShouldBe("R");
        result.CleanedText.ShouldBe("C");
    }

    [Fact]
    public async Task RerunAsync_PassesCorrectionsThrough()
    {
        CleanupRerunInput? captured = null;
        var svc = new FakeCleanupRerunService(i => { captured = i; return ("p", "r", "c"); });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" },
        };
        await svc.RerunAsync(new CleanupRerunInput
        {
            RawTranscript = "we tested chat gbt",
            ModelName = "m",
            ModelPath = "/tmp/m.gguf",
            Corrections = corrections,
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Corrections.Replacements.ShouldContainKey("chat gbt");
    }
}
```

- [ ] **Step 8: Build and test**

```bash
cd /home/jesse/git/winpepper
export DOTNET_ROOT="$HOME/.dotnet"
dotnet restore
dotnet build
dotnet test --filter "FullyQualifiedName~FakeCleanupRerunServiceTests"
```

Expected: 3 tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Winpepper.History/Lab tests/Winpepper.History.Tests/Lab src/Winpepper.History/Winpepper.History.csproj
git commit -m "feat(history): Lab cleanup-rerun service + fake"
```

---

## Task 19: `HistoryListViewModel` (cross-platform)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/ViewModels/HistoryListViewModel.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/ViewModels/HistoryRowViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/ViewModels/HistoryListViewModelTests.cs`

View-models live in `Winpepper.History` (cross-platform) so they can be unit-tested on Linux. The Windows-only WinUI page (Task 22) just binds to them. They implement `INotifyPropertyChanged` manually — no MVVM toolkit dependency to keep this library tiny.

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/ViewModels/HistoryListViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.History.ViewModels;
using Xunit;

namespace Winpepper.History.Tests.ViewModels;

public class HistoryListViewModelTests : IDisposable
{
    private readonly string _root;
    public HistoryListViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmlist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Refresh_LoadsEntries_NewestFirst()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "old", CreatedAtUtc = DateTime.UtcNow.AddMinutes(-5), RawTranscript = "old" });
        store.Append(new HistoryEntry { Id = "new", CreatedAtUtc = DateTime.UtcNow, RawTranscript = "new" });

        var vm = new HistoryListViewModel(store);
        vm.Refresh();

        vm.Rows.Count.ShouldBe(2);
        vm.Rows[0].Entry.Id.ShouldBe("new");
        vm.Rows[1].Entry.Id.ShouldBe("old");
    }

    [Fact]
    public void Refresh_FiresPropertyChanged_ForRows()
    {
        var store = new HistoryStore(_root);
        var vm = new HistoryListViewModel(store);
        var fired = false;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryListViewModel.Rows)) fired = true; };
        vm.Refresh();
        fired.ShouldBeTrue();
    }

    [Fact]
    public void DeleteSelected_RemovesEntryAndRefreshes()
    {
        var store = new HistoryStore(_root);
        store.Append(new HistoryEntry { Id = "a", RawTranscript = "x" });
        var vm = new HistoryListViewModel(store);
        vm.Refresh();

        vm.DeleteSelected(vm.Rows[0]);
        vm.Rows.ShouldBeEmpty();
        store.Load().Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Row_FormatsTimestamp_AndDuration()
    {
        var entry = new HistoryEntry
        {
            CreatedAtUtc = new DateTime(2026, 5, 15, 14, 30, 0, DateTimeKind.Utc),
            DurationMs = 2500,
            RawTranscript = "hi",
        };
        var row = new HistoryRowViewModel(entry);
        row.DurationDisplay.ShouldBe("2.5s");
        row.TimestampDisplay.ShouldContain("2026");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryListViewModelTests"
```

Expected: view-model types not found.

- [ ] **Step 3: Implement `src/Winpepper.History/ViewModels/HistoryRowViewModel.cs`**

```csharp
using System.ComponentModel;

namespace Winpepper.History.ViewModels;

public sealed class HistoryRowViewModel : INotifyPropertyChanged
{
    public HistoryRowViewModel(HistoryEntry entry) { Entry = entry; }

    public HistoryEntry Entry { get; }

    public string TimestampDisplay => Entry.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TranscriptPreviewDisplay => Entry.TranscriptPreview;
    public string DurationDisplay
    {
        get
        {
            var s = Entry.DurationMs / 1000.0;
            return $"{s:F1}s";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- [ ] **Step 4: Implement `src/Winpepper.History/ViewModels/HistoryListViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.History.ViewModels;

public sealed class HistoryListViewModel : INotifyPropertyChanged
{
    private readonly HistoryStore _store;
    private ObservableCollection<HistoryRowViewModel> _rows = new();

    public HistoryListViewModel(HistoryStore store) { _store = store; }

    public ObservableCollection<HistoryRowViewModel> Rows
    {
        get => _rows;
        private set { _rows = value; OnPropertyChanged(); }
    }

    public void Refresh()
    {
        var loaded = _store.Load();
        Rows = new ObservableCollection<HistoryRowViewModel>(
            loaded.Entries.Select(e => new HistoryRowViewModel(e)));
    }

    public void DeleteSelected(HistoryRowViewModel row)
    {
        _store.Delete(row.Entry.Id);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryListViewModelTests"
```

Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.History/ViewModels tests/Winpepper.History.Tests/ViewModels
git commit -m "feat(history): HistoryListViewModel + HistoryRowViewModel"
```

---

## Task 20: `HistoryDetailViewModel` (Lab)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.History/ViewModels/RerunPanelViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.History.Tests/ViewModels/HistoryDetailViewModelTests.cs`

The detail VM exposes:
- The original transcript + cleaned text (read-only).
- Two `RerunPanelViewModel`s — one for transcription, one for cleanup.
- A "Show cleanup transcript" string pair (assembled prompt + raw output) populated after a cleanup rerun.
- A "Promote to default" callback handed in by the host (so the VM doesn't depend on `SettingsStore` types).

- [ ] **Step 1: Write failing test `tests/Winpepper.History.Tests/ViewModels/HistoryDetailViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.History.Lab;
using Winpepper.History.ViewModels;
using Xunit;

namespace Winpepper.History.Tests.ViewModels;

public class HistoryDetailViewModelTests : IDisposable
{
    private readonly string _root;
    public HistoryDetailViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmdetail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private HistoryEntry NewEntry()
    {
        var wav = "2026-05-15/x.wav";
        var abs = Path.Combine(_root, wav);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        WavWriter.WriteMono16kInt16(abs, new float[16]);
        return new HistoryEntry
        {
            Id = "x",
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            WavRelativePath = wav,
        };
    }

    [Fact]
    public async Task RunTranscriptionRerun_PopulatesResult_AndDiff()
    {
        var entry = NewEntry();
        var fakeAsr = new FakeTranscriptionRerunService((_, m) => "hello earth");
        var fakeCleanup = new FakeCleanupRerunService();
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.TranscriptionPanel.SelectedModelName = "parakeet-alt";
        vm.TranscriptionPanel.SelectedModelDirectory = _root;
        await vm.TranscriptionPanel.RunAsync(CancellationToken.None);

        vm.TranscriptionPanel.RerunText.ShouldBe("hello earth");
        vm.TranscriptionPanel.Diff.Count.ShouldBeGreaterThan(0);
        // Original is "hello world" — rerun is "hello earth" — at least one Insert "earth"
        vm.TranscriptionPanel.Diff.Any(s => s.Kind == Winpepper.History.Diff.WordDiffKind.Insert
                                          && s.Text.Contains("earth")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCleanupRerun_PopulatesCleanedText_PromptAndRawOutput()
    {
        var entry = NewEntry();
        var fakeAsr = new FakeTranscriptionRerunService();
        var fakeCleanup = new FakeCleanupRerunService(i =>
            ("PROMPT-BODY", "<think>x</think>raw", $"cleaned: {i.RawTranscript}"));
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.CleanupPanel.SelectedModelPath = Path.Combine(_root, "qwen-alt.gguf");
        await vm.CleanupPanel.RunAsync(CancellationToken.None);

        vm.CleanupPanel.RerunText.ShouldBe("cleaned: hello world");
        vm.CleanupAssembledPrompt.ShouldBe("PROMPT-BODY");
        vm.CleanupRawOutput.ShouldContain("raw");
    }

    [Fact]
    public async Task RunCleanupRerun_DoesNotMutatePersistedEntry()
    {
        // Spec §10.1: Lab reruns are ephemeral experiments. The on-disk
        // history entry must be byte-for-byte identical after a rerun.
        var entry = NewEntry();
        var entryBeforeRaw = entry.RawTranscript;
        var entryBeforeClean = entry.CleanedText;
        var entryBeforeWav = entry.WavRelativePath;
        var entryBeforeId = entry.Id;

        var fakeAsr = new FakeTranscriptionRerunService();
        var fakeCleanup = new FakeCleanupRerunService(i =>
            ("MUTATED-PROMPT", "MUTATED-RAW", "MUTATED-CLEAN"));
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.CleanupPanel.SelectedModelPath = Path.Combine(_root, "qwen-alt.gguf");
        vm.CleanupCustomPrompt = "completely different prompt";
        await vm.CleanupPanel.RunAsync(CancellationToken.None);

        // The VM surfaces the rerun result for the UI:
        vm.CleanupPanel.RerunText.ShouldBe("MUTATED-CLEAN");
        // ...but the underlying entry is untouched.
        vm.Entry.RawTranscript.ShouldBe(entryBeforeRaw);
        vm.Entry.CleanedText.ShouldBe(entryBeforeClean);
        vm.Entry.WavRelativePath.ShouldBe(entryBeforeWav);
        vm.Entry.Id.ShouldBe(entryBeforeId);
        vm.OriginalTranscript.ShouldBe(entryBeforeRaw);
        vm.OriginalCleanedText.ShouldBe(entryBeforeClean);
    }

    [Fact]
    public void PromoteAsrDefault_InvokesCallback_WithSelectedModel()
    {
        var entry = NewEntry();
        string? promoted = null;
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: n => promoted = n,
            promoteCleanupDefault: _ => { });

        vm.TranscriptionPanel.SelectedModelName = "parakeet-alt";
        vm.PromoteTranscriptionRerunAsDefault();
        promoted.ShouldBe("parakeet-alt");
    }

    [Fact]
    public void PromoteCleanupDefault_InvokesCallback_WithSelectedModel()
    {
        var entry = NewEntry();
        string? promoted = null;
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: _ => { },
            promoteCleanupDefault: n => promoted = n);

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.PromoteCleanupRerunAsDefault();
        promoted.ShouldBe("qwen-alt");
    }

    [Fact]
    public void OriginalProperties_ExposeEntryValues()
    {
        var entry = NewEntry();
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });
        vm.OriginalTranscript.ShouldBe("hello world");
        vm.OriginalCleanedText.ShouldBe("Hello, world.");
        vm.WavAbsolutePath.ShouldBe(Path.Combine(_root, "2026-05-15", "x.wav"));
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryDetailViewModelTests"
```

Expected: types not found.

- [ ] **Step 3: Implement `src/Winpepper.History/ViewModels/RerunPanelViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.History.Diff;

namespace Winpepper.History.ViewModels;

public sealed class RerunPanelViewModel : INotifyPropertyChanged
{
    public string SelectedModelName { get; set; } = "";

    /// <summary>
    /// Absolute path to the model on disk. For ASR (Parakeet) this is the
    /// model directory; for cleanup (GGUF) this is the model directory too —
    /// the path-to-the-file variant lives in <see cref="SelectedModelPath"/>.
    /// </summary>
    public string SelectedModelDirectory { get; set; } = "";

    /// <summary>
    /// Absolute path to a single model file (used by cleanup GGUFs which
    /// <c>LlamaCleanupBackend</c> opens by file path, not by directory).
    /// </summary>
    public string SelectedModelPath { get; set; } = "";

    private string _rerunText = "";
    public string RerunText
    {
        get => _rerunText;
        private set { _rerunText = value; OnPropertyChanged(); OnPropertyChanged(nameof(Diff)); }
    }

    public IReadOnlyList<WordDiffSegment> Diff { get; private set; } = Array.Empty<WordDiffSegment>();

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { _isRunning = value; OnPropertyChanged(); }
    }

    public Func<CancellationToken, Task<string>> Runner { get; init; } = _ => Task.FromResult("");
    public string Baseline { get; init; } = "";

    public async Task RunAsync(CancellationToken ct)
    {
        IsRunning = true;
        try
        {
            var text = await Runner(ct).ConfigureAwait(false);
            RerunText = text;
            Diff = WordDiff.Compute(Baseline, text);
            OnPropertyChanged(nameof(Diff));
        }
        finally
        {
            IsRunning = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Implement `src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs`**

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Winpepper.History.Lab;

namespace Winpepper.History.ViewModels;

public sealed class HistoryDetailViewModel : INotifyPropertyChanged
{
    private readonly string _historyRoot;
    private readonly ITranscriptionRerunService _transcriptionService;
    private readonly ICleanupRerunService _cleanupService;
    private readonly Action<string> _promoteAsrDefault;
    private readonly Action<string> _promoteCleanupDefault;

    public HistoryDetailViewModel(
        HistoryEntry entry,
        string historyRoot,
        ITranscriptionRerunService transcriptionService,
        ICleanupRerunService cleanupService,
        Action<string> promoteAsrDefault,
        Action<string> promoteCleanupDefault)
    {
        Entry = entry;
        _historyRoot = historyRoot;
        _transcriptionService = transcriptionService;
        _cleanupService = cleanupService;
        _promoteAsrDefault = promoteAsrDefault;
        _promoteCleanupDefault = promoteCleanupDefault;

        TranscriptionPanel = new RerunPanelViewModel
        {
            Baseline = entry.RawTranscript,
            Runner = async ct =>
            {
                var r = await _transcriptionService.RerunAsync(
                    WavAbsolutePath, TranscriptionPanel.SelectedModelName,
                    TranscriptionPanel.SelectedModelDirectory, ct);
                return r.Text;
            },
        };

        CleanupPanel = new RerunPanelViewModel
        {
            Baseline = entry.CleanedText,
            Runner = async ct =>
            {
                var r = await _cleanupService.RerunAsync(new CleanupRerunInput
                {
                    RawTranscript = entry.RawTranscript,
                    ModelName = CleanupPanel.SelectedModelName,
                    // Plan 2's LlamaCleanupBackend takes a path to the .gguf
                    // file (not the directory). The Lab page resolves the
                    // file before assigning SelectedModelPath.
                    ModelPath = CleanupPanel.SelectedModelPath,
                    CustomBasePrompt = CleanupCustomPrompt,
                    IncludeWindowContext = IncludeWindowContextInRerun,
                    WindowContextText = "", // Lab doesn't refetch on its own; user can paste in v1
                    Corrections = Winpepper.Corrections.CorrectionsData.Empty,
                }, ct);
                CleanupAssembledPrompt = r.AssembledPrompt;
                CleanupRawOutput = r.RawOutput;
                return r.CleanedText;
            },
        };
    }

    public HistoryEntry Entry { get; }

    public string OriginalTranscript => Entry.RawTranscript;
    public string OriginalCleanedText => Entry.CleanedText;
    public string WavAbsolutePath => Path.Combine(_historyRoot, Entry.WavRelativePath);

    public RerunPanelViewModel TranscriptionPanel { get; }
    public RerunPanelViewModel CleanupPanel { get; }

    public string CleanupCustomPrompt { get; set; } = "";
    public bool IncludeWindowContextInRerun { get; set; }

    private string _cleanupAssembledPrompt = "";
    public string CleanupAssembledPrompt
    {
        get => _cleanupAssembledPrompt;
        private set { _cleanupAssembledPrompt = value; OnPropertyChanged(); }
    }

    private string _cleanupRawOutput = "";
    public string CleanupRawOutput
    {
        get => _cleanupRawOutput;
        private set { _cleanupRawOutput = value; OnPropertyChanged(); }
    }

    public void PromoteTranscriptionRerunAsDefault()
        => _promoteAsrDefault(TranscriptionPanel.SelectedModelName);

    public void PromoteCleanupRerunAsDefault()
        => _promoteCleanupDefault(CleanupPanel.SelectedModelName);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 5: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~HistoryDetailViewModelTests"
```

Expected: 6 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.History/ViewModels/HistoryDetailViewModel.cs src/Winpepper.History/ViewModels/RerunPanelViewModel.cs tests/Winpepper.History.Tests/ViewModels/HistoryDetailViewModelTests.cs
git commit -m "feat(history): HistoryDetailViewModel + RerunPanelViewModel"
```

---

## Task 21: `ModelsTabViewModel` (cross-platform)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ViewModels/ModelCardViewModel.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs`

The Models tab shows two cards (one per kind). Each card tracks:
- Current selection (model name).
- A list of available descriptors (the registry filtered by kind).
- Per-file download status (idle / downloading X% / verifying / complete / failed).
- A "Download Missing" button that calls into `MissingModelsResolver` + `ModelDownloader`.

- [ ] **Step 1: Write failing test `tests/Winpepper.Models.Tests/ViewModels/ModelsTabViewModelTests.cs`**

```csharp
using Shouldly;
using Winpepper.Models.ViewModels;
using Xunit;

namespace Winpepper.Models.Tests.ViewModels;

public class ModelsTabViewModelTests : IDisposable
{
    private readonly string _root;
    public ModelsTabViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmmodels-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Initialize_BuildsOneCardPerKind()
    {
        var registry = new ModelRegistry();
        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        vm.AsrCard.SelectedName.ShouldBe("parakeet-tdt-0.6b-v3");
        vm.CleanupCard.SelectedName.ShouldBe("qwen2.5-0.5b-instruct-q4_k_m");
        vm.AsrCard.Available.ShouldAllBe(d => d.Kind == ModelKind.Asr);
        vm.CleanupCard.Available.ShouldAllBe(d => d.Kind == ModelKind.Cleanup);
    }

    [Fact]
    public void IsInstalled_ReflectsDisk()
    {
        var registry = new ModelRegistry();
        // Create the Parakeet files so the descriptor "looks installed".
        var d = registry.Find("parakeet-tdt-0.6b-v3")!;
        var modelDir = Path.Combine(_root, d.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        foreach (var f in d.Files)
            File.WriteAllText(Path.Combine(modelDir, f.RelativePath), "x");

        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: d.Name, currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        vm.AsrCard.IsSelectedInstalled.ShouldBeTrue();
        vm.CleanupCard.IsSelectedInstalled.ShouldBeFalse();
    }

    [Fact]
    public async Task DownloadMissingAsync_OnlyEnqueuesMissingSelected()
    {
        var registry = new ModelRegistry();
        var fake = new FakeDownloader();
        var vm = new ModelsTabViewModel(registry, _root, fake,
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: _ => { }, promoteCleanup: _ => { });

        await vm.DownloadMissingAsync(CancellationToken.None);

        fake.DownloadedNames.ShouldContain("parakeet-tdt-0.6b-v3");
        fake.DownloadedNames.ShouldContain("qwen2.5-0.5b-instruct-q4_k_m");
    }

    [Fact]
    public void SetAsrSelection_FiresPromote()
    {
        var registry = new ModelRegistry();
        string? promoted = null;
        var vm = new ModelsTabViewModel(registry, _root, new FakeDownloader(),
            currentAsrName: "parakeet-tdt-0.6b-v3",
            currentCleanupName: "qwen2.5-0.5b-instruct-q4_k_m",
            promoteAsr: n => promoted = n, promoteCleanup: _ => { });

        vm.AsrCard.SelectedName = "parakeet-tdt-0.6b-v3";
        vm.AsrCard.CommitSelection();
        promoted.ShouldBe("parakeet-tdt-0.6b-v3");
    }
}

internal sealed class FakeDownloader : ModelsTabViewModel.IDownloader
{
    public List<string> DownloadedNames { get; } = new();

    public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                              IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        DownloadedNames.Add(descriptor.Name);
        progress.Report(new DownloadProgress
        {
            DescriptorName = descriptor.Name,
            FileRelativePath = descriptor.Files[0].RelativePath,
            BytesDownloaded = descriptor.Files[0].SizeBytes,
            TotalBytes = descriptor.Files[0].SizeBytes,
            Phase = DownloadPhase.Complete,
        });
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelsTabViewModelTests"
```

Expected: types not found.

- [ ] **Step 3: Implement `src/Winpepper.Models/ViewModels/ModelCardViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Winpepper.Models.ViewModels;

public sealed class ModelCardViewModel : INotifyPropertyChanged
{
    private readonly string _installRoot;
    private readonly Action<string> _promote;

    public ModelCardViewModel(ModelKind kind, IEnumerable<ModelDescriptor> available,
                              string installRoot, string selectedName, Action<string> promote)
    {
        Kind = kind;
        Available = new ObservableCollection<ModelDescriptor>(available);
        _installRoot = installRoot;
        _selectedName = selectedName;
        _promote = promote;
    }

    public ModelKind Kind { get; }
    public ObservableCollection<ModelDescriptor> Available { get; }

    private string _selectedName;
    public string SelectedName
    {
        get => _selectedName;
        set { _selectedName = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSelectedInstalled)); }
    }

    public ModelDescriptor? SelectedDescriptor =>
        Available.FirstOrDefault(d => d.Name == SelectedName);

    public bool IsSelectedInstalled =>
        SelectedDescriptor?.IsFullyInstalled(_installRoot) ?? false;

    public ObservableCollection<DownloadProgress> ProgressByFile { get; } = new();

    public void ReportProgress(DownloadProgress progress)
    {
        // Replace last entry with the same key for ObservableCollection-friendly UI binding.
        for (var i = 0; i < ProgressByFile.Count; i++)
        {
            if (ProgressByFile[i].DescriptorName == progress.DescriptorName
                && ProgressByFile[i].FileRelativePath == progress.FileRelativePath)
            {
                ProgressByFile[i] = progress;
                return;
            }
        }
        ProgressByFile.Add(progress);
    }

    public void CommitSelection() => _promote(SelectedName);

    /// <summary>
    /// Raise <see cref="INotifyPropertyChanged.PropertyChanged"/> for
    /// <see cref="IsSelectedInstalled"/>. Call after a download finishes so
    /// the UI re-reads the derived "yes/no" label without round-tripping
    /// through <see cref="SelectedName"/>.
    /// </summary>
    public void RaiseIsSelectedInstalledChanged()
        => OnPropertyChanged(nameof(IsSelectedInstalled));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 4: Implement `src/Winpepper.Models/ViewModels/ModelsTabViewModel.cs`**

```csharp
using System.ComponentModel;

namespace Winpepper.Models.ViewModels;

public sealed class ModelsTabViewModel : INotifyPropertyChanged
{
    public interface IDownloader
    {
        Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                           IProgress<DownloadProgress> progress, CancellationToken ct);
    }

    private readonly ModelRegistry _registry;
    private readonly string _installRoot;
    private readonly IDownloader _downloader;

    public ModelsTabViewModel(ModelRegistry registry, string installRoot, IDownloader downloader,
                              string currentAsrName, string currentCleanupName,
                              Action<string> promoteAsr, Action<string> promoteCleanup)
    {
        _registry = registry;
        _installRoot = installRoot;
        _downloader = downloader;

        AsrCard = new ModelCardViewModel(ModelKind.Asr,
            registry.ByKind(ModelKind.Asr), installRoot, currentAsrName, promoteAsr);
        CleanupCard = new ModelCardViewModel(ModelKind.Cleanup,
            registry.ByKind(ModelKind.Cleanup), installRoot, currentCleanupName, promoteCleanup);
    }

    public ModelCardViewModel AsrCard { get; }
    public ModelCardViewModel CleanupCard { get; }

    public async Task DownloadMissingAsync(CancellationToken ct)
    {
        var resolver = new MissingModelsResolver();
        var selectedNames = new[] { AsrCard.SelectedName, CleanupCard.SelectedName };
        var missing = resolver.FindMissing(_registry.All, _installRoot, selectedNames);

        foreach (var d in missing)
        {
            var card = d.Kind == ModelKind.Asr ? AsrCard : CleanupCard;
            var progress = new Progress<DownloadProgress>(p => card.ReportProgress(p));
            await _downloader.DownloadAsync(d, _installRoot, progress, ct).ConfigureAwait(false);
        }

        // Refresh installed flags: IsSelectedInstalled is a derived property
        // computed from disk; raise PropertyChanged so any UI binding re-reads it.
        AsrCard.RaiseIsSelectedInstalledChanged();
        CleanupCard.RaiseIsSelectedInstalledChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- [ ] **Step 5: Verify pass**

```bash
cd /home/jesse/git/winpepper && dotnet test --filter "FullyQualifiedName~ModelsTabViewModelTests"
```

Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Models/ViewModels tests/Winpepper.Models.Tests/ViewModels
git commit -m "feat(models): ModelsTabViewModel + ModelCardViewModel"
```

---

## Task 22: Wire `Winpepper.App` to consume `Winpepper.History` + `Winpepper.Models`

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Winpepper.App.csproj`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Services/HistoryServices.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Services/ModelsServices.cs`

**Cross-plan coordination note:** Plan 3 creates `Winpepper.App` as a WinUI 3 packaged project with placeholder pages for History, History detail, and Models. Plan 4 is responsible for filling those pages in. The exact paths for placeholder XAML files (`Views/HistoryPage.xaml`, `Views/HistoryDetailPage.xaml`, `Views/ModelsPage.xaml`) and Plan 3's `App.xaml.cs` DI bootstrap MUST match Plan 3's actual layout — re-read Plan 3 before starting this task and adjust the file paths if they differ.

- [ ] **Step 1: Add project references in `src/Winpepper.App/Winpepper.App.csproj`**

Append inside the existing `<ItemGroup>` that holds project refs:

```xml
<ProjectReference Include="..\Winpepper.History\Winpepper.History.csproj" />
<ProjectReference Include="..\Winpepper.Models\Winpepper.Models.csproj" />
```

- [ ] **Step 2: Implement `src/Winpepper.App/Services/HistoryServices.cs`**

```csharp
using Winpepper.History;
using Winpepper.History.Lab;

namespace Winpepper.App.Services;

/// <summary>
/// Singleton bag of history-related services. The XAML pages resolve these
/// from the app's DI container (set up in Plan 3's <c>App.xaml.cs</c>).
/// </summary>
public sealed class HistoryServices
{
    public HistoryServices(string historyRoot)
    {
        Store = new HistoryStore(historyRoot);
        Archiver = new HistoryArchiver(Store);
        TranscriptionRerun = new ParakeetTranscriptionRerunService();
        CleanupRerun = new LlamaCleanupRerunService();
        HistoryRoot = historyRoot;
    }

    public string HistoryRoot { get; }
    public HistoryStore Store { get; }
    public HistoryArchiver Archiver { get; }
    public ITranscriptionRerunService TranscriptionRerun { get; }
    public ICleanupRerunService CleanupRerun { get; }
}
```

- [ ] **Step 3: Implement `src/Winpepper.App/Services/ModelsServices.cs`**

```csharp
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Services;

public sealed class ModelsServices : ModelsTabViewModel.IDownloader, IDisposable
{
    public ModelsServices(string modelsRoot)
    {
        ModelsRoot = modelsRoot;
        Registry = new ModelRegistry();
        _http = new HttpClientRangeClient();
        _downloader = new ModelDownloader(_http);
    }

    public string ModelsRoot { get; }
    public ModelRegistry Registry { get; }

    private readonly HttpClientRangeClient _http;
    private readonly ModelDownloader _downloader;

    public Task DownloadAsync(ModelDescriptor descriptor, string installRoot,
                              IProgress<DownloadProgress> progress, CancellationToken ct)
        => _downloader.DownloadAsync(descriptor, installRoot, progress, ct);

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: Build on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App
git commit -m "feat(app): wire History + Models services into Winpepper.App"
```

---

## Task 23: History page XAML (Windows-only)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/HistoryPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/HistoryPage.xaml.cs`

Plan 3 Task 20 (lines 3322–3336) ships `MainWindow.xaml` with `Tag="history"` as a `NavigationViewItem` set to `IsEnabled="False"` and unwired in the `OnNavSelectionChanged` switch. Plan 3 ships NO placeholder `HistoryPage.xaml` — that file is born here. Task 23 must therefore (a) create the new `HistoryPage` XAML + code-behind, and (b) enable + route the History/Lab/Models nav items in `MainWindow`. The Lab and Models pages land in Tasks 24 and 25, so the switch case for "lab" and "models" is added now (forward-referencing types that build clean once Tasks 24 and 25 land — Plan 3's `MainWindow.xaml.cs` already lives under `Winpepper.App.Views`, the same namespace as the new pages).

- [ ] **Step 1: Create `src/Winpepper.App/Views/HistoryPage.xaml`**

```xml
<Page x:Class="Winpepper.App.Views.HistoryPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:vm="using:Winpepper.History.ViewModels">
    <Grid Padding="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="History" Style="{StaticResource TitleTextBlockStyle}" Margin="0,0,0,12" />

        <ListView Grid.Row="1"
                  ItemsSource="{x:Bind ViewModel.Rows, Mode=OneWay}"
                  SelectionMode="Single"
                  ItemClick="OnRowClick"
                  IsItemClickEnabled="True">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="vm:HistoryRowViewModel">
                    <Grid ColumnSpacing="12" Padding="8">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="160" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="80" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <TextBlock Grid.Column="0" Text="{x:Bind TimestampDisplay}" />
                        <TextBlock Grid.Column="1" Text="{x:Bind TranscriptPreviewDisplay}" TextTrimming="CharacterEllipsis" />
                        <TextBlock Grid.Column="2" Text="{x:Bind DurationDisplay}" />
                        <Button Grid.Column="3" Content="Copy" Click="OnCopy" Tag="{x:Bind}" />
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </Grid>
</Page>
```

- [ ] **Step 2: Create `src/Winpepper.App/Views/HistoryPage.xaml.cs`**

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Winpepper.App.Services;
using Winpepper.History.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class HistoryPage : Page
{
    public HistoryListViewModel ViewModel { get; private set; } = null!;

    public HistoryPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var services = (HistoryServices)App.Current.Services.GetService(typeof(HistoryServices))!;
        ViewModel = new HistoryListViewModel(services.Store);
        ViewModel.Refresh();
    }

    private void OnRowClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryRowViewModel row)
        {
            Frame.Navigate(typeof(HistoryDetailPage), row.Entry);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HistoryRowViewModel row })
        {
            var pkg = new DataPackage();
            pkg.SetText(row.Entry.CleanedText);
            Clipboard.SetContent(pkg);
        }
    }
}
```

- [ ] **Step 3: Enable + route the History/Lab/Models nav items in `MainWindow`**

Plan 3 Task 20 shipped these three `NavigationViewItem`s with `IsEnabled="False"` and `ToolTipService.ToolTip="Available in Plan 4"`; the `OnNavSelectionChanged` switch in `MainWindow.xaml.cs` does not route them. We patch both now so all three pages become reachable. The "lab" and "models" case-arms forward-reference `HistoryDetailPage` and `ModelsPage`, which land in Tasks 24 and 25 respectively — the project won't build clean until those tasks complete (same pattern Plan 3 Task 20 used for `RecordingPage` / `CleanupPage` / `CorrectionsPage`).

In `src/Winpepper.App/Views/MainWindow.xaml`, remove `IsEnabled="False"` and the Plan-4 placeholder tooltip from the three relevant items so the section reads:

```xml
                <NavigationViewItem Tag="history"     Content="History" />
                <NavigationViewItem Tag="lab"         Content="Lab" />
                <NavigationViewItem Tag="models"      Content="Models" />
                <NavigationViewItem Tag="diagnostics" Content="Diagnostics" IsEnabled="False" ToolTipService.ToolTip="Available in Plan 5" />
```

(Leave Diagnostics disabled — that's Plan 5.)

In `src/Winpepper.App/Views/MainWindow.xaml.cs`, expand the `OnNavSelectionChanged` switch to:

```csharp
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var pageType = (string?)item.Tag switch
        {
            "recording"   => typeof(RecordingPage),
            "cleanup"     => typeof(CleanupPage),
            "corrections" => typeof(CorrectionsPage),
            "history"     => typeof(HistoryPage),
            "lab"         => typeof(HistoryDetailPage),
            "models"      => typeof(ModelsPage),
            _ => null,
        };
        if (pageType is not null)
            ContentFrame.Navigate(pageType, _shell);
    }
```

Note: clicking "Lab" from the nav rail navigates to `HistoryDetailPage` with no parameter. Without an `e.Parameter`, `HistoryDetailPage.OnNavigatedTo` (Task 24) would throw on the `(HistoryEntry)e.Parameter` cast. Guard the cast in Task 24 (see Task 24 Step 2 below) so a direct nav lands on an empty Lab.

- [ ] **Step 4: Build on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: build fails on missing `HistoryDetailPage` and `ModelsPage` types. Those land in Tasks 24 and 25; rerun the build after each.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Views/HistoryPage.xaml \
        src/Winpepper.App/Views/HistoryPage.xaml.cs \
        src/Winpepper.App/Views/MainWindow.xaml \
        src/Winpepper.App/Views/MainWindow.xaml.cs
git commit -m "feat(app): History page + enable nav routing for history/lab/models"
```

---

## Task 24: History detail / Lab page XAML (Windows-only)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/HistoryDetailPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/DiffSegmentsControl.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/DiffSegmentsControl.xaml.cs`

Plan 3 does NOT ship a placeholder `HistoryDetailPage`; Task 23 wires "lab" to `HistoryDetailPage`, so the type is born here. The Lab page has six panels: original-transcript, original-cleaned, transcription-rerun, cleanup-rerun, cleanup-transcript modal, and WAV playback. The diff rendering uses a small reusable `DiffSegmentsControl` UserControl that takes an `IReadOnlyList<WordDiffSegment>` and renders coloured runs.

- [ ] **Step 1: Create `src/Winpepper.App/Views/HistoryDetailPage.xaml`**

```xml
<Page x:Class="Winpepper.App.Views.HistoryDetailPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:local="using:Winpepper.App.Views"
      xmlns:models="using:Winpepper.Models">
    <ScrollViewer Padding="24">
        <StackPanel Spacing="16">

            <TextBlock Text="History detail" Style="{StaticResource TitleTextBlockStyle}" />

            <!-- Original side-by-side -->
            <Grid ColumnSpacing="16">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*" />
                    <ColumnDefinition Width="*" />
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column="0">
                    <TextBlock Text="Original transcript" Style="{StaticResource SubtitleTextBlockStyle}" />
                    <TextBlock x:Name="OriginalTranscriptText" TextWrapping="Wrap" IsTextSelectionEnabled="True" />
                </StackPanel>
                <StackPanel Grid.Column="1">
                    <TextBlock Text="Original cleaned text" Style="{StaticResource SubtitleTextBlockStyle}" />
                    <TextBlock x:Name="OriginalCleanedText" TextWrapping="Wrap" IsTextSelectionEnabled="True" />
                </StackPanel>
            </Grid>

            <!-- WAV playback -->
            <MediaPlayerElement x:Name="WavPlayer" AreTransportControlsEnabled="True" Height="60" />

            <!-- Rerun transcription panel -->
            <StackPanel Spacing="8" Padding="12" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="6">
                <TextBlock Text="Rerun transcription" Style="{StaticResource SubtitleTextBlockStyle}" />
                <ComboBox x:Name="AsrModelPicker"
                          ItemsSource="{x:Bind AvailableAsrModels, Mode=OneWay}"
                          DisplayMemberPath="DisplayName"
                          SelectionChanged="OnAsrSelectionChanged" />
                <Button Content="Run" Click="OnRunTranscriptionRerun" />
                <local:DiffSegmentsControl x:Name="TranscriptionDiff" />
                <Button Content="Use as default ASR" Click="OnPromoteAsr" />
            </StackPanel>

            <!-- Rerun cleanup panel -->
            <StackPanel Spacing="8" Padding="12" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="6">
                <TextBlock Text="Rerun cleanup" Style="{StaticResource SubtitleTextBlockStyle}" />
                <ComboBox x:Name="CleanupModelPicker"
                          ItemsSource="{x:Bind AvailableCleanupModels, Mode=OneWay}"
                          DisplayMemberPath="DisplayName"
                          SelectionChanged="OnCleanupSelectionChanged" />
                <TextBox x:Name="CustomPromptBox" PlaceholderText="Optional custom base prompt..."
                         AcceptsReturn="True" TextWrapping="Wrap" Height="120" />
                <ToggleSwitch x:Name="WindowContextToggle" Header="Include window context" />
                <Button Content="Run" Click="OnRunCleanupRerun" />
                <local:DiffSegmentsControl x:Name="CleanupDiff" />
                <Button Content="Show cleanup transcript" Click="OnShowCleanupTranscript" />
                <Button Content="Use as default cleanup" Click="OnPromoteCleanup" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 2: Create `src/Winpepper.App/Views/HistoryDetailPage.xaml.cs`**

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Storage;
using Winpepper.App.Services;
using Winpepper.Core.Settings;
using Winpepper.History;
using Winpepper.History.ViewModels;
using Winpepper.Models;

namespace Winpepper.App.Views;

public sealed partial class HistoryDetailPage : Page
{
    public HistoryDetailViewModel? ViewModel { get; private set; }
    public IReadOnlyList<ModelDescriptor> AvailableAsrModels { get; private set; } = Array.Empty<ModelDescriptor>();
    public IReadOnlyList<ModelDescriptor> AvailableCleanupModels { get; private set; } = Array.Empty<ModelDescriptor>();

    public HistoryDetailPage()
    {
        this.InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        // Task 23 routes the "Lab" nav-rail click to this page with no
        // parameter; only a row click from HistoryPage hands us an entry.
        // Bail out cleanly when the parameter is missing so the empty Lab
        // shows instead of throwing.
        if (e.Parameter is not HistoryEntry entry) return;

        var history = (HistoryServices)App.Current.Services.GetService(typeof(HistoryServices))!;
        var models = (ModelsServices)App.Current.Services.GetService(typeof(ModelsServices))!;
        var settings = (SettingsStore)App.Current.Services.GetService(typeof(SettingsStore))!;

        AvailableAsrModels = models.Registry.ByKind(ModelKind.Asr).ToList();
        AvailableCleanupModels = models.Registry.ByKind(ModelKind.Cleanup).ToList();

        ViewModel = new HistoryDetailViewModel(
            entry, history.HistoryRoot,
            history.TranscriptionRerun, history.CleanupRerun,
            promoteAsrDefault: name =>
            {
                var s = settings.Load();
                settings.Save(s with { AsrModelName = name });
            },
            promoteCleanupDefault: name =>
            {
                var s = settings.Load();
                settings.Save(s with { CleanupModelName = name });
            });

        OriginalTranscriptText.Text = ViewModel.OriginalTranscript;
        OriginalCleanedText.Text = ViewModel.OriginalCleanedText;

        // Hook the WAV player. MediaSource.CreateFromUri with a bare Windows
        // path (e.g. "C:\\...") raises a UriFormatException because the path
        // isn't a valid absolute URI. Open the file via StorageFile instead;
        // this is the supported WinUI 3 pattern for absolute local paths.
        try
        {
            var wavPath = ViewModel.WavAbsolutePath;
            var file = await StorageFile.GetFileFromPathAsync(wavPath);
            WavPlayer.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception)
        {
            // Missing WAV (entry pruned, manual delete, etc.) — leave the
            // player empty so the rest of the Lab still renders.
            WavPlayer.Source = null;
        }

        // Bind diffs.
        ViewModel.TranscriptionPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                TranscriptionDiff.Segments = ViewModel.TranscriptionPanel.Diff;
        };
        ViewModel.CleanupPanel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "Diff")
                CleanupDiff.Segments = ViewModel.CleanupPanel.Diff;
        };
    }

    private void OnAsrSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (AsrModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.TranscriptionPanel.SelectedModelName = d.Name;
            var models = (ModelsServices)App.Current.Services.GetService(typeof(ModelsServices))!;
            ViewModel.TranscriptionPanel.SelectedModelDirectory =
                Path.Combine(models.ModelsRoot, d.InstallDirRelative);
        }
    }

    private void OnCleanupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is null) return;
        if (CleanupModelPicker.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupPanel.SelectedModelName = d.Name;
            var models = (ModelsServices)App.Current.Services.GetService(typeof(ModelsServices))!;
            // Cleanup descriptors have exactly one GGUF file (see Plan 4
            // Task 10's catalog); resolve it once and hand the file path to
            // LlamaCleanupBackend through the rerun service.
            var dir = Path.Combine(models.ModelsRoot, d.InstallDirRelative);
            var file = d.Files.FirstOrDefault();
            ViewModel.CleanupPanel.SelectedModelDirectory = dir;
            ViewModel.CleanupPanel.SelectedModelPath =
                file is null ? "" : Path.Combine(dir, file.RelativePath);
        }
    }

    private async void OnRunTranscriptionRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.TranscriptionPanel.RunAsync(CancellationToken.None);
    }

    private async void OnRunCleanupRerun(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.CleanupCustomPrompt = CustomPromptBox.Text;
        ViewModel.IncludeWindowContextInRerun = WindowContextToggle.IsOn;
        await ViewModel.CleanupPanel.RunAsync(CancellationToken.None);
    }

    private async void OnShowCleanupTranscript(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var dlg = new ContentDialog
        {
            XamlRoot = this.XamlRoot,
            Title = "Cleanup transcript",
            CloseButtonText = "Close",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = $"=== Assembled prompt ===\n{ViewModel.CleanupAssembledPrompt}\n\n=== Raw model output ===\n{ViewModel.CleanupRawOutput}",
                    IsTextSelectionEnabled = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                },
                Height = 480,
            },
        };
        await dlg.ShowAsync();
    }

    private void OnPromoteAsr(object sender, RoutedEventArgs e) => ViewModel?.PromoteTranscriptionRerunAsDefault();
    private void OnPromoteCleanup(object sender, RoutedEventArgs e) => ViewModel?.PromoteCleanupRerunAsDefault();
}
```

- [ ] **Step 3: Implement `src/Winpepper.App/Views/DiffSegmentsControl.xaml`**

```xml
<UserControl x:Class="Winpepper.App.Views.DiffSegmentsControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <RichTextBlock x:Name="DiffText" IsTextSelectionEnabled="True" TextWrapping="Wrap" />
</UserControl>
```

- [ ] **Step 4: Implement `src/Winpepper.App/Views/DiffSegmentsControl.xaml.cs`**

```csharp
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Winpepper.History.Diff;

namespace Winpepper.App.Views;

public sealed partial class DiffSegmentsControl : UserControl
{
    private IReadOnlyList<WordDiffSegment> _segments = Array.Empty<WordDiffSegment>();

    public DiffSegmentsControl()
    {
        this.InitializeComponent();
    }

    public IReadOnlyList<WordDiffSegment> Segments
    {
        get => _segments;
        set { _segments = value; Render(); }
    }

    private void Render()
    {
        DiffText.Blocks.Clear();
        var para = new Paragraph();
        foreach (var seg in _segments)
        {
            var run = new Run { Text = seg.Text };
            switch (seg.Kind)
            {
                case WordDiffKind.Insert:
                    run.Foreground = new SolidColorBrush(Colors.Green);
                    break;
                case WordDiffKind.Delete:
                    run.Foreground = new SolidColorBrush(Colors.Red);
                    run.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                    break;
                case WordDiffKind.Equal:
                default:
                    break;
            }
            para.Inlines.Add(run);
        }
        DiffText.Blocks.Add(para);
    }
}
```

- [ ] **Step 5: Build on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Views/HistoryDetailPage.xaml src/Winpepper.App/Views/HistoryDetailPage.xaml.cs src/Winpepper.App/Views/DiffSegmentsControl.xaml src/Winpepper.App/Views/DiffSegmentsControl.xaml.cs
git commit -m "feat(app): History detail / Lab page with diff control"
```

---

## Task 25: Models tab XAML (Windows-only)

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/ModelsPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/ModelsPage.xaml.cs`

Plan 3 ships no placeholder `ModelsPage`; Task 23 wires "models" to this type, so the file is born here.

- [ ] **Step 1: Create `src/Winpepper.App/Views/ModelsPage.xaml`**

```xml
<Page x:Class="Winpepper.App.Views.ModelsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:models="using:Winpepper.Models">
    <Grid Padding="24" RowSpacing="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Models" Style="{StaticResource TitleTextBlockStyle}" />

        <Grid Grid.Row="1" ColumnSpacing="16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <!-- ASR card -->
            <StackPanel Grid.Column="0" Padding="12" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="6" Spacing="8">
                <TextBlock Text="ASR" Style="{StaticResource SubtitleTextBlockStyle}" />
                <ComboBox x:Name="AsrCombo"
                          ItemsSource="{x:Bind ViewModel.AsrCard.Available, Mode=OneWay}"
                          DisplayMemberPath="DisplayName"
                          SelectionChanged="OnAsrChanged" />
                <TextBlock Text="Installed:" />
                <TextBlock x:Name="AsrInstalledText" />
                <ListView ItemsSource="{x:Bind ViewModel.AsrCard.ProgressByFile, Mode=OneWay}">
                    <ListView.ItemTemplate>
                        <DataTemplate x:DataType="models:DownloadProgress">
                            <Grid ColumnSpacing="8">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="80" />
                                    <ColumnDefinition Width="100" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{x:Bind FileRelativePath}" TextTrimming="CharacterEllipsis" />
                                <TextBlock Grid.Column="1" Text="{x:Bind PercentComplete}" />
                                <TextBlock Grid.Column="2" Text="{x:Bind Phase}" />
                            </Grid>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
            </StackPanel>

            <!-- Cleanup card -->
            <StackPanel Grid.Column="1" Padding="12" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}" BorderThickness="1" CornerRadius="6" Spacing="8">
                <TextBlock Text="Cleanup" Style="{StaticResource SubtitleTextBlockStyle}" />
                <ComboBox x:Name="CleanupCombo"
                          ItemsSource="{x:Bind ViewModel.CleanupCard.Available, Mode=OneWay}"
                          DisplayMemberPath="DisplayName"
                          SelectionChanged="OnCleanupChanged" />
                <TextBlock Text="Installed:" />
                <TextBlock x:Name="CleanupInstalledText" />
                <ListView ItemsSource="{x:Bind ViewModel.CleanupCard.ProgressByFile, Mode=OneWay}">
                    <ListView.ItemTemplate>
                        <DataTemplate x:DataType="models:DownloadProgress">
                            <Grid ColumnSpacing="8">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*" />
                                    <ColumnDefinition Width="80" />
                                    <ColumnDefinition Width="100" />
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{x:Bind FileRelativePath}" TextTrimming="CharacterEllipsis" />
                                <TextBlock Grid.Column="1" Text="{x:Bind PercentComplete}" />
                                <TextBlock Grid.Column="2" Text="{x:Bind Phase}" />
                            </Grid>
                        </DataTemplate>
                    </ListView.ItemTemplate>
                </ListView>
            </StackPanel>
        </Grid>

        <Button Grid.Row="2" Content="Download Missing Models" Click="OnDownloadMissing" HorizontalAlignment="Right" />
    </Grid>
</Page>
```

- [ ] **Step 2: Create `src/Winpepper.App/Views/ModelsPage.xaml.cs`**

```csharp
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Services;
using Winpepper.Core.Settings;
using Winpepper.Models;
using Winpepper.Models.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class ModelsPage : Page
{
    public ModelsTabViewModel ViewModel { get; private set; } = null!;

    public ModelsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var models = (ModelsServices)App.Current.Services.GetService(typeof(ModelsServices))!;
        var settings = (SettingsStore)App.Current.Services.GetService(typeof(SettingsStore))!;
        var s = settings.Load();

        ViewModel = new ModelsTabViewModel(
            models.Registry, models.ModelsRoot, models,
            currentAsrName: s.AsrModelName,
            currentCleanupName: s.CleanupModelName,
            promoteAsr: name =>
            {
                var cur = settings.Load();
                settings.Save(cur with { AsrModelName = name });
            },
            promoteCleanup: name =>
            {
                var cur = settings.Load();
                settings.Save(cur with { CleanupModelName = name });
            });

        // Initial selection in the combo boxes.
        AsrCombo.SelectedItem = ViewModel.AsrCard.SelectedDescriptor;
        CleanupCombo.SelectedItem = ViewModel.CleanupCard.SelectedDescriptor;
        UpdateInstalledLabels();
    }

    private void OnAsrChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AsrCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.AsrCard.SelectedName = d.Name;
            ViewModel.AsrCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    private void OnCleanupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupCombo.SelectedItem is ModelDescriptor d)
        {
            ViewModel.CleanupCard.SelectedName = d.Name;
            ViewModel.CleanupCard.CommitSelection();
            UpdateInstalledLabels();
        }
    }

    private async void OnDownloadMissing(object sender, RoutedEventArgs e)
    {
        await ViewModel.DownloadMissingAsync(CancellationToken.None);
        UpdateInstalledLabels();
    }

    private void UpdateInstalledLabels()
    {
        AsrInstalledText.Text = ViewModel.AsrCard.IsSelectedInstalled ? "yes" : "no";
        CleanupInstalledText.Text = ViewModel.CleanupCard.IsSelectedInstalled ? "yes" : "no";
    }
}
```

- [ ] **Step 3: Build on the VM**

```bash
cd /home/jesse/git/winpepper
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Views/ModelsPage.xaml src/Winpepper.App/Views/ModelsPage.xaml.cs
git commit -m "feat(app): Models tab with download progress + selection"
```

---

## Task 26: Manual smoke test on the VM

**Files:**
- Modify: `/home/jesse/git/winpepper/docs/manual-test.md`

- [ ] **Step 1: Append the Plan 4 smoke procedure**

Append to `docs/manual-test.md`:

```markdown
## Plan 4 smoke (Windows VM)

1. `./scripts/sync-to-vm.sh`
2. `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Release"`
3. Launch the packaged app on the VM (per Plan 3's onboarding instructions).
4. Models tab:
   - The ASR and Cleanup cards each show a "Download Missing Models" button.
   - Click it. Watch the progress list populate. Confirm WAV / GGUF files land under
     `%LOCALAPPDATA%\winpepper\models\` and the "Installed" label flips to "yes".
   - Verify SHA-256 by deleting one file and re-running — it should re-download.
   - Verify resume by killing the app mid-download, restarting, and clicking again — the file should resume from byte X (check log for `Range: bytes=X-`).
5. Trigger a dictation session (hold + release).
6. History tab:
   - Newest entry appears at the top with the correct timestamp and preview.
   - Click it → detail page opens.
   - Click "Run" on the transcription rerun → diff renders (likely all-equal if the same model).
   - Pick a different model, rerun, observe diff.
   - Click "Run" on the cleanup rerun → cleaned text appears.
   - Click "Show cleanup transcript" → modal shows assembled prompt + raw output.
   - Click "Use as default cleanup" → return to Models tab, confirm the cleanup combo
     reflects the new selection. Settings file `%LOCALAPPDATA%\winpepper\settings.json`
     should also show the new `cleanupModelName` value.
7. Generate >50 sessions (or seed the index manually) and confirm only 50 remain
   and the oldest WAV files are deleted from disk.
```

- [ ] **Step 2: Run through the smoke procedure**

Execute every numbered step on the VM. Capture before/after sizes of the model files and a screenshot (if WinUI display is available via the VM's RDP/VNC).

- [ ] **Step 3: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs: Plan 4 smoke procedure"
```

---

## Self-review checklist (for the writer)

After completing all tasks, verify:

- [ ] Every step has concrete code or commands — no "implement X" placeholders.
- [ ] The only `TODO` strings in committed code are the `TODO(verify-at-exec)` markers in `ModelRegistry.cs` (Task 10), and they are replaced by real SHA-256 values before merge by running `scripts/verify-model-hashes.ps1`.
- [ ] Types referenced across tasks match exactly:
    - `HistoryEntry`, `HistoryTimings`, `HistoryIndex` (Tasks 2, 3, 4, 6, 7).
    - `HistoryStore`, `HistoryArchiver`, `HistoryArchiveInput` (Tasks 4, 6, 7).
    - `WordDiff`, `WordDiffSegment`, `WordDiffKind` (Tasks 15, 20, 24).
    - `ModelKind`, `ModelFile`, `ModelDescriptor`, `ModelRegistry` (Tasks 9, 10, 13, 14, 21, 25).
    - `DownloadProgress`, `DownloadPhase`, `IHttpRangeClient`, `ModelDownloader`, `ModelDownloadException` (Tasks 11, 12, 13, 21, 22).
    - `ITranscriptionRerunService`, `TranscriptionRerunResult`, `ICleanupRerunService`, `CleanupRerunInput`, `CleanupRerunResult` (Tasks 17, 18, 20, 24).
    - `HistoryListViewModel`, `HistoryRowViewModel`, `HistoryDetailViewModel`, `RerunPanelViewModel` (Tasks 19, 20, 23, 24).
    - `ModelCardViewModel`, `ModelsTabViewModel` (Tasks 21, 25).
- [ ] File paths in step headers match the paths in `git add` commands.
- [ ] Each task ends with a green build and a green test run on Linux (where applicable) and on the VM (Windows-only tasks).
- [ ] Plan 2 / Plan 3 cross-coordination notes are present where required:
    - Task 7 amends Plan 1's `Pipeline` and notes that Plan 3 must re-apply the archiver call in the WinUI 3 pipeline.
    - Task 16 notes that `AppSettings` field additions must be coordinated with Plan 2's cleanup-related fields.
    - Task 18 documents the public surface of `CleanupRunner` / `PromptBuilder` it depends on, so Plan 2 implementers know not to rename them.
    - Task 22 notes that the placeholder XAML files in `Winpepper.App` come from Plan 3 and that their paths must match.

If you find issues, fix them inline.

## What Plan 4 does NOT cover (intentionally — see follow-on plans)

- Post-paste learning (Plan 5).
- Diagnostics tab (Plan 5).
- WiX MSI, autostart, code signing (Plan 6).
- ARM64 / Microsoft Store identity (out of v1 scope).

## Handoff

When all tasks are committed, the smoke test passes, and `scripts/verify-model-hashes.ps1` has been used to replace every `TODO(verify-at-exec)` placeholder in `ModelRegistry.cs`: tell the user the History/Lab/Models surface is alive, then start Plan 5 (post-paste learning + diagnostics).










