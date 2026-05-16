using Winpepper.Core.Sessions;

namespace Winpepper.Core.Crash;

public interface ICrashSink
{
    string? WriteDump(Exception ex, string source);
    void ResetSessionEngine(SessionEngine engine);
}
