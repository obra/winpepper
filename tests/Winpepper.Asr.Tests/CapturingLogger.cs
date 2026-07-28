using Microsoft.Extensions.Logging;

namespace Winpepper.Asr.Tests;

/// <summary>Records formatted Warning+ messages so tests can assert on log
/// noise (e.g. that the pump does NOT warn on an ordinary abandon race).</summary>
public sealed class CapturingLogger : ILogger
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings
    {
        get { lock (_warnings) return _warnings.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Warning) return;
        lock (_warnings) _warnings.Add(formatter(state, exception));
    }
}
