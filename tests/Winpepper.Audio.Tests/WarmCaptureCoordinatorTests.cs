using System.Linq;
using System.Threading;
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

    [Fact]
    public void ConcurrencyHammer_RebuildVsFrames_NeverThrows()
    {
        // A rolling registry of sources so the frame thread can fire callbacks
        // from whichever source is (or just was) live, exactly the race the
        // council could not settle statically.
        var live = new System.Collections.Concurrent.ConcurrentBag<FakeCaptureSource>();
        FakeCaptureSource Make() { var s = new FakeCaptureSource(); live.Add(s); return s; }

        var buffer = new WarmCaptureBuffer(ringCapacitySamples: 4000);
        using var c = new WarmCaptureCoordinator(buffer, Make);
        c.EnsureStarted();
        buffer.StartSession(0);

        Exception? escaped = null;
        var stop = false;
        var frame = new float[] { 0.1f, -0.1f, 0.2f, -0.2f };

        var frameThread = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    // Fire frames from every source ever made — including ones
                    // that were just disposed by a concurrent Rebuild. The fake's
                    // RaiseFrame never throws on its own; the ONLY way an
                    // exception escapes here is if the coordinator touches a
                    // disposed source (which the epoch guard must prevent).
                    foreach (var s in live.ToArray())
                        s.RaiseFrame(frame);
                }
            }
            catch (Exception ex) { escaped = ex; }
        });

        var rebuildThread = new Thread(() =>
        {
            try { for (var i = 0; i < 5000; i++) c.Rebuild(); }
            catch (Exception ex) { escaped = ex; }
            finally { Volatile.Write(ref stop, true); }
        });

        frameThread.Start();
        rebuildThread.Start();
        // Bounded joins: a genuine deadlock inside Rebuild()/OnSourceFrame must
        // fail this test fast and loudly instead of hanging the whole run.
        rebuildThread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        frameThread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();

        escaped.ShouldBeNull();

        // Deterministic post-race phase: the assertion above only proves the
        // race didn't crash — it never inspects buffer contents, so it would
        // still pass even if the epoch guard in OnSourceFrame were deleted
        // entirely. Prove the stale-bleed guarantee explicitly, the same way
        // StaleSourceFrame_AfterRebuild_IsIgnored_NoDisposedAccess does, but
        // right after the concurrency churn above.
        //
        // At any quiescent point (no Rebuild/EnsureStarted in flight) exactly
        // one FakeCaptureSource in `live` is non-disposed: Rebuild disposes
        // the prior current source and starts a new one atomically under the
        // coordinator's lock, and this test never disposes a source any other
        // way. That makes Single(...) an order-independent way to pick out
        // "the source that is current right now" even though `live` is a
        // ConcurrentBag with no defined enumeration order.
        var staleSource = live.ToArray().Single(s => !s.Disposed);
        c.Rebuild(); // staleSource becomes stale/disposed; a fresh source becomes current
        var currentSource = live.ToArray().Single(s => !s.Disposed);

        buffer.StartSession(prerollSamples: 0);
        staleSource.RaiseFrame(new float[] { 42 });   // stale-device audio: must be dropped
        currentSource.RaiseFrame(new float[] { 7 });  // current-device audio: must land
        buffer.StopSession().ShouldBe(new float[] { 7 });
    }
}
