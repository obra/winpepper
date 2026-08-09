namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>
/// Worker-side request loop. Hosts ONE engine and at most ONE stream,
/// single-threaded (transcribe.cpp allows one compute in flight per model, so
/// serialization is free correctness). Every request produces exactly one
/// response frame. TranscribeBatch auto-disposes an open stream first: the
/// engine-wide compute gate is held for a stream's lifetime, so a same-engine
/// batch while a stream is open would stall 5 s and throw (the bench's
/// documented trap, scripts/asr-latency-bench/Program.cs:769-776) — worker
/// restart/dispose is the subprocess replacement for the bench's second
/// engine. EOF (client died) and Shutdown both dispose stream + engine so a
/// vanished client can never leave the gate held.
/// </summary>
public static class TranscribeWorkerLoop
{
    public static int Run(Stream input, Stream output,
        Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log)
    {
        ITranscribeCppEngine? engine = null;
        ITranscribeCppStream? stream = null;
        try
        {
            while (true)
            {
                WorkerOp op;
                byte[] payload;
                try { (op, payload) = WorkerWire.ReadFrame(input); }
                catch (Exception e) when (e is EndOfStreamException or IOException or ObjectDisposedException)
                {
                    log("worker input closed; exiting");
                    return 0;
                }

                if (op == WorkerOp.Shutdown)
                {
                    WorkerWire.WriteFrame(output, WorkerOp.Ok, Array.Empty<byte>());
                    return 0;
                }

                byte[] response;
                WorkerOp responseOp;
                try
                {
                    (responseOp, response) = Handle(op, payload, engineFactory, log, ref engine, ref stream);
                }
                catch (Exception ex)
                {
                    (responseOp, response) = (WorkerOp.Error, ErrorPayload(0, ex));
                }
                try { WorkerWire.WriteFrame(output, responseOp, response); }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    log("worker output closed; exiting");
                    return 0;
                }
            }
        }
        finally
        {
            try { stream?.Dispose(); } catch { /* releasing on the way out */ }
            try { engine?.Dispose(); } catch { /* releasing on the way out */ }
        }
    }

    private static (WorkerOp, byte[]) Handle(WorkerOp op, byte[] payload,
        Func<string, string, ITranscribeCppEngine> engineFactory, Action<string> log,
        ref ITranscribeCppEngine? engine, ref ITranscribeCppStream? stream)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms, System.Text.Encoding.UTF8);

        switch (op)
        {
            case WorkerOp.Load:
            {
                var runtimeDir = WorkerWire.ReadString(r)!;
                var ggufPath = WorkerWire.ReadString(r)!;
                engine?.Dispose();
                engine = engineFactory(runtimeDir, ggufPath);
                var modelName = engine.ModelName; // copy to a local: `engine` is a ref parameter and cannot be captured in the lambda (CS1628)
                return (WorkerOp.LoadOk, Build(w => WorkerWire.WriteString(w, modelName)));
            }
            case WorkerOp.BeginStream:
            {
                RequireEngine(engine);
                var attContextRight = r.ReadInt32();
                var language = WorkerWire.ReadString(r);
                stream?.Dispose();
                var gateWaitMs = 0;
                try { stream = engine!.BeginStream(attContextRight, language, out gateWaitMs); }
                catch (Exception ex) { return (WorkerOp.Error, ErrorPayload(gateWaitMs, ex)); }
                var wait = gateWaitMs;
                return (WorkerOp.BeginStreamOk, Build(w => w.Write(wait)));
            }
            case WorkerOp.Feed:
            {
                RequireStream(stream);
                var samples = WorkerWire.ReadFloats(r);
                var committed = stream!.Feed(samples, samples.Length);
                return (WorkerOp.FeedOk, Build(w => WorkerWire.WriteString(w, committed)));
            }
            case WorkerOp.FinalizeStream:
            {
                RequireStream(stream);
                var (text, wasTruncated) = stream!.Finalize();
                return (WorkerOp.FinalizeOk, Build(w => { WorkerWire.WriteString(w, text); w.Write(wasTruncated); }));
            }
            case WorkerOp.DisposeStream:
            {
                stream?.Dispose();
                stream = null;
                return (WorkerOp.Ok, Array.Empty<byte>());
            }
            case WorkerOp.TranscribeBatch:
            {
                RequireEngine(engine);
                var language = WorkerWire.ReadString(r);
                var samples = WorkerWire.ReadFloats(r);
                if (stream is not null)
                {
                    log("batch requested while a stream is open; disposing the stream to release the compute gate");
                    stream.Dispose();
                    stream = null;
                }
                var gateWaitMs = 0;
                string text;
                try { text = engine!.TranscribeBatch(samples, language, out gateWaitMs); }
                catch (Exception ex) { return (WorkerOp.Error, ErrorPayload(gateWaitMs, ex)); }
                var wait = gateWaitMs;
                return (WorkerOp.BatchOk, Build(w => { w.Write(wait); WorkerWire.WriteString(w, text); }));
            }
            default:
                throw new InvalidOperationException($"unknown worker request op {op}");
        }
    }

    private static void RequireEngine(ITranscribeCppEngine? engine)
    {
        if (engine is null) throw new InvalidOperationException("worker engine not loaded (send Load first)");
    }

    private static void RequireStream(ITranscribeCppStream? stream)
    {
        if (stream is null) throw new InvalidOperationException("no open stream (send BeginStream first)");
    }

    private static byte[] Build(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return ms.ToArray();
    }

    private static byte[] ErrorPayload(int gateWaitMs, Exception ex) => Build(w =>
    {
        w.Write(gateWaitMs);
        WorkerWire.WriteString(w, ex.GetType().Name);
        WorkerWire.WriteString(w, ex.Message);
    });
}
