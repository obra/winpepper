using Shouldly;
using Winpepper.Platform.WindowContext;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public class WindowContextStampTests
{
    [Fact]
    public void NullConsumed_OmitsField() =>
        WindowContextStamp.CtxSrc(null, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBeNull();

    [Fact]
    public void NotConsumedInTime_IsNone_RegardlessOfWhatItLaterProduced() =>
        WindowContextStamp.CtxSrc(false, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBe("none");

    [Fact]
    public void ConsumedUia_IsUia() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.FromUia("x"))).ShouldBe("uia");

    [Fact]
    public void ConsumedOcr_IsOcr() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.FromOcr("x", 0.9))).ShouldBe("ocr");

    [Fact]
    public void ConsumedEmpty_IsNone() =>
        WindowContextStamp.CtxSrc(true, Task.FromResult(WindowContextResult.Empty)).ShouldBe("none");

    [Fact]
    public void ConsumedButCancelled_IsNone()
    {
        var cancelled = Task.FromCanceled<WindowContextResult>(new CancellationToken(canceled: true));
        WindowContextStamp.CtxSrc(true, cancelled).ShouldBe("none");
    }

    [Fact]
    public void NoTaskAtAll_IsNone_WhenARunnerSomehowReportsConsumption() =>
        WindowContextStamp.CtxSrc(true, null).ShouldBe("none");
}
