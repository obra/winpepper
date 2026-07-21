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
}
