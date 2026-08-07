using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

public class EmReplaceSelStrategyTests
{
    private static EmReplaceSelStrategy NewStrategy(
        Func<long, string?> className,
        Func<long, bool>? emGetSelProbe = null,
        Func<long, string, bool>? sendReplaceSel = null)
        => new(
            NullLogger.Instance,
            className: className,
            emGetSelProbe: emGetSelProbe ?? (_ => true),
            sendReplaceSel: sendReplaceSel ?? ((_, _) => true));

    [Fact]
    public void Channel_IsEmReplaceSel()
    {
        NewStrategy(_ => "Edit").Channel.ShouldBe(DeliveryChannel.EmReplaceSel);
    }

    [Theory]
    [InlineData("Edit")]              // classic EDIT
    [InlineData("RICHEDIT50W")]       // rich edit
    [InlineData("RichEditD2DPT")]     // Win11 Notepad
    public void Gate_Passes_WhenClassContainsEdit_CaseInsensitive(string cls)
    {
        NewStrategy(_ => cls).CanDeliver(42, 7).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Chrome_RenderWidgetHostHWND")]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]
    [InlineData(null)]
    public void Gate_Fails_WhenClassDoesNotContainEdit(string? cls)
    {
        NewStrategy(_ => cls).CanDeliver(42, 7).ShouldBeFalse();
    }

    [Fact]
    public void Gate_Fails_WhenEmGetSelProbeFails()
    {
        NewStrategy(_ => "Edit", emGetSelProbe: _ => false).CanDeliver(42, 7).ShouldBeFalse();
    }

    [Fact]
    public void Gate_Fails_OnZeroFocusedChild_WithoutProbing()
    {
        // 0 encodes "unstable or no focused child" (pinned decision #2) —
        // the gate must fail closed without touching the target.
        var strategy = NewStrategy(
            _ => throw new InvalidOperationException("must not probe class"),
            emGetSelProbe: _ => throw new InvalidOperationException("must not probe EM_GETSEL"));
        strategy.CanDeliver(42, 0).ShouldBeFalse();
    }

    [Fact]
    public void Gate_ProbesTheFocusedChild_NotTheForeground()
    {
        var probed = new List<long>();
        var strategy = NewStrategy(
            h => { probed.Add(h); return "Edit"; },
            emGetSelProbe: h => { probed.Add(h); return true; });
        strategy.CanDeliver(42, 7).ShouldBeTrue();
        probed.ShouldBe(new[] { 7L, 7L });
    }

    [Fact]
    public void TrySendChunk_DelegatesToReplaceSel_AndReportsResult()
    {
        var sent = new List<(long Hwnd, string Chunk)>();
        var strategy = NewStrategy(_ => "Edit",
            sendReplaceSel: (h, c) => { sent.Add((h, c)); return true; });

        strategy.TrySendChunk(7, "hello wo").ShouldBeTrue();
        sent.ShouldBe(new[] { (7L, "hello wo") });
    }

    [Fact]
    public void TrySendChunk_False_OnRefusedSend()
    {
        NewStrategy(_ => "Edit", sendReplaceSel: (_, _) => false)
            .TrySendChunk(7, "hello wo").ShouldBeFalse();
    }
}
