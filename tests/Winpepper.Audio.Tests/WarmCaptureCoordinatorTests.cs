using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class WarmCaptureCoordinatorTests
{
    private static WarmCaptureCoordinator NewCoordinator(
        Func<ICaptureSource> factory, out WarmCaptureBuffer buffer)
    {
        buffer = new WarmCaptureBuffer(ringCapacitySamples: 16000);
        return new WarmCaptureCoordinator(buffer, factory);
    }

    [Fact]
    public void EnsureStarted_StartsExactlyOneSource()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out _);

        c.EnsureStarted();
        c.EnsureStarted(); // idempotent

        made.Count.ShouldBe(1);
        made[0].Started.ShouldBeTrue();
        c.IsRunning.ShouldBeTrue();
        c.ActiveDeviceId.ShouldBe("fake-device");
    }

    [Fact]
    public void Frames_RouteToBuffer_AndReRaiseOnlyDuringSession()
    {
        FakeCaptureSource? src = null;
        var c = NewCoordinator(() => src = new FakeCaptureSource(), out var buffer);
        var reRaised = new List<float>();
        c.FramesAvailable += f => reRaised.AddRange(f.ToArray());

        c.EnsureStarted();
        src!.RaiseFrame(new float[] { 1, 2 });   // idle: ring only, no re-raise
        buffer.StartSession(prerollSamples: 0);
        src!.RaiseFrame(new float[] { 3, 4 });   // active: re-raised
        var session = buffer.StopSession();

        reRaised.ShouldBe(new float[] { 3, 4 });
        session.ShouldBe(new float[] { 3, 4 });
    }

    [Fact]
    public void Rebuild_DisposesOldSource_ClearsRing_AndStartsNew()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out var buffer);

        c.EnsureStarted();
        made[0].RaiseFrame(new float[] { 9, 9, 9 }); // stale-device audio into the ring
        c.Rebuild();

        made.Count.ShouldBe(2);
        made[0].Disposed.ShouldBeTrue();   // old disposed
        made[1].Started.ShouldBeTrue();    // new started
        c.ActiveDeviceId.ShouldBe("fake-device");

        // Ring was cleared on rebuild: a session started now sees no stale audio.
        buffer.StartSession(prerollSamples: 16000);
        buffer.StopSession().ShouldBeEmpty();
    }

    [Fact]
    public void StartLocked_DisposesPartialSource_WhenStartThrows()
    {
        FakeCaptureSource? src = null;
        var c = NewCoordinator(() => src = new FakeCaptureSource { ThrowOnStart = true }, out _);

        c.EnsureStarted();          // must swallow the throw, not leak the source

        c.IsRunning.ShouldBeFalse();
        src!.Disposed.ShouldBeTrue(); // partial source disposed (Bug 5)
    }

    [Fact]
    public void StaleSourceFrame_AfterRebuild_IsIgnored_NoDisposedAccess()
    {
        var made = new List<FakeCaptureSource>();
        var c = NewCoordinator(() => { var s = new FakeCaptureSource(); made.Add(s); return s; }, out var buffer);
        c.EnsureStarted();
        var old = made[0];
        c.Rebuild();

        // A late frame from the disposed old source must be dropped by the epoch
        // guard without ever routing into the buffer.
        buffer.StartSession(0);
        old.RaiseFrame(new float[] { 5 });
        buffer.StopSession().ShouldBeEmpty();
    }
}
