using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Winpepper.Asr.TranscribeCpp.Worker;

/// <summary>Real child-process IWorkerProcess: redirected stdio, stderr lines
/// forwarded to a log callback, kill = whole process tree (ggml may spawn
/// nothing today, but the tree kill is free insurance). On Windows the child
/// is additionally bound to a Job Object with KILL_ON_JOB_CLOSE (a failed
/// bind is logged — see WindowsJob.BindKillOnClose — and forfeits the job
/// guarantees below). What the job actually guarantees: if the PARENT
/// CRASHES, the kernel closes the orphaned
/// job handle and the worker — even one wedged in native code that will never
/// see stdin EOF — is killed. In the supervised path the handle is closed at
/// kill time (KillLocked -> Dispose below), which kills any survivor THEN —
/// there is no job handle left at app exit, so a killed worker that is wedged
/// in a KERNEL-mode call and survives both the kill and the job-close is NOT
/// reaped at app exit; it can linger until the kernel operation completes or
/// the OS cleans it up. That leak is the accepted residual (see the plan's A1
/// residual note). No-op on Linux, so the Linux tests are unaffected.</summary>
public sealed class ExeWorkerProcess : IWorkerProcess
{
    private readonly Process _process;
    private readonly nint _jobHandle; // Windows Job Object; 0 elsewhere. Held for the worker's lifetime.

    private ExeWorkerProcess(Process process, nint jobHandle)
    {
        _process = process;
        _jobHandle = jobHandle;
    }

    public static ExeWorkerProcess Start(ProcessStartInfo psi, Action<string>? onStderrLine = null)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start worker process '{psi.FileName}'");
        if (onStderrLine is not null)
        {
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onStderrLine(e.Data); };
            process.BeginErrorReadLine();
        }
        else
        {
            // Drain stderr so the child can never block on a full pipe.
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
        }
        var jobHandle = OperatingSystem.IsWindows() ? WindowsJob.BindKillOnClose(process, onStderrLine) : 0;
        return new ExeWorkerProcess(process, jobHandle);
    }

    public Stream Input => _process.StandardInput.BaseStream;
    public Stream Output => _process.StandardOutput.BaseStream;
    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
        if (_jobHandle != 0) WindowsJob.Close(_jobHandle); // closing the job kills any survivor
    }
}

/// <summary>Minimal Job Object P/Invoke: CreateJobObject + SetInformationJobObject
/// (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE) + AssignProcessToJobObject. Failures are
/// tolerated (returns 0): the EOF/kill paths still supervise the worker; the job
/// is the belt-and-braces guarantee for parent CRASH and kernel-wedged workers.</summary>
internal static class WindowsJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    internal static nint BindKillOnClose(Process process, Action<string>? log = null)
    {
        var job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            log?.Invoke("worker job object create failed: " +
                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message} — " +
                "workers will not be reaped if this process crashes");
            return 0;
        }
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>())
            || !AssignProcessToJobObject(job, process.Handle))
        {
            log?.Invoke("worker job object bind failed: " +
                $"{new Win32Exception(Marshal.GetLastWin32Error()).Message} — " +
                "workers will not be reaped if this process crashes");
            CloseHandle(job);
            return 0;
        }
        return job;
    }

    internal static void Close(nint handle) => CloseHandle(handle);
}

public sealed class ExeWorkerProcessFactory : IWorkerProcessFactory
{
    private readonly Func<ProcessStartInfo> _psi;
    private readonly Action<string>? _onStderrLine;

    public ExeWorkerProcessFactory(Func<ProcessStartInfo> psi, Action<string>? onStderrLine = null)
    {
        _psi = psi;
        _onStderrLine = onStderrLine;
    }

    public IWorkerProcess Start() => ExeWorkerProcess.Start(_psi(), _onStderrLine);
}
