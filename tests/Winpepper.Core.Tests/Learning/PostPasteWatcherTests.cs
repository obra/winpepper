using Shouldly;
using Winpepper.Corrections;
using Winpepper.Core.Learning;
using Xunit;

namespace Winpepper.Core.Tests.Learning;

public class PostPasteWatcherTests : IDisposable
{
    private readonly string _storePath;
    public PostPasteWatcherTests()
    {
        _storePath = Path.Combine(Path.GetTempPath(), $"corr-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_storePath)) File.Delete(_storePath); }

    private static PostPasteContext Ctx(string injected) => new()
    {
        ElementId = "el-1",
        InjectedText = injected,
        SessionId = Guid.NewGuid(),
        InjectionEndUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Yes_Decision_Writes_Misheard_Replacement()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("Send chat gbt the link"));
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        prompt.Calls.Count.ShouldBe(1);
        prompt.Calls[0].Wrong.ShouldBe("chat gbt");
        prompt.Calls[0].Right.ShouldBe("ChatGPT");

        var data = store.Load();
        data.Replacements.Keys.ShouldContain("chat gbt");
        data.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }

    [Fact]
    public async Task Preferred_Decision_Writes_Preferred_List_Entry()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Preferred);
        using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("Send chat gbt the link"));
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        var data = store.Load();
        data.Preferred.ShouldContain("ChatGPT");
        data.Replacements.Keys.ShouldNotContain("chat gbt");
    }

    [Fact]
    public async Task No_Decision_Suppresses_Same_Pair_For_Session()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.No);
        using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, TimeSpan.FromSeconds(30));

        await ppw.BeginAsync(Ctx("Send chat gbt the link")).ContinueWith(_ => { });
        await watcher.EmitAsync("el-1", "Send ChatGPT the link");

        var done2 = ppw.BeginAsync(Ctx("Send chat gbt please"));
        await watcher.EmitAsync("el-1", "Send ChatGPT please");
        await done2;

        prompt.Calls.Count.ShouldBe(1);
        store.Load().Replacements.Keys.ShouldNotContain("chat gbt");
    }

    [Fact]
    public async Task Watch_Window_Elapses_Without_Change_Cleans_Up()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, TimeSpan.FromMilliseconds(50));

        await ppw.BeginAsync(Ctx("nothing changes"));
        await Task.Delay(150);

        prompt.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Change_That_Fails_Constraints_Does_Not_Prompt()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_storePath);
        var prompt = new FakeToastPrompt(PostPasteDecision.Yes);
        using var ppw = new PostPasteWatcher(watcher, new StoreWriter(store), prompt, TimeSpan.FromSeconds(30));

        var done = ppw.BeginAsync(Ctx("the quick brown fox"));
        await watcher.EmitAsync("el-1", "a slow brown fox");
        await Task.Delay(50);
        await done.ContinueWith(_ => { });

        prompt.Calls.Count.ShouldBe(0);
    }

    private sealed class FakeToastPrompt : IPostPasteToastPrompt
    {
        public List<LearningCandidate> Calls { get; } = new();
        private readonly PostPasteDecision _next;
        public FakeToastPrompt(PostPasteDecision next) { _next = next; }
        public Task<PostPasteDecision> AskAsync(LearningCandidate c, CancellationToken ct)
        {
            Calls.Add(c);
            return Task.FromResult(_next);
        }
    }

    private sealed class StoreWriter : ICorrectionWriter
    {
        private readonly CorrectionStore _s;
        public StoreWriter(CorrectionStore s) { _s = s; }
        public bool AddReplacement(string w, string r) => _s.AddReplacement(w, r);
        public bool AddPreferred(string v) => _s.AddPreferred(v);
    }
}
