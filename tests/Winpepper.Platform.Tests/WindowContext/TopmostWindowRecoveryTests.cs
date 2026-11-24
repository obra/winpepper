using Shouldly;
using Winpepper.App.Views.Native;
using Xunit;

namespace Winpepper.Platform.Tests.WindowContext;

public sealed class TopmostWindowRecoveryTests
{
    private static readonly IntPtr Hwnd = new(123);

    [Fact]
    public void SuccessfulNativePromotion_DoesNotResetTheWindow()
    {
        var requests = new List<bool>();
        var result = TopmostWindowRecovery.Assert(Hwnd,
            _ => TopmostWindowRecovery.TopmostStyle,
            (hwnd, topmost) =>
            {
                hwnd.ShouldBe(Hwnd);
                requests.Add(topmost);
                return new(true, 0);
            });

        requests.ShouldBe(new[] { true });
        result.IsTopmost.ShouldBeTrue();
        result.RecoveryAttempted.ShouldBeFalse();
    }

    [Fact]
    public void SuccessWithoutTopmostFlag_ResetsThenPromotesAndVerifiesAgain()
    {
        var requests = new List<bool>();
        var styles = new Queue<long>(new[] { 0x080800A0L, 0x080800A8L });
        var result = TopmostWindowRecovery.Assert(Hwnd,
            _ => styles.Dequeue(),
            (_, topmost) =>
            {
                requests.Add(topmost);
                return new(true, 0); // The first call lies, as in tray-only startup.
            });

        requests.ShouldBe(new[] { true, false, true });
        styles.ShouldBeEmpty();
        result.RecoveryAttempted.ShouldBeTrue();
        result.IsTopmost.ShouldBeTrue();
        result.InitialStyle.ShouldBe(0x080800A0L);
        result.FinalStyle.ShouldBe(0x080800A8L);
    }

    [Fact]
    public void RecoveryWithoutTopmostFlag_ReportsFailureEvenWhenApisReturnSuccess()
    {
        var result = TopmostWindowRecovery.Assert(Hwnd, _ => 0x08080080L, (_, _) => new(true, 0));

        result.RecoveryAttempted.ShouldBeTrue();
        result.IsTopmost.ShouldBeFalse();
        result.RetryRequest!.Value.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void FailedNativeRequests_PreserveErrorsAndStillAttemptFinalPromotion()
    {
        var requests = new List<bool>();
        var results = new Queue<WindowPositionResult>(new[]
        {
            new WindowPositionResult(false, 5),
            new WindowPositionResult(false, 1400),
            new WindowPositionResult(false, 87),
        });
        var result = TopmostWindowRecovery.Assert(Hwnd, _ => 0,
            (_, topmost) => { requests.Add(topmost); return results.Dequeue(); });

        requests.ShouldBe(new[] { true, false, true });
        result.IsTopmost.ShouldBeFalse();
        result.InitialRequest.ShouldBe(new(false, 5));
        result.ResetRequest.ShouldBe(new WindowPositionResult(false, 1400));
        result.RetryRequest.ShouldBe(new WindowPositionResult(false, 87));
    }
}
