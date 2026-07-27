# Settings lost-update fix — evidence

Branch: fix/settings-lost-update, forked from 100d33c.

## Fork-point baseline (before any change)
- `./scripts/linux-tests.sh`: LINUX SUITE: GREEN, grand total: 1162 tests (verbatim last 3 lines pasted below)

```

linux-tests grand total: 1162 tests
LINUX SUITE: GREEN
```

## Red test (Task 1) — verbatim failure against HEAD, BEFORE the fix

Command:

```bash
dotnet build tests/Winpepper.Core.Tests -c Release -f net9.0 -p:EnableWindowsTargeting=true
dotnet exec tests/Winpepper.Core.Tests/bin/Release/net9.0/Winpepper.Core.Tests.dll \
  -method "Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.Flush_PreservesChangesWrittenOutsideTheWriter"
```

Verbatim output (2026-07-26, HEAD = d6aa194, before any src change):

```
xUnit.net v3 In-Process Runner v1.0.0+5b41c61aa1 (64-bit .NET 9.0.0)
  Discovering: Winpepper.Core.Tests
  Discovered:  Winpepper.Core.Tests
  Starting:    Winpepper.Core.Tests
    Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.Flush_PreservesChangesWrittenOutsideTheWriter [FAIL]
      Shouldly.ShouldAssertException : store.Load();
              final.CleanupModelName
          should be
      "promoted-model"
          but was
      "qwen2.5-0.5b-instruct-q4_k_m"
          difference
      Difference     |  |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |        
                     | \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/       
      Index          | 0    1    2    3    4    5    6    7    8    9    10   11   12   13   14   15   16   17   18   19   20   ...  
      Expected Value | p    r    o    m    o    t    e    d    -    m    o    d    e    l                                       ...  
      Actual Value   | q    w    e    n    2    .    5    -    0    .    5    b    -    i    n    s    t    r    u    c    t    ...  
      Expected Code  | 112  114  111  109  111  116  101  100  45   109  111  100  101  108                                     ...  
      Actual Code    | 113  119  101  110  50   46   53   45   48   46   53   98   45   105  110  115  116  114  117  99   116  ...  
      
      Difference     |       |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |   
                     |      \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  
      Index          | ...  7    8    9    10   11   12   13   14   15   16   17   18   19   20   21   22   23   24   25   26   27   
      Expected Value | ...  d    -    m    o    d    e    l                                                                          
      Actual Value   | ...  -    0    .    5    b    -    i    n    s    t    r    u    c    t    -    q    4    _    k    _    m    
      Expected Code  | ...  100  45   109  111  100  101  108                                                                        
      Actual Code    | ...  45   48   46   53   98   45   105  110  115  116  114  117  99   116  45   113  52   95   107  95   109  
      Stack Trace:
        tests/Winpepper.Core.Tests/Settings/DebouncedSettingsWriterTests.cs(106,0): at Winpepper.Core.Tests.Settings.DebouncedSettingsWriterTests.Flush_PreservesChangesWrittenOutsideTheWriter()
           at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
           at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
           at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task, ConfigureAwaitOptions options)
        --- End of stack trace from previous location ---
           at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
           at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
           at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task, ConfigureAwaitOptions options)
  Finished:    Winpepper.Core.Tests
=== TEST EXECUTION SUMMARY ===
   Winpepper.Core.Tests  Total: 1, Errors: 0, Failed: 1, Skipped: 0, Not Run: 0, Time: 0.195s
```

Why this failure was expected: `DebouncedSettingsWriter` snapshots the whole
`AppSettings` record at construction (`_pending = store.Load()` in the ctor) and
`Flush()` rewrites the entire record from that snapshot. The out-of-band
`store.Save(...)` between construction and the unrelated `WindowWidth` flush is
therefore silently reverted — `CleanupModelName` comes back as the boot-time
default `"qwen2.5-0.5b-instruct-q4_k_m"` instead of `"promoted-model"`.

## Write-authority audit (Task 4)

All commands run from the worktree root after the Task 4 edits.

1. All `.Save(` callers in `src/`:

```
$ grep -rn "\.Save(" --include="*.cs" src/
src/Winpepper.Core/Settings/DebouncedSettingsWriter.cs:121:                    _store.Save(after);
src/Winpepper.App/Views/ModelsPage.xaml.cs:231:            keyStore.Save(key.Trim());
src/Winpepper.App/Views/ModelsPage.xaml.cs:266:                    keyStore.Save(typed.Trim());          // typed key is valid -> save it
src/Winpepper.App/Hosting/AppShell.cs:88:            store.Save(settings);
src/Winpepper.App/Hosting/AppShell.cs:161:            (_, _) => { /* Plan 2 wires CorrectionStore.Save() here */ });
```

Exactly the expected set: the writer's own `_store.Save(after)` (single runtime
authority), the documented pre-writer boot repair in `AppShell.Create()`
(AppShell.cs:88 — was :83 before the 5-line comment added above it), two
`keyStore.Save(...)` hits (DPAPI API-key store, NOT settings.json, out of
scope; were :218/:253 pre-edit, shifted by the toggle-capture expansion), and
one comment at AppShell.cs:161 (was :155). The former direct-save bypasses at
ModelsPage:46 and HistoryDetailPage:73 are GONE.

2. MainWindow needs no change (already uses the writer):

```
$ grep -n "SettingsWriter.QueueAndFlushAsync" src/Winpepper.App/Views/MainWindow.xaml.cs
58:            _ = _shell.SettingsWriter.QueueAndFlushAsync(
```

3. No mutator reads live control state inside the lambda:

```
$ grep -rn "Toggle.IsOn })" --include="*.cs" src/
(no hits — exit code 1)
```

Note for the review gate: ModelsPage/HistoryDetailPage/RecordingPage/AppShell
edits are Windows-only WinUI code — not compilable on Linux; hand-verified
against the plan's code blocks and type-checked by windows-gate.sh below. One
behavioral note: mutators now execute at flush time; the four lambdas that
previously read live WinUI control state (ModelsPage AssemblyAi/Streaming
toggles, RecordingPage Autostart toggle) now capture that state into a local
BEFORE queueing, so no mutator reads UI state at all — flush-time execution is
safe on any thread, including a racing debounce tick or a Dispose flush.

## Windows gate (Task 4)

`timeout 2700 ./scripts/windows-gate.sh` from WSL (2026-07-26), after cross-OS
hygiene (`rm -rf src/*/bin src/*/obj tests/*/bin tests/*/obj`) and the host
process poll (two consecutive zero `winpepper` dotnet.exe counts). Exit 0.
Verbatim tail:

```
================ windows-gate summary ================
Winpepper.App build: OK
Winpepper.Asr.Tests (net9.0): OK     Winpepper.Asr.Tests  Total: 231, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 6.805s
Winpepper.Audio.Tests (net9.0): OK     Winpepper.Audio.Tests  Total: 62, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.436s
Winpepper.Audio.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Audio.Tests  Total: 64, Errors: 0, Failed: 0, Skipped: 1, Not Run: 0, Time: 0.472s
Winpepper.Cleanup.Tests (net9.0): OK     Winpepper.Cleanup.Tests  Total: 106, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.895s
Winpepper.Cleanup.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Cleanup.Tests  Total: 129, Errors: 0, Failed: 0, Skipped: 7, Not Run: 0, Time: 11.560s
Winpepper.Core.Tests (net9.0): OK     Winpepper.Core.Tests  Total: 387, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.553s
Winpepper.Corrections.Tests (net9.0): OK     Winpepper.Corrections.Tests  Total: 23, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.678s
Winpepper.History.Tests (net9.0): OK     Winpepper.History.Tests  Total: 45, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.264s
Winpepper.IntegrationTests (net9.0): OK     Winpepper.IntegrationTests  Total: 2, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.688s
Winpepper.Models.Tests (net9.0): OK     Winpepper.Models.Tests  Total: 100, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 1.731s
Winpepper.Platform.Tests (net9.0): OK     Winpepper.Platform.Tests  Total: 218, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 0.512s
Winpepper.Platform.Tests (net9.0-windows10.0.19041.0): OK     Winpepper.Platform.Tests  Total: 222, Errors: 0, Failed: 0, Skipped: 2, Not Run: 0, Time: 2.295s
grand total tests: 1589 (cross-check only; roughly ~1300+ across 12 runs -- record the actual number)
GATE: GREEN
```

Actual grand total across the 12 runs: 1589 tests, 0 failed.
