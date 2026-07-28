using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup.Tests.Fakes;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CleanupBackendHolderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delegate-injected harness. The resolve map: any raw name resolves to
    /// itself (null -> "model-default"); "model-b" carries PromptFormat
    /// "granite" + OmitPromptExample true so format propagation is observable.
    /// The backend factory can be gated to simulate a slow GGUF load.
    /// </summary>
    private sealed class Harness
    {
        public volatile string? Desired;
        public volatile bool VerifyResult = true;
        public int VerifyCalls;
        public int BackendFactoryCalls;
        public Exception? ThrowOnNextBackendConstruction;
        public ManualResetEventSlim? FactoryGate; // when set, the factory blocks until it is released
        public readonly List<CleanupModelTarget> FactoryTargets = new();
        public readonly List<bool> RunnerOmitFlags = new();
        public readonly List<DisposableFakeBackend> Backends = new();
        public readonly CollectingLogger<CleanupBackendHolder> Log = new();
        public CleanupBackendHolder Holder { get; }

        public Harness()
        {
            Holder = new CleanupBackendHolder(
                desiredModelName: () => Desired,
                resolve: raw => new CleanupModelTarget(
                    GgufPath: $"/tmp/{raw ?? "model-default"}.gguf",
                    ResolvedName: raw ?? "model-default",
                    FellBackToDefault: false,
                    PromptFormat: raw == "model-b" ? "granite" : "chatml",
                    OmitPromptExample: raw == "model-b"),
                verifyReady: _ =>
                {
                    Interlocked.Increment(ref VerifyCalls);
                    return VerifyResult;
                },
                backendFactory: target =>
                {
                    FactoryGate?.Wait(Timeout);
                    Interlocked.Increment(ref BackendFactoryCalls);
                    var pendingThrow = Interlocked.Exchange(ref ThrowOnNextBackendConstruction, null);
                    if (pendingThrow is not null) throw pendingThrow;
                    var backend = new DisposableFakeBackend();
                    lock (FactoryTargets)
                    {
                        FactoryTargets.Add(target);
                        Backends.Add(backend);
                    }
                    return backend;
                },
                runnerFactory: (backend, omit) =>
                {
                    lock (RunnerOmitFlags) RunnerOmitFlags.Add(omit);
                    return new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance,
                        omitPromptExample: omit);
                },
                log: Log);
        }

        /// <summary>
        /// Simulate dictations: call the seam until the holder reports the
        /// model loaded, or time out. Each poll is one "dictation".
        /// </summary>
        public CleanupBackendLease DictateUntilLoaded(string resolvedName)
        {
            CleanupBackendLease lease = Holder.EnsureCurrent();
            SpinWait.SpinUntil(() =>
            {
                lease = Holder.EnsureCurrent();
                return string.Equals(lease.LoadedModelName, resolvedName, StringComparison.Ordinal);
            }, Timeout).ShouldBeTrue($"expected {resolvedName} to be adopted within {Timeout}");
            return lease;
        }
    }

    [Fact]
    public void RequestPrewarm_DoesNotSwap_UntilEnsureCurrent()
    {
        var h = new Harness { Desired = "model-a" };

        h.Holder.RequestPrewarm();

        // Only EnsureCurrent (the dictation seam) mutates the live pair.
        h.Holder.LoadedModelName.ShouldBeNull();

        var lease = h.DictateUntilLoaded("model-a");
        lease.Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void EnsureCurrent_WhilePrewarmInFlight_KeepsCurrentForThisDictation()
    {
        var h = new Harness { Desired = "model-a", FactoryGate = new ManualResetEventSlim(false) };
        h.Holder.RequestPrewarm();

        // The load is stuck in the factory: this dictation must proceed
        // without cleanup rather than wait for the load.
        var lease = h.Holder.EnsureCurrent();
        lease.Runner.ShouldBeNull();
        lease.LoadedModelName.ShouldBeNull();

        h.FactoryGate.Set();
        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
    }

    [Fact]
    public void EnsureCurrent_Swap_DisposesOldBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        var first = h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        var second = h.DictateUntilLoaded("model-b");

        second.Runner.ShouldNotBeNull();
        second.Runner.ShouldNotBeSameAs(first.Runner);
        h.Backends[0].Disposed.ShouldBeTrue();   // replaced live backend freed at the seam
        h.Backends[1].Disposed.ShouldBeFalse();  // the new live backend stays alive
    }

    [Fact]
    public void RequestPrewarm_SameModelTwice_LoadsOnce()
    {
        var h = new Harness { Desired = "model-a", FactoryGate = new ManualResetEventSlim(false) };

        h.Holder.RequestPrewarm();
        h.Holder.RequestPrewarm(); // second promote of the same model: no second load

        h.FactoryGate.Set();
        h.DictateUntilLoaded("model-a");
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void Lease_LoadedModelName_IsTheResolvedNameOfTheModelThatRuns()
    {
        var h = new Harness { Desired = "model-b" };
        h.Holder.RequestPrewarm();

        var lease = h.DictateUntilLoaded("model-b");

        // History attribution stamps this value: the actually-used model.
        lease.LoadedModelName.ShouldBe("model-b");
        h.Holder.LoadedModelName.ShouldBe("model-b");
    }

    [Fact]
    public void Swap_ConstructsFreshPairFromTheNewModelsDescriptorValues()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-b");

        // PromptFormat reaches the backend factory; OmitPromptExample reaches
        // the runner factory — both from the NEW model's descriptor.
        h.FactoryTargets[0].PromptFormat.ShouldBe("chatml");
        h.FactoryTargets[1].PromptFormat.ShouldBe("granite");
        h.RunnerOmitFlags[0].ShouldBeFalse();
        h.RunnerOmitFlags[1].ShouldBeTrue();
    }
}
