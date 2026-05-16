using System.Globalization;
using System.IO;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Winpepper.Core.Logging;

/// <summary>Serilog sink that forwards rendered events into a <see cref="LogRingBuffer"/>.</summary>
internal sealed class RingBufferSink : ILogEventSink
{
    // {Message:lj} renders strings as literals (no surrounding quotes), matching the file sink.
    private static readonly MessageTemplateTextFormatter Formatter =
        new("{Message:lj}", CultureInfo.InvariantCulture);

    private readonly LogRingBuffer _buffer;
    public RingBufferSink(LogRingBuffer buffer) { _buffer = buffer; }

    public void Emit(LogEvent logEvent)
    {
        var levelTag = logEvent.Level switch
        {
            LogEventLevel.Verbose => "VRB",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Information => "INF",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Fatal => "FTL",
            _ => "INF",
        };
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        Formatter.Format(logEvent, sw);
        var message = sw.ToString();
        if (logEvent.Exception is not null)
            message = $"{message} | {logEvent.Exception.GetType().Name}: {logEvent.Exception.Message}";
        _buffer.Append(new LogTailEntry(logEvent.Timestamp.UtcDateTime, levelTag, message));
    }
}
