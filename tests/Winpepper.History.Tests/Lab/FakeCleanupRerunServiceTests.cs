using Shouldly;
using Winpepper.Corrections;
using Winpepper.History.Lab;
using Xunit;

namespace Winpepper.History.Tests.Lab;

public class FakeCleanupRerunServiceTests
{
    [Fact]
    public async Task RerunAsync_ReturnsAssembledPromptAndCleanedText()
    {
        var svc = new FakeCleanupRerunService();
        var input = new CleanupRerunInput
        {
            RawTranscript = "hello world",
            ModelName = "qwen-test",
            ModelPath = "/tmp/qwen-test.gguf",
        };
        var result = await svc.RerunAsync(input, CancellationToken.None);
        result.ModelName.ShouldBe("qwen-test");
        result.AssembledPrompt.ShouldContain("hello world");
        result.CleanedText.ShouldContain("hello world");
    }

    [Fact]
    public async Task RerunAsync_HonorsCustomProduce()
    {
        var svc = new FakeCleanupRerunService(_ => ("P", "R", "C"));
        var result = await svc.RerunAsync(new CleanupRerunInput
        {
            RawTranscript = "x", ModelName = "m", ModelPath = "/tmp/m.gguf",
        }, CancellationToken.None);
        result.AssembledPrompt.ShouldBe("P");
        result.RawOutput.ShouldBe("R");
        result.CleanedText.ShouldBe("C");
    }

    [Fact]
    public async Task RerunAsync_PassesCorrectionsThrough()
    {
        CleanupRerunInput? captured = null;
        var svc = new FakeCleanupRerunService(i => { captured = i; return ("p", "r", "c"); });
        var corrections = new CorrectionsData
        {
            Replacements = new Dictionary<string, string> { ["chat gbt"] = "ChatGPT" },
        };
        await svc.RerunAsync(new CleanupRerunInput
        {
            RawTranscript = "we tested chat gbt",
            ModelName = "m",
            ModelPath = "/tmp/m.gguf",
            Corrections = corrections,
        }, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Corrections.Replacements.ContainsKey("chat gbt").ShouldBeTrue();
    }
}
