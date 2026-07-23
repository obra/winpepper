# Task 3: Clamp Retry-After to [0, 30s] - Implementation Report

## Summary
✅ **COMPLETE** — Task 3 successfully implemented and tested. The `RetryAfter` method now defensively clamps retry delays to [0s, 30s], preventing negative TimeSpan exceptions and excessive delays from malformed or malicious Retry-After headers.

---

## Implemented Changes

### 1. Test Implementation
**File:** `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`

Added theory-based test method `Upload_429_ClampsGarbageRetryAfter` with three inline data cases:
- **"-5" → 0s**: Negative header values clamped to minimum (0 seconds)
- **"99999" → 30s**: Huge header values clamped to maximum (30 seconds)
- **"banana" → null**: Non-numeric/malformed values ignored, falling back to exponential backoff with jitter (>0, within [0,30] by clamping)

Test validates:
- `delays[0] >= TimeSpan.Zero`
- `delays[0] <= TimeSpan.FromSeconds(30)`
- Exact match for numeric cases (0s, 30s)
- Backoff range for non-numeric cases (>0, <=30)

### 2. Clamping Logic Implementation
**File:** `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`

Replaced `RetryAfter` method (lines 183-190) with:
- **Static readonly constant:** `MaxRetryAfter = TimeSpan.FromSeconds(30)`
- **Logic flow:**
  1. Try to extract raw delay from HTTP `Retry-After` header (both `Delta` and numeric formats)
  2. Return `null` if no header present
  3. **Clamp to [0, 30s]:**
     - Negative values → 0s (prevents ArgumentOutOfRangeException in Task.Delay)
     - Values > 30s → 30s (prevents excessive delays blocking dictation)
  4. Return clamped value

---

## RED → GREEN Evidence

### RED State (Before Implementation)
```
xUnit.net v3 In-Process Runner v1.0.0+5b41c61aa1 (64-bit .NET 9.0.0)
=== TEST EXECUTION SUMMARY ===
   Winpepper.Asr.Tests  Total: 11, Errors: 0, Failed: 2, Skipped: 0, Not Run: 0

FAILURES:
  Upload_429_ClampsGarbageRetryAfter(headerValue: "-5", expectedSeconds: 0) [FAIL]
    → delay was -00:00:05 (should be >= 00:00:00)
  
  Upload_429_ClampsGarbageRetryAfter(headerValue: "99999", expectedSeconds: 30) [FAIL]
    → delay was 1.03:46:39 (should be <= 00:00:30)
```

### GREEN State (After Implementation)
```
xUnit.net v3 In-Process Runner v1.0.0+5b41c61aa1 (64-bit .NET 9.0.0)
=== TEST EXECUTION SUMMARY ===
   Winpepper.Asr.Tests  Total: 11, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0, Time: 0.179s

ALL TESTS PASSED:
  ✓ Upload_429_ClampsGarbageRetryAfter(headerValue: "-5", expectedSeconds: 0) [FINISHED] Time: 0.0018012s
  ✓ Upload_429_ClampsGarbageRetryAfter(headerValue: "99999", expectedSeconds: 30) [FINISHED] Time: 0.000417s
  ✓ Upload_429_ClampsGarbageRetryAfter(headerValue: "banana", expectedSeconds: null) [FINISHED] Time: 0.0000872s
  ✓ Upload_429_HonorsRetryAfterThenSucceeds [FINISHED]
  ✓ Upload_503_BacksOffThenSucceeds [FINISHED]
  ✓ Upload_401_ThrowsAuthErrorWithoutRetry [FINISHED]
  ✓ Upload_400_ThrowsWithoutRetry [FINISHED]
  ✓ Upload_SendsRawBytesWithBareAuthHeader [FINISHED]
  ✓ CreateTranscript_SendsSpeechModelPayload_ReturnsId [FINISHED]
  ✓ GetTranscript_ParsesCompletedFields [FINISHED]
  ✓ ValidateKey_404MeansValid_401MeansBadKey [FINISHED]
```

---

## Test Execution Commands & Results

### Build Command
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build tests/Winpepper.Asr.Tests/Winpepper.Asr.Tests.csproj -c Debug -f net9.0
```
**Result:** ✅ Build succeeded (0 errors, 0 warnings)

### Run Full ASR Test Suite
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
  -class "Winpepper.Asr.Tests.AssemblyAiClientTests"
```
**Result:** ✅ All 11 tests PASSED (0 failures, 0 errors)

### Verbose Test Output (GREEN State)
```bash
export DOTNET_ROOT="$PWD/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet exec tests/Winpepper.Asr.Tests/bin/Debug/net9.0/Winpepper.Asr.Tests.dll \
  -class "Winpepper.Asr.Tests.AssemblyAiClientTests" -verbose
```
**Result:** ✅ All 11 tests completed successfully

---

## Files Changed

### Modified Files
1. **`src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`**
   - Lines 183-190 → Lines 183-200 (extended with clamping logic)
   - Added: `MaxRetryAfter` constant (line 183)
   - Modified: `RetryAfter` method with clamping logic (lines 185-200)

2. **`tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`**
   - Lines 150+ appended with new test method
   - Added: `Upload_429_ClampsGarbageRetryAfter` theory-based test (3 inline data cases)

### Change Summary
```
2 files changed, 39 insertions(+), 5 deletions(-)
```

---

## Git Commit

**Commit SHA:** `c19e25c`  
**Commit Message:** `fix(asr): clamp AssemblyAI Retry-After to [0,30s]`  
**Timestamp:** Task completed and committed to worktree  

Command executed:
```bash
git add src/Winpepper.Asr/Transcription/AssemblyAiClient.cs tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs
git commit -m "fix(asr): clamp AssemblyAI Retry-After to [0,30s]"
```

---

## Concerns & Considerations

### ✅ Completeness
- **Negative values:** Handled → clamped to 0s
- **Huge values:** Handled → clamped to 30s
- **Non-numeric values:** Handled → returns null, falls back to exponential backoff (which is also bounded by logic usage)
- **Pre-existing tests:** All 8 pre-existing tests still pass (no regressions)

### ✅ Correctness
- **Max value (30s):** Reasonable upper bound for rate-limit backoff (prevents multi-hour delays)
- **Min value (0s):** Prevents ArgumentOutOfRangeException from Task.Delay (which rejects negative TimeSpan)
- **Defensive coding:** Comments explain the rationale ("Clamp defensively...")
- **Type safety:** No nullable issues; `TimeSpan.FromSeconds()` handles both positive and negative inputs

### ✅ Output Quality
- Code is pristine, follows existing patterns (static readonly, local var for intermediate state)
- Test structure mirrors existing tests (uses `Make()` helper, `FakeHttpMessageHandler`, Shouldly assertions)
- Comments are clear and aligned with the brief

### No Known Issues
- No breaking changes to existing code
- No changes to public API contracts
- No new dependencies introduced

---

## Implementation Summary

| Step | Status | Evidence |
|------|--------|----------|
| Write failing test | ✅ DONE | Test added; 3 theory cases with inline data |
| Verify RED (2 failures) | ✅ VERIFIED | `-5` failed (negative), `99999` failed (huge) |
| Implement clamping | ✅ DONE | `RetryAfter` method with min/max bounds logic |
| Verify GREEN (all 11 pass) | ✅ VERIFIED | 0 failures, 0 errors, all tests complete |
| Run full ASR suite | ✅ VERIFIED | 11/11 tests pass including all pre-existing tests |
| Commit with brief message | ✅ DONE | SHA `c19e25c`, message follows brief exactly |

---

## Brief Compliance Checklist

- [x] **Step 1:** Write failing test with all three cases (-5, 99999, banana)
- [x] **Step 2:** Run tests, verify FAIL on -5 and 99999 cases
- [x] **Step 3:** Implement clamping with MaxRetryAfter constant and logic
- [x] **Step 4:** Run full test suite, verify PASS on all tests
- [x] **Step 5:** Commit with exact message from brief

---

## Conclusion

Task 3 is **complete and verified**. The Retry-After header is now defensively clamped to [0s, 30s], protecting against:
- **Negative delays:** Would throw from Task.Delay (now clamped to 0s)
- **Excessive delays:** Would stall dictation (now clamped to 30s)
- **Malformed headers:** Non-numeric values ignored (fallback to backoff, also bounded)

All tests pass. No regressions. Ready for merge.
