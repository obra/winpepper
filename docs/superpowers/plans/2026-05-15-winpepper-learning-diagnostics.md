# Winpepper Plan 5 — Post-Paste Learning, Diagnostics, Error Bus Polish

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the v1 feature surface. Add `PostPasteWatcher` so an edit the user makes immediately after dictation turns into a learned misheard-replacement / preferred transcription. Land the Diagnostics tab (live log tail, "Open log folder", "Copy diagnostics bundle" — never audio). Introduce `Winpepper.Core.ErrorBus` so every pipeline stage funnels failures through one channel, the tray Error icon carries the most recent error summary, the clipboard-fallback path emits a toast, and per-stage failures can deep-link to the relevant settings tab. Wire `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` into a minidump writer that drops `MiniDumpWriteDump` output under `%LOCALAPPDATA%\winpepper\crashes\` and attempts a state-machine reset to Idle before exiting.

**Architecture:** `PostPasteWatcher` lives in `Winpepper.Core` and depends on an `IFocusedElementTextWatcher` abstraction. The Windows-only implementation in `Winpepper.Platform.Learning` subscribes UIA's `TextEdit_TextChangedEvent` (falls back to `Text_TextChangedEvent`) on the focused element captured at injection time, polling the element's text for divergence over a 30 s window. Diff acceptance reuses the constraint set from pepper-x: token-level Levenshtein on the diff window, minimum word length 3, edit distance ≤ 60 % of word length, no whitespace/punctuation drift, no common autocomplete patterns. `ErrorBus` is a pure-C# in-process pub/sub with one-shot observers (the tray) and persistent subscribers (the Diagnostics page). The log tail is a Serilog `ILogEventSink` writing into a fixed-capacity `LogRingBuffer` (2000 lines) that the Diagnostics view-model reads. The diagnostics-bundle assembler is pure-C# `System.IO.Compression` over the logs directory, a JSON sysinfo file, settings (no secrets in v1), and recent history index — explicitly skipping any `*.wav`. The crash handler P/Invokes `dbghelp.dll!MiniDumpWriteDump` and writes a timestamped `.dmp` per crash, then asks `SessionEngine` to reset.

**Tech Stack:** C# / .NET 9, `UIAutomationClient` (already referenced from Plan 2 in `Winpepper.Platform`), Serilog (custom sink), `System.IO.Compression`, `System.Diagnostics.Process`, P/Invoke against `dbghelp.dll`. No new NuGet packages.

**Spec:** [docs/superpowers/specs/2026-05-15-winpepper-design.md](../specs/2026-05-15-winpepper-design.md) — §7.3 Diagnostics tab, §8.2 Post-paste learning, §9.1 Error bus, §9.3 Crash safety, §9.5 Diagnostics bundle.

**Prerequisites:**

- **Plan 1** (`plan-1/foundation`) — `Winpepper.Core` (`AtomicFile`, `SessionEngine`, `SessionState`, `SessionEvent`, `IUiThread`, `WinpepperLogging`), `Winpepper.Audio.WasapiRecorder`, `Winpepper.Asr.ParakeetSession`, `Winpepper.Platform.Injection.TextInjector`, VM scripts.
- **Plan 2** — `Winpepper.Corrections.CorrectionStore` (specifically `AddPreferred(string)` and `AddReplacement(string wrong, string right)`), `Winpepper.Platform.WindowContext.UiaTreeReader` (we reuse the same UIA dependency surface).
- **Plan 3** — `Winpepper.App` (WinUI 3 packaged), `MainWindow.xaml` (the Diagnostics `NavigationViewItem` ships there with `IsEnabled="False"` and `ToolTipService.ToolTip="Available in Plan 5"` — Plan 5 flips it on and routes it). `SessionViewModel`, `TrayIconHost`, `AppShell`, `AppPaths`, `PipelineHost`.
- **Plan 4** — `Winpepper.History.HistoryStore` (used by the diagnostics bundle for recent-session metadata, never WAVs), `HistoryArchiver` (Plan 4 already calls it from `PipelineHost`; Plan 5 hangs off the same lifecycle point for `PostPasteWatcher`).

**Known WinUI build carry-forward.** As of Plans 3 and 4, `dotnet build src/Winpepper.App` on the VM fails during the WinAppSDK 1.6/1.7 + .NET 9 XAML markup compiler step (`PlatformNotSupportedException: RuntimeEnvironment.GetRuntimeInterfaceAsObject` — diagnosed in the Plan 3 milestone commit). Plan 5 does **not** try to fix this. Tasks that touch `Winpepper.App` XAML or its code-behind (Tasks 14, 15, 16, 17, 18, 19, 21, 23) are written for the eventual fixed-toolchain world. When you reach one of those tasks, write the code exactly as specified, run the build, observe the same WinUI-toolchain failure, commit the code as-written, and move on. The view-model / pure-C# pieces (everything in `Winpepper.Core`, `Winpepper.Corrections`, `Winpepper.Platform` (non-XAML), and the Diagnostics view-model in `Winpepper.App` if it builds standalone) compile and test on Linux throughout.

**Repo root throughout:** `/home/jesse/git/winpepper/` (Linux). Windows VM build/test directory: `C:\winpepper\` (synced via `scripts/sync-to-vm.sh`).

---

## Conventions

**Test-driven for every task.** Write the failing test first. Run it and confirm it fails. Implement minimal code. Run it and confirm it passes. Commit.

**Commits.** One commit per task at minimum. Smaller commits within a task are fine. Always end a task with a green build and green tests on Linux *and* (where applicable, modulo the WinUI carry-forward) on the Windows VM.

**Building.** Cross-platform tasks build and test on Linux (`dotnet build`, `dotnet test`). Windows-only tasks run on the VM via `./scripts/winrun "..."`.

**Linux build env:** `export DOTNET_ROOT="$HOME/.dotnet"` if the SDK isn't on PATH.

**Skipping Windows tests on Linux.** Tests that touch Win32 / WinUI / UIA-on-real-element APIs are tagged `[Trait("Platform", "Windows")]`. Linux CI runs `dotnet test --filter "Platform!=Windows"`. The Windows VM runs the full suite.

**View-model discipline.** Diagnostics view-model never touches `DispatcherQueue` directly — it raises `PropertyChanged` and the page dispatches on receipt. The ring-buffer notifications go through `IUiThread.Post` (Plan 3 Task 4).

**Toasts.** The clipboard-fallback toast and the post-paste learning toast both go through a single `IToastService` introduced in Task 13. The WinUI implementation uses `Microsoft.Toolkit.Uwp.Notifications` via the WinAppSDK toast API (`AppNotificationManager`, already pulled in transitively by H.NotifyIcon). Tests use `FakeToastService`.

---

## Task 1: Scaffold `ErrorBus` in `Winpepper.Core`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Errors/ErrorRecord.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Errors/ErrorStage.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Errors/ErrorBus.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Errors/ErrorBusTests.cs`

The spec (§9.1) calls for a single channel that every pipeline stage funnels failures through. The tray, the Diagnostics page, and the toast service all subscribe. Implementation: a small in-process pub/sub with an in-memory ring of the last N records (so a late subscriber can render the most recent error).

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Errors/ErrorBusTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorBusTests
{
    [Fact]
    public void Report_Notifies_Active_Subscribers()
    {
        var bus = new ErrorBus(capacity: 10);
        var received = new List<ErrorRecord>();
        using var _ = bus.Subscribe(received.Add);

        bus.Report(ErrorStage.Asr, new InvalidOperationException("model load"), Guid.NewGuid());

        received.Count.ShouldBe(1);
        received[0].Stage.ShouldBe(ErrorStage.Asr);
        received[0].Message.ShouldBe("model load");
    }

    [Fact]
    public void Recent_Returns_Newest_First_Capped_At_Capacity()
    {
        var bus = new ErrorBus(capacity: 3);
        var sid = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            bus.Report(ErrorStage.Cleanup, new Exception($"e{i}"), sid);

        var recent = bus.Recent();
        recent.Count.ShouldBe(3);
        recent[0].Message.ShouldBe("e4");
        recent[2].Message.ShouldBe("e2");
    }

    [Fact]
    public void MostRecent_Returns_Null_When_Empty()
    {
        var bus = new ErrorBus(capacity: 10);
        bus.MostRecent().ShouldBeNull();
    }

    [Fact]
    public void Subscribe_Disposing_Stops_Notifications()
    {
        var bus = new ErrorBus(capacity: 10);
        var received = new List<ErrorRecord>();
        var sub = bus.Subscribe(received.Add);
        bus.Report(ErrorStage.Injection, new Exception("a"), Guid.NewGuid());
        sub.Dispose();
        bus.Report(ErrorStage.Injection, new Exception("b"), Guid.NewGuid());
        received.Count.ShouldBe(1);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ErrorBusTests"
```

Expected: build fails — `Winpepper.Core.Errors.ErrorBus`, `ErrorRecord`, `ErrorStage` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Errors/ErrorStage.cs`**

```csharp
namespace Winpepper.Core.Errors;

public enum ErrorStage
{
    Audio,
    Asr,
    Cleanup,
    OcrUia,
    Injection,
    Learning,
    History,
    Models,
    Settings,
    Hotkey,
    Crash,
    Unknown,
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Errors/ErrorRecord.cs`**

```csharp
namespace Winpepper.Core.Errors;

/// <summary>One pipeline failure. Created by <see cref="ErrorBus.Report"/>.</summary>
public sealed record ErrorRecord
{
    public required ErrorStage Stage { get; init; }
    public required string Message { get; init; }
    public required string ExceptionType { get; init; }
    public required string StackTrace { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required Guid SessionId { get; init; }
}
```

- [ ] **Step 5: Implement `src/Winpepper.Core/Errors/ErrorBus.cs`**

```csharp
namespace Winpepper.Core.Errors;

/// <summary>
/// In-process pub/sub for pipeline errors. Spec §9.1. Subscribers receive
/// every report; the in-memory ring keeps the most recent N records so late
/// subscribers (e.g., the Diagnostics page opening for the first time) can
/// hydrate their UI.
/// </summary>
public sealed class ErrorBus
{
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly LinkedList<ErrorRecord> _recent = new();
    private readonly List<Action<ErrorRecord>> _subscribers = new();

    public ErrorBus(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void Report(ErrorStage stage, Exception ex, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var record = new ErrorRecord
        {
            Stage = stage,
            Message = ex.Message,
            ExceptionType = ex.GetType().FullName ?? ex.GetType().Name,
            StackTrace = ex.StackTrace ?? "",
            TimestampUtc = DateTime.UtcNow,
            SessionId = sessionId,
        };

        Action<ErrorRecord>[] snapshot;
        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > _capacity) _recent.RemoveLast();
            snapshot = _subscribers.ToArray();
        }

        foreach (var s in snapshot)
        {
            try { s(record); }
            catch { /* subscribers must not propagate */ }
        }
    }

    public IReadOnlyList<ErrorRecord> Recent()
    {
        lock (_gate) return _recent.ToArray();
    }

    public ErrorRecord? MostRecent()
    {
        lock (_gate) return _recent.First?.Value;
    }

    public IDisposable Subscribe(Action<ErrorRecord> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    private void Unsubscribe(Action<ErrorRecord> handler)
    {
        lock (_gate) _subscribers.Remove(handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ErrorBus _bus;
        private readonly Action<ErrorRecord> _handler;
        public Subscription(ErrorBus bus, Action<ErrorRecord> handler) { _bus = bus; _handler = handler; }
        public void Dispose() => _bus.Unsubscribe(_handler);
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ErrorBusTests"
```

Expected: 4 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Errors tests/Winpepper.Core.Tests/Errors
git commit -m "feat(core): ErrorBus pub/sub with capped recent buffer"
```

---

## Task 2: `LogRingBuffer` ring-buffer for the Diagnostics live tail

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Logging/LogRingBuffer.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Logging/LogTailEntry.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Logging/LogRingBufferTests.cs`

Spec §7.3: "Live log tail (rolling last 2000 lines)". This is a thread-safe FIFO ring that the Serilog sink writes into and the Diagnostics view-model reads.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Logging/LogRingBufferTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class LogRingBufferTests
{
    [Fact]
    public void Append_Preserves_Insertion_Order_Below_Capacity()
    {
        var buf = new LogRingBuffer(capacity: 5);
        for (var i = 0; i < 3; i++)
            buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"line {i}"));

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(3);
        snap[0].Message.ShouldBe("line 0");
        snap[2].Message.ShouldBe("line 2");
    }

    [Fact]
    public void Append_Evicts_Oldest_When_Capacity_Exceeded()
    {
        var buf = new LogRingBuffer(capacity: 3);
        for (var i = 0; i < 6; i++)
            buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"line {i}"));

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(3);
        snap[0].Message.ShouldBe("line 3");
        snap[1].Message.ShouldBe("line 4");
        snap[2].Message.ShouldBe("line 5");
    }

    [Fact]
    public void Appended_Event_Fires_Per_Append()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var heard = 0;
        buf.Appended += _ => heard++;
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "x"));
        buf.Append(new LogTailEntry(DateTime.UtcNow, "WRN", "y"));
        heard.ShouldBe(2);
    }

    [Fact]
    public void Snapshot_Is_Defensive_Copy()
    {
        var buf = new LogRingBuffer(capacity: 5);
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "a"));
        var snap = buf.Snapshot();
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "b"));
        snap.Count.ShouldBe(1); // stale snapshot unchanged
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LogRingBufferTests"
```

Expected: build fails — `LogRingBuffer`, `LogTailEntry` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Logging/LogTailEntry.cs`**

```csharp
namespace Winpepper.Core.Logging;

/// <summary>One rendered log line for the Diagnostics live tail.</summary>
public sealed record LogTailEntry(DateTime TimestampUtc, string Level, string Message);
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Logging/LogRingBuffer.cs`**

```csharp
namespace Winpepper.Core.Logging;

/// <summary>
/// Bounded FIFO ring of rendered log lines. Spec §7.3: live tail of the last
/// 2000 lines. Thread-safe (the Serilog sink may write from any thread).
/// </summary>
public sealed class LogRingBuffer
{
    private readonly int _capacity;
    private readonly Queue<LogTailEntry> _q;
    private readonly object _gate = new();

    public event Action<LogTailEntry>? Appended;

    public LogRingBuffer(int capacity = 2000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _q = new Queue<LogTailEntry>(capacity);
    }

    public int Capacity => _capacity;

    public void Append(LogTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_q.Count >= _capacity) _q.Dequeue();
            _q.Enqueue(entry);
        }
        Appended?.Invoke(entry);
    }

    public IReadOnlyList<LogTailEntry> Snapshot()
    {
        lock (_gate) return _q.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _q.Clear();
    }
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LogRingBufferTests"
```

Expected: 4 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Logging/LogTailEntry.cs src/Winpepper.Core/Logging/LogRingBuffer.cs tests/Winpepper.Core.Tests/Logging
git commit -m "feat(core): LogRingBuffer for Diagnostics live tail"
```

---

## Task 3: Serilog sink that writes into `LogRingBuffer`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Logging/RingBufferSink.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Core/Logging/WinpepperLogging.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Logging/RingBufferSinkTests.cs`

Plan 1's `WinpepperLogging.Create(...)` returns an `ILoggerFactory` and wires Serilog's file and console sinks. We extend it with a third sink that appends into a shared `LogRingBuffer`. The buffer is returned alongside the factory so `AppShell` can hand it to the Diagnostics view-model.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Logging/RingBufferSinkTests.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Xunit;

namespace Winpepper.Core.Tests.Logging;

public class RingBufferSinkTests
{
    [Fact]
    public void Logger_Writes_Lines_Into_Buffer()
    {
        var buf = new LogRingBuffer(capacity: 10);
        using var factory = WinpepperLogging.CreateWithBuffer(
            Path.Combine(Path.GetTempPath(), $"wp-log-{Guid.NewGuid():N}"),
            debugConsole: false,
            minimumLevel: LogLevel.Information,
            buffer: buf);
        var log = factory.CreateLogger("test");

        log.LogInformation("hello {Who}", "world");

        var snap = buf.Snapshot();
        snap.Count.ShouldBeGreaterThan(0);
        snap[^1].Message.ShouldContain("hello world");
        snap[^1].Level.ShouldBe("INF");
    }

    [Fact]
    public void Below_Minimum_Level_Is_Filtered_Out()
    {
        var buf = new LogRingBuffer(capacity: 10);
        using var factory = WinpepperLogging.CreateWithBuffer(
            Path.Combine(Path.GetTempPath(), $"wp-log-{Guid.NewGuid():N}"),
            debugConsole: false,
            minimumLevel: LogLevel.Warning,
            buffer: buf);
        var log = factory.CreateLogger("test");

        log.LogInformation("ignored");
        log.LogWarning("kept");

        var snap = buf.Snapshot();
        snap.Count.ShouldBe(1);
        snap[0].Message.ShouldContain("kept");
        snap[0].Level.ShouldBe("WRN");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~RingBufferSinkTests"
```

Expected: build fails — `WinpepperLogging.CreateWithBuffer` doesn't exist.

- [ ] **Step 3: Implement `src/Winpepper.Core/Logging/RingBufferSink.cs`**

```csharp
using Serilog.Core;
using Serilog.Events;

namespace Winpepper.Core.Logging;

/// <summary>Serilog sink that forwards rendered events into a <see cref="LogRingBuffer"/>.</summary>
internal sealed class RingBufferSink : ILogEventSink
{
    private readonly LogRingBuffer _buffer;
    public RingBufferSink(LogRingBuffer buffer) { _buffer = buffer; }

    public void Emit(LogEvent logEvent)
    {
        var levelTag = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "INF",
        };
        var message = logEvent.RenderMessage();
        if (logEvent.Exception is not null)
            message = $"{message} | {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
        _buffer.Append(new LogTailEntry(logEvent.Timestamp.UtcDateTime, levelTag, message));
    }
}
```

- [ ] **Step 4: Extend `src/Winpepper.Core/Logging/WinpepperLogging.cs`**

Add a new factory method that includes the ring sink. Leave the existing `Create(...)` intact.

```csharp
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Winpepper.Core.Logging;

public static class WinpepperLogging
{
    public static ILoggerFactory Create(string logDirectory, bool debugConsole, LogLevel minimumLevel)
        => CreateInternal(logDirectory, debugConsole, minimumLevel, buffer: null);

    public static ILoggerFactory CreateWithBuffer(
        string logDirectory,
        bool debugConsole,
        LogLevel minimumLevel,
        LogRingBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return CreateInternal(logDirectory, debugConsole, minimumLevel, buffer);
    }

    private static ILoggerFactory CreateInternal(
        string logDirectory,
        bool debugConsole,
        LogLevel minimumLevel,
        LogRingBuffer? buffer)
    {
        Directory.CreateDirectory(logDirectory);

        var serilogLevel = minimumLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };

        var template = "{Timestamp:yyyy-MM-ddTHH:mm:ss.fff} [{Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";

        var config = new LoggerConfiguration()
            .MinimumLevel.Is(serilogLevel)
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(logDirectory, "winpepper-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: template,
                shared: false);

        if (debugConsole)
            config = config.WriteTo.Console(outputTemplate: template);

        if (buffer is not null)
            config = config.WriteTo.Sink(new RingBufferSink(buffer));

        Log.Logger = config.CreateLogger();
        return LoggerFactory.Create(b => b.AddSerilog(Log.Logger, dispose: false));
    }

    public static void Flush() => Log.CloseAndFlush();
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~RingBufferSinkTests"
```

Expected: 2 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.Core/Logging tests/Winpepper.Core.Tests/Logging/RingBufferSinkTests.cs
git commit -m "feat(logging): Serilog ring-buffer sink for Diagnostics tail"
```

---

## Task 4: Token-level Levenshtein + learning-diff acceptance

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/LevenshteinDistance.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/LearningCandidate.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/LearningDiffAnalyzer.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Learning/LevenshteinDistanceTests.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Learning/LearningDiffAnalyzerTests.cs`

Spec §8.2 (4): the analyzer must apply pepper-x's constraints — minimum word length 3, edit distance ≤ 60 % of the word's length, no whitespace-only diffs, no punctuation drift, no common autocomplete patterns. Anchored at a single word position in the diff window. The pure-C# analyzer lives in `Winpepper.Core.Learning` so it's testable on Linux.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Learning/LevenshteinDistanceTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class LevenshteinDistanceTests
{
    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "", 0)]
    [InlineData("a", "", 1)]
    [InlineData("", "a", 1)]
    [InlineData("chat gbt", "ChatGPT", 4)]
    [InlineData("equal", "equal", 0)]
    public void Compute_Matches_Reference_Values(string a, string b, int expected)
    {
        LevenshteinDistance.Compute(a, b).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LevenshteinDistanceTests"
```

Expected: build fails — `LevenshteinDistance` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Learning/LevenshteinDistance.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>Two-row dynamic-programming Levenshtein. O(|a|+|b|) memory.</summary>
public static class LevenshteinDistance
{
    public static int Compute(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
```

- [ ] **Step 4: Verify Levenshtein pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LevenshteinDistanceTests"
```

Expected: 6 tests pass.

- [ ] **Step 5: Write the failing test `tests/Winpepper.Core.Tests/Learning/LearningDiffAnalyzerTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class LearningDiffAnalyzerTests
{
    [Fact]
    public void Accepts_Single_Word_Replacement_Within_Distance_Cap()
    {
        var c = LearningDiffAnalyzer.Analyze(
            injected: "Send chat gbt the link",
            current:  "Send ChatGPT the link");

        c.ShouldNotBeNull();
        c!.Wrong.ShouldBe("chat gbt");
        c.Right.ShouldBe("ChatGPT");
    }

    [Fact]
    public void Rejects_Equal_Strings()
    {
        LearningDiffAnalyzer.Analyze("hello world", "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_When_Multiple_Word_Positions_Differ()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "the quick brown fox",
            current:  "a slow brown fox").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Word_Shorter_Than_Min_Length()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "say hi there",
            current:  "say bye there").ShouldBeNull(); // "hi" length 2
    }

    [Fact]
    public void Rejects_Edit_Distance_Above_Sixty_Percent_Of_Word_Length()
    {
        // "cat" (3) vs "dog" (3) — distance 3 = 100 % > 60 %.
        LearningDiffAnalyzer.Analyze("the cat sat", "the dog sat").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Punctuation_Drift_Only()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "hello, world.",
            current:  "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Whitespace_Only_Diff()
    {
        LearningDiffAnalyzer.Analyze(
            injected: "hello  world",
            current:  "hello world").ShouldBeNull();
    }

    [Fact]
    public void Rejects_Common_Autocomplete_Capitalization_Of_First_Letter()
    {
        // "anthropic" -> "Anthropic" looks like an autocomplete capitalizer.
        LearningDiffAnalyzer.Analyze(
            injected: "love anthropic stuff",
            current:  "love Anthropic stuff").ShouldBeNull();
    }

    [Fact]
    public void Rejects_When_Diff_Is_Appended_Text_Beyond_Injection()
    {
        // User typed more after — not a correction, just continuation.
        LearningDiffAnalyzer.Analyze(
            injected: "hello there",
            current:  "hello there friend").ShouldBeNull();
    }
}
```

- [ ] **Step 6: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LearningDiffAnalyzerTests"
```

Expected: build fails — `LearningDiffAnalyzer` and `LearningCandidate` not found.

- [ ] **Step 7: Implement `src/Winpepper.Core/Learning/LearningCandidate.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>One accepted misheard-replacement candidate from a post-paste diff.</summary>
public sealed record LearningCandidate(string Wrong, string Right);
```

- [ ] **Step 8: Implement `src/Winpepper.Core/Learning/LearningDiffAnalyzer.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Token-level diff between <c>injected</c> (what we typed) and <c>current</c>
/// (what's in the element now). Returns a <see cref="LearningCandidate"/> when
/// exactly one word position differs and all pepper-x constraints pass.
/// Spec §8.2 (4).
/// </summary>
public static class LearningDiffAnalyzer
{
    public const int MinWordLength = 3;
    public const double MaxEditDistanceRatio = 0.60;

    public static LearningCandidate? Analyze(string injected, string current)
    {
        ArgumentNullException.ThrowIfNull(injected);
        ArgumentNullException.ThrowIfNull(current);
        if (string.Equals(injected, current, StringComparison.Ordinal)) return null;

        var lhs = Tokenize(injected);
        var rhs = Tokenize(current);

        // Equal token counts only — appended/removed-text edits are not corrections.
        if (lhs.Count != rhs.Count) return null;

        // Identify the single differing position.
        var diffIndex = -1;
        for (var i = 0; i < lhs.Count; i++)
        {
            if (!string.Equals(lhs[i], rhs[i], StringComparison.Ordinal))
            {
                if (diffIndex >= 0) return null; // more than one differing token
                diffIndex = i;
            }
        }
        if (diffIndex < 0) return null;

        var wrong = lhs[diffIndex];
        var right = rhs[diffIndex];

        // No whitespace-only or punctuation-only drift.
        if (string.IsNullOrWhiteSpace(wrong) || string.IsNullOrWhiteSpace(right)) return null;
        if (StripWordChars(wrong).Length == 0 || StripWordChars(right).Length == 0) return null;

        // Min word length applies to both sides.
        if (wrong.Length < MinWordLength || right.Length < MinWordLength) return null;

        // Reject "first-letter capitalization only" — looks like autocomplete.
        if (IsFirstLetterCapitalizationOnly(wrong, right)) return null;

        // Edit distance bound: <= 60 % of the longer word's length.
        var maxLen = Math.Max(wrong.Length, right.Length);
        var dist = LevenshteinDistance.Compute(wrong, right);
        if (dist == 0) return null;
        if (dist > Math.Floor(maxLen * MaxEditDistanceRatio)) return null;

        return new LearningCandidate(wrong, right);
    }

    private static List<string> Tokenize(string s)
    {
        // Split on whitespace runs. Empty tokens are dropped.
        var parts = s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return new List<string>(parts);
    }

    private static string StripWordChars(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    private static bool IsFirstLetterCapitalizationOnly(string a, string b)
    {
        if (a.Length != b.Length) return false;
        if (a.Length == 0) return false;
        if (!char.IsLetter(a[0]) || !char.IsLetter(b[0])) return false;
        if (char.ToLowerInvariant(a[0]) != char.ToLowerInvariant(b[0])) return false;
        if (a[0] == b[0]) return false; // identical, not a capitalization change
        // Rest must be identical.
        for (var i = 1; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }
}
```

- [ ] **Step 9: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~LearningDiffAnalyzerTests"
```

Expected: 9 tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Core/Learning tests/Winpepper.Core.Tests/Learning
git commit -m "feat(learning): token-level Levenshtein diff with pepper-x constraints"
```

---

## Task 5: `IFocusedElementTextWatcher` abstraction + in-memory fake

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/IFocusedElementTextWatcher.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/FocusedElementTextChange.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/FakeFocusedElementTextWatcher.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Learning/FakeFocusedElementTextWatcherTests.cs`

The Windows-specific UIA subscription goes in `Winpepper.Platform.Learning` in Task 7. `Winpepper.Core.PostPasteWatcher` (Task 6) depends only on the abstraction so it's tested without UIA.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Learning/FakeFocusedElementTextWatcherTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class FakeFocusedElementTextWatcherTests
{
    [Fact]
    public async Task Emits_Changes_To_Subscriber_Until_Disposed()
    {
        var fake = new FakeFocusedElementTextWatcher();
        var received = new List<string>();
        using var sub = fake.Subscribe("element-id-1", c => { received.Add(c.NewText); return Task.CompletedTask; });

        await fake.EmitAsync("element-id-1", "step 1");
        await fake.EmitAsync("element-id-1", "step 2");

        received.ShouldBe(new[] { "step 1", "step 2" });
    }

    [Fact]
    public async Task Subscriptions_Are_Scoped_To_Element_Id()
    {
        var fake = new FakeFocusedElementTextWatcher();
        var received = new List<string>();
        using var sub = fake.Subscribe("target", c => { received.Add(c.NewText); return Task.CompletedTask; });

        await fake.EmitAsync("not-target", "noise");
        await fake.EmitAsync("target", "real");

        received.ShouldBe(new[] { "real" });
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~FakeFocusedElementTextWatcherTests"
```

Expected: build fails — types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/Learning/FocusedElementTextChange.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>One text-change notification from the focused UIA element.</summary>
public sealed record FocusedElementTextChange(string ElementId, string NewText, DateTime TimestampUtc);
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Learning/IFocusedElementTextWatcher.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Abstraction over UIA's <c>TextEdit_TextChangedEvent</c> /
/// <c>Text_TextChangedEvent</c> subscription. The Windows implementation lives
/// in <c>Winpepper.Platform.Learning</c>; <c>FakeFocusedElementTextWatcher</c>
/// drives unit tests for <c>PostPasteWatcher</c>.
/// </summary>
public interface IFocusedElementTextWatcher
{
    /// <summary>
    /// Subscribe to text changes for the supplied opaque element identifier.
    /// Implementations decide what the identifier means (UIA RuntimeId).
    /// Disposing the returned <see cref="IDisposable"/> tears down the subscription.
    /// </summary>
    IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange);
}
```

- [ ] **Step 5: Implement `src/Winpepper.Core/Learning/FakeFocusedElementTextWatcher.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Test double for <see cref="IFocusedElementTextWatcher"/>. Drives changes by
/// calling <see cref="EmitAsync"/>. Used by <c>PostPasteWatcherTests</c>.
/// </summary>
public sealed class FakeFocusedElementTextWatcher : IFocusedElementTextWatcher
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Func<FocusedElementTextChange, Task>>> _subs = new();

    public IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange)
    {
        ArgumentException.ThrowIfNullOrEmpty(elementId);
        ArgumentNullException.ThrowIfNull(onChange);
        lock (_gate)
        {
            if (!_subs.TryGetValue(elementId, out var list))
                _subs[elementId] = list = new List<Func<FocusedElementTextChange, Task>>();
            list.Add(onChange);
        }
        return new Sub(this, elementId, onChange);
    }

    public async Task EmitAsync(string elementId, string newText)
    {
        Func<FocusedElementTextChange, Task>[] snapshot;
        lock (_gate)
        {
            if (!_subs.TryGetValue(elementId, out var list)) return;
            snapshot = list.ToArray();
        }
        var change = new FocusedElementTextChange(elementId, newText, DateTime.UtcNow);
        foreach (var h in snapshot) await h(change);
    }

    private sealed class Sub : IDisposable
    {
        private readonly FakeFocusedElementTextWatcher _owner;
        private readonly string _id;
        private readonly Func<FocusedElementTextChange, Task> _h;
        public Sub(FakeFocusedElementTextWatcher owner, string id, Func<FocusedElementTextChange, Task> h)
        { _owner = owner; _id = id; _h = h; }
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._subs.TryGetValue(_id, out var list)) list.Remove(_h);
            }
        }
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~FakeFocusedElementTextWatcherTests"
```

Expected: 2 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Learning tests/Winpepper.Core.Tests/Learning/FakeFocusedElementTextWatcherTests.cs
git commit -m "feat(learning): IFocusedElementTextWatcher abstraction + fake"
```

---

## Task 6: `PostPasteWatcher` — the learning orchestrator

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/PostPasteContext.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/PostPasteDecision.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/IPostPasteToastPrompt.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/PostPasteWatcher.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Learning/PostPasteWatcherTests.cs`

Spec §8.2 (1)–(7). The watcher takes a context captured at injection time, subscribes via `IFocusedElementTextWatcher`, runs each change through `LearningDiffAnalyzer`, and — on the first accepted candidate — calls `IPostPasteToastPrompt.AskAsync(...)` to render the non-modal Yes/Preferred/No toast. The toast prompt is an abstraction so tests assert on the call without a real WinUI toast. Behaviour per spec:

- Watch window: 30 s (configurable via constructor parameter for tests).
- Toast timeout: 8 s. The toast layer enforces it; the watcher only consumes the decision.
- `Yes` → call `corrections.AddReplacement(wrong, right)`.
- `Preferred` → call `corrections.AddPreferred(right)`.
- `No` → record `(wrong, right)` in a session-scoped suppression set so the same pair doesn't re-prompt for the rest of this session.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Learning/PostPasteWatcherTests.cs`**

```csharp
using Shouldly;
using Winpepper.Corrections;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class PostPasteWatcherTests : IDisposable
{
    private readonly string _storePath;
    public PostPasteWatcherTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"corr-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_storePath)) File.Delete(_storePath); }

    private static PostPasteContext Ctx(string injected) => new()
    {
        ElementId = "el-1",
        InjectedText = injected,
        SessionId = Guid.NewGuid(),
        InjectionEndUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Yes_Decision_Writes_Misheard_Replacement()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, store, prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("Send chat gbt the link"));
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        prompt.Calls.Count.ShouldBe(1);
        prompt.Calls[0].Wrong.ShouldBe("chat gbt");
        prompt.Calls[0].Right.ShouldBe("ChatGPT");

        var data = store.Load();
        data.Replacements.ShouldContainKey("chat gbt");
        data.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }

    [Fact]
    public async Task Preferred_Decision_Writes_Preferred_List_Entry()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Preferred);
        using var ppw = new PostPasteWatcher(watcher, store, prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("Send chat gbt the link"));
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        var data = store.Load();
        data.Preferred.ShouldContain("ChatGPT");
        data.Replacements.ShouldNotContainKey("chat gbt");
    }

    [Fact]
    public async Task No_Decision_Suppresses_Same_Pair_For_Session()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.No);
        using var ppw = new PostPasteWatcher(watcher, store, prompt, TimeSpan.FromSeconds(30));

        await ppw.BeginAsync(Ctx("Send chat gbt the link")).ContinueWith(_ => { });
        // first change triggers prompt and stores suppression
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");

        // Start a second session in the same watcher lifetime — same pair must not prompt.
        var done2 = ppw.BeginAsync(Ctx("Send chat gbt please"));
        await watcher.EmitAsync("el-1", "Send ChatGPT please");
        await done2;

        prompt.Calls.Count.ShouldBe(1); // only the first call asked
        store.Load().Replacements.ShouldNotContainKey("chat gbt");
    }

    [Fact]
    public async Task Watch_Window_Elapses_Without_Change_Cleans_Up()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, store, prompt, TimeSpan.FromMilliseconds(50));

        await ppw.BeginAsync(Ctx("nothing changes"));
        await Task.Delay(150);

        prompt.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Change_That_Fails_Constraints_Does_Not_Prompt()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, store, prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("the quick brown fox"));
        // Two-position diff — analyzer rejects.
        await watcher.EmitAsync("el-1", "a slow brown fox");
        await Task.Delay(50);
        await done.ContinueWith(_ => { });

        prompt.Calls.Count.ShouldBe(0);
    }

    private sealed class FakeToastPrompt : IPostPasteToastPrompt
    {
        public List<LearningCandidate> Calls { get; } = new();
        private readonly PostPasteDecision _next;
        public FakeToastPrompt(PostPasteDecision next) { _next = next; }
        public Task<PostPasteDecision> AskAsync(LearningCandidate c, CancellationToken ct)
        {
            Calls.Add(c);
            return Task.FromResult(_next);
        }
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~PostPasteWatcherTests"
```

Expected: build fails — `PostPasteWatcher`, `PostPasteContext`, `PostPasteDecision`, `IPostPasteToastPrompt` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Learning/PostPasteContext.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Snapshot captured at injection completion. The watcher uses it to subscribe
/// to text changes on the focused element and diff what the user has done.
/// Spec §8.2 (1).
/// </summary>
public sealed record PostPasteContext
{
    public required string ElementId { get; init; }
    public required string InjectedText { get; init; }
    public required Guid SessionId { get; init; }
    public required DateTime InjectionEndUtc { get; init; }
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Learning/PostPasteDecision.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>User's response to the post-paste toast. Spec §8.2 (6).</summary>
public enum PostPasteDecision
{
    Yes,
    Preferred,
    No,
}
```

- [ ] **Step 5: Implement `src/Winpepper.Core/Learning/IPostPasteToastPrompt.cs`**

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Renders the non-modal "Learn correction: wrong → right? [Yes / Preferred / No]"
/// toast and resolves with the user's choice. Spec §8.2 (5). Implementations
/// enforce the 8 s timeout themselves and return <see cref="PostPasteDecision.No"/>
/// when it elapses.
/// </summary>
public interface IPostPasteToastPrompt
{
    Task<PostPasteDecision> AskAsync(LearningCandidate candidate, CancellationToken ct);
}
```

- [ ] **Step 6: Implement `src/Winpepper.Core/Learning/PostPasteWatcher.cs`**

```csharp
using Winpepper.Corrections;

namespace Winpepper.Core.Learning;

/// <summary>
/// Orchestrates the post-paste learning flow. Spec §8.2.
/// Lifecycle: <see cref="BeginAsync"/> is called once per dictation session,
/// right after injection completes. Internally:
///   1. Subscribes to the focused element via <see cref="IFocusedElementTextWatcher"/>.
///   2. Watches for up to <c>watchWindow</c> (30 s default).
///   3. Runs each change through <see cref="LearningDiffAnalyzer"/>.
///   4. On the first accepted candidate not in the session suppression set,
///      asks <see cref="IPostPasteToastPrompt"/> and applies the user's decision.
///   5. Unsubscribes (regardless of decision) and ignores any further changes
///      for this session.
/// </summary>
public sealed class PostPasteWatcher : IDisposable
{
    private readonly IFocusedElementTextWatcher _watcher;
    private readonly CorrectionStore _store;
    private readonly IPostPasteToastPrompt _prompt;
    private readonly TimeSpan _watchWindow;
    private readonly HashSet<(string Wrong, string Right)> _sessionSuppress = new();
    private readonly object _gate = new();
    private bool _disposed;

    public PostPasteWatcher(
        IFocusedElementTextWatcher watcher,
        CorrectionStore store,
        IPostPasteToastPrompt prompt,
        TimeSpan? watchWindow = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _watchWindow = watchWindow ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Begin a watch session. Returns a Task that completes when either a
    /// candidate has been resolved (Yes/Preferred/No) or the watch window
    /// elapses. Callers may fire-and-forget.
    /// </summary>
    public async Task BeginAsync(PostPasteContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_disposed) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(_watchWindow);
        cts.Token.Register(() => tcs.TrySetResult(false));

        var sub = _watcher.Subscribe(ctx.ElementId, async change =>
        {
            if (cts.IsCancellationRequested) return;
            var candidate = LearningDiffAnalyzer.Analyze(ctx.InjectedText, change.NewText);
            if (candidate is null) return;

            lock (_gate)
            {
                if (_sessionSuppress.Contains((candidate.Wrong, candidate.Right))) return;
            }

            PostPasteDecision decision;
            try { decision = await _prompt.AskAsync(candidate, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { decision = PostPasteDecision.No; }

            ApplyDecision(candidate, decision);
            tcs.TrySetResult(true);
        });

        try { await tcs.Task.ConfigureAwait(false); }
        finally { sub.Dispose(); }
    }

    private void ApplyDecision(LearningCandidate c, PostPasteDecision decision)
    {
        switch (decision)
        {
            case PostPasteDecision.Yes:
                _store.AddReplacement(c.Wrong, c.Right);
                break;
            case PostPasteDecision.Preferred:
                _store.AddPreferred(c.Right);
                break;
            case PostPasteDecision.No:
                lock (_gate) _sessionSuppress.Add((c.Wrong, c.Right));
                break;
        }
    }

    public void Dispose() { _disposed = true; }
}
```

- [ ] **Step 7: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~PostPasteWatcherTests"
```

Expected: 5 tests pass.

- [ ] **Step 8: Wire `Winpepper.Core.csproj` to reference `Winpepper.Corrections`** if not already

Plan 2 already added the package reference indirectly via `AppShell`, but `Winpepper.Core` itself must reference `Winpepper.Corrections` so `PostPasteWatcher` compiles. Edit `src/Winpepper.Core/Winpepper.Core.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>Winpepper.Core</RootNamespace>
    <AssemblyName>Winpepper.Core</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Logging" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />
    <PackageReference Include="Serilog" />
    <PackageReference Include="Serilog.Extensions.Logging" />
    <PackageReference Include="Serilog.Sinks.File" />
    <PackageReference Include="Serilog.Sinks.Console" />
    <ProjectReference Include="..\Winpepper.Corrections\Winpepper.Corrections.csproj" />
  </ItemGroup>
</Project>
```

Note: `Winpepper.Corrections` already references `Winpepper.Core` (for `AtomicFile`). Adding the reverse direction creates a project-reference cycle. **Resolve the cycle** by moving `AtomicFile` into a new tiny library `Winpepper.Core.Abstractions` — or, simpler, by inverting the dependency: leave `Winpepper.Corrections` referencing `Winpepper.Core`, and have `Winpepper.Core.Learning.PostPasteWatcher` depend on a thin abstraction over `CorrectionStore` instead of the concrete type.

**Take the abstraction route.** Replace the constructor parameter `CorrectionStore store` with the interface defined in the next sub-step.

Revise `PostPasteWatcher` and its test to depend on `ICorrectionWriter`:

In `src/Winpepper.Core/Learning/ICorrectionWriter.cs` (new file):

```csharp
namespace Winpepper.Core.Learning;

/// <summary>
/// Narrow write surface over <c>Winpepper.Corrections.CorrectionStore</c> used
/// by <see cref="PostPasteWatcher"/>. Keeps <c>Winpepper.Core</c> from having
/// to take a project reference on <c>Winpepper.Corrections</c>.
/// </summary>
public interface ICorrectionWriter
{
    bool AddReplacement(string wrong, string right);
    bool AddPreferred(string value);
}
```

Update `PostPasteWatcher` to take `ICorrectionWriter` instead of `CorrectionStore`:

```csharp
using Winpepper.Core.Learning;

namespace Winpepper.Core.Learning;

public sealed class PostPasteWatcher : IDisposable
{
    private readonly IFocusedElementTextWatcher _watcher;
    private readonly ICorrectionWriter _writer;
    private readonly IPostPasteToastPrompt _prompt;
    private readonly TimeSpan _watchWindow;
    private readonly HashSet<(string Wrong, string Right)> _sessionSuppress = new();
    private readonly object _gate = new();
    private bool _disposed;

    public PostPasteWatcher(
        IFocusedElementTextWatcher watcher,
        ICorrectionWriter writer,
        IPostPasteToastPrompt prompt,
        TimeSpan? watchWindow = null)
    {
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _watchWindow = watchWindow ?? TimeSpan.FromSeconds(30);
    }

    public async Task BeginAsync(PostPasteContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (_disposed) return;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(_watchWindow);
        cts.Token.Register(() => tcs.TrySetResult(false));

        var sub = _watcher.Subscribe(ctx.ElementId, async change =>
        {
            if (cts.IsCancellationRequested) return;
            var candidate = LearningDiffAnalyzer.Analyze(ctx.InjectedText, change.NewText);
            if (candidate is null) return;

            lock (_gate)
            {
                if (_sessionSuppress.Contains((candidate.Wrong, candidate.Right))) return;
            }

            PostPasteDecision decision;
            try { decision = await _prompt.AskAsync(candidate, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { decision = PostPasteDecision.No; }

            ApplyDecision(candidate, decision);
            tcs.TrySetResult(true);
        });

        try { await tcs.Task.ConfigureAwait(false); }
        finally { sub.Dispose(); }
    }

    private void ApplyDecision(LearningCandidate c, PostPasteDecision decision)
    {
        switch (decision)
        {
            case PostPasteDecision.Yes: _writer.AddReplacement(c.Wrong, c.Right); break;
            case PostPasteDecision.Preferred: _writer.AddPreferred(c.Right); break;
            case PostPasteDecision.No: lock (_gate) _sessionSuppress.Add((c.Wrong, c.Right)); break;
        }
    }

    public void Dispose() { _disposed = true; }
}
```

Update the test to wrap `CorrectionStore` in an inline `ICorrectionWriter` adapter:

```csharp
private sealed class StoreWriter : ICorrectionWriter
{
    private readonly CorrectionStore _s;
    public StoreWriter(CorrectionStore s) { _s = s; }
    public bool AddReplacement(string w, string r) => _s.AddReplacement(w, r);
    public bool AddPreferred(string v) => _s.AddPreferred(v);
}
```

In each test, replace `using var ppw = new PostPasteWatcher(watcher, store, prompt, ...);` with `using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, ...);`.

- [ ] **Step 9: Verify pass after refactor**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~PostPasteWatcherTests"
```

Expected: 5 tests pass. Skip Step 8's project-reference edit — it's no longer needed once the interface boundary is in place.

- [ ] **Step 10: Add `CorrectionStoreWriter` adapter in `Winpepper.Corrections`**

So production code has a one-liner adapter rather than re-implementing the interface in `AppShell`. Create `src/Winpepper.Corrections/CorrectionStoreWriter.cs`:

```csharp
using Winpepper.Core.Learning;

namespace Winpepper.Corrections;

public sealed class CorrectionStoreWriter : ICorrectionWriter
{
    private readonly CorrectionStore _store;
    public CorrectionStoreWriter(CorrectionStore store) { _store = store; }
    public bool AddReplacement(string wrong, string right) => _store.AddReplacement(wrong, right);
    public bool AddPreferred(string value) => _store.AddPreferred(value);
}
```

`Winpepper.Corrections` already references `Winpepper.Core`, so this compiles.

- [ ] **Step 11: Commit**

```bash
git add src/Winpepper.Core/Learning src/Winpepper.Corrections/CorrectionStoreWriter.cs tests/Winpepper.Core.Tests/Learning/PostPasteWatcherTests.cs
git commit -m "feat(learning): PostPasteWatcher with toast prompt + correction writer"
```

---

## Task 7: Windows `UiaFocusedElementTextWatcher`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Learning/UiaFocusedElementCapture.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Learning/UiaFocusedElementTextWatcher.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Learning/UiaFocusedElementCaptureTests.cs`

Spec §8.2 (2): subscribe to UIA `TextEdit_TextChangedEvent` on the focused element (fallback `Text_TextChangedEvent`). Two pieces:

- `UiaFocusedElementCapture` — pure logic that turns a foreground `IntPtr` plus a UIA RuntimeId into the opaque `string` element id `PostPasteContext` carries.
- `UiaFocusedElementTextWatcher` — the actual subscription. This file is Windows-only (under `#if WINDOWS`) and uses `System.Windows.Automation` (same dependency Plan 2's `UiaTreeReader` uses).

Only `UiaFocusedElementCapture` has Linux-runnable unit tests. The subscription class is exercised manually in the smoke test (Task 22).

- [ ] **Step 1: Write the failing test `tests/Winpepper.Platform.Tests/Learning/UiaFocusedElementCaptureTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Learning;
using Xunit;

namespace Winpepper.Platform.Tests.Learning;

public class UiaFocusedElementCaptureTests
{
    [Fact]
    public void RuntimeIdToString_Joins_Ints_With_Dots_For_Stable_Key()
    {
        var key = UiaFocusedElementCapture.RuntimeIdToString(new[] { 42, 7, 1, 5 });
        key.ShouldBe("42.7.1.5");
    }

    [Fact]
    public void RuntimeIdToString_Empty_Array_Returns_Empty_String()
    {
        UiaFocusedElementCapture.RuntimeIdToString(Array.Empty<int>()).ShouldBe("");
    }

    [Fact]
    public void RuntimeIdToString_Null_Array_Returns_Empty_String()
    {
        UiaFocusedElementCapture.RuntimeIdToString(null).ShouldBe("");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
cd /home/jesse/git/winpepper
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~UiaFocusedElementCaptureTests"
```

Expected: build fails — `UiaFocusedElementCapture` not found.

- [ ] **Step 3: Implement `src/Winpepper.Platform/Learning/UiaFocusedElementCapture.cs`**

```csharp
namespace Winpepper.Platform.Learning;

/// <summary>
/// Pure helpers for translating UIA RuntimeIds into the opaque string ids that
/// <see cref="UiaFocusedElementTextWatcher"/> (Windows-only) and the
/// pure-C# <c>PostPasteWatcher</c> exchange. Spec §8.2 (1)–(2).
/// </summary>
public static class UiaFocusedElementCapture
{
    public static string RuntimeIdToString(int[]? runtimeId)
    {
        if (runtimeId is null || runtimeId.Length == 0) return string.Empty;
        return string.Join('.', runtimeId);
    }
}
```

- [ ] **Step 4: Verify the pure-C# test passes**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~UiaFocusedElementCaptureTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Implement `src/Winpepper.Platform/Learning/UiaFocusedElementTextWatcher.cs` (Windows-only)**

```csharp
#if WINDOWS
using System.Collections.Concurrent;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Winpepper.Core.Learning;

namespace Winpepper.Platform.Learning;

/// <summary>
/// Subscribes to UIA <c>TextEdit_TextChangedEvent</c> on the focused element
/// (falls back to <c>Text_TextChangedEvent</c>). Spec §8.2 (2).
///
/// The watcher is constructed once at app start and reused; each call to
/// <see cref="Subscribe"/> resolves the live <c>AutomationElement</c> for the
/// supplied RuntimeId, attaches the handler, and returns an <see cref="IDisposable"/>
/// that detaches the handler.
///
/// Read of the element's current text on each event uses the same fallback
/// chain as <c>UiaTextExtraction</c> from Plan 2: <c>TextPattern.DocumentRange.GetText</c>,
/// then <c>ValuePattern.Value</c>, then <c>Name</c>.
/// </summary>
public sealed class UiaFocusedElementTextWatcher : IFocusedElementTextWatcher
{
    private readonly ILogger<UiaFocusedElementTextWatcher> _log;
    private readonly ConcurrentDictionary<string, AutomationElement> _byId = new();

    public UiaFocusedElementTextWatcher(ILogger<UiaFocusedElementTextWatcher> log) { _log = log; }

    /// <summary>
    /// Register a live UIA element under the supplied id so a later
    /// <see cref="Subscribe"/> call can find it. The orchestrator (PipelineHost
    /// Task 11) registers right before injection completes.
    /// </summary>
    public void RegisterFocusedElement(string elementId, AutomationElement element)
    {
        if (string.IsNullOrEmpty(elementId)) return;
        _byId[elementId] = element;
    }

    public IDisposable Subscribe(string elementId, Func<FocusedElementTextChange, Task> onChange)
    {
        if (!_byId.TryGetValue(elementId, out var element))
        {
            _log.LogDebug("UiaFocusedElementTextWatcher: no element registered for id {Id}", elementId);
            return new NoopDisposable();
        }

        AutomationEvent? subscribedEvent;
        AutomationEventHandler handler = (s, e) =>
        {
            try
            {
                if (s is not AutomationElement el) return;
                var text = ReadText(el);
                if (text is null) return;
                _ = onChange(new FocusedElementTextChange(elementId, text, DateTime.UtcNow));
            }
            catch (Exception ex) { _log.LogTrace(ex, "text-change handler threw"); }
        };

        try
        {
            Automation.AddAutomationEventHandler(
                TextPattern.TextChangedEvent, element, TreeScope.Element, handler);
            subscribedEvent = TextPattern.TextChangedEvent;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "TextEdit_TextChangedEvent unavailable; falling back to ValuePattern poll");
            subscribedEvent = null;
        }

        return new Subscription(() =>
        {
            try
            {
                if (subscribedEvent is not null)
                    Automation.RemoveAutomationEventHandler(subscribedEvent, element, handler);
            }
            catch (Exception ex) { _log.LogTrace(ex, "RemoveAutomationEventHandler failed"); }
            _byId.TryRemove(elementId, out _);
        });
    }

    private static string? ReadText(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tp) && tp is TextPattern tpat)
                return tpat.DocumentRange.GetText(8000);
        }
        catch { }
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && vp is ValuePattern vpat)
                return vpat.Current.Value;
        }
        catch { }
        try { return el.Current.Name; } catch { return null; }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        public Subscription(Action dispose) { _dispose = dispose; }
        public void Dispose() => _dispose();
    }

    private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
}
#endif
```

- [ ] **Step 6: Reference `Winpepper.Core` from `Winpepper.Platform`**

`Winpepper.Platform.csproj` already references `Winpepper.Core`. Confirm no edit is needed by reading `src/Winpepper.Platform/Winpepper.Platform.csproj`; if the `<ProjectReference Include="..\Winpepper.Core\Winpepper.Core.csproj" />` line is present (it is, from Plan 1), no change.

- [ ] **Step 7: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.Platform/Winpepper.Platform.csproj -f net9.0-windows10.0.19041.0"
```

Expected: build succeeds (this project does not hit the WinUI XAML compiler).

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Platform/Learning tests/Winpepper.Platform.Tests/Learning
git commit -m "feat(platform): UIA-backed IFocusedElementTextWatcher implementation"
```

---

## Task 8: Capture the focused UIA element at injection time

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Learning/FocusedElementSnapshot.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Learning/FocusedElementCapturer.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Learning/FocusedElementSnapshotTests.cs`

Spec §8.2 (1): "After injection completes, record `(foregroundWindowHandle, focusedElementRuntimeId, injectedText, injectionEndTime)`." We capture the foreground hwnd + the focused `AutomationElement` and the stringified RuntimeId. The capture API is Windows-only; the record type is cross-platform.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Platform.Tests/Learning/FocusedElementSnapshotTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Learning;
using Xunit;

namespace Winpepper.Platform.Tests.Learning;

public class FocusedElementSnapshotTests
{
    [Fact]
    public void Empty_Snapshot_Has_IsValid_False()
    {
        FocusedElementSnapshot.Empty.IsValid.ShouldBeFalse();
        FocusedElementSnapshot.Empty.ElementId.ShouldBe("");
    }

    [Fact]
    public void Snapshot_With_Element_Id_Is_Valid()
    {
        var snap = new FocusedElementSnapshot
        {
            ForegroundHwnd = new IntPtr(0x1234),
            ElementId = "42.7",
            WindowTitle = "Notepad",
        };
        snap.IsValid.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~FocusedElementSnapshotTests"
```

Expected: build fails — `FocusedElementSnapshot` not found.

- [ ] **Step 3: Implement `src/Winpepper.Platform/Learning/FocusedElementSnapshot.cs`**

```csharp
namespace Winpepper.Platform.Learning;

/// <summary>
/// What we know about the focused element at injection time. Spec §8.2 (1).
/// </summary>
public sealed record FocusedElementSnapshot
{
    public required IntPtr ForegroundHwnd { get; init; }
    public required string ElementId { get; init; }
    public required string WindowTitle { get; init; }

    public bool IsValid => !string.IsNullOrEmpty(ElementId);

    public static FocusedElementSnapshot Empty { get; } = new()
    {
        ForegroundHwnd = IntPtr.Zero,
        ElementId = string.Empty,
        WindowTitle = string.Empty,
    };
}
```

- [ ] **Step 4: Implement `src/Winpepper.Platform/Learning/FocusedElementCapturer.cs` (Windows-only)**

```csharp
#if WINDOWS
using System.Windows.Automation;
using Microsoft.Extensions.Logging;
using Winpepper.Platform.WindowContext;

namespace Winpepper.Platform.Learning;

/// <summary>
/// Resolves the currently-focused UIA element and packages it as a
/// <see cref="FocusedElementSnapshot"/>. Spec §8.2 (1).
/// Also registers the live <c>AutomationElement</c> with the supplied
/// <see cref="UiaFocusedElementTextWatcher"/> so a later <c>Subscribe</c>
/// call can attach to it.
/// </summary>
public sealed class FocusedElementCapturer
{
    private readonly UiaFocusedElementTextWatcher _watcher;
    private readonly ILogger<FocusedElementCapturer> _log;

    public FocusedElementCapturer(
        UiaFocusedElementTextWatcher watcher,
        ILogger<FocusedElementCapturer> log)
    {
        _watcher = watcher;
        _log = log;
    }

    public FocusedElementSnapshot Capture()
    {
        IntPtr hwnd;
        try { hwnd = UiaNative.GetForegroundWindow(); }
        catch (Exception ex) { _log.LogDebug(ex, "GetForegroundWindow failed"); return FocusedElementSnapshot.Empty; }

        AutomationElement? focused = null;
        try { focused = AutomationElement.FocusedElement; }
        catch (Exception ex) { _log.LogDebug(ex, "AutomationElement.FocusedElement failed"); }

        if (focused is null) return FocusedElementSnapshot.Empty;

        int[]? runtimeId = null;
        try { runtimeId = focused.GetRuntimeId(); }
        catch (Exception ex) { _log.LogDebug(ex, "GetRuntimeId failed"); }
        var id = UiaFocusedElementCapture.RuntimeIdToString(runtimeId);
        if (string.IsNullOrEmpty(id)) return FocusedElementSnapshot.Empty;

        var title = "";
        try
        {
            var buf = new char[512];
            var len = UiaNative.GetWindowTextW(hwnd, buf, buf.Length);
            if (len > 0) title = new string(buf, 0, len);
        }
        catch { }

        _watcher.RegisterFocusedElement(id, focused);
        return new FocusedElementSnapshot { ForegroundHwnd = hwnd, ElementId = id, WindowTitle = title };
    }
}
#endif
```

- [ ] **Step 5: Verify pass on Linux for the record-only test**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~FocusedElementSnapshotTests"
```

Expected: 2 tests pass.

- [ ] **Step 6: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.Platform/Winpepper.Platform.csproj -f net9.0-windows10.0.19041.0"
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Platform/Learning tests/Winpepper.Platform.Tests/Learning/FocusedElementSnapshotTests.cs
git commit -m "feat(platform): FocusedElementCapturer + FocusedElementSnapshot"
```

---

## Task 9: Diagnostics bundle assembler

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Diagnostics/DiagnosticsBundle.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Diagnostics/DiagnosticsSysInfo.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Diagnostics/DiagnosticsBundleBuilder.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Diagnostics/DiagnosticsBundleBuilderTests.cs`

Spec §7.3 + §9.5: "zips logs + system info + recent history metadata, never audio".

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Diagnostics/DiagnosticsBundleBuilderTests.cs`**

```csharp
using System.IO.Compression;
using Shouldly;
using Winpepper.Core.Diagnostics;
using Xunit;

namespace Winpepper.Core.Tests.Diagnostics;

public class DiagnosticsBundleBuilderTests : IDisposable
{
    private readonly string _root;
    public DiagnosticsBundleBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"wp-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Build_Zips_Logs_Settings_History_Index_And_SysInfo_But_Skips_Wav()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "winpepper-20260516.log"), "log content");
        File.WriteAllText(Path.Combine(logs, "winpepper-20260515.log"), "older log");

        var historyRoot = Path.Combine(_root, "history"); Directory.CreateDirectory(historyRoot);
        File.WriteAllText(Path.Combine(historyRoot, "index.json"), "[]");
        File.WriteAllBytes(Path.Combine(historyRoot, "session-1.wav"), new byte[] { 0, 1, 2 });

        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{"schema":1}""");

        var output = Path.Combine(_root, "bundle.zip");
        var inputs = new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = historyRoot,
            SettingsPath = settings,
            SysInfo = new DiagnosticsSysInfo
            {
                AppVersion = "0.5.0",
                OsDescription = "Windows 11 Pro 23H2",
                ProcessorCount = 16,
                Is64BitOs = true,
                CapturedAtUtc = DateTime.UtcNow,
            },
        };

        DiagnosticsBundleBuilder.Build(inputs, output);

        File.Exists(output).ShouldBeTrue();
        using var zip = ZipFile.OpenRead(output);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        names.ShouldContain("logs/winpepper-20260516.log");
        names.ShouldContain("logs/winpepper-20260515.log");
        names.ShouldContain("history-index.json");
        names.ShouldContain("settings.json");
        names.ShouldContain("sysinfo.json");
        names.ShouldNotContain(n => n.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_Tolerates_Missing_History_Index()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{}""");

        var output = Path.Combine(_root, "bundle.zip");
        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = Path.Combine(_root, "does-not-exist"),
            SettingsPath = settings,
            SysInfo = DiagnosticsSysInfo.Capture("0.0.0"),
        }, output);

        File.Exists(output).ShouldBeTrue();
        using var zip = ZipFile.OpenRead(output);
        zip.Entries.ShouldNotContain(e => e.FullName == "history-index.json");
    }

    [Fact]
    public void Build_Tolerates_Missing_Settings_File()
    {
        var logs = Path.Combine(_root, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "a.log"), "x");

        var output = Path.Combine(_root, "bundle.zip");
        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = logs,
            HistoryRoot = _root,
            SettingsPath = Path.Combine(_root, "no-settings.json"),
            SysInfo = DiagnosticsSysInfo.Capture("0.0.0"),
        }, output);

        File.Exists(output).ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DiagnosticsBundleBuilderTests"
```

Expected: build fails — `DiagnosticsBundle`, `DiagnosticsSysInfo`, `DiagnosticsBundleBuilder` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Diagnostics/DiagnosticsSysInfo.cs`**

```csharp
using System.Runtime.InteropServices;

namespace Winpepper.Core.Diagnostics;

/// <summary>System-info block written into <c>sysinfo.json</c> inside the bundle.</summary>
public sealed record DiagnosticsSysInfo
{
    public required string AppVersion { get; init; }
    public required string OsDescription { get; init; }
    public required int ProcessorCount { get; init; }
    public required bool Is64BitOs { get; init; }
    public required DateTime CapturedAtUtc { get; init; }

    public static DiagnosticsSysInfo Capture(string appVersion) => new()
    {
        AppVersion = appVersion,
        OsDescription = RuntimeInformation.OSDescription,
        ProcessorCount = Environment.ProcessorCount,
        Is64BitOs = Environment.Is64BitOperatingSystem,
        CapturedAtUtc = DateTime.UtcNow,
    };
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Diagnostics/DiagnosticsBundle.cs`**

```csharp
namespace Winpepper.Core.Diagnostics;

/// <summary>Inputs to <see cref="DiagnosticsBundleBuilder.Build"/>.</summary>
public sealed record DiagnosticsBundle
{
    public required string LogsDir { get; init; }
    public required string HistoryRoot { get; init; }
    public required string SettingsPath { get; init; }
    public required DiagnosticsSysInfo SysInfo { get; init; }
}
```

- [ ] **Step 5: Implement `src/Winpepper.Core/Diagnostics/DiagnosticsBundleBuilder.cs`**

```csharp
using System.IO.Compression;
using System.Text.Json;

namespace Winpepper.Core.Diagnostics;

/// <summary>
/// Assembles the "Copy diagnostics bundle" zip. Spec §7.3 and §9.5. Never
/// includes <c>*.wav</c> files (filter is explicit, not by directory).
/// </summary>
public static class DiagnosticsBundleBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Build(DiagnosticsBundle inputs, string outputZipPath)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrEmpty(outputZipPath);

        var parent = Path.GetDirectoryName(outputZipPath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        if (File.Exists(outputZipPath)) File.Delete(outputZipPath);

        using var fs = File.Create(outputZipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        // Logs.
        if (Directory.Exists(inputs.LogsDir))
        {
            foreach (var file in Directory.EnumerateFiles(inputs.LogsDir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                AddFile(zip, file, $"logs/{name}");
            }
        }

        // History index (NO wav files).
        if (Directory.Exists(inputs.HistoryRoot))
        {
            var indexPath = Path.Combine(inputs.HistoryRoot, "index.json");
            if (File.Exists(indexPath))
                AddFile(zip, indexPath, "history-index.json");
        }

        // Settings.
        if (File.Exists(inputs.SettingsPath))
            AddFile(zip, inputs.SettingsPath, "settings.json");

        // Sysinfo.
        var entry = zip.CreateEntry("sysinfo.json", CompressionLevel.Fastest);
        using (var es = entry.Open())
        using (var sw = new StreamWriter(es))
        {
            sw.Write(JsonSerializer.Serialize(inputs.SysInfo, JsonOptions));
        }
    }

    private static void AddFile(ZipArchive zip, string source, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var es = entry.Open();
        using var fs = File.OpenRead(source);
        fs.CopyTo(es);
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DiagnosticsBundleBuilderTests"
```

Expected: 3 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Diagnostics tests/Winpepper.Core.Tests/Diagnostics
git commit -m "feat(diagnostics): bundle builder zips logs/history/settings/sysinfo"
```

---

## Task 10: `DiagnosticsViewModel`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/DiagnosticsViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/DiagnosticsViewModelTests.cs`

The view-model exposes:

- A bound `ObservableCollection<LogTailEntry>` (capped at the buffer's capacity).
- A `MinimumLevel` property (`LogLevel`) for the user's Debug/Info toggle.
- Commands: `OpenLogFolder()`, `CopyDiagnosticsBundle()`.
- A read-only `LastBundlePath` string surfaced under the button as feedback.

The actual log folder open + bundle save dialog go through `IDiagnosticsHost` (defined here, implemented by `AppShell` in Task 14).

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/ViewModels/DiagnosticsViewModelTests.cs`**

```csharp
using System.Collections.Specialized;
using Microsoft.Extensions.Logging;
using Shouldly;
using Winpepper.Core.Logging;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class DiagnosticsViewModelTests
{
    private static DiagnosticsViewModel Build(LogRingBuffer buf, FakeHost host)
        => new(buf, new SynchronousUiThread(), host);

    [Fact]
    public void Existing_Buffer_Entries_Are_Hydrated_On_Construct()
    {
        var buf = new LogRingBuffer(capacity: 5);
        buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", "boot"));
        var host = new FakeHost();

        var vm = Build(buf, host);

        vm.Tail.Count.ShouldBe(1);
        vm.Tail[0].Message.ShouldBe("boot");
    }

    [Fact]
    public void New_Appends_Flow_Into_Tail()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost();
        var vm = Build(buf, host);

        buf.Append(new LogTailEntry(DateTime.UtcNow, "WRN", "uh oh"));

        vm.Tail.Count.ShouldBe(1);
        vm.Tail[0].Level.ShouldBe("WRN");
    }

    [Fact]
    public async Task CopyDiagnosticsBundle_Invokes_Host_And_Sets_LastBundlePath()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost { ReturnedBundlePath = "C:\\temp\\bundle.zip" };
        var vm = Build(buf, host);

        await vm.CopyDiagnosticsBundleAsync();

        host.SaveBundleCalled.ShouldBeTrue();
        vm.LastBundlePath.ShouldBe("C:\\temp\\bundle.zip");
    }

    [Fact]
    public void OpenLogFolder_Invokes_Host()
    {
        var buf = new LogRingBuffer(capacity: 5);
        var host = new FakeHost();
        var vm = Build(buf, host);

        vm.OpenLogFolder();

        host.OpenLogFolderCalled.ShouldBeTrue();
    }

    [Fact]
    public void Tail_Is_Capped_At_Buffer_Capacity()
    {
        var buf = new LogRingBuffer(capacity: 3);
        var host = new FakeHost();
        var vm = Build(buf, host);

        for (var i = 0; i < 10; i++) buf.Append(new LogTailEntry(DateTime.UtcNow, "INF", $"l{i}"));

        vm.Tail.Count.ShouldBe(3);
        vm.Tail[0].Message.ShouldBe("l7");
        vm.Tail[2].Message.ShouldBe("l9");
    }

    private sealed class FakeHost : IDiagnosticsHost
    {
        public bool OpenLogFolderCalled { get; private set; }
        public bool SaveBundleCalled { get; private set; }
        public string? ReturnedBundlePath { get; set; }

        public void OpenLogFolder() => OpenLogFolderCalled = true;
        public Task<string?> SaveBundleAsync()
        {
            SaveBundleCalled = true;
            return Task.FromResult(ReturnedBundlePath);
        }
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DiagnosticsViewModelTests"
```

Expected: build fails — `DiagnosticsViewModel`, `IDiagnosticsHost` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/ViewModels/DiagnosticsViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Winpepper.Core.Logging;
using Winpepper.Core.Threading;

namespace Winpepper.Core.ViewModels;

/// <summary>
/// Plug for the Diagnostics page. The page binds the WinUI <c>ListView</c> to
/// <see cref="Tail"/>, the level combo to <see cref="MinimumLevel"/>, and
/// invokes <see cref="OpenLogFolder"/> / <see cref="CopyDiagnosticsBundleAsync"/>
/// from button clicks. Spec §7.3.
/// </summary>
public interface IDiagnosticsHost
{
    void OpenLogFolder();
    /// <summary>Show a save dialog and write the bundle. Null = user cancelled.</summary>
    Task<string?> SaveBundleAsync();
}

public sealed class DiagnosticsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly LogRingBuffer _buffer;
    private readonly IUiThread _ui;
    private readonly IDiagnosticsHost _host;
    private LogLevel _level = LogLevel.Information;
    private string _lastBundlePath = "";

    public ObservableCollection<LogTailEntry> Tail { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public DiagnosticsViewModel(LogRingBuffer buffer, IUiThread ui, IDiagnosticsHost host)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        foreach (var e in _buffer.Snapshot()) Tail.Add(e);
        _buffer.Appended += OnAppended;
    }

    public LogLevel MinimumLevel
    {
        get => _level;
        set { if (_level == value) return; _level = value; Raise(nameof(MinimumLevel)); }
    }

    public string LastBundlePath
    {
        get => _lastBundlePath;
        private set { if (_lastBundlePath == value) return; _lastBundlePath = value; Raise(nameof(LastBundlePath)); }
    }

    public void OpenLogFolder() => _host.OpenLogFolder();

    public async Task CopyDiagnosticsBundleAsync()
    {
        var path = await _host.SaveBundleAsync().ConfigureAwait(false);
        if (path is not null) _ui.Post(() => LastBundlePath = path);
    }

    private void OnAppended(LogTailEntry entry)
    {
        _ui.Post(() =>
        {
            while (Tail.Count >= _buffer.Capacity) Tail.RemoveAt(0);
            Tail.Add(entry);
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose() => _buffer.Appended -= OnAppended;
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~DiagnosticsViewModelTests"
```

Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/ViewModels/DiagnosticsViewModel.cs tests/Winpepper.Core.Tests/ViewModels/DiagnosticsViewModelTests.cs
git commit -m "feat(viewmodels): DiagnosticsViewModel with bound log tail"
```

---

## Task 11: `CrashHandler` + `MiniDumpWriter`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Crash/MiniDumpWriter.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Crash/DbgHelpNative.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Crash/CrashHandler.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Crash/ICrashSink.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Crash/CrashHandlerTests.cs`

Spec §9.3: catch `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`. Log. Write a minidump via `dbghelp.dll!MiniDumpWriteDump`. Attempt state-machine reset to Idle. Exit only if reset fails.

The cross-platform `Winpepper.Core.Crash.CrashHandler` orchestrates via the `ICrashSink` interface; the Windows-only `MiniDumpWriter` is the production implementation. Unit tests use a `FakeCrashSink`.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Crash/CrashHandlerTests.cs`**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Core.Crash;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Xunit;

namespace Winpepper.Core.Tests.Crash;

public class CrashHandlerTests
{
    [Fact]
    public void OnUnhandled_Writes_Dump_Logs_Bus_And_Tries_Engine_Reset()
    {
        var engine = new SessionEngine();
        engine.Apply(SessionEvent.StartRequested); // engine into Recording
        var bus = new ErrorBus();
        var sink = new FakeCrashSink { WriteDumpResult = "C:\\dumps\\one.dmp" };
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        var ex = new InvalidOperationException("boom");
        var keepAlive = handler.HandleUnhandled(ex, fromTaskScheduler: false);

        sink.WroteDump.ShouldBeTrue();
        engine.State.ShouldBe(SessionState.Idle);
        bus.MostRecent()?.Stage.ShouldBe(ErrorStage.Crash);
        keepAlive.ShouldBeTrue();
    }

    [Fact]
    public void OnUnhandled_Returns_False_When_Reset_Fails()
    {
        var engine = new SessionEngine();
        var bus = new ErrorBus();
        var sink = new FakeCrashSink { WriteDumpResult = null, ThrowOnReset = true };
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        var keepAlive = handler.HandleUnhandled(new Exception("x"), fromTaskScheduler: true);

        keepAlive.ShouldBeFalse();
    }

    [Fact]
    public void OnUnhandled_Sets_Reset_Source_Tag_From_Caller()
    {
        var engine = new SessionEngine();
        var bus = new ErrorBus();
        var sink = new FakeCrashSink();
        var handler = new CrashHandler(sink, bus, engine, NullLogger<CrashHandler>.Instance);

        handler.HandleUnhandled(new Exception("a"), fromTaskScheduler: true);
        sink.LastSource.ShouldBe("TaskScheduler.UnobservedTaskException");

        handler.HandleUnhandled(new Exception("b"), fromTaskScheduler: false);
        sink.LastSource.ShouldBe("AppDomain.UnhandledException");
    }

    private sealed class FakeCrashSink : ICrashSink
    {
        public bool WroteDump { get; private set; }
        public string? WriteDumpResult { get; set; }
        public bool ThrowOnReset { get; set; }
        public string? LastSource { get; private set; }

        public string? WriteDump(Exception ex, string source)
        {
            WroteDump = true;
            LastSource = source;
            return WriteDumpResult;
        }

        public void ResetSessionEngine(SessionEngine engine)
        {
            if (ThrowOnReset) throw new InvalidOperationException("reset failed");
            engine.Apply(SessionEvent.CancelRequested);
        }
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CrashHandlerTests"
```

Expected: build fails — `CrashHandler` and `ICrashSink` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Crash/ICrashSink.cs`**

```csharp
using Winpepper.Core.Sessions;

namespace Winpepper.Core.Crash;

/// <summary>
/// Crash-time side effects abstracted so <see cref="CrashHandler"/> is unit-testable.
/// The production Windows implementation lives in
/// <c>Winpepper.Platform.Crash.MiniDumpWriter</c>.
/// </summary>
public interface ICrashSink
{
    /// <summary>Writes a process minidump and returns its full path, or null on failure.</summary>
    string? WriteDump(Exception ex, string source);

    /// <summary>Returns the SessionEngine to <see cref="SessionState.Idle"/>. May throw.</summary>
    void ResetSessionEngine(SessionEngine engine);
}
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Crash/CrashHandler.cs`**

```csharp
using Microsoft.Extensions.Logging;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;

namespace Winpepper.Core.Crash;

/// <summary>
/// Routes unhandled exceptions through the standard pipeline: log → minidump
/// → ErrorBus → engine reset. Spec §9.3.
/// </summary>
public sealed class CrashHandler
{
    private readonly ICrashSink _sink;
    private readonly ErrorBus _bus;
    private readonly SessionEngine _engine;
    private readonly ILogger<CrashHandler> _log;

    public CrashHandler(ICrashSink sink, ErrorBus bus, SessionEngine engine, ILogger<CrashHandler> log)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Returns true if the app should remain running, false if reset failed and
    /// the caller should exit. Per spec: "The app exits only if reset fails."
    /// </summary>
    public bool HandleUnhandled(Exception ex, bool fromTaskScheduler)
    {
        var source = fromTaskScheduler
            ? "TaskScheduler.UnobservedTaskException"
            : "AppDomain.UnhandledException";

        _log.LogCritical(ex, "Unhandled exception from {Source}", source);

        string? dumpPath = null;
        try { dumpPath = _sink.WriteDump(ex, source); }
        catch (Exception sinkEx) { _log.LogError(sinkEx, "MiniDump write failed"); }
        if (dumpPath is not null) _log.LogInformation("Minidump written to {Path}", dumpPath);

        _bus.Report(ErrorStage.Crash, ex, Guid.Empty);

        try
        {
            _sink.ResetSessionEngine(_engine);
            return true;
        }
        catch (Exception resetEx)
        {
            _log.LogCritical(resetEx, "SessionEngine reset failed; app will exit");
            return false;
        }
    }
}
```

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~CrashHandlerTests"
```

Expected: 3 tests pass.

- [ ] **Step 6: Implement `src/Winpepper.Platform/Crash/DbgHelpNative.cs` (Windows)**

```csharp
#if WINDOWS
using System.Runtime.InteropServices;

namespace Winpepper.Platform.Crash;

internal static partial class DbgHelpNative
{
    [Flags]
    public enum MINIDUMP_TYPE : uint
    {
        MiniDumpNormal = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithFullMemory = 0x00000002,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpWithThreadInfo = 0x00001000,
        MiniDumpWithProcessThreadData = 0x00010000,
    }

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        MINIDUMP_TYPE dumpType,
        IntPtr expParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetCurrentProcessId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr GetCurrentProcess();
}
#endif
```

- [ ] **Step 7: Implement `src/Winpepper.Platform/Crash/MiniDumpWriter.cs` (Windows)**

```csharp
#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Core.Crash;
using Winpepper.Core.Sessions;

namespace Winpepper.Platform.Crash;

public sealed class MiniDumpWriter : ICrashSink
{
    private readonly string _crashDir;
    private readonly ILogger<MiniDumpWriter> _log;

    public MiniDumpWriter(string crashDir, ILogger<MiniDumpWriter> log)
    {
        _crashDir = crashDir;
        _log = log;
        Directory.CreateDirectory(crashDir);
    }

    public string? WriteDump(Exception ex, string source)
    {
        var fileName = $"winpepper-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp";
        var fullPath = Path.Combine(_crashDir, fileName);

        try
        {
            using var fs = File.Create(fullPath);
            var ok = DbgHelpNative.MiniDumpWriteDump(
                DbgHelpNative.GetCurrentProcess(),
                DbgHelpNative.GetCurrentProcessId(),
                fs.SafeFileHandle.DangerousGetHandle(),
                DbgHelpNative.MINIDUMP_TYPE.MiniDumpWithThreadInfo
                    | DbgHelpNative.MINIDUMP_TYPE.MiniDumpWithProcessThreadData,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (!ok)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _log.LogError("MiniDumpWriteDump failed: 0x{Err:X}", err);
                try { File.Delete(fullPath); } catch { }
                return null;
            }
        }
        catch (Exception innerEx)
        {
            _log.LogError(innerEx, "minidump file IO failed");
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
            return null;
        }

        // Sidecar text file so a user can read the crash without WinDbg.
        try
        {
            var sidecar = Path.ChangeExtension(fullPath, ".txt");
            File.WriteAllText(sidecar,
                $"source: {source}{Environment.NewLine}" +
                $"type:   {ex.GetType().FullName}{Environment.NewLine}" +
                $"msg:    {ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                ex.ToString());
        }
        catch { /* sidecar best-effort */ }

        return fullPath;
    }

    public void ResetSessionEngine(SessionEngine engine)
    {
        // Drive the engine back to Idle by applying CancelRequested. If it's
        // already Idle this is a no-op (state machine ignores the event).
        engine.Apply(SessionEvent.CancelRequested);
        if (engine.State != SessionState.Idle)
        {
            // Cancel didn't work from this state; apply Failed which the
            // state machine treats as a universal sink to Idle.
            engine.Apply(SessionEvent.Failed);
        }
        if (engine.State != SessionState.Idle)
            throw new InvalidOperationException("SessionEngine refused to reset to Idle");
    }
}
#endif
```

- [ ] **Step 8: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.Platform/Winpepper.Platform.csproj -f net9.0-windows10.0.19041.0"
```

Expected: build succeeds.

- [ ] **Step 9: Commit**

```bash
git add src/Winpepper.Core/Crash src/Winpepper.Platform/Crash tests/Winpepper.Core.Tests/Crash
git commit -m "feat(crash): CrashHandler + MiniDumpWriter (dbghelp P/Invoke)"
```

---

## Task 12: `IToastService` + fake

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Notifications/IToastService.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Notifications/ToastButton.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Notifications/FakeToastService.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Notifications/FakeToastServiceTests.cs`

Spec §5.6: "the cleaned text is copied to the clipboard and a toast says so". Spec §8.2 (5): the post-paste learning prompt is a non-modal toast. We unify both flows behind one interface.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Notifications/FakeToastServiceTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.Core.Tests.Notifications;

public class FakeToastServiceTests
{
    [Fact]
    public async Task ShowAsync_Returns_Default_Button_Tag_After_Timeout()
    {
        var fake = new FakeToastService();
        fake.AutoSelect(""); // empty = let timeout fire
        var result = await fake.ShowAsync(
            "title", "body",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromMilliseconds(10));
        result.ShouldBe(""); // empty string means "no choice / timed out"
    }

    [Fact]
    public async Task ShowAsync_Returns_Selected_Button_Tag()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("yes");
        var result = await fake.ShowAsync("t", "b",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromSeconds(30));
        result.ShouldBe("yes");
    }

    [Fact]
    public async Task ShowAsync_Records_Last_Call_For_Assertion()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        await fake.ShowAsync("title", "body",
            new[] { new ToastButton("yes", "Yes"), new ToastButton("no", "No") },
            timeout: TimeSpan.FromSeconds(1));

        fake.Calls.Count.ShouldBe(1);
        fake.Calls[0].Title.ShouldBe("title");
        fake.Calls[0].Body.ShouldBe("body");
        fake.Calls[0].Buttons.Length.ShouldBe(2);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~FakeToastServiceTests"
```

Expected: build fails — types missing.

- [ ] **Step 3: Implement `src/Winpepper.Core/Notifications/ToastButton.cs`**

```csharp
namespace Winpepper.Core.Notifications;

/// <summary>One button on a toast. <c>Tag</c> is what <see cref="IToastService.ShowAsync"/> resolves to.</summary>
public sealed record ToastButton(string Tag, string Label);
```

- [ ] **Step 4: Implement `src/Winpepper.Core/Notifications/IToastService.cs`**

```csharp
namespace Winpepper.Core.Notifications;

/// <summary>
/// Non-modal toast surface. The empty string is reserved for "no button selected /
/// timeout". Buttons return their <see cref="ToastButton.Tag"/>.
/// </summary>
public interface IToastService
{
    Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout);
}
```

- [ ] **Step 5: Implement `src/Winpepper.Core/Notifications/FakeToastService.cs`**

```csharp
namespace Winpepper.Core.Notifications;

public sealed class FakeToastService : IToastService
{
    public sealed record Call(string Title, string Body, ToastButton[] Buttons);

    public List<Call> Calls { get; } = new();
    private string _next = "";

    public void AutoSelect(string tag) => _next = tag;

    public async Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout)
    {
        Calls.Add(new Call(title, body, buttons.ToArray()));
        if (string.IsNullOrEmpty(_next))
        {
            // simulate timeout
            await Task.Delay(timeout < TimeSpan.FromMilliseconds(50) ? timeout : TimeSpan.FromMilliseconds(50));
            return "";
        }
        return _next;
    }
}
```

- [ ] **Step 6: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~FakeToastServiceTests"
```

Expected: 3 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.Core/Notifications tests/Winpepper.Core.Tests/Notifications
git commit -m "feat(notifications): IToastService abstraction + fake"
```

---

## Task 13: `ToastPostPasteToastPrompt` adapter

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Learning/ToastPostPasteToastPrompt.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Learning/ToastPostPasteToastPromptTests.cs`

Bridges `IPostPasteToastPrompt` (Task 6) to `IToastService` (Task 12). Per spec §8.2 (5)–(6): three buttons (Yes / Preferred / No), 8 s timeout, timeout treated as No.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Learning/ToastPostPasteToastPromptTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Learning;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class ToastPostPasteToastPromptTests
{
    [Fact]
    public async Task Yes_Tag_Maps_To_PostPasteDecisionYes()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("yes");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.Yes);
    }

    [Fact]
    public async Task Preferred_Tag_Maps_To_PostPasteDecisionPreferred()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("preferred");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.Preferred);
    }

    [Fact]
    public async Task No_Tag_Maps_To_PostPasteDecisionNo()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.No);
    }

    [Fact]
    public async Task Timeout_Returns_No()
    {
        var fake = new FakeToastService();
        fake.AutoSelect(""); // simulate timeout
        var p = new ToastPostPasteToastPrompt(fake);
        var r = await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        r.ShouldBe(PostPasteDecision.No);
    }

    [Fact]
    public async Task Body_Includes_Wrong_And_Right_Strings()
    {
        var fake = new FakeToastService();
        fake.AutoSelect("no");
        var p = new ToastPostPasteToastPrompt(fake);
        await p.AskAsync(new LearningCandidate("chat gbt", "ChatGPT"), CancellationToken.None);
        fake.Calls[0].Body.ShouldContain("chat gbt");
        fake.Calls[0].Body.ShouldContain("ChatGPT");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ToastPostPasteToastPromptTests"
```

Expected: build fails — `ToastPostPasteToastPrompt` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Learning/ToastPostPasteToastPrompt.cs`**

```csharp
using Winpepper.Core.Notifications;

namespace Winpepper.Core.Learning;

/// <summary>
/// Renders the post-paste learning toast via <see cref="IToastService"/> and
/// maps the chosen tag back to a <see cref="PostPasteDecision"/>. Spec §8.2 (5)–(6).
/// </summary>
public sealed class ToastPostPasteToastPrompt : IPostPasteToastPrompt
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    private readonly IToastService _toasts;

    public ToastPostPasteToastPrompt(IToastService toasts)
    {
        _toasts = toasts ?? throw new ArgumentNullException(nameof(toasts));
    }

    public async Task<PostPasteDecision> AskAsync(LearningCandidate c, CancellationToken ct)
    {
        var body = $"Learn correction: `{c.Wrong}` -> `{c.Right}`?";
        var buttons = new[]
        {
            new ToastButton("yes", "Yes"),
            new ToastButton("preferred", "Preferred"),
            new ToastButton("no", "No"),
        };
        var chosen = await _toasts.ShowAsync("Winpepper", body, buttons, Timeout).ConfigureAwait(false);
        return chosen switch
        {
            "yes" => PostPasteDecision.Yes,
            "preferred" => PostPasteDecision.Preferred,
            _ => PostPasteDecision.No,
        };
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ToastPostPasteToastPromptTests"
```

Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Learning/ToastPostPasteToastPrompt.cs tests/Winpepper.Core.Tests/Learning/ToastPostPasteToastPromptTests.cs
git commit -m "feat(learning): ToastPostPasteToastPrompt adapter"
```

---

## Task 14: WinUI `AppNotificationToastService`

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Notifications/AppNotificationToastService.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Winpepper.App.csproj`

> **WinUI build carry-forward:** This task lives inside `Winpepper.App`. The XAML-compiler PNSE that Plans 3 and 4 hit still applies. Write the code as specified, run the build, observe the failure, commit, move on.

WinAppSDK ships `Microsoft.Windows.AppNotifications.AppNotificationManager`. The toast XML uses the standard `<toast>` schema with `<actions>` and `<action arguments=...>` so the user's choice round-trips back as the tag we asked for.

- [ ] **Step 1: Ensure the AppNotifications namespace is reachable**

`Microsoft.WindowsAppSDK` (already in `Winpepper.App.csproj`) ships `Microsoft.Windows.AppNotifications` in its WinRT projection. No new package reference is needed.

- [ ] **Step 2: Implement `src/Winpepper.App/Notifications/AppNotificationToastService.cs`**

```csharp
#if WINDOWS
using System.Collections.Concurrent;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Winpepper.Core.Notifications;

namespace Winpepper.App.Notifications;

/// <summary>
/// Production <see cref="IToastService"/> backed by WinAppSDK's
/// <c>AppNotificationManager</c>. Each toast carries a unique id; the manager's
/// <c>NotificationInvoked</c> event resolves the matching pending task.
/// </summary>
public sealed class AppNotificationToastService : IToastService, IDisposable
{
    private readonly AppNotificationManager _mgr;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public AppNotificationToastService()
    {
        _mgr = AppNotificationManager.Default;
        _mgr.NotificationInvoked += OnInvoked;
        _mgr.Register();
    }

    public Task<string> ShowAsync(string title, string body, IReadOnlyList<ToastButton> buttons, TimeSpan timeout)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var builder = new AppNotificationBuilder()
            .AddText(title)
            .AddText(body);
        foreach (var btn in buttons)
        {
            builder.AddButton(new AppNotificationButton(btn.Label)
                .AddArgument("toastId", id)
                .AddArgument("tag", btn.Tag));
        }
        builder.AddArgument("toastId", id);
        _mgr.Show(builder.BuildNotification());

        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (_pending.TryRemove(id, out var stale)) stale.TrySetResult("");
        });

        return tcs.Task;
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        if (!args.Arguments.TryGetValue("toastId", out var id)) return;
        if (!_pending.TryRemove(id, out var tcs)) return;
        args.Arguments.TryGetValue("tag", out var tag);
        tcs.TrySetResult(tag ?? "");
    }

    public void Dispose()
    {
        _mgr.NotificationInvoked -= OnInvoked;
        _mgr.Unregister();
    }
}
#endif
```

- [ ] **Step 3: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected (per carry-forward block): WinAppSDK XAML markup-compiler PNSE. Commit anyway.

- [ ] **Step 4: Commit**

```bash
git add src/Winpepper.App/Notifications
git commit -m "feat(app): WinAppSDK-backed AppNotificationToastService"
```

---

## Task 15: Wire `ErrorBus` through `PipelineHost`

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.Core/ViewModels/SessionViewModel.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorBusTests.cs`

> **WinUI build carry-forward:** `PipelineHost` and `AppShell` are Windows-only. Build to verify the diff isn't a typo and commit as-written.

Goals:

- Every pipeline `catch` block calls `ErrorBus.Report(stage, ex, sessionId)`. The bus is constructed once in `AppShell.BootstrapAsync` and handed to `PipelineHost`.
- `SessionViewModel` subscribes to the bus and updates its `LastErrorStage` + `LastErrorMessage` so the tray icon's tooltip can carry the error summary (spec §7.1: "Error — yellow triangle; tooltip carries error summary").

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorBusTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.ViewModels;

public class SessionViewModelErrorBusTests
{
    [Fact]
    public void Vm_Updates_LastError_When_ErrorBus_Reports()
    {
        var bus = new ErrorBus();
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.AttachErrorBus(bus);

        bus.Report(ErrorStage.Audio, new InvalidOperationException("mic gone"), Guid.NewGuid());

        vm.LastErrorStage.ShouldBe(ErrorStage.Audio);
        vm.LastErrorMessage.ShouldBe("mic gone");
    }

    [Fact]
    public void Vm_Sets_Stage_To_Error_On_Bus_Report()
    {
        var bus = new ErrorBus();
        var engine = new SessionEngine();
        var vm = new SessionViewModel(engine, new SynchronousUiThread());
        vm.AttachErrorBus(bus);

        bus.Report(ErrorStage.Asr, new InvalidOperationException("load fail"), Guid.NewGuid());

        vm.Stage.ShouldBe(SessionStage.Error);
        vm.StatusText.ShouldContain("load fail");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SessionViewModelErrorBusTests"
```

Expected: build fails — `SessionViewModel.AttachErrorBus`, `LastErrorStage`, `LastErrorMessage` not found.

- [ ] **Step 3: Extend `src/Winpepper.Core/ViewModels/SessionViewModel.cs`**

Add new fields, a public attach method, and properties. Touch only the additive surface — keep existing methods intact. Replace the file body with:

```csharp
using System.ComponentModel;
using System.Diagnostics;
using Winpepper.Core.Errors;
using Winpepper.Core.Sessions;
using Winpepper.Core.Threading;

namespace Winpepper.Core.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged
{
    private readonly IUiThread _ui;
    private readonly SessionEngine _engine;
    private readonly Stopwatch _stopwatch = new();
    private SessionStage _stage = SessionStage.Idle;
    private string _statusText = "Ready";
    private long _elapsedMs;
    private ErrorStage? _lastErrorStage;
    private string _lastErrorMessage = "";
    private IDisposable? _busSub;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SessionViewModel(SessionEngine engine, IUiThread ui)
    {
        _engine = engine;
        _ui = ui;
        _engine.StateChanged += OnEngineStateChanged;
    }

    public SessionStage Stage
    {
        get => _stage;
        private set { if (_stage == value) return; _stage = value; Raise(nameof(Stage)); Raise(nameof(StatusText)); }
    }

    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText == value) return; _statusText = value; Raise(nameof(StatusText)); }
    }

    public long ElapsedMs
    {
        get => _elapsedMs;
        private set { if (_elapsedMs == value) return; _elapsedMs = value; Raise(nameof(ElapsedMs)); }
    }

    public ErrorStage? LastErrorStage
    {
        get => _lastErrorStage;
        private set { if (_lastErrorStage == value) return; _lastErrorStage = value; Raise(nameof(LastErrorStage)); }
    }

    public string LastErrorMessage
    {
        get => _lastErrorMessage;
        private set { if (_lastErrorMessage == value) return; _lastErrorMessage = value; Raise(nameof(LastErrorMessage)); }
    }

    public void AttachErrorBus(ErrorBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _busSub?.Dispose();
        _busSub = bus.Subscribe(OnBusReport);
    }

    private void OnBusReport(ErrorRecord rec) => _ui.Post(() =>
    {
        LastErrorStage = rec.Stage;
        LastErrorMessage = rec.Message;
        Stage = SessionStage.Error;
        StatusText = $"Error ({rec.Stage}): {rec.Message}";
    });

    public void MarkCleaningUp() => _ui.Post(() =>
    {
        Stage = SessionStage.CleaningUp;
        StatusText = "Cleaning up...";
    });

    public void NotifyError(string message) => _ui.Post(() =>
    {
        Stage = SessionStage.Error;
        StatusText = $"Error: {message}";
    });

    public void Tick() => _ui.Post(() =>
    {
        if (_stopwatch.IsRunning) ElapsedMs = _stopwatch.ElapsedMilliseconds;
    });

    private void OnEngineStateChanged(SessionState from, SessionState to)
    {
        _ui.Post(() =>
        {
            switch (to)
            {
                case SessionState.Recording:
                    _stopwatch.Restart();
                    Stage = SessionStage.Recording;
                    StatusText = "Recording...";
                    break;
                case SessionState.Transcribing:
                    Stage = SessionStage.Transcribing;
                    StatusText = "Transcribing...";
                    break;
                case SessionState.Injecting:
                    Stage = SessionStage.Injecting;
                    StatusText = "Inserting...";
                    break;
                case SessionState.Idle:
                    _stopwatch.Stop();
                    Stage = SessionStage.Idle;
                    StatusText = "Ready";
                    break;
            }
        });
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _busSub?.Dispose();
        _engine.StateChanged -= OnEngineStateChanged;
    }
}
```

- [ ] **Step 4: Reference `Winpepper.Core.Errors` from `SessionViewModel`** (already added in Step 3's `using Winpepper.Core.Errors;`).

- [ ] **Step 5: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~SessionViewModelErrorBusTests"
```

Expected: 2 tests pass. Re-run the full Core suite to confirm existing `SessionViewModelTests` still pass:

```bash
dotnet test tests/Winpepper.Core.Tests
```

Expected: full Core suite green.

- [ ] **Step 6: Add `ErrorBus` field to `PipelineHost`**

Edit `src/Winpepper.App/Hosting/PipelineHost.cs`. Add at the top of the field block:

```csharp
private readonly Winpepper.Core.Errors.ErrorBus _errorBus;
private Guid _currentSessionId = Guid.Empty;
```

Add `ErrorBus errorBus` as the second positional ctor arg (right after `ILoggerFactory factory`):

```csharp
public PipelineHost(
    ILoggerFactory factory,
    Winpepper.Core.Errors.ErrorBus errorBus,
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
    _errorBus = errorBus;
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

- [ ] **Step 7: Stamp session ids on each `HotkeyDown` / `Toggle` start and route catches through ErrorBus**

In `PipelineHost.HandleHotkey`, replace the outer try/catch in `RunAsync` and add bus reports at every existing `catch`:

```csharp
private async Task RunAsync(CancellationToken ct)
{
    try
    {
        await foreach (var evt in _hook.Events.ReadAllAsync(ct))
        {
            try { await HandleHotkey(evt, ct); }
            catch (Exception ex)
            {
                _log.LogError(ex, "pipeline error");
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Unknown, ex, _currentSessionId);
                _engine.Apply(SessionEvent.Failed);
                _vm.NotifyError(ex.Message);
            }
        }
    }
    catch (OperationCanceledException) { }
}
```

In each `HoldDown` and `Toggle` start branch, add immediately after `_engine.Apply(SessionEvent.StartRequested);`:

```csharp
_currentSessionId = Guid.NewGuid();
```

In every existing `catch (Exception ex)` inside the cleanup invocation, replace the `_log.LogWarning(ex, "cleanup failed; ...")` line with:

```csharp
_log.LogWarning(ex, "cleanup failed; falling back to raw transcript");
_errorBus.Report(Winpepper.Core.Errors.ErrorStage.Cleanup, ex, _currentSessionId);
```

For the injection path, replace:

```csharp
if (!string.IsNullOrWhiteSpace(final)) _injector.TryInject(final);
```

with:

```csharp
if (!string.IsNullOrWhiteSpace(final))
{
    var injected = _injector.TryInject(final);
    if (!injected)
    {
        _errorBus.Report(
            Winpepper.Core.Errors.ErrorStage.Injection,
            new InvalidOperationException("SendInput refused; clipboard fallback engaged"),
            _currentSessionId);
        // Plan 5 Task 16 fills in the clipboard-fallback path.
    }
}
```

Apply the same change in the Toggle branch (replace `final2` block analogously).

- [ ] **Step 8: Wire `ErrorBus` in `AppShell.BootstrapAsync`**

In `src/Winpepper.App/Hosting/AppShell.cs`, just after the `var engine = new SessionEngine();` line, add:

```csharp
var errorBus = new Winpepper.Core.Errors.ErrorBus();
sessionVm.AttachErrorBus(errorBus);
```

Pass `errorBus` to `new PipelineHost(...)` as the second positional arg:

```csharp
var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                historyServices.Archiver, settings.AsrModelName, cleanupModelName,
                                cleanup, correctionStore, windowContext, cleanupOptions);
```

Hold `errorBus` as a public read-only property on `AppShell` so the Diagnostics page can subscribe:

```csharp
public Winpepper.Core.Errors.ErrorBus ErrorBus { get; }
```

Assign it in the `AppShell` private ctor (add `Winpepper.Core.Errors.ErrorBus errorBus` between `SessionViewModel sessionVm,` and `RecordingSettingsViewModel recVm,`), and update the `BootstrapAsync` call site to pass it.

- [ ] **Step 9: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Linux test runs of `Winpepper.Core.Tests` still pass.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Core/ViewModels/SessionViewModel.cs src/Winpepper.App/Hosting/PipelineHost.cs src/Winpepper.App/Hosting/AppShell.cs tests/Winpepper.Core.Tests/ViewModels/SessionViewModelErrorBusTests.cs
git commit -m "feat(errors): route pipeline failures through ErrorBus"
```

---

## Task 16: Clipboard fallback + clipboard toast

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Platform/Injection/ClipboardFallback.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Platform.Tests/Injection/ClipboardFallbackTests.cs`

> **WinUI build carry-forward:** `PipelineHost` edits still hit the WinUI compiler. Commit as-written.

Spec §5.6: "If injection fails (e.g., foreground window is a secure prompt that blocks synthetic input), the cleaned text is copied to the clipboard and a toast says so."

`ClipboardFallback` is the side-effect layer (sets clipboard + records that the call happened). The pure-logic seam — `SetClipboardText(string)` — is delegated through an `IClipboard` so unit tests run on Linux. The Windows-only impl uses `Windows.ApplicationModel.DataTransfer.Clipboard` via WinRT projection from `Winpepper.App`, but the wiring lives in `Winpepper.Platform` so `PipelineHost` doesn't depend on `Microsoft.WindowsAppSDK` for this.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Platform.Tests/Injection/ClipboardFallbackTests.cs`**

```csharp
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class ClipboardFallbackTests
{
    [Fact]
    public void Copy_Calls_Clipboard_With_Exact_String()
    {
        var clip = new FakeClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("hello world");
        clip.LastSetText.ShouldBe("hello world");
    }

    [Fact]
    public void Copy_Empty_String_Is_NoOp()
    {
        var clip = new FakeClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("");
        clip.LastSetText.ShouldBeNull();
    }

    [Fact]
    public void Copy_Wraps_Exceptions_And_Returns_False()
    {
        var clip = new ThrowingClipboard();
        var fb = new ClipboardFallback(clip);
        fb.Copy("x").ShouldBeFalse();
    }

    private sealed class FakeClipboard : IClipboard
    {
        public string? LastSetText { get; private set; }
        public bool SetText(string text) { LastSetText = text; return true; }
    }

    private sealed class ThrowingClipboard : IClipboard
    {
        public bool SetText(string text) => throw new InvalidOperationException("denied");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~ClipboardFallbackTests"
```

Expected: build fails — `ClipboardFallback`, `IClipboard` not found.

- [ ] **Step 3: Implement `src/Winpepper.Platform/Injection/ClipboardFallback.cs`**

```csharp
namespace Winpepper.Platform.Injection;

/// <summary>
/// Cross-platform clipboard seam. Production Windows impl lives in
/// <c>Winpepper.App.Hosting.WindowsClipboard</c>.
/// </summary>
public interface IClipboard
{
    /// <summary>Returns true on success.</summary>
    bool SetText(string text);
}

/// <summary>
/// Spec §5.6 fallback: when <see cref="TextInjector.TryInject"/> fails, write
/// the text to the clipboard. A toast announcing it is fired separately by
/// <c>PipelineHost</c> (so this class stays test-friendly).
/// </summary>
public sealed class ClipboardFallback
{
    private readonly IClipboard _clip;

    public ClipboardFallback(IClipboard clip)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
    }

    public bool Copy(string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        try { return _clip.SetText(text); }
        catch { return false; }
    }
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Platform.Tests --filter "FullyQualifiedName~ClipboardFallbackTests"
```

Expected: 3 tests pass.

- [ ] **Step 5: Implement the Windows clipboard adapter `src/Winpepper.App/Hosting/WindowsClipboard.cs`**

```csharp
#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Winpepper.Platform.Injection;

namespace Winpepper.App.Hosting;

public sealed class WindowsClipboard : IClipboard
{
    public bool SetText(string text)
    {
        var pkg = new DataPackage();
        pkg.SetText(text);
        Clipboard.SetContent(pkg);
        return true;
    }
}
#endif
```

- [ ] **Step 6: Wire `ClipboardFallback` + clipboard toast into `PipelineHost`**

Add a constructor parameter `ClipboardFallback clipboardFallback` and `IToastService toasts` (both required). Stash them as fields.

When injection refuses (Task 15 Step 7 currently reports an Injection-stage error), replace the comment line with:

```csharp
clipboardFallback.Copy(final);
_ = toasts.ShowAsync(
    "Winpepper",
    "Couldn't type into the active window. The cleaned text is on your clipboard.",
    Array.Empty<Winpepper.Core.Notifications.ToastButton>(),
    TimeSpan.FromSeconds(6));
```

Apply the same change in the Toggle branch (use `final2`).

- [ ] **Step 7: Wire `ClipboardFallback` + `IToastService` in `AppShell.BootstrapAsync`**

Just before the `new PipelineHost(...)` call, add:

```csharp
var clipboard = new Winpepper.App.Hosting.WindowsClipboard();
var clipboardFallback = new Winpepper.Platform.Injection.ClipboardFallback(clipboard);
var toasts = new Winpepper.App.Notifications.AppNotificationToastService();
```

Hold them as `AppShell` properties so other consumers (Task 17) can reuse:

```csharp
public Winpepper.Core.Notifications.IToastService Toasts { get; }
public Winpepper.Platform.Injection.ClipboardFallback ClipboardFallback { get; }
```

Pass both to `PipelineHost`:

```csharp
var pipeline = new PipelineHost(factory, errorBus, engine, sessionVm, sounds,
                                hold, toggle, cancel, AppPaths.ParakeetModelDir,
                                historyServices.Archiver, settings.AsrModelName, cleanupModelName,
                                clipboardFallback, toasts,
                                cleanup, correctionStore, windowContext, cleanupOptions);
```

Update the `PipelineHost` ctor signature to match — the optional parameters from Plan 4 stay at the end, the new required `clipboardFallback` and `toasts` go right after `cleanupModelName`:

```csharp
public PipelineHost(
    ILoggerFactory factory,
    Winpepper.Core.Errors.ErrorBus errorBus,
    SessionEngine engine,
    SessionViewModel vm,
    ISoundEffectPlayer sounds,
    HotkeyChord hold, HotkeyChord toggle, HotkeyChord cancel,
    string modelDir,
    Winpepper.History.HistoryArchiver archiver,
    string asrModelName,
    string cleanupModelName,
    Winpepper.Platform.Injection.ClipboardFallback clipboardFallback,
    Winpepper.Core.Notifications.IToastService toasts,
    Winpepper.Cleanup.CleanupRunner? cleanup = null,
    Winpepper.Corrections.CorrectionStore? corrections = null,
    Winpepper.Platform.WindowContext.WindowContextPrefetch? windowContext = null,
    Winpepper.Cleanup.CleanupOptions? cleanupOptions = null)
```

Stash as fields:

```csharp
private readonly Winpepper.Platform.Injection.ClipboardFallback _clipboardFallback;
private readonly Winpepper.Core.Notifications.IToastService _toasts;
```

- [ ] **Step 8: Dispose `AppNotificationToastService` in `AppShell.Dispose`**

Append to the existing `Dispose` body:

```csharp
(Toasts as IDisposable)?.Dispose();
```

- [ ] **Step 9: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Commit as-written.

- [ ] **Step 10: Commit**

```bash
git add src/Winpepper.Platform/Injection/ClipboardFallback.cs src/Winpepper.App/Hosting/WindowsClipboard.cs src/Winpepper.App/Hosting/PipelineHost.cs src/Winpepper.App/Hosting/AppShell.cs tests/Winpepper.Platform.Tests/Injection
git commit -m "feat(injection): clipboard fallback with toast when SendInput refuses"
```

---

## Task 17: Wire `PostPasteWatcher` into `PipelineHost`

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/PipelineHost.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`

> **WinUI build carry-forward.**

After a successful injection (the `_engine.Apply(SessionEvent.InjectionCompleted);` line in both `HoldUp` and `Toggle-stop` branches), capture the focused element and fire-and-forget `PostPasteWatcher.BeginAsync(...)`.

- [ ] **Step 1: Add fields to `PipelineHost`**

```csharp
private readonly Winpepper.Core.Learning.PostPasteWatcher? _postPaste;
private readonly Winpepper.Platform.Learning.FocusedElementCapturer? _focusedCapturer;
```

- [ ] **Step 2: Add optional ctor parameters**

```csharp
Winpepper.Core.Learning.PostPasteWatcher? postPaste = null,
Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null
```

Add at the end of the optional-param list. Assign:

```csharp
_postPaste = postPaste;
_focusedCapturer = focusedCapturer;
```

- [ ] **Step 3: Capture + start the watcher after injection completes**

Inside both `HoldUp` and `Toggle-stop` branches, right *before* `_engine.Apply(SessionEvent.InjectionCompleted);`, add:

```csharp
if (_postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final))
{
    var snap = _focusedCapturer.Capture();
    if (snap.IsValid)
    {
        _ = _postPaste.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
        {
            ElementId = snap.ElementId,
            InjectedText = final,
            SessionId = _currentSessionId,
            InjectionEndUtc = DateTime.UtcNow,
        });
    }
}
```

Apply the same edit in the Toggle branch (use `final2`).

- [ ] **Step 4: Construct `PostPasteWatcher` + `FocusedElementCapturer` in `AppShell.BootstrapAsync`**

Right after the `correctionStore` block:

```csharp
Winpepper.Core.Learning.PostPasteWatcher? postPaste = null;
Winpepper.Platform.Learning.FocusedElementCapturer? focusedCapturer = null;
try
{
    var uiaWatcher = new Winpepper.Platform.Learning.UiaFocusedElementTextWatcher(
        factory.CreateLogger<Winpepper.Platform.Learning.UiaFocusedElementTextWatcher>());
    focusedCapturer = new Winpepper.Platform.Learning.FocusedElementCapturer(
        uiaWatcher,
        factory.CreateLogger<Winpepper.Platform.Learning.FocusedElementCapturer>());
    if (correctionStore is not null)
    {
        var writer = new Winpepper.Corrections.CorrectionStoreWriter(correctionStore);
        var prompt = new Winpepper.Core.Learning.ToastPostPasteToastPrompt(toasts);
        postPaste = new Winpepper.Core.Learning.PostPasteWatcher(uiaWatcher, writer, prompt);
    }
}
catch (Exception ex)
{
    factory.CreateLogger("Winpepper.App").LogWarning(ex,
        "PostPasteWatcher unavailable; post-paste learning will be disabled.");
}
```

Pass them through `new PipelineHost(..., postPaste: postPaste, focusedCapturer: focusedCapturer)`.

- [ ] **Step 5: Report PostPaste-stage failures into the ErrorBus**

Wrap the `_postPaste.BeginAsync(...)` call with continuation-on-faulted that funnels exceptions into the bus:

```csharp
if (_postPaste is not null && _focusedCapturer is not null && !string.IsNullOrWhiteSpace(final))
{
    var snap = _focusedCapturer.Capture();
    if (snap.IsValid)
    {
        var watchTask = _postPaste.BeginAsync(new Winpepper.Core.Learning.PostPasteContext
        {
            ElementId = snap.ElementId,
            InjectedText = final,
            SessionId = _currentSessionId,
            InjectionEndUtc = DateTime.UtcNow,
        });
        var sid = _currentSessionId;
        _ = watchTask.ContinueWith(t =>
        {
            if (t.Exception is not null)
                _errorBus.Report(Winpepper.Core.Errors.ErrorStage.Learning,
                                  t.Exception.GetBaseException(), sid);
        }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
```

- [ ] **Step 6: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Commit as-written.

- [ ] **Step 7: Commit**

```bash
git add src/Winpepper.App/Hosting
git commit -m "feat(learning): fire PostPasteWatcher after every successful injection"
```

---

## Task 18: Wire `CrashHandler` into `Program.Main`

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Program.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/App.xaml.cs`

> **WinUI build carry-forward.**

Spec §9.3: install global handlers as early as possible. We register them in `Program.Main` so even pre-`Application.Start` crashes are caught.

- [ ] **Step 1: Add a static crash-handler bag to `App.xaml.cs`**

So `Program.Main` (which runs before `AppShell.BootstrapAsync` builds the real instance) can hold a placeholder and later upgrade it.

```csharp
public partial class App : Application
{
    public static Winpepper.Core.Crash.CrashHandler? CrashHandler { get; set; }
    public static IServiceProvider? Services { get; set; } // (already added by Plan 4)
    // ... existing code untouched ...
}
```

(If `Services` already exists in `App.xaml.cs`, leave it; just add `CrashHandler`.)

- [ ] **Step 2: Install global handlers in `Program.Main`**

Replace the body of `Main` with:

```csharp
public static int Main(string[] args)
{
    var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
    Environment.SetEnvironmentVariable("WINPEPPER_START_HIDDEN", startHidden ? "1" : "0");

    AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
    TaskScheduler.UnobservedTaskException += OnUnobservedTask;

    var key = "Winpepper-singleton";
    var instance = AppInstance.FindOrRegisterForKey(key);
    if (!instance.IsCurrent)
    {
        var current = AppInstance.GetCurrent();
        instance.RedirectActivationToAsync(current.GetActivatedEventArgs()).AsTask().Wait();
        return 0;
    }

    WinRT.ComWrappersSupport.InitializeComWrappers();
    Application.Start((p) =>
    {
        var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
        System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
        _ = new App();
    });
    return 0;
}

private static void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
{
    if (e.ExceptionObject is not Exception ex) return;
    var keepAlive = App.CrashHandler?.HandleUnhandled(ex, fromTaskScheduler: false) ?? false;
    if (!keepAlive) Environment.Exit(1);
}

private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
{
    var keepAlive = App.CrashHandler?.HandleUnhandled(e.Exception, fromTaskScheduler: true) ?? false;
    e.SetObserved();
    if (!keepAlive) Environment.Exit(1);
}
```

- [ ] **Step 3: Build the `CrashHandler` in `AppShell.BootstrapAsync` and publish it**

After `errorBus` is constructed and before `new PipelineHost(...)`:

```csharp
var crashDir = Path.Combine(AppPaths.Root, "crashes");
Directory.CreateDirectory(crashDir);
var miniDump = new Winpepper.Platform.Crash.MiniDumpWriter(crashDir,
    factory.CreateLogger<Winpepper.Platform.Crash.MiniDumpWriter>());
var crashHandler = new Winpepper.Core.Crash.CrashHandler(miniDump, errorBus, engine,
    factory.CreateLogger<Winpepper.Core.Crash.CrashHandler>());
App.CrashHandler = crashHandler;
```

Add a public read-only property on `AppShell`:

```csharp
public Winpepper.Core.Crash.CrashHandler CrashHandler { get; }
```

Assign through the private ctor.

- [ ] **Step 4: Extend `AppPaths` with `CrashesDir`**

`src/Winpepper.App/Hosting/AppPaths.cs` — add:

```csharp
public static string CrashesDir => Path.Combine(Root, "crashes");
```

Use `AppPaths.CrashesDir` instead of the inline `Path.Combine` from Step 3.

- [ ] **Step 5: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Commit as-written.

- [ ] **Step 6: Commit**

```bash
git add src/Winpepper.App/Program.cs src/Winpepper.App/App.xaml.cs src/Winpepper.App/Hosting/AppShell.cs src/Winpepper.App/Hosting/AppPaths.cs
git commit -m "feat(crash): register global handlers + wire MiniDumpWriter"
```

---

## Task 19: Tray icon Error state surfaces last error

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayIconHost.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayIconStateMapper.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs`

> **WinUI build carry-forward.** The Linux test in this task is for the pure-C# mapper.

Spec §7.1: tray Error state shows "yellow triangle; tooltip carries error summary". Plan 3 wired tray icon swaps from `SessionStage`; Plan 5 extends that to surface `LastErrorMessage` in the tooltip and (in `TrayIconStateMapper`) chooses which icon resource to load.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs`**

```csharp
using Shouldly;
using Winpepper.App.Tray; // mapper lives in Winpepper.App because both icons + tooltips do
using Winpepper.Core.ViewModels;
using Xunit;

namespace Winpepper.Core.Tests.Tray;

public class TrayIconStateMapperTests
{
    [Fact]
    public void Idle_Returns_Ready_Resources()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Idle, lastErrorMessage: null, paused: false);
        r.IconName.ShouldBe("AppIcon.ico");
        r.Tooltip.ShouldBe("Winpepper - Ready");
    }

    [Fact]
    public void Recording_Returns_Recording_Resources()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, false);
        r.IconName.ShouldBe("AppIcon-Recording.ico");
        r.Tooltip.ShouldBe("Winpepper - Recording...");
    }

    [Fact]
    public void Error_Returns_Error_Icon_And_Includes_Message_In_Tooltip()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Error, "Mic unavailable", false);
        r.IconName.ShouldBe("AppIcon-Error.ico");
        r.Tooltip.ShouldContain("Mic unavailable");
    }

    [Fact]
    public void Paused_Overrides_Stage()
    {
        var r = TrayIconStateMapper.Map(SessionStage.Recording, null, paused: true);
        r.Tooltip.ShouldBe("Winpepper - Paused");
        r.IconName.ShouldBe("AppIcon.ico");
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~TrayIconStateMapperTests"
```

Expected: build fails — `TrayIconStateMapper` not found.

For the test to find the type without `Winpepper.Core.Tests` depending on `Winpepper.App` (which is Windows-only), put `TrayIconStateMapper` in a **new** small assembly: `Winpepper.Core.ViewModels`. Hold on — the tray mapping is purely about names and labels, so it can live in `Winpepper.Core` directly:

- [ ] **Step 3: Move the test to `Winpepper.Core` and implement the mapper there**

Rename the file to `tests/Winpepper.Core.Tests/Tray/TrayIconStateMapperTests.cs` and update the `using` to `using Winpepper.Core.Tray;`. Implement `src/Winpepper.Core/Tray/TrayIconStateMapper.cs`:

```csharp
using Winpepper.Core.ViewModels;

namespace Winpepper.Core.Tray;

public sealed record TrayIconState(string IconName, string Tooltip);

public static class TrayIconStateMapper
{
    public static TrayIconState Map(SessionStage stage, string? lastErrorMessage, bool paused)
    {
        if (paused) return new TrayIconState("AppIcon.ico", "Winpepper - Paused");

        return stage switch
        {
            SessionStage.Recording   => new("AppIcon-Recording.ico", "Winpepper - Recording..."),
            SessionStage.Transcribing => new("AppIcon-Loading.ico",  "Winpepper - Transcribing..."),
            SessionStage.CleaningUp  => new("AppIcon-Loading.ico",   "Winpepper - Cleaning up..."),
            SessionStage.Injecting   => new("AppIcon-Loading.ico",   "Winpepper - Inserting..."),
            SessionStage.Error       => new("AppIcon-Error.ico",     $"Winpepper - Error: {lastErrorMessage ?? "see Diagnostics"}"),
            _                        => new("AppIcon.ico",           "Winpepper - Ready"),
        };
    }
}
```

Update the test `using` to `using Winpepper.Core.Tray;`.

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~TrayIconStateMapperTests"
```

Expected: 4 tests pass.

- [ ] **Step 5: Use the mapper in `src/Winpepper.App/Tray/TrayIconHost.cs`**

Replace the body of `UpdateFromSession` with:

```csharp
private void UpdateFromSession()
{
    var state = Winpepper.Core.Tray.TrayIconStateMapper.Map(
        _session.Stage, _session.LastErrorMessage, _paused);
    var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", state.IconName);
    if (File.Exists(iconPath))
        _icon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
    _menu.StatusItemControl.Text = _paused ? "Paused" : _session.StatusText;
    _icon.ToolTipText = state.Tooltip;
    _menu.StatusProgressBar.Visibility =
        !_paused && _session.Stage is SessionStage.Recording or SessionStage.Transcribing or SessionStage.CleaningUp
            ? Visibility.Visible : Visibility.Collapsed;
}
```

Also extend the `OnSessionChanged` filter so the tray updates when `LastErrorMessage` changes:

```csharp
private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
{
    if (e.PropertyName is nameof(SessionViewModel.Stage)
                       or nameof(SessionViewModel.StatusText)
                       or nameof(SessionViewModel.LastErrorMessage))
        UpdateFromSession();
}
```

- [ ] **Step 6: Add the new icon assets (placeholders OK)**

`scripts/make-placeholder-icon.ps1` produces the four icons. Run on the VM:

```bash
./scripts/winrun "powershell -ExecutionPolicy Bypass -File scripts/make-placeholder-icon.ps1 -Out src\\Winpepper.App\\Assets -Names AppIcon,AppIcon-Recording,AppIcon-Loading,AppIcon-Error"
```

If the existing script doesn't accept a `-Names` parameter, edit it to loop over the supplied names. Plan 3 ships a single `AppIcon.ico`; this task introduces the three additional variants.

Include them in `Winpepper.App.csproj`:

```xml
<Content Include="Assets\AppIcon-Recording.ico" CopyToOutputDirectory="PreserveNewest" />
<Content Include="Assets\AppIcon-Loading.ico"   CopyToOutputDirectory="PreserveNewest" />
<Content Include="Assets\AppIcon-Error.ico"     CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 7: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Commit as-written.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.Core/Tray src/Winpepper.App/Tray src/Winpepper.App/Assets/*.ico src/Winpepper.App/Winpepper.App.csproj tests/Winpepper.Core.Tests/Tray
git commit -m "feat(tray): icon state mapper with Error tooltip surface"
```

---

## Task 20: Per-stage error deep links

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.Core/Errors/ErrorDeepLink.cs`
- Create: `/home/jesse/git/winpepper/tests/Winpepper.Core.Tests/Errors/ErrorDeepLinkTests.cs`

Spec §9.1 table: "Mic unavailable" → device picker; "Model load fails" → Models tab. A pure-C# helper maps `ErrorStage` to a `NavigationTarget` string. `MainWindow` (Task 21) reads this when the user clicks the error toast.

- [ ] **Step 1: Write the failing test `tests/Winpepper.Core.Tests/Errors/ErrorDeepLinkTests.cs`**

```csharp
using Shouldly;
using Winpepper.Core.Errors;
using Xunit;

namespace Winpepper.Core.Tests.Errors;

public class ErrorDeepLinkTests
{
    [Theory]
    [InlineData(ErrorStage.Audio,     "recording")]
    [InlineData(ErrorStage.Asr,       "models")]
    [InlineData(ErrorStage.Cleanup,   "cleanup")]
    [InlineData(ErrorStage.OcrUia,    "cleanup")]
    [InlineData(ErrorStage.Injection, "diagnostics")]
    [InlineData(ErrorStage.Learning,  "corrections")]
    [InlineData(ErrorStage.Models,    "models")]
    [InlineData(ErrorStage.History,   "history")]
    [InlineData(ErrorStage.Settings,  "recording")]
    [InlineData(ErrorStage.Hotkey,    "recording")]
    [InlineData(ErrorStage.Crash,     "diagnostics")]
    [InlineData(ErrorStage.Unknown,   "diagnostics")]
    public void Map_Returns_Nav_Tag_For_Each_Stage(ErrorStage stage, string expected)
    {
        ErrorDeepLink.NavigationTagFor(stage).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ErrorStage.Audio,     "Open Recording settings")]
    [InlineData(ErrorStage.Asr,       "Open Models tab")]
    [InlineData(ErrorStage.Cleanup,   "Open Cleanup settings")]
    [InlineData(ErrorStage.Injection, "Open Diagnostics")]
    public void Action_Label_Reads_For_Humans(ErrorStage stage, string expected)
    {
        ErrorDeepLink.ActionLabelFor(stage).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Verify failure**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ErrorDeepLinkTests"
```

Expected: build fails — `ErrorDeepLink` not found.

- [ ] **Step 3: Implement `src/Winpepper.Core/Errors/ErrorDeepLink.cs`**

```csharp
namespace Winpepper.Core.Errors;

/// <summary>
/// Maps an <see cref="ErrorStage"/> to a navigation tag and human label used
/// by the tray error toast's "Open X" deep-link button. Spec §9.1.
/// </summary>
public static class ErrorDeepLink
{
    public static string NavigationTagFor(ErrorStage stage) => stage switch
    {
        ErrorStage.Audio     => "recording",
        ErrorStage.Asr       => "models",
        ErrorStage.Cleanup   => "cleanup",
        ErrorStage.OcrUia    => "cleanup",
        ErrorStage.Injection => "diagnostics",
        ErrorStage.Learning  => "corrections",
        ErrorStage.Models    => "models",
        ErrorStage.History   => "history",
        ErrorStage.Settings  => "recording",
        ErrorStage.Hotkey    => "recording",
        ErrorStage.Crash     => "diagnostics",
        ErrorStage.Unknown   => "diagnostics",
        _ => "diagnostics",
    };

    public static string ActionLabelFor(ErrorStage stage) => stage switch
    {
        ErrorStage.Audio     => "Open Recording settings",
        ErrorStage.Asr       => "Open Models tab",
        ErrorStage.Cleanup   => "Open Cleanup settings",
        ErrorStage.OcrUia    => "Open Cleanup settings",
        ErrorStage.Injection => "Open Diagnostics",
        ErrorStage.Learning  => "Open Corrections",
        ErrorStage.Models    => "Open Models tab",
        ErrorStage.History   => "Open History",
        ErrorStage.Settings  => "Open Recording settings",
        ErrorStage.Hotkey    => "Open Recording settings",
        ErrorStage.Crash     => "Open Diagnostics",
        ErrorStage.Unknown   => "Open Diagnostics",
        _ => "Open Diagnostics",
    };
}
```

- [ ] **Step 4: Verify pass**

```bash
dotnet test tests/Winpepper.Core.Tests --filter "FullyQualifiedName~ErrorDeepLinkTests"
```

Expected: 16 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.Core/Errors/ErrorDeepLink.cs tests/Winpepper.Core.Tests/Errors/ErrorDeepLinkTests.cs
git commit -m "feat(errors): ErrorDeepLink maps stages to nav tags + labels"
```

---

## Task 21: Diagnostics page XAML + nav routing + error-toast deep links

**Files:**
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/DiagnosticsPage.xaml`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Views/DiagnosticsPage.xaml.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Views/MainWindow.xaml`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Views/MainWindow.xaml.cs`
- Create: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/DiagnosticsHost.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Hosting/AppShell.cs`

> **WinUI build carry-forward.**

Plan 3 ships the Diagnostics nav item disabled. Plan 5 enables and routes it, then renders the live tail.

- [ ] **Step 1: Implement `src/Winpepper.App/Hosting/DiagnosticsHost.cs`**

```csharp
#if WINDOWS
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Winpepper.Core.Diagnostics;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Hosting;

public sealed class DiagnosticsHost : IDiagnosticsHost
{
    private readonly Func<Window?> _mainWindow;
    private readonly string _logsDir;
    private readonly string _historyRoot;
    private readonly string _settingsPath;
    private readonly string _appVersion;

    public DiagnosticsHost(
        Func<Window?> mainWindow, string logsDir, string historyRoot,
        string settingsPath, string appVersion)
    {
        _mainWindow = mainWindow;
        _logsDir = logsDir;
        _historyRoot = historyRoot;
        _settingsPath = settingsPath;
        _appVersion = appVersion;
    }

    public void OpenLogFolder()
    {
        try { Process.Start(new ProcessStartInfo { FileName = _logsDir, UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    public async Task<string?> SaveBundleAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"winpepper-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
        };
        picker.FileTypeChoices.Add("Zip archive", new[] { ".zip" });
        var win = _mainWindow();
        if (win is not null)
        {
            var hwnd = WindowNative.GetWindowHandle(win);
            InitializeWithWindow.Initialize(picker, hwnd);
        }
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;

        DiagnosticsBundleBuilder.Build(new DiagnosticsBundle
        {
            LogsDir = _logsDir,
            HistoryRoot = _historyRoot,
            SettingsPath = _settingsPath,
            SysInfo = DiagnosticsSysInfo.Capture(_appVersion),
        }, file.Path);
        return file.Path;
    }
}
#endif
```

- [ ] **Step 2: Implement `src/Winpepper.App/Views/DiagnosticsPage.xaml`**

```xml
<Page x:Class="Winpepper.App.Views.DiagnosticsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:logging="using:Winpepper.Core.Logging">
    <Grid Padding="24" RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="Diagnostics" Style="{StaticResource TitleTextBlockStyle}" />

        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="12">
            <Button x:Name="OpenLogFolderBtn" Content="Open log folder" Click="OnOpenLogFolder" />
            <Button x:Name="CopyBundleBtn" Content="Copy diagnostics bundle" Click="OnCopyBundle" />
            <TextBlock x:Name="LastBundleLabel" VerticalAlignment="Center" />
        </StackPanel>

        <ListView Grid.Row="2"
                  x:Name="TailList"
                  ItemsSource="{x:Bind ViewModel.Tail, Mode=OneWay}"
                  SelectionMode="None">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="logging:LogTailEntry">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock Text="{x:Bind TimestampUtc}" Foreground="Gray" />
                        <TextBlock Text="{x:Bind Level}" />
                        <TextBlock Text="{x:Bind Message}" TextWrapping="Wrap" />
                    </StackPanel>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>

        <TextBlock Grid.Row="3"
                   Text="Tail keeps the most recent 2000 log lines. Audio is never included in the diagnostics bundle."
                   Foreground="Gray"
                   FontSize="12" />
    </Grid>
</Page>
```

- [ ] **Step 3: Implement `src/Winpepper.App/Views/DiagnosticsPage.xaml.cs`**

```csharp
#if WINDOWS
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Winpepper.App.Hosting;
using Winpepper.Core.ViewModels;

namespace Winpepper.App.Views;

public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsViewModel ViewModel { get; private set; } = null!;

    public DiagnosticsPage()
    {
        this.InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var shell = (AppShell)e.Parameter;
        ViewModel = new DiagnosticsViewModel(
            shell.LogTail,
            shell.Ui,
            shell.DiagnosticsHost);
        Bindings.Update();
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e) => ViewModel.OpenLogFolder();

    private async void OnCopyBundle(object sender, RoutedEventArgs e)
    {
        await ViewModel.CopyDiagnosticsBundleAsync();
        LastBundleLabel.Text = string.IsNullOrEmpty(ViewModel.LastBundlePath)
            ? ""
            : $"Saved: {ViewModel.LastBundlePath}";
    }
}
#endif
```

- [ ] **Step 4: Add `LogTail`, `Ui`, `DiagnosticsHost` plumbing to `AppShell`**

In `AppShell.BootstrapAsync`, replace the existing `WinpepperLogging.Create(...)` call with:

```csharp
var logTail = new Winpepper.Core.Logging.LogRingBuffer(capacity: 2000);
var factory = Winpepper.Core.Logging.WinpepperLogging.CreateWithBuffer(
    AppPaths.LogsDir, debugConsole: false,
    minimumLevel: Microsoft.Extensions.Logging.LogLevel.Information,
    buffer: logTail);
```

Hold `logTail`, the `IUiThread`, and the `DiagnosticsHost` on `AppShell`:

```csharp
public Winpepper.Core.Logging.LogRingBuffer LogTail { get; }
public Winpepper.Core.Threading.IUiThread Ui { get; }
public Winpepper.App.Hosting.DiagnosticsHost DiagnosticsHost { get; }
```

Construct the host after the main-window field exists. Use a thunk for `Func<Window?>` so the property can be set later:

```csharp
var diagHost = new Winpepper.App.Hosting.DiagnosticsHost(
    mainWindow: () => Main,
    logsDir: AppPaths.LogsDir,
    historyRoot: AppPaths.HistoryRoot,
    settingsPath: AppPaths.SettingsJson,
    appVersion: "0.5.0");
```

(Replace `"0.5.0"` with whatever the current version string is — Plan 4 introduced the version string in `TrayIconHost`.)

Pass `logTail`, `uiThread`, and `diagHost` through the private `AppShell` ctor and assign to the new properties.

- [ ] **Step 5: Enable + route the Diagnostics nav item in `MainWindow`**

Edit `src/Winpepper.App/Views/MainWindow.xaml`:

```xml
<NavigationViewItem Tag="diagnostics" Content="Diagnostics" />
```

(Drop `IsEnabled="False"` and the `ToolTipService.ToolTip` attribute.)

Edit `src/Winpepper.App/Views/MainWindow.xaml.cs`:

```csharp
var pageType = (string?)item.Tag switch
{
    "recording"   => typeof(RecordingPage),
    "cleanup"     => typeof(CleanupPage),
    "corrections" => typeof(CorrectionsPage),
    "history"     => typeof(HistoryPage),
    "lab"         => typeof(HistoryDetailPage),
    "models"      => typeof(ModelsPage),
    "diagnostics" => typeof(DiagnosticsPage),
    _ => null,
};
```

Add a public method to `MainWindow` so `App` can deep-link from a toast click:

```csharp
public void NavigateToTag(string tag)
{
    foreach (var item in Nav.MenuItems)
    {
        if (item is NavigationViewItem navItem && (string?)navItem.Tag == tag)
        {
            Nav.SelectedItem = navItem;
            return;
        }
    }
}
```

- [ ] **Step 6: Toast-on-bus-report with deep link**

In `AppShell.BootstrapAsync`, right after `sessionVm.AttachErrorBus(errorBus);`:

```csharp
errorBus.Subscribe(rec =>
{
    var tag = Winpepper.Core.Errors.ErrorDeepLink.NavigationTagFor(rec.Stage);
    var label = Winpepper.Core.Errors.ErrorDeepLink.ActionLabelFor(rec.Stage);
    _ = toasts.ShowAsync(
        "Winpepper error",
        $"{rec.Stage}: {rec.Message}",
        new[] { new Winpepper.Core.Notifications.ToastButton(tag, label) },
        TimeSpan.FromSeconds(10)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !string.IsNullOrEmpty(t.Result))
            {
                uiThread.Post(() => (Main as Views.MainWindow)?.NavigateToTag(t.Result));
                uiThread.Post(() => ShowMain());
            }
        });
});
```

- [ ] **Step 7: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"
```

Expected: WinUI markup-compiler PNSE per carry-forward.

- [ ] **Step 8: Commit**

```bash
git add src/Winpepper.App/Views/DiagnosticsPage.xaml src/Winpepper.App/Views/DiagnosticsPage.xaml.cs src/Winpepper.App/Views/MainWindow.xaml src/Winpepper.App/Views/MainWindow.xaml.cs src/Winpepper.App/Hosting/DiagnosticsHost.cs src/Winpepper.App/Hosting/AppShell.cs
git commit -m "feat(diagnostics): Diagnostics page + error-toast deep links"
```

---

## Task 22: Manual smoke procedure

**Files:**
- Modify: `/home/jesse/git/winpepper/docs/manual-test.md` — append a Plan 5 section.

Plan 5 introduces non-trivial Windows-only behaviour that the integration test suite can't drive (UIA on real apps, crash dump on real process). Manual smoke covers it.

- [ ] **Step 1: Append the following section to `docs/manual-test.md`**

```markdown
## Plan 5 — Post-paste learning, Diagnostics, error bus, crash dumps

### Setup
1. Build + deploy as in earlier plans. Confirm `dotnet test` (Linux filter) is fully green.
2. Run `./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj"`. WinUI compiler PNSE is expected (carry-forward); proceed with the previously-built binary or, once the WinUI block is resolved, with the fresh build.
3. Launch the app on the VM.

### Post-paste learning
1. Open Notepad. Focus the document.
2. Press the hold-to-record hotkey, say "send chat gbt the link", release.
3. Observe injected text "send chat gbt the link" (or similar — depending on cleanup).
4. Within 30 s, edit `chat gbt` to `ChatGPT` in Notepad.
5. A non-modal toast should appear: "Learn correction: `chat gbt` -> `ChatGPT`? [Yes / Preferred / No]".
6. Click "Yes". Open `%LOCALAPPDATA%\winpepper\corrections.json` and confirm `"chat gbt": "ChatGPT"` under `replacements`.
7. Repeat with "Preferred" — confirm `"ChatGPT"` shows up under `preferred` and `replacements` is unchanged.
8. Repeat with "No" — confirm both lists are unchanged and a second identical edit in the same session does **not** re-prompt.
9. Wait 30 s with no edits. The watch window should silently close (no error).

### Toast button compatibility
- Microsoft Edge address bar — confirm Edge's autocomplete-style edits do NOT trigger the toast.
- Word — confirm Word's autocapitalize ("anthropic" → "Anthropic") does NOT trigger the toast.

### Diagnostics tab
1. Open the main window, click "Diagnostics" in the nav.
2. Confirm the tail shows the recent log lines (at least the boot lines).
3. Trigger a session; confirm new lines appear at the bottom.
4. Click "Open log folder" — Explorer opens at `%LOCALAPPDATA%\winpepper\logs\`.
5. Click "Copy diagnostics bundle" — pick a destination, watch the zip get created.
6. Unzip the bundle; confirm: `logs/winpepper-*.log`, `history-index.json`, `settings.json`, `sysinfo.json`.
7. Confirm there are **no** `*.wav` files in the zip.

### Error bus + tray
1. Rename the parakeet model directory under `%LOCALAPPDATA%\winpepper\models\` so ASR fails.
2. Trigger a session. Confirm the tray icon flips to the yellow Error glyph, the tooltip carries "Error (Asr): ...", and a toast appears with an "Open Models tab" button.
3. Click the toast button — main window opens and selects Models. Restore the model directory.

### Clipboard fallback
1. Open Windows Security → focus a search box. (Or any UAC-protected window.)
2. Trigger a session. Confirm a toast says "Couldn't type into the active window. The cleaned text is on your clipboard."
3. Paste with Ctrl+V — the cleaned text appears.

### Crash safety
1. Open Diagnostics tab.
2. Trigger an artificial crash using the developer hotkey (Ctrl+Shift+F12 — Task 23 wires this as a debug-build-only menu item; if not built into the current binary, throw from `PipelineHost` by editing in a temporary `throw new InvalidOperationException("synthetic crash")` and rebuilding).
3. Confirm `%LOCALAPPDATA%\winpepper\crashes\winpepper-YYYYMMDD-HHMMSS-PID.dmp` exists.
4. Confirm the sidecar `.txt` carries the exception type and stack.
5. Confirm the app stayed alive: tray still present, "Ready" status.
6. Re-trigger a dictation session and confirm the full pipeline still works.
```

- [ ] **Step 2: Commit**

```bash
git add docs/manual-test.md
git commit -m "docs: Plan 5 manual smoke procedure"
```

---

## Task 23: Debug-build crash trigger (developer-only)

**Files:**
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayMenu.xaml`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayMenu.xaml.cs`
- Modify: `/home/jesse/git/winpepper/src/Winpepper.App/Tray/TrayIconHost.cs`

> **WinUI build carry-forward.**

For the manual smoke step "trigger an artificial crash" to be repeatable without recompiling, add a `Debug`-only tray menu item.

- [ ] **Step 1: Add the menu item in `TrayMenu.xaml`**

Inside the existing menu flyout, wrap the new item in a debug-only block by always defining it but only making it visible under `#if DEBUG`:

```xml
<MenuFlyoutItem x:Name="CrashTestItem" Text="Throw synthetic crash" Visibility="Collapsed" />
```

- [ ] **Step 2: In `TrayMenu.xaml.cs`, mark the menu item visible under `#if DEBUG`**

```csharp
public partial class TrayMenu
{
    public TrayMenu()
    {
        InitializeComponent();
#if DEBUG
        CrashTestItem.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
#endif
    }
}
```

- [ ] **Step 3: Hook the click in `TrayIconHost`**

In the `TrayIconHost` ctor, after the existing `_menu.QuitMenuItem.Click += ...` line:

```csharp
_menu.CrashTestItem.Click += (_, _) =>
    throw new InvalidOperationException("synthetic crash from tray menu");
```

The throw runs on the UI thread; `Program.Main`'s `AppDomain.UnhandledException` handler picks it up, writes the dump, and resets the engine.

- [ ] **Step 4: VM build**

```bash
./scripts/winrun "dotnet build src/Winpepper.App/Winpepper.App.csproj -c Debug"
```

Expected: WinUI markup-compiler PNSE per carry-forward. Commit as-written.

- [ ] **Step 5: Commit**

```bash
git add src/Winpepper.App/Tray
git commit -m "feat(tray): debug-only 'Throw synthetic crash' menu item"
```

---

## Task 24: Integration test — `PostPasteWatcher` end-to-end with `CorrectionStore`

**Files:**
- Create: `/home/jesse/git/winpepper/tests/Winpepper.IntegrationTests/PostPasteLearningEndToEndTests.cs`
- Modify: `/home/jesse/git/winpepper/tests/Winpepper.IntegrationTests/Winpepper.IntegrationTests.csproj` (add `Winpepper.Core` + `Winpepper.Corrections` refs if missing)

Spec §10.2: integration tests are opt-in via `WINPEPPER_INTEGRATION=1`. This test exercises the end-to-end `FakeFocusedElementTextWatcher` → `LearningDiffAnalyzer` → `ToastPostPasteToastPrompt` → `CorrectionStore` flow using a real on-disk `corrections.json` (atomic-file path).

- [ ] **Step 1: Write the test `tests/Winpepper.IntegrationTests/PostPasteLearningEndToEndTests.cs`**

```csharp
using Shouldly;
using Winpepper.Corrections;
using Winpepper.Core.Learning;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.IntegrationTests;

public class PostPasteLearningEndToEndTests : IDisposable
{
    private readonly string _correctionsPath;
    public PostPasteLearningEndToEndTests()
    {
        _correctionsPath = Path.Combine(Path.GetTempPath(), $"corr-it-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_correctionsPath)) File.Delete(_correctionsPath); }

    [Fact]
    public async Task Full_Flow_Persists_Replacement_To_Disk()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_correctionsPath);
        var writer = new CorrectionStoreWriter(store);
        var toasts = new FakeToastService();
        toasts.AutoSelect("yes");
        var prompt = new ToastPostPasteToastPrompt(toasts);
        using var ppw = new PostPasteWatcher(watcher, writer, prompt, TimeSpan.FromSeconds(5));

        var ctx = new PostPasteContext
        {
            ElementId = "el-1",
            InjectedText = "Send chat gbt the link",
            SessionId = Guid.NewGuid(),
            InjectionEndUtc = DateTime.UtcNow,
        };
        var done = ppw.BeginAsync(ctx);

        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        var disk = new CorrectionStore(_correctionsPath).Load();
        disk.Replacements.ShouldContainKey("chat gbt");
        disk.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }
}
```

- [ ] **Step 2: Run the test**

```bash
cd /home/jesse/git/winpepper
dotnet test tests/Winpepper.IntegrationTests --filter "FullyQualifiedName~PostPasteLearningEndToEndTests"
```

This integration test is pure-C# (no UIA — uses the fake). It runs on Linux. The `WINPEPPER_INTEGRATION=1` gate is left for the live-UIA tests Plan 5 doesn't ship; this one is unconditional.

Expected: 1 test passes.

- [ ] **Step 3: Commit**

```bash
git add tests/Winpepper.IntegrationTests/PostPasteLearningEndToEndTests.cs
git commit -m "test(integration): post-paste learning end-to-end with disk-backed store"
```

---

## Task 25: Final full-suite test sweep

**Files:**
- None.

- [ ] **Step 1: Full Linux test run**

```bash
cd /home/jesse/git/winpepper
dotnet test --filter "Platform!=Windows"
```

Expected: every project's Linux-runnable suite green, including:

- `Winpepper.Core.Tests/Errors/ErrorBusTests` (4 tests, Task 1)
- `Winpepper.Core.Tests/Logging/LogRingBufferTests` (4 tests, Task 2)
- `Winpepper.Core.Tests/Logging/RingBufferSinkTests` (2 tests, Task 3)
- `Winpepper.Core.Tests/Learning/LevenshteinDistanceTests` (6 tests, Task 4)
- `Winpepper.Core.Tests/Learning/LearningDiffAnalyzerTests` (9 tests, Task 4)
- `Winpepper.Core.Tests/Learning/FakeFocusedElementTextWatcherTests` (2 tests, Task 5)
- `Winpepper.Core.Tests/Learning/PostPasteWatcherTests` (5 tests, Task 6)
- `Winpepper.Platform.Tests/Learning/UiaFocusedElementCaptureTests` (3 tests, Task 7)
- `Winpepper.Platform.Tests/Learning/FocusedElementSnapshotTests` (2 tests, Task 8)
- `Winpepper.Core.Tests/Diagnostics/DiagnosticsBundleBuilderTests` (3 tests, Task 9)
- `Winpepper.Core.Tests/ViewModels/DiagnosticsViewModelTests` (5 tests, Task 10)
- `Winpepper.Core.Tests/Crash/CrashHandlerTests` (3 tests, Task 11)
- `Winpepper.Core.Tests/Notifications/FakeToastServiceTests` (3 tests, Task 12)
- `Winpepper.Core.Tests/Learning/ToastPostPasteToastPromptTests` (5 tests, Task 13)
- `Winpepper.Core.Tests/ViewModels/SessionViewModelErrorBusTests` (2 tests, Task 15)
- `Winpepper.Platform.Tests/Injection/ClipboardFallbackTests` (3 tests, Task 16)
- `Winpepper.Core.Tests/Tray/TrayIconStateMapperTests` (4 tests, Task 19)
- `Winpepper.Core.Tests/Errors/ErrorDeepLinkTests` (16 tests, Task 20)
- `Winpepper.IntegrationTests/PostPasteLearningEndToEndTests` (1 test, Task 24)

Plus every test from Plans 1–4. No regressions.

- [ ] **Step 2: VM build sweep**

```bash
./scripts/winrun "dotnet build winpepper.sln"
```

Expected: every non-`Winpepper.App` project compiles cleanly. `Winpepper.App` hits the carry-forward XAML markup-compiler PNSE. Capture the failure verbatim in the next commit message so the user knows it's the known issue, not a new regression.

- [ ] **Step 3: Commit (sweep marker)**

If anything changed during the sweep (test additions, asset tweaks), commit it. Otherwise, no commit.

```bash
git status
# If there are changes:
git commit -am "chore: Plan 5 final test sweep"
```

---

## Self-review checklist (for the writer)

After completing all tasks, verify:

- [ ] **Spec coverage.** Each of the four Plan-5 spec sections maps to at least one task:
    - §7.3 Diagnostics tab → Tasks 2, 3, 9, 10, 21 (live tail buffer + sink + bundle + view-model + page).
    - §8.2 Post-paste learning → Tasks 4, 5, 6, 7, 8, 13, 17 (analyzer + watcher abstraction + watcher + UIA impl + capturer + toast prompt + pipeline wiring).
    - §9.1 Error bus polish → Tasks 1, 15, 16, 19, 20, 21 (bus + pipeline reporting + clipboard fallback + tray icon mapper + deep links + toast).
    - §9.3 Crash safety → Tasks 11, 18, 23 (handler + minidump writer + global registration + dev-only crash trigger).
- [ ] **Placeholder scan.** Search the plan for `TODO`, `TBD`, "implement later", "fill in", "Similar to Task". Every step has concrete code or commands.
- [ ] **Type consistency.** Types referenced across tasks match exactly:
    - `ErrorBus`, `ErrorRecord`, `ErrorStage`, `ErrorDeepLink` (Tasks 1, 15, 20).
    - `LogRingBuffer`, `LogTailEntry`, `RingBufferSink`, `WinpepperLogging.CreateWithBuffer` (Tasks 2, 3, 21).
    - `LearningCandidate`, `LearningDiffAnalyzer`, `LevenshteinDistance` (Task 4 → Tasks 6, 13, 24).
    - `IFocusedElementTextWatcher`, `FocusedElementTextChange`, `FakeFocusedElementTextWatcher`, `UiaFocusedElementTextWatcher`, `UiaFocusedElementCapture` (Tasks 5, 7, 8, 17).
    - `PostPasteContext`, `PostPasteDecision`, `IPostPasteToastPrompt`, `PostPasteWatcher`, `ICorrectionWriter`, `CorrectionStoreWriter`, `ToastPostPasteToastPrompt` (Tasks 6, 13, 17, 24).
    - `FocusedElementSnapshot`, `FocusedElementCapturer` (Tasks 8, 17).
    - `DiagnosticsBundle`, `DiagnosticsSysInfo`, `DiagnosticsBundleBuilder`, `DiagnosticsViewModel`, `IDiagnosticsHost`, `DiagnosticsHost` (Tasks 9, 10, 21).
    - `CrashHandler`, `ICrashSink`, `MiniDumpWriter`, `DbgHelpNative` (Tasks 11, 18).
    - `IToastService`, `ToastButton`, `FakeToastService`, `AppNotificationToastService` (Tasks 12, 13, 14, 16, 21).
    - `ClipboardFallback`, `IClipboard`, `WindowsClipboard` (Task 16).
    - `TrayIconStateMapper`, `TrayIconState` (Task 19).
- [ ] **File paths in step headers match `git add` commands.** Every task ends with a green build and a green test run on Linux (Windows-only tasks ride the carry-forward).

## What Plan 5 does NOT cover (intentionally)

- WiX MSI, autostart MSI install, code signing, CI nightly — Plan 6.
- ARM64 / Microsoft Store identity — out of v1 scope.

## Handoff

When all tasks are committed and `dotnet test --filter "Platform!=Windows"` runs green on Linux: tell the user the v1 feature surface is complete (modulo the WinUI build carry-forward), then start Plan 6 (packaging + signing).
