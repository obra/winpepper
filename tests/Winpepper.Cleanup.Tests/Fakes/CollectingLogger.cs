using Microsoft.Extensions.Logging;

namespace Winpepper.Cleanup.Tests.Fakes;

/// <summary>Collects warning log lines so tests can assert observability
/// (e.g. window-context truncation) is LOUD, not silent. Also collects
/// Information lines (prewarm start/finish markers).</summary>
internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<string> Warnings { get; } = new();
    public List<string> Infos { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
            Warnings.Add(formatter(state, exception));
        else if (logLevel == LogLevel.Information)
            Infos.Add(formatter(state, exception));
    }
}
