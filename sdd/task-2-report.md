# Task 2 Report: Capture slow-native-call start ticks in the streaming transcriber

## Summary

Task 2 completed successfully. Implemented instrumentation-only changes to capture raw data for slow native calls (≥250 ms) with absolute `Environment.TickCount64` ticks at call start, capped at 16 entries + overflow counter.

## Implementation

### Files Modified
1. **src/Winpepper.Asr/Transcription/NativeCallStats.cs**
   - Added optional `Over250StartTicks` parameter (IReadOnlyList<long>?) with default null
   - Added optional `Over250Overflow` parameter (int) with default 0
   - Added constant `Over250ListCap = 16`
   - Updated docstring to explain the new fields

2. **src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs**
   - Added two new fields to Session class:
     - `private readonly List<long> _over250StartTicks = new();`
     - `private int _over250Overflow;`
   - Updated `TimedNativeCall<T>` method:
     - Capture `var startTick = Environment.TickCount64;` at start
     - In finally block: record start tick if call ≥250ms, cap at 16, increment overflow counter
   - Updated `NativeCallStats` getter to include the new fields in the snapshot

3. **tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs**
   - Added test: `Session_RecordsOver250StartTicks_Absolute()`
     - Validates ticks are captured and fall within expected time range
     - Validates tick count matches CountOver250Ms
     - Validates overflow is 0 when below cap
   - Added test: `Session_CapsOver250StartTicksAt16_AndCountsOverflow()`
     - Tests cap behavior with 17 slow feeds (begin + 16 feeds + 1 overflow)
     - Validates list is capped at 16
     - Validates overflow counter increments correctly

4. **tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs**
   - Modified `StatsExposingTranscriber.StatsSession.NativeCallStats` property from readonly to settable
   - Added test: `FinishAsync_PropagatesOver250Ticks_ThroughFinishStats()`
     - Validates Over250StartTicks and Over250Overflow propagate through StreamingFinishStats
     - Uses Shouldly assertions for property-level validation (avoiding reference-equality issues with list)

5. **docs/plans/2026-07-29-cleanup-asr-contention-evidence.md**
   - Appended capture notes under "## 0b — instrumentation added" section

## TDD Evidence

### RED (Compile Failure)
```
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true
```

Expected failures before implementation (12 compilation errors):
- CS1739: 'NativeCallStats' does not have parameter 'Over250StartTicks'
- CS1061: 'NativeCallStats' does not contain definition for 'Over250StartTicks' / 'Over250Overflow'
- CS0117: 'NativeCallStats' does not contain definition for 'Over250ListCap'

Build FAILED with 12 errors as expected.

### GREEN (All Tests Passing)
```
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true \
  && dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -notrait Platform=Windows
```

Result:
```
=== TEST EXECUTION SUMMARY ===
   Winpepper.Asr.Tests  Total: 319, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 9.102s
```

All 319 tests pass, including:
- 2 new NemotronStreamingTranscriber tests (absolute ticks capture + cap/overflow)
- 1 new propagation test (StreamingDictationSession)
- All pre-existing tests (287 tests + new 3 = 319)

### Full Linux Test Suite
```
./scripts/linux-tests.sh
```

Result:
```
linux-tests grand total: 1535 tests
LINUX SUITE: GREEN
```

All 9 test projects passed:
- Winpepper.Asr.Tests: 319 tests ✓
- Winpepper.Audio.Tests: 73 tests ✓
- Winpepper.Cleanup.Tests: 195 tests ✓
- Winpepper.Core.Tests: 440 tests ✓
- Winpepper.Corrections.Tests: 23 tests ✓
- Winpepper.History.Tests: 45 tests ✓
- Winpepper.IntegrationTests: 2 tests ✓
- Winpepper.Models.Tests: 130 tests ✓
- Winpepper.Platform.Tests: 308 tests ✓

## Self-Review

### Completeness
- ✓ All required code changes implemented per brief
- ✓ All three test cases added with correct assertions
- ✓ Evidence file appended per Step 5
- ✓ TDD cycle: write failing tests → implement → verify green
- ✓ Full test suite passes

### Code Quality
- ✓ Follows existing patterns in NemotronStreamingTranscriber (same gate/locking discipline)
- ✓ Naming matches brief exactly (Over250StartTicks, Over250ListCap, Over250Overflow)
- ✓ Comments and docstrings updated to explain new fields
- ✓ Test assertions use property-level checks (not whole-record equality) to avoid list reference issues
- ✓ No behavior changes; pure measurement/data-carrier additions

### Discipline
- ✓ Only modified files specified in brief
- ✓ No restructuring or refactoring outside task scope
- ✓ Preserved backward compatibility (new fields are optional with defaults)
- ✓ Respects existing locking discipline (_nativeGate serialization)

### Testing
- ✓ TDD followed: RED → GREEN verified
- ✓ No stray warnings in test output (one pre-existing xUnit1051 warning, unrelated)
- ✓ Both timing-sensitive tests (300 ms delay) behave correctly
- ✓ Cap test (17 iterations = ~5 s) completes within reasonable time

## Concerns

None. The implementation:
- Matches the brief exactly
- Passes all tests
- Maintains backward compatibility
- Follows existing code patterns
- Adds no behavior changes (measurement only)

## Commit

```
Commit: 74f1467
Subject: feat(asr): record start ticks of native calls >= 250 ms (cap 16 + overflow)
Body: Run 1 / step 0b: raw data for over250_at. Absolute ticks; offset conversion happens at stamp time.
Co-Authored-By: Amplifier <240397093+microsoft-amplifier@users.noreply.github.com>
```

Files changed: 5
- src/Winpepper.Asr/Transcription/NativeCallStats.cs
- src/Winpepper.Asr/Transcription/NemotronStreamingTranscriber.cs
- tests/Winpepper.Asr.Tests/Transcription/NemotronStreamingTranscriberTests.cs
- tests/Winpepper.Asr.Tests/StreamingDictationSessionTests.cs
- docs/plans/2026-07-29-cleanup-asr-contention-evidence.md

Insertions: 94, Deletions: 5
