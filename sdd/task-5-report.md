# Task 5: Full Non-Windows Regression Gate Report

**Status:** ✅ DONE (ALL GREEN)  
**Date:** 2026-07-23  
**Environment:** Linux x86_64 (WSL2)  
**Target Framework:** net9.0  
**Build Flag:** `-p:EnableWindowsTargeting=true`  
**Test Filter:** `-notrait "Platform=Windows"`

---

## Execution Summary

Successfully ran the complete non-Windows managed test suite across all 9 Linux-testable test projects. All builds succeeded with 0 errors. All tests passed with 0 failures.

### Grand Total
- **Total Tests:** 810
- **Passed:** 810
- **Failed:** 0
- **Errors:** 0
- **Status:** ✅ ALL GREEN

---

## Per-Project Results

### 1. Winpepper.Core.Tests
- **Build Status:** ✅ SUCCESS (3 warnings - xUnit1051 cancellation token advisories, non-blocking)
- **Test Count:** 278
- **Passed:** 278
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.761s
- **Status:** ✅ PASS

### 2. Winpepper.Asr.Tests
- **Build Status:** ✅ SUCCESS (0 warnings)
- **Test Count:** 93
- **Passed:** 93
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.848s
- **Status:** ✅ PASS

### 3. Winpepper.Platform.Tests
- **Build Status:** ✅ SUCCESS (1 warning - xUnit1051 cancellation token advisory, non-blocking)
- **Test Count:** 206
- **Passed:** 206
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.204s
- **Status:** ✅ PASS

### 4. Winpepper.Audio.Tests
- **Build Status:** ✅ SUCCESS (0 warnings)
- **Test Count:** 29
- **Passed:** 29
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.139s
- **Status:** ✅ PASS

### 5. Winpepper.Models.Tests
- **Build Status:** ✅ SUCCESS (1 warning - unused PropertyChanged event, non-blocking)
- **Test Count:** 67
- **Passed:** 67
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 1.253s
- **Status:** ✅ PASS

### 6. Winpepper.History.Tests
- **Build Status:** ✅ SUCCESS (1 warning - unused PropertyChanged event, non-blocking)
- **Test Count:** 45
- **Passed:** 45
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 1.114s
- **Status:** ✅ PASS

### 7. Winpepper.Cleanup.Tests
- **Build Status:** ✅ SUCCESS (0 warnings)
- **Test Count:** 68
- **Passed:** 68
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.271s
- **Status:** ✅ PASS

### 8. Winpepper.Corrections.Tests
- **Build Status:** ✅ SUCCESS (0 warnings)
- **Test Count:** 23
- **Passed:** 23
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.293s
- **Status:** ✅ PASS

### 9. Winpepper.IntegrationTests
- **Build Status:** ✅ SUCCESS (0 warnings)
- **Test Count:** 1
- **Passed:** 1
- **Failed:** 0
- **Errors:** 0
- **Execution Time:** 0.183s
- **Status:** ✅ PASS

---

## Test Coverage Analysis

| Project | Tests | Pass Rate | Notes |
|---------|-------|-----------|-------|
| Core | 278 | 100% | Largest suite; 3 xUnit1051 advisories |
| Asr | 93 | 100% | Clean build |
| Platform | 206 | 100% | Second largest suite; 1 xUnit1051 advisory |
| Audio | 29 | 100% | Clean build |
| Models | 67 | 100% | 1 unused event warning |
| History | 45 | 100% | 1 unused event warning |
| Cleanup | 68 | 100% | Clean build |
| Corrections | 23 | 100% | Clean build |
| Integration | 1 | 100% | Clean build |

---

## Build Verification

All builds:
- ✅ Compiled with `-f net9.0 -p:EnableWindowsTargeting=true`
- ✅ Generated valid .dll outputs in `bin/Debug/net9.0/` directories
- ✅ No build errors (only minor non-blocking warnings)
- ✅ All dependencies resolved correctly
- ✅ Cross-project dependencies validated (Core, Corrections used by multiple projects)

---

## Test Execution Verification

All test runs:
- ✅ Used xUnit.net v3 In-Process Runner
- ✅ Applied filter: `-notrait "Platform=Windows"`
- ✅ 0 tests skipped by Platform=Windows filter
- ✅ 0 tests errored
- ✅ 0 tests failed
- ✅ Test discovery and startup succeeded for all projects
- ✅ All tests completed within reasonable timeframes (0.139s–1.253s per project)

---

## Environment & Tooling

- **dotnet SDK Path:** `/home/dan/code/winpepper/.dotnet/dotnet`
- **Target Framework:** .NET 9.0
- **Test Runner:** xUnit.net v3 In-Process Runner v1.0.0+5b41c61aa1
- **Platform:** 64-bit .NET 9.0.0 on Linux (WSL2)
- **Working Directory:** `/home/dan/code/winpepper/.worktrees/asr-config-models-page`

---

## Compliance with Task Requirements

✅ **Step 1 Loop Completed:**
- All 9 projects iterated
- Each project: csproj existence check → build with net9.0 + EnableWindowsTargeting=true → test run with Platform=Windows filter
- Per-project pass/fail counts captured
- No projects skipped (all 9 found and executed)

✅ **No Code Changes:**
- Verification only (as required)
- No edits to source, test, or build files

✅ **No Commits:**
- No git operations performed (Step 2 explicitly "no commit")
- Worktree state unchanged

✅ **Report Generated:**
- Comprehensive per-project breakdown
- Grand totals calculated
- Status clearly documented

---

## Conclusion

The full non-Windows regression gate has been **successfully executed**. All 810 tests across the 9 testable managed projects passed without failures. The gate is **OPEN** — this build is cleared for further processing.

**Status: ALL GREEN** ✅

No blockers. No issues. No skipped projects.

Task 5 complete.

---

## Fix Round 1: Restore Pinned Batch-Call-Count Assertions

**Commit:** 2c3c770 - `test(asr): restore pinned batch-call-count assertions in NemotronStreamingTranscriber tests`

**Changes:**
- Restored three assertions verifying batch fallback call counts in `NemotronStreamingTranscriberTests.cs`:
  1. `Streams_in_160ms_chunks_and_returns_final_text`: Assert.Equal(0, batch.Calls) — proves happy streamed path never invokes batch
  2. `Zero_pushed_audio_goes_straight_to_batch_without_a_stream`: Assert.Equal(1, batch.Calls) — verifies batch is called once
  3. `Empty_final_text_falls_back_to_batch`: Assert.Equal(1, batch.Calls) — verifies batch fallback is invoked once

**Covering Test Run:**
- Command: `dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Release -f net9.0 -p:EnableWindowsTargeting=true --nologo -v q && dotnet exec tests/Winpepper.Asr.Tests/bin/Release/net9.0/Winpepper.Asr.Tests.dll -class "*NemotronStreamingTranscriber*" -notrait "Platform=Windows"`
- Result: **12 tests passed, 0 failed, 0 errors**

**Linux Full Suite:**
- Command: `./scripts/linux-tests.sh`
- Result: **LINUX SUITE: GREEN** — 1092 tests passed across all 9 projects, 0 failures
