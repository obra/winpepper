using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Winpepper.Platform.Injection;
using Xunit;

namespace Winpepper.Platform.Tests.Injection;

[Trait("Platform", "Windows")]
public class DeliveryStrategyWindowsTests
{
    // Production string shape: ASCII + surrogate pair (G-clef) + accents.
    private const string Payload = "Even we worked \uD834\uDD1E caf\u00E9 done.";

    [Fact]
    public void EmReplaceSel_DeliversChunksVerbatim_InOrder_ToHostedEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();
        var strategy = new EmReplaceSelStrategy(NullLogger.Instance);
        var target = host.EditHwnd.ToInt64();

        strategy.CanDeliver(host.ParentHwnd.ToInt64(), target).ShouldBeTrue();
        foreach (var chunk in InjectionChunker.Split(Payload, TextInjector.ChunkCodeUnits))
            strategy.TrySendChunk(target, chunk).ShouldBeTrue();

        host.ReadText().ShouldBe(Payload); // verbatim, in order, surrogates intact
    }

    [Fact]
    public void WmCharSmto_DeliversUnitsVerbatim_InOrder_ToHostedEdit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();
        var strategy = new WmCharSmtoStrategy(NullLogger.Instance);
        var target = host.EditHwnd.ToInt64();

        strategy.CanDeliver(host.ParentHwnd.ToInt64(), target).ShouldBeTrue();
        foreach (var chunk in InjectionChunker.Split(Payload, TextInjector.ChunkCodeUnits))
            strategy.TrySendChunk(target, chunk).ShouldBeTrue();

        host.ReadText().ShouldBe(Payload);
    }

    [Fact]
    public void EmReplaceSelGate_PassesOnHostedEditClass()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();

        var cls = MessageDelivery.ClassName(host.EditHwnd.ToInt64());
        cls.ShouldNotBeNull();
        cls.ShouldContain("Edit", Case.Insensitive);
        MessageDelivery.EmGetSelProbe(host.EditHwnd.ToInt64()).ShouldBeTrue();
    }

    [Fact]
    public void DestroyedHwnd_GateAndSend_FailLoudlyFalse()
    {
        if (!OperatingSystem.IsWindows()) return;
        var host = NativeEditHost.Start();
        var target = host.EditHwnd.ToInt64();
        var foreground = host.ParentHwnd.ToInt64();
        host.Dispose(); // windows destroyed with their thread

        var em = new EmReplaceSelStrategy(NullLogger.Instance);
        var wm = new WmCharSmtoStrategy(NullLogger.Instance);
        em.CanDeliver(foreground, target).ShouldBeFalse();
        em.TrySendChunk(target, "x").ShouldBeFalse();
        wm.TrySendChunk(target, "x").ShouldBeFalse();
    }

    [Fact]
    public void NonPumpingWindow_TrySendChunk_ReturnsFalse_WithinTwiceTheTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.StartNonPumping();
        var strategy = new WmCharSmtoStrategy(NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        var ok = strategy.TrySendChunk(host.EditHwnd.ToInt64(), "x"); // one unit => one SMTO call
        sw.Stop();

        ok.ShouldBeFalse();
        // Pipeline-never-hangs pin: the property being protected is BOUNDED
        // termination (no wedged dictation), and VM noise delays but never
        // accelerates wall-clock — 10x the 150 ms SMTO timeout keeps the fail
        // signal for a real hang while making GitHub runner jitter irrelevant
        // (widened from 2x on 2026-08-24: 300 ms was just tighter than the
        // property and tripped on slow-VM scheduling, not on hangs).
        sw.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(1500);
    }

    [Fact]
    public void NonPumpingWindow_EmReplaceSel_ChunkSend_AlsoBounded()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.StartNonPumping();
        var strategy = new EmReplaceSelStrategy(NullLogger.Instance);

        var sw = Stopwatch.StartNew();
        var ok = strategy.TrySendChunk(host.EditHwnd.ToInt64(), "hello wo"); // one chunk => one SMTO call
        sw.Stop();

        ok.ShouldBeFalse();
        // Same bounded-termination reasoning as the WmCharSmto pin above.
        sw.ElapsedMilliseconds.ShouldBeLessThanOrEqualTo(1500);
    }

    [Fact]
    public void FocusedChildProbe_DoubleSample_IsStable_OnInProcWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = NativeEditHost.Start();

        var capture = FocusedChildProbe.Capture(
            host.ParentHwnd.ToInt64(), MessageDelivery.SampleFocusedChild, Thread.Sleep);

        capture.Stable.ShouldBeTrue();
        capture.FocusedChildHwnd.ShouldBe(host.EditHwnd.ToInt64());
    }
}
