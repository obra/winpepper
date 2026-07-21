using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public sealed class FallbackTranscriberTests
{
    private static readonly ReadOnlyMemory<float> Audio = new float[] { 0f, 0f, 0f };

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
    public async Task PrimaryFails_FallsBackToLocalAndRecordsLocalProvider()
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
}
