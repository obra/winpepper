namespace Winpepper.History.Lab;

public sealed record TranscriptionRerunResult
{
    public required string ModelName { get; init; }
    public required string Text { get; init; }
    public required TimeSpan Elapsed { get; init; }
}
