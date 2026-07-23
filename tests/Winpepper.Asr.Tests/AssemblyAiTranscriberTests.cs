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

    private static AssemblyAiTranscriber Make(
        FakeAssemblyAiClient client, bool hasKey = true,
        int deadlineSec = 10, TimeSpan? poll = null,
        List<TimeSpan>? delays = null,
        Func<AssemblyAiRequestExtras>? extras = null,
        bool delete = true)
    {
        var opts = new AssemblyAiOptions
        {
            Model = "universal-2",
            CloudDeadline = TimeSpan.FromSeconds(deadlineSec),
            PollInterval = poll ?? TimeSpan.FromSeconds(1),
            FirstPollDelay = TimeSpan.FromMilliseconds(750),
            DeleteAfterTranscribe = delete,
        };
        return new AssemblyAiTranscriber(
            client, new StubKeyStore { HasKey = hasKey }, opts,
            NullLogger<AssemblyAiTranscriber>.Instance,
            delay: (ts, _) => { delays?.Add(ts); return Task.CompletedTask; },
            extrasProvider: extras,
            scheduleDetached: a => a().GetAwaiter().GetResult()); // run inline for determinism
    }

    [Fact]
    public async Task HappyPath_FirstPollGrace_ReturnsText_AndDeletes()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("processing", null, null, null, null))
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hello from the cloud", 0.95, 4.0, null));
        var delays = new List<TimeSpan>();
        var t = Make(client, delays: delays);

        var result = await t.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello from the cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        client.RiffMagic().ShouldBe("RIFF");
        delays[0].ShouldBe(TimeSpan.FromMilliseconds(750)); // first-poll grace precedes poll #1
        client.Deleted.ShouldContain("t-fake");             // retention cleanup issued
    }

    [Fact]
    public async Task DeleteDisabled_DoesNotDelete()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("completed", "hi", 0.9, 1.0, null));
        var t = Make(client, delete: false);
        await t.TranscribeAsync(Audio, CancellationToken.None);
        client.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task UnrecognizedStatus_DoesNotDropCompletion()
    {
        // A weird status must NOT abort or silently drop the eventual completion.
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("123", null, null, null, null))   // coerced non-string
            .EnqueuePoll(new AssemblyAiTranscript("completed", "recovered", 0.9, 2.0, null));
        var t = Make(client);
        var result = await t.TranscribeAsync(Audio, CancellationToken.None);
        result.Text.ShouldBe("recovered");
    }

    [Fact]
    public async Task ErrorStatus_Throws()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("error", null, null, null, "Transcoding failed"));
        var t = Make(client);
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("Transcoding failed");
    }

    [Fact]
    public async Task NeverCompletes_TimesOutAfterPollBudget()
    {
        var client = new FakeAssemblyAiClient(); // always "processing"
        var t = Make(client, deadlineSec: 3, poll: TimeSpan.FromSeconds(1));
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.Message.ShouldContain("timed out");
        client.PollCalls.ShouldBe(3); // ceil(3s / 1s)
    }

    [Fact]
    public async Task NoKey_ThrowsAuthError()
    {
        var client = new FakeAssemblyAiClient();
        var t = Make(client, hasKey: false);
        var ex = await Should.ThrowAsync<AssemblyAiException>(() => t.TranscribeAsync(Audio, CancellationToken.None));
        ex.IsAuthError.ShouldBeTrue();
    }

    [Fact]
    public async Task PassesExtrasToCreate()
    {
        var client = new FakeAssemblyAiClient()
            .EnqueuePoll(new AssemblyAiTranscript("completed", "x", 0.9, 1.0, null));
        var extras = new AssemblyAiRequestExtras(
            new[] { new AssemblyAiCustomSpelling(new[] { "winpeper" }, "Winpepper") },
            Array.Empty<string>());
        var t = Make(client, extras: () => extras);
        await t.TranscribeAsync(Audio, CancellationToken.None);
        client.LastExtras.ShouldBe(extras);
    }
}
