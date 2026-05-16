using Shouldly;
using Winpepper.Corrections;
using Winpepper.Core.Learning;
using Winpepper.Core.Notifications;
using Xunit;

namespace Winpepper.IntegrationTests;

public class PostPasteLearningEndToEndTests : IDisposable
{
    private readonly string _correctionsPath;
    public PostPasteLearningEndToEndTests()
    {
        _correctionsPath = Path.Combine(Path.GetTempPath(), $"corr-it-{Guid.NewGuid():N}.json");
    }
    public void Dispose() { if (File.Exists(_correctionsPath)) File.Delete(_correctionsPath); }

    [Fact]
    public async Task Full_Flow_Persists_Replacement_To_Disk()
    {
        var watcher = new FakeFocusedElementTextWatcher();
        var store = new CorrectionStore(_correctionsPath);
        var writer = new CorrectionStoreWriter(store);
        var toasts = new FakeToastService();
        toasts.AutoSelect("yes");
        var prompt = new ToastPostPasteToastPrompt(toasts);
        using var ppw = new PostPasteWatcher(watcher, writer, prompt, TimeSpan.FromSeconds(5));

        var ctx = new PostPasteContext
        {
            ElementId = "el-1",
            InjectedText = "Send chat gbt the link",
            SessionId = Guid.NewGuid(),
            InjectionEndUtc = DateTime.UtcNow,
        };
        var done = ppw.BeginAsync(ctx);

        await watcher.EmitAsync("el-1", "Send ChatGPT the link");
        await done;

        var disk = new CorrectionStore(_correctionsPath).Load();
        disk.Replacements.Keys.ShouldContain("chat gbt");
        disk.Replacements["chat gbt"].ShouldBe("ChatGPT");
    }
}
