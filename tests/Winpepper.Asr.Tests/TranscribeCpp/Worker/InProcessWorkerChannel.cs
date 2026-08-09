using System.IO.Pipes;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

/// <summary>
/// Runs the REAL TranscribeWorkerLoop on a background thread over anonymous
/// pipes — full client↔worker integration without a child process. Kill()
/// tears the pipes down WRITE-ENDS-FIRST. NOTE this is NOT identical to real
/// process death: killing a real child closes the PEER's (child's) ends, and
/// only peer-WRITE-end closure unblocks a blocked read. On Unix, disposing a
/// read end with an in-flight blocked read blocks the DISPOSER until the peer
/// write end closes (socket-wrapped pipe fds) — hence the strict order below
/// (V7: 300/300 clean vs a deterministic deadlock with read-ends-first).
/// The factory counts started channels so tests can assert respawns.
/// </summary>
public sealed class InProcessWorkerChannel : IWorkerProcess
{
    private readonly AnonymousPipeServerStream _toWorker;
    private readonly AnonymousPipeClientStream _workerIn;
    private readonly AnonymousPipeServerStream _fromWorker;
    private readonly AnonymousPipeClientStream _workerOut;
    private readonly Thread _thread;
    private volatile bool _exited;

    public InProcessWorkerChannel(Func<ITranscribeCppEngine> engineFactory)
    {
        _toWorker = new AnonymousPipeServerStream(PipeDirection.Out);
        _workerIn = new AnonymousPipeClientStream(PipeDirection.In, _toWorker.ClientSafePipeHandle);
        _fromWorker = new AnonymousPipeServerStream(PipeDirection.In);
        _workerOut = new AnonymousPipeClientStream(PipeDirection.Out, _fromWorker.ClientSafePipeHandle);
        _thread = new Thread(() =>
        {
            try { TranscribeWorkerLoop.Run(_workerIn, _workerOut, (_, _) => engineFactory(), _ => { }); }
            catch { /* pipe torn down by Kill */ }
            finally { _exited = true; }
        }) { IsBackground = true };
        _thread.Start();
    }

    public Stream Input => _toWorker;
    public Stream Output => _fromWorker;
    public bool HasExited => _exited;

    public int KillCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    /// <summary>Simulates the worker process dying on its own (natural exit):
    /// HasExited flips true but nothing is torn down — like a real child that
    /// exited while the parent still holds the Process object, pipes, and
    /// (on Windows) the job handle.</summary>
    public void SimulateNaturalExit() => _exited = true;

    public void Kill()
    {
        KillCalls++;
        _exited = true;
        // WRITE ends FIRST — each unblocks the opposite side's blocked read
        // (EOF / IO fault). Disposing a read end while a read is in flight
        // would block THIS thread until its peer write end closes (V7).
        _toWorker.Dispose();   // client->worker write end: the worker's ReadFrame EOFs
        _workerOut.Dispose();  // worker->client write end: the client's deadline'd ReadFrame unblocks
        _fromWorker.Dispose();
        _workerIn.Dispose();
    }

    public void Dispose()
    {
        DisposeCalls++;
        Kill();
    }
}

public sealed class InProcessWorkerChannelFactory : IWorkerProcessFactory
{
    private readonly Func<ITranscribeCppEngine> _engineFactory;
    public InProcessWorkerChannelFactory(Func<ITranscribeCppEngine> engineFactory) => _engineFactory = engineFactory;
    public int Started { get; private set; }
    public InProcessWorkerChannel? Last { get; private set; }
    public List<InProcessWorkerChannel> All { get; } = new();
    public IWorkerProcess Start()
    {
        Started++;
        Last = new InProcessWorkerChannel(_engineFactory);
        All.Add(Last);
        return Last;
    }
}
