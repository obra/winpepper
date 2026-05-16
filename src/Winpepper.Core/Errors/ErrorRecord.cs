namespace Winpepper.Core.Errors;

/// <summary>One pipeline failure. Created by <see cref="ErrorBus.Report"/>.</summary>
public sealed record ErrorRecord
{
    public required ErrorStage Stage { get; init; }
    public required string Message { get; init; }
    public required string ExceptionType { get; init; }
    public required string StackTrace { get; init; }
    public required DateTime TimestampUtc { get; init; }
    public required Guid SessionId { get; init; }
}
