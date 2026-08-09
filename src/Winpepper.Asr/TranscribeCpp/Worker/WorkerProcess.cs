namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>A running worker's stdio + lifecycle, abstracted so supervision
/// logic is testable without child processes (see InProcessWorkerChannel in
/// tests and ExeWorkerProcess for the real thing).</summary>
public interface IWorkerProcess : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    bool HasExited { get; }
    void Kill();
}

public interface IWorkerProcessFactory
{
    IWorkerProcess Start();
}
