using System.Diagnostics;
using Shouldly;
using Winpepper.Asr.TranscribeCpp;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

/// <summary>Spawns the REAL worker loop as a REAL child process
/// (TranscribeWorkerHost — the portable half of `Winpepper.exe
/// --transcribe-worker`) through the PRODUCTION ExeWorkerProcessFactory +
/// WorkerProcessEngine. No native model needed: TranscribeCppEngine.Load's
/// contract.json existence check runs before any native/Windows-only gate,
/// so a bogus runtime dir yields a real structured Error frame on any OS.</summary>
public sealed class WorkerHostProcessTests
{
    private static ProcessStartInfo HostPsi()
    {
        var dir = AppContext.BaseDirectory;
        var apphost = Path.Combine(dir, OperatingSystem.IsWindows() ? "TranscribeWorkerHost.exe" : "TranscribeWorkerHost");
        if (File.Exists(apphost)) return new ProcessStartInfo(apphost);
        // Fallback: the suite always runs under `dotnet exec` (AGENTS.md),
        // so Environment.ProcessPath is the dotnet muxer on both OSes.
        return new ProcessStartInfo(Environment.ProcessPath!,
            $"exec \"{Path.Combine(dir, "TranscribeWorkerHost.dll")}\"");
    }

    [Fact]
    public void Load_WithBogusRuntimeDir_ReturnsStructuredError_OverARealProcess()
    {
        var factory = new ExeWorkerProcessFactory(HostPsi);
        using var engine = new WorkerProcessEngine(factory,
            "/definitely-missing-runtime", "/missing.gguf", "worker-host-test");

        var ex = Should.Throw<TranscribeCppException>(() => engine.TranscribeBatch(new float[16], null, out _));
        ex.Message.ShouldContain("contract.json not found");
    }

    [Fact]
    public void Kill_TerminatesTheRealWorkerProcess()
    {
        var factory = new ExeWorkerProcessFactory(HostPsi);
        var proc = factory.Start();
        try
        {
            proc.HasExited.ShouldBeFalse();
            proc.Kill();
            SpinWait.SpinUntil(() => proc.HasExited, TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }
        finally { proc.Dispose(); }
    }
}
