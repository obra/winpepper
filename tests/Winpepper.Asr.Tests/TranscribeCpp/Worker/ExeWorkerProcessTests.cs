using System.Diagnostics;
using Shouldly;
using Winpepper.Asr.TranscribeCpp.Worker;
using Xunit;

namespace Winpepper.Asr.Tests.TranscribeCpp.Worker;

public sealed class ExeWorkerProcessTests
{
    /// <summary>A child that lives ~60 s unless killed, with stdio redirected.
    /// Windows: `cmd /c ping` (n pings ≈ n-1 seconds); Linux: `sleep`.</summary>
    private static ProcessStartInfo Sleeper() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1 > NUL")
        : new ProcessStartInfo("/bin/sleep", "60");

    [Fact]
    public void Start_SpawnsALiveProcess_WithUsableStdio()
    {
        using var p = ExeWorkerProcess.Start(Sleeper());
        p.HasExited.ShouldBeFalse();
        p.Input.CanWrite.ShouldBeTrue();
        p.Output.CanRead.ShouldBeTrue();
    }

    [Fact]
    public void Kill_TerminatesTheProcess_AndPendingReadsComplete()
    {
        using var p = ExeWorkerProcess.Start(Sleeper());
        var pending = Task.Run(() => p.Output.Read(new byte[16], 0, 16));
        p.Kill();
        // Exit is observable...
        SpinWait.SpinUntil(() => p.HasExited, TimeSpan.FromSeconds(5)).ShouldBeTrue();
        // ...and the blocked stdout read unblocks (EOF => 0, or an IO fault) —
        // this is what lets WorkerProcessEngine's deadline'd read complete.
        var completed = pending.Wait(TimeSpan.FromSeconds(5));
        completed.ShouldBeTrue();
    }

    [Fact]
    public void HasExited_TracksNaturalExit()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("/bin/sh", "-c \"exit 0\"");
        using var p = ExeWorkerProcess.Start(psi);
        SpinWait.SpinUntil(() => p.HasExited, TimeSpan.FromSeconds(10)).ShouldBeTrue();
    }
}
