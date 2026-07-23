using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class FallbackTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0f, 0f };

    private sealed class BlockingTranscriber : ITranscriber
    {
        public string ModelName => "assemblyai/universal-2";
        public int Calls { get; private set; }
        public async Task<TranscriptionResult> TranscribeAsync(ReadOnlyMemory<float> mono16k, CancellationToken ct)
        {
            Calls++;
            await Task.Delay(Timeout.Infinite, ct); // block until the deadline cancels us
            return new TranscriptionResult("never", ModelName);
        }
    }

    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryResultAndProvider()
    {
        var primary = FakeTranscriber.Returning("assemblyai/universal-2", "hello cloud");
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello cloud");
        result.ProviderModelName.ShouldBe("assemblyai/universal-2");
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToLocal()
    {
        var primary = FakeTranscriber.Throwing("assemblyai/universal-2", new InvalidOperationException("network down"));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        string? notice = null;
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance, msg => notice = msg);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        result.ProviderModelName.ShouldBe("parakeet-tdt-0.6b-v3");
        local.Calls.ShouldBe(1);
        notice.ShouldNotBeNull();
    }

    [Fact]
    public async Task UserCancellation_DoesNotFallBack()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var primary = FakeTranscriber.Throwing("assemblyai/universal-2", new OperationCanceledException(cts.Token));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance);

        await Should.ThrowAsync<OperationCanceledException>(() => fb.TranscribeAsync(Audio, cts.Token));
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CloudDeadline_Elapses_FallsBackToLocalImmediately()
    {
        var primary = new BlockingTranscriber();
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        // scheduleDeadline cancels the cloud CTS immediately (deterministic, no real wait).
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance,
            cloudDeadline: TimeSpan.FromSeconds(10),
            scheduleDeadline: (cts, _) => cts.Cancel());

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        primary.Calls.ShouldBe(1);
        local.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task InvalidModel400_RaisesConfigError_AndFallsBack()
    {
        var primary = FakeTranscriber.Throwing("assemblyai/universal-9000",
            new AssemblyAiException("AssemblyAI request failed (400): {\"error\":\"invalid speech_model\"}", 400));
        var local = FakeTranscriber.Returning("parakeet-tdt-0.6b-v3", "hello local");
        string? configError = null;
        var fb = new FallbackTranscriber(primary, local, NullLogger<FallbackTranscriber>.Instance,
            onConfigError: msg => configError = msg);

        var result = await fb.TranscribeAsync(Audio, CancellationToken.None);

        result.Text.ShouldBe("hello local");
        configError.ShouldNotBeNull();
        configError!.ShouldContain("speech_model");
    }
}
