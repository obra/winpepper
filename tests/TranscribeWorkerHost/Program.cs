using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;

// The PORTABLE half of `Winpepper.exe --transcribe-worker`
// (src/Winpepper.App/Program.cs:27-51): same loop, same engine factory, same
// stderr log prefix. Deliberately omitted, Windows-only pieces: SetErrorMode
// (WER suppression) and the MTA thread hop (this Main is not [STAThread]).
// Exists so tests can spawn the REAL worker loop as a REAL child process on
// any OS — the loop itself is pure BCL until Load touches native code.
return TranscribeWorkerLoop.Run(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    (runtimeDir, ggufPath) => TranscribeCppEngine.Load(
        runtimeDir, ggufPath, msg => Console.Error.WriteLine($"[transcribe-worker] {msg}")),
    msg => Console.Error.WriteLine($"[transcribe-worker] {msg}"));
