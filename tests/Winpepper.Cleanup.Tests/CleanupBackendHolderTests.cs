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
                    GgufPath: raw == "no-gguf" ? null : $"/tmp/{raw ?? "model-default"}.gguf",
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

    [Fact]
    public void EnsureCurrent_DesiredDiffersAndNoPrewarm_SelfHealsViaBackgroundLoad()
    {
        // No RequestPrewarm at all (e.g. the model was installed after boot):
        // the seam itself must kick a background load and a later dictation swaps.
        var h = new Harness { Desired = "model-a" };

        var first = h.Holder.EnsureCurrent();
        first.Runner.ShouldBeNull(); // this dictation proceeds raw; no synchronous load

        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
    }

    [Fact]
    public void FailedVerification_KeepsCurrentModel_AndNeverConstructsBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.VerifyResult = false;
        h.Desired = "model-b";
        h.Holder.RequestPrewarm();

        // Wait until at least one model-b verification attempt has run.
        var verifyCallsBefore = h.VerifyCalls;
        SpinWait.SpinUntil(() => h.VerifyCalls > verifyCallsBefore, Timeout).ShouldBeTrue();

        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");   // kept the working model
        lease.Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBe(1);            // model-b backend never constructed
        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout)
            .ShouldBeTrue("failed verification must be logged loudly");
    }

    [Fact]
    public void MissingGgufPath_KeepsCurrentModel_AndNeverConstructsBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "no-gguf"; // the harness resolve below maps this to GgufPath null
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout).ShouldBeTrue();
        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");
        h.BackendFactoryCalls.ShouldBe(1);
    }

    [Fact]
    public void StalePrewarm_RepromotingTheLoadedModel_DisposesTheUnusedPrewarmedBackend()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        SpinWait.SpinUntil(() => { lock (h.FactoryTargets) return h.Backends.Count == 2; }, Timeout)
            .ShouldBeTrue("model-b pre-warm should construct a backend");

        // The user promotes model-a back before model-b was ever used: the
        // pre-warmed model-b backend must be disposed, never swapped in.
        h.Desired = "model-a";
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Backends[1].Disposed, Timeout)
            .ShouldBeTrue("unused pre-warmed backend must be disposed");
        var lease = h.Holder.EnsureCurrent();
        lease.LoadedModelName.ShouldBe("model-a");
        h.Backends[0].Disposed.ShouldBeFalse(); // the live backend stays alive
    }

    [Fact]
    public void FailedLoad_IsRetriedByALaterDictation()
    {
        var h = new Harness { Desired = "model-a" };
        h.ThrowOnNextBackendConstruction = new InvalidOperationException("boom");
        h.Holder.RequestPrewarm();

        // First attempt fails (logged); polling the seam retries and succeeds.
        h.DictateUntilLoaded("model-a").Runner.ShouldNotBeNull();
        h.BackendFactoryCalls.ShouldBeGreaterThanOrEqualTo(2);
        h.Log.Warnings.ShouldNotBeEmpty();
    }

    [Fact]
    public void Dispose_DisposesLiveAndPendingBackends()
    {
        var h = new Harness { Desired = "model-a" };
        h.Holder.RequestPrewarm();
        h.DictateUntilLoaded("model-a");

        h.Desired = "model-b";
        h.Holder.RequestPrewarm();
        SpinWait.SpinUntil(() => { lock (h.FactoryTargets) return h.Backends.Count == 2; }, Timeout)
            .ShouldBeTrue();

        h.Holder.Dispose();

        SpinWait.SpinUntil(() => h.Backends[0].Disposed && h.Backends[1].Disposed, Timeout)
            .ShouldBeTrue("both the live and the pending pre-warmed backend must be disposed");
    }

    [Fact]
    public void Prewarm_RunsWarmupOnTheNewBackend_BeforeItIsAdoptable()
    {
        DisposableFakeBackend? warmed = null;
        DisposableFakeBackend? made = null;
        var holder = new CleanupBackendHolder(
            desiredModelName: () => "model-a",
            resolve: raw => new CleanupModelTarget(
                GgufPath: "/tmp/model-a.gguf", ResolvedName: "model-a",
                FellBackToDefault: false, PromptFormat: "chatml"),
            verifyReady: _ => true,
            backendFactory: _ => made = new DisposableFakeBackend(),
            runnerFactory: (backend, omit) =>
                new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance, omit),
            log: new CollectingLogger<CleanupBackendHolder>(),
            warmup: (backend, _) =>
            {
                warmed = (DisposableFakeBackend)backend;
                return Task.CompletedTask;
            });

        holder.RequestPrewarm();
        SpinWait.SpinUntil(() => holder.EnsureCurrent().Runner is not null, Timeout)
            .ShouldBeTrue();

        // The warm-up ran inside the pre-warm load task, on the new backend,
        // before the seam could adopt it: a pre-warm is "ready" only after
        // load + warm-up complete (ledger A5).
        warmed.ShouldNotBeNull();
        warmed.ShouldBeSameAs(made);
    }

    [Fact]
    public void UnknownModelName_FallingBackToDefault_LogsWarning()
    {
        var h = new HarnessWithFallback { Desired = "bogus-name" };
        h.Holder.RequestPrewarm();

        SpinWait.SpinUntil(() => h.Log.Warnings.Count > 0, Timeout)
            .ShouldBeTrue("silent registry fallback must be surfaced");
        h.DictateUntilLoaded("model-default");
    }

    /// <summary>Harness whose resolve reports FellBackToDefault for unknown names.</summary>
    private sealed class HarnessWithFallback
    {
        public volatile string? Desired;
        public readonly CollectingLogger<CleanupBackendHolder> Log = new();
        public CleanupBackendHolder Holder { get; }

        public HarnessWithFallback()
        {
            Holder = new CleanupBackendHolder(
                desiredModelName: () => Desired,
                resolve: raw => new CleanupModelTarget(
                    GgufPath: "/tmp/model-default.gguf",
                    ResolvedName: "model-default",
                    FellBackToDefault: raw != "model-default",
                    PromptFormat: "chatml"),
                verifyReady: _ => true,
                backendFactory: _ => new DisposableFakeBackend(),
                runnerFactory: (backend, omit) =>
                    new CleanupRunner(backend, NullLogger<CleanupRunner>.Instance, omit),
                log: Log);
        }

        public void DictateUntilLoaded(string resolvedName) =>
            SpinWait.SpinUntil(() =>
                string.Equals(Holder.EnsureCurrent().LoadedModelName, resolvedName, StringComparison.Ordinal),
                Timeout).ShouldBeTrue();
    }
}
