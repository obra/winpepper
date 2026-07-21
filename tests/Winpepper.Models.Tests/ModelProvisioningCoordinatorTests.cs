using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace Winpepper.Models.Tests;

public sealed class ModelProvisioningCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"provisioning-{Guid.NewGuid():N}");

    public ModelProvisioningCoordinatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("too long")]
    public async Task VerifyReadyAsync_RequiresDeclaredSizeAndSha256(string actualContents)
    {
        var descriptor = Descriptor("ready");
        var modelDir = Path.Combine(_root, descriptor.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(
            Path.Combine(modelDir, "model.bin"), actualContents, TestContext.Current.CancellationToken);
        var coordinator = new ModelProvisioningCoordinator(_root, (_, _, _, _) => Task.CompletedTask);

        var ready = await coordinator.VerifyReadyAsync(descriptor, CancellationToken.None);

        ready.ShouldBeFalse();
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Missing);
    }

    [Fact]
    public async Task EnsureReadyAsync_CoalescesConcurrentOnboardingAndModelsRequests_AndEndsVerifiedReady()
    {
        var descriptor = Descriptor("hello");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCount = 0;
        var states = new List<ModelProvisioningStatus>();
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, progress, ct) =>
        {
            Interlocked.Increment(ref downloadCount);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            await File.WriteAllTextAsync(Path.Combine(modelDir, "model.bin"), "hello", ct);
        });
        coordinator.StateChanged += (_, state) => states.Add(state.Status);

        var onboardingRequest = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);
        await entered.Task;
        var modelsPageRequest = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);
        release.TrySetResult();
        await Task.WhenAll(onboardingRequest, modelsPageRequest);

        downloadCount.ShouldBe(1);
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Ready);
        states.ShouldContain(ModelProvisioningStatus.Downloading);
        states.ShouldContain(ModelProvisioningStatus.Verifying);
        states[^1].ShouldBe(ModelProvisioningStatus.Ready);
    }

    [Fact]
    public async Task VerifyReadyAsync_WaitsForAnActiveProvisioningOperation()
    {
        var descriptor = Descriptor("hello");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<ModelProvisioningStatus>();
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            await File.WriteAllTextAsync(Path.Combine(modelDir, "model.bin"), "hello", ct);
        });
        coordinator.StateChanged += (_, state) => states.Add(state.Status);

        var provisioning = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);
        await entered.Task;
        var verification = coordinator.VerifyReadyAsync(descriptor, CancellationToken.None);

        verification.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await provisioning;
        (await verification).ShouldBeTrue();
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Ready);
        states.TakeLast(2).ShouldBe([ModelProvisioningStatus.Verifying, ModelProvisioningStatus.Ready]);
    }

    [Fact]
    public async Task VerifyReadyAsync_CancellationDoesNotWaitForOrCancelBlockedProvisioning()
    {
        var descriptor = Descriptor("hello");
        var provisioningEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvisioning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, _, ct) =>
        {
            provisioningEntered.TrySetResult();
            await releaseProvisioning.Task.WaitAsync(ct);
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            await File.WriteAllTextAsync(Path.Combine(modelDir, "model.bin"), "hello", ct);
        });

        var provisioning = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);
        await provisioningEntered.Task;
        using var verifyCts = new CancellationTokenSource();
        var verification = coordinator.VerifyReadyAsync(descriptor, verifyCts.Token);
        verifyCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await verification.WaitAsync(
                TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        provisioning.IsCompleted.ShouldBeFalse();

        releaseProvisioning.TrySetResult();
        await provisioning;
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Ready);
    }

    [Fact]
    public async Task EnsureReadyAsync_WaitsForEarlierVerification_AndPublishesOrderedStates()
    {
        var descriptor = Descriptor("hello");
        var modelDir = Path.Combine(_root, descriptor.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(
            Path.Combine(modelDir, "model.bin"), "hello", TestContext.Current.CancellationToken);
        var verificationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadCount = 0;
        var states = new List<ModelProvisioningStatus>();
        var coordinator = new ModelProvisioningCoordinator(
            _root,
            (_, _, _, _) => { Interlocked.Increment(ref downloadCount); return Task.CompletedTask; },
            verifyFile: async (_, _, ct) =>
            {
                verificationEntered.TrySetResult();
                await releaseVerification.Task.WaitAsync(ct);
                return true;
            });
        coordinator.StateChanged += (_, state) => states.Add(state.Status);

        var verification = coordinator.VerifyReadyAsync(descriptor, CancellationToken.None);
        await verificationEntered.Task;
        var provisioning = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);

        provisioning.IsCompleted.ShouldBeFalse();
        releaseVerification.TrySetResult();
        (await verification).ShouldBeTrue();
        await provisioning;

        downloadCount.ShouldBe(0);
        states.ShouldBe([
            ModelProvisioningStatus.Verifying,
            ModelProvisioningStatus.Ready,
            ModelProvisioningStatus.Verifying,
            ModelProvisioningStatus.Ready,
        ]);
    }

    [Fact]
    public async Task VerifyReadyAsync_CancellationRestoresPreviousStableState()
    {
        var descriptor = Descriptor("hello");
        var modelDir = Path.Combine(_root, descriptor.InstallDirRelative);
        Directory.CreateDirectory(modelDir);
        await File.WriteAllTextAsync(
            Path.Combine(modelDir, "model.bin"), "hello", TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ModelProvisioningCoordinator(
            _root,
            (_, _, _, _) => Task.CompletedTask,
            verifyFile: async (_, _, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            });
        using var cts = new CancellationTokenSource();

        var verification = coordinator.VerifyReadyAsync(descriptor, cts.Token);
        await entered.Task;
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => verification);
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Missing);
    }

    [Fact]
    public async Task EnsureReadyAsync_ReportsMonotonicAggregateProgressAcrossFiles()
    {
        var descriptor = TwoFileDescriptor();
        var progressValues = new List<double>();
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, progress, ct) =>
        {
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            progress.Report(Progress(model, model.Files[0], 5));
            await File.WriteAllTextAsync(Path.Combine(modelDir, model.Files[0].RelativePath), "hello", ct);
            progress.Report(Progress(model, model.Files[1], 0));
            progress.Report(Progress(model, model.Files[1], 5));
            await File.WriteAllTextAsync(Path.Combine(modelDir, model.Files[1].RelativePath), "world", ct);
        });
        coordinator.StateChanged += (_, state) =>
            progressValues.Add(state.ProgressPercent);

        await coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);

        progressValues.ShouldBe(progressValues.OrderBy(value => value));
        progressValues.ShouldContain(50);
        progressValues[^1].ShouldBe(100);
    }

    [Fact]
    public async Task EnsureReadyAsync_TransitionsThroughRetrying_AfterFailure()
    {
        var descriptor = Descriptor("hello");
        var attempts = 0;
        var states = new List<ModelProvisioningStatus>();
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, _, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("offline");
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            await File.WriteAllTextAsync(Path.Combine(modelDir, "model.bin"), "hello", ct);
        });
        coordinator.StateChanged += (_, state) => states.Add(state.Status);

        await Should.ThrowAsync<IOException>(() => coordinator.EnsureReadyAsync(descriptor, CancellationToken.None));
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Failed);

        await coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);

        states.ShouldContain(ModelProvisioningStatus.Retrying);
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Ready);
        attempts.ShouldBe(2);
    }

    private static ModelDescriptor Descriptor(string contents) => new()
    {
        Name = "asr",
        Kind = ModelKind.Asr,
        DisplayName = "ASR",
        InstallDirRelative = "asr",
        Files =
        [
            new ModelFile
            {
                RelativePath = "model.bin",
                Url = "https://example.test/model.bin",
                SizeBytes = contents.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents))).ToLowerInvariant(),
            },
        ],
    };

    private static ModelDescriptor TwoFileDescriptor() => new()
    {
        Name = "asr-two-file",
        Kind = ModelKind.Asr,
        DisplayName = "ASR",
        InstallDirRelative = "asr-two-file",
        Files =
        [
            ModelFileFor("a.bin", "hello"),
            ModelFileFor("b.bin", "world"),
        ],
    };

    private static ModelFile ModelFileFor(string path, string contents) => new()
    {
        RelativePath = path,
        Url = $"https://example.test/{path}",
        SizeBytes = contents.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contents))).ToLowerInvariant(),
    };

    private static DownloadProgress Progress(ModelDescriptor descriptor, ModelFile file, long bytes) => new()
    {
        DescriptorName = descriptor.Name,
        FileRelativePath = file.RelativePath,
        BytesDownloaded = bytes,
        TotalBytes = file.SizeBytes,
        Phase = DownloadPhase.Downloading,
    };
}
