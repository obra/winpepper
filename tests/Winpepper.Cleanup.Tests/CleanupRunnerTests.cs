using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Cleanup;
using Winpepper.Cleanup.Tests.Fakes;
using Winpepper.Corrections;
using Xunit;

namespace Winpepper.Cleanup.Tests;

public class CleanupRunnerTests
{
    private static CleanupRunner NewRunner(ILlamaCleanupBackend backend) =>
        new(backend, new NullLogger<CleanupRunner>());

    private static CleanupOptions DefaultOptions() => new()
    {
        Profile = CleanupProfile.Ordinary,
        Timeout = TimeSpan.FromSeconds(1),
        WindowContextEnabled = false,
        WindowContextWait = TimeSpan.FromMilliseconds(50),
    };

    // NOTE: every raw transcript here is >= 4 words so the short-transcript
    // bypass (Task 4) does not fire, and outputs share content with the raw so
    // the similarity floor (Task 5) does not fire — these tests isolate the
    // pre-existing LLM/fallback behavior.

    [Fact]
    public async Task Run_ForwardsTheUnwrappedRawTranscriptToTheBackend()
    {
        // Raw-completion prompt formats (CleanupPromptFormatter.RawIo) consume
        // the raw transcript directly; the runner must hand the backend the
        // UNWRAPPED text alongside the PromptBuilder outputs.
        var backend = new FakeLlamaCleanupBackend { Output = "Hello, my name is Crispy." };
        var runner = NewRunner(backend);
        await runner.RunAsync("hello my name is crispy",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        backend.LastRawTranscript.ShouldBe("hello my name is crispy");
        backend.LastUserPrompt.ShouldBe("<USER-INPUT>\nhello my name is crispy\n</USER-INPUT>");
    }

    [Fact]
    public async Task Run_OmitPromptExample_SendsTheExampleFreeDefaultPrompt()
    {
        // Models flagged ModelDescriptor.OmitPromptExample (LFM2.5-1.2B) echo
        // the worked example instead of cleaning; the runner must build the
        // system prompt from BasePrompts.DefaultNoExample for them.
        var backend = new FakeLlamaCleanupBackend { Output = "Hello, my name is Crispy." };
        var runner = new CleanupRunner(backend, new NullLogger<CleanupRunner>(),
            omitPromptExample: true);
        await runner.RunAsync("hello my name is crispy",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        backend.LastSystemPrompt.ShouldNotBeNull();
        backend.LastSystemPrompt!.ShouldContain(BasePrompts.DefaultNoExample);
        backend.LastSystemPrompt!.ShouldNotContain("Output: " + BasePrompts.DefaultExampleOutputs[0]);
    }

    [Fact]
    public async Task Run_LlmEchoesPromptScaffold_FallsBackToRawTranscript()
    {
        var garbage = "<OUTPUT>\nI think we should just ship it tomorrow.\n</OUTPUT>Human: I think we should just ship it tomorrow.";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = garbage });
        var result = await runner.RunAsync("Hello, my name is Crispy. How do you do?",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("Hello, my name is Crispy. How do you do?");
    }

    [Fact]
    public async Task Run_LlmOutputImplausiblyLong_FallsBackToRawTranscript()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = new string('x', 500) });
        var result = await runner.RunAsync("short utterance here now please",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("short utterance here now please");
    }

    [Fact]
    public async Task Run_MarkerSpokenByUser_IsNotRejected()
    {
        // A user who actually dictated "Output:" must not trip the echo guard.
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Output: forty-two." });
        var result = await runner.RunAsync("output colon forty two",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Output: forty-two.");
    }

    [Fact]
    public void LooksLikePromptEcho_ChatTemplateMarkers_Detected()
    {
        CleanupRunner.LooksLikePromptEcho("<|im_start|>assistant hi", "anything").ShouldBeTrue();
        CleanupRunner.LooksLikePromptEcho("### Response: hi", "anything").ShouldBeTrue();
        CleanupRunner.LooksLikePromptEcho("Plain cleaned sentence.", "anything").ShouldBeFalse();
    }

    [Fact]
    public async Task Run_WindowContextOverBudget_LogsTruncationWarning()
    {
        var log = new CollectingLogger<CleanupRunner>();
        var runner = new CleanupRunner(
            new FakeLlamaCleanupBackend { Output = "Hello there world now." }, log);
        var options = DefaultOptions() with { WindowContextEnabled = true };
        var context = Task.FromResult<string?>(
            new string('x', PromptBuilder.WindowContextMaxChars + 1_000));

        await runner.RunAsync("um hello there world now",
            CorrectionsData.Empty, context, options, CancellationToken.None);

        log.Warnings.ShouldContain(w => w.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_WindowContextWithinBudget_LogsNoTruncationWarning()
    {
        var log = new CollectingLogger<CleanupRunner>();
        var runner = new CleanupRunner(
            new FakeLlamaCleanupBackend { Output = "Hello there world now." }, log);
        var options = DefaultOptions() with { WindowContextEnabled = true };
        var context = Task.FromResult<string?>("small window context");

        await runner.RunAsync("um hello there world now",
            CorrectionsData.Empty, context, options, CancellationToken.None);

        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Run_LlmReturnsCleanText_UsesLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Hello world." });
        var result = await runner.RunAsync("um hello there world",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsThinkBlock_StripsItBeforeUsingOutput()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "<think>reasoning</think>Hello world.",
        });
        var result = await runner.RunAsync("hello world okay then",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsEmpty_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "" });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("we tested chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT");
        result.Path.ShouldBe(CleanupPath.FallbackEmpty);
    }

    [Fact]
    public async Task Run_LlmReturnsEllipsis_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "..." });
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("hello world okay then");
        result.Path.ShouldBe(CleanupPath.FallbackEllipsis);
    }

    [Fact]
    public async Task Run_LlmExceedsTimeout_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Delay = TimeSpan.FromSeconds(5),
            Output = "unused",
        });
        var opts = DefaultOptions() with { Timeout = TimeSpan.FromMilliseconds(50) };
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, opts, CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackTimeout);
        result.CleanedText.ShouldBe("hello world okay then");
    }

    [Fact]
    public async Task Run_BackendThrows_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Throw = new InvalidOperationException("kaboom") });
        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("hello world okay then");
    }

    [Fact]
    public async Task Run_AppliesCorrectionPostPass_OnLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "we tested chat gbt." });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("we tested chat gbt today", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_MaxNewTokens_FollowsSpecFormula()
    {
        // Spec §5.5: max_new_tokens = min(2048, ceil(transcript_chars * 2.0)).
        var backend = new FakeLlamaCleanupBackend { Output = "x" };
        var runner = NewRunner(backend);

        var raw124 = string.Join(" ", Enumerable.Repeat("word", 25)); // 25*4 + 24 spaces = 124 chars
        await runner.RunAsync(raw124, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe((int)System.Math.Ceiling(raw124.Length * 2.0)); // 248

        var rawLong = string.Join(" ", Enumerable.Repeat("word", 1250)); // > 2048 tokens by formula
        await runner.RunAsync(rawLong, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task Run_AwaitsWindowContext_UpTo50msThenProceeds()
    {
        var tcs = new TaskCompletionSource<string?>();
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(50),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, tcs.Task, opts, CancellationToken.None);
        sw.Stop();

        result.CleanedText.ShouldBe("cleaned");
        sw.ElapsedMilliseconds.ShouldBeLessThan(500);
    }

    [Fact]
    public async Task Run_UsesWindowContext_WhenReadyInTime()
    {
        var ready = Task.FromResult<string?>("the foreground window says hello");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(500),
        };
        await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastSystemPrompt.ShouldNotBeNull();
        backend.LastSystemPrompt!.ShouldContain("the foreground window says hello");
    }

    [Fact]
    public async Task Run_WindowContextDisabled_OmitsItEvenWhenTaskCompletes()
    {
        var ready = Task.FromResult<string?>("ignored");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { WindowContextEnabled = false };
        await runner.RunAsync("cleaned up this sentence", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastSystemPrompt.ShouldNotBeNull();
        backend.LastSystemPrompt!.ShouldNotContain("ignored");
        backend.LastSystemPrompt!.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public async Task Run_ShortTranscript_BypassesLlm_AndKeepsRaw()
    {
        // Bug-3(c): "Right." must never become the model's ship-it example.
        var backend = new FakeLlamaCleanupBackend { Output = "I think we should just ship it tomorrow." };
        var runner = NewRunner(backend);
        var result = await runner.RunAsync("Right.", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassShort);
        result.CleanedText.ShouldBe("Right.");
        backend.CallCount.ShouldBe(0); // LLM never called
    }

    [Fact]
    public async Task Run_ShortTranscript_StillAppliesCorrectionPostPass()
    {
        var backend = new FakeLlamaCleanupBackend { Output = "ignored" };
        var runner = NewRunner(backend);
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var result = await runner.RunAsync("chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassShort);
        result.CleanedText.ShouldBe("ChatGPT");
        backend.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Run_FourWords_IsNotBypassed()
    {
        var backend = new FakeLlamaCleanupBackend { Output = "Clean up this sentence." };
        var runner = NewRunner(backend);
        var result = await runner.RunAsync("clean up this sentence", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        backend.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Run_WholesaleTruncation_RejectedToFallback()
    {
        // Live case: long question wholesale-replaced by "Me".
        var raw = "Who should be fixing this? Me or the person configuring RunPod?";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Me" });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe(raw);
    }

    [Fact]
    public async Task Run_LegitimateFillerRemoval_IsAccepted()
    {
        // High overlap -> a real cleanup, even though the output equals a
        // former example. Similarity beats blacklisting.
        var raw = "um so like I think we should basically just ship it tomorrow you know";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "I think we should just ship it tomorrow." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("I think we should just ship it tomorrow.");
    }

    [Fact]
    public async Task Run_LegitimateSelfCorrection_IsAccepted()
    {
        // Output matches the retained example, but overlap with raw is high.
        var raw = "write me a function called add_numbers no wait scratch that call it sum";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Write me a function called sum." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Write me a function called sum.");
    }

    [Fact]
    public async Task Run_KnownExampleEcho_WithLowOverlap_IsRejected()
    {
        // Bare few-shot echo, no scaffold markers. 5 words (not >6, so the
        // truncation rule does not apply) with a single shared content word
        // ("sum") so retention is 0.2 (>0, not wholesale) — this must be caught
        // specifically by the known-example guard.
        var raw = "call sum here now please";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Write me a function called sum." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe(raw);
    }

    [Fact]
    public async Task Run_HeavyFillerInput_IsNotFalselyRejected()
    {
        var raw = "um uh like you know basically I really think this is good";
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "I really think this is good." });
        var result = await runner.RunAsync(raw, CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("I really think this is good.");
    }

    [Fact]
    public async Task Run_Disabled_BypassesLlm_AndStillAppliesCorrections()
    {
        // The user's Enabled toggle, read live per dictation: LLM never called,
        // deterministic corrections still run.
        var backend = new FakeLlamaCleanupBackend { Output = "ignored" };
        var runner = NewRunner(backend);
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal) { ["chat gbt"] = "ChatGPT" },
        };
        var opts = DefaultOptions() with { Enabled = false };

        var result = await runner.RunAsync("we tested chat gbt today", corrections, null, opts, CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassDisabled);
        result.CleanedText.ShouldBe("we tested ChatGPT today");
        backend.CallCount.ShouldBe(0); // LLM never called
    }

    [Fact]
    public async Task Run_Disabled_And_Cloud_ReportsDisabledBypass()
    {
        // Precedence pin (spec-owner ruling): the user's Enabled toggle outranks
        // everything — even a cloud transcript reports BypassDisabled when the
        // toggle is off, so the history/log label reflects the user's switch.
        var backend = new FakeLlamaCleanupBackend { Output = "ignored" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { Enabled = false };

        var result = await runner.RunAsync("hello world okay then", CorrectionsData.Empty, null, opts,
            CancellationToken.None, skipLlm: true);

        result.Path.ShouldBe(CleanupPath.BypassDisabled);
        backend.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Run_Disabled_And_Short_ReportsDisabledBypass()
    {
        // Same ruling for the short-transcript bypass: disabled outranks short.
        var backend = new FakeLlamaCleanupBackend { Output = "ignored" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { Enabled = false };

        var result = await runner.RunAsync("Right.", CorrectionsData.Empty, null, opts, CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.BypassDisabled);
        backend.CallCount.ShouldBe(0);
    }

    // Preflight is the SINGLE policy home shared by RunAsync (bypass behavior)
    // and PipelineHost (which engine event to fire — whether the pill shows a
    // "Cleaning up..." phase). These pin the three bypass reasons and the run case.

    [Fact]
    public void Preflight_Disabled_IsFalse()
    {
        var opts = DefaultOptions() with { Enabled = false };
        CleanupRunner.Preflight("clean up this sentence", opts, cloudTranscript: false).ShouldBeFalse();
    }

    [Fact]
    public void Preflight_CloudTranscript_IsFalse()
    {
        CleanupRunner.Preflight("clean up this sentence", DefaultOptions(), cloudTranscript: true).ShouldBeFalse();
    }

    [Fact]
    public void Preflight_ShortTranscript_IsFalse()
    {
        CleanupRunner.Preflight("Right.", DefaultOptions(), cloudTranscript: false).ShouldBeFalse();
    }

    [Fact]
    public void Preflight_EnabledLocalFourWords_IsTrue()
    {
        CleanupRunner.Preflight("clean up this sentence", DefaultOptions(), cloudTranscript: false).ShouldBeTrue();
    }

    [Fact]
    public async Task Preflight_True_Implies_BackendIsCalled_And_False_Implies_NotCalled()
    {
        // The contract PipelineHost relies on: when Preflight says the LLM will
        // run, RunAsync calls the backend; when it says no, it never does.
        var backend = new FakeLlamaCleanupBackend { Output = "Cleaned sentence here now." };
        var runner = NewRunner(backend);

        CleanupRunner.Preflight("clean up this sentence", DefaultOptions(), false).ShouldBeTrue();
        await runner.RunAsync("clean up this sentence", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.CallCount.ShouldBe(1);

        var disabled = DefaultOptions() with { Enabled = false };
        CleanupRunner.Preflight("clean up this sentence", disabled, false).ShouldBeFalse();
        await runner.RunAsync("clean up this sentence", CorrectionsData.Empty, null, disabled, CancellationToken.None);
        backend.CallCount.ShouldBe(1); // unchanged
    }

    [Fact]
    public async Task SkipLlm_RunsDeterministicOnly_NoBackendCall()
    {
        // A backend whose call count we assert stays zero -- proves the LLM path is skipped.
        var backend = new FakeLlamaCleanupBackend { Output = "I think we should just ship it tomorrow." };
        var runner = NewRunner(backend);
        var corrections = CorrectionsData.Empty with
        {
            Replacements = new Dictionary<string, string> { ["kubernettes"] = "Kubernetes" },
        };

        var result = await runner.RunAsync(
            rawTranscript: "deploy to kubernettes now",
            corrections: corrections,
            windowContextTask: null,
            options: DefaultOptions(),
            ct: CancellationToken.None,
            skipLlm: true);

        result.Path.ShouldBe(CleanupPath.BypassProvider);
        result.CleanedText.ShouldBe("deploy to Kubernetes now"); // correction applied deterministically
        backend.CallCount.ShouldBe(0); // LLM never called
    }
}
