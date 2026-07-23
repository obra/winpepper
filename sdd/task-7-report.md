# Task 7 Implementation Report: custom_spelling + keyterms payload

## Summary
✅ **COMPLETE** — Task 7 successfully implemented. All 5 files modified as per brief. Full test suite passes with `Failed: 0` (54 tests total).

## Implementation Details

### Files Modified/Created
1. **Created**: `src/Winpepper.Asr/Transcription/AssemblyAiRequests.cs`
   - `AssemblyAiCustomSpelling` record: maps misheard forms to correct text
   - `AssemblyAiRequestExtras` record: holds CustomSpelling array and Keyterms array with Empty static

2. **Modified**: `src/Winpepper.Asr/Transcription/AssemblyAiClient.cs`
   - Updated interface `IAssemblyAiClient.CreateTranscriptAsync` to accept `AssemblyAiRequestExtras extras` parameter
   - Replaced anonymous object payload with `Dictionary<string, object?>` to conditionally include fields
   - Added logic to include `custom_spelling` when `extras.CustomSpelling.Count > 0`
   - Added logic to include `keyterms_prompt` when `extras.Keyterms.Count > 0`
   - **Explicitly avoided** `word_boost` field (silently downgrades universal-3 models per brief)

3. **Modified**: `tests/Winpepper.Asr.Tests/AssemblyAiClientTests.cs`
   - Replaced old `CreateTranscript_SendsSpeechModelPayload_ReturnsId` test
   - Added `CreateTranscript_SendsSpeechModelPayload_NoVocab_NoWordBoost`: validates base payload without custom_spelling/keyterms
   - Added `CreateTranscript_MapsCustomSpelling_AndKeyterms`: validates custom_spelling and keyterms_prompt are serialized correctly
   - Added `CreateTranscript_401_ThrowsAuthError`: validates auth error handling with new signature
   - All new tests verify absence of `word_boost` in JSON payload

4. **Modified**: `tests/Winpepper.Asr.Tests/FakeAssemblyAiClient.cs`
   - Updated `CreateTranscriptAsync` signature to accept `AssemblyAiRequestExtras extras`
   - Added `LastExtras` property to record the extras passed to the fake
   - Maintains `Task.FromResult("t-fake")` behavior

5. **Modified**: `src/Winpepper.Asr/Transcription/AssemblyAiTranscriber.cs`
   - Updated line 49 call to `CreateTranscriptAsync` to pass `AssemblyAiRequestExtras.Empty`
   - Future Task 14 will wire the real extras provider at this call site

## Test Results
```
=== TEST EXECUTION SUMMARY ===
   Winpepper.Asr.Tests  Total: 54, Errors: 0, Failed: 0, Skipped: 0, Time: 0.819s
```

All existing tests continue to pass. Three new tests added and passing:
- `CreateTranscript_SendsSpeechModelPayload_NoVocab_NoWordBoost` ✅
- `CreateTranscript_MapsCustomSpelling_AndKeyterms` ✅
- `CreateTranscript_401_ThrowsAuthError` ✅

## Key Design Decisions

1. **Conditional Payload Construction**: Used `Dictionary<string, object?>` instead of anonymous type to conditionally include custom_spelling and keyterms_prompt only when non-empty. This prevents serializing empty arrays/fields.

2. **No word_boost**: Per brief, `word_boost` is intentionally omitted from the payload. The comment documents that it silently downgrades universal-3 models.

3. **Keyterms Conditional**: Only `keyterms_prompt` is sent when extras.Keyterms is non-empty, as it represents an opt-in paid add-on on some tiers.

4. **Custom Spelling Always**: `custom_spelling` is sent whenever extras.CustomSpelling is non-empty, as it's safe on all tiers.

5. **JSON Serialization**: Anonymous types used for custom_spelling elements preserve the key order (`from` then `to`) guaranteed by C# member order.

## Commit Information
```
Commit SHA: e0bc535
Message: feat(asr): send custom_spelling always + keyterms opt-in, never word_boost
Branch: fix-assemblyai-asr-integration
```

## Notes & Concerns
- **None** — implementation follows brief exactly with clean separation of concerns
- Task 14 will provide the real extras provider; for now, production uses `AssemblyAiRequestExtras.Empty`
- All three new test cases validate correct JSON serialization and field absence/presence as expected
