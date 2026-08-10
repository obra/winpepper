using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class WorkerProtocolTests
{
    [Fact]
    public void Frame_RoundTrips_OpAndPayload()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.Feed, new byte[] { 1, 2, 3 });
        ms.Position = 0;
        var (op, payload) = WorkerWire.ReadFrame(ms);
        op.ShouldBe(WorkerOp.Feed);
        payload.ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public void Frame_EmptyPayload_RoundTrips()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.FinalizeStream, Array.Empty<byte>());
        ms.Position = 0;
        var (op, payload) = WorkerWire.ReadFrame(ms);
        op.ShouldBe(WorkerOp.FinalizeStream);
        payload.ShouldBeEmpty();
    }

    [Fact]
    public void ReadFrame_OnEof_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        Should.Throw<EndOfStreamException>(() => WorkerWire.ReadFrame(ms));
    }

    [Fact]
    public void ReadFrame_OnTruncatedPayload_ThrowsEndOfStream()
    {
        using var ms = new MemoryStream();
        WorkerWire.WriteFrame(ms, WorkerOp.Feed, new byte[] { 1, 2, 3, 4 });
        var truncated = new MemoryStream(ms.ToArray(), 0, (int)ms.Length - 2);
        Should.Throw<EndOfStreamException>(() => WorkerWire.ReadFrame(truncated));
    }

    [Fact]
    public void ReadFrame_OnInsaneLength_ThrowsInvalidData()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)WorkerOp.Feed);
        ms.Write(BitConverter.GetBytes(int.MaxValue));
        ms.Position = 0;
        Should.Throw<InvalidDataException>(() => WorkerWire.ReadFrame(ms));
    }

    [Fact]
    public void WriteFrame_OversizePayload_Throws_WithoutWriting()
    {
        // A frame the peer would fatally reject must never leave the writer.
        // Allocating MaxPayloadBytes+1 (~65 MiB) once in a unit test is
        // wasteful but acceptable.
        using var ms = new MemoryStream();
        var oversize = new byte[WorkerWire.MaxPayloadBytes + 1];
        Should.Throw<InvalidDataException>(() => WorkerWire.WriteFrame(ms, WorkerOp.TranscribeBatch, oversize));
        ms.Length.ShouldBe(0); // nothing written — the connection stays usable
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello — em dash and üñïcode")]
    public void String_RoundTrips_IncludingNull(string? value)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            WorkerWire.WriteString(w, value);
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        WorkerWire.ReadString(r).ShouldBe(value);
    }

    [Fact]
    public void Floats_RoundTrip_RespectingCount()
    {
        var samples = new float[] { 0.5f, -1f, 0.25f, 99f };
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
            WorkerWire.WriteFloats(w, samples, count: 3); // only the first 3
        ms.Position = 0;
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        WorkerWire.ReadFloats(r).ShouldBe(new float[] { 0.5f, -1f, 0.25f });
    }
}
