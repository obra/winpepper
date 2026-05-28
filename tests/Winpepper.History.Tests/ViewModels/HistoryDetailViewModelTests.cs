using Shouldly;
using Winpepper.History.Lab;
using Winpepper.History.ViewModels;
using Xunit;

namespace Winpepper.History.Tests.ViewModels;

public class HistoryDetailViewModelTests : IDisposable
{
    private readonly string _root;
    public HistoryDetailViewModelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vmdetail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private HistoryEntry NewEntry()
    {
        var wav = "2026-05-15/x.wav";
        var abs = Path.Combine(_root, wav);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        WavWriter.WriteMono16kInt16(abs, new float[16]);
        return new HistoryEntry
        {
            Id = "x",
            RawTranscript = "hello world",
            CleanedText = "Hello, world.",
            WavRelativePath = wav,
        };
    }

    [Fact]
    public async Task RunTranscriptionRerun_PopulatesResult_AndDiff()
    {
        var entry = NewEntry();
        var fakeAsr = new FakeTranscriptionRerunService((_, m) => "hello earth");
        var fakeCleanup = new FakeCleanupRerunService();
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.TranscriptionPanel.SelectedModelName = "parakeet-alt";
        vm.TranscriptionPanel.SelectedModelDirectory = _root;
        await vm.TranscriptionPanel.RunAsync(CancellationToken.None);

        vm.TranscriptionPanel.RerunText.ShouldBe("hello earth");
        vm.TranscriptionPanel.Diff.Count.ShouldBeGreaterThan(0);
        vm.TranscriptionPanel.Diff.Any(s => s.Kind == Winpepper.History.Diff.WordDiffKind.Insert
                                          && s.Text.Contains("earth")).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCleanupRerun_PopulatesCleanedText_PromptAndRawOutput()
    {
        var entry = NewEntry();
        var fakeAsr = new FakeTranscriptionRerunService();
        var fakeCleanup = new FakeCleanupRerunService(i =>
            ("PROMPT-BODY", "<think>x</think>raw", $"cleaned: {i.RawTranscript}"));
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.CleanupPanel.SelectedModelPath = Path.Combine(_root, "qwen-alt.gguf");
        await vm.CleanupPanel.RunAsync(CancellationToken.None);

        vm.CleanupPanel.RerunText.ShouldBe("cleaned: hello world");
        vm.CleanupAssembledPrompt.ShouldBe("PROMPT-BODY");
        vm.CleanupRawOutput.ShouldContain("raw");
    }

    [Fact]
    public async Task RunCleanupRerun_DoesNotMutatePersistedEntry()
    {
        var entry = NewEntry();
        var entryBeforeRaw = entry.RawTranscript;
        var entryBeforeClean = entry.CleanedText;
        var entryBeforeWav = entry.WavRelativePath;
        var entryBeforeId = entry.Id;

        var fakeAsr = new FakeTranscriptionRerunService();
        var fakeCleanup = new FakeCleanupRerunService(i =>
            ("MUTATED-PROMPT", "MUTATED-RAW", "MUTATED-CLEAN"));
        var vm = new HistoryDetailViewModel(entry, _root, fakeAsr, fakeCleanup,
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.CleanupPanel.SelectedModelPath = Path.Combine(_root, "qwen-alt.gguf");
        vm.CleanupCustomPrompt = "completely different prompt";
        await vm.CleanupPanel.RunAsync(CancellationToken.None);

        vm.CleanupPanel.RerunText.ShouldBe("MUTATED-CLEAN");
        vm.Entry.RawTranscript.ShouldBe(entryBeforeRaw);
        vm.Entry.CleanedText.ShouldBe(entryBeforeClean);
        vm.Entry.WavRelativePath.ShouldBe(entryBeforeWav);
        vm.Entry.Id.ShouldBe(entryBeforeId);
        vm.OriginalTranscript.ShouldBe(entryBeforeRaw);
        vm.OriginalCleanedText.ShouldBe(entryBeforeClean);
    }

    [Fact]
    public void PromoteAsrDefault_InvokesCallback_WithSelectedModel()
    {
        var entry = NewEntry();
        string? promoted = null;
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: n => promoted = n,
            promoteCleanupDefault: _ => { });

        vm.TranscriptionPanel.SelectedModelName = "parakeet-alt";
        vm.PromoteTranscriptionRerunAsDefault();
        promoted.ShouldBe("parakeet-alt");
    }

    [Fact]
    public void PromoteCleanupDefault_InvokesCallback_WithSelectedModel()
    {
        var entry = NewEntry();
        string? promoted = null;
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: _ => { },
            promoteCleanupDefault: n => promoted = n);

        vm.CleanupPanel.SelectedModelName = "qwen-alt";
        vm.PromoteCleanupRerunAsDefault();
        promoted.ShouldBe("qwen-alt");
    }

    [Fact]
    public void OriginalProperties_ExposeEntryValues()
    {
        var entry = NewEntry();
        var vm = new HistoryDetailViewModel(entry, _root,
            new FakeTranscriptionRerunService(), new FakeCleanupRerunService(),
            promoteAsrDefault: _ => { }, promoteCleanupDefault: _ => { });
        vm.OriginalTranscript.ShouldBe("hello world");
        vm.OriginalCleanedText.ShouldBe("Hello, world.");
        // WavRelativePath is stored with '/' for cross-platform persistence;
        // Path.Combine on Windows produces a mixed-separator string. Compare
        // resolved paths via FileInfo.FullName so the assertion doesn't care
        // about the in-string separator — only about the file identity.
        new FileInfo(vm.WavAbsolutePath).FullName
            .ShouldBe(new FileInfo(Path.Combine(_root, "2026-05-15", "x.wav")).FullName);
    }
}
