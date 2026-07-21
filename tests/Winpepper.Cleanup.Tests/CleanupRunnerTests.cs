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

    // Regression tests for prompt-scaffold echo / runaway generation being
    // injected as dictation (raw "Hello, my name is Crispy" came out as
    // "<OUTPUT>I think we should just ship it tomorrow.</OUTPUT>Human: ...").

    [Fact]
    public async Task Run_LlmEchoesPromptScaffold_FallsBackToRawTranscript()
    {
        var garbage = "<OUTPUT>\nI think we should just ship it tomorrow.\n</OUTPUT>Human: I think we should just ship it tomorrow.\n\nOutput: I think we should just ship it tomorrow.";
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
        var result = await runner.RunAsync("short utterance",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackImplausible);
        result.CleanedText.ShouldBe("short utterance");
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
    public async Task Run_LlmReturnsCleanText_UsesLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "Hello world." });
        var result = await runner.RunAsync(
            rawTranscript: "um hello world",
            corrections: CorrectionsData.Empty,
            windowContextTask: null,
            options: DefaultOptions(),
            ct: CancellationToken.None);
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
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("Hello world.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_LlmReturnsEmpty_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "" });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        };
        var result = await runner.RunAsync("we tested chat gbt", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT");
        result.Path.ShouldBe(CleanupPath.FallbackEmpty);
    }

    [Fact]
    public async Task Run_LlmReturnsEllipsis_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "..." });
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("hello");
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
        var result = await runner.RunAsync("hello world", CorrectionsData.Empty, null, opts, CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackTimeout);
        result.CleanedText.ShouldBe("hello world");
    }

    [Fact]
    public async Task Run_BackendThrows_FallsBackToCorrectionOnlyPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Throw = new InvalidOperationException("kaboom"),
        });
        var result = await runner.RunAsync("hello", CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("hello");
    }

    [Fact]
    public async Task Run_AppliesCorrectionPostPass_OnLlmPath()
    {
        var runner = NewRunner(new FakeLlamaCleanupBackend { Output = "we tested chat gbt." });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["chat gbt"] = "ChatGPT",
            },
        };
        var result = await runner.RunAsync("raw", corrections, null, DefaultOptions(), CancellationToken.None);
        result.CleanedText.ShouldBe("we tested ChatGPT.");
        result.Path.ShouldBe(CleanupPath.Llm);
    }

    [Fact]
    public async Task Run_MaxNewTokens_FollowsSpecFormula()
    {
        // Spec §5.5: max_new_tokens = min(2048, ceil(transcript_chars * 2.0))
        // For a 100-char transcript that's min(2048, 200) = 200.
        var backend = new FakeLlamaCleanupBackend { Output = "x" };
        var runner = NewRunner(backend);
        await runner.RunAsync(new string('a', 100), CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(200);

        // For 5000-char transcript = min(2048, 10000) = 2048.
        await runner.RunAsync(new string('a', 5000), CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);
        backend.LastMaxNewTokens.ShouldBe(2048);
    }

    [Fact]
    public async Task Run_AwaitsWindowContext_UpTo500msThenProceeds()
    {
        // The window-context task hangs for 5s; the runner should give up at 50ms.
        var tcs = new TaskCompletionSource<string?>();
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with
        {
            WindowContextEnabled = true,
            WindowContextWait = TimeSpan.FromMilliseconds(50),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await runner.RunAsync("raw", CorrectionsData.Empty, tcs.Task, opts, CancellationToken.None);
        sw.Stop();

        result.CleanedText.ShouldBe("cleaned");
        sw.ElapsedMilliseconds.ShouldBeLessThan(500); // bailed out at ~50ms
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
        await runner.RunAsync("raw", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastPrompt.ShouldNotBeNull();
        backend.LastPrompt!.ShouldContain("the foreground window says hello");
    }

    [Fact]
    public async Task Run_WindowContextDisabled_OmitsItEvenWhenTaskCompletes()
    {
        var ready = Task.FromResult<string?>("ignored");
        var backend = new FakeLlamaCleanupBackend { Output = "cleaned" };
        var runner = NewRunner(backend);
        var opts = DefaultOptions() with { WindowContextEnabled = false };
        await runner.RunAsync("raw", CorrectionsData.Empty, ready, opts, CancellationToken.None);

        backend.LastPrompt.ShouldNotBeNull();
        backend.LastPrompt!.ShouldNotContain("ignored");
        backend.LastPrompt!.ShouldNotContain("<WINDOW-OCR-CONTENT>");
    }

    [Fact]
    public async Task Run_LlmPath_AppNameMishearingCorrected()
    {
        // LLM returns plausible text that still contains the ASR mishearing.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Output = "Testing wheat pepper. How's it going?",
        });
        var result = await runner.RunAsync("testing wheat pepper how's it going",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.Llm);
        result.CleanedText.ShouldBe("Testing Winpepper. How's it going?");
    }

    [Fact]
    public async Task Run_FallbackPath_AppNameMishearingCorrected()
    {
        // Backend throws -> FallbackBackendError -> raw transcript is what gets
        // injected. The app-name correction must still be applied there.
        var runner = NewRunner(new FakeLlamaCleanupBackend
        {
            Throw = new InvalidOperationException("boom"),
        });
        var result = await runner.RunAsync("Testing wheat pepper.",
            CorrectionsData.Empty, null, DefaultOptions(), CancellationToken.None);

        result.Path.ShouldBe(CleanupPath.FallbackBackendError);
        result.CleanedText.ShouldBe("Testing Winpepper.");
    }
}
