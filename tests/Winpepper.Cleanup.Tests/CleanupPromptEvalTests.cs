#if WINDOWS
using Microsoft.Extensions.Logging.Abstractions;
using Winpepper.Corrections;
using Xunit;
using Xunit.Sdk;

namespace Winpepper.Cleanup.Tests;

// ---------------------------------------------------------------------------
// Model-gated prompt eval suite: runs the REAL model with the REAL production
// prompt (CleanupRunner + BasePrompts.Default via CleanupOptions defaults)
// over the fixed dictation cases in CleanupEvalCases and asserts behavior.
//
// Registry-driven, one pre-provisioned slot class per ModelKind.Cleanup entry
// below. The ~18 cases within a model share one loaded backend (class
// fixture). A new registry entry automatically fills the next slot with ZERO
// test edits; CleanupEvalCasesTests.Registry_CleanupModels_FitWithinEvalSlots
// fails loudly when the registry outgrows the provisioned slots.
//
// SERIAL, not parallel: all slot classes share one non-parallel collection.
// The slots were originally distinct implicit collections so models ran
// concurrently -- fine while only one registry model was installed, but with
// 3+ real GGUFs installed xUnit initializes the class fixtures concurrently
// and parallel LLamaSharp/Vulkan model loads crash natively (fatal error in
// FixtureMappingManager.InitializeAsync, 2026-07-27; same failure family as
// the 0xC0000005 documented on LlamaCleanupBackendIntegrationTests). A single
// GPU serializes the work anyway, so parallel slots bought no wall time.
//
// Determinism: the fixture pins the sampling seed (LlamaCleanupBackend's
// samplingSeed ctor parameter, eval-only; production keeps a random seed) on
// top of the production temperature of 0.1.
// ---------------------------------------------------------------------------

/// <summary>Loads one registry cleanup model once for its slot's eval class.
/// When the slot is empty or the GGUF is absent on disk, records a skip
/// reason instead of throwing, mirroring LlamaCleanupBackendIntegrationTests'
/// self-skip behavior.</summary>
public abstract class CleanupEvalModelFixture : IDisposable
{
    private readonly LlamaCleanupBackend? _backend;

    public string ModelName { get; } = "(none)";
    public string? SkipReason { get; }
    public CleanupRunner? Runner { get; }

    protected CleanupEvalModelFixture(int slot)
    {
        var descriptor = CleanupEvalModels.AtSlot(slot);
        if (descriptor is null)
        {
            SkipReason = $"no cleanup model at registry slot {slot} " +
                         $"(registry has {CleanupEvalModels.CleanupModels.Count} cleanup model(s))";
            return;
        }

        ModelName = descriptor.Name;
        var ggufPath = CleanupEvalModels.GgufPathFor(descriptor);
        if (ggufPath is null)
        {
            SkipReason = $"cleanup model '{descriptor.Name}' declares no .gguf file in the registry";
            return;
        }
        if (!File.Exists(ggufPath))
        {
            SkipReason = $"cleanup model '{descriptor.Name}' not present at {ggufPath}; " +
                         $"install it via the app's Models page or point " +
                         $"{CleanupEvalModels.ModelsRootEnvVar} at a models root";
            return;
        }

        _backend = new LlamaCleanupBackend(ggufPath, new NullLogger<LlamaCleanupBackend>(),
            samplingSeed: 42, promptFormat: descriptor.PromptFormat);
        // Warm once so the first case doesn't pay cold-start cost inside the
        // production 15s timeout budget.
        _backend.WarmAsync(CancellationToken.None).GetAwaiter().GetResult();
        Runner = new CleanupRunner(_backend, new NullLogger<CleanupRunner>(),
            omitPromptExample: descriptor.OmitPromptExample);
    }

    public void Dispose() => _backend?.Dispose();
}

public sealed class CleanupEvalModelFixture0 : CleanupEvalModelFixture { public CleanupEvalModelFixture0() : base(0) { } }
public sealed class CleanupEvalModelFixture1 : CleanupEvalModelFixture { public CleanupEvalModelFixture1() : base(1) { } }
public sealed class CleanupEvalModelFixture2 : CleanupEvalModelFixture { public CleanupEvalModelFixture2() : base(2) { } }
public sealed class CleanupEvalModelFixture3 : CleanupEvalModelFixture { public CleanupEvalModelFixture3() : base(3) { } }

[Trait("Platform", "Windows")]
public abstract class CleanupPromptEvalTestsBase
{
    private readonly CleanupEvalModelFixture _fx;

    protected CleanupPromptEvalTestsBase(CleanupEvalModelFixture fx) => _fx = fx;

    /// <summary>Theory rows for a slot: (modelName, caseName) so failure and
    /// skip output names the model AND the case. An unused slot collapses to a
    /// single skipped sentinel row instead of 18 noisy skips.</summary>
    protected static TheoryData<string, string> RowsForSlot(int slot)
    {
        var rows = new TheoryData<string, string>();
        var descriptor = CleanupEvalModels.AtSlot(slot);
        if (descriptor is null)
        {
            rows.Add($"(no cleanup model at registry slot {slot})", "(slot unused)");
            return rows;
        }
        foreach (var c in CleanupEvalCases.All)
        {
            rows.Add(descriptor.Name, c.Name);
        }
        return rows;
    }

    protected async Task RunCaseAsync(string caseName)
    {
        Assert.SkipWhen(_fx.SkipReason is not null, _fx.SkipReason ?? "cleanup eval unavailable");

        var evalCase = CleanupEvalCases.ByName(caseName);

        // Known-failing baseline (see CleanupEvalCases.KnownFailingBaseline for
        // the policy): the case still RUNS; a failure on a baselined (model,
        // case) pair becomes a dynamic skip carrying the baseline reason plus
        // the fresh failure detail. A non-baselined failure fails loudly.
        try
        {
            await RunAndVerifyAsync(evalCase, caseName);
        }
        catch (Exception ex) when (
            CleanupEvalCases.TryGetBaselineReason(_fx.ModelName, caseName, out var baselineReason))
        {
            Assert.Skip(
                $"KNOWN-FAILING baseline ({_fx.ModelName}/{caseName}): {baselineReason}\n" +
                $"Fresh failure detail: {ex.Message}");
        }

        // Passed while baselined: surface that the entry may be retirable.
        if (CleanupEvalCases.TryGetBaselineReason(_fx.ModelName, caseName, out var staleReason))
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"NOTE: case '{caseName}' PASSED but is in KnownFailingBaseline for " +
                $"'{_fx.ModelName}' — the baseline entry may be retirable (verify across " +
                $"a few runs; the Vulkan GPU path is nondeterministic). Entry: {staleReason}");
        }
    }

    private async Task RunAndVerifyAsync(CleanupEvalCase evalCase, string caseName)
    {
        // Production path end-to-end: CleanupRunner with default options
        // (Ordinary profile => BasePrompts.Default, temp 0.1, 15s timeout,
        // no window context), no corrections, so the runner's preflight and
        // plausibility guards are part of what's evaluated.
        var result = await _fx.Runner!.RunAsync(
            evalCase.RawTranscript,
            CorrectionsData.Empty,
            windowContextTask: null,
            new CleanupOptions(),
            CancellationToken.None);

        // A fallback on an eval case is a failure: the model produced output
        // the production guards rejected (or timed out/errored).
        if (result.Path != CleanupPath.Llm)
        {
            throw new XunitException(
                $"[{_fx.ModelName}] case '{caseName}': runner took fallback path " +
                $"{result.Path} instead of accepting the LLM output.\n" +
                $"Raw transcript: {evalCase.RawTranscript}\n" +
                $"Raw model output: {result.RawModelOutput}");
        }

        try
        {
            evalCase.Verify(result.CleanedText);
        }
        catch (Exception ex)
        {
            throw new XunitException(
                $"[{_fx.ModelName}] case '{caseName}' failed.\n" +
                $"Raw transcript: {evalCase.RawTranscript}\n" +
                $"Cleaned output: {result.CleanedText}\n" +
                ex.Message);
        }
    }
}

// One sealed class per registry slot, all in ONE non-parallel collection so
// model loads (class fixtures) never overlap on the native Vulkan loader --
// see the header comment. Cases within a model reuse the one loaded backend.

[CollectionDefinition("cleanup-eval-models-serial", DisableParallelization = true)]
public sealed class CleanupEvalModelsSerialCollection { }

[Collection("cleanup-eval-models-serial")]
public sealed class CleanupPromptEvalModelSlot0 : CleanupPromptEvalTestsBase, IClassFixture<CleanupEvalModelFixture0>
{
    public CleanupPromptEvalModelSlot0(CleanupEvalModelFixture0 fx) : base(fx) { }

    public static TheoryData<string, string> Rows => RowsForSlot(0);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task Case(string model, string caseName)
    {
        _ = model; // in the row purely so the test display name carries it
        return RunCaseAsync(caseName);
    }
}

[Collection("cleanup-eval-models-serial")]
public sealed class CleanupPromptEvalModelSlot1 : CleanupPromptEvalTestsBase, IClassFixture<CleanupEvalModelFixture1>
{
    public CleanupPromptEvalModelSlot1(CleanupEvalModelFixture1 fx) : base(fx) { }

    public static TheoryData<string, string> Rows => RowsForSlot(1);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task Case(string model, string caseName)
    {
        _ = model;
        return RunCaseAsync(caseName);
    }
}

[Collection("cleanup-eval-models-serial")]
public sealed class CleanupPromptEvalModelSlot2 : CleanupPromptEvalTestsBase, IClassFixture<CleanupEvalModelFixture2>
{
    public CleanupPromptEvalModelSlot2(CleanupEvalModelFixture2 fx) : base(fx) { }

    public static TheoryData<string, string> Rows => RowsForSlot(2);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task Case(string model, string caseName)
    {
        _ = model;
        return RunCaseAsync(caseName);
    }
}

[Collection("cleanup-eval-models-serial")]
public sealed class CleanupPromptEvalModelSlot3 : CleanupPromptEvalTestsBase, IClassFixture<CleanupEvalModelFixture3>
{
    public CleanupPromptEvalModelSlot3(CleanupEvalModelFixture3 fx) : base(fx) { }

    public static TheoryData<string, string> Rows => RowsForSlot(3);

    [Theory]
    [MemberData(nameof(Rows))]
    public Task Case(string model, string caseName)
    {
        _ = model;
        return RunCaseAsync(caseName);
    }
}
#endif
