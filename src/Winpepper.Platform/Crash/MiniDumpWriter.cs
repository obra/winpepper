#if WINDOWS
using Microsoft.Extensions.Logging;
using Winpepper.Core.Crash;
using Winpepper.Core.Sessions;

namespace Winpepper.Platform.Crash;

public sealed class MiniDumpWriter : ICrashSink
{
    private readonly string _crashDir;
    private readonly ILogger<MiniDumpWriter> _log;

    public MiniDumpWriter(string crashDir, ILogger<MiniDumpWriter> log)
    {
        _crashDir = crashDir;
        _log = log;
        Directory.CreateDirectory(crashDir);
    }

    public string? WriteDump(Exception ex, string source)
    {
        var fileName = $"winpepper-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.dmp";
        var fullPath = Path.Combine(_crashDir, fileName);

        try
        {
            using var fs = File.Create(fullPath);
            var ok = DbgHelpNative.MiniDumpWriteDump(
                DbgHelpNative.GetCurrentProcess(),
                DbgHelpNative.GetCurrentProcessId(),
                fs.SafeFileHandle.DangerousGetHandle(),
                DbgHelpNative.MINIDUMP_TYPE.MiniDumpWithThreadInfo
                    | DbgHelpNative.MINIDUMP_TYPE.MiniDumpWithProcessThreadData,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (!ok)
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _log.LogError("MiniDumpWriteDump failed: 0x{Err:X}", err);
                try { File.Delete(fullPath); } catch { }
                return null;
            }
        }
        catch (Exception innerEx)
        {
            _log.LogError(innerEx, "minidump file IO failed");
            try { if (File.Exists(fullPath)) File.Delete(fullPath); } catch { }
            return null;
        }

        try
        {
            var sidecar = Path.ChangeExtension(fullPath, ".txt");
            File.WriteAllText(sidecar,
                $"source: {source}{Environment.NewLine}" +
                $"type:   {ex.GetType().FullName}{Environment.NewLine}" +
                $"msg:    {ex.Message}{Environment.NewLine}{Environment.NewLine}" +
                ex.ToString());
        }
        catch { }

        return fullPath;
    }

    public void ResetSessionEngine(SessionEngine engine)
    {
        engine.Apply(SessionEvent.CancelRequested);
        if (engine.State != SessionState.Idle)
            engine.Apply(SessionEvent.Failed);
        if (engine.State != SessionState.Idle)
            throw new InvalidOperationException("SessionEngine refused to reset to Idle");
    }
}
#endif
