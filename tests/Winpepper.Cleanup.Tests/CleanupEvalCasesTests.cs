using Shouldly;
using Winpepper.Models;
using Xunit;

namespace Winpepper.Cleanup.Tests;

/// <summary>
/// Pure structural guards for the eval suite. These run on every platform with
/// no model, so a case-set or registry change that would silently invalidate
/// the model-gated eval is caught on the Linux gate too.
/// </summary>
public class CleanupEvalCasesTests
{
    [Fact]
    public void CaseNames_AreUnique()
    {
        CleanupEvalCases.All.Select(c => c.Name).ShouldBeUnique();
    }

    [Fact]
    public void EveryCase_PassesRunnerPreflight_SoTheLlmActuallyRuns()
    {
        // The short-transcript bypass (< 4 words) would silently turn an eval
        // case into a no-op that never exercises the prompt.
        var options = new CleanupOptions();
        foreach (var c in CleanupEvalCases.All)
        {
            CleanupRunner.Preflight(c.RawTranscript, options, cloudTranscript: false)
                .ShouldBeTrue($"eval case '{c.Name}' would bypass the LLM (needs >= 4 words)");
        }
    }

    [Fact]
    public void Registry_HasAtLeastOneCleanupModel()
    {
        CleanupEvalModels.CleanupModels.ShouldNotBeEmpty(
            "the eval suite is registry-driven; an empty cleanup-model list means zero eval coverage");
    }

    [Fact]
    public void Registry_CleanupModels_FitWithinEvalSlots()
    {
        CleanupEvalModels.CleanupModels.Count.ShouldBeLessThanOrEqualTo(
            CleanupEvalModels.SlotCount,
            $"the registry now has more ModelKind.Cleanup entries than eval slots: add a " +
            $"CleanupPromptEvalModelSlot{CleanupEvalModels.SlotCount} class (and fixture) in " +
            $"CleanupPromptEvalTests.cs and bump CleanupEvalModels.SlotCount, or the newest " +
            $"model ships without prompt-eval coverage");
    }

    [Fact]
    public void Registry_CleanupModels_EachDeclareAGgufFile()
    {
        foreach (var descriptor in CleanupEvalModels.CleanupModels)
        {
            CleanupEvalModels.GgufPathFor(descriptor).ShouldNotBeNull(
                $"cleanup model '{descriptor.Name}' declares no .gguf file, so the eval " +
                $"harness cannot load it");
        }
    }

    [Fact]
    public void ModelsRoot_HonorsEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable(CleanupEvalModels.ModelsRootEnvVar);
        try
        {
            var overridePath = Path.Combine(Path.GetTempPath(), "winpepper-eval-models");
            Environment.SetEnvironmentVariable(CleanupEvalModels.ModelsRootEnvVar, overridePath);
            CleanupEvalModels.ModelsRoot.ShouldBe(overridePath);

            var descriptor = CleanupEvalModels.CleanupModels[0];
            CleanupEvalModels.GgufPathFor(descriptor)!
                .ShouldStartWith(overridePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CleanupEvalModels.ModelsRootEnvVar, original);
        }
    }
}
