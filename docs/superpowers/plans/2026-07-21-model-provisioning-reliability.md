# Model Provisioning Reliability Implementation Plan

> **For the implementer:** Follow test-driven development. Work only in the assigned worktree, commit the finished track, and do not push.

**Goal:** Make downloads resume after stalls and prevent onboarding from offering dictation until the required ASR model is verified and usable.

**Architecture:** Harden the range transport/downloader, then introduce one readiness/provisioning service consumed by onboarding and Models UI. Keep core view models platform-neutral by injecting narrow interfaces or delegates.

---

### Task 1: Prove and harden resumable transport

**Files:** `src/Winpepper.Models/*Range*`, `src/Winpepper.Models/ModelDownloader.cs`, `tests/Winpepper.Models.Tests/ModelDownloaderTests.cs`, related new test files as needed.

1. Add failing tests for a stalled stream, retry from the partial length, rejected/ignored range responses, and cancellation preserving the partial.
2. Add a 30-second idle timeout around forward progress, with a testable time abstraction or configurable option.
3. Retry transient failures up to three attempts with bounded 1s/2s backoff and resume from the current partial length.
4. Validate HTTP range semantics before appending. If range is ignored, truncate/restart safely; reject incompatible ranges.
5. Verify declared size as well as SHA-256 before promotion.
6. Run the model test project and commit the transport slice.

### Task 2: Add authoritative provisioning/readiness state

**Files:** `src/Winpepper.Models/*`, `tests/Winpepper.Models.Tests/*`.

1. Add failing tests for Missing/Downloading/Verifying/Retrying/Ready/Failed transitions and verified readiness.
2. Implement a concurrency-safe coordinator that coalesces simultaneous ensure requests and exposes state/progress/error.
3. Ensure Retry reuses partial files and Ready requires every required ASR artifact to pass size/hash verification.
4. Run model tests and commit the coordinator slice.

### Task 3: Gate onboarding on real readiness

**Files:** `src/Winpepper.Core/ViewModels/OnboardingViewModel.cs`, `src/Winpepper.App/Views/OnboardingPage.xaml*`, application composition files, core/app tests.

1. Add failing view-model/integration tests showing Download cannot advance on failure and Test Dictation is unreachable while ASR is unready.
2. Replace the no-op download delegate with the production provisioning coordinator.
3. Bind progress, retry, busy, and error state on Download Models. Remove model-skip-to-test behavior.
4. Recheck readiness and pipeline startup before enabling/running Test Dictation.
5. Keep cleanup-model provisioning optional and preserve existing settings behavior.
6. Run affected tests and commit the onboarding slice.

### Task 4: Reuse provisioning from Models UI

**Files:** `src/Winpepper.App/Views/ModelsPage.xaml*`, related model view models/services and tests.

1. Add a failing test that concurrent Models/onboarding requests share one provisioning operation.
2. Route Models page install/retry through the coordinator, with cancellable UI lifetime tokens where practical.
3. Verify existing installed/remove flows remain correct.
4. Run all affected tests, then a Windows Release build. Commit final track changes and report commits plus exact commands/results.
