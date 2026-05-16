namespace Winpepper.History.Lab;

public sealed class FakeCleanupRerunService : ICleanupRerunService
{
    private readonly Func<CleanupRerunInput, (string Prompt, string Raw, string Clean)> _produce;
    public FakeCleanupRerunService(Func<CleanupRerunInput, (string, string, string)>? produce = null)
        => _produce = produce ?? (i => ($"PROMPT[{i.ModelName}]:{i.RawTranscript}", $"RAW[{i.ModelName}]", $"CLEAN[{i.ModelName}] {i.RawTranscript}"));

    public Task<CleanupRerunResult> RerunAsync(CleanupRerunInput input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (p, r, c) = _produce(input);
        return Task.FromResult(new CleanupRerunResult
        {
            ModelName = input.ModelName,
            AssembledPrompt = p,
            RawOutput = r,
            CleanedText = c,
            Elapsed = TimeSpan.Zero,
        });
    }
}
