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
        var coordinator = new ModelProvisioningCoordinator(_root, async (model, installRoot, _, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            var modelDir = Path.Combine(installRoot, model.InstallDirRelative);
            Directory.CreateDirectory(modelDir);
            await File.WriteAllTextAsync(Path.Combine(modelDir, "model.bin"), "hello", ct);
        });

        var provisioning = coordinator.EnsureReadyAsync(descriptor, CancellationToken.None);
        await entered.Task;
        var verification = coordinator.VerifyReadyAsync(descriptor, CancellationToken.None);

        verification.IsCompleted.ShouldBeFalse();
        release.TrySetResult();
        await provisioning;
        (await verification).ShouldBeTrue();
        coordinator.State.Status.ShouldBe(ModelProvisioningStatus.Ready);
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
}
