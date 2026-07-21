using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class AssemblyAiTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0.5f, -0.5f, 0f };

    private sealed class StubKeyStore : IAssemblyAiKeyStore
    {
        public bool HasKey { get; init; } = true;
        public void Save(string apiKey) { }
        public string? Load() => HasKey ? "KEY" : null;
        public void Clear() { }
    }

    private static AssemblyAiTranscriber Make(FakeAssemblyAiClient client, bool hasKey = true, TimeSpan? total = null, TimeSpan? poll = null)
    {
        var opts = new AssemblyAiOptions
        {
            Model = "universal-2",
            TotalTimeout = total ?? TimeSpan.FromSeconds(45),
            PollInterval = poll ?? TimeSpan.FromSeconds(1),
        };
        return new AssemblyAiTranscriber(client, new StubKeyStore { HasKey = hasKey }, opts,
            NullLogger<AssemblyAiTranscriber>.Instance, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task HappyPath_UploadsWavAndReturnsTextWithProviderModel()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("processing", null, null, null, null))
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hello from the cloud", 0.95, 4.0, null));
        var transcriber = Make(client);

        var result = await transcriber.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello from the cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        client.RiffMagic().ShouldBe("RIFF"); // uploaded a real WAV
    }

    [Fact]
    public async Task ErrorStatus_ThrowsWithApiMessage()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("error", null, null, null, "Transcoding failed"));
        var transcriber = Make(client);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("Transcoding failed");
    }

    [Fact]
    public async Task NeverCompletes_TimesOutAfterPollBudget()
    {
        var client = new FakeAssemblyAiClient(); // always "processing"
        var transcriber = Make(client, total: TimeSpan.FromSeconds(3), poll: TimeSpan.FromSeconds(1));

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("timed out");
        client.PollCalls.ShouldBe(3); // ceil(3s / 1s)
    }

    [Fact]
    public async Task NoKey_ThrowsAuthError()
    {
        var client = new FakeAssemblyAiClient();
        var transcriber = Make(client, hasKey: false);

        var ex = await Should.ThrowAsync<AssemblyAiException>(() => transcriber.TranscribeAsync(Audio, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
    }
}
