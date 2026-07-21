using System.Text;
using Winpepper.Asr.Transcription;

namespace Winpepper.Asr.Tests;

/// <summary>Scripts upload/create/poll behavior and records the uploaded body.</summary>
public sealed class FakeAssemblyAiClient : IAssemblyAiClient
{
    private readonly Queue<AssemblyAiTranscript> _pollResults = new();
    public byte[]? UploadedBytes { get; private set; }
    public int PollCalls { get; private set; }
    public AssemblyAiRequestExtras? LastExtras { get; private set; }

    public FakeAssemblyAiClient EnqueuePoll(AssemblyAiTranscript t) { _pollResults.Enqueue(t); return this; }

    public Task<string> UploadAsync(byte[] audio, CancellationToken ct)
    {
        UploadedBytes = audio;
        return Task.FromResult("https://cdn/aai/fake");
    }

    public Task<string> CreateTranscriptAsync(string audioUrl, string model, AssemblyAiRequestExtras extras, CancellationToken ct)
    {
        LastExtras = extras;
        return Task.FromResult("t-fake");
    }

    public Task<AssemblyAiTranscript> GetTranscriptAsync(string id, CancellationToken ct)
    {
        PollCalls++;
        // If a specific result is queued use it; otherwise keep returning "processing".
        var next = _pollResults.Count > 0 ? _pollResults.Dequeue()
            : new AssemblyAiTranscript("processing", null, null, null, null);
        return Task.FromResult(next);
    }

    public Task<bool> ValidateKeyAsync(CancellationToken ct) => Task.FromResult(true);

    public string RiffMagic() => UploadedBytes is null ? "" : Encoding.ASCII.GetString(UploadedBytes, 0, 4);
}
