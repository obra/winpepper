using Shouldly;
using Winpepper.Audio;
using Xunit;

namespace Winpepper.Audio.Tests;

public class WarmCaptureBufferTests
{
    private static float[] Ramp(int from, int count)
    {
        var a = new float[count];
        for (var i = 0; i < count; i++) a[i] = from + i;
        return a;
    }

    [Fact]
    public void Ingest_TrimsOldestBeyondCapacity_PrerollTakesMostRecent()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 15)); // 0..14; only last 10 (5..14) survive

        buf.StartSession(prerollSamples: 10);
        var session = buf.StopSession();

        session.ShouldBe(Ramp(5, 10)); // 5..14
    }

    [Fact]
    public void StartSession_SeedsPreroll_ThenAppendsLiveFrames()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2, 3 });

        buf.StartSession(prerollSamples: 100); // takes all available (3)
        buf.Ingest(new float[] { 4, 5 });
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public void StartSession_PrerollLargerThanAvailable_TakesWhatExists()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2 });

        buf.StartSession(prerollSamples: 100);
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 1, 2 });
    }

    [Fact]
    public void Ingest_WhileInactive_DoesNotAppendToSession()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1 });          // inactive, ring only
        buf.StartSession(prerollSamples: 0);    // no preroll
        buf.Ingest(new float[] { 2 });          // active -> session
        var session = buf.StopSession();

        session.ShouldBe(new float[] { 2 });
    }

    [Fact]
    public void SecondSession_ResetsSessionBuffer()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[] { 1, 2 });
        buf.StopSession().ShouldBe(new float[] { 1, 2 });

        buf.StartSession(0);
        buf.Ingest(new float[] { 9 });
        buf.StopSession().ShouldBe(new float[] { 9 });
    }

    [Fact]
    public void IsSessionActive_TracksStartAndStop()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.IsSessionActive.ShouldBeFalse();
        buf.StartSession(0);
        buf.IsSessionActive.ShouldBeTrue();
        buf.StopSession();
        buf.IsSessionActive.ShouldBeFalse();
    }

    [Fact]
    public void Clear_DropsRing_SoNextSessionHasNoStalePreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.Ingest(new float[] { 1, 2, 3 }); // stale-device audio

        buf.Clear();                          // device rebuilt -> ring invalid

        buf.StartSession(prerollSamples: 100);
        buf.Ingest(new float[] { 4, 5 });     // only new-device audio
        buf.StopSession().ShouldBe(new float[] { 4, 5 });
    }

    [Fact]
    public void Clear_WhileSessionActive_DropsRingButKeepsSessionUsable()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(prerollSamples: 0);
        buf.Ingest(new float[] { 7 });
        buf.Clear();                          // must not throw or wedge the session
        buf.Ingest(new float[] { 8 });
        buf.StopSession().ShouldBe(new float[] { 7, 8 });
    }

    [Fact]
    public void SessionWasSilent_TrueWhenAllSamplesEssentiallyZero()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[64]); // zero-filled
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeTrue();
    }

    [Fact]
    public void SessionWasSilent_FalseWhenSpeechPresent()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.Ingest(new float[] { 0.3f, -0.3f, 0.3f, -0.3f });
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeFalse();
    }

    [Fact]
    public void SessionWasSilent_FalseWhenNothingCaptured()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);
        buf.StartSession(0);
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeFalse(); // empty != silent capture
    }

    [Fact]
    public void SessionWasSilent_DoesNotLeakFromPriorSessionOnSameBuffer()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 100);

        // Session A: silent capture.
        buf.StartSession(0);
        buf.Ingest(new float[64]); // zero-filled
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeTrue();

        // Session B on the SAME buffer instance: real audio present.
        buf.StartSession(0);
        buf.Ingest(new float[] { 0.2f, -0.2f, 0.2f, -0.2f });
        buf.StopSession();
        buf.SessionWasSilent.ShouldBeFalse();
    }

    [Fact]
    public void StartSession_ReturnsTheSeededPreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 15)); // ring keeps 5..14

        var preroll = buf.StartSession(prerollSamples: 10);

        preroll.ShouldBe(Ramp(5, 10)); // exactly what StopSession will lead with
        buf.StopSession().ShouldBe(Ramp(5, 10));
    }

    [Fact]
    public void StartSession_NoRingHistory_ReturnsEmptyPreroll()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        var preroll = buf.StartSession(prerollSamples: 10);
        preroll.ShouldBeEmpty();
    }

    [Fact]
    public void StartSession_ZeroPrerollRequested_ReturnsEmpty()
    {
        var buf = new WarmCaptureBuffer(ringCapacitySamples: 10);
        buf.Ingest(Ramp(0, 5));
        var preroll = buf.StartSession(prerollSamples: 0);
        preroll.ShouldBeEmpty();
    }
}
