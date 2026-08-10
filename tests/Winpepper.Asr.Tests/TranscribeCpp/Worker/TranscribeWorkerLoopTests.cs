using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Winpepper.Asr.Tests.Transcription;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class TranscribeWorkerLoopTests
{
    /// <summary>Runs the loop over in-memory request/response buffers:
    /// requests are pre-written into the input stream, then the loop is run
    /// to completion (EOF or Shutdown), then responses are read back.</summary>
    private static List<(WorkerOp Op, byte[] Payload)> RunScript(
        FakeTranscribeCppEngine engine, params (WorkerOp Op, byte[] Payload)[] requests)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        foreach (var (op, payload) in requests) WorkerWire.WriteFrame(input, op, payload);
        input.Position = 0;
        var exit = TranscribeWorkerLoop.Run(input, output, (_, _) => engine, _ => { });
        exit.ShouldBe(0);
        output.Position = 0;
        var responses = new List<(WorkerOp, byte[])>();
        while (output.Position < output.Length) responses.Add(WorkerWire.ReadFrame(output));
        return responses;
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static byte[] LoadPayload() => Payload(w =>
    {
        WorkerWire.WriteString(w, "/runtime");
        WorkerWire.WriteString(w, "/model.gguf");
    });

    [Fact]
    public void Load_RespondsWithModelName()
    {
        var engine = new FakeTranscribeCppEngine();
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()));
        rs.Count.ShouldBe(1);
        rs[0].Op.ShouldBe(WorkerOp.LoadOk);
        using var r = new BinaryReader(new MemoryStream(rs[0].Payload));
        WorkerWire.ReadString(r).ShouldBe(engine.ModelName);
    }

    [Fact]
    public void BeginStream_Feed_Finalize_Dispose_FullSessionRoundTrip()
    {
        var engine = new FakeTranscribeCppEngine { FinalText = "hello from worker", GateWaitMsToReport = 7 };
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, "en-US"); });
        var feed = Payload(w => WorkerWire.WriteFloats(w, new float[2560], 2560));
        var rs = RunScript(engine,
            (WorkerOp.Load, LoadPayload()),
            (WorkerOp.BeginStream, begin),
            (WorkerOp.Feed, feed),
            (WorkerOp.FinalizeStream, Array.Empty<byte>()),
            (WorkerOp.DisposeStream, Array.Empty<byte>()));

        rs.Select(x => x.Op).ShouldBe(new[]
            { WorkerOp.LoadOk, WorkerOp.BeginStreamOk, WorkerOp.FeedOk, WorkerOp.FinalizeOk, WorkerOp.Ok });

        using var beginR = new BinaryReader(new MemoryStream(rs[1].Payload));
        beginR.ReadInt32().ShouldBe(7); // gateWaitMs surfaced per call

        using var finR = new BinaryReader(new MemoryStream(rs[3].Payload));
        WorkerWire.ReadString(finR).ShouldBe("hello from worker");
        finR.ReadBoolean().ShouldBeFalse();

        engine.BeginStreamLanguages.ShouldBe(new[] { "en-US" });
        engine.LastStream!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void TranscribeBatch_WhileStreamOpen_DisposesTheStreamFirst()
    {
        // The compute-gate trap: a batch on the same engine while a stream is
        // open would deadlock on the engine-wide gate. The worker's contract
        // is to dispose the open stream (releasing the gate) before batch.
        var engine = new FakeTranscribeCppEngine();
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        var batch = Payload(w => { WorkerWire.WriteString(w, null); WorkerWire.WriteFloats(w, new float[16], 16); });
        var rs = RunScript(engine,
            (WorkerOp.Load, LoadPayload()),
            (WorkerOp.BeginStream, begin),
            (WorkerOp.TranscribeBatch, batch));

        rs[2].Op.ShouldBe(WorkerOp.BatchOk);
        engine.LastStream!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void EngineThrow_MapsToErrorFrame_PreservingTranscribeCppExceptionType()
    {
        var engine = new FakeTranscribeCppEngine { ThrowOnBeginStream = true };
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.BeginStream, begin));

        rs[1].Op.ShouldBe(WorkerOp.Error);
        using var r = new BinaryReader(new MemoryStream(rs[1].Payload));
        r.ReadInt32(); // gateWaitMs
        WorkerWire.ReadString(r).ShouldBe(nameof(TranscribeCppException));
    }

    [Fact]
    public void RequestBeforeLoad_ReturnsError_NotCrash()
    {
        var engine = new FakeTranscribeCppEngine();
        var batch = Payload(w => { WorkerWire.WriteString(w, null); WorkerWire.WriteFloats(w, new float[4], 4); });
        var rs = RunScript(engine, (WorkerOp.TranscribeBatch, batch));
        rs[0].Op.ShouldBe(WorkerOp.Error);
    }

    [Fact]
    public void Shutdown_RespondsOk_DisposesEngine_AndExitsZero()
    {
        var engine = new FakeTranscribeCppEngine();
        var rs = RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.Shutdown, Array.Empty<byte>()));
        rs[1].Op.ShouldBe(WorkerOp.Ok);
        engine.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void CleanEof_DisposesEngineAndStream_AndExitsZero()
    {
        var engine = new FakeTranscribeCppEngine();
        var begin = Payload(w => { w.Write(13); WorkerWire.WriteString(w, null); });
        RunScript(engine, (WorkerOp.Load, LoadPayload()), (WorkerOp.BeginStream, begin));
        engine.LastStream!.Disposed.ShouldBeTrue(); // crashed/vanished client frees the gate
        engine.Disposed.ShouldBeTrue();
    }
}
