using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Asr.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests;

public class FallbackStreamingTranscriberTests
{
    private static FallbackStreamingTranscriber Wrap(
        FakeStreamingTranscriber primary, FakeTranscriber local,
        Action<string>? onFallback = null, Action<string>? onConfigError = null,
        Action<CancellationTokenSource, TimeSpan>? scheduleDeadline = null)
        => new(primary, local, NullLogger<FallbackStreamingTranscriber>.Instance,
            onFallback: onFallback, cloudDeadline: TimeSpan.FromSeconds(10),
            onConfigError: onConfigError,
            scheduleDeadline: scheduleDeadline ?? ((_, _) => { }));

    [Fact]
    public async Task HappyPath_ReturnsTheCloudResult()
    {
        var primary = new FakeStreamingTranscriber("assemblyai/universal-streaming");
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken);
        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("CLOUD");
        local.Calls.ShouldBe(0);
        f.ModelName.ShouldBe("assemblyai/universal-streaming");
    }

    [Fact]
    public async Task StartFailure_FallsBackToLocalAtFinish()
    {
        string? notice = null;
        var primary = new FakeStreamingTranscriber("cloud") { ThrowOnStart = new AssemblyAiException("connect refused") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, onFallback: n => notice = n);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken); // must NOT throw
        var result = await session.FinishAsync(new float[100], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("LOCAL");
        local.Calls.ShouldBe(1);
        notice.ShouldNotBeNull();
    }

    private sealed class HangingStartTranscriber : IStreamingTranscriber
    {
        public string ModelName => "cloud";
        public async Task<IStreamingTranscriptionSession> StartSessionAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct); // a wedged ConnectAsync HANGS, honoring only ct
            throw new UnreachableException();
        }
    }

    [Fact]
    public async Task StartHang_IsBoundedByTheConnectDeadline_ThenLocalRunsAtFinish()
    {
        string? notice = null;
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = new FallbackStreamingTranscriber(
            new HangingStartTranscriber(), local, NullLogger<FallbackStreamingTranscriber>.Instance,
            onFallback: n => notice = n, cloudDeadline: TimeSpan.FromSeconds(10),
            scheduleDeadline: (cts, _) => cts.Cancel()); // connect deadline fires immediately

        // Must NOT hang and must NOT throw: the connect deadline (not the user
        // token) cancelled the connect, so this degrades to a failed-mode session.
        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        var result = await session.FinishAsync(new float[100], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("LOCAL");
        local.Calls.ShouldBe(1);
        notice.ShouldNotBeNull();
    }

    [Fact]
    public async Task MidStreamPushFailure_IsSwallowed_AndLocalRunsAtFinish()
    {
        var primary = new FakeStreamingTranscriber("cloud") { ThrowOnPush = new AssemblyAiException("socket died") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        await session.PushAsync(new float[800], TestContext.Current.CancellationToken); // must NOT throw
        var result = await session.FinishAsync(new float[800], TestContext.Current.CancellationToken);

        result.Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task FinishFailure_FallsBackToLocal()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = (_, _) => throw new AssemblyAiException("processing failed") };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task CloudDeadline_FiresOnThePostStopWait_ThenLocalRuns()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        {
            OnFinish = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); },
        };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, scheduleDeadline: (cts, _) => cts.Cancel()); // deadline fires immediately

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
    }

    [Fact]
    public async Task UserCancellation_Rethrows_WithoutRunningLocal()
    {
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new UnreachableException(); } };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local);

        using var userCts = new CancellationTokenSource();
        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        var finish = session.FinishAsync(new float[800], userCts.Token);
        userCts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => finish);
        local.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task InvalidModel400_RaisesConfigError_AndFallsBack()
    {
        string? configError = null;
        var primary = new FakeStreamingTranscriber("cloud")
        { OnFinish = (_, _) => throw new AssemblyAiException("unsupported model", statusCode: 400) };
        var local = FakeTranscriber.Returning("local", "LOCAL");
        var f = Wrap(primary, local, onConfigError: msg => configError = msg);

        await using var session = await f.StartSessionAsync(TestContext.Current.CancellationToken);
        (await session.FinishAsync(new float[800], TestContext.Current.CancellationToken)).Text.ShouldBe("LOCAL");
        configError.ShouldNotBeNull();
    }

    private sealed class UnreachableException : Exception { }
}
